using System;
using System.ComponentModel;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        int positioningMode = 1;

        [Category("Interaction"), DefaultValue(1)]
        [Description("计时模式：0=首达；1=波峰。人工游标保持所选采样位置。Timing mode: 0=first arrival; 1=peak. Manual cursors retain the selected sample.")]
        public int PositioningMode
        {
            get { return positioningMode; }
            set
            {
                if (value < 0 || value > 1 || positioningMode == value) return;
                positioningMode = value;
                alignmentIncident = -1; projectedAlignmentReady = false;
                AlignCalibration(); RecomputeFilteredCurves();
                Invalidate();
            }
        }

        public void SetPositioningMode(int mode) { PositioningMode = mode; }

        double[] ActivePositioningSamples()
        {
            return !Visible(0, true) && Visible(3, false) ? filteredPulse : pulse;
        }

        int SnapIncidentFive(int targetIndex)
        {
            int target = Limit(targetIndex, 0, Math.Max(0, pulse.Length - 1));
            if (positioningMode == 0 || pulse.Length == 0 || (!Visible(0, true) && !Visible(3, false))) return target;
            double[] samples = ActivePositioningSamples();
            if (samples.Length == 0) return target;
            target = Limit(target, 0, samples.Length - 1);
            int first = Math.Max(0, target - 2), last = (int)Math.Min(samples.Length - 1L, target + 2L);
            int best = target;
            double largest = IsFinite(samples[target]) ? Math.Abs(samples[target]) : double.NegativeInfinity;
            for (int i = first; i <= last; i++)
            {
                if (!IsFinite(samples[i])) continue;
                double magnitude = Math.Abs(samples[i]);
                if (magnitude > largest || (magnitude == largest && Math.Abs(i - target) < Math.Abs(best - target)))
                {
                    best = i;
                    largest = magnitude;
                }
            }
            return best;
        }

        int SnapReflectionForMode(int targetIndex)
        {
            int target = ConstrainReflectionSelection(targetIndex);
            if(target<0)return -1;
            if(positioningMode == 0) return FirstArrivalAtRelease(target);
            if (pulse.Length == 0 || (!Visible(0, true) && !Visible(3, false))) return target;
            double[] samples = ActivePositioningSamples();
            if (samples.Length < 3) return target;
            target = Limit(target, 0, samples.Length - 1);
            bool findPeak = IncidentIsPeak(samples);
            int radius = (int)Math.Min(samples.Length - 1L, Math.Max(5L, (long)n1 + n2));
            int first = (int)Math.Max(1L, (long)target - radius);
            int last = (int)Math.Min(samples.Length - 2L, (long)target + radius);
            int allowedFirst,allowedLast;
            if(!TryReflectionSelectionBounds(out allowedFirst,out allowedLast))return -1;
            first=Math.Max(first,allowedFirst);last=Math.Min(last,allowedLast);
            // Method 7 must not snap to a tiny noise extremum beside the real
            // pulse. Use the same raw signal floor as its guarded measurement.
            double noise=Safe(originalParameterRow,28,double.NaN),baseline=0;
            bool rejectNoise=IsSeventhRow&&IsFinite(noise)&&noise>0&&dtNs>0;
            if(rejectNoise){baseline=SeventhFixedBand(pulse,1e9/dtNs,false).Baseline;rejectNoise=IsFinite(baseline);}
            int best = -1, nearest = int.MaxValue;
            for (int i = first; i <= last; )
            {
                double value = samples[i];
                if (!IsFinite(value)) { i++; continue; }
                int plateauFirst, plateauLast;
                EqualAmplitudeBounds(samples, i, out plateauFirst, out plateauLast);
                i = plateauLast + 1;
                if (plateauFirst == 0 || plateauLast == samples.Length - 1) continue;
                double left = samples[plateauFirst - 1], right = samples[plateauLast + 1];
                if (!IsFinite(left) || !IsFinite(right)) continue;
                bool matches = findPeak
                    ? value > left && value > right
                    : value < left && value < right;
                if (!matches) continue;
                if(rejectNoise&&(findPeak?value-baseline:baseline-value)<4.5*noise)continue;
                int candidate = Limit(target, Math.Max(first, plateauFirst), Math.Min(last, plateauLast));
                if(rejectNoise)
                {
                    int begin,end,middle=incident+(candidate-incident)/2;
                    var measured=MeasureManualSeventh(seventhPulseBand,candidate,Math.Max(0,middle+1),pulse.Length-1,noise,out begin,out end);
                    if(!measured.Valid||measured.Main<4.5*measured.Noise||measured.Sign!=(findPeak?1:-1)||
                        measured.MainPeak<first||measured.MainPeak>last||Math.Abs(measured.MainPeak-candidate)>2)continue;
                    candidate=measured.MainPeak;
                }
                int distance = Math.Abs(candidate - target);
                if (best < 0 || distance < nearest || (distance == nearest && (findPeak ? value > samples[best] : value < samples[best])))
                {
                    best = candidate;
                    nearest = distance;
                }
            }
            // If no matching local extremum exists, keep the user's release point.
            return best >= 0 ? best : target;
        }

        bool IncidentIsPeak(double[] samples)
        {
            int index = Limit(incident, 0, Math.Max(0, samples.Length - 1));
            if (samples.Length == 0) return true;
            double value = samples[index];
            int plateauFirst, plateauLast;
            EqualAmplitudeBounds(samples, index, out plateauFirst, out plateauLast);
            bool hasLeft = plateauFirst > 0 && IsFinite(samples[plateauFirst - 1]);
            bool hasRight = plateauLast + 1 < samples.Length && IsFinite(samples[plateauLast + 1]);
            if (IsFinite(value) && (hasLeft || hasRight))
            {
                bool peak = (!hasLeft || value > samples[plateauFirst - 1]) && (!hasRight || value > samples[plateauLast + 1]);
                bool valley = (!hasLeft || value < samples[plateauFirst - 1]) && (!hasRight || value < samples[plateauLast + 1]);
                if (peak) return true;
                if (valley) return false;
            }
            // A manually supplied incident point may lie on a slope or flat region.
            // Its sign is only a fallback when the local shape is not an extremum.
            return !IsFinite(value) || value >= 0;
        }

        void EqualAmplitudeBounds(double[] samples, int index, out int first, out int last)
        {
            first = last = index;
            double value = samples[index];
            while (first > 0 && samples[first - 1] == value) first--;
            while (last + 1 < samples.Length && samples[last + 1] == value) last++;
        }

        void PreviewReflectionAtIndex(int targetIndex)
        {
            int target=ConstrainReflectionSelection(targetIndex);
            if(target<0)return;
            if(target==reflection&&reflectionSelected)return;
            SuppressPositionApproximationForExactEdit();
            reflection = target;
            reflectionSelected=true;
            AlignCalibration();
            RecomputeFilteredCurves();
            UpdateDynamicAnalysis();
            Invalidate();
            NotifyReviewAnalysisEdited("ReflectionPreview",true);
        }
    }
}
