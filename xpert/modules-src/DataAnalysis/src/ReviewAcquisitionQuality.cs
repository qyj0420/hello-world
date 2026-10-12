using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    // Acquisition validity is independent of candidate score and of choosing Save.
    // Only bounded metadata is carried here; never follow raw paths from a header.
    internal sealed class ReviewAcquisitionQuality
    {
        internal static readonly ReviewAcquisitionQuality Unrecorded=new ReviewAcquisitionQuality();
        internal bool Recorded,ManualOverRangeSaved,OverRange,OverRangeAccepted;
        internal string PeakValidity="",LocationValidity="",RawSha256="";
        internal bool ReferenceOnly {get{return ManualOverRangeSaved||OverRange||OverRangeAccepted||
            PeakValidity.Length>0&&PeakValidity!="valid"||LocationValidity.Length>0&&LocationValidity!="valid";}}
        static bool Flag(Dictionary<string,object> c,string key)
        {object v;if(!c.TryGetValue(key,out v))return false;if(!(v is bool))throw new InvalidDataException("Invalid acquisition quality flag: "+key);return (bool)v;}
        static string Text(Dictionary<string,object> c,string key,int limit)
        {object v;if(!c.TryGetValue(key,out v)||v==null)return "";var s=v as string;if(s==null||s.Length>limit||s.Any(Char.IsControl))throw new InvalidDataException("Invalid acquisition quality text: "+key);return s;}
        internal static ReviewAcquisitionQuality FromContext(Dictionary<string,object> c)
        {
            if(c==null)throw new InvalidDataException("Acquisition context must be an object.");
            var q=new ReviewAcquisitionQuality {Recorded=true,ManualOverRangeSaved=Flag(c,"manualOverRangeSaved"),
                OverRange=Flag(c,"overRange"),OverRangeAccepted=Flag(c,"overRangeAccepted"),
                PeakValidity=Text(c,"peakValidity",64),LocationValidity=Text(c,"locationValidity",64),RawSha256=Text(c,"rawSha256",128)};
            if(q.RawSha256.Length>0&&!Regex.IsMatch(q.RawSha256,@"\A[0-9a-fA-F]{64}\z"))throw new InvalidDataException("Invalid acquisition raw SHA256.");
            return q;
        }
        internal static ReviewAcquisitionQuality Parse(string json)
        {return FromContext(ParseContext(json));}
        internal static Dictionary<string,object> ParseContext(string json)
        {
            if(json==null||json.Length>65536)throw new InvalidDataException("Acquisition context exceeds its metadata budget.");
            // Reject duplicate top-level keys (including escaped spellings) and
            // excessive depth before recursive allocation can discard evidence.
            int depth=0;char previous='\0';var keys=new HashSet<string>(StringComparer.Ordinal);
            var parser=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8};
            for(int i=0;i<json.Length;i++) {
                char c=json[i];if(Char.IsWhiteSpace(c))continue;
                if(c=='"') {
                    int start=i;bool escaped=false;while(++i<json.Length) {
                        char s=json[i];if(escaped){escaped=false;continue;}if(s=='\\'){escaped=true;continue;}if(s=='"')break;
                    }
                    if(i>=json.Length)throw new InvalidDataException("Incomplete acquisition context string.");
                    if(depth==1&&(previous=='{'||previous==',')) {
                        string key;try{key=parser.Deserialize<string>(json.Substring(start,i-start+1));}
                        catch(ArgumentException e){throw new InvalidDataException("Invalid acquisition context key.",e);}
                        if(!keys.Add(key))throw new InvalidDataException("Duplicate acquisition context field.");
                    }
                    previous='"';continue;
                }
                if(c=='{'||c=='['){if(++depth>8)throw new InvalidDataException("Acquisition context exceeds its nesting budget.");}
                else if(c=='}'||c==']'){if(--depth<0)throw new InvalidDataException("Invalid acquisition context structure.");}
                previous=c;
            }
            if(depth!=0)throw new InvalidDataException("Incomplete acquisition context structure.");
            try {
                var result=parser.DeserializeObject(json) as Dictionary<string,object>;
                if(result==null)throw new InvalidDataException("Acquisition context must be an object.");return result;
            }
            catch(Exception e)when(e is ArgumentException||e is InvalidOperationException){throw new InvalidDataException("Invalid acquisition context.",e);}
        }
        internal static string ReadHeaderLine(StreamReader reader)
        {
            var text=new StringBuilder(128);int value;
            while((value=reader.Read())>=0) {
                char c=(char)value;if(c=='\n')return text.ToString();
                if(c=='\r'){if(reader.Peek()=='\n')reader.Read();return text.ToString();}
                if(text.Length>=65536)throw new InvalidDataException("Acquisition header line exceeds its size budget.");text.Append(c);
            }
            return text.Length==0?null:text.ToString();
        }
        internal Dictionary<string,object> Context()
        {return new Dictionary<string,object>{{"manualOverRangeSaved",ManualOverRangeSaved},{"overRange",OverRange},
            {"overRangeAccepted",OverRangeAccepted},{"peakValidity",PeakValidity},{"locationValidity",LocationValidity},{"rawSha256",RawSha256},
            {"referenceOnly",ReferenceOnly},{"saveIsNotValidityConfirmation",true}};}
        internal static ReviewAcquisitionQuality Combine(ReviewAcquisitionQuality a,ReviewAcquisitionQuality b)
        {
            a=a??Unrecorded;b=b??Unrecorded;
            if(a.ReferenceOnly&&b.ReferenceOnly&&a.RawSha256.Length>0&&b.RawSha256.Length>0&&
                !String.Equals(a.RawSha256,b.RawSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Acquisition quality source hashes conflict.");
            return a.ReferenceOnly?a:b;
        }
        internal static ReviewAcquisitionQuality ReadFile(string path)
        {return (ReviewCapacitorDemInput.IsDem(path)?ReviewCapacitorDemInput.Metadata(path):ReviewLocationFileReader.ReadMetadata(path)).AcquisitionQuality;}
        internal static ReviewAnalysisResult Apply(ReviewAnalysisResult result,ReviewAcquisitionQuality quality)
        {
            quality=quality??Unrecorded;result.AcquisitionQuality=quality;
            if(!quality.ReferenceOnly||!result.Success)return result;
            for(int row=0;row<result.Parameters.GetLength(0);row++)result.Parameters[row,35]=0;
            // A saved risk segment must not become an accepted historical prior.
            result.AHistoryReceipt=null;
            // Capacitor statistics intentionally return a bounded descriptive
            // string, whereas cable algorithms return JSON. Preserve either.
            ReviewStatusJson.ValidateText(result.Status);
            var status=(result.Status??"").TrimStart().StartsWith("{",StringComparison.Ordinal)?
                ReviewStatusJson.DeserializeObject(result.Status) as Dictionary<string,object>:
                new Dictionary<string,object>{{"analysis_provenance_text",result.Status??""}};
            if(status==null)throw new InvalidDataException("Reference-only result lacks status evidence.");
            status["acquisition_quality"]=quality.Context();result.Status=ReviewStatusJson.Serialize(status);return result;
        }
    }

    public sealed partial class PdReviewControl
    {
        Dictionary<string,ReviewAcquisitionQuality> acquisitionQualities=new Dictionary<string,ReviewAcquisitionQuality>(StringComparer.Ordinal);
        string acquisitionQualityProject="";
        static string QualityKey(double[,] matrix,int row)
        {return matrix[row,0].ToString("0",CultureInfo.InvariantCulture)+":"+matrix[row,1].ToString("0",CultureInfo.InvariantCulture);}
        bool IsAcquisitionReferenceOnly(int row)
        {ReviewAcquisitionQuality q;return row>=0&&row<SourceRowCount&&acquisitionQualities!=null&&acquisitionQualities.TryGetValue(QualityKey(working,row),out q)&&q.ReferenceOnly;}
        [Browsable(false)] public bool CurrentAcquisitionReferenceOnly {get{return IsAcquisitionReferenceOnly(selected);}}
        [Browsable(false)] public string CurrentAcquisitionQualityCode {get{return selected<0?"None":CurrentAcquisitionReferenceOnly?"ReferenceOnly":"NoRiskMetadataRecorded";}}
        string AcquisitionRiskText()
        {return L("Overrange / uncertain peaks: reference only. Not accepted or used for learning.","超量程／峰值不确定：仅供参考，不纳入有效结果或学习。");}
        Dictionary<string,ReviewAcquisitionQuality> ResolveAcquisitionQualities(double[,] parameters,Dictionary<string,ReviewAcquisitionQuality> supplied=null,bool readSources=true)
        {
            var keys=new HashSet<string>(Enumerable.Range(0,parameters.GetLength(0)).Select(r=>QualityKey(parameters,r)),StringComparer.Ordinal);
            var ids=new HashSet<double>(Enumerable.Range(0,parameters.GetLength(0)).Select(r=>parameters[r,1]));
            var result=new Dictionary<string,ReviewAcquisitionQuality>(StringComparer.Ordinal);
            var retained=supplied??(SameAnalysisProjectPath(projectPath,acquisitionQualityProject)?acquisitionQualities:null);
            if(retained!=null)foreach(var p in retained)if(keys.Contains(p.Key)&&p.Value.ReferenceOnly)result[p.Key]=p.Value;
            // Only the private rollback path uses its exact captured map instead
            // of rereading files that may have caused the attempted load to fail.
            if(readSources&&analysisTree!=null) {
                var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
                for(int i=0;i<paths.Length;i++) {
                    string key=QualityKey(catalog,i);if(!ids.Contains(catalog[i,1]))continue;
                    var q=ReviewAcquisitionQuality.ReadFile(paths[i]);ReviewAcquisitionQuality old;
                    if(q.ReferenceOnly&&Enumerable.Range(0,parameters.GetLength(0)).Any(r=>parameters[r,1]==catalog[i,1]&&parameters[r,0]!=catalog[i,0]))
                        throw new InvalidDataException("Risk acquisition phase identity differs from its source record.");
                    if(!keys.Contains(key))continue;
                    if(result.TryGetValue(key,out old))q=ReviewAcquisitionQuality.Combine(old,q);
                    if(q.ReferenceOnly)result[key]=q;
                }
            }
            return result;
        }
        Dictionary<string,ReviewAcquisitionQuality> WithCompletedAcquisitionQualities(CompletedFile[] files)
        {
            var result=new Dictionary<string,ReviewAcquisitionQuality>(acquisitionQualities,StringComparer.Ordinal);
            foreach(var file in files) {
                var q=ReviewAcquisitionQuality.Combine(file.Result.AcquisitionQuality,ReviewAcquisitionQuality.ReadFile(file.Request.LocationFilePath));
                if(!q.ReferenceOnly)continue;
                for(int row=0;row<file.Result.Parameters.GetLength(0);row++)result[QualityKey(file.Result.Parameters,row)]=q;
            }
            return result;
        }
        string SerializeAcquisitionQualities()
        {return ArchiveJson().Serialize(acquisitionQualities.ToDictionary(p=>p.Key,p=>(object)p.Value.Context(),StringComparer.Ordinal));}
        static Dictionary<string,ReviewAcquisitionQuality> ParseAcquisitionQualities(string json)
        {
            var result=new Dictionary<string,ReviewAcquisitionQuality>(StringComparer.Ordinal);if(String.IsNullOrEmpty(json))return result;
            if(json.Length>16*1024*1024)throw new InvalidDataException("Saved acquisition quality metadata exceeds its budget.");
            var data=ArchiveJson().DeserializeObject(json) as Dictionary<string,object>;
            if(data==null||data.Count>100000)throw new InvalidDataException("Invalid saved acquisition quality metadata.");
            foreach(var p in data) {
                if(p.Key.Length>128||!Regex.IsMatch(p.Key,@"\A[0-3]:[0-9]+\z"))throw new InvalidDataException("Invalid acquisition quality file identity.");
                var q=ReviewAcquisitionQuality.FromContext(p.Value as Dictionary<string,object>);
                if(!q.ReferenceOnly)throw new InvalidDataException("Saved risk evidence cannot be relabelled valid.");result.Add(p.Key,q);
            }
            return result;
        }
        bool HasAcquisitionRiskInMatrix(double[,] matrix)
        {return HasAcquisitionRiskInMatrix(matrix,acquisitionQualities);}
        static bool HasAcquisitionRiskInMatrix(double[,] matrix,Dictionary<string,ReviewAcquisitionQuality> qualities)
        {ReviewAcquisitionQuality q;for(int r=0;r<matrix.GetLength(0);r++)if(qualities.TryGetValue(QualityKey(matrix,r),out q)&&q.ReferenceOnly)return true;return false;}
    }
}
