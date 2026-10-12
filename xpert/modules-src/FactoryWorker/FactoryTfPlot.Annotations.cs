using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal enum FactoryTfDrawMode { Browse, Rectangle, Oval }
    internal sealed partial class FactoryTfPlot
    {
        private FactoryTfAnnotationDocument annotations;
        private Dictionary<string, List<FactoryTfAnnotationRegion>> annotationMap = new Dictionary<string, List<FactoryTfAnnotationRegion>>();
        private List<FactoryTfAnnotationRegion> visibleRegions = new List<FactoryTfAnnotationRegion>();
        private readonly Dictionary<string, Rectangle> regionCaptionHits = new Dictionary<string, Rectangle>();
        private readonly ToolTip annotationTip = new ToolTip();
        private Rectangle annotationArea;
        private double annotationXmax = 1, annotationYmax = 1;
        private Point dragFirst, dragLast;
        private bool regionDragging;
        private FactoryTfRegionEvent pendingRegion;
        private readonly ContextMenuStrip labelMenu = new ContextMenuStrip();
        private Dictionary<string, FactoryPulseMatch> recognition = new Dictionary<string, FactoryPulseMatch>();
        private FactoryClusterResult clustering = new FactoryClusterResult();
        internal void SetRecognition(Dictionary<string, FactoryPulseMatch> values, FactoryClusterResult clusters)
        { recognition = values; clustering = clusters; Invalidate(); }
        internal string RecognitionText(FactoryTfDisplayPoint point)
        {
            string key = FactoryTfAnnotationDocument.PointKey(point); FactoryPulseMatch match; int group;
            return (recognition.TryGetValue(key, out match) ? match.Text : "未识别") +
                (clustering.Groups.TryGetValue(key, out group) ? (group < 0 ? "；离群点（不代表干扰）" : "；本视图簇 " + (group + 1)) : "");
        }
        internal Color PointColor(FactoryTfDisplayPoint point)
        {
            int group; if (!clustering.Groups.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out group)) return point.Round.Simulation ? Color.FromArgb(165, 96, 173) : Color.FromArgb(0, 125, 116);
            return group < 0 ? Color.Gray : new[] { Color.Teal, Color.SteelBlue, Color.DarkViolet, Color.IndianRed, Color.Olive, Color.DarkCyan, Color.Sienna, Color.RoyalBlue }[group % 8];
        }
        private FactoryTfDrawMode drawingMode;
        internal string HighlightedRegionId { get; set; }
        internal int VisibleRegionCount { get { return visibleRegions.Count; } }
        internal int RegionRenderCount { get; private set; }
        internal event EventHandler<FactoryTfRegionEvent> RegionSelected;
        internal event EventHandler<FactoryTfLabelEditEvent> LabelEditRequested;
        internal FactoryTfDrawMode DrawingMode
        {
            get { return drawingMode; }
            set { CancelRegionDrag(); drawingMode = value; Cursor = value == FactoryTfDrawMode.Browse ? Cursors.Default : Cursors.Cross; }
        }
        internal void SetAnnotations(FactoryTfAnnotationDocument doc)
        { annotations = doc; RefreshAnnotationMap(); Invalidate(); }
        private void RefreshAnnotationMap()
        {
            regionCaptionHits.Clear();
            annotationMap = annotations == null ? new Dictionary<string, List<FactoryTfAnnotationRegion>>() : annotations.MatchMap(points);
            visibleRegions = annotationMap.Values.SelectMany(r => r).Distinct().ToList();
        }
        private void RememberAxes(Rectangle area, double xmax, double ymax)
        { annotationArea = area; annotationXmax = xmax; annotationYmax = ymax; }
        private RectangleF RegionPixels(FactoryTfRegionGeometry geometry)
        {
            double left = annotationArea.Left + geometry.MinimumFrequencyMHz / annotationXmax * annotationArea.Width,
                right = annotationArea.Left + geometry.MaximumFrequencyMHz / annotationXmax * annotationArea.Width,
                top = annotationArea.Bottom - geometry.MaximumTimeUs / annotationYmax * annotationArea.Height,
                bottom = annotationArea.Bottom - geometry.MinimumTimeUs / annotationYmax * annotationArea.Height;
            return RectangleF.FromLTRB((float)Math.Max(-100000, Math.Min(100000, left)), (float)Math.Max(-100000, Math.Min(100000, top)),
                (float)Math.Max(-100000, Math.Min(100000, right)), (float)Math.Max(-100000, Math.Min(100000, bottom)));
        }
        private static Color RegionColor(FactoryTfAnnotationRegion region)
        {
            return region.Kind == FactoryTfLabelKind.SuspectedDefect ? Color.FromArgb(185, 72, 79) :
                region.Kind == FactoryTfLabelKind.SuspectedInterference ? Color.FromArgb(44, 112, 192) : Color.FromArgb(113, 83, 165);
        }
        private void DrawAnnotationRegions(Graphics g)
        {
            regionCaptionHits.Clear();
            var saved = g.Save(); g.SetClip(annotationArea); g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                foreach (var region in visibleRegions)
                {
                    var bounds = RegionPixels(region.Geometry); Color color = RegionColor(region);
                    using (var fill = new SolidBrush(Color.FromArgb(18, color))) using (var pen = new Pen(color, region.Id == HighlightedRegionId ? 3 : 1.7f))
                    {
                        if (region.Geometry.Shape == FactoryTfRegionShape.Rectangle) { g.FillRectangle(fill, bounds); g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height); }
                        else { g.FillEllipse(fill, bounds); g.DrawEllipse(pen, bounds); }
                    }
                    int x = Math.Max(annotationArea.Left + 2, Math.Min(annotationArea.Right - 80, (int)bounds.Left)),
                        y = Math.Max(annotationArea.Top + 1, Math.Min(annotationArea.Bottom - Font.Height - 2, (int)bounds.Top));
                    var captionBounds = new Rectangle(x, y, Math.Max(80, Math.Min(210, annotationArea.Right - x)), Font.Height + 4);
                    regionCaptionHits[region.Id] = captionBounds;
                    TextRenderer.DrawText(g, region.Name + " · 人工", Font, captionBounds, color,
                        Color.FromArgb(248, 249, 252), TextFormatFlags.EndEllipsis);
                    RegionRenderCount++;
                }
                if (regionDragging || pendingRegion != null)
                {
                    var draft = regionDragging ? FactoryTfRegionGeometry.FromDrag(annotationArea, dragFirst, dragLast, DrawingMode == FactoryTfDrawMode.Oval, annotationXmax, annotationYmax) : pendingRegion.Geometry;
                    if (draft != null) using (var pen = new Pen(Color.DarkOrange, 2) { DashStyle = DashStyle.Dash })
                    { var bounds = RegionPixels(draft); if (draft.Shape == FactoryTfRegionShape.Rectangle) g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width, bounds.Height); else g.DrawEllipse(pen, bounds); }
                    if (!regionDragging && pendingRegion != null)
                    {
                        var matches = pendingRegion.Points.Select(point => { FactoryPulseMatch match; return recognition.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out match) ? match : new FactoryPulseMatch(); });
                        string text = FactoryPulseLabels.Selection(matches).Text + " · 右键添加标签";
                        TextRenderer.DrawText(g, text, Font, new Rectangle(annotationArea.Left, annotationArea.Top + 2, annotationArea.Width, Font.Height + 5), Color.DarkSlateBlue, Color.White, TextFormatFlags.EndEllipsis);
                    }
                }
            }
            finally { g.Restore(saved); }
        }
        internal bool BeginRegionDrag(Point first)
        {
            if (!Enabled || DrawingMode == FactoryTfDrawMode.Browse || !annotationArea.Contains(first) || !points.Any(p => p.Pulse.Valid)) return false;
            dragFirst = dragLast = first; regionDragging = true; Capture = true; Invalidate(); return true;
        }
        internal void ContinueRegionDrag(Point last) { if (regionDragging) { dragLast = last; Invalidate(); } }
        internal void EndRegionDrag(Point last)
        {
            if (!regionDragging) return;
            var geometry = FactoryTfRegionGeometry.FromDrag(annotationArea, dragFirst, last, DrawingMode == FactoryTfDrawMode.Oval, annotationXmax, annotationYmax);
            CancelRegionDrag(); if (!Enabled || geometry == null) return;
            var selected = points.Where(p => p.Pulse.Valid && geometry.Contains(p.Pulse.EquivalentBandwidthMHz, p.Pulse.EquivalentTimeUs)).ToList();
            pendingRegion = new FactoryTfRegionEvent { Geometry = geometry, Points = selected }; Invalidate();
        }
        internal void AddPendingLabel()
        { if (!Enabled || pendingRegion == null) return; var selected = pendingRegion; var handler = RegionSelected; if (handler != null) handler(this, selected); pendingRegion = null; Invalidate(); }
        internal void CancelRegionDrag() { regionDragging = false; pendingRegion = null; Capture = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) BeginRegionDrag(e.Location); else if (e.Button == MouseButtons.Right && Enabled) ShowLabelMenu(e.Location); }
        private void ShowLabelMenu(Point at)
        {
            PrepareLabelMenu(at);
            if (labelMenu.Items.Count > 0) labelMenu.Show(this, at);
        }
        internal ContextMenuStrip PrepareLabelMenu(Point at)
        {
            FactoryLabelMenus.Clear(labelMenu.Items);
            if (!Enabled) return labelMenu;
            var editable = new List<FactoryTfAnnotationRegion>();
            if (pendingRegion != null)
            {
                labelMenu.Items.Add("添加选区标签…", null, (s,e) => AddPendingLabel()); labelMenu.Items.Add("取消选区", null, (s,e) => CancelRegionDrag());
                editable = EditableLabels(pendingRegion.Points);
            }
            else
            {
                var hit = hits.OrderBy(h => Math.Pow(h.Item1.X - at.X, 2) + Math.Pow(h.Item1.Y - at.Y, 2)).FirstOrDefault();
                if (hit != null && Math.Pow(hit.Item1.X - at.X, 2) + Math.Pow(hit.Item1.Y - at.Y, 2) < 100)
                {
                    var point = hit.Item2;
                    labelMenu.Items.Add(new ToolStripMenuItem(RecognitionText(point)) { Enabled = false });
                    labelMenu.Items.Add("添加此脉冲标签…", null, (s,e) => RequestPoints(new List<FactoryTfDisplayPoint> { point }));
                    editable = EditableLabels(new[] { point });
                    int group; if (clustering.Groups.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out group) && group >= 0)
                        labelMenu.Items.Add("添加本视图簇 " + (group + 1) + " 的标签…", null, (s,e) => RequestPoints(clustering.Members[group]));
                }
                if (annotationArea.Width > 0 && annotationArea.Height > 0 && annotationArea.Contains(at))
                {
                    double frequency = (double)(at.X - annotationArea.Left) / annotationArea.Width * annotationXmax,
                        time = (double)(annotationArea.Bottom - at.Y) / annotationArea.Height * annotationYmax;
                    editable.AddRange(visibleRegions.Where(r => r.Geometry.Contains(frequency, time) ||
                        (regionCaptionHits.ContainsKey(r.Id) && regionCaptionHits[r.Id].Contains(at))));
                }
            }
            FactoryLabelMenus.AddEdits(labelMenu.Items, editable, region =>
            { var handler = LabelEditRequested; if (Enabled && handler != null) handler(this, new FactoryTfLabelEditEvent { Region = region }); });
            return labelMenu;
        }
        private List<FactoryTfAnnotationRegion> EditableLabels(IEnumerable<FactoryTfDisplayPoint> selected)
        { return FactoryPulseLabels.EditableLabels(annotations, selected, point => { FactoryPulseMatch match; return recognition.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out match) ? match : null; }); }
        private void RequestPoints(List<FactoryTfDisplayPoint> selected)
        { pendingRegion = new FactoryTfRegionEvent { Geometry = FactoryMainForm.BoundsFor(selected), Points = selected }; AddPendingLabel(); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); if (regionDragging) { ContinueRegionDrag(e.Location); return; }
            var hit = hits.OrderBy(h => Math.Pow(h.Item1.X - e.X, 2) + Math.Pow(h.Item1.Y - e.Y, 2)).FirstOrDefault();
            List<FactoryTfAnnotationRegion> labels;
            string text = hit != null && Math.Pow(hit.Item1.X - e.X, 2) + Math.Pow(hit.Item1.Y - e.Y, 2) < 100 && annotationMap.TryGetValue(FactoryTfAnnotationDocument.PointKey(hit.Item2), out labels)
                ? String.Join("\n", labels.Select(r => r.Name + "（" + FactoryTfAnnotationRegion.KindText(r.Kind) + "，人工）\n缺陷：" + r.Defect + "\n可能干扰源：" + r.PossibleInterferenceSource)) : "";
            if (hit != null && Math.Pow(hit.Item1.X - e.X, 2) + Math.Pow(hit.Item1.Y - e.Y, 2) < 100) text += "\n" + RecognitionText(hit.Item2);
            if (annotationTip.GetToolTip(this) != text) annotationTip.SetToolTip(this, text);
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) EndRegionDrag(e.Location); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && regionDragging) CancelRegionDrag(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) CancelRegionDrag(); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        { if (keyData == Keys.Escape && regionDragging) { CancelRegionDrag(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
        protected override void Dispose(bool disposing) { if (disposing) { annotationTip.Dispose(); labelMenu.Dispose(); } base.Dispose(disposing); }
    }
}
