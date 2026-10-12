using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        private ContextMenuStrip accessMenu;
        private readonly Dictionary<Control, string> settingsCaptions = new Dictionary<Control, string>();
        private bool applyingSettingsLanguage;
        private static readonly Dictionary<string, string> SettingsEnglish = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "定位评分：可疑下限 / %", "Location score: suspect minimum / %" },
            { "定位评分：手动接受下限 / %", "Location score: manual acceptance minimum / %" },
            { "定位评分：自动接受下限 / %", "Location score: automatic acceptance minimum / %" },
            { "定位评分限值须满足 0 ≤ 可疑下限 ≤ 手动接受下限 ≤ 自动接受下限 ≤ 100。", "Location score limits must satisfy 0 ≤ suspect ≤ manual acceptance ≤ automatic acceptance ≤ 100." },
            { "已核对振荡回路连接（启用振荡指标和量程窗口；不是加压许可）", "Oscillation circuit checked (enables metrics and range window; not permission to energize)" },
            { "首过零后的量程评估周期数（1–5，默认 1）", "Range evaluation cycles after first zero (1–5, default 1)" },
            { "建议保留 1 个周期。较多周期会增加内存占用和处理时间；最多 5 个，数据不足时仅保留实际可用数据。", "Keeping 1 cycle is recommended. More cycles use more memory and take longer to process. The maximum is 5; when less data is available, keep only the available data." },
            { "分析 0 kV 数据（关闭时仅保存为噪声参考）", "Analyze 0 kV data (otherwise save as noise reference only)" },
            { "自动量程建议每次提醒", "Prompt for every automatic range recommendation" },
            { "显示普通自动量程建议（重新勾选可恢复）", "Show ordinary automatic range advice (check to restore)" },
            { "关闭提醒后，仅在已开始的本轮内按可用量程自动调整；不能调整或无法判断时保留数据并停止后续轮次。重新勾选可恢复普通提醒；修改本设置不启动采集或加压。", "When reminders are disabled, available ranges may be adjusted only within the round already started. If adjustment is unavailable or cannot be assessed, retain the data and stop future rounds. Check again to restore ordinary reminders; changing this setting does not start acquisition or pressure output." },
            { "加压速度", "Ramp speed" },
            { "普通加压速度：AO步长 / V", "Normal ramp: AO step / V" },
            { "普通加压速度：循环请求间隔 / ms", "Normal ramp: requested interval / ms" },
            { "加压前后不进行软件残压测量或残压等待。仍执行实际 AO 归零、高压关闭及操作结果核对；软件不据此宣称已放电。", "No software residual measurement or residual wait before/after pressure. Actual AO zero, HV OFF and operation results remain required; this does not certify discharge." },
            { "两项共同控制普通升压速度；实际速度受电压反馈和设备响应影响，不是恒定kV/s。精细化加压采用独立调节，并保留更小步长及更长间隔限制。修改不会启动加压，也不改变限压及急停保护。", "Both settings control the normal ramp. Actual speed depends on voltage feedback and device response, not a constant kV/s. Fine ramp uses separate control while retaining smaller-step and longer-interval limits. Changes do not start output or alter voltage limits or emergency stop." },
            { "关闭每次提醒时，普通量程建议自动用于下一轮；异常量程仍提示。修改本设置不启动采集或加压。", "When disabled, ordinary range recommendations apply to the next round automatically; abnormal ranges still prompt. Changing this setting does not start acquisition or energize the equipment." },
            { "自动搜索并连接", "Search and connect" },
            { "仅有一组可用设备时自动连接；多组时请选择。连接后自动更新设备信息，不启动采集或加压。", "Connect automatically when one device is available; choose when several are available. Device details are updated without starting acquisition or high-voltage output." },
            { "设备已连接，连接信息已更新。其他修改尚未保存，请核对后保存设置。", "Device connected and connection details updated. Other changes remain unsaved; review and save the settings." },
            { "未完成连接，原有修改已保留。可重试搜索或检查设备状态。", "Connection was not completed. Your edits were retained; retry the search or check device status." },
            { "参数设置", "Settings" }, { "保存设置", "Save settings" }, { "取消", "Cancel" },
            { "常规", "General" }, { "报告", "Report" }, { "全局语言", "Language" },
            { "数据根目录", "Data folder" }, { "高级设置…", "Advanced settings…" }, { "浏览…", "Browse…" },
            { "定位对照 C…", "Location comparison C…" },
            { "设备关键参数", "Critical device settings" }, { "厂家维护", "Manufacturer maintenance" },
            { "厂家 OEM", "Manufacturer OEM" }, { "系统管理", "System administration" },
            { "系统管理 / 日志与端口", "System administration / Logs and ports" },
            { "分压、加压、校准修正及最高限压等设备参数需要授权后设置。", "Authorization is required to change voltage ratios, calibration correction and the voltage limit." },
            { "常规分析参数及程序路径需要维护授权后设置；指定高级参数已移至 HV DAC Xpert Plus Data。", "Maintenance authorization is required to change analysis settings and program paths. Additional settings are available in HV DAC Xpert Plus Data." },
            { "软件标识和品牌信息需要厂家授权后设置。", "Manufacturer authorization is required to change software identity and branding." },
            { "日志及硬件端口需要独立管理授权后查看和设置。", "Administration authorization is required to view logs and configure device ports." },
            { "AI、缺陷识别、模型与指定分析采样参数需要独立授权后设置。", "Authorization is required to change AI, defect recognition, models and acquisition settings." },
            { "输入授权口令…", "Enter authorization password…" },
            { "此页设置需要维护授权。关闭设置窗口后需重新授权。", "Maintenance authorization is required. Closing this settings window ends the authorization." },
            { "报告输出目录", "Report output folder" }, { "报告出具版式", "Report format" },
            { "仅 PDF", "PDF only" }, { "仅 Word", "Word only" }, { "PDF 和 Word", "PDF and Word" },
            { "同步生成国家电网标准格式报告", "Also generate a State Grid format report" },
            { "参照用户提供的 Q/GDW 11262—2024 附录 M，额外生成国家电网格式报告，不替换现有报告。默认关闭；启用后仍需核对报告内容和适用要求。", "Generate an additional State Grid report based on Q/GDW 11262—2024, Appendix M. Existing reports are retained. Check the content and applicable requirements before use." },
            { "软件型号", "Software model" }, { "窗口标题（留空跟随语言）", "Window title (blank uses the selected language)" },
            { "Logo 文件（留空不显示）", "Logo file (blank hides logo)" }, { "窗口图标（留空使用中性图标）", "Window icon (blank uses neutral icon)" },
            { "仅使用本机图片。保存后保留一份副本；更改会应用于本软件窗口。程序文件和快捷方式图标由安装版本决定。", "Use local images. A copy is kept when saved and applied to application windows. Executable and shortcut icons belong to the installed edition." },
            { "说明书路径", "User manual file" }, { "界面版权声明", "Copyright notice" },
            { "设备参数仅限授权人员设置。修改后须核验设备；未经设备核验的参数不会下发，连接设备不代表允许加压。", "Only authorized personnel may change device settings. Verify the device after changes. Unverified settings are not sent to the device; a connection does not authorize high voltage." },
            { "设备参数按 ART 与 Pico 序列号组合保存。首次连接请完成独立设备配置；保存不会启动加压。", "Device settings are saved for each ART and Pico serial-number pair. Complete device setup on the first connection; saving does not start high-voltage output." },
            { "分压比", "Voltage divider ratio" }, { "加压比", "Voltage gain" }, { "校准系数修正（默认 1）", "Calibration correction (default 1)" },
            { "触发阈值 / V（Pico EXT）", "Trigger threshold / V (Pico EXT)" },
            { "电感量 / H", "Inductance / H" }, { "电感直流电阻 / Ω", "Inductor DC resistance / Ω" }, { "最高限压 / kV", "Voltage limit / kV" },
            { "余弦超低频", "Cosine VLF" }, { "综合超低频", "Combined VLF" }, { "耐压设备能力", "Withstand test capabilities" },
            { "余弦：余弦超低频、直流耐压、护套耐压；综合：另包含正弦超低频。\r\n两项均关闭时，首页耐压入口灰显。首次每项目每相先做低压振荡波测电容：限压30 kV → 1 kV；60 kV → 3 kV。其他限压自动估算并明确标注；实机使用前须由厂家核对。\r\n勾选设备能力不会开启实机高压。", "Cosine VLF supports cosine VLF, DC and sheath tests; combined VLF also supports sine VLF.\r\nThe withstand test is unavailable when neither capability is selected. First measure capacitance at low voltage for each project phase: 1 kV for a 30 kV limit, or 3 kV for a 60 kV limit. Other limits use a marked estimate that requires manufacturer verification.\r\nSelecting a capability does not enable live high voltage." },
            { "局放采集长度 / ms", "PD acquisition duration / ms" }, { "电压反馈", "Voltage feedback" },
            { "功能开关", "Features" }, { "采样与分析参数", "Acquisition and analysis" }, { "首末端组合规则", "Endpoint identification" }, { "AI 模型加载", "AI models" },
            { "启用 AI 学习与分析建议", "Enable AI learning and analysis suggestions" }, { "启用缺陷识别（需同时启用 AI）", "Enable defect recognition (requires AI)" },
            { "显示独立缺陷识别模块入口", "Show the separate defect recognition tool" },
            { "请先选择项目，再打开独立缺陷识别。隐藏入口不会关闭已打开的窗口；独立窗口中的设置单独保存。", "Select a project before opening the separate defect recognition tool. Hiding its entry does not close an open window. Its settings are saved separately." },
            { "保存设置不会连接设备或开始试验。关闭缺陷识别后仍可进行常规数据分析。", "Saving settings does not connect devices or start a test. Regular analysis remains available when defect recognition is off." },
            { "局放采样率 / Hz", "PD sample rate / Hz" }, { "采样位数 / bit", "Sample resolution / bit" },
            { "采样参数须符合设备能力，请勿超出设备允许范围。", "Acquisition settings must remain within the device's supported range." },
            { "最大脉冲数", "Maximum pulses" }, { "校准平均次数 N", "Calibration averaging count N" }, { "干扰模板相关容差 / %", "Interference correlation tolerance / %" },
            { "默认 1%，相似度 ≥99% 的脉冲排除在校准/定位提取之外，原始数据保留。0% 表示仅排除完全相关的脉冲，并非关闭过滤。", "Default: 1%. Pulses with at least 99% similarity are excluded from calibration and location extraction; raw data is retained. At 0%, only fully correlated pulses are excluded; filtering remains active." },
            { "多重反射抑制次数（-1 自动）", "Reflection suppression count (-1: automatic)" }, { "定位方法", "Location method" },
            { "定位过滤算法无效，请检查设置。", "The location filter algorithm is invalid. Check the settings." },
            { "更改定位方法后，请重新分析数据。原有分析结果保持不变。", "Reanalyze data after changing the location method. Existing analysis results remain unchanged." },
            { "原定位方法已停用，请选择 A 或 B 后保存。原有分析结果保持不变。", "The previous location method is no longer available. Select A or B before saving. Existing analysis results remain unchanged." },
            { "分析模型库路径", "Analysis model folder or file" }, { "分析模型库路径留空时使用主程序随包模型；不会读取或替换独立缺陷识别程序的模型设置。", "Leave blank to use the bundled analysis model. The separate defect recognition tool's model settings are unchanged." },
            { "最终统一分析模型路径", "Combined analysis model file" }, { "扩展模型仅在对应功能支持时使用。", "Additional models are used only by features that support them." },
            { "缺陷识别模型", "Defect recognition model" }, { "标准模型文件（可选）", "Standard model file (optional)" }, { "竞赛模型文件（可选）", "Competition model file (optional)" },
            { "标准模型", "Standard model" }, { "竞赛模型", "Competition model" },
            { "文件路径留空时使用已安装的对应模型。模型选择同时用于数据分析和加压后的缺陷识别；独立缺陷识别窗口仍单独保存设置。", "Empty paths use the corresponding installed model. The selection applies to analysis and post-pressure recognition; the independent window keeps its own preferences." },
            { "扩展模型 A 路径", "Additional model A" }, { "扩展模型 B 路径", "Additional model B" }, { "扩展模型 C 路径", "Additional model C" }, { "扩展模型 D 路径", "Additional model D" },
            { "首末端识别组合规则：原始波形、校准、采样率等质量检查始终有效。", "Combine the endpoint criteria below. Waveform, calibration and sample-rate quality checks always remain active." },
            { "① 反射主瓣分裂；与②的关系", "① Split reflection main lobe; combine with ②" }, { "② 入射半高宽 ≥ 校准半高宽 ×", "② Incident FWHM ≥ calibration FWHM ×" },
            { "①②组合与③的关系", "Combine (①, ②) with ③" }, { "③ 入射正反峰比 ≤ 校准峰比 ÷", "③ Incident polarity ratio ≤ calibration ratio ÷" },
            { "组合顺序：(① 与/或 ②) 与/或 ③。条件未满足不直接判作近端；证据缺失保留待判断。已有结果须重新分析。", "Order: (① AND/OR ②) AND/OR ③. Unmet criteria do not prove a near-end location. Missing evidence remains undetermined. Reanalyze existing results after changes." },
            { "或（任一满足）", "OR (either criterion)" }, { "与（同时满足）", "AND (both criteria)" },
            { "以下参数用于统一分析流程；语言跟随“常规”页的全局语言设置。", "These settings apply to data analysis." },
            { "衰减容差 / %", "Attenuation tolerance / %" }, { "近似接头误差 / %", "Joint position tolerance / %" },
            { "接头范围：接头位置 ± 当前相电缆长度 × 误差 ÷ 100。输入 1 表示 ±1%；结论使用近似后的结果。", "Joint range: position ± phase cable length × tolerance / 100. A value of 1 means ±1%. Conclusions use this joint range." },
            { "相关误差 / %", "Correlation tolerance / %" }, { "自动阈值倍数", "Automatic threshold multiplier" },
            { "校准、匹配、波速、长度与定位统一使用首达时序", "Use first-arrival timing for calibration, matching, velocity, length and location" }, { "首达统一计时模式", "First-arrival timing" },
            { "启用相位过滤", "Enable phase filtering" }, { "相位过滤开关", "Phase filtering" }, { "相位象限", "Phase quadrants" }, { "分析参数", "Analysis settings" },
            { "采集组件路径（默认使用随包组件）", "Acquisition component (blank uses the bundled component)" }, { "报告模块 / 程序路径", "Report component or program" }, { "报告模板路径", "Report template file" }, { "程序与报告路径", "Program and report files" },
            { "厂家维护：设备身份、驱动与接入程序的设置仅供授权人员使用。\r\n启用连接检查不会自动加压或开始试验。更改以下字段后请重新检查设备状态。", "Device identity, driver and connection settings are for authorized personnel only.\r\nConnection checks do not apply high voltage or start a test. Check device status again after changing these settings." },
            { "启用指定设备的连接检查（默认关闭 / 离线）", "Enable configured device connection checks (off by default)" }, { "硬件模式", "Device mode" },
            { "设备网络地址 / MAC", "Device MAC address" }, { "控制模块位置（身份校验）", "Controller location (identity check)" }, { "采集模块位置（身份校验）", "Acquisition device location (identity check)" },
            { "控制模块序列号（追溯备注）", "Controller serial number (reference)" }, { "采集模块序列号（追溯备注）", "Acquisition serial number (reference)" }, { "排除的直连设备序列号（追溯备注）", "Excluded directly connected device serial number (reference)" },
            { "驱动安装标记路径", "Driver installation record" }, { "驱动包路径", "Driver package" }, { "硬件状态 JSON 路径", "Device status file (JSON)" }, { "连接器程序路径（兼容）", "Connection program (compatibility)" },
            { "设备连接程序路径", "Device connection program" }, { "首次配置工具（可能需要管理员权限）", "Initial setup tool (may require administrator permission)" }, { "驱动安装界面程序", "Driver installer" }, { "零电压采集设置", "Zero-voltage acquisition settings" },
            { "高级 / 硬件", "Advanced / Device" }, { "软件日志", "Software logs" }, { "项目建立记录 / 操作记录", "Project / operation records" },
            { "打开日志目录", "Open log folder" }, { "浏览操作记录", "View operation records" }, { "浏览软件日志…", "Browse software logs…" },
            { "振荡频率正常下限 / Hz", "Frequency normal minimum / Hz" }, { "振荡频率正常上限 / Hz", "Frequency normal maximum / Hz" },
            { "振荡频率红色警告阈值 / Hz", "Frequency danger threshold / Hz" }, { "电容正常下限 / μF", "Capacitance normal minimum / μF" }, { "电容正常上限 / μF", "Capacitance normal maximum / μF" },
            { "补偿电容 / μF（200 nF = 0.2）", "Compensation / μF (200 nF = 0.2)" },
            { "使用补偿电容（显示电容 = 原始电容 − 补偿电容）", "Use compensation capacitance (display = raw − compensation)" },
            { "显示振荡频率异常提醒（重新勾选可恢复）", "Show abnormal frequency reminder (check to restore)" },
            { "显示两次自动加压之间的倒计时窗口", "Show the countdown between automatic pressure requests" },
            { "显示自动加压倒计时（重新勾选可恢复）", "Show automatic pressure countdown (check to restore)" },
            { "自动加压倒计时 / 秒（1–10）", "Automatic pressure countdown / s (1–10)" },
            { "自动加压间隔 / 秒（0–10）", "Automatic pressure interval / s (0–10)" }, { "定位保存周期数（整数 1–5，默认 1）", "Location save cycles (integer 1–5, default 1)" },
            { "进入新建项目时显示三页操作引导（重新勾选可恢复）", "Show the three-page new project guide (check to restore)" },
            { "0 秒不等待且不弹出倒计时。隐藏倒计时仅隐藏窗口，仍保留所设间隔。关闭提醒不改变设备安全联锁，不启动自动高压流程。", "0 seconds skips both the delay and countdown window. Hiding the countdown preserves the configured interval. Hiding reminders never disables interlocks or starts an automatic high-voltage sequence." },
            { "数据根目录不是有效路径。", "The data folder path is invalid." }, { "分压比必须是大于 0 的有限数值。", "The voltage divider ratio must be finite and greater than 0." },
            { "加压比必须是大于 0 的有限数值。", "The voltage gain must be finite and greater than 0." }, { "局放采样率必须在 1 Hz 到 1 THz 之间。", "The PD sample rate must be between 1 Hz and 1 THz." },
            { "采样位数必须在 1 到 64 位之间。", "Sample resolution must be between 1 and 64 bits." }, { "校准系数修正必须是大于 0 的有限数值。", "Calibration correction must be finite and greater than 0." },
            { "电感量必须是大于 0 的有限数值。", "Inductance must be finite and greater than 0." }, { "电感直流电阻必须是非负有限数值。", "Inductor DC resistance must be finite and nonnegative." },
            { "最高限压必须是大于 0 的有限数值。", "The voltage limit must be finite and greater than 0." }, { "局放采集长度必须在 0.001 ms 到 24 小时之间。", "PD acquisition duration must be between 0.001 ms and 24 hours." },
            { "局放采集长度必须在 0.001 ms 到 60 ms 之间。", "PD acquisition duration must be between 0.001 ms and 60 ms." },
            { "振荡频率阈值须满足 0 < 下限 < 上限 < 红色警告阈值（最大 1 MHz）。", "Frequency limits must satisfy 0 < minimum < maximum < danger threshold, up to 1 MHz." },
            { "电容正常范围须满足 0 < 下限 < 上限（单位 μF）。", "Capacitance limits must satisfy 0 < minimum < maximum, in μF." },
            { "自动加压间隔倒计时必须为 1 至 10 秒。", "The automatic pressure interval must be between 1 and 10 seconds." }, { "补偿电容须为非负有限数值（单位 μF）。", "Compensation capacitance must be finite and nonnegative, in μF." },
            { "定位保存周期数必须为 1 至 5 的整数。", "Location save cycles must be an integer from 1 to 5." }, { "量程评估周期数必须为 1 至 5 的整数。", "Range evaluation cycles must be an integer from 1 to 5." }, { "电压反馈源无效，请选择 DIV 或 Vmp。", "The voltage feedback source is invalid. Select DIV or Vmp." },
            { "报告出具版式无效。", "The report format is invalid." }, { "全局语言代码无效。", "The language selection is invalid." }, { "配置中包含无效的文件或目录路径。", "A file or folder path is invalid." },
            { "分析参数缺失。", "Analysis settings are missing." }, { "衰减容差必须在 0% 到 100% 之间。", "Attenuation tolerance must be between 0% and 100%." }, { "近似接头误差必须在 0% 到 100% 之间。", "Joint position tolerance must be between 0% and 100%." },
            { "干扰模板相关容差必须在 0%～10% 之间。", "Interference correlation tolerance must be between 0% and 10%." }, { "校准平均次数 N 必须为 3～256。", "Calibration averaging count N must be between 3 and 256." },
            { "相关误差必须在 0% 到 100% 之间。", "Correlation tolerance must be between 0% and 100%." }, { "自动阈值倍数必须在 0 到 1000 之间。", "The automatic threshold multiplier must be between 0 and 1000." },
            { "最大脉冲数必须在 1 到 100 之间。", "Maximum pulses must be between 1 and 100." }, { "多重反射抑制次数必须为 -1（自动）或 0 到 100。", "Reflection suppression must be -1 (automatic), or from 0 to 100." },
            { "定位方法设置无效，请重新选择 A 或 B。", "The location method is invalid. Select A or B." }, { "模型库路径无效。", "The model path is invalid." }, { "硬件配置缺失。", "Device settings are missing." },
            { "设备网络地址必须为 12 位十六进制字符。", "The device MAC address must contain 12 hexadecimal characters." }, { "启用硬件模式时必须指定设备网络地址。", "Specify the device MAC address before enabling device mode." },
            { "启用硬件模式时必须指定 Controller Location。", "Specify the controller location before enabling device mode." }, { "启用硬件模式时必须指定 Acquisition Location。", "Specify the acquisition device location before enabling device mode." },
            { "采集设备序列号不能与排除设备序列号相同。", "The selected and excluded acquisition serial numbers must differ." }, { "高级硬件配置中包含无效路径。", "A device settings file path is invalid." },
            { "首末端组合规则无效：两倍率须为 1～100 的有限数值，逻辑关系须为 AND 或 OR；未使用默认值替代。", "Endpoint criteria are invalid: both multipliers must be finite and between 1 and 100, with AND or OR operators. Existing settings have not been replaced." },
            { "所有文件", "All files" }, { "模型文件", "Model files" }, { "图片文件", "Image files" }, { "帮助文档", "Help documents" },
            { "程序或配置", "Programs or settings" }, { "程序或程序集", "Programs or assemblies" }, { "报告模板", "Report templates" },
            { "安装包", "Installation packages" }, { "JSON 文件", "JSON files" }, { "程序", "Programs" }, { "采集设置", "Acquisition settings" }
        };

        private int SettingsLanguageIndex { get { var item = language == null ? null : language.SelectedItem as LanguageItem; return AppLanguages.IndexOf(item == null ? original.LanguageCode : item.Code); } }
        private string SettingsText(string chinese, string english) { return UiLanguage.Text(SettingsLanguageIndex, chinese, english); }
        private string SettingsCaption(string chinese)
        {
            string english;
            return SettingsEnglish.TryGetValue(chinese, out english) ? SettingsText(chinese, english) : UiLanguage.BuiltIn(SettingsLanguageIndex, chinese);
        }
        private void InitializeSettingsLanguage()
        {
            language.SelectedIndexChanged += delegate { ApplySettingsLanguage(); };
            ApplySettingsLanguage();
        }
        private void ApplySettingsLanguage()
        {
            if (applyingSettingsLanguage) return;
            applyingSettingsLanguage = true;
            try
            {
                TranslateSettingsTree(this);
                // Locked pages may not be mounted in the outer tab control yet.
                foreach (var page in new[] { deviceTab, expertTab, oemTab, systemAdminTab, plusDataTab }) if (page != null) TranslateSettingsTree(page);
                RefreshOemLogoInfo();
                ReplaceSettingsChoices(reportFormat, new[] { "仅 PDF", "仅 Word", "PDF 和 Word" });
                ReplaceSettingsChoices(endpointFirstOperator, new[] { "或（任一满足）", "与（同时满足）" });
                ReplaceSettingsChoices(endpointSecondOperator, new[] { "或（任一满足）", "与（同时满足）" });
                ReplaceSettingsChoices(defectModelKind, new[] { "标准模型", "竞赛模型" });
                if (accessMenu != null)
                {
                    var captions = new[] { "设备关键参数", "厂家维护", "厂家 OEM", "系统管理 / 日志与端口", "HV DAC Xpert Plus Data" };
                    for (int i = 0; i < captions.Length; i++) accessMenu.Items[i].Text = SettingsCaption(captions[i]);
                }
            }
            finally { applyingSettingsLanguage = false; }
        }
        private void TranslateSettingsTree(Control root)
        {
            // This label is formatted from current image dimensions and language,
            // not a static caption captured on the first settings-tree pass.
            if(root==oemLogoInfo)return;
            if (root is Label || root is ButtonBase || root is TabPage || root is GroupBox || root is Form)
            {
                string caption;
                if (!settingsCaptions.TryGetValue(root, out caption)) { caption = root.Text; settingsCaptions.Add(root, caption); }
                string next = SettingsCaption(caption);
                if (root.Text != next) root.Text = next;
            }
            // Editable text, paths and numbers are deliberately excluded.
            foreach (Control child in root.Controls) TranslateSettingsTree(child);
        }
        private void ReplaceSettingsChoices(ComboBox box, string[] captions)
        {
            if (box == null) return;
            int selected = box.SelectedIndex;
            for (int i = 0; i < captions.Length; i++) box.Items[i] = SettingsCaption(captions[i]);
            box.SelectedIndex = selected;
        }
        private string TranslateSettingsFilter(string filter)
        {
            var parts = filter.Split('|');
            for (int i = 0; i < parts.Length; i += 2) parts[i] = SettingsCaption(parts[i]);
            return String.Join("|", parts);
        }
    }
}
