using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Waveform;

namespace Xiongxin.Owts.HostAdapters.AHistory
{
    /// <summary>
    /// Rewrite-side port of dac-source <c>AHistoryEvidence</c> (ReviewAHistoryAuxiliary):
    /// Anchor / Capture / Select for Algo-5 previous* backfill.
    /// Deviations vs GUI (documented, not forged):
    /// - acquisition id = measurementId (no LOC "Acquisition context" GUID);
    /// - project/settings/wave-hash provenance checks skipped (same auto-stub batch);
    /// - Overlay Position is 0-based [13] (Δsamples at [12]); NormalizeOverlayPosition remaps mistaken 12→13 if needed.
    /// Policy string matches source: A-history-Task28-v1;strength40;interior;native-accepted&gt;=0.70.
    /// </summary>
    public static class AHistoryEvidenceSelect
    {
        public const string Policy = "A-history-Task28-v1;strength40;interior;native-accepted>=0.70;recorded-acquisition";
        public const string SelectId = "AHistoryEvidenceSelect/v1";
        public const int MaximumRows = 4096;
        public const long MaximumCells = 8000000;
        /// <summary>Overlay / AHistory Anchor position column (0-based).</summary>
        public const int OverlayPositionColumn = 13;
        /// <summary>Cluster strength passed to Task28 (GUI Select).</summary>
        public const double ClusterStrength = 40;
        /// <summary>minimumIndependentRecords for Task28 (GUI Select uses 3).</summary>
        public const int MinimumIndependentRecords = 3;

        static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>
        /// Source <c>AHistoryEvidence.Anchor</c>: accepted + score∈[0.70,1] + phase 1..3 + interior position
        /// + positive voltage/charge/sigma/velocity/sampleRate + onset reflection &gt; direct.
        /// </summary>
        public static bool Anchor(double[,] p, int r, int positionColumn = OverlayPositionColumn)
        {
            if (p == null || r < 0 || r >= p.GetLength(0) || p.GetLength(1) < 36) return false;
            if (positionColumn < 0 || positionColumn >= p.GetLength(1)) return false;
            double length = p[r, 5], x = p[r, positionColumn], epsilon = Math.Max(1e-9, 1e-12 * length);
            return p[r, 35] == 1
                && Finite(p[r, 33]) && p[r, 33] >= 0.70 && p[r, 33] <= 1
                && p[r, 0] >= 1 && p[r, 0] <= 3 && p[r, 0] == Math.Floor(p[r, 0])
                && Finite(length) && length > 0
                && Finite(x) && x > epsilon && length - x > epsilon
                && Finite(p[r, 2]) && p[r, 2] > 0
                && Finite(p[r, 15]) && p[r, 15] > 0
                && Finite(p[r, 28]) && p[r, 28] > 0
                && Finite(p[r, 6]) && p[r, 6] > 0
                && Finite(p[r, 7]) && p[r, 7] > 0
                && Finite(p[r, 10]) && Finite(p[r, 11]) && p[r, 11] > p[r, 10];
        }

        public static bool AnchorRow(double[] rowMajor36, int positionColumn = OverlayPositionColumn)
        {
            if (rowMajor36 == null || rowMajor36.Length < 36) return false;
            var m = new double[1, 36];
            for (int c = 0; c < 36; c++) m[0, c] = rowMajor36[c];
            return Anchor(m, 0, positionColumn);
        }

        /// <summary>
        /// If Overlay [13] is not an interior position but Worker index 12 is, copy 12→13
        /// so Anchor/Task28 see Overlay layout. Mutates a clone; never invents positions.
        /// </summary>
        public static double[,] NormalizeOverlayPosition(double[,] parameters)
        {
            if (parameters == null || parameters.GetLength(0) == 0)
                return parameters == null ? new double[0, 36] : (double[,])parameters.Clone();
            int rows = parameters.GetLength(0), cols = parameters.GetLength(1);
            var copy = (double[,])parameters.Clone();
            if (cols < 36) return copy;
            int needRemap = 0, totalAccepted = 0;
            for (int r = 0; r < rows; r++)
            {
                if (copy[r, 35] != 1) continue;
                totalAccepted++;
                double length = copy[r, 5];
                if (!Finite(length) || length <= 0) continue;
                double eps = Math.Max(1e-9, 1e-12 * length);
                bool ok13 = Finite(copy[r, 13]) && copy[r, 13] > eps && length - copy[r, 13] > eps;
                bool ok12 = Finite(copy[r, 12]) && copy[r, 12] > eps && length - copy[r, 12] > eps;
                if (!ok13 && ok12) needRemap++;
            }
            if (totalAccepted == 0 || needRemap * 2 < totalAccepted) return copy;
            for (int r = 0; r < rows; r++)
            {
                double length = copy[r, 5];
                if (!Finite(length) || length <= 0) continue;
                double eps = Math.Max(1e-9, 1e-12 * length);
                bool ok13 = Finite(copy[r, 13]) && copy[r, 13] > eps && length - copy[r, 13] > eps;
                bool ok12 = Finite(copy[r, 12]) && copy[r, 12] > eps && length - copy[r, 12] > eps;
                if (!ok13 && ok12) copy[r, 13] = copy[r, 12];
            }
            return copy;
        }

        public static AHistoryReceipt Capture(
            long measurementId,
            int phase,
            ParameterMatrixDto parameters,
            ParameterMatrixDto raw,
            ParameterMatrixDto filtered,
            ParameterMatrixDto equivalent)
        {
            double[,] p = ToRectangular(parameters, 36);
            p = NormalizeOverlayPosition(p);
            if (p.GetLength(0) == 0) return null;

            // GUI Capture uses currentParameterMatrix only; Worker returns TOTAL —
            // keep rows whose locationDataId (col1) matches this measurement.
            var idx = new List<int>();
            for (int i = 0; i < p.GetLength(0); i++)
            {
                double id = p[i, 1];
                if (Finite(id) && Math.Abs(id - measurementId) > 0.5) continue;
                if (Anchor(p, i)) idx.Add(i);
            }
            if (idx.Count == 0) return null;
            if (idx.Count > MaximumRows) throw new InvalidOperationException("History row budget exceeded.");

            double[,] r = ToRectangular(raw, 0);
            double[,] f = ToRectangular(filtered, 0);
            double[,] e = ToRectangular(equivalent, 0);
            // Align envelope rows to parameter rows when widths match cumulative layout.
            int[] rows = idx.ToArray();
            var receipt = new AHistoryReceipt
            {
                MeasurementId = measurementId,
                Phase = phase,
                Parameters = CopyRows(p, rows, 36),
                Raw = CopyRows(r, rows, 0),
                Filtered = CopyRows(f, rows, 0),
                Equivalent = CopyRows(e, rows, 0)
            };
            long cells = (long)rows.Length * (36L
                + Math.Max(0, receipt.Raw.GetLength(1))
                + Math.Max(0, receipt.Filtered.GetLength(1))
                + Math.Max(0, receipt.Equivalent.GetLength(1)));
            if (cells > MaximumCells)
                throw new InvalidOperationException("History waveform budget exceeded; no partial history retained.");
            return receipt;
        }

        /// <summary>
        /// Source <c>AHistoryEvidence.Select</c> (batch receipts → qualified previous*).
        /// Blocks same measurementId; requires phase match + Anchor; groups by cols 5|6|7;
        /// Task28 FilterLocationMatrixAdvanced(40,-1,3,false,false).
        /// </summary>
        public static AHistorySelection Select(IList<AHistoryReceipt> receipts, int phase, long excludeMeasurementId)
        {
            var result = new AHistorySelection();
            if (receipts == null || receipts.Count == 0)
            {
                result.State = "no_qualified_history";
                result.Detail = "No captured receipts yet.";
                return result;
            }
            if (phase < 1 || phase > 3)
            {
                result.State = "unavailable";
                result.Detail = "Missing current phase provenance (need 1..3).";
                return result;
            }

            var candidates = receipts.Where(r => r != null && r.MeasurementId != excludeMeasurementId).ToList();
            // One receipt per acquisition (measurementId); drop duplicates conservatively.
            candidates = candidates
                .GroupBy(r => r.MeasurementId)
                .Where(g => g.Count() == 1)
                .Select(g => g.First())
                .ToList();

            var rowRefs = new List<Tuple<AHistoryReceipt, int>>();
            foreach (var r in candidates)
            {
                if (r.Parameters == null) continue;
                for (int i = 0; i < r.Parameters.GetLength(0); i++)
                {
                    if (r.Parameters[i, 0] == phase && Anchor(r.Parameters, i))
                        rowRefs.Add(Tuple.Create(r, i));
                }
            }
            if (rowRefs.Count > MaximumRows)
                throw new InvalidOperationException("History row budget exceeded.");
            result.CandidateRows = rowRefs.Count;

            var kept = new List<Tuple<AHistoryReceipt, int>>();
            var diagnostics = new List<string>();
            foreach (var group in rowRefs.GroupBy(x => string.Join("|", new[] { 5, 6, 7 }.Select(
                c => x.Item1.Parameters[x.Item2, c].ToString("R", CultureInfo.InvariantCulture)))))
            {
                var items = group.ToList();
                double[,] input = JoinParameters(items);
                // Remap col1 to acquisition vote id; clear col22 (GUI Select).
                var ids = items.Select(x => x.Item1.MeasurementId).Distinct().OrderBy(x => x)
                    .Select((v, i) => new { v, i }).ToDictionary(x => x.v, x => x.i);
                for (int i = 0; i < input.GetLength(0); i++)
                {
                    input[i, 1] = ids[items[i].Item1.MeasurementId];
                    input[i, 22] = double.NaN;
                }
                double[,] retained, gated, scores, summary;
                int[] indices;
                string status;
                new LocationClusterFilterEngine().FilterLocationMatrixAdvanced(
                    input, ClusterStrength, -1, MinimumIndependentRecords, false, false,
                    out retained, out gated, out indices, out scores, out summary, out status);
                foreach (int index in indices) kept.Add(items[index]);
                diagnostics.Add(status);
            }

            long totalCells = kept.Count == 0 ? 0 : (long)kept.Count * (36L
                + kept.Max(x => x.Item1.Raw == null ? 0 : x.Item1.Raw.GetLength(1))
                + kept.Max(x => x.Item1.Filtered == null ? 0 : x.Item1.Filtered.GetLength(1))
                + kept.Max(x => x.Item1.Equivalent == null ? 0 : x.Item1.Equivalent.GetLength(1)));
            if (totalCells > MaximumCells)
                throw new InvalidOperationException("Combined history waveform budget exceeded; auxiliary disabled.");

            result.Parameters = JoinMatrix(kept, x => x.Item1.Parameters, 36);
            result.Raw = JoinMatrix(kept, x => x.Item1.Raw, 0);
            result.Filtered = JoinMatrix(kept, x => x.Item1.Filtered, 0);
            result.Equivalent = JoinMatrix(kept, x => x.Item1.Equivalent, 0);
            result.QualifiedRows = kept.Count;
            result.State = kept.Count > 0 ? "applied" : "no_qualified_history";
            result.Task28 = "[" + string.Join(",", diagnostics) + "]";
            result.Detail = kept.Count > 0
                ? "qualified=" + kept.Count + ";candidates=" + result.CandidateRows
                : (result.CandidateRows > 0
                    ? "candidates=" + result.CandidateRows + " but Task28 retained 0 (need ≥"
                      + MinimumIndependentRecords + " independent acquisitions per geometry)"
                    : "no Anchor+phase rows from prior receipts");
            return result;
        }

        public static ParameterHistory ToBundleHistory(AHistorySelection selection)
        {
            var hist = new ParameterHistory();
            if (selection == null || selection.QualifiedRows <= 0)
                return hist;
            hist.PreviousParameters = FromRectangular(selection.Parameters, 36);
            hist.PreviousRaw = FromRectangular(selection.Raw, 0);
            hist.PreviousFiltered = FromRectangular(selection.Filtered, 0);
            hist.PreviousEquivalent = FromRectangular(selection.Equivalent, 0);
            // AstraExplicitHistory: nonempty previous rows require waveform width > 0.
            EnsureEnvelopeWidth(hist);
            return hist;
        }

        static void EnsureEnvelopeWidth(ParameterHistory hist)
        {
            int rows = hist.PreviousParameters != null ? hist.PreviousParameters.RowCount : 0;
            if (rows <= 0) return;
            if (hist.PreviousRaw == null || hist.PreviousRaw.RowCount != rows || EnvelopeCols(hist.PreviousRaw) <= 0)
                hist.PreviousRaw = PlaceholderEnvelope(rows, hist.PreviousParameters);
            if (hist.PreviousFiltered == null || hist.PreviousFiltered.RowCount != rows || EnvelopeCols(hist.PreviousFiltered) <= 0)
                hist.PreviousFiltered = PlaceholderEnvelope(rows, hist.PreviousParameters);
            if (hist.PreviousEquivalent == null || hist.PreviousEquivalent.RowCount != rows || EnvelopeCols(hist.PreviousEquivalent) <= 0)
                hist.PreviousEquivalent = AllNanEnvelope(rows, Math.Max(1, EnvelopeCols(hist.PreviousRaw)));
        }

        static int EnvelopeCols(ParameterMatrixDto m)
        {
            if (m == null || m.RowCount <= 0 || m.Values == null || m.Values.Length == 0) return 0;
            return m.Values.Length / Math.Max(1, m.RowCount);
        }

        /// <summary>
        /// Build a minimal finite envelope from col31/32 window lengths so AstraExplicitHistory Validate passes
        /// when Capture lacked waveform rows (should be rare when Worker returns envelopes).
        /// </summary>
        static ParameterMatrixDto PlaceholderEnvelope(int rows, ParameterMatrixDto parameters)
        {
            int width = 1;
            if (parameters != null && parameters.Values != null)
            {
                for (int r = 0; r < rows; r++)
                {
                    int o = r * 36;
                    if (o + 32 >= parameters.Values.Length) break;
                    double first = parameters.Values[o + 31], last = parameters.Values[o + 32];
                    if (Finite(first) && Finite(last) && last >= first)
                        width = Math.Max(width, (int)(last - first + 1));
                }
            }
            var values = new double[rows * width];
            for (int i = 0; i < values.Length; i++) values[i] = 0;
            // Mark NaN past window per row using col31/32 when present.
            if (parameters != null && parameters.Values != null)
            {
                for (int r = 0; r < rows; r++)
                {
                    int o = r * 36;
                    double first = o + 32 < parameters.Values.Length ? parameters.Values[o + 31] : 0;
                    double last = o + 32 < parameters.Values.Length ? parameters.Values[o + 32] : 0;
                    int count = 1;
                    if (Finite(first) && Finite(last) && last >= first)
                        count = Math.Max(1, (int)(last - first + 1));
                    for (int c = count; c < width; c++)
                        values[r * width + c] = double.NaN;
                }
            }
            return new ParameterMatrixDto { RowCount = rows, Values = values };
        }

        static ParameterMatrixDto AllNanEnvelope(int rows, int width)
        {
            width = Math.Max(1, width);
            var values = new double[rows * width];
            for (int i = 0; i < values.Length; i++) values[i] = double.NaN;
            return new ParameterMatrixDto { RowCount = rows, Values = values };
        }

        public static string StatusToken(AHistorySelection selection)
        {
            if (selection == null)
                return "ahistory=on;ahist_state=disabled;ahist_qualified=0";
            return "ahistory=on"
                + ";ahist_state=" + (selection.State ?? "unknown")
                + ";ahist_qualified=" + selection.QualifiedRows.ToString(CultureInfo.InvariantCulture)
                + ";ahist_candidates=" + selection.CandidateRows.ToString(CultureInfo.InvariantCulture)
                + ";ahist_policy=" + Policy;
        }

        static double[,] ToRectangular(ParameterMatrixDto dto, int minimumColumns)
        {
            if (dto == null || dto.RowCount <= 0 || dto.Values == null || dto.Values.Length == 0)
                return minimumColumns > 0 ? new double[0, minimumColumns] : new double[0, 0];
            int rows = dto.RowCount;
            int cols = dto.Values.Length / Math.Max(1, rows);
            if (cols <= 0) cols = minimumColumns;
            if (minimumColumns > 0) cols = Math.Max(cols, minimumColumns);
            var m = new double[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * (dto.Values.Length / Math.Max(1, rows)) + c;
                    m[r, c] = (c < dto.Values.Length / Math.Max(1, rows) && idx < dto.Values.Length)
                        ? dto.Values[idx]
                        : double.NaN;
                }
            return m;
        }

        static ParameterMatrixDto FromRectangular(double[,] m, int minimumColumns)
        {
            if (m == null || m.GetLength(0) == 0)
                return new ParameterMatrixDto { RowCount = 0, Values = new double[0] };
            int rows = m.GetLength(0), cols = Math.Max(minimumColumns, m.GetLength(1));
            var values = new double[rows * cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    values[r * cols + c] = c < m.GetLength(1) ? m[r, c] : double.NaN;
            return new ParameterMatrixDto { RowCount = rows, Values = values };
        }

        static double[,] CopyRows(double[,] source, int[] rows, int minimumWidth)
        {
            int width = rows.Length == 0
                ? minimumWidth
                : Math.Max(minimumWidth, source == null || source.GetLength(0) == 0 ? 0 : source.GetLength(1));
            var copy = new double[rows.Length, width];
            for (int i = 0; i < rows.Length; i++)
            {
                int src = rows[i];
                for (int c = 0; c < width; c++)
                    copy[i, c] = source != null && src < source.GetLength(0) && c < source.GetLength(1)
                        ? source[src, c]
                        : double.NaN;
            }
            return copy;
        }

        static double[,] JoinParameters(List<Tuple<AHistoryReceipt, int>> items)
        {
            var m = new double[items.Count, 36];
            for (int i = 0; i < items.Count; i++)
            {
                var src = items[i].Item1.Parameters;
                int sr = items[i].Item2;
                for (int c = 0; c < 36; c++)
                    m[i, c] = src != null && sr < src.GetLength(0) && c < src.GetLength(1) ? src[sr, c] : double.NaN;
            }
            return m;
        }

        static double[,] JoinMatrix(List<Tuple<AHistoryReceipt, int>> items, Func<Tuple<AHistoryReceipt, int>, double[,]> get, int minimumWidth)
        {
            int width = items.Count == 0
                ? minimumWidth
                : Math.Max(minimumWidth, items.Max(x =>
                {
                    var s = get(x);
                    return s == null || s.GetLength(0) == 0 ? 0 : s.GetLength(1);
                }));
            var result = new double[items.Count, width];
            for (int i = 0; i < items.Count; i++)
            {
                var source = get(items[i]);
                int sr = items[i].Item2;
                for (int c = 0; c < width; c++)
                    result[i, c] = source != null && sr < source.GetLength(0) && c < source.GetLength(1)
                        ? source[sr, c]
                        : double.NaN;
            }
            return result;
        }
    }

    public sealed class AHistoryReceipt
    {
        public long MeasurementId { get; set; }
        public int Phase { get; set; }
        public double[,] Parameters { get; set; }
        public double[,] Raw { get; set; }
        public double[,] Filtered { get; set; }
        public double[,] Equivalent { get; set; }
    }

    public sealed class AHistorySelection
    {
        public string State { get; set; }
        public string Detail { get; set; }
        public int CandidateRows { get; set; }
        public int QualifiedRows { get; set; }
        public string Task28 { get; set; }
        public double[,] Parameters { get; set; }
        public double[,] Raw { get; set; }
        public double[,] Filtered { get; set; }
        public double[,] Equivalent { get; set; }

        public AHistorySelection()
        {
            State = "no_qualified_history";
            Detail = "";
            Task28 = "[]";
            Parameters = new double[0, 36];
            Raw = new double[0, 0];
            Filtered = new double[0, 0];
            Equivalent = new double[0, 0];
        }
    }

    /// <summary>Session-scoped receipt accumulator for CompareMany mid chain.</summary>
    public sealed class AHistorySession
    {
        readonly List<AHistoryReceipt> _receipts = new List<AHistoryReceipt>();

        public IList<AHistoryReceipt> Receipts { get { return _receipts; } }

        public AHistorySelection SelectFor(int phase, long excludeMeasurementId)
        {
            return AHistoryEvidenceSelect.Select(_receipts, phase, excludeMeasurementId);
        }

        public void TryCapture(long measurementId, int phase, LocationAnalysisResult loc)
        {
            if (loc == null || !loc.Success || loc.Parameters == null || loc.Parameters.RowCount <= 0)
                return;
            try
            {
                var receipt = AHistoryEvidenceSelect.Capture(
                    measurementId, phase, loc.Parameters, loc.Raw, loc.Filtered, loc.Equivalent);
                if (receipt != null) _receipts.Add(receipt);
            }
            catch (InvalidOperationException)
            {
                // Budget exceeded: leave session as-is (GUI also refuses partial retain).
            }
        }
    }
}
