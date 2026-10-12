using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// Capture full Worker statusMessage / exceptionChain / detected_pairs /
    /// output_threshold_rejected / added_rows for a measurement (no fabricated points).
    /// v2: dated bundle folder + CAL stub header preview + retest checklist (CALv5 path).
    /// </summary>
    public static class DacStatusJsonCapture
    {
        public const string CaptureId = "DacStatusJsonCapture/v2";

        public sealed class CaptureResult
        {
            public long MeasurementId;
            public int Algo;
            public double Thr;
            public bool AnalyzerSuccess;
            public string AnalyzerStatus;
            public int NewCount;
            public string NewPositionsM;
            public string DumpPath;
            public string Note;
            public string StubDetail;
            public string ProjectPath;
            public string BundleDir;
        }

        public sealed class BundleResult
        {
            public string BundleDir;
            public string ChecklistPath;
            public string ReadmePath;
            public IList<CaptureResult> Captures = new List<CaptureResult>();
            public string Note;
        }

        /// <summary>
        /// Run Sixth (algo5) and Project (algo7) at given thr on mid; write dumps into a dated
        /// bundle under outDir (or outDir itself when createBundleFolder=false).
        /// </summary>
        public static IList<CaptureResult> CaptureMid(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            long measurementId,
            string outDir,
            double thr = 2.0,
            int[] algos = null)
        {
            BundleResult bundle = CaptureMidBundle(analyzer, import, measurementId, outDir, thr, algos, true);
            return bundle != null ? bundle.Captures : new List<CaptureResult>();
        }

        /// <summary>
        /// CALv5 / mid5 retest entry: create dated folder, dump StatusJson for each algo,
        /// copy CAL header preview, write RETEST_CHECKLIST.txt. No thr1; no fabricated points.
        /// </summary>
        public static BundleResult CaptureMidBundle(
            ILocationAnalyzer analyzer,
            DacImportResult import,
            long measurementId,
            string outDir,
            double thr = 2.0,
            int[] algos = null,
            bool createBundleFolder = true)
        {
            var bundle = new BundleResult
            {
                Captures = new List<CaptureResult>(),
                Note = CaptureId + ";brand=" + StatusCopy.BrandCompany + ";NOT_FIELD_LOCATION_DECISION"
            };
            if (analyzer == null || import == null || !import.Success)
            {
                bundle.Note += ";fail=import_or_analyzer";
                return bundle;
            }
            DacMeasurementWaveform wave = DacFileImporter.TryGetMeasurement(import, measurementId);
            if (wave == null || wave.EngineeringSamples == null || wave.EngineeringSamples.Length < 8)
            {
                bundle.Note += ";fail=wave_missing";
                return bundle;
            }

            Directory.CreateDirectory(outDir ?? ".");
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string bundleDir = outDir;
            if (createBundleFolder)
            {
                bundleDir = Path.Combine(outDir,
                    string.Format(CultureInfo.InvariantCulture, "statusjson_calv5_mid{0}_{1}", measurementId, stamp));
                Directory.CreateDirectory(bundleDir);
            }
            bundle.BundleDir = bundleDir;

            if (algos == null || algos.Length == 0) algos = new[] { 5, 7 };

            string sharedStubDetail = "";
            string sharedProjectPath = "";

            foreach (int algo in algos)
            {
                string dumpName = string.Format(CultureInfo.InvariantCulture,
                    "mid{0}_statusjson_algo{1}_thr{2}_{3}.json",
                    measurementId, algo,
                    thr.ToString("G4", CultureInfo.InvariantCulture).Replace('.', 'p'),
                    stamp);
                string latestName = string.Format(CultureInfo.InvariantCulture,
                    "mid{0}_statusjson_algo{1}_thr{2}_latest.json",
                    measurementId, algo,
                    thr.ToString("G4", CultureInfo.InvariantCulture).Replace('.', 'p'));
                string dumpPath = Path.Combine(bundleDir, dumpName);
                string latestInBundle = Path.Combine(bundleDir, latestName);
                // Also keep flat latest under outDir for existing scripts / pairing-sweep.
                string latestFlat = Path.Combine(outDir, latestName);

                string unitStatus;
                bool scaled;
                double factorUsed;
                string stubDetail;
                LocationAnalysisRequest req = DacLocationCompare.BuildRequest(
                    import, wave, out unitStatus, out scaled, out factorUsed, out stubDetail);
                sharedStubDetail = stubDetail ?? "";
                sharedProjectPath = req != null ? (req.ProjectPath ?? "") : "";

                var tune = DacCompareTuning.Default.Clone();
                tune.Id = "statusjson_algo" + algo + "_thr" + thr.ToString("G4", CultureInfo.InvariantCulture);
                tune.AlgorithmIndex = algo;
                tune.AutomaticThresholdMultiplier = thr;
                tune.ApplyTo(req);
                req.StatusDumpPath = dumpPath;

                LocationAnalysisResult loc = analyzer.AnalyzeIncremental(req);
                var cap = new CaptureResult
                {
                    MeasurementId = measurementId,
                    Algo = algo,
                    Thr = thr,
                    AnalyzerSuccess = loc != null && loc.Success,
                    AnalyzerStatus = loc != null ? (loc.Status ?? "") : "null",
                    DumpPath = dumpPath,
                    StubDetail = stubDetail ?? "",
                    ProjectPath = sharedProjectPath,
                    BundleDir = bundleDir,
                    Note = CaptureId + ";scaled=" + (scaled ? "1" : "0")
                        + ";unit=" + Truncate(unitStatus, 120)
                        + ";stub=" + Truncate(stubDetail, 160)
                        + ";NOT_FIELD_LOCATION_DECISION"
                };
                double[] pos = loc != null
                    ? DacLocationCompare.ExtractPositions(loc.Parameters, cap.AnalyzerStatus, measurementId)
                    : new double[0];
                if (pos.Length == 0 && loc != null && loc.Parameters != null && loc.Parameters.RowCount > 0)
                    pos = DacLocationCompare.ExtractPositions(loc.Parameters, cap.AnalyzerStatus, null);
                cap.NewCount = pos.Length;
                cap.NewPositionsM = Join(pos);

                if (File.Exists(dumpPath))
                {
                    TryCopy(dumpPath, latestInBundle);
                    TryCopy(dumpPath, latestFlat);
                    WriteMeta(dumpPath, cap);
                }
                else
                {
                    WriteFallbackDump(dumpPath, latestInBundle, cap, loc);
                    TryCopy(dumpPath, latestFlat);
                    WriteMeta(dumpPath, cap);
                }

                bundle.Captures.Add(cap);
            }

            WriteCalHeaderPreview(bundleDir, sharedProjectPath, sharedStubDetail);
            bundle.ChecklistPath = WriteChecklist(bundleDir, import, measurementId, thr, sharedStubDetail, bundle.Captures);
            bundle.ReadmePath = WriteBundleReadme(bundleDir, import, measurementId, thr, sharedStubDetail);
            return bundle;
        }

        static void WriteMeta(string dumpPath, CaptureResult cap)
        {
            try
            {
                var meta = new StringBuilder();
                meta.AppendLine("capture=" + CaptureId);
                meta.AppendLine("brand=" + StatusCopy.BrandCompany);
                meta.AppendLine("mid=" + cap.MeasurementId.ToString(CultureInfo.InvariantCulture));
                meta.AppendLine("algo=" + cap.Algo.ToString(CultureInfo.InvariantCulture));
                meta.AppendLine("thr=" + cap.Thr.ToString("G4", CultureInfo.InvariantCulture));
                meta.AppendLine("analyzer_success=" + (cap.AnalyzerSuccess ? "1" : "0"));
                meta.AppendLine("new_count=" + cap.NewCount.ToString(CultureInfo.InvariantCulture));
                meta.AppendLine("new_m=" + cap.NewPositionsM);
                meta.AppendLine("stub=" + Truncate(cap.StubDetail, 400));
                meta.AppendLine("project=" + Truncate(cap.ProjectPath, 400));
                meta.AppendLine("bundle=" + Truncate(cap.BundleDir, 400));
                meta.AppendLine("status=" + Truncate(cap.AnalyzerStatus, 500));
                meta.AppendLine("note=" + cap.Note);
                File.WriteAllText(dumpPath + ".meta.txt", meta.ToString(), new UTF8Encoding(false));
            }
            catch { /* ignore */ }
        }

        static void WriteCalHeaderPreview(string bundleDir, string projectPath, string stubDetail)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("capture=" + CaptureId);
                sb.AppendLine("stub=" + (stubDetail ?? ""));
                sb.AppendLine("project=" + (projectPath ?? ""));
                sb.AppendLine("---");
                if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
                {
                    string[] cals = Directory.GetFiles(projectPath, "*.cal");
                    Array.Sort(cals, StringComparer.OrdinalIgnoreCase);
                    foreach (string cal in cals)
                    {
                        sb.AppendLine("file=" + Path.GetFileName(cal));
                        string[] lines = File.ReadAllLines(cal, Encoding.UTF8);
                        int n = Math.Min(lines.Length, 16);
                        for (int i = 0; i < n; i++)
                        {
                            string line = lines[i] ?? "";
                            // Skip huge Data rows; only header keys.
                            if (line.StartsWith("Data", StringComparison.OrdinalIgnoreCase) && line.Trim().Length <= 8)
                            {
                                sb.AppendLine(line);
                                sb.AppendLine("… (Data samples omitted; see stub .cal on disk)");
                                break;
                            }
                            if (line.Length > 200) line = line.Substring(0, 200) + "…";
                            sb.AppendLine(line);
                        }
                        sb.AppendLine("---");
                    }
                    if (cals.Length == 0)
                        sb.AppendLine("(no *.cal in stub project)");
                }
                else
                {
                    sb.AppendLine("(stub project path missing — Worker may not have run; box without net48 is expected)");
                }
                File.WriteAllText(Path.Combine(bundleDir, "cal_stub_header.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { /* ignore */ }
        }

        static string WriteChecklist(
            string bundleDir,
            DacImportResult import,
            long mid,
            double thr,
            string stubDetail,
            IList<CaptureResult> caps)
        {
            string path = Path.Combine(bundleDir, "RETEST_CHECKLIST.txt");
            var sb = new StringBuilder();
            sb.AppendLine(StatusCopy.BrandCompany + " · CALv5 / mid" + mid + " StatusJson 复测核对清单");
            sb.AppendLine("capture=" + CaptureId);
            sb.AppendLine("dac=" + (import != null ? import.SourcePath : ""));
            sb.AppendLine("thr=" + thr.ToString("G4", CultureInfo.InvariantCulture) + " (禁止 thr1)");
            sb.AppendLine("stub=" + (stubDetail ?? ""));
            sb.AppendLine();
            sb.AppendLine("请从各 mid*_statusjson_*.json 填回（或把整个本目录打 zip 回传）：");
            sb.AppendLine("[ ] calibration_reliable / attenuation_reference_* （CAL 参考是否可用）");
            sb.AppendLine("[ ] cal_src 是否含 dac_plot_pc；Units=pC；peaks；atten_db（对照 cal_stub_header.txt）");
            sb.AppendLine("[ ] discovered_pulses（期望相对旧 StatusJson 的 3；勿用 thr1 抬高）");
            sb.AppendLine("[ ] detected_pairs_before_output_filter / accepted_pairs / paired_count");
            sb.AppendLine("[ ] unpaired_rows / added_rows / output_threshold_rejected");
            sb.AppendLine("[ ] pair_low_band_snr_* / pearson_* / fwhm_* 拒因是否仍在");
            sb.AppendLine("[ ] new_count / new_m（vs pd_map=7 for mid5）；nn 仅作对照，不伪造点");
            sb.AppendLine("[ ] algo5 与 algo7(→inner4) 是否均 worker_ok");
            sb.AppendLine();
            sb.AppendLine("本轮捕获摘要：");
            if (caps != null)
            {
                foreach (CaptureResult c in caps)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  algo={0} ok={1} new={2} dump={3}",
                        c.Algo, c.AnalyzerSuccess ? 1 : 0, c.NewCount, Path.GetFileName(c.DumpPath)));
                }
            }
            sb.AppendLine();
            sb.AppendLine("禁止：thr1 洪水、Near/hs 切片凑条数、降硬门 SNR/Pearson/FWHM。");
            sb.AppendLine("仍不可验收现场定位，直至 pairs/事件数与 pd_map 同量级且位置齐。");
            sb.AppendLine("详见 docs/ACCURACY_COMPARE.md §3.15 / §3.16。");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static string WriteBundleReadme(
            string bundleDir,
            DacImportResult import,
            long mid,
            double thr,
            string stubDetail)
        {
            string path = Path.Combine(bundleDir, "README.txt");
            var sb = new StringBuilder();
            sb.AppendLine(StatusCopy.BrandCompany + " · StatusJson dump bundle (CALv5 retest)");
            sb.AppendLine("capture=" + CaptureId);
            sb.AppendLine("mid=" + mid.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("thr=" + thr.ToString("G4", CultureInfo.InvariantCulture));
            sb.AppendLine("dac=" + (import != null ? import.SourcePath : ""));
            sb.AppendLine("stub=" + (stubDetail ?? ""));
            sb.AppendLine();
            sb.AppendLine("Files:");
            sb.AppendLine("  mid*_statusjson_algo{5|7}_thr2_*.json  — Worker StatusJson (or fallback summary)");
            sb.AppendLine("  *.meta.txt                              — capture metadata + stub");
            sb.AppendLine("  cal_stub_header.txt                     — Task17ProjectStubBuilder/v5 CAL header");
            sb.AppendLine("  RETEST_CHECKLIST.txt                    — fields to report back");
            sb.AppendLine();
            sb.AppendLine("One-click (user PC): tools/Run-StatusJsonDump-CalV5.ps1");
            sb.AppendLine("CLI: --statusjson-dump <outDir> <dac> <mid> [algoCsv] [thr]");
            sb.AppendLine("NOT_FIELD_LOCATION_DECISION");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        static void WriteFallbackDump(string dumpPath, string latestPath, CaptureResult cap, LocationAnalysisResult loc)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ok\":").Append(cap.AnalyzerSuccess ? "true" : "false").Append(',');
            sb.Append("\"errorCode\":\"").Append(Esc(cap.AnalyzerSuccess ? "" : "NoWorkerStatusDump")).Append("\",");
            sb.Append("\"capture\":\"").Append(Esc(CaptureId)).Append("\",");
            sb.Append("\"brand\":\"").Append(Esc(StatusCopy.BrandCompany)).Append("\",");
            sb.Append("\"mid\":").Append(cap.MeasurementId.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"algo\":").Append(cap.Algo.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"thr\":").Append(cap.Thr.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"new_count\":").Append(cap.NewCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"new_m\":\"").Append(Esc(cap.NewPositionsM)).Append("\",");
            sb.Append("\"stub\":\"").Append(Esc(Truncate(cap.StubDetail, 400))).Append("\",");
            sb.Append("\"analyzer_status\":\"").Append(Esc(Truncate(cap.AnalyzerStatus, 4000))).Append("\",");
            sb.Append("\"detected_pairs_before_output_filter\":null,");
            sb.Append("\"output_threshold_rejected\":null,");
            sb.Append("\"discovered_pulses\":null,");
            sb.Append("\"added_rows\":null,");
            sb.Append("\"exceptionChain\":\"").Append(Esc(ExtractChain(cap.AnalyzerStatus))).Append("\",");
            sb.Append("\"statusMessage\":\"").Append(Esc(ExtractAlgoStatus(cap.AnalyzerStatus))).Append("\",");
            sb.Append("\"note\":\"worker_statusDump_missing_or_simplified_fallback;NOT_FIELD_LOCATION_DECISION\"");
            sb.Append('}');
            string json = sb.ToString();
            File.WriteAllText(dumpPath, json, new UTF8Encoding(false));
            TryCopy(dumpPath, latestPath);
        }

        static void TryCopy(string src, string dst)
        {
            try { if (File.Exists(src)) File.Copy(src, dst, true); } catch { /* ignore */ }
        }

        static string ExtractChain(string status)
        {
            if (string.IsNullOrEmpty(status)) return "";
            int i = status.IndexOf("chain=", StringComparison.Ordinal);
            if (i < 0) i = status.IndexOf("exceptionChain=", StringComparison.Ordinal);
            if (i < 0) return "";
            int start = status.IndexOf('=', i) + 1;
            int end = status.IndexOf(';', start);
            if (end < 0) end = Math.Min(status.Length, start + 800);
            return status.Substring(start, end - start);
        }

        static string ExtractAlgoStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return "";
            int i = status.IndexOf("algoStatus=", StringComparison.Ordinal);
            if (i < 0) return Truncate(status, 2000);
            return Truncate(status.Substring(i + "algoStatus=".Length), 4000);
        }

        static string Join(double[] pos)
        {
            if (pos == null || pos.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < pos.Length; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(pos[i].ToString("G6", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
