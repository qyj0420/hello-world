using System;
using System.ComponentModel;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        double[] filteredDampingSource;
        double filteredDampingIncidentAnchor = double.NaN, filteredDampingReflectionAnchor = double.NaN;
        double filteredDampingDtNs = double.NaN, filteredDampingSlopePerNs = double.NaN;
        bool filteredDampingCalibrationAvailable, hasFilteredDampingCurve;
        HVX.SignalEvidence.SixthCalibrationReference filteredDampingRawReference;
        bool filteredDampingRawQualified;
        int filteredDampingStart = -1, filteredDampingEnd = -1;
        string filteredDampingStatus = "FilteredCalibrationUnavailable";

        [Browsable(false)] public bool HasFilteredDampingCurve { get { return hasFilteredDampingCurve; } }
        [Browsable(false)] public int FilteredCalibrationIncidentPeakIndex { get { return filteredDampingStart; } }
        [Browsable(false)] public int FilteredCalibrationReflectionPeakIndex { get { return filteredDampingEnd; } }
        [Browsable(false)] public double FilteredDampingSlopePerNs { get { return filteredDampingSlopePerNs; } }
        [Browsable(false)] public string FilteredDampingStatusCode { get { return filteredDampingStatus; } }

        void RefitFilteredDamping()
        {
            HVX.SignalEvidence.SixthCalibrationReference rawReference;
            bool rawQualified=TryRawDampingReference(out rawReference);
            // CAL timing, not the draggable PD reflection, defines the project calibration interval.
            double incidentAnchor = incident;
            double reflectionAnchor = ProjectCalibrationEnabled ?
                selectedProjectCalibration == null || dtNs <= 0 ? double.NaN : incident + projectAlignedDurationNs / dtNs :
                incident + (originalParameterRow.Length>14&&IsFinite(originalParameterRow[14])&&originalParameterRow[14]>0?
                    originalParameterRow[14]:initialReflection-(double)initialIncident);
            if (ReferenceEquals(filteredDampingSource, filteredCalibration) &&
                filteredDampingIncidentAnchor.Equals(incidentAnchor) && filteredDampingReflectionAnchor.Equals(reflectionAnchor) &&
                filteredDampingDtNs.Equals(dtNs) && filteredDampingCalibrationAvailable == hasAlignedCalibrationCurve &&
                ReferenceEquals(filteredDampingRawReference,rawReference) && filteredDampingRawQualified==rawQualified) return;
            filteredDampingSource = filteredCalibration;
            filteredDampingIncidentAnchor = incidentAnchor; filteredDampingReflectionAnchor = reflectionAnchor;
            filteredDampingDtNs = dtNs; filteredDampingCalibrationAvailable = hasAlignedCalibrationCurve;
            filteredDampingRawReference=rawReference;filteredDampingRawQualified=rawQualified;
            filteredAttenuation = new double[pulse.Length];
            hasFilteredDampingCurve = false; filteredDampingStart = filteredDampingEnd = -1;
            filteredDampingSlopePerNs = double.NaN; filteredDampingStatus = "FilteredCalibrationUnavailable";
            if(!rawQualified){filteredDampingStatus="RawCalibrationReferenceUnavailable:"+rawDampingReferenceReason;return;}
            if (!hasAlignedCalibrationCurve || filteredCalibration.Length < 2 || !IsFinite(dtNs) || dtNs <= 0) return;
            if (!IsFinite(incidentAnchor) || !IsFinite(reflectionAnchor) || incidentAnchor < 0 || reflectionAnchor <= incidentAnchor)
            { filteredDampingStatus = "InvalidCalibrationPeakOrder"; return; }
            // Do not manufacture a reflection peak from ringing when the actual CAL reflection is cropped.
            if (reflectionAnchor > filteredCalibration.Length - 1 || incidentAnchor > filteredCalibration.Length - 1)
            { filteredDampingStatus = "CalibrationReflectionOutsideWaveform"; return; }
            double separation = reflectionAnchor - incidentAnchor;
            int radius = Math.Max(2, Math.Min(64, (int)Math.Min(64, separation / 8)));
            int midpoint = (int)Math.Floor(incidentAnchor + separation / 2);
            int firstStart = Math.Max(0, (int)Math.Floor(incidentAnchor) - radius), firstEnd = Math.Min(midpoint, (int)Math.Ceiling(incidentAnchor) + radius);
            int lastStart = Math.Max(midpoint + 1, (int)Math.Floor(reflectionAnchor) - radius), lastEnd = Math.Min(filteredCalibration.Length - 1, (int)Math.Ceiling(reflectionAnchor) + radius);
            // Legacy inputs have no CAL header. Require calibration evidence around both anchors;
            // an externally supplied decay or filter ringing alone must not invent a second pulse.
            if (FilteredCalibrationPeak(alignedCalibration, firstStart, firstEnd, incidentAnchor) < 0 ||
                FilteredCalibrationPeak(alignedCalibration, lastStart, lastEnd, reflectionAnchor) < 0)
            { filteredDampingStatus = "CalibrationPeaksUnavailable"; return; }
            int first = FilteredCalibrationPeak(filteredCalibration,
                firstStart, firstEnd, incidentAnchor);
            int last = FilteredCalibrationPeak(filteredCalibration,
                lastStart, lastEnd, reflectionAnchor);
            filteredDampingStatus = "FilteredCalibrationPeaksUnavailable";
            if (first < 0 || last <= first) return;
            double firstValue = filteredCalibration[first], lastMagnitude = Math.Abs(filteredCalibration[last]);
            double firstMagnitude = Math.Abs(firstValue);
            if (!IsFinite(firstMagnitude) || !IsFinite(lastMagnitude) || firstMagnitude <= 1e-12 || lastMagnitude <= 1e-12) return;
            double slope = (Math.Log(lastMagnitude) - Math.Log(firstMagnitude)) / (last - (double)first);
            // This is an attenuation envelope. Opposite-polarity reflections retain their magnitude,
            // as in the unfiltered CAL fit, while the envelope keeps the incident pulse's sign.
            if (!IsFinite(slope) || slope >= -1e-14)
            { filteredDampingStatus = "NonDecreasingFilteredCalibration"; return; }
            double sign = Math.Sign(firstValue);
            for (int i = first; i <= last; i++)
                filteredAttenuation[i] = sign * Math.Exp(Math.Log(firstMagnitude) + slope * (i - (double)first));
            // Preserve exact measured endpoint amplitudes rather than a log/exp round-trip.
            filteredAttenuation[first] = firstValue; filteredAttenuation[last] = sign * lastMagnitude;
            filteredDampingStart = first; filteredDampingEnd = last;
            filteredDampingSlopePerNs = slope / dtNs;
            hasFilteredDampingCurve = true; filteredDampingStatus = "Ready";
        }

        static int FilteredCalibrationPeak(double[] values, int start, int end, double anchor)
        {
            int best = -1;
            for (int i = start; i <= end; i++)
            {
                double amplitude = Math.Abs(values[i]);
                if (!IsFinite(amplitude) || amplitude <= 1e-12) continue;
                double left = i > 0 ? Math.Abs(values[i - 1]) : 0;
                double right = i + 1 < values.Length ? Math.Abs(values[i + 1]) : 0;
                if (amplitude < left || amplitude < right || (amplitude == left && amplitude == right)) continue;
                if (best < 0 || amplitude > Math.Abs(values[best]) ||
                    (amplitude == Math.Abs(values[best]) && Math.Abs(i - anchor) < Math.Abs(best - anchor))) best = i;
            }
            return best;
        }
    }
}
