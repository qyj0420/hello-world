using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        const string TestInformationSection = "TestInformation";
        const string EndpointDetailsSection = "EndpointDetails";
        const string TestConfigurationSection = "TestConfiguration";
        const string EnvironmentSection = "EnvironmentAndGNSS";
        const string InsulationSection = "InsulationAndNotes";
        const string JointSection = "JointsAndBody";
        bool applyingResponsiveLayout;
        string lastResponsiveMetricsKey;

        bool IsMinimizedViewport()
        {
            Form host = FindForm();
            return ClientSize.Width <= 0 || ClientSize.Height <= 0 ||
                (host != null && host.WindowState == FormWindowState.Minimized);
        }

        string ResponsiveMetricsKey()
        {
            var key = new StringBuilder();
            key.Append(ClientSize).Append('|').Append(DeviceDpi).Append('|').Append(embeddedMode)
                .Append('|').Append(languageIndex).Append('|').Append(Font.Name).Append(':')
                .Append(Font.SizeInPoints).Append(':').Append(Font.Style);
            foreach (Control control in new Control[] { statusLabel, maxLengthLabel })
                if (control != null) key.Append('|').Append(control.Text).Append('|').Append(control.Font);
            foreach (var section in optionalSections.Values)
                if (section.TabButton != null) key.Append('|').Append(section.TabButton.Text).Append('|').Append(section.TabButton.Font);
            return key.ToString();
        }

        sealed class OptionalSectionState
        {
            internal string Code;
            internal Panel Section;
            internal Control Content;
            internal RowStyle RowStyle;
            internal ProjectCreationButton ToggleButton;
            internal ProjectCreationTabButton TabButton;
            internal int ExpandedHeight;
            internal bool Expanded;
        }

        readonly Dictionary<string, OptionalSectionState> optionalSections =
            new Dictionary<string, OptionalSectionState>(StringComparer.OrdinalIgnoreCase);
        ComboBox organizationCombo;
        ComboBox operatorCombo;
        TextBox testLocationText;
        DateTimePicker testDatePicker;
        CheckBox dualEndedCheck;
        NumericUpDown humidityNumber;
        NumericUpDown temperatureNumber;
        TextBox weatherText;
        Label latitudeValue;
        Label longitudeValue;
        Label altitudeValue;
        Label fixStatusValue;
        Label sampleTimeValue;
        Label hardwareSourceLabel;
        TextBox startNameText;
        ComboBox startTypeCombo;
        TextBox dispatchStartText;
        TextBox endNameText;
        ComboBox endTypeCombo;
        TextBox dispatchEndText;
        TextBox lineNameText;
        TextBox cableModelText;
        ComboBox cableModeCombo;
        NumericUpDown lengthANumber;
        NumericUpDown lengthBNumber;
        NumericUpDown lengthCNumber;
        ComboBox phaseVoltageCombo;
        DateTimePicker layingDatePicker;
        ComboBox pressureModeCombo;
        TextBox insulationA1Text;
        TextBox insulationA2Text;
        TextBox insulationB1Text;
        TextBox insulationB2Text;
        TextBox insulationC1Text;
        TextBox insulationC2Text;
        TextBox notesText;
        ComboBox selectedPhaseCombo;
        ComboBox attachmentTypeCombo;
        ComboBox bodyTypeCombo;
        ComboBox beforeBodyTypeCombo;
        NumericUpDown jointPositionNumber;
        DataGridView cableGrid;
        CableTopologyCanvas topology;
        ProjectCreationButton addJointButton;
        ProjectCreationButton editJointButton;
        ProjectCreationButton deleteJointButton;
        ProjectCreationButton saveButton;
        ProjectCreationButton loadButton;
        ProjectCreationButton clearButton;
        ProjectCreationButton exitButton;
        ProjectCreationButton acquireHardwareButton;
        ProjectCreationButton weatherRefreshButton;
        ProjectCreationButton hardwareToggleButton;
        ProjectCreationButton pressureEditButton;
        Label statusLabel;
        Label savedBadge;
        ProjectCreationDeviceStateLabel deviceStateLabel;
        Label lengthSummaryLabel;
        Label validationSummaryLabel;
        Label lengthARequiredMark;
        Label lengthBRequiredMark;
        Label lengthCRequiredMark;
        Label startNameCaptionLabel;
        Label endNameCaptionLabel;
        Label loadTypeCaptionLabel;
        Label lengthACaptionLabel;
        Label lengthBCaptionLabel;
        Label lengthCCaptionLabel;
        Label routeCaptionLabel;
        Label endpointStartTypeCaptionLabel;
        Label endpointEndTypeCaptionLabel;
        Label endpointStartIdCaptionLabel;
        Label endpointEndIdCaptionLabel;
        Label endpointLineCaptionLabel;
        Label endpointModelCaptionLabel;
        Control lengthAField;
        Control lengthBField;
        Control lengthCField;
        TableLayoutPanel electricalFields;
        TableLayoutPanel endNameEditor;
        ColumnStyle capacitorRefreshColumn;
        ProjectCreationButton capacitorIdRefreshButton;
        ComboBox languageCombo;
        Panel leftScrollPanel;
        Panel rightScrollPanel;
        Panel tabPageHost;
        Label maxLengthLabel;
        Label modelOverviewLabel;
        Panel hardwareSection;
        Control hardwareContent;
        RowStyle hardwareRowStyle;
        bool environmentDetailsExpanded;
        string expandedOptionalSectionCode = string.Empty;
        TableLayoutPanel rootLayout;
        RowStyle headerRowStyle;
        Control headerPanel;
        Label projectPageTitle;
        RowStyle actionRowStyle;
        RowStyle statusRowStyle;
        TableLayoutPanel bodyLayout;
        RowStyle requiredRowStyle;
        int requiredBaseHeight=190;
        Control requiredCard;
        Control requiredHeading;
        TableLayoutPanel tabWorkspaceLayout;
        RowStyle tabStripRowStyle;
        TableLayoutPanel tabStrip;
        TableLayoutPanel jointPageLayout;
        Panel jointEditorPane;
        readonly List<Panel> tabPages = new List<Panel>();
        Panel actionBar;

        public event EventHandler EnvironmentDetailsExpandedChanged;

        [System.ComponentModel.Category("HVX")]
        [System.ComponentModel.DefaultValue(false)]
        public bool EnvironmentDetailsExpanded
        {
            get { return environmentDetailsExpanded; }
            set
            {
                if (InvokeRequired) { Invoke(new Action<bool>(SetEnvironmentDetailsExpanded), value); return; }
                SetEnvironmentDetailsExpanded(value);
            }
        }

        [System.ComponentModel.Browsable(false)]
        public long EnvironmentDetailsExpandedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollEnvironmentDetailsExpandedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollEnvironmentDetailsExpandedSequence, value); }
        }
        long pollEnvironmentDetailsExpandedSequence;

        [System.ComponentModel.Browsable(false)]
        public string ExpandedOptionalSectionCode { get { return expandedOptionalSectionCode; } }

        [System.ComponentModel.Browsable(false)]
        public string[] OptionalSectionCodes
        {
            get
            {
                return new[] { TestInformationSection, EndpointDetailsSection, TestConfigurationSection,
                    EnvironmentSection, InsulationSection, JointSection };
            }
        }

        public bool SetOptionalSectionExpanded(string sectionCode, bool expanded)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool, bool>(SetOptionalSectionExpanded), sectionCode, expanded);
            if (string.IsNullOrWhiteSpace(sectionCode) || !optionalSections.ContainsKey(sectionCode)) return false;
            SetAccordionSectionExpanded(sectionCode, expanded);
            return true;
        }

        [System.ComponentModel.Browsable(false)]
        public bool LayoutFitsViewport
        {
            get
            {
                if (leftScrollPanel == null || rightScrollPanel == null) return false;
                return ClientSize.Width >= 720 && ClientSize.Height >= 490 &&
                    !LeftColumnScrollRequired && !RightColumnScrollRequired && !HorizontalScrollRequired;
            }
        }

        [System.ComponentModel.Browsable(false)] public bool LeftColumnScrollRequired { get { return leftScrollPanel != null && leftScrollPanel.VerticalScroll.Visible; } }
        [System.ComponentModel.Browsable(false)] public bool RightColumnScrollRequired { get { return rightScrollPanel != null && rightScrollPanel.VerticalScroll.Visible; } }
        [System.ComponentModel.Browsable(false)] public bool HorizontalScrollRequired
        {
            get { return (leftScrollPanel != null && leftScrollPanel.HorizontalScroll.Visible) || (rightScrollPanel != null && rightScrollPanel.HorizontalScroll.Visible); }
        }

        void InitializeLayout()
        {
            SuspendLayout();
            rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = ProjectCreationTheme.Workspace,
                ColumnCount = 1,
                RowCount = 4,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            headerRowStyle = new RowStyle(SizeType.Absolute, 56);
            rootLayout.RowStyles.Add(headerRowStyle);
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actionRowStyle = new RowStyle(SizeType.Absolute, 52);
            statusRowStyle = new RowStyle(SizeType.Absolute, 24);
            rootLayout.RowStyles.Add(actionRowStyle);
            rootLayout.RowStyles.Add(statusRowStyle);
            headerPanel = BuildHeader();
            rootLayout.Controls.Add(headerPanel, 0, 0);
            rootLayout.Controls.Add(BuildBody(), 0, 1);
            rootLayout.Controls.Add(BuildActions(), 0, 2);
            rootLayout.Controls.Add(BuildStatus(), 0, 3);
            Controls.Add(rootLayout);
            SizeChanged += delegate { ApplyResponsiveLayout(); };
            FontChanged += delegate { ApplyResponsiveLayout(); };
            LanguageChanged += delegate { ApplyResponsiveLayout(); RefreshProjectActionLayout(); };
            ApplyEmbeddedMode();
            InitializeValidationPresentation();
            ResumeLayout(true);
        }

        void ApplyEmbeddedMode()
        {
            if (rootLayout == null || headerRowStyle == null || headerPanel == null) return;
            rootLayout.SuspendLayout();
            headerPanel.Visible = !embeddedMode;
            ApplyResponsiveLayout();
            rootLayout.ResumeLayout(true);
        }

        void ApplyResponsiveLayout()
        {
            if (rootLayout == null || headerRowStyle == null || applyingResponsiveLayout || IsMinimizedViewport()) return;
            string metricsKey = ResponsiveMetricsKey();
            if (String.Equals(lastResponsiveMetricsKey, metricsKey, StringComparison.Ordinal)) return;
            applyingResponsiveLayout = true;
            rootLayout.SuspendLayout();
            try
            {
            bool compact = ClientSize.Height > 0 && ClientSize.Height < 700;
            headerRowStyle.Height = embeddedMode ? 0 : compact ? 36 : 56;
            if (actionRowStyle != null) actionRowStyle.Height = compact ? 38 : 52;
            if (statusRowStyle != null) statusRowStyle.Height = Math.Max(compact ? 18 : 24,
                statusLabel == null ? 0 : statusLabel.Font.Height + 6);
            if (bodyLayout != null) bodyLayout.Padding = compact ? new Padding(2) : new Padding(12, 12, 12, 8);
            bool spacious = ClientSize.Height >= 760;
            int requiredLine = ProjectCreationTheme.UiFont(compact ? 9.5f : 10.5f).Height + 4;
            int requiredEditor = Math.Max(compact ? 35 : 48, (compact ? ProjectCreationTheme.UiFont(9.5f) : ProjectCreationTheme.ComfortableInputFont).Height + (compact ? 8 : 16));
            requiredBaseHeight=Math.Max(compact ? 148 : spacious ? 270 : 220, 2 * (requiredLine + requiredEditor + 4) + (compact ? 6 : 56));
            if (requiredRowStyle != null) requiredRowStyle.Height = requiredBaseHeight;
            if (requiredCard != null) requiredCard.Margin = compact ? new Padding(0, 0, 0, 2) : new Padding(0, 0, 0, 8);
            if (requiredHeading != null) { requiredHeading.Visible = !compact; requiredHeading.Height = compact ? 0 : 44; }
            if (requiredCard != null) SizeRequiredFields(requiredCard, !compact);
            if (tabStripRowStyle != null) tabStripRowStyle.Height = Math.Max(compact ? 32 : 40, ProjectCreationTheme.UiFont(9.5f).Height + 8);
            if (tabStrip != null)
            {
                tabStrip.Padding = compact ? new Padding(0, 1, 2, 0) : new Padding(0, 4, 4, 0);
                if (tabStrip.ColumnStyles.Count > 0) tabStrip.ColumnStyles[0].Width = compact ? 188 : 264;
            }
            if (modelOverviewLabel != null) modelOverviewLabel.Visible = false;
            if (maxLengthLabel != null)
            {
                maxLengthLabel.Dock = DockStyle.Fill;
                maxLengthLabel.Width = 146;
                maxLengthLabel.Padding = compact ? new Padding(8, 0, 8, 0) : Padding.Empty;
                FitTabSummaryWidth();
            }
            if (jointPageLayout != null) jointPageLayout.Padding = compact ? new Padding(2) : new Padding(12);
            if (jointEditorPane != null) jointEditorPane.Padding = compact ? new Padding(2) : new Padding(12);
            foreach (Panel page in tabPages) page.Padding = compact ? new Padding(2) : new Padding(12);
            if (actionBar != null) actionBar.Padding = compact ? new Padding(8, 2, 8, 2) : new Padding(12, 7, 12, 7);
            if (savedBadge != null)
            {
                savedBadge.Height = compact ? 32 : 36;
                savedBadge.Location = compact ? new Point(8, 3) : new Point(12, 7);
            }
            RefreshProjectActionLayout();
            }
            finally
            {
                rootLayout.ResumeLayout(true);
                applyingResponsiveLayout = false;
            }
            lastResponsiveMetricsKey = ResponsiveMetricsKey();
        }

        Control BuildHeader()
        {
            ProjectCreationHeader header = new ProjectCreationHeader { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(18, 0, 14, 3) };
            Label title = projectPageTitle = new Label
            {
                Text = "创建项目", Name = "ProjectPageTitle", Tag = "dynamic",
                ForeColor = Color.White,
                Font = ProjectCreationTheme.UiFont(12f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 14)
            };
            languageCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Width = 168,
                Height = 28,
                Font = ProjectCreationTheme.UiFont(8.7f),
                AccessibleName = "语言 / Language",
                Visible = false,
                TabStop = false
            };
            languageCombo.Items.AddRange(new object[] { "中文", "English" });
            languageCombo.SelectedIndex = 0;
            header.Controls.Add(title);
            deviceStateLabel = new ProjectCreationDeviceStateLabel
            {
                Text = "●  设备就绪",
                Tag = "dynamic",
                ForeColor = Color.White,
                Font = ProjectCreationTheme.UiFont(9.5f),
                AutoSize = false,
                Width = 190,
                Dock = DockStyle.Right,
                TextAlign = ContentAlignment.MiddleRight,
                UseMnemonic = false
            };
            header.Controls.Add(deviceStateLabel);
            return header;
        }

        void RefreshProjectPageTitle()
        {
            if (projectPageTitle == null) return;
            bool editing = !String.IsNullOrWhiteSpace(loadedProjectPath);
            projectPageTitle.Text = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,
                editing ? "编辑项目" : "创建项目", editing ? "Edit Project" : "Create Project");
        }

        Control BuildBody()
        {
            bodyLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(12, 12, 12, 8),
                Margin = Padding.Empty,
                BackColor = ProjectCreationTheme.Workspace
            };
            // Two readable field rows remain fixed at the 1280 x 720 whole-window minimum.
            requiredRowStyle = new RowStyle(SizeType.Absolute, 190);
            bodyLayout.RowStyles.Add(requiredRowStyle);
            bodyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            requiredCard = BuildRequiredCard();
            requiredCard.Margin = new Padding(0, 0, 0, 8);
            bodyLayout.Controls.Add(requiredCard, 0, 0);

            bodyLayout.Controls.Add(BuildTabbedWorkspace(), 0, 1);
            return bodyLayout;
        }

        Control BuildTabbedWorkspace()
        {
            Panel card = NewCard();
            tabWorkspaceLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            tabStripRowStyle = new RowStyle(SizeType.Absolute, 40);
            tabWorkspaceLayout.RowStyles.Add(tabStripRowStyle);
            tabWorkspaceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tabStrip = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
                Margin = Padding.Empty, Padding = new Padding(0, 4, 4, 0),
                BackColor = ProjectCreationTheme.SurfaceAlt
            };
            tabStrip.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tabStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 264));
            for (int i = 0; i < 6; i++) tabStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6f));
            Panel overview = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ProjectCreationTheme.SurfaceAlt };
            Label modelLabel = new Label
            {
                Text = "负载模型", Dock = DockStyle.Left, Width = 114,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
                ForeColor = ProjectCreationTheme.Ink, Font = ProjectCreationTheme.UiFont(10.5f, FontStyle.Bold)
            };
            modelOverviewLabel = modelLabel;
            maxLengthLabel = new Label
            {
                Text = "最大长度  0.0 m", Tag = "dynamic", Dock = DockStyle.Right, Width = 146,
                TextAlign = ContentAlignment.MiddleRight, ForeColor = ProjectCreationTheme.SecondaryInk,
                Font = ProjectCreationTheme.UiFont(8.2f)
            };
            maxLengthLabel.TextChanged += delegate { FitTabSummaryWidth(); };
            maxLengthLabel.FontChanged += delegate { FitTabSummaryWidth(); };
            overview.Controls.Add(modelLabel); overview.Controls.Add(maxLengthLabel);
            tabStrip.Controls.Add(overview, 0, 0);
            tabPageHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ProjectCreationTheme.Surface };
            tabWorkspaceLayout.Controls.Add(tabStrip, 0, 0); tabWorkspaceLayout.Controls.Add(tabPageHost, 0, 1);
            card.Controls.Add(tabWorkspaceLayout);

            jointPageLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty, Padding = new Padding(12),
                BackColor = ProjectCreationTheme.Surface
            };
            jointPageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            jointPageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            Control model = BuildLeftColumn();
            model.Margin = new Padding(0, 0, 8, 0);
            jointPageLayout.Controls.Add(model, 0, 0);
            jointEditorPane = NewCard();
            jointEditorPane.Margin = Padding.Empty;
            jointEditorPane.Padding = new Padding(12);
            jointEditorPane.Controls.Add(BuildJointEditor());
            jointPageLayout.Controls.Add(jointEditorPane, 1, 0);

            RegisterTab(tabStrip, 1, JointSection, "接头与本体", jointPageLayout);
            RegisterTab(tabStrip, 2, TestConfigurationSection, "加压列表", BuildCableFields());
            RegisterTab(tabStrip, 3, EndpointDetailsSection, "端点信息", BuildEndpointFields());
            RegisterTab(tabStrip, 4, TestInformationSection, "检测信息", BuildBasicFields());
            RegisterTab(tabStrip, 5, InsulationSection, "绝缘与备注", BuildInsulationFields());
            hardwareContent = BuildHardwareFields();
            RegisterTab(tabStrip, 6, EnvironmentSection, "环境与北斗", hardwareContent);
            rightScrollPanel = tabPageHost;
            SetAccordionSectionExpanded(JointSection, true);
            return card;
        }

        void FitTabSummaryWidth()
        {
            if (tabStrip == null || maxLengthLabel == null || tabStrip.ColumnStyles.Count == 0 || IsMinimizedViewport()) return;
            bool compact = ClientSize.Height > 0 && ClientSize.Height < 700;
            int textWidth = TextRenderer.MeasureText(maxLengthLabel.Text, maxLengthLabel.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
            tabStrip.ColumnStyles[0].Width = Math.Max(compact ? 188 : 264, textWidth + maxLengthLabel.Padding.Horizontal + 12);
            for (int column = 1; column < tabStrip.ColumnStyles.Count; column++)
            {
                Control button = tabStrip.GetControlFromPosition(column, 0);
                if (button == null) continue;
                tabStrip.ColumnStyles[column].SizeType = SizeType.Percent;
                tabStrip.ColumnStyles[column].Width = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 20;
            }
        }

        void RegisterTab(TableLayoutPanel strip, int column, string code, string title, Control content)
        {
            ProjectCreationTabButton tab = new ProjectCreationTabButton
            {
                Text = title, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 0, 0),
                Tag = "dynamic", AccessibleName = title + "选项卡"
            };
            strip.Controls.Add(tab, column, 0);
            tab.TextChanged += delegate { FitTabSummaryWidth(); };
            tab.FontChanged += delegate { FitTabSummaryWidth(); };
            content.Dock = DockStyle.Fill;
            content.Visible = false;
            if (code != JointSection)
            {
                Panel page = new Panel
                {
                    Dock = DockStyle.Fill, Visible = false, AutoScroll = false,
                    Padding = new Padding(12),
                    BackColor = ProjectCreationTheme.Surface
                };
                content.Dock = DockStyle.Fill;
                page.Controls.Add(content);
                content.Visible = true;
                content = page;
                tabPages.Add(page);
            }
            tabPageHost.Controls.Add(content);
            OptionalSectionState state = new OptionalSectionState
            {
                Code = code, Content = content, TabButton = tab
            };
            optionalSections[code] = state;
            tab.Click += delegate { SetAccordionSectionExpanded(code, true); };
        }

        static string TabCaption(int language, string code)
        {
            string values;
            switch (code)
            {
                case JointSection:
                    values = "接头与本体|Joints & Body|Съединения|Spojevi|Spojky|Jonctions|Muffen|Kötések|Giunti|Mufy|Îmbinări|Муфты|Спојнице|Spojky|Spojke|Empalmes|Ekler|Mối nối";
                    break;
                case TestConfigurationSection:
                    values = "加压列表|Voltage Steps|Стъпки|Koraci napona|Kroky napětí|Paliers|Stufen|Lépések|Fasi tensione|Stopnie|Trepte|Ступени|Кораци|Kroky|Koraki|Escalones|Adımlar|Bậc áp";
                    break;
                case EndpointDetailsSection:
                    values = "端点信息|Endpoints|Краища|Krajevi|Konce|Extrémités|Endpunkte|Végpontok|Estremità|Końce|Capete|Концы|Крајеви|Konce|Konci|Extremos|Uçlar|Đầu cáp";
                    break;
                case TestInformationSection:
                    values = "检测信息|Test Info|Данни тест|Ispitivanje|Zkouška|Essai|Prüfdaten|Vizsgálat|Prova|Badanie|Testare|Испытание|Испитивање|Skúška|Preskus|Ensayo|Test|Thử nghiệm";
                    break;
                case InsulationSection:
                    values = "绝缘与备注|Insulation|Изолация|Izolacija|Izolace|Isolement|Isolation|Szigetelés|Isolamento|Izolacja|Izolație|Изоляция|Изолација|Izolácia|Izolacija|Aislamiento|Yalıtım|Cách điện";
                    break;
                case EnvironmentSection:
                    values = "环境与北斗|Env. & BeiDou|Среда/BeiDou|Okoliš/BeiDou|Prostředí|Env. / BeiDou|Umwelt/BeiDou|Környezet|Ambiente|Środowisko|Mediu/BeiDou|Среда/BeiDou|Окружење|Prostredie|Okolje|Entorno|Ortam|Môi trường";
                    break;
                default: return code;
            }
            string[] captions = values.Split('|');
            if (language == 18) return Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(captions[0]);
            string translated;
            if(language!=0 && Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(language,captions[1],out translated)) return translated;
            language=Xiongxin.OWTS.Localization.UiLanguage.LegacyIndex(language);
            return captions[language];
        }

        Control BuildLeftColumn()
        {
            Panel card = NewCard();
            TableLayoutPanel content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(0, 0, 0, 0),
                Margin = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            topology = new CableTopologyCanvas { Margin = Padding.Empty };
            startTypeCombo=ChoiceCombo();startTypeCombo.Name="StartTerminalType";
            endTypeCombo=ChoiceCombo();endTypeCombo.Name="EndTerminalType";
            var terminations=new TableLayoutPanel{Name="ModelTerminationSelectors",Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
            terminations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));terminations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            Label unused;
            Control startField=VerticalField("始端类型",startTypeCombo,false,out unused,out endpointStartTypeCaptionLabel);
            Control endField=VerticalField("终端类型",endTypeCombo,false,out unused,out endpointEndTypeCaptionLabel);
            startField.Height=endField.Height=70;startField.MinimumSize=endField.MinimumSize=new Size(0,70);
            terminations.Controls.Add(startField,0,0);terminations.Controls.Add(endField,1,0);
            var topologyViewport = new Panel { Name = "CableTopologyViewport", Dock = DockStyle.Fill,
                AutoScroll = true, Margin = Padding.Empty, BackColor = ProjectCreationTheme.Surface };
            topologyViewport.Controls.Add(topology);
            ConfigureTopologyViewport(topologyViewport, topology);
            Panel footer = new Panel { Dock = DockStyle.Fill, BackColor = ProjectCreationTheme.ActiveSoft, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 6, 0, 0) };
            lengthSummaryLabel = Hint("三相长度可独立设置；定位时按当前相长度计算。");
            lengthSummaryLabel.Dock = DockStyle.Fill;
            lengthSummaryLabel.Font = ProjectCreationTheme.UiFont(8.1f);
            footer.Controls.Add(lengthSummaryLabel);
            content.Controls.Add(terminations,0,0);content.Controls.Add(topologyViewport, 0, 1); content.Controls.Add(footer, 0, 2);
            card.Controls.Add(content);
            leftScrollPanel = card;
            return card;
        }

        static void ConfigureTopologyViewport(Panel viewport, CableTopologyCanvas canvas)
        {
            bool fitting = false;
            Action fit = delegate
            {
                if (fitting || viewport.IsDisposed || canvas.IsDisposed || viewport.ClientSize.Width < 1) return;
                fitting = true;
                try
                {
                    int width = Math.Max(1, viewport.ClientSize.Width);
                    int required = canvas.MeasureContentHeight(width);
                    if (required > viewport.ClientSize.Height && !viewport.VerticalScroll.Visible)
                        width = Math.Max(1, width - SystemInformation.VerticalScrollBarWidth);
                    required = canvas.MeasureContentHeight(width);
                    viewport.AutoScrollMinSize = new Size(0, required);
                    canvas.Bounds = new Rectangle(viewport.AutoScrollPosition.X, viewport.AutoScrollPosition.Y,
                        width, Math.Max(required, viewport.ClientSize.Height));
                }
                finally { fitting = false; }
            };
            viewport.SizeChanged += delegate { fit(); };
            viewport.Layout += delegate { fit(); };
            canvas.FontChanged += delegate { fit(); };
            // Model/name changes already invalidate the canvas. Measure before
            // the next paint, while retaining scroll position for joint review.
            canvas.Invalidated += delegate { fit(); };
            fit();
        }

        Control BuildRightColumn()
        {
            TableLayoutPanel stack = NewStack();
            stack.Margin = Padding.Empty;
            Panel unusedSection; RowStyle unusedRow; ProjectCreationButton unusedButton;
            AddOptionalSection(stack, TestInformationSection, "检测信息", BuildBasicFields(), 272, out unusedSection, out unusedRow, out unusedButton);
            AddOptionalSection(stack, EndpointDetailsSection, "端点详细信息", BuildEndpointFields(), 226, out unusedSection, out unusedRow, out unusedButton);
            AddOptionalSection(stack, TestConfigurationSection, "试验配置", BuildCableFields(), 166, out unusedSection, out unusedRow, out unusedButton);
            hardwareContent = BuildHardwareFields();
            AddOptionalSection(stack, EnvironmentSection, "环境与北斗 · 硬件采集", hardwareContent, 416, out hardwareSection, out hardwareRowStyle, out hardwareToggleButton);
            AddOptionalSection(stack, InsulationSection, "绝缘记录与备注", BuildInsulationFields(), 292, out unusedSection, out unusedRow, out unusedButton);
            AddOptionalSection(stack, JointSection, "接头与本体结构", BuildJointEditor(), 340, out unusedSection, out unusedRow, out unusedButton);
            rightScrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ProjectCreationTheme.Surface, Padding = new Padding(8, 7, 8, 7) };
            rightScrollPanel.Controls.Add(stack);
            Panel card = NewCard();
            card.Controls.Add(rightScrollPanel);
            card.Controls.Add(BuildSectionHeading("补充资料", string.Empty));
            RefreshOptionalSectionLanguage();
            return card;
        }

        Control BuildRequiredCard()
        {
            Panel card = NewCard();
            card.Controls.Add(BuildRequiredFields());
            requiredHeading = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = ProjectCreationTheme.SurfaceAlt };
            requiredHeading.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen border = new Pen(ProjectCreationTheme.Border))
                    e.Graphics.DrawLine(border, 0, requiredHeading.Height - 1, requiredHeading.Width, requiredHeading.Height - 1);
            };
            Label title = new Label
            {
                Text = "线路与电气参数", Dock = DockStyle.Left, Width = 340, AutoEllipsis = true,
                Padding = new Padding(12, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ProjectCreationTheme.Ink, Font = ProjectCreationTheme.UiFont(10.5f, FontStyle.Bold)
            };
            Label requiredBadge = new Label
            {
                Text = "*", Dock = DockStyle.Left, Width = 28,
                TextAlign = ContentAlignment.MiddleCenter, ForeColor = ProjectCreationTheme.Danger,
                BackColor = Color.FromArgb(255, 246, 246), Font = ProjectCreationTheme.UiFont(8f, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 10)
            };
            validationSummaryLabel.Dock = DockStyle.Right;
            validationSummaryLabel.Width = 260;
            validationSummaryLabel.AutoEllipsis = false;
            validationSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
            Action fitHeading = delegate
            {
                title.Width = TextRenderer.MeasureText(title.Text, title.Font).Width + title.Padding.Horizontal + 12;
                int available = Math.Max(180, requiredHeading.ClientSize.Width - title.Width - requiredBadge.Width - 12);
                validationSummaryLabel.Width = Math.Min(available, TextRenderer.MeasureText(validationSummaryLabel.Text, validationSummaryLabel.Font).Width + validationSummaryLabel.Padding.Horizontal + 12);
            };
            requiredHeading.SizeChanged += delegate { fitHeading(); };
            title.TextChanged += delegate { fitHeading(); };
            validationSummaryLabel.TextChanged += delegate { fitHeading(); };
            requiredHeading.Controls.Add(validationSummaryLabel);
            requiredHeading.Controls.Add(requiredBadge);
            requiredHeading.Controls.Add(title);
            card.Controls.Add(requiredHeading);
            return card;
        }

        Control BuildRequiredFields()
        {
            startNameText = FieldText("始端名称");
            endNameText = FieldText("终端名称");
            endNameText.AutoSize = false;
            endNameText.BorderStyle = BorderStyle.None;
            cableModeCombo = ChoiceCombo(); cableModeCombo.Items.AddRange(ProjectModes.All);
            phaseVoltageCombo = ChoiceCombo();
            lengthANumber = Number(0, 1000000, 1, "0.0");
            lengthBNumber = Number(0, 1000000, 1, "0.0");
            lengthCNumber = Number(0, 1000000, 1, "0.0");
            lengthANumber.AccessibleName = "A相长度";
            lengthBNumber.AccessibleName = "B相长度";
            lengthCNumber.AccessibleName = "C相长度";

            capacitorIdRefreshButton = new ProjectCreationButton
            {
                Text = "↻",
                Tag = "dynamic",
                AccessibleName = "刷新电容编号",
                Quiet = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 0, 0),
                TabStop = false
            };
            endNameEditor = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            endNameEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            capacitorRefreshColumn = new ColumnStyle(SizeType.Absolute, 0);
            endNameEditor.ColumnStyles.Add(capacitorRefreshColumn);
            endNameEditor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            endNameText.Dock = DockStyle.Fill;
            endNameText.Margin = Padding.Empty;
            endNameEditor.Controls.Add(endNameText, 0, 0);
            endNameEditor.Controls.Add(capacitorIdRefreshButton, 1, 0);

            TableLayoutPanel content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(6, 2, 6, 2),
                Margin = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            TableLayoutPanel route = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            route.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            route.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            route.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            Label unused;
            var routeStart=(TableLayoutPanel)VerticalField("始端名称", startNameText, true, out unused, out startNameCaptionLabel);
            route.Controls.Add(routeStart, 0, 0);
            var routeMark = new TableLayoutPanel { Name="ProjectRouteDirection", Dock = DockStyle.Fill, ColumnCount=1, RowCount=2, Margin = new Padding(4,0,4,0), BackColor = ProjectCreationTheme.Surface };
            routeMark.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            routeMark.RowStyles.Add(new RowStyle(SizeType.Absolute,24));
            routeMark.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            routeCaptionLabel = new Label
            {
                Text = "线路区间",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.TopCenter,
                ForeColor = ProjectCreationTheme.SecondaryInk,
                Font = ProjectCreationTheme.UiFont(9.5f),
                AutoEllipsis = false,
                UseMnemonic = false
            };
            var routeArrow=new Panel { Name="ProjectRouteArrow", Dock=DockStyle.Top, Height=35, Margin=new Padding(0,1,0,0), BackColor=ProjectCreationTheme.Surface };
            routeArrow.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int y = routeArrow.Height / 2;
                int x1 = 5;
                int x2 = Math.Max(x1 + 10, routeArrow.Width - 5);
                using (Pen line = new Pen(ProjectCreationTheme.Active, 1.6f))
                {
                    e.Graphics.DrawLine(line, x1, y, x2, y);
                    e.Graphics.DrawLine(line, x2 - 6, y - 4, x2, y);
                    e.Graphics.DrawLine(line, x2 - 6, y + 4, x2, y);
                }
            };
            routeMark.Controls.Add(routeCaptionLabel,0,0);
            routeMark.Controls.Add(routeArrow,0,1);
            route.Controls.Add(routeMark, 1, 0);
            var routeEnd=(TableLayoutPanel)VerticalField("终端名称", endNameEditor, true, out unused, out endNameCaptionLabel);
            route.Controls.Add(routeEnd, 2, 0);

            electricalFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = Padding.Empty };
            electricalFields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            electricalFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            electricalFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            electricalFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            electricalFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            electricalFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            electricalFields.Controls.Add(VerticalField("负载类型", cableModeCombo, false, out unused, out loadTypeCaptionLabel), 0, 0);
            electricalFields.Controls.Add(VerticalField("相电压", phaseVoltageCombo, true, out unused), 1, 0);
            lengthAField = VerticalField("A相长度 / m", lengthANumber, true, out lengthARequiredMark, out lengthACaptionLabel);
            lengthBField = VerticalField("B相长度 / m", lengthBNumber, true, out lengthBRequiredMark, out lengthBCaptionLabel);
            lengthCField = VerticalField("C相长度 / m", lengthCNumber, true, out lengthCRequiredMark, out lengthCCaptionLabel);
            electricalFields.Controls.Add(lengthAField, 2, 0);
            electricalFields.Controls.Add(lengthBField, 3, 0);
            electricalFields.Controls.Add(lengthCField, 4, 0);

            validationSummaryLabel = new Label
            {
                Text = "带 * 的字段为必填项",
                Tag = "dynamic",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ProjectCreationTheme.SecondaryInk,
                Font = ProjectCreationTheme.UiFont(8.1f),
                Padding = new Padding(4, 0, 4, 0),
                AutoEllipsis = true,
                UseMnemonic = false
            };
            content.Controls.Add(route, 0, 0);
            content.Controls.Add(electricalFields, 0, 1);
            ConfigureRouteFieldAlignment(content,route,routeStart,routeEnd,routeMark,routeArrow);
            return content;
        }

        void ConfigureRouteFieldAlignment(TableLayoutPanel content,TableLayoutPanel route,TableLayoutPanel start,TableLayoutPanel end,TableLayoutPanel mark,Panel arrow)
        {
            bool fitting=false;
            var fields=new[]{start,end};
            var frames=fields.Select(f=>f.Controls.OfType<ProjectCreationInputFrame>().Single()).ToArray();
            Action fit=delegate
            {
                if(fitting)return;
                fitting=true;
                try
                {
                    int captionHeight=TextRenderer.MeasureText(routeCaptionLabel.Text,routeCaptionLabel.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Height;
                    foreach(var field in fields)
                    {
                        var caption=field.Controls.OfType<FlowLayoutPanel>().Single();
                        captionHeight=Math.Max(captionHeight,caption.Controls.Cast<Control>().Select(c=>c.PreferredSize.Height+c.Margin.Vertical).DefaultIfEmpty(0).Max());
                    }
                    captionHeight+=4;
                    int frameHeight=Math.Max(ClientSize.Height>0&&ClientSize.Height<700?35:48,
                        frames.Max(f=>EditableDescendants(f).Select(c=>c.Font.Height+2+f.Padding.Vertical).DefaultIfEmpty(35).Max()));
                    foreach(var field in fields)field.RowStyles[0].Height=captionHeight;
                    foreach(var frame in frames)frame.Height=frameHeight;
                    mark.RowStyles[0].Height=captionHeight;
                    arrow.Height=frameHeight;
                    int textWidth=TextRenderer.MeasureText(routeCaptionLabel.Text,routeCaptionLabel.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                    route.ColumnStyles[1].Width=Math.Max(90,textWidth+20+mark.Margin.Horizontal);
                    // Both required rows must retain their full field height;
                    // the direction marker shares the name editors' first row.
                    int minimumRow=captionHeight+frameHeight+frames.Max(f=>f.Margin.Vertical)+4;
                    foreach(var field in electricalFields.Controls.OfType<TableLayoutPanel>())
                    {
                        var frame=field.Controls.OfType<ProjectCreationInputFrame>().FirstOrDefault();
                        if(frame!=null)minimumRow=Math.Max(minimumRow,(int)Math.Ceiling(field.RowStyles[0].Height)+frame.Height+frame.Margin.Vertical+4);
                    }
                    if(requiredCard!=null&&requiredRowStyle!=null)
                    {
                        int total=2*minimumRow+content.Padding.Vertical+requiredCard.Padding.Vertical+requiredCard.Margin.Vertical+
                            (requiredHeading!=null&&requiredHeading.Visible?requiredHeading.Height:0);
                        int height=Math.Max(requiredBaseHeight,total);
                        if(requiredRowStyle.Height!=height)requiredRowStyle.Height=height;
                    }
                    arrow.Invalidate();
                }
                finally{fitting=false;}
            };
            content.Layout+=(s,e)=>fit();route.SizeChanged+=(s,e)=>fit();
            routeCaptionLabel.FontChanged+=(s,e)=>fit();routeCaptionLabel.TextChanged+=(s,e)=>fit();
            foreach(var field in fields)
            {
                var caption=field.Controls.OfType<FlowLayoutPanel>().Single();
                foreach(Control label in caption.Controls){label.FontChanged+=(s,e)=>fit();label.TextChanged+=(s,e)=>fit();}
            }
            foreach(var frame in frames)
            {
                frame.SizeChanged+=(s,e)=>fit();frame.PaddingChanged+=(s,e)=>fit();
                foreach(var editor in EditableDescendants(frame))editor.FontChanged+=(s,e)=>fit();
            }
            fit();
        }

        static Panel NewCard()
        {
            Panel card = new Panel { Dock = DockStyle.Fill, BackColor = ProjectCreationTheme.Surface, Margin = Padding.Empty, Padding = new Padding(1) };
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                Rectangle area = new Rectangle(0, 0, Math.Max(0, card.Width - 1), Math.Max(0, card.Height - 1));
                using (Pen pen = new Pen(ProjectCreationTheme.Border)) e.Graphics.DrawRectangle(pen, area);
            };
            return card;
        }

        static Panel BuildSectionHeading(string title, string eyebrow)
        {
            Panel heading = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = ProjectCreationTheme.SurfaceAlt, Padding = new Padding(12, 0, 10, 0) };
            Label headingLabel = new Label
            {
                Text = title,
                Dock = string.IsNullOrWhiteSpace(eyebrow) ? DockStyle.Fill : DockStyle.Left,
                Width = 250,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ProjectCreationTheme.Ink,
                Font = ProjectCreationTheme.UiFont(10.5f, FontStyle.Bold),
                AutoEllipsis = true,
                UseMnemonic = false
            };
            heading.Controls.Add(headingLabel);
            if (!string.IsNullOrWhiteSpace(eyebrow))
            {
                Label eyebrowLabel = new Label
                {
                    Text = eyebrow,
                    Tag = "dynamic",
                    Dock = DockStyle.Right,
                    Width = 190,
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = ProjectCreationTheme.SecondaryInk,
                    Font = ProjectCreationTheme.UiFont(7.2f, FontStyle.Bold),
                    AutoEllipsis = true,
                    UseMnemonic = false
                };
                heading.Controls.Add(eyebrowLabel);
            }
            return heading;
        }

        static Control VerticalField(string label, Control input, bool required, out Label requiredMarker)
        {
            Label captionLabel;
            return VerticalField(label, input, required, out requiredMarker, out captionLabel);
        }

        static Control VerticalField(string label, Control input, bool required, out Label requiredMarker, out Label captionLabel)
        {
            TableLayoutPanel field = new TableLayoutPanel
            {
                Name = "ProjectField",
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(4, 0, 4, 0),
                Padding = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // 9.5 pt Microsoft YaHei UI reports a 21 px preferred label height
            // at 96 DPI.  Keep the full glyph box visible in compact mode.
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, ProjectCreationTheme.UiFont(9.5f).Height + 4));
            field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel caption = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = ProjectCreationTheme.Surface
            };
            captionLabel = new Label
            {
                Text = label,
                AutoSize = true,
                Margin = new Padding(0, 0, 3, 0),
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = ProjectCreationTheme.Ink,
                Font = ProjectCreationTheme.UiFont(9.5f),
                AutoEllipsis = false,
                UseMnemonic = false
            };
            requiredMarker = new Label
            {
                Text = required ? "*" : string.Empty,
                Tag = "dynamic",
                AutoSize = true,
                Margin = Padding.Empty,
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = ProjectCreationTheme.Danger,
                Font = ProjectCreationTheme.UiFont(9f, FontStyle.Bold)
            };
            caption.Controls.Add(captionLabel); caption.Controls.Add(requiredMarker);
            Control presentedInput = input;
            bool mainField = required || input is TextBox || input is NumericUpDown || input is ProjectCreationChoiceBox || input is Label || input is Button;
            if (mainField)
            {
                ProjectCreationInputFrame frame = new ProjectCreationInputFrame
                {
                    Dock = DockStyle.Top, Height = Math.Max(input is TextBox && ((TextBox)input).Multiline ? 42 : 35, Math.Max(input.Font.Height + 6, input is ComboBox ? ((ComboBox)input).PreferredHeight + 4 : 0)),
                    Margin = new Padding(0, 1, 0, 0),
                    Padding = input is NumericUpDown ? new Padding(4, 2, 4, 2)
                        : input is TextBox ? new Padding(6, 2, 6, 2)
                        : new Padding(1, 2, 1, 2)
                };
                TextBox textBox = input as TextBox;
                if (textBox != null) { textBox.AutoSize = false; textBox.BorderStyle = BorderStyle.None; }
                NumericUpDown number = input as NumericUpDown;
                if (number != null) number.BorderStyle = BorderStyle.None;
                Label valueLabel = input as Label;
                if (valueLabel != null) { valueLabel.BorderStyle = BorderStyle.None; valueLabel.BackColor = ProjectCreationTheme.Surface; }
                input.Dock = DockStyle.Fill;
                input.Margin = Padding.Empty;
                frame.Controls.Add(input);
                FitFieldWidthOnLayout(frame, input);
                presentedInput = frame;
            }
            else if (input is ProjectCreationChoiceBox)
            {
                Panel frame = new ProjectCreationInputFrame
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 1, 0, 0)
                };
                input.Dock = DockStyle.Fill;
                input.Margin = Padding.Empty;
                frame.Controls.Add(input);
                FitFieldWidthOnLayout(frame, input);
                presentedInput = frame;
            }
            else
            {
                input.Dock = DockStyle.Fill;
                input.Margin = new Padding(0, 1, 0, 0);
            }
            field.Controls.Add(caption, 0, 0); field.Controls.Add(presentedInput, 0, 1);
            FitFieldWidthOnLayout(field, presentedInput);
            return field;
        }

        static void FitFieldWidthOnLayout(Control parent, Control input)
        {
            // Native single-line editors can retain their preferred width when
            // a previously hidden accordion page first becomes visible.
            Action fit = delegate
            {
                if (parent.ClientSize.Width <= 0) return;
                int width = Math.Max(1, parent.ClientSize.Width - parent.Padding.Horizontal - input.Margin.Horizontal);
                if (input.Width != width) input.Width = width;
            };
            parent.Layout += delegate { fit(); };
            input.SizeChanged += delegate { fit(); };
            input.HandleCreated += delegate { fit(); };
        }

        static TableLayoutPanel NewStack()
        {
            TableLayoutPanel stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                RowCount = 0,
                BackColor = ProjectCreationTheme.Workspace
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return stack;
        }

        static Panel AddSection(TableLayoutPanel stack, string title, Control content, int height)
        {
            Panel section = new Panel
            {
                Dock = DockStyle.Top,
                Height = height,
                Margin = new Padding(0, 0, 0, 6),
                BackColor = ProjectCreationTheme.Surface
            };
            section.Paint += delegate(object sender, PaintEventArgs e)
            {
                Rectangle area = new Rectangle(0, 0, section.Width - 1, section.Height - 1);
                using (Pen pen = new Pen(ProjectCreationTheme.Border)) e.Graphics.DrawRectangle(pen, area);
            };
            Panel heading = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                BackColor = ProjectCreationTheme.SurfaceAlt,
                Padding = Padding.Empty
            };
            Label headingLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(12, 7, 0, 0),
                BackColor = ProjectCreationTheme.SurfaceAlt,
                ForeColor = ProjectCreationTheme.Ink,
                Font = ProjectCreationTheme.UiFont(9.2f, FontStyle.Bold),
                AutoEllipsis = true,
                UseMnemonic = false
            };
            heading.Controls.Add(headingLabel);
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(8, 6, 8, 6);
            section.Controls.Add(content);
            section.Controls.Add(heading);
            stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.Absolute, height + 6));
            stack.Controls.Add(section, 0, stack.RowCount - 1);
            return section;
        }

        void AddOptionalSection(TableLayoutPanel stack, string code, string title, Control content, int expandedHeight,
            out Panel section, out RowStyle rowStyle, out ProjectCreationButton toggleButton)
        {
            section = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 42,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = ProjectCreationTheme.Surface
            };
            Panel capturedSection = section;
            section.Paint += delegate(object sender, PaintEventArgs e)
            {
                Rectangle area = new Rectangle(0, 0, Math.Max(0, capturedSection.Width - 1), Math.Max(0, capturedSection.Height - 1));
                using (Pen pen = new Pen(ProjectCreationTheme.Border)) e.Graphics.DrawRectangle(pen, area);
                OptionalSectionState current;
                if (optionalSections.TryGetValue(code, out current) && current.Expanded)
                    using (SolidBrush accent = new SolidBrush(ProjectCreationTheme.Active)) e.Graphics.FillRectangle(accent, 0, 0, 3, capturedSection.Height);
            };
            TableLayoutPanel heading = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = ProjectCreationTheme.SurfaceAlt
            };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            Label headingLabel = new Label
            {
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ProjectCreationTheme.Ink,
                Font = ProjectCreationTheme.UiFont(9f, FontStyle.Bold),
                AutoEllipsis = true,
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            toggleButton = new ProjectCreationButton
            {
                Text = "⌄",
                Tag = "dynamic",
                AccessibleName = "展开" + title,
                Dock = DockStyle.Fill,
                Height = 28,
                Quiet = true,
                Margin = new Padding(2, 6, 6, 6)
            };
            heading.Controls.Add(headingLabel, 0, 0); heading.Controls.Add(toggleButton, 1, 0);
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(10, 8, 10, 8);
            content.Visible = false;
            section.Controls.Add(content); section.Controls.Add(heading);
            stack.RowCount++;
            rowStyle = new RowStyle(SizeType.Absolute, 46);
            stack.RowStyles.Add(rowStyle);
            stack.Controls.Add(section, 0, stack.RowCount - 1);
            OptionalSectionState state = new OptionalSectionState
            {
                Code = code,
                Section = section,
                Content = content,
                RowStyle = rowStyle,
                ToggleButton = toggleButton,
                ExpandedHeight = Math.Max(90, expandedHeight)
            };
            optionalSections[code] = state;
            string capturedCode = code;
            EventHandler headerToggle = delegate { SetAccordionSectionExpanded(capturedCode, !optionalSections[capturedCode].Expanded); };
            heading.Cursor = Cursors.Hand;
            heading.Click += headerToggle;
            headingLabel.Click += headerToggle;
            if (!string.Equals(code, EnvironmentSection, StringComparison.OrdinalIgnoreCase))
            {
                toggleButton.Click += delegate { SetAccordionSectionExpanded(capturedCode, !optionalSections[capturedCode].Expanded); };
            }
        }

        void SetEnvironmentDetailsExpanded(bool expanded)
        {
            if (IsDisposed || Disposing) return;
            if (!optionalSections.ContainsKey(EnvironmentSection)) return;
            SetAccordionSectionExpanded(EnvironmentSection, expanded);
        }

        void SetAccordionSectionExpanded(string code, bool expanded)
        {
            if (tabPageHost != null)
            {
                if (!expanded) code = JointSection;
                if (!optionalSections.ContainsKey(code)) return;
                bool wasEnvironment = environmentDetailsExpanded;
                tabPageHost.SuspendLayout();
                foreach (OptionalSectionState tabState in optionalSections.Values)
                {
                    bool selected = string.Equals(tabState.Code, code, StringComparison.OrdinalIgnoreCase);
                    tabState.Expanded = selected;
                    tabState.Content.Visible = selected;
                    tabState.TabButton.Selected = selected;
                    if (selected) tabState.Content.BringToFront();
                }
                tabPageHost.ResumeLayout(true);
                expandedOptionalSectionCode = code;
                environmentDetailsExpanded = code == EnvironmentSection;
                RefreshOptionalSectionLanguage();
                if (wasEnvironment != environmentDetailsExpanded)
                {
                    System.Threading.Interlocked.Increment(ref pollEnvironmentDetailsExpandedSequence);
                    RaiseSafely(EnvironmentDetailsExpandedChanged);
                }
                return;
            }
            bool oldEnvironment = environmentDetailsExpanded;
            foreach (OptionalSectionState state in optionalSections.Values)
            {
                bool show = expanded && string.Equals(state.Code, code, StringComparison.OrdinalIgnoreCase);
                state.Expanded = show;
                state.Content.Visible = show;
                int sectionHeight = show ? state.ExpandedHeight : 42;
                state.RowStyle.Height = sectionHeight + 4;
                state.Section.Height = sectionHeight;
                state.ToggleButton.Primary = false;
                state.ToggleButton.Invalidate();
                state.Section.Invalidate();
            }
            expandedOptionalSectionCode = expanded ? code : string.Empty;
            environmentDetailsExpanded = optionalSections[EnvironmentSection].Expanded;
            RefreshOptionalSectionLanguage();
            if (rightScrollPanel != null)
            {
                rightScrollPanel.AutoScrollPosition = Point.Empty;
                rightScrollPanel.PerformLayout();
                if (expanded)
                {
                    Control section = optionalSections[code].Section;
                    int offset = rightScrollPanel.PointToClient(section.PointToScreen(Point.Empty)).Y - rightScrollPanel.Padding.Top;
                    rightScrollPanel.AutoScrollPosition = new Point(0, Math.Max(0, offset));
                }
            }
            if (oldEnvironment != environmentDetailsExpanded)
            {
                System.Threading.Interlocked.Increment(ref pollEnvironmentDetailsExpandedSequence);
                RaiseSafely(EnvironmentDetailsExpandedChanged);
            }
        }

        void RefreshOptionalSectionLanguage()
        {
            foreach (OptionalSectionState state in optionalSections.Values)
            {
                if (state.TabButton != null)
                {
                    state.TabButton.Text = TabCaption(languageIndex, state.Code);
                    state.TabButton.AccessibleName = state.TabButton.Text + " " + L("选项卡");
                    continue;
                }
                state.ToggleButton.Text = state.Expanded ? "⌃" : "⌄";
                state.ToggleButton.AccessibleName = L(state.Expanded ? "收起" : "展开") + " " + state.Code;
            }
        }

        Control BuildBasicFields()
        {
            organizationCombo = HistoryCombo("检测单位");
            operatorCombo = HistoryCombo("测试人员");
            testLocationText = FieldText("测试地点");
            testDatePicker = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Margin = Padding.Empty };
            dualEndedCheck = new CheckBox { Text = "双端测试（数据文件追加 D）", Dock = DockStyle.Fill, ForeColor = ProjectCreationTheme.Ink, AutoSize = false, Margin = Padding.Empty };
            TableLayoutPanel grid = CompactGrid(4);
            AddCompactPair(grid, 0, "检测单位", organizationCombo, "测试人员", operatorCombo);
            AddCompactPair(grid, 1, "测试地点", testLocationText, "测试时间", testDatePicker);
            Label unused;
            Control testMode = VerticalField("检测方式", dualEndedCheck, false, out unused);
            grid.Controls.Add(testMode, 0, 2); grid.SetColumnSpan(testMode, 2);
            // The mode field needs the same full caption/input height as the
            // other fields; 54 px clipped its checkbox under display scaling.
            // The fourth row is intentionally empty space, not an operator hint.
            ConfigureCompactFieldRows(grid, 3);
            return grid;
        }

        Control BuildHardwareFields()
        {
            temperatureNumber = Number(-80, 120, 1, "0.0");
            humidityNumber = Number(0, 100, 1, "0.0");
            temperatureNumber.ReadOnly = true;
            humidityNumber.ReadOnly = true;
            weatherText = FieldText("天气描述");
            acquireHardwareButton = new ProjectCreationButton { Text = "读取硬件", Primary = true, Dock = DockStyle.Fill, Height = 30, Margin = Padding.Empty };
            weatherRefreshButton = new ProjectCreationButton { Text = "刷新天气", Dock = DockStyle.Fill, Height = 30, Margin = Padding.Empty };
            latitudeValue = ValueLabel("—"); longitudeValue = ValueLabel("—");
            altitudeValue = ValueLabel("—"); fixStatusValue = ValueLabel("未采集"); sampleTimeValue = ValueLabel("—");
            hardwareSourceLabel = ValueLabel("硬件");
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 4,
                Margin = Padding.Empty, Padding = Padding.Empty, BackColor = ProjectCreationTheme.Surface
            };
            for (int column = 0; column < 4; column++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            for (int row = 0; row < 3; row++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3f));
            Label unused;
            grid.Controls.Add(VerticalField("温度 / ℃", temperatureNumber, false, out unused), 0, 0);
            grid.Controls.Add(VerticalField("湿度 / %", humidityNumber, false, out unused), 1, 0);
            grid.Controls.Add(VerticalField("天气", weatherText, false, out unused), 2, 0);
            grid.Controls.Add(VerticalField("联网天气", weatherRefreshButton, false, out unused), 3, 0);
            grid.Controls.Add(VerticalField("北斗纬度", latitudeValue, false, out unused), 0, 1);
            grid.Controls.Add(VerticalField("北斗经度", longitudeValue, false, out unused), 1, 1);
            grid.Controls.Add(VerticalField("海拔 / m", altitudeValue, false, out unused), 2, 1);
            grid.Controls.Add(VerticalField("定位状态", fixStatusValue, false, out unused), 3, 1);
            Control sampled = VerticalField("采样时间", sampleTimeValue, false, out unused);
            grid.Controls.Add(sampled, 0, 2); grid.SetColumnSpan(sampled, 2);
            grid.Controls.Add(VerticalField("数据源", hardwareSourceLabel, false, out unused), 2, 2);
            grid.Controls.Add(VerticalField("硬件采集", acquireHardwareButton, false, out unused), 3, 2);
            ConfigureEndpointFieldSpacing(grid);
            grid.Name="EnvironmentInformationFields";
            var environmentOptions = new FlowLayoutPanel { Dock=DockStyle.Top,Height=50,WrapContents=false,Margin=Padding.Empty };
            var limitsButton = new ProjectCreationButton { Text=EnvironmentText("温湿度范围与提醒","Temperature / humidity limits"),Width=300,Height=Math.Max(34,ProjectCreationTheme.UiFont(9.5f).Height+12),Margin=new Padding(4,1,4,1),Tag="dynamic" };
            limitsButton.Click += delegate { ShowEnvironmentLimits(); };
            LanguageChanged += delegate { limitsButton.Text=EnvironmentText("温湿度范围与提醒","Temperature / humidity limits"); };
            environmentOptions.Controls.Add(limitsButton);
            grid.Controls.Add(environmentOptions,0,3);grid.SetColumnSpan(environmentOptions,4);
            environmentOptions.Height = limitsButton.Height + limitsButton.Margin.Vertical;
            grid.RowStyles[3] = new RowStyle(SizeType.Absolute, environmentOptions.Height);
            grid.RowCount = 5;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return grid;
        }

        Control BuildEndpointFields()
        {
            dispatchStartText = FieldText("始端调度号");
            dispatchEndText = FieldText("终端调度号");
            lineNameText = FieldText("线路名称"); cableModelText = FieldText("电缆型号");
            TableLayoutPanel grid = CompactGrid(3);
            for (int row = 0; row < grid.RowStyles.Count; row++) grid.RowStyles[row].Height = 60;
            Label unused;
            grid.Controls.Add(VerticalField("始端调度号", dispatchStartText, false, out unused, out endpointStartIdCaptionLabel), 0, 0);
            grid.Controls.Add(VerticalField("终端调度号", dispatchEndText, false, out unused, out endpointEndIdCaptionLabel), 1, 0);
            grid.Controls.Add(VerticalField("线路名称", lineNameText, false, out unused, out endpointLineCaptionLabel), 0, 1);
            grid.Controls.Add(VerticalField("电缆型号", cableModelText, false, out unused, out endpointModelCaptionLabel), 1, 1);
            ConfigureEndpointFieldSpacing(grid);
            return grid;
        }

        static void ConfigureEndpointFieldSpacing(TableLayoutPanel grid)
        {
            // Three related rows belong together, not at 0%, 33% and 66% of a
            // maximized page. Spare space stays below the form, while editors
            // get larger hit targets whenever the viewport permits it.
            grid.Name="EndpointInformationFields";grid.RowCount=4;
            grid.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            bool fitting=false;
            Action fit=delegate
            {
                if(fitting||grid.ClientSize.Height<1)return;
                fitting=true;grid.SuspendLayout();
                try
                {
                    bool comfortable=grid.ClientSize.Height >= (grid.Name == "EnvironmentInformationFields" ? 390 : 300);
                    int requiredRow = 0;
                    foreach(Control control in grid.Controls)
                    {
                        var field=control as TableLayoutPanel;
                        if(field==null||field.RowStyles.Count!=2)continue;
                        var frame=field.Controls.OfType<ProjectCreationInputFrame>().FirstOrDefault();
                        if(frame==null||frame.Controls.Count!=1)continue;
                        Control input=frame.Controls[0];input.Font=comfortable?ProjectCreationTheme.ComfortableInputFont:ProjectCreationTheme.UiFont(9.5f);
                        int caption=ProjectCreationTheme.UiFont(9.5f).Height+4;
                        int inner=input is ComboBox?((ComboBox)input).PreferredHeight:input.Font.Height+2;
                        int editor=Math.Max(comfortable?48:35,inner+(comfortable?14:4));
                        int row=caption+editor+(comfortable?16:1);
                        field.RowStyles[0].Height=caption;frame.Height=editor;
                        int vertical=Math.Max(2,(editor-inner)/2);
                        frame.Padding=new Padding(input is TextBox?8:2,vertical,input is TextBox?8:2,vertical);
                        requiredRow = Math.Max(requiredRow, row);
                        field.Margin=comfortable?new Padding(8,4,8,4):new Padding(4,0,4,0);
                    }
                    for(int index=0;index<3;index++){grid.RowStyles[index].SizeType=SizeType.Absolute;grid.RowStyles[index].Height=requiredRow;}
                }
                finally{grid.ResumeLayout(true);fitting=false;}
            };
            grid.SizeChanged+=(s,e)=>fit();grid.VisibleChanged+=(s,e)=>fit();grid.FontChanged+=(s,e)=>fit();
        }

        static void SizeRequiredFields(Control root, bool comfortable)
        {
            foreach(Control child in root.Controls)
            {
                var field=child as TableLayoutPanel;
                if(field!=null&&field.Name=="ProjectField")
                {
                    var frame=field.Controls.OfType<ProjectCreationInputFrame>().FirstOrDefault();
                    if(frame!=null&&frame.Controls.Count==1)
                    {
                        Control input=frame.Controls[0];
                        foreach(Control editor in EditableDescendants(input))
                            editor.Font=comfortable?ProjectCreationTheme.ComfortableInputFont:ProjectCreationTheme.UiFont(9.5f);
                        var caption=field.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
                        if(caption!=null)foreach(Label label in caption.Controls.OfType<Label>())
                            label.Font=ProjectCreationTheme.UiFont(comfortable?10.5f:9.5f,FontStyle.Bold);
                        int line=comfortable?ProjectCreationTheme.UiFont(10.5f).Height:ProjectCreationTheme.UiFont(9.5f).Height;
                        field.RowStyles[0].Height=line+4;
                        int preferred = EditableDescendants(input).Select(c=>c is ComboBox ? ((ComboBox)c).PreferredHeight : c.Font.Height+2).DefaultIfEmpty(input.Font.Height+2).Max();
                        frame.Height=Math.Max(comfortable?48:35,preferred+(comfortable?14:4));
                        frame.Padding=input is TextBox?new Padding(8,comfortable?7:2,8,comfortable?7:2):new Padding(2,comfortable?5:2,2,comfortable?5:2);
                    }
                }
                else SizeRequiredFields(child,comfortable);
            }
        }

        static IEnumerable<Control> EditableDescendants(Control root)
        {
            if(root is TextBox||root is ComboBox||root is NumericUpDown){yield return root;yield break;}
            foreach(Control child in root.Controls)foreach(Control editor in EditableDescendants(child))yield return editor;
        }

        static void ConfigureCompactFieldRows(TableLayoutPanel grid,int fieldRows)
        {
            bool fitting=false;
            Action fit=delegate
            {
                if(fitting||grid.ClientSize.Height<1)return;
                fitting=true;grid.SuspendLayout();
                try
                {
                    bool comfortable=grid.ClientSize.Height>=340;
                    SizeRequiredFields(grid,comfortable);
                    int row=comfortable?Math.Max(88,ProjectCreationTheme.ComfortableInputFont.Height+ProjectCreationTheme.UiFont(10.5f).Height+34):Math.Max(72,ProjectCreationTheme.UiFont(9.5f).Height*2+20);
                    for(int i=0;i<fieldRows;i++){grid.RowStyles[i].SizeType=SizeType.Absolute;grid.RowStyles[i].Height=row;}
                    // The last hint/notes area can take the spare space; input
                    // rows above are never distributed across the whole page.
                    int final=grid.RowStyles.Count-1;grid.RowStyles[final].SizeType=SizeType.Percent;grid.RowStyles[final].Height=100;
                }
                finally{grid.ResumeLayout(true);fitting=false;}
            };
            grid.SizeChanged+=(s,e)=>fit();grid.VisibleChanged+=(s,e)=>fit();
        }

        Control BuildInsulationFields()
        {
            insulationA1Text = FieldText("A-1"); insulationA2Text = FieldText("A-2");
            insulationB1Text = FieldText("B-1"); insulationB2Text = FieldText("B-2");
            insulationC1Text = FieldText("C-1"); insulationC2Text = FieldText("C-2");
            notesText = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, Margin = Padding.Empty };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            TableLayoutPanel grid = CompactGrid(4);
            var captions = new List<Label>();
            TextBox[] inputs = { insulationA1Text, insulationA2Text, insulationB1Text, insulationB2Text, insulationC1Text, insulationC2Text };
            for (int i = 0; i < inputs.Length; i++)
            {
                Label marker, caption;
                Control field = VerticalField("", inputs[i], false, out marker, out caption);
                caption.Tag = "dynamic"; captions.Add(caption);
                grid.Controls.Add(field, i % 2, i / 2);
            }
            Label unused;
            Control notesField = VerticalField("备注", notesText, false, out unused);
            ConfigureCompactFieldRows(grid, 3);
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(8, 0, 0, 0) };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Only the explanatory reference panel may scroll vertically on a small
            // display. Measurements and the project action buttons stay visible.
            var referenceViewport = new Panel { Name = "InsulationReferenceViewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            Control guidance = BuildInsulationGuidance();
            guidance.Dock = DockStyle.Top;
            referenceViewport.Controls.Add(guidance);
            detail.Controls.Add(notesField, 0, 0); detail.Controls.Add(referenceViewport, 0, 1);
            layout.Controls.Add(grid, 0, 0); layout.Controls.Add(detail, 1, 0);
            bool fitting = false;
            Action fitReference = delegate
            {
                if (fitting || referenceViewport.ClientSize.Width < 1) return;
                fitting = true;
                try
                {
                    // Reserve the vertical-scroll gutter even when unused, avoiding
                    // the layout oscillating between a fitting and overflowing width.
                    int width = Math.Max(100, referenceViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
                    guidance.Width = width;
                    var top = (TableLayoutPanel)insulationReferenceCombo.Parent;
                    top.SuspendLayout();
                    top.ColumnCount = 2; top.RowCount = 2; top.ColumnStyles.Clear();
                    top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    top.RowStyles.Clear(); top.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); top.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(36, insulationReferenceCombo.PreferredHeight + 6)));
                    top.SetCellPosition(insulationDetailsButton, new TableLayoutPanelCellPosition(1, 0));
                    top.SetCellPosition(insulationReferenceCombo, new TableLayoutPanelCellPosition(0, 1)); top.SetColumnSpan(insulationReferenceCombo, 2);
                    top.ResumeLayout(true);
                    insulationGuidancePanel.ColumnStyles.Clear(); insulationGuidancePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    insulationGuidancePanel.RowStyles[0].Height = top.RowStyles[0].Height + top.RowStyles[1].Height;
                    int textWidth = Math.Max(100, width - 24);
                    int checkHeight = TextRenderer.MeasureText(insulationComparableCheck.Text, insulationComparableCheck.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + 10;
                    int statusHeight = TextRenderer.MeasureText(insulationGuidanceStatus.Text, insulationGuidanceStatus.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak).Height + 10;
                    insulationGuidancePanel.RowStyles[1].Height = checkHeight;
                    guidance.Height = (int)insulationGuidancePanel.RowStyles[0].Height + checkHeight + statusHeight + guidance.Padding.Vertical;
                    referenceViewport.AutoScrollMinSize = new Size(0, guidance.Height);
                    var notesFrame = notesText.Parent as ProjectCreationInputFrame;
                    if (notesFrame != null) notesFrame.Dock = DockStyle.Fill;
                }
                finally { fitting = false; }
            };
            Action refresh = delegate
            {
                for (int i = 0; i < captions.Count; i++) captions[i].Text = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,
                    ((char)('A' + i / 2)) + "相" + (i % 2 == 0 ? "首次测量 / MΩ" : "二次测量 / MΩ"),
                    "L" + (i / 2 + 1) + (i % 2 == 0 ? " first / MΩ" : " second / MΩ"));
                RefreshInsulationGuidance(); fitReference();
            };
            referenceViewport.SizeChanged += delegate { fitReference(); };
            referenceViewport.VisibleChanged += delegate { fitReference(); };
            guidance.Layout += delegate { fitReference(); };
            insulationGuidanceStatus.TextChanged += delegate { fitReference(); };
            LanguageChanged += delegate { refresh(); };
            refresh();
            return layout;
        }

        Control BuildCableFields()
        {
            layingDatePicker = new DateTimePicker { Dock = DockStyle.Fill, Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Margin = Padding.Empty };
            pressureModeCombo = ChoiceCombo(); pressureModeCombo.Items.AddRange(new object[] { "旧电缆", "新电缆", "自定义", "任意值" });
            pressureEditButton = new ProjectCreationButton { Text = "编辑加压列表", Primary = true, Margin = new Padding(0, 2, 8, 2) };
            pressureSaveAsButton = new ProjectCreationButton { Text = "另存为", Margin = new Padding(0, 2, 8, 2) };
            pressureLoadButton = new ProjectCreationButton { Text = "加载列表", Margin = new Padding(0, 2, 0, 2) };
            pressurePlanLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
            pressurePlanLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(72, ProjectCreationTheme.UiFont(9.5f).Height * 2 + 20)));
            pressurePlanLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            pressurePlanLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pressurePlanLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            TableLayoutPanel fields = CompactGrid(1);
            AddCompactPair(fields, 0, "铺设时间", layingDatePicker, "加压模式", pressureModeCombo);
            pressurePlanSummary = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = ProjectCreationTheme.SecondaryInk,
                Tag = "dynamic", AutoEllipsis = true, UseMnemonic = false, Margin = new Padding(4, 0, 4, 0) };
            pressurePreviewGrid = new DataGridView
            {
                Name = "VoltagePlanPreview", Dock = DockStyle.Fill, BackgroundColor = ProjectCreationTheme.Surface, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, EnableHeadersVisualStyles = false, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ScrollBars = ScrollBars.Vertical, Margin = new Padding(4, 0, 4, 4), AllowUserToResizeRows = false
            };
            pressurePreviewGrid.ColumnHeadersDefaultCellStyle.BackColor = ProjectCreationTheme.SurfaceAlt;
            pressurePreviewGrid.ColumnHeadersDefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            pressurePreviewGrid.DefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            pressurePreviewGrid.DefaultCellStyle.SelectionBackColor = ProjectCreationTheme.Active;
            pressurePreviewGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            pressurePreviewGrid.AlternatingRowsDefaultCellStyle.BackColor = ProjectCreationTheme.Workspace;
            pressurePreviewGrid.GridColor = ProjectCreationTheme.Border;
            pressurePreviewGrid.Columns.Add("Number", "序号");
            pressurePreviewGrid.Columns.Add("Multiplier", "加压倍数");
            pressurePreviewGrid.Columns.Add("Repetitions", "次数");
            pressurePreviewGrid.Columns.Add("Voltage", "计算电压 / kV");
            pressurePreviewGrid.Columns[0].FillWeight = 16;
            pressurePreviewGrid.Columns[1].FillWeight = 30;
            pressurePreviewGrid.Columns[2].FillWeight = 22;
            pressurePreviewGrid.Columns[3].FillWeight = 32;
            foreach (DataGridViewColumn column in pressurePreviewGrid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            pressurePreviewGrid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs args) { if (args.RowIndex >= 0) EditPressureList(); };
            pressurePlanActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(4, 2, 4, 0) };
            pressurePlanActions.Controls.AddRange(new Control[] { pressureEditButton, pressureSaveAsButton, pressureLoadButton });
            pressureSaveAsButton.Click += delegate { SaveVoltagePlanAs(); };
            pressureLoadButton.Click += delegate { LoadVoltagePlanFromFile(); };
            pressurePlanLayout.Controls.Add(fields, 0, 0);
            pressurePlanLayout.Controls.Add(pressurePlanSummary, 0, 1);
            pressurePlanLayout.Controls.Add(pressurePreviewGrid, 0, 2);
            pressurePlanLayout.Controls.Add(pressurePlanActions, 0, 3);
            return pressurePlanLayout;
        }

        Control BuildJointEditor()
        {
            TableLayoutPanel root = new TableLayoutPanel { Name = "JointEditorLayout", Margin = Padding.Empty, RowCount = 4, ColumnCount = 1, BackColor = ProjectCreationTheme.Surface };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 2 * Math.Max(72, ProjectCreationTheme.UiFont(9.5f).Height * 2 + 20)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Panel heading = new Panel { Dock = DockStyle.Fill, BackColor = ProjectCreationTheme.Surface };
            Label title = new Label { Text = "电缆接头编辑", Dock = DockStyle.Left, Width = 160, TextAlign = ContentAlignment.MiddleLeft, ForeColor = ProjectCreationTheme.SecondaryInk, Font = ProjectCreationTheme.UiFont(8.2f) };
            Label hint = new Label { Text = "接头列表", Dock = DockStyle.Right, Width = 95, TextAlign = ContentAlignment.MiddleRight, ForeColor = ProjectCreationTheme.SecondaryInk, Font = ProjectCreationTheme.UiFont(8.2f) };
            heading.Controls.Add(title); heading.Controls.Add(hint);
            selectedPhaseCombo = ChoiceCombo(); selectedPhaseCombo.Items.AddRange(ProjectModes.Phases);
            attachmentTypeCombo = ChoiceCombo(); bodyTypeCombo = ChoiceCombo(); beforeBodyTypeCombo = ChoiceCombo(); jointPositionNumber = Number(0, 1000000, 1, "0.0");
            beforeBodyTypeCombo.Name = "JointInsulationBefore"; bodyTypeCombo.Name = "JointInsulationAfter";
            TableLayoutPanel fields = CompactGrid(3);
            AddCompactPair(fields, 0, "相别", selectedPhaseCombo, "附件类型", attachmentTypeCombo);
            AddCompactPair(fields, 1, "接头前绝缘", beforeBodyTypeCombo, "接头后绝缘", bodyTypeCombo);
            Label positionCaption;
            fields.Controls.Add(VerticalField("位置 / m", jointPositionNumber, false, out positionCaption), 0, 2);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(0, 2, 0, 2), BackColor = ProjectCreationTheme.Surface };
            addJointButton = new ProjectCreationButton { Text = "添加接头", Width = 126, Height = 34, Primary = true, Margin = Padding.Empty };
            editJointButton = new ProjectCreationButton { Text = "编辑选中", Width = 104, Height = 32, Margin = new Padding(0, 0, 4, 0) };
            deleteJointButton = new ProjectCreationButton { Text = "删除选中", Width = 104, Height = 32, Margin = Padding.Empty, Danger = true };
            actions.Controls.Add(addJointButton); actions.Controls.Add(editJointButton); actions.Controls.Add(deleteJointButton);
            cableGrid = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = ProjectCreationTheme.Surface, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                ColumnHeadersHeight = 28, RowTemplate = { Height = 28 }
            };
            cableGrid.ColumnHeadersDefaultCellStyle.BackColor = ProjectCreationTheme.SurfaceAlt;
            cableGrid.ColumnHeadersDefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            cableGrid.GridColor = ProjectCreationTheme.Border;
            cableGrid.DefaultCellStyle.SelectionBackColor = ProjectCreationTheme.Active;
            cableGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            cableGrid.DefaultCellStyle.ForeColor = ProjectCreationTheme.Ink;
            cableGrid.Columns.Add("Phase", "相别"); cableGrid.Columns.Add("Node", "节点"); cableGrid.Columns.Add("Type", "附件类型");
            cableGrid.Columns.Add("Position", "位置 / m"); cableGrid.Columns.Add("Material", "接头后绝缘");
            cableGrid.Columns.Add("MaterialBefore", "接头前绝缘"); cableGrid.Columns["MaterialBefore"].DisplayIndex = 4;
            ConfigureJointGridColumns(cableGrid);
            cableGrid.FontChanged += delegate {
                // FontChanged precedes DataGridView's inherited header-style
                // update; measure the new font, not the previous header font.
                cableGrid.ColumnHeadersDefaultCellStyle.Font = cableGrid.Font;
                ConfigureJointGridColumns(cableGrid);
            };
            foreach (DataGridViewColumn column in cableGrid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            ContextMenuStrip jointMenu = new ContextMenuStrip();
            jointMenu.Items.Add("编辑选中", null, delegate { EditSelectedJoint(); });
            jointMenu.Items.Add("删除选中", null, delegate { DeleteSelectedJoint(); });
            cableGrid.ContextMenuStrip = jointMenu;
            cableGrid.CellMouseDown += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
                cableGrid.ClearSelection(); cableGrid.Rows[e.RowIndex].Selected = true;
            };
            root.Controls.Add(heading, 0, 0); root.Controls.Add(fields, 0, 1); root.Controls.Add(actions, 0, 2); root.Controls.Add(cableGrid, 0, 3);
            var viewport = new Panel { Name = "JointEditorViewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, BackColor = ProjectCreationTheme.Surface };
            viewport.Controls.Add(root);
            ConfigureJointEditorSpacing(root, heading, fields, actions, cableGrid, viewport);
            return viewport;
        }

        static void ConfigureJointEditorSpacing(TableLayoutPanel root, Panel heading, TableLayoutPanel fields, FlowLayoutPanel actions, DataGridView grid, Panel viewport)
        {
            bool fitting = false;
            Action fit = delegate
            {
                if (fitting || viewport.ClientSize.Width < 1) return;
                fitting = true; root.SuspendLayout(); fields.SuspendLayout();
                try
                {
                    int headingHeight = heading.Controls.Cast<Control>().Select(c => c.Font.Height + 6).DefaultIfEmpty(24).Max();
                    int[] rows = new int[fields.RowCount];
                    foreach (TableLayoutPanel field in fields.Controls.OfType<TableLayoutPanel>())
                    {
                        var caption = field.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
                        var frame = field.Controls.OfType<ProjectCreationInputFrame>().FirstOrDefault();
                        if (caption == null || frame == null || frame.Controls.Count != 1) continue;
                        int captionHeight = caption.Controls.Cast<Control>().Select(c => Math.Max(c.Font.Height, c.GetPreferredSize(Size.Empty).Height) + c.Margin.Vertical).DefaultIfEmpty(JointEditorFontHeight(root)).Max() + 3;
                        Control input = frame.Controls[0];
                        int inputHeight = input is ComboBox ? ((ComboBox)input).PreferredHeight : Math.Max(input.Font.Height + 6, input.PreferredSize.Height);
                        frame.Height = Math.Max(35, inputHeight + frame.Padding.Vertical + 2);
                        field.RowStyles[0].Height = captionHeight;
                        rows[fields.GetRow(field)] = Math.Max(rows[fields.GetRow(field)], captionHeight + frame.Height + frame.Margin.Vertical + field.Margin.Vertical);
                    }
                    int actionHeight = 0, actionRowHeight = 0, actionRowWidth = 0;
                    int actionWidth = Math.Max(1, viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - actions.Padding.Horizontal - root.Padding.Horizontal);
                    foreach (Control action in actions.Controls)
                    {
                        action.Height = Math.Max(34, action.Font.Height + 14);
                        action.Width = Math.Max(126, TextRenderer.MeasureText(action.Text, action.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 26);
                        int actionItemWidth = action.Width + action.Margin.Horizontal;
                        if (actionRowWidth > 0 && actionRowWidth + actionItemWidth > actionWidth)
                        { actionHeight += actionRowHeight; actionRowHeight = actionRowWidth = 0; }
                        actionRowWidth += actionItemWidth; actionRowHeight = Math.Max(actionRowHeight, action.Height + action.Margin.Vertical);
                    }
                    actionHeight += actionRowHeight;
                    // The two insulation selectors add a third field row. At
                    // the minimum host height, spend only spare pixels on the
                    // optional inter-row gap so all actions remain reachable
                    // without shrinking input glyphs or button hit targets.
                    int fieldGap = Math.Min(5, Math.Max(0, (viewport.ClientSize.Height - headingHeight -
                        rows.Sum() - actionHeight - actions.Padding.Vertical - 3) / Math.Max(1, rows.Length)));
                    for (int i = 0; i < rows.Length; i++)
                    {
                        rows[i] += fieldGap;
                        fields.RowStyles[i].SizeType = SizeType.Absolute;
                        fields.RowStyles[i].Height = rows[i];
                    }
                    root.RowStyles[0].Height = headingHeight;
                    root.RowStyles[1].Height = rows.Sum();
                    root.RowStyles[2].Height = actionHeight + actions.Padding.Vertical + 3;
                    // A short host gets a vertical scrollable editor instead of
                    // squeezing controls behind the following row's button.
                    // Keep the actual header and at least two complete rows
                    // usable even when the editor needs outer vertical scroll.
                    int gridRowsHeight = 0, gridRows = 0;
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        if (!row.Visible) continue;
                        gridRowsHeight += row.Height;
                        if (++gridRows == 2) break;
                    }
                    gridRowsHeight += (2 - gridRows) * Math.Max(1, grid.RowTemplate.Height);
                    int gridHeight = (grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0) + gridRowsHeight +
                        grid.Margin.Vertical + grid.Padding.Vertical + 2 + SystemInformation.HorizontalScrollBarHeight;
                    // The material-aware table may need horizontal scrolling.
                    // Reserve that strip before laying out two complete rows;
                    // otherwise the native scrollbar consumes the second row.
                    int requiredHeight = headingHeight + rows.Sum() + actionHeight + actions.Padding.Vertical + 3 + gridHeight;
                    int width = viewport.ClientSize.Width;
                    if (requiredHeight > viewport.ClientSize.Height && !viewport.VerticalScroll.Visible) width -= SystemInformation.VerticalScrollBarWidth;
                    viewport.AutoScrollMinSize = new Size(0, requiredHeight);
                    root.Bounds = new Rectangle(viewport.AutoScrollPosition.X, viewport.AutoScrollPosition.Y, Math.Max(1, width), Math.Max(requiredHeight, viewport.ClientSize.Height));
                }
                finally { fields.ResumeLayout(true); root.ResumeLayout(true); fitting = false; }
            };
            root.SizeChanged += delegate { fit(); }; root.VisibleChanged += delegate { fit(); }; root.FontChanged += delegate { fit(); };
            root.Layout += delegate { fit(); };
            viewport.SizeChanged += delegate { fit(); }; viewport.Layout += delegate { fit(); };
            grid.ColumnHeadersHeightChanged += delegate { fit(); };
            grid.RowHeightChanged += delegate { fit(); };
            grid.RowsAdded += delegate { fit(); }; grid.RowsRemoved += delegate { fit(); };
            foreach (Control input in EditableDescendants(fields)) input.FontChanged += delegate { fit(); };
            foreach (TableLayoutPanel field in fields.Controls.OfType<TableLayoutPanel>())
                foreach (FlowLayoutPanel caption in field.Controls.OfType<FlowLayoutPanel>())
                    foreach (Control label in caption.Controls) label.FontChanged += delegate { fit(); };
            foreach (Control action in actions.Controls) { action.FontChanged += delegate { fit(); }; action.TextChanged += delegate { fit(); }; }
            fit();
        }

        static int JointEditorFontHeight(Control control) { return Math.Max(16, control.Font.Height); }

        Control BuildActions()
        {
            actionBar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ProjectCreationTheme.Surface, Padding = new Padding(12, 7, 12, 7) };
            actionBar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen border = new Pen(ProjectCreationTheme.Border)) e.Graphics.DrawLine(border, 0, 0, actionBar.Width, 0);
            };
            FlowLayoutPanel right = projectActionButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 552, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            exitButton = new ProjectCreationButton { Text = "退出", Width = 126, Danger = true, Margin = new Padding(8, 0, 0, 0) };
            clearButton = new ProjectCreationButton { Text = "清空", Width = 98, Margin = new Padding(8, 0, 0, 0) };
            loadButton = new ProjectCreationButton { Text = "加载项目", Width = 98, Margin = new Padding(8, 0, 0, 0) };
            saveButton = new ProjectCreationButton { Text = "保存项目", Width = 142, Primary = true, Margin = new Padding(8, 0, 0, 0) };
            right.Controls.AddRange(new Control[] { exitButton, clearButton, loadButton, saveButton });
            foreach (Control button in right.Controls)
            {
                button.TextChanged += delegate { RefreshProjectActionLayout(); };
                button.FontChanged += delegate { RefreshProjectActionLayout(); };
            }
            actionBar.SizeChanged += delegate { RefreshProjectActionLayout(); };
            savedBadge = new Label { Text = "●  未保存", Tag = "dynamic", AutoSize = false, Size = new Size(180, 36), TextAlign = ContentAlignment.MiddleLeft, BackColor = ProjectCreationTheme.Surface, ForeColor = ProjectCreationTheme.Warning, Location = new Point(12, 7) };
            actionBar.Controls.Add(right); actionBar.Controls.Add(savedBadge);
            return actionBar;
        }

        bool refreshingProjectActionLayout;
        void RefreshProjectActionLayout()
        {
            if (refreshingProjectActionLayout) return;
            refreshingProjectActionLayout = true;
            try
            {
                LayoutProjectActions();
                if (projectActionButtons != null && actionBar != null)
                {
                    int available = Math.Max(180, actionBar.ClientSize.Width - actionBar.Padding.Horizontal - 150);
                    int total = projectActionButtons.Controls.Cast<Control>().Sum(c => c.Width + c.Margin.Horizontal);
                    int width = Math.Min(total, available), rows = 1, used = 0, lineHeight = 0;
                    foreach (Control action in projectActionButtons.Controls)
                    {
                        int needed = action.Width + action.Margin.Horizontal;
                        if (used > 0 && used + needed > width) { rows++; used = 0; }
                        used += needed;
                        lineHeight = Math.Max(lineHeight, action.Height + action.Margin.Vertical);
                    }
                    projectActionButtons.WrapContents = true;
                    projectActionButtons.Width = width;
                    projectActionButtons.Height = rows * lineHeight;
                    if (actionRowStyle != null) actionRowStyle.Height = rows * lineHeight + actionBar.Padding.Vertical + 2;
                    if (savedBadge != null) savedBadge.Width = Math.Max(100, actionBar.ClientSize.Width - actionBar.Padding.Horizontal - width - 12);
                }
                // RowStyle changes alone can remain stale when they are made inside
                // a child SizeChanged event. Apply the new measured footer height now.
                if (rootLayout != null) rootLayout.PerformLayout();
                if (actionBar != null) actionBar.PerformLayout();
            }
            finally { refreshingProjectActionLayout = false; }
        }

        Control BuildStatus()
        {
            statusLabel = new Label { Dock = DockStyle.Fill, BackColor = ProjectCreationTheme.SurfaceAlt, ForeColor = ProjectCreationTheme.SecondaryInk, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 8, 0), Font = ProjectCreationTheme.UiFont(8.2f) };
            return statusLabel;
        }

        static TableLayoutPanel FieldGrid(int rows)
        {
            TableLayoutPanel grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = rows, BackColor = ProjectCreationTheme.Surface };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            return grid;
        }

        static TableLayoutPanel CompactGrid(int rows)
        {
            TableLayoutPanel grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows,
                BackColor = ProjectCreationTheme.Surface,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            return grid;
        }

        static void AddCompactPair(TableLayoutPanel grid, int row, string leftLabel, Control left, string rightLabel, Control right)
        {
            Label unused;
            grid.Controls.Add(VerticalField(leftLabel, left, false, out unused), 0, row);
            grid.Controls.Add(VerticalField(rightLabel, right, false, out unused), 1, row);
        }

        static void AddPair(TableLayoutPanel grid, int row, string leftLabel, Control left, string rightLabel, Control right)
        {
            grid.Controls.Add(FieldLabel(leftLabel), 0, row); grid.Controls.Add(left, 1, row);
            grid.Controls.Add(FieldLabel(rightLabel), 2, row); grid.Controls.Add(right, 3, row);
        }

        static void AddSpan(TableLayoutPanel grid, int row, string label, Control control)
        {
            grid.Controls.Add(FieldLabel(label), 0, row); grid.Controls.Add(control, 1, row); grid.SetColumnSpan(control, 3);
        }

        static void AddInline(TableLayoutPanel grid, int column, string label, Control control)
        {
            grid.Controls.Add(FieldLabel(label), column, 0); grid.Controls.Add(control, column + 1, 0);
        }

        static Label FieldLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = ProjectCreationTheme.SecondaryInk, Margin = new Padding(0, 3, 4, 3), AutoEllipsis = true, UseMnemonic = false };
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = ProjectCreationTheme.SecondaryInk, Font = ProjectCreationTheme.UiFont(8f), Margin = new Padding(4, 1, 8, 1), AutoEllipsis = true, UseMnemonic = false };
        }

        static Label ValueLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = ProjectCreationTheme.SurfaceAlt, ForeColor = ProjectCreationTheme.Ink, BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(5, 0, 0, 0), Margin = new Padding(4, 3, 8, 3) };
        }

        static TextBox FieldText(string accessibleName)
        {
            return new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AccessibleName = accessibleName, Margin = new Padding(4, 3, 8, 3), ForeColor = ProjectCreationTheme.Ink, BackColor = ProjectCreationTheme.Surface, Font = ProjectCreationTheme.UiFont(8.8f) };
        }

        static ComboBox ChoiceCombo()
        {
            return new ProjectCreationChoiceBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(4, 3, 8, 3), ForeColor = ProjectCreationTheme.Ink, BackColor = ProjectCreationTheme.Surface, Font = ProjectCreationTheme.UiFont(8.8f) };
        }

        static ComboBox HistoryCombo(string accessibleName)
        {
            return new ProjectCreationChoiceBox
            {
                Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
                AccessibleName = accessibleName, Margin = new Padding(4, 3, 8, 3)
            };
        }

        static NumericUpDown Number(decimal minimum, decimal maximum, int decimals, string format)
        {
            return new ProjectCreationNumberBox { Dock = DockStyle.Fill, Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals, ThousandsSeparator = true, Margin = new Padding(4, 3, 8, 3), TextAlign = HorizontalAlignment.Right, ForeColor = ProjectCreationTheme.Ink, BackColor = ProjectCreationTheme.Surface, Font = ProjectCreationTheme.UiFont(8.8f) };
        }
    }
}
