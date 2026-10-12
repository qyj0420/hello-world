using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace HVX.Report.Security
{
    // Compiled from the same source in the report engine and optional-appendix
    // consumer. Limits apply to untrusted templates, not acquisition records.
    internal static class BoundedDocx
    {
        internal const long MaxPackageBytes = 64L * 1024 * 1024;
        internal const long MaxExpandedBytes = 128L * 1024 * 1024;
        internal const long MaxXmlBytes = 4L * 1024 * 1024;
        internal const long MaxTotalXmlBytes = 16L * 1024 * 1024;
        internal const int MaxEntries = 2048;
        internal const int MaxNodes = 200000;
        internal const int MaxDepth = 128;
        internal const int MaxReadMilliseconds = 10000;

        internal static void ValidateFile(string path)
        {
            if (new FileInfo(path).Length > MaxPackageBytes)
                throw Limit("compressed package", MaxPackageBytes);
            using (var archive = ZipFile.OpenRead(path)) ValidateArchive(archive);
        }

        internal static void ValidateArchive(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxEntries) throw Limit("entry count", MaxEntries);
            long expanded = 0, xml = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                // Duplicate part names otherwise let different readers select
                // different contents from the same package.
                if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate DOCX part: " + entry.FullName);
                if (entry.Length > MaxExpandedBytes - expanded) throw Limit("expanded package", MaxExpandedBytes);
                expanded += entry.Length;
                if (!IsXml(entry.FullName)) continue;
                if (entry.Length > MaxXmlBytes) throw Limit("XML part " + entry.FullName, MaxXmlBytes);
                if (entry.Length > MaxTotalXmlBytes - xml) throw Limit("total XML", MaxTotalXmlBytes);
                xml += entry.Length;
            }
            // Templates are data, not permission to fetch a remote Office
            // template during PDF conversion. Ordinary hyperlinks remain valid.
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                XDocument relationships = LoadXml(archive, entry.FullName, LoadOptions.None);
                foreach (var relationship in relationships.Descendants())
                {
                    if (relationship.Name.LocalName != "Relationship") continue;
                    string type = ((string)relationship.Attribute("Type") ?? string.Empty).Trim();
                    if (!type.EndsWith("/attachedTemplate", StringComparison.OrdinalIgnoreCase)) continue;
                    string mode = ((string)relationship.Attribute("TargetMode") ?? string.Empty).Trim();
                    string target = ((string)relationship.Attribute("Target") ?? string.Empty).Trim();
                    Uri absolute;
                    if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase) ||
                        Uri.TryCreate(target, UriKind.Absolute, out absolute) || target.StartsWith("\\", StringComparison.Ordinal) ||
                        target.StartsWith("//", StringComparison.Ordinal))
                        throw new InvalidDataException("External attached templates are not supported. Remove the attached template relationship before generating or converting a report.");
                }
            }
        }

        internal static XDocument LoadXml(ZipArchive archive, string name, LoadOptions options)
        {
            var entry = archive.GetEntry(name);
            if (entry == null) throw new InvalidDataException("DOCX package part is missing: " + name);
            try { if (entry.Length > MaxXmlBytes) throw Limit("XML part " + name, MaxXmlBytes); }
            catch (InvalidOperationException)
            {
                // ZipArchive Update entries lose their Length metadata after
                // being rewritten. The actual-byte quota still applies below.
                if (archive.Mode != ZipArchiveMode.Update) throw;
            }
            var watch = Stopwatch.StartNew();
            // Check structure before allocating an XDocument. In particular, a
            // small XML file can still contain too many empty elements.
            using (var stream = new LimitedReadStream(entry.Open(), MaxXmlBytes, watch))
            using (var reader = XmlReader.Create(stream, Settings()))
            {
                int nodes = 0;
                while (reader.Read())
                {
                    if (++nodes > MaxNodes) throw Limit("XML node count", MaxNodes);
                    if (reader.Depth > MaxDepth) throw Limit("XML depth", MaxDepth);
                    CheckTime(watch);
                }
            }
            using (var stream = new LimitedReadStream(entry.Open(), MaxXmlBytes, watch))
            using (var reader = XmlReader.Create(stream, Settings()))
                return XDocument.Load(reader, options);
        }

        static XmlReaderSettings Settings()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes,
                MaxCharactersFromEntities = 1024, CloseInput = false };
        }
        static bool IsXml(string name)
        { return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase); }
        static InvalidDataException Limit(string what, long limit)
        { return new InvalidDataException("DOCX resource limit exceeded: " + what + " (limit " + limit + "). Use a smaller report template."); }
        static void CheckTime(Stopwatch watch)
        { if (watch.ElapsedMilliseconds > MaxReadMilliseconds) throw new InvalidDataException("DOCX XML read timed out. Use a smaller report template."); }

        // Metadata checks alone are insufficient: enforce the quota on actual
        // decompressed bytes as well. This stream owns only the entry stream.
        sealed class LimitedReadStream : Stream
        {
            readonly Stream inner; readonly long limit; readonly Stopwatch watch;
            long read;
            internal LimitedReadStream(Stream inner, long limit, Stopwatch watch)
            { this.inner = inner; this.limit = limit; this.watch = watch; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                CheckTime(watch);
                int requested = (int)Math.Min(count, limit - read + 1);
                int size = inner.Read(buffer, offset, requested);
                read += size;
                if (read > limit) throw Limit("decompressed XML", limit);
                CheckTime(watch);
                return size;
            }
            public override int ReadByte()
            { var single = new byte[1]; return Read(single, 0, 1) == 0 ? -1 : single[0]; }
            protected override void Dispose(bool disposing)
            { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { return read; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
