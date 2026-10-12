using System;
using System.Collections.Generic;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Filled only by the current analysis, never consumed by physical matching.
    private readonly List<double[]> reviewCurrentParameterRows=new List<double[]>();
    private readonly List<double[]> reviewCurrentRawRows=new List<double[]>();
    private readonly List<double[]> reviewCurrentFilteredRows=new List<double[]>();
    private int reviewCurrentPhase;
    private long reviewCurrentRecordId;

    private static bool A4ReviewInteger(double value,double maximum)
    {return Finite(value)&&value>=0&&value<=maximum&&value==Math.Floor(value);}
    private static string A4ReviewEndpointKey(double phase,double recordId,double sampleIndex)
    {return phase.ToString("R",Inv)+":"+recordId.ToString("R",Inv)+":"+sampleIndex.ToString("R",Inv);}
    private static void A4ValidateReviewRow(double[] p,double[] raw,double[] filtered,string label,bool currentTimingProjectionPending=false)
    {
        if(p==null||p.Length!=36||raw==null||filtered==null||raw.Length!=filtered.Length)
            throw new ArgumentException(label+": review parameters require 36 columns and corresponding raw/filtered waveform widths.");
        if(!A4ReviewInteger(p[0],3)||!A4ReviewInteger(p[1],9007199254740991d)||!A4ReviewInteger(p[10],int.MaxValue)||!A4ReviewInteger(p[11],int.MaxValue))
            throw new ArgumentException(label+": review phase, record ID and pulse indices must be exact nonnegative integers.");
        // Score and acceptance are separate fields. An unchanged primary
        // candidate vetoed by local ambiguity legitimately retains a high
        // feature score while remaining unaccepted. Do not empty the whole LOC.
        if(!Finite(p[15])||p[15]<0||!Finite(p[33])||p[33]<0||p[33]>1||p[35]!=0)
            throw new ArgumentException(label+": review rows require finite nonnegative amplitude, confidence in [0,1], and column35=0.");
        if(!Finite(p[7])||p[7]<=0||!A4ReviewInteger(p[14],int.MaxValue)||p[14]<=0||
            p[12]!=p[11]-p[10]||p[12]<=0||(!currentTimingProjectionPending&&p[12]>p[14]+Math.Max(4,Math.Floor(p[14]/8))))
            throw new ArgumentException(label+": review delay must be positive and within the bounded round-trip uncertainty envelope.");
        // Review coordinates may be signed endpoint extrapolations. They remain
        // explicitly unaccepted and do not expand the existing delay envelope.
        // Old boundary rows containing NaN are still readable without invention.
        bool validPosition=Finite(p[13]);
        bool unresolvedBoundary=Double.IsNaN(p[13])&&p[12]>p[14];
        if(!Finite(p[5])||p[5]<=0||(!validPosition&&!unresolvedBoundary)||
            !Finite(p[21])||p[21]<0||p[21]>1)
            throw new ArgumentException(label+": review position must be finite (signed endpoint extrapolation is allowed), or legacy NaN for an out-of-range boundary delay; correlation must be in [0,1].");
        if(!A4ReviewInteger(p[31],int.MaxValue)||!A4ReviewInteger(p[32],int.MaxValue)||p[32]<p[31])
            throw new ArgumentException(label+": invalid review waveform bounds.");
        long count=(long)p[32]-(long)p[31]+1;
        if(count>raw.Length)throw new ArgumentException(label+": review waveform is shorter than its matrix bounds.");
        for(int i=0;i<raw.Length;i++)
            if(i<count?(!Finite(raw[i])||!Finite(filtered[i])):(!double.IsNaN(raw[i])||!double.IsNaN(filtered[i])))
                throw new ArgumentException(label+": review waveform requires finite valid samples followed only by NaN padding.");
    }
    internal void CompleteReviewOutput(double[,] previousP,double[,] previousRaw,double[,] previousFiltered,
        out double[,] outputP,out double[,] outputRaw,out double[,] outputFiltered)
    {
        int previousCount=previousP==null?0:previousP.GetLength(0);
        int rawCount=previousRaw==null?0:previousRaw.GetLength(0),filteredCount=previousFiltered==null?0:previousFiltered.GetLength(0);
        if(previousCount!=rawCount||previousCount!=filteredCount||
            (previousP!=null&&previousP.GetLength(1)!=36&&(previousCount!=0||previousP.GetLength(1)!=0)))
            throw new ArgumentException("The three review history matrices must have corresponding rows and 36 parameter columns.");
        if(previousCount>0&&(previousRaw.GetLength(1)!=previousFiltered.GetLength(1)))
            throw new ArgumentException("Review history raw and filtered waveform widths must match.");
        if(reviewCurrentParameterRows.Count!=reviewCurrentRawRows.Count||reviewCurrentParameterRows.Count!=reviewCurrentFilteredRows.Count)
            throw new InvalidOperationException("Current review matrices lost their row correspondence.");
        var seen=new HashSet<string>();var confirmedEndpoints=new HashSet<string>();
        for(int r=0;r<ParameterMatrix2D.GetLength(0);r++)
        {
            var identity=A4BoundCanonicalIdentity(canonicalPhysicalRows,r,ParameterMatrix2D.GetLength(0),A4Row(ParameterMatrix2D,r),false);
            if(!confirmedEndpoints.Add(identity.IncidentKey)||!confirmedEndpoints.Add(identity.ReflectionKey))
                throw new InvalidOperationException("Confirmed outputs share a canonical physical endpoint.");
        }
        var parameters=new List<double[]>();var rawRows=new List<double[]>();var filteredRows=new List<double[]>();
        int replaced=0,removedConfirmed=0,retained=0;
        for(int r=0;r<previousCount;r++)
        {
            double[] p=A4Row(previousP,r),raw=A4Row(previousRaw,r),filtered=A4Row(previousFiltered,r);
            A4ValidateReviewRow(p,raw,filtered,"Review history row "+r);
            // Legacy private-stage history has displayed indices only. Preserve
            // it after shape/value validation; never infer canonical peaks or let
            // it reject a current row. Public history is appended by the facade.
            canonicalOutputIdentities.Add(p,null);
            parameters.Add(p);rawRows.Add(raw);filteredRows.Add(filtered);retained++;
        }
        seen.Clear();
        for(int r=0;r<reviewCurrentParameterRows.Count;r++)
        {
            double[] p=(double[])reviewCurrentParameterRows[r].Clone(),raw=(double[])reviewCurrentRawRows[r].Clone(),filtered=(double[])reviewCurrentFilteredRows[r].Clone();
            // Current canonical candidates passed the piecewise hard window.
            // Requested first-arrival timing is checked at final projection,
            // rather than applying a legacy average-RT envelope to mixed cable.
            A4ValidateReviewRow(p,raw,filtered,"Current review row "+r,true);
            if(p[0]!=reviewCurrentPhase||p[1]!=reviewCurrentRecordId)throw new InvalidOperationException("Current review row has the wrong record identity.");
            var identity=A4RegisteredCanonicalIdentity(reviewCurrentParameterRows[r]);
            canonicalOutputIdentities.Add(p,identity);
            string key=identity.PairKey;
            if(!seen.Add(key))throw new InvalidOperationException("Duplicate current review row identity: "+key);
            if(confirmedEndpoints.Contains(identity.IncidentKey)||confirmedEndpoints.Contains(identity.ReflectionKey))
            {removedConfirmed++;continue;}
            parameters.Add(p);rawRows.Add(raw);filteredRows.Add(filtered);
        }
        A4SortRowsByCharge(parameters,rawRows,filteredRows);
        A4CaptureCanonicalReviewRows(parameters);
        outputP=A4Matrix(parameters,36);outputRaw=A4Matrix(rawRows,0);outputFiltered=A4Matrix(filteredRows,0);
        StatusJson=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+",\"review_history\":{\"independent_of_confirmed_outputs\":true,\"used_for_matching_or_ownership\":false,\"output_includes_history\":true,\"external_concatenation_required\":false,\"replacement_key\":\"none_caller_history_append_only\",\"identity_key\":\"current_phase_record_canonical_incident_reflection\",\"confirmed_endpoint_exclusion\":\"either_canonical_endpoint_matches_confirmed_current_physical_endpoint\",\"history_endpoint_limit\":\"36_columns_do_not_reconstruct_historical_canonical_peaks; validated_history_preserved_without_current_endpoint_exclusion\",\"confidence_range\":\"0<=column33<=1; high-score unaccepted local ambiguity retains original method score\",\"acceptance_column35\":0,\"sort\":\"abs(column15)_descending\",\"sorted_id_column9\":\"zero_based_independent_review_row\",\"input_rows\":"+previousCount+",\"replaced_current_record_rows\":"+replaced+",\"removed_already_confirmed_rows\":"+removedConfirmed+",\"retained_history_rows\":"+retained+",\"added_rows\":"+(parameters.Count-retained)+",\"output_rows\":"+parameters.Count+"}}";
        LastResultJson=StatusJson;
    }
}
