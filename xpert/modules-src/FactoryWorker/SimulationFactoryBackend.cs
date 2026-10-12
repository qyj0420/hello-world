using System;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Pure software signal generator. No native device APIs or output task exists.
    internal sealed class SimulationFactoryBackend : IFactoryBackend, IFactoryFaultMonitor, IFactoryOpticalRecovery, IFactoryFinalAi4Observation
    {
        private readonly IFactoryClock clock;
        private FactorySettings settings;
        private bool hv, optical, armed, free, discharged;
        private bool opticalRetryUsed;
        private double ao, chargedV;
        private long captureStart = Int64.MaxValue;
        internal SimulationFactoryBackend(IFactoryClock clock) { this.clock = clock; }
        public void Initialize(FactorySettings s, bool calibration) { settings = s.Clone(); }
        public bool ReadEmergencyReleased() { return true; }
        public FactoryFinalAi4Observation ReadFinalAi4Observation(System.Threading.CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(hv || optical || ao!=0 || armed)throw new InvalidOperationException("仿真末轮观察前输出/采集尚未收尾。");
            DateTime start=DateTime.UtcNow;
            clock.Pause(10,token);
            return FactoryFinalAi4Observation.Evaluate(new double[800],80000,settings.Input.DividerVPerV,true,start,DateTime.UtcNow);
        }
        public FactoryFeedback ReadFeedback()
        {
            double v = hv ? ao * settings.PressureVPerV : discharged ? 0 : chargedV;
            var f = new FactoryFeedback { TimestampUtc = DateTime.UtcNow, AoV = ao, EmergencyReleased = true,
                VmonRawV = v / (settings.VmonFullScaleKv * 1000) * settings.FeedbackFullScaleV,
                DividerRawV = v / settings.Input.DividerVPerV,
                ImonRawV = hv && ao > 0 ? .02 : 0 };
            // Constant synthetic block exercises the same median calculation;
            // it is not a real sampled waveform or a hardware latency model.
            var block = new double[settings.FeedbackWindowMs * 3];
            for (int i = 0; i < settings.FeedbackWindowMs; i++)
            { block[i*3] = f.ImonRawV; block[i*3+1] = f.VmonRawV; block[i*3+2] = f.DividerRawV; }
            f.SampleCount = settings.FeedbackWindowMs;
            f.ImonMinimumRawV = f.ImonMaximumRawV = f.ImonRawV;
            f.VmonMinimumRawV = f.VmonMaximumRawV = f.VmonRawV;
            f.DividerMinimumRawV = f.DividerMaximumRawV = f.DividerRawV;
            FactoryFeedbackWindow.Apply(f, block, settings.FeedbackWindowMs, settings.FeedbackWindowMs);
            return f;
        }
        // Synthetic capacitor holds its charge until the simulated optical discharge.
        // Zeroing VSET changes the source command, not the stored capacitor peak.
        public void WriteAo(double volts) { settings.ValidateAoCommand(volts); ao = volts; if (hv && volts > 0) chargedV = ao * settings.PressureVPerV; }
        public void SetHv(bool on) { hv = on; }
        public void SetOptical(bool on)
        { optical = on; if (on) { if (!armed) throw new InvalidOperationException(); captureStart = clock.ElapsedMilliseconds; discharged = true; } }
        public void Arm(bool freeRun)
        { free = freeRun; armed = true; if (free) captureStart = clock.ElapsedMilliseconds; }
        public void ArmBeforeShutdown()
        { if (!hv || optical || armed) throw new InvalidOperationException("仿真切换状态错误。"); Arm(false); }
        public bool ArtComplete { get { return armed && clock.ElapsedMilliseconds - captureStart >= settings.Input.CaptureDurationMs; } }
        public bool PicoComplete { get { return ArtComplete; } }
        public BenchCapture ReadCapture()
        {
            if (!ArtComplete || !PicoComplete || hv || optical || ao != 0) throw new InvalidOperationException("仿真段尚未完成。");
            var c = new BenchCapture { Settings = settings.Input, Simulation = true, ArtSerial = "SIM-ART", PicoSerial = "SIM-PICO",
                CapturedUtc = DateTime.UtcNow, Runtime = "SIMULATION ONLY · synthetic RLC and calibration pulse · no hardware access",
                PicoRateHz = settings.Input.RequestedPicoRateHz, ArtRateHz = BenchSettings.ArtRate,
                PicoFullScaleV = BenchSettings.Ranges[settings.Input.RangeIndex], MaximumAdc = 32767,
                HardwareBits = PicoCaptureConfiguration.HardwareBits(settings.Input.ResolutionBits, settings.Input.RangeIndex),
                VoltageRaw = new double[settings.Input.ExpectedArtCount], PdAdc = new short[settings.Input.RequestedPicoCount] };
            var random = new Random(42);
            for (int i = 0; i < c.VoltageRaw.Length; i++)
            {
                double t = i / c.ArtRateHz-(free?0:settings.Input.PreTriggerDurationMs/1000);
                c.VoltageRaw[i] = free ? 0 : chargedV / settings.Input.DividerVPerV * (t<0?1:Math.Exp(-25 * t) * Math.Cos(2 * Math.PI * 400 * t));
                c.VoltageRaw[i] += (random.NextDouble() - .5) * .00003;
            }
            double peak = settings.Input.CalibrationPulsePc / settings.Input.PdCalibrationPcPerV;
            double frequency = free ? settings.CalibrationFrequencyHz : 400;
            double origin = 2.0 / frequency;
            for (int i = 0; i < c.PdAdc.Length; i++)
            {
                double t = i / c.PicoRateHz-(free?0:settings.Input.PreTriggerDurationMs/1000);
                double phase = (t - origin) * frequency;
                double nearest = Math.Round(phase);
                double distance = Math.Abs(phase - nearest) / frequency;
                double v = nearest >= 0 && distance < .000002 ? peak * Math.Exp(-distance * distance / (2 * 2e-7 * 2e-7)) : 0;
                v += (random.NextDouble() - .5) * .00004;
                double code = v * c.MaximumAdc / c.PicoFullScaleV;
                if (Math.Abs(code) >= c.MaximumAdc) c.Overflow = true;
                c.PdAdc[i] = (short)Math.Max(-c.MaximumAdc, Math.Min(c.MaximumAdc, Math.Round(code)));
            }
            return c;
        }
        public void StopCapture() { armed = false; }
        public void PrepareOpticalRetry()
        {
            if(!settings.UseOpticalTimeoutRetry || opticalRetryUsed || hv || optical || ao!=0 || !armed || free || ArtComplete || PicoComplete)
                throw new InvalidOperationException("仿真补发状态错误。");
            opticalRetryUsed=true; StopCapture(); captureStart=Int64.MaxValue; Arm(false);
        }
        public void PrepareFaultMonitoring() { StopCapture(); }
        public FactoryFeedback ReadFaultFeedback() { return ReadFeedback(); }
        public void Shutdown() { hv = false; optical = false; ao = 0; }
        public void Dispose() { Shutdown(); }
    }
}
