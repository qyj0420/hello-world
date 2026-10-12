using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Policy fixtures only. No NativeFactoryBackend instance, SDK or device is used.
    internal static class FactoryBatchAuthorizationTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("Batch authorization: "+message);++checks;};
            Action<Action,string> rejects=(action,message)=>check(Rejected(action),message);
            var request=Request(5);
            var guard=new FactoryBatchAuthorization("fixture-session-A");
            var issued=guard.Authorize(request);
            check(request.BatchPermit==null && issued.BatchPermit!=null && !Object.ReferenceEquals(request,issued),"authorization returns an owned copy, not a UI capability");
            check(!Object.ReferenceEquals(request.Input,issued.Input),"input settings are frozen independently");
            check(issued.BatchPermit.Matches(issued),"new one-round permit matches exact issued settings");
            issued.Validate(FactoryRunKind.Pressure);
            check(issued.BatchStartConfirmed && !issued.BatchContinuation && issued.IndependentDischargeConfirmed && issued.FeedbackWiringConfirmed,"first round retains only explicit start confirmations");
            var beforeConsume=Capture(issued);
            rejects(()=>guard.Complete(issued,beforeConsume),"an archive alone is not native execution/cleanup evidence");
            check(!issued.BatchPermit.Matches(issued),"failed completion revokes the in-flight permit");
            rejects(()=>guard.Authorize(Next(issued,2)),"failed completion cannot authorize the next round");

            guard=new FactoryBatchAuthorization("fixture-session-A");
            issued=guard.Authorize(Request(5));
            issued.BatchPermit.ConsumeForNative("fixture-session-A",issued);
            rejects(()=>issued.BatchPermit.ConsumeForNative("fixture-session-A",issued),"a permit cannot be consumed twice");
            issued.BatchPermit.MarkNativeEnded();
            guard.Complete(issued,Capture(issued));
            check(!issued.BatchPermit.Matches(issued),"successful completion expires the old permit");
            var continuation=Next(issued,2);
            rejects(()=>continuation.Validate(FactoryRunKind.Pressure),"transport-only continuation cannot directly validate for output");
            var next=guard.Authorize(continuation);
            next.Validate(FactoryRunKind.Pressure);
            check(next.BatchPermit.Matches(next) && next.BatchContinuation && !next.BatchStartConfirmed,"server grants only the immediate continuation");
            check(!next.IndependentDischargeConfirmed && !next.FeedbackWiringConfirmed,"continuation never fabricates another manual discharge/wiring confirmation");
            check(continuation.BatchPermit==null,"server never writes a runtime permit into incoming UI settings");

            string xml;
            using(var writer=new StringWriter()) {new XmlSerializer(typeof(FactorySettings)).Serialize(writer,next);xml=writer.ToString();}
            FactorySettings reloaded;
            using(var reader=new StringReader(xml))reloaded=(FactorySettings)new XmlSerializer(typeof(FactorySettings)).Deserialize(reader);
            check(!xml.Contains("BatchPermit") && reloaded.BatchPermit==null,"runtime permit is absent from XML");
            check(reloaded.AutomaticBatch && reloaded.BatchContinuation && !reloaded.BatchStartConfirmed,"XML keeps honest batch metadata only");
            rejects(()=>reloaded.Validate(FactoryRunKind.Pressure),"saved continuation cannot impersonate process-local permission");
            var otherSession=new FactoryBatchAuthorization("fixture-session-B");
            rejects(()=>otherSession.Authorize(reloaded),"reconnected session cannot resume old batch from XML");
            rejects(()=>next.BatchPermit.ConsumeForNative("fixture-session-B",next),"old permit cannot be used by a new native session");
            guard.Revoke();
            check(!next.BatchPermit.Matches(next),"cancellation/fault revoke invalidates issued continuation immediately");
            rejects(()=>next.Validate(FactoryRunKind.Pressure),"revoked continuation fails strict validation");
            rejects(()=>guard.Authorize(Next(next,3)),"revocation never automatically resumes at a later round");

            var changes=new Dictionary<string,Action<FactorySettings>> {
                {"ART serial",s=>s.Input.ArtSerial="OTHER-ART"},
                {"Pico serial",s=>s.Input.PicoSerial="OTHER-PICO"},
                {"ART device",s=>s.Input.ArtDevice="Dev2"},
                {"target voltage",s=>s.TargetKv+=.01},
                {"divider ratio",s=>s.Input.DividerVPerV+=1},
                {"pressure ratio",s=>s.PressureVPerV+=1},
                {"current limit",s=>s.CurrentLimitMa-=1},
                {"voltage limit",s=>s.MaximumKv-=1},
                {"AO ceiling",s=>s.MaximumAoV-=.1},
                {"feedback window",s=>s.FeedbackWindowMs+=1},
                {"ramp timeout",s=>s.RampTimeoutSeconds+=1},
                {"capture duration",s=>s.Input.CaptureDurationMs-=1},
                {"sample rate",s=>s.Input.RequestedPicoRateHz+=1000},
                {"trigger threshold",s=>s.Input.ExternalTriggerVolts+=.1},
                {"location cycles",s=>s.LocationSaveCycleCount+=1},
                {"sequence identity",s=>s.SequenceId="replayed-other-sequence"},
                {"attempt",s=>s.SequenceAttempt+=1},
                {"count",s=>s.SequenceCount+=1},
                {"skipped round",s=>s.SequenceRound=s.Input.RunNumber=3},
                {"repeated round",s=>s.SequenceRound=s.Input.RunNumber=1},
                {"wrong run number",s=>s.Input.RunNumber=4},
                {"early final observation",s=>s.FinalSequenceAi4Observation=true},
                {"fabricated start confirmation",s=>s.BatchStartConfirmed=true},
                {"fabricated discharge confirmation",s=>s.IndependentDischargeConfirmed=true},
                {"fabricated wiring confirmation",s=>s.FeedbackWiringConfirmed=true},
                {"simulation mode",s=>s.Simulation=true}
            };
            foreach(var change in changes)
            {
                FactorySettings first;
                var g=Prepared("mutation-"+change.Key,out first);
                var candidate=Next(first,2);change.Value(candidate);
                rejects(()=>g.Authorize(candidate),"reject changed "+change.Key);
                rejects(()=>g.Authorize(Next(first,2)),"changed "+change.Key+" revokes rather than silently recovering");
            }

            foreach(bool chooseRecommendation in new[]{false,true})
            {
                FactorySettings first;
                var g=Prepared("range-"+chooseRecommendation,out first);
                var candidate=Next(first,2);
                var quality=FactoryRangeReview.EvaluateCapturedData(Capture(first));
                int recommended=FactoryBatchAuthorization.ApplyChargeFloor(FactoryPressureSequence.NextRange(first.Input.RangeIndex,quality),first.Input.PdCalibrationPcPerV);
                candidate.Input.RangeIndex=chooseRecommendation?recommended:first.Input.RangeIndex;
                var accepted=g.Authorize(candidate);
                check(accepted.BatchPermit.Matches(accepted),"only last actual or independently recommended range is accepted: "+chooseRecommendation);
                check(first.Input.RangeIndex==10,"range decision does not rewrite archived range");
            }
            {
                FactorySettings first;var g=Prepared("arbitrary-range",out first);
                int recommended=FactoryBatchAuthorization.ApplyChargeFloor(FactoryPressureSequence.NextRange(first.Input.RangeIndex,FactoryRangeReview.EvaluateCapturedData(Capture(first))),first.Input.PdCalibrationPcPerV);
                var candidate=Next(first,2);candidate.Input.RangeIndex=0;
                if(recommended==0)candidate.Input.RangeIndex=1;
                rejects(()=>g.Authorize(candidate),"arbitrary intermediate range does not bypass recommendation policy");
            }

            foreach(string failure in new[]{"not-ended","null-record","failed-acquisition","safety-forgery","missing-capture","wrong-record","wrong-source","wrong-range","simulated-source"})
            {
                var g=new FactoryBatchAuthorization("bad-completion-"+failure);
                var current=g.Authorize(Request(5));
                current.BatchPermit.ConsumeForNative("bad-completion-"+failure,current);
                if(failure!="not-ended")current.BatchPermit.MarkNativeEnded();
                var record=Capture(current);
                if(failure=="null-record")record=null;
                if(failure=="failed-acquisition")record.AcquisitionCompleted=false;
                if(failure=="safety-forgery")record.ResidualSafe=true;
                if(failure=="missing-capture")record.Capture=null;
                if(failure=="wrong-record")record.Settings.TargetKv+=.01;
                if(failure=="wrong-source")record.Capture.Settings.PicoSerial="OTHER-PICO";
                if(failure=="wrong-range")record.Capture.Settings.RangeIndex-=1;
                if(failure=="simulated-source")record.Capture.Simulation=true;
                rejects(()=>g.Complete(current,record),"reject incomplete/mismatched receipt: "+failure);
                check(!current.BatchPermit.Matches(current),"receipt failure revokes permit: "+failure);
                rejects(()=>g.Authorize(Next(current,2)),"receipt failure cannot auto-resume: "+failure);
            }

            foreach(string missing in new[]{"start","discharge","wiring"})
            {
                var g=new FactoryBatchAuthorization("missing-"+missing);var s=Request(5);
                if(missing=="start")s.BatchStartConfirmed=false;
                if(missing=="discharge")s.IndependentDischargeConfirmed=false;
                if(missing=="wiring")s.FeedbackWiringConfirmed=false;
                rejects(()=>g.Authorize(s),"initial explicit "+missing+" confirmation is required");
            }
            {
                var g=new FactoryBatchAuthorization("finished");var only=g.Authorize(Request(1));
                only.BatchPermit.ConsumeForNative("finished",only);only.BatchPermit.MarkNativeEnded();g.Complete(only,Capture(only));
                var repeated=Next(only,1);
                rejects(()=>g.Authorize(repeated),"finished final round cannot be replayed as continuation");
            }
            check(FactoryBatchAuthorization.ApplyChargeFloor(0,1000)==3,"server clamps raw below-floor recommendation at 100 pC");
            check(FactoryBatchAuthorization.ApplyChargeFloor(0,999)==4,"server uses exact scale rather than display rounding");
            check(FactoryBatchAuthorization.ApplyChargeFloor(10,1000)==10,"upper bound stays 20 V");
            check(FactoryBatchAuthorization.ApplyChargeFloor(0,5)==10,"small coefficient uses last available range");
            rejects(()=>FactoryBatchAuthorization.ApplyChargeFloor(0,4),"no imaginary larger range for insufficient calibration");
            rejects(()=>FactoryBatchAuthorization.ApplyChargeFloor(0,Double.NaN),"invalid coefficient rejected");
            var belowFloor=Request(2);belowFloor.Input.RangeIndex=0;
            rejects(()=>new FactoryBatchAuthorization("below-floor").Authorize(belowFloor),"new batch cannot bypass physical charge floor");
            return "Batch authorization: "+checks+" checks PASS; process-local one-use managed policy; HardwareAccessed=False";
        }

        internal static FactorySettings Request(int count)
        {
            var s=new FactoryTests.Fixture().Settings;
            s.Simulation=false;s.VmonAfterHvStartup=true;s.EndAfterPressureCapture=true;
            s.AutomaticBatch=true;s.BatchStartConfirmed=true;s.BatchContinuation=false;
            s.IndependentDischargeConfirmed=s.FeedbackWiringConfirmed=true;
            s.Input.ArtSerial="FAKE-BATCH-ART";s.Input.PicoSerial="FAKE-BATCH-PICO";
            s.Input.RangeIndex=10;s.Input.OscillationCircuitConnected=true;
            s.SequenceId="pure-fixture-sequence";s.SequenceCount=count;s.SequenceRound=s.Input.RunNumber=1;s.SequenceAttempt=1;
            s.FinalSequenceAi4Observation=count==1;
            s.Validate(FactoryRunKind.Pressure);
            return s;
        }
        internal static FactorySettings Next(FactorySettings previous,int round)
        {
            var s=previous.Clone();s.BatchPermit=null;s.BatchStartConfirmed=false;s.BatchContinuation=true;
            s.IndependentDischargeConfirmed=s.FeedbackWiringConfirmed=false;
            s.SequenceRound=s.Input.RunNumber=round;s.FinalSequenceAi4Observation=round==s.SequenceCount;
            return s;
        }
        internal static FactoryResult Capture(FactorySettings issued)
        {
            var simulated=issued.Clone();simulated.Simulation=true;
            simulated.AutomaticBatch=simulated.BatchStartConfirmed=simulated.BatchContinuation=false;simulated.BatchPermit=null;
            var result=FactoryPressureSequenceTests.Capture(simulated);
            result.Settings=issued.Clone();result.Capture.Settings=issued.Clone().Input;
            result.Capture.Simulation=issued.Simulation;
            // Logical real-mode policy fixture only; no native capture occurred.
            if(!issued.Simulation){result.Capture.ArtSerial=issued.Input.ArtSerial;result.Capture.PicoSerial=issued.Input.PicoSerial;}
            return result;
        }
        private static FactoryBatchAuthorization Prepared(string session,out FactorySettings first)
        {
            var g=new FactoryBatchAuthorization(session);first=g.Authorize(Request(5));
            first.BatchPermit.ConsumeForNative(session,first);first.BatchPermit.MarkNativeEnded();g.Complete(first,Capture(first));
            return g;
        }
        private static bool Rejected(Action action)
        {
            try{action();return false;}catch(ArgumentException){return true;}catch(InvalidOperationException){return true;}
        }
    }
}
