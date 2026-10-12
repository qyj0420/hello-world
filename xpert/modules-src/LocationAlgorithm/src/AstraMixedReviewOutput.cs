using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Per-call audit counters only. No input/output waveform or history is cached;
    // the facade creates a fresh engine for each invocation.
    private int mixedHistoryReviewRowsFromMain,mixedHistoryExplicitReviewRows,mixedHistoryDuplicateRows;
    private sealed class A4MixedRow
    {
        internal double[] P,Raw,Filtered,Equivalent;
        internal bool Review;
        internal A4CanonicalOutputIdentity Canonical;
        internal int SourceRow;
        internal double SourceId;
    }
    private static bool A4MixedIsReview(double[] row)
    {return Finite(row[33])&&row[33]<.7&&row[35]==0;}
    private static int A4MixedCount(double[,] matrix)
    {return matrix==null?0:matrix.GetLength(0);}
    private static bool A4MixedEqualNumber(double a,double b)
    {return a==b||double.IsNaN(a)&&double.IsNaN(b);}
    private static void A4MixedValidateThreeShapes(double[,] p,double[,] raw,double[,] filtered,string label)
    {
        int rows=A4MixedCount(p);
        if(rows!=A4MixedCount(raw)||rows!=A4MixedCount(filtered)||
            p!=null&&p.GetLength(1)!=36&&(rows!=0||p.GetLength(1)!=0))
            throw new ArgumentException(label+": parameter, raw and filtered matrices require corresponding rows and 36 parameter columns.");
        // Widths from different history sources may differ, but each source's raw
        // and filtered rows still share one explicitly verifiable valid interval.
        if(rows>0&&raw.GetLength(1)!=filtered.GetLength(1))
            throw new ArgumentException(label+": raw and filtered waveform widths must match.");
    }
    private static void A4MixedRequireSameReview(A4MixedRow original,A4MixedRow duplicate,string label)
    {
        for(int c=0;c<36;c++)
        {
            // IDs are independently sorted by the two outputs. Review equivalent
            // width/bandwidth are unvalidated and the mixed output always writes
            // NaN; tolerate legacy review ports that retained values in 23/24.
            if(c==9||c==23||c==24)continue;
            if(!A4MixedEqualNumber(original.P[c],duplicate.P[c]))
                throw new ArgumentException(label+": duplicate review identity has conflicting parameter column "+c+".");
        }
        int count=checked((int)((long)original.P[32]-(long)original.P[31]+1));
        for(int c=0;c<count;c++)
            if(original.Raw[c]!=duplicate.Raw[c]||original.Filtered[c]!=duplicate.Filtered[c])
                throw new ArgumentException(label+": duplicate review identity has conflicting valid waveform samples.");
        // Each source has already been fully validated; tails contain NaN only,
        // so their different rectangular padding widths do not constitute data.
    }
    private void A4MixedAddReview(Dictionary<string,A4MixedRow> seen,List<A4MixedRow> rows,
        double[] p,double[] raw,double[] filtered,string label)
    {
        A4ValidateReviewRow(p,raw,filtered,label);
        var row=new A4MixedRow {P=p,Raw=raw,Filtered=filtered,Review=true};
        string key=A4RowKey(p);A4MixedRow earlier;
        if(seen.TryGetValue(key,out earlier))
        {A4MixedRequireSameReview(earlier,row,label);mixedHistoryDuplicateRows++;return;}
        seen.Add(key,row);rows.Add(row);
    }
    internal void SplitMixedReviewHistory(double[,] previousP,double[,] previousR,double[,] previousF,double[,] previousE,
        double[,] previousReviewP,double[,] previousReviewR,double[,] previousReviewF,
        out double[,] physicalP,out double[,] physicalR,out double[,] physicalF,out double[,] physicalE,
        out double[,] reviewP,out double[,] reviewR,out double[,] reviewF)
    {
        mixedHistoryReviewRowsFromMain=mixedHistoryExplicitReviewRows=mixedHistoryDuplicateRows=0;
        A4MixedValidateThreeShapes(previousP,previousR,previousF,"Main history");
        A4MixedValidateThreeShapes(previousReviewP,previousReviewR,previousReviewF,"Independent review history");
        int mainRows=A4MixedCount(previousP);
        if(A4MixedCount(previousE)!=mainRows||mainRows>0&&previousE.GetLength(1)<1)
            throw new ArgumentException("Mixed main history requires one equivalent-waveform row per parameter row; return all four main matrices together. Review-only rows require an explicit all-NaN equivalent row, not a missing matrix.");
        var parameters=new List<double[]>();var rawRows=new List<double[]>();var filteredRows=new List<double[]>();var equivalentRows=new List<double[]>();
        var pending=new List<A4MixedRow>();var identities=new Dictionary<string,A4MixedRow>();
        for(int r=0;r<mainRows;r++)
        {
            double[] p=A4Row(previousP,r),raw=A4Row(previousR,r),filtered=A4Row(previousF,r),equivalent=A4Row(previousE,r);
            if(A4MixedIsReview(p))
            {
                if(equivalent.Any(v=>!double.IsNaN(v)))
                    throw new ArgumentException("Main history review row "+r+": equivalent waveform must be entirely NaN, because equivalent features were not validated.");
                mixedHistoryReviewRowsFromMain++;
                A4MixedAddReview(identities,pending,p,raw,filtered,"Main history review row "+r);
            }
            else
            {
                // A physically confirmed row with project acceptance column35=0
                // remains physical history. Score+flag jointly identify review.
                parameters.Add(p);rawRows.Add(raw);filteredRows.Add(filtered);equivalentRows.Add(equivalent);
            }
        }
        for(int r=0;r<A4MixedCount(previousReviewP);r++)
        {
            mixedHistoryExplicitReviewRows++;
            A4MixedAddReview(identities,pending,A4Row(previousReviewP,r),A4Row(previousReviewR,r),A4Row(previousReviewF,r),"Independent review history row "+r);
        }
        physicalP=A4Matrix(parameters,36);physicalR=A4Matrix(rawRows,0);physicalF=A4Matrix(filteredRows,0);physicalE=A4Matrix(equivalentRows,0);
        // Preserve existing physical-row validation and equivalent waveform rules.
        // No invalid/missing physical equivalent row is repaired by review data.
        A4ValidatePrevious(physicalP,physicalR,physicalF);
        var reviewParameters=pending.Select(v=>v.P).ToList();var reviewRaw=pending.Select(v=>v.Raw).ToList();var reviewFiltered=pending.Select(v=>v.Filtered).ToList();
        A4SortRowsByCharge(reviewParameters,reviewRaw,reviewFiltered);
        reviewP=A4Matrix(reviewParameters,36);reviewR=A4Matrix(reviewRaw,0);reviewF=A4Matrix(reviewFiltered,0);
    }
    internal void CompleteMixedReviewOutput(ref double[,] nextP,ref double[,] nextR,ref double[,] nextF,ref double[,] nextE,
        double[,] nextReviewP,double[,] nextReviewR,double[,] nextReviewF)
    {
        A4MixedValidateThreeShapes(nextP,nextR,nextF,"Physical output");
        A4MixedValidateThreeShapes(nextReviewP,nextReviewR,nextReviewF,"Independent review output");
        int physicalCount=A4MixedCount(nextP),reviewCount=A4MixedCount(nextReviewP);
        if(A4MixedCount(nextE)!=physicalCount||physicalCount>0&&nextE.GetLength(1)<1)
            throw new InvalidOperationException("Physical equivalent output lost its row correspondence before review merge.");
        var rows=new List<A4MixedRow>();var identities=new HashSet<string>();var endpoints=new HashSet<string>();
        for(int r=0;r<physicalCount;r++)
        {
            double[] p=A4Row(nextP,r);
            if(A4MixedIsReview(p))throw new InvalidOperationException("Review row entered the physical output before the display-only merge.");
            var identity=A4BoundCanonicalIdentity(canonicalPhysicalRows,r,physicalCount,p,false);
            if(!identities.Add(identity.PairKey))throw new InvalidOperationException("Duplicate canonical physical output identity before review merge.");
            if(!endpoints.Add(identity.IncidentKey)||!endpoints.Add(identity.ReflectionKey))
                throw new InvalidOperationException("Physical outputs share a canonical endpoint before review merge.");
            rows.Add(new A4MixedRow {P=p,Raw=A4Row(nextR,r),Filtered=A4Row(nextF,r),Equivalent=A4Row(nextE,r),Review=false,Canonical=identity,SourceRow=r,SourceId=p[9]});
        }
        int equivalentWidth=Math.Max(1,nextE==null?0:nextE.GetLength(1));
        for(int r=0;r<reviewCount;r++)
        {
            double[] p=A4Row(nextReviewP,r),raw=A4Row(nextReviewR,r),filtered=A4Row(nextReviewF,r);
            var identity=A4BoundCanonicalIdentity(canonicalReviewRows,r,reviewCount,p,true);
            A4ValidateReviewRow(p,raw,filtered,"Independent review output row "+r,identity!=null);
            // A null identity is explicitly validated private-stage history, not
            // a current row. Public history is appended after this stage.
            if(identity!=null)
            {
                if(!identities.Add(identity.PairKey))throw new InvalidOperationException("Duplicate canonical output identity across physical/review matrices.");
                if(endpoints.Contains(identity.IncidentKey)||endpoints.Contains(identity.ReflectionKey))
                    throw new InvalidOperationException("Review output still uses a confirmed canonical endpoint; independent review exclusion must run before display merge.");
            }
            double sourceId=p[9];p[23]=double.NaN;p[24]=double.NaN;p[35]=0;
            rows.Add(new A4MixedRow {P=p,Raw=raw,Filtered=filtered,Equivalent=Enumerable.Repeat(double.NaN,equivalentWidth).ToArray(),
                Review=true,Canonical=identity,SourceRow=r,SourceId=sourceId});
        }
        rows=rows.OrderByDescending(v=>Finite(v.P[15])?Math.Abs(v.P[15]):double.NegativeInfinity)
            .ThenBy(v=>v.P[0]).ThenBy(v=>v.P[1]).ThenBy(v=>v.Canonical==null?v.P[10]:v.Canonical.Incident)
            .ThenBy(v=>v.Canonical==null?v.P[11]:v.Canonical.Reflection).ThenBy(v=>v.P[26]).ToList();
        var mapping=new StringBuilder();
        for(int r=0;r<rows.Count;r++)
        {
            A4MixedRow row=rows[r];row.P[9]=r;if(r>0)mapping.Append(',');
            mapping.Append("{\"source\":").Append(Json(row.Review?"independent_review":"physical_before_merge"))
                .Append(",\"source_row\":").Append(row.SourceRow).Append(",\"source_id_column9\":").Append(NumberJson(row.SourceId))
                .Append(",\"main_row\":").Append(r).Append(",\"main_id_column9\":").Append(r)
                .Append(",\"phase\":").Append(NumberJson(row.P[0])).Append(",\"record_id\":").Append(NumberJson(row.P[1]))
                .Append(",\"incident_index\":").Append(NumberJson(row.P[10])).Append(",\"reflection_index\":").Append(NumberJson(row.P[11])).Append('}');
        }
        // Construct every result before replacing any reference. The facade owns
        // the seven-output transaction and still exposes original inputs on error.
        double[,] combinedP=A4Matrix(rows.Select(v=>v.P).ToList(),36),combinedR=A4Matrix(rows.Select(v=>v.Raw).ToList(),0),
            combinedF=A4Matrix(rows.Select(v=>v.Filtered).ToList(),0),combinedE=A4Matrix(rows.Select(v=>v.Equivalent).ToList(),0);
        string status=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+",\"merged_review_output\":{\"enabled\":true,\"scope\":\"output_only; not_physical_matching_ownership_or_project_statistics\",\"implicit_history_cache\":false,"+
            "\"review_identity_rule\":\"column33<0.70 AND column35=0\",\"high_confidence_project_unaccepted_rows_remain_physical\":true,"+
            "\"review_confidence_modified\":false,\"review_equivalent_features_columns23_24\":\"NaN_in_mixed_main_output\",\"review_equivalent_waveform\":\"all_NaN_at_least_one_column\","+
            "\"main_matrices_share_global_amplitude_sort\":true,\"sort\":\"abs(column15)_descending\",\"main_id_column9\":\"zero_based_global_sorted_row\","+
            "\"independent_review_outputs_retained\":true,\"independent_review_id_space\":\"zero_based_review_only_sorted_row\",\"independent_review_subset_definition\":\"same_identity_confidence_and_waveform; IDs_independent; unvalidated_23_24_may_be_legacy_values\","+
            "\"legacy_diagnostic_row_space\":\"physical_before_merge\",\"project_statistics_scope\":\"physical_before_merge_only\",\"equivalent_waveform_status_scope\":\"physical_before_merge_only\","+
            "\"history_review_rows_from_main\":"+mixedHistoryReviewRowsFromMain+",\"history_explicit_review_rows\":"+mixedHistoryExplicitReviewRows+",\"history_duplicate_review_rows_removed\":"+mixedHistoryDuplicateRows+","+
            "\"deduplication_key\":\"current_phase_record_canonical_incident_reflection\",\"display_timing_used_for_current_identity\":false,\"history_canonical_identity_inferred\":false,\"duplicate_ignored_parameter_columns\":[9,23,24],\"duplicate_ignored_waveform_values\":\"NaN_tail_padding_only\","+
            "\"physical_rows\":"+physicalCount+",\"review_rows\":"+reviewCount+",\"main_rows\":"+rows.Count+",\"row_identity_mapping\":["+mapping+"]}}";
        canonicalMixedRows=rows.Select(v=>v.Canonical).ToArray();
        nextP=combinedP;nextR=combinedR;nextF=combinedF;nextE=combinedE;StatusJson=status;LastResultJson=status;
        // Engine properties intentionally remain in the physical-analysis space.
        // Only the explicit public return matrices contain these review copies.
    }
}
