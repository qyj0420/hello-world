using System;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using System.IO;
using HVX.Controls;

internal static class PressurePdDisplayEnvelopeSmoke
{
    static int checks;
    static Type candidateType;
    static Func<int, int, int> NewEnvelope(double[] values, double baseline, CancellationToken token)
    {
        if (candidateType == null)
        {
            var value = new PressurePdDisplayEnvelope(values, baseline, token);
            return value.MaximumIndex;
        }
        object instance;
        try { instance = candidateType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { typeof(double[]), typeof(double), typeof(CancellationToken) }, null).Invoke(new object[] { values, baseline, token }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        return (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>), instance,
            candidateType.GetMethod("MaximumIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }
    static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    static int Reference(double[] values, double baseline, int first, int last)
    {
        int best = first;
        for (int i = first + 1; i <= last; i++) if (Math.Abs(values[i] - baseline) > Math.Abs(values[best] - baseline)) best = i;
        return best;
    }
    static void Case(double[] values, double baseline, Random random)
    {
        var original = (double[])values.Clone(); var envelope = NewEnvelope(values, baseline, CancellationToken.None);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int first = random.Next(values.Length), last = random.Next(first, values.Length);
            Check(envelope(first, last) == Reference(values, baseline, first, last), "exact bucket index/tie order " + iteration);
        }
        for (int i = 0; i < values.Length; i++)
            if (BitConverter.DoubleToInt64Bits(values[i]) != BitConverter.DoubleToInt64Bits(original[i])) throw new Exception("source modified");
        Check(true, "immutable source");
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0)
            {
                var assembly = Assembly.LoadFrom(Path.Combine(args[0], "modules", "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
                candidateType = assembly.GetType("HVX.Controls.PressurePdDisplayEnvelope", true);
                Console.WriteLine("ACTUAL_CANDIDATE=" + assembly.Location);
            }
            var random = new Random(20261010);
            foreach (int n in new[] { 1, 2, 255, 256, 257, 1024, 1025, 10007 })
            {
                var values = new double[n]; for (int i = 0; i < n; i++) values[i] = random.Next(-9, 10) * .125;
                foreach (double baseline in new[] { 0d, -.125, .125, 10 }) Case(values, baseline, random);
                Array.Clear(values, 0, values.Length); Case(values, 0, random);
            }
            var empty = NewEnvelope(new double[0], 0, CancellationToken.None);
            bool rejected = false; try { empty(0, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "empty query rejected");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); rejected = false;
                try { NewEnvelope(new double[0], 0, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected, "pre-cancellation propagated");
            }
            var large = new double[6250000]; for (int i = 0; i < large.Length; i++) large[i] = random.NextDouble() * 100;
            large[256] = 200; large[6250000 - 257] = -250;
            var watch = Stopwatch.StartNew(); var indexed = NewEnvelope(large, .123, CancellationToken.None);
            long prepareMs = watch.ElapsedMilliseconds; int expectedChecksum = 0, actualChecksum = 0;
            const int buckets = 600, frames = 20;
            watch.Restart();
            for (int frame = 0; frame < frames; frame++) for (int bucket = 0; bucket < buckets; bucket++)
            {
                int first = (int)((long)bucket * large.Length / buckets), last = (int)((long)(bucket + 1) * large.Length / buckets) - 1;
                expectedChecksum ^= Reference(large, .123, first, last);
            }
            long beforeMs = watch.ElapsedMilliseconds; watch.Restart();
            for (int frame = 0; frame < frames; frame++) for (int bucket = 0; bucket < buckets; bucket++)
            {
                int first = (int)((long)bucket * large.Length / buckets), last = (int)((long)(bucket + 1) * large.Length / buckets) - 1;
                actualChecksum ^= indexed(first, last);
            }
            long afterMs = watch.ElapsedMilliseconds;
            Check(expectedChecksum == actualChecksum, "repeated frames exact extrema checksum");
            // Compare individually too, so the even frame count checksum does
            // not accidentally conceal different extrema.
            for (int bucket = 0; bucket < buckets; bucket++)
            {
                int first = (int)((long)bucket * large.Length / buckets), last = (int)((long)(bucket + 1) * large.Length / buckets) - 1;
                Check(indexed(first, last) == Reference(large, .123, first, last), "large frame exact individual bucket " + bucket);
            }
            Console.WriteLine("TIMING samples=" + large.Length + " frames=" + frames + " buckets=" + buckets + " prepare_ms=" + prepareMs
                + " old_scan_ms=" + beforeMs + " indexed_scan_ms=" + afterMs);
            Console.WriteLine("PASS checks=" + checks + " hardware=false sdk=false product=false user_projects_written=false"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
