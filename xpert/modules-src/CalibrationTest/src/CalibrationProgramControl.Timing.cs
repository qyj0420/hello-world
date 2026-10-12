using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private bool useFirstArrivalTiming;
        [DefaultValue(false)]
        public bool UseFirstArrivalTiming
        {
            get { return useFirstArrivalTiming; }
            set
            {
                if (useFirstArrivalTiming == value) return;
                ClearCursorSelection();
                lock (stateLock)
                {
                    useFirstArrivalTiming = value;
                    foreach (PhaseState phase in CalibrationPhases())
                    {
                        phase.VelocityAdoptions.Clear();
                        RebuildAllDerivedNoLock(phase);
                    }
                }
                // A host may restore the project before its timing preference.
                // Retry the formal reader only when no waveform is held; never
                // replace an in-memory edit or reinterpret a loaded acquisition.
                if(!SimulationMode&&!HasCalibrationData&&!String.IsNullOrWhiteSpace(ProjectPath)&&!HasActiveHardwareSession)
                    LoadLiveCalibrations();
                ResetViewInternal(); Invalidate();
            }
        }
        public int GetIncidentFirstArrivalIndex(int phaseIndex, string levelCode)
        { TdrAutoAnalysisResult a = GetAnalysisNoLockSafe(phaseIndex, levelCode); return a == null ? -1 : a.IncidentFirstArrivalIndex; }
        public int GetReflectionFirstArrivalIndex(int phaseIndex, string levelCode)
        { TdrAutoAnalysisResult a = GetAnalysisNoLockSafe(phaseIndex, levelCode); return a == null ? -1 : a.ReflectionFirstArrivalIndex; }
        public double GetMeasuredVelocityMPerUs(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            lock (stateLock)
            {
                PhaseState phase = GetPhaseStateNoLock(phaseIndex); MarkerData m;
                if (!phase.Markers.TryGetValue(NormalizeLevelCode(levelCode), out m) || m.DistanceMarkerSeconds <= m.StartTimeSeconds) return Double.NaN;
                return 2 * phase.CableLengthMeters / ((m.DistanceMarkerSeconds - m.StartTimeSeconds) * 1e6);
            }
        }
        public string GetTimingStatus(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult a = GetAnalysisNoLockSafe(phaseIndex, levelCode);
            if (a == null || a.SearchStatus != TdrSearchStatus.Ok) return CalibrationText("未找到可靠的入射波和反射波。", "No reliable incident/reflection pair.");
            if (UseFirstArrivalTiming && (a.IncidentFirstArrivalIndex < 0 || a.ReflectionFirstArrivalIndex <= a.IncidentFirstArrivalIndex))
                return CalibrationText("首达未确认，请重新选择入射和反射波峰。", "First arrival is unconfirmed. Reselect the incident and reflection peaks.");
            return String.Empty;
        }
        private IEnumerable<PhaseState> CalibrationPhases()
        { yield return allPhase; for (int i = 0; i < phases.Length; i++) yield return phases[i]; }
        private void ApplyTimingNoLock(PhaseState phase, string level, WaveformData active, TdrAutoAnalysisResult result)
        {
            if (result == null || result.SearchStatus != TdrSearchStatus.Ok) { phase.Markers.Remove(level); return; }
            var selected=ReflectionSelectionNoLock(phase,level,false);
            if(selected!=null&&selected.Confirmed&&!selected.PeakAnchored) { RestoreManualReflectionTimingNoLock(phase,level,result);return; }
            // Always measure onset on the original waveform; the display filter cannot change timing evidence.
            WaveformData raw; if (!phase.Waveforms.TryGetValue(level, out raw)) raw = active;
            double rawBaseline, rawSigma;
            TdrReflectionAnalyzer.RawTimingStatistics(raw.Samples, out rawBaseline, out rawSigma);
            int separation = Math.Max(1, result.ReflectionIndex - result.IncidentIndex);
            if(selected!=null&&selected.PeakAnchored) {
                result.IncidentFirstArrivalIndex=CalibrationArrivalMath.FindSelectedPacket(raw.Samples,result.IncidentIndex,0,
                    result.ReflectionIndex-1,separation,rawBaseline,rawSigma);
                result.ReflectionFirstArrivalIndex=CalibrationArrivalMath.FindSelectedPacket(raw.Samples,result.ReflectionIndex,
                    result.IncidentIndex+1,raw.Samples.Length-1,separation,rawBaseline,rawSigma);
                if(result.ReflectionFirstArrivalIndex<=result.IncidentIndex)result.ReflectionFirstArrivalIndex=-1;
            } else {
            result.IncidentFirstArrivalIndex = CalibrationArrivalMath.Find(raw.Samples, result.IncidentIndex,
                0, Math.Min(raw.Samples.Length - 1, result.IncidentIndex + separation / 2), separation, rawBaseline, rawSigma);
            result.ReflectionFirstArrivalIndex = CalibrationArrivalMath.Find(raw.Samples, result.ReflectionIndex,
                Math.Max(result.IncidentIndex + 1, result.ReflectionIndex - separation / 2),
                Math.Min(raw.Samples.Length - 1, result.ReflectionIndex + separation / 2), separation, rawBaseline, rawSigma);
            result.ReflectionFirstArrivalIndex=CalibrationArrivalMath.LocalReflectionArrival(raw.Samples,
                result.ReflectionIndex,result.ReflectionFirstArrivalIndex,separation,rawBaseline,rawSigma);
            }
            if (!phase.OriginalObservations.ContainsKey(level)) phase.OriginalObservations[level] = new CalibrationTimingObservation {
                Length = phase.CableLengthMeters, PeakIncident = result.IncidentIndex, PeakReflection = result.ReflectionIndex,
                ArrivalIncident = result.IncidentFirstArrivalIndex, ArrivalReflection = result.ReflectionFirstArrivalIndex };
            int i = UseFirstArrivalTiming ? result.IncidentFirstArrivalIndex : result.IncidentIndex;
            int r = UseFirstArrivalTiming ? result.ReflectionFirstArrivalIndex : result.ReflectionIndex;
            if (i < 0 || r <= i) { phase.Markers.Remove(level); return; }
            double t0 = raw.X0Seconds + i * raw.DxSeconds, t1 = raw.X0Seconds + r * raw.DxSeconds;
            phase.Markers[level] = new MarkerData(t0, t1, t0 + 2 * phase.CableLengthMeters / (phase.VelocityMPerUs * 1e6),
                (t1 - t0) * phase.VelocityMPerUs * 1e6 / 2);
        }
        // Manual timing is intentionally not peak-snapped. The physical peak pair and its decay stay intact.
        public bool SetPhaseTimingCursors(int phaseIndex, string levelCode, double incidentSeconds, double reflectionSeconds)
        {
            ValidatePhaseIndex(phaseIndex); string level = NormalizeLevelCode(levelCode);
            if (!IsFinite(incidentSeconds) || !IsFinite(reflectionSeconds) || reflectionSeconds <= incidentSeconds) return false;
            lock (stateLock)
            {
                PhaseState phase = GetPhaseStateNoLock(phaseIndex); WaveformData raw; TdrAutoAnalysisResult a;
                if (!phase.Waveforms.TryGetValue(level, out raw) || !phase.Analyses.TryGetValue(level, out a) || a.SearchStatus != TdrSearchStatus.Ok) return false;
                int i = (int)Math.Round((incidentSeconds - raw.X0Seconds) / raw.DxSeconds), r = (int)Math.Round((reflectionSeconds - raw.X0Seconds) / raw.DxSeconds);
                if(i<0||r<=i||r>=raw.Samples.Length)return false;
                if (UseFirstArrivalTiming) { a.IncidentFirstArrivalIndex = i; a.ReflectionFirstArrivalIndex = r; }
                phase.Markers[level] = new MarkerData(incidentSeconds, reflectionSeconds,
                    incidentSeconds + 2 * phase.CableLengthMeters / (phase.VelocityMPerUs * 1e6),
                    (reflectionSeconds - incidentSeconds) * phase.VelocityMPerUs * 1e6 / 2);
                RememberManualReflectionNoLock(phase,level);
                phase.VelocityAdoptions.Remove(level); PublishLevelNoLock(phase, level);
                System.Threading.Interlocked.Increment(ref cursorChangedSequence);
            }
            Invalidate(); RaiseAnalysisAndCursorEvents(); return true;
        }
    }
    internal static class CalibrationArrivalMath
    {
        // An explicitly selected peak cannot borrow a quiet guard/onset from
        // another packet. Ambiguous, unresolved or truncated support is absent
        // evidence, not permission to time at the peak or at a crop boundary.
        internal static int FindSelectedPacket(double[] raw,int peak,int first,int last,int separation,double baseline,double sigma)
        {
            if(raw==null||peak<1||peak>=raw.Length-1||!Finite(baseline)||!Finite(sigma)||sigma<0)return -1;
            int span=Math.Max(12,Math.Min(96,separation/6));
            first=Math.Max(first,peak-span);last=Math.Min(last,peak+span);
            if(first<0||last>=raw.Length||first>=peak||last<=peak)return -1;
            for(int i=first;i<=last;i++)if(!Finite(raw[i]))return -1;
            int arrival=Find(raw,peak,first,last,separation,baseline,sigma);
            if(arrival>first&&arrival<peak)return arrival;
            // A preceding oscillation may supply the broad detector's guard.
            // Recover only the connected rise of this fixed selected peak; a
            // crop edge, isolated spike or another packet remains unconfirmed.
            arrival=LocalReflectionArrival(raw,peak,-1,separation,baseline,sigma);
            return arrival>first&&arrival<=peak-2?arrival:-1;
        }
        // A sustained earlier oscillation can bridge the quiet-window detector
        // to another packet. Retain the fixed selected calibration peak and
        // locate its connected rising lobe on raw samples, including TDR.
        internal static int LocalReflectionArrival(double[] raw,int peak,int proposed,int separation,double baseline,double sigma)
        {
            int span=Math.Max(12,Math.Min(96,separation/6));
            if(raw==null||peak<1||peak>=raw.Length||!Finite(baseline)||!Finite(sigma)||sigma<0)return -1;
            if(proposed>=Math.Max(0,peak-span)&&proposed<peak)return proposed;
            double amplitude=Math.Abs(raw[peak]-baseline),low=Math.Max(3*sigma,.02*amplitude);
            if(amplitude<=Math.Max(6*sigma,low)||!Finite(amplitude))return -1;
            int sign=Math.Sign(raw[peak]-baseline),first=Math.Max(0,peak-span),valleyIndex=peak;
            double valley=amplitude;
            for(int i=peak-1;i>=first;i--) {
                double value=(raw[i]-baseline)*sign;if(!Finite(value))return -1;
                if(value<low)return i+1;
                if(value>valley+Math.Max(3*sigma,low)) {
                    for(int k=valleyIndex+1;k<=peak;k++)if((raw[k]-baseline)*sign>=valley+low)return k;
                    return -1;
                }
                if(value<valley){valley=value;valleyIndex=i;}
            }
            return -1; // No supported onset inside this packet; never use the crop edge.
        }
        internal static int Find(double[] raw, int peak, int first, int last, int roundTrip, double baseline, double sigma)
        {
            if (raw == null || peak < first || peak > last || first < 0 || last >= raw.Length || !Finite(baseline) || !Finite(sigma) || sigma < 0) return -1;
            return HVX.Task17.Location.PeakAnchoredLeadingEdgeMath.FindPacket(raw.Length, first, last, peak,
                Math.Max(1, roundTrip), sigma, index => raw[index] - baseline);
        }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
    }
}
