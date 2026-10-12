using System;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 导入/仿真高速波形落盘：eng.f64（可绘图）+ 可选 hs.zlib（可重放压缩通道），
    /// 登记 waveform_file 并关联 acquisition。
    /// </summary>
    public static class MeasurementWaveformPersister
    {
        public const int MaxEngSamples = 2000000;

        /// <summary>
        /// 新建一条 acquisition，写入工程波形旁路文件，返回 acquisitionId。
        /// compressedHsZlib 非空时额外落 .hs.zlib（原样可重放）；为空则回退 wave.CompressedHsZlib（.dac 导入自动附 blob）。
        /// </summary>
        public static Guid SaveImported(
            IProjectRepository repository,
            string projectDir,
            DacMeasurementWaveform wave,
            byte[] compressedHsZlib,
            string phase)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            if (wave == null) throw new ArgumentNullException("wave");

            string root = string.IsNullOrWhiteSpace(projectDir) ? Path.GetTempPath() : projectDir;
            Directory.CreateDirectory(Path.Combine(root, "waveforms"));
            string stem = "imp-m" + wave.MeasurementId.ToString(CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string metaRel = "waveforms/" + stem + ".meta.txt";

            var sb = new StringBuilder();
            sb.AppendLine("source=import");
            sb.AppendLine("measurementId=" + wave.MeasurementId.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("containerId=" + wave.ContainerId.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("channel=" + wave.Channel.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("sampleRateHz=" + wave.SampleRateHz.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("engSamples=" + (wave.EngineeringSamples != null ? wave.EngineeringSamples.Length : 0));
            sb.AppendLine("gain=" + wave.Gain.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("offset=" + wave.Offset.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("calFactorLoc=" + wave.CalibrationFactorLoc.ToString("G9", CultureInfo.InvariantCulture));
            sb.AppendLine("hvMaxKv=" + (double.IsNaN(wave.HvMaxKv) ? "" : wave.HvMaxKv.ToString("G6", CultureInfo.InvariantCulture)));
            File.WriteAllText(Path.Combine(root, metaRel), sb.ToString());

            string json = "{\"source\":\"import\",\"measurementId\":" + wave.MeasurementId.ToString(CultureInfo.InvariantCulture)
                + ",\"sampleRateHz\":" + wave.SampleRateHz.ToString(CultureInfo.InvariantCulture)
                + ",\"engSamples\":" + (wave.EngineeringSamples != null ? wave.EngineeringSamples.Length : 0) + "}";

            Guid acqId = repository.InsertAcquisition(new AcquisitionRecordDto
            {
                Phase = phase ?? PhaseLabel(wave.Phase),
                TargetVoltageKv = double.IsNaN(wave.HvMaxKv) ? 0 : wave.HvMaxKv,
                CapturedUtc = DateTime.UtcNow,
                ExitReason = "Imported",
                CycleReferenceJson = json
            }, new[] { metaRel });

            int n = wave.EngineeringSamples != null ? wave.EngineeringSamples.Length : 0;
            if (n > 0 && n <= MaxEngSamples)
            {
                string engRel = "waveforms/" + stem + ".eng.f64";
                WriteFloat64Le(Path.Combine(root, engRel), wave.EngineeringSamples);
                repository.InsertWaveformFile(acqId, "eng", engRel, wave.SampleRateHz, n);
            }
            else if (n > MaxEngSamples)
            {
                const int PreviewMax = 65536;
                int step = Math.Max(1, n / PreviewMax);
                string prevRel = "waveforms/" + stem + ".eng.preview.f64";
                int written = 0;
                using (var fs = new FileStream(Path.Combine(root, prevRel), FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    for (int i = 0; i < n && written < PreviewMax; i += step)
                    {
                        bw.Write(wave.EngineeringSamples[i]);
                        written++;
                    }
                }
                repository.InsertWaveformFile(acqId, "eng.preview", prevRel, wave.SampleRateHz / step, written);
                File.AppendAllText(Path.Combine(root, metaRel),
                    "engPolicy=previewOnly;fullSamples=" + n + ";previewSamples=" + written + Environment.NewLine);
            }

            byte[] zlibBlob = compressedHsZlib;
            if ((zlibBlob == null || zlibBlob.Length == 0) && wave.CompressedHsZlib != null && wave.CompressedHsZlib.Length > 0)
                zlibBlob = wave.CompressedHsZlib;
            if (zlibBlob != null && zlibBlob.Length > 0)
            {
                string zlibRel = "waveforms/" + stem + ".hs.zlib";
                File.WriteAllBytes(Path.Combine(root, zlibRel), zlibBlob);
                repository.InsertWaveformFile(acqId, "hs.zlib", zlibRel, wave.SampleRateHz, n);
            }

            return acqId;
        }

        /// <summary>在已有 acquisition 上追加一段工程波形（仿真/二次通道）。</summary>
        public static Guid AttachEngineering(
            IProjectRepository repository,
            string projectDir,
            Guid acquisitionId,
            string channel,
            double[] samples,
            double sampleRateHz)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            if (acquisitionId == Guid.Empty) throw new ArgumentException("acquisitionId");
            if (samples == null || samples.Length == 0) throw new ArgumentException("samples");

            string root = string.IsNullOrWhiteSpace(projectDir) ? Path.GetTempPath() : projectDir;
            Directory.CreateDirectory(Path.Combine(root, "waveforms"));
            string ch = string.IsNullOrWhiteSpace(channel) ? "eng" : channel.Trim();
            string stem = "att-" + ch + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            int n = samples.Length;
            if (n > MaxEngSamples)
                throw new InvalidOperationException("eng samples exceed MaxEngSamples=" + MaxEngSamples);

            string rel = "waveforms/" + stem + ".eng.f64";
            WriteFloat64Le(Path.Combine(root, rel), samples);
            return repository.InsertWaveformFile(acquisitionId, ch, rel, sampleRateHz, n);
        }

        static void WriteFloat64Le(string path, double[] samples)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                for (int i = 0; i < samples.Length; i++)
                    bw.Write(samples[i]);
            }
        }

        static string PhaseLabel(int phase)
        {
            if (phase == 0) return "A";
            if (phase == 1) return "B";
            if (phase == 2) return "C";
            return phase.ToString(CultureInfo.InvariantCulture);
        }
    }
}
