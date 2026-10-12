using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        sealed class ReviewedRangeRetry
        {
            internal string Context,Acquisition;
            internal long Request;
            internal int Next;
            internal bool Explicit,StartedIntent;
            internal PressureOverflowRetryRecord Overflow;
            internal PressureZeroVoltageResult Source;
            internal string EvidencePath,EvidenceHash;
            internal long Action,Generation,RawLength,RawWriteTicks;
        }
        readonly HashSet<int> pressureRangeRetryVisited=new HashSet<int>();
        string pressureRangeRetryContext="";
        long pressureRangeRetryRequest;
        int pressureRangeRetries;
        bool pressureRangeRetrySilent,dispatchingReviewedRangeRetry,pressureQualityDecisionRunning;
        ReviewedRangeRetry reviewedRangeRetry;
        internal Func<RangeDecisionContext,PressureRangeChoice> PressureQualityChoiceForTests;
        string CaptureRangeRetryContextNoLock()
        {return CaptureOperatorPressurePlanContextNoLock()+"|"+selectedPlanStepIndex+"|"+currentPlanRepetition+"|"+requestedTargetPeakVoltageKv.ToString("R",CultureInfo.InvariantCulture);}
        bool HasReviewedRangeStartIntentNoLock()
        {
            var permit=reviewedRangeRetry;
            return dispatchingReviewedRangeRetry&&permit!=null&&permit.Request==pressureRequestSequence-1&&
                permit.Context==CaptureRangeRetryContextNoLock()&&SelectedPicoRangeCode==permit.Next&&
                (permit.Explicit||permit.StartedIntent&&operatorPressureIntentConsumed&&operatorPressureIntentSequence==permit.Request&&
                    operatorPressurePlanContext==CaptureOperatorPressurePlanContextNoLock()&&(permit.Overflow!=null||pressureRangeRetrySilent)&&pressureRangeRetryRequest==permit.Request);
        }
        bool HasRecordedRangeRetryStartIntent(long request)
        {
            lock(stateLock)return operatorPressureIntentConsumed&&request>0&&request==pressureRequestSequence&&
                request==operatorPressureIntentSequence&&operatorPressurePlanContext==CaptureOperatorPressurePlanContextNoLock()&&
                operatorPressureIntentContext==CaptureAutomaticPressureContext();
        }
        bool CurrentPressureQualityDecision(string acquisition,long request,string context)
        {
            return !IsDisposed&&!Disposing&&Visible&&IsHandleCreated&&!IsBusy&&!HasActiveHardwareSession&&
                cycleAcquisitionId==acquisition&&pressureRequestSequence==request&&CaptureRangeRetryContextNoLock()==context&&
                pressureStateCode=="COMPLETE"&&postProcessStateCode=="OVERRANGE_DECISION_REQUIRED"&&
                !finishCurrentRoundRequested&&!operatorPressureStopRequested&&!automaticPlanInterrupted&&!pressureEmergencyLatched&&
                CanRequestVoltageNoLock(requestedTargetPeakVoltageKv);
        }
        // Returns a recorded decision only. The host revokes the previous batch
        // before asking RequestReviewedRangeRetry to publish a fresh request.
        public async Task<string> ReviewPressureOverRangeAsync()
        {
            if(pressureQualityDecisionRunning)return "Pending";
            pressureQualityDecisionRunning=true;
            string id=cycleAcquisitionId,context=CaptureRangeRetryContextNoLock();long request=pressureRequestSequence;
            try {
                var capture=lastPressureCapture;
                if(!CurrentPressureQualityDecision(id,request,context))return "Stale";
                if(capture==null||capture.Quality==null||lastPressureAcquisitionId!=id)throw new InvalidOperationException("本轮量程评估记录缺失，不能自动继续。");
                // Manual range is a separate operator decision, never an
                // automatic range retry or an Ignore-to-save shortcut.
                if(!automaticRange)return await ReviewManualPressureOverRangeAsync(capture,id,request,context);
                if(pressureRangeRetryRequest!=request||pressureRangeRetryContext!=context) {
                    pressureRangeRetryContext=context;pressureRangeRetryRequest=request;pressureRangeRetries=0;
                    // Persisted ordinary reminder choice, not a start permit.
                    pressureRangeRetrySilent=!AutomaticRangeReminderEnabled;pressureRangeRetryVisited.Clear();
                }
                var review=capture.RangeReview;int current=SelectedPicoRangeCode;
                if(review==null&&requestedTargetPeakVoltageKv==0) {
                    var whole=new RangeDecisionContext(capture.Quality,current,capture.Quality.RecommendedIndex,0,pressureRangeRetries,!advancedMode,false,pressureRangeRetrySilent);
                    string why;
                    review=new PressureRangeReviewSnapshot{Quality=capture.Quality,CurrentRangeIndex=current,NextRangeIndex=capture.Quality.RecommendedIndex,
                        CanApply=RangeDecisionPolicy.CanAdjustAndRetry(whole,out why),NextRoundBlocked=!capture.Quality.WindowValid,BlockReason=why};
                }
                if(review==null||current!=review.CurrentRangeIndex)throw new InvalidOperationException("本轮量程身份不一致，不能自动继续。");
                pressureRangeRetryVisited.Add(current);
                bool startedIntent=HasRecordedRangeRetryStartIntent(request);
                int overflowNext;
                if(startedIntent&&CanAutomaticallyUpscaleOverflow(capture,current,out overflowNext)) {
                    var permit=CreateOverflowRetry(capture,id,request,context,current,overflowNext);
                    await Task.Run(()=>WriteOverflowRetry(permit));
                    if(!CurrentPressureQualityDecision(id,request,context)||!OverflowRetryCurrent(permit))return "Stale";
                    reviewedRangeRetry=permit;
                    ResolveOverRangeDecision(false);
                    ShowPressureRangeReview(review,PressureUiText("本轮溢出数据保留，自动升至更高的有效量程重新采集；等待设备和安全检查。","Overflow data retained; retry at a higher calibrated range awaits device and safety checks."));
                    return "Retry";
                }
                var decision=new RangeDecisionContext(review.Quality,current,review.NextRangeIndex,requestedTargetPeakVoltageKv*1000,
                    pressureRangeRetries,!advancedMode,requestedTargetPeakVoltageKv>0,pressureRangeRetrySilent);
                string reason;
                bool bounded=review.CanApply&&!review.NextRoundBlocked&&!pressureRangeRetryVisited.Contains(review.NextRangeIndex);
                bool adjustable=PressureRangeDecisionDialog.CanOfferAdjustment(decision,bounded,out reason);
                if(current==BenchSettings.Ranges.Length-1||!adjustable||pressureRangeRetrySilent&&!startedIntent) {
                    // No usable adjustment means no modal Ignore-only choice.
                    // Keep raw evidence and reject continuation without inventing
                    // acceptance, quality success or hardware-failure evidence.
                    var heldRecord=RangeDecisionPolicy.CreateRecord(decision,RangeDecisionAction.Dismiss);
                    heldRecord.Meaning=current==BenchSettings.Ranges.Length-1?
                        "已到20 V最大量程，不再显示调档窗口；本轮质量未经接受，原始记录保留，未自动保存/分析或继续试验。":
                        !adjustable?reason:"本轮没有有效的已开始意图；提醒偏好不授权重试，原始记录保留。";
                    string heldFile=Path.Combine(capture.Directory,"pressure-quality-held-"+Guid.NewGuid().ToString("N")+".xml");
                    await Task.Run(()=>{using(var stream=new FileStream(heldFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))new XmlSerializer(typeof(RangeDecisionRecord)).Serialize(stream,heldRecord);});
                    if(!CurrentPressureQualityDecision(id,request,context))return "Stale";
                    ResolveOverRangeDecision(false);InterruptPressureContinuation();
                    bool maximumOverflow=current==BenchSettings.Ranges.Length-1&&PressureOverflowUpscale.HasActualOverflow(capture.Capture,capture.OriginalRangeQuality??capture.Quality);
                    ShowPressureRangeReview(review,maximumOverflow?
                        PressureUiText("放电量严重超标，已停止试验。","Discharge range severely exceeded; the test has stopped."):
                        PressureUiText(PressureRangeDecisionDialog.HeldStatus(review.Quality),"Range quality needs review; raw data retained and subsequent rounds stopped."));
                    if(maximumOverflow)pressureRangeReviewDetails=PressureUiText("20 V最大采集档仍存在驱动溢出或原始削顶；请核查信号与校准。ADC异常不单独证明实际放电量或设备故障。本轮未接受、未有效保存或分析。","Driver overflow or raw clipping remains at the 20 V maximum input range. Check the signal and calibration; ADC evidence alone does not prove physical discharge magnitude or a device fault. This round was not accepted, saved as valid or analyzed.")+"\r\n"+pressureRangeReviewDetails;
                    return "Stopped";
                }
                bool automatic=pressureRangeRetrySilent&&startedIntent&&adjustable&&
                    !RangeDecisionPolicy.IsMaximumRangeRisk(decision)&&!RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(decision);
                PressureRangeChoice choice;
                if(automatic)choice=new PressureRangeChoice{Action=RangeDecisionAction.AdjustRetry,Suppress=true,Automatic=true};
                else if(PressureQualityChoiceForTests!=null)choice=PressureQualityChoiceForTests(decision);
                else using(var dialog=new PressureRangeDecisionDialog(decision,FindForm()==null?null:FindForm().Icon,bounded)) {
                    dialog.ShowDialog(FindForm());choice=new PressureRangeChoice{Action=dialog.Decision,Suppress=dialog.SuppressFurtherRangePrompts};
                }
                if(!CurrentPressureQualityDecision(id,request,context))return "Stale";
                if(!automatic)PersistOrdinaryRangePromptSuppression(choice.Suppress);
                bool silent=!AutomaticRangeReminderEnabled;
                var effective=new RangeDecisionContext(review.Quality,current,review.NextRangeIndex,requestedTargetPeakVoltageKv*1000,
                    pressureRangeRetries,!advancedMode,requestedTargetPeakVoltageKv>0,silent);
                if(choice.Action==RangeDecisionAction.AdjustRetry&&!PressureRangeDecisionDialog.CanOfferAdjustment(effective,bounded,out reason))
                    throw new InvalidOperationException("本轮没有合法的新量程可重试；原始记录保留。");
                var record=RangeDecisionPolicy.CreateRecord(effective,choice.Action);
                record.WasAutomaticDecision=automatic;record.SuppressFurtherRangePrompts=choice.Suppress;
                string file=Path.Combine(capture.Directory,"pressure-quality-choice-"+Guid.NewGuid().ToString("N")+".xml");
                await Task.Run(()=>{using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))new XmlSerializer(typeof(RangeDecisionRecord)).Serialize(stream,record);});
                if(!CurrentPressureQualityDecision(id,request,context))return "Stale";
                if(choice.Action==RangeDecisionAction.Ignore) {ResolveOverRangeDecision(true);return "Ignored";}
                if(choice.Action!=RangeDecisionAction.AdjustRetry) {
                    ResolveOverRangeDecision(false);InterruptPressureContinuation();
                    ShowPressureRangeReview(review,PressureUiText("已取消量程处理，原始记录保留；后续轮次已停止。","Range decision canceled; raw data retained and subsequent rounds stopped."));return "Stopped";
                }
                pressureRangeRetrySilent=silent;
                reviewedRangeRetry=new ReviewedRangeRetry{Context=context,Acquisition=id,Request=request,Next=review.NextRangeIndex,Explicit=!automatic,StartedIntent=startedIntent};
                ResolveOverRangeDecision(false);
                ShowPressureRangeReview(review,PressureUiText("已选择调整量程重新试验，等待当前设备和安全检查。","Range retry selected; awaiting device and safety checks."));
                return "Retry";
            }catch {
                if(CurrentPressureQualityDecision(id,request,context)) {
                    ResolveOverRangeDecision(false);InterruptPressureContinuation();
                    ShowPressureRangeReview(lastPressureCapture==null?null:lastPressureCapture.RangeReview,PressureUiText("量程处理未完成，后续轮次已停止；原始记录保留。","Range review incomplete; subsequent rounds stopped and raw data retained."));
                }
                throw;
            }finally {pressureQualityDecisionRunning=false;}
        }
        public bool RequestReviewedRangeRetry()
        {
            bool issued=false;
            RunOnUiThread(delegate {
                var permit=reviewedRangeRetry;
                if(permit==null||permit.Request!=pressureRequestSequence||permit.Acquisition!=cycleAcquisitionId||
                    permit.Context!=CaptureRangeRetryContextNoLock()||IsBusy||HasActiveHardwareSession||
                    postProcessStateCode!="OVERRANGE_REJECTED"||pressureStateCode!="COMPLETE"||
                    pressureEmergencyLatched||automaticPlanInterrupted||finishCurrentRoundRequested||operatorPressureStopRequested||
                    !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv)||pressureRangeRetryVisited.Contains(permit.Next))return;
                if(permit.Overflow!=null&&!OverflowRetryCurrent(permit)){reviewedRangeRetry=null;return;}
                lock(stateLock){selectedRangeCode=RangeCodes[permit.Next];rangeChangedSequence++;}
                UpdateRangeUi();dispatchingReviewedRangeRetry=true;
                try {
                    RequestPressure();issued=pressureRequestSequence==permit.Request+1;
                    if(issued){pressureRangeRetries++;pressureRangeRetryVisited.Add(permit.Next);pressureRangeRetryRequest=pressureRequestSequence;RecordIssuedOverflowRangeNoLock(permit);}
                }finally{dispatchingReviewedRangeRetry=false;reviewedRangeRetry=null;}
            });
            return issued;
        }
    }
}
