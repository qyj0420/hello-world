using System;

namespace HVX.Task17.Location
{
    // Explicit caller-supplied endpoint provenance, not a boundary inferred from
    // lead length, a CAL injection point, a waveform peak or a known PD position.
    internal sealed class EighthLoopClockContext
    {
        public EighthCalibration OriginalCalibration;
        public string BoundaryKind = "unknown";
        public string BoundarySource = "";
    }

    internal sealed class EighthResolvedClocks
    {
        public bool PhysicalLoopKnown, SelectedRoleClockKnown;
        public double DutWindowSeconds, PeakLoopSeconds, SelectedRoleLoopSeconds;
        public double LoopUncertaintySeconds, RoleMeasurementUncertaintySeconds, ModeDiscrepancySeconds;
        public string BoundaryKind="unknown", BoundarySource="", Reason="physical_loop_boundary_unknown";
        public string RecurrenceBasis="native_peak_original_CAL", CoverageBasis="native_incident_peak_device_loop_context";
        public string PairWindowBasis="selected_mode_DUT", PairRoleBasis="";

        static bool Finite(double x) {return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        static bool Same(double x,double y)
        {return Finite(x)&&Finite(y)&&Math.Abs(x-y)<=Math.Max(1e-18,64*2.2204460492503131e-16*Math.Max(Math.Abs(x),Math.Abs(y)));}

        internal static EighthLoopClockContext Legacy(EighthCalibration search)
        {
            return new EighthLoopClockContext { OriginalCalibration=search,
                BoundaryKind=search!=null&&search.RemovedLeadRoundTripSeconds==0?"dut_to_terminal":"unknown",
                BoundarySource=search!=null&&search.RemovedLeadRoundTripSeconds==0?
                    "legacy_zero_lead_coincident_endpoint_assumption":"positive_lead_boundary_not_provided" };
        }

        internal static EighthResolvedClocks Resolve(EighthCalibration search,EighthLoopClockContext context,
            double dutWindow,bool firstArrival,double locFs)
        {
            var r=new EighthResolvedClocks {DutWindowSeconds=Finite(dutWindow)?dutWindow:0,PairRoleBasis=firstArrival?"native_onset_original_CAL":"native_peak_original_CAL"};
            if(context==null)return r;
            if((context.BoundaryKind!=null&&context.BoundaryKind.Length>32)||
                (context.BoundarySource!=null&&context.BoundarySource.Length>256))
            {r.Reason="physical_loop_provenance_token_exceeds_bounded_contract";return r;}
            r.BoundaryKind=context.BoundaryKind??"unknown";r.BoundarySource=context.BoundarySource??"";
            if((r.BoundaryKind!="device_to_terminal"&&r.BoundaryKind!="dut_to_terminal")||String.IsNullOrWhiteSpace(r.BoundarySource))return r;
            var original=context.OriginalCalibration;
            if(!EighthPulseMath.CalibrationUsable(search)||!EighthPulseMath.CalibrationUsable(original)||
                !Finite(locFs)||locFs<=0||!Finite(dutWindow)||dutWindow<=0||
                !Finite(search.RemovedLeadRoundTripSeconds)||search.RemovedLeadRoundTripSeconds<0||
                original.RemovedLeadRoundTripSeconds!=0||
                (r.BoundaryKind=="dut_to_terminal"&&search.RemovedLeadRoundTripSeconds!=0)||
                !Object.ReferenceEquals(original.Incident,search.Incident)||!Object.ReferenceEquals(original.Reflection,search.Reflection)||
                original.SampleRateHz!=search.SampleRateHz||
                original.Incident.SampleRateHz!=original.SampleRateHz||original.Reflection.SampleRateHz!=original.SampleRateHz||
                !Same(original.RoundTripSeconds,((double)original.Reflection.Peak-original.Incident.Peak)/original.SampleRateHz)||
                !Same(original.RoundTripSeconds-search.RemovedLeadRoundTripSeconds,search.RoundTripSeconds)||
                !Same(original.OnsetRoundTripSeconds-search.RemovedLeadRoundTripSeconds,search.OnsetRoundTripSeconds)||
                !Same(original.OnsetRoundTripMinimumSeconds-search.RemovedLeadRoundTripSeconds,search.OnsetRoundTripMinimumSeconds)||
                !Same(original.OnsetRoundTripMaximumSeconds-search.RemovedLeadRoundTripSeconds,search.OnsetRoundTripMaximumSeconds)||
                !Finite(original.RoundTripSeconds*locFs)||original.RoundTripSeconds*locFs<1)
            {r.Reason="physical_loop_clock_provenance_or_geometry_inconsistent";return r;}
            r.PhysicalLoopKnown=true;r.PeakLoopSeconds=original.RoundTripSeconds;
            r.LoopUncertaintySeconds=original.TimingUncertaintySeconds;
            r.ModeDiscrepancySeconds=original.ModeDiscrepancySeconds;
            r.RoleMeasurementUncertaintySeconds=original.MeasurementTimingUncertaintySeconds>0?
                original.MeasurementTimingUncertaintySeconds:original.TimingUncertaintySeconds;
            r.SelectedRoleClockKnown=!firstArrival||EighthPulseMath.OnsetCalibrationUsable(original);
            r.SelectedRoleLoopSeconds=firstArrival?original.OnsetRoundTripSeconds:original.RoundTripSeconds;
            if(firstArrival&&(!Same(original.OnsetRoundTripSeconds,((double)original.Reflection.Onset-original.Incident.Onset)/original.SampleRateHz)||
                !Same(original.OnsetRoundTripMinimumSeconds,((double)original.Reflection.OnsetEarliest-original.Incident.OnsetLatest)/original.SampleRateHz)||
                !Same(original.OnsetRoundTripMaximumSeconds,((double)original.Reflection.OnsetLatest-original.Incident.OnsetEarliest)/original.SampleRateHz)))
                r.SelectedRoleClockKnown=false;
            if(!Finite(r.SelectedRoleLoopSeconds*locFs)||r.SelectedRoleLoopSeconds*locFs<1)r.SelectedRoleClockKnown=false;
            if(!Finite(r.SelectedRoleLoopSeconds))r.SelectedRoleLoopSeconds=0;
            r.Reason=r.SelectedRoleClockKnown?"explicit_boundary_original_CAL_loop_DUT_window_separated":"physical_onset_role_clock_unresolved_no_peak_fallback";
            return r;
        }
    }
}
