using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        double windowLength=double.NaN,windowVelocity=double.NaN;
        int windowPhase=int.MinValue;
        double maximumDisplaySpan=double.PositiveInfinity;
        [Browsable(false)] public string DisplayWindowStatus { get; private set; }="Unavailable";
        [Browsable(false)] public string DisplayWindowMessage
        {
            get
            {
                if(DisplayWindowStatus=="UnknownGeometry")return CalibrationText("电缆长度或波速无效，当前显示原始采集范围", "Invalid cable length or velocity; showing the actual captured range.");
                if(DisplayWindowStatus=="InconsistentMarkers")return CalibrationText("反射标记与电缆传播范围不一致，请核对长度、波速和反射选择", "Reflection cursor is inconsistent with the cable geometry; review length, velocity and selection.");
                if(DisplayWindowStatus=="CaptureTooShort")return CalibrationText("现有采集范围不足以显示第二次折返，未补造数据", "The capture does not include the second return; no data has been invented.");
                if(DisplayWindowStatus=="PreTriggerTooShort")return CalibrationText("现有入射前数据不足，按实际采集起点显示", "Insufficient pre-arrival data; showing the actual capture start.");
                if(DisplayWindowStatus=="TailTooShort")return CalibrationText("现有尾部数据不足，按实际采集终点显示", "Insufficient tail data; showing the actual capture end.");
                if(DisplayWindowStatus=="InsufficientResolution")return CalibrationText("当前采样间隔不足以显示此电缆范围", "Insufficient sample resolution for this cable range.");
                return string.Empty;
            }
        }
        // Only the viewport changes. Raw samples, capture length and cursor times are untouched.
        internal static double[] CalculateDisplayWindow(double dataStart,double dataEnd,double dx,double length,double velocity,double incident,double reflection)
        {
            return HVX.CalibrationDisplay.CalibrationPulseDisplayWindow.Calculate(dataStart,dataEnd,dx,length,velocity,incident,reflection);
        }
        bool RefreshDisplayWindowContext(WaveformData waveform)
        {
            PhaseState phase; MarkerData markers;
            double length,velocity,incident=double.NaN,reflection=double.NaN;
            int selected;
            lock(stateLock)
            {
                selected=selectedPhaseIndex;phase=GetPhaseStateNoLock(selected);
                length=phase.CableLengthMeters;velocity=phase.VelocityMPerUs;
                if(phase.Markers.TryGetValue(selectedLevelCode,out markers))
                {incident=markers.StartTimeSeconds;reflection=markers.DistanceMarkerSeconds;}
                else { TdrAutoAnalysisResult a;if(phase.Analyses.TryGetValue(selectedLevelCode,out a))incident=a.IncidentTimeSeconds; }
            }
            bool changed=windowPhase!=selected||!windowLength.Equals(length)||!windowVelocity.Equals(velocity);
            // Do not auto-pan while dragging a cursor. Keep its chosen time intact.
            if(viewReady&&ReferenceEquals(viewWaveform,waveform)&&!changed)return false;
            windowPhase=selected;windowLength=length;windowVelocity=velocity;
            if(IsCapacitiveLoad) {
                viewX0=waveform.X0Seconds;viewX1=viewX0+Math.Max(1,waveform.Samples.Length-1)*waveform.DxSeconds;
                maximumDisplaySpan=double.PositiveInfinity;DisplayWindowStatus="Ready";return true;
            }
            double[] window=CalculateDisplayWindow(waveform.X0Seconds,waveform.X0Seconds+Math.Max(1,waveform.Samples.Length-1)*waveform.DxSeconds,waveform.DxSeconds,length,velocity,incident,reflection);
            viewX0=window[0];viewX1=window[1];maximumDisplaySpan=window[2];
            DisplayWindowStatus=window[3]==1?"UnknownGeometry":window[3]==2?"InconsistentMarkers":window[3]==3?"CaptureTooShort":window[3]==4?"InsufficientResolution":window[3]==5?"PreTriggerTooShort":window[3]==6?"TailTooShort":"Ready";
            return true;
        }
        void LimitDisplayWindow(WaveformData waveform,double anchor)
        {
            if(!IsFinite(maximumDisplaySpan)||viewX1-viewX0<=maximumDisplaySpan)return;
            double dataStart=waveform.X0Seconds,dataEnd=dataStart+(waveform.Samples.Length-1)*waveform.DxSeconds;
            double span=Math.Min(dataEnd-dataStart,maximumDisplaySpan);
            double ratio=(anchor-viewX0)/Math.Max(1e-18,viewX1-viewX0);
            viewX0=Math.Max(dataStart,Math.Min(dataEnd-span,anchor-span*Math.Max(0,Math.Min(1,ratio))));viewX1=viewX0+span;
        }
    }
}
