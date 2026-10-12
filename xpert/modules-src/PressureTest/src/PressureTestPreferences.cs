using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        double frequencyReminderMinHz=20, frequencyReminderMaxHz=500, frequencyDangerThresholdHz=850;
        double capacitanceNormalMinUf=.02, capacitanceNormalMaxUf=8;
        double compensationCapacitanceUf=.2;
        bool useCompensationCapacitance;
        bool frequencyReminderEnabled=true, automaticPressureCountdownEnabled=true;
        int automaticPressureCountdownSeconds=1;
        bool metricFrequencyAvailable, metricCapacitanceAvailable, metricTanDeltaAvailable;
        double rawMeasuredCapacitanceUf=Double.NaN;
        double rawMeasuredTanDeltaPercent=Double.NaN;
        string lastFrequencyReminderState="";
        PressureReminderDialog frequencyReminderDialog, pressureCountdownDialog;
        System.Windows.Forms.Timer pressureCountdownTimer;
        TaskCompletionSource<bool> pressureCountdownCompletion;
        Stopwatch pressureCountdownClock;
        CancellationToken pressureCountdownCancellation;
        string pressureCountdownContext;
        bool pressureCountdownBeforeCapture;
        int activePressureCountdownSeconds;
        long countdownCompletedPressureSequence=-1;
        string countdownCompletedContext="";
        internal string LastCountdownCancellationSource {get;private set;}
        PressureReminderDialog captureCooldownDialog;
        int captureCooldownRemainingMilliseconds;
        int automaticCapturePreparation;
        long preparedCaptureActionSequence;
        string preparedCaptureContext="";
        public event EventHandler PressurePreferencesChanged;
        void InitializePressurePreferences()
        {
            EventHandler cancel=delegate {CancelAutomaticPressureSchedulingCore();};
            PolarityChanged+=cancel;ProgramModeChanged+=cancel;VoltageSettingChanged+=cancel;
            AdvancedModeChanged+=cancel;PhaseChanged+=cancel;RangeModeChanged+=cancel;RangeChanged+=cancel;
        }

        public double FrequencyReminderMinHz { get{return frequencyReminderMinHz;} set{SetPositivePreference(value,()=>frequencyReminderMinHz=value);} }
        public double FrequencyReminderMaxHz { get{return frequencyReminderMaxHz;} set{SetPositivePreference(value,()=>frequencyReminderMaxHz=value);} }
        public double FrequencyDangerThresholdHz { get{return frequencyDangerThresholdHz;} set{SetPositivePreference(value,()=>frequencyDangerThresholdHz=value);} }
        public double CapacitanceNormalMinUf { get{return capacitanceNormalMinUf;} set{SetPositivePreference(value,()=>capacitanceNormalMinUf=value);} }
        public double CapacitanceNormalMaxUf { get{return capacitanceNormalMaxUf;} set{SetPositivePreference(value,()=>capacitanceNormalMaxUf=value);} }
        public bool UseCompensationCapacitance { get{return useCompensationCapacitance;} set{RunOnUiThread(()=>{if(PressureParametersLocked)return;useCompensationCapacitance=value;UpdateMetricsUi();});} }
        public double CompensationCapacitanceUf
        { get{return compensationCapacitanceUf;} set{if(!IsFinite(value)||value<0||value>1000000)throw new ArgumentOutOfRangeException("value");RunOnUiThread(()=>{if(PressureParametersLocked)return;compensationCapacitanceUf=value;UpdateMetricsUi();});} }
        [Browsable(false)] public double RawEquivalentCapacitanceUf {get{return rawMeasuredCapacitanceUf;}}
        [Browsable(false)] public double RawDissipationFactorPercent {get{return rawMeasuredTanDeltaPercent;}}
        [Browsable(false)] public double DisplayedEquivalentCapacitanceUf
        {get{return !metricCapacitanceAvailable||currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview?Double.NaN:rawMeasuredCapacitanceUf-(useCompensationCapacitance?compensationCapacitanceUf:0);}}
        [Browsable(false)] public bool IsDisplayedCapacitanceValid
        {get{double value=DisplayedEquivalentCapacitanceUf;return IsFinite(value)&&value>=0;}}
        public bool FrequencyReminderEnabled { get{return frequencyReminderEnabled;} set{RunOnUiThread(()=>{frequencyReminderEnabled=value;lastFrequencyReminderState="";if(!value)CloseFrequencyReminder();});} }
        public bool AutomaticPressureCountdownEnabled { get{return automaticPressureCountdownEnabled;} set{RunOnUiThread(()=>{automaticPressureCountdownEnabled=value;});} }
        public int AutomaticPressureCountdownSeconds
        { get{return automaticPressureCountdownSeconds;} set{if(value<0||value>10)throw new ArgumentOutOfRangeException("value");int seconds=Math.Max(1,value);RunOnUiThread(()=>{if(PressureParametersLocked)return;if(seconds!=automaticPressureCountdownSeconds)CancelAutomaticPressureSchedulingCore();automaticPressureCountdownSeconds=seconds;});} }
        [Browsable(false)] public bool AutomaticPressureCountdownPending {get{return pressureCountdownCompletion!=null;}}
        [Browsable(false)] public string FrequencyStatusCode {get{return FrequencyStatus();}}
        [Browsable(false)] public string CapacitanceStatusCode {get{return CapacitanceStatus();}}

        void SetPositivePreference(double value,Action setter)
        {if(!IsFinite(value)||value<=0||value>1000000)throw new ArgumentOutOfRangeException("value");RunOnUiThread(()=>{if(PressureParametersLocked)return;setter();lastFrequencyReminderState="";UpdateMetricsUi();});}
        string FrequencyStatus()
        {
            if(!metricFrequencyAvailable||currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview||
                frequencyReminderMinHz>=frequencyReminderMaxHz||frequencyReminderMaxHz>=frequencyDangerThresholdHz)return "Unknown";
            double value=OscillationFrequencyHz;
            return value>frequencyDangerThresholdHz?"Danger":value>frequencyReminderMaxHz?"High":value<frequencyReminderMinHz?"Low":"Normal";
        }
        string CapacitanceStatus()
        {
            if(!metricCapacitanceAvailable||currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview||capacitanceNormalMinUf>=capacitanceNormalMaxUf)return "Unknown";
            double value=DisplayedEquivalentCapacitanceUf;
            if(!IsFinite(value)||value<0)return "Invalid";
            return value<capacitanceNormalMinUf||value>capacitanceNormalMaxUf?"Warning":"Normal";
        }
        static Color MetricStatusColor(string status)
        {return status=="Unknown"?PressureTestTheme.SecondaryInk:status=="Normal"?PressureTestTheme.Success:status=="Danger"?PressureTestTheme.Danger:Color.FromArgb(190,106,0);}
        void UpdateMetricStatusVisuals()
        {
            if(frequencyValueLabel==null)return;
            // No approved cable/material/test-method criterion is configured.
            // A displayed tan-delta value is not a pass/fail assessment.
            if(tanDeltaValueLabel!=null)
            {
                string criteria=PressureUiText("未配置适用判据","No applicable criteria configured");
                bool available=metricTanDeltaAvailable&&!currentAcquisitionReferenceOnly&&!currentAcquisitionReferencePreview;
                string invalid=available&&rawMeasuredTanDeltaPercent<0?PressureUiText("无效估计值","Invalid estimate"):String.Empty;
                tanDeltaValueLabel.ForeColor=PressureTestTheme.SecondaryInk;
                SetPressureUiText(tanDeltaValueLabel,available?MetricText(rawMeasuredTanDeltaPercent,"0.00"):"—");
                tanDeltaValueLabel.AccessibleDescription=PressureUiText("有效振荡电压达到0.7 U0后计算显示。","Calculated when valid oscillation voltage reaches 0.7 U0.")+" "+criteria+(invalid.Length>0?" · "+invalid:"");
                var caption=tanDeltaValueLabel.Parent==null?null:tanDeltaValueLabel.Parent.Controls["MetricTanDeltaCaption"] as Label;
                if(caption!=null)
                {
                    string text=PressureUiText("介损 / %","tanδ / %")+
                        (invalid.Length>0?" · "+PressureUiText("无效","Invalid"):"")+"\r\n"+
                        PressureUiText("未配置适用判据","Criteria not configured");
                    if(caption.Text!=text)
                    {
                        caption.Text=text;
                        arrangeRealtimeSidebar?.Invoke();
                    }
                    caption.ForeColor=PressureTestTheme.SecondaryInk;
                }
            }
            string frequency=FrequencyStatus(),capacitance=CapacitanceStatus();
            frequencyValueLabel.ForeColor=MetricStatusColor(frequency);capacitanceValueLabel.ForeColor=MetricStatusColor(capacitance);
            frequencyValueLabel.AccessibleDescription=frequency+" · "+frequencyReminderMinHz.ToString("G",CultureInfo.InvariantCulture)+"–"+frequencyReminderMaxHz.ToString("G",CultureInfo.InvariantCulture)+" Hz";
            SetPressureUiText(frequencyValueLabel,frequency=="Unknown"?"—":MetricText(OscillationFrequencyHz,"0.00"));
            SetPressureUiText(capacitanceValueLabel,capacitance=="Unknown"?"—":capacitance=="Invalid"?PressureUiText("无效","Invalid"):MetricText(DisplayedEquivalentCapacitanceUf,"0.00"));
            capacitanceValueLabel.AccessibleDescription=capacitance+" · "+capacitanceNormalMinUf.ToString("G",CultureInfo.InvariantCulture)+"–"+capacitanceNormalMaxUf.ToString("G",CultureInfo.InvariantCulture)+" μF"+
                String.Format(CultureInfo.InvariantCulture,"; Raw={0:G} μF; CompensationEnabled={1}; Compensation={2:G} μF; Result={3:G} μF",RawEquivalentCapacitanceUf,useCompensationCapacitance,compensationCapacitanceUf,DisplayedEquivalentCapacitanceUf);
            UpdateCompensationCapacitanceUi();
        }
        // Only explicit user choices notify the host to persist. Host property
        // setters deliberately do not raise this event.
        public void SuppressFrequencyReminder()
        {RunOnUiThread(()=>{FrequencyReminderEnabled=false;Raise(PressurePreferencesChanged);});}
        public void SuppressAutomaticPressureCountdown()
        {RunOnUiThread(()=>{automaticPressureCountdownEnabled=false;Raise(PressurePreferencesChanged);});}
        public void SetUseCompensationCapacitance(bool value)
        {RunOnUiThread(()=>{if(useCompensationCapacitance==value)return;UseCompensationCapacitance=value;Raise(PressurePreferencesChanged);});}
        void MaybeShowFrequencyReminder()
        {
            string status=FrequencyStatus();
            if(status=="Normal"||status=="Unknown"){lastFrequencyReminderState="";CloseFrequencyReminder();return;}
            if(!frequencyReminderEnabled||!Visible||FindForm()==null||lastFrequencyReminderState==status)return;
            lastFrequencyReminderState=status;
            CloseFrequencyReminder();
            var dialog=new PressureReminderDialog(this,"PressureFrequencyReminder",
                PressureUiText("振荡频率提醒","Oscillation frequency warning"),
                String.Format(CultureInfo.InvariantCulture,PressureUiText("振荡频率 {0:0.###} Hz，超出正常范围 {1:0.###}–{2:0.###} Hz。\r\n请核对测试回路。此提示不改变设备安全联锁。","Frequency {0:0.###} Hz is outside the normal range {1:0.###}–{2:0.###} Hz.\r\nCheck the test circuit. Device safety interlocks remain active."),OscillationFrequencyHz,frequencyReminderMinHz,frequencyReminderMaxHz),false);
            frequencyReminderDialog=dialog;
            dialog.Confirmed+=delegate {if(dialog.DoNotRemind)SuppressFrequencyReminder();else CloseFrequencyReminder();};
            dialog.FormClosed+=delegate {if(ReferenceEquals(frequencyReminderDialog,dialog))frequencyReminderDialog=null;};
            dialog.Show(FindForm());
        }
        void CloseFrequencyReminder()
        {var dialog=frequencyReminderDialog;frequencyReminderDialog=null;if(dialog!=null){dialog.Close();dialog.Dispose();}}

        // One explicitly host-authorized request, never a high-voltage driver or
        // an automatic loop. AutomaticPressureEnabled still means auto-save.
        // The hardware host must validate each mailbox request and all interlocks.
        public Task<bool> RequestAutomaticNextPressureAsync()
        {return RequestAutomaticNextPressureAsync(CancellationToken.None);}
        public Task<bool> RequestAutomaticNextPressureAsync(CancellationToken cancellationToken)
        {return RequestPressureCountdownAsync(cancellationToken,false);}

        // The host calls this before confirmation/output. A countdown already
        // completed for this exact mailbox/context is reused, never duplicated.
        public Task<bool> RequestAutomaticCaptureCountdownAsync(CancellationToken cancellationToken)
        {return RequestPressureCountdownAsync(cancellationToken,true);}
        async Task<bool> RequestPressureCountdownAsync(CancellationToken cancellationToken,bool beforeCapture)
        {
            if(!await WaitForPendingP04ReadAsync(cancellationToken))return false;
            var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RunOnUiThread(delegate
            {
                CancelAutomaticPressureSchedulingCore();
                LastCountdownCancellationSource="";
                if(cancellationToken.IsCancellationRequested){LastCountdownCancellationSource="CancellationToken";completion.TrySetResult(false);return;}
                pressureCountdownBeforeCapture=beforeCapture;
                if(beforeCapture)
                {
                    // Preparation is owned by the capture, not by the optional
                    // automatic-mode window. Manual captures wait on the same
                    // cancellable cooldown and monitor-shutdown boundary.
                    lock(stateLock)
                    {
                        if(!CapturePreparationContextCurrentNoLock(cancellationToken,true))
                        {LastCountdownCancellationSource="ContextUnavailable";completion.TrySetResult(false);return;}
                        preparedCaptureActionSequence=ActionSequence;
                        preparedCaptureContext=CaptureAutomaticPressureContext();
                        Interlocked.Exchange(ref automaticCapturePreparation,1);
                    }
                    if(advancedMode){completion.TrySetResult(true);return;}
                }
                if(!CanProceedAutomaticCountdown(true)){LastCountdownCancellationSource="ContextUnavailable";completion.TrySetResult(false);return;}
                if(beforeCapture&&countdownCompletedPressureSequence==PressureRequestSequence&&countdownCompletedContext==CaptureAutomaticPressureContext())
                {completion.TrySetResult(true);return;}
                pressureCountdownCompletion=completion;pressureCountdownCancellation=cancellationToken;
                pressureCountdownContext=CaptureAutomaticPressureContext();pressureCountdownClock=Stopwatch.StartNew();
                // Legacy zero/hidden preferences cannot remove the required wait.
                activePressureCountdownSeconds=Math.Max(1,automaticPressureCountdownSeconds);
                UpdateActionBusyUiForRound();
                pressureCountdownTimer=new System.Windows.Forms.Timer{Interval=50};
                pressureCountdownTimer.Tick+=delegate{TickAutomaticPressureCountdown();};
                if(automaticPressureCountdownEnabled)
                {
                    var dialog=new PressureReminderDialog(this,"AutomaticPressureCountdown",PressureUiText("自动加压倒计时","Automatic pressure countdown"),"",true);
                    long cancellationSequence=ActionSequence;
                    pressureCountdownDialog=dialog;
                    dialog.FormClosed+=delegate
                    {
                        if(!ReferenceEquals(pressureCountdownDialog,dialog))return;
                        bool suppress=dialog.DoNotRemind;
                        bool current=!pressureCountdownCancellation.IsCancellationRequested&&CanProceedAutomaticCountdown()&&pressureCountdownContext==CaptureAutomaticPressureContext();
                        bool canceled=current&&ReportPressurePreparationCanceled(cancellationSequence);
                        CancelAutomaticPressureSchedulingCore(canceled?"OperatorCanceled":"ContextUnavailable");
                        if(suppress)SuppressAutomaticPressureCountdown();
                    };
                    UpdateCountdownText();dialog.Show(FindForm());
                }
                if(pressureCountdownTimer!=null)pressureCountdownTimer.Start();
            });
            if(IsDisposed||Volatile.Read(ref disposeStarted)!=0)completion.TrySetResult(false);
            return await completion.Task;
        }
        bool CapturePreparationContextCurrentNoLock(CancellationToken token,bool allowPendingWait=false)
        {
            if(token.IsCancellationRequested||Volatile.Read(ref disposeStarted)!=0||!IsHandleCreated||!Visible||FindForm()==null||
                finishCurrentRoundRequested||automaticPlanInterrupted||pressureEmergencyLatched||
                (!CanRequestVoltageNoLock(CalculateTargetPeakVoltageKvNoLock())&&!(allowPendingWait&&HasPendingP04ReadNoLock()))||
                !IsBusy||!ActionPending||ActionCode!="PRESSURE"||IsPressureRequestBlockedNoLock())return false;
            lock(zeroHardwareLock)return zeroHardwareRunning&&zeroHardwareCancellation!=null&&
                !zeroHardwareCancellation.IsCancellationRequested&&zeroHardwareCancellation.Token==token;
        }
        // The host consumes preparation only after all asynchronous waits and
        // final validation, immediately before handing this request to its
        // supervised worker. Returning true is not hardware release evidence.
        public bool TryBeginPreparedHardwareCapture(CancellationToken token)
        {
            bool accepted=false;
            RunOnUiThread(delegate
            {
                lock(stateLock)
                {
                    if(Volatile.Read(ref automaticCapturePreparation)==0||
                        preparedCaptureActionSequence!=ActionSequence||
                        preparedCaptureContext!=CaptureAutomaticPressureContext()||
                        !CapturePreparationContextCurrentNoLock(token))return;
                    Interlocked.Exchange(ref automaticCapturePreparation,0);
                    lock(zeroHardwareLock) if(zeroActionOwner!=null&&zeroActionOwner.Sequence==ActionSequence)
                        zeroActionOwner.DispatchCommitted=true;
                    accepted=true;
                }
            });
            return accepted;
        }
        bool CanProceedAutomaticCountdown(bool allowPendingWait=false)
        {
            if(!pressureCountdownBeforeCapture)return CanScheduleAutomaticPressure(allowPendingWait);
            lock(stateLock)return Volatile.Read(ref disposeStarted)==0&&IsHandleCreated&&Visible&&FindForm()!=null&&
                !advancedMode&&!finishCurrentRoundRequested&&!pressureEmergencyLatched&&
                !automaticPlanInterrupted&&(simulationMode||IsHighVoltagePermissionNoLock()||allowPendingWait&&HasPendingP04ReadNoLock())&&
                (CanRequestVoltageNoLock(CalculateTargetPeakVoltageKvNoLock())||allowPendingWait&&HasPendingP04ReadNoLock())&&
                IsBusy&&ActionPending&&ActionCode=="PRESSURE"&&HasActiveHardwareSession&&
                !IsPressureRequestBlockedNoLock();
        }
        bool CanScheduleAutomaticPressure(bool allowPendingWait=false)
        {
            lock(stateLock)
            {
                double target=CalculateTargetPeakVoltageKvNoLock();
                return !finishCurrentRoundRequested&&Volatile.Read(ref disposeStarted)==0&&IsHandleCreated&&Visible&&FindForm()!=null&&!IsBusy&&ProjectPath.Length>0&&
                    pressureStateCode=="COMPLETE"&&!IsPressureRequestBlockedNoLock()&&
                    (!currentAcquisitionReferenceOnly||!advancedMode&&postProcessStateCode=="COMPLETE"&&hasAcquisitionResult)&&!currentAcquisitionReferencePreview&&
                    IsFinite(target)&&target>=0&&(target==0||(maximumPeakVoltageKv>0&&target<=maximumPeakVoltageKv))&&
                    (simulationMode||IsHighVoltagePermissionNoLock()||allowPendingWait&&HasPendingP04ReadNoLock())&&
                    (CanRequestVoltageNoLock(target)||allowPendingWait&&HasPendingP04ReadNoLock());
            }
        }
        string CaptureAutomaticPressureContext()
        {
            // Spectrum and display-window changes do not change the authorized
            // physical pressure context and must not cancel this countdown.
            lock(stateLock) return ProjectPath+"|"+selectedPhaseCode+"|"+positivePolarity+"|"+planMode+"|"+
                selectedPlanStepIndex+"|"+currentPlanRepetition+"|"+advancedMode+"|"+automaticRange+"|"+
                selectedRangeCode+"|"+CalculateTargetPeakVoltageKvNoLock().ToString("R",CultureInfo.InvariantCulture)+"|"+
                calibrationPcPerVolt.ToString("R",CultureInfo.InvariantCulture)+"|"+calibrationSource+"|"+
                PressureRequestSequence.ToString(CultureInfo.InvariantCulture)+"|"+SimulationMode;
        }
        void TickAutomaticPressureCountdown()
        {
            if(pressureCountdownCompletion==null)return;
            // A bounded, same-reader wait pauses dispatch only. User Stop,
            // cancellation, changed context and real faults take the old path.
            lock(stateLock)if(!pressureCountdownCancellation.IsCancellationRequested&&HasPendingP04ReadNoLock()&&
                !finishCurrentRoundRequested&&!automaticPlanInterrupted&&!pressureEmergencyLatched&&
                pressureCountdownContext==CaptureAutomaticPressureContext()) {UpdateCountdownText();return;}
            if(pressureCountdownCancellation.IsCancellationRequested||!CanProceedAutomaticCountdown()||pressureCountdownContext!=CaptureAutomaticPressureContext())
            {CancelAutomaticPressureSchedulingCore();return;}
            UpdateCountdownText();
            if(pressureCountdownClock.Elapsed.TotalSeconds>=activePressureCountdownSeconds)FinishAutomaticPressureCountdown();
        }
        void UpdateCountdownText()
        {
            if(pressureCountdownDialog==null)return;
            int remaining=Math.Max(0,(int)Math.Ceiling(activePressureCountdownSeconds-pressureCountdownClock.Elapsed.TotalSeconds));
            pressureCountdownDialog.Message=String.Format(CultureInfo.InvariantCulture,PressureUiText("下一轮将在 {0} 秒后准备开始。\r\n取消可停止本轮；开始前仍需完成安全确认。","The next round will be ready to start in {0} seconds.\r\nCancel stops this round. Safety confirmation is still required before starting."),remaining);
        }
        void FinishAutomaticPressureCountdown()
        {
            var completion=pressureCountdownCompletion;if(completion==null)return;
            long before=PressureRequestSequence;
            bool allowed=!pressureCountdownCancellation.IsCancellationRequested&&CanProceedAutomaticCountdown()&&pressureCountdownContext==CaptureAutomaticPressureContext();
            bool suppress=pressureCountdownDialog!=null&&pressureCountdownDialog.DoNotRemind;
            pressureCountdownCompletion=null;
            try
            {
                DisposeCountdownVisuals();
                UpdateActionBusyUiForRound();
                if(suppress)SuppressAutomaticPressureCountdown();
                if(!allowed||pressureCountdownCancellation.IsCancellationRequested||!CanProceedAutomaticCountdown()||pressureCountdownContext!=CaptureAutomaticPressureContext())
                {LastCountdownCancellationSource="ContextUnavailable";completion.TrySetResult(false);return;}
                if(pressureCountdownBeforeCapture)
                {
                    countdownCompletedPressureSequence=PressureRequestSequence;
                    countdownCompletedContext=CaptureAutomaticPressureContext();completion.TrySetResult(true);return;
                }
                RequestPressure();
                bool issued=PressureRequestSequence!=before;
                if(issued){countdownCompletedPressureSequence=PressureRequestSequence;countdownCompletedContext=CaptureAutomaticPressureContext();}
                completion.TrySetResult(issued);
            }
            catch(Exception error)
            {
                bool published=PressureRequestSequence!=before;
                lastError=published?"Pressure request was published but a notification failed; do not retry automatically.":"Pressure countdown failed before publishing the request.";
                completion.TrySetException(new InvalidOperationException(lastError,error));
            }
        }
        public void CancelAutomaticPressureScheduling()
        {RunOnUiThread(()=>CancelAutomaticPressureSchedulingCore());}
        void CancelAutomaticPressureSchedulingCore([System.Runtime.CompilerServices.CallerMemberName]string caller="")
        {var completion=pressureCountdownCompletion;pressureCountdownCompletion=null;DisposeCountdownVisuals();if(completion!=null){LastCountdownCancellationSource=caller;completion.TrySetResult(false);UpdateActionBusyUiForRound();}}
        void DisposeCountdownVisuals()
        {
            var timer=pressureCountdownTimer;pressureCountdownTimer=null;if(timer!=null){timer.Stop();timer.Dispose();}
            var dialog=pressureCountdownDialog;pressureCountdownDialog=null;if(dialog!=null){dialog.Close();dialog.Dispose();}
            pressureCountdownClock=null;
        }
        [Browsable(false)] public int CaptureCooldownRemainingMilliseconds {get{return captureCooldownRemainingMilliseconds;}}
        public void ReportCaptureCooldown(int remainingMilliseconds)
        {
            if(remainingMilliseconds<0)throw new ArgumentOutOfRangeException("remainingMilliseconds");
            RunOnUiThread(delegate
            {
                // Zero closes the countdown only. Monitor shutdown and final
                // validation may still be pending; they remain cancellable.
                captureCooldownRemainingMilliseconds=remainingMilliseconds;
                bool active;
                lock(zeroHardwareLock)active=zeroHardwareRunning&&zeroHardwareCancellation!=null&&!zeroHardwareCancellation.IsCancellationRequested;
                if(remainingMilliseconds==0||!active||advancedMode||!Visible||FindForm()==null||IsDisposed||Disposing)
                {CloseCaptureCooldown();return;}
                if(!automaticPressureCountdownEnabled)return;
                if(captureCooldownDialog==null)
                {
                    var dialog=new PressureReminderDialog(this,"AcquisitionCooldown",PressureUiText("等待下一次采集","Waiting for the next acquisition"),"",true);
                    captureCooldownDialog=dialog;
                    dialog.FormClosed+=delegate {if(ReferenceEquals(captureCooldownDialog,dialog)){captureCooldownDialog=null;RequestHardwareStop();if(dialog.DoNotRemind)SuppressAutomaticPressureCountdown();}};
                    dialog.Show(FindForm());
                }
                captureCooldownDialog.Message=String.Format(CultureInfo.InvariantCulture,PressureUiText("采集间隔还剩 {0} 秒。\r\n取消可停止本轮。","Acquisition cooldown: {0} seconds remaining.\r\nCancel stops this round."),(int)Math.Ceiling(remainingMilliseconds/1000.0));
            });
        }
        void CloseCaptureCooldown()
        {captureCooldownRemainingMilliseconds=0;var dialog=captureCooldownDialog;captureCooldownDialog=null;if(dialog!=null){bool suppress=dialog.DoNotRemind;dialog.Close();dialog.Dispose();if(suppress)SuppressAutomaticPressureCountdown();}}
        // Only cancels the pre-output wait. This is not proof that hardware has
        // stopped; ownership remains with the worker until its normal cleanup.
        void CancelCapturePreparationForSafety()
        {if(Volatile.Read(ref automaticCapturePreparation)!=0)RequestHardwareStop();}
        protected override void OnVisibleChanged(EventArgs e)
        {base.OnVisibleChanged(e);if(!Visible){CancelPressureStartConfirmation();CancelAutomaticPressureSchedulingCore();CloseCaptureCooldown();CloseFrequencyReminder();}}
        protected override void OnParentChanged(EventArgs e)
        {CancelPressureStartConfirmation();CancelAutomaticPressureSchedulingCore();CloseCaptureCooldown();CloseFrequencyReminder();base.OnParentChanged(e);}
    }

    internal sealed class PressureReminderDialog:Form
    {
        readonly Label message;
        readonly CheckBox suppress;
        internal event EventHandler Confirmed;
        internal string Message{get{return message.Text;}set{if(message.Text!=value)message.Text=value;}}
        internal bool DoNotRemind{get{return suppress.Checked;}}
        internal PressureReminderDialog(PressureTestControl owner,string name,string title,string text,bool countdown)
        {
            Name=name;Text=title;Font=owner.Font;AutoScaleMode=AutoScaleMode.Font;
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=false;MaximizeBox=false;
            ClientSize=new Size(600,280);MinimumSize=new Size(450,230);BackColor=Color.White;
            HVX.Presentation.OemUiBranding.Apply(this);
            var shell=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));Controls.Add(shell);
            var viewport=new Panel{Name="PressureReminderViewport",Dock=DockStyle.Fill,AutoScroll=true,AutoScrollMargin=new Size(0,12),Margin=Padding.Empty};shell.Controls.Add(viewport,0,0);
            var table=new TableLayoutPanel{Dock=DockStyle.Top,Padding=new Padding(16),ColumnCount=1,RowCount=2,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));table.RowStyles.Add(new RowStyle(SizeType.AutoSize));table.RowStyles.Add(new RowStyle(SizeType.AutoSize));viewport.Controls.Add(table);
            message=new Label{Name="PressureReminderMessage",Text=text,AutoSize=true,ForeColor=PressureTestTheme.Ink,Anchor=AnchorStyles.Left|AnchorStyles.Right,Margin=Padding.Empty};
            suppress=new CheckBox{Name="DoNotRemind",Text=Xiongxin.OWTS.Localization.UiLanguage.Text(owner.LanguageIndex,"不再显示（可在设置→常规中恢复）","Do not show again (restore in Settings > General)"),AutoSize=false,Anchor=AnchorStyles.Left|AnchorStyles.Right,Margin=new Padding(0,8,0,8)};
            var button=new HVX.Presentation.R2RoundedButton{Name=countdown?"CancelCountdown":"ConfirmFrequencyReminder",AutoSize=true,MinimumSize=new Size(100,36),Text=Xiongxin.OWTS.Localization.UiLanguage.Text(owner.LanguageIndex,countdown?"取消本次调度":"确定",countdown?"Cancel schedule":"OK"),Anchor=AnchorStyles.Right};
            button.Click+=delegate{if(countdown)Close();else{var handler=Confirmed;if(handler!=null)handler(this,EventArgs.Empty);}};
            table.Controls.Add(message,0,0);table.Controls.Add(suppress,0,1);
            var footer=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,WrapContents=false,Padding=new Padding(12),Margin=Padding.Empty};footer.Controls.Add(button);shell.Controls.Add(footer,0,1);CancelButton=button;
            bool arranging=false;EventHandler fit=delegate
            {
                if(arranging)return;arranging=true;
                try
                {
                    int width=Math.Max(160,viewport.ClientSize.Width-table.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-2);
                    message.MaximumSize=new Size(width,0);
                    suppress.Height=Math.Max(suppress.Font.Height+8,TextRenderer.MeasureText(suppress.Text,suppress.Font,new Size(Math.Max(100,width-28),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+8);
                    button.MinimumSize=new Size(100,Math.Max(36,button.GetPreferredSize(Size.Empty).Height));
                }
                finally{arranging=false;}
            };
            viewport.SizeChanged+=fit;FontChanged+=fit;message.FontChanged+=fit;suppress.FontChanged+=fit;Shown+=fit;fit(this,EventArgs.Empty);
        }
    }
}
