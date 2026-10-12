using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    // A caller-supplied, per-invocation prior. It affects review preference only,
    // never measurements, candidate discovery, hard eligibility or commitment.
    private double[,] a6SpatialHistory=new double[0,36];
    private int a6SpatialPhase;
    private long a6SpatialRecordId;
    private List<AstraSixthSpatialPreference.Anchor> a6SpatialAnchors=new List<AstraSixthSpatialPreference.Anchor>();
    private readonly Dictionary<A4Pair,string> a6SpatialDecisions=new Dictionary<A4Pair,string>();
    private readonly Dictionary<string,int> a6SpatialHistoryExclusions=new Dictionary<string,int>();
    private int a6SpatialCurrentAnchors,a6SpatialHistoryAnchors,a6SpatialHistoryScanned;
    private bool a6SpatialHistoryIncomplete;
    private sealed class A6HistoricalAnchor
    {internal double Record,Time,Position,TimingTolerance,WindowStart,WindowEnd,Charge;}

    internal void A6SetSpatialHistory(double[,] history)
    {a6SpatialHistory=history==null?new double[0,36]:(double[,])history.Clone();}
    private void A6SetSpatialIdentity(int phase,long recordId)
    {a6SpatialPhase=phase;a6SpatialRecordId=recordId;}
    private void A6SpatialExclude(string reason,int count=1)
    {int old;a6SpatialHistoryExclusions.TryGetValue(reason,out old);a6SpatialHistoryExclusions[reason]=old+count;}
    private static double A6SpatialRadius(A4Context c)
    {return Math.Max(2*c.Velocity*1e6/(2*c.Fs),Math.Min(10,Math.Max(2,.01*c.Length)));}
    private static double A6SpatialCoordinate(A4Context c,double position)
    {return Finite(position)&&position>=-A6SpatialRadius(c)&&position<=c.Length+A6SpatialRadius(c)?Clamp(position,0,c.Length):double.NaN;}

    private void A6PrepareSpatialPrior(A4Context c,List<A4Pair> committed)
    {
        a6SpatialAnchors.Clear();a6SpatialDecisions.Clear();a6SpatialHistoryExclusions.Clear();
        a6SpatialCurrentAnchors=a6SpatialHistoryAnchors=a6SpatialHistoryScanned=0;a6SpatialHistoryIncomplete=false;
        // Freeze stage-one anchors before selecting any ambiguous review. A review
        // chosen by this prior never becomes evidence for the next review.
        foreach(var pair in committed.OrderBy(p=>p.OutputIncident))
        {
            A6Evidence e;if(!a6Evidence.TryGetValue(pair,out e))continue;
            double x=A6SpatialCoordinate(c,e.EvidencePosition);
            if(pair.State!="accepted"||pair.Ambiguous||!e.Eligible||!Finite(x)||
                A4LocalAmbiguous(a6LocalAmbiguities,pair.Incident.Id))continue;
            a6SpatialAnchors.Add(new AstraSixthSpatialPreference.Anchor(x,"current:"+pair.Incident.Id));a6SpatialCurrentAnchors++;
        }
        int rows=a6SpatialHistory.GetLength(0);
        if(rows>100000){a6SpatialHistoryIncomplete=true;A6SpatialExclude("history_scan_budget_exceeded_prior_disabled",rows);return;}
        if(a6SpatialHistory.GetLength(1)!=36){A6SpatialExclude("history_schema_not_36_columns",rows);return;}
        var valid=new List<A6HistoricalAnchor>();
        for(int i=0;i<rows;i++)
        {
            a6SpatialHistoryScanned++;
            if(a6SpatialHistory[i,0]!=a6SpatialPhase){A6SpatialExclude("other_phase");continue;}
            if(a6SpatialHistory[i,35]!=1||!Finite(a6SpatialHistory[i,33])||a6SpatialHistory[i,33]<.70||a6SpatialHistory[i,33]>1)
            {A6SpatialExclude("not_accepted_high_score");continue;}
            double id=a6SpatialHistory[i,1],t0=a6SpatialHistory[i,10],t1=a6SpatialHistory[i,11];
            if(!Finite(id)||id<0||id>9007199254740991d||id!=Math.Floor(id)||!Finite(t0)||!Finite(t1)||t0<0||t1<=t0)
            {A6SpatialExclude("invalid_identity_or_unpaired");continue;}
            if(id==a6SpatialRecordId){A6SpatialExclude("current_record_id_self_reference");continue;}
            double length=a6SpatialHistory[i,5],velocity=a6SpatialHistory[i,6],fs=a6SpatialHistory[i,7];
            if(!Finite(length)||!Finite(velocity)||!Finite(fs)||Math.Abs(length-c.Length)>Math.Max(.05,c.Length*1e-4)||
                Math.Abs(velocity-c.Velocity)>Math.Max(1e-6,c.Velocity*1e-4)||Math.Abs(fs-c.Fs)>Math.Max(1e-3,c.Fs*1e-8))
            {A6SpatialExclude("incompatible_cable_geometry_or_sample_rate");continue;}
            double x=A6SpatialCoordinate(c,a6SpatialHistory[i,13]);
            if(!Finite(x)||!Finite(a6SpatialHistory[i,15])||a6SpatialHistory[i,15]<=0)
            {A6SpatialExclude("unknown_or_outlying_position_or_charge");continue;}
            double width=a6SpatialHistory[i,23];
            double tolerance=Finite(width)&&width>0?Math.Max(8,Math.Min(1000,width)*fs/1e9):8;
            valid.Add(new A6HistoricalAnchor {Record=id,Time=t0,Position=x,TimingTolerance=tolerance,
                WindowStart=a6SpatialHistory[i,31],WindowEnd=a6SpatialHistory[i,32],Charge=a6SpatialHistory[i,15]});
        }
        // Peak/first-arrival timing can differ by more than equivalent RMS width.
        // The same raw window and unchanged raw charge provide an independent
        // duplicate identity without enlarging every event's timing neighborhood.
        var windowDeduplicated=new List<A6HistoricalAnchor>();
        foreach(var record in valid.GroupBy(x=>x.Record))
        {
            var withWindow=record.Where(x=>Finite(x.WindowStart)&&Finite(x.WindowEnd)&&
                x.WindowStart>=0&&x.WindowStart==Math.Floor(x.WindowStart)&&
                x.WindowEnd==Math.Floor(x.WindowEnd)&&x.WindowEnd>x.WindowStart);
            windowDeduplicated.AddRange(record.Except(withWindow));
            foreach(var duplicate in withWindow.GroupBy(x=>new {x.WindowStart,x.WindowEnd,x.Charge}))
            {
                var group=duplicate.OrderBy(x=>x.Position).ThenBy(x=>x.Time).ToArray();
                if(group[group.Length-1].Position-group[0].Position>A6SpatialRadius(c)/2)
                    A6SpatialExclude("same_raw_window_event_conflicting_positions",group.Length);
                else
                {
                    var representative=group[group.Length/2];
                    windowDeduplicated.Add(representative);
                    if(group.Length>1)A6SpatialExclude("same_raw_window_event_vote_removed",group.Length-1);
                }
            }
        }
        valid=windowDeduplicated;
        // A duplicate event (including nearby first-arrival/peak variants) has one
        // vote. Contradictory locations for that event do not vote at all.
        var anchors=new List<AstraSixthSpatialPreference.Anchor>();
        foreach(var record in valid.GroupBy(x=>x.Record).OrderBy(g=>g.Key))
        {
            var ordered=record.OrderBy(x=>x.Time).ThenBy(x=>x.Position).ToArray();
            for(int i=0;i<ordered.Length;)
            {
                int end=i+1;double tolerance=ordered[i].TimingTolerance;
                while(end<ordered.Length&&ordered[end].Time-ordered[i].Time<=Math.Max(tolerance,ordered[end].TimingTolerance))end++;
                var group=ordered.Skip(i).Take(end-i).OrderBy(x=>x.Position).ToArray();
                if(group[group.Length-1].Position-group[0].Position>A6SpatialRadius(c)/2)
                    A6SpatialExclude("duplicate_event_conflicting_positions",group.Length);
                else
                {
                    string key="history:"+record.Key.ToString("R",CultureInfo.InvariantCulture)+":"+ordered[i].Time.ToString("R",CultureInfo.InvariantCulture);
                    anchors.Add(new AstraSixthSpatialPreference.Anchor(group[group.Length/2].Position,key));
                    if(group.Length>1)A6SpatialExclude("duplicate_event_vote_removed",group.Length-1);
                }
                i=end;
            }
        }
        if(anchors.Count>4096){a6SpatialHistoryIncomplete=true;A6SpatialExclude("history_anchor_budget_exceeded_prior_disabled",anchors.Count);return;}
        a6SpatialHistoryAnchors=anchors.Count;a6SpatialAnchors.AddRange(anchors);
    }

    private A4Pair A6ChooseSpatialReview(A4Context c,A4Pulse incident,List<A4Pair> candidates,A4Pair fallback)
    {
        if(fallback==null||!A4LocalAmbiguous(a6LocalAmbiguities,incident.Id))return fallback;
        var eligible=candidates.Where(p=>p.Incident.Id==incident.Id&&a6Evidence[p].Eligible&&
            A6ReviewMeasurable(a6Evidence[p])).OrderByDescending(p=>a6Evidence[p].Score).ThenBy(p=>p.OutputReflection).ToList();
        if(eligible.Count>0)
        {double maximum=a6Evidence[eligible[0]].Score;eligible=eligible.Where(p=>a6Evidence[p].Score>=70&&maximum-a6Evidence[p].Score<=5+1e-9).ToList();}
        var ranked=eligible.Select(p=>new AstraSixthSpatialPreference.Candidate(p.EdgeId,A6SpatialCoordinate(c,a6Evidence[p].EvidencePosition),a6Evidence[p].Score));
        var decision=AstraSixthSpatialPreference.Evaluate(ranked,a6SpatialAnchors,c.Length,c.Velocity*1e6/(2*c.Fs));
        A4Pair selected=eligible.FirstOrDefault(p=>p.EdgeId==decision.PreferredId)??fallback;
        bool clear=decision.HasConcentrationPreference&&eligible.Contains(selected);
        double factor=clear?1:.8;
        selected.Confidence=a6Evidence[selected].Score/100*factor;
        // Never set Eligible/Selected/State=accepted or change owner/measurements.
        a6SpatialDecisions[selected]="{\"applied\":true,\"uses_history_and_frozen_current_anchors\":true,"+
            "\"original_score_percent\":"+NumberJson(a6Evidence[selected].Score)+",\"output_score_percent\":"+NumberJson(100*selected.Confidence)+
            ",\"score_factor\":"+NumberJson(factor)+",\"preference_only_still_unaccepted\":true,\"hard_gates_not_relaxed\":true,"+
            "\"reason\":"+Json(clear?decision.Reason:"no_unique_reliable_concentration:"+decision.Reason)+
            ",\"radius_m\":"+NumberJson(decision.Radius)+",\"anchor_count\":"+decision.AnchorCount+
            ",\"eligible_close_candidate_count\":"+decision.CandidateCount+",\"preferred_support\":"+decision.PreferredSupport+
            ",\"runner_up_support\":"+decision.RunnerUpSupport+",\"original_fallback_reflection_peak\":"+fallback.OutputReflection+
            ",\"selected_reflection_peak\":"+selected.OutputReflection+"}";
        return selected;
    }
    private string A6SpatialDecisionJson(A4Pair pair)
    {string json;return a6SpatialDecisions.TryGetValue(pair,out json)?json:"{\"applied\":false}";}
    private string A6SpatialPriorJson(A4Context c)
    {
        return "{\"scope\":\"method6_ambiguous_review_preference_only\",\"current_anchor_count\":"+a6SpatialCurrentAnchors+
            ",\"historical_anchor_count\":"+a6SpatialHistoryAnchors+",\"history_rows_scanned\":"+a6SpatialHistoryScanned+
            ",\"history_prior_budget_incomplete\":"+(a6SpatialHistoryIncomplete?"true":"false")+
            ",\"radius_m\":"+NumberJson(A6SpatialRadius(c))+",\"minimum_distinct_support\":3,\"minimum_uniform_density_ratio\":2,"+
            "\"minimum_winner_support_ratio\":1.5,\"minimum_winner_support_gap\":2,\"no_concentration_score_factor\":0.8,"+
            "\"history_same_phase_and_geometry_only\":true,\"current_record_id_excluded\":true,\"current_anchors_frozen_before_preferences\":true,"+
            "\"preference_results_never_become_anchors\":true,\"accepted_results_unchanged_by_prior\":true,"+
            "\"history_project_identity_not_encoded_caller_must_supply_same_project\":true,"+
            "\"deduplication_uses_record_id_and_incident_timing_not_file_hash\":true,"+
            "\"same_record_raw_window_and_charge_variants_deduplicated\":true,\"no_persistent_history\":true,"+
            "\"engineering_prior_not_field_probability\":true,\"history_exclusions\":{"+
            String.Join(",",a6SpatialHistoryExclusions.OrderBy(x=>x.Key).Select(x=>Json(x.Key)+":"+x.Value))+"}}";
    }
}
