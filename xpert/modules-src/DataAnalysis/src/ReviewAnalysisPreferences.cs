using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        void RestoreAnalysisPreferences()
        {
            try {
                var value=ReviewAnalysisPreferences.Read();
                if(value==null)return;
                SetAnalysisSettings(value.Attenuation,value.CorrelationError,value.ThresholdMultiplier,value.MaximumPulses,value.PhaseFilter,value.Quadrants);
                AnalysisAlgorithmIndex=value.AlgorithmIndex;AnalysisMultipleReflectionSuppressionCount=value.Suppression;
                AnalysisUseFirstArrivalTiming=value.FirstArrival;
                ModelLibraryPath=value.ModelPath;
            } catch(Exception error) { if(IsFatalHostException(error))throw; }
        }
        void SaveAnalysisPreferences()
        {
            try {
                ReviewAnalysisPreferences.Write(new ReviewAnalysisPreferences.Values {
                    Attenuation=AnalysisAttenuationTolerancePercent,CorrelationError=AnalysisCorrelationErrorPercent,
                    ThresholdMultiplier=AnalysisAutomaticThresholdMultiplier,MaximumPulses=AnalysisMaximumPulseCount,
                    AlgorithmIndex=AnalysisAlgorithmIndex,Suppression=AnalysisMultipleReflectionSuppressionCount,
                    PhaseFilter=AnalysisPhaseFilterEnabled,Quadrants=AnalysisQuadrants,FirstArrival=AnalysisUseFirstArrivalTiming,ModelPath=ModelLibraryPath});
            } catch(Exception error) {
                if(IsFatalHostException(error))throw;
                LastError=L("Settings applied, but preferences could not be saved: ","设置已应用，但参数记忆保存失败：")+error.Message;
            }
        }
    }

    // Only the explicit dialog-OK path writes preferences. The schema contains
    // analysis choices and model location, never a password, verifier, or unlock state.
    internal static class ReviewAnalysisPreferences
    {
        internal sealed class Values
        {
            internal double Attenuation,CorrelationError,ThresholdMultiplier;
            internal int MaximumPulses,AlgorithmIndex,Suppression;
            internal bool PhaseFilter,FirstArrival;
            internal bool[] Quadrants;
            internal string ModelPath="";
        }
        internal static Func<string> PathProvider=()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HVX","PdReview","analysis-settings.json");
        static readonly object Gate=new object();
        const int MaximumBytes=16384;
        static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=8};}
        internal static Values Read()
        {
            lock(Gate) {
                string path=PathProvider();if(!File.Exists(path))return null;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(stream.Length>MaximumBytes)throw new InvalidDataException("Analysis preferences exceed the size limit.");
                    using(var reader=new StreamReader(stream,new UTF8Encoding(false,true),false))
                        return Parse(Serializer().DeserializeObject(reader.ReadToEnd()) as Dictionary<string,object>);
                }
            }
        }
        static Values Parse(Dictionary<string,object> data)
        {
            if(data==null||!data.ContainsKey("Schema")||!object.Equals(data["Schema"],"HVX.AnalysisPreferences"))
                throw new InvalidDataException("Invalid analysis preferences schema.");
            int version=Integer(data,"Version",1,2);
            if(data.Count!=(version==1?11:12))throw new InvalidDataException("Invalid analysis preferences fields.");
            object model;
            string path="";
            if(version==2){
                if(!data.TryGetValue("ModelLibraryPath",out model)||!(model is string)||((string)model).Length>4096||((string)model).IndexOf('\0')>=0)
                    throw new InvalidDataException("Invalid model path preference.");
                path=((string)model).Trim();
            }
            var quadrants=data["Quadrants"] as object[];
            if(quadrants==null||quadrants.Length!=4||quadrants.Any(x=>!(x is bool)))throw new InvalidDataException("Invalid phase quadrant preferences.");
            return new Values {
                Attenuation=Number(data,"AttenuationPercent",0,100),CorrelationError=Number(data,"CorrelationErrorPercent",0,100),
                ThresholdMultiplier=Number(data,"ThresholdMultiplier",0,1000),MaximumPulses=Integer(data,"MaximumPulses",1,100),
                AlgorithmIndex=Integer(data,"AlgorithmIndex",0,9),Suppression=Integer(data,"MultipleReflectionSuppressionCount",-1,100),
                PhaseFilter=Flag(data,"PhaseFilter"),FirstArrival=Flag(data,"UseFirstArrivalTiming"),Quadrants=quadrants.Cast<bool>().ToArray(),ModelPath=path};
        }
        static double Number(Dictionary<string,object> data,string name,double low,double high)
        {
            object raw;if(!data.TryGetValue(name,out raw)||!(raw is int||raw is long||raw is double||raw is decimal))throw new InvalidDataException("Invalid numerical preference: "+name);
            double value=Convert.ToDouble(raw,CultureInfo.InvariantCulture);
            if(double.IsNaN(value)||double.IsInfinity(value)||value<low||value>high)throw new InvalidDataException("Out-of-range preference: "+name);
            return value;
        }
        static int Integer(Dictionary<string,object> data,string name,int low,int high)
        {double value=Number(data,name,low,high);if(value!=Math.Truncate(value))throw new InvalidDataException("Invalid integer preference: "+name);return (int)value;}
        static bool Flag(Dictionary<string,object> data,string name)
        {object value;if(!data.TryGetValue(name,out value)||!(value is bool))throw new InvalidDataException("Invalid flag preference: "+name);return (bool)value;}
        internal static void Write(Values value)
        {
            var data=new Dictionary<string,object> {
                {"Schema","HVX.AnalysisPreferences"},{"Version",2},{"AttenuationPercent",value.Attenuation},
                {"CorrelationErrorPercent",value.CorrelationError},{"ThresholdMultiplier",value.ThresholdMultiplier},
                {"MaximumPulses",value.MaximumPulses},{"AlgorithmIndex",value.AlgorithmIndex},{"MultipleReflectionSuppressionCount",value.Suppression},
                {"PhaseFilter",value.PhaseFilter},{"UseFirstArrivalTiming",value.FirstArrival},{"Quadrants",value.Quadrants},{"ModelLibraryPath",value.ModelPath??""}};
            string json=Serializer().Serialize(data);
            Parse(Serializer().DeserializeObject(json) as Dictionary<string,object>);
            byte[] bytes=new UTF8Encoding(false,true).GetBytes(json);
            if(bytes.Length>MaximumBytes)throw new InvalidDataException("Analysis preferences exceed the size limit.");
            lock(Gate) {
                string path=Path.GetFullPath(PathProvider()),directory=Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);string temporary=Path.Combine(directory,".analysis-settings-"+Guid.NewGuid().ToString("N")+".tmp");
                try {
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                } finally {if(File.Exists(temporary))File.Delete(temporary);}
            }
        }
    }
}
