using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    /// <summary>Single LabVIEW .NET Container control. All matrix columns and row indices are zero-based.</summary>
    [ToolboxItem(true)]
    public partial class UnifiedSpectrumControl : UserControl
    {
        static readonly string[] ModeNames = { "Q-L", "N-L", "Q-PHI", "N-PHI", "F-T", "U-Q-L", "U-N-L", "PHI-Q-L", "PHI-N-L", "n-Q-U", "n-N-U", "PRPD", "L-PRPD" };
        readonly bool competitionProfile, completeReadOnlyModes;
        string ModeCaption(int value) { return competitionProfile&&!completeReadOnlyModes ? value==7?"U-Q-PHI":value==8?"U-N-PHI":ModeNames[value] : ModeNames[value]; }
        readonly ProjectMetadata metadata = new ProjectMetadata();
        readonly HashSet<int> deleted = new HashSet<int>();
        readonly Dictionary<int, ViewState> states = new Dictionary<int, ViewState>();
        double[,] source = new double[0, 36];
        SpectrumView view;
        double currentLength = double.NaN;
        double[] currentJoints = new double[0];
        int[] visibleRows = new int[0], displayedPhases = new int[0];
        string projectPath = "";
        bool english, acceptedOnly;
        bool[] visible = { true, true, true };
        int mode, phaseFilter = -1, positionBins = 100, phaseBins = 72, chargeBins = 64;
        bool showJoints = true;
        int[] lastRows = new int[0], hitRows = new int[0];
        double[,] lastMatrix = new double[0, 36];
        double[] hitIds = new double[0];

        public UnifiedSpectrumControl() : this(false) { }
        internal UnifiedSpectrumControl(bool competition) : this(competition,false) { }
        // Full analysis spectra with competition's read-only actions. This does
        // not enable editing, automatic conclusions, learning or model calls.
        internal UnifiedSpectrumControl(bool competition,bool completeModes)
        {
            competitionProfile=competition;
            completeReadOnlyModes=competition&&completeModes;
            if(competition){mode=11;automaticConclusions=false;showConclusionAnnotations=false;showJoints=false;}
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = HVX.PdReview.ReviewTheme.Surface; ForeColor = HVX.PdReview.ReviewTheme.Ink;
            var ownedFont=HVX.PdReview.ReviewTheme.RegularFont();Font=ownedFont;Disposed+=(s,e)=>ownedFont.Dispose();
            MinimumSize = new Size(0, 320); Size = new Size(1000, 600); TabStop = true;
            InitializeInteraction(); InitializeToolbar(); Rebuild(true);
        }

        [Category("Data"), Description("0 Q-L, 1 N-L, 2 Q-PHI, 3 N-PHI, 4 F-T, 5..8 3D, 9..10 2D test-order dual axes, 11 PRPD count heatmap, 12 L-PRPD")]
        public int DisplayModeIndex
        {
            get { return mode; }
            set { if (value < 0 || value >= ModeNames.Length || competitionProfile&&!ToolbarModeIndices.Contains(value)) throw new ArgumentOutOfRangeException("DisplayModeIndex"); if (mode == value) return; FlushReviewPreview();CancelGesture(); mode = value; ClearHit(); Rebuild(false);if(competitionProfile)NotifyHost(ViewChanged); }
        }
        [Browsable(false)] public string DisplayModeName { get { return ModeCaption(mode); } }
        [Category("Language"), DefaultValue(false)]
        public bool EnglishVersion { get { return english; } set { if(english==value)return; LanguageIndex=value?1:0; } }
        public void SetLanguage(bool isEnglish) { EnglishVersion = isEnglish; }
        public string GetPhaseLabel(int phase)
        { return phase == 0 || phase == -1 ? "ALL" : phase >= 1 && phase <= 3 ? (english ? "L" + phase : new[] { "A", "B", "C" }[phase - 1]) : "?"; }
        [Category("Data"), Description("-1 ALL of visible phases, 0 input ALL rows only, 1 A/L1, 2 B/L2, 3 C/L3")]
        public int PhaseFilterIndex
        {
            get { return phaseFilter; }
            set { if (value < -1 || value > 3) throw new ArgumentOutOfRangeException("PhaseFilterIndex"); if (phaseFilter == value) return; CancelGesture(); phaseFilter = value; states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true);if(completeReadOnlyModes)NotifyHost(ViewChanged); }
        }
        [Browsable(false)]
        public bool[] PhaseVisibility { get { return (bool[])visible.Clone(); } set { SetPhaseVisibility(value); } }
        public void SetPhaseVisibility(bool[] values)
        {
            if (values == null || values.Length != 3) throw new ArgumentException("PhaseVisibility must contain exactly three booleans.");
            if (visible.SequenceEqual(values)) return;
            CancelGesture(); visible = (bool[])values.Clone(); states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true);
            if(completeReadOnlyModes)NotifyHost(ViewChanged);
        }
        [Category("Data"), DefaultValue(false)]
        public bool AcceptedOnly { get { return acceptedOnly; } set { if (acceptedOnly == value) return; CancelGesture(); acceptedOnly = value; states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true); } }
        [Category("Project")]
        public string ProjectPath
        {
            get { return projectPath; }
            set { string path = (value ?? "").Trim(); if (string.Equals(path, projectPath, StringComparison.OrdinalIgnoreCase)) return; projectPath = path; metadata.Load(path); operatingVoltageKv=metadata.RatedPhaseVoltageKv; ClearProjectSelection(); states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true); NotifyRangeContextChanged(); }
        }
        public void ReloadProjectMetadata() { metadata.Load(projectPath); operatingVoltageKv=metadata.RatedPhaseVoltageKv; states.Clear(); ClearDataViews(); ClearHit(); Rebuild(true); NotifyRangeContextChanged(); }
        [Browsable(false)] public string ProjectMetadataStatus { get { return metadata.Status; } }
        [Browsable(false)] public string ProjectMetadataDetails { get { return metadata.Details; } }
        [Browsable(false)] public int ProjectMetadataReadCount { get { return metadata.ReadCount; } }
        [Browsable(false)] public double CurrentCableLengthM { get { return currentLength; } }
        [Browsable(false)] public double[] CurrentJointPositionsM { get { return (double[])currentJoints.Clone(); } }
        [Category("Appearance"), DefaultValue(true)] public bool ShowJointMarkers { get { return showJoints; } set { if(showJoints==value)return; showJoints = value; Invalidate(); } }
        [Category("Binning"), DefaultValue(100)] public int PositionBinCount { get { return positionBins; } set { int n=CheckBins(value,100);if(positionBins==n)return;positionBins=n;states.Clear();ClearDataViews();Rebuild(true); } }
        [Category("Binning"), DefaultValue(72)] public int PhaseBinCount { get { return phaseBins; } set { int n=CheckBins(value,360);if(phaseBins==n)return;phaseBins=n;states.Clear();ClearDataViews();Rebuild(true); } }
        [Category("Binning"), DefaultValue(64)] public int ChargeBinCount { get { return chargeBins; } set { int n=CheckBins(value,240);if(chargeBins==n)return;chargeBins=n;states.Clear();ClearDataViews();Rebuild(true); } }
        static int CheckBins(int value, int max) { if (value < 1 || value > max) throw new ArgumentOutOfRangeException("binCount"); return value; }
        [Browsable(false)] public string StatusMessage { get { return view == null ? "" : view.Warning ?? ""; } }
        [Browsable(false)] public string ControlBuildVersion { get { return "1.3.0 20260921"; } }
        [Browsable(false)] public string LoadedAssemblyPath { get { return typeof(UnifiedSpectrumControl).Assembly.Location; } }
        [Browsable(false)] public int SourceRowCount { get { return source.GetLength(0); } }
        [Browsable(false)] public int VisiblePointCount { get { return view.Points.Count; } }
        [Browsable(false)] public int VisibleSourceRowCount { get { return VisibleRows().Length; } }
        [Browsable(false)] public int[] LastSelectedSourceRowIndices { get { return (int[])lastRows.Clone(); } }
        [Browsable(false)] public double[,] LastSelectedPulseMatrix { get { return (double[,])lastMatrix.Clone(); } }
        [Browsable(false)] public double SelectedPdMapId { get; private set; } = -1;
        [Browsable(false)] public int SelectedSourceRowIndex { get; private set; } = -1;
        [Browsable(false)] public double SelectedRecordId { get; private set; } = -1;
        [Browsable(false)] public double SelectedLocId { get; private set; } = -1;
        [Browsable(false)] public int[] DoubleClickedSourceRowIndices { get { return (int[])hitRows.Clone(); } }
        [Browsable(false)] public double[] DoubleClickedPdMapIds { get { return (double[])hitIds.Clone(); } }
        [Browsable(false)] public int PdMapSelectionSequence { get; private set; }
        public event EventHandler PdMapSelected;
        public event EventHandler ViewChanged;
        public event EventHandler RegionActionCompleted;
        public event EventHandler SelectionShapeChanged;

        [Browsable(false), Description("Write only when analysis results change. Display properties reuse the cached snapshot.")]
        public double[,] ResultParameterMatrix { get { return (double[,])source.Clone(); } set { LoadDataMatrix(value); } }

        public void LoadDataMatrix(double[,] parameterMatrix)
        {
            if (parameterMatrix == null || parameterMatrix.GetLength(1) < 36)
                throw new ArgumentException("A two-dimensional matrix with at least 36 columns is required.");
            dataLoaded=true;
            if (MatrixEquals(source, parameterMatrix)) { lastError="";DropDrawingCaches(); Invalidate(); return; }
            var snapshot=(double[,])parameterMatrix.Clone();
            CancelGesture(); source = snapshot; MatrixCloneCount++; ClearReviewVisibilityForNewSource(); deleted.Clear(); states.Clear(); ClearDataViews(); ClearHit(); lastError="";
            lastRows = new int[0]; lastMatrix = new double[0, source.GetLength(1)]; Rebuild(true);
        }
        public void LoadProjectResults(string path, double[,] parameterMatrix) { UpdateInputs(path,parameterMatrix,mode,english,phaseFilter,visible,acceptedOnly); }
        static bool MatrixEquals(double[,] a, double[,] b)
        {
            if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1)) return false;
            for (int r = 0; r < a.GetLength(0); r++) for (int c = 0; c < a.GetLength(1); c++) if (!a[r, c].Equals(b[r, c])) return false;
            return true;
        }
        static bool Finite(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }
        double CableLength() { return CableLength(source); }
        double CableLength(double[,] matrix)
        {
            var lengths = new List<double>();
            bool anyAbc = false;
            for (int r=0;r<matrix.GetLength(0);r++) if(matrix[r,0]>=1 && matrix[r,0]<=3 && matrix[r,0]==Math.Truncate(matrix[r,0])) anyAbc=true;
            for (int r = 0; r < matrix.GetLength(0); r++)
            {
                double p = matrix[r, 0], len = matrix[r, 5];
                bool match = phaseFilter >= 0 ? p == phaseFilter : (p == 0 && !anyAbc && visible.Any(v=>v)) || (p >= 1 && p <= 3 && p == (int)p && visible[(int)p - 1]);
                if (match && Finite(len) && len > 0) lengths.Add(len);
            }
            return lengths.Count > 0 ? lengths.Max() : CableMarkerLength();
        }
        int MetadataPhase() { if (phaseFilter >= 0) return phaseFilter; var shown=Enumerable.Range(1,3).Where(p=>visible[p-1]).ToArray(); return shown.Length==1?shown[0]:0; }
        void ClearHit() { SelectedPdMapId = SelectedRecordId = SelectedLocId = -1; SelectedSourceRowIndex = -1; hitRows = new int[0]; hitIds = new double[0]; }
        void ClearProjectSelection() { ClearReviewVisibilityForNewSource(); deleted.Clear(); lastRows = new int[0]; lastMatrix = new double[0, source.GetLength(1)]; }
        void Rebuild(bool resetView)
        {
            CancelGesture(); view = GetDataView();
            visibleRows=view.Points.SelectMany(p=>p.SourceRows).Distinct().OrderBy(r=>r).ToArray();
            displayedPhases=visibleRows.Select(r=>(int)source[r,0]).Distinct().OrderByDescending(p=>p).ToArray();
            ViewState state;
            if (!states.TryGetValue(mode, out state)) { state = new ViewState(); states.Add(mode, state); resetView = true; }
            if (resetView || state.ReviewRangeDirty) SetDefaultDisplayRange(state);
            RefreshLocationClusters();
            if(!reviewPreviewDirty)RefreshConclusionAnnotations();
            RunSelfCheck(true);
            SyncToolbar();
            Invalidate();
        }
        // Lazy-create ViewState for current mode before Rebuild can run (avoids KeyNotFoundException
        // when partials/toolbar/paint touch State after states.Clear() or on a freshly switched mode).
        ViewState State
        {
            get
            {
                ViewState state;
                if (!states.TryGetValue(mode, out state))
                {
                    state = new ViewState();
                    states.Add(mode, state);
                }
                return state;
            }
        }
        public void ResetView() { ResetCameraOrientation(false); Rebuild(true); NotifyHost(ViewChanged); }
        public void RestoreDeletedRows() { if(deleted.Count==0)return; deleted.Clear(); ClearDataViews(); Rebuild(true); }
        int[] VisibleRows() { return visibleRows; }
        double[,] RowsMatrix(int[] rows)
        {
            int cols = source.GetLength(1); var result = new double[rows.Length, cols];
            for (int r = 0; r < rows.Length; r++) for (int c = 0; c < cols; c++) result[r, c] = source[rows[r], c]; return result;
        }
        double[,] PresentedRowsMatrix(int[] rows)
        {var result=RowsMatrix(rows);if(reviewPositionProjection!=null)for(int i=0;i<rows.Length;i++)result[i,13]=reviewPositionProjection(rows[i],result[i,13]);return result;}
        /// <summary>Each plot row is X,Y,Z,phase,count; source members are accessible separately.</summary>
        public double[,] GetDisplayedPoints()
        {
            var result = new double[view.Points.Count, 5];
            for (int i = 0; i < view.Points.Count; i++) { var p = view.Points[i]; result[i, 0] = p.X; result[i, 1] = p.Y; result[i, 2] = p.Z; result[i, 3] = p.Phase; result[i, 4] = p.SourceRows.Length; } return result;
        }
        public int[] GetPointSourceRows(int pointIndex) { return (int[])view.Points[pointIndex].SourceRows.Clone(); }
        public void ExportDataCsv(string path) { ExportChecked(() => WriteCsv(path, VisibleRows())); }
        public void ExportSelectedDataCsv(string path) { ExportChecked(() => WriteCsv(path, SelectedRows())); }
        void WriteCsv(string path, int[] rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
                WriteCsv(writer,rows);
        }
        void WriteCsv(TextWriter writer, int[] rows)
        {
            {
                writer.Write("SourceRow,PhaseLabel,PDmapID"); for (int c = 0; c < source.GetLength(1); c++) writer.Write(",Column" + c); writer.WriteLine(",RawPositionM,CoordinateReference");
                foreach (int r in rows) { writer.Write(r.ToString(CultureInfo.InvariantCulture) + "," + GetPhaseLabel((int)source[r, 0]) + "," + source[r, 9].ToString("R", CultureInfo.InvariantCulture)); for (int c = 0; c < source.GetLength(1); c++) {double value=c==13&&reviewPositionProjection!=null?reviewPositionProjection(r,source[r,c]):source[r,c];writer.Write("," + value.ToString("R", CultureInfo.InvariantCulture));} writer.WriteLine(","+source[r,13].ToString("R",CultureInfo.InvariantCulture)+",displayed DUT coordinate; raw timing retained"); }
            }
        }
        public void SavePng(string path, int width, int height)
        { ExportChecked(() => SavePngCore(path, width, height)); }
        void SavePngCore(string path, int width, int height)
        {
            if (width < 360 || height < 260 || width > 8192 || height > 8192) throw new ArgumentOutOfRangeException("imageSize");
            if((long)width*height>16000000)throw new ArgumentOutOfRangeException("imageSize","Maximum 16 million pixels.");
            if(!RunSelfCheck(true))throw new InvalidOperationException(lastError);
            using (var bmp = new Bitmap(width, height)) using (var g = Graphics.FromImage(bmp)) { Render(g, new Rectangle(0, 0, width, height), false); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { DropDrawingCaches(); if (regionMenu != null) regionMenu.Dispose(); if (tooltip != null) tooltip.Dispose(); if(chargeCountToggle!=null)chargeCountToggle.Dispose(); } base.Dispose(disposing); }
        sealed class ViewState
        {
            public double X0, X1, Y0, Y1, AutoX0, AutoX1, AutoY0, AutoY1;
            public bool HasAutomaticRange, ReviewRangeDirty;
            public readonly List<SelectionRegion> Regions = new List<SelectionRegion>();
        }
        sealed class SelectionRegion { public double X0, X1, Y0, Y1; public bool Circle; public bool Contains(double x, double y) { if (x < X0 || x > X1 || y < Y0 || y > Y1) return false; if (!Circle) return true; double dx = (2 * x - X0 - X1) / (X1 - X0), dy = (2 * y - Y0 - Y1) / (Y1 - Y0); return dx * dx + dy * dy <= 1.0000000001; } }
    }
}
