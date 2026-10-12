using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class OperationAuditLogSmoke
{
    static int checks;
    static void Check(bool value, string description) { checks++; if (!value) throw new Exception("Check " + checks + ": " + description); }
    static void Stage(string description) { Console.WriteLine("AUDIT_STAGE=" + DateTime.UtcNow.ToString("O") + " " + description); }
    static object Field(object value, string name) { return value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value); }
    static IEnumerable<Control> Descendants(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (Control nested in Descendants(c)) yield return nested; } }
    static void PumpUntil(Func<bool> ready) { var timeout = Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(10); } while (!ready() && timeout.ElapsedMilliseconds < 10000); if (!ready()) throw new TimeoutException("Viewer query timeout."); }
    static OperationAuditLog Create(string path, long size = 1024 * 1024, int files = 30) { return new OperationAuditLog(path, new OperationAuditOptions { MaxFileBytes = size, MaxFilesPerKind = files }); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 1 && args[0] == "--worker")
            {
                var worker = Create(args[1]);
                for (int i = 0; i < 20; i++) if (!worker.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded, null, AuditModule.DataAnalysis)) return 2;
                return 0;
            }
            Application.EnableVisualStyles();
            Application.ThreadException += (sender, error) =>
            {
                Console.Error.WriteLine("AUDIT_UI_THREAD_EXCEPTION=" + error.Exception);
                Environment.Exit(3);
            };
            string qa = Path.Combine(Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(args[0], "OperationAuditLogSmoke")), Guid.NewGuid().ToString("N"));
            if (qa.IndexOf(Path.DirectorySeparatorChar + "build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("QA output must be under build.");
            Directory.CreateDirectory(qa);
            var log = Create(Path.Combine(qa, "logs"));
            Check(!Directory.Exists(log.DirectoryPath), "constructor creates no logs");
            Check(log.Query(AuditLogKind.Project).Entries.Count == 0 && !Directory.Exists(log.DirectoryPath), "empty query readonly");
            Check(!log.TryWriteOperation((OperationAuditAction)999, AuditOutcome.Succeeded), "unknown action rejected");
            Check(!log.TryWriteOperation(OperationAuditAction.ModuleOpen, (AuditOutcome)999), "unknown outcome rejected");
            Check(!log.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded, null, (AuditModule)999), "unknown module rejected");
            Check(!Directory.Exists(log.DirectoryPath), "rejected records create no file");
            string project = Path.Combine(qa, "客户项目"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "unaltered.txt"), "original", Encoding.UTF8);
            Check(log.TryWriteProject(ProjectAuditAction.ProjectCreated, AuditOutcome.Succeeded, project), "project creation logged");
            Check(log.TryWriteProject(ProjectAuditAction.ProjectUpdated, AuditOutcome.Succeeded, project), "project update logged");
            Check(log.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded, project, AuditModule.ProjectCreation), "typed module logged");
            var query = log.Query(AuditLogKind.Project);
            Check(query.Entries.Count == 2 && query.Entries.All(e => e.Kind == AuditLogKind.Project && e.TimeUtc.Offset == TimeSpan.Zero), "separate project category UTC timestamps");
            Check(log.Query(AuditLogKind.Operation).Entries.Single().Module == AuditModule.ProjectCreation, "module roundtrip");
            Check(query.Entries.All(e => e.ProjectPath == project), "normal project path preserved exactly");
            Check(Directory.GetFiles(log.DirectoryPath, "project_*.jsonl").Length == 1 && Directory.GetFiles(log.DirectoryPath, "operation_*.jsonl").Length == 1, "two independent categories");
            foreach (string secret in new[] { "SyntheticPassword@123", "SyntheticToken#456", "Synthetic Access Phrase" })
                Check(log.TryWriteOperation(OperationAuditAction.AuthorizationRejected, AuditOutcome.Failed, @"C:\password=" + secret), "sensitive path omitted, action still retained");
            Check(log.TryWriteOperation(OperationAuditAction.AuthorizationRejected, AuditOutcome.Failed, "Bearer sensitive-token\r\ninjection"), "newline non-path omitted");
            string content = string.Join("", Directory.GetFiles(log.DirectoryPath, "*.jsonl").Select(File.ReadAllText));
            Check(!content.Contains("SyntheticPassword@123") && !content.Contains("SyntheticToken#456") && !content.Contains("Synthetic Access Phrase") && !content.Contains("sensitive-token"), "credentials never persisted");
            Check(log.Query(AuditLogKind.Operation, keyword: "AuthorizationRejected").Entries.All(e => e.ProjectPathOmitted && e.ProjectPath == ""), "omission audit flag");
            Check(log.Query(AuditLogKind.Operation, keyword: "ProjectCreation").Entries.Count == 1, "keyword searches typed module");
            Check(log.Query(AuditLogKind.Operation, DateTimeOffset.UtcNow.AddHours(1)).Entries.Count == 0, "time filter");
            Check(log.Query(AuditLogKind.Operation, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1)).ReadFailureCode == "invalid_query", "invalid interval bounded");
            Check(log.Query(AuditLogKind.Operation, maxRows: 2001).ReadFailureCode == "invalid_query", "row limit validated");
            Check(log.Query(AuditLogKind.Operation, keyword: new string('x',129)).ReadFailureCode == "invalid_query", "keyword length bounded");
            Check(log.Query(AuditLogKind.Operation, maxRows:1).Entries.Count == 1 && log.Query(AuditLogKind.Operation, maxRows:1).IsLimited, "row truncation reported");

            string operations = Directory.GetFiles(log.DirectoryPath, "operation_*.jsonl").Single();
            File.AppendAllText(operations, "{broken}\n" + new string('z', 9000) + "\n{\"partial\":", new UTF8Encoding(false));
            var corrupt = log.Query(AuditLogKind.Operation);
            Check(corrupt.InvalidLines == 2 && corrupt.PartialLines == 1 && corrupt.Entries.Count == 5, "bad and oversized lines tolerate incomplete tail");
            Check(log.TryWriteOperation(OperationAuditAction.ReturnHome, AuditOutcome.Succeeded), "writer recovers after incomplete tail without truncation");
            Check(log.Query(AuditLogKind.Operation).Entries.Count == 6, "next valid record remains readable");
            var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check(log.Query(AuditLogKind.Operation, cancellation:cancel.Token).ReadFailureCode == "cancelled", "query cancellation");
            Check(File.ReadAllText(Path.Combine(project, "unaltered.txt"), Encoding.UTF8) == "original", "project files untouched");

            string rotateRoot = Path.Combine(qa, "rotation"); var rotate = Create(rotateRoot, 2048, 3);
            for (int i = 0; i < 45; i++) Check(rotate.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded), "rotation write " + i);
            var rolled = Directory.GetFiles(rotateRoot, "operation_*.jsonl");
            Check(rolled.Length <= 3 && rolled.All(p => new FileInfo(p).Length <= 2048), "storage bounded by file size and retention");
            Check(rotate.Query(AuditLogKind.Operation).Entries.Count > 0 && rotate.Query(AuditLogKind.Operation).InvalidLines == 0, "rotated JSONL records valid");
            File.WriteAllText(Path.Combine(rotateRoot, "customer-note.txt"), "preserve", Encoding.UTF8);
            for (int i = 0; i < 10; i++) rotate.TryWriteOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded);
            Check(File.ReadAllText(Path.Combine(rotateRoot, "customer-note.txt"), Encoding.UTF8) == "preserve", "retention does not delete unknown files");
            string latest = Directory.GetFiles(rotateRoot, "operation_*.jsonl").OrderByDescending(p => p).First();
            string old = Path.Combine(rotateRoot, "operation_20000101_0000.jsonl"); File.Move(latest, old);
            Check(rotate.TryWriteOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded) && Directory.GetFiles(rotateRoot,"operation_" + DateTime.UtcNow.ToString("yyyyMMdd") + "_*.jsonl").Length > 0, "daily bucket exists");
            var limited = new OperationAuditLog(rotateRoot, new OperationAuditOptions { MaxQueryBytes = 1024, MaxQueryLines = 2 });
            Check(limited.Query(AuditLogKind.Operation).IsLimited, "scan byte and line limits reported");

            string parallelRoot = Path.Combine(qa, "concurrency"); var concurrent = Create(parallelRoot);
            Check(concurrent.TryWriteOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded), "concurrent store initialized");
            int failures = 0;
            var tasks = Enumerable.Range(0, 3).Select(i => Task.Run(() => { var writer = Create(parallelRoot); for (int j = 0; j < 15; j++) if (!writer.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded)) Interlocked.Increment(ref failures); })).ToArray();
            Task.WaitAll(tasks);
            Check(failures == 0 && concurrent.Query(AuditLogKind.Operation).Entries.Count == 46 && concurrent.Query(AuditLogKind.Operation).InvalidLines == 0, "threads and instances preserve whole lines");
            using (var process = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--worker \"" + parallelRoot + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                for (int i = 0; i < 20; i++) Check(concurrent.TryWriteOperation(OperationAuditAction.ModuleOpen, AuditOutcome.Succeeded), "cross-process local write");
                Check(process.WaitForExit(20000) && process.ExitCode == 0, "cross-process writer succeeded");
            }
            var parallelEntries = concurrent.Query(AuditLogKind.Operation);
            Check(parallelEntries.Entries.Count == 86 && parallelEntries.Entries.Select(e=>e.EventId).Distinct().Count() == 86 && parallelEntries.InvalidLines == 0, "named mutex serializes processes");
            using (var locked = new FileStream(Directory.GetFiles(parallelRoot,"operation_*.jsonl").Single(), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!concurrent.TryWriteOperation(OperationAuditAction.SettingsSaved, AuditOutcome.Succeeded), "write failure returns false, no throw");
            Check(concurrent.Query(AuditLogKind.Operation).LastWriteFailureCode == "storage_unavailable" && concurrent.LastWriteFailureUtc.HasValue, "writer failure visible to viewer");
            Check(concurrent.TryWriteOperation(OperationAuditAction.SettingsSaved, AuditOutcome.Succeeded), "logger recovers after failure");
            string unowned = Path.Combine(qa, "unowned"); Directory.CreateDirectory(unowned); File.WriteAllText(Path.Combine(unowned,"customer.txt"),"keep");
            Check(!Create(unowned).TryWriteOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded), "unowned nonempty directory refused");
            Check(File.ReadAllText(Path.Combine(unowned,"customer.txt")) == "keep", "unowned data untouched");
            string blocked = Path.Combine(qa,"blocked"); File.WriteAllText(blocked,"keep");
            Check(!Create(blocked).TryWriteOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded), "invalid storage never crashes application");
            TestQueued(qa);
            Console.WriteLine("AUDIT_QUEUE_TESTS_PASS=" + checks);
            TestViewer(log,qa);
            Console.WriteLine("OPERATION_AUDIT_PASS=" + checks); Console.WriteLine("EVIDENCE=" + qa); return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void TestQueued(string qa)
    {
        var ordered = Create(Path.Combine(qa,"queued-order"));
        for(int i=0;i<40;i++) Check(ordered.EnqueueOperation(OperationAuditAction.ModuleOpen,AuditOutcome.Requested,Path.Combine(qa,"sequence",i.ToString("D4")),AuditModule.DataBrowser),"queued event accepted");
        Check(ordered.Flush(TimeSpan.FromSeconds(3)),"bounded queue drain");
        var rows = File.ReadAllLines(Directory.GetFiles(ordered.DirectoryPath,"operation_*.jsonl").Single()).Select(s=>new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(s)).ToArray();
        Check(rows.Length==40 && rows.Select((r,i)=>Path.GetFileName((string)r["projectPath"])==i.ToString("D4")).All(v=>v),"single writer preserves enqueue order");
        Check(ordered.Shutdown(TimeSpan.FromSeconds(1)) && ordered.PendingEntries==0,"shutdown drains accepted queue");
        Check(!ordered.EnqueueOperation(OperationAuditAction.ApplicationExit,AuditOutcome.Succeeded) && ordered.LastWriteFailureCode=="queue_closed","shutdown rejects new entries explicitly");
        Check(!ordered.Flush(TimeSpan.FromSeconds(30)),"unbounded drain budget rejected");

        var bounded = new OperationAuditLog(Path.Combine(qa,"queued-bound"),new OperationAuditOptions{MaxQueueEntries=2});
        using(var mutex = new Mutex(false,(string)Field(bounded,"mutexName")))
        {
            mutex.WaitOne();
            try
            {
                Check(bounded.EnqueueOperation(OperationAuditAction.ApplicationStart,AuditOutcome.Succeeded),"first queued entry");
                Check(SpinWait.SpinUntil(()=>(bool)Field(bounded,"activeWrite"),1000),"background writer begins independently");
                Check(bounded.EnqueueProject(ProjectAuditAction.ProjectCreated,AuditOutcome.Succeeded),"first waiting slot");
                Check(bounded.EnqueueOperation(OperationAuditAction.ReturnHome,AuditOutcome.Cancelled),"second waiting slot");
                var timer=Stopwatch.StartNew();
                Check(!bounded.EnqueueOperation(OperationAuditAction.SettingsOpened,AuditOutcome.Requested) && bounded.LastWriteFailureCode=="queue_full","capacity overflow is visible");
                Check(timer.ElapsedMilliseconds<150,"full queue does not wait for file IO");
                timer.Restart();
                Check(!bounded.Flush(TimeSpan.FromMilliseconds(10)) && timer.ElapsedMilliseconds<200 && bounded.LastWriteFailureCode=="flush_timeout","flush has finite short budget");
            }
            finally { mutex.ReleaseMutex(); }
        }
        Check(bounded.Flush(TimeSpan.FromSeconds(2)),"accepted entries can drain after timeout");
        Check(bounded.Query(AuditLogKind.Operation).Entries.Any(e=>e.Action=="ReturnHome"&&e.Outcome==AuditOutcome.Cancelled),"cancellation outcome not relabelled success");
        Check(bounded.Shutdown(TimeSpan.FromSeconds(1)),"bounded queue shutdown");
        string blocked=Path.Combine(qa,"queued-failure");File.WriteAllText(blocked,"not a directory");
        var failure=Create(blocked);
        Check(failure.EnqueueOperation(OperationAuditAction.ApplicationStart,AuditOutcome.Succeeded),"enqueue performs no filesystem IO");
        Check(failure.Flush(TimeSpan.FromSeconds(2)) && failure.LastWriteFailureCode=="storage_unavailable","asynchronous write error remains inspectable");
        Check(failure.Query(AuditLogKind.Operation).LastWriteFailureCode.Length>0,"viewer can inspect asynchronous failures");
        failure.Shutdown(TimeSpan.FromSeconds(1));
    }
    static void TestViewer(OperationAuditLog log, string qa)
    {
        foreach(int language in new[]{0,1,18})
            using (var view = new OperationLogViewer(log,language))
            {
                view.FormClosing += (s,e) => Stage(language + " FormClosing event cancel=" + e.Cancel);
                view.FormClosed += (s,e) => Stage(language + " FormClosed event");
                view.Disposed += (s,e) => Stage(language + " Disposed event");
                Console.WriteLine("AUDIT_VIEWER_START=" + language);
                Stage(language + " show begin"); view.Show(); Stage(language + " show end / first query pump begin");
                PumpUntil(()=>!view.IsLoading && view.LastQueryResult != null); Stage(language + " first query pump end");
                foreach(Size size in new[]{new Size(760,520),new Size(1100,700)})
                {
                    Stage(language + " resize " + size.Width + " begin"); view.ClientSize = size;
                    Stage(language + " resize " + size.Width + " end / events begin"); Application.DoEvents();
                    Stage(language + " events end / bitmap1 " + size.Width + " begin");
                    using(var bitmap = new Bitmap(view.Width,view.Height)) { view.DrawToBitmap(bitmap,new Rectangle(Point.Empty,view.Size)); bitmap.Save(Path.Combine(qa,"viewer-" + language + "-" + size.Width + ".png")); }
                    Stage(language + " bitmap1 " + size.Width + " end / assertions begin");
                    foreach (Button button in Descendants(view).OfType<Button>().Where(b=>b.Visible))
                        Check(view.RectangleToScreen(view.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "viewer actions visible " + language + " " + button.Text + " window=" + view.ClientRectangle + " button=" + view.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)));
                    Check(Descendants(view).OfType<DataGridView>().All(g=>g.ReadOnly && !g.AllowUserToAddRows && !g.AllowUserToDeleteRows && g.ScrollBars==ScrollBars.Vertical), "viewer readonly vertical-only grids");
                    Check(Descendants(view).OfType<DataGridView>().Where(g=>g.Visible).All(g=>g.ClientSize.Height >= 100), "minimum viewer retains usable log rows");
                    Check(Descendants(view).OfType<DateTimePicker>().All(p=>p.Width >= TextRenderer.MeasureText("2026-09-28 23:59",p.Font).Width + 40), "time filter displays full date and time");
                    Stage(language + " assertions end / bitmap2 " + size.Width + " begin");
                    using(var bitmap = new Bitmap(view.Width,view.Height)) { view.DrawToBitmap(bitmap,new Rectangle(Point.Empty,view.Size)); bitmap.Save(Path.Combine(qa,"viewer-" + language + "-" + size.Width + ".png")); }
                    Stage(language + " bitmap2 " + size.Width + " end");
                }
                Stage(language + " select tab begin");
                var tabs = (TabControl)Field(view,"tabs"); tabs.SelectedIndex = 1;
                Stage(language + " select tab end / query pump begin"); PumpUntil(()=>!view.IsLoading);
                Stage(language + " query pump end");
                Check(view.LastQueryResult.Entries.All(e=>e.Kind==AuditLogKind.Operation), "category tab query");
                Check(!Descendants(view).OfType<Button>().Any(b=>new[]{"删除","清空","上传","Delete","Clear","Upload"}.Any(word=>b.Text.Contains(word))), "viewer has no destructive/network action");
                Stage(language + " close begin"); view.Close(); Stage(language + " close end / lifecycle assertions begin");
                Check(!view.IsLoading, "closed viewer has no active UI query " + language);
                int closedSequence = (int)Field(view, "requestSequence");
                var closedResult = view.LastQueryResult;
                Task afterClose = view.RefreshLogsAsync();
                Check(afterClose.IsCompleted && !afterClose.IsFaulted && !afterClose.IsCanceled, "refresh after close returns immediately " + language);
                Check((int)Field(view, "requestSequence") == closedSequence && Object.ReferenceEquals(view.LastQueryResult, closedResult), "refresh after close starts no query or result update " + language);
                Stage(language + " lifecycle assertions end / events begin"); Application.DoEvents();
                Stage(language + " final events end");
                Console.WriteLine("AUDIT_VIEWER_CLOSED=" + language);
            }
    }
}
