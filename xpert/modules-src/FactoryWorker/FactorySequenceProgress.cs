using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Process-local checkpoint, not a hardware permission or an automatic retry.
    // Only a fresh Start click / choice / operator confirmation can consume it.
    internal sealed class FactorySequenceProgress
    {
        internal string Id { get; private set; }
        internal int Count { get; private set; }
        internal int Completed { get; private set; }
        internal int Attempt { get; private set; }
        internal int NextRound { get { return Completed + 1; } }
        internal int Remaining { get { return Count - Completed; } }
        internal bool CanResume { get; private set; }
        internal bool Running { get; private set; }
        internal bool InFlight { get { return inFlight; } }
        private FactorySettings pending;
        private string stoppedUi;
        private bool emergency, inFlight;

        internal FactorySequenceProgress(FactorySettings first, int count)
        {
            if (first == null || first.TargetKv <= 0 || first.ZeroSynchronizedOptical || count < 1 || count > 100)
                throw new ArgumentException("连续加压需非零目标及1～100次。");
            Id = Guid.NewGuid().ToString("N"); Count = count; pending = WithoutPermission(first);
        }
        internal void Observe(string key)
        { if (Running && (key == "EMERGENCY_ACTIVE" || key=="CONNECTION_LOST")) emergency = true; }
        internal void Begin(FactorySettings first, int count)
        {
            if (Running || Remaining <= 0 || count != Count || (Attempt > 0 && !CanResume) ||
                Fingerprint(first) != Fingerprint(pending))
                throw new InvalidOperationException("连续序列或待执行参数不匹配，不能恢复。");
            // These assertions must come from the NEW start confirmation, not pending.
            first.Validate(FactoryRunKind.Pressure);
            Running = true; CanResume = false; stoppedUi = null; emergency = false; inFlight = false; ++Attempt;
        }
        internal void Starting(FactorySettings settings)
        { pending = WithoutPermission(settings); inFlight = true; }
        internal void Captured()
        {
            if (!Running || !inFlight || emergency)
                throw new InvalidOperationException("急停或状态不明的本轮不能计入已完成次数。");
            ++Completed; inFlight = false;
        }
        internal void Prepared(FactorySettings next) { pending = WithoutPermission(next); }
        internal void End()
        { CanResume = Running && emergency && Remaining > 0; Running = false; }
        internal void RememberStoppedUi(FactorySettings current)
        {
            if (!CanResume) return;
            // A changed/invalid UI must not prevent cleanup after a hardware
            // error. No valid snapshot means restart only, never a guessed resume.
            stoppedUi = null;
            try { stoppedUi = Fingerprint(current); }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        internal bool Matches(FactorySettings current, int count)
        {
            if(!CanResume || Running || stoppedUi==null || count!=Count || current==null || current.Input==null)return false;
            // UI compatibility probing must fail closed for invalid edits, not
            // throw while showing a stopped checkpoint. ResumeRequest/Begin
            // remain the strict submission gates and never normalize inputs.
            try {return stoppedUi==Fingerprint(current);}
            catch(ArgumentException){return false;}
            catch(InvalidOperationException){return false;}
        }
        internal FactorySettings ResumeRequest(FactorySettings current, int count)
        {
            if (!Matches(current,count)) throw new InvalidOperationException("参数已改变，不能继续原序列；请重新开始。");
            return WithoutPermission(pending);
        }
        internal string Describe()
        { return "原序列已完成 " + Completed + " / " + Count + " 轮，剩余 " + Remaining + " 轮；急停/通信异常中断的第 " + NextRound + " 轮未计入完成。"; }

        private static FactorySettings WithoutPermission(FactorySettings settings)
        {
            var copy = settings.Clone();
            copy.FeedbackWiringConfirmed = copy.IndependentDischargeConfirmed = copy.CalibrationSourceConfirmed = false;
            copy.BatchStartConfirmed = copy.BatchContinuation = false;
            copy.BatchPermit = null;
            return copy;
        }
        private static string Fingerprint(FactorySettings settings)
        {
            var copy = WithoutPermission(settings);
            // Validate the same effective input contract used by a worker. Ignore
            // only bookkeeping and per-round acknowledgements, NOT range/devices.
            copy.SequenceId = null; copy.SequenceCount = copy.SequenceRound = copy.SequenceAttempt = 1;
            copy.FinalSequenceAi4Observation = false;
            copy.Input.RunNumber = 1;
            copy.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            using (var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            { new XmlSerializer(typeof(FactorySettings)).Serialize(writer,copy); return writer.ToString(); }
        }
    }
}
