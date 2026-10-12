using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        Button previousSampleButton, nextSampleButton;
        ToolTip cursorStepToolTip;
        bool updatingCursorStepButtons;

        void InitializeCursorStepButtons()
        {
            if (previousSampleButton != null) return;
            cursorStepToolTip = new ToolTip { ShowAlways = true };
            previousSampleButton = CreateCursorStepButton("PreviousSampleButton", "◀", -1);
            nextSampleButton = CreateCursorStepButton("NextSampleButton", "▶", 1);
            Controls.Add(previousSampleButton);
            Controls.Add(nextSampleButton);
            SizeChanged += CursorStepSizeChanged;
            Invalidated += CursorStepInvalidated;
            if (exportButton != null) exportButton.LocationChanged += CursorStepSizeChanged;
            LayoutCursorStepButtons();
            UpdateCursorStepButtons();
        }

        Button CreateCursorStepButton(string name, string symbol, int direction)
        {
            var button = new LabVIEWChartCommon.SampleStepButton(direction)
            {
                Name = name,
                Text = symbol,
                Size = new Size(28, 26),
                MinimumSize = new Size(28, 26),
                MaximumSize = new Size(28, 26),
                FlatStyle = FlatStyle.Flat,
                BackColor = HVX.PdReview.ReviewTheme.Surface,
                UseVisualStyleBackColor = false,
                TabStop = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                Cursor = Cursors.Hand,
                Enabled = false
            };
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = HVX.PdReview.ReviewTheme.ActiveSoft;
            button.Click += delegate { StepSelectedCursor(direction); };
            return button;
        }

        void CursorStepSizeChanged(object sender, EventArgs e) { LayoutCursorStepButtons(); }
        void CursorStepInvalidated(object sender, InvalidateEventArgs e) { UpdateCursorStepButtons(); }

        void LayoutCursorStepButtons()
        {
            if (previousSampleButton == null || nextSampleButton == null || IsDisposed || Disposing) return;
            int exportLeft = Math.Max(0, ClientSize.Width - (exportButton == null ? 28 : exportButton.Width) - 3);
            int top = Math.Max(0, ClientSize.Height - nextSampleButton.Height - 3);
            int nextLeft = Math.Max(0, exportLeft - 12 - nextSampleButton.Width);
            nextSampleButton.Location = new Point(nextLeft, top);
            previousSampleButton.Location = new Point(Math.Max(0, nextLeft - 3 - previousSampleButton.Width), top);
            previousSampleButton.BringToFront();
            nextSampleButton.BringToFront();
        }

        void UpdateCursorStepButtons()
        {
            if (updatingCursorStepButtons || previousSampleButton == null || nextSampleButton == null || IsDisposed || Disposing) return;
            updatingCursorStepButtons = true;
            try
            {
                bool selected = selectedCursor == 1 || selectedCursor == 2;
                bool available = selected && pulse.Length > 0;
                int index = selectedCursor == 1 ? incident : reflection;
                previousSampleButton.Enabled = available && index > 0;
                nextSampleButton.Enabled = available && index < pulse.Length - 1;
                if(selectedCursor==2)
                {
                    int first,last;bool bounded=TryReflectionSelectionBounds(out first,out last);
                    previousSampleButton.Enabled=available&&bounded&&index>first;
                    nextSampleButton.Enabled=available&&bounded&&index<last;
                }
                Color color = selectedCursor == 1 ? incidentColor : selectedCursor == 2 ? reflectionColor : HVX.PdReview.ReviewTheme.SecondaryInk;
                previousSampleButton.ForeColor = nextSampleButton.ForeColor = color;
                string cursorName = selectedCursor == 1 ? (L("incident cursor","入射游标")) :
                    selectedCursor == 2 ? (L("reflection cursor","反射游标")) : "";
                string previous = L("Previous sample","前一样点");
                string next = L("Next sample","后一样点");
                string guidance = selected
                    ? String.Format(L("Move the {0} by one sample.","移动{0}一个采样点。"),cursorName)
                    : (L("Select an incident or reflection cursor first.","请先选中入射或反射游标。") );
                previousSampleButton.AccessibleName = previous;
                nextSampleButton.AccessibleName = next;
                previousSampleButton.AccessibleDescription = nextSampleButton.AccessibleDescription = guidance;
                cursorStepToolTip.SetToolTip(previousSampleButton, previous + " — " + guidance);
                cursorStepToolTip.SetToolTip(nextSampleButton, next + " — " + guidance);
            }
            finally { updatingCursorStepButtons = false; }
        }

        public void SelectIncidentCursor()
        {
            EndCursorDrag("cursor selected by command");
            SelectCursor(1);
        }

        public void SelectReflectionCursor()
        {
            EndCursorDrag("cursor selected by command");
            SelectCursor(2);
        }

        /// <summary>Moves only the selected cursor by exactly -1 or +1 sample without snapping or resetting the view.</summary>
        public void StepSelectedCursor(int direction)
        {
            if ((direction != -1 && direction != 1) || pulse.Length == 0 || (selectedCursor != 1 && selectedCursor != 2)) return;
            EndCursorDrag("sample step");
            int current = selectedCursor == 1 ? incident : reflection;
            int target = (int)Math.Max(0L, Math.Min(pulse.Length - 1L, (long)current + direction));
            if(selectedCursor==2){target=ConstrainReflectionSelection(target);if(target<0)return;}
            if (target == current&&(selectedCursor!=2||reflectionSelected)) { UpdateCursorStepButtons(); return; }
            SuppressPositionApproximationForExactEdit();
            if (selectedCursor == 1) incident = target;
            else {reflection = target;reflectionSelected=true;}
            ClampIndices();
            AlignCalibration();
            RecomputeFilteredCurves();
            UpdateDynamicAnalysis();
            UpdateCursorStepButtons();
            Invalidate();
            NotifyReviewAnalysisEdited("SampleStep");
        }

        void DisposeCursorStepButtons()
        {
            SizeChanged -= CursorStepSizeChanged;
            Invalidated -= CursorStepInvalidated;
            if (exportButton != null) exportButton.LocationChanged -= CursorStepSizeChanged;
            if (cursorStepToolTip != null) { cursorStepToolTip.Dispose(); cursorStepToolTip = null; }
            if (previousSampleButton != null) previousSampleButton.Dispose();
            if (nextSampleButton != null) nextSampleButton.Dispose();
        }
    }
}
