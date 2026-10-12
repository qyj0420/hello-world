using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    // UI-only transport: derived median line plus untouched raw interval extrema.
    internal sealed class FactoryFeedbackTrendSample
    {
        // elapsed ms, VMON kV, IMON mA, signed AI4 kV, AO V,
        // VMON min/max, IMON min/max, signed AI4 min/max in the display interval.
        internal double[] Values = new double[11];
        internal string VmonDisplayText
        { get { double raw = Values[1]; return Values.Length > 12 && Values[12] > 0 && raw < 0 && raw >= -Values[12]
            ? "工作 0.000 kV（原始窗口 " + raw.ToString("F3") + " kV，负噪声按0）" : raw.ToString("F3") + " kV"; } }
        internal string ValueSourceLabel { get { return Values.Length > 11 && Values[11] >= 3
            ? "窗口" + Values[11].ToString("0",CultureInfo.InvariantCulture) + "点中位数"
            : "诊断/旧记录原始末点（非完整窗口）"; } }
        internal string Encode() { return String.Join(";",Array.ConvertAll(Values,v => v.ToString("R",CultureInfo.InvariantCulture))); }
        internal static FactoryFeedbackTrendSample Parse(string text)
        {
            var parts = (text ?? "").Split(';'); if (parts.Length != 11 && parts.Length != 12 && parts.Length != 13) return null;
            var sample = new FactoryFeedbackTrendSample { Values = new double[parts.Length] };
            for (int i = 0; i < parts.Length; i++)
                if (!Double.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out sample.Values[i]) || !FactorySettings.Finite(sample.Values[i])) return null;
            if (sample.Values[0] < 0) return null;
            return sample;
        }
    }
    internal sealed class FactoryFeedbackTrendPublisher
    {
        private readonly FactorySettings settings;
        private readonly IFactoryClock clock;
        private readonly Action<string,string> report;
        private FactoryFeedbackTrendSample pending;
        private long last = -1000;
        internal FactoryFeedbackTrendPublisher(FactorySettings s, IFactoryClock c, Action<string,string> r)
        { settings = s; clock = c; report = r; }
        internal void Observe(FactoryFeedback f)
        {
            if (f == null) return;
            // Do not carry pre-capture extrema into the post-capture interval.
            if (pending != null && clock.ElapsedMilliseconds-pending.Values[0]>250) Flush();
            double vm = settings.VmonFullScaleKv / settings.FeedbackFullScaleV;
            double im = settings.ImonFullScaleMa / settings.FeedbackFullScaleV;
            double div = settings.Input.DividerVPerV / 1000;
            var sample = new FactoryFeedbackTrendSample { Values = new[] {
                (double)clock.ElapsedMilliseconds,FactoryFeedbackWindow.Vmon(f) * vm,FactoryFeedbackWindow.Imon(f) * im,FactoryFeedbackWindow.Divider(f) * div,f.AoV,
                FactoryFeedbackBounds.Minimum(f.VmonRawV,f.VmonMinimumRawV,f.SampleCount) * vm,
                FactoryFeedbackBounds.Maximum(f.VmonRawV,f.VmonMaximumRawV,f.SampleCount) * vm,
                FactoryFeedbackBounds.Minimum(f.ImonRawV,f.ImonMinimumRawV,f.SampleCount) * im,
                FactoryFeedbackBounds.Maximum(f.ImonRawV,f.ImonMaximumRawV,f.SampleCount) * im,
                FactoryFeedbackBounds.Minimum(f.DividerRawV,f.DividerMinimumRawV,f.SampleCount) * div,
                FactoryFeedbackBounds.Maximum(f.DividerRawV,f.DividerMaximumRawV,f.SampleCount) * div,
                (double)f.WindowSampleCount, settings.ZeroBackgroundLimitV / 1000 } };
            foreach (double value in sample.Values) if (!FactorySettings.Finite(value)) return; // controller still rejects invalid raw feedback
            if (pending != null)
                for (int i = 5; i < 11; i += 2)
                { sample.Values[i] = Math.Min(sample.Values[i],pending.Values[i]); sample.Values[i+1] = Math.Max(sample.Values[i+1],pending.Values[i+1]); }
            pending = sample;
            if (clock.ElapsedMilliseconds - last >= 100) Flush();
        }
        internal void Flush()
        {
            if (pending == null) return;
            report("FEEDBACK_TREND",pending.Encode()); pending = null; last = clock.ElapsedMilliseconds;
        }
    }
    internal sealed partial class FactoryFeedbackTrendControl : Control
    {
        private readonly List<FactoryFeedbackTrendSample> samples = new List<FactoryFeedbackTrendSample>();
        private bool live;
        private int currentWindows;
        private double currentMean, currentMin, currentMax;
        internal int SampleCount { get { return samples.Count; } }
        internal FactoryFeedbackTrendControl()
        { DoubleBuffered = true; BackColor = Color.White; Font = new Font("Microsoft YaHei UI",9); Dock = DockStyle.Fill; }
        internal void ClearTrend() { samples.Clear(); live = false; currentWindows = 0; currentMean = currentMin = currentMax = 0; Invalidate(); }
        internal void SetLive(bool value) { live = value; Invalidate(); }
        internal void Add(string value)
        {
            var sample = FactoryFeedbackTrendSample.Parse(value); if (sample == null) return;
            if (samples.Count > 0 && sample.Values[0] < samples[samples.Count-1].Values[0]) return;
            samples.Add(sample);
            if (sample.Values.Length > 11 && sample.Values[11] >= 3)
            {
                double current = sample.Values[2]; currentWindows++;
                currentMean += (current-currentMean)/currentWindows;
                currentMin = currentWindows == 1 ? current : Math.Min(currentMin,current);
                currentMax = currentWindows == 1 ? current : Math.Max(currentMax,current);
            }
            // Retain the beginning of a long manual session. Compact display
            // bins with extrema, not control data; the CSV retains SDK samples.
            if (samples.Count > 12000)
            {
                for (int i=0; i<6000; i++)
                {
                    var a=samples[i*2+1]; var b=samples[i*2+2];
                    for(int j=5;j<11;j+=2) { b.Values[j]=Math.Min(a.Values[j],b.Values[j]); b.Values[j+1]=Math.Max(a.Values[j+1],b.Values[j+1]); }
                    samples[i+1]=b;
                }
                samples.RemoveRange(6001,6000);
            }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = e.Graphics; if (Width < 240 || Height < 180) return;
            if (OverlayChannels) { PaintOverlay(g); return; }
            string[] names = { "VMON · AI2", "IMON · AI0", "分压器 · AI4（有符号）" };
            string[] units = { "kV", "mA", "kV" };
            Color[] colors = { Color.SteelBlue, Color.DarkOrange, Color.Teal };
            string state = samples.Count == 0 ? "等待本次原始反馈" : live ? "实时监测" : "已停止 · 保留末次数据，非实时";
            using (var ink = new SolidBrush(Color.FromArgb(35,65,80)))
                g.DrawString(state + " | 末次=" + (samples.Count == 0 ? "等待完整窗口" : samples[samples.Count-1].ValueSourceLabel) + " / 竖线=原始极值；AO=" + (samples.Count == 0 ? "—" : samples[samples.Count-1].Values[4].ToString("F4")) + " V",Font,ink,8,5);
            double end = samples.Count == 0 ? 1 : Math.Max(1,samples[samples.Count-1].Values[0] / 1000);
            double begin = 0; float lane = (Height-78) / 3f;
            for (int channel = 0; channel < 3; channel++)
            {
                int valueIndex = channel+1, lowIndex = 5+channel*2;
                double bound = channel == 1 ? .1 : 5;
                foreach (var sample in samples)
                    if (sample.Values[0] / 1000 >= begin)
                        bound = Math.Max(bound,1.08 * Math.Max(Math.Abs(sample.Values[lowIndex]),Math.Abs(sample.Values[lowIndex+1])));
                var area = new RectangleF(78,47+channel*lane,Width-105,lane-30);
                using (var label = new SolidBrush(colors[channel]))
                    g.DrawString(names[channel] + "  " + (samples.Count == 0 ? "— " + units[channel] : channel == 0 ? samples[samples.Count-1].VmonDisplayText : samples[samples.Count-1].Values[valueIndex].ToString("F3") + " " + units[channel]) +
                        (channel == 1 && currentWindows > 0 ? "；显示窗口均值 " + currentMean.ToString("F3") + " / 范围 " + currentMin.ToString("F3") + "～" + currentMax.ToString("F3") + " mA" : ""),Font,label,78,area.Top-20);
                using (var grid = new Pen(Color.FromArgb(223,232,239)))
                using (var label = new SolidBrush(Color.SlateGray))
                {
                    for (int row = 0; row <= 2; row++)
                    { float y=area.Top+area.Height*row/2; g.DrawLine(grid,area.Left,y,area.Right,y); g.DrawString((bound*(1-row)).ToString("0.###"),Font,label,5,y-7); }
                    for (int tick = 0; tick <= 4; tick++)
                    { float x=area.Left+area.Width*tick/4; g.DrawLine(grid,x,area.Top,x,area.Bottom); if (channel == 2) g.DrawString((begin+(end-begin)*tick/4).ToString("0.0"),Font,label,x-8,area.Bottom+2); }
                }
                using (var line = new Pen(colors[channel],1.4f))
                using (var peak = new Pen(Color.FromArgb(85,colors[channel]),1))
                {
                    PointF? previous = null; double previousMs=0;
                    foreach (var sample in samples)
                    {
                        var v=sample.Values; if (v[0]/1000<begin) continue;
                        float x=area.Left+(float)((v[0]/1000-begin)/(end-begin))*area.Width;
                        Func<double,float> y = a => area.Top+area.Height*(float)(1-a/bound)/2;
                        g.DrawLine(peak,x,y(v[lowIndex]),x,y(v[lowIndex+1]));
                        var point=new PointF(x,y(v[valueIndex]));
                        if (previous.HasValue && v[0]-previousMs<=250) g.DrawLine(line,previous.Value,point);
                        else g.DrawEllipse(line,x-1,point.Y-1,2,2);
                        previous=point; previousMs=v[0];
                    }
                }
            }
            using (var label = new SolidBrush(Color.SlateGray))
                g.DrawString("从初始化起 / s；约100 ms显示窗口统计，非全采样平均；原始点存CSV，过流/过压检查原始峰值；有限采集有空档。",Font,label,8,Height-22);
        }
    }
}
