using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Compare the production DLL with this mainline's physical-only compilation.
// No historical upstream physical source or learned weights are substituted.
internal static class Task17NeuralIsolationSmoke
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32000000 };
    static int checks;
    static readonly List<object> cases = new List<object>();
    static void Check(bool good, string detail) { checks++; if (!good) throw new InvalidOperationException(detail); }
    static string Hash(byte[] value)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", ""); }
    static string FileHash(string path) { return Hash(File.ReadAllBytes(path)); }
    static string ComparableStatus(string status)
    {
        var data=Json.Deserialize<Dictionary<string,object>>(status);
        object currentObject;
        if(data.TryGetValue("current_analysis",out currentObject))
        {
            var current=currentObject as Dictionary<string,object>; object elapsed;
            if(current!=null&&current.TryGetValue("raw_half_height_diagnostic_elapsed_ms",out elapsed))
            {
                double duration=Convert.ToDouble(elapsed,CultureInfo.InvariantCulture);
                Check(!Double.IsNaN(duration)&&!Double.IsInfinity(duration)&&duration>=0,"wall-clock diagnostic remains finite and nonnegative");
                // The sole excluded field is an observed stopwatch duration, not
                // a candidate feature, score, classification or physical result.
                current.Remove("raw_half_height_diagnostic_elapsed_ms");
            }
        }
        return Json.Serialize(data);
    }
    static string MatrixHash(double[,] matrix)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            if (matrix == null) writer.Write(-1);
            else
            {
                writer.Write(matrix.GetLength(0)); writer.Write(matrix.GetLength(1));
                // Compare every finite, non-finite and signed-zero bit, not rounded JSON.
                foreach (double value in matrix) writer.Write(BitConverter.DoubleToInt64Bits(value));
            }
            writer.Flush(); return Hash(stream.ToArray());
        }
    }
    static string VectorHash(double[] vector)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(vector.Length);
            foreach (double value in vector) writer.Write(BitConverter.DoubleToInt64Bits(value));
            writer.Flush(); return Hash(stream.ToArray());
        }
    }
    static double[] Wave(int a = 500, int b = 700, double amplitude = 1000)
    {
        var samples = new double[4096];
        for (int i = 0; i < samples.Length; i++)
        {
            double x = (i - a) / 10d, y = (i - b) / 10d;
            samples[i] = amplitude * (Math.Exp(-x*x/2) - .6*Math.Exp(-(x-3)*(x-3)/2)) +
                amplitude * .6 * (Math.Exp(-y*y/2) - .6*Math.Exp(-(y-3)*(y-3)/2)) + .00001 * amplitude * Math.Sin(i*.73);
        }
        return samples;
    }
    static string CycleHeader(bool positive)
    {
        return "HVX_CycleReference\t{\"schema\":\"HVX.LocationCycles/1\",\"sampleRateHz\":125000000," +
            "\"sampleCount\":4096,\"captureStartSeconds\":0.125,\"firstCrossingPositive\":" + (positive ? "true" : "false") +
            ",\"crossingSeconds\":[" + String.Join(",", new[] {0d,600/125e6,1800/125e6,2800/125e6,4095/125e6}.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]}";
    }
    static string Fixture(string qa)
    {
        string root = Path.Combine(qa, "fixture"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        string[] header = {"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1",
            "Start_point (s)\t0.000004","Final_point (s)\t0.000006","SR\t125000000","Calib_Attenuation\t4.436974992",
            "Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"};
        File.WriteAllLines(Path.Combine(root,"ALL_1nC.CAL"), header.Concat(Wave(500,750,.01).Select(v => v.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));
        return root;
    }
    static string WriteLoc(string root, string cycle)
    {
        string loc = Path.Combine(root,"A_12.0kV_synthetic.loc");
        string[] header = {"Cable-length\t170","Velocity\t170","Delta x\t125000000","Phase\t18","Reserved\t0","Reserved2\t0",
            "Reserved3\t0","Reserved4\t0","Reserved5\t0",cycle??"Reserved6\t0","Data"};
        File.WriteAllLines(loc,header.Concat(Wave().Select(v => (v*100000).ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));
        return loc;
    }
    sealed class Snapshot
    {
        internal bool Ok;
        internal string Signature, Status;
        internal Dictionary<string,string> Hashes = new Dictionary<string,string>();
        internal Dictionary<string,int[]> Shapes = new Dictionary<string,int[]>();
        internal double[,] Rows;
    }
    static Snapshot Analyze(Assembly assembly, string root, int algorithm, bool first, bool memory)
    {
        string kind = algorithm == 6 ? "Seventh" : algorithm == 5 ? "Sixth" : "Project";
        Type type = assembly.GetType("HVX.Task17.Location." + kind + "LocationAnalyzer",true);
        object target = Activator.CreateInstance(type);
        MethodInfo entry = type.GetMethod("Analyze" + kind + "LocationIncremental");
        var raw = Wave(); string originalRaw = VectorHash(raw);
        var values = new Dictionary<string,object> {
            {"projectPath",root},{"locationFilePath",Path.Combine(root,"A_12.0kV_synthetic.loc")},{"locSampleHz",125e6},{"locData",raw},{"useLocData",memory},
            {"previousParameterMatrix",new double[0,36]},{"previousPulseEnvelopeMatrix",new double[0,0]},{"previousFilteredPulseEnvelopeMatrix",new double[0,0]},
            {"previousEquivalentPulseWaveformMatrix",new double[0,0]},{"locationDataId",1L},{"automaticAnalysis",true},{"locationAlgorithm",algorithm},
            {"useFirstArrivalTiming",first},{"useAttenuationReference",true},{"usePulseWidthComparison",true},
            {"thresholdType",-1},{"manualThresholdAmplitude",100d},{"automaticThresholdMultiplier",3d},{"multipleReflectionSuppressionCount",1},
            {"maximumDischargeCount",10},{"incidentPreExtensionPoints",200},{"roundTripPostExtensionPoints",500},{"initialPhaseDegrees",13d},
            {"oscillationFrequencyHz",50d},{"applyLinearFilter",false},{"linearFilterCutoffsHz",new[]{10000d,3000000d}},
            {"useEquivalentPulseWindow",false},{"equivalentPulseWindowNs",new double[0]},{"minimumCorrelationPercent",70d},
            {"attenuationErrorPercent",15d},{"pulseWidthErrorPercent",-1d},{"scoreWeights",new double[0]}};
        var parameters = entry.GetParameters(); object[] args = parameters.Select(p => p.IsOut ? null : values[p.Name]).ToArray();
        var snapshot = new Snapshot { Ok = (bool)entry.Invoke(target,args), Signature = String.Join("|", parameters.Select(p => p.Name+":"+p.ParameterType.FullName+":"+p.IsOut)) };
        Check(VectorHash(raw) == originalRaw,"physical analysis must not mutate caller waveform");
        for (int i=0;i<parameters.Length;i++) if (parameters[i].IsOut)
        {
            var matrix = args[i] as double[,];
            if (matrix != null)
            { snapshot.Hashes[parameters[i].Name] = MatrixHash(matrix); snapshot.Shapes[parameters[i].Name] = new[]{matrix.GetLength(0),matrix.GetLength(1)}; }
            else if (parameters[i].Name == "statusMessage") snapshot.Status = (string)args[i];
            else throw new InvalidOperationException("Unexpected public output " + parameters[i].Name);
        }
        snapshot.Rows = (double[,])args[Array.FindIndex(parameters,p => p.Name=="currentParameterMatrix2DOutput")];
        return snapshot;
    }
    static string Dll(string input)
    {
        if (File.Exists(input)) return Path.GetFullPath(input);
        string flat = Path.Combine(input,"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
        return Path.GetFullPath(File.Exists(flat) ? flat : Path.Combine(input,"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
    }
    static int Main(string[] args)
    {
        try
        {
            if(args.Length!=3) throw new ArgumentException("Candidate, QA directory and independently compiled baseline required.");
            string candidatePath=Dll(args[0]), baselinePath=Dll(args[2]), qa=Path.GetFullPath(args[1]);
            Check(qa.IndexOf("\\build\\",StringComparison.OrdinalIgnoreCase)>=0,"QA root must remain under build");
            Directory.CreateDirectory(qa);
            Assembly candidate=Assembly.LoadFile(candidatePath), baseline=Assembly.LoadFile(baselinePath);
            Check(candidate.GetType("HVX.Task17.Location.ReflectionLearningEngine",false)!=null,"actual candidate contains learning API");
            Check(baseline.GetType("HVX.Task17.Location.ReflectionLearningEngine",false)==null,"baseline excludes all learning engines");
            foreach(string api in new[]{"TrainNeural","InspectNeuralModel","SuggestNeural"})
                Check(candidate.GetType("HVX.Task17.Location.ReflectionLearningEngine").GetMethod(api)!=null,"actual neural API "+api);
            string root=Fixture(qa);
            foreach(string scenario in new[]{"memory_legacy","file_variable_positive","file_variable_negative"})
            {
                bool memory=scenario=="memory_legacy";
                string loc=WriteLoc(root,memory?null:CycleHeader(scenario.EndsWith("positive",StringComparison.Ordinal)));
                string locHash=FileHash(loc), calHash=FileHash(Path.Combine(root,"ALL_1nC.CAL"));
                for(int method=0;method<7;method++) foreach(bool first in new[]{false,true})
                {
                    string name=scenario+"/method="+method+"/first="+first;
                    Snapshot before=Analyze(baseline,root,method,first,memory), after=Analyze(candidate,root,method,first,memory);
                    Check(before.Ok&&after.Ok,name+" both analyses succeed: "+before.Status+" / "+after.Status);
                    Check(before.Signature==after.Signature,name+" public argument names/types/out contract unchanged");
                    Check(after.Rows.GetLength(0)>0&&after.Rows.GetLength(1)==36,name+" retains nonempty 36-column physical output");
                    string beforeComparable=ComparableStatus(before.Status),afterComparable=ComparableStatus(after.Status);
                    if(beforeComparable!=afterComparable) File.WriteAllText(Path.Combine(qa,"diagnostic-mismatch.json"),Json.Serialize(new {name,before=before.Status,after=after.Status,beforeMatrices=before.Hashes,afterMatrices=after.Hashes}));
                    Check(beforeComparable==afterComparable,name+" complete physical diagnostics identical except one observed stopwatch duration");
                    Check(before.Hashes.Count==8&&after.Hashes.Count==8,name+" all eight public output matrices compared");
                    foreach(var item in before.Hashes) Check(after.Hashes.ContainsKey(item.Key)&&after.Hashes[item.Key]==item.Value,name+" exact numeric bits "+item.Key);
                    Check(memory?after.Status.Contains("legacy_uniform_frequency"):after.Status.Contains("recorded_alternating_zero_crossings"),name+" period-adaptive provenance preserved");
                    Check(FileHash(loc)==locHash&&FileHash(Path.Combine(root,"ALL_1nC.CAL"))==calHash,name+" original LOC/CAL unchanged");
                    cases.Add(new {name,passed=true,matrix_sha256=after.Hashes,matrix_shapes=after.Shapes,
                        comparable_status_sha256=Hash(Encoding.UTF8.GetBytes(afterComparable)),before_raw_status_sha256=Hash(Encoding.UTF8.GetBytes(before.Status)),after_raw_status_sha256=Hash(Encoding.UTF8.GetBytes(after.Status))});
                }
            }
            string evidence=Path.Combine(qa,"physical-neural-isolation.json");
            File.WriteAllText(evidence,Json.Serialize(new {ok=true,checks,cases,synthetic_only=true,physical_hardware_executed=false,
                baseline_dll=baselinePath,baseline_sha256=FileHash(baselinePath),candidate_dll=candidatePath,candidate_sha256=FileHash(candidatePath),
                original_source_replaced=false,learned_weights_deployed=false,
                excluded_diagnostic_field="current_analysis.raw_half_height_diagnostic_elapsed_ms (finite nonnegative wall-clock stopwatch only)"}));
            Console.WriteLine("PASS "+checks+" Task17NeuralIsolationSmoke; CASES="+cases.Count+"; EVIDENCE="+evidence); return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
