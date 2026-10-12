using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using HVX.Controls;

namespace CalibrationWaveform
{
    public sealed partial class CalibrationWaveformControl
    {
        private double[] decayCachedX, decayCachedY, decayCachedAnchors;
        private double decayCachedIncident = double.NaN, decayCachedReflection = double.NaN;
        private bool decayCacheReady;
        private double[] decayReferenceX, decayReferenceY;
        private double decayReferenceIncident, decayReferenceReflection;
        private double decayReferenceIncidentCursor, decayReferenceReflectionCursor;
        private bool decayUseArchivedPeaks;

        // A formal CAL reader has already validated these RAW packet identities.
        // They anchor only the display copy; travel-time cursors are never moved.
        // Bind to this exact waveform and timing, not a later selection or edit.
        public void SetDecayPulsePeakReferences(double incidentIndex, double reflectionIndex)
        {
            lock (dataLock)
            {
                decayUseArchivedPeaks = true;
                decayReferenceX = xs; decayReferenceY = ys;
                decayReferenceIncident = incidentIndex; decayReferenceReflection = reflectionIndex;
                decayReferenceIncidentCursor = incidentX; decayReferenceReflectionCursor = reflectionX;
                decayCacheReady = false;
            }
            SafeInvalidate();
        }

        public void ClearDecayPulsePeakReferences()
        {
            lock (dataLock)
            {
                decayUseArchivedPeaks = false; decayReferenceX = decayReferenceY = null;
                decayCacheReady = false;
            }
            SafeInvalidate();
        }

        private sealed class DecayPulsePeak
        {
            internal int Index;
            internal double Baseline;
            internal double Magnitude;
            internal int Polarity;
            internal double Distance;
        }

        // The time cursors may deliberately identify the FIRST ARRIVAL. They
        // continue to define TimeDifference, CalculatedDistance and persisted
        // cursor positions. The drawing has its own nearby pulse-peak anchors.
        private double[] DecayAnchors()
        {
            lock (dataLock)
            {
                if (decayCacheReady && ReferenceEquals(decayCachedX, xs) && ReferenceEquals(decayCachedY, ys) &&
                    decayCachedIncident == incidentX && decayCachedReflection == reflectionX) return decayCachedAnchors;
                decayCachedX = xs; decayCachedY = ys;
                decayCachedIncident = incidentX; decayCachedReflection = reflectionX;
                decayCacheReady = true;
                return decayCachedAnchors = FindDecayAnchors();
            }
        }

        private double[] FindDecayAnchors()
        {
            if (xs.Length < 12 || xs.Length != ys.Length || !IsFinite(incidentX) || !IsFinite(reflectionX) ||
                reflectionX <= incidentX || incidentX < xs[0] || reflectionX > xs[xs.Length - 1]) return null;
            // Binary searches and interpolation require strictly ordered times.
            // Reject malformed samples instead of inventing an attenuation curve.
            for (int i = 0; i < xs.Length; i++)
                if (!IsFinite(xs[i]) || (i > 0 && xs[i] <= xs[i - 1])) return null;

            // Units are microseconds. Windows are independent of zoom, bounded
            // by the cursor separation, and cannot include the other pulse.
            double radius = Math.Min(0.5, (reflectionX - incidentX) * 0.25);
            DecayPulsePeak incident, reflection;
            if (decayUseArchivedPeaks)
            {
                if (!ReferenceEquals(decayReferenceX, xs) || !ReferenceEquals(decayReferenceY, ys) ||
                    decayReferenceIncidentCursor != incidentX || decayReferenceReflectionCursor != reflectionX ||
                    !IsFinite(decayReferenceIncident) || !IsFinite(decayReferenceReflection) ||
                    decayReferenceIncident != Math.Truncate(decayReferenceIncident) ||
                    decayReferenceReflection != Math.Truncate(decayReferenceReflection) ||
                    decayReferenceIncident < 1 || decayReferenceReflection >= xs.Length - 1 ||
                    decayReferenceReflection <= decayReferenceIncident) return null;
                int firstPeak = (int)decayReferenceIncident, secondPeak = (int)decayReferenceReflection;
                int width = Math.Max(12, Math.Min(96, (secondPeak - firstPeak) / 6));
                incident = FindReferencedDisplayLobe(incidentX, radius, firstPeak, width);
                reflection = FindReferencedDisplayLobe(reflectionX, radius, secondPeak, width);
            }
            else
            {
                // Historical records have no validated raw peak identity.
                incident = FindCursorPulsePeak(incidentX, radius);
                reflection = FindCursorPulsePeak(reflectionX, radius);
            }
            if (incident == null || reflection == null || reflection.Index <= incident.Index ||
                incident.Polarity != reflection.Polarity || reflection.Magnitude > incident.Magnitude) return null;

            // A signed exponential cannot join opposite-polarity peaks. Such a
            // trace is not falsely displayed as attenuation, nor is a zero/noise
            // endpoint replaced with an arbitrary small positive amplitude.
            return new[] { xs[incident.Index], ys[incident.Index], xs[reflection.Index], ys[reflection.Index],
                incident.Baseline, reflection.Baseline };
        }

        private DecayPulsePeak FindReferencedDisplayLobe(double cursor, double radius, int anchor, int width)
        {
            if (Math.Abs(xs[anchor] - cursor) > radius) return null;
            int first = Array.BinarySearch(xs, cursor - radius), last = Array.BinarySearch(xs, cursor + radius);
            if (first < 0) first = ~first; if (last < 0) last = ~last - 1;
            first = Math.Max(0, first); last = Math.Min(xs.Length - 1, last);
            if (last - first < 10 || anchor <= first || anchor >= last) return null;
            for (int i = first; i <= last; i++) if (!IsFinite(ys[i])) return null;
            int edges = Math.Max(3, (last - first + 1) / 5);
            var left = new List<double>(); var right = new List<double>();
            for (int i = 0; i < edges; i++) { left.Add(ys[first + i]); right.Add(ys[last - i]); }
            double leftBase = DecayMedian(left), rightBase = DecayMedian(right);
            double leftTime = xs[first + edges / 2], rightTime = xs[last - edges / 2];
            if (rightTime <= leftTime) return null;
            double slope = (rightBase - leftBase) / (rightTime - leftTime);
            var noise = new List<double>();
            for (int i = 0; i < edges; i++)
            {
                int a = first + i, b = last - i;
                noise.Add(Math.Abs(ys[a] - leftBase - slope * (xs[a] - leftTime)));
                noise.Add(Math.Abs(ys[b] - leftBase - slope * (xs[b] - leftTime)));
            }
            // Same connected signed-lobe rule as the calibration display. The
            // raw identity is supplied, so no nearest precursor/later peak search.
            double threshold = Math.Max(1e-12, 3 * 1.4826 * DecayMedian(noise));
            double atAnchor = ys[anchor] - leftBase - slope * (xs[anchor] - leftTime);
            if (Math.Abs(atAnchor) <= threshold) return null;
            int sign = Math.Sign(atAnchor), begin = anchor, end = anchor;
            int minimum = Math.Max(first, anchor - width), maximum = Math.Min(last, anchor + width);
            while (begin > minimum && (ys[begin - 1] - leftBase - slope * (xs[begin - 1] - leftTime)) * sign > threshold) begin--;
            while (end < maximum && (ys[end + 1] - leftBase - slope * (xs[end + 1] - leftTime)) * sign > threshold) end++;
            if (begin == minimum || end == maximum || end - begin < 2) return null;
            int peak = anchor; double magnitude = Math.Abs(atAnchor);
            for (int i = begin; i <= end; i++)
            {
                double value = (ys[i] - leftBase - slope * (xs[i] - leftTime)) * sign;
                if (value > magnitude) { magnitude = value; peak = i; }
            }
            if (peak <= begin || peak >= end) return null;
            double before = (ys[peak - 1] - leftBase - slope * (xs[peak - 1] - leftTime)) * sign;
            double after = (ys[peak + 1] - leftBase - slope * (xs[peak + 1] - leftTime)) * sign;
            if (Math.Min(before, after) < magnitude * .2) return null;
            return new DecayPulsePeak { Index = peak, Baseline = leftBase + slope * (xs[peak] - leftTime),
                Magnitude = magnitude, Polarity = sign, Distance = Math.Abs(xs[peak] - cursor) };
        }

        private DecayPulsePeak FindCursorPulsePeak(double cursor, double radius)
        {
            int first = Array.BinarySearch(xs, cursor - radius);
            if (first < 0) first = ~first;
            int last = Array.BinarySearch(xs, cursor + radius);
            if (last < 0) last = ~last - 1;
            first = Math.Max(0, first); last = Math.Min(xs.Length - 1, last);
            if (last - first < 10) return null;
            for (int i = first; i <= last; i++) if (!IsFinite(ys[i])) return null;

            // As in the calibration detector, use robust median/MAD statistics.
            // Quiet shoulders on BOTH sides estimate a local baseline including
            // slow drift; taking abs(raw sample) would mistake a DC offset for PD.
            int edgeCount = Math.Max(3, (last - first + 1) / 5);
            var left = new List<double>(); var right = new List<double>();
            for (int i = 0; i < edgeCount; i++) { left.Add(ys[first + i]); right.Add(ys[last - i]); }
            double leftBase = DecayMedian(left), rightBase = DecayMedian(right);
            double leftTime = xs[first + edgeCount / 2], rightTime = xs[last - edgeCount / 2];
            if (rightTime <= leftTime) return null;
            double slope = (rightBase - leftBase) / (rightTime - leftTime);
            var noise = new List<double>();
            for (int i = 0; i < edgeCount; i++)
            {
                int a = first + i, b = last - i;
                noise.Add(Math.Abs(ys[a] - leftBase - slope * (xs[a] - leftTime)));
                noise.Add(Math.Abs(ys[b] - leftBase - slope * (xs[b] - leftTime)));
            }
            double sigma = 1.4826 * DecayMedian(noise);
            double floor = Math.Max(1e-12, Math.Max(Math.Abs(leftBase), Math.Abs(rightBase)) * 1e-12);
            double threshold = Math.Max(floor, 6 * sigma);
            DecayPulsePeak best = null;
            bool ambiguous = false;
            for (int index = first; index <= last; index++)
            {
                double centered = ys[index] - leftBase - slope * (xs[index] - leftTime);
                if (Math.Abs(centered) <= threshold) continue;
                int begin = index, peak = index;
                int polarity = centered < 0 ? -1 : 1;
                double maximum = Math.Abs(centered);
                while (index + 1 <= last)
                {
                    double next = ys[index + 1] - leftBase - slope * (xs[index + 1] - leftTime);
                    if (Math.Abs(next) <= threshold || (next < 0 ? -1 : 1) != polarity) break;
                    index++;
                    if (Math.Abs(next) > maximum) { maximum = Math.Abs(next); peak = index; }
                }
                int end = index;
                // Require a resolved pulse with both shoulders, not an isolated
                // sample glitch, truncated waveform, or monotonic ramp.
                if (end - begin + 1 < 3 || begin <= first || end >= last || peak <= begin || peak >= end) continue;
                double beforePeak = Math.Abs(ys[peak - 1] - leftBase - slope * (xs[peak - 1] - leftTime));
                double afterPeak = Math.Abs(ys[peak + 1] - leftBase - slope * (xs[peak + 1] - leftTime));
                if (Math.Min(beforePeak, afterPeak) < maximum * 0.2) continue;
                double distance = cursor < xs[begin] ? xs[begin] - cursor : cursor > xs[end] ? cursor - xs[end] : 0;
                if (distance > radius * 0.35) continue;
                // Associate with the closest pulse, NOT the largest peak anywhere
                // in the waveform/window. A larger unrelated noise burst must not
                // replace the selected calibration pulse.
                if (best == null || distance < best.Distance - 1e-12)
                {
                    best = new DecayPulsePeak { Index = peak, Baseline = leftBase + slope * (xs[peak] - leftTime),
                        Magnitude = maximum, Polarity = polarity, Distance = distance };
                    ambiguous = false;
                }
                else if (Math.Abs(distance - best.Distance) <= 1e-12) ambiguous = true;
            }
            return ambiguous ? null : best;
        }

        private static double DecayMedian(List<double> values)
        {
            values.Sort();
            int half = values.Count / 2;
            return values.Count % 2 == 0 ? (values[half - 1] + values[half]) * 0.5 : values[half];
        }

        private static double DecayAt(double[] anchors, double x)
        {
            double fraction = (x - anchors[0]) / (anchors[2] - anchors[0]);
            if (fraction <= 0) return anchors[1];
            if (fraction >= 1) return anchors[3];
            double incident = anchors[1] - anchors[4], reflection = anchors[3] - anchors[5];
            double baseline = (1 - fraction) * anchors[4] + fraction * anchors[5];
            double magnitude = Math.Exp((1 - fraction) * Math.Log(Math.Abs(incident)) + fraction * Math.Log(Math.Abs(reflection)));
            return baseline + (incident < 0 ? -magnitude : magnitude);
        }

        private void DrawExponentialDecay(Graphics g, Rectangle plot, double yMin, double yMax)
        {
            double[] anchors = DecayAnchors();
            if (anchors == null) return;
            double start = Math.Max(anchors[0], viewXMin);
            double end = Math.Min(anchors[2], viewXMax);
            if (start >= end) return;
            int segments = Math.Max(2, Math.Min(512, (int)Math.Ceiling(Math.Abs(XToPixel(end, plot) - XToPixel(start, plot)))));
            var points = new PointF[segments + 1];
            for (int i = 0; i <= segments; i++)
            {
                double x = start + (end - start) * i / segments;
                points[i] = new PointF(XToPixel(x, plot), YToPixel(DecayAt(anchors, x), plot, yMin, yMax));
            }
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(plot);
                using (var pen = new Pen(BrowserTheme.PhaseA, 1.2f) { DashStyle = DashStyle.Dash })
                    g.DrawLines(pen, points);
                using (var brush = new SolidBrush(BrowserTheme.PhaseA))
                {
                    foreach (int index in new[] { 0, 2 })
                    {
                        if (anchors[index] < viewXMin || anchors[index] > viewXMax) continue;
                        float x = XToPixel(anchors[index], plot);
                        float y = YToPixel(anchors[index + 1], plot, yMin, yMax);
                        g.FillEllipse(brush, x - 2.5f, y - 2.5f, 5, 5);
                    }
                }
            }
            finally { g.Restore(state); }
        }
    }
}
