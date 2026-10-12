using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        // Input-only bridge. It never opens ART/Pico, creates a hardware task,
        // starts Task38, or sends a power/output command. The acquisition owner
        // must supply the physical P0.4 bit, not transport readiness.
        const double P04MaximumAgeSeconds = 2;
        const double P04FutureToleranceSeconds = 0.25;
        System.Windows.Forms.Timer p04ExpiryTimer;
        // Timestamped P0.4 owns live permission from construction, not only
        // after the first report. Legacy untimed setters may only deny access.
        bool p04FeedbackManaged = true, p04SampleKnown, p04BitHigh;
        bool p04SchedulingInvalidated;
        long p04SchedulingRevision;
        bool p04UiRefreshQueued;
        int p04UiRefreshHandleGeneration;
        bool pressureEmergencyLatched;
        long p04Session, p04ReceivedTimestamp, p04SourceGeneration;
        DateTime p04SessionStartedUtc = DateTime.UtcNow;
        DateTime p04CapturedUtc, p04NewestCapturedUtc;
        double p04RemainingAgeSeconds;
        // Physical samples from a fault-latched backend are display-only.
        // They can never renew the ordinary HIGH output-permission cache.
        bool p04LatchedDisplayKnown, p04LatchedDisplayHigh;
        DateTime p04LatchedDisplayUtc;
        long p04LatchedDisplayReceived;
        double p04LatchedDisplayRemaining;
        // Waiting is issued only by the same healthy DI owner before its
        // original 500ms deadline. It removes electrical permission, not the
        // operator's current batch/start intent. No old HIGH is renewed here.
        bool p04ReadPending,p04PendingSchedulePreserved;
        DateTime p04PendingObservedUtc,p04PendingUntilUtc;
        long p04PendingReceived;
        double p04PendingRemainingSeconds;

        [Browsable(false)] public DateTime P04CapturedUtc
        { get { lock (stateLock) return p04SampleKnown ? p04CapturedUtc : DateTime.MinValue; } }

        /// <summary>
        /// Reports the physical P0.4 signal: 1 permits non-zero pressure; 0
        /// prohibits it. capturedUtc must be the UTC sampling time. Unknown,
        /// failed, old or future-dated input fails closed. This is not an SDK
        /// reader; the supervised hardware owner publishes timestamped reads.
        /// </summary>
        public void ReportP04Status(bool connected, bool bitHigh, DateTime capturedUtc)
        { ReportP04StatusCore(null,connected,bitHigh,capturedUtc); }

        // A producer lease changes at monitor/capture ownership boundaries.
        // Rotation neither renews nor erases a still-fresh physical sample.
        public long BeginP04FeedbackSource()
        { lock(stateLock)return ++p04SourceGeneration; }

        public void ReportP04StatusForSource(long source,bool connected,bool bitHigh,DateTime capturedUtc)
        { ReportP04StatusCore(source,connected,bitHigh,capturedUtc); }

        public void ReportPendingP04StatusForSource(long source,DateTime observedUtc,DateTime deadlineUtc)
        {
            lock(stateLock) {
                if(simulationMode||Volatile.Read(ref disposeStarted)!=0||source!=p04SourceGeneration||pressureEmergencyLatched)return;
                DateTime now=DateTime.UtcNow;
                if(observedUtc.Kind!=DateTimeKind.Utc||deadlineUtc.Kind!=DateTimeKind.Utc||observedUtc<p04SessionStartedUtc||
                    observedUtc<=p04NewestCapturedUtc||observedUtc<p04PendingObservedUtc||deadlineUtc<=now||deadlineUtc<=observedUtc||
                    (deadlineUtc-observedUtc).TotalMilliseconds>500||(observedUtc-now).TotalSeconds>P04FutureToleranceSeconds)return;
                // Duplicate publication never extends the absolute/monotonic
                // deadline, and pending cannot erase a real LOW/Stop barrier.
                if(p04ReadPending&&(observedUtc==p04PendingObservedUtc||deadlineUtc>p04PendingUntilUtc))return;
                if(p04SampleKnown&&!p04BitHigh||p04SchedulingInvalidated||automaticPlanInterrupted||finishCurrentRoundRequested)return;
                p04ReadPending=true;p04PendingSchedulePreserved=true;
                p04PendingObservedUtc=observedUtc;p04PendingUntilUtc=deadlineUtc;
                p04PendingReceived=Stopwatch.GetTimestamp();
                p04PendingRemainingSeconds=(deadlineUtc-now).TotalSeconds;
            }
            QueueP04FeedbackUiRefresh();
        }

        bool HasPendingP04ReadNoLock()
        {
            return p04ReadPending&&!pressureEmergencyLatched&&Volatile.Read(ref disposeStarted)==0&&
                DateTime.UtcNow<p04PendingUntilUtc&&
                (Stopwatch.GetTimestamp()-p04PendingReceived)/(double)Stopwatch.Frequency<p04PendingRemainingSeconds;
        }

        // Waiting preserves only this request/context. It neither starts a
        // device nor treats the previous HIGH as a new permission. The owner
        // still checks its stricter 100ms freshness at every actual output.
        public async Task<bool> WaitForPendingP04ReadAsync(CancellationToken token)
        {
            string context;long source;
            lock(stateLock){
                if(IsManualZeroInputNoLock(CalculateTargetPeakVoltageKvNoLock())&&requestedTargetPeakVoltageKv<=0&&!pressureEmergencyLatched)return true;
                if(!p04ReadPending)return true;context=CaptureAutomaticPressureContext();source=p04SourceGeneration;
            }
            while(true) {
                token.ThrowIfCancellationRequested();
                lock(stateLock) {
                    if(Volatile.Read(ref disposeStarted)!=0||source!=p04SourceGeneration||context!=CaptureAutomaticPressureContext()||
                        finishCurrentRoundRequested||automaticPlanInterrupted||pressureEmergencyLatched)return false;
                    if(!p04ReadPending)return HasCurrentP04FeedbackNoLock()&&p04BitHigh;
                    if(!HasPendingP04ReadNoLock())return false;
                }
                await Task.Delay(10,token);
            }
        }

        public void ReportLatchedP04StatusForSource(long source,bool bitHigh,DateTime capturedUtc)
        {
            lock(stateLock) {
                if(simulationMode||Volatile.Read(ref disposeStarted)!=0||source!=p04SourceGeneration)return;
                double age=(DateTime.UtcNow-capturedUtc).TotalSeconds;
                if(capturedUtc.Kind!=DateTimeKind.Utc||capturedUtc<p04SessionStartedUtc||
                    age>P04MaximumAgeSeconds||age<-P04FutureToleranceSeconds)return;
                if(capturedUtc<p04NewestCapturedUtc||p04LatchedDisplayKnown&&capturedUtc<p04LatchedDisplayUtc)return;
                if(p04LatchedDisplayKnown&&capturedUtc==p04LatchedDisplayUtc&&bitHigh)return;
                p04LatchedDisplayKnown=true;p04LatchedDisplayHigh=bitHigh;
                p04LatchedDisplayUtc=capturedUtc;p04LatchedDisplayReceived=Stopwatch.GetTimestamp();
                p04LatchedDisplayRemaining=P04MaximumAgeSeconds-Math.Max(0,age);
                p04SampleKnown=false;p04BitHigh=false;
                p04ReadPending=p04PendingSchedulePreserved=false;
                p04NewestCapturedUtc=capturedUtc;
                pressureEmergencyLatched=true;
                automaticPlanInterrupted=true;
                InvalidateP04SchedulingNoLock();
            }
            CancelCapturePreparationForSafety();QueueP04FeedbackUiRefresh();
        }

        bool HasCurrentLatchedP04DisplayNoLock()
        {
            if(!p04LatchedDisplayKnown||Volatile.Read(ref disposeStarted)!=0)return false;
            double wallAge=(DateTime.UtcNow-p04LatchedDisplayUtc).TotalSeconds;
            double elapsed=(Stopwatch.GetTimestamp()-p04LatchedDisplayReceived)/(double)Stopwatch.Frequency;
            return wallAge>=-P04FutureToleranceSeconds&&wallAge<=P04MaximumAgeSeconds&&
                elapsed>=0&&elapsed<=p04LatchedDisplayRemaining;
        }

        void ReportP04StatusCore(long? source,bool connected,bool bitHigh,DateTime capturedUtc)
        {
            bool continuityLost;
            bool cancelPreparation;
            // Apply the safety input before UI dispatch. A stalled UI must not
            // keep the previous true after a worker has reported a read failure
            // or a physical low. UI work below only renders the cached state.
            lock (stateLock)
            {
                if (simulationMode || Volatile.Read(ref disposeStarted) != 0) return;
                if(source.HasValue&&source.Value!=p04SourceGeneration)return;
                continuityLost = !HasPendingP04ReadNoLock()&&p04FeedbackManaged && highVoltageFeedbackKnown && !HasCurrentP04FeedbackNoLock();
                p04FeedbackManaged = true;
                DateTime now = DateTime.UtcNow;
                double age = (now - capturedUtc).TotalSeconds;
                if (!connected || p04ReadPending&&!HasPendingP04ReadNoLock() || capturedUtc.Kind != DateTimeKind.Utc ||
                    capturedUtc < p04SessionStartedUtc || age > P04MaximumAgeSeconds ||
                    age < -P04FutureToleranceSeconds)
                {
                    // The new barrier prevents a queued pre-failure read from
                    // restoring permission after a failed read.
                    ResetP04FeedbackStateNoLock(now);
                }
                else if(p04ReadPending&&bitHigh&&capturedUtc<=p04PendingObservedUtc)
                {
                    // A publication of the previous HIGH is not a new read.
                    // LOW follows the existing order/equal-time LOW-wins rule.
                    return;
                }
                else if (capturedUtc <= p04NewestCapturedUtc)
                {
                    // Replayed/out-of-order callbacks cannot extend the TTL
                    // or replace a newer low with an older high. At equal
                    // timestamp resolution the prohibiting low wins.
                    if (capturedUtc == p04NewestCapturedUtc && !bitHigh && p04SampleKnown) {
                        p04ReadPending=p04PendingSchedulePreserved=false;
                        p04BitHigh = false;
                    }
                    else return;
                }
                else
                {
                    p04ReadPending=false;
                    p04LatchedDisplayKnown=false;
                    p04SampleKnown = true;
                    p04BitHigh = bitHigh;
                    p04CapturedUtc = p04NewestCapturedUtc = capturedUtc;
                    p04ReceivedTimestamp = Stopwatch.GetTimestamp();
                    p04RemainingAgeSeconds = P04MaximumAgeSeconds - Math.Max(0, age);
                }
                bool nonzero=requestedTargetPeakVoltageKv>0 || CalculateTargetPeakVoltageKvNoLock()>0;
                bool manualZero=!nonzero&&IsManualZeroInputNoLock(CalculateTargetPeakVoltageKvNoLock());
                if(nonzero&&p04SampleKnown&&!p04BitHigh&&
                    (pressureStateCode=="RUNNING"||pressureStateCode=="RAMPING"||pressureStateCode=="HOLDING"))
                    pressureEmergencyLatched=true;
                // LOW permits only a manually requested input-only zero round;
                // it never authorizes automatic scheduling, even at 0 kV.
                if (continuityLost || !p04SampleKnown || !p04BitHigh) {
                    InvalidateP04SchedulingNoLock();
                    if(CurrentPressureRoundPendingNoLock()&&!manualZero)automaticPlanInterrupted=true;
                }
                cancelPreparation=!manualZero&&(continuityLost||!p04SampleKnown||!p04BitHigh);
            }
            if(cancelPreparation)CancelCapturePreparationForSafety();
            QueueP04FeedbackUiRefresh();
        }

        /// <summary>Call when the hardware owner/session changes or disconnects.</summary>
        public void ResetP04FeedbackSession()
        {
            // Rotate before dispatch, so an already queued old report cannot
            // become current while the UI is processing the session change.
            bool manualZero;
            lock (stateLock) { ++p04SourceGeneration;ResetP04FeedbackStateNoLock(DateTime.UtcNow);
                manualZero=IsManualZeroInputNoLock(CalculateTargetPeakVoltageKvNoLock())&&requestedTargetPeakVoltageKv<=0; }
            if(!manualZero)CancelCapturePreparationForSafety();
            QueueP04FeedbackUiRefresh();
        }

        // Safety state has already been committed under stateLock. This path
        // must not wait for UI dispatch: otherwise one slow repaint prevents
        // the producer from reading the next physical sample and renewing TTL.
        // The callback carries a handle identity only, never a cached HIGH.
        void QueueP04FeedbackUiRefresh()
        {
            if(Volatile.Read(ref disposeStarted)!=0||IsDisposed)return;
            if(Thread.CurrentThread.ManagedThreadId==ownerThreadId)
            {
                if(IsHandleCreated)RenderCurrentP04Feedback();
                return;
            }
            int generation;
            lock(stateLock)
            {
                if(p04UiRefreshQueued||!IsHandleCreated||Volatile.Read(ref disposeStarted)!=0)return;
                generation=Volatile.Read(ref handleGeneration);
                p04UiRefreshQueued=true;p04UiRefreshHandleGeneration=generation;
            }
            try
            {
                BeginInvoke((Action)(()=>{
                    lock(stateLock)
                    {
                        if(generation!=Volatile.Read(ref handleGeneration)||
                            generation!=p04UiRefreshHandleGeneration||!p04UiRefreshQueued)return;
                        p04UiRefreshQueued=false;
                    }
                    if(Volatile.Read(ref disposeStarted)==0&&!IsDisposed&&IsHandleCreated)RenderCurrentP04Feedback();
                }));
            }
            catch(InvalidOperationException error)
            {
                lock(stateLock)if(generation==p04UiRefreshHandleGeneration)p04UiRefreshQueued=false;
                if(Volatile.Read(ref disposeStarted)==0&&!IsDisposed)Trace.TraceError("P04 UI dispatch unavailable: "+error);
            }
        }

        void RenderCurrentP04Feedback()
        {
            try
            {
                bool hasSample;
                lock(stateLock)hasSample=(p04SampleKnown||p04LatchedDisplayKnown||p04ReadPending)&&!simulationMode;
                // A reset callback cannot erase a later current-source read.
                if(p04ExpiryTimer!=null){if(hasSample)p04ExpiryTimer.Start();else p04ExpiryTimer.Stop();}
                ApplyP04FeedbackToUi();
            }
            catch(Exception error)
            {
                // A failed UI cancellation must not silently restore a permit.
                lock(stateLock)ResetP04FeedbackStateNoLock(DateTime.UtcNow);
                Trace.TraceError("P04 UI refresh failed; feedback invalidated: "+error);
                CancelCapturePreparationForSafety();
            }
        }

        void RefreshP04FeedbackForCreatedHandle()
        {
            lock(stateLock){p04UiRefreshQueued=false;p04UiRefreshHandleGeneration=Volatile.Read(ref handleGeneration);}
            RenderCurrentP04Feedback();
        }

        void InvalidateP04FeedbackUiDispatch()
        {
            lock(stateLock){p04UiRefreshQueued=false;p04UiRefreshHandleGeneration=Volatile.Read(ref handleGeneration);}
            if(p04ExpiryTimer!=null)p04ExpiryTimer.Stop();
        }

        void InitializeP04Feedback()
        {
            p04ExpiryTimer = new System.Windows.Forms.Timer { Interval = 250 };
            p04ExpiryTimer.Tick += delegate { RenderCurrentP04Feedback(); };
        }

        void DisposeP04Feedback()
        {
            lock (stateLock) { ++p04SourceGeneration;ResetP04FeedbackStateNoLock(DateTime.UtcNow); }
            var timer = p04ExpiryTimer;
            p04ExpiryTimer = null;
            if (timer != null) { timer.Stop(); timer.Dispose(); }
        }

        void ResetP04FeedbackStateNoLock(DateTime now)
        {
            ++p04Session;
            p04SessionStartedUtc = now;
            p04SampleKnown = false;
            p04BitHigh = false;
            p04CapturedUtc = p04NewestCapturedUtc = DateTime.MinValue;
            p04ReceivedTimestamp = 0;
            p04RemainingAgeSeconds = 0;
            p04LatchedDisplayKnown=false;
            p04ReadPending=p04PendingSchedulePreserved=false;
            p04PendingObservedUtc=p04PendingUntilUtc=DateTime.MinValue;
            InvalidateP04SchedulingNoLock();
            // Ownership remains with timestamped P0.4 across reset/expiry.
        }

        void InvalidateP04SchedulingNoLock()
        {p04SchedulingInvalidated=true;++p04SchedulingRevision;}

        bool HasCurrentP04FeedbackNoLock()
        {
            if (p04ReadPending || !p04SampleKnown || simulationMode || Volatile.Read(ref disposeStarted) != 0) return false;
            double wallAge = (DateTime.UtcNow - p04CapturedUtc).TotalSeconds;
            double elapsed = (Stopwatch.GetTimestamp() - p04ReceivedTimestamp) / (double)Stopwatch.Frequency;
            return wallAge >= -P04FutureToleranceSeconds && wallAge <= P04MaximumAgeSeconds &&
                elapsed >= 0 && elapsed <= p04RemainingAgeSeconds;
        }

        bool IsHighVoltageFeedbackKnownNoLock()
        { return p04FeedbackManaged ? HasCurrentP04FeedbackNoLock() : highVoltageFeedbackKnown; }

        bool IsHighVoltagePermissionNoLock()
        { return !pressureEmergencyLatched && (p04FeedbackManaged ? HasCurrentP04FeedbackNoLock() && p04BitHigh && !p04SchedulingInvalidated : highVoltageFeedbackKnown && !highVoltageClosed); }

        void ApplyP04FeedbackToUi()
        {
            bool known, high, interrupted, manualZero,pending,preserveSchedule;
            long revision;
            lock (stateLock)
            {
                if (!p04FeedbackManaged) return;
                known = HasCurrentP04FeedbackNoLock();
                pending=HasPendingP04ReadNoLock();
                manualZero=IsManualZeroInputNoLock(CalculateTargetPeakVoltageKvNoLock())&&requestedTargetPeakVoltageKv<=0;
                if(!known&&!pending&&CurrentPressureRoundPendingNoLock()&&!manualZero)automaticPlanInterrupted=true;
                high = known && p04BitHigh;
                interrupted = p04SchedulingInvalidated;
                revision=p04SchedulingRevision;
                preserveSchedule=p04PendingSchedulePreserved&&!interrupted&&!pressureEmergencyLatched&&!automaticPlanInterrupted;
                if(!pending)p04PendingSchedulePreserved=false;
            }
            // A low/failure followed quickly by a high must still cancel the
            // old schedule, even if UI dispatch coalesces both into the high.
            if (interrupted) CancelAutomaticPressureSchedulingCore();
            if(!simulationMode&&!manualZero&&!pending&&(!known||CalculateTargetPeakVoltageKvNoLock()>0&&(interrupted||!high)))CancelCapturePreparationForSafety();
            if(interrupted)CancelIdlePressureDecisionForStop();
            // Do not clear a LOW/failure arriving while cancellation executes.
            // A HIGH queued before that later input cannot release its barrier.
            lock(stateLock)if(revision==p04SchedulingRevision)p04SchedulingInvalidated=false;
            ApplyHighVoltageStateCore(known, high,preserveSchedule&&(pending||known&&high));
            // Display-only samples can expire while ordinary permission stays
            // false. Refresh the independent physical display on every render,
            // not only when scheduling or output permission changed.
            UpdateHighVoltageStatusUi();
            if(interrupted)UpdatePostProcessUi();
        }
    }
}
