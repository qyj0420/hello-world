using System;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace Xiongxin.Scope
{
    public class ChannelSettings { public bool Enabled = true; public int Range = 6; public int Coupling = 1; public bool Limit20MHz; }
    public class Settings
    {
        public bool Simulation = true;
        public bool CalibrationPulse;
        public string LibraryPath = "", Serial = "";
        public ChannelSettings A = new ChannelSettings(), B = new ChannelSettings { Enabled = false };
        public int Resolution = 2, TriggerMode, TriggerSource, TriggerEdge = 2, AutoMs = 500, TimeoutMs = 3000;
        public double RateMS = 125, DurationMs = 60, PrePercent = 10, ThresholdV = .05;
        public uint DelaySamples;
        public string SimFault = "正常";
        public static readonly double[] Ranges = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 20 };
        public static readonly int[] Bits = { 8, 12, 14, 15, 16 };
        public void Validate()
        {
            if (CalibrationPulse && TriggerMode != 1) throw new ArgumentException("脉冲校准须使用单次触发采集。");
            if (A == null || B == null || !A.Enabled && !B.Enabled) throw new ArgumentException("至少启用一个通道。");
            foreach (var c in new[] { A, B }) if (c.Range < 0 || c.Range >= Ranges.Length || c.Coupling < 0 || c.Coupling > 1) throw new ArgumentException("通道设置无效。");
            if (Resolution < 0 || Resolution > 4 || !Finite(RateMS) || RateMS < .001 || RateMS > 1000 || !Finite(DurationMs) || DurationMs < .01 || DurationMs > 1000)
                throw new ArgumentException("分辨率、采样率或采集时长无效。");
            if (!Finite(PrePercent) || PrePercent < 0 || PrePercent > 90 || TimeoutMs < 100 || TimeoutMs > 60000 || DurationMs >= TimeoutMs)
                throw new ArgumentException("预触发比例须为 0～90%；采集超时须大于单段时长。");
            if (TriggerMode < 0 || TriggerMode > 2 || TriggerSource != 0 && TriggerSource != 1 && TriggerSource != 4 || TriggerEdge < 2 || TriggerEdge > 4)
                throw new ArgumentException("触发配置无效。");
            if (TriggerMode != 0)
            {
                if (TriggerSource == 0 && !A.Enabled || TriggerSource == 1 && !B.Enabled) throw new ArgumentException("触发通道必须启用。");
                double limit = TriggerSource == 4 ? 5 : Ranges[(TriggerSource == 0 ? A : B).Range];
                if (!Finite(ThresholdV) || Math.Abs(ThresholdV) >= limit) throw new ArgumentException("触发阈值必须在触发输入量程内；EXT 使用 ±5 V 阈值标度。");
                if (TriggerMode == 2 && (AutoMs < 1 || AutoMs > 32767 || AutoMs + DurationMs >= TimeoutMs)) throw new ArgumentException("自动触发时间加采集时长必须小于采集超时。");
                if (DelaySamples > 0 && PrePercent > 0) throw new ArgumentException("本工具中延迟采集与预触发互斥：使用触发延迟时请将预触发设为 0%。");
                if (DelaySamples / (RateMS * 1000) + DurationMs >= TimeoutMs) throw new ArgumentException("触发延迟与采集时长超过超时预算。");
            }
        }
        public static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        public int Count(double intervalNs)
        {
            if (!Finite(intervalNs) || intervalNs <= 0) throw new ArgumentException("采样间隔无效。");
            HVX.Acquisition.PicoCaptureCadence.RequireDuration(DurationMs);
            double n = Math.Ceiling(DurationMs * 1000000 / intervalNs);
            // At the 60 ms ceiling, rounding up one hardware sample must not
            // create a longer block. This is a pre-acquisition sample budget.
            if (n * intervalNs > 60000000.0) n = Math.Floor(60000000.0 / intervalNs);
            while(n>=2 && n/(1e9/intervalNs)*1000.0>60) n--; // Preserve the strict bound through floating-point rate conversion.
            if (n < 2 || n > 20000000) throw new ArgumentException("单通道单段点数须在 2～2000 万之间，请调整时长或采样率。");
            HVX.Acquisition.PicoCaptureCadence.RequireSamples((int)n, 1e9 / intervalNs);
            return (int)n;
        }
    }
    public class Command { public string Op, DataPath, TracePath; public Settings Config; }
    public class Reply
    {
        public bool Ok = true, Connected, Faulted;
        public string Message = "", Serial = "", Model = "", Driver = "", Library = "", DataPath = "";
        public string CaptureStage = "";
        public int Samples, PreSamples, Resolution;
        public uint Timebase;
        public double IntervalNs;
        public short MaxAdc, Overflow;
        public Settings Config;
    }
    public class Capture { public Reply Info; public short[] A, B; }
    public static class Wire
    {
        public static string Encode<T>(T obj) { using (var w = new StringWriter()) { new XmlSerializer(typeof(T)).Serialize(w, obj); return Convert.ToBase64String(Encoding.UTF8.GetBytes(w.ToString())); } }
        public static T Decode<T>(string s) { using (var r = new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String(s)))) return (T)new XmlSerializer(typeof(T)).Deserialize(r); }
        public static void Save(Capture c, string path)
        {
            using (var w = new BinaryWriter(new FileStream(path, FileMode.CreateNew)))
            {
                w.Write(0x58585331); w.Write(Encode(c.Info));
                foreach (var a in new[] { c.A, c.B }) { w.Write(a == null ? 0 : a.Length); if (a != null) { byte[] b = new byte[a.Length * 2]; Buffer.BlockCopy(a, 0, b, 0, b.Length); w.Write(b); } }
            }
        }
        public static Capture Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (r.ReadInt32() != 0x58585331) throw new IOException("波形文件格式错误。");
                var c = new Capture { Info = Decode<Reply>(r.ReadString()) };
                for (int ch = 0; ch < 2; ch++) { int n = r.ReadInt32(); if (n < 0 || n > 20000000) throw new IOException("波形点数错误。"); if (n == 0) continue; byte[] b = r.ReadBytes(n * 2); if (b.Length != n * 2) throw new IOException("波形数据不完整。"); var a = new short[n]; Buffer.BlockCopy(b, 0, a, 0, b.Length); if (ch == 0) c.A = a; else c.B = a; }
                if(c.Info==null || c.Info.Config==null || c.Info.Samples<2 || c.Info.Samples>20000000 || c.Info.MaxAdc<=0 || !Settings.Finite(c.Info.IntervalNs) || c.Info.IntervalNs<=0)throw new IOException("波形元数据无效。");
                c.Info.Config.Validate();
                if((c.A!=null)!=(c.Info.Config.A.Enabled) || (c.B!=null)!=(c.Info.Config.B.Enabled) || c.A!=null&&c.A.Length!=c.Info.Samples || c.B!=null&&c.B.Length!=c.Info.Samples || r.BaseStream.Position!=r.BaseStream.Length)throw new IOException("波形点数或通道与元数据不一致。");
                return c;
            }
        }
    }
}
