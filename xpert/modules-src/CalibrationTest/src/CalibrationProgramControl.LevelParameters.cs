using System;
using System.Collections.Generic;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // Ranging and charge calibration share a page, not mutable geometry.
        // Each level keeps its own display/acquisition parameters.
        private sealed class LiveLevelParameters
        {
            internal double Length, Velocity, Cutoff;
            internal bool ShortCable, WideBand;
        }
        private readonly Dictionary<string,LiveLevelParameters> liveLevelParameters=new Dictionary<string,LiveLevelParameters>();
        private readonly double[] persistedCableLengths=new double[4];

        private void RememberLiveLevelParameters(int phaseIndex,string level)
        {
            if(SimulationMode)return;
            var phase=GetPhaseStateNoLock(phaseIndex);
            liveLevelParameters[LiveKey(phaseIndex,level)]=new LiveLevelParameters {
                Length=phase.CableLengthMeters,Velocity=phase.VelocityMPerUs,
                ShortCable=phase.ShortCable,WideBand=phase.WideBand,Cutoff=phase.BandwidthCutoffMHz};
        }
        private double PersistedCalibrationLength(int phaseIndex,double fallback)
        {
            double length=persistedCableLengths[phaseIndex+1];
            return IsFinite(length)&&length>0?length:fallback;
        }
        private void RestoreLiveLevelParameters(int phaseIndex,string level)
        {
            if(SimulationMode)return;
            var phase=GetPhaseStateNoLock(phaseIndex);LiveLevelParameters parameters;
            if(liveLevelParameters.TryGetValue(LiveKey(phaseIndex,level),out parameters)) {
                phase.CableLengthMeters=parameters.Length;phase.VelocityMPerUs=parameters.Velocity;
                phase.ShortCable=parameters.ShortCable;phase.WideBand=parameters.WideBand;
                phase.BandwidthCutoffMHz=parameters.Cutoff;
            }
            if(level!="TDR"&&!IsCapacitiveLoad)
                phase.CableLengthMeters=PersistedCalibrationLength(phaseIndex,phase.CableLengthMeters);
        }
        private void RebuildCurrentLiveLevelNoLock(int phaseIndex)
        {
            if(SimulationMode) {RebuildAllDerivedNoLock(GetPhaseStateNoLock(phaseIndex));return;}
            RememberLiveLevelParameters(phaseIndex,selectedLevelCode);
            RebuildLevelDerivedNoLock(GetPhaseStateNoLock(phaseIndex),selectedLevelCode);
        }
    }
}
