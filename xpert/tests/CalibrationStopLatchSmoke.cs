using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationStopLatchSmoke
{
    static int checks;
    static Type type;
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static Task Run(Control page, IEnumerable<string> levels, Func<string, Task<bool>> capture)
    { return (Task)type.GetMethod("RunHardwareAcquisitionLevels", Hidden).Invoke(page, new object[] { levels, capture }); }
    static void Stop(Control page) { type.GetMethod("RequestHardwareStop").Invoke(page, null); }
    static void Pump(Task task)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < limit) Application.DoEvents();
        Check(task.IsCompleted, "controlled acquisition/release finished");
        task.GetAwaiter().GetResult();
    }
    sealed class Levels : IEnumerable<string>
    {
        internal int Enumerations;
        public IEnumerator<string> GetEnumerator() { Enumerations++; return ((IEnumerable<string>)new[] { "100pC", "500pC", "5nC" }).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Pass the isolated build root.");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            type = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "CalibrationTest", "Xiongxin.OWTS.CalibrationTest.dll"))
                .GetType("HVX.Controls.CalibrationProgramControl", true);
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                var called = new List<string>();
                Pump(Run(page, new Levels(), level => { called.Add(level); return Task.FromResult(true); }));
                Check(String.Join(",", called) == "100pC,500pC,5nC", "normal batch order unchanged");
                called.Clear();
                Pump(Run(page, new Levels(), level => { called.Add(level); return Task.FromResult(false); }));
                Check(called.Count == 1, "failed/pending-save capture does not advance");
            }
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                var levels = new Levels(); int calls = 0; Stop(page);
                Pump(Run(page, levels, level => { calls++; return Task.FromResult(true); }));
                Check(calls == 0 && levels.Enumerations == 0, "stop before batch prevents even enumeration");
            }
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                var release = new TaskCompletionSource<bool>();
                var published = new List<string>();
                Task batch = Run(page, new Levels(), level => { published.Add(level); return release.Task; });
                Check(!batch.IsCompleted && published.Count == 1, "first real result exists while release is pending");
                Stop(page); Stop(page);
                Check(!batch.IsCompleted, "stop is not falsely reported as completed hardware release");
                release.SetResult(true); Pump(batch);
                Check(published.Count == 1 && published[0] == "100pC", "late stop retains completed result and never starts a new level");
                Check((bool)type.GetField("hostConnectionUnavailable", Hidden).GetValue(page), "stop stays latched after successful release");
                int attempts = 0;
                Pump(Run(page, new Levels(), level => { attempts++; return Task.FromResult(true); }));
                Check(attempts == 0, "re-entering the same stopped page cannot clear latch");
            }
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                int calls = 0;
                Pump(Run(page, new Levels(), level => { calls++; Stop(page); return Task.FromResult(true); }));
                Check(calls == 1, "synchronous stop at completed-level boundary cannot advance");
            }
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                var release = new TaskCompletionSource<bool>(); int calls = 0;
                Task batch = Run(page, new Levels(), level => { calls++; return release.Task; });
                page.Dispose(); release.SetResult(true); Pump(batch);
                Check(calls == 1, "disposed page during release cannot advance");
                Pump(Run(page, new Levels(), level => { calls++; return Task.FromResult(true); }));
                Check(calls == 1, "disposed page cannot begin another batch");
            }
            using (Control page = (Control)Activator.CreateInstance(type))
            {
                var release = new TaskCompletionSource<bool>(); int calls = 0;
                Task batch = Run(page, new Levels(), level => { calls++; return release.Task; });
                release.SetException(new IOException("offline release failure"));
                bool propagated = false;
                try { Pump(batch); } catch (IOException) { propagated = true; }
                Check(propagated && calls == 1, "release failure remains visible and does not start another level");
            }
            Console.WriteLine("CALIBRATION_STOP_LATCH_PASS=" + checks + "; HARDWARE_COMMANDS=0; REAL_PROJECT_WRITES=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
