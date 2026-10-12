using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryR29Tests
    {
        internal static string Run() {return FactoryOfflineAsyncTest.Run(Check);}
        private static async Task<string> Check()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("R29: "+why);n++;};
            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-R29-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            var w=FactoryMeasuredRangeWindow.Select(false,2500000,125000000,275000,true,2.8183445880578226);
            check(w.Valid && w.StartSample==352294 && w.EndExclusive==2500000,"20 ms uses actual first-zero tail, not eight cycles");
            check(w.Definition.Contains("17.181648"),"exact actual evaluated duration");
            foreach(double bad in new[]{Double.NaN,Double.PositiveInfinity,-1,0,20,20.1})
                check(!FactoryMeasuredRangeWindow.Select(false,2500000,125000000,275000,true,bad).Valid,"invalid/preceding-trigger zero rejected");
            check(!FactoryMeasuredRangeWindow.Select(false,2500000,125000000,275000,false,3).Valid,"no phase cannot guess");
            check(FactoryMeasuredRangeWindow.Select(true,2500000,125000000,0,false,Double.NaN).Count==2500000,"zero/calibration full record preserved");
            var s=new FactoryTests.Fixture().Settings;s.Input.ArtSerial="SIM-ART";s.Input.PicoSerial="SIM-PICO";
            s.Simulation=true;s.Input.OscillationCircuitConnected=true;s.EndAfterPressureCapture=true;
            s.Input.RangeIndex=10;s.Input.AutoRange=false;s.Input.CaptureDurationMs=20;
            FactoryPressureStartPrompt.ValidateRangePreflight(s,5);
            check(FactoryPressureStartPrompt.Timing(s).Contains("不要求8周期"),"20ms preflight does not impose attenuation cycles");
            var missingCircuit=s.Clone();missingCircuit.Input.OscillationCircuitConnected=false;
            bool preflightRejected=false;try{FactoryPressureStartPrompt.ValidateRangePreflight(missingCircuit,5);}catch(ArgumentException){preflightRejected=true;}
            check(!preflightRejected && !missingCircuit.Input.OscillationCircuitConnected,"legacy feature false does not block repeated capture or invent confirmation");
            var dcOnly=s.Clone();dcOnly.Input.PreTriggerPercent=100;
            preflightRejected=false;try{FactoryPressureStartPrompt.ValidateRangePreflight(dcOnly,5);}catch(ArgumentException){preflightRejected=true;}
            check(preflightRejected,"all-DC repeated range capture diagnosed before output");
            FactoryPressureStartPrompt.ValidateRangePreflight(dcOnly,1);
            check(true,"single DC acquisition remains available");
            var seen=new List<int>();var events=new List<string>();var results=new List<FactoryResult>();
            var progress=new FactorySequenceProgress(s,5);
            Func<FactorySettings,Task<FactoryResult>> run=next=>{
                seen.Add(next.Input.RangeIndex);var r=FactoryPressureSequenceTests.Capture(next);
                r.DirectoryPath=Path.Combine(root,"round-"+next.SequenceRound);Directory.CreateDirectory(r.DirectoryPath);results.Add(r);
                return Task.FromResult(r);
            };
            using(var form=new FactoryMainForm(Path.Combine(root,"labels"),Path.Combine(root,"profiles")))
            {
                var review=typeof(FactoryMainForm).GetMethod("ReviewCapturedRange",BindingFlags.Instance|BindingFlags.NonPublic);
                int done=await FactoryPressureSequence.Run(s,5,run,null,null,CancellationToken.None,
                    (r,q)=>Task.FromResult((bool)review.Invoke(form,new object[]{r,q,true})),progress,null,(k,t)=>events.Add(k+":"+t));
                check(done==5 && progress.Completed==5 && progress.Remaining==0 && seen.Count==5,"five captures finish and remaining reaches zero");
                check(seen[1]<seen[0],"changed range actually delivered to second worker, not UI only");
                check(events.Last().Contains("剩余 0 次") && events.Last().StartsWith("SEQUENCE_END"),"completed counts visible in final progress");
                check(results.All(r=>r.Metrics.ReferenceAnalysis.PhaseValid),"short simulation records retain measured phase");
                check(results.All(r=>!r.Metrics.ReferenceAnalysis.DecayValid),"short record does not fake eight-cycle decay");
                check(results.All(r=>r.Metrics.ReferenceAnalysis.Detail.Contains("所需") && r.Metrics.ReferenceAnalysis.Detail.Contains("实际")),"decay shows required and actual span");
                for(int i=0;i<4;i++)
                {
                    string file=Directory.GetFiles(results[i].DirectoryPath,"next-range-decision-*.xml").Single();
                    FactoryNextRangeDecision d;using(var f=File.OpenRead(file))d=(FactoryNextRangeDecision)new XmlSerializer(typeof(FactoryNextRangeDecision)).Deserialize(f);
                    check(d.WasAutomaticDecision && !d.ReminderEnabled && d.ArtSerial=="SIM-ART" && d.PicoSerial=="SIM-PICO","silent decision logs identity and mode");
                    check(d.CompletedRounds==i+1 && d.RemainingRounds==4-i && d.FinalRangeIndex==seen[i+1],"silent decision matches next actual capture and counts");
                    check(d.ActualRecordMs==20 && d.EvaluationWindowMs>0 && d.EvaluationWindowMs<20 && !String.IsNullOrEmpty(d.Reason),"silent decision includes actual window/reason");
                }
                var last=results.Last();var q=FactoryRangeReview.EvaluateCapturedData(last);
                bool apply=(bool)review.Invoke(form,new object[]{last,q,false});
                check(!apply && Directory.GetFiles(last.DirectoryPath,"next-range-decision-*.xml").Length==1,"terminal round also records no-next-round decision");
            }
            // A denied real-mode confirmation remains a stopped queue, never a restart.
            var real=s.Clone();real.Simulation=false;real.VmonAfterHvStartup=true;
            real.FeedbackWiringConfirmed=real.IndependentDischargeConfirmed=true;
            var trace=new List<string>();int calls=0;
            int stopped=await FactoryPressureSequence.Run(real,5,next=>{calls++;return Task.FromResult(FactoryPressureSequenceTests.Capture(next));},
                (i,total,next)=>Task.FromResult(false),null,CancellationToken.None,null,null,null,(k,t)=>trace.Add(k+":"+t));
            check(stopped==1 && calls==1 && trace.Any(x=>x.StartsWith("SEQUENCE_WAIT_CONFIRM")) && trace.Any(x=>x.Contains("剩余 4 次")),"declined confirmation explicitly reports wait/remaining without output");
            var x=new double[1000];x[500]=10;x[501]=-.4;
            var env=FactoryPulseEnvelopeWindow.Select(x,0,0,x.Length,490,520);
            check(env.Start==484 && env.End==517 && env.Peak==500,"100101 reference 3 percent / 16 quiet boundary");
            x[500]=-10;env=FactoryPulseEnvelopeWindow.Select(x,0,0,x.Length,490,520);
            check(env.Peak==500 && env.Start==484,"negative main peak uses absolute envelope");
            check(FactoryPulseEnvelopeWindow.Select(x,0,495,1000,500,501)==null,"incomplete left selection rejected");
            check(FactoryPulseEnvelopeWindow.Select(x,0,0,510,500,501)==null,"incomplete right selection rejected");
            check(FactoryPulseEnvelopeWindow.Select(new double[1000],0,0,1000,400,600)==null,"flat noise-free selection cannot manufacture pulse");
            var longTail=Enumerable.Repeat(1.0,70000).ToArray();longTail[35000]=10;
            check(FactoryPulseEnvelopeWindow.Select(longTail,0,0,longTail.Length,35000,35001)==null,"search cap not accepted as envelope end");
            var pulseRound=FactoryPostProcessingTests.Round(Path.Combine(root,"pulse"));
            FactoryPostProcessingTests.AddPulse(pulseRound.Capture,2000,8000);FactoryPostProcessingTests.AddPulse(pulseRound.Capture,7000,-12000);
            var raw=(short[])pulseRound.Capture.PdAdc.Clone();
            var tf=FactoryTfAnalysis.ExtractSelected(pulseRound,10,0,pulseRound.Capture.Settings.CaptureDurationMs);
            check(tf.Pulses.Count==2 && tf.PulseWindowProtocol==FactoryPulseEnvelopeWindow.Protocol,"new selected extraction uses actual reference envelope mode");
            check(tf.Pulses[0].ChargePc>0 && tf.Pulses[1].ChargePc<0 && pulseRound.Capture.PdAdc.SequenceEqual(raw),"signed pulses and unchanged raw ADC");
            foreach(var p in tf.Pulses)
            {
                var reference=FactoryPulseEnvelopeWindow.Select(FactoryPdSignal.Get(pulseRound.Capture),tf.BaselineV,0,raw.Length,p.PeakSample,p.PeakSample+1);
                check(p.StartSample==reference.Start && p.EndSample==reference.End,"TF uses reference envelope unchanged");
            }
            var fastPulseRound=FactoryPostProcessingTests.Round(Path.Combine(root,"fast-window"),125000000);
            FactoryPostProcessingTests.AddPulse(fastPulseRound.Capture,50000,12000);
            var fastEstimate=FactoryTfAnalysis.Estimate(fastPulseRound.Capture);
            var tight=FactoryPulseEnvelopeWindow.Select(FactoryPdSignal.Get(fastPulseRound.Capture),fastEstimate.BaselineV,0,fastPulseRound.Capture.PdAdc.Length,49995,50005);
            check(tight!=null,"125MS/s drag identifies a complete envelope");
            var tightTf=FactoryTfAnalysis.ExtractSelected(fastPulseRound,10,tight.Start*1000.0/125000000,(tight.End+1)*1000.0/125000000);
            check(tightTf.Pulses.Count==1 && tightTf.Pulses[0].StartSample==tight.Start && tightTf.Pulses[0].EndSample==tight.End,
                "confirmed tight drag envelope is extractable at native125MS/s");
            check(FactoryTfAnalysis.ExtractSelected(fastPulseRound,10,(tight.Start+1)*1000.0/125000000,(tight.End+1)*1000.0/125000000).Pulses.Count==0,
                "incomplete left envelope is still rejected at125MS/s");
            check(FactoryTfAnalysis.ExtractSelected(fastPulseRound,10,tight.Start*1000.0/125000000,tight.End*1000.0/125000000).Pulses.Count==0,
                "incomplete right envelope is still rejected at125MS/s");
            return "V1.0.29: "+n+" checks PASS; 20ms five-round queue, actual next ranges "+String.Join(",",seen)+"; silent decisions, counts, required/actual decay, 100101 windows; HardwareAccessed=False; "+root;
        }
        internal static void Replay(string original,string output)
        {
            Directory.CreateDirectory(output);
            FactoryResult r;using(var f=File.OpenRead(Path.Combine(original,"result.xml")))r=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(f);
            r.Capture=BenchCapture.Load(original);
            var q=FactoryRangeReview.EvaluateCapturedData(r);
            int next=FactoryPressureSequence.NextRange(r.Capture.Settings.RangeIndex,q);
            if(!q.WindowValid || q.WindowEndExclusive!=r.Capture.PdAdc.Length)throw new Exception("Actual record range regression");
            File.WriteAllText(Path.Combine(output,"actual-record-replay.txt"),"HardwareAccessed=False\r\nSource="+original+"\r\n"+q.WindowDefinition+"\r\n"+q.Reason+"\r\nCurrentIndex="+r.Capture.Settings.RangeIndex+"; NextIndex="+next+"\r\nDecayValid="+r.Metrics.ReferenceAnalysis.DecayValid);
        }
    }
}
