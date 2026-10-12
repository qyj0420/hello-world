using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        GripSplitContainer outer, charts;
        FlowLayoutPanel side, actions,archiveActions;
        Label emptyWave, statusLabel;
        Label[] metricLabels, metricValues, metricUnits;
        ComboBox wavelet;
        NumericUpDown highPass, lowPass, levels, ignore;
        Label highLabel, lowLabel, waveLabel, levelsLabel, ignoreLabel;
        CheckBox statisticsEnabledBox;
        TrackBar statisticsSlider;
        Label strengthLabel;
        Button previousButton,nextButton,acceptButton,deleteButton,uncertainButton,evaluateButton,reportButton;
        Label[] classIndicators;
        bool uiSync, layoutReady, resizingCharts,updatingLayoutMinimum,preservingViewportScroll;
        double splitRatio=.46;
        int sideWidth=172;
        Size layoutMinimum;

        // Logical client-area pixels at 96 DPI, not a bitmap scaling target.
        [Browsable(false)] public Size DesignReferenceSize { get { return new Size(1920,1080); } }
        [Browsable(false)] public Size MinimumViewportSize { get { return new Size(1120,700); } }
        [Browsable(false)] public Size MinimumHostWindowSize { get { return new Size(1120,740); } }
        protected override Size DefaultSize { get { return new Size(1920,1080); } }

        // Complete unscrolled workspace size. Unlike Control.MinimumSize this
        // does not force a native host to clip a larger child window.
        [Browsable(false)] public Size RecommendedMinimumSize { get { return layoutMinimum; } }

        [Category("Layout"),DefaultValue(.46)] public double WaveformHeightRatio
        {
            get { return splitRatio; }
            set { if(!Finite(value)||value<.10||value>.90)throw new ArgumentOutOfRangeException("WaveformHeightRatio");splitRatio=value;ApplySplitLayout(); }
        }
        [Category("Layout"),DefaultValue(172)] public int RightPanelWidth
        {
            get { return sideWidth; }
            set { if(value<172||value>520)throw new ArgumentOutOfRangeException("RightPanelWidth");sideWidth=value;ApplySplitLayout(); }
        }
        void InitializeLayout()
        {
            outer=new GripSplitContainer { Name="ReviewChartSidebarSplit",Dock=DockStyle.Fill,Orientation=Orientation.Vertical,FixedPanel=FixedPanel.Panel2,IsSplitterFixed=true,SplitterWidth=5,TabStop=false };
            charts=new GripSplitContainer { Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterWidth=6 };
            Controls.Add(outer);outer.Panel1.Controls.Add(charts);
            pdmap.Dock=DockStyle.Fill;spectrum.Dock=DockStyle.Fill;
            charts.Panel1.Controls.Add(pdmap);charts.Panel2.Controls.Add(spectrum);
            emptyWave=new Label { Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.Gray,BackColor=Color.White };
            charts.Panel1.Controls.Add(emptyWave);
            // The fixed bottom status bar leaves 342 px for the full wavelet
            // page at the minimum viewport. Keep controls and fonts unchanged; a 4 px
            // trailing inset avoids reserving a scrollbar for 2 px of padding.
            side=new FlowLayoutPanel { Name="ReviewSettings",Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,BackColor=ReviewTheme.SurfaceAlt,Padding=new Padding(6,6,6,4) };
            outer.Panel2.Controls.Add(side);
            analysisSettingsButton=ActionButton(152);analysisSettingsButton.Name="ReviewAnalysisSettings";
            ReviewTheme.ButtonRole(analysisSettingsButton,true);
            analysisSettingsButton.Click+=(s,e)=>{if(ShowAnalysisSettingsDialog())SyncThresholdUi();};side.Controls.Add(analysisSettingsButton);
            InitializeAiLearningModeUi();
            InitializeFilterControls();
            statisticsEnabledBox=new CheckBox { Name="ReviewLocationFilterEnabled",Width=156,Height=23,Margin=new Padding(0,4,0,0) };side.Controls.Add(statisticsEnabledBox);
            strengthLabel=TextLabel(156,22);side.Controls.Add(strengthLabel);
            statisticsSlider=new TrackBar { Name="ReviewLocationFilterStrength",Minimum=0,Maximum=100,Value=40,TickFrequency=20,SmallChange=1,LargeChange=10,AutoSize=false,Width=156,Height=30,Margin=new Padding(0) };
            side.Controls.Add(statisticsSlider);
            statisticsEnabledBox.CheckedChanged+=(s,e)=>{if(!uiSync)LocationFilterEnabled=statisticsEnabledBox.Checked;};
            statisticsSlider.ValueChanged+=(s,e)=>{if(!uiSync)LocationFilterStrength=statisticsSlider.Value;};
            InitializeLocationFilterSelection();
            InitializeAHistoryAuxiliaryUi();
            InitializeDisplayAdvisor();
            statusLabel=TextLabel(156,20);statusLabel.Name="ReviewStatus";statusLabel.Margin=Padding.Empty;statusLabel.ForeColor=Color.FromArgb(157,70,38);statusLabel.Visible=false;side.Controls.Add(statusLabel);
            var actionHost=new Panel { Name="ReviewActionsHost",Dock=DockStyle.Bottom,Height=297,BackColor=side.BackColor };
            actions=new FlowLayoutPanel { Name="ReviewActions",Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(6,5,6,4),BackColor=side.BackColor };
            actionHost.Controls.Add(actions);
            actionHost.Controls.Add(new Panel { Name="ReviewActionsDivider",Dock=DockStyle.Top,Height=2,BackColor=ReviewTheme.Border,TabStop=false });
            outer.Panel2.Controls.Add(actionHost);
            classIndicators=new Label[3];
            var classes=new FlowLayoutPanel { Width=156,Height=27,Margin=new Padding(0),WrapContents=false };
            for(int i=0;i<3;i++){classIndicators[i]=new RoundedReviewBadge { Name="ReviewClass"+i,Width=50,Height=24,TextAlign=ContentAlignment.MiddleCenter,Margin=new Padding(1),BackColor=ReviewTheme.Disabled,ForeColor=ReviewTheme.Ink };classes.Controls.Add(classIndicators[i]);}
            actions.Controls.Add(classes);
            InitializeReviewMetrics();
            var navigation=new FlowLayoutPanel { Width=156,Height=31,Margin=new Padding(0),WrapContents=false };
            previousButton=ActionButton(74);previousButton.Name="ReviewPrevious";nextButton=ActionButton(74);nextButton.Name="ReviewNext";navigation.Controls.Add(previousButton);navigation.Controls.Add(nextButton);actions.Controls.Add(navigation);
            previousButton.Click+=(s,e)=>PreviousPdMap();nextButton.Click+=(s,e)=>NextPdMap();
            acceptButton=ActionButton(152);acceptButton.Name="ReviewAccept";deleteButton=ActionButton(152);deleteButton.Name="ReviewDelete";reportButton=ActionButton(152);reportButton.Name="ReviewReport";
            uncertainButton=ActionButton(152);uncertainButton.Name="ReviewUncertain";
            evaluateButton=ActionButton(152);evaluateButton.Name="ReviewEvaluate";
            ReviewTheme.ButtonRole(acceptButton,true);ReviewTheme.ButtonRole(evaluateButton,true);
            rangeFilterButton=ActionButton(152);rangeFilterButton.Name="ReviewRangeFilter";
            rangeFilterButton.Click+=(s,e)=>ShowRangeFilterDialog();
            actions.Controls.Add(acceptButton);actions.Controls.Add(deleteButton);actions.Controls.Add(uncertainButton);actions.Controls.Add(rangeFilterButton);
            archiveActions=new FlowLayoutPanel{Name="ReviewArchiveActions",Dock=DockStyle.Bottom,Height=62,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(6,0,6,4),BackColor=side.BackColor};
            archiveActions.Controls.Add(evaluateButton);archiveActions.Controls.Add(reportButton);actionHost.Controls.Add(archiveActions);
            acceptButton.Click+=(s,e)=>AcceptAndAdvanceManual();deleteButton.Click+=(s,e)=>DeleteCurrent();reportButton.Click+=(s,e)=>ShowSaveAnalysisDialog();
            uncertainButton.Click+=(s,e)=>MarkCurrentUncertain();
            evaluateButton.Click+=(s,e)=>ShowSavedAnalysisDialog();
            InitializeEvaluationPopup();
            InitializeReviewKeyboardHints();
            // Dock/resize also raises SplitterMoved. It must not overwrite the
            // requested fixed sidebar width when this divider is non-draggable.
            outer.SplitterMoved+=(s,e)=>{if(!outer.IsSplitterFixed&&!resizingCharts){sideWidth=outer.Panel2.Width;UpdateMinimum();}};
            charts.SplitterMoved+=(s,e)=>{if(!resizingCharts&&charts.Height>charts.SplitterWidth)splitRatio=(double)charts.SplitterDistance/(charts.Height-charts.SplitterWidth);};
            Resize+=(s,e)=>ApplySplitLayout();
            outer.SizeChanged+=(s,e)=>ApplySplitLayout();
            spectrum.ViewChanged+=(s,e)=>{if(!spectrum.IsCameraViewNotification)ApplySplitLayout();};
            // A diagnostic scrollbar can appear after an inner filter-page
            // change without resizing this control. Refit only when the
            // required sidebar width changes; ordinary paints do no work.
            side.Layout+=(s,e)=>
            {
                if(!layoutReady||resizingCharts||updatingLayoutMinimum)return;
                UpdateMinimum();
                if(outer.Panel2.Width!=SidebarLayoutWidth())ApplySplitLayout();
            };
            InitializeProjectWorkspace();
            InitializeReviewStatusBar();
            InitializeUnifiedAppearance();
            layoutReady=true;UpdateLanguage();SyncFilterUi();UpdateMinimum();ApplySplitLayout();
        }
        static Label TextLabel(int width,int height) { return new Label { Width=width,Height=height,AutoEllipsis=true,Margin=new Padding(0,2,0,2) }; }
        static NumericUpDown Number(decimal minimum,decimal maximum,decimal value,decimal step) { return new ReviewNumericInput { Minimum=minimum,Maximum=maximum,Value=value,Increment=step,Width=68,Height=28,DecimalPlaces=step<1?1:0 }; }
        static Button ActionButton(int width) { var b=new RoundedReviewButton { Width=width,Height=24,FlatStyle=FlatStyle.Flat,Margin=new Padding(2),UseVisualStyleBackColor=false };ReviewTheme.ButtonRole(b,false);return b; }
        void InitializeReviewMetrics()
        {
            int line=Math.Max(20,Font.Height+4);
            var metrics=new Panel { Name="ReviewMetrics",Width=156,Height=line*5,Margin=new Padding(0,2,0,4),BackColor=Color.Transparent };
            metricLabels=new Label[5];metricValues=new Label[5];metricUnits=new Label[5];
            string[] units={"%","m","pC","kV","°"};
            for(int i=0;i<metricLabels.Length;i++)
            {
                metricLabels[i]=MetricCell("ReviewMetric"+i+"Label",0,i*line,72,ContentAlignment.MiddleLeft);
                metricValues[i]=MetricCell("ReviewMetric"+i+"Value",72,i*line,56,ContentAlignment.MiddleRight);
                var unit=metricUnits[i]=MetricCell("ReviewMetric"+i+"Unit",130,i*line,26,ContentAlignment.MiddleLeft);unit.Text=units[i];
                metricLabels[i].Height=metricValues[i].Height=unit.Height=line;
                metrics.Controls.Add(metricLabels[i]);metrics.Controls.Add(metricValues[i]);metrics.Controls.Add(unit);
            }
            actions.Controls.Add(metrics);
        }
        static Label MetricCell(string name,int x,int y,int width,ContentAlignment alignment)
        { return new Label { Name=name,Location=new Point(x,y),Width=width,Height=16,Margin=new Padding(0),TextAlign=alignment,AutoEllipsis=true,BackColor=Color.Transparent }; }
        static string MetricNumber(double value)
        { return Finite(value)?value.ToString("0.0",CultureInfo.InvariantCulture):"—"; }
        bool ReviewCoincidentPending()
        {
            if(selected<0)return false;
            if(pdmap.HasSelectedReflection&&pdmap.CurrentIncidentIndex>=0&&pdmap.CurrentReflectionIndex==pdmap.CurrentIncidentIndex)return true;
            // Retain known loaded coincidence even when its real source indices fall outside the displayed crop.
            double incident=working[selected,10],reflection=working[selected,11];
            return Finite(incident)&&incident>=0&&incident<=int.MaxValue&&incident==Math.Truncate(incident)&&
                Finite(reflection)&&reflection==incident;
        }
        Label AddLabeled(Control input,FlowLayoutPanel target)
        {
            var panel=new Panel { Width=156,Height=30,Margin=new Padding(0,1,0,1) };
            var label=new Label { Width=88,Height=27,Location=new Point(0,2),TextAlign=ContentAlignment.MiddleLeft };input.Location=new Point(88,1);panel.Controls.Add(label);panel.Controls.Add(input);target.Controls.Add(panel);return label;
        }
        void UpdateMinimum()
        {
            if(!layoutReady||updatingLayoutMinimum)return;updatingLayoutMinimum=true;
            try
            {
                // Fixed tree/command widths; only the middle graph area stretches.
                int treeWidth=workspaceTreePanel.Width+workspaceTreeSeparator.Width;
                int headerHeight=workspaceHeader.Height+workspaceHeaderDivider.Height+
                    (unifiedTitleBar!=null&&unifiedTitleBar.Visible?unifiedTitleBar.Height:0)+
                    (compactWorkspace&&compactChartTabs!=null?compactChartTabs.Height:0);
                int plotWidth=Math.Max(pdmap.MinimumSize.Width,spectrum.MinimumSize.Width);
                int plotHeight=compactWorkspace?Math.Max(pdmap.MinimumSize.Height,spectrum.MinimumSize.Height):
                    pdmap.MinimumSize.Height+spectrum.MinimumSize.Height+charts.SplitterWidth;
                int settingsHeight=side.Padding.Vertical;
                foreach(Control child in side.Controls)
                {
                    // Hidden diagnostics must not force the complete workspace
                    // beyond the minimum viewport. Active errors remain readable in
                    // the independently scrolling settings region above actions.
                    if(!child.Visible)continue;
                    settingsHeight+=(child==filterParametersHost?Math.Max(FlowContentHeight(bandParameters),FlowContentHeight(waveletParameters)):child.Height)+child.Margin.Vertical;
                }
                if(side.AutoScroll)
                {
                    Size settingsCanvas=new Size(0,Math.Max(0,settingsHeight));
                    if(side.AutoScrollMinSize!=settingsCanvas)side.AutoScrollMinSize=settingsCanvas;
                }
                int sidebarHeight=compactWorkspace?Math.Max(settingsHeight,FlowContentHeight(actions)+archiveActions.Height)+
                    (compactSideTabs==null?0:compactSideTabs.Height):settingsHeight+actions.Parent.Height;
                // Keep ordinary 9.5 pt operation controls and plots in the host;
                // verbose diagnostic text scrolls locally, not the entire UI.
                // Large fonts retain the complete-workspace scroll fallback.
                // The 1120 px notebook viewport is a supported working size,
                // not a smaller window onto a 1280 px virtual canvas.  The
                // tree, plots and action sidebar already have measured minima;
                // let those minima decide if a genuinely smaller host needs a
                // fallback, while normal notebook windows stay scrollbar-free.
                Size logicalViewport=compactWorkspace?new Size(1065,416):MinimumViewportSize;
                double fontScale=Font==null?1.0:Math.Max(1.0,Font.SizeInPoints/9.5);
                // Width follows translated text, not display zoom. A wider
                // sidebar must never inflate the whole virtual workspace.
                Size viewport=new Size((int)Math.Ceiling(logicalViewport.Width*fontScale),(int)Math.Ceiling(logicalViewport.Height*fontScale));
                if(Font.SizeInPoints<=9.5f&&!compactWorkspace)sidebarHeight=viewport.Height-headerHeight-StatusBarHeight;
                var minimum=new Size(Math.Max(viewport.Width,treeWidth+plotWidth+SidebarLayoutWidth()+outer.SplitterWidth+workspaceBody.Padding.Horizontal),
                    Math.Max(viewport.Height,headerHeight+StatusBarHeight+workspaceBody.Padding.Vertical+Math.Max(plotHeight,sidebarHeight)));
                layoutMinimum=minimum;
                LayoutWorkspaceCanvas();
                // Docking the canvas can finish a pending compact/DPI layout
                // while this reentrancy guard is held. Re-read widths after
                // that pass; otherwise the old scaled plot minimum survives
                // until the next user action and creates spurious scrollbars.
                int settledWidth=Math.Max(viewport.Width,workspaceTreePanel.Width+workspaceTreeSeparator.Width+
                    Math.Max(pdmap.MinimumSize.Width,spectrum.MinimumSize.Width)+SidebarLayoutWidth()+outer.SplitterWidth+workspaceBody.Padding.Horizontal);
                if(layoutMinimum.Width!=settledWidth){layoutMinimum=new Size(settledWidth,layoutMinimum.Height);LayoutWorkspaceCanvas();}
            }
            finally{updatingLayoutMinimum=false;}
        }
        static int FlowContentHeight(Control parent)
        {int height=parent.Padding.Vertical;foreach(Control child in parent.Controls)height+=child.Height+child.Margin.Vertical;return height;}
        void LayoutWorkspaceCanvas()
        {
            if(workspaceCanvas==null)return;
            Size minimum=layoutMinimum.Width>0&&layoutMinimum.Height>0?layoutMinimum:MinimumViewportSize;
            bool needsViewportScroll=ClientSize.Width<minimum.Width||ClientSize.Height<minimum.Height;
            Rectangle bounds;
            if(needsViewportScroll)
            {
                AutoScroll=true;
                if(AutoScrollMinSize!=minimum)AutoScrollMinSize=minimum;
                bounds=new Rectangle(AutoScrollPosition,minimum);
            }
            else
            {
                if(AutoScrollPosition!=Point.Empty)AutoScrollPosition=Point.Empty;
                AutoScroll=false;
                if(AutoScrollMinSize!=Size.Empty)AutoScrollMinSize=Size.Empty;
                bounds=new Rectangle(Point.Empty,ClientSize);
            }
            if(workspaceCanvas.Bounds!=bounds)workspaceCanvas.Bounds=bounds;
        }
        int SidebarLayoutWidth()
        {
            int minimum=0;
            foreach(Control child in side.Controls)minimum=Math.Max(minimum,child.Width+child.Margin.Horizontal+side.Padding.Horizontal);
            foreach(Control child in actions.Controls)minimum=Math.Max(minimum,child.Width+child.Margin.Horizontal+actions.Padding.Horizontal);
            // A local diagnostics scrollbar needs its own width. It must not
            // steal the numeric input / filter-button area from fixed rows.
            // Predict the scrollbar from actual visible content, rather than
            // reading VerticalScroll.Visible during its transient layout pass.
            // This reserves space before WinForms reduces the client width.
            int contentHeight=side.Padding.Vertical;
            foreach(Control child in side.Controls)if(child.Visible)contentHeight+=child.Height+child.Margin.Vertical;
            // FlowLayoutPanel consumes one additional device pixel at the
            // vertical scrollbar boundary; reserve it so 208 px setting rows
            // remain wholly inside the compact 1366x768 laptop viewport.
            bool settingsScroll=side.ClientSize.Height>0&&
                (contentHeight>side.ClientSize.Height||side.AutoScrollMinSize.Height>side.ClientSize.Height);
            if(settingsScroll)minimum+=SystemInformation.VerticalScrollBarWidth+2;
            return Math.Max(sideWidth,minimum);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            bool prior=preservingViewportScroll;preservingViewportScroll=true;
            try{base.OnLayout(e);if(layoutReady)ApplySplitLayout();}
            finally{preservingViewportScroll=prior;}
        }
        protected override Point ScrollToControl(Control activeControl)
        {return preservingViewportScroll?AutoScrollPosition:base.ScrollToControl(activeControl);}
        void ApplySplitLayout()
        {
            if(!layoutReady||resizingCharts)return;resizingCharts=true;
            try
            {
                ApplyCompactWorkspace();
                ApplyReadableWorkspaceLayout();
                UpdateMinimum();
                int leftMinimum=Math.Max(pdmap.MinimumSize.Width,spectrum.MinimumSize.Width),rightMinimum=SidebarLayoutWidth();
                if(outer.Width>=leftMinimum+rightMinimum+outer.SplitterWidth)
                { outer.Panel1MinSize=leftMinimum;outer.Panel2MinSize=rightMinimum;outer.SplitterDistance=Math.Max(leftMinimum,outer.Width-outer.SplitterWidth-rightMinimum); }
                charts.Bounds=outer.Panel1.ClientRectangle;
                if(compactWorkspace){EnsureCompactChartHosts();return;}
                if(charts.Panel1Collapsed||charts.Panel2Collapsed)return;
                int available=charts.Height-charts.SplitterWidth;
                int topMinimum=pdmap.MinimumSize.Height,bottomMinimum=spectrum.MinimumSize.Height;
                if(available>=topMinimum+bottomMinimum)
                { charts.Panel1MinSize=topMinimum;charts.Panel2MinSize=bottomMinimum;charts.SplitterDistance=Math.Max(topMinimum,Math.Min(available-bottomMinimum,(int)Math.Round(available*splitRatio))); }
            }
            finally { resizingCharts=false; }
        }
        void UpdateLanguage()
        {
            if(!layoutReady)return;uiSync=true;
            try
            {
                UpdateFilterLanguage();
                highLabel.Text=L("High-pass / kHz","高通 / kHz");lowLabel.Text=L("Low-pass / MHz","低通 / MHz");
                waveLabel.Text=L("Wavelet","小波基");levelsLabel.Text=L("Levels","分解层数");ignoreLabel.Text=L("Ignore levels","忽略层数");
                classIndicators[0].Text=L("Included","已纳入");classIndicators[1].Text=L("Unsure","不确定");classIndicators[2].Text=L("Excluded","已排除");
                classIndicators[0].AccessibleName=L("Included","已纳入");classIndicators[1].AccessibleName=L("Uncertain","不确定");classIndicators[2].AccessibleName=L("Excluded","已排除");
                statisticsEnabledBox.Text=L("Statistics filter","统计过滤");
                previousButton.Text=L("← Prev","← 上个");nextButton.Text=L("Next →","下个 →");
                acceptButton.Text=L("Accept","接受");deleteButton.Text=L("Remove","删除");reportButton.Text=L("Save analysis","保存分析");
                uncertainButton.Text=L("Uncertain","不确定");
                evaluateButton.Text=L("Load analysis","加载分析");
                rangeFilterButton.Text=L("Filter","过滤");
                evaluateButton.AccessibleName=L("Load a saved analysis for this project","加载当前项目已保存的分析");
                string[] metricNames=L(new[]{"Confidence","Position","Charge","Voltage","Phase"},new[]{"确信度","位置","放电量","电压","相位"});
                for(int i=0;i<metricNames.Length;i++){metricLabels[i].Text=metricNames[i];metricValues[i].AccessibleName=metricNames[i];}
                acceptButton.AccessibleName=L("Accept current PDmap","接受当前PDmap");
                deleteButton.AccessibleName=L("Remove current PDmap from accepted results","取消当前PDmap接受状态");
                reportButton.AccessibleName=L("Save the analysis and report attachments","保存分析与报告附件");
                deleteButton.AccessibleDescription=L("Remove the current PDmap from accepted results; keep source data.","取消当前PDmap接受状态，不删除原始数据。");
                uncertainButton.AccessibleName=L("Mark current pulse uncertain","将当前脉冲标记为不确定");
                uncertainButton.AccessibleDescription=L("Keep the source waveform for later review; exclude it from accepted discharge statistics.","保留原始波形供后续复核，不计入已接受的放电统计。");
                reportButton.AccessibleDescription=L("Name this analysis and save a new time-stamped folder under the project's Analysis report directory.","为本次分析命名，并保存到项目 Analysis report 下的新时间文件夹。");
                RefreshAiLearningUi();
                UpdateReviewKeyboardHints();
                UpdateWorkspaceLanguage();
                if(unifiedModuleTitle!=null)unifiedModuleTitle.Text=L("Partial discharge analysis","局部放电分析");
                RefreshDisplayAdviceUi();
                FitLanguageLabels();
                ApplyReadableWorkspaceLayout();
                operationStatus="";RefreshBottomStatus();
            }
            finally { uiSync=false; }
        }
        void RefreshReviewUi()
        {
            if(!layoutReady)return;uiSync=true;
            try
            {
                if(reviewBandHasSelection!=(selected>=0)||!reviewBandSampleRate.Equals(CurrentReviewSampleRate))SyncFilterUi();
                bool has=selected>=0;pdmap.Visible=has&&!locationOverviewMode;emptyWave.Visible=!has&&!locationOverviewMode;
                emptyWave.Text=SourceRowCount==0?(L("No data loaded","尚未加载数据")):(L("No PDmap remains after filtering","过滤后无可显示的 PDmap"));
                acceptButton.Enabled=CanAccept;deleteButton.Enabled=CanDelete;uncertainButton.Enabled=CanMarkUncertain;RefreshAnalysisArchiveActions();
                rangeFilterButton.Enabled=!IsAnalysisRunning&&SourceRowCount>0;
                RefreshEvaluationUi();
                RefreshAiLearningUi();
                int previous,next;GetAdjacentRows(out previous,out next);previousButton.Enabled=!IsAnalysisRunning&&previous>=0;nextButton.Enabled=!IsAnalysisRunning&&next>=0;
                double confidence=CurrentConfidence;
                bool coincidentPending=ReviewCoincidentPending();
                bool beforeIncidentPending=has&&pdmap.ConfidenceStatusCode=="ReflectionBeforeIncidentReviewOnly";
                bool missingReflection=has&&!pdmap.HasSelectedReflection;
                bool pendingConfidence=coincidentPending||beforeIncidentPending||missingReflection||CurrentAcquisitionReferenceOnly;
                int category=pendingConfidence||!Finite(confidence)||confidence<0||confidence>1?-1:confidence*100<spectrum.ClassificationLowThresholdPercent?2:confidence*100<spectrum.ClassificationHighThresholdPercent?1:0;
                // Review decisions, not a numerical confidence threshold, own these
                // badges. Preserve the measured confidence separately below.
                int decisionCategory=!has?-1:CurrentAccepted?0:removed[selected]?2:CurrentUncertain?1:-1;
                for(int i=0;i<3;i++){classIndicators[i].BackColor=i==decisionCategory?ReviewTheme.Active:ReviewTheme.Disabled;classIndicators[i].ForeColor=i==decisionCategory?Color.White:ReviewTheme.Ink;}
                classIndicators[0].Text=CurrentAcceptanceIndicatorText();
                classIndicators[0].AccessibleDescription=CurrentDecisionExplanation();
                if(analysisDiagnosticsTip!=null)analysisDiagnosticsTip.SetToolTip(classIndicators[0],CurrentDecisionExplanation());
                metricLabels[0].Text=CurrentScoreLabel(pendingConfidence);
                metricValues[0].AccessibleName=metricLabels[0].Text;
                metricValues[0].Text=pendingConfidence?(L("Pending","待判断")):MetricNumber(category<0?double.NaN:confidence*100);
                metricUnits[0].Text=pendingConfidence?"":"%";metricUnits[0].Visible=!pendingConfidence;
                int[] columns={13,15,2,3};
                for(int i=0;i<columns.Length;i++)metricValues[i+1].Text=MetricNumber(has?working[selected,columns[i]]:double.NaN);
                metricValues[1].Text=MetricNumber(PresentedLocationM);
                if(CurrentAcquisitionReferenceOnly)metricValues[1].Text=L("Reference","仅参考");
                if(missingReflection)metricValues[1].Text=L("Unlocated","未定位");
                metricUnits[1].Visible=!missingReflection&&!CurrentAcquisitionReferenceOnly;
                acceptButton.AccessibleDescription=missingReflection?L("Select a reflection in the waveform before accepting a location.","请先在波形中选取反射波，再接受定位结果。"):has&&!ManualLocationScoreEligible?ManualLocationScoreReason():"";
                if(analysisDiagnosticsTip!=null)analysisDiagnosticsTip.SetToolTip(acceptButton,acceptButton.AccessibleDescription);
                metricValues[0].AccessibleDescription=connectingGeometryStatus=="GeometryChangedConfidenceNeedsReview"?(L("Geometry changed; reanalyse or review confidence.","几何参数已变更，请重新分析/复核确信度。")):"";
                bool missingSeventhContext=has&&pdmap.ConfidenceStatusCode=="SeventhMeasurementContextUnavailable";
                if(missingSeventhContext)metricValues[0].AccessibleDescription=ManualConfidenceExplanation();
                if(coincidentPending)metricValues[0].AccessibleDescription=L("Coincident cursors show self-correlation, not independent reflection evidence. Assess calibration and independent events at the same location.","入射与反射游标重合时，自相关不代表独立反射证据；需结合校准与同位置的独立放电事件判断。");
                else if(beforeIncidentPending)metricValues[0].AccessibleDescription=L("The selected reflection precedes the incident within the observation tolerance. It is not an independent echo; review the pulse selection and calibration.","所选反射位于入射之前，仅在观察容差内保留；不能作为独立反射证据，请结合脉冲选择与校准复核。");
                if(CurrentIsEMatchingScore)metricValues[0].AccessibleDescription+=(metricValues[0].AccessibleDescription.Length==0?"":Environment.NewLine)+CurrentScoreExplanation(pendingConfidence)+L("; matching ratio, not accuracy or physical confirmation.","；匹配比值评分，不是准确率或物理确认。");
                UpdateConnectingGeometryTip();
                statisticsEnabledBox.Checked=locationFilterEnabled;statisticsSlider.Value=(int)Math.Round(locationFilterStrength);
                strengthLabel.Text=(L("Filter strength: ","过滤强度："))+locationFilterStrength.ToString("0.#",CultureInfo.InvariantCulture)+(locationFilterEnabled?"":" / "+(L("off","关闭")));
                SyncLocationFilterSelection();
                SyncAHistoryAuxiliaryUi();
                statusLabel.Text=locationFilterEnabled&&locationFilterError.Length>0?locationFilterError:LastError;
                int unknownB=locationFilterBUnknownRows;
                if(!locationFilterEnabled&&displayRecommendation!=null&&displayRecommendation.AlgorithmId=="B")unknownB=displayRecommendation.UnknownRows;
                if(string.IsNullOrEmpty(statusLabel.Text)&&locationFilterAlgorithmId=="B"&&unknownB>0)
                    statusLabel.Text=L("Filter B: unknown boundary candidates require review: ","过滤B：边界未知候选待复核：")+unknownB;
                if(string.IsNullOrEmpty(statusLabel.Text)&&missingSeventhContext)
                    statusLabel.Text=ManualConfidenceExplanation();
                if(string.IsNullOrEmpty(statusLabel.Text)&&beforeIncidentPending)
                    statusLabel.Text=L("Reflection precedes incident; correlation is reference only.","反射位于入射之前；相关度仅供参考，不作为定位置信度。");
                if(string.IsNullOrEmpty(statusLabel.Text)&&has&&pdmap.ConfidenceStatusCode=="SeventhEvidenceConflict")
                    statusLabel.Text=L("Reflection evidence conflicts; manual review required.","反射波证据不一致，请人工复核。");
                if(string.IsNullOrEmpty(statusLabel.Text)&&!reviewBandAvailable)
                    statusLabel.Text=L("Sample rate is invalid or too low for band-pass.","采样率无效或过低，带通不可用。");
                if(string.IsNullOrEmpty(statusLabel.Text)&&pdmap.ShowFilteredCurve&&pdmap.FilterStatus.IndexOf("bypass:",StringComparison.OrdinalIgnoreCase)>=0)
                    statusLabel.Text=L("Filter failed; original curve used.","滤波失败，已使用原始曲线。");
                // B's unresolved boundary/parameter state must be visible, while
                // the legacy extra status/file-name line remains hidden.
                statusLabel.Visible=locationFilterAlgorithmId=="B"&&(unknownB>0||locationFilterError.Length>0);
                if(statusLabel.Visible)statusLabel.Text=locationFilterError.Length>0?L("B: check parameters","B：请检查参数"):L("B: review unknown ","B：未知待复核 ")+unknownB;
                if(CurrentAcquisitionReferenceOnly) {
                    statusLabel.Visible=true;statusLabel.Text=L("Overrange · reference only","超量程 · 仅供参考");
                    statusLabel.AccessibleDescription=AcquisitionRiskText();
                    acceptButton.AccessibleDescription=metricValues[0].AccessibleDescription=AcquisitionRiskText();
                    if(analysisDiagnosticsTip!=null)analysisDiagnosticsTip.SetToolTip(statusLabel,AcquisitionRiskText());
                }
                UpdateLocationFilterBTip(unknownB);
                RefreshWorkspaceUi();
                ApplyCapacitorReviewUi();
                RefreshBottomStatus();
            }
            finally { uiSync=false; }
        }
        [Category("Filtering")] public int WaveformFilterMode
        {
            get { return !pdmap.ShowFilteredCurve?0:(int)pdmap.FilterParameters[0]+1; }
            set { if(value<0||value>2)throw new ArgumentOutOfRangeException("WaveformFilterMode");var p=pdmap.FilterParameters;if(value>0)p[0]=value-1;SetReviewFilters(p);pdmap.ShowFilteredCurve=value!=0&&(value!=1||reviewBandAvailable);SyncFilterUi();RefreshReviewUi(); }
        }
        [Browsable(false)] public double[] FilterParameters
        {
            get { return pdmap.FilterParameters; }
            set { if(value!=null)SetReviewFilters(value);SyncFilterUi();RefreshReviewUi(); }
        }
        void SyncFilterUi()
        {
            if(!layoutReady)return;bool previousSync=uiSync;uiSync=true;
            try { var p=SetReviewFilters(pdmap.FilterParameters);SyncReviewBandRange();SetFilterSelection(WaveformFilterMode);SetNumeric(highPass,p[1]/1000);SetNumeric(lowPass,p[2]/1000000);wavelet.SelectedIndex=p[3]==2?0:p[3]==8?2:1;SetNumeric(levels,p[4]);SetNumeric(ignore,p[5]);SyncFilterSliders();UpdateFilterEnabled(); }
            finally { uiSync=previousSync; }
        }
        static void SetNumeric(NumericUpDown control,double value) { if(Finite(value))control.Value=(decimal)Math.Max((double)control.Minimum,Math.Min((double)control.Maximum,value)); }
        void ApplyFilterFromUi(int endpoint=0)
        {
            if(uiSync)return;
            int mode=SelectedFilterMode;
            var p=pdmap.FilterParameters;
            if(mode==1){p[0]=0;p[1]=(double)highPass.Value*1000;p[2]=(double)lowPass.Value*1000000;}
            else if(mode==2){p[0]=1;p[3]=wavelet.SelectedIndex==0?2.0:wavelet.SelectedIndex==2?8.0:4.0;p[4]=(double)levels.Value;p[5]=(double)ignore.Value;}
            SetReviewFilters(p,endpoint);
            pdmap.ShowFilteredCurve=mode>0&&(mode!=1||reviewBandAvailable);SyncFilterUi();RefreshReviewUi();
        }
        sealed class GripSplitContainer : SplitContainer
        {
            public GripSplitContainer() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);BackColor=Color.White;Panel1.BackColor=Color.White;Panel2.BackColor=Color.White; }
            protected override void OnPaint(PaintEventArgs e)
            { base.OnPaint(e);var r=SplitterRectangle;using(var divider=new Pen(ReviewTheme.Border,1))
                if(Orientation==Orientation.Horizontal)e.Graphics.DrawLine(divider,r.Left,r.Top+r.Height/2,r.Right,r.Top+r.Height/2);
                else e.Graphics.DrawLine(divider,r.Left+r.Width/2,r.Top,r.Left+r.Width/2,r.Bottom);
              if(IsSplitterFixed)return;using(var pen=new Pen(ReviewTheme.SecondaryInk,2))
                if(Orientation==Orientation.Horizontal)e.Graphics.DrawLine(pen,r.Left+r.Width/2-15,r.Top+r.Height/2,r.Left+r.Width/2+15,r.Top+r.Height/2);
                else e.Graphics.DrawLine(pen,r.Left+r.Width/2,r.Top+r.Height/2-15,r.Left+r.Width/2,r.Top+r.Height/2+15); }
        }
    }
}
