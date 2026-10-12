using System;

namespace HVX.SignalEvidence
{
    // Residuals are measured ORIGINAL samples minus the caller's guarded raw
    // baseline, and the fixed-band samples minus their own guarded baseline.
    // Callers preserve the full support; no missing sample is padded or removed.
    internal sealed class SeventhPacketInput
    {
        internal double[] RawResidual,FixedBandResidual;
        internal int RawOrigin,FixedBandOrigin;
        internal double SampleRateHz=double.NaN,RawNoise=double.NaN,BandNoise=double.NaN;
        internal bool CompleteGuard;
    }
    internal sealed class SeventhPacketMeasurement
    {
        internal bool Valid;
        internal int MainPeak=-1,OppositePeak=-1,Sign;
        internal double Main=double.NaN,Opposite=double.NaN,Noise=double.NaN;
        internal A7PulseDescriptor Shape=new A7PulseDescriptor();
        internal RawEndpointShape RawShape=new RawEndpointShape();
        internal string Reason="complete_guarded_packet_unavailable";
    }
    internal static class SeventhPacketMath
    {
        internal const string Version="2026.09.22-seventh-guarded-dual-lobe-packet1";
        internal static SeventhPacketMeasurement Measure(SeventhPacketInput x)
        {
            if(x==null)throw new ArgumentNullException("x");
            var r=new SeventhPacketMeasurement();
            if(!x.CompleteGuard)return r;
            if(x.RawResidual==null||x.FixedBandResidual==null||x.RawResidual.Length<3||x.FixedBandResidual.Length<3||
                x.RawResidual.Length>65536||x.FixedBandResidual.Length>65536||x.RawOrigin<0||x.FixedBandOrigin<0||
                (long)x.RawOrigin+x.RawResidual.Length>int.MaxValue||(long)x.FixedBandOrigin+x.FixedBandResidual.Length>int.MaxValue||
                !ConfidenceMath.Finite(x.SampleRateHz)||x.SampleRateHz<6000000||
                !ConfidenceMath.Finite(x.RawNoise)||x.RawNoise<0||!ConfidenceMath.Finite(x.BandNoise)||x.BandNoise<0)
            {r.Reason="packet_arrays_noise_or_sampling_invalid";return r;}
            int peak=-1;double maximum=0;
            for(int i=0;i<x.RawResidual.Length;i++)
            {
                double value=x.RawResidual[i];if(!ConfidenceMath.Finite(value)){r.Reason="raw_packet_has_nonfinite_samples";return r;}
                if(Math.Abs(value)>maximum){maximum=Math.Abs(value);peak=i;}
            }
            if(peak<=0||peak>=x.RawResidual.Length-1||maximum<=0){r.Reason="raw_main_peak_unresolved_at_support_edge";return r;}
            r.Sign=Math.Sign(x.RawResidual[peak]);r.Main=maximum;r.MainPeak=x.RawOrigin+peak;
            r.RawShape=RawEndpointMath.Measure(x.RawResidual,x.SampleRateHz,x.RawNoise);
            r.Opposite=0;r.Noise=x.RawNoise;
            for(int i=0;i<x.RawResidual.Length;i++)if(-r.Sign*x.RawResidual[i]>r.Opposite)
            {r.Opposite=-r.Sign*x.RawResidual[i];r.OppositePeak=x.RawOrigin+i;}
            int bandPeak=-1;double bandMaximum=0;
            for(int i=0;i<x.FixedBandResidual.Length;i++)
            {
                double value=x.FixedBandResidual[i];if(!ConfidenceMath.Finite(value)){r.Reason="fixed_band_packet_has_nonfinite_samples";return r;}
                if(r.Sign*value>bandMaximum){bandMaximum=r.Sign*value;bandPeak=i;}
            }
            if(bandPeak<=0||bandPeak>=x.FixedBandResidual.Length-1){r.Reason="same_polarity_fixed_band_peak_unresolved";return r;}
            r.Shape=A7WaveformMath.Measure(x.FixedBandResidual,0,x.FixedBandResidual.Length-1,bandPeak,0,Math.Max(1e-15,x.BandNoise),x.SampleRateHz);
            r.Valid=r.Shape.Valid;
            if(r.Valid)r.Reason="complete_guarded_raw_dual_lobes_and_fixed_band_shape";
            else
            {
                // Explain the failed fixed-band measurement without changing
                // its gate or treating an incomplete packet as a scored pair.
                double bandAbsoluteMaximum=0;
                foreach(double value in x.FixedBandResidual)bandAbsoluteMaximum=Math.Max(bandAbsoluteMaximum,Math.Abs(value));
                double bandFloor=5*Math.Max(1e-15,x.BandNoise);
                r.Reason=bandAbsoluteMaximum<bandFloor?"fixed_band_shape_below_five_sigma":
                    bandMaximum<.5*bandAbsoluteMaximum?"fixed_band_opposite_lobe_dominates":"fixed_band_shape_unavailable";
            }
            return r;
        }
    }
}
