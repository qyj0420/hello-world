using System;
using System.Globalization;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Algorithms;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 出厂加压 → 定位入参提取（非 E+ 公式）。
    /// 对齐 WaveformSummaryWriter：ADC→V（可选 ×pC/V）；超长波形用峰值仓降采样，
    /// 有效 Fs 目标 80 kHz、下限 ≥20 kHz（否则 10 kHz HP 失效）。
    /// </summary>
    public static class FactoryLocateExtract
    {
        /// <summary>与 WaveformSummaryWriter.PicoLocatePreviewRateHz 一致。</summary>
        public const double PicoLocatePreviewRateHz = 80000;
        /// <summary>与 WaveformSummaryWriter.PicoLocatePreviewMinRateHz 一致。</summary>
        public const double PicoLocatePreviewMinRateHz = 20000;
        /// <summary>与 WaveformSummaryWriter.PicoPreviewMaxSamples 一致。</summary>
        public const int PicoPreviewMaxSamples = 20000;
        /// <summary>硬顶：约 2s@20k。</summary>
        public const int HardCapSamples = 40000;
        /// <summary>低于此点数保留全分辨率（校准 Rapid 短脉冲）。</summary>
        public const int KeepFullResolutionBelow = PicoPreviewMaxSamples;

        public sealed class ExtractResult
        {
            public LocationAnalysisRequest Request { get; set; }
            public double[] LocDataVolts { get; set; }
            public double EffectiveSampleRateHz { get; set; }
            public int Stride { get; set; }
            public bool UsedPeakHoldDownsample { get; set; }
            public bool ScaledToPc { get; set; }
            public double PdCalibrationPcPerVolt { get; set; }
            public string UnitStatus { get; set; }
            public string Detail { get; set; }

            public ExtractResult()
            {
                Request = new LocationAnalysisRequest();
                LocDataVolts = new double[0];
                EffectiveSampleRateHz = double.NaN;
                Stride = 1;
                UnitStatus = string.Empty;
                Detail = string.Empty;
            }
        }

        public static LocationAnalysisRequest BuildRequest(
            AcquisitionCycleResult cycle,
            double pdCalibrationPcPerVolt)
        {
            return Build(cycle, pdCalibrationPcPerVolt).Request;
        }

        public static ExtractResult Build(AcquisitionCycleResult cycle, double pdCalibrationPcPerVolt)
        {
            var er = new ExtractResult();
            if (cycle == null || cycle.PicoRawAdc == null || cycle.PicoRawAdc.Length == 0)
            {
                er.Detail = "empty=no PicoRawAdc";
                er.UnitStatus = "empty";
                er.Request = new LocationAnalysisRequest { UseLocData = true, LocData = new double[0] };
                return er;
            }

            double picoHz = cycle.PicoSampleRateHz > 0 ? cycle.PicoSampleRateHz : 125e6;
            short maxAdc = cycle.PicoMaximumAdc > 0 ? cycle.PicoMaximumAdc : (short)32512;
            double fullScale = cycle.PicoFullScaleVolts > 0 ? cycle.PicoFullScaleVolts : 1.0;
            double scale = fullScale / maxAdc;

            short[] adc = cycle.PicoRawAdc;
            int n = adc.Length;
            int step = 1;
            bool down = false;

            // 出厂意图：定位可用全速率；仅当点数过大时按 80k 目标 / 20k 下限峰值仓抽稀
            if (n > KeepFullResolutionBelow)
            {
                double picoTargetHz = PicoLocatePreviewRateHz;
                step = Math.Max(1, (int)Math.Round(picoHz / picoTargetHz));
                int maxStepForMinFs = Math.Max(1, (int)Math.Floor(picoHz / PicoLocatePreviewMinRateHz));
                int approxOut = (n + step - 1) / step;
                if (approxOut > PicoPreviewMaxSamples)
                {
                    int stepForCap = Math.Max(step, (n + PicoPreviewMaxSamples - 1) / PicoPreviewMaxSamples);
                    step = stepForCap > maxStepForMinFs ? maxStepForMinFs : stepForCap;
                }
                if (step > maxStepForMinFs) step = maxStepForMinFs;
                down = step > 1;
            }

            int writeCap = Math.Max(PicoPreviewMaxSamples, (n + step - 1) / step);
            if (writeCap > HardCapSamples) writeCap = HardCapSamples;

            var volts = new double[Math.Min(writeCap, (n + step - 1) / step)];
            int written = 0;
            for (int i = 0; i < n && written < volts.Length; i += step)
            {
                int end = Math.Min(n, i + step);
                short peak = adc[i];
                int peakAbs = Math.Abs((int)peak);
                for (int k = i + 1; k < end; k++)
                {
                    short v = adc[k];
                    int a = Math.Abs((int)v);
                    if (a > peakAbs) { peak = v; peakAbs = a; }
                }
                volts[written++] = peak * scale;
            }
            if (written != volts.Length)
            {
                var trim = new double[written];
                Array.Copy(volts, trim, written);
                volts = trim;
            }

            double effectiveHz = picoHz / step;
            er.LocDataVolts = volts;
            er.EffectiveSampleRateHz = effectiveHz;
            er.Stride = step;
            er.UsedPeakHoldDownsample = down;
            er.PdCalibrationPcPerVolt = pdCalibrationPcPerVolt;

            double[] locData = volts;
            bool scaled = false;
            string unit = "volts";
            if (!double.IsNaN(pdCalibrationPcPerVolt) && pdCalibrationPcPerVolt > 0)
            {
                locData = new double[volts.Length];
                for (int i = 0; i < volts.Length; i++)
                    locData[i] = volts[i] * pdCalibrationPcPerVolt;
                scaled = true;
                unit = "pC;factor=" + pdCalibrationPcPerVolt.ToString("G6", CultureInfo.InvariantCulture);
            }
            er.ScaledToPc = scaled;
            er.UnitStatus = unit;
            er.Detail = "n=" + n
                + ";out=" + locData.Length
                + ";stride=" + step
                + ";fsIn=" + picoHz.ToString("G9", CultureInfo.InvariantCulture)
                + ";fsEff=" + effectiveHz.ToString("G9", CultureInfo.InvariantCulture)
                + ";peakHold=" + down
                + ";range=" + cycle.PicoRangeIndexUsed
                + ";firstZero_s=" + (double.IsNaN(cycle.FirstZeroTimeSeconds)
                    ? "NaN"
                    : cycle.FirstZeroTimeSeconds.ToString("G9", CultureInfo.InvariantCulture));

            var req = new LocationAnalysisRequest
            {
                UseLocData = true,
                LocData = locData,
                SampleRateHz = effectiveHz,
                OscillationFrequencyHz = double.NaN,
                ConnectingCableLengthM = 0
            };
            // 与 .dac 对照默认扫参一致；不改 E+ 公式本身
            DacCompareTuning.Default.ApplyTo(req);
            // ApplyTo 可能覆盖 SampleRate/LocData — 重新钉死提取结果
            req.UseLocData = true;
            req.LocData = locData;
            req.SampleRateHz = effectiveHz;
            er.Request = req;
            return er;
        }

        /// <summary>
        /// 计算定位预览 stride（供 AcquisitionCyclePersister 与出厂 WaveformSummaryWriter 共用规则）。
        /// </summary>
        public static int ComputeLocatePreviewStride(int sampleCount, double picoSampleRateHz, out double effectiveHz)
        {
            double picoHz = picoSampleRateHz > 0 ? picoSampleRateHz : 125e6;
            int n = sampleCount < 0 ? 0 : sampleCount;
            if (n <= KeepFullResolutionBelow)
            {
                effectiveHz = picoHz;
                return 1;
            }
            int step = Math.Max(1, (int)Math.Round(picoHz / PicoLocatePreviewRateHz));
            int maxStepForMinFs = Math.Max(1, (int)Math.Floor(picoHz / PicoLocatePreviewMinRateHz));
            int approxOut = (n + step - 1) / step;
            if (approxOut > PicoPreviewMaxSamples)
            {
                int stepForCap = Math.Max(step, (n + PicoPreviewMaxSamples - 1) / PicoPreviewMaxSamples);
                step = stepForCap > maxStepForMinFs ? maxStepForMinFs : stepForCap;
            }
            if (step > maxStepForMinFs) step = maxStepForMinFs;
            effectiveHz = picoHz / step;
            return step;
        }
    }
}
