using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // Final fail-closed guard after endpoint promotion, best-candidate recovery
    // and timing projection. Only current scores/acceptance can be downgraded;
    // never changes history, physical columns, pulse ownership or waveforms.
    internal void CompleteUnifiedAttenuationReview(ref double[,] parameters)
    {
        var c=equivalentContext;
        if(c==null||c.Algorithm<0||c.Algorithm>4)return;
        var next=(double[,])parameters.Clone();var ledger=new List<string>();
        var packets=projectRetainedPackets??new List<A4Pulse>();
        for(int r=0;r<next.GetLength(0);r++)
        {
            if(!c.Attenuation)continue;
            if(parameters[r,0]!=reviewCurrentPhase||parameters[r,1]!=reviewCurrentRecordId)
                throw new InvalidOperationException("Final attenuation row is not bound to the current phase/CAL and LOC record.");
            var pair=A4CurrentMixedPair(parameters,r);
            if(pair==null&&(projectRetainedRawPeaks==null||projectRetainedRawPeaks.Length!=next.GetLength(0)))
                throw new InvalidOperationException("Final attenuation retained packet identity is unavailable.");
            A4Pulse a=pair==null?packets.FirstOrDefault(p=>A4ChargeIndex(p)==projectRetainedRawPeaks[r]):pair.Incident;
            A4Pulse b=pair==null?null:pair.Reflection;
            double delay=parameters[r,12]/c.Fs;
            // A recovered output has a real reflection index but no original
            // accepted edge. Bind it only to one already-discovered raw support.
            if(b==null&&Finite(parameters[r,11]))
            {
                double reflectionIndex=parameters[r,11];
                var candidates=packets.Where(p=>p!=a&&p.Start<=reflectionIndex&&p.End>=reflectionIndex).Take(2).ToArray();
                if(candidates.Length==1)b=candidates[0];
            }
            A4AttenuationEvidence evidence=a!=null&&b!=null?
                A4CalibrationDeviationAtDelay(c,a,b,delay):new A4AttenuationEvidence {Enabled=true,
                    TolerancePercent=c.AttenuationTolerancePercent,Reason="no_reliable_final_raw_packet_pair"};
            double ceiling=HVX.SignalEvidence.SixthConfidenceMath.AttenuationCeiling(true,evidence.Applied,evidence.BasePassed,evidence.SoftConsistencyScore);
            next[r,33]=Math.Min(next[r,33],ceiling);
            if(!evidence.Applied||!evidence.Passed||!evidence.BasePassed||!Finite(next[r,33])||next[r,33]<0)next[r,35]=0;
            ledger.Add("{\"row\":"+r+",\"phase\":"+NumberJson(next[r,0])+",\"actual_output_delay_samples\":"+NumberJson(next[r,12])+
                ",\"before_score\":"+NumberJson(parameters[r,33])+",\"after_score\":"+NumberJson(next[r,33])+
                ",\"accepted\":"+(next[r,35]==1?"true":"false")+",\"evidence\":"+A4AttenuationJson(evidence)+"}");
        }
        string json="{\"enabled\":"+(c.Attenuation?"true":"false")+",\"policy\":\"shared_original_CAL_LOC_explicit_tolerance_final_downgrade_only\","+
            "\"tolerance_percent\":"+NumberJson(c.AttenuationTolerancePercent)+",\"allowed_changed_columns\":[33,35],\"rows\":["+String.Join(",",ledger)+"]}";
        string previous=String.IsNullOrWhiteSpace(StatusJson)?"{}":StatusJson;
        int end=previous.LastIndexOf('}');
        string prefix=end>=0?previous.Substring(0,end):"{";
        string separator=prefix.TrimEnd().EndsWith("{",StringComparison.Ordinal)?"":",";
        parameters=next;ParameterMatrix2D=next;ParameterMatrix=Flatten(next);
        StatusJson=LastResultJson=prefix+separator+"\"final_shared_attenuation_review\":"+json+"}";
    }
}
