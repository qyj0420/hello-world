using System;
using System.IO;

namespace Xiongxin.ZeroBench
{
    /// <summary>Uniform display/export products. These are not the native location data or a PD classification.</summary>
    public sealed class DetectedWaveformResult
    {
        public double OutputRateHz;
        public double DurationSeconds;
        public double RequestedDurationSeconds;
        public double BaselineV;
        public double[] VoltageKv;
        public double[] PdEnvelopePc;
        public int OutputCount;
        public int ConsumedPdSamples;
        public int UnusedPdSamples;
        public bool Truncated;
        public bool Overflow;
        public string Algorithm;
        public string VoltageAlgorithm;
    }

    public static class DetectedWaveforms
    {
        public const double OutputRateHz = 80000.0;
        public const string PdAlgorithm = "80k-absolute-peak-bin-v1: subtract robust DC baseline; maximum absolute native-sample amplitude in each [t,t+12.5us) bin; no point skipping, no threshold masking";
        public const string SignedVoltageAlgorithm = "Signed AI4 volts times divider V/V divided by 1000; no rectification, baseline subtraction, noise gating or forced zero; linear resampling only if actual ART rate differs from 80k";

        /// <summary>
        /// The optional baseline must be the same robust DC estimate used by AnalyzePd when supplied.
        /// A bin includes every original sample whose timestamp lies in its half-open time interval.
        /// Thus 125 MS/s produces alternating 1563/1562-sample bins, not a rounded fixed stride.
        /// Only complete common-time bins are returned. Missing tails are never padded with zero.
        /// Raw voltage/ADC arrays remain unchanged; hardware overflow remains visible in metadata.
        /// </summary>
        public static DetectedWaveformResult Build(BenchCapture capture, double? baselineV = null)
        {
            if (capture == null || capture.Settings == null) throw new ArgumentNullException("capture");
            BenchSettings settings = capture.Settings;
            if (capture.VoltageRaw == null || capture.PdAdc == null || capture.VoltageRaw.Length == 0 || capture.PdAdc.Length == 0)
                throw new InvalidDataException("80 kS/s 检波需要两卡实际采集数据，不补造缺失数据。");
            if (!Positive(capture.ArtRateHz) || !Positive(capture.PicoRateHz) || capture.PicoRateHz < OutputRateHz)
                throw new InvalidDataException("Pico 原始采样率必须至少为 80 kS/s；不能插值制造检波采样点。");
            if (!Positive(settings.CaptureDurationSeconds) || !Positive(settings.DividerVPerV) ||
                !Positive(settings.PdCalibrationPcPerV) || !Positive(capture.PicoFullScaleV) ||
                capture.MaximumAdc <= 0 || capture.MaximumAdc > Int16.MaxValue ||
                !Finite(settings.TransientExclusionMs) || settings.TransientExclusionMs < 0)
                throw new InvalidDataException("检波所需时间、采样率、量程或校准元数据无效。");
            if (baselineV.HasValue && !Finite(baselineV.Value))
                throw new ArgumentException("局放基线必须为有限实数。", "baselineV");

            double availableSeconds = Math.Min(settings.CaptureDurationSeconds,
                Math.Min(capture.VoltageRaw.Length / capture.ArtRateHz, capture.PdAdc.Length / capture.PicoRateHz));
            int count = Math.Min(settings.ExpectedArtCount, FloorNearInteger(availableSeconds * OutputRateHz));
            // Do not extrapolate an ART voltage value beyond its last acquired timestamp.
            int voltageSupported = FloorNearInteger((capture.VoltageRaw.Length - 1) * OutputRateHz / capture.ArtRateHz) + 1;
            count = Math.Min(count, voltageSupported);
            if (count < 1) throw new InvalidDataException("共同时间范围不足一个完整 12.5 µs 检波窗口。");
            int lastNative = CeilingNearInteger(count * capture.PicoRateHz / OutputRateHz);
            while (lastNative > capture.PdAdc.Length && count > 0)
                lastNative = CeilingNearInteger(--count * capture.PicoRateHz / OutputRateHz);
            if (count < 1) throw new InvalidDataException("Pico 数据不足一个完整检波窗口。");

            double scaleV = capture.PicoFullScaleV / capture.MaximumAdc;
            double baseline = baselineV ?? EstimateBaseline(capture.PdAdc, lastNative, capture.PicoRateHz,
                settings.TransientExclusionMs, scaleV);
            var result = new DetectedWaveformResult {
                OutputRateHz = OutputRateHz, DurationSeconds = count / OutputRateHz,
                RequestedDurationSeconds = settings.CaptureDurationSeconds, BaselineV = baseline,
                VoltageKv = new double[count], PdEnvelopePc = new double[count], OutputCount = count,
                ConsumedPdSamples = lastNative, UnusedPdSamples = capture.PdAdc.Length - lastNative,
                Truncated = count < settings.ExpectedArtCount, Overflow = capture.Overflow,
                Algorithm = PdAlgorithm, VoltageAlgorithm = SignedVoltageAlgorithm
            };

            int begin = 0;
            for (int bin = 0; bin < count; bin++)
            {
                int end = CeilingNearInteger((bin + 1) * capture.PicoRateHz / OutputRateHz);
                if (end <= begin || end > capture.PdAdc.Length)
                    throw new InvalidDataException("检波窗口无原始采样点；拒绝补零或虚构数据。");
                double peakV = 0;
                for (int sample = begin; sample < end; sample++)
                    peakV = Math.Max(peakV, Math.Abs(capture.PdAdc[sample] * scaleV - baseline));
                result.PdEnvelopePc[bin] = peakV * settings.PdCalibrationPcPerV;
                begin = end;

                double position = bin * capture.ArtRateHz / OutputRateHz;
                double nearest = Math.Round(position);
                if (Math.Abs(position - nearest) < 1e-8) position = nearest;
                int left = (int)Math.Floor(position);
                if (left < 0 || left >= capture.VoltageRaw.Length)
                    throw new InvalidDataException("电压采集范围不足，不能外推补齐检波数据。");
                double volts = capture.VoltageRaw[left];
                double fraction = position - left;
                if (fraction > 0)
                {
                    if (left + 1 >= capture.VoltageRaw.Length)
                        throw new InvalidDataException("电压末端没有真实采样点可用于重采样。");
                    volts += (capture.VoltageRaw[left + 1] - volts) * fraction;
                }
                result.VoltageKv[bin] = volts * settings.DividerVPerV / 1000.0;
                if (!Finite(result.VoltageKv[bin]) || !Finite(result.PdEnvelopePc[bin]))
                    throw new InvalidDataException("检波数据含非有限值；检查原始数据及换算系数。");
            }
            return result;
        }

        private static double EstimateBaseline(short[] adc, int count, double rate, double exclusionMs, double scaleV)
        {
            int begin = (int)Math.Min(count, Math.Ceiling(exclusionMs * rate / 1000.0));
            if (begin == count) begin = 0; // A short diagnostic capture still has acquired baseline samples.
            int sampleCount = Math.Min(8192, count - begin);
            var samples = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = adc[begin + (int)((long)i * (count - begin) / sampleCount)] * scaleV;
            Array.Sort(samples);
            return (sampleCount & 1) != 0 ? samples[sampleCount / 2]
                : .5 * (samples[sampleCount / 2 - 1] + samples[sampleCount / 2]);
        }

        private static int FloorNearInteger(double value)
        {
            double integer = Math.Round(value);
            if (Math.Abs(value - integer) < 1e-8) value = integer;
            if (!Finite(value) || value < 0 || value >= Int32.MaxValue) throw new InvalidDataException("检波点数越界。");
            return (int)Math.Floor(value);
        }
        private static int CeilingNearInteger(double value)
        {
            double integer = Math.Round(value);
            if (Math.Abs(value - integer) < 1e-8) value = integer;
            if (!Finite(value) || value < 0 || value >= Int32.MaxValue) throw new InvalidDataException("原始采样边界越界。");
            return (int)Math.Ceiling(value);
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool Positive(double value) { return Finite(value) && value > 0; }
    }
}
