using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HVX.Controls;

internal static class CalibrationComparisonAverageSmoke
{
    static int passed;
    static readonly CancellationToken None = CancellationToken.None;
    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message); ++passed;
    }
    static void Close(double actual, double expected, string message, double tolerance = 1e-10)
    {
        Check(Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected)),
            message + ": actual=" + actual.ToString("R") + " expected=" + expected.ToString("R"));
    }
    static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { ++passed; return; }
        throw new Exception(message + ": expected " + typeof(T).Name);
    }
    static double[] Axis(int count, double step, double offset = 0)
    {
        return Enumerable.Range(0, count).Select(i => offset + i * step).ToArray();
    }
    static CalibrationComparisonAveragePlan Plan(double[][] x, double[][] y, double[] origins = null, int limit = 4000000)
    {
        var extents = new List<CalibrationComparisonWaveExtent>();
        for (int i = 0; i < x.Length; ++i)
            extents.Add(CalibrationComparisonMath.Inspect(x[i], y[i], origins == null ? 0 : origins[i], None));
        return CalibrationComparisonMath.PlanAverage(extents, limit, None);
    }
    static CalibrationComparisonAverageResult Average(double[][] x, double[][] y, double[] origins = null,
        double[] baselines = null, double[] scales = null)
    {
        var plan = Plan(x, y, origins); var mean = new CalibrationComparisonAverageAccumulator(plan);
        for (int i = 0; i < x.Length; ++i)
            mean.Add(x[i], y[i], origins == null ? 0 : origins[i], baselines == null ? 0 : baselines[i],
                scales == null ? 1 : scales[i], None);
        return mean.Complete(None);
    }
    static int Main()
    {
        try
        {
            var x = Axis(11, .1);
            var a = x.Select(t => 2 * t + 4).ToArray();
            var b = x.Select(t => 6 * t - 2).ToArray();
            var c = x.Select(t => -2 * t + 7).ToArray();
            var originalX = (double[])x.Clone(); var originalA = (double[])a.Clone();
            var two = Average(new[] { x, x }, new[] { a, b });
            Check(two.TraceCount == 2 && two.XSeconds.Length == 11, "two-trace native grid");
            for (int i = 0; i < two.Y.Length; ++i) Close(two.Y[i], 4 * two.XSeconds[i] + 1, "two exact linear mean");
            var three = Average(new[] { x, x, x }, new[] { a, b, c });
            for (int i = 0; i < three.Y.Length; ++i) Close(three.Y[i], 2 * three.XSeconds[i] + 3, "three equal weights");
            Check(x.SequenceEqual(originalX) && a.SequenceEqual(originalA), "source arrays remain unchanged");

            var coarse = Axis(6, .2); var fine = Axis(21, .05);
            var mixed = Average(new[] { coarse, fine }, new[] { coarse.Select(t => t).ToArray(), fine.Select(t => 3 * t).ToArray() });
            Check(mixed.XSeconds.Length == 21, "uses finest native interval, not coarsest");
            for (int i = 0; i < mixed.Y.Length; ++i) Close(mixed.Y[i], 2 * mixed.XSeconds[i], "different sample rates interpolate before mean");

            var pulse = new double[fine.Length]; pulse[9] = 12;
            var narrow = Average(new[] { coarse, fine }, new[] { new double[coarse.Length], pulse });
            Close(narrow.Y[9], 6, "finest-rate one-sample pulse is not discarded");
            Check(narrow.Y.Count(v => v > 1e-10) == 1, "narrow pulse shape preserved on native grid");

            var negative = Axis(13, .1, -.4); var shorter = Axis(5, .15, -.1);
            var overlap = Average(new[] { negative, shorter }, new[] { negative.Select(t => 2 * t).ToArray(), shorter.Select(t => 4 * t).ToArray() });
            Close(overlap.XSeconds[0], -.1, "common overlap start supports negative times");
            Close(overlap.XSeconds.Last(), .5, "common overlap end only, no zero padding");
            for (int i = 0; i < overlap.Y.Length; ++i) Close(overlap.Y[i], 3 * overlap.XSeconds[i], "overlap mean");

            var irregular = new[] { 0.0, .1, .35, .7, 1.0 };
            var nonuniform = Average(new[] { irregular, x }, new[] { irregular.Select(t => 5 * t).ToArray(), x.Select(t => -t).ToArray() });
            for (int i = 0; i < nonuniform.Y.Length; ++i) Close(nonuniform.Y[i], 2 * nonuniform.XSeconds[i], "nonuniform valid coordinates interpolate");

            var shifted = Axis(11, .1, 5);
            var align = Average(new[] { x, shifted }, new[] { a, a }, new[] { .2, 5.2 });
            Close(align.XSeconds[0], -.2, "alignment subtracts each incident separately");
            for (int i = 0; i < align.Y.Length; ++i) Close(align.Y[i], 2 * (align.XSeconds[i] + .2) + 4, "aligned mean");
            Throws<InvalidDataException>(() => Average(new[] { x, shifted }, new[] { a, a }), "unaligned disjoint traces must not fabricated-align");
            var touch = Axis(11, .1, 1);
            Throws<InvalidDataException>(() => Average(new[] { x, touch }, new[] { a, a }), "one shared endpoint has no shared interval");

            var small = new[] { 0.0, 1, 2, 3, 4 };
            var shape = new[] { 0.0, .25, 1, -.5, 0 };
            var y1 = shape.Select(v => 3 + 4 * v).ToArray(); var y2 = shape.Select(v => -5 + 10 * v).ToArray();
            var normalized = Average(new[] { small, small }, new[] { y1, y2 }, null, new[] { 3.0, -5 }, new[] { 4.0, 10 });
            for (int i = 0; i < shape.Length; ++i) Close(normalized.Y[i], shape[i], "per-source baseline and absolute peak normalization");
            var raw = Average(new[] { small, small }, new[] { y1, y2 });
            for (int i = 0; i < shape.Length; ++i) Close(raw.Y[i], -1 + 7 * shape[i], "normalization off retains original physical amplitude");
            var cancelShape = Average(new[] { small, small }, new[] { shape, shape.Select(v => -v).ToArray() });
            foreach (double value in cancelShape.Y) Close(value, 0, "signed equal mean is not absolute-value average or renormalized");

            var single = Average(new[] { fine }, new[] { pulse });
            Check(single.TraceCount == 1 && single.Y.Length == pulse.Length, "single trace retains native sample count");
            for (int i = 0; i < pulse.Length; ++i) Close(single.Y[i], pulse[i], "single uniform trace retains sample values");
            var plan = Plan(new[] { x, x }, new[] { a, b });
            var exposedAxis = plan.XSeconds; exposedAxis[0] = 999;
            Close(plan.XSeconds[0], 0, "plan time axis cannot be mutated externally");

            Throws<InvalidDataException>(() => Plan(new[] { x }, new[] { a }, null, 10), "sample limit explicit error, no silent downsampling");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.PlanAverage(new List<CalibrationComparisonWaveExtent>(), 10, None), "empty wave list");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(null, a, 0, None), "null samples");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(new double[0], new double[0], 0, None), "empty waveform");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(x, new double[2], 0, None), "mismatched arrays");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(new[] { 1.0, 1 }, new[] { 1.0, 2 }, 0, None), "duplicate times");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(new[] { 1.0, 0 }, new[] { 1.0, 2 }, 0, None), "reversed times");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(new[] { 1.0, Double.PositiveInfinity }, new[] { 1.0, 2 }, 0, None), "nonfinite time");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(new[] { 1.0, 2 }, new[] { 1.0, Double.NaN }, 0, None), "nonfinite amplitude");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(x, a, Double.NaN, None), "nonfinite origin");
            Throws<InvalidDataException>(() => CalibrationComparisonMath.Inspect(x, a, 1e30, None), "precision collapse is explicit failure");

            var accumulator = new CalibrationComparisonAverageAccumulator(plan);
            accumulator.Add(x, a, 0, 0, 1, None);
            Throws<InvalidOperationException>(() => accumulator.Complete(None), "partial selection cannot complete silently");
            Throws<InvalidDataException>(() => accumulator.Add(x, b, 0, 0, 0, None), "zero normalization scale");
            Throws<InvalidDataException>(() => accumulator.Add(shorter, shorter, 0, 0, 1, None), "changed waveform extent rejected");
            var invalid = (double[])b.Clone(); invalid[invalid.Length - 1] = Double.NaN;
            Throws<InvalidDataException>(() => accumulator.Add(x, invalid, 0, 0, 1, None), "invalid late sample is transactional");
            Check(accumulator.TraceCount == 1, "failed addition preserves completed count");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Throws<OperationCanceledException>(() => CalibrationComparisonMath.Inspect(x, a, 0, cancelled.Token), "inspect cancellation");
                Throws<OperationCanceledException>(() => CalibrationComparisonMath.PlanAverage(new[] { CalibrationComparisonMath.Inspect(x, a, 0, None) }, 20, cancelled.Token), "plan cancellation");
                Throws<OperationCanceledException>(() => accumulator.Add(x, b, 0, 0, 1, cancelled.Token), "add cancellation");
                Check(accumulator.TraceCount == 1, "cancelled addition preserves completed count");
            }
            accumulator.Add(x, b, 0, 0, 1, None); var final = accumulator.Complete(None);
            for (int i = 0; i < final.Y.Length; ++i) Close(final.Y[i], two.Y[i], "retry after rejected input gives exact valid mean");
            Throws<InvalidOperationException>(() => accumulator.Add(x, b, 0, 0, 1, None), "extra trace rejected");
            final.Y[0] = 999; Close(accumulator.Complete(None).Y[0], two.Y[0], "result owns independent amplitudes");

            var huge = new[] { Double.MaxValue / 2, Double.MaxValue / 2 };
            var safeHuge = Average(new[] { new[] { 0.0, 1 }, new[] { 0.0, 1 } }, new[] { huge, huge });
            Check(safeHuge.Y.All(CalibrationComparisonMath.Finite), "equal finite large amplitudes avoid sum overflow");
            Console.WriteLine("CalibrationComparisonAverageSmoke PASS " + passed + " assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
