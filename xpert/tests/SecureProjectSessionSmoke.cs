using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.OWTS.Desktop;

internal static class SecureProjectSessionSmoke
{
    static int checks;
    static byte[] Bytes(string value) { return Encoding.UTF8.GetBytes(value); }
    static void Check(bool result, string why) { checks++; if (!result) throw new Exception(why); }
    static void Reject<T>(Action action, string why) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected refusal: " + why); }
    static ProjectFileChange Change(string name, ProjectRecordKind kind, string value) { return new ProjectFileChange(name, kind, Bytes(value)); }
    static string Item(IProjectSession session) { return Encoding.UTF8.GetString(session.ReadMetadata("ITEM.in")); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--crash-before-head")
            {
                using (var child = SecureProjectSession.Open(args[1]))
                {
                    SecureProjectSession.BeforePublishForTests = () => Environment.Exit(73);
                    child.Commit(new[] { Change("ITEM.in", ProjectRecordKind.Project, "unpublished secret"), Change("CABLE.in", ProjectRecordKind.Cable, "unpublished cable") }, child.Revision, CancellationToken.None);
                }
                return 74;
            }
            string fixture = Path.GetFullPath(args.Length > 1 ? args[1] : args[0]);
            if (fixture.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0) throw new ArgumentException("Use build evidence.");
            Directory.CreateDirectory(fixture);
            string root = Path.Combine(fixture, "session-" + Guid.NewGuid().ToString("N"));
            var original = Bytes("private project data for original revision");
            using (var s = SecureProjectSession.Create(root, original, Bytes("private cable data")))
            {
                Check(s.Revision == 1 && s.ListFiles().SequenceEqual(new[] { "CABLE.in", "ITEM.in" }), "initial complete revision");
                original[0] = 0;
                Check(Item(s).StartsWith("private"), "create snapshots plaintext");
                Check(!File.Exists(Path.Combine(root, "ITEM.in")) && !File.Exists(Path.Combine(root, "CABLE.in")), "no plaintext sidecar");
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    // The exclusive writer lock contains no project data and cannot be read while the session is open.
                    if (String.Equals(Path.GetFileName(file), "writer.owlock", StringComparison.OrdinalIgnoreCase)) continue;
                    Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("private project data"), "data not readable on disk");
                }
                Check(s.Commit(new[] { Change("ITEM.in", ProjectRecordKind.Project, "revision two project"), Change("CABLE.in", ProjectRecordKind.Cable, "revision two cable") }, 1, CancellationToken.None) == 2, "pair publishes one revision");
                Check(Item(s) == "revision two project" && Encoding.UTF8.GetString(s.ReadMetadata("CABLE.in")) == "revision two cable", "no mixed metadata");
                Reject<IOException>(() => s.Commit(new[] { Change("ITEM.in", ProjectRecordKind.Project, "stale write") }, 1, CancellationToken.None), "stale revision");
                Check(s.Revision == 2 && Item(s) == "revision two project", "stale write retains head");
                foreach (string name in new[] { "../ITEM.in", "a/../b", "C:/data", "a\\b", "/root", "a/", "CON", "nul.txt", "a//b", "a. " })
                    Reject<ArgumentException>(() => Change(name, ProjectRecordKind.Analysis, "bad"), "invalid logical name");
                Reject<ArgumentException>(() => s.Commit(new[] { Change("a.json", ProjectRecordKind.Analysis, "a"), Change("A.JSON", ProjectRecordKind.Analysis, "b") }, 2, CancellationToken.None), "case duplicate");
                Reject<ArgumentException>(() => s.Commit(new[] { ProjectFileChange.Delete("ITEM.in") }, 2, CancellationToken.None), "mandatory metadata cannot be deleted");
                Reject<ArgumentException>(() => s.Commit(new[] { Change("ITEM.in", ProjectRecordKind.Acquisition, "wrong") }, 2, CancellationToken.None), "record kind binds metadata");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Reject<OperationCanceledException>(() => s.CommitAsync(new[] { Change("ITEM.in", ProjectRecordKind.Project, "cancelled") }, 2, cancelled.Token).GetAwaiter().GetResult(), "cancelled async commit");
                }
                Check(s.Revision == 2, "pre-cancel preserves head");
                SecureProjectSession.BeforePublishForTests = () => { throw new IOException("injected before manifest"); };
                try { Reject<IOException>(() => s.Commit(new[] { Change("ITEM.in", ProjectRecordKind.Project, "orphan project"), Change("CABLE.in", ProjectRecordKind.Cable, "orphan cable") }, 2, CancellationToken.None), "pair failed before publication"); }
                finally { SecureProjectSession.BeforePublishForTests = null; }
                Check(s.Revision == 2 && Item(s) == "revision two project", "unpublished records stay invisible");
            }
            using (var reopened = SecureProjectSession.Open(root))
                Check(reopened.Revision == 2 && Item(reopened) == "revision two project", "reopen ignores unreferenced records");
            var start = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, "--crash-before-head \"" + root + "\"")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(start))
            {
                if (!p.WaitForExit(20000)) { p.Kill(); p.WaitForExit(); throw new Exception("Crash probe timeout"); }
                Check(p.ExitCode == 73, "actual child crash before head");
            }
            using (var s = SecureProjectSession.Open(root))
            {
                Check(s.Revision == 2 && Item(s) == "revision two project", "crash preserves complete previous pair");
                byte[] raw = Enumerable.Range(0, 9 * 1024 * 1024 + 17).Select(i => (byte)(i % 251)).ToArray();
                var input = (byte[])raw.Clone();
                long revision = s.CommitAsync(new[] {
                    new ProjectFileChange("A/sample.LOC", ProjectRecordKind.Acquisition, () => new MemoryStream(input, false)),
                    Change("Analysis/notes.json", ProjectRecordKind.Analysis, "approved operator notes")
                }, 2, CancellationToken.None).GetAwaiter().GetResult();
                Check(revision == 3 && s.ListFiles().Length == 4, "stream and analysis publish together");
                Reject<InvalidDataException>(() => s.ReadMetadata("A/sample.LOC"), "large waveform not loaded as metadata");
                using (var output = new MemoryStream())
                {
                    s.ReadFile("a/SAMPLE.loc", output);
                    using (var sha = SHA256.Create()) Check(sha.ComputeHash(raw).SequenceEqual(sha.ComputeHash(output.ToArray())), "stream exact byte preservation across chunks");
                }
                long expected = s.Revision;
                Task<long> first = s.CommitAsync(new[] { Change("Analysis/one.json", ProjectRecordKind.Analysis, "one") }, expected, CancellationToken.None);
                Task<long> second = s.CommitAsync(new[] { Change("Analysis/two.json", ProjectRecordKind.Analysis, "two") }, expected, CancellationToken.None);
                int success = 0, stale = 0;
                foreach (Task<long> t in new[] { first, second })
                    try { t.GetAwaiter().GetResult(); success++; } catch (IOException) { stale++; }
                Check(success == 1 && stale == 1 && s.Revision == expected + 1, "one concurrent writer wins, other reloads");
                s.Commit(new[] { ProjectFileChange.Delete("Analysis/notes.json") }, s.Revision, CancellationToken.None);
                Reject<FileNotFoundException>(() => s.ReadMetadata("Analysis/notes.json"), "deleted logical record hidden");
                Check(s.ListFiles().Contains("A/sample.LOC"), "delete retains raw capture");
                var source = Bytes("constructor snapshot");
                var snapshot = new ProjectFileChange("Analysis/snapshot.json", ProjectRecordKind.Analysis, source); source[0] = 0;
                s.Commit(new[] { snapshot }, s.Revision, CancellationToken.None);
                Check(Encoding.UTF8.GetString(s.ReadMetadata("Analysis/snapshot.json")) == "constructor snapshot", "async input independent of caller byte buffer");
            }
            using (var s = SecureProjectSession.Open(root))
                Check(s.Revision == 6 && s.ListFiles().Contains("A/sample.LOC"), "full session reopens with revisions");
            Console.WriteLine("SECURE_PROJECT_SESSION_PASS=" + checks);
            Console.WriteLine("HARDWARE_ACCESSED=False;BUSINESS_MODULES_MIGRATED=False;EVIDENCE=" + root);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { SecureProjectSession.BeforePublishForTests = null; }
    }
}
