using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Anchors are determined independently, before looking at the other packet or
    // calculating any correlation. First/second refer to TIME ORDER of the two
    // significant original main lobes, not which absolute peak happens to be taller.
    private sealed class A6TemporalAnchor
    {
        internal bool Valid,HasSecond,AbsoluteDominantLobeChanged;
        internal int RawFirstPeak=-1,RawSecondPeak=-1,Peak=-1,SecondPeak=-1,Sign;
        internal int CollapsedRawFragments;
        internal string Reason="not_measured",Mode="unavailable";
    }

    private static double A6TemporalRawValue(double[] raw,A4AttenuationMeasurement m,int index)
    {
        return raw[index]-A4RawMeasurementBaseline(m,index);
    }

    private static bool A6TemporalLowLobe(A4AttenuationMeasurement m,int rawReference,int sign,out int peak)
    {
        peak=-1;
        if(m.SignalSamples==null||rawReference<m.Start||rawReference>m.End||
            m.Start<m.SignalOrigin||(long)m.End-m.SignalOrigin>=m.SignalSamples.Length||sign==0)return false;
        int local=rawReference-m.SignalOrigin;
        double atReference=m.SignalSamples[local]-m.Baseline;
        // Do not jump to a later same-sign lobe if filtering has erased/reversed
        // this original lobe at its own peak. That correspondence is unresolved.
        if(!Finite(atReference)||Math.Sign(atReference)!=sign)return false;
        int first=rawReference,last=rawReference;
        while(first>m.Start)
        {
            double value=m.SignalSamples[first-1-m.SignalOrigin]-m.Baseline;
            if(!Finite(value))return false;if(Math.Sign(value)!=sign)break;first--;
        }
        while(last<m.End)
        {
            double value=m.SignalSamples[last+1-m.SignalOrigin]-m.Baseline;
            if(!Finite(value))return false;if(Math.Sign(value)!=sign)break;last++;
        }
        double amplitude=0;
        for(int i=first;i<=last;i++)
        {
            double value=sign*(m.SignalSamples[i-m.SignalOrigin]-m.Baseline);
            if(!Finite(value))return false;
            if(value>amplitude){amplitude=value;peak=i;}
        }
        // The existing three-sigma review measurement floor is a structural
        // guard here. It is not a new correlation threshold or an amplitude fit.
        return last>first&&peak>m.Start&&peak<m.End&&Finite(m.Noise)&&m.Noise>=0&&amplitude>3*m.Noise;
    }

    private sealed class A6TemporalSignRun
    {
        internal int Start,End,Peak,Sign;
        internal double Amplitude,SumSquares;
    }
    private static List<A6TemporalSignRun> A6TemporalSignRuns(double[] values,int origin,int start,int end,
        double baseline,double noise,bool raw)
    {
        if(values==null||start<origin||end<start||(long)end-origin>=values.Length||
            !Finite(baseline)||!Finite(noise)||noise<0)return null;
        var all=new List<A6TemporalSignRun>();A6TemporalSignRun current=null;
        for(int i=start;i<=end;i++)
        {
            double value=values[i-origin]-baseline;if(!Finite(value))return null;
            int sign=Math.Sign(value);if(sign==0){current=null;continue;}
            if(current==null||current.Sign!=sign)
            {current=new A6TemporalSignRun{Start=i,End=i,Peak=i,Sign=sign};all.Add(current);}
            current.End=i;current.SumSquares+=value*value;
            if(Math.Abs(value)>current.Amplitude){current.Amplitude=Math.Abs(value);current.Peak=i;}
            if(!Finite(current.SumSquares))return null;
        }
        var significant=new List<A6TemporalSignRun>();
        foreach(var run in all)
        {
            int count=run.End-run.Start+1;
            // Same 4.5-sigma original-lobe significance as A6EvidenceMath.Lobes.
            // Processed connected lobes use the existing 3-sigma review floor.
            if(raw?run.Amplitude>4.5*noise&&run.SumSquares>noise*noise*(count+4.5*Math.Sqrt(2d*count)):
                count>1&&run.Peak>start&&run.Peak<end&&run.Amplitude>3*noise)significant.Add(run);
        }
        return significant;
    }

    // Narrow recovery for raw high-frequency fragments whose two largest peaks
    // have the same sign. Energy's largest/second-largest ranking does NOT define
    // temporal waveform structure. The next *significant processed sign run* is
    // fixed before examining any candidate/rho; it may not be skipped in search
    // of a later better-looking lobe. Full supports still enter signed Pearson.
    private static A6TemporalAnchor A6FoldTemporalRawFragments(double[] corrected,A4AttenuationMeasurement m,int canonical)
    {
        var anchor=new A6TemporalAnchor{Reason="temporal_raw_fragments_unresolved",Mode="fixed_band_connected_lobes_with_original_fragment_correspondence"};
        var original=A6TemporalSignRuns(corrected,m.RawStart,m.RawStart,m.RawEnd,0,m.RawNoise,true);
        var processed=A6TemporalSignRuns(m.SignalSamples,m.SignalOrigin,m.Start,m.End,m.Baseline,m.Noise,false);
        if(original==null||processed==null||original.Count<2||processed.Count<2)return anchor;
        var first=original[0];int firstPeak;
        if(!A6TemporalLowLobe(m,first.Peak,first.Sign,out firstPeak))
        {anchor.Reason="temporal_first_raw_fragment_has_no_unique_processed_correspondence";return anchor;}
        int firstIndex=processed.FindIndex(p=>p.Peak==firstPeak);
        if(firstIndex<0||firstIndex+1>=processed.Count)
        {anchor.Reason="temporal_next_significant_processed_lobe_unavailable";return anchor;}
        var next=processed[firstIndex+1];
        if(next.Sign==first.Sign)
        {anchor.Reason="temporal_next_significant_processed_lobe_same_polarity_unresolved";return anchor;}
        A6TemporalSignRun second=null;
        foreach(var run in original)
        {
            int mapped;
            if(!A6TemporalLowLobe(m,run.Peak,run.Sign,out mapped))continue;
            if(mapped==firstPeak)anchor.CollapsedRawFragments++;
            if(second==null&&run.Start>first.End&&run.Sign==next.Sign&&mapped==next.Peak)second=run;
        }
        if(second==null)
        {anchor.Reason="temporal_next_processed_lobe_has_no_original_correspondence";return anchor;}
        // A complete original quiet guard separates distinct packets. Do not
        // reinterpret such neighbours as high-frequency fragments of one lobe.
        int quiet=0;
        for(int i=first.End+1;i<second.Start;i++)
        {
            quiet=Math.Abs(corrected[i-m.RawStart])<=3*m.RawNoise?quiet+1:0;
            if(quiet>=16){anchor.Reason="temporal_original_quiet_guard_separates_fragments";return anchor;}
        }
        int canonicalMapped;
        if(!A6TemporalLowLobe(m,canonical,Math.Sign(corrected[canonical-m.RawStart]),out canonicalMapped)||
            canonicalMapped!=firstPeak&&canonicalMapped!=next.Peak)
        {anchor.Reason="temporal_canonical_fragment_outside_first_corresponding_lobes";return anchor;}
        anchor.RawFirstPeak=first.Peak;anchor.RawSecondPeak=second.Peak;
        anchor.Peak=firstPeak;anchor.SecondPeak=next.Peak;anchor.Sign=first.Sign;anchor.HasSecond=true;
        anchor.AbsoluteDominantLobeChanged=m.Peak>=m.Start&&m.Peak<=m.End&&
            Math.Sign(m.SignalSamples[m.Peak-m.SignalOrigin]-m.Baseline)!=anchor.Sign;
        anchor.Valid=true;anchor.Reason="original_high_frequency_fragments_folded_by_fixed_band_connected_lobes";
        return anchor;
    }

    private static A6TemporalAnchor A6BuildTemporalAnchor(double[] raw,double fs,
        A4AttenuationMeasurement m,int canonical)
    {
        var anchor=new A6TemporalAnchor();
        if(raw==null||m==null||!m.Valid||m.SignalSamples==null||m.RawStart<0||m.RawEnd>=raw.Length||
            m.RawEnd<m.RawStart||!Finite(fs)||fs<=0||!Finite(m.RawBaseline)||!Finite(m.RawSlope)||
            !Finite(m.RawNoise)||m.RawNoise<0)
        {anchor.Reason="temporal_original_or_processed_measurement_unavailable";return anchor;}
        double[] corrected=new double[m.RawEnd-m.RawStart+1];
        int maximum=0;
        for(int i=0;i<corrected.Length;i++)
        {
            corrected[i]=A6TemporalRawValue(raw,m,m.RawStart+i);
            if(!Finite(corrected[i])){anchor.Reason="temporal_nonfinite_detrended_original";return anchor;}
            if(Math.Abs(corrected[i])>Math.Abs(corrected[maximum]))maximum=i;
        }
        // CAL has marked packet supports, not a LOC canonical charge identity.
        // Pick its deterministic detrended original maximum only to identify the
        // raw main lobe; temporal ordering below remains independent of magnitude.
        if(canonical<0)canonical=m.RawStart+maximum;
        if(canonical<m.RawStart||canonical>m.RawEnd)
        {anchor.Reason="temporal_canonical_peak_outside_original_support";return anchor;}
        A6LobeResult lobes=A6EvidenceMath.Lobes(corrected,0,corrected.Length-1,
            canonical-m.RawStart,0,m.RawNoise,fs);
        int first,second=-1;
        if(lobes.Valid)
        {
            first=lobes.FirstPeak;second=lobes.SecondPeak;
            if(Math.Sign(corrected[first])==Math.Sign(corrected[second]))
                return A6FoldTemporalRawFragments(corrected,m,canonical);
            anchor.HasSecond=true;anchor.Mode="two_significant_original_main_lobes_in_time_order";
        }
        else if(lobes.Reason=="no_significant_second_lobe"&&lobes.MainPeak>=0)
        {first=lobes.MainPeak;anchor.Mode="single_significant_original_lobe";}
        else {anchor.Reason="temporal_raw_structure_unavailable:"+lobes.Reason;return anchor;}
        anchor.RawFirstPeak=m.RawStart+first;anchor.Sign=Math.Sign(corrected[first]);
        if(!A6TemporalLowLobe(m,anchor.RawFirstPeak,anchor.Sign,out anchor.Peak))
        {anchor.Reason="temporal_first_raw_lobe_has_no_unique_significant_processed_correspondence";return anchor;}
        if(second>=0)
        {
            anchor.RawSecondPeak=m.RawStart+second;
            if(!A6TemporalLowLobe(m,anchor.RawSecondPeak,Math.Sign(corrected[second]),out anchor.SecondPeak)||
                anchor.SecondPeak<=anchor.Peak)
            {anchor.Reason="temporal_second_raw_lobe_order_or_processed_correspondence_unresolved";return anchor;}
        }
        anchor.AbsoluteDominantLobeChanged=m.Peak>=m.Start&&m.Peak<=m.End&&
            Math.Sign(m.SignalSamples[m.Peak-m.SignalOrigin]-m.Baseline)!=anchor.Sign;
        anchor.Valid=true;anchor.Reason="independent_original_temporal_lobe_correspondence";
        return anchor;
    }

    // Same numerical definition as A6CorrelationMeasure, now with separate
    // processed snippets and global coordinates. BOTH full supports survive
    // every +/-2 sample sensitivity check. No sign flip, abs(rho), crop or lag fit.
    private static A6Correlation A6CorrelationMeasurePackets(A4AttenuationMeasurement a,A4AttenuationMeasurement b,int pa,int pb)
    {
        var result=new A6Correlation {PeakA=pa,PeakB=pb};double scaleA,scaleB;
        if(a==null||b==null||a.End>=b.Start||
            !A6CorrelationSupport(a.SignalSamples,a.Start-a.SignalOrigin,a.End-a.SignalOrigin,pa-a.SignalOrigin,a.Baseline,out scaleA)||
            !A6CorrelationSupport(b.SignalSamples,b.Start-b.SignalOrigin,b.End-b.SignalOrigin,pb-b.SignalOrigin,b.Baseline,out scaleB))return result;
        int signA=Math.Sign(a.SignalSamples[pa-a.SignalOrigin]-a.Baseline),signB=Math.Sign(b.SignalSamples[pb-b.SignalOrigin]-b.Baseline);
        result.PolarityRatio=signA==0||signB==0?0:signB/(double)signA;
        int begin=Math.Min(a.Start-pa,b.Start-pb)-2,end=Math.Max(a.End-pa,b.End-pb)+2;
        double lower=double.PositiveInfinity,upper=double.NegativeInfinity,fixedValue=double.NaN;int bestShift=0;
        for(int shift=-2;shift<=2;shift++)
        {
            double meanA=0,meanB=0,aa=0,bb=0,ab=0;int count=0;
            for(int k=begin;k<=end;k++)
            {
                int i=pa+k,j=pb+k+shift;
                double va=i>=a.Start&&i<=a.End?(a.SignalSamples[i-a.SignalOrigin]-a.Baseline)/scaleA:0;
                double vb=j>=b.Start&&j<=b.End?(b.SignalSamples[j-b.SignalOrigin]-b.Baseline)/scaleB:0;
                count++;double da=va-meanA,db=vb-meanB;meanA+=da/count;meanB+=db/count;
                aa+=da*(va-meanA);bb+=db*(vb-meanB);ab+=da*(vb-meanB);
            }
            if(count<3||!Finite(aa)||!Finite(bb)||!Finite(ab)||aa<=0||bb<=0)return result;
            double rho=(ab/Math.Sqrt(aa))/Math.Sqrt(bb);
            if(!Finite(rho)||rho< -1-1e-12||rho>1+1e-12)return result;
            rho=Clamp(rho,-1,1);if(shift==0)fixedValue=rho;lower=Math.Min(lower,rho);
            if(rho>upper||rho==upper&&(Math.Abs(shift)<Math.Abs(bestShift)||Math.Abs(shift)==Math.Abs(bestShift)&&shift<bestShift))
            {upper=rho;bestShift=shift;}
        }
        result.Fixed=fixedValue;result.Lower=lower;result.Upper=upper;result.Shift=bestShift;result.Valid=true;return result;
    }

    private static A6Correlation A6TemporalPacketCorrelation(double[] raw,double fs,
        A4AttenuationMeasurement a,A4AttenuationMeasurement b,int canonicalA,int canonicalB)
    {
        A6TemporalAnchor aa=A6BuildTemporalAnchor(raw,fs,a,canonicalA),bb=A6BuildTemporalAnchor(raw,fs,b,canonicalB);
        A6Correlation result=aa.Valid&&bb.Valid?A6CorrelationMeasurePackets(a,b,aa.Peak,bb.Peak):new A6Correlation();
        result.AnchorA=aa;result.AnchorB=bb;result.AlignmentRule="original_temporal_lobes_unique_processed_correspondence_no_rho_selection";
        result.StructureCompatible=aa.Valid&&bb.Valid&&aa.HasSecond==bb.HasSecond&&aa.Sign==bb.Sign;
        result.OppositeTemporalPolarity=aa.Valid&&bb.Valid&&aa.Sign!=bb.Sign;
        result.StructureReason=!aa.Valid?"incident_"+aa.Reason:!bb.Valid?"reflection_"+bb.Reason:
            aa.HasSecond!=bb.HasSecond?"temporal_raw_structure_count_mismatch":result.OppositeTemporalPolarity?
            "opposite_original_temporal_lobe_polarity_not_sign_flipped":"corresponding_original_temporal_lobes";
        // Preserve measured signed values in diagnostics for genuine inversion,
        // but do not let any threshold override unresolved/inverted structure.
        if(!result.StructureCompatible)result.Valid=false;
        return result;
    }

    private static string A6TemporalAnchorJson(A6TemporalAnchor a)
    {
        if(a==null)return "null";
        return "{\"valid\":"+(a.Valid?"true":"false")+",\"reason\":"+Json(a.Reason)+",\"mode\":"+Json(a.Mode)+
            ",\"raw_first_lobe_peak\":"+a.RawFirstPeak+",\"raw_second_lobe_peak\":"+a.RawSecondPeak+
            ",\"processed_first_lobe_peak\":"+a.Peak+",\"processed_second_lobe_peak\":"+a.SecondPeak+
            ",\"original_first_lobe_sign\":"+a.Sign+",\"collapsed_raw_fragments_in_first_processed_lobe\":"+a.CollapsedRawFragments+
            ",\"absolute_dominant_lobe_differs_from_first\":"+(a.AbsoluteDominantLobeChanged?"true":"false")+"}";
    }

    private static string A6TemporalCorrelationJson(A6Correlation c)
    {
        return "{\"rule\":"+Json(c.AlignmentRule)+",\"structure_compatible\":"+(c.StructureCompatible?"true":"false")+
            ",\"reason\":"+Json(c.StructureReason)+",\"opposite_temporal_polarity\":"+(c.OppositeTemporalPolarity?"true":"false")+
            ",\"absolute_peak_alignment_mixes_temporal_lobes\":"+(c.AnchorA!=null&&c.AnchorB!=null&&c.AnchorA.Valid&&c.AnchorB.Valid&&
                c.AnchorA.AbsoluteDominantLobeChanged!=c.AnchorB.AbsoluteDominantLobeChanged?"true":"false")+
            ",\"anchor_selected_by_correlation\":false,\"timing_modified\":false,\"complete_supports_preserved\":true,\"incident\":"+
            A6TemporalAnchorJson(c.AnchorA)+",\"reflection\":"+A6TemporalAnchorJson(c.AnchorB)+"}";
    }
}
