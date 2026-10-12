using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPressureSequenceTests
    {
        internal static string Run() { return FactoryOfflineAsyncTest.Run(Check); }
        private static async Task<string> Check()
        {
            int n=0; Action<bool,string> verify=(ok,msg)=>{if(!ok)throw new Exception("R25 sequence: "+msg);++n;};
            var initial=new FactoryTests.Fixture().Settings;initial.Input.ArtSerial="SIM-ART";initial.Input.PicoSerial="SIM-PICO";
            initial.Input.OscillationCircuitConnected=true;initial.EndAfterPressureCapture=true;initial.Input.RangeIndex=10;
            var seen=new List<FactorySettings>(); int confirmations=0;
            Func<FactorySettings,Task<FactoryResult>> run=next=>
            {
                seen.Add(next.Clone());return Task.FromResult(Capture(next));
            };
            var count=await FactoryPressureSequence.Run(initial,3,run,(i,total,s)=>{++confirmations;return Task.FromResult(false);},null,CancellationToken.None);
            verify(count==3 && seen.Count==3,"finite simulation count includes first round");
            verify(confirmations==0,"simulation uses no real-operator prompt");
            verify(seen.TrueForAll(s=>s.TargetKv==initial.TargetKv && s.SequenceCount==3),"voltage remains fixed");
            verify(seen[0].SequenceId==seen[2].SequenceId && seen[2].SequenceRound==3,"sequence metadata survives each round");
            verify(initial.SequenceCount==1 && initial.Input.RangeIndex==10,"request and original range are immutable");
            verify(seen[1].Input.RangeIndex<seen[0].Input.RangeIndex,"next-round range auto-adjusted from complete raw window");
            var real=initial.Clone();real.Simulation=false;real.VmonAfterHvStartup=true;
            real.FeedbackWiringConfirmed=real.IndependentDischargeConfirmed=true;
            seen.Clear();confirmations=0;
            count=await FactoryPressureSequence.Run(real,3,run,(i,total,s)=>
            { ++confirmations;verify(!s.IndependentDischargeConfirmed && !s.FeedbackWiringConfirmed,"next real round has no carried permission");return Task.FromResult(false);},null,CancellationToken.None);
            verify(count==1 && seen.Count==1 && confirmations==1,"declined second round cannot dispatch");
            seen.Clear();count=await FactoryPressureSequence.Run(real,2,run,null,null,CancellationToken.None);
            verify(count==1 && seen.Count==1,"no confirmation provider cannot auto energize");
            seen.Clear();count=await FactoryPressureSequence.Run(real,2,run,(i,t,s)=>Task.FromResult(true),null,CancellationToken.None);
            verify(count==2 && seen[1].IndependentDischargeConfirmed,"new explicit confirmation permits next round only");
            int calls=0;bool failed=false;
            try{await FactoryPressureSequence.Run(initial,3,s=>{++calls;throw new InvalidOperationException("fixture failure");},null,null,CancellationToken.None);}catch(InvalidOperationException){failed=true;}
            verify(failed && calls==1,"failure never retries");
            using(var cancelled=new CancellationTokenSource())
            {
                calls=0;failed=false;
                try{await FactoryPressureSequence.Run(initial,3,s=>{++calls;var r=Capture(s);cancelled.Cancel();return Task.FromResult(r);},null,null,cancelled.Token);}catch(OperationCanceledException){failed=true;}
                verify(failed && calls==1,"cancel after capture prevents next round");
            }
            foreach(int bad in new[]{0,101})
            {failed=false;try{await FactoryPressureSequence.Run(initial,bad,run,null,null,CancellationToken.None);}catch(ArgumentException){failed=true;}verify(failed,"count bounds");}
            foreach(var q in new[]{new LocationRangeResult{WindowValid=false},new LocationRangeResult{AtMaximumAndClipped=true},new LocationRangeResult{DriverOverflowUncertain=true},new LocationRangeResult{MaximumRangeInsufficient=true}})
            {failed=false;try{FactoryPressureSequence.NextRange(10,q);}catch(InvalidOperationException){failed=true;}verify(failed,"uncertain range stops queue");}
            verify(FactoryPressureSequence.NextRange(7,new LocationRangeResult{RecommendedIndex=5,FullClippedSamples=1})==7,"full-segment clipping prevents downrange");
            verify(FactoryPressureSequence.NextRange(5,new LocationRangeResult{RecommendedIndex=8,RecommendationUncertain=true})==6,"local clipping permits only one adjacent uprange");
            int reviews=0;
            seen.Clear();
            count=await FactoryPressureSequence.Run(initial,2,run,null,null,CancellationToken.None,(r,q)=>
            {++reviews;return Task.FromResult(false);});
            verify(count==2 && reviews==1 && seen[1].Input.RangeIndex==10,"closing reminder keeps range for next round");
            seen.Clear();reviews=0;
            count=await FactoryPressureSequence.Run(initial,2,run,null,null,CancellationToken.None,(r,q)=>
            {++reviews;return Task.FromResult(true);});
            verify(count==2 && reviews==1 && seen[1].Input.RangeIndex<10,"explicit reminder acceptance applies only next round");
            var missing=initial.Clone();missing.Input.OscillationCircuitConnected=false;
            seen.Clear();reviews=0;FactorySequenceStoppedException stopped=null;
            count=await FactoryPressureSequence.Run(missing,2,run,null,null,CancellationToken.None,(r,q)=>{++reviews;return Task.FromResult(true);});
            verify(count==2 && seen.Count==2 && !seen[0].Input.OscillationCircuitConnected && !missing.Input.OscillationCircuitConnected,
                "legacy feature false does not disable measured windows or fabricate wiring confirmation");
            seen.Clear();reviews=0;
            Func<FactorySettings,Task<FactoryResult>> flatRun=next=>{seen.Add(next.Clone());var r=Capture(next);Array.Clear(r.Capture.VoltageRaw,0,r.Capture.VoltageRaw.Length);return Task.FromResult(r);};
            try{await FactoryPressureSequence.Run(missing,6,flatRun,null,null,CancellationToken.None,(r,q)=>{++reviews;return Task.FromResult(true);});}
            catch(FactorySequenceStoppedException ex){stopped=ex;}
            verify(stopped!=null && stopped.CompletedRounds==1 && stopped.RequestedRounds==6 && seen.Count==1,"invalid window stops next round, not completed round");
            verify(stopped.LastCompletedRound.CaptureOnlyAvailable() && !stopped.LastCompletedRound.Completed && !stopped.LastCompletedRound.ResidualSafe,"queue stop retains honest captured result");
            verify(reviews==1 && stopped.Message.Contains("电压信噪比不足") && !stopped.Message.Contains("未确认振荡回路"),
                "invalid measured signal is presented before stopping without an obsolete checkbox instruction");
            var captured=Capture(initial);var quality=FactoryRangeReview.EvaluateCapturedData(captured);
            verify(FactoryNextRangeReview.NeedsPrompt(captured,quality,true),"checked reminder shows changed recommendation");
            verify(!FactoryNextRangeReview.NeedsPrompt(captured,quality,false),"unchecked suppresses ordinary recommendation popup");
            verify(FactoryNextRangeReview.CanApply(captured,quality,10),"measurement-only result can set next range without safety acceptance");
            var choice=FactoryNextRangeReview.Decide(captured,quality,10,true);
            verify(choice.ApplyNextRound && !choice.AutomaticRetryRequested && !captured.Completed && !captured.ResidualSafe && captured.Settings.Input.RangeIndex==10,"next-range decision does not mutate current data or safety flags");
            quality.FullClippedSamples=1;
            verify(!FactoryNextRangeReview.CanApply(captured,quality,10),"dialog cannot downrange with full-segment clipping");
            quality.FullClippedSamples=0;quality.WindowValid=false;
            verify(!FactoryNextRangeReview.CanApply(captured,quality,10) && FactoryNextRangeReview.NeedsPrompt(captured,quality,true),"invalid window warning cannot apply");
            quality.WindowValid=true;quality.AtMaximumAndClipped=true;
            verify(FactoryNextRangeReview.NeedsPrompt(captured,quality,false),"critical maximum-range risk remains visible when unchecked");
            verify(!FactoryNextRangeReview.CanApply(captured,quality,10),"critical invalid recommendation cannot apply");
            return "R26 sequence/reminder: "+n+" checks PASS; managed fixtures only; HardwareAccessed=False";
        }
        internal static FactoryResult Capture(FactorySettings request)
        {
            // Pure simulation, including logical real-mode policy fixtures.
            var simulated=request.Clone();simulated.Simulation=true;
            var clock=new FactoryTests.FakeClock();
            var r=FactoryRunner.Run(simulated,FactoryRunKind.Pressure,new SimulationFactoryBackend(clock),clock,(k,t)=>{},CancellationToken.None);
            r.Settings=request.Clone();r.Capture.Simulation=request.Simulation;r.Capture.Settings=request.Input;
            return r;
        }
    }
}
