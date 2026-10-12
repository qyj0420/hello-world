using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Project;
using Xiongxin.Owts.Contracts.Report;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 报告导出最小实现：只读 SQLite 中**已保存的分析快照**（AnalysisId 为空 = 最新一条），
    /// 输出 HTML + Markdown；若快照关联 acquisition 且有波形旁路文件，附 SVG 折线，
    /// 并可选同内容 PNG（<see cref="WaveformPngChart"/> 纯托管；失败则跳过，SVG 仍在）。
    /// 无快照则拒绝导出。结论未迁入时如实写「未迁入」，不伪造；图上不标定位点。
    /// </summary>
    public sealed class MinimalHtmlReportExporter : IReportExporter
    {
        public const string ConclusionNotMigratedText = "未迁入（Task15 / ConclusionAlgorithm 尚未迁移，本报告不给出结论）";

        readonly IProjectRepository _repository;

        public MinimalHtmlReportExporter(IProjectRepository repository)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            _repository = repository;
        }

        public ReportExportResult Export(ReportExportRequest request)
        {
            if (request == null) request = new ReportExportRequest();
            AnalysisPackageDto analysis;
            ProjectDraftDto project;
            try
            {
                analysis = _repository.LoadAnalysis(request.AnalysisId);
                project = _repository.LoadProject();
            }
            catch (Exception ex)
            {
                return Failed("读取工程/分析失败：" + ex.Message);
            }
            if (analysis == null)
                return Failed("无已保存的分析快照，拒绝导出（请先运行滤波/定位并保存分析）。");

            var ctx = AnalysisSnapshotWriter.ParseContext(analysis.ReportInputContext);
            bool conclusionMigrated = Get(ctx, "conclusion.success") == "true";
            string conclusionText = conclusionMigrated ? Get(ctx, "conclusion.message") : ConclusionNotMigratedText;

            string dir = !string.IsNullOrWhiteSpace(request.ProjectPath) ? request.ProjectPath
                : !string.IsNullOrWhiteSpace(request.ProjectDbPath) ? Path.GetDirectoryName(Path.GetFullPath(request.ProjectDbPath))
                : Path.GetTempPath();
            string reportDir = Path.Combine(dir, "reports");
            Directory.CreateDirectory(reportDir);
            string stem = "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + analysis.Id.ToString("N").Substring(0, 8);
            string htmlPath = Path.Combine(reportDir, stem + ".html");
            string mdPath = Path.Combine(reportDir, stem + ".md");

            var warnings = new List<string>();
            var chartRels = new List<string>();
            var pngRels = new List<string>();
            var chartNotes = new List<string>();
            AttachWaveformCharts(dir, reportDir, stem, analysis, chartRels, pngRels, chartNotes, warnings);

            File.WriteAllText(htmlPath, BuildHtml(project, analysis, ctx, conclusionText, chartRels, pngRels, chartNotes), new UTF8Encoding(true));
            File.WriteAllText(mdPath, BuildMarkdown(project, analysis, ctx, conclusionText, chartRels, pngRels, chartNotes), new UTF8Encoding(false));

            if (!conclusionMigrated) warnings.Add("结论未迁入：报告未给出结论。");
            if (Get(ctx, "source") == "simulation") warnings.Add("数据来源为仿真，不可作为现场试验报告。");
            warnings.Add("PDF/Word 正式模板未迁入；当前为 HTML/Markdown 最小报告。");
            if (chartRels.Count == 0)
                warnings.Add("无关联波形附图（分析未绑 acquisition 或旁路文件缺失/不可读）。");

            try
            {
                _repository.InsertReportArtifact(new ReportArtifactDto { AnalysisId = analysis.Id, Format = "html", RelPath = Path.Combine("reports", stem + ".html") });
                _repository.InsertReportArtifact(new ReportArtifactDto { AnalysisId = analysis.Id, Format = "md", RelPath = Path.Combine("reports", stem + ".md") });
                foreach (string rel in chartRels)
                    _repository.InsertReportArtifact(new ReportArtifactDto { AnalysisId = analysis.Id, Format = "svg", RelPath = rel });
                foreach (string rel in pngRels)
                {
                    if (string.IsNullOrEmpty(rel)) continue;
                    _repository.InsertReportArtifact(new ReportArtifactDto { AnalysisId = analysis.Id, Format = "png", RelPath = rel });
                }
            }
            catch (Exception ex)
            {
                warnings.Add("报告文件已生成，但登记 report_artifact 失败：" + ex.Message);
            }

            var outputs = new List<string> { htmlPath, mdPath };
            foreach (string rel in chartRels)
                outputs.Add(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
            int pngCount = 0;
            foreach (string rel in pngRels)
            {
                if (string.IsNullOrEmpty(rel)) continue;
                pngCount++;
                outputs.Add(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
            }

            string chartMsg = "";
            if (chartRels.Count > 0)
            {
                chartMsg = " + " + chartRels.Count + " SVG";
                if (pngCount > 0) chartMsg += "/" + pngCount + " PNG";
                else chartMsg += "（PNG 跳过）";
            }

            return new ReportExportResult
            {
                Success = true,
                StatusMessage = "已从分析快照 " + analysis.Id.ToString("D") + " 导出 HTML/Markdown" + chartMsg,
                OutputPaths = outputs.ToArray(),
                UsedAnalysisId = analysis.Id,
                Warnings = warnings.ToArray()
            };
        }

        void AttachWaveformCharts(
            string projectDir,
            string reportDir,
            string stem,
            AnalysisPackageDto analysis,
            List<string> chartRels,
            List<string> pngRels,
            List<string> chartNotes,
            List<string> warnings)
        {
            if (analysis.AcquisitionId == Guid.Empty)
            {
                warnings.Add("分析快照未关联 acquisition.id，跳过波形附图。");
                return;
            }
            IReadOnlyList<WaveformFileDto> files;
            try
            {
                files = _repository.ListWaveformFiles(analysis.AcquisitionId);
            }
            catch (Exception ex)
            {
                warnings.Add("ListWaveformFiles 失败：" + ex.Message);
                return;
            }
            if (files == null || files.Count == 0)
            {
                warnings.Add("acquisition 无 waveform_file 记录。");
                return;
            }

            int chartIndex = 0;
            foreach (WaveformFileDto wf in files)
            {
                if (wf == null || string.IsNullOrWhiteSpace(wf.RelPath)) continue;
                string ch = wf.Channel ?? string.Empty;
                if (ch.Equals("meta", StringComparison.OrdinalIgnoreCase)) continue;

                string abs = Path.Combine(projectDir, wf.RelPath.Replace('/', Path.DirectorySeparatorChar));
                string unit;
                string loadErr;
                double[] samples = WaveformSvgChart.TryLoadSamples(abs, ch, out unit, out loadErr);
                if (samples == null || samples.Length == 0)
                {
                    if (loadErr != "SkipMeta")
                        warnings.Add("波形跳过 channel=" + ch + "：" + (loadErr ?? "empty"));
                    continue;
                }

                chartIndex++;
                string safeCh = SanitizeFileToken(ch);
                string title = "通道 " + ch + "（已存快照，无定位标注）";

                string svgName = stem + "-wf" + chartIndex.ToString() + "-" + safeCh + ".svg";
                string absSvg = Path.Combine(reportDir, svgName);
                string svg = WaveformSvgChart.RenderSvg(samples, title, wf.SampleRateHz, unit);
                File.WriteAllText(absSvg, svg, new UTF8Encoding(false));
                string svgRel = Path.Combine("reports", svgName).Replace('\\', '/');
                chartRels.Add(svgRel);

                string pngName = stem + "-wf" + chartIndex.ToString() + "-" + safeCh + ".png";
                string absPng = Path.Combine(reportDir, pngName);
                string pngErr = null;
                string pngRelAdded = null;
                if (WaveformPngChart.IsAvailable && WaveformPngChart.TryWritePng(absPng, samples, title, wf.SampleRateHz, unit, out pngErr))
                {
                    pngRelAdded = Path.Combine("reports", pngName).Replace('\\', '/');
                }
                else if (!string.IsNullOrEmpty(pngErr))
                {
                    warnings.Add("PNG 跳过 channel=" + ch + "：" + pngErr);
                }
                else if (!WaveformPngChart.IsAvailable)
                {
                    warnings.Add("PNG 不可用（依赖未就绪），已保留 SVG channel=" + ch);
                }
                // 与 chartRels 对齐：失败时占位空串，HTML/MD 回退 SVG
                pngRels.Add(pngRelAdded ?? string.Empty);

                chartNotes.Add(ch + " | n=" + samples.Length
                    + (wf.SampleRateHz > 0 ? " | fs=" + wf.SampleRateHz.ToString("G6") + "Hz" : "")
                    + " | " + wf.RelPath);
            }
        }

        static string SanitizeFileToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "ch";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            return sb.Length == 0 ? "ch" : sb.ToString();
        }

        static string BuildHtml(ProjectDraftDto p, AnalysisPackageDto a, IList<KeyValuePair<string, string>> ctx,
            string conclusion, IList<string> chartRels, IList<string> pngRels, IList<string> chartNotes)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>OWTS 试验报告（最小版）</title>");
            sb.Append("<style>body{font-family:'Microsoft YaHei',sans-serif;margin:24px}table{border-collapse:collapse;margin:8px 0 16px}td,th{border:1px solid #999;padding:4px 10px;text-align:left}.warn{color:#b00;font-weight:bold}figure{margin:16px 0}figcaption{color:#555;font-size:13px}img.wf{max-width:100%;border:1px solid #ccc}</style></head><body>");
            sb.Append("<h1>OWTS 振荡波局放试验报告（最小版）</h1>");
            if (Get(ctx, "source") == "simulation") sb.Append("<p class=\"warn\">数据来源：仿真。不可作为现场试验报告。</p>");
            sb.Append("<h2>工程信息</h2><table>");
            foreach (var row in ProjectRows(p)) Row(sb, row.Key, row.Value);
            sb.Append("</table><h2>分析快照</h2><table>");
            Row(sb, "分析 ID", a.Id.ToString("D"));
            Row(sb, "算法", a.AlgorithmId);
            Row(sb, "采集 ID", a.AcquisitionId == Guid.Empty ? "(未关联)" : a.AcquisitionId.ToString("D"));
            Row(sb, "保存时间 (UTC)", a.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (var kv in ctx) if (!kv.Key.StartsWith("conclusion.", StringComparison.Ordinal)) Row(sb, kv.Key, kv.Value);
            sb.Append("</table>");

            sb.Append("<h2>波形附图</h2>");
            if (chartRels == null || chartRels.Count == 0)
            {
                sb.Append("<p class=\"warn\">无波形附图（未关联已存波形）。</p>");
            }
            else
            {
                for (int i = 0; i < chartRels.Count; i++)
                {
                    string svgName = Path.GetFileName(chartRels[i].Replace('/', Path.DirectorySeparatorChar));
                    string imgName = svgName;
                    if (pngRels != null && i < pngRels.Count && !string.IsNullOrWhiteSpace(pngRels[i]))
                        imgName = Path.GetFileName(pngRels[i].Replace('/', Path.DirectorySeparatorChar));
                    string note = (chartNotes != null && i < chartNotes.Count) ? chartNotes[i] : chartRels[i];
                    sb.Append("<figure><figcaption>").Append(Enc(note)).Append("</figcaption>");
                    sb.Append("<img class=\"wf\" src=\"").Append(Enc(imgName)).Append("\" alt=\"waveform\"/>");
                    if (!string.Equals(imgName, svgName, StringComparison.OrdinalIgnoreCase))
                        sb.Append("<p><a href=\"").Append(Enc(svgName)).Append("\">SVG</a></p>");
                    sb.Append("</figure>");
                }
                sb.Append("<p>说明：附图仅复现已落盘波形折线，不标注定位点或结论。优先嵌入 PNG（若已生成），SVG 同步保留。</p>");
            }

            sb.Append("<h2>结论</h2><p class=\"warn\">").Append(Enc(conclusion)).Append("</p>");
            sb.Append("<p>生成时间：").Append(Enc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append("</p></body></html>");
            return sb.ToString();
        }

        static string BuildMarkdown(ProjectDraftDto p, AnalysisPackageDto a, IList<KeyValuePair<string, string>> ctx,
            string conclusion, IList<string> chartRels, IList<string> pngRels, IList<string> chartNotes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# OWTS 振荡波局放试验报告（最小版）").AppendLine();
            if (Get(ctx, "source") == "simulation") sb.AppendLine("> **数据来源：仿真。不可作为现场试验报告。**").AppendLine();
            sb.AppendLine("## 工程信息").AppendLine().AppendLine("| 项 | 值 |").AppendLine("|---|---|");
            foreach (var row in ProjectRows(p)) sb.AppendLine("| " + Md(row.Key) + " | " + Md(row.Value) + " |");
            sb.AppendLine().AppendLine("## 分析快照").AppendLine().AppendLine("| 项 | 值 |").AppendLine("|---|---|");
            sb.AppendLine("| 分析 ID | " + a.Id.ToString("D") + " |");
            sb.AppendLine("| 算法 | " + Md(a.AlgorithmId) + " |");
            sb.AppendLine("| 采集 ID | " + (a.AcquisitionId == Guid.Empty ? "(未关联)" : a.AcquisitionId.ToString("D")) + " |");
            foreach (var kv in ctx) if (!kv.Key.StartsWith("conclusion.", StringComparison.Ordinal)) sb.AppendLine("| " + Md(kv.Key) + " | " + Md(kv.Value) + " |");
            sb.AppendLine().AppendLine("## 波形附图").AppendLine();
            if (chartRels == null || chartRels.Count == 0)
            {
                sb.AppendLine("无波形附图（未关联已存波形）。");
            }
            else
            {
                for (int i = 0; i < chartRels.Count; i++)
                {
                    string svgName = Path.GetFileName(chartRels[i].Replace('/', Path.DirectorySeparatorChar));
                    string imgName = svgName;
                    if (pngRels != null && i < pngRels.Count && !string.IsNullOrWhiteSpace(pngRels[i]))
                        imgName = Path.GetFileName(pngRels[i].Replace('/', Path.DirectorySeparatorChar));
                    string note = (chartNotes != null && i < chartNotes.Count) ? chartNotes[i] : chartRels[i];
                    sb.AppendLine("- " + note);
                    sb.AppendLine("  ![" + Md(note) + "](" + imgName + ")");
                    if (!string.Equals(imgName, svgName, StringComparison.OrdinalIgnoreCase))
                        sb.AppendLine("  - SVG: [" + svgName + "](" + svgName + ")");
                }
                sb.AppendLine().AppendLine("附图仅复现已落盘波形折线，不标注定位点或结论。优先嵌入 PNG（若已生成），SVG 同步保留。");
            }
            sb.AppendLine().AppendLine("## 结论").AppendLine().AppendLine("**" + conclusion + "**");
            return sb.ToString();
        }

        static IEnumerable<KeyValuePair<string, string>> ProjectRows(ProjectDraftDto p)
        {
            if (p == null) { yield return new KeyValuePair<string, string>("工程", "(未加载)"); yield break; }
            yield return new KeyValuePair<string, string>("单位", p.Organization);
            yield return new KeyValuePair<string, string>("操作员", p.OperatorName);
            yield return new KeyValuePair<string, string>("线路", p.LineName);
            yield return new KeyValuePair<string, string>("电缆模式", p.CableMode);
            yield return new KeyValuePair<string, string>("相电压", p.PhaseVoltage);
            yield return new KeyValuePair<string, string>("长度 A/B/C (m)", p.LengthA + " / " + p.LengthB + " / " + p.LengthC);
        }

        static void Row(StringBuilder sb, string k, string v)
        {
            sb.Append("<tr><th>").Append(Enc(k)).Append("</th><td>").Append(Enc(v)).Append("</td></tr>");
        }

        static string Enc(string s) { return WebUtility.HtmlEncode(s ?? string.Empty); }
        static string Md(string s) { return (s ?? string.Empty).Replace("|", "\\|"); }

        static string Get(IList<KeyValuePair<string, string>> ctx, string key)
        {
            foreach (var kv in ctx) if (kv.Key == key) return kv.Value;
            return string.Empty;
        }

        static ReportExportResult Failed(string message)
        {
            return new ReportExportResult { Success = false, StatusMessage = message };
        }
    }
}
