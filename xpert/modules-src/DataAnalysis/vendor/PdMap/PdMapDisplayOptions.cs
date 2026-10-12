using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        CheckBox mixedCableCheckBox, calibrationCheckBox, dampingCheckBox, approximateJointCheckBox, filteredCheckBox;
        CheckBox[] displayOptionBoxes;
        Font displayOptionsFont;
        ToolTip displayOptionsToolTip;
        bool updatingDisplayOptions, disposingDisplayOptions;
        int displayOptionsLayoutWidth = -1, displayOptionsLayoutHeight = -1;
        bool displayOptionsLayoutEnglish, displayOptionsLayoutAutomaticMixed;
        Rectangle displayOptionsFrameBounds;
        Button displayOptionsToggle;
        bool displayOptionsExpanded;
        bool CompactDisplayOptions {get{return ClientSize.Height<400;}}

        [Category("Appearance"), DefaultValue(false)]
        [Description("显示当前原始或滤波组的校准参考曲线。Show the calibration reference in the active raw/filtered group.")]
        public bool ShowCalibrationCurve
        {
            get { return Visible(Visible(3, false) ? 4 : 1, false) && showCalibration; }
            set { SetDisplayCurveOptions(value, ShowDampingCurve, ShowFilteredCurve); }
        }

        [Category("Appearance"), DefaultValue(false)]
        [Description("显示当前原始或滤波组的阻尼衰减参考曲线。Show the damping-decay reference in the active raw/filtered group.")]
        public bool ShowDampingCurve
        {
            get { return Visible(Visible(3, false) ? 5 : 2, false) && showAttenuation; }
            set { SetDisplayCurveOptions(ShowCalibrationCurve, value, ShowFilteredCurve); }
        }

        [Category("Appearance"), DefaultValue(false)]
        [Description("切换原始/滤波曲线组并保留校准和衰减显示选择。Switch raw/filtered groups while preserving auxiliary display choices.")]
        public bool ShowFilteredCurve
        {
            get { return Visible(3, false); }
            set { SetDisplayCurveOptions(ShowCalibrationCurve, ShowDampingCurve, value); }
        }

        void SetDisplayCurveOptions(bool calibrationVisible, bool dampingVisible, bool filtered)
        {
            bool[] flags = (bool[])curveVisibility.Clone();
            flags[0] = !filtered; flags[1] = !filtered && calibrationVisible; flags[2] = !filtered && dampingVisible;
            flags[3] = filtered; flags[4] = filtered && calibrationVisible; flags[5] = filtered && dampingVisible;
            if (calibrationVisible) showCalibration = true;
            if (dampingVisible) showAttenuation = true;
            SetCurveVisibility(flags);
        }

        void InitializeDisplayOptions()
        {
            if (displayOptionBoxes != null) return;
            displayOptionsFont = HVX.PdReview.ReviewTheme.RegularFont();
            displayOptionsToolTip = new ToolTip { ShowAlways = true };
            mixedCableCheckBox = CreateDisplayOption("MixedCableCheckBox");
            mixedCableCheckBox.Click+=(s,e)=>
            {
                if(ConnectingCableLengthM>0)ShowConnectingCableHint(mixedCableCheckBox,AutomaticMixedCableHint());
            };
            calibrationCheckBox = CreateDisplayOption("CalibrationCheckBox");
            dampingCheckBox = CreateDisplayOption("DampingCheckBox");
            approximateJointCheckBox = CreateDisplayOption("ApproximateJointCheckBox");
            filteredCheckBox = CreateDisplayOption("FilteredCheckBox");
            displayOptionBoxes = new[] { mixedCableCheckBox, calibrationCheckBox, dampingCheckBox, approximateJointCheckBox, filteredCheckBox };
            foreach (var box in displayOptionBoxes) Controls.Add(box);
            displayOptionsToggle=new HVX.PdReview.RoundedReviewButton{Name="PdMapDisplayOptionsToggle",Font=displayOptionsFont,Visible=false};
            HVX.PdReview.ReviewTheme.ButtonRole(displayOptionsToggle,false);
            displayOptionsToggle.Click+=(s,e)=>{displayOptionsExpanded=!displayOptionsExpanded;displayOptionsLayoutWidth=-1;cachedLayout=null;LayoutDisplayOptions();Invalidate();};
            Controls.Add(displayOptionsToggle);
            InitializeConnectingCableOptions();
            SizeChanged += DisplayOptionsSizeChanged;
            Invalidated += DisplayOptionsInvalidated;
            UpdateDisplayOptions();
        }

        CheckBox CreateDisplayOption(string name)
        {
            var box = new CheckBox
            {
                Name = name, AutoSize = false, AutoEllipsis = false,
                Font = displayOptionsFont, ForeColor = HVX.PdReview.ReviewTheme.Ink,
                BackColor = Color.Transparent, FlatStyle = FlatStyle.Standard,
                Margin = Padding.Empty, Padding = Padding.Empty,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                TabStop = true, Cursor = Cursors.Hand, Checked = false
            };
            box.CheckedChanged += DisplayOptionChanged;
            return box;
        }

        void DisplayOptionsSizeChanged(object sender, EventArgs e) { LayoutDisplayOptions(); }
        void DisplayOptionsInvalidated(object sender, InvalidateEventArgs e) { UpdateDisplayOptions(); }

        void DisplayOptionChanged(object sender, EventArgs e)
        {
            if (updatingDisplayOptions || disposingDisplayOptions || displayOptionBoxes == null) return;
            updatingDisplayOptions = true;
            try
            {
                if (ReferenceEquals(sender, mixedCableCheckBox))
                {
                    // Keep the automatic state readable and focusable, but never let
                    // a UI toggle bypass the lead's required piecewise geometry.
                    if(ConnectingCableLengthM>0)mixedCableCheckBox.Checked=true;
                    else MixedCableCalculation = mixedCableCheckBox.Checked;
                }
                else if (ReferenceEquals(sender, approximateJointCheckBox)) ApproximateCablePositions = approximateJointCheckBox.Checked;
                else
                {
                    // Only the eight visibility flags are replaced. Keep both auxiliary intents
                    // while transferring them to the raw/filtered group, and preserve joints/boundaries.
                    bool filtered = filteredCheckBox.Checked;
                    bool calibration = calibrationCheckBox.Checked, damping = dampingCheckBox.Checked;
                    SetDisplayCurveOptions(calibration, damping, filtered);
                }
            }
            finally { updatingDisplayOptions = false; }
            UpdateDisplayOptions();
        }

        void UpdateDisplayOptions()
        {
            if (updatingDisplayOptions || disposingDisplayOptions || displayOptionBoxes == null || IsDisposed || Disposing) return;
            updatingDisplayOptions = true;
            try
            {
                bool filtered = Visible(3, false);
                mixedCableCheckBox.Checked = MixedCableCalculation;
                mixedCableCheckBox.Enabled=true;
                mixedCableCheckBox.AutoCheck=ConnectingCableLengthM==0;
                mixedCableCheckBox.Text=DisplayOptionCaptions(false)[0];
                string mixedHint=ConnectingCableLengthM>0?AutomaticMixedCableHint():DisplayOptionCaptions(false)[0];
                mixedCableCheckBox.AccessibleDescription=mixedHint;
                displayOptionsToolTip.SetToolTip(mixedCableCheckBox,mixedHint);
                approximateJointCheckBox.Checked = ApproximateCablePositions;
                filteredCheckBox.Checked = filtered;
                calibrationCheckBox.Checked = Visible(filtered ? 4 : 1, false) && showCalibration;
                dampingCheckBox.Checked = Visible(filtered ? 5 : 2, false) && showAttenuation;
                SyncConnectingCableOptions();
                LayoutDisplayOptions();
            }
            finally { updatingDisplayOptions = false; }
        }

        string[] DisplayOptionCaptions(bool compact)
        {
            return L(compact
                ? new[] { ConnectingCableLengthM>0?"Mixed (auto)":"Mixed cable", "Cal.", "Decay", "Snap joints", "Filtered" }
                : new[] { ConnectingCableLengthM>0?"Mixed cable (auto)":"Mixed-cable location", "Calibration", "Damping decay", "Snap to joints", "Filtered waveform" },compact
                ? new[] { ConnectingCableLengthM>0?"混合（自动）":"混合电缆", "校准", "衰减", "近似接头", "滤波" }
                : new[] { ConnectingCableLengthM>0?"混合电缆（自动）":"混合电缆定位", "校准曲线", "阻尼衰减曲线", "近似接头", "滤波曲线" });
        }

        Rectangle[] DisplayOptionRectangles(int width, out int footerHeight)
        {
            int rowHeight=Math.Max(28,(displayOptionsFont??Font).Height+10),gap=8,left=16,framePadding=8;
            // Reserve the right-hand button cluster on every row. This keeps even
            // a narrow, wrapped footer free of overlap without shrinking the axis font.
            int available = Math.Max(28, width - left - framePadding - 8);
            string[] captions = DisplayOptionCaptions(false);
            var rectangles = new Rectangle[endpointIdentificationAction==null?7:8];
            int x = 0, row = 0;
            Font font = displayOptionsFont ?? Font;
            for (int i = 0; i < rectangles.Length; i++)
            {
                int w = Math.Min(available, i<5?TextRenderer.MeasureText(captions[i], font, Size.Empty, TextFormatFlags.NoPadding).Width + Math.Max(30,Font.Height+16):i==7?EndpointIdentificationWidth:ConnectingOptionWidth(i));
                if (x > 0 && x + w > available) { row++; x = 0; }
                rectangles[i] = new Rectangle(left + x, row * rowHeight, w, rowHeight);
                x += w + gap;
            }
            // Cursor/export controls occupy only the bottom row, not every
            // option row. Reserving their width throughout wasted a third of
            // the usable footer on notebook displays.
            footerHeight = (row + 1) * rowHeight + 16;
            if(rectangles[rectangles.Length-1].Right>width-116)footerHeight+=30;
            return rectangles;
        }

        int DisplayOptionsFooterHeight(int width)
        {
            if(CompactDisplayOptions&&!displayOptionsExpanded)return Math.Max(38,Font.Height+14);
            int height;
            DisplayOptionRectangles(width, out height);
            return height+(CompactDisplayOptions?Math.Max(34,Font.Height+10):0);
        }

        void LayoutDisplayOptions()
        {
            if (displayOptionBoxes == null || disposingDisplayOptions || IsDisposed || Disposing) return;
            if (displayOptionsLayoutWidth == ClientSize.Width && displayOptionsLayoutHeight == ClientSize.Height && displayOptionsLayoutEnglish == english && displayOptionsLayoutAutomaticMixed == (ConnectingCableLengthM>0)) return;
            displayOptionsLayoutWidth = ClientSize.Width; displayOptionsLayoutHeight = ClientSize.Height; displayOptionsLayoutEnglish = english;
            displayOptionsLayoutAutomaticMixed=ConnectingCableLengthM>0;
            bool compact=CompactDisplayOptions,expanded=!compact||displayOptionsExpanded;
            displayOptionsToggle.Visible=compact;
            displayOptionsToggle.Text=expanded?L("Hide display options","收起显示选项"):L("Display options","波形显示选项");
            foreach(var option in displayOptionBoxes)option.Visible=expanded;
            reversePositionBox.Visible=connectingCableLabel.Visible=connectingCableInput.Visible=expanded;
            if(endpointIdentificationButton!=null)endpointIdentificationButton.Visible=expanded&&endpointIdentificationAction!=null;
            if(!expanded)
            {
                displayOptionsToggle.SetBounds(16,Math.Max(0,ClientSize.Height-Font.Height-10),Math.Min(ClientSize.Width-140,TextRenderer.MeasureText(displayOptionsToggle.Text,displayOptionsFont).Width+24),Font.Height+8);
                displayOptionsFrameBounds=Rectangle.Empty;LayoutCursorStepButtons();displayOptionsToggle.BringToFront();return;
            }
            int height;
            Rectangle[] rectangles = DisplayOptionRectangles(ClientSize.Width, out height);
            string[] captions = DisplayOptionCaptions(false), full = DisplayOptionCaptions(false);
            int top = Math.Max(0, ClientSize.Height - height + 8);
            if(compact){displayOptionsToggle.SetBounds(16,Math.Max(0,top-Font.Height-12),Math.Min(ClientSize.Width-140,TextRenderer.MeasureText(displayOptionsToggle.Text,displayOptionsFont).Width+24),Font.Height+8);displayOptionsToggle.BringToFront();}
            Rectangle contentBounds = Rectangle.Empty;
            for (int i = 0; i < displayOptionBoxes.Length; i++)
            {
                var box = displayOptionBoxes[i];
                box.Text = captions[i]; box.AccessibleName = full[i];
                box.AccessibleDescription = i==0&&ConnectingCableLengthM>0?AutomaticMixedCableHint():full[i];
                displayOptionsToolTip.SetToolTip(box, box.AccessibleDescription);
                Rectangle rectangle = rectangles[i]; rectangle.Offset(0, top); box.Bounds = rectangle;
                contentBounds = contentBounds.IsEmpty ? rectangle : Rectangle.Union(contentBounds, rectangle);
                box.BringToFront();
            }
            LayoutConnectingCableOptions(rectangles,top,ref contentBounds);
            LayoutEndpointIdentification(rectangles,top,ref contentBounds);
            displayOptionsFrameBounds = contentBounds;
            displayOptionsFrameBounds.Inflate(8, 5);
            LayoutCursorStepButtons();
        }

        // Paint behind the existing native checkboxes rather than introducing a
        // container: tab order, data bindings and mouse hit testing stay unchanged.
        // Called only from OnPaint, so the decorative UI is not exported to PNG.
        void DrawDisplayOptionsFrame(Graphics graphics)
        {
            if (displayOptionBoxes == null || disposingDisplayOptions || displayOptionsFrameBounds.IsEmpty) return;
            RectangleF bounds = displayOptionsFrameBounds;
            bounds.X += .5F; bounds.Y += .5F; bounds.Width -= 1F; bounds.Height -= 1F;
            const float diameter = 14F;
            using (var path = new GraphicsPath())
            using (var fill = new SolidBrush(HVX.PdReview.ReviewTheme.SurfaceAlt))
            using (var border = new Pen(HVX.PdReview.ReviewTheme.Border, 1F))
            {
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                GraphicsState state = graphics.Save();
                try
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.FillPath(fill, path);
                    graphics.DrawPath(border, path);
                }
                finally { graphics.Restore(state); }
            }
        }

        void DisposeDisplayOptions()
        {
            disposingDisplayOptions = true;
            SizeChanged -= DisplayOptionsSizeChanged; Invalidated -= DisplayOptionsInvalidated;
            if (displayOptionsToolTip != null) { displayOptionsToolTip.Dispose(); displayOptionsToolTip = null; }
            if (displayOptionBoxes != null)
            {
                foreach (var box in displayOptionBoxes) { box.CheckedChanged -= DisplayOptionChanged; box.Dispose(); }
                displayOptionBoxes = null;
            }
            if (displayOptionsFont != null) { displayOptionsFont.Dispose(); displayOptionsFont = null; }
        }
    }
}
