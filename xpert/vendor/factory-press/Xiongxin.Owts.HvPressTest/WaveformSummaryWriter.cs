using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Acquisition;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>每次采集：CSV 行 + 旁路波形摘要（meta + 可选降采样预览）。</summary>
    public static class WaveformSummaryWriter
    {
        /// <summary>AI4 振荡预览点数软上限（超则抽稀；振荡不做 10kHP）。</summary>
        public const int PreviewMaxSamples = 4800;
        /// <summary>
        /// Pico 定位预览目标有效采样率（Hz）。须 ≥20k 才能做 10kHz 线性高通；
        /// 默认 80k；长波形宁可多写点，也绝不把有效 Fs 压到 &lt;20k（旧 8k 导致高通失效/噪声墙）。
        /// </summary>
        public const double PicoLocatePreviewRateHz = 80000;
        /// <summary>Pico 定位预览有效 Fs 硬下限（= 10kHP 尼奎斯特安全边）。</summary>
        public const double PicoLocatePreviewMinRateHz = 20000;
        /// <summary>兼容旧名：曾误用 8k 统一 Fs；现指向定位预览目标 Fs。</summary>
        public const double UnifiedPreviewRateHz = PicoLocatePreviewRateHz;
        /// <summary>Pico 预览点软上限（约 250ms@80k）；越过下限时仍可超过此值保 Fs。</summary>
        public const int PicoPreviewMaxSamples = 20000;

        public static string EnsureRunDirectory(string outputRoot)
        {
            string root = string.IsNullOrWhiteSpace(outputRoot)
                ? Path.Combine(Directory.GetCurrentDirectory(), "out", "hv-press")
                : outputRoot;
            string run = Path.Combine(Path.GetFullPath(root),
                "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(Path.Combine(run, "waveforms"));
            return run;
        }

        public static void WriteCsvHeader(string csvPath)
        {
            string header = "step,targetKv,targetV,success,exitReason,peakActualVoltageV,peakCurrentA,"
                + "picoSamples,picoRangeIndex,picoFullScaleV,suggestedRangeIndex,task6RangeSuitable,picoOverrangeOrClipped,"
                + "firstZeroTimeSeconds,dischargeVerified,aoZeroConfirmed,"
                + BasicMetricsResult.CsvHeaderFragment()
                + ",errorMessage,waveformStem,metricsDiagnostic";
            File.WriteAllText(csvPath, header + Environment.NewLine, new UTF8Encoding(false));
        }

        public static string AppendCycle(
            string runDir,
            string csvPath,
            int step,
            double targetKv,
            AcquisitionCycleResult cycle,
            BasicMetricsResult metrics,
            bool aoZeroConfirmed)
        {
            string stem = "step" + step.ToString("D3", CultureInfo.InvariantCulture)
                + "-" + targetKv.ToString("0.###", CultureInfo.InvariantCulture) + "kV";
            string waveDir = Path.Combine(runDir, "waveforms");
            WriteMeta(waveDir, stem, targetKv, cycle, metrics, aoZeroConfirmed);
            WritePicoRawFullRate(waveDir, stem, cycle);
            WritePreview(waveDir, stem, cycle);

            var sb = new StringBuilder();
            sb.Append(step).Append(',');
            sb.Append(targetKv.ToString("G9", CultureInfo.InvariantCulture)).Append(',');
            sb.Append((targetKv * 1000.0).ToString("G9", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(cycle != null && cycle.Success ? "true" : "false").Append(',');
            sb.Append(cycle != null ? cycle.ExitReason.ToString() : "None").Append(',');
            sb.Append(Num(cycle != null ? cycle.PeakActualVoltageV : double.NaN)).Append(',');
            sb.Append(Num(cycle != null ? cycle.PeakCurrentA : double.NaN)).Append(',');
            sb.Append(cycle != null && cycle.PicoRawAdc != null ? cycle.PicoRawAdc.Length : 0).Append(',');
            sb.Append(cycle != null ? cycle.PicoRangeIndexUsed : -1).Append(',');
            sb.Append(Num(cycle != null ? cycle.PicoFullScaleVolts : double.NaN)).Append(',');
            sb.Append(cycle != null ? cycle.SuggestedPicoRangeIndex : -1).Append(',');
            sb.Append(cycle != null && cycle.Task6RangeSuitable ? "true" : "false").Append(',');
            sb.Append(cycle != null && cycle.PicoOverrangeOrClipped ? "true" : "false").Append(',');
            sb.Append(Num(cycle != null ? cycle.FirstZeroTimeSeconds : double.NaN)).Append(',');
            sb.Append(cycle != null && cycle.DischargeVerified ? "true" : "false").Append(',');
            sb.Append(aoZeroConfirmed ? "true" : "false").Append(',');
            sb.Append(metrics != null ? metrics.ToCsvFragment() : ",,,,,,,");
            sb.Append(',');
            sb.Append(Csv(cycle != null ? cycle.ErrorMessage : ""));
            sb.Append(',').Append(stem);
            sb.Append(',').Append(Csv(metrics != null ? metrics.Diagnostic : ""));
            File.AppendAllText(csvPath, sb.ToString() + Environment.NewLine, new UTF8Encoding(false));
            return stem;
        }

        static void WriteMeta(string waveDir, string stem, double targetKv, AcquisitionCycleResult cycle, BasicMetricsResult metrics, bool aoZero)
        {
            var sb = new StringBuilder();
            sb.AppendLine("program=Xiongxin.Owts.HvPressTest");
            sb.AppendLine("voltageGainNote=" + PressTestConfig.TestBenchGainNote);
            sb.AppendLine("targetKv=" + targetKv.ToString("G9", CultureInfo.InvariantCulture));
            if (cycle != null)
            {
                sb.AppendLine("success=" + cycle.Success);
                sb.AppendLine("exit=" + cycle.ExitReason);
                sb.AppendLine("peakV=" + cycle.PeakActualVoltageV.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("peakA=" + cycle.PeakCurrentA.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("picoSamples=" + (cycle.PicoRawAdc != null ? cycle.PicoRawAdc.Length : 0));
                sb.AppendLine("picoSampleRateHz=" + cycle.PicoSampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("picoResolutionBits=" + cycle.PicoResolutionBits);
                sb.AppendLine("picoFullScaleV=" + cycle.PicoFullScaleVolts.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("picoMaximumAdc=" + cycle.PicoMaximumAdc);
                sb.AppendLine("picoRawFile=" + stem + ".pico.raw.i16");
                sb.AppendLine("picoRangeIndex=" + cycle.PicoRangeIndexUsed);
                sb.AppendLine("suggestedRangeIndex=" + cycle.SuggestedPicoRangeIndex);
                sb.AppendLine("task6RangeSuitable=" + cycle.Task6RangeSuitable);
                sb.AppendLine("picoOverrangeOrClipped=" + cycle.PicoOverrangeOrClipped);
                if (!string.IsNullOrEmpty(cycle.Task6RangeMessage))
                    sb.AppendLine("task6RangeMessage=" + cycle.Task6RangeMessage);
                sb.AppendLine("firstZero_s=" + (double.IsNaN(cycle.FirstZeroTimeSeconds) ? "NaN" : cycle.FirstZeroTimeSeconds.ToString("G9", CultureInfo.InvariantCulture)));
                sb.AppendLine("dividerSamples=" + (cycle.UsbDividerRawVolts != null ? cycle.UsbDividerRawVolts.Length : 0));
                sb.AppendLine("usbSampleRateHz=" + cycle.UsbSampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("dischargeVerified=" + cycle.DischargeVerified);
                sb.AppendLine("error=" + (cycle.ErrorMessage ?? ""));
            }
            sb.AppendLine("aoZeroConfirmed=" + aoZero);
            if (metrics != null)
            {
                sb.AppendLine("frequencyStatus=" + metrics.FrequencyStatus);
                sb.AppendLine("oscillationFrequencyHz=" + (double.IsNaN(metrics.OscillationFrequencyHz) ? "NaN" : metrics.OscillationFrequencyHz.ToString("G9", CultureInfo.InvariantCulture)));
                sb.AppendLine("capacitanceStatus=" + metrics.CapacitanceStatus);
                sb.AppendLine("equivalentCapacitanceUf=" + (double.IsNaN(metrics.EquivalentCapacitanceUf) ? "NaN" : metrics.EquivalentCapacitanceUf.ToString("G9", CultureInfo.InvariantCulture)));
                sb.AppendLine("dischargeStatus=" + metrics.DischargeStatus);
                sb.AppendLine("apparentChargePc=" + (double.IsNaN(metrics.ApparentChargePc) ? "NaN" : metrics.ApparentChargePc.ToString("G9", CultureInfo.InvariantCulture)));
                sb.AppendLine("pdPeakVolts=" + (double.IsNaN(metrics.PdPeakVolts) ? "NaN" : metrics.PdPeakVolts.ToString("G9", CultureInfo.InvariantCulture)));
                sb.AppendLine("jointStatus=" + metrics.JointStatus);
                sb.AppendLine("metricsDiagnostic=" + metrics.Diagnostic);
            }
            File.WriteAllText(Path.Combine(waveDir, stem + ".meta.txt"), sb.ToString(), new UTF8Encoding(false));
        }


        /// <summary>
        /// 定位用满采样 Pico 原始 ADC（int16 LE）。禁止用 80k 预览做定位切片。
        /// 文件：stem.pico.raw.i16；标度见同 stem.meta.txt（picoSampleRateHz/picoFullScaleV/picoMaximumAdc）。
        /// </summary>
        static void WritePicoRawFullRate(string waveDir, string stem, AcquisitionCycleResult cycle)
        {
            if (cycle == null || cycle.PicoRawAdc == null || cycle.PicoRawAdc.Length == 0) return;
            string path = Path.Combine(waveDir, stem + ".pico.raw.i16");
            short[] adc = cycle.PicoRawAdc;
            var bytes = new byte[checked(adc.Length * 2)];
            Buffer.BlockCopy(adc, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
        }

        static void WritePreview(string waveDir, string stem, AcquisitionCycleResult cycle)
        {
            if (cycle == null) return;
            double picoHz = cycle.PicoSampleRateHz > 0 ? cycle.PicoSampleRateHz : 125000000;
            double artHz = cycle.UsbSampleRateHz > 0 ? cycle.UsbSampleRateHz : 80000;
            // Pico 定位预览目标 80k（≥20k 才能 10kHP）；AI4 振荡保持原生/接近原生，不做强制同 Fs 抽稀
            double picoTargetHz = PicoLocatePreviewRateHz;

            if (cycle.PicoRawAdc != null && cycle.PicoRawAdc.Length > 0)
            {
                int n = cycle.PicoRawAdc.Length;
                // stride 使 picoHz/stride ≈ picoTargetHz；|峰值|仓保持窄脉冲；窗内 UI 不再二次降采样
                int step = Math.Max(1, (int)Math.Round(picoHz / picoTargetHz));
                if (step < 1) step = 1;
                // 校准 Rapid 平均脉冲常仅数百点（µs 级）；80k stride 会压成 1 点 → 无法画图
                // 短波形保留全部分辨率
                if (n <= PicoPreviewMaxSamples)
                    step = 1;
                // 软上限：尽量不超过 PicoPreviewMaxSamples，但绝不可让有效 Fs < 20k（否则 10kHP 失效）
                int maxStepForMinFs = Math.Max(1, (int)Math.Floor(picoHz / PicoLocatePreviewMinRateHz));
                int approxOut = (n + step - 1) / step;
                if (approxOut > PicoPreviewMaxSamples)
                {
                    int stepForCap = Math.Max(step, (n + PicoPreviewMaxSamples - 1) / PicoPreviewMaxSamples);
                    // 若按软上限会掉到 <20k，则改用保 Fs 的最大 stride（点数可超过软上限）
                    step = stepForCap > maxStepForMinFs ? maxStepForMinFs : stepForCap;
                }
                if (step > maxStepForMinFs) step = maxStepForMinFs;
                double effectiveHz = picoHz / step;
                int writeCap = Math.Max(PicoPreviewMaxSamples, (n + step - 1) / step);
                // 硬顶防爆内存：最长约 2s@20k
                if (writeCap > 40000) writeCap = 40000;
                var sb = new StringBuilder();
                sb.AppendLine("# picoSampleRateHz=" + picoHz.ToString("G9", CultureInfo.InvariantCulture)
                    + " picoPreviewStride=" + step.ToString(CultureInfo.InvariantCulture)
                    + " picoEffectiveRateHz=" + effectiveHz.ToString("G9", CultureInfo.InvariantCulture)
                    + " picoLocatePreviewRateHz=" + picoTargetHz.ToString("G9", CultureInfo.InvariantCulture)
                    + " previewIndexMode=sequential"
                    + " picoResolutionBits=" + cycle.PicoResolutionBits
                    + " picoFullScaleV=" + cycle.PicoFullScaleVolts.ToString("G9", CultureInfo.InvariantCulture));
                sb.AppendLine("index,adc,approxVolts");
                double scale = cycle.PicoMaximumAdc > 0 ? cycle.PicoFullScaleVolts / cycle.PicoMaximumAdc : 0;
                int written = 0;
                for (int i = 0; i < n && written < writeCap; i += step)
                {
                    int end = Math.Min(n, i + step);
                    short peak = cycle.PicoRawAdc[i];
                    int peakAbs = Math.Abs((int)peak);
                    for (int k = i + 1; k < end; k++)
                    {
                        short v = cycle.PicoRawAdc[k];
                        int a = Math.Abs((int)v);
                        if (a > peakAbs) { peak = v; peakAbs = a; }
                    }
                    sb.Append(written).Append(',')
                        .Append(peak.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append((peak * scale).ToString("G6", CultureInfo.InvariantCulture))
                        .AppendLine();
                    written++;
                }
                File.WriteAllText(Path.Combine(waveDir, stem + ".pico.preview.csv"), sb.ToString(), new UTF8Encoding(false));
            }

            // AI4 分压器：振荡平滑曲线——尽量保留原生 80k（仅超点数上限才抽稀）；不用 Pico、不做峰值仓
            if (cycle.UsbDividerRawVolts != null && cycle.UsbDividerRawVolts.Length > 0)
            {
                var dsb = new StringBuilder();
                int dn = cycle.UsbDividerRawVolts.Length;
                int dstep = 1;
                if (dn > PreviewMaxSamples)
                    dstep = Math.Max(1, (dn + PreviewMaxSamples - 1) / PreviewMaxSamples);
                double artEff = artHz / dstep;
                dsb.AppendLine("# artSampleRateHz=" + artHz.ToString("G9", CultureInfo.InvariantCulture)
                    + " artEffectiveRateHz=" + artEff.ToString("G9", CultureInfo.InvariantCulture)
                    + " previewIndexMode=sequential");
                dsb.AppendLine("index,dividerRawV");
                int dw = 0;
                for (int i = 0; i < dn && dw < PreviewMaxSamples; i += dstep)
                {
                    dsb.Append(dw).Append(',')
                        .Append(cycle.UsbDividerRawVolts[i].ToString("G9", CultureInfo.InvariantCulture))
                        .AppendLine();
                    dw++;
                }
                File.WriteAllText(Path.Combine(waveDir, stem + ".ai4.preview.csv"), dsb.ToString(), new UTF8Encoding(false));
            }
        }

        static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            return v.ToString("G9", CultureInfo.InvariantCulture);
        }

        static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
