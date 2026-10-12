using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HVX.ReportGenerator
{
    // Report-side quality adapter only; never loads a location algorithm or raw
    // path from the metadata. A risk label is independent of choosing Save.
    internal static class ReportAcquisitionQuality
    {
        static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=16};
        static Dictionary<string,object> Object(string text,int limit)
        {
            if(text==null||text.Length>limit)throw new InvalidDataException("Acquisition quality metadata exceeds its budget.");
            // Do not let duplicate JSON keys (including escaped spellings) hide
            // a true risk flag before the bounded deserializer sees them.
            var stack=new List<HashSet<string>>();char previous='\0';
            for(int i=0;i<text.Length;i++){
                char c=text[i];if(Char.IsWhiteSpace(c))continue;
                if(c=='"'){
                    int start=i;bool escaped=false;
                    while(++i<text.Length){char s=text[i];if(escaped){escaped=false;continue;}if(s=='\\'){escaped=true;continue;}if(s=='"')break;}
                    if(i==text.Length)throw new InvalidDataException("Incomplete acquisition quality text.");
                    if(stack.Count>0&&stack.Last()!=null&&(previous=='{'||previous==','))
                        if(!stack.Last().Add(Json.Deserialize<string>(text.Substring(start,i-start+1))))throw new InvalidDataException("Duplicate acquisition quality field.");
                    previous='"';continue;
                }
                if(c=='{'||c=='['){stack.Add(c=='{'?new HashSet<string>(StringComparer.Ordinal):null);if(stack.Count>16)throw new InvalidDataException("Acquisition quality nesting exceeds its budget.");}
                else if(c=='}'||c==']'){if(stack.Count==0)throw new InvalidDataException("Invalid acquisition quality structure.");stack.RemoveAt(stack.Count-1);}
                previous=c;
            }
            if(stack.Count!=0)throw new InvalidDataException("Incomplete acquisition quality structure.");
            var value=Json.DeserializeObject(text) as Dictionary<string,object>;
            if(value==null)throw new InvalidDataException("Acquisition quality must be an object.");return value;
        }
        static bool Risk(Dictionary<string,object> context)
        {
            if(context==null)throw new InvalidDataException("Invalid saved acquisition quality.");bool risk=false;object value;
            foreach(string key in new[]{"manualOverRangeSaved","overRange","overRangeAccepted"})if(context.TryGetValue(key,out value)){
                if(!(value is bool))throw new InvalidDataException("Invalid acquisition quality flag.");risk|=(bool)value;
            }
            foreach(string key in new[]{"peakValidity","locationValidity","rawSha256"})if(context.TryGetValue(key,out value)&&value!=null){
                var text=value as string;if(text==null||text.Length>(key=="rawSha256"?128:64)||text.Any(Char.IsControl))throw new InvalidDataException("Invalid acquisition quality text.");
                if(key=="rawSha256"){if(text.Length>0&&!Regex.IsMatch(text,@"\A[0-9a-fA-F]{64}\z"))throw new InvalidDataException("Invalid acquisition quality hash.");}
                else risk|=text.Length>0&&text!="valid";
            }
            return risk;
        }
        internal static string Key(double[,] matrix,int row)
        {return matrix[row,0].ToString("0",CultureInfo.InvariantCulture)+":"+matrix[row,1].ToString("0",CultureInfo.InvariantCulture);}
        static string SourceIdentity(string phaseText,string idText)
        {
            double phase,id;
            if(!Double.TryParse(phaseText,NumberStyles.Float,CultureInfo.InvariantCulture,out phase)||!Double.TryParse(idText,NumberStyles.Float,CultureInfo.InvariantCulture,out id)||
                Double.IsNaN(phase)||Double.IsNaN(id)||phase<0||phase>3||phase!=Math.Truncate(phase)||id<0||id>9007199254740991d||id!=Math.Truncate(id))throw new InvalidDataException("Invalid quality source identity.");
            return phase.ToString("0",CultureInfo.InvariantCulture)+":"+id.ToString("0",CultureInfo.InvariantCulture);
        }
        static string HeaderLine(StreamReader reader,ref int total)
        {
            var line=new StringBuilder(128);int value;
            while((value=reader.Read())>=0){char c=(char)value;if(++total>16*1024*1024)throw new InvalidDataException("Acquisition header work budget exceeded.");
                if(c=='\n')return line.ToString();if(c=='\r'){if(reader.Peek()=='\n')reader.Read();return line.ToString();}
                if(line.Length>=65536)throw new InvalidDataException("Acquisition header line exceeds its budget.");line.Append(c);
            }return line.Length==0?null:line.ToString();
        }
        internal static void Read(ReportAnalysisPackage package,Dictionary<string,string> settings,Dictionary<string,object> session,double[,] matrix,List<string> warnings)
        {
            string saved;var map=settings.TryGetValue("AcquisitionQualities",out saved)?Object(saved,16*1024*1024):new Dictionary<string,object>();
            if(map.Count>100000)throw new InvalidDataException("Too many acquisition quality identities.");
            foreach(var pair in map)if(pair.Key.Length>128||!Regex.IsMatch(pair.Key,@"\A[0-3]:[0-9]+\z")||!Risk(pair.Value as Dictionary<string,object>))throw new InvalidDataException("Saved risk evidence cannot be relabelled valid.");
            // Older archives did not persist quality. If their original LOC/DEM
            // is still available, inspect only its bounded header, conservatively
            // adding risk; never remove a saved risk or declare the ADC verified.
            object filesValue;var files=session.TryGetValue("files",out filesValue)?filesValue as object[]:null;int total=0;
            var referenced=new HashSet<string>(Enumerable.Range(0,matrix.GetLength(0)).Select(r=>Key(matrix,r)),StringComparer.Ordinal);
            var unavailable=new HashSet<string>(StringComparer.Ordinal);var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(files!=null){
                if(files.Length>100000)throw new InvalidDataException("Too many quality source records.");
                string root=Path.GetFullPath(package.ProjectPath).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
                foreach(var value in files){
                    var row=value as object[];if(row==null||row.Length<3||!(row[0] is string)||!(row[1] is string)||!(row[2] is string))throw new InvalidDataException("Invalid saved quality source identity.");
                    string relative=(string)row[0];if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative)||relative.Contains(":"))throw new InvalidDataException("Invalid saved quality source path.");
                    string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
                    if(!path.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Quality source leaves its project.");
                    string extension=Path.GetExtension(path);if(!extension.Equals(".loc",StringComparison.OrdinalIgnoreCase)&&!extension.Equals(".dem",StringComparison.OrdinalIgnoreCase))continue;
                    string key=SourceIdentity((string)row[1],(string)row[2]);
                    if(!paths.Add(path))throw new InvalidDataException("Duplicate saved quality source path.");
                    for(string cursor=path;!String.IsNullOrEmpty(cursor);cursor=Path.GetDirectoryName(cursor))
                        if((File.Exists(cursor)||Directory.Exists(cursor))&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked acquisition quality source is unavailable.");
                    // Empty placeholders and missing recordings are not corrupt
                    // quality headers. Omit them from the in-memory load scope,
                    // retaining the archive and every persisted risk label.
                    if(!File.Exists(path)||new FileInfo(path).Length==0){
                        unavailable.Add(key);package.SkippedSourceRecordCount++;
                        if(row.Length>=6&&object.Equals(row[4],"0")&&object.Equals(row[5],"1"))package.SkippedPendingSelectedRecordCount++;
                        continue;
                    }
                    // The catalog also contains unanalysed recordings. Their
                    // headers cannot invalidate an unrelated saved result scope.
                    if(!referenced.Contains(key))continue;
                    bool seen=false;using(var reader=new StreamReader(path,Encoding.UTF8,true,4096))for(int i=0;i<(extension.Equals(".dem",StringComparison.OrdinalIgnoreCase)?16:11);i++){
                        string line=HeaderLine(reader,ref total);if(line==null)throw new InvalidDataException("Incomplete acquisition quality header.");
                        int split=line.IndexOf('\t'),colon=line.IndexOf(':');if(split<0||colon>=0&&colon<split)split=colon;if(split<0)continue;
                        if(!line.Substring(0,split).Replace(" ","").Equals("Acquisitioncontext",StringComparison.OrdinalIgnoreCase))continue;
                        if(seen)throw new InvalidDataException("Duplicate acquisition quality header.");seen=true;
                        var context=Object(line.Substring(split+1),65536);
                        if(Risk(context)&&!map.ContainsKey(key))map[key]=context;
                    }
                }
            }
            package.UnavailableSourceRows=Enumerable.Range(0,matrix.GetLength(0)).Where(r=>unavailable.Contains(Key(matrix,r))).ToArray();
            if(package.SkippedSourceRecordCount>0)warnings.Add("Unavailable source records omitted from the load scope: "+package.SkippedSourceRecordCount+"; archived result rows with unavailable sources: "+package.UnavailableSourceRows.Length+". Original files and archive are unchanged.");
            package.AcquisitionQualityJson=Json.Serialize(map);
            var riskIds=map.Keys.GroupBy(k=>k.Substring(2),StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>new HashSet<string>(g,StringComparer.Ordinal),StringComparer.Ordinal);
            for(int r=0;r<matrix.GetLength(0);r++){
                HashSet<string> identities;string key=Key(matrix,r);
                if(riskIds.TryGetValue(matrix[r,1].ToString("0",CultureInfo.InvariantCulture),out identities)&&!identities.Contains(key))throw new InvalidDataException("Risk acquisition phase identity differs from its report source.");
            }
            package.AcquisitionReferenceOnlyRows=Enumerable.Range(0,matrix.GetLength(0)).Where(r=>map.ContainsKey(Key(matrix,r))).ToArray();
            if(package.AcquisitionReferenceOnlyRows.Length>0)warnings.Add("Saved overrange / uncertain-peak records are reference only and excluded from valid report results and learning.");
        }
    }

    public sealed partial class ReportGeneratorControl
    {
        bool HasAcquisitionRisk {get{return analysisPackage!=null&&analysisPackage.AcquisitionReferenceOnlyRows.Length>0;}}
        string PartialSourceLoadWarning()
        {return analysisPackage!=null&&analysisPackage.SkippedSourceRecordCount>0?String.Format(ReportLanguages.Ui(languageIndex,
            "已跳过 {0} 条缺失或空数据，仅加载其余可用数据。",
            "Skipped {0} missing or empty recordings; only available data was loaded."),analysisPackage.SkippedSourceRecordCount):"";}
        string AcquisitionRiskWarning()
        {return HasAcquisitionRisk?String.Format(ReportLanguages.Ui(languageIndex,
            "采集质量说明：{0} 条超量程／峰值不确定记录仅供参考，未纳入有效统计、可信定位或学习。保存不等于有效性确认。",
            "Acquisition quality: {0} overrange / uncertain-peak records are reference only, excluded from valid statistics, trusted locations and learning. Saving does not confirm validity."),analysisPackage.AcquisitionReferenceOnlyRows.Length):"";}
        string AcquisitionRiskShortWarning()
        {return HasAcquisitionRisk?String.Format(ReportLanguages.Ui(languageIndex,"{0} 条风险采集仅供参考，不纳入有效结果或学习。","{0} risk acquisitions are reference only, excluded from valid results and learning."),analysisPackage.AcquisitionReferenceOnlyRows.Length):"";}
        string WithAcquisitionRiskWarning(string text)
        {string warning=String.Join(Environment.NewLine,new[]{PartialSourceLoadWarning(),AcquisitionRiskWarning()}.Where(x=>x.Length>0));return warning.Length==0||(text??"").StartsWith(warning,StringComparison.Ordinal)?text??"":warning+Environment.NewLine+(text??"");}
    }
}
