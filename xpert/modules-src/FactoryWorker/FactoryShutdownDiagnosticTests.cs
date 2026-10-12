using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryShutdownDiagnosticTests
    {
        internal static string Run()
        {
            int n=0;
            Action<bool,string> check=(ok,text)=>{if(!ok)throw new Exception("R13: "+text);n++;};
            foreach(int requested in new[]{200,500,800})
            {
                var f=new FactoryTests.Fixture(); f.Settings.Input.ArmDelayMs=requested;
                var r=f.Run(); var events=f.Backend.Events;
                int arm=events.FindIndex(e=>e.Name=="ARM_EXT"), off=events.FindIndex(arm+1,e=>e.Name=="SHUTDOWN"), light=events.FindIndex(e=>e.Name=="OPTICAL_HIGH");
                check(r.Completed && arm>0 && off>arm && light>off,"arm -> OFF -> light order");
                check(events[off].At-events[arm].At==Math.Max(500,requested),"minimum 500 ms counted from arming start before OFF, retains larger user delay");
                check(events[light].At==events[off].At,"no extra 500 ms delay after successful OFF");
                check(!events.Skip(arm).Any(e=>e.Name=="AO_POSITIVE"),"no further AO increments after task handoff");
            }
            foreach(int latency in new[]{150,500})
            {
                var f=new FactoryTests.Fixture(); f.Backend.ArmLatencyMs=latency;
                var r=f.Run(); var events=f.Backend.Events;
                var arm=events.First(e=>e.Name=="ARM_EXT"); var ready=events.First(e=>e.Name=="ARM_READY");
                var off=events.First(e=>e.Name=="SHUTDOWN" && e.At>=ready.At);
                check(r.Completed && off.At-arm.At==Math.Max(500,latency),"arming duration overlaps timer; no fixed-timer premature OFF");
                check(events.First(e=>e.Name=="OPTICAL_HIGH").At==off.At,"slow arm cannot cause a second delay after OFF");
            }
            var slowArm=new FactoryTests.Fixture(); slowArm.Backend.ArmLatencyMs=650;
            Fail(()=>slowArm.Run());
            check(!slowArm.Backend.Hv && slowArm.Backend.Ao==0 && slowArm.Backend.OpticalHighCount==0,"late SDK return above 500 ms shuts down, never proceeds to light");
            var early=new FactoryTests.Fixture(); early.Backend.PrematureComplete=true;
            var ex=(FactoryRunException)Fail(()=>early.Run());
            check(ex.PartialResult.Capture!=null && !ex.PartialResult.Completed && !ex.PartialResult.ResidualSafe,"early data retained but not accepted or discharged");
            check(early.Backend.OpticalHighCount==0 && early.Backend.ReadCount==1,"no optical retry after early edge");
            check(ex.PartialResult.Capture.Runtime.Contains("DIAGNOSTIC ONLY"),"raw early capture explicitly marked");
            int earlyArm=early.Backend.Events.FindIndex(e=>e.Name=="ARM_EXT");
            int earlyOff=early.Backend.Events.FindIndex(earlyArm+1,e=>e.Name=="SHUTDOWN");
            check(early.Backend.Events[earlyOff].At==early.Backend.Events[earlyArm].At && earlyOff<early.Backend.Events.FindIndex(e=>e.Name=="READ"),"early completion before OFF disables immediately before diagnostics, without waiting 500 ms");
            var armFail=new FactoryTests.Fixture(); armFail.Backend.FailArm=true; Fail(()=>armFail.Run());
            check(!armFail.Backend.Hv && armFail.Backend.Ao==0 && armFail.Backend.OpticalHighCount==0,"arming failure shuts down without light");
            var cancelled=new FactoryTests.Fixture();
            Fail(()=>cancelled.Run((k,t)=>{if(k=="STEP_ARM_PASSED") cancelled.Cancellation.Cancel();}));
            check(!cancelled.Backend.Hv && cancelled.Backend.Ao==0 && cancelled.Backend.OpticalHighCount==0,"stop during handoff cannot leave HV or emit light");
            var cancelWait=new FactoryTests.Fixture();
            cancelWait.Clock.AfterPause=()=>{if(cancelWait.Backend.Armed)cancelWait.Cancellation.Cancel();};
            Fail(()=>cancelWait.Run());
            check(cancelWait.Backend.Events.First(e=>e.Name=="SHUTDOWN" && e.At>cancelWait.Backend.Events.First(a=>a.Name=="ARM_EXT").At).At-cancelWait.Backend.Events.First(e=>e.Name=="ARM_EXT").At<500 && cancelWait.Backend.OpticalHighCount==0,"cancel while waiting does not defer shutdown until 500 ms");
            var low=new FactoryTests.Fixture();low.Backend.EmergencyRule=b=>!b.Armed;
            Fail(()=>low.Run());check(!low.Backend.Hv && low.Backend.OpticalHighCount==0,"DI low during armed settle never permits optical");

            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R13-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var s=new FactorySettings { Simulation=false }; var clock=new FactoryTests.FakeClock();
            var d=new FactoryShutdownDiagnostic(s,clock); d.Monitor(true);
            for(int b=0;b<10;b++) {clock.Advance(100);var raw=new double[300];for(int i=0;i<100;i++){raw[i*3]=.01;raw[i*3+1]=.02;raw[i*3+2]=-.03;}d.Raw(raw,100);}
            check(d.Trigger(1000,1002,"OFF",true),"software prewindow trigger created");
            check(!d.Trigger(1003,1004,"OFF",true),"cleanup never overwrites first event");
            for(int b=0;b<50;b++) {clock.Advance(10);d.Raw(new double[30],10);}
            d.Finish("test",true); var before=d.Record.Points.Count; d.Raw(new[]{1.0,2.0,3.0},1);
            check(before==1001 && d.Record.Points.Count==before,"500 ms prewindow + 500ms tail; freezes before later captures");
            check(d.Record.Points.First().EstimatedMs==500 && d.Record.Points.Last().EstimatedMs==1500,"window endpoints and raw spacing retained");
            check(d.Record.Points.First().Ai4==-.03 && d.Record.RateHz==1000,"signed raw values and real monitor rate");
            string file=d.Save(root);var saved=Load(file);
            check(saved.Points.Count==before && File.ReadAllLines(Path.Combine(root,"shutdown-diagnostic.csv")).Length==before+1,"XML/CSV raw roundtrip counts");
            check(saved.TimingNotice.Contains("不是物理") && saved.TimingNotice.Contains("125M"),"time and bandwidth limitations explicit");

            var fault=new FactoryTests.Fixture();fault.Settings.DividerOnlyFeedback=true;
            fault.Backend.FeedbackRule=(b,v)=>{if(b.Hv && b.Ao>0)v.ImonRawV=5;return v;};
            string faildir=Path.Combine(root,"ramp-fault");
            using(var logged=new FactoryAiRecordingBackend(fault.Backend,faildir,fault.Settings,fault.Clock))
                Fail(()=>FactoryRunner.Run(fault.Settings,FactoryRunKind.Pressure,logged,fault.Clock,(k,t)=>logged.ObserveStage(k),fault.Cancellation.Token));
            saved=Load(Path.Combine(faildir,"shutdown-diagnostic.xml"));
            check(saved.OffCallsReturned && saved.PostReadFinished,"fault shutdown followed by bounded read-only tail");
            check(saved.Points.Any(p=>p.HostReadMs>saved.OffReturnedMs) && saved.Points.Any(p=>p.Ai0==5),"both faulting sample and post-OFF samples retained");
            check(!fault.Backend.Hv && fault.Backend.Ao==0 && fault.Backend.OpticalHighCount==0 && fault.Backend.ArmCount==0,"fault branch does not arm/fire to fill diagnostics");
            var normal=new FactoryTests.Fixture(); string normaldir=Path.Combine(root,"normal");
            using(var logged=new FactoryAiRecordingBackend(normal.Backend,normaldir,normal.Settings,normal.Clock))
                FactoryRunner.Run(normal.Settings,FactoryRunKind.Pressure,logged,normal.Clock,(k,t)=>logged.ObserveStage(k),CancellationToken.None);
            saved=Load(Path.Combine(normaldir,"shutdown-diagnostic.xml"));
            check(!saved.PostReadFinished && saved.Status.Contains("三路AI已停止"),"normal handoff cannot claim post-OFF three-channel continuity");
            check(saved.Points.All(p=>p.HostReadMs<=saved.OffRequestMs),"restored post-capture monitoring not spliced into old prewindow");
            var noHv=new FactoryTests.Fixture();string nodir=Path.Combine(root,"no-hv");
            using(var logged=new FactoryAiRecordingBackend(noHv.Backend,nodir,noHv.Settings,noHv.Clock)) {logged.Initialize(noHv.Settings,false);logged.ReadFeedback();logged.Shutdown();}
            check(!File.Exists(Path.Combine(nodir,"shutdown-diagnostic.xml")),"no HV attempt produces no fictitious HV OFF window");
            var badOff=new FactoryTests.Fixture(); string badOffDir=Path.Combine(root,"off-failure");
            using(var logged=new FactoryAiRecordingBackend(badOff.Backend,badOffDir,badOff.Settings,badOff.Clock))
                Fail(()=>FactoryRunner.Run(badOff.Settings,FactoryRunKind.Pressure,logged,badOff.Clock,(k,t)=>
                {logged.ObserveStage(k);if(k=="STEP_HV_ON_PASSED"){badOff.Backend.FailShutdown=true;badOff.Cancellation.Cancel();}},badOff.Cancellation.Token));
            saved=Load(Path.Combine(badOffDir,"shutdown-diagnostic.xml"));
            check(!saved.OffCallsReturned && !saved.PostReadFinished,"failed disable never starts post-window wait");
            check(badOff.Backend.DisposeCount==1 && badOff.Backend.OpticalHighCount==0,"failed disable still attempts Dispose, never light");
            var badRead=new FactoryTests.Fixture();string badReadDir=Path.Combine(root,"post-read-failure");
            badRead.Backend.FeedbackRule=(b,v)=>{if(b.Hv && b.Ao>0)v.ImonRawV=5;if(!b.Hv && b.HvHighCount>0)throw new IOException("fake diagnostic read failure");return v;};
            using(var logged=new FactoryAiRecordingBackend(badRead.Backend,badReadDir,badRead.Settings,badRead.Clock))
                Fail(()=>FactoryRunner.Run(badRead.Settings,FactoryRunKind.Pressure,logged,badRead.Clock,(k,t)=>logged.ObserveStage(k),CancellationToken.None));
            saved=Load(Path.Combine(badReadDir,"shutdown-diagnostic.xml"));
            check(saved.Status.Contains("fake diagnostic read failure") && !saved.PostReadFinished,"tail-read failure retained as incomplete");
            check(!badRead.Backend.Hv && badRead.Backend.Ao==0 && badRead.Backend.DisposeCount==1,"diagnostic failure cannot skip cleanup");
            return "R13 pre-OFF handoff/diagnostics: "+n+" offline checks PASS; HardwareAccessed=False; fixtures="+root;
        }
        private static FactoryShutdownRecord Load(string file)
        {using(var stream=File.OpenRead(file))return (FactoryShutdownRecord)new XmlSerializer(typeof(FactoryShutdownRecord)).Deserialize(stream);}
        private static Exception Fail(Action work)
        {try{work();}catch(Exception ex){return ex;}throw new Exception("Expected rejection");}
    }
}
