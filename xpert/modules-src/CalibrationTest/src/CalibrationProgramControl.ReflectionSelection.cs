using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HVX.CalibrationStorage;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // A pending handle is an editing aid, never a measurement marker. Only
        // a committed user selection can populate the measured analysis pair.
        private sealed class ReflectionSelectionState
        {
            internal WaveformData Source;
            internal int Index, IncidentPeak, ReflectionPeak;
            internal double IncidentSeconds, ReflectionSeconds;
            internal bool Confirmed, NoticeShown, FirstArrival, PeakAnchored;
        }
        private readonly Dictionary<string,ReflectionSelectionState> reflectionSelections = new Dictionary<string,ReflectionSelectionState>();
        private RectangleF pendingReflectionBadgeHit;
        private int pendingReflectionDragOriginalIndex = -1;
        internal Action<string> ShowReflectionSelectionNoticeForTests;

        private ReflectionSelectionState ReflectionSelectionNoLock(PhaseState phase,string level,bool create)
        {
            if(IsCapacitiveLoad)return null;
            WaveformData raw;TdrAutoAnalysisResult a;
            if(!phase.Waveforms.TryGetValue(level,out raw))return null;
            int phaseIndex=Object.ReferenceEquals(phase,allPhase)?-1:Array.IndexOf(phases,phase);
            // Archive validation builds detached phases. They must never remove
            // or reuse the live ALL-phase selection merely because IndexOf is -1.
            if(phaseIndex<0&&!Object.ReferenceEquals(phase,allPhase))return null;
            string key=LiveKey(phaseIndex,level);ReflectionSelectionState selection;
            if(reflectionSelections.TryGetValue(key,out selection)&&Object.ReferenceEquals(selection.Source,raw))return selection;
            reflectionSelections.Remove(key);
            if(!create||!phase.Analyses.TryGetValue(level,out a)||a==null||a.SearchStatus==TdrSearchStatus.Ok||
                a.IncidentIndex<0||a.IncidentIndex>=raw.Samples.Length-1||!IsFinite(a.IncidentTimeSeconds)||!(a.IncidentMagnitude>0))return null;
            ReflectionCandidateEvidence candidate;
            double samples=2*phase.CableLengthMeters/(phase.VelocityMPerUs*1e6*raw.DxSeconds);
            int index=phase.ReflectionCandidates.TryGetValue(level,out candidate)?candidate.Index:
                a.IncidentIndex+(IsFinite(samples)&&samples>0&&samples<raw.Samples.Length?(int)Math.Round(samples):Math.Max(1,(raw.Samples.Length-a.IncidentIndex)/2));
            selection=new ReflectionSelectionState {Source=raw,Index=Math.Max(a.IncidentIndex+1,Math.Min(raw.Samples.Length-1,index))};
            reflectionSelections[key]=selection;return selection;
        }

        private void NotifyMissingReflectionOnce(int phaseIndex,string level)
        {
            ReflectionSelectionState selection;
            lock(stateLock)selection=ReflectionSelectionNoLock(GetPhaseStateNoLock(phaseIndex),level,true);
            if(selection==null||selection.Confirmed||selection.NoticeShown)return;
            selection.NoticeShown=true;
            string message=CalibrationText("未检测到可靠反射波。请将波形上的“拖动选择反射”标记拖到实际反射波位置。\r\n\r\n也可直接保存入射校准系数；未选择反射时，反射与衰减记录为待确认，不影响系数保存。",
                "No reliable reflection was detected. Drag the 'Select reflection' marker to the actual reflected pulse.\r\n\r\nYou can also save the incident calibration coefficient now. Unselected reflection and attenuation remain unconfirmed.");
            AuditQualityOperation(phaseIndex,level,"ReflectionSelectionNotice","Shown",message);
            if(ShowReflectionSelectionNoticeForTests!=null)ShowReflectionSelectionNoticeForTests(message);
            else MessageBox.Show(FindForm(),message,CalibrationText("选择反射波","Select reflection"),MessageBoxButtons.OK,MessageBoxIcon.Information);
        }

        private void DrawPendingReflectionSelection(Graphics g,RectangleF plot,double x0,double x1)
        {
            pendingReflectionBadgeHit=RectangleF.Empty;
            var phase=GetPhaseStateNoLock(SelectedPhaseIndex);MarkerData marker;
            if(phase.Markers.TryGetValue(SelectedLevelCode,out marker))return;
            var selection=ReflectionSelectionNoLock(phase,SelectedLevelCode,true);
            if(selection==null||selection.Confirmed||!(x1>x0))return;
            double seconds=selection.Source.X0Seconds+selection.Index*selection.Source.DxSeconds;
            if(seconds<x0||seconds>x1)return;
            var color=Color.FromArgb(180,124,24);
            DrawMarkerLine(g,plot,seconds,x0,x1,color);
            float x=plot.Left+(float)((seconds-x0)/(x1-x0))*plot.Width;
            float width=Math.Min(180,plot.Width-12);
            pendingReflectionBadgeHit=new RectangleF(Math.Max(plot.Left+6,Math.Min(plot.Right-width-6,x+6)),Math.Max(plot.Top+6,plot.Bottom-72),width,36);
            using(var fill=new SolidBrush(Color.FromArgb(248,242,223)))g.FillRectangle(fill,pendingReflectionBadgeHit);
            ThemeText(g,CalibrationText("拖动选择反射 · 待确认","Select reflection · unconfirmed"),pendingReflectionBadgeHit,color,9f);
        }

        private bool HitPendingReflection(PointF point,PhaseState phase,string level)
        {
            MarkerData marker;if(phase.Markers.TryGetValue(level,out marker))return false;
            var selection=ReflectionSelectionNoLock(phase,level,true);
            if(selection==null||selection.Confirmed)return false;
            float x=TimeToReferenceX(selection.Source.X0Seconds+selection.Index*selection.Source.DxSeconds);
            return pendingReflectionBadgeHit.Contains(point)||Math.Abs(point.X-x)<=9;
        }

        private void PreviewPendingReflection(PointF point)
        {
            var phase=GetPhaseStateNoLock(SelectedPhaseIndex);
            var selection=ReflectionSelectionNoLock(phase,SelectedLevelCode,false);TdrAutoAnalysisResult a;
            if(selection==null||!phase.Analyses.TryGetValue(SelectedLevelCode,out a))return;
            selection.Index=Math.Max(a.IncidentIndex+1,ReferenceXToSample(point.X-cursorGrabOffsetX,selection.Source));
            Invalidate();
        }

        private void RememberManualReflectionNoLock(PhaseState phase,string level)
        {
            WaveformData raw;MarkerData marker;TdrAutoAnalysisResult a;
            if(!phase.Waveforms.TryGetValue(level,out raw)||!phase.Markers.TryGetValue(level,out marker)||
                !phase.Analyses.TryGetValue(level,out a)||a.SearchStatus!=TdrSearchStatus.Ok)return;
            int phaseIndex=Object.ReferenceEquals(phase,allPhase)?-1:Array.IndexOf(phases,phase);
            reflectionSelections[LiveKey(phaseIndex,level)]=new ReflectionSelectionState {Source=raw,Index=a.ReflectionIndex,
                IncidentPeak=a.IncidentIndex,ReflectionPeak=a.ReflectionIndex,IncidentSeconds=marker.StartTimeSeconds,
                ReflectionSeconds=marker.DistanceMarkerSeconds,Confirmed=true,NoticeShown=true,FirstArrival=UseFirstArrivalTiming};
        }

        private TdrAutoAnalysisResult RestoreManualReflectionNoLock(PhaseState phase,string level,WaveformData active,TdrAutoAnalysisResult proposed)
        {
            var selection=ReflectionSelectionNoLock(phase,level,false);
            if(selection==null||!selection.Confirmed)return proposed;
            return BuildManualResult(active,phase,selection.IncidentPeak,selection.ReflectionPeak,proposed);
        }

        private void RestoreManualReflectionTimingNoLock(PhaseState phase,string level,TdrAutoAnalysisResult result)
        {
            var selection=ReflectionSelectionNoLock(phase,level,false);
            if(selection==null||!selection.Confirmed||selection.PeakAnchored||result.SearchStatus!=TdrSearchStatus.Ok||
                result.IncidentIndex!=selection.IncidentPeak||result.ReflectionIndex!=selection.ReflectionPeak)return;
            double start=selection.IncidentSeconds,end=selection.ReflectionSeconds;
            phase.Markers[level]=new MarkerData(start,end,start+2*phase.CableLengthMeters/(phase.VelocityMPerUs*1e6),(end-start)*phase.VelocityMPerUs*1e6/2);
            if(UseFirstArrivalTiming) {result.IncidentFirstArrivalIndex=(int)Math.Round((start-selection.Source.X0Seconds)/selection.Source.DxSeconds);
                result.ReflectionFirstArrivalIndex=(int)Math.Round((end-selection.Source.X0Seconds)/selection.Source.DxSeconds);}
        }
    }
}
