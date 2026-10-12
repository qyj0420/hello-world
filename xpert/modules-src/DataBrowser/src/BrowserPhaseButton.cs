using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.Controls
{
    // A real check box preserves keyboard and accessibility toggle semantics.
    internal sealed class BrowserPhaseButton : CheckBox
    {
        internal Color PhaseColor { get; set; }
        bool hovering;
        internal BrowserPhaseButton()
        {
            AutoSize=false;Appearance=Appearance.Button;Size=new Size(76,32);
            Font=new Font("Arial",10f);FlatStyle=FlatStyle.Flat;Cursor=Cursors.Hand;
            DoubleBuffered=true;SetStyle(ControlStyles.UserPaint|ControlStyles.ResizeRedraw,true);
        }
        void FitLabel()
        {
            Size text = TextRenderer.MeasureText(Text, Font);
            Size = new Size(Math.Max(76, text.Width + 24), Math.Max(32, text.Height + 8));
        }
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);FitLabel();}
        protected override void OnTextChanged(EventArgs e){base.OnTextChanged(e);FitLabel();}
        protected override void OnCheckedChanged(EventArgs e){base.OnCheckedChanged(e);Invalidate();}
        protected override void OnMouseEnter(EventArgs e){hovering=true;base.OnMouseEnter(e);Invalidate();}
        protected override void OnMouseLeave(EventArgs e){hovering=false;base.OnMouseLeave(e);Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.Clear(Parent==null?BrowserTheme.Surface:Parent.BackColor);
            if(Width<2||Height<2)return;
            g.SmoothingMode=SmoothingMode.AntiAlias;
            var rect=new RectangleF(.5f,.5f,Width-1f,Height-1f);
            float scale=e.Graphics.DpiX/96f;
            float diameter=Math.Min(12f*scale,Math.Min(rect.Width,rect.Height));
            using(var path=new GraphicsPath())
            {
                path.AddArc(rect.Left,rect.Top,diameter,diameter,180,90);
                path.AddArc(rect.Right-diameter,rect.Top,diameter,diameter,270,90);
                path.AddArc(rect.Right-diameter,rect.Bottom-diameter,diameter,diameter,0,90);
                path.AddArc(rect.Left,rect.Bottom-diameter,diameter,diameter,90,90);path.CloseFigure();
                Color fill=!Enabled?BrowserTheme.Canvas:Checked?PhaseColor:BrowserTheme.Surface;
                if(Enabled&&(hovering||Capture))fill=ControlPaint.Light(fill,Checked?.10f:.02f);
                using(var brush=new SolidBrush(fill))g.FillPath(brush,path);
                using(var border=new Pen(Enabled?PhaseColor:BrowserTheme.Border,scale))g.DrawPath(border,path);
            }
            Color ink=!Enabled?BrowserTheme.Muted:Checked?(PhaseColor.R>180&&PhaseColor.G>100?BrowserTheme.Ink:Color.White):BrowserTheme.Ink;
            var textBounds=new Rectangle(Checked?15:1,0,Width-(Checked?16:2),Height);
            TextRenderer.DrawText(g,Text,Font,textBounds,ink,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Checked)using(var tick=new Pen(ink,1.7f)){float y=Height/2f;g.DrawLines(tick,new[]{new PointF(8,y),new PointF(11,y+3),new PointF(17,y-4)});}
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,Rectangle.Inflate(ClientRectangle,-5,-5),ink,Checked?PhaseColor:BrowserTheme.Surface);
        }
    }
}
