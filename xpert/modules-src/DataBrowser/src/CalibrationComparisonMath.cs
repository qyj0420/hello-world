using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace HVX.Controls
{
    // Extents intentionally do not retain the source arrays: comparison can read one
    // full waveform at a time in a validation pass and then in an averaging pass.
    internal sealed class CalibrationComparisonWaveExtent
    {
        internal double StartSeconds { get; private set; }
        internal double EndSeconds { get; private set; }
        internal double MinimumIntervalSeconds { get; private set; }
        internal int SampleCount { get; private set; }

        internal CalibrationComparisonWaveExtent(double start, double end, double interval, int count)
        {
            StartSeconds = start; EndSeconds = end;
            MinimumIntervalSeconds = interval; SampleCount = count;
        }
    }

    internal sealed class CalibrationComparisonAveragePlan
    {
        readonly double[] times;
        internal double[] XSeconds { get { return (double[])times.Clone(); } }
        internal int SampleCount { get { return times.Length; } }
        internal int TraceCount { get; private set; }
        internal double StartSeconds { get { return times[0]; } }
        internal double EndSeconds { get { return times[times.Length - 1]; } }
        internal double IntervalSeconds { get; private set; }

        internal CalibrationComparisonAveragePlan(double[] axis, int traces, double interval)
        {
            times = axis; TraceCount = traces; IntervalSeconds = interval;
        }

        internal double TimeAt(int index) { return times[index]; }
    }

    internal sealed class CalibrationComparisonAverageResult
    {
        internal double[] XSeconds { get; private set; }
        internal double[] Y { get; private set; }
        internal int TraceCount { get; private set; }
        internal CalibrationComparisonAverageResult(double[] x, double[] y, int count)
        {
            XSeconds = x; Y = y; TraceCount = count;
        }
    }

    internal static class CalibrationComparisonMath
    {
        internal static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        internal static CalibrationComparisonWaveExtent Inspect(double[] xSeconds, double[] y,
            double originSeconds, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (xSeconds == null || y == null || xSeconds.Length != y.Length || xSeconds.Length < 2)
                throw new InvalidDataException("平均要求每条波形至少有两个对应的有效采样点。");
            if (!Finite(originSeconds)) throw new InvalidDataException("波形对齐时间无效。");
            double first = xSeconds[0] - originSeconds, previous = first, minimum = Double.PositiveInfinity;
            if (!Finite(first)) throw new InvalidDataException("波形时间坐标无效。");
            for (int i = 0; i < xSeconds.Length; ++i)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double time = xSeconds[i] - originSeconds;
                if (!Finite(xSeconds[i]) || !Finite(time) || !Finite(y[i]))
                    throw new InvalidDataException("波形包含非有限的时间或幅值。");
                if (i > 0)
                {
                    double interval = time - previous;
                    if (!Finite(interval) || interval <= 0 || xSeconds[i] <= xSeconds[i - 1])
                        throw new InvalidDataException("波形时间必须严格递增，且对齐后不能丢失采样精度。");
                    minimum = Math.Min(minimum, interval);
                }
                previous = time;
            }
            cancellation.ThrowIfCancellationRequested();
            return new CalibrationComparisonWaveExtent(first, previous, minimum, xSeconds.Length);
        }

        internal static CalibrationComparisonAveragePlan PlanAverage(
            IList<CalibrationComparisonWaveExtent> waves, int maxSamples, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (waves == null || waves.Count == 0) throw new InvalidDataException("没有可以进行平均的校准波形。");
            if (maxSamples < 2) throw new ArgumentOutOfRangeException("maxSamples");
            double start = Double.NegativeInfinity, end = Double.PositiveInfinity, finest = Double.PositiveInfinity;
            foreach (var wave in waves)
            {
                cancellation.ThrowIfCancellationRequested();
                if (wave == null || !Finite(wave.StartSeconds) || !Finite(wave.EndSeconds) ||
                    !Finite(wave.MinimumIntervalSeconds) || wave.MinimumIntervalSeconds <= 0 ||
                    wave.EndSeconds <= wave.StartSeconds || wave.SampleCount < 2)
                    throw new InvalidDataException("参与平均的波形范围无效。");
                start = Math.Max(start, wave.StartSeconds); end = Math.Min(end, wave.EndSeconds);
                finest = Math.Min(finest, wave.MinimumIntervalSeconds);
            }
            double span = end - start;
            if (!Finite(span) || span <= 0)
                throw new InvalidDataException("所选波形没有共同的时间范围，无法平均；可检查入射游标对齐设置。");

            // Use a grid at least as fine as the finest source interval. Include both
            // common endpoints, with no extrapolation or synthetic zero padding.
            double intervals = span / finest;
            if (!Finite(intervals)) throw TooManySamples(maxSamples);
            // A tiny representation error must not turn an integer grid into one extra
            // interval: regular single-trace inputs should keep their native samples.
            double nearest = Math.Round(intervals);
            if (Math.Abs(intervals - nearest) <= Math.Max(1.0, intervals) * 1e-10)
                intervals = nearest;
            double required = Math.Ceiling(intervals) + 1;
            if (!Finite(required) || required > maxSamples || required > Int32.MaxValue)
                throw TooManySamples(maxSamples);
            int count = Math.Max(2, (int)required);
            double step = span / (count - 1);
            if (!Finite(step) || step <= 0) throw new InvalidDataException("共同时间轴的采样间隔无效。");
            var x = new double[count];
            for (int i = 0; i < count; ++i)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                x[i] = i == count - 1 ? end : start + step * i;
                if (!Finite(x[i]) || (i > 0 && x[i] <= x[i - 1]))
                    throw new InvalidDataException("共同时间轴无法在当前数值精度下保持递增。");
            }
            cancellation.ThrowIfCancellationRequested();
            return new CalibrationComparisonAveragePlan(x, waves.Count, step);
        }

        static InvalidDataException TooManySamples(int maxSamples)
        {
            return new InvalidDataException("共同时间轴超过 " + maxSamples +
                " 点的平均计算上限；请减少所选记录或检查采样率。不会以降采样数据代替原始波形求平均。");
        }
    }

    internal sealed class CalibrationComparisonAverageAccumulator
    {
        readonly CalibrationComparisonAveragePlan plan;
        double[] average;
        int count;
        internal int TraceCount { get { return count; } }

        internal CalibrationComparisonAverageAccumulator(CalibrationComparisonAveragePlan plan)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            this.plan = plan; average = new double[plan.SampleCount];
        }

        // Apply display normalization independently of time alignment. baseline=0,
        // scale=1 preserves physical amplitude; otherwise callers use each full
        // waveform's baseline and absolute peak, not the reduced display preview.
        internal void Add(double[] xSeconds, double[] y, double originSeconds,
            double baseline, double scale, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count >= plan.TraceCount) throw new InvalidOperationException("平均波形数超过已验证的记录数。");
            if (!CalibrationComparisonMath.Finite(baseline) || !CalibrationComparisonMath.Finite(scale) || scale <= 0)
                throw new InvalidDataException("平均使用的基线或归一化峰值无效。");
            var extent = CalibrationComparisonMath.Inspect(xSeconds, y, originSeconds, cancellation);
            if (extent.StartSeconds > plan.StartSeconds || extent.EndSeconds < plan.EndSeconds)
                throw new InvalidDataException("波形不再覆盖已验证的共同时间范围，请重新加载后平均。");
            var candidate = new double[average.Length];
            int left = 0, nextCount = count + 1;
            double oldWeight = (double)count / nextCount;
            for (int i = 0; i < candidate.Length; ++i)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                double time = plan.TimeAt(i);
                while (left + 1 < xSeconds.Length - 1 && xSeconds[left + 1] - originSeconds < time)
                {
                    if ((left & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    ++left;
                }
                double t0 = xSeconds[left] - originSeconds, t1 = xSeconds[left + 1] - originSeconds;
                double fraction = (time - t0) / (t1 - t0);
                if (!CalibrationComparisonMath.Finite(fraction) || fraction < 0 || fraction > 1)
                    throw new InvalidDataException("共同时间轴超出当前波形，不能使用外推或补零进行平均。");
                // Weighted interpolation avoids overflow from subtracting opposite
                // large finite amplitudes. Normalize before averaging, equally weighted.
                double value = y[left] * (1 - fraction) + y[left + 1] * fraction;
                value = (value - baseline) / scale;
                double mean = count == 0 ? value : average[i] * oldWeight + value / nextCount;
                if (!CalibrationComparisonMath.Finite(value) || !CalibrationComparisonMath.Finite(mean))
                    throw new InvalidDataException("归一化或平均幅值超出有效数值范围。");
                candidate[i] = mean;
            }
            cancellation.ThrowIfCancellationRequested();
            // Invalid/cancelled additions never leave a partially updated mean.
            average = candidate; count = nextCount;
        }

        internal CalibrationComparisonAverageResult Complete(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count != plan.TraceCount)
                throw new InvalidOperationException("部分已选择波形尚未加入平均，不能显示不完整的平均结果。");
            var result = new CalibrationComparisonAverageResult(plan.XSeconds, (double[])average.Clone(), count);
            cancellation.ThrowIfCancellationRequested(); return result;
        }
    }
}
