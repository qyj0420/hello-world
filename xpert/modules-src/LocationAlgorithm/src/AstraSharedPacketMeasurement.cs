using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Guards are chosen once from support geometry by the caller. This function
    // never moves them, shrinks the original packet, or uses a pair's score.
    private static A4AttenuationMeasurement A4MeasureSharedPacket(A4MeasurementSignal s,A4Pulse p,
        IList<A4Pulse> packets,int leftStart,int leftEnd,int rightStart,int rightEnd)
    {
        var m=new A4AttenuationMeasurement {SupportRule="known_raw_neighbour_midpoint_limited_padding_shared_trend"};
        if(s==null||s.Raw==null||s.BandSignal==null||p==null||packets==null||p.Start<0||p.End>=s.Raw.Length||p.End<p.Start)
        {m.Reason="invalid_shared_measurement_inputs";return m;}
        m.Start=m.RawStart=p.Start;m.End=m.RawEnd=p.End;
        if(p.Clipped||p.UnresolvedOverlap)
        {m.Reason=p.Clipped?"original_packet_clipped":"original_packet_unresolved_overlap";return m;}
        if(leftStart<0||leftEnd>=p.Start||rightStart<=p.End||rightEnd>=s.Raw.Length||
            leftEnd-leftStart+1<16||rightEnd-rightStart+1<16)
        {m.Reason="two_original_quiet_guards_of_16_samples_required";return m;}
        int minimum=0,maximum=s.Raw.Length-1;
        foreach(A4Pulse q in packets)
        {
            if(q==null){m.Reason="original_neighbour_packet_unavailable";return m;}
            if(q.Id==p.Id)continue;
            if(q.Start<=p.End&&q.End>=p.Start){m.Reason="raw_scoring_support_overlaps_other_packet";return m;}
            if(q.Start<=leftEnd&&q.End>=leftStart||q.Start<=rightEnd&&q.End>=rightStart)
            {m.Reason="raw_guard_overlaps_known_packet";return m;}
            if(q.End<p.Start)minimum=Math.Max(minimum,(int)(((long)q.End+p.Start)/2)+1);
            if(q.Start>p.End)maximum=Math.Min(maximum,(int)(((long)p.End+q.Start)/2));
        }
        int begin=Math.Max(Math.Max(0,minimum),p.Start-16),end=Math.Min(Math.Min(s.Raw.Length-1,maximum),p.End+16);
        if(begin==0||end==s.Raw.Length-1){m.Reason="diagnostic_measurement_touches_record_edge";return m;}
        double leftCenter=(leftStart+leftEnd)/2d,rightCenter=(rightStart+rightEnd)/2d;
        double leftLevel=Median(A4Slice(s.DetectionRaw,leftStart,leftEnd)),rightLevel=Median(A4Slice(s.DetectionRaw,rightStart,rightEnd));
        double slope=(rightLevel-leftLevel)/(rightCenter-leftCenter),origin=(p.Start+p.End)/2d;
        double intercept=leftLevel+slope*(origin-leftCenter);
        // Remove a full-record linear response, not a high-pass of a short pulse
        // crop. DC and the local line commute with the fixed linear band operator.
        double rampScale=slope*Math.Max(1,s.Raw.Length-1);
        double[] leftRaw=new double[leftEnd-leftStart+1],rightRaw=new double[rightEnd-rightStart+1];
        double[] leftBand=new double[leftRaw.Length],rightBand=new double[rightRaw.Length];
        for(int side=0;side<2;side++)
        {
            int first=side==0?leftStart:rightStart;double[] raw=side==0?leftRaw:rightRaw,band=side==0?leftBand:rightBand;
            for(int j=0;j<raw.Length;j++)
            {
                int i=first+j;raw[j]=s.DetectionRaw[i]-(intercept+slope*(i-origin));
                band[j]=s.BandSignal[i]-rampScale*s.BandRamp[i];
            }
        }
        // One-step differences underestimate colored acquisition noise. Keep the
        // independently measured, long-trend-corrected ORIGINAL noise floor;
        // local guard RMS may increase uncertainty but cannot excuse a dirty guard.
        double independent=Math.Max(1e-15,Math.Max(s.RawSigma,s.DifferenceSigma));
        m.RawNoise=independent;
        foreach(double[] guard in new[]{leftRaw,rightRaw})
        {
            double center=Median(guard);
            m.RawNoise=Math.Max(m.RawNoise,Math.Max(RobustSigma(guard,center),A4AttenuationGuardRms(guard,center)));
        }
        m.Baseline=.5*(Median(leftBand)+Median(rightBand));
        m.Noise=m.RawNoise;
        // The gain<=1 band operator spreads a real packet into neighbouring
        // original quiet guards. This deterministic pre/post ringing is not a
        // new random-noise estimate; retain the conservative ORIGINAL noise.
        m.RawBaselineOrigin=(int)Math.Round(origin);
        m.RawBaseline=s.Trend[m.RawBaselineOrigin]+intercept+slope*(m.RawBaselineOrigin-origin);
        m.RawSlope=slope+(s.Trend[Math.Min(s.Trend.Length-1,m.RawBaselineOrigin+1)]-
            s.Trend[Math.Max(0,m.RawBaselineOrigin-1)])/2;
        m.SignalOrigin=begin;m.SignalSamples=new double[end-begin+1];m.RawBaselineSamples=new double[m.SignalSamples.Length];
        for(int i=begin;i<=end;i++)
        {
            m.SignalSamples[i-begin]=s.BandSignal[i]-rampScale*s.BandRamp[i];
            m.RawBaselineSamples[i-begin]=s.Trend[i]+intercept+slope*(i-origin);
            if(i>=p.Start&&i<=p.End)m.RawAmplitude=Math.Max(m.RawAmplitude,Math.Abs(s.Raw[i]-m.RawBaselineSamples[i-begin]));
        }
        if(!Finite(m.RawAmplitude)||m.RawAmplitude<=0||!Finite(m.Noise))
        {m.Reason="invalid_original_amplitude_or_noise";return m;}
        foreach(double[] guard in new[]{leftRaw,rightRaw})
        {
            double center=Median(guard),peak=0;int active=0;
            foreach(double value in guard){double z=Math.Abs(value-center);peak=Math.Max(peak,z);if(z>4*independent)active++;}
            if(peak>Math.Max(6*independent,.15*m.RawAmplitude)||active>guard.Length*.25)
            {m.Reason="original_guard_contains_unresolved_signal_after_detrend";return m;}
            int half=guard.Length/2;
            double curvature=Math.Abs(Median(A4Slice(guard,0,half-1))-Median(A4Slice(guard,half,guard.Length-1)));
            if(curvature>3*m.RawNoise){m.Reason="original_guard_nonlinear_drift_after_detrend";return m;}
        }
        double extensionLimit=Math.Max(6*independent,.15*m.RawAmplitude);
        for(int i=begin;i<=end;i++)
        {
            if((i<p.Start||i>p.End)&&Math.Abs(s.Raw[i]-m.RawBaselineSamples[i-begin])>extensionLimit)
            {m.Reason="low_band_extension_contains_original_activity";return m;}
            double amplitude=Math.Abs(m.SignalSamples[i-begin]-m.Baseline);
            if(amplitude>m.Amplitude){m.Amplitude=amplitude;m.Peak=i;}
        }
        m.Start=begin;m.End=end;m.Snr=m.Amplitude/m.Noise;
        if(!Finite(m.Amplitude)||m.Amplitude<=0||!Finite(m.Snr))
        {m.Reason="invalid_shared_band_amplitude_or_snr";return m;}
        if(m.Peak==begin||m.Peak==end||Math.Abs(m.SignalSamples[0]-m.Baseline)>=.85*m.Amplitude||
            Math.Abs(m.SignalSamples[m.SignalSamples.Length-1]-m.Baseline)>=.85*m.Amplitude)
        {m.Reason="low_band_peak_unresolved_at_measurement_edge";return m;}
        m.UncertaintyDb=A4LowBandAmplitudeErrorDb(m.Amplitude,m.Noise);
        m.Valid=true;m.Reason=m.Snr<10?"shared_fixed_band_original_guards_valid_low_snr":"shared_fixed_band_original_guards_valid";
        return m;
    }
}
