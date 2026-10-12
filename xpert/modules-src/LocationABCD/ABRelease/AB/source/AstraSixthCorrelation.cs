using System;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Measurements only. Neither the sign nor these values imply an acceptance decision.
    private sealed class A6Correlation
    {
        internal bool Valid;
        internal double Fixed=double.NaN,Lower=double.NaN,Upper=double.NaN;
        // Shift identifies Upper, not a substituted timing peak. Fixed always uses shift zero.
        internal int Shift,PeakA=-1,PeakB=-1;
        // sign(B)/sign(A) at the explicit shape anchors: +1 same, -1 opposite,
        // 0 unresolved. Production uses corresponding temporal lobes, not independent maxima.
        internal double PolarityRatio;
        internal string AlignmentRule="explicit_anchors_legacy_primitive",StructureReason="not_evaluated";
        internal bool StructureCompatible=true,OppositeTemporalPolarity;
        internal A6TemporalAnchor AnchorA,AnchorB;
    }

    private static bool A6CorrelationSupport(double[] signal,int start,int end,int peak,double baseline,
        out double scale)
    {
        scale=0;
        if(signal==null||start<0||end>=signal.Length||end<=start||peak<start||peak>end||!Finite(baseline))return false;
        double minimum=double.PositiveInfinity,maximum=double.NegativeInfinity;
        for(int i=start;i<=end;i++)
        {
            double value=signal[i]-baseline;
            if(!Finite(signal[i])||!Finite(value))return false;
            minimum=Math.Min(minimum,value);maximum=Math.Max(maximum,value);
            scale=Math.Max(scale,Math.Abs(value));
        }
        // Zero padding must not manufacture variance for an intrinsically constant measured support.
        return maximum>minimum&&Finite(scale)&&scale>0;
    }

    private static A6Correlation A6CorrelationMeasure(double[] signal,
        int aStart,int aEnd,int aPeak,double aBaseline,int bStart,int bEnd,int bPeak,double bBaseline)
    {
        var result=new A6Correlation {PeakA=aPeak,PeakB=bPeak};
        double aScale,bScale;
        if(!A6CorrelationSupport(signal,aStart,aEnd,aPeak,aBaseline,out aScale)||
            !A6CorrelationSupport(signal,bStart,bEnd,bPeak,bBaseline,out bScale))return result;
        if(aEnd>=bStart)return result; // Incident and reflection must be distinct ordered supports.
        int signA=Math.Sign(signal[aPeak]-aBaseline),signB=Math.Sign(signal[bPeak]-bBaseline);
        result.PolarityRatio=signA==0||signB==0?0:signB/(double)signA;
        // Retain BOTH complete supports for every shift. With j=bPeak+k+shift,
        // +/-2 guards also preserve the complete reflected support at either endpoint.
        int begin=Math.Min(aStart-aPeak,bStart-bPeak)-2;
        int end=Math.Max(aEnd-aPeak,bEnd-bPeak)+2;
        double lower=double.PositiveInfinity,upper=double.NegativeInfinity,fixedValue=double.NaN;
        int bestShift=0;
        for(int shift=-2;shift<=2;shift++)
        {
            double meanA=0,meanB=0,aa=0,bb=0,ab=0;int count=0;
            // Welford centered covariance after independent positive scale factors.
            // Scaling prevents under/overflow; no waveform, sign or lobe is replaced.
            for(int k=begin;k<=end;k++)
            {
                int i=aPeak+k,j=bPeak+k+shift;
                double a=i>=aStart&&i<=aEnd?(signal[i]-aBaseline)/aScale:0;
                double b=j>=bStart&&j<=bEnd?(signal[j]-bBaseline)/bScale:0;
                count++;double da=a-meanA,db=b-meanB;
                meanA+=da/count;meanB+=db/count;
                aa+=da*(a-meanA);bb+=db*(b-meanB);ab+=da*(b-meanB);
            }
            if(count<3||!Finite(aa)||!Finite(bb)||!Finite(ab)||aa<=0||bb<=0)return result;
            double rho=(ab/Math.Sqrt(aa))/Math.Sqrt(bb);
            if(!Finite(rho)||rho< -1-1e-12||rho>1+1e-12)return result;
            rho=Math.Max(-1,Math.Min(1,rho)); // Floating point roundoff only.
            if(shift==0)fixedValue=rho;
            lower=Math.Min(lower,rho);
            if(rho>upper||rho==upper&&(Math.Abs(shift)<Math.Abs(bestShift)||
                Math.Abs(shift)==Math.Abs(bestShift)&&shift<bestShift))
            {upper=rho;bestShift=shift;}
        }
        result.Fixed=fixedValue;result.Lower=lower;result.Upper=upper;result.Shift=bestShift;result.Valid=true;
        return result;
    }

    private static int A6AbsoluteCorrelationPeak(double[] signal,int start,int end,double baseline)
    {
        if(signal==null||start<0||end>=signal.Length||end<start||!Finite(baseline))return -1;
        int peak=-1;double maximum=-1;
        for(int i=start;i<=end;i++)
        {
            double amplitude=Math.Abs(signal[i]-baseline);
            if(!Finite(signal[i])||!Finite(amplitude))return -1;
            // Deterministic earliest absolute maximum. Never search only the incident's sign.
            if(amplitude>maximum){maximum=amplitude;peak=i;}
        }
        return maximum>0?peak:-1;
    }

    private static A6Correlation A6CalibrationCorrelation(A4Context c,A4AttenuationScoringReference reference)
    {
        if(c==null||c.Calibration==null||reference==null||reference.Incident==null||reference.Reflection==null)
            return new A6Correlation();
        ParsedCalibration cal=c.Calibration;
        if(cal.Samples==null||cal.Samples.Length<3||!Finite(cal.SampleRate)||cal.SampleRate<6000000)
            return new A6Correlation();
        foreach(double value in cal.Samples)if(!Finite(value))return new A6Correlation();
        A4AttenuationMeasurement a=reference.Incident,b=reference.Reflection;
        // CAL and LOC use exactly the same temporal-lobe policy on independently
        // guarded processed snippets. No second filtering or absolute-peak alignment.
        return A6TemporalPacketCorrelation(cal.Samples,cal.SampleRate,a,b,-1,-1);
    }
}
