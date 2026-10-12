using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal enum SettingsSection
    {
        General,
        Device,
        Expert,
        Oem,
        SystemAdmin,
        PlusData
    }

    internal sealed partial class SettingsForm : Form
    {
        private readonly AppSettings original;
        private TabControl tabs;
        private TabPage deviceTab;
        private TabPage expertTab;
        private TabPage oemTab;
        private TabControl expertTabs;
        private bool deviceUnlocked;
        private bool expertUnlocked;
        private bool oemUnlocked;
        private bool systemAdminUnlocked;
        private TabPage systemAdminTab;
        private readonly HardwareStatusSnapshot reviewedHardware;

        private ComboBox language;
        private TextBox dataRoot;
        private TextBox softwareModel;
        private TextBox windowTitle;
        private TextBox logoPath;
        private TextBox iconPath;
        private bool savingOemAssets;
        private TextBox manualPath;
        private TextBox copyrightText;

        private NumericUpDown dividerRatio;
        private NumericUpDown pressureRatio;
        private NumericUpDown pressureRampStep,pressureRampInterval;
        private NumericUpDown endResidualPercent;
        private NumericUpDown pdSampleRate;
        private NumericUpDown pdSampleBits;
        private NumericUpDown calibrationCorrection;
        private NumericUpDown inductance;
        private NumericUpDown inductorResistance;
        private NumericUpDown maximumVoltage;
        private NumericUpDown deviceTrigger;
        private CheckBox supportsCosineVlf;
        private CheckBox supportsCombinedVlf;
        private NumericUpDown captureDuration;
        private ComboBox hvupVoltageSource;

        private ComboBox reportFormat;
        private CheckBox generateStateGridReport;
        private CheckBox enableAiFeatures;
        private TextBox task35Model;
        private TextBox task36Model;
        private TextBox task37Model;
        private TextBox task38Model;
        private TextBox unifiedModel;
        private TextBox task6Path;
        private TextBox reportPath;
        private TextBox reportOutputPath;
        private TextBox reportTemplatePath;

        private NumericUpDown minus3Attenuation;
        private NumericUpDown jointApproximationError;
        private NumericUpDown minus3CorrelationError;
        private NumericUpDown minus3Threshold;
        private NumericUpDown minus3MaximumPulses;
        private NumericUpDown calibrationAverageCount;
        private NumericUpDown noiseSimilarityTolerance;
        private CheckBox enableCompetitionDefectModule;
        private NumericUpDown minus3ReflectionSuppression;
        private ComboBox minus3AlgorithmIndex;
        private CheckBox minus3FirstArrival;
        private CheckBox minus3EnablePhaseFilter;
        private CheckBox minus3Quadrant0To90;
        private CheckBox minus3Quadrant90To180;
        private CheckBox minus3Quadrant180To270;
        private CheckBox minus3Quadrant270To360;
        private TextBox minus3ModelPath;

        private CheckBox hardwareEnabled;
        private TextBox selectedHubMac;
        private TextBox controllerLocation;
        private TextBox acquisitionLocation;
        private TextBox controllerSerial;
        private TextBox acquisitionSerial;
        private TextBox excludedSerial;
        private TextBox driverMarkerPath;
        private TextBox driverPackagePath;
        private TextBox statusJsonPath;
        private TextBox connectorExecutablePath;
        private TextBox integrationExecutablePath;
        private TextBox firstConfigurationToolPath;
        private TextBox driverInstallerPath;
        private TextBox task39InputSettingsPath;

        public AppSettings Settings { get; private set; }

        public SettingsForm()
            : this(AppSettings.Load())
        {
        }

        public SettingsForm(AppSettings current)
            : this(current, SettingsSection.General)
        {
        }

        public SettingsForm(AppSettings current, SettingsSection initialSection)
            : this(current, initialSection, null)
        {
        }

        internal SettingsForm(AppSettings current, SettingsSection initialSection, HardwareStatusSnapshot actual)
        {
            reviewedHardware = actual == null ? null : actual.Clone();
            original = current == null ? new AppSettings() : current.Clone();
            Settings = original.Clone();
            BuildUi();
            LoadValues(original);
            InitializeSettingsLanguage();
            SelectSection(initialSection);
        }

        private void BuildUi()
        {
            Text = "参数设置";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1040, 760);
            MinimumSize = new Size(900, 640);
            BackColor = AppTheme.Canvas;
            Font = AppTheme.Font(9.0f, FontStyle.Regular);
            AppTheme.ApplyDialogIcon(this);
            FormClosing += (s,e) => { if(savingOemAssets)e.Cancel=true; };
            Disposed += delegate { maintenanceGrants.Clear(); };

            Panel actions = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 64,
                BackColor = AppTheme.Surface,
                Padding = new Padding(12, 12, 16, 12)
            };
            Button save = AppTheme.Button("保存设置", true);
            save.Width = 112;
            save.Dock = DockStyle.Right;
            save.Click += SaveClicked;
            Button cancel = AppTheme.Button("取消", false);
            cancel.Width = 96;
            cancel.Dock = DockStyle.Right;
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(save);
            actions.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 });
            actions.Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(16, 7),
                Font = AppTheme.Font(9.5f, FontStyle.Regular)
            };
            tabs.TabPages.Add(BuildGeneralTab());
            tabs.TabPages.Add(BuildReportTab());
            deviceTab = CreateSettingsTab("设备关键参数");
            expertTab = CreateSettingsTab("厂家维护");
            oemTab = CreateSettingsTab("厂家 OEM");
            systemAdminTab = CreateSettingsTab("系统管理");
            plusDataTab = CreateSettingsTab("HV DAC Xpert Plus Data");
            if (SettingsAccessControl.Enabled)
            {
                AddLockedSectionEntry(deviceTab, SettingsSection.Device);
                AddLockedSectionEntry(expertTab, SettingsSection.Expert);
                AddLockedSectionEntry(oemTab, SettingsSection.Oem);
                AddLockedSectionEntry(systemAdminTab, SettingsSection.SystemAdmin);
                AddLockedSectionEntry(plusDataTab, SettingsSection.PlusData);
            }
            tabs.TabPages.Add(plusDataTab);
            tabs.SelectedIndexChanged += delegate { OpenSelectedSettingsSection(); };

            TableLayoutPanel shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            shell.Controls.Add(tabs, 0, 0);
            shell.Controls.Add(actions, 0, 1);
            EventHandler fitActions = delegate
            {
                Size saveSize = save.GetPreferredSize(Size.Empty);
                Size cancelSize = cancel.GetPreferredSize(Size.Empty);
                save.Width = Math.Max(112, saveSize.Width);
                cancel.Width = Math.Max(96, cancelSize.Width);
                shell.RowStyles[1].Height = Math.Max(64, Math.Max(saveSize.Height, cancelSize.Height) + actions.Padding.Vertical + actions.Margin.Vertical);
            };
            save.FontChanged += fitActions;
            cancel.FontChanged += fitActions;
            save.TextChanged += fitActions;
            cancel.TextChanged += fitActions;
            fitActions(this, EventArgs.Empty);
            Controls.Add(shell);
            ResponsiveFormLayout.Attach(this, new Size(760, 520));
        }

        private TabPage BuildGeneralTab()
        {
            TableLayoutPanel grid = CreateGrid();
            language = new SettingsComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            foreach (LanguageItem item in AppLanguages.SelectionItems) language.Items.Add(item);
            AddRow(grid, "全局语言", language);

            dataRoot = CreateTextBox();
            AddRow(grid, "数据根目录", CreatePathEditor(dataRoot, true, String.Empty));
            AddGeneralPressureRows(grid);
            Button hardwareAccess = AppTheme.Button("高级设置…", false);
            hardwareAccess.AutoSize = true;
            accessMenu = new ContextMenuStrip();
            accessMenu.Items.Add("设备关键参数", null, delegate { SelectSection(SettingsSection.Device); });
            accessMenu.Items.Add("厂家维护", null, delegate { SelectSection(SettingsSection.Expert); });
            accessMenu.Items.Add("厂家 OEM", null, delegate { SelectSection(SettingsSection.Oem); });
            accessMenu.Items.Add("系统管理 / 日志与端口", null, delegate { SelectSection(SettingsSection.SystemAdmin); });
            accessMenu.Items.Add("HV DAC Xpert Plus Data", null, delegate { SelectSection(SettingsSection.PlusData); });
            hardwareAccess.Click += delegate { accessMenu.Show(hardwareAccess, new Point(0, hardwareAccess.Height)); };
            AddWideRow(grid, hardwareAccess);
            // Comparison engines remain available to offline verification, not as a customer settings entry.
            return CreateTab("常规", grid);
        }

        private TabPage BuildReportTab()
        {
            TableLayoutPanel grid = CreateGrid();
            reportOutputPath = CreateTextBox();
            AddRow(grid, "报告输出目录", CreatePathEditor(reportOutputPath, true, String.Empty));
            reportFormat = new SettingsComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            reportFormat.Items.Add("仅 PDF");
            reportFormat.Items.Add("仅 Word");
            reportFormat.Items.Add("PDF 和 Word");
            AddRow(grid, "报告出具版式", reportFormat);
            generateStateGridReport = new CheckBox
            {
                Name = "GenerateStateGridReport",
                Text = "同步生成国家电网标准格式报告",
                AutoSize = true,
                ForeColor = AppTheme.Ink,
                UseVisualStyleBackColor = true
            };
            AddWideRow(grid, generateStateGridReport);
            Label stateGridNote = CreateNote("参照用户提供的 Q/GDW 11262—2024 附录 M，额外生成国家电网格式报告，不替换现有报告。默认关闭；启用后仍需核对报告内容和适用要求。");
            stateGridNote.Name = "StateGridReportNote";
            AddWideRow(grid, stateGridNote);
            // Constrain preferred text size to the current tab width so large
            // fonts wrap into auto-sized rows instead of covering the next row.
            grid.SizeChanged += delegate
            {
                int available = Math.Max(100, grid.ClientSize.Width - grid.Padding.Horizontal - 8);
                Size maximum = new Size(available, 0);
                if (generateStateGridReport.MaximumSize != maximum) generateStateGridReport.MaximumSize = maximum;
                if (stateGridNote.MaximumSize != maximum) stateGridNote.MaximumSize = maximum;
            };
            return CreateTab("报告", grid);
        }

        private Control BuildOemPanel()
        {
            TableLayoutPanel grid = CreateGrid();
            softwareModel = CreateTextBox();
            AddRow(grid, "软件型号", softwareModel);
            windowTitle = CreateTextBox();
            AddRow(grid, "窗口标题（留空跟随语言）", windowTitle);
            logoPath = CreateTextBox();
            AddRow(grid, "Logo 文件（留空不显示）", CreatePathEditor(logoPath, false, "图片文件|*.png;*.jpg;*.jpeg;*.bmp"));
            AddOemLogoInfo(grid);
            iconPath = CreateTextBox();
            AddRow(grid, "窗口图标（留空使用中性图标）", CreatePathEditor(iconPath, false, "图标文件|*.ico"));
            AddWideRow(grid, CreateNote("仅使用本机图片。保存后保留一份副本；更改会应用于本软件窗口。程序文件和快捷方式图标由安装版本决定。", AppTheme.Muted));
            manualPath = CreateTextBox();
            AddRow(grid, "说明书路径", CreatePathEditor(manualPath, false, "帮助文档|*.pdf;*.doc;*.docx;*.html;*.htm|所有文件|*.*"));
            copyrightText = CreateTextBox();
            copyrightText.Multiline = true;
            copyrightText.Height = 76;
            copyrightText.ScrollBars = ScrollBars.Vertical;
            AddRow(grid, "界面版权声明", copyrightText);
            return grid;
        }

        private Control BuildAcquisitionPanel()
        {
            TableLayoutPanel grid = CreateGrid();
            AddWideRow(grid, CreateNote(
                "设备参数按 ART 与 Pico 序列号组合保存。首次连接请完成独立设备配置；保存不会启动加压。",
                AppTheme.Warning));
            dividerRatio = CreateNumeric(0.000001M, 1000000000M, 6, 0.1M);
            AddRow(grid, "分压比", dividerRatio);
            pressureRatio = CreateNumeric(0.000001M, 1000000000M, 6, 1M);
            pressureRatio.Enabled = false; // Compatibility field; no editable ratio.
            deviceTrigger = CreateNumeric(-4.999999M, 4.999999M, 6, .01M);
            AddRow(grid, "触发阈值 / V（Pico EXT）", deviceTrigger);
            calibrationCorrection = CreateNumeric(0.000001M, 1000000M, 6, 0.01M);
            AddRow(grid, "校准系数修正（默认 1）", calibrationCorrection);
            inductance = CreateNumeric(0.000000001M, 1000000000M, 9, 0.01M);
            AddRow(grid, "电感量 / H", inductance);
            inductorResistance = CreateNumeric(0M, 1000000000M, 6, 0.1M);
            AddRow(grid, "电感直流电阻 / Ω", inductorResistance);
            maximumVoltage = CreateNumeric(0.000001M, 1000000M, 6, 1M);
            AddRow(grid, "最高限压 / kV", maximumVoltage);
            supportsCosineVlf = new CheckBox { Text = "余弦超低频", AutoSize = true };
            supportsCombinedVlf = new CheckBox { Text = "综合超低频", AutoSize = true };
            AddRow(grid, "耐压设备能力", supportsCosineVlf);
            AddRow(grid, "", supportsCombinedVlf);
            AddWideRow(grid, CreateNote(
                "余弦：余弦超低频、直流耐压、护套耐压；综合：另包含正弦超低频。\r\n" +
                "两项均关闭时，首页耐压入口灰显。首次每项目每相先做低压振荡波测电容：限压30 kV → 1 kV；60 kV → 3 kV。其他限压自动估算并明确标注；实机使用前须由厂家核对。\r\n" +
                "勾选设备能力不会开启实机高压。"));
            captureDuration = CreateNumeric(0.001M, 60M, 3, 1M);
            AddRow(grid, "局放采集长度 / ms", captureDuration);
            hvupVoltageSource = new SettingsComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
            hvupVoltageSource.Items.Add("DIV");
            hvupVoltageSource.Items.Add("Vmp");
            AddRow(grid, "电压反馈", hvupVoltageSource);
            return grid;
        }

        private TabPage BuildMinus3Tab()
        {
            TableLayoutPanel grid = CreateGrid();
            Label note = CreateNote("以下参数用于统一分析流程；语言跟随“常规”页的全局语言设置。");
            AddWideRow(grid, note);
            minus3Attenuation = CreateNumeric(0M, 100M, 3, 1M);
            AddRow(grid, "衰减容差 / %", minus3Attenuation);
            jointApproximationError = CreateNumeric(0M, 100M, 3, 0.1M);
            AddRow(grid, "近似接头误差 / %", jointApproximationError);
            AddWideRow(grid, CreateNote("接头范围：接头位置 ± 当前相电缆长度 × 误差 ÷ 100。输入 1 表示 ±1%；结论使用近似后的结果。"));
            minus3CorrelationError = CreateNumeric(0M, 100M, 3, 1M);
            AddRow(grid, "相关误差 / %", minus3CorrelationError);
            minus3Threshold = CreateNumeric(0M, 1000M, 6, 0.1M);
            AddRow(grid, "自动阈值倍数", minus3Threshold);
            minus3FirstArrival = new CheckBox { Text = "校准、匹配、波速、长度与定位统一使用首达时序", AutoSize = true };
            AddRow(grid, "首达统一计时模式", minus3FirstArrival);
            minus3EnablePhaseFilter = new CheckBox { Text = "启用相位过滤", AutoSize = true };
            AddRow(grid, "相位过滤开关", minus3EnablePhaseFilter);
            TableLayoutPanel quadrants = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            quadrants.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            quadrants.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            minus3Quadrant0To90 = new CheckBox { Text = "[0°, 90°)", AutoSize = true, Margin = new Padding(0, 4, 12, 4) };
            minus3Quadrant90To180 = new CheckBox { Text = "[90°, 180°)", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            minus3Quadrant180To270 = new CheckBox { Text = "[180°, 270°)", AutoSize = true, Margin = new Padding(0, 4, 12, 4) };
            minus3Quadrant270To360 = new CheckBox { Text = "[270°, 360°)", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
            quadrants.Controls.Add(minus3Quadrant0To90, 0, 0);
            quadrants.Controls.Add(minus3Quadrant90To180, 1, 0);
            quadrants.Controls.Add(minus3Quadrant180To270, 0, 1);
            quadrants.Controls.Add(minus3Quadrant270To360, 1, 1);
            AddRow(grid, "相位象限", quadrants);
            return CreateTab("分析参数", grid);
        }

        private TabPage BuildModelsTab()
        {
            TableLayoutPanel grid = CreateGrid();
            task6Path = CreateTextBox();
            AddRow(grid, "采集组件路径（默认使用随包组件）", CreatePathEditor(task6Path, false, "程序或配置|*.exe;*.dll;*.json;*.xml|所有文件|*.*"));
            reportPath = CreateTextBox();
            AddRow(grid, "报告模块 / 程序路径", CreatePathEditor(reportPath, false, "程序或程序集|*.exe;*.dll|所有文件|*.*"));
            reportTemplatePath = CreateTextBox();
            AddRow(grid, "报告模板路径", CreatePathEditor(reportTemplatePath, false, "报告模板|*.docx;*.dotx;*.html;*.htm|所有文件|*.*"));
            return CreateTab("程序与报告路径", grid);
        }

        private TabPage BuildHardwareTab()
        {
            TableLayoutPanel grid = CreateGrid();
            Label warning = CreateNote(
                "厂家维护：设备身份、驱动与接入程序的设置仅供授权人员使用。\r\n" +
                "启用连接检查不会自动加压或开始试验。更改以下字段后请重新检查设备状态。",
                AppTheme.Warning);
            AddWideRow(grid, warning);
            hardwareEnabled = new CheckBox
            {
                Text = "启用指定设备的连接检查（默认关闭 / 离线）",
                AutoSize = true,
                Font = AppTheme.Font(9.5f, FontStyle.Bold),
                ForeColor = AppTheme.Navy
            };
            AddRow(grid, "硬件模式", hardwareEnabled);
            AddDeviceSearchRow(grid);
            selectedHubMac = CreateTextBox();
            AddRow(grid, "设备网络地址 / MAC", selectedHubMac);
            controllerLocation = CreateTextBox();
            AddRow(grid, "控制模块位置（身份校验）", controllerLocation);
            acquisitionLocation = CreateTextBox();
            AddRow(grid, "采集模块位置（身份校验）", acquisitionLocation);
            controllerSerial = CreateTextBox();
            AddRow(grid, "控制模块序列号（追溯备注）", controllerSerial);
            acquisitionSerial = CreateTextBox();
            AddRow(grid, "采集模块序列号（追溯备注）", acquisitionSerial);
            excludedSerial = CreateTextBox();
            AddRow(grid, "排除的直连设备序列号（追溯备注）", excludedSerial);
            driverMarkerPath = CreateTextBox();
            AddRow(grid, "驱动安装标记路径", CreatePathEditor(driverMarkerPath, false, "所有文件|*.*"));
            driverPackagePath = CreateTextBox();
            AddRow(grid, "驱动包路径", CreatePathEditor(driverPackagePath, false, "安装包|*.exe;*.msi;*.zip|所有文件|*.*"));
            statusJsonPath = CreateTextBox();
            AddRow(grid, "硬件状态 JSON 路径", CreatePathEditor(statusJsonPath, false, "JSON 文件|*.json|所有文件|*.*"));
            connectorExecutablePath = CreateTextBox();
            AddRow(grid, "连接器程序路径（兼容）", CreatePathEditor(connectorExecutablePath, false, "程序|*.exe|所有文件|*.*"));
            integrationExecutablePath = CreateTextBox();
            AddRow(grid, "设备连接程序路径", CreatePathEditor(integrationExecutablePath, false, "程序|*.exe|所有文件|*.*"));
            firstConfigurationToolPath = CreateTextBox();
            AddRow(grid, "首次配置工具（可能需要管理员权限）", CreatePathEditor(firstConfigurationToolPath, false, "程序|*.exe|所有文件|*.*"));
            driverInstallerPath = CreateTextBox();
            AddRow(grid, "驱动安装界面程序", CreatePathEditor(driverInstallerPath, false, "程序|*.exe;*.msi|所有文件|*.*"));
            task39InputSettingsPath = CreateTextBox();
            AddRow(grid, "零电压采集设置", CreatePathEditor(task39InputSettingsPath, false, "采集设置|*.xml"));
            return CreateTab("高级 / 硬件", grid);
        }

        private TabPage BuildPressureRampTab()
        {
            var grid=CreateGrid();
            pressureRampStep=CreateNumeric(.0001M,.1M,4,.0001M);pressureRampStep.Name="PressureRampStepV";
            // This control needs sub-millivolt steps, unlike the normal two-decimal editors.
            pressureRampStep.DecimalPlaces=4;
            ((SettingsNumber)pressureRampStep).MinimumDecimalPlaces=4;
            pressureRampInterval=CreateNumeric(1,1000,0,1);pressureRampInterval.Name="PressureRampIntervalMs";
            AddRow(grid,"普通加压速度：AO步长 / V",pressureRampStep);
            AddRow(grid,"普通加压速度：循环请求间隔 / ms",pressureRampInterval);
            endResidualPercent=CreateNumeric(.01M,100,2,.01M);endResidualPercent.Name="EndResidualPercent";
            // Historical setting is retained for reading old configuration only;
            // the pressure runtime now forbids before/after residual acquisition.
            endResidualPercent.Enabled=false;endResidualPercent.Visible=false;
            AddWideRow(grid,CreateNote("加压前后不进行软件残压测量或残压等待。仍执行实际 AO 归零、高压关闭及操作结果核对；软件不据此宣称已放电。"));
            AddWideRow(grid,CreateNote("两项共同控制普通升压速度；实际速度受电压反馈和设备响应影响，不是恒定kV/s。精细化加压采用独立调节，并保留更小步长及更长间隔限制。修改不会启动加压，也不改变限压及急停保护。"));
            return CreateTab("加压速度",grid);
        }

        private TabPage CreateSettingsTab(string title)
        {
            return new TabPage(title) { BackColor = AppTheme.Canvas, Padding = new Padding(18), AutoScroll = true };
        }

        private void OpenSelectedSettingsSection()
        {
            SettingsSection section = tabs.SelectedTab == deviceTab ? SettingsSection.Device
                : tabs.SelectedTab == expertTab ? SettingsSection.Expert
                : tabs.SelectedTab == oemTab ? SettingsSection.Oem
                : tabs.SelectedTab == systemAdminTab ? SettingsSection.SystemAdmin
                : tabs.SelectedTab == plusDataTab ? SettingsSection.PlusData : SettingsSection.General;
            try { OpenSection(section); }
            catch (InvalidOperationException error)
            {
                new Diagnostics().Error("Settings", "Could not open settings.", error);
                MessageBox.Show(this, SettingsText("无法打开设置，请检查原配置后重试。", "Cannot open settings. Check the existing configuration and try again."), SettingsText("配置无效", "Invalid settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SelectSection(SettingsSection section)
        {
            if (tabs == null) return;
            switch (section)
            {
                case SettingsSection.PlusData:
                    tabs.SelectedTab = plusDataTab;
                    break;
                case SettingsSection.SystemAdmin:
                    if (!tabs.TabPages.Contains(systemAdminTab)) tabs.TabPages.Add(systemAdminTab);
                    tabs.SelectedTab = systemAdminTab;
                    break;
                case SettingsSection.Device:
                    if (!tabs.TabPages.Contains(deviceTab)) tabs.TabPages.Add(deviceTab);
                    tabs.SelectedTab = deviceTab;
                    break;
                case SettingsSection.Expert:
                    if (!tabs.TabPages.Contains(expertTab)) tabs.TabPages.Add(expertTab);
                    tabs.SelectedTab = expertTab;
                    break;
                case SettingsSection.Oem:
                    if (!tabs.TabPages.Contains(oemTab)) tabs.TabPages.Add(oemTab);
                    tabs.SelectedTab = oemTab;
                    break;
                default: tabs.SelectedIndex = 0; break;
            }
            OpenSelectedSettingsSection();
        }

        private void OpenSection(SettingsSection section)
        {
            if (!IsSectionAuthorized(section)) return;
            if (section == SettingsSection.PlusData && !plusDataUnlocked)
            {
                // Initialize only on explicit navigation. Invalid input must not
                // replace a saved endpoint policy with numeric defaults.
                double width, ratio; bool firstAnd, secondAnd; string message;
                if (!AppSettings.TryParseEndpointPolicy(original.TaskMinus3.EndpointPolicy,
                    out width, out ratio, out firstAnd, out secondAnd, out message))
                    throw new InvalidOperationException(message);
                Control panel = BuildPlusDataPanel();
                try { LoadPlusDataValues(original); }
                catch { panel.Dispose(); throw; }
                while (plusDataTab.Controls.Count > 0) plusDataTab.Controls[0].Dispose();
                plusDataTab.Controls.Add(panel);
                plusDataUnlocked = true;
            }
            else if (section == SettingsSection.Device && !deviceUnlocked)
            {
                deviceTab.Controls.Clear();
                deviceTab.Controls.Add(BuildAcquisitionPanel());
                LoadDeviceValues(original);
                deviceUnlocked = true;
            }
            else if (section == SettingsSection.Expert && !expertUnlocked)
            {
                expertTab.Controls.Clear();
                expertTabs = new TabControl { Dock = DockStyle.Fill, Font = AppTheme.Font(9.0f, FontStyle.Regular) };
                expertTabs.TabPages.Add(BuildPressureRampTab());
                expertTabs.TabPages.Add(BuildMinus3Tab());
                expertTabs.TabPages.Add(BuildModelsTab());
                expertTab.Controls.Add(expertTabs);
                LoadExpertValues(original);
                expertUnlocked = true;
            }
            else if (section == SettingsSection.SystemAdmin && !systemAdminUnlocked)
            {
                systemAdminTab.Controls.Clear();
                var adminTabs = new TabControl { Dock = DockStyle.Fill };
                adminTabs.TabPages.Add(BuildHardwareTab());
                var logs = CreateGrid();
                AddLogBrowserRow(logs);
                adminTabs.TabPages.Add(CreateTab("软件日志", logs));
                systemAdminTab.Controls.Add(adminTabs);
                LoadHardwareValues(original);
                systemAdminUnlocked = true;
            }
            else if (section == SettingsSection.Oem && !oemUnlocked)
            {
                oemTab.Controls.Clear();
                oemTab.Controls.Add(BuildOemPanel());
                LoadOemValues(original);
                oemUnlocked = true;
            }
            ApplySettingsLanguage();
        }

        private void LoadValues(AppSettings value)
        {
            AppLanguages.InitializeSelection(language, value.LanguageCode);
            dataRoot.Text = value.DataRoot;
            LoadGeneralPressureValues(value);
            reportFormat.SelectedIndex = value.ReportOutputFormat == ReportOutputFormat.PdfOnly
                ? 0
                : value.ReportOutputFormat == ReportOutputFormat.WordOnly ? 1 : 2;
            reportOutputPath.Text = value.ReportOutputPath;
            generateStateGridReport.Checked = value.GenerateStateGridReport;
        }

        private void LoadOemValues(AppSettings value)
        {
            softwareModel.Text = value.SoftwareModel;
            windowTitle.Text = value.WindowTitle ?? String.Empty;
            logoPath.Text = value.LogoPath;
            iconPath.Text = value.IconPath;
            manualPath.Text = value.ManualPath;
            copyrightText.Text = value.CopyrightText;
        }

        private void LoadDeviceValues(AppSettings value)
        {
            SetNumeric(dividerRatio, value.DividerRatio);
            SetNumeric(pressureRatio, value.PressureRatio);
            SetNumeric(deviceTrigger, value.DeviceTriggerVolts);
            SetNumeric(calibrationCorrection, value.PdCalibrationCorrectionFactor);
            SetNumeric(inductance, value.InductanceHenry);
            SetNumeric(inductorResistance, value.InductorDcResistanceOhm);
            SetNumeric(maximumVoltage, value.MaximumVoltageKv);
            supportsCosineVlf.Checked = value.SupportsCosineVlf;
            supportsCombinedVlf.Checked = value.SupportsCombinedVlf;
            SetNumeric(captureDuration, value.OscillatingPdCaptureDurationMs);
            hvupVoltageSource.SelectedIndex = value.HvupVoltageSource == HvupVoltageSource.VMonitor ? 1 : 0;
        }

        private void LoadExpertValues(AppSettings value)
        {
            SetNumeric(pressureRampStep,value.PressureRampStepV);
            SetNumeric(pressureRampInterval,value.PressureRampIntervalMs);
            SetNumeric(endResidualPercent,value.EndResidualPercent);
            task6Path.Text = value.Task6Path;
            reportPath.Text = value.ReportPath;
            reportTemplatePath.Text = value.ReportTemplatePath;

            TaskMinus3AnalysisSettings analysis = value.TaskMinus3 ?? new TaskMinus3AnalysisSettings();
            SetNumeric(minus3Attenuation, analysis.AttenuationTolerancePercent);
            SetNumeric(jointApproximationError, analysis.JointApproximationErrorPercent);
            SetNumeric(minus3CorrelationError, analysis.CorrelationErrorPercent);
            SetNumeric(minus3Threshold, analysis.AutomaticThresholdMultiplier);
            minus3FirstArrival.Checked = analysis.UseFirstArrivalTiming;
            minus3EnablePhaseFilter.Checked = analysis.PhaseFilterEnabled;
            minus3Quadrant0To90.Checked = analysis.Quadrant0To90;
            minus3Quadrant90To180.Checked = analysis.Quadrant90To180;
            minus3Quadrant180To270.Checked = analysis.Quadrant180To270;
            minus3Quadrant270To360.Checked = analysis.Quadrant270To360;

        }

        private void LoadHardwareValues(AppSettings value)
        {
            HardwareSettings hardware = value.Hardware ?? new HardwareSettings();
            hardwareEnabled.Checked = hardware.Enabled;
            selectedHubMac.Text = hardware.SelectedHubMac;
            controllerLocation.Text = hardware.ControllerLocation;
            acquisitionLocation.Text = hardware.AcquisitionLocation;
            controllerSerial.Text = hardware.ControllerSerial;
            acquisitionSerial.Text = hardware.AcquisitionSerial;
            excludedSerial.Text = hardware.ExcludedAcquisitionSerial;
            driverMarkerPath.Text = hardware.DriverMarkerPath;
            driverPackagePath.Text = hardware.DriverPackagePath;
            statusJsonPath.Text = hardware.StatusJsonPath;
            connectorExecutablePath.Text = hardware.ConnectorExecutablePath;
            integrationExecutablePath.Text = hardware.IntegrationExecutablePath;
            firstConfigurationToolPath.Text = hardware.FirstConfigurationToolPath;
            driverInstallerPath.Text = hardware.DriverInstallerPath;
            task39InputSettingsPath.Text = hardware.Task39InputSettingsPath;
        }

        private AppSettings CaptureValues()
        {
            AppSettings value = original.Clone();
            value.LanguageCode = AppLanguages.CaptureSelectionCode(language, original.LanguageCode);
            value.DataRoot = dataRoot.Text;
            CaptureGeneralPressureValues(value);
            value.ReportOutputFormat = reportFormat.SelectedIndex == 0
                ? ReportOutputFormat.PdfOnly
                : reportFormat.SelectedIndex == 1 ? ReportOutputFormat.WordOnly : ReportOutputFormat.PdfAndWord;
            value.ReportOutputPath = reportOutputPath.Text;
            value.GenerateStateGridReport = generateStateGridReport.Checked;

            if (oemUnlocked && IsSectionAuthorized(SettingsSection.Oem)) CaptureOemValues(value);
            if (deviceUnlocked && IsSectionAuthorized(SettingsSection.Device)) CaptureDeviceValues(value);
            if (expertUnlocked && IsSectionAuthorized(SettingsSection.Expert)) CaptureExpertValues(value);
            if (systemAdminUnlocked && IsSectionAuthorized(SettingsSection.SystemAdmin)) CaptureHardwareValues(value);
            if (plusDataUnlocked && IsSectionAuthorized(SettingsSection.PlusData)) CapturePlusDataValues(value);
            // Reset inherited transient grants and bind these edits to the
            // sections actually authorized in this dialog.
            value.DeviceProfileEditAuthorized = deviceUnlocked && IsSectionAuthorized(SettingsSection.Device);
            value.ExpertDeviceProfileEditAuthorized = expertUnlocked && IsSectionAuthorized(SettingsSection.Expert);
            return value;
        }

        private void CaptureOemValues(AppSettings value)
        {
            value.SoftwareModel = softwareModel.Text;
            value.WindowTitle = windowTitle.Text.Trim();
            value.LogoPath = logoPath.Text;
            value.IconPath = iconPath.Text;
            value.ManualPath = manualPath.Text;
            value.CopyrightText = copyrightText.Text;
        }

        private void CaptureDeviceValues(AppSettings value)
        {
            value.DividerRatio = ReadNumber(dividerRatio);
            value.DeviceTriggerVolts = ReadNumber(deviceTrigger);
            value.PdCalibrationCorrectionFactor = ReadNumber(calibrationCorrection);
            value.InductanceHenry = ReadNumber(inductance);
            value.InductorDcResistanceOhm = ReadNumber(inductorResistance);
            value.MaximumVoltageKv = ReadNumber(maximumVoltage);
            value.PressureRatio = DeviceParameterProfiles.PressureRatioFor(value.MaximumVoltageKv);
            value.SupportsCosineVlf = supportsCosineVlf.Checked;
            value.SupportsCombinedVlf = supportsCombinedVlf.Checked;
            value.OscillatingPdCaptureDurationMs = ReadNumber(captureDuration);
            value.HvupVoltageSource = hvupVoltageSource.SelectedIndex == 1 ? HvupVoltageSource.VMonitor : HvupVoltageSource.Divider;
        }

        private void CaptureExpertValues(AppSettings value)
        {
            value.PressureRampStepV=ReadNumber(pressureRampStep);
            value.PressureRampIntervalMs=Convert.ToInt32(ReadNumber(pressureRampInterval));
            value.EndResidualPercent=ReadNumber(endResidualPercent);
            value.Task6Path = task6Path.Text;
            value.ReportPath = reportPath.Text;
            value.ReportTemplatePath = reportTemplatePath.Text;

            value.TaskMinus3 = value.TaskMinus3 ?? new TaskMinus3AnalysisSettings();
            value.TaskMinus3.AttenuationTolerancePercent = ReadNumber(minus3Attenuation);
            value.TaskMinus3.JointApproximationErrorPercent = ReadNumber(jointApproximationError);
            value.TaskMinus3.CorrelationErrorPercent = ReadNumber(minus3CorrelationError);
            value.TaskMinus3.AutomaticThresholdMultiplier = ReadNumber(minus3Threshold);
            value.TaskMinus3.UseFirstArrivalTiming = minus3FirstArrival.Checked;
            value.TaskMinus3.PhaseFilterEnabled = minus3EnablePhaseFilter.Checked;
            value.TaskMinus3.Quadrant0To90 = minus3Quadrant0To90.Checked;
            value.TaskMinus3.Quadrant90To180 = minus3Quadrant90To180.Checked;
            value.TaskMinus3.Quadrant180To270 = minus3Quadrant180To270.Checked;
            value.TaskMinus3.Quadrant270To360 = minus3Quadrant270To360.Checked;

        }

        private void CaptureHardwareValues(AppSettings value)
        {
            value.Hardware = value.Hardware ?? new HardwareSettings();
            value.Hardware.Enabled = hardwareEnabled.Checked;
            value.Hardware.SelectedHubMac = selectedHubMac.Text;
            value.Hardware.ControllerLocation = controllerLocation.Text;
            value.Hardware.AcquisitionLocation = acquisitionLocation.Text;
            value.Hardware.ControllerSerial = controllerSerial.Text;
            value.Hardware.AcquisitionSerial = acquisitionSerial.Text;
            value.Hardware.ExcludedAcquisitionSerial = excludedSerial.Text;
            value.Hardware.DriverMarkerPath = driverMarkerPath.Text;
            value.Hardware.DriverPackagePath = driverPackagePath.Text;
            value.Hardware.StatusJsonPath = statusJsonPath.Text;
            value.Hardware.ConnectorExecutablePath = connectorExecutablePath.Text;
            value.Hardware.IntegrationExecutablePath = integrationExecutablePath.Text;
            value.Hardware.FirstConfigurationToolPath = firstConfigurationToolPath.Text;
            value.Hardware.DriverInstallerPath = driverInstallerPath.Text;
            value.Hardware.Task39InputSettingsPath = task39InputSettingsPath.Text;
        }

        private async void SaveClicked(object sender, EventArgs e)
        {
            if (savingOemAssets) return;
            try
            {
                AppSettings candidate = CaptureValues();
                string validation;
                if (!candidate.TryValidate(out validation))
                {
                    LogSettingAction(OperationAuditAction.SettingsSaveFailed, AuditOutcome.Failed);
                    MessageBox.Show(this, SettingsCaption(validation), SettingsText("设置校验", "Check settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if(oemUnlocked && (!String.IsNullOrWhiteSpace(candidate.LogoPath) || !String.IsNullOrWhiteSpace(candidate.IconPath))) {
                    savingOemAssets=true;Enabled=false;
                    await OemBranding.ImportAsync(candidate);
                    if(IsDisposed||Disposing)return;
                }
                // The captured device snapshot predates edits in this dialog. It
                // cannot confirm a different hub, port, serial or integration tool.
                if (deviceUnlocked) DeviceSetupStatus.ConfirmParameters(candidate,
                    SameHardwareConfiguration(original.Hardware, candidate.Hardware) ? reviewedHardware : null);
                // Guarded editions capture only fields authorized in this window.
                // SaveCore synchronizes the active serial-pair record atomically.
                candidate.Save();
                LogSettingAction(OperationAuditAction.SettingsSaved, AuditOutcome.Succeeded);
                Settings = candidate;
                savingOemAssets=false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                LogSettingAction(OperationAuditAction.SettingsSaveFailed, AuditOutcome.Failed);
                new Diagnostics().Error("Settings","Could not save settings.",error);
                if(!IsDisposed&&!Disposing) MessageBox.Show(this, savingOemAssets
                    ? SettingsText("品牌图片未能保存。请选择本机有效的 PNG、JPEG、BMP 或 ICO 文件，并检查大小及访问权限。原设置未改变。", "Brand images could not be saved. Choose valid local PNG, JPEG, BMP or ICO files and check their size and access permissions. Previous settings were retained.")
                    : SettingsText("设置未保存。请检查参数和文件权限后重试。", "Settings were not saved. Check the values and file permissions, then retry."), SettingsText("设置保存失败", "Settings not saved"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally {savingOemAssets=false;if(!IsDisposed&&!Disposing)Enabled=true;}
        }

        private static bool SameHardwareConfiguration(HardwareSettings before, HardwareSettings after)
        {
            if (before == null || after == null) return false;
            foreach (System.Reflection.PropertyInfo property in typeof(HardwareSettings).GetProperties())
                if (!Object.Equals(property.GetValue(before, null), property.GetValue(after, null))) return false;
            return true;
        }

        private static TabPage CreateTab(string title, Control content)
        {
            TabPage page = new TabPage(title)
            {
                BackColor = AppTheme.Canvas,
                Padding = new Padding(18),
                AutoScroll = true
            };
            page.Controls.Add(content);
            return page;
        }

        private static TableLayoutPanel CreateGrid()
        {
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(2),
                BackColor = AppTheme.Canvas
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            return grid;
        }

        private static void AddRow(TableLayoutPanel grid, string labelText, Control control)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label label = new Label
            {
                Text = labelText,
                AutoSize = true,
                MaximumSize = new Size(233, 0),
                ForeColor = AppTheme.Ink,
                Margin = new Padding(5, 13, 12, 8),
                Anchor = AnchorStyles.Left
            };
            control.Margin = new Padding(4, 6, 4, 6);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(control, 1, row);
            ComboBox choice = control as ComboBox;
            if (choice != null)
            {
                EventHandler fitChoice = delegate
                {
                    int height = Math.Max(choice.Height, choice.PreferredHeight);
                    choice.MinimumSize = new Size(0, height);
                    RowStyle style = grid.RowStyles[row];
                    float required = Math.Max(height + choice.Margin.Vertical,
                        label.GetPreferredSize(new Size(230, 0)).Height + label.Margin.Vertical) + 2;
                    style.SizeType = SizeType.Absolute;
                    if (style.Height != required) style.Height = required;
                };
                choice.FontChanged += fitChoice;
                choice.SizeChanged += fitChoice;
                label.FontChanged += fitChoice;
                label.TextChanged += fitChoice;
                fitChoice(choice, EventArgs.Empty);
            }
        }

        private static void AddWideRow(TableLayoutPanel grid, Control control)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(4, 5, 4, 12);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        private static Label CreateNote(string text)
        {
            return CreateNote(text, AppTheme.Muted);
        }

        private static Label CreateNote(string text, Color color)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                ForeColor = color,
                BackColor = Color.FromArgb(249, 251, 253),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12),
                Font = AppTheme.Font(9.0f, FontStyle.Regular)
            };
        }

        private static TextBox CreateTextBox()
        {
            return new TextBox { Width = 610, BorderStyle = BorderStyle.FixedSingle };
        }

        private static NumericUpDown CreateNumeric(decimal minimum, decimal maximum, int decimals, decimal increment)
        {
            return new SettingsNumber
            {
                Minimum = minimum,
                Maximum = maximum,
                DecimalPlaces = decimals == 0 ? 0 : 2,
                Increment = increment,
                Width = 260,
                TextAlign = HorizontalAlignment.Right
            };
        }

        private static void SetNumeric(NumericUpDown control, double value)
        {
            SettingsNumber number = control as SettingsNumber;
            if (number != null) { number.LoadSource(value); return; }
            decimal converted;
            try
            {
                converted = Convert.ToDecimal(value);
            }
            catch
            {
                converted = control.Minimum;
            }
            if (converted < control.Minimum) converted = control.Minimum;
            if (converted > control.Maximum) converted = control.Maximum;
            control.Value = converted;
        }

        private Control CreatePathEditor(TextBox target, bool directory, string filter)
        {
            TableLayoutPanel editor = new TableLayoutPanel
            {
                AutoSize = false,
                Height = 42,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            target.Dock = DockStyle.Fill;
            target.Margin = new Padding(0, 8, 0, 8);
            Button browse = AppTheme.Button("浏览…", false);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(8, 2, 0, 2);
            browse.Click += delegate
            {
                if (directory) BrowseDirectory(target);
                else BrowseFile(target, filter);
            };
            editor.Controls.Add(target, 0, 0);
            editor.Controls.Add(browse, 1, 0);
            EventHandler fitEditor = delegate
            {
                Size preferred = browse.GetPreferredSize(Size.Empty);
                editor.ColumnStyles[1].Width = Math.Max(90, preferred.Width + browse.Margin.Horizontal);
                editor.Height = Math.Max(42, Math.Max(target.PreferredHeight + target.Margin.Vertical, preferred.Height + browse.Margin.Vertical));
            };
            browse.FontChanged += fitEditor;
            target.FontChanged += fitEditor;
            browse.TextChanged += fitEditor;
            fitEditor(editor, EventArgs.Empty);
            return editor;
        }

        private void BrowseDirectory(TextBox target)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = SettingsText("选择目录", "Select folder");
                if (Directory.Exists(target.Text)) dialog.SelectedPath = target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
            }
        }

        private async void BrowseFile(TextBox target, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = SettingsText("选择文件", "Select file");
                dialog.Filter = TranslateSettingsFilter(String.IsNullOrWhiteSpace(filter) ? "所有文件|*.*" : filter);
                bool branding=target==logoPath||target==iconPath;
                if(branding){dialog.InitialDirectory=OemBranding.AssetDirectory;dialog.RestoreDirectory=true;}
                else if (File.Exists(target.Text)) dialog.FileName = target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) {
                    try {
                        string path=branding?await OemBranding.ImportAssetAsync(dialog.FileName,target==iconPath):dialog.FileName;
                        if(!IsDisposed&&!target.IsDisposed)target.Text=path;
                    } catch(Exception error)when(error is IOException||error is InvalidDataException||error is UnauthorizedAccessException||error is ArgumentException||error is NotSupportedException||error is OutOfMemoryException||error is System.Runtime.InteropServices.ExternalException) {
                        new Diagnostics().Error("OemImport","Selected branding asset was not imported.",error);
                        if(!IsDisposed)MessageBox.Show(this,SettingsText("图片未加载，请检查图片文件和保存权限后重试。","The image was not loaded. Check the file and folder access, then retry."),Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private static string ModelFilter()
        {
            return "模型文件|*.onnx;*.dll;*.json;*.bin;*.pt;*.pth|所有文件|*.*";
        }
    }
}
