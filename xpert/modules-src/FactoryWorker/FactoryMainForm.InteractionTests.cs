using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static string TestInteractionChanges()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Interaction regression: "+why);n++;};
            var settings=new FactorySettings {ImonFullScaleMa=7,CurrentLimitMa=10};
            try{settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);throw new Exception("invalid limit accepted");}
            catch(ArgumentException ex){check(ex.Message.Contains("限流/mA=10") && ex.Message.Contains("IMON满量程 7"),"specific mismatch not generic error");}
            settings.ImonFullScaleMa=10;settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);check(true,"matching limit accepted unchanged");
            string root=Path.Combine(Path.GetTempPath(),"factory-interaction-"+Guid.NewGuid().ToString("N"));
            using(var f=new FactoryMainForm(Path.Combine(root,"labels")))
            {
                f.smokeMode=true;f.busy=true;f.SetBusy(true);
                f.cancellation=new CancellationTokenSource();f.recognitionCancellation=new CancellationTokenSource();f.analysisCancellation=new CancellationTokenSource();
                f.RequestUserStop();
                check(f.cancellation.IsCancellationRequested && f.recognitionCancellation.IsCancellationRequested && f.analysisCancellation.IsCancellationRequested,"stop cancels all owned work");
                check(f.recognitionSuspended && !f.start.Enabled && f.busy,"stop does not prematurely mark release complete");
                var previous=f.labelTask;f.lastRecognitionSignature="force different";f.SchedulePulseRecognition();
                check(ReferenceEquals(previous,f.labelTask),"stop does not reschedule recognition");
                f.cancellation.Dispose();f.cancellation=null;f.recognitionCancellation.Dispose();f.recognitionCancellation=null;f.analysisCancellation.Dispose();f.analysisCancellation=null;
                f.busy=false;f.SetBusy(false);check(f.start.Enabled && !f.stop.Enabled,"simulation idle only after release");
                check(!f.monitorExtrema.Checked && !f.pressureTrend.ShowIntervalExtrema,"fresh UI hides interval extrema by default");
                f.monitorExtrema.Checked=true;var olderProfile=f.SnapshotParameters();f.monitorExtrema.Checked=false;f.ApplyParameterProfile(olderProfile);
                check(f.monitorExtrema.Checked && f.pressureTrend.ShowIntervalExtrema,"an explicit saved true still restores interval extrema");
                f.monitorExtrema.Checked=false;var profile=f.SnapshotParameters();f.monitorExtrema.Checked=true;f.ApplyParameterProfile(profile);
                check(!f.pressureTrend.ShowIntervalExtrema && !f.monitorExtrema.Checked,"extrema visibility restored without touching data");
                check(f.ReadSettings().Input.TriggerPulseMs==1000,"main UI uses 1000 ms constant");
                f.ShowInTaskbar=false;f.Opacity=0;f.Show();Application.DoEvents();
                foreach(bool popup in new[]{true,false})
                {
                    f.repeatPrompts.Checked=popup;f.busy=true;f.cancellation=new CancellationTokenSource();f.SetBusy(true);
                    var wait=f.ConfirmNextRound(2,5,f.ReadSettings(),f.cancellation.Token);
                    check(!wait.IsCompleted && f.Enabled && f.stop.Enabled,"popup/inline next-round wait keeps main Stop reachable");
                    f.RequestUserStop();var elapsed=Stopwatch.StartNew();
                    while(!wait.IsCompleted && elapsed.ElapsedMilliseconds<3000){Application.DoEvents();Thread.Sleep(1);}
                    check(wait.IsCompleted && !wait.GetAwaiter().GetResult(),"stop closes popup/inline confirmation without accepting");
                    check(f.busy && !f.start.Enabled,"waiting cancellation still waits for owner cleanup");
                    CheckNextRoundPromptCleaned(f,check);
                    f.cancellation.Dispose();f.cancellation=null;f.busy=false;f.SetBusy(false);
                }
                f.Hide();
                TestStoppedCapturePreview(f,root,check);
                TestStoppedEnvelope(f,check);
            }
            foreach(bool popup in new[]{true,false})
            {
                using(var f=new FactoryMainForm(Path.Combine(root,"close-"+popup)))
                {
                    f.smokeMode=true;f.ShowInTaskbar=false;f.Opacity=0;f.Show();Application.DoEvents();
                    f.repeatPrompts.Checked=popup;f.busy=true;f.cancellation=new CancellationTokenSource();f.SetBusy(true);
                    var wait=f.ConfirmNextRound(2,3,f.ReadSettings(),f.cancellation.Token);
                    f.Close();PumpInteractionTask(wait);
                    check(!wait.GetAwaiter().GetResult() && f.closeAfterStop && !f.IsDisposed && f.busy,
                        "owner close denies popup/inline confirmation and still waits for operation cleanup");
                    CheckNextRoundPromptCleaned(f,check);
                    f.cancellation.Dispose();f.cancellation=null;f.busy=false;f.SetBusy(false);f.Close();
                    check(f.IsDisposed,"owner closes after the cancelled wait releases its operation");
                }
            }
            return "Feedback/UI interaction: "+n+" offline checks PASS; no hardware; auto-reenergization not enabled.";
        }

        private static void TestStoppedCapturePreview(FactoryMainForm f,string root,Action<bool,string> check)
        {
            // A controlled pending read verifies the exact await boundary, without
            // a worker process, SDK, real record or timing-dependent disk delay.
            f.ResetStages();f.latest=null;f.busy=true;f.SetBusy(true);
            f.cancellation=new CancellationTokenSource();
            var pending=new TaskCompletionSource<bool>();f.capturePreviewLoad=pending.Task;
            var ready=f.WaitForRoundCapturePreview(new FactoryResult {DirectoryPath=Path.Combine(root,"must-not-load")},f.cancellation.Token);
            int generation=f.capturePreviewGeneration,timeline=f.timelineGeneration;
            f.RequestUserStop();pending.SetResult(true);PumpInteractionTask(ready);
            check(ready.IsCanceled,"stop after preview await cancels before fallback loading and range analysis");
            check(f.capturePreviewSuspended && f.capturePreviewGeneration>generation && f.timelineGeneration>timeline,"stop invalidates pending preview and timeline generations");
            var stoppedTask=f.capturePreviewLoad;generation=f.capturePreviewGeneration;
            f.RequestCapturePreview(Path.Combine(root,"must-not-load"));
            check(ReferenceEquals(stoppedTask,f.capturePreviewLoad) && f.capturePreviewGeneration==generation,"late capture notification cannot restart preview after stop");
            check(!f.start.Enabled && f.busy,"preview cancellation cannot enable start before owner cleanup");
            f.cancellation.Dispose();f.cancellation=null;

            // Publish a small, entirely managed fake-backend fixture and exercise
            // the real asynchronous reader. No native backend is instantiated.
            var fixture=new FactoryTests.Fixture();var record=fixture.Run();
            string directory=Path.Combine(root,"preview-fixture");
            using(var publisher=new FactoryCapturePublisher(directory,(key,value)=>{}))publisher.Queue(record);
            string previewDirectory=Path.Combine(directory,"capture-preview");
            f.ResetStages();f.latest=null;f.combined.ClearWave();f.rawPd.ClearWave();
            f.stageSettings=record.Settings.Clone();f.activeCaptureDirectory=directory;f.activeCaptureKind=record.Kind;
            f.RequestCapturePreview(previewDirectory);var loading=f.capturePreviewLoad;
            check(!loading.IsCompleted,"real preview reader is awaiting UI continuation");
            f.RequestUserStop();PumpInteractionTask(loading);
            check(f.displayedPreview==null && !f.combined.HasWaveform && !f.rawPd.HasWaveform,"stopped in-flight preview cannot apply late waveform results");

            f.ResetStages();f.latest=null;f.stageSettings=record.Settings.Clone();
            f.activeCaptureDirectory=directory;f.activeCaptureKind=record.Kind;
            check(!f.capturePreviewSuspended,"explicit new round reset resumes preview loading");
            f.RequestCapturePreview(previewDirectory);PumpInteractionTask(f.capturePreviewLoad);
            check(f.displayedPreview!=null && f.combined.HasWaveform && f.rawPd.HasWaveform,"new round preview remains available after prior stop");
            var displayed=f.displayedPreview;f.RequestUserStop();
            check(ReferenceEquals(displayed,f.displayedPreview) && f.combined.HasWaveform && f.rawPd.HasWaveform,"stop retains already displayed waveforms");
            f.busy=false;f.SetBusy(false);
        }

        private static void PumpInteractionTask(Task task)
        {
            var elapsed=Stopwatch.StartNew();
            while(!task.IsCompleted && elapsed.ElapsedMilliseconds<10000){Application.DoEvents();Thread.Sleep(1);}
            if(!task.IsCompleted)throw new TimeoutException("Offline UI task did not complete");
            if(task.IsFaulted)task.GetAwaiter().GetResult();
        }

        private static void TestStoppedEnvelope(FactoryMainForm f,Action<bool,string> check)
        {
            var fixture=new FactoryTests.Fixture();var round=fixture.Run();
            // A fresh, sizeable managed buffer keeps the filtering task pending
            // until Stop/Close are delivered. It is never exported as device data.
            round.Capture.Settings.RequestedPicoRateHz=25000000;
            round.Capture.Settings.CaptureDurationMs=40;
            round.Capture.PicoRateHz=25000000;
            round.Capture.PdAdc=new short[round.Capture.Settings.RequestedPicoCount];
            round.Capture.VoltageRaw=new double[round.Capture.Settings.ExpectedArtCount];
            f.pulseAnalysisRound=round;f.rawPd.ConfigureSelection(5,25);
            double first=f.rawPd.SelectionStartMs,last=f.rawPd.SelectionEndMs;
            var selecting=f.SelectPulseEnvelope(0,40);
            check(!selecting.IsCompleted && f.exporting && f.stop.Enabled && !f.start.Enabled,"envelope analysis exposes stop and blocks start");
            f.RequestUserStop();PumpInteractionTask(selecting);
            check(f.rawPd.SelectionStartMs==first && f.rawPd.SelectionEndMs==last && f.tfSummary.Text.Contains("已停止"),"cancelled envelope cannot change the selected interval: "+first+"/"+last+" -> "+f.rawPd.SelectionStartMs+"/"+f.rawPd.SelectionEndMs+"; "+f.tfSummary.Text);
            check(!f.exporting && f.analysisCancellation==null && f.start.Enabled,"envelope cancellation releases its owner before ready");

            round.Capture.PdAdc=new short[round.Capture.Settings.RequestedPicoCount];f.Show();Application.DoEvents();
            var closing=f.SelectPulseEnvelope(0,40);
            check(!closing.IsCompleted && f.exporting,"envelope close test has pending work");
            f.Close();
            check(f.closeAfterStop && !f.IsDisposed && !f.start.Enabled,"close waits for cancelled envelope cleanup");
            PumpInteractionTask(closing);
            check(f.IsDisposed,"envelope finally completes the deferred close");
        }
    }
}
