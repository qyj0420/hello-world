using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    internal sealed class ReviewLocationFileData
    {
        internal string DataPath;
        internal double[] ChargeSamples;
        internal double SampleRateHz;
        internal double VelocityMPerUs=double.NaN;
        internal double VoltageKv=double.NaN;
        internal string AcquisitionFilterId=String.Empty;
        internal bool ZeroVoltageAnalysisOptIn;
        internal ReviewAcquisitionQuality AcquisitionQuality=ReviewAcquisitionQuality.Unrecorded;
    }

    // LOC storage is already calibrated charge * 100000. Do not apply CAL again.
    // This reader only reads the selected LOC and never follows header paths.
    internal static partial class ReviewLocationFileReader
    {
        internal const int MaximumSamples = 16000000;
        private const long MaximumFileBytes = 512L * 1024 * 1024;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly char[] SampleColumnSeparators = {'\t',' ',','};
        private const string Number = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        internal static double ReadSampleRateHz(string path)
        {
            if(new FileInfo(path).Length>MaximumFileBytes)throw new InvalidDataException("LOC exceeds the 512 MiB limit.");
            using(var reader=Open(path))return ReadHeader(reader);
        }
        private static double ReadHeader(StreamReader reader)
        {double velocity;return ReadHeader(reader,out velocity);}
        private static double ReadHeader(StreamReader reader,out double velocity)
        {double voltage;return ReadHeader(reader,out velocity,out voltage);}
        private static double ReadHeader(StreamReader reader,out double velocity,out double voltage)
        { FilterMetadata filter;return ReadHeader(reader,out velocity,out voltage,out filter); }
        private sealed class FilterMetadata { internal string Id=String.Empty;internal int Count;internal double Rate;internal bool ZeroVoltage; }
        private static double ReadHeader(StreamReader reader,out double velocity,out double voltage,out FilterMetadata filter)
        {ReviewAcquisitionQuality quality;return ReadHeader(reader,out velocity,out voltage,out filter,out quality);}
        private static double ReadHeader(StreamReader reader,out double velocity,out double voltage,out FilterMetadata filter,out ReviewAcquisitionQuality quality)
        {
            quality=ReviewAcquisitionQuality.Unrecorded;bool seenContext=false;
            filter=null;bool seenCycle=false,seenZero=false;string phaseHeader=null,chargeUnit=null;double captureStart=double.NaN;
            double rate=double.NaN;velocity=double.NaN;voltage=double.NaN;bool velocityConflict=false;
            for(int i=0;i<11;i++){
                string line=ReviewAcquisitionQuality.ReadHeaderLine(reader);if(line==null)throw new InvalidDataException("LOC header requires 11 bounded lines.");
                int split=line.IndexOf('\t');if(split<0)split=line.IndexOfAny(new[]{':','：','='});if(split<0)continue;
                string key=Regex.Replace(line.Substring(0,split).ToLowerInvariant(),@"[\s_\-]","");
                if(key=="acquisitioncontext") {
                    if(seenContext)throw new InvalidDataException("Duplicate LOC acquisition context.");seenContext=true;
                    quality=ReviewAcquisitionQuality.Parse(line.Substring(split+1));
                }
                if(key=="phase")phaseHeader=line.Substring(split+1).Trim();
                if(key=="chargeunit")chargeUnit=line.Substring(split+1).Trim();
                if(key=="capturestartseconds"&&!Double.TryParse(line.Substring(split+1),NumberStyles.Float,Inv,out captureStart))captureStart=Double.NaN;
                if(key=="hvxzerovoltagereference"){
                    if(seenZero||seenCycle)throw new InvalidDataException("Conflicting LOC reference metadata.");
                    seenZero=true;filter=ParseZeroVoltageMetadata(line.Substring(split+1),phaseHeader,chargeUnit,captureStart);
                }
                if(key=="hvxcyclereference"){
                    if(seenCycle||seenZero)throw new InvalidDataException("Duplicate or conflicting LOC cycle reference.");seenCycle=true;
                    var metadata=new JavaScriptSerializer{MaxJsonLength=65536,RecursionLimit=8}.Deserialize<Dictionary<string,object>>(line.Substring(split+1));
                    object id,scope;
                    if(metadata.TryGetValue("pdAcquisitionFilterId",out id)){
                        if(!Object.Equals(id,HVX.SignalProcessing.PdLinearHighPass.FilterId) ||
                            !metadata.TryGetValue("pdAcquisitionFilterScope",out scope) || !Object.Equals(scope,"full-segment-before-cycle-crop") ||
                            !(Object.Equals(metadata["schema"],"HVX.LocationCycles/1")||Object.Equals(metadata["schema"],"HVX.LocationCycles/2")))
                            throw new InvalidDataException("Unsupported LOC acquisition filter provenance.");
                        filter=new FilterMetadata {Id=(string)id,Count=Convert.ToInt32(metadata["sampleCount"],Inv),Rate=Convert.ToDouble(metadata["sampleRateHz"],Inv)};
                    }else if(metadata.ContainsKey("pdAcquisitionFilterScope"))throw new InvalidDataException("LOC acquisition filter scope has no filter identity.");
                }
                if(key=="voltage"||key=="voltage(kv)"||key=="targetpeakkv"){
                    string text=line.Substring(split+1).Trim();
                    Match match=Regex.Match(text,"^("+Number+")\\s*(?:kV)?$",RegexOptions.IgnoreCase);double value;
                    if(!match.Success||!Double.TryParse(match.Groups[1].Value,NumberStyles.Float,Inv,out value)||!Finite(value)||value<0)
                        throw new InvalidDataException("LOC voltage is invalid.");
                    if(Finite(voltage)&&voltage!=value)throw new InvalidDataException("LOC voltage headers conflict.");
                    voltage=value;
                }
                if(key.StartsWith("velocity",StringComparison.Ordinal)||Regex.IsMatch(key,@"^v(?:[:(（]|$)")){
                    double value=ParseVelocity(key,line.Substring(split+1));
                    if(Finite(value)){
                        if(value<=0||value>299.792458)velocityConflict=true;
                        else if(Finite(velocity)&&Math.Abs(value-velocity)>1e-9)velocityConflict=true;
                        else velocity=value;
                    }else velocityConflict=true;
                }
                if(key.Contains("deltax")||key.Contains("samplerate")||key.Contains("samplingrate")||key=="sr"){
                    double candidate=ParseRate(key,line.Substring(split+1));
                    if(!Finite(candidate)||candidate<=0)throw new InvalidDataException("LOC sampling rate is invalid.");
                    if(Finite(rate)&&Math.Abs(rate-candidate)>1e-9*Math.Max(rate,candidate))throw new InvalidDataException("LOC sampling-rate headers conflict.");
                    rate=candidate;
                }
            }
            if(velocityConflict)velocity=double.NaN;
            if(seenZero&&voltage!=0)throw new InvalidDataException("Zero-voltage analysis metadata requires a recorded voltage of 0 kV.");
            if(filter!=null&&(filter.Count<16||filter.Count>MaximumSamples||!Finite(filter.Rate)||filter.Rate<=0||Math.Abs(rate-filter.Rate)>1e-9*filter.Rate))
                throw new InvalidDataException("LOC acquisition filter metadata does not match its sample rate.");
            if(!Finite(rate)||rate<=0)throw new InvalidDataException("LOC sampling rate is missing.");return rate;
        }

        internal static double ParseVelocity(string key,string text)
        {
            Match match=Regex.Match(text,Number);double value;
            if(!match.Success||!double.TryParse(match.Value,NumberStyles.Float,Inv,out value)||!Finite(value))return double.NaN;
            string units=(key+" "+text.Substring(match.Index+match.Length)).ToLowerInvariant().Replace("μ","u").Replace("µ","u");
            Match unit=Regex.Match(units,@"m\s*/\s*(nsec|ns|usec|us|msec|ms|sec|s)\b");
            if(unit.Success){
                string u=unit.Groups[1].Value;
                if(u=="ns"||u=="nsec")value*=1000;
                else if(u=="ms"||u=="msec")value/=1000;
                else if(u=="s"||u=="sec")value/=1e6;
            }else if(units.Contains("/")||units.Contains("(" )||units.Contains("（"))return double.NaN;
            return value>0&&value<=299.792458?value:double.NaN;
        }

        internal static ReviewLocationFileData Read(string path,string projectPath)
        {return ReadCancellable(path,projectPath,CancellationToken.None);}
        internal static ReviewLocationFileData ReadCancellable(string path,string projectPath,CancellationToken cancellation)
        {return ReadCore(path,projectPath,true,cancellation);}

        // The frozen algorithm owns its file parser. Validate without retaining
        // a second full waveform before handing it the locked source file.
        internal static void ValidateSamples(string path,string projectPath)
        {ReadCore(path,projectPath,false,CancellationToken.None);}

        private static ReviewLocationFileData ReadCore(string path,string projectPath,bool retainSamples,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(String.IsNullOrWhiteSpace(path)) throw new ArgumentException("LOC path is required.");
            string full=Path.GetFullPath(path);
            if(!String.Equals(Path.GetExtension(full),".loc",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected a LOC file.");
            if(!String.IsNullOrWhiteSpace(projectPath))
            {
                string root=Path.GetFullPath(projectPath);
                if(File.Exists(root)) root=Path.GetDirectoryName(root);
                string prefix=root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("LOC file is outside the selected project.");
            }
            FileInfo info=new FileInfo(full);
            if(!info.Exists) throw new FileNotFoundException("LOC file was not found.",full);
            if(info.Length>MaximumFileBytes) throw new InvalidDataException("LOC exceeds the 512 MiB preview limit.");
            double rate=double.NaN,velocity=double.NaN,voltage=double.NaN;
            FilterMetadata filter;
            ReviewAcquisitionQuality quality;
            List<double> samples=retainSamples?new List<double>():null;int sampleCount=0;
            using(StreamReader reader=Open(full))
            {
                rate=ReadHeader(reader,out velocity,out voltage,out filter,out quality);
                cancellation.ThrowIfCancellationRequested();
                string sampleLine; int lineNumber=11;bool trailingBlank=false;
                while((sampleLine=reader.ReadLine())!=null)
                {
                    lineNumber++;
                    if((lineNumber&4095)==0)cancellation.ThrowIfCancellationRequested();
                    string value=sampleLine.Trim();
                    if(value.Length==0){trailingBlank=true;continue;}
                    if(trailingBlank)throw new InvalidDataException("LOC contains a missing interior sample before line "+lineNumber.ToString(Inv)+"; later sample times were not shifted.");
                    double stored;
                    // New LOCs contain one numeric value per line. Parse those
                    // directly without a temporary separator array per sample.
                    // Legacy multi-column lines retain the exact first-token rule.
                    if(!double.TryParse(value,NumberStyles.Float,Inv,out stored)) {
                        int end=value.IndexOfAny(SampleColumnSeparators);
                        if(end>=0)value=value.Substring(0,end);
                        if(end<0||!double.TryParse(value,NumberStyles.Float,Inv,out stored))
                            throw new InvalidDataException("Invalid LOC sample at line "+lineNumber.ToString(Inv)+".");
                    }
                    if(!Finite(stored)) throw new InvalidDataException("Invalid LOC sample at line "+lineNumber.ToString(Inv)+".");
                    if(sampleCount>=MaximumSamples) throw new InvalidDataException("LOC exceeds the 16 million sample preview limit.");
                    double charge=stored/100000.0;
                    if(Math.Abs(charge)>1e100) throw new InvalidDataException("LOC charge is outside the numeric display range.");
                    sampleCount++;if(retainSamples)samples.Add(charge);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            if(!Finite(rate)||rate<=0) throw new InvalidDataException("LOC sampling rate is missing.");
            if(sampleCount<16) throw new InvalidDataException("LOC contains fewer than 16 samples.");
            if(filter!=null&&filter.Count!=sampleCount)throw new InvalidDataException("LOC acquisition filter metadata does not match its sample count.");
            return new ReviewLocationFileData{DataPath=full,ChargeSamples=retainSamples?samples.ToArray():new double[0],SampleRateHz=rate,VelocityMPerUs=velocity,VoltageKv=voltage,AcquisitionQuality=quality,AcquisitionFilterId=filter==null?String.Empty:filter.Id,ZeroVoltageAnalysisOptIn=filter!=null&&filter.ZeroVoltage};
        }

        private static double ParseRate(string key,string value)
        {
            Match match=Regex.Match(value,Number); double number;
            if(!match.Success||!double.TryParse(match.Value,NumberStyles.Float,Inv,out number)||!Finite(number)||number<=0) return double.NaN;
            string unit=(key+" "+value).ToLowerInvariant();
            if(unit.Contains("ghz")) return number*1e9;
            if(unit.Contains("mhz")) return number*1e6;
            if(unit.Contains("khz")) return number*1e3;
            if(unit.Contains("hz")) return number;
            if(Regex.IsMatch(unit,@"\b(?:ns|nsec)\b")) return 1e9/number;
            if(Regex.IsMatch(unit,@"\b(?:us|usec|μs|µs)\b")) return 1e6/number;
            if(Regex.IsMatch(unit,@"\b(?:ms|msec)\b")) return 1e3/number;
            if(Regex.IsMatch(unit,@"\b(?:s|sec)\b")) return 1/number;
            return number<.001?1/number:number<1000?1e9/number:number;
        }
        private static StreamReader Open(string path)
        {
            HVX.Storage.PressureRecordPublication.RequireReadable(path);
            byte[] probe=new byte[8192]; int count;
            using(FileStream stream=File.OpenRead(path)) count=stream.Read(probe,0,probe.Length);
            Encoding encoding=new UTF8Encoding(false,true);
            try { encoding.GetDecoder().GetCharCount(probe,0,count,false); }
            catch(DecoderFallbackException) { encoding=Encoding.GetEncoding(936); }
            return new StreamReader(path,encoding,true);
        }
        private static bool Finite(double value) { return !double.IsNaN(value)&&!double.IsInfinity(value); }
    }
}
