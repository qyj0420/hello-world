using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryDividerTraceTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok) throw new Exception("R12: "+why); checks++;};
            foreach(double sign in new[]{1.0,-1.0})
            {
                var f=new FactoryTests.Fixture(); f.Settings.TargetKv=1; f.Settings.DividerOnlyFeedback=true;
                f.Settings.Input.AoStepV=.005; f.Settings.Input.LoopDelayMs=7;
                f.Backend.FeedbackRule=(b,v)=>{v.DividerRawV*=sign;v.VmonRawV=0;return v;};
                var r=f.Run();
                check(r.Completed && r.Ramp.Last().VmonRawV==0,"only divider can confirm target even with different VMON");
                check(r.Ramp.Last().DividerRawV*sign>0,"signed divider kept unchanged");
                check(r.Ramp.Last().AoV<.17 && f.Backend.HvHighCount==1,"divider controls growth/ceiling without retry");
                var writes=f.Backend.Events.Where(e=>e.Name=="AO_POSITIVE").ToArray();
                check(writes.Length>25 && writes.Zip(writes.Skip(1),(a,b)=>b.At-a.At).All(t=>t>=7),"configured step/interval affects output sequence");
                check(r.Ramp.Zip(r.Ramp.Skip(1),(a,b)=>b.AoV-a.AoV).All(d=>d<=.005000001),"configured maximum AO step respected");
            }
            var dual=new FactoryTests.Fixture();dual.Settings.TargetKv=1;dual.Backend.FeedbackRule=(b,v)=>{v.VmonRawV=0;return v;};
            check(Fail(()=>dual.Run()).ToString().Contains("不一致"),"legacy dual-feedback default retained");
            var lag=new FactoryTests.Fixture();lag.Settings.TargetKv=1;lag.Settings.DividerOnlyFeedback=true;lag.Settings.RampTimeoutSeconds=.05;
            lag.Backend.FeedbackRule=(b,v)=>{v.DividerRawV=0;v.VmonRawV=b.Hv && b.Ao>0 ? .99/30*5 : 0;return v;};
            check(Fail(()=>lag.Run()).ToString().Contains("超时") && lag.Backend.OpticalHighCount==0,"VMON alone cannot confirm divider-only target");
            foreach(int guard in new[]{0,1,2,3,4})
            {
                var f=new FactoryTests.Fixture();f.Settings.TargetKv=1;f.Settings.MaximumFeedbackKv=1.02;f.Settings.DividerOnlyFeedback=true;
                f.Backend.FeedbackRule=(b,v)=>
                {
                    if(b.Hv && b.Ao>0)
                    {
                        if(guard==0) v.VmonRawV=1.1/30*5;
                        if(guard==1) v.DividerRawV=-1100.0/11000;
                        if(guard==2) v.ImonRawV=5;
                        if(guard==3) v.TimestampUtc=DateTime.UtcNow.AddSeconds(-10);
                        if(guard==4) v.DividerRawV=Double.NaN;
                    }
                    return v;
                };
                Fail(()=>f.Run());
                check(f.Backend.OpticalHighCount==0 && !f.Backend.Hv && f.Backend.Ao==0,"independent guard retained: "+guard);
            }
            var emergency=new FactoryTests.Fixture();emergency.Settings.DividerOnlyFeedback=true;
            emergency.Backend.EmergencyRule=b=>b.Ao==0;Fail(()=>emergency.Run());
            check(!emergency.Backend.Hv && emergency.Backend.OpticalHighCount==0,"emergency still stops divider-only ramp");
            var residual=new FactoryTests.Fixture();residual.Settings.DividerOnlyFeedback=true;
            residual.Backend.FeedbackRule=(b,v)=>{v.VmonRawV=.1;return v;};Fail(()=>residual.Run());
            check(residual.Backend.HvHighCount==0,"initial VMON residual protection not bypassed");
            check(new FactorySettings().DividerOnlyFeedback==false && emergency.Settings.Clone().DividerOnlyFeedback,"new mode XML clone and old default");
            var capped=new FactoryTests.Fixture(); capped.Settings.TargetKv=1; capped.Settings.DividerOnlyFeedback=true; capped.Settings.RampTimeoutSeconds=.1;
            capped.Backend.FeedbackRule=(b,v)=>{v.VmonRawV=0;v.DividerRawV*=.9;return v;};
            check(Fail(()=>capped.Run()).ToString().Contains("超时"),"low feedback cannot request extra 10 percent AO headroom");
            check(capped.Backend.MaximumAo<=capped.Settings.AoCommandCeilingV && capped.Backend.OpticalHighCount==0,"ratio plus authorized 5 percent is strict AO ceiling even if target not reached");
            var fixedStep=new FactoryTests.Fixture();fixedStep.Settings.TargetKv=1;fixedStep.Settings.DividerOnlyFeedback=true;fixedStep.Settings.RampTimeoutSeconds=.1;
            fixedStep.Backend.FeedbackRule=(b,v)=>{v.VmonRawV=0;v.DividerRawV=b.Ao>0 ? 950.0/11000 : 0;return v;};
            var fixedFailure=(FactoryRunException)Fail(()=>fixedStep.Run());
            var distinct=fixedFailure.PartialResult.Ramp.Select(v=>v.AoV).Distinct().ToArray();
            check(distinct.Length>10 && distinct.Zip(distinct.Skip(1),(a,b)=>Math.Abs(b-a-.01)<1e-10 ||
                Math.Abs(b-fixedStep.Settings.NominalAoV)<1e-10 || Math.Abs(b-fixedStep.Settings.AoCommandCeilingV)<1e-10).All(ok=>ok),
                "fixed AO increment except nominal checkpoint and final command ceiling");

            var clock=new FactoryTests.FakeClock();var settings=new FactorySettings();var messages=new List<string>();
            var publisher=new FactoryFeedbackTrendPublisher(settings,clock,(k,v)=>messages.Add(v));
            publisher.Observe(new FactoryFeedback { VmonRawV=.1,ImonRawV=.01,DividerRawV=-.2 });
            check(messages.Count==1,"first feedback is displayed immediately before HV ON");
            clock.Advance(10);publisher.Observe(new FactoryFeedback { VmonRawV=.4,ImonRawV=.04,DividerRawV=-.8 });
            clock.Advance(90);publisher.Observe(new FactoryFeedback { VmonRawV=.2,ImonRawV=.02,DividerRawV=-.3 });
            var parsed=FactoryFeedbackTrendSample.Parse(messages.Last());
            check(Math.Abs(parsed.Values[1]-1.24)<1e-9 && Math.Abs(parsed.Values[3]+3.3)<1e-9,"latest unaveraged signed engineering values with 31kV/5V VMON");
            check(Math.Abs(parsed.Values[6]-2.48)<1e-9 && Math.Abs(parsed.Values[9]+8.8)<1e-9,"display bins retain early peaks, not just last sample");
            clock.Advance(1);publisher.Observe(new FactoryFeedback { DividerRawV=-.1 });publisher.Flush();
            check(messages.Count==3,"final pending display sample flushes on stop");
            clock.Advance(1);publisher.Observe(new FactoryFeedback { DividerRawV=-.4 });
            clock.Advance(1000);publisher.Observe(new FactoryFeedback { DividerRawV=0 });
            publisher.Flush();
            check(FactoryFeedbackTrendSample.Parse(messages.Last()).Values[9]==0,"capture gap does not carry old extrema into new segment");
            check(FactoryFeedbackTrendSample.Parse("bad")==null && FactoryFeedbackTrendSample.Parse(messages[0].Replace("0;","NaN;"))==null,"bad transport never displayed as real data");
            var oldCulture=Thread.CurrentThread.CurrentCulture;
            try{Thread.CurrentThread.CurrentCulture=new CultureInfo("de-DE");check(FactoryFeedbackTrendSample.Parse(parsed.Encode())!=null,"trend invariant culture");}
            finally{Thread.CurrentThread.CurrentCulture=oldCulture;}

            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R12-"+Guid.NewGuid().ToString("N"));
            var rawSettings=new FactorySettings { Simulation=false };
            using(var trace=new FactoryAiTrace(Path.Combine(root,"raw"),rawSettings,clock))
            {
                trace.Monitor(true); trace.Stage("SAFE_RUNNING");
                trace.Raw(new[]{.001,.002,-.003,.004,.005,-.006},2);
                trace.Command("HV_ON_RETURNED",0,true); trace.Stage("HV_UP_RUNNING");trace.Command("AO_RETURNED",.01);
                trace.Raw(new[]{.007,.008,-.009},1);
                trace.Monitor(false);trace.Monitor(true);trace.Raw(new[]{Double.NaN,.1,-.2},1);
            }
            var lines=File.ReadAllLines(Path.Combine(root,"raw","ai-monitor-raw.csv"));
            check(lines.Length==5,"every returned raw sample recorded without display decimation");
            check(lines[1].Contains("SAFE_RUNNING") && lines[1].Contains("-0.003"),"pre-HV signed first sample retained");
            check(lines[2].StartsWith("1,1,1,") && lines[3].StartsWith("1,2,2,"),"1000 S/s hardware segment sample indices continuous");
            check(lines[4].StartsWith("2,0,0,") && lines[4].Contains("NaN"),"gap starts new segment, invalid raw preserved for diagnosis");
            string events=File.ReadAllText(Path.Combine(root,"raw","ai-monitor-events.csv"));
            check(events.Contains("HV_ON_RETURNED") && events.Contains("MONITOR_STOPPED") && events.Contains("NOT per-sample hardware"),"command phases and timestamp limits documented");
            var failureFixture=new FactoryTests.Fixture();failureFixture.Settings.DividerOnlyFeedback=true;
            failureFixture.Backend.FeedbackRule=(b,v)=>{if(b.Hv && b.Ao>0) v.ImonRawV=5;return v;};
            using(var recorded=new FactoryAiRecordingBackend(failureFixture.Backend,Path.Combine(root,"failed"),failureFixture.Settings,failureFixture.Clock))
            {
                Fail(()=>FactoryRunner.Run(failureFixture.Settings,FactoryRunKind.Pressure,recorded,failureFixture.Clock,
                    (k,v)=>recorded.ObserveStage(k),failureFixture.Cancellation.Token));
            }
            lines=File.ReadAllLines(Path.Combine(root,"failed","ai-monitor-raw.csv"));
            check(lines.Length>5 && lines[1].Contains("STEP_SAFE_RUNNING"),"failed round still saves initial observations");
            check(lines.Any(l=>l.Contains(",5,")),"faulting observation recorded before protection rejection and post-off records");
            check(!failureFixture.Backend.Hv && failureFixture.Backend.Ao==0 && failureFixture.Backend.OpticalHighCount==0,"recorder failure path still cleans hardware first");
            var manual=new FactoryTests.Fixture();manual.Settings.DividerOnlyFeedback=true;
            using(var recorded=new FactoryAiRecordingBackend(manual.Backend,Path.Combine(root,"manual"),manual.Settings,manual.Clock))
            using(var engine=new FactoryManualEngine(recorded,b=>{},manual.Clock,(k,v)=>recorded.ObserveStage(k),manual.Cancellation.Token))
            {engine.Initialize(manual.Settings);engine.Tick();check(manual.Backend.HvHighCount==0,"initialization/Tick logs before manual ON");engine.HvOn(1);engine.Tick();}
            check(File.ReadAllLines(Path.Combine(root,"manual","ai-monitor-raw.csv")).Length>2,"manual idle/startup data persists without any capture");
            return "R12 divider-only/trend/raw trace: "+checks+" offline checks PASS; HardwareAccessed=False; fixtures="+root;
        }
        private static Exception Fail(Action run)
        {try{run();}catch(Exception ex){return ex;}throw new Exception("Expected failure did not occur");}
    }
}
