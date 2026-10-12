using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.PdReview
{
    internal static class ReviewTheme
    {
        // Instrument-workstation palette: neutral data surfaces, restrained
        // primary actions and a separate active-state accent. Phase/data colors
        // remain semantic and are deliberately not recolored by this theme.
        internal static readonly Color TopBar=Color.FromArgb(11,45,77);
        internal static readonly Color Accent=Color.FromArgb(0,163,199);
        internal static readonly Color AccentHover=Color.FromArgb(0,137,170);
        internal static readonly Color Active=Accent;
        internal static readonly Color ActiveSoft=Color.FromArgb(234,242,248);
        internal static readonly Color Workspace=Color.FromArgb(243,247,251);
        internal static readonly Color Surface=Color.White;
        internal static readonly Color SurfaceAlt=Color.FromArgb(234,242,248);
        internal static readonly Color Border=Color.FromArgb(202,216,229);
        internal static readonly Color Grid=Color.FromArgb(233,239,244);
        internal static readonly Color Muted=Color.FromArgb(96,117,138);
        internal static readonly Color Disabled=Color.FromArgb(227,233,239);
        internal static readonly Color DisabledInk=Color.FromArgb(101,117,135);
        internal static readonly Color Ink=Color.FromArgb(24,51,77);
        internal static readonly Color SecondaryInk=Color.FromArgb(96,117,138);
        internal static readonly Color Danger=Color.FromArgb(196,71,71);
        internal static readonly Color Success=Color.FromArgb(35,138,103);
        internal static Font RegularFont(){return new Font("Microsoft YaHei UI",9.5F,FontStyle.Regular);}
        internal static void ApplyDialogIcon(Form form)
        {
            // Own a clone, never the process-wide Form default or an owner's icon.
            HVX.Presentation.OemUiBranding.Apply(form);
        }
        internal static void FitDialogToWorkingArea(Form form)
        {
            Rectangle area=Screen.FromControl(form.Owner??form).WorkingArea;
            int width=Math.Max(1,area.Width-24),height=Math.Max(1,area.Height-24);
            form.MinimumSize=new Size(Math.Min(form.MinimumSize.Width,width),Math.Min(form.MinimumSize.Height,height));
            form.Size=new Size(Math.Min(form.Width,width),Math.Min(form.Height,height));
            form.Location=new Point(Math.Max(area.Left,Math.Min(form.Left,area.Right-form.Width)),Math.Max(area.Top,Math.Min(form.Top,area.Bottom-form.Height)));
        }
        internal static void BindDialogFooter(Form form,FlowLayoutPanel footer)
        {
            bool arranging=false;
            Action fit=()=>{
                if(arranging||form.IsDisposed)return;arranging=true;
                try {
                    int available=Math.Max(1,footer.ClientSize.Width-footer.Padding.Horizontal),used=0,rowHeight=0,height=footer.Padding.Vertical;
                    footer.WrapContents=true;
                    foreach(Control item in footer.Controls) {
                        var button=item as Button;
                        if(button!=null){button.Width=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+28;button.Height=Math.Max(32,button.Font.Height+14);}
                        int width=item.Width+item.Margin.Horizontal,h=item.Height+item.Margin.Vertical;
                        if(used>0&&used+width>available){height+=rowHeight;used=0;rowHeight=0;}
                        used+=width;rowHeight=Math.Max(rowHeight,h);
                    }
                    footer.Height=Math.Max(40,height+rowHeight+4);
                } finally {arranging=false;}
            };
            foreach(Control item in footer.Controls){item.FontChanged+=(s,e)=>fit();item.TextChanged+=(s,e)=>fit();}
            footer.SizeChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();form.FontChanged+=(s,e)=>fit();fit();
        }
        internal static void ReserveDialogBody(Form form,Control footer,Size baseline)
        {
            bool arranging=false;
            Action fit=()=>{
                if(arranging||form.IsDisposed||!form.IsHandleCreated)return;arranging=true;
                try {
                    Rectangle area=Screen.FromControl(form.Owner??form).WorkingArea;
                    int minimumHeight=Math.Max(baseline.Height,form.Height-form.ClientSize.Height+form.Padding.Vertical+footer.Height+Math.Max(160,form.Font.Height*5));
                    form.MinimumSize=new Size(Math.Min(baseline.Width,Math.Max(1,area.Width-24)),Math.Min(minimumHeight,Math.Max(1,area.Height-24)));
                    FitDialogToWorkingArea(form);
                }finally{arranging=false;}
            };
            footer.SizeChanged+=(s,e)=>fit();form.FontChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();
        }
        internal static void ButtonRole(Button button,bool primary)
        {
            button.BackColor=primary?Accent:Surface;
            button.ForeColor=primary?Color.White:Ink;
            button.FlatAppearance.BorderColor=primary?Accent:Border;
        }
        internal static GraphicsPath Rounded(RectangleF r,float radius=4)
        {
            var p=new GraphicsPath();float d=Math.Max(1,Math.Min(radius*2,Math.Min(r.Width,r.Height)));
            p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);
            p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
        }
        internal static void Frame(Graphics g,Rectangle bounds,Color fill,Color border)
        { FrameWithRadius(g,bounds,fill,border,4f); }
        static void FrameWithRadius(Graphics g,Rectangle bounds,Color fill,Color border,float radius)
        {
            if(bounds.Width<2||bounds.Height<2)return;
            var state=g.Save();g.SmoothingMode=SmoothingMode.AntiAlias;
            try { using(var path=Rounded(new RectangleF(bounds.X+.5f,bounds.Y+.5f,bounds.Width-1,bounds.Height-1),radius))
            using(var brush=new SolidBrush(fill))using(var pen=new Pen(border))
            {g.FillPath(brush,path);g.DrawPath(pen,path);} } finally {g.Restore(state);}
        }
        internal static void Paint(Control c,Graphics g,Color border,bool hover,bool pressed)
        {
            g.Clear(c.Parent==null?Color.White:c.Parent.BackColor);
            Color fill=c.Enabled?c.BackColor:Disabled;
            bool neutral=c.BackColor.ToArgb()==Surface.ToArgb()||c.BackColor.ToArgb()==SurfaceAlt.ToArgb();
            if(c.Enabled&&pressed)fill=neutral?Color.FromArgb(221,233,242):ControlPaint.Dark(fill,.1f);
            else if(c.Enabled&&hover)fill=neutral?ActiveSoft:AccentHover;
            FrameWithRadius(g,c.ClientRectangle,fill,c.Enabled?(hover?Active:border):Border,c is Button?6f*Math.Max(.5f,g.DpiX/96f):4f);
            var r=Rectangle.Inflate(c.ClientRectangle,-2,-2);
            TextRenderer.DrawText(g,c.Text,c.Font,r,c.Enabled?c.ForeColor:DisabledInk,
                TextFormatFlags.NoPadding|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
        }
    }
    internal sealed class RoundedReviewButton:Button
    {
        bool hover,pressed;
        internal RoundedReviewButton(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnPaint(PaintEventArgs e){ReviewTheme.Paint(this,e.Graphics,FlatAppearance.BorderColor,hover,pressed);if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),ForeColor,BackColor);}
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){pressed=e.Button==MouseButtons.Left;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture){pressed=false;Invalidate();}base.OnMouseCaptureChanged(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space){pressed=true;Invalidate();}base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){if(e.KeyCode==Keys.Space){pressed=false;Invalidate();}base.OnKeyUp(e);}
        protected override void OnLostFocus(EventArgs e){pressed=false;Invalidate();base.OnLostFocus(e);}
        protected override void OnEnabledChanged(EventArgs e){if(!Enabled)hover=pressed=false;Invalidate();base.OnEnabledChanged(e);}
    }
    internal sealed class RoundedReviewToggle:CheckBox
    {
        internal RoundedReviewToggle(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnPaint(PaintEventArgs e){ReviewTheme.Paint(this,e.Graphics,FlatAppearance.BorderColor,false,false);if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),ForeColor,BackColor);}
    }
    internal sealed class RoundedReviewBadge:Label
    {
        internal RoundedReviewBadge(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);}
        protected override void OnPaint(PaintEventArgs e){ReviewTheme.Paint(this,e.Graphics,BackColor,false,false);}
    }
}
