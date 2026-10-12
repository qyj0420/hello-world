using System;
using System.Collections.Generic;

namespace HVX.Task17.Location
{
    // Method 8 is an independent, native-time-domain evidence kernel. No legacy
    // localization engine, carrier frequency, attenuation law or amplitude-order
    // assumption is used here. Scores are morphology evidence, not probabilities.
    internal sealed class EighthPulse
    {
        public int Start, End, Peak, Onset, WidthSamples, ActiveSamples;
        public double Amplitude, AbsAmplitude, Noise, SampleRateHz, LocalBaseline;
        public double TimingUncertaintySeconds;
        public bool OnsetIdentifiable;
        public int OnsetEarliest, OnsetLatest;
        public double OnsetTimingUncertaintySeconds;
        public string OnsetReason = "not_measured";
        public int[] ObservedFrontStarts = new int[0];
        public double[] Samples = new double[0];
        internal EighthPacketSpectrum Spectrum;
        public bool Valid, EdgeClipped, FlatTop;
        public string Reason = "";
    }

    internal sealed class EighthCalibration
    {
        public EighthResourceReceipt ResourceUsage;
        public bool Valid, TemplateReliable;
        public string Reason = "";
        public EighthPulse Incident, Reflection;
        public double SignedGain, RoundTripSeconds, OnsetRoundTripSeconds;
        // Search-only coordinate shift; never a measured physical-loop period.
        public double RemovedLeadRoundTripSeconds;
        public double SampleRateHz, NoiseSigma, Baseline, Quality;
        public double TimingUncertaintySeconds, ResolutionSeconds;
        public double MeasurementTimingUncertaintySeconds, ModeDiscrepancySeconds;
        public bool OnsetClockIdentifiable;
        public string OnsetClockReason = "not_measured";
        public double OnsetRoundTripMinimumSeconds, OnsetRoundTripMaximumSeconds;
        public double OnsetMeasurementTimingUncertaintySeconds;
        public int RelativePolarity, ResolutionSamples;
        public double[] Template = new double[0];
        // Keep intermediate and later CAL packets as measured evidence. Their
        // existence does not by itself assign a joint/terminal propagation role.
        public List<EighthPulse> ObservedPackets = new List<EighthPulse>();
        public int RecordSampleCount;
    }

    internal sealed class EighthPair
    {
        public EighthPulse Incident, Reflection;
        public double T0, T1, DelaySamples, Correlation, Score, Gain;
        public double TimingUncertaintySeconds;
        public int RelativePolarity;
        public bool Accepted;
        public bool SourceIdentitySupported;
        public string SourceIdentityReason = "not_assessed";
        public bool FullRoundTripRoleAmbiguous;
        public bool ModeRoundTripRoleUnresolved;
        public bool FirstArrivalUnresolved;
        public string Reason = "";
        public string Role = "review";
        public int FamilyRootPeak = -1, EchoOrder;
    }

    internal sealed class EighthCandidateEvidence
    {
        public int Id, IncidentIndex, ReflectionIndex;
        public EighthPair Pair;
        public bool Passed, Unknown, OutputEligible, PropagationEligible;
        public double RawCorrelation, IncidentSnr, ReflectionSnr, TimingUncertaintySeconds;
        public string IncidentPacketReason = "", ReflectionPacketReason = "";
        public string Reason = "";
    }

    internal sealed class EighthAnalysis
    {
        public EighthResourceReceipt ResourceUsage;
        public bool Valid, LimitReached, BaselineStable;
        public string Reason = "";
        public double NoiseSigma, Baseline;
        public int LocalNoiseWindowSamples;
        public bool BaselineCorrectionUsed;
        public string BaselineCorrectionSummary = "global_robust_median";
        public double[] BackgroundSamples = new double[0];
        public int AmbiguousCount;
        public int CandidateCount, EchoCount, ReviewCount;
        public bool PropagationComplete = true;
        // Display coverage is not graph evaluation, source identity, or accuracy.
        // Counts are output-eligible packet rows after the final review policy.
        public bool GraphEvaluationComplete, PublicationLimited, PublicationComplete;
        public int PublicationEligibleCount, PublishedCount, PublicationOmittedCount;
        public string PropagationSummary = "{}";
        public EighthResolvedClocks Clocks;
        internal EighthCalibration IdentityCalibration;
        internal EighthResourceReceipt IdentityResourceUsage;
        public List<EighthCandidateEvidence> CandidateEvidence = new List<EighthCandidateEvidence>();
        public List<EighthPulse> Pulses = new List<EighthPulse>();
        public List<EighthPair> Pairs = new List<EighthPair>();
        public List<EighthPulse> Unpaired = new List<EighthPulse>();
        public List<EighthPulse> RecoveryReplacedLowSnr = new List<EighthPulse>();
        public List<EighthPulse> RecoveryUnverified = new List<EighthPulse>();
    }

    internal static class EighthPulseMath
    {
        internal const string ReleaseVersion = "3.0.22";
        internal const string FileVersion = ReleaseVersion + ".0";
        internal const string SourceVersion = ReleaseVersion + "-internal-source-identity-advisory";

        // Closed bounds use the caller's declared binary64 seconds. Multiplying
        // a measured d/Fs back by Fs can move an exact native endpoint by one ULP;
        // Ceil then incorrectly asks for an entire additional sample. Do not
        // repair that round-trip conversion with an epsilon or expanded window.
        // These predicates do not claim exact-rational cross-clock arithmetic.
        internal static bool SampleDelayFitsWindow(double samples, double fs, double windowSeconds)
        {
            if (!Finite(samples) || samples <= 0 || !Finite(fs) || fs <= 0 ||
                !Finite(windowSeconds) || windowSeconds <= 0) return false;
            double elapsed = samples / fs;
            return Finite(elapsed) && elapsed > 0 && elapsed <= windowSeconds;
        }

        internal static bool SampleWindowCovered(double availableSamples, double fs, double windowSeconds)
        {
            if (!Finite(availableSamples) || availableSamples < 0 || !Finite(fs) || fs <= 0 ||
                !Finite(windowSeconds) || windowSeconds <= 0) return false;
            double elapsed = availableSamples / fs;
            return Finite(elapsed) && elapsed >= windowSeconds;
        }

        private const int Quiet = 3;
        private const int MaximumRecordSamples = 8388608;
        private const int MaximumPublishedIncidents = 4096;
        private const int MaximumPacketSamples = 65536;
        private const int MaximumCandidates = 200000;

        private sealed class PacketSet
        {
            internal bool Valid, BaselineStable;
            internal string Reason = "";
            internal double Baseline, Noise;
            internal int LocalNoiseWindowSamples;
            internal bool BaselineCorrectionUsed;
            internal string BaselineCorrectionSummary = "global_robust_median";
            internal double[] BackgroundSamples = new double[0];
            internal List<EighthPulse> Pulses = new List<EighthPulse>();
            internal List<EighthPulse> RecoveryReplacedLowSnr = new List<EighthPulse>();
            internal List<EighthPulse> RecoveryUnverified = new List<EighthPulse>();
        }

        public static EighthCalibration Calibrate(double[] raw, double sampleRateHz,
            int incidentMarker, int reflectionMarker, double knownRoundTripSeconds)
        { return CalibrateWithBudget(raw,sampleRateHz,incidentMarker,reflectionMarker,knownRoundTripSeconds,new EighthKernelLimits()); }

        internal static EighthCalibration CalibrateWithBudget(double[] raw,double sampleRateHz,int incidentMarker,int reflectionMarker,
            double knownRoundTripSeconds,EighthKernelLimits limits)
        {
            using(var scope=EighthKernelResources.Begin(limits))
            {
                EighthCalibration result;
                try { result=CalibrateCore(raw,sampleRateHz,incidentMarker,reflectionMarker,knownRoundTripSeconds);if(result.Valid)ValidateCalibrationNumbers(result); }
                catch(EighthBudgetExceeded e){result=CalibrationFailure(new EighthCalibration{SampleRateHz=Finite(sampleRateHz)?sampleRateHz:0},e.Message);}
                if(!result.Valid&&scope.Meter.Receipt.FailureReason.Length==0)scope.Meter.Receipt.FailureReason=result.Reason;
                scope.Meter.Receipt.Complete=result.Valid;result.ResourceUsage=scope.Meter.Receipt;return result;
            }
        }
        private static EighthCalibration CalibrateCore(double[] raw, double sampleRateHz,
            int incidentMarker, int reflectionMarker, double knownRoundTripSeconds)
        {
            var r = new EighthCalibration { SampleRateHz = Finite(sampleRateHz) ? sampleRateHz : 0 };
            if (!Finite(knownRoundTripSeconds) || knownRoundTripSeconds <= 0)
                return CalibrationFailure(r, "invalid_known_round_trip");
            var s = Measure(raw, sampleRateHz, true);
            r.ObservedPackets = s.Pulses;
            r.RecordSampleCount = raw == null ? 0 : raw.Length;
            r.NoiseSigma = s.Noise; r.Baseline = s.Baseline;
            if (!s.Valid) return CalibrationFailure(r, s.Reason);
            if (incidentMarker < 0 || reflectionMarker <= incidentMarker || reflectionMarker >= raw.Length)
                return CalibrationFailure(r, "calibration_markers_not_ordered_or_outside_record");
            if (!s.BaselineStable) return CalibrationFailure(r, "calibration_baseline_unstable");
            r.Incident = FindMarker(s.Pulses, incidentMarker);
            r.Reflection = FindMarker(s.Pulses, reflectionMarker);
            if (r.Incident == null || r.Reflection == null)
                return CalibrationFailure(r, "calibration_marker_has_no_observed_support");
            if (Object.ReferenceEquals(r.Incident, r.Reflection) || r.Reflection.Start <= r.Incident.End)
                return CalibrationFailure(r, "calibration_overlap_unresolved");
            if (!r.Incident.Valid || !r.Reflection.Valid)
                return CalibrationFailure(r, "calibration_support_invalid:" + r.Incident.Reason + "/" + r.Reflection.Reason);
            if (r.Incident.AbsAmplitude < 6 * s.Noise || r.Reflection.AbsAmplitude < 6 * s.Noise)
                return CalibrationFailure(r, "calibration_signal_below_six_sigma");
            double measured = (r.Reflection.Peak - r.Incident.Peak) / sampleRateHz;
            if (Math.Abs(measured - knownRoundTripSeconds) > Math.Max(2 / sampleRateHz, .10 * knownRoundTripSeconds))
                return CalibrationFailure(r, "calibration_round_trip_inconsistent_with_geometry");
            double signedCorrelation = ShapeCorrelation(r.Incident, r.Reflection);
            r.Quality = Math.Abs(signedCorrelation);
            // Timing capability is separate from a template's transferability.
            // Independently observed CAL packets/markers/geometry still determine
            // the clock when the two shapes differ. Shape is only auxiliary.
            r.TemplateReliable = r.Quality >= .80;
            r.RelativePolarity = signedCorrelation < 0 ? -1 : 1;
            r.SignedGain = r.RelativePolarity * (r.Reflection.AbsAmplitude / r.Incident.AbsAmplitude);
            if (!Finite(r.SignedGain) || r.SignedGain == 0)
                return CalibrationFailure(r, "calibration_gain_unmeasurable");
            r.RoundTripSeconds = measured;
            r.OnsetRoundTripSeconds = (r.Reflection.Onset - r.Incident.Onset) / sampleRateHz;
            r.OnsetRoundTripMinimumSeconds = (r.Reflection.OnsetEarliest - r.Incident.OnsetLatest) / sampleRateHz;
            r.OnsetRoundTripMaximumSeconds = (r.Reflection.OnsetLatest - r.Incident.OnsetEarliest) / sampleRateHz;
            r.OnsetMeasurementTimingUncertaintySeconds = r.Incident.OnsetTimingUncertaintySeconds + r.Reflection.OnsetTimingUncertaintySeconds;
            r.OnsetClockIdentifiable = OnsetUsable(r.Incident) && OnsetUsable(r.Reflection) &&
                r.OnsetRoundTripMinimumSeconds > 0;
            r.OnsetClockReason = r.OnsetClockIdentifiable ? "independently_identifiable_CAL_onsets" :
                "CAL_onset_unresolved:" + r.Incident.OnsetReason + "/" + r.Reflection.OnsetReason;
            EighthKernelResources.Current.Bytes(32+8L*r.Incident.Samples.Length,"CAL_template_copy");
            EighthKernelResources.Work(r.Incident.Samples.Length,"CAL_template_copy");
            r.Template = (double[])r.Incident.Samples.Clone();
            // Peak, onset and explicit CAL marks are different observed clocks
            // when dispersion changes the two packet shapes. Their discrepancy
            // belongs in the clock uncertainty, not an arbitrary percent margin.
            double markerRt = (reflectionMarker - incidentMarker) / sampleRateHz;
            r.MeasurementTimingUncertaintySeconds = r.Incident.TimingUncertaintySeconds + r.Reflection.TimingUncertaintySeconds;
            r.ModeDiscrepancySeconds = Math.Max(r.OnsetClockIdentifiable ? Math.Abs(measured - r.OnsetRoundTripSeconds) : 0,
                Math.Abs(measured - markerRt));
            r.TimingUncertaintySeconds = r.MeasurementTimingUncertaintySeconds + r.ModeDiscrepancySeconds;
            r.ResolutionSamples = Math.Max(r.Incident.WidthSamples, r.Reflection.WidthSamples) + Quiet;
            r.ResolutionSeconds = r.ResolutionSamples / sampleRateHz;
            r.Valid = true;
            r.Reason = r.TemplateReliable ? "calibration_temporally_ordered_independent_support" :
                "calibration_timing_valid_template_shape_not_transferable";
            return r;
        }

        public static EighthAnalysis Analyze(double[] raw, double fs, double roundTripSeconds,
            double thresholdPc, bool firstArrival, EighthCalibration cal,
            int maximumIncidents = 100, double minimumCorrelation = .85,
            Func<int, bool> incidentAllowed = null, int suppressionOrderLimit = -1)
        {
            return AnalyzeWithBudget(raw,fs,roundTripSeconds,thresholdPc,firstArrival,cal,new EighthKernelLimits(),maximumIncidents,minimumCorrelation,incidentAllowed,suppressionOrderLimit);
        }

        public static EighthAnalysis AnalyzeWithClocks(double[] raw,double fs,double roundTripSeconds,
            double thresholdPc,bool firstArrival,EighthCalibration cal,EighthLoopClockContext clocks,
            int maximumIncidents=100,double minimumCorrelation=.85,Func<int,bool> incidentAllowed=null,int suppressionOrderLimit=-1)
        {
            return AnalyzeWithClockBudget(raw,fs,roundTripSeconds,thresholdPc,firstArrival,cal,clocks,
                new EighthKernelLimits(),maximumIncidents,minimumCorrelation,incidentAllowed,suppressionOrderLimit);
        }

        internal static EighthAnalysis AnalyzeWithBudget(double[] raw,double fs,double roundTripSeconds,double thresholdPc,bool firstArrival,
            EighthCalibration cal,EighthKernelLimits limits,int maximumIncidents=100,double minimumCorrelation=.85,
            Func<int,bool> incidentAllowed=null,int suppressionOrderLimit=-1)
        {
            return AnalyzeWithClockBudget(raw,fs,roundTripSeconds,thresholdPc,firstArrival,cal,EighthResolvedClocks.Legacy(cal),
                limits,maximumIncidents,minimumCorrelation,incidentAllowed,suppressionOrderLimit);
        }

        internal static EighthAnalysis AnalyzeWithClockBudget(double[] raw,double fs,double roundTripSeconds,double thresholdPc,bool firstArrival,
            EighthCalibration cal,EighthLoopClockContext clocks,EighthKernelLimits limits,int maximumIncidents=100,double minimumCorrelation=.85,
            Func<int,bool> incidentAllowed=null,int suppressionOrderLimit=-1)
        {
            using(var scope=EighthKernelResources.Begin(limits))
            {
                EighthAnalysis result;
                try { result=AnalyzeCore(raw,fs,roundTripSeconds,thresholdPc,firstArrival,cal,clocks,maximumIncidents,minimumCorrelation,incidentAllowed,suppressionOrderLimit);if(result.Valid)ValidateAnalysisNumbers(result); }
                catch(EighthBudgetExceeded e){result=AnalysisFailure(new EighthAnalysis{LimitReached=e.Message.IndexOf("budget",StringComparison.Ordinal)>=0},e.Message);}
                if(!result.Valid&&scope.Meter.Receipt.FailureReason.Length==0)scope.Meter.Receipt.FailureReason=result.Reason;
                scope.Meter.Receipt.Complete=result.Valid&&result.PropagationComplete;result.ResourceUsage=scope.Meter.Receipt;return result;
            }
        }
        private static EighthAnalysis AnalyzeCore(double[] raw, double fs, double roundTripSeconds,
            double thresholdPc, bool firstArrival, EighthCalibration cal,EighthLoopClockContext clockContext,
            int maximumIncidents, double minimumCorrelation,
            Func<int, bool> incidentAllowed, int suppressionOrderLimit)
        {
            var r = new EighthAnalysis();
            r.IdentityCalibration=cal;
            r.Clocks=EighthResolvedClocks.Resolve(cal,clockContext,roundTripSeconds,firstArrival,fs);
            if (!Finite(roundTripSeconds) || roundTripSeconds <= 0 || !Finite(thresholdPc) || thresholdPc < 0 ||
                !Finite(minimumCorrelation) || minimumCorrelation < 0 || minimumCorrelation > 1 ||
                maximumIncidents < 1 || maximumIncidents > MaximumPublishedIncidents || suppressionOrderLimit < -1)
                return AnalysisFailure(r, "invalid_analysis_parameters");
            double observedSupport = cal != null && Finite(cal.ResolutionSeconds) && cal.ResolutionSeconds > 0 ? cal.ResolutionSeconds : 0;
            var s = Measure(raw, fs, false, observedSupport);
            if (s.Valid && CalibrationUsable(cal) && r.Clocks.PhysicalLoopKnown &&
                !RecoverAffineGuardPackets(raw, fs, observedSupport, s, cal,r.Clocks))
                EighthKernelResources.Fail("experimental_recovery_budget_incomplete_no_prefix");
            r.Baseline = s.Baseline; r.NoiseSigma = s.Noise; r.BaselineStable = s.BaselineStable;
            r.LocalNoiseWindowSamples = s.LocalNoiseWindowSamples;
            r.BaselineCorrectionUsed=s.BaselineCorrectionUsed;r.BaselineCorrectionSummary=s.BaselineCorrectionSummary;
            r.BackgroundSamples=s.BackgroundSamples;
            r.Pulses = s.Pulses;
            r.RecoveryReplacedLowSnr = s.RecoveryReplacedLowSnr;
            r.RecoveryUnverified = s.RecoveryUnverified;
            if (!s.Valid) return AnalysisFailure(r, s.Reason);
            r.Valid = true;
            int n = s.Pulses.Count;
            EighthKernelResources.Current.Bytes(32+17L*n,"eligibility_and_template_scores");
            EighthKernelResources.Work(3L*n,"packet_eligibility_and_template_dispatch");
            var eligible = new bool[n];
            for (int i = 0; i < n; i++)
            {
                EighthPulse p = s.Pulses[i];
                // Output threshold is inclusive, but a zero-amplitude point is never an event.
                eligible[i] = p.AbsAmplitude > 0 && p.AbsAmplitude >= thresholdPc &&
                    (incidentAllowed == null || incidentAllowed(p.Peak));
            }
            bool usableCal = CalibrationUsable(cal);
            if (!usableCal || !s.BaselineStable)
            {
                r.PropagationComplete = false;
                r.Reason = !usableCal ? "missing_or_invalid_calibration_no_automatic_acceptance" : "baseline_unstable_no_automatic_acceptance";
                AppendUnpaired(r, s.Pulses, eligible, maximumIncidents);
                return r;
            }
            // A supplied round trip must agree with the same timing mode in CAL.
            bool onsetClockUsable = OnsetCalibrationUsable(cal);
            double referenceRt = firstArrival && onsetClockUsable ? cal.OnsetRoundTripSeconds : cal.RoundTripSeconds;
            if (Math.Abs(roundTripSeconds - referenceRt) > Math.Max(2 / fs + 2 / cal.SampleRateHz, .10 * referenceRt))
            {
                r.PropagationComplete = false;
                r.Reason = "analysis_round_trip_inconsistent_with_calibration";
                AppendUnpaired(r, s.Pulses, eligible, maximumIncidents);
                return r;
            }
            var incidentShape = new double[n];
            var reflectionShape = new double[n];
            for (int i = 0; i < n; i++)
            {
                incidentShape[i] = Math.Abs(ShapeCorrelation(s.Pulses[i], cal.Incident));
                reflectionShape[i] = Math.Abs(ShapeCorrelation(s.Pulses[i], cal.Reflection));
            }
            int candidates = 0;
            double maxDelay = roundTripSeconds * fs;
            if (!Finite(maxDelay) || !SampleDelayFitsWindow(1, fs, roundTripSeconds))
                return AnalysisFailure(r, "round_trip_below_native_sampling_resolution");
            for (int i = 0; i < n; i++)
            {
                EighthPulse a = s.Pulses[i];
                // Output eligibility NEVER restricts evidence-only echo seeds.
                // Every observed six-sigma packet may verify a predicted family.
                if (!a.Valid || a.AbsAmplitude < 6 * a.Noise) continue;
                double t0 = firstArrival ? a.Onset : a.Peak;
                for (int j = i + 1; j < n; j++)
                {
                    EighthPulse b = s.Pulses[j];
                    double t1 = firstArrival ? b.Onset : b.Peak;
                    double delay = t1 - t0;
                    // Invalid/nonpositive anchors still reach the existing
                    // evidence checks; they must not stop later candidates.
                    if (delay > 0 && !SampleDelayFitsWindow(delay, fs, roundTripSeconds)) break;
                    EighthKernelResources.Current.Candidate();candidates++;
                    var evidence = new EighthCandidateEvidence { Id = r.CandidateEvidence.Count,
                        IncidentIndex = i, ReflectionIndex = j, OutputEligible = eligible[i],
                        IncidentSnr = a.AbsAmplitude / a.Noise, ReflectionSnr = b.AbsAmplitude / b.Noise,
                        TimingUncertaintySeconds = firstArrival ? a.OnsetTimingUncertaintySeconds + b.OnsetTimingUncertaintySeconds :
                            a.TimingUncertaintySeconds + b.TimingUncertaintySeconds,
                        IncidentPacketReason = a.Reason, ReflectionPacketReason = b.Reason };
                    r.CandidateEvidence.Add(evidence);
                    double signed = ShapeCorrelation(a, b);
                    double corr = Math.Abs(signed);
                    evidence.RawCorrelation = corr;
                    if (!b.Valid || b.Start <= a.End || b.AbsAmplitude < 6 * b.Noise || delay <= 0)
                    {
                        evidence.Unknown = b.AbsAmplitude >= 6 * b.Noise &&
                            (!b.Valid || b.Start <= a.End);
                        evidence.Reason = "packet_support_unmeasurable_or_not_independent:" + b.Reason;
                        continue;
                    }
                    bool waveformPassed = corr >= minimumCorrelation;
                    int polarity = signed < 0 ? -1 : 1;
                    double template = Math.Min(incidentShape[i], reflectionShape[j]);
                    double score = corr * (cal.TemplateReliable ? .95 + .05 * template : 1);
                    double gain = polarity * (b.AbsAmplitude / a.AbsAmplitude);
                    if (!Finite(gain)) { evidence.Unknown = true; evidence.Reason = "finite_amplitudes_but_gain_overflow_unresolved"; continue; }
                    double measurement = r.Clocks.RoleMeasurementUncertaintySeconds;
                    double roleLoop=r.Clocks.SelectedRoleLoopSeconds*fs;
                    bool fullRoundTrip = r.Clocks.SelectedRoleClockKnown && Math.Abs(delay - roleLoop) <= Math.Max(1,
                        fs * (a.TimingUncertaintySeconds + b.TimingUncertaintySeconds + measurement));
                    bool nearRoundTripMode = r.Clocks.SelectedRoleClockKnown && !fullRoundTrip && Math.Abs(delay - roleLoop) <= Math.Max(1,
                        fs * (a.TimingUncertaintySeconds + b.TimingUncertaintySeconds + measurement + r.Clocks.ModeDiscrepancySeconds));
                    bool calibratedRolesDistinct = cal.TemplateReliable &&
                        incidentShape[i] > reflectionShape[i] + .10 && reflectionShape[j] > incidentShape[j] + .10;
                    bool sameBranchUnknown = fullRoundTrip && !calibratedRolesDistinct;
                    bool softModeUnknown = nearRoundTripMode && !calibratedRolesDistinct;
                    bool physicalClockUnknown=!r.Clocks.PhysicalLoopKnown||!r.Clocks.SelectedRoleClockKnown;
                    double earliestDelay = b.OnsetEarliest - a.OnsetLatest;
                    double latestDelay = b.OnsetLatest - a.OnsetEarliest;
                    bool firstUnknown = firstArrival && (!onsetClockUsable || !OnsetUsable(a) || !OnsetUsable(b) ||
                        earliestDelay <= 0 || !SampleDelayFitsWindow(latestDelay, fs, cal.OnsetRoundTripMinimumSeconds));
                    // Gain > 1 and inverted reflections are allowed. Gain is a diagnostic
                    // ratio, never a passive reflection coefficient or charge compensation.
                    var pair = new EighthPair {
                        Incident = a, Reflection = b, T0 = t0, T1 = t1, DelaySamples = delay,
                        Correlation = corr, Score = score, Gain = gain,
                        RelativePolarity = polarity,
                        TimingUncertaintySeconds = evidence.TimingUncertaintySeconds,
                        Accepted = waveformPassed && !sameBranchUnknown && !softModeUnknown && !firstUnknown && !physicalClockUnknown,
                        Role = waveformPassed && !sameBranchUnknown && !softModeUnknown && !firstUnknown && !physicalClockUnknown ? "independent" : "review", FamilyRootPeak = a.Peak,
                        FullRoundTripRoleAmbiguous = sameBranchUnknown,
                        ModeRoundTripRoleUnresolved = softModeUnknown,
                        FirstArrivalUnresolved = firstUnknown,
                        Reason = physicalClockUnknown ? r.Clocks.Reason : firstUnknown ? "first_arrival_support_or_CAL_clock_interval_unresolved" :
                            sameBranchUnknown ? "full_roundtrip_pair_I_I_or_R_R_vs_endpoint_role_unresolved" :
                            softModeUnknown ? "CAL_mode_discrepancy_near_RT_requires_distinct_observed_branch_family" :
                            waveformPassed ? "complete_LOC_pair_CAL_clock_valid_template_auxiliary_only" :
                            "complete_LOC_shape_below_requested_floor_requires_independent_branch_recurrence"
                    };
                    evidence.Pair = pair; evidence.Passed = waveformPassed;
                    evidence.Reason = pair.Reason;
                }
            }
            r.CandidateCount = candidates;
            EighthPropagationMath.Resolve(r, eligible, fs, roundTripSeconds,r.Clocks,
                raw.Length, maximumIncidents, suppressionOrderLimit, ShapeCorrelation);
            return r;
        }

        private sealed class PulseComparer : IComparer<EighthPulse>
        {
            internal static readonly PulseComparer Instance = new PulseComparer();
            public int Compare(EighthPulse a, EighthPulse b) { return a.Peak.CompareTo(b.Peak); }
        }

        internal static bool CalibrationUsable(EighthCalibration c)
        {
            return c != null && c.Valid && c.Incident != null && c.Reflection != null && c.Incident.Valid && c.Reflection.Valid &&
                Finite(c.SampleRateHz) && c.SampleRateHz > 0 && Finite(c.RoundTripSeconds) && c.RoundTripSeconds > 0 &&
                Finite(c.TimingUncertaintySeconds) && c.TimingUncertaintySeconds >= 0 &&
                Finite(c.MeasurementTimingUncertaintySeconds) && c.MeasurementTimingUncertaintySeconds >= 0 &&
                Finite(c.ModeDiscrepancySeconds) && c.ModeDiscrepancySeconds >= 0 &&
                (c.RelativePolarity == 1 || c.RelativePolarity == -1);
        }

        internal static bool OnsetUsable(EighthPulse p)
        {
            return p != null && p.Valid && p.OnsetIdentifiable && p.OnsetReason != "not_measured" &&
                !String.IsNullOrWhiteSpace(p.OnsetReason) && p.Start >= 0 && p.Peak <= p.End && p.Start <= p.OnsetEarliest &&
                p.OnsetEarliest <= p.Onset && p.Onset <= p.OnsetLatest && p.OnsetLatest <= p.Peak &&
                Finite(p.SampleRateHz) && p.SampleRateHz > 0 &&
                Finite(p.OnsetTimingUncertaintySeconds) && p.OnsetTimingUncertaintySeconds >= 0 &&
                p.OnsetTimingUncertaintySeconds * p.SampleRateHz + 1e-9 >=
                    Math.Max(p.Onset - p.OnsetEarliest, p.OnsetLatest - p.Onset);
        }

        internal static bool OnsetCalibrationUsable(EighthCalibration c)
        {
            return CalibrationUsable(c) && c.OnsetClockIdentifiable &&
                OnsetUsable(c.Incident) && OnsetUsable(c.Reflection) &&
                !String.IsNullOrWhiteSpace(c.OnsetClockReason) && c.OnsetClockReason != "not_measured" &&
                Finite(c.OnsetRoundTripSeconds) && Finite(c.OnsetRoundTripMinimumSeconds) &&
                Finite(c.OnsetRoundTripMaximumSeconds) && c.OnsetRoundTripMinimumSeconds > 0 &&
                c.OnsetRoundTripMinimumSeconds <= c.OnsetRoundTripSeconds &&
                c.OnsetRoundTripSeconds <= c.OnsetRoundTripMaximumSeconds &&
                Finite(c.OnsetMeasurementTimingUncertaintySeconds) && c.OnsetMeasurementTimingUncertaintySeconds >=
                    Math.Max(c.OnsetRoundTripSeconds - c.OnsetRoundTripMinimumSeconds,
                        c.OnsetRoundTripMaximumSeconds - c.OnsetRoundTripSeconds);
        }

        private static void AppendUnpaired(EighthAnalysis r, List<EighthPulse> pulses, bool[] eligible, int maximum)
        {
            EighthKernelResources.Work(2L*pulses.Count,"unpaired_output_dispatch");
            EighthKernelResources.Meta(32+16L*Math.Min(pulses.Count,maximum),"unpaired_output_list");
            int count=0;
            for(int i=0;i<pulses.Count;i++)if(eligible[i])count++;
            r.PublicationEligibleCount=count;
            r.PublicationLimited=count>maximum;
            r.PublicationComplete=!r.PublicationLimited;
            r.PublicationOmittedCount=Math.Max(0,count-maximum);
            // The record was measured, but unavailable calibration/baseline/clock
            // evidence did not evaluate a propagation graph. Do not claim it did.
            if(r.PublicationLimited){r.LimitReached=true;r.PropagationComplete=false;}
            for (int i = 0; i < pulses.Count; i++) if (eligible[i])
            {
                if (r.Unpaired.Count >= maximum) break;
                r.Unpaired.Add(pulses[i]);
            }
            r.PublishedCount=r.Unpaired.Count;
        }

        private static EighthPulse FindMarker(List<EighthPulse> pulses, int marker)
        {
            EighthPulse found = null;
            foreach (EighthPulse p in pulses) if (marker >= p.Start && marker <= p.End)
            {
                if (found != null) return null;
                found = p;
            }
            return found;
        }

        internal static double EstimateNoise(double[] raw)
        {
            if (raw == null || raw.Length < 2) return Double.NaN;
            int count = Math.Min(65536, raw.Length - 1);
            var differences = new double[count];
            for (int i = 0; i < count; i++)
            {
                int index = 1 + (int)((long)i * (raw.Length - 1) / count);
                if (!Finite(raw[index]) || !Finite(raw[index - 1])) return Double.NaN;
                double difference = raw[index] - raw[index - 1];
                if (!Finite(difference)) return Double.NaN;
                differences[i] = difference;
            }
            double center = Median((double[])differences.Clone());
            for (int i = 0; i < count; i++) differences[i] = Math.Abs(differences[i] - center);
            double sigma = 1.4826 * Median(differences) / Math.Sqrt(2);
            if (!Finite(sigma)) return Double.NaN;
            // Adjacent differences measure innovations, not the amplitude of
            // coloured background. Robust native, contiguous blocks supply the
            // missing amplitude scale; no resampling or filtering of raw data.
            int width = Math.Min(256, Math.Max(16, raw.Length / 64));
            int blocks = Math.Min(257, Math.Max(1, raw.Length / width));
            var amplitudes = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                int start = blocks == 1 ? 0 : (int)((long)b * (raw.Length - width) / (blocks - 1));
                int length = Math.Min(width, raw.Length - start);
                var values = new double[length]; Array.Copy(raw, start, values, 0, length);
                double local = Median(values);
                for (int j = 0; j < length; j++)
                {
                    if (!Finite(raw[start + j])) return Double.NaN;
                    values[j] = Math.Abs(raw[start + j] - local);
                }
                amplitudes[b] = 1.4826 * Median(values);
            }
            double amplitudeSigma = Median(amplitudes);
            return Finite(amplitudeSigma) ? Math.Max(1e-15, Math.Max(sigma, amplitudeSigma)) : Double.NaN;
        }

        // A global median ignores a noisy tail occupying a minority of the LOC.
        // Estimate a bounded local amplitude floor on the native residual. This
        // changes discovery/support gates only, never filters samples or shifts
        // indices. Absolute residual (not innovations alone) covers correlated
        // excursions. CAL retains the separately validated marker measurement.
        private static double[] LocalNoiseFloor(double[] residual, double globalNoise, double fs, double measuredSupportSeconds, out int measuredWindow)
        {
            // Use a time scale from guarded CAL supports, converted to LOC's
            // native rate. A fixed 512-sample window erased clean wide packets.
            // The factor provides quiet context around the two branch supports;
            // this is not a cable position or an allowed source-count prior.
            double requested = Math.Max(512, 8 * measuredSupportSeconds * fs);
            int width = (int)Math.Min(residual.Length, requested), stride = Math.Max(1, width / 2);
            measuredWindow = width;
            int blocks = 1 + (residual.Length - 1) / stride;
            var levels = new double[blocks];
            for (int b = 0; b < blocks; b++)
            {
                int center = b * stride;
                int start = Math.Max(0, Math.Min(residual.Length - width, center - width / 2));
                var values = new double[width];
                for (int j = 0; j < width; j++) values[j] = Math.Abs(residual[start + j]);
                levels[b] = Math.Max(globalNoise, 1.4826 * Median(values));
            }
            var local = new double[residual.Length];
            for (int i = 0; i < local.Length; i++)
            {
                int b = Math.Min(blocks - 1, i / stride), next = Math.Min(blocks - 1, b + 1);
                double f = (i - b * stride) / (double)stride;
                local[i] = levels[b] * (1 - f) + levels[next] * f;
            }
            return local;
        }

        // EXPERIMENT ONLY. Add observed narrow packets from two independently
        // quiet affine guards; existing supports are never replaced or split.
        // No location, family delay prediction or expected source count enters.
        private static bool RecoverAffineGuardPackets(double[] raw, double fs, double supportSeconds, PacketSet set, EighthCalibration cal,EighthResolvedClocks clocks)
        {
            if (!Finite(supportSeconds) || supportSeconds <= 0 || !set.BaselineStable) return true;
            // CAL guard samples belong to CAL's grid, not to LOC's grid.
            double activeHalf = Math.Max(cal.Incident.WidthSamples, cal.Reflection.WidthSamples) - 2 * Quiet - 1;
            double requested = Math.Ceiling(Math.Max(0,activeHalf) / cal.SampleRateHz * fs / 2) + Quiet;
            if (!Finite(requested) || requested > MaximumPacketSamples / 2) return true;
            int radius = Math.Max(2 * Quiet + 1, (int)requested);
            if (2 * radius + 3 >= raw.Length) return true;
            var meter=EighthKernelResources.Current;
            meter.Meta(256+160L*set.Pulses.Count,"recovery_source_indices");meter.Work(4L*set.Pulses.Count,"recovery_partition");
            var measured = set.Pulses.ToArray();
            var solid = new List<EighthPulse>(); var retry = new List<EighthPulse>();
            foreach(var p in measured) { if(!p.Valid && p.Reason=="low_snr") retry.Add(p); else solid.Add(p); }
            var existing = solid.ToArray(); // Never split or replace a valid/structurally invalid packet.
            var replaced = new Dictionary<EighthPulse,EighthPulse>();
            var recovered = new List<EighthPulse>(); int cursor = 0, retryCursor = 0, work = 0;
            long sampleWork = 0;
            for (int peak = radius + 1; peak < raw.Length - radius - 1; peak++)
            {
                meter.Work(16,"recovery_scan_and_guards");
                while (cursor < existing.Length && existing[cursor].End < peak - radius) {meter.Work(1,"recovery_existing_cursor");cursor++;}
                if (cursor < existing.Length && existing[cursor].Start <= peak + radius)
                { peak = Math.Max(peak, existing[cursor].End + radius); continue; }
                while(retryCursor<retry.Count && retry[retryCursor].End<peak-radius){meter.Work(1,"recovery_retry_cursor");retryCursor++;}
                EighthPulse previous=null;
                if(retryCursor<retry.Count && retry[retryCursor].Start<=peak+radius)
                {
                    previous=retry[retryCursor];
                    if(previous.Peak!=peak || previous.Start<peak-radius || previous.End>peak+radius ||
                        retryCursor+1<retry.Count && retry[retryCursor+1].Start<=peak+radius)continue;
                }
                bool extremum = raw[peak] >= raw[peak - 1] && raw[peak] > raw[peak + 1] ||
                                raw[peak] <= raw[peak - 1] && raw[peak] < raw[peak + 1];
                if (!extremum || Math.Abs(raw[peak] - set.Baseline) < 4.5 * set.Noise) continue;
                int start = peak - radius, end = peak + radius;
                if (Math.Abs(raw[peak] - (raw[start] / 2 + raw[end] / 2)) < 6 * set.Noise) continue;
                meter.RecoveryCheck();work++;
                sampleWork += 2L*radius+1;
                if(sampleWork>8L*MaximumRecordSamples)meter.Fail("recovery_samples_budget_exceeded_no_prefix");
                double left = raw[start]/3 + raw[start + 1]/3 + raw[start + 2]/3;
                double right = raw[end - 2]/3 + raw[end - 1]/3 + raw[end]/3;
                double leftSlope = (raw[start + 2] - raw[start]) / 2;
                double rightSlope = (raw[end] - raw[end - 2]) / 2;
                double slope = (right - left) / (2 * radius - 2);
                double error = 0;
                for (int k = -1; k <= 1; k++)
                {
                    double a = raw[start + 1 + k] - (left + k * leftSlope);
                    double b = raw[end - 1 + k] - (right + k * rightSlope);
                    error += a * a + b * b;
                }
                double noise = Math.Max(set.Noise, Math.Sqrt(error / 2));
                if (!Finite(noise) || !Finite(slope) || Math.Abs(slope - leftSlope) > 4 * noise || Math.Abs(slope - rightSlope) > 4 * noise) continue;
                double baseline = left + slope * (radius - 1);
                meter.RecoverySamples(2*radius+1);meter.Work(8L*(2*radius+1)+64,"recovery_sample_and_onset_passes");
                var samples = new double[2 * radius + 1]; bool quiet = true;
                int active = 0, measuredPeak = -1, first = -1; double max = 0, amplitude = 0;
                for (int k = 0; k < samples.Length; k++)
                {
                    double value = raw[start + k] - (baseline + slope * (k - radius));
                    samples[k] = value;
                    if (!Finite(value)) { quiet = false; break; }
                    if ((k < Quiet || k >= samples.Length - Quiet) && Math.Abs(value) > 2.5 * noise) quiet = false;
                    if (Math.Abs(value) > 2.5 * noise) { active++; if (first < 0) first = k; }
                    if (Math.Abs(value) > max) { max = Math.Abs(value); amplitude = value; measuredPeak = start + k; }
                }
                if (!quiet || measuredPeak != peak || active < 3 || max < 6 * noise) continue;
                int flat = 0; bool clipped = false;
                foreach (double value in samples) { flat = Math.Abs(Math.Abs(value) - max) <= Math.Max(1e-15, max * 1e-10) ? flat + 1 : 0; if (flat >= 3) clipped = true; }
                if (clipped) continue;
                meter.Meta(640+12L*samples.Length,"recovery_descriptor_fronts_lists");
                var p = new EighthPulse {Start=start,End=end,Peak=peak,Onset=start+first,WidthSamples=samples.Length,
                    ActiveSamples=active,Amplitude=amplitude,AbsAmplitude=max,Noise=noise,SampleRateHz=fs,
                    LocalBaseline=baseline,Samples=samples,Valid=true,Reason="EXPERIMENT_local_affine_guard_recovered_not_release"};
                double gate = Math.Max(3 * noise, .05 * max);
                for (int k = first; k <= radius; k++) if (Math.Abs(samples[k]) >= gate) { p.Onset = start + k; break; }
                int onsetOffset = p.Onset - start;
                double rise = onsetOffset > 0 ? Math.Abs(samples[onsetOffset] / max - samples[onsetOffset - 1] / max) : 0;
                p.TimingUncertaintySeconds = Math.Max(.5, Math.Min(p.WidthSamples, rise > 0 ? (noise / max) / rise : p.WidthSamples)) / fs;
                MeasureOnsetEvidence(p, gate);
                if(!Finite(p.Amplitude)||!Finite(p.AbsAmplitude)||!Finite(p.Noise)||!Finite(p.SampleRateHz)||!Finite(p.LocalBaseline)||!Finite(p.TimingUncertaintySeconds)||!Finite(p.OnsetTimingUncertaintySeconds))meter.Fail("nonfinite_recovery_packet_no_prefix");
                recovered.Add(p);meter.Receipt.RecoveredPackets++;
                if(previous!=null)replaced[p]=previous;
                // Recovery count is bounded by explicit cumulative sample/metadata work,
                // never by the unrelated maximum published-incident parameter.
                peak = end + radius;
            }
            // A locally quiet residual is insufficient: coloured AR noise also
            // creates compact excursions. Admit recovery evidence only when an
            // three adjacent observed complete pairs support the same two
            // branches. The first adjacent hop of at least one branch must have
            // two original independent-detector packets. Later branch samples
            // remain observed, never filled in using a predicted arrival.
            if(recovered.Count==0)return true;
            meter.Meta(256+160L*(existing.Length+recovered.Count),"recovery_verification_graph");
            meter.Sort(existing.Length+recovered.Count,"recovery_sort");
            var original = new HashSet<EighthPulse>(existing);
            var all = new List<EighthPulse>(existing); all.AddRange(recovered); all.Sort(PulseComparer.Instance);
            var next = new Dictionary<EighthPulse,List<EighthPulse>>();
            // These links compare native peaks, not the selected display mode.
            double tc = clocks.PeakLoopSeconds * fs;
            double dutPeakWindow = cal.RoundTripSeconds * fs;
            foreach (var a in all)
            {
                meter.Work(1,"recovery_link_source");
                var links = new List<EighthPulse>(); next[a] = links;
                if (!a.Valid || a.AbsAmplitude < 6 * a.Noise) continue;
                double low = a.Peak + .75 * tc, high = a.Peak + 1.25 * tc;
                int l = 0, h = all.Count;
                while (l < h) { int m = l + (h-l)/2; if (all[m].Peak < low) l=m+1; else h=m; }
                for (int ix=l; ix<all.Count && all[ix].Peak<=high; ix++)
                {
                    meter.RecoveryCheck();work++;
                    var b=all[ix];
                    if (!b.Valid || b.AbsAmplitude < 6 * b.Noise || b.Start <= a.End) continue;
                    if (Math.Abs(b.Peak-a.Peak-tc) > EighthPropagationMath.Tolerance(a,b,fs,clocks.LoopUncertaintySeconds,1,tc)) continue;
                    if (Math.Abs(ShapeCorrelation(a,b)) < .90 || b.AbsAmplitude>=a.AbsAmplitude) continue;
                    meter.Meta(32,"recovery_link");links.Add(b);
                }
            }
            var verified = new HashSet<EighthPulse>();
            for (int ai=0; ai<all.Count; ai++)
            {
                var a=all[ai]; if (!a.Valid || a.AbsAmplitude<6*a.Noise || next[a].Count==0) continue;
                for (int bi=ai+1; bi<all.Count && all[bi].Peak-a.Peak<=dutPeakWindow; bi++)
                {
                    meter.RecoveryCheck();work++;
                    var b=all[bi];
                    if (!b.Valid || b.AbsAmplitude<6*b.Noise || b.Start<=a.End || next[b].Count==0) continue;
                    if (Math.Abs(tc-(b.Peak-a.Peak))<=EighthPropagationMath.Tolerance(a,b,fs,clocks.LoopUncertaintySeconds,1,tc)) continue;
                    foreach (var c in next[a]) foreach (var d in next[b])
                    {
                        meter.RecoveryCheck();work++;
                        if (c.Peak<=b.Peak || d.Start<=c.End || !EighthPropagationMath.ConsistentRatios(a,b,c,d) || Math.Sign(ShapeCorrelation(a,c))!=Math.Sign(ShapeCorrelation(b,d))) continue;
                        if(!EighthPropagationMath.Decays(a,c)||!EighthPropagationMath.Decays(b,d))continue;
                        foreach (var e in next[c]) foreach (var f in next[d])
                        {
                            meter.RecoveryCheck();work++;
                            if (e.Peak<=d.Peak || f.Start<=e.End || !EighthPropagationMath.ConsistentRatios(c,d,e,f) || Math.Sign(ShapeCorrelation(c,e))!=Math.Sign(ShapeCorrelation(d,f))) continue;
                            // Use the same observed cumulative drop principle as
                            // the propagation kernel; no skipped or missing hop.
                            if(!EighthPropagationMath.Decays(a,e)||!EighthPropagationMath.Decays(b,f)||
                                !EighthPropagationMath.ConsistentRatios(a,b,e,f))continue;
                            if (!(original.Contains(a)&&original.Contains(c)) && !(original.Contains(b)&&original.Contains(d))) continue;
                            meter.Meta(80,"recovery_verified_tuple_members");
                            foreach (var p in new[]{a,b,c,d,e,f}) if (!original.Contains(p)) verified.Add(p);
                        }
                    }
                }
            }
            foreach(var p in recovered)
            {
                if(!verified.Contains(p)) {set.RecoveryUnverified.Add(p);continue;}
                EighthPulse old;
                if(replaced.TryGetValue(p,out old)) {meter.Work(set.Pulses.Count,"recovery_replaced_packet_remove");set.Pulses.Remove(old);set.RecoveryReplacedLowSnr.Add(old);}
            }
            meter.Work(set.Pulses.Count+recovered.Count,"recovery_commit");meter.Sort(set.Pulses.Count+verified.Count,"recovery_commit_sort");
            set.Pulses.AddRange(verified); set.Pulses.Sort(PulseComparer.Instance);
            return true;
        }

        private static PacketSet Measure(double[] raw,double fs,bool calibrationRecord=false,double measuredSupportSeconds=0)
        {
            if(EighthKernelResources.Current!=null)return MeasureLedger(raw,fs,calibrationRecord,measuredSupportSeconds);
            using(var scope=EighthKernelResources.Begin(new EighthKernelLimits()))
            {
                try{return MeasureLedger(raw,fs,calibrationRecord,measuredSupportSeconds);}
                catch(EighthBudgetExceeded e){return new PacketSet{Reason=e.Message};}
            }
        }
        private static PacketSet MeasureLedger(double[] raw,double fs,bool calibrationRecord,double measuredSupportSeconds)
        {
            var meter=EighthKernelResources.Current;var r=new PacketSet();
            var budget=new Method8.Experimental.FullRecordLedger.FullRecordMeasurement.ResourceBudget{
                MaximumContextBytes=Math.Min(384L*1024*1024,meter.RemainingBytes),
                MaximumMetadataBytes=Math.Min(32L*1024*1024,Math.Min(meter.RemainingBytes,meter.RemainingMetadata)),
                MaximumWorkUnits=meter.RemainingWork};
            var prep=Method8.Experimental.FullRecordLedger.FullRecordMeasurement.Prepare(raw,fs,calibrationRecord,measuredSupportSeconds,budget);
            meter.Work(prep.SampleVisits+prep.SortWorkUnits,"whole_record_prepare");
            meter.Receipt.MeasurementWorkUnits+=prep.SampleVisits+prep.SortWorkUnits;
            meter.Bytes(prep.PeakLogicalContextBytes,"whole_record_context_peak");
            if(!prep.Complete){r.Reason=prep.Reason;return r;}
            var c=prep.Context;r.Baseline=c.Baseline;r.Noise=c.Noise;
            r.LocalNoiseWindowSamples=c.LocalNoiseWindowSamples;r.BaselineCorrectionUsed=c.BaselineCorrectionUsed;r.BaselineCorrectionSummary=c.BaselineCorrectionSummary;
            // The already-owned background is retained, not silently copied again.
            r.BackgroundSamples=c.Background??new double[0];
            budget.MaximumContextBytes=384L*1024*1024;
            budget.MaximumMetadataBytes=Math.Min(32L*1024*1024,Math.Min(meter.RemainingBytes,meter.RemainingMetadata));
            budget.MaximumWorkUnits=c.PreparationWorkUnits+meter.RemainingWork;
            var scan=Method8.Experimental.FullRecordLedger.FullRecordMeasurement.Scan(c,meter.Limits.MeasurementPageSamples,budget);
            meter.Work(scan.ScanSampleVisits,"whole_record_scan");meter.Receipt.MeasurementWorkUnits+=scan.ScanSampleVisits;
            meter.Meta(scan.MetadataBytes,"whole_record_ledger");
            if(!scan.Complete){r.BackgroundSamples=new double[0];r.Reason=scan.Reason;return r;}
            var pending=new List<EighthPulse>();long materialized=0;
            foreach(var q in scan.Ledger)
            {
                if(q.WidthSamples>c.SampleCount-materialized)meter.Fail("standard_materialized_support_exceeds_record_no_prefix");
                meter.Bytes(32+8L*q.WidthSamples,"standard_sample_array");
                meter.Meta(672+4L*q.ObservedFrontCount,"standard_descriptor_fronts_list");
                meter.Work(q.WidthSamples+q.ObservedFrontCount+1L,"standard_compatibility_copy");
                var samples=new double[q.WidthSamples];for(int i=0;i<samples.Length;i++)samples[i]=q.SampleAt(i);
                var fronts=new int[q.ObservedFrontCount];for(int i=0;i<fronts.Length;i++)fronts[i]=q.ObservedFrontAt(i);
                var p=new EighthPulse{Start=q.Start,End=q.End,Peak=q.Peak,Onset=q.Onset,WidthSamples=q.WidthSamples,ActiveSamples=q.ActiveSamples,
                    Amplitude=q.Amplitude,AbsAmplitude=q.AbsAmplitude,Noise=q.Noise,SampleRateHz=q.SampleRateHz,LocalBaseline=q.LocalBaseline,
                    TimingUncertaintySeconds=q.TimingUncertaintySeconds,OnsetIdentifiable=q.OnsetIdentifiable,OnsetEarliest=q.OnsetEarliest,OnsetLatest=q.OnsetLatest,
                    OnsetTimingUncertaintySeconds=q.OnsetTimingUncertaintySeconds,OnsetReason=q.OnsetReason,ObservedFrontStarts=fronts,Samples=samples,
                    Valid=q.Valid,EdgeClipped=q.EdgeClipped,FlatTop=q.FlatTop,Reason=q.Reason};
                pending.Add(p);materialized+=q.WidthSamples;meter.Receipt.StandardSamplesMaterialized+=q.WidthSamples;meter.Receipt.StandardPackets++;
            }
            r.Pulses=pending;r.BaselineStable=scan.BaselineStable.Value;r.Valid=true;r.Reason="measured";return r;
        }

        private static void MeasureOnsetEvidence(EighthPulse p, double gate)
        {
            p.OnsetEarliest = p.OnsetLatest = p.Onset;
            // A resolved leading lobe may precede the largest lobe and cross
            // zero normally. Only investigate the weak prefix BEFORE that
            // first lobe; broad smooth fronts and multipolar fronts remain valid.
            double resolvedLobe = Math.Max(6 * p.Noise, 2 * gate);
            int stop = p.Peak;
            for (int i = p.Onset; i <= p.Peak; i++)
                if (Math.Abs(p.Samples[i - p.Start]) >= resolvedLobe) { stop = i; break; }
            bool above = false, returned = false;
            int observedStarts = 0;
            for (int i = p.Onset; i <= stop; i++)
            {
                double value = Math.Abs(p.Samples[i - p.Start]);
                if (value >= gate + p.Noise && !above)
                {
                    if (observedStarts == 0 || returned) observedStarts++;
                    if (returned) p.OnsetLatest = i;
                    above = true; returned = false;
                }
                else if (above && value < Math.Max(0, gate - p.Noise))
                { above = false; returned = true; }
            }
            p.OnsetIdentifiable = p.Valid && observedStarts <= 1;
            p.OnsetReason = !p.Valid ? "invalid_packet_support" : observedStarts > 1 ?
                "multiple_noise_resolved_weak_prefix_restarts_before_first_lobe" : "single_resolved_leading_front";
            // A real pre-echo may exceed the first-lobe threshold: stopping at
            // that first lobe misses stronger overlapping fronts. Record the
            // whole pre-peak sequence. A single bipolar leading lobe is not a
            // restart ambiguity, but repeated same-sign resolved starts are.
            var starts = new List<int>(); int positive = 0, negative = 0;
            above = false;
            for (int i = p.Onset; i <= p.Peak; i++)
            {
                double value = p.Samples[i - p.Start];
                if (Math.Abs(value) >= gate + p.Noise && !above)
                {
                    starts.Add(i); if (value > 0) positive++; else negative++;
                    above = true;
                }
                else if (above && Math.Abs(value) < Math.Max(0, gate - p.Noise)) above = false;
            }
            p.ObservedFrontStarts = starts.ToArray();
            if (positive > 1 || negative > 1)
            {
                p.OnsetLatest = Math.Max(p.OnsetLatest, starts[starts.Count - 1]);
                p.OnsetIdentifiable = false;
                p.OnsetReason = "multiple_same_polarity_fronts_in_full_prepeak_support";
            }
            p.OnsetTimingUncertaintySeconds = p.TimingUncertaintySeconds +
                Math.Max(p.Onset - p.OnsetEarliest, p.OnsetLatest - p.Onset) / p.SampleRateHz;
        }

        // Align measured peaks at equal physical times. Resampling uses the lower
        // native rate and zero only beyond already-observed quiet guards. No time
        // warping, frequency assumption, arbitrary upsampling or peak-order rule.
        internal static double ShapeCorrelation(EighthPulse a, EighthPulse b)
        {
            if (a == null || b == null || a.Samples == null || b.Samples == null ||
                a.Samples.Length < 3 || b.Samples.Length < 3 || a.AbsAmplitude <= 0 || b.AbsAmplitude <= 0 ||
                !Finite(a.SampleRateHz) || !Finite(b.SampleRateHz) || a.SampleRateHz <= 0 || b.SampleRateHz <= 0) return 0;
            // On the same native clock, alignment is an integer offset. A
            // sample -> seconds -> sample round trip can put an exact boundary
            // infinitesimally outside support and silently discard that sample.
            // Preserve the entire original support without interpolation.
            if (a.SampleRateHz == b.SampleRateHz)
            {
                long firstOffset = Math.Min((long)a.Start-a.Peak,(long)b.Start-b.Peak);
                long lastOffset = Math.Max((long)a.End-a.Peak,(long)b.End-b.Peak);
                if (lastOffset < firstOffset || lastOffset-firstOffset > MaximumPacketSamples*2L) return 0;
                if(EighthKernelResources.Current!=null)EighthKernelResources.Current.Correlation(lastOffset-firstOffset+1,false);
                double nativeAA=0,nativeBB=0,nativeAB=0;
                for (long k=firstOffset;k<=lastOffset;k++)
                {
                    long ai=(long)a.Peak-a.Start+k,bi=(long)b.Peak-b.Start+k;
                    double x=ai>=0&&ai<a.Samples.Length?a.Samples[(int)ai]/a.AbsAmplitude:0;
                    double y=bi>=0&&bi<b.Samples.Length?b.Samples[(int)bi]/b.AbsAmplitude:0;
                    nativeAA+=x*x;nativeBB+=y*y;nativeAB+=x*y;
                }
                if(nativeAA<=0||nativeBB<=0||!Finite(nativeAA)||!Finite(nativeBB)||!Finite(nativeAB))return 0;
                return Math.Max(-1,Math.Min(1,nativeAB/Math.Sqrt(nativeAA*nativeBB)));
            }
            double rate = Math.Min(a.SampleRateHz, b.SampleRateHz);
            double first = Math.Min((a.Start - a.Peak) / a.SampleRateHz, (b.Start - b.Peak) / b.SampleRateHz);
            double last = Math.Max((a.End - a.Peak) / a.SampleRateHz, (b.End - b.Peak) / b.SampleRateHz);
            int from = (int)Math.Floor(first * rate), to = (int)Math.Ceiling(last * rate);
            if ((long)to - from > MaximumPacketSamples * 2) return 0;
            if(EighthKernelResources.Current!=null)EighthKernelResources.Current.Correlation((long)to-from+1,true);
            double aa = 0, bb = 0, ab = 0;
            for (int k = from; k <= to; k++)
            {
                double t = k / rate;
                double x = AtNormalized(a, t), y = AtNormalized(b, t);
                aa += x * x; bb += y * y; ab += x * y;
            }
            if (aa <= 0 || bb <= 0 || !Finite(aa) || !Finite(bb) || !Finite(ab)) return 0;
            return Math.Max(-1, Math.Min(1, ab / Math.Sqrt(aa * bb)));
        }

        private static double AtNormalized(EighthPulse p, double time)
        {
            double x = p.Peak - p.Start + time * p.SampleRateHz;
            if (x < 0 || x > p.Samples.Length - 1) return 0;
            int lo = (int)Math.Floor(x), hi = Math.Min(p.Samples.Length - 1, lo + 1);
            // Normalize before interpolation: two finite opposite-polarity samples
            // can otherwise overflow in (high-low), including Infinity*0 at an exact grid point.
            double a = p.Samples[lo] / p.AbsAmplitude, b = p.Samples[hi] / p.AbsAmplitude;
            return a + (b - a) * (x - lo);
        }

        private static double Median(double[] values)
        {
            Array.Sort(values);
            int m = values.Length / 2;
            return values.Length % 2 != 0 ? values[m] : values[m - 1] / 2 + values[m] / 2;
        }

        private static void ValidateCalibrationNumbers(EighthCalibration c)
        {
            EighthKernelResources.Work(16,"CAL_final_numeric_fields");
            if(!Finite(c.SignedGain)||!Finite(c.RoundTripSeconds)||!Finite(c.OnsetRoundTripSeconds)||
                !Finite(c.SampleRateHz)||!Finite(c.NoiseSigma)||!Finite(c.Baseline)||!Finite(c.Quality)||
                !Finite(c.TimingUncertaintySeconds)||!Finite(c.ResolutionSeconds)||!Finite(c.MeasurementTimingUncertaintySeconds)||
                !Finite(c.ModeDiscrepancySeconds)||!Finite(c.OnsetRoundTripMinimumSeconds)||!Finite(c.OnsetRoundTripMaximumSeconds)||
                !Finite(c.OnsetMeasurementTimingUncertaintySeconds))
                EighthKernelResources.Fail("nonfinite_derived_calibration_no_prefix");
        }
        private static bool PairNumbersFinite(EighthPair p)
        {
            return Finite(p.T0)&&Finite(p.T1)&&Finite(p.DelaySamples)&&Finite(p.Correlation)&&Finite(p.Score)&&Finite(p.Gain)&&Finite(p.TimingUncertaintySeconds);
        }
        private static void ValidateAnalysisNumbers(EighthAnalysis a)
        {
            EighthKernelResources.Work(2+12L*a.CandidateEvidence.Count+7L*a.Pairs.Count,"analysis_final_numeric_fields");
            if(!Finite(a.NoiseSigma)||!Finite(a.Baseline))EighthKernelResources.Fail("nonfinite_derived_analysis_no_prefix");
            foreach(var e in a.CandidateEvidence)
                if(!Finite(e.RawCorrelation)||!Finite(e.IncidentSnr)||!Finite(e.ReflectionSnr)||!Finite(e.TimingUncertaintySeconds)||
                    (e.Pair!=null&&!PairNumbersFinite(e.Pair)))EighthKernelResources.Fail("nonfinite_derived_candidate_no_prefix");
            foreach(var p in a.Pairs)if(!PairNumbersFinite(p))EighthKernelResources.Fail("nonfinite_derived_pair_no_prefix");
        }
        private static bool Finite(double x) { return !Double.IsNaN(x) && !Double.IsInfinity(x); }
        private static EighthCalibration CalibrationFailure(EighthCalibration r, string reason)
        {
            r.Valid = false; r.Reason = reason;
            if (!Finite(r.NoiseSigma)) r.NoiseSigma = 0;
            if (!Finite(r.Baseline)) r.Baseline = 0;
            if (!Finite(r.SignedGain)) r.SignedGain = 0;
            return r;
        }
        private static EighthAnalysis AnalysisFailure(EighthAnalysis r, string reason)
        {
            r.Valid = false; r.PropagationComplete = false; r.Reason = reason;
            if(!String.IsNullOrEmpty(reason)&&reason.IndexOf("budget",StringComparison.Ordinal)>=0)r.LimitReached=true;
            r.GraphEvaluationComplete=r.PublicationLimited=r.PublicationComplete=false;
            r.PublicationEligibleCount=r.PublishedCount=r.PublicationOmittedCount=0;
            foreach(var e in r.CandidateEvidence)if(e.Pair!=null){e.Pair.Accepted=false;e.Pair.Role="review";}
            r.Pairs.Clear();r.Unpaired.Clear();r.CandidateEvidence.Clear();r.Pulses.Clear();r.RecoveryUnverified.Clear();r.RecoveryReplacedLowSnr.Clear();r.BackgroundSamples=new double[0];
            r.CandidateCount=r.EchoCount=r.ReviewCount=r.AmbiguousCount=0;r.PropagationSummary="{}";
            if (!Finite(r.NoiseSigma)) r.NoiseSigma = 0;
            if (!Finite(r.Baseline)) r.Baseline = 0;
            return r;
        }
    }
}
