using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // New policy is called only by 1..5, 7 and 8. A4AnalysisLimit and method 6
    // remain unchanged. Zero/negative retains the legacy automatic spelling.
    private static int ReviewedAnalysisLimit(int requested)
    { return requested<=0?512:Math.Min(512,requested); }

    private static void ReviewedValidatePhaseClock(double frequency,double sampleRate,int sampleCount)
    {
        if(Finite(frequency)&&(frequency<0||frequency>sampleRate/2||
            !Finite(360*frequency*Math.Max(0,sampleCount-1)/sampleRate)))
            throw new ArgumentException("Oscillation frequency cannot be represented on the native LOC clock; no discharge conclusion computed.");
    }

    private static string ReviewedCoverageJson(A4Context c,IEnumerable<A4Pulse> packets,int requested)
    {
        var candidates=packets.Where(p=>A4AnalysisPhaseEligible(c,A4ChargeIndex(p))&&
            A4RawCharge(c,A4ChargeIndex(p))>0&&A4RawCharge(c,A4ChargeIndex(p))>=c.IncidentThreshold)
            .GroupBy(A4ChargeIndex).Select(g=>g.First()).ToArray();
        var visited=new HashSet<int>(c.AnalysisVisitIds);
        int notVisited=candidates.Count(p=>!visited.Contains(p.Id)&&p.Owner<0);
        return "{\"requested\":"+requested+",\"effective\":"+c.AnalysisLimit+
            ",\"automatic_budget\":512,\"hard_safety_budget\":512,\"requested_above_safety_budget\":"+
            (requested>512?"true":"false")+",\"eligible_threshold_packets\":"+candidates.Length+
            ",\"unvisited_unowned_threshold_packets\":"+notVisited+
            ",\"complete_candidate_coverage\":"+(!c.WindowBudgetExceeded&&!c.CandidateEvaluationLimitExceeded&&notVisited==0?"true":"false")+
            ",\"policy\":\"explicit_positive_limit_honored_up_to_512; zero_or_negative_automatic_512; incomplete_is_not_no_discharge\"}";
    }

    // A pending signed extrapolation remains evidence, not an accepted endpoint.
    // Projected endpoint positions are explicitly distinguished from raw timing.
    private static string ReviewedWindowJson(A4Context c,IEnumerable<double[]> rows)
    {
        var details=new List<string>();int nominalOutside=0,revoked=0;
        foreach(double[] row in rows)
        {
            if(!Finite(row[11])||!Finite(row[10]))continue;
            double delay=row[11]-row[10];
            double rawPosition=delay>=int.MinValue&&delay<=int.MaxValue&&delay==Math.Truncate(delay)?
                A4BoundaryOutputPosition((int)delay,c.Fs,c.Joints,c.Velocities,c.Length):double.NaN;
            bool nominal=Finite(rawPosition)&&delay>0&&delay<=c.RoundTripExact+1e-9&&rawPosition>=-1e-9&&rawPosition<=c.Length+1e-9;
            if(!nominal){nominalOutside++;if(row[35]==1)revoked++;row[35]=0;}
            details.Add("{\"row\":"+NumberJson(row[9])+",\"delay_samples\":"+NumberJson(delay)+
                ",\"raw_timing_position_m\":"+NumberJson(rawPosition)+",\"output_position_m\":"+NumberJson(row[13])+
                ",\"nominal_window\":"+(nominal?"true":"false")+
                ",\"endpoint_display_projected\":"+(Finite(rawPosition)&&Finite(row[13])&&Math.Abs(rawPosition-row[13])>1e-9?"true":"false")+"}");
        }
        return "{\"exact_round_trip_samples\":"+NumberJson(c.RoundTripExact)+
            ",\"nominal_outside_rows\":"+nominalOutside+",\"acceptance_revoked\":"+revoked+
            ",\"outside_policy\":\"retain_signed_review_position; never_auto_accept_or_silently_clip_to_endpoint\",\"rows\":["+
            String.Join(",",details)+"]}";
    }

    private static void ReviewedMatrixBudget(int count,int width)
    {
        if(count<0||width<0||(long)count*width>4194304)
            throw new InvalidOperationException("Reviewed output matrix budget exceeded; no partial localization is committed. Reduce the incident budget or output context.");
    }

    internal void CompleteReviewedSafetyContract(ref double[,] parameters,bool firstArrival)
    {
        var c=equivalentContext;
        if(c==null||c.Algorithm<0||c.Algorithm>4)return; // Never method 6.
        var next=(double[,])parameters.Clone();var timing=new List<string>();
        for(int r=0;r<next.GetLength(0);r++)
        {
            A4Pair pair=A4CurrentMixedPair(parameters,r);
            int peak=pair==null?projectRetainedRawPeaks[r]:A4ChargeIndex(pair.Incident);
            next[r,3]=c.CycleReference!=null?A4PhaseAt(c,peak):Finite(EffectiveOscillationFrequencyHz)&&EffectiveOscillationFrequencyHz>0?
                NormalizeDegrees(EffectiveInitialPhaseDegrees+360*EffectiveOscillationFrequencyHz*peak/c.Fs):
                NormalizeDegrees(EffectiveInitialPhaseDegrees);
            if(firstArrival&&pair!=null&&Finite(next[r,10])&&Finite(next[r,11]))
            {
                var uncertainty=HVX.SignalEvidence.RequestedTimingUncertaintyMath.Evaluate(true,
                    peak,A4ChargeIndex(pair.Reflection),(int)next[r,10],(int)next[r,11],c.RoundTripExact);
                if(!uncertainty.Valid||uncertainty.RequiresReview)
                {next[r,35]=0;if(Finite(next[r,33]))next[r,33]=Math.Min(.49,next[r,33]);}
                timing.Add("{\"row\":"+r+",\"requires_review\":"+(!uncertainty.Valid||uncertainty.RequiresReview?"true":"false")+
                    ",\"anchor_delay_disagreement_samples\":"+NumberJson(uncertainty.DisagreementSamples)+
                    ",\"tolerance_samples\":"+NumberJson(uncertainty.ToleranceSamples)+",\"reason\":"+Json(uncertainty.Reason)+"}");
            }
            if(Finite(next[r,12])&&next[r,12]==Math.Truncate(next[r,12])&&next[r,12]>=int.MinValue&&next[r,12]<=int.MaxValue)
            {
                double rawPosition=A4BoundaryOutputPosition((int)next[r,12],c.Fs,c.Joints,c.Velocities,c.Length);
                if(Finite(rawPosition)&&(rawPosition<0||rawPosition>c.Length))next[r,13]=rawPosition;
            }
            if(next[r,35]==1&&(!Finite(next[r,13])||!Finite(next[r,33])||!Finite(next[r,11])||next[r,11]<=next[r,10]))
                next[r,35]=0;
            for(int col=0;col<36;col++)if(Double.IsInfinity(next[r,col]))
                throw new InvalidOperationException("Nonfinite output arithmetic; no partial localization committed.");
        }
        var rows=Enumerable.Range(0,next.GetLength(0)).Select(r=>A4Row(next,r)).ToList();
        string windows=ReviewedWindowJson(c,rows);
        for(int r=0;r<rows.Count;r++)next[r,35]=rows[r][35];
        StatusJson=StatusJson.Substring(0,StatusJson.LastIndexOf('}'))+
            ",\"final_safety_contract\":{\"phase_anchor\":\"canonical_incident_peak\",\"window_review\":"+windows+
            ",\"timing_uncertainty\":["+String.Join(",",timing)+"]}}";
        LastResultJson=StatusJson;parameters=next;ParameterMatrix2D=next;ParameterMatrix=Flatten(next);
    }
}
