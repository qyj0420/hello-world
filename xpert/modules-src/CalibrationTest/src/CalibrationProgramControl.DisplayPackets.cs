using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        sealed class DisplayPacketCache
        {
            internal WaveformData Raw, Display;
            internal TdrAutoAnalysisResult Source, Decay;
            internal double Incident, Reflection;
            internal string Status;
        }
        readonly Dictionary<string, DisplayPacketCache> displayPacketCache = new Dictionary<string, DisplayPacketCache>();
        [Browsable(false)] public string DisplayDecayStatus { get; private set; } = "Unavailable";

        TdrAutoAnalysisResult DisplayDecayNoLock(PhaseState phase, string level, WaveformData active,
            MarkerData marker, TdrAutoAnalysisResult analysis)
        {
            if (level == "TDR" || marker == null || analysis == null || analysis.SearchStatus != TdrSearchStatus.Ok) {
                DisplayDecayStatus = "Unavailable"; return null;
            }
            WaveformData raw;
            if (!phase.Waveforms.TryGetValue(level, out raw)) { DisplayDecayStatus = "UnknownPacket"; return null; }
            int phaseIndex = Object.ReferenceEquals(phase, allPhase) ? -1 : Array.IndexOf(phases, phase);
            string key = LiveKey(phaseIndex, level); DisplayPacketCache cached;
            if (displayPacketCache.TryGetValue(key, out cached) && Object.ReferenceEquals(cached.Raw, raw) &&
                Object.ReferenceEquals(cached.Display, active) && Object.ReferenceEquals(cached.Source, analysis) &&
                cached.Incident == marker.StartTimeSeconds && cached.Reflection == marker.DistanceMarkerSeconds) {
                DisplayDecayStatus = cached.Status; return cached.Decay;
            }
            // Timing cursors measure travel, not amplitude/polarity. Always
            // retain the chosen physical peak pair; the first smaller bipolar
            // precursor after an arrival must not replace either selected peak.
            int incident = analysis.IncidentIndex, reflection = analysis.ReflectionIndex;
            int width = Math.Max(12, Math.Min(96, (reflection - incident) / 6));
            int incidentSign = incident < 0 || !IsFinite(raw.Samples[incident]) ? 0 : Math.Sign(raw.Samples[incident] - analysis.Baseline);
            int reflectionSign = reflection < 0 || !IsFinite(raw.Samples[reflection]) ? 0 : Math.Sign(raw.Samples[reflection] - analysis.Baseline);
            incident = ConnectedDisplayLobe(active.Samples, incident, width, analysis.Baseline, analysis.NoiseSigma, incidentSign);
            reflection = ConnectedDisplayLobe(active.Samples, reflection, width, analysis.Baseline, analysis.NoiseSigma, reflectionSign);
            TdrAutoAnalysisResult decay = null; string status = "UnknownPacket";
            if (incident >= 0 && reflection > incident) {
                decay = BuildManualResult(active, phase, incident, reflection, analysis);
                status = decay.IncidentPolarity != decay.ReflectionPolarity ? "OppositePolarity" :
                    decay.ReflectionMagnitude >= decay.IncidentMagnitude || decay.DecayCurveMagnitude.Length < 2 ? "NonDecaying" : "Ready";
                if (status != "Ready") decay = null;
            }
            displayPacketCache[key] = new DisplayPacketCache { Raw = raw, Display = active, Source = analysis,
                Incident = marker.StartTimeSeconds, Reflection = marker.DistanceMarkerSeconds, Decay = decay, Status = status };
            DisplayDecayStatus = status; return decay;
        }

        // Associate with the FIRST resolved lobe after the real travel cursor,
        // not the largest later absolute peak in a bipolar/ringing packet.
        internal static int FirstResolvedLobe(double[] values, int arrival, int limit, int width, double baseline, double sigma)
        {
            if (values == null || arrival < 0 || arrival >= values.Length || limit <= arrival || !IsFinite(baseline)) return -1;
            sigma = IsFinite(sigma) ? Math.Max(0, sigma) : 0;
            int last = Math.Min(values.Length - 1, Math.Min(limit - 1, arrival + width));
            double threshold = Math.Max(1e-12, 6 * sigma);
            for (int i = arrival; i <= last; i++) {
                double value = values[i] - baseline;
                if (!IsFinite(value)) return -1;
                if (Math.Abs(value) <= threshold) continue;
                int begin = i, peak = i, sign = Math.Sign(value), count = 1; double maximum = Math.Abs(value);
                while (i + 1 <= last) {
                    double next = (values[i + 1] - baseline) * sign;
                    if (!IsFinite(next)) return -1;
                    if (next <= threshold) break;
                    i++; count++; if (next > maximum) { maximum = next; peak = i; }
                }
                if (count >= 3 && peak > begin && peak < i && i < last) return peak;
                if (count < 3 && i < last) continue; // A sub-resolution precursor is not a resolved lobe.
                return -1; // An unresolved first lobe is not replaced by a later stronger lobe.
            }
            return -1;
        }

        // Low-pass changes the display copy only. Its peak must remain on the
        // connected signed lobe containing the selected raw packet anchor.
        internal static int ConnectedDisplayLobe(double[] values, int anchor, int width, double baseline, double sigma, int rawPolarity)
        {
            if (values == null || anchor < 1 || anchor >= values.Length - 1 || !IsFinite(baseline)) return -1;
            double value = values[anchor] - baseline;
            double floor = Math.Max(1e-12, IsFinite(sigma) ? 3 * Math.Max(0, sigma) : 0);
            if (!IsFinite(value) || Math.Abs(value) <= floor) return -1;
            int sign = Math.Sign(value), first = anchor, last = anchor;
            if (rawPolarity == 0 || sign != rawPolarity) return -1;
            while (first > Math.Max(0, anchor - width) && (values[first - 1] - baseline) * sign > floor) first--;
            while (last < Math.Min(values.Length - 1, anchor + width) && (values[last + 1] - baseline) * sign > floor) last++;
            if (first == 0 || last == values.Length - 1 || last - first < 2 ||
                (first == anchor - width && (values[first - 1] - baseline) * sign > floor) ||
                (last == anchor + width && (values[last + 1] - baseline) * sign > floor)) return -1;
            int best = anchor;
            for (int i = first; i <= last; i++) {
                if (!IsFinite(values[i])) return -1;
                if ((values[i] - baseline) * sign > (values[best] - baseline) * sign) best = i;
            }
            return best > first && best < last ? best : -1;
        }

        [Browsable(false)] public double[] GetDisplayDecayAnchors(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            lock (stateLock) {
                string level = NormalizeLevelCode(levelCode); var phase = GetPhaseStateNoLock(phaseIndex);
                WaveformData raw; MarkerData marker; TdrAutoAnalysisResult analysis;
                if (!phase.Waveforms.TryGetValue(level, out raw) || !phase.Markers.TryGetValue(level, out marker) ||
                    !phase.Analyses.TryGetValue(level, out analysis)) { DisplayDecayStatus="Unavailable";return new double[0]; }
                var decay = DisplayDecayNoLock(phase, level, ActiveWaveformNoLock(phase, level, raw), marker, analysis);
                return decay == null ? new double[0] : new[] { decay.DecayCurveTimeSeconds[0], decay.IncidentValue,
                    decay.DecayCurveTimeSeconds[decay.DecayCurveTimeSeconds.Length - 1], decay.ReflectionValue };
            }
        }
    }
}
