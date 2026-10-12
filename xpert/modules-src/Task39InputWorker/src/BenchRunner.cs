using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.ZeroBench
{
    internal static class BenchRunner
    {
        private static readonly object workerGate = new object();
        private static bool workerRunning;
        private static int unreapedPid;
        private static DateTime unreapedStartUtc;

        internal static async Task<BenchCapture> RunAsync(BenchSettings settings, Action<string, string> report, CancellationToken cancel)
        {
            settings.Validate();
            string records = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records");
            string directory = Path.Combine(records, (settings.RealHardware ? "HW_" : "SIM_") + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            settings.Save(Path.Combine(directory, "settings.xml"));
            using (var log = new StreamWriter(Path.Combine(directory, "sequence.log"), false, System.Text.Encoding.UTF8))
            {
                object logSync = new object();
                Action<string, string> progress = (stage, message) =>
                {
                    lock (logSync) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + stage + " | " + message); log.Flush(); }
                    report(stage, message);
                };
                try
                {
                    BenchCapture capture;
                    if (!settings.RealHardware)
                        capture = await Task.Run(() => Simulate(settings, directory, progress, cancel), cancel);
                    else
                    {
                        await RunWorker(settings, directory, progress, cancel);
                        cancel.ThrowIfCancellationRequested();
                        capture = await Task.Run(() => BenchCapture.Load(directory), cancel);
                    }
                    progress("COMPLETE", settings.CaptureDurationMs + " ms 原始波形已保存：" + directory);
                    progress("STEP_COMPLETE_PASSED", "完整记录已保存且校验通过：" + directory + (capture.Simulation ? "；仅仿真，无硬件测试。" : "；本轮只验证输入与触发，不验证 HV / AO。"));
                    return capture;
                }
                catch (Exception ex)
                {
                    try { progress("STEP_COMPLETE_" + (ex is OperationCanceledException ? "CANCELED" : "FAILED"), ex.ToString()); progress(ex is OperationCanceledException ? "CANCELED" : "ERROR", ex is OperationCanceledException ? "操作者停止，本轮结果不作为完整测量。" : ex.Message); }
                    catch { /* Preserve acquisition/cancellation failure if logging itself failed. */ }
                    throw;
                }
            }
        }

        private static async Task RunWorker(BenchSettings settings, string directory, Action<string, string> report, CancellationToken cancel)
        {
            await RunCommandAsync("--hardware", Path.Combine(directory, "settings.xml"), directory,
                45000 + settings.TriggerTimeoutMs, report, cancel);
        }

        // Discovery, acquisition and driver-free child probes share one owned
        // worker lease, redirected UTF-8 pipes, START handshake and process job.
        internal static async Task RunCommandAsync(string command, string settingsPath, string directory,
            int timeoutMilliseconds, Action<string, string> report, CancellationToken cancel)
        {
            if (command != "--hardware" && command != "--discover" && command != "--worker-self-test")
                throw new ArgumentException("未知子进程命令。", "command");
            if (String.IsNullOrWhiteSpace(settingsPath) || String.IsNullOrWhiteSpace(directory) ||
                settingsPath.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 || directory.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("子进程参数包含无效字符。");
            if (timeoutMilliseconds < 100 || timeoutMilliseconds > 180000) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            if (report == null) report = delegate { };
            AcquireWorkerLease();
            try { await RunLeasedWorker(command, settingsPath, directory, timeoutMilliseconds, report, cancel); }
            finally { lock (workerGate) workerRunning = false; }
        }

        private static void AcquireWorkerLease()
        {
            lock (workerGate)
            {
                if (workerRunning) throw new InvalidOperationException("已有本工具采集进程，禁止并行打开设备。");
                if (unreapedPid != 0)
                {
                    try
                    {
                        using (var old = Process.GetProcessById(unreapedPid))
                            if (!old.HasExited && old.StartTime.ToUniversalTime() == unreapedStartUtc)
                                throw new InvalidOperationException("上一采集进程 PID " + unreapedPid + " 尚未退出，禁止再次启动。");
                    }
                    catch (ArgumentException) { /* PID no longer exists. */ }
                    unreapedPid = 0;
                }
                workerRunning = true;
            }
        }

        private static async Task RunLeasedWorker(string command, string settingsPath, string directory,
            int timeoutMilliseconds, Action<string, string> report, CancellationToken cancel)
        {
            var info = new ProcessStartInfo
            {
                FileName = System.Reflection.Assembly.GetExecutingAssembly().Location,
                Arguments = command + " \"" + settingsPath + "\" \"" + directory.TrimEnd('\\', '/') + "\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using (var job = new ProcessJob())
            using (var process = new Process { StartInfo = info })
            {
                Exception callbackFailure = null;
                object callbackSync = new object();
                bool acceptCallbacks = true, started = false, outputStarted = false, errorStarted = false;
                int outputEnded = 0, errorEnded = 0;
                var errorText = new System.Text.StringBuilder();
                Action<string, string> safeReport = (stage, message) =>
                {
                    lock (callbackSync)
                    {
                        if (!acceptCallbacks) return;
                        try { report(stage, message); }
                        catch (Exception ex) { Interlocked.CompareExchange(ref callbackFailure, ex, null); }
                    }
                };
                DataReceivedEventHandler output = (s, e) =>
                {
                    if (e.Data == null) { Interlocked.Exchange(ref outputEnded, 1); return; }
                    int pipe = e.Data.IndexOf('|');
                    safeReport(pipe > 0 ? e.Data.Substring(0, pipe) : "INFO", pipe > 0 ? e.Data.Substring(pipe + 1) : e.Data);
                };
                DataReceivedEventHandler errorOutput = (s, e) =>
                {
                    if (e.Data == null) Interlocked.Exchange(ref errorEnded, 1);
                    else
                    {
                        lock (callbackSync) if (errorText.Length < 32768) errorText.AppendLine(e.Data);
                        safeReport("INFO", e.Data);
                    }
                };
                process.OutputDataReceived += output;
                process.ErrorDataReceived += errorOutput;
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new InvalidOperationException("无法启动本工具采集进程。");
                    started = true;
                    lock (workerGate) { unreapedPid = process.Id; unreapedStartUtc = process.StartTime.ToUniversalTime(); }
                    // Worker waits for START and may not load a hardware driver before attachment.
                    job.Attach(process);
                    process.BeginOutputReadLine(); outputStarted = true;
                    process.BeginErrorReadLine(); errorStarted = true;
                    cancel.ThrowIfCancellationRequested();
                    process.StandardInput.WriteLine("START"); process.StandardInput.Flush();
                    Stopwatch clock = Stopwatch.StartNew();
                    while (!process.HasExited)
                    {
                        cancel.ThrowIfCancellationRequested();
                        Exception callbackError = Interlocked.CompareExchange(ref callbackFailure, null, null);
                        if (callbackError != null) throw new InvalidOperationException("采集日志或界面进度回调失败。", callbackError);
                        if (clock.ElapsedMilliseconds > timeoutMilliseconds)
                            throw new TimeoutException("采集或驱动释放超时，将回收本工具工作进程；如本轮启用 TRG，P0.7 物理低电平未确认，不能据此认定输出安全。");
                        await Task.Delay(100, cancel);
                    }
                    cancel.ThrowIfCancellationRequested();
                    int workerExitCode = process.ExitCode;
                    // A vendor helper can inherit the redirected standard handles.
                    // Worker exit is the lifetime boundary, not pipe EOF. Release
                    // only our job's descendants now that the worker is gone.
                    // Dispose is idempotent; the outer using still covers errors.
                    job.Dispose();
                    Stopwatch drain = Stopwatch.StartNew();
                    while ((Volatile.Read(ref outputEnded) == 0 || Volatile.Read(ref errorEnded) == 0) && drain.ElapsedMilliseconds < 1000)
                        await Task.Delay(20, cancel);
                    cancel.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref outputEnded) == 0 || Volatile.Read(ref errorEnded) == 0)
                        safeReport("INFO", "工作进程已退出（退出码 " + workerExitCode +
                            "），日志管道未收到全部结束标记；停止等待日志，继续按退出码和本轮结果文件校验，不能据此判定采集通过。");
                    Exception finalCallbackError = Interlocked.CompareExchange(ref callbackFailure, null, null);
                    if (finalCallbackError != null) throw new InvalidOperationException("采集日志或界面进度回调失败。", finalCallbackError);
                    if (workerExitCode != 0)
                    {
                        string error = Path.Combine(directory, "error.txt");
                        string bufferedError;
                        lock (callbackSync) bufferedError = errorText.ToString();
                        string details = File.Exists(error) ? File.ReadAllText(error, System.Text.Encoding.UTF8) : bufferedError;
                        throw new InvalidOperationException("工作进程失败，退出码 " + workerExitCode +
                            (String.IsNullOrWhiteSpace(details) ? "；未获得子进程错误详情。" : "。" + Environment.NewLine + details));
                    }
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException || ex is TimeoutException) throw;
                    // Bootstrap may exit before START is written, producing a
                    // broken-pipe exception in the parent. Prefer its persisted
                    // cause to the secondary pipe error when available.
                    string details = null;
                    try
                    {
                        string errorPath = Path.Combine(directory, "error.txt");
                        if (File.Exists(errorPath)) details = File.ReadAllText(errorPath, System.Text.Encoding.UTF8);
                    }
                    catch { /* Preserve the original failure if diagnostics cannot be read. */ }
                    if (!String.IsNullOrWhiteSpace(details) && !ex.Message.Contains(details))
                        throw new InvalidOperationException("工作进程启动或运行失败。" + Environment.NewLine + details, ex);
                    throw;
                }
                finally
                {
                    try
                    {
                        if (started && !process.HasExited)
                        {
                            try { process.StandardInput.WriteLine("CANCEL"); process.StandardInput.Flush(); } catch { }
                            Stopwatch release = Stopwatch.StartNew();
                            while (!process.HasExited && release.ElapsedMilliseconds < 2000) await Task.Delay(50);
                            if (!process.HasExited)
                            {
                                safeReport("RELEASE", "驱动尚未返回，回收本工具工作进程 PID " + process.Id + "；下一次仍锁定原序列号。如本轮启用 TRG，不能确认 P0.7 已恢复物理低电平。");
                                try { process.Kill(); }
                                catch (InvalidOperationException) { if (!process.HasExited) throw; }
                                if (!process.WaitForExit(3000)) throw new InvalidOperationException("本工具采集进程尚未退出，禁止再次启动。");
                            }
                        }
                    }
                    finally
                    {
                        // Synchronize with a callback already in progress before the shared log closes.
                        lock (callbackSync) acceptCallbacks = false;
                        process.OutputDataReceived -= output; process.ErrorDataReceived -= errorOutput;
                        if (outputStarted) try { process.CancelOutputRead(); } catch (InvalidOperationException) { }
                        if (errorStarted) try { process.CancelErrorRead(); } catch (InvalidOperationException) { }
                        if (started && process.HasExited) lock (workerGate) unreapedPid = 0;
                        // If reaping failed, retain PID+start time; the next launch is refused until exit.
                    }
                }
            }
        }

        internal static BenchCapture Simulate(BenchSettings s, string directory, Action<string, string> report, CancellationToken cancel)
        {
            Action<string, string, int> step = (stage, message, delay) =>
            {
                cancel.ThrowIfCancellationRequested(); report(stage, "仿真 · " + message); report("STEP_" + stage + "_RUNNING", "仿真 · " + message);
                if (delay > 0 && cancel.WaitHandle.WaitOne(delay)) cancel.ThrowIfCancellationRequested();
                report("STEP_" + stage + "_PASSED", "仿真流程通过，未测试硬件 · " + message);
            };
            step("INIT", "模拟两卡初始化；本次未访问硬件", 40);
            step("AO_ZERO", "模拟 AO=0 V；目标=0 V", 20);
            step("HV_ON", "HV ON 状态演示", 20);
            step("HV_DELAY", "HV ON→HV UP 延时 " + s.HvDelayMs + " ms", s.HvDelayMs);
            step("HV_UP", "结束条件成立：设定施加电压=0 V；立即结束升压；AO 保持0；步长未使用", s.LoopDelayMs);
            step("HV_OFF", "HV OFF 状态演示；AO=0 V", 20);
            step("ARM", "模拟 ART/Pico 均已布置", 20);
            step("ARM_DELAY", "两卡就绪后延时 " + s.ArmDelayMs + " ms", s.ArmDelayMs);
            step("TRG_OUTPUT", "模拟 P0.7 TRG；未创建真实 DO 任务", 20);
            step("WAIT_TRIGGER", "模拟共享数字边沿", 50);
            step("READ", "生成 " + s.CaptureDurationMs + " ms 电压和局放；定位保留前 min(20ms,采集长度)", 20);
            var c = new BenchCapture
            {
                Settings = s, VoltageRaw = new double[s.ExpectedArtCount], PdAdc = new short[s.RequestedPicoCount], PicoRateHz = s.RequestedPicoRateHz,
                PicoFullScaleV = BenchSettings.Ranges[s.RangeIndex], Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO",
                HardwareBits = PicoCaptureConfiguration.HardwareBits(s.ResolutionBits, s.RangeIndex),
                CapturedUtc = DateTime.UtcNow, DirectoryPath = directory, Runtime = "仿真数据，不是实测"
            };
            var rng = new Random(1729 + s.RunNumber);
            double nominal = s.CalibrationPulsePc / s.PdCalibrationPcPerV;
            for (int i = 0; i < c.VoltageRaw.Length; i++)
            {
                double t = i / c.ArtRateHz;
                c.VoltageRaw[i] = (rng.NextDouble() - .5) * .00012 + .0005 * Math.Exp(-t / .00008);
                if (s.SimulationOscillation) c.VoltageRaw[i] += 2000 / s.DividerVPerV * Math.Exp(-60 * t) * Math.Cos(2 * Math.PI * 600 * t);
            }
            double scale = c.MaximumAdc / c.PicoFullScaleV;
            for (int i = 0; i < c.PdAdc.Length; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                double v = (rng.NextDouble() - .5) * nominal * .015;
                double sampleAt125M = i * 125000000.0 / c.PicoRateHz;
                if (sampleAt125M < 12000) v += nominal * 2.3 * Math.Exp(-sampleAt125M / 1500.0) * Math.Sin(sampleAt125M * .021);
                double relative = (i % (int)Math.Round(.01 * c.PicoRateHz) - .005 * c.PicoRateHz) * 125000000.0 / c.PicoRateHz;
                if (Math.Abs(relative) < 200) v += nominal * Math.Exp(-relative * relative / 180.0);
                double counts = v * scale;
                if (Math.Abs(counts) > c.MaximumAdc) c.Overflow = true;
                c.PdAdc[i] = (short)Math.Round(Math.Max(-c.MaximumAdc, Math.Min(c.MaximumAdc, counts)));
            }
            step("RELEASE", "模拟采集结束，保存原始数据", 10);
            c.Save(directory);
            return c;
        }
    }
}
