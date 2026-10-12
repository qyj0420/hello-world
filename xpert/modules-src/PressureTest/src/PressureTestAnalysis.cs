using System;
using System.Collections.Generic;

namespace HVX.Controls
{
    /// <summary>
    /// Signal-only implementation for the pressure-test control.  It has no UI
    /// or hardware dependencies and therefore remains deterministic and easy to
    /// exercise with captured waveform blocks.
    /// </summary>
    internal static class PressureTestAnalysis
    {
        private const double TwoPi = 2.0 * Math.PI;

        internal static PressureTestAnalysisResult Analyze(
            IReadOnlyList<double> voltageKv,
            IReadOnlyList<double> pdPc,
            double sampleRateHz,
            double inductanceH)
        {
            return Analyze(
                voltageKv,
                pdPc,
                sampleRateHz,
                inductanceH,
                PdPolarity.Positive,
                null);
        }

        internal static PressureTestAnalysisResult Analyze(
            IReadOnlyList<double> voltageKv,
            IReadOnlyList<double> pdPc,
            double sampleRateHz,
            double inductanceH,
            PdPolarity polarity)
        {
            return Analyze(voltageKv, pdPc, sampleRateHz, inductanceH, polarity, null);
        }

        internal static PressureTestAnalysisResult Analyze(
            IReadOnlyList<double> voltageKv,
            IReadOnlyList<double> pdPc,
            double sampleRateHz,
            double inductanceH,
            PdPolarity polarity,
            PressureTestAnalysisOptions options)
        {
            PressureTestAnalysisResult result = new PressureTestAnalysisResult();
            result.PartialDischargeSampleIndex = -1;
            options = options ?? new PressureTestAnalysisOptions();

            if (!IsFinite(sampleRateHz) || sampleRateHz <= 0.0)
            {
                result.Status |= PressureTestAnalysisStatus.InvalidSampleRate;
                return result;
            }

            AnalyzePartialDischarge(voltageKv, pdPc, sampleRateHz, polarity, options, result);

            if (voltageKv == null || voltageKv.Count < 8)
            {
                result.Status |= PressureTestAnalysisStatus.InsufficientVoltageSamples;
                if (!IsFinite(inductanceH) || inductanceH <= 0.0)
                {
                    result.Status |= PressureTestAnalysisStatus.InvalidInductance;
                }

                return result;
            }

            double[] detrended;
            if (!TryDetrend(voltageKv, result, out detrended))
            {
                result.Status |= PressureTestAnalysisStatus.InsufficientVoltageSamples;
                return result;
            }

            double minimumFrequency = options.MinimumFrequencyHz;
            if (!IsFinite(minimumFrequency) || minimumFrequency <= 0.0)
            {
                minimumFrequency = 1.0;
            }

            double maximumFrequency = options.MaximumFrequencyHz;
            double nyquistLimit = sampleRateHz * 0.45;
            if (!IsFinite(maximumFrequency) || maximumFrequency <= minimumFrequency)
            {
                maximumFrequency = nyquistLimit;
            }

            maximumFrequency = Math.Min(maximumFrequency, nyquistLimit);
            if (maximumFrequency <= minimumFrequency)
            {
                result.Status |= PressureTestAnalysisStatus.FrequencyOutOfRange;
                return result;
            }

            int smoothingSamples = ChooseSmoothingWidth(
                sampleRateHz,
                maximumFrequency,
                detrended.Length,
                options.SmoothingSamples);
            double[] working = SmoothSymmetric(detrended, smoothingSamples);

            double noise = EstimateDifferenceNoise(working);
            double scale = PercentileAbsolute(working, 0.90);
            if (!IsFinite(scale) || scale <= 0.0)
            {
                result.Status |= PressureTestAnalysisStatus.FrequencyNotFound;
                return result;
            }

            double hysteresis = Math.Max(scale * 0.02, noise * 3.0);
            if (!IsFinite(hysteresis) || hysteresis <= 0.0)
            {
                hysteresis = scale * 0.01;
            }

            List<double> risingCrossings = FindRisingCrossings(working, hysteresis);
            double periodSeconds;
            double frequencyConfidence;
            if (!TryEstimatePeriod(
                risingCrossings,
                sampleRateHz,
                minimumFrequency,
                maximumFrequency,
                Math.Max(2, options.MinimumUsableCycles),
                out periodSeconds,
                out frequencyConfidence))
            {
                result.Status |= PressureTestAnalysisStatus.FrequencyNotFound;
                result.Status |= PressureTestAnalysisStatus.InsufficientCycles;
                return result;
            }

            double dampedFrequency = 1.0 / periodSeconds;
            if (!IsFinite(dampedFrequency) ||
                dampedFrequency < minimumFrequency ||
                dampedFrequency > maximumFrequency)
            {
                result.Status |= PressureTestAnalysisStatus.FrequencyOutOfRange;
                return result;
            }

            result.OscillationFrequencyHz = dampedFrequency;
            result.FrequencyConfidence = frequencyConfidence;
            result.Validity |= PressureTestMetricValidity.Frequency;

            double alpha;
            double fitRSquared;
            bool hasReliableDamping = TryEstimateDamping(
                working,
                risingCrossings,
                periodSeconds * sampleRateHz,
                sampleRateHz,
                Math.Max(hysteresis, noise * 3.0),
                options.MinimumEnvelopeFitRSquared,
                out alpha,
                out fitRSquared);

            result.EnvelopeFitRSquared = fitRSquared;
            if (IsFinite(alpha))
            {
                result.DampingCoefficientPerSecond = alpha;
            }

            if (!hasReliableDamping)
            {
                if (IsFinite(alpha) && alpha <= 0.0)
                {
                    result.Status |= PressureTestAnalysisStatus.NonDecayingEnvelope;
                }
                else
                {
                    result.Status |= PressureTestAnalysisStatus.EnvelopeFitPoor;
                }
            }

            double dampedAngularFrequency = TwoPi * dampedFrequency;
            double naturalAngularFrequency;
            if (hasReliableDamping)
            {
                naturalAngularFrequency = Math.Sqrt(
                    (dampedAngularFrequency * dampedAngularFrequency) + (alpha * alpha));
            }
            else
            {
                naturalAngularFrequency = dampedAngularFrequency;
                result.Status |= PressureTestAnalysisStatus.DampingCorrectionSkipped;
            }

            result.NaturalFrequencyHz = naturalAngularFrequency / TwoPi;

            if (IsFinite(inductanceH) && inductanceH > 0.0)
            {
                double capacitanceF = 1.0 /
                    (naturalAngularFrequency * naturalAngularFrequency * inductanceH);
                double capacitanceUf = capacitanceF * 1000000.0;
                if (IsFinite(capacitanceUf) && capacitanceUf >= 0.0)
                {
                    result.EquivalentCapacitanceUf = capacitanceUf;
                    result.Validity |= PressureTestMetricValidity.EquivalentCapacitance;
                }
            }
            else
            {
                result.Status |= PressureTestAnalysisStatus.InvalidInductance;
            }

            if (hasReliableDamping && !options.SkipDissipationFactor)
            {
                double rawLossPercent = 100.0 * 2.0 * alpha / naturalAngularFrequency;
                if (IsFinite(rawLossPercent) && rawLossPercent >= 0.0)
                {
                    result.RawDissipationFactorPercent = rawLossPercent;
                    if (IsFinite(options.SystemLossPercent) && options.SystemLossPercent >= 0.0)
                    {
                        double corrected = rawLossPercent - options.SystemLossPercent;
                        if (corrected < 0.0)
                        {
                            corrected = 0.0;
                            result.Status |= PressureTestAnalysisStatus.SystemLossExceedsMeasuredLoss;
                        }

                        result.DissipationFactorPercent = corrected;
                    }
                    else
                    {
                        result.DissipationFactorPercent = rawLossPercent;
                        result.Status |= PressureTestAnalysisStatus.SystemLossCalibrationMissing;
                    }

                    result.Validity |= PressureTestMetricValidity.DissipationFactor;
                }
            }

            CorrelatePartialDischargePhase(
                voltageKv,
                sampleRateHz,
                options.PdChannelDelaySamples,
                risingCrossings,
                dampedFrequency,
                result);

            return result;
        }

        private static void AnalyzePartialDischarge(
            IReadOnlyList<double> voltageKv,
            IReadOnlyList<double> pdPc,
            double sampleRateHz,
            PdPolarity polarity,
            PressureTestAnalysisOptions options,
            PressureTestAnalysisResult result)
        {
            if (pdPc == null || pdPc.Count == 0)
            {
                result.Status |= PressureTestAnalysisStatus.InsufficientPartialDischargeSamples;
                return;
            }

            if (voltageKv != null && voltageKv.Count != pdPc.Count)
            {
                result.Status |= PressureTestAnalysisStatus.ChannelLengthMismatch;
            }

            List<double> finiteValues = new List<double>(pdPc.Count);
            int i;
            for (i = 0; i < pdPc.Count; i++)
            {
                if (IsFinite(pdPc[i]))
                {
                    finiteValues.Add(pdPc[i]);
                }
                else
                {
                    result.Status |= PressureTestAnalysisStatus.NonFiniteInputIgnored;
                }
            }

            if (finiteValues.Count == 0)
            {
                result.Status |= PressureTestAnalysisStatus.InsufficientPartialDischargeSamples;
                return;
            }

            double baseline = Median(finiteValues);
            double threshold = options.PdDetectionThresholdPc;
            if (!IsFinite(threshold) || threshold < 0.0)
            {
                threshold = 0.0;
            }

            int bestIndex = -1;
            double bestMagnitude = threshold;
            double bestSignedValue = 0.0;
            for (i = 0; i < pdPc.Count; i++)
            {
                double sample = pdPc[i];
                if (!IsFinite(sample))
                {
                    continue;
                }

                double signedValue = sample - baseline;
                double magnitude;
                if (polarity == PdPolarity.Negative)
                {
                    magnitude = -signedValue;
                }
                else if (polarity == PdPolarity.Both)
                {
                    magnitude = Math.Abs(signedValue);
                }
                else
                {
                    magnitude = signedValue;
                }

                if (magnitude > bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    bestSignedValue = signedValue;
                    bestIndex = i;
                }
            }

            result.Validity |= PressureTestMetricValidity.PartialDischarge;
            if (bestIndex < 0)
            {
                result.PartialDischargeMaximumPc = 0.0;
                result.PartialDischargeSignedPc = 0.0;
                result.Status |= PressureTestAnalysisStatus.NoPartialDischargeEvent;
                return;
            }

            result.PartialDischargeMaximumPc = bestMagnitude;
            result.PartialDischargeSignedPc = bestSignedValue;
            result.PartialDischargeSampleIndex = bestIndex;
            result.PartialDischargeEventTimeSeconds =
                (bestIndex - options.PdChannelDelaySamples) / sampleRateHz;
        }

        private static void CorrelatePartialDischargePhase(
            IReadOnlyList<double> voltageKv,
            double sampleRateHz,
            int delaySamples,
            IList<double> risingCrossings,
            double frequencyHz,
            PressureTestAnalysisResult result)
        {
            if (result.PartialDischargeSampleIndex < 0 || voltageKv == null)
            {
                return;
            }

            int voltageIndex = result.PartialDischargeSampleIndex - delaySamples;
            if (voltageIndex < 0 || voltageIndex >= voltageKv.Count)
            {
                result.Status |= PressureTestAnalysisStatus.ChannelNotAligned;
                return;
            }

            if (IsFinite(voltageKv[voltageIndex]))
            {
                result.VoltageAtPartialDischargeKv = voltageKv[voltageIndex];
                result.Validity |= PressureTestMetricValidity.VoltageAtPartialDischarge;
            }

            if (risingCrossings == null || risingCrossings.Count == 0 || !IsFinite(frequencyHz))
            {
                return;
            }

            double cycles = (((double)voltageIndex - risingCrossings[0]) / sampleRateHz) * frequencyHz;
            result.PartialDischargePhaseDegrees = (cycles - Math.Floor(cycles)) * 360.0;
            result.Validity |= PressureTestMetricValidity.PartialDischargePhase;
        }

        private static bool TryDetrend(
            IReadOnlyList<double> input,
            PressureTestAnalysisResult result,
            out double[] detrended)
        {
            detrended = new double[input.Count];
            int count = 0;
            int i;
            for (i = 0; i < input.Count; i++)
            {
                if (IsFinite(input[i]))
                {
                    count++;
                }
                else
                {
                    result.Status |= PressureTestAnalysisStatus.NonFiniteInputIgnored;
                }
            }

            if (count < 8)
            {
                return false;
            }

            // A least-squares line is biased by an exponentially decaying
            // sinusoid and changes both its zero crossings and envelope. The
            // median removes acquisition offset without manufacturing decay.
            List<double> finiteValues = new List<double>(count);
            for (i = 0; i < input.Count; i++)
            {
                if (IsFinite(input[i]))
                {
                    finiteValues.Add(input[i]);
                }
            }

            double baseline = Median(finiteValues);
            for (i = 0; i < input.Count; i++)
            {
                detrended[i] = IsFinite(input[i])
                    ? input[i] - baseline
                    : 0.0;
            }

            return true;
        }

        private static int ChooseSmoothingWidth(
            double sampleRateHz,
            double maximumFrequencyHz,
            int sampleCount,
            int requestedWidth)
        {
            int width = requestedWidth;
            if (width <= 0)
            {
                width = (int)Math.Floor(sampleRateHz / maximumFrequencyHz / 8.0);
            }

            width = Math.Max(1, Math.Min(width, Math.Min(1001, sampleCount / 4)));
            if ((width & 1) == 0)
            {
                width++;
            }

            return Math.Min(width, sampleCount % 2 == 1 ? sampleCount : sampleCount - 1);
        }

        private static double[] SmoothSymmetric(double[] values, int width)
        {
            if (width <= 1)
            {
                return (double[])values.Clone();
            }

            double[] prefix = new double[values.Length + 1];
            int i;
            for (i = 0; i < values.Length; i++)
            {
                prefix[i + 1] = prefix[i] + values[i];
            }

            int radius = width / 2;
            double[] smoothed = new double[values.Length];
            for (i = 0; i < values.Length; i++)
            {
                int left = Math.Max(0, i - radius);
                int right = Math.Min(values.Length - 1, i + radius);
                smoothed[i] = (prefix[right + 1] - prefix[left]) / (right - left + 1);
            }

            return smoothed;
        }

        private static List<double> FindRisingCrossings(double[] values, double hysteresis)
        {
            List<double> crossings = new List<double>();
            bool armed = false;
            int i;
            for (i = 0; i < values.Length; i++)
            {
                if (values[i] <= -hysteresis)
                {
                    armed = true;
                }

                if (!armed || values[i] < hysteresis)
                {
                    continue;
                }

                int right = i;
                while (right > 0 && values[right - 1] > 0.0)
                {
                    right--;
                }

                if (right > 0)
                {
                    double y0 = values[right - 1];
                    double y1 = values[right];
                    double denominator = y1 - y0;
                    double fraction = Math.Abs(denominator) > double.Epsilon
                        ? -y0 / denominator
                        : 0.0;
                    crossings.Add((right - 1) + fraction);
                }

                armed = false;
            }

            return crossings;
        }

        private static bool TryEstimatePeriod(
            IList<double> crossings,
            double sampleRateHz,
            double minimumFrequencyHz,
            double maximumFrequencyHz,
            int minimumCycles,
            out double periodSeconds,
            out double confidence)
        {
            periodSeconds = double.NaN;
            confidence = 0.0;
            if (crossings == null || crossings.Count < minimumCycles + 1)
            {
                return false;
            }

            double minimumPeriodSamples = sampleRateHz / maximumFrequencyHz;
            double maximumPeriodSamples = sampleRateHz / minimumFrequencyHz;
            List<double> periods = new List<double>();
            int i;
            for (i = 1; i < crossings.Count; i++)
            {
                double period = crossings[i] - crossings[i - 1];
                if (period >= minimumPeriodSamples && period <= maximumPeriodSamples)
                {
                    periods.Add(period);
                }
            }

            if (periods.Count < minimumCycles)
            {
                return false;
            }

            double median = Median(periods);
            List<double> deviations = new List<double>(periods.Count);
            for (i = 0; i < periods.Count; i++)
            {
                deviations.Add(Math.Abs(periods[i] - median));
            }

            double mad = Median(deviations);
            double tolerance = Math.Max(median * 0.20, mad * 3.0);
            double sum = 0.0;
            double sumSquares = 0.0;
            int accepted = 0;
            for (i = 0; i < periods.Count; i++)
            {
                if (Math.Abs(periods[i] - median) <= tolerance)
                {
                    sum += periods[i];
                    sumSquares += periods[i] * periods[i];
                    accepted++;
                }
            }

            if (accepted < minimumCycles)
            {
                return false;
            }

            double mean = sum / accepted;
            double variance = Math.Max(0.0, (sumSquares / accepted) - (mean * mean));
            double coefficientOfVariation = mean > 0.0 ? Math.Sqrt(variance) / mean : 1.0;
            confidence = Math.Max(0.0, Math.Min(1.0, 1.0 - (5.0 * coefficientOfVariation)));
            periodSeconds = mean / sampleRateHz;
            return IsFinite(periodSeconds) && periodSeconds > 0.0;
        }

        private static bool TryEstimateDamping(
            double[] values,
            IList<double> crossings,
            double periodSamples,
            double sampleRateHz,
            double minimumAmplitude,
            double minimumRSquared,
            out double alpha,
            out double rSquared)
        {
            alpha = double.NaN;
            rSquared = double.NaN;
            List<double> times = new List<double>();
            List<double> logAmplitudes = new List<double>();
            int cycle;
            for (cycle = 0; cycle + 1 < crossings.Count; cycle++)
            {
                double span = crossings[cycle + 1] - crossings[cycle];
                if (Math.Abs(span - periodSamples) > periodSamples * 0.25)
                {
                    continue;
                }

                int start = Math.Max(0, (int)Math.Floor(crossings[cycle]));
                int end = Math.Min(values.Length - 1, (int)Math.Ceiling(crossings[cycle + 1]));
                double maximum = double.NegativeInfinity;
                int maximumIndex = -1;
                int i;
                for (i = start; i <= end; i++)
                {
                    if (values[i] > maximum)
                    {
                        maximum = values[i];
                        maximumIndex = i;
                    }
                }

                if (maximumIndex >= 0 && maximum > minimumAmplitude)
                {
                    times.Add(maximumIndex / sampleRateHz);
                    logAmplitudes.Add(Math.Log(maximum));
                }
            }

            if (times.Count < 3)
            {
                return false;
            }

            double intercept;
            double slope;
            if (!TryRobustLineFit(times, logAmplitudes, out intercept, out slope, out rSquared))
            {
                return false;
            }

            alpha = -slope;
            return IsFinite(alpha) && alpha > 0.0 &&
                IsFinite(rSquared) && rSquared >= minimumRSquared;
        }

        private static bool TryRobustLineFit(
            IList<double> x,
            IList<double> y,
            out double intercept,
            out double slope,
            out double rSquared)
        {
            intercept = double.NaN;
            slope = double.NaN;
            rSquared = double.NaN;
            if (!TryLineFit(x, y, null, out intercept, out slope, out rSquared))
            {
                return false;
            }

            List<double> residuals = new List<double>(x.Count);
            int i;
            for (i = 0; i < x.Count; i++)
            {
                residuals.Add(Math.Abs(y[i] - (intercept + (slope * x[i]))));
            }

            double medianResidual = Median(residuals);
            double cutoff = Math.Max(1e-12, medianResidual * 4.4478);
            bool[] include = new bool[x.Count];
            int included = 0;
            for (i = 0; i < x.Count; i++)
            {
                include[i] = residuals[i] <= cutoff;
                if (include[i])
                {
                    included++;
                }
            }

            return included >= 3 && TryLineFit(x, y, include, out intercept, out slope, out rSquared);
        }

        private static bool TryLineFit(
            IList<double> x,
            IList<double> y,
            bool[] include,
            out double intercept,
            out double slope,
            out double rSquared)
        {
            intercept = double.NaN;
            slope = double.NaN;
            rSquared = double.NaN;
            int count = 0;
            double sumX = 0.0;
            double sumY = 0.0;
            int i;
            for (i = 0; i < x.Count; i++)
            {
                if (include != null && !include[i])
                {
                    continue;
                }

                sumX += x[i];
                sumY += y[i];
                count++;
            }

            if (count < 2)
            {
                return false;
            }

            double meanX = sumX / count;
            double meanY = sumY / count;
            double sxx = 0.0;
            double sxy = 0.0;
            for (i = 0; i < x.Count; i++)
            {
                if (include != null && !include[i])
                {
                    continue;
                }

                double dx = x[i] - meanX;
                sxx += dx * dx;
                sxy += dx * (y[i] - meanY);
            }

            if (sxx <= 0.0)
            {
                return false;
            }

            slope = sxy / sxx;
            intercept = meanY - (slope * meanX);
            double residualSquares = 0.0;
            double totalSquares = 0.0;
            for (i = 0; i < x.Count; i++)
            {
                if (include != null && !include[i])
                {
                    continue;
                }

                double residual = y[i] - (intercept + (slope * x[i]));
                residualSquares += residual * residual;
                double centered = y[i] - meanY;
                totalSquares += centered * centered;
            }

            rSquared = totalSquares > 0.0
                ? Math.Max(0.0, 1.0 - (residualSquares / totalSquares))
                : 1.0;
            return IsFinite(slope) && IsFinite(intercept);
        }

        private static double EstimateDifferenceNoise(double[] values)
        {
            if (values.Length < 3)
            {
                return 0.0;
            }

            List<double> differences = new List<double>(values.Length - 1);
            int i;
            for (i = 1; i < values.Length; i++)
            {
                differences.Add(values[i] - values[i - 1]);
            }

            double center = Median(differences);
            for (i = 0; i < differences.Count; i++)
            {
                differences[i] = Math.Abs(differences[i] - center);
            }

            return Median(differences) * 1.4826 / Math.Sqrt(2.0);
        }

        private static double PercentileAbsolute(double[] values, double percentile)
        {
            double[] absolute = new double[values.Length];
            int i;
            for (i = 0; i < values.Length; i++)
            {
                absolute[i] = Math.Abs(values[i]);
            }

            Array.Sort(absolute);
            int index = (int)Math.Round((absolute.Length - 1) * percentile);
            index = Math.Max(0, Math.Min(absolute.Length - 1, index));
            return absolute[index];
        }

        private static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0)
            {
                return double.NaN;
            }

            double[] sorted = new double[values.Count];
            int i;
            for (i = 0; i < values.Count; i++)
            {
                sorted[i] = values[i];
            }

            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return (sorted.Length & 1) == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
