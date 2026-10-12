using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal static class AppTheme
    {
        public static readonly Color Navy = Color.FromArgb(11, 45, 77);
        public static readonly Color Accent = Color.FromArgb(0, 163, 199);
        public static readonly Color AccentHover = Color.FromArgb(0, 137, 170);
        public static readonly Color Canvas = Color.FromArgb(243, 247, 251);
        public static readonly Color Surface = Color.White;
        public static readonly Color Border = Color.FromArgb(202, 216, 229);
        public static readonly Color Ink = Color.FromArgb(24, 51, 77);
        public static readonly Color Muted = Color.FromArgb(96, 117, 138);
        public static readonly Color Success = Color.FromArgb(35, 138, 103);
        public static readonly Color Danger = Color.FromArgb(196, 71, 71);
        public static readonly Color Warning = Color.FromArgb(209, 154, 18);

        public static Font Font(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
        }

        public static Button Button(string text, bool primary)
        {
            Button button = new AppThemeButton
            {
                Text = text,
                AutoSize = false,
                Height = 38,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Surface,
                ForeColor = primary ? Color.White : Ink,
                Font = Font(9.5f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Margin = new Padding(4)
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.BorderSize = 1;
            return button;
        }

        public static Icon LoadIcon()
        {
            return HVX.Presentation.OemUiBranding.LoadIcon();
        }

        public static Image LoadLogo()
        {
            return HVX.Presentation.OemUiBranding.LoadLogo();
        }

        public static void ApplyDialogIcon(Form form)
        {
            HVX.Presentation.OemUiBranding.Apply(form);
        }

        // Only callers with adaptive/scrollable bodies opt in. System dialogs
        // are not resized and their warning/error icons are not replaced.
        internal static void FitDialogToWorkingArea(Form form)
        {
            form.Shown += delegate { FitDialogToWorkingArea(form, Screen.FromControl(form.Owner ?? form).WorkingArea); };
            // Initial placement follows the owner; an explicitly moved dialog
            // must stay on its own monitor instead of snapping back to the owner.
            form.ResizeEnd += delegate { FitDialogToWorkingArea(form, Screen.FromControl(form).WorkingArea); };
        }
        internal static void FitDialogToWorkingArea(Form form, Rectangle work)
        {
            int width = System.Math.Max(1, work.Width - 16), height = System.Math.Max(1, work.Height - 16);
            form.MinimumSize = new Size(System.Math.Min(form.MinimumSize.Width, width), System.Math.Min(form.MinimumSize.Height, height));
            if (form.WindowState != FormWindowState.Normal) return;
            form.Size = new Size(System.Math.Min(form.Width, width), System.Math.Min(form.Height, height));
            form.Location = new Point(System.Math.Max(work.Left, System.Math.Min(form.Left, work.Right - form.Width)),
                System.Math.Max(work.Top, System.Math.Min(form.Top, work.Bottom - form.Height)));
        }
    }

    // Style only. The inherited Button keeps accessibility, focus, mnemonics,
    // keyboard activation, Click, DialogResult and the original layout contract.
    internal sealed class AppThemeButton : HVX.Presentation.R2RoundedButton
    {
        bool hover, pressed;
        internal AppThemeButton()
        { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true); }
        protected override void OnMouseEnter(System.EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(System.EventArgs e){hover=pressed=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnMouseDown(MouseEventArgs e){if(e.Button==MouseButtons.Left)pressed=true;Invalidate();base.OnMouseDown(e);}
        protected override void OnMouseUp(MouseEventArgs e){pressed=false;Invalidate();base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space)pressed=true;Invalidate();base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){pressed=false;Invalidate();base.OnKeyUp(e);}
        protected override void OnEnabledChanged(System.EventArgs e){if(!Enabled)hover=pressed=false;base.OnEnabledChanged(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            // Image/custom-flat variants retain the native renderer rather than
            // silently changing icon/text arrangement outside this small scope.
            if(Image!=null||FlatStyle!=FlatStyle.Flat){base.OnPaint(e);return;}
            Color canvas=AppTheme.Surface;
            for(Control p=Parent;p!=null;p=p.Parent)if(p.BackColor.A==255){canvas=p.BackColor;break;}
            e.Graphics.Clear(canvas);if(Width<3||Height<3)return;
            var state=e.Graphics.Save();
            try {
                e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                float scale=System.Math.Max(.5f,e.Graphics.DpiX/96f),line=System.Math.Min(scale,System.Math.Min(Width,Height)-1);
                RectangleF r=new RectangleF(line/2,line/2,Width-line,Height-line);
                float d=System.Math.Min(12*scale,System.Math.Min(r.Width,r.Height));
                Color face=!Enabled?Color.FromArgb(239,244,248):pressed?ControlPaint.Dark(BackColor,.04f):BackColor;
                Color configuredBorder=FlatAppearance.BorderColor;
                bool ordinaryBorder=configuredBorder==AppTheme.Border||configuredBorder==AppTheme.Accent;
                Color border=!Enabled?AppTheme.Border:hover&&ordinaryBorder?AppTheme.Accent:configuredBorder;
                Color ink=Enabled?ForeColor:AppTheme.Muted;
                using(var path=new GraphicsPath()) {
                    path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
                    path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();
                    using(var b=new SolidBrush(face))e.Graphics.FillPath(b,path);
                    if(FlatAppearance.BorderSize>0)using(var p=new Pen(border,line))e.Graphics.DrawPath(p,path);
                }
                TextFormatFlags flags=TextFormatFlags.PreserveGraphicsClipping|TextFormatFlags.EndEllipsis;
                flags|=TextAlign==ContentAlignment.TopLeft||TextAlign==ContentAlignment.TopCenter||TextAlign==ContentAlignment.TopRight?TextFormatFlags.Top:
                    TextAlign==ContentAlignment.BottomLeft||TextAlign==ContentAlignment.BottomCenter||TextAlign==ContentAlignment.BottomRight?TextFormatFlags.Bottom:TextFormatFlags.VerticalCenter;
                flags|=TextAlign==ContentAlignment.MiddleLeft||TextAlign==ContentAlignment.TopLeft||TextAlign==ContentAlignment.BottomLeft?TextFormatFlags.Left:
                    TextAlign==ContentAlignment.MiddleRight||TextAlign==ContentAlignment.TopRight||TextAlign==ContentAlignment.BottomRight?TextFormatFlags.Right:TextFormatFlags.HorizontalCenter;
                flags|=Text.Contains("\n")?TextFormatFlags.WordBreak:TextFormatFlags.SingleLine;
                if(!ShowKeyboardCues)flags|=TextFormatFlags.HidePrefix;
                TextRenderer.DrawText(e.Graphics,Text,Font,Rectangle.Inflate(ClientRectangle,-4,-2),ink,flags);
                if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-5,-5),ink,face);
            } finally { e.Graphics.Restore(state); }
        }
    }
}
