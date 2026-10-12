using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HVX.UnifiedSpectrum
{
    internal sealed class SpectrumConclusionMarker
    {
        internal int PhaseMask, ActionCode;
        internal double Position;
    }

    internal sealed class SpectrumConclusionResult
    {
        internal string Api, Summary;
        internal string[] Status;
        internal double[,] Locations;
        internal int ValidPulses;
        internal HVX.Location.AnalysisResult Evidence;
        internal double[][] Joints;
    }

    internal static class SpectrumTask15Bridge
    {
        internal const string LatestFile = "e15c7e4a92bf60e8135ac904.V1.0.15.dll";
        internal const string LegacyFile = "9c917f1e37ecb17bbec673c0.V1.0.15.dll";
        const string EngineType = "HVX.Location.LocatorEngine";

        internal static string ResolvePath(string configured)
        { return typeof(HVX.Location.EmbeddedConclusions).Assembly.Location; }

        internal static Type ResolveType(string path)
        { return typeof(HVX.Location.LocatorEngine); }

        internal static SpectrumConclusionMarker[] Analyze(Type type, double[,] matrix, double[][] joints,
            double operatingVoltage, double serviceYears, int bodyType, int jointType, bool english,
            int visibleMask, out string api)
        {
            var result = AnalyzeResult(type, matrix, joints, operatingVoltage, serviceYears, bodyType, jointType, english, false);
            api = result.Api;
            return Parse(HVX.Location.EmbeddedConclusions.GetVisibleLocationMatrix(result.Evidence, visibleMask), visibleMask);
        }

        // Explicit review evaluation reads the formal business summary as well as
        // the matrix. Passive chart annotations keep their previous contract.
        internal static SpectrumConclusionResult AnalyzeResult(Type type, double[,] matrix, double[][] joints,
            double operatingVoltage, double serviceYears, int bodyType, int jointType, bool english, bool includeSummary = true)
        {
            if (type != typeof(HVX.Location.LocatorEngine)) throw new ArgumentException("Only the bundled Task15 Evidence V3 source engine is supported.");
            if (joints == null || joints.Length != 3) throw new ArgumentException("Three phase joint arrays are required.");
            var result = HVX.Location.EmbeddedConclusions.Analyze(matrix, joints[0], joints[1], joints[2], operatingVoltage, english);
            if (result == null) throw new InvalidOperationException("Task15 returned no result.");
            if (!Property<bool>(result, "Ok")) throw new InvalidOperationException("Task15 analysis failed: " + Property<string>(result, "Error"));
            string api = result.ApiVersion + "; " + result.AlgorithmId + "; source-embedded EmbeddedConclusions.Analyze";
            int rows = Property<int>(result, "LocationRows"), columns = Property<int>(result, "LocationColumns");
            double[] values = Property<double[]>(result, "LocationMatrix");
            if (rows < 0 || columns < 9 || values == null || (long)rows * columns > values.LongLength)
                throw new InvalidOperationException("Invalid Task15 LocationMatrix shape.");
            var output = new double[rows, columns];
            for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) output[r, c] = values[r * columns + c];
            return new SpectrumConclusionResult { Api = api, Locations = output, Evidence = result, Joints=joints.Select(j=>(double[])j.Clone()).ToArray(),
                Summary = includeSummary ? Property<string>(result, "SummaryText") : "",
                Status = includeSummary ? (string[])Property<string[]>(result, "StatusArray").Clone() : new string[0],
                ValidPulses = includeSummary ? Property<int>(result, "ValidPulses") : 0 };
        }

        static T Property<T>(object instance, string name)
        {
            PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null) throw new MissingMemberException(instance.GetType().FullName, name);
            object value = property.GetValue(instance, null);
            if (value is T) return (T)value;
            if (typeof(T) == typeof(string) && value == null) return (T)(object)string.Empty;
            throw new InvalidOperationException("Unexpected Task15 property type: " + name);
        }

        internal static SpectrumConclusionMarker[] Parse(double[,] matrix, int visibleMask)
        {
            if (matrix == null || matrix.GetLength(1) < 9) throw new ArgumentException("Task15 LocationMatrix requires at least 9 columns.");
            var markers = new List<SpectrumConclusionMarker>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (int r = 0; r < matrix.GetLength(0); r++)
            {
                double mask = matrix[r, 0], position = matrix[r, 1], action = matrix[r, 8];
                if (!Finite(mask) || mask != Math.Truncate(mask) || mask < 1 || mask > 7 ||
                    !Finite(position) || position < 0 || !Finite(action) || action != Math.Truncate(action) || action < 0 || action > 3)
                    throw new ArgumentException("Task15 phase mask, position or ActionCode is invalid.");
                int phaseMask = (int)mask;
                // A combined ALL conclusion cannot be used as one phase's conclusion.
                if ((phaseMask & visibleMask) != phaseMask || action == 0) continue;
                string key = phaseMask + ":" + position.ToString("R", CultureInfo.InvariantCulture) + ":" + action;
                if (unique.Add(key)) markers.Add(new SpectrumConclusionMarker { PhaseMask = phaseMask, Position = position, ActionCode = (int)action });
            }
            return markers.OrderBy(p => p.Position).ThenBy(p => p.PhaseMask).ToArray();
        }

        internal static double InferOperatingVoltage(double[,] matrix, out string source)
        {
            var levels = new List<double>();
            for (int r = 0; r < matrix.GetLength(0); r++)
                if (Finite(matrix[r, 2]) && matrix[r, 2] > 0) levels.Add(Math.Round(matrix[r, 2], 1, MidpointRounding.AwayFromZero));
            levels = levels.Distinct().OrderBy(x => x).ToList();
            if (levels.Count == 0) { source = "none"; return 0; }
            if (levels.Count == 1) { source = "Task11 single_observed_voltage_level (inferred, not rated U0)"; return levels[0]; }
            // Exact Task11 standard-U0 ladder fit; it remains a reported inference.
            double[] candidates = { 3.6, 6, 8.7, 12, 18, 20, 26, 36, 45, 64, 76, 127, 220 };
            double[] multipliers = { 0.5, 0.6, 0.7, 0.8, 1, 1.2, 1.4, 1.5, 1.7, 1.8, 2, 2.2, 2.4, 2.5, 3 };
            double best = levels[0], bestScore = double.PositiveInfinity;
            foreach (double candidate in candidates)
            {
                double score = 0;
                foreach (double level in levels)
                {
                    double ratio = level / candidate, nearest = multipliers.Min(v => Math.Abs(v - ratio));
                    score += nearest * nearest;
                    if (ratio < 0.45) score += (0.45 - ratio) * (0.45 - ratio) * 4;
                    if (ratio > 3.05) score += (ratio - 3.05) * (ratio - 3.05) * 4;
                }
                score /= levels.Count;
                double maximumRatio = levels[levels.Count - 1] / candidate;
                if (maximumRatio < 1.65) score += (1.65 - maximumRatio) * 0.15;
                if (score < bestScore) { best = candidate; bestScore = score; }
            }
            source = "Task11 accepted_voltage_ladder_standard_u0_fit (inferred)";
            return best;
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    public partial class UnifiedSpectrumControl
    {
        SpectrumConclusionMarker[] conclusionMarkers = new SpectrumConclusionMarker[0];
        string conclusionContext, conclusionStatus = "NotRun", conclusionStatusCode = "NotRun", task15RuntimePath = "";
        string conclusionApi = "", conclusionVoltageSource = "";
        bool automaticConclusions = true, showConclusionAnnotations = true, conclusionProvided;
        internal Func<bool> AutomaticConclusionsGate;
        internal event EventHandler ConclusionSettingsChanged;
        double operatingVoltageKv, conclusionServiceYears = 2.0, conclusionUsedVoltage;
        int conclusionBodyType, conclusionJointType, conclusionAnalysisCount;

        [Category("Conclusions"), DefaultValue(true)]
        public bool AutomaticConclusions { get { return automaticConclusions; } set { if (automaticConclusions == value) return; automaticConclusions = value; ReloadConclusions(); } }
        [Category("Conclusions"), DefaultValue(true)]
        public bool ShowConclusionAnnotations { get { return showConclusionAnnotations; } set { if (showConclusionAnnotations == value) return; showConclusionAnnotations = value; DropDrawingCaches(); Invalidate(); } }
        [Category("Conclusions"), Description("Legacy compatibility setting; ignored by the source-embedded Task15 Evidence V3 engine.")]
        public string Task15RuntimePath
        {
            get { return task15RuntimePath; }
            set { string path = (value ?? "").Trim(); if (path.Length > 0 && (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Use an absolute DLL path.");
                if (string.Equals(path, task15RuntimePath, StringComparison.OrdinalIgnoreCase)) return; task15RuntimePath = path; ReloadConclusions(); }
        }
        [Category("Conclusions"), DefaultValue(0.0), Description("Rated phase-to-earth operating voltage U0 / kV. 0 means unknown; never inferred from test levels.")]
        public double OperatingVoltageKv { get { return operatingVoltageKv; } set { if (!Finite(value) || value < 0) throw new ArgumentOutOfRangeException("OperatingVoltageKv"); if (value == operatingVoltageKv) return; operatingVoltageKv = value; ReloadConclusions();NotifyRangeContextChanged(); } }
        [Category("Conclusions"), DefaultValue(2.0)]
        public double ConclusionCableServiceYears { get { return conclusionServiceYears; } set { if (!Finite(value) || value < 0) throw new ArgumentOutOfRangeException("ConclusionCableServiceYears"); if (value == conclusionServiceYears) return; conclusionServiceYears = value; ReloadConclusions(); } }
        [Category("Conclusions"), DefaultValue(0), Description("Formal Task15 cableBodyType code; default 0 follows its standard profile.")]
        public int ConclusionCableBodyType { get { return conclusionBodyType; } set { if (value < 0) throw new ArgumentOutOfRangeException("ConclusionCableBodyType"); if (value == conclusionBodyType) return; conclusionBodyType = value; ReloadConclusions(); } }
        [Category("Conclusions"), DefaultValue(0), Description("Formal Task15 intermediateJointType code; default 0 follows its standard profile.")]
        public int ConclusionIntermediateJointType { get { return conclusionJointType; } set { if (value < 0) throw new ArgumentOutOfRangeException("ConclusionIntermediateJointType"); if (value == conclusionJointType) return; conclusionJointType = value; ReloadConclusions(); } }
        [Browsable(false)] public string ConclusionStatus { get { return conclusionStatus; } }
        [Browsable(false)] public string ConclusionStatusCode { get { return conclusionStatusCode; } }
        [Browsable(false)] public string ConclusionApi { get { return conclusionApi; } }
        [Browsable(false)] public string ConclusionOperatingVoltageSource { get { return conclusionVoltageSource; } }
        [Browsable(false)] public double ConclusionOperatingVoltageKv { get { return conclusionUsedVoltage; } }
        [Browsable(false)] public int ConclusionAnalysisCount { get { return conclusionAnalysisCount; } }
        [Browsable(false)] public int ConclusionMarkerCount { get { return conclusionMarkers.Length; } }

        string ConclusionContextKey(bool includeLanguage = true)
        {
            return MatrixCloneCount + "|review:" + reviewRevision + "|" + metadata.ReadCount + "|" + projectPath + "|" + phaseFilter + "|" + string.Join(",", visible.Select(v => v ? "1" : "0")) +
                "|" + acceptedOnly + "|" + (includeLanguage ? english.ToString() : "provided-language-independent") + "|" + string.Join(",", deleted.OrderBy(r => r)) + "|" + task15RuntimePath + "|" +
                operatingVoltageKv.ToString("R", CultureInfo.InvariantCulture) + "|" + conclusionServiceYears.ToString("R", CultureInfo.InvariantCulture) + "|" + conclusionBodyType + "|" + conclusionJointType;
        }
        int ConclusionVisibleMask()
        {
            if (phaseFilter == 0) return 0;
            int mask = 0;
            for (int p = 1; p <= 3; p++) if (visible[p - 1] && (phaseFilter < 0 || phaseFilter == p)) mask |= 1 << (p - 1);
            return mask;
        }
        internal int ConclusionVisiblePhaseMask { get { return ConclusionVisibleMask(); } }
        double[,] ConclusionInput(int mask)
        {
            var rows = new List<int>();
            for (int r = 0; r < source.GetLength(0); r++)
            {
                double phase = source[r, 0];
                if (phase < 1 || phase > 3 || phase != Math.Truncate(phase) || !Finite(phase)) continue;
                if ((mask & (1 << ((int)phase - 1))) == 0 || source[r, 35] != 1 || deleted.Contains(r) || !IsReviewRowVisible(r)) continue;
                rows.Add(r);
            }
            var result = new double[rows.Count, source.GetLength(1)];
            for (int i = 0; i < rows.Count; i++) for (int c = 0; c < result.GetLength(1); c++) result[i,c] = source[rows[i],c];
            return result;
        }
        double[][] ConclusionJoints(double[,] matrix)
        {
            var result = new double[3][];
            for (int p = 1; p <= 3; p++)
            {
                double length = metadata.LengthForPhase(p);
                for (int r = 0; r < matrix.GetLength(0); r++) if (matrix[r, 0] == p && Finite(matrix[r, 5]) && matrix[r, 5] > 0)
                    length = !Finite(length) ? matrix[r, 5] : Math.Max(length, matrix[r, 5]);
                if (!Finite(length) || length <= 0) length = currentLength;
                if (!Finite(length) || length <= 0) throw new InvalidOperationException("Cable length is required for Task15 conclusions.");
                var joints=reviewJointProvider==null?metadata.JointsForPhase(p):reviewJointProvider(p,length);
                result[p - 1] = new[] { 0.0, length }.Concat(joints.Where(v => Finite(v) && v > 0 && v < length)).Distinct().OrderBy(v => v).ToArray();
            }
            return result;
        }

        // Called by Rebuild only; both failures and successful results are cached.
        void RefreshConclusionAnnotations()
        {
            string key = ConclusionContextKey(!conclusionProvided);
            if (string.Equals(key, conclusionContext, StringComparison.Ordinal)) return;
            conclusionProvided = false;
            conclusionContext = ConclusionContextKey(); conclusionMarkers = new SpectrumConclusionMarker[0]; conclusionApi = "";
            conclusionVoltageSource = ""; conclusionUsedVoltage = 0;
            if (!automaticConclusions || AutomaticConclusionsGate != null && !AutomaticConclusionsGate()) { SetConclusionStatus("Disabled", "Automatic Task15 conclusions are disabled."); return; }
            int mask = ConclusionVisibleMask();
            double[,] matrix = ConclusionInput(mask);
            if (matrix.GetLength(0) == 0) { SetConclusionStatus("NoAcceptedInput", "No accepted A/B/C input for Task15; no equipment-condition conclusion is available."); return; }
            try
            {
                conclusionAnalysisCount++;
                string path = SpectrumTask15Bridge.ResolvePath(task15RuntimePath);
                Type type = SpectrumTask15Bridge.ResolveType(path);
                conclusionUsedVoltage = operatingVoltageKv;
                conclusionVoltageSource = operatingVoltageKv > 0 ? "explicit OperatingVoltageKv" : "unknown U0; not inferred from test levels";
                if (conclusionUsedVoltage < 0 || !Finite(conclusionUsedVoltage)) throw new InvalidOperationException("Operating voltage is invalid.");
                double[][] joints = ConclusionJoints(matrix);
                var markers = new List<SpectrumConclusionMarker>();
                // Task11 policy: partial selections must not inherit a hidden/other phase's tier.
                int[] scopes = mask == 7 ? new[] { 7 } : new[] { 1, 2, 4 }.Where(p => (mask & p) != 0).ToArray();
                foreach (int scope in scopes)
                {
                    double[,] phaseInput = scope == mask ? matrix : ConclusionInput(scope);
                    if (phaseInput.GetLength(0) == 0) continue;
                    string api;
                    markers.AddRange(SpectrumTask15Bridge.Analyze(type, phaseInput, joints, conclusionUsedVoltage,
                        conclusionServiceYears, conclusionBodyType, conclusionJointType, english, scope, out api));
                    conclusionApi = api;
                }
                conclusionMarkers = markers.OrderBy(p => p.Position).ThenBy(p => p.PhaseMask).ToArray();
                string profile = "Evidence V3 algorithmMode=0; engineering evidence score, not a calibrated probability; years/material metadata do not raise severity";
                SetConclusionStatus("Ready", "Task15 completed; " + conclusionMarkers.Length + " action markers; " + conclusionVoltageSource +
                    "; runtime=" + path + "; " + profile + ". Empty markers do not independently prove absence of discharge.");
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException || ex is System.Threading.ThreadAbortException) throw;
                conclusionMarkers = new SpectrumConclusionMarker[0];
                string code = ex is FileNotFoundException ? "RuntimeUnavailable" : ex is BadImageFormatException ? "RuntimeArchitectureMismatch" : "AnalysisUnavailable";
                SetConclusionStatus(code, "Task15 conclusion unavailable (not a no-discharge result): " + (ex is BadImageFormatException ? "The runtime must match the host process architecture. " : "") + ex.Message);
            }
        }

        void SetConclusionStatus(string code, string status)
        { conclusionStatusCode = code; conclusionStatus = status; DropDrawingCaches(); LayoutSpectrumToolbar(); }

        public void ReloadConclusions()
        { conclusionProvided = false; conclusionContext = null; RefreshConclusionAnnotations(); Invalidate(); if(ConclusionSettingsChanged!=null)ConclusionSettingsChanged(this,EventArgs.Empty); }

        /// <summary>Bind a formal Task15 LocationMatrix to the current data/settings. Future data/filter/project changes invalidate it.</summary>
        public void SetTask15ConclusionMatrix(double[,] locationMatrix)
        {
            SpectrumConclusionMarker[] markers = SpectrumTask15Bridge.Parse(locationMatrix, ConclusionVisibleMask());
            conclusionMarkers = markers; conclusionProvided = true; conclusionContext = ConclusionContextKey(false); conclusionApi = "caller-supplied Task15 LocationMatrix";
            conclusionVoltageSource = "caller-supplied conclusion"; conclusionUsedVoltage = 0;
            SetConclusionStatus("Provided", "Caller-supplied Task15 LocationMatrix for the current context; combined hidden-phase rows are excluded.");
            Invalidate();
        }
        internal void ClearProvidedConclusions()
        {
            if(!conclusionProvided&&conclusionMarkers.Length==0)return;
            conclusionProvided=false;conclusionContext=null;conclusionMarkers=new SpectrumConclusionMarker[0];conclusionApi="";
            SetConclusionStatus("Pending","The current review data has no published conclusion.");Invalidate();
        }
        public double[,] GetConclusionAnnotations()
        {
            var matrix = new double[conclusionMarkers.Length, 3];
            for (int r = 0; r < conclusionMarkers.Length; r++) { matrix[r, 0] = conclusionMarkers[r].PhaseMask; matrix[r, 1] = conclusionMarkers[r].Position; matrix[r, 2] = conclusionMarkers[r].ActionCode; }
            return matrix;
        }

        void DrawConclusionAnnotations(Graphics g, Rectangle area)
        {
            if (!showConclusionAnnotations || mode != 0 && mode != 1 || conclusionMarkers.Length == 0) return;
            var occupied = new List<RectangleF>();
            int omitted = 0;
            int lanes = Math.Max(1, Math.Min(4, (area.Height - 32) / 24));
            // Keep high-action labels first; every marker remains in the public matrix.
            foreach (SpectrumConclusionMarker marker in conclusionMarkers.OrderByDescending(p => p.ActionCode).ThenBy(p => p.Position))
            {
                if (marker.Position < State.X0 || marker.Position > State.X1) continue;
                float x = Project(new SpectrumPoint { X = marker.Position, Y = State.Y0 }, area).X;
                Color color = marker.ActionCode == 3 ? Color.FromArgb(190, 45, 45) : marker.ActionCode == 2 ? Color.FromArgb(222, 120, 15) : Color.FromArgb(55, 105, 170);
                string action = marker.ActionCode == 3 ? (L("Verify first","优先核查")) : marker.ActionCode == 2 ? (L("Attention","重点关注")) : (L("Follow up","计划跟踪"));
                string phases = string.Join("/", Enumerable.Range(1, 3).Where(p => (marker.PhaseMask & (1 << (p - 1))) != 0).Select(GetPhaseLabel));
                string text = phases + " " + action + " " + marker.Position.ToString("0.#", CultureInfo.InvariantCulture) + " m";
                using (var pen = new Pen(Color.FromArgb(180, color), marker.ActionCode == 3 ? 2 : 1.3f))
                using (var ink = new SolidBrush(color)) using (var background = new SolidBrush(Color.FromArgb(225, Color.White)))
                {
                    pen.DashStyle = marker.ActionCode >= 2 ? DashStyle.Dash : DashStyle.Dot;
                    g.DrawLine(pen, x, area.Top, x, area.Bottom);
                    float width = Math.Min(area.Width - 4, g.MeasureString(text, Font).Width + 10);
                    float left = Math.Max(area.Left + 2, Math.Min(area.Right - width - 2, x - width / 2));
                    bool placed = false;
                    for (int lane = 0; lane < lanes && !placed; lane++)
                    {
                        var label = new RectangleF(left, area.Top + 5 + lane * 24, width, 22);
                        RectangleF padded = label; padded.Inflate(3, 1);
                        if (occupied.Any(r => r.IntersectsWith(padded))) continue;
                        occupied.Add(padded); g.FillRectangle(background, label); CenterText(g, text, Font, ink, label); placed = true;
                    }
                    if (!placed) omitted++;
                }
            }
            if (omitted > 0 && area.Height > 55)
                using (var background = new SolidBrush(Color.FromArgb(230, Color.White)))
                using (var ink = new SolidBrush(Color.DimGray))
                {
                    string note = english ? "+" + omitted + " labels: GetConclusionAnnotations()" : "另有 " + omitted + " 个标签，完整结果见 GetConclusionAnnotations()";
                    float width = Math.Min(area.Width - 4, g.MeasureString(note, Font).Width + 10);
                    var label = new RectangleF(area.Left + 2, area.Bottom - 25, width, 22);
                    g.FillRectangle(background, label); CenterText(g, note, Font, ink, label);
                }
        }
    }
}
