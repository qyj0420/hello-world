using System;
using System.Collections.Generic;
using System.Globalization;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    // Scheduling memory only: a failed input range is not selected again within
    // the same explicitly started plan row. It grants no electrical permission.
    internal sealed class PressurePlanRangeFloor
    {
        internal string Context="";
        internal int Minimum=-1;
        // A scheduling hold is not a successful measurement. In particular,
        // a skipped intermediate range was neither captured nor proven bad.
        internal int IssuedMinimum=-1;
        readonly HashSet<int> failed=new HashSet<int>();
        readonly HashSet<int> skippedForBoundedUpscale=new HashSet<int>();
        internal void Bind(string context)
        {if(context==Context)return;Context=context??"";Minimum=IssuedMinimum=-1;failed.Clear();skippedForBoundedUpscale.Clear();}
        internal void Reset(){Bind("");}
        internal void Failed(int range){if(Context.Length>0)failed.Add(range);}
        internal void IssuedUpscale(int current,int next)
        {
            if(Context.Length==0||current<0||next!=current+2)return;
            skippedForBoundedUpscale.Add(current+1);
            IssuedMinimum=Math.Max(IssuedMinimum,next);
        }
        internal int EffectiveMinimum {get{return Math.Max(Minimum,IssuedMinimum);}}
        internal void AcceptedSaved(int range)
        {
            if(Context.Length==0||failed.Count==0)return;
            foreach(int value in failed)if(value>=range)return;
            Minimum=Math.Max(Minimum,range);
        }
        internal HashSet<int> Visited(ISet<int> current)
        {var result=new HashSet<int>(current);result.UnionWith(failed);result.UnionWith(skippedForBoundedUpscale);return result;}
        internal bool Blocks(int range){return EffectiveMinimum>=0&&range<EffectiveMinimum;}
    }

    public sealed partial class PressureTestControl
    {
        readonly PressurePlanRangeFloor pressurePlanRangeFloor=new PressurePlanRangeFloor();
        long operatorPressureStartEpoch;
        string CapturePressurePlanRangeRowNoLock()
        {
            if(operatorPressureStartEpoch<=0||!automaticRange||advancedMode||!planMode||simulationMode)return "";
            return CaptureOperatorPressurePlanContextNoLock()+"|"+calibrationSourceHash+"|"+
                operatorPressureStartEpoch.ToString(CultureInfo.InvariantCulture)+"|"+selectedPlanStepIndex+"|"+
                requestedTargetPeakVoltageKv.ToString("R",CultureInfo.InvariantCulture)+"|"+zeroHardwareGeneration+"|"+
                p04SchedulingRevision;
        }
        void BindPressurePlanRangeRowNoLock(){pressurePlanRangeFloor.Bind(CapturePressurePlanRangeRowNoLock());}
        void RecordIssuedOverflowRangeNoLock(ReviewedRangeRetry permit)
        {
            BindPressurePlanRangeRowNoLock();
            if(permit!=null&&permit.Overflow!=null&&permit.StartedIntent){
                pressurePlanRangeFloor.Failed(permit.Overflow.CurrentRangeIndex);
                pressurePlanRangeFloor.IssuedUpscale(permit.Overflow.CurrentRangeIndex,permit.Overflow.NextRangeIndex);
            }
        }
        HashSet<int> PressurePlanVisitedRangesNoLock()
        {BindPressurePlanRangeRowNoLock();return pressurePlanRangeFloor.Visited(pressureRangeRetryVisited);}
        int SavedPressurePlanRangeFloorNoLock(PressureZeroVoltageResult capture,int current,bool critical)
        {
            BindPressurePlanRangeRowNoLock();
            if(critical||finishCurrentRoundRequested||operatorPressureStopRequested||automaticPlanInterrupted||pressureEmergencyLatched||
                (!CanRequestVoltageNoLock(requestedTargetPeakVoltageKv)&&!HasPendingP04ReadNoLock())){
                pressurePlanRangeFloor.Reset();return -1;
            }
            // A native exit or a recommendation is not a saved useful round.
            // Complete-cycle validation and the existing actual save/ACK gates
            // are required; no quality fields or original recommendation change.
            if(capture!=null&&capture.Quality!=null&&capture.Quality.WindowValid&&capture.Capture!=null&&capture.Capture.Settings!=null&&
                capture.Capture.Settings.RangeIndex==current&&capture.Capture.Settings.PdCalibrationPcPerV==calibrationPcPerVolt&&
                capture.Capture.Settings.AutoRange==automaticRange&&
                dataSaved&&postProcessStateCode=="COMPLETE"&&pressureStateCode=="COMPLETE"&&
                !overRangeIgnored&&!lastAcquisitionOverRange&&locationCycleWindow!=null&&locationCycleWindow.Success&&
                cycleIdentityAccepted&&cycleIdentityBound&&HasActualReleasedPressureCapture()&&
                HasRecordedRangeRetryStartIntent(pressureRequestSequence)&&
                !PressureOverflowUpscale.HasActualOverflow(capture.Capture,capture.OriginalRangeQuality??capture.Quality))
                pressurePlanRangeFloor.AcceptedSaved(current);
            return pressurePlanRangeFloor.EffectiveMinimum;
        }
    }
}
