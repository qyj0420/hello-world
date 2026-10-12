using System;
using System.IO;
using System.Text;

namespace HVX.Report.Security
{
    // Shared by the OOXML engine and the Office/WPS adapter. Never remove a
    // retained input or the previous completed report before publication.
    internal static class ReportOutputSafety
    {
        internal static void EnsureDifferent(string input, string output)
        {
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The report output must be different from its input and template.");
        }

        internal static void Publish(string staged, string output, bool overwrite)
        {
            EnsureDifferent(staged, output);
            // File.Replace is atomic on the supported local Windows filesystem.
            // Unsupported filesystems must report failure, not delete the old
            // report and retry with a destructive non-atomic fallback.
            if (File.Exists(output))
            {
                if (!overwrite) throw new IOException("Report output already exists.");
                File.Replace(staged, output, null);
            }
            else File.Move(staged, output);
        }

        internal static void ValidatePdfEnvelope(string path)
        {
            // A cheap publication preflight, not a complete PDF parser or a
            // claim about rendering fidelity. Reject empty/truncated/error files.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 12) throw new InvalidDataException("The converter did not produce a complete PDF file.");
                var header = new byte[5];
                if (stream.Read(header, 0, header.Length) != header.Length || Encoding.ASCII.GetString(header) != "%PDF-")
                    throw new InvalidDataException("The converter output is not a PDF file.");
                int count = (int)Math.Min(4096, stream.Length);
                stream.Position = stream.Length - count;
                var tail = new byte[count]; int total = 0;
                while (total < count) { int read = stream.Read(tail, total, count - total); if (read == 0) break; total += read; }
                if (Encoding.ASCII.GetString(tail, 0, total).IndexOf("%%EOF", StringComparison.Ordinal) < 0)
                    throw new InvalidDataException("The converter PDF output is incomplete.");
            }
        }
    }
}
