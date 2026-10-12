using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using HVX.PD.PulseToolkit;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public static class FactoryCalibrationLevels
    {
        // Same charge levels as 100 CalibrationTest, excluding its TDR distance mode.
        public static readonly string[] Labels = { "200 nC", "150 nC", "100 nC", "50 nC", "20 nC", "10 nC", "5 nC", "2 nC", "1 nC", "500 pC", "200 pC", "100 pC", "50 pC", "20 pC", "10 pC" };
        public static readonly double[] Pc = { 200000, 150000, 100000, 50000, 20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10 };
        public const int DefaultIndex = 11;
    }
    public sealed class FactoryTfThreshold
    {
        public double BaselineV, NoiseSigmaV, ThresholdPc, RawPeakPc;
        public string Method = "100数据浏览的全段抽样中位数/MAD + 3倍通用噪声阈值；最低2个ADC量化步，扣基线绝对幅值";
    }
    public sealed class FactoryTfPulse
    {
        public int StartSample, EndSample, PeakSample;
        public double TimeMs, PeakV, ChargePc, EquivalentTimeUs, EquivalentBandwidthMHz, SnrDb;
        public bool Clipped, Valid;
        public string Detail = "";
        [XmlIgnore] public double[] SamplesV = new double[0];
    }
    public sealed class FactoryTfResult
    {
        public string SourceId = "", SourceDirectory = "", ArtSerial = "", PicoSerial = "", ChainKey = "";
        public DateTime CapturedUtc;
        public bool Simulation, DriverOverflow;
        public double PicoRateHz, CaptureDurationMs, FullScaleV, CoefficientPcPerV, ThresholdPc, BaselineV, NoiseSigmaV;
        public int ResolutionBits, HardwareBits, CandidateCount, RejectedCount;
        public int PicoCoupling = 1;
        public bool HasSelectionWindow;
        public double SelectionStartMs,SelectionEndMs;
        public bool PicoBandwidth20MHz;
        // Missing in historical XML means the original unfiltered ADC method.
        public string PdFilterProtocol = "";
        public string PulseWindowProtocol = ""; // Empty: historical quiet-gap/PulseToolkit boundary.
        public string Method = "原始Pico ADC扣基线阈值；独立静隙脉冲开窗；PulseToolkit v2.1局部边界/v2.0能量矩TF；最多100个幅值最大的候选；不做定位/局放类型判定";
        public List<FactoryTfPulse> Pulses = new List<FactoryTfPulse>();
        [XmlIgnore] public string RawDataDirectory = "";
        public void Validate()
        {
            if (String.IsNullOrWhiteSpace(SourceId) || String.IsNullOrWhiteSpace(ChainKey) || String.IsNullOrWhiteSpace(ArtSerial) || String.IsNullOrWhiteSpace(PicoSerial) || CapturedUtc == DateTime.MinValue || CapturedUtc.Kind != DateTimeKind.Utc ||
                !FactorySettings.Positive(PicoRateHz) || !FactorySettings.Positive(CaptureDurationMs) || !FactorySettings.Positive(FullScaleV) ||
                !FactorySettings.Positive(CoefficientPcPerV) || !FactorySettings.Positive(ThresholdPc) || !FactorySettings.Finite(BaselineV) ||
                !FactorySettings.Finite(NoiseSigmaV) || NoiseSigmaV < 0 || Pulses == null || Pulses.Count > 100 || CandidateCount < Pulses.Count || RejectedCount < 0 || RejectedCount > CandidateCount ||
                PicoRateHz > 1e9 || CaptureDurationMs > 1000 || !new[] { 8, 12, 14, 15, 16 }.Contains(ResolutionBits) || HardwareBits < 6 || HardwareBits > ResolutionBits)
                throw new InvalidDataException("TF记录元数据无效，不加入累计。");
            int rangeIndex = Array.IndexOf(BenchSettings.Ranges, FullScaleV);
            if (rangeIndex < 0 || HardwareBits != PicoCaptureConfiguration.HardwareBits(ResolutionBits, rangeIndex) ||
                (PicoCoupling != 0 && PicoCoupling != 1) || PicoRateHz * CaptureDurationMs / 1000 > BenchSettings.MaximumPicoSamples ||
                CapturedUtc > DateTime.SpecifyKind(DateTime.MaxValue.AddHours(-8), DateTimeKind.Utc) ||
                (PdFilterProtocol!="" && PdFilterProtocol!=FactoryPdSignal.FilterId) ||
                (PulseWindowProtocol!="" && PulseWindowProtocol!=FactoryPulseEnvelopeWindow.Protocol) ||
                ChainKey != FactoryTfAnalysis.CreateChainKey(Simulation, ArtSerial, PicoSerial, PicoRateHz, ResolutionBits, PicoCoupling, PicoBandwidth20MHz,PdFilterProtocol))
                throw new InvalidDataException("TF来源身份、通道条件、位数或缓冲区长度不一致。");
            PicoCaptureConfiguration.TimebaseFor(ResolutionBits, PicoRateHz);
            if (HasSelectionWindow && (!FactorySettings.Finite(SelectionStartMs) || !FactorySettings.Finite(SelectionEndMs) ||
                SelectionStartMs < 0 || SelectionEndMs <= SelectionStartMs || SelectionEndMs > CaptureDurationMs))
                throw new InvalidDataException("TF保存选区必须为本轮时长内的有限递增边界。");
            foreach (var p in Pulses)
                if (p == null || p.StartSample < 0 || p.EndSample < p.StartSample || p.PeakSample < p.StartSample || p.PeakSample > p.EndSample ||
                    p.EndSample >= Math.Ceiling(PicoRateHz * CaptureDurationMs / 1000) || !FactorySettings.Finite(p.TimeMs) || p.TimeMs < 0 || p.TimeMs >= CaptureDurationMs ||
                    !FactorySettings.Finite(p.PeakV) || !FactorySettings.Finite(p.ChargePc) ||
                    (HasSelectionWindow && (p.StartSample < Math.Ceiling(SelectionStartMs * PicoRateHz / 1000 - 1e-9) ||
                        p.EndSample >= Math.Ceiling(SelectionEndMs * PicoRateHz / 1000 - 1e-9))) ||
                    (p.Valid && (p.Clipped || DriverOverflow || !FactorySettings.Positive(p.EquivalentTimeUs) || !FactorySettings.Positive(p.EquivalentBandwidthMHz) || !FactorySettings.Finite(p.SnrDb) || p.SnrDb < 10)))
                    throw new InvalidDataException("TF脉冲索引、单位或质量标志无效。");
        }
    }
    public static class FactoryTfAnalysis
    {
        public const int MaximumPulses = 100;
        public static FactoryTfThreshold Estimate(BenchCapture capture)
        {
            capture.ValidateCapture(); var x = FactoryPdSignal.Get(capture);
            int n = Math.Min(4096, x.Length); var probe = new double[n];
            double scale = capture.PicoFullScaleV / capture.MaximumAdc;
            for (int i = 0; i < n; i++) probe[i] = x[(int)((long)i * (x.Length - 1) / Math.Max(1, n - 1))];
            Array.Sort(probe); double baseline = Median(probe);
            for (int i = 0; i < n; i++) probe[i] = Math.Abs(probe[i] - baseline);
            Array.Sort(probe); double sigma = Math.Max(1.4826 * Median(probe), scale / Math.Sqrt(12));
            double peak = 0;
            foreach (double v in x) peak = Math.Max(peak, Math.Abs(v - baseline));
            double threshold = Math.Max(2 * scale, Math.Min(peak, Math.Max(5, Math.Sqrt(2 * Math.Log(Math.Max(2, x.Length)))) * sigma * 3));
            return new FactoryTfThreshold { BaselineV = baseline, NoiseSigmaV = sigma, ThresholdPc = threshold * capture.Settings.PdCalibrationPcPerV,
                RawPeakPc = peak * capture.Settings.PdCalibrationPcPerV, Method=FactoryPdSignal.FilterId+"；全段滤波后抽样中位数/MAD，最低2 ADC步阈值" };
        }
        private sealed class Candidate { internal int Start, End, Peak; internal double Amplitude; }
        public static FactoryTfResult Extract(FactoryResult round, double confirmedThresholdPc)
        { return ExtractCore(round, confirmedThresholdPc, false); }
        internal static FactoryTfResult ExtractSelected(FactoryResult round,double thresholdPc,double firstMs,double lastMs)
        {return ExtractCore(round,thresholdPc,false,firstMs,lastMs,true);}
        internal static FactoryTfResult ExtractForCalibrationLabels(FactoryResult round, double thresholdPc)
        { return ExtractCore(round, thresholdPc, true); }
        private static FactoryTfResult ExtractCore(FactoryResult round, double confirmedThresholdPc, bool calibrationLabels,double firstMs=0,double lastMs=Double.NaN,bool selected=false)
        {
            bool available=round!=null && (round.Completed && round.ResidualSafe || selected && round.CaptureOnlyAvailable());
            if (!available || round.Kind != (calibrationLabels ? FactoryRunKind.Calibration : FactoryRunKind.Pressure) || round.Capture == null)
                throw new InvalidOperationException("只分析已经完整结束并通过残压检查的加压采集；校准数据不混入试验统计。");
            var c = round.Capture; c.ValidateCapture();
            if(Double.IsNaN(lastMs))lastMs=c.PdAdc.Length*1000.0/c.PicoRateHz;
            if(!FactorySettings.Finite(firstMs) || !FactorySettings.Finite(lastMs) || firstMs<0 || lastMs<=firstMs || lastMs>c.PdAdc.Length*1000.0/c.PicoRateHz+1e-9)
                throw new ArgumentException("脉冲选区超出本轮原始数据");
            int first=(int)Math.Ceiling(firstMs*c.PicoRateHz/1000-1e-9),last=Math.Min(c.PdAdc.Length,(int)Math.Ceiling(lastMs*c.PicoRateHz/1000-1e-9));
            if (!FactorySettings.Positive(confirmedThresholdPc)) throw new ArgumentException("请确认正的脉冲阈值 / pC。");
            var estimate = Estimate(c); double scale = c.PicoFullScaleV / c.MaximumAdc;
            var signal=FactoryPdSignal.Get(c);
            if (calibrationLabels)
            {
                // A two-code alternating background can have zero MAD when
                // one quantized level has a slight majority. Conservatively
                // leave a 12-LSB label gate; do not alter the 100 calibration
                // detector, its coefficient, or the user-confirmed pressure gate.
                estimate.NoiseSigmaV = Math.Max(estimate.NoiseSigmaV, 2 * scale);
                confirmedThresholdPc = Math.Max(confirmedThresholdPc, 12 * scale * c.Settings.PdCalibrationPcPerV);
            }
            double threshold = confirmedThresholdPc / c.Settings.PdCalibrationPcPerV;
            double boundary = Math.Min(threshold, Math.Max(estimate.NoiseSigmaV * 3, threshold * .1));
            int quiet = Math.Max(4, (int)Math.Ceiling(c.PicoRateHz * 64e-9));
            int gap = Math.Max(quiet, (int)Math.Ceiling(c.PicoRateHz * .25e-6));
            int pulseLimit = Math.Min(60000,Math.Max(32,(int)Math.Ceiling(c.PicoRateHz*40e-6)));
            // A symmetric 10 kHz FIR has finite pre/post tails. Allow their
            // full support when finding/extracting an event. A filtered step
            // can legitimately appear as an edge candidate, not a PD diagnosis.
            int maxEvent = Math.Min(60000,pulseLimit+2*HVX.SignalProcessing.PdLinearHighPass.GuardSamples(c.PicoRateHz));
            int before = Math.Max(32, (int)Math.Ceiling(c.PicoRateHz * 2e-6));
            var result = new FactoryTfResult { SourceId = Path.GetFullPath(round.DirectoryPath), SourceDirectory = round.DirectoryPath,
                ArtSerial = c.ArtSerial, PicoSerial = c.PicoSerial, CapturedUtc = c.CapturedUtc, Simulation = c.Simulation, DriverOverflow = c.Overflow,
                PicoRateHz = c.PicoRateHz, CaptureDurationMs = c.Settings.CaptureDurationMs, FullScaleV = c.PicoFullScaleV,
                CoefficientPcPerV = c.Settings.PdCalibrationPcPerV, ResolutionBits = c.Settings.ResolutionBits, HardwareBits = c.HardwareBits,
                PicoCoupling = c.Settings.PicoCoupling, PicoBandwidth20MHz = c.Settings.PicoBandwidth20MHz,
                BaselineV = estimate.BaselineV, NoiseSigmaV = estimate.NoiseSigmaV, ThresholdPc = confirmedThresholdPc,
                ChainKey = ChainKey(c),PdFilterProtocol=FactoryPdSignal.FilterId,
                Method=FactoryPdSignal.FilterId+"；全段原采样率10 kHz线性相位高通后按选区提取、TF和标签；削顶仍检查原始ADC；最多100候选；不作位置/类型判定。",
                HasSelectionWindow=selected,SelectionStartMs=firstMs,SelectionEndMs=lastMs };
            var candidates = new List<Candidate>();
            bool envelopeMode=selected || calibrationLabels;
            if(envelopeMode)
            {
                result.PulseWindowProtocol=FactoryPulseEnvelopeWindow.Protocol;
                result.Method += " 开窗参考100+101校准信号过滤：主峰3%门槛、两侧连续16点静隙、每侧最多32760点；不完整包络拒绝。";
            }
            for (int i = first; i < last; i++)
            {
                double a = Math.Abs(signal[i] - estimate.BaselineV); if (a < threshold) continue;
                int start = i, peak = i, quietRun = 0; double largest = a;
                while (++i < last)
                {
                    a = Math.Abs(signal[i] - estimate.BaselineV);
                    if (a > largest) { largest = a; peak = i; }
                    quietRun = a < boundary ? quietRun + 1 : 0;
                    if (quietRun >= quiet && i - peak >= gap) break;
                }
                result.CandidateCount++;
                int end = Math.Min(last - 1, i - quietRun);
                // A drag-selected 3%/16-point envelope may be shorter than the
                // historical time-based quiet gap. In envelope mode its own
                // complete two-sided boundary check below is authoritative.
                if ((!envelopeMode && i >= last) || end - start > maxEvent) { result.RejectedCount++; continue; }
                var candidate = new Candidate { Start = start, End = Math.Max(peak, end), Peak = peak, Amplitude = largest };
                candidates.Add(candidate); candidates.Sort((left, right) => right.Amplitude.CompareTo(left.Amplitude));
                if (candidates.Count > MaximumPulses) candidates.RemoveAt(candidates.Count - 1);
            }
            var extractor = new PulseExtractionEngine(); var analyzer = new PulseAnalysisEngine();
            foreach (var candidate in candidates)
            {
                if(envelopeMode)
                {
                    int guard=HVX.SignalProcessing.PdLinearHighPass.GuardSamples(c.PicoRateHz);
                    var w=FactoryPulseEnvelopeWindow.Select(signal,estimate.BaselineV,first,last,candidate.Peak,candidate.Peak+1);
                    if(w==null || w.Start<guard || w.End>=signal.Length-guard) {result.RejectedCount++;continue;}
                    if(result.Pulses.Exists(p=>p.StartSample<=w.Peak && p.EndSample>=w.Peak)) {result.RejectedCount++;continue;}
                    var samples=new double[w.End-w.Start+1]; Array.Copy(signal,w.Start,samples,0,samples.Length);
                    var a=analyzer.Analyze(samples,c.PicoRateHz); bool rail=false;
                    for(int j=w.Start;j<=w.End;j++)rail |= Math.Abs((int)c.PdAdc[j])>=c.MaximumAdc;
                    double peak=signal[w.Peak]-estimate.BaselineV;
                    bool valid=a.Success && !rail && !c.Overflow && FactorySettings.Positive(a.EquivalentTimeNs) &&
                        FactorySettings.Positive(a.EquivalentBandwidthMHz) && FactorySettings.Finite(a.SnrDb) && a.SnrDb>=10;
                    result.Pulses.Add(new FactoryTfPulse {StartSample=w.Start,EndSample=w.End,PeakSample=w.Peak,
                        TimeMs=w.Peak*1000.0/c.PicoRateHz,PeakV=peak,ChargePc=peak*c.Settings.PdCalibrationPcPerV,
                        EquivalentTimeUs=a.EquivalentTimeNs/1000,EquivalentBandwidthMHz=a.EquivalentBandwidthMHz,SnrDb=a.SnrDb,
                        Clipped=rail,Valid=valid,SamplesV=samples,Detail=rail?"ADC削顶，不定量":c.Overflow?"驱动溢出，不计入有效TF":valid?"100+101包络开窗脉冲候选；非类型/位置判定":"包络已截取，TF质量不足"});
                    continue;
                }
                int lo = Math.Max(first, candidate.Start - before), hi = Math.Min(last - 1, candidate.End + before);
                if (hi - lo + 1 < 32 || candidate.Start == first || hi == last - 1) { result.RejectedCount++; continue; }
                var local = new double[hi - lo + 1];
                for (int i = 0; i < local.Length; i++) local[i] = signal[lo + i];
                int roiLo = Math.Max(0, candidate.Start - lo - 4), roiHi = Math.Min(local.Length - 1, candidate.End - lo + 4);
                var extracted = extractor.Extract(local, c.PicoRateHz, roiLo, roiHi);
                if (!extracted.Success || extracted.Pulse.Length>maxEvent) { result.RejectedCount++; continue; }
                var parameters = analyzer.Analyze(extracted.Pulse, c.PicoRateHz);
                bool clipped = false;
                for (int i = lo + extracted.StartIndex; i <= lo + extracted.EndIndex; i++) clipped |= Math.Abs((int)c.PdAdc[i]) >= c.MaximumAdc;
                var p = new FactoryTfPulse { StartSample = lo + extracted.StartIndex, EndSample = lo + extracted.EndIndex,
                    PeakSample = lo + extracted.PeakIndex, TimeMs = (lo + extracted.PeakIndex) * 1000.0 / c.PicoRateHz,
                    PeakV = extracted.PeakCorrectedValue, ChargePc = extracted.PeakCorrectedValue * c.Settings.PdCalibrationPcPerV,
                    EquivalentTimeUs = parameters.EquivalentTimeNs / 1000, EquivalentBandwidthMHz = parameters.EquivalentBandwidthMHz,
                    SnrDb = parameters.SnrDb, Clipped = clipped, SamplesV = extracted.Pulse,
                    Valid = parameters.Success && !clipped && !c.Overflow && FactorySettings.Positive(parameters.EquivalentTimeNs) &&
                        FactorySettings.Positive(parameters.EquivalentBandwidthMHz) && FactorySettings.Finite(parameters.SnrDb) && parameters.SnrDb >= 10 };
                p.Detail = clipped ? "ADC削顶：不定量、不计入TF有效点" : c.Overflow ? "驱动溢出：不计入TF有效点" : !parameters.Success ? parameters.Error : !p.Valid ? "脉冲信噪比/TF质量不足" : "独立脉冲候选；非局放类型或位置判定";
                result.Pulses.Add(p);
            }
            if (calibrationLabels) result.Method = "仅校准干扰模板匹配；不加入加压试验统计。" + result.Method;
            result.Pulses.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs)); result.Validate(); return result;
        }
        public static string ChainKey(BenchCapture c,string filter=FactoryPdSignal.FilterId)
        {
            return CreateChainKey(c.Simulation, c.ArtSerial, c.PicoSerial, c.PicoRateHz, c.Settings.ResolutionBits, c.Settings.PicoCoupling, c.Settings.PicoBandwidth20MHz,filter);
        }
        internal static string CreateChainKey(bool simulation, string artSerial, string picoSerial, double rate, int bits, int coupling, bool bandwidth,string filter="")
        { return (simulation ? "SIM" : "HW") + "|" + artSerial + "|" + picoSerial + "|" + rate.ToString("R", CultureInfo.InvariantCulture) + "|" + bits + "|" + coupling + "|" + bandwidth+(filter==""?"":"|"+filter); }
        private static double Median(double[] sorted)
        { return (sorted.Length & 1) == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) * .5; }
    }
    public sealed class FactoryTfSession
    {
        private readonly Dictionary<string, FactoryTfResult> rounds = new Dictionary<string, FactoryTfResult>(StringComparer.OrdinalIgnoreCase);
        public string ChainKey { get; private set; }
        public int RoundCount { get { return rounds.Count; } }
        public IList<FactoryTfResult> Rounds { get { return rounds.Values.OrderBy(r => r.CapturedUtc).ToList().AsReadOnly(); } }
        public void Add(FactoryTfResult result)
        {
            result.Validate();
            if (rounds.Count > 0 && result.ChainKey != ChainKey) throw new InvalidOperationException("设备、模式或Pico采集条件不同，未混入累计。请先保存并清空累计，或仅查看本轮。");
            if(rounds.Values.Any(r=>r.SourceId!=result.SourceId && r.PulseWindowProtocol!=result.PulseWindowProtocol))
                throw new InvalidOperationException("脉冲开窗协议不同，未混入累计；请先保存并清空累计。");
            if (rounds.Count >= 100 && !rounds.ContainsKey(result.SourceId)) throw new InvalidOperationException("累计已到100轮，请保存并清空后继续；历史没有被丢弃。");
            // Frozen feature-only snapshot, not a reference to a mutable UI result.
            using (var stream = new MemoryStream())
            {
                var xml = new XmlSerializer(typeof(FactoryTfResult)); xml.Serialize(stream, result); stream.Position = 0;
                rounds[result.SourceId] = (FactoryTfResult)xml.Deserialize(stream);
                rounds[result.SourceId].RawDataDirectory = result.RawDataDirectory;
            }
            ChainKey = result.ChainKey;
        }
        public void Clear() { rounds.Clear(); ChainKey = null; }
    }
}
