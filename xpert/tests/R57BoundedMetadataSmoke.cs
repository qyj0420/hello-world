using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HVX.ProjectCables;
using HVX.Controls;
using LabVIEWItemInformation;

internal static class R57BoundedMetadataSmoke
{
    static int checks;
    static void Check(bool value, string label) { checks++; if (!value) throw new Exception(label); }
    static Exception Failure(Action action) { try { action(); } catch (Exception ex) { return ex is TargetInvocationException ? ex.InnerException : ex; } throw new Exception("Expected rejection."); }
    static string Hash(byte[] data) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(data)); }
    static string Fixture(string root, string name, Encoding encoding, bool english)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        string item = english ? "[ITEM]\r\nStartName = 北京 José\r\nEndName = Zürich 终端\r\nCoreCount = Three-core\r\nCableLength = 400\r\nALength = 400\r\nBLength = 400\r\nCLength = 400\r\nPhaseVoltage = 8.7kV\r\nTestMode = Arbitrary\r\nRemarks = Unicode 中文\r\n"
            : "[ITEM]\r\n始端地点 = 北京\r\n终端地点 = 终端\r\n电缆芯数 = 三芯\r\n电缆长度 = 400\r\nA相电缆长度 = 400\r\nB相电缆长度 = 400\r\nC相电缆长度 = 400\r\n相电压 = 8.7kV\r\n加压模式 = 任意值\r\n备注 = 原中文\r\n";
        File.WriteAllText(Path.Combine(path, "ITEM.in"), item, encoding);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), "0\tA\t\t\r\n1\tStart\tType\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tType\t400m\r\n", encoding);
        return path;
    }
    sealed class ChangingStream : MemoryStream
    {
        readonly long reported;
        internal ChangingStream(byte[] bytes, long reportedLength) : base(bytes, false) { reported = reportedLength; }
        public override long Length { get { return reported; } }
    }
    static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var encodings = new Encoding[] { new UTF8Encoding(false, true), new UTF8Encoding(true, true), Encoding.GetEncoding(936), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) };
            for (int index = 0; index < encodings.Length; index++)
            {
                bool english = index < 2;
                string path = Fixture(root, "encoding-" + index, encodings[index], english);
                byte[] original = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                var document = ItemDocument.Load(path);
                Check(document.Values["StartName"] == (english ? "北京 José" : "北京"), "browser legacy/Unicode decode " + index);
                Check(document.CablePositions[0].Last() == 400, "browser cable decode " + index);
                var draft = ProjectCreationStorage.Load(path);
                Check(draft.StartName == (english ? "北京 José" : "北京"), "editor legacy/Unicode decode " + index);
                Check(draft.LengthA == 400 && draft.PressureMode == ProjectPressureModes.Arbitrary, "editor numeric and enum " + index);
                Check(Hash(original) == BoundedProjectMetadata.HashFile(Path.Combine(path, "ITEM.in")), "stream hash equality " + index);
                string copy = Path.Combine(root, "copy-" + index + ".in"); document.SaveCopy(copy);
                byte[] copied = File.ReadAllBytes(copy);
                Check(copied.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "copy UTF8 BOM " + index);
                Check(new UTF8Encoding(false, true).GetString(copied).Contains("StartName"), "copy English stable keys " + index);
                Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))), "read and copy source untouched " + index);
            }
            int limit = BoundedProjectMetadata.MaximumBytes;
            string exact = Path.Combine(root, "byte-exact.in"); byte[] exactBytes = new byte[limit]; File.WriteAllBytes(exact, exactBytes);
            Check(BoundedProjectMetadata.ReadBytes(exact).Length == limit, "exact byte budget read");
            Check(BoundedProjectMetadata.HashFile(exact) == Hash(exactBytes), "exact byte budget stream hash");
            string large = Path.Combine(root, "byte-too-large.in"); File.WriteAllBytes(large, new byte[limit + 1]);
            Check(Failure(() => BoundedProjectMetadata.ReadBytes(large)) is ProjectMetadataLimitException, "oversize actual read rejected");
            Check(Failure(() => BoundedProjectMetadata.HashFile(large)) is ProjectMetadataLimitException, "oversize hash rejected");
            Check(BoundedProjectMetadata.ValidateText(new string('x', BoundedProjectMetadata.MaximumLineCharacters)).Length > 0, "exact line character budget");
            Check(Failure(() => BoundedProjectMetadata.ValidateText(new string('x', BoundedProjectMetadata.MaximumLineCharacters + 1))) is ProjectMetadataLimitException, "single long line rejected before parsing");
            Check(BoundedProjectMetadata.ValidateText(new string('\n', BoundedProjectMetadata.MaximumLines - 1)).Length > 0, "exact line count budget");
            Check(Failure(() => BoundedProjectMetadata.ValidateText(new string('\n', BoundedProjectMetadata.MaximumLines))) is ProjectMetadataLimitException, "line count rejected before splitting");
            Check(Failure(() => BoundedProjectMetadata.ValidateText(new string('x', BoundedProjectMetadata.MaximumCharacters + 1))) is ProjectMetadataLimitException, "decoded character budget");
            Check(Failure(() => BoundedProjectMetadata.ValidateText("test\0invalid")) is InvalidDataException, "NUL metadata rejected");
            using (var grow = new ChangingStream(new byte[8193], 8192)) Check(Failure(() => BoundedProjectMetadata.ReadBytes(grow)) is IOException, "growth after length probe rejected");
            using (var shrink = new ChangingStream(new byte[8191], 8192)) Check(Failure(() => BoundedProjectMetadata.ReadBytes(shrink)) is IOException, "shrink after length probe rejected");
            using (var growPastLimit = new ChangingStream(new byte[limit + 1], limit)) Check(Failure(() => BoundedProjectMetadata.ReadBytes(growPastLimit)) is ProjectMetadataLimitException, "growth past budget rejected");
            string oversize = Fixture(root, "oversize-item", encodings[0], true);
            File.WriteAllBytes(Path.Combine(oversize, "ITEM.in"), new byte[limit + 1]);
            byte[] cable = File.ReadAllBytes(Path.Combine(oversize, "CABLE.in"));
            Check(Failure(() => ItemDocument.Load(oversize)) is ProjectMetadataLimitException, "browser oversized ITEM refuses");
            Check(Failure(() => ProjectCreationStorage.Load(oversize)) is ProjectMetadataLimitException, "editor oversized ITEM refuses");
            Check(File.ReadAllBytes(Path.Combine(oversize, "CABLE.in")).SequenceEqual(cable) && Directory.GetFiles(oversize).Length == 2, "oversize load does not write");
            oversize = Fixture(root, "oversize-cable", encodings[0], true);
            File.WriteAllBytes(Path.Combine(oversize, "CABLE.in"), new byte[limit + 1]);
            Check(Failure(() => ItemDocument.Load(oversize)) is ProjectMetadataLimitException, "browser oversized CABLE not hidden as fallback");
            Check(Failure(() => ProjectCreationStorage.Load(oversize)) is ProjectMetadataLimitException, "editor oversized CABLE refuses");
            string stale = Fixture(root, "changed-file", encodings[0], true); var staleDraft = ProjectCreationStorage.Load(stale);
            File.AppendAllText(Path.Combine(stale, "ITEM.in"), "Vendor = new-owner\r\n", encodings[0]);
            byte[] changed = File.ReadAllBytes(Path.Combine(stale, "ITEM.in")); staleDraft.Notes = "must not replace";
            Check(Failure(() => ProjectCreationStorage.UpdateExisting(staleDraft, stale)) is IOException, "concurrent stale baseline still rejected");
            Check(changed.SequenceEqual(File.ReadAllBytes(Path.Combine(stale, "ITEM.in"))), "concurrent external edit remains exact");
            string locked = Fixture(root, "writer-lock", encodings[0], true);
            using (var writer = new FileStream(Path.Combine(locked, "ITEM.in"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                Check(Failure(() => ProjectCreationStorage.Load(locked)) is IOException, "active writer refused by read sharing");
            var edited = ProjectCreationStorage.Load(locked); edited.Notes = new string('x', BoundedProjectMetadata.MaximumLineCharacters + 1);
            byte[] before = File.ReadAllBytes(Path.Combine(locked, "ITEM.in"));
            Check(Failure(() => ProjectCreationStorage.UpdateExisting(edited, locked)) is ProjectMetadataLimitException, "oversized save refused before staging");
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(locked, "ITEM.in"))), "oversized edit does not modify original");
            Console.WriteLine("PASS R57 bounded metadata checks=" + checks); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
