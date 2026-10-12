using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // Display and manual screening belong to this page/phase. Never reuse a
        // hand-set value from a different project, phase or acquisition session.
        private readonly Dictionary<int,double> manualNoiseThresholds = new Dictionary<int,double>();
        private readonly Dictionary<int,double> automaticNoiseThresholds = new Dictionary<int,double>();
        private bool noiseThresholdDragging, noiseZooming, noiseTemplateMode;
        private PointF noiseGestureStart, noiseGestureEnd;
        private int noiseGesturePhase;
        private double? noiseOriginalThreshold;
        private double noiseThresholdGrabOffset;
        private bool NoiseGestureActive { get { return noiseThresholdDragging || noiseZooming || noiseSelecting; } }
        private double? ManualNoiseThreshold(int phase) { double v; return manualNoiseThresholds.TryGetValue(phase,out v) ? (double?)v : null; }
        private double DisplayNoiseThreshold
        {
            get { double v; return manualNoiseThresholds.TryGetValue(SelectedPhaseIndex,out v) || automaticNoiseThresholds.TryGetValue(SelectedPhaseIndex,out v) ? v : 0; }
        }
        private RectangleF NoiseModeBounds(CompactGeometry ui,int index)
        { float width=Math.Min(84,(ui.Center.Width-40)*.14f);return new RectangleF(ui.Center.Left+12+index*(width+4),ui.Center.Top+53,width,34); }
        private RectangleF NoiseThresholdCheckBounds(CompactGeometry ui)
        { float width=Math.Min(218,(ui.Center.Width-40)*.3f);return new RectangleF(ui.Center.Right-width-10,ui.Center.Top+53,width,28); }
        private string NoiseThresholdCaption
        { get { return ManualNoiseThreshold(SelectedPhaseIndex).HasValue
            ? CalibrationText("阈值 ±","Threshold ±")+DisplayNoiseThreshold.ToString("G3",CultureInfo.InvariantCulture)+" V"
            : CalibrationText("阈值：自动","Threshold: auto"); } }
        private void RestoreAutomaticNoiseThreshold()
        { if(IsBusy)return;CancelNoiseGesture();manualNoiseThresholds.Remove(SelectedPhaseIndex);NoiseBaselineVisible=true;Invalidate(); }
        private double NoiseValue(float y)
        { var p=CurrentNoisePlotRectangle;return noiseY1-(Math.Max(p.Top,Math.Min(p.Bottom,y))-p.Top)/p.Height*(noiseY1-noiseY0); }
        private float NoiseValueY(double value)
        { var p=CurrentNoisePlotRectangle;return p.Bottom-(float)((value-noiseY0)/(noiseY1-noiseY0))*p.Height; }
        private bool HitNoiseThreshold(PointF point)
        {
            if(!NoiseBaselineVisible||noiseY1<=noiseY0||!CurrentNoisePlotRectangle.Contains(point))return false;
            float tolerance=7;
            if(!UseCompactLayout)tolerance*=ArtworkHeight/Math.Max(1,GetContentBounds(ClientRectangle).Height);
            double value=DisplayNoiseThreshold;
            return Math.Abs(point.Y-NoiseValueY(value))<=tolerance || Math.Abs(point.Y-NoiseValueY(-value))<=tolerance;
        }
        private PointF NoiseGesturePoint(Point location)
        {
            if(UseCompactLayout)return location;
            var r=GetContentBounds(ClientRectangle);
            return new PointF((location.X-r.Left)*ArtworkWidth/r.Width,(location.Y-r.Top)*ArtworkHeight/r.Height);
        }
        private void CancelNoiseGesture()
        {
            if(noiseThresholdDragging) {
                if(noiseOriginalThreshold.HasValue)manualNoiseThresholds[noiseGesturePhase]=noiseOriginalThreshold.Value;
                else manualNoiseThresholds.Remove(noiseGesturePhase);
            }
            if(noiseSelecting)noiseSelectionStart=noiseSelectionEnd=noiseSelectionPeak=-1;
            noiseThresholdDragging=noiseZooming=noiseSelecting=false;
            if(Capture)Capture=false;
            Cursor=Cursors.Default;Invalidate();
        }
        private void ApplyNoiseBoxZoom()
        {
            var plot=CurrentNoisePlotRectangle;
            float left=Math.Max(plot.Left,Math.Min(noiseGestureStart.X,noiseGestureEnd.X));
            float right=Math.Min(plot.Right,Math.Max(noiseGestureStart.X,noiseGestureEnd.X));
            float top=Math.Max(plot.Top,Math.Min(noiseGestureStart.Y,noiseGestureEnd.Y));
            float bottom=Math.Min(plot.Bottom,Math.Max(noiseGestureStart.Y,noiseGestureEnd.Y));
            if(right-left<6 || bottom-top<6)return;
            double x0=noiseX0+(left-plot.Left)/plot.Width*(noiseX1-noiseX0);
            double x1=noiseX0+(right-plot.Left)/plot.Width*(noiseX1-noiseX0);
            double y0=NoiseValue(bottom),y1=NoiseValue(top);
            if(noiseViewSource==null || x1-x0<noiseViewSource.DxSeconds*4 || y1-y0<1e-12)return;
            noiseX0=x0;noiseX1=x1;noiseY0=y0;noiseY1=y1;
        }
        private void DrawNoiseZoomSelection(Graphics g,RectangleF plot)
        {
            if(!noiseZooming)return;
            var box=RectangleF.FromLTRB(Math.Min(noiseGestureStart.X,noiseGestureEnd.X),Math.Min(noiseGestureStart.Y,noiseGestureEnd.Y),
                Math.Max(noiseGestureStart.X,noiseGestureEnd.X),Math.Max(noiseGestureStart.Y,noiseGestureEnd.Y));
            box.Intersect(plot);
            using(var brush=new SolidBrush(Color.FromArgb(28,0,163,199)))g.FillRectangle(brush,box);
            using(var pen=new Pen(Color.FromArgb(0,163,199)))g.DrawRectangle(pen,box.X,box.Y,box.Width,box.Height);
        }
    }
}
