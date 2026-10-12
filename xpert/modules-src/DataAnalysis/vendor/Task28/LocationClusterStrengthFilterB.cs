// Recovered spatial display filter, distinct from Task28 statistical evidence filter A.
// Sources: dac_runtime_20261010_r01/python_algorithms/dac_recovered/cluster.py,
// manual_evidence/cluster_display_review.txt; DAC VA 0x624680 / 0x6418a8 / 0x6245a0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HVX.PD.LocationFiltering
{
    public sealed class LocationFilterBOptions
    {
        public double Strength { get; set; }
        public double ChargeThresholdPc { get; set; }
        public double VoltageLimitKv { get; set; }
        public int MinimumClusterSize { get; set; }
        public int FromM { get; set; }
        public int? ToM { get; set; }
        public LocationFilterBOptions()
        {
            Strength = 40; ChargeThresholdPc = 20; VoltageLimitKv = double.MaxValue;
            MinimumClusterSize = 3; FromM = 0; ToM = null;
        }
        public LocationFilterBOptions Clone() { return (LocationFilterBOptions)MemberwiseClone(); }
    }

    public sealed class LocationFilterBResult
    {
        public bool[] KeepMask { get; internal set; }
        public bool[] UnknownMask { get; internal set; }
        public string DiagnosticsJson { get; internal set; }
        public int EligibleRows { get; internal set; }
        // Includes conservative unknowns; known masks (including terminal density bypass) are not physical support.
        public int RetainedRows { get; internal set; }
        public int UnknownRows { get; internal set; }
    }

    /// <summary>
    /// Non-mutating spatial display mask, NOT physical localization or defect probability.
    /// f32 event quantization and nearest-even rounding reproduce the recovered research model;
    /// native x87 state and undefined memory are not emulated. Unknowns remain visible for review.
    /// Host adaptations: accepted complete rows only, nonnegative x, positive voltage, phase 0
    /// as an independent pool, exact cable-length pools, and strength restricted to [0,100].
    /// No compensated charge, LOC deduplication, endpoint protection, or swap-delete cleanup.
    /// </summary>
    public static class LocationClusterStrengthFilterB
    {
        public const string Version = "1.0.0";
        public const int MaximumGroups = 8192;

        public static void ValidateOptions(LocationFilterBOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (!Finite(options.Strength) || options.Strength < 0 || options.Strength > 100)
                throw new ArgumentException("Filter B strength must be finite in [0,100].", "options");
            if (!Finite(options.ChargeThresholdPc) || options.ChargeThresholdPc < 0 ||
                !Finite(options.VoltageLimitKv) || options.VoltageLimitKv < 0 || options.MinimumClusterSize < 0)
                throw new ArgumentException("Filter B thresholds must be finite and nonnegative; minimum must be nonnegative.", "options");
            if (options.FromM < 0 || options.FromM > 100000 ||
                (options.ToM.HasValue && (options.ToM.Value < options.FromM || options.ToM.Value > 100000)))
                throw new ArgumentException("Filter B range must be integer bins in [0,100000], From <= To.", "options");
        }

        public static LocationFilterBResult Evaluate(double[,] matrix, bool[] complete, LocationFilterBOptions options)
        {
            LocationClusterFilterEngine.ValidateInputMatrix(matrix);
            if (complete == null || complete.Length != matrix.GetLength(0))
                throw new ArgumentException("One completeness flag per matrix row is required.", "complete");
            ValidateOptions(options);
            LocationFilterBOptions config = options.Clone();
            int rowCount = matrix.GetLength(0);
            LocationFilterBResult result = new LocationFilterBResult {
                KeepMask = new bool[rowCount], UnknownMask = new bool[rowCount] };
            Dictionary<GroupKey, Group> groups = new Dictionary<GroupKey, Group>();
            int incomplete = 0, unaccepted = 0, invalid = 0, unrepresentable = 0;
            int voltageExcluded = 0, rangeExcluded = 0, noSeed = 0, densityExcluded = 0, terminalRetained = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (!complete[i]) { incomplete++; continue; }
                if (matrix[i, 35] != 1) { unaccepted++; continue; }
                double phase = matrix[i, 0], length = matrix[i, 5], x = matrix[i, 13];
                double q = matrix[i, 15], v = matrix[i, 2];
                if (!Finite(phase) || phase < 0 || phase > 3 || phase != Math.Floor(phase) ||
                    !Finite(length) || length < 1 || length > 100000 ||
                    !Finite(x) || x < 0 || x > length || !Finite(q) || !Finite(v) || v <= 0)
                { invalid++; continue; }
                // Conversion is explicit: matrix data is never overwritten with quantized values.
                x = (double)(float)x; q = Math.Abs((double)(float)q); v = (double)(float)v;
                // The host excludes zero voltage after quantization too (positive underflow).
                // This is an explicit host validity rule, not a claim about native DAC behavior.
                if (!Finite(q) || !Finite(v) || v <= 0) { unrepresentable++; continue; }
                GroupKey key = new GroupKey((int)phase, length);
                Group group;
                if (!groups.TryGetValue(key, out group))
                {
                    if (groups.Count >= MaximumGroups)
                        throw new ArgumentException("Filter B supports at most 8192 exact phase/length groups per call.", "matrix");
                    group = new Group((int)phase, length);
                    if (config.FromM > group.Bins || (config.ToM.HasValue && config.ToM.Value > group.Bins))
                        throw new ArgumentException("Filter B explicit range exceeds a cable's rounded length; no implicit clipping.", "options");
                    groups.Add(key, group);
                }
                bool voltagePass = VoltagePass(v, config.VoltageLimitKv);
                int bin = Math.Max(0, Math.Min(group.Bins, Round(x)));
                group.Rows.Add(new Row(i, bin, voltagePass));
                if (q > config.ChargeThresholdPc)
                    group.Seeds.Add(new Interval(Math.Max(0, Round(x - length / 100)),
                        Math.Min(group.Bins, Round(x + length / 100))));
                int countBin = (int)Math.Floor(x);
                if (voltagePass && countBin < group.Bins) group.CountBins.Add(countBin);
                result.EligibleRows++;
            }

            // Sparse sorted lists: O(N log N) work and O(N) memory independent of sum(cable lengths).
            // Every eligible event votes once, including repeated LOC IDs and subthreshold charge.
            foreach (Group group in groups.Values)
            {
                group.CountBins.Sort();
                MergeSeeds(group.Seeds);
                double densityThreshold = Math.Max(config.MinimumClusterSize,
                    (double)group.CountBins.Count / group.Bins * group.Width * config.Strength / 10);
                int to = config.ToM.HasValue ? config.ToM.Value : group.Bins;
                foreach (Row row in group.Rows)
                {
                    if (!row.VoltagePass) { voltageExcluded++; continue; }
                    if ((config.FromM > 0 && row.Bin <= config.FromM) || (to < group.Bins && row.Bin >= to))
                    { rangeExcluded++; continue; }
                    if (!Seeded(group.Seeds, row.Bin)) { noSeed++; continue; }
                    bool unknown = false;
                    // Terminal mask[n] is not traversed by the native density loop.
                    if (config.Strength > 5 && row.Bin < group.Bins)
                    {
                        int left = row.Bin - group.Half, right = row.Bin + group.Half;
                        if (Math.Max(Math.Abs(left), right) >= group.Bins) unknown = true;
                        else
                        {
                            int count = Count(group.CountBins, Math.Max(0, left), right);
                            // Negative indexes are mirrored with abs, so positive bins can vote twice.
                            if (left < 0) count += Count(group.CountBins, 1, -left);
                            if (count < densityThreshold) { densityExcluded++; continue; }
                        }
                    }
                    result.KeepMask[row.Source] = true;
                    result.RetainedRows++;
                    if (row.Bin == group.Bins) terminalRetained++;
                    if (unknown) { result.UnknownMask[row.Source] = true; result.UnknownRows++; }
                }
            }
            StringBuilder json = new StringBuilder(1024);
            json.Append("{\"algorithm\":\"LocationFilterB.ClusterStrength\",\"version\":\"").Append(Version)
                .Append("\",\"evidence\":[\"0x624680\",\"0x6418a8\",\"0x6245a0\"],")
                .Append("\"classification\":\"spatial display filtering only; not physical truth or probability\",")
                .Append("\"unknownPolicy\":\"keep for review; never confirmed support\",")
                .Append("\"terminalPolicy\":\"mask[n] skips density; retention is not support evidence\",")
                .Append("\"adaptations\":\"complete accepted rows; x>=0; v>0 before and after f32; phase 0 separate; exact length groups; strength 0..100; finite f32 events\",")
                .Append("\"rounding\":\"f32 events then nearest-even; native x87 state not replayed\",")
                .Append("\"densityEnabled\":").Append(config.Strength > 5 ? "true" : "false").Append(',');
            json.Append("\"parameters\":{");
            Real(json, "Strength", config.Strength); Real(json, "ChargeThresholdPc", config.ChargeThresholdPc);
            Real(json, "VoltageLimitKv", config.VoltageLimitKv); Number(json, "MinimumClusterSize", config.MinimumClusterSize);
            Number(json, "FromM", config.FromM);
            json.Append("\"ToM\":").Append(config.ToM.HasValue ? config.ToM.Value.ToString(CultureInfo.InvariantCulture) : "null").Append("},");
            Number(json, "rows", rowCount); Number(json, "eligibleRows", result.EligibleRows);
            Number(json, "retainedRows", result.RetainedRows); Number(json, "unknownRows", result.UnknownRows);
            Number(json, "knownMaskRetainedRows", result.RetainedRows - result.UnknownRows);
            Number(json, "terminalMaskRetainedRows", terminalRetained);
            Number(json, "groups", groups.Count); Number(json, "incompleteRows", incomplete);
            Number(json, "unacceptedRows", unaccepted); Number(json, "invalidRows", invalid);
            Number(json, "unrepresentableRows", unrepresentable); Number(json, "voltageExcludedRows", voltageExcluded);
            Number(json, "rangeExcludedRows", rangeExcluded); Number(json, "noSeedRows", noSeed);
            Number(json, "densityExcludedRows", densityExcluded);
            json.Append("\"groupParameters\":[");
            bool firstGroup = true;
            foreach (Group group in groups.Values)
            {
                if (!firstGroup) json.Append(','); firstGroup = false;
                json.Append('{'); Number(json, "phase", group.Phase); Real(json, "lengthM", group.Length);
                Number(json, "bins", group.Bins); Number(json, "w", group.Width); Number(json, "h", group.Half);
                Number(json, "actualSumBins", 2 * group.Half + 1); Number(json, "countedEvents", group.CountBins.Count);
                Real(json, "meanDensity", (double)group.CountBins.Count / group.Bins);
                Real(json, "densityThreshold", Math.Max(config.MinimumClusterSize,
                    (double)group.CountBins.Count / group.Bins * group.Width * config.Strength / 10));
                json.Append("\"densityEnabled\":").Append(config.Strength > 5 ? "true" : "false").Append('}');
            }
            json.Append("],\"unknownSourceRows\":[");
            bool first = true;
            for (int i = 0; i < rowCount; i++) if (result.UnknownMask[i])
            { if (!first) json.Append(','); json.Append(i.ToString(CultureInfo.InvariantCulture)); first = false; }
            result.DiagnosticsJson = json.Append("]}").ToString();
            return result;
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static int Round(double value) { return (int)Math.Round(value, MidpointRounding.ToEven); }
        private static bool VoltagePass(double voltage, double limit)
        {
            // MaxValue is the host's no-limit default; avoid overflowing limit*10.
            return limit > double.MaxValue / 10 || Math.Round(voltage * 10, MidpointRounding.ToEven) <= Math.Round(limit * 10, MidpointRounding.ToEven);
        }
        private static void Number(StringBuilder json, string key, int value)
        { json.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture)).Append(','); }
        private static void Real(StringBuilder json, string key, double value)
        { json.Append('"').Append(key).Append("\":").Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(','); }
        private static int LowerBound(List<int> values, int value)
        {
            int low = 0, high = values.Count;
            while (low < high) { int middle = low + (high - low) / 2; if (values[middle] < value) low = middle + 1; else high = middle; }
            return low;
        }
        private static int Count(List<int> values, int low, int high)
        { return low > high ? 0 : LowerBound(values, high + 1) - LowerBound(values, low); }
        private static bool Seeded(List<Interval> intervals, int bin)
        {
            int low = 0, high = intervals.Count;
            while (low < high) { int middle = low + (high - low) / 2; if (intervals[middle].Low <= bin) low = middle + 1; else high = middle; }
            return low > 0 && intervals[low - 1].High >= bin;
        }
        private static void MergeSeeds(List<Interval> intervals)
        {
            intervals.Sort(delegate(Interval a, Interval b) { int c = a.Low.CompareTo(b.Low); return c != 0 ? c : a.High.CompareTo(b.High); });
            int written = 0;
            for (int i = 0; i < intervals.Count; i++)
            {
                Interval current = intervals[i];
                if (written > 0 && current.Low <= intervals[written - 1].High + 1)
                { Interval previous = intervals[written - 1]; previous.High = Math.Max(previous.High, current.High); intervals[written - 1] = previous; }
                else intervals[written++] = current;
            }
            if (written < intervals.Count) intervals.RemoveRange(written, intervals.Count - written);
        }
        private struct GroupKey : IEquatable<GroupKey>
        {
            private readonly int phase; private readonly double length;
            public GroupKey(int phase, double length) { this.phase = phase; this.length = length; }
            public bool Equals(GroupKey other) { return phase == other.phase && length == other.length; }
            public override bool Equals(object value) { return value is GroupKey && Equals((GroupKey)value); }
            public override int GetHashCode() { return unchecked(phase * 397 ^ length.GetHashCode()); }
        }
        private struct Row
        {
            public readonly int Source, Bin; public readonly bool VoltagePass;
            public Row(int source, int bin, bool voltagePass) { Source = source; Bin = bin; VoltagePass = voltagePass; }
        }
        private struct Interval
        {
            public int Low, High;
            public Interval(int low, int high) { Low = low; High = high; }
        }
        private sealed class Group
        {
            public readonly int Bins, Width, Half;
            public readonly int Phase;
            public readonly double Length;
            public readonly List<Row> Rows = new List<Row>();
            public readonly List<int> CountBins = new List<int>();
            public readonly List<Interval> Seeds = new List<Interval>();
            public Group(int phase, double length) { Phase = phase; Length = length; Bins = Round(length); Width = Math.Max(1, (int)Math.Ceiling(length / 100)); Half = Round(Width / 2.0); }
        }
    }
}
