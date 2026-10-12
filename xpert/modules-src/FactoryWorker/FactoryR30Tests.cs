using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryR30Tests
    {
        internal static string Run(){return FactoryOfflineAsyncTest.Run(Check);}
        private static FactoryTests.Fixture Fixture()
        {
            var f=new FactoryTests.Fixture();f.Settings.EndAfterPressureCapture=true;f.Settings.OpticalTimeoutRetry=true;
            f.Settings.VmonAfterHvStartup=true;f.Settings.ZeroBackgroundLimitV=f.Settings.StartupVmonLimitV=500;
            f.Settings.Input.ArtSerial="SIM-ART";f.Settings.Input.PicoSerial="SIM-PICO";f.Settings.Input.ExternalTriggerVolts=3;
            return f;
        }
        private static FactoryRunException Fail(FactoryTests.Fixture f,Action<string,string> report=null)
        {try{f.Run(report);}catch(FactoryRunException ex){return ex;}throw new Exception("Expected managed failure");}
        private static async Task<string> Check()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R30: "+why);++n;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R30-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            // Pure SDK status/callback state injection: no native initialization/calls.
            var p=new PicoInputCapture();int errors=0,busy=0,online=0;
            p.CommunicationError=t=>errors++;p.CommunicationBusy=t=>busy++;p.CommunicationOk=()=>online++;
            var armed=typeof(PicoInputCapture).GetField("<Armed>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic);
            armed.SetValue(p,true);p.HandleTransportStatus(0x27);
            check(errors==0 && busy==1 && online==0,"armed BUSY neither disconnect nor false ONLINE");
            armed.SetValue(p,false);bool rejected=false;try{p.HandleTransportStatus(0x27);}catch(InvalidOperationException){rejected=true;}
            check(rejected && errors==0,"unexpected BUSY stops, without claiming physical disconnect");
            try{p.HandleTransportStatus(0x11);}catch(InvalidOperationException){}
            check(errors==1,"real driver errors not swallowed");p.HandleTransportStatus(0);check(online==1,"only successful ping reports online");
            var type=typeof(PicoInputCapture);type.GetField("handle",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(p,(short)17);
            type.GetField("acquisitionGeneration",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(p,2);armed.SetValue(p,true);
            var ready=type.GetMethod("OnReady",BindingFlags.Instance|BindingFlags.NonPublic);
            ready.Invoke(p,new object[]{(short)17,0U,new IntPtr(1)});check(!p.Ready,"late old-generation callback cannot complete retry");
            ready.Invoke(p,new object[]{(short)18,0U,new IntPtr(2)});check(!p.Ready,"different handle callback ignored");
            ready.Invoke(p,new object[]{(short)17,0U,new IntPtr(2)});check(p.Ready,"current callback accepted");
            // No Dispose: this object contains a TEST-only handle, never a native handle.

            var normal=Fixture();var ok=normal.Run();
            check(ok.CaptureOnlyAvailable() && normal.Backend.OpticalHighCount==1 && !ok.TriggerRecovery.InitialTimeout,"normal round unchanged");
            check(BenchSettings.OpticalHoldMilliseconds==1000 && ok.Settings.Input.TriggerPulseMs==1000,"fixed optical metadata is 1000 ms");
            var normalHigh=normal.Backend.Events.FindIndex(x=>x.Name=="OPTICAL_HIGH");
            var normalLow=normal.Backend.Events.FindIndex(normalHigh+1,x=>x.Name=="OPTICAL_LOW");
            check(normal.Backend.Events[normalLow].At-normal.Backend.Events[normalHigh].At==1000,"normal optical request is exactly 1000 ms in fake clock");
            var retry=Fixture();retry.Settings.Input.TriggerTimeoutMs=6000;retry.Backend.TriggerCompletesOnPulse=2;
            var failed=Fail(retry);var r=failed.PartialResult;
            check(r.TriggerRecovery.InitialWaitMs==3000 && r.TriggerRecovery.SupplementAttempted,"exact 3s protocol independent of legacy input timeout");
            check(retry.Backend.OpticalHighCount==2 && retry.Backend.RetryCount==1 && retry.Backend.HvHighCount==1,"one supplemental light, no source restart");
            var ev=retry.Backend.Events;int stop=ev.FindIndex(x=>x.Name=="STOP_CAPTURE"),prep=ev.FindIndex(x=>x.Name=="RETRY_PREPARE"),arm=ev.FindIndex(prep,x=>x.Name=="ARM_EXT"),pulse=ev.FindIndex(arm,x=>x.Name=="OPTICAL_HIGH");
            check(stop>=0 && prep>stop && arm>prep && pulse>arm,"stop old pair before cooldown/rearm and supplemental pulse");
            int firstHigh=ev.FindIndex(x=>x.Name=="OPTICAL_HIGH"),firstLow=ev.FindIndex(firstHigh+1,x=>x.Name=="OPTICAL_LOW"),retryLow=ev.FindIndex(pulse+1,x=>x.Name=="OPTICAL_LOW");
            check(ev[firstLow].At-ev[firstHigh].At==1000 && ev[retryLow].At-ev[pulse].At==1000,"initial and supplemental high requests share 1000 ms");
            check(ev[prep].At-ev[firstHigh].At==3000,"3s retry deadline still starts at high command return including 1s hold");
            check(r.TriggerRecovery.DiagnosticCaptureSaved && r.Capture!=null && !r.CaptureOnlyAvailable() && !r.Completed && !r.ResidualSafe,"supplement only diagnostic, never completion/safety credit");
            check(r.TriggerRecovery.EligibleForThresholdAdjustment && !r.FaultResidual.Started && !ev.Any(x=>x.Name=="FAULT_READ"),"pure linkage failure eligible, no post AI");
            r.DirectoryPath=Path.Combine(root,"diagnostic");Directory.CreateDirectory(r.DirectoryPath);Program.SaveResult(r,r.DirectoryPath,true);
            var preview=FactoryCapturePreview.Load(r.DirectoryPath);
            check(preview.Result.TriggerRecovery.DiagnosticCaptureSaved && preview.Result.Capture!=null,"saved diagnostic distinction survives preview");
            var forever=Fixture();forever.Backend.NeverComplete=true;var never=Fail(forever).PartialResult;
            check(forever.Backend.OpticalHighCount==2 && forever.Backend.RetryCount==1 && never.TriggerRecovery.SupplementWaitMs==3000 && never.Capture==null,"second timeout bounded; no third light");
            var partial=Fixture();partial.Backend.OnlyArtCompletes=true;var part=Fail(partial).PartialResult;
            check(partial.Backend.OpticalHighCount==1 && partial.Backend.RetryCount==0 && part.TriggerRecovery.ArtCompleteAtTimeout && !part.TriggerRecovery.PicoCompleteAtTimeout,"one-card completion forbids supplementation/mixing");
            foreach(string mode in new[]{"cancel","estop","disconnect","overcurrent","rearm","stop","read","dispose","shutdown","cancel-before-light"})
            {
                var f=Fixture();f.Backend.TriggerCompletesOnPulse=2;long fullCurrentAt=-1;
                if(mode=="estop")f.Backend.EmergencyRule=b=>b.OpticalHighCount==0 || f.Clock.ElapsedMilliseconds<b.OpticalAt+1100;
                if(mode=="disconnect")f.Backend.EmergencyRule=b=>{if(b.OpticalHighCount>0 && f.Clock.ElapsedMilliseconds>=b.OpticalAt+1100)throw new IOException("fake USB lost");return true;};
                // Current alone, while voltage is rising, is no longer a trip.
                // Exercise the current contract with a genuinely stalled
                // selected voltage and continuous full-scale current instead.
                if(mode=="overcurrent")f.Backend.FeedbackRule=(b,v)=>{if(b.Hv && b.Ao>0){
                    if(fullCurrentAt<0)fullCurrentAt=f.Clock.ElapsedMilliseconds;
                    v.ImonRawV=5;v.DividerRawV=0;v.VmonRawV=0;
                }return v;};
                f.Backend.FailRetry=mode=="rearm";f.Backend.FailStop=mode=="stop";f.Backend.FailRead=mode=="read";f.Backend.FailDispose=mode=="dispose";
                var bad=Fail(f,(k,t)=>{if(mode=="cancel" && k=="WAIT")f.Cancellation.Cancel();
                    if(mode=="shutdown" && k=="TRIGGER_LINKAGE_TIMEOUT")f.Backend.FailShutdown=true;
                    if(mode=="cancel-before-light" && k=="TRIGGER_SUPPLEMENT")f.Cancellation.Cancel();});
                check(!bad.PartialResult.TriggerRecovery.EligibleForThresholdAdjustment && !bad.PartialResult.Completed,mode+" cannot lower EXT or become success");
                if(mode=="overcurrent")check(fullCurrentAt>=0 && f.Clock.ElapsedMilliseconds-fullCurrentAt>=FactoryRampCurrentGuard.RequiredDurationMs &&
                    bad.ToString().Contains("选中电压连续3秒无有效增长且AI0电流原始反馈连续3秒达到5 V") && f.Backend.ReadCount==0,
                    "CURRENT fault requires both real fake-clock 3-second conditions and cannot accept a capture");
                check(f.Backend.HvHighCount==1 && f.Backend.DisposeCount==1 && !f.Backend.Hv && !f.Backend.Optical && f.Backend.Ao==0,mode+" no HV restart; cleanup attempted");
                if(mode!="read" && mode!="dispose")check(f.Backend.OpticalHighCount<=1,mode+" no supplemental positive output");
            }
            var fault=Fixture();fault.Backend.FailArm=true;int readsAfterOff=0;
            fault.Backend.FeedbackRule=(b,v)=>{if(b.HvHighCount>0&&!b.Hv){readsAfterOff++;v.DividerRawV=.01;}return v;};
            string traceDir=Path.Combine(root,"ai-fault");Directory.CreateDirectory(traceDir);
            try {using(var logged=new FactoryAiRecordingBackend(fault.Backend,traceDir,fault.Settings,fault.Clock))
                FactoryRunner.Run(fault.Settings,FactoryRunKind.Pressure,logged,fault.Clock,(k,t)=>logged.ObserveStage(k),CancellationToken.None);}
            catch(FactoryRunException){}
            check(readsAfterOff==0 && !fault.Backend.Events.Any(x=>x.Name=="FAULT_READ"),"10mV fault noise cannot start residual or diagnostic AI acquisition");

            foreach(int timeoutMs in new[]{1000,1060,1061})
            {
                var f=Fixture();f.Settings.Input.TriggerTimeoutMs=timeoutMs;bool accepted=true;
                try{f.Settings.Validate(FactoryRunKind.Pressure);}catch(ArgumentException){accepted=false;}
                check(accepted==(timeoutMs==1061),"factory timeout covers 1000 ms plus actual 60 ms capture, strict boundary "+timeoutMs);
            }
            var shared=new BenchSettings {AutomaticSharedTrigger=true,TriggerPulseMs=1000,TriggerTimeoutMs=1000};
            bool sharedRejected=false;try{shared.ValidateForNewRun();}catch(ArgumentException){sharedRejected=true;}
            check(sharedRejected,"shared helper cannot accept timeout at 1000 ms hold boundary");
            shared.TriggerTimeoutMs=1001;shared.ValidateForNewRun();check(true,"shared helper accepts larger timeout with 1000 ms request");
            shared.TriggerPulseMs=1500;sharedRejected=false;try{shared.ValidateForNewRun();}catch(ArgumentException){sharedRejected=true;}
            check(sharedRejected,"historical 1500 ms shared pulse cannot authorize a new output");
            shared.Validate();check(shared.TriggerPulseMs==1500,"historical metadata validation leaves the recorded 1500 ms unchanged");

            int current=Array.IndexOf(BenchSettings.Ranges,1.0);
            foreach(double fraction in new[]{.24999,.25,.274666,.5,.79999,.80,.9})
            {
                var q=new LocationRangeResult{WindowValid=true,LocationPeakFraction=fraction,RecommendedIndex=current-1,Change=true};
                FactoryRangeRetention.Apply(q,current);bool retain=fraction>=.25&&fraction<.8;
                check((q.RecommendedIndex==current)==retain,"25-80 retain boundary "+fraction);
            }
            foreach(string hazard in new[]{"clip","maximum","overflow","invalid","uprange"})
            {
                var q=new LocationRangeResult{WindowValid=hazard!="invalid",LocationPeakFraction=.3,RecommendedIndex=hazard=="uprange"?current+1:current-1,Change=true,
                    LocationClippedSamples=hazard=="clip"?1:0,MaximumRangeInsufficient=hazard=="maximum",DriverOverflowUncertain=hazard=="overflow"};
                int before=q.RecommendedIndex;FactoryRangeRetention.Apply(q,current);check(q.RecommendedIndex==before && q.Change,"retain does not clear "+hazard);
            }
            var realAdvisor=LocationRangeAdvisor.Evaluate(new short[]{9000,-9000,0},0,3,32767,1,current,0,2100,false);
            FactoryRangeRetention.Apply(realAdvisor,current);check(realAdvisor.RecommendedIndex==current && !realAdvisor.Change,"audit 27.47% input now retains 1V");

            var tracker=new FactoryTriggerThresholdPolicy();int record=0;
            Func<double,FactoryResult> item=old=>{var x=new FactoryResult{Settings=Fixture().Settings,Kind=FactoryRunKind.Pressure,
                TriggerRecovery=new FactoryTriggerRecoveryRecord{InitialTimeout=true,EligibleForThresholdAdjustment=true},DirectoryPath=Path.Combine(root,"decision-"+(++record))};
                x.Settings.Input.ExternalTriggerVolts=old;Directory.CreateDirectory(x.DirectoryPath);return x;};
            var one=tracker.Failure(item(3));var two=tracker.Failure(item(3));var third=tracker.Failure(item(3));
            check(!one.Changed && !two.Changed && third.NewVolts==2.25 && third.Changed,"only third linkage failure lowers to 75%");
            check(File.Exists(Path.Combine(third.RecordDirectory,"trigger-threshold-decision.xml")),"decision persisted before parameter change");
            tracker.Failure(item(.6));tracker.Failure(item(.6));var floor=tracker.Failure(item(.6));check(floor.NewVolts==.5,"floor .5V");
            tracker.Success(Fixture().Settings);check(tracker.Failure(item(.5)).ConsecutiveFailures==1,"normal success resets streak");
            var same=item(.5);tracker.Failure(same);rejected=false;try{tracker.Failure(same);}catch(InvalidOperationException){rejected=true;}check(rejected,"same round cannot count twice");
            var other=item(.5);other.Settings.Input.PicoSerial="OTHER";check(tracker.Failure(other).ConsecutiveFailures==1,"exact card pair isolation");
            var invalid=item(3);invalid.TriggerRecovery.EligibleForThresholdAdjustment=false;rejected=false;try{tracker.Failure(invalid);}catch(InvalidOperationException){rejected=true;}check(rejected,"non-trigger fault excluded");
            var malformed=item(3);File.WriteAllText(Path.Combine(malformed.DirectoryPath,"trigger-threshold-decision.xml"),"existing evidence");rejected=false;try{tracker.Failure(malformed);}catch(IOException){rejected=true;}check(rejected,"failed audit write cannot change counter/settings");

            var settings=Fixture().Settings;settings.Input.OscillationCircuitConnected=true;settings.Input.RangeIndex=10;
            var starts=new List<long>();var completedAt=new List<long>();var watch=Stopwatch.StartNew();var reports=new List<string>();
            var progress=new FactorySequenceProgress(settings,3);
            int done=await FactoryPressureSequence.Run(settings,3,s=>{starts.Add(watch.ElapsedMilliseconds);var v=FactoryPressureSequenceTests.Capture(s);completedAt.Add(watch.ElapsedMilliseconds);return Task.FromResult(v);},
                null,null,CancellationToken.None,null,progress,null,(k,t)=>reports.Add(k));
            check(done==3 && progress.Completed==3 && progress.Remaining==0,"continuous counts reach requested total");
            check(starts[1]-completedAt[0]>=1000 && starts[2]-completedAt[1]>=1000,"each next capture starts at least 1s after prior completion");
            check(reports.Count(x=>x=="SEQUENCE_INTERVAL_WAIT")==2,"no extra delay after terminal round");
            using(var cancel=new CancellationTokenSource())
            {
                int calls=0;bool cancelled=false;var cp=new FactorySequenceProgress(settings,3);
                try{await FactoryPressureSequence.Run(settings,3,s=>{calls++;return Task.FromResult(FactoryPressureSequenceTests.Capture(s));},null,null,cancel.Token,null,cp,null,
                    (k,t)=>{if(k=="SEQUENCE_INTERVAL_WAIT")cancel.Cancel();});}catch(OperationCanceledException){cancelled=true;}
                check(cancelled && calls==1 && cp.Completed==1 && cp.Remaining==2,"stop during 1s interval never launches next round or removes completed credit");
            }
            return "V1.0.30: "+n+" checks PASS; BUSY/epochs, 3s bounded optical diagnostic, abort paths, 25% retention, threshold audit, actual inter-round delay; HardwareAccessed=False; "+root;
        }
    }
}
