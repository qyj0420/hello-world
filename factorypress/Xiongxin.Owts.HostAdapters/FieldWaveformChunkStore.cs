using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using Xiongxin.Owts.Contracts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// Field 超大 Pico raw：分块旁路落盘 + memory-mapped 按块读，避免整段二次缓冲占死。
    /// 不替代 previewOnly：调用方仍可只写预览。
    /// </summary>
    public static class FieldWaveformChunkStore
    {
        public const int DefaultChunkSamples = 500000;
        public const string ManifestChannel = "picoA.chunks";
        public const string ChunkChannelPrefix = "picoA.chunk";

        public sealed class WriteResult
        {
            public int FullSamples;
            public int ChunkSamples;
            public int ChunkCount;
            public string ManifestRelPath;
            public readonly List<ChunkEntry> Chunks = new List<ChunkEntry>();
        }

        public sealed class ChunkEntry
        {
            public int Index;
            public string Channel;
            public string RelPath;
            public int SampleCount;
        }

        public sealed class Manifest
        {
            public int FullSamples;
            public int ChunkSamples;
            public int ChunkCount;
            public readonly List<string> ChunkRelPaths = new List<string>();
        }

        /// <summary>
        /// 按块流式写 int16 LE；不分配第二份全长数组。登记 waveform_file（manifest + 每块）。
        /// </summary>
        public static WriteResult WriteInt16Chunks(
            IProjectRepository repository,
            string projectRoot,
            Guid acquisitionId,
            string stem,
            short[] samples,
            double sampleRateHz,
            int chunkSamples)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            if (samples == null || samples.Length == 0) throw new ArgumentException("samples");
            if (chunkSamples <= 0) throw new ArgumentOutOfRangeException("chunkSamples");

            string root = string.IsNullOrWhiteSpace(projectRoot) ? Path.GetTempPath() : projectRoot;
            Directory.CreateDirectory(Path.Combine(root, "waveforms"));
            if (string.IsNullOrWhiteSpace(stem)) stem = "acq-chunk-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var result = new WriteResult
            {
                FullSamples = samples.Length,
                ChunkSamples = chunkSamples
            };

            int offset = 0;
            int index = 0;
            while (offset < samples.Length)
            {
                int n = Math.Min(chunkSamples, samples.Length - offset);
                string rel = "waveforms/" + stem + ".picoA.chunk" + index.ToString("D3", CultureInfo.InvariantCulture) + ".i16";
                string abs = Path.Combine(root, rel);
                WriteInt16Range(abs, samples, offset, n);
                string channel = ChunkChannelPrefix + index.ToString(CultureInfo.InvariantCulture);
                repository.InsertWaveformFile(acquisitionId, channel, rel, sampleRateHz, n);
                result.Chunks.Add(new ChunkEntry
                {
                    Index = index,
                    Channel = channel,
                    RelPath = rel,
                    SampleCount = n
                });
                offset += n;
                index++;
            }

            result.ChunkCount = index;
            string manRel = "waveforms/" + stem + ".picoA.chunks.txt";
            WriteManifest(Path.Combine(root, manRel), result);
            repository.InsertWaveformFile(acquisitionId, ManifestChannel, manRel, sampleRateHz, result.FullSamples);
            result.ManifestRelPath = manRel;
            return result;
        }

        /// <summary>memory-mapped 读一块 int16（仅该块进托管堆，非整段 Field）。</summary>
        public static short[] ReadInt16ChunkMapped(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
                throw new FileNotFoundException("chunk missing", absolutePath);

            long byteLen = new FileInfo(absolutePath).Length;
            if (byteLen <= 0) return new short[0];
            if ((byteLen & 1) != 0) throw new InvalidDataException("int16 file length not even: " + absolutePath);
            int samples = (int)(byteLen / 2);
            if (samples <= 0) return new short[0];

            var output = new short[samples];
            using (var mmf = MemoryMappedFile.CreateFromFile(absolutePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, byteLen, MemoryMappedFileAccess.Read))
            {
                for (int i = 0; i < samples; i++)
                    output[i] = accessor.ReadInt16(i * 2L);
            }
            return output;
        }

        /// <summary>按全局样本下标读一个 short（打开对应块的 mmap，用完即释）。</summary>
        public static short ReadSampleMapped(string projectRoot, Manifest manifest, int globalIndex)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            if (globalIndex < 0 || globalIndex >= manifest.FullSamples)
                throw new ArgumentOutOfRangeException("globalIndex");
            int chunkSamples = manifest.ChunkSamples > 0 ? manifest.ChunkSamples : DefaultChunkSamples;
            int chunkIndex = globalIndex / chunkSamples;
            int local = globalIndex % chunkSamples;
            if (chunkIndex < 0 || chunkIndex >= manifest.ChunkRelPaths.Count)
                throw new InvalidOperationException("chunk index out of manifest");
            string abs = Path.Combine(projectRoot ?? "", manifest.ChunkRelPaths[chunkIndex]);
            using (var mmf = MemoryMappedFile.CreateFromFile(abs, FileMode.Open, null, 0, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
            {
                return accessor.ReadInt16(local * 2L);
            }
        }

        public static Manifest ParseManifest(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
                throw new FileNotFoundException("manifest missing", absolutePath);
            var m = new Manifest();
            foreach (string line in File.ReadAllLines(absolutePath))
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (key == "fullSamples") int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out m.FullSamples);
                else if (key == "chunkSamples") int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out m.ChunkSamples);
                else if (key == "chunkCount") int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out m.ChunkCount);
                else if (key.StartsWith("chunk", StringComparison.OrdinalIgnoreCase) && key != "chunkSamples" && key != "chunkCount")
                    m.ChunkRelPaths.Add(val.Replace('\\', '/'));
            }
            if (m.ChunkCount == 0) m.ChunkCount = m.ChunkRelPaths.Count;
            return m;
        }

        static void WriteManifest(string absolutePath, WriteResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("format=picoA.i16.chunks");
            sb.AppendLine("fullSamples=" + result.FullSamples.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("chunkSamples=" + result.ChunkSamples.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("chunkCount=" + result.ChunkCount.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < result.Chunks.Count; i++)
                sb.AppendLine("chunk" + i.ToString(CultureInfo.InvariantCulture) + "=" + result.Chunks[i].RelPath);
            File.WriteAllText(absolutePath, sb.ToString());
        }

        static void WriteInt16Range(string absolutePath, short[] samples, int offset, int count)
        {
            using (var fs = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var bw = new BinaryWriter(fs))
            {
                for (int i = 0; i < count; i++)
                    bw.Write(samples[offset + i]);
            }
        }
    }
}
