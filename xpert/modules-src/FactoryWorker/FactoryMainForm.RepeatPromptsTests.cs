using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        internal static string TestRepeatPrompts()
        { return FactoryOfflineAsyncTest.Run(CheckRepeatPrompts); }

        private static async Task<string> CheckRepeatPrompts()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Optional prompt regression: "+why);++checks;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-RepeatPrompts-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fake=new FactoryTests.Fixture();
            fake.Settings.EndAfterPressureCapture=true;fake.Settings.SequenceCount=3;
            fake.Settings.Input.ArtSerial="SIM-ART";fake.Settings.Input.PicoSerial="SIM-PICO";
            fake.Settings.Input.AutoRange=true;
            var round=fake.Run();
            // Managed fake acquisition plus an explicit synthetic, unclipped ADC
            // pulse. No native initialization, enumeration, worker, or device I/O.
            round.Capture.PdAdc[100]=1000;round.Capture.PdAdc[101]=-1000;
            var quality=LocationRangeAdvisor.Evaluate(round.Capture.PdAdc,0,round.Capture.PdAdc.Length,
                round.Capture.MaximumAdc,round.Capture.PicoFullScaleV,round.Capture.Settings.RangeIndex,
                0,round.Capture.Settings.PdCalibrationPcPerV,false);
            int current=round.Capture.Settings.RangeIndex;
            check(round.CaptureOnlyAvailable() && quality.WindowValid && quality.Change && quality.RecommendedIndex<current,
                "complete fake capture yields a finite ordinary lower-range recommendation");
            check(FactoryOptionalPromptPolicy.CanSuppressRange(round,quality),"ordinary valid recommendation is suppressible");
            int nativeLikeCalls=fake.Backend.InitializeCount+fake.Backend.HvHighCount+fake.Backend.OpticalHighCount;
            using(var form=new FactoryMainForm(Path.Combine(root,"isolated-labels")))
            {
                form.smokeMode=true;form.ShowInTaskbar=false;form.Opacity=0;
                check(form.mode.SelectedIndex==0 && form.repeatPrompts.Checked,"default prompt preference remains enabled in isolated simulation UI");
                check(FactoryOptionalPromptPolicy.NeedsRangePrompt(round,quality,form.repeatPrompts.Checked),"enabled ordinary reminder still needs a prompt");
                form.range.SelectedIndex=current;form.repeatPrompts.Checked=false;
                round.DirectoryPath=RepeatPromptRecord(root,"suppressed");
                check(!form.ReviewCapturedRange(round,quality,true),"suppression does not accept a range adjustment");
                var decision=ReadRepeatPromptDecision(round.DirectoryPath);
                check(decision.OptionalPromptSuppressed && decision.Action=="SuppressedKeepCurrent" && !decision.ApplyNextRound,
                    "suppressed decision is explicitly persisted as keep current");
                check(decision.CurrentRangeIndex==current && decision.FinalRangeIndex==current && form.range.SelectedIndex==current,
                    "hidden prompt cannot change actual or UI range");
                check(round.Settings.Input.AutoRange && round.Capture.Settings.AutoRange,"suppression never rewrites captured reminder authorization");
                check(decision.ArtSerial=="SIM-ART" && decision.PicoSerial=="SIM-PICO" && decision.ReminderEnabled && !decision.AutomaticRetryRequested,
                    "suppressed audit retains identities and no output/retry permission");
                check(form.diagnostics.Text.Contains("未将隐藏提示视为同意调档"),"suppressed decision remains visible in diagnostics");

                round.DirectoryPath=RepeatPromptRecord(root,"no-next-round");
                check(!form.ReviewCapturedRange(round,quality,false),"last-round suppression never changes range");
                decision=ReadRepeatPromptDecision(round.DirectoryPath);
                check(decision.OptionalPromptSuppressed && decision.Action=="NoQueuedNextRound" && decision.FinalRangeIndex==current,
                    "no queued round retains its distinct logged outcome");

                round.Settings.Input.AutoRange=false;
                round.DirectoryPath=RepeatPromptRecord(root,"previously-authorized-auto");
                check(form.ReviewCapturedRange(round,quality,true),"existing reminder-off authorization still chooses next-round automatic adjustment");
                decision=ReadRepeatPromptDecision(round.DirectoryPath);
                check(decision.Action=="AutomaticAdjustNextRound" && decision.ApplyNextRound && decision.WasAutomaticDecision &&
                    decision.FinalRangeIndex==quality.RecommendedIndex && !decision.OptionalPromptSuppressed && !decision.ReminderEnabled,
                    "original silent auto-range decision remains accurate and separately logged");
                check(!round.Settings.Input.AutoRange && round.Capture.Settings.RangeIndex==current,
                    "silent next-round recommendation does not rewrite current captured range or reminder setting");
                round.Settings.Input.AutoRange=true;
                form.repeatPrompts.Checked=true;
                check(FactoryOptionalPromptPolicy.NeedsRangePrompt(round,quality,true),"restoring master checkbox restores ordinary prompt");
                check(round.Settings.Input.AutoRange,"restoring display setting does not create or clear range authorization");

                foreach(string hazard in new[]{"invalid-window","maximum-clipped","maximum-insufficient","unlocalized-overflow","window-clipped","expected-above-maximum"})
                {
                    var q=RangeDecisionPolicy.CopyQuality(quality);
                    if(hazard=="invalid-window")q.WindowValid=false;
                    if(hazard=="maximum-clipped")q.AtMaximumAndClipped=true;
                    if(hazard=="maximum-insufficient")q.MaximumRangeInsufficient=true;
                    if(hazard=="unlocalized-overflow")q.DriverOverflowUncertain=true;
                    if(hazard=="window-clipped")q.LocationClippedSamples=1;
                    if(hazard=="expected-above-maximum")q.ExpectedCalibrationVolts=BenchSettings.Ranges.Last()+1;
                    check(!FactoryOptionalPromptPolicy.CanSuppressRange(round,q),hazard+" is not an optional message");
                    check(FactoryOptionalPromptPolicy.NeedsRangePrompt(round,q,false),hazard+" remains visible with master disabled");
                    using(var dialog=new FactoryNextRangeDialog(round,q,current,form.Icon,null,true))
                    {
                        dialog.DoNotShow.Checked=true;
                        check(!dialog.DoNotShow.Enabled && !dialog.DisableOptionalPrompts && !dialog.ApplyNext,
                            hazard+" cannot request suppression or implicitly apply a range");
                    }
                }
                using(var dialog=new FactoryNextRangeDialog(round,quality,current,form.Icon,null,true))
                {
                    check(dialog.DoNotShow.Enabled && !dialog.DoNotShow.Checked && !dialog.DisableOptionalPrompts,
                        "ordinary dialog exposes unchecked suppression preference");
                    dialog.DoNotShow.Checked=true;
                    check(dialog.DisableOptionalPrompts && !dialog.ApplyNext && !dialog.KeepCurrent && dialog.DialogResult==DialogResult.None,
                        "checking do-not-show is not a range decision or dialog acceptance");
                    check(dialog.AcceptButton==null,"Enter has no default range-change acceptance");
                }

                // Exercise the actual modal workflow with an isolated timer that
                // only checks the optional checkbox and closes this owned dialog.
                form.Show();round.DirectoryPath=RepeatPromptRecord(root,"modal-do-not-show-close");
                bool sawRange=false,modalTimedOut=false;
                using(var closeDialog=new System.Windows.Forms.Timer {Interval=25})
                {
                    var watch=Stopwatch.StartNew();
                    closeDialog.Tick+=(s,e)=>
                    {
                        foreach(var dialog in form.OwnedForms.OfType<FactoryNextRangeDialog>().ToArray())
                        {
                            sawRange=true;dialog.DoNotShow.Checked=true;dialog.Close();closeDialog.Stop();
                        }
                        if(watch.ElapsedMilliseconds>3000)
                        {modalTimedOut=true;foreach(Form dialog in form.OwnedForms.ToArray())dialog.Close();closeDialog.Stop();}
                    };
                    closeDialog.Start();
                    check(!form.ReviewCapturedRange(round,quality,true),"closing an optional range popup does not apply it");
                }
                check(sawRange && !modalTimedOut && !form.repeatPrompts.Checked,"actual dialog do-not-show checkbox updates master only after closing");
                decision=ReadRepeatPromptDecision(round.DirectoryPath);
                check(decision.DisableOptionalPromptsRequested && !decision.ApplyNextRound && decision.Action=="KeepCurrent" &&
                    decision.FinalRangeIndex==current && round.Settings.Input.AutoRange,
                    "actual close saves display request without fabricating adjustment acceptance");

                string notice="fixture-only optional information; no device operation";
                int owned=form.OwnedForms.Length;
                form.ShowOptionalNotice(form,"fixture notice",notice);
                check(form.status.Text==notice && form.diagnostics.Text.Contains("OPTIONAL_NOTICE") && form.diagnostics.Text.Contains(notice),
                    "disabled information popup still publishes status and diagnostic entry");
                check(form.OwnedForms.Length==owned,"disabled information notice opens no window");

                var realPolicyOnly=round.Settings.Clone();realPolicyOnly.Simulation=false;
                realPolicyOnly.FeedbackWiringConfirmed=false;realPolicyOnly.IndependentDischargeConfirmed=false;
                await CheckNextRoundPromptPresentations(form,realPolicyOnly,check,root);
                check(round.Settings.Input.AutoRange,"confirmation display choice leaves captured range authorization unchanged");
                form.Hide();
            }
            check(fake.Backend.InitializeCount+fake.Backend.HvHighCount+fake.Backend.OpticalHighCount==nativeLikeCalls,
                "all UI prompt tests made no additional acquisition or output calls, including managed fake backend");
            return "Optional repeat prompts: "+checks+" checks PASS; keep-current/silent authorization/audit, mandatory warnings, standalone per-round confirmation independent of ordinary preferences and cancellation; HardwareAccessed=False; "+root;
        }

        private static async Task CheckNextRoundPromptPresentations(FactoryMainForm form,FactorySettings next,Action<bool,string> check,string root)
        {
            // Only exercise UI acknowledgements. NEVER Run or launch a worker in
            // this logical real-mode fixture; there is no native/device I/O.
            Control statusRow=form.resultSummary.Parent;
            var existingControls=statusRow.Controls.Cast<Control>().ToArray();
            var originalVisibility=existingControls.ToDictionary(c=>c,c=>c.Visible);
            int originalRowHeight=statusRow.Height;
            check(form.repeatPrompts.Text=="重复普通提示","master label clearly distinguishes optional messages from mandatory confirmation");
            foreach(bool ordinaryPrompts in new[]{true,false})
            {
                form.repeatPrompts.Checked=ordinaryPrompts;
                var pending=form.ConfirmNextRound(2,3,next,CancellationToken.None);
                var dialog=form.OwnedForms.OfType<FactoryNextRoundDialog>().Single();
                check(!pending.IsCompleted && dialog.Visible && dialog.Owner==form && dialog.Icon!=null && dialog.AcceptButton==null,
                    "either optional preference creates a separate owned confirmation requiring an explicit click");
                check(!dialog.Modal && form.Enabled,"confirmation is modeless so the main Stop remains usable");
                check(!DescendantsForPromptTest(dialog).OfType<CheckBox>().Any(),
                    "mandatory confirmation has no misleading do-not-show checkbox");
                check(statusRow.Controls.Count==existingControls.Length && existingControls.All(c=>c.Visible==originalVisibility[c]) &&
                    statusRow.Height==originalRowHeight,"waiting creates no inline panel and does not hide or resize main-page controls");
                var originalSize=form.Size;form.Size=form.MinimumSize;form.PerformLayout();
                check(dialog.Continue.Visible && dialog.Stop.Visible && dialog.Continue.Enabled && dialog.Stop.Enabled,
                    "standalone continuation and stop controls remain usable with minimum main-window size");
                using(var bitmap=new System.Drawing.Bitmap(dialog.Width,dialog.Height))
                {dialog.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,dialog.Size));bitmap.Save(Path.Combine(root,"next-round-standalone-"+ordinaryPrompts+".png"));}
                form.SaveImage(Path.Combine(root,"main-without-inline-"+ordinaryPrompts+".png"));form.Size=originalSize;
                form.repeatPrompts.Checked=!ordinaryPrompts;
                await Task.Delay(80);
                check(!pending.IsCompleted && dialog.Visible,"changing optional preference or waiting never accepts or hides the mandatory confirmation");
                dialog.Close();
                check(!await RepeatPromptCompleted(pending),"closing standalone confirmation denies this round in either preference state");
                CheckNextRoundPromptCleaned(form,check);

                form.repeatPrompts.Checked=ordinaryPrompts;
                pending=form.ConfirmNextRound(2,3,next,CancellationToken.None);
                dialog=form.OwnedForms.OfType<FactoryNextRoundDialog>().Single();
                check(!pending.IsCompleted,"a new round never inherits the previous acknowledgement");
                dialog.Continue.PerformClick();
                check(await RepeatPromptCompleted(pending),"only an explicit standalone continuation accepts the pending round");
                check(dialog.IsDisposed,"accepted confirmation window is disposed");
                CheckNextRoundPromptCleaned(form,check);

                pending=form.ConfirmNextRound(3,3,next,CancellationToken.None);
                form.OwnedForms.OfType<FactoryNextRoundDialog>().Single().Stop.PerformClick();
                check(!await RepeatPromptCompleted(pending),"standalone Stop Queue denies continuation in either preference state");
                CheckNextRoundPromptCleaned(form,check);

                pending=form.ConfirmNextRound(3,3,next,CancellationToken.None);
                form.RequestUserStop();
                check(!await RepeatPromptCompleted(pending),"main Stop denies continuation without requiring an owner cancellation token");
                CheckNextRoundPromptCleaned(form,check);

                using(var cancel=new CancellationTokenSource())
                {
                    pending=form.ConfirmNextRound(3,3,next,cancel.Token);
                    cancel.Cancel();
                    check(!await RepeatPromptCompleted(pending),"cancellation denies and closes standalone confirmation");
                    CheckNextRoundPromptCleaned(form,check);
                }
                using(var cancel=new CancellationTokenSource())
                {
                    form.cancellation=cancel;
                    try
                    {
                        pending=form.ConfirmNextRound(3,3,next,cancel.Token);
                        form.ObserveConnection("LINK_ART_ERROR",FactoryConnectionWatch.Stamp("offline test: disconnected while waiting"),true);
                        check(cancel.IsCancellationRequested && !await RepeatPromptCompleted(pending),
                            "connection fault cancellation denies standalone continuation");
                        CheckNextRoundPromptCleaned(form,check);
                    }
                    finally{form.cancellation=null;}
                }
                check(statusRow.Controls.Count==existingControls.Length && existingControls.All(c=>c.Visible==originalVisibility[c]),
                    "all exit paths leave main-page controls present and visible as before the wait");
            }
            check(!next.IndependentDischargeConfirmed && !next.FeedbackWiringConfirmed,
                "all display paths leave caller settings and persisted approvals untouched");
        }

        private static void CheckNextRoundPromptCleaned(FactoryMainForm form,Action<bool,string> check)
        {
            check(form.rejectNextRound==null && !form.OwnedForms.OfType<FactoryNextRoundDialog>().Any(),
                "wait completion removes the standalone window and active rejection callback");
        }

        private static System.Collections.Generic.IEnumerable<Control> DescendantsForPromptTest(Control parent)
        {
            foreach(Control child in parent.Controls)
            {yield return child;foreach(Control descendant in DescendantsForPromptTest(child))yield return descendant;}
        }

        private static string RepeatPromptRecord(string root,string name)
        {string directory=Path.Combine(root,name);Directory.CreateDirectory(directory);return directory;}
        private static FactoryNextRangeDecision ReadRepeatPromptDecision(string directory)
        {
            string[] paths=Directory.GetFiles(directory,"next-range-decision-*.xml");
            if(paths.Length!=1)throw new Exception("Expected exactly one isolated prompt-decision record: "+directory);
            using(var file=File.OpenRead(paths[0]))return (FactoryNextRangeDecision)new XmlSerializer(typeof(FactoryNextRangeDecision)).Deserialize(file);
        }
        private static async Task<bool> RepeatPromptCompleted(Task<bool> pending)
        {
            if(await Task.WhenAny(pending,Task.Delay(3000))!=pending)throw new Exception("Isolated next-round confirmation did not close promptly.");
            return await pending;
        }
    }
}
