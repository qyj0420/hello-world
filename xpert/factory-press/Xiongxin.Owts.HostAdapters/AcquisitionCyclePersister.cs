using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 一轮采集落库：acquisition 行 + waveforms/*.meta.txt（channel=meta）
    /// + 可选 Pico 原始 ADC：≤MaxRawSamples 单文件 *.picoA.i16；
    ///   超限时默认分块旁路（FieldWaveformChunkStore）+ 降采样预览；
    ///   WriteChunkedWhenOversized=false 时保留 previewOnly 边界（只写预览）。
    /// </summary>
    public static class AcquisitionCyclePersister
    {
        /// <summary>超过此点数不写单文件完整 raw（避免 60 ms×125 MS/s 巨文件误落临时盘）。</summary>
        public const int MaxRawSamples = 2000000;
        public const int DefaultPreviewMaxSamples = 65536;

        public sealed class PersistOptions
        {
            public bool WriteRaw = true;
            /// <summary>单文件完整 raw 上限；默认 MaxRawSamples。Smoke 可缩短以模拟 Field。</summary>
            public int MaxRawSamplesLimit = MaxRawSamples;
            /// <summary>分块大小；默认 FieldWaveformChunkStore.DefaultChunkSamples。Smoke 可缩短。</summary>
            public int ChunkSamples = FieldWaveformChunkStore.DefaultChunkSamples;
            /// <summary>超限时是否分块写完整 raw；false → 仅 previewOnly（旧边界）。</summary>
            public bool WriteChunkedWhenOversized = true;
            public int PreviewMaxSamples = DefaultPreviewMaxSamples;
        }

        public static Guid Save(IProjectRepository repository, string projectDir, AcquisitionCycleResult cycle, double targetVoltageV, string phase)
        {
            return Save(repository, projectDir, cycle, targetVoltageV, phase, new PersistOptions());
        }

        public static Guid Save(IProjectRepository repository, string projectDir, AcquisitionCycleResult cycle, double targetVoltageV, string phase, bool writeRaw)
        {
            return Save(repository, projectDir, cycle, targetVoltageV, phase, new PersistOptions { WriteRaw = writeRaw });
        }

        public static Guid Save(IProjectRepository repository, string projectDir, AcquisitionCycleResult cycle, double targetVoltageV, string phase, PersistOptions options)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            if (cycle == null) throw new ArgumentNullException("cycle");
            if (options == null) options = new PersistOptions();

            string root = string.IsNullOrWhiteSpace(projectDir) ? Path.GetTempPath() : projectDir;
            Directory.CreateDirectory(Path.Combine(root, "waveforms"));
            string stem = "acq-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string metaRel = "waveforms/" + stem + ".meta.txt";
            int n = cycle.PicoRawAdc != null ? cycle.PicoRawAdc.Length : 0;
            int maxRaw = options.MaxRawSamplesLimit > 0 ? options.MaxRawSamplesLimit : MaxRawSamples;
            int previewMax = options.PreviewMaxSamples > 0 ? options.PreviewMaxSamples : DefaultPreviewMaxSamples;
            int chunkSamples = options.ChunkSamples > 0 ? options.ChunkSamples : FieldWaveformChunkStore.DefaultChunkSamples;

            var sb = new StringBuilder();
            sb.AppendLine("exit=" + cycle.ExitReason);
            sb.AppendLine("success=" + cycle.Success);
            sb.AppendLine("targetV=" + targetVoltageV.ToString("G6", CultureInfo.InvariantCulture));
            sb.AppendLine("peakV=" + cycle.PeakActualVoltageV.ToString("G6", CultureInfo.InvariantCulture));
            sb.AppendLine("peakA=" + cycle.PeakCurrentA.ToString("G6", CultureInfo.InvariantCulture));
            sb.AppendLine("picoSamples=" + n);
            sb.AppendLine("picoSampleRateHz=" + cycle.PicoSampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("picoSampleIntervalNs=" + cycle.PicoSampleIntervalNanoseconds.ToString("G6", CultureInfo.InvariantCulture));
            sb.AppendLine("picoFullScaleV=" + cycle.PicoFullScaleVolts.ToString("G6", CultureInfo.InvariantCulture));
            sb.AppendLine("picoMaxAdc=" + cycle.PicoMaximumAdc);
            sb.AppendLine("picoRangeIndex=" + cycle.PicoRangeIndexUsed);
            sb.AppendLine("picoResolutionBits=" + cycle.PicoResolutionBits);
            sb.AppendLine("picoSerial=" + (cycle.PicoSerial ?? ""));
            sb.AppendLine("suggestedRangeIndex=" + cycle.SuggestedPicoRangeIndex);
            sb.AppendLine("task6RangeSuitable=" + cycle.Task6RangeSuitable);
            sb.AppendLine("picoOverrangeOrClipped=" + cycle.PicoOverrangeOrClipped);
            if (!string.IsNullOrEmpty(cycle.Task6RangeMessage))
                sb.AppendLine("task6RangeMessage=" + cycle.Task6RangeMessage);
            sb.AppendLine("firstZero_s=" + (double.IsNaN(cycle.FirstZeroTimeSeconds) ? "NaN" : cycle.FirstZeroTimeSeconds.ToString("G9", CultureInfo.InvariantCulture)));
            sb.AppendLine("dividerSamples=" + (cycle.UsbDividerRawVolts != null ? cycle.UsbDividerRawVolts.Length : 0));
            sb.AppendLine("usbSampleRateHz=" + cycle.UsbSampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("dischargeVerified=" + cycle.DischargeVerified);
            sb.AppendLine("dischargeMsg=" + (cycle.DischargeStatusMessage ?? ""));
            sb.AppendLine("error=" + (cycle.ErrorMessage ?? string.Empty));
            File.WriteAllText(Path.Combine(root, metaRel), sb.ToString());

            string json = "{\"exit\":\"" + cycle.ExitReason + "\",\"success\":" + (cycle.Success ? "true" : "false")
                + ",\"peakV\":" + cycle.PeakActualVoltageV.ToString(CultureInfo.InvariantCulture)
                + ",\"targetV\":" + targetVoltageV.ToString(CultureInfo.InvariantCulture)
                + ",\"picoSamples\":" + n
                + ",\"picoRangeIndex\":" + cycle.PicoRangeIndexUsed
                + ",\"picoFullScaleV\":" + cycle.PicoFullScaleVolts.ToString(CultureInfo.InvariantCulture) + "}";

            Guid acqId = repository.InsertAcquisition(new AcquisitionRecordDto
            {
                Phase = phase ?? string.Empty,
                TargetVoltageKv = targetVoltageV / 1000.0,
                CapturedUtc = cycle.CompletedUtc == default(DateTime) ? DateTime.UtcNow : cycle.CompletedUtc,
                ArtSerial = cycle.ArtSerial ?? string.Empty,
                PicoSerial = cycle.PicoSerial ?? string.Empty,
                ExitReason = cycle.ExitReason.ToString(),
                CycleReferenceJson = json
            }, new[] { metaRel });

            if (options.WriteRaw && n > 0 && n <= maxRaw)
            {
                string rawRel = "waveforms/" + stem + ".picoA.i16";
                using (var fs = new FileStream(Path.Combine(root, rawRel), FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    foreach (short s in cycle.PicoRawAdc) bw.Write(s);
                }
                repository.InsertWaveformFile(acqId, "picoA", rawRel, cycle.PicoSampleRateHz, n);
            }
            // 完整 raw 也写一份定位预览（出厂始终有 preview；供 10kHP / 快速浏览）
            if (options.WriteRaw && n > 0 && n <= maxRaw)
            {
                WritePicoPreview(root, stem, cycle.PicoRawAdc, n, previewMax, acqId, repository, cycle.PicoSampleRateHz);
            }
            else if (options.WriteRaw && n > maxRaw)
            {
                // 始终写降采样预览（报告 SVG / 快速浏览）；previewOnly 边界保留
                int writtenPreview = WritePicoPreview(root, stem, cycle.PicoRawAdc, n, previewMax, acqId, repository, cycle.PicoSampleRateHz);

                if (options.WriteChunkedWhenOversized)
                {
                    FieldWaveformChunkStore.WriteResult chunks = FieldWaveformChunkStore.WriteInt16Chunks(
                        repository, root, acqId, stem, cycle.PicoRawAdc, cycle.PicoSampleRateHz, chunkSamples);
                    File.AppendAllText(Path.Combine(root, metaRel),
                        "rawPolicy=chunked;reason=exceeds MaxRawSamples=" + maxRaw
                        + ";fullSamples=" + n
                        + ";chunkSamples=" + chunks.ChunkSamples
                        + ";chunkCount=" + chunks.ChunkCount
                        + ";previewSamples=" + writtenPreview
                        + ";manifest=" + (chunks.ManifestRelPath ?? "")
                        + Environment.NewLine);
                }
                else
                {
                    File.AppendAllText(Path.Combine(root, metaRel),
                        "rawPolicy=previewOnly;reason=exceeds MaxRawSamples=" + maxRaw
                        + ";fullSamples=" + n
                        + ";previewSamples=" + writtenPreview
                        + Environment.NewLine);
                }
            }

            int nd = cycle.UsbDividerRawVolts != null ? cycle.UsbDividerRawVolts.Length : 0;
            if (options.WriteRaw && nd > 0 && nd <= maxRaw)
            {
                string divRel = "waveforms/" + stem + ".ai4.f64";
                using (var fs = new FileStream(Path.Combine(root, divRel), FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    foreach (double v in cycle.UsbDividerRawVolts) bw.Write(v);
                }
                repository.InsertWaveformFile(acqId, "ai4", divRel, cycle.UsbSampleRateHz, nd);
            }
            return acqId;
        }

        /// <summary>
        /// 出厂 WaveformSummaryWriter 同规则：目标 80 kHz、下限 ≥20 kHz、峰值仓；禁盲目 n/previewMax 抽稀砸穿 10kHP。
        /// </summary>
        static int WritePicoPreview(
            string root,
            string stem,
            short[] adc,
            int n,
            int previewMax,
            Guid acqId,
            IProjectRepository repository,
            double sampleRateHz)
        {
            if (adc == null || n <= 0) return 0;
            double eff;
            int step = FactoryLocateExtract.ComputeLocatePreviewStride(n, sampleRateHz, out eff);
            // Soft cap from options.PreviewMaxSamples，但不可让 Fs < 20k
            int maxStepForMinFs = Math.Max(1, (int)Math.Floor((sampleRateHz > 0 ? sampleRateHz : 125e6) / FactoryLocateExtract.PicoLocatePreviewMinRateHz));
            if (previewMax > 0)
            {
                int approx = (n + step - 1) / step;
                if (approx > previewMax)
                {
                    int stepForCap = Math.Max(step, (n + previewMax - 1) / previewMax);
                    step = stepForCap > maxStepForMinFs ? maxStepForMinFs : stepForCap;
                    eff = (sampleRateHz > 0 ? sampleRateHz : 125e6) / step;
                }
            }
            int writeCap = Math.Max(FactoryLocateExtract.PicoPreviewMaxSamples, (n + step - 1) / step);
            if (writeCap > FactoryLocateExtract.HardCapSamples) writeCap = FactoryLocateExtract.HardCapSamples;

            string prevRel = "waveforms/" + stem + ".picoA.preview.i16";
            int written = 0;
            using (var fs = new FileStream(Path.Combine(root, prevRel), FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                for (int i = 0; i < n && written < writeCap; i += step)
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
                    bw.Write(peak);
                    written++;
                }
            }
            repository.InsertWaveformFile(acqId, "picoA.preview", prevRel, eff, written);
            // 旁路 meta 追加有效 Fs，便于定位回读核对
            try
            {
                string metaPath = Path.Combine(root, "waveforms/" + stem + ".meta.txt");
                if (File.Exists(metaPath))
                {
                    File.AppendAllText(metaPath,
                        "picoPreviewStride=" + step.ToString(CultureInfo.InvariantCulture)
                        + Environment.NewLine
                        + "picoEffectiveRateHz=" + eff.ToString("G9", CultureInfo.InvariantCulture)
                        + Environment.NewLine
                        + "picoLocatePreviewRateHz=" + FactoryLocateExtract.PicoLocatePreviewRateHz.ToString("G9", CultureInfo.InvariantCulture)
                        + Environment.NewLine
                        + "previewIndexMode=peakHold"
                        + Environment.NewLine);
                }
            }
            catch { /* meta 追加失败不阻断落库 */ }
            return written;
        }
    }
}
