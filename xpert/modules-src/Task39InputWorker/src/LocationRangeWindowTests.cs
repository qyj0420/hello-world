using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    internal static class LocationRangeWindowTests
    {
        internal static string Run()
        {
            const int maxAdc = 32767;
            var raw = new short[] { 32767, 200, -400, 300, 500, -32768 };
            var snapshot = (short[])raw.Clone();
            var zero = LocationRangeWindow.Select(0, 0, raw.Length, 125e6, -1, Double.NaN, Double.NaN, false);
            Require(zero.Valid && zero.StartSample == 0 && zero.Count == raw.Length && zero.EndExclusive == raw.Length && zero.RequestedCycles == 0,
                "0 V uses all provided location data without phase, frequency, or N");
            var zeroAnalysis = LocationRangeAdvisor.Evaluate(raw, zero, maxAdc, .1, 3, 100, 2100, false);
            Require(zeroAnalysis.WindowValid && zeroAnalysis.LocationClippedSamples == 2 && zeroAnalysis.OutsideClippedSamples == 0,
                "0 V keeps both leading and trailing overshoots; no transient skip or calibration-period exclusion");
            Require(zeroAnalysis.WindowStartSample == 0 && zeroAnalysis.WindowEndExclusive == raw.Length && zeroAnalysis.WindowDefinition == zero.Definition,
                "window definition is retained in serialized analysis");
            for (int i = 0; i < raw.Length; i++) Require(raw[i] == snapshot[i], "0 V raw data immutable");
            var offsetZero = LocationRangeWindow.Select(0, 3, 5, 1000, 0, -100, -1, true);
            Require(offsetZero.Valid && offsetZero.StartSample == 3 && offsetZero.EndExclusive == 8, "0 V respects full provided segment even with nonzero available origin");

            var cycles = LocationRangeWindow.Select(1000, 0, 12, 1000, 2, .002, 250, true);
            Require(cycles.Valid && cycles.StartSample == 2 && cycles.EndExclusive == 10 && cycles.Count == 8, "two complete cycles start at validated first zero and exclude end boundary");
            var samples = new short[] { 32767, 32767, 0, 5000, -5000, 3000, -3000, 2000, -2000, 0, 32767, 32767 };
            var samplesBefore = (short[])samples.Clone();
            var middle = LocationRangeAdvisor.Evaluate(samples, cycles, maxAdc, 1, 6, 0, 2100, false);
            Require(middle.WindowValid && middle.LocationQualityPassed && middle.LocationClippedSamples == 0 &&
                middle.OutsideClippedSamples == 4 && middle.OutsideLongestClippedRun == 2 && middle.RecommendedIndex == 5,
                "before/after overshoots remain outside complete-cycle evaluation and do not drive recommendation");
            for (int i = 0; i < samples.Length; i++) Require(samples[i] == samplesBefore[i], "nonzero window analysis cannot alter source data");
            var fractional = LocationRangeWindow.Select(1, 0, 10, 1000, 1, .0021, 400, true);
            Require(fractional.Valid && fractional.StartSample == 3 && fractional.EndExclusive == 5 && fractional.Count == 2,
                "both fractional boundaries are mapped with ceil");
            var boundary = LocationRangeWindow.Select(1, 0, 10, 1000, 2, .002, 250, true);
            Require(boundary.Valid && boundary.EndExclusive == 10, "complete cycle ending exactly at available end is accepted");

            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 2, .002, 250, false), samples, "unvalidated first phase");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 9, 1000, 2, .002, 250, true), samples, "insufficient complete N cycles");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 3, 9, 1000, 2, .002, 250, true), samples, "first zero precedes available segment");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 0, .002, 250, true), samples, "zero requested cycles for nonzero voltage");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 2, Double.NaN, 250, true), samples, "invalid zero-phase time");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 2, .002, 0, true), samples, "zero validated frequency");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 2, .002, Double.NaN, true), samples, "NaN frequency");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(1, 0, 12, 1000, 2, .002, Double.Epsilon, true), samples, "overflowed cycle end");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(0, 0, 0, 1000, 2, .002, 250, true), samples, "empty zero-volt segment");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(0, 0, 12, Double.NaN, 2, .002, 250, true), samples, "invalid actual sample rate");
            AssertInvalidNoAdvice(LocationRangeWindow.Select(Double.NaN, 0, 12, 1000, 2, .002, 250, true), samples, "invalid target voltage");
            var validButLongerThanData = LocationRangeWindow.Select(1, 0, 12, 1000, 2, .002, 250, true);
            var tooShort = LocationRangeAdvisor.Evaluate(new short[5], validButLongerThanData, maxAdc, 1, 6, 0, 2100, false);
            Require(!tooShort.WindowValid && tooShort.RecommendedIndex == -1 && !tooShort.Change, "window bridge rejects incomplete actual data without truncation or advice");
            var serializer = new XmlSerializer(typeof(LocationRangeResult));
            using (var output = new StringWriter())
            {
                serializer.Serialize(output, middle);
                using (var input = new StringReader(output.ToString()))
                {
                    var copy = (LocationRangeResult)serializer.Deserialize(input);
                    Require(copy.WindowValid && copy.WindowStartSample == 2 && copy.WindowEndExclusive == 10 && copy.WindowDefinition == cycles.Definition,
                        "XML retains exact window origin, end, validity, and definition");
                }
            }
            return "LOCATION_RANGE_WINDOW_TESTS=PASS: 0 V whole location segment includes start/end overshoots; nonzero validated phase+frequency N-cycle ceil window; insufficient/missing-phase windows prohibit advice; outside runs remain disjoint; immutable samples and exact XML boundaries; no HV or hardware accessed.";
        }

        private static void AssertInvalidNoAdvice(LocationRangeWindowResult window, short[] data, string name)
        {
            Require(!window.Valid && !String.IsNullOrEmpty(window.FailureReason), name + " rejects selection");
            var assessment = LocationRangeAdvisor.Evaluate(data, window, 32767, 1, 6, 100, 2100, false);
            Require(!assessment.WindowValid && !assessment.LocationQualityPassed && assessment.RecommendedIndex == -1 &&
                !assessment.Change && assessment.RecommendationUncertain && Double.IsNaN(assessment.LocationPeakVolts), name + " produces no range recommendation");
        }
        private static void Require(bool condition, string name) { if (!condition) throw new Exception("Location range window test failed: " + name); }
    }
}
