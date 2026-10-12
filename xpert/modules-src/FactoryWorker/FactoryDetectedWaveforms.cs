// Same bin boundaries as the old DetectedWaveforms; factory-only shared
// calibration filter added. Original ADC arrays and inherited class are unchanged.
using System;
using System.IO;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    /// <summary>Uniform display/export products. These are not the native location data or a PD classification.</summary>
    public static class FactoryDetectedWaveforms
    {
        public const double OutputRateHz = 80000.0;
        public const string PdAlgorithm = PdLinearHighPass.FilterId + "; 80k-absolute-peak-bin-v2: full acquired segment zero-phase 10kHz high-pass, then every native sample maximum absolute amplitude in each [t,t+12.5us) bin; no threshold masking";
        public const string SignedVoltageAlgorithm = "Signed AI4 volts times divider V/V divided by 1000; no rectification, baseline subtraction, noise gating or forced zero; linear resampling only if actual ART rate differs from 80k";

        /// <summary>
        /// Calibration and pressure share the 100 10 kHz high-pass filter. The
        /// returned BaselineV describes original raw DC; it is never subtracted
        /// from the high-passed waveform a second time.
        /// A bin includes every original sample whose timestamp lies in its half-open time interval.
        /// Thus 125 MS/s produces alternating 1563/1562-sample bins, not a rounded fixed stride.
        /// Only complete common-time bins are returned. Missing tails are never padded with zero.
        /// Raw voltage/ADC arrays remain unchanged; hardware overflow remains visible in metadata.
        /// </summary>
        public static DetectedWaveformResult Build(BenchCapture capture)
        { double full, after; return Build(capture, out full, out after); }

        public static DetectedWaveformResult Build(BenchCapture capture, out double fullPeakPc, out double postTransientPeakPc)
        {
            if (capture == null || capture.Settings == null) throw new ArgumentNullException("capture");
            BenchSettings settings = capture.Settings;
            if (capture.VoltageRaw == null || capture.PdAdc == null || capture.VoltageRaw.Length == 0 || capture.PdAdc.Length == 0)
                throw new InvalidDataException("80 kS/s 检波需要两卡实际采集数据，不补造缺失数据。");
            if (!Positive(capture.ArtRateHz) || !Positive(capture.PicoRateHz) || capture.PicoRateHz < 100000.0)
                throw new InvalidDataException("同校准的 10 kHz 高通检波要求 Pico 原始采样率至少为 100 kS/s；80 kS/s 是检波后输出率，不能插值制造原始采样点。");
            if (!Positive(settings.CaptureDurationSeconds) || !Positive(settings.DividerVPerV) ||
                !Positive(settings.PdCalibrationPcPerV) || !Positive(capture.PicoFullScaleV) ||
                capture.MaximumAdc <= 0 || capture.MaximumAdc > Int16.MaxValue ||
                !Finite(settings.TransientExclusionMs) || settings.TransientExclusionMs < 0)
                throw new InvalidDataException("检波所需时间、采样率、量程或校准元数据无效。");


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
            double baseline = EstimateBaseline(capture.PdAdc, lastNative, capture.PicoRateHz,
                settings.PreTriggerDurationMs+settings.TransientExclusionMs, scaleV);
            // Filter the whole segment BEFORE binning, exactly as calibration.
            // Filtering after 80k detection would destroy narrow physical peaks.
            var filtered = FactoryPdSignal.Get(capture);
            fullPeakPc = 0; postTransientPeakPc = 0;
            int exclusion = (int)Math.Min(filtered.Length, Math.Ceiling((settings.PreTriggerDurationMs+settings.TransientExclusionMs) * capture.PicoRateHz / 1000));
            for (int i = 0; i < lastNative; i++)
            {
                double charge = Math.Abs(filtered[i]) * settings.PdCalibrationPcPerV;
                fullPeakPc = Math.Max(fullPeakPc, charge);
                if (i >= exclusion) postTransientPeakPc = Math.Max(postTransientPeakPc, charge);
            }
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
                    peakV = Math.Max(peakV, Math.Abs(filtered[sample]));
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
