using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HVX.SignalProcessing;

internal static class PdHighPassInteriorFastPathSmoke
{
    static int checks;
    static MethodInfo original;
    static MethodInfo candidateApply, candidateCancellable, candidateReflect;
    static double[] Candidate(double[] values, double rate)
    {
        if (candidateApply == null) return PdLinearHighPass.Apply(values, rate);
        try { return (double[])candidateApply.Invoke(null, new object[] { values, rate }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    static double[] Reference(double[] values, double rate) { return (double[])original.Invoke(null, new object[] { values, rate }); }
    static void Exact(double[] values, double rate, string name)
    {
        var retained = (double[])values.Clone(); var expected = Reference(values, rate); var actual = Candidate(values, rate);
        Check(expected.Length == actual.Length, name + " length");
        for (int i = 0; i < values.Length; i++)
        {
            if (BitConverter.DoubleToInt64Bits(actual[i]) != BitConverter.DoubleToInt64Bits(expected[i])) throw new Exception(name + " differs at " + i);
            if (BitConverter.DoubleToInt64Bits(values[i]) != BitConverter.DoubleToInt64Bits(retained[i])) throw new Exception(name + " modifies input at " + i);
        }
        Check(true, name + " bit-identical filtered output/input preservation");
    }
    static int Main(string[] args)
    {
        try
        {
            string path = Path.GetFullPath(args[0]);
            using (var file = File.OpenRead(path)) using (var hash = SHA256.Create())
                Check(BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "") == "178E6D277E4024CDDABD5E2F76D126BEEDBC6E2DB6467E4473F1D775F34F0684", "exact frozen r39 baseline");
            var baseline = Assembly.LoadFrom(path).GetType("HVX.SignalProcessing.PdLinearHighPass", true);
            original = baseline.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
            Check((string)baseline.GetField("FilterId").GetValue(null) == PdLinearHighPass.FilterId, "same filter identity");
            var reflect = typeof(PdLinearHighPass).GetMethod("Reflect", BindingFlags.NonPublic | BindingFlags.Static);
            if (args.Length > 1)
            {
                // LoadFile deliberately isolates the same assembly identity from
                // the old reference, so LoadFrom cannot silently reuse old bytes.
                var candidate = Assembly.LoadFile(Path.GetFullPath(args[1])).GetType("HVX.SignalProcessing.PdLinearHighPass", true);
                candidateApply = candidate.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
                candidateCancellable = candidate.GetMethod("ApplyCancellable", BindingFlags.Public | BindingFlags.Static);
                candidateReflect = candidate.GetMethod("Reflect", BindingFlags.NonPublic | BindingFlags.Static);
                Check((string)candidate.GetField("FilterId").GetValue(null) == PdLinearHighPass.FilterId, "candidate filter identity unchanged");
                reflect = candidateReflect;
            }
            foreach (int count in new[] { 1, 2, 3, 7, 1024, 100007 })
                foreach (int index in new[] { Int32.MinValue, -1000007, -1024, -7, -1, 0, 1, 2, 1024, 1000007, Int32.MaxValue })
                {
                    int expected = 0;
                    if (count != 1) { int period = 2 * (count - 1), reduced = index % period; if (reduced < 0) reduced += period; expected = reduced < count ? reduced : period - reduced; }
                    Check((int)reflect.Invoke(null, new object[] { index, count }) == expected, "edge modulo preserved " + count + "/" + index);
                }
            var random = new Random(20261010);
            foreach (double rate in new[] { 100000d, 1000000d, 125000000d, 1000000000d })
                foreach (int count in new[] { 0, 1, 2, 3, 20, 401, 4001 })
                {
                    var values = new double[count]; for (int i = 0; i < count; i++) values[i] = random.Next(-32767, 32768) * .00125;
                    Exact(values, rate, "random edges " + rate + "/" + count);
                }
            foreach (bool constant in new[] { true, false })
            {
                var values = new double[100001]; for (int i = 0; i < values.Length; i++) values[i] = constant ? 12 : i == values.Length / 2 ? 1 : 0;
                Exact(values, 125000000, constant ? "DC" : "symmetric impulse");
            }
            foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity })
            {
                bool rejected = false; try { Candidate(new[] { invalid }, 1e6); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "nonfinite still rejected");
            }
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel(); bool canceled = false;
                try {
                    if (candidateCancellable == null) PdLinearHighPass.ApplyCancellable(new double[100], 1e6, stop.Token);
                    else try { candidateCancellable.Invoke(null, new object[] { new double[100], 1e6, stop.Token }); }
                        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
                } catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "cancellation still propagated");
            }
            var large = new double[6250000]; for (int i = 0; i < large.Length; i++) large[i] = Math.Sin(i * .0037) + random.NextDouble() * .125;
            var watch = Stopwatch.StartNew(); var expectedLarge = Reference(large, 125000000); long oldMs = watch.ElapsedMilliseconds;
            watch.Restart(); var actualLarge = Candidate(large, 125000000); long newMs = watch.ElapsedMilliseconds;
            for (int i = 0; i < large.Length; i++)
                if (BitConverter.DoubleToInt64Bits(actualLarge[i]) != BitConverter.DoubleToInt64Bits(expectedLarge[i])) throw new Exception("full-rate bit mismatch " + i);
            Check(true, "all 6250000 output samples bit-identical to frozen protected baseline");
            Console.WriteLine("TIMING samples=" + large.Length + " rate=125000000 frozen_protected_baseline_ms=" + oldMs +
                (candidateApply == null ? " source_fast_path_ms=" : " final_candidate_ms=") + newMs);
            Console.WriteLine("PASS checks=" + checks + " hardware=false sdk=false product=false user_projects_written=false"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
