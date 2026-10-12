using System;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using Xiongxin.Owts.Acquisition;
using Xiongxin.Owts.Acquisition.Live;
using Xiongxin.Owts.Ai;
using Xiongxin.Owts.Algorithms;
using Xiongxin.Owts.Contracts.Acquisition;
using Xiongxin.Owts.Contracts.Ai;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Project;
using Xiongxin.Owts.Contracts.Report;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.Contracts.Waveform;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 组合根：仿真采集单例、ONNX/缺模 AI、阶段2 算法、SQLite、报告占位、状态五态板。
    /// </summary>
    public sealed class OwtsServices : IDisposable
    {
        public IAcquisitionSession Acquisition { get; private set; }
        public IDefectRecognitionService DefectRecognition { get; private set; }
        /// <summary>现场 ONNX 推理（脉冲窗/特征向量）。缺模 GetModelStatus/Recognize → MissingModel。</summary>
        public IDefectRecognizer DefectRecognizer { get; private set; }
        /// <summary>Task6 首零相位 DLL 评估。缺 DLL → MissingDll；有则反射 AnalyzeWithLinearity。</summary>
        public IFirstZeroPhaseEvaluator FirstZeroPhase { get; private set; }
        public ILocationAnalyzer LocationAnalyzer { get; private set; }
        public ISeventhEvidenceEvaluator SeventhEvidence { get; private set; }
        public ISignalFilter SignalFilter { get; private set; }
        public IConclusionEngine ConclusionEngine { get; private set; }
        public IProjectRepository Repository { get; private set; }
        public IProjectWorkspace Workspace { get; private set; }
        public IReportExporter ReportExporter { get; private set; }
        public OperationalStatusBoard StatusBoard { get; private set; }
        public OwtsRuntimeOptions Options { get; private set; }

        bool _disposed;

        /// <summary>是否实机组合（仅 CreateLive 为 true）。</summary>
        public bool IsLiveHardware { get; private set; }

        /// <summary>是否 FakeLive（硬化 LiveAcquisitionSession + Fake 后端；无驱动）。</summary>
        public bool IsFakeLive { get; private set; }

        /// <summary>局放 pC/V 系数：Connect/Align 时从 {AppBase}/config/pd-calibration.json 加载；无效默认 2000。</summary>
        public double PdCalibrationPcPerVolt { get; private set; }

        /// <summary>最近一次 pd-calibration 路径（可能尚不存在）。</summary>
        public string PdCalibrationPath { get; private set; }

        int _lastSuccessfulRangeIndex = -1;
        double _lastSuccessfulRangeKv = double.NaN;

        /// <summary>最近一次成功加压量程（供连续同压/下一批沿用；-1=尚未成功）。</summary>
        public int LastSuccessfulPicoRangeIndex { get { return _lastSuccessfulRangeIndex; } }

        /// <summary>最近一次成功加压目标 kV（与 LastSuccessfulPicoRangeIndex 配对）。</summary>
        public double LastSuccessfulRangeTargetKv { get { return _lastSuccessfulRangeKv; } }

        /// <summary>最近一次成功加压周期结果（供定位数据提取；非 E+ 公式）。</summary>
        public AcquisitionCycleResult LastPressCycle { get; private set; }



        /// <summary>仿真组合：强制 AllowLiveHardware=false，永不触达 Live 类型或驱动。</summary>
        public static OwtsServices CreateSimulation(OwtsRuntimeOptions options)
        {
            if (options == null) options = new OwtsRuntimeOptions();
            options.AllowLiveHardware = false;
            var fz = FirstZeroPhaseEvaluatorFactory.Create(options.Task6RuntimeDirectory);
            return Compose(options, SingletonAcquisitionSession.CreateSimulation(fz), false, false, fz);
        }

        /// <summary>
        /// FakeLive：与 FactoryPressApi / HvPressTest 同一硬化采集路径（LiveAcquisitionSession），
        /// 后端为 FakeLiveHardwareBackend；不加载驱动；可 Smoke / DesktopUi 演示加压门禁。
        /// 默认写入测试台架增益（≠危险占位 1000），与 PressTestRunner 一致。
        /// </summary>
        public static OwtsServices CreateFakeLive(OwtsRuntimeOptions options)
        {
            if (options == null) options = new OwtsRuntimeOptions();
            options.AllowLiveHardware = false; // FakeLive 不碰真驱动
            var fz = FirstZeroPhaseEvaluatorFactory.Create(options.Task6RuntimeDirectory);
            return Compose(options, CreateFakeLiveSession(options, fz), false, true, fz);
        }

        /// <summary>
        /// 实机组合：要求 AllowLiveHardware=true，否则抛错。外层仍是 SingletonAcquisitionSession。
        /// 构造本身不加载驱动；驱动在 Acquisition.Open 时加载，缺失返回明确错误。
        /// </summary>
        public static OwtsServices CreateLive(OwtsRuntimeOptions options)
        {
            if (options == null || !options.AllowLiveHardware)
                throw new InvalidOperationException("实机模式未启用：须 AllowLiveHardware=true（或环境变量 "
                    + OwtsRuntimeOptions.AllowLiveEnvVar + "=1）并经操作员二次确认。");
            var fzLive = FirstZeroPhaseEvaluatorFactory.Create(options.Task6RuntimeDirectory);
            return Compose(options, CreateLiveSession(options, fzLive), true, false, fzLive);
        }

        /// <summary>按门控选择：AllowLiveHardware=true → 实机，否则仿真。</summary>
        public static OwtsServices Create(OwtsRuntimeOptions options)
        {
            return options != null && options.AllowLiveHardware ? CreateLive(options) : CreateSimulation(options);
        }

        /// <summary>出厂加压台架硬过流（A）；与 PressTestConfig.TestBenchHardOverCurrentA 一致。</summary>
        public const double FactoryBenchHardOverCurrentA = 0.012;

        /// <summary>
        /// 与 PressTestRunner.OpenSession 对齐的 LiveHardwareOptions：
        /// FakeLive=Short；实机=Pico/ART 振荡同窗 60ms；台架过流 0.012A；步间延时 5ms。
        /// </summary>
        static LiveHardwareOptions BuildLiveHardwareOptions(OwtsRuntimeOptions options, bool forFakeLive)
        {
            if (options == null) options = new OwtsRuntimeOptions();
            var hw = new LiveHardwareOptions
            {
                ArtDriverDirectory = options.ArtDriverDirectory ?? string.Empty,
                PicoSdkDirectory = options.PicoSdkDirectory ?? string.Empty,
                ArtDeviceName = string.IsNullOrWhiteSpace(options.ArtDeviceName) ? "Dev1" : options.ArtDeviceName,
                PicoSerial = options.PicoSerial ?? string.Empty,
                ExpectedArtIdentity = options.ExpectedArtIdentity ?? string.Empty,
                ExpectedPicoSerial = options.ExpectedPicoSerial ?? string.Empty,
                SuppressPicoStartupWindow = options.SuppressPicoStartupWindow,
                TargetReachedToleranceFraction = 0.05,
                OverVoltageTripFraction = 0.20,
                ZeroBackgroundLimitV = 500.0,
                EStopReadTimeoutMilliseconds = 300,
                EStopPollIntervalMilliseconds = 10,
                InterRoundCooldownMilliseconds = 2000,
                RampStepDelayMilliseconds = 5,
                PicoRangeIndex = 6,
                PicoResolutionBits = 14,
                PicoTimebase = 3,
                PicoExpectedSampleIntervalNanoseconds = 8.0,
                PicoExternalTriggerThresholdV = 3.0,
                PicoCaptureTimeoutMilliseconds = 3000,
                ArtCaptureTimeoutMilliseconds = 3000,
                ArtMonitorInputMinimumV = -5,
                ArtMonitorInputMaximumV = 5
            };
            if (forFakeLive)
                hw.ApplyCaptureProfile(PicoCaptureProfile.Short);
            else if (options.PicoCaptureProfile == PicoCaptureProfile.Field)
                hw.ApplyCaptureProfile(PicoCaptureProfile.Field);
            else
                hw.ApplyPicoOscillationWindow60Ms(); // 实机默认：与出厂加压同窗

            if (!string.IsNullOrWhiteSpace(options.BenchCalibrationFilePath))
                hw.ApplyBenchCalibrationFile(options.BenchCalibrationFilePath);

            if (hw.BenchCalibrationApplied)
                hw.HardOverCurrentA = FactoryBenchHardOverCurrentA;
            return hw;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static IAcquisitionSession CreateLiveSession(OwtsRuntimeOptions options, IFirstZeroPhaseEvaluator firstZero = null)
        {
            var hw = BuildLiveHardwareOptions(options, forFakeLive: false);
            return new SingletonAcquisitionSession(new LiveAcquisitionSession(hw, null, firstZero));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static IAcquisitionSession CreateFakeLiveSession(OwtsRuntimeOptions options, IFirstZeroPhaseEvaluator firstZero = null)
        {
            var hw = BuildLiveHardwareOptions(options, forFakeLive: true);
            // FakeLive：无文件时写入测试台架增益，便于 DesktopUi 加压门禁演示（≠ 现网危险占位 1000）
            if (!hw.BenchCalibrationApplied || !hw.AllowsLiveNonZeroEnergize())
            {
                var cal = LiveBenchCalibration.CreateBenchSample(
                    2000, 12, 0.1, 2000, "FakeLive", StatusCopy.TestBenchGainNote);
                string err;
                if (!hw.TryApplyBenchCalibration(cal, out err))
                    throw new InvalidOperationException("FakeLive 台架标定写入失败: " + err);
            }
            hw.HardOverCurrentA = FactoryBenchHardOverCurrentA;
            // 与 PressTestRunner 一致：Fake 后端共用同一 LiveHardwareOptions（采样/分辨率）
            var fake = new FakeLiveHardwareBackend(hw);
            fake.SetEStopPressed(true); // Open 前默认急停按下
            return new SingletonAcquisitionSession(new LiveAcquisitionSession(hw, fake, firstZero));
        }

        static OwtsServices Compose(OwtsRuntimeOptions options, IAcquisitionSession acquisition, bool live, bool fakeLive, IFirstZeroPhaseEvaluator firstZero = null)
        {
            var services = new OwtsServices();
            services.Options = options;
            services.IsLiveHardware = live;
            services.IsFakeLive = fakeLive;
            services.Acquisition = acquisition;
            services.FirstZeroPhase = firstZero ?? FirstZeroPhaseEvaluatorFactory.Create(options != null ? options.Task6RuntimeDirectory : null);
            // ONNX 路径：始终挂 OnnxDefectRecognizer；IDefectRecognitionService 缺文件时仍 ModelMissing
            var onnx = new OnnxDefectRecognizer(options.OnnxModelPath);
            services.DefectRecognizer = onnx;
            services.DefectRecognition = new OnnxBackedDefectRecognitionService(onnx);
            services.LocationAnalyzer = new Task17LocationAdapter(new SimplifiedLocationAnalyzer());
            services.SeventhEvidence = new SeventhEvidenceEvaluator();
            services.SignalFilter = new PdLinearSignalFilter();
            services.ConclusionEngine = new NotImplementedConclusionEngine();
            services.Repository = new SqliteProjectRepository();
            services.Workspace = new SqliteProjectWorkspace(services.Repository);
            services.ReportExporter = new MinimalHtmlReportExporter(services.Repository);
            services.StatusBoard = new OperationalStatusBoard();
            services.PdCalibrationPcPerVolt = PdCalibrationStore.DefaultPdCalibrationPcPerVolt;
            services.PdCalibrationPath = PdCalibrationStore.DefaultPath();
            return services;
        }

        public void OpenProjectDatabase(string projectDbPath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(projectDbPath))
                throw new ArgumentException("projectDbPath");
            Options.ProjectDbPath = System.IO.Path.GetFullPath(projectDbPath);
            Repository.OpenOrCreate(Options.ProjectDbPath);
            string msg;
            if (Workspace != null)
                Workspace.TryOpen(Options.ProjectDbPath, out msg);
            string recent = string.IsNullOrWhiteSpace(Options.RecentLibrariesFilePath)
                ? ProjectLibraryCatalog.DefaultRecentFilePath()
                : Options.RecentLibrariesFilePath;
            ProjectLibraryCatalog.Remember(recent, Options.ProjectDbPath);
        }

        /// <summary>列出最近 + 扫描根下的工程库（SQLite .owts / project.owts.db / 遗留目录）。</summary>
        public IReadOnlyList<ProjectLibraryEntry> ListProjectLibraries(string scanRoot = null, bool loadSummaries = true)
        {
            ThrowIfDisposed();
            string recent = string.IsNullOrWhiteSpace(Options.RecentLibrariesFilePath)
                ? ProjectLibraryCatalog.DefaultRecentFilePath()
                : Options.RecentLibrariesFilePath;
            string root = scanRoot;
            if (string.IsNullOrWhiteSpace(root))
                root = Options.ProjectLibrariesRoot;
            return ProjectLibraryCatalog.MergeRecentAndScan(recent, root, Options.ProjectDbPath, loadSummaries);
        }

        /// <summary>
        /// 切换当前工程库：打开 SQLite / 目录内 project.owts.db / 遗留 ITEM.in 导入。
        /// 不关闭采集会话（硬件与库解耦）；写入最近列表。
        /// </summary>
        public bool TrySwitchProjectLibrary(string path, out string message)
        {
            ThrowIfDisposed();
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                message = "请指定工程库路径。";
                return false;
            }
            if (Workspace == null)
            {
                message = "Workspace 未就绪。";
                return false;
            }
            if (!Workspace.TryOpen(path, out message))
                return false;
            string db = Workspace.ProjectPath;
            if (string.IsNullOrEmpty(db))
            {
                message = "切换后工程路径为空。";
                return false;
            }
            Options.ProjectDbPath = System.IO.Path.GetFullPath(db);
            // Repository 已由 Workspace.TryOpen → OpenOrCreate；再同步一次以防旁路
            if (Repository != null)
                Repository.OpenOrCreate(Options.ProjectDbPath);
            string recent = string.IsNullOrWhiteSpace(Options.RecentLibrariesFilePath)
                ? ProjectLibraryCatalog.DefaultRecentFilePath()
                : Options.RecentLibrariesFilePath;
            ProjectLibraryCatalog.Remember(recent, Options.ProjectDbPath);
            message = string.IsNullOrEmpty(message) ? ("已切换工程库: " + Options.ProjectDbPath) : message;
            return true;
        }

        /// <summary>当前库测量/采集历史摘要（按时间倒序）。</summary>
        public IReadOnlyList<AcquisitionHistorySummary> ListCurrentAcquisitionSummaries(int take = 200)
        {
            ThrowIfDisposed();
            return ProjectLibraryCatalog.ListAcquisitionSummaries(Repository, take);
        }


        /// <summary>从采集快照同步五态板（不含目标电压；由调用方设 TargetVoltageKv）。</summary>
        public void SyncStatusBoardFromAcquisition()
        {
            ThrowIfDisposed();
            if (Acquisition == null || StatusBoard == null) return;
            AcquisitionStatusSnapshot snap = Acquisition.GetStatus();
            StatusBoard.Connected = snap.IsDeviceConnected || Acquisition.IsOpen;
            StatusBoard.Calibrated = snap.IsCalibrated;
            StatusBoard.Calibrating = snap.IsCalibrating;
            StatusBoard.EStopPressed = snap.EStopPressed;
            StatusBoard.EmergencyLatched = snap.EmergencyLatched;
            StatusBoard.ImpedanceBandwidthWide = snap.ImpedanceBandwidthWide;
        }

        /// <summary>UI「停止」：转发 OperatorAbort（不永久锁存）；真 P0.4 仍独立。</summary>
        public void RequestOperatorAbort()
        {
            ThrowIfDisposed();
            if (Acquisition == null) return;
            Acquisition.RequestOperatorAbort();
        }

        /// <summary>
        /// 硬件急停已解除且源关时清软件锁存（对齐 HvPressTest / LiveAcquisitionSession）。
        /// 仿真无锁存语义时返回 true；真急停/未知仍拒绝。
        /// </summary>
        public bool TryClearEmergencyLatchIfHardwareSafe(out string detail)
        {
            ThrowIfDisposed();
            detail = null;
            var singleton = Acquisition as SingletonAcquisitionSession;
            if (singleton != null)
                return singleton.TryClearEmergencyLatchIfHardwareSafe(out detail);
            var live = Acquisition as LiveAcquisitionSession;
            if (live != null)
                return live.TryClearEmergencyLatchIfHardwareSafe(out detail);
            detail = "非 Live/FakeLive（无软件急停锁存语义）";
            return true;
        }


        /// <summary>
        /// 连接后 ART+Pico 双身份校验（LiveAcquisitionSession.Open 已在配置期望串时硬门回滚；
        /// 本 API 供 UI/日志复核）。失败返回 false；未配置期望串时通过（跳过）。
        /// </summary>
        public bool TryValidateDualIdentity(out string message)
        {
            ThrowIfDisposed();
            message = string.Empty;
            if (Acquisition == null)
            {
                message = StatusCopy.DeviceNotConnected;
                return false;
            }
            var probe = Acquisition.Probe(new HardwareProbeRequest
            {
                AllowSimulation = !IsLiveHardware,
                RequestedControllerLocation = Options != null ? Options.ExpectedArtIdentity : null,
                RequestedAcquisitionLocation = Options != null ? Options.ExpectedPicoSerial : null
            });
            // Live 会话已在 Probe 内 Apply；再统一用 Options 期望串确认
            string expArt = Options != null ? Options.ExpectedArtIdentity : null;
            string expPico = Options != null ? Options.ExpectedPicoSerial : null;
            string status;
            bool ok = HardwareDualIdentity.TryValidate(
                expArt, expPico,
                probe.ControllerSerial, probe.AcquisitionSerial,
                out message, out status);
            if (!ok)
                probe.IdentityValid = false;
            return ok;
        }

        public string FormatStatusFiveStates()
        {
            SyncStatusBoardFromAcquisition();
            return StatusBoard.FormatFiveStates();
        }

        /// <summary>
        /// DesktopUi / Host 日志：当前组合根的硬化采集能力摘要（lease / DI 拆锁 / Pico 冷却 / 单次 Open / 无 PicoScope.exe）。
        /// </summary>
        public string DescribeAcquisitionHardening()
        {
            ThrowIfDisposed();
            if (IsLiveHardware)
            {
                return StatusCopy.BrandCompany + " · 实机：" + StatusCopy.HardeningLivePathHint
                    + "；cleanupFailed门闩=" + (SingletonAcquisitionSession.ProcessCleanupFailed ? "已置位" : "清")
                    + "；" + SessionFault.FormatLatchSummary();
            }
            if (IsFakeLive)
            {
                return StatusCopy.BrandCompany + " · FakeLive：" + StatusCopy.FakeLiveModeHint
                    + "；" + StatusCopy.HardeningLivePathHint
                    + "（Fake 后端不加载驱动；冷却 Mark* 由实机后端打点）"
                    + "；" + SessionFault.FormatLatchSummary();
            }
            return StatusCopy.BrandCompany + " · 仿真：门禁与五态可演；无 DeviceSdkLease/驱动；"
                + StatusCopy.PicoSdkOnlyNoSplash;
        }

        /// <summary>进程/落盘清理失败门闩：true 时须操作员确认关断后 TryResetCleanupGateAfterOperatorAck，或退出进程。</summary>
        public bool ProcessCleanupFailed
        {
            get { return SingletonAcquisitionSession.ProcessCleanupFailed; }
        }

        /// <summary>
        /// 操作员确认高压/AO 已关断后，复位进程+落盘清理门，允许同进程再 Connect。
        /// 不自动调用；会话仍 Open 时拒绝。真 Clear/CloseUnit 失败后仍须确认再复位。
        /// </summary>
        public bool TryResetCleanupGateAfterOperatorAck(bool operatorAcknowledgedSourceOff, out string detail)
        {
            return SingletonAcquisitionSession.TryResetCleanupGateAfterOperatorAck(operatorAcknowledgedSourceOff, out detail);
        }

        /// <summary>session-fault / cleanup-fault 落盘摘要（无弹窗）。</summary>
        public string DescribeSessionFaultLatch()
        {
            return StatusCopy.BrandCompany + " · " + SessionFault.FormatLatchSummary();
        }

        /// <summary>
        /// Task15 结论完整路径（因子骨架 + ONNX stub 特征 + 推理候选）。
        /// OfficialConclusion 恒 false；供 DesktopUi / Host 接线，不伪造成功结论。
        /// engOrPulseSamples：工程波形或脉冲窗；可空（特征全 0）。
        /// pulseVoltageKv/Charge/Phase/Energy：可选 Level 趋势输入。
        /// </summary>
        public ConclusionPathSnapshot EvaluateConclusionPath(
            ParameterMatrixDto accepted = null,
            double voltageMinKv = double.NaN,
            double voltageMaxKv = double.NaN,
            float[] engOrPulseSamples = null,
            double[] pulseVoltageKv = null,
            double[] pulseChargeAbsPc = null,
            double[] pulsePhaseDeg = null,
            double[] pulseRealEnergy = null,
            string contextKey = "host")
        {
            ThrowIfDisposed();
            return ConclusionPathEvaluator.Evaluate(
                ConclusionEngine,
                DefectRecognizer,
                accepted,
                voltageMinKv,
                voltageMaxKv,
                engOrPulseSamples,
                pulseVoltageKv,
                pulseChargeAbsPc,
                pulsePhaseDeg,
                pulseRealEnergy,
                contextKey);
        }

        /// <summary>
        /// CALv5 / mid StatusJson 落盘到 outDir 下带时间戳的 bundle（HostAdapters 侧，无 WinForms）。
        /// thr 默认 2；禁止 thr1。无 Worker 时仍写 fallback JSON + CAL 头预览 + RETEST_CHECKLIST。
        /// </summary>
        public DacStatusJsonCapture.BundleResult DumpMidStatusJson(
            DacImportResult import,
            long measurementId,
            string outDir,
            double thr = 2.0,
            int[] algos = null)
        {
            ThrowIfDisposed();
            if (LocationAnalyzer == null)
                throw new InvalidOperationException("LocationAnalyzer 未接线。");
            return DacStatusJsonCapture.CaptureMidBundle(
                LocationAnalyzer, import, measurementId, outDir ?? "docs/accuracy-runs", thr, algos, true);
        }

        /// <summary>ONNX stub 特征描述（DesktopUi 日志可读）。</summary>
        public string DescribeOnnxFeatureStub(float[] samples = null, int targetLength = 0)
        {
            ThrowIfDisposed();
            int len = targetLength > 0 ? targetLength : Xiongxin.Owts.Ai.OnnxFeatureStubBuilder.DefaultFeatureLength;
            if (DefectRecognizer != null)
            {
                var st = DefectRecognizer.GetModelStatus();
                if (st != null && !string.IsNullOrWhiteSpace(st.InputShape))
                {
                    string s = st.InputShape.Trim().Trim('[', ']');
                    string[] parts = s.Split(new[] { ',', 'x', 'X', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = parts.Length - 1; i >= 0; i--)
                    {
                        int n;
                        if (int.TryParse(parts[i].Trim(), out n) && n > 0) { len = n; break; }
                    }
                }
            }
            float[] fv = Xiongxin.Owts.Ai.OnnxFeatureStubBuilder.FromSamples(samples, len);
            return StatusCopy.BrandCompany + " · " + Xiongxin.Owts.Ai.OnnxFeatureStubBuilder.Describe(fv);
        }

        /// <summary>读门闩；cleanupForbidsReopen 时禁止连接。</summary>
        public bool TryReadSessionFaultLatch(out string category, out string detail, out bool cleanupForbidsReopen)
        {
            category = string.Empty;
            detail = string.Empty;
            SessionFault fault;
            if (!SessionFault.TryReadAnyLatch(out fault, out cleanupForbidsReopen))
            {
                cleanupForbidsReopen = SessionFault.HasCleanupErrorForbiddingReopen();
                return false;
            }
            category = fault.Category.ToString();
            detail = fault.Detail ?? string.Empty;
            if (fault.Category == SessionFaultCategory.CleanupError)
                cleanupForbidsReopen = true;
            return true;
        }


        /// <summary>
        /// 将台架标定 Apply 到当前会话（仅 CreateLive / FakeLive 内层为 <see cref="LiveAcquisitionSession"/> 时成功）。
        /// 仿真返回 false 并说明；不抛。成功后可更新 <see cref="OwtsRuntimeOptions.BenchCalibrationFilePath"/>（由调用方）。
        /// </summary>
        public bool TryApplyBenchCalibration(LiveBenchCalibration cal, out string error)
        {
            ThrowIfDisposed();
            error = null;
            if (cal == null)
            {
                error = "台架标定对象为空。";
                return false;
            }
            if (!BenchCalibrationEditor.TryValidateBeforeSave(cal, out error))
                return false;
            if (!cal.BenchCalibrated || !cal.AllowsLiveNonZeroEnergize())
            {
                error = StatusCopy.BenchGainNotCalibrated + "：须 BenchCalibrated 且增益非占位 1000 才可 Apply 到 Live。";
                return false;
            }

            bool ok = false;
            var singleton = Acquisition as SingletonAcquisitionSession;
            if (singleton != null)
                ok = singleton.TryApplyBenchCalibration(cal, out error);
            else
            {
                var liveDirect = Acquisition as LiveAcquisitionSession;
                if (liveDirect != null)
                    ok = liveDirect.TryApplyBenchCalibration(cal, out error);
                else
                {
                    error = "当前采集会话不支持台架标定 Apply。";
                    return false;
                }
            }
            if (ok)
            {
                // 连接后可编增益：Apply 即时生效，勿须重连；硬过流对齐出厂加压台架
                var live = TryGetLiveSessionInner();
                if (live != null && live.HardwareOptions != null)
                    live.HardwareOptions.HardOverCurrentA = FactoryBenchHardOverCurrentA;
                error = null;
            }
            return ok;
        }

        /// <summary>从当前 Live/FakeLive 回填编辑页；仿真返回默认占位草案。</summary>
        public LiveBenchCalibration CaptureBenchCalibrationOrDefault()
        {
            ThrowIfDisposed();
            var singleton = Acquisition as SingletonAcquisitionSession;
            if (singleton != null)
            {
                var live = singleton.TryGetLiveSession();
                if (live != null) return live.CaptureBenchCalibration();
            }
            var direct = Acquisition as LiveAcquisitionSession;
            if (direct != null) return direct.CaptureBenchCalibration();
            return new LiveBenchCalibration();
        }

        public bool IsLiveOrFakeLiveSession
        {
            get
            {
                if (_disposed || Acquisition == null) return false;
                var singleton = Acquisition as SingletonAcquisitionSession;
                if (singleton != null) return singleton.TryGetLiveSession() != null;
                return Acquisition is LiveAcquisitionSession;
            }
        }


        LiveAcquisitionSession TryGetLiveSessionInner()
        {
            var singleton = Acquisition as SingletonAcquisitionSession;
            if (singleton != null) return singleton.TryGetLiveSession();
            return Acquisition as LiveAcquisitionSession;
        }

        /// <summary>
        /// Open 之后对齐出厂加压：Live.Open 会用会话选项重置 BenchCalibrationApplied，
        /// 须再 Apply 台架文件/FakeLive 默认增益；硬过流对齐 0.012A。
        /// </summary>
        public string AlignHardwareAfterOpen()
        {
            ThrowIfDisposed();
            var live = TryGetLiveSessionInner();
            if (live == null)
            {
                string pdSim = LoadPdCalibrationOrDefault();
                return StatusCopy.BrandCompany + " · 仿真会话：无 DeviceSdkLease/Pico 对齐项；" + pdSim;
            }

            if (!string.IsNullOrWhiteSpace(Options.BenchCalibrationFilePath)
                && File.Exists(Options.BenchCalibrationFilePath))
            {
                LiveBenchCalibration cal = LiveBenchCalibration.LoadFile(Options.BenchCalibrationFilePath);
                string err;
                if (!live.TryApplyBenchCalibration(cal, out err))
                    return "台架标定 Apply 失败: " + (err ?? "");
            }
            else if (IsFakeLive && !live.BenchCalibrationApplied)
            {
                var cal = LiveBenchCalibration.CreateBenchSample(
                    2000, 12, 0.1, 2000, "FakeLive", StatusCopy.TestBenchGainNote);
                string err;
                if (!live.TryApplyBenchCalibration(cal, out err))
                    return "FakeLive 台架标定 Apply 失败: " + (err ?? "");
            }

            if (live.HardwareOptions != null && live.HardwareOptions.BenchCalibrationApplied)
                live.HardwareOptions.HardOverCurrentA = FactoryBenchHardOverCurrentA;

            string pdMsg = LoadPdCalibrationOrDefault();
            string harden = DescribeAcquisitionHardening();
            string gainHint = live.BenchCalibrationApplied
                ? "台架增益已 Apply（可用「台架标定…」改增益后 Apply，无需重连；HardOC="
                    + FactoryBenchHardOverCurrentA.ToString(CultureInfo.InvariantCulture) + "A）"
                : "台架增益未 Apply — 请用「台架标定…」写入后再非零加压";
            return harden + "；" + pdMsg + "；" + gainHint;
        }

        /// <summary>加载 {AppBase}/config/pd-calibration.json；无效/缺失 → 默认 2000 pC/V。</summary>
        public string LoadPdCalibrationOrDefault()
        {
            ThrowIfDisposed();
            bool fromFile;
            string pathUsed;
            PdCalibrationPcPerVolt = PdCalibrationStore.LoadPcPerVoltOrDefault(out pathUsed, out fromFile);
            PdCalibrationPath = pathUsed ?? PdCalibrationStore.DefaultPath();
            if (fromFile)
                return "pd-calibration 已加载 pC/V="
                    + PdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                    + " ← " + PdCalibrationPath;
            return "pd-calibration 无效或缺失，使用默认 pC/V="
                + PdCalibrationStore.DefaultPdCalibrationPcPerVolt.ToString("G9", CultureInfo.InvariantCulture)
                + "（路径 " + PdCalibrationPath + "）";
        }

        /// <summary>
        /// Xpert 加压页注入已匹配的放电校准系数（pC/V），供 FactoryLocateExtract 与清单进料使用。
        /// 无效值不改写现有系数。
        /// </summary>
        public void ApplyXpertPdCalibrationPcPerVolt(double pcPerVolt)
        {
            ThrowIfDisposed();
            if (!(pcPerVolt > 0) || double.IsNaN(pcPerVolt) || double.IsInfinity(pcPerVolt)) return;
            PdCalibrationPcPerVolt = pcPerVolt;
            PdCalibrationPath = "xpert-discharge-calibration";
        }

        /// <summary>
        /// 加压批次前：清软锁存（对齐 PressTestRunner.PrepareInterlocks 习惯）。
        /// 真急停/未知仍拒绝。
        /// </summary>
        public bool PreparePressBatch(out string detail)
        {
            ThrowIfDisposed();
            return TryClearEmergencyLatchIfHardwareSafe(out detail);
        }

        /// <summary>出厂加压同路径校准：ChannelA Rapid + 自适量程 + 间隔筛选（须急停按下）。</summary>
        public FactoryAlignedCalibrationResult RunFactoryAlignedCalibration(
            CancellationToken cancellation, Action<string> log = null)
        {
            ThrowIfDisposed();
            var result = new FactoryAlignedCalibrationResult();
            var live = TryGetLiveSessionInner();
            if (live == null)
            {
                result.Success = false;
                result.ExitCode = 2;
                result.Message = "非 Live/FakeLive：无 CalibrationPulse/Rapid 路径（仿真请用 0kV RunCycle）。";
                return result;
            }

            // FakeLive：若已解除则自动按下（对齐 PressTestRunner.EnsureEStopPressedForCalibration）
            var st = Acquisition.GetStatus();
            if (IsFakeLive && !st.EStopPressed)
            {
                Acquisition.SetEStopPressed(true);
                LogLine(log, "FakeLive：校准前急停为解除，已自动按下（演示；真机须按下急停后再校准）");
            }
            else if (!st.EStopPressed)
            {
                result.Success = false;
                result.ExitCode = 2;
                result.Message = StatusCopy.CalibNeedsEStopPressed
                    + "（当前「" + StatusCopy.EStopReleased + "」）";
                LogLine(log, "ABORT: " + result.Message);
                return result;
            }

            var req = new CalibrationPulseRequest
            {
                ImpedanceBandwidthWide = Acquisition.ImpedanceBandwidthWide,
                InitialPicoRangeIndex = live.HardwareOptions != null ? live.HardwareOptions.PicoRangeIndex : 6,
                MaxRangeRetries = 2,
                PicoResolutionBits = live.HardwareOptions != null ? live.HardwareOptions.PicoResolutionBits : 14,
                PicoTimebase = live.HardwareOptions != null ? live.HardwareOptions.PicoTimebase : 3u,
                PicoTargetSampleRateHz = 125e6,
                SampleIntervalNanoseconds = LiveHardwareOptions.SampleIntervalNanosecondsFromTimebase(
                    live.HardwareOptions != null ? live.HardwareOptions.PicoTimebase : 3u),
                CableLengthM = CalibrationCaptureTiming.DefaultCableLengthM,
                VelocityMPerUs = CalibrationCaptureTiming.DefaultVelocityMPerUs,
                PeriodToleranceFraction = CalibrationPulseIntervalFilter.DefaultPeriodToleranceFraction
            };

            TextWriter tw = log != null ? (TextWriter)new CallbackTextWriter(log) : TextWriter.Null;
            var runner = new CalibrationPulseAcquisitionRunner();
            CalibrationPulseAcquisitionResult cal = runner.Run(live, req, tw, cancellation);
            result.ExitCode = cal != null ? cal.ExitCode : 3;
            result.Message = cal != null ? (cal.Message ?? string.Empty) : "校准返回空";
            result.RangeIndexUsed = cal != null ? cal.RangeIndexUsed : -1;
            result.AveragedCycle = cal != null ? cal.AveragedCycle : null;
            result.Success = cal != null && cal.Success;
            if (result.Success)
            {
                Acquisition.SetCalibrated(true);
                LogLine(log, "校准采集完成；已标记已校准（≠ 可加压；开始加压仍须急停解除）。");
            }
            return result;
        }

        /// <summary>
        /// 出厂加压同路径连续加压：量程自调重试 + 档间 PicoCaptureCooldown（FakeLive 跳过缓冲等待）。
        /// 调用方须已通过 CanEnterNonZeroPressure / 急停解除门禁。
        /// </summary>
        public IList<FactoryAlignedPressResult> RunFactoryAlignedPress(
            double targetVoltageV,
            int reps,
            CancellationToken cancellation,
            Action<string> log = null,
            int maxRangeRetries = 2,
            int initialRangeIndex = 6)
        {
            ThrowIfDisposed();
            if (reps < 1) reps = 1;
            if (reps > 20) reps = 20;
            if (maxRangeRetries < 1) maxRangeRetries = 1;

            var live = TryGetLiveSessionInner();
            double targetKv = targetVoltageV / 1000.0;
            // 与 PressTestRunner：优先沿用上次成功量程，禁止每轮回 ±1V 原点重爬
            int rangeIndex = ResolveStartingRangeIndex(initialRangeIndex, live, log);
            var list = new List<FactoryAlignedPressResult>();

            LogLine(log, "—— 连续加压开始（自动量程 maxRetries=" + maxRangeRetries
                + " startRange=" + rangeIndex
                + (_lastSuccessfulRangeIndex >= 0
                    ? (" lastOkRange=" + _lastSuccessfulRangeIndex
                        + " lastOkKv=" + (double.IsNaN(_lastSuccessfulRangeKv) ? "NaN"
                            : _lastSuccessfulRangeKv.ToString("0.###", CultureInfo.InvariantCulture)))
                    : "")
                + "）——");

            for (int rep = 1; rep <= reps; rep++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (reps > 1)
                    LogLine(log, "加压轮次 " + rep.ToString(CultureInfo.InvariantCulture)
                        + "/" + reps.ToString(CultureInfo.InvariantCulture));

                // 连续同压：强制沿用该压上次成功量程
                if (_lastSuccessfulRangeIndex >= 0
                    && !double.IsNaN(_lastSuccessfulRangeKv)
                    && Math.Abs(targetKv - _lastSuccessfulRangeKv) < 1e-9
                    && rangeIndex != _lastSuccessfulRangeIndex)
                {
                    LogLine(log, "同压沿用成功量程：目标 "
                        + targetKv.ToString("0.###", CultureInfo.InvariantCulture) + " kV"
                        + " range " + rangeIndex + " → " + _lastSuccessfulRangeIndex);
                    rangeIndex = _lastSuccessfulRangeIndex;
                    ApplyRangeToLive(live, rangeIndex, log);
                }

                if (rep > 1)
                    WaitInterRoundPicoCooldown(live, log, cancellation);

                if (!(targetVoltageV > 0))
                    LogLine(log, "【0V：跳过 HVON/HVUP→噪声采集】");

                FactoryAlignedPressResult one = null;
                int attempt = 0;
                while (true)
                {
                    attempt++;
                    if (cancellation.IsCancellationRequested)
                    {
                        one = FinishOperatorStopResult(null, rangeIndex);
                        list.Add(one);
                        return list;
                    }

                    AcquisitionCycleResult cycle = Acquisition.RunCycleAsync(
                        new AcquisitionCycleRequest
                        {
                            TargetVoltageV = targetVoltageV,
                            SuggestedPicoRangeIndex = rangeIndex
                        },
                        cancellation).GetAwaiter().GetResult();

                    one = new FactoryAlignedPressResult
                    {
                        Cycle = cycle,
                        RangeIndexUsed = rangeIndex
                    };

                    if (cycle != null && cycle.ExitReason == AcquisitionExitReason.OperatorStop)
                    {
                        one.Success = false;
                        one.OperatorStopped = true;
                        one.Message = StatusCopy.OperatorAbortNoLatch;
                        // 丢弃建议量程，避免下游把 Suggested 当升档依据
                        if (cycle != null)
                        {
                            cycle.SuggestedPicoRangeIndex = cycle.PicoRangeIndexUsed >= 0
                                ? cycle.PicoRangeIndexUsed : rangeIndex;
                            cycle.Task6RangeSuitable = true;
                            cycle.Task6RangeMessage = "操作员停止：跳过量程核算/升档重加";
                        }
                        list.Add(one);
                        return list;
                    }

                    if (cancellation.IsCancellationRequested)
                    {
                        one = FinishOperatorStopResult(cycle, rangeIndex);
                        list.Add(one);
                        return list;
                    }

                    if (IsHardSafetyExit(cycle))
                    {
                        one.Success = false;
                        one.HardSafetyAbort = true;
                        one.Message = "安全退出：" + (cycle != null ? cycle.ExitReason.ToString() : "?")
                            + " — " + (cycle != null ? cycle.ErrorMessage : "");
                        list.Add(one);
                        return list;
                    }

                    if (cycle != null && !cycle.Success && IsTransientNonSafetyAbort(cycle) && attempt <= maxRangeRetries)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            one = FinishOperatorStopResult(cycle, rangeIndex);
                            list.Add(one);
                            return list;
                        }
                        LogLine(log, "短暂采集中止（非急停），重试本轮 " + attempt + "/" + maxRangeRetries
                            + "：" + (cycle.ErrorMessage ?? ""));
                        string clr;
                        TryClearEmergencyLatchIfHardwareSafe(out clr);
                        continue;
                    }

                    if (cycle != null && cycle.Success
                        && !cycle.Task6RangeSuitable
                        && cycle.SuggestedPicoRangeIndex >= 0
                        && cycle.SuggestedPicoRangeIndex != rangeIndex
                        && attempt <= maxRangeRetries)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            one = FinishOperatorStopResult(cycle, rangeIndex);
                            list.Add(one);
                            return list;
                        }

                        bool isDecrease = cycle.SuggestedPicoRangeIndex < rangeIndex;
                        // 记住成功档：禁止峰过小盲目降到远低于已成功档
                        if (isDecrease && !cycle.PicoOverrangeOrClipped
                            && _lastSuccessfulRangeIndex >= 0
                            && cycle.SuggestedPicoRangeIndex < _lastSuccessfulRangeIndex)
                        {
                            LogLine(log, "自动量程：拒绝盲目降档 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                                + "（低于已成功档 " + _lastSuccessfulRangeIndex + "；无超量程）；保持 " + rangeIndex
                                + (string.IsNullOrEmpty(cycle.Task6RangeMessage) ? "" : ("；" + cycle.Task6RangeMessage)));
                            break;
                        }
                        // 单次最多接受降 2 档
                        if (isDecrease && !cycle.PicoOverrangeOrClipped
                            && rangeIndex - cycle.SuggestedPicoRangeIndex > 2)
                        {
                            int clamped = Math.Max(0, rangeIndex - 2);
                            if (clamped == 0 && rangeIndex > 2) clamped = Math.Max(1, rangeIndex - 2);
                            LogLine(log, "自动量程：钳制降档 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                                + " 为 " + clamped + "（单次≤2档，禁掉±10mV悬崖）");
                            cycle.SuggestedPicoRangeIndex = clamped;
                            if (clamped == rangeIndex) break;
                        }

                        LogLine(log, "自动量程：索引 " + rangeIndex + " → " + cycle.SuggestedPicoRangeIndex
                            + "（重加本轮 " + attempt + "/" + maxRangeRetries + "）"
                            + (cycle.PicoOverrangeOrClipped ? " [超量程/饱和]" : "")
                            + (string.IsNullOrEmpty(cycle.Task6RangeMessage) ? "" : (" " + cycle.Task6RangeMessage)));
                        rangeIndex = cycle.SuggestedPicoRangeIndex;
                        ApplyRangeToLive(live, rangeIndex, log);
                        continue;
                    }

                    one.Success = cycle != null && cycle.Success;
                    one.Message = one.Success
                        ? ("Exit=" + cycle.ExitReason)
                        : (cycle != null ? (cycle.ErrorMessage ?? cycle.ExitReason.ToString()) : "空结果");
                    // 成功：记住本档实际量程（PicoRangeIndexUsed），不把未验证 Suggested 带入下一档
                    if (one.Success && cycle != null)
                    {
                        int used = cycle.PicoRangeIndexUsed >= 0 ? cycle.PicoRangeIndexUsed : rangeIndex;
                        rangeIndex = used;
                        one.RangeIndexUsed = used;
                        RememberSuccessfulRange(used, targetKv);
                        LastPressCycle = cycle;
                        if (cycle.SuggestedPicoRangeIndex >= 0
                            && cycle.SuggestedPicoRangeIndex != used
                            && !cycle.Task6RangeSuitable)
                        {
                            LogLine(log, "量程记忆：本档成功用 index=" + used
                                + "；Suggested=" + cycle.SuggestedPicoRangeIndex
                                + " 仅作提示，不带入下一档/同压");
                        }
                    }
                    else
                        one.RangeIndexUsed = rangeIndex;
                    list.Add(one);
                    break;
                }

                if (one == null || !one.Success)
                    return list;
            }
            return list;
        }

        int ResolveStartingRangeIndex(int initialRangeIndex, LiveAcquisitionSession live, Action<string> log)
        {
            if (_lastSuccessfulRangeIndex >= 0)
            {
                ApplyRangeToLive(live, _lastSuccessfulRangeIndex, log);
                return _lastSuccessfulRangeIndex;
            }
            int idx = initialRangeIndex >= 0 ? initialRangeIndex : 6;
            return idx;
        }

        void RememberSuccessfulRange(int rangeIndex, double targetKv)
        {
            if (rangeIndex < 0) return;
            _lastSuccessfulRangeIndex = rangeIndex;
            _lastSuccessfulRangeKv = targetKv;
        }

        void ApplyRangeToLive(LiveAcquisitionSession live, int rangeIndex, Action<string> log)
        {
            if (live == null || rangeIndex < 0) return;
            try
            {
                var hw = live.HardwareOptions;
                live.ApplyPicoCaptureSettings(
                    hw != null && hw.PicoResolutionBits > 0 ? hw.PicoResolutionBits : 14,
                    hw != null && hw.PicoTimebase > 0 ? hw.PicoTimebase : 3u,
                    125e6,
                    rangeIndex);
            }
            catch (Exception ex)
            {
                LogLine(log, "沿用成功量程写硬件跳过: " + ex.Message);
            }
        }

        static FactoryAlignedPressResult FinishOperatorStopResult(AcquisitionCycleResult cycle, int rangeIndex)
        {
            if (cycle != null)
            {
                cycle.SuggestedPicoRangeIndex = cycle.PicoRangeIndexUsed >= 0 ? cycle.PicoRangeIndexUsed : rangeIndex;
                cycle.Task6RangeSuitable = true;
                cycle.Task6RangeMessage = "操作员停止：跳过量程核算/升档重加";
            }
            return new FactoryAlignedPressResult
            {
                Success = false,
                OperatorStopped = true,
                Message = StatusCopy.OperatorAbortNoLatch,
                RangeIndexUsed = rangeIndex,
                Cycle = cycle
            };
        }

        /// <summary>
        /// 出厂加压同路径：从 AcquisitionCycleResult 提取定位 LocData（ADC→V，可选 ×pC/V）。
        /// 不改 E+ 公式；对齐 WaveformSummaryWriter 的峰值仓/有效 Fs 约束意图。
        /// </summary>
        public LocationAnalysisRequest BuildLocateRequestFromPressCycle(
            AcquisitionCycleResult cycle,
            double pdCalibrationPcPerVolt = double.NaN)
        {
            ThrowIfDisposed();
            return FactoryLocateExtract.BuildRequest(
                cycle,
                !double.IsNaN(pdCalibrationPcPerVolt) && pdCalibrationPcPerVolt > 0
                    ? pdCalibrationPcPerVolt
                    : PdCalibrationPcPerVolt);
        }

        void WaitInterRoundPicoCooldown(LiveAcquisitionSession live, Action<string> log, CancellationToken cancellation)
        {
            if (IsFakeLive || live == null)
            {
                LogLine(log, "档间：FakeLive/无 Live 跳过 Pico 缓冲等待；下轮由导通起算 2s 门控。");
                return;
            }
            int remain = PicoCaptureCooldown.RemainingMilliseconds;
            if (remain == int.MaxValue)
            {
                LogLine(log, "档间等待 Pico 缓冲释放确认…");
                PicoCaptureCooldown.Wait(cancellation, r =>
                {
                    if (r == int.MaxValue) return;
                    LogLine(log, "Pico 缓冲剩余 " + r + " ms…");
                }, null);
            }
            else if (remain > 0)
            {
                LogLine(log, "档间等 Pico 缓冲 " + remain + " ms…");
                PicoCaptureCooldown.Wait(cancellation, r => LogLine(log, "Pico 缓冲剩余 " + r + " ms…"), null);
            }
            else
                LogLine(log, "档间：Pico 缓冲已释放；下轮由导通起算 2s 门控。");
        }

        static bool IsHardSafetyExit(AcquisitionCycleResult cycle)
        {
            if (cycle == null) return true;
            if (IsTransientNonSafetyAbort(cycle)) return false;
            switch (cycle.ExitReason)
            {
                case AcquisitionExitReason.EmergencyStop:
                case AcquisitionExitReason.HardOverCurrent:
                case AcquisitionExitReason.OverVoltage:
                    return true;
                default:
                    return false;
            }
        }

        static bool IsTransientNonSafetyAbort(AcquisitionCycleResult cycle)
        {
            if (cycle == null || cycle.Success) return false;
            string m = cycle.ErrorMessage ?? "";
            if (m.IndexOf("非急停中止", StringComparison.Ordinal) < 0
                && m.IndexOf("缓存失效", StringComparison.Ordinal) < 0)
                return false;
            if (cycle.ExitReason == AcquisitionExitReason.HardOverCurrent
                || cycle.ExitReason == AcquisitionExitReason.OverVoltage)
                return false;
            return true;
        }

        static void LogLine(Action<string> log, string line)
        {
            if (log != null && !string.IsNullOrEmpty(line)) log(line);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Acquisition != null) Acquisition.Dispose();
            if (DefectRecognition != null) DefectRecognition.Dispose();
            // DefectRecognizer disposed by OnnxBacked service when shared; avoid double-dispose if separate
            DefectRecognizer = null;
            if (FirstZeroPhase != null) FirstZeroPhase.Dispose();
            FirstZeroPhase = null;
            if (Repository != null) Repository.Dispose();
            Acquisition = null;
            DefectRecognition = null;
            Repository = null;
            Workspace = null;
            ReportExporter = null;
            StatusBoard = null;
        }

        void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(OwtsServices));
        }
    }

/// <summary>DesktopUi / HostAdapters：出厂加压同路径校准结果。</summary>
    public sealed class FactoryAlignedCalibrationResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; }
        public int RangeIndexUsed { get; set; }
        public AcquisitionCycleResult AveragedCycle { get; set; }

        public FactoryAlignedCalibrationResult()
        {
            Message = string.Empty;
            RangeIndexUsed = -1;
        }
    }

    /// <summary>DesktopUi / HostAdapters：出厂加压同路径单轮加压结果。</summary>
    public sealed class FactoryAlignedPressResult
    {
        public bool Success { get; set; }
        public bool OperatorStopped { get; set; }
        public bool HardSafetyAbort { get; set; }
        public string Message { get; set; }
        public int RangeIndexUsed { get; set; }
        public AcquisitionCycleResult Cycle { get; set; }

        public FactoryAlignedPressResult()
        {
            Message = string.Empty;
            RangeIndexUsed = -1;
        }
    }

    /// <summary>把 Action&lt;string&gt; 接到 CalibrationPulse 的 TextWriter 日志。</summary>
    sealed class CallbackTextWriter : TextWriter
    {
        readonly Action<string> _write;
        public CallbackTextWriter(Action<string> write) { _write = write; }
        public override Encoding Encoding { get { return Encoding.UTF8; } }
        public override void WriteLine(string value)
        {
            if (_write != null) _write(value ?? string.Empty);
        }
        public override void Write(char value) { }
    }
}
