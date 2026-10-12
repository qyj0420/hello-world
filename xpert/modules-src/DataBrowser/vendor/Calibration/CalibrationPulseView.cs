using System;
using System.ComponentModel;

namespace CalibrationWaveform
{
    public sealed partial class CalibrationWaveformControl
    {
        bool showDecayCurve = true;
        [DefaultValue(true)] public bool ShowDecayCurve
        { get { return showDecayCurve; } set { if(showDecayCurve==value)return;showDecayCurve=value;SafeInvalidate(); } }
        [Browsable(false)] public string PulseViewStatus { get; private set; } = "Unavailable";
        [Browsable(false)] public string PulseViewMessage
        {
            get {
                if(PulseViewStatus=="CaptureTooShort")return Text("现有采集范围不足以显示第二次折返，未补造数据", "The capture does not include the second return; no data has been invented.");
                if(PulseViewStatus=="PreTriggerTooShort")return Text("现有入射前数据不足，按实际采集起点显示", "Insufficient pre-arrival data; showing the actual capture start.");
                if(PulseViewStatus=="TailTooShort")return Text("现有尾部数据不足，按实际采集终点显示", "Insufficient tail data; showing the actual capture end.");
                if(PulseViewStatus=="UnknownGeometry")return Text("长度或波速无效，显示原始采集范围", "Invalid length or velocity; showing the actual captured range.");
                if(PulseViewStatus=="InconsistentMarkers")return Text("反射标记与电缆传播范围不一致，请核对", "Reflection cursor is inconsistent with the cable geometry; review the selection.");
                if(PulseViewStatus=="InsufficientResolution")return Text("当前采样间隔不足以显示此电缆范围", "Insufficient sample resolution for this cable range.");
                return String.Empty;
            }
        }

        // The archived X axis uses actual microseconds. Change only the view;
        // never rewrite samples, cursor times, cable length or capture evidence.
        public void FitPulseView()
        {
            lock (dataLock) {
                if (xs.Length < 2) { PulseViewStatus = "Unavailable"; return; }
                double dx = (dataXMax - dataXMin) / (xs.Length - 1);
                double[] w = HVX.CalibrationDisplay.CalibrationPulseDisplayWindow.Calculate(
                    dataXMin * 1e-6, dataXMax * 1e-6, dx * 1e-6, cableLengthMeters,
                    propagationVelocityMPerUs, incidentX * 1e-6, reflectionX * 1e-6);
                viewXMin = w[0] * 1e6; viewXMax = w[1] * 1e6; autoScaleY = true;
                PulseViewStatus = w[3] == 0 ? "Ready" : w[3] == 1 ? "UnknownGeometry" :
                    w[3] == 2 ? "InconsistentMarkers" : w[3] == 3 ? "CaptureTooShort" :
                    w[3] == 4 ? "InsufficientResolution" : w[3] == 5 ? "PreTriggerTooShort" : "TailTooShort";
            }
            SafeInvalidate();
        }
    }
}
