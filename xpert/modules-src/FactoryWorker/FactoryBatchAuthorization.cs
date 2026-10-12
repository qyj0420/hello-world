using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // Process-local, one-use execution evidence. No XML field can construct it.
    // This is permission to finish an explicitly started machine batch, NOT a
    // measurement of discharge, a personnel-entry permission or fault recovery.
    internal sealed class FactoryBatchPermit
    {
        private readonly string session, sequence, fingerprint;
        private readonly int attempt, count, round, range;
        private readonly bool start, continuation, wiring, independent;
        private volatile bool active = true, nativeEnded;
        private int consumed;
        internal FactoryBatchPermit(string sessionId, FactorySettings settings)
        {
            session=sessionId; sequence=settings.SequenceId; attempt=settings.SequenceAttempt;
            count=settings.SequenceCount; round=settings.SequenceRound; range=settings.Input.RangeIndex;
            start=settings.BatchStartConfirmed; continuation=settings.BatchContinuation;
            wiring=settings.FeedbackWiringConfirmed; independent=settings.IndependentDischargeConfirmed;
            fingerprint=FactoryBatchAuthorization.Fingerprint(settings);
        }
        internal bool Matches(FactorySettings settings)
        {
            return active && settings!=null && settings.Input!=null && settings.AutomaticBatch &&
                sequence==settings.SequenceId && attempt==settings.SequenceAttempt && count==settings.SequenceCount &&
                round==settings.SequenceRound && range==settings.Input.RangeIndex &&
                start==settings.BatchStartConfirmed && continuation==settings.BatchContinuation &&
                wiring==settings.FeedbackWiringConfirmed && independent==settings.IndependentDischargeConfirmed &&
                settings.Input.RunNumber==round && settings.FinalSequenceAi4Observation==(round==count) &&
                fingerprint==FactoryBatchAuthorization.Fingerprint(settings);
        }
        internal void ConsumeForNative(string sessionId, FactorySettings settings)
        {
            if(sessionId!=session || !Matches(settings) || Interlocked.CompareExchange(ref consumed,1,0)!=0)
                throw new InvalidOperationException("批次轮次许可不属于当前设备会话、已失效或已被消费；禁止输出。");
        }
        internal void MarkNativeEnded()
        {
            if(!active || consumed!=1)throw new InvalidOperationException("没有已消费的批次轮次可完成。");
            nativeEnded=true;
        }
        internal bool NativeEnded {get{return active && consumed==1 && nativeEnded;}}
        internal void Revoke(){active=false;}
    }

    internal sealed class FactoryBatchAuthorization
    {
        private readonly string sessionId;
        private string sequence, fingerprint, blocked;
        private int attempt, count, expectedRound, currentRange, recommendedRange, adjacentUpscaleFallback=-1;
        private bool ready;
        private FactoryBatchPermit inFlight;
        private readonly HashSet<string> startedAttempts=new HashSet<string>(StringComparer.Ordinal);
        internal FactoryBatchAuthorization(string sessionId)
        {
            if(String.IsNullOrWhiteSpace(sessionId))throw new ArgumentException("缺少设备会话代号。");
            this.sessionId=sessionId;
        }
        internal FactorySettings Authorize(FactorySettings incoming)
        {
            try{return AuthorizeCore(incoming);}
            catch{Revoke();throw;}
        }
        private FactorySettings AuthorizeCore(FactorySettings incoming)
        {
            if(incoming==null || incoming.Input==null)throw new ArgumentNullException("incoming");
            var next=incoming.Clone(); next.BatchPermit=null;
            if(!next.AutomaticBatch)
            {
                if(next.BatchStartConfirmed || next.BatchContinuation)
                    throw new InvalidOperationException("非批次请求不能携带批次授权声明。");
                Revoke(); return next;
            }
            if(next.Simulation || next.TargetKv<=0 || next.ZeroSynchronizedOptical || !next.UseCaptureOnlyEnd ||
                !next.UsePostHvVmonStartup || String.IsNullOrWhiteSpace(next.SequenceId) || next.SequenceAttempt<1 ||
                next.SequenceCount<1 || next.SequenceCount>100 || next.SequenceRound<1 || next.SequenceRound>next.SequenceCount)
                throw new InvalidOperationException("自动批次仅适用于同会话非零加压的有效有界轮次。");
            if(next.Input.RunNumber!=next.SequenceRound || next.FinalSequenceAi4Observation!=(next.SequenceRound==next.SequenceCount))
                throw new InvalidOperationException("批次采集轮次或末轮诊断标记不一致。");
            if(inFlight!=null)throw new InvalidOperationException("前一轮批次尚未确认归档和资源收尾。");
            next.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            if(ApplyChargeFloor(next.Input.RangeIndex,next.Input.PdCalibrationPcPerV)!=next.Input.RangeIndex)
                throw new InvalidOperationException("批次局放量程低于100 pC，未授权本轮输出。");
            if(!next.BatchContinuation)
            {
                if(!next.BatchStartConfirmed || !next.IndependentDischargeConfirmed || !next.FeedbackWiringConfirmed)
                    throw new InvalidOperationException("新批次或故障后续接必须由新的开始操作明确授权，不继承历史确认。");
                next.Validate(FactoryRunKind.Pressure);
                string attemptKey=next.SequenceId+"/"+next.SequenceAttempt.ToString(CultureInfo.InvariantCulture);
                if(!startedAttempts.Add(attemptKey))
                    throw new InvalidOperationException("该批次执行代号已使用，不能将旧开始请求重放为新授权。");
                Revoke(); sequence=next.SequenceId; attempt=next.SequenceAttempt; count=next.SequenceCount;
                expectedRound=next.SequenceRound; fingerprint=Fingerprint(next); ready=true; blocked=null;
                currentRange=recommendedRange=next.Input.RangeIndex;
            }
            else
            {
                if(next.BatchStartConfirmed || next.IndependentDischargeConfirmed || next.FeedbackWiringConfirmed)
                    throw new InvalidOperationException("正常批次续轮不能冒充新的人工接线/放电确认。");
                if(!ready || !String.IsNullOrEmpty(blocked) || next.SequenceId!=sequence || next.SequenceAttempt!=attempt ||
                    next.SequenceCount!=count || next.SequenceRound!=expectedRound || Fingerprint(next)!=fingerprint ||
                    (next.Input.RangeIndex!=currentRange && next.Input.RangeIndex!=recommendedRange && next.Input.RangeIndex!=adjacentUpscaleFallback))
                    throw new InvalidOperationException("批次续轮代号、次序、冻结参数或合法量程不匹配；不自动恢复或跳过轮次。"+blocked);
            }
            ready=false;
            next.BatchPermit=inFlight=new FactoryBatchPermit(sessionId,next);
            return next;
        }
        internal void Complete(FactorySettings issued, FactoryResult archived)
        {
            try{CompleteCore(issued,archived);}
            catch{Revoke();throw;}
        }
        private void CompleteCore(FactorySettings issued, FactoryResult archived)
        {
            if(inFlight==null || issued==null || !Object.ReferenceEquals(issued.BatchPermit,inFlight) || !inFlight.Matches(issued) ||
                !inFlight.NativeEnded || archived==null || archived.Settings==null || archived.Capture==null || archived.Capture.Settings==null ||
                archived.Kind!=FactoryRunKind.Pressure ||
                !(archived.CaptureOnlyAvailable() || !issued.SkipPressureResidualMeasurement && issued.UseEndResidualThreshold && archived.CompleteCaptureAvailable()) ||
                !inFlight.Matches(archived.Settings) || archived.Capture.Simulation ||
                archived.Capture.Settings.ArtSerial!=issued.Input.ArtSerial || archived.Capture.Settings.PicoSerial!=issued.Input.PicoSerial ||
                archived.Capture.Settings.RangeIndex!=issued.Input.RangeIndex)
                throw new InvalidOperationException("批次缺少匹配的正常采集、原生资源收尾或成功归档收据；不授权下一轮。");
            archived.Capture.ValidateCapture();
            currentRange=issued.Input.RangeIndex; recommendedRange=currentRange; adjacentUpscaleFallback=-1; blocked=null;
            if(issued.SequenceRound<count)
            {
                try
                {
                    var quality=archived.CaptureOnlyAvailable() ? FactoryRangeReview.EvaluateCapturedData(archived) : FactoryRangeReview.Evaluate(archived);
                    recommendedRange=ApplyChargeFloor(FactoryPressureSequence.NextRange(currentRange,quality),issued.Input.PdCalibrationPcPerV);
                    // Only this server's freshly evaluated selected window can
                    // open the adjacent fallback to its own two-step upscale.
                    // It is not a general range interval or an extra start permit.
                    if(quality.WindowValid&&quality.FactoryCycleWindow&&quality.LocationClippedSamples>0&&
                        recommendedRange==currentRange+HVX.Acquisition.BoundedClippedRangePolicy.MaximumIncrease&&
                        ApplyChargeFloor(currentRange+1,issued.Input.PdCalibrationPcPerV)==currentRange+1)
                        adjacentUpscaleFallback=currentRange+1;
                }
                catch(Exception ex)
                {
                    // The already completed round stays completed; only the
                    // next-round authorization is withheld for invalid quality.
                    blocked="上一轮量程评估不能授权续轮："+ex.Message;
                }
            }
            inFlight.Revoke(); inFlight=null; expectedRound=issued.SequenceRound+1;
            ready=expectedRound<=count && blocked==null;
        }
        internal void Revoke()
        {
            if(inFlight!=null)inFlight.Revoke();
            inFlight=null; ready=false; sequence=fingerprint=blocked=null;
            expectedRound=0; adjacentUpscaleFallback=-1;
        }
        // Same application bound as the desktop, after the frozen advisor's
        // safety checks. An adjacent fallback exists only when this server's
        // actual selected-window clipping produced its own bounded +2 advice.
        internal static int ApplyChargeFloor(int recommended,double coefficient)
        {
            if(recommended<0||recommended>=Xiongxin.ZeroBench.BenchSettings.Ranges.Length||
                Double.IsNaN(coefficient)||Double.IsInfinity(coefficient)||coefficient<=0)
                throw new InvalidOperationException("量程或校准系数无效，不能授权自动续轮。");
            for(int i=0;i<Xiongxin.ZeroBench.BenchSettings.Ranges.Length;i++)
                if(Xiongxin.ZeroBench.BenchSettings.Ranges[i]*coefficient>=100)return Math.Max(i,recommended);
            throw new InvalidOperationException("20 V最大档仍不足100 pC，不能授权自动续轮。");
        }
        internal static string Fingerprint(FactorySettings settings)
        {
            var copy=settings.Clone(); copy.BatchPermit=null;
            copy.AutomaticBatch=copy.BatchStartConfirmed=copy.BatchContinuation=false;
            copy.FeedbackWiringConfirmed=copy.IndependentDischargeConfirmed=copy.CalibrationSourceConfirmed=false;
            copy.SequenceId=null; copy.SequenceCount=copy.SequenceRound=copy.SequenceAttempt=1;
            copy.FinalSequenceAi4Observation=false; copy.Input.RunNumber=1; copy.Input.RangeIndex=0;
            using(var writer=new StringWriter(CultureInfo.InvariantCulture))
            {new XmlSerializer(typeof(FactorySettings)).Serialize(writer,copy);return writer.ToString();}
        }
    }
}
