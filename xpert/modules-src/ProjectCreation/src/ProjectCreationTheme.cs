using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal static class ProjectCreationTheme
    {
        internal static readonly Color Accent = Color.FromArgb(11, 45, 77);
        internal static readonly Color Active = Color.FromArgb(0, 163, 199);
        internal static readonly Color ActiveHover = Color.FromArgb(0, 137, 170);
        internal static readonly Color ActiveSoft = Color.FromArgb(234, 247, 251);
        internal static readonly Color Workspace = Color.FromArgb(243, 247, 251);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color SurfaceAlt = Color.FromArgb(234, 242, 248);
        internal static readonly Color Border = Color.FromArgb(202, 216, 229);
        internal static readonly Color Grid = Color.FromArgb(228, 236, 243);
        internal static readonly Color Muted = Color.FromArgb(239, 244, 248);
        internal static readonly Color DisabledInk = Color.FromArgb(105, 122, 137);
        internal static readonly Color Ink = Color.FromArgb(24, 51, 77);
        internal static readonly Color SecondaryInk = Color.FromArgb(96, 117, 138);
        internal static readonly Color Success = Color.FromArgb(35, 138, 103);
        internal static readonly Color Warning = Color.FromArgb(195, 152, 52);
        internal static readonly Color Danger = Color.FromArgb(196, 71, 71);
        internal static readonly Color PhaseA = Color.FromArgb(209, 154, 18);
        internal static readonly Color PhaseB = Color.FromArgb(43, 154, 104);
        internal static readonly Color PhaseC = Color.FromArgb(207, 77, 81);
        internal static readonly Color InputBorder = Color.FromArgb(202, 216, 229);

        // Fixed process-lifetime UI font set, like SystemFonts: never grows with
        // controls, refreshes or polling. Temporary paint fonts still use Font().
        static readonly Font UiRegular = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
        internal static readonly Font ComfortableInputFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
        static readonly Font UiBold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        static readonly Font UiSection = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        static readonly Font UiTitle = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
        internal static Font UiFont(float size, FontStyle style = FontStyle.Regular)
        {
            if (size >= 12f) return UiTitle;
            if (size >= 10.5f) return UiSection;
            return style == FontStyle.Bold ? UiBold : UiRegular;
        }

        internal static Font Font(float size, FontStyle style = FontStyle.Regular)
        {
            return new Font("Microsoft YaHei UI", Math.Max(9.5f, size), style, GraphicsUnit.Point);
        }

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Color PhaseColor(string phase)
        {
            if (phase == "B相") return PhaseB;
            if (phase == "C相") return PhaseC;
            return PhaseA;
        }
    }

    internal sealed class ProjectCreationHeader : Panel
    {
        internal ProjectCreationHeader()
        {
            DoubleBuffered = true;
            BackColor = ProjectCreationTheme.Accent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(ProjectCreationTheme.Accent);
            using (SolidBrush brush = new SolidBrush(ProjectCreationTheme.Active))
                e.Graphics.FillRectangle(brush, 0, Math.Max(0, Height - 3), Width, 3);
        }
    }

    internal class ProjectCreationButton : Button
    {
        bool hover;
        bool pressed;
        internal bool Primary { get; set; }
        internal bool Danger { get; set; }
        internal bool Quiet { get; set; }

        internal ProjectCreationButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Height = 34;
            Font = ProjectCreationTheme.UiFont(9.5f);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = ProjectCreationTheme.Surface;
            Color ink = ProjectCreationTheme.Ink;
            Color border = ProjectCreationTheme.Border;
            if (!Enabled)
            {
                fill = ProjectCreationTheme.Muted;
                ink = ProjectCreationTheme.DisabledInk;
            }
            else if (Quiet)
            {
                fill = Parent == null ? ProjectCreationTheme.Surface : Parent.BackColor;
                ink = ProjectCreationTheme.Active;
                border = fill;
                if (hover) fill = ProjectCreationTheme.ActiveSoft;
            }
            else if (Danger)
            {
                fill = hover ? Color.FromArgb(251, 238, 239) : ProjectCreationTheme.Surface;
                ink = ProjectCreationTheme.Danger;
                border = hover ? ProjectCreationTheme.Danger : ProjectCreationTheme.Border;
            }
            else if (Primary)
            {
                fill = hover ? ProjectCreationTheme.ActiveHover : ProjectCreationTheme.Active;
                ink = Color.White;
                border = fill;
            }
            else if (hover)
            {
                fill = ProjectCreationTheme.ActiveSoft;
                border = ProjectCreationTheme.Active;
            }
            if (pressed && Enabled) fill = ControlPaint.Dark(fill, 0.07f);
            RectangleF bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = ProjectCreationTheme.Rounded(bounds, 6f * Math.Max(.5f, e.Graphics.DpiX / 96f)))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(border))
            {
                e.Graphics.FillPath(brush, path);
                e.Graphics.DrawPath(pen, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), ink, fill);
        }
    }

    internal sealed class ProjectCreationTabButton : ProjectCreationButton
    {
        bool selected;
        bool hover;

        internal bool Selected
        {
            get { return selected; }
            set { if (selected == value) return; selected = value; Invalidate(); }
        }

        internal ProjectCreationTabButton()
        {
            Height = 36;
            Font = ProjectCreationTheme.UiFont(9.5f);
            AccessibleRole = AccessibleRole.PageTab;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = selected ? ProjectCreationTheme.Surface : hover ? ProjectCreationTheme.ActiveSoft : ProjectCreationTheme.SurfaceAlt;
            Color border = selected ? ProjectCreationTheme.Border : ProjectCreationTheme.Border;
            using (GraphicsPath outline = new GraphicsPath())
            {
                float right = Math.Max(1, Width - 1.5f);
                float bottom = Math.Max(1, Height - 0.5f);
                outline.StartFigure();
                outline.AddLine(0.5f, bottom, 0.5f, 6f);
                outline.AddArc(0.5f, 0.5f, 10f, 10f, 180f, 90f);
                outline.AddLine(5.5f, 0.5f, right - 5f, 0.5f);
                outline.AddArc(right - 10f, 0.5f, 10f, 10f, 270f, 90f);
                outline.AddLine(right, 5.5f, right, bottom);
                outline.CloseFigure();
                using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, outline);
                using (Pen pen = new Pen(border)) e.Graphics.DrawPath(pen, outline);
            }
            if (selected)
            {
                using (SolidBrush white = new SolidBrush(ProjectCreationTheme.Surface))
                    e.Graphics.FillRectangle(white, 1, Height - 2, Math.Max(0, Width - 2), 2);
                using (SolidBrush accent = new SolidBrush(ProjectCreationTheme.Active))
                    e.Graphics.FillRectangle(accent, 1, 1, Math.Max(0, Width - 2), 3);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                selected ? ProjectCreationTheme.ActiveHover : ProjectCreationTheme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -5));
        }
    }

    internal sealed class ProjectCreationChoiceBox : ComboBox
    {
        internal ProjectCreationChoiceBox()
        {
            FlatStyle = FlatStyle.Flat;
        }
    }

    internal sealed class ProjectCreationInputFrame : Panel
    {
        internal ProjectCreationInputFrame()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = ProjectCreationTheme.Surface;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle border = new Rectangle(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            using (Pen pen = new Pen(ProjectCreationTheme.InputBorder))
                e.Graphics.DrawRectangle(pen, border);
        }
    }

    internal sealed class ProjectCreationNumberBox : NumericUpDown
    {
        const int EditSetMargins = 0x00D3;
        const int EditMarginsBoth = 0x0003;
        Control layoutParent;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, IntPtr value);

        internal ProjectCreationNumberBox()
        {
            BorderStyle = BorderStyle.FixedSingle;
        }

        protected override void OnParentChanged(EventArgs e)
        {
            if (layoutParent != null) layoutParent.Layout -= ParentLayout;
            base.OnParentChanged(e);
            layoutParent = Parent;
            if (layoutParent != null) layoutParent.Layout += ParentLayout;
            FitToParentWidth();
        }

        void ParentLayout(object sender, LayoutEventArgs e)
        {
            FitToParentWidth();
        }

        void FitToParentWidth()
        {
            if (Parent == null || Dock != DockStyle.Fill || Parent.ClientSize.Width <= 0) return;
            int width = Math.Max(1, Parent.ClientSize.Width - Margin.Horizontal);
            if (Width != width) Width = width;
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (Parent != null && Dock == DockStyle.Fill && Parent.ClientSize.Width > 0)
                width = Math.Min(width, Math.Max(1, Parent.ClientSize.Width - Margin.Horizontal));
            base.SetBoundsCore(x, y, width, height, specified);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            foreach (Control child in Controls)
            {
                if (child.GetType().Name.IndexOf("Buttons", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    child.Visible = false;
                    child.Width = 0;
                }
                else
                {
                    child.Dock = DockStyle.Fill;
                    child.Margin = Padding.Empty;
                    // NumericUpDown's native edit normally reserves its final
                    // pixels for the spin buttons. Keep a small text inset even
                    // after those buttons are hidden so the final digit stays
                    // clear of the border at compact field widths.
                    int margins = 3 | (8 << 16);
                    SendMessage(child.Handle, EditSetMargins, new IntPtr(EditMarginsBoth), new IntPtr(margins));
                }
            }
            FitToParentWidth();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && layoutParent != null)
            {
                layoutParent.Layout -= ParentLayout;
                layoutParent = null;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ProjectCreationDeviceStateLabel : Label
    {
        bool ready = true;

        internal bool Ready
        {
            get { return ready; }
            set { if (ready != value) { ready = value; Invalidate(); } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            string state = (Text ?? string.Empty).TrimStart();
            if (state.StartsWith("●", StringComparison.Ordinal))
                state = state.Substring(1).TrimStart();

            TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Size dotSize = TextRenderer.MeasureText(e.Graphics, "●", Font, Size.Empty, flags);
            Size textSize = TextRenderer.MeasureText(e.Graphics, state, Font, Size.Empty, flags);
            int gap = 6;
            int totalWidth = dotSize.Width + gap + textSize.Width;
            int x = Math.Max(0, ClientSize.Width - totalWidth);
            int y = Math.Max(0, (ClientSize.Height - Math.Max(dotSize.Height, textSize.Height)) / 2);
            TextRenderer.DrawText(e.Graphics, "●", Font, new Point(x, y), ready ? ProjectCreationTheme.Success : ProjectCreationTheme.Muted, flags);
            TextRenderer.DrawText(e.Graphics, state, Font, new Point(x + dotSize.Width + gap, y), ForeColor, flags);
        }
    }

    internal sealed partial class CableTopologyCanvas : Control
    {
        Image capacitorPhoto;
        internal string CableMode = ProjectModes.ThreeCore;
        internal double LengthA;
        internal double LengthB;
        internal double LengthC;
        internal string StartName = "始端";
        internal string EndName = "终端";
        internal string StartType="户内冷缩",EndType="户内冷缩";
        internal Rectangle StartNameBounds, EndNameBounds;
        internal string SelectedPhase = "A相";
        internal int LanguageIndex;
        internal IEnumerable<JointData> Joints = Enumerable.Empty<JointData>();

        internal bool IsCapacitorMode { get { return CableMode == ProjectModes.Capacitor; } }
        internal int RenderedModelCount { get { return CableMode == ProjectModes.ThreePhaseSingleCore ? 3 : 1; } }
        internal string[] RenderedLengthTexts
        {
            get
            {
                if (IsCapacitorMode) return new string[0];
                if (CableMode == ProjectModes.ThreePhaseSingleCore)
                    return new[] { ProjectText.LengthMetres(LengthA), ProjectText.LengthMetres(LengthB), ProjectText.LengthMetres(LengthC) };
                return new[] { ProjectText.LengthMetres(LengthA) };
            }
        }

        internal CableTopologyCanvas()
        {
            using (var resource = typeof(CableTopologyCanvas).Assembly.GetManifestResourceStream("XIONGXIN.Capacitor.png"))
                if (resource != null)
                    using (Image original = Image.FromStream(resource)) capacitorPhoto = new Bitmap(original);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = ProjectCreationTheme.Surface;
            MinimumSize = new Size(260, 110);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && capacitorPhoto != null) { capacitorPhoto.Dispose(); capacitorPhoto = null; }
            if (disposing) jointToolTip.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            renderedJointMarkers.Clear();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            DrawEndpointNames(e.Graphics);
            if (IsCapacitorMode)
            {
                DrawCapacitor(e.Graphics);
                return;
            }

            int left = 104;
            int right = Math.Max(left + 150, Width - 126);
            int top = Math.Max(8, Math.Max(StartNameBounds.Bottom, EndNameBounds.Bottom) + 12);
            int bottom = Math.Max(top + 120, Height + 28);
            if (CableMode == ProjectModes.ThreePhaseSingleCore)
            {
                for (int index = 0; index < ProjectModes.Phases.Length; index++)
                {
                    string phase = ProjectModes.Phases[index];
                    int y = top + (index + 1) * (bottom - top) / 4;
                    DrawCableRow(e.Graphics, phase, PhaseLength(phase), left, right, y, false);
                }
            }
            else if (CableMode == ProjectModes.SinglePhase)
            {
                DrawCableRow(e.Graphics, "A相", LengthA, left, right, top + (bottom - top) / 2, false);
            }
            else
            {
                DrawCableRow(e.Graphics, "A相", LengthA, left, right, top + (bottom - top) / 2, true);
            }
        }

        double PhaseLength(string phase)
        {
            if (phase == "B相") return LengthB;
            if (phase == "C相") return LengthC;
            return LengthA;
        }

        string EndpointCaption(bool isStart)
        {
            string name = isStart ? StartName : EndName;
            string caption = string.IsNullOrWhiteSpace(name) ? ProjectCreationLocalization.T(LanguageIndex, isStart ? "始端" : "终端") : name;
            if (!string.IsNullOrWhiteSpace(name)) caption += Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex, isStart ? " · 始端" : " · 终端", isStart ? " · Start" : " · End");
            if (IsCapacitorMode)
            {
                if (isStart && string.Equals(caption, "设备侧", StringComparison.OrdinalIgnoreCase))
                    caption = ProjectCreationLocalization.TranslateCanonical(LanguageIndex, "设备侧");
                if (!isStart && caption.StartsWith("电容侧-", StringComparison.OrdinalIgnoreCase))
                    caption = ProjectCreationLocalization.TranslateCanonical(LanguageIndex, "电容侧") + caption.Substring(3);
            }
            return caption;
        }

        internal int MeasureContentHeight(int availableWidth)
        {
            using (Font font = ProjectCreationTheme.Font(8.1f, FontStyle.Bold))
            {
                int half = Math.Max(1, (availableWidth - 40) / 2);
                var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                int namesHeight = Math.Max(TextRenderer.MeasureText(EndpointCaption(true), font, new Size(half, Int32.MaxValue), flags).Height,
                    TextRenderer.MeasureText(EndpointCaption(false), font, new Size(half, Int32.MaxValue), flags).Height);
                return namesHeight + (CableMode == ProjectModes.ThreePhaseSingleCore ? 270 : 140);
            }
        }

        void DrawEndpointNames(Graphics graphics)
        {
            string start = EndpointCaption(true), end = EndpointCaption(false);
            using (Font font = ProjectCreationTheme.Font(8.1f, FontStyle.Bold))
            {
                int half = Math.Max(1, (Width - 40) / 2);
                TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                Size startSize = TextRenderer.MeasureText(graphics, start, font, new Size(half, Int32.MaxValue), flags);
                Size endSize = TextRenderer.MeasureText(graphics, end, font, new Size(half, Int32.MaxValue), flags);
                int nameHeight = Math.Max(startSize.Height, endSize.Height);
                StartNameBounds = new Rectangle(12, 8, half, nameHeight);
                EndNameBounds = new Rectangle(Width - half - 12, 8, half, nameHeight);
                // Content height is measured by the surrounding scroll viewport
                // before painting. Painting must not enlarge a docked control.
                TextRenderer.DrawText(graphics, start, font, StartNameBounds, ProjectCreationTheme.SecondaryInk, flags | TextFormatFlags.Left);
                TextRenderer.DrawText(graphics, end, font, EndNameBounds, ProjectCreationTheme.SecondaryInk, flags | TextFormatFlags.Right);
            }
        }

        void DrawCableRow(Graphics graphics, string phase, double length, int left, int right, int y, bool integrated)
        {
            Color phaseColor = ProjectCreationTheme.PhaseColor(phase);
            // An integrated three-core cable is one neutral assembly.  Its
            // internal conductors carry the canonical yellow/green/red phase
            // colors, while the shared label, joints and terminations remain
            // neutral instead of inheriting L1's color.
            Color modelColor = integrated ? ProjectCreationTheme.SecondaryInk : phaseColor;
            int cableLeft = left + 22;
            int cableRight = right - 22;
            HVX.ProjectCables.CableVisualStyle.DrawBody(graphics,cableLeft,cableRight,y,integrated,phaseColor);
            HVX.ProjectCables.CableVisualStyle.DrawTermination(graphics,cableLeft,y,-1,StartType);
            HVX.ProjectCables.CableVisualStyle.DrawTermination(graphics,cableRight,y,1,EndType);

            string phaseText = integrated
                ? "ALL"
                : ProjectCreationLocalization.TranslateCanonical(LanguageIndex, phase);
            using (Font phaseFont = ProjectCreationTheme.Font(8.7f, FontStyle.Bold))
            using (Font lengthFont = ProjectCreationTheme.Font(8.2f, FontStyle.Bold))
            {
                Rectangle phaseBounds = integrated
                    ? new Rectangle(cableLeft, y - 38, cableRight - cableLeft, 24)
                    : new Rectangle(10, y - 12, 66, 24);
                TextRenderer.DrawText(graphics, phaseText, phaseFont, phaseBounds, modelColor,
                    (integrated ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right) |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(graphics, ProjectText.LengthMetres(length) + " m", lengthFont,
                    new Rectangle(Math.Max(right + 8, Width - 118), y - 12, 108, 24), ProjectCreationTheme.Ink,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            DrawJointMarkers(graphics, phase, length, cableLeft, cableRight, y, integrated, modelColor);
        }

        static void DrawTermination(Graphics graphics, int x, int y, bool pointsRight, Color phaseColor)
        {
            int direction = pointsRight ? 1 : -1;
            Point[] body =
            {
                new Point(x - direction * 18, y - 4), new Point(x - direction * 4, y - 7),
                new Point(x + direction * 16, y - 4), new Point(x + direction * 16, y + 4),
                new Point(x - direction * 4, y + 7), new Point(x - direction * 18, y + 4)
            };
            using (SolidBrush shell = new SolidBrush(Color.FromArgb(222, phaseColor)))
            using (Pen outline = new Pen(Color.FromArgb(91, 68, 66), 1f))
            {
                graphics.FillPolygon(shell, body);
                graphics.DrawPolygon(outline, body);
                for (int ring = -1; ring <= 1; ring++)
                {
                    int ringX = x + direction * ring * 6;
                    graphics.DrawLine(outline, ringX, y - 7, ringX, y + 7);
                }
            }
        }

        void DrawCapacitor(Graphics graphics)
        {
            if (capacitorPhoto != null)
            {
                int availableWidth = Math.Max(1, Width - 48);
                int availableHeight = Math.Max(1, Height - 105);
                float scale = Math.Min(availableWidth / (float)capacitorPhoto.Width,
                    Math.Min(availableHeight / (float)capacitorPhoto.Height, 1.35f));
                int imageWidth = Math.Max(1, (int)(capacitorPhoto.Width * scale));
                int imageHeight = Math.Max(1, (int)(capacitorPhoto.Height * scale));
                var bounds = new Rectangle((Width - imageWidth) / 2, 44 + Math.Max(0, (availableHeight - imageHeight) / 2), imageWidth, imageHeight);
                var interpolation = graphics.InterpolationMode;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(capacitorPhoto, bounds);
                graphics.InterpolationMode = interpolation;
                // The approved HD asset already includes its Capacitor caption.
                return;
            }
            int centerX = Width / 2;
            int centerY = Math.Max(92, Height / 2 - 2);
            int radius = Math.Min(64, Math.Max(42, Math.Min(Width, Height) / 5));
            using (Pen halo = new Pen(Color.FromArgb(177, 219, 229), 1.3f))
            {
                halo.DashStyle = DashStyle.Dash;
                graphics.DrawEllipse(halo, centerX - radius, centerY - radius, radius * 2, radius * 2);
            }
            using (Pen symbol = new Pen(ProjectCreationTheme.Accent, 4f))
            {
                int plateGap = 12;
                int plateHalfHeight = 24;
                graphics.DrawLine(symbol, centerX - plateGap, centerY - plateHalfHeight, centerX - plateGap, centerY + plateHalfHeight);
                graphics.DrawLine(symbol, centerX + plateGap, centerY - plateHalfHeight, centerX + plateGap, centerY + plateHalfHeight);
                graphics.DrawLine(symbol, centerX - radius / 2, centerY, centerX - plateGap, centerY);
                graphics.DrawLine(symbol, centerX + plateGap, centerY, centerX + radius / 2, centerY);
            }
            string caption = ProjectCreationLocalization.TranslateCanonical(LanguageIndex, "电容");
            using (Font font = ProjectCreationTheme.Font(9.5f))
                TextRenderer.DrawText(graphics, caption, font, new Rectangle(centerX - 120, centerY + radius + 10, 240, 26),
                    ProjectCreationTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
