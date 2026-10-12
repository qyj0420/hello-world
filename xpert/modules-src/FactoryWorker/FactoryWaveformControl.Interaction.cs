using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryWaveformControl
    {
        private readonly CheckBox boxZoom = new CheckBox { Text="框选放大",Checked=true,AutoSize=true };
        private readonly CheckBox pulseWindow = new CheckBox { Text="开窗截取",AutoSize=true };
        internal event Action<double,double> PulseWindowRequested;
        private readonly NumericUpDown chargeRuler = new NumericUpDown { Minimum=0,Maximum=100000000,DecimalPlaces=3,Width=104,Increment=1 };
        private readonly ToolTip interactionTip = new ToolTip();
        private bool locationTools,updatingRuler;
        internal event EventHandler DisplayPreferenceChanged;
        internal bool BoxZoomEnabled {get{return boxZoom.Checked;}set{boxZoom.Checked=value;}}
        internal bool PulseWindowEnabled {get{return pulseWindow.Checked;}set{pulseWindow.Checked=value;}}
        internal decimal ChargeRulerMaximumPc
        {
            get{return (decimal)fixedChargeMaximumPc;}
            set
            {
                if(value<0 || value>chargeRuler.Maximum)throw new ArgumentOutOfRangeException("value");
                fixedChargeMaximumPc=(double)value;
                updatingRuler=true;try{chargeRuler.Value=value;}finally{updatingRuler=false;}
                canvas.ResetVerticalView();
                if(DisplayPreferenceChanged!=null)DisplayPreferenceChanged(this,EventArgs.Empty);
            }
        }
        internal void EnableLocationInteraction()
        {
            if(locationTools)return;locationTools=true;
            var smaller=new Button {Text="−",Width=28,Height=27,FlatStyle=FlatStyle.Flat};
            var larger=new Button {Text="+",Width=28,Height=27,FlatStyle=FlatStyle.Flat};
            var automatic=new Button {Text="自动",Width=46,Height=27,FlatStyle=FlatStyle.Flat};
            foreach(Control c in new Control[]{boxZoom,pulseWindow,new Label {Text="标尺 ±pC",AutoSize=true,Padding=new Padding(0,5,0,0)},chargeRuler,smaller,larger,automatic})
            {c.Margin=new Padding(3,2,2,2);tools.Controls.Add(c);}
            interactionTip.SetToolTip(boxZoom,"勾选：左键拖框局部放大；拖动A/B线仍调整脉冲选区。中键拖动平移；取消勾选后左键也可平移。右键/复位显示整段；Esc取消拖框。");
            interactionTip.SetToolTip(chargeRuler,"整段局放纵轴正负标尺，0为自动；只调整显示，不改硬件量程、阈值、ADC或标签。");
            interactionTip.SetToolTip(pulseWindow,"参考100+101校准信号过滤：左键拖选主峰范围，按3%峰值/16点静隙扩展A/B包络；再确认阈值提取。原始数据不改写。");
            interactionTip.SetToolTip(smaller,"标尺减半，波形纵向放大");interactionTip.SetToolTip(larger,"标尺加倍，波形纵向缩小");
            chargeRuler.ValueChanged+=(s,e)=>{if(!updatingRuler)ChargeRulerMaximumPc=chargeRuler.Value;};
            boxZoom.CheckedChanged+=(s,e)=>{canvas.CancelBoxZoom();if(DisplayPreferenceChanged!=null)DisplayPreferenceChanged(this,EventArgs.Empty);};
            pulseWindow.CheckedChanged+=(s,e)=>{canvas.CancelBoxZoom();if(pulseWindow.Checked)boxZoom.Checked=false;};
            boxZoom.CheckedChanged+=(s,e)=>{if(boxZoom.Checked)pulseWindow.Checked=false;};
            smaller.Click+=(s,e)=>ScaleChargeRuler(.5);larger.Click+=(s,e)=>ScaleChargeRuler(2);automatic.Click+=(s,e)=>ChargeRulerMaximumPc=0;
            Disposed+=(s,e)=>interactionTip.Dispose();
        }
        private void ScaleChargeRuler(double factor)
        {
            double minimum,maximum;canvas.GetChargeRange(out minimum,out maximum);
            ChargeRulerMaximumPc=(decimal)Math.Max(.001,Math.Min(100000000,Math.Max(Math.Abs(minimum),Math.Abs(maximum))*factor));
        }
        internal void ApplyBoxZoomForTest(Point first,Point last)
        {first.Offset(-canvas.Left,-canvas.Top);last.Offset(-canvas.Left,-canvas.Top);canvas.TestDragZoom(first,last);}
        internal double ChargeVisibleSpan {get{double a,b;canvas.GetChargeRange(out a,out b);return b-a;}}

        private sealed partial class FactoryWaveformCanvas
        {
            private bool boxDragging;
            private Point boxFirst,boxLast;
            internal void GetChargeRange(out double a,out double b){ChargeRange(out a,out b);}
            internal void ResetVerticalView(){verticalMin=0;verticalMax=1;SceneChanged();}
            internal void CancelBoxZoom(){boxDragging=false;Invalidate();}
            private Point ClampBoxPoint(Point point)
            {var p=Plot;return new Point(Math.Max(p.Left,Math.Min(p.Right,point.X)),Math.Max(p.Top,Math.Min(p.Bottom,point.Y)));}
            private Rectangle BoxRectangle {get{return Rectangle.FromLTRB(Math.Min(boxFirst.X,boxLast.X),Math.Min(boxFirst.Y,boxLast.Y),Math.Max(boxFirst.X,boxLast.X),Math.Max(boxFirst.Y,boxLast.Y));}}
            private void DrawBoxZoom(Graphics g)
            {
                if(!boxDragging)return;
                using(var fill=new SolidBrush(Color.FromArgb(38,Color.DarkOrange)))g.FillRectangle(fill,BoxRectangle);
                using(var line=new Pen(Color.DarkOrange,1)){line.DashStyle=DashStyle.Dash;g.DrawRectangle(line,BoxRectangle);}
            }
            private void FinishBoxZoom(Point at)
            {
                boxLast=ClampBoxPoint(at);var box=BoxRectangle;var p=Plot;boxDragging=false;
                if(box.Width<6 || !owner.pulseWindow.Checked && box.Height<6 || Count==0){Invalidate();return;}
                double span=viewMax-viewMin,first=viewMin+(box.Left-p.Left)*span/p.Width,last=viewMin+(box.Right-p.Left)*span/p.Width;
                if(last-first<Math.Min(endMs,stepMs*4)){Invalidate();return;}
                if(owner.pulseWindow.Checked)
                { if(owner.PulseWindowRequested!=null)owner.PulseWindowRequested(Math.Max(0,first),Math.Min(endMs,last));Invalidate();return; }
                double ySpan=verticalMax-verticalMin,top=verticalMin+(box.Top-p.Top)*ySpan/p.Height,bottom=verticalMin+(box.Bottom-p.Top)*ySpan/p.Height;
                if(bottom-top<1e-8)return;
                viewMin=Math.Max(0,first);viewMax=Math.Min(endMs,last);verticalMin=top;verticalMax=bottom;mouse=null;SceneChanged();
            }
            internal void TestDragZoom(Point first,Point last)
            {
                OnMouseDown(new MouseEventArgs(MouseButtons.Left,1,first.X,first.Y,0));
                OnMouseMove(new MouseEventArgs(MouseButtons.Left,0,last.X,last.Y,0));
                OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,last.X,last.Y,0));
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {base.OnKeyDown(e);if(e.KeyCode==Keys.Escape){CancelBoxZoom();dragging=false;selectionDrag=0;Capture=false;e.Handled=true;}}
        }
    }
}
