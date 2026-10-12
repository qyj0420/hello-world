using System;
using System.Collections.Generic;

namespace HVX.Controls
{
    public enum TdrSearchStatus
    {
        Ok = 0,
        InvalidArgument = 1,
        TooFewSamples = 2,
        TooManyInvalidSamples = 3,
        IncidentNotFound = 4,
        PredictedReflectionOutOfRange = 5,
        ReflectionNotFound = 6
    }

    public enum TdrDecayFitStatus
    {
        Ok = 0,
        InsufficientPoints = 1,
        NonDecaying = 2,
        InvalidAmplitude = 3
    }

    public enum TdrDecayFitMode
    {
        EndpointConstrained = 0,
        RobustEnvelope = 1
    }

    /// <summary>Options for deterministic TDR incident/reflection location and decay fitting.</summary>
    public sealed class TdrAutoAnalysisOptions
    {
        public TdrAutoAnalysisOptions()
        {
            IncidentHintSeconds = Double.NaN;
            IncidentHintHalfWindowSeconds = Double.NaN;
            ReflectionToleranceFraction = 0.15;
            ReflectionMinHalfWindowSamples = 8;
            IncidentThresholdSigma = 6.0;
            ReflectionThresholdSigma = 4.0;
            MinimumReflectionRelativeAmplitude = 0.01;
            DetectionSmoothHalfWidth = 2;
            FitEnvelopeHalfWidth = 4;
            MaximumInvalidSampleFraction = 0.05;
            FitMode = TdrDecayFitMode.EndpointConstrained;
        }

        public double IncidentHintSeconds { get; set; }
        public double IncidentHintHalfWindowSeconds { get; set; }
        public double ReflectionToleranceFraction { get; set; }
        public int ReflectionMinHalfWindowSamples { get; set; }
        public double IncidentThresholdSigma { get; set; }
        public double ReflectionThresholdSigma { get; set; }
        public double MinimumReflectionRelativeAmplitude { get; set; }
        public int DetectionSmoothHalfWidth { get; set; }
        public int FitEnvelopeHalfWidth { get; set; }
        public double MaximumInvalidSampleFraction { get; set; }
        public TdrDecayFitMode FitMode { get; set; }
    }

    /// <summary>LabVIEW-friendly result object; all time values are in seconds.</summary>
    public sealed class TdrAutoAnalysisResult
    {
        public TdrAutoAnalysisResult()
        {
            SearchStatus = TdrSearchStatus.InvalidArgument;
            FitStatus = TdrDecayFitStatus.InvalidAmplitude;
            Message = String.Empty;
            IncidentIndex = -1;
            ReflectionIndex = -1;
            IncidentFirstArrivalIndex = -1;
            ReflectionFirstArrivalIndex = -1;
            IncidentTimeSeconds = Double.NaN;
            ReflectionTimeSeconds = Double.NaN;
            PredictedReflectionTimeSeconds = Double.NaN;
            Baseline = Double.NaN;
            NoiseSigma = Double.NaN;
            IncidentValue = Double.NaN;
            ReflectionValue = Double.NaN;
            IncidentMagnitude = Double.NaN;
            ReflectionMagnitude = Double.NaN;
            IncidentPolarity = 0;
            ReflectionPolarity = 0;
            CalculatedCableLengthMeters = Double.NaN;
            TimingErrorSeconds = Double.NaN;
            Confidence = 0.0;
            DecayAmplitudeA = Double.NaN;
            DecayConstantPerSecond = Double.NaN;
            DecayConstantPerMicrosecond = Double.NaN;
            DecayTauSeconds = Double.NaN;
            AttenuationDb = Double.NaN;
            FitR2 = Double.NaN;
            DecayCurveTimeSeconds = new double[0];
            DecayCurveMagnitude = new double[0];
        }

        public TdrSearchStatus SearchStatus { get; internal set; }
        public TdrDecayFitStatus FitStatus { get; internal set; }
        public string Message { get; internal set; }
        public int IncidentIndex { get; internal set; }
        public int ReflectionIndex { get; internal set; }
        // Peak identities remain amplitude/decay anchors. Timing never overwrites them.
        public int IncidentFirstArrivalIndex { get; internal set; }
        public int ReflectionFirstArrivalIndex { get; internal set; }
        public double IncidentTimeSeconds { get; internal set; }
        public double ReflectionTimeSeconds { get; internal set; }
        public double PredictedReflectionTimeSeconds { get; internal set; }
        public double Baseline { get; internal set; }
        public double NoiseSigma { get; internal set; }
        public double IncidentValue { get; internal set; }
        public double ReflectionValue { get; internal set; }
        public double IncidentMagnitude { get; internal set; }
        public double ReflectionMagnitude { get; internal set; }
        public int IncidentPolarity { get; internal set; }
        public int ReflectionPolarity { get; internal set; }
        public bool IsPolarityInverted { get; internal set; }
        public double IncidentSnr { get; internal set; }
        public double ReflectionSnr { get; internal set; }
        public double CalculatedCableLengthMeters { get; internal set; }
        public double TimingErrorSeconds { get; internal set; }
        public double Confidence { get; internal set; }
        // Render cache only, not an acceptance decision or a persisted probability.
        internal double DisplayReflectionScore = Double.NaN;
        public double DecayAmplitudeA { get; internal set; }
        public double DecayConstantPerSecond { get; internal set; }
        public double DecayConstantPerMicrosecond { get; internal set; }
        public double DecayTauSeconds { get; internal set; }
        public double AttenuationDb { get; internal set; }
        public double FitR2 { get; internal set; }
        public double[] DecayCurveTimeSeconds { get; internal set; }
        public double[] DecayCurveMagnitude { get; internal set; }
    }

    public static class TdrReflectionAnalyzer
    {
        public static TdrAutoAnalysisResult Analyze(
            double[] samples,
            double x0Seconds,
            double dxSeconds,
            double cableLengthMeters,
            double velocityMetersPerMicrosecond)
        {
            return Analyze(samples, x0Seconds, dxSeconds, cableLengthMeters,
                velocityMetersPerMicrosecond, new TdrAutoAnalysisOptions());
        }

        public static TdrAutoAnalysisResult Analyze(
            double[] samples,
            double x0Seconds,
            double dxSeconds,
            double cableLengthMeters,
            double velocityMetersPerMicrosecond,
            TdrAutoAnalysisOptions options)
        {
            var result = new TdrAutoAnalysisResult();
            if (samples == null || options == null || !IsFinite(x0Seconds) ||
                !IsFinite(dxSeconds) || dxSeconds <= 0.0 ||
                !IsFinite(cableLengthMeters) || cableLengthMeters <= 0.0 ||
                !IsFinite(velocityMetersPerMicrosecond) || velocityMetersPerMicrosecond <= 0.0 ||
                !ValidateOptions(options))
            {
                return Fail(result, TdrSearchStatus.InvalidArgument, "Invalid TDR analysis argument.");
            }
            if (samples.Length < 16)
                return Fail(result, TdrSearchStatus.TooFewSamples, "At least 16 samples are required.");

            int invalidCount;
            double[] clean = ReplaceInvalidSamples(samples, out invalidCount);
            if (clean == null || invalidCount > samples.Length * options.MaximumInvalidSampleFraction)
                return Fail(result, TdrSearchStatus.TooManyInvalidSamples, "Too many NaN or infinite samples.");

            double baseline = Median(clean);
            double[] centered = new double[clean.Length];
            double[] magnitude = new double[clean.Length];
            double maximumMagnitude = 0.0;
            for (int i = 0; i < clean.Length; i++)
            {
                centered[i] = clean[i] - baseline;
                magnitude[i] = Math.Abs(centered[i]);
                if (magnitude[i] > maximumMagnitude) maximumMagnitude = magnitude[i];
            }

            double sigma = EstimateNoiseSigma(centered, maximumMagnitude);
            result.Baseline = baseline;
            result.NoiseSigma = sigma;
            if (!(maximumMagnitude > 0.0))
                return Fail(result, TdrSearchStatus.IncidentNotFound, "No incident peak was found.");

            double[] detection = SmoothTriangular(magnitude, options.DetectionSmoothHalfWidth);
            double incidentThreshold = options.IncidentThresholdSigma * sigma;
            int incidentDetectionIndex = FindIncidentPeak(
                detection, x0Seconds, dxSeconds, incidentThreshold, options);
            if (incidentDetectionIndex < 0)
                return Fail(result, TdrSearchStatus.IncidentNotFound, "No incident peak exceeded the detection threshold.");

            int refineRadius = Math.Max(2, options.DetectionSmoothHalfWidth + 1);
            int incidentIndex = FindMaximumIndex(magnitude,
                Math.Max(0, incidentDetectionIndex - refineRadius),
                Math.Min(magnitude.Length - 1, incidentDetectionIndex + refineRadius));
            double roundTripSeconds = 2.0 * cableLengthMeters /
                (velocityMetersPerMicrosecond * 1000000.0);
            double travelSamples = roundTripSeconds / dxSeconds;
            double predictedIndex = incidentIndex + travelSamples;
            double predictedTime = x0Seconds + predictedIndex * dxSeconds;

            result.IncidentIndex = incidentIndex;
            result.IncidentTimeSeconds = x0Seconds + incidentIndex * dxSeconds;
            result.PredictedReflectionTimeSeconds = predictedTime;
            result.IncidentValue = centered[incidentIndex];
            result.IncidentMagnitude = magnitude[incidentIndex];
            result.IncidentPolarity = Sign(centered[incidentIndex]);
            result.IncidentSnr = magnitude[incidentIndex] / sigma;

            int halfWidth = Math.Max(options.ReflectionMinHalfWindowSamples,
                (int)Math.Ceiling(travelSamples * options.ReflectionToleranceFraction));
            halfWidth = Math.Max(halfWidth, 2 * refineRadius);
            double requestedStart = predictedIndex - halfWidth;
            double requestedEnd = predictedIndex + halfWidth;
            if (predictedIndex < 0.0 || predictedIndex > magnitude.Length - 1 ||
                requestedEnd < 1.0 || requestedStart > magnitude.Length - 2)
            {
                return Fail(result, TdrSearchStatus.PredictedReflectionOutOfRange,
                    "The predicted reflection is outside the waveform.");
            }

            int searchStart = Math.Max(1, (int)Math.Floor(requestedStart));
            int searchEnd = Math.Min(magnitude.Length - 2, (int)Math.Ceiling(requestedEnd));
            double requestedWidth = Math.Max(1.0, requestedEnd - requestedStart);
            if ((searchEnd - searchStart) / requestedWidth < 0.60)
            {
                return Fail(result, TdrSearchStatus.PredictedReflectionOutOfRange,
                    "Too little of the predicted reflection window is available.");
            }

            double reflectionThreshold = Math.Max(options.ReflectionThresholdSigma * sigma,
                magnitude[incidentIndex] * options.MinimumReflectionRelativeAmplitude);
            int reflectionDetectionIndex = FindReflectionPeak(
                detection, searchStart, searchEnd, predictedIndex, halfWidth,
                reflectionThreshold, sigma);
            if (reflectionDetectionIndex < 0)
                return Fail(result, TdrSearchStatus.ReflectionNotFound,
                    "No reflection peak was found in the predicted window.");

            int reflectionIndex = FindMaximumIndex(magnitude,
                Math.Max(searchStart, reflectionDetectionIndex - refineRadius),
                Math.Min(searchEnd, reflectionDetectionIndex + refineRadius));
            if (reflectionIndex <= incidentIndex)
                return Fail(result, TdrSearchStatus.ReflectionNotFound,
                    "The reflection peak does not follow the incident peak.");

            result.ReflectionIndex = reflectionIndex;
            result.ReflectionTimeSeconds = x0Seconds + reflectionIndex * dxSeconds;
            result.ReflectionValue = centered[reflectionIndex];
            result.ReflectionMagnitude = magnitude[reflectionIndex];
            result.ReflectionPolarity = Sign(centered[reflectionIndex]);
            result.IsPolarityInverted = result.IncidentPolarity != 0 &&
                result.ReflectionPolarity != 0 &&
                result.IncidentPolarity != result.ReflectionPolarity;
            result.ReflectionSnr = magnitude[reflectionIndex] / sigma;
            result.TimingErrorSeconds = result.ReflectionTimeSeconds - predictedTime;
            result.CalculatedCableLengthMeters =
                (result.ReflectionTimeSeconds - result.IncidentTimeSeconds) *
                velocityMetersPerMicrosecond * 1000000.0 / 2.0;
            result.SearchStatus = TdrSearchStatus.Ok;
            result.Message = "OK";

            FitDecay(result, magnitude, sigma, x0Seconds, dxSeconds, options);
            result.Confidence = CalculateConfidence(result, halfWidth * dxSeconds);
            return result;
        }

        internal static void RawTimingStatistics(double[] samples, out double baseline, out double sigma)
        {
            baseline = Double.NaN; sigma = Double.NaN;
            if (samples == null || samples.Length < 16) return;
            for (int i = 0; i < samples.Length; i++) if (!IsFinite(samples[i])) return;
            baseline = Median(samples); var centered = new double[samples.Length]; double maximum = 0;
            for (int i = 0; i < samples.Length; i++) { centered[i] = samples[i] - baseline; maximum = Math.Max(maximum, Math.Abs(centered[i])); }
            sigma = EstimateNoiseSigma(centered, maximum);
        }

        private static bool ValidateOptions(TdrAutoAnalysisOptions options)
        {
            bool hintValid = Double.IsNaN(options.IncidentHintSeconds) ||
                IsFinite(options.IncidentHintSeconds);
            bool hintWindowValid = Double.IsNaN(options.IncidentHintHalfWindowSeconds) ||
                (IsFinite(options.IncidentHintHalfWindowSeconds) && options.IncidentHintHalfWindowSeconds >= 0.0);
            return hintValid && hintWindowValid &&
                IsFinite(options.ReflectionToleranceFraction) && options.ReflectionToleranceFraction > 0.0 &&
                options.ReflectionToleranceFraction <= 1.0 &&
                options.ReflectionMinHalfWindowSamples >= 1 &&
                IsFinite(options.IncidentThresholdSigma) && options.IncidentThresholdSigma > 0.0 &&
                IsFinite(options.ReflectionThresholdSigma) && options.ReflectionThresholdSigma > 0.0 &&
                IsFinite(options.MinimumReflectionRelativeAmplitude) &&
                options.MinimumReflectionRelativeAmplitude >= 0.0 &&
                options.MinimumReflectionRelativeAmplitude <= 1.0 &&
                options.DetectionSmoothHalfWidth >= 0 && options.DetectionSmoothHalfWidth <= 128 &&
                options.FitEnvelopeHalfWidth >= 1 && options.FitEnvelopeHalfWidth <= 1024 &&
                IsFinite(options.MaximumInvalidSampleFraction) &&
                options.MaximumInvalidSampleFraction >= 0.0 &&
                options.MaximumInvalidSampleFraction <= 0.5;
        }

        private static int FindIncidentPeak(double[] envelope, double x0, double dx,
            double threshold, TdrAutoAnalysisOptions options)
        {
            int start = 1;
            int end = envelope.Length - 2;
            if (!Double.IsNaN(options.IncidentHintSeconds))
            {
                double halfWindow = Double.IsNaN(options.IncidentHintHalfWindowSeconds)
                    ? Math.Max(dx * 8.0, dx * (2 * options.DetectionSmoothHalfWidth + 3))
                    : options.IncidentHintHalfWindowSeconds;
                start = Math.Max(start, (int)Math.Floor((options.IncidentHintSeconds - halfWindow - x0) / dx));
                end = Math.Min(end, (int)Math.Ceiling((options.IncidentHintSeconds + halfWindow - x0) / dx));
                if (end < start) return -1;
                int best = FindMaximumIndex(envelope, start, end);
                return envelope[best] >= threshold ? best : -1;
            }

            for (int i = start; i <= end; i++)
            {
                if (envelope[i] >= threshold && envelope[i] >= envelope[i - 1] &&
                    envelope[i] > envelope[i + 1]) return i;
            }
            return -1;
        }

        private static int FindReflectionPeak(double[] envelope, int start, int end,
            double predictedIndex, int halfWidth, double threshold, double sigma)
        {
            int best = -1;
            double bestScore = Double.NegativeInfinity;
            double scale = Math.Max(1.0, halfWidth * 0.5);
            for (int i = start; i <= end; i++)
            {
                if (envelope[i] < threshold || envelope[i] < envelope[i - 1] ||
                    envelope[i] <= envelope[i + 1]) continue;
                double prominence = CalculateProminence(envelope, i, start, end);
                if (prominence < sigma) continue;
                double normalizedDistance = (i - predictedIndex) / scale;
                double score = prominence / sigma *
                    Math.Exp(-0.5 * normalizedDistance * normalizedDistance);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private static double CalculateProminence(double[] values, int index, int start, int end)
        {
            int radius = 4;
            double leftMinimum = values[index];
            double rightMinimum = values[index];
            for (int i = Math.Max(start, index - radius); i < index; i++)
                if (values[i] < leftMinimum) leftMinimum = values[i];
            for (int i = index + 1; i <= Math.Min(end, index + radius); i++)
                if (values[i] < rightMinimum) rightMinimum = values[i];
            return values[index] - Math.Max(leftMinimum, rightMinimum);
        }

        private static void FitDecay(TdrAutoAnalysisResult result, double[] magnitude,
            double sigma, double x0, double dx, TdrAutoAnalysisOptions options)
        {
            double incidentAmplitude = result.IncidentMagnitude;
            double reflectionAmplitude = result.ReflectionMagnitude;
            double duration = result.ReflectionTimeSeconds - result.IncidentTimeSeconds;
            if (!(incidentAmplitude > 0.0) || !(reflectionAmplitude > 0.0) || !(duration > 0.0))
            {
                result.FitStatus = TdrDecayFitStatus.InvalidAmplitude;
                return;
            }

            double logAmplitude;
            double slope;
            int usablePointCount;
            if (options.FitMode == TdrDecayFitMode.RobustEnvelope &&
                TryRobustEnvelopeFit(magnitude, result.IncidentIndex, result.ReflectionIndex,
                    dx, sigma, options.FitEnvelopeHalfWidth, incidentAmplitude,
                    out logAmplitude, out slope, out usablePointCount))
            {
                result.DecayAmplitudeA = Math.Exp(logAmplitude);
                result.DecayConstantPerSecond = -slope;
            }
            else
            {
                result.DecayAmplitudeA = incidentAmplitude;
                result.DecayConstantPerSecond = Math.Log(incidentAmplitude / reflectionAmplitude) / duration;
                usablePointCount = CountFitPoints(magnitude, result.IncidentIndex,
                    result.ReflectionIndex, sigma, incidentAmplitude, options.FitEnvelopeHalfWidth);
            }

            result.DecayConstantPerMicrosecond = result.DecayConstantPerSecond * 1.0e-6;
            result.DecayTauSeconds = result.DecayConstantPerSecond > 0.0
                ? 1.0 / result.DecayConstantPerSecond
                : Double.PositiveInfinity;
            result.AttenuationDb = 20.0 * Math.Log10(incidentAmplitude / reflectionAmplitude);
            result.FitStatus = result.DecayConstantPerSecond > 0.0
                ? (usablePointCount >= 3 ? TdrDecayFitStatus.Ok : TdrDecayFitStatus.InsufficientPoints)
                : TdrDecayFitStatus.NonDecaying;

            int count = result.ReflectionIndex - result.IncidentIndex + 1;
            result.DecayCurveTimeSeconds = new double[count];
            result.DecayCurveMagnitude = new double[count];
            for (int i = 0; i < count; i++)
            {
                double time = x0 + (result.IncidentIndex + i) * dx;
                result.DecayCurveTimeSeconds[i] = time;
                result.DecayCurveMagnitude[i] = result.DecayAmplitudeA *
                    Math.Exp(-result.DecayConstantPerSecond *
                        (time - result.IncidentTimeSeconds));
            }
            result.FitR2 = CalculateFitR2(magnitude, result, sigma,
                dx, options.FitEnvelopeHalfWidth);
        }

        private static bool TryRobustEnvelopeFit(double[] magnitude, int start, int end,
            double dx, double sigma, int halfWidth, double incidentAmplitude,
            out double intercept, out double slope, out int pointCount)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            var baseWeights = new List<double>();
            double floor = Math.Max(1.5 * sigma, incidentAmplitude * 0.005);
            int step = Math.Max(1, halfWidth);
            for (int i = start; i <= end; i += step)
            {
                double value = LocalMaximum(magnitude, i, halfWidth, start, end);
                if (value <= floor) continue;
                xs.Add((i - start) * dx);
                ys.Add(Math.Log(value));
                baseWeights.Add((i == start || i + step > end) ? 4.0 : 1.0);
            }
            if (xs.Count == 0 || xs[xs.Count - 1] < (end - start) * dx)
            {
                xs.Add((end - start) * dx);
                ys.Add(Math.Log(Math.Max(magnitude[end], Double.Epsilon)));
                baseWeights.Add(4.0);
            }
            pointCount = xs.Count;
            intercept = Double.NaN;
            slope = Double.NaN;
            if (xs.Count < 3) return false;

            double[] weights = baseWeights.ToArray();
            for (int iteration = 0; iteration < 5; iteration++)
            {
                if (!WeightedLineFit(xs, ys, weights, out intercept, out slope)) return false;
                double[] absoluteResiduals = new double[xs.Count];
                for (int i = 0; i < xs.Count; i++)
                    absoluteResiduals[i] = Math.Abs(ys[i] - intercept - slope * xs[i]);
                double scale = 1.4826 * Median(absoluteResiduals);
                if (!(scale > 1.0e-15)) break;
                double huberLimit = 1.345 * scale;
                for (int i = 0; i < weights.Length; i++)
                {
                    double robustWeight = absoluteResiduals[i] <= huberLimit
                        ? 1.0 : huberLimit / absoluteResiduals[i];
                    weights[i] = baseWeights[i] * robustWeight;
                }
            }
            return IsFinite(intercept) && IsFinite(slope);
        }

        private static bool WeightedLineFit(List<double> xs, List<double> ys,
            double[] weights, out double intercept, out double slope)
        {
            double sw = 0.0, sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
            for (int i = 0; i < xs.Count; i++)
            {
                double w = weights[i];
                sw += w;
                sx += w * xs[i];
                sy += w * ys[i];
                sxx += w * xs[i] * xs[i];
                sxy += w * xs[i] * ys[i];
            }
            double denominator = sw * sxx - sx * sx;
            if (!(Math.Abs(denominator) > Double.Epsilon))
            {
                intercept = Double.NaN;
                slope = Double.NaN;
                return false;
            }
            slope = (sw * sxy - sx * sy) / denominator;
            intercept = (sy - slope * sx) / sw;
            return true;
        }

        private static int CountFitPoints(double[] magnitude, int start, int end,
            double sigma, double incidentAmplitude, int halfWidth)
        {
            double floor = Math.Max(1.5 * sigma, incidentAmplitude * 0.005);
            int count = 0;
            int step = Math.Max(1, halfWidth);
            for (int i = start; i <= end; i += step)
                if (LocalMaximum(magnitude, i, halfWidth, start, end) > floor) count++;
            return count;
        }

        private static double CalculateFitR2(double[] magnitude,
            TdrAutoAnalysisResult result, double sigma, double dx, int halfWidth)
        {
            var observed = new List<double>();
            var predicted = new List<double>();
            double floor = Math.Max(1.5 * sigma, result.IncidentMagnitude * 0.005);
            int step = Math.Max(1, halfWidth);
            for (int i = result.IncidentIndex; i <= result.ReflectionIndex; i += step)
            {
                double value = LocalMaximum(magnitude, i, halfWidth,
                    result.IncidentIndex, result.ReflectionIndex);
                if (value <= floor) continue;
                double elapsed = (i - result.IncidentIndex) * dx;
                observed.Add(Math.Log(value));
                predicted.Add(Math.Log(result.DecayAmplitudeA) -
                    result.DecayConstantPerSecond * elapsed);
            }
            if (observed.Count < 3) return Double.NaN;
            double mean = 0.0;
            for (int i = 0; i < observed.Count; i++) mean += observed[i];
            mean /= observed.Count;
            double total = 0.0, residual = 0.0;
            for (int i = 0; i < observed.Count; i++)
            {
                double d = observed[i] - mean;
                double e = observed[i] - predicted[i];
                total += d * d;
                residual += e * e;
            }
            return total > Double.Epsilon ? 1.0 - residual / total : Double.NaN;
        }

        internal static double CalculateConfidence(TdrAutoAnalysisResult result,
            double reflectionHalfWindowSeconds)
        {
            double incidentScore = Math.Min(1.0, result.IncidentSnr / 10.0);
            double reflectionScore = Math.Min(1.0, result.ReflectionSnr / 8.0);
            double timeScale = Math.Max(reflectionHalfWindowSeconds * 0.5, Double.Epsilon);
            double q = result.TimingErrorSeconds / timeScale;
            double timingScore = Math.Exp(-0.5 * q * q);
            double fitScore = Double.IsNaN(result.FitR2)
                ? 0.5 : Math.Max(0.0, Math.Min(1.0, result.FitR2));
            return Math.Max(0.0, Math.Min(1.0,
                0.35 * incidentScore + 0.35 * reflectionScore +
                0.20 * timingScore + 0.10 * fitScore));
        }

        private static double[] ReplaceInvalidSamples(double[] source, out int invalidCount)
        {
            double[] result = (double[])source.Clone();
            invalidCount = 0;
            for (int i = 0; i < result.Length; i++)
                if (!IsFinite(result[i])) invalidCount++;
            if (invalidCount == 0) return result;
            if (invalidCount == result.Length) return null;

            int index = 0;
            while (index < result.Length)
            {
                if (IsFinite(result[index])) { index++; continue; }
                int runStart = index;
                while (index < result.Length && !IsFinite(result[index])) index++;
                int runEnd = index - 1;
                int left = runStart - 1;
                int right = index < result.Length ? index : -1;
                for (int i = runStart; i <= runEnd; i++)
                {
                    if (left >= 0 && right >= 0)
                    {
                        double fraction = (double)(i - left) / (right - left);
                        result[i] = result[left] + (result[right] - result[left]) * fraction;
                    }
                    else if (left >= 0) result[i] = result[left];
                    else result[i] = result[right];
                }
            }
            return result;
        }

        private static double EstimateNoiseSigma(double[] centered, double maximumMagnitude)
        {
            double[] deviations = new double[centered.Length];
            double center = Median(centered);
            for (int i = 0; i < centered.Length; i++)
                deviations[i] = Math.Abs(centered[i] - center);
            double sigma = 1.4826 * Median(deviations);
            double floor = Math.Max(1.0e-15, maximumMagnitude * 1.0e-12);
            return sigma > floor ? sigma : floor;
        }

        private static double[] SmoothTriangular(double[] values, int halfWidth)
        {
            if (halfWidth <= 0) return (double[])values.Clone();
            double[] output = new double[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double sum = 0.0, weightSum = 0.0;
                for (int offset = -halfWidth; offset <= halfWidth; offset++)
                {
                    int index = i + offset;
                    if (index < 0 || index >= values.Length) continue;
                    double weight = halfWidth + 1 - Math.Abs(offset);
                    sum += values[index] * weight;
                    weightSum += weight;
                }
                output[i] = sum / weightSum;
            }
            return output;
        }

        private static double LocalMaximum(double[] values, int center, int radius,
            int minimumIndex, int maximumIndex)
        {
            int start = Math.Max(minimumIndex, center - radius);
            int end = Math.Min(maximumIndex, center + radius);
            return values[FindMaximumIndex(values, start, end)];
        }

        private static int FindMaximumIndex(double[] values, int start, int end)
        {
            int best = start;
            for (int i = start + 1; i <= end; i++)
                if (values[i] > values[best]) best = i;
            return best;
        }

        private static double CalculateProminenceUnused(double[] values, int index)
        {
            return values[index];
        }

        private static double Median(double[] values)
        {
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return (sorted.Length & 1) == 0
                ? 0.5 * (sorted[middle - 1] + sorted[middle])
                : sorted[middle];
        }

        private static int Sign(double value)
        {
            return value > 0.0 ? 1 : (value < 0.0 ? -1 : 0);
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private static TdrAutoAnalysisResult Fail(TdrAutoAnalysisResult result,
            TdrSearchStatus status, string message)
        {
            result.SearchStatus = status;
            result.Message = message;
            return result;
        }
    }

    /// <summary>
    /// Offline, symmetric linear-phase FIR low-pass filter. The returned samples are
    /// group-delay compensated so that TDR cursor indices do not move.
    /// </summary>
    public static class LinearLowPassFilter
    {
        public static double[] Apply(double[] samples, double sampleIntervalSeconds,
            double cutoffMegahertz)
        {
            return Apply(samples, sampleIntervalSeconds, cutoffMegahertz, 129);
        }

        public static double[] Apply(double[] samples, double sampleIntervalSeconds,
            double cutoffMegahertz, int tapCount)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (!IsFinite(sampleIntervalSeconds) || sampleIntervalSeconds <= 0.0)
                throw new ArgumentOutOfRangeException("sampleIntervalSeconds");
            if (!IsFinite(cutoffMegahertz) || cutoffMegahertz < 1.0 || cutoffMegahertz > 60.0)
                throw new ArgumentOutOfRangeException("cutoffMegahertz",
                    "The bandwidth must be between 1 MHz and 60 MHz.");
            if (tapCount < 3 || (tapCount & 1) == 0)
                throw new ArgumentOutOfRangeException("tapCount", "Tap count must be an odd number of at least three.");
            for (int i = 0; i < samples.Length; i++)
                if (!IsFinite(samples[i]))
                    throw new ArgumentException("Samples must contain only finite values.", "samples");

            double sampleRate = 1.0 / sampleIntervalSeconds;
            double nyquist = sampleRate * 0.5;
            double cutoffHz = cutoffMegahertz * 1000000.0;

            // A requested analogue bandwidth at or above Nyquist passes every
            // representable discrete-time frequency, so filtering is unnecessary.
            if (cutoffHz >= nyquist * (1.0 - 1.0e-12))
                return (double[])samples.Clone();
            if (samples.Length < 3)
                throw new ArgumentException("At least three samples are required for FIR filtering.", "samples");

            int usableTaps = Math.Min(tapCount,
                (samples.Length & 1) == 1 ? samples.Length : samples.Length - 1);
            if (usableTaps < 3) usableTaps = 3;
            double[] kernel = DesignKernel(usableTaps, cutoffHz / sampleRate);
            int center = usableTaps / 2;
            double[] output = new double[samples.Length];
            for (int i = 0; i < output.Length; i++)
            {
                double sum = 0.0;
                for (int k = 0; k < kernel.Length; k++)
                {
                    int sourceIndex = ReflectIndex(i + k - center, samples.Length);
                    sum += kernel[k] * samples[sourceIndex];
                }
                output[i] = sum;
            }
            return output;
        }

        private static double[] DesignKernel(int tapCount, double normalizedCutoff)
        {
            double[] kernel = new double[tapCount];
            int center = tapCount / 2;
            double sum = 0.0;
            for (int i = 0; i < tapCount; i++)
            {
                int m = i - center;
                double ideal = m == 0
                    ? 2.0 * normalizedCutoff
                    : Math.Sin(2.0 * Math.PI * normalizedCutoff * m) / (Math.PI * m);
                double hamming = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (tapCount - 1));
                kernel[i] = ideal * hamming;
                sum += kernel[i];
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
            return kernel;
        }

        private static int ReflectIndex(int index, int length)
        {
            while (index < 0 || index >= length)
            {
                if (index < 0) index = -index;
                if (index >= length) index = 2 * length - 2 - index;
            }
            return index;
        }

        private static bool IsFinite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }
}
