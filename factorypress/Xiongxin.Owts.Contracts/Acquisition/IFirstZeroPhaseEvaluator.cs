using System;

namespace Xiongxin.Owts.Contracts.Acquisition
{
    /// <summary>
    /// Task6 首零相位量程评估（对照 dac-source Task38Task6Analyzer / WavePreprocessEngine.AnalyzeWithLinearity）。
    /// 缺 DLL → StatusCode=MissingDll，禁止伪造 FirstZeroTimeSeconds（保持 NaN）。
    /// </summary>
    public interface IFirstZeroPhaseEvaluator : IDisposable
    {
        /// <summary>DLL 配置与探测状态（可读，供 Host/UI/Smoke）。</summary>
        Task6DllStatus GetDllStatus();

        /// <summary>
        /// 评估。缺 DLL / 加载失败 → UsedDll=false、StatusCode 标明原因，不抛死。
        /// 成功时填 RangeSuitable / BestRangeIndex / FirstZeroTimeSeconds 等；禁止编造相位。
        /// </summary>
        FirstZeroPhaseEvaluationResult Evaluate(FirstZeroPhaseEvaluationRequest request);
    }

    /// <summary>DLL 探测可读状态。</summary>
    public sealed class Task6DllStatus
    {
        public string ConfiguredDirectory { get; set; }
        public bool PathConfigured { get; set; }
        public bool TrioPresent { get; set; }
        public bool EngineLoaded { get; set; }
        public string EngineDllPath { get; set; }
        public string Detail { get; set; }

        public Task6DllStatus()
        {
            ConfiguredDirectory = string.Empty;
            EngineDllPath = string.Empty;
            Detail = string.Empty;
        }
    }

    /// <summary>← Task6InputBundle + AnalyzeWithLinearity 标定参数子集。</summary>
    public sealed class FirstZeroPhaseEvaluationRequest
    {
        public double[] VoltageRawVolts { get; set; }
        public double VoltageSampleRateHz { get; set; }
        public double[] PdRawVolts { get; set; }
        public double PdSampleRateHz { get; set; }
        public double PretriggerMicroseconds { get; set; }
        public double TransientSkipMicroseconds { get; set; }
        public double[] PdRangeValuesVolts { get; set; }
        public int CurrentPdRangeIndex { get; set; }

        /// <summary>分压比 V/V；DLL 入参用 /1000 → kV/V。</summary>
        public double DividerRatioVPerV { get; set; }
        public double AcDividerRatioVPerV { get; set; }
        public double VoltageCalibration { get; set; }
        public double PdCalibrationPcPerVolt { get; set; }
        public double InductanceH { get; set; }
        public double InductorDcrOhm { get; set; }
        public double TargetVoltageV { get; set; }
        public bool IsBackgroundCapture { get; set; }

        public FirstZeroPhaseEvaluationRequest()
        {
            VoltageRawVolts = new double[0];
            PdRawVolts = new double[0];
            PdRangeValuesVolts = new double[0];
            VoltageCalibration = 1.0;
            PdCalibrationPcPerVolt = 1.0;
            InductanceH = 1.0;
            InductorDcrOhm = 0.0;
            AcDividerRatioVPerV = 11000.0;
            DividerRatioVPerV = 11000.0;
        }
    }

    /// <summary>← Task6RangeFeedback + 评估路径状态。</summary>
    public sealed class FirstZeroPhaseEvaluationResult
    {
        public const string StatusMissingDll = "MissingDll";
        public const string StatusDllLoadFailed = "DllLoadFailed";
        public const string StatusBackgroundSkipped = "BackgroundSkipped";
        public const string StatusEvaluationOk = "EvaluationOk";
        public const string StatusEvaluationFailed = "EvaluationFailed";
        public const string StatusInvalidInput = "InvalidInput";

        /// <summary>整体调用是否得到可用反馈（含 DLL 成功分析）。缺 DLL 时为 false。</summary>
        public bool Success { get; set; }
        public string StatusCode { get; set; }
        public string Diagnostic { get; set; }

        /// <summary>是否真正走过 DLL AnalyzeWithLinearity。</summary>
        public bool UsedDll { get; set; }

        public bool Task6Success { get; set; }
        public int Task6Status { get; set; }
        public string ErrorMessage { get; set; }
        public int QualityFlags { get; set; }
        public bool PdOverRange { get; set; }
        public bool MultipleClippedPulses { get; set; }
        public bool RangeSuitable { get; set; }
        /// <summary>对照 Task6RangeAdjustmentDirection：Suitable=-1, Increase=0, Decrease=1。</summary>
        public int RangeAdjustmentDirection { get; set; }
        public int BestRangeIndex { get; set; }
        public double PdMaximumAbsolute { get; set; }
        /// <summary>无效时必须为 NaN；禁止伪造 0。</summary>
        public double FirstZeroTimeSeconds { get; set; }

        public FirstZeroPhaseEvaluationResult()
        {
            StatusCode = StatusMissingDll;
            Diagnostic = string.Empty;
            ErrorMessage = string.Empty;
            RangeAdjustmentDirection = -1;
            BestRangeIndex = -1;
            FirstZeroTimeSeconds = double.NaN;
        }

        public static FirstZeroPhaseEvaluationResult MissingDll(string detail)
        {
            return new FirstZeroPhaseEvaluationResult
            {
                Success = false,
                UsedDll = false,
                StatusCode = StatusMissingDll,
                Diagnostic = detail ?? "Task6 DLL missing.",
                FirstZeroTimeSeconds = double.NaN
            };
        }
    }
}
