using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Waveform;
using Xiongxin.Owts.Persistence;
using Xiongxin.Owts.HostAdapters.AHistory;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// .dac 导入 → 校准换算(V→pC) → LocationAnalysisRequest（useLocData）→ ILocationAnalyzer → 与 pd_map 摘要对照。
    /// 不伪造定位点；对照仅展示两侧已有数值。
    /// </summary>
    public static class DacLocationCompare
    {
        public const string CompareId = "DacLocationCompare/v8";
        /// <summary>
        /// Overlay / Task17 PositionM：源码 <c>parameters[r,13]</c> 为 <b>0-based index 13</b>
        ///（[12]=Δsamples，[13]=output_position_m）。不是「1-based 列号13→index12」。
        /// </summary>
        public const int Task17PositionColumnIndex = 13;
        /// <summary>SimplifiedPeakScan 把估计位置写在 col0。</summary>
        public const int SimplifiedPositionColumnIndex = 0;

        public static LocationAnalysisRequest BuildRequest(
            DacImportResult import,
            DacMeasurementWaveform wave,
            string stubProjectPath = null,
            string locationFilePath = null)
        {
            string unitStatus;
            bool scaled;
            double factorUsed;
            string stubDetail;
            return BuildRequest(import, wave, out unitStatus, out scaled, out factorUsed, out stubDetail,
                stubProjectPath, locationFilePath);
        }

        /// <summary>构造请求并返回单位换算状态（供对照摘要写入）。</summary>
        public static LocationAnalysisRequest BuildRequest(
            DacImportResult import,
            DacMeasurementWaveform wave,
            out string unitStatus,
            out bool scaled,
            out double factorUsed,
            string stubProjectPath = null,
            string locationFilePath = null)
        {
            string stubDetail;
            return BuildRequest(import, wave, out unitStatus, out scaled, out factorUsed, out stubDetail,
                stubProjectPath, locationFilePath);
        }

        /// <summary>
        /// 构造请求；无 stub 路径时自动 <see cref="Task17ProjectStubBuilder"/>（Status/Note 标 stub=auto）。
        /// </summary>
        public static LocationAnalysisRequest BuildRequest(
            DacImportResult import,
            DacMeasurementWaveform wave,
            out string unitStatus,
            out bool scaled,
            out double factorUsed,
            out string stubDetail,
            string stubProjectPath = null,
            string locationFilePath = null)
        {
            if (wave == null) throw new ArgumentNullException("wave");
            double fs = wave.SampleRateHz > 0 ? wave.SampleRateHz : 1e8;
            factorUsed = wave.CalibrationFactorLoc > 0
                ? wave.CalibrationFactorLoc
                : (import != null && import.CalibrationFactorLoc > 0 ? import.CalibrationFactorLoc : double.NaN);
            double[] locData;
            scaled = DacCalibrationScale.TryScaleVoltsToPc(
                wave.EngineeringSamples ?? new double[0], factorUsed, out locData, out unitStatus);

            stubDetail = string.Empty;
            string projectPath = stubProjectPath ?? string.Empty;
            string locPath = locationFilePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(locPath))
            {
                Task17ProjectStubBuilder.StubResult stub = Task17ProjectStubBuilder.Build(import, wave);
                stubDetail = stub != null ? (stub.Detail ?? string.Empty) : "stub=missing";
                if (stub != null && stub.Success)
                {
                    if (string.IsNullOrWhiteSpace(projectPath))
                        projectPath = stub.ProjectDirectory;
                    if (string.IsNullOrWhiteSpace(locPath))
                        locPath = stub.LocationFilePath;
                }
            }
            else
            {
                stubDetail = "stub=caller";
            }

            var req = new LocationAnalysisRequest
            {
                ProjectPath = projectPath,
                LocationFilePath = locPath,
                LocationDataId = wave.MeasurementId,
                UseLocData = true,
                LocData = locData,
                SampleRateHz = fs,
                ConnectingCableLengthM = 0
            };
            // 默认可扫参数（原软件 TaskMinus3）；可用 CompareTuning.Default / 扫参覆盖
            DacCompareTuning.Default.ApplyTo(req);
            return req;
        }

        public static DacCompareSummary CompareOne(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            long measurementId,
            string stubProjectPath = null,
            string locationFilePath = null,
            DacCompareTuning tuning = null,
            ParameterHistory carryHistory = null,
            AHistorySession aHistorySession = null)
        {
            var summary = new DacCompareSummary { MeasurementId = measurementId };
            if (analyzer == null || import == null || !import.Success)
            {
                summary.Note = "ImportOrAnalyzerMissing";
                summary.UnitStatus = "units=unknown";
                return summary;
            }

            DacMeasurementWaveform wave = DacFileImporter.TryGetMeasurement(import, measurementId);
            if (wave == null || wave.EngineeringSamples == null || wave.EngineeringSamples.Length < 8)
            {
                summary.Note = "WaveformMissingOrTooShort";
                summary.UnitStatus = "units=unknown";
                return summary;
            }

            IList<DacPdMapEntry> baselineEntries = DacFileImporter.BaselineEntriesFor(import, measurementId);
            double[] baseline = new double[baselineEntries.Count];
            int[] termCodes = new int[baselineEntries.Count];
            for (int bi = 0; bi < baselineEntries.Count; bi++)
            {
                baseline[bi] = baselineEntries[bi].PdLocationM;
                termCodes[bi] = baselineEntries[bi].TerminationPdLoc;
            }
            summary.BaselinePositionsM = baseline;
            summary.BaselineTerminationCodes = termCodes;
            summary.BaselineCount = baseline.Length;

            string unitStatus;
            bool scaled;
            double factorUsed;
            string stubDetail;
            LocationAnalysisRequest req = BuildRequest(
                import, wave, out unitStatus, out scaled, out factorUsed, out stubDetail,
                stubProjectPath, locationFilePath);
            DacCompareTuning tune = tuning ?? DacCompareTuning.Default;
            if (tune != null) tune.ApplyTo(req);
            summary.UnitStatus = unitStatus;
            summary.CalibrationScaled = scaled;
            summary.CalibrationFactorUsed = factorUsed;

            // AHistory Select → previous* (GUI-equivalent). Fallback: legacy carry append-only.
            int histRows = 0;
            AHistorySelection ahistSel = null;
            int phaseCode = ResolvePhaseCode(wave);
            if (aHistorySession != null)
            {
                ahistSel = aHistorySession.SelectFor(phaseCode, measurementId);
                req.BundleHistory = AHistoryEvidenceSelect.ToBundleHistory(ahistSel);
                histRows = ahistSel != null ? ahistSel.QualifiedRows : 0;
            }
            else if (carryHistory != null)
            {
                req.BundleHistory = carryHistory;
                if (carryHistory.PreviousParameters != null)
                    histRows = carryHistory.PreviousParameters.RowCount;
            }

            LocationAnalysisResult loc = analyzer.AnalyzeIncremental(req);
            if (loc == null)
            {
                summary.Note = "AnalyzerReturnedNull;" + unitStatus + ";" + Truncate(stubDetail, 160);
                return summary;
            }

            summary.AnalyzerSuccess = loc.Success;
            // Surface stub=auto on Status so CSV / UI show how project files were obtained.
            string analyzerStatus = loc.Status ?? string.Empty;
            if (!string.IsNullOrEmpty(stubDetail) && analyzerStatus.IndexOf("stub=", StringComparison.Ordinal) < 0)
                analyzerStatus = analyzerStatus + ";" + Truncate(stubDetail, 220);
            if (analyzerStatus.IndexOf("history=", StringComparison.Ordinal) < 0)
            {
                analyzerStatus = analyzerStatus
                    + ";history=" + (histRows > 0 ? "incremental" : "none")
                    + ";hist_rows=" + histRows.ToString(CultureInfo.InvariantCulture);
            }
            if (aHistorySession != null && analyzerStatus.IndexOf("ahistory=", StringComparison.Ordinal) < 0)
                analyzerStatus = analyzerStatus + ";" + AHistoryEvidenceSelect.StatusToken(ahistSel);
            summary.AnalyzerStatus = analyzerStatus;

            // AutomaticAcceptance / Seventh writeback (col33/35) before Extract + AHistory Capture.
            // Only promotes when score≥0.70 evidence exists; never blind-fills 1.
            string acceptWb = "";
            if (loc.Success && IsRealTask17Status(summary.AnalyzerStatus) && loc.Parameters != null
                && req.AutomaticAcceptance)
            {
                acceptWb = WorkerAcceptWriteback.Apply(
                    loc.Parameters,
                    req.AutomaticAcceptance,
                    req.AttenuationTolerancePercent);
                if (!string.IsNullOrEmpty(acceptWb) && analyzerStatus.IndexOf("accept_wb=", StringComparison.Ordinal) < 0)
                {
                    analyzerStatus = analyzerStatus + ";" + acceptWb;
                    summary.AnalyzerStatus = analyzerStatus;
                }
            }

            // Cumulative Task17 output retains prior locationDataId rows — filter to this mid.
            double[] positions = ExtractPositions(loc.Parameters, summary.AnalyzerStatus, measurementId);
            if (positions.Length == 0 && loc.Parameters != null && loc.Parameters.RowCount > 0)
            {
                // id-col filter miss (DLL layout) or empty new batch: prefer append-only slice.
                if (histRows > 0 && loc.Parameters.RowCount > histRows)
                    positions = ExtractPositionsFromRowOffset(loc.Parameters, summary.AnalyzerStatus, histRows);
                else if (histRows == 0)
                    positions = ExtractPositions(loc.Parameters, summary.AnalyzerStatus, null);
            }
            summary.NewPositionsM = positions;
            summary.NewRowCount = positions.Length;

            double L = import.CableLengthM;
            double[] aligned = DacEndpointAlign.AlignMany(positions, L);
            summary.AlignedPositionsM = aligned;
            summary.NnRawM = DacEndpointAlign.NearestNeighborAbs(baseline, positions);
            summary.NnAlignedM = DacEndpointAlign.NearestNeighborAbs(baseline, aligned);
            summary.NnBodyM = DacEndpointAlign.NearestNeighborBodyAbs(baselineEntries, aligned);
            summary.FarEndpointHit = DacEndpointAlign.FarEndpointHit(baselineEntries, aligned, L);
            summary.AlignNote = DacEndpointAlign.CorrespondenceNote()
                + ";nn_raw_m=" + DacEndpointAlign.FormatMeters(summary.NnRawM)
                + ";nn_aligned_m=" + DacEndpointAlign.FormatMeters(summary.NnAlignedM)
                + ";nn_body_m=" + DacEndpointAlign.FormatMeters(summary.NnBodyM)
                + ";far_hit=" + (summary.FarEndpointHit ? "1" : "0");

            // AHistory session: Capture Anchor-qualified rows for later Select.
            // Legacy carry: feed full cumulative Task17 matrices (append-only).
            if (aHistorySession != null && loc.Success && IsRealTask17Status(summary.AnalyzerStatus))
            {
                aHistorySession.TryCapture(measurementId, phaseCode, loc);
            }
            else if (carryHistory != null && loc.Success && IsRealTask17Status(summary.AnalyzerStatus)
                && loc.Parameters != null && loc.Parameters.RowCount > 0
                && loc.Parameters.Values != null)
            {
                carryHistory.PreviousParameters = CloneParameterMatrix(loc.Parameters);
                carryHistory.PreviousRaw = CloneParameterMatrix(loc.Raw);
                carryHistory.PreviousFiltered = CloneParameterMatrix(loc.Filtered);
                carryHistory.PreviousEquivalent = CloneParameterMatrix(loc.Equivalent);
            }

            DacCompareTuning noteTune = tuning ?? DacCompareTuning.Default;
            summary.Note = CompareId
                + ";L=" + L.ToString("G6", CultureInfo.InvariantCulture)
                + ";v_cal=" + import.CalibratedVelocityMPerUs.ToString("G9", CultureInfo.InvariantCulture)
                + ";fs=" + wave.SampleRateHz.ToString("G6", CultureInfo.InvariantCulture)
                + ";samples=" + wave.EngineeringSamples.Length
                + ";baseline=" + baseline.Length
                + ";new=" + positions.Length
                + ";scaled=" + (scaled ? "1" : "0")
                + ";factor=" + DacCalibrationScale.FormatFactor(factorUsed)
                + ";endpoint=" + (req.EndpointPolicy ?? DacCalibrationScale.DefaultEndpointPolicy)
                + ";term_note=" + DacCalibrationScale.TerminationPdLocNote
                + ";align=" + DacEndpointAlign.AlignId
                + ";nn_aligned_m=" + DacEndpointAlign.FormatMeters(summary.NnAlignedM)
                + ";nn_body_m=" + DacEndpointAlign.FormatMeters(summary.NnBodyM)
                + ";far_hit=" + (summary.FarEndpointHit ? "1" : "0")
                + ";history=" + (histRows > 0 ? "incremental" : "none")
                + ";hist_rows=" + histRows.ToString(CultureInfo.InvariantCulture)
                + (aHistorySession != null ? (";" + AHistoryEvidenceSelect.StatusToken(ahistSel)) : "")
                + (string.IsNullOrEmpty(acceptWb) ? "" : (";" + acceptWb))
                + ";" + (noteTune != null ? noteTune.ToStatusToken() : "tune=?")
                + ";" + Truncate(stubDetail, 160)
                + ";NOT_FIELD_LOCATION_DECISION";
            return summary;
        }

        /// <summary>
        /// 对 import 内全部（或指定）measurement 跑对照；默认最多 maxMeasurements 条。
        /// 默认 <paramref name="incrementalHistory"/>=true：按 mid 升序链式；
        /// previous* 经 <see cref="AHistoryEvidenceSelect"/>（Anchor + Task28）筛选，Status 标 ahistory=on。
        /// </summary>
        public static IList<DacCompareSummary> CompareMany(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            int maxMeasurements = 5,
            IEnumerable<long> measurementIds = null,
            string stubProjectPath = null,
            string locationFilePath = null,
            DacCompareTuning tuning = null,
            bool incrementalHistory = true)
        {
            var list = new List<DacCompareSummary>();
            if (import == null || import.Measurements == null) return list;

            HashSet<long> filter = null;
            if (measurementIds != null)
            {
                filter = new HashSet<long>();
                foreach (long id in measurementIds) filter.Add(id);
            }

            var ordered = new List<long>();
            for (int i = 0; i < import.Measurements.Count; i++)
            {
                long mid = import.Measurements[i].MeasurementId;
                if (filter != null && !filter.Contains(mid)) continue;
                ordered.Add(mid);
                if (filter == null && ordered.Count >= maxMeasurements) break;
            }
            ordered.Sort();

            // AHistory session replaces naive cumulative carry when incrementalHistory is on.
            AHistorySession ahist = incrementalHistory ? new AHistorySession() : null;
            for (int i = 0; i < ordered.Count; i++)
            {
                list.Add(CompareOne(analyzer, import, ordered[i], stubProjectPath, locationFilePath, tuning, null, ahist));
            }
            return list;
        }

        /// <summary>
        /// AHistory/Task17 phase code 1..3 (A/B/C).
        /// .dac measurements.phase is 0-based (0=A,1=B,2=C) — same as Task17ProjectStubBuilder.
        /// </summary>
        static int ResolvePhaseCode(DacMeasurementWaveform wave)
        {
            if (wave == null) return 1;
            int p = wave.Phase;
            if (p >= 0 && p <= 2) return p + 1;
            if (p >= 1 && p <= 3) return p; // already 1-based
            return 1;
        }

        public static string FormatSummaryText(DacImportResult import, IList<DacCompareSummary> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CompareId + " — 原 pd_map vs 新定位（不伪造点；非现场定位决策）");
            if (import != null)
            {
                sb.AppendLine("Source: " + (import.SourcePath ?? ""));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "CableLengthM={0:G6}  ConfigV={1:G6} m/µs  CalV={2:G9} m/µs  pd_map={3}  waves={4}  cal_points={5}",
                    import.CableLengthM, import.ConfiguredVelocityMPerUs, import.CalibratedVelocityMPerUs,
                    import.PdMapBaseline != null ? import.PdMapBaseline.Count : 0,
                    import.Measurements != null ? import.Measurements.Count : 0,
                    import.CalibrationPoints != null ? import.CalibrationPoints.Count : 0));
                sb.AppendLine("Import: " + (import.Message ?? ""));
                sb.AppendLine("Scale: " + DacCalibrationScale.FormulaId + " ; EndpointPolicy default=" + DacCalibrationScale.DefaultEndpointPolicy + " ; tune=" + (DacCompareTuning.Default != null ? DacCompareTuning.Default.Id : "?"));
                sb.AppendLine("Align: " + DacEndpointAlign.CorrespondenceNote());
            }
            if (rows == null || rows.Count == 0)
            {
                sb.AppendLine("(no compare rows)");
                return sb.ToString();
            }
            for (int i = 0; i < rows.Count; i++)
            {
                DacCompareSummary s = rows[i];
                sb.AppendLine("--- measurement_id=" + s.MeasurementId
                    + " Success=" + s.AnalyzerSuccess
                    + " newRows=" + s.NewRowCount
                    + " baseline=" + s.BaselineCount
                    + " scaled=" + s.CalibrationScaled
                    + " far_hit=" + s.FarEndpointHit);
                sb.AppendLine("  Status: " + Truncate(s.AnalyzerStatus, 240));
                sb.AppendLine("  Units:  " + Truncate(s.UnitStatus, 240));
                sb.AppendLine("  pd_map positions (m): " + JoinDoubles(s.BaselinePositionsM, 12));
                sb.AppendLine("  term_pd_loc:           " + JoinInts(s.BaselineTerminationCodes, 64));
                sb.AppendLine("  new positions (m):    " + JoinDoubles(s.NewPositionsM, 12));
                sb.AppendLine("  new aligned (m):      " + JoinDoubles(s.AlignedPositionsM, 12));
                sb.AppendLine("  nn_raw_m=" + DacEndpointAlign.FormatMeters(s.NnRawM)
                    + " nn_aligned_m=" + DacEndpointAlign.FormatMeters(s.NnAlignedM)
                    + " nn_body_m=" + DacEndpointAlign.FormatMeters(s.NnBodyM));
                sb.AppendLine("  Note: " + (s.Note ?? ""));
            }
            sb.AppendLine("提醒：new_m 为引擎原始出数（不伪造）；aligned 仅对照折叠（±2% + excess→L）。体点中位用 nn_body（剔 term=2）。不得用于现场定位决策。");
            return sb.ToString();
        }

        /// <summary>
        /// 写出 CSV：mid,pd_map_m,new_m,status（另附 factor/scaled/unit 列便于验收）。
        /// 多位置时 pd_map_m / new_m 用 ';' 连接；不伪造点。
        /// </summary>
        public static void WriteCompareCsv(string path, DacImportResult import, IList<DacCompareSummary> rows)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("mid,pd_map_m,term_pd_loc,new_m,new_m_aligned,nn_raw_m,nn_aligned_m,nn_body_m,far_hit,status,factor,scaled,unit_status,analyzer_success,dac,L_m,v_cal");
            string dacName = import != null && !string.IsNullOrEmpty(import.SourcePath)
                ? Path.GetFileName(import.SourcePath) : "";
            string L = import != null ? import.CableLengthM.ToString("G6", CultureInfo.InvariantCulture) : "";
            string vCal = import != null ? import.CalibratedVelocityMPerUs.ToString("G9", CultureInfo.InvariantCulture) : "";

            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    DacCompareSummary s = rows[i];
                    string status = CsvEscape(BuildRowStatus(s));
                    sb.Append(s.MeasurementId.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.BaselinePositionsM, 64, ";"))).Append(',')
                        .Append(CsvEscape(JoinInts(s.BaselineTerminationCodes, 64))).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.NewPositionsM, 64, ";"))).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.AlignedPositionsM, 64, ";"))).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnRawM)).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnAlignedM)).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnBodyM)).Append(',')
                        .Append(s.FarEndpointHit ? "1" : "0").Append(',')
                        .Append(status).Append(',')
                        .Append(DacCalibrationScale.FormatFactor(s.CalibrationFactorUsed)).Append(',')
                        .Append(s.CalibrationScaled ? "1" : "0").Append(',')
                        .Append(CsvEscape(s.UnitStatus ?? "")).Append(',')
                        .Append(s.AnalyzerSuccess ? "1" : "0").Append(',')
                        .Append(CsvEscape(dacName)).Append(',')
                        .Append(L).Append(',')
                        .Append(vCal)
                        .AppendLine();
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string BuildRowStatus(DacCompareSummary s)
        {
            if (s == null) return "unknown";
            if (!string.IsNullOrEmpty(s.Note) && s.Note.StartsWith("ImportOrAnalyzer", StringComparison.Ordinal))
                return "import_or_analyzer_missing";
            if (!string.IsNullOrEmpty(s.Note) && s.Note.StartsWith("WaveformMissing", StringComparison.Ordinal))
                return "waveform_missing";
            if (!s.CalibrationScaled)
                return "units_unconfirmed;NOT_FIELD_LOCATION_DECISION";
            if (!s.AnalyzerSuccess)
                return "analyzer_failed;scaled_pc;NOT_FIELD_LOCATION_DECISION";
            if (s.NewRowCount == 0)
                return "no_new_positions;scaled_pc;NOT_FIELD_LOCATION_DECISION";
            string st = s.AnalyzerStatus ?? "";
            string histTok = "";
            if (st.IndexOf("history=incremental", StringComparison.Ordinal) >= 0)
                histTok = ";history=incremental";
            else if (st.IndexOf("history=none", StringComparison.Ordinal) >= 0)
                histTok = ";history=none";
            string ahistTok = "";
            if (st.IndexOf("ahistory=on", StringComparison.Ordinal) >= 0)
            {
                ahistTok = ";ahistory=on";
                foreach (string key in new[] { "ahist_state=", "ahist_qualified=", "ahist_candidates=" })
                {
                    int i = st.IndexOf(key, StringComparison.Ordinal);
                    if (i < 0) continue;
                    int j = st.IndexOf(';', i);
                    ahistTok += ";" + (j < 0 ? st.Substring(i) : st.Substring(i, j - i));
                }
            }
            if (IsRealTask17Status(st))
                return "ok_task17;scaled_pc" + histTok + ahistTok + ";NOT_FIELD_LOCATION_DECISION";
            if (st.IndexOf("Simplified", StringComparison.Ordinal) >= 0
                || st.IndexOf("fallback=Simplified", StringComparison.Ordinal) >= 0)
                return "ok_simplified_not_tdr;scaled_pc;NOT_FIELD_LOCATION_DECISION";
            return "ok_analyzer;scaled_pc;NOT_FIELD_LOCATION_DECISION";
        }

        public static double[] ExtractPositions(ParameterMatrixDto matrix, string status)
        {
            return ExtractPositions(matrix, status, null);
        }

        /// <summary>
        /// 抽取位置。Task17 路径取 Overlay <c>[13]</c>（0-based；[12]=Δsamples）；
        /// 若提供 <paramref name="locationDataIdFilter"/>，仅保留参数矩阵 col1==id 的行（增量历史累积后的本 mid 新增）。
        /// </summary>
        public static double[] ExtractPositions(ParameterMatrixDto matrix, string status, long? locationDataIdFilter)
        {
            if (matrix == null || matrix.Values == null || matrix.RowCount <= 0)
                return new double[0];
            int cols = ParameterMatrixDto.ColumnCount;
            bool preferTask17 = IsRealTask17Status(status);
            int col = preferTask17 ? Task17PositionColumnIndex : SimplifiedPositionColumnIndex;
            var list = new List<double>(matrix.RowCount);
            for (int r = 0; r < matrix.RowCount; r++)
            {
                if (preferTask17 && locationDataIdFilter.HasValue)
                {
                    int idIdx = r * cols + 1; // col1 = locationDataId
                    if (idIdx >= matrix.Values.Length) break;
                    double idVal = matrix.Values[idIdx];
                    if (double.IsNaN(idVal) || Math.Abs(idVal - locationDataIdFilter.Value) > 0.5)
                        continue;
                }
                int idx = r * cols + col;
                if (idx >= matrix.Values.Length) break;
                double v = matrix.Values[idx];
                if (preferTask17 && (double.IsNaN(v) || double.IsInfinity(v)))
                {
                    int alt = r * cols + SimplifiedPositionColumnIndex;
                    if (alt < matrix.Values.Length) v = matrix.Values[alt];
                }
                if (!double.IsNaN(v) && !double.IsInfinity(v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        static bool IsRealTask17Status(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            if (status.IndexOf("fallback=Simplified", StringComparison.Ordinal) >= 0) return false;
            if (status.IndexOf("inproc_failed", StringComparison.Ordinal) >= 0) return false;
            if (status.IndexOf("worker_ok_false", StringComparison.Ordinal) >= 0) return false;
            if (status.IndexOf("worker=absent", StringComparison.Ordinal) >= 0) return false;
            if (status.IndexOf("Task17", StringComparison.Ordinal) < 0) return false;
            return ContainsToken(status, "path=worker")
                || status.IndexOf("worker=net48", StringComparison.Ordinal) >= 0
                || ContainsToken(status, "path=inproc")
                || status.IndexOf("runtime=task17_host", StringComparison.Ordinal) >= 0;
        }

        static ParameterMatrixDto CloneParameterMatrix(ParameterMatrixDto src)
        {
            if (src == null) return new ParameterMatrixDto();
            var values = src.Values != null ? (double[])src.Values.Clone() : new double[0];
            return new ParameterMatrixDto { RowCount = src.RowCount, Values = values };
        }

        /// <summary>Append-only：只取 rowOffset 之后的新行位置（历史回退路径）。</summary>
        static double[] ExtractPositionsFromRowOffset(ParameterMatrixDto matrix, string status, int rowOffset)
        {
            if (matrix == null || matrix.Values == null || matrix.RowCount <= rowOffset || rowOffset < 0)
                return new double[0];
            int cols = ParameterMatrixDto.ColumnCount;
            bool preferTask17 = IsRealTask17Status(status);
            int col = preferTask17 ? Task17PositionColumnIndex : SimplifiedPositionColumnIndex;
            var list = new List<double>();
            for (int r = rowOffset; r < matrix.RowCount; r++)
            {
                int idx = r * cols + col;
                if (idx >= matrix.Values.Length) break;
                double v = matrix.Values[idx];
                if (preferTask17 && (double.IsNaN(v) || double.IsInfinity(v)))
                {
                    int alt = r * cols + SimplifiedPositionColumnIndex;
                    if (alt < matrix.Values.Length) v = matrix.Values[alt];
                }
                if (!double.IsNaN(v) && !double.IsInfinity(v))
                    list.Add(v);
            }
            return list.ToArray();
        }

        static string JoinInts(int[] a, int max, string sep = ";")
        {
            if (a == null || a.Length == 0) return "";
            var sb = new StringBuilder();
            int n = Math.Min(max, a.Length);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(a[i].ToString(CultureInfo.InvariantCulture));
            }
            if (a.Length > n) sb.Append(sep).Append("…(+").Append(a.Length - n).Append(')');
            return sb.ToString();
        }

        static string JoinDoubles(double[] a, int max, string sep = ", ")
        {
            if (a == null || a.Length == 0) return "(none)";
            var sb = new StringBuilder();
            int n = Math.Min(max, a.Length);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(sep);
                sb.Append(a[i].ToString("G6", CultureInfo.InvariantCulture));
            }
            if (a.Length > n) sb.Append(sep).Append("…(+").Append(a.Length - n).Append(')');
            return sb.ToString();
        }

        static string CsvEscape(string s)
        {
            if (s == null) return "";
            bool need = s.IndexOfAny(new[] { ',', '"', '\r', '\n', ';' }) >= 0
                || s.IndexOf("(none)", StringComparison.Ordinal) >= 0;
            // always quote position fields that may contain ';'
            if (s.IndexOf(';') >= 0) need = true;
            if (!need) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }


        static bool ContainsToken(string status, string token)
        {
            int i = status.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return false;
            int end = i + token.Length;
            if (end >= status.Length) return true;
            char c = status[end];
            return c == ';' || c == '|' || c == ' ' || c == ',';
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>
        /// 写出扫参明细 CSV（含 tuning_id）；不伪造点。
        /// </summary>
        public static void WriteSweepDetailCsv(
            string path,
            DacImportResult import,
            string tuningId,
            string tuningCompact,
            IList<DacCompareSummary> rows)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool append = File.Exists(path) && new FileInfo(path).Length > 0;
            var sb = new StringBuilder();
            if (!append)
                sb.AppendLine("tuning_id,tuning,mid,pd_map_m,term_pd_loc,new_m,new_m_aligned,nn_raw_m,nn_aligned_m,nn_body_m,far_hit,new_rows,baseline,status,factor,scaled,dac,L_m");

            string dacName = import != null && !string.IsNullOrEmpty(import.SourcePath)
                ? Path.GetFileName(import.SourcePath) : "";
            string L = import != null ? import.CableLengthM.ToString("G6", CultureInfo.InvariantCulture) : "";

            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    DacCompareSummary s = rows[i];
                    sb.Append(CsvEscape(tuningId ?? "")).Append(',')
                        .Append(CsvEscape(tuningCompact ?? "")).Append(',')
                        .Append(s.MeasurementId.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.BaselinePositionsM, 64, ";"))).Append(',')
                        .Append(CsvEscape(JoinInts(s.BaselineTerminationCodes, 64))).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.NewPositionsM, 64, ";"))).Append(',')
                        .Append(CsvEscape(JoinDoubles(s.AlignedPositionsM, 64, ";"))).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnRawM)).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnAlignedM)).Append(',')
                        .Append(DacEndpointAlign.FormatMeters(s.NnBodyM)).Append(',')
                        .Append(s.FarEndpointHit ? "1" : "0").Append(',')
                        .Append(s.NewRowCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(s.BaselineCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(CsvEscape(BuildRowStatus(s))).Append(',')
                        .Append(DacCalibrationScale.FormatFactor(s.CalibrationFactorUsed)).Append(',')
                        .Append(s.CalibrationScaled ? "1" : "0").Append(',')
                        .Append(CsvEscape(dacName)).Append(',')
                        .Append(L)
                        .AppendLine();
                }
            }
            if (append) File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            else File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>中位数（忽略 NaN）；空则 NaN。</summary>
        public static double MedianFinite(IList<double> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            var list = new List<double>();
            for (int i = 0; i < values.Count; i++)
                if (!double.IsNaN(values[i]) && !double.IsInfinity(values[i]))
                    list.Add(values[i]);
            if (list.Count == 0) return double.NaN;
            list.Sort();
            int n = list.Count;
            if ((n & 1) == 1) return list[n / 2];
            return 0.5 * (list[n / 2 - 1] + list[n / 2]);
        }
    }
}

