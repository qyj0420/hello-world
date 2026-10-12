using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 有依据的对齐实验：查清 mid 条数差（pd_map N vs Worker 1）。
    /// 依据：
    /// 1) .dac pd_map.high_speed_index_orig 为旧采集栈事件锚点（与 refl 恒等）；
    /// 2) Sixth paired_automatic_window_policy：incident_pre=ceil(0.5*one_way); reflection_post=one_way after round_trip；
    /// 3) Project algorithmIndex=7 为 GUI 可选 B 方法（dac-source AlgorithmABChoice / TaskMinus3）。
    /// 不伪造点；切片只喂已有波形窗，位置仍由引擎给出。
    /// </summary>
    public static class DacPdMapAlignExperiment
    {
        public const string ExperimentId = "DacPdMapAlignExperiment/v1";

        public sealed class Row
        {
            public long MeasurementId;
            public string Mode; // full | slice
            public int Algo;
            public double Thr;
            public int PdMapOrdinal; // -1 for full
            public double BaselineM;
            public int HsIndex;
            public int SliceStart;
            public int SliceLen;
            public int NewCount;
            public string NewPositionsM;
            public double NnBodyM;
            public bool Success;
            public string Status;
        }

        /// <summary>
        /// 对指定 mid 跑：全波 algo5/thr2、algo7/thr2、algo5/thr1；以及按每个 pd_map 锚点切片的 algo5/thr2。
        /// </summary>
        public static IList<Row> RunForMeasurement(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            long measurementId)
        {
            var rows = new List<Row>();
            if (analyzer == null || import == null || !import.Success)
                return rows;

            DacMeasurementWaveform wave = DacFileImporter.TryGetMeasurement(import, measurementId);
            if (wave == null || wave.EngineeringSamples == null || wave.EngineeringSamples.Length < 8)
                return rows;

            IList<DacPdMapEntry> baseline = DacFileImporter.BaselineEntriesFor(import, measurementId);
            double L = import.CableLengthM;
            double v = import.CalibratedVelocityMPerUs > 0
                ? import.CalibratedVelocityMPerUs
                : import.ConfiguredVelocityMPerUs;
            double fs = wave.SampleRateHz > 0 ? wave.SampleRateHz : 1e8;
            int oneWay = ComputeOneWaySamples(L, v, fs);
            int roundTrip = Math.Max(1, oneWay * 2);
            int pre = Math.Max(1, (oneWay + 1) / 2); // ceil(0.5*one_way)
            int post = roundTrip + oneWay; // reflection room after anchor
            int n = wave.EngineeringSamples.Length;

            // --- full-wave variants ---
            rows.Add(RunOne(analyzer, import, wave, measurementId, baseline, "full", 5, 2.0, -1, double.NaN, 0, 0, n));
            rows.Add(RunOne(analyzer, import, wave, measurementId, baseline, "full", 7, 2.0, -1, double.NaN, 0, 0, n));
            rows.Add(RunOne(analyzer, import, wave, measurementId, baseline, "full", 5, 1.0, -1, double.NaN, 0, 0, n));

            // --- per pd_map high_speed_index slice ---
            for (int i = 0; i < baseline.Count; i++)
            {
                DacPdMapEntry e = baseline[i];
                int hs = e.HighSpeedIndexOrig;
                if (hs < 0 || hs >= n) hs = Math.Max(0, Math.Min(n - 1, hs));
                int start = Math.Max(0, hs - pre);
                int end = Math.Min(n, hs + post);
                if (end - start < 64)
                {
                    start = Math.Max(0, hs - 32);
                    end = Math.Min(n, hs + 32);
                }
                int len = end - start;
                double[] slice = new double[len];
                Array.Copy(wave.EngineeringSamples, start, slice, 0, len);
                DacMeasurementWaveform sliced = CloneWave(wave, slice);
                rows.Add(RunOne(analyzer, import, sliced, measurementId, baseline, "slice", 5, 2.0,
                    i, e.PdLocationM, hs, start, len));
            }

            // annotate window policy once on first row note via Status suffix already
            if (rows.Count > 0)
            {
                rows[0].Status = (rows[0].Status ?? "")
                    + ";align_exp=" + ExperimentId
                    + ";one_way=" + oneWay.ToString(CultureInfo.InvariantCulture)
                    + ";pre=" + pre.ToString(CultureInfo.InvariantCulture)
                    + ";post=" + post.ToString(CultureInfo.InvariantCulture)
                    + ";L=" + L.ToString("G6", CultureInfo.InvariantCulture)
                    + ";v=" + v.ToString("G9", CultureInfo.InvariantCulture)
                    + ";NOT_FIELD_LOCATION_DECISION";
            }
            return rows;
        }

        static Row RunOne(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            DacMeasurementWaveform wave,
            long measurementId,
            IList<DacPdMapEntry> allBaseline,
            string mode,
            int algo,
            double thr,
            int ordinal,
            double baselineM,
            int hsIndex,
            int sliceStart,
            int sliceLen)
        {
            var row = new Row
            {
                MeasurementId = measurementId,
                Mode = mode,
                Algo = algo,
                Thr = thr,
                PdMapOrdinal = ordinal,
                BaselineM = baselineM,
                HsIndex = hsIndex,
                SliceStart = sliceStart,
                SliceLen = sliceLen,
                NewPositionsM = "",
                NnBodyM = double.NaN,
                Status = ""
            };

            var tune = DacCompareTuning.Default.Clone();
            tune.Id = mode + "_algo" + algo + "_thr" + thr.ToString("G4", CultureInfo.InvariantCulture);
            tune.Note = ExperimentId;
            tune.AlgorithmIndex = algo;
            tune.AutomaticThresholdMultiplier = thr;

            // CompareOne uses full mid baseline for nn; for slice we also compute vs that one entry.
            DacCompareSummary summary = DacLocationCompare.CompareOne(
                analyzer, import, measurementId, null, null, tune, null, null);

            // Rebuild request on the (possibly sliced) wave — CompareOne always pulls full wave from import.
            // So for slice mode we must call analyzer directly.
            if (string.Equals(mode, "slice", StringComparison.Ordinal))
            {
                string unitStatus;
                bool scaled;
                double factorUsed;
                string stubDetail;
                LocationAnalysisRequest req = DacLocationCompare.BuildRequest(
                    import, wave, out unitStatus, out scaled, out factorUsed, out stubDetail);
                tune.ApplyTo(req);
                LocationAnalysisResult loc = analyzer.AnalyzeIncremental(req);
                row.Success = loc != null && loc.Success;
                row.Status = loc != null ? (loc.Status ?? "") : "null";
                double[] pos = loc != null
                    ? DacLocationCompare.ExtractPositions(loc.Parameters, row.Status, measurementId)
                    : new double[0];
                if (pos.Length == 0 && loc != null && loc.Parameters != null && loc.Parameters.RowCount > 0)
                    pos = DacLocationCompare.ExtractPositions(loc.Parameters, row.Status, null);
                row.NewCount = pos.Length;
                row.NewPositionsM = Join(pos);
                if (pos.Length > 0 && !double.IsNaN(baselineM))
                {
                    double best = double.PositiveInfinity;
                    for (int i = 0; i < pos.Length; i++)
                    {
                        double d = Math.Abs(pos[i] - baselineM);
                        if (d < best) best = d;
                    }
                    row.NnBodyM = best;
                }
                row.Status = Truncate(row.Status, 240)
                    + ";pd_map_n=" + (allBaseline != null ? allBaseline.Count : 0).ToString(CultureInfo.InvariantCulture)
                    + ";mode=slice;algo=" + algo
                    + ";thr=" + thr.ToString("G4", CultureInfo.InvariantCulture)
                    + ";hs=" + hsIndex
                    + ";slice=" + sliceStart + "+" + sliceLen
                    + ";scaled=" + (scaled ? "1" : "0")
                    + ";NOT_FIELD_LOCATION_DECISION";
                return row;
            }

            // full-wave path: reuse CompareOne result (but CompareOne used Default then ApplyTo — we passed tune)
            row.Success = summary.AnalyzerSuccess;
            row.Status = Truncate(summary.AnalyzerStatus ?? "", 240)
                + ";mode=full;algo=" + algo
                + ";thr=" + thr.ToString("G4", CultureInfo.InvariantCulture)
                + ";NOT_FIELD_LOCATION_DECISION";
            row.NewCount = summary.NewRowCount;
            row.NewPositionsM = Join(summary.NewPositionsM);
            row.NnBodyM = summary.NnBodyM;
            row.BaselineM = double.NaN; // multi-baseline
            row.HsIndex = -1;
            row.SliceStart = 0;
            row.SliceLen = wave.EngineeringSamples != null ? wave.EngineeringSamples.Length : 0;
            return row;
        }

        public static void WriteCsv(string path, DacImportResult import, long measurementId, IList<Row> rows)
        {
            var sb = new StringBuilder();
            sb.Append("mid,mode,algo,thr,pd_ord,baseline_m,hs_index,slice_start,slice_len,new_count,new_m,nn_body_m,success,status,dac,L_m,v_cal,experiment\n");
            string dac = import != null ? Path.GetFileName(import.SourcePath ?? "") : "";
            string L = import != null ? import.CableLengthM.ToString("G6", CultureInfo.InvariantCulture) : "";
            string v = import != null ? import.CalibratedVelocityMPerUs.ToString("G9", CultureInfo.InvariantCulture) : "";
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                sb.Append(r.MeasurementId.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Mode ?? "").Append(',')
                    .Append(r.Algo.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.Thr.ToString("G4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.PdMapOrdinal.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(Fmt(r.BaselineM)).Append(',')
                    .Append(r.HsIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.SliceStart.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.SliceLen.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(r.NewCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append('"').Append((r.NewPositionsM ?? "").Replace("\"", "\"\"")).Append('"').Append(',')
                    .Append(Fmt(r.NnBodyM)).Append(',')
                    .Append(r.Success ? "1" : "0").Append(',')
                    .Append('"').Append((r.Status ?? "").Replace("\"", "\"\"")).Append('"').Append(',')
                    .Append(dac).Append(',')
                    .Append(L).Append(',')
                    .Append(v).Append(',')
                    .Append(ExperimentId)
                    .Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static int ComputeOneWaySamples(double lengthM, double velocityMPerUs, double sampleRateHz)
        {
            if (!(lengthM > 0) || !(velocityMPerUs > 0) || !(sampleRateHz > 0))
                return 2048;
            // one_way_s = L / (v_m_per_us * 1e6); samples = one_way_s * fs
            double samples = lengthM / (velocityMPerUs * 1e6) * sampleRateHz;
            if (samples < 1 || double.IsNaN(samples) || double.IsInfinity(samples))
                return 2048;
            return Math.Max(1, (int)Math.Round(samples));
        }

        static DacMeasurementWaveform CloneWave(DacMeasurementWaveform src, double[] samples)
        {
            return new DacMeasurementWaveform
            {
                ContainerId = src.ContainerId,
                MeasurementId = src.MeasurementId,
                Channel = src.Channel,
                Gain = src.Gain,
                Offset = src.Offset,
                DeclaredSamples = samples.Length,
                XincrementSeconds = src.XincrementSeconds,
                SampleRateHz = src.SampleRateHz,
                EngineeringSamples = samples,
                CalibrationPointId = src.CalibrationPointId,
                CalibrationFactorLoc = src.CalibrationFactorLoc,
                CalibrationChargePc = src.CalibrationChargePc,
                HvMaxKv = src.HvMaxKv,
                Phase = src.Phase
            };
        }

        static string Join(double[] values)
        {
            if (values == null || values.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(values[i].ToString("G6", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        static string Fmt(double v)
        {
            if (double.IsNaN(v)) return "";
            if (double.IsPositiveInfinity(v)) return "";
            return v.ToString("G6", CultureInfo.InvariantCulture);
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max);
        }
    }
}
