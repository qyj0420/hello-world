using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    internal interface IProjectSession : IDisposable
    {
        Guid ProjectId { get; }
        long Revision { get; }
        string[] ListFiles();
        byte[] ReadMetadata(string logicalPath);
        void ReadFile(string logicalPath, Stream destination);
        Task<long> CommitAsync(IEnumerable<ProjectFileChange> changes, long expectedRevision, CancellationToken cancellation);
    }

    internal sealed class ProjectFileChange
    {
        internal readonly string Path;
        internal readonly ProjectRecordKind Kind;
        internal readonly byte[] Data;
        internal readonly Func<Stream> OpenStream;
        internal readonly bool Remove;
        internal ProjectFileChange(string path, ProjectRecordKind kind, byte[] data)
        {
            Path = SecureProjectSession.ValidateName(path); Kind = kind;
            if (data == null || data.Length == 0 || data.Length > 4 * 1024 * 1024) throw new ArgumentException("项目元数据长度无效。");
            Data = (byte[])data.Clone();
        }
        internal ProjectFileChange(string path, ProjectRecordKind kind, Func<Stream> openStream)
        { Path = SecureProjectSession.ValidateName(path); Kind = kind; OpenStream = openStream ?? throw new ArgumentNullException("openStream"); }
        private ProjectFileChange(string path) { Path = SecureProjectSession.ValidateName(path); Remove = true; }
        internal static ProjectFileChange Delete(string path) { return new ProjectFileChange(path); }
    }

    // An authenticated manifest is the sole business publication point. Earlier
    // appended records remain invisible if cancellation or a crash precedes it.
    internal sealed class SecureProjectSession : IProjectSession
    {
        const int MaximumFiles = 4096, MaximumRevisions = 10000;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HXREV001");
        sealed class Entry
        {
            internal string Name; internal Guid Id; internal ProjectRecordKind Kind;
            internal bool Stream; internal long Length; internal byte[] Hash;
        }
        sealed class Head
        {
            internal Guid Id, Previous; internal long Sequence;
            internal Dictionary<string, Entry> Files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
        readonly SecureProjectStore store;
        readonly object gate = new object();
        Head head;
        bool disposed;
        internal static Action BeforePublishForTests;
        public Guid ProjectId { get { return store.ProjectId; } }
        public long Revision { get { lock (gate) { EnsureOpen(); return head.Sequence; } } }
        SecureProjectSession(SecureProjectStore store, Head head) { this.store = store; this.head = head; }

        internal static SecureProjectSession Create(string path, byte[] item, byte[] cable)
        {
            if (item == null || cable == null || item.Length > 4 * 1024 * 1024 || cable.Length > 4 * 1024 * 1024)
                throw new ArgumentException("项目元数据长度无效。");
            var store = SecureProjectStore.Create(path, item, cable);
            try
            {
                var head = new Head { Sequence = 1 };
                head.Files.Add("ITEM.in", Make("ITEM.in", ProjectRecordKind.Project, store.List(ProjectRecordKind.Project).Single(), item));
                head.Files.Add("CABLE.in", Make("CABLE.in", ProjectRecordKind.Cable, store.List(ProjectRecordKind.Cable).Single(), cable));
                head.Id = store.Append(ProjectRecordKind.Revision, Serialize(head), Guid.Empty);
                return new SecureProjectSession(store, head);
            }
            catch { store.Dispose(); throw; }
        }

        internal static SecureProjectSession Open(string path)
        {
            var store = SecureProjectStore.Open(path);
            try
            {
                var ids = store.List(ProjectRecordKind.Revision);
                if (ids.Count == 0 || ids.Count > MaximumRevisions) throw new InvalidDataException("项目没有可用的业务版本，或版本数量超出支持范围。");
                var heads = new List<Head>();
                foreach (Guid id in ids)
                {
                    var record = store.Read(ProjectRecordKind.Revision, id);
                    try
                    {
                        var h = Parse(record.Data); h.Id = id;
                        if (record.RelatedRecordId != h.Previous) throw new InvalidDataException("项目版本关联无效。");
                        heads.Add(h);
                    }
                    finally { Array.Clear(record.Data, 0, record.Data.Length); }
                }
                heads = heads.OrderBy(h => h.Sequence).ToList();
                Guid previous = Guid.Empty; long sequence = 0;
                foreach (Head h in heads)
                {
                    if (h.Sequence != ++sequence || h.Previous != previous) throw new InvalidDataException("项目版本链不完整或存在冲突。");
                    previous = h.Id;
                }
                var result = new SecureProjectSession(store, heads.Last());
                foreach (Entry entry in result.head.Files.Values)
                {
                    var record = store.Read(entry.Kind, entry.Id);
                    try
                    {
                        if (entry.Stream)
                        {
                            if (record.Data.Length < 53 || Encoding.ASCII.GetString(record.Data, 0, 9) != "HXSTREAM1" ||
                                BitConverter.ToInt64(record.Data, 9) != entry.Length || !Equal(record.Data.Skip(21).Take(32).ToArray(), entry.Hash))
                                throw new InvalidDataException("采集记录与项目版本不一致。");
                        }
                        else if (record.Data.LongLength != entry.Length || !Equal(Digest(record.Data), entry.Hash))
                            throw new InvalidDataException("项目元数据与版本不一致。");
                    }
                    finally { Array.Clear(record.Data, 0, record.Data.Length); }
                }
                return result;
            }
            catch { store.Dispose(); throw; }
        }

        public string[] ListFiles() { lock (gate) { EnsureOpen(); return head.Files.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(); } }
        public byte[] ReadMetadata(string path)
        {
            lock (gate)
            {
                Entry entry = Get(path);
                if (entry.Stream || entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("请按流读取采集记录。");
                var record = store.Read(entry.Kind, entry.Id);
                if (record.Data.LongLength != entry.Length || !Equal(Digest(record.Data), entry.Hash))
                { Array.Clear(record.Data, 0, record.Data.Length); throw new InvalidDataException("项目元数据校验失败。"); }
                return record.Data;
            }
        }
        public void ReadFile(string path, Stream destination)
        {
            if (destination == null || !destination.CanWrite) throw new ArgumentException("输出不可写。");
            lock (gate)
            {
                Entry entry = Get(path);
                if (entry.Stream) store.ReadStream(entry.Kind, entry.Id, destination);
                else { byte[] bytes = ReadMetadata(path); try { destination.Write(bytes, 0, bytes.Length); } finally { Array.Clear(bytes, 0, bytes.Length); } }
            }
        }
        Entry Get(string path)
        {
            EnsureOpen(); Entry entry;
            if (!head.Files.TryGetValue(ValidateName(path), out entry)) throw new FileNotFoundException("项目中没有所选记录。");
            return entry;
        }

        public Task<long> CommitAsync(IEnumerable<ProjectFileChange> changes, long expectedRevision, CancellationToken cancellation)
        {
            // Capture the list before queueing; byte changes own a constructor snapshot.
            ProjectFileChange[] copy = Prepare(changes);
            return Task.Run(() => CommitPrepared(copy, expectedRevision, cancellation), cancellation);
        }
        internal long Commit(IEnumerable<ProjectFileChange> changes, long expectedRevision, CancellationToken cancellation)
        { return CommitPrepared(Prepare(changes), expectedRevision, cancellation); }
        static ProjectFileChange[] Prepare(IEnumerable<ProjectFileChange> changes)
        {
            if (changes == null) throw new ArgumentNullException("changes");
            var result = changes.Take(MaximumFiles + 1).ToArray();
            if (result.Length == 0 || result.Length > MaximumFiles || result.Any(c => c == null) ||
                result.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw new ArgumentException("项目修改为空、重复或超出支持范围。");
            foreach (var c in result)
            {
                if (c.Remove) { if (c.Path.Equals("ITEM.in", StringComparison.OrdinalIgnoreCase) || c.Path.Equals("CABLE.in", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("不能删除项目或电缆信息。"); continue; }
                if (c.Kind < ProjectRecordKind.Project || c.Kind > ProjectRecordKind.ReportArtifact || c.Kind == ProjectRecordKind.RawChunk ||
                    c.Path.Equals("ITEM.in", StringComparison.OrdinalIgnoreCase) && (c.Kind != ProjectRecordKind.Project || c.OpenStream != null) ||
                    c.Path.Equals("CABLE.in", StringComparison.OrdinalIgnoreCase) && (c.Kind != ProjectRecordKind.Cable || c.OpenStream != null))
                    throw new ArgumentException("项目记录类型与内容不匹配。");
            }
            return result;
        }
        long CommitPrepared(ProjectFileChange[] changes, long expectedRevision, CancellationToken cancellation)
        {
            lock (gate)
            {
                EnsureOpen(); cancellation.ThrowIfCancellationRequested();
                if (expectedRevision != head.Sequence) throw new IOException("项目已被其他操作修改，请重新加载后保存。");
                if (head.Sequence >= MaximumRevisions) throw new IOException("项目版本数量已达上限，请通过维护流程归档。");
                var next = new Head { Previous = head.Id, Sequence = head.Sequence + 1, Files = new Dictionary<string, Entry>(head.Files, StringComparer.OrdinalIgnoreCase) };
                foreach (ProjectFileChange change in changes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (change.Remove) { next.Files.Remove(change.Path); continue; }
                    Entry entry;
                    if (change.Data != null)
                    {
                        Guid id = store.Append(change.Kind, change.Data, Guid.Empty);
                        entry = Make(change.Path, change.Kind, id, change.Data);
                    }
                    else
                    {
                        using (Stream input = change.OpenStream())
                        using (var hashed = new HashingInput(input, cancellation))
                        {
                            Guid id = store.AppendStream(change.Kind, hashed, Guid.Empty);
                            entry = new Entry { Name = change.Path, Kind = change.Kind, Id = id, Stream = true, Length = hashed.Count, Hash = hashed.Finish() };
                        }
                    }
                    next.Files[change.Path] = entry;
                    if (next.Files.Count > MaximumFiles) throw new IOException("项目记录数量超出支持范围。");
                }
                cancellation.ThrowIfCancellationRequested();
                Action before = BeforePublishForTests; if (before != null) before();
                cancellation.ThrowIfCancellationRequested();
                next.Id = store.Append(ProjectRecordKind.Revision, Serialize(next), head.Id);
                head = next; // No cancellation after durable publication: success is real.
                return head.Sequence;
            }
        }
        static Entry Make(string name, ProjectRecordKind kind, Guid id, byte[] data)
        { return new Entry { Name = name, Kind = kind, Id = id, Length = data.LongLength, Hash = Digest(data) }; }
        static byte[] Serialize(Head h)
        {
            using (var output = new MemoryStream())
            {
                using (var writer = new BinaryWriter(output, new UTF8Encoding(false, true), true))
                {
                    writer.Write(Magic); writer.Write(h.Sequence); writer.Write(h.Previous.ToByteArray()); writer.Write(h.Files.Count);
                    foreach (Entry e in h.Files.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                    { writer.Write(e.Name); writer.Write((byte)e.Kind); writer.Write(e.Id.ToByteArray()); writer.Write(e.Stream); writer.Write(e.Length); writer.Write(e.Hash); }
                }
                return output.ToArray();
            }
        }
        static Head Parse(byte[] bytes)
        {
            if (bytes.Length < 36 || bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("项目版本长度无效。");
            using (var input = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(input, new UTF8Encoding(false, true)))
            {
                if (!Equal(reader.ReadBytes(8), Magic)) throw new InvalidDataException("项目版本格式无效。");
                var h = new Head { Sequence = reader.ReadInt64(), Previous = new Guid(reader.ReadBytes(16)) };
                int count = reader.ReadInt32();
                if (h.Sequence < 1 || h.Sequence > MaximumRevisions || count < 2 || count > MaximumFiles) throw new InvalidDataException("项目版本内容无效。");
                for (int i = 0; i < count; i++)
                {
                    var e = new Entry { Name = ValidateName(reader.ReadString()), Kind = (ProjectRecordKind)reader.ReadByte(), Id = new Guid(reader.ReadBytes(16)), Stream = reader.ReadBoolean(), Length = reader.ReadInt64(), Hash = reader.ReadBytes(32) };
                    if (e.Id == Guid.Empty || e.Kind < ProjectRecordKind.Project || e.Kind > ProjectRecordKind.ReportArtifact || e.Kind == ProjectRecordKind.RawChunk ||
                        e.Length <= 0 || e.Length > 4L * 1024 * 1024 * 1024 || !e.Stream && e.Length > 4 * 1024 * 1024 || e.Hash.Length != 32 || h.Files.ContainsKey(e.Name))
                        throw new InvalidDataException("项目版本记录无效。");
                    h.Files.Add(e.Name, e);
                }
                if (input.Position != input.Length || !h.Files.ContainsKey("ITEM.in") || !h.Files.ContainsKey("CABLE.in") ||
                    h.Files["ITEM.in"].Kind != ProjectRecordKind.Project || h.Files["CABLE.in"].Kind != ProjectRecordKind.Cable ||
                    h.Files["ITEM.in"].Stream || h.Files["CABLE.in"].Stream) throw new InvalidDataException("项目版本缺少完整元数据。");
                return h;
            }
        }
        internal static string ValidateName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > 220 || name.Contains("\\") || name.StartsWith("/") || name.EndsWith("/")) throw new ArgumentException("项目记录名称无效。");
            foreach (string part in name.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new ArgumentException("项目记录名称无效。");
            return name;
        }
        static byte[] Digest(byte[] data) { using (var sha = SHA256.Create()) return sha.ComputeHash(data); }
        static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0; }
        void EnsureOpen() { if (disposed) throw new ObjectDisposedException("SecureProjectSession"); }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; store.Dispose(); } }

        sealed class HashingInput : Stream
        {
            readonly Stream input; readonly CancellationToken cancellation; readonly SHA256 sha = SHA256.Create();
            bool finished; internal long Count;
            internal HashingInput(Stream input, CancellationToken token) { this.input = input ?? throw new InvalidDataException("采集输入为空。"); if (!input.CanRead) throw new InvalidDataException("采集输入不可读。"); cancellation = token; }
            public override int Read(byte[] buffer, int offset, int count)
            { cancellation.ThrowIfCancellationRequested(); int read = input.Read(buffer, offset, count); if (read > 0) { sha.TransformBlock(buffer, offset, read, buffer, offset); Count += read; } return read; }
            internal byte[] Finish() { if (!finished) { sha.TransformFinalBlock(new byte[0], 0, 0); finished = true; } return sha.Hash; }
            protected override void Dispose(bool disposing) { if (disposing) sha.Dispose(); base.Dispose(disposing); }
            public override bool CanRead { get { return true; } } public override bool CanSeek { get { return false; } } public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override long Seek(long o, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long value) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }
}
