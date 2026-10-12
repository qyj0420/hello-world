using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HVX.ReportGenerator
{
    // Keep native expand/state-image hit testing and vertical scrolling, but never
    // move the checkbox gutter horizontally when a long file name is selected.
    internal sealed class ReportTreeView : TreeView
    {
        const int TvsNoHorizontalScroll = 0x8000;
        protected override CreateParams CreateParams
        {
            get { CreateParams parameters = base.CreateParams; parameters.Style |= TvsNoHorizontalScroll; return parameters; }
        }
        internal void RefreshItemMetrics()
        {
            int imageHeight = Math.Max(ImageList == null ? 0 : ImageList.ImageSize.Height,
                StateImageList == null ? 0 : StateImageList.ImageSize.Height);
            int padding = Math.Max(6, (int)Math.Ceiling(6 * DeviceDpi / 96.0));
            int wanted = Math.Min(32767, Math.Max((int)Math.Ceiling(27 * DeviceDpi / 96.0), Math.Max(Font.Height, imageHeight) + padding));
            if (ItemHeight != wanted) ItemHeight = wanted;
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); RefreshItemMetrics(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RefreshItemMetrics(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshItemMetrics(); }
    }

    internal sealed class ReportButton : Button
    {
        internal Color NormalBackColor = Color.White;
        internal Color NormalTextColor = Color.FromArgb(23, 59, 86);
        internal Color HoverBackColor = Color.FromArgb(233, 244, 250);
        internal Color PressedBackColor = Color.FromArgb(233, 239, 244);
        internal Color ButtonBorderColor = Color.FromArgb(216, 226, 234);
        internal Color DisabledBackColor = Color.FromArgb(242, 245, 248);
        internal Color DisabledTextColor = Color.FromArgb(97, 116, 136);
        bool mouseOver, mouseDown;

        internal ReportButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { mouseOver = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { mouseOver = false; mouseDown = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) mouseDown = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { mouseDown = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color back = !Enabled ? DisabledBackColor : mouseDown ? PressedBackColor : mouseOver ? HoverBackColor : NormalBackColor;
            Color fore = Enabled ? NormalTextColor : DisabledTextColor;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent == null ? Color.White : Parent.BackColor);
            using (GraphicsPath path = RoundedRectangle(new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1.5f), Math.Max(1, Height - 1.5f)), 6f * Math.Max(.5f, e.Graphics.DpiX / 96f)))
            using (SolidBrush fill = new SolidBrush(back))
            using (Pen pen = new Pen(Enabled ? ButtonBorderColor : DisabledBackColor, 1f))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(pen, path);
            }
            Rectangle textBounds = Rectangle.Inflate(ClientRectangle, -6, -2);
            TextRenderer.DrawText(e.Graphics, Text ?? string.Empty, Font, textBounds, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues && Enabled)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), fore, back);
        }

        static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180f, 90f);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270f, 90f);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Shared industrial-instrument palette, aligned with ProjectDataBrowserControl.</summary>
    internal static class ReportTheme
    {
        internal static void ApplyDialogIcon(Form form)
        {
            HVX.Presentation.OemUiBranding.Apply(form);
        }
        internal static void BindDialogFooter(Form form,FlowLayoutPanel footer,Action<int> reserveHeight=null)
        {
            bool arranging=false;
            Action fit=()=>{
                if(arranging||form.IsDisposed)return;arranging=true;
                try {
                    int available=Math.Max(1,footer.ClientSize.Width-footer.Padding.Horizontal),used=0,rowHeight=0,height=footer.Padding.Vertical;
                    footer.WrapContents=true;
                    foreach(Control item in footer.Controls){
                        var button=item as Button;
                        if(button!=null){button.Width=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+28;button.Height=Math.Max(34,button.Font.Height+14);}
                        int width=item.Width+item.Margin.Horizontal,h=item.Height+item.Margin.Vertical;
                        if(used>0&&used+width>available){height+=rowHeight;used=0;rowHeight=0;}used+=width;rowHeight=Math.Max(rowHeight,h);
                    }
                    height+=rowHeight+4;if(reserveHeight!=null)reserveHeight(height);footer.Height=height;
                }finally{arranging=false;}
            };
            footer.SizeChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();foreach(Control item in footer.Controls){item.FontChanged+=(s,e)=>fit();item.TextChanged+=(s,e)=>fit();}fit();
        }
        internal static void ReserveDialogBody(Form form,Control footer,Size baseline)
        {
            bool arranging=false;
            Action fit=()=>{
                if(arranging||form.IsDisposed||!form.IsHandleCreated)return;arranging=true;
                try {
                    Rectangle area=Screen.FromControl(form.Owner??form).WorkingArea;
                    int width=Math.Max(1,area.Width-24),height=Math.Max(1,area.Height-24);
                    int minimumHeight=Math.Max(baseline.Height,form.Height-form.ClientSize.Height+form.Padding.Vertical+footer.Height+Math.Max(160,form.Font.Height*5));
                    form.MinimumSize=new Size(Math.Min(baseline.Width,width),Math.Min(minimumHeight,height));
                    form.Size=new Size(Math.Min(form.Width,width),Math.Min(form.Height,height));
                    form.Location=new Point(Math.Max(area.Left,Math.Min(form.Left,area.Right-form.Width)),Math.Max(area.Top,Math.Min(form.Top,area.Bottom-form.Height)));
                }finally{arranging=false;}
            };
            footer.SizeChanged+=(s,e)=>fit();form.FontChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();form.DpiChanged+=(s,e)=>fit();
        }
        internal static readonly Color Navy = Color.FromArgb(11, 45, 77);
        internal static readonly Color Header = Color.FromArgb(234, 242, 248);
        internal static readonly Color Primary = Color.FromArgb(0, 163, 199);
        internal static readonly Color PrimaryHover = Color.FromArgb(0, 137, 170);
        internal static readonly Color Cyan = Color.FromArgb(0, 163, 199);
        internal static readonly Color TopBar = Color.FromArgb(11, 45, 77);
        internal static readonly Color SectionBar = Color.FromArgb(234, 242, 248);
        internal static readonly Color DisabledPrimary = Color.FromArgb(227, 233, 239);
        internal static readonly Color Canvas = Color.FromArgb(243, 247, 251);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color SurfaceAlt = Color.FromArgb(234, 242, 248);
        internal static readonly Color Border = Color.FromArgb(202, 216, 229);
        internal static readonly Color Grid = Color.FromArgb(233, 239, 244);
        internal static readonly Color Ink = Color.FromArgb(24, 51, 77);
        internal static readonly Color Muted = Color.FromArgb(96, 117, 138);
        internal static readonly Color Selection = Color.FromArgb(0, 163, 199);
        internal static readonly Color Disabled = Color.FromArgb(227, 233, 239);
        internal static readonly Color Error = Color.FromArgb(196, 71, 71);
        internal static readonly Color Success = Color.FromArgb(35, 138, 103);
        internal static readonly Color Warning = Color.FromArgb(217, 162, 37);
        internal static readonly Color PhaseA = Color.FromArgb(217, 162, 37);
        internal static readonly Color PhaseB = Color.FromArgb(31, 120, 102);
        internal static readonly Color PhaseC = Color.FromArgb(175, 68, 59);

        internal static Font UiFont(float size, FontStyle style)
        {
            try { return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); }
            catch { return SystemFonts.MessageBoxFont; }
        }

        internal static void StylePrimaryButton(Button button)
        {
            StyleButton(button);
            button.BackColor = Primary;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Primary;
            button.FlatAppearance.MouseOverBackColor = PrimaryHover;
            button.FlatAppearance.MouseDownBackColor = Navy;
            ReportButton themed = button as ReportButton;
            if (themed != null)
            {
                themed.NormalBackColor = Primary; themed.NormalTextColor = Color.White;
                themed.HoverBackColor = PrimaryHover; themed.PressedBackColor = Navy;
                themed.ButtonBorderColor = Primary; themed.DisabledBackColor = DisabledPrimary;
                themed.DisabledTextColor = Muted;
            }
        }

        internal static void StyleSecondaryButton(Button button)
        {
            StyleButton(button);
            button.BackColor = Surface;
            button.ForeColor = Navy;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = SectionBar;
            button.FlatAppearance.MouseDownBackColor = Grid;
            ReportButton themed = button as ReportButton;
            if (themed != null)
            {
                themed.NormalBackColor = Surface; themed.NormalTextColor = Navy;
                themed.HoverBackColor = SectionBar; themed.PressedBackColor = Grid;
                themed.ButtonBorderColor = Border; themed.DisabledBackColor = SurfaceAlt;
                themed.DisabledTextColor = Muted;
            }
        }

        internal static void StyleButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.AutoEllipsis = true;
            button.FlatAppearance.BorderSize = 1;
            button.Cursor = Cursors.Hand;
            button.Font = UiFont(9.5f, FontStyle.Regular);
            button.Height = 34;
            button.Padding = new Padding(10, 0, 10, 0);
            button.UseVisualStyleBackColor = false;
        }

        internal static void StyleTree(TreeView tree)
        {
            tree.BorderStyle = BorderStyle.None;
            tree.BackColor = Surface;
            tree.ForeColor = Ink;
            tree.LineColor = Border;
            tree.FullRowSelect = false;
            tree.HideSelection = false;
            tree.ShowNodeToolTips = true;
            tree.Font = UiFont(9.5f, FontStyle.Regular);
            // Native text, checkboxes and scroll blits must share the same clip and
            // row coordinates. OwnerDrawText used a full-width label outside the
            // native label's update region and left duplicated glyphs after scroll.
            // Availability colours are already applied to each node by the model.
            tree.DrawMode = TreeViewDrawMode.Normal;
            var measured = tree as ReportTreeView;
            if (measured != null) measured.RefreshItemMetrics();
            else tree.ItemHeight = Math.Max(27, tree.Font.Height + 6);
        }

        internal static void DrawCardBorder(object sender, PaintEventArgs e)
        {
            Control control = sender as Control;
            if (control == null || control.Width < 2 || control.Height < 2) return;
            using (Pen pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        }

        internal static void DrawHeadingAccent(object sender, PaintEventArgs e)
        {
            Control control = sender as Control;
            if (control == null) return;
            using (SolidBrush brush = new SolidBrush(Cyan)) e.Graphics.FillRectangle(brush, 0, 0, 3, control.Height);
        }

        internal static Bitmap BuildPlaceholder(Size size, string title, string detail)
        {
            int width = Math.Max(320, size.Width);
            int height = Math.Max(180, size.Height);
            Bitmap bitmap = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Pen grid = new Pen(Grid, 1.0f))
            using (Pen axis = new Pen(Border, 1.4f))
            using (Brush ink = new SolidBrush(Ink))
            using (Brush muted = new SolidBrush(Muted))
            using (Font titleFont = UiFont(12.0f, FontStyle.Bold))
            using (Font detailFont = UiFont(9.0f, FontStyle.Regular))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Surface);
                Rectangle plot = new Rectangle(52, 52, width - 84, height - 92);
                for (int x = plot.Left; x <= plot.Right; x += Math.Max(40, plot.Width / 8)) graphics.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                for (int y = plot.Top; y <= plot.Bottom; y += Math.Max(30, plot.Height / 5)) graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                graphics.DrawLine(axis, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
                graphics.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);
                graphics.DrawString(title ?? string.Empty, titleFont, ink, new PointF(18, 15));
                graphics.DrawString(detail ?? string.Empty, detailFont, muted, new RectangleF(18, height - 28, width - 36, 20));
            }
            return bitmap;
        }
    }
}
