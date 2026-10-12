using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    internal static class RangeDecisionTests
    {
        internal static string Run()
        {
            var quality = new LocationRangeResult { RecommendedIndex = 5, RecommendedFullScaleVolts = .5, Change = true,
                LocationClippedSamples = 8, FullClippedSamples = 11, OutsideClippedSamples = 3,
                LocationQualityPassed = false, LocationQuality = "离线测试削顶，不合格", DriverOverflow = true, RecommendationUncertain = true };
            var context = new RangeDecisionContext(quality, 3, 5, 0, 0, false, false);
            string reason;
            Require(RangeDecisionPolicy.CanAdjustAndRetry(context, out reason), "first adjustment allowed");
            Require(RangeDecisionPolicy.RetryButtonText(context) == "调整并重新采集", "no false high-voltage capability");
            var original = new BenchSettings { RealHardware = true, AutomaticSharedTrigger = true, ArtSerial = "LOCKED-ART", PicoSerial = "LOCKED-PICO", RangeIndex = 3,
                RunNumber = 17, RequestedPicoRateHz = 25e6, ResolutionBits = 15, CaptureDurationMs = 8, PicoCoupling = 0,
                PicoBandwidth20MHz = true, PdCalibrationPcPerV = 2200, DividerVPerV = 12000 };
            var retry = RangeDecisionPolicy.CreateRetrySettings(original, context);
            foreach (FieldInfo field in typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.Name == "RangeIndex" || field.Name == "RunNumber") continue;
                Require(Object.Equals(field.GetValue(original), field.GetValue(retry)), "only range/run changes: " + field.Name);
            }
            Require(original.RangeIndex == 3 && original.RunNumber == 17 && retry.RangeIndex == 5 && retry.RunNumber == 18, "immutable original exact retry");
            var ignored = RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.Ignore);
            Require(ignored.OperatorAcceptedData && !ignored.OriginalQualityPassed && ignored.QualitySnapshot.LocationClippedSamples == 8 &&
                ignored.QualitySnapshot.DriverOverflow && !ignored.RequestedRetry, "ignore cannot erase quality flags");
            quality.LocationClippedSamples = 0;
            context.Quality.LocationClippedSamples = 4;
            Require(ignored.QualitySnapshot.LocationClippedSamples == 8, "decision snapshot deep copy");
            var dismissed = RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.Dismiss);
            Require(!dismissed.OperatorAcceptedData && !dismissed.RequestedRetry && !dismissed.RequestedStopAutomatic, "dismiss is not implicit acceptance");
            Reject(() => RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.StopAutomatic), "stop only appears in automatic flow");
            var second = new RangeDecisionContext(context.Quality, 3, 5, 0, 1, false, false);
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(second, out reason), "one retry limit");
            Reject(() => RangeDecisionPolicy.CreateRecord(second, RangeDecisionAction.AdjustRetry), "policy rejects second retry");
            var automaticRanges = new RangeDecisionContext(context.Quality, 3, 5, 0, 1, false, false, true);
            Require(automaticRanges.IsAutomaticRangeRetry && !automaticRanges.IsAutomaticFlow && RangeDecisionPolicy.RetryLimit(automaticRanges) == 10 &&
                RangeDecisionPolicy.CanAdjustAndRetry(automaticRanges, out reason), "automatic range retries independent of automatic pressure mode");
            var automaticRecord = RangeDecisionPolicy.CreateRecord(automaticRanges, RangeDecisionAction.AdjustRetry);
            Require(automaticRecord.MaximumRetries == 10 && automaticRecord.AutomaticRangeRetriesEnabled && !automaticRecord.WasAutomaticDecision,
                "automatic retry policy metadata does not imply automatic operator action");
            var exhausted = new RangeDecisionContext(context.Quality, 3, 5, 0, 10, false, false, true);
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(exhausted, out reason), "automatic range retry cap");
            Reject(() => RangeDecisionPolicy.CreateRecord(exhausted, RangeDecisionAction.AdjustRetry), "cannot exceed automatic retry cap");
            var atMaximum = new LocationRangeResult { Change = true, RecommendedIndex = 10, MaximumRangeInsufficient = true };
            Require(RangeDecisionPolicy.CanAdjustAndRetry(new RangeDecisionContext(atMaximum, 8, 10, 0, 0, false, false), out reason) &&
                reason.Contains("不保证合格"), "limited conservative recommendation permits last attempt at maximum range");
            var clippedBelowMaximum = LocationRangeAdvisor.Evaluate(new short[] { 32767, -32767 }, 2, 32767, 10, 9, 100, 2100, true);
            Require(clippedBelowMaximum.MaximumRangeInsufficient && clippedBelowMaximum.Change &&
                RangeDecisionPolicy.CanAdjustAndRetry(new RangeDecisionContext(clippedBelowMaximum, 9, 10, 0, 1, false, false, true), out reason),
                "clipped 10V conservative fourfold demand may try 20V once");
            var expectedBeyondMaximum = new LocationRangeResult { Change = true, RecommendedIndex = 10, ExpectedCalibrationVolts = 21, MaximumRangeInsufficient = true };
            var impossibleExpected = new RangeDecisionContext(expectedBeyondMaximum, 8, 10, 0, 0, false, false, true);
            Require(RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(impossibleExpected) &&
                !RangeDecisionPolicy.CanAdjustAndRetry(impossibleExpected, out reason) && reason.Contains("禁止自动重采"),
                "known expected signal above maximum cannot retry even before maximum range");
            var same = new LocationRangeResult { Change = false, RecommendedIndex = 3 };
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(new RangeDecisionContext(same, 3, 3, 0, 0, false, false), out reason), "unchanged range cannot retry");
            var invalidWindow = new LocationRangeResult { WindowValid = false, RecommendedIndex = 5, Change = true };
            var invalidContext = new RangeDecisionContext(invalidWindow, 3, 5, 0, 0, false, false, true);
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(invalidContext, out reason) && reason.Contains("窗口无效"), "invalid window cannot authorize retry even with a range change");
            Reject(() => RangeDecisionPolicy.CreateRecord(invalidContext, RangeDecisionAction.AdjustRetry), "invalid-window retry decision rejected");
            var minimum = new LocationRangeResult { RecommendedIndex = 0, Change = false, LocationLowOccupancy = true };
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(new RangeDecisionContext(minimum, 0, 0, 0, 0, false, false, true), out reason), "minimum range cannot repeat same range indefinitely");
            foreach (var risk in new[] {
                new LocationRangeResult { RecommendedIndex = 10, LocationClippedSamples = 1 },
                new LocationRangeResult { RecommendedIndex = 10, LocationHighOccupancy = true },
                new LocationRangeResult { RecommendedIndex = 10, DriverOverflowUncertain = true },
                new LocationRangeResult { RecommendedIndex = 10, DriverOverflow = true } })
            {
                var maximumRisk = new RangeDecisionContext(risk, 10, 10, 0, 0, false, false, true);
                Require(RangeDecisionPolicy.IsMaximumRangeRisk(maximumRisk) && !RangeDecisionPolicy.CanAdjustAndRetry(maximumRisk, out reason) &&
                    reason.Contains("不证明"), "maximum-range warning cannot be suppressed or misstate physical aging");
                Require(RangeDecisionPolicy.CreateRecord(maximumRisk, RangeDecisionAction.Ignore).OperatorAcceptedData, "maximum risk Ignore saves operator decision without retry");
            }
            var mismatch = new RangeDecisionContext(context.Quality, 3, 6, 0, 0, false, false);
            Require(!RangeDecisionPolicy.CanAdjustAndRetry(mismatch, out reason), "diagnostic recommendation identity");
            var automatic = new RangeDecisionContext(context.Quality, 3, 5, 10000, 0, true, true);
            Require(RangeDecisionPolicy.RetryButtonText(automatic) == "调整并重新加压", "capability-specific pressurization label");
            var stop = RangeDecisionPolicy.CreateRecord(automatic, RangeDecisionAction.StopAutomatic);
            Require(stop.RequestedStopAutomatic && !stop.RequestedRetry && !stop.OperatorAcceptedData, "automatic stop explicit");
            bool falseVoltageRejected = false;
            try { new RangeDecisionContext(context.Quality, 3, 5, 1000, 0, false, false); } catch (ArgumentException) { falseVoltageRejected = true; }
            Require(falseVoltageRejected, "no nonzero voltage claim in zero bench");
            using (var stream = new MemoryStream())
            {
                var serializer = new XmlSerializer(typeof(RangeDecisionRecord)); serializer.Serialize(stream, ignored); stream.Position = 0;
                var restored = (RangeDecisionRecord)serializer.Deserialize(stream);
                Require(restored.Action == RangeDecisionAction.Ignore && restored.OperatorAcceptedData && !restored.OriginalQualityPassed &&
                    restored.QualitySnapshot.LocationClippedSamples == 8 && restored.DecidedUtc.Kind == DateTimeKind.Utc, "XML decision retains quality and UTC");
            }
            return "RANGE_DECISION_TESTS=PASS: explicit actions; manual one retry / automatic max10 independent of pressure mode; range bounds and unsuppressible maximum-risk warning; immutable exact-target settings clone, persistent quality snapshot and XML; no forms shown or hardware accessed.";
        }
        private static void Require(bool value, string name) { if (!value) throw new Exception("Range decision test failed: " + name); }
        private static void Reject(Action action, string name) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Require(rejected, name); }
    }
}
