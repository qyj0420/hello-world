using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private A4Context equivalentContext;
    private Dictionary<string,A4Pulse> equivalentCurrentPackets=new Dictionary<string,A4Pulse>();
    private static string A4RowKey(double[,] rows,int r)
    {return rows[r,0].ToString("R",Inv)+":"+rows[r,1].ToString("R",Inv)+":"+rows[r,10].ToString("R",Inv)+":"+rows[r,11].ToString("R",Inv);}
    private static string A4RowKey(double[] row)
    {return row[0].ToString("R",Inv)+":"+row[1].ToString("R",Inv)+":"+row[10].ToString("R",Inv)+":"+row[11].ToString("R",Inv);}
    private static int A4AnalysisLimit(int requested){return requested<=0?100:Math.Min(100,requested);}
    private static double[] A4NormalizeFilter(bool requested,double[] values,double fs,out bool enabled,out string reason)
    {
        enabled=false;reason=!requested?"switch_off":"missing_two_cutoffs";
        if(!requested||values==null||values.Length<2)return new double[0];
        if(double.IsNaN(values[0])||double.IsNaN(values[1]))throw new ArgumentException("Filter cutoff cannot be NaN.");
        double a=Clamp(values[0],10000,25000000),b=Clamp(values[1],10000,25000000);
        double low=Math.Min(a,b),high=Math.Min(Math.Max(a,b),fs/2);
        if(!(low<high)){reason="no_passband_after_clamp_and_nyquist";return new double[0];}
        enabled=true;reason="sorted_clamped_10khz_25mhz_and_nyquist";return new[]{low,high};
    }
    private static double[] A4NormalizeEquivalent(bool requested,double[] values,double fs,out bool enabled,out string reason)
    {
        enabled=false;reason=!requested?"automatic_complete_raw_packet":"missing_two_times_automatic_packet";
        if(!requested||values==null||values.Length<2)return new double[0];
        if(double.IsNaN(values[0])||double.IsNaN(values[1]))throw new ArgumentException("Equivalent pulse window cannot contain NaN.");
        double pre=Clamp(values[0],30,10000),total=Clamp(values[1],30,10000),dt=1e9/fs;
        // Keep the peak inside the requested window after sample quantisation.
        if(dt>9970){reason="sample_interval_too_large_for_custom_window_automatic_packet";return new double[0];}
        if(total<pre+dt){total=Math.Min(10000,pre+dt);pre=Math.Min(pre,total-dt);}
        enabled=true;reason="clamped_30_10000ns_peak_included";return new[]{pre,total};
    }
    internal double[,] CompleteEquivalentOutput(double[,] previousParameters,double[,] previousEquivalent,
        double[,] parameters,bool requested,double[] windowNs)
    {
        int oldCount=previousParameters==null?0:previousParameters.GetLength(0);
        if((previousEquivalent==null?0:previousEquivalent.GetLength(0))!=oldCount)
            throw new ArgumentException("previousEquivalentPulseWaveformMatrix must have one row per previousParameterMatrix row. Reanalyse old records when upgrading.");
        var history=new Dictionary<string,int>();
        for(int r=0;r<oldCount;r++)
        {string key=A4RowKey(previousParameters,r);if(history.ContainsKey(key))throw new ArgumentException("Duplicate history row identity.");history.Add(key,r);}
        bool custom;string reason;double[] times=A4NormalizeEquivalent(requested,windowNs,equivalentContext.Fs,out custom,out reason);
        var waves=new List<double[]>();var details=new List<string>();
        for(int r=0;r<parameters.GetLength(0);r++)
        {
            string key=A4RowKey(parameters,r);
            A4Pair physical=oldCount==0?A4CurrentPhysicalPair(parameters,r):null;
            A4Pulse pulse=physical==null?null:physical.Incident;
            if(oldCount==0&&pulse==null)
                throw new InvalidOperationException("Current equivalent waveform lost its canonical physical pair.");
            if(oldCount>0&&!equivalentCurrentPackets.TryGetValue(key,out pulse))
            {
                int oldRow;if(!history.TryGetValue(key,out oldRow))throw new InvalidOperationException("Equivalent waveform lost its parameter row identity.");
                double[] oldWave=A4Row(previousEquivalent,oldRow).TakeWhile(v=>!double.IsNaN(v)).ToArray();
                if(oldWave.Length==0||oldWave.Any(v=>!Finite(v)))throw new ArgumentException("History equivalent waveform is empty or invalid.");
                waves.Add(oldWave);details.Add("{\"row\":"+r+",\"history\":true,\"samples\":"+oldWave.Length+"}");continue;
            }
            int start=pulse.Start,end=pulse.End;bool clipped=false,recomputed=false;
            if(custom)
            {
                long before=checked((long)Math.Ceiling(times[0]*equivalentContext.Fs/1e9));
                long count=checked((long)Math.Ceiling(times[1]*equivalentContext.Fs/1e9));
                count=Math.Max(2,Math.Max(count,before+1));
                long wantedStart=(long)A4ChargeIndex(pulse)-before,wantedEnd=checked(wantedStart+count-1);
                start=(int)Math.Max(0,wantedStart);end=(int)Math.Min(equivalentContext.Raw.Length-1L,wantedEnd);
                clipped=wantedStart!=start||wantedEnd!=end;
            }
            if(end<start)throw new InvalidOperationException("Equivalent pulse window does not intersect the LOC data.");
            double[] waveform;
            if(pulse.AnalysisSamples!=null&&start>=pulse.AnalysisOrigin&&end<pulse.AnalysisOrigin+pulse.AnalysisSamples.Length)
                waveform=A4Slice(pulse.AnalysisSamples,start-pulse.AnalysisOrigin,end-pulse.AnalysisOrigin);
            else if(!equivalentContext.Filtered)waveform=A4Slice(equivalentContext.Raw,start,end);
            else
            {
                int guard=Math.Max(128,equivalentContext.RoundTrip),left=Math.Max(0,start-guard),right=Math.Min(equivalentContext.Raw.Length-1,end+guard);
                double[] filtered=A4ZeroPhase(A4Slice(equivalentContext.Raw,left,right),equivalentContext.Fs,equivalentContext.EffectiveCutoffs);
                waveform=A4Slice(filtered,start-left,end-left);recomputed=true;
            }
            double energy=0,derivative=0,peak=0;
            for(int j=0;j<waveform.Length;j++)
            {waveform[j]-=pulse.Baseline;energy+=waveform[j]*waveform[j];peak=Math.Max(peak,Math.Abs(waveform[j]));if(j>0){double d=waveform[j]-waveform[j-1];derivative+=d*d;}}
            parameters[r,23]=energy/Math.Max(1e-30,peak*peak)/equivalentContext.Fs*1e9;
            parameters[r,24]=equivalentContext.Fs/(2*Math.PI)*Math.Sqrt(derivative/Math.Max(1e-30,energy));
            waves.Add(waveform);
            details.Add("{\"row\":"+r+",\"history\":false,\"start\":"+start+",\"end\":"+end+",\"samples\":"+waveform.Length+
                ",\"raw_peak_anchor\":"+A4ChargeIndex(pulse)+",\"baseline_removed\":"+NumberJson(pulse.Baseline)+",\"clipped_to_record\":"+(clipped?"true":"false")+
                ",\"feature_filter_window_recomputed\":"+(recomputed?"true":"false")+"}");
        }
        StatusJson=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+",\"equivalent_waveform\":{\"scope\":\"all_rows_including_history\",\"custom_requested\":"+(requested?"true":"false")+
            ",\"custom_enabled\":"+(custom?"true":"false")+",\"normalization\":"+Json(reason)+",\"effective_window_ns\":"+NumberArrayJson(times)+
            ",\"units\":\"ns\",\"padding\":\"NaN_after_valid_samples\",\"signal\":\"analysis_window_samples_minus_local_baseline\",\"rows\":["+String.Join(",",details)+"]}}";
        // Keep the internal flattened mirror consistent after changing only metric columns.
        ParameterMatrix=Flatten(parameters);LastResultJson=StatusJson;
        return A4Matrix(waves,0);
    }
}
