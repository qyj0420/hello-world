using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // This is a software event window in the EXISTING 1 kS/s ART monitor.
    // It is deliberately not a Pico block, a hardware trigger, or a safety verdict.
    public sealed class FactoryShutdownPoint
    {
        public int Segment;
        public long Index, HostReadMs;
        public double EstimatedMs, Ai0, Ai2, Ai4;
    }
    public sealed class FactoryShutdownEvent
    {
        public long HostMs;
        public string Name;
    }
    public sealed class FactoryShutdownRecord
    {
        public bool Simulation, OffCallsReturned, PostReadFinished;
        public int PreWindowMs = 500, PostWindowMs = 500;
        public long OffRequestMs, OffReturnedMs;
        public double RateHz, VmonKvPerV, ImonMaPerV, DividerKvPerV;
        public string Status, TimingNotice;
        public List<FactoryShutdownPoint> Points = new List<FactoryShutdownPoint>();
        public List<FactoryShutdownEvent> Events = new List<FactoryShutdownEvent>();
    }
    internal sealed class FactoryShutdownDiagnostic
    {
        internal const int WindowMs = 500;
        private readonly Queue<FactoryShutdownPoint> ring = new Queue<FactoryShutdownPoint>();
        private readonly List<FactoryShutdownEvent> events = new List<FactoryShutdownEvent>();
        private readonly FactorySettings settings;
        private readonly IFactoryClock clock;
        private int segment;
        private long index;
        private bool frozen;
        internal FactoryShutdownRecord Record { get; private set; }
        internal FactoryShutdownDiagnostic(FactorySettings settings, IFactoryClock clock)
        { this.settings = settings.Clone(); this.clock = clock; }
        internal void Monitor(bool start)
        { if (start) { segment++; index = 0; } Event(start ? "MONITOR_START" : "MONITOR_GAP"); }
        internal void Event(string name) { EventAt(name, clock.ElapsedMilliseconds); }
        internal void EventAt(string name, long ms)
        {
            var e = new FactoryShutdownEvent { HostMs = ms, Name = name };
            if (Record != null) { if (Record.Events.Count < 200) Record.Events.Add(e); return; }
            events.Add(e);
            if (events.Count > 200) events.RemoveAt(0);
        }
        internal void Raw(double[] values, int count)
        {
            if (values == null || count < 0 || count > values.Length / 3) throw new InvalidDataException("关断诊断：SDK点数无效。");
            long readMs = clock.ElapsedMilliseconds;
            for (int i = 0; i < count; i++)
            {
                var p = new FactoryShutdownPoint { Segment = segment, Index = index++, HostReadMs = readMs,
                    // Batches can be delayed. This estimate is a plotting aid ONLY;
                    // exact within-segment index and host read time are saved separately.
                    EstimatedMs = settings.Simulation ? readMs : readMs - (count - 1 - i) * 1000.0 / FactoryArtFeedbackPlan.RateHz,
                    Ai0 = values[3*i], Ai2 = values[3*i+1], Ai4 = values[3*i+2] };
                if (Record == null)
                {
                    ring.Enqueue(p);
                    while (ring.Count > 4000 || (ring.Count > 1 && ring.Peek().HostReadMs < readMs - 1000)) ring.Dequeue();
                }
                else if (!frozen && p.EstimatedMs >= Record.OffRequestMs - WindowMs && Record.Points.Count < 8000)
                    Record.Points.Add(p);
            }
        }
        internal bool Trigger(long requestedMs, long returnedMs, string command, bool returned)
        {
            if (Record != null) return false;
            Record = new FactoryShutdownRecord { Simulation = settings.Simulation, OffRequestMs = requestedMs,
                OffReturnedMs = returnedMs, OffCallsReturned = returned,
                RateHz = settings.Simulation ? 0 : FactoryArtFeedbackPlan.RateHz,
                VmonKvPerV = settings.VmonFullScaleKv / settings.FeedbackFullScaleV,
                ImonMaPerV = settings.ImonFullScaleMa / settings.FeedbackFullScaleV,
                DividerKvPerV = settings.Input.DividerVPerV / 1000,
                Status = "等待关断后只读记录；不代表已放电。",
                TimingNotice = "软件关断请求为0 ms，不是物理HV OFF边沿。曲线时间按批末主机读取时间回推，仅为近似；保留段号/点号/原始读时刻，不承诺跨批或命令逐点同步。ART三路1 kS/s，不是80k振荡或125M局放采集；仿真仅记录观察点。无平均、滤波、补零或自动扣零。" };
            Record.Points.AddRange(ring.Where(p => p.EstimatedMs >= requestedMs - WindowMs));
            Record.Events.AddRange(events.Where(e => e.HostMs >= requestedMs - WindowMs));
            ring.Clear(); events.Clear();
            EventAt(command + "_REQUEST (software)", requestedMs);
            EventAt(command + (returned ? "_RETURNED (not physical readback)" : "_FAILED (outputs uncertain)"), returnedMs);
            return true;
        }
        internal void Finish(string status, bool readFinished)
        {
            if (Record == null) return;
            frozen = true; // freeze before subsequent normal capture/restored monitoring
            if(readFinished && !Record.Points.Any(p=>p.EstimatedMs>Record.OffReturnedMs && p.HostReadMs>=Record.OffReturnedMs+WindowMs-50))
            { readFinished=false; status="等待窗口已结束，但没有覆盖窗口尾部的关断后样本；残压未知"; }
            Record.PostReadFinished = readFinished;
            Record.Status = status + "；不用于残压放行，不代表已放电。";
            if (!readFinished) Record.Status = "诊断记录不完整：" + Record.Status;
            Event("DIAGNOSTIC_END: " + (readFinished ? "read window elapsed; inspect gaps/invalid points" : "incomplete"));
        }
        internal string Save(string directory)
        {
            if (Record == null) return null;
            string path = Path.Combine(directory, "shutdown-diagnostic.xml");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                new XmlSerializer(typeof(FactoryShutdownRecord)).Serialize(stream, Record);
            using (var writer = new StreamWriter(new FileStream(Path.Combine(directory,"shutdown-diagnostic.csv"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(true)))
            {
                writer.WriteLine("approx_relative_ms,segment,sample_index,host_read_elapsed_ms,AI0_IMON_raw_V,AI2_VMON_raw_V,AI4_divider_raw_V,VMON_kV,IMON_mA,divider_signed_kV,simulation");
                foreach (var p in Record.Points)
                    writer.WriteLine(String.Join(",", new[] { N(p.EstimatedMs-Record.OffRequestMs),p.Segment.ToString(CultureInfo.InvariantCulture),p.Index.ToString(CultureInfo.InvariantCulture),p.HostReadMs.ToString(CultureInfo.InvariantCulture),
                        N(p.Ai0),N(p.Ai2),N(p.Ai4),N(p.Ai2*Record.VmonKvPerV),N(p.Ai0*Record.ImonMaPerV),N(p.Ai4*Record.DividerKvPerV),Record.Simulation.ToString() }));
            }
            return path;
        }
        private static string N(double value) { return value.ToString("R",CultureInfo.InvariantCulture); }
    }

    internal static class FactoryShutdownReadWindow
    {
        // Called only AFTER all requested disable calls have returned. No caller
        // cancellation can interrupt the disable calls. Reads are diagnostic only.
        internal static void Run(IFactoryClock clock, Action read)
        {
            long start = clock.ElapsedMilliseconds;
            var wall = Stopwatch.StartNew();
            int iterations = 0;
            while (clock.ElapsedMilliseconds - start < FactoryShutdownDiagnostic.WindowMs && wall.ElapsedMilliseconds < 750 && iterations++ < 51)
            {
                read(); // bounded read requests at least one point even if Available reports zero
                clock.Pause(10, CancellationToken.None);
            }
            read(); // drain the last available batch; never wait for an external trigger
            if (clock.ElapsedMilliseconds-start < FactoryShutdownDiagnostic.WindowMs)
                throw new TimeoutException("关断诊断未完成500 ms只读窗口；不会自动重试加压。");
        }
    }

    internal sealed partial class FactoryShutdownDiagnosticControl : Control
    {
        internal FactoryShutdownRecord Record { get; private set; }
        private string error;
        internal FactoryShutdownDiagnosticControl()
        { Dock=DockStyle.Fill; DoubleBuffered=true; BackColor=Color.White; Font=new Font("Microsoft YaHei UI",9); }
        internal void ClearRecord() { Record=null; error=null; ClearTimeline(); Invalidate(); }
        internal void LoadRecord(string path)
        {
            try { using(var stream=File.OpenRead(path)) Record=(FactoryShutdownRecord)new XmlSerializer(typeof(FactoryShutdownRecord)).Deserialize(stream); error=null; }
            catch(Exception ex) { Record=null; error="诊断文件无法读取："+ex.Message; }
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g=e.Graphics;
            if(timeline!=null){DrawTimeline(g);return;}
            if(Record==null) { g.DrawString(error ?? "等待本轮首次HV OFF前后诊断；0 kV/未启动HV时不生成。",Font,Brushes.DimGray,12,12); return; }
            if(Width<300 || Height<240) return;
            var r=Record;
            g.DrawString((r.Simulation ? "仿真观察点" : "ART原始三路 · 1 kS/s")+" · 关断前500 ms / 后500 ms请求 · 不是高速局放预触发",Font,Brushes.Teal,8,5);
            g.DrawString(r.Status,Font,Brushes.DarkRed,new RectangleF(8,25,Width-16,42));
            double min=-500, max=Math.Max(500,r.OffReturnedMs-r.OffRequestMs+500);
            var names=new[]{"VMON · AI2 / kV","IMON · AI0 / mA","分压器 · AI4 / kV（有符号）"};
            var colors=new[]{Color.SteelBlue,Color.DarkOrange,Color.Teal};
            float lane=(Height-150)/3f;
            for(int ch=0;ch<3;ch++)
            {
                Func<FactoryShutdownPoint,double> value=p=>ch==0?p.Ai2*r.VmonKvPerV:ch==1?p.Ai0*r.ImonMaPerV:p.Ai4*r.DividerKvPerV;
                double bound=ch==1?.1:5;
                foreach(var p in r.Points) if(FactorySettings.Finite(value(p))) bound=Math.Max(bound,Math.Abs(value(p))*1.08);
                var area=new RectangleF(82,91+ch*lane,Width-105,lane-29);
                using(var pen=new Pen(colors[ch],1))
                using(var grid=new Pen(Color.FromArgb(225,233,239)))
                {
                    g.DrawString(names[ch],Font,Brushes.DimGray,82,area.Top-20);
                    for(int row=0;row<3;row++) { float y=area.Top+row*area.Height/2; g.DrawLine(grid,area.Left,y,area.Right,y); g.DrawString((bound*(1-row)).ToString("0.###"),Font,Brushes.Gray,6,y-7); }
                    Func<double,float> x=ms=>area.Left+(float)((ms-min)/(max-min))*area.Width;
                    // Draw event markers FIRST: a spike exactly at shutdown must
                    // not be hidden beneath the vertical annotation.
                    using(var mark=new Pen(Color.Firebrick,1)) { mark.DashStyle=System.Drawing.Drawing2D.DashStyle.Dash; g.DrawLine(mark,x(0),area.Top,x(0),area.Bottom); }
                    using(var mark=new Pen(Color.FromArgb(120,Color.DarkViolet),1)) g.DrawLine(mark,x(r.OffReturnedMs-r.OffRequestMs),area.Top,x(r.OffReturnedMs-r.OffRequestMs),area.Bottom);
                    PointF? last=null; FactoryShutdownPoint prev=null;
                    foreach(var p in r.Points)
                    {
                        double t=p.EstimatedMs-r.OffRequestMs, v=value(p);
                        if(t<min || t>max || !FactorySettings.Finite(v)) { last=null; prev=null; continue; }
                        var point=new PointF(x(t),area.Top+area.Height*(float)(1-v/bound)/2);
                        // Do not join across segments, out-of-order inferred time,
                        // missing indices, or long read gaps. No invented continuity.
                        if(last.HasValue && p.Segment==prev.Segment && p.Index==prev.Index+1 && p.EstimatedMs>=prev.EstimatedMs && p.EstimatedMs-prev.EstimatedMs<=25) g.DrawLine(pen,last.Value,point);
                        else g.DrawEllipse(pen,point.X-1,point.Y-1,2,2);
                        last=point;prev=p;
                    }
                    if(ch==2) for(int tick=0;tick<=4;tick++) { double t=min+(max-min)*tick/4;g.DrawString(t.ToString("0"),Font,Brushes.Gray,x(t)-15,area.Bottom+3); }
                }
            }
            g.DrawString("红虚线：关断请求；紫线：关断调用结束。近似相对时间 / ms，非物理边沿或逐点同步。\n批内保留原始样本；完整点号、主机时间、阶段见CSV/XML。缺点、超时、无效值不补齐；不作放电确认。",Font,Brushes.DimGray,new RectangleF(8,Height-55,Width-16,52));
        }
    }
}
