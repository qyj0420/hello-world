using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Method 7 only. A CAL time marker, charge coefficient and waveform template
    // are different capabilities. A weak/mismatched shape never authorizes an
    // echo, but it must not silently erase an otherwise measured time reference.
    private sealed class A7CalibrationCapabilities
    {
        internal A4AttenuationScoringReference Reference=new A4AttenuationScoringReference();
        internal bool MarkerTimingValid,TimingValid,ChargeCalibrationValid,TemplateReliable,AttenuationReliable,NonmonotonicAmplitude;
        internal double TravelSeconds=double.NaN;
        internal string DiagnosticJson="{}";
    }

    private static A7CalibrationCapabilities A7BuildCalibrationCapabilities(A4Context c)
    {
        var cap=new A7CalibrationCapabilities();var r=cap.Reference;
        ParsedCalibration cal=c==null?null:c.Calibration;
        string reason="calibration_not_found_or_too_short",incidentProof="null",reflectionProof="null";
        if(cal==null||cal.Samples==null||cal.Samples.Length<64)return A7FinishCalibration(cap,reason,incidentProof,reflectionProof);
        if(cal.PacketIdentity!=null) {
            cal.PacketIdentity.ValidateBinding(cal.Phase,cal.CalibrationValuePc,cal.SampleRate,cal.Samples,
                cal.CalibrationCoefficient,cal.StartTime,cal.FinalTime);
            r.PacketIdentityPolicy="validated_saved_peak_identity_original_packet_gates";r.TravelClockPolicy="saved_CAL_time_unchanged";
        }
        cap.ChargeCalibrationValid=Finite(cal.CalibrationCoefficient)&&cal.CalibrationCoefficient>0&&
            Finite(cal.CalibrationValuePc)&&cal.CalibrationValuePc>0;
        if(!Finite(cal.SampleRate)||cal.SampleRate<6000000||cal.Samples.Any(v=>!Finite(v)))
            return A7FinishCalibration(cap,"calibration_samples_or_sampling_rate_invalid",incidentProof,reflectionProof);
        double last=(cal.Samples.Length-1)/cal.SampleRate;
        if(!Finite(cal.StartTime)||!Finite(cal.FinalTime)||cal.StartTime<0||cal.FinalTime<=cal.StartTime||cal.FinalTime>last)
            return A7FinishCalibration(cap,"calibration_markers_outside_record_or_not_ordered",incidentProof,reflectionProof);
        int markedA=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0),
            markedB=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
        if(markedB-markedA<2)return A7FinishCalibration(cap,"calibration_marked_round_trip_less_than_two_samples",incidentProof,reflectionProof);
        cap.MarkerTimingValid=true;cap.TravelSeconds=r.TravelSeconds=cal.PacketIdentity==null?(markedB-markedA)/cal.SampleRate:cal.FinalTime-cal.StartTime;
        A4MeasurementSignal profile=A4PrepareMeasurementSignal(cal.Samples,cal.SampleRate,markedB-markedA);
        var context=new A4Context {Raw=profile.DetectionRaw,Signal=profile.DetectionRaw,Fs=cal.SampleRate,
            Center=profile.DetectionCenter,RawCenter=profile.DetectionCenter,Sigma=profile.RawSigma,
            Threshold=4.5*profile.RawSigma,RoundTrip=markedB-markedA,RoundTripExact=markedB-markedA};
        List<A4Pulse> packets=A4Extract(context);
        int anchorA=cal.PacketIdentity==null?markedA:cal.PacketIdentity.IncidentPeakIndex,
            anchorB=cal.PacketIdentity==null?markedB:cal.PacketIdentity.ReflectionPeakIndex;
        A4Pulse a=packets.FirstOrDefault(p=>p.Start<=anchorA&&p.End>=anchorA),
            b=packets.FirstOrDefault(p=>p.Start<=anchorB&&p.End>=anchorB);
        if(a==null||b==null||a.Id==b.Id||a.End>=b.Start)
            return A7FinishCalibration(cap,"calibration_marked_original_packets_not_separable",incidentProof,reflectionProof);
        if(a.Clipped||b.Clipped||a.UnresolvedOverlap||b.UnresolvedOverlap||
            !A7CalibrationRawPeakUnclipped(cal.Samples,a)||!A7CalibrationRawPeakUnclipped(cal.Samples,b))
            return A7FinishCalibration(cap,"calibration_original_packet_clipped_or_overlap",incidentProof,reflectionProof);
        context.Raw=cal.Samples;context.MeasurementSignal=profile;
        context.AttenuationScoringSignal=profile.BandSignal;context.AttenuationScoringRawSigma=profile.RawSigma;
        context.AttenuationScoringPackets=packets;
        r.Incident=A7MeasureCalibrationPacket(context,a,packets,out incidentProof);
        r.Reflection=A7MeasureCalibrationPacket(context,b,packets,out reflectionProof);
        r.Unclipped=true;r.IndependentCompletePackets=r.Incident.Valid&&r.Reflection.Valid;
        r.IncidentSnr=r.Incident.RawAmplitude/Math.Max(1e-15,r.Incident.RawNoise);
        r.ReflectionSnr=r.Reflection.RawAmplitude/Math.Max(1e-15,r.Reflection.RawNoise);
        cap.TimingValid=r.IndependentCompletePackets&&Finite(r.IncidentSnr)&&Finite(r.ReflectionSnr)&&r.IncidentSnr>=3&&r.ReflectionSnr>=3;
        if(r.Incident.RawAmplitude>0)r.RawRetention=r.Reflection.RawAmplitude/r.Incident.RawAmplitude;
        if(r.Incident.Amplitude>0)r.Retention=r.Reflection.Amplitude/r.Incident.Amplitude;
        cap.NonmonotonicAmplitude=Finite(r.RawRetention)&&r.RawRetention>1;
        r.Quality=CalibrationQualityMath.Evaluate(new CalibrationQualityInput {Identified=true,
            IndependentCompletePackets=r.IndependentCompletePackets,Unclipped=r.Unclipped,
            IncidentAmplitude=r.Incident.RawAmplitude,ReflectionAmplitude=r.Reflection.RawAmplitude,
            IncidentNoise=r.Incident.RawNoise,ReflectionNoise=r.Reflection.RawNoise});
        bool recognized=cap.TimingValid&&r.Quality.Recognized;
        var ai=A7PipelineMeasure(cal.Samples,r.Incident,cal.SampleRate);
        var ar=A7PipelineMeasure(cal.Samples,r.Reflection,cal.SampleRate);
        cap.TemplateReliable=recognized&&ai.Valid&&ar.Valid;
        bool same=ai.MainPeak>=0&&ar.MainPeak>=0&&ai.Sign==ar.Sign;
        cap.AttenuationReliable=recognized&&same&&Finite(r.RawRetention)&&r.RawRetention>0&&r.RawRetention<=1;
        r.Valid=cap.TemplateReliable&&cap.AttenuationReliable;
        r.UncertaintyDb=r.Incident.UncertaintyDb+r.Reflection.UncertaintyDb;
        reason=!cap.TimingValid?"calibration_guarded_timing_unreliable":
            cap.NonmonotonicAmplitude?"calibration_time_preserved_nonmonotonic_amplitude_no_exponential_loss_reference":
            !cap.TemplateReliable?"calibration_time_preserved_template_unreliable":
            !cap.AttenuationReliable?"calibration_time_preserved_attenuation_unreliable":"seventh_separate_calibration_capabilities_reliable";
        return A7FinishCalibration(cap,reason,incidentProof,reflectionProof);
    }

    private static A7CalibrationCapabilities A7FinishCalibration(A7CalibrationCapabilities cap,string reason,string incidentProof,string reflectionProof)
    {
        cap.Reference.Reason=reason;
        cap.DiagnosticJson="{\"policy\":\"seventh_time_shape_attenuation_separate_capabilities\",\"marker_timing_valid\":"+(cap.MarkerTimingValid?"true":"false")+
            ",\"timing_valid\":"+(cap.TimingValid?"true":"false")+",\"template_reliable\":"+(cap.TemplateReliable?"true":"false")+
            ",\"charge_calibration_coefficient_valid\":"+(cap.ChargeCalibrationValid?"true":"false")+",\"coefficient_reapplied_to_restored_LOC_charge\":false"+
            ",\"attenuation_reliable\":"+(cap.AttenuationReliable?"true":"false")+",\"nonmonotonic_amplitude\":"+(cap.NonmonotonicAmplitude?"true":"false")+
            ",\"marker_round_trip_seconds\":"+NumberJson(cap.TravelSeconds)+",\"does_not_override_cable_geometry\":true,\"raw_retention\":"+NumberJson(cap.Reference.RawRetention)+
            (cap.Reference.PacketIdentityPolicy=="legacy_marker_association"?"":",\"packet_identity_policy\":"+Json(cap.Reference.PacketIdentityPolicy)+",\"travel_clock_policy\":"+Json(cap.Reference.TravelClockPolicy))+
            ",\"incident_raw_snr\":"+NumberJson(cap.Reference.IncidentSnr)+",\"reflection_raw_snr\":"+NumberJson(cap.Reference.ReflectionSnr)+
            ",\"reason\":"+Json(reason)+",\"incident_guard_proof\":"+incidentProof+",\"reflection_guard_proof\":"+reflectionProof+"}";
        return cap;
    }

    private static A4AttenuationMeasurement A7MeasureCalibrationPacket(A4Context context,A4Pulse p,List<A4Pulse> packets,out string proof)
    {
        A6OriginalGuardTrace trace;var original=A6ScoringMeasurementCore(context,p,out trace);
        double originalSnr=original.RawAmplitude/Math.Max(1e-15,original.RawNoise);
        proof="{\"policy\":\"original_calibration_guards\",\"raw_snr\":"+NumberJson(originalSnr)+",\"reason\":"+Json(original.Reason)+"}";
        if(original.Valid&&Finite(originalSnr)&&originalSnr>=6)return original;
        if(p.Clipped||p.UnresolvedOverlap)return original;
        // One geometry/quietness-selected pair, never a score/SNR-maximizing retry.
        // A measured non-quiet shoulder can be excluded from a noise guard; it is
        // not deleted from the waveform or moved into a favourable pulse crop.
        int left,right;double floor=Math.Max(1e-15,Math.Max(context.MeasurementSignal.RawSigma,context.MeasurementSignal.DifferenceSigma));
        if(!A7CalibrationQuietGuard(context.MeasurementSignal,p,packets,true,floor,out left)||
            !A7CalibrationQuietGuard(context.MeasurementSignal,p,packets,false,floor,out right))return original;
        var measured=A4MeasureSharedPacket(context.MeasurementSignal,p,packets,left,left+15,right,right+15);
        proof="{\"policy\":\"nearest_support_excluded_proven_quiet_16_original_samples_one_pass\",\"left_start\":"+left+
            ",\"left_end\":"+(left+15)+",\"right_start\":"+right+",\"right_end\":"+(right+15)+
            ",\"independent_full_record_noise_floor\":"+NumberJson(floor)+",\"original_raw_snr\":"+NumberJson(originalSnr)+
            ",\"measured_raw_snr\":"+NumberJson(measured.RawAmplitude/Math.Max(1e-15,measured.RawNoise))+",\"reason\":"+Json(measured.Reason)+"}";
        return measured.Valid?measured:original;
    }

    private static bool A7CalibrationQuietGuard(A4MeasurementSignal s,A4Pulse p,List<A4Pulse> packets,bool left,double sigma,out int selected)
    {
        selected=-1;int radius=Math.Min(4096,Math.Max(64,s.Raw.Length));
        int first=left?Math.Max(0,p.Start-radius):p.End+1;
        int last=left?p.Start-16:Math.Min(s.Raw.Length-16,p.End+radius);
        for(int step=0;step<=last-first;step++)
        {
            int start=left?last-step:first+step,end=start+15;
            if(packets.Any(q=>q.Id!=p.Id&&q.Start<=end&&q.End>=start))continue;
            var values=A4Slice(s.DetectionRaw,start,end);if(values.Any(v=>!Finite(v)))continue;
            double center=Median(values);
            if(Math.Abs(center-s.DetectionCenter)>3*sigma||A4AttenuationGuardRms(values,center)>1.25*sigma||
                Math.Abs(Median(values.Take(8).ToArray())-Median(values.Skip(8).ToArray()))>sigma||values.Max(v=>Math.Abs(v-center))>4*sigma)continue;
            selected=start;return true;
        }
        return false;
    }

    private static bool A7CalibrationRawPeakUnclipped(double[] raw,A4Pulse p)
    {
        // Test stored samples, not detrended values: subtraction of a small
        // slope can otherwise make a genuinely flat ADC plateau look resolved.
        double baseline=Median(raw);int peak=-1;double magnitude=0;
        for(int i=p.Start;i<=p.End;i++)if(Math.Abs(raw[i]-baseline)>magnitude)
        {magnitude=Math.Abs(raw[i]-baseline);peak=i;}
        if(peak<=0||peak>=raw.Length-1||magnitude<=0)return false;
        double epsilon=Math.Max(1e-15,magnitude*1e-12);int equal=1;
        for(int i=peak-1;i>=0&&equal<3&&Math.Abs(raw[i]-raw[peak])<=epsilon;i--)equal++;
        for(int i=peak+1;i<raw.Length&&equal<3&&Math.Abs(raw[i]-raw[peak])<=epsilon;i++)equal++;
        return equal<3;
    }
}
