using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed partial class CableTopologyCanvas
    {
        internal double SelectedJointPosition = double.NaN;
        readonly ToolTip jointToolTip = new ToolTip();
        readonly List<JointMarker> renderedJointMarkers = new List<JointMarker>();
        JointMarker hoveredJoint;

        // A display projection only: coincident nodes share one sleeve in the
        // integrated cable, but the project records and their phases stay intact.
        internal sealed class JointMarker
        {
            internal double Position;
            internal string Phase;
            internal JointData[] Sources;
            internal Rectangle Bounds;
            internal Rectangle LabelBounds;
            internal bool Selected;
            internal string Caption;
            internal string Detail;
        }

        internal JointMarker[] RenderedJointMarkers { get { return renderedJointMarkers.ToArray(); } }

        void DrawJointMarkers(Graphics graphics, string phase, double length, int left, int right,
            int y, bool integrated, Color modelColor)
        {
            if (double.IsNaN(length) || double.IsInfinity(length) || length <= 0 || right <= left) return;
            var source = (Joints ?? Enumerable.Empty<JointData>()).Where(joint => joint != null &&
                (integrated || joint.Phase == phase) && !double.IsNaN(joint.Position) &&
                !double.IsInfinity(joint.Position) && joint.Position > 0 && joint.Position < length)
                .OrderBy(joint => joint.Position).ToArray();
            var markers = new List<JointMarker>();
            foreach (var joint in source)
            {
                JointMarker marker = markers.LastOrDefault();
                if (marker == null || Math.Abs(marker.Position - joint.Position) > 0.000001)
                {
                    int x = left + (int)Math.Round((right - left) * joint.Position / length);
                    marker = new JointMarker { Position = joint.Position, Phase = phase,
                        Bounds = new Rectangle(x - 6, y - 10, 12, 20), Sources = new JointData[0] };
                    markers.Add(marker);
                }
                marker.Sources = marker.Sources.Concat(new[] { joint }).ToArray();
            }
            using (Font labelFont = ProjectCreationTheme.Font(8.2f))
            using (Font selectedFont = ProjectCreationTheme.Font(8.2f, FontStyle.Bold))
            {
                // Allocate labels independently of source order. The selected
                // table row gets first choice; crowded ordinary labels use the
                // tooltip rather than overlap each other or another cable row.
                int rowSpace = CableMode == ProjectModes.ThreePhaseSingleCore ? Math.Max(1, (Height + 20) / 4) : Height;
                int labelBottom = Math.Min(Height - 4, y + rowSpace - 42);
                if (CableMode != ProjectModes.ThreePhaseSingleCore) labelBottom = Height - 4;
                var used = new List<Rectangle>();
                foreach (var marker in markers)
                {
                    marker.Selected = marker.Sources.Any(joint => joint.Phase == SelectedPhase &&
                        Math.Abs(joint.Position - SelectedJointPosition) <= 0.000001);
                    marker.Caption = ProjectText.LengthMetres(marker.Position) + " m";
                    marker.Detail = string.Join(Environment.NewLine, marker.Sources.Select(joint =>
                        ProjectCreationLocalization.TranslateCanonical(LanguageIndex, joint.Phase) + " · " +
                        ProjectCreationLocalization.TranslateCanonical(LanguageIndex, joint.Name) + " · " +
                        marker.Caption + " · " + ProjectCreationLocalization.TranslateCanonical(LanguageIndex, joint.AttachmentType) + " · " +
                        ProjectCreationLocalization.TranslateCanonical(LanguageIndex, joint.Material)).ToArray());
                    Color ink = marker.Selected ? ProjectCreationTheme.Accent : modelColor;
                    using (var fill = new SolidBrush(marker.Selected ? Color.FromArgb(218, 247, 252) : Color.FromArgb(220, 229, 233)))
                    using (var outline = new Pen(ink, marker.Selected ? 2.2f : 1.3f))
                    {
                        graphics.FillRectangle(fill, marker.Bounds);
                        graphics.DrawRectangle(outline, marker.Bounds);
                        graphics.DrawLine(outline, marker.Bounds.Left + 3, y - 9, marker.Bounds.Left + 3, y + 9);
                        graphics.DrawLine(outline, marker.Bounds.Right - 3, y - 9, marker.Bounds.Right - 3, y + 9);
                        if (marker.Selected) graphics.DrawRectangle(outline, Rectangle.Inflate(marker.Bounds, 4, 4));
                    }
                }
                foreach (var marker in markers.OrderByDescending(item => item.Selected))
                {
                    Font font = marker.Selected ? selectedFont : labelFont;
                    Size size = TextRenderer.MeasureText(graphics, marker.Caption, font, Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    int width = size.Width + 6, height = size.Height + 4;
                    int x = Math.Max(4, Math.Min(Width - width - 4, marker.Bounds.Left + 6 - width / 2));
                    for (int lane = 0; lane < 3; lane++)
                    {
                        var bounds = new Rectangle(x, y + 17 + lane * (height + 5), width, height);
                        if (bounds.Bottom > labelBottom || bounds.Right > Width - 4 || used.Any(other => Rectangle.Inflate(other, 4, 2).IntersectsWith(bounds))) continue;
                        marker.LabelBounds = bounds; used.Add(bounds);
                        Color ink = marker.Selected ? ProjectCreationTheme.Accent : modelColor;
                        using (var pen = new Pen(ink))
                            graphics.DrawLine(pen, marker.Bounds.Left + 6, y + 11, marker.Bounds.Left + 6, bounds.Top - 1);
                        using (var fill = new SolidBrush(BackColor)) graphics.FillRectangle(fill, bounds);
                        TextRenderer.DrawText(graphics, marker.Caption, font, bounds, ink,
                            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        break;
                    }
                }
            }
            renderedJointMarkers.AddRange(markers);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            JointMarker marker = renderedJointMarkers.FirstOrDefault(item =>
                Rectangle.Inflate(item.Bounds, 5, 5).Contains(e.Location) || item.LabelBounds.Contains(e.Location));
            if (ReferenceEquals(marker, hoveredJoint)) return;
            hoveredJoint = marker;
            jointToolTip.SetToolTip(this, marker == null ? string.Empty : marker.Detail);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoveredJoint = null; jointToolTip.SetToolTip(this, string.Empty);
            base.OnMouseLeave(e);
        }
    }
}
