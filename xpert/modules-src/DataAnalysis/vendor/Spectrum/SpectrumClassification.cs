using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        bool confidenceClassificationEnabled, syncingClassificationControl;
        double classificationLowThresholdPercent = HVX.LocationScoring.LocationScorePolicy.DefaultSuspiciousMinimumPercent, classificationHighThresholdPercent = HVX.LocationScoring.LocationScorePolicy.DefaultAutomaticAcceptanceMinimumPercent;
        CheckBox classificationCheckbox;

        [Category("Classification"), DefaultValue(false), Description("Colour Q-L, Q-PHI and F-T by column 33 confidence. This changes display only; matrix values and accepted flags are not modified.")]
        public bool ConfidenceClassificationEnabled
        {
            get { return confidenceClassificationEnabled; }
            set { if (confidenceClassificationEnabled == value) return; confidenceClassificationEnabled = value; ClassificationDisplayChanged(); }
        }
        [Category("Classification"), DefaultValue(30.0), Description("Interference confidence upper boundary in percent, exclusive. Must be below the confirmed boundary.")]
        public double ClassificationLowThresholdPercent
        {
            get { return classificationLowThresholdPercent; }
            set { SetClassificationThresholds(value, classificationHighThresholdPercent); }
        }
        [Category("Classification"), DefaultValue(50.0), Description("High score lower boundary in percent, inclusive. Must not be below the suspect boundary. Not physical confirmation.")]
        public double ClassificationHighThresholdPercent
        {
            get { return classificationHighThresholdPercent; }
            set { SetClassificationThresholds(classificationLowThresholdPercent, value); }
        }
        public void SetClassificationThresholds(double lowPercent, double highPercent)
        {
            if (!HVX.LocationScoring.LocationScorePolicy.IsValid(lowPercent,lowPercent,highPercent))
                throw new ArgumentException("Classification thresholds must be finite and satisfy 0 <= low <= high <= 100.");
            if (classificationLowThresholdPercent == lowPercent && classificationHighThresholdPercent == highPercent) return;
            classificationLowThresholdPercent = lowPercent; classificationHighThresholdPercent = highPercent;
            ClassificationDisplayChanged();
        }
        public void ResetClassificationThresholds() { SetClassificationThresholds(HVX.LocationScoring.LocationScorePolicy.DefaultSuspiciousMinimumPercent, HVX.LocationScoring.LocationScorePolicy.DefaultAutomaticAcceptanceMinimumPercent); }
        [Browsable(false)] public bool ClassificationControlVisible { get { return !competitionProfile&&(mode == 0 || mode == 2 || mode == 4); } }
        [Browsable(false)] public Rectangle ClassificationCheckboxBounds
        { get { return ClassificationControlVisible && classificationCheckbox != null ? classificationCheckbox.Bounds : Rectangle.Empty; } }
        bool ClassificationActive { get { return confidenceClassificationEnabled && ClassificationControlVisible; } }
        int ClassificationLegendHeight { get { return Math.Max(24, Font.Height + 4); } }
        int ClassificationFooterReserve { get { return Math.Max(20, Font.Height + 8); } }

        /// <summary>Source column 33 is confidence in [0,1]. Returns -1 unknown, 0 interference, 1 suspect or 2 confirmed, independent of the display toggle and column 35 acceptance.</summary>
        public int GetSourceClassification(int row)
        { return ClassifySourceRow(source,row,classificationLowThresholdPercent,classificationHighThresholdPercent); }
        public static int ClassifySourceRow(double[,] source,int row,double classificationLowThresholdPercent,double classificationHighThresholdPercent)
        {
            if(!HVX.LocationScoring.LocationScorePolicy.IsValid(classificationLowThresholdPercent,classificationLowThresholdPercent,classificationHighThresholdPercent))
                throw new ArgumentException("Classification thresholds must be finite and satisfy 0 <= low <= high <= 100.");
            if (row < 0 || row >= source.GetLength(0)) throw new ArgumentOutOfRangeException("row");
            if (LabVIEWPdMap.PdMapTimingEvidence.IsNegativeDelayRow(source,row)) return -1;
            if (Finite(source[row,10]) && source[row,10]>=0 && source[row,10]<=int.MaxValue && source[row,10]==Math.Floor(source[row,10]) &&
                source[row,11]==source[row,10] && source[row,12]==0 &&
                Finite(source[row,31]) && source[row,31]>=0 && source[row,31]<=int.MaxValue && source[row,31]==Math.Floor(source[row,31]) &&
                Finite(source[row,32]) && source[row,32]>source[row,31] && source[row,32]<=int.MaxValue && source[row,32]==Math.Floor(source[row,32])) return -1;
            double confidence = source[row, 33];
            if (!Finite(confidence) || confidence < 0 || confidence > 1) return -1;
            // Preserve the zero boundary even if an exceptionally small positive
            // percent threshold underflows when converted to a fraction.
            if(!HVX.LocationScoring.LocationScorePolicy.Meets(confidence,classificationLowThresholdPercent))return 0;
            return HVX.LocationScoring.LocationScorePolicy.Meets(confidence,classificationHighThresholdPercent)?2:1;
        }
        /// <summary>Current visible source-row counts in order: unknown, interference, suspect, confirmed. The returned array is independent of the cached matrix.</summary>
        public int[] GetVisibleClassificationCounts()
        {
            var counts = new int[4];
            foreach (int row in visibleRows) counts[GetSourceClassification(row) + 1]++;
            return counts;
        }
        void ClassificationDisplayChanged()
        {
            CancelGesture(); DropDrawingCaches(); SyncClassificationControl(); LayoutSpectrumToolbar(); Invalidate(); NotifyHost(ViewChanged);
        }
        void InitializeClassificationControl()
        {
            if (classificationCheckbox != null) return;
            classificationCheckbox = new CheckBox { Name = "SpectrumConfidenceClassification", AutoSize = false,
                BackColor = Color.White, TabIndex = 3, AccessibleRole = AccessibleRole.CheckButton,
                TextAlign = ContentAlignment.MiddleLeft, CheckAlign = ContentAlignment.MiddleLeft };
            classificationCheckbox.CheckedChanged += delegate
            { if (!syncingClassificationControl) { ConfidenceClassificationEnabled = classificationCheckbox.Checked; NotifyHost(ClassificationManuallyChanged); } };
            Controls.Add(classificationCheckbox); SyncClassificationControl();
        }
        void SyncClassificationControl()
        {
            if (classificationCheckbox == null || classificationCheckbox.IsDisposed) return;
            syncingClassificationControl = true;
            try
            {
                classificationCheckbox.Visible = ClassificationControlVisible;
                classificationCheckbox.Font = Font; classificationCheckbox.ForeColor = ForeColor;
                classificationCheckbox.Text = L("Classify by confidence","分类管理");
                classificationCheckbox.Checked = confidenceClassificationEnabled;
                classificationCheckbox.AccessibleName = classificationCheckbox.Text;
                classificationCheckbox.AccessibleDescription = ClassificationLegendText;
                if (tooltip != null) tooltip.SetToolTip(classificationCheckbox, ClassificationLegendText);
            }
            finally { syncingClassificationControl = false; }
            LayoutClassificationControl();
        }
        void LayoutClassificationControl()
        {
            if (classificationCheckbox == null || classificationCheckbox.IsDisposed) return;
            int width = TextRenderer.MeasureText(L("Classify by confidence","分类管理"), Font).Width + 28;
            int height = Math.Max(26, Font.Height + 8);
            int right = RegionSelectionEnabled ? ShapeBounds(ClientRectangle).Left - 8 : ClientSize.Width - 12 - ChartExportReservedWidth;
            int bottom = ClientSize.Height - ToolbarHeightForWidth(ClientSize.Width) - 8;
            classificationCheckbox.Bounds = new Rectangle(Math.Max(0, right - width), Math.Max(0, bottom - height), width, height);
            classificationCheckbox.BringToFront();
        }
        public void PerformClassificationCheckboxClick()
        {
            if (!ClassificationControlVisible) throw new InvalidOperationException("Confidence classification is available only for Q-L, Q-PHI and F-T.");
            ConfidenceClassificationEnabled = !confidenceClassificationEnabled;
            NotifyHost(ClassificationManuallyChanged);
        }
        public event EventHandler ClassificationManuallyChanged;
        static string ClassificationPercent(double value)
        { return value.ToString(value > 0 && value < .000001 ? "G17" : "0.###############", CultureInfo.InvariantCulture); }
        [Browsable(false)] public string ClassificationLegendText
        {
            get
            {
                string low = ClassificationPercent(classificationLowThresholdPercent), high = ClassificationPercent(classificationHighThresholdPercent);
                int unknown = GetVisibleClassificationCounts()[0];
                return String.Format(L("Gray: low score <{0}%; 50% phase color: medium score {0}% to <{1}%; solid phase color: high score >={1}%; hollow gray: score unavailable ({2}). Score bands do not confirm a physical discharge; original acceptance is retained.",
                    "灰色：低评分 <{0}%；半透明相色：中评分 {0}%～<{1}%；不透明相色：高评分 ≥{1}%；灰色空心：无有效评分（{2}）。评分分级不代表物理放电确认，不改变原始接受标志。"),low,high,unknown);
            }
        }
        string ClassificationHeaderText
        {
            get
            {
            string low = ClassificationPercent(classificationLowThresholdPercent), high = ClassificationPercent(classificationHighThresholdPercent);
            int unknown = GetVisibleClassificationCounts()[0];
            string label = String.Format(L("Low: <{0}%   Medium: {0}–<{1}%   High: ≥{1}%","低评分：<{0}%   中评分：{0}～<{1}%   高评分：≥{1}%"),low,high);
            if (unknown > 0) label += L("   Hollow: unavailable ","   空心：无有效确信度 ")+unknown;
            return label;
            }
        }

        sealed class ClassificationPalette : IDisposable
        {
            internal readonly Brush[,] Fills = new Brush[3, 4];
            internal readonly Pen UnknownOutline = new Pen(Color.Gray, 1.25f);
            internal readonly PointF[] Triangle = new PointF[3];
            internal ClassificationPalette()
            {
                for (int phase = 0; phase < 4; phase++)
                {
                    Fills[0, phase] = new SolidBrush(Color.Gray);
                    Fills[1, phase] = new SolidBrush(Color.FromArgb(128, PhaseColors[phase]));
                    Fills[2, phase] = new SolidBrush(PhaseColors[phase]);
                }
            }
            public void Dispose() { foreach (Brush brush in Fills) brush.Dispose(); UnknownOutline.Dispose(); }
        }
        void DrawClassifiedMarker(Graphics g, PointF pixel, SpectrumPoint point, ClassificationPalette palette)
        {
            int classification = point.SourceRows.Length == 1 ? GetSourceClassification(point.SourceRows[0]) : -1;
            if (classification < 0) DrawMarker(g, pixel, point.Phase, 3.5f, palette.UnknownOutline);
            else DrawFilledMarker(g, pixel, point.Phase, 3.5f, palette.Fills[classification, Math.Max(0, Math.Min(3, point.Phase))], palette.Triangle);
        }
    }
}
