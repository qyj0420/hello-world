using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.Calibration
{
    // This interface deliberately has no high-voltage, analogue-output or ground commands.
    internal interface IControlBoxIo
    {
        void Open();
        bool ReadEmergencySignalHigh();
        void WriteShortCable(bool on);
        void Close();
    }

    internal sealed class ControlBox
    {
        readonly IControlBoxIo io;
        readonly object ioGate = new object(), stateGate = new object();
        volatile CalibrationEmergencyMonitor emergencyMonitor;
        bool? emergencySignalHigh;
        volatile bool connected, cleanupPending, capturing, shortCable;
        int captureStarting;
        long impedanceRevision;
        volatile bool shortCableVerified;
        internal bool Connected { get { return connected; } private set { connected=value; } }
        // Connected includes retained ownership after failed cleanup, not permission to acquire.
        internal bool CleanupPending { get { return cleanupPending; } private set { cleanupPending=value; } }
        internal bool? EmergencySignalHigh { get { var monitor=emergencyMonitor;if(monitor!=null)return monitor.Snapshot.High;lock(stateGate) return emergencySignalHigh; } private set { lock(stateGate) emergencySignalHigh=value; } }
        internal bool Capturing { get { return capturing; } private set { capturing=value; } }
        internal bool ShortCable { get { return shortCable; } private set { shortCable=value; } }
        internal bool ShortCableVerified { get { return shortCableVerified; } }
        internal long ImpedanceRevision { get { return Interlocked.Read(ref impedanceRevision); } }
        internal ControlBox(IControlBoxIo io) { this.io = io ?? throw new ArgumentNullException("io"); }
        internal bool EmergencyMonitoringActive { get { return emergencyMonitor != null; } }
        internal CalibrationEmergencySnapshot EmergencySnapshot { get { var monitor=emergencyMonitor;return monitor==null?null:monitor.Snapshot; } }
        internal void RequestMonitorStop(){var monitor=emergencyMonitor;if(monitor!=null)monitor.Stop(0);}
        internal void ConnectMonitored(Action<Exception> cancelRound,Action<CalibrationEmergencySnapshot> changed)
        {
            if(Connected||CleanupPending)throw new InvalidOperationException("控制箱连接仍被占用，请先完成释放。");
            Connected=true;CleanupPending=true;
            try
            {
                lock(ioGate)io.Open();CleanupPending=false;
                var timed=io as ICalibrationEmergencyIo;
                CalibrationEmergencyMonitor created=null;
                created=new CalibrationEmergencyMonitor(timeout=>{
                    lock(ioGate){if(CleanupPending||!Connected)throw new IOException("控制箱正在释放，禁止读取许可。");
                        return timed==null?io.ReadEmergencySignalHigh():timed.ReadEmergencySignalHigh(timeout);}},
                    ()=>timed!=null&&timed.HasVerifiedSessionIdentity,cancelRound,
                    timed==null?(Action)null:(()=>{lock(ioGate){if(CleanupPending)throw new IOException("控制箱正在释放，不重建任务。");timed.RebuildEmergencyInput();}}),
                    snapshot=>{if(!Object.ReferenceEquals(emergencyMonitor,created))return;EmergencySignalHigh=snapshot.High;if(changed!=null)changed(snapshot);});
                emergencyMonitor=created;created.Start();
                // A new, post-request sample is required, including first connection.
                Poll();
            }
            catch(Exception openError)
            {
                EmergencySignalHigh=null;
                try{Disconnect();}catch(Exception closeError){throw new AggregateException("控制箱连接未完成，资源仍待释放。",openError,closeError);}
                throw;
            }
        }
        internal void Connect()
        {
            if (Connected || CleanupPending) throw new InvalidOperationException("控制箱连接仍被占用，请先完成释放。");
            // Open can fail after creating a task. Retain ownership until Close confirms cleanup.
            Connected = true;
            CleanupPending = true;
            try
            {
                lock(ioGate)io.Open();
                CleanupPending = false;
                Poll();
            }
            catch (Exception openError)
            {
                EmergencySignalHigh = null;
                try { Disconnect(); }
                catch (Exception closeError)
                {
                    throw new AggregateException("控制箱连接未完成，资源仍待释放，当前禁止校准。", openError, closeError);
                }
                throw;
            }
        }
        internal bool Poll()
        {
            EmergencySignalHigh = null;
            if (CleanupPending) throw new InvalidOperationException("控制箱资源尚未释放，请先重试释放，当前禁止校准。");
            if (!Connected) throw new InvalidOperationException("控制箱未连接，状态未知。");
            var monitor=emergencyMonitor;
            if(monitor!=null){bool value=monitor.ReadFreshAsync().GetAwaiter().GetResult();monitor.ThrowIfFaulted();EmergencySignalHigh=value;}
            else lock(ioGate) EmergencySignalHigh = io.ReadEmergencySignalHigh();
            return EmergencySignalHigh.Value;
        }
        // Calibration observes an external input only. Either measured emergency
        // level is valid here; this is never permission to energize HV outputs.
        internal async Task RequireInputReadyAsync()
        {
            var monitor=emergencyMonitor;
            if(monitor==null){RequireInputReady();return;}
            if(CleanupPending||!Connected)throw new InvalidOperationException("控制箱资源不可用，当前禁止校准。");
            await monitor.ReadFreshAsync();monitor.ThrowIfFaulted();
            if(!Object.ReferenceEquals(monitor,emergencyMonitor)||CleanupPending||!Connected)
                throw new InvalidOperationException("设备会话已改变，当前校准已取消。");
        }
        internal void RequireInputReady()
        {
            Poll();
        }
        internal void SetShortCable(bool value)
        {
            EnterIdleTransition();
            try {
                RequireInputReady();
                try { lock(ioGate) { RequireConnectedOwner();io.WriteShortCable(value); } }
                catch { shortCableVerified=false;CleanupPending = true; EmergencySignalHigh = null; throw; }
                PublishImpedanceWrite(value);
            }finally{Interlocked.Exchange(ref captureStarting,0);}
        }
        internal void BeginCapture()
        {
            EnterIdleTransition();
            try{RequireInputReady();lock(ioGate){RequireConnectedOwner();Capturing = true;}}finally{Interlocked.Exchange(ref captureStarting,0);}
        }
        internal async Task SetShortCableAsync(bool value)
        {
            EnterIdleTransition();
            try{
                await RequireInputReadyAsync();
                try{lock(ioGate){RequireConnectedOwner();io.WriteShortCable(value);}}
                catch{shortCableVerified=false;CleanupPending=true;EmergencySignalHigh=null;throw;}
                PublishImpedanceWrite(value);
            }finally{Interlocked.Exchange(ref captureStarting,0);}
        }
        void PublishImpedanceWrite(bool value)
        {
            ShortCable=value;shortCableVerified=true;Interlocked.Increment(ref impedanceRevision);
        }
        internal void RequireStableImpedance(long expectedRevision)
        {
            lock(ioGate) {
                RequireConnectedOwner();
                if(ImpedanceRevision!=expectedRevision)
                    throw new IOException("采集期间阻抗已切换。原始数据已保留，本段不合并为稳定校准，请重新采集。");
            }
        }
        internal async Task BeginCaptureAsync(){
            EnterIdleTransition();
            try{await RequireInputReadyAsync();lock(ioGate){RequireConnectedOwner();Capturing=true;}}finally{Interlocked.Exchange(ref captureStarting,0);}
        }
        void EnterIdleTransition(){
            if(Interlocked.CompareExchange(ref captureStarting,1,0)!=0)throw new InvalidOperationException("正在检查采集条件，请等待当前操作完成。");
            if(Capturing){Interlocked.Exchange(ref captureStarting,0);throw new InvalidOperationException("已经在采集，不能重复开始或切换阻抗。");}
        }
        void RequireConnectedOwner(){
            if(CleanupPending||!Connected)throw new InvalidOperationException("控制箱会话已结束，当前操作已取消。");
            var monitor=emergencyMonitor;if(monitor!=null)monitor.ThrowIfFaulted();
        }
        internal void EndCapture() { Capturing = false; }
        internal void Disconnect()
        {
            EmergencySignalHigh = null;
            shortCableVerified=false;
            CleanupPending = true;
            try {
                var monitor=emergencyMonitor;
                if(monitor!=null){monitor.Dispose();emergencyMonitor=null;}
                lock(ioGate)io.Close();
            }
            catch (Exception error)
            {
                Connected = true;
                throw new IOException("控制箱资源释放未完成，请重试释放；当前禁止校准。", error);
            }
            Connected = false; CleanupPending = false; Capturing = false;shortCableVerified=false;
        }
    }

    internal interface ICalibrationEmergencyIo
    {
        bool HasVerifiedSessionIdentity { get; }
        bool ReadEmergencySignalHigh(int timeoutMilliseconds);
        void RebuildEmergencyInput();
    }
    internal sealed class ArtControlBoxIo : IControlBoxIo, ICalibrationEmergencyIo
    {
        readonly string device, library;
        readonly string expectedSerial;
        readonly IArtControlBoxNative native;
        IntPtr input, impedance;
        internal bool HasOwnedTasks { get { return input != IntPtr.Zero || impedance != IntPtr.Zero; } }
        internal bool CleanupPending { get; private set; }
        public bool HasVerifiedSessionIdentity { get; private set; }
        bool inputRebuilt;
        internal ArtControlBoxIo(string device, string library) : this(device, library, new ArtControlBoxNative()) { }
        internal ArtControlBoxIo(string device, string library, string expectedSerial)
            : this(device, library, new ArtControlBoxNative(), expectedSerial) { }
        internal ArtControlBoxIo(string device, string library, IArtControlBoxNative native)
            : this(device, library, native, null) { }
        internal ArtControlBoxIo(string device, string library, IArtControlBoxNative native, string expectedSerial)
        { this.device = device; this.library = library; this.native = native ?? throw new ArgumentNullException("native"); this.expectedSerial=expectedSerial; }
        public void Open()
        {
            if (HasOwnedTasks || CleanupPending)
                throw new InvalidOperationException("控制箱任务尚未释放。");
            // Keep physical lines fixed: no configurable arbitrary output routes.
            if (String.IsNullOrWhiteSpace(device) || device.IndexOfAny(new[] {'/', '\\', ',', ':'}) >= 0)
                throw new ArgumentException("设备名称无效。");
            native.Load(library);
            HasVerifiedSessionIdentity=false;inputRebuilt=false;
            VerifySelectedIdentity();
            try
            {
                Art.Check(native.CreateTask("Calibration_P04", out input));
                if (input == IntPtr.Zero) throw new IOException("控制箱输入连接未建立。");
                Art.Check(native.CreateInput(input, device + "/port0/line4"));
                Art.Check(native.Start(input));
                // Impedance is created lazily on explicit selection, never touched at connect.
            }
            catch (Exception openError)
            {
                CleanupPending = true;
                try { Close(); }
                catch (Exception closeError)
                { throw new AggregateException("控制箱连接未完成，资源仍待释放。", openError, closeError); }
                throw;
            }
        }
        void VerifySelectedIdentity()
        {
            if(expectedSerial!=null)
            {
                uint expected, actual;
                if(!UInt32.TryParse(expectedSerial,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out expected)||expected==0||
                    expected.ToString(System.Globalization.CultureInfo.InvariantCulture)!=expectedSerial)
                    throw new ArgumentException("所选控制卡的序列号无效，当前禁止校准。");
                Art.Check(native.ReadDeviceSerial(device,out actual));
                if(actual==0||actual!=expected)throw new IOException("控制卡身份与所选设备不符，当前禁止校准，请返回主页检查连接。");
                // Evidence of this session's selected identity only. A driver
                // property may be cached and is NOT a live electrical permission.
                HasVerifiedSessionIdentity=true;
            }
        }
        public bool ReadEmergencySignalHigh()
        { return ReadEmergencySignalHigh(CalibrationEmergencyMonitor.FirstReadTimeoutMs); }
        public bool ReadEmergencySignalHigh(int timeoutMilliseconds)
        {
            RequireUsable();
            if (input == IntPtr.Zero) throw new IOException("急停状态读取不可用，请检查设备连接。");
            byte value; int read, bytes;
            if(timeoutMilliseconds<1||timeoutMilliseconds>CalibrationEmergencyMonitor.NoDataDeadlineMs)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            var timed=native as IArtTimedEmergencyNative;
            Art.Check(timed==null?native.Read(input,out value,out read,out bytes):
                timed.Read(input,timeoutMilliseconds/1000.0,out value,out read,out bytes));
            if (read != 1 || bytes != 1 || value > 1) throw new IOException("无法确认设备的急停状态，请检查设备连接。");
            return value != 0;
        }
        public void RebuildEmergencyInput()
        {
            RequireUsable();
            if(inputRebuilt||!HasVerifiedSessionIdentity)throw new IOException("本次输入任务不能再次重建，请停止并检查连接。");
            inputRebuilt=true;
            // Called only by the sole reader after its second timeout returned
            // and cancellation was latched. No other task/line is changed.
            var failures=new List<Exception>();ClearOwnedTask(ref input,failures);
            if(failures.Count!=0){CleanupPending=true;throw new AggregateException("急停输入释放未完成，保留原句柄。",failures);}
            try{
                VerifySelectedIdentity();Art.Check(native.CreateTask("Calibration_P04",out input));
                if(input==IntPtr.Zero)throw new IOException("急停输入任务重建未完成。");
                Art.Check(native.CreateInput(input,device+"/port0/line4"));Art.Check(native.Start(input));
            }catch{CleanupPending=true;throw;}
        }
        public void WriteShortCable(bool on)
        {
            RequireUsable();
            if (input == IntPtr.Zero) throw new IOException("控制箱未连接。");
            try
            {
                VerifySelectedIdentity();
                if (impedance == IntPtr.Zero)
                {
                    Art.Check(native.CreateTask("Calibration_P12", out impedance));
                    if (impedance == IntPtr.Zero) throw new IOException("阻抗控制连接未建立。");
                    Art.Check(native.CreateOutput(impedance, device + "/port1/line2"));
                    Art.Check(native.Start(impedance));
                }
                int written;
                Art.Check(native.Write(impedance, on ? (byte)1 : (byte)0, out written));
                if (written != 1) throw new IOException("阻抗切换未确认完成。");
            }
            catch { CleanupPending = true; throw; }
        }
        void RequireUsable()
        {
            if (CleanupPending) throw new InvalidOperationException("控制箱资源尚待释放，当前禁止校准。");
        }
        public void Close()
        {
            CleanupPending = true;
            var errors = new List<Exception>();
            ClearOwnedTask(ref impedance, errors);
            ClearOwnedTask(ref input, errors);
            if (errors.Count != 0) throw new AggregateException("控制箱资源释放未完成，请重试释放。", errors);
            CleanupPending = false;
            HasVerifiedSessionIdentity=false;
        }
        void ClearOwnedTask(ref IntPtr task, List<Exception> errors)
        {
            if (task == IntPtr.Zero) return;
            try
            {
                Art.Check(native.Clear(task));
                // Never discard the only ownership record before the SDK confirms release.
                task = IntPtr.Zero;
            }
            catch (Exception error) { errors.Add(error); }
        }
    }

    // A narrow injected boundary permits ownership regression tests without loading a driver.
    internal interface IArtControlBoxNative
    {
        void Load(string directory);
        int ReadDeviceSerial(string device, out uint serial);
        int CreateTask(string name, out IntPtr task);
        int CreateInput(IntPtr task, string lines);
        int CreateOutput(IntPtr task, string lines);
        int Start(IntPtr task);
        int Clear(IntPtr task);
        int Read(IntPtr task, out byte value, out int read, out int bytes);
        int Write(IntPtr task, byte value, out int written);
    }
    internal interface IArtTimedEmergencyNative
    { int Read(IntPtr task,double timeoutSeconds,out byte value,out int read,out int bytes); }
    internal sealed class ArtControlBoxNative : IArtControlBoxNative, IArtTimedEmergencyNative
    {
        public void Load(string directory) { Art.Load(directory); }
        public int ReadDeviceSerial(string device, out uint serial) { return Art.GetDeviceSerial(device, 0x0632, out serial, IntPtr.Zero); }
        public int CreateTask(string name, out IntPtr task) { return Art.CreateTask(name, out task); }
        public int CreateInput(IntPtr task, string lines) { return Art.CreateDI(task, lines, "", 0); }
        public int CreateOutput(IntPtr task, string lines) { return Art.CreateDO(task, lines, "", 0); }
        public int Start(IntPtr task) { return Art.Start(task); }
        public int Clear(IntPtr task) { return Art.Clear(task); }
        public int Read(IntPtr task, out byte value, out int read, out int bytes)
        {return Read(task,CalibrationEmergencyMonitor.FirstReadTimeoutMs/1000.0,out value,out read,out bytes);}
        public int Read(IntPtr task,double timeoutSeconds,out byte value,out int read,out int bytes)
        {
            var data = new byte[1];
            int status = Art.Read(task, 1, timeoutSeconds, 0, data, 1, out read, out bytes, IntPtr.Zero);
            value = data[0]; return status;
        }
        public int Write(IntPtr task, byte value, out int written)
        { return Art.Write(task, 1, 0, .02, 0, new[] { value }, out written, IntPtr.Zero); }
    }

    internal static class Art
    {
        static readonly object loadSync = new object();
        static string loadedPath;
        internal static void Load(string directory)
        {
            lock (loadSync)
            {
            string path = Path.GetFullPath(Path.Combine(directory, "Art_DAQ.dll"));
            if (loadedPath != null)
            {
                if (!String.Equals(path, loadedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("ART runtime is already configured; restart before selecting another version.");
                return;
            }
            if (!File.Exists(path)) throw new FileNotFoundException("需要已安装的 ART-DAQ x64 驱动。", path);
            int status = HvxConfigureArtPathW(path);
            if (status != 0) throw new InvalidOperationException("ART backend configuration failed (bridge " + status + ").");
            loadedPath = path;
            }
        }
        internal static void Check(int status) { if (status < 0) throw new CalibrationArtException(status); }
        // Official Art_DAQ.h uint32 ArtDAQ_Dev_SerialNum; read only, before any task creation.
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_GetDeviceAttribute", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern int GetDeviceSerial(string device, int attribute, out uint serial, IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", CharSet=CharSet.Unicode, ExactSpelling=true, CallingConvention=CallingConvention.StdCall)] static extern int HvxConfigureArtPathW(string path);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CreateTask", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern int CreateTask(string name, out IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CreateDIChan", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern int CreateDI(IntPtr task, string lines, string name, int grouping);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_CreateDOChan", CallingConvention=CallingConvention.StdCall, CharSet=CharSet.Ansi)] internal static extern int CreateDO(IntPtr task, string lines, string name, int grouping);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_StartTask", CallingConvention=CallingConvention.StdCall)] internal static extern int Start(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_ClearTask", CallingConvention=CallingConvention.StdCall)] internal static extern int Clear(IntPtr task);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_ReadDigitalLines", CallingConvention=CallingConvention.StdCall)] internal static extern int Read(IntPtr task, int count, double timeout, int layout, [Out] byte[] data, uint length, out int read, out int bytes, IntPtr reserved);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint="ArtDAQ_WriteDigitalLines", CallingConvention=CallingConvention.StdCall)] internal static extern int Write(IntPtr task, int count, int autoStart, double timeout, int layout, [In] byte[] data, out int written, IntPtr reserved);
    }
}
