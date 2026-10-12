using System;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryWorkerExitedException : InvalidOperationException
    { internal FactoryWorkerExitedException(string message):base(message){} }
    public sealed class FactoryTriggerRecoveryRecord
    {
        public bool InitialTimeout, ArtCompleteAtTimeout, PicoCompleteAtTimeout;
        public bool SupplementAttempted, DiagnosticCaptureSaved, EligibleForThresholdAdjustment;
        public DateTime TimeoutUtc, SupplementRequestedUtc;
        public long InitialWaitMs, SupplementWaitMs;
        public string Outcome = "NotUsed";
    }

    internal sealed class FactoryCaptureTimeoutException : TimeoutException
    {
        internal readonly bool Art, Pico;
        internal readonly long Elapsed;
        internal FactoryCaptureTimeoutException(bool art,bool pico,long elapsed)
            : base("两卡未在期限内完成：ART="+art+"，Pico="+pico+"，实际等待 "+elapsed+" ms。检查 PFI0 / Pico EXT 联动检测、边沿及阈值。")
        {Art=art;Pico=pico;Elapsed=elapsed;}
    }
    internal sealed class FactoryLinkageFailureException : InvalidOperationException
    {
        internal FactoryLinkageFailureException(string detail) : base("联动触发存在错误，请重新加压。"+detail) { }
    }
    // Parent process receives structured, identity-checked evidence after worker exit.
    internal sealed class FactoryTriggerFailureException : InvalidOperationException
    {
        internal readonly FactoryResult Result;
        internal FactoryTriggerFailureException(FactoryResult result,Exception source)
            : base("联动触发存在错误，请重新加压。"+result.TriggerRecovery.Outcome,source) {Result=result;}
    }
    internal static class FactoryTriggerRecovery
    {
        internal const int WaitMs=3000;
        internal static void WaitOrRecover(FactorySettings s,IFactoryBackend b,IFactoryClock c,FactoryResult result,
            long firstPulse,Action<string,string> report,CancellationToken token)
        {
            FactoryCaptureTimeoutException timeout;
            try {FactoryRunner.WaitCapture(s,b,c,firstPulse,report,token,WaitMs);return;}
            catch(FactoryCaptureTimeoutException ex) {timeout=ex;}
            var r=result.TriggerRecovery;
            r.InitialTimeout=true;r.TimeoutUtc=DateTime.UtcNow;r.InitialWaitMs=timeout.Elapsed;
            r.ArtCompleteAtTimeout=timeout.Art;r.PicoCompleteAtTimeout=timeout.Pico;
            r.Outcome="原始联动等待超时";
            report("TRIGGER_LINKAGE_TIMEOUT",timeout.Message+"本轮不计完成；不自动重新加压。");
            token.ThrowIfCancellationRequested();FactoryRunner.CheckEmergency(b);
            b.Shutdown(); // Abort immediately if any disable call fails.
            if(timeout.Art || timeout.Pico)
            {
                r.Outcome="已有卡完成或超期完成，未补发，避免旧新段混合。";
                r.EligibleForThresholdAdjustment=true;
                throw new FactoryLinkageFailureException(r.Outcome);
            }
            var recovery=b as IFactoryOpticalRecovery;
            if(recovery==null)throw new InvalidOperationException("无双卡重新布置能力，未补发光触发。");
            // Recheck the old segment before stopping: a late completed card
            // cannot become an eligible replacement segment.
            if(b.ArtComplete || b.PicoComplete)
                throw new InvalidOperationException("停止旧段前已有卡迟到完成，未补发；不混合新旧段。");
            b.StopCapture();
            FactoryRunner.WaitForCaptureCooldown(s.Simulation,report,token,()=>FactoryRunner.CheckEmergency(b));
            token.ThrowIfCancellationRequested();FactoryRunner.CheckEmergency(b);
            recovery.PrepareOpticalRetry();
            token.ThrowIfCancellationRequested();FactoryRunner.CheckEmergency(b);
            // A late/unsolicited edge during rearm must never be reused.
            if(b.ArtComplete || b.PicoComplete)throw new InvalidOperationException("补发前已有新段完成，未补发；触发来源需核查。");
            report("TRIGGER_SUPPLEMENT", "旧双卡采集已停止并重新布置；仅补发一次 P0.7，不重新加压，补发段仅作诊断。");
            long start=c.ElapsedMilliseconds;
            Exception pulseError=null;
            r.SupplementAttempted=true;r.SupplementRequestedUtc=DateTime.UtcNow;
            try
            {
                token.ThrowIfCancellationRequested();FactoryRunner.CheckEmergency(b);
                b.SetOptical(true);start=c.ElapsedMilliseconds;
                FactoryRunner.GuardedDelay(BenchSettings.OpticalHoldMilliseconds,b,c,token);
            }
            catch(Exception ex) {pulseError=ex;}
            finally
            {
                try {b.SetOptical(false);}
                catch(Exception ex) {pulseError=pulseError==null?ex:new AggregateException(pulseError,ex);}
            }
            if(pulseError!=null)throw pulseError;
            try
            {
                FactoryRunner.WaitCapture(s,b,c,start,report,token,WaitMs);
                var capture=b.ReadCapture();capture.ValidateCapture();
                b.StopCapture();b.Shutdown();
                capture.Runtime+=" | V1.0.30 supplemental optical DIAGNOSTIC ONLY, initial trial failed";
                result.Capture=capture;r.DiagnosticCaptureSaved=true;
                r.Outcome="单次补发已取得双卡完整诊断段；不是原试验有效同步结果，本轮未计完成。";
            }
            catch(FactoryCaptureTimeoutException)
            {r.Outcome="单次补发后仍未完成双卡采集；不再补发，停止本轮/连续队列。";}
            finally {r.SupplementWaitMs=c.ElapsedMilliseconds-start;}
            r.EligibleForThresholdAdjustment=true;
            report("TRIGGER_LINKAGE_RESULT",r.Outcome);
            throw new FactoryLinkageFailureException(r.Outcome);
        }
    }
}
