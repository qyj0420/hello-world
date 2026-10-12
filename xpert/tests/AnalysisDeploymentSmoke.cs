using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Runs the deployed review adapter and its actual Task17/Task15 payload, not a
// recompiled adapter, mock engine or developer source-directory fallback.
internal static class AnalysisDeploymentSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    const string AlgorithmName = "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll";
    static int checks;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new InvalidOperationException(text); }
    static object Call(Type type, string name, params object[] values)
    { return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(null, values); }
    static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static void Set(object value, string name, object data) { value.GetType().GetField(name, All).SetValue(value, data); }
    static string Hash(string path)
    { using (var input = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""); }
    static double[] Wave(int incident, int reflection, double amplitude)
    {
        var data = new double[4096];
        for (int i = 0; i < data.Length; i++)
        {
            double x = (i - incident) / 10d, y = (i - reflection) / 10d;
            data[i] = amplitude * (Math.Exp(-x * x / 2) - .6 * Math.Exp(-(x - 3) * (x - 3) / 2)) +
                amplitude * .6 * (Math.Exp(-y * y / 2) - .6 * Math.Exp(-(y - 3) * (y - 3) / 2)) +
                .00001 * amplitude * Math.Sin(i * .73);
        }
        return data;
    }
    static void WriteWave(string path, string[] header, double[] values)
    {
        File.WriteAllText(path, string.Join("\r\n", header) + "\r\n" +
            string.Join("\r\n", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), new UTF8Encoding(true));
    }
    static string Fixture(string evidence)
    {
        string root = Path.Combine(evidence, "synthetic-project"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ITEM.in"), "[ITEM]\r\nCable length = 170\r\n", new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "CABLE.in"), "0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n", new UTF8Encoding(true));
        WriteWave(Path.Combine(root, "ALL_1nC.cal"), new[] {
            "Cable length\t170", "Cal.value\t1000", "Velocity (m/us)\t170", "NI_Calib_factor\t1",
            "Start_point (s)\t0.000004", "Final_point (s)\t0.000006", "SR\t125000000", "Calib_Attenuation\t4.436974992",
            "Phase\tALL", "Units\tV", "Reserved\t0", "Reserved2\t0", "Data"
        }, Wave(500, 750, .01));
        // LOC engineering charge is stored multiplied by 100000. The UI adapter
        // reads this actual file, including all eleven header lines.
        WriteWave(Path.Combine(root, "A_12.0kV_synthetic.loc"), new[] {
            "Cable length\t170", "Velocity (m/us)\t170", "Delta x\t8E-9", "Phase\t0", "Voltage\t12.0",
            "Reserved1\t0", "Reserved2\t0", "Reserved3\t0", "Reserved4\t0", "Reserved5\t0", "Data"
        }, Wave(500, 700, 1000).Select(v => v * 100000).ToArray());
        return root;
    }
    public sealed class MissingPayloadProbe : MarshalByRefObject
    {
        public string Run(string control, string project)
        {
            Type engine = Assembly.LoadFrom(control).GetType("HVX.PdReview.ReviewAnalysisEngine", true);
            // Exercise the enabled A route. Index 6 is an intentionally
            // retired method and must fail before loading a runtime.
            object[] inputs = { project, 5, null };
            bool ok = (bool)engine.GetMethod("CheckEnvironment", All).Invoke(null, inputs);
            return (ok ? "UNEXPECTED_SUCCESS\n" : "EXPECTED_FAILURE\n") + (string)inputs[2];
        }
        public override object InitializeLifetimeService() { return null; }
        public string EmbeddedConclusion(string control)
        {
            var assembly=Assembly.LoadFrom(control);var bridge=assembly.GetType("HVX.UnifiedSpectrum.SpectrumTask15Bridge",true);
            var engine=(Type)Call(bridge,"ResolveType",Path.Combine(Path.GetDirectoryName(control),"missing-Task15.dll"));
            if(engine.Assembly!=assembly)throw new Exception("Conclusion engine is not source-embedded in the deployed control.");
            var p=new double[6,36];for(int r=0;r<6;r++){p[r,0]=1;p[r,1]=r+1;p[r,2]=12;p[r,3]=30;p[r,9]=r;p[r,13]=60+r*.01;p[r,15]=100;p[r,33]=.95;p[r,35]=1;}
            var result=Call(bridge,"AnalyzeResult",engine,p,new[]{new[]{0d,170d},new double[0],new double[0]},0d,2d,0,0,false,true);
            return (string)Field(result,"Api")+"\n"+(string)Field(result,"Summary");
        }
    }
    static void MissingDependency(string control, string project, string evidence)
    {
        string folder = Path.Combine(evidence, "deliberately-incomplete-payload"); Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(control)); File.Copy(control, target);
        AppDomain domain = AppDomain.CreateDomain("missing-task17-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = (MissingPayloadProbe)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(MissingPayloadProbe).FullName);
            string result = probe.Run(target, project);
            File.WriteAllText(Path.Combine(evidence, "missing-runtime-diagnostic.txt"), result, Encoding.UTF8);
            Check(result.StartsWith("EXPECTED_FAILURE", StringComparison.Ordinal), "Incomplete distribution cannot pass via an already-loaded or developer Task17 DLL");
            Check(result.Contains("FileNotFoundException") && result.Contains(Path.Combine(folder, AlgorithmName)), "Missing runtime diagnostic identifies exact absent payload path");
            Check(Directory.GetFiles(folder,"*.dll").Length==1,"isolated directory has only control DLL, no Task15 external runtime");
            string conclusion=probe.EmbeddedConclusion(target);
            Check(conclusion.Contains("HVX-EVIDENCE-15-V3")&&conclusion.Contains("source-embedded"),"real conclusion succeeds with no Task15 DLL and unknown U0");
        }
        finally { AppDomain.Unload(domain); }
    }
    static void Conclusion(Assembly review, double[,] matrix)
    {
        Type bridge = review.GetType("HVX.UnifiedSpectrum.SpectrumTask15Bridge", true);
        string runtime = (string)Call(bridge, "ResolvePath", "");
        Check(string.Equals(Path.GetDirectoryName(runtime), Path.GetDirectoryName(review.Location), StringComparison.OrdinalIgnoreCase), "Task15 resolves beside deployed analysis control");
        Type engine = (Type)Call(bridge, "ResolveType", runtime);
        Check(engine.Assembly==review&&runtime==review.Location,"Task15 engine is compiled into the actual deployed review assembly");
        // Use a separate clearly synthetic conclusion fixture. It tests the
        // runtime API contract only, not automatic acceptance of analysis rows.
        var input = (double[,])matrix.Clone();
        for (int row = 0; row < input.GetLength(0); row++)
        {
            input[row, 0] = 1; input[row, 2] = 12; input[row, 13] = 60;
            input[row, 22] = double.NaN; input[row, 33] = .95; input[row, 35] = 1;
        }
        object result = Call(bridge, "AnalyzeResult", engine, input, new[] { new[] { 0d, 170d }, new[] { 0d, 170d }, new[] { 0d, 170d } }, 8.7d, 2d, 0, 0, false, true);
        Check(((string)Field(result,"Api")).Contains("HVX-EVIDENCE-15-V3")&&((string)Field(result,"Api")).StartsWith("hvx-conclusion-dotnet-3.",StringComparison.Ordinal), "Deployed Task15 identifies the new embedded source version");
        Check(!string.IsNullOrWhiteSpace((string)Field(result, "Summary")), "Deployed Task15 real API generates a summary");
        Check((int)Field(result, "ValidPulses") > 0, "Synthetic conclusion fixture retains valid pulses");
        Console.WriteLine("TASK15_RUNTIME=" + runtime + "; SHA256=" + Hash(runtime));
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass actual distribution root and isolated evidence root.");
            Check(IntPtr.Size == 8, "Integration test runs x64 as the application does");
            string distribution = Path.GetFullPath(args[0]), qa = Path.GetFullPath(args[1]);
            if (qa.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0) throw new ArgumentException("Evidence must be inside a build directory.");
            string evidence = Path.Combine(qa, "analysis-deployment-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(evidence);
            string control = Path.Combine(distribution, "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll");
            string algorithm = Path.Combine(Path.GetDirectoryName(control), AlgorithmName);
            string pressure = Path.Combine(distribution, "modules", "PressureTest", AlgorithmName);
            Check(File.Exists(control) && File.Exists(algorithm) && File.Exists(pressure), "Both consumers have real deployed analysis payload");
            Check(Hash(algorithm) == Hash(pressure), "Analysis and pressure use byte-identical Task17 runtime");
            string project = Fixture(evidence);
            var originals = Directory.GetFiles(project).ToDictionary(path => path, Hash);
            MissingDependency(control, project, evidence);
            Assembly review = Assembly.LoadFrom(control);
            Type engine = review.GetType("HVX.PdReview.ReviewAnalysisEngine", true), requestType = review.GetType("HVX.PdReview.ReviewAnalysisRequest", true);
            Check(string.Equals((string)engine.GetProperty("ExpectedAlgorithmPath", All).GetValue(null, null), algorithm, StringComparison.OrdinalIgnoreCase), "Review adapter expects the actual staged Task17 path");
            double[,] conclusionInput = null;
            foreach (int method in new[] { -1, 0, 1, 2, 3, 4, 6, 8, 99 })
            {
                object[] environment = { project, method, null };
                Check(!(bool)Call(engine, "CheckEnvironment", environment) &&
                    ((string)environment[2]).Contains("select A or B"), "Retired or invalid method is rejected before runtime use: " + method);
                object request = Activator.CreateInstance(requestType, true);
                Set(request, "ProjectPath", project); Set(request, "LocationFilePath", Path.Combine(project, "A_12.0kV_synthetic.loc"));
                Set(request, "AlgorithmIndex", method); Set(request, "LocationDataId", 101L);
                object rejected = Call(engine, "Analyze", request);
                Check(!(bool)Field(rejected, "Success") && ((double[,])Field(rejected, "Parameters")).GetLength(0) == 0 &&
                    ((string)Field(rejected, "Status")).Contains("select A or B"), "Retired method cannot silently fall back to A: " + method);
            }
            Check(FileVersionInfo.GetVersionInfo(algorithm).FileVersion=="3.0.21.0","Exact frozen A/B candidate file version is deployed");
            foreach (int method in new[] { 5, 7 })
            {
                object[] environment = { project, method, null };
                Check((bool)Call(engine, "CheckEnvironment", environment), "Deployed environment method " + method + ": " + environment[2]);
                foreach (bool firstArrival in new[] { false, true })
                {
                    object request = Activator.CreateInstance(requestType, true);
                    Set(request, "ProjectPath", project); Set(request, "LocationFilePath", Path.Combine(project, "A_12.0kV_synthetic.loc"));
                    Set(request, "AlgorithmIndex", method); Set(request, "LocationDataId", 101L); Set(request, "MaxPulses", 10);
                    Set(request, "UseFirstArrivalTiming", firstArrival); Set(request, "OscillationFrequencyHz", 50d);
                    Set(request, "ManualThresholdEnabled", true); Set(request, "ManualThreshold", 100d);
                    object result = Call(engine, "Analyze", request); string status = (string)Field(result, "Status");
                    File.WriteAllText(Path.Combine(evidence, "method-" + method + "-" + (firstArrival ? "first-arrival" : "peak") + ".json"), status, Encoding.UTF8);
                    Check((bool)Field(result, "Success"), "Actual deployed adapter method " + method + ", first=" + firstArrival + " completes: " + status);
                    if(method==7)Check(status.Contains("source_identity_requires_review")&&status.Contains("engineering_candidate_not_manual_or_physical_confirmation"),"B retains source-identity and acceptance semantics in status");
                    var parameters = (double[,])Field(result, "Parameters");
                    Check(parameters.GetLength(0) > 0 && parameters.GetLength(0) <= 10 && parameters.GetLength(1) == 36, "Actual method publishes nonempty bounded 36-column pulse matrix");
                    Check(((double[,])Field(result, "Raw")).GetLength(0) == parameters.GetLength(0), "Actual waveform and parameter row counts agree");
                    Check(Enumerable.Range(0, parameters.GetLength(0)).All(r => parameters[r, 1] == 101), "Real algorithm retains request record identity");
                    if (conclusionInput == null) conclusionInput = parameters;
                    Console.WriteLine("METHOD=" + method + "; FIRST_ARRIVAL=" + firstArrival + "; ROWS=" + parameters.GetLength(0));
                }
            }
            Conclusion(review, conclusionInput);
            Check(Directory.GetFiles(project).OrderBy(p => p).SequenceEqual(originals.Keys.OrderBy(p => p)), "Analysis creates no project-side files");
            foreach (var file in originals) Check(Hash(file.Key) == file.Value, "Analysis leaves original synthetic input bytes unchanged: " + Path.GetFileName(file.Key));
            Console.WriteLine("TASK17_RUNTIME=" + algorithm + "; SHA256=" + Hash(algorithm));
            Console.WriteLine("ANALYSIS_DEPLOYMENT_PASS=" + checks); Console.WriteLine("EVIDENCE=" + evidence); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
