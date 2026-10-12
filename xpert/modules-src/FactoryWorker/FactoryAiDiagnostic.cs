using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal interface IFactoryAiReadOnly : IDisposable
    {
        string Initialize(FactorySettings settings);
        double[] ReadBlock(out bool emergencyReleased);
    }
    internal static class FactoryAiDiagnostic
    {
        internal static void Validate(FactorySettings s)
        {
            if (s == null) throw new ArgumentNullException("s");
            FactoryP04Diagnostic.Validate(s.Input);
            if (s.Input.ArtTerminal != 10083 || !FactorySettings.Positive(s.MaximumKv) || !FactorySettings.Positive(s.VmonFullScaleKv) ||
                !FactorySettings.Positive(s.FeedbackFullScaleV) || !FactorySettings.Positive(s.Input.DividerVPerV))
                throw new ArgumentException("AI 只读诊断须使用已确认 RSE/AGND 及有效换算参数。");
            new FactoryArtFeedbackPlan(s);
        }
        internal static string Run(FactorySettings s, IFactoryAiReadOnly native, string csv,
            Action<string,string> report, CancellationToken token)
        {
            Validate(s); if (native == null) throw new ArgumentNullException("native");
            var text = new StringBuilder();
            Action<string> log = value => { text.AppendLine(value); if (report != null) report("AI_DIAG",value); };
            double[] sums = new double[3], low = { Double.PositiveInfinity, Double.PositiveInfinity, Double.PositiveInfinity },
                high = { Double.NegativeInfinity, Double.NegativeInfinity, Double.NegativeInfinity };
            int total = 0, highCount = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                log("只读 AI0 / AI2 / AI4 与 P0.4，保存约1秒原始数据。没有 AO/DO、HV、光触发、设备复位或 Pico 打开；不接管已有输出。");
                log("请勿与 DMC 或其他设备程序同时占用 ART。结果不作为加压许可，不自动写入零点补偿。");
                log(new FactoryArtFeedbackPlan(s).Describe(s)); log(native.Initialize(s));
                using (var writer = new StreamWriter(csv,false,new UTF8Encoding(true)))
                {
                    writer.WriteLine("time_ms,AI0_IMON_raw_mV,AI2_VMON_raw_mV,AI4_divider_raw_mV,P04_block_read");
                    for (int block = 0; block < 10; block++)
                    {
                        token.ThrowIfCancellationRequested(); bool emergency;
                        double[] raw = native.ReadBlock(out emergency);
                        if (raw == null || raw.Length != 300) throw new InvalidDataException("AI 只读诊断缺少三通道100点完整组。");
                        if (emergency) highCount++;
                        for (int i = 0; i < 100; i++)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                double value = raw[i * 3 + c];
                                if (!FactorySettings.Finite(value)) throw new InvalidDataException("AI 诊断原始值非有限。");
                                sums[c] += value; low[c] = Math.Min(low[c],value); high[c] = Math.Max(high[c],value);
                            }
                            writer.WriteLine(String.Format(CultureInfo.InvariantCulture,"{0:F3},{1:R},{2:R},{3:R},{4}",
                                total * 1000.0 / FactoryArtFeedbackPlan.RateHz,raw[i * 3] * 1000,raw[i * 3 + 1] * 1000,raw[i * 3 + 2] * 1000,emergency ? 1 : 0));
                            total++;
                        }
                        log("#" + (block + 1) + " 最新 AI0/AI2/AI4=" + (raw[297] * 1000).ToString("F3") + " / " +
                            (raw[298] * 1000).ToString("F3") + " / " + (raw[299] * 1000).ToString("F3") + " mV；P0.4=" + (emergency ? "高" : "低") + "（组末独立读取，非逐点同步）。");
                    }
                }
                string[] names = { "AI0 IMON", "AI2 VMON", "AI4 分压器" };
                for (int c = 0; c < 3; c++) log(names[c] + "：均值 " + (sums[c] / total * 1000).ToString("F3") +
                    " mV；最小 " + (low[c] * 1000).ToString("F3") + " mV；最大 " + (high[c] * 1000).ToString("F3") + " mV。");
                log("P0.4 高 " + highCount + "/10组；原始CSV：" + csv + "。均值只作诊断，不替代峰值保护或放电确认。");
            }
            finally { native.Dispose(); }
            return text.ToString();
        }
    }
    internal sealed class FactoryAiReadOnlyNative : IFactoryAiReadOnly
    {
        private readonly P04ReadOnlyNative digital = new P04ReadOnlyNative();
        private IntPtr ai; private bool started;
        public string Initialize(FactorySettings s)
        {
            string runtime = digital.Load(s.Input.ArtLibraryDirectory);
            string identity = digital.Identity(s.Input.ArtDevice);
            if (identity != "USB3135 / " + s.Input.ArtSerial) throw new InvalidOperationException("ART 型号或序列号不符，停止 AI 只读诊断。");
            digital.OpenLine(s.Input.ArtDevice,0);
            InputOnlyArtNative.Check(InputOnlyArtNative.CreateTask("",out ai),"创建三通道 AI 只读诊断");
            if (ai == IntPtr.Zero) throw new InvalidOperationException("AI 诊断句柄为空。");
            FactoryArtFeedbackNative.Configure(ai,s,InputOnlyArtNative.Check);
            started = true; InputOnlyArtNative.Check(InputOnlyArtNative.StartTask(ai),"启动 AI 只读诊断");
            return runtime + "；身份 " + identity;
        }
        public double[] ReadBlock(out bool emergencyReleased)
        {
            if (!started || ai == IntPtr.Zero) throw new InvalidOperationException("AI 只读诊断未就绪。");
            var raw = new double[300]; int count;
            InputOnlyArtNative.Check(InputOnlyArtNative.ReadAnalog(ai,100,.3,1,raw,300,out count,IntPtr.Zero),"读取 AI 只读100点组");
            if (count != 100) throw new InvalidDataException("AI 只读组点数不足。");
            var line = digital.ReadLine(); string state = FactoryP04Diagnostic.LineState(line);
            if (state == "无效/未知") throw new InvalidDataException("P0.4只读值无效；不会按高电平处理。");
            emergencyReleased = state == "高"; return raw;
        }
        public void Dispose()
        {
            var failures = new List<Exception>();
            if (ai != IntPtr.Zero)
            {
                if (started) try { InputOnlyArtNative.Check(InputOnlyArtNative.StopTask(ai),"停止 AI 只读诊断"); started = false; } catch (Exception ex) { failures.Add(ex); }
                try { InputOnlyArtNative.Check(InputOnlyArtNative.ClearTask(ai),"释放 AI 只读诊断"); ai = IntPtr.Zero; started = false; } catch (Exception ex) { failures.Add(ex); }
            }
            try { digital.CloseLine(); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("AI/DI只读诊断清理报告错误；没有创建输出。",failures);
        }
    }
}
