using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Xiongxin.OWTS.Desktop
{
    // Packaging/integrity, not encryption. Native libraries and helper processes
    // still require ordinary files. Never load an unverified external namesake
    // when this executable includes an embedded distribution.
    internal static class EmbeddedModulePayload
    {
        internal const string IndexResourceName = "HVX.Modules.Index.v1";
        const string Header = "XIONGXIN.Modules.v1";
        const string BlobPrefix = "HVX.Modules.Blob.";
        const long MaximumFileLength = 256L * 1024 * 1024;
        const long MaximumTotalLength = 1024L * 1024 * 1024;
        const int MaximumIndexLength = 2 * 1024 * 1024;
        sealed class Entry
        {
            internal string RelativePath, Hash, Resource;
            internal long Length;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct FileIdentity
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileIdentity info);
        static void RequireSingleLink(FileStream input, string path)
        {
            FileIdentity info;
            if (!GetFileInformationByHandle(input.SafeFileHandle, out info)) throw new IOException("Cannot verify module cache file identity: " + path);
            if (info.LinkCount != 1) throw new IOException("Hard-linked module cache files are not allowed: " + path);
        }
        internal static bool HasEmbeddedPayload
        { get { return typeof(EmbeddedModulePayload).Assembly.GetManifestResourceNames().Contains(IndexResourceName); } }

        internal static string ResolveModulesRoot()
        {
            Assembly owner = typeof(EmbeddedModulePayload).Assembly;
            if (!owner.GetManifestResourceNames().Contains(IndexResourceName))
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "modules"); // Source-built test hosts only.
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) throw new IOException("Windows local application-data directory is unavailable.");
            return ResolveModulesRoot(owner, Path.Combine(local, "XIONGXIN", "OWTS", "ModuleCache", "v1"));
        }

        // Explicit assembly/cache arguments allow isolated verification without
        // touching a real user's cache. Production callers use the no-arg entry.
        internal static string ResolveModulesRoot(Assembly owner, string cacheRoot)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            byte[] index = ReadIndex(owner);
            List<Entry> entries = ParseIndex(index);
            var names = new HashSet<string>(owner.GetManifestResourceNames(), StringComparer.Ordinal);
            var needed = new HashSet<string>(entries.Select(e => e.Resource), StringComparer.Ordinal);
            if (needed.Any(name => !names.Contains(name)) || names.Any(name => name.StartsWith(BlobPrefix, StringComparison.Ordinal) && !needed.Contains(name)))
                throw new InvalidDataException("Embedded module resource inventory does not match its index.");
            string root = SafeCacheRoot(cacheRoot), bundle = Hash(index);
            string version = Path.Combine(root, bundle), modules = Path.Combine(version, "modules");
            foreach (Entry entry in entries) ContainedFile(modules, entry.RelativePath);
            string lockName = "Local\\XIONGXIN.OWTS.Modules." + Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()));
            using (var mutex = new Mutex(false, lockName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Another process is preparing the software components. Please retry shortly.");
                    EnsureNoLinks(root);
                    Directory.CreateDirectory(root);
                    EnsureNoLinks(root);
                    if (Directory.Exists(version) || File.Exists(version))
                    {
                        VerifyVersion(version, entries, index);
                        return modules;
                    }
                    string pending = Path.Combine(root, ".pending-" + bundle.Substring(0, 12) + "-" + Guid.NewGuid().ToString("N"));
                    if (Directory.Exists(pending) || File.Exists(pending)) throw new IOException("Unexpected existing module staging path.");
                    string stagingModules = Path.Combine(pending, "modules");
                    foreach (Entry entry in entries) ContainedFile(stagingModules, entry.RelativePath);
                    Directory.CreateDirectory(stagingModules);
                    // Incomplete staging is deliberately retained for diagnostics;
                    // never recursively delete a directory supplied by a user.
                    foreach (Entry entry in entries)
                    {
                        EnsureNoLinks(stagingModules);
                        string destination = ContainedFile(stagingModules, entry.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        EnsureNoLinks(Path.GetDirectoryName(destination));
                        Extract(owner, entry, destination);
                    }
                    using (var receipt = new FileStream(Path.Combine(pending, "payload.index"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { receipt.Write(index, 0, index.Length); receipt.Flush(true); }
                    VerifyVersion(pending, entries, index);
                    EnsureNoLinks(root);
                    Directory.Move(pending, version); // Same-volume atomic publication.
                    VerifyVersion(version, entries, index);
                    return modules;
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        internal static void ValidateIndex(string text)
        { ParseIndex(new UTF8Encoding(false, true).GetBytes(text ?? "")); }

        static byte[] ReadIndex(Assembly owner)
        {
            using (Stream stream = owner.GetManifestResourceStream(IndexResourceName))
            {
                if (stream == null) throw new InvalidDataException("Embedded module index is missing.");
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaximumIndexLength) throw new InvalidDataException("Embedded module index exceeds its size limit.");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }
        static List<Entry> ParseIndex(byte[] index)
        {
            if (index.Length == 0 || index.Length > MaximumIndexLength) throw new InvalidDataException("Invalid embedded module index size.");
            string text = new UTF8Encoding(false, true).GetString(index);
            if (text.IndexOf('\r') >= 0 || !text.EndsWith("\n", StringComparison.Ordinal)) throw new InvalidDataException("Module index requires canonical LF records.");
            string[] lines = text.Split('\n');
            if (lines[0] != Header || lines.Length < 3 || lines.Length > 4098) throw new InvalidDataException("Invalid module index schema or file count.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, long>(StringComparer.Ordinal);
            var entries = new List<Entry>(); long total = 0;
            for (int line = 1; line < lines.Length - 1; line++)
            {
                string[] fields = lines[line].Split('\t'); long length;
                if (fields.Length != 4 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0 || length > MaximumFileLength)
                    throw new InvalidDataException("Invalid embedded module entry.");
                ValidateRelativePath(fields[0]);
                if (!paths.Add(fields[0])) throw new InvalidDataException("Duplicate embedded module path: " + fields[0]);
                string hash = fields[2];
                if (hash.Length != 64 || hash.Any(c => !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) || fields[3] != BlobPrefix + hash + ".gz")
                    throw new InvalidDataException("Invalid embedded module hash or resource name.");
                long previous;
                if (hashes.TryGetValue(hash, out previous) && previous != length) throw new InvalidDataException("Conflicting lengths for one module resource.");
                hashes[hash] = length; total += length;
                if (total > MaximumTotalLength) throw new InvalidDataException("Embedded module payload exceeds its expanded size limit.");
                entries.Add(new Entry { RelativePath = fields[0], Length = length, Hash = hash, Resource = fields[3] });
            }
            foreach (string path in paths)
            {
                int slash = path.IndexOf('/');
                while (slash >= 0)
                {
                    if (paths.Contains(path.Substring(0, slash))) throw new InvalidDataException("A module file collides with a directory.");
                    slash = path.IndexOf('/', slash + 1);
                }
            }
            return entries;
        }
        static void ValidateRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 220 || path.IndexOf('\\') >= 0 || Path.IsPathRooted(path))
                throw new InvalidDataException("Invalid relative module path.");
            string[] segments = path.Split('/');
            if (segments.Length < 2) throw new InvalidDataException("Module files require a named module directory.");
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Any(c => c < 32))
                    throw new InvalidDataException("Invalid module path component.");
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '0' && stem[3] <= '9')
                    throw new InvalidDataException("Reserved device name in module path.");
            }
        }
        static string SafeCacheRoot(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)) throw new ArgumentException("Use an absolute module cache directory.");
            string full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string drive = Path.GetPathRoot(full);
            if (full.Length <= drive.Length || full.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("Module cache must be a local subdirectory, not a volume or network root.");
            EnsureNoLinks(full); return full;
        }
        static string ContainedFile(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.Length >= 260)
                throw new IOException("Module cache path is outside its root or exceeds the supported Windows path length: " + full);
            return full;
        }
        static void EnsureNoLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked module cache paths are not allowed: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        static void Extract(Assembly owner, Entry entry, string destination)
        {
            using (Stream packed = owner.GetManifestResourceStream(entry.Resource))
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress, false))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; long total = 0; int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > entry.Length) throw new InvalidDataException("Expanded module length exceeds its manifest: " + entry.RelativePath);
                    sha.TransformBlock(buffer, 0, read, buffer, 0); output.Write(buffer, 0, read);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                if (total != entry.Length || Hex(sha.Hash) != entry.Hash) throw new InvalidDataException("Embedded module failed integrity verification: " + entry.RelativePath);
                output.Flush(true);
            }
        }
        static void VerifyVersion(string version, List<Entry> entries, byte[] index)
        {
            EnsureNoLinks(version);
            if (!Directory.Exists(version)) throw new IOException("Module cache is not a directory: " + version);
            string[] top = Directory.GetFileSystemEntries(version);
            if (top.Length != 2 || !File.Exists(Path.Combine(version, "payload.index")) || !Directory.Exists(Path.Combine(version, "modules")))
                throw new InvalidDataException("Module cache inventory is invalid. Do not use this cache: " + version);
            string receipt = Path.Combine(version, "payload.index"); EnsureNoLinks(receipt);
            using (var input = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                RequireSingleLink(input, receipt);
                if (input.Length != index.Length || Hash(input) != Hash(index)) throw new InvalidDataException("Module cache index was changed: " + version);
            }
            string modules = Path.Combine(version, "modules");
            var expected = entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            var expectedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
            foreach (Entry entry in entries)
            {
                int slash = entry.RelativePath.LastIndexOf('/');
                while (slash >= 0) { expectedDirs.Add(entry.RelativePath.Substring(0, slash)); slash = entry.RelativePath.LastIndexOf('/', slash - 1); }
            }
            int count = 0; var pending = new Stack<string>(); pending.Push(modules);
            while (pending.Count > 0)
            {
                string directory = pending.Pop(); EnsureNoLinks(directory);
                foreach (string item in Directory.GetFileSystemEntries(directory))
                {
                    EnsureNoLinks(item);
                    string relative = item.Substring(modules.Length + 1).Replace('\\', '/');
                    if (Directory.Exists(item))
                    {
                        if (!expectedDirs.Contains(relative)) throw new InvalidDataException("Unexpected directory in module cache: " + relative);
                        pending.Push(item); continue;
                    }
                    Entry entry;
                    if (!expected.TryGetValue(relative, out entry)) throw new InvalidDataException("Unexpected file in module cache: " + relative);
                    using (var input = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        RequireSingleLink(input, item);
                        if (input.Length != entry.Length || Hash(input) != entry.Hash) throw new InvalidDataException("Module cache file was changed: " + item);
                    }
                    count++;
                }
            }
            if (count != entries.Count) throw new InvalidDataException("Module cache contains missing files: " + version);
        }
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        static string Hash(Stream input) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(input)); }
        static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", ""); }
    }
}
