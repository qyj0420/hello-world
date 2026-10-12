using System;
using System.Collections.Generic;
using System.Reflection;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryAcceptanceChecksTests
    {
        internal static string Run()
        {
            int checks = 0;
            var result = Result(false);
            var evidence = new FactoryAcceptanceEvidence { RatedMaximumKv = 28 };
            var evaluated = FactoryAcceptanceChecks.Evaluate(result, evidence);
            Require(Find(evaluated, "振荡频率").Status == FactoryAcceptanceStatus.DiagnosticOnly, "200 nF data remains a reference diagnostic"); checks++;
            Require(Find(evaluated, "DL/T 1932 标准负载").Status == FactoryAcceptanceStatus.DiagnosticOnly, "200 nF is not the 150±7.5 nF verification fixture"); checks++;
            Require(Find(evaluated, "本轮电压峰值误差").Status == FactoryAcceptanceStatus.Pending, "software divider settings cannot prove voltage accuracy"); checks++;
            Require(Find(evaluated, "外部电源绝缘耐压").Status == FactoryAcceptanceStatus.ExternalRecordRequired, "software capture cannot prove dielectric safety"); checks++;
            foreach (var check in evaluated) Require(!check.Name.Contains("定位"), "location is not silently added as a factory test"); checks++;

            evidence.LoadCapacitanceNf = 150; evidence.VoltageWaveformAt80PercentConfirmed = true;
            evidence.VoltageReferenceConfirmed = true; evidence.VoltageReferenceId = "STD-DIV-001";
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "振荡频率").Status == FactoryAcceptanceStatus.DiagnosticOnly, "current 150 nF input cannot requalify a frozen 200 nF capture"); checks++;
            result = Result(false, 150);
            evaluated = FactoryAcceptanceChecks.Evaluate(result, evidence);
            Require(Find(evaluated, "振荡频率").Status == FactoryAcceptanceStatus.Passed, "qualified 80% fixture checks real frequency against 30～500 Hz"); checks++;
            Require(Find(evaluated, "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Passed, "50% attenuation boundary inclusive"); checks++;
            result.Metrics.ReferenceAnalysis.MeasuredDecayPercent = 50.01;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Failed, "attenuation exceeding 50% fails"); checks++;
            result.Metrics.ReferenceAnalysis.CyclePeakKv = new double[8];
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Pending, "eight peaks are only seven cycles"); checks++;
            result.Metrics.ReferenceAnalysis.CyclePeakKv = new double[9]; result.Metrics.ReferenceAnalysis.MeasuredDecayPercent = 40;
            result.Metrics.ReferenceAnalysis.Valid = false;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Passed, "measured attenuation does not depend on known-C reverse-model validity"); checks++;
            result.Settings.ReferenceCapacitorEnabled = false;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Passed, "disabled reference capacitor leaves verified phase and measured attenuation available"); checks++;
            result.Metrics.ReferenceAnalysis.PhaseValid = false;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "前 8 周期幅值衰减").Status == FactoryAcceptanceStatus.Pending, "unreliable first-zero phase cannot prove eight-cycle attenuation"); checks++;
            result.Metrics.ReferenceAnalysis.PhaseValid = true;
            result.Metrics.FrequencyHz = 500.01;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "振荡频率").Status == FactoryAcceptanceStatus.Failed, "frequency greater than 500 Hz fails"); checks++;
            result.Metrics.FrequencyHz = 400;
            evidence.IndependentVoltageReferenceKv = 10; result.Metrics.OscillationPeakKv = 10.2;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "本轮电压峰值误差").Status == FactoryAcceptanceStatus.Passed, "independent single voltage point can pass 3% error"); checks++;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Pending, "single point cannot prove full-range accuracy"); checks++;
            result.Metrics.OscillationPeakKv = 10.31;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "本轮电压峰值误差").Status == FactoryAcceptanceStatus.Failed, "independent voltage error above 3% fails"); checks++;
            result.Metrics.OscillationPeakKv = 10.2;
            result.Capture.VoltageRaw[0] = 10;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "本轮电压峰值误差").Status == FactoryAcceptanceStatus.Pending, "initial clipped ART overshoot cannot become a valid peak error despite a verified unclipped tail fit"); checks++;
            Throws(() => FactoryAcceptanceChecks.AddVoltageReading(evidence, result, 10, "STD-DIV-001"), "clipped initial ART overshoot must not enter voltage verification history"); checks++;
            result.Capture.VoltageRaw[0] = -9.95;
            Throws(() => FactoryAcceptanceChecks.AddVoltageReading(evidence, result, 10, "STD-DIV-001"), "negative ART rail boundary is equally invalid"); checks++;
            result.Capture.VoltageRaw[0] = 0;
            result.Settings.TargetKv = 0;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "本轮电压峰值误差").Status == FactoryAcceptanceStatus.Pending, "zero-voltage/background result is not a relative oscillation-peak accuracy test"); checks++;
            Throws(() => FactoryAcceptanceChecks.AddVoltageReading(evidence, result, 10, "STD-DIV-001"), "zero target cannot populate the high-voltage accuracy series"); checks++;
            result.Settings.TargetKv = 22.4;

            var synthetic = Result(true);
            Require(Find(FactoryAcceptanceChecks.Evaluate(synthetic, evidence), "振荡频率").Status == FactoryAcceptanceStatus.DiagnosticOnly, "simulation does not become real standard proof"); checks++;

            evidence.PdReferenceConfirmed = true; evidence.PdReferenceId = "STD-Q-001";
            result.Kind = FactoryRunKind.Calibration;
            result.Calibration = new CalibrationResult { Valid = true, MeanPeakV = .01, CoefficientPcPerV = 10000,
                Detail=FactoryCalibrationAdapter.Method+"。离线夹具" };
            result.Capture.Settings.PdCalibrationPcPerV = 2100;
            FactoryChargeVerificationReading reading = FactoryAcceptanceChecks.AddChargeReading(evidence, result, 100, "STD-Q-001");
            Require(reading.MeasuredPc == 21 && reading.AppliedCoefficientPcPerV == 2100, "verification uses previously applied coefficient, not same-capture new candidate 10000"); checks++;
            Throws(() => FactoryAcceptanceChecks.AddChargeReading(evidence, result, 100, "STD-Q-001"), "same capture is never five independent measurements"); checks++;
            result.Completed = false;
            Throws(() => FactoryAcceptanceChecks.AddChargeReading(evidence, result, 100, "STD-Q-001"), "failed/unfinished records cannot count toward verification"); checks++;
            result.Completed = true;

            evidence.ChargeReadings.Clear();
            for (int i = 0; i < 5; i++) evidence.ChargeReadings.Add(Charge(100, 100 + i, i.ToString(), result));
            FactoryAcceptanceCheck q = Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档");
            Require(q.Status == FactoryAcceptanceStatus.Passed, "five fixed-coefficient readings use spread/reference, not CV"); checks++;
            evidence.ChargeReadings[4].MeasuredPc = 105;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Failed, "5% stability is not better than 5%"); checks++;
            foreach (var entry in evidence.ChargeReadings) entry.MeasuredPc = 110;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Passed, "10% measurement-error boundary inclusive"); checks++;
            foreach (var entry in evidence.ChargeReadings) entry.MeasuredPc = 110.01;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Failed, "relative charge error above 10% fails despite excellent repeatability"); checks++;
            evidence.ChargeReadings[4].AppliedCoefficientPcPerV = 2200;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "changing coefficient during repeat series invalidates stability proof"); checks++;
            evidence.ChargeReadings[4].AppliedCoefficientPcPerV = 2100;
            evidence.ChargeReadings[4].PicoSerial = "OTHER-PICO";
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "records from another card cannot complete current-device verification"); checks++;
            evidence.ChargeReadings[4].PicoSerial = result.Capture.PicoSerial;
            foreach (var entry in evidence.ChargeReadings) entry.MeasuredPc = 100;
            foreach (var entry in evidence.ChargeReadings) entry.Conditions.LoadCapacitanceNf = 200;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.DiagnosticOnly, "five historical 200 nF readings remain diagnostic after current UI and current capture use 150 nF"); checks++;
            foreach (var entry in evidence.ChargeReadings) entry.Conditions.LoadCapacitanceNf = 150;
            var savedConditions = evidence.ChargeReadings[4].Conditions;
            evidence.ChargeReadings[4].Conditions = new FactoryVerificationConditions();
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.DiagnosticOnly, "legacy missing-condition reading cannot complete formal verification"); checks++;
            evidence.ChargeReadings[4].Conditions = savedConditions;
            savedConditions.PicoCoupling = result.Capture.Settings.PicoCoupling == 1 ? 0 : 1;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix AC and DC coupling"); checks++;
            savedConditions.PicoCoupling = result.Capture.Settings.PicoCoupling;
            savedConditions.PicoBandwidth20MHz = !result.Capture.Settings.PicoBandwidth20MHz;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix hardware bandwidth conditions"); checks++;
            savedConditions.PicoBandwidth20MHz = result.Capture.Settings.PicoBandwidth20MHz;
            savedConditions.PicoActualRateHz *= 2;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix actual sampling rates"); checks++;
            savedConditions.PicoActualRateHz /= 2;
            savedConditions.PicoRequestedRateHz *= 2;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix requested sampling rates"); checks++;
            savedConditions.PicoRequestedRateHz /= 2;
            savedConditions.ResolutionBits = 15;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix resolution modes"); checks++;
            savedConditions.ResolutionBits = result.Capture.Settings.ResolutionBits;
            savedConditions.HardwareBits--;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence must retain range-specific hardware resolution evidence"); checks++;
            savedConditions.HardwareBits = result.Capture.HardwareBits;
            savedConditions.PdFilterProtocol = "UNKNOWN/NO-HIGH-PASS";
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix detector/high-pass protocols"); checks++;
            savedConditions.PdFilterProtocol = FactoryDetectedWaveforms.PdAlgorithm;
            savedConditions.CalibrationProtocol = "LEGACY-RAW-PEAK";
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "five-repeat sequence cannot mix coefficient peak definitions"); checks++;
            savedConditions.CalibrationProtocol = FactoryCalibrationAdapter.Method;
            foreach(int legacyTarget in new[]{0,-1,73})
            {
                savedConditions.CalibrationPulseCount=legacyTarget;
                Require(Find(FactoryAcceptanceChecks.Evaluate(result,evidence),"100 pC 电量档").Status==FactoryAcceptanceStatus.Passed,
                    "current time-segment protocol ignores unused legacy target metadata in a fixed-coefficient series");checks++;
            }
            savedConditions.CalibrationPulseCount=result.Settings.CalibrationPulseCount;
            var oldA=Conditions(result);var oldB=Conditions(result);
            oldA.CalibrationProtocol=oldB.CalibrationProtocol=FactoryCalibrationAdapter.LegacyFixedCountMethod;
            var recorded=typeof(FactoryAcceptanceChecks).GetMethod("ConditionsRecorded",BindingFlags.Static|BindingFlags.NonPublic);
            var same=typeof(FactoryAcceptanceChecks).GetMethod("SameChargeChain",BindingFlags.Static|BindingFlags.NonPublic);
            Require((bool)recorded.Invoke(null,new object[]{oldA}) && (bool)same.Invoke(null,new object[]{oldA,oldB}),
                "historical fixed-count protocol keeps its original valid matching count semantics");checks++;
            oldB.CalibrationPulseCount=oldA.CalibrationPulseCount+1;
            Require(!(bool)same.Invoke(null,new object[]{oldA,oldB}),"historical fixed-count protocols still reject mixed target counts");checks++;
            oldB.CalibrationPulseCount=0;
            Require(!(bool)recorded.Invoke(null,new object[]{oldB}),"historical fixed-count conditions still require the original minimum count");checks++;
            result.Capture.Settings.PdCalibrationPcPerV = 2200;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "historical old coefficient series cannot certify a different current applied coefficient"); checks++;
            result.Capture.Settings.PdCalibrationPcPerV = 2100;
            result.Completed = false;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "100 pC 电量档").Status == FactoryAcceptanceStatus.Pending, "rejected current result cannot attach historical identity proof to unfinished capture"); checks++;
            result.Completed = true;

            evidence.SensitivitySequenceConfirmed = true; evidence.MinimumDetectablePc = 10; evidence.BackgroundNoisePc = 1;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "局放测量灵敏度").Status == FactoryAcceptanceStatus.Failed, "sensitivity must be better than 10 pC"); checks++;
            evidence.MinimumDetectablePc = 9;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "局放测量灵敏度").Status == FactoryAcceptanceStatus.Passed, "sub-10 pC fixture sensitivity can pass with independent descending sequence"); checks++;
            evidence.BackgroundNoisePc = 2;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "局放测量灵敏度").Status == FactoryAcceptanceStatus.Pending, "2 pC background does not meet background<2 pC condition"); checks++;

            evidence.VoltageRangeCoverageConfirmed = true;
            foreach (double fraction in new[] { .2, .4, .6, .8, 1.0 })
                evidence.VoltageReadings.Add(new FactoryVoltageVerificationReading { AcquisitionId = "V" + fraction, NominalKv = 28 * fraction,
                    MeasuredKv = 28 * fraction * 1.02, ReferenceKv = 28 * fraction, IndependentReferenceConfirmed = true,
                    ReferenceId = "STD-DIV-001", ArtSerial = result.Capture.ArtSerial, PicoSerial = result.Capture.PicoSerial, Conditions = Conditions(result) });
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Passed, "five distinct evenly spread external voltage points cover declared maximum"); checks++;
            foreach (var point in evidence.VoltageReadings) point.Conditions.LoadCapacitanceNf = 200;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.DiagnosticOnly, "historical voltage points keep their original 200 nF load qualification"); checks++;
            foreach (var point in evidence.VoltageReadings) point.Conditions.LoadCapacitanceNf = 150;
            var voltageConditions = evidence.VoltageReadings[4].Conditions;
            evidence.VoltageReadings[4].Conditions = new FactoryVerificationConditions();
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.DiagnosticOnly, "voltage history without frozen conditions cannot prove standard coverage"); checks++;
            evidence.VoltageReadings[4].Conditions = voltageConditions;
            voltageConditions.RawDividerWithinRange = false;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Pending, "saturated historical voltage peak is not an effective range point"); checks++;
            voltageConditions.RawDividerWithinRange = true;
            voltageConditions.DividerVPerV *= 2;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Pending, "full-range voltage history cannot mix divider gains"); checks++;
            voltageConditions.DividerVPerV /= 2;
            voltageConditions.ArtTerminal = 10106;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Pending, "full-range voltage history cannot mix ART input reference configurations"); checks++;
            voltageConditions.ArtTerminal = result.Capture.Settings.ArtTerminal;
            evidence.VoltageReadings[4].ReferenceKv *= .8;
            Require(Find(FactoryAcceptanceChecks.Evaluate(result, evidence), "电压全范围 5 点误差").Status == FactoryAcceptanceStatus.Failed, "one failed voltage point fails five-point conclusion"); checks++;
            checks+=TestRecordedCalibrationProtocols();
            return "Factory standard evidence: " + checks + " offline checks passed; no hardware or complete-standard certificate generated.";
        }

        private static int TestRecordedCalibrationProtocols()
        {
            int checks=0;
            foreach(string protocol in new[]{FactoryCalibrationAdapter.Method,FactoryCalibrationAdapter.LegacyFixedCountMethod,""})
            {
                var evidence=new FactoryAcceptanceEvidence {PdReferenceConfirmed=true,PdReferenceId="STD-Q-001"};
                FactoryResult result=null;
                for(int i=0;i<5;i++)
                {
                    result=Result(false,150);result.Kind=FactoryRunKind.Calibration;
                    result.Capture.CapturedUtc=new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc).AddSeconds(i);
                    string detail=protocol==""?"历史记录没有可辨认的方法前缀":protocol+"。原保存算法说明";
                    result.Calibration=new CalibrationResult {Valid=true,MeanPeakV=100.0/2100,
                        CoefficientPcPerV=2100,Detail=detail};
                    var reading=FactoryAcceptanceChecks.AddChargeReading(evidence,result,100,"STD-Q-001");
                    Require(reading.Conditions.CalibrationProtocol==protocol,
                        "re-entered charge evidence preserves the actual new/old/unknown saved method");checks++;
                    Require(result.Calibration.Detail==detail && result.Calibration.MeanPeakV==100.0/2100,
                        "provenance extraction never rewrites historical calibration evidence");checks++;
                }
                var status=Find(FactoryAcceptanceChecks.Evaluate(result,evidence),"100 pC 电量档").Status;
                Require(status==(protocol==""?FactoryAcceptanceStatus.DiagnosticOnly:FactoryAcceptanceStatus.Passed),
                    "known historical algorithms retain their own series; unknown method cannot become qualified new evidence");checks++;
            }
            foreach(string missing in new string[]{null,"",FactoryCalibrationAdapter.Method+"-unrecognized"})
            {
                var result=Result(false,150);result.Kind=FactoryRunKind.Calibration;
                result.Calibration=new CalibrationResult {Valid=true,MeanPeakV=.01,CoefficientPcPerV=10000,Detail=missing};
                var reading=FactoryAcceptanceChecks.AddChargeReading(new FactoryAcceptanceEvidence {
                    PdReferenceConfirmed=true,PdReferenceId="STD-Q-001"},result,100,"STD-Q-001");
                Require(reading.Conditions.CalibrationProtocol=="","missing or near-match method prefixes stay unknown");checks++;
            }
            return checks;
        }

        private static FactoryResult Result(bool simulation, double loadNf = 200)
        {
            var input = new BenchSettings { RealHardware = !simulation, RequestedPicoRateHz = 125000, CaptureDurationMs = 1,
                RangeIndex = 5, ArtSerial = "ART-OFFLINE-TEST", PicoSerial = "PICO-OFFLINE-TEST", PdCalibrationPcPerV = 2100 };
            var capture = new BenchCapture { Settings = input, VoltageRaw = new double[80], PdAdc = new short[125], ArtRateHz = 80000,
                PicoRateHz = 125000, MaximumAdc = 32767, PicoFullScaleV = .5, HardwareBits = 14, Simulation = simulation,
                ArtSerial = simulation ? "SIM-ART" : input.ArtSerial, PicoSerial = simulation ? "SIM-PICO" : input.PicoSerial,
                CapturedUtc = DateTime.UtcNow, Runtime = "OFFLINE-EVIDENCE-EVALUATOR-FIXTURE-NO-NATIVE-CALLS" };
            capture.ValidateCapture();
            return new FactoryResult { Settings = new FactorySettings { Input = input, Simulation = simulation, TargetKv = 22.4,
                    VerificationContextRecorded = true, VerificationLoadCapacitanceNf = loadNf, VerificationRatedMaximumKv = 28, VerificationAt80PercentConfirmed = true },
                Capture = capture, Kind = FactoryRunKind.Pressure, Completed = true, ResidualSafe = true,
                Metrics = new FactoryMetrics { FrequencyValid = true, FrequencyHz = 400, OscillationPeakKv = 10,
                    ReferenceAnalysis = new FactoryReferenceResult { Valid = true, PhaseValid = true, DecayValid = true, RequestedCycles = 8,
                        CyclePeakKv = new double[9], MeasuredDecayPercent = 50 } } };
        }
        private static FactoryChargeVerificationReading Charge(double known, double measured, string id, FactoryResult result)
        { return new FactoryChargeVerificationReading { KnownPc = known, MeasuredPc = measured, AppliedCoefficientPcPerV = 2100,
            AcquisitionId = id, ReferenceId = "STD-Q-001", IndependentReferenceConfirmed = true,
            ArtSerial = result.Capture.ArtSerial, PicoSerial = result.Capture.PicoSerial, Conditions = Conditions(result) }; }
        private static FactoryVerificationConditions Conditions(FactoryResult result)
        {
            var input = result.Capture.Settings;
            return new FactoryVerificationConditions { Recorded = result.Settings.VerificationContextRecorded,
                LoadCapacitanceNf = result.Settings.VerificationLoadCapacitanceNf, RatedMaximumKv = result.Settings.VerificationRatedMaximumKv,
                At80PercentConfirmed = result.Settings.VerificationAt80PercentConfirmed, PicoRequestedRateHz = input.RequestedPicoRateHz,
                PicoActualRateHz = result.Capture.PicoRateHz, ResolutionBits = input.ResolutionBits, HardwareBits = result.Capture.HardwareBits,
                PicoCoupling = input.PicoCoupling, PicoBandwidth20MHz = input.PicoBandwidth20MHz, DividerVPerV = input.DividerVPerV,
                ArtRateHz = result.Capture.ArtRateHz, ArtTerminal = input.ArtTerminal, VoltagePeakProtocol = FactoryAcceptanceChecks.VoltagePeakProtocol,
                CalibrationFrequencyHz = result.Settings.CalibrationFrequencyHz, CalibrationPulseCount = result.Settings.CalibrationPulseCount,
                PdFilterProtocol = FactoryDetectedWaveforms.PdAlgorithm, CalibrationProtocol = FactoryCalibrationAdapter.Method, RawDividerWithinRange = true };
        }
        private static FactoryAcceptanceCheck Find(List<FactoryAcceptanceCheck> checks, string name)
        { foreach (var check in checks) if (check.Name == name) return check; throw new InvalidOperationException("Missing check " + name); }
        private static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException("Standard evidence check failed: " + text); }
        private static void Throws(Action action, string text)
        { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Expected rejection: " + text); }
    }
}
