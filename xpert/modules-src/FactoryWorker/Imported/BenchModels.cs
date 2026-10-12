using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    [Serializable]
    public sealed class BenchSettings
    {
        public bool RealHardware;
        // Missing from older XML means input-only, never implicitly enable an output.
        public bool AutomaticSharedTrigger;
        // Retain this serialized field for exact historical record metadata.
        // New runs use the fixed optical hold; historical widths remain replayable.
        public int TriggerPulseMs = OpticalHoldMilliseconds;
        public const int OpticalHoldMilliseconds = 1000;
        private const int MaximumHistoricalTriggerPulseMilliseconds = 1500;
        public const double MaximumNewCaptureDurationMs = 60;
        public string ArtDevice = "Dev1";
        public string ArtSerial = "";
        public string PicoSerial = "";
        public string ArtLibraryDirectory = @"C:\Program Files (x86)\ART Technology\ART-DAQ\Lib\x64";
        public string PicoLibraryDirectory = "auto";
        public int ArtTerminal = 10083;
        public string ArtTrigger = "/Dev1/PFI0";
        public bool FallingTrigger;
        public double ExternalTriggerVolts = 1.5;
        public double RequestedPicoRateHz = 125000000;
        public int ResolutionBits = 14;
        public double CaptureDurationMs = 60;
        // Missing from historical XML means the original all-post-trigger capture.
        public int PreTriggerPercent;
        [XmlIgnore] public int ArtPreTriggerCount { get { return (int)((long)ExpectedArtCount * PreTriggerPercent / 100); } }
        [XmlIgnore] public int PicoPreTriggerCount { get { return (int)((long)RequestedPicoCount * PreTriggerPercent / 100); } }
        [XmlIgnore] public double PreTriggerDurationMs { get { return ArtPreTriggerCount * 1000.0 / ArtRate; } }
        public bool PicoChannelAEnabled = true;
        public int PicoCoupling = 1;
        public bool PicoBandwidth20MHz;
        // Offset remains fixed until absolute-voltage conversion is implemented.
        public double PicoAnalogOffsetVolts;
        public int RangeIndex = 10;
        public double PdCalibrationPcPerV = 2100;
        public double DividerVPerV = 11000;
        public double InductanceH = .7;
        public double ResistanceOhms = 22;
        public double TargetVoltageV;
        public double AoStepV = .01;
        public int LoopDelayMs = 1;
        public int HvDelayMs = 50;
        public int ArmDelayMs = 200;
        public int TriggerTimeoutMs = 5000;
        public double TransientExclusionMs = .5;
        public double CalibrationPulsePc = 100;
        public bool AutoRange = true;
        public bool SimulationOscillation;
        public bool OscillationCircuitConnected;
        public int RunNumber;
        public const double PicoRate = 125000000;
        public const double ArtRate = 80000;
        public const double DurationSeconds = .060;
        public const int PicoBits = 14;
        public const int PicoCount = 7500000;
        public const int ArtCount = 4800;
        public const int MaximumPicoSamples = 50000000;
        public static readonly double[] Ranges = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 20 };
        [XmlIgnore] public double CaptureDurationSeconds { get { return CaptureDurationMs / 1000.0; } }
        [XmlIgnore] public int ExpectedArtCount { get { return SampleCount(ArtRate, CaptureDurationMs, 80000); } }
        [XmlIgnore] public int RequestedPicoCount { get { return SampleCount(RequestedPicoRateHz, CaptureDurationMs, MaximumPicoSamples); } }

        public void Validate()
        {
            if (TargetVoltageV != 0) throw new ArgumentException("本程序当前接线限定为 0 V 输入采集；不允许非零设定。");
            if (RangeIndex < 0 || RangeIndex >= Ranges.Length) throw new ArgumentException("局放量程索引无效。");
            if (!PicoChannelAEnabled) throw new ArgumentException("本工具只采集 Pico A；请启用 A 通道后开始，B 通道不会代替采集。");
            if (PicoCoupling != 0 && PicoCoupling != 1) throw new ArgumentException("Pico A 耦合只能选择 AC 或 DC。");
            if (!Finite(PicoAnalogOffsetVolts) || PicoAnalogOffsetVolts != 0) throw new ArgumentException("本工具的 Pico A 模拟偏置固定为 0 V。");
            if (!Finite(CaptureDurationMs) || CaptureDurationMs < 1 || CaptureDurationMs > 1000)
                throw new ArgumentException("采集长度须为 1～1000 ms；同时受 Pico 单次 5000 万点上限限制。");
            PicoCaptureConfiguration.TimebaseFor(ResolutionBits, RequestedPicoRateHz);
            int artSamples = ExpectedArtCount, picoSamples = RequestedPicoCount;
            if(PreTriggerPercent<0 || PreTriggerPercent>100)throw new ArgumentException("触发前比例必须在0～100%之间。");
            // Both cards must represent the same physical interval, never rounded independently.
            if((long)artSamples*PreTriggerPercent%100!=0 || (long)picoSamples*PreTriggerPercent%100!=0)
                throw new ArgumentException("采集位置须与两卡完整样点对齐，请使用整数毫秒采集长度。");
            RequireAlignedSampleCount(ArtRate, CaptureDurationMs);
            RequireAlignedSampleCount(RequestedPicoRateHz, CaptureDurationMs);
            if (!Positive(PdCalibrationPcPerV) || !Positive(DividerVPerV) || !Positive(InductanceH) ||
                !Finite(ResistanceOhms) || ResistanceOhms < 0) throw new ArgumentException("换算和回路参数无效。");
            if (ArtTerminal != 10083 && ArtTerminal != 10078 && ArtTerminal != 10106) throw new ArgumentException("ART 输入参考方式无效。");
            if (String.IsNullOrWhiteSpace(ArtDevice) || ArtDevice.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new ArgumentException("ART 模块名无效。");
            string prefix = "/" + ArtDevice + "/PFI";
            int port;
            if (ArtTrigger == null || !ArtTrigger.StartsWith(prefix, StringComparison.Ordinal) ||
                !Int32.TryParse(ArtTrigger.Substring(prefix.Length), out port) || port < 0 || port > 3) throw new ArgumentException("数字触发端须为当前 ART 模块的 PFI0～3。");
            if (!Finite(ExternalTriggerVolts) || ExternalTriggerVolts <= -5 || ExternalTriggerVolts >= 5) throw new ArgumentException("Pico EXT 触发阈值须在 ±5 V 内。");
            if (TriggerPulseMs < 1 || TriggerPulseMs > MaximumHistoricalTriggerPulseMilliseconds) throw new ArgumentException("TRG 脉冲记录宽度无效。");
            if (AutomaticSharedTrigger && (ArtTrigger != "/" + ArtDevice + "/PFI0" || FallingTrigger || ExternalTriggerVolts <= 0))
                throw new ArgumentException("P0.7 共享触发限定 PFI0 / Pico EXT 上升沿，EXT 阈值必须为正；不得改写其他端口。");
            if (HvDelayMs < 50 || HvDelayMs > 5000 || ArmDelayMs < 200 || ArmDelayMs > 5000 ||
                TriggerTimeoutMs < 100 || TriggerTimeoutMs > 60000 || LoopDelayMs < 1 || LoopDelayMs > 1000) throw new ArgumentException("延时参数超出允许范围。");
            if (TriggerTimeoutMs <= CaptureDurationMs) throw new ArgumentException("触发等待超时必须大于整段采集长度。");
            if (!Finite(TransientExclusionMs) || TransientExclusionMs < 0 || TransientExclusionMs >= 20) throw new ArgumentException("过冲排除窗口须在 0～20 ms 之间。");
            if (TransientExclusionMs >= CaptureDurationMs)
                throw new ArgumentException("过冲排除窗口必须小于本轮采集长度，须保留真实数据用于局放基线和峰值分析。");
            if (!Positive(CalibrationPulsePc) || !Positive(AoStepV) || AoStepV > 0.1) throw new ArgumentException("脉冲电量或模拟步长参数无效。");
            if (RealHardware && (String.IsNullOrWhiteSpace(ArtSerial) || String.IsNullOrWhiteSpace(PicoSerial)))
                throw new ArgumentException("实机采集须先获取并选择本次 ART 和 Pico 的准确序列号，不能换用其他设备。");
        }
        public void ValidateForNewRun()
        {
            ValidateNewCaptureDuration();
            Validate();
            if (AutomaticSharedTrigger && TriggerPulseMs != OpticalHoldMilliseconds)
                throw new ArgumentException("新采集的光触发固定持续 " + OpticalHoldMilliseconds + " ms；旧记录的不同宽度只用于回放。");
            if (AutomaticSharedTrigger && TriggerTimeoutMs <= Math.Max(OpticalHoldMilliseconds, CaptureDurationMs))
                throw new ArgumentException("触发超时必须大于光触发保持 " + OpticalHoldMilliseconds + " ms 与采集段长度中的较大值；发光持续时间不改变采集长度。");
        }
        public void ValidateNewCaptureDuration()
        {
            // Existing records retain their original 1..1000 ms validation and
            // sample counts. Only new device requests have the 60 ms hard cap.
            if(!Finite(CaptureDurationMs) || CaptureDurationMs<1 || CaptureDurationMs>MaximumNewCaptureDurationMs)
                throw new ArgumentException("新采集单段长度须为 1～60 ms；超过60 ms的旧记录仅可读取，不能直接用于新采集。不会自动缩短、扩展或拼接采集段。");
        }
        private static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        private static bool Positive(double v) { return Finite(v) && v > 0; }
        private static int SampleCount(double rate, double durationMs, int maximum)
        {
            double value = Math.Round(rate * durationMs / 1000.0, MidpointRounding.AwayFromZero);
            if (!Finite(value) || value < 1 || value > maximum)
                throw new ArgumentException("采样率 × 采集长度超出单次缓冲区上限（Pico 最多 50,000,000 点）。");
            return checked((int)value);
        }
        private static void RequireAlignedSampleCount(double rate, double durationMs)
        {
            double samples = rate * durationMs / 1000.0;
            // Only forgive floating-point arithmetic noise, never a physical partial
            // sample at either end of the shared capture interval. At the 50 M sample
            // limit the absolute tolerance remains much less than one ADC sample.
            double tolerance = Math.Max(1e-7, Math.Abs(samples) * 1e-12);
            if (Math.Abs(samples - Math.Round(samples, MidpointRounding.AwayFromZero)) > tolerance)
                throw new ArgumentException("采集长度须匹配 80 kS/s 完整点数且与 Pico 时基对齐；建议整数毫秒或两卡均为整数点数的时长，不能静默舍入共同边界。");
        }
        public void Save(string path) { using (var s = File.Create(path)) new XmlSerializer(typeof(BenchSettings)).Serialize(s, this); }
        public static BenchSettings Load(string path) { using (var s = File.OpenRead(path)) return (BenchSettings)new XmlSerializer(typeof(BenchSettings)).Deserialize(s); }
    }

    public sealed class BenchCapture
    {
        public BenchSettings Settings;
        public double[] VoltageRaw = new double[0];
        public short[] PdAdc = new short[0];
        public double ArtRateHz = BenchSettings.ArtRate;
        // Historical BENCH-1 records used fixed +/-10 V. BENCH-2 explicitly
        // records the committed divider range, without changing sample bytes.
        public double ArtFullScaleV = 10;
        public double PicoRateHz = BenchSettings.PicoRate;
        public int MaximumAdc = 32767;
        public double PicoFullScaleV;
        public int HardwareBits;
        public bool Overflow;
        public bool Simulation;
        public string ArtSerial = "";
        public string PicoSerial = "";
        public string Runtime = "";
        public DateTime CapturedUtc;
        public string DirectoryPath = "";

        public void ValidateCapture()
        {
            if (Settings == null) throw new InvalidDataException("缺少本轮采集设置。");
            Settings.Validate();
            if(!Finite(ArtFullScaleV)||ArtFullScaleV<=0||ArtFullScaleV>10)throw new InvalidDataException("ART输入量程元数据无效。");
            if (Simulation == Settings.RealHardware) throw new InvalidDataException("采集来源标识与设置不一致。");
            if (VoltageRaw == null || VoltageRaw.Length != Settings.ExpectedArtCount ||
                PdAdc == null || PdAdc.Length != Settings.RequestedPicoCount)
                throw new InvalidDataException("完整记录点数与本轮设置不符：ART 应为 " + Settings.ExpectedArtCount + "，Pico 应为 " + Settings.RequestedPicoCount + "。");
            if (!Finite(ArtRateHz) || ArtRateHz <= 0 || Math.Abs(ArtRateHz - BenchSettings.ArtRate) > .5 ||
                !PicoCaptureConfiguration.RateMatches(PicoRateHz, Settings.RequestedPicoRateHz))
                throw new InvalidDataException("回读采样率不满足 ART 80 kS/s 或本轮 Pico 请求；不接受静默降采样或虚假时基。");
            if (Math.Abs(VoltageRaw.Length / ArtRateHz - Settings.CaptureDurationSeconds) > .501 / ArtRateHz + 1e-9 ||
                Math.Abs(PdAdc.Length / PicoRateHz - Settings.CaptureDurationSeconds) > .501 / PicoRateHz + 1e-9)
                throw new InvalidDataException("采集长度与本轮设置不符；仅允许半个采样点的点数舍入。");
            if (MaximumAdc <= 0 || MaximumAdc > 32767 || !Finite(PicoFullScaleV) ||
                Math.Abs(PicoFullScaleV - BenchSettings.Ranges[Settings.RangeIndex]) > 1e-12)
                throw new InvalidDataException("Pico ADC 上限或本轮量程元数据无效。");
            int expectedBits = PicoCaptureConfiguration.HardwareBits(Settings.ResolutionBits, Settings.RangeIndex);
            if (HardwareBits != expectedBits) throw new InvalidDataException("当前分辨率模式的量程规格位数不匹配。");
            if (CapturedUtc.Kind != DateTimeKind.Utc || CapturedUtc == DateTime.MinValue || String.IsNullOrWhiteSpace(Runtime))
                throw new InvalidDataException("采集时间或运行库来源元数据无效。");
            if (Simulation)
            {
                if (ArtSerial != "SIM-ART" || PicoSerial != "SIM-PICO") throw new InvalidDataException("仿真设备标识无效。");
            }
            else if (!String.Equals(ArtSerial, Settings.ArtSerial.Trim(), StringComparison.Ordinal) ||
                !String.Equals(PicoSerial, Settings.PicoSerial.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException("记录的 ART/Pico 身份与本轮锁定序列号不一致。");
            foreach (double value in VoltageRaw) if (!Finite(value)) throw new InvalidDataException("电压记录含非有限数值。");
        }

        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }

        public void Save(string directory)
        {
            ValidateCapture();
            Directory.CreateDirectory(directory);
            Settings.Save(Path.Combine(directory, "settings.xml"));
            string finalPath = Path.Combine(directory, "capture.bin");
            string pendingPath = finalPath + ".pending";
            using (var w = new BinaryWriter(File.Create(pendingPath)))
            {
                bool rangeMetadata=ArtFullScaleV!=10;
                w.Write(rangeMetadata?"XIONGXIN-ZERO-BENCH-2":"XIONGXIN-ZERO-BENCH-1"); w.Write(Simulation); w.Write(CapturedUtc.ToBinary());
                w.Write(ArtSerial); w.Write(PicoSerial); w.Write(Runtime ?? "");
                w.Write(ArtRateHz); w.Write(PicoRateHz); w.Write(MaximumAdc); w.Write(PicoFullScaleV); w.Write(HardwareBits); w.Write(Overflow);
                if(rangeMetadata)w.Write(ArtFullScaleV);
                w.Write(VoltageRaw.Length); WriteSamples(w, VoltageRaw, sizeof(double));
                w.Write(PdAdc.Length); WriteSamples(w, PdAdc, sizeof(short));
            }
            if (File.Exists(finalPath)) File.Replace(pendingPath, finalPath, null); else File.Move(pendingPath, finalPath);
        }
        public static BenchCapture Load(string directory)
        {
            var c = new BenchCapture { Settings = BenchSettings.Load(Path.Combine(directory, "settings.xml")), DirectoryPath = directory };
            c.Settings.Validate(); // Bound allocation before trusting file counts.
            using (var r = new BinaryReader(File.OpenRead(Path.Combine(directory, "capture.bin"))))
            {
                string format=r.ReadString();
                if (format != "XIONGXIN-ZERO-BENCH-1"&&format!="XIONGXIN-ZERO-BENCH-2") throw new InvalidDataException("采集文件标识无效。");
                c.Simulation = r.ReadBoolean(); c.CapturedUtc = DateTime.FromBinary(r.ReadInt64());
                c.ArtSerial = r.ReadString(); c.PicoSerial = r.ReadString(); c.Runtime = r.ReadString();
                c.ArtRateHz = r.ReadDouble(); c.PicoRateHz = r.ReadDouble(); c.MaximumAdc = r.ReadInt32();
                c.PicoFullScaleV = r.ReadDouble(); c.HardwareBits = r.ReadInt32(); c.Overflow = r.ReadBoolean();
                if(format=="XIONGXIN-ZERO-BENCH-2")c.ArtFullScaleV=r.ReadDouble();
                int n = r.ReadInt32(); if (n != c.Settings.ExpectedArtCount) throw new InvalidDataException("ART 数据与本轮采集长度不符。");
                RequirePayload(r, n, sizeof(double));
                c.VoltageRaw = new double[n]; ReadSamples(r, c.VoltageRaw, sizeof(double));
                n = r.ReadInt32(); if (n != c.Settings.RequestedPicoCount) throw new InvalidDataException("Pico 数据与本轮采集长度不符。");
                RequirePayload(r, n, sizeof(short));
                c.PdAdc = new short[n]; ReadSamples(r, c.PdAdc, sizeof(short));
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("采集文件含未声明的尾部数据。");
            }
            c.ValidateCapture();
            return c;
        }

        // Preserve BENCH-1 primitive little-endian bytes exactly. Only the IO
        // granularity changes; no conversion, decimation or extra full-size copy.
        private static void WriteSamples(BinaryWriter writer, Array values, int width)
        {
            int total = checked(values.Length * width);
            byte[] buffer = new byte[Math.Min(65536, total)];
            for (int offset = 0; offset < total; )
            {
                int count = Math.Min(buffer.Length, total - offset);
                Buffer.BlockCopy(values, offset, buffer, 0, count);
                if (!BitConverter.IsLittleEndian) ReverseElements(buffer, count, width);
                writer.Write(buffer, 0, count);
                offset += count;
            }
        }
        private static void RequirePayload(BinaryReader reader, int count, int width)
        {
            if (checked((long)count * width) > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new EndOfStreamException("采集样点数据不完整。");
        }
        private static void ReadSamples(BinaryReader reader, Array values, int width)
        {
            int total = checked(values.Length * width);
            byte[] buffer = new byte[Math.Min(65536, total)];
            for (int offset = 0; offset < total; )
            {
                int count = Math.Min(buffer.Length, total - offset), filled = 0;
                while (filled < count)
                {
                    int read = reader.Read(buffer, filled, count - filled);
                    if (read == 0) throw new EndOfStreamException("采集样点数据不完整。");
                    filled += read;
                }
                if (!BitConverter.IsLittleEndian) ReverseElements(buffer, count, width);
                Buffer.BlockCopy(buffer, 0, values, offset, count);
                offset += count;
            }
        }
        private static void ReverseElements(byte[] buffer, int count, int width)
        {
            for (int offset = 0; offset < count; offset += width) Array.Reverse(buffer, offset, width);
        }
    }
}
