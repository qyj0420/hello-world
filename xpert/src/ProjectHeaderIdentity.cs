using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using HVX.ProjectCables;

namespace Xiongxin.OWTS.Desktop
{
    // Display-only, bounded metadata reader. It never rewrites old project names
    // or needs the analysis/report modules to identify a loaded project.
    internal sealed class ProjectHeaderIdentity
    {
        string cachedPath;
        DateTime cachedWrite;
        long cachedLength = -1;
        internal string StartName { get; private set; }
        internal string EndName { get; private set; }
        internal int ReadCount { get; private set; }
        string cachedCablePath;
        DateTime cachedCableWrite;
        long cachedCableLength = -1;
        string[] lengthLabels = new string[0];
        double[] cableLengths = new double[0];
        internal int CableReadCount { get; private set; }

        internal void Invalidate() { cachedPath = cachedCablePath = null; }
        internal void Load(string project)
        {
            if (String.IsNullOrWhiteSpace(project)) {
                Invalidate(); StartName = EndName = String.Empty;
                lengthLabels = new string[0]; cableLengths = new double[0]; return;
            }
            // Geometry has its own change key: a joint edit can change CABLE.in
            // without touching ITEM.in. Invalid geometry is never guessed from a
            // timestamp or another phase and is retried only when files change.
            LoadLengths(project);
            try
            {
                string path = Path.Combine(Path.GetFullPath(project), "ITEM.in");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 2 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                { Invalidate(); StartName = EndName = String.Empty; return; }
                if (String.Equals(cachedPath, path, StringComparison.OrdinalIgnoreCase) && cachedWrite == info.LastWriteTimeUtc && cachedLength == info.Length) return;
                string start = String.Empty, end = String.Empty;
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Project header too large.");
                    bytes = new byte[(int)stream.Length]; int offset = 0, count;
                    while (offset < bytes.Length && (count = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += count;
                    if (offset != bytes.Length) throw new IOException("Project header changed during reading.");
                }
                ReadCount++;
                foreach (string raw in Decode(bytes).Replace("\r", "\n").Split('\n'))
                {
                    int equals = raw.IndexOf('='); if (equals < 0) continue;
                    string key = raw.Substring(0, equals).Trim().Trim('\uFEFF').TrimEnd(':', '：').Trim();
                    string value = Clean(raw.Substring(equals + 1));
                    switch (key.Replace("_", "").Replace(" ", "").ToLowerInvariant())
                    {
                        case "始端地点": case "始端地點": case "始端名稱": case "始端名称": case "始端": case "起点地点": case "起點地點": case "startname": case "cable.startname": start = value; break;
                        case "终端地点": case "終端地點": case "终端名称": case "終端名稱": case "终端": case "終端": case "终点地点": case "末端名称": case "末端名稱": case "endname": case "cable.endname": end = value; break;
                    }
                }
                StartName = start; EndName = end;
                cachedPath = path; cachedLength = info.Length; cachedWrite = info.LastWriteTimeUtc;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is System.Security.SecurityException)
            { Invalidate(); StartName = EndName = String.Empty; }
        }

        void LoadLengths(string project)
        {
            try {
                string path = Path.Combine(Path.GetFullPath(project), "CABLE.in");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 2 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                { cachedCablePath = null; cableLengths = new double[0]; lengthLabels = new string[0]; return; }
                if (String.Equals(cachedCablePath, path, StringComparison.OrdinalIgnoreCase) &&
                    cachedCableWrite == info.LastWriteTimeUtc && cachedCableLength == info.Length) return;
                cableLengths = new double[0]; lengthLabels = new string[0];
                cachedCablePath = path; cachedCableWrite = info.LastWriteTimeUtc; cachedCableLength = info.Length;
                CableReadCount++;
                var snapshot = CableJointStore.ReadGeometry(project);
                cableLengths = snapshot.Blocks.Select(b => b.Length).ToArray();
                lengthLabels = snapshot.Blocks.Select(b => b.Phase +
                    (snapshot.Blocks.Count(other => other.Phase == b.Phase) > 1 ? "#" + (b.Index + 1).ToString(CultureInfo.InvariantCulture) : "")).ToArray();
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is System.Security.SecurityException)
            { cableLengths = new double[0]; lengthLabels = new string[0]; }
        }

        internal string LengthCaption(bool english)
        {
            string prefix = english ? "Cable length: " : "电缆长度：";
            if (cableLengths.Length == 0) return prefix + "—";
            // Only collapse equal lengths; A/B/C of different lengths must not be
            // described by their maximum as though it applied to every phase.
            if (cableLengths.All(n => Math.Abs(n - cableLengths[0]) < .0000001))
                return prefix + cableLengths[0].ToString("0.0", CultureInfo.InvariantCulture) + " m";
            return prefix + String.Join(" / ", cableLengths.Select((n, i) => lengthLabels[i] + " " + n.ToString("0.0", CultureInfo.InvariantCulture) + " m"));
        }

        static string Clean(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
            value = value.Replace("\\\"", "\"").Replace("\\r", " ").Replace("\\n", " ").Replace("\\t", " ");
            var text = new StringBuilder();
            foreach (char c in value) if (!Char.IsControl(c)) { text.Append(c); if (text.Length == 500) break; }
            return text.ToString().Trim();
        }
        static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254) return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255) return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0) return new UnicodeEncoding(false, false, true).GetString(bytes);
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[2] == 0) return new UnicodeEncoding(true, false, true).GetString(bytes);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(936).GetString(bytes); }
        }
    }
}
