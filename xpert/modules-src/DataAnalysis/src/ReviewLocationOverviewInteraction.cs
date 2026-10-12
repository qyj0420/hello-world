using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.PdReview
{
    internal sealed partial class ReviewLocationOverview
    {
        int cursor1=-1,cursor2=-1,activeCursor=-1;
        bool selectingRange;
        int selectionX,selectionEndX;
        internal event EventHandler CursorsChanged;
        internal Func<double,double> PositionForDelayNs;
        internal Func<double> RoundTripTimeNs;
        internal Func<string> MeasurementVelocityText;
        internal string MeasurementGeometryStatus="";
        internal bool CursorsVisible {get{return cursor1>=0&&cursor2>=0&&samples.Length>0;}}
        internal int Cursor1Index {get{return cursor1;}}
        internal int Cursor2Index {get{return cursor2;}}
        internal double CursorDeltaNs {get{return CursorsVisible?Math.Abs(cursor2-(double)cursor1)*1e9/sampleRate:double.NaN;}}
        internal double CursorPositionM {get{return CursorsVisible&&PositionForDelayNs!=null?PositionForDelayNs(CursorDeltaNs):double.NaN;}}
        double TimeDisplayScale {get{return (viewEnd-(double)viewStart)/sampleRate<.0001?1e6:1e3;}}
        string TimeDisplayUnit {get{return TimeDisplayScale==1e6?"μs":"ms";}}
        float SampleX(double sample,Rectangle plot){return plot.Left+(float)((sample-viewStart)/Math.Max(1,viewEnd-viewStart)*plot.Width);}
        int SampleAtX(int x,Rectangle plot){return Math.Max(viewStart,Math.Min(viewEnd,viewStart+(int)Math.Round((x-plot.Left)/(double)plot.Width*(viewEnd-viewStart))));}
        internal double ReflectionBoundarySample
        {
            get{
                double duration=RoundTripTimeNs==null?double.NaN:RoundTripTimeNs();
                double count=duration*sampleRate/1e9;
                return cursor1>=0&&Finite(count)&&count>0?cursor1+count:double.NaN;
            }
        }
        void ConstrainInteractiveReflection()
        {
            double boundary=ReflectionBoundarySample;
            if(!CursorsVisible||!Finite(boundary))return;
            // The marker uses exact travel time; the cursor uses the last recorded
            // sample not later than that boundary, never a rounded-outside sample.
            int maximum=(int)Math.Min(samples.Length-1,Math.Floor(boundary+1e-7));
            cursor2=Math.Max(cursor1,Math.Min(maximum,cursor2));
        }
        void NotifyMeasurement(){Invalidate();var handler=CursorsChanged;if(handler!=null)handler(this,EventArgs.Empty);}
        void ResetMeasurement(){cursor1=cursor2=activeCursor=-1;selectingRange=false;Capture=false;NotifyMeasurement();}
        internal void SetMeasurementCursors(int first,int second)
        {
            if(first<0||second<0||first>=samples.Length||second>=samples.Length)throw new ArgumentOutOfRangeException("first","Cursor indices must identify recorded samples.");
            selectingRange=false;Capture=false;
            cursor1=first;cursor2=second;NotifyMeasurement();
        }
        void EnsureMeasurementVisible()
        {
            if(samples.Length==0)return;
            bool firstInside=cursor1>=viewStart&&cursor1<=viewEnd,secondInside=cursor2>=viewStart&&cursor2<=viewEnd;
            if(!CursorsVisible||(!firstInside&&!secondInside)){
                cursor1=viewStart+(viewEnd-viewStart)/3;cursor2=viewStart+2*(viewEnd-viewStart)/3;
            }else{
                cursor1=Math.Max(viewStart,Math.Min(viewEnd,cursor1));cursor2=Math.Max(viewStart,Math.Min(viewEnd,cursor2));
            }
            ConstrainInteractiveReflection();NotifyMeasurement();
        }
        internal void ZoomToSamples(int start,int end)
        {
            if(start<0||end<start||end>=samples.Length)throw new ArgumentOutOfRangeException("start");
            selectingRange=false;Capture=false;
            int minimum=Math.Min(16,samples.Length);
            if(end-start+1<minimum){int mid=(start+end)/2;start=Math.Max(0,Math.Min(samples.Length-minimum,mid-minimum/2));end=start+minimum-1;}
            viewStart=start;viewEnd=end;EnsureMeasurementVisible();Invalidate();
        }
        int CursorAt(Point point)
        {
            if(!CursorsVisible||!PlotRectangle.Contains(point))return -1;
            double d1=Math.Abs(SampleX(cursor1,PlotRectangle)-point.X),d2=Math.Abs(SampleX(cursor2,PlotRectangle)-point.X);
            if(Math.Min(d1,d2)>8)return -1;
            return Math.Abs(d1-d2)<.5?(activeCursor==0?1:0):d1<d2?0:1;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Right){ResetView();return;}
            if(e.Button!=MouseButtons.Left||samples.Length<2||!PlotRectangle.Contains(e.Location))return;
            Focus();activeCursor=CursorAt(e.Location);Capture=true;
            if(activeCursor<0){selectingRange=true;selectionX=selectionEndX=e.X;}
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(Capture&&activeCursor>=0){
                int value=SampleAtX(e.X,PlotRectangle),oldFirst=cursor1,oldSecond=cursor2;
                if(activeCursor==0)cursor1=value;else cursor2=value;
                ConstrainInteractiveReflection();
                if(cursor1!=oldFirst||cursor2!=oldSecond)NotifyMeasurement();
            }else if(Capture&&selectingRange){selectionEndX=Math.Max(PlotRectangle.Left,Math.Min(PlotRectangle.Right,e.X));Invalidate();}
            else Cursor=CursorAt(e.Location)>=0?Cursors.SizeWE:Cursors.Cross;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);if(e.Button!=MouseButtons.Left)return;
            bool zoom=selectingRange;int first=SampleAtX(selectionX,PlotRectangle),last=SampleAtX(e.X,PlotRectangle);
            selectingRange=false;Capture=false;
            if(zoom&&Math.Abs(e.X-selectionX)>=6)ZoomToSamples(Math.Min(first,last),Math.Max(first,last));else Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {base.OnMouseCaptureChanged(e);if(!Capture){selectingRange=false;Invalidate();}}
        protected override void OnLostFocus(EventArgs e)
        {selectingRange=false;Capture=false;base.OnLostFocus(e);Invalidate();}
        void PaintMeasurement(Graphics g,Rectangle plot)
        {
            if(selectingRange){
                var rectangle=new Rectangle(Math.Min(selectionX,selectionEndX),plot.Top,Math.Abs(selectionX-selectionEndX),plot.Height);
                using(var fill=new SolidBrush(Color.FromArgb(38,0,123,160)))g.FillRectangle(fill,rectangle);
                using(var pen=new Pen(Color.FromArgb(0,112,151)){DashStyle=DashStyle.Dash})g.DrawRectangle(pen,rectangle);
            }
            if(!CursorsVisible)return;
            PaintReflectionBoundary(g,plot);
            Color[] colors={Color.FromArgb(0,148,182),Color.FromArgb(226,117,14)};
            int[] indices={cursor1,cursor2};
            for(int i=0;i<2;i++){
                float x=SampleX(indices[i],plot);if(x<plot.Left||x>plot.Right)continue;
                using(var pen=new Pen(colors[i],activeCursor==i?2F:1.3F){DashStyle=DashStyle.Dash})g.DrawLine(pen,x,plot.Top,x,plot.Bottom);
                using(var brush=new SolidBrush(colors[i]))g.FillPolygon(brush,new[]{new PointF(x-5,plot.Top),new PointF(x+5,plot.Top),new PointF(x,plot.Top+7)});
                int labelWidth=Math.Max(30,Font.Height*2);
                TextRenderer.DrawText(g,"T"+(i+1),Font,new Rectangle((int)Math.Max(plot.Left,Math.Min(plot.Right-labelWidth,x-labelWidth/2)),plot.Top+8,labelWidth,AxisLineHeight),colors[i],TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            }
            string time="T1="+(cursor1*1e6/sampleRate).ToString("0.###",CultureInfo.InvariantCulture)+" μs   T2="+(cursor2*1e6/sampleRate).ToString("0.###",CultureInfo.InvariantCulture)+" μs   |Δt|="+CursorDeltaNs.ToString("0.0",CultureInfo.InvariantCulture)+" ns";
            double position=CursorPositionM;
            string location=Finite(position)?L("Position: ","定位：")+position.ToString("0.0",CultureInfo.InvariantCulture)+" m":L("Position unavailable: ","定位不可用：")+MeasurementGeometryStatus;
            string velocity=MeasurementVelocityText==null?"":MeasurementVelocityText();
            if(!string.IsNullOrEmpty(velocity))location+="   "+velocity;
            location+=L("  (measurement only, not an analysis result)","  （仅测量，不写入分析结果）");
            const TextFormatFlags flags=TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g,time,HeaderFont,HeaderRow(2),Color.FromArgb(34,51,63),flags);
            TextRenderer.DrawText(g,location,HeaderFont,HeaderRow(3),Color.FromArgb(26,69,88),flags);
        }
        internal RectangleF ReflectionRangeRectangle(Rectangle plot)
        {
            if(!CursorsVisible)return RectangleF.Empty;
            double boundary=ReflectionBoundarySample;
            if(!Finite(boundary))return RectangleF.Empty;
            double first=Math.Max(cursor1,viewStart),last=Math.Min(boundary,viewEnd);
            if(last<=first)return RectangleF.Empty;
            float left=Math.Max(plot.Left,SampleX(first,plot)),right=Math.Min(plot.Right,SampleX(last,plot));
            return right>left?new RectangleF(left,plot.Top,right-left,plot.Height):RectangleF.Empty;
        }
        void PaintReflectionRange(Graphics g,Rectangle plot)
        {
            RectangleF area=ReflectionRangeRectangle(plot);
            if(area.IsEmpty)return;
            GraphicsState saved=g.Save();
            try{
                g.SetClip(plot,CombineMode.Intersect);
                using(var fill=new SolidBrush(Color.FromArgb(239,247,250)))g.FillRectangle(fill,area);
            }finally{g.Restore(saved);}
        }
        void PaintReflectionBoundary(Graphics g,Rectangle plot)
        {
            double boundary=ReflectionBoundarySample;
            if(!Finite(boundary))return;
            float actual=SampleX(boundary,plot);
            float x=Math.Max(plot.Left,Math.Min(plot.Right,actual));
            Color color=Color.FromArgb(72,122,139);
            int labelTop=plot.Top+8+AxisLineHeight;
            using(var pen=new Pen(color,1.2F){DashStyle=DashStyle.DashDot})g.DrawLine(pen,x,labelTop,x,plot.Bottom);
            string label=L("2L boundary","2L 往返边界")+(actual>plot.Right?" →":actual<plot.Left?" ←":"");
            var size=TextRenderer.MeasureText(g,label,Font);
            int left=(int)Math.Max(plot.Left,Math.Min(plot.Right-size.Width,x-size.Width/2));
            TextRenderer.DrawText(g,label,Font,new Rectangle(left,labelTop,size.Width,AxisLineHeight),color,BackColor,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter);
        }
    }
}
