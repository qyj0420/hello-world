using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal sealed partial class PressureWaveformView
    {
        internal event EventHandler ManualThresholdGuideDragged;
        internal Func<bool> ManualThresholdGuideCanDrag;
        bool manualThresholdDragging;
        Rectangle manualThresholdDragPlot;
        double manualThresholdDragMaximum,manualThresholdDragOriginal;
        internal bool ManualThresholdGuideDragging { get { return manualThresholdDragging; } }

        bool CanDragManualThreshold
        {get{return Enabled&&manualThresholdGuideEnabled&&!IsDisposed&&!Disposing&&
            (ManualThresholdGuideCanDrag==null||ManualThresholdGuideCanDrag());}}

        Rectangle ManualThresholdPlotBounds()
        {
            using(var graphics=CreateGraphics())
            using(var font=CreateAxisFont())
                return CreatePdTrack(CreatePlotBounds(graphics,DisplayPdMaximum,cachedVoltageAbs,font));
        }

        bool HitManualThreshold(Point location)
        {
            if(!CanDragManualThreshold)return false;
            double maximum=DisplayPdMaximum;
            if(!IsFinitePositive(maximum)||manualThresholdGuidePc>maximum)return false;
            Rectangle plot=ManualThresholdPlotBounds();
            return plot.Width>0&&plot.Height>0&&location.X>=plot.Left&&location.X<=plot.Right&&
                location.Y>=plot.Top-6&&location.Y<=plot.Bottom+6&&
                Math.Abs(location.Y-MapY(manualThresholdGuidePc,0,maximum,plot))<=6;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Only the visible pC guide owns this gesture. Off-guide clicks,
            // other buttons and the lower review plot retain their own behavior.
            if(e.Button==MouseButtons.Left&&HitManualThreshold(e.Location))
            {
                manualThresholdDragPlot=ManualThresholdPlotBounds();
                manualThresholdDragMaximum=DisplayPdMaximum;
                manualThresholdDragOriginal=manualThresholdGuidePc;
                manualThresholdDragging=true;Capture=true;Cursor=Cursors.HSplit;
                Focus();return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if(manualThresholdDragging)
            {
                if(!CanDragManualThreshold){CancelManualThresholdDrag(true);return;}
                MoveManualThreshold(e.Y);return;
            }
            Cursor=HitManualThreshold(e.Location)?Cursors.HSplit:Cursors.Default;
            base.OnMouseMove(e);
        }

        void MoveManualThreshold(int y)
        {
            // Freeze the existing pC mapping for this gesture; changing the
            // threshold cannot move its own axis or alter raw waveforms/time.
            double ratio=Math.Max(0,Math.Min(1,(manualThresholdDragPlot.Bottom-y)/(double)manualThresholdDragPlot.Height));
            double value=Math.Round(Math.Min(1e9,ratio*manualThresholdDragMaximum),2);
            if(manualThresholdGuidePc.Equals(value))return;
            manualThresholdGuidePc=value;
            // Paint only the guide during the gesture. Host sync (NumericUpDown /
            // AnalysisThresholdChanged) waits until mouse-up to avoid full-page flash.
            Invalidate();
            if(!manualThresholdDragging)RaiseManualThresholdGuideDragged();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if(manualThresholdDragging&&e.Button==MouseButtons.Left)
            {
                if(CanDragManualThreshold)MoveManualThreshold(e.Y);
                else {CancelManualThresholdDrag(true);return;}
                manualThresholdDragging=false;Capture=false;Cursor=Cursors.Default;Invalidate();
                // Commit after the drag flag clears so the host can sync the
                // NumericUpDown without a mid-gesture ValueChanged round-trip.
                RaiseManualThresholdGuideDragged();
                return;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if(manualThresholdDragging&&!Capture)CancelManualThresholdDrag(true);
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if(!Enabled)CancelManualThresholdDrag(true);
            base.OnEnabledChanged(e);
        }

        protected override bool ProcessCmdKey(ref Message message,Keys keyData)
        {
            if(manualThresholdDragging&&keyData==Keys.Escape){CancelManualThresholdDrag(true);return true;}
            return base.ProcessCmdKey(ref message,keyData);
        }

        void CancelManualThresholdDrag(bool restoreValue)
        {
            if(!manualThresholdDragging)return;
            manualThresholdDragging=false;
            if(restoreValue&&!manualThresholdGuidePc.Equals(manualThresholdDragOriginal))
            {
                manualThresholdGuidePc=manualThresholdDragOriginal;
                if(!IsDisposed&&!Disposing)RaiseManualThresholdGuideDragged();
            }
            Capture=false;Cursor=Cursors.Default;Invalidate();
        }

        void RaiseManualThresholdGuideDragged()
        {var handler=ManualThresholdGuideDragged;if(handler!=null)handler(this,EventArgs.Empty);}
    }
}
