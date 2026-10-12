using System;
using Xiongxin.Owts.Acquisition.Live;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>演示/壳侧组装选项。默认全程仿真，不碰真驱动。</summary>
    public sealed class OwtsRuntimeOptions
    {
        /// <summary>环境变量：=1 时 FromEnvironment 打开实机门控。</summary>
        public const string AllowLiveEnvVar = "OWTS_ALLOW_LIVE_HARDWARE";
        public const string ArtDirEnvVar = "OWTS_ART_DRIVER_DIR";
        public const string PicoDirEnvVar = "OWTS_PICO_SDK_DIR";
        public const string PicoSerialEnvVar = "OWTS_PICO_SERIAL";
        public const string ArtDeviceEnvVar = "OWTS_ART_DEVICE";
        public const string ExpectedArtEnvVar = "OWTS_EXPECTED_ART_IDENTITY";
        public const string ExpectedPicoEnvVar = "OWTS_EXPECTED_PICO_SERIAL";
        public const string BenchCalibrationEnvVar = "OWTS_BENCH_CALIBRATION_FILE";
        public const string OnnxModelEnvVar = "OWTS_ONNX_MODEL";
        public const string Task6RuntimeDirEnvVar = "OWTS_TASK6_RUNTIME_DIR";
        public const string ProjectLibrariesRootEnvVar = "OWTS_PROJECT_LIBRARIES_ROOT";

        public string ProjectDbPath { get; set; }
        public string LegacyProjectDirectory { get; set; }

        /// <summary>实机门控，默认 false。CreateSimulation 无视此值永远仿真；CreateLive 要求为 true。</summary>
        public bool AllowLiveHardware { get; set; }

        public string ArtDriverDirectory { get; set; }
        public string PicoSdkDirectory { get; set; }
        public string ArtDeviceName { get; set; }
        public string PicoSerial { get; set; }
        /// <summary>期望 ART 身份（双校验）；空=不强制。可与 ArtDeviceName 相同。</summary>
        public string ExpectedArtIdentity { get; set; }
        /// <summary>期望 Pico 序列号（双校验）；空=不强制。</summary>
        public string ExpectedPicoSerial { get; set; }
        /// <summary>true（默认）= 抑制 Pico 官方启动窗；仅 SDK，不启 PicoScope.exe。</summary>
        public bool SuppressPicoStartupWindow { get; set; }
        /// <summary>Short（默认）或 Field（≈60 ms / 7.5M 点，内存与落盘有风险）。</summary>
        public PicoCaptureProfile PicoCaptureProfile { get; set; }
        /// <summary>台架标定 JSON 路径（VoltageGain/DividerRatio/CurrentScale）。空=未写入，Live 拒非零。</summary>
        public string BenchCalibrationFilePath { get; set; }
        /// <summary>ONNX 缺陷模型路径（现场推理）。空则走 MissingModel；亦可读环境变量 OWTS_ONNX_MODEL。</summary>
        public string OnnxModelPath { get; set; }
        /// <summary>Task6 首零相位 DLL 目录（含三件套或 Task6 子目录）。空则读 OWTS_TASK6_RUNTIME_DIR；缺则 MissingDll。</summary>
        public string Task6RuntimeDirectory { get; set; }
        /// <summary>工程库扫描根目录（含 project.owts.db / *.owts.db / *.owts 或遗留 ITEM.in）。空则读 OWTS_PROJECT_LIBRARIES_ROOT。</summary>
        public string ProjectLibrariesRoot { get; set; }
        /// <summary>最近工程库列表文件；空则用 LocalAppData/Xiongxin.Owts/recent-project-libraries.txt。</summary>
        public string RecentLibrariesFilePath { get; set; }

        public OwtsRuntimeOptions()
        {
            ProjectDbPath = string.Empty;
            LegacyProjectDirectory = string.Empty;
            AllowLiveHardware = false;
            ArtDriverDirectory = string.Empty;
            PicoSdkDirectory = string.Empty;
            ArtDeviceName = "Dev1";
            PicoSerial = string.Empty;
            ExpectedArtIdentity = string.Empty;
            ExpectedPicoSerial = string.Empty;
            SuppressPicoStartupWindow = true;
            PicoCaptureProfile = PicoCaptureProfile.Short;
            BenchCalibrationFilePath = string.Empty;
            OnnxModelPath = string.Empty;
            Task6RuntimeDirectory = string.Empty;
            ProjectLibrariesRoot = string.Empty;
            RecentLibrariesFilePath = string.Empty;
        }

        /// <summary>从环境变量读取实机配置（未设置则保持仿真默认）。</summary>
        public static OwtsRuntimeOptions FromEnvironment()
        {
            var o = new OwtsRuntimeOptions();
            o.AllowLiveHardware = Environment.GetEnvironmentVariable(AllowLiveEnvVar) == "1";
            o.ArtDriverDirectory = Environment.GetEnvironmentVariable(ArtDirEnvVar) ?? string.Empty;
            o.PicoSdkDirectory = Environment.GetEnvironmentVariable(PicoDirEnvVar) ?? string.Empty;
            o.PicoSerial = Environment.GetEnvironmentVariable(PicoSerialEnvVar) ?? string.Empty;
            string artDev = Environment.GetEnvironmentVariable(ArtDeviceEnvVar);
            if (!string.IsNullOrWhiteSpace(artDev)) o.ArtDeviceName = artDev.Trim();
            o.ExpectedArtIdentity = Environment.GetEnvironmentVariable(ExpectedArtEnvVar) ?? string.Empty;
            o.ExpectedPicoSerial = Environment.GetEnvironmentVariable(ExpectedPicoEnvVar) ?? string.Empty;
            // Pico：若只配了 Open 目标序列号，默认同作期望（强制该台）
            if (string.IsNullOrWhiteSpace(o.ExpectedPicoSerial) && !string.IsNullOrWhiteSpace(o.PicoSerial))
                o.ExpectedPicoSerial = o.PicoSerial;
            o.BenchCalibrationFilePath = Environment.GetEnvironmentVariable(BenchCalibrationEnvVar) ?? string.Empty;
            o.OnnxModelPath = Environment.GetEnvironmentVariable(OnnxModelEnvVar) ?? string.Empty;
            o.Task6RuntimeDirectory = Environment.GetEnvironmentVariable(Task6RuntimeDirEnvVar) ?? string.Empty;
            o.ProjectLibrariesRoot = Environment.GetEnvironmentVariable(ProjectLibrariesRootEnvVar) ?? string.Empty;
            return o;
        }
    }
}
