using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Profiling only: no UI construction, SDK or device calls, source project writes,
// or business algorithm edits. The RMS-only comparison calls the exact existing
// private PulseToolkit function, and checks both requested features bit-for-bit.
static class R58SingleLocFeatureProfileOffline
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=64*1024*1024,RecursionLimit=128};}
    static string Hash(string path){using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream));}
    static double[,] Matrix(string path)
    {using(var stream=File.OpenRead(path))using(var reader=new BinaryReader(stream)){if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="HVXMAT01")throw new Exception("matrix signature");int rows=reader.ReadInt32(),cols=reader.ReadInt32();if(rows<0||cols<0||(long)rows*cols>32000000||stream.Length!=16+8L*rows*cols)throw new Exception("matrix bounds");var data=new double[rows,cols];for(int r=0;r<rows;r++)for(int c=0;c<cols;c++)data[r,c]=reader.ReadDouble();return data;}}
    static string Decode(string text)
    {const string prefix="HVX.Diagnostics.Gzip.v1:";if(!text.StartsWith(prefix,StringComparison.Ordinal))return text;using(var input=new MemoryStream(Convert.FromBase64String(text.Substring(prefix.Length))))using(var zip=new GZipStream(input,CompressionMode.Decompress))using(var reader=new StreamReader(zip,Encoding.UTF8))return reader.ReadToEnd();}
    static string Identity(double phase,double id){return phase.ToString("R",CultureInfo.InvariantCulture)+":"+id.ToString("R",CultureInfo.InvariantCulture);}
    static double[,] Rows(double[,] source,int[] rows){var result=new double[rows.Length,source.GetLength(1)];for(int r=0;r<rows.Length;r++)for(int c=0;c<result.GetLength(1);c++)result[r,c]=source[rows[r],c];return result;}
    static double Milliseconds(Action action){var timer=Stopwatch.StartNew();action();timer.Stop();return timer.Elapsed.TotalMilliseconds;}
    static double Median(IEnumerable<double> values){var sorted=values.OrderBy(x=>x).ToArray();return sorted[sorted.Length/2];}
    static bool Same(double a,double b){return BitConverter.DoubleToInt64Bits(a)==BitConverter.DoubleToInt64Bits(b);}
    static double Field(object value,string name){return (double)value.GetType().GetProperty(name).GetValue(value,null);}
    static string N(double value){return value.ToString("F3",CultureInfo.InvariantCulture);}
    static int Main(string[] args)
    {
        try {
            if(args.Length!=3)throw new ArgumentException("module.dll original-project archive-id");
            string module=Path.GetFullPath(args[0]),project=Path.GetFullPath(args[1]),archive=Path.Combine(project,"Analysis report",args[2]);
            string[] names={"parameters.bin","pdmap.bin","session.json","analysis.json"};
            var before=names.ToDictionary(name=>name,name=>Hash(Path.Combine(archive,name)));
            var source=Matrix(Path.Combine(archive,"parameters.bin"));var waves=Matrix(Path.Combine(archive,"pdmap.bin"));
            var session=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(Path.Combine(archive,"session.json"),new UTF8Encoding(false,true)));
            var settings=((object[])session["settings"]).Cast<object[]>().ToDictionary(row=>(string)row[0],row=>(string)row[1]);
            var encoded=(Dictionary<string,object>)Json().DeserializeObject(settings["AlgorithmDiagnostics"]);
            var diagnostics=encoded.ToDictionary(pair=>pair.Key,pair=>Decode((string)pair.Value),StringComparer.OrdinalIgnoreCase);
            var statuses=new Dictionary<string,string>();var paths=new Dictionary<string,string>();
            foreach(object[] file in (object[])session["files"]) {string status,key=(string)file[1]+":"+(string)file[2];if(diagnostics.TryGetValue((string)file[0],out status)){statuses.Add(key,status);paths.Add(key,(string)file[0]);}}
            var assembly=Assembly.LoadFrom(module);var adapter=assembly.GetType("HVX.PdReview.ReviewEAnalysis",true);
            var helper=assembly.GetType("HVX.PdReview.ReviewPulseTimeFrequency",true);var engine=assembly.GetType("HVX.PD.PulseToolkit.PulseAnalysisEngine",true);
            var resultType=assembly.GetType("HVX.PD.PulseToolkit.PulseAnalysisResult",true);
            MethodInfo display=adapter.GetMethod("CreateTimeFrequencyDisplay",Flags),unpack=adapter.GetMethod("UnpackLedger",Flags),measure=helper.GetMethod("TryMeasure",Flags),analyze=engine.GetMethod("Analyze",Flags),rms=engine.GetMethod("TimeFrequency",Flags);
            var groups=Enumerable.Range(0,source.GetLength(0)).GroupBy(row=>Identity(source[row,0],source[row,1])).OrderBy(group=>group.Key).ToArray();
            int measured=0,missing=0,duplicateWindows=0,equalFeatures=0;double totalFull=0,totalRmsOnly=0;var allGroupMs=new List<double>();
            foreach(var group in groups) {
                string status;if(!statuses.TryGetValue(group.Key,out status))continue;
                int[] rows=group.ToArray();var parameters=Rows(source,rows);var packets=Rows(waves,rows);
                var oneStatus=new Dictionary<string,string>{{group.Key,status}};double[,] projected=null;
                double cold=Milliseconds(()=>projected=(double[,])display.Invoke(null,new object[]{parameters,packets,oneStatus}));
                var repeated=new List<double>();for(int iteration=0;iteration<5;iteration++)repeated.Add(Milliseconds(()=>display.Invoke(null,new object[]{parameters,packets,oneStatus})));
                double median=Median(repeated);allGroupMs.Add(median);
                var parsed=(Dictionary<string,object>)Json().DeserializeObject(status);
                var ledger=(Dictionary<string,object>)unpack.Invoke(null,new object[]{parsed["upstream_evidence"]});
                double baseline=Convert.ToDouble(ledger["baseline_pc"],CultureInfo.InvariantCulture);
                var events=((object[])ledger["events"]).Cast<Dictionary<string,object>>().ToArray();
                var supports=new HashSet<string>();var paddedPackets=new List<double[]>();var sampleRates=new List<double>();var packetCounts=new List<int>();
                int localMissing=0,localDuplicates=0;
                for(int row=0;row<rows.Length;row++) {
                    if(Double.IsNaN(projected[row,23])||Double.IsNaN(projected[row,24])){missing++;localMissing++;continue;}
                    int incident=(int)parameters[row,10],reflection=(int)parameters[row,11],sampleFirst=(int)parameters[row,31];
                    var pulse=events.First(e=>Convert.ToInt32(e["incident"],CultureInfo.InvariantCulture)==incident&&Convert.ToInt32(e["reflection"],CultureInfo.InvariantCulture)==reflection);
                    var window=(object[])pulse["incident_window"];int first=Convert.ToInt32(window[0]),last=Convert.ToInt32(window[1]),count=last-first+1;
                    string support=incident+":"+first+":"+last;
                    if(!supports.Add(support)){duplicateWindows++;localDuplicates++;}
                    var rawPacket=new double[count];for(int i=0;i<count;i++)rawPacket[i]=packets[row,first-sampleFirst+i];
                    object[] measureArgs={rawPacket,first,incident,parameters[row,7],baseline,pulse,Double.NaN,Double.NaN};
                    if(!(bool)measure.Invoke(null,measureArgs)||!Same((double)measureArgs[6],projected[row,23])||!Same((double)measureArgs[7],projected[row,24]))throw new Exception("feature replay mismatch");
                    int guard=Math.Max(16,count);var padded=new double[count+2*guard];for(int i=0;i<count;i++)padded[guard+i]=rawPacket[i]-baseline;
                    paddedPackets.Add(padded);sampleRates.Add(parameters[row,7]);packetCounts.Add(count);measured++;
                    object full=analyze.Invoke(Activator.CreateInstance(engine),new object[]{padded,parameters[row,7]});
                    object narrow=Activator.CreateInstance(resultType);rms.Invoke(null,new object[]{narrow,padded,parameters[row,7]});
                    if(!Same(Field(full,"EquivalentTimeNs"),Field(narrow,"EquivalentTimeNs"))||!Same(Field(full,"EquivalentBandwidthHz"),Field(narrow,"EquivalentBandwidthHz")))throw new Exception("RMS-only exact feature mismatch");
                    equalFeatures+=2;
                }
                var fullTimes=new List<double>();var rmsTimes=new List<double>();
                for(int iteration=0;iteration<5;iteration++) {
                    fullTimes.Add(Milliseconds(()=>{for(int i=0;i<paddedPackets.Count;i++)analyze.Invoke(Activator.CreateInstance(engine),new object[]{paddedPackets[i],sampleRates[i]});}));
                    rmsTimes.Add(Milliseconds(()=>{for(int i=0;i<paddedPackets.Count;i++)rms.Invoke(null,new object[]{Activator.CreateInstance(resultType),paddedPackets[i],sampleRates[i]});}));
                }
                double fullMedian=Median(fullTimes),rmsMedian=Median(rmsTimes);totalFull+=fullMedian;totalRmsOnly+=rmsMedian;
                parsed["time_frequency_contract"]="PulseToolkit-2.0.0-incident-energy-rms-ns-hz-v1";
                var currentStatus=new Dictionary<string,string>{{group.Key,Json().Serialize(parsed)}};var currentTimes=new List<double>();
                for(int iteration=0;iteration<5;iteration++)currentTimes.Add(Milliseconds(()=>display.Invoke(null,new object[]{projected,packets,currentStatus})));
                Console.WriteLine("LOC="+Path.GetFileName(paths[group.Key])+";identity="+group.Key+";rows="+rows.Length+";measured="+paddedPackets.Count+";missing="+localMissing+";duplicateIncidentWindows="+localDuplicates+";packetSamples="+(packetCounts.Count==0?"none":packetCounts.Min()+".."+packetCounts.Max())+";legacyDisplayColdMs="+N(cold)+";legacyDisplayMedianMs="+N(median)+";fullAnalyzeMedianMs="+N(fullMedian)+";exactRmsOnlyMedianMs="+N(rmsMedian)+";newContractDisplayMedianMs="+N(Median(currentTimes)));
            }
            foreach(string name in names)if(Hash(Path.Combine(archive,name))!=before[name])throw new Exception("original project changed");
            Console.WriteLine("PROFILE_PASS;groups="+allGroupMs.Count+";measuredRows="+measured+";missingRows="+missing+";duplicateIncidentWindows="+duplicateWindows+";bitExactFeatureComparisons="+equalFeatures+";singleLocLegacyMedianMs="+N(Median(allGroupMs))+";singleLocLegacyMaxMs="+N(allGroupMs.Max())+";sumFullAnalyzeMs="+N(totalFull)+";sumExactRmsOnlyMs="+N(totalRmsOnly)+";moduleSha256="+Hash(module).Replace("-","")+";UIConstructed=false;SDKCalled=false;HardwareUsed=false;SourceProjectWritten=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
