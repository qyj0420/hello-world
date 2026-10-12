using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // Entries always refer to control-owned immutable matrix snapshots. A small
        // LRU bounds memory when a large project is switched through all modes.
        readonly Dictionary<int, SpectrumView> dataViews = new Dictionary<int, SpectrumView>();
        readonly List<int> recentModes = new List<int>();
        bool metadataGeometryDirty = true, renderingBase;
        bool capacitorStatisticsMode;
        [Browsable(false)] public bool CapacitorStatisticsMode {
            get { return capacitorStatisticsMode; }
            set {if(value==capacitorStatisticsMode)return;capacitorStatisticsMode=value;states.Clear();ClearDataViews();ClearHit();Rebuild(true);}
        }
        Bitmap baseImage;
        string baseImageKey, hitKey;
        readonly Dictionary<long, List<HitPoint>> hitGrid = new Dictionary<long, List<HitPoint>>();
        string lastError = "", selfCheckCode = "NotRun";
        bool selfCheckPassed;
        int notificationDepth;
        sealed class HitPoint { public PointF Pixel; public SpectrumPoint Point; }

        [Browsable(false)] public string LastError { get { return lastError; } }
        [Browsable(false)] public string SelfCheckStatusCode { get { return selfCheckCode; } }
        [Browsable(false)] public bool SelfCheckPassed { get { return selfCheckPassed; } }
        [Browsable(false)] public int SelfRepairCount { get; private set; }
        [Browsable(false)] public int DataBuildCount { get; private set; }
        [Browsable(false)] public int DataCacheHitCount { get; private set; }
        [Browsable(false)] public int MatrixCloneCount { get; private set; }
        [Browsable(false)] public int BaseRenderCount { get; private set; }
        [Browsable(false)] public int HitIndexBuildCount { get; private set; }
        [Browsable(false)] public int CachedModeCount { get { return dataViews.Count; } }
        [Browsable(false)] public double LastDataBuildMilliseconds { get; private set; }

        void ClearDataViews()
        {
            InvalidateSummaryStatistics();
            dataViews.Clear(); recentModes.Clear(); metadataGeometryDirty = true;
            DropDrawingCaches();
        }
        void DropDrawingCaches()
        {
            if (baseImage != null) { baseImage.Dispose(); baseImage = null; }
            baseImageKey = hitKey = null; hitGrid.Clear();
        }
        SpectrumView GetDataView()
        {
            if (metadataGeometryDirty)
            {
                currentLength = capacitorStatisticsMode?0:CableLength();
                currentJoints = capacitorStatisticsMode?new double[0]:CableMarkerJoints();
                metadataGeometryDirty = false;
            }
            SpectrumView found;
            if (dataViews.TryGetValue(mode, out found)) DataCacheHitCount++;
            else
            {
                var clock = Stopwatch.StartNew();
                found = competitionProfile&&!completeReadOnlyModes?SpectrumData.BuildCompetition(source,mode,visible,phaseFilter,acceptedOnly,phaseBins,chargeBins,currentLength,DrawingReviewVisibility(),useCompensatedCharge):SpectrumData.Build(source, mode, visible, phaseFilter, acceptedOnly, deleted, positionBins, phaseBins, chargeBins, currentLength, DrawingReviewVisibility(), reviewCountRange, reviewPositionProjection, useCompensatedCharge,capacitorStatisticsMode);
                clock.Stop(); LastDataBuildMilliseconds = clock.Elapsed.TotalMilliseconds; DataBuildCount++;
                if (recentModes.Count >= 3) { dataViews.Remove(recentModes[0]); recentModes.RemoveAt(0); }
                dataViews.Add(mode, found);
            }
            recentModes.Remove(mode); recentModes.Add(mode);
            return found;
        }
        static bool ValidRange(double min, double max) { return Finite(min) && Finite(max) && max > min && Finite(max - min); }

        /// <summary>Repairs derived display state only. Never changes source measurements or acceptance flags.</summary>
        public bool RunSelfCheck(bool repairDisplayState)
        {
            selfCheckPassed = false;
            if (view == null || source.GetLength(1) < 36) { selfCheckCode = "InvalidSchema"; return false; }
            if (!ValidRange(view.XMin, view.XMax) || !ValidRange(view.YMin, view.YMax) || !ValidRange(view.ZMin, view.ZMax))
            { selfCheckCode = "InvalidDataRange"; lastError = L("The data range cannot be represented safely. Check extreme input values.","数据范围无法安全绘制，请检查输入中的极端数值。"); return false; }
            foreach (var p in view.Points)
            {
                if (!Finite(p.X) || !Finite(p.Y) || !Finite(p.Z) || p.Phase < 0 || p.Phase > 3 || p.SourceRows == null || p.SourceRows.Any(r => r < 0 || r >= source.GetLength(0)))
                { selfCheckCode = "InvalidDerivedPoint"; lastError = L("Invalid derived coordinate or source-row mapping.","派生坐标或源行映射无效。"); return false; }
            }
            bool repaired = false;
            if (!ValidRange(State.X0, State.X1) || !ValidRange(State.Y0, State.Y1))
            {
                if (!repairDisplayState) { selfCheckCode = "InvalidViewRange"; return false; }
                SetDefaultDisplayRange(State);
                CancelGesture(); DropDrawingCaches(); repaired = true;
            }
            var badRegions = State.Regions.Where(r => !ValidRange(r.X0, r.X1) || !ValidRange(r.Y0, r.Y1)).ToArray();
            if (badRegions.Length > 0)
            {
                if (!repairDisplayState) { selfCheckCode = "InvalidRegion"; return false; }
                foreach (var r in badRegions) State.Regions.Remove(r); repaired = true;
            }
            if (SelectedSourceRowIndex >= 0 && !visibleRows.Contains(SelectedSourceRowIndex))
            {
                if (!repairDisplayState) { selfCheckCode = "StaleSelection"; return false; }
                ClearHit(); repaired = true;
            }
            if (repaired) { SelfRepairCount++; Invalidate(); }
            selfCheckCode = repaired ? "DisplayStateRepaired" : "OK"; selfCheckPassed = true;
            return true;
        }
        public void ClearLastError() { lastError = ""; Invalidate(); }

        /// <summary>Invalid input reports false and retains the last complete, verified snapshot.</summary>
        public bool TryLoadDataMatrix(double[,] matrix)
        {
            try
            {
                if (matrix == null || matrix.GetLength(1) < 36) throw new ArgumentException("Matrix needs at least 36 columns.");
                if (!MatrixEquals(source, matrix))
                {
                    var candidate = SpectrumData.Build(matrix, mode, visible, phaseFilter, acceptedOnly,
                        new HashSet<int>(), positionBins, phaseBins, chargeBins, CableLength(matrix));
                    if (!ValidRange(candidate.XMin, candidate.XMax) || !ValidRange(candidate.YMin, candidate.YMax) || !ValidRange(candidate.ZMin, candidate.ZMax)
                        || candidate.Points.Any(p => !Finite(p.X) || !Finite(p.Y) || !Finite(p.Z)))
                        throw new ArgumentException("Input data range cannot be represented safely.");
                }
                LoadDataMatrix(matrix); lastError = ""; return RunSelfCheck(true);
            }
            catch (ArgumentException ex) { lastError = (L("Input rejected; previous snapshot retained. ","输入被拒绝，保留上次完整数据。")) + ex.Message; Invalidate(); return false; }
        }

        /// <summary>One atomic validation/commit for frequently updated LabVIEW ports.</summary>
        public void UpdateInputs(string path, double[,] matrix, int displayModeIndex, bool englishVersion, int phaseFilterIndex, bool[] phaseVisibility, bool onlyAccepted)
        {
            if (matrix == null || matrix.GetLength(1) < 36) throw new ArgumentException("Matrix needs at least 36 columns.");
            if (displayModeIndex < 0 || displayModeIndex > 12 || competitionProfile&&!ToolbarModeIndices.Contains(displayModeIndex)) throw new ArgumentOutOfRangeException("displayModeIndex");
            if (phaseFilterIndex < -1 || phaseFilterIndex > 3) throw new ArgumentOutOfRangeException("phaseFilterIndex");
            if (phaseVisibility == null || phaseVisibility.Length != 3) throw new ArgumentException("Exactly three visibility flags are required.");
            string newPath = (path ?? "").Trim();
            bool matrixChanged = !MatrixEquals(source, matrix), pathChanged = !string.Equals(projectPath, newPath, StringComparison.OrdinalIgnoreCase);
            bool filterChanged = phaseFilter != phaseFilterIndex || !visible.SequenceEqual(phaseVisibility) || acceptedOnly != onlyAccepted;
            bool modeChanged = mode != displayModeIndex;
            // Clone before changing state: allocation failure cannot mix old data with a new project.
            double[,] snapshot = matrixChanged ? (double[,])matrix.Clone() : source;
            dataLoaded=true;
            if (matrixChanged || pathChanged || filterChanged || modeChanged) CancelGesture();
            if (pathChanged) { projectPath = newPath; metadata.Load(newPath); ClearProjectSelection(); }
            if (matrixChanged) { source = snapshot; MatrixCloneCount++; ClearReviewVisibilityForNewSource(); deleted.Clear(); lastRows = new int[0]; lastMatrix = new double[0, source.GetLength(1)]; }
            phaseFilter = phaseFilterIndex; visible = (bool[])phaseVisibility.Clone(); acceptedOnly = onlyAccepted;
            mode = displayModeIndex; english = englishVersion; UpdateMenuLanguage();
            if (matrixChanged || pathChanged || filterChanged) { states.Clear(); ClearDataViews(); ClearHit(); }
            else if (modeChanged) ClearHit();
            if (matrixChanged || pathChanged || filterChanged || modeChanged) Rebuild(matrixChanged || pathChanged || filterChanged);
            else { RefreshConclusionAnnotations(); SyncToolbar(); Invalidate(); }
        }

        string GeometryKey()
        {
            Rectangle plot=PlotArea(ClientRectangle);
            return RuntimeHelpers.GetHashCode(view) + ":" + ClientSize.Width + ":" + ClientSize.Height + ":" + plot.X + ":" + plot.Y + ":" + plot.Width + ":" + plot.Height + ":" +
                State.X0.ToString("R", CultureInfo.InvariantCulture) + ":" + State.X1.ToString("R", CultureInfo.InvariantCulture) + ":" +
                State.Y0.ToString("R", CultureInfo.InvariantCulture) + ":" + State.Y1.ToString("R", CultureInfo.InvariantCulture) + ":" + CameraGeometryKey;
        }
        void PaintWithCache(Graphics g)
        {
            if (view == null || !selfCheckPassed || !ValidRange(State.X0, State.X1) || !ValidRange(State.Y0, State.Y1))
            {
                if (!RunSelfCheck(true)) { DrawFailure(g); return; }
            }
            // No file I/O, input-matrix cloning or data aggregation in this path.
            string key = GeometryKey() + ":" + languageIndex + ":" + english + ":" + showJoints + ":" + dataLoaded + ":" + lastError + ":" + BackColor.ToArgb() + ":" + ForeColor.ToArgb() + ":" + Font.ToString();
            try
            {
                if (baseImage == null || baseImageKey != key)
                {
                    if (baseImage != null) { baseImage.Dispose(); baseImage = null; }
                    if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
                    if ((long)ClientSize.Width * ClientSize.Height > 16000000) { lastError = "Control image exceeds 16 million pixels."; DrawFailure(g); return; }
                    var next = new Bitmap(ClientSize.Width, ClientSize.Height);
                    try { using (var painter = Graphics.FromImage(next)) { renderingBase = true; Render(painter, ClientRectangle, false); } }
                    catch { next.Dispose(); throw; }
                    finally { renderingBase = false; }
                    baseImage = next; baseImageKey = key; BaseRenderCount++;
                }
                g.DrawImageUnscaled(baseImage, 0, 0);
                // Draw GDI text directly on the destination surface. Baking it
                // into a default-DPI bitmap degrades header glyphs on HiDPI screens.
                DrawSpectrumHeader(g,ClientRectangle,null);
                DrawInteractionOverlay(g);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ExternalException || ex is OverflowException)
            { DropDrawingCaches(); lastError = ex.GetType().Name + ": " + ex.Message; selfCheckCode = "RenderFailed"; selfCheckPassed = false; DrawFailure(g); }
        }
        void DrawFailure(Graphics g)
        {
            g.Clear(BackColor); using (var ink = new SolidBrush(Color.Firebrick))
                g.DrawString((L("Unable to display. ","无法显示。")) + lastError + " [" + selfCheckCode + "]", Font, ink, ClientRectangle);
        }
        void DrawInteractionOverlay(Graphics g)
        {
            if (!RegionSelectionEnabled) return;
            var area = PlotArea(ClientRectangle); var saved = g.Save(); g.SetClip(area);
            foreach (var r in State.Regions) { var a = Project(new SpectrumPoint { X = r.X0, Y = r.Y1 }, area); var b = Project(new SpectrumPoint { X = r.X1, Y = r.Y0 }, area); DrawRegion(g, new RectangleF(a.X, a.Y, b.X-a.X, b.Y-a.Y), r.Circle); }
            if (dragging) DrawRegion(g, DragBox(), shape == 1);
            if (SelectedSourceRowIndex >= 0) { var p = view.Points.FirstOrDefault(x => x.SourceRows.Contains(SelectedSourceRowIndex)); if (p != null) { var at = Project(p, area); using (var pen = new Pen(HVX.PdReview.ReviewTheme.Active, 2)) g.DrawEllipse(pen, at.X-7, at.Y-7, 14, 14); } }
            g.Restore(saved);
            DrawSelectionButtons(g,ClientRectangle);
        }

        static long GridCell(int x, int y) { return ((long)x << 32) ^ (uint)y; }
        void EnsureHitIndex()
        {
            string key = GeometryKey(); if (key == hitKey) return;
            hitGrid.Clear(); var area = PlotArea(ClientRectangle);
            foreach (var point in view.Points)
            {
                if (point.SourceRows.Length == 0) continue;
                var pixel = Project(point, area);
                if (!Finite(pixel.X) || !Finite(pixel.Y) || pixel.X < area.Left || pixel.X > area.Right || pixel.Y < area.Top || pixel.Y > area.Bottom) continue;
                long cell = GridCell((int)Math.Floor(pixel.X / 16), (int)Math.Floor(pixel.Y / 16)); List<HitPoint> bucket;
                if (!hitGrid.TryGetValue(cell, out bucket)) { bucket = new List<HitPoint>(); hitGrid.Add(cell, bucket); }
                bucket.Add(new HitPoint { Pixel = pixel, Point = point });
            }
            hitKey = key; HitIndexBuildCount++;
        }
        IEnumerable<HitPoint> HitCandidates(int x, int y)
        {
            EnsureHitIndex(); int cx = (int)Math.Floor(x / 16.0), cy = (int)Math.Floor(y / 16.0);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) { List<HitPoint> bucket; if (hitGrid.TryGetValue(GridCell(cx+dx,cy+dy), out bucket)) foreach (var hit in bucket) yield return hit; }
        }
        int NearestHoverRow(int x, int y)
        {
            double best = 64; int row = -1;
            foreach (var hit in HitCandidates(x,y)) { double d = (hit.Pixel.X-x)*(hit.Pixel.X-x)+(hit.Pixel.Y-y)*(hit.Pixel.Y-y); int r = hit.Point.SourceRows[0]; if (d < best || d == best && (row < 0 || r < row)) { row = r; best = d; } }
            return row;
        }
        void NotifyHost(EventHandler handlers)
        {
            // Event recipients may update properties. Suppress recursive notifications,
            // and isolate host callback failures from Windows paint/input dispatch.
            if (handlers == null || notificationDepth != 0) return;
            notificationDepth++;
            try { foreach (EventHandler handler in handlers.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch (Exception ex) { if (ex is OutOfMemoryException || ex is AccessViolationException) throw; lastError = "HostEvent: " + ex.Message; } }
            finally { notificationDepth--; if(lastError.Length>0)LayoutSpectrumToolbar(); }
        }
    }
}
