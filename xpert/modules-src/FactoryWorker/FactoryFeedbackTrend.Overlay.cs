using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryFeedbackTrendControl
    {
        // Presentation only. Shared seconds axis, voltage left / current right.
        // No rectification, resampling, filtering, or changes to control feedback.
        internal bool OverlayChannels { get; set; }
        internal bool ShowIntervalExtrema=true;
        internal string LastEncoded { get { return samples.Count == 0 ? "" : samples[samples.Count-1].Encode(); } }
        internal double OverlayVoltageBound { get { return OverlayBound(false); } }
        internal double OverlayCurrentBound { get { return OverlayBound(true); } }
        private double OverlayBound(bool current)
        {
            double bound = current ? .1 : 5;
            foreach (var sample in samples)
                foreach (int index in current ? new[]{2,7,8} : new[]{1,3,5,6,9,10})
                    bound = Math.Max(bound,Math.Abs(sample.Values[index])*1.08);
            return bound;
        }
        private void PaintOverlay(Graphics g)
        {
            var last = samples.Count == 0 ? null : samples[samples.Count-1];
            string state = last == null ? "等待本轮" : live ? "实时" : "已停止 · 非实时";
            DrawOverlayText(g,"加压电压 / 电流 · " + state,Color.DarkSlateGray,new RectangleF(8,5,Width-16,20));
            DrawOverlayText(g,"VMON " + (last == null ? "—" : last.Values[1].ToString("F3")) + " kV",Color.SteelBlue,new RectangleF(8,26,Width/2f-8,19));
            DrawOverlayText(g,"AI4 " + (last == null ? "—" : last.Values[3].ToString("F3")) + " kV",Color.Teal,new RectangleF(Width/2f,26,Width/2f-8,19));
            DrawOverlayText(g,"IMON / AI0 " + (last == null ? "—" : last.Values[2].ToString("F3")) + " mA",Color.DarkOrange,new RectangleF(8,45,Width/2f-8,19));
            DrawOverlayText(g,"AO " + (last == null ? "—" : last.Values[4].ToString("F4")) + " V",Color.SlateGray,new RectangleF(Width/2f,45,Width/2f-8,19));
            var area = new RectangleF(54,86,Width-110,Height-124);
            if (area.Height < 25 || area.Width < 100) return;
            double voltageBound = OverlayVoltageBound, currentBound = OverlayCurrentBound;
            double end = last == null ? 1 : Math.Max(1,last.Values[0]/1000);
            DrawOverlayText(g,"电压 / kV",Color.Teal,new RectangleF(area.Left,66,110,20));
            DrawOverlayText(g,"电流 / mA",Color.DarkOrange,new RectangleF(area.Right-74,66,80,20));
            using (var grid = new Pen(Color.FromArgb(223,232,239)))
            {
                for (int row=0; row<=2; row++)
                {
                    float y=area.Top+area.Height*row/2;
                    g.DrawLine(grid,area.Left,y,area.Right,y);
                    DrawOverlayText(g,(voltageBound*(1-row)).ToString("0.##"),Color.Teal,new RectangleF(2,y-9,50,19));
                    DrawOverlayText(g,(currentBound*(1-row)).ToString("0.##"),Color.DarkOrange,new RectangleF(area.Right+4,y-9,50,19));
                }
                for(int tick=0;tick<=4;tick++)
                {
                    float x=area.Left+area.Width*tick/4;
                    g.DrawLine(grid,x,area.Top,x,area.Bottom);
                    DrawOverlayText(g,(end*tick/4).ToString("0.0"),Color.SlateGray,new RectangleF(x-12,area.Bottom+2,46,19));
                }
            }
            var saved = g.Save();
            try
            {
                g.SetClip(area);
                Color[] colors={Color.SteelBlue,Color.DarkOrange,Color.Teal};
                for(int channel=0;channel<3;channel++)
                using(var line=new Pen(colors[channel],1.25f))
                using(var peak=new Pen(Color.FromArgb(65,colors[channel]),1))
                {
                    double bound=channel==1 ? currentBound : voltageBound;
                    Func<double,float> y = v => area.Top+area.Height*(float)(1-v/bound)/2;
                    PointF? previous=null; double previousMs=0;
                    foreach(var sample in samples)
                    {
                        var v=sample.Values; float x=area.Left+(float)(v[0]/1000/end)*area.Width;
                        int low=5+channel*2;
                        if(ShowIntervalExtrema)g.DrawLine(peak,x,y(v[low]),x,y(v[low+1]));
                        var point=new PointF(x,y(v[channel+1]));
                        if(previous.HasValue && v[0]-previousMs<=250)g.DrawLine(line,previous.Value,point);
                        else g.DrawEllipse(line,x-1,point.Y-1,2,2);
                        previous=point;previousMs=v[0];
                    }
                }
            }
            finally { g.Restore(saved); }
            DrawOverlayText(g,"从初始化起 / s · 中位数趋势 · "+(ShowIntervalExtrema?"竖线=约100ms原始范围":"区间极值已隐藏")+" · 空档不连线",Color.SlateGray,new RectangleF(8,Height-19,Width-16,19));
        }
        private void DrawOverlayText(Graphics g,string text,Color color,RectangleF box)
        {
            using(var brush=new SolidBrush(color))
            using(var format=new StringFormat { FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter })
                g.DrawString(text,Font,brush,box,format);
        }
    }
}
