using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        ContextMenuStrip regionMenu;
        SpectrumLinkedSelection linkedSelection;
        ToolStripMenuItem regionStatisticsItem, exitRegionStatisticsItem, deleteRegionItem, clearRegionItem, resetRegionViewItem;
        ToolTip tooltip;
        Point dragStart, dragEnd;
        bool dragging, shapePressed, clearPressed;
        int shape;
        [Category("Selection"), Description("0 rectangle, 1 ellipse with independent width and height; switches on bottom-right button release.")]
        public int SelectionShapeIndex { get { return shape; } set { if (value != 0 && value != 1) throw new ArgumentOutOfRangeException("SelectionShapeIndex"); if (shape == value) return; shape = value; NotifyHost(SelectionShapeChanged); Invalidate(); } }
        [Category("Selection"), DefaultValue(true)] public bool UseInternalContextMenu { get; set; } = true;
        int regionActionCommand;
        [Category("Selection"), Description("Writing 0 statistics, 1 delete cached rows or 2 cancel regions executes once. Do not call ApplyRegionAction again for the same action.")]
        public int RegionActionCommand { get { return regionActionCommand; } set { if(value<0||value>2) throw new ArgumentOutOfRangeException("RegionActionCommand"); regionActionCommand=value; ApplyRegionAction(); } }
        [Browsable(false)] public int RegionActionSequence { get; private set; }
        [Browsable(false)] public int SelectionRegionCount { get { return State.Regions.Count; } }
        [Browsable(false)] public bool RegionSelectionEnabled { get { return mode == 0 || mode == 2 || mode == 4 || CompetitionLocationSelectionPlane; } }
        [Browsable(false)]
        public SpectrumLinkedSelection LinkedSelection
        {
            get { return linkedSelection; }
            set
            {
                if (Object.ReferenceEquals(linkedSelection, value)) return;
                if (linkedSelection != null) linkedSelection.Changed -= LinkedSelectionChanged;
                linkedSelection = value;
                if (linkedSelection != null) linkedSelection.Changed += LinkedSelectionChanged;
                UpdateRegionActionItems();
            }
        }
        bool SharedSelectionActive { get { return linkedSelection != null && linkedSelection.Active; } }
        void LinkedSelectionChanged(object sender, EventArgs e) { UpdateRegionActionItems(); Invalidate(); }
        internal void SyncLinkedContextMenu() { UpdateRegionActionItems(); }
        [Browsable(false)] public string LastInteractionStatus { get; private set; } = "";
        [Browsable(false)] public Rectangle SelectionShapeButtonBounds { get { return ShapeBounds(ClientRectangle); } }
        [Browsable(false)] public Rectangle SelectionClearButtonBounds { get { return ClearSelectionBounds(ClientRectangle); } }

        void InitializeInteraction()
        {
            tooltip = new ToolTip(); regionMenu = new ContextMenuStrip();
            regionStatisticsItem = new ToolStripMenuItem { Name = "StatisticsSelectedRegion" };
            exitRegionStatisticsItem = new ToolStripMenuItem { Name = "ExitRegionStatistics" };
            deleteRegionItem = new ToolStripMenuItem { Name = "DeleteSelectedRegion" };
            clearRegionItem = new ToolStripMenuItem { Name = "ClearSelectionRegions" };
            regionStatisticsItem.Click += (s, e) => RegionActionCommand = 0;
            exitRegionStatisticsItem.Click += (s, e) => ExitLinkedOrLocalSelection();
            deleteRegionItem.Click += (s, e) => RegionActionCommand = 1;
            clearRegionItem.Click += (s, e) => ClearLinkedOrLocalSelection();
            regionMenu.Items.AddRange(competitionProfile?new ToolStripItem[]{regionStatisticsItem,exitRegionStatisticsItem,clearRegionItem}:new ToolStripItem[] { regionStatisticsItem, exitRegionStatisticsItem, deleteRegionItem, clearRegionItem });
            regionMenu.Items.Add(new ToolStripSeparator());
            resetRegionViewItem = new ToolStripMenuItem(); resetRegionViewItem.Click += (s, e) => ResetView(); regionMenu.Items.Add(resetRegionViewItem);
            regionMenu.Opening += (s, e) => UpdateRegionActionItems();
            // completeReadOnlyModes uses analysis spectra: restore voltage/filter/undo items
            // (still omit delete — competition/read-only path). Ordinary analysis keeps delete.
            if(!competitionProfile||completeReadOnlyModes)InitializeRangeContextItems();UpdateMenuLanguage();
        }
        void UpdateMenuLanguage()
        {
            UpdateCableJointMenu();
            if (regionMenu == null) return;
            regionStatisticsItem.Text = L("Statistics for selected region", "统计所选区域");
            exitRegionStatisticsItem.Text = L("Exit regional statistics", "退出区域统计");
            deleteRegionItem.Text = L("Delete selected region", "删除所选区域");
            clearRegionItem.Text = L("Clear regions", "取消选区");
            resetRegionViewItem.Text = L("Reset view", "复位视图");
            UpdateRegionActionItems();
            UpdateRangeContextItems();
        }
        void UpdateRegionActionItems()
        {
            if (regionMenu == null) return;
            bool shared = SharedSelectionActive;
            bool localStats = RegionStatisticsActive;
            ViewState localState;
            bool hasLocalRegions = states.TryGetValue(mode, out localState) && localState.Regions.Count > 0;
            // Linked dual-pane: both menus reflect the same shared selection/filter.
            regionStatisticsItem.Enabled = RegionSelectionEnabled && !shared;
            deleteRegionItem.Enabled = RegionSelectionEnabled && hasLocalRegions && !competitionProfile;
            clearRegionItem.Enabled = (RegionSelectionEnabled && hasLocalRegions) || shared || localStats;
            exitRegionStatisticsItem.Enabled = localStats || shared;
            exitRegionStatisticsItem.Checked = localStats || shared;
            clearRegionItem.Checked = shared && !hasLocalRegions;
        }
        void ExitLinkedOrLocalSelection()
        {
            if (SharedSelectionActive) { linkedSelection.Clear(); return; }
            ExitRegionStatistics();
        }
        void ClearLinkedOrLocalSelection()
        {
            if (SharedSelectionActive) { linkedSelection.Clear(); return; }
            RegionActionCommand = 2;
        }
        Rectangle ShapeBounds(Rectangle bounds) { return CompetitionLocationSelectionPlane?new Rectangle(bounds.Left+12,bounds.Bottom-ToolbarHeightForWidth(bounds.Width)-ViewActionButtonHeight-6,32,ViewActionButtonHeight):new Rectangle(bounds.Right - 78 - ChartExportReservedWidth, bounds.Bottom - ToolbarHeightForWidth(bounds.Width) - 34, 32, 26); }
        Rectangle ClearSelectionBounds(Rectangle bounds) { return CompetitionLocationSelectionPlane?new Rectangle(bounds.Left+50,bounds.Bottom-ToolbarHeightForWidth(bounds.Width)-ViewActionButtonHeight-6,28,ViewActionButtonHeight):new Rectangle(bounds.Right - 40 - ChartExportReservedWidth, bounds.Bottom - ToolbarHeightForWidth(bounds.Width) - 34, 28, 26); }
        void DrawSelectionButtons(Graphics g, Rectangle bounds)
        {
            if (!RegionSelectionEnabled) return;
            Rectangle shapeButton = ShapeBounds(bounds), clearButton = ClearSelectionBounds(bounds);
            var saved = g.Save();
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var outline = new Pen(HVX.PdReview.ReviewTheme.Border)) using (var icon = new Pen(HVX.PdReview.ReviewTheme.Accent, 1.5f))
            {
                HVX.PdReview.ReviewTheme.Frame(g,shapeButton,shapePressed?HVX.PdReview.ReviewTheme.ActiveSoft:HVX.PdReview.ReviewTheme.Surface,outline.Color);
                var shapeIcon = new Rectangle(shapeButton.Left + 7, shapeButton.Top + 7, shapeButton.Width - 14, shapeButton.Height - 14);
                if (shape == 0) g.DrawRectangle(icon, shapeIcon); else g.DrawEllipse(icon, shapeIcon);
                HVX.PdReview.ReviewTheme.Frame(g,clearButton,clearPressed?HVX.PdReview.ReviewTheme.ActiveSoft:HVX.PdReview.ReviewTheme.Surface,outline.Color);
                float center = clearButton.Left + clearButton.Width / 2f, top = clearButton.Top + 6;
                g.DrawLines(icon, new[] { new PointF(center - 3, top + 2), new PointF(center - 3, top), new PointF(center + 3, top), new PointF(center + 3, top + 2) });
                g.DrawLine(icon, center - 7, top + 3, center + 7, top + 3);
                g.DrawLines(icon, new[] { new PointF(center - 5, top + 5), new PointF(center - 4, top + 14), new PointF(center + 4, top + 14), new PointF(center + 5, top + 5) });
                g.DrawLine(icon, center - 2, top + 6, center - 2, top + 12);
                g.DrawLine(icon, center + 2, top + 6, center + 2, top + 12);
            }
            g.Restore(saved);
        }
        void CancelGesture() { dragging = shapePressed = clearPressed = rotating3D = false; Capture = false; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); Focus();
            if (e.Button != MouseButtons.Left) return;
            if (view != null && view.Is3D && !CompetitionLocationSelectionPlane && selfCheckPassed && e.Clicks == 1 && PlotArea(ClientRectangle).Contains(e.Location)) { BeginCameraDrag(e.Location); return; }
            if (!RegionSelectionEnabled) return;
            if (ClearSelectionBounds(ClientRectangle).Contains(e.Location)) { clearPressed = true; Capture = true; Invalidate(); return; }
            if (ShapeBounds(ClientRectangle).Contains(e.Location)) { shapePressed = true; Capture = true; Invalidate(); return; }
            if (e.Clicks == 1 && PlotArea(ClientRectangle).Contains(e.Location)) { dragging = true; Capture = true; dragStart = dragEnd = e.Location; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (rotating3D) { ContinueCameraDrag(e.Location); return; }
            if (dragging) { dragEnd = Constrain(e.Location, PlotArea(ClientRectangle)); Invalidate(); return; }
            if (!RegionSelectionEnabled) { tooltip.SetToolTip(this, ""); return; }
            if (ClearSelectionBounds(ClientRectangle).Contains(e.Location)) { tooltip.SetToolTip(this, L("Clear selection regions; data is retained.","清除选区；不删除数据。")); return; }
            if (ShapeBounds(ClientRectangle).Contains(e.Location)) { tooltip.SetToolTip(this, english ? (shape == 0 ? "Rectangle selection. Release to switch to ellipse; drag to set its width and height." : "Ellipse selection. Drag to set its width and height; release the button to switch to rectangle.") : (shape == 0 ? "当前为矩形选区。释放按钮切换为椭圆；拖动可自由设置宽高。" : "当前为椭圆选区，可自由拖动设置宽高。释放按钮切换为矩形。")); return; }
            if(!selfCheckPassed){tooltip.SetToolTip(this, "");return;}
            int row = NearestHoverRow(e.X, e.Y);
            tooltip.SetToolTip(this, PointTooltipText(row));
        }
        string PointTooltipText(int row)
        {
            if (row < 0 || row >= source.GetLength(0) || !RegionSelectionEnabled) return "";
            // Q-L displays the magnitude of the original Q (column 15), not the
            // compensated Q used by test-order statistics. F-T uses the same Q.
            double charge = Math.Abs(PresentedCharge(row)) / ChargeDisplayScale;
            return (L("Charge: ","放电量：")) + TooltipMeasurement(charge, ChargeUnitLabel) + "\n" +
                (L("Position: ","放电位置：")) + TooltipMeasurement(source[row, 13], "m") + "\n" +
                (L("Test voltage: ","试验电压：")) + TooltipMeasurement(source[row, 2], "kV");
        }
        string TooltipMeasurement(double value, string unit)
        {
            if (!Finite(value)) return L("Not provided","未提供");
            string format = unit == "pC" ? "0" : "0.0";
            return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + " " + unit;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && UseInternalContextMenu) { if(!competitionProfile||completeReadOnlyModes)PrepareCableJointContext(e.Location); UpdateRegionActionItems(); regionMenu.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            if (rotating3D) { ContinueCameraDrag(e.Location); CancelGesture(); Invalidate(); return; }
            if (clearPressed) { bool inside = ClearSelectionBounds(ClientRectangle).Contains(e.Location); CancelGesture(); if (inside && RegionSelectionEnabled) RegionActionCommand = 2; Invalidate(); return; }
            if (shapePressed) { bool inside = ShapeBounds(ClientRectangle).Contains(e.Location); CancelGesture(); if (inside) SelectionShapeIndex = 1 - shape; Invalidate(); return; }
            if (!dragging) return;
            dragEnd = Constrain(e.Location, PlotArea(ClientRectangle)); RectangleF box = DragBox();
            CancelGesture();
            if (box.Width >= 4 && box.Height >= 4)
            {
                var area = PlotArea(ClientRectangle);
                AddSelectionRegion(DataX(box.Left, area), DataX(box.Right, area), DataY(box.Bottom, area), DataY(box.Top, area), shape == 1);
                // Drawing is a pending selection, not a statistics command.
                // The operator explicitly invokes Statistics selected region
                // from the context menu before the shared input is filtered.
            }
            Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { if (!Capture) { dragging = shapePressed = clearPressed = rotating3D = false; Invalidate(); } base.OnMouseCaptureChanged(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); CancelGesture(); if (e.Button == MouseButtons.Left) SelectPointAt(e.X, e.Y); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { CancelGesture(); State.Regions.Clear(); Invalidate(); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ApplyAxisWheel(e,(ModifierKeys & Keys.Shift) != 0);
        }
        internal void ApplyAxisWheel(MouseEventArgs e,bool vertical)
        {
            Rectangle area = PlotArea(ClientRectangle); if (!area.Contains(e.Location) || view.Is3D&&!CompetitionLocationSelectionPlane || e.Delta == 0) return;
            bool y = vertical; double factor = e.Delta > 0 ? 0.8 : 1.25;
            if (y) { double anchor = DataY(e.Y, area), span = (State.Y1 - State.Y0) * factor; if (span < 1e-12 || span > 1e18) return; State.Y0 = anchor + (State.Y0 - anchor) * factor; State.Y1 = State.Y0 + span; }
            else { double anchor = DataX(e.X, area), span = (State.X1 - State.X0) * factor; if (span < 1e-12 || span > 1e18) return; State.X0 = anchor + (State.X0 - anchor) * factor; State.X1 = State.X0 + span; }
            Invalidate(); NotifyHost(ViewChanged);
        }
        // Keep the legacy parameter name for source/API compatibility; true means an ellipse.
        public void AddSelectionRegion(double minimumX, double maximumX, double minimumY, double maximumY, bool circle)
        {
            if (!RegionSelectionEnabled) throw new InvalidOperationException("Regions require Q-L, Q-PHI, F-T or the competition L-PRPD XY plane.");
            if (!Finite(minimumX) || !Finite(maximumX) || !Finite(minimumY) || !Finite(maximumY) || maximumX <= minimumX || maximumY <= minimumY) throw new ArgumentException("Region bounds must be finite and increasing.");
            State.Regions.Add(new SelectionRegion { X0 = minimumX, X1 = maximumX, Y0 = minimumY, Y1 = maximumY, Circle = circle }); Invalidate();
        }
        int[] SelectedRows()
        {
            if (!RegionSelectionEnabled) return new int[0];
            return view.Points.Where(p => State.Regions.Any(r => r.Contains(p.X, p.Y))).SelectMany(p => p.SourceRows).Distinct().OrderBy(r => r).ToArray();
        }
        internal int[] GetSelectionRegionSourceRows() { return SelectedRows(); }
        public double[,] ApplyRegionAction()
        {
            if(competitionProfile&&RegionActionCommand==1)throw new InvalidOperationException("Competition view is read-only; result deletion is unavailable.");
            if (!RegionSelectionEnabled) throw new InvalidOperationException("Select Q-L, Q-PHI, F-T or the competition L-PRPD XY plane to use region actions.");
            if (RegionActionCommand < 0 || RegionActionCommand > 2) throw new ArgumentOutOfRangeException("RegionActionCommand");
            int action = RegionActionCommand;
            lastRows = SelectedRows(); lastMatrix = RowsMatrix(lastRows);
            bool regional = action == 0 && EnterRegionStatistics(lastRows);
            if (action == 1) { RememberRegionDeletion(lastRows);foreach (int row in lastRows) deleted.Add(row); ClearHit(); State.Regions.Clear(); ClearDataViews(); Rebuild(false); }
            if (action == 2) State.Regions.Clear();
            var completedSnapshot=(double[,])lastMatrix.Clone();
            RegionActionSequence++; Invalidate(); NotifyHost(RegionActionCompleted);
            if (regional) NotifyHost(RegionStatisticsChanged);
            return completedSnapshot;
        }
        public bool SelectPointAt(int pixelX, int pixelY)
        {
            ClearHit(); Invalidate(); if (!RegionSelectionEnabled || !selfCheckPassed) return false;
            hitRows = NearestRows(pixelX, pixelY); if (hitRows.Length == 0) { LastInteractionStatus = L("No point at this position.","此处没有数据点。"); return false; }
            // A hit with a missing MAP ID must not be silently substituted with another point.
            int row = hitRows[0]; hitIds = hitRows.Select(r => source[r, 9]).ToArray();
            double id = source[row, 9]; if (!Finite(id) || id < 0 || Math.Truncate(id) != id) { LastInteractionStatus = L("The point has no valid PDmap ID in column 9.","该点第9列没有有效的PDmap ID。"); return false; }
            SelectedSourceRowIndex = row; SelectedPdMapId = id; SelectedRecordId = source[row, 1]; SelectedLocId = source[row, 8];
            LastInteractionStatus = hitRows.Length > 1 ? (L("Overlapping points: all candidate rows are available.","存在重叠点，已输出全部候选行。")) : "";
            PdMapSelectionSequence++; NotifyHost(PdMapSelected); Invalidate(); return true;
        }
        int[] NearestRows(int px, int py)
        {
            Rectangle area = PlotArea(ClientRectangle); if (px<area.Left-4 || px>area.Right+4 || py<area.Top-4 || py>area.Bottom+4) return new int[0];
            return HitCandidates(px,py).Select(p=>new {p.Point,D=(p.Pixel.X-px)*(p.Pixel.X-px)+(p.Pixel.Y-py)*(p.Pixel.Y-py)})
                .Where(p=>p.D<=64).OrderBy(p=>p.D).ThenBy(p=>p.Point.SourceRows[0]).SelectMany(p=>p.Point.SourceRows).Distinct().ToArray();
        }
        public double[] GetSourcePointPixel(int sourceRow)
        {
            if (!selfCheckPassed) return new double[0];
            var point = view.Points.FirstOrDefault(p => p.SourceRows.Contains(sourceRow)); if (point == null) return new double[0];
            var pixel = Project(point, PlotArea(ClientRectangle)); return Finite(pixel.X) && Finite(pixel.Y) ? new[] { (double)pixel.X, (double)pixel.Y } : new double[0];
        }
        static Point Constrain(Point p, Rectangle area) { return new Point(Math.Max(area.Left, Math.Min(area.Right, p.X)), Math.Max(area.Top, Math.Min(area.Bottom, p.Y))); }
        RectangleF DragBox()
        {
            int dx = dragEnd.X - dragStart.X, dy = dragEnd.Y - dragStart.Y;
            return new RectangleF(Math.Min(dragStart.X, dragStart.X + dx), Math.Min(dragStart.Y, dragStart.Y + dy), Math.Abs(dx), Math.Abs(dy));
        }
    }
}
