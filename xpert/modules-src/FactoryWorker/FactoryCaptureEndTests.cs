using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryCaptureEndTests
    {
        internal static string Run()
        {
            int n=0; Action<bool,string> check=(ok,text)=>{if(!ok)throw new Exception("R25: "+text);++n;};
            check(!new FactorySettings().EndAfterPressureCapture, "old XML default preserved");
            var f=new FactoryTests.Fixture(); f.Settings.EndAfterPressureCapture=true;
            check(f.Settings.Clone().UseCaptureOnlyEnd,"option survives serialization");
            check(!NativeFactoryBackend.RestoreMonitorAfterCapture(f.Settings,false,false),"normal native finite stop does not recreate AI");
            check(NativeFactoryBackend.RestoreMonitorAfterCapture(f.Settings,true,false),"calibration unchanged");
            check(NativeFactoryBackend.RestoreMonitorAfterCapture(f.Settings,false,true),"fault observation remains available");
            check(NativeFactoryBackend.RestoreMonitorAfterCapture(new FactorySettings(),false,false),"old workflow retains monitor");
            var zero=f.Settings.Clone();zero.TargetKv=0;
            check(!zero.UseCaptureOnlyEnd && NativeFactoryBackend.RestoreMonitorAfterCapture(zero,false,false),"zero mode not changed");
            int postReads=0;
            f.Backend.FeedbackRule=(b,x)=>{if(b.ReadCount>0){++postReads;throw new Exception("unexpected post capture AI");}return x;};
            var keys=new List<string>(); long readyAt=-1;
            var r=FactoryRunner.Run(f.Settings,FactoryRunKind.Pressure,f.Backend,f.Clock,(k,t)=>keys.Add(k),CancellationToken.None,
                x=>{readyAt=f.Clock.ElapsedMilliseconds;check(x.PostCaptureMonitoringSkipped,"preview records omission");});
            check(postReads==0,"no normal or residual reads after complete capture");
            check(r.CaptureOnlyAvailable() && !r.Completed && !r.ResidualSafe,"data completion not safety completion");
            check(!r.CompleteCaptureAvailable(),"existing acceptance gate stays locked");
            check(!r.FaultResidual.Started,"normal end is not a fault observation");
            check(!keys.Contains("RESIDUAL") && !keys.Contains("STEP_DISCHARGE_RUNNING"),"no residual loop reported");
            check(keys.Contains("STEP_DISCHARGE_SKIPPED") && keys.Contains("STEP_COMPLETE_UNVERIFIED"),"unmeasured state recorded honestly");
            check(!keys.Contains("STEP_COMPLETE_PASSED") && !keys.Contains("STEP_DISCHARGE_PASSED"),"no unmeasured safety pass");
            check(readyAt==f.Clock.ElapsedMilliseconds,"no post-capture timer waits");
            check(f.Backend.DisposeCount==1 && !f.Backend.Hv && !f.Backend.Optical && f.Backend.Ao==0,"outputs disabled and disposed");
            check(f.Backend.HvHighCount==1 && f.Backend.OpticalHighCount==1,"no repeated output");
            check(r.Metrics!=null && r.Capture.PdAdc.Length>0,"waveforms retained");
            r.Settings.TargetKv=0;check(!r.CaptureOnlyAvailable(),"mode cannot be reinterpreted");r.Settings.TargetKv=.5;
            using(var memory=new MemoryStream())
            {
                var serializer=new XmlSerializer(typeof(FactoryResult)); serializer.Serialize(memory,r);memory.Position=0;
                var loaded=(FactoryResult)serializer.Deserialize(memory);loaded.Capture=r.Capture;
                check(loaded.CaptureOnlyAvailable() && !loaded.ResidualSafe && !loaded.Completed,"metadata roundtrip preserves unverified state");
            }
            foreach(string fault in new[]{"arm","read","dispose"})
            {
                var bad=new FactoryTests.Fixture();bad.Settings.EndAfterPressureCapture=true;bad.Settings.DischargeTimeoutSeconds=.12;
                bad.Backend.FailArm=fault=="arm";bad.Backend.FailRead=fault=="read";bad.Backend.FailDispose=fault=="dispose";
                FactoryRunException failure=null;try{bad.Run();}catch(FactoryRunException ex){failure=ex;}
                check(failure!=null && !failure.PartialResult.CaptureOnlyAvailable(),fault+" cannot become completed acquisition");
                check(!failure.PartialResult.FaultResidual.Started && !bad.Backend.Events.Any(x=>x.Name=="FAULT_READ"),
                    fault+" R30 capture-only fault cleanup adds no residual acquisition");
            }
            var previewFailure=new FactoryTests.Fixture();previewFailure.Settings.EndAfterPressureCapture=true;
            var ok=FactoryRunner.Run(previewFailure.Settings,FactoryRunKind.Pressure,previewFailure.Backend,previewFailure.Clock,(k,t)=>{},
                CancellationToken.None,x=>{throw new IOException("display unavailable");});
            check(ok.CaptureOnlyAvailable(),"display failure cannot fabricate safety result or restart monitoring");
            string directory=Path.Combine(Path.GetTempPath(),"Xiongxin-R25-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            var logged=new FactoryTests.Fixture();logged.Settings.EndAfterPressureCapture=true;int loggedReads=0;
            logged.Backend.FeedbackRule=(b,x)=>{if(b.ReadCount>0)++loggedReads;return x;};
            using(var recording=new FactoryAiRecordingBackend(logged.Backend,directory,logged.Settings,logged.Clock))
                FactoryRunner.Run(logged.Settings,FactoryRunKind.Pressure,recording,logged.Clock,(k,t)=>recording.ObserveStage(k),CancellationToken.None);
            check(loggedReads==0,"logging/shutdown diagnostic does not append AI after finite read");
            var legacy=new FactoryTests.Fixture();check(legacy.Run().ResidualSafe,"legacy residual test still runs");
            var pending = new FactoryTests.Fixture().Settings; pending.Simulation=false; pending.VmonAfterHvStartup=true;
            pending.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            check(!pending.FeedbackWiringConfirmed && !pending.IndependentDischargeConfirmed,"prompt preflight does not grant approval");
            bool refused=false;try{pending.Validate(FactoryRunKind.Pressure);}catch(ArgumentException){refused=true;}
            check(refused,"worker still requires per-round confirmation");
            check(FactoryStartConfirmation.Accept(pending,FactoryRunKind.Pressure,false)==null,"cancel cannot authorize worker");
            var confirmed=FactoryStartConfirmation.Accept(pending,FactoryRunKind.Pressure,true);
            check(confirmed.FeedbackWiringConfirmed && confirmed.IndependentDischargeConfirmed,"explicit existing-dialog confirmation accepted");
            check(!pending.FeedbackWiringConfirmed && !pending.IndependentDischargeConfirmed,"form defaults retain no prior approval");
            pending.CurrentLimitMa=100;refused=false;
            try{pending.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);}catch(ArgumentException){refused=true;}
            check(refused,"prompt preflight still validates electrical limits");
            return "R25 capture-end: "+n+" checks PASS; HardwareAccessed=False";
        }
    }
}
