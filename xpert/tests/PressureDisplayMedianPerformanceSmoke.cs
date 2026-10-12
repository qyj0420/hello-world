using System;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using System.IO;
using HVX.Controls;

internal static class PressureDisplayMedianPerformanceSmoke
{
    static int checks;
    static Type medianType = typeof(PressureDisplayMedian);
    static Func<double[], CancellationToken, double> calculate = PressureDisplayMedian.Calculate;
    static void Assert(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    static double SortedMedian(double[] values)
    {
        if (values == null || values.Length == 0) return 0;
        double[] copy = (double[])values.Clone(); Array.Sort(copy);
        int middle = copy.Length / 2;
        return (copy.Length & 1) != 0 ? copy[middle] : (copy[middle - 1] + copy[middle]) * .5;
    }
    static void Case(double[] values, string name)
    {
        double[] original = values == null ? null : (double[])values.Clone();
        double actual = calculate(values, CancellationToken.None), expected = SortedMedian(values);
        Assert(actual.Equals(expected), name + " median differs from sorted reference");
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
                if (BitConverter.DoubleToInt64Bits(values[i]) != BitConverter.DoubleToInt64Bits(original[i]))
                    throw new Exception(name + " mutated input at " + i);
            Assert(true, name + " input immutable");
        }
    }
    static void Bench(double[] values, string name)
    {
        double expected = 0, actual = 0; long referenceMs = 0, selectedMs = 0;
        // Alternating runs reduce one-direction warm-up bias. Report timings,
        // not a hardware guarantee or acceptance of physical control latency.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            var watch = Stopwatch.StartNew();
            if ((iteration & 1) == 0)
            {
                expected = SortedMedian(values); referenceMs += watch.ElapsedMilliseconds;
                watch.Restart(); actual = calculate(values, CancellationToken.None); selectedMs += watch.ElapsedMilliseconds;
            }
            else
            {
                actual = calculate(values, CancellationToken.None); selectedMs += watch.ElapsedMilliseconds;
                watch.Restart(); expected = SortedMedian(values); referenceMs += watch.ElapsedMilliseconds;
            }
            Assert(actual.Equals(expected), name + " benchmark exact result");
        }
        Console.WriteLine("TIMING name=" + name + " samples=" + values.Length + " runs=3 sorted_total_ms=" + referenceMs
            + " selected_total_ms=" + selectedMs + " ratio=" + (selectedMs / (double)Math.Max(1, referenceMs)).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                var assembly = Assembly.LoadFrom(Path.Combine(args[0], "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
                medianType = assembly.GetType("HVX.Controls.PressureDisplayMedian", true);
                calculate = (Func<double[], CancellationToken, double>)Delegate.CreateDelegate(typeof(Func<double[], CancellationToken, double>),
                    medianType.GetMethod("Calculate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
                Console.WriteLine("ACTUAL_CANDIDATE=" + assembly.Location);
            }
            Case(null, "null"); Case(new double[0], "empty");
            Case(new double[] { 5 }, "one"); Case(new double[] { 9, 1 }, "two");
            Case(new double[] { Double.NaN, Double.NegativeInfinity, -0.0, 0, Double.PositiveInfinity }, "total double order");
            Case(new double[] { Double.MaxValue, Double.MaxValue }, "preserve overflow arithmetic");
            var random = new Random(20261010);
            for (int n = 0; n < 200; n++)
            {
                var v = new double[n]; for (int i = 0; i < n; i++) v[i] = random.Next(-8, 9) * .125;
                Case(v, "duplicate random " + n); Array.Sort(v); Case(v, "sorted " + n);
                Array.Reverse(v); Case(v, "reverse " + n);
            }
            for (int n = 201; n < 8192; n += 193)
            {
                var v = new double[n]; for (int i = 0; i < n; i++) v[i] = random.NextDouble() * 200 - 100;
                Case(v, "continuous random " + n);
            }
            var adversarial = new double[100003];
            for (int i = 0; i < adversarial.Length; i++) adversarial[i] = Math.Min(i, adversarial.Length - i);
            Case(adversarial, "organ pipe");
            // Exercise the cancellable fallback directly; pivot-dependent
            // fallback coverage is otherwise not deterministic.
            var heap = medianType.GetMethod("HeapSort", BindingFlags.Static | BindingFlags.NonPublic);
            var heapInput = new double[10001]; for (int i = 0; i < heapInput.Length; i++) heapInput[i] = random.NextDouble();
            var heapExpected = (double[])heapInput.Clone(); Array.Sort(heapExpected, 37, 9871);
            heap.Invoke(null, new object[] { heapInput, 37, 9907, CancellationToken.None });
            for (int i = 0; i < heapInput.Length; i++) Assert(heapInput[i].Equals(heapExpected[i]), "fallback exact interval sorting " + i);
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel(); bool canceled = false;
                try { calculate(adversarial, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled, "pre-cancellation propagated");
            }
            var large = new double[6250000];
            for (int i = 0; i < large.Length; i++) large[i] = random.Next(-32767, 32768) * .00123;
            Bench(large, "quantized ADC-sized display");
            for (int i = 0; i < large.Length; i++) large[i] = random.NextDouble();
            Bench(large, "continuous filtered display");
            using (var stop = new CancellationTokenSource())
            {
                stop.CancelAfter(5); var watch = Stopwatch.StartNew(); bool canceled = false;
                try { calculate(large, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && watch.ElapsedMilliseconds < 2000, "in-flight selection cancellation bounded");
                Console.WriteLine("CANCEL_MS=" + watch.ElapsedMilliseconds);
            }
            Console.WriteLine("PASS checks=" + checks + " hardware=false sdk=false product=false user_projects_written=false"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
