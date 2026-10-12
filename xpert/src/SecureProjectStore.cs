using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    // Version 1 of the new local project format.  The legacy ITEM.in/CABLE.in
    // readers remain separate until every module has been migrated.
    internal enum ProjectRecordKind : byte
    {
        Project = 1,
        Cable = 2,
        Calibration = 3,
        Acquisition = 4,
        Analysis = 5,
        ReportMetadata = 6,
        LegacySource = 7,
        RawChunk = 8,
        ReportArtifact = 9,
        MigrationIndex = 10,
        Revision = 11
    }

    internal sealed class ProjectRecord
    {
        public Guid Id { get; internal set; }
        public Guid ProjectId { get; internal set; }
        public Guid RelatedRecordId { get; internal set; }
        public ProjectRecordKind Kind { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
        public byte[] Data { get; internal set; }
    }

    /// <summary>
    /// A project folder contains only opaque, authenticated records.  Each
    /// project gets independent random keys protected by Windows DPAPI for the
    /// current user.  This is not a DRM or administrator-proof scheme.
    /// </summary>
    internal sealed partial class SecureProjectStore : IDisposable
    {
        private const int MaximumPlaintextBytes = 64 * 1024 * 1024;
        private const int RawChunkBytes = 8 * 1024 * 1024;
        private const int MaximumRawChunks = 512;
        private const int TransferIterations = 600000;
        private const int HeaderLength = 72;
        private const int MacLength = 32;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HXOW");
        private static readonly byte[] KeyMagic = Encoding.ASCII.GetBytes("HXKEY1");
        private static readonly byte[] TransferMagic = Encoding.ASCII.GetBytes("HXPORT1");
        private static readonly byte[] DpapiEntropy = Encoding.ASCII.GetBytes("XIONGXIN.OWTS.ProjectKey.v1");
        private readonly string root;
        private readonly byte[] encryptionKey;
        private readonly byte[] macKey;
        private readonly FileStream writerLock;
        private bool disposed;
        private bool recoveryRequired;

        public Guid ProjectId { get; private set; }
        public string RootPath { get { return root; } }

        private SecureProjectStore(string path, Guid projectId, byte[] keys, bool acquireWriterLock = true)
        {
            root = path;
            if (acquireWriterLock)
            {
                RejectLink(root);
                string lockPath = Path.Combine(root, "writer.owlock");
                if (File.Exists(lockPath)) RejectLink(lockPath);
                try { writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) { throw new IOException("项目已由另一个实例打开，或无法取得独占访问权限。", ex); }
            }
            ProjectId = projectId;
            encryptionKey = new byte[32];
            macKey = new byte[32];
            Buffer.BlockCopy(keys, 0, encryptionKey, 0, 32);
            Buffer.BlockCopy(keys, 32, macKey, 0, 32);
            Array.Clear(keys, 0, keys.Length);
        }

        public static SecureProjectStore Create(string path, byte[] projectData, byte[] cableData)
        {
            string full = ValidateNewDirectory(path);
            string staging = NewStagingPath(full);
            ValidatePayload(projectData);
            ValidatePayload(cableData);
            byte[] keys = new byte[64];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(keys);
            Guid projectId = Guid.NewGuid();
            byte[] protectedKeys = null;
            try
            {
                protectedKeys = ProtectedData.Protect(keys, DpapiEntropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(staging);
                byte[] keyFile = new byte[KeyMagic.Length + 16 + protectedKeys.Length];
                Buffer.BlockCopy(KeyMagic, 0, keyFile, 0, KeyMagic.Length);
                Buffer.BlockCopy(projectId.ToByteArray(), 0, keyFile, KeyMagic.Length, 16);
                Buffer.BlockCopy(protectedKeys, 0, keyFile, KeyMagic.Length + 16, protectedKeys.Length);
                WriteNewFile(Path.Combine(staging, "vault.owkey"), keyFile);
                SecureProjectStore store = new SecureProjectStore(staging, projectId, keys);
                try
                {
                    store.WriteIndex();
                    store.Append(ProjectRecordKind.Project, projectData, Guid.Empty);
                    store.Append(ProjectRecordKind.Cable, cableData, Guid.Empty);
                    store.Dispose();
                    Directory.Move(staging, full);
                    return Open(full);
                }
                catch
                {
                    store.Dispose();
                    throw;
                }
            }
            finally
            {
                Array.Clear(keys, 0, keys.Length);
                if (protectedKeys != null) Array.Clear(protectedKeys, 0, protectedKeys.Length);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        public static SecureProjectStore Open(string path)
        {
            string full = Path.GetFullPath(path);
            string keyPath = Path.Combine(full, "vault.owkey");
            if ((File.GetAttributes(keyPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目密钥不能是链接文件。");
            byte[] keyFile = ReadBoundedFile(keyPath, KeyMagic.Length + 17, 4096);
            if (keyFile.Length < KeyMagic.Length + 17 ||
                !FixedEquals(keyFile, 0, KeyMagic, 0, KeyMagic.Length))
                throw new InvalidDataException("项目密钥格式无效。请勿手工修改项目文件。");
            byte[] idBytes = new byte[16];
            Buffer.BlockCopy(keyFile, KeyMagic.Length, idBytes, 0, 16);
            byte[] protectedKeys = new byte[keyFile.Length - KeyMagic.Length - 16];
            Buffer.BlockCopy(keyFile, KeyMagic.Length + 16, protectedKeys, 0, protectedKeys.Length);
            byte[] keys = null;
            try
            {
                try
                {
                    keys = ProtectedData.Unprotect(protectedKeys, DpapiEntropy, DataProtectionScope.CurrentUser);
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException("无法用当前 Windows 账户打开此项目；请使用受控迁移流程，不要复制密钥文件。");
                }
                if (keys.Length != 64) throw new InvalidDataException("项目密钥长度无效。");
                SecureProjectStore store = new SecureProjectStore(full, new Guid(idBytes), keys);
                try
                {
                    store.ReadIndex();
                    store.RecoverTransaction();
                    store.ValidateIndexedFiles();
                    IList<Guid> projects = store.List(ProjectRecordKind.Project);
                    IList<Guid> cables = store.List(ProjectRecordKind.Cable);
                    if (projects.Count == 0 || cables.Count == 0)
                        throw new InvalidDataException("项目缺少项目或电缆信息。");
                    foreach (Guid id in projects) store.Read(ProjectRecordKind.Project, id);
                    foreach (Guid id in cables) store.Read(ProjectRecordKind.Cable, id);
                    return store;
                }
                catch
                {
                    store.Dispose();
                    throw;
                }
            }
            finally
            {
                if (keys != null) Array.Clear(keys, 0, keys.Length);
                Array.Clear(protectedKeys, 0, protectedKeys.Length);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public Guid Append(ProjectRecordKind kind, byte[] data, Guid relatedRecordId)
        {
            EnsureOpen();
            ValidateKind(kind);
            ValidatePayload(data);
            Guid id = Guid.NewGuid();
            byte[] envelope = Seal(kind, id, relatedRecordId, data);
            try
            {
                CommitRecord(kind, id, envelope);
                return id;
            }
            finally { Array.Clear(envelope, 0, envelope.Length); }
        }

        // Long LOC and acquisition waveforms are streamed as 8 MiB authenticated
        // chunks; the final encrypted descriptor binds their order and SHA-256.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public Guid AppendStream(ProjectRecordKind kind, Stream input, Guid relatedRecordId)
        {
            EnsureOpen();
            ValidateKind(kind);
            if (kind == ProjectRecordKind.RawChunk)
                throw new ArgumentException("RawChunk is reserved for the store.", "kind");
            if (input == null || !input.CanRead) throw new ArgumentException("Readable input required.", "input");
            List<Guid> chunks = new List<Guid>();
            long total = 0;
            byte[] buffer = new byte[RawChunkBytes];
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                while (true)
                {
                    int filled = 0;
                    while (filled < buffer.Length)
                    {
                        int read = input.Read(buffer, filled, buffer.Length - filled);
                        if (read == 0) break;
                        filled += read;
                    }
                    if (filled == 0) break;
                    if (chunks.Count >= MaximumRawChunks)
                        throw new InvalidDataException("原始记录超过当前支持的最大长度。");
                    sha.TransformBlock(buffer, 0, filled, buffer, 0);
                    byte[] piece = new byte[filled];
                    Buffer.BlockCopy(buffer, 0, piece, 0, filled);
                    try { chunks.Add(Append(ProjectRecordKind.RawChunk, piece, Guid.Empty)); }
                    finally { Array.Clear(piece, 0, piece.Length); }
                    total += filled;
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                digest = sha.Hash;
            }
            Array.Clear(buffer, 0, buffer.Length);
            if (total == 0) throw new InvalidDataException("空文件不得作为有效采集记录导入。");
            byte[] descriptor;
            using (MemoryStream output = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(output, Encoding.UTF8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("HXSTREAM1"));
                    writer.Write(total);
                    writer.Write(chunks.Count);
                    writer.Write(digest);
                    foreach (Guid id in chunks) writer.Write(id.ToByteArray());
                }
                descriptor = output.ToArray();
            }
            return Append(kind, descriptor, relatedRecordId);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void ReadStream(ProjectRecordKind kind, Guid id, Stream output)
        {
            EnsureOpen();
            if (output == null || !output.CanWrite)
                throw new ArgumentException("Writable output required.", "output");
            byte[] descriptor = Read(kind, id).Data;
            using (MemoryStream input = new MemoryStream(descriptor, false))
            using (BinaryReader reader = new BinaryReader(input, Encoding.UTF8))
            {
                byte[] signature = reader.ReadBytes(9);
                if (signature.Length != 9 || Encoding.ASCII.GetString(signature) != "HXSTREAM1")
                    throw new InvalidDataException("记录不是流式原始数据。");
                long expected = reader.ReadInt64();
                int count = reader.ReadInt32();
                if (expected <= 0 || expected > (long)RawChunkBytes * MaximumRawChunks || count <= 0 || count > MaximumRawChunks ||
                    descriptor.Length != 9 + 8 + 4 + 32 + count * 16)
                    throw new InvalidDataException("流式记录目录无效。");
                byte[] expectedHash = reader.ReadBytes(32);
                List<Guid> chunks = new List<Guid>();
                for (int i = 0; i < count; i++) chunks.Add(new Guid(reader.ReadBytes(16)));
                long total = 0;
                // Spool only authenticated ciphertext to a private, delete-on-close
                // handle. No caller receives plaintext until all chunks and the
                // aggregate digest pass; the second pass uses this same snapshot.
                string snapshotPath = Path.Combine(Path.GetTempPath(), "hxstream-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream snapshot = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 8192, FileOptions.DeleteOnClose | FileOptions.SequentialScan))
                using (BinaryWriter snapshotWriter = new BinaryWriter(snapshot, Encoding.UTF8, true))
                using (SHA256 sha = SHA256.Create())
                {
                    foreach (Guid chunk in chunks)
                    {
                        byte[] sealedChunk = ReadVerifiedEnvelope(ProjectRecordKind.RawChunk, chunk);
                        byte[] piece = OpenEnvelope(sealedChunk, ProjectRecordKind.RawChunk, chunk).Data;
                        try
                        {
                            if (piece.Length <= 0 || piece.Length > RawChunkBytes || total + piece.Length > expected)
                                throw new InvalidDataException("原始数据分块长度无效。");
                            sha.TransformBlock(piece, 0, piece.Length, piece, 0);
                            snapshotWriter.Write(sealedChunk.Length);
                            snapshotWriter.Write(sealedChunk);
                            total += piece.Length;
                        }
                        finally
                        {
                            Array.Clear(piece, 0, piece.Length);
                            Array.Clear(sealedChunk, 0, sealedChunk.Length);
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    if (total != expected || !FixedEquals(sha.Hash, 0, expectedHash, 0, 32))
                        throw new CryptographicException("原始数据长度或摘要校验失败。");
                    snapshotWriter.Flush();
                    snapshot.Position = 0;
                    using (BinaryReader snapshotReader = new BinaryReader(snapshot, Encoding.UTF8, true))
                        foreach (Guid chunk in chunks)
                        {
                            byte[] sealedChunk = snapshotReader.ReadBytes(snapshotReader.ReadInt32());
                            byte[] piece = OpenEnvelope(sealedChunk, ProjectRecordKind.RawChunk, chunk).Data;
                            try { output.Write(piece, 0, piece.Length); }
                            finally
                            {
                                Array.Clear(piece, 0, piece.Length);
                                Array.Clear(sealedChunk, 0, sealedChunk.Length);
                            }
                        }
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public IList<Guid> List(ProjectRecordKind kind)
        {
            EnsureOpen();
            ValidateKind(kind);
            List<Guid> result = new List<Guid>();
            foreach (IndexEntry entry in index.Values)
                if (entry.Kind == kind) result.Add(entry.Id);
            result.Sort();
            return result;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public ProjectRecord Read(ProjectRecordKind kind, Guid id)
        {
            EnsureOpen();
            ValidateKind(kind);
            if (id == Guid.Empty) throw new ArgumentException("记录编号不能为空。", "id");
            byte[] envelope = ReadVerifiedEnvelope(kind, id);
            try
            {
                return OpenEnvelope(envelope, kind, id);
            }
            finally { Array.Clear(envelope, 0, envelope.Length); }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void Dispose()
        {
            if (disposed) return;
            Array.Clear(encryptionKey, 0, encryptionKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            disposed = true;
            if (writerLock != null) writerLock.Dispose();
        }

        public static string GenerateTransferPassphrase()
        {
            byte[] randomBytes = new byte[24];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(randomBytes);
            try { return Convert.ToBase64String(randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
            finally { Array.Clear(randomBytes, 0, randomBytes.Length); }
        }

        // A transfer package is a new folder of already encrypted records plus
        // a passphrase-wrapped key.  The source's Windows-only vault is omitted.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
        public void ExportTransfer(string destination, string passphrase)
        {
            EnsureOpen();
            ValidatePassphrase(passphrase);
            string target = ValidateNewDirectory(destination);
            string staging = NewStagingPath(target);
            Directory.CreateDirectory(staging);
            try
            {
                CopyIndexedRecords(staging, false);
                byte[] keys = new byte[64];
                Buffer.BlockCopy(encryptionKey, 0, keys, 0, 32);
                Buffer.BlockCopy(macKey, 0, keys, 32, 32);
                try
                {
                    WriteNewFile(Path.Combine(staging, "transfer.owkey"), WrapTransferKey(keys, ProjectId, passphrase));
                    using (SecureProjectStore validation = new SecureProjectStore(staging, ProjectId, (byte[])keys.Clone(), false))
                    {
                        validation.ReadIndex();
                        validation.ValidateIndexedFiles(true);
                        validation.ValidateAllRecords();
                    }
                }
                finally { Array.Clear(keys, 0, keys.Length); }
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        public static SecureProjectStore ImportTransfer(string source, string destination, string passphrase)
        {
            ValidatePassphrase(passphrase);
            string package = Path.GetFullPath(source);
            if (!Directory.Exists(package) ||
                (File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0)
                throw new DirectoryNotFoundException("移交包不存在或包含目录链接。");
            string target = ValidateNewDirectory(destination);
            string transferKeyPath = Path.Combine(package, "transfer.owkey");
            if ((File.GetAttributes(transferKeyPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("移交密钥不能是链接文件。");
            Guid projectId;
            byte[] keys = UnwrapTransferKey(ReadBoundedFile(transferKeyPath, 1, 4096), passphrase, out projectId);
            string staging = NewStagingPath(target);
            Directory.CreateDirectory(staging);
            try
            {
                // Authenticate and bound the package inventory before copying any
                // record. Never enumerate arbitrary package files into the target.
                using (SecureProjectStore packageStore = new SecureProjectStore(package, projectId, (byte[])keys.Clone(), false))
                {
                    packageStore.ReadIndex();
                    packageStore.CopyIndexedRecords(staging, true);
                }
                byte[] protectedKeys = ProtectedData.Protect(keys, DpapiEntropy, DataProtectionScope.CurrentUser);
                byte[] keyFile = new byte[KeyMagic.Length + 16 + protectedKeys.Length];
                Buffer.BlockCopy(KeyMagic, 0, keyFile, 0, KeyMagic.Length);
                Buffer.BlockCopy(projectId.ToByteArray(), 0, keyFile, KeyMagic.Length, 16);
                Buffer.BlockCopy(protectedKeys, 0, keyFile, KeyMagic.Length + 16, protectedKeys.Length);
                WriteNewFile(Path.Combine(staging, "vault.owkey"), keyFile);
                using (SecureProjectStore validation = Open(staging))
                {
                    validation.ValidateAllRecords();
                }
                Directory.Move(staging, target);
                return Open(target);
            }
            finally
            {
                Array.Clear(keys, 0, keys.Length);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        private static byte[] WrapTransferKey(byte[] keys, Guid projectId, string passphrase)
        {
            byte[] salt = new byte[16], iv = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(salt);
                random.GetBytes(iv);
            }
            byte[] derived = DeriveTransferKeys(passphrase, salt, TransferIterations);
            byte[] wrappingKey = new byte[32], signingKey = new byte[32];
            Buffer.BlockCopy(derived, 0, wrappingKey, 0, 32);
            Buffer.BlockCopy(derived, 32, signingKey, 0, 32);
            try
            {
                byte[] cipher;
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = wrappingKey;
                    aes.IV = iv;
                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                        cipher = encryptor.TransformFinalBlock(keys, 0, keys.Length);
                }
                byte[] result = new byte[TransferMagic.Length + 16 + 4 + 16 + 16 + 4 + cipher.Length + MacLength];
                int offset = 0;
                Buffer.BlockCopy(TransferMagic, 0, result, offset, TransferMagic.Length); offset += TransferMagic.Length;
                Buffer.BlockCopy(projectId.ToByteArray(), 0, result, offset, 16); offset += 16;
                Buffer.BlockCopy(BitConverter.GetBytes(TransferIterations), 0, result, offset, 4); offset += 4;
                Buffer.BlockCopy(salt, 0, result, offset, 16); offset += 16;
                Buffer.BlockCopy(iv, 0, result, offset, 16); offset += 16;
                Buffer.BlockCopy(BitConverter.GetBytes(cipher.Length), 0, result, offset, 4); offset += 4;
                Buffer.BlockCopy(cipher, 0, result, offset, cipher.Length);
                using (HMACSHA256 hmac = new HMACSHA256(signingKey))
                {
                    byte[] digest = hmac.ComputeHash(result, 0, result.Length - MacLength);
                    Buffer.BlockCopy(digest, 0, result, result.Length - MacLength, MacLength);
                }
                Array.Clear(cipher, 0, cipher.Length);
                return result;
            }
            finally
            {
                Array.Clear(derived, 0, derived.Length);
                Array.Clear(wrappingKey, 0, wrappingKey.Length);
                Array.Clear(signingKey, 0, signingKey.Length);
            }
        }

        private static byte[] UnwrapTransferKey(byte[] data, string passphrase, out Guid projectId)
        {
            projectId = Guid.Empty;
            const int fixedLength = 7 + 16 + 4 + 16 + 16 + 4;
            if (data.Length < fixedLength + 16 + MacLength || data.Length > 4096 ||
                !FixedEquals(data, 0, TransferMagic, 0, TransferMagic.Length))
                throw new InvalidDataException("移交密钥格式无效。");
            byte[] id = new byte[16], salt = new byte[16], iv = new byte[16];
            Buffer.BlockCopy(data, 7, id, 0, 16);
            int iterations = BitConverter.ToInt32(data, 23);
            if (iterations < 300000 || iterations > 2000000)
                throw new InvalidDataException("移交密钥的派生参数无效。");
            Buffer.BlockCopy(data, 27, salt, 0, 16);
            Buffer.BlockCopy(data, 43, iv, 0, 16);
            int cipherLength = BitConverter.ToInt32(data, 59);
            if (cipherLength != 80 || data.Length != fixedLength + cipherLength + MacLength)
                throw new InvalidDataException("移交密钥长度无效。");
            byte[] derived = DeriveTransferKeys(passphrase, salt, iterations);
            byte[] wrappingKey = new byte[32], signingKey = new byte[32];
            Buffer.BlockCopy(derived, 0, wrappingKey, 0, 32);
            Buffer.BlockCopy(derived, 32, signingKey, 0, 32);
            try
            {
                byte[] digest;
                using (HMACSHA256 hmac = new HMACSHA256(signingKey))
                    digest = hmac.ComputeHash(data, 0, data.Length - MacLength);
                if (!FixedEquals(data, data.Length - MacLength, digest, 0, MacLength))
                    throw new CryptographicException("移交口令错误或移交包已损坏。");
                byte[] keys;
                using (Aes aes = Aes.Create())
                {
                    aes.KeySize = 256;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = wrappingKey;
                    aes.IV = iv;
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                        keys = decryptor.TransformFinalBlock(data, fixedLength, cipherLength);
                }
                if (keys.Length != 64) throw new InvalidDataException("移交密钥内容无效。");
                projectId = new Guid(id);
                return keys;
            }
            finally
            {
                Array.Clear(derived, 0, derived.Length);
                Array.Clear(wrappingKey, 0, wrappingKey.Length);
                Array.Clear(signingKey, 0, signingKey.Length);
            }
        }

        private static byte[] DeriveTransferKeys(string passphrase, byte[] salt, int iterations)
        {
            byte[] password = Encoding.UTF8.GetBytes(passphrase);
            try
            {
                using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                    return kdf.GetBytes(64);
            }
            finally { Array.Clear(password, 0, password.Length); }
        }

        private static void ValidatePassphrase(string passphrase)
        {
            if (String.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
                throw new ArgumentException("移交口令至少需要 12 个字符；不要使用设备维护密码。", "passphrase");
        }

        private static ProjectRecordKind[] AllKinds()
        {
            return new[] { ProjectRecordKind.Project, ProjectRecordKind.Cable,
                ProjectRecordKind.Calibration, ProjectRecordKind.Acquisition,
                ProjectRecordKind.Analysis, ProjectRecordKind.ReportMetadata,
                ProjectRecordKind.LegacySource, ProjectRecordKind.RawChunk,
                ProjectRecordKind.ReportArtifact, ProjectRecordKind.MigrationIndex, ProjectRecordKind.Revision };
        }

        private void ValidateAllRecords()
        {
            if (List(ProjectRecordKind.Project).Count == 0 || List(ProjectRecordKind.Cable).Count == 0)
                throw new InvalidDataException("项目缺少项目或电缆信息。");
            foreach (IndexEntry entry in index.Values)
            {
                ProjectRecord verified = Read(entry.Kind, entry.Id);
                try
                {
                    if (entry.Kind != ProjectRecordKind.RawChunk && verified.Data.Length >= 9 &&
                        Encoding.ASCII.GetString(verified.Data, 0, 9) == "HXSTREAM1")
                        ReadStream(entry.Kind, entry.Id, Stream.Null);
                }
                finally { Array.Clear(verified.Data, 0, verified.Data.Length); }
            }
        }

        private void CopyIndexedRecords(string destination, bool transfer)
        {
            ValidateIndexedFiles(transfer);
            byte[] currentIndex = SerializeIndex();
            byte[] savedIndex = ReadBoundedFile(IndexPath, 60, 60 + MaximumIndexedRecords * 49);
            if (currentIndex.Length != savedIndex.Length || !FixedEquals(currentIndex, 0, savedIndex, 0, currentIndex.Length))
                throw new CryptographicException("项目索引在复制前已改变。");
            long total = 0;
            foreach (IndexEntry entry in index.Values)
            {
                string path = Path.Combine(root, FolderName(entry.Kind), entry.Id.ToString("N") + Extension(entry.Kind));
                long size = new FileInfo(path).Length;
                if (size < HeaderLength + 64 || size > MaximumEnvelopeBytes || total + size > MaximumTransferBytes)
                    throw new InvalidDataException("移交包超过单记录或总大小限制。");
                total += size;
            }
            total = 0;
            foreach (IndexEntry entry in index.Values)
            {
                byte[] sealedRecord = ReadVerifiedEnvelope(entry.Kind, entry.Id);
                try
                {
                    total += sealedRecord.Length;
                    if (total > MaximumTransferBytes) throw new InvalidDataException("移交包超过总大小限制。");
                    ProjectRecord verified = OpenEnvelope(sealedRecord, entry.Kind, entry.Id);
                    Array.Clear(verified.Data, 0, verified.Data.Length);
                    Checkpoint("transfer-record-verified");
                    string folder = Path.Combine(destination, FolderName(entry.Kind));
                    Directory.CreateDirectory(folder);
                    WriteNewFile(Path.Combine(folder, entry.Id.ToString("N") + Extension(entry.Kind)), sealedRecord);
                }
                finally { Array.Clear(sealedRecord, 0, sealedRecord.Length); }
            }
            WriteNewFile(Path.Combine(destination, "index.owidx"), currentIndex);
        }

        private byte[] Seal(ProjectRecordKind kind, Guid id, Guid relatedId, byte[] data)
        {
            byte[] compressed = Compress(data);
            byte[] iv = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(iv);
            byte[] cipher;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                aes.IV = iv;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    cipher = encryptor.TransformFinalBlock(compressed, 0, compressed.Length);
            }
            byte[] result = new byte[HeaderLength + iv.Length + cipher.Length + MacLength];
            Buffer.BlockCopy(Magic, 0, result, 0, 4);
            result[4] = 1;
            result[5] = (byte)kind;
            result[6] = 1; // deflate
            Buffer.BlockCopy(id.ToByteArray(), 0, result, 8, 16);
            Buffer.BlockCopy(ProjectId.ToByteArray(), 0, result, 24, 16);
            Buffer.BlockCopy(relatedId.ToByteArray(), 0, result, 40, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(DateTime.UtcNow.Ticks), 0, result, 56, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, result, 64, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(cipher.Length), 0, result, 68, 4);
            Buffer.BlockCopy(iv, 0, result, HeaderLength, 16);
            Buffer.BlockCopy(cipher, 0, result, HeaderLength + 16, cipher.Length);
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
            {
                byte[] digest = hmac.ComputeHash(result, 0, result.Length - MacLength);
                Buffer.BlockCopy(digest, 0, result, result.Length - MacLength, MacLength);
            }
            Array.Clear(compressed, 0, compressed.Length);
            Array.Clear(cipher, 0, cipher.Length);
            return result;
        }

        private ProjectRecord OpenEnvelope(byte[] bytes, ProjectRecordKind expectedKind, Guid expectedId)
        {
            if (bytes.Length < HeaderLength + 16 + 16 + MacLength ||
                !FixedEquals(bytes, 0, Magic, 0, Magic.Length) || bytes[4] != 1 ||
                bytes[5] != (byte)expectedKind || bytes[6] != 1 || bytes[7] != 0)
                throw new InvalidDataException("记录格式或版本不受支持。");
            int length = BitConverter.ToInt32(bytes, 64);
            int cipherLength = BitConverter.ToInt32(bytes, 68);
            if (length < 0 || length > MaximumPlaintextBytes || cipherLength < 16 ||
                cipherLength % 16 != 0 ||
                bytes.Length != HeaderLength + 16L + cipherLength + MacLength)
                throw new InvalidDataException("记录长度无效。");
            byte[] digest;
            using (HMACSHA256 hmac = new HMACSHA256(macKey))
                digest = hmac.ComputeHash(bytes, 0, bytes.Length - MacLength);
            if (!FixedEquals(bytes, bytes.Length - MacLength, digest, 0, MacLength))
                throw new CryptographicException("项目记录完整性校验失败。数据可能已被修改。");
            byte[] idBytes = new byte[16], projectBytes = new byte[16], relatedBytes = new byte[16];
            Buffer.BlockCopy(bytes, 8, idBytes, 0, 16);
            Buffer.BlockCopy(bytes, 24, projectBytes, 0, 16);
            Buffer.BlockCopy(bytes, 40, relatedBytes, 0, 16);
            Guid id = new Guid(idBytes);
            if (id != expectedId || new Guid(projectBytes) != ProjectId)
                throw new InvalidDataException("记录编号或所属项目不匹配。");
            long ticks = BitConverter.ToInt64(bytes, 56);
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("记录时间无效。");
            byte[] compressed;
            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encryptionKey;
                byte[] iv = new byte[16];
                Buffer.BlockCopy(bytes, HeaderLength, iv, 0, 16);
                aes.IV = iv;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    compressed = decryptor.TransformFinalBlock(bytes, HeaderLength + 16, cipherLength);
            }
            try
            {
                byte[] data = Decompress(compressed, length);
                return new ProjectRecord
                {
                    Id = id,
                    ProjectId = ProjectId,
                    RelatedRecordId = new Guid(relatedBytes),
                    Kind = expectedKind,
                    CreatedUtc = new DateTime(ticks, DateTimeKind.Utc),
                    Data = data
                };
            }
            finally { Array.Clear(compressed, 0, compressed.Length); }
        }

        private static byte[] Compress(byte[] data)
        {
            using (MemoryStream output = new MemoryStream())
            {
                using (DeflateStream zip = new DeflateStream(output, CompressionLevel.Optimal, true))
                    zip.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        private static byte[] Decompress(byte[] compressed, int expectedLength)
        {
            using (MemoryStream input = new MemoryStream(compressed, false))
            using (DeflateStream zip = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                byte[] chunk = new byte[8192];
                int count;
                while ((count = zip.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (output.Length + count > expectedLength)
                        throw new InvalidDataException("记录解压长度异常。");
                    output.Write(chunk, 0, count);
                }
                if (output.Length != expectedLength)
                    throw new InvalidDataException("记录内容不完整。");
                return output.ToArray();
            }
        }

        private static string ValidateNewDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("项目目录不能为空。", "path");
            string full = Path.GetFullPath(path.Trim());
            if (Directory.Exists(full) || File.Exists(full))
                throw new IOException("目标项目目录已存在；为保护原始数据，请选择新的目录。");
            string parent = Path.GetDirectoryName(full);
            if (String.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("项目目录的上级目录不存在。");
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("项目目录不能建立在链接目录中。");
            ValidateContainerPathBudget(full);
            // Reserve room for the same-volume staging container before any
            // records or keys are created. Legacy Framework callers need not
            // have Windows long-path policy enabled.
            ValidateContainerPathBudget(Path.Combine(parent, ".hx-stage-" + new string('0', 32)));
            return full;
        }

        private static void ValidateContainerPathBudget(string path)
        {
            string longestRecord = Path.Combine(path, "report-artifacts", new string('0', 32) + ".owf.pending");
            if (longestRecord.Length >= 260)
                throw new PathTooLongException("项目目录过长，无法安全创建记录及事务文件；请选择更短的目录。");
        }

        private static string NewStagingPath(string target)
        {
            // Do not repeat a possibly long user-selected folder name. This is
            // still a private, unique sibling on the destination volume.
            string staging = Path.Combine(Path.GetDirectoryName(target), ".hx-stage-" + Guid.NewGuid().ToString("N"));
            ValidateContainerPathBudget(staging);
            return staging;
        }

        private static void ValidatePayload(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > MaximumPlaintextBytes)
                throw new ArgumentException("记录内容必须在 1 字节至 64 MiB 之间。", "data");
        }

        private static void ValidateKind(ProjectRecordKind kind)
        {
            if (kind < ProjectRecordKind.Project || kind > ProjectRecordKind.Revision)
                throw new ArgumentOutOfRangeException("kind");
        }

        private static string FolderName(ProjectRecordKind kind)
        {
            switch (kind)
            {
                case ProjectRecordKind.Project: return "project";
                case ProjectRecordKind.Cable: return "cable";
                case ProjectRecordKind.Calibration: return "calibration";
                case ProjectRecordKind.Acquisition: return "acquisition";
                case ProjectRecordKind.Analysis: return "analysis";
                case ProjectRecordKind.LegacySource: return "legacy";
                case ProjectRecordKind.RawChunk: return "chunks";
                case ProjectRecordKind.ReportArtifact: return "report-artifacts";
                case ProjectRecordKind.MigrationIndex: return "migration";
                case ProjectRecordKind.Revision: return "revisions";
                default: return "report";
            }
        }

        private static string Extension(ProjectRecordKind kind)
        {
            switch (kind)
            {
                case ProjectRecordKind.Project: return ".owp";
                case ProjectRecordKind.Cable: return ".owc";
                case ProjectRecordKind.Calibration: return ".owk";
                case ProjectRecordKind.Acquisition: return ".owd";
                case ProjectRecordKind.Analysis: return ".owa";
                case ProjectRecordKind.LegacySource: return ".owl";
                case ProjectRecordKind.RawChunk: return ".owx";
                case ProjectRecordKind.ReportArtifact: return ".owf";
                case ProjectRecordKind.MigrationIndex: return ".owm";
                case ProjectRecordKind.Revision: return ".owv";
                default: return ".owr";
            }
        }

        private static void WriteNewFile(string path, byte[] data)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static bool FixedEquals(byte[] first, int firstOffset, byte[] second, int secondOffset, int count)
        {
            int difference = 0;
            for (int i = 0; i < count; i++) difference |= first[firstOffset + i] ^ second[secondOffset + i];
            return difference == 0;
        }

        private void EnsureOpen()
        {
            if (disposed) throw new ObjectDisposedException("SecureProjectStore");
            if (recoveryRequired) throw new IOException("写入中断；请关闭并重新打开项目以完成事务恢复。");
        }
    }
}
