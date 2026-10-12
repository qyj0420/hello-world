using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.ZeroBench
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--job-probe") { Thread.Sleep(20000); return 0; }
            if (args.Length == 1 && args[0] == "--pipe-hold-probe") { Thread.Sleep(20000); return 0; }
            if (args.Length == 3 && (args[0] == "--hardware" || args[0] == "--discover" || args[0] == "--worker-self-test"))
                return WorkerMain(args[0], args[1], args[2]);
            if (args.Length == 2 && args[0] == "--child-process-tests")
            {
                try { File.WriteAllText(args[1], ChildProcessTests.Run(), Encoding.UTF8); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8); return 1; }
            }
            if (args.Length == 2 && args[0] == "--pipe-inheritance-tests")
            {
                try { File.WriteAllText(args[1], ChildProcessTests.RunPipeInheritance(), Encoding.UTF8); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8); return 1; }
            }
            if (args.Length == 2 && args[0] == "--scan-devices") return ScanDevices(args[1]);
            if (args.Length == 3 && args[0] == "--capture-check") return BenchVerification.CaptureOnce(args[1], args[2]);
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try { string report = AnalysisTests.Run() + Environment.NewLine + CoreTests.Run() + Environment.NewLine + WaitTests.Run() + Environment.NewLine + DiscoveryTests.Run() + Environment.NewLine + TriggerOutputTests.Run() + Environment.NewLine + StageDiagnosticsTests.Run()
                    + Environment.NewLine + CaptureConfigurationTests.Run() + Environment.NewLine + WaveScaleTests.Run() + Environment.NewLine + DetectedWaveformTests.Run() + Environment.NewLine + LocationRangeTests.Run() + Environment.NewLine + LocationRangeWindowTests.Run() + Environment.NewLine + RangeDecisionTests.Run() + Environment.NewLine + WorkflowTests.Run(); File.WriteAllText(args[1], report, Encoding.UTF8); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8); return 1; }
            }
            if (args.Length == 2 && args[0] == "--export-self-test")
            {
                try { File.WriteAllText(args[1], ExportTests.Run(), Encoding.UTF8); return 0; }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm())
            {
                if (args.Length == 2 && args[0] == "--ui-smoke")
                {
                    int code = 0;
                    form.Shown += async (s, e) => { try { await form.SmokeAsync(args[1]); } catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); code = 1; } finally { form.Close(); } };
                    Application.Run(form); return code;
                }
                if (args.Length == 3 && args[0] == "--ui-record-check")
                {
                    int code = 0;
                    form.Shown += async (s, e) => { try { await form.RecordCheckAsync(args[1], args[2]); } catch (Exception ex) { File.WriteAllText(args[2] + ".error.txt", ex.ToString()); code = 1; } finally { form.Close(); } };
                    Application.Run(form); return code;
                }
                if (args.Length == 2 && args[0] == "--ui-range-review-test")
                {
                    int code = 0;
                    form.Shown += async (s, e) => { try { await form.RangeReviewSmokeAsync(args[1]); } catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); code = 1; } finally { form.Close(); } };
                    Application.Run(form); return code;
                }
                if (args.Length == 2 && args[0] == "--ui-discovery-check")
                {
                    int code = 0;
                    form.Shown += async (s, e) => { try { await form.ScanSmokeAsync(args[1]); } catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); code = 1; } finally { form.Close(); } };
                    Application.Run(form); return code;
                }
                Application.Run(form);
            }
            return 0;
        }

        private static int ScanDevices(string reportPath)
        {
            var text = new StringBuilder(); object gate = new object();
            text.AppendLine("SCAN_STARTED_UTC=" + DateTime.UtcNow.ToString("O"));
            text.AppendLine("MODE=只读枚举；父进程不加载驱动，不打开设备，不创建采集任务。");
            int code;
            try
            {
                var found = DiscoveryRunner.RunAsync(new BenchSettings(), (stage, message) =>
                {
                    lock (gate) text.AppendLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + stage + " | " + message);
                }, CancellationToken.None).GetAwaiter().GetResult();
                text.AppendLine("ART_RUNTIME=" + found.ArtRuntime);
                text.AppendLine("PICO_RUNTIME=" + found.PicoRuntime);
                text.AppendLine("ART_COUNT=" + found.ArtDevices.Count);
                foreach (var device in found.ArtDevices)
                    text.AppendLine("ART_DEVICE=" + device.Name + " | MODEL=" + device.Model + " | SERIAL=" + device.Serial);
                text.AppendLine("PICO_COUNT=" + found.PicoSerials.Count);
                foreach (string serial in found.PicoSerials) text.AppendLine("PICO_SERIAL=" + serial);
                text.AppendLine("ART_ERROR=" + found.ArtError);
                text.AppendLine("PICO_ERROR=" + found.PicoError);
                code = String.IsNullOrEmpty(found.ArtError) && String.IsNullOrEmpty(found.PicoError) ? 0 : 2;
                text.AppendLine("SCAN_STATUS=" + (code == 0 ? "完成枚举；设备数量不代表初始化成功" : "完成但含供应商错误；不能当作无设备"));
                found.Save(Path.GetFullPath(reportPath) + ".xml");
            }
            catch (Exception ex) { text.AppendLine("SCAN_STATUS=工作进程或报告处理失败"); text.AppendLine(ex.ToString()); code = 1; }
            text.AppendLine("SCAN_FINISHED_UTC=" + DateTime.UtcNow.ToString("O"));
            try
            {
                string path = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            }
            catch { return 1; }
            return code;
        }

        private static int WorkerMain(string command, string settingsPath, string directory)
        {
            Mutex mutex = null;
            CancellationTokenSource cancellation = null;
            StreamWriter output = null, error = null;
            bool owns = false;
            int exitCode = 1;
            Exception inputFailure = null;
            try
            {
                // A GUI executable with CREATE_NO_WINDOW has redirected handles
                // but no console code page. Console.OutputEncoding calls the
                // console API and can fail with ERROR_INVALID_HANDLE. Encode the
                // redirected streams directly without touching console settings.
                var utf8 = new UTF8Encoding(false, true);
                output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
                error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
                var input = new StreamReader(Console.OpenStandardInput(), utf8, false);
                cancellation = new CancellationTokenSource();
                if (command == "--worker-self-test" && settingsPath == "bootstrap-failure")
                    throw new IOException("自检注入 START 之前的管道初始化错误。");
                // No native driver or device enumeration is allowed before START.
                if (input.ReadLine() != "START") throw new OperationCanceledException("未收到受控工作进程启动握手。");
                if (command == "--worker-self-test" && settingsPath == "stdio-failure")
                    throw new IOException("自检注入 STDIO 错误；应写入 error.txt，而非未捕获退出。");
                if (command == "--worker-self-test" && settingsPath == "mutex-failure")
                    throw new UnauthorizedAccessException("自检注入 Mutex 创建错误；应写入 error.txt。");
                mutex = new Mutex(false, @"Local\XiongxinZeroBenchInputWorker");
                try { owns = mutex.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) throw new InvalidOperationException("已有本工具工作进程，请先停止并释放。");
                var lifetime = cancellation;
                Task.Run(() =>
                {
                    try
                    {
                        string line = input.ReadLine();
                        if (line != null && line != "CANCEL")
                            Interlocked.CompareExchange(ref inputFailure, new IOException("未知工作进程控制命令。"), null);
                    }
                    catch (Exception ex) { Interlocked.CompareExchange(ref inputFailure, ex, null); }
                    finally { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
                });
                Action<string, string> report = (stage, message) => output.WriteLine(stage + "|" + (message ?? String.Empty).Replace('\r', ' ').Replace('\n', ' '));
                if (command == "--worker-self-test")
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "worker.pid"), System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                    report("PROBE", "START 已收到；UTF-8 中文管道正常；未加载驱动。");
                    if (settingsPath == "wait")
                    {
                        try { cancellation.Token.WaitHandle.WaitOne(); cancellation.Token.ThrowIfCancellationRequested(); }
                        finally { File.WriteAllText(Path.Combine(directory, "cancelled.txt"), "正常取消清理", utf8); }
                    }
                    else if (settingsPath == "failure") throw new InvalidOperationException("自检注入工作错误：中文信息完整保留。");
                    else if (settingsPath == "pipe-hold-success" || settingsPath == "pipe-hold-failure")
                    {
                        int helperPid = PipeHoldProbe.Start(directory);
                        report("PROBE", "无驱动后代持有继承日志句柄，PID=" + helperPid);
                        if (settingsPath == "pipe-hold-failure") throw new InvalidOperationException("自检注入持管道后代期间工作失败；退出码不得被日志结束状态覆盖。");
                    }
                    else if (settingsPath != "success") throw new ArgumentException("未知无驱动子进程自检请求。");
                    File.WriteAllText(Path.Combine(directory, "worker-ok.txt"), "握手、编码和互斥已通过", utf8);
                }
                else
                {
                    BenchSettings settings = BenchSettings.Load(settingsPath);
                    if (command == "--discover")
                    {
                        // Discovery runs before identities exist; it must not use
                        // the acquisition validator which requires locked serials.
                        var discovered = DeviceDiscovery.Run(settings, report, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        discovered.Save(Path.Combine(directory, "devices.xml"));
                    }
                    else
                    {
                        settings.Validate();
                        if (!settings.RealHardware) throw new ArgumentException("硬件子进程仅接受明确的实机输入采集请求。");
                        BenchCapture capture = HardwareCapture.Run(settings, directory, report, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        capture.Save(directory);
                    }
                }
                Exception controlError = Interlocked.CompareExchange(ref inputFailure, null, null);
                if (controlError != null) throw new IOException("工作进程控制管道读取失败。", controlError);
                cancellation.Token.ThrowIfCancellationRequested();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                // Bootstrap failures (pipes, encoding, mutex, handshake) also land
                // here. Failure to persist a diagnostic must not escape Main.
                Exception pipeError = Interlocked.CompareExchange(ref inputFailure, null, null);
                RecordWorkerFailure(directory, error, pipeError != null && ex is OperationCanceledException
                    ? new IOException("工作进程控制管道失败，已停止本轮。", pipeError) : ex);
                exitCode = 1;
            }
            finally
            {
                if (owns) try { mutex.ReleaseMutex(); } catch (Exception ex) { RecordWorkerFailure(directory, error, ex); exitCode = 1; }
                if (mutex != null) try { mutex.Dispose(); } catch { }
                if (cancellation != null) cancellation.Dispose();
                try { if (output != null) output.Dispose(); } catch { }
                try { if (error != null) error.Dispose(); } catch { }
            }
            return exitCode;
        }

        private static void RecordWorkerFailure(string directory, TextWriter error, Exception failure)
        {
            try { Directory.CreateDirectory(directory); File.AppendAllText(Path.Combine(directory, "error.txt"), failure + Environment.NewLine, new UTF8Encoding(false)); } catch { }
            try { if (error != null) error.WriteLine(failure.ToString()); } catch { }
        }
    }
}
