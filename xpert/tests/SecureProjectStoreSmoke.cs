using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xiongxin.OWTS.Desktop;

internal static class SecureProjectStoreSmoke
{
    private static int checks;

    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Utf8(string value) { return Encoding.UTF8.GetBytes(value); }

    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex)
        {
            // Exception.ToString itself can fail on legacy path exceptions;
            // always preserve a compact, useful failure instead of CLR text.
            Console.Error.WriteLine("SECURE_STORE_FAIL checks=" + checks + " type=" + ex.GetType().FullName);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--crash")
        {
            using (SecureProjectStore store = SecureProjectStore.Open(args[1]))
            {
                SecureProjectStore.FaultInjection = phase => { if (phase == args[2]) Environment.Exit(73); };
                store.Append(ProjectRecordKind.Calibration, Utf8("crash durable record"), Guid.Empty);
            }
            Environment.Exit(74);
        }
        if (args.Length == 2 && args[0] == "--lock")
        {
            try { using (SecureProjectStore ignored = SecureProjectStore.Open(args[1])) { } }
            catch (IOException) { Environment.Exit(0); }
            Environment.Exit(1);
        }
        if (args.Length != 1) throw new ArgumentException("output directory required");
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "secure-" + Guid.NewGuid().ToString("N"));
        byte[] project = Utf8("保密项目：测试单位-001");
        byte[] cable = Utf8("电缆长度 414.0 m");
        string generatedPassphrase = SecureProjectStore.GenerateTransferPassphrase();
        Require(generatedPassphrase.Length == 32 &&
            generatedPassphrase != SecureProjectStore.GenerateTransferPassphrase(), "random transfer passphrase");
        Guid calibrationId;
        Guid acquisitionId;
        Guid analysisId;
        Guid streamedId;
        Guid projectId;
        using (SecureProjectStore store = SecureProjectStore.Create(path, project, cable))
        {
            projectId = store.ProjectId;
            Require(store.List(ProjectRecordKind.Project).Count == 1, "project record");
            Require(store.List(ProjectRecordKind.Cable).Count == 1, "cable record");
            calibrationId = store.Append(ProjectRecordKind.Calibration, Utf8("校准系数=1.223"), Guid.Empty);
            byte[] waveform = new byte[410688];
            for (int i = 0; i < waveform.Length; i++) waveform[i] = (byte)(i % 251);
            acquisitionId = store.Append(ProjectRecordKind.Acquisition, waveform, calibrationId);
            analysisId = store.Append(ProjectRecordKind.Analysis, Utf8("AI 建议和人工修正结果"), acquisitionId);
            using (MemoryStream stream = new MemoryStream(new byte[17 * 1024 * 1024], false))
                streamedId = store.AppendStream(ProjectRecordKind.LegacySource, stream, Guid.Empty);
            Require(store.Read(ProjectRecordKind.Calibration, calibrationId).RelatedRecordId == Guid.Empty, "calibration link");
            Require(store.Read(ProjectRecordKind.Acquisition, acquisitionId).RelatedRecordId == calibrationId, "acquisition link");
            Require(store.Read(ProjectRecordKind.Analysis, analysisId).RelatedRecordId == acquisitionId, "analysis link");
            Require(store.Read(ProjectRecordKind.Acquisition, acquisitionId).Data.SequenceEqual(waveform), "waveform roundtrip");
            using (MemoryStream reconstructed = new MemoryStream())
            {
                store.ReadStream(ProjectRecordKind.LegacySource, streamedId, reconstructed);
                Require(reconstructed.Length == 17L * 1024 * 1024, "chunked stream length");
            }
        }

        using (SecureProjectStore store = SecureProjectStore.Open(path))
        {
            Require(store.ProjectId == projectId, "project id reload");
            Guid projectRecord = store.List(ProjectRecordKind.Project).Single();
            Require(store.Read(ProjectRecordKind.Project, projectRecord).Data.SequenceEqual(project), "project roundtrip");
            Guid cableRecord = store.List(ProjectRecordKind.Cable).Single();
            Require(store.Read(ProjectRecordKind.Cable, cableRecord).Data.SequenceEqual(cable), "cable roundtrip");
            Require(store.List(ProjectRecordKind.Acquisition).Single() == acquisitionId, "acquisition inventory");
            Require(store.List(ProjectRecordKind.RawChunk).Count == 3, "stream divided into bounded chunks");
            string transfer = Path.Combine(output, "transfer-" + Guid.NewGuid().ToString("N"));
            store.ExportTransfer(transfer, generatedPassphrase);
            Require(File.Exists(Path.Combine(transfer, "transfer.owkey")), "portable key present");
            Require(!File.Exists(Path.Combine(transfer, "vault.owkey")), "Windows account key excluded");
            bool wrongPasswordRejected = false;
            try
            {
                SecureProjectStore.ImportTransfer(transfer,
                    Path.Combine(output, "reject-" + Guid.NewGuid().ToString("N")),
                    "incorrect transfer secret");
            }
            catch (CryptographicException) { wrongPasswordRejected = true; }
            Require(wrongPasswordRejected, "wrong transfer passphrase must fail");
            string imported = Path.Combine(output, "import-" + Guid.NewGuid().ToString("N"));
            using (SecureProjectStore copy = SecureProjectStore.ImportTransfer(transfer, imported,
                generatedPassphrase))
            {
                Require(copy.ProjectId == projectId, "transfer project identity");
                Require(copy.Read(ProjectRecordKind.Project, projectRecord).Data.SequenceEqual(project), "transfer content");
                using (MemoryStream reconstructed = new MemoryStream())
                {
                    copy.ReadStream(ProjectRecordKind.LegacySource, streamedId, reconstructed);
                    Require(reconstructed.Length == 17L * 1024 * 1024, "transferred chunked stream");
                }
            }
            byte[] indexBytes = File.ReadAllBytes(Path.Combine(imported, "index.owidx"));
            indexBytes[indexBytes.Length - 1] ^= 0x01;
            File.WriteAllBytes(Path.Combine(imported, "index.owidx"), indexBytes);
            bool indexTamperRejected = false;
            try { using (SecureProjectStore ignored = SecureProjectStore.Open(imported)) { } }
            catch (CryptographicException) { indexTamperRejected = true; }
            Require(indexTamperRejected, "index tamper must fail closed");
        }

        string recordPath = Path.Combine(path, "calibration", calibrationId.ToString("N") + ".owk");
        byte[] raw = File.ReadAllBytes(recordPath);
        Require(Encoding.UTF8.GetString(raw).IndexOf("校准系数", StringComparison.Ordinal) < 0, "plaintext leaked");
        raw[raw.Length - 1] ^= 0x01;
        File.WriteAllBytes(recordPath, raw);
        using (SecureProjectStore store = SecureProjectStore.Open(path))
        {
            bool tamperDetected = false;
            try { store.Read(ProjectRecordKind.Calibration, calibrationId); }
            catch (CryptographicException) { tamperDetected = true; }
            Require(tamperDetected, "tamper must fail closed");
        }
        File.Delete(Path.Combine(path, "analysis", analysisId.ToString("N") + ".owa"));
        bool deletionDetected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Open(path)) { } }
        catch (InvalidDataException) { deletionDetected = true; }
        Require(deletionDetected, "record deletion must fail closed");

        bool overwriteRejected = false;
        try { SecureProjectStore.Create(path, project, cable); }
        catch (IOException) { overwriteRejected = true; }
        Require(overwriteRejected, "existing project cannot be overwritten");
        CheckPathBudget(output);
        CheckTransactions(output);
        CheckStreamFailures(output);
        CheckTransferSnapshot(output, generatedPassphrase);
        Console.WriteLine("SECURE_STORE_PASS=" + checks + " " + path);
    }

    private static int RunChild(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arguments);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        using (Process child = Process.Start(info))
        {
            if (!child.WaitForExit(30000)) { child.Kill(); throw new TimeoutException("crash child timeout"); }
            return child.ExitCode;
        }
    }

    private static void CheckPathBudget(string output)
    {
        string tooLong = Path.Combine(output, new string('p', 210 - output.Length - 1));
        bool rejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Create(tooLong, Utf8("project"), Utf8("cable"))) { } }
        catch (PathTooLongException) { rejected = true; }
        Require(rejected && !Directory.Exists(tooLong), "long container rejected before writing");

        // A 158-character destination used to exceed MAX_PATH after both the
        // staging and record temporary suffixes were appended. Keep this exact
        // path-depth regression even when the surrounding suite path changes.
        string source = Path.Combine(output, new string('s', 158 - output.Length - 1));
        string target = Path.Combine(output, new string('t', 158 - output.Length - 1));
        using (SecureProjectStore store = SecureProjectStore.Create(source, Utf8("project"), Utf8("cable")))
        {
            store.Append(ProjectRecordKind.ReportArtifact, Utf8("private artifact"), Guid.Empty);
            store.ExportTransfer(target, SecureProjectStore.GenerateTransferPassphrase());
        }
        Require(File.Exists(Path.Combine(target, "transfer.owkey")), "deep export succeeds without long-path policy");
    }

    private static string NewProject(string output, string prefix)
    {
        string path = Path.Combine(output, prefix + "-" + Guid.NewGuid().ToString("N"));
        using (SecureProjectStore ignored = SecureProjectStore.Create(path, Utf8("project"), Utf8("cable"))) { }
        return path;
    }

    private static void CheckTransactions(string output)
    {
        string locked = NewProject(output, "lock");
        using (SecureProjectStore first = SecureProjectStore.Open(locked))
        {
            bool rejected = false;
            try { using (SecureProjectStore ignored = SecureProjectStore.Open(locked)) { } }
            catch (IOException) { rejected = true; }
            Require(rejected, "second instance cannot acquire stale index");
            Require(RunChild("--lock \"" + locked + "\"") == 0, "cross-process writer lock");
            first.Append(ProjectRecordKind.Analysis, Utf8("first writer"), Guid.Empty);
        }
        using (SecureProjectStore reopened = SecureProjectStore.Open(locked))
        {
            Require(reopened.List(ProjectRecordKind.Analysis).Count == 1, "lock released on dispose");
            System.Threading.Tasks.Task[] writes = Enumerable.Range(0, 6).Select(n => System.Threading.Tasks.Task.Run(() =>
                reopened.Append(ProjectRecordKind.Analysis, Utf8("parallel " + n), Guid.Empty))).ToArray();
            System.Threading.Tasks.Task.WaitAll(writes);
            Require(reopened.List(ProjectRecordKind.Analysis).Count == 7, "same-instance concurrent appends serialized");
        }

        string[] phases = { "journal-partial", "journal-staged", "journal-published", "record-temp-partial",
            "record-temp-written", "record-published", "index-temp-partial", "index-temp-written",
            "index-published", "journal-removed" };
        foreach (string phase in phases)
        {
            string path = NewProject(output, "crash-" + phase);
            Require(RunChild("--crash \"" + path + "\" " + phase) == 73, "crash checkpoint " + phase);
            using (SecureProjectStore recovered = SecureProjectStore.Open(path))
            {
                int expected = phase == "journal-partial" || phase == "journal-staged" ? 0 : 1;
                Require(recovered.List(ProjectRecordKind.Calibration).Count == expected, "atomic recovery " + phase);
                if (expected != 0)
                    Require(Encoding.UTF8.GetString(recovered.Read(ProjectRecordKind.Calibration,
                        recovered.List(ProjectRecordKind.Calibration).Single()).Data) == "crash durable record", "recovered content " + phase);
                recovered.Append(ProjectRecordKind.Analysis, Utf8("after recovery"), Guid.Empty);
            }
            using (SecureProjectStore reopened = SecureProjectStore.Open(path))
                Require(reopened.List(ProjectRecordKind.Analysis).Count == 1, "recovery is repeatable " + phase);
            Require(!File.Exists(Path.Combine(path, "pending.owtxn")), "committed journal removed " + phase);
        }

        string interrupted = NewProject(output, "write-failure");
        using (SecureProjectStore store = SecureProjectStore.Open(interrupted))
        {
            bool failed = false;
            SecureProjectStore.FaultInjection = phase => { if (phase == "index-temp-written") throw new IOException("injected index failure"); };
            try { store.Append(ProjectRecordKind.Calibration, Utf8("recover me"), Guid.Empty); }
            catch (IOException) { failed = true; }
            finally { SecureProjectStore.FaultInjection = null; }
            Require(failed, "index failure injected");
            bool staleRejected = false;
            try { store.List(ProjectRecordKind.Calibration); }
            catch (IOException) { staleRejected = true; }
            Require(staleRejected, "failed instance requires reopen");
        }
        using (SecureProjectStore store = SecureProjectStore.Open(interrupted))
            Require(store.List(ProjectRecordKind.Calibration).Count == 1, "index failure recovers durable record");

        string corrupted = NewProject(output, "journal-tamper");
        Require(RunChild("--crash \"" + corrupted + "\" journal-published") == 73, "prepare journal tamper");
        string journalPath = Path.Combine(corrupted, "pending.owtxn");
        byte[] journal = File.ReadAllBytes(journalPath);
        journal[journal.Length - 1] ^= 1;
        File.WriteAllBytes(journalPath, journal);
        bool tamperRejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Open(corrupted)) { } }
        catch (CryptographicException) { tamperRejected = true; }
        Require(tamperRejected, "unauthenticated recovery journal rejected");

        string replay = NewProject(output, "journal-replay");
        Require(RunChild("--crash \"" + replay + "\" journal-published") == 73, "prepare journal replay");
        journal = File.ReadAllBytes(Path.Combine(replay, "pending.owtxn"));
        using (SecureProjectStore store = SecureProjectStore.Open(replay))
            store.Append(ProjectRecordKind.Analysis, Utf8("later revision"), Guid.Empty);
        File.WriteAllBytes(Path.Combine(replay, "pending.owtxn"), journal);
        bool replayRejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Open(replay)) { } }
        catch (CryptographicException) { replayRejected = true; }
        Require(replayRejected, "journal bound to previous index revision");

        string unknown = NewProject(output, "unknown-record");
        Directory.CreateDirectory(Path.Combine(unknown, "analysis"));
        File.WriteAllBytes(Path.Combine(unknown, "analysis", Guid.NewGuid().ToString("N") + ".owa"), Utf8("unknown"));
        bool unknownRejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Open(unknown)) { } }
        catch (InvalidDataException) { unknownRejected = true; }
        Require(unknownRejected, "unlogged unknown records are never discarded");
        foreach (string location in new[] { "root-file", "root-directory", "nested-directory" })
        {
            string extraPath = NewProject(output, location);
            if (location == "root-file") File.WriteAllBytes(Path.Combine(extraPath, "unknown.bin"), Utf8("unknown"));
            else Directory.CreateDirectory(Path.Combine(extraPath, location == "root-directory" ? "unknown" : "project\\nested"));
            bool extraRejected = false;
            try { using (SecureProjectStore ignored = SecureProjectStore.Open(extraPath)) { } }
            catch (InvalidDataException) { extraRejected = true; }
            Require(extraRejected, "reject unknown container entry " + location);
        }

        string deleted = NewProject(output, "committed-delete");
        Require(RunChild("--crash \"" + deleted + "\" index-published") == 73, "prepare committed deletion");
        File.Delete(Directory.GetFiles(Path.Combine(deleted, "calibration")).Single());
        bool deletionRejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.Open(deleted)) { } }
        catch (InvalidDataException) { deletionRejected = true; }
        Require(deletionRejected, "recovery does not conceal committed record deletion");
    }

    private static void CheckStreamFailures(string output)
    {
        string path = NewProject(output, "stream-failure");
        using (SecureProjectStore store = SecureProjectStore.Open(path))
        {
            Guid id;
            using (MemoryStream input = new MemoryStream(new byte[9 * 1024 * 1024]))
                id = store.AppendStream(ProjectRecordKind.LegacySource, input, Guid.Empty);
            byte[] descriptor = store.Read(ProjectRecordKind.LegacySource, id).Data;
            descriptor[21] ^= 1;
            Guid badDigest = store.Append(ProjectRecordKind.LegacySource, descriptor, Guid.Empty);
            using (MemoryStream sink = new MemoryStream())
            {
                bool rejected = false;
                try { store.ReadStream(ProjectRecordKind.LegacySource, badDigest, sink); }
                catch (CryptographicException) { rejected = true; }
                Require(rejected && sink.Length == 0, "aggregate digest failure emits no plaintext");
            }
            byte[] lastId = new byte[16];
            Buffer.BlockCopy(descriptor, descriptor.Length - 16, lastId, 0, 16);
            string chunk = Path.Combine(path, "chunks", new Guid(lastId).ToString("N") + ".owx");
            byte[] damaged = File.ReadAllBytes(chunk);
            damaged[damaged.Length - 1] ^= 1;
            File.WriteAllBytes(chunk, damaged);
            using (MemoryStream sink = new MemoryStream())
            {
                bool rejected = false;
                try { store.ReadStream(ProjectRecordKind.LegacySource, id, sink); }
                catch (CryptographicException) { rejected = true; }
                Require(rejected && sink.Length == 0, "late chunk tamper emits no plaintext");
            }
        }
    }

    private static void CheckTransferSnapshot(string output, string passphrase)
    {
        string path = NewProject(output, "transfer-snapshot");
        string package = Path.Combine(output, "snapshot-package-" + Guid.NewGuid().ToString("N"));
        using (SecureProjectStore store = SecureProjectStore.Open(path))
        {
            string record = Path.Combine(path, "project", store.List(ProjectRecordKind.Project).Single().ToString("N") + ".owp");
            byte[] original = File.ReadAllBytes(record);
            bool mutated = false;
            SecureProjectStore.FaultInjection = phase =>
            {
                if (phase != "transfer-record-verified" || mutated) return;
                byte[] changed = (byte[])original.Clone();
                changed[changed.Length - 1] ^= 1;
                File.WriteAllBytes(record, changed);
                mutated = true;
            };
            try { store.ExportTransfer(package, passphrase); }
            finally { SecureProjectStore.FaultInjection = null; File.WriteAllBytes(record, original); }
            Require(mutated, "transfer race injected");
        }
        using (SecureProjectStore copy = SecureProjectStore.ImportTransfer(package,
            Path.Combine(output, "snapshot-import-" + Guid.NewGuid().ToString("N")), passphrase))
            Require(Encoding.UTF8.GetString(copy.Read(ProjectRecordKind.Project,
                copy.List(ProjectRecordKind.Project).Single()).Data) == "project", "export copies verified bytes despite source replacement");
        string extra = Path.Combine(package, "project", Guid.NewGuid().ToString("N") + ".owp");
        File.WriteAllBytes(extra, new byte[1024]);
        string rejectedTarget = Path.Combine(output, "reject-extra-" + Guid.NewGuid().ToString("N"));
        bool rejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.ImportTransfer(package, rejectedTarget, passphrase)) { } }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && !Directory.Exists(rejectedTarget), "unindexed transfer files rejected before publication");
        File.Delete(extra);
        string oversized = Directory.GetFiles(Path.Combine(package, "project")).Single();
        using (FileStream file = new FileStream(oversized, FileMode.Open, FileAccess.Write))
            file.SetLength(65L * 1024 * 1024 + 1);
        rejectedTarget = Path.Combine(output, "reject-oversized-" + Guid.NewGuid().ToString("N"));
        rejected = false;
        try { using (SecureProjectStore ignored = SecureProjectStore.ImportTransfer(package, rejectedTarget, passphrase)) { } }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected && !Directory.Exists(rejectedTarget), "oversized indexed transfer record rejected before copying");
    }
}
