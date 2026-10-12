using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace HVX.OWTS.Task38
{
    [Serializable]
    public sealed class Task38Profile
    {
        public Task38Profile()
        {
            Hardware = new Task38Configuration {
                DividerRatioVPerV = 11000, VoltageGainVPerAoVolt = 6000,
                SourceVoltageFeedbackFullScaleV = 30000, SourceCurrentFeedbackFullScaleA = .01,
                CurrentLimitA = .01, ResidualVoltageSafeThresholdV = 50, ZeroVoltageAlarmThresholdV = 0,
                SwitchingTransientSkipMicroseconds = 500, ArtDeviceName = "Dev1", PicoResolutionBits = 15
            };
            VoltageCalibration = 1.0;
            PdCalibrationPcPerVolt = 123;
            AcDividerRatioVPerV = 11000;
            Task6Directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Task6");
            InductanceH = 1.0;
            InductorDcrOhm = 22;
        }
        public Task38Configuration Hardware { get; set; }
        public double VoltageCalibration { get; set; }
        public double PdCalibrationPcPerVolt { get; set; }
        public double AcDividerRatioVPerV { get; set; }
        public double InductanceH { get; set; }
        public double InductorDcrOhm { get; set; }
        public string Task6Directory { get; set; }
        public Task38Configuration EffectiveHardware()
        {
            if (!FinitePositive(VoltageCalibration) || !FinitePositive(PdCalibrationPcPerVolt) ||
                !FinitePositive(InductanceH) || !FinitePositive(AcDividerRatioVPerV))
                throw new ArgumentException("电压校准系数、局放校准系数、交流分压比和电感必须为有限正数。");
            Task38Configuration c = Hardware.Clone();
            if (Double.IsNaN(InductorDcrOhm) || Double.IsInfinity(InductorDcrOhm) || InductorDcrOhm < 0)
                throw new ArgumentException("电感直流电阻必须为有限非负数，单位 Ω；0 表示未扣除电阻损耗。");
            c.DividerRatioVPerV *= VoltageCalibration;
            c.FiniteDividerRatioVPerV = AcDividerRatioVPerV * VoltageCalibration;
            return c;
        }
        private static bool FinitePositive(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v) && v > 0; }
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".pending";
            using (FileStream s = File.Create(temp)) new XmlSerializer(typeof(Task38Profile)).Serialize(s, this);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        public static Task38Profile Load(string path)
        {
            using (FileStream s = File.OpenRead(path)) return (Task38Profile)new XmlSerializer(typeof(Task38Profile)).Deserialize(s);
        }
    }

    public sealed class Task38AnalysisResult
    {
        public Task6RangeFeedback RangeFeedback { get; internal set; }
        public double FrequencyHz { get; internal set; }
        public double DampingPerSecond { get; internal set; }
        public double CorrectedTanDelta { get; internal set; }
        public bool VoltageAnalysisSuccess { get; internal set; }
        public int VoltageQualityFlags { get; internal set; }
        public string Error { get; internal set; }
    }

    public sealed class Task38Task6Analyzer
    {
        private readonly MethodInfo analyze;
        private readonly object engine;
        public Task38Task6Analyzer(string directory)
        {
            string full = Path.GetFullPath(directory);
            Assembly.LoadFrom(Path.Combine(full, "7e4a9c2d6f1b8053a7c4e912.V1.0.6.dll"));
            Assembly a = Assembly.LoadFrom(Path.Combine(full, "05f21d64e56010f23ed61cc9.V1.0.6.dll"));
            if (!File.Exists(Path.Combine(full, "2c8a82000748444a44aa0ad8.V1.0.6.dll")))
                throw new FileNotFoundException("任务6原生计算依赖缺失。");
            Type t = a.GetType("OWTS.WavePreprocess.WavePreprocessEngine", true);
            engine = Activator.CreateInstance(t);
            analyze = t.GetMethod("AnalyzeWithLinearity");
            if (analyze == null || analyze.GetParameters().Length != 29)
                throw new InvalidOperationException("任务6正式 AnalyzeWithLinearity 接口不匹配。");
        }
        public Task38AnalysisResult Analyze(Task38CycleResult capture, Task38Profile profile)
        {
            if (capture == null || !capture.Success) throw new InvalidOperationException("本轮采集未成功，禁止给出合适量程结论。");
            if (capture.IsBackgroundCapture) throw new InvalidOperationException("背景采集不计算振荡介损和首零相位量程结论。");
            Task6InputBundle b = capture.BuildTask6Input(profile.Hardware);
            // Task6's divider unit is kV/V; raw ART/Pico arrays remain in volts.
            object[] args = new object[]
            {
                b.VoltageRawVolts, b.VoltageSampleRateHz, b.PdRawVolts, b.PdSampleRateHz,
                b.VoltageSampleRateHz, b.PretriggerMicroseconds, b.TransientSkipMicroseconds,
                profile.Hardware.DividerRatioVPerV / 1000.0, profile.AcDividerRatioVPerV / 1000.0,
                profile.VoltageCalibration, profile.PdCalibrationPcPerVolt, false, new double[0, 2],
                profile.InductanceH, profile.InductorDcrOhm, 0.0, 0.0, b.PdRangeValuesVolts, b.CurrentPdRangeIndex,
                0.0, 0.0, 0, 0, 0, 3, new double[] { 20.0, 5000.0 },
                profile.Hardware.TargetVoltageV / 1000.0, null, null
            };
            try { analyze.Invoke(engine, args); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("任务6分析失败", ex.InnerException ?? ex); }
            object pd = args[27], hv = args[28];
            return new Task38AnalysisResult
            {
                RangeFeedback = new Task6RangeFeedback
                {
                    Task6Success = Get<bool>(pd, "Success"), Task6Status = Get<int>(pd, "Status"),
                    ErrorMessage = Get<string>(pd, "ErrorMessage"), QualityFlags = Get<int>(pd, "QualityFlags"),
                    PdOverRange = Get<bool>(pd, "PdOverRange"), MultipleClippedPulses = Get<bool>(pd, "MultipleClippedPulses"),
                    RangeSuitable = Get<bool>(pd, "RangeSuitable"), RangeAdjustmentDirection = Get<int>(pd, "RangeAdjustmentDirection"),
                    BestRangeIndex = Get<int>(pd, "BestRangeIndex"), PdMaximumAbsolute = Get<double>(pd, "PdMaximumAbsolute"),
                    FirstZeroTimeSeconds = Get<double>(pd, "FirstZeroTimeSeconds")
                },
                FrequencyHz = Get<double>(hv, "FrequencyHz"), DampingPerSecond = Get<double>(hv, "DampingAlphaPerSecond"),
                CorrectedTanDelta = Get<double>(hv, "CorrectedTanDelta"),
                VoltageAnalysisSuccess = Get<bool>(hv, "Success"), VoltageQualityFlags = Get<int>(hv, "QualityFlags"),
                Error = Get<string>(pd, "ErrorMessage")
            };
        }
        private static T Get<T>(object o, string name)
        {
            if (o == null) throw new InvalidOperationException("任务6返回空结果。");
            PropertyInfo p = o.GetType().GetProperty(name);
            if (p == null) throw new MissingMemberException(o.GetType().FullName, name);
            return (T)p.GetValue(o, null);
        }
    }
}
