using System;
using System.Globalization;
using System.Text;
using System.Reflection;

namespace HVX.Task17.Location;

/// <summary>Pure append-only matrix operations. No result, engine or input is retained.</summary>
internal static class AstraExplicitHistory
{
    internal static int Rows(double[,] matrix) { return matrix==null?0:matrix.GetLength(0); }
    private static bool Finite(double value) { return !double.IsNaN(value)&&!double.IsInfinity(value); }
    internal static double[,] Copy(double[,] matrix,int emptyColumns)
    {return matrix==null||matrix.GetLength(0)==0?new double[0,emptyColumns]:(double[,])matrix.Clone();}

    internal static void NumberRows(double[,] parameters)
    {for(int r=0;r<parameters.GetLength(0);r++)parameters[r,9]=r;}

    internal static void Validate(double[,] parameters,double[,] raw,double[,] filtered,double[,] equivalent,string label)
    {
        int rows=Rows(parameters);
        if(rows!=Rows(raw)||rows!=Rows(filtered)||rows!=Rows(equivalent))
            throw new ArgumentException(label+": all four matrices must have corresponding row counts.");
        if(parameters!=null&&parameters.GetLength(1)!=36&&!(rows==0&&parameters.GetLength(1)==0))
            throw new ArgumentException(label+": parameter matrix must have 36 columns (or an empty 0x0 input).");
        // Zero-row matrices have no sample interval to compare. LabVIEW may keep
        // independent old column widths after an empty array subset operation.
        if(rows>0&&raw.GetLength(1)!=filtered.GetLength(1))
            throw new ArgumentException(label+": raw and filtered waveform widths must match.");
        if(rows==0)return;
        if(raw.GetLength(1)==0||equivalent.GetLength(1)==0)
            throw new ArgumentException(label+": nonempty parameter rows require explicit waveform rows; unavailable equivalent rows use all-NaN samples.");
        for(int r=0;r<rows;r++)
        {
            double first=parameters[r,31],last=parameters[r,32];
            if(!Finite(first)||!Finite(last)||first<0||last<first||last>int.MaxValue||
                first!=Math.Floor(first)||last!=Math.Floor(last))
                throw new ArgumentException(label+": row "+r+" has invalid waveform bounds in columns31/32.");
            long count=(long)last-(long)first+1;
            if(count>raw.GetLength(1))
                throw new ArgumentException(label+": row "+r+" waveform is shorter than columns31/32 specify.");
            for(int c=0;c<raw.GetLength(1);c++)
            {
                bool valid=c<count?Finite(raw[r,c])&&Finite(filtered[r,c]):double.IsNaN(raw[r,c])&&double.IsNaN(filtered[r,c]);
                if(!valid)throw new ArgumentException(label+": row "+r+" raw/filtered samples must be finite inside columns31/32, then NaN only.");
            }
            bool padding=false;
            for(int c=0;c<equivalent.GetLength(1);c++)
            {
                double value=equivalent[r,c];
                if(double.IsNaN(value)){padding=true;continue;}
                if(padding||!Finite(value))
                    throw new ArgumentException(label+": row "+r+" equivalent waveform must be a finite prefix followed by NaN, or entirely NaN.");
            }
            // Acceptance and confidence are caller-owned historical values. Do not
            // interpret them as a row type, re-score them, or regenerate features.
        }
    }

    internal static void Append(double[,] previousP,double[,] previousR,double[,] previousF,double[,] previousE,
        double[,] currentP,double[,] currentR,double[,] currentF,double[,] currentE,
        out double[,] totalP,out double[,] totalR,out double[,] totalF,out double[,] totalE)
    {
        Validate(previousP,previousR,previousF,previousE,"Explicit history");
        Validate(currentP,currentR,currentF,currentE,"Current output");
        totalP=AppendMatrix(previousP,currentP,36);
        totalR=AppendMatrix(previousR,currentR,0);
        totalF=AppendMatrix(previousF,currentF,0);
        totalE=AppendMatrix(previousE,currentE,0);
        // pdmap_id belongs to the output's row space, not to the originating file.
        NumberRows(totalP);
    }

    private static double[,] AppendMatrix(double[,] previous,double[,] current,int minimumColumns)
    {
        int oldRows=Rows(previous),newRows=Rows(current),rows=checked(oldRows+newRows);
        int width=Math.Max(minimumColumns,Math.Max(oldRows==0?0:previous.GetLength(1),newRows==0?0:current.GetLength(1)));
        var output=new double[rows,width];
        for(int r=0;r<rows;r++)
        {
            double[,] source=r<oldRows?previous:current;int sourceRow=r<oldRows?r:r-oldRows;
            for(int c=0;c<width;c++)output[r,c]=c<source.GetLength(1)?source[sourceRow,c]:double.NaN;
        }
        return output;
    }

    private static string Json(string value)
    {
        var result=new StringBuilder("\"");
        foreach(char ch in value??"")
        {
            if(ch=='\"')result.Append("\\\"");
            else if(ch=='\\')result.Append("\\\\");
            else if(ch<32)result.Append("\\u").Append(((int)ch).ToString("x4",CultureInfo.InvariantCulture));
            else result.Append(ch);
        }
        return result.Append('"').ToString();
    }

    private static readonly string BuildVersion=Task17BuildInfo.Version;

    internal static string Status(bool ok,string stage,string analysisStatus,int previousRows,int currentRows,string message,
        bool useExplicitSpatialPrior=false)
    {
        string inner=String.IsNullOrWhiteSpace(analysisStatus)?"null":analysisStatus;
        string prior=useExplicitSpatialPrior?"true":"false";
        return "{\"ok\":"+(ok?"true":"false")+",\"build\":"+Json(BuildVersion)+",\"stage\":"+Json(stage)+
            ",\"message\":"+Json(message)+",\"history_mode\":\"caller_supplied_append_only\",\"implicit_history_cache\":false,"+
            "\"analysis_receives_history\":"+prior+",\"history_affects_matching_or_acceptance\":"+prior+","+
            "\"history_affects_ambiguous_review_preference_only\":"+prior+",\"history_affects_accepted_pairs\":false,\"previous_rows\":"+previousRows+
            ",\"current_rows\":"+currentRows+",\"total_rows\":"+((long)previousRows+currentRows)+
            ",\"total_order\":\"previous_rows_then_current_rows\",\"total_global_sort\":false,\"history_deduplicated\":false,"+
            "\"same_record_replacement\":false,\"repeated_current_input_appends_again\":true,"+
            "\"successful_total_column9\":\"zero_based_total_row\",\"current_column9\":\"zero_based_current_row\","+
            "\"history_parameter_changes_on_success\":\"column9_only\",\"historical_confidence_and_acceptance_preserved\":true,"+
            "\"review_rows_in_main_matrices\":true,\"independent_review_ports\":false,"+
            "\"failure_current_outputs_empty\":true,\"failure_total_outputs\":\"copies_of_explicit_history_only\","+
            "\"all_successful_output_arrays_independent\":true,\"current_analysis_diagnostics_scope\":\"current_call_only; internal_review_stages_are_not_public_ports; all_inner_row_ids_are_current_not_total\","+
            "\"current_analysis\":"+inner+"}";
    }
}
