namespace Xiongxin.Owts.Contracts.Ui
{
    /// <summary>
    /// 定稿状态文案（见 docs/glossary、docs/ui-status-conventions.md）。
    /// 现网壳不改；重编侧统一从此取值，禁止把「已连接/联锁」写成「可加压」。
    /// </summary>
    public static class StatusCopy
    {
        public const string Connected = "已连接";
        public const string Calibrated = "已校准";
        public const string ReadyToEnergize = "可加压";
        public const string EStopPressed = "急停按下";
        public const string EStopReleased = "急停解除";

        public const string PowerInput = "电源输入";
        public const string PowerSwitch = "电源开关";

        public const string ConnectedNotEnergize =
            "设备已连接；非零加压前须确认已校准、急停解除及本轮现场条件";
        public const string InterlockNotEnergize =
            "联锁条件之一已满足；是否「可加压」须另核校准与现场确认";
        public const string ZeroKvNoiseOnly =
            "0 kV 噪声对照（不算「可加压」达标）";
        /// <summary>
        /// 校准备急停按下。急停解除（高电平）时禁止校准采集。
        /// </summary>
        public const string CalibNeedsEStopPressed =
            "急停已解除，无法校准采集；请先按下急停后再校准";
        /// <summary>历史别名：现语义为须急停按下（与 CalibNeedsEStopPressed 同文）。</summary>
        public const string CalibNeedsEStopReleased = CalibNeedsEStopPressed;
        /// <summary>进入校准轮：须已连接且急停按下。</summary>
        public const string CalibNeedsConnected =
            "校准=Pico 采集中；须已连接且急停按下；自适量程→最佳档→脉冲触发；阻抗可随时调节";
        /// <summary>筛选/平均后无有效校准脉冲（空波形或峰过低）。</summary>
        public const string NoCalibrationWaveform =
            "未检测到校准波形，校准源未开机或电量低";
        /// <summary>顶栏第三态：Pico 正在做校准采集。</summary>
        public const string Calibrating = "校准中";
        public const string NotCalibrated = "未校准";
        public const string DeviceNotConnected =
            "设备尚未连接或连接未确认，请检查连接";
        /// <summary>阻抗板带宽（P1.2）可随时调节，校准采集不锁死。</summary>
        public const string ImpedanceAdjustableAnytime =
            "阻抗端口可随时调节（校准采集中亦不锁死）";

        /// <summary>残压/放电确认：未通过时禁止任何「可接近」暗示。</summary>
        public const string DischargeNotVerified =
            "未放电确认 ≠ 可接近；HV 关闭、AO=0 仍不等于已安全放电";
        public const string DischargeVerifiedSafe =
            "残压已降至安全阈值（仍须遵守现场规程，≠ 可加压）";
        public const string DischargeResidualHigh =
            "残压超限：未确认放电，禁止靠近设备";
        public const string GroundReleaseNeedsConfirm =
            "接地保持中：须操作员确认后才能解除接地；未确认 ≠ 可接近";
        public const string GroundReleasedAfterConfirm =
            "已确认解除接地（仍须遵守现场规程；≠ 可加压、≠ 可接近试品前的最终许可）";
        public const string GroundReleaseDenied =
            "拒绝解除接地：未勾选操作员确认（operatorAcknowledged=false）";
        public const string HoldGroundActive =
            "接地保持中（残压未确认）；未放电确认 ≠ 可接近";
        public const string DischargeVerifyTimeout =
            "接地后残余电压未在超时时间内降至安全阈值；未放电确认 ≠ 可接近";

        /// <summary>现场规程文档相对仓库根路径（DesktopUi/帮助链引用）。</summary>
        public const string GroundReleaseProcedureRelPath =
            "docs/GROUND_RELEASE_PROCEDURE.md";
        /// <summary>解除接地对话框/帮助摘要：链到规程要点，不暗示可接近。</summary>
        public const string GroundReleaseProcedureHint =
            "现场解除接地须按 docs/GROUND_RELEASE_PROCEDURE.md："
            + "急停确认→残压确认→HoldGround→软件双确认→可接近条件齐全；"
            + "软件确认 ≠ 可接近许可";

        /// <summary>Live 非零加压：台架增益/分压仍为危险占位（如 VoltageGain=1000）时拒绝。</summary>
        public const string BenchGainNotCalibrated =
            "未台架标定禁止真高压";
        public const string BenchGainNotCalibratedDetail =
            "未台架标定禁止真高压：VoltageGain/DividerRatio 仍为危险默认占位，须从工程配置写入台架标定后方可非零加压（仿真可用占位增益）";

        /// <summary>AI2（源 VMON）工程单位：未写入 SourceVoltageScaleVPerV。</summary>
        public const string Ai2SourceScaleNotCalibrated =
            "AI2源反馈未标定（SourceVoltageScaleVPerV≤0）；工程 kV/V 不可靠";
        /// <summary>AI2 已按台架比例换算。</summary>
        public const string Ai2SourceScaleApplied =
            "AI2源反馈已按 SourceVoltageScaleVPerV 换算为工程电压";
        /// <summary>未标定时残压监测回退 VoltageGain（仅参考）。</summary>
        public const string Ai2SourceScaleFallbackGain =
            "AI2源反馈未标定，残压监测回退 VoltageGain（工程值仅供参考；≠ 已标定）";

        /// <summary>五态「已校准」= PD/Pico 电量校准轮；≠ 台架 VoltageGain。</summary>
        public const string PdCalibrationNotBench =
            "「已校准」仅表示 PD/Pico 电量校准轮完成；≠ 台架 VoltageGain/DividerRatio";
        /// <summary>台架未 Apply 到 Live 会话。</summary>
        public const string BenchNotApplied = "未写入";
        /// <summary>台架已 Apply。</summary>
        public const string BenchApplied = "已写入";
        /// <summary>加压测试用台架增益说明（数值见 HvPressTest/BenchCalibrationEditor TestBench 2000）。</summary>
        public const string TestBenchGainNote =
            "TEST_BENCH_ONLY VoltageGainVPerAoVolt=2000 — 加压测试台架标定，绕过危险默认 1000；非现网壳默认。";

        /// <summary>加压中急停/异常：已 HV OFF，并尝试 Pico 同步触发采集。</summary>
        public const string PressAbortHvOffPicoCapture =
            "加压异常：已推入 HV OFF（P0.0/P0.3 同步断开），并尝试 Pico 同步触发采集";
        /// <summary>整机掉线：无法做 Pico 同步采集。</summary>
        public const string PressAbortDeviceOffline =
            "加压异常：设备整机掉线，跳过 Pico 同步采集；已尽力关源";
        /// <summary>仅 Pico 通讯异常：加压控制继续，建议重新加压。</summary>
        public const string PicoCommAnomalyRetry =
            "可能通讯异常（仅 Pico）：加压控制继续；已内部关闭并重开 Pico；建议重新加压";

        /// <summary>Pico 策略：仅 SDK，抑制官方启动窗，不启 PicoScope.exe。</summary>
        public const string PicoSdkOnlyNoSplash =
            "Pico：仅 SDK，抑制启动窗；不启 PicoScope.exe";
        /// <summary>FakeLive：硬化 LiveAcquisitionSession 路径，无真实驱动。</summary>
        public const string FakeLiveModeHint =
            "FakeLive：硬化采集路径（LiveAcquisitionSession+Fake 后端），无驱动；≠ 实机";
        /// <summary>ART+Pico 双身份通过。</summary>
        public const string DualIdentityOk =
            "双设备身份校验通过（ART+Pico）";
        /// <summary>ART+Pico 双身份失败。</summary>
        public const string DualIdentityMismatch =
            "双设备身份校验失败：ART/Pico 与配置不一致";
        /// <summary>未配置期望序列号时不阻断。</summary>
        public const string DualIdentityNotConfigured =
            "双设备身份：未配置期望 ART/Pico 序列号（跳过强制校验）";

        /// <summary>公司品牌（界面/窗口/报告页脚）。</summary>
        public const string BrandCompany = "雄新电力";
        /// <summary>产品线短名（可与公司名并列）。</summary>
        public const string BrandProduct = "雄新 OWTS";
        /// <summary>窗口标题前缀：雄新电力 · 雄新 OWTS。</summary>
        public const string BrandWindowPrefix = "雄新电力 · 雄新 OWTS";

        /// <summary>DesktopUi / Host 日志：硬化采集路径摘要（lease + DI 拆锁 + Pico 冷却）。</summary>
        public const string HardeningLivePathHint =
            "硬化采集：DeviceSdkLease 独占 + P0.4 DI 独立 _diGate + IdleDiRebuild + AO新鲜反馈窗 + PicoCaptureCooldown；Pico 单次 Open；"
            + PicoSdkOnlyNoSplash;

        /// <summary>清理失败后禁止再 Open（进程门闩）。</summary>
        public const string CleanupFailedNoReopen =
            "设备清理失败，禁止同进程再次 Open；请退出进程并核查硬件关断状态";

        /// <summary>UI 停止（OperatorAbort）：关源，不永久锁存；≠ 真急停。</summary>
        public const string OperatorAbortNoLatch =
            "操作员停止：已关源（未永久锁存；真 P0.4 仍独立监视）";
        /// <summary>软件急停锁存仍在（硬件可能已解除）。</summary>
        public const string SoftwareEmergencyLatch =
            "软件急停锁存";
        /// <summary>锁存已清（P0.4 解除且源关）。</summary>
        public const string SoftwareEmergencyLatchCleared =
            "已清软件急停锁存（P0.4 解除且源关断）";
    }
}
