using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Xiongxin.Scope
{
    internal sealed class Engine
    {
        short handle; bool simulation, connected, faulted;
        string serial = "", model = "", driver = "";
        string captureStage = "Idle";
        internal Action<string> Trace;
        void Stage(string value) { captureStage=value; TraceEvent(value); }
        void TraceEvent(string value) { try { if(Trace!=null)Trace(value); } catch { /* Diagnostics must not bypass cleanup. */ } }
        readonly List<GCHandle> pins = new List<GCHandle>();
        Mutex deviceLease;
        internal bool Connected { get { return connected; } }
        internal bool Faulted { get { return faulted; } }
        internal Reply Status(string message = "") { return new Reply { Connected = connected, Faulted = faulted, Serial = serial, Model = model, Driver = driver, Library = simulation ? "仿真，不加载SDK" : Native.LoadedPath, Message = message, CaptureStage = captureStage }; }
        string Info(int index) { var b = new StringBuilder(512); short needed; Native.Check(Native.ps5000aGetUnitInfo(handle, b, 512, out needed, index), "读取设备信息"); return b.ToString(); }
        internal Reply Scan(Settings c)
        {
            if (handle != 0 || connected) throw new InvalidOperationException("请先释放当前设备再扫描。");
            if (c.Simulation) return new Reply { Message = "SIM-5242D（仿真）", Serial = "SIM-5242D" };
            Native.Load(c.LibraryPath); short count, length = 4096; var b = new StringBuilder(4096);
            Native.Check(Native.ps5000aEnumerateUnits(out count, b, ref length), "枚举设备");
            return new Reply { Serial = b.ToString(), Library = Native.LoadedPath, Message = "可枚举设备 " + count + " 台：" + b + "；已被其他程序打开的设备可能不在列表中。" };
        }
        internal Reply Open(Settings c)
        {
            if (connected || handle != 0) throw new InvalidOperationException("设备尚未释放。");
            if (c.Resolution < 0 || c.Resolution > 4) throw new ArgumentException("分辨率无效。");
            simulation = c.Simulation; faulted = false;
            if (simulation)
            {
                if (c.SimFault.StartsWith("模拟断连")) throw new InvalidOperationException("仿真设备离线，请恢复后重连。");
                connected = true; serial = "SIM-5242D"; model = "5242D 仿真"; driver = "SIM 1.0"; return Status("仿真已连接。");
            }
            Native.Load(c.LibraryPath);
            string requested = (c.Serial ?? "").Trim();
            if (requested.Length == 0)
            {
                string[] found = Scan(c).Serial.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (found.Length != 1) throw new InvalidOperationException("请先扫描并填写唯一的设备序列号，避免连接错误设备。");
                requested = found[0].Trim();
            }
            try
            {
                // One owner per physical serial among copies of this program in this login session.
                deviceLease = new Mutex(false,"Local\\Xiongxin-PicoScope-"+requested.Replace('/','_').Replace('\\','_'));
                bool acquired;try{acquired=deviceLease.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
                if(!acquired){deviceLease.Dispose();deviceLease=null;throw new InvalidOperationException("本设备正由另一份本工具占用，请先在原窗口释放设备。");}
                Native.SuppressStartupWindow();
                uint code = Native.ps5000aOpenUnit(out handle, requested, c.Resolution);
                if (handle < 0) handle = 0; // Negative values are failed-open sentinels, not owned handles.
                if (code == 0x119 || code == 0x11A || code == 0x11E) Native.Check(Native.ps5000aChangePowerSource(handle, code), "确认设备供电状态");
                else Native.Check(code, "打开设备");
                if (handle == 0) throw new InvalidOperationException("打开设备未返回有效句柄。");
                model = Info(3); serial = Info(4); driver = Info(0);
                if (!String.Equals(serial, requested, StringComparison.Ordinal)) throw new InvalidOperationException("返回的序列号与指定设备不一致。");
                if (!String.Equals(model.Trim(), "5242D", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("本工具验证目标为非MSO 5242D，实际型号为 " + model);
                connected = true; return Status("已连接，未启动采集；仅在点击采集后应用通道和触发参数。\r\n"+Native.StartupNote);
            }
            catch (Exception e)
            {
                try { Close(); } catch (Exception cleanup) { faulted = true; throw new AggregateException("打开失败且释放失败，禁止继续采集。", e, cleanup); }
                throw;
            }
        }
        internal Reply Ping(Settings c)
        {
            Require();
            try
            {
                if (simulation) { if (c.SimFault.StartsWith("模拟断连")) throw new IOExceptionLike("仿真通讯断开。"); }
                else { Native.Check(Native.ps5000aPingUnit(handle), "检查连接"); if (Info(4) != serial) throw new IOExceptionLike("设备身份变化。"); }
                return Status("连接检查通过。");
            }
            catch { faulted = true; throw; }
        }
        internal void Close()
        {
            bool closedNative = false;
            if (handle != 0)
            {
                // Always attempt Close even when Stop returns an error. Do not lose a failed handle.
                Exception stopError = null;
                try { Native.Check(Native.ps5000aStop(handle), "释放前停止采集"); } catch (Exception ex) { stopError = ex; }
                try { Native.Check(Native.ps5000aCloseUnit(handle), "关闭设备"); }
                catch (Exception ex) { faulted = true; if (stopError != null) throw new AggregateException(stopError, ex); throw; }
                handle = 0;
                closedNative = true;
            }
            FreePins(); connected = false; faulted = false;
            if(deviceLease!=null){deviceLease.ReleaseMutex();deviceLease.Dispose();deviceLease=null;}
            if (closedNative && !String.IsNullOrWhiteSpace(serial))
                HVX.Acquisition.PicoCaptureCadence.Shared.ConfirmReleasedAsync(serial,TraceEvent).GetAwaiter().GetResult();
        }
        void Require() { if (!connected || faulted) throw new InvalidOperationException("连接未就绪或发生故障，请先释放并重连。"); }
        void FreePins() { foreach (var p in pins) if (p.IsAllocated) p.Free(); pins.Clear(); }
        double Timebase(uint tb, int samples, out int capacity)
        { float ns; Native.Check(Native.ps5000aGetTimebase2(handle, tb, samples, out ns, out capacity, 0), "读取时基 " + tb); if (!Settings.Finite(ns) || ns <= 0) throw new InvalidOperationException("驱动返回无效采样间隔。"); return ns; }
        uint ChooseTimebase(double desiredNs)
        {
            uint first = 0; float ns = 0; int capacity;
            for (; first < 32; first++) { uint code = Native.ps5000aGetTimebase2(handle, first, 2, out ns, out capacity, 0); if (code == 0) break; if (code != 14) Native.Check(code, "查询最小时基"); }
            if (first == 32) throw new InvalidOperationException("未找到当前通道和分辨率的可用时基。");
            if (!Settings.Finite(ns) || ns <= 0) throw new InvalidOperationException("驱动返回无效的最小时基。");
            if (ns >= desiredNs) return first;
            uint lo = first, hi = Math.Max(first + 1, 8);
            while (Timebase(hi, 2, out capacity) < desiredNs) { lo = hi; hi *= 2; if (hi > 16777216) throw new InvalidOperationException("请求采样率超出工具支持范围。"); }
            while (hi - lo > 1) { uint mid = lo + (hi - lo) / 2; if (Timebase(mid, 2, out capacity) >= desiredNs) hi = mid; else lo = mid; }
            return hi;
        }
        internal Capture Capture(Settings c, Func<bool> cancel)
        {
            Stage("Configure");
            Require(); c.Validate();
            HVX.Acquisition.PicoCaptureCadence.RequireDuration(c.DurationMs);
            if (simulation) return Simulate(c, cancel);
            bool mayBeRunning = false;
            bool stopConfirmed = false;
            double actualDurationMs=c.DurationMs;
            Exception captureError = null;
            // Worker stdin cancellation stays live while its SDK thread waits.
            // No native call or pinned allocation occurs during cooldown.
            var cadence = HVX.Acquisition.PicoCaptureCadence.Shared.EnterAsync(serial,CancellationToken.None,TraceEvent,cancel).GetAwaiter().GetResult();
            try
            {
                cadence.BeginCapture();
                Native.Check(Native.ps5000aStop(handle), "采集前停止");
                stopConfirmed = true;
                Native.Check(Native.ps5000aSetDeviceResolution(handle, c.Resolution), "设置分辨率");
                for (int ch = 0; ch < 2; ch++)
                {
                    var setting = ch == 0 ? c.A : c.B;
                    Native.Check(Native.ps5000aSetChannel(handle, ch, (short)(setting.Enabled ? 1 : 0), setting.Coupling, setting.Range, 0), "设置通道 " + (ch == 0 ? "A" : "B"));
                    if (setting.Enabled) Native.Check(Native.ps5000aSetBandwidthFilter(handle, ch, setting.Limit20MHz ? 1 : 0), "设置带宽");
                }
                int bits; Native.Check(Native.ps5000aGetDeviceResolution(handle, out bits), "复核分辨率");
                if (bits != c.Resolution) throw new InvalidOperationException("实际分辨率与请求不一致。");
                int capacity; Native.Check(Native.ps5000aMemorySegments(handle, 1, out capacity), "设置单段内存");
                uint tb = ChooseTimebase(1000 / c.RateMS); int available;
                double dt = Timebase(tb, 2, out available); int count = c.Count(dt);
                dt = Timebase(tb, count, out available);
                HVX.Acquisition.PicoCaptureCadence.RequireSamples(count,1e9/dt);
                actualDurationMs=count*dt/1000000.0;
                if (count > capacity || count > available) throw new InvalidOperationException("当前模式设备内存不足，请降低采样率或缩短采集时长。");
                if ((c.TriggerMode == 0 ? 0 : c.DelaySamples * dt / 1000000) + count * dt / 1000000 + (c.TriggerMode == 2 ? c.AutoMs : 0) >= c.TimeoutMs)
                    throw new ArgumentException("按实际时基计算的延时、采集时长超过超时预算。");
                short maximum; Native.Check(Native.ps5000aMaximumValue(handle, out maximum), "读取ADC满量程");
                if (maximum <= 0) throw new InvalidOperationException("ADC满量程无效。");
                double fullScale = c.TriggerSource == 4 ? 5 : Settings.Ranges[(c.TriggerSource == 0 ? c.A : c.B).Range];
                short threshold = c.TriggerMode == 0 ? (short)0 : (short)Math.Round(c.ThresholdV / fullScale * (c.TriggerSource == 4 ? 32767 : maximum));
                Native.Check(Native.ps5000aSetSimpleTrigger(handle, (short)(c.TriggerMode == 0 ? 0 : 1), c.TriggerMode == 0 ? (c.A.Enabled ? 0 : 1) : c.TriggerSource, threshold, c.TriggerEdge,
                    c.TriggerMode == 0 ? 0 : c.DelaySamples, (short)(c.TriggerMode == 2 ? c.AutoMs : 0)), "设置触发");
                var result = new Capture { Info = Status(), A = c.A.Enabled ? new short[count] : null, B = c.B.Enabled ? new short[count] : null };
                for (int ch = 0; ch < 2; ch++)
                {
                    short[] values = ch == 0 ? result.A : result.B; if (values == null) continue;
                    var pin = GCHandle.Alloc(values, GCHandleType.Pinned); pins.Add(pin);
                    Native.Check(Native.ps5000aSetDataBuffer(handle, ch, pin.AddrOfPinnedObject(), count, 0, 0), "注册采集缓存");
                }
                int pre = c.TriggerMode == 0 ? 0 : (int)(count * c.PrePercent / 100); int wait;
                if (cancel()) throw new OperationCanceledException();
                Stage("RunBlock");
                mayBeRunning = true; stopConfirmed=false; Native.Check(Native.ps5000aRunBlock(handle, pre, count - pre, tb, out wait, 0, IntPtr.Zero, IntPtr.Zero), "启动段采集");
                Stage("AwaitReady");
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    if (cancel()) throw new OperationCanceledException();
                    if (clock.ElapsedMilliseconds >= c.TimeoutMs) throw new TimeoutException("等待触发或采集完成超时；未返回有效波形。");
                    short ready; Native.Check(Native.ps5000aIsReady(handle, out ready), "等待采集完成"); if (ready != 0) break;
                    Thread.Sleep(10);
                }
                uint returned = (uint)count; short overflow;
                Stage("GetValues");
                Native.Check(Native.ps5000aGetValues(handle, 0, ref returned, 1, 0, 0, out overflow), "读取波形");
                if (returned != count) throw new InvalidOperationException("返回点数与请求不一致，丢弃不完整采集段。");
                var r = result.Info; r.Config = c; r.Samples = count; r.PreSamples = pre; r.IntervalNs = dt; r.Timebase = tb;
                r.MaxAdc = maximum; r.Overflow = overflow; r.Resolution = bits;
                r.Message = c.TriggerMode == 2 ? "自动触发模式：可能由阈值或超时启动，不能据此证明收到外部触发。" : "采集完成。";
                Stage("Complete"); r.CaptureStage = captureStage;
                return result;
            }
            catch (Exception ex) { captureError = ex; throw; }
            finally
            {
                try
                {
                // A successful Stop is mandatory before unpinning buffers used by a possibly active capture.
                if (mayBeRunning)
                {
                    TraceEvent("Stop.enter after " + captureStage);
                    try { Native.Check(Native.ps5000aStop(handle), "结束采集"); stopConfirmed=true; }
                    catch (Exception stopError)
                    {
                        faulted = true;
                        if (captureError != null) throw new AggregateException("采集阶段 " + captureStage + " 异常，随后停止失败。", captureError, stopError);
                        captureStage = "Stop"; throw;
                    }
                }
                if(stopConfirmed)
                {
                    FreePins();
                    // This engine is calibration-only. Pressure and stress callers
                    // retain their existing 2 s cadence; prior records are honored.
                    cadence.Released(actualDurationMs, c.CalibrationPulse, !c.CalibrationPulse);
                    TraceEvent("Capture.exit");
                }
                else faulted=true;
                }
                finally { cadence.Dispose(); }
            }
        }
        Capture Simulate(Settings c, Func<bool> cancel)
        {
            if (c.SimFault.StartsWith("模拟断连")) { faulted = true; throw new IOExceptionLike("仿真设备已断连，请释放后重连。"); }
            if (c.SimFault == "模拟驱动卡死") Thread.Sleep(Timeout.Infinite);
            int wait = c.SimFault == "模拟无触发" && c.TriggerMode == 1 ? c.TimeoutMs : c.TriggerMode == 2 ? c.AutoMs : 100;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < wait) { if (cancel()) throw new OperationCanceledException(); Thread.Sleep(10); }
            if (c.SimFault == "模拟无触发" && c.TriggerMode == 1) throw new TimeoutException("仿真等待触发超时。");
            double dt = 1000 / c.RateMS; int n = c.Count(dt); int pre = c.TriggerMode == 0 ? 0 : (int)(n * c.PrePercent / 100);
            var data = new Capture { Info = Status(), A = c.A.Enabled ? new short[n] : null, B = c.B.Enabled ? new short[n] : null };
            var random = new Random(); short ov = 0;
            for (int ch = 0; ch < 2; ch++)
            {
                short[] a = ch == 0 ? data.A : data.B; if (a == null) continue;
                double scale = Settings.Ranges[(ch == 0 ? c.A : c.B).Range];
                for (int i = 0; i < n; i++)
                {
                    if (i % 32768 == 0 && cancel()) throw new OperationCanceledException();
                    double t = (i - pre) * dt * 1e-9;
                    double volts = (ch == 0 ? .15 * Math.Sin(2 * Math.PI * 1000 * t) : .08 * Math.Sign(Math.Sin(2 * Math.PI * 500 * t))) + (random.NextDouble() - .5) * .002;
                    if (c.SimFault == "模拟削顶") volts *= 100;
                    double adc = volts / scale * 32767;
                    if (Math.Abs(adc) > 32767) ov |= (short)(1 << ch);
                    a[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(adc)));
                }
            }
            var r = data.Info; r.Config = c; r.Samples = n; r.PreSamples = pre; r.IntervalNs = dt; r.MaxAdc = 32767; r.Overflow = ov; r.Resolution = c.Resolution;
            r.Message = "合成波形，仅验证界面与生命周期；不模拟实际ADC位数、滤波及精确触发相位。"; return data;
        }
        sealed class IOExceptionLike : Exception { internal IOExceptionLike(string message) : base(message) { } }
    }
}
