using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// All mutation is confined to a new QA directory; no hardware or real projects.
static class ProjectFileTransactionsSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type operations, scanner, recordType; static int checks; static string qa;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
    static T Get<T>(object obj, string name) { return (T)obj.GetType().GetProperty(name, All).GetValue(obj, null); }
    static object Call(Type type, string name, params object[] args)
    {
        try { return type.GetMethod(name, All).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }
    static string Fixture(string name, bool report = false)
    {
        string path = Path.Combine(qa, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n始端地点 = QA\r\n终端地点 = END\r\n电缆长度 = 400\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t终端\t户内冷缩\t400m\r\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(path, "zz-raw.dem"), new byte[] { 0, 1, 254, 255 });
        if (report) File.WriteAllBytes(Path.Combine(path, "result.docx"), new byte[] { 1, 2, 3 });
        return path;
    }
    static Array Records(params string[] paths)
    {
        Array result = Array.CreateInstance(recordType, paths.Length);
        for (int i = 0; i < paths.Length; i++) result.SetValue(Call(scanner, "ReadProject", paths[i]), i);
        return result;
    }
    static Dictionary<string, string> Snapshot(string path)
    {
        using (var sha = SHA256.Create()) return Directory.GetFiles(path, "*", SearchOption.AllDirectories).ToDictionary(
            file => file.Substring(path.Length), file => BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))) + ":" + File.GetAttributes(file));
    }
    static void Same(Dictionary<string,string> expected, string path, string label)
    {
        var actual = Snapshot(path); Check(actual.Count == expected.Count, label + " file count");
        foreach (var file in expected) Check(actual.ContainsKey(file.Key) && actual[file.Key] == file.Value, label + file.Key);
    }
    static void Roots()
    {
        string drive = Path.GetPathRoot(qa);
        string normalized = (string)Call(operations, "NormalizeDestination", drive);
        Check(normalized == drive, "drive root remains absolute");
        Check((string)Call(scanner, "NormalizeExistingDirectory", drive) == drive, "scanner preserves root too");
        Check((string)Call(operations, "SafeCombine", normalized, "QA-no-write") == Path.Combine(drive, "QA-no-write"), "root export plans exact target (no root write)");
        Check((string)Call(operations, "SafeCombineRelative", drive, "folder\\file.docx") == Path.Combine(drive, "folder", "file.docx"), "relative report root preserved");
        bool rejected = false; try { Call(operations, "SafeCombineRelative", qa, "..\\outside.docx"); } catch (IOException) { rejected = true; }
        Check(rejected, "relative traversal remains rejected");
    }
    static void Exports()
    {
        string one = Fixture("export-one"), two = Fixture("export-two"), destination = Path.Combine(qa, "exports");
        var beforeOne = Snapshot(one); var beforeTwo = Snapshot(two); object result;
        using (var locked = new FileStream(Path.Combine(two, "zz-raw.dem"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Call(operations, "ExportProjects", Records(one, two), destination);
        Check(!Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 1, "second export failure reports first success; actual: " + Get<string>(result, "Error"));
        string[] outputs = Get<string[]>(result, "OutputPaths"); Check(outputs.Length == 1 && Directory.Exists(outputs[0]), "first actual output path retained");
        Same(beforeOne, outputs[0], "complete first export");
        Check(Get<string>(result, "Error").Contains(outputs[0]), "partial error tells actual completed path");
        Check(((Array)Get<object>(Call(scanner, "Scan", destination), "Projects")).Length == 1, "staging not advertised as project");
        Same(beforeOne, one, "export never changes first source"); Same(beforeTwo, two, "export never changes failed source");
        result = Call(operations, "ExportProjects", Records(one, two), destination);
        Check(Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 2, "retry completes both without overwriting original export");
        Check(Get<string[]>(result, "OutputPaths").All(p => p != outputs[0]), "retry unique outputs");

        one = Fixture("reports-one", true); two = Fixture("reports-two", true); destination = Path.Combine(qa, "reports");
        var records = Records(one, two);
        using (var locked = new FileStream(Path.Combine(two, "result.docx"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = Call(operations, "ExportReports", records, destination);
        outputs = Get<string[]>(result, "OutputPaths");
        Check(!Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 1 && outputs.Length == 1 && File.Exists(outputs[0]), "report batch failure exposes exactly published report");
        Check(File.ReadAllBytes(outputs[0]).SequenceEqual(new byte[] { 1, 2, 3 }), "published report unchanged");
        Check(Get<string>(result, "Error").Contains("未完成临时副本"), "leftover staging explicitly identified rather than hidden");
    }
    static void Removal()
    {
        string project = Fixture("readonly-removal"); string raw = Path.Combine(project, "zz-raw.dem");
        File.SetAttributes(raw, FileAttributes.ReadOnly); File.SetAttributes(Path.Combine(project, "CABLE.in"), FileAttributes.ReadOnly);
        var before = Snapshot(project); object result = Call(operations, "DeleteProjects", qa, Records(project));
        Check(Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 1, "read-only project safely quarantined");
        Check(!Directory.Exists(project) && Get<string[]>(result, "OutputPaths").Single() == project, "original namespace removed and result identity preserved");
        string recovery = Get<string[]>(result, "RecoveryPaths").Single();
        Check(Path.GetDirectoryName(recovery) == Path.GetDirectoryName(project), "same-parent recovery (no cross volume)");
        Same(before, recovery, "all original bytes and attributes preserved");
        var metadata = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(recovery + ".restore.json"));
        Check((string)metadata["OriginalPath"] == project && (string)metadata["RecoveryPath"] == recovery, "durable unambiguous recovery mapping");
        Check(!(bool)Call(scanner, "IsProjectDirectory", recovery), "quarantine cannot be reopened as active project");
        Check(!((IEnumerable)Get<object>(Call(scanner, "Scan", qa), "Projects")).Cast<object>().Any(p => Get<string>(p, "ProjectPath") == recovery), "recursive scan excludes recovery tree");
        Check(Get<string>(result, "Message").Contains("未释放磁盘空间"), "honest recoverable deletion message");
        Check(recovery.StartsWith(qa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(project), "test restore exact QA scope");
        Directory.Move(recovery, project); Same(before, project, "manual restore recovers intact project");
        Check((bool)Call(scanner, "IsProjectDirectory", project), "restored project discoverable");

        string first = Fixture("partial-removal-first"), second = Fixture("partial-removal-second");
        var beforeFirst = Snapshot(first); var beforeSecond = Snapshot(second);
        var hook = operations.GetField("BeforeRecoverableMoveForTests", All); int moves = 0;
        hook.SetValue(null, new Action<string>(path => { if (++moves == 2) throw new IOException("injected rename failure"); }));
        try { result = Call(operations, "DeleteProjects", qa, Records(first, second)); }
        finally { hook.SetValue(null, null); }
        string moved = Get<string[]>(result, "OutputPaths").Single(); string intact = moved == first ? second : first;
        Check(!Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 1, "partial rename count accurate");
        Check(Get<string[]>(result, "RecoveryPaths").Length == 1, "partial rename recovery count accurate");
        Same(moved == first ? beforeFirst : beforeSecond, Get<string[]>(result, "RecoveryPaths")[0], "completed partial recovery intact");
        Same(intact == first ? beforeFirst : beforeSecond, intact, "failed rename retains full original project");
        Check(Get<string>(result, "Error").Contains(Get<string[]>(result, "RecoveryPaths")[0]) && Get<string>(result, "Error").Contains(intact), "partial removal tells both states");

        string nestedParent = Fixture("overlap"), nestedChild = Fixture("overlap\\child");
        result = Call(operations, "DeleteProjects", qa, Records(nestedParent, nestedChild));
        Check(!Get<bool>(result, "Ok") && Get<int>(result, "ProcessedCount") == 0 && Directory.Exists(nestedParent) && Directory.Exists(nestedChild), "overlap preflight prevents stale recovery paths");
        result = Call(operations, "DeleteProjects", nestedParent, Records(nestedParent));
        Check(!Get<bool>(result, "Ok") && Directory.Exists(nestedParent), "storage root itself never removable");
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("BuildRoot QAroot");
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectManagement", "Xiongxin.OWTS.ProjectManagement.dll"));
            operations = assembly.GetType("HVX.Controls.ProjectFileOperations", true); scanner = assembly.GetType("HVX.Controls.ProjectScanner", true); recordType = assembly.GetType("HVX.Controls.ProjectRecord", true);
            // Leave headroom for legacy .NET Framework's export staging name.
            // Keep the complete unique ID and all file-transaction assertions.
            qa = Path.Combine(Path.GetFullPath(args[1]), "pft-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            Roots(); Exports(); Removal(); Console.WriteLine("PASS " + checks + "; HARDWARE_COMMANDS=0; REAL_PROJECT_WRITES=0; QA=" + qa); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
