using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Latest nonzero-pressure rule explicitly REPLACES the historical 4%
    // workflow tests. No native entry point, SDK or real device is executed.
    internal static class FactoryNoResidualPressureTests
    {
        internal static string FixtureRoot=null;
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("No residual pressure: "+why);n++;};
            foreach(bool div in new[]{true,false})foreach(bool oldEnd in new[]{false,true})foreach(bool postHv in new[]{false,true})
            {
                var f=new FactoryTests.Fixture();var s=f.Settings;
                s.DividerOnlyFeedback=div;s.EndAfterPressureCapture=oldEnd;s.VmonAfterHvStartup=postHv;
                s.EndResidualCheckEnabled=true;s.EndResidualThresholdV=1200;s.TargetHoldChecks=2;
                if(oldEnd){s.SequenceId=Guid.NewGuid().ToString("N");s.FinalSequenceAi4Observation=true;}
                s.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                string stage="";int reads=0;
                f.Backend.FeedbackRule=(b,x)=>{
                    if(stage!="STEP_HV_UP_RUNNING"||!b.Hv||b.Armed||b.ReadCount!=0)
                        throw new Exception("Unexpected residual/background read at "+stage);
                    reads++;x.ImonRawV=10.06;if(div)x.VmonRawV=Double.NaN;else x.DividerRawV=Double.NaN;return x;
                };
                var keys=new List<string>();
                var r=f.Run((k,t)=>{keys.Add(k);if(k.StartsWith("STEP_")&&k.EndsWith("_RUNNING"))stage=k;});
                check(reads==r.Ramp.Count+r.TargetHoldFeedback.Count&&reads>0,"only actual ramp and hold read AI");
                check(r.CaptureOnlyAvailable()&&!r.CompleteCaptureAvailable()&&!r.Completed&&!r.ResidualSafe,"acquisition receipt never claims residual safe");
                check(r.StartupFeedback.Count==0&&!r.FaultResidual.Started&&r.FinalAi4Observation==null,"no fabricated initial/end/fault/final observations");
                check(keys.Contains("STEP_SAFE_SKIPPED")&&keys.Contains("HV_ZERO_FEEDBACK_SKIPPED")&&keys.Contains("STEP_DISCHARGE_SKIPPED"),"skipped stages logged honestly");
                check(!keys.Contains("RESIDUAL")&&!keys.Contains("INITIAL_FEEDBACK")&&!keys.Contains("STEP_DISCHARGE_PASSED"),"no measurement or safety-pass logs");
                check(!oldEnd||keys.Contains("STEP_FINAL_AI4_SKIPPED"),"old final-AI4 flag cannot create an after-pressure sample");
                check(f.Backend.HvHighCount==1&&f.Backend.OpticalHighCount==1&&!f.Backend.Hv&&f.Backend.Ao==0&&f.Backend.DisposeCount==1,"actual output sequence and cleanup remain");
                check(r.SafetyDetail.Contains("独立安全放电"),"independent physical discharge is explicit");
                var loaded=UnifiedPressureBridge.Read<FactorySettings>(UnifiedPressureBridge.Xml(r.Settings));
                check(!loaded.PressureFeedbackContext&&!loaded.SkipPressureResidualMeasurement,"XML does not carry runtime pressure purpose");
                loaded.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                check(loaded.SkipPressureResidualMeasurement&&!loaded.ShouldCheckEndResidual,"old XML cannot bypass derived policy");
                r.Settings=UnifiedPressureBridge.Read<FactorySettings>(UnifiedPressureBridge.Xml(loaded));
                check(r.CaptureOnlyAvailable(),"archive reload retains acquisition-only contract");
                check(!NativeFactoryBackend.StartMonitorBeforeRamp(s,false),"native monitor remains dormant before actual ramp");
                check(!NativeFactoryBackend.RestoreMonitorAfterCapture(s,false,false)&&!NativeFactoryBackend.RestoreMonitorAfterCapture(s,false,true),"neither normal nor fault recreates monitor");
            }
            foreach(string fault in new[]{"arm","read","stop","dispose","shutdown","io","cancel","emergency","ceiling","invalid","timeout"})
            {
                var f=new FactoryTests.Fixture();var s=f.Settings;s.DividerOnlyFeedback=true;
                s.EndResidualCheckEnabled=true;s.EndResidualThresholdV=1200;
                f.Backend.FailArm=fault=="arm";f.Backend.FailRead=fault=="read";f.Backend.FailStop=fault=="stop";
                f.Backend.FailDispose=fault=="dispose";f.Backend.FailShutdown=fault=="shutdown";
                if(fault=="timeout")s.RampTimeoutSeconds=.05;
                int extraReads=0;
                f.Backend.FeedbackRule=(b,x)=>{
                    if(!b.Hv||b.ReadCount>0)extraReads++;
                    if(fault=="io"&&b.Ao>0)throw new IOException("fake SDK read failed");
                    if(fault=="cancel"&&b.Ao>0)f.Cancellation.Cancel();
                    if(fault=="ceiling")x.DividerRawV=10000/s.Input.DividerVPerV;
                    if(fault=="invalid")x.DividerRawV=Double.NaN;
                    if(fault=="timeout")x.DividerRawV=0;
                    return x;
                };
                if(fault=="emergency")f.Backend.EmergencyRule=b=>!b.Hv;
                var keys=new List<string>();FactoryRunException error=Fail(()=>f.Run((k,t)=>keys.Add(k))) as FactoryRunException;
                check(error!=null&&!error.PartialResult.CaptureOnlyAvailable(),"failed "+fault+" cannot advance acquisition");
                check(extraReads==0&&!error.PartialResult.FaultResidual.Started&&error.PartialResult.FaultResidual.Feedback.Count==0,"failed "+fault+" does not measure residual");
                check(!f.Backend.Events.Any(x=>x.Name=="FAULT_READ"||x.Name=="FAULT_MONITOR"),"failed "+fault+" no hidden fault AI owner");
                check(!f.Backend.Hv&&f.Backend.Ao==0&&f.Backend.DisposeCount==1,"failed "+fault+" shutdown/ownership cleanup retained");
            }
            // Production shutdown logger used with a fake backend: a managed
            // ramp error occurs while the original monitor would still exist.
            string root=String.IsNullOrEmpty(FixtureRoot)?Path.Combine(Path.GetTempPath(),"HVX-NoResidual-"+Guid.NewGuid().ToString("N")):FixtureRoot;
            foreach(bool fail in new[]{false,true})
            {
                string dir=Path.Combine(root,fail?"fault":"normal");Directory.CreateDirectory(dir);
                var f=new FactoryTests.Fixture();int postReads=0;f.Settings.DividerOnlyFeedback=true;
                f.Backend.FeedbackRule=(b,x)=>{if(!b.Hv||b.ReadCount>0)postReads++;if(fail&&b.Ao>0)throw new IOException("fake logger-path IO failure");return x;};
                var logger=new FactoryAiRecordingBackend(f.Backend,dir,f.Settings,f.Clock);
                Action action=()=>FactoryRunner.Run(f.Settings,FactoryRunKind.Pressure,logger,f.Clock,(k,t)=>logger.ObserveStage(k),CancellationToken.None);
                if(fail)Fail(action);else action();
                check(postReads==0,"shutdown logger never appends residual sampling "+fail);
                check(File.ReadAllText(Path.Combine(dir,"ai-monitor-events.csv")).Contains("SHUTDOWN_AI_SKIPPED"),"trace retains honest skipped record "+fail);
                var record=UnifiedPressureBridge.Read<FactoryShutdownRecord>(File.ReadAllText(Path.Combine(dir,"shutdown-diagnostic.xml")));
                check(record.OffCallsReturned&&!record.PostReadFinished&&record.Status.Contains("SHUTDOWN_AI_SKIPPED"),"actual shutdown receipt, no fake post-read completion "+fail);
            }
            var direct=new FactoryTests.Fixture();direct.Backend.FeedbackRule=(b,x)=>{throw new Exception("forbidden residual read");};
            direct.Settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            var result=new FactoryResult{Settings=direct.Settings,Kind=FactoryRunKind.Pressure};
            var log=new List<string>();
            FactoryRunner.WaitInitialResidual(direct.Settings,direct.Backend,direct.Clock,result,(k,t)=>log.Add(k),CancellationToken.None);
            FactoryFaultResidual.Observe(direct.Settings,direct.Backend,direct.Clock,result,(k,t)=>log.Add(k));
            check(result.StartupFeedback.Count==0&&!result.FaultResidual.Started&&direct.Clock.ElapsedMilliseconds==0,"direct helper calls cannot enable pressure residual measurement");
            check(log.Contains("INITIAL_FEEDBACK_SKIPPED")&&log.Contains("FAULT_RESIDUAL_SKIPPED"),"direct bypass attempts marked skipped");
            var zero=direct.Settings.Clone();zero.TargetKv=0;
            check(!zero.SkipPressureResidualMeasurement&&zero.ShouldCheckEndResidual,"zero optical/calibration independent policy not silently changed");
            check(NativeFactoryBackend.StartMonitorBeforeRamp(zero,true)&&NativeFactoryBackend.RestoreMonitorAfterCapture(zero,true,true),"calibration owns its existing guarded input lifecycle");
            var invalidCalibration=direct.Settings.Clone();invalidCalibration.TargetKv=1;
            check(Fail(()=>invalidCalibration.ValidateBeforeOperatorConfirmation(FactoryRunKind.Calibration)) is ArgumentException,"nonzero calibration target rejected before any device operation");
            check(!invalidCalibration.PressureFeedbackContext&&!invalidCalibration.SkipPressureResidualMeasurement,"calibration purpose clears previous pressure prohibition even on invalid target");
            invalidCalibration.Simulation=false;
            check(Fail(()=>FactoryNativeSessionPolicy.PrepareRound(invalidCalibration,true)) is ArgumentException,"native calibration preflight also rejects nonzero target before SDK");
            zero.ValidateBeforeOperatorConfirmation(FactoryRunKind.Calibration);
            check(!zero.PressureFeedbackContext&&!zero.SkipPressureResidualMeasurement&&zero.ShouldCheckEndResidual,"valid zero calibration keeps original guarded diagnostic policy");
            var calibrationFixture=new FactoryTests.Fixture();var calibrationKeys=new List<string>();
            var calibrationResult=calibrationFixture.Run((k,t)=>calibrationKeys.Add(k),FactoryRunKind.Calibration);
            check(calibrationResult.Settings.TargetKv==0&&!calibrationResult.Settings.SkipPressureResidualMeasurement,"actual calibration runner normalizes inherited target and binds calibration purpose");
            check(calibrationResult.StartupFeedback.Count>0&&!calibrationKeys.Contains("STEP_SAFE_SKIPPED")&&!calibrationKeys.Contains("STEP_DISCHARGE_SKIPPED"),"calibration diagnostic reads not skipped by pressure policy");
            var native=new NativeFactoryBackend();Set(native,"initialized",true);Set(native,"settings",direct.Settings.Clone());
            foreach(Action read in new Action[]{()=>native.ReadFeedback(),native.ReadShutdownDiagnostic,native.PrepareFaultMonitoring,()=>native.ReadFaultFeedback(),()=>native.ReadFinalAi4Observation(CancellationToken.None)})
                check(Fail(read) is InvalidOperationException,"native residual entry rejects before any SDK call");
            // Synthetic lifecycle identifiers are used ONLY with injected
            // managed task IO. No SDK handle exists and no SDK method is called.
            foreach(bool failStop in new[]{false,true})
            {
                var io=new TaskIo { FailStop=failStop };var owned=new NativeFactoryBackend(io);
                Set(owned,"initialized",true);Set(owned,"identitiesVerified",true);Set(owned,"settings",direct.Settings.Clone());
                int id=1;foreach(string name in new[]{"optical","hv","ao","indicator","currentMode","monitor"})
                {var task=Get(owned,name);Set(task,"Handle",new IntPtr(id++));Set(task,"Configured",true);Set(task,"Started",true);}
                ((FactoryHvCommandState)Get(owned,"hvCommands")).Write(true,high=>{});
                if(failStop)Fail(owned.Shutdown);else owned.Shutdown();
                check(io.Events.Count==6&&io.Events[5]=="STOP:6", "all five disable writes precede monitor stop "+failStop);
                check(io.Events.Take(5).All(x=>x.StartsWith("ZERO:")),"no new positive output while disabling "+failStop);
                check((IntPtr)Get(Get(owned,"monitor"),"Handle")==new IntPtr(6),"Stop retains ownership until actual Clear "+failStop);
                check((bool)Get(Get(owned,"monitor"),"Started")==failStop&&owned.Faulted==failStop,"Stop success/failure is not fabricated "+failStop);
            }
            var manual=new FactoryTests.Fixture();int manualReads=0;
            manual.Backend.FeedbackRule=(b,x)=>{manualReads++;throw new Exception("manual pre/post residual read forbidden");};
            using(var engine=new FactoryManualEngine(manual.Backend,high=>{},manual.Clock,(k,t)=>{},manual.Cancellation.Token))
            {engine.Initialize(manual.Settings);engine.Tick();engine.HvOn(1);engine.Tick();engine.HvOff();engine.Tick();}
            check(manualReads==0&&!manual.Backend.Hv&&manual.Backend.Ao==0,"stepped manual pre/post stages are DI-only and still shut down");
            string input=UnifiedPressureBridge.Xml(direct.Settings.Input);
            foreach(string source in new[]{"DIV","VMON"})
            {
                var s=UnifiedPressureBridge.Read<FactorySettings>(UnifiedPressureBridge.CreateConfigurationWithoutResidual(input,6000,30,8,true,4.5,source));
                check(!s.EndResidualCheckEnabled&&s.EndResidualThresholdV==0&&s.DividerOnlyFeedback==(source=="DIV"),"new bridge serializes source with no residual option "+source);
                s.TargetKv=.5;s.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);check(s.SkipPressureResidualMeasurement&&!s.ShouldCheckEndResidual,"new bridge pressure executes prohibition "+source);
            }
            check(typeof(UnifiedPressureBridge).GetMethod("CreateConfigurationWithoutResidual").GetParameters().Length==7,"distinct seven-parameter host contract");
            return "Pressure residual prohibition replaces old 4% policy: "+n+" offline checks PASS; HardwareAccessed=False; SDKCalled=False; independent physical discharge not verified";
        }
        private static Exception Fail(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected no-residual failure.");}
        private static void Set(object value,string name,object setting)
        {value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,setting);}
        private static object Get(object value,string name)
        {return value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);}
        private sealed class TaskIo:IFactoryArtTaskIo
        {
            internal bool FailStop;internal readonly List<string> Events=new List<string>();
            public int Stop(IntPtr task){Events.Add("STOP:"+task.ToInt64());return FailStop?-1:0;}
            public int Clear(IntPtr task){throw new Exception("this proof must not Clear an owned task");}
            public int WriteLines(IntPtr task,int samples,uint autoStart,double timeout,uint layout,byte[] data,out int written,IntPtr reserved)
            {if(data.Length!=1||data[0]!=0)throw new Exception("positive or port-wide output");Events.Add("ZERO:"+task.ToInt64());written=1;return 0;}
            public int WriteAnalog(IntPtr task,int samples,uint autoStart,double timeout,uint layout,double[] data,out int written,IntPtr reserved)
            {if(data.Length!=1||data[0]!=0)throw new Exception("positive AO");Events.Add("ZERO:"+task.ToInt64());written=1;return 0;}
            public void Check(int code,string operation){if(code<0)throw new IOException("fake monitor stop failure: "+operation);}
        }
    }
}
