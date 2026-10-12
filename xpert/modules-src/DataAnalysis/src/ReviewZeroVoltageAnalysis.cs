using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal static partial class ReviewLocationFileReader
    {
        private static FilterMetadata ParseZeroVoltageMetadata(string json,string phase,string unit,double start)
        {
            var m=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(json);
            object value;
            if(m==null||!m.TryGetValue("schema",out value)||!Object.Equals(value,"HVX.ZeroVoltageAnalysis/1")||
                !m.TryGetValue("analysisOptIn",out value)||!Object.Equals(value,true)||
                !m.TryGetValue("phaseKnown",out value)||!Object.Equals(value,false)||
                !m.TryGetValue("simulation",out value)||!(value is bool)||phase!="NaN"||unit!="pC x 100000")
                throw new InvalidDataException("Zero-voltage analysis identity or units are invalid.");
            foreach(string key in new[]{"locationAcquisitionId","noiseReferenceSourceId"})
                if(!m.TryGetValue(key,out value)||!(value is string)||String.IsNullOrWhiteSpace((string)value))
                    throw new InvalidDataException("Zero-voltage source identity is missing.");
            double target=ZeroNumber(m,"targetVoltageKv"),rate=ZeroNumber(m,"sampleRateHz"),count=ZeroNumber(m,"sampleCount"),offset=ZeroNumber(m,"captureStartSeconds");
            if(target!=0||rate<=0||count<16||count>MaximumSamples||count!=Math.Truncate(count)||offset<0||!Finite(start)||offset!=start)
                throw new InvalidDataException("Zero-voltage acquisition metadata is invalid.");
            var result=new FilterMetadata{Count=(int)count,Rate=rate,ZeroVoltage=true};
            object scope;
            if(m.TryGetValue("pdAcquisitionFilterId",out value)){
                if(!Object.Equals(value,HVX.SignalProcessing.PdLinearHighPass.FilterId)||
                    !m.TryGetValue("pdAcquisitionFilterScope",out scope)||!Object.Equals(scope,"full-segment-before-zero-voltage-analysis"))
                    throw new InvalidDataException("Unsupported zero-voltage acquisition filter provenance.");
                result.Id=(string)value;
            }else if(m.ContainsKey("pdAcquisitionFilterScope"))throw new InvalidDataException("Filter scope has no identity.");
            return result;
        }
        static double ZeroNumber(Dictionary<string,object> metadata,string key)
        {
            object value;double number;
            if(!metadata.TryGetValue(key,out value)||value is bool||value is string||value==null||
                !Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||!Finite(number))
                throw new InvalidDataException("Invalid zero-voltage numeric metadata: "+key);
            return number;
        }
        // A bounded header check is enough for tree eligibility. The complete
        // body/count is revalidated immediately before any analysis is invoked.
        internal static bool IsZeroVoltageAnalysisRecord(string path)
        {
            try{
                if(!Regex.IsMatch(Path.GetFileName(path),@"^(ALL|A|B|C|L1|L2|L3)_0(?:\.0+)?kV_",RegexOptions.IgnoreCase))return false;
                if(new FileInfo(path).Length>MaximumFileBytes)return false;
                using(var reader=Open(path)){
                    double velocity,voltage;FilterMetadata metadata;
                    ReadHeader(reader,out velocity,out voltage,out metadata);
                    return voltage==0&&metadata!=null&&metadata.ZeroVoltage;
                }
            }catch(Exception error){
                if(error is OutOfMemoryException||error is StackOverflowException||error is AccessViolationException)throw;
                return false;
            }
        }
        internal static ReviewLocationFileData ReadMetadata(string path)
        {
            if(new FileInfo(path).Length>MaximumFileBytes)throw new InvalidDataException("LOC exceeds the file size limit.");
            using(var reader=Open(path)){
                double velocity,voltage;FilterMetadata metadata;ReviewAcquisitionQuality quality;
                double rate=ReadHeader(reader,out velocity,out voltage,out metadata,out quality);
                return new ReviewLocationFileData{DataPath=path,SampleRateHz=rate,VelocityMPerUs=velocity,VoltageKv=voltage,
                    AcquisitionQuality=quality,
                    ZeroVoltageAnalysisOptIn=metadata!=null&&metadata.ZeroVoltage,AcquisitionFilterId=metadata==null?String.Empty:metadata.Id};
            }
        }
    }
    public sealed partial class PdReviewControl
    {
        bool analyzeZeroVoltageData;
        public bool AnalyzeZeroVoltageData
        {
            get{return analyzeZeroVoltageData;}
            set{
                if(InvokeRequired){Invoke(new Action(()=>AnalyzeZeroVoltageData=value));return;}
                if(value==analyzeZeroVoltageData)return;
                if(analysisBusy)throw new InvalidOperationException("请等待分析完成，再修改零电压分析设置。");
                // Removing this opt-in only removes derived zero-voltage results;
                // neither original samples nor nonzero results are changed.
                if(!value&&analysisTree!=null){
                    var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
                    for(int i=0;i<paths.Length;i++)if(catalog[i,2]==0&&catalog[i,4]!=0&&!DeleteLocationAnalysis(paths[i]))
                        throw new InvalidOperationException(LastError);
                }
                analyzeZeroVoltageData=value;
                if(analysisTree!=null)analysisTree.AnalyzeZeroVoltageData=value;
            }
        }
        bool CanAnalyzeRecord(string path,double voltage)
        {return voltage>0||voltage==0&&AnalyzeZeroVoltageData&&(capacitorProject&&ReviewCapacitorDemInput.IsDem(path)?
            ReviewCapacitorDemInput.ZeroOptIn(path):ReviewLocationFileReader.IsZeroVoltageAnalysisRecord(path));}
    }
}
