using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        PressureWaveformView waveformView;
        PressureSpectrumDashboardView spectrumDashboardView;
        Label maxPdValueLabel, capacitanceValueLabel, frequencyValueLabel, tanDeltaValueLabel;
        Label customModeHintLabel, pressureActionHintLabel, highVoltageLabel;
        PressureTestButton manualSaveButton;
        TableLayoutPanel pressureMessageLayout;
        PressureTestButton pressureMessageDetailsButton;
        string pressureActionFullMessage = String.Empty;
        [System.ComponentModel.Browsable(false)]
        public string PressureActionFullMessage { get { return pressureActionFullMessage; } }
        int pressureMessageRowHeight;
        bool pressureMessageInline;
        bool arrangingPressureMessage;
        Label taskInformationLabel;
        Label connectionStatusLabel;
        TableLayoutPanel rootLayout;
        Control moduleHeader;
        PressureTestButton customVoltageValueButton;
        PressureTestButton customVoltageMinusButton,customVoltagePlusButton;
        TextBox customVoltageInlineEditor;
        string customVoltageEditorOriginalText = String.Empty;
        RadioButton positiveRadio, negativeRadio;
        CheckBox automaticPressureCheck, useCompensationCapacitanceCheck;
        PressureTestSegmentButton planModeButton, customModeButton, normalModeButton, advancedModeButton;
        PressureTestSegmentButton automaticRangeButton, manualRangeButton;
        PressureTestSegmentButton u0VoltageModeButton, rmsVoltageModeButton, peakVoltageModeButton;
        PressureTestSegmentButton oneCycleButton, twoCycleButton, maximumWindowButton;
        PressureActionButton pressureActionButton;
        PressureTestButton deleteButton;
        Panel pressurePlanHost, customVoltagePanel, manualRangePanel;
        DataGridView pressurePlanGrid;
        PressureTestButton rangeSelectorButton;
        TableLayoutPanel actionLayout;
        ContextMenuStrip rangeMenu;
        readonly Dictionary<string, PressureTestSegmentButton> phaseButtons = new Dictionary<string, PressureTestSegmentButton>(StringComparer.OrdinalIgnoreCase);
        Action arrangePressureBody, arrangeRealtimeSidebar, arrangePressureSettings, arrangePressureActions, arrangeDisplaySettings;
        Action arrangeProgramCard, arrangeCustomVoltage, arrangeRangeCard, arrangeManualRange;
        bool condensedPressureSettings, groupedPressureSettings, compactVoltageSummaryInAction, compactHorizontalVoltageStepper;
        bool showPressureModePage;
        PressureTestSegmentButton compactPlanButton, compactCustomButton, compactModeButton, compactRangeButton;

        void InitializeLayout()
        {
            SuspendLayout();
            BackColor = PressureTestTheme.Workspace;
            ForeColor = PressureTestTheme.Ink;
            Font = PressureTestTheme.Font(9.5f);
            DoubleBuffered = true;
            TableLayoutPanel root = Table(1, 4);
            rootLayout = root;
            root.Name = "RootLayout";
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            moduleHeader = BuildHeader();
            moduleHeader.Name = "PressureModuleHeader";
            root.Controls.Add(moduleHeader, 0, 0);
            root.Controls.Add(BuildBody(), 0, 1);
            root.Controls.Add(BuildTaskInformationBar(), 0, 3);
            root.SizeChanged += delegate
            {
                // 1280 x 720 is the complete LabVIEW window.  After reserving
                // 120 px for LabVIEW chrome, the control receives 1280 x 600.
                UpdateEmbeddedLayout();
            };
            Controls.Add(root);
            CapturePressureBuiltInText(this);
            FontChanged += delegate { RefreshPressureSidebarLayout(); };
            WireCustomVoltageCommitOnExternalClick(this);
            ResumeLayout(true);
        }

        void UpdateEmbeddedLayout()
        {
            if (rootLayout == null || moduleHeader == null) return;
            bool compact = rootLayout.ClientSize.Height <= 650;
            rootLayout.SuspendLayout();
            moduleHeader.Visible = !EmbeddedMode;
            rootLayout.RowStyles[0].Height = EmbeddedMode ? 0f : rootLayout.ClientSize.Height < 520 ? 42f : compact ? 54f : 62f;
            rootLayout.RowStyles[2].Height = pressureStartConfirmation == null ? 0f : Math.Max(110f, Font.Height * 6f + 20f);
            rootLayout.RowStyles[3].Height = Math.Max(compact?22f:26f,taskInformationLabel==null?26:taskInformationLabel.Font.Height+8);
            rootLayout.ResumeLayout(true);
        }

        Control BuildHeader()
        {
            Panel header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(15, 0, 15, 0), BackColor = PressureTestTheme.Navy };
            TableLayoutPanel layout = Table(4, 1);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18f));
            layout.Controls.Add(Label("OWTS  加压测试", 12f, FontStyle.Bold, Color.White, ContentAlignment.MiddleLeft), 0, 0);
            Label connected = Label("●  设备已连接", 9.5f, FontStyle.Bold, Color.White, ContentAlignment.MiddleCenter);
            connectionStatusLabel = connected;
            connected.Anchor = AnchorStyles.Left;
            connected.Size = new Size(120, 36);
            connected.BackColor = PressureTestTheme.Success;
            layout.Controls.Add(connected, 1, 0);
            Label metadata = Label("2026-06-13 21:36   ·   测试-测试   ·   电缆长度 175.4 m", 9.5f, FontStyle.Regular, Color.FromArgb(232, 242, 249), ContentAlignment.MiddleCenter);
            metadata.AutoEllipsis = true;
            layout.Controls.Add(metadata, 2, 0);
            header.Controls.Add(layout);
            return header;
        }

        Control BuildBody()
        {
            TableLayoutPanel body = Table(5, 1);
            body.Name = "BodyLayout";
            body.Margin = Padding.Empty;
            body.Padding = new Padding(8, 8, 8, 4);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280f));
            body.Controls.Add(BuildRealtimePanel(), 0, 0);
            body.Controls.Add(Divider("LeftPaneDivider", true), 1, 0);
            body.Controls.Add(BuildCenterPanel(), 2, 0);
            body.Controls.Add(Divider("RightPaneDivider", true), 3, 0);
            body.Controls.Add(BuildPressurePanel(), 4, 0);
            bool arrangingBody = false;
            arrangePressureBody = delegate
            {
                if (arrangingBody || body.ClientSize.Width <= 0) return;
                arrangingBody = true;
                try
                {
                    // A wider monitor belongs to the waveform, not wider empty
                    // sidebars. Point-font pixel height also follows Windows DPI.
                    float scale = Math.Max(1f, Font.Height / 17f);
                    float leftWidth = Math.Max(180f * scale,
                        SidebarSingleLineWidth(useCompensationCapacitanceCheck) + 40);
                    // Preferred width already measures every visible caption at
                    // the current DPI. A second scaled fixed floor prevented
                    // the compact editor from narrowing on 150% displays.
                    float rightWidth = Math.Max(215f, PreferredPressureSidebarWidth());
                    leftWidth = Math.Min(body.ClientSize.Width * .24f, leftWidth);
                    rightWidth = Math.Min(body.ClientSize.Width * .33f, rightWidth);
                    body.SuspendLayout();
                    if (Math.Abs(body.ColumnStyles[0].Width-leftWidth) > .1f) body.ColumnStyles[0].Width=leftWidth;
                    if (Math.Abs(body.ColumnStyles[4].Width-rightWidth) > .1f) body.ColumnStyles[4].Width=rightWidth;
                    body.Padding = body.ClientSize.Height < 520
                        ? new Padding(8, 6, 8, 3)
                        : new Padding(8, 8, 8, 4);
                    body.ResumeLayout(true);
                }
                finally { arrangingBody = false; }
            };
            body.SizeChanged += delegate { arrangePressureBody(); };
            return body;
        }

        Control BuildRealtimePanel()
        {
            PressureTestCard card = Card(new Padding(6, 2, 6, 2), Padding.Empty);
            card.Name = "RealtimeSidebar";
            Panel viewport = new Panel { Name = "RealtimeSidebarScroll", Dock = DockStyle.Fill,
                Margin = Padding.Empty, AutoScroll = false, BackColor = PressureTestTheme.Surface };
            TableLayoutPanel layout = Table(1, 7);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            var metrics = new[] { BuildMetricCard("MetricPd", "∿", "局放最大值 / pC", out maxPdValueLabel),
                BuildMetricCard("MetricCapacitance", "╫", "等效电容 / μF", out capacitanceValueLabel),
                BuildMetricCard("MetricFrequency", "∿", "振荡频率 / Hz", out frequencyValueLabel),
                BuildMetricCard("MetricTanDelta", "%", "介损 / %", out tanDeltaValueLabel) };
            for(int i=0;i<metrics.Length;i++)layout.Controls.Add(metrics[i],0,i+1);
            var divider=Divider("LeftMetricsDivider",false);layout.Controls.Add(divider,0,5);
            Control display=BuildPolarityPanel();layout.Controls.Add(display,0,6);
            var tabs=Segments(3);tabs.Name="RealtimeGroupTabs";bool showDisplay=false,arranging=false;int metricPage=0;
            PressureTestSegmentButton metricsTab=null,displayTab=null,moreMetricsTab=null;
            Action fitSidebar = delegate
            {
                if(arranging)return;arranging=true;
                try {
                    int[] metricHeights=metrics.Select(MetricPreferredHeight).ToArray();
                    int displayHeight=DisplaySettingsHeight();
                    bool grouped=viewport.ClientSize.Height<metricHeights.Sum()+displayHeight+6;
                    // At very short heights even the metrics alone may not fit.
                    // Keep readable fonts and provide a second metrics page,
                    // rather than clipping the final value or adding a scrollbar.
                    bool splitMetrics=grouped&&viewport.ClientSize.Height<metricHeights.Sum()+SidebarButtonHeight(metricsTab,displayTab);
                    moreMetricsTab.Visible=splitMetrics;
                    tabs.ColumnStyles[0].Width=splitMetrics?33.33f:50f;
                    tabs.ColumnStyles[1].Width=splitMetrics?33.33f:0f;
                    tabs.ColumnStyles[2].Width=splitMetrics?33.34f:50f;
                    int tabHeight=grouped?SidebarButtonHeight(metricsTab,displayTab,splitMetrics?moreMetricsTab:null):0;
                    int shownHeight=metrics.Select((m,i)=>!splitMetrics||i/2==metricPage?metricHeights[i]:0).Sum();
                    layout.SuspendLayout();tabs.Visible=grouped;layout.RowStyles[0].Height=tabHeight;
                    for(int i=0;i<metrics.Length;i++){
                        bool shown=(!grouped||!showDisplay)&&(!splitMetrics||i/2==metricPage);
                        metrics[i].Visible=shown;
                        int spare=Math.Max(0,viewport.ClientSize.Height-tabHeight-(grouped?0:displayHeight+6)-shownHeight);
                        layout.RowStyles[i+1].Height=shown?metricHeights[i]+Math.Min(14,spare/4):0;
                    }
                    divider.Visible=!grouped;layout.RowStyles[5].Height=grouped?0:6;
                    display.Visible=!grouped||showDisplay;layout.RowStyles[6].SizeType=SizeType.Absolute;
                    layout.RowStyles[6].Height=display.Visible?displayHeight:0;
                    metricsTab.Selected=!showDisplay&&(!splitMetrics||metricPage==0);displayTab.Selected=showDisplay;
                    moreMetricsTab.Selected=!showDisplay&&splitMetrics&&metricPage==1;
                    layout.ResumeLayout(true);
                }finally{arranging=false;}
            };
            metricsTab=NavigationSegment("实时指标",()=>{showDisplay=false;metricPage=0;fitSidebar();});metricsTab.Name="RealtimeMetricsTab";
            moreMetricsTab=NavigationSegment("更多指标",()=>{showDisplay=false;metricPage=1;fitSidebar();});moreMetricsTab.Name="RealtimeMoreMetricsTab";
            displayTab=NavigationSegment("显示设置",()=>{showDisplay=true;fitSidebar();});displayTab.Name="RealtimeDisplayTab";
            tabs.Controls.Add(metricsTab,0,0);tabs.Controls.Add(moreMetricsTab,1,0);tabs.Controls.Add(displayTab,2,0);layout.Controls.Add(tabs,0,0);
            arrangeRealtimeSidebar=fitSidebar;
            viewport.SizeChanged += delegate { fitSidebar(); };
            viewport.FontChanged += delegate { fitSidebar(); };
            viewport.Controls.Add(layout);
            card.Controls.Add(viewport);
            fitSidebar();
            return card;
        }

        Control BuildMetricCard(string name, string iconText, string caption, out Label valueLabel)
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2, 3, 2, 3), Padding = new Padding(8, 6, 5, 5), BackColor = PressureTestTheme.SurfaceAlt };
            panel.Name = name;
            Label icon = Label(iconText, 18f, FontStyle.Bold, Color.FromArgb(109, 137, 165), ContentAlignment.MiddleCenter);
            icon.Dock = DockStyle.Left;
            icon.Width = 28;
            TableLayoutPanel text = Table(1, name == "MetricCapacitance" ? 3 : 2);
            text.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 43f));
            text.RowStyles.Add(new RowStyle(SizeType.Percent, 57f));
            Label captionLabel = Label(caption, 9.5f, FontStyle.Regular, PressureTestTheme.Ink, ContentAlignment.BottomLeft);
            captionLabel.Name = name + "Caption";
            captionLabel.AutoEllipsis = false;
            valueLabel = Label("0.0", 18f, FontStyle.Bold, PressureTestTheme.Accent, ContentAlignment.TopLeft);
            text.Controls.Add(captionLabel, 0, 0);
            text.Controls.Add(valueLabel, 0, 1);
            if(name == "MetricCapacitance")
            {
                useCompensationCapacitanceCheck = new CheckBox { Name="UseCompensationCapacitance", Text="使用补偿电容",
                    Dock=DockStyle.Fill, Margin=Padding.Empty, Font=PressureTestTheme.Font(9.5f), ForeColor=PressureTestTheme.Ink };
                useCompensationCapacitanceCheck.CheckedChanged += delegate {
                    if(!suppressUiEvents)SetUseCompensationCapacitance(useCompensationCapacitanceCheck.Checked);
                };
                text.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
                text.Controls.Add(useCompensationCapacitanceCheck,0,2);
            }
            Action fitMetric = delegate
            {
                // Decorative icons must not take the space needed by units and
                // metric captions in a narrow customer-facing sidebar.
                icon.Visible = panel.Height >= 95 && panel.ClientSize.Width >= 285;
                panel.Margin = new Padding(2,1,2,1);
                panel.Padding = new Padding(4,2,3,2);
                text.RowStyles[0].SizeType=SizeType.Absolute;
                text.RowStyles[0].Height=SidebarTextHeight(captionLabel,Math.Max(20,text.ClientSize.Width));
                text.RowStyles[1].SizeType=SizeType.Percent;text.RowStyles[1].Height=100;
                if(name=="MetricCapacitance")text.RowStyles[2].Height=SidebarTextHeight(useCompensationCapacitanceCheck,Math.Max(20,text.ClientSize.Width-20))+6;
            };
            panel.SizeChanged += delegate { fitMetric(); };
            captionLabel.FontChanged += delegate { fitMetric(); };
            captionLabel.TextChanged += delegate { fitMetric(); };
            text.SizeChanged += delegate { fitMetric(); };
            panel.Controls.Add(text);
            panel.Controls.Add(icon);
            return panel;
        }

        Control BuildPolarityPanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(2, 6, 2, 4), Padding = new Padding(5, 4, 5, 3), BackColor = PressureTestTheme.SurfaceAlt };
            TableLayoutPanel layout = Table(1, 5);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Label cycleCaption=Caption("显示周期");
            layout.Controls.Add(cycleCaption, 0, 0);
            TableLayoutPanel cycleModes = Segments(3);
            oneCycleButton = Segment("1", delegate { SetWaveformWindow("ONE"); }, "WAVEFORM_WINDOW");
            twoCycleButton = Segment("2", delegate { SetWaveformWindow("TWO"); }, "WAVEFORM_WINDOW");
            maximumWindowButton = Segment("∞", delegate { SetWaveformWindow("MAXIMUM"); }, "WAVEFORM_WINDOW");
            maximumWindowButton.AccessibleName = "最长时间";
            oneCycleButton.Name = "OneCycleButton";
            twoCycleButton.Name = "TwoCycleButton";
            maximumWindowButton.Name = "MaximumWindowButton";
            oneCycleButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            twoCycleButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            maximumWindowButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            cycleModes.Controls.Add(oneCycleButton, 0, 0);
            cycleModes.Controls.Add(twoCycleButton, 1, 0);
            cycleModes.Controls.Add(maximumWindowButton, 2, 0);
            arrangeDisplaySettings = delegate
            {
                bool wrap = false; // 1 / 2 / infinity are intentionally one compact row.
                cycleModes.SuspendLayout();
                cycleModes.RowCount = wrap ? 2 : 1;
                while(cycleModes.RowStyles.Count < 2) cycleModes.RowStyles.Add(new RowStyle(SizeType.Percent,50));
                cycleModes.RowStyles[0].Height = wrap ? 50 : 100; cycleModes.RowStyles[1].Height = wrap ? 50 : 0;
                cycleModes.ColumnStyles[0].Width = wrap ? 50 : 33.33f; cycleModes.ColumnStyles[1].Width = wrap ? 50 : 33.33f; cycleModes.ColumnStyles[2].Width = wrap ? 0 : 33.34f;
                cycleModes.SetColumn(maximumWindowButton,wrap ? 0 : 2); cycleModes.SetRow(maximumWindowButton,wrap ? 1 : 0); cycleModes.SetColumnSpan(maximumWindowButton,wrap ? 2 : 1);
                layout.RowStyles[0].Height = SidebarTextHeight(cycleCaption,layout.ClientSize.Width);
                layout.RowStyles[1].Height = SidebarButtonHeight(oneCycleButton,twoCycleButton,maximumWindowButton);
                layout.RowStyles[2].Height = vlfPolarityAvailable?SidebarTextHeight(polarityCaption,layout.ClientSize.Width):0;
                layout.RowStyles[3].Height = vlfPolarityAvailable?SidebarButtonHeight(positiveRadio,negativeRadio):0;
                cycleModes.ResumeLayout(true);
            };
            cycleModes.SizeChanged += delegate { arrangeDisplaySettings(); };
            layout.FontChanged += delegate { arrangeDisplaySettings(); };
            layout.Controls.Add(cycleModes, 0, 1);
            polarityCaption = Caption("超低频加压极性");
            polarityCaption.Visible = vlfPolarityAvailable;
            layout.Controls.Add(polarityCaption, 0, 2);
            Panel polarity = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            positiveRadio = Radio("正极性", new Point(2, 0));
            negativeRadio = Radio("负极性", new Point(77, 0));
            positiveRadio.CheckedChanged += delegate { if (!suppressUiEvents && positiveRadio.Checked) ExecuteParameterAction("POLARITY", delegate { SetPolarity(true); }); };
            negativeRadio.CheckedChanged += delegate { if (!suppressUiEvents && negativeRadio.Checked) ExecuteParameterAction("POLARITY", delegate { SetPolarity(false); }); };
            polarity.Controls.Add(positiveRadio);
            polarity.Controls.Add(negativeRadio);
            polarity.SizeChanged += delegate
            {
                int half = polarity.ClientSize.Width / 2;
                positiveRadio.SetBounds(0, 0, half, polarity.ClientSize.Height);
                negativeRadio.SetBounds(half, 0, polarity.ClientSize.Width - half, polarity.ClientSize.Height);
            };
            layout.Controls.Add(polarity, 0, 3);
            polarityContainer = polarity;
            polarity.Visible = vlfPolarityAvailable;
            panel.Controls.Add(layout);
            return panel;
        }

        Control BuildCenterPanel()
        {
            TableLayoutPanel center = Table(1, 4);
            center.Name = "CenterChartsHost";
            center.Margin = Padding.Empty;
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            Control waveform = BuildWaveformCard();
            Control spectrum = BuildSpectrumAnalysisCard();
            Control divider = Divider("CenterChartsDivider", false);
            TableLayoutPanel tabs = Segments(2);
            tabs.Name = "CompactChartTabs";
            bool showSpectrum = false;
            PressureTestSegmentButton waveformTab = null, spectrumTab = null;
            Action arrange = delegate
            {
                bool compact = ClientSize.Height < 580;
                center.SuspendLayout();
                tabs.Visible = compact;
                center.RowStyles[0].Height = compact ? 32f : 0f;
                center.RowStyles[1].SizeType = compact && showSpectrum ? SizeType.Absolute : SizeType.Percent;
                center.RowStyles[1].Height = compact ? (showSpectrum ? 0f : 100f) : 40f;
                center.RowStyles[2].Height = compact ? 0f : 8f;
                center.RowStyles[3].SizeType = compact && !showSpectrum ? SizeType.Absolute : SizeType.Percent;
                center.RowStyles[3].Height = compact ? (showSpectrum ? 100f : 0f) : 60f;
                waveform.Visible = !compact || !showSpectrum;
                spectrum.Visible = !compact || showSpectrum;
                divider.Visible = !compact;
                waveformTab.Selected = !showSpectrum;
                spectrumTab.Selected = showSpectrum;
                center.ResumeLayout(true);
            };
            waveformTab = Segment("局放与电压波形", delegate { showSpectrum = false; arrange(); }, "DISPLAY_VIEW");
            waveformTab.Name = "CompactWaveformTab";
            spectrumTab = Segment("谱图分析", delegate { showSpectrum = true; arrange(); }, "DISPLAY_VIEW");
            spectrumTab.Name = "CompactSpectrumTab";
            tabs.Controls.Add(waveformTab, 0, 0);
            tabs.Controls.Add(spectrumTab, 1, 0);
            center.Controls.Add(tabs, 0, 0);
            center.Controls.Add(waveform, 0, 1);
            center.Controls.Add(divider, 0, 2);
            center.Controls.Add(spectrum, 0, 3);
            center.SizeChanged += delegate { arrange(); };
            arrange();
            return center;
        }

        Control BuildWaveformCard()
        {
            PressureTestCard card = Card(new Padding(8, 5, 8, 8), Padding.Empty);
            card.Name = "WaveformCard";
            TableLayoutPanel layout = Table(1, 2);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Panel heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(Section("∿", "局放与电压波形"));
            heading.BackColor = PressureTestTheme.SectionBar;
            waveformView = new PressureWaveformView { Name = "WaveformView", Dock = DockStyle.Fill, Margin = Padding.Empty };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(waveformView, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        Control BuildSpectrumAnalysisCard()
        {
            PressureTestCard card = Card(new Padding(8, 5, 8, 8), Padding.Empty);
            card.Name = "SpectrumAnalysisCard";
            TableLayoutPanel layout = Table(1, 2);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Panel heading = new Panel { Dock = DockStyle.Fill };
            heading.BackColor = PressureTestTheme.SectionBar;
            heading.Controls.Add(Section("▦", "谱图分析"));
            spectrumDashboardView = new PressureSpectrumDashboardView
            {
                Name = "SpectrumDashboardView",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            spectrumDashboardView.UserAction += HandleSpectrumUserAction;
            spectrumDashboardView.EndpointReviewRequested += delegate { BeginEndpointReview(); };
            spectrumDashboardView.DisplayRecommendationChanged += delegate { Raise(DisplayRecommendationChanged); };
            spectrumDashboardView.Review.PdMapSelected += delegate {
                lock (stateLock) {
                    selectedSpectrumSourceRowIndex = spectrumDashboardView.Review.SelectedSourceRowIndex;
                    spectrumPointSelectionSequence++;
                }
            };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(spectrumDashboardView, 0, 1);
            card.Controls.Add(layout);
            return card;
        }

        Control BuildPressurePanel()
        {
            TableLayoutPanel right = Table(1, 6);
            right.Name = "PressureSidebar";
            right.Margin = Padding.Empty;
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 8f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 160f));
            arrangePressureActions = delegate
            {
                right.RowStyles[1].Height = 0f;
                right.RowStyles[2].Height = 4f;
                right.RowStyles[3].Height = SidebarButtonHeight(phaseButtons.Values.Cast<Control>().ToArray())+10;
                right.RowStyles[4].Height = 4f;
                right.RowStyles[5].Height = ActionCardHeight();
            };
            right.SizeChanged += delegate { arrangePressureActions(); };
            right.FontChanged += delegate { arrangePressureActions(); };
            right.Controls.Add(BuildPressureSettingsHost(), 0, 0);
            right.Controls.Add(Divider("RightPhaseDivider", false), 0, 2);
            right.Controls.Add(BuildPhaseSelector(), 0, 3);
            right.Controls.Add(Divider("RightActionDivider", false), 0, 4);
            right.Controls.Add(BuildActionCard(), 0, 5);
            return right;
        }

        Control BuildPressureSettingsHost()
        {
            Panel viewport = new Panel { Name = "PressureSettingsScroll", Dock = DockStyle.Fill,
                Margin = Padding.Empty, AutoScroll = false, BackColor = PressureTestTheme.Surface };
            TableLayoutPanel settings = Table(1, 4);
            settings.Dock = DockStyle.Top;
            settings.Name = "PressureSettingsHost";
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
            settings.RowStyles.Add(new RowStyle(SizeType.Percent,100f));
            Control program = BuildProgramCard();
            Control range = BuildModeAndRangeCard();
            TableLayoutPanel tabs = Segments(4);
            tabs.Name = "CompactSettingsTabs";
            bool showRange = false;
            bool arranging = false;
            PressureTestSegmentButton programTab = null, rangeTab = null, modeTab = null;
            Action arrange = delegate
            {
                if (arranging) return;
                arranging = true;
                try
                {
                condensedPressureSettings=false;
                groupedPressureSettings=false;
                compactVoltageSummaryInAction=false;
                compactHorizontalVoltageStepper=false;
                int rangeHeight = RangeSettingsHeight(),programHeight=ProgramSettingsHeight();
                bool compact = viewport.ClientSize.Height < programHeight+rangeHeight;
                // In PLAN the grid, not a trailing empty row, owns the spare
                // height. Prefer retaining both mode/range rows; remove their
                // redundant captions first when DPI leaves less room.
                bool stretchingPlan = PressureProgramModeCode == "PLAN";
                if (stretchingPlan && compact)
                {
                    condensedPressureSettings=true;
                    compact=viewport.ClientSize.Height<ProgramSettingsHeight()+RangeSettingsHeight();
                }
                groupedPressureSettings=compact;
                var tabControls=new Control[]{programTab,compactPlanButton,modeTab,rangeTab};
                var tabWidths=tabControls.Select(item=>SidebarSingleLineWidth(item)+18).ToArray();
                int tabWidth=tabWidths.Sum();
                for(int i=0;i<tabWidths.Length;i++)tabs.ColumnStyles[i].Width=100f*tabWidths[i]/tabWidth;
                // Equal quarters wrap the longer Custom label at large fonts,
                // needlessly adding a full line to all four navigation tabs.
                int tabHeight=compact?(viewport.ClientSize.Width>=tabWidth
                    ?Math.Max(32,tabControls.Where(item=>item!=null).Select(item=>
                        TextRenderer.MeasureText(item.Text,item.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Height+12).DefaultIfEmpty(32).Max())
                    :SidebarButtonHeight(tabControls)):0;
                programHeight=ProgramSettingsHeight();
                if(compact)condensedPressureSettings=viewport.ClientSize.Height<tabHeight+Math.Max(programHeight,rangeHeight);
                // At very short viewports keep the actual editor, units, target
                // action and emergency status visible. The duplicate conversion
                // summary is already shown by the main action's target peak.
                compactVoltageSummaryInAction=compact&&!stretchingPlan&&
                    viewport.ClientSize.Height<tabHeight+ProgramSettingsHeight();
                // When even the condensed page cannot fit vertically, place
                // both step keys beside the value. Keep the normal tall value
                // font and key hit areas; the safety/action area is unchanged.
                compactHorizontalVoltageStepper=compactVoltageSummaryInAction&&
                    viewport.ClientSize.Height<tabHeight+ProgramSettingsHeight();
                arrangeProgramCard?.Invoke();arrangeCustomVoltage?.Invoke();arrangeRangeCard?.Invoke();arrangeManualRange?.Invoke();
                rangeHeight=RangeSettingsHeight();programHeight=ProgramSettingsHeight();
                settings.SuspendLayout();
                tabs.Visible = compact;
                settings.RowStyles[0].Height = tabHeight;
                settings.RowStyles[1].Height = compact && showRange ? 0f : stretchingPlan
                    ? Math.Max(programHeight,viewport.ClientSize.Height-tabHeight-(compact?0:rangeHeight)) : programHeight;
                settings.RowStyles[2].Height = compact && !showRange ? 0f : rangeHeight;
                program.Visible = !compact || !showRange;
                range.Visible = !compact || showRange;
                int requiredHeight=tabHeight+(compact?(showRange?rangeHeight:programHeight):programHeight+rangeHeight);
                settings.Height=Math.Max(viewport.ClientSize.Height,requiredHeight);
                programTab.Selected = !showRange&&PressureProgramModeCode!="PLAN";
                compactPlanButton.Selected=!showRange&&PressureProgramModeCode=="PLAN";
                compactPlanButton.Enabled=planVoltagesKv.Length>0;
                rangeTab.Selected = showRange&&!showPressureModePage;
                modeTab.Selected=showRange&&showPressureModePage;
                settings.ResumeLayout(true);
                }
                finally { arranging = false; }
            };
            programTab = Segment("自定义", delegate { SetPressureProgramMode("CUSTOM");showRange = false; arrange(); },"PROGRAM_MODE");
            programTab.Name = "CompactProgramTab";
            compactCustomButton=programTab;
            compactPlanButton=Segment("计划",delegate { SetPressureProgramMode("PLAN");showRange=false;arrange(); },"PROGRAM_MODE");
            compactPlanButton.Name="CompactPlanTab";
            modeTab=NavigationSegment("模式",delegate {showPressureModePage=true;showRange=true;arrange();});modeTab.Name="CompactModeTab";compactModeButton=modeTab;
            rangeTab = NavigationSegment("量程", delegate { showPressureModePage=false;showRange = true; arrange(); });
            rangeTab.Name = "CompactRangeTab";
            compactRangeButton=rangeTab;
            tabs.Controls.Add(programTab, 0, 0);
            tabs.Controls.Add(compactPlanButton,1,0);
            tabs.Controls.Add(modeTab,2,0);
            tabs.Controls.Add(rangeTab, 3, 0);
            settings.Controls.Add(tabs, 0, 0);
            settings.Controls.Add(program, 0, 1);
            settings.Controls.Add(range, 0, 2);
            settings.SizeChanged += delegate { arrange(); };
            viewport.SizeChanged += delegate { arrange(); };
            viewport.FontChanged += delegate { arrange(); };
            arrangePressureSettings=arrange;
            viewport.Controls.Add(settings);
            arrange();
            return viewport;
        }

        Control BuildProgramCard()
        {
            PressureTestCard card = Card(new Padding(7, 4, 7, 5), new Padding(0, 0, 0, 4));
            TableLayoutPanel layout = Table(1, 2);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            TableLayoutPanel mode = Segments(2);
            planModeButton = Segment("计划加压", delegate { SetPressureProgramMode("PLAN"); }, "PROGRAM_MODE");
            customModeButton = Segment("自定义加压", delegate { SetPressureProgramMode("CUSTOM"); }, "PROGRAM_MODE");
            mode.Controls.Add(planModeButton, 0, 0);
            mode.Controls.Add(customModeButton, 1, 0);
            layout.Controls.Add(mode, 0, 0);
            pressurePlanHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 0, 0) };
            pressurePlanGrid = BuildPlanGrid();
            customVoltagePanel = BuildCustomVoltagePanel();
            pressurePlanHost.Controls.Add(customVoltagePanel);
            pressurePlanHost.Controls.Add(pressurePlanGrid);
            layout.Controls.Add(pressurePlanHost, 0, 1);
            arrangeProgramCard = delegate
            {
                card.Padding=condensedPressureSettings?new Padding(4,0,4,0):new Padding(7,4,7,5);
                card.Margin=condensedPressureSettings?Padding.Empty:new Padding(0,0,0,4);
                pressurePlanHost.Margin=condensedPressureSettings?Padding.Empty:new Padding(0,3,0,0);
                mode.Visible=!groupedPressureSettings;
                layout.RowStyles[0].Height = groupedPressureSettings?0:SidebarButtonHeight(planModeButton,customModeButton);
                if (pressurePlanGrid != null)
                {
                    pressurePlanGrid.DefaultCellStyle.Font=pressurePlanGrid.Font;
                    pressurePlanGrid.ColumnHeadersDefaultCellStyle.Font=planModeButton.Font;
                    pressurePlanGrid.ColumnHeadersHeight=Math.Max(42,pressurePlanGrid.Font.Height*2+8);
                    int rowHeight=Math.Max(30,pressurePlanGrid.Font.Height+8);
                    pressurePlanGrid.RowTemplate.Height=rowHeight;
                    foreach(DataGridViewRow row in pressurePlanGrid.Rows)if(row.Height!=rowHeight)row.Height=rowHeight;
                }
            };
            layout.SizeChanged += delegate { arrangeProgramCard(); };
            card.Controls.Add(layout);
            return card;
        }

        DataGridView BuildPlanGrid()
        {
            DataGridView grid = new DataGridView();
            grid.Name = "PressurePlanGrid";
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = grid.AllowUserToResizeColumns = false;
            grid.ReadOnly = true;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersHeight = Math.Max(42,Font.Height*2+8);
            grid.RowTemplate.Height = 30;
            grid.BackgroundColor = PressureTestTheme.Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = PressureTestTheme.Border;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 241, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = PressureTestTheme.Ink;
            grid.ColumnHeadersDefaultCellStyle.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.ForeColor = PressureTestTheme.Ink;
            grid.DefaultCellStyle.Font = PressureTestTheme.Font(9.5f);
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.SelectionBackColor = PressureTestTheme.Accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.ScrollBars = ScrollBars.Vertical;
            grid.Columns.Add("U0", "U0 倍数");
            grid.Columns.Add("Count", "次数");
            grid.Columns.Add("Voltage", "RMS/kV");
            // Row indices are execution-plan indices, never a display sort order.
            foreach(DataGridViewColumn column in grid.Columns)
                column.SortMode=DataGridViewColumnSortMode.NotSortable;
            var voltageDisplayMenu=new ContextMenuStrip();
            var rmsDisplay=new ToolStripMenuItem("RMS / kV");
            var peakDisplay=new ToolStripMenuItem("Peak / kV");
            rmsDisplay.Click+=delegate{SetPlanVoltageDisplayMode("RMS");};
            peakDisplay.Click+=delegate{SetPlanVoltageDisplayMode("PEAK");};
            voltageDisplayMenu.Items.Add(rmsDisplay);voltageDisplayMenu.Items.Add(peakDisplay);
            grid.Disposed+=delegate{voltageDisplayMenu.Dispose();};
            grid.ColumnHeaderMouseClick+=delegate(object sender,DataGridViewCellMouseEventArgs e){
                if(e.ColumnIndex!=2)return;
                rmsDisplay.Checked=PlanVoltageDisplayMode=="RMS";peakDisplay.Checked=!rmsDisplay.Checked;
                var bounds=grid.GetCellDisplayRectangle(2,-1,true);
                voltageDisplayMenu.Show(grid,new Point(bounds.Left,bounds.Bottom));
            };
            grid.SelectionChanged += delegate
            {
                if (suppressUiEvents || grid.SelectedRows.Count == 0) return;
                int index = grid.SelectedRows[0].Index;
                if (index >= 0 && index != SelectedPlanStepIndex) ExecuteParameterAction("PLAN_STEP", delegate { SetSelectedPlanStep(index); });
            };
            return grid;
        }

        Panel BuildCustomVoltagePanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = PressureTestTheme.Surface, Padding = new Padding(2) };
            TableLayoutPanel layout = Table(1, 4);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
            Label voltageCaption=Caption("电压设置");
            layout.Controls.Add(voltageCaption, 0, 0);
            TableLayoutPanel modes = Segments(3);
            u0VoltageModeButton = Segment("U0", delegate { SetCustomVoltageMode("U0"); }, "VOLTAGE_MODE");
            rmsVoltageModeButton = Segment("有效值", delegate { SetCustomVoltageMode("RMS"); }, "VOLTAGE_MODE");
            peakVoltageModeButton = Segment("峰值", delegate { SetCustomVoltageMode("PEAK"); }, "VOLTAGE_MODE");
            u0VoltageModeButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            rmsVoltageModeButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            peakVoltageModeButton.Font = PressureTestTheme.Font(9.5f, FontStyle.Bold);
            modes.Controls.Add(u0VoltageModeButton, 0, 0);
            modes.Controls.Add(rmsVoltageModeButton, 1, 0);
            modes.Controls.Add(peakVoltageModeButton, 2, 0);
            layout.Controls.Add(modes, 0, 1);
            TableLayoutPanel editor = Table(3, 2);
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 39f));
            editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
            PressureTestButton minus = EditorButton("−");
            minus.Name="CustomVoltageDecrease";minus.AccessibleName="− 0.1";
            customVoltageMinusButton=minus;
            minus.Click += delegate { ExecuteParameterAction("VOLTAGE_DECREASE", delegate { ChangeCustomVoltage(-0.1); }); };
            Panel valueEditorHost = new Panel
            {
                Name = "CustomVoltageEditorHost",
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 2, 4, 2),
                BackColor = PressureTestTheme.Surface
            };
            customVoltageValueButton = new PressureTestButton
            {
                Name = "CustomVoltageValueEditor",
                Dock = DockStyle.None,
                Margin = Padding.Empty,
                Size = new Size(100, 56),
                Text = "0.5U0",
                Font = PressureTestTheme.Font(16f, FontStyle.Bold)
            };
            customVoltageValueButton.Click += delegate { BeginCustomVoltageEdit(); };
            customVoltageInlineEditor = new TextBox
            {
                Name = "CustomVoltageInlineEditor",
                Dock = DockStyle.None,
                Margin = Padding.Empty,
                AutoSize = false,
                Size = new Size(100, 40),
                Text = "0.5",
                TextAlign = HorizontalAlignment.Center,
                BorderStyle = BorderStyle.FixedSingle,
                Font = PressureTestTheme.Font(16f, FontStyle.Bold),
                ForeColor = PressureTestTheme.Navy,
                BackColor = PressureTestTheme.Surface,
                Visible = false
            };
            customVoltageInlineEditor.Leave += delegate { CommitCustomVoltageEditor(); };
            customVoltageInlineEditor.KeyDown += delegate(object sender, KeyEventArgs eventArgs)
            {
                if (eventArgs.KeyCode == Keys.Enter)
                {
                    CommitCustomVoltageEditor();
                    eventArgs.SuppressKeyPress = true;
                }
                else if (eventArgs.KeyCode == Keys.Escape)
                {
                    EndCustomVoltageEdit(false);
                    eventArgs.SuppressKeyPress = true;
                }
            };
            valueEditorHost.Controls.Add(customVoltageInlineEditor);
            valueEditorHost.Controls.Add(customVoltageValueButton);
            valueEditorHost.Resize += delegate { LayoutCustomVoltageEditor(valueEditorHost); };
            LayoutCustomVoltageEditor(valueEditorHost);
            customVoltageValueButton.BringToFront();
            PressureTestButton plus = EditorButton("+");
            plus.Name="CustomVoltageIncrease";plus.AccessibleName="+ 0.1";
            customVoltagePlusButton=plus;
            plus.Click += delegate { ExecuteParameterAction("VOLTAGE_INCREASE", delegate { ChangeCustomVoltage(0.1); }); };
            editor.Controls.Add(valueEditorHost, 0, 0);
            editor.SetRowSpan(valueEditorHost,2);
            editor.Controls.Add(plus, 1, 0);
            editor.Controls.Add(minus, 1, 1);
            editor.SizeChanged += delegate
            {
                // One vertical stepper column leaves the value readable in a
                // narrower sidebar. Each symbol keeps its full font height.
                int stepWidth=Math.Max(32,Math.Max(minus.Font.Height,plus.Font.Height)+14)+5;
                editor.ColumnStyles[1].Width=stepWidth;
                editor.ColumnStyles[2].Width=compactHorizontalVoltageStepper?stepWidth:0;
            };
            layout.Controls.Add(editor, 0, 2);
            customModeHintLabel = Label(String.Empty, 9.5f, FontStyle.Regular, PressureTestTheme.SecondaryInk, ContentAlignment.MiddleCenter);
            customModeHintLabel.Name = "CustomVoltageHintLabel";
            customModeHintLabel.AutoSize=false;
            customModeHintLabel.AutoEllipsis=true;
            layout.Controls.Add(customModeHintLabel, 0, 3);
            arrangeCustomVoltage = delegate
            {
                panel.Padding=condensedPressureSettings?Padding.Empty:new Padding(2);
                customModeHintLabel.Margin=condensedPressureSettings?Padding.Empty:new Padding(3);
                voltageCaption.Visible=!condensedPressureSettings;
                customModeHintLabel.Visible=!compactVoltageSummaryInAction;
                customVoltageValueButton.AccessibleDescription=customModeHintLabel.Text;
                editor.SuspendLayout();
                editor.SetRowSpan(valueEditorHost,compactHorizontalVoltageStepper?1:2);
                editor.SetCellPosition(minus,new TableLayoutPanelCellPosition(compactHorizontalVoltageStepper?2:1,compactHorizontalVoltageStepper?0:1));
                editor.RowStyles[0].Height=compactHorizontalVoltageStepper?100:50;
                editor.RowStyles[1].SizeType=compactHorizontalVoltageStepper?SizeType.Absolute:SizeType.Percent;
                editor.RowStyles[1].Height=compactHorizontalVoltageStepper?0:50;
                editor.ColumnStyles[2].Width=compactHorizontalVoltageStepper?editor.ColumnStyles[1].Width:0;
                editor.ResumeLayout(true);
                layout.RowStyles[0].Height = condensedPressureSettings?0:Math.Max(24,Font.Height+3);
                layout.RowStyles[1].Height = SidebarButtonHeight(u0VoltageModeButton,rmsVoltageModeButton,peakVoltageModeButton);
                layout.RowStyles[2].Height = CustomEditorHeight();
                layout.RowStyles[3].Height = compactVoltageSummaryInAction?0:CustomVoltageHintHeight();
            };
            layout.SizeChanged += delegate { arrangeCustomVoltage(); };
            panel.Controls.Add(layout);
            return panel;
        }

        Control BuildModeAndRangeCard()
        {
            PressureTestCard card = Card(new Padding(7, 4, 7, 5), new Padding(0, 4, 0, 4));
            TableLayoutPanel layout = Table(1, 5);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Label pressureCaption=Caption("加压模式"),rangeCaption=Caption("量程选择");
            layout.Controls.Add(pressureCaption, 0, 0);
            TableLayoutPanel runModes = Segments(2);
            normalModeButton = Segment("自动加压", delegate { SetAdvancedMode(false); }, "CONTROL_MODE");
            advancedModeButton = Segment("手动加压", delegate { SetAdvancedMode(true); }, "CONTROL_MODE");
            runModes.Controls.Add(normalModeButton, 0, 0);
            runModes.Controls.Add(advancedModeButton, 1, 0);
            layout.Controls.Add(runModes, 0, 1);
            layout.Controls.Add(rangeCaption, 0, 2);
            TableLayoutPanel rangeModes = Segments(2);
            automaticRangeButton = Segment("自动量程", delegate { SetAutomaticRange(true); }, "RANGE_MODE");
            manualRangeButton = Segment("手动量程", delegate { SetAutomaticRange(false); }, "RANGE_MODE");
            rangeModes.Controls.Add(automaticRangeButton, 0, 0);
            rangeModes.Controls.Add(manualRangeButton, 1, 0);
            layout.Controls.Add(rangeModes, 0, 3);
            manualRangePanel = BuildManualRangePanel();
            layout.Controls.Add(manualRangePanel, 0, 4);
            arrangeRangeCard = delegate
            {
                bool showMode=!groupedPressureSettings||showPressureModePage,showRange=!groupedPressureSettings||!showPressureModePage;
                bool reducedCaptions=groupedPressureSettings||condensedPressureSettings;
                card.Padding=reducedCaptions?new Padding(4,0,4,0):new Padding(7,4,7,5);
                card.Margin=reducedCaptions?Padding.Empty:new Padding(0,4,0,4);
                pressureCaption.Visible=rangeCaption.Visible=!reducedCaptions;
                runModes.Visible=showMode;rangeModes.Visible=showRange;
                layout.RowStyles[0].Height = reducedCaptions?0:Math.Max(24,Font.Height+3);
                layout.RowStyles[1].Height = showMode?SidebarButtonHeight(normalModeButton,advancedModeButton):0;
                layout.RowStyles[2].Height = reducedCaptions?0:Math.Max(24,Font.Height+3);
                layout.RowStyles[3].Height = showRange?SidebarButtonHeight(automaticRangeButton,manualRangeButton):0;
                manualRangePanel.Visible=showRange&&(!AutomaticRange||!IsDischargeCalibrated);
            };
            layout.SizeChanged += delegate { arrangeRangeCard(); };
            card.Controls.Add(layout);
            return card;
        }

        Panel BuildManualRangePanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0) };
            TableLayoutPanel layout = Table(1, 2);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            Label caption = Caption("局放量程");
            rangeSelectorButton = new PressureTestButton
            {
                Name = "RangeSelectorButton",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 0),
                Font = PressureTestTheme.Font(9.5f, FontStyle.Bold)
            };
            rangeMenu = new ContextMenuStrip
            {
                Name = "RangeSelectorMenu",
                AutoSize = false,
                BackColor = PressureTestTheme.Surface,
                ForeColor = PressureTestTheme.Ink,
                Font = PressureTestTheme.Font(9.5f, FontStyle.Bold),
                Padding = new Padding(4),
                ShowCheckMargin = false,
                ShowImageMargin = false,
                DropShadowEnabled = true,
                Renderer = new PressureTestRangeMenuRenderer()
            };
            for (int i = 0; i < RangeLabels.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(RangeLabels[i])
                {
                    AutoSize = false,
                    Margin = new Padding(0, 1, 0, 1),
                    Font = rangeMenu.Font
                };
                item.Tag = RangeCodes[i];
                item.Click += delegate(object sender, EventArgs eventArgs)
                {
                    ToolStripMenuItem selected = sender as ToolStripMenuItem;
                    if (selected != null) ExecuteParameterAction("RANGE", delegate { SetSelectedRange(Convert.ToString(selected.Tag, CultureInfo.InvariantCulture)); });
                };
                rangeMenu.Items.Add(item);
            }
            rangeMenu.Opening += delegate { LayoutRangeMenu(); };
            rangeSelectorButton.Click += delegate
            {
                LayoutRangeMenu();
                rangeMenu.Show(rangeSelectorButton, new Point(0, rangeSelectorButton.Height + 2));
            };
            layout.Controls.Add(caption, 0, 0);
            layout.Controls.Add(rangeSelectorButton, 0, 1);
            arrangeManualRange = delegate
            {
                caption.Visible=!groupedPressureSettings&&!condensedPressureSettings;
                layout.RowStyles[0].Height = groupedPressureSettings||condensedPressureSettings?0:Math.Max(24,Font.Height+3);
                layout.RowStyles[1].Height = SidebarButtonHeight(rangeSelectorButton);
            };
            layout.SizeChanged += delegate { arrangeManualRange(); };
            panel.Controls.Add(layout);
            return panel;
        }

        void LayoutRangeMenu()
        {
            if (rangeMenu == null) return;
            int width = Math.Max(200, rangeSelectorButton == null ? 220 : rangeSelectorButton.Width);
            foreach(ToolStripItem item in rangeMenu.Items)width=Math.Max(width,TextRenderer.MeasureText(item.Text,rangeMenu.Font).Width+30);
            int rowHeight = Math.Max(30,rangeMenu.Font.Height+8);
            rangeMenu.SuspendLayout();
            try
            {
                rangeMenu.Width = width;
                foreach (ToolStripItem item in rangeMenu.Items)
                    item.Size = new Size(Math.Max(1, width - rangeMenu.Padding.Horizontal - 2), rowHeight);
                rangeMenu.Height = rangeMenu.Padding.Vertical + rangeMenu.Items.Count * (rowHeight + 2) + 2;
            }
            finally
            {
                rangeMenu.ResumeLayout(true);
            }
        }

        Control BuildActionCard()
        {
            PressureTestCard card = Card(new Padding(9, 8, 9, 8), Padding.Empty);
            card.Name = "ActionCard";
            actionLayout = Table(4, 4);
            actionLayout.Name = "ActionLayout";
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58f));
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72f));
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));
            actionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0f));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43f));
            actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            pressureActionButton = new PressureActionButton { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 5), Primary = true, Font = PressureTestTheme.Font(13f, FontStyle.Bold) };
            pressureActionButton.Name = "PressureActionButton";
            pressureActionButton.Click += delegate { PressureActionClicked(); };
            actionLayout.Controls.Add(pressureActionButton, 0, 0);
            actionLayout.SetColumnSpan(pressureActionButton, 4);
            pressureActionHintLabel = Label(String.Empty, 9.5f, FontStyle.Regular, PressureTestTheme.SecondaryInk, ContentAlignment.MiddleCenter);
            pressureActionHintLabel.Name = "PostProcessHintLabel";
            pressureActionHintLabel.Visible = false;
            pressureMessageLayout = Table(2, 1);
            pressureMessageLayout.Name = "PressureMessageSummaryRow";
            // A default AutoSize row retains the button's original 34 px
            // preferred height even after the parent allocates a compact row.
            pressureMessageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            pressureMessageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pressureMessageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62f));
            pressureMessageLayout.Visible = false;
            pressureMessageDetailsButton = new PressureTestButton { Name = "PressureMessageDetailsButton", Dock = DockStyle.Fill,
                Margin = new Padding(3, 0, 0, 0), Text = "详情", Font = PressureTestTheme.Font(9.5f) };
            pressureMessageDetailsButton.Click += delegate
            {
                using (Form details = CreatePressureMessageDetailsDialog()) details.ShowDialog(FindForm());
            };
            pressureMessageLayout.Controls.Add(pressureActionHintLabel, 0, 0);
            pressureMessageLayout.Controls.Add(pressureMessageDetailsButton, 1, 0);
            actionLayout.Controls.Add(pressureMessageLayout, 0, 1);
            actionLayout.SetColumnSpan(pressureMessageLayout, 4);
            deleteButton = new PressureTestButton { Dock = DockStyle.Fill, Margin = new Padding(0, 3, 3, 0), Text = "删除", Danger = true };
            deleteButton.Name = "DeleteButton";
            deleteButton.Click += delegate { if(PressureParametersLocked)return;if(DataSaved)RequestDelete();else RequestSaveCurrentRound(); };
            automaticPressureCheck = new CheckBox
            {
                Name = "AutomaticPostProcessCheckBox",
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 3, 3, 0),
                Text = "自动保存",
                TextAlign = ContentAlignment.MiddleCenter,
                CheckAlign = ContentAlignment.MiddleLeft,
                Font = PressureTestTheme.Font(9.5f, FontStyle.Bold),
                ForeColor = PressureTestTheme.Ink,
                BackColor = PressureTestTheme.Surface,
                UseVisualStyleBackColor = false
            };
            automaticPressureCheck.CheckedChanged += delegate
            {
                if (!suppressUiEvents) ExecuteParameterAction("AUTOMATIC_POST_PROCESS", delegate { SetAutomaticPressure(automaticPressureCheck.Checked); });
            };
            highVoltageLabel = new PressureEmergencyStatusLabel {Dock=DockStyle.Fill,Font=PressureTestTheme.Font(9.5f,FontStyle.Bold),ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter,Text="● 急停反馈待确认 · 加压禁用"};
            highVoltageLabel.Name = "HighVoltageStatusLabel";
            highVoltageLabel.Click += delegate { ShowPressureEmergencyAcknowledgement(); };
            highVoltageLabel.Margin = new Padding(3, 3, 0, 0);
            highVoltageLabel.BackColor = PressureTestTheme.Success;
            actionLayout.Controls.Add(deleteButton, 0, 2);
            manualSaveButton=new PressureTestButton {Name="ManualSaveRoundButton",Dock=DockStyle.Fill,Margin=new Padding(0,3,3,0),Text="保存"};
            manualSaveButton.Click+=delegate{RequestSaveCurrentRound();};
            actionLayout.Controls.Add(manualSaveButton, 1, 2);
            actionLayout.Controls.Add(automaticPressureCheck, 3, 2);
            actionLayout.Controls.Add(highVoltageLabel, 0, 3);
            actionLayout.SetColumnSpan(highVoltageLabel, 4);
            actionLayout.SizeChanged += delegate
            {
                ArrangePressureMessage();
            };
            card.Controls.Add(actionLayout);
            card.SizeChanged += delegate
            {
                card.Padding = card.Parent != null && card.Parent.ClientSize.Height < 480
                    ? new Padding(9, 3, 9, 3) : card.ClientSize.Height < 130
                    ? new Padding(7, 4, 7, 4)
                    : new Padding(9, 8, 9, 8);
            };
            return card;
        }

        Control BuildPhaseSelector()
        {
            PressureTestCard card = Card(new Padding(7, 5, 7, 5), Padding.Empty);
            TableLayoutPanel phases = Segments(3);
            AddPhase(phases, 0, "A", "A相");
            AddPhase(phases, 1, "B", "B相");
            AddPhase(phases, 2, "C", "C相");
            card.Controls.Add(phases);
            card.SizeChanged += delegate
            {
                card.Padding = card.ClientSize.Height < 45
                    ? new Padding(7, 2, 7, 2)
                    : new Padding(7, 5, 7, 5);
            };
            return card;
        }

        Control BuildTaskInformationBar()
        {
            Panel bar = new Panel
            {
                Name = "TaskInformationBar",
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = new Padding(12, 2, 12, 2),
                BackColor = PressureTestTheme.SurfaceAlt
            };
            bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = PressureTestTheme.Border });
            taskInformationLabel = Label(String.Empty, 9.5f, FontStyle.Regular,
                PressureTestTheme.SecondaryInk, ContentAlignment.MiddleLeft);
            taskInformationLabel.Name = "TaskInformationText";
            taskInformationLabel.AutoEllipsis = true;
            bar.Controls.Add(taskInformationLabel);
            taskInformationLabel.BringToFront();
            return bar;
        }

        static TableLayoutPanel Table(int columns, int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = columns, RowCount = rows };
        }

        static int SidebarTextHeight(Control control,int width)
        {
            if(control==null)return 24;
            return TextRenderer.MeasureText(control.Text.Length==0?"Ag国":control.Text,control.Font,
                new Size(Math.Max(20,width),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+4;
        }
        static int SidebarButtonHeight(params Control[] controls)
        { return Math.Max(32,controls.Where(c=>c!=null).Select(c=>TextRenderer.MeasureText(
            String.IsNullOrEmpty(c.Text)?"Ag国":c.Text,c.Font,new Size(Math.Max(20,c.ClientSize.Width-14),Int32.MaxValue),
            TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+10).DefaultIfEmpty(32).Max()); }
        static int SidebarSingleLineWidth(Control control)
        { return control==null?0:TextRenderer.MeasureText(control.Text??String.Empty,control.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width; }
        static int SidebarSegmentRowWidth(params Control[] controls)
        { return controls.Length*(controls.Where(c=>c!=null).Select(c=>SidebarSingleLineWidth(c)+20).DefaultIfEmpty(0).Max())+18; }
        int PreferredPressureSidebarWidth()
        {
            int width=Math.Max(SidebarSegmentRowWidth(planModeButton,customModeButton),
                Math.Max(SidebarSegmentRowWidth(normalModeButton,advancedModeButton),
                    SidebarSegmentRowWidth(automaticRangeButton,manualRangeButton)));
            width=Math.Max(width,SidebarSegmentRowWidth(u0VoltageModeButton,rmsVoltageModeButton,peakVoltageModeButton));
            width=Math.Max(width,SidebarSegmentRowWidth(phaseButtons.Values.Cast<Control>().ToArray()));
            if(groupedPressureSettings)
                width=Math.Max(width,new Control[]{compactCustomButton,compactPlanButton,compactModeButton,compactRangeButton}.Sum(item=>SidebarSingleLineWidth(item)+18));
            if(ClientSize.Height<520&&automaticPressureCheck!=null)
            {
                // On a very short viewport Details shares the save row. Allow
                // at most two lines for Auto-save, otherwise its four-line
                // checkbox consumes the value editor's remaining height.
                // Ask WinForms for the native checkbox/glyph allowance, then
                // retain the same 14 px inner text allowance as sidebar buttons.
                int checkboxInset=Math.Max(0,automaticPressureCheck.GetPreferredSize(Size.Empty).Width-
                    SidebarSingleLineWidth(automaticPressureCheck))+14;
                int automaticWidth=(SidebarSingleLineWidth(automaticPressureCheck)+1)/2+checkboxInset+automaticPressureCheck.Margin.Horizontal;
                width=Math.Max(width,18+Math.Max(40,SidebarSingleLineWidth(deleteButton)+18)+
                    Math.Max(40,SidebarSingleLineWidth(manualSaveButton)+18)+
                    Math.Max(60,SidebarSingleLineWidth(pressureMessageDetailsButton)+20)+automaticWidth);
            }
            // Both program modes keep the existing plan sidebar width. The
            // custom editor must not choose a narrower/wider body column when
            // switching modes or editing its value.
            if(pressurePlanGrid!=null)
            {
                // The existing two-line header may wrap at spaces. Measuring
                // "U0 multiplier" as one line needlessly widens every column.
                int widest=pressurePlanGrid.Columns.Cast<DataGridViewColumn>().SelectMany(column=>
                    column.HeaderText.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries)).Select(word=>
                    TextRenderer.MeasureText(word,pressurePlanGrid.ColumnHeadersDefaultCellStyle.Font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width+8).DefaultIfEmpty(0).Max();
                width=Math.Max(width,widest*3+32);
            }
            return width;
        }
        int CustomEditorHeight()
        { return Math.Max(48,Math.Max(customVoltageValueButton==null?48:customVoltageValueButton.Font.Height+14,(compactHorizontalVoltageStepper?1:2)*Math.Max(28,customVoltagePlusButton==null?28:customVoltagePlusButton.Font.Height+10)+4)); }
        int CustomVoltageHintHeight()
        {return Math.Max(24,customModeHintLabel==null?Font.Height+6:customModeHintLabel.Font.Height+6);}
        int MetricPreferredHeight(Control card)
        {
            var caption=card.Controls.Find(card.Name+"Caption",true).FirstOrDefault();
            var value=card.Name=="MetricPd"?maxPdValueLabel:card.Name=="MetricCapacitance"?capacitanceValueLabel:card.Name=="MetricFrequency"?frequencyValueLabel:tanDeltaValueLabel;
            int checkbox=card.Name=="MetricCapacitance"?SidebarTextHeight(useCompensationCapacitanceCheck,Math.Max(60,card.ClientSize.Width-37))+6:0;
            return SidebarTextHeight(caption,Math.Max(60,card.ClientSize.Width-18))+(value==null?28:value.Font.Height)+10+checkbox;
        }
        int DisplaySettingsHeight()
        { return 22+Math.Max(24,oneCycleButton.Font.Height+4)+SidebarButtonHeight(oneCycleButton,twoCycleButton,maximumWindowButton)+(vlfPolarityAvailable?Math.Max(24,Font.Height+4)+SidebarButtonHeight(positiveRadio,negativeRadio):0); }
        int ProgramSettingsHeight()
        {
            if (PressureProgramModeCode == "PLAN")
                return (condensedPressureSettings?0:22)+(groupedPressureSettings?0:SidebarButtonHeight(planModeButton,customModeButton))+
                    Math.Max(42,pressurePlanGrid.Font.Height*2+8)+Math.Max(30,pressurePlanGrid.Font.Height+8)*Math.Min(5,Math.Max(1,pressurePlanGrid.Rows.Count));
            int hint=compactVoltageSummaryInAction?0:CustomVoltageHintHeight();
            return (condensedPressureSettings?0:22)+(groupedPressureSettings?0:SidebarButtonHeight(planModeButton,customModeButton))+(condensedPressureSettings?0:Math.Max(24,Font.Height+3))+
                SidebarButtonHeight(u0VoltageModeButton,rmsVoltageModeButton,peakVoltageModeButton)+
                CustomEditorHeight()+hint+4;
        }
        int RangeSettingsHeight()
        {
            if(groupedPressureSettings)return showPressureModePage?SidebarButtonHeight(normalModeButton,advancedModeButton)+4:SidebarButtonHeight(automaticRangeButton,manualRangeButton)+SidebarButtonHeight(rangeSelectorButton)+10;
            if(condensedPressureSettings)return SidebarButtonHeight(normalModeButton,advancedModeButton)+SidebarButtonHeight(automaticRangeButton,manualRangeButton)+SidebarButtonHeight(rangeSelectorButton)+10;
            return 28+Math.Max(24,Font.Height+3)*3+SidebarButtonHeight(normalModeButton,advancedModeButton)+SidebarButtonHeight(automaticRangeButton,manualRangeButton)+SidebarButtonHeight(rangeSelectorButton);
        }
        int ActionCardHeight()
        {
            ArrangePressureMessage();
            int padding=actionLayout!=null&&actionLayout.Parent!=null&&actionLayout.Parent.Parent!=null&&actionLayout.Parent.Parent.ClientSize.Height<480?6:16;
            return padding+PressureActionRowHeight()+PressureAuxiliaryRowHeight()+PressureStatusRowHeight()+pressureMessageRowHeight;
        }

        string pressureMessageGeometryInput;
        string pressureSidebarGeometryInput;
        Size measuredPressureSidebarContentSize;
        static void AppendPressureGeometryControl(StringBuilder key,Control control)
        {
            if(control==null){key.Append("|null");return;}
            string text=control.Text??String.Empty;
            key.Append('|').Append(text.Length).Append(':').Append(text).Append('|').Append(control.Font)
                .Append('|').Append(control.ClientSize.Width).Append('|').Append(control.Margin);
        }
        string PressureMessageGeometryInput()
        {
            var key=new StringBuilder();key.Append(ClientSize.Height).Append('|').Append(actionLayout.ClientSize.Width);
            AppendPressureGeometryControl(key,deleteButton);AppendPressureGeometryControl(key,manualSaveButton);
            AppendPressureGeometryControl(key,automaticPressureCheck);AppendPressureGeometryControl(key,highVoltageLabel);
            return key.ToString();
        }
        string PressureSidebarGeometryInput()
        {
            var key=new StringBuilder();key.Append(ClientSize).Append('|').Append(Font).Append('|')
                .Append(groupedPressureSettings).Append('|').Append(condensedPressureSettings).Append('|')
                .Append(compactHorizontalVoltageStepper).Append('|').Append(compactVoltageSummaryInAction).Append('|');
            if(actionLayout!=null)key.Append(actionLayout.Parent==null||actionLayout.Parent.Parent==null?0:actionLayout.Parent.Parent.ClientSize.Height);
            foreach(var control in new Control[]{planModeButton,customModeButton,normalModeButton,advancedModeButton,
                automaticRangeButton,manualRangeButton,u0VoltageModeButton,rmsVoltageModeButton,peakVoltageModeButton,
                compactCustomButton,compactPlanButton,compactModeButton,compactRangeButton,pressureActionButton,
                deleteButton,manualSaveButton,pressureMessageDetailsButton,automaticPressureCheck,highVoltageLabel})
                AppendPressureGeometryControl(key,control);
            foreach(var control in phaseButtons.Values)AppendPressureGeometryControl(key,control);
            if(pressurePlanGrid!=null){key.Append('|').Append(pressurePlanGrid.ColumnHeadersDefaultCellStyle.Font);
                foreach(DataGridViewColumn column in pressurePlanGrid.Columns)key.Append('|').Append(column.HeaderText);}
            return key.ToString();
        }
        Size MeasurePressureSidebarContentIfChanged()
        {
            string input=PressureSidebarGeometryInput();
            if(input!=pressureSidebarGeometryInput){
                measuredPressureSidebarContentSize=new Size(PreferredPressureSidebarWidth(),ActionCardHeight());
                pressureSidebarGeometryInput=PressureSidebarGeometryInput();
            }
            return measuredPressureSidebarContentSize;
        }

        int PressureActionRowHeight()
        { return pressureActionButton==null?116:Math.Max(116,(int)Math.Ceiling((SidebarButtonHeight(pressureActionButton)+pressureActionButton.Margin.Vertical)*1.2)); }
        int PressureAuxiliaryRowHeight()
        {
            return Math.Max(SidebarButtonHeight(deleteButton,manualSaveButton,pressureMessageInline?pressureMessageDetailsButton:null)+3,
                SidebarTextHeight(automaticPressureCheck,Math.Max(20,automaticPressureCheck==null?80:automaticPressureCheck.Width-20))+8);
        }
        int PressureStatusRowHeight()
        {
            int baseline=Math.Max(30,(highVoltageLabel==null?Font.Height:highVoltageLabel.Font.Height)*(ClientSize.Height<520?1:2)+6);
            if(highVoltageLabel==null||highVoltageLabel.ClientSize.Width<=0)return baseline;
            // Measure the actual wrapped safety text and include the table-cell
            // margin. Two English lines can be one pixel taller than 2*Font.Height;
            // short windows must not silently reserve only one line either.
            int textHeight=TextRenderer.MeasureText(highVoltageLabel.Text,highVoltageLabel.Font,
                new Size(highVoltageLabel.ClientSize.Width,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
            return Math.Max(baseline,textHeight+highVoltageLabel.Margin.Vertical);
        }

        void ArrangePressureMessage()
        {
            if (arrangingPressureMessage || actionLayout == null) return;
            arrangingPressureMessage = true;
            try
            {
                // Diagnostics live in the dedicated debug window. Keep their
                // backing text for that window/accessibility, never in a row
                // whose appearance displaces the main pressure control.
                pressureMessageInline = false;
                pressureMessageRowHeight = 0;
                pressureMessageLayout.Visible = false;
                pressureActionHintLabel.Visible = false;
                pressureMessageDetailsButton.Visible = false;
                manualSaveButton.Visible = false; // Save/Delete share one state button.
                pressureActionHintLabel.AccessibleDescription = pressureActionFullMessage;
                pressureMessageDetailsButton.AccessibleDescription = pressureActionFullMessage;
                pressureActionHintLabel.Text = String.Empty;
                string geometry=PressureMessageGeometryInput();
                if(geometry==pressureMessageGeometryInput)return;
                pressureMessageGeometryInput=geometry;
                highVoltageLabel.AutoEllipsis = ClientSize.Height < 520;
                actionLayout.SuspendLayout();
                try {
                    // Keep the shared Save/Delete action comfortable to click;
                    // measure translations and scale the minimum with the font.
                    float saveWidth=Math.Max(96,Math.Max(deleteButton.Font.Height*6,SidebarSingleLineWidth(deleteButton)+32));
                    float auxiliaryHeight=PressureAuxiliaryRowHeight(),statusHeight=PressureStatusRowHeight();
                    if(actionLayout.ColumnStyles[0].Width!=saveWidth)actionLayout.ColumnStyles[0].Width=saveWidth;
                    if(actionLayout.ColumnStyles[1].Width!=0)actionLayout.ColumnStyles[1].Width=0;
                    if(actionLayout.ColumnStyles[2].Width!=0)actionLayout.ColumnStyles[2].Width=0;
                    if(actionLayout.RowStyles[1].Height!=0)actionLayout.RowStyles[1].Height=0;
                    if(actionLayout.RowStyles[2].Height!=auxiliaryHeight)actionLayout.RowStyles[2].Height=auxiliaryHeight;
                    if(actionLayout.RowStyles[3].Height!=statusHeight)actionLayout.RowStyles[3].Height=statusHeight;
                } finally {actionLayout.ResumeLayout(true);}
            }
            finally { arrangingPressureMessage = false; }
        }

        static string FitPressureMessageSummary(string message, Font font, int width, int height)
        {
            if (String.IsNullOrEmpty(message) || height <= 0) return String.Empty;
            string candidate = message.Length <= 256 ? message : message.Substring(0, 256);
            Func<string, bool> fits = text => TextRenderer.MeasureText(text, font, new Size(width, Int32.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + 4 <= height;
            if (message.Length <= 256 && fits(candidate)) return candidate;
            int low = 0, high = candidate.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (fits(candidate.Substring(0, mid).TrimEnd() + "…")) low = mid; else high = mid - 1;
            }
            // Do not split a UTF-16 surrogate pair in the visible summary.
            if (low > 0 && Char.IsHighSurrogate(candidate[low - 1])) low--;
            return candidate.Substring(0, low).TrimEnd() + "…";
        }

        Form CreatePressureMessageDetailsDialog()
        {
            return new PressureMessageDetailsDialog(pressureActionFullMessage, LanguageIndex, Font, FindForm());
        }

        sealed class PressureMessageDetailsDialog : Form
        {
            readonly TextBox fullText;
            readonly Label copyStatus;
            readonly int language;
            Icon ownedIcon;
            public PressureMessageDetailsDialog(string message, int languageIndex, Font uiFont, Form owner)
            {
                language = languageIndex;
                Name = "PressureMessageDetailsDialog";
                Text = Xiongxin.OWTS.Localization.UiLanguage.Text(language, "完整提示", "Full message");
                Font = uiFont;
                StartPosition = FormStartPosition.CenterParent;
                Size = new Size(640, 420);
                MinimumSize = new Size(400, 260);
                BackColor = PressureTestTheme.Surface;
                ForeColor = PressureTestTheme.Ink;
                if (owner != null && owner.Icon != null) ownedIcon = (Icon)owner.Icon.Clone();
                else
                {
                    string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "xiongxin.ico");
                    if (System.IO.File.Exists(iconPath)) ownedIcon = new Icon(iconPath);
                }
                if (ownedIcon != null) Icon = ownedIcon;
                var layout = Table(1, 3);
                layout.Padding = new Padding(12);
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(26, Font.Height + 8)));
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(38, Font.Height + 16)));
                fullText = new TextBox { Name = "PressureMessageFullText", Dock = DockStyle.Fill, Multiline = true,
                    ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Text = message ?? String.Empty,
                    AccessibleName = Text, BackColor = PressureTestTheme.Surface, ForeColor = PressureTestTheme.Ink };
                copyStatus = new Label { Name = "PressureMessageCopyStatus", Dock = DockStyle.Fill, AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft, ForeColor = PressureTestTheme.SecondaryInk };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                var close = new PressureTestButton { Name = "PressureMessageCloseButton", Text = Xiongxin.OWTS.Localization.UiLanguage.Text(language, "关闭", "Close"),
                    Height = Math.Max(32, Font.Height + 12), DialogResult = DialogResult.Cancel, Primary = true, Font = uiFont };
                var copy = new PressureTestButton { Name = "PressureMessageCopyButton", Text = Xiongxin.OWTS.Localization.UiLanguage.Text(language, "复制全文", "Copy all"), Height = close.Height, Font = uiFont };
                close.Width = Math.Max(86, TextRenderer.MeasureText(close.Text, Font).Width + 24);
                copy.Width = Math.Max(100, TextRenderer.MeasureText(copy.Text, Font).Width + 24);
                copy.Click += delegate { CopyFullMessage(null); };
                footer.Controls.Add(close); footer.Controls.Add(copy);
                layout.Controls.Add(fullText, 0, 0); layout.Controls.Add(copyStatus, 0, 1); layout.Controls.Add(footer, 0, 2);
                Controls.Add(layout);
                CancelButton = close;
            }
            void CopyFullMessage(Action<string> copy)
            {
                try
                {
                    if (copy == null) Clipboard.SetText(fullText.Text); else copy(fullText.Text);
                    copyStatus.Text = Xiongxin.OWTS.Localization.UiLanguage.Text(language, "全文已复制", "Full message copied");
                }
                catch (System.Runtime.InteropServices.ExternalException)
                { copyStatus.Text = Xiongxin.OWTS.Localization.UiLanguage.Text(language, "剪贴板暂不可用；可选择文本后按 Ctrl+C。", "Clipboard unavailable; select text and press Ctrl+C."); }
            }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing && ownedIcon != null) { ownedIcon.Dispose(); ownedIcon = null; }
            }
        }
        Size pressureSidebarContentSize;
        void RefreshPressureSidebarLayout()
        {
            pressureSidebarGeometryInput=null;
            pressureMessageGeometryInput=null;
            UpdateEmbeddedLayout();
            arrangePressureBody?.Invoke();
            arrangeDisplaySettings?.Invoke();arrangePressureActions?.Invoke();arrangePressureSettings?.Invoke();arrangeRealtimeSidebar?.Invoke();
            if(rootLayout!=null)rootLayout.PerformLayout();
            if (pressureActionButton != null && automaticPressureCheck != null)
                pressureSidebarContentSize = MeasurePressureSidebarContentIfChanged();
        }

        void UpdateCompensationCapacitanceUi()
        {
            if(useCompensationCapacitanceCheck==null)return;
            bool previous=suppressUiEvents;suppressUiEvents=true;
            try { useCompensationCapacitanceCheck.Checked=UseCompensationCapacitance; }
            finally { suppressUiEvents=previous; }
        }

        static Panel Divider(string name, bool vertical)
        {
            return new Panel
            {
                Name = name,
                Dock = DockStyle.Fill,
                Margin = vertical ? new Padding(2, 0, 2, 0) : new Padding(0, 2, 0, 2),
                BackColor = PressureTestTheme.Border,
                TabStop = false
            };
        }

        static PressureTestCard Card(Padding padding, Padding margin)
        {
            return new PressureTestCard { Dock = DockStyle.Fill, Padding = padding, Margin = margin };
        }

        static Label Label(string text, float size, FontStyle style, Color color, ContentAlignment alignment)
        {
            return new Label { Dock = DockStyle.Fill, Text = text, Font = PressureTestTheme.Font(size, style), ForeColor = color, TextAlign = alignment };
        }

        static Label Section(string icon, string text)
        {
            Label label = Label(icon + "  " + text, 10.5f, FontStyle.Bold, PressureTestTheme.Ink, ContentAlignment.MiddleLeft);
            label.AutoEllipsis = true;
            return label;
        }

        static Label Caption(string text)
        {
            return Label(text, 9.5f, FontStyle.Bold, PressureTestTheme.SecondaryInk, ContentAlignment.BottomLeft);
        }

        static RadioButton Radio(string text, Point location)
        {
            return new RadioButton { Text = text, Location = location, Size = new Size(82, 34), ForeColor = PressureTestTheme.Ink, Font = PressureTestTheme.Font(9.5f, FontStyle.Bold), UseVisualStyleBackColor = true };
        }

        static TableLayoutPanel Segments(int count)
        {
            TableLayoutPanel layout = Table(count, 1);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            for (int i = 0; i < count; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
            return layout;
        }

        PressureTestSegmentButton Segment(string text, Action action, string code = "SELECTION")
        {
            PressureTestSegmentButton button = new PressureTestSegmentButton { Dock = DockStyle.Fill, Margin = new Padding(2), Text = text };
            button.Click += delegate { if (!suppressUiEvents && action != null) ExecuteParameterAction(code, action); };
            return button;
        }

        static PressureTestSegmentButton NavigationSegment(string text,Action show)
        {
            var button=new PressureTestSegmentButton{Dock=DockStyle.Fill,Margin=new Padding(2),Text=text,WrapText=true};
            button.Click+=delegate {show();};
            return button;
        }

        static PressureTestButton EditorButton(string text)
        {
            return new PressureTestButton
            {
                Dock = DockStyle.Fill,
                Size = new Size(44, 56),
                Margin = new Padding(2,1,2,1),
                Text = text,
                Font = PressureTestTheme.Font(12f, FontStyle.Bold)
            };
        }

        void AddPhase(TableLayoutPanel parent, int column, string code, string text)
        {
            PressureTestSegmentButton button = Segment(text, delegate { SetSelectedPhase(code); }, "PHASE");
            button.Name = "Phase" + code + "Button";
            phaseButtons[code] = button;
            parent.Controls.Add(button, column, 0);
        }

        void ChangeCustomVoltage(double delta)
        {
            double current = CustomVoltageValue;
            double value = Math.Round(Math.Max(0, Math.Min(999.9, current + delta)), 1, MidpointRounding.AwayFromZero);
            double minimum;
            lock (stateLock) minimum = MinimumCustomVoltageValueNoLock();
            // Decrease stops at the positive floor, not at an accidental zero
            // reference. Zero is still available by explicit reference input.
            if (current > 0 && IsFinite(minimum) && minimum <= 999.9 && value < minimum) value = minimum;
            SetCustomVoltageValue(value);
        }

        void BeginCustomVoltageEdit()
        {
            if (customVoltageValueButton == null || customVoltageInlineEditor == null) return;
            customVoltageEditorOriginalText = FormatCustomVoltageValue(CustomVoltageValue);
            customVoltageInlineEditor.Text = customVoltageEditorOriginalText;
            customVoltageValueButton.Visible = false;
            customVoltageInlineEditor.Visible = true;
            customVoltageInlineEditor.BringToFront();
            customVoltageInlineEditor.Focus();
            customVoltageInlineEditor.SelectAll();
        }

        void WireCustomVoltageCommitOnExternalClick(Control control)
        {
            if (control == null) return;
            if (!Object.ReferenceEquals(control, customVoltageInlineEditor))
                control.MouseDown += CommitCustomVoltageOnExternalMouseDown;
            foreach (Control child in control.Controls)
                WireCustomVoltageCommitOnExternalClick(child);
        }

        void CommitCustomVoltageOnExternalMouseDown(object sender, MouseEventArgs eventArgs)
        {
            if (customVoltageInlineEditor == null || !customVoltageInlineEditor.Visible) return;
            if (Object.ReferenceEquals(sender, customVoltageInlineEditor)) return;
            CommitCustomVoltageEditor();
        }

        void LayoutCustomVoltageEditor(Panel host)
        {
            if (host == null || customVoltageValueButton == null || customVoltageInlineEditor == null) return;
            int width = Math.Max(1, host.ClientSize.Width);
            int buttonHeight = Math.Max(1, Math.Min(Math.Max(56,customVoltageValueButton.Font.Height+10), host.ClientSize.Height));
            int editorHeight = Math.Max(1, Math.Min(Math.Max(40,customVoltageInlineEditor.Font.Height+8), host.ClientSize.Height));
            customVoltageValueButton.SetBounds(0, Math.Max(0, (host.ClientSize.Height - buttonHeight) / 2), width, buttonHeight);
            customVoltageInlineEditor.SetBounds(0, Math.Max(0, (host.ClientSize.Height - editorHeight) / 2), width, editorHeight);
        }

        void CommitCustomVoltageEditor()
        {
            if (customVoltageInlineEditor == null || !customVoltageInlineEditor.Visible) return;
            string text = customVoltageInlineEditor.Text.Trim();
            bool edited = !String.Equals(text, customVoltageEditorOriginalText, StringComparison.Ordinal);
            double value;
            bool parsed = Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            CloseCustomVoltageEdit();
            if (edited && parsed && value >= 0 && value <= 999.9)
                ExecuteParameterAction("VOLTAGE_VALUE", delegate { SetCustomVoltageValue(value); });
        }

        void EndCustomVoltageEdit(bool commit)
        {
            if (commit) CommitCustomVoltageEditor();
            else CloseCustomVoltageEdit();
        }

        void CloseCustomVoltageEdit()
        {
            customVoltageEditorOriginalText = String.Empty;
            if (customVoltageInlineEditor != null) customVoltageInlineEditor.Visible = false;
            if (customVoltageValueButton != null)
            {
                customVoltageValueButton.Visible = true;
                customVoltageValueButton.BringToFront();
            }
            UpdateCustomVoltageUi();
        }

        void ApplyAllStateToUi()
        {
            UpdatePolarityUi();
            UpdateProgramUi();
            UpdateAdvancedUi();
            UpdateAutomaticPressureUi();
            UpdateRangeUi();
            UpdateCustomVoltageUi();
            RefreshPlanGrid();
            UpdateMetricsUi();
            UpdateLocationFilterUi();
            UpdateChannelUi();
            UpdatePhaseUi();
            UpdateWaveformWindowUi();
            UpdatePressureUi();
            UpdateTaskInformationUi();
            UpdatePostProcessUi();
        }

        bool vlfPolarityAvailable;
        Control polarityCaption, polarityContainer;
        public bool VlfPolarityAvailable
        {
            get { return vlfPolarityAvailable; }
            set
            {
                vlfPolarityAvailable = value;
                if (polarityCaption != null) polarityCaption.Visible = value;
                if (polarityContainer != null) { polarityContainer.Visible = value; polarityContainer.Enabled = value; }
                arrangeDisplaySettings?.Invoke();
                arrangeRealtimeSidebar?.Invoke();
            }
        }

        void UpdatePolarityUi()
        {
            if (positiveRadio == null) return;
            suppressUiEvents = true;
            try { positiveRadio.Checked = PositivePolarity; negativeRadio.Checked = !PositivePolarity; }
            finally { suppressUiEvents = false; }
        }

        void UpdateProgramUi()
        {
            if (planModeButton == null) return;
            planModeButton.Enabled = planVoltagesKv.Length > 0;
            if(compactPlanButton!=null)compactPlanButton.Enabled=planModeButton.Enabled;
            if (planVoltagesKv.Length == 0) planMode = false;
            bool plan = PressureProgramModeCode == "PLAN";
            planModeButton.Selected = plan;
            customModeButton.Selected = !plan;
            pressurePlanGrid.Visible = plan;
            customVoltagePanel.Visible = !plan;
            if (plan) pressurePlanGrid.BringToFront(); else customVoltagePanel.BringToFront();
            if (!plan) UpdateCustomVoltageUi();
            arrangePressureSettings?.Invoke();
        }

        void UpdateAdvancedUi()
        {
            if (advancedModeButton == null) return;
            advancedModeButton.Selected = AdvancedMode;
            normalModeButton.Selected = !AdvancedMode;
            normalModeButton.Enabled = planMode && planVoltagesKv.Length > 0 && !PressureControlsDisplayLocked;
        }

        void UpdateAutomaticPressureUi()
        {
            if (automaticPressureCheck == null) return;
            suppressUiEvents = true;
            try { automaticPressureCheck.Checked = AutomaticPressureEnabled; }
            finally { suppressUiEvents = false; }
            UpdatePostProcessUi();
        }

        void UpdatePostProcessUi()
        {
            if (!IsBusy) UpdateActionBusyUiForRound();
            if (deleteButton != null) {
                deleteButton.Text=DataSaved?PressureUiText("删除","Delete"):PressureUiText("保存","Save");
                deleteButton.Danger=DataSaved;
                deleteButton.Enabled=!PressureControlsDisplayLocked&&!IsBusy && (DataSaved?DeleteEnabled:
                    hasAcquisitionResult&&!currentAcquisitionReferenceOnly&&!currentAcquisitionReferencePreview&&!IsPressureRequestBlockedNoLock()&&
                    PressureStateCode!="FAULT"&&PostProcessStateCode!="ACQUISITION_INVALID"&&PostProcessStateCode!="ACQUISITION_FAILED");
                deleteButton.AccessibleDescription=DataSaved?PressureUiText("删除当前已保存记录","Delete the current saved record"):PressureUiText("保存当前完整采集","Save the current complete capture");
            }
            if (pressureActionButton != null)
            {
                string state = PostProcessStateCode;
                pressureActionButton.Enabled = CanStopCurrentPressure || CanFinishCurrentRound || !PressureParametersLocked && state != "SAVE_REQUESTED" &&
                    state != "ANALYSIS_REQUESTED" && state != "OVERRANGE_DECISION_REQUIRED" && CanRequestVoltageNoLock(CalculatedTargetPeakVoltageKv);
            }
            if (pressureActionHintLabel == null) return;
            if(manualSaveButton!=null)manualSaveButton.Enabled=!IsBusy&&hasAcquisitionResult&&!dataSaved&&!currentAcquisitionReferenceOnly&&!currentAcquisitionReferencePreview&&!IsPressureRequestBlockedNoLock();
            string message = String.Empty;
            if (AutomaticPostProcessEnabled) switch (PostProcessStateCode)
            {
                case "SAVE_REQUESTED":
                    message = OverRangeIgnored ? "超量程已忽略，等待保存完成" : "正在自动保存，删除暂不可用";
                    break;
                case "ANALYSIS_REQUESTED":
                    message = "保存完成，正在转入分析";
                    break;
                case "OVERRANGE_DECISION_REQUIRED":
                    message = "数据超量程：等待确认是否保存，不自动再次加压";
                    break;
                case "OVERRANGE_REJECTED":
                    message = "超量程数据未保存；重新采集须手动启动";
                    break;
                case "REPRESSURE_REQUESTED":
                    message = "本次不保存、不分析，等待重新加压";
                    break;
                case "COMPLETE":
                    message = OverRangeIgnored ? "超量程已忽略，保存完成，可删除" : "保存完成，可删除";
                    break;
                case "SAVE_FAILED":
                    message = "自动保存失败，删除仍禁用";
                    break;
                case "ANALYSIS_FAILED":
                    message = "自动分析失败，删除仍禁用";
                    break;
            }
            if(LastPressureRangeReviewStatus.Length>0)message=LastPressureRangeReviewStatus;
            if(voltageLimitMessage.Length>0)message=voltageLimitMessage;
            if (!CanRequestVoltageNoLock(CalculatedTargetPeakVoltageKv)) message = PressureUiText("急停未解除或回读未知，非零加压禁用", "Emergency stop active / unknown: nonzero pressure disabled");
            if (PostProcessStateCode == "ACQUISITION_INVALID" || PostProcessStateCode == "ACQUISITION_FAILED") message = LastPostProcessError;
            if (projectPlanError.Length > 0) message = projectPlanError;
            bool visible = message.Length > 0;
            if (IsBusy) { message = "忙碌中 · 等待主程序完成"; visible = true; }
            if (FinishCurrentRoundPending) { message = PressureUiText("当前轮完成后停止；不启动后续加压。", "Finish this round; no further pressure steps."); visible = true; }
            else if (AutomaticPressureStopped && message.Length == 0) { message = PressureUiText("已退出自动加压，后续须手动启动。", "Automatic pressure exited; further starts are manual."); visible = true; }
            pressureActionFullMessage = visible ? message : String.Empty;
            ArrangePressureMessage();
            // Value-only plan/save changes do not require resizing every pane.
            // Still update the message and safety state above on every call.
            var contentSize = MeasurePressureSidebarContentIfChanged();
            if (contentSize != pressureSidebarContentSize) RefreshPressureSidebarLayout();
            if(!updatingPressureUi)UpdatePressureUi();
        }

        void UpdateRangeUi()
        {
            if (automaticRangeButton == null) return;
            bool previousVisible = manualRangePanel.Visible;
            string previousText = rangeSelectorButton == null ? String.Empty : rangeSelectorButton.Text;
            automaticRangeButton.Selected = AutomaticRange;
            manualRangeButton.Selected = !AutomaticRange;
            manualRangeButton.Enabled = AdvancedMode && !PressureControlsDisplayLocked;
            automaticRangeButton.Enabled = !PressureControlsDisplayLocked;
            bool calibrated=IsDischargeCalibrated;
            manualRangePanel.Visible = !AutomaticRange||!calibrated;
            int index = Array.FindIndex(RangeCodes, code => String.Equals(code, SelectedRangeCode, StringComparison.OrdinalIgnoreCase));
            if (rangeSelectorButton != null)
            {
                rangeSelectorButton.Text = calibrated&&index>=0 ?
                    (GetVisibleDischargeRanges()[index]?RangeLabels[index]:PressureUiText("请选择量程","Select range"))+"  ▾" : UncalibratedRangeLabel;
                rangeSelectorButton.Enabled = calibrated&&AdvancedMode&&!PressureControlsDisplayLocked;
                rangeSelectorButton.AccessibleDescription=calibrated&&index>=0?"Pico "+SelectedPicoRangeCode+"; ± "+SelectedFullScaleVoltageV.ToString("G17",CultureInfo.InvariantCulture)+" V; ± "+SelectedExactFullScalePc.ToString("G17",CultureInfo.InvariantCulture)+" pC; "+DischargeCalibrationPcPerVolt.ToString("G17",CultureInfo.InvariantCulture)+" pC/V; "+DischargeCalibrationSource:UncalibratedRangeLabel;
            }
            if (rangeMenu != null)
            {
                string[] labels=RangeLabels;bool[] visible=GetVisibleDischargeRanges();int rangeIndex=0;
                foreach (ToolStripItem menuItem in rangeMenu.Items)
                {
                    ToolStripMenuItem item = menuItem as ToolStripMenuItem;
                    if (item != null)
                    {
                        item.Text=labels[rangeIndex];item.Available=calibrated&&visible[rangeIndex++];
                        item.Enabled=item.Available&&AdvancedMode&&!PressureControlsDisplayLocked;
                        item.Checked = String.Equals(Convert.ToString(item.Tag, CultureInfo.InvariantCulture),
                            SelectedRangeCode, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            if (previousVisible != manualRangePanel.Visible ||
                previousText != (rangeSelectorButton == null ? String.Empty : rangeSelectorButton.Text))
                arrangePressureSettings?.Invoke();
            if(waveformView!=null)waveformView.SetDischargeScale(!AutomaticRange,SelectedExactFullScalePc);
        }

        void UpdateCustomVoltageUi()
        {
            if (u0VoltageModeButton == null) return;
            string mode = CustomVoltageModeCode;
            u0VoltageModeButton.Selected = mode == "U0";
            rmsVoltageModeButton.Selected = mode == "RMS";
            peakVoltageModeButton.Selected = mode == "PEAK";
            if (customVoltageValueButton != null)
                customVoltageValueButton.Text = FormatCustomVoltageValue(CustomVoltageValue) + (mode == "U0" ? "U0" : " kV");
            double targetPeak = CalculatedTargetPeakVoltageKv;
            customModeHintLabel.Text = IsFinite(targetPeak) && targetPeak >= 0
                ? String.Format(CultureInfo.InvariantCulture, PressureUiText("目标峰值 {0:0.0} kV", "Target peak {0:0.0} kV"), targetPeak)
                : PressureUiText("目标峰值未知（无法计算）", "Target peak unknown");
            string minimumDescription = PressureUiText("非零电压最低峰值为 1 kV；0 kV 仅用于参考采集。", "Minimum nonzero peak: 1 kV. Zero is for reference acquisition only.");
            customModeHintLabel.AccessibleDescription=customModeHintLabel.Text + ". " + minimumDescription;
            customVoltageValueButton.AccessibleDescription=customModeHintLabel.AccessibleDescription;
            customVoltageInlineEditor.AccessibleDescription=customModeHintLabel.AccessibleDescription;
        }

        static string FormatCustomVoltageValue(double value)
        {
            // Operator-facing precision only. The unchanged editor commit
            // path retains the original value, conversion and 1 kV peak floor.
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        void RefreshPlanGrid()
        {
            if (pressurePlanGrid == null) return;
            double[] multipliers = GetPressurePlanMultipliers();
            int[] repetitions = GetPressurePlanRepetitions();
            double[] voltages = GetPressurePlanVoltagesKv();
            suppressUiEvents = true;
            try
            {
                pressurePlanGrid.Rows.Clear();
                pressurePlanGrid.Columns[0].HeaderText = PressureUiText("U0 倍数", "U0 multiplier");
                pressurePlanGrid.Columns[1].HeaderText = PressureUiText("次数", "Runs");
                pressurePlanGrid.Columns[2].HeaderText = PlanVoltageHeaderText();
                pressurePlanGrid.Columns[2].HeaderCell.ToolTipText = PressureUiText("点击切换有效值 / 峰值显示；不修改项目计划或加压目标。", "Click to display RMS / peak; the stored plan and target are unchanged.");
                pressurePlanGrid.AccessibleDescription = pressurePlanGrid.Columns[2].HeaderCell.ToolTipText;
                for (int i = 0; i < multipliers.Length; i++)
                    pressurePlanGrid.Rows.Add(multipliers[i].ToString("0.0###############", CultureInfo.InvariantCulture), repetitions[i].ToString(CultureInfo.InvariantCulture), PlanVoltageDisplayText(voltages[i]));
                SelectPlanGridRow(SelectedPlanStepIndex);
            }
            finally { suppressUiEvents = false; }
            UpdatePressureUi();
        }

        void SelectPlanGridRow(int index)
        {
            if (pressurePlanGrid == null || index < 0 || index >= pressurePlanGrid.Rows.Count) return;
            if(pressurePlanGrid.CurrentCell!=null&&pressurePlanGrid.CurrentCell.RowIndex==index&&
                pressurePlanGrid.Rows[index].Selected&&pressurePlanGrid.SelectedRows.Count==1)return;
            bool previous=suppressUiEvents;
            suppressUiEvents = true;
            try
            {
                pressurePlanGrid.ClearSelection();
                pressurePlanGrid.Rows[index].Selected = true;
                pressurePlanGrid.CurrentCell = pressurePlanGrid.Rows[index].Cells[0];
            }
            finally { suppressUiEvents = previous; }
        }

        void UpdateMetricsUi()
        {
            if (maxPdValueLabel == null) return;
            var pdCaption=Controls.Find("MetricPdCaption",true).FirstOrDefault();
            if(pdCaption!=null)pdCaption.Text=currentAcquisitionReferenceOnly?
                PressureUiText("采集峰值（参考）/ pC","Capture peak (reference) / pC"):
                PressureUiText("局放最大值 / pC","Max PD / pC");
            SetPressureUiText(maxPdValueLabel,MetricText(PartialDischargeMaximumPc,"0"));
            // Status/availability/compensation determine the final values.
            // Do not first paint raw or differently formatted intermediate text.
            UpdateMetricStatusVisuals();
        }

        static string MetricText(double value,string format="0.0")
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) ? "—" : value.ToString(format, CultureInfo.InvariantCulture);
        }

        static void SetPressureUiText(Control control,string text)
        { if(control!=null&&control.Text!=text)control.Text=text; }

        void UpdateLocationFilterUi()
        {
            // Task-28 filtering remains available through the public API for host compatibility.
            // Its former inline controls were deliberately removed with the legacy location card.
        }

        void UpdateChannelUi()
        {
            if (spectrumDashboardView != null)
                spectrumDashboardView.SelectedSpectrumCode = SelectedChannelCode;
            UpdateTaskInformationUi();
        }

        void UpdatePhaseUi()
        {
            foreach (KeyValuePair<string, PressureTestSegmentButton> item in phaseButtons)
                item.Value.Selected = String.Equals(item.Key, SelectedPhaseCode, StringComparison.OrdinalIgnoreCase);
            UpdateTaskInformationUi();
        }

        void UpdateWaveformWindowUi()
        {
            if (oneCycleButton == null) return;
            string code = WaveformWindowCode;
            oneCycleButton.Selected = code == "ONE";
            twoCycleButton.Selected = code == "TWO";
            maximumWindowButton.Selected = code == "MAXIMUM";
            if (waveformView != null) waveformView.SetDisplayWindow(code, OscillationFrequencyHz);
        }

        // Display ownership only: a completed result belongs to the selection
        // used for that round, not to whichever plan row is selected afterward.
        // Keep the result/waveform state unchanged for inspection and saving.
        long pressureActionDisplayRequestSequence = -1;
        long pressureActionDisplayVoltageSequence, pressureActionDisplayProgramSequence, pressureActionDisplayPhaseSequence;
        int pressureActionDisplayPlanStep, pressureActionDisplayRepetition;
        string pressureActionDisplayProject;
        bool updatingPressureUi;

        void UpdatePressureFeedbackFill(string pressureState)
        {
            if(pressureActionButton==null)return;
            bool measured=pressureState=="RUNNING"||pressureState=="RAMPING"||pressureState=="HOLDING"||pressureState=="COMPLETE"||pressureState=="FAULT";
            if(currentAcquisitionReferencePreview)measured=false;
            double actual=ActualPeakVoltageKv,requested=RequestedTargetPeakVoltageKv;
            bool live=pressureState=="RUNNING"||pressureState=="RAMPING"||pressureState=="HOLDING";
            if(live&&pressureVoltageFeedbackSequence==PressureRequestSequence)actual=pressureVoltageFeedbackKv;
            double ratio=measured&&IsFinite(actual)&&actual>=0&&IsFinite(requested)&&requested>0?actual/requested:Double.NaN;
            string post=PostProcessStateCode;
            bool pending=post=="WAITING_FOR_ACQUISITION"||post=="SAVE_REQUESTED"||post=="ANALYSIS_REQUESTED"||post=="AWAITING_MANUAL_SAVE"||post=="OVERRANGE_DECISION_REQUIRED";
            bool failed=post.EndsWith("FAILED",StringComparison.Ordinal)||post=="ACQUISITION_INVALID"||post=="CANCELED"||post=="OVERRANGE_REJECTED";
            bool complete=pressureVoltageReachedSequence==PressureRequestSequence||
                pressureState=="COMPLETE"&&!pending&&!failed&&!operatorPressureStopRequested;
            pressureActionButton.MeasurementProgressVisible=measured;
            pressureActionButton.MeasurementProgress=measured&&complete?1:IsFinite(ratio)?Math.Min(.95,Math.Max(0,ratio)):Double.NaN;
        }

        void UpdatePressureUi()
        {
            if (pressureActionButton == null || updatingPressureUi) return;
            updatingPressureUi=true;
            try {
            string pressureState;
            bool selectedTargetWaiting;
            lock (stateLock)
            {
                pressureState=pressureStateCode;
                if(pressureState!="COMPLETE" || pressureActionDisplayRequestSequence!=pressureRequestSequence)
                {
                    pressureActionDisplayRequestSequence=pressureRequestSequence;
                    pressureActionDisplayVoltageSequence=voltageSettingChangedSequence;
                    pressureActionDisplayProgramSequence=programModeChangedSequence;
                    pressureActionDisplayPhaseSequence=phaseChangedSequence;
                    pressureActionDisplayPlanStep=selectedPlanStepIndex;
                    pressureActionDisplayRepetition=currentPlanRepetition;
                    pressureActionDisplayProject=ProjectPath;
                }
                selectedTargetWaiting=pressureState=="COMPLETE" &&
                    (pressureActionDisplayVoltageSequence!=voltageSettingChangedSequence ||
                     pressureActionDisplayProgramSequence!=programModeChangedSequence ||
                     pressureActionDisplayPhaseSequence!=phaseChangedSequence ||
                     pressureActionDisplayPlanStep!=selectedPlanStepIndex ||
                     pressureActionDisplayRepetition!=currentPlanRepetition ||
                     pressureActionDisplayProject!=ProjectPath ||
                     postProcessStateCode=="WAITING_FOR_ACQUISITION");
            }
            if(selectedTargetWaiting)pressureState="WAITING";
            string title;
            bool success = false, danger = false;
            switch (pressureState)
            {
                case "RUNNING": title = PressureUiText("正在加压","Pressurizing"); break;
                case "RAMPING": title = PressureUiText("正在加压","Pressurizing"); break;
                case "HOLDING": title = PressureUiText("保持加压","Holding voltage"); break;
                case "COMPLETE": title = PressureUiText("加压完成","Completed"); success = true; break;
                case "STOPPED": title = PressureUiText("采集已停止","Capture stopped"); break;
                case "FAULT": title = PressureUiText("本轮未完成","Round incomplete"); danger = true; break;
                default: title = PressureUiText("等待加压","Ready"); break;
            }
            bool postPending=PostProcessStateCode=="WAITING_FOR_ACQUISITION"||PostProcessStateCode=="SAVE_REQUESTED"||
                PostProcessStateCode=="ANALYSIS_REQUESTED"||PostProcessStateCode=="AWAITING_MANUAL_SAVE"||PostProcessStateCode=="OVERRANGE_DECISION_REQUIRED";
            bool postFailed=PostProcessStateCode.EndsWith("FAILED",StringComparison.Ordinal)||PostProcessStateCode=="ACQUISITION_INVALID"||PostProcessStateCode=="CANCELED"||PostProcessStateCode=="OVERRANGE_REJECTED";
            bool roundComplete=pressureState=="COMPLETE"&&!postPending&&!postFailed&&!operatorPressureStopRequested;
            if(pressureState=="COMPLETE"&&postPending) {
                title=PostProcessStateCode=="ANALYSIS_REQUESTED"?PressureAnalysisProgressTitle():
                    PostProcessStateCode=="SAVE_REQUESTED"?PressureUiText("正在保存","Saving"):PressureUiText("等待保存","Awaiting save");success=false;
            }
            if(postFailed&&pressureState!="STOPPED"){
                success=false;danger=true;
                title=PostProcessStateCode=="SAVE_FAILED"?PressureUiText("保存失败","Save failed"):
                    PostProcessStateCode=="ANALYSIS_FAILED"?PressureUiText("分析失败","Analysis failed"):
                    PressureUiText("本轮未完成","Round incomplete");
            }
            double target = CalculatedTargetPeakVoltageKv;
            string repetition = String.Empty;
            if (PressureProgramModeCode == "PLAN")
                repetition = "  ·  " + CurrentPlanRepetition.ToString(CultureInfo.InvariantCulture) + "/" +
                    CurrentPlanRepetitionTotal.ToString(CultureInfo.InvariantCulture) + PressureUiText(" 次"," runs");
            string peakTitle = title + PressureUiText(" · 峰值", " · Peak");
            bool knownTarget = IsFinite(target) && target >= 0;
            string actionText = peakTitle + Environment.NewLine + (knownTarget ? target.ToString("0.0", CultureInfo.InvariantCulture) + " kV" : "—") + repetition;
            bool measuredProgress=pressureState=="RUNNING"||pressureState=="RAMPING"||pressureState=="HOLDING"||pressureState=="COMPLETE"||pressureState=="FAULT";
            double actualPeak=ActualPeakVoltageKv,requestedPeak=RequestedTargetPeakVoltageKv;
            bool live=pressureState=="RUNNING"||pressureState=="RAMPING"||pressureState=="HOLDING";
            if(live&&pressureVoltageFeedbackSequence==PressureRequestSequence)actualPeak=pressureVoltageFeedbackKv;
            // Voltage progress is not a save/analysis completion indicator.
            // A reached tolerance window is full only after the real backend
            // PASS, including an actual 0.98 target which is within tolerance.
            UpdatePressureFeedbackFill(pressureState);
            if(measuredProgress)
            {
                string actualText=IsFinite(actualPeak)&&actualPeak>=0?actualPeak.ToString("0.0",CultureInfo.InvariantCulture):"—";
                string requestedText=IsFinite(requestedPeak)&&requestedPeak>=0?requestedPeak.ToString("0.0",CultureInfo.InvariantCulture):"—";
                actionText=peakTitle+Environment.NewLine+actualText+" / "+requestedText+" kV"+repetition;
                pressureActionButton.AccessibleDescription=PressureUiText("当前反馈 / 目标峰值；背景来自实测比例，完整成功结束才满格。","Current feedback / target peak; measured ratio, full only after successful completion.")+" "+pressureVoltageFeedbackSource;
            }
            else pressureActionButton.AccessibleDescription = knownTarget ? String.Empty : PressureUiText("目标峰值未知（无法计算）", "Target peak unknown (cannot calculate)");
            if (CanStopCurrentPressure)
            {
                string stopTitle=operatorPressureStopRequested?PressureUiText("正在停止 · 等待回执","Stopping · awaiting acknowledgement"):
                    (danger?title:PressureWorkerDisplayTitle())+PressureUiText(" · 点击停止"," · click to stop");
                int firstLine=actionText.IndexOf(Environment.NewLine,StringComparison.Ordinal);
                actionText=stopTitle+(firstLine<0?String.Empty:actionText.Substring(firstLine));
                success=false;
            }
            else if (CanFinishCurrentRound)
            {
                string stopTitle = FinishCurrentRoundPending
                    ? PressureUiText("本轮结束后停止", "Stop after this round")
                    : PressureUiText("完成本轮后停止", "Finish round and stop");
                if(PostProcessStateCode=="ANALYSIS_REQUESTED")
                    stopTitle=PressureAnalysisProgressTitle()+(FinishCurrentRoundPending?PressureUiText(" · 随后停止"," · then stop"):PressureUiText(" · 可停止续轮"," · stop queue"));
                if(danger)
                    stopTitle=title+(FinishCurrentRoundPending?PressureUiText(" · 随后停止"," · then stop"):PressureUiText(" · 停止续轮"," · stop queue"));
                int firstLine = actionText.IndexOf(Environment.NewLine, StringComparison.Ordinal);
                actionText = stopTitle + (firstLine < 0 ? String.Empty : actionText.Substring(firstLine));
                pressureActionButton.AccessibleDescription = PressureUiText("退出自动加压；等待本轮真实采集、保存及回执完成，不启动下一轮。", "Exit automatic pressure; finish this acquisition, save and acknowledgement, then stop future rounds.");
            }
            if(currentAcquisitionReferencePreview && !selectedTargetWaiting)
            {
                actionText=PressureUiText("参考仿真完成","Preview complete")+Environment.NewLine+PressureUiText("非实测","Not measured");
                success=false;
                pressureActionButton.MeasurementProgressVisible=false;
                pressureActionButton.MeasurementProgress=Double.NaN;
            }
            // Keep the selected preset on line two, but never overwrite the
            // boolean action (stop / finish round) on line one with a mode name.
            // The operator requested the preset throughout this primary control.
            // Live feedback still drives control/protection and measured fill;
            // hiding its number must not alter the voltage judgment or data.
            bool repeating=pressureRangeRetries>0&&pressureRangeRetryRequest==pressureRequestSequence;
            string modeTitle=repeating?PressureUiText("重复加压","Retry pressure"):
                !advancedMode&&planMode?PressureUiText("连续加压","Sequence pressure"):PressureUiText("单次加压","Single pressure");
            pressureActionButton.SetModeBadge(repeating?PressureActionButton.PresentationModes.RangeRetry:
                !advancedMode&&planMode?PressureActionButton.PresentationModes.Sequence:PressureActionButton.PresentationModes.Single,modeTitle);
            if(!currentAcquisitionReferencePreview) {
                string compactTitle=operatorPressureStopRequested&&(live||HasActiveHardwareSession)?PressureUiText("正在停止","Stopping"):
                    pressureState=="STOPPED"?PressureUiText("已经停止","Stopped"):
                    live||HasActiveHardwareSession?PressureUiText("正在加压","Pressurizing"):
                    pressureState=="COMPLETE"?PressureUiText("加压完成","Completed"):PressureUiText("开始加压","Start pressure");
                // Pressure can finish before persistence/analysis. Do not label
                // a real failure (red) or pending result as a successful round.
                // Keep the normal measured-fill palette and all workflow state.
                if(pressureState=="FAULT"||postFailed&&pressureState!="STOPPED"||pressureState=="COMPLETE"&&postPending)
                    compactTitle=title;
                if(CanStopCurrentPressure || CanFinishCurrentRound) {
                    int actionLine=actionText.IndexOf(Environment.NewLine,StringComparison.Ordinal);
                    compactTitle=actionLine<0?actionText:actionText.Substring(0,actionLine);
                }
                double displayedVoltage=target;
                string compactCount=pressureState=="STOPPED"?String.Empty:
                    repeating?"  ·  "+PressureUiText("重采 ","Retry ")+pressureRangeRetries.ToString(CultureInfo.InvariantCulture):
                    !advancedMode&&planMode?repetition:String.Empty;
                actionText=compactTitle+Environment.NewLine+
                    (IsFinite(displayedVoltage)&&displayedVoltage>=0?displayedVoltage.ToString("0.0",CultureInfo.InvariantCulture):"—")+" kV"+compactCount;
                pressureActionButton.AccessibleDescription=modeTitle+"；"+(knownTarget?
                    PressureUiText("预设峰值电压；实际反馈继续用于控制和保护。","Preset peak voltage; actual feedback remains active for control and protection."):
                    PressureUiText("目标峰值未知（无法计算）","Target peak unknown (cannot calculate)"));
            }
            if(pressureActionButton.Text!=actionText)pressureActionButton.Text=actionText;
            pressureActionButton.Primary = !success && !danger;
            pressureActionButton.Success = success;
            pressureActionButton.Danger = danger;
            if (connectionStatusLabel != null)
            {
                connectionStatusLabel.Text = SimulationMode ? PressureUiText("仿真 · 非实测","Simulation · Not measured") : HighVoltageStatusCode=="UNKNOWN" ? PressureUiText("设备反馈待确认","Device feedback unconfirmed") : PressureUiText("设备反馈已接收","Device feedback received");
                connectionStatusLabel.BackColor = SimulationMode || HighVoltageStatusCode=="UNKNOWN" ? PressureTestTheme.SecondaryInk : PressureTestTheme.Success;
            }
            UpdateHighVoltageStatusUi();
            UpdateTaskInformationUi();
            UpdatePostProcessUi();
            UpdatePressureNoiseInitializationUi();
            } finally { updatingPressureUi=false; }
        }

        void UpdateTaskInformationUi()
        {
            if (taskInformationLabel == null) return;
            string project;
            double length;
            lock (stateLock) { project = projectName; length = cableLengthMeters; }
            string text = (SimulationMode ? PressureUiText("仿真 · 非实测   ·   仿真示例：","Simulation · Not measured · Example: ") : PressureUiText("任务：","Project: ")) + String.Format(CultureInfo.InvariantCulture,
                PressureUiText("{0}   ·   电缆长度 {1:0.0} m   ·   当前谱图 {2}   ·   当前相位 {3}相   ·   {4}","{0} · Cable {1:0.0} m · View {2} · Phase {3} · {4}"),
                project, length, SelectedChannelCode, SelectedPhaseCode,
                PressureStateCode == "RUNNING" || PressureStateCode == "RAMPING" ? PressureUiText("正在加压","Pressurizing") : PressureUiText("就绪","Ready"));
            if(!String.IsNullOrWhiteSpace(BackgroundPressureAnalysisStatusText))text+="   ·   "+BackgroundPressureAnalysisStatusText;
            if(operatorPressureStopRequested&&HasActiveHardwareSession)text+="   ·   "+PressureUiText("正在停止，等待设备回执","Stopping; awaiting device acknowledgement");
            else if(!String.IsNullOrWhiteSpace(LastPostProcessError))text+="   ·   "+LastPostProcessError;
            SetPressureUiText(taskInformationLabel,text);
        }
    }
}
