using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Desktop;

internal static class LegacyProjectConverterSmoke
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        string parent = Path.GetFullPath(args.Length == 0
            ? Path.Combine(Environment.CurrentDirectory, "build", "legacy-converter-smoke")
            : args[0]);
        Directory.CreateDirectory(parent);
        string scratch = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string source = Path.Combine(scratch, "old-project");
            string target = Path.Combine(scratch, "converted-project");
            Directory.CreateDirectory(source);
            byte[] gbkItem = Encoding.GetEncoding(936).GetBytes(
                "[ITEM]\r\n所属单位: = \"雄新试验\"\r\n电缆长度: = \"414.0\"\r\n" +
                "自定义字段: = \"保留未知字段\"\r\n文档路径: = \"C:\\outside\\report.docx\"\r\n");
            File.WriteAllBytes(Path.Combine(source, "ITEM.in"), gbkItem);
            File.WriteAllText(Path.Combine(source, "CABLE.in"),
                "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n备注: = \"未识别\"\r\n",
                new UTF8Encoding(true));
            File.WriteAllBytes(Path.Combine(source, "X.dem"), new byte[] { 0, 1, 2, 3, 255, 128 });
            WriteLargeFile(Path.Combine(source, "X.loc"), 8 * 1024 * 1024 + 17);
            string analysis = Path.Combine(source, "Analysis");
            Directory.CreateDirectory(analysis);
            File.WriteAllText(Path.Combine(analysis, "X.json"), "{\"result\":true}", Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(source, "Y.dem"), new byte[] { 4, 5, 6 });
            File.WriteAllBytes(Path.Combine(source, "Y.loc"), new byte[] { 7, 8, 9 });
            File.WriteAllBytes(Path.Combine(source, "orphan.dem"), new byte[] { 10 });
            File.WriteAllBytes(Path.Combine(source, "lone.loc"), new byte[] { 11 });
            File.WriteAllBytes(Path.Combine(source, "zero.bin"), new byte[0]);
            string nested = Path.Combine(source, "附件");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "说明.txt"), "原始附件", Encoding.UTF8);

            Dictionary<string, string> before = SourceHashes(source);
            LegacyProjectInspection scan = LegacyProjectConverter.Inspect(source);
            Require(scan.CanConvert, "synthetic project is convertible despite nonblocking issues");
            Require(scan.Files.Count == 11, "all eleven source files inventoried");
            Require(scan.Files.All(file => !Path.IsPathRooted(file.RelativePath) &&
                file.RelativePath.IndexOf("..", StringComparison.Ordinal) < 0),
                "migration index uses only safe relative paths");
            Require(scan.ProjectMetadata.Encoding == "GBK", "GBK ITEM.in detected");
            Require(scan.ProjectMetadata.Normalized["cableLength"] == "414.0",
                "known ITEM.in field normalized");
            Require(scan.ProjectMetadata.Fields["自定义字段"] == "保留未知字段",
                "unknown ITEM.in key preserved");
            Require(scan.CableMetadata.Encoding == "UTF-8-BOM", "UTF-8 CABLE.in detected");
            Require(scan.CableMetadata.Rows.Count == 2, "tabular cable rows preserved");
            Require(scan.CableMetadata.Fields["备注"] == "未识别", "unknown CABLE.in key preserved");
            foreach (string code in new[] { "ZeroByteFile", "AbsolutePathReference",
                "OrphanDEM", "OrphanLOC", "MissingAnalysis" })
                Require(scan.Issues.Any(issue => issue.Code == code), "issue is explicit: " + code);
            Require(!scan.Issues.Any(issue => issue.Code == "MissingAnalysis" &&
                issue.RelativePath == "X.dem"), "Analysis/X.json pairs with X.dem/X.loc");

            LegacyProjectConversionResult result = LegacyProjectConverter.Convert(source, target);
            Require(result.FileCount == before.Count && result.IssueCount == scan.Issues.Count,
                "conversion result includes complete file and issue totals");
            Require(Directory.Exists(target), "target published after validation");
            Require(SourceHashes(source).OrderBy(item => item.Key).SequenceEqual(
                before.OrderBy(item => item.Key)), "source files remain byte-for-byte unchanged");
            Require(!Directory.GetDirectories(scratch, ".owm-*").Any(),
                "no staging directory remains after atomic publish");

            using (SecureProjectStore store = SecureProjectStore.Open(target))
            {
                Require(store.ProjectId == result.ProjectId, "published project ID matches result");
                Require(store.List(ProjectRecordKind.MigrationIndex).Count == 1,
                    "exactly one encrypted migration index exists");
                var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
                string indexJson = Encoding.UTF8.GetString(
                    store.Read(ProjectRecordKind.MigrationIndex, result.MigrationIndexId).Data);
                Require(indexJson.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0,
                    "migration index contains relative paths, not source absolute paths");
                LegacyMigrationIndex index = serializer.Deserialize<LegacyMigrationIndex>(indexJson);
                Require(index.Files.Count == before.Count && index.Issues.Count == scan.Issues.Count,
                    "encrypted migration index contains files and problems");
                Require(index.Files.All(file => file.RecordId != Guid.Empty && file.Sha256.Length == 64),
                    "every source, including zero-byte file, has SHA-256 and an encrypted record ID");
                Require(index.Files.First(file => file.RelativePath == "X.loc").Size > 8 * 1024 * 1024,
                    "LOC fixture crosses the 8 MiB streaming boundary");
                foreach (LegacySourceFile file in index.Files)
                {
                    string original = Path.Combine(source, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (file.Size == 0)
                    {
                        Require(file.Representation == "zero-marker" &&
                            Encoding.UTF8.GetString(store.Read(ProjectRecordKind.LegacySource,
                                file.RecordId).Data).Contains("legacy-zero-file-v1"),
                            "zero-byte file has an explicit encrypted marker");
                    }
                    else
                    {
                        using (var output = new MemoryStream())
                        {
                            store.ReadStream(ProjectRecordKind.LegacySource, file.RecordId, output);
                            Require(output.ToArray().SequenceEqual(File.ReadAllBytes(original)),
                                "legacy source bytes round-trip: " + file.RelativePath);
                        }
                    }
                }
                LegacyMetadataDocument project = serializer.Deserialize<LegacyMetadataDocument>(
                    Encoding.UTF8.GetString(store.Read(ProjectRecordKind.Project, index.ProjectRecordId).Data));
                LegacyMetadataDocument cable = serializer.Deserialize<LegacyMetadataDocument>(
                    Encoding.UTF8.GetString(store.Read(ProjectRecordKind.Cable, index.CableRecordId).Data));
                Require(project.Fields["自定义字段"] == "保留未知字段" &&
                    cable.Rows.Count == 2, "normalized encrypted metadata preserves unknown values");
            }

            bool existingRejected = false;
            try { LegacyProjectConverter.Convert(source, target); }
            catch (IOException) { existingRejected = true; }
            Require(existingRejected && Directory.Exists(target), "existing destination is never overwritten");
            bool nestedRejected = false;
            try { LegacyProjectConverter.Convert(source, Path.Combine(source, "new-project")); }
            catch (InvalidDataException) { nestedRejected = true; }
            Require(nestedRejected, "destination inside source is rejected before publishing");

            string cancelledTarget = Path.Combine(scratch, "cancelled-project");
            bool cancelled = false;
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    LegacyProjectConverter.Convert(source, cancelledTarget, cancellation.Token,
                        progress =>
                        {
                            if (progress.Phase == "加密保存" &&
                                progress.CurrentRelativePath == "X.loc" &&
                                progress.CompletedBytes >= 8 * 1024 * 1024)
                                cancellation.Cancel();
                        });
                }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Require(cancelled && !Directory.Exists(cancelledTarget),
                "cancellation during a streamed LOC never publishes a partial target");
            Require(!Directory.GetDirectories(scratch, ".owm-*").Any(),
                "cancellation removes converter-owned staging directory");
            Require(SourceHashes(source).OrderBy(item => item.Key).SequenceEqual(
                before.OrderBy(item => item.Key)), "cancellation also leaves source bytes unchanged");

            string changedTarget = Path.Combine(scratch, "changed-project");
            byte[] originalItem = File.ReadAllBytes(Path.Combine(source, "ITEM.in"));
            bool changed = false;
            bool changeRejected = false;
            try
            {
                LegacyProjectConverter.Convert(source, changedTarget, CancellationToken.None,
                    progress =>
                    {
                        if (!changed && progress.Phase == "加密保存" &&
                            progress.CurrentRelativePath == "Y.dem")
                        {
                            File.AppendAllText(Path.Combine(source, "ITEM.in"), "\r\n外部同时修改", Encoding.UTF8);
                            changed = true;
                        }
                    });
            }
            catch (IOException) { changeRejected = true; }
            finally { File.WriteAllBytes(Path.Combine(source, "ITEM.in"), originalItem); }
            Require(changed && changeRejected && !Directory.Exists(changedTarget),
                "source mutation during migration blocks publication");
            Require(!Directory.GetDirectories(scratch, ".owm-*").Any(),
                "source mutation also removes converter-owned staging");

            Console.WriteLine("LEGACY_PROJECT_CONVERTER_SMOKE_PASS=" + checks +
                " files=" + before.Count + " issues=" + scan.Issues.Count);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            string expectedPrefix = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string checkedScratch = Path.GetFullPath(scratch);
            if (checkedScratch.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(checkedScratch))
                Directory.Delete(checkedScratch, true);
        }
    }

    private static void WriteLargeFile(string path, int length)
    {
        byte[] block = new byte[8192];
        for (int index = 0; index < block.Length; index++) block[index] = (byte)((index * 17 + 13) & 255);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            while (output.Length < length)
            {
                int take = (int)Math.Min(block.Length, length - output.Length);
                output.Write(block, 0, take);
            }
        }
    }

    private static Dictionary<string, string> SourceHashes(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
                .Replace('\\', '/');
            using (SHA256 sha = SHA256.Create())
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                result.Add(relative, BitConverter.ToString(sha.ComputeHash(input)).Replace("-", String.Empty));
        }
        return result;
    }

    private static void Require(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
