using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        Bitmap thresholdDragBackdrop;
        int thresholdDragGuideY = int.MinValue;
        internal bool ManualThresholdGuideDragging { get { return manualThresholdDragging; } }

        bool CanDragManualThreshold
        {get{return Enabled&&manualThresholdGuideEnabled&&!IsDisposed&&!Disposing&&
            (ManualThresholdGuideCanDrag==null||ManualThresholdGuideCanDrag());}}

        Rectangle ManualThresholdPlotBounds()
        {
            if(manualThresholdDragging&&manualThresholdDragPlot.Width>0)return manualThresholdDragPlot;
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
                CaptureThresholdDragBackdrop();
                thresholdDragGuideY=MapY(manualThresholdGuidePc,0,manualThresholdDragMaximum,manualThresholdDragPlot);
                Focus();return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if(manualThresholdDragging)
            {
                if(!CanDragManualThreshold){CancelManualThresholdDrag(true);return;}
                MoveManualThreshold(e.Y,true);return;
            }
            Cursor=HitManualThreshold(e.Location)?Cursors.HSplit:Cursors.Default;
            base.OnMouseMove(e);
        }

        void MoveManualThreshold(int y,bool notifyHost)
        {
            // Freeze the existing pC mapping for this gesture; changing the
            // threshold cannot move its own axis or alter raw waveforms/time.
            double ratio=Math.Max(0,Math.Min(1,(manualThresholdDragPlot.Bottom-y)/(double)manualThresholdDragPlot.Height));
            double value=Math.Round(Math.Min(1e9,ratio*manualThresholdDragMaximum),2);
            if(manualThresholdGuidePc.Equals(value))
            {
                if(notifyHost)RaiseManualThresholdGuideDragged();
                return;
            }
            int previousY=thresholdDragGuideY;
            manualThresholdGuidePc=value;
            thresholdDragGuideY=MapY(manualThresholdGuidePc,0,manualThresholdDragMaximum,manualThresholdDragPlot);
            // Prefer a thin dirty strip over a full control Invalidate so the
            // heavy waveform underlay is not rebuilt on every mouse sample.
            InvalidateThresholdGuideStrip(previousY,thresholdDragGuideY);
            if(notifyHost)RaiseManualThresholdGuideDragged();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if(manualThresholdDragging&&e.Button==MouseButtons.Left)
            {
                if(CanDragManualThreshold)MoveManualThreshold(e.Y,false);
                else {CancelManualThresholdDrag(true);return;}
                EndThresholdDrag(true);
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
            bool notifyHost=false;
            if(restoreValue&&!manualThresholdGuidePc.Equals(manualThresholdDragOriginal))
            {
                manualThresholdGuidePc=manualThresholdDragOriginal;
                notifyHost=true;
            }
            EndThresholdDrag(notifyHost);
        }

        void EndThresholdDrag(bool notifyHost)
        {
            manualThresholdDragging=false;
            Capture=false;Cursor=Cursors.Default;
            ReleaseThresholdDragBackdrop();
            thresholdDragGuideY=int.MinValue;
            Invalidate();
            if(notifyHost&&!IsDisposed&&!Disposing)RaiseManualThresholdGuideDragged();
        }

        void RaiseManualThresholdGuideDragged()
        {var handler=ManualThresholdGuideDragged;if(handler!=null)handler(this,EventArgs.Empty);}

        void CaptureThresholdDragBackdrop()
        {
            ReleaseThresholdDragBackdrop();
            if(!IsHandleCreated||Width<=0||Height<=0)return;
            try
            {
                var bitmap=new Bitmap(Width,Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(bitmap))
                {
                    g.Clear(BackColor);
                    PaintWaveformContent(g,false);
                }
                thresholdDragBackdrop=bitmap;
            }
            catch(Exception)
            {
                ReleaseThresholdDragBackdrop();
            }
        }

        void ReleaseThresholdDragBackdrop()
        {
            if(thresholdDragBackdrop==null)return;
            thresholdDragBackdrop.Dispose();
            thresholdDragBackdrop=null;
        }

        void InvalidateThresholdGuideStrip(int previousY,int nextY)
        {
            if(thresholdDragBackdrop==null||manualThresholdDragPlot.Width<=0)
            {
                Invalidate();
                return;
            }
            int top=Math.Min(previousY,nextY)-18;
            int bottom=Math.Max(previousY,nextY)+18;
            // Include the label band above the guide line.
            top=Math.Min(top,manualThresholdDragPlot.Top);
            bottom=Math.Max(bottom,manualThresholdDragPlot.Bottom);
            var strip=Rectangle.Intersect(ClientRectangle,
                new Rectangle(manualThresholdDragPlot.Left,top,manualThresholdDragPlot.Width,Math.Max(1,bottom-top)));
            if(strip.Width<=0||strip.Height<=0)Invalidate();
            else Invalidate(strip);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ReleaseThresholdDragBackdrop();
            base.OnHandleDestroyed(e);
        }

        bool TryPaintThresholdDragFrame(Graphics g)
        {
            if(!manualThresholdDragging||thresholdDragBackdrop==null)return false;
            g.DrawImageUnscaled(thresholdDragBackdrop,0,0);
            using(var font=CreateAxisFont())
                DrawManualThresholdGuide(g,manualThresholdDragPlot,0,manualThresholdDragMaximum,font);
            return true;
        }
    }
}
