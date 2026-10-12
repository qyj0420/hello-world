using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // A completed measurement is not a failed hardware round just because the
    // next round cannot be prepared. No safety/result flags are changed here.
    internal sealed class FactorySequenceStoppedException : InvalidOperationException
    {
        internal readonly int CompletedRounds, RequestedRounds;
        internal readonly FactoryResult LastCompletedRound;
        internal FactorySequenceStoppedException(int done, int total, FactoryResult round, Exception reason)
            : base("连续队列停止（已完成 " + done + " / " + total + "，剩余 " + (total-done) + " 次）：" + reason.Message, reason)
        { CompletedRounds=done; RequestedRounds=total; LastCompletedRound=round; }
    }

    internal static class FactoryPressureSequence
    {
        internal const int InterRoundDelayMs = 2000;
        // One serialized round in the device session, never retries a failed or uncertain
        // round. Automatic batches are authorized once, then each continuation
        // is independently gated by the resident session's completion receipt.
        internal static async Task<int> Run(FactorySettings first, int count,
            Func<FactorySettings,Task<FactoryResult>> run,
            Func<int,int,FactorySettings,Task<bool>> confirmNext,
            Action<int,int,FactorySettings> starting, CancellationToken token,
            Func<FactoryResult,LocationRangeResult,Task<bool>> reviewRange = null,
            FactorySequenceProgress progress = null, Action captured = null, Action<string,string> report = null)
        {
            if (count < 1 || count > 100 || first.TargetKv <= 0 || first.ZeroSynchronizedOptical)
                throw new ArgumentException("连续加压仅用于非零单一目标，次数1～100。");
            progress = progress ?? new FactorySequenceProgress(first,count);
            progress.Begin(first,count);
            var pending = first.Clone(); string id = progress.Id;
            int firstRound = progress.NextRound, completed = progress.Completed;
            FactoryResult lastResult = null;
            Action<string,string> emit = (code,detail) => {
                string text = "已完成 " + completed + " / " + count + "，剩余 " + (count-completed) + " 次；" + detail;
                if(lastResult != null && !String.IsNullOrEmpty(lastResult.DirectoryPath)) FactorySequenceAudit.Save(lastResult,code,text,completed,count);
                if(report != null)report(code,text);
            };
            try
            {
            for (int i = firstRound; i <= count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > firstRound)
                {
                    // A previous request's assertions are not current evidence.
                    pending.FeedbackWiringConfirmed = pending.IndependentDischargeConfirmed = false;
                    pending.BatchPermit = null;
                    if (first.AutomaticBatch)
                    {
                        pending.BatchStartConfirmed = false;
                        pending.BatchContinuation = true;
                        emit("BATCH_AUTOMATIC_CONTINUATION", "同批次自动准备第 " + i + " 轮；实际开始仍核验当前急停许可、上一轮保存和资源收尾。");
                    }
                    else if (!pending.Simulation)
                    {
                        emit("SEQUENCE_WAIT_CONFIRM", "下一轮 " + i + " 待现场确认；量程 ±" + BenchSettings.Ranges[pending.Input.RangeIndex] + " V。");
                        if (confirmNext == null || !await confirmNext(i,count,pending.Clone()))
                        { emit("SEQUENCE_CANCELLED", "未确认下一轮，队列结束；不是已完成全部轮次。"); break; }
                        token.ThrowIfCancellationRequested();
                        pending = FactoryStartConfirmation.Accept(pending,FactoryRunKind.Pressure,true);
                    }
                }
                pending.SequenceId=id; pending.SequenceCount=count; pending.SequenceRound=i;
                pending.FinalSequenceAi4Observation=pending.UseCaptureOnlyEnd && i==count;
                pending.SequenceAttempt=progress.Attempt;
                pending.Input.RunNumber=i;
                // A transport request is not an output permit. The resident
                // worker validates again with its non-serialized permit.
                if (pending.AutomaticBatch && pending.BatchContinuation)
                    pending.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                else pending.Validate(FactoryRunKind.Pressure);
                progress.Starting(pending);
                if (starting != null) starting(i,count,pending.Clone());
                var issued=pending.Clone(); var result=await run(issued);
                token.ThrowIfCancellationRequested();
                if (result==null || !FactoryCapturePreview.Matches(pending,FactoryRunKind.Pressure,result) ||
                    result.Settings.Input.RangeIndex!=pending.Input.RangeIndex ||
                    !(result.CaptureOnlyAvailable() || result.CompleteCaptureAvailable()))
                    throw new InvalidOperationException("本轮未正常结束或记录身份不一致，停止连续队列。");
                result.Capture.ValidateCapture();
                if(result.Capture.Simulation!=pending.Simulation ||
                    result.Capture.Settings.RangeIndex!=pending.Input.RangeIndex ||
                    result.Capture.Settings.ArtSerial!=pending.Input.ArtSerial || result.Capture.Settings.PicoSerial!=pending.Input.PicoSerial)
                    throw new InvalidOperationException("采集来源与连续任务不一致，停止队列。");
                progress.Captured(); completed=progress.Completed;
                lastResult = result;
                emit("SEQUENCE_CAPTURED", "本轮采集已保存。");
                if(captured!=null)captured();
                if (i == count) break;
                LocationRangeResult quality=null;
                try
                {
                    quality=result.CaptureOnlyAvailable() ? FactoryRangeReview.EvaluateCapturedData(result) : FactoryRangeReview.Evaluate(result);
                    bool apply=reviewRange==null || await reviewRange(result,quality);
                    token.ThrowIfCancellationRequested();
                    // Present/log an invalid window or overflow before stopping.
                    // The dialog still cannot apply an invalid recommendation.
                    int recommended=NextRange(result.Capture.Settings.RangeIndex,quality);
                    if(first.AutomaticBatch)recommended=FactoryBatchAuthorization.ApplyChargeFloor(recommended,result.Capture.Settings.PdCalibrationPcPerV);
                    pending=first.Clone();
                    pending.Input.RangeIndex=apply ? recommended : result.Capture.Settings.RangeIndex;
                    progress.Prepared(pending);
                    emit("SEQUENCE_PREPARED", "下一轮量程 ±" + BenchSettings.Ranges[pending.Input.RangeIndex] + " V；" + quality.WindowDefinition);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    string reason=ex.Message;
                    if(quality!=null && !String.IsNullOrEmpty(result.DirectoryPath))
                    {
                        var blocked=FactoryNextRangeReview.Decide(result,quality,result.Capture.Settings.RangeIndex,false);
                        blocked.Action="StopQueue";blocked.Reason=reason;blocked.Meaning="不发下一轮："+reason;
                        FactoryNextRangeReview.Save(result,blocked);
                    }
                    emit("SEQUENCE_STOPPED", reason);
                    throw new FactorySequenceStoppedException(completed,count,result,new InvalidOperationException(reason,ex));
                }
                // run has returned only after round cleanup, completed-data loading
                // and release. Do not insert this delay in the energized handoff.
                await WaitBetweenRounds(emit,token);
            }
            emit("SEQUENCE_END", (completed==count ? "本次队列全部采集完成。" : "队列提前结束，未完成轮次未扣除。") +
                (lastResult!=null && lastResult.FinalAi4Observation!=null ? " " + lastResult.FinalAi4Observation.Describe() : ""));
            return completed;
            }
            finally { progress.End(); }
        }

        internal static async Task WaitBetweenRounds(Action<string,string> emit,CancellationToken token)
        {
            var elapsed=Stopwatch.StartNew();
            emit("SEQUENCE_INTERVAL_WAIT","本轮资源收尾完成；下一轮前等待至少 2 秒，可点击停止；底层共享冷却门仍独立校验。");
            try
            {
                while(elapsed.ElapsedMilliseconds<InterRoundDelayMs)
                    await Task.Delay(Math.Max(1,(int)(InterRoundDelayMs-elapsed.ElapsedMilliseconds)),token);
                token.ThrowIfCancellationRequested();
                emit("SEQUENCE_INTERVAL_READY","轮间等待完成，实际 "+elapsed.ElapsedMilliseconds+" ms；不代表放电确认。");
            }
            catch(OperationCanceledException)
            {emit("SEQUENCE_INTERVAL_CANCELLED","轮间等待已停止，实际 "+elapsed.ElapsedMilliseconds+" ms；未启动下一轮。");throw;}
        }

        internal static int NextRange(int current, LocationRangeResult q)
        {
            if(q==null || !q.WindowValid || q.RecommendedIndex<0 || q.RecommendedIndex>=BenchSettings.Ranges.Length)
                throw new InvalidOperationException("无有效量程评估窗口，停止连续队列；本轮原始数据已保留。" + (q==null ? "无评估结果。" : q.WindowDefinition + "；" + q.Reason));
            if(q.AtMaximumAndClipped || q.MaximumRangeInsufficient || FactoryTransientRangePolicy.BlocksForOverflow(q,current))
                throw new InvalidOperationException("量程已不足或驱动溢出原因不明，停止连续队列；不自动重试。");
            // Unlocalized/full-segment clipping prevents lowering, except for the
            // explicitly evidenced switching prefix. In-window clipping only upranges.
            if (FactoryTransientRangePolicy.BlocksDownrange(q) && q.RecommendedIndex<current)
                return current;
            // Only localized sustained/distributed raw clipping can use two
            // upward steps. Isolated clipping and every decrease stay adjacent.
            // The frozen quality/optimum remains unchanged and proves no permit.
            return HVX.Acquisition.BoundedClippedRangePolicy.Next(current,q.RecommendedIndex,BenchSettings.Ranges.Length,
                q.WindowValid&&q.FactoryCycleWindow,q.LocationSampleCount,q.LocationClippedSamples,q.LocationLongestClippedRun);
        }
    }
}
