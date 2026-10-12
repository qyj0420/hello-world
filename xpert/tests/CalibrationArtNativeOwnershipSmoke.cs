using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Xiongxin.Calibration;

internal static class CalibrationArtNativeOwnershipSmoke
{
    sealed class Fake : IArtControlBoxNative
    {
        internal readonly List<string> Calls = new List<string>();
        internal readonly Dictionary<IntPtr, string> Owned = new Dictionary<IntPtr, string>();
        internal readonly HashSet<IntPtr> ClearFailures = new HashSet<IntPtr>();
        internal string Failure;
        internal bool ReturnZeroTask, ThrowClear;
        internal byte Signal;
        internal int ReadCount = 1, ReadBytes = 1, Written = 1;
        internal uint Serial = 1023417009;
        int next;
        int Result(string call) { Calls.Add(call); return call == Failure ? -99 : 0; }
        public void Load(string path) { Calls.Add("fake-load"); }
        public int ReadDeviceSerial(string device, out uint serial) {serial=Serial;return Result("serial:"+device);}
        public int CreateTask(string name, out IntPtr task)
        {
            task = ReturnZeroTask ? IntPtr.Zero : new IntPtr(++next);
            if (task != IntPtr.Zero) Owned.Add(task, name);
            return Result("create:" + name);
        }
        public int CreateInput(IntPtr task, string lines) { return Result("di:" + lines); }
        public int CreateOutput(IntPtr task, string lines) { return Result("do:" + lines); }
        public int Start(IntPtr task) { return Result("start:" + task); }
        public int Clear(IntPtr task)
        {
            Calls.Add("clear:" + task);
            if (ClearFailures.Contains(task))
            {
                if (ThrowClear) throw new IOException("injected clear exception");
                return -98;
            }
            if (!Owned.Remove(task)) throw new Exception("Attempted to clear a task not owned by this backend.");
            return 0;
        }
        public int Read(IntPtr task, out byte value, out int read, out int bytes)
        { value = Signal; read = ReadCount; bytes = ReadBytes; return Result("read:" + task); }
        public int Write(IntPtr task, byte value, out int written)
        { written = Written; return Result("write:" + task + ":" + value); }
    }
    static int count;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
    static Exception Reject(Action operation, string message)
    {
        try { operation(); } catch (Exception error) { count++; return error; }
        throw new Exception("Expected rejection: " + message);
    }
    static IntPtr Handle(ArtControlBoxIo io, string field)
    { return (IntPtr)typeof(ArtControlBoxIo).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(io); }
    static ArtControlBoxIo Create(Fake fake) { return new ArtControlBoxIo("OfflineDevice", "NOT_A_DRIVER_DIRECTORY", fake); }

    static void AllowedRoutes()
    {
        var fake = new Fake(); var io = Create(fake); io.Open();
        Check(fake.Calls.Count == 4, "open only loads, creates P04 DI and starts input");
        Check(fake.Calls[1] == "create:Calibration_P04" && fake.Calls[2] == "di:OfflineDevice/port0/line4", "only fixed P04 input at open");
        Check(io.HasOwnedTasks && !io.CleanupPending && Handle(io, "impedance") == IntPtr.Zero, "no output task at connect");
        Check(!io.ReadEmergencySignalHigh(), "raw low remains low");
        fake.Signal = 1; Check(io.ReadEmergencySignalHigh(), "raw high remains high");
        fake.Signal = 2; Reject(() => io.ReadEmergencySignalHigh(), "invalid digital value");
        fake.Signal = 0; fake.ReadCount = 0; Reject(() => io.ReadEmergencySignalHigh(), "missing sample");
        fake.ReadCount = 1; fake.ReadBytes = 0; Reject(() => io.ReadEmergencySignalHigh(), "missing byte"); fake.ReadBytes = 1;
        io.WriteShortCable(true); IntPtr task = Handle(io, "impedance"); io.WriteShortCable(false);
        Check(fake.Calls.FindAll(x => x.StartsWith("create:")).Count == 2, "P12 reused on repeated impedance selection");
        Check(fake.Calls.Contains("do:OfflineDevice/port1/line2"), "only fixed P12 output");
        Check(!fake.Calls.Exists(x => x.Contains("P05") || x.Contains("line5") || x.Contains("line6") || x.Contains("line7")), "no lamp, HV or optical route");
        Check(Handle(io, "impedance") == task, "successful write preserves handle");
        io.Close(); Check(!io.HasOwnedTasks && !io.CleanupPending && fake.Owned.Count == 0, "both tasks released");
        int calls = fake.Calls.Count; io.Close(); Check(fake.Calls.Count == calls, "no duplicate Clear after release");
        Reject(() => io.ReadEmergencySignalHigh(), "closed read"); Reject(() => io.WriteShortCable(true), "closed output");
        io.Open(); Check(Handle(io, "input") != new IntPtr(1), "reconnect gets new task rather than old handle"); io.Close();
    }
    static void PartialCleanup()
    {
        foreach (bool failInput in new[] { false, true })
        foreach (bool throwClear in new[] { false, true })
        {
            var fake = new Fake { ThrowClear = throwClear }; var io = Create(fake); io.Open(); io.WriteShortCable(true);
            IntPtr input = Handle(io, "input"), output = Handle(io, "impedance");
            IntPtr failed = failInput ? input : output, cleared = failInput ? output : input;
            fake.ClearFailures.Add(failed); Reject(io.Close, "partial cleanup failure");
            Check(io.HasOwnedTasks && io.CleanupPending && fake.Owned.Count == 1, "partial owner retained");
            Check(Handle(io, failInput ? "input" : "impedance") == failed, "failed handle unchanged");
            Check(Handle(io, failInput ? "impedance" : "input") == IntPtr.Zero, "successful sibling cleared");
            int calls = fake.Calls.Count;
            Reject(io.Open, "cannot replace failed task"); Reject(() => io.ReadEmergencySignalHigh(), "pending read");
            Reject(() => io.WriteShortCable(false), "pending output"); Check(fake.Calls.Count == calls, "guard makes no I/O");
            fake.ClearFailures.Clear(); io.Close(); Check(!io.HasOwnedTasks && !io.CleanupPending, "explicit retry success");
            Check(fake.Calls.FindAll(x => x == "clear:" + failed).Count == 2, "failed task retried once");
            Check(fake.Calls.FindAll(x => x == "clear:" + cleared).Count == 1, "released task never cleared twice");
        }
        var both = new Fake(); var owned = Create(both); owned.Open(); owned.WriteShortCable(true);
        both.ClearFailures.Add(Handle(owned, "input")); both.ClearFailures.Add(Handle(owned, "impedance"));
        var error = Reject(owned.Close, "both clear failures");
        Check(((AggregateException)error).InnerExceptions.Count == 2 && both.Owned.Count == 2, "preserves both failure evidence and ownership");
        both.ClearFailures.Clear(); owned.Close();
    }
    static void PartialCreation()
    {
        foreach (string failure in new[] { "create:Calibration_P04", "di:OfflineDevice/port0/line4", "start:1" })
        {
            var fake = new Fake { Failure = failure }; var io = Create(fake); fake.ClearFailures.Add(new IntPtr(1));
            var error = Reject(io.Open, "open failure: " + failure);
            Check(error is AggregateException, "both open and cleanup failure evidence");
            Check(io.HasOwnedTasks && io.CleanupPending && Handle(io, "input") == new IntPtr(1), "partial input retained");
            Reject(io.Open, "partial input cannot be replaced"); fake.ClearFailures.Clear(); io.Close(); Check(!io.HasOwnedTasks, "partial input explicitly released");
        }
        foreach (string failure in new[] { "create:Calibration_P12", "do:OfflineDevice/port1/line2", "start:2", "write:2:1" })
        {
            var fake = new Fake(); var io = Create(fake); io.Open(); fake.Failure = failure;
            Reject(() => io.WriteShortCable(true), "output failure: " + failure);
            Check(io.CleanupPending && fake.Owned.Count == 2, "partial output owner retained with input");
            int calls = fake.Calls.Count;
            Reject(() => io.WriteShortCable(false), "cannot reuse incomplete output");
            Reject(() => io.ReadEmergencySignalHigh(), "incomplete output blocks permission");
            Check(fake.Calls.Count == calls, "fault guard does not retry output");
            io.Close(); Check(fake.Owned.Count == 0 && !io.HasOwnedTasks, "partial output explicitly released");
        }
        var empty = new Fake { ReturnZeroTask = true }; var emptyIo = Create(empty);
        Reject(emptyIo.Open, "success with zero handle rejected");
        Check(!emptyIo.HasOwnedTasks && !emptyIo.CleanupPending && !empty.Calls.Exists(x => x.StartsWith("start:")), "zero handle never started");
        var shortWrite = new Fake { Written = 0 }; var shortIo = Create(shortWrite); shortIo.Open();
        Reject(() => shortIo.WriteShortCable(true), "incomplete write rejected");
        Check(shortIo.CleanupPending && shortIo.HasOwnedTasks, "incomplete write ownership preserved"); shortIo.Close();
        foreach (string invalid in new[] { "", " ", "Dev/1", "Dev\\1", "Dev,1", "Dev:1" })
        {
            var fake = new Fake(); var io = new ArtControlBoxIo(invalid, "unused", fake);
            Reject(io.Open, "invalid device route"); Check(fake.Calls.Count == 0, "invalid route cannot load backend");
        }
    }
    static int Main()
    {
        ControlBoxTests.Run(); AllowedRoutes(); PartialCleanup(); PartialCreation(); IdentityBeforeTasks();
        Check((IntPtr)typeof(Art).GetField("module", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == IntPtr.Zero, "real ART library never loaded");
        Console.WriteLine("PASS: " + count + " native-boundary fake checks; no SDK, hardware or product startup.");
        return 0;
    }
    static void IdentityBeforeTasks()
    {
        foreach(uint serial in new uint[]{0,1023417010})
        {
            var fake=new Fake{Serial=serial};var io=new ArtControlBoxIo("Dev7","unused",fake,"1023417009");
            Reject(io.Open,"wrong native serial");
            Check(fake.Owned.Count==0&&!fake.Calls.Exists(x=>x.StartsWith("create:")||x.StartsWith("write:")),"mismatch has zero task creation/output");
        }
        var failed=new Fake{Failure="serial:Dev7"};var failedIo=new ArtControlBoxIo("Dev7","unused",failed,"1023417009");
        Reject(failedIo.Open,"failed serial read");Check(failed.Owned.Count==0&&!failed.Calls.Exists(x=>x.StartsWith("create:")),"failed read cannot create input");
        var matching=new Fake();var valid=new ArtControlBoxIo("Dev7","unused",matching,"1023417009");valid.Open();
        Check(matching.Calls[1]=="serial:Dev7"&&matching.Calls[2]=="create:Calibration_P04","exact alias serial checked before P04 creation");
        matching.Serial=1023417010;
        Reject(()=>valid.WriteShortCable(true),"alias changed before impedance creation");
        Check(matching.Owned.Count==1&&!matching.Calls.Exists(x=>x.StartsWith("do:")||x.StartsWith("write:")),"changed alias cannot create or write P12");
        Check(valid.CleanupPending&&valid.HasOwnedTasks,"identity failure retains original input ownership");valid.Close();
        Check(!valid.HasOwnedTasks,"explicit cleanup releases original input only");
    }
}
