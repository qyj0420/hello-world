using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    // Real-mode policy is driven exclusively by synthetic captures and an in-memory
    // permit owner. This test does not create a device process or load a driver.
    internal static class FactoryAutomaticBatchTests
    {
        internal static string Run() { return FactoryOfflineAsyncTest.Run(Check); }

        private static async Task<string> Check()
        {
            int checks=0;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("Automatic batch: "+message);++checks;};
            string directory=Path.Combine(Path.GetTempPath(),"Xiongxin-Automatic-Batch-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var first=FactoryBatchAuthorizationTests.Request(5);
            var guard=new FactoryBatchAuthorization("automatic-batch-policy-fixture");
            var issuedRounds=new List<FactorySettings>();
            var archived=new List<FactoryResult>();
            var events=new List<string>();
            var reported=new List<string>();
            var starts=new List<long>();
            var finishes=new List<long>();
            var elapsed=Stopwatch.StartNew();
            int prompts=0,starting=0;
            var progress=new FactorySequenceProgress(first,5);
            int count=await FactoryPressureSequence.Run(first,5,incoming=>{
                int round=issuedRounds.Count+1;
                check(incoming.BatchPermit==null,"UI transport does not issue native permission for round "+round);
                check(archived.Count==round-1,"dispatch waits for the previous archive and release");
                if(round>1)
                {
                    check(File.Exists(Path.Combine(directory,"round-"+(round-1),"result.xml")),"prior complete archive exists before round "+round);
                    check(elapsed.ElapsedMilliseconds-finishes[round-2]>=FactoryPressureSequence.InterRoundDelayMs-30,"round "+round+" preserves cancellable 2-second cooldown");
                }
                starts.Add(elapsed.ElapsedMilliseconds);
                var issued=guard.Authorize(incoming);
                issued.Validate(FactoryRunKind.Pressure);
                issued.BatchPermit.ConsumeForNative("automatic-batch-policy-fixture",issued);
                events.Add("begin-"+round);issuedRounds.Add(issued.Clone());
                var record=FactoryBatchAuthorizationTests.Capture(issued);
                issued.BatchPermit.MarkNativeEnded();events.Add("release-"+round);
                string output=Path.Combine(directory,"round-"+round);Directory.CreateDirectory(output);
                Program.SaveResult(record,output,false);events.Add("archive-"+round);
                guard.Complete(issued,record);events.Add("receipt-"+round);
                archived.Add(record);finishes.Add(elapsed.ElapsedMilliseconds);
                return Task.FromResult(record);
            },(round,total,settings)=>{
                ++prompts;
                throw new InvalidOperationException("A normal automatic batch must not ask to confirm the next round.");
            },(round,total,settings)=>{
                ++starting;
                check(total==5 && round==starting,"starting reports consecutive rounds and stable total");
                if(round>1)check(!settings.IndependentDischargeConfirmed && !settings.FeedbackWiringConfirmed && !settings.BatchStartConfirmed,
                    "starting continuation contains no forged operator confirmation");
            },CancellationToken.None,null,progress,null,(key,text)=>reported.Add(key));

            check(count==5 && issuedRounds.Count==5 && archived.Count==5,"one explicit real-mode policy start completes five synthetic rounds");
            check(prompts==0,"no modal, inline or hidden confirm-next callback is invoked");
            check(progress.Completed==5 && progress.Remaining==0 && !progress.Running && !progress.CanResume,"finished and remaining counts are accurate");
            check(!reported.Contains("SEQUENCE_WAIT_CONFIRM"),"normal batch emits no per-round confirmation state");
            check(issuedRounds[0].BatchStartConfirmed && !issuedRounds[0].BatchContinuation,"only first round is a newly confirmed start");
            for(int i=0;i<issuedRounds.Count;++i)
            {
                var current=issuedRounds[i];
                check(current.SequenceRound==i+1 && current.SequenceCount==5 && current.SequenceId==issuedRounds[0].SequenceId,
                    "batch identity and round metadata "+(i+1));
                check(current.TargetKv==first.TargetKv && current.Input.ArtSerial==first.Input.ArtSerial && current.Input.PicoSerial==first.Input.PicoSerial,
                    "device identity and target remain frozen "+(i+1));
                check(current.FinalSequenceAi4Observation==(i==4),"only final round requests fresh AI4 observation "+(i+1));
                check(!archived[i].ResidualSafe && !archived[i].Completed && archived[i].CaptureOnlyAvailable(),"round "+(i+1)+" does not claim discharge safety");
                if(i>0)check(current.BatchContinuation && !current.BatchStartConfirmed && !current.IndependentDischargeConfirmed && !current.FeedbackWiringConfirmed,
                    "round "+(i+1)+" uses batch continuation, not fabricated manual confirmation");
                int begin=events.IndexOf("begin-"+(i+1)),release=events.IndexOf("release-"+(i+1));
                int archive=events.IndexOf("archive-"+(i+1)),receipt=events.IndexOf("receipt-"+(i+1));
                check(begin<release && release<archive && archive<receipt,"execution/release/archive/receipt order "+(i+1));
                if(i<4)check(receipt<events.IndexOf("begin-"+(i+2)),"next round waits for complete receipt "+(i+1));
            }
            check(!first.BatchContinuation && first.BatchPermit==null && first.SequenceRound==1,"source settings are not converted into a persistent running capability");

            var simulation=first.Clone();simulation.Simulation=true;
            simulation.AutomaticBatch=simulation.BatchStartConfirmed=true;simulation.BatchContinuation=false;
            simulation.BatchPermit=null;simulation.IndependentDischargeConfirmed=simulation.FeedbackWiringConfirmed=false;
            simulation.SequenceId=null;simulation.SequenceRound=simulation.SequenceCount=simulation.Input.RunNumber=1;
            var simSeen=new List<FactorySettings>();
            count=await FactoryPressureSequence.Run(simulation,5,s=>{
                simSeen.Add(s.Clone());return Task.FromResult(FactoryBatchAuthorizationTests.Capture(s));
            },(round,total,settings)=>{throw new InvalidOperationException("Simulation must not confirm every round.");},null,CancellationToken.None);
            check(count==5 && simSeen.Count==5,"AutomaticBatch simulation independently completes five rounds without native permission");
            check(simSeen.TrueForAll(s=>s.Simulation && s.AutomaticBatch && s.BatchPermit==null),"AutomaticBatch simulation does not acquire a hardware capability");
            check(simSeen[0].BatchStartConfirmed && !simSeen[0].BatchContinuation &&
                simSeen[4].BatchContinuation && !simSeen[4].BatchStartConfirmed,"simulation exercises the same explicit batch/continuation metadata branch");

            foreach(string failure in new[]{"capture-error","archive-error","release-error"})
            {
                int calls=0;bool failed=false;
                try
                {
                    await FactoryPressureSequence.Run(first,5,s=>{
                        ++calls;
                        throw new InvalidOperationException("synthetic "+failure);
                    },(round,total,settings)=>{throw new InvalidOperationException("Failure cannot enter a continuation prompt.");},null,CancellationToken.None);
                }
                catch(InvalidOperationException){failed=true;}
                check(failed && calls==1,failure+" never auto-retries or dispatches next round");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                int calls=0;bool cancelled=false;
                try
                {
                    await FactoryPressureSequence.Run(first,5,s=>{
                        ++calls;var record=FactoryBatchAuthorizationTests.Capture(s);cancellation.Cancel();return Task.FromResult(record);
                    },null,null,cancellation.Token);
                }
                catch(OperationCanceledException){cancelled=true;}
                check(cancelled && calls==1,"stop after capture prevents all later dispatch");
            }
            using(var cancellation=new CancellationTokenSource())
            {
                int calls=0;bool cancelled=false,waiting=false;
                try
                {
                    await FactoryPressureSequence.Run(first,5,s=>{++calls;return Task.FromResult(FactoryBatchAuthorizationTests.Capture(s));},null,null,
                        cancellation.Token,null,null,null,(key,text)=>{if(key=="SEQUENCE_INTERVAL_WAIT"){waiting=true;cancellation.Cancel();}});
                }
                catch(OperationCanceledException){cancelled=true;}
                check(waiting && cancelled && calls==1,"stop during cooldown cancels without a continuation prompt");
            }

            foreach(string failure in new[]{"EMERGENCY_ACTIVE","CONNECTION_LOST"})
            {
                var checkpoint=new FactorySequenceProgress(first,5);int calls=0;bool failed=false;
                try
                {
                    await FactoryPressureSequence.Run(first,5,s=>{
                        ++calls;checkpoint.Observe(failure);throw new InvalidOperationException(failure);
                    },null,null,CancellationToken.None,null,checkpoint);
                }
                catch(InvalidOperationException){failed=true;}
                checkpoint.RememberStoppedUi(first);
                check(failed && calls==1 && checkpoint.Completed==0 && checkpoint.Remaining==5 && !checkpoint.Running,
                    failure+" interrupted round is not counted or automatically restarted");
                check(checkpoint.CanResume,"explicit later resume remains distinguishable from automatic recovery "+failure);
                var resume=checkpoint.ResumeRequest(first,5);
                check(!resume.BatchStartConfirmed && !resume.BatchContinuation && resume.BatchPermit==null &&
                    !resume.IndependentDischargeConfirmed && !resume.FeedbackWiringConfirmed,
                    failure+" checkpoint contains no usable batch or manual confirmation");
            }
            return "Automatic batch: "+checks+" checks PASS; five-round real-mode policy/simulation fixtures, archives and cooldown; HardwareAccessed=False";
        }
    }
}
