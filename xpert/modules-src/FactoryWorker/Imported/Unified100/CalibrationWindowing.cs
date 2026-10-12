// Source snapshot: D:/codex/HVXUnifiedDesktop/modules-src/CalibrationStandalone/CalibrationWindowing.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace Xiongxin.FactoryTest.Compatibility100
{
    // Timing uses samples at the actual acquisition rate, never display-decimated points.
    public sealed class CalibrationWindowSettings
    {
        public double SampleRateHz = 125000000;
        public double CableLengthM = 100;
        public double VelocityMPerUs = 160;
        public int PrePeakSamples = 400;
        public int TailSamples = 256;
        public int PeakSearchSamples = 128;
        public double FrequencyHz = 100;
        public double ThresholdV = .005;
        public double FullScaleV = .2;
        public bool CapacitiveLoad;
        // Capacitor calibration has no propagation distance. Capture an explicit
        // 40 us pulse envelope, not a fictitious cable length or wave velocity.
        public double CapacitorEnvelopeUs = 40;
        private double EnvelopeSamples { get { return (CapacitiveLoad ? CapacitorEnvelopeUs : 10 * CableLengthM / VelocityMPerUs) * SampleRateHz / 1000000; } }
        public int PostPeakSamples
        {
            get { Validate(); return checked((int)Math.Ceiling(EnvelopeSamples) + TailSamples); }
        }
        public int WindowSamples { get { return checked(PrePeakSamples + PostPeakSamples + 1); } }
        public void Validate()
        {
            if (!Positive(SampleRateHz) || (!CapacitiveLoad && (!Positive(CableLengthM) || CableLengthM>50000 || !Positive(VelocityMPerUs))) ||
                (CapacitiveLoad && (!Positive(CapacitorEnvelopeUs) || CapacitorEnvelopeUs>10000)) ||
                !Positive(FrequencyHz) || !Positive(ThresholdV) || !Positive(FullScaleV) || ThresholdV >= FullScaleV ||
                PrePeakSamples < 64 || TailSamples < 64 || PeakSearchSamples < 8 || PeakSearchSamples > PrePeakSamples / 2)
                throw new ArgumentException("校准窗口参数无效：线长须大于0且不超过50000 m，并填写有效波速、正阈值，峰前至少64点，尾部至少64点。");
            double count = PrePeakSamples + Math.Ceiling(EnvelopeSamples) + TailSamples + 1;
            if (count > 2000000 || count >= SampleRateHz / FrequencyHz * .8)
                throw new ArgumentException("校准窗口过长：不能覆盖相邻校准脉冲，且单窗口最多200万点。请核对长度、波速或源频率。");
        }
        private static bool Positive(double x) { return x > 0 && !Double.IsInfinity(x) && !Double.IsNaN(x); }
    }

    public sealed class CalibrationWindowResult
    {
        public double[][] Windows;
        public double[] Average;
        // Factory provenance: measured incident positions from the same
        // accepted raw segment, never reconstructed from the configured Hz.
        public int[] PeakSampleIndices;
        public int CandidateCount;
        public int RejectedCount;
        public string Message;
    }

    public static class CalibrationWindowing
    {
        public static CalibrationWindowResult FromFilteredSegment(double[] samples,CalibrationWindowSettings s,int guardSamples, Func<int,bool> rejectTemplate = null)
        {
            s.Validate();var peaks=new List<int>();
            for(int i=guardSamples;i<samples.Length-guardSamples;i++) {
                if(Math.Abs(samples[i])<s.ThresholdV)continue;
                int peak=i;for(int j=i+1;j<Math.Min(samples.Length-guardSamples,i+s.PeakSearchSamples);j++)if(Math.Abs(samples[j])>Math.Abs(samples[peak]))peak=j;
                peaks.Add(peak);i=Math.Min(samples.Length-guardSamples-1,peak+s.PostPeakSamples);
                if(peaks.Count>4096)throw new InvalidOperationException("段模式候选过多，请检查干扰与触发阈值。");
            }
            if(rejectTemplate!=null) peaks=peaks.Where(p=>!rejectTemplate(p)).ToList();
            double period=s.SampleRateHz/s.FrequencyHz,tolerance=Math.Max(s.PeakSearchSamples,period*.002);
            var best=new List<int>();bool ambiguous=false;
            foreach(int seed in peaks) {
                var group=new List<int>();foreach(int p in peaks)if(Math.Abs((p-seed)/period-Math.Round((p-seed)/period))*period<=tolerance)group.Add(p);
                if(group.Count>best.Count){best=group;ambiguous=false;}else if(group.Count==best.Count&&group.Count>0&&!best.Contains(seed))ambiguous=true;
            }
            if(best.Count<3||ambiguous)throw new InvalidOperationException("段模式未找到唯一且至少三次的校准周期。请核对源频率、阈值或干扰模板。");
            int expected=(int)Math.Round((best[best.Count-1]-best[0])/period)+1;
            if(best.Count<expected*.8)throw new InvalidOperationException("脉冲周期缺失超过20%，不将低频干扰当作校准源。");
            var accepted=new List<double[]>();var acceptedPeaks=new List<int>();int rejected=peaks.Count-best.Count;
            foreach(int peak in best) {
                int first=peak-s.PrePeakSamples;
                if(first<guardSamples||first+s.WindowSamples>samples.Length-guardSamples){rejected++;continue;}
                var wave=new double[s.WindowSamples];Array.Copy(samples,first,wave,0,wave.Length);accepted.Add(wave);acceptedPeaks.Add(peak);
            }
            var result=Finish(accepted,peaks.Count,rejected,s);result.PeakSampleIndices=acceptedPeaks.ToArray();return result;
        }
        // Full raw blocks have already passed the shared linear-phase filter.
        // Retain double precision and reject filter-boundary windows (no padding).
        public static CalibrationWindowResult FromFilteredTriggeredBlocks(double[][] blocks, CalibrationWindowSettings s, int guardSamples)
        {
            s.Validate(); var accepted=new List<double[]>(); int rejected=0;
            foreach(var block in blocks) {
                var peaks=new List<int>();
                for(int i=guardSamples;i<block.Length-guardSamples;i++) {
                    if(Math.Abs(block[i])<s.ThresholdV)continue;
                    int peak=i;
                    for(int j=i+1;j<Math.Min(block.Length-guardSamples,i+s.PeakSearchSamples);j++)if(Math.Abs(block[j])>Math.Abs(block[peak]))peak=j;
                    peaks.Add(peak);i=Math.Min(block.Length-guardSamples-1,peak+s.PostPeakSamples);
                    if(peaks.Count>4096)throw new InvalidOperationException("候选脉冲过多，请检查触发源与干扰。");
                }
                if(peaks.Count!=1){rejected++;continue;}
                int start=peaks[0]-s.PrePeakSamples;
                if(start<guardSamples || start+s.WindowSamples>block.Length-guardSamples){rejected++;continue;}
                var wave=new double[s.WindowSamples];Array.Copy(block,start,wave,0,wave.Length);
                accepted.Add(wave);
            }
            return Finish(accepted,blocks.Length,rejected,s);
        }

        // Segment mode: fit a repeated phase at the configured frequency. A competing
        // equally-supported phase is ambiguous and must not silently become calibration.
        public static CalibrationWindowResult FromSegment(short[] raw, CalibrationWindowSettings s, Func<bool> cancelled)
        {
            s.Validate();
            var peaks = Find(raw, s, cancelled);
            double period = s.SampleRateHz / s.FrequencyHz;
            double tolerance = Math.Max(s.PeakSearchSamples, period * .002);
            List<int> best = new List<int>(); int competing = 0;
            foreach (int seed in peaks)
            {
                CheckCancel(cancelled);
                var group = new List<int>();
                foreach (int p in peaks)
                {
                    double cycles = (p - seed) / period;
                    if (Math.Abs(cycles - Math.Round(cycles)) * period <= tolerance) group.Add(p);
                }
                if (group.Count > best.Count) { best = group; competing = 0; }
                else if (group.Count == best.Count && group.Count > 0 && !best.Contains(seed)) competing = group.Count;
            }
            if (best.Count < 3 || competing == best.Count)
                throw new InvalidOperationException("未获得唯一且至少3次的校准周期序列；可能脉冲不足或存在同频干扰，请延长段长、检查源频率并人工确认。");
            // Missing most periods is not a 100/400 Hz source (e.g. 25/50 Hz interference).
            int expected = (int)Math.Round((best[best.Count - 1] - best[0]) / period) + 1;
            if (best.Count < expected * .8)
                throw new InvalidOperationException("校准脉冲周期缺失超过20%，不能把25/50 Hz干扰视为校准源。");
            return Extract(raw, best, peaks.Count, s, cancelled);
        }

        // One independently triggered block per incident pulse. The caller owns hardware
        // pretrigger/timeout settings; incomplete windows are rejected, never zero padded.
        public static CalibrationWindowResult FromTriggeredBlocks(short[][] blocks, CalibrationWindowSettings s, Func<bool> cancelled)
        {
            s.Validate(); var accepted = new List<double[]>(); int rejected = 0;
            foreach (short[] block in blocks)
            {
                var candidates = Find(block, s, cancelled);
                if (candidates.Count != 1) { rejected++; continue; }
                var one = Extract(block, candidates, 1, s, cancelled);
                if (one.Windows.Length == 1) accepted.Add(one.Windows[0]); else rejected++;
            }
            return Finish(accepted, blocks.Length, rejected, s);
        }
        private static List<int> Find(short[] raw, CalibrationWindowSettings s, Func<bool> cancelled)
        {
            if (raw == null) throw new ArgumentNullException("raw");
            var peaks = new List<int>();
            for (int i = 0; i < raw.Length; i++)
            {
                if ((i & 32767) == 0) CheckCancel(cancelled);
                if (Math.Abs((double)raw[i]) * s.FullScaleV / 32767 < s.ThresholdV) continue;
                int peak = i, end = Math.Min(raw.Length, i + s.PeakSearchSamples);
                for (int j = i + 1; j < end; j++) if (Math.Abs((int)raw[j]) > Math.Abs((int)raw[peak])) peak = j;
                peaks.Add(peak);
                if (peaks.Count > 4096) throw new InvalidOperationException("候选脉冲过多，请提高阈值或检查噪声。");
                i = Math.Min(raw.Length - 1, peak + s.PostPeakSamples);
            }
            return peaks;
        }
        private static CalibrationWindowResult Extract(short[] raw, List<int> peaks, int candidates, CalibrationWindowSettings s, Func<bool> cancelled)
        {
            var windows = new List<double[]>(); int rejected = candidates - peaks.Count;
            foreach (int peak in peaks)
            {
                CheckCancel(cancelled);
                int start = peak - s.PrePeakSamples;
                if (start < 0 || start + s.WindowSamples > raw.Length) { rejected++; continue; }
                var w = new double[s.WindowSamples]; bool clipped = false;
                double baseline = 0;
                for (int j = 0; j < s.PrePeakSamples / 2; j++) baseline += raw[start + j] * s.FullScaleV / 32767;
                baseline /= s.PrePeakSamples / 2;
                for (int j = 0; j < w.Length; j++)
                {
                    clipped |= Math.Abs((int)raw[start + j]) >= 32760;
                    w[j] = raw[start + j] * s.FullScaleV / 32767 - baseline;
                }
                if (clipped) rejected++; else windows.Add(w);
            }
            return Finish(windows, candidates, rejected, s);
        }
        private static CalibrationWindowResult Finish(List<double[]> windows, int candidates, int rejected, CalibrationWindowSettings s)
        {
            double[] average = new double[s.WindowSamples];
            foreach (double[] w in windows) for (int i = 0; i < w.Length; i++) average[i] += w[i] / windows.Count;
            return new CalibrationWindowResult { Windows = windows.ToArray(), Average = average, CandidateCount = candidates,
                RejectedCount = rejected, Message = "候选 " + candidates + "；有效 " + windows.Count + "；剔除 " + rejected + "（非周期、边界或削顶）。同频同形干扰仍需人工确认。" };
        }
        private static void CheckCancel(Func<bool> cancelled) { if (cancelled != null && cancelled()) throw new OperationCanceledException(); }
    }
}
