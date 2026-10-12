using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.Scope
{
    internal sealed class ScopePlot : Control
    {
        internal Capture Data;
        readonly double[] dcMeanCodes=new double[2];
        double left, span = 1,yCenter,ySpan=2; Point dragStart,dragEnd; bool dragging;
        Rectangle PlotBounds {get{return new Rectangle(75,42,Math.Max(10,Width-100),Math.Max(10,Height-92));}}
        internal ScopePlot()
        {
            DoubleBuffered = true; BackColor = Color.White; Dock = DockStyle.Fill; MinimumSize = new Size(400, 230);
            MouseWheel += (s,e) => Zoom(e,(ModifierKeys&Keys.Shift)!=0);
            MouseDown += (s,e) => { Focus();if(!PlotBounds.Contains(e.Location))return;if(e.Button==MouseButtons.Right)ResetView();else if(e.Button==MouseButtons.Left){dragging=true;dragStart=dragEnd=e.Location;Capture=true;} };
            MouseUp += (s,e) => {if(dragging){dragEnd=Clamp(e.Location);var r=PlotBounds;if(Math.Abs(dragEnd.X-dragStart.X)>=6&&Math.Abs(dragEnd.Y-dragStart.Y)>=6){double a=(Math.Min(dragStart.X,dragEnd.X)-r.Left)/(double)r.Width,b=(Math.Max(dragStart.X,dragEnd.X)-r.Left)/(double)r.Width;left+=span*a;span*=b-a;double top=yCenter+ySpan/2;double hi=top-(Math.Min(dragStart.Y,dragEnd.Y)-r.Top)*ySpan/r.Height,lo=top-(Math.Max(dragStart.Y,dragEnd.Y)-r.Top)*ySpan/r.Height;yCenter=(lo+hi)/2;ySpan=hi-lo;}dragging=false;Capture=false;Invalidate();} };
            MouseMove += (s,e) => {if(dragging){dragEnd=Clamp(e.Location);Invalidate();}};
            MouseCaptureChanged+=(s,e)=>{if(!Capture){dragging=false;Invalidate();}};
        }
        internal void SetData(Capture data) { Data = data;
            dcMeanCodes[0]=data==null||data.A==null?0:CaptureVoltage.MeanAdc(data.A);
            dcMeanCodes[1]=data==null||data.B==null?0:CaptureVoltage.MeanAdc(data.B);
            ResetView(); }
        Point Clamp(Point p){var r=PlotBounds;return new Point(Math.Max(r.Left,Math.Min(r.Right,p.X)),Math.Max(r.Top,Math.Min(r.Bottom,p.Y)));}
        internal void Zoom(MouseEventArgs e,bool vertical)
        {
            var r=PlotBounds;if(Data==null||!r.Contains(e.Location))return;double factor=e.Delta>0?.82:1/.82;
            if(vertical){double fraction=(e.Y-r.Top)/(double)r.Height,anchor=yCenter+ySpan/2-fraction*ySpan;double next=Math.Max(1e-9,Math.Min(1e6,ySpan*factor));yCenter=anchor+(fraction-.5)*next;ySpan=next;}
            else{double fraction=(e.X-r.Left)/(double)r.Width,anchor=left+span*fraction;span=Math.Max(2.0/Data.Info.Samples,Math.Min(1,span*factor));left=Math.Max(0,Math.Min(1-span,anchor-span*fraction));}Invalidate();
        }
        internal void ResetView() {left=0;span=1;yCenter=0;ySpan=2;Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g=e.Graphics; var r=PlotBounds;
            TextRenderer.DrawText(g,"输入波形（已去直流）/ V · 滚轮水平缩放 · Shift+滚轮垂直缩放 · 左拖框选 · 右键复位",Font,new Rectangle(12,8,Width-24,32),Color.FromArgb(35,55,70),TextFormatFlags.EndEllipsis);
            double ymax=1;
            if(Data!=null) { ymax=0; if(Data.A!=null)ymax=Math.Max(ymax,Settings.Ranges[Data.Info.Config.A.Range]);if(Data.B!=null)ymax=Math.Max(ymax,Settings.Ranges[Data.Info.Config.B.Range]); }
            using(var grid=new Pen(Color.FromArgb(226,234,239)))
            for(int k=0;k<=8;k++) { float x=r.Left+k*r.Width/8f, y=r.Top+k*r.Height/8f;g.DrawLine(grid,x,r.Top,x,r.Bottom);g.DrawLine(grid,r.Left,y,r.Right,y);TextRenderer.DrawText(g,(ymax*(yCenter+ySpan/2-k*ySpan/8)).ToString("G4"),Font,new Rectangle(0,(int)y-9,70,20),Color.Gray,TextFormatFlags.Right); }
            if(Data==null) { TextRenderer.DrawText(g,"等待采集；没有设备输出。",Font,new Point(r.Left+18,r.Top+20),Color.Gray);return; }
            int begin=(int)(left*Data.Info.Samples), end=Math.Min(Data.Info.Samples,Math.Max(begin+2,(int)((left+span)*Data.Info.Samples)));
            for(int k=0;k<=8;k++)
            { double sample=begin+(end-begin)*k/8.0;double ms=(sample-Data.Info.PreSamples)*Data.Info.IntervalNs/1e6+(Data.Info.Config.TriggerMode==0?0:Data.Info.Config.DelaySamples*Data.Info.IntervalNs/1e6);TextRenderer.DrawText(g,ms.ToString("G5",CultureInfo.InvariantCulture),Font,new Rectangle(r.Left+k*r.Width/8-35,r.Bottom+8,70,20),Color.Gray,TextFormatFlags.HorizontalCenter); }
            TextRenderer.DrawText(g,Data.Info.Config.TriggerMode==0?"记录时间 / ms":"相对名义触发位置 / ms（自动触发不保证真实触发）",Font,new Point(r.Left,r.Bottom+30),Color.Gray);
            var saved=g.Save();g.SetClip(r);
            for(int ch=0;ch<2;ch++)
            {
                var data=ch==0?Data.A:Data.B;if(data==null)continue;
                double scale=Settings.Ranges[(ch==0?Data.Info.Config.A:Data.Info.Config.B).Range]/Data.Info.MaxAdc;
                using(var pen=new Pen(ch==0?Color.FromArgb(0,135,140):Color.FromArgb(215,110,25),1))
                {
                    PointF? previous=null;
                    for(int px=0;px<r.Width;px++)
                    {
                        int start=begin+(int)((long)(end-begin)*px/r.Width), stop=begin+(int)((long)(end-begin)*(px+1)/r.Width);
                        start=Math.Min(data.Length-1,start);stop=Math.Min(data.Length,Math.Max(start+1,stop));
                        short min=data[start],max=min;for(int i=start+1;i<stop;i++){min=Math.Min(min,data[i]);max=Math.Max(max,data[i]);}
                        float y1=(float)(r.Top+(yCenter+ySpan/2-(min-dcMeanCodes[ch])*scale/ymax)*r.Height/ySpan),y2=(float)(r.Top+(yCenter+ySpan/2-(max-dcMeanCodes[ch])*scale/ymax)*r.Height/ySpan);
                        float x=r.Left+px;g.DrawLine(pen,x,y1,x,y2);
                        if(end-begin<r.Width*2) { var point=new PointF(x,y1);if(previous.HasValue)g.DrawLine(pen,previous.Value,point);previous=point; }
                    }
                }
            }
            g.Restore(saved);
            if(dragging)using(var pen=new Pen(Color.Teal){DashStyle=DashStyle.Dash})g.DrawRectangle(pen,Math.Min(dragStart.X,dragEnd.X),Math.Min(dragStart.Y,dragEnd.Y),Math.Abs(dragStart.X-dragEnd.X),Math.Abs(dragStart.Y-dragEnd.Y));
        }
    }
}
