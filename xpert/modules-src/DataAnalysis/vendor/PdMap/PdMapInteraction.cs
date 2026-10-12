using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        readonly Timer cursorBlinkTimer = new Timer { Interval = 500 };
        int selectedCursor, draggingCursor, dragStartX, dragStartIndex, lastDragTarget = -1;
        bool cursorBlinkEnabled = true, cursorBlinkBright = true;
        bool boxZoomActive;
        Point boxZoomStart, boxZoomEnd;

        [Browsable(false), Description("选中游标 / Selected cursor: 0=None, 1=Incident, 2=Reflection.")]
        public int SelectedCursor { get { return selectedCursor; } }

        [Category("Interaction"), DefaultValue(true), Description("选中三角标慢速闪烁 / Slowly blink the selected cursor handle.")]
        public bool CursorBlinkEnabled
        {
            get { return cursorBlinkEnabled; }
            set { cursorBlinkEnabled = value; UpdateCursorBlink(); Invalidate(); }
        }

        void InitializeCursorInteraction()
        {
            cursorBlinkTimer.Tick += CursorBlinkTick;
        }

        void CursorBlinkTick(object sender, EventArgs e)
        {
            if (selectedCursor == 0 || !cursorBlinkEnabled || IsDisposed) return;
            cursorBlinkBright = !cursorBlinkBright;
            Invalidate();
        }

        void UpdateCursorBlink()
        {
            if (cursorBlinkTimer == null || IsDisposed || Disposing) return;
            cursorBlinkTimer.Stop();
            cursorBlinkBright = true;
            if (selectedCursor != 0 && cursorBlinkEnabled && IsHandleCreated && base.Visible && Enabled)
                cursorBlinkTimer.Start();
        }

        void SelectCursor(int cursorId)
        {
            selectedCursor = cursorId;
            UpdateCursorBlink();
            UpdateCursorStepButtons();
            Invalidate();
        }

        public void ClearCursorSelection()
        {
            EndCursorDrag("selection cleared by data load or command");
            SelectCursor(0);
        }

        Rectangle PlotRectangle(Rectangle canvas)
        {
            return MeasurePlotRectangle(canvas);
        }

        bool PrepareInteraction()
        {
            if (pulse.Length == 0 || ClientSize.Width < 280 || ClientSize.Height < 210) return false;
            lastPlot = PlotRectangle(ClientRectangle);
            EnsureView();
            return true;
        }

        bool HitCursor(Point point, int index)
        {
            if (index < viewX0 || index > viewX1) return false;
            float x = X(index, lastPlot);
            bool handle = Math.Abs(point.X - x) <= 12 && point.Y >= lastPlot.Top - 18 && point.Y <= lastPlot.Top + 3;
            bool line = Math.Abs(point.X - x) <= 7 && point.Y >= lastPlot.Top && point.Y <= lastPlot.Bottom;
            return handle || line;
        }

        int HitCursor(Point point)
        {
            bool hitIncident = HitCursor(point, incident), hitReflection = ReflectionWithinWaveform&&HitCursor(point, reflection);
            if (hitIncident && hitReflection)
            {
                float a = Math.Abs(point.X - X(incident, lastPlot)), b = Math.Abs(point.X - X(reflection, lastPlot));
                // If the handles overlap exactly, successive clicks let either cursor be picked.
                if (Math.Abs(a - b) < .5f) return selectedCursor == 1 ? 2 : 1;
                return a < b ? 1 : 2;
            }
            return hitIncident ? 1 : hitReflection ? 2 : 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right)
            {
                // ContextMenuStrip (or the LabVIEW host) owns right-click. Never reset here.
                EndCursorDrag("right click");
                return;
            }
            if (e.Button != MouseButtons.Left || !PrepareInteraction()) return;
            Focus();
            int hit = capacitorStatisticsMode?0:HitCursor(e.Location);
            if (hit != 0)
            {
                SelectCursor(hit);
                draggingCursor = hit;
                LastInteractionStopReason = "";
                dragStartX = e.X;
                dragStartIndex = hit == 1 ? incident : reflection;
                lastDragTarget = -1;
                Capture = true;
                Cursor = Cursors.SizeWE;
            }
            else if (lastPlot.Contains(e.Location))
            {
                // Empty-plot presses reserve a zoom rectangle; a click alone changes no data or view.
                SelectCursor(0);
                boxZoomActive = true;
                boxZoomStart = boxZoomEnd = e.Location;
                LastInteractionStopReason = "";
                Capture = true;
                Cursor = Cursors.Cross;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if(capacitorStatisticsMode||e.Button!=MouseButtons.Left||!PrepareInteraction()||!lastPlot.Contains(e.Location))return;
            EndCursorDrag("manual reflection selection");
            MoveReflectionToIndex((int)Math.Round(Clamp(DataX(e.X,lastPlot),0,pulse.Length-1)));
            SelectCursor(2);
        }

        void MoveDraggedCursor(int mouseX)
        {
            if (draggingCursor == 0 || pulse.Length == 0 || (mouseX == dragStartX && lastDragTarget < 0)) return;
            double index = dragStartIndex + (mouseX - (double)dragStartX) * (viewX1 - viewX0) / Math.Max(1, lastPlot.Width);
            int target = (int)Math.Round(Clamp(index, 0, pulse.Length - 1));
            if (target == lastDragTarget) return;
            lastDragTarget = target;
            if (draggingCursor == 1) MoveIncidentToIndex(target);
            else PreviewReflectionAtIndex(target);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (draggingCursor != 0 || boxZoomActive)
            {
                ClearConnectorToolTip();
                if ((e.Button & MouseButtons.Left) == 0) { EndCursorDrag("left button no longer held"); return; }
                if (draggingCursor != 0) MoveDraggedCursor(e.X);
                else { boxZoomEnd = ClampBoxZoomPoint(e.Location); Invalidate(); }
                return;
            }
            Cursor = PrepareInteraction() && HitCursor(e.Location) != 0 ? Cursors.SizeWE : Cursors.Default;
            UpdateConnectorToolTip(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && draggingCursor != 0)
            {
                MoveDraggedCursor(e.X);
                if (draggingCursor == 2 && lastDragTarget >= 0) MoveReflectionToIndex(lastDragTarget);
                // A manual release commits the selected sample, never a nearby
                // extremum, first arrival or accessory approximation.
                if (lastDragTarget >= 0) NotifyReviewAnalysisEdited("CursorReleased");
                EndCursorDrag("mouse released");
            }
            else if (e.Button == MouseButtons.Left && boxZoomActive)
            {
                boxZoomEnd = ClampBoxZoomPoint(e.Location);
                ApplyBoxZoom();
                EndCursorDrag("box zoom released");
            }
            base.OnMouseUp(e);
        }

        void EndCursorDrag(string reason = "command")
        {
            if (draggingCursor != 0 || boxZoomActive) LastInteractionStopReason = reason;
            bool hadBox = boxZoomActive;
            draggingCursor = 0;
            boxZoomActive = false;
            lastDragTarget = -1;
            // Capture loss, Escape and focus/visibility changes retain the last
            // previewed candidate. Publish it once as a final state, just as on
            // mouse-up; releasing capture below can re-enter this method.
            if(lastReviewNotificationWasPreview)NotifyReviewAnalysisEdited("CursorEnded");
            if (Capture) Capture = false;
            Cursor = Cursors.Default;
            if (hadBox) Invalidate();
        }

        Point ClampBoxZoomPoint(Point point)
        {
            return new Point(Math.Max(lastPlot.Left, Math.Min(lastPlot.Right, point.X)), Math.Max(lastPlot.Top, Math.Min(lastPlot.Bottom, point.Y)));
        }

        Rectangle BoxZoomRectangle()
        {
            return Rectangle.FromLTRB(Math.Min(boxZoomStart.X, boxZoomEnd.X), Math.Min(boxZoomStart.Y, boxZoomEnd.Y),
                Math.Max(boxZoomStart.X, boxZoomEnd.X), Math.Max(boxZoomStart.Y, boxZoomEnd.Y));
        }

        void ApplyBoxZoom()
        {
            Rectangle rectangle = BoxZoomRectangle();
            // Ignore clicks, hand jitter, and one-dimensional drags; this gesture always zooms both axes.
            if (rectangle.Width < 6 || rectangle.Height < 6 || lastPlot.Width <= 0 || lastPlot.Height <= 0) return;
            double left = DataX(rectangle.Left, lastPlot), right = DataX(rectangle.Right, lastPlot);
            double bottom = DataY(rectangle.Bottom, lastPlot), top = DataY(rectangle.Top, lastPlot);
            if (!IsFinite(left) || !IsFinite(right) || !IsFinite(bottom) || !IsFinite(top) || right <= left || top <= bottom) return;
            viewX0 = left; viewX1 = right; viewY0 = bottom; viewY1 = top;
            viewReady = true;
        }

        void DrawBoxZoomSelection(Graphics graphics, Rectangle plot)
        {
            if (!boxZoomActive) return;
            Rectangle rectangle = Rectangle.Intersect(BoxZoomRectangle(), plot);
            if (rectangle.Width < 1 || rectangle.Height < 1) return;
            using (var fill = new SolidBrush(Color.FromArgb(24, HVX.PdReview.ReviewTheme.Active)))
            using (var border = new Pen(HVX.PdReview.ReviewTheme.Active, 1.2f))
            {
                border.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                graphics.FillRectangle(fill, rectangle);
                graphics.DrawRectangle(border, rectangle);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) EndCursorDrag("mouse capture lost");
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            ClearConnectorToolTip();
            if (draggingCursor == 0 && !boxZoomActive) Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && (selectedCursor != 0 || draggingCursor != 0 || boxZoomActive)) { ClearCursorSelection(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            EndCursorDrag("focus lost");
            base.OnLostFocus(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!base.Visible) EndCursorDrag("control hidden");
            UpdateCursorBlink();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) EndCursorDrag("control disabled");
            UpdateCursorBlink();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateCursorBlink();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            EndCursorDrag("handle destroyed");
            cursorBlinkTimer.Stop();
            base.OnHandleDestroyed(e);
        }

        void DrawCursorHandle(Graphics g, Rectangle plot, float x, Color color, int cursorId, bool interactive)
        {
            bool selected = selectedCursor == cursorId;
            PointF[] triangle = { new PointF(x - 8, plot.Top - 12), new PointF(x + 8, plot.Top - 12), new PointF(x, plot.Top - 2) };
            Color fill = selected && interactive && cursorBlinkEnabled && !cursorBlinkBright ? Color.FromArgb(55, color) : color;
            using (var brush = new SolidBrush(fill)) g.FillPolygon(brush, triangle);
            if (selected)
            {
                // A persistent double outline keeps selection obvious during the dim phase.
                using (var outer = new Pen(Color.White, 4f)) g.DrawPolygon(outer, triangle);
                using (var outline = new Pen(color, 1.7f)) g.DrawPolygon(outline, triangle);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                EndCursorDrag("control disposed");
                cursorBlinkTimer.Stop();
                cursorBlinkTimer.Tick -= CursorBlinkTick;
                cursorBlinkTimer.Dispose();
                DisposeDisplayOptions();
                DisposeCursorStepButtons();
                DisposeConnectorToolTip();
                DisposeSeriesGeometry();
                ContextMenuStrip = null;
                menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
