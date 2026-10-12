using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private sealed class A6ReviewTemporalGroup
    {
        internal A6TemporalSignRun First,Last;
        internal int BandIndex,Fragments;
    }

    private static List<A6TemporalSignRun> A6ReviewSignificantRawRuns(double[] corrected,int origin,double noise)
    {
        // Review structure only: the existing three-sigma structural peak floor
        // AND a three-sigma integrated-energy excess, using actual raw noise.
        // Do not change the normal 4.5-sigma lobe/energy-feature measurement.
        var runs=A6TemporalSignRuns(corrected,origin,origin,origin+corrected.Length-1,0,noise,false);
        if(runs==null)return null;
        return runs.FindAll(r=>r.SumSquares>noise*noise*((r.End-r.Start+1)+3*Math.Sqrt(2d*(r.End-r.Start+1))));
    }

    // A secondary absolute-amplitude ranking is an energy-feature input, not a
    // universal definition of the first arrival. A + - + packet can otherwise
    // be compared at its - + lobes against another packet's + - lobes.
    // This narrowly repairs that ambiguity only in opt-in, unaccepted review.
    private static A6TemporalAnchor A6ReviewConnectedTemporalAnchor(double[] raw,double fs,
        A4AttenuationMeasurement m,int canonical)
    {
        A6TemporalAnchor original=A6BuildTemporalAnchor(raw,fs,m,canonical);
        if(raw==null||m==null||!m.Valid||m.SignalSamples==null||m.RawStart<0||m.RawEnd>=raw.Length||
            m.RawEnd<m.RawStart||canonical<m.RawStart||canonical>m.RawEnd||
            !Finite(fs)||fs<=0||!Finite(m.RawNoise)||m.RawNoise<0)return original;
        var corrected=new double[m.RawEnd-m.RawStart+1];
        for(int i=0;i<corrected.Length;i++)
        {
            corrected[i]=A6TemporalRawValue(raw,m,m.RawStart+i);
            if(!Finite(corrected[i]))return original;
        }
        List<A6TemporalSignRun> source=A6ReviewSignificantRawRuns(corrected,m.RawStart,m.RawNoise);
        List<A6TemporalSignRun> band=A6TemporalSignRuns(m.SignalSamples,m.SignalOrigin,m.Start,m.End,m.Baseline,m.Noise,false);
        if(source==null||band==null||source.Count<3||band.Count<3)return original;
        var groups=new List<A6ReviewTemporalGroup>();
        foreach(A6TemporalSignRun run in source)
        {
            int peak;
            if(!A6TemporalLowLobe(m,run.Peak,run.Sign,out peak))continue;
            int index=band.FindIndex(p=>p.Peak==peak);
            if(index<0)continue;
            A6ReviewTemporalGroup last=groups.Count==0?null:groups[groups.Count-1];
            if(last!=null&&index==last.BandIndex){last.Last=run;last.Fragments++;continue;}
            groups.Add(new A6ReviewTemporalGroup{First=run,Last=run,BandIndex=index,Fragments=1});
        }
        // Ordinary single/bipolar packets retain the existing numerical path.
        // Three raw fragments inside only two processed lobes are not tripolar.
        if(groups.Count<3)return original;
        var anchor=new A6TemporalAnchor{Mode="review_earliest_significant_original_connected_lobe_chain",
            Reason="review_three_lobe_original_correspondence_unresolved"};
        if(!Object.ReferenceEquals(groups[0].First,source[0]))
        {anchor.Reason="review_significant_original_lobe_missing_unique_ordered_band_correspondence";return anchor;}
        int canonicalPeak;
        if(!A6TemporalLowLobe(m,canonical,Math.Sign(corrected[canonical-m.RawStart]),out canonicalPeak))
        {anchor.Reason="review_canonical_peak_outside_corresponding_connected_lobe_chain";return anchor;}
        int canonicalGroup=groups.FindIndex(g=>band[g.BandIndex].Peak==canonicalPeak);
        if(canonicalGroup<0)
        {anchor.Reason="review_canonical_peak_outside_corresponding_connected_lobe_chain";return anchor;}
        // Only establish the leading structure through the canonical main lobe
        // (at least the first two lobes). Later ringing does not redefine the
        // first anchor. It remains entirely in the signed Pearson support.
        int requiredLast=Math.Max(1,canonicalGroup);
        // Also check quiet separation inside a group of raw fragments mapped
        // onto one broad processed lobe; grouping must not conceal a raw gap.
        int chainQuiet=0;
        for(int i=groups[0].First.End+1;i<groups[requiredLast].First.Start;i++)
        {
            chainQuiet=Math.Abs(corrected[i-m.RawStart])<=3*m.RawNoise?chainQuiet+1:0;
            if(chainQuiet>=16){anchor.Reason="review_original_quiet_guard_separates_lobe_chain";return anchor;}
        }
        for(int g=1;g<=requiredLast;g++)
        {
            A6ReviewTemporalGroup previous=groups[g-1],current=groups[g];
            if(current.BandIndex!=previous.BandIndex+1||current.First.Sign==previous.First.Sign)
            {anchor.Reason="review_next_significant_connected_lobe_not_corresponding_in_time";return anchor;}
            foreach(A6TemporalSignRun run in source)
                if(run.Start>previous.Last.End&&run.End<current.First.Start)
                {anchor.Reason="review_intervening_significant_raw_lobe_not_mapped_cannot_skip";return anchor;}
            // A full original quiet interval denotes a possible new packet,
            // not a tail to be folded into a favorable comparison.
            int quiet=0;
            for(int i=previous.Last.End+1;i<current.First.Start;i++)
            {
                quiet=Math.Abs(corrected[i-m.RawStart])<=3*m.RawNoise?quiet+1:0;
                if(quiet>=16){anchor.Reason="review_original_quiet_guard_separates_lobe_chain";return anchor;}
            }
        }
        A6ReviewTemporalGroup first=groups[0],second=groups[1];
        anchor.RawFirstPeak=first.First.Peak;anchor.RawSecondPeak=second.First.Peak;
        anchor.Peak=band[first.BandIndex].Peak;anchor.SecondPeak=band[second.BandIndex].Peak;
        anchor.Sign=first.First.Sign;anchor.HasSecond=true;anchor.CollapsedRawFragments=first.Fragments;
        anchor.AbsoluteDominantLobeChanged=m.Peak>=m.Start&&m.Peak<=m.End&&
            Math.Sign(m.SignalSamples[m.Peak-m.SignalOrigin]-m.Baseline)!=anchor.Sign;
        anchor.Valid=true;anchor.Reason="review_only_three_sigma_peak_and_integrated_raw_lobe_significance_earliest_connected_not_secondary_amplitude_rank";
        return anchor;
    }

    private static A6Correlation A6ReviewConnectedTemporalCorrelation(double[] raw,double fs,
        A4AttenuationMeasurement a,A4AttenuationMeasurement b,int canonicalA,int canonicalB)
    {
        A6TemporalAnchor aa=A6ReviewConnectedTemporalAnchor(raw,fs,a,canonicalA),
            bb=A6ReviewConnectedTemporalAnchor(raw,fs,b,canonicalB);
        const string reviewMode="review_earliest_significant_original_connected_lobe_chain";
        if(aa.Mode!=reviewMode&&bb.Mode!=reviewMode)
            return A6TemporalPacketCorrelation(raw,fs,a,b,canonicalA,canonicalB);
        // Identical numerical primitive: full supports, signed Fixed Pearson.
        // +/-2 samples remain sensitivity diagnostics, never a selected fit.
        A6Correlation result=aa.Valid&&bb.Valid?A6CorrelationMeasurePackets(a,b,aa.Peak,bb.Peak):new A6Correlation();
        result.AnchorA=aa;result.AnchorB=bb;
        result.AlignmentRule="review_only_earliest_significant_connected_raw_lobe_no_score_or_shift_selection";
        result.StructureCompatible=aa.Valid&&bb.Valid&&aa.HasSecond==bb.HasSecond&&aa.Sign==bb.Sign;
        result.OppositeTemporalPolarity=aa.Valid&&bb.Valid&&aa.Sign!=bb.Sign;
        result.StructureReason=!aa.Valid?"incident_"+aa.Reason:!bb.Valid?"reflection_"+bb.Reason:
            aa.HasSecond!=bb.HasSecond?"temporal_raw_structure_count_mismatch":result.OppositeTemporalPolarity?
            "opposite_original_temporal_lobe_polarity_not_sign_flipped":"review_corresponding_original_temporal_lobe_chain";
        if(!result.StructureCompatible)result.Valid=false;
        return result;
    }
}
