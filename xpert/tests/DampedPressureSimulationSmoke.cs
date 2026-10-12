using System;
using System.Linq;
using Xiongxin.OWTS.Desktop;

internal static class DampedPressureSimulationSmoke
{
    static int checks;
    static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
    static int Main()
    {
        try
        {
            foreach (bool positive in new[] { true, false })
            foreach (int phase in new[] { 1, 2, 3 })
            {
                var data = DampedPressureSimulation.Create(11.9, 175.4, phase, positive);
                Check(data.Pulses.Count > 20, "Multiple discharges across cycles");
                Check(data.Discharge.Count(q => q != 0) == data.Pulses.Count, "One event set drives both displays");
                Check(data.Pulses.Max(p => p.Sample) < data.Discharge.Length / 2, "Discharges extinguish as voltage decays");
                var groups = data.Pulses.GroupBy(p => p.Cycle).OrderBy(p => p.Key).ToArray();
                for (int i = 1; i < groups.Length; i++)
                {
                    Check(groups[i].Count() <= groups[i - 1].Count(), "Cycle discharge count decreases");
                    Check(groups[i].Max(p => Math.Abs(p.Charge)) < groups[i - 1].Max(p => Math.Abs(p.Charge)), "Cycle discharge magnitude decreases");
                }
                var matrix = data.Matrix(175.4, phase);
                for (int i = 0; i < data.Pulses.Count; i++)
                {
                    var pulse = data.Pulses[i];
                    Check((pulse.PhaseDegrees > 0 && pulse.PhaseDegrees < 90) || (pulse.PhaseDegrees > 180 && pulse.PhaseDegrees < 270), "Only physical I/III phase windows");
                    Check(matrix[i, 0] == phase && matrix[i, 13] == pulse.Position && matrix[i, 16] == data.Discharge[pulse.Sample], "Phase, position and charge match event");
                    Check(matrix[i, 1] == pulse.Sample / DampedPressureSimulation.SampleRateHz && matrix[i, 2] == Math.Abs(data.Voltage[pulse.Sample]), "Same time and voltage in statistics");
                }
                int samplesPerCycle = (int)(DampedPressureSimulation.SampleRateHz / DampedPressureSimulation.FrequencyHz);
                for (int c = 1; c < 30; c++)
                    Check(Math.Abs(data.Voltage[c * samplesPerCycle + 100]) < Math.Abs(data.Voltage[(c - 1) * samplesPerCycle + 100]), "Voltage envelope damped");
            }
            bool rejected = false; try { DampedPressureSimulation.Create(double.NaN, 180, 1, true); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Reject invalid parameters");
            for (int phase = 1; phase <= 3; phase++)
            {
                var noise = DampedPressureSimulation.CreateZeroNoise(phase);
                Check(noise.Voltage.All(v => v == 0), "Reference generator is explicitly zero voltage");
                Check(noise.Discharge.Any(q => q != 0), "Reference retains a non-zero noise waveform");
                Check(noise.Pulses.Count == 0 && noise.MaximumCharge == 0 && noise.Matrix(175.4, phase).GetLength(0) == 0,
                    "Noise never creates PD events or reported maximum discharge");
                Check(noise.Discharge.SequenceEqual(DampedPressureSimulation.CreateZeroNoise(phase).Discharge), "Reference generator repeatable");
            }
            Console.WriteLine("DAMPED_PRESSURE_SIMULATION_PASS=" + checks); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
