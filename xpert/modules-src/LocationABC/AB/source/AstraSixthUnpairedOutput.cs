using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Method 6 output bookkeeping only. An unpaired pulse is never an A4Pair and
    // never acquires a fabricated reflection, delay, position, or compensation.
    private sealed class A6OutputPulse
    {
        internal double[] Row;
        internal A4Pulse Pulse;
        internal A4Pair Pair;
        internal string State,Reason;
        internal int PairedSubsetRow=-1;
    }
    private readonly Dictionary<double[],A6OutputPulse> a6OutputPulseBindings=new Dictionary<double[],A6OutputPulse>();
    private A6OutputPulse[] a6CurrentOutputPulses=new A6OutputPulse[0];
    private double[][] a6PairedOutputRows=new double[0][];

    private void A6ResetOutputPulseBindings()
    {
        a6OutputPulseBindings.Clear();a6CurrentOutputPulses=new A6OutputPulse[0];
        a6PairedOutputRows=new double[0][];
    }
    private void A6CapturePairedOutputRows(List<double[]> rows)
    {
        // The original equivalent-waveform implementation still receives its
        // original paired-only matrix and canonical binding, in original order.
        A4CaptureCanonicalPhysicalRows(rows);
        a6PairedOutputRows=rows.ToArray();
        for(int i=0;i<rows.Count;i++)
        {
            A4Pair pair=A4RegisteredCanonicalIdentity(rows[i]).Pair;
            if(pair==null)throw new InvalidOperationException("Sixth paired output lost its real pair.");
            a6OutputPulseBindings.Add(rows[i],new A6OutputPulse {Row=rows[i],Pulse=pair.Incident,Pair=pair,
                State=pair.State=="accepted"?"accepted_pair":"review_pair",Reason=pair.ReviewReason,PairedSubsetRow=i});
        }
    }
    private void A6AppendUnpairedOutputs(A4Context c,IEnumerable<A4Pulse> packets,IList<A4Pair> selected,
        IDictionary<int,string> retentionReasons,int phase,long recordId,double voltage,double frequency,
        double initialPhase,bool firstArrival,bool automaticAnalysis,int requestedSuppression,double tanDelta,
        int pre,int post,double[] displaySignal,List<double[]> parameters,List<double[]> raw,List<double[]> filtered)
    {
        var incidentIds=new HashSet<int>(selected.Select(p=>p.Incident.Id));
        // A review pair still owns its displayed reflection for this output.
        // Keeping that very packet as a second independent unpaired MAP makes
        // the user see an artificial NaN reflection beside the scored pair.
        var pairedReflectionIds=new HashSet<int>(selected.Select(p=>p.Reflection.Id));
        var pairedReflectionPeaks=new HashSet<int>(selected.Select(p=>A4ChargeIndex(p.Reflection)));
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        foreach(A4Pulse pulse in packets.OrderByDescending(p=>A4RawCharge(c,A4ChargeIndex(p))).ThenBy(A4ChargeIndex))
        {
            if(parameters.Count>=c.AnalysisLimit)break;
            int peak=A4ChargeIndex(pulse);
            if(!A4AnalysisPhaseEligible(c,peak))continue;
            if(peak<0||peak>=c.Raw.Length||A4RawCharge(c,peak)<c.IncidentThreshold||
                incidentIds.Contains(pulse.Id)||pairedReflectionIds.Contains(pulse.Id)||pairedReflectionPeaks.Contains(peak))continue;
            // Inclusive threshold matches Project methods; retain no zero-amplitude phantom.
            // The sixth branch and its existing retention semantics are unchanged.
            if(SeventhMode&&A4RawCharge(c,peak)<=0)continue;
            int t0=peak;
            if(firstArrival)
            {int onset=A4RawLeadingEdge(c,pulse,peak);if(onset>=0&&onset<=peak)t0=onset;}
            int start,end;
            if(!A4CompletePdmapCrop(c,SeventhMode?t0:peak,t0,pre,post,out start,out end))continue;
            double[] row=NewNaNRow();
            row[0]=phase;row[1]=recordId;row[2]=voltage;
            row[3]=Finite(frequency)&&frequency>0?NormalizeDegrees(initialPhase+360*frequency*(SeventhMode?peak:t0)/c.Fs):NormalizeDegrees(initialPhase);
            row[4]=c.IncidentThreshold;row[5]=c.Length;row[6]=c.Velocity;row[7]=c.Fs;row[8]=recordId;
            row[9]=parameters.Count;row[10]=t0;
            // 11 reflection, 12 delay, 13 location, 16/17 compensation, and
            // 21/22 pair evidence remain NaN: no localization was measured.
            row[14]=c.RoundTrip;row[15]=A4RawCharge(c,peak);row[18]=pulse.Energy/c.Fs;
            row[19]=tanDelta;row[20]=t0-start;row[25]=tanDelta;row[26]=SeventhMode?6:5;row[27]=requestedSuppression;
            row[28]=c.Sigma;row[29]=A4RawBaselineAt(c,peak)+c.IncidentThreshold;
            row[30]=A4RawBaselineAt(c,peak)-c.IncidentThreshold;row[31]=start;row[32]=end;
            row[33]=double.NaN;row[34]=automaticAnalysis?1:0;row[35]=0;
            string reason,retentionReason;
            bool retentionOnly=retentionReasons!=null&&retentionReasons.TryGetValue(pulse.Id,out retentionReason);
            if(retentionOnly)reason=retentionReasons[pulse.Id];
            else if(!visited.Contains(pulse.Id))reason=c.CandidateEvaluationLimitExceeded?
                "not_analyzed_candidate_budget_or_incident_cap":"not_analyzed_incident_analysis_cap";
            else if(!c.AnalysisExclusions.TryGetValue(pulse.Id,out reason))reason="no_final_accepted_or_measurable_review_pair";
            string state=retentionOnly?"unpaired_retention_only_not_analyzed":
                visited.Contains(pulse.Id)?"unpaired_analyzed":"unpaired_not_analyzed";
            a6OutputPulseBindings.Add(row,new A6OutputPulse {Row=row,Pulse=pulse,State=state,Reason=reason});
            if(SeventhMode)ReviewedMatrixBudget(parameters.Count+1,Math.Max(end-start+1,raw.Select(v=>v.Length).DefaultIfEmpty(0).Max()));
            parameters.Add(row);raw.Add(A4Slice(c.Raw,start,end));filtered.Add(A4Slice(displaySignal,start,end));
        }
    }
    private void A6BindCurrentOutputRows(List<double[]> rows)
    {
        a6CurrentOutputPulses=rows.Select(row=>{
            A6OutputPulse item;
            if(!a6OutputPulseBindings.TryGetValue(row,out item))throw new InvalidOperationException("Sixth output lost its actual incident pulse binding.");
            return item;
        }).ToArray();
    }
    private string A6UnpairedOutputJson()
    {
        return "{\"policy\":\"retain_discovered_threshold_packets_without_fabricated_pairs\",\"retained_output_obeys_analysis_pulse_cap\":true,"+
            "\"unpaired_confidence\":\"NaN_not_scored_not_zero_discharge_probability\",\"row_type_rule\":\"NaN_reflection_column11_identifies_unpaired_rows; consult_state_for_not_analyzed_vs_analyzed\","+
            "\"unpaired_count\":"+a6CurrentOutputPulses.Count(p=>p.Pair==null)+",\"paired_count\":"+a6PairedOutputRows.Length+
            ",\"unpaired_nan_columns_zero_based\":[11,12,13,16,17,21,22],\"rows\":["+
            String.Join(",",a6CurrentOutputPulses.Select((item,index)=>"{\"row\":"+index+",\"packet_id\":"+item.Pulse.Id+
                ",\"raw_incident_peak\":"+A4ChargeIndex(item.Pulse)+",\"state\":"+Json(item.State)+",\"reason\":"+Json(item.Reason??"")+
                ",\"pair_scored\":"+(item.Pair==null?"false":"true")+",\"paired_subset_row\":"+item.PairedSubsetRow+"}"))+"]}";
    }

    internal double[,] A6CompleteEquivalentOutput(double[,] parameters,bool requested,double[] windowNs)
    {
        if(parameters.GetLength(0)!=a6CurrentOutputPulses.Length)
            throw new InvalidOperationException("Sixth equivalent output row count does not match current incident bindings.");
        string originalStatus=StatusJson;
        // Preserve every arithmetic operation of the pre-retention paired path.
        double[,] pairedParameters=A4Matrix(a6PairedOutputRows.ToList(),36);
        double[,] pairedWaveforms=CompleteEquivalentOutput(new double[0,36],new double[0,0],pairedParameters,requested,windowNs);
        const string marker=",\"equivalent_waveform\":";
        int markerIndex=StatusJson.LastIndexOf(marker,StringComparison.Ordinal);
        if(markerIndex<0)throw new InvalidOperationException("Paired equivalent diagnostic section is missing.");
        string pairedDiagnostic=StatusJson.Substring(markerIndex+marker.Length);
        pairedDiagnostic=pairedDiagnostic.Substring(0,pairedDiagnostic.Length-1);
        bool custom;string normalization;
        double[] times=A4NormalizeEquivalent(requested,windowNs,equivalentContext.Fs,out custom,out normalization);
        var waves=new List<double[]>();var details=new List<string>();
        for(int r=0;r<a6CurrentOutputPulses.Length;r++)
        {
            A6OutputPulse item=a6CurrentOutputPulses[r];
            if(item.Row[0]!=parameters[r,0]||item.Row[1]!=parameters[r,1]||item.Row[10]!=parameters[r,10]||
                item.Row[15]!=parameters[r,15])throw new InvalidOperationException("Sixth equivalent output no longer matches its incident identity.");
            if(item.Pair!=null)
            {
                int p=item.PairedSubsetRow;
                parameters[r,23]=pairedParameters[p,23];parameters[r,24]=pairedParameters[p,24];
                waves.Add(A4Row(pairedWaveforms,p));
                details.Add("{\"row\":"+r+",\"paired\":true,\"original_paired_subset_row\":"+p+"}");
                continue;
            }
            string detail;
            waves.Add(A6UnpairedEquivalentWaveform(item.Pulse,parameters,r,custom,times,out detail));
            details.Add("{\"row\":"+r+",\"paired\":false,"+detail+"}");
        }
        StatusJson=originalStatus.Substring(0,originalStatus.LastIndexOf('}'))+",\"equivalent_waveform\":{\"scope\":\"all_current_rows_including_unpaired\","+
            "\"paired_submatrix_uses_unchanged_original_implementation\":true,\"paired_subset_calculation\":"+pairedDiagnostic+
            ",\"custom_requested\":"+(requested?"true":"false")+",\"custom_enabled\":"+(custom?"true":"false")+
            ",\"normalization\":"+Json(normalization)+",\"effective_window_ns\":"+NumberArrayJson(times)+
            ",\"units\":\"ns\",\"padding\":\"NaN_after_valid_samples_or_all_NaN_if_unavailable\",\"rows\":["+String.Join(",",details)+"]}}";
        ParameterMatrix=Flatten(parameters);LastResultJson=StatusJson;
        return A4Matrix(waves,0);
    }

    private double[] A6UnpairedEquivalentWaveform(A4Pulse pulse,double[,] parameters,int row,bool custom,double[] times,out string detail)
    {
        int start=pulse.Start,end=pulse.End;bool clipped=false;
        string unavailable=pulse.Clipped?"original_packet_clipped":pulse.UnresolvedOverlap?"original_packet_overlap_or_unresolved":null;
        if(custom)
        {
            long before=checked((long)Math.Ceiling(times[0]*equivalentContext.Fs/1e9));
            long count=checked((long)Math.Ceiling(times[1]*equivalentContext.Fs/1e9));count=Math.Max(2,Math.Max(count,before+1));
            long wantedStart=(long)A4ChargeIndex(pulse)-before,wantedEnd=checked(wantedStart+count-1);
            start=(int)Math.Max(0,wantedStart);end=(int)Math.Min(equivalentContext.Raw.Length-1L,wantedEnd);
            clipped=wantedStart!=start||wantedEnd!=end;
        }
        if(start<0||end>=equivalentContext.Raw.Length||end-start+1<2)unavailable="insufficient_original_pulse_support";
        double[] waveform=null;double baseline=0;string signal="fixed_shared_analysis_band_unverified_local_guard";
        if(unavailable==null)
        {
            if(pulse.AnalysisSamples!=null&&start>=pulse.AnalysisOrigin&&end<pulse.AnalysisOrigin+pulse.AnalysisSamples.Length)
            {waveform=A4Slice(pulse.AnalysisSamples,start-pulse.AnalysisOrigin,end-pulse.AnalysisOrigin);baseline=pulse.Baseline;signal="existing_analysis_window_samples_minus_local_baseline";}
            else if(equivalentContext.MeasurementSignal!=null&&equivalentContext.MeasurementSignal.BandSignal!=null)
                waveform=A4Slice(equivalentContext.MeasurementSignal.BandSignal,start,end);
            else unavailable="fixed_shared_analysis_band_unavailable";
        }
        double energy=0,derivative=0,peak=0;
        if(waveform!=null)
        {
            for(int j=0;j<waveform.Length;j++)
            {waveform[j]-=baseline;energy+=waveform[j]*waveform[j];peak=Math.Max(peak,Math.Abs(waveform[j]));if(j>0){double d=waveform[j]-waveform[j-1];derivative+=d*d;}}
            if(waveform.Any(v=>!Finite(v))||!Finite(energy)||!Finite(derivative)||!(energy>0)||!(peak>0))unavailable="nonfinite_or_zero_energy_pulse";
        }
        bool available=unavailable==null;
        parameters[row,23]=available?energy/(peak*peak)/equivalentContext.Fs*1e9:double.NaN;
        parameters[row,24]=available?equivalentContext.Fs/(2*Math.PI)*Math.Sqrt(derivative/energy):double.NaN;
        detail="\"raw_peak_anchor\":"+A4ChargeIndex(pulse)+",\"start\":"+start+",\"end\":"+end+
            ",\"available\":"+(available?"true":"false")+",\"reason\":"+Json(unavailable??"single_incident_features_without_localization")+
            ",\"signal\":"+Json(signal)+",\"baseline_removed\":"+NumberJson(baseline)+",\"custom_window_clipped\":"+(clipped?"true":"false")+
            ",\"pair_quality_validated\":false,\"samples\":"+(available?waveform.Length:0);
        return available?waveform:new[]{double.NaN};
    }
}
