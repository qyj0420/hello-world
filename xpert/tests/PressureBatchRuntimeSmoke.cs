using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Text;
using System.Windows.Forms;

// Independent offline audit. This entry never invokes the runtime's Main,
// CaptureAsync, CaptureBatchAsync, MonitorAsync, Monitor, StartWorker or SDK.
internal static class PressureBatchRuntimeSmoke
{
    static Assembly runtime;
    static string fixtureRoot;
    static int checks;
    static int workerCalls;
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type T(string name) { return runtime.GetType("Xiongxin.FactoryTest." + name, true); }
    static object Invoke(string type, string name, params object[] args)
    {
        try { return T(type).GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args); }
        catch (TargetInvocationException e)
        {
            Console.WriteLine("FAILED_MEMBER=" + type + "." + name);
            ExceptionDispatchInfo.Capture(e.InnerException ?? e).Throw();
            throw;
        }
    }
    static object Get(object value, string field)
    { return value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(value); }
    static void Set(object value, string field, object data)
    { value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(value, data); }
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); ++checks; }
    static void Reject(Action action, string why)
    {
        bool rejected = false;
        try { action(); } catch (Exception e) { rejected = e is ArgumentException || e is InvalidDataException || e is InvalidOperationException; }
        Check(rejected, why);
    }
    static string Xml(object data) { return (string)Invoke("UnifiedPressureBridge", "Xml", data); }
    static bool Accept(string[] args) { return (bool)Invoke("UnifiedWorkerEntry", "AcceptsArguments", new object[] { args }); }
    static void EntryContract()
    {
        Check(runtime.EntryPoint.DeclaringType == T("UnifiedWorkerEntry"), "Assembly uses only the restricted entry");
        string dir = Path.Combine(fixtureRoot, "OfflineEntry-DoNotOpen");
        int otherPid = Process.GetCurrentProcess().Id + 1000;
        string pid = otherPid.ToString();
        string[] resident = { "--device-session", Path.Combine(dir, "session-settings.xml"), dir, pid };
        string[] worker = { "--worker", "Pressure", Path.Combine(dir, "factory-settings.xml"), dir, pid };
        Check(Accept(resident), "Structurally valid resident request recognized without executing it");
        Check(Accept(worker), "Structurally valid pressure request recognized without executing it");
        foreach (var args in new[] { new string[0], new[] { "--self-test", "x" }, new[] { "--simulation-check", "x" },
            new[] { "--manual" }, new[] { "--worker", "Calibration", worker[2], dir, pid },
            new[] { "--worker", "WatchConnection", worker[2], dir, pid },
            new[] { "--worker", "Discovery", worker[2], dir, pid },
            new[] { "--worker", "Pressure", worker[2], dir, pid, "extra" },
            new[] { "--device-session", resident[1], dir, pid, "extra" },
            new[] { "--device-session", resident[1], dir, "0" },
            new[] { "--device-session", resident[1], dir, "-1" },
            new[] { "--device-session", resident[1], dir, "abc" },
            new[] { "--device-session", resident[1], dir, Process.GetCurrentProcess().Id.ToString() },
            new[] { "--device-session", "session-settings.xml", dir, pid },
            new[] { "--device-session", resident[1], "relative", pid },
            new[] { "--device-session", worker[2], dir, pid },
            new[] { "--worker", "Pressure", resident[1], dir, pid },
            new[] { "--device-session", Path.Combine(dir, "other.xml"), dir, pid } })
            Check(!Accept(args), "Reject unsupported entry shape: " + String.Join("|", args));
        Check(!Accept(null), "Null entry rejected");
        Console.WriteLine("ENTRY_CONTRACT_CHECKS=" + checks);
    }
    static object Batch(string config, string input, double target, string id, int round, int count, bool start, bool continuation)
    { return Invoke("UnifiedPressureBridge", "PrepareBatchSettings", config, input, target, id, round, count, start, continuation); }
    static void BridgeContract()
    {
        foreach (var method in T("UnifiedPressureBridge").GetMethods(BindingFlags.Public | BindingFlags.Static).OrderBy(m => m.Name))
            Console.WriteLine("BRIDGE_API=" + method);
        var settings = Invoke("FactoryBatchAuthorizationTests", "Request", 2);
        var input = Get(settings, "Input");
        Set(input, "RealHardware", true);
        string config = Xml(settings), inputXml = Xml(input), id = Guid.NewGuid().ToString("N");
        double target = (double)Get(settings, "TargetKv");
        var first = Batch(config, inputXml, target, id, 1, 2, true, false);
        Check((bool)Get(first, "AutomaticBatch") && (bool)Get(first, "BatchStartConfirmed"), "Explicit first start becomes a batch request");
        Check(!(bool)Get(first, "BatchContinuation") && Get(first, "BatchPermit") == null, "Bridge cannot issue an opaque permit");
        Check((bool)Get(first, "IndependentDischargeConfirmed") && (bool)Get(first, "FeedbackWiringConfirmed"), "First request reflects explicit initial confirmation");
        Check(!(bool)Get(first, "FinalSequenceAi4Observation"), "Intermediate request has no final AI4 observation");
        Set(input, "RunNumber", 2); string secondXml = Xml(input);
        var second = Batch(config, secondXml, target, id, 2, 2, false, true);
        Check((bool)Get(second, "BatchContinuation") && !(bool)Get(second, "BatchStartConfirmed"), "Second request is continuation only");
        Check(!(bool)Get(second, "IndependentDischargeConfirmed") && !(bool)Get(second, "FeedbackWiringConfirmed"), "Continuation does not forge manual confirmation");
        Check(Get(second, "BatchPermit") == null && (bool)Get(second, "FinalSequenceAi4Observation"), "Continuation is unprivileged; final-only AI4 flag");
        Check((int)Get(second, "SequenceRound") == 2 && (int)Get(second, "SequenceCount") == 2 && (string)Get(second, "SequenceId") == id, "Stable batch metadata");
        Reject(() => Batch(config, secondXml, target, id, 2, 2, true, true), "Continuation cannot reassert start");
        Reject(() => Batch(config, inputXml, target, id, 1, 2, false, false), "First start requires confirmation");
        Reject(() => Batch(config, inputXml, target, id, 1, 2, false, true), "Round one cannot continue");
        Reject(() => Batch(config, secondXml, target, id, 2, 2, true, false), "Non-first round cannot create start");
        Reject(() => Batch(config, inputXml, target, "not-a-guid", 1, 2, true, false), "Malformed batch id rejected");
        Reject(() => Batch(config, inputXml, target, id, 1, 101, true, false), "Unbounded batch rejected");
        Reject(() => Batch(config, inputXml, target, id, 2, 2, false, true), "Run number mismatch rejected");
        Reject(() => Batch(config, inputXml, 0.0, id, 1, 2, true, false), "Zero target not silently made automatic");
        Set(input, "ArtSerial", "changed-device");
        Reject(() => Batch(config, Xml(input), target, id, 2, 2, false, true), "Bridge rejects changed device identity");
        Set(settings, "Simulation", true);
        Reject(() => Batch(Xml(settings), Xml(input), target, id, 2, 2, false, true), "Real bridge does not accept simulation as real batch");
        var events = new System.Collections.Generic.List<string>();
        Action<string,string> sink = (key,text) => events.Add(key + "|" + text);
        var adapted = (Action<string,string>)Invoke("UnifiedPressureBridge", "AdaptProgress", false, sink);
        adapted("LINK_ART_HIGH", "original-utc|valid"); adapted("LINK_ART_LOW", "original-utc|low"); adapted("LINK_ART_UNKNOWN", "original-utc|unknown");
        Check(events.SequenceEqual(new[] { "EMERGENCY_RELEASED|original-utc|valid", "EMERGENCY_ACTIVE|original-utc|low", "EMERGENCY_UNKNOWN|original-utc|unknown" }), "Bridge preserves original timestamp/detail without fabricating fresh samples");
        events.Clear(); adapted = (Action<string,string>)Invoke("UnifiedPressureBridge", "AdaptProgress", true, sink);
        adapted("LINK_ART_HIGH", "simulated"); Check(events[0] == "SIM_EMERGENCY_RELEASED|simulated", "Simulation cannot masquerade as real permission");
        // Synthetic capture archive only: this helper never invokes the SDK.
        var archivedSettings = Invoke("FactoryBatchAuthorizationTests", "Request", 2);
        var archivedInput = Get(archivedSettings, "Input"); Set(archivedInput, "RealHardware", true);
        string archivedInputXml = Xml(archivedInput);
        var archived = Invoke("FactoryBatchAuthorizationTests", "Capture", archivedSettings);
        string archive = Path.Combine(fixtureRoot, "RangeReview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        Invoke("Program", "SaveResult", archived, archive, false);
        string review = (string)Invoke("UnifiedPressureBridge", "ReviewCapturedRound", archive, archivedInputXml, (double)Get(archivedSettings, "TargetKv"));
        Check(review.Contains("HVX.Pressure.RangeReview/V1.0.30.9"), "Offline review identifies frozen range schema");
        Check(review.Contains("<CurrentRangeIndex>10</CurrentRangeIndex>"), "Offline review preserves actual archived range");
        Reject(() => Invoke("UnifiedPressureBridge", "ReviewCapturedRound", archive, archivedInputXml, (double)Get(archivedSettings, "TargetKv") + 1), "Range review rejects wrong target");
        Set(archivedInput, "RangeIndex", 9);
        Reject(() => Invoke("UnifiedPressureBridge", "ReviewCapturedRound", archive, Xml(archivedInput), (double)Get(archivedSettings, "TargetKv")), "Range review rejects wrong range provenance");
        Console.WriteLine("SYNTHETIC_RANGE_ARCHIVE=" + archive);
        Console.WriteLine("ENTRY_AND_BRIDGE_CHECKS=" + checks);
    }
    static int MarkWorkerCalled() { ++workerCalls; return 7; }
    sealed class OfflineLease : IDisposable { public void Dispose() {} }
    static void RestrictedSessionRound(string command, int expectedCalls)
    {
        string directory = Path.Combine(fixtureRoot, "RestrictedEntry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string record = Path.Combine(directory, "record"); Directory.CreateDirectory(record);
        var settings = Invoke("FactoryBatchAuthorizationTests", "Request", 1);
        Set(settings, "AutomaticBatch", false); Set(settings, "BatchStartConfirmed", false);
        Set(settings, "BatchContinuation", false); Set(settings, "CalibrationSourceConfirmed", true);
        Set(settings, "FinalSequenceAi4Observation", false);
        Set(Get(settings, "Input"), "ArtSerial", "1023417009");
        if(command=="Calibration")
        {
            Reject(()=>Invoke("FactoryNativeSessionPolicy","PrepareQueuedRound",settings,true),
                "Nonzero pressure target cannot inherit calibration purpose");
            Set(settings,"TargetKv",0d);
        }
        // Establish that upstream policy accepts the fixture, so the local
        // Calibration rejection cannot be mistaken for a bad-settings failure.
        Invoke("FactoryNativeSessionPolicy", "PrepareQueuedRound", settings, command == "Calibration");
        File.WriteAllText(Path.Combine(directory, "session-settings.xml"), Xml(settings), Encoding.Unicode);
        File.WriteAllText(Path.Combine(directory, "session.heartbeat"), "offline fake only");
        File.WriteAllText(Path.Combine(record, "factory-settings.xml"), Xml(settings), Encoding.Unicode);
        var request = Activator.CreateInstance(T("FactorySessionRequest"));
        Set(request, "Id", Guid.NewGuid().ToString("N")); Set(request, "Command", command);
        Set(request, "Directory", record); Set(request, "SettingsPath", Path.Combine(record, "factory-settings.xml"));
        File.WriteAllText(Path.Combine(directory, "request.xml"), Xml(request), Encoding.Unicode);
        Type fakeType = T("FactoryBatchServerTests").GetNestedType("FakeSession", BindingFlags.NonPublic);
        var fake = Activator.CreateInstance(fakeType, true);
        var method = T("UnifiedWorkerEntry").GetMethod("RunResidentCore", Flags);
        var parameters = method.GetParameters();
        Type sessionInterface = runtime.GetType("Xiongxin.FactoryTest.IFactoryResidentSession", true);
        var create = Expression.Lambda(parameters[1].ParameterType, Expression.Constant(fake, sessionInterface)).Compile();
        var requestArg = Expression.Parameter(T("FactorySessionRequest"), "request");
        var sessionArg = Expression.Parameter(sessionInterface, "session");
        var callback = Expression.Lambda(parameters[2].ParameterType,
            Expression.Call(typeof(PressureBatchRuntimeSmoke).GetMethod("MarkWorkerCalled", Flags)), requestArg, sessionArg).Compile();
        workerCalls = 0;
        int exit = (int)method.Invoke(null, new object[] {
            new[] { "--device-session", Path.Combine(directory, "session-settings.xml"), directory, Process.GetCurrentProcess().Id.ToString() },
            create, callback, new Func<IDisposable>(() => new OfflineLease()) });
        Check(workerCalls == expectedCalls, command + " exact worker delegate calls=" + expectedCalls);
        Check((int)Get(fake, "Opens") == 1 && (int)Get(fake, "Disposes") == 1, "Fake session opened and disposed once for " + command);
        Check(exit != 0, "Synthetic rejection/failing callback is not success for " + command);
        if (command == "Calibration") Check(File.ReadAllText(Path.Combine(directory, "error.txt")).Contains("只接受Pressure"), "Actual RunCore failure was local pressure-only guard");
        Console.WriteLine("RESTRICTED_SESSION_FIXTURE=" + command + "; workerCalls=" + workerCalls + "; " + directory);
    }
    static void Check2Contract()
    {
        if (T("UnifiedWorkerEntry").GetMethod("ValidateSessionRequest", Flags) == null)
            throw new InvalidOperationException("Required pressure-only session guard is missing");
        var request = Activator.CreateInstance(T("FactorySessionRequest"));
        Set(request, "Command", "Pressure"); Invoke("UnifiedWorkerEntry", "ValidateSessionRequest", request); Check(true, "Pressure command allowed by local adapter");
        foreach (string command in new[] { "Calibration", "Discovery", "WatchConnection", "Manual", "", null })
        { Set(request, "Command", command); Reject(() => Invoke("UnifiedWorkerEntry", "ValidateSessionRequest", request), "Reject resident command " + command); }
        Reject(() => Invoke("UnifiedWorkerEntry", "ValidateSessionRequest", new object[] { null }), "Reject null resident request");
        RestrictedSessionRound("Calibration", 0); RestrictedSessionRound("Pressure", 1);
        Reject(() => Invoke("UnifiedPressureBridge", "AttachLifetime", new object[] { null }), "Reject null close-hook host");
        var disposedForm = new Form(); disposedForm.Dispose();
        Reject(() => Invoke("UnifiedPressureBridge", "AttachLifetime", disposedForm), "Reject disposed close-hook host");
        using (var form = new Form())
        {
            Invoke("UnifiedPressureBridge", "AttachLifetime", form); Invoke("UnifiedPressureBridge", "AttachLifetime", form);
            var e = new FormClosingEventArgs(CloseReason.UserClosing, true);
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { e });
            Check(e.Cancel, "Earlier host navigation cancellation preserved by close hook");
            e = new FormClosingEventArgs(CloseReason.UserClosing, false);
            typeof(Form).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { e });
            Check(!e.Cancel, "No device session: close hook does not synthesize work or block close");
        }
        Console.WriteLine("ENTRY_AND_BRIDGE_CHECKS=" + checks);
    }
    [STAThread]
    static int Main(string[] args)
    {
        string previousTemp = Environment.GetEnvironmentVariable("TEMP"), previousTmp = Environment.GetEnvironmentVariable("TMP");
        try
        {
            if (args.Length != 2) return 2;
            string build = Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar);
            fixtureRoot = Path.GetFullPath(args[1]);
            if (!fixtureRoot.StartsWith(build + Path.DirectorySeparatorChar + "qa" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Offline fixtures must remain in this candidate's QA directory.");
            Directory.CreateDirectory(fixtureRoot);
            // Upstream fake suites use GetTempPath for synthetic archives and
            // IPC files. Keep those writes in the same isolated QA tree too.
            string temporaryRoot = Path.Combine(fixtureRoot, "temp");
            Directory.CreateDirectory(temporaryRoot);
            Environment.SetEnvironmentVariable("TEMP", temporaryRoot, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP", temporaryRoot, EnvironmentVariableTarget.Process);
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(null);
            runtime = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "PressureRuntime", "Xiongxin.Pressure.Runtime.exe"));
            Console.WriteLine("AUDIT_UTC=" + DateTime.UtcNow.ToString("O"));
            Console.WriteLine("RUNTIME=" + runtime.FullName);
            EntryContract(); BridgeContract(); Check2Contract();
            // The latest pressure contract prohibits any before/after-pressure
            // residual acquisition, including the historical final AI4 opt-in.
            // Keep the old archive mathematics/XML suite below, but validate
            // the live workflow with its explicit replacement rather than
            // requiring a now-forbidden native read. Historical FAIL receipts
            // remain evidence; this is not a successful run of the old suite.
            Console.WriteLine("SUPERSEDED_SUITE=FactoryFinalAi4WorkflowTests;REPLACEMENT=FactoryNoResidualPressureTests;REASON=Current pressure contract prohibits final AI4 acquisition");
            T("FactoryNoResidualPressureTests").GetField("FixtureRoot",Flags).SetValue(null,Path.Combine(fixtureRoot,"no-residual"));
            string[] tests = { "FactoryBatchAuthorizationTests", "FactoryBatchServerTests", "FactoryAutomaticBatchTests",
                "FactoryNativeSessionTests", "FactoryResidentDeviceServerTests", "FactoryResidentClientTests",
                "FactorySessionFaultTests", "FactoryEmergencyTimingContractTests", "FactoryBufferEmergencyCurrentTests", "FactoryCaptureCooldownTests",
                "FactoryCooldownWorkflowTests", "FactoryPicoLifecycleTests", "FactoryPressureSequenceTests",
                "FactorySequenceResumeTests", "FactoryFinalAi4ObservationTests", "FactoryNoResidualPressureTests",
                "FactoryLocationCycleTests", "FactoryTransientRangeTests", "FactoryR30Tests" };
            foreach (string test in tests)
            {
                var timer = Stopwatch.StartNew();
                Console.WriteLine("TEST_BEGIN=" + test); Console.Out.Flush();
                Console.WriteLine(Invoke(test, "Run"));
                Console.WriteLine("TEST_PASS=" + test + "; elapsed_ms=" + timer.ElapsedMilliseconds); Console.Out.Flush();
            }
            Console.WriteLine("HardwareAccessed=False; PhysicalAcceptance=False; OFFLINE_AUDIT=PASS; TestSuites=" + tests.Length);
            return 0;
        }
        catch (Exception e) { Console.WriteLine("OFFLINE_AUDIT=FAIL\n" + e); return 1; }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", previousTemp, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TMP", previousTmp, EnvironmentVariableTarget.Process);
        }
    }
}
