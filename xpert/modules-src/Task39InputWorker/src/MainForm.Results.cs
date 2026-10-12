using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    public sealed class BenchAnalysisRecord
    {
        public string Source;
        public VoltageAnalysisResult Voltage;
        public PdAnalysisResult Discharge;
        public RangeProposal Range;
        public LocationRangeResult LocationRange;
        public RangeDecisionRecord RangeDecision;
        public string RangeReviewStatus = "未处理";
        public int RangeRetryNumber;
        public string ParentCaptureDirectory;
        public string RetryCaptureDirectory;
        public string RetryOutcome;
        public string DetectionAlgorithm;
        public double DetectedRateHz;
        public int DetectedCount;
        public string LocationDefinition = "触发样点0起连续20ms原始Pico数据；不含距离结果，未经电缆波速/长度换算";
        public string Synchronization = "实机两卡须接收同一个外部数字边沿；卡间固定延迟尚未标定";
    }

    internal sealed partial class MainForm
    {
        private readonly RangeState rangeState = new RangeState();
        private BenchAnalysisRecord analysis;
        private short[] locationAdc;
        private void DisplayCapture(BenchCapture capture)
        {
            BenchSettings s = capture.Settings;
            var v = SignalAnalysis.AnalyzeVoltage(capture.VoltageRaw, capture.ArtRateHz, s.DividerVPerV, s.InductanceH, s.ResistanceOhms,
                capture.Simulation ? s.SimulationOscillation : s.OscillationCircuitConnected, s.TransientExclusionMs);
            var pd = SignalAnalysis.AnalyzePd(capture.PdAdc, capture.PicoRateHz, capture.MaximumAdc, capture.PicoFullScaleV,
                s.PdCalibrationPcPerV, s.TransientExclusionMs, capture.Overflow);
            locationAdc = SignalAnalysis.CopyLocation20Ms(capture.PdAdc, capture.PicoRateHz);
            // This executable only permits a zero target. Use every original sample in the
            // complete location segment, regardless of a fitted oscillation or exclusion setting.
            var rangeWindow = LocationRangeWindow.Select(s.TargetVoltageV, 0, locationAdc.Length,
                capture.PicoRateHz, 1, Double.NaN, Double.NaN, false);
            var next = LocationRangeAdvisor.Evaluate(capture.PdAdc, rangeWindow, capture.MaximumAdc, capture.PicoFullScaleV,
                s.RangeIndex, s.CalibrationPulsePc, s.PdCalibrationPcPerV, capture.Overflow);
            if (!next.WindowValid) throw new InvalidDataException("定位量程窗口无效：" + next.LocationQuality);
            var detected = DetectedWaveforms.Build(capture, pd.BaselineVolts);
            analysis = new BenchAnalysisRecord { Source = capture.Simulation ? "SIMULATION / 仿真" : "HARDWARE / 原始采集", Voltage = v, Discharge = pd,
                LocationRange = next, DetectionAlgorithm = detected.Algorithm, DetectedRateHz = detected.OutputRateHz, DetectedCount = detected.OutputCount,
                LocationDefinition = "触发样点0起 min(20ms,本轮时长) 原始Pico数据；量程评估不排除窗口内过冲；不是距离结果" };
            analysis.RangeReviewStatus = NeedsRangeDecision(capture, next) ? "待操作者确认" : s.AutoRange ? "无须调档；保留原始质量结论" : "仅建议；未启用量程弹窗";
            SaveAnalysis(capture, analysis);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(capture.DirectoryPath, "location_0_20ms_i16.bin"))))
                foreach (short value in locationAdc) writer.Write(value);
            double pdScale = capture.PicoFullScaleV / capture.MaximumAdc * s.PdCalibrationPcPerV;
            string tag = capture.Simulation ? "仿真" : "实测";
            double duration = s.CaptureDurationMs, locationMs = locationAdc.Length * 1000.0 / capture.PicoRateHz;
            voltage.Title = tag + " · 分压器电压 80 kS/s（保留符号 / 原始偏置）";
            discharge.Title = tag + " · 局放检波 80 kS/s · 绝对峰值包络 / " + detected.OutputCount.ToString("N0") + " 点";
            string quality = next.LocationClippedSamples > 0 ? "已削顶，不合格" : next.DriverOverflowUncertain ? "驱动溢出，无法确认" : next.LocationHighOccupancy ? "余量不足" : next.LocationLowOccupancy ? "量程偏大" : "量程占用合适";
            location.Title = tag + " · 原始定位窗口 0～" + locationMs.ToString("G5") + " ms · " + quality;
            voltage.Set(detected.VoltageKv, detected.OutputRateHz, 1, duration);
            discharge.Set(detected.PdEnvelopePc, detected.OutputRateHz, 1, duration);
            location.Set(locationAdc, capture.PicoRateHz, pdScale, locationMs);
            viewTabs.TabPages[0].Text = "80k 检波 · " + duration.ToString("G5") + " ms";
            viewTabs.TabPages[1].Text = "定位原始 · " + locationMs.ToString("G5") + " ms";
            string voltageMetrics = v.Valid
                ? String.Format(CultureInfo.InvariantCulture, "f {0:F2} Hz · C {1:F3} nF · 介损估计 {2:F4}% · 拟合 R² {3:F5}", v.FrequencyHz, v.CapacitanceF * 1e9, v.CorrectedTanDeltaEstimate * 100, v.FitRSquared)
                : "f / C / 介损：无效（" + v.Status + "）";
            string maximum = pd.Valid ? pd.MaximumEquivalentPc.ToString("F3") + " pC" : "无效：" + pd.Status;
            result.Text = String.Format(CultureInfo.InvariantCulture,
                "{0} · Pico 实际 {1:F3} MS/s · {2:F3} ns/点 · " + s.ResolutionBits + " bit 模式 / 量程规格 {3} bit · {4:N0} 点\n" +
                "{5}\n全段峰值 {6:G5} V；排除前 {7:G4} ms 后最大等效电量 {8}（候选 {9}；非物理局放确认）\n" +
                "本轮 {10} → 建议 {11}；{12}", tag, capture.PicoRateHz / 1e6, 1e9 / capture.PicoRateHz, capture.HardwareBits, capture.PdAdc.Length,
                voltageMetrics, pd.RawAbsolutePeakVolts, s.TransientExclusionMs, maximum, pd.PulseCandidateCount,
                range.Items[s.RangeIndex], range.Items[next.RecommendedIndex], s.AutoRange ? "处理结果见定位量程诊断" : "仅推荐，弹窗未勾选");
            result.Text += String.Format(CultureInfo.InvariantCulture, "\n定位量程：{0} · 峰值占用 {1:P1} · 削顶 {2:N0} 点（连续最长 {3:N0}）；窗外削顶 {4:N0} 点\n{5}",
                quality, next.LocationPeakFraction, next.LocationClippedSamples, next.LocationLongestClippedRun, next.OutsideClippedSamples,
                next.RecommendationUncertain ? "真实峰值/溢出位置不确定；推荐不保证消除削顶。详情见“定位量程诊断”。" : "按定位原始峰值与校准预期较大者预留35%余量，直接跨档推荐。详情见“定位量程诊断”。");
            rangeDiagnosticText.Text = String.Format(CultureInfo.InvariantCulture,
                "{0} · 定位窗口量程评估\r\n\r\n窗口：触发后 0～{1:G5} ms，共 {2:N0} 原始点；不排除窗口内过冲。\r\n本轮量程：{3}；下一轮推荐：{4}\r\n\r\n{5}\r\n峰值：{6:G6} V，占满幅 {7:P2}\r\n削顶：{8:N0} 点；最长连续削顶：{9:N0} 点\r\n\r\n窗外峰值：{10:G6} V；窗外削顶：{11:N0} 点\r\n驱动全段溢出：{12}\r\n\r\n推荐依据\r\n{13}\r\n\r\n{14}\r\n\r\n采集步骤绿色只表示数据流程完成，不代表量程、定位信噪比或设备安全通过。",
                tag, locationMs, locationAdc.Length, range.Items[s.RangeIndex], range.Items[next.RecommendedIndex], next.LocationQuality,
                next.LocationPeakVolts, next.LocationPeakFraction, next.LocationClippedSamples, next.LocationLongestClippedRun,
                next.OutsidePeakVolts, next.OutsideClippedSamples, capture.Overflow ? "是（没有逐点位置标记）" : "否", next.Reason, next.SafetyNote);
            conditions["art"].Text = conditions["pico"].Text = capture.Simulation ? "本轮仿真完成；非硬件验证" : "本轮已完整采集；连接已释放";
            conditions["complete"].Text = "● ART " + capture.VoltageRaw.Length + " 点，Pico " + capture.PdAdc.Length + " 点，完整 " + duration + " ms";
            conditions["trigger"].Text = capture.Simulation ? "仿真共享边沿；不是实机触发验证" : "两卡均完成采集；同一边沿依赖外接线，卡间偏移未标定";
            rangeDiagnosticText.AppendText("\r\n\r\n量程统计范围：" + next.WindowDefinition + "\r\n0 V 使用整段定位数据，不按过冲排除时间或振荡周期裁剪。");
            status.Text = "本轮完成。已保存原始数据及定位量程诊断；导出包含80k检波和原始定位CSV。ADC削顶不等于硬件保护动作或安全状态。";
        }

        private async Task ExportData()
        {
            if (latest == null || analysis == null || exportInProgress || cancellation != null || discovering) return;
            BenchCapture captured = latest; BenchAnalysisRecord record = analysis; short[] window = locationAdc;
            exportInProgress = true;
            export.Enabled = start.Enabled = discover.Enabled = false;
            try
            {
                string output = Path.Combine(captured.DirectoryPath, "csv_" + DateTime.Now.ToString("HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
                status.Text = "正在导出：80k检波、电压原始、局放候选和原始定位窗口……";
                await Task.Run(() => Export(captured, record, window, output));
                status.Text = "CSV 导出完成：" + output;
            }
            catch (Exception ex) { status.Text = "导出失败：" + ex.Message; }
            finally
            {
                exportInProgress = false;
                if (!IsDisposed && !Disposing)
                {
                    export.Enabled = latest != null && !discovering && cancellation == null;
                    start.Enabled = discover.Enabled = cancellation == null && !discovering;
                    if (closeAfterExport) Close();
                }
            }
        }

        internal static void Export(BenchCapture c, BenchAnalysisRecord a, short[] window, string output)
        {
            c.ValidateCapture();
            if (a == null || a.Discharge == null || window == null || window.Length != Math.Min(c.PdAdc.Length, (int)Math.Floor(.020 * c.PicoRateHz + 1e-9)))
                throw new InvalidDataException("导出分析或20ms定位窗口不完整。");
            if (Directory.Exists(output)) throw new IOException("目标导出目录已存在。");
            string pending = output + ".pending";
            if (Directory.Exists(pending)) throw new IOException("存在尚未完成的同名导出目录。");
            Directory.CreateDirectory(pending);
            var inv = CultureInfo.InvariantCulture;
            string source = c.Simulation ? "simulation" : "hardware";
            c.Settings.Save(Path.Combine(pending, "settings.xml"));
            using (var metadata = File.Create(Path.Combine(pending, "analysis.xml"))) new XmlSerializer(typeof(BenchAnalysisRecord)).Serialize(metadata, a);
            var detected = DetectedWaveforms.Build(c, a.Discharge.BaselineVolts);
            using (var writer = new StreamWriter(Path.Combine(pending, "synchronized_detected_80k.csv"), false, new UTF8Encoding(true), 65536))
            {
                writer.WriteLine("time_ms,divider_kV,pd_abs_peak_envelope_pC,pd_quality,source");
                for (int i = 0; i < detected.OutputCount; i++) writer.WriteLine((i * 1000.0 / detected.OutputRateHz).ToString("R", inv) + "," + detected.VoltageKv[i].ToString("R", inv) + "," + detected.PdEnvelopePc[i].ToString("R", inv) + "," + (a.Discharge.Clipped ? "capture_clipped_or_overflow" : "equivalent_not_confirmed_PD") + "," + source);
            }
            using (var writer = new StreamWriter(Path.Combine(pending, "voltage_raw.csv"), false, new UTF8Encoding(true), 65536))
            {
                writer.WriteLine("time_ms,AI4_raw_V,divider_kV,source");
                for (int i = 0; i < c.VoltageRaw.Length; i++) writer.WriteLine((i * 1000.0 / c.ArtRateHz).ToString("R", inv) + "," + c.VoltageRaw[i].ToString("R", inv) + "," + (c.VoltageRaw[i] * c.Settings.DividerVPerV / 1000).ToString("R", inv) + "," + (c.Simulation ? "simulation" : "hardware"));
            }
            using (var writer = new StreamWriter(Path.Combine(pending, "pd_pulse_candidates.csv"), false, new UTF8Encoding(true), 65536))
            {
                writer.WriteLine("peak_time_ms,raw_V,baseline_corrected_V,equivalent_pC,quality,source");
                int begin = (int)Math.Ceiling(c.Settings.TransientExclusionMs * c.PicoRateHz / 1000);
                int run = 0, peakIndex = 0; double peak = 0, rawPeak = 0;
                double factor = c.PicoFullScaleV / c.MaximumAdc;
                for (int i = begin; i <= c.PdAdc.Length; i++)
                {
                    double rawV = i < c.PdAdc.Length ? c.PdAdc[i] * factor : 0;
                    double v = rawV - a.Discharge.BaselineVolts;
                    if (i < c.PdAdc.Length && Math.Abs(v) > a.Discharge.CandidateThresholdVolts)
                    { run++; if (Math.Abs(v) > Math.Abs(peak)) { peak = v; rawPeak = rawV; peakIndex = i; } }
                    else
                    {
                        if (run >= 2) writer.WriteLine((peakIndex * 1000.0 / c.PicoRateHz).ToString("R", inv) + "," + rawPeak.ToString("R", inv) + "," + peak.ToString("R", inv) + "," + (peak * c.Settings.PdCalibrationPcPerV).ToString("R", inv) + "," + (a.Discharge.Clipped ? "clipped_invalid" : "candidate_not_confirmed_PD") + "," + source);
                        run = 0; peak = rawPeak = 0;
                    }
                }
            }
            string target = Path.Combine(pending, "location_0_20ms.csv");
            using (var writer = new StreamWriter(target, false, new UTF8Encoding(true), 1048576))
            {
                writer.WriteLine("time_ms,raw_ADC,input_V,equivalent_pC,source");
                double factor = c.PicoFullScaleV / c.MaximumAdc;
                for (int i = 0; i < window.Length; i++) writer.WriteLine((i * 1000.0 / c.PicoRateHz).ToString("R", inv) + "," + window[i].ToString(inv) + "," + (window[i] * factor).ToString("R", inv) + "," + (window[i] * factor * c.Settings.PdCalibrationPcPerV).ToString("R", inv) + "," + source);
            }
            // Only a fully closed set of CSVs receives the final directory name. Failed exports retain
            // an explicitly .pending directory, and closing the UI waits for this operation to finish.
            Directory.Move(pending, output);
        }
    }
}
