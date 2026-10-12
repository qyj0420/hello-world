using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A4AttenuationMeasurement
    {
        internal bool Valid;
        internal string Reason="not_measured";
        internal double Amplitude,Noise,Snr,Baseline,RawAmplitude,UncertaintyDb;
        // Baseline belongs ONLY to SignalSamples. Original lobe energies use the
        // original-domain baseline accessor, never subtract this processed value.
        internal double[] SignalSamples,RawBaselineSamples;
        internal int SignalOrigin,RawBaselineOrigin;
        internal double RawBaseline,RawSlope,RawNoise;
        internal int Start=-1,End=-1,Peak=-1,RawStart=-1,RawEnd=-1;
        internal string SupportRule="unbounded_legacy_padding";
    }
    private sealed class A4AttenuationScoringReference
    {
        internal bool Valid;
        internal string Reason="not_measured";
        internal double Retention,RawRetention,IncidentSnr,ReflectionSnr,TravelSeconds,UncertaintyDb;
        internal A4AttenuationMeasurement Incident,Reflection;
        internal bool IndependentCompletePackets,Unclipped;
        internal HVX.SignalEvidence.CalibrationQualityResult Quality;
    }
    private static double A4AttenuationGuardRms(double[] values,double center)
    {
        double scale=0;foreach(double value in values)scale=Math.Max(scale,Math.Abs(value-center));
        if(scale==0)return 0;
        double energy=0;foreach(double value in values){double z=(value-center)/scale;energy+=z*z;}
        return scale*Math.Sqrt(energy/values.Length);
    }
    // rawGuards MUST be two neighbouring-support-excluded ORIGINAL sample arrays.
    // They are explicit rather than read from p, whose guards may belong to a user-filtered analysis window.
    // rawSigma is likewise an original-signal estimate. A low-pass has gain <= 1, so retaining this
    // conservative noise scale avoids misclassifying deterministic pre-ringing as measured random noise.
    // This function reads all inputs, including p, without modifying their support, timing, ownership or data.
    private static A4AttenuationMeasurement A4MeasureAttenuationPacket(double[] raw,double[] lowSignal,
        A4Pulse p,double rawCenter,double rawSigma,double[][] rawGuards)
    {
        return A4MeasureAttenuationPacketCore(raw,lowSignal,p,rawCenter,rawSigma,rawGuards,
            0,raw==null?-1:raw.Length-1,"unbounded_legacy_padding");
    }

    // Divide the quiet gap between known ORIGINAL packets at its integer midpoint.
    // This is a geometry-only support rule: it never inspects a candidate's correlation,
    // expected attenuation, position, polarity or score. Diagnostic padding cannot own
    // the same sample as a neighbour, enter its raw support or turn two distinct raw
    // packets into an artificial overlap. True raw overlap remains invalid.
    private static A4AttenuationMeasurement A4MeasureAttenuationPacketWithinNeighbours(double[] raw,double[] lowSignal,
        A4Pulse p,double rawCenter,double rawSigma,double[][] rawGuards,IList<A4Pulse> packets)
    {
        if(raw==null||p==null||packets==null)
            return new A4AttenuationMeasurement {Reason="original_neighbour_supports_unavailable"};
        int lower=0,upper=raw.Length-1;
        foreach(A4Pulse q in packets)
        {
            if(q==null)return new A4AttenuationMeasurement {Reason="original_neighbour_packet_unavailable"};
            // Packet identity is the same immutable ID used by guard occupancy and
            // the prepared correlation signature, not managed-object identity.
            if(q.Id==p.Id)continue;
            if(q.Start<=p.End&&q.End>=p.Start)
                return new A4AttenuationMeasurement {Reason="raw_scoring_support_overlaps_other_packet"};
            if(q.End<p.Start)lower=Math.Max(lower,(int)(((long)q.End+p.Start)/2)+1);
            if(q.Start>p.End)upper=Math.Min(upper,(int)(((long)p.End+q.Start)/2));
        }
        return A4MeasureAttenuationPacketCore(raw,lowSignal,p,rawCenter,rawSigma,rawGuards,
            lower,upper,"known_raw_neighbour_midpoint_limited_padding");
    }

    private static A4AttenuationMeasurement A4MeasureAttenuationPacketCore(double[] raw,double[] lowSignal,
        A4Pulse p,double rawCenter,double rawSigma,double[][] rawGuards,int minimumStart,int maximumEnd,string supportRule)
    {
        var m=new A4AttenuationMeasurement {SupportRule=supportRule};
        if(raw==null||lowSignal==null||raw.Length!=lowSignal.Length||p==null||
            p.Start<0||p.End>=raw.Length||p.End<p.Start||minimumStart>p.Start||maximumEnd<p.End||
            !Finite(rawCenter)||!Finite(rawSigma)||rawSigma<0)
        {m.Reason="invalid_original_packet_or_signal";return m;}
        m.Start=m.RawStart=p.Start;m.End=m.RawEnd=p.End;
        if(p.Clipped||p.UnresolvedOverlap)
        {m.Reason=p.Clipped?"original_packet_clipped":"original_packet_unresolved_overlap";return m;}
        if(rawGuards==null||rawGuards.Length!=2||rawGuards.Any(g=>g==null||g.Length<16||g.Any(v=>!Finite(v))))
        {m.Reason="two_original_quiet_guards_of_16_samples_required";return m;}
        for(int i=p.Start;i<=p.End;i++)if(!Finite(raw[i])||!Finite(lowSignal[i]))
        {m.Reason="nonfinite_packet_samples";return m;}
        double first=Median(rawGuards[0]),second=Median(rawGuards[1]);
        m.Baseline=first*.5+second*.5;
        double sigma=Math.Max(1e-15,rawSigma);
        m.Noise=sigma;
        for(int side=0;side<2;side++)
        {
            double center=side==0?first:second;
            m.Noise=Math.Max(m.Noise,Math.Max(RobustSigma(rawGuards[side],center),A4AttenuationGuardRms(rawGuards[side],center)));
        }
        for(int i=p.Start;i<=p.End;i++)m.RawAmplitude=Math.Max(m.RawAmplitude,Math.Abs(raw[i]-m.Baseline));
        if(!Finite(m.Noise)||!Finite(m.RawAmplitude)||m.RawAmplitude<=0)
        {m.Reason="invalid_original_amplitude_or_noise";return m;}
        // Local baseline drift is structural evidence, not an excuse to manufacture a reliable CAL ratio.
        if(Math.Abs(first-second)>3*m.Noise||Math.Abs(m.Baseline-rawCenter)>Math.Max(4*sigma,.10*m.RawAmplitude))
        {m.Reason="original_guard_baseline_drift";return m;}
        foreach(double[] side in rawGuards)
        {
            double sideCenter=Median(side),guardPeak=0;int active=0;
            foreach(double value in side)
            {
                double departure=Math.Abs(value-sideCenter);guardPeak=Math.Max(guardPeak,departure);
                if(departure>4*sigma)active++;
            }
            // Use the independent raw noise estimate here, not the potentially contaminated guard RMS.
            if(guardPeak>Math.Max(6*sigma,.15*m.RawAmplitude)||active>side.Length*.25)
            {m.Reason="original_guard_contains_unresolved_signal";return m;}
        }
        // A proven isolated raw packet can broaden under the diagnostic low-pass. Inspect at most
        // 16 adjacent samples on each side, only with two adequate raw guards and no new raw event.
        // Callers construct guards from neighbour-excluded ranges; no plotting or matching window is enlarged.
        int begin=Math.Max(Math.Max(0,minimumStart),p.Start-16),
            end=Math.Min(Math.Min(raw.Length-1,maximumEnd),(int)Math.Min(int.MaxValue,(long)p.End+16));
        if(begin==0||end==raw.Length-1)
        {m.Reason="diagnostic_measurement_touches_record_edge";return m;}
        double extensionLimit=Math.Max(6*sigma,.15*m.RawAmplitude);
        for(int i=begin;i<=end;i++)
        {
            if(!Finite(raw[i])||!Finite(lowSignal[i])){m.Reason="nonfinite_extended_samples";return m;}
            if((i<p.Start||i>p.End)&&Math.Abs(raw[i]-m.Baseline)>extensionLimit)
            {m.Reason="low_band_extension_contains_original_activity";return m;}
        }
        m.Start=begin;m.End=end;
        for(int i=begin;i<=end;i++)
        {
            double amplitude=Math.Abs(lowSignal[i]-m.Baseline);
            if(amplitude>m.Amplitude){m.Amplitude=amplitude;m.Peak=i;}
        }
        if(!Finite(m.Amplitude)||m.Amplitude<=0)
        {m.Reason="invalid_low_band_amplitude";return m;}
        m.Snr=m.Amplitude/m.Noise;
        if(m.Peak==begin||m.Peak==end||
            Math.Abs(lowSignal[begin]-m.Baseline)>=.85*m.Amplitude||Math.Abs(lowSignal[end]-m.Baseline)>=.85*m.Amplitude)
        {m.Reason="low_band_peak_unresolved_at_measurement_edge";return m;}
        if(!Finite(m.Snr)){m.Reason="nonfinite_low_band_snr";return m;}
        m.UncertaintyDb=A4LowBandAmplitudeErrorDb(m.Amplitude,m.Noise);
        // Structural validity and statistical reliability are separate: the
        // common scorer requires CAL >=6 sigma and LOC >=4.5 sigma. Missing
        // reliability is unknown; its nominal weight is not moved to correlation.
        m.Valid=true;m.Reason=m.Snr<10?"original_guards_valid_low_snr":"original_guards_valid";
        return m;
    }
    private static A4AttenuationScoringReference A4BuildAttenuationScoringReference(A4Context c,bool sixthRawAmplitude=false)
    {
        var reference=new A4AttenuationScoringReference();
        if(c==null||!c.Attenuation){reference.Reason="attenuation_reference_disabled";return reference;}
        ParsedCalibration cal=c.Calibration;
        if(cal==null||cal.Samples==null||cal.Samples.Length<64)
        {reference.Reason="calibration_not_found_or_too_short";return reference;}
        if(!Finite(cal.SampleRate)||cal.SampleRate<6000000||!Finite(c.Fs)||c.Fs<6000000||cal.Samples.Any(v=>!Finite(v)))
        {reference.Reason="calibration_samples_or_sampling_rate_invalid";return reference;}
        double maximumTime=(cal.Samples.Length-1)/cal.SampleRate;
        if(!Finite(cal.StartTime)||!Finite(cal.FinalTime)||cal.StartTime<0||cal.FinalTime<=cal.StartTime||cal.FinalTime>maximumTime)
        {reference.Reason="calibration_markers_outside_record_or_not_ordered";return reference;}
        int markedA=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0),
            markedB=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
        if(markedB-markedA<12){reference.Reason="calibration_marked_round_trip_too_short";return reference;}
        A4MeasurementSignal profile=A4PrepareMeasurementSignal(cal.Samples,cal.SampleRate,markedB-markedA);
        double center=profile.DetectionCenter,sigma=profile.RawSigma;
        // Discover complete ORIGINAL packets around the actual calibration markers. A nearby small
        // filtered lobe is never substituted for a missing or structurally unreliable marked packet.
        var rawContext=new A4Context {Raw=profile.DetectionRaw,Signal=profile.DetectionRaw,Fs=cal.SampleRate,Center=center,
            RawCenter=center,Sigma=sigma,Threshold=4.5*sigma,RoundTrip=markedB-markedA,RoundTripExact=markedB-markedA};
        List<A4Pulse> packets=A4Extract(rawContext);
        A4Pulse a=packets.FirstOrDefault(p=>p.Start<=markedA&&p.End>=markedA),
            b=packets.FirstOrDefault(p=>p.Start<=markedB&&p.End>=markedB);
        if(a==null||b==null||a==b||a.End>=b.Start)
        {reference.Reason="calibration_marked_original_packets_not_separable";return reference;}
        if(a.Clipped||b.Clipped){reference.Reason="calibration_original_main_peak_clipped";return reference;}
        reference.Unclipped=true;
        // Extraction used corrected high-bandwidth samples but all packet indices
        // remain original coordinates. CAL and LOC now share the same guard and
        // full-record fixed-band measurement path, without filtering pulse crops.
        rawContext.Raw=cal.Samples;rawContext.MeasurementSignal=profile;
        rawContext.AttenuationScoringSignal=profile.BandSignal;rawContext.AttenuationScoringRawSigma=sigma;
        rawContext.AttenuationScoringPackets=packets;A6OriginalGuardTrace trace;
        reference.Incident=A6ScoringMeasurementCore(rawContext,a,out trace);
        reference.Reflection=A6ScoringMeasurementCore(rawContext,b,out trace);
        // CAL recognition compares original packet amplitude with its original
        // quiet-guard noise. The fixed-band amplitude may be attenuated by the
        // diagnostic filter; its conservative SNR remains unchanged on each
        // measurement and in all LOC evidence gates.
        reference.IncidentSnr=reference.Incident.RawAmplitude/Math.Max(1e-15,reference.Incident.RawNoise);
        reference.ReflectionSnr=reference.Reflection.RawAmplitude/Math.Max(1e-15,reference.Reflection.RawNoise);
        reference.TravelSeconds=(markedB-markedA)/cal.SampleRate;
        if(reference.Incident.Amplitude>0)reference.Retention=reference.Reflection.Amplitude/reference.Incident.Amplitude;
        if(reference.Incident.RawAmplitude>0)reference.RawRetention=reference.Reflection.RawAmplitude/reference.Incident.RawAmplitude;
        // All methods use the same raw marked CAL amplitude convention. The
        // bool parameter is retained internally for existing call sites only.
        {
            var displayed=HVX.SignalEvidence.SixthConfidenceMath.CalibrationFromMarkers(cal.Samples,markedA,markedB,cal.SampleRate);
            if(!displayed.Available){reference.Reason=displayed.Reason;return reference;}
            reference.Quality=displayed.Quality;
            reference.RawRetention=displayed.Retention;reference.TravelSeconds=displayed.TravelSeconds;
        }
        if(!reference.Incident.Valid||!reference.Reflection.Valid)
        {reference.Reason="calibration_incident_"+reference.Incident.Reason+";reflection_"+reference.Reflection.Reason;return reference;}
        reference.IndependentCompletePackets=true;
        if(!HVX.SignalEvidence.SixthConfidenceMath.BasicCalibrationRatioValid(reference.RawRetention))
        {reference.Reason="recognized_calibration_not_compatible_with_nonincreasing_attenuation_model";return reference;}
        reference.UncertaintyDb=reference.Incident.UncertaintyDb+reference.Reflection.UncertaintyDb;
        reference.Valid=reference.Quality!=null&&reference.Quality.Recognized&&reference.IncidentSnr>=6&&reference.ReflectionSnr>=6;
        reference.Reason=reference.Valid?"original_complete_calibration_packets_identified_without_relative_amplitude_floor":
            "calibration_guarded_snr_below_six_sigma";
        return reference;
    }
    private static string A4AttenuationMeasurementJson(A4AttenuationMeasurement m)
    {
        if(m==null)return "null";
        return "{\"valid\":"+(m.Valid?"true":"false")+",\"reason\":"+Json(m.Reason)+
            ",\"amplitude\":"+NumberJson(m.Amplitude)+",\"raw_amplitude\":"+NumberJson(m.RawAmplitude)+
            ",\"noise\":"+NumberJson(m.Noise)+",\"snr\":"+NumberJson(m.Snr)+",\"baseline\":"+NumberJson(m.Baseline)+
            ",\"snr_domain\":\"shared_fixed_band_amplitude_over_original_guard_noise\""+
            ",\"raw_baseline\":"+NumberJson(m.RawBaseline)+",\"raw_slope_per_sample\":"+NumberJson(m.RawSlope)+
            ",\"raw_noise\":"+NumberJson(m.RawNoise)+",\"signal_origin\":"+m.SignalOrigin+
            ",\"baseline_domain\":\"shared_fixed_band\",\"raw_baseline_domain\":\"original_samples\""+
            ",\"start\":"+m.Start+",\"end\":"+m.End+",\"peak\":"+m.Peak+
            ",\"raw_start\":"+m.RawStart+",\"raw_end\":"+m.RawEnd+",\"support_expansion_rule\":"+Json(m.SupportRule)+
            ",\"uncertainty_db\":"+NumberJson(m.UncertaintyDb)+"}";
    }
    private static string A4AttenuationReferenceJson(A4AttenuationScoringReference r)
    {
        if(r==null)return "null";
        return "{\"valid\":"+(r.Valid?"true":"false")+",\"reason\":"+Json(r.Reason)+
            ",\"retention\":"+NumberJson(r.Retention)+",\"raw_retention\":"+NumberJson(r.RawRetention)+
            ",\"incident_snr\":"+NumberJson(r.IncidentSnr)+",\"reflection_snr\":"+NumberJson(r.ReflectionSnr)+
            ",\"snr_domain\":\"original_packet_amplitude_over_original_guard_noise\""+
            ",\"travel_seconds\":"+NumberJson(r.TravelSeconds)+",\"uncertainty_db\":"+NumberJson(r.UncertaintyDb)+
            ",\"raw_calibration_identified\":"+(r.Quality!=null&&r.Quality.Recognized?"true":"false")+
            ",\"raw_ratio_measurement_uncertainty_percent\":"+NumberJson(r.Quality==null?double.NaN:r.Quality.RelativeUncertaintyPercent)+
            ",\"incident\":"+A4AttenuationMeasurementJson(r.Incident)+",\"reflection\":"+A4AttenuationMeasurementJson(r.Reflection)+"}";
    }
}
