using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Xml.Serialization;

// Standalone test: only this file is compiled. All implementation types are
// loaded from the supplied candidate. Never invokes Main, SDK, native backend,
// acquisition, connection, worker/process launch, or user settings persistence.
internal static class FinePressurePublishedSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly host, worker;
    static int checks;
    static Type W(string name) { return worker.GetType("Xiongxin.FactoryTest." + name, true); }
    static Type H(string name) { return host.GetType("Xiongxin.OWTS.Desktop." + name, true); }
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); ++checks; }
    static object New(Type type) { return Activator.CreateInstance(type, true); }
    static object Get(object value, string name)
    {
        var type = value.GetType(); var field = type.GetField(name, All);
        return field != null ? field.GetValue(value) : type.GetProperty(name, All).GetValue(value, null);
    }
    static void Set(object value, string name, object data)
    {
        var type = value.GetType(); var field = type.GetField(name, All);
        if (field != null) field.SetValue(value, data); else type.GetProperty(name, All).SetValue(value, data, null);
    }
    static object Invoke(Type type, object instance, string name, params object[] values)
    {
        try { return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == values.Length).Invoke(instance, values); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
    }
    static object Call(object instance, string name, params object[] values) { return Invoke(instance.GetType(), instance, name, values); }
    static object Static(string type, string name, params object[] values) { return Invoke(W(type), null, name, values); }
    static void Reject(Action action, string why)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, why);
    }
    static string Xml(object value)
    { using (var writer = new StringWriter(CultureInfo.InvariantCulture)) { new XmlSerializer(value.GetType()).Serialize(writer, value); return writer.ToString(); } }
    static object Read(string xml)
    { using (var reader = new StringReader(xml)) return new XmlSerializer(W("FactorySettings")).Deserialize(reader); }
    static string Hash(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    static void Close(double actual, double expected, string why)
    { Check(Math.Abs(actual - expected) <= 1e-12 * Math.Max(1, Math.Abs(expected)), why); }

    static void HostContract()
    {
        var type = H("AppSettings"); var settings = New(type);
        Check(type.GetProperty("EnableFinePressureRamp").PropertyType == typeof(bool), "host enable type");
        Check(type.GetProperty("FinePressureThresholdAutomatic").PropertyType == typeof(bool), "host automatic type");
        Check(type.GetProperty("FinePressureThresholdKv").PropertyType == typeof(double), "host threshold type/unit kV");
        Check(!(bool)Get(settings, "EnableFinePressureRamp") && (bool)Get(settings, "FinePressureThresholdAutomatic"), "new host default disabled/automatic");
        foreach (double maximum in new[] { 30d, 60d, 300d, .000001, 1000000d })
        {
            Set(settings, "MaximumVoltageKv", maximum);
            Close((double)Get(settings, "FinePressureThresholdKv"), maximum * .15, "automatic threshold follows actual maximum");
        }
        Set(settings, "MaximumVoltageKv", 30d); Set(settings, "FinePressureThresholdAutomatic", false);
        Set(settings, "FinePressureThresholdKv", 2.123456789d); Set(settings, "MaximumVoltageKv", 60d);
        Close((double)Get(settings, "FinePressureThresholdKv"), 2.123456789d, "manual threshold not overwritten");
        var clone = Call(settings, "Clone");
        Check(!(bool)Get(clone, "FinePressureThresholdAutomatic"), "clone preserves manual mode");
        Close((double)Get(clone, "FinePressureThresholdKv"), 2.123456789d, "clone preserves full precision");
        var status = H("DeviceSetupStatus");
        string before = (string)Invoke(status, null, "ParameterHash", settings);
        Set(settings, "EnableFinePressureRamp", true);
        Check(before != (string)Invoke(status, null, "ParameterHash", settings), "host fingerprint binds enable");
        before = (string)Invoke(status, null, "ParameterHash", settings); Set(settings, "FinePressureThresholdKv", 3d);
        Check(before != (string)Invoke(status, null, "ParameterHash", settings), "host fingerprint binds manual threshold");
        Set(settings, "FinePressureThresholdKv", 9d); before = (string)Invoke(status, null, "ParameterHash", settings);
        Set(settings, "FinePressureThresholdAutomatic", true);
        Check(before != (string)Invoke(status, null, "ParameterHash", settings), "host fingerprint binds auto flag even at equal value");
        var profileFields = (string[])H("DeviceParameterProfiles").GetField("Fields", All).GetValue(null);
        foreach (string field in new[] { "EnableFinePressureRamp", "FinePressureThresholdAutomatic", "FinePressureThresholdKv" })
            Check(profileFields.Contains(field), "pair profile persists " + field);
    }

    static object Configuration(string input, double maximum, double threshold, bool enabled)
    { return Read((string)Static("UnifiedPressureBridge", "CreateConfigurationWithFineRamp", input, maximum * 1000 / 5, maximum, 8, enabled, threshold)); }
    static void WorkerContract()
    {
        var input = New(worker.GetType("Xiongxin.ZeroBench.BenchSettings", true)); string inputXml = Xml(input);
        Set(input,"AoStepV",.0123d);Set(input,"LoopDelayMs",17);
        var rampConfiguration=Configuration(Xml(input),30,4.5,false);
        var rampInput=Get(rampConfiguration,"Input");
        Check((double)Get(rampInput,"AoStepV")==.0123d&&(int)Get(rampInput,"LoopDelayMs")==17,"normal ramp values survive runtime configuration");
        var rampRequest=Static("UnifiedPressureBridge","PrepareRequest",Xml(rampConfiguration),Xml(rampInput),1d);
        Check((double)Get(Get(rampRequest,"Input"),"AoStepV")==.0123d&&(int)Get(Get(rampRequest,"Input"),"LoopDelayMs")==17,"normal ramp values survive per-round preparation");
        Set(rampInput,"AoStepV",.0124d);
        Reject(()=>Static("UnifiedPressureBridge","PrepareRequest",Xml(rampRequest),Xml(rampInput),1d),"changed ramp step cannot reuse prepared session");
        Set(rampInput,"AoStepV",.0123d);Set(rampInput,"LoopDelayMs",18);
        Reject(()=>Static("UnifiedPressureBridge","PrepareRequest",Xml(rampRequest),Xml(rampInput),1d),"changed ramp interval cannot reuse prepared session");
        var method = W("UnifiedPressureBridge").GetMethod("CreateConfigurationWithFineRamp", All);
        Check(method != null && method.ReturnType == typeof(string) &&
            method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(double), typeof(double), typeof(int), typeof(bool), typeof(double) }), "new bridge exact six-argument contract");
        var s = Configuration(inputXml, 60, 9, true);
        Check((bool)Get(s, "EnableFinePressureRamp") && (double)Get(s, "FinePressureThresholdKv") == 9, "actual bridge carries fine fields");
        Check((double)Get(s, "MaximumKv") == 30 && (double)Get(s, "MaximumFeedbackKv") == 30 &&
            (double)Get(s, "MaximumAoV") == 5 && (double)Get(s, "TargetTolerancePercent") == 2, "hard protections remain unchanged");
        Set(s, "TargetKv", 15d); Close((double)Get(s, "RampUpperV"), 15300, "feedback remains +2 percent");
        Close((double)Get(s, "AoCommandCeilingV"), 1.3125, "AO envelope separate from pressure tolerance");
        var old = Read((string)Static("UnifiedPressureBridge", "CreateConfigurationWithReview", inputXml, 6000d, 30d, 8));
        Check(!(bool)Get(old, "EnableFinePressureRamp") && (double)Get(old, "FinePressureThresholdKv") == 4.5, "old adapter opt-in remains disabled with 15 percent value");
        foreach (double bad in new[] { 0d, -1d, Double.NaN, Double.PositiveInfinity, 31d })
            Reject(() => Configuration(inputXml, 30, bad, true), "bridge rejects bad threshold " + bad);
        var large = Configuration(inputXml, 300, 45, true);
        Check((double)Get(large, "FinePressureThresholdKv") == 45 && (double)Get(large, "MaximumKv") == 30, "selector not silently clamped and target limit not expanded");
        Set(large, "TargetKv", 31d);
        object pressure = Enum.Parse(W("FactoryRunKind"), "Pressure");
        Reject(() => Call(large, "ValidateBeforeOperatorConfirmation", pressure), "31 kV actual request still rejected");
        s = Configuration(inputXml, 30, 4.5, true);
        foreach (double target in new[] { 0d, 4.5 - 1e-8, 4.5, 4.5 + 1e-8 })
        { Set(s, "TargetKv", target); Check((bool)Get(s, "UseFinePressureRamp") == (target > 0 && target <= 4.5), "inclusive peak threshold boundary"); }
        Set(s, "TargetKv", 1d); Set(s, "EnableFinePressureRamp", false); Check(!(bool)Get(s, "UseFinePressureRamp"), "disabled fixed path");
        Set(s, "EnableFinePressureRamp", true); Set(s, "ZeroSynchronizedOptical", true); Check(!(bool)Get(s, "UseFinePressureRamp"), "zero diagnostic excluded");
        Set(s, "ZeroSynchronizedOptical", false); Set(s, "EndAfterPressureCapture", false); Check(!(bool)Get(s, "UseFinePressureRamp"), "legacy manual path excluded");
        Set(s, "EndAfterPressureCapture", true); Set(s, "RampTimeoutSeconds", 3d);
        Reject(() => Call(s, "ValidateBeforeOperatorConfirmation", pressure), "fine schedule cannot exhaust original deadline");
        Set(s, "RampTimeoutSeconds", 10d); Call(s, "ValidateBeforeOperatorConfirmation", pressure);
        var clone = Call(s, "Clone"); Check((bool)Get(clone, "EnableFinePressureRamp") && (double)Get(clone, "FinePressureThresholdKv") == 4.5, "worker XML clone retains fine configuration");
        string fp = (string)Static("FactoryBatchAuthorization", "Fingerprint", s); Set(s, "FinePressureThresholdKv", 9d);
        Check(fp != (string)Static("FactoryBatchAuthorization", "Fingerprint", s), "batch hash binds threshold");
        fp = (string)Static("FactoryBatchAuthorization", "Fingerprint", s); Set(s, "EnableFinePressureRamp", false);
        Check(fp != (string)Static("FactoryBatchAuthorization", "Fingerprint", s), "batch hash binds enable");
        StepContract(Configuration(inputXml, 30, 4.5, true));
    }

    static object Scheduler(object settings, object stats)
    { return Activator.CreateInstance(W("UnifiedFinePressureRamp"), All, null, new[] { settings, stats, (object)0L }, CultureInfo.InvariantCulture); }
    static void StepContract(object s)
    {
        Set(s, "TargetKv", 1d); Set(s, "FeedbackWindowMs", 10); Set(Get(s, "Input"), "LoopDelayMs", 1);
        var stats = New(W("FactoryRampStatistics")); var ramp = Scheduler(s, stats);
        Check((bool)Get(stats, "FineRampApplied") && (int)Get(stats, "FineRampPeriodMs") == 10, "actual scheduler uses slower feedback period");
        Close((double)Get(stats, "FineRampPlannedSeconds"), 3, "three second scheduling plan");
        Close((double)Call(ramp, "NextAo", 0d, 0d, 0L), 0, "no credit without elapsed time");
        double nominal = (double)Get(s, "NominalAoV");
        Close((double)Call(ramp, "NextAo", 0d, 0d, 10L), nominal * 10 / 3000, "one period increment matches independent equation");
        Close((double)Call(ramp, "NextAo", 0d, 0d, 1000L), nominal * 20 / 3000, "large delay credits at most two periods");
        Check((int)Call(ramp, "FinishCycle", 1000L) == 0 && (int)Get(stats, "FineRampSkippedPeriods") == 99, "elapsed periods skipped without catch-up burst");
        Check((int)Call(ramp, "FinishCycle", 1005L) == 5, "next deadline remains bounded");
        Close((double)Call(ramp, "NextAo", .1, 1000d, 1010L), .1, "target reached does not add output");
        Set(Get(s, "Input"), "AoStepV", .0001d); stats = New(W("FactoryRampStatistics")); ramp = Scheduler(s, stats);
        Close((double)Call(ramp, "NextAo", 0d, 0d, 1000L), .0001, "existing smaller AO step remains cap");
        Close((double)Call(ramp, "NextAo", .1, 999.94d, 1010L), .1 + .06 / 6000 * .25, "remaining actual feedback bounds step");
        Close((double)Get(stats, "FineRampMaximumIncrementV"), .0001, "actual maximum step recorded");
        Set(Get(s, "Input"), "LoopDelayMs", 25); stats = New(W("FactoryRampStatistics")); ramp = Scheduler(s, stats);
        Check((int)Get(stats, "FineRampPeriodMs") == 25 && (int)Call(ramp, "FinishCycle", 0L) == 25, "longer existing pacing retained");
        Reject(() => Call(s, "ValidateAoCommand", (double)Get(s, "AoCommandCeilingV") + .001), "original AO envelope remains enforced");
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) return 2;
            string root = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            string qa = Path.GetFullPath(args[1]);
            if (!qa.StartsWith(root + Path.DirectorySeparatorChar + "qa" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("QA path must belong to supplied candidate.");
            string hostPath = Path.Combine(root, "Xiongxin.OWTS.Desktop.exe");
            string workerPath = Path.Combine(root, "modules", "PressureRuntime", "Xiongxin.Pressure.Runtime.exe");
            string h = Hash(hostPath), w = Hash(workerPath);
            host = Assembly.LoadFrom(hostPath); worker = Assembly.LoadFrom(workerPath);
            Console.WriteLine("HOST_SHA256=" + h + "\nWORKER_SHA256=" + w);
            HostContract(); WorkerContract();
            Check(Hash(hostPath) == h && Hash(workerPath) == w, "candidate files unchanged during test");
            Console.WriteLine("FINE_PRESSURE_PUBLISHED_PASS=" + checks + "; ImplementationSourceCompiled=False; HardwareAccessed=False; ProductEntryExecuted=False; UserSettingsWritten=False; PhysicalAcceptance=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
