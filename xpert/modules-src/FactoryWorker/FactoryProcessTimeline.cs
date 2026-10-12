using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryProcessPoint
    { internal double Ms,Vm,Im,Div;internal long Index;internal int Segment; }
    internal sealed class FactoryProcessTimeline
    {
        internal readonly List<FactoryProcessPoint> Points=new List<FactoryProcessPoint>();
        internal double OpticalMs=Double.NaN,OffMs=Double.NaN,EndMs=1;
        internal string Detail="";
        internal static FactoryProcessTimeline Read(string directory)
        {
            if(Path.GetFileName(directory)=="capture-preview")directory=Path.GetDirectoryName(directory);
            var data=new FactoryProcessTimeline();string path=Path.Combine(directory,"ai-monitor-raw.csv");
            if(File.Exists(path))using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(stream))
            {
                reader.ReadLine();string line;var batch=new List<FactoryProcessPoint>();double host=-1;
                Action flush=()=>{for(int i=0;i<batch.Count;i++){batch[i].Ms=host-(batch.Count-1-i);data.Points.Add(batch[i]);}batch.Clear();};
                while((line=reader.ReadLine())!=null)
                {
                    var v=line.Split(',');if(v.Length!=15)throw new InvalidDataException("监测CSV列数不符");
                    int segment=Int32.Parse(v[0],CultureInfo.InvariantCulture);long index=Int64.Parse(v[1],CultureInfo.InvariantCulture);
                    double next=Double.Parse(v[3],CultureInfo.InvariantCulture);
                    if(batch.Count>0 && (next!=host || segment!=batch[batch.Count-1].Segment || index!=batch[batch.Count-1].Index+1))flush();
                    host=next;batch.Add(new FactoryProcessPoint {Index=index,Segment=segment,Vm=Double.Parse(v[12],CultureInfo.InvariantCulture),Im=Double.Parse(v[11],CultureInfo.InvariantCulture),Div=Double.Parse(v[13],CultureInfo.InvariantCulture)});
                    if(data.Points.Count+batch.Count>2000000)throw new InvalidDataException("全程监测记录超过显示点数上限");
                }
                flush();
            }
            path=Path.Combine(directory,"ai-monitor-events.csv");
            if(File.Exists(path))using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(stream))
            {
                string line;while((line=reader.ReadLine())!=null)
                {
                    int comma=line.IndexOf(',');double ms;if(comma<0 || !Double.TryParse(line.Substring(0,comma),NumberStyles.Float,CultureInfo.InvariantCulture,out ms))continue;
                    if(Double.IsNaN(data.OpticalMs) && line.Contains("OPTICAL_HIGH_REQUEST"))data.OpticalMs=ms;
                    if(Double.IsNaN(data.OffMs) && line.Contains("REQUEST (host command boundary"))data.OffMs=ms;
                }
            }
            foreach(var p in data.Points)data.EndMs=Math.Max(data.EndMs,p.Ms);
            if(FactorySettings.Finite(data.OpticalMs))data.EndMs=Math.Max(data.EndMs,data.OpticalMs);
            data.Detail="全程原始监测 + AI4振荡段；横轴为主机近似时间。VMON/IMON未采集区留空。";
            return data;
        }
    }
    internal sealed partial class FactoryShutdownDiagnosticControl
    {
        private FactoryProcessTimeline timeline;
        private FactorySampleEnvelope oscillation;
        private double oscillationRate,oscillationStart,viewStart,viewEnd=1;
        private bool timelineDragging;
        private int timelineDragX;
        private double timelineDragStart,timelineDragEnd;
        internal void ClearTimeline(){timeline=null;oscillation=null;viewStart=0;viewEnd=1;}
        internal void SetTimeline(FactoryProcessTimeline data,BenchCapture capture)
        {
            timeline=data;
            if(capture!=null)
            {oscillation=new FactorySampleEnvelope(capture.VoltageRaw,capture.Settings.DividerVPerV/1000);oscillationRate=capture.ArtRateHz;oscillationStart=data.OpticalMs-capture.Settings.PreTriggerDurationMs;}
            if(oscillation!=null && FactorySettings.Finite(data.OpticalMs))data.EndMs=Math.Max(data.EndMs,oscillationStart+oscillation.Count*1000/oscillationRate);
            viewStart=0;viewEnd=Math.Max(1,data.EndMs);Invalidate();
        }
        internal void SetTimelineView(double start,double end)
        {if(timeline==null || start<0 || end<=start || end>timeline.EndMs+1e-6)throw new ArgumentOutOfRangeException();viewStart=start;viewEnd=end;Invalidate();}
        internal bool HasOscillationTimeline {get{return oscillation!=null && timeline!=null && FactorySettings.Finite(timeline.OpticalMs);}}
        private void DrawTimeline(Graphics g)
        {
            if(Width<300 || Height<230)return;
            g.DrawString(timeline.Detail,Font,Brushes.Teal,8,5);
            g.DrawString("滚轮放大时间 · 拖动平移 · 右键全程；红线HV OFF请求，紫线光触发请求（非硬件边沿标定）。",Font,Brushes.DimGray,8,27);
            if(oscillation!=null && !FactorySettings.Finite(timeline.OpticalMs))g.DrawString("缺少光触发时间记录，振荡段未拼接；请在加压页查看。",Font,Brushes.DarkOrange,8,48);
            string[] names={"VMON / kV","IMON / mA","分压器 AI4 / kV · 加压与振荡"};Color[] colors={Color.SteelBlue,Color.DarkOrange,Color.Teal};
            float lane=(Height-115)/3f;
            for(int channel=0;channel<3;channel++)
            {
                var area=new RectangleF(85,88+channel*lane,Width-110,lane-30);double bound=channel==1?.1:5;
                Func<FactoryProcessPoint,double> value=p=>channel==0?p.Vm:channel==1?p.Im:p.Div;
                foreach(var p in timeline.Points)if(p.Ms>=viewStart && p.Ms<=viewEnd && FactorySettings.Finite(value(p)))bound=Math.Max(bound,Math.Abs(value(p))*1.08);
                int lo=0,hi=0;
                bool wave=channel==2 && HasOscillationTimeline;
                if(wave)
                {
                    int first=Math.Max(0,(int)Math.Floor((viewStart-oscillationStart)*oscillationRate/1000)),end=Math.Min(oscillation.Count,(int)Math.Ceiling((viewEnd-oscillationStart)*oscillationRate/1000));
                    bool missing;if(end>first){oscillation.Query(first,end,out lo,out hi,out missing);if(lo>=0)bound=Math.Max(bound,Math.Max(Math.Abs(oscillation.Value(lo)),Math.Abs(oscillation.Value(hi)))*1.08);}
                }
                Func<double,float> x=ms=>area.Left+(float)((ms-viewStart)/(viewEnd-viewStart)*area.Width);
                Func<double,float> y=v=>area.Top+area.Height*(float)(1-v/bound)/2;
                g.DrawString(names[channel],Font,Brushes.DimGray,85,area.Top-20);
                using(var grid=new Pen(Color.FromArgb(225,233,239)))for(int r=0;r<3;r++){float py=area.Top+r*area.Height/2;g.DrawLine(grid,area.Left,py,area.Right,py);g.DrawString((bound*(1-r)).ToString("0.###"),Font,Brushes.Gray,5,py-7);}
                var state=g.Save();g.SetClip(area);
                using(var mark=new Pen(Color.Firebrick)){if(FactorySettings.Finite(timeline.OffMs))g.DrawLine(mark,x(timeline.OffMs),area.Top,x(timeline.OffMs),area.Bottom);}
                using(var mark=new Pen(Color.MediumPurple)){if(FactorySettings.Finite(timeline.OpticalMs))g.DrawLine(mark,x(timeline.OpticalMs),area.Top,x(timeline.OpticalMs),area.Bottom);}
                using(var pen=new Pen(colors[channel],1))
                {
                    FactoryProcessPoint prev=null;PointF last=PointF.Empty;
                    foreach(var p in timeline.Points)
                    {
                        double v=value(p);if(p.Ms<viewStart || p.Ms>viewEnd || !FactorySettings.Finite(v)){prev=null;continue;}
                        var point=new PointF(x(p.Ms),y(v));
                        if(prev!=null && p.Segment==prev.Segment && p.Index==prev.Index+1 && p.Ms>=prev.Ms && p.Ms-prev.Ms<=25)g.DrawLine(pen,last,point);
                        else g.DrawEllipse(pen,point.X-1,point.Y-1,2,2);
                        prev=p;last=point;
                    }
                    if(wave)
                    {
                        PointF? prior=null;
                        for(int col=0;col<(int)area.Width;col++)
                        {
                            double ms0=viewStart+col*(viewEnd-viewStart)/area.Width,ms1=viewStart+(col+1)*(viewEnd-viewStart)/area.Width;
                            int first=Math.Max(0,(int)Math.Ceiling((ms0-oscillationStart)*oscillationRate/1000)),end=Math.Min(oscillation.Count,(int)Math.Ceiling((ms1-oscillationStart)*oscillationRate/1000));
                            bool missing;if(first>=end){continue;}oscillation.Query(first,end,out lo,out hi,out missing);if(missing || lo<0){prior=null;continue;}
                            var ordered=new[]{first,lo,hi,end-1};Array.Sort(ordered);
                            foreach(int i in ordered){var point=new PointF(x(oscillationStart+i*1000/oscillationRate),y(oscillation.Value(i)));if(prior.HasValue)g.DrawLine(pen,prior.Value,point);prior=point;}
                        }
                    }
                }
                g.Restore(state);
                if(channel==2)for(int i=0;i<=5;i++){double ms=viewStart+(viewEnd-viewStart)*i/5;g.DrawString((ms/1000).ToString("0.######"),Font,Brushes.Gray,x(ms)-20,area.Bottom+3);}
            }
            g.DrawString("全程时间 / s · 不补齐采集切换空档；放大可看振荡原始点，采后不追加AI采集。",Font,Brushes.DimGray,85,Height-23);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);if(timeline==null)return;
            double span=Math.Max(.01,Math.Min(timeline.EndMs,(viewEnd-viewStart)*(e.Delta>0?.7:1/.7))),fraction=Math.Max(0,Math.Min(1,(e.X-85.0)/Math.Max(1,Width-110)));
            double pivot=viewStart+(viewEnd-viewStart)*fraction;viewStart=Math.Max(0,Math.Min(timeline.EndMs-span,pivot-span*fraction));viewEnd=viewStart+span;Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {base.OnMouseDown(e);Focus();if(timeline==null)return;if(e.Button==MouseButtons.Right){viewStart=0;viewEnd=timeline.EndMs;Invalidate();}else if(e.Button==MouseButtons.Left){timelineDragging=true;timelineDragX=e.X;timelineDragStart=viewStart;timelineDragEnd=viewEnd;Capture=true;}}
        protected override void OnMouseMove(MouseEventArgs e)
        {base.OnMouseMove(e);if(!timelineDragging || timeline==null)return;double span=timelineDragEnd-timelineDragStart;viewStart=Math.Max(0,Math.Min(timeline.EndMs-span,timelineDragStart+(timelineDragX-e.X)*span/Math.Max(1,Width-110)));viewEnd=viewStart+span;Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);timelineDragging=false;Capture=false;}
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)timelineDragging=false;}
    }
}
