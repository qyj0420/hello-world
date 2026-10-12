using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.FactoryPressOwts
{
    /// <summary>
    /// Desktop「加压测试」→ 出厂加压硬件栈桥接（品牌：雄新电力 / 雄新 OWTS）。
    /// 加载 <c>Grok.Pressure.CompileOnly.dll</c>（Contracts + Acquisition + HvPressTest），
    /// 经 <see cref="Xiongxin.Owts.HvPressTest.FactoryPressApi"/> 走完整硬件路径：
    /// DeviceSdkLease、LiveEmergencyMonitor、SessionFault、StatusCopy、
    /// Task6RangeAdvisor、PicoCaptureCooldown、ART DB37（P0.0/P0.3/P0.4/P0.5）。
    /// 无 DLL 时 <see cref="IsAvailable"/> 为 false，不得假装已融合。
    /// </summary>
    public static class OwtsFactoryAlignedHost
    {
        public const string Brand = "雄新电力 / 雄新 OWTS";
        public const string EnvForceAligned = "XIONGXIN_OWTS_FACTORY_ALIGNED";
        const string ApiTypeName = "Xiongxin.Owts.HvPressTest.FactoryPressApi";
        const string ConfigTypeName = "Xiongxin.Owts.HvPressTest.PressTestConfig";

        static readonly object Gate = new object();
        static Assembly _loaded;
        static string _loadError;

        public static bool IsAvailable
        {
            get { EnsureLoaded(); return _loaded != null; }
        }

        public static string LoadError
        {
            get { EnsureLoaded(); return _loadError; }
        }

        public static bool PreferAlignedPath
        {
            get
            {
                string v = Environment.GetEnvironmentVariable(EnvForceAligned);
                if (string.Equals(v, "0", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase))
                    return false;
                return IsAvailable;
            }
        }

        static void EnsureLoaded()
        {
            if (_loaded != null || _loadError != null) return;
            lock (Gate)
            {
                if (_loaded != null || _loadError != null) return;
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                    string[] candidates =
                    {
                        Path.Combine(baseDir, "modules", "FactoryPressOwts", "Grok.Pressure.CompileOnly.dll"),
                        Path.Combine(baseDir, "modules", "PressureRuntime", "Grok.Pressure.CompileOnly.dll"),
                        Path.Combine(baseDir, "Grok.Pressure.CompileOnly.dll"),
                    };
                    foreach (string path in candidates)
                    {
                        if (!File.Exists(path)) continue;
                        Assembly asm = Assembly.LoadFrom(path);
                        if (asm.GetType(ApiTypeName, false) != null)
                        {
                            _loaded = asm;
                            return;
                        }
                    }
                    _loadError = "未找到 Grok.Pressure.CompileOnly.dll（含 FactoryPressApi）。"
                        + "Windows：tools\\Build-GrokPressCompileOnly.ps1 -OutputRoot build\\factory-press-owts"
                        + " 后 tools\\Stage-FactoryPressOwts.ps1。";
                }
                catch (Exception ex)
                {
                    _loadError = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Connect → Calibrate（急停按下）→ Press（急停解除 + Task6RangeAdvisor）→ Release。
        /// FakeLive 可在无驱动箱冒烟；Live 须 Windows + Art_DAQ + ps5000a。
        /// </summary>
        public static Task<string> CaptureAlignedAsync(
            double targetKv,
            string outputDirectory,
            bool useFakeLive,
            double voltageGain,
            Action<string> log,
            CancellationToken token)
        {
            if (!IsAvailable)
                throw new InvalidOperationException(_loadError ?? "Owts factory-aligned host unavailable.");

            return Task.Run(() =>
            {
                Directory.CreateDirectory(outputDirectory);
                Type apiType = _loaded.GetType(ApiTypeName, true);
                Type cfgType = _loaded.GetType(ConfigTypeName, true);
                object api = Activator.CreateInstance(apiType, new object[] { new CallbackWriter(log) });
                try
                {
                    object cfg = Activator.CreateInstance(cfgType);
                    Set(cfg, "Mode", useFakeLive ? "FakeLive" : "Live");
                    Set(cfg, "VoltageGainVPerAoVolt", voltageGain > 0 ? voltageGain : 6000.0);
                    Set(cfg, "DividerRatio", 12000.0);
                    Set(cfg, "OutputDirectory", outputDirectory);
                    Set(cfg, "TargetsKv", new System.Collections.Generic.List<double> { targetKv });
                    Set(cfg, "MaxRangeRetries", 2);
                    Set(cfg, "InitialPicoRangeIndex", 6);
                    Set(cfg, "RunCalibrationFirst", true);
                    Set(cfg, "AutoRangeAdjust", true);

                    apiType.GetMethod("Connect").Invoke(api, new object[] { cfg });
                    Log(log, Brand + " FactoryPressApi.Connect OK mode=" + (useFakeLive ? "FakeLive" : "Live"));

                    // 校准：StatusCopy 要求急停按下
                    MethodInfo calib = apiType.GetMethod("Calibrate", new Type[] { typeof(CancellationToken) });
                    int calibExit = (int)calib.Invoke(api, new object[] { token });
                    Log(log, "Calibrate exit=" + calibExit + "（急停按下=可校准/AO0；解除后方可非零加压；0kV≠可加压）");

                    MethodInfo press = apiType.GetMethod("Press", new Type[] {
                        typeof(System.Collections.Generic.IList<double>), typeof(CancellationToken) });
                    int pressExit = (int)press.Invoke(api, new object[] { null, token });
                    Log(log, "Press exit=" + pressExit + "（Task6RangeAdvisor / PicoCooldown / HV OFF+Pico 同步）");

                    string runDir = Convert.ToString(apiType.GetProperty("RunDirectory").GetValue(api, null));
                    File.WriteAllText(Path.Combine(outputDirectory, "owts-factory-aligned-result.txt"),
                        "brand=" + Brand + "\r\n"
                        + "targetKv=" + targetKv.ToString("R", CultureInfo.InvariantCulture) + "\r\n"
                        + "fakeLive=" + useFakeLive + "\r\n"
                        + "calibExit=" + calibExit + "\r\n"
                        + "pressExit=" + pressExit + "\r\n"
                        + "runDirectory=" + runDir + "\r\n"
                        + "api=FactoryPressApi\r\n"
                        + "stack=DeviceSdkLease|LiveEmergencyMonitor|SessionFault|StatusCopy|Task6RangeAdvisor\r\n");
                    if (pressExit != 0)
                        throw new InvalidOperationException("FactoryPressApi.Press 失败 exit=" + pressExit);
                    return string.IsNullOrEmpty(runDir) ? outputDirectory : runDir;
                }
                finally
                {
                    try { apiType.GetMethod("Release").Invoke(api, null); }
                    catch (Exception ex) { Log(log, "Release: " + ex.Message); }
                    IDisposable d = api as IDisposable;
                    if (d != null) d.Dispose();
                }
            }, token);
        }

        static void Set(object obj, string name, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            else
            {
                FieldInfo f = obj.GetType().GetField(name);
                if (f != null) f.SetValue(obj, value);
            }
        }

        static void Log(Action<string> log, string line)
        {
            if (log != null) log(line);
        }

        sealed class CallbackWriter : TextWriter
        {
            readonly Action<string> _log;
            public CallbackWriter(Action<string> log) { _log = log; }
            public override System.Text.Encoding Encoding { get { return System.Text.Encoding.UTF8; } }
            public override void WriteLine(string value) { if (_log != null) _log(value ?? ""); }
            public override void Write(string value) { /* line-oriented */ }
        }
    }
}
