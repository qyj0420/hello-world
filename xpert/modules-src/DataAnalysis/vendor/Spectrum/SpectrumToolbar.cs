using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        const int ToolbarPadding = 6, ToolbarButtonGap = 3, ToolbarRowGap = 4;
        // UI order is distinct from the stable 0..12 mode API. Modes 7 and 8
        // remain callable, but no native button is created for either mode.
        static readonly int[] StandardToolbarModes = { 0, 1, 2, 3, 4, 5, 6, 9, 10, 11, 12 };
        static readonly int[] CompetitionToolbarModes = { 11, 2, 3, 7, 8, 12 };
        int[] ToolbarModeIndices { get { return competitionProfile&&!completeReadOnlyModes?CompetitionToolbarModes:StandardToolbarModes; } }
        Panel spectrumToolbar, modeViewport, phaseToolbar;
        Button[] modeButtons;
        CheckBox[] phaseButtons;
        bool syncingToolbar, layingOutToolbar;
        Font measuredToolbarFont;
        bool measuredToolbarEnglish;
        readonly int[] modeButtonWidths = new int[ModeNames.Length];
        int modeStripWidth, phaseStripWidth, phaseButtonWidth, toolbarButtonHeight;
        bool compactHostLayout;
        bool phaseSelectorVisible=true;
        ComboBox compactModeChoice;
        Label compactModeCaption;
        ComboBox compactPhaseChoice;
        [DefaultValue(true)]
        public bool PhaseSelectorVisible
        {
            get{return phaseSelectorVisible;}
            set{if(phaseSelectorVisible==value)return;phaseSelectorVisible=value;LayoutSpectrumToolbar();Invalidate();}
        }
        public bool CompactHostLayout
        {
            get{return compactHostLayout;}
            set{if(compactHostLayout==value)return;compactHostLayout=value;LayoutSpectrumToolbar();Invalidate();}
        }

        // Native children keep keyboard activation, focus cues and accessibility
        // inside the host. Keep the complete mode row visible; move the complete
        // phase group below it instead of clipping buttons or adding a scrollbar.
        void InitializeToolbar()
        {
            if (spectrumToolbar != null) return;
            spectrumToolbar = new Panel { Name = "SpectrumToolbar", TabIndex = 0,
                BackColor = competitionProfile?Color.FromArgb(245,248,251):HVX.PdReview.ReviewTheme.SurfaceAlt };
            modeViewport = new Panel { Name = "SpectrumModeViewport", AutoScroll = false,
                TabIndex = 0, BackColor = spectrumToolbar.BackColor };
            phaseToolbar = new Panel { Name = "SpectrumPhaseToolbar", TabIndex = 1,
                BackColor = spectrumToolbar.BackColor };
            spectrumToolbar.Controls.Add(modeViewport);
            spectrumToolbar.Controls.Add(phaseToolbar);
            Controls.Add(spectrumToolbar);
            compactModeChoice=new ComboBox{Name="SpectrumCompactMode",DropDownStyle=ComboBoxStyle.DropDownList,Visible=false,Font=Font,IntegralHeight=false,MaxDropDownItems=11};
            foreach(int index in ToolbarModeIndices)compactModeChoice.Items.Add(ModeCaption(index));
            compactModeChoice.SelectedIndexChanged+=(s,e)=>{if(syncingToolbar||compactModeChoice.SelectedIndex<0)return;DisplayModeIndex=ToolbarModeIndices[compactModeChoice.SelectedIndex];SyncToolbar();NotifyHost(ViewChanged);};
            spectrumToolbar.Controls.Add(compactModeChoice);
            compactModeCaption=new Label{Name="SpectrumCompactModeCaption",AutoSize=false,TextAlign=ContentAlignment.MiddleLeft,Visible=false,Font=Font};
            spectrumToolbar.Controls.Add(compactModeCaption);
            // AI recognition window: one phase group only (no A/B/C multi-toggle strip).
            compactPhaseChoice=new ComboBox{Name="SpectrumPhaseChoice",DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,IntegralHeight=false};
            for(int phase=1;phase<=3;phase++)compactPhaseChoice.Items.Add("");
            compactPhaseChoice.SelectedIndexChanged+=(s,e)=>{
                if(syncingToolbar||!completeReadOnlyModes||compactPhaseChoice.SelectedIndex<0)return;
                int phase=compactPhaseChoice.SelectedIndex+1;
                var next=new bool[3]; next[phase-1]=true;
                CancelGesture(); phaseFilter=phase; visible=next;
                states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true);
                SyncToolbar(); NotifyHost(ViewChanged);
            };
            spectrumToolbar.Controls.Add(compactPhaseChoice);
            modeButtons = new Button[ModeNames.Length];
            for (int tab = 0; tab < ToolbarModeIndices.Length; tab++)
            {
                int i = ToolbarModeIndices[tab];
                int index = i;
                var button = new HVX.PdReview.RoundedReviewButton { Name = "SpectrumMode" + i, Text = ModeCaption(i),
                    TabIndex = tab, Padding = Padding.Empty, Margin = Padding.Empty,
                    FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                    AccessibleRole = AccessibleRole.PushButton };
                button.FlatAppearance.BorderSize = 1;
                button.Click += delegate { DisplayModeIndex = index; SyncToolbar(); NotifyHost(ViewChanged); };
                modeButtons[i] = button; modeViewport.Controls.Add(button);
            }
            phaseButtons = new CheckBox[3];
            for (int i = 0; i < phaseButtons.Length; i++)
            {
                int index = i;
                var toggle = new HVX.PdReview.RoundedReviewToggle { Name = "SpectrumPhase" + (i + 1),
                    Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                    TabIndex = i, Padding = Padding.Empty, Margin = Padding.Empty,
                    AccessibleRole = AccessibleRole.CheckButton };
                toggle.FlatAppearance.BorderSize = 1;
                toggle.CheckedChanged += delegate
                {
                    if (syncingToolbar) return;
                    // AI defect (completeReadOnlyModes) uses a single-phase combo below;
                    // ordinary analysis keeps multi-phase toggles.
                    if (completeReadOnlyModes) return;
                    var next = EffectiveToolbarPhaseVisibility(); next[index] = toggle.Checked;
                    ApplyToolbarPhaseVisibility(next);
                };
                phaseButtons[i] = toggle; phaseToolbar.Controls.Add(toggle);
            }
            Resize += delegate { LayoutSpectrumToolbar(); };
            FontChanged += delegate { SyncToolbar(); };
            InitializeCameraToolbar(); InitializeColorModeButton();
            if(!competitionProfile)InitializeClassificationControl();
            InitializeChartExport();
            LayoutSpectrumToolbar(); SyncToolbar();
        }

        // This does not parse files, clone the input matrix or rebuild plot data.
        void SyncToolbar()
        {
            if (spectrumToolbar == null || spectrumToolbar.IsDisposed) return;
            syncingToolbar = true;
            try
            {
                spectrumToolbar.Font = Font;
                compactModeCaption.Font=Font;compactModeCaption.Text=L("Chart","谱图");
                compactModeChoice.AccessibleName=L("Chart type","谱图类型");
                compactModeChoice.Font=Font;compactModeChoice.SelectedIndex=Array.IndexOf(ToolbarModeIndices,mode);
                for (int i = 0; i < modeButtons.Length; i++)
                {
                    var button = modeButtons[i];
                    if (button == null) continue;
                    bool selected = i == mode;
                    button.BackColor = selected ? (competitionProfile?Color.FromArgb(0,123,155):HVX.PdReview.ReviewTheme.Active) : HVX.PdReview.ReviewTheme.Surface;
                    button.ForeColor = selected ? Color.White : HVX.PdReview.ReviewTheme.Ink;
                    button.FlatAppearance.BorderColor = selected ? HVX.PdReview.ReviewTheme.Active : HVX.PdReview.ReviewTheme.Border;
                    button.AccessibleName = (L("Chart ","谱图 ")) + ModeCaption(i);
                    button.AccessibleDescription = selected ? (L("Selected","已选择")) : "";
                }
                var effective = EffectiveToolbarPhaseVisibility();
                for (int i = 0; i < phaseButtons.Length; i++)
                {
                    var button = phaseButtons[i]; button.Text = english ? "L" + (i + 1) : new[] { "A相", "B相", "C相" }[i];
                    if(competitionProfile&&effective[i])button.Text="✓ "+button.Text;
                    button.Checked = effective[i];
                    Color phaseColor = PhaseButtonColor(i);
                    button.BackColor = effective[i] ? phaseColor : HVX.PdReview.ReviewTheme.Surface;
                    button.ForeColor = effective[i] ? (i == 0 ? HVX.PdReview.ReviewTheme.Ink : Color.White) : (i == 0 ? PhaseColors[1] : phaseColor);
                    button.FlatAppearance.BorderColor = PhaseColors[i + 1];
                    button.FlatAppearance.CheckedBackColor = phaseColor;
                    button.FlatAppearance.MouseOverBackColor = effective[i] ? phaseColor : ControlPaint.LightLight(phaseColor);
                    button.FlatAppearance.MouseDownBackColor = effective[i] ? ControlPaint.Dark(phaseColor) : ControlPaint.LightLight(phaseColor);
                    if(competitionProfile)
                    {
                        button.BackColor=effective[i]?ControlPaint.LightLight(phaseColor):Color.White;
                        button.ForeColor=effective[i]?PhaseColors[i+1]:HVX.PdReview.ReviewTheme.Muted;
                        button.FlatAppearance.CheckedBackColor=button.BackColor;
                    }
                    button.AccessibleName = (L("Show ","显示 ")) + button.Text;
                    button.AccessibleDescription = effective[i]?L("Phase shown","该相已显示"):L("Phase hidden","该相已隐藏");
                    // AI recognition: hide the multi-phase toggle strip entirely.
                    button.Visible = !completeReadOnlyModes;
                }
                if (completeReadOnlyModes && compactPhaseChoice != null)
                {
                    // AI recognition: one phase combo; multi-toggle strip stays hidden.
                    for (int phase = 1; phase <= 3; phase++)
                        compactPhaseChoice.Items[phase - 1] = english ? ("L" + phase) : new[] { "A相", "B相", "C相" }[phase - 1];
                    int selectedPhase = phaseFilter >= 1 && phaseFilter <= 3
                        ? phaseFilter
                        : (effective[0] ? 1 : effective[1] ? 2 : effective[2] ? 3 : 1);
                    if (compactPhaseChoice.SelectedIndex != selectedPhase - 1)
                        compactPhaseChoice.SelectedIndex = selectedPhase - 1;
                    compactPhaseChoice.Visible = true;
                    phaseToolbar.Visible = false;
                }
                else if (compactPhaseChoice != null)
                {
                    compactPhaseChoice.Visible = false;
                    phaseToolbar.Visible = true;
                }
                SyncCameraToolbar(); SyncColorModeButton(); SyncClassificationControl(); LayoutSpectrumToolbar();
            }
            finally { syncingToolbar = false; }
        }

        // Preserve the established A/yellow, B/green, C/red phase identity;
        // filled versus outlined states remain distinguishable without color.
        static Color PhaseButtonColor(int index)
        { return index == 0 ? Color.FromArgb(255, 211, 58) : index == 1 ? Color.FromArgb(34, 119, 72) : Color.FromArgb(190, 52, 44); }

        void LayoutSpectrumToolbar()
        {
            if (spectrumToolbar == null || spectrumToolbar.IsDisposed || layingOutToolbar) return;
            layingOutToolbar = true;
            try
            {
                bool changed = EnsureToolbarMetrics();
                // MinimumSize is the actual complete mode row, including margins.
                // The guard prevents the resize event caused by clamping from
                // reentering layout; dimensions are read again after clamping.
                // On compact notebook hosts the mode strip becomes two rows.
                // Never enlarge the whole analysis canvas just to preserve a
                // single toolbar row or hide modes behind a horizontal scroll.
                int minimumWidth = compactHostLayout&&completeReadOnlyModes
                    ?Math.Max(300,(phaseSelectorVisible?phaseStripWidth:0)+ToolbarPadding*2+78)
                    :Math.Max(compactHostLayout?360:520, modeButtonWidths.Max() + ToolbarPadding * 2);
                // Keep room for the tallest supported classification header,
                // a usable plot, axis titles and the native interaction row.
                // This minimum stays stable when the checkbox or mode changes.
                // Compact height keeps all controls/labels and a readable plot;
                // do not impose the former 440 px panel minimum on the splitter.
                int minimumPlotHeight = Math.Max(40, Font.Height * 2);
                // A measured large-font or wrapped warning header may exceed the
                // normal stable reserve. Derive a finite minimum from width/text,
                // never from the previous control height (so it can shrink again).
                int measuredWidth=Math.Max(minimumWidth,ClientSize.Width);
                int headerMinimum = HeaderLayout(measuredWidth).PlotTop;
                int contentMinimum = headerMinimum + minimumPlotHeight + 80;
                int minimumHeight = ToolbarHeightForWidth(measuredWidth) + contentMinimum;
                var minimum = new Size(minimumWidth, minimumHeight);
                if (MinimumSize != minimum) MinimumSize = minimum;
                int width = Math.Max(0, ClientSize.Width), height = ToolbarHeightForWidth(width);
                var bounds = new Rectangle(0, Math.Max(0, ClientSize.Height - height), width, height);
                changed |= spectrumToolbar.Bounds != bounds;
                spectrumToolbar.Bounds = bounds;
                int modeRows=ModeRowsForWidth(width);
                modeViewport.Bounds = new Rectangle(ToolbarPadding, ToolbarPadding,
                    modeRows==1?modeStripWidth:Math.Max(0,width-ToolbarPadding*2),
                    modeRows*toolbarButtonHeight+(modeRows-1)*ToolbarRowGap);
                int left = 0,row=0;
                foreach (int index in ToolbarModeIndices)
                {
                    if(left>0&&left+modeButtonWidths[index]>modeViewport.Width){left=0;row++;}
                    modeButtons[index].Bounds = new Rectangle(left,row*(toolbarButtonHeight+ToolbarRowGap),modeButtonWidths[index],toolbarButtonHeight);
                    left += modeButtonWidths[index] + ToolbarButtonGap;
                }
                int phaseTop = ToolbarPadding + (ToolbarRowsForWidth(width) - 1) * (toolbarButtonHeight + ToolbarRowGap);
                modeViewport.Visible=!compactHostLayout;compactModeChoice.Visible=compactHostLayout;compactModeCaption.Visible=compactHostLayout;
                if (!phaseSelectorVisible)
                {
                    phaseToolbar.Visible=false;
                    if(compactPhaseChoice!=null)compactPhaseChoice.Visible=false;
                    if(compactHostLayout)LayoutCompactModeChoice(width-ToolbarPadding*2);
                }
                else if (completeReadOnlyModes && compactPhaseChoice != null)
                {
                    // AI recognition: one phase combo only — no A/B/C multi-toggle strip.
                    phaseToolbar.Visible = false;
                    foreach (var button in phaseButtons) button.Visible = false;
                    compactPhaseChoice.Visible = true;
                    int phaseW = Math.Max(72, phaseButtonWidth + 24);
                    if (compactHostLayout)
                    {
                        LayoutCompactModeChoice(width-ToolbarPadding*2-phaseW-8);
                        compactPhaseChoice.SetBounds(compactModeChoice.Right + 8, ToolbarPadding, phaseW, Math.Max(compactModeChoice.Height, toolbarButtonHeight));
                    }
                    else
                    {
                        compactPhaseChoice.SetBounds(Math.Max(ToolbarPadding, width - ToolbarPadding - phaseW), phaseTop, phaseW, toolbarButtonHeight);
                    }
                }
                else
                {
                    if (compactPhaseChoice != null) compactPhaseChoice.Visible = false;
                    phaseToolbar.Visible = true;
                    phaseToolbar.Bounds = new Rectangle(Math.Max(ToolbarPadding, width - ToolbarPadding - phaseStripWidth), phaseTop, phaseStripWidth, toolbarButtonHeight);
                    for (int i = 0; i < phaseButtons.Length; i++)
                        phaseButtons[i].Bounds = new Rectangle(i * (phaseButtonWidth + ToolbarButtonGap), 0, phaseButtonWidth, toolbarButtonHeight);
                    if (compactHostLayout)
                    {
                        phaseToolbar.Top = ToolbarPadding;
                        LayoutCompactModeChoice(phaseToolbar.Left-ToolbarPadding-8);
                    }
                }
                spectrumToolbar.BringToFront();
                LayoutCameraToolbar(); LayoutColorModeButton(); LayoutClassificationControl(); LayoutChartExport();LayoutLearningActions();
                if (changed) { DropDrawingCaches(); Invalidate(); }
            }
            finally { layingOutToolbar = false; }
        }

        void LayoutCompactModeChoice(int availableWidth)
        {
            // A chart selector is an action-sized control, not a full-width
            // blank-looking field. Measure actual text so large fonts and DPI
            // still leave the complete selected mode and arrow visible.
            int gap=8,captionWidth=MeasureToolbarText(compactModeCaption.Text)+4;
            int choiceWidth=ToolbarModeIndices.Max(index=>MeasureToolbarText(ModeCaption(index)))+42;
            choiceWidth=Math.Min(Math.Max(70,availableWidth-captionWidth-gap),Math.Max(108,choiceWidth));
            int height=compactModeChoice.PreferredHeight;
            compactModeCaption.SetBounds(ToolbarPadding,ToolbarPadding,captionWidth,height);
            compactModeChoice.SetBounds(compactModeCaption.Right+gap,ToolbarPadding,choiceWidth,height);
            compactModeChoice.DropDownWidth=choiceWidth;
            compactModeChoice.DropDownHeight=Math.Min(380,Math.Max(120,(Font.Height+8)*ToolbarModeIndices.Length));
        }

        bool EnsureToolbarMetrics()
        {
            if (measuredToolbarFont != null && measuredToolbarFont.Equals(Font) && measuredToolbarEnglish == english) return false;
            measuredToolbarFont = Font; measuredToolbarEnglish = english;
            modeStripWidth = -ToolbarButtonGap;
            foreach (int index in ToolbarModeIndices)
            {
                modeButtonWidths[index] = Math.Max(competitionProfile?78:38, MeasureToolbarText(ModeCaption(index)) + (competitionProfile?24:10));
                modeStripWidth += modeButtonWidths[index] + ToolbarButtonGap;
            }
            if(competitionProfile)
            {
                int uniform=ToolbarModeIndices.Max(index=>modeButtonWidths[index]);modeStripWidth=-ToolbarButtonGap;
                foreach(int index in ToolbarModeIndices){modeButtonWidths[index]=uniform;modeStripWidth+=uniform+ToolbarButtonGap;}
            }
            phaseButtonWidth = 36;
            for (int i = 0; i < 3; i++) phaseButtonWidth = Math.Max(phaseButtonWidth, MeasureToolbarText((competitionProfile?"✓ ":"")+(english ? "L" + (i + 1) : new[] { "A相", "B相", "C相" }[i])) + 14);
            phaseStripWidth = phaseButtonWidth * 3 + ToolbarButtonGap * 2;
            toolbarButtonHeight = Math.Max(competitionProfile?34:25, Font.Height + (competitionProfile?14:10));
            return true;
        }
        int MeasureToolbarText(string text)
        { return TextRenderer.MeasureText(text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width; }
        int ModeRowsForWidth(int width)
        { return width >= modeStripWidth + ToolbarPadding * 2 ? 1 : 2; }
        int ToolbarRowsForWidth(int width)
        { return compactHostLayout?1:ModeRowsForWidth(width)==2?(phaseSelectorVisible?3:2):!phaseSelectorVisible||width >= modeStripWidth + phaseStripWidth + ToolbarButtonGap + ToolbarPadding * 2 ? 1 : 2; }
        internal int ToolbarHeightForWidth(int width)
        { EnsureToolbarMetrics(); return width > 0 ? ToolbarPadding * 2 + ToolbarRowsForWidth(width) * toolbarButtonHeight + (ToolbarRowsForWidth(width) - 1) * ToolbarRowGap : 0; }
        [Browsable(false)] public int MinimumToolbarWidth { get { EnsureToolbarMetrics(); return Math.Max(520,modeButtonWidths.Max()+ToolbarPadding*2); } }
        [Browsable(false)] public int ToolbarRowCount { get { EnsureToolbarMetrics(); return ToolbarRowsForWidth(ClientSize.Width); } }

        bool[] EffectiveToolbarPhaseVisibility()
        {
            // Single-phase input filters can mask stored visibility flags.
            // Buttons reflect effective state, and combinations start from it.
            return Enumerable.Range(0, 3).Select(i => visible[i] &&
                (phaseFilter == -1 || phaseFilter == i + 1)).ToArray();
        }

        void ApplyToolbarPhaseVisibility(bool[] next)
        {
            if (phaseFilter != -1 || !visible.SequenceEqual(next))
            {
                // Commit the combined filter in one pass, avoiding an intermediate
                // single-phase rebuild and retaining the existing matrix snapshot.
                CancelGesture(); phaseFilter = -1; visible = (bool[])next.Clone();
                states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true);
                SyncToolbar(); NotifyHost(ViewChanged);
            }
            else SyncToolbar();
        }

        [Browsable(false)] public Rectangle ToolbarBounds { get { return spectrumToolbar == null ? Rectangle.Empty : spectrumToolbar.Bounds; } }
        [Browsable(false)] public int ModeButtonCount { get { return ToolbarModeIndices.Length; } }
        /// <summary>Returns UI modes in display order, retaining their stable DisplayModeIndex values.</summary>
        public int[] GetVisibleModeIndices() { return (int[])ToolbarModeIndices.Clone(); }
        public string GetModeButtonText(int index) { CheckToolbarMode(index); return ModeCaption(index); }
        public string GetPhaseButtonText(int phase) { CheckToolbarPhase(phase); return phaseButtons[phase - 1].Text; }
        public bool GetPhaseButtonChecked(int phase) { CheckToolbarPhase(phase); return phaseButtons[phase - 1].Checked; }
        public Rectangle GetModeButtonBounds(int index) { CheckToolbarMode(index); return modeButtons[index] == null ? Rectangle.Empty : ToolbarChildBounds(modeButtons[index], modeViewport); }
        public Rectangle GetPhaseButtonBounds(int phase) { CheckToolbarPhase(phase); return phaseSelectorVisible?ToolbarChildBounds(phaseButtons[phase - 1], phaseToolbar):Rectangle.Empty; }
        // Legacy automation entry points remain binary-compatible; no ALL UI is created.
        public Rectangle GetAllPhasesButtonBounds() { return Rectangle.Empty; }
        public void ScrollModeButtonIntoView(int index) { CheckToolbarMode(index); LayoutSpectrumToolbar(); }
        // Keep old automation calls valid without recreating hidden UI buttons.
        public void PerformModeButtonClick(int index)
        {
            CheckToolbarMode(index);
            if (modeButtons[index] == null) { DisplayModeIndex = index; SyncToolbar(); NotifyHost(ViewChanged); return; }
            modeButtons[index].PerformClick();
        }
        public void PerformPhaseButtonClick(int phase)
        {
            CheckToolbarPhase(phase);
            if (completeReadOnlyModes)
            {
                var next = new bool[3]; next[phase - 1] = true;
                CancelGesture(); phaseFilter = phase; visible = next;
                states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true);
                SyncToolbar(); NotifyHost(ViewChanged);
                return;
            }
            phaseButtons[phase - 1].Checked = !phaseButtons[phase - 1].Checked;
        }
        public void PerformAllPhasesButtonClick() { ApplyToolbarPhaseVisibility(new[] { true, true, true }); }
        void CheckToolbarMode(int index) { if (index < 0 || index >= ModeNames.Length) throw new ArgumentOutOfRangeException("index"); InitializeToolbar(); }
        void CheckToolbarPhase(int phase) { if (phase < 1 || phase > 3) throw new ArgumentOutOfRangeException("phase"); InitializeToolbar(); }
        Rectangle ToolbarChildBounds(Control child, Control viewport)
        {
            var clipped = Rectangle.Intersect(child.Bounds, viewport.ClientRectangle);
            if (clipped.Width <= 0 || clipped.Height <= 0) return Rectangle.Empty;
            clipped.Offset(viewport.Left + spectrumToolbar.Left, viewport.Top + spectrumToolbar.Top);
            return clipped;
        }
    }
}
