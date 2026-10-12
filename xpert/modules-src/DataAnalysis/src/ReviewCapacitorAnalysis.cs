using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HVX.SignalProcessing;

namespace HVX.PdReview
{
    // Explicit capacitor route. LOC contains calibrated pC * 100000, not volts:
    // applying a CAL coefficient again would square its effect. Never call the
    // cable reflection analyzer, and never synthesize a length or a wave speed.
    internal static class ReviewCapacitorAnalysis
    {
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
        sealed class Packet { internal int Start,End,Peak;internal double Phase; }
        internal static ReviewAnalysisResult Analyze(ReviewAnalysisRequest request,string root)
        {
            if(!HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root))throw new InvalidDataException("电容统计需要项目明确声明电容负载。");
            var data=ReviewLocationFileReader.Read(request.LocationFilePath,root);
            var identity=Regex.Match(Path.GetFileName(request.LocationFilePath),@"^(ALL|A|B|C)_([-+]?[0-9]+(?:\.[0-9]+)?)kV_",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(!identity.Success)throw new InvalidDataException("LOC 文件名缺少可靠相别和电压标识。");
            string phase=identity.Groups[1].Value.ToUpperInvariant();int phaseCode=phase=="A"?1:phase=="B"?2:phase=="C"?3:0;
            double voltage=Double.Parse(identity.Groups[2].Value,Inv);
            if(voltage<0)throw new InvalidDataException("LOC 电压不能为负值。");
            bool zero=voltage==0;
            if(zero&&(!request.AnalyzeZeroVoltageData||!data.ZeroVoltageAnalysisOptIn))throw new InvalidDataException("该零电压记录仅可作为噪声参考。");
            var header=ReadMetadata(request.LocationFilePath,11);
            if(header.ContainsKey("Sample units")&&!new[]{"pC*100000","pC x100000"}.Contains(header["Sample units"]))
                throw new InvalidDataException("此 LOC 未声明支持的已校准 pC 存储单位；不能猜测电压换算系数。");
            double frequency=request.OscillationFrequencyHz>0?request.OscillationFrequencyHz:Value(header,"F(Hz)","Frequency","Hz");
            double initialPhase=Value(header,"Initial phase","Phase");
            string dem=Path.ChangeExtension(request.LocationFilePath,".dem");
            if(!(frequency>0)&&File.Exists(dem))frequency=Value(ReadMetadata(dem,32),"Hz","F(Hz)","Frequency");
            if(zero){frequency=0;initialPhase=Double.NaN;}
            // A recorded phase origin is required. Unknown timing remains NaN
            // rather than placing all events at a fabricated zero-degree phase.
            var highPassed=AcquisitionHighPassedSamples(data);
            var filtered=highPassed;
            var calibration=HVX.CalibrationStorage.LiveCalibrationRecord.Best(root,phase,request.UseFirstArrivalTiming,null,true);
            if(calibration!=null&&calibration.IsCapacitor&&calibration.BandwidthMHz>0)
                filtered=PdLinearLowPass.Apply(filtered,1/data.SampleRateHz,calibration.BandwidthMHz);
            return AnalyzeSamples(request,root,data,highPassed,filtered,phaseCode,voltage,
                peak=>frequency>0&&Finite(initialPhase)?((initialPhase+360*frequency*peak/data.SampleRateHz)%360+360)%360:Double.NaN,
                "coefficient=already-applied-in-LOC; location-algorithm=not-called"+(Finite(initialPhase)&&frequency>0?"":"; phase=unknown-no-invented-frequency/origin"));
        }
        internal static ReviewAnalysisResult AnalyzeDem(ReviewAnalysisRequest request,string root)
        {
            var input=ReviewCapacitorDemInput.Read(request.LocationFilePath,root,request.AnalyzeZeroVoltageData);
            // The full original segment is filtered exactly once. DEM bins and
            // optional display bandwidth do not enter pulse quantification.
            return AnalyzeSamples(request,root,input.Data,input.HighPassed,input.HighPassed,input.PhaseCode,input.TargetKv,input.PhaseAt,input.Status);
        }
        static ReviewAnalysisResult AnalyzeSamples(ReviewAnalysisRequest request,string root,ReviewLocationFileData data,
            double[] highPassed,double[] filtered,int phaseCode,double voltage,Func<int,double> phaseAt,string provenance)
        {
            var templates=NoisePulseTemplates.Load(root,false);
            var absolute=filtered.Select(Math.Abs).ToArray();Array.Sort(absolute);
            double sigma=absolute[absolute.Length/2]/.6744897501960817;
            double threshold=request.ManualThresholdEnabled?request.ManualThreshold:Math.Max(sigma*Math.Max(1,request.AutomaticThresholdMultiplier),absolute[absolute.Length-1]*1e-9);
            if(!(threshold>0))return Empty(threshold,"没有超过阈值的入射脉冲。");
            int quiet=Math.Max(8,(int)Math.Ceiling(data.SampleRateHz*.25e-6));
            int guard=PdLinearHighPass.GuardSamples(data.SampleRateHz),excluded=0,clipped=0;
            var packets=new List<Packet>();
            for(int i=guard;i<filtered.Length-guard;i++)
            {
                if(Math.Abs(filtered[i])<=threshold)continue;
                int first=i,peak=i,last=i,quietCount=0;
                while(++i<filtered.Length-guard) {
                    if(Math.Abs(filtered[i])>Math.Abs(filtered[peak]))peak=i;
                    if(Math.Abs(filtered[i])>Math.Max(sigma*2,threshold*.1)){last=i;quietCount=0;}
                    else if(++quietCount>=quiet)break;
                }
                int start=Math.Max(0,first-quiet),end=Math.Min(filtered.Length-1,last+quiet);
                if(i>=filtered.Length-guard||start<guard||end>=filtered.Length-guard){clipped++;continue;}
                // Noise templates are taken from the common 10 kHz high-pass
                // observation, before optional calibration-bandwidth reduction.
                if(NoisePulseTemplates.Matches(templates,highPassed,peak,data.SampleRateHz,request.NoiseSimilarityTolerancePercent)){excluded++;continue;}
                double phi=phaseAt(peak);
                if(Finite(phi)&&!request.PhaseQuadrants[Math.Min(3,(int)(phi/90))])continue;
                packets.Add(new Packet{Start=start,End=end,Peak=peak,Phase=phi});
                if(packets.Count==request.MaxPulses)break;
            }
            int rows=packets.Count,width=rows==0?0:packets.Max(p=>p.End-p.Start+1);
            var result=new ReviewAnalysisResult{Success=true,AppliedThreshold=threshold,Parameters=new double[rows,36],Raw=new double[rows,width],Filtered=new double[rows,width],Equivalent=new double[rows,width]};
            for(int row=0;row<rows;row++) {
                var packet=packets[row];var p=result.Parameters;
                for(int c=0;c<36;c++)p[row,c]=Double.NaN;
                p[row,0]=phaseCode;p[row,1]=request.LocationDataId;p[row,2]=voltage;p[row,3]=packet.Phase;
                p[row,4]=threshold;p[row,7]=data.SampleRateHz;p[row,8]=request.LocationDataId;p[row,9]=row;p[row,10]=packet.Peak;
                p[row,13]=0;p[row,15]=filtered[packet.Peak];p[row,16]=p[row,15];p[row,20]=packet.Peak-packet.Start;
                p[row,26]=-2; // Explicit capacitor statistical row, not a location algorithm result.
                p[row,28]=sigma;p[row,29]=threshold;p[row,30]=-threshold;p[row,31]=packet.Start;p[row,32]=packet.End;
                p[row,34]=request.AutomaticAcceptance?1:0;p[row,35]=request.AutomaticAcceptance?1:0;
                double energy=0,derivative=0;
                for(int k=0;k<width;k++) {
                    int sample=packet.Start+k;bool valid=sample<=packet.End;
                    result.Raw[row,k]=valid?data.ChargeSamples[sample]:Double.NaN;
                    result.Filtered[row,k]=result.Equivalent[row,k]=valid?filtered[sample]:Double.NaN;
                    if(valid){energy+=filtered[sample]*filtered[sample];if(k>0){double d=filtered[sample]-filtered[sample-1];derivative+=d*d;}}
                }
                p[row,18]=energy/data.SampleRateHz;p[row,23]=energy/(p[row,15]*p[row,15])/data.SampleRateHz*1e9;
                p[row,24]=energy>0?data.SampleRateHz/(2*Math.PI)*Math.Sqrt(derivative/energy):Double.NaN;
            }
            result.Status="电容局放：仅统计电荷、电压、相位与次数；0 m 是无定位占位，不代表故障位置。"+
                " pulses="+rows+"; noise-excluded="+excluded+"; clipped="+clipped+"; units=calibrated-pC; filter="+PdLinearHighPass.FilterId+
                "; acquisition-filter="+(String.IsNullOrEmpty(data.AcquisitionFilterId)?"unrecorded-derived-only":data.AcquisitionFilterId)+
                "; "+provenance;
            return result;
        }
        // Current acquisitions are filtered before cropping/saving. Repeating
        // that same FIR would square its frequency response. Legacy data retains
        // the existing derived-analysis filter, without rewriting its provenance.
        internal static double[] AcquisitionHighPassedSamples(ReviewLocationFileData data)
        {
            if(data==null)throw new ArgumentNullException("data");
            return data.AcquisitionFilterId==PdLinearHighPass.FilterId
                ? data.ChargeSamples : PdLinearHighPass.Apply(data.ChargeSamples,data.SampleRateHz);
        }
        static ReviewAnalysisResult Empty(double threshold,string message){return new ReviewAnalysisResult{Success=true,AppliedThreshold=threshold,Status="电容局放 · 无定位："+message};}
        static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
        static Dictionary<string,string> ReadMetadata(string path,int limit)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            // Only read a bounded header of this LOC or its same-name DEM; never
            // follow a path supplied by file content.
            using(var reader=new StreamReader(path,Encoding.GetEncoding(936),true))for(int i=0;i<limit;i++) {
                string line=reader.ReadLine();if(line==null)break;
                int split=line.IndexOf('\t');if(split<0)split=line.IndexOfAny(new[]{'=','：',':'});if(split<0)continue;
                string key=line.Substring(0,split).Trim();if(key.Length>0)result[key]=line.Substring(split+1).Trim();
            }
            return result;
        }
        static double Value(Dictionary<string,string> values,params string[] names)
        {
            foreach(string name in names){string text;double number;if(values.TryGetValue(name,out text)&&Double.TryParse(text,NumberStyles.Float,Inv,out number)&&Finite(number))return number;}
            return Double.NaN;
        }
    }
}
