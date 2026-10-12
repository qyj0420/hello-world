using System;
using System.Collections.Generic;
using Xiongxin.ZeroBench;
using Xiongxin.FactoryTest.Compatibility100;

namespace Xiongxin.FactoryTest
{
    // Pure sample/decision regressions; no UI, worker or device calls.
    internal static class FactoryCompatibility100Tests
    {
        internal static string Run()
        {
            int checks = 0;
            var round = Round();
            // Below R30's 25% retain band: shared 65% headroom still downranges.
            for (int i = 0; i < round.Capture.PdAdc.Length; i++) round.Capture.PdAdc[i] = 6000;
            var q = FactoryRangeReview.Evaluate(round);
            Require(q.WindowValid && q.LocationSampleCount == round.Capture.PdAdc.Length,
                "0 kV uses the complete raw segment"); checks++;
            Require(q.LocationQualityPassed && q.RecommendedIndex == 3 && q.Change,
                "100 advisor chooses a direct 65% headroom range"); checks++;
            var context = FactoryRangeReview.CreateContext(round, q, 0, false);
            var ignored = RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.Ignore);
            Require(ignored.OperatorAcceptedData && ignored.QualitySnapshot.LocationQualityPassed,
                "ignore is explicit operator acceptance"); checks++;
            q.LocationQualityPassed = false;
            Require(ignored.QualitySnapshot.LocationQualityPassed, "decision quality is a deep snapshot"); checks++;
            var retry = FactoryRangeReview.CreateRetrySettings(round, context, new HashSet<int> { 4 });
            Require(retry.Input.RangeIndex == 3 && round.Settings.Input.RangeIndex == 4,
                "retry changes next settings only"); checks++;
            RequireThrows(() => FactoryRangeReview.CreateRetrySettings(round, context, new HashSet<int> { 3, 4 }),
                "revisiting a range is rejected"); checks++;

            round.Settings.TargetKv = 1;
            round.Capture.Settings.OscillationCircuitConnected=true;
            for(int i=0;i<round.Capture.VoltageRaw.Length;i++)round.Capture.VoltageRaw[i]=-Math.Cos(2*Math.PI*400*i/round.Capture.ArtRateHz);
            round.Settings.RangeEvaluationCycleCount = 8;
            round.Metrics = new FactoryMetrics { FrequencyValid = true, FrequencyHz = 400,
                ReferenceAnalysis = new FactoryReferenceResult { PhaseValid = true, Valid = false, FirstZeroMs = .625 } };
            round.Capture.PdAdc[0] = 32767;
            q = FactoryRangeReview.Evaluate(round);
            Require(q.WindowValid && Math.Abs(q.WindowStartSample-625)<=1 && Math.Abs(q.WindowEndExclusive-3125)<=1,
                "factory nonzero raw window is first zero plus saved one cycle, independent of decay"); checks++;
            Require(q.LocationClippedSamples == 0 && q.OutsideClippedSamples == 1,
                "starting overshoot remains outside window evidence"); checks++;
            Require(q.LocationQualityPassed,
                "valid phase remains usable with standard capacitor inversion disabled"); checks++;
            round.Settings.RangeEvaluationCycleCount = 30;
            q = FactoryRangeReview.Evaluate(round);
            Require(q.WindowValid && Math.Abs(q.WindowStartSample-625)<=1,
                "legacy range cycle setting no longer gates factory ranging"); checks++;
            context = FactoryRangeReview.CreateContext(round, q, 0, true);
            string reason;
            Require(q.WindowEndExclusive < round.Capture.PdAdc.Length,
                "factory range uses saved N rather than legacy inactive count"); checks++;
            round.Settings.RangeEvaluationCycleCount = 8;
            round.Metrics.ReferenceAnalysis.PhaseValid = false;
            q = FactoryRangeReview.Evaluate(round);
            Require(q.WindowValid, "range phase does not depend on attenuation fit phase"); checks++;
            Array.Clear(round.Capture.VoltageRaw,0,round.Capture.VoltageRaw.Length);
            q=FactoryRangeReview.Evaluate(round);
            Require(!q.WindowValid,"absent measured voltage phase still rejects window");checks++;
            round.Kind = FactoryRunKind.Calibration;
            q = FactoryRangeReview.Evaluate(round);
            Require(q.WindowValid && q.LocationSampleCount == round.Capture.PdAdc.Length,
                "calibration quality always sees the complete long segment"); checks++;
            context = FactoryRangeReview.CreateContext(round, q, 0, false);
            Require(!context.HasRealPressurization && context.TargetVoltageV == 0,
                "calibration retry has no HV label or nonzero target"); checks++;

            var clipped = Round(10);
            clipped.Capture.PdAdc[1000] = 32767;
            q = FactoryRangeReview.Evaluate(clipped);
            context = FactoryRangeReview.CreateContext(clipped, q, 0, true);
            Require(RangeDecisionPolicy.IsMaximumRangeRisk(context), "maximum range clipping cannot suppress the risk warning"); checks++;
            Require(!FactoryRangeReview.CanSilentRetry(context, new HashSet<int>(), out reason),
                "maximum range clipping cannot retrigger automatically"); checks++;
            ignored = RangeDecisionPolicy.CreateRecord(context, RangeDecisionAction.Ignore);
            Require(!ignored.OriginalQualityPassed && ignored.QualitySnapshot.LocationClippedSamples == 1,
                "ignore retains clipping quality"); checks++;
            clipped.Completed = false;
            RequireThrows(() => FactoryRangeReview.Evaluate(clipped), "an incomplete hardware round cannot request a retry"); checks++;

            var calibration = CalibrationCapture();
            var c = FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8);
            Require(c.Valid && Math.Abs(c.CoefficientPcPerV / 2100 - 1) < .02,
                "100 capacitor route calibrates a 2100 pC/V incident-only input: " + c.Detail); checks++;
            Require(c.AveragePulseV.Length == 697 && c.CompleteWindowCount >= 3,
                "100 explicit 40 us capacitive envelope is averaged"); checks++;
            calibration.Overflow = true;
            Require(!FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8).Valid,
                "raw overflow cannot be hidden by calibration filtering"); checks++;
            calibration.Overflow = false;
            calibration.PdAdc[2000] = 32767;
            Require(!FactoryCalibrationAdapter.Calibrate(calibration, 100, 100, 8).Valid,
                "raw ADC clipping invalidates a coefficient"); checks++;
            calibration = CalibrationCapture();
            Require(!FactoryCalibrationAdapter.Calibrate(calibration, 100, 400, 8).Valid,
                "wrong calibration frequency cannot masquerade as periodic pulses"); checks++;

            var windows = new double[8][];
            for (int i = 0; i < windows.Length; i++)
            {
                windows[i] = new double[500];
                for (int j = 0; j < 200; j++) windows[i][j] = (j % 2 == 0 ? 1 : -1) * .0001;
                windows[i][400 + i % 2] = .05;
            }
            var assessment = CalibrationIncidentQuality.Analyze(windows, 200, 336, 464,
                100, .2, false, "factory-capacitor", null, false);
            Require(assessment.SaveRecommended && Math.Abs(assessment.CoefficientPcPerV - 4000) < 1e-6,
                "coefficient uses peak of the aligned average, not mean of separate maxima"); checks++;
            Require(Double.IsNaN(assessment.ReferenceDeviationFactor),
                "coefficient-only route does not use the old cable circuit amplitude reference"); checks++;
            foreach (double width in new[] { .3, 12.0 })
            {
                var same = CalibrationCapture(width);
                var known = FactoryCalibrationAdapter.Calibrate(same, 100, 100, 8);
                Require(known.Valid, "shared-filter calibration valid at pulse width " + width + ": " + known.Detail); checks++;
                same.Settings.PdCalibrationPcPerV = known.CoefficientPcPerV;
                same.VoltageRaw[0] = -.05;
                short originalCode = same.PdAdc[5000];
                double fullPeak, afterPeak;
                var detected = FactoryDetectedWaveforms.Build(same, out fullPeak, out afterPeak);
                double binsPeak = 0;
                foreach (double v in detected.PdEnvelopePc) binsPeak = Math.Max(binsPeak, v);
                Require(Math.Abs(afterPeak / 100 - 1) < .02 && Math.Abs(binsPeak / 100 - 1) < .02,
                    "same calibrated wide/narrow pulse recovers known Q with the pressure detector"); checks++;
                Require(detected.OutputCount == 8000 && detected.ConsumedPdSamples == same.PdAdc.Length,
                    "shared-filter detector includes complete 80k bins and all source samples"); checks++;
                Require(Math.Abs(detected.VoltageKv[0] + .55) < 1e-12 && same.PdAdc[5000] == originalCode,
                    "shared filtering preserves signed divider voltage and original ADC"); checks++;
            }
            return "PASS 100 range/calibration compatibility: " + checks + " checks (no hardware)."+
                Environment.NewLine+FactoryCalibrationDurationTests.Run();
        }

        private static FactoryResult Round(int range = 4)
        {
            var settings = new FactorySettings();
            settings.Input.RequestedPicoRateHz = 1000000;
            settings.Input.RangeIndex = range;
            var capture = new BenchCapture { Settings = settings.Input, Simulation = true,
                PicoFullScaleV = BenchSettings.Ranges[range], MaximumAdc = 32767,
                HardwareBits = PicoCaptureConfiguration.HardwareBits(14, range),
                PicoRateHz = 1000000, ArtRateHz = 80000,
                VoltageRaw = new double[settings.Input.ExpectedArtCount], PdAdc = new short[settings.Input.RequestedPicoCount],
                CapturedUtc = DateTime.UtcNow, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO", Runtime = "managed compatibility test" };
            return new FactoryResult { Settings = settings, Capture = capture, Kind = FactoryRunKind.Pressure,
                Completed = true, ResidualSafe = true };
        }
        internal static BenchCapture CalibrationCapture(double widthSamples = .3)
        {
            var r = Round();
            r.Settings.Input.CaptureDurationMs = 100;
            r.Capture.VoltageRaw = new double[r.Settings.Input.ExpectedArtCount];
            r.Capture.PdAdc = new short[r.Settings.Input.RequestedPicoCount];
            for (int i = 0; i < r.Capture.PdAdc.Length; i++) r.Capture.PdAdc[i] = (short)(i % 2 == 0 ? 2 : -2);
            double height = 100 / 2100.0 / r.Capture.PicoFullScaleV * r.Capture.MaximumAdc;
            for (int peak = 5000; peak < r.Capture.PdAdc.Length; peak += 10000)
                for (int j = -(int)Math.Ceiling(widthSamples * 5); j <= (int)Math.Ceiling(widthSamples * 5); j++)
                    r.Capture.PdAdc[peak + j] += (short)Math.Round(height * Math.Exp(-j * j / (2 * widthSamples * widthSamples)));
            return r.Capture;
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("100 compatibility: " + message); }
        private static void RequireThrows(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("100 compatibility: " + message);
        }
    }
}
