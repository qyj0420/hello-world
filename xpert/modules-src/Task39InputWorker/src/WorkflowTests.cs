using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    internal static class WorkflowTests
    {
        internal static string Run()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", "dynamic_" + Guid.NewGuid().ToString("N"));
            var settings = new BenchSettings { RequestedPicoRateHz = 25000000, ResolutionBits = 15, CaptureDurationMs = 8, RangeIndex = 4 };
            settings.Validate();
            var c = BenchRunner.Simulate(settings, directory, (stage, message) => { }, CancellationToken.None);
            c.ValidateCapture();
            if (c.PdAdc.Length != 200000 || c.VoltageRaw.Length != 640 || c.HardwareBits != 15) throw new Exception("Dynamic simulator ignores requested parameters.");
            var location = SignalAnalysis.CopyLocation20Ms(c.PdAdc, c.PicoRateHz);
            if (location.Length != c.PdAdc.Length) throw new Exception("Short record location not bounded to capture length.");
            var d = DetectedWaveforms.Build(c);
            if (d.OutputCount != 640 || d.Truncated) throw new Exception("Dynamic detected data truncated or padded.");
            var a = new BenchAnalysisRecord { Discharge = SignalAnalysis.AnalyzePd(c.PdAdc, c.PicoRateHz, c.MaximumAdc, c.PicoFullScaleV, settings.PdCalibrationPcPerV) };
            MainForm.Export(c, a, location, Path.Combine(directory, "csv"));
            if (File.ReadLines(Path.Combine(directory, "csv", "synchronized_detected_80k.csv")).Count() != 641 ||
                File.ReadLines(Path.Combine(directory, "csv", "location_0_20ms.csv")).Count() != 200001) throw new Exception("Short capture export uses legacy 60/20ms constants.");
            return "WORKFLOW_TESTS=PASS: 25MS/s 15bit 8ms simulated acquisition, raw roundtrip, 640-point detector, bounded location and CSV; no hardware accessed.";
        }
    }
}
