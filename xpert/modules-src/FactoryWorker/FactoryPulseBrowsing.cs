using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Selection uses capture identity and native ADC index, never a TF coordinate
    // or mutable object reference. Coincident TF points remain separate pulses.
    internal sealed class FactoryPulseSelection
    {
        private List<FactoryTfDisplayPoint> points = new List<FactoryTfDisplayPoint>();
        internal int Index { get; private set; } = -1;
        internal int Count { get { return points.Count; } }
        internal FactoryTfDisplayPoint Current { get { return Index < 0 ? null : points[Index]; } }
        internal void SetPoints(List<FactoryTfDisplayPoint> source)
        {
            var previous = Current; points = new List<FactoryTfDisplayPoint>(source);
            Index = points.FindIndex(p => Same(p, previous));
            if (Index < 0 && points.Count > 0) Index = 0;
        }
        internal bool Select(FactoryTfDisplayPoint point)
        { int i = points.FindIndex(p => Same(p, point)); if (i < 0) return false; Index = i; return true; }
        internal bool Move(int direction)
        {
            int next = Index + direction;
            if (Index < 0 || next < 0 || next >= points.Count) return false;
            Index = next; return true;
        }
        internal static bool Same(FactoryTfDisplayPoint a, FactoryTfDisplayPoint b)
        {
            return a != null && b != null && a.Round.CapturedUtc == b.Round.CapturedUtc &&
                String.Equals(a.Round.SourceId, b.Round.SourceId, StringComparison.OrdinalIgnoreCase) && a.Pulse.PeakSample == b.Pulse.PeakSample;
        }
    }
    internal sealed class FactoryPulseRawCache
    { internal string SourceId; internal BenchCapture Capture; }
    internal static class FactoryTfPulseReader
    {
        internal static bool Matches(FactoryTfResult r, BenchCapture c)
        {
            return c != null && c.Settings != null && r.CapturedUtc == c.CapturedUtc && r.Simulation == c.Simulation &&
                r.ArtSerial == c.ArtSerial && r.PicoSerial == c.PicoSerial && r.ChainKey == FactoryTfAnalysis.ChainKey(c,r.PdFilterProtocol) &&
                r.FullScaleV == c.PicoFullScaleV && r.CoefficientPcPerV == c.Settings.PdCalibrationPcPerV &&
                r.CaptureDurationMs == c.Settings.CaptureDurationMs && r.HardwareBits == c.HardwareBits && r.DriverOverflow == c.Overflow;
        }
        internal static double[] Read(FactoryTfDisplayPoint point, FactoryResult current, ref FactoryPulseRawCache cached)
        {
            if (point == null) throw new ArgumentNullException("point");
            var r = point.Round; var p = point.Pulse; r.Validate();
            if (!r.Pulses.Contains(p)) throw new InvalidDataException("脉冲不属于当前TF记录。");
            int count = p.EndSample - p.StartSample + 1;
            if (count > 65536 || Math.Abs(p.TimeMs - p.PeakSample * 1000.0 / r.PicoRateHz) > .501 * 1000 / r.PicoRateHz)
                throw new InvalidDataException("原始脉冲窗口或峰时索引不一致。");
            if (p.SamplesV != null && p.SamplesV.Length > 0)
            {
                if (p.SamplesV.Length != count || p.SamplesV.Any(x => !FactorySettings.Finite(x)))
                    throw new InvalidDataException("原始脉冲长度或数值不完整。");
                return p.SamplesV;
            }
            BenchCapture capture = current != null && String.Equals(r.SourceId, Path.GetFullPath(current.DirectoryPath), StringComparison.OrdinalIgnoreCase) && Matches(r, current.Capture) ? current.Capture :
                cached != null && String.Equals(r.SourceId, cached.SourceId, StringComparison.OrdinalIgnoreCase) && Matches(r, cached.Capture) ? cached.Capture : null;
            if (capture == null)
            {
                string failure = "历史TF只有特征；对应原始数据缺失。";
                foreach (string folder in new[] { r.RawDataDirectory, r.SourceDirectory }.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(Path.Combine(folder, "capture.bin")) || !File.Exists(Path.Combine(folder, "settings.xml"))) continue;
                    try
                    {
                        var loaded = BenchCapture.Load(folder);
                        if (!Matches(r, loaded)) { failure = "原始目录的采集身份、时间或参数与TF不一致，不显示其他轮次的脉冲。"; continue; }
                        capture = loaded; cached = new FactoryPulseRawCache { SourceId = r.SourceId, Capture = loaded }; break;
                    }
                    catch (Exception ex) { failure = "无法读取对应原始数据：" + ex.Message; }
                }
                if (capture == null) throw new InvalidDataException(failure);
            }
            capture.ValidateCapture();
            if (p.EndSample >= capture.PdAdc.Length) throw new InvalidDataException("原始数据缺少当前脉冲窗口。");
            var samples = new double[count]; double scale = capture.PicoFullScaleV / capture.MaximumAdc;
            var filtered=r.PdFilterProtocol==FactoryPdSignal.FilterId?FactoryPdSignal.Get(capture):null;
            for (int i = 0; i < count; i++) samples[i] = filtered==null?capture.PdAdc[p.StartSample + i] * scale:filtered[p.StartSample+i];
            return samples;
        }
    }
}
