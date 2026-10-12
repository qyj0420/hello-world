using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal static class FactorySequenceResumeTests
    {
        internal static string Run() { return FactoryOfflineAsyncTest.Run(Check); }
        private static async Task<string> Check()
        {
            int checks=0;
            Action<bool,string> verify=(ok,text)=>{if(!ok)throw new Exception("R28 resume: "+text);++checks;};
            var first=new FactoryTests.Fixture().Settings;
            first.EndAfterPressureCapture=true;first.Input.OscillationCircuitConnected=true;
            first.Input.ArtSerial="SIM-ART";first.Input.PicoSerial="SIM-PICO";first.Input.RangeIndex=10;
            var progress=new FactorySequenceProgress(first,5);
            var seen=new List<FactorySettings>(); FactorySettings interrupted=null;
            var remainingUpdates=new List<int>();
            try
            {
                await FactoryPressureSequence.Run(first,5,s=>{
                    seen.Add(s.Clone());
                    if(s.SequenceRound==3){interrupted=s.Clone();progress.Observe("EMERGENCY_ACTIVE");throw new InvalidOperationException("simulated emergency");}
                    return Task.FromResult(FactoryPressureSequenceTests.Capture(s));
                },null,null,CancellationToken.None,null,progress,()=>remainingUpdates.Add(progress.Remaining));
            }
            catch(InvalidOperationException ex){verify(ex.Message=="simulated emergency","original fault is preserved");}
            verify(progress.Completed==2 && progress.NextRound==3 && progress.Remaining==3 && seen.Count==3,"failed round not completed or retried");
            verify(String.Join(",",remainingUpdates)=="4,3","remaining updates only after successful capture, no decrement on interrupted round");
            verify(progress.CanResume && !progress.Running && progress.Attempt==1,"checkpoint created only after queue ends");
            var ui=first.Clone();ui.Input.RangeIndex=interrupted.Input.RangeIndex;
            progress.RememberStoppedUi(ui);
            progress.Observe("EMERGENCY_RELEASED");
            verify(progress.CanResume && !progress.Running && seen.Count==3,"emergency release cannot dispatch or consume checkpoint");
            verify(progress.Matches(ui,5),"same effective request compatible");
            foreach(Action<FactorySettings> change in new Action<FactorySettings>[] {
                s=>s.TargetKv+=.1,s=>s.PressureVPerV+=1,s=>s.Input.DividerVPerV+=1,
                s=>s.Input.ArtSerial="different",s=>s.Input.PicoSerial="different",s=>s.Simulation=false,
                s=>s.VmonFullScaleKv+=1,s=>s.CurrentLimitMa-=1,s=>s.Input.AoStepV/=2,
                s=>s.Input.RangeIndex=s.Input.RangeIndex==10?9:10,s=>s.Input.CaptureDurationMs+=1,
                s=>s.Input.AutoRange=!s.Input.AutoRange,s=>s.FeedbackWindowMs+=1,s=>s.Input.OscillationCircuitConnected=false })
            {
                var changed=ui.Clone();change(changed);
                verify(!progress.Matches(changed,5),"parameter/device change prohibits resume");
            }
            verify(!progress.Matches(ui,4),"count change prohibits resume");
            var invalidDuration=ui.Clone();invalidDuration.Input.CaptureDurationMs=61;
            verify(!progress.Matches(invalidDuration,5),"invalid 61 ms edit is a nonmatching checkpoint, not a UI exception");
            bool invalidResumeRejected=false;
            try{progress.ResumeRequest(invalidDuration,5);}catch(InvalidOperationException){invalidResumeRejected=true;}
            verify(invalidResumeRejected && progress.Completed==2 && !progress.Running,
                "61 ms cannot produce a resumed request or advance completed rounds");
            using(var dialog=new FactorySequenceResumeDialog(progress,invalidDuration,5,null))
                verify(!dialog.ContinueButton.Enabled && dialog.RestartButton.Enabled,
                    "invalid duration offers no continue action without crashing the resume dialog");
            verify(progress.Matches(ui,5) && progress.CanResume && invalidDuration.Input.CaptureDurationMs==61,
                "an invalid probe neither mutates the checkpoint nor silently clamps edited parameters");
            using(var dialog=new FactorySequenceResumeDialog(progress,ui,5,null))
            {
                verify(dialog.Choice==FactorySequenceChoice.Cancel && dialog.AcceptButton==dialog.CancelChoice && dialog.CancelButton==dialog.CancelChoice,"default Enter/Escape is cancel");
                verify(dialog.ContinueButton.Enabled,"same-parameter resume offered");
                verify(progress.CanResume && !progress.Running,"merely viewing/closing dialog cannot dispatch");
            }
            using(var dialog=new FactorySequenceResumeDialog(progress,ui,4,null))
                verify(!dialog.ContinueButton.Enabled && dialog.RestartButton.Enabled,"changed settings offer restart/cancel only");
            var resume=progress.ResumeRequest(ui,5);
            verify(!resume.FeedbackWiringConfirmed && !resume.IndependentDischargeConfirmed,"no prior-round operator approval carried");
            verify(resume.Input.RangeIndex==interrupted.Input.RangeIndex,"resume preserves evaluated range of interrupted round");
            string id=progress.Id;seen.Clear();
            int done=await FactoryPressureSequence.Run(resume,5,s=>{seen.Add(s.Clone());return Task.FromResult(FactoryPressureSequenceTests.Capture(s));},null,null,CancellationToken.None,null,progress,()=>remainingUpdates.Add(progress.Remaining));
            verify(String.Join(",",remainingUpdates)=="4,3,2,1,0","resumed countdown completes remaining sequence without reset");
            verify(done==5 && seen.Count==3 && seen[0].SequenceRound==3 && seen[2].SequenceRound==5,"resume executes remaining rounds exactly once");
            verify(seen.TrueForAll(s=>s.SequenceId==id && s.SequenceAttempt==2 && s.SequenceCount==5),"same identity and new attempt are recorded");
            verify(!progress.CanResume && progress.Completed==5,"completed sequence not resumable");
            verify(new FactorySequenceProgress(ui,5).Id!=id,"restart creates distinct identity");

            // Real-mode policy fixture: no drivers, all captures are synthetic.
            var real=first.Clone();real.Simulation=false;real.VmonAfterHvStartup=true;
            real=FactoryStartConfirmation.Accept(real,FactoryRunKind.Pressure,true);
            var realProgress=new FactorySequenceProgress(real,2);
            try { await FactoryPressureSequence.Run(real,2,s=>{realProgress.Observe("EMERGENCY_ACTIVE");throw new InvalidOperationException("fixture");},null,null,CancellationToken.None,null,realProgress); }
            catch(InvalidOperationException){}
            realProgress.RememberStoppedUi(real);
            var realResume=realProgress.ResumeRequest(real,2);
            int dispatched=0; bool rejected=false;
            try { await FactoryPressureSequence.Run(realResume,2,s=>{++dispatched;return Task.FromResult(FactoryPressureSequenceTests.Capture(s));},null,null,CancellationToken.None,null,realProgress); }
            catch(ArgumentException){rejected=true;}
            verify(rejected && dispatched==0 && realProgress.CanResume,"fresh real-mode confirmation required before resumed worker");
            realResume=FactoryStartConfirmation.Accept(realResume,FactoryRunKind.Pressure,true);
            done=await FactoryPressureSequence.Run(realResume,2,s=>{++dispatched;return Task.FromResult(FactoryPressureSequenceTests.Capture(s));},
                (i,n,s)=>{verify(!s.FeedbackWiringConfirmed && !s.IndependentDischargeConfirmed,"later resumed round needs new confirmation");return Task.FromResult(false);},
                null,CancellationToken.None,null,realProgress);
            verify(done==1 && dispatched==1 && !realProgress.CanResume,"declining next round stops, no new emergency checkpoint");
            foreach(bool cancel in new[]{false,true})
            {
                var other=new FactorySequenceProgress(first,2);bool caught=false;
                try { await FactoryPressureSequence.Run(first,2,s=>{if(cancel)throw new OperationCanceledException();throw new InvalidOperationException("non-emergency");},null,null,CancellationToken.None,null,other); }
                catch(Exception){caught=true;}
                verify(caught && !other.CanResume && !other.Running,"non-emergency failure/cancel does not offer emergency continuation");
            }
            var repeat=new FactorySequenceProgress(first,2);
            for(int attempt=1;attempt<=2;attempt++)
            {
                var request=attempt==1?first:repeat.ResumeRequest(first,2);
                try { await FactoryPressureSequence.Run(request,2,s=>{repeat.Observe("EMERGENCY_ACTIVE");throw new InvalidOperationException("repeat emergency");},null,null,CancellationToken.None,null,repeat); }
                catch(InvalidOperationException){}
                repeat.RememberStoppedUi(first);
                verify(repeat.CanResume && repeat.Completed==0 && repeat.Attempt==attempt,"second emergency preserves logical count and new attempt");
            }
            var badCapture=new FactorySequenceProgress(first,2);
            try { await FactoryPressureSequence.Run(first,2,s=>{var r=FactoryPressureSequenceTests.Capture(s);r.Capture.Simulation=!s.Simulation;return Task.FromResult(r);},null,null,CancellationToken.None,null,badCapture); }
            catch(InvalidOperationException){}
            catch(System.IO.InvalidDataException){}
            verify(badCapture.Completed==0,"identity-invalid capture never increments completion");
            string prompt=FactoryPressureStartPrompt.Text(real,5,true);
            verify(prompt.Length<300 && prompt.Contains("人员已撤离") && prompt.Contains("5 轮"),"ordinary start compact and preserves acknowledgement");
            return "R28 emergency resume/start prompt: "+checks+" checks PASS; HardwareAccessed=False";
        }
    }
}
