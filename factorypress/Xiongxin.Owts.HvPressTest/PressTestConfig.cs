using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Acquisition.Live;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>
    /// 加压测试配置。台架标定（TEST_BENCH_ONLY）：
    /// AO/直流源监控 0–5V→0–30kV（Gain/SourceScale=6000）；电流 0–5V→0–10mA（0.002 A/V）；分压 12000:1。
    /// 明确绕过「1000 危险默认」门；不得当作现网生产增益。
    /// </summary>
    public sealed class PressTestConfig
    {
        public const double TestBenchVoltageGain = 6000.0;
        /// <summary>分压比：反馈 1 V 对应 12000 V。</summary>
        public const double TestBenchDividerRatio = 12000.0;
        /// <summary>电流监控：0–5 V → 0–10 mA ⇒ 0.002 A/V（2 mA/V）。</summary>
        public const double TestBenchCurrentScaleAPerVolt = 0.002;
        /// <summary>直流源监控：0–5 V → 0–30 kV ⇒ ×6000。</summary>
        public const double TestBenchSourceVoltageScale = 6000.0;
        /// <summary>硬过流阈值（A）；略高于满量程 10 mA。</summary>
        public const double TestBenchHardOverCurrentA = 0.012;
        public const string TestBenchGainNote =
            "TEST_BENCH_ONLY Gain/AI2=6000 (0–5V→0–30kV); I=0.002A/V (0–5V→0–10mA); Divider=12000:1 — 非现网壳默认。";

        /// <summary>FakeLive | Live</summary>
        public string Mode { get; set; }
        public string BenchCalibrationFile { get; set; }
        public double InductanceH { get; set; }
        public double InductorDcrOhm { get; set; }
        /// <summary>pC/V；≤0 或未配 → 放电量 NotAvailable（不伪造）。</summary>
        public double PdCalibrationPcPerVolt { get; set; }
        /// <summary>校准器注入放电量（pC）；用于系数=注入/峰压。默认 100。</summary>
        public double InjectedChargePc { get; set; }
        public int MaxRangeRetries { get; set; }
        /// <summary>true=自动量程（达压后按 Task6 升/降档并重加本轮）；false=手动量程（不自动切，仅提示超量程或峰&lt;20%FS）。</summary>
        public bool AutoRangeAdjust { get; set; }
        public int InitialPicoRangeIndex { get; set; }
        /// <summary>Pico 分辨率位数；默认 14（亦可 15）。</summary>
        public int PicoResolutionBits { get; set; }
        /// <summary>目标采样率 Hz；默认 125e6。界面可选。</summary>
        public double PicoTargetSampleRateHz { get; set; }
        /// <summary>Pico timebase；与目标采样率联动，默认 3≈8ns@125MS/s。</summary>
        public uint PicoTimebase { get; set; }
        /// <summary>触发位置 0–100（%）；0=窗起点，50=50%，默认 0。</summary>
        public int PicoTriggerPositionPercent { get; set; }
        /// <summary>触发采集振荡/局放长度（ms）；写入 Pico/ART 实际采集长度。默认 60。</summary>
        public double OscillationCaptureDurationMs { get; set; }
        public int RampStepDelayMilliseconds { get; set; }
        /// <summary>AO 每步伏特（RampStepAoVolts）；默认 0.01（Gain=6000→工程≈60V/步）。≤0 则回退按 60V/Gain 估算。</summary>
        public double RampStepAoVolts { get; set; }
        public string OutputDirectory { get; set; }
        public List<double> TargetsKv { get; set; }
        /// <summary>配置给出的接头位置（米）；本程序不做测距，仅透传；无则 Joint=NotAvailable。</summary>
        public List<double> JointPositionsM { get; set; }
        public string ArtDeviceName { get; set; }
        public string PicoSerial { get; set; }
        /// <summary>true（默认）= Live OpenUnit 前抑制 Pico 官方启动窗；仅 SDK。</summary>
        public bool SuppressPicoStartupWindow { get; set; }
        /// <summary>连续加压前跑校准态：0 kV Pico 采集；阻抗可调；急停可切换（FakeLive 演示）。</summary>
        public bool RunCalibrationFirst { get; set; }
        /// <summary>覆盖台架 VoltageGain；NaN=用文件或默认 6000。</summary>
        public double VoltageGainVPerAoVolt { get; set; }
        public double DividerRatioVPerV { get; set; }
        public double CurrentScaleAPerVolt { get; set; }
        public double SourceVoltageScaleVPerV { get; set; }
        /// <summary>校准态结束后假阻抗档（宽=true）。</summary>
        public bool ImpedanceBandwidthWide { get; set; }
        /// <summary>达压有效窗口比例，默认 0.05（±5%）。</summary>
        public double TargetReachedToleranceFraction { get; set; }
        /// <summary>异常过压比例，默认 0.20（+20%）；夹到 (0.05,0.50]。</summary>
        public double OverVoltageTripFraction { get; set; }
        /// <summary>HV ON 后 AO=0 背景限（V），默认 500。</summary>
        public double ZeroBackgroundLimitV { get; set; }
        /// <summary>P0.4 单次读超时上限 ms，默认 300。</summary>
        public int EStopReadTimeoutMilliseconds { get; set; }
        /// <summary>P0.4 请求节拍 ms，默认 10。</summary>
        public int EStopPollIntervalMilliseconds { get; set; }
        /// <summary>成功档后冷却 ms，默认 2000；Stop/急停/未知/断线撤销批次须显式重开。</summary>
        public int InterRoundCooldownMilliseconds { get; set; }
        /// <summary>高通线性滤波（默认开）；左窗检波 + 右窗定位原始局放（均滤波后）；仅量程调节参考用未滤波 Pico。</summary>
        public bool HighPassLinearFilterEnabled { get; set; }
        /// <summary>高通低截止 Hz，默认 10000。</summary>
        public double HighPassCutoffHz { get; set; }
        /// <summary>定位：相位起 N 周期，默认 2。</summary>
        public int LocateCycleCount { get; set; }
        /// <summary>校准采集：Rapid 脉冲平均次数 N（筛选后取至多 N 段按点平均），默认 8。</summary>
        public int PulseAverageCount { get; set; }
        /// <summary>校准采集电缆长度 L（m）；时序 pre=0.5×L/v。</summary>
        public double CableLengthM { get; set; }
        /// <summary>波速 v（m/µs）；默认对齐 ConnectingCableGeometry 150。</summary>
        public double VelocityMPerUs { get; set; }
        /// <summary>固定间隔容差（相对标称周期），默认 0.35=±35%；适配 50/100/400Hz，主要剔噪声。</summary>
        public double PeriodToleranceFraction { get; set; }
        /// <summary>显式标称周期 ns；≤0/NaN=在 50/100/400Hz 与实测中位间自适应。</summary>
        public double ExpectedPeriodNanoseconds { get; set; }

        public PressTestConfig()
        {
            Mode = "FakeLive";
            BenchCalibrationFile = string.Empty;
            InductanceH = 1.15;
            InductorDcrOhm = 0;
            PdCalibrationPcPerVolt = double.NaN;
            InjectedChargePc = PdCalibrationStore.DefaultInjectedChargePc;
            MaxRangeRetries = 2;
            AutoRangeAdjust = true;
            InitialPicoRangeIndex = 6; // ±1 V 初始（自动量程）
            PicoResolutionBits = 14;
            PicoTargetSampleRateHz = 125e6;
            PicoTimebase = 3; // 8 ns → 125 MS/s @14/15bit
            PicoTriggerPositionPercent = 0;
            OscillationCaptureDurationMs = LiveHardwareOptions.OscillationCaptureDurationMilliseconds;
            RampStepDelayMilliseconds = 1;
            RampStepAoVolts = 0.01;
            OutputDirectory = "out/hv-press";
            TargetsKv = new List<double>();
            JointPositionsM = null;
            ArtDeviceName = "Dev1";
            PicoSerial = string.Empty;
            SuppressPicoStartupWindow = true;
            RunCalibrationFirst = true;
            VoltageGainVPerAoVolt = double.NaN; // → 默认 6000
            DividerRatioVPerV = TestBenchDividerRatio;
            CurrentScaleAPerVolt = TestBenchCurrentScaleAPerVolt;
            SourceVoltageScaleVPerV = double.NaN; // → 默认 6000
            ImpedanceBandwidthWide = false;
            HighPassLinearFilterEnabled = true;
            HighPassCutoffHz = 10000;
            LocateCycleCount = 2;
            PulseAverageCount = 8;
            CableLengthM = 100.0;
            VelocityMPerUs = 150.0;
            PeriodToleranceFraction = 0.35; // ±35%，适配 50/100/400Hz，主要剔噪声
            ExpectedPeriodNanoseconds = double.NaN;
            TargetReachedToleranceFraction = 0.05;
            OverVoltageTripFraction = 0.20;
            ZeroBackgroundLimitV = 500.0;
            EStopReadTimeoutMilliseconds = 300;
            EStopPollIntervalMilliseconds = 10;
            InterRoundCooldownMilliseconds = 1000;
        }

        public bool IsFakeLive
        {
            get { return string.Equals(Mode, "FakeLive", StringComparison.OrdinalIgnoreCase); }
        }

        public bool HasPdCalibration
        {
            get
            {
                return !double.IsNaN(PdCalibrationPcPerVolt)
                    && !double.IsInfinity(PdCalibrationPcPerVolt)
                    && PdCalibrationPcPerVolt > 0;
            }
        }

        public static PressTestConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("配置路径为空。", "path");
            string full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException("加压配置不存在：" + full, full);
            string text = File.ReadAllText(full, Encoding.UTF8);
            string ext = Path.GetExtension(full);
            if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".list", StringComparison.OrdinalIgnoreCase))
                return LoadTargetsText(text, full);
            return ParseJson(text, Path.GetDirectoryName(full));
        }

        /// <summary>仅含目标 kV 的文本列表；其余用默认（含 TestBench 2000）。</summary>
        public static PressTestConfig LoadTargetsText(string text, string sourcePath = null)
        {
            var cfg = CreateSmokeDefaults();
            cfg.TargetsKv = new List<double>();
            if (!string.IsNullOrEmpty(sourcePath))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
                string bench = Path.Combine(dir ?? ".", "bench-cal-test-6000.json");
                if (File.Exists(bench)) cfg.BenchCalibrationFile = bench;
            }
            foreach (string raw in (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("//", StringComparison.Ordinal))
                    continue;
                double kv;
                if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out kv))
                    throw new FormatException("加压列表行无法解析为 kV：" + line);
                if (double.IsNaN(kv) || double.IsInfinity(kv) || kv < 0)
                    throw new FormatException("加压目标非法：" + line);
                cfg.TargetsKv.Add(kv);
            }
            if (cfg.TargetsKv.Count == 0)
                throw new InvalidOperationException("加压列表为空。");
            return cfg;
        }

        public static PressTestConfig CreateSmokeDefaults()
        {
            return new PressTestConfig
            {
                Mode = "FakeLive",
                InductanceH = 1.15,
                InductorDcrOhm = 0,
                PdCalibrationPcPerVolt = double.NaN,
                MaxRangeRetries = 2,
                AutoRangeAdjust = true,
                InitialPicoRangeIndex = 6,
                RampStepDelayMilliseconds = 1,
                RampStepAoVolts = 0.01,
                OutputDirectory = "out/hv-press-smoke",
                TargetsKv = new List<double> { 1.0, 2.0 }
            };
        }

        public static PressTestConfig ParseJson(string json, string baseDir = null)
        {
            var cfg = CreateSmokeDefaults();
            if (string.IsNullOrWhiteSpace(json)) return cfg;

            cfg.Mode = ExtractString(json, "mode") ?? cfg.Mode;
            string bench = ExtractString(json, "benchCalibrationFile");
            if (!string.IsNullOrEmpty(bench))
            {
                cfg.BenchCalibrationFile = ResolvePath(bench, baseDir);
            }
            cfg.InductanceH = ExtractDouble(json, "inductanceH", cfg.InductanceH);
            cfg.InductorDcrOhm = ExtractDouble(json, "inductorDcrOhm", cfg.InductorDcrOhm);
            if (HasKey(json, "pdCalibrationPcPerVolt"))
            {
                if (IsJsonNull(json, "pdCalibrationPcPerVolt"))
                    cfg.PdCalibrationPcPerVolt = double.NaN;
                else
                    cfg.PdCalibrationPcPerVolt = ExtractDouble(json, "pdCalibrationPcPerVolt", double.NaN);
            }
            if (HasKey(json, "injectedChargePc") && !IsJsonNull(json, "injectedChargePc"))
                cfg.InjectedChargePc = ExtractDouble(json, "injectedChargePc", cfg.InjectedChargePc);
            cfg.MaxRangeRetries = (int)ExtractDouble(json, "maxRangeRetries", cfg.MaxRangeRetries);
            if (HasKey(json, "autoRangeAdjust"))
                cfg.AutoRangeAdjust = ExtractBool(json, "autoRangeAdjust", cfg.AutoRangeAdjust);
            cfg.InitialPicoRangeIndex = (int)ExtractDouble(json, "initialPicoRangeIndex", cfg.InitialPicoRangeIndex);
            cfg.PicoResolutionBits = (int)ExtractDouble(json, "picoResolutionBits", cfg.PicoResolutionBits);
            cfg.PicoTargetSampleRateHz = ExtractDouble(json, "picoTargetSampleRateHz", cfg.PicoTargetSampleRateHz);
            if (cfg.PicoTargetSampleRateHz > 0)
                cfg.PicoTimebase = ResolvePicoTimebase(cfg.PicoTargetSampleRateHz);
            uint tb = (uint)ExtractDouble(json, "picoTimebase", cfg.PicoTimebase);
            if (tb > 0) cfg.PicoTimebase = tb;
            if (HasKey(json, "picoTriggerPositionPercent"))
                cfg.PicoTriggerPositionPercent = (int)ExtractDouble(json, "picoTriggerPositionPercent", cfg.PicoTriggerPositionPercent);
            if (HasKey(json, "oscillationCaptureDurationMs") && !IsJsonNull(json, "oscillationCaptureDurationMs"))
                cfg.OscillationCaptureDurationMs = LiveHardwareOptions.ClampOscillationCaptureDurationMs(
                    ExtractDouble(json, "oscillationCaptureDurationMs", cfg.OscillationCaptureDurationMs));
            cfg.RampStepDelayMilliseconds = (int)ExtractDouble(json, "rampStepDelayMilliseconds", cfg.RampStepDelayMilliseconds);
            cfg.RampStepAoVolts = ExtractDouble(json, "rampStepAoVolts", cfg.RampStepAoVolts);
            string outDir = ExtractString(json, "outputDirectory");
            if (!string.IsNullOrEmpty(outDir))
                cfg.OutputDirectory = ResolvePath(outDir, baseDir);
            string art = ExtractString(json, "artDeviceName");
            if (!string.IsNullOrEmpty(art)) cfg.ArtDeviceName = art;
            string pico = ExtractString(json, "picoSerial");
            if (pico != null) cfg.PicoSerial = pico;
            if (HasKey(json, "suppressPicoStartupWindow"))
            {
                string raw = ExtractBoolToken(json, "suppressPicoStartupWindow");
                if (raw != null)
                    cfg.SuppressPicoStartupWindow = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            if (HasKey(json, "runCalibrationFirst"))
            {
                string raw = ExtractBoolToken(json, "runCalibrationFirst");
                if (raw != null)
                    cfg.RunCalibrationFirst = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            if (HasKey(json, "impedanceBandwidthWide"))
            {
                string raw = ExtractBoolToken(json, "impedanceBandwidthWide");
                if (raw != null)
                    cfg.ImpedanceBandwidthWide = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
            }
            if (HasKey(json, "voltageGainVPerAoVolt") && !IsJsonNull(json, "voltageGainVPerAoVolt"))
                cfg.VoltageGainVPerAoVolt = ExtractDouble(json, "voltageGainVPerAoVolt", cfg.VoltageGainVPerAoVolt);
            if (HasKey(json, "targetReachedToleranceFraction") && !IsJsonNull(json, "targetReachedToleranceFraction"))
                cfg.TargetReachedToleranceFraction = ExtractDouble(json, "targetReachedToleranceFraction", cfg.TargetReachedToleranceFraction);
            if (HasKey(json, "overVoltageTripFraction") && !IsJsonNull(json, "overVoltageTripFraction"))
                cfg.OverVoltageTripFraction = ExtractDouble(json, "overVoltageTripFraction", cfg.OverVoltageTripFraction);
            if (HasKey(json, "zeroBackgroundLimitV") && !IsJsonNull(json, "zeroBackgroundLimitV"))
                cfg.ZeroBackgroundLimitV = ExtractDouble(json, "zeroBackgroundLimitV", cfg.ZeroBackgroundLimitV);
            if (HasKey(json, "eStopReadTimeoutMilliseconds"))
                cfg.EStopReadTimeoutMilliseconds = (int)ExtractDouble(json, "eStopReadTimeoutMilliseconds", cfg.EStopReadTimeoutMilliseconds);
            if (HasKey(json, "eStopPollIntervalMilliseconds"))
                cfg.EStopPollIntervalMilliseconds = (int)ExtractDouble(json, "eStopPollIntervalMilliseconds", cfg.EStopPollIntervalMilliseconds);
            if (HasKey(json, "interRoundCooldownMilliseconds"))
                cfg.InterRoundCooldownMilliseconds = (int)ExtractDouble(json, "interRoundCooldownMilliseconds", cfg.InterRoundCooldownMilliseconds);
            if (HasKey(json, "pulseAverageCount"))
                cfg.PulseAverageCount = (int)ExtractDouble(json, "pulseAverageCount", cfg.PulseAverageCount);
            if (HasKey(json, "cableLengthM") && !IsJsonNull(json, "cableLengthM"))
                cfg.CableLengthM = ExtractDouble(json, "cableLengthM", cfg.CableLengthM);
            if (HasKey(json, "velocityMPerUs") && !IsJsonNull(json, "velocityMPerUs"))
                cfg.VelocityMPerUs = ExtractDouble(json, "velocityMPerUs", cfg.VelocityMPerUs);
            if (HasKey(json, "periodToleranceFraction") && !IsJsonNull(json, "periodToleranceFraction"))
                cfg.PeriodToleranceFraction = ExtractDouble(json, "periodToleranceFraction", cfg.PeriodToleranceFraction);
            if (HasKey(json, "expectedPeriodNanoseconds") && !IsJsonNull(json, "expectedPeriodNanoseconds"))
                cfg.ExpectedPeriodNanoseconds = ExtractDouble(json, "expectedPeriodNanoseconds", cfg.ExpectedPeriodNanoseconds);
            else if (HasKey(json, "expectedPulseFrequencyHz") && !IsJsonNull(json, "expectedPulseFrequencyHz"))
            {
                double hz = ExtractDouble(json, "expectedPulseFrequencyHz", 0);
                if (hz > 0) cfg.ExpectedPeriodNanoseconds = 1e9 / hz;
            }
            if (HasKey(json, "dividerRatioVPerV") && !IsJsonNull(json, "dividerRatioVPerV"))
                cfg.DividerRatioVPerV = ExtractDouble(json, "dividerRatioVPerV", cfg.DividerRatioVPerV);
            if (HasKey(json, "currentScaleAPerVolt") && !IsJsonNull(json, "currentScaleAPerVolt"))
                cfg.CurrentScaleAPerVolt = ExtractDouble(json, "currentScaleAPerVolt", cfg.CurrentScaleAPerVolt);
            if (HasKey(json, "sourceVoltageScaleVPerV") && !IsJsonNull(json, "sourceVoltageScaleVPerV"))
                cfg.SourceVoltageScaleVPerV = ExtractDouble(json, "sourceVoltageScaleVPerV", cfg.SourceVoltageScaleVPerV);

            cfg.TargetsKv = ExtractDoubleArray(json, "targetsKv");
            if (cfg.TargetsKv == null || cfg.TargetsKv.Count == 0)
                throw new InvalidOperationException("配置缺少 targetsKv 加压列表。");

            if (HasKey(json, "jointPositionsM") && !IsJsonNull(json, "jointPositionsM"))
                cfg.JointPositionsM = ExtractDoubleArray(json, "jointPositionsM");
            else
                cfg.JointPositionsM = null;

            return cfg;
        }

        static string ResolvePath(string path, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string root = string.IsNullOrEmpty(baseDir) ? Directory.GetCurrentDirectory() : baseDir;
            return Path.GetFullPath(Path.Combine(root, path));
        }

        static bool HasKey(string json, string name)
        {
            return json.IndexOf("\"" + name + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsJsonNull(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return false;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            return j + 4 <= json.Length
                && string.Compare(json, j, "null", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
        }

        static List<double> ExtractDoubleArray(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return new List<double>();
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return new List<double>();
            int lb = json.IndexOf('[', colon);
            int rb = json.IndexOf(']', lb + 1);
            if (lb < 0 || rb < 0) return new List<double>();
            string body = json.Substring(lb + 1, rb - lb - 1);
            var list = new List<double>();
            foreach (string part in body.Split(','))
            {
                string t = part.Trim();
                if (t.Length == 0 || string.Equals(t, "null", StringComparison.OrdinalIgnoreCase)) continue;
                double v;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    throw new FormatException(name + " 含非法数值：" + t);
                list.Add(v);
            }
            return list;
        }

        static bool ExtractBool(string json, string key, bool fallback)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return fallback;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i + pat.Length);
            if (colon < 0) return fallback;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j >= json.Length) return fallback;
            if (json.Length >= j + 4 && string.Compare(json, j, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            if (json.Length >= j + 5 && string.Compare(json, j, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                return false;
            return fallback;
        }

        static double ExtractDouble(string json, string name, double fallback)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return fallback;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j + 4 <= json.Length && string.Compare(json, j, "null", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return fallback;
            int k = j;
            if (k < json.Length && (json[k] == '-' || json[k] == '+')) k++;
            while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '.' || json[k] == 'e' || json[k] == 'E' || json[k] == '+' || json[k] == '-'))
                k++;
            if (k <= j) return fallback;
            double v;
            if (double.TryParse(json.Substring(j, k - j), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }


        static string ExtractBoolToken(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return null;
            int j = colon + 1;
            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
            if (j + 4 <= json.Length && string.Compare(json, j, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return "true";
            if (j + 5 <= json.Length && string.Compare(json, j, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                return "false";
            return null;
        }

        static string ExtractString(string json, string name)
        {
            string key = "\"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int colon = json.IndexOf(':', i + key.Length);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = q1 + 1;
            var sb = new StringBuilder();
            while (q2 < json.Length)
            {
                char ch = json[q2];
                if (ch == '\\' && q2 + 1 < json.Length)
                {
                    char n = json[q2 + 1];
                    if (n == '"' || n == '\\') { sb.Append(n); q2 += 2; continue; }
                    if (n == 'n') { sb.Append('\n'); q2 += 2; continue; }
                    if (n == 'r') { sb.Append('\r'); q2 += 2; continue; }
                }
                if (ch == '"') break;
                sb.Append(ch);
                q2++;
            }
            return sb.ToString();
        }
        /// <summary>目标采样率 → Pico timebase；与 LiveHardwareOptions 加压/校准共用。</summary>
        public static uint ResolvePicoTimebase(double targetSampleRateHz)
        {
            return LiveHardwareOptions.ResolvePicoTimebase(targetSampleRateHz);
        }

        public static string FormatPicoRate(double hz)
        {
            if (hz >= 1e6) return (hz / 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " MS/s";
            if (hz >= 1e3) return (hz / 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " kS/s";
            return hz.ToString("0.###", CultureInfo.InvariantCulture) + " S/s";
        }

    }
}
