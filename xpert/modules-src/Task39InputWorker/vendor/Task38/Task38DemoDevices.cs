using System;
using System.Threading;

namespace HVX.OWTS.Task38
{
    // Explicit simulation backend: contains no vendor API calls.
    public sealed class Task38DemoFactory : ITask38DeviceFactory
    {
        public volatile bool Connected = true;
        public volatile bool SafetyReady = true;
        public double ResidualVoltageV;
        public double SignalPeakVolts = 0.04;
        public int? RandomSeed;
        private int captureSequence;
        public int InitializationDelayMilliseconds;
        public double MaximumAoCommandVolts;
        public int EnergizeCount;
        public int CreatedArtCount;
        public string PicoSerial = "SIM-5242D";
        public string ArtSerial = "SIM-USB3135";
        public string LastRequestedPicoSerial;
        private DemoBus bus;
        public IArtUsb3135Device CreateArt()
        {
            Interlocked.Increment(ref CreatedArtCount);
            bus = new DemoBus();
            return new DemoArt(this, bus);
        }
        public IPicoScope5242DBlockDevice CreatePico() { return new DemoPico(this, bus); }
        internal void Check() { if (!Connected) throw new InvalidOperationException("仿真设备通讯已断开"); }
        public static Task38Profile CreateProfile()
        {
            var profile = new Task38Profile();
            profile.Hardware.TargetVoltageV = 10000;
            profile.Hardware.HardOverCurrentA = .015;
            profile.Hardware.RampStepAoVolts = .025;
            return profile;
        }

        private sealed class DemoBus { public readonly ManualResetEvent Trigger = new ManualResetEvent(false); }
        private sealed class DemoArt : IArtUsb3135Device, IDeviceIdentity
        {
            public string DeviceSerial { get { return owner.ArtSerial; } }
            public string IdentityDiagnostic { get { return String.Empty; } }
            private readonly object gate = new object();
            private readonly Task38DemoFactory owner;
            private readonly DemoBus bus;
            private Task38Configuration cfg;
            private bool hv, ground, armed;
            private double ao, peak;
            public DemoArt(Task38DemoFactory f, DemoBus b) { owner = f; bus = b; }
            public void Initialize(Task38Configuration c)
            {
                if (owner.InitializationDelayMilliseconds > 0) Thread.Sleep(owner.InitializationDelayMilliseconds);
                owner.Check(); cfg = c.Clone(); ground = true;
            }
            public bool ReadSafetyReady() { owner.Check(); return owner.SafetyReady; }
            public FeedbackSample ReadFeedback()
            {
                owner.Check();
                lock (gate)
                {
                    double v = hv ? ao * cfg.VoltageGainVPerAoVolt : owner.ResidualVoltageV;
                    peak = Math.Max(peak, v);
                    return new FeedbackSample { TimestampUtc = DateTime.UtcNow, DividerVoltageV = v,
                        SourceVoltageV = v, DividerFeedbackRawV = v / cfg.DividerRatioVPerV,
                        CurrentA = hv ? cfg.CurrentLimitA * 0.3 : 0 };
                }
            }
            public void WriteAoVolts(double v)
            {
                owner.Check(); lock (gate) { ao = v; owner.MaximumAoCommandVolts = Math.Max(owner.MaximumAoCommandVolts, v); }
            }
            public void SetDigitalOutputs(bool h, bool l, bool c, bool g)
            {
                owner.Check(); lock (gate)
                {
                    if (h && !hv) Interlocked.Increment(ref owner.EnergizeCount);
                    if (!ground && g) bus.Trigger.Set();
                    ground = g; hv = h;
                }
            }
            public void ForceSourceOff() { owner.Check(); lock (gate) { hv = false; ao = 0; } }
            public void StopMonitoringInputs() { owner.Check(); }
            public void RestartMonitoringInputs() { owner.Check(); }
            public void ArmFiniteDividerCapture() { owner.Check(); armed = true; bus.Trigger.Reset(); }
            public double[] ReadFiniteDividerCapture(int timeout)
            {
                if (!bus.Trigger.WaitOne(timeout)) throw new TimeoutException("仿真触发未到达");
                owner.Check();
                double[] v = new double[cfg.ArtFiniteSampleCount];
                for (int i = 0; i < v.Length; i++)
                {
                    double t = i / cfg.ArtFiniteSampleRateHz - 0.002;
                    v[i] = peak / cfg.DividerRatioVPerV * (t < 0 ? 1 : Math.Exp(-60 * t) * Math.Cos(2 * Math.PI * 300 * t));
                }
                armed = false; return v;
            }
            public void AbortFiniteDividerCapture() { armed = false; }
            public bool FiniteCaptureArmed { get { return armed; } }
            public void Dispose() { lock (gate) { hv = false; ao = 0; } }
        }
        private sealed class DemoPico : IPicoScope5242DBlockDevice, IDeviceConnectionProbe
        {
            private readonly Task38DemoFactory owner;
            private readonly DemoBus bus;
            private Task38Configuration cfg;
            private bool armed;
            private int range;
            public DemoPico(Task38DemoFactory f, DemoBus b) { owner = f; bus = b; }
            public void Initialize(Task38Configuration c) { owner.Check(); owner.LastRequestedPicoSerial = c.PicoSerial; cfg = c.Clone(); }
            public string CheckConnection() { owner.Check(); return owner.PicoSerial; }
            public void ArmBlockCapture(int r) { owner.Check(); range = r; armed = true; }
            public PicoBlockCapture ReadBlockCapture(int timeout)
            {
                if (!bus.Trigger.WaitOne(timeout)) throw new TimeoutException("仿真触发未到达");
                owner.Check();
                short[] v = new short[cfg.PicoSampleCount];
                double full = cfg.PicoRangeFullScaleVolts[range];
                int sequence = Interlocked.Increment(ref owner.captureSequence);
                Random random = new Random(unchecked((owner.RandomSeed ?? Environment.TickCount) + sequence * 7919));
                bool overflow = false;
                for (int i = 0; i < v.Length; i++)
                {
                    double t = i / cfg.PicoSampleRateHz;
                    double u = 0.00001 * Math.Sin(i * 0.917);
                    if (cfg.TargetVoltageV > 0 && t >= 0.002 && t < 0.0022) u += 30 * Math.Exp(-(t - 0.002) * 25000) * Math.Cos(i * 0.8);
                    if (Math.Abs(u) >= full) overflow = true;
                    v[i] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(u / full * 32767)));
                }
                // Fresh random bipolar PD pulses and smaller delayed reflections each shot.
                // Keep the switching transient separate from the post-zero PD assessment.
                double duration = v.Length / cfg.PicoSampleRateHz;
                int pulses = Math.Max(1, (int)(duration * (650 + random.Next(650))));
                for (int n = 0; cfg.TargetVoltageV > 0 && n < pulses && duration > .00402; n++)
                {
                    double at = .004 + random.NextDouble() * (duration - .00402);
                    double amplitude = owner.SignalPeakVolts * (.25 + .75 * random.NextDouble()) * (random.Next(2) == 0 ? -1 : 1);
                    double frequency = 2000000 + random.NextDouble() * 3000000;
                    AddPulse(v, at, amplitude, frequency, full, ref overflow);
                    AddPulse(v, at + .000002 + random.NextDouble() * .000004,
                        amplitude * (.15 + .25 * random.NextDouble()), frequency, full, ref overflow);
                }
                armed = false;
                return new PicoBlockCapture { RawAdc = v, SampleCount = v.Length, MaximumAdc = 32767,
                    ResolutionModeBits = cfg.PicoResolutionBits, RangeHardwareBits = PicoResolutionInfo.HardwareBits(cfg.PicoResolutionBits, range),
                    FullScaleVolts = full, SampleIntervalNanoseconds = 8, HardwareOverflow = overflow,
                    Variant = "5242D DEMO", Serial = "SIM-5242D" };
            }
            private void AddPulse(short[] values, double start, double amplitude, double frequency, double full, ref bool overflow)
            {
                int first = (int)(start * cfg.PicoSampleRateHz);
                int length = (int)(.000006 * cfg.PicoSampleRateHz);
                for (int j = 0; j < length && first + j < values.Length; j++)
                {
                    double t = j / cfg.PicoSampleRateHz;
                    double adc = values[first + j] + amplitude * Math.Exp(-t * 900000) *
                        Math.Cos(t * 2 * Math.PI * frequency) / full * 32767;
                    if (Math.Abs(adc) >= 32767) overflow = true;
                    values[first + j] = (short)Math.Max(-32767, Math.Min(32767, Math.Round(adc)));
                }
            }
            public void AbortBlockCapture() { armed = false; }
            public bool BlockCaptureArmed { get { return armed; } }
            public void Dispose() { armed = false; }
        }
    }
}
