using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryPulseRecognitionRow
    { public int PeakSample; public string Label = "", Detail = ""; public bool Matched, Conflict, ExcludedFromDerived; }
    public sealed class FactoryPulseFilterResult
    {
        public string Method = "manual-template-strict-1pct-v1; TF relative / normalized native waveform + Hilbert envelope pointwise max error <=1%";
        public string Boundary = "仅原始窗口的派生检波过滤；原始ADC、电压、量程保护及原始最大值不改写。类别是人工标签相似匹配，不是缺陷诊断。";
        public int FilteredCount;
        public double[] PdPc = new double[0];
        public List<FactoryPulseRecognitionRow> Pulses = new List<FactoryPulseRecognitionRow>();
    }
    internal static class FactoryPulseFilter
    {
        internal static FactoryPulseFilterResult Build(BenchCapture capture, FactoryTfResult tf, Dictionary<string, FactoryPulseMatch> matches, DetectedWaveformResult detected = null)
        {
            var result = new FactoryPulseFilterResult(); var original = detected ?? FactoryDetectedWaveforms.Build(capture);
            result.PdPc = (double[])original.PdEnvelopePc.Clone();
            var filteredPulses = new List<FactoryTfPulse>();
            foreach (var p in tf.Pulses)
            {
                FactoryPulseMatch match; if (!matches.TryGetValue(FactoryTfAnnotationDocument.PointKey(new FactoryTfDisplayPoint { Round = tf, Pulse = p }), out match)) match = new FactoryPulseMatch();
                bool exclude = p.Valid && !capture.Overflow && match.Matched && !match.Conflict && match.Filter &&
                    !tf.Pulses.Any(other => other != p && other.StartSample <= p.EndSample && other.EndSample >= p.StartSample);
                if (exclude) filteredPulses.Add(p);
                result.Pulses.Add(new FactoryPulseRecognitionRow { PeakSample = p.PeakSample, Matched = match.Matched, Conflict = match.Conflict,
                    ExcludedFromDerived = exclude, Label = match.Label == null ? "未识别" : match.Label.Name, Detail = match.Text });
            }
            result.FilteredCount = filteredPulses.Count; if (result.FilteredCount == 0) return result;
            var mask = new bool[capture.PdAdc.Length];
            foreach (var p in filteredPulses) for (int i = p.StartSample; i <= p.EndSample; i++) mask[i] = true;
            // Same 100 full-segment high-pass BEFORE exclusion/binning. No
            // zeroing raw samples, no smoothing across the excluded window.
            double scale = capture.PicoFullScaleV / capture.MaximumAdc;
            var volts = capture.PdAdc.Select(v => v * scale).ToArray(); var highPassed = PdLinearHighPass.Apply(volts, capture.PicoRateHz);
            int begin = 0;
            for (int bin = 0; bin < result.PdPc.Length; bin++)
            {
                double boundary = (bin + 1) * capture.PicoRateHz / original.OutputRateHz;
                double nearest = Math.Round(boundary); if (Math.Abs(boundary - nearest) < 1e-8) boundary = nearest;
                int end = Math.Min(highPassed.Length, (int)Math.Ceiling(boundary)); double peak = 0;
                for (int i = begin; i < end; i++) if (!mask[i]) peak = Math.Max(peak, Math.Abs(highPassed[i]));
                result.PdPc[bin] = peak * capture.Settings.PdCalibrationPcPerV; begin = end;
            }
            return result;
        }
    }
}
