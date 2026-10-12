using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        static readonly double DefaultElevation = Math.Atan(0.22 / 0.67) * 180.0 / Math.PI;
        sealed class CameraState
        {
            internal int PlaneIndex;
            internal double Azimuth, Elevation = DefaultElevation;
            internal double CosAzimuth, SinAzimuth, CosElevation, SinElevation, Width, Height;
            internal CameraState() { UpdateProjection(); }
            internal void UpdateProjection()
            {
                double azimuth = Azimuth * Math.PI / 180.0, elevation = Elevation * Math.PI / 180.0;
                CosAzimuth = Math.Cos(azimuth); SinAzimuth = Math.Sin(azimuth);
                CosElevation = Math.Cos(elevation); SinElevation = Math.Sin(elevation);
                // Exact extents of all eight rotated unit-cube corners. No per-point trigonometry.
                Width = Math.Abs(.68 * CosAzimuth - .22 * SinAzimuth) + Math.Abs(.68 * SinAzimuth + .22 * CosAzimuth);
                Height = Math.Abs(SinElevation * SinAzimuth) + Math.Abs(CosElevation) + Math.Abs(SinElevation * CosAzimuth);
            }
        }
        readonly Dictionary<int, CameraState> cameras = new Dictionary<int, CameraState>();
        bool rotating3D, cameraDragMoved;
        bool cameraViewNotification;
        Point cameraDragStart;
        double cameraDragAzimuth, cameraDragElevation;

        CameraState Camera
        {
            get { CameraState camera; if (!cameras.TryGetValue(mode, out camera)) { camera = new CameraState(); cameras.Add(mode, camera); } return camera; }
        }
        [Category("3D view"), DefaultValue(0.0), Description("View azimuth in degrees, stored independently for each display mode. Finite inputs wrap into [-180,180).")]
        public double ViewAzimuthDegrees
        {
            get { return Camera.Azimuth; }
            set { if (!Finite(value)) throw new ArgumentOutOfRangeException("ViewAzimuthDegrees"); CancelGesture(); SetCameraOrientation(value, Camera.Elevation, true); }
        }
        [Category("3D view"), Description("View elevation in degrees, within [-80,80], stored independently for each display mode.")]
        public double ViewElevationDegrees
        {
            get { return Camera.Elevation; }
            set { if (!Finite(value) || value < -80 || value > 80) throw new ArgumentOutOfRangeException("ViewElevationDegrees"); CancelGesture(); SetCameraOrientation(Camera.Azimuth, value, true); }
        }
        [Browsable(false)] public bool Is3DRotating { get { return rotating3D; } }
        // Camera orientation/plane changes repaint only this plot. Consumers
        // can retain ViewChanged while avoiding parameter, selection and page
        // refresh work for this one synchronous notification.
        [Browsable(false)] public bool IsCameraViewNotification { get { return cameraViewNotification; } }
        void NotifyCameraViewChanged()
        {
            bool previous=cameraViewNotification;cameraViewNotification=true;
            try { NotifyHost(ViewChanged); }
            finally { cameraViewNotification=previous; }
        }
        string CameraGeometryKey { get { return Camera.PlaneIndex.ToString(CultureInfo.InvariantCulture) + ":" + Camera.Azimuth.ToString("R", CultureInfo.InvariantCulture) + ":" + Camera.Elevation.ToString("R", CultureInfo.InvariantCulture); } }

        bool CompetitionVoltagePhaseView { get { return competitionProfile&&(mode==7||mode==8); } }
        bool SupportsPlaneViews { get { return mode==5||mode==6||mode==12||CompetitionVoltagePhaseView; } }
        bool IsChargeVoltagePlane { get { return (mode==5||mode==6||CompetitionVoltagePhaseView)&&Camera.PlaneIndex==3; } }
        bool IsPhaseHorizontalPlane { get { return mode==12&&(Camera.PlaneIndex==3||!competitionProfile&&Camera.PlaneIndex==2); } }
        bool CompetitionLocationSelectionPlane { get { return competitionProfile&&mode==12&&Camera.PlaneIndex==1; } }
        [Category("3D view"), DefaultValue(0), Description("0: saved 3D view, 1: XY; L-PRPD uses 2: ZX and 3: ZY with phase horizontal. U-Q-L uses 3: ZY with voltage horizontal. Other modes retain XZ/YZ. No data reaggregation.")]
        public int ViewPlaneIndex
        {
            get { return Camera.PlaneIndex; }
            set
            {
                if(value<0||value>3)throw new ArgumentOutOfRangeException("ViewPlaneIndex");
                if(value!=0&&!SupportsPlaneViews)throw new InvalidOperationException("This spectrum does not support plane views.");
                CancelGesture();if(Camera.PlaneIndex==value)return;
                Camera.PlaneIndex=value;DropDrawingCaches();SyncCameraToolbar();Invalidate();NotifyCameraViewChanged();
            }
        }

        static double NormalizeAzimuth(double value)
        { value %= 360.0; if (value >= 180) value -= 360; if (value < -180) value += 360; return value == 0 ? 0 : value; }
        void SetCameraOrientation(double azimuth, double elevation, bool notify)
        {
            var camera = Camera; azimuth = NormalizeAzimuth(azimuth);
            if (camera.Azimuth == azimuth && camera.Elevation == elevation && camera.PlaneIndex==0) return;
            camera.Azimuth = azimuth; camera.Elevation = elevation; camera.PlaneIndex=0;camera.UpdateProjection();
            DropDrawingCaches(); SyncCameraToolbar();Invalidate(); if (notify) NotifyCameraViewChanged();
        }
        public void Reset3DView() { CancelGesture(); ResetCameraOrientation(true); }
        void ResetCameraOrientation(bool notify) { SetCameraOrientation(0, DefaultElevation, notify); }

        PointF ProjectNormalized3D(double x, double y, double z, Rectangle area)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z)) return new PointF(float.NaN, float.NaN);
            var camera = Camera;
            if(camera.PlaneIndex!=0)
            {
                // Exact orthographic projections: the omitted coordinate cannot move a point.
                double u=camera.PlaneIndex==3?y:x,v=camera.PlaneIndex==1?y:z;
                if(IsChargeVoltagePlane){u=z;v=y;}
                if(IsPhaseHorizontalPlane){u=z;v=camera.PlaneIndex==2?x:y;}
                double planeX=area.Left+u*area.Width,planeY=area.Bottom-v*area.Height;
                if(!Finite(planeX)||!Finite(planeY)||Math.Abs(planeX)>float.MaxValue||Math.Abs(planeY)>float.MaxValue)return new PointF(float.NaN,float.NaN);
                return new PointF((float)planeX,(float)planeY);
            }
            x -= .5; y -= .5; z -= .5;
            double horizontal = camera.CosAzimuth * x + camera.SinAzimuth * z;
            double depth = -camera.SinAzimuth * x + camera.CosAzimuth * z;
            double projectedX = .68 * horizontal + .22 * depth;
            double projectedY = camera.CosElevation * y + camera.SinElevation * depth;
            double px = area.Left + area.Width * (.08 + .90 * (.5 + projectedX / camera.Width));
            double py = area.Bottom - area.Height * (.08 + .89 * (.5 + projectedY / camera.Height));
            if (!Finite(px) || !Finite(py) || Math.Abs(px) > float.MaxValue || Math.Abs(py) > float.MaxValue) return new PointF(float.NaN, float.NaN);
            return new PointF((float)px, (float)py);
        }
        double ProjectionDepth(SpectrumPoint point)
        {
            var camera = Camera;
            double x = (point.X - State.X0) / (State.X1 - State.X0) - .5;
            double y = (point.Y - State.Y0) / (State.Y1 - State.Y0) - .5;
            double z = (point.Z - view.ZMin) / (view.ZMax - view.ZMin) - .5;
            if(camera.PlaneIndex==1)return z;
            if(camera.PlaneIndex==2)return y;
            if(camera.PlaneIndex==3)return x;
            double depth = -camera.SinAzimuth * x + camera.CosAzimuth * z;
            return camera.CosElevation * depth - camera.SinElevation * y;
        }
        void BeginCameraDrag(Point point)
        {
            CancelGesture(); rotating3D = true; cameraDragMoved=false;cameraDragStart = point;
            cameraDragAzimuth = Camera.Azimuth; cameraDragElevation = Camera.Elevation;
            Capture = true; if (tooltip != null) tooltip.SetToolTip(this, "");
        }
        void ContinueCameraDrag(Point point)
        {
            if (!rotating3D) return;
            if(!cameraDragMoved)
            {
                Size threshold=SystemInformation.DragSize;
                if(Math.Abs((double)point.X-cameraDragStart.X)<=Math.Max(2,threshold.Width/2)&&
                    Math.Abs((double)point.Y-cameraDragStart.Y)<=Math.Max(2,threshold.Height/2))return;
                cameraDragMoved=true;
            }
            double azimuth = cameraDragAzimuth + ((double)point.X - cameraDragStart.X) * .45;
            double elevation = Math.Max(-80, Math.Min(80, cameraDragElevation - ((double)point.Y - cameraDragStart.Y) * .30));
            SetCameraOrientation(azimuth, elevation, true);
        }
    }
}
