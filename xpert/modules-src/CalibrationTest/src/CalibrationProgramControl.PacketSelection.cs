using System;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private int packetDragIncident=-1,packetDragReflection=-1;

        // GUI coordinates identify the physical peak pair. Measurement markers
        // are separate and always follow the selected device timing preference.
        public bool SetPhasePacketPeaks(int phaseIndex,string levelCode,int incidentPeak,int reflectionPeak)
        {
            ValidatePhaseIndex(phaseIndex);string level=NormalizeLevelCode(levelCode);
            lock(stateLock) {
                var phase=GetPhaseStateNoLock(phaseIndex);WaveformData raw;TdrAutoAnalysisResult previous;
                if(IsCapacitiveLoad||!phase.Waveforms.TryGetValue(level,out raw)||incidentPeak<1||
                    reflectionPeak<=incidentPeak||reflectionPeak>=raw.Samples.Length-1)return false;
                phase.Analyses.TryGetValue(level,out previous);
                double baseline,sigma;TdrReflectionAnalyzer.RawTimingStatistics(raw.Samples,out baseline,out sigma);
                if(!IsFinite(baseline)||!IsFinite(sigma)||sigma<0||
                    Math.Abs(raw.Samples[incidentPeak]-baseline)<=Math.Max(1e-12,6*sigma)||
                    Math.Abs(raw.Samples[reflectionPeak]-baseline)<=Math.Max(1e-12,6*sigma))return false;
                incidentPeak=SelectedLocalPeak(raw.Samples,incidentPeak,baseline);
                reflectionPeak=SelectedLocalPeak(raw.Samples,reflectionPeak,baseline);
                if(incidentPeak<1||reflectionPeak<=incidentPeak)return false;
                var active=ActiveWaveformNoLock(phase,level,raw);
                var result=BuildManualResult(active,phase,incidentPeak,reflectionPeak,previous);
                int p=Object.ReferenceEquals(phase,allPhase)?-1:Array.IndexOf(phases,phase);
                reflectionSelections[LiveKey(p,level)]=new ReflectionSelectionState {Source=raw,Index=reflectionPeak,
                    IncidentPeak=incidentPeak,ReflectionPeak=reflectionPeak,Confirmed=true,NoticeShown=true,
                    PeakAnchored=true,FirstArrival=UseFirstArrivalTiming};
                StoreAnalysisNoLock(phase,level,active,result);
                System.Threading.Interlocked.Increment(ref cursorChangedSequence);
                System.Threading.Interlocked.Increment(ref analysisChangedSequence);
            }
            Invalidate();RaiseAnalysisAndCursorEvents();return true;
        }

        private static int SelectedLocalPeak(double[] raw,int selected,double baseline)
        {
            int best=-1,distance=Int32.MaxValue,sign=Math.Sign(raw[selected]-baseline);
            // Pixel/sample rounding may miss a peak by at most two samples.
            // Never search a prediction window or a different signed lobe.
            for(int i=Math.Max(1,selected-2);i<=Math.Min(raw.Length-2,selected+2);i++) {
                double value=(raw[i]-baseline)*sign;
                if(!IsFinite(value)||value<=0||value<(raw[i-1]-baseline)*sign||value<=(raw[i+1]-baseline)*sign)continue;
                int d=Math.Abs(i-selected);if(d<distance){best=i;distance=d;}
            }
            return best;
        }

        private MarkerData PacketGuiMarkersNoLock(PhaseState phase,string level,MarkerData measured)
        {
            WaveformData raw;TdrAutoAnalysisResult analysis;
            if(IsCapacitiveLoad||!phase.Waveforms.TryGetValue(level,out raw)||!phase.Analyses.TryGetValue(level,out analysis)||
                analysis==null||analysis.SearchStatus!=TdrSearchStatus.Ok||analysis.IncidentIndex<0||analysis.ReflectionIndex<=analysis.IncidentIndex)return measured;
            int i=packetDragIncident>=0?packetDragIncident:analysis.IncidentIndex;
            int r=packetDragReflection>=0?packetDragReflection:analysis.ReflectionIndex;
            return new MarkerData(raw.X0Seconds+i*raw.DxSeconds,raw.X0Seconds+r*raw.DxSeconds,
                measured==null?Double.NaN:measured.VelocityMarkerSeconds,measured==null?Double.NaN:measured.DistanceMeters);
        }
        private MarkerData CurrentPacketGuiMarkers(MarkerData measured)
        {lock(stateLock)return PacketGuiMarkersNoLock(GetPhaseStateNoLock(SelectedPhaseIndex),SelectedLevelCode,measured);}

        private void SelectReflectionPacket(int phaseIndex,string level,int reflectionPeak)
        {
            TdrAutoAnalysisResult analysis=GetAnalysisNoLockSafe(phaseIndex,level);
            if(analysis!=null)SetPhasePacketPeaks(phaseIndex,level,analysis.IncidentIndex,reflectionPeak);
        }
    }
}
