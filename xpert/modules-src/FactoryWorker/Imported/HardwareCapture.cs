using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HVX.OWTS.Task38;

namespace Xiongxin.ZeroBench
{
    // Runs only inside the owned, disposable acquisition worker. This adapter has
    // no direct output/reset/HV API. Optional single-line TRG is isolated in
    // ArtSharedTrigger; all other ports remain untouched. P0.4 is read only.
    internal static class HardwareCapture
    {
        internal static BenchCapture Run(BenchSettings s, string directory,
            Action<string, string> report, CancellationToken cancellation)
        {
            if (s == null) throw new ArgumentNullException("s");
            s.ValidateForNewRun();
            if (!s.RealHardware) throw new ArgumentException("硬件采集路径要求实机模式。");
            if (String.IsNullOrWhiteSpace(s.ArtSerial) || String.IsNullOrWhiteSpace(s.PicoSerial))
                throw new ArgumentException("必须指定本次 ART 和 Pico 的准确序列号。");
            if (report == null) report = delegate { };
            var art = new InputOnlyArt();
            var pico = new PicoInputCapture();
            var triggerOutput = s.AutomaticSharedTrigger ? new ArtSharedTrigger() : null;
            BenchCapture result = null;
            Exception failure = null;
            var cleanupErrors = new List<Exception>();
            string activeStage = "INIT";
            Action<string, string> begin = (stage, detail) => { activeStage = stage; report(stage, detail); report("STEP_" + stage + "_RUNNING", detail); };
            Action<string> passed = detail => report("STEP_" + activeStage + "_PASSED", detail);
            Action<string, string> skipped = (stage, detail) => { report(stage, detail); report("STEP_" + stage + "_SKIPPED", detail); };
            try
            {
                cancellation.ThrowIfCancellationRequested();
                begin("INIT", "ART：校验指定身份，初始化 AI4 输入与数字触发；尚未访问 Pico。");
                art.Prepare(s);
                report("STEP_INIT_RUNNING", "ART 初始化成功：" + art.Serial + "，AI4 80 kS/s；开始初始化指定 Pico " + s.PicoSerial + "。");
                report("SAFETY", "P0.4 原始输入：" + (art.SafetyRawHigh ? "高电平" : "低电平") +
                    (art.SafetyRawHigh ? "（用户定义：急停解除）" : "（用户定义：不允许加压）") +
                    "；本零压工具仅记录，不授权 HV 输出。");
                pico.Initialize(s);
                string actualPicoSerial = pico.Serial;
                if (!String.Equals(actualPicoSerial, s.PicoSerial.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Pico 身份复核不匹配，未开始采集。");
                passed("两卡身份及参数初始化成功。ART " + art.Serial + "；Pico " + pico.Serial +
                    "；实际 " + (pico.ActualRateHz / 1e6).ToString("0.######", CultureInfo.InvariantCulture) + " MS/s / " + s.ResolutionBits + " bit；时基 " + pico.Timebase +
                    "；A " + (s.PicoCoupling == 1 ? "DC" : "AC") + " / " + (s.PicoBandwidth20MHz ? "20 MHz 限带" : "全带宽") + " / 偏置 0 V" +
                    "；EXT 请求 " + s.ExternalTriggerVolts.ToString("F3", CultureInfo.InvariantCulture) + " V，写入阈值 " + pico.ExternalThresholdAdc +
                    "（量化 " + pico.ExternalThresholdQuantizedVolts.ToString("F6", CultureInfo.InvariantCulture) + " V）；驱动返回成功，非物理阈值回读；自动触发关闭。");
                if (triggerOutput != null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    begin("TRG_OUTPUT", "仅创建 " + s.ArtDevice + "/port0/line7 并请求初始低电平；不修改其他 DO/AO。");
                    triggerOutput.Prepare(s);
                    report("STEP_TRG_OUTPUT_UNVERIFIED", "P0.7 初始低写入成功；物理电平尚无独立回读，尚未发出脉冲。");
                }
                else skipped("TRG_OUTPUT", "外部触发只读模式，不创建输出任务。");
                cancellation.ThrowIfCancellationRequested();
                skipped("AO_ZERO", "未执行；目标参数 0 V，未创建 AO 任务；不证明 AO 物理电压为零。");
                skipped("HV_ON", "未执行；未发送 HV ON。");
                begin("HV_DELAY", "流程延时 " + s.HvDelayMs + " ms；不代表物理 HV 已使能。");
                Delay(s.HvDelayMs, cancellation);
                passed("流程延时结束；Windows 不保证毫秒实时精度。");
                skipped("HV_UP", "目标 = 0 V，立即结束；未执行升压，AI0 / AI2 悬空不参与反馈判断。");
                skipped("HV_OFF", "未执行；未发送任何 HV 输出命令，不能据此确认高压已关闭。");
                // This adapter owns no HV/AO. Wait before Arm, not inside its
                // post-arm quiet interval or an energized pressure handoff.
                if(FactoryCaptureCooldown.ReleasePending)FactoryCaptureCooldown.EnsureReady();
                if(FactoryCaptureCooldown.RemainingMilliseconds>0)
                {
                    report("CAPTURE_COOLDOWN","等待Pico共享采后/新会话冷却；可取消，不代表外部设备已放电。");
                    FactoryCaptureCooldown.Wait(cancellation);
                }
                begin("ARM", "开始布置 Pico A，随后 ART AI4：共享数字边沿，禁止自动触发替代。");
                // Neither native call emits a trigger. The same physical edge must
                // arrive at both PFI and EXT only after both devices are armed.
                pico.Arm();
                report("STEP_ARM_RUNNING", "Pico RunBlock 已返回成功；正在启动 ART AI4 有限触发任务。");
                art.Arm();
                if (!pico.Armed || !art.Armed)
                    throw new InvalidOperationException("两卡未全部进入等待采集状态。");
                passed("ART 和 Pico 布置调用均成功；尚未证明触发线到达。");
                begin("ARM_DELAY", "两卡已布置，静默检查 " + s.ArmDelayMs + " ms；此时若已有数据则拒绝本轮。");
                var waiting = new InputCaptureWait(art.AvailableSamples, () => pico.Ready, pico.CheckCompletionStatus,
                    (uint)s.ExpectedArtCount, s.CaptureDurationMs, new MonotonicInputCaptureClock());
                waiting.WaitQuiet(s.ArmDelayMs, cancellation);
                passed("静默期完成：发出触发前未发现任何一张卡已采集。");
                Action pulse = triggerOutput == null ? (Action)null : () =>
                {
                    begin("TRG_OUTPUT", "请求 P0.7 晶闸管光触发，正常保持 " + BenchSettings.OpticalHoldMilliseconds + " ms 后回低；取消或错误将尝试提前回低，不算完成完整保持。同步检测共用信号进入 PFI0 与 Pico EXT。");
                    triggerOutput.Pulse(cancellation);
                    report("STEP_TRG_OUTPUT_UNVERIFIED", "P0.7 高写入、" + BenchSettings.OpticalHoldMilliseconds + " ms 软件保持及回低/清理调用完成；发光、晶闸管导通和物理电平/时长未独立测量。采集段仍为 " + s.CaptureDurationMs + " ms，继续核对两卡完整数据。");
                    activeStage = "WAIT_TRIGGER";
                };
                waiting.WaitComplete(s.TriggerTimeoutMs, () => begin("WAIT_TRIGGER", "等待共享" +
                    (s.FallingTrigger ? "下降沿" : "上升沿") + "进入 " + s.ArtTrigger + " 与 Pico EXT（" + s.ExternalTriggerVolts + " V），超时 " + s.TriggerTimeoutMs + " ms。"), pulse, cancellation,
                    (count, ready) => report("ACQ_STATE", "ART=" + count + "/" + s.ExpectedArtCount + " 点；Pico=" + (ready ? "完成回调已到达" : "等待完成回调")));
                passed("两卡均完成共享外触发采集；布置静默期及最短采集时间检查通过。未标定两卡固有时延。");
                cancellation.ThrowIfCancellationRequested();
                begin("READ", "读取 ART 与 Pico，校验完整 " + s.CaptureDurationMs + " ms 数据、实际时基和分辨率。");
                double[] voltage = art.Read(100);
                cancellation.ThrowIfCancellationRequested();
                PicoBlockCapture pd = pico.ReadCompleted();
                cancellation.ThrowIfCancellationRequested();
                if (voltage.Length != s.ExpectedArtCount || pd.SampleCount != s.RequestedPicoCount ||
                    pd.RawAdc == null || pd.RawAdc.Length != s.RequestedPicoCount)
                    throw new InvalidDataException("未取得本轮请求的完整 ART " + s.ExpectedArtCount + " 点 / Pico " + s.RequestedPicoCount + " 点数据。");
                if (pd.ResolutionModeBits != s.ResolutionBits || pd.MaximumAdc <= 0 ||
                    !(pd.SampleIntervalNanoseconds > 0) || Double.IsInfinity(pd.SampleIntervalNanoseconds) ||
                    !PicoCaptureConfiguration.RateMatches(1e9 / pd.SampleIntervalNanoseconds, s.RequestedPicoRateHz))
                    throw new InvalidDataException("Pico 返回的分辨率、ADC 或采样间隔不符合本轮设置要求。");
                if (!String.Equals(pd.Serial, s.PicoSerial.Trim(), StringComparison.Ordinal))
                    throw new InvalidDataException("采集返回的 Pico 序列号发生变化。");
                result = new BenchCapture
                {
                    Settings = s,
                    VoltageRaw = voltage,
                    PdAdc = pd.RawAdc,
                    ArtRateHz = art.ActualRate,
                    PicoRateHz = 1e9 / pd.SampleIntervalNanoseconds,
                    MaximumAdc = pd.MaximumAdc,
                    PicoFullScaleV = pd.FullScaleVolts,
                    HardwareBits = pd.RangeHardwareBits,
                    Overflow = pd.HardwareOverflow,
                    Simulation = false,
                    ArtSerial = art.Serial,
                    PicoSerial = pd.Serial,
                    Runtime = "ART: " + art.RuntimePath + " | Pico: " + PicoRuntimeLocator.Resolve(s.PicoLibraryDirectory),
                    CapturedUtc = DateTime.UtcNow,
                    DirectoryPath = directory
                };
                result.ValidateCapture();
                passed("已验证 ART " + voltage.Length + " 点 / " + art.ActualRate + " S/s；Pico " + pd.SampleCount + " 点 / " +
                    (result.PicoRateHz / 1e6).ToString("0.######", CultureInfo.InvariantCulture) + " MS/s / " + s.ResolutionBits +
                    " bit 模式；完整 " + s.CaptureDurationMs + " ms。溢出=" + result.Overflow);
            }
            catch (Exception ex)
            {
                failure = ex;
                report("STEP_" + activeStage + (ex is OperationCanceledException ? "_CANCELED" : "_FAILED"), ex.ToString());
            }
            finally
            {
                try { begin("RELEASE", "释放本工具 TRG 任务、ART 输入任务及指定 Pico 连接；不重置设备或其他端口。"); }
                catch (Exception ex) { cleanupErrors.Add(ex); }
                try { if (triggerOutput != null) triggerOutput.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                try { art.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                try { pico.Dispose(); } catch (Exception ex) { cleanupErrors.Add(ex); }
                report("STEP_RELEASE_" + (cleanupErrors.Count == 0 ? "PASSED" : "FAILED"), cleanupErrors.Count == 0
                    ? "本工具资源释放调用成功；不代表外部设备已放电。"
                    : new AggregateException("资源释放失败；不保证物理 TRG 已回低。", cleanupErrors).ToString());
            }
            if (cleanupErrors.Count != 0)
            {
                if (failure != null) cleanupErrors.Insert(0, failure);
                throw new AggregateException("采集或输入资源释放失败；本工具工作进程退出后可重新尝试连接。", cleanupErrors);
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        private static void Delay(int milliseconds, CancellationToken cancellation)
        {
            if (cancellation.WaitHandle.WaitOne(milliseconds)) cancellation.ThrowIfCancellationRequested();
        }
    }

    internal sealed class InputOnlyArt : IDisposable
    {
        private IntPtr task;
        private IntPtr safetyTask;
        private bool safetyStarted;
        private int sampleCount;
        internal bool Armed { get; private set; }
        internal bool SafetyRawHigh { get; private set; }
        internal string Serial { get; private set; }
        internal double ActualRate { get; private set; }
        internal string RuntimePath { get; private set; }

        internal void Prepare(BenchSettings settings, bool freeRun = false, bool retainSafetyInput = false, bool readSafetyInput = true)
        {
            if (task != IntPtr.Zero) throw new InvalidOperationException("已有 ART 输入任务。");
            sampleCount = settings.ExpectedArtCount;
            RuntimePath = InputOnlyArtNative.Load(settings.ArtLibraryDirectory);
            uint serial;
            InputOnlyArtNative.Check(InputOnlyArtNative.GetSerial(settings.ArtDevice, 0x0632, out serial, IntPtr.Zero), "读取 ART 序列号");
            Serial = serial.ToString(CultureInfo.InvariantCulture);
            if (serial == 0 || !String.Equals(Serial, settings.ArtSerial.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("ART 序列号不匹配：" + settings.ArtDevice + " 返回 " + Serial + "；未创建采集任务。");
            var product = new StringBuilder(128);
            InputOnlyArtNative.Check(InputOnlyArtNative.GetProduct(settings.ArtDevice, 0x0631, product, product.Capacity, IntPtr.Zero), "读取 ART 型号");
            if (!String.Equals(product.ToString().Trim(), "USB3135", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("预期 USB3135，实际型号 " + product + "；未创建采集任务。");
            if(!readSafetyInput && (!freeRun || retainSafetyInput))
                throw new InvalidOperationException("无DI模式仅允许自由运行的AI只读采集。");
            if(readSafetyInput)ReadSafetyDiagnostic(settings.ArtDevice,retainSafetyInput);
            IntPtr created;
            int status = InputOnlyArtNative.CreateTask(String.Empty, out created);
            task = created;
            InputOnlyArtNative.Check(status, "创建只读 ART AI4 任务");
            if (task == IntPtr.Zero) throw new InvalidOperationException("ART 未返回有效任务句柄。");
            InputOnlyArtNative.Check(InputOnlyArtNative.CreateAi(task, settings.ArtDevice + "/ai4", String.Empty,
                settings.ArtTerminal, -10, 10, 10348, null), "配置 AI4 输入参考与 ±10 V 量程");
            InputOnlyArtNative.Check(InputOnlyArtNative.ConfigureClock(task, String.Empty, BenchSettings.ArtRate,
                10280, 10178, sampleCount), "配置 80 kS/s · " + settings.CaptureDurationMs + " ms 有限采集");
            if (!freeRun)
                InputOnlyArtNative.Check(InputOnlyArtNative.ConfigureTrigger(task, settings.ArtTrigger,
                    settings.FallingTrigger ? 10171 : 10280), "配置 ART 外部数字触发");
            InputOnlyArtNative.Check(InputOnlyArtNative.TaskControl(task, 2), "验证 ART 采集参数");
            InputOnlyArtNative.Check(InputOnlyArtNative.TaskControl(task, 3), "提交 ART 输入时钟及触发参数，尚未启动采集");
            double rate;
            InputOnlyArtNative.Check(InputOnlyArtNative.GetTimingRate(task, 0x1344, out rate, sizeof(double)), "回读 ART 实际采样率");
            if (Double.IsNaN(rate) || Double.IsInfinity(rate) || Math.Abs(rate - BenchSettings.ArtRate) > .5)
                throw new InvalidOperationException("ART 时钟回读 " + rate.ToString("G17", CultureInfo.InvariantCulture) + " S/s 与请求不符。");
            ActualRate = rate;
        }

        private void ReadSafetyDiagnostic(string device,bool retainSafetyInput)
        {
            IntPtr created;
            int status = InputOnlyArtNative.CreateTask(String.Empty, out created);
            safetyTask = created;
            InputOnlyArtNative.Check(status, "创建 P0.4 只读数字输入任务");
            if (safetyTask == IntPtr.Zero) throw new InvalidOperationException("ART 未返回有效数字输入句柄。");
            InputOnlyArtNative.Check(InputOnlyArtNative.CreateDi(safetyTask, device + "/port0/line4", String.Empty, 0), "配置 P0.4 数字输入");
            safetyStarted = true;
            InputOnlyArtNative.Check(InputOnlyArtNative.StartTask(safetyTask), "启动 P0.4 输入诊断");
            // The zero-input owner may transfer this same task to its sole DI
            // monitor. No second reader/task is created and cleanup stays here.
            if(retainSafetyInput)return;
            SafetyRawHigh=ReadSafetyInput(100);
            ClearOwnedTask(ref safetyTask, ref safetyStarted, "P0.4 输入诊断");
        }

        internal bool ReadSafetyInput(int timeoutMs)
        {
            if(safetyTask==IntPtr.Zero || !safetyStarted)throw new InvalidOperationException("P0.4 输入任务未启动。");
            var data = new byte[] { 0xA5 };
            int count=-1, bytes=-1;
            int status=InputOnlyArtNative.ReadDigitalReceipt(safetyTask,1,Math.Max(1,timeoutMs)/1000.0,0,
                data,(uint)data.Length,ref count,ref bytes,IntPtr.Zero);
            if(Xiongxin.FactoryTest.FactoryEmergencyReadPolicy.IsZeroSamplePending(status,count))
                throw new Xiongxin.FactoryTest.FactoryEmergencyReadTimeoutException(status,null);
            InputOnlyArtNative.Check(status,"读取 P0.4 原始电平");
            if (count != 1 || bytes != 1 || data[0] > 1) throw new InvalidDataException("P0.4 未返回有效的单线输入样本。");
            return data[0]!=0;
        }

        internal void Arm()
        {
            if (task == IntPtr.Zero || Armed) throw new InvalidOperationException("ART 输入任务未就绪或已启动。");
            // Retain the ownership state even if StartTask fails halfway through.
            Armed = true;
            InputOnlyArtNative.Check(InputOnlyArtNative.StartTask(task), "布置 ART AI4 等待外触发");
        }

        internal double[] Read(int timeoutMs)
        {
            if (task == IntPtr.Zero || !Armed) throw new InvalidOperationException("ART 输入任务未布置。");
            var data = new double[sampleCount];
            int read;
            int code = InputOnlyArtNative.ReadAnalog(task, data.Length, Math.Max(.1, timeoutMs / 1000.0),
                0, data, (uint)data.Length, out read, IntPtr.Zero);
            if (code == -200284 || code == -26802)
                throw new TimeoutException("ART 等待数字触发或完整数据超时；未用软件触发替代。");
            InputOnlyArtNative.Check(code, "读取 ART AI4 本轮有限段数据");
            if (read != data.Length) throw new InvalidDataException("ART 返回 " + read + " 点，预期 " + data.Length + " 点。");
            foreach (double value in data)
                if (Double.IsNaN(value) || Double.IsInfinity(value)) throw new InvalidDataException("ART 返回非有限电压值。");
            return data;
        }

        internal uint AvailableSamples()
        {
            if (task == IntPtr.Zero || !Armed) throw new InvalidOperationException("ART 输入任务未布置。");
            uint count;
            InputOnlyArtNative.Check(InputOnlyArtNative.GetAvailableSamples(task, out count), "读取 ART 可用点数");
            if (count > sampleCount) throw new InvalidDataException("ART 返回超出有限段长度的可用点数。");
            return count;
        }

        public void Dispose()
        {
            var errors = new List<Exception>();
            try { ClearOwnedTask(ref safetyTask, ref safetyStarted, "P0.4 输入诊断"); }
            catch (Exception ex) { errors.Add(ex); }
            bool started = Armed;
            try { ClearOwnedTask(ref task, ref started, "ART AI4 输入"); }
            catch (Exception ex) { errors.Add(ex); }
            Armed = started;
            if (errors.Count != 0) throw new AggregateException("只读 ART 任务清理失败。", errors);
        }

        private static void ClearOwnedTask(ref IntPtr handle, ref bool started, string name)
        {
            if (handle == IntPtr.Zero) return;
            Exception stopError = null;
            if (started)
            {
                try { InputOnlyArtNative.Check(InputOnlyArtNative.StopTask(handle), "停止 " + name); started = false; }
                catch (Exception ex) { stopError = ex; }
            }
            try { InputOnlyArtNative.Check(InputOnlyArtNative.ClearTask(handle), "释放 " + name); }
            catch (Exception clearError)
            {
                if (stopError != null) throw new AggregateException("ART 停止和释放均失败。", stopError, clearError);
                throw;
            }
            handle = IntPtr.Zero;
            started = false;
            if (stopError != null) throw new InvalidOperationException("ART 任务已释放，但停止调用报告错误。", stopError);
        }
    }

    internal static class InputOnlyArtNative
    {
        private static string loadedPath;
        internal static string Load(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("ART 运行库目录不能为空。");
            string path = Path.GetFullPath(Path.Combine(directory, "Art_DAQ.dll"));
            if (loadedPath != null)
            {
                if (!String.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("工作进程已加载另一份 ART 运行库。");
                return loadedPath;
            }
            if (!File.Exists(path)) throw new FileNotFoundException("未找到 ART x64 运行库。", path);
            PicoRuntimeLocator.VerifyX64(path);
            int status = HvxConfigureArtPathW(path);
            if (status != 0) throw new InvalidOperationException("ART backend configuration failed (bridge " + status + ").");
            loadedPath = path;
            return path;
        }
        internal static void Check(int status, string operation)
        {
            if (status >= 0) return;
            var data = new byte[4096];
            string detail = String.Empty;
            try { GetError(data, (uint)data.Length); detail = Encoding.Default.GetString(data).TrimEnd('\0', ' ', '\r', '\n'); }
            catch (Exception infoError) { detail = "错误详情获取失败：" + infoError.Message; }
            throw new InvalidOperationException(operation + "失败，ART " + status + "：" + detail);
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true, CallingConvention=CallingConvention.StdCall)] private static extern int HvxConfigureArtPathW(string path);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetDeviceAttribute", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int GetSerial(string device, int attribute, out uint serial, IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetDeviceAttributeEx", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int GetProduct(string device, int attribute, StringBuilder product, int capacity, IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateTask", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int CreateTask(string name, out IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateAIVoltageChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int CreateAi(IntPtr task, string physical, string name, int terminal, double minimum, double maximum, int unit, string scale);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CreateDIChan", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int CreateDi(IntPtr task, string lines, string assignedName, int grouping);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadDigitalLines", CallingConvention = CallingConvention.StdCall)] internal static extern int ReadDigital(IntPtr task, int samples, double timeout, int layout, [Out] byte[] data, uint length, out int read, out int bytesPerSample, IntPtr reserved);
        // Same native ABI, but preserve the caller's -1 sentinel if the SDK
        // does not write its output count. Other legacy out callers are unchanged.
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadDigitalLines", CallingConvention = CallingConvention.StdCall)] internal static extern int ReadDigitalReceipt(IntPtr task, int samples, double timeout, int layout, [Out] byte[] data, uint length, [In,Out] ref int read, [In,Out] ref int bytesPerSample, IntPtr reserved);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgSampClkTiming", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int ConfigureClock(IntPtr task, string source, double rate, int edge, int mode, int samples);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_CfgDigEdgeStartTrig", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] internal static extern int ConfigureTrigger(IntPtr task, string source, int edge);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetTimingAttribute", CallingConvention = CallingConvention.StdCall)] internal static extern int GetTimingRate(IntPtr task, int attribute, out double rate, int bytes);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_TaskControl", CallingConvention = CallingConvention.StdCall)] internal static extern int TaskControl(IntPtr task, int action);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StartTask", CallingConvention = CallingConvention.StdCall)] internal static extern int StartTask(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadAnalogF64", CallingConvention = CallingConvention.StdCall)] internal static extern int ReadAnalog(IntPtr task, int samples, double timeout, int layout, [Out] double[] data, uint length, out int read, IntPtr reserved);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ReadAnalogF64", CallingConvention = CallingConvention.StdCall)] internal static extern int ReadAnalogReceipt(IntPtr task, int samples, double timeout, int layout, [Out] double[] data, uint length, [In,Out] ref int read, IntPtr reserved);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetReadAvailSampPerChan", CallingConvention = CallingConvention.StdCall)] internal static extern int GetAvailableSamples(IntPtr task, out uint count);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_StopTask", CallingConvention = CallingConvention.StdCall)] internal static extern int StopTask(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_ClearTask", CallingConvention = CallingConvention.StdCall)] internal static extern int ClearTask(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetExtendedErrorInfo", CallingConvention = CallingConvention.StdCall)] private static extern int GetError([Out] byte[] error, uint size);
    }
}
