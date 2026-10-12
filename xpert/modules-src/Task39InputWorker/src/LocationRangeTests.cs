using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    internal static class LocationRangeTests
    {
        internal static string Run()
        {
            const int maxAdc = 32767;
            var shortRecord = LocationRangeAdvisor.Evaluate(new short[] { 0, 30, -20 }, 2500000, maxAdc, 20, 10, 0, 2100, false);
            Require(shortRecord.WindowShortened && shortRecord.LocationSampleCount == 3 && shortRecord.OutsideSampleCount == 0, "short record uses every available sample without padding");
            Require(shortRecord.RecommendedIndex == 2 && shortRecord.LocationLowOccupancy && !shortRecord.LocationQualityPassed, "background can jump from 20 V to 50 mV without pulse candidates but cannot establish good range utilization");

            var zero = LocationRangeAdvisor.Evaluate(new short[10], 10, maxAdc, 20, 10, 0, 2100, false);
            Require(zero.RecommendedIndex == 0 && !zero.RecommendationUncertain, "zero background directly selects minimum range");
            Require(zero.LocationLowOccupancy && !zero.LocationQualityPassed, "zero signal does not pass range optimization or SNR quality");
            var calibration = LocationRangeAdvisor.Evaluate(new short[10], 10, maxAdc, 20, 10, 100, 2100, false);
            Require(calibration.RecommendedIndex == 3 && Math.Abs(calibration.ExpectedCalibrationVolts - 100.0 / 2100) < 1e-12, "known calibration amplitude sets 100 mV range despite quiet background");

            var raw = new short[] { 0, -32768, -32768, 0, 32767, 32767, 32767, 0 };
            var before = (short[])raw.Clone();
            var negativeClip = LocationRangeAdvisor.Evaluate(raw, 5, maxAdc, .1, 3, 0, 2100, false);
            Require(negativeClip.LocationClippedSamples == 3 && negativeClip.LocationLongestClippedRun == 2, "negative saturation safely widened and local run counted");
            Require(negativeClip.OutsideClippedSamples == 2 && negativeClip.OutsideLongestClippedRun == 2, "boundary belongs exclusively to outside window");
            Require(negativeClip.FullClippedSamples == 5 && negativeClip.FullLongestClippedRun == 3, "full run may cross window boundary");
            Require(negativeClip.RecommendedIndex == 5 && negativeClip.RecommendationUncertain && !negativeClip.LocationQualityPassed, "clipping proposes at least fourfold cross-range increase with uncertainty");
            Require(negativeClip.LocationPeakFraction > 1, "negative full-scale code is not clamped");
            for (int i = 0; i < raw.Length; i++) Require(raw[i] == before[i], "raw samples not changed");

            var outsideOnly = LocationRangeAdvisor.Evaluate(new short[] { 0, 100, -100, 32767, -32768 }, 3, maxAdc, 1, 6, 0, 2100, false);
            Require(outsideOnly.LocationClippedSamples == 0 && outsideOnly.OutsideClippedSamples == 2 && outsideOnly.LocationLowOccupancy && !outsideOnly.RecommendationUncertain, "outside clipping remains separate from low-occupancy location-window quality");
            Require(outsideOnly.RecommendedIndex == 0 && outsideOnly.FullPeakVolts > 1 && outsideOnly.OutsidePeakVolts > 1, "outside transient preserved separately and does not choose location range");
            var suitableWindow = LocationRangeAdvisor.Evaluate(new short[] { 0, 5000, -5000, 32767 }, 3, maxAdc, 1, 6, 0, 2100, false);
            Require(suitableWindow.LocationQualityPassed && !suitableWindow.LocationLowOccupancy && suitableWindow.OutsideClippedSamples == 1, "suitable 10-to-80 percent window can pass independently of outside clipping");
            var lowerBoundary = LocationRangeAdvisor.Evaluate(new short[] { 1000 }, 1, 10000, 1, 6, 0, 2100, false);
            Require(lowerBoundary.LocationQualityPassed && !lowerBoundary.LocationLowOccupancy, "exact 10 percent belongs to accepted occupancy interval");
            var belowBoundary = LocationRangeAdvisor.Evaluate(new short[] { 999 }, 1, 10000, 1, 6, 0, 2100, false);
            Require(!belowBoundary.LocationQualityPassed && belowBoundary.LocationLowOccupancy, "below 10 percent is not an optimized range");
            var globalOverflow = LocationRangeAdvisor.Evaluate(new short[] { 0, 100, -100, 32767 }, 3, maxAdc, 1, 6, 0, 2100, true);
            Require(globalOverflow.DriverOverflowUncertain && globalOverflow.RecommendationUncertain && !globalOverflow.LocationQualityPassed, "driver overflow without window clipping cannot pass");
            Require(globalOverflow.RecommendedIndex == 6, "unlocalized driver overflow prevents automatic reduction");

            var highUsage = LocationRangeAdvisor.Evaluate(new short[] { 27000, 0 }, 2, maxAdc, .1, 3, 0, 2100, false);
            Require(highUsage.LocationHighOccupancy && highUsage.LocationClippedSamples == 0 && !highUsage.LocationQualityPassed && highUsage.RecommendedIndex == 4, "80 percent occupancy drives next capture up without claiming clipping");
            var clippedMaximum = LocationRangeAdvisor.Evaluate(new short[] { -32767 }, 1, maxAdc, 20, 10, 0, 2100, true);
            Require(clippedMaximum.AtMaximumAndClipped && clippedMaximum.MaximumRangeInsufficient && clippedMaximum.RecommendedIndex == 10 && !clippedMaximum.Change, "maximum range clipped explicitly has no greater option");
            var excessiveCalibration = LocationRangeAdvisor.Evaluate(new short[2], 2, maxAdc, .1, 3, 42000, 2100, false);
            Require(excessiveCalibration.RecommendedIndex == 10 && excessiveCalibration.MaximumRangeInsufficient, "calibration demand cannot silently exceed maximum range");

            // MaxAdc need not equal Int16.MaxValue: the hardware's reported limit is authoritative.
            var customMax = LocationRangeAdvisor.Evaluate(new short[] { -10000, 9999, 10000 }, 2, 10000, .2, 4, 0, 2100, false);
            Require(customMax.LocationClippedSamples == 1 && customMax.OutsideClippedSamples == 1 && customMax.LocationPeakVolts == .2, "hardware ADC limit and exclusive window boundary honored");
            var oneSample = LocationRangeAdvisor.Evaluate(new short[] { 1 }, 1, maxAdc, .01, 0, 0, 2100, false);
            Require(!oneSample.WindowShortened && oneSample.LocationSampleCount == 1, "single sample window supported");
            var middleWindow = LocationRangeAdvisor.Evaluate(new short[] { 32767, 32767, 5000, -5000, 32767, 32767 }, 2, 2, maxAdc, 1, 6, 0, 2100, false);
            Require(middleWindow.WindowValid && middleWindow.WindowStartSample == 2 && middleWindow.WindowEndExclusive == 4 &&
                middleWindow.LocationSampleCount == 2 && middleWindow.OutsideSampleCount == 4, "explicit nonzero-start window carries exact exclusive boundaries");
            Require(middleWindow.LocationClippedSamples == 0 && middleWindow.LocationQualityPassed &&
                middleWindow.OutsideClippedSamples == 4 && middleWindow.OutsideLongestClippedRun == 2, "outside prefix and suffix clipped runs are not concatenated across the selected window");
            Require(middleWindow.RecommendedIndex == 5, "large before/after overshoots do not set the middle window recommendation");
            var serializer = new XmlSerializer(typeof(LocationRangeResult));
            using (var text = new StringWriter())
            {
                serializer.Serialize(text, negativeClip);
                using (var input = new StringReader(text.ToString()))
                {
                    var copy = (LocationRangeResult)serializer.Deserialize(input);
                    Require(copy.LocationClippedSamples == 3 && copy.RecommendedIndex == 5 && copy.RecommendationUncertain && copy.Reason == negativeClip.Reason, "XML serialization round trip preserves diagnostics");
                }
            }
            Reject(() => LocationRangeAdvisor.Evaluate(null, 1, maxAdc, 1, 6, 0, 2100, false), "missing data");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[0], 1, maxAdc, 1, 6, 0, 2100, false), "empty data");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 0, maxAdc, 1, 6, 0, 2100, false), "empty window");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 1, 0, 1, 6, 0, 2100, false), "invalid ADC upper bound");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 1, maxAdc, .5, 6, 0, 2100, false), "range metadata mismatch");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 1, maxAdc, Double.NaN, 6, 0, 2100, false), "NaN scale");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 1, maxAdc, 1, 6, Double.NaN, 2100, false), "NaN calibration");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[1], 1, maxAdc, 1, 6, 0, 0, false), "zero coefficient");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[3], 1, 3, maxAdc, 1, 6, 0, 2100, false), "explicit window cannot be silently shortened");
            Reject(() => LocationRangeAdvisor.Evaluate(new short[3], -1, 2, maxAdc, 1, 6, 0, 2100, false), "negative explicit start");
            return "LOCATION_RANGE_TESTS=PASS: raw location-window saturation/counts/runs, direct cross-range/background/calibration recommendation, exclusive window boundary, outside overshoot isolation, driver uncertainty, maximum range, immutable raw data, short records and XML round trip; no hardware accessed.";
        }
        private static void Require(bool condition, string name) { if (!condition) throw new Exception("Location range test failed: " + name); }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Require(rejected, name);
        }
    }
}
