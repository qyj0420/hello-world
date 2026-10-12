using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.Calibration;
using Xiongxin.Scope;

internal static class CalibrationSessionCleanupSmoke
{
    sealed class Io : IControlBoxIo
    {
        internal bool High, ReadFailure, CloseFailure;
        internal int Opens, Closes, Reads, Writes;
        public void Open() { Opens++; }
        public bool ReadEmergencySignalHigh() { Reads++; if (ReadFailure) throw new IOException("fake input failure"); return High; }
        public void WriteShortCable(bool value) { Writes++; }
        public void Close() { Closes++; if (CloseFailure) throw new IOException("fake ART clear failure"); }
    }
    sealed class Worker : ICalibrationWorker
    {
        internal bool IsBroken, CloseFailure, RetirementFailure, ForcedRetirement;
        internal int Closes, Retirements, Cancels, Sends;
        internal TaskCompletionSource<bool> CloseGate;
        public bool Broken { get { return IsBroken; } }
        public Task<Reply> Send(Command command, int timeout) { Sends++; throw new Exception("No worker commands allowed in this fixture."); }
        public void Cancel() { Cancels++; }
        public async Task Close(int commandTimeout = 5000, int exitTimeout = 2000)
        { Closes++; if (CloseGate != null) await CloseGate.Task; if (CloseFailure) throw new IOException("fake Pico close failure"); }
        public Task<bool> RetireUnresponsive()
        { Retirements++; if (RetirementFailure) throw new IOException("fake owned worker has not exited"); return Task.FromResult(ForcedRetirement); }
    }
    sealed class Context : IDisposable
    {
        internal readonly CalibrationForm Form;
        internal readonly CalibrationProgramControl Page;
        internal readonly ControlBox Box;
        internal readonly Io Io;
        internal readonly Worker Worker;
        internal readonly List<Exception> Errors = new List<Exception>();
        internal Context(string root, bool broken = false)
        {
            Io = new Io(); Box = new ControlBox(Io); Box.Connect(); Worker = new Worker { IsBroken = broken };
            Form = new CalibrationForm(null, true);
            Form.HostHardwareIdentityProvider=()=>new[]{"fake-bound-session","FakeArt","1023417009","ABCDE/1234"};
            ((Timer)Get(Form, "safetyTimer")).Stop();
            Form.HostErrorReported += error => Errors.Add(error);
            Set(Form, "box", Box); Set(Form, "client", Worker); Set(Form, "picoConnected", true);
            ((TextBox)Get(Form, "projectPath")).Text = root;
            Set(Form, "geometryConfirmed", true);
            Page = new CalibrationProgramControl();
            Set(Page, "activeAcquisition", Form);
            Call(Form, "RefreshState");
        }
        public void Dispose()
        {
            Io.CloseFailure = false; Worker.CloseFailure = false; Worker.RetirementFailure = false;
            if (Worker.CloseGate != null) Worker.CloseGate.TrySetResult(true);
            // Test cleanup never substitutes for the assertions made before this point.
            Set(Form, "capturing", false); Set(Form, "operationBusy", false);
            Form.Dispose(); Page.Dispose();
        }
    }
    static int checks;
    static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    static object Get(object instance, string name) { return instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance); }
    static void Set(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value); }
    static object Call(object instance, string name, params object[] args) { return instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(instance, args); }
    static async Task Reject(Func<Task> action, string reason)
    { try { await action(); } catch { checks++; return; } throw new Exception("Expected rejection: " + reason); }

    static async Task CleanupFailures(string root)
    {
        using (var c = new Context(root))
        {
            Check(c.Form.HasOwnedHardwareResources && c.Form.HasActiveHardwareSession && c.Page.IsBusy, "initial owned session locks parent");
            c.Io.CloseFailure = true; c.Worker.CloseFailure = true;
            await Reject(() => c.Form.HostReleaseAsync(), "both close failures");
            Check(Object.ReferenceEquals(Get(c.Form, "client"), c.Worker) && (bool)Get(c.Form, "picoConnected"), "failed Pico close retains original client and connection state");
            Check(c.Box.Connected && c.Box.CleanupPending && c.Form.HasActiveHardwareSession, "failed ART close retains owner and session lock");
            Check(c.Errors.Count == 2 && c.Worker.Closes == 1 && c.Io.Closes == 1, "both releases attempted and errors preserved");
            Call(c.Page, "RetainOrDisposeAcquisition", c.Form);
            Check(Object.ReferenceEquals(Get(c.Page, "activeAcquisition"), c.Form) && !c.Form.IsDisposed, "failed coordinator retained, not disposed");
            Check(c.Page.HasActiveHardwareSession && c.Page.IsBusy, "failed cleanup keeps page busy/navigation contract");
            Check(c.Page.RequestHardwareStop() && c.Worker.Cancels == 1, "stop requests worker cancel without claiming released");
            Check(c.Page.IsBusy && c.Form.HasOwnedHardwareResources, "cancel is not release evidence");
            int opens = c.Io.Opens;
            Call(c.Form, "ConnectBox");
            Check(c.Io.Opens == opens && Object.ReferenceEquals(Get(c.Form, "box"), c.Box), "pending box cannot be replaced by a new native backend");
            Check(!((Button)Get(c.Form, "connectBox")).Enabled && !((Button)Get(c.Form, "connectPico")).Enabled, "pending cleanup disables reconnect");
            c.Worker.CloseFailure = false;
            await Reject(async () => { await c.Page.RetryHardwareCleanupAsync(); }, "ART still fails after Pico clean close");
            Check(Get(c.Form, "client") == null && !(bool)Get(c.Form, "picoConnected"), "successful Pico close alone clears Pico ownership");
            Check(c.Page.IsBusy && c.Box.CleanupPending && !c.Form.IsDisposed, "remaining ART failure still locks page");
            Check((bool)Get(c.Page, "calibrationStopRequested"), "stop latch retained while release pending");
            int closes = c.Worker.Closes;
            c.Io.CloseFailure = false;
            Check(await c.Page.RetryHardwareCleanupAsync(), "explicit final retry completes cleanup");
            Check(c.Worker.Closes == closes && !c.Box.Connected && !c.Box.CleanupPending, "successful Pico not closed twice; ART retry succeeds");
            Check(!c.Page.IsBusy && !c.Page.HasActiveHardwareSession && Get(c.Page, "activeAcquisition") == null && c.Form.IsDisposed, "unlock and disposal only after all owners cleared");
            Check(!(bool)Get(c.Page, "calibrationStopRequested"), "stop latch clears only after release completion");
            Check(await c.Page.RetryHardwareCleanupAsync(), "empty retry is idempotent");
            Check(c.Worker.Sends == 0, "no OPEN/CAPTURE/SDK worker calls");
        }
    }
    static async Task RetirementAndBusy(string root)
    {
        using (var c = new Context(root, true))
        {
            c.Worker.RetirementFailure = true;
            await Reject(() => c.Form.HostReleaseAsync(), "worker cannot be retired");
            Check(c.Worker.Retirements == 1 && c.Worker.Closes == 0, "broken worker uses owned retirement path");
            Check(Object.ReferenceEquals(Get(c.Form, "client"), c.Worker) && c.Form.HasOwnedHardwareResources && c.Page.IsBusy, "failed retirement retains client and lock");
            Check(!c.Box.Connected && c.Errors.Count == 1, "independent successful ART release is preserved");
            Call(c.Page, "RetainOrDisposeAcquisition", c.Form);
            Check(!c.Form.IsDisposed && Object.ReferenceEquals(Get(c.Page, "activeAcquisition"), c.Form), "failed retirement coordinator stays alive");
            // No claim that a successful retirement proves native Stop/Close or clears persistent BLOCKED.
        }
        using (var c = new Context(root))
        {
            Set(c.Form, "capturing", true);
            await Reject(() => c.Form.HostReleaseAsync(), "acquisition still active");
            Set(c.Form, "capturing", false); Set(c.Form, "operationBusy", true);
            await Reject(() => c.Form.HostReleaseAsync(), "worker operation still active");
            Check(c.Worker.Closes == 0 && c.Worker.Retirements == 0 && c.Io.Closes == 0, "busy release has no cleanup calls");
            Set(c.Form, "operationBusy", false); Set(c.Page, "hostAcquisitionActive", true);
            Check(!await c.Page.RetryHardwareCleanupAsync(), "parent capture prevents cleanup reentry");
            Set(c.Page, "hostAcquisitionActive", false);
            c.Worker.CloseGate = new TaskCompletionSource<bool>();
            Task<bool> release = c.Page.RetryHardwareCleanupAsync();
            Check(!release.IsCompleted && c.Page.IsBusy && (bool)Get(c.Page, "hardwareCleanupInProgress"), "awaited cleanup keeps parent lock");
            Check(!await c.Page.RetryHardwareCleanupAsync(), "duplicate cleanup cannot race first");
            Check(c.Worker.Closes == 1 && c.Io.Closes == 0, "ART cleanup waits for outstanding owned worker operation");
            c.Worker.CloseGate.SetResult(true);
            Check(await release && !c.Page.IsBusy && c.Form.IsDisposed, "release acknowledgement unlocks once");
        }
    }
    static async Task CalibrationPolarity(string root)
    {
        using (var c = new Context(root))
        {
            Check(((Button)Get(c.Form, "acquire")).Enabled, "low P04 and valid state permit calibration UI");
            c.Box.BeginCapture(); Check(c.Box.Capturing, "low P04 permits box capture"); c.Box.EndCapture();
            c.Io.High = true; c.Box.Poll(); Call(c.Form, "RefreshState");
            Check(((Button)Get(c.Form, "acquire")).Enabled && ((Label)Get(c.Form, "boxStatus")).Text.Contains("急停已解除"), "actual high permits external input calibration with truthful display");
            c.Box.BeginCapture();Check(c.Box.Capturing,"high P04 permits input-only box capture");c.Box.EndCapture();
            Set(c.Form, "capturing", true);
            Call(c.Form, "PollEmergency");
            Check(c.Worker.Cancels == 0 && !(bool)Get(c.Form, "stopRequested"), "emergency level change does not fault external-input calibration");
            Set(c.Form, "capturing", false);
            c.Io.High = false; c.Box.Poll(); Set(c.Form, "capturing", true); Set(c.Form, "readFailureNotified", true);
            c.Io.ReadFailure = true; Call(c.Form, "PollEmergency");
            Check(c.Worker.Cancels == 1 && c.Box.EmergencySignalHigh == null, "actual input-read failure still requests stop and remains unknown");
            Set(c.Form, "capturing", false); c.Io.ReadFailure = false;
            await c.Form.HostReleaseAsync();
            Check(!c.Form.HasOwnedHardwareResources, "polarity fixture explicitly releases fake resources");
        }
    }
    static async Task UnverifiedRetirement(string root)
    {
        foreach (bool forced in new[] { false, true })
        using (var c = new Context(root, true))
        {
            c.Worker.ForcedRetirement = forced;
            await c.Form.HostReleaseAsync();
            Check(c.Worker.Retirements == 1 && c.Worker.Closes == 0 && Get(c.Form, "client") == null, "exited worker object retired without native close claim");
            Check(c.Form.HostRequiresConnectionRecheck && !c.Form.HasOwnedHardwareResources, "process exit remains unverified hardware state");
            string text = ((TextBox)Get(c.Form, "result")).Text;
            Check(text.Contains("释放状态未确认") && !text.Contains("Pico 已释放"), "retirement log distinguishes exit from device release");
            Check(!((Button)Get(c.Form, "connectPico")).Enabled && !((Button)Get(c.Form, "connectBox")).Enabled, "unverified retirement cannot reopen from coordinator");
            await Reject(() => c.Form.HostAcquireAsync(180, 170, 100, false), "retired coordinator cannot start another capture");
            Check(c.Worker.Sends == 0 && c.Io.Opens == 1, "rejected restart has no worker or device-open calls");
            Call(c.Page, "RetainOrDisposeAcquisition", c.Form);
            Check(c.Form.IsDisposed && Get(c.Page, "activeAcquisition") == null, "retired coordinator disposed after owned objects ended");
            Check((bool)Get(c.Page, "hostConnectionUnavailable") && !(bool)Call(c.Page, "CanContinueHardwareAcquisition"), "parent latches required connection recheck");
            Check(!c.Page.HasActiveHardwareSession && !c.Page.IsBusy, "finished process does not leave a fictitious running session");
            Check(await c.Page.RetryHardwareCleanupAsync() && (bool)Get(c.Page, "hostConnectionUnavailable"), "empty cleanup retry cannot clear recheck requirement");
        }
    }
    static async Task GenerationIdentity(string root)
    {
        using (var c = new Context(root))
        using (var other = new CalibrationForm(null, true))
        {
            ((Timer)Get(other, "safetyTimer")).Stop();
            Set(c.Page, "hardwareSessionGeneration", 12L);
            Check((bool)Call(c.Page, "IsCurrentHardwareAcquisition", c.Form, 12L), "matching coordinator and generation accepted");
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", c.Form, 11L), "old generation rejected despite same coordinator reference");
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", other, 12L), "foreign coordinator rejected despite matching generation");
            Set(c.Page, "activeAcquisition", other); Set(c.Page, "hardwareSessionGeneration", 13L);
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", c.Form, 12L), "old session cannot update replacement session");
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", c.Form, 13L), "old coordinator cannot borrow replacement generation");
            Check((bool)Call(c.Page, "IsCurrentHardwareAcquisition", other, 13L), "replacement session identity accepted");
            other.Dispose();
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", other, 13L), "disposed coordinator rejected");
            Set(c.Page, "activeAcquisition", c.Form);
            await c.Form.HostReleaseAsync(); c.Page.Dispose();
            Check(!(bool)Call(c.Page, "IsCurrentHardwareAcquisition", c.Form, 13L), "disposed page rejects even current identity");
        }
    }
    static async Task CancelInsideConfirmation(string root)
    {
        foreach (bool shortCable in new[] { false, true })
        foreach (DialogResult answer in new[] { DialogResult.OK, DialogResult.Cancel })
        using (var c = new Context(root))
        {
            int confirmations = 0;
            c.Form.HostParameterConfirmationForTests = () =>
            {
                confirmations++;
                if (answer == DialogResult.OK) c.Form.HostCancel();
                return answer;
            };
            if (answer == DialogResult.OK)
                await Reject(() => c.Form.HostAcquireAsync(180, 170, 100, shortCable), "cancel while confirmation open must defeat later OK");
            else
            {
                await c.Form.HostAcquireAsync(180, 170, 100, shortCable);
                Check(c.Form.HasOwnedHardwareResources, "declined parameters do not falsely claim retained resources released");
                await c.Form.HostReleaseAsync();
            }
            Check(confirmations == 1 && c.Worker.Cancels == (answer == DialogResult.OK ? 1 : 0), "confirmation records actual stop versus user decline");
            Check(((ComboBox)Get(c.Form, "impedance")).SelectedIndex == (shortCable ? 1 : 0), "host parameter really changes visible selection without output");
            Check(c.Io.Opens == 1 && c.Io.Writes == 0 && c.Worker.Sends == 0, "no impedance, OPEN or CAPTURE before or after cancelled confirmation");
            Check(c.Worker.Closes == 1 && c.Io.Closes == 1 && !c.Form.HasOwnedHardwareResources, "cancelled confirmation still releases retained fake resources");
            Check(!c.Form.LastCaptureSucceeded, "cancelled confirmation cannot publish capture success");
            Call(c.Page, "RetainOrDisposeAcquisition", c.Form);
            Check(c.Form.IsDisposed && !c.Page.IsBusy, "confirmed release permits parent unlock after cancellation");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        Exception failure = null;
        Application.EnableVisualStyles();
        using (var pump = new Form { ShowInTaskbar = false, Opacity = 0 })
        {
            pump.Shown += async delegate
            {
                try
                {
                    string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
                    await CleanupFailures(root); await RetirementAndBusy(root); await CalibrationPolarity(root);
                    await UnverifiedRetirement(root); await GenerationIdentity(root);
                    await CancelInsideConfirmation(root);
                    Check((IntPtr)typeof(Art).GetField("module", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == IntPtr.Zero, "ART native library never loaded");
                    Console.WriteLine("CALIBRATION_SESSION_CLEANUP_PASS=" + checks + "; only injected fake I/O/worker; no SDK, actual worker, hardware or product startup.");
                }
                catch (Exception error) { failure = error; Console.Error.WriteLine(error); }
                finally { pump.Close(); }
            };
            Application.Run(pump);
        }
        return failure == null ? 0 : 1;
    }
}
