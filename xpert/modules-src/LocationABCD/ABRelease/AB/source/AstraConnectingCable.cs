using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HVX.Geometry;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    internal double ConnectingCableLengthM { get; set; }
    internal HVX.SignalEvidence.RawEndpointPolicy ManufacturerEndpointPolicy;

    private void A4ValidateConnectingCableSetting()
    {
        if(!Finite(ConnectingCableLengthM)||ConnectingCableLengthM<0)
            throw new ArgumentOutOfRangeException("ConnectingCableLengthM","Connecting cable length must be finite and nonnegative.");
    }

    // This runs before discovery/context creation. The raw CAL object and all measured
    // pulse indices/delays remain untouched: the common lead path cancels in PD delay.
    private string A4ApplyConnectingCable(ref CableMetadata cable,ParsedCalibration cal,bool calibrationGeometryAllowed,
        ref double length,ref double velocity,ref double[] joints,ref double[] velocities)
    {
        A4ValidateConnectingCableSetting();
        if(ConnectingCableLengthM==0)return "{\"enabled\":false,\"length_m\":0}";
        if(!calibrationGeometryAllowed||cal==null||cal.Samples==null||cal.Samples.Length<2||
            !Finite(cal.SampleRate)||cal.SampleRate<=0||!Finite(cal.StartTime)||!Finite(cal.FinalTime)||
            cal.StartTime<0||cal.FinalTime<=cal.StartTime||cal.FinalTime>(cal.Samples.Length-1)/cal.SampleRate)
            throw new InvalidDataException("Connecting cable compensation requires ordered, in-record same-phase/ALL CAL markers and a valid CAL sampling rate.");
        int a=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0),
            b=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
        if(b<=a)throw new InvalidDataException("Connecting cable CAL markers resolve to an empty travel interval.");
        double rawRoundTripUs=(b-a)/cal.SampleRate*1e6;
        var known=new bool[velocities.Length];
        for(int i=0;i<known.Length;i++)
        {
            double segmentStart=joints[i],segmentEnd=joints[i+1];
            var segment=cable.Segments.FirstOrDefault(s=>s.StartM<=segmentStart+1e-6&&s.EndM>=segmentEnd-1e-6);
            double materialVelocity;
            known[i]=segment!=null&&A4TryVelocity(segment.InsulationType??"",out materialVelocity);
        }
        var result=ConnectingCableGeometry.Build(length,ConnectingCableLengthM,true,rawRoundTripUs,
            joints,velocities,known,2e6/cal.SampleRate);
        if(!result.CanUseCalibrationGeometry)
            throw new InvalidDataException("Connecting cable geometry conflicts with CAL travel time and explicit DUT segment velocities; no automatic positioning result was published.");
        double lead=ConnectingCableLengthM;
        var shiftedSegments=new List<CableSegment>();
        foreach(var s in cable.Segments)
        {
            double start=Math.Max(lead,s.StartM),end=Math.Min(length,s.EndM);
            if(end>start)shiftedSegments.Add(new CableSegment {StartM=start-lead,EndM=end-lead,InsulationType=s.InsulationType});
        }
        double originalLength=length;
        var shiftedAccessories=cable.Accessories.Where(x=>x.PositionM>=lead&&x.PositionM<=originalLength)
            .Select(x=>new CableAccessory {Name=x.Name,Type=x.Type,PositionM=x.PositionM-lead}).ToList();
        joints=result.DutJointsM;velocities=result.DutSegmentVelocitiesMPerUs;
        length=result.DutLengthM;velocity=2*length/result.GeometryDutRoundTripUs;
        cable=new CableMetadata {SelectedPhase=cable.SelectedPhase,SelectionSource=cable.SelectionSource,
            SamePhaseAvailable=cable.SamePhaseAvailable,DuplicateBlocksCollapsed=cable.DuplicateBlocksCollapsed,
            PhaseLengthsJson=cable.PhaseLengthsJson,CableLength=length,VelocityMPerUs=velocity,
            Accessories=shiftedAccessories,Segments=shiftedSegments,JointPositionsIncludingEnds=joints.ToList(),
            InternalJointPositions=joints.Skip(1).Take(joints.Length-2).ToList()};
        return "{\"enabled\":true,\"length_m\":"+NumberJson(lead)+",\"velocity_m_per_us\":150,"+
            "\"calibration_injection\":\"device\",\"source_total_length_m\":"+NumberJson(result.TotalLengthM)+
            ",\"dut_length_m\":"+NumberJson(length)+",\"calibration_original_round_trip_us\":"+NumberJson(rawRoundTripUs)+
            ",\"connecting_cable_round_trip_us\":"+NumberJson(result.ConnectingCableRoundTripUs)+
            ",\"calibration_dut_round_trip_us\":"+NumberJson(result.CalibrationDutRoundTripUs)+
            ",\"geometry_dut_round_trip_us\":"+NumberJson(result.GeometryDutRoundTripUs)+
            ",\"unknown_velocity_resolved_from_calibration\":"+(result.UsedCalibrationForUnknownVelocity?"true":"false")+
            ",\"pd_measured_delay_unchanged\":true,\"raw_calibration_attenuation_reference_unchanged\":true,"+
            "\"additional_lead_amplitude_compensation\":false,\"historical_rows_reprojected\":false}";
    }
}
