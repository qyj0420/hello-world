using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Globalization;

namespace Xiongxin.ZeroBench
{
    internal static class ExportTests
    {
        internal static string Run()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification", "export_" + Guid.NewGuid().ToString("N"));
            var capture = BenchRunner.Simulate(new BenchSettings { RangeIndex = 4 }, directory, (a, b) => { }, CancellationToken.None);
            var analysis = new BenchAnalysisRecord { Source = "SIMULATION / 仿真", Discharge = SignalAnalysis.AnalyzePd(capture.PdAdc, capture.PicoRateHz, capture.MaximumAdc, capture.PicoFullScaleV, 2100) };
            var window = SignalAnalysis.CopyLocation20Ms(capture.PdAdc, capture.PicoRateHz);
            string output = Path.Combine(directory, "csv");
            MainForm.Export(capture, analysis, window, output);
            if (Directory.Exists(output + ".pending")) throw new Exception("Completed export remains pending.");
            if (File.ReadLines(Path.Combine(output, "voltage_raw.csv")).Count() != 4801) throw new Exception("Voltage CSV incomplete.");
            string[] detected = File.ReadAllLines(Path.Combine(output, "synchronized_detected_80k.csv"));
            if (detected.Length != 4801 || Math.Abs(Double.Parse(detected[4800].Split(',')[0], CultureInfo.InvariantCulture) - 59.9875) > 1e-9)
                throw new Exception("Detected 80k CSV count/timing incorrect.");
            if (!detected.Skip(1).Any(line => Double.Parse(line.Split(',')[2], CultureInfo.InvariantCulture) > 90)) throw new Exception("Detector lost calibration peaks.");
            if (File.ReadLines(Path.Combine(output, "location_0_20ms.csv")).Count() != 2500001) throw new Exception("Location CSV incomplete.");
            string[] pulses = File.ReadAllLines(Path.Combine(output, "pd_pulse_candidates.csv"));
            if (pulses.Length != analysis.Discharge.PulseCandidateCount + 1 || pulses.Skip(1).Any(s => !s.EndsWith(",simulation")))
                throw new Exception("Candidate count or data source mismatch.");
            for (int i = 1; i < pulses.Length; i++)
            {
                double time = Double.Parse(pulses[i].Split(',')[0], CultureInfo.InvariantCulture);
                if (Math.Abs(time - (5 + (i - 1) * 10)) > .002) throw new Exception("Pulse times must cover all 60ms without integer overflow.");
            }
            string last = File.ReadLines(Path.Combine(output, "location_0_20ms.csv")).Last();
            if (Math.Abs(Double.Parse(last.Split(',')[0], CultureInfo.InvariantCulture) - 19.999992) > 1e-10 || !last.EndsWith(",simulation")) throw new Exception("Wrong trigger-referenced CSV duration or source.");
            return "EXPORT_TESTS=PASS: 4800 voltage points, 2500000 location points, pulse count, source tags, settings and completed-directory transaction; simulation only.";
        }
    }
}
