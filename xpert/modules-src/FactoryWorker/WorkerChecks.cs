using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class WorkerChecks
    {
        internal static async Task<int> Run(string root)
        {
            Directory.CreateDirectory(root);
            using (var writer = new StreamWriter(Path.Combine(root, "worker-tests.txt")))
            {
                Action<string,string> report = (key, detail) => writer.WriteLine(key + " " + detail);
                var pressure = new FactorySettings { Simulation = true, TargetKv = 5, MaximumKv = 5, MaximumFeedbackKv = 5.1, ReferenceDcKv = 5, ReferenceOscillationPeakKv = 5 };
                pressure.Input.RangeIndex = 4;
                pressure.Input.OscillationCircuitConnected = true;
                var capture = await Program.StartWorker(pressure, FactoryRunKind.Pressure, Path.Combine(root, "pressure"), report, CancellationToken.None);
                if (!capture.Completed || !capture.ResidualSafe || capture.Capture.PdAdc.Length != 7500000 || capture.Metrics.Detected.OutputCount != 4800)
                    throw new Exception("Simulation pressure worker metadata or complete counts failed.");
                writer.WriteLine("PRESSURE_WORKER=PASS; actual-native-count=7500000; detector=4800; no-hardware");
                if (!capture.Metrics.FrequencyValid || Math.Abs(capture.Metrics.FrequencyHz - 400) > 5 ||
                    !FactorySettings.Positive(capture.Metrics.CapacitanceNf) || !FactorySettings.Finite(capture.Metrics.EstimatedLossTangent) ||
                    capture.Metrics.PressureRatioValid || !capture.Metrics.DividerRatioValid)
                    throw new Exception("End-to-end oscillation metrics or independent ratio verification failed: " + capture.Metrics.Detail);
                if (capture.RampStatistics.ExitCode != "Reached" || capture.RampStatistics.Iterations != capture.Ramp.Count || capture.RampStatistics.ElapsedMilliseconds <= 0)
                    throw new Exception("Worker did not persist first-reach ramp statistics.");
                writer.WriteLine("OSCILLATION_METRICS=PASS; frequency=" + capture.Metrics.FrequencyHz + "; divider-ratio=PASS; pressure-ratio=UNVERIFIED (no stable tail in first-reach ramp); RAMP_STATISTICS=PASS");
                Program.Export(capture, Path.Combine(root, "pressure.csv"));
                if (File.ReadAllLines(Path.Combine(root, "pressure.csv")).Length != 4801) throw new Exception("80k CSV count mismatch.");
                var calibration = pressure.Clone(); calibration.TargetKv = 0;
                var candidate = await Program.StartWorker(calibration, FactoryRunKind.Calibration, Path.Combine(root, "calibration"), report, CancellationToken.None);
                if (!candidate.Completed || candidate.Calibration == null || !candidate.Calibration.Valid ||
                    candidate.Settings.Input.CaptureDurationMs != 100 || candidate.Capture.PdAdc.Length != 12500000 ||
                    Math.Abs(candidate.Calibration.CoefficientPcPerV - 2100) > 5)
                    throw new Exception("Simulation calibration worker candidate failed: " + (candidate.Calibration == null ? "none" : candidate.Calibration.Detail));
                writer.WriteLine("CALIBRATION_WORKER=PASS; 100ms; 12500000 native samples; candidate=" + candidate.Calibration.CoefficientPcPerV);
                var zeroSettings = pressure.Clone(); zeroSettings.TargetKv = 0; zeroSettings.FeedbackWiringConfirmed = false;
                var zero = await Program.StartWorker(zeroSettings,FactoryRunKind.Pressure,Path.Combine(root,"zero-input"),report,CancellationToken.None);
                if (!zero.Completed || !zero.ReadOnlyAcquisition || zero.ResidualSafe || !zero.CompleteCaptureAvailable() ||
                    zero.Capture.PdAdc.Length != 7500000 || zero.Metrics.Detected.OutputCount != 4800 || !zero.Capture.Runtime.Contains("NO_COMMON_TRIGGER"))
                    throw new Exception("0 kV worker must use input-only free-run branch, not HV state machine.");
                writer.WriteLine("ZERO_INPUT_WORKER=PASS; free-run; no output; no discharge or synchronization claim");
                var zeroTriggerSettings=zeroSettings.Clone();zeroTriggerSettings.ZeroSynchronizedOptical=true;
                var zeroTrigger=await Program.StartWorker(zeroTriggerSettings,FactoryRunKind.Pressure,Path.Combine(root,"zero-trigger"),report,CancellationToken.None);
                if(!zeroTrigger.Completed || !zeroTrigger.ZeroTriggerDiagnostic || zeroTrigger.ReadOnlyAcquisition || zeroTrigger.ResidualSafe ||
                    !zeroTrigger.CompleteCaptureAvailable() || !zeroTrigger.Capture.Runtime.Contains("ZERO_EXTERNAL_OPTICAL_DIAGNOSTIC"))
                    throw new Exception("Zero external trigger worker metadata failed.");
                writer.WriteLine("ZERO_TRIGGER_WORKER=PASS; external optical diagnostic; no HV/AO; no discharge claim");
                using (var stop = new CancellationTokenSource())
                {
                    var delayed = pressure.Clone(); delayed.Input.HvDelayMs = 5000;
                    stop.CancelAfter(1500);
                    bool rejected = false;
                    try { await Program.StartWorker(delayed, FactoryRunKind.Pressure, Path.Combine(root, "cancelled"), report, stop.Token); }
                    catch { rejected = true; }
                    if (!rejected || File.Exists(Path.Combine(root, "cancelled", "result.xml"))) throw new Exception("Cancel must not produce accepted result.");
                    if (!File.ReadAllText(Path.Combine(root,"cancelled","progress.log")).Contains("STEP_HV_ON_PASSED"))
                        throw new Exception("Cancel regression must reach simulated HV ON before cancellation.");
                    writer.WriteLine("SUPERVISED_CANCEL=PASS; no-accepted-result");
                }
                writer.WriteLine("HardwareAccessed=False; PhysicalAcceptance=False; WORKER_CHECKS=PASS");
            }
            return 0;
        }
    }
}
