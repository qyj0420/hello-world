using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.ZeroBench;
using Xiongxin.OWTS.Noise;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        readonly object zeroHardwareLock = new object();
        string zeroHardwareSettings = "", lastZeroCaptureDirectory = "";
        Func<string, CancellationToken, Task<string>> zeroHardwareCapture;
        Func<string, double, CancellationToken, Task<string>> pressureHardwareCapture;
        Func<string,string,double,string> pressureRangeReview;
        public void ConfigurePressureRangeReview(Func<string,string,double,string> review)
        {
            if(review==null)throw new ArgumentNullException("review");
            lock(zeroHardwareLock){if(zeroHardwareRunning)throw new InvalidOperationException("Finish the active capture first.");pressureRangeReview=review;}
        }
        PressureZeroVoltageResult lastPressureCapture;
        string lastPressureAcquisitionId = "";
        public void ConfigurePressureHardwareBackend(string settingsXml, Func<string,double,CancellationToken,Task<string>> captureAsync)
        {
            if(captureAsync==null)throw new ArgumentNullException("captureAsync");
            ConfigureZeroVoltageHardwareBackend(settingsXml,(xml,token)=>captureAsync(xml,0,token));
            lock(zeroHardwareLock)pressureHardwareCapture=captureAsync;
        }
        CancellationTokenSource zeroHardwareCancellation;
        long zeroHardwareGeneration;
        long zeroLastClaimedActionSequence;
        ZeroActionOwner zeroActionOwner;
        bool zeroHardwareRunning, zeroRangeSilentSession;
        internal Action<string> ZeroReferenceNoticeForTests;
        Form zeroRangeDialog;
        System.Windows.Forms.Timer zeroContextTimer;
        readonly HashSet<string> zeroCaptureDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal Func<RangeDecisionContext, PressureRangeChoice> ZeroRangeDecisionForTests = null;
        internal Func<bool, bool> ZeroTriggerConfirmationForTests = null;
        internal Action ZeroBeforeActionAcknowledgementForTests = null;

        // The delegate must not finish until its owned input worker has exited and
        // released both devices. This control never opens a driver or owns an SDK handle.
        public void ConfigureZeroVoltageHardwareBackend(string settingsXml, Func<string, CancellationToken, Task<string>> captureAsync)
        {
            if (captureAsync == null) throw new ArgumentNullException("captureAsync");
            var settings = PressureZeroVoltageCapture.ReadSettings(settingsXml);
            RunOnUiThread(delegate {
                lock (zeroHardwareLock)
                {
                    if (zeroHardwareRunning || IsBusy) throw new InvalidOperationException("Finish the current action before configuring the input backend.");
                    if (IsDisposed || Disposing) throw new ObjectDisposedException(GetType().Name);
                    zeroHardwareSettings = PressureZeroVoltageCapture.SettingsXml(settings);
                    zeroHardwareCapture = captureAsync; zeroHardwareGeneration++;
                    zeroRangeSilentSession = false;
                }
                ActionHandshakeEnabled = true;
            });
        }

        public void ClearZeroVoltageHardwareBackend()
        {
            RequestHardwareStop();
            lock (zeroHardwareLock) { zeroHardwareCapture = null; pressureHardwareCapture=null; pressureRangeReview=null; zeroHardwareSettings = ""; zeroHardwareGeneration++; zeroRangeSilentSession = false; }
        }
        [Browsable(false)] public bool HasActiveHardwareSession { get { lock (zeroHardwareLock) return zeroHardwareRunning; } }
        [Browsable(false)] public string LastZeroVoltageCaptureDirectory { get { lock (zeroHardwareLock) return lastZeroCaptureDirectory; } }

        // A diagnostic outcome for one owned request, not a hardware stop or
        // release acknowledgement. Only explicit pre-dispatch UI cancellation
        // may set it; a stale callback cannot classify a later request.
        public bool ReportPressurePreparationCanceled(long sequence)
        {
            bool marked=false;
            RunOnUiThread(delegate {
                lock(stateLock) lock(zeroHardwareLock) {
                    var owner=zeroActionOwner;
                    if(owner==null||owner.Sequence!=sequence||owner.DispatchCommitted||
                        owner.Cancellation.IsCancellationRequested||!ActionPending||ActionSequence!=sequence||
                        !Visible||!IsHandleCreated||pressureEmergencyLatched||automaticPlanInterrupted||
                        !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv))return;
                    owner.OperatorCanceledBeforeDispatch=true;marked=true;
                }
            });
            return marked;
        }
        public bool WasPressurePreparationCanceled(long sequence)
        {
            lock(zeroHardwareLock)return zeroActionOwner!=null&&zeroActionOwner.Sequence==sequence&&
                (zeroActionOwner.OperatorCanceledBeforeDispatch&&!zeroActionOwner.DispatchCommitted||
                 zeroActionOwner.OperatorStopRequested&&(!zeroActionOwner.DispatchCommitted||zeroActionOwner.NativeNeverEnteredConfirmed));
        }
        public bool ReportPressureOperatorStop(long sequence)
        {
            lock(zeroHardwareLock) {
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!ActionPending||ActionSequence!=sequence)return false;
                zeroActionOwner.OperatorStopRequested=true;return true;
            }
        }
        // The host may call this only after verifying the specific supervised
        // launch's pre-native evidence. Never call merely because a worker quit.
        public bool ReportPressureCaptureNeverStarted(long sequence)
        {
            lock(zeroHardwareLock) {
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted)return false;
                zeroActionOwner.NativeNeverEnteredConfirmed=true;return true;
            }
        }
        // Host calls only after checking the typed bridge receipt's exact
        // request + session identities. This does not accept data or clear DI.
        public bool ReportPressureRequestRejected(long sequence)
        {
            lock(zeroHardwareLock) {
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted||
                    !ActionPending||ActionSequence!=sequence||!String.IsNullOrEmpty(zeroActionOwner.FailureOutcomeStatus))return false;
                zeroActionOwner.NativeNeverEnteredConfirmed=true;
                zeroActionOwner.RequestRejectedBeforeNative=true;return true;
            }
        }
        public bool ReportPressureInputStopConfirmed(long sequence)
        {
            lock(zeroHardwareLock){
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted)return false;
                zeroActionOwner.InputStopConfirmed=true;return true;
            }
        }
        public bool WasPressureOperatorInputStopped(long sequence)
        {
            lock(zeroHardwareLock)return zeroActionOwner!=null&&zeroActionOwner.Sequence==sequence&&
                zeroActionOwner.OperatorStopRequested&&zeroActionOwner.InputStopConfirmed;
        }
        public bool ReportPressureRoundStopConfirmed(long sequence)
        {
            lock(zeroHardwareLock){
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted)return false;
                zeroActionOwner.RoundStopConfirmed=true;return true;
            }
        }
        public bool WasPressureOperatorRoundStopped(long sequence)
        {
            lock(zeroHardwareLock)return zeroActionOwner!=null&&zeroActionOwner.Sequence==sequence&&
                zeroActionOwner.OperatorStopRequested&&zeroActionOwner.RoundStopConfirmed;
        }
        // The host invokes only after validating the exact worker's retained-DI
        // emergency receipt. This records round termination, never a HIGH/LOW
        // sample, electrical readiness, or authorization for another round.
        public bool ReportPressureEmergencyRoundStopConfirmed(long sequence)
        {
            lock(zeroHardwareLock){
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted)return false;
                zeroActionOwner.EmergencyRoundStopConfirmed=true;return true;
            }
        }
        // Pure operator-facing classification from the Host's exact typed
        // failure. It changes no stop evidence, readiness, quality or ownership.
        public bool ReportPressureCaptureFailureStatus(long sequence,string status)
        {
            if(status!="SessionEndedAfterFailedRelease"&&status!="StopReleaseUnconfirmed"&&status!="WorkerSessionExited")return false;
            lock(zeroHardwareLock){
                if(zeroActionOwner==null||zeroActionOwner.Sequence!=sequence||!zeroActionOwner.DispatchCommitted)return false;
                zeroActionOwner.FailureOutcomeStatus=status;return true;
            }
        }

        // A request is not proof of a stopped device. HasActiveHardwareSession
        // remains true until the worker delegate has acknowledged termination.
        public bool RequestHardwareStop()
        {
            CancellationTokenSource source;
            lock (zeroHardwareLock) { source = zeroHardwareCancellation; }
            if (source == null) return false;
            try { source.Cancel(); } catch (ObjectDisposedException) { }
            if (IsHandleCreated && !IsDisposed && !Disposing)
            {
                try { BeginInvoke((Action)delegate { if (zeroRangeDialog != null && !zeroRangeDialog.IsDisposed) zeroRangeDialog.Close(); }); }
                catch (InvalidOperationException) { }
            }
            return true;
        }

        public async Task<bool> ExecuteZeroVoltageHardwareActionAsync(long sequence)
        {
            ZeroActionOwner owner = null;
            ZeroCaptureContext context = null;
            bool success = false;
            bool operatorCanceled = false;
            bool operatorStopped = false;
            bool rangeQualityBlocked = false;
            PressureZeroVoltageResult finalResult = null;
            string error = "";
            try
            {
                RunOnUiThread(delegate {
                    owner = ClaimZeroAction(sequence);
                    if (owner != null)
                    {
                        context = BeginZeroCapture(owner);
                        // Ownership changes the allowed UI branch: a real stop
                        // request must remain reachable even in manual mode.
                        UpdateActionBusyUi();
                    }
                });
                if (context == null) return false;
                var settings = context.Settings;
                var visited = new HashSet<int>();
                int retries = 0;
                // Reminder persistence is not trigger/output permission. The
                // owned request and (for the integrated backend) consumed start
                // intent must still precede a bounded range chain.
                bool automaticRetries = settings.AutoRange && !AutomaticRangeReminderEnabled;
                if(context.PressureCapture!=null&&!HasRecordedRangeRetryStartIntent(pressureRequestSequence))automaticRetries=false;
                bool chainTriggerAuthorized = false;
                if (!ConfirmZeroTrigger(context, false)) {
                    ReportPressurePreparationCanceled(owner.Sequence);
                    throw new OperationCanceledException("本轮采集未获确认，尚未启动。");
                }
                while (true)
                {
                    EnsureZeroCaptureCurrent(context);
                    if (!visited.Add(settings.RangeIndex)) throw new InvalidOperationException("A previously tried range cannot be retried automatically.");
                    DateTime startedUtc = DateTime.UtcNow;
                    string requestXml = PressureZeroVoltageCapture.SettingsXml(settings);
                    // Legacy input delegates have no preparation-commit API.
                    // Once invoked, their native entry is unknown, never benign
                    // merely because the integrated host marker was not set.
                    if(context.PressureCapture==null)lock(zeroHardwareLock)owner.DispatchCommitted=true;
                    string returned = await Task.Run(() => context.PressureCapture==null ? context.Capture(requestXml, context.Cancellation.Token) :
                        context.PressureCapture(requestXml, context.TargetKv, context.Cancellation.Token)).ConfigureAwait(false);
                    EnsureZeroCaptureCurrent(context);
                    string directory = PressureZeroVoltageCapture.CaptureDirectory(context.Project, returned);
                    lock (zeroHardwareLock)
                        if (!zeroCaptureDirectories.Add(directory)) throw new InvalidDataException("The worker returned an already used capture directory.");
                    var requested = settings;
                    if(context.TargetKv>0&&context.RangeReview==null)throw new InvalidOperationException("缺少本轮量程评估接口，原始数据已保留。");
                    Func<PressureRangeReviewSnapshot> reviewPreparation=context.TargetKv>0 ?
                        (Func<PressureRangeReviewSnapshot>)(()=>PressureRangeReviewSnapshot.Read(context.RangeReview(directory,requestXml,context.TargetKv),requested.RangeIndex)) : null;
                    // Range review consumes original ADC in the configured N-cycle
                    // voltage window; it need not wait for the separate full-segment
                    // HP/display conversion. Both readers validate their own input.
                    // Join them before publication: neither is an output permission.
                    var readPreparation=await PressureCapturePreparation.RunAsync(
                        ()=>PressureZeroVoltageCapture.Read(directory,requested,startedUtc,context.Cancellation.Token,context.TargetKv,context.PhaseVoltageRms),
                        reviewPreparation,context.Cancellation.Token).ConfigureAwait(false);
                    var result=readPreparation.Display;
                    var prepared=await PressureCapturePreparation.RunAsync(
                        ()=>PressureWaveformView.PrepareData(result.VoltageKv,result.Capture.ArtRateHz,result.ChargePc,result.Capture.PicoRateHz,context.Cancellation.Token),
                        (Func<PressureRangeReviewSnapshot>)null,context.Cancellation.Token).ConfigureAwait(false);
                    EnsureZeroCaptureCurrent(context);
                    if(readPreparation.Review!=null){result.RangeReview=readPreparation.Review;result.Quality=readPreparation.Review.Quality;}
                    result.DisplayPreparationMilliseconds=prepared.DisplayMilliseconds;
                    result.RangeReviewMilliseconds=readPreparation.ReviewMilliseconds;
                    result.PostReadPreparationMilliseconds=readPreparation.TotalMilliseconds+prepared.TotalMilliseconds;
                    result.OriginalRangeQuality=result.Quality;
                    result.Quality=PressureChargeRangeFloor.Bound(result.Quality,requested.RangeIndex,context.CalibrationScale);
                    if(result.RangeReview!=null) {
                        PressureChargeRangeFloor.BoundReview(result.RangeReview,requested.RangeIndex,context.CalibrationScale);
                        result.Quality=result.RangeReview.Quality;
                    }
                    EnsureZeroCaptureCurrent(context);
                    PressureZeroVoltageCapture.WriteEvidence(result, context.AcquisitionId, context.Phase, context.CalibrationSource, null,context.TargetKv,context.AnalyzeZero);
                    var metadata = ZeroCaptureMetadata(context, result);
                    ZeroVoltageNoiseReference noise = null;
                    try { if(context.TargetKv==0)noise = await Task.Run(() => new ZeroVoltageNoiseReference(result.ChargePc, NoiseAcquisitionContext.Read(metadata))).ConfigureAwait(false); }
                    catch (ArgumentException) { /* Keep the complete raw data; an unsupported reference is explicitly pending. */ }
                    var display = prepared.Display;
                    EnsureZeroCaptureCurrent(context);
                    RunOnUiThread(delegate { RequireZeroCaptureCurrent(context); InstallZeroCapture(context, result, metadata, noise, display); });
                    finalResult=result;

                    // A range retry at nonzero voltage is a new HV operation, never
                    // authorized by the zero-input silent-retry preference.
                    if(context.TargetKv>0){success=true;break;}

                    var decisionContext = new RangeDecisionContext(result.Quality, settings.RangeIndex, result.Quality.RecommendedIndex,
                        0, retries, false, false, automaticRetries);
                    bool uncertain=!result.Quality.WindowValid||result.Quality.DriverOverflowUncertain||
                        result.Quality.RecommendationUncertain&&result.Quality.LocationClippedSamples==0;
                    if(PressureChargeRangeFloor.MustHoldAtMaximum(result.Quality,settings.RangeIndex)||uncertain)
                    {
                        var held=RangeDecisionPolicy.CreateRecord(decisionContext,RangeDecisionAction.Dismiss);
                        held.Meaning="量程质量待复核，未接受本轮质量；保留原始数据并停止后续轮次。";
                        PressureZeroVoltageCapture.WriteEvidence(result,context.AcquisitionId,context.Phase,context.CalibrationSource,held,context.TargetKv,context.AnalyzeZero);
                        rangeQualityBlocked=true;success=true;break;
                    }
                    if (PressureChargeRangeFloor.IsLowOnlyAtFloor(result.Quality,settings.RangeIndex,context.CalibrationScale) ||
                        !PressureZeroVoltageCapture.NeedsDecision(settings, result.Quality) ||
                        automaticRetries && result.Quality.LocationQualityPassed && !result.Quality.MaximumRangeInsufficient &&
                        !RangeDecisionPolicy.IsMaximumRangeRisk(decisionContext) && !RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(decisionContext))
                    { success = true; break; }

                    // A hidden ordinary reminder is not permission to retry.
                    // In the integrated path the host consumes start intent
                    // inside Capture, after automaticRetries was snapshotted.
                    // Preserve the clean low-amplitude result and actual range
                    // when this request has no authorized automatic retry chain.
                    if(!automaticRetries&&!AutomaticRangeReminderEnabled&&
                        PressureChargeRangeFloor.IsOrdinaryLowRangeAdvice(result.Quality,settings.RangeIndex))
                    {
                        var hidden=RangeDecisionPolicy.CreateRecord(decisionContext,RangeDecisionAction.Dismiss);
                        hidden.SuppressFurtherRangePrompts=true;
                        hidden.Meaning="普通量程提醒已按保存偏好隐藏；保留原始幅值不足标记，未接受质量、未调档或重采。";
                        PressureZeroVoltageCapture.WriteEvidence(result,context.AcquisitionId,context.Phase,context.CalibrationSource,hidden,context.TargetKv,context.AnalyzeZero);
                        success=true;break;
                    }

                    PressureRangeChoice choice = null;
                    string reason;
                    bool adjustable=PressureRangeDecisionDialog.CanOfferAdjustment(decisionContext,!visited.Contains(decisionContext.RecommendedRangeIndex),out reason);
                    if(!adjustable)
                    {
                        var held=RangeDecisionPolicy.CreateRecord(decisionContext,RangeDecisionAction.Dismiss);held.Meaning=reason;
                        PressureZeroVoltageCapture.WriteEvidence(result,context.AcquisitionId,context.Phase,context.CalibrationSource,held,context.TargetKv,context.AnalyzeZero);
                        rangeQualityBlocked=true;success=true;break;
                    }
                    bool canSilentRetry = automaticRetries && adjustable;
                    if (canSilentRetry) choice = new PressureRangeChoice { Action = RangeDecisionAction.AdjustRetry, Suppress = true, Automatic = true };
                    else RunOnUiThread(delegate { RequireZeroCaptureCurrent(context); choice = GetZeroRangeChoice(decisionContext); });
                    EnsureZeroCaptureCurrent(context);
                    if(!choice.Automatic)PersistOrdinaryRangePromptSuppression(choice.Suppress);
                    bool enableAutomatic = settings.AutoRange&&!AutomaticRangeReminderEnabled;
                    var effective = new RangeDecisionContext(result.Quality, settings.RangeIndex, result.Quality.RecommendedIndex, 0, retries, false, false, enableAutomatic);
                    var record = RangeDecisionPolicy.CreateRecord(effective, choice.Action);
                    record.SuppressFurtherRangePrompts = choice.Suppress;
                    record.WasAutomaticDecision = choice.Automatic;
                    PressureZeroVoltageCapture.WriteEvidence(result, context.AcquisitionId, context.Phase, context.CalibrationSource, record,context.TargetKv,context.AnalyzeZero);
                    if (choice.Action == RangeDecisionAction.Ignore)
                    {
                        RunOnUiThread(delegate { RequireZeroCaptureCurrent(context); lock (stateLock) overRangeIgnored = true; });
                        success = true; break;
                    }
                    if (choice.Action != RangeDecisionAction.AdjustRetry) throw new OperationCanceledException("The raw capture is preserved; its range quality was not accepted.");
                    if (visited.Contains(effective.RecommendedRangeIndex)) throw new InvalidOperationException("Automatic range search would revisit an earlier range; raw rounds are preserved for inspection.");
                    if (!chainTriggerAuthorized)
                    {
                        if (!ConfirmZeroTrigger(context, enableAutomatic)) throw new OperationCanceledException("The retry trigger was not authorized; raw data is preserved.");
                        chainTriggerAuthorized = enableAutomatic;
                    }
                    automaticRetries = enableAutomatic;
                    if (choice.Suppress) zeroRangeSilentSession = true;
                    settings = RangeDecisionPolicy.CreateRetrySettings(settings, effective);
                    RunOnUiThread(delegate {
                        RequireZeroCaptureCurrent(context);
                        context.RangeIndex = settings.RangeIndex;
                        lock (stateLock) selectedRangeCode = RangeCodes[settings.RangeIndex];
                        // A finite, recorded zero-input range decision only;
                        // consumed once by the shared host before the next wait.
                        lock(zeroHardwareLock)owner.RetryPermitRange=settings.RangeIndex;
                        UpdateRangeUi();
                    });
                    retries++;
                }
                if(success&&!rangeQualityBlocked&&context.TargetKv==0&&PressureZeroVoltageCapture.ShouldWarnSmallReference(finalResult))
                    RunOnUiThread(delegate {
                        RequireZeroCaptureCurrent(context);
                        string notice=PressureUiText("校准可能存在档位混淆。", "The calibration range may be mixed up.");
                        if(ZeroReferenceNoticeForTests!=null)ZeroReferenceNoticeForTests(notice);
                        else MessageBox.Show(FindForm(),notice,PressureUiText("提示", "Notice"),MessageBoxButtons.OK,MessageBoxIcon.Information);
                    });
            }
            catch (Exception exception)
            {
                operatorCanceled = exception is OperationCanceledException && owner!=null && WasPressurePreparationCanceled(owner.Sequence);
                operatorStopped = exception is OperationCanceledException && owner!=null &&
                    (WasPressureOperatorInputStopped(owner.Sequence)||WasPressureOperatorRoundStopped(owner.Sequence));
                error = PressureCaptureFailureText(exception);
                string outcomeStatus=owner==null?null:owner.FailureOutcomeStatus;
                if(outcomeStatus=="SessionEndedAfterFailedRelease")error=PressureUiText(
                    "本轮采集未完成；控制会话已主动结束，需要重新连接。原始数据保留，不自动继续。",
                    "The capture did not complete. Its control session has ended; reconnect explicitly. Raw data are retained; no automatic restart.");
                else if(outcomeStatus=="StopReleaseUnconfirmed")error=PressureUiText(
                    "本轮停止尚未确认。请保持高压停止，按现场规程检查设备；不会自动继续。",
                    "This round has not been confirmed as stopped. Keep high voltage stopped and check the equipment under site procedures. The test will not resume automatically.");
                else if(outcomeStatus=="WorkerSessionExited")error=PressureUiText(
                    "设备操作异常，停止状态待确认。请按现场规程检查设备后重新连接；原始数据保留。",
                    "The equipment operation failed and its stop state is unconfirmed. Check the equipment under site procedures before reconnecting; original data are retained.");
            }
            finally
            {
                if (owner != null)
                {
                    bool acknowledged = false;
                    try
                    {
                        RunOnUiThread(delegate {
                            try
                            {
                                if (zeroContextTimer != null) { zeroContextTimer.Stop(); zeroContextTimer.Dispose(); zeroContextTimer = null; }
                                if (context == null || ZeroCaptureCurrent(context, false))
                                {
                                    lock (stateLock) {
                                        if(owner.EmergencyRoundStopConfirmed) {
                                            pressureEmergencyLatched=true;
                                            automaticPlanInterrupted=true;
                                            InvalidateP04SchedulingNoLock();
                                        }
                                        // A later safety/context failure must still win over
                                        // an earlier click on Cancel. No result is fabricated.
                                        bool knownUnstartedStop=owner.OperatorStopRequested&&(!owner.DispatchCommitted||owner.NativeNeverEnteredConfirmed);
                                        operatorCanceled=operatorCanceled&&context!=null&&ZeroCaptureCurrent(context,!knownUnstartedStop)&&
                                            (!owner.DispatchCommitted||knownUnstartedStop)&&!pressureEmergencyLatched&&(!automaticPlanInterrupted||knownUnstartedStop)&&
                                            CanRequestVoltageNoLock(context.TargetKv);
                                        operatorStopped=operatorStopped&&context!=null&&ZeroCaptureCurrent(context,false)&&!pressureEmergencyLatched&&
                                            (context.TargetKv==0&&owner.InputStopConfirmed&&CanRequestVoltageNoLock(0)||
                                             owner.RoundStopConfirmed&&CanRequestVoltageNoLock(context.TargetKv));
                                        bool requestRejected=owner.RequestRejectedBeforeNative&&context!=null&&ZeroCaptureCurrent(context,false)&&
                                            String.IsNullOrEmpty(owner.FailureOutcomeStatus)&&!pressureEmergencyLatched&&CanRequestVoltageNoLock(context.TargetKv);
                                        if(requestRejected)error=PressureUiText("本轮未开始，请核对设置后重新点击开始。","This round did not start. Check the settings, then click Start again.");
                                        pressureStateCode = success ? "COMPLETE" : owner.EmergencyRoundStopConfirmed||operatorStopped ? "STOPPED" : operatorCanceled||requestRejected ? "WAITING" : "FAULT";
                                        lastError = lastPostProcessError = error;
                                        lastActionCode = success ? "ZERO_REFERENCE_CAPTURED" : owner.EmergencyRoundStopConfirmed ? "PRESSURE_EMERGENCY_STOPPED" : operatorStopped ? owner.RoundStopConfirmed?"PRESSURE_ROUND_STOPPED":"INPUT_CAPTURE_STOPPED" : operatorCanceled ? "PRESSURE_PREPARATION_CANCELED" : requestRejected ? "PRESSURE_REQUEST_REJECTED" : "ZERO_CAPTURE_STOPPED";
                                        SetPostProcessStateNoLock(success ? context!=null&&(context.TargetKv>0||context.AnalyzeZero) ? "WAITING_FOR_ACQUISITION" : "COMPLETE" : owner.EmergencyRoundStopConfirmed||operatorCanceled||operatorStopped ? "CANCELED" : requestRejected ? "READY" : "ACQUISITION_FAILED");
                                        if(success&&rangeQualityBlocked) {
                                            dataSaved=dataAnalyzed=deleteEnabled=false;overRangeIgnored=false;
                                            lastActionCode="RANGE_QUALITY_RETAINED";
                                            lastPostProcessError=PressureUiText(PressureRangeDecisionDialog.HeldStatus(finalResult.Quality),"Range quality needs review; raw data retained and subsequent rounds stopped.");
                                            SetPostProcessStateNoLock(finalResult.Quality.WindowValid?"OVERRANGE_REJECTED":"ACQUISITION_INVALID");
                                        }
                                    }
                                    UpdatePostProcessUi(); UpdatePressureUi();
                                }
                                if (ZeroBeforeActionAcknowledgementForTests != null) ZeroBeforeActionAcknowledgementForTests();
                                // A stop can arrive after the capture result was installed
                                // but before its mailbox acknowledgement. Keep that display,
                                // discard the round, and do not leave a save-wait state behind.
                                // This branch requires an actual successful worker completion;
                                // a failed/unknown shutdown never becomes STOPPED here.
                                if (success && owner.OperatorStopRequested)
                                {
                                    success = false;
                                    error = PressureUiText("本轮已中断，数据仅供查看，未保存或分析。", "This round was interrupted; data is display-only and was not saved or analyzed.");
                                    if (context != null && ZeroCaptureCurrent(context, false))
                                    {
                                        lock (stateLock)
                                        {
                                            pressureStateCode = pressureEmergencyLatched ? "FAULT" : "STOPPED";
                                            lastError = lastPostProcessError = error;
                                            lastActionCode = "INTERRUPTED_ROUND_DISCARDED";
                                            dataSaved = dataAnalyzed = deleteEnabled = false;
                                            SetPostProcessStateNoLock("CANCELED");
                                        }
                                        UpdatePostProcessUi(); UpdatePressureUi();
                                    }
                                }
                                // Only the unique claimant may ACK. Keep its lease
                                // through the UI acknowledgement, not merely until
                                // the worker exits; a rejected duplicate has no ACK path.
                                acknowledged = CompleteAction(owner.Sequence, success, error);
                            }
                            finally
                            {
                                // CompleteAction deliberately acknowledges while
                                // this owner still holds the acquisition lease.
                                // Refresh once more after the real release: zero
                                // references and canceled/failed rounds may have
                                // no save/analysis callback to unlock the controls.
                                // Nonzero rounds still stay locked by their pending
                                // save/analysis state; this never grants HV permission.
                                if (ReleaseZeroAction(owner) && !IsDisposed && !Disposing)
                                {
                                    // LOW/Stop may arrive while the real owner still
                                    // holds its lease. Re-evaluate only after its ACK
                                    // and release; a legacy finish-current-round alone
                                    // still preserves normal valid-round saving.
                                    if (operatorPressureStopRequested || automaticPlanInterrupted || pressureEmergencyLatched)
                                        CancelIdlePressureDecisionForStop();
                                    UpdateActionBusyUi(); UpdatePostProcessUi();
                                }
                            }
                            if(acknowledged&&(owner.RequestRejectedBeforeNative||success&&rangeQualityBlocked))InterruptPressureContinuation();
                            if(acknowledged&&success&&!rangeQualityBlocked&&context!=null&&(context.TargetKv>0||context.AnalyzeZero))ReportAcquisitionCompleted(lastAcquisitionOverRange);
                            if(acknowledged&&success&&!rangeQualityBlocked&&context!=null&&context.TargetKv==0&&!context.AnalyzeZero)
                                BeginInvoke((Action)(async()=>{try{await ContinueCompletedPressurePlanAsync();}catch(Exception failure){lastPostProcessError=failure.Message;CancelAutomaticPressureSchedulingCore();}}));
                        });
                    }
                    finally
                    {
                        // RunOnUiThread intentionally does nothing after Dispose.
                        // Release only this owner, never a subsequent action's state.
                        ReleaseZeroAction(owner);
                        owner.Cancellation.Dispose();
                    }
                    if (!acknowledged) success = false;
                }
            }
            return success;
        }

        ZeroActionOwner ClaimZeroAction(long sequence)
        {
            long pending; string code, json;
            if (!ReadPendingAction(out pending, out code, out json) || pending != sequence || code != "PRESSURE")
                return null;
            lock (zeroHardwareLock)
            {
                // A sequence is single-use even when initial validation fails or
                // an external host closes its mailbox before acknowledgement.
                if (zeroActionOwner != null || sequence <= zeroLastClaimedActionSequence) return null;
                var owner = new ZeroActionOwner { Sequence = sequence, Cancellation = new CancellationTokenSource() };
                zeroLastClaimedActionSequence = sequence; zeroActionOwner = owner;
                zeroHardwareCancellation = owner.Cancellation; zeroHardwareRunning = true;
                return owner;
            }
        }
        bool ReleaseZeroAction(ZeroActionOwner owner)
        {
            lock (zeroHardwareLock)
            {
                if (Object.ReferenceEquals(zeroActionOwner, owner))
                { zeroActionOwner = null; zeroHardwareCancellation = null; zeroHardwareRunning = false; Interlocked.Exchange(ref automaticCapturePreparation,0); return true; }
                return false;
            }
        }

        ZeroCaptureContext BeginZeroCapture(ZeroActionOwner owner)
        {
            owner.Cancellation.Token.ThrowIfCancellationRequested();
            if (SimulationMode) throw new InvalidOperationException("The real input backend must not consume a simulation action.");
            if (requestedTargetPeakVoltageKv != 0 && pressureHardwareCapture==null) throw new InvalidOperationException("加压控制尚未配置。");
            if (String.IsNullOrEmpty(cycleAcquisitionId) || !cycleIdentityBound || !Directory.Exists(ProjectPath))
                throw new InvalidOperationException("A current project-bound acquisition identity is required.");
            if (!IsDischargeCalibrated || calibrationSimulation) throw new InvalidOperationException("A valid real calibration for the selected phase is required.");
            lock (zeroHardwareLock)
            {
                if (!Object.ReferenceEquals(zeroActionOwner, owner)) throw new InvalidOperationException("The action is not owned by this capture request.");
                if (zeroHardwareCapture == null) throw new InvalidOperationException("No verified zero-voltage hardware backend is configured.");
                var settings = PressureZeroVoltageCapture.ReadSettings(zeroHardwareSettings);
                lock(stateLock)if(pressureBatchCaptureEnabled&&requestedTargetPeakVoltageKv>0&&planMode&&!advancedMode)
                    settings.RunNumber=pressureRequestBatchRound;
                settings.RangeIndex = SelectedPicoRangeCode; settings.PdCalibrationPcPerV = calibrationPcPerVolt;
                settings.TargetVoltageV = 0; settings.AutoRange = automaticRange; settings.Validate();
                    var value = new ZeroCaptureContext { Settings = settings, Capture = zeroHardwareCapture, PressureCapture=pressureHardwareCapture, RangeReview=pressureRangeReview, TargetKv=requestedTargetPeakVoltageKv, PhaseVoltageRms=baseU0VoltageKv, Generation = zeroHardwareGeneration,
                    Sequence = owner.Sequence, Project = ProjectPath, Phase = SelectedPhaseCode, AcquisitionId = cycleAcquisitionId,
                    CalibrationSource = calibrationSource, CalibrationUtc = calibrationAcquiredUtc, CalibrationScale = calibrationPcPerVolt,
                    RangeIndex = settings.RangeIndex, AutomaticRange = automaticRange,
                    AnalyzeZero=cycleZeroAnalysisRequested,
                    Cancellation = owner.Cancellation };
                zeroContextTimer = new System.Windows.Forms.Timer { Interval = 200 };
                zeroContextTimer.Tick += delegate { if (!ZeroCaptureCurrent(value, true)) RequestHardwareStop(); };
                zeroContextTimer.Start();
                return value;
            }
        }

        bool ZeroCaptureCurrent(ZeroCaptureContext context, bool checkCancellation)
        {
            if (IsDisposed || Disposing || Volatile.Read(ref disposeStarted) != 0 || checkCancellation && context.Cancellation.IsCancellationRequested) return false;
            lock (zeroHardwareLock) if (context.Generation != zeroHardwareGeneration || !Object.ReferenceEquals(zeroHardwareCancellation, context.Cancellation)) return false;
            long sequence; string code, json;
            return ReadPendingAction(out sequence, out code, out json) && sequence == context.Sequence && code == "PRESSURE" &&
                !SimulationMode && String.Equals(ProjectPath, context.Project, StringComparison.OrdinalIgnoreCase) && SelectedPhaseCode == context.Phase &&
                cycleAcquisitionId == context.AcquisitionId && requestedTargetPeakVoltageKv == context.TargetKv && CalculateTargetPeakVoltageKvNoLock() == context.TargetKv &&
                SelectedPicoRangeCode == context.RangeIndex && automaticRange == context.AutomaticRange &&
                IsDischargeCalibrated && !calibrationSimulation && calibrationPcPerVolt == context.CalibrationScale &&
                calibrationSource == context.CalibrationSource && calibrationAcquiredUtc == context.CalibrationUtc;
        }
        void RequireZeroCaptureCurrent(ZeroCaptureContext value)
        { if (!ZeroCaptureCurrent(value, true)) throw new OperationCanceledException("The acquisition was canceled or its project, phase, calibration, or target changed."); }
        void EnsureZeroCaptureCurrent(ZeroCaptureContext value)
        {
            value.Cancellation.Token.ThrowIfCancellationRequested();
            bool current = false; RunOnUiThread(delegate { current = ZeroCaptureCurrent(value, true); });
            if (!current) throw new OperationCanceledException("The acquisition context is no longer current; raw worker files are preserved.");
        }
        bool ConfirmZeroTrigger(ZeroCaptureContext value, bool automatic)
        {
            // The integrated pressure backend checks the current start-button
            // intent. Do not ask again after that explicit operator action.
            if(value.PressureCapture!=null)return true;
            if (!value.Settings.AutomaticSharedTrigger) return true;
            bool result = false;
            RunOnUiThread(delegate {
                RequireZeroCaptureCurrent(value);
                if (ZeroTriggerConfirmationForTests != null) { result = ZeroTriggerConfirmationForTests(automatic); return; }
                string text = PressureUiText("保持零电压设置。请确认现场已准备就绪，并允许本轮同步采集。", "Keep the zero-voltage setting. Confirm that the site is ready and allow synchronized acquisition for this round.");
                result = MessageBox.Show(FindForm(), text, PressureUiText("确认同步采集", "Confirm synchronized acquisition"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            });
            return result;
        }
        PressureRangeChoice GetZeroRangeChoice(RangeDecisionContext context)
        {
            if (ZeroRangeDecisionForTests != null) return ZeroRangeDecisionForTests(context);
            var owner = FindForm();
            using (var dialog = new PressureRangeDecisionDialog(context, owner == null ? null : owner.Icon))
            {
                zeroRangeDialog = dialog;
                try { dialog.ShowDialog(owner); return new PressureRangeChoice { Action = dialog.Decision, Suppress = dialog.SuppressFurtherRangePrompts }; }
                finally { zeroRangeDialog = null; }
            }
        }
        IDictionary<string, object> ZeroCaptureMetadata(ZeroCaptureContext value, PressureZeroVoltageResult result)
        {
            var capture = result.Capture;
            return new Dictionary<string, object> {
                {"schema","HVX.NoiseReference/1"},{"projectPath",value.Project},{"phaseCode",value.Phase},{"sampleUnit","pC"},
                {"sourceId",result.Directory},{"calibrationId",value.CalibrationSource},{"deviceId",capture.PicoSerial},{"channelId","Pico:A"},
                {"sampleRateHz",capture.PicoRateHz},{"fullScaleV",capture.PicoFullScaleV},{"targetVoltageKv",value.TargetKv},
                {"chargeScalePcPerVolt",value.CalibrationScale},{"simulation",false},{"resolutionBits",capture.HardwareBits},
                {"rangeCode",capture.Settings.RangeIndex},{"capturedUtc",new DateTimeOffset(capture.CapturedUtc)},
                {"locationAcquisitionId",value.AcquisitionId},{"voltageEvidenceVerified",value.TargetKv>0},{"analyzeZeroVoltageData",value.AnalyzeZero},
                {"displayPreparationMilliseconds",result.DisplayPreparationMilliseconds},{"rangeReviewMilliseconds",result.RangeReviewMilliseconds},
                {"postReadPreparationMilliseconds",result.PostReadPreparationMilliseconds}
            };
        }
        void InstallZeroCapture(ZeroCaptureContext context, PressureZeroVoltageResult result, IDictionary<string, object> metadata,
            ZeroVoltageNoiseReference noise, PressureWaveformView.PreparedData display)
        {
            ValidateLocationAcquisitionIdentity(metadata);
            currentZeroVoltageAnalysis=context.TargetKv==0&&context.AnalyzeZero;
            zeroAnalysisSource=currentZeroVoltageAnalysis?result.Directory:"";
            currentAcquisitionReferenceOnly = context.TargetKv==0&&!currentZeroVoltageAnalysis; currentAcquisitionReferencePreview = false;
            if(context.TargetKv==0)spectrumDashboardView.ClearData(); spectrumDashboardView.SetReferencePreviewMessage(String.Empty);
            currentNoiseReference = noise; noiseReferenceState = noise == null ? "Pending" : noise.State;
            if (noise != null) noiseReferences.Add(noise);
            lock (stateLock)
            {
                selectedRangeCode = RangeCodes[result.Capture.Settings.RangeIndex];
                lastVoltageKv = result.VoltageKv; lastDischargePc = result.ChargePc; lastSampleRateHz = result.Capture.ArtRateHz;
                hasAcquisitionResult = true;
                // The driver and full-segment flags remain immutable evidence.
                // For a verified N-cycle evaluation they cannot reverse the
                // window-only recommendation and cause perpetual down/up retries.
                bool outsideOnly=context.TargetKv>0&&PressureOverflowUpscale.ProvenOutsideCycleClipping(result.Capture,result.Quality);
                lastAcquisitionOverRange = result.Quality.LocationClippedSamples > 0 ||
                    (result.Quality.FactoryCycleWindow?result.Quality.LocationPeakFraction>=1:result.Quality.LocationHighOccupancy) ||
                    result.Quality.DriverOverflow && !result.Quality.SwitchingTransientExcluded && !outsideOnly || result.Quality.MaximumRangeInsufficient ||
                    PressureChargeRangeFloor.MustHoldAtMaximum(result.Quality,result.Capture.Settings.RangeIndex);
                overRangeIgnored = false; dataSaved = context.TargetKv==0&&!currentZeroVoltageAnalysis; dataAnalyzed = false; deleteEnabled = false;
            }
            if(context.TargetKv>0||currentZeroVoltageAnalysis){lastPressureCapture=result;lastPressureAcquisitionId=context.AcquisitionId;}
            lock (zeroHardwareLock) lastZeroCaptureDirectory = result.Directory;
            // Independent hardware time bases: no resampling or common-rate fiction.
            waveformView.InstallPreparedData(display);
            RefreshNoiseReferenceView();
            if(currentZeroVoltageAnalysis)ApplyZeroVoltageAnalysisMetrics(result.PeakPc);else if(context.TargetKv==0)ApplyNoiseReferenceMetrics(result.PeakPc);else ApplyPressureCaptureMetrics(result);
            EvaluateLocationCycleWindow(); UpdateWaveformWindowUi(); UpdateRangeUi();
        }

        void ApplyPressureCaptureMetrics(PressureZeroVoltageResult capture)
        {
            // Voltage metrics use ART's time base. PD has an independent native
            // rate and is already DC-corrected; never assign ART time to Pico.
            var metrics=capture.VoltageMetrics;
            ApplyRealtimeMetrics(capture.MeasurementPeakPc,metrics.EquivalentCapacitanceUf,metrics.OscillationFrequencyHz,metrics.DissipationFactorPercent);
            lock(stateLock)analysisStatus="INDEPENDENT_TIMEBASES";
        }

        sealed class ZeroCaptureContext
        {
            internal double TargetKv,PhaseVoltageRms;
            internal Func<string,double,CancellationToken,Task<string>> PressureCapture;
            internal Func<string,string,double,string> RangeReview;
            internal BenchSettings Settings; internal Func<string, CancellationToken, Task<string>> Capture;
            internal CancellationTokenSource Cancellation; internal long Generation, Sequence;
            internal string Project, Phase, AcquisitionId, CalibrationSource;
            internal DateTimeOffset CalibrationUtc; internal double CalibrationScale;
            internal int RangeIndex; internal bool AutomaticRange;
            internal bool AnalyzeZero;
        }
        sealed class ZeroActionOwner
        {
            internal int RetryPermitRange=-1;
            internal long Sequence;
            internal CancellationTokenSource Cancellation;
            internal string FailureOutcomeStatus;
            internal bool DispatchCommitted, OperatorCanceledBeforeDispatch, OperatorStopRequested, NativeNeverEnteredConfirmed, RequestRejectedBeforeNative, InputStopConfirmed, RoundStopConfirmed, EmergencyRoundStopConfirmed;
        }
    }
    internal sealed class PressureRangeChoice
    {
        internal RangeDecisionAction Action; internal bool Suppress, Automatic;
    }
}
