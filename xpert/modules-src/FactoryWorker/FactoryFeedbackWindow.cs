using System;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryFeedbackWindow
    {
        internal static void Apply(FactoryFeedback feedback, double[] raw, int count, int requested, int requiredChannel = -1)
        {
            if (feedback == null || raw == null || count < 1 || raw.Length != count * 3 || requested < 3 || requested > 40)
                throw new ArgumentException("AI窗口数据或长度无效。");
            // A short parser/legacy fixture is not promoted to a complete window.
            feedback.WindowSampleCount = 0;
            if (count < requested) return;
            var sorted = new double[requested];
            var median = new double[3];
            for (int channel = 0; channel < 3; channel++)
            {
                bool valid = true;
                for (int i = 0; i < requested; i++)
                {
                    double value = raw[(count - requested + i) * 3 + channel];
                    if (!FactorySettings.Finite(value))
                    {
                        if (requiredChannel < 0 || channel == requiredChannel)
                            throw new InvalidOperationException("选中电压窗口存在非有限样本，不允许滤掉后放行。");
                        valid = false;
                    }
                    sorted[i] = value;
                }
                if (!valid) { median[channel] = Double.NaN; continue; }
                Array.Sort(sorted);
                median[channel] = requested % 2 == 0 ? (sorted[requested / 2 - 1] + sorted[requested / 2]) * .5 : sorted[requested / 2];
            }
            feedback.ImonMedianRawV = median[0]; feedback.VmonMedianRawV = median[1]; feedback.DividerMedianRawV = median[2];
            feedback.WindowSampleCount = requested;
        }
        internal static double Divider(FactoryFeedback f) { return f.WindowSampleCount > 0 ? f.DividerMedianRawV : f.DividerRawV; }
        internal static double Vmon(FactoryFeedback f) { return f.WindowSampleCount > 0 ? f.VmonMedianRawV : f.VmonRawV; }
        internal static double Imon(FactoryFeedback f) { return f.WindowSampleCount > 0 ? f.ImonMedianRawV : f.ImonRawV; }
    }
}
