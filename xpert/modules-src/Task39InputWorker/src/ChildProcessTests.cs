using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    // Exercises the actual GUI executable as a hidden child with redirected
    // pipes and a process job. Probe commands never load a device driver.
    internal static class ChildProcessTests
    {
        internal static string Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", "child_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            int checks = 0;
            var messages = new List<string>();
            string success = Path.Combine(root, "success"); Directory.CreateDirectory(success);
            BenchRunner.RunCommandAsync("--worker-self-test", "success", success, 5000,
                (stage, message) => { lock (messages) messages.Add(stage + "|" + message); }, CancellationToken.None).GetAwaiter().GetResult();
            Require(File.ReadAllText(Path.Combine(success, "worker-ok.txt"), Encoding.UTF8) == "握手、编码和互斥已通过", "UTF-8 result");
            Require(messages.Exists(x => x == "PROBE|START 已收到；UTF-8 中文管道正常；未加载驱动。"), "UTF-8 stdout must survive real hidden WinExe child"); checks++;

            foreach (string scenario in new[] { "failure", "stdio-failure", "mutex-failure", "bootstrap-failure" })
            {
                string directory = Path.Combine(root, scenario); Directory.CreateDirectory(directory);
                bool rejected = false;
                try { BenchRunner.RunCommandAsync("--worker-self-test", scenario, directory, 5000, delegate { }, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidOperationException ex)
                {
                    rejected = true;
                    Require((ex.Message.Contains("退出码 1") || scenario == "bootstrap-failure") && ex.Message.Contains("自检注入"), "controlled error and readable parent diagnostic for " + scenario);
                }
                Require(rejected && File.ReadAllText(Path.Combine(directory, "error.txt"), Encoding.UTF8).Contains("自检注入"), "bootstrap/worker error persisted for " + scenario); checks++;
            }

            // A real pre-owned named mutex must fail cleanly rather than crash.
            using (var held = new Mutex(false, @"Local\XiongxinZeroBenchInputWorker"))
            {
                bool owned = false;
                try
                {
                    try { owned = held.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                    Require(owned, "another live worker prevents isolated mutex test");
                    string directory = Path.Combine(root, "mutex-contended"); Directory.CreateDirectory(directory);
                    bool rejected = false;
                    try { BenchRunner.RunCommandAsync("--worker-self-test", "success", directory, 5000, delegate { }, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("已有本工具工作进程"); }
                    Require(rejected && File.Exists(Path.Combine(directory, "error.txt")), "real mutex contention diagnostic"); checks++;
                }
                finally { if (owned) held.ReleaseMutex(); }
            }

            using (var cancellation = new CancellationTokenSource())
            {
                string directory = Path.Combine(root, "cancel"); Directory.CreateDirectory(directory);
                bool killed = false, cancelled = false;
                try
                {
                    BenchRunner.RunCommandAsync("--worker-self-test", "wait", directory, 10000,
                        (stage, message) => { if (stage == "PROBE") cancellation.Cancel(); if (message.Contains("回收本工具只读进程 PID")) killed = true; }, cancellation.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && !killed && File.ReadAllText(Path.Combine(directory, "cancelled.txt"), Encoding.UTF8) == "正常取消清理", "CANCEL handshake returns without forced kill"); checks++;
            }
            return "CHILD_PROCESS_TESTS=PASS: " + checks + " real hidden WinExe child checks; START/job attachment, UTF-8 pipes, worker/STDIO/Mutex diagnostics, real mutex contention, graceful CANCEL; no driver loaded. Evidence: " + root + Environment.NewLine + RunPipeInheritance();
        }

        internal static string RunPipeInheritance()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", "pipe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var results = new List<string>();
            var failures = new List<string>();
            foreach (string scenario in new[] { "pipe-hold-success", "pipe-hold-failure" })
            {
                string directory = Path.Combine(root, scenario); Directory.CreateDirectory(directory);
                var timer = Stopwatch.StartNew();
                Exception rejection = null;
                try { BenchRunner.RunCommandAsync("--worker-self-test", scenario, directory, 5000, delegate { }, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { rejection = ex; }
                timer.Stop();
                bool failedWorker = scenario == "pipe-hold-failure";
                bool semantics = failedWorker
                    ? rejection is InvalidOperationException && rejection.Message.Contains("退出码 1") && rejection.Message.Contains("自检注入持管道后代期间工作失败")
                    : rejection == null && File.Exists(Path.Combine(directory, "worker-ok.txt"));
                if (failedWorker) Require(File.ReadAllText(Path.Combine(directory, "error.txt"), Encoding.UTF8).Contains("自检注入持管道后代期间工作失败"), "held-pipe worker error persisted");
                bool helperGone = WaitForHelperExit(Path.Combine(directory, "pipe-helper.txt"));
                string line = scenario + ": elapsed_ms=" + timer.ElapsedMilliseconds + "; exit_semantics=" + semantics + "; helper_gone=" + helperGone + "; exception=" + (rejection == null ? "none" : rejection.Message.Replace('\r', ' ').Replace('\n', ' '));
                results.Add(line);
                if (!semantics || !helperGone || timer.ElapsedMilliseconds >= 7000) failures.Add(line);
            }
            string report = "PIPE_INHERITANCE_TESTS=" + (failures.Count == 0 ? "PASS" : "FAIL") + Environment.NewLine + String.Join(Environment.NewLine, results) + Environment.NewLine + "No driver loaded; helper duration is 20 seconds; evidence: " + root;
            File.WriteAllText(Path.Combine(root, "summary.txt"), report, Encoding.UTF8);
            if (failures.Count != 0) throw new Exception(report);
            return report;
        }

        private static bool WaitForHelperExit(string identityPath)
        {
            string[] identity = File.ReadAllLines(identityPath);
            int pid = Int32.Parse(identity[0]); long ticks = Int64.Parse(identity[1]);
            Require(identity[2] == "IN_JOB=True", "pipe helper in worker Job");
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3000)
            {
                try
                {
                    using (var child = Process.GetProcessById(pid))
                        if (child.HasExited || child.StartTime.ToUniversalTime().Ticks != ticks) return true;
                }
                catch (ArgumentException) { return true; }
                Thread.Sleep(25);
            }
            return false;
        }

        private static void Require(bool value, string message) { if (!value) throw new Exception("Child process test failed: " + message); }
    }
}
