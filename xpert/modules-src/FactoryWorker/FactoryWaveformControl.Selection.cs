using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryWaveformControl
    {
        internal event EventHandler SelectionChanged;
        internal bool HasSelection { get {return canvas.selectionEnabled;} }
        internal double SelectionStartMs {get{return canvas.selectionStart;}}
        internal double SelectionEndMs {get{return canvas.selectionEnd;}}
        internal double AutoScaleStartMs {get{return canvas.scaleStartMs;}}
        internal void ConfigureSelection(double firstZeroMs,double lastMs)
        { canvas.ConfigureSelection(firstZeroMs,lastMs); }
        internal void SetSelection(double firstMs,double lastMs)
        { canvas.SetSelection(firstMs,lastMs); }
        internal void SetThreshold(double thresholdPc,double baselinePc)
        {canvas.thresholdPc=thresholdPc;canvas.baselinePc=baselinePc;canvas.SceneChanged();}
        private sealed partial class FactoryWaveformCanvas
        {
            internal bool selectionEnabled;
            internal double selectionStart,selectionEnd,scaleStartMs,thresholdPc,baselinePc;
            private int selectionDrag;
            internal void ConfigureSelection(double start,double end)
            {
                selectionEnabled=Finite(start) && Finite(end) && start>=0 && end>start && end<=endMs+1e-9;
                if(selectionEnabled){scaleStartMs=start;selectionStart=start;selectionEnd=Math.Min(endMs,end);}
                else scaleStartMs=0;
                RebuildRanges();SceneChanged();
            }
            internal void SetSelection(double start,double end)
            {
                if(!selectionEnabled || !Finite(start) || !Finite(end) || start<0 || end<=start || end>endMs+1e-9)
                    throw new ArgumentOutOfRangeException("start");
                selectionStart=start;selectionEnd=Math.Min(end,endMs);SceneChanged();
                if(owner.SelectionChanged!=null)owner.SelectionChanged(owner,EventArgs.Empty);
            }
            private bool BeginSelectionDrag(int x)
            {
                if(!selectionEnabled)return false;
                double a=Plot.Left+(selectionStart-viewMin)/(viewMax-viewMin)*Plot.Width;
                double b=Plot.Left+(selectionEnd-viewMin)/(viewMax-viewMin)*Plot.Width;
                selectionDrag=Math.Abs(x-a)<=9?1:Math.Abs(x-b)<=9?2:0;
                return selectionDrag!=0;
            }
            private void MoveSelection(int x)
            {
                double t=Math.Max(0,Math.Min(endMs,viewMin+(x-Plot.Left)*(viewMax-viewMin)/Plot.Width));
                double gap=Math.Min(stepMs,Math.Min(selectionEnd,endMs-selectionStart)/2);
                if(selectionDrag==1)SetSelection(Math.Max(0,Math.Min(t,selectionEnd-gap)),selectionEnd);
                else SetSelection(selectionStart,Math.Min(endMs,Math.Max(t,selectionStart+gap)));
            }
            private void DrawSelection(Graphics g,Rectangle p,double min,double max)
            {
                if(!selectionEnabled)return;
                float a=p.Left+(float)((selectionStart-viewMin)/(viewMax-viewMin)*p.Width),b=p.Left+(float)((selectionEnd-viewMin)/(viewMax-viewMin)*p.Width);
                using(var fill=new SolidBrush(Color.FromArgb(25,70,150,210)))g.FillRectangle(fill,Math.Max(p.Left,a),p.Top,Math.Max(0,Math.Min(p.Right,b)-Math.Max(p.Left,a)),p.Height);
                using(var pen=new Pen(Color.RoyalBlue,1.5f))
                {pen.DashStyle=DashStyle.Dash;g.DrawLine(pen,a,p.Top,a,p.Bottom);g.DrawLine(pen,b,p.Top,b,p.Bottom);}
                DrawLabel(g,"A "+selectionStart.ToString("0.###"),new Rectangle((int)a+3,p.Top+2,100,Font.Height+3),Color.RoyalBlue);
                DrawLabel(g,"B "+selectionEnd.ToString("0.###"),new Rectangle((int)b-103,p.Top+2,100,Font.Height+3),Color.RoyalBlue,System.Windows.Forms.TextFormatFlags.Right);
                if(Finite(thresholdPc) && thresholdPc>0)using(var pen=new Pen(Color.FromArgb(150,Color.DarkOrange)))
                {pen.DashStyle=DashStyle.Dot;foreach(double v in new[]{baselinePc-thresholdPc,baselinePc+thresholdPc}){float y=ProjectCharge(v,p,min,max);g.DrawLine(pen,p.Left,y,p.Right,y);}}
            }
        }
    }
}
