using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
#if FACTORY_PRESS_GUI || NET8_0_WINDOWS
using System.Windows.Forms;
#endif

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 雄新电力 · 雄新 OWTS 出厂加压测试入口。
    /// Windows（net8.0-windows）：无参双击开 GUI；--smoke/--config/--help 走控制台。
    /// Linux（net8.0）：仅控制台。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
#if FACTORY_PRESS_GUI || NET8_0_WINDOWS
            if (!WantsConsoleMode(args))
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogFatal("UI", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                    LogFatal("Domain", e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
                if (WantsCalibrationUi(args))
                    Application.Run(new FactoryCalibrationForm());
                else
                    Application.Run(new FactoryPressForm());
                return 0;
            }
            EnsureConsole();
#endif
            return RunConsole(args ?? Array.Empty<string>());
        }

        static void LogFatal(string kind, Exception ex)
        {
            try
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string dir = Path.Combine(desk, "hv-press-out");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
                string body = "[" + DateTime.Now.ToString("o") + "] " + kind + "\r\n"
                    + (ex != null ? (ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.StackTrace) : "(null)")
                    + "\r\n";
                File.AppendAllText(path, body);
                try { System.Diagnostics.Trace.WriteLine(body); } catch { }
            }
            catch { /* never throw from fatal logger */ }
        }

        /// <summary>
        /// 独立校准采集 UI：--calibrate-ui / --calib-ui，或 exe 名含 Calibration / 校准采集。
        /// </summary>
        static bool WantsCalibrationUi(string[] args)
        {
            if (HasFlag(args, "--calibrate-ui") || HasFlag(args, "--calib-ui")
                || HasFlag(args, "--calibration-ui"))
                return true;
            try
            {
                string name = Path.GetFileNameWithoutExtension(
                    Environment.GetCommandLineArgs() != null && Environment.GetCommandLineArgs().Length > 0
                        ? Environment.GetCommandLineArgs()[0]
                        : (System.Diagnostics.Process.GetCurrentProcess().MainModule != null
                            ? System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName
                            : ""));
                if (string.IsNullOrEmpty(name)) return false;
                if (name.IndexOf("Calibration", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("校准采集", StringComparison.Ordinal) >= 0) return true;
            }
            catch { }
            return false;
        }

        static bool WantsConsoleMode(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            if (HasFlag(args, "--gui")) return false;
            if (HasFlag(args, "--console")) return true;
            if (HasFlag(args, "--smoke") || HasFlag(args, "--help") || HasFlag(args, "-h")
                || HasFlag(args, "help") || HasFlag(args, "--config") || HasFlag(args, "-c")
                || HasFlag(args, "--live") || HasFlag(args, "--fake")
                || HasFlag(args, "--calibrate") || HasFlag(args, "--no-calibrate")
                || HasFlag(args, "--gain") || HasFlag(args, "--out") || HasFlag(args, "--bench"))
                return true;
            if (File.Exists(args[0])) return true;
            return false;
        }

#if FACTORY_PRESS_GUI || NET8_0_WINDOWS
        static void EnsureConsole()
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS) == 0)
                AllocConsole();
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        }

        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();
#endif

        static int RunConsole(string[] args)
        {
            try
            {
                PrintBanner();

                if (HasFlag(args, "--help") || HasFlag(args, "-h")
                    || (args.Length > 0 && (string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))))
                {
                    PrintHelp();
                    return 0;
                }

                PressTestConfig config;
                if (HasFlag(args, "--smoke"))
                {
                    config = BuildSmokeConfig(args);
                }
                else if (HasFlag(args, "--config") || HasFlag(args, "-c"))
                {
                    string path = FlagValue(args, "--config") ?? FlagValue(args, "-c");
                    if (string.IsNullOrEmpty(path))
                    {
                        Console.Error.WriteLine("缺少配置路径。");
                        PrintHelp();
                        return 1;
                    }
                    config = PressTestConfig.Load(path);
                }
                else if (args.Length > 0 && File.Exists(args[0]))
                {
                    config = PressTestConfig.Load(args[0]);
                }
                else
                {
                    string sample = FindSampleConfig();
                    if (sample != null)
                    {
                        Console.WriteLine("使用样例配置: " + sample);
                        config = PressTestConfig.Load(sample);
                    }
                    else
                    {
                        Console.WriteLine("未找到配置，使用内置 FakeLive smoke 列表 [1,2] kV。");
                        config = PressTestConfig.CreateSmokeDefaults();
                    }
                }

                ApplyCliOverrides(args, config);

                using (var api = new FactoryPressApi(Console.Out))
                {
                    api.Connect(config);
                    int code = 0;
                    if (config.RunCalibrationFirst)
                    {
                        code = api.Calibrate();
                        if (code != 0)
                        {
                            Console.Error.WriteLine("FAIL calibrate exit=" + code
                                + " aborted=" + api.AbortedBySafety
                                + " reason=" + (api.AbortReason ?? ""));
                            int rel = api.Release();
                            return code != 0 ? code : rel;
                        }
                    }
                    code = api.Press();
                    int completed = api.CompletedSteps;
                    string csv = api.CsvPath;
                    bool aborted = api.AbortedBySafety;
                    string reason = api.AbortReason;
                    int releaseHint = api.Release();
                    if (code == 0 && releaseHint != 0) code = releaseHint;
                    if (code == 0)
                        Console.WriteLine("OK exit=0 completed=" + completed + " csv=" + csv);
                    else
                        Console.Error.WriteLine("FAIL exit=" + code
                            + " aborted=" + aborted
                            + " reason=" + (reason ?? ""));
                    return code;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("出厂加压测试 异常: " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 99;
            }
        }

        static void ApplyCliOverrides(string[] args, PressTestConfig config)
        {
            string outDir = FlagValue(args, "--out");
            if (!string.IsNullOrEmpty(outDir))
                config.OutputDirectory = Path.GetFullPath(outDir);

            if (HasFlag(args, "--live"))
                config.Mode = "Live";
            if (HasFlag(args, "--fake") || HasFlag(args, "--smoke"))
                config.Mode = "FakeLive";

            if (HasFlag(args, "--calibrate") || HasFlag(args, "--cal"))
                config.RunCalibrationFirst = true;
            if (HasFlag(args, "--no-calibrate") || HasFlag(args, "--no-cal"))
                config.RunCalibrationFirst = false;

            string gain = FlagValue(args, "--gain");
            if (!string.IsNullOrEmpty(gain))
            {
                double g;
                if (!double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out g) || g <= 0)
                    throw new FormatException("--gain 须为正数，默认台架 6000。");
                if (Math.Abs(g - 1000.0) < 1e-9)
                    throw new InvalidOperationException("--gain=1000 为危险占位，禁止；请用 6000（AO5V→30kV）或台架实测值。");
                config.VoltageGainVPerAoVolt = g;
            }

            string bench = FlagValue(args, "--bench");
            if (!string.IsNullOrEmpty(bench))
                config.BenchCalibrationFile = Path.GetFullPath(bench);

            if (HasFlag(args, "--impedance-wide"))
                config.ImpedanceBandwidthWide = true;
            if (HasFlag(args, "--impedance-narrow"))
                config.ImpedanceBandwidthWide = false;

            if (HasFlag(args, "--no-suppress-pico-splash"))
                config.SuppressPicoStartupWindow = false;
        }

        static PressTestConfig BuildSmokeConfig(string[] args)
        {
            string sample = FindSampleConfig();
            PressTestConfig config;
            if (sample != null)
            {
                config = PressTestConfig.Load(sample);
                Console.WriteLine("Smoke 使用样例: " + sample);
            }
            else
            {
                config = PressTestConfig.CreateSmokeDefaults();
                Console.WriteLine("Smoke 使用内置默认列表。");
            }
            config.Mode = "FakeLive";
            config.RunCalibrationFirst = true;
            config.SuppressPicoStartupWindow = true;
            if (string.IsNullOrWhiteSpace(config.OutputDirectory)
                || config.OutputDirectory.IndexOf("smoke", StringComparison.OrdinalIgnoreCase) < 0)
            {
                config.OutputDirectory = Path.Combine(
                    AppContext.BaseDirectory, "out", "smoke-run");
            }
            return config;
        }

        static string FindSampleConfig()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "config", "press-list.sample.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "config", "press-list.sample.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "Xiongxin.Owts.HvPressTest", "config", "press-list.sample.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config", "press-list.sample.json")
            };
            foreach (string c in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(c);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        static bool HasFlag(string[] args, string name)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string FlagValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        static void PrintBanner()
        {
            Console.WriteLine();
            Console.WriteLine("  雄新电力 · 雄新 OWTS 出厂加压测试");
            Console.WriteLine("  Windows 双击开 GUI · 控制台：--smoke / --config");
            Console.WriteLine();
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"Xiongxin.Owts.HvPressTest — 雄新电力 · 雄新 OWTS 出厂加压测试

Windows 无参数双击：打开 WinForms（连接/校准/开始加压/停止/释放会话）
  增益默认 2000；FakeLive|Live；五态：已连接/已校准/可加压/急停按下/急停解除

控制台:
  --smoke --gain 2000
  --config config/press-list.sample.json
  --live --config config/press-list.live.sample.json
  --help
");
        }
    }
}
