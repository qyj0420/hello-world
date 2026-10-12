using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SecureProjectStore
    {
        private sealed class IndexEntry
        {
            public ProjectRecordKind Kind;
            public Guid Id;
            public byte[] Hash;
        }

        private static readonly byte[] IndexMagic = Encoding.ASCII.GetBytes("HXINDEX1");
        private static readonly byte[] TransactionMagic = Encoding.ASCII.GetBytes("HXTRANS1");
        private const int MaximumIndexedRecords = 1000000;
        private const int MaximumEnvelopeBytes = MaximumPlaintextBytes + 1024 * 1024;
        private const long MaximumTransferBytes = 16L * 1024 * 1024 * 1024;
        private readonly Dictionary<Guid, IndexEntry> index = new Dictionary<Guid, IndexEntry>();
        private string IndexPath { get { return Path.Combine(root, "index.owidx"); } }
        private string JournalPath { get { return Path.Combine(root, "pending.owtxn"); } }

        // Internal hook used only by the independently compiled crash test.
        internal static Action<string> FaultInjection;
        private static void Checkpoint(string phase)
        {
            Action<string> hook = FaultInjection;
            if (hook != null) hook(phase);
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目不支持链接文件或目录。");
        }

        private static byte[] ReadBoundedFile(string path, int minimum, int maximum)
        {
            RejectLink(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < minimum || stream.Length > maximum)
                    throw new InvalidDataException("项目文件长度无效。");
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException();
                    offset += count;
                }
                return bytes;
            }
        }

        private byte[] ReadVerifiedEnvelope(ProjectRecordKind kind, Guid id)
        {
            IndexEntry entry;
            if (!index.TryGetValue(id, out entry) || entry.Kind != kind)
                throw new FileNotFoundException("项目索引中不存在该记录。");
            string folder = Path.Combine(root, FolderName(kind));
            RejectLink(root);
            RejectLink(folder);
            byte[] bytes = ReadBoundedFile(Path.Combine(folder, id.ToString("N") + Extension(kind)),
                HeaderLength + 64, MaximumEnvelopeBytes);
            using (SHA256 sha = SHA256.Create())
                if (!FixedEquals(sha.ComputeHash(bytes), 0, entry.Hash, 0, 32))
                    throw new CryptographicException("项目记录与索引不一致。数据可能已被修改。");
            return bytes;
        }

        private void CommitRecord(ProjectRecordKind kind, Guid id, byte[] envelope)
        {
            if (index.Count >= MaximumIndexedRecords)
                throw new InvalidDataException("项目记录数量超过支持上限。");
            ValidateIndexedFiles();
            byte[] before = SerializeIndex();
            byte[] diskIndex = ReadBoundedFile(IndexPath, 60, 60 + MaximumIndexedRecords * 49);
            if (before.Length != diskIndex.Length || !FixedEquals(before, 0, diskIndex, 0, before.Length))
                throw new CryptographicException("磁盘索引在打开后已改变。");
            byte[] digest;
            using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(envelope);
            // Journal binds the exact old index and full sealed record. Publish it
            // only after a durable flush, before creating any project record.
            byte[] journal = new byte[8 + 16 + 32 + 1 + 16 + 32 + 4 + envelope.Length + 32];
            Buffer.BlockCopy(TransactionMagic, 0, journal, 0, 8);
            Buffer.BlockCopy(ProjectId.ToByteArray(), 0, journal, 8, 16);
            using (SHA256 sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(before), 0, journal, 24, 32);
            journal[56] = (byte)kind;
            Buffer.BlockCopy(id.ToByteArray(), 0, journal, 57, 16);
            Buffer.BlockCopy(digest, 0, journal, 73, 32);
            Buffer.BlockCopy(BitConverter.GetBytes(envelope.Length), 0, journal, 105, 4);
            Buffer.BlockCopy(envelope, 0, journal, 109, envelope.Length);
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
                Buffer.BlockCopy(hmac.ComputeHash(journal, 0, journal.Length - 32), 0, journal, journal.Length - 32, 32);
            // Preparation is outside the committed project namespace, on the same
            // volume. A process exit before publication can leave encrypted debris
            // here but never an ambiguous file in the authenticated container.
            string prepared = root + ".prepare-" + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(prepared, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(journal, 0, journal.Length / 2);
                    stream.Flush(true);
                    Checkpoint("journal-partial");
                    stream.Write(journal, journal.Length / 2, journal.Length - journal.Length / 2);
                    stream.Flush(true);
                }
                Checkpoint("journal-staged");
                File.Move(prepared, JournalPath);
                Checkpoint("journal-published");
                RecoverTransaction();
            }
            catch
            {
                recoveryRequired = true;
                throw;
            }
            finally
            {
                Array.Clear(journal, 0, journal.Length);
                if (File.Exists(prepared)) File.Delete(prepared);
            }
        }

        private byte[] SerializeIndex()
        {
            if (index.Count > MaximumIndexedRecords)
                throw new InvalidDataException("项目记录数量超过支持上限。");
            byte[] contents = new byte[8 + 16 + 4 + index.Count * 49 + 32];
            Buffer.BlockCopy(IndexMagic, 0, contents, 0, 8);
            Buffer.BlockCopy(ProjectId.ToByteArray(), 0, contents, 8, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(index.Count), 0, contents, 24, 4);
            int offset = 28;
            foreach (IndexEntry entry in index.Values.OrderBy(e => (int)e.Kind).ThenBy(e => e.Id))
            {
                contents[offset++] = (byte)entry.Kind;
                Buffer.BlockCopy(entry.Id.ToByteArray(), 0, contents, offset, 16); offset += 16;
                Buffer.BlockCopy(entry.Hash, 0, contents, offset, 32); offset += 32;
            }
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
            {
                byte[] signature = hmac.ComputeHash(contents, 0, offset);
                Buffer.BlockCopy(signature, 0, contents, offset, 32);
            }
            return contents;
        }

        private void WriteIndex()
        {
            WriteNewFile(IndexPath, SerializeIndex());
        }

        private static void WriteTransactionTemporary(string path, byte[] bytes, string phase)
        {
            if (File.Exists(path))
            {
                RejectLink(path);
                File.Delete(path);
            }
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length / 2);
                stream.Flush(true);
                Checkpoint(phase + "-partial");
                stream.Write(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);
                stream.Flush(true);
            }
            Checkpoint(phase + "-written");
        }

        private void RecoverTransaction()
        {
            if (!File.Exists(JournalPath)) return;
            byte[] journal = ReadBoundedFile(JournalPath, 141 + HeaderLength + 64, 141 + MaximumEnvelopeBytes);
            byte[] signature;
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
                signature = hmac.ComputeHash(journal, 0, journal.Length - 32);
            if (!FixedEquals(journal, 0, TransactionMagic, 0, 8) ||
                !FixedEquals(journal, 8, ProjectId.ToByteArray(), 0, 16) ||
                !FixedEquals(journal, journal.Length - 32, signature, 0, 32))
                throw new CryptographicException("项目事务日志认证失败，拒绝自动恢复。");
            int length = BitConverter.ToInt32(journal, 105);
            if (length < HeaderLength + 64 || length > MaximumEnvelopeBytes || journal.Length != length + 141)
                throw new InvalidDataException("事务记录长度无效。");
            ProjectRecordKind kind = (ProjectRecordKind)journal[56];
            ValidateKind(kind);
            byte[] idBytes = new byte[16], digest = new byte[32];
            Buffer.BlockCopy(journal, 57, idBytes, 0, 16);
            Buffer.BlockCopy(journal, 73, digest, 0, 32);
            Guid id = new Guid(idBytes);
            if (id == Guid.Empty) throw new InvalidDataException("事务记录编号无效。");
            byte[] envelope = new byte[length];
            Buffer.BlockCopy(journal, 109, envelope, 0, length);
            using (SHA256 sha = SHA256.Create())
                if (!FixedEquals(sha.ComputeHash(envelope), 0, digest, 0, 32))
                    throw new CryptographicException("事务记录摘要不匹配。");
            ProjectRecord verified = OpenEnvelope(envelope, kind, id);
            Array.Clear(verified.Data, 0, verified.Data.Length);
            IndexEntry existing;
            bool committed = index.TryGetValue(id, out existing);
            if (committed && (existing.Kind != kind || !FixedEquals(existing.Hash, 0, digest, 0, 32)))
                throw new CryptographicException("事务与已提交记录冲突。");
            if (committed) index.Remove(id);
            byte[] before;
            try { before = SerializeIndex(); }
            finally { if (committed) index.Add(id, existing); }
            using (SHA256 sha = SHA256.Create())
                if (!FixedEquals(sha.ComputeHash(before), 0, journal, 24, 32))
                    throw new CryptographicException("事务不属于当前索引版本，拒绝重放。");
            string folder = Path.Combine(root, FolderName(kind));
            if (Directory.Exists(folder)) RejectLink(folder);
            else Directory.CreateDirectory(folder);
            string recordPath = Path.Combine(folder, id.ToString("N") + Extension(kind));
            string recordTemporary = recordPath + ".pending";
            if (File.Exists(recordPath))
            {
                byte[] saved = ReadBoundedFile(recordPath, HeaderLength + 64, MaximumEnvelopeBytes);
                if (saved.Length != envelope.Length || !FixedEquals(saved, 0, envelope, 0, envelope.Length))
                    throw new CryptographicException("事务目标记录已改变，拒绝覆盖。");
                if (File.Exists(recordTemporary)) { RejectLink(recordTemporary); File.Delete(recordTemporary); }
            }
            else
            {
                if (committed) throw new InvalidDataException("已提交的事务记录缺失，拒绝掩盖删除。");
                WriteTransactionTemporary(recordTemporary, envelope, "record-temp");
                File.Move(recordTemporary, recordPath);
            }
            Checkpoint("record-published");
            string indexTemporary = IndexPath + ".pending";
            if (!committed)
            {
                index.Add(id, new IndexEntry { Kind = kind, Id = id, Hash = digest });
                WriteTransactionTemporary(indexTemporary, SerializeIndex(), "index-temp");
                File.Replace(indexTemporary, IndexPath, null, true);
            }
            else if (File.Exists(indexTemporary)) { RejectLink(indexTemporary); File.Delete(indexTemporary); }
            Checkpoint("index-published");
            File.Delete(JournalPath);
            Checkpoint("journal-removed");
            Array.Clear(envelope, 0, envelope.Length);
            Array.Clear(journal, 0, journal.Length);
        }

        private void ReadIndex()
        {
            byte[] bytes = ReadBoundedFile(IndexPath, 60, 60 + MaximumIndexedRecords * 49);
            if (!FixedEquals(bytes, 0, IndexMagic, 0, 8))
                throw new InvalidDataException("项目索引格式无效。");
            byte[] projectBytes = new byte[16];
            Buffer.BlockCopy(bytes, 8, projectBytes, 0, 16);
            if (new Guid(projectBytes) != ProjectId)
                throw new InvalidDataException("项目索引身份不匹配。");
            int count = BitConverter.ToInt32(bytes, 24);
            if (count < 0 || count > MaximumIndexedRecords || bytes.Length != 8L + 16 + 4 + count * 49L + 32)
                throw new InvalidDataException("项目索引记录数无效。");
            byte[] signature;
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
                signature = hmac.ComputeHash(bytes, 0, bytes.Length - 32);
            if (!FixedEquals(bytes, bytes.Length - 32, signature, 0, 32))
                throw new CryptographicException("项目索引完整性校验失败。");
            index.Clear();
            int offset = 28;
            for (int i = 0; i < count; i++)
            {
                ProjectRecordKind kind = (ProjectRecordKind)bytes[offset++];
                ValidateKind(kind);
                byte[] idBytes = new byte[16], hash = new byte[32];
                Buffer.BlockCopy(bytes, offset, idBytes, 0, 16); offset += 16;
                Buffer.BlockCopy(bytes, offset, hash, 0, 32); offset += 32;
                Guid id = new Guid(idBytes);
                if (id == Guid.Empty || index.ContainsKey(id))
                    throw new InvalidDataException("项目索引包含重复或空记录编号。");
                index.Add(id, new IndexEntry { Kind = kind, Id = id, Hash = hash });
            }
        }

        private void ValidateIndexedFiles(bool transfer = false)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目目录不能是链接目录。");
            HashSet<string> folders = new HashSet<string>(AllKinds().Select(FolderName), StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFileSystemEntries(root))
            {
                RejectLink(path);
                string name = Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    if (!folders.Contains(name)) throw new InvalidDataException("项目包含未知目录。");
                }
                else if (!String.Equals(name, "index.owidx", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(name, transfer ? "transfer.owkey" : "vault.owkey", StringComparison.OrdinalIgnoreCase) &&
                    (transfer || !String.Equals(name, "writer.owlock", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("项目包含未纳入索引的文件。");
            }
            foreach (IndexEntry entry in index.Values)
            {
                string path = Path.Combine(root, FolderName(entry.Kind), entry.Id.ToString("N") + Extension(entry.Kind));
                if (!File.Exists(path)) throw new InvalidDataException("项目记录缺失：" + entry.Id.ToString("N"));
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("项目记录不能是链接文件。");
            }
            foreach (ProjectRecordKind kind in AllKinds())
            {
                string folder = Path.Combine(root, FolderName(kind));
                if (!Directory.Exists(folder)) continue;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("项目记录目录不能是链接目录。");
                if (Directory.GetDirectories(folder).Length != 0)
                    throw new InvalidDataException("项目记录目录包含未知子目录。");
                foreach (string path in Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    Guid id;
                    if (!String.Equals(Path.GetExtension(path), Extension(kind), StringComparison.OrdinalIgnoreCase) ||
                        !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out id) ||
                        !index.ContainsKey(id) || index[id].Kind != kind)
                        throw new InvalidDataException("项目包含未纳入索引的文件，请先运行修复或重新导入。");
                }
            }
        }
    }
}
