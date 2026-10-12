using System;
using System.Collections.Generic;
using System.Linq;
using HVX.SignalEvidence;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    internal double SeventhMinimumLocalizationConfidencePercent {get;set;}
    private sealed class A7CausalDiagnosticSnapshot
    {
        internal A4Context Context;
        internal IList<A4Pair> All;
        internal SeventhGraphNode[] Nodes;
        internal SeventhGraphEdge[] Edges;
        internal Dictionary<A4Pair,SeventhProposalEvidence> Evidence;
        internal Dictionary<A4Pair,string> Reasons;
        internal Dictionary<A4Pair,string> SolverReasons;
        internal SeventhGraphResult Graph;
    }
    private A7CausalDiagnosticSnapshot a7CausalDiagnosticSnapshot;
    private readonly Dictionary<A4Pair,SeventhEngineeringQualityResult> a7EngineeringQualities=new Dictionary<A4Pair,SeventhEngineeringQualityResult>();
    private readonly Dictionary<A4Pair,string> a7ConfidenceGateReasons=new Dictionary<A4Pair,string>();
    private readonly Dictionary<A4Pair,string> a7StateBeforeConfidence=new Dictionary<A4Pair,string>();
    private readonly HashSet<int> a7SelectedBeforeConfidence=new HashSet<int>(),a7PublishedAfterConfidence=new HashSet<int>();
    private bool a7ConfidenceGateFinalized;

    private void A7ResetConfidenceState()
    {
        SeventhEngineeringQuality.ValidateMinimumPercent(SeventhMinimumLocalizationConfidencePercent);
        a7CausalDiagnosticSnapshot=null;a7EngineeringQualities.Clear();a7ConfidenceGateReasons.Clear();a7StateBeforeConfidence.Clear();
        a7SelectedBeforeConfidence.Clear();a7PublishedAfterConfidence.Clear();a7ConfidenceGateFinalized=false;
        a7CausalGraphJson="{\"version\":\"not_run\",\"complete\":false,\"confidence_gate_finalized\":false}";
    }
    private void A7CaptureCausalDiagnostic(A4Context c,IList<A4Pair> all,SeventhGraphNode[] nodes,SeventhGraphEdge[] edges,
        Dictionary<A4Pair,SeventhProposalEvidence> evidence,Dictionary<A4Pair,string> reasons,SeventhGraphResult graph)
    {
        a7CausalDiagnosticSnapshot=new A7CausalDiagnosticSnapshot{Context=c,All=all,Nodes=nodes,Edges=edges,Evidence=evidence,
            Reasons=reasons,SolverReasons=new Dictionary<A4Pair,string>(reasons),Graph=graph};
    }
    private static SeventhEngineeringQualityResult A7Quality(SeventhProposalEvidence evidence)
    {
        bool passed=evidence!=null&&evidence.Passed&&evidence.Incident!=null&&evidence.Reflection!=null&&
            evidence.Incident.Complete&&evidence.Reflection.Complete;
        return SeventhEngineeringQuality.Evaluate(passed,evidence==null?double.NaN:evidence.CoreCorrelation,
            evidence==null?double.NaN:evidence.LongCorrelation,evidence==null?double.NaN:evidence.RawCorrelation,
            evidence==null||evidence.Incident==null?double.NaN:evidence.Incident.Snr,
            evidence==null||evidence.Reflection==null?double.NaN:evidence.Reflection.Snr);
    }
    private void A7ApplyFinalConfidenceGate(A4Context c,IList<A4Pair> all,List<A4Pair> output,double floor)
    {
        if(a7ConfidenceGateFinalized)throw new InvalidOperationException("Method 7 confidence gate must run once per physical analysis.");
        SeventhEngineeringQuality.ValidateMinimumPercent(SeventhMinimumLocalizationConfidencePercent);
        var snapshot=a7CausalDiagnosticSnapshot;
        if(snapshot==null||snapshot.Context!=c||!Object.ReferenceEquals(snapshot.All,all))
            throw new InvalidOperationException("Method 7 confidence gate lost the current complete physical graph context.");
        foreach(var pair in output)a7SelectedBeforeConfidence.Add(pair.EdgeId);
        foreach(var pair in all)
        {
            a7StateBeforeConfidence[pair]=pair.State;
            SeventhProposalEvidence evidence;
            if(a7SelectedBeforeConfidence.Contains(pair.EdgeId))
                evidence=A7MeasuredProposal(c,pair.OutputIncident,pair.OutputReflection,floor);
            else if(!snapshot.Evidence.TryGetValue(pair,out evidence))evidence=null;
            var quality=A7Quality(evidence);a7EngineeringQualities[pair]=quality;
            if(!a7SelectedBeforeConfidence.Contains(pair.EdgeId))
            {
                bool physicalVeto=snapshot.Graph.Selected.Contains(pair.EdgeId);
                a7ConfidenceGateReasons[pair]=physicalVeto?"withheld_by_final_physical_checks_before_confidence_gate":
                    "not_selected_by_physical_graph_quality_does_not_promote_candidate";
                if(physicalVeto)
                {
                    pair.State="physical_safety_withheld";
                    snapshot.Reasons[pair]=a7ConfidenceGateReasons[pair];
                    if(pair.Incident.Owner==pair.EdgeId)pair.Incident.Owner=-1;
                    if(pair.Reflection.Owner==pair.EdgeId)pair.Reflection.Owner=-1;
                }
                continue;
            }
            bool passed=SeventhEngineeringQuality.PassesMinimum(quality,SeventhMinimumLocalizationConfidencePercent);
            a7ConfidenceGateReasons[pair]=SeventhMinimumLocalizationConfidencePercent==0?"confidence_gate_disabled_physical_review_identity_unchanged":
                !quality.Available?"confidence_withheld_engineering_quality_unavailable":
                passed?"engineering_quality_meets_publish_threshold_original_review_identity_unchanged":
                    "confidence_withheld_engineering_quality_below_publish_threshold";
            if(passed){a7PublishedAfterConfidence.Add(pair.EdgeId);continue;}
            output.Remove(pair);a7IndependentSeedPairs.Remove(pair);
            pair.State="confidence_withheld";
            pair.ReviewReason+=(String.IsNullOrEmpty(pair.ReviewReason)?"":";")+a7ConfidenceGateReasons[pair];
            snapshot.Reasons[pair]=pair.ReviewReason;
            if(pair.Incident.Owner==pair.EdgeId)pair.Incident.Owner=-1;
            if(pair.Reflection.Owner==pair.EdgeId)pair.Reflection.Owner=-1;
            c.AnalysisExclusions[pair.Incident.Id]=a7ConfidenceGateReasons[pair];
            // No Confidence/C33 mutation, no re-ranking, no replacement pair,
            // no graph mutation, no promotion, and no automatic training label.
        }
        a7ConfidenceGateFinalized=true;
        a7CausalGraphJson=A7CausalGraphDiagnostic(c,all,snapshot.Nodes,snapshot.Edges,snapshot.Evidence,snapshot.Reasons,snapshot.Graph);
    }
    private string A7EngineeringQualityJson(A4Pair pair)
    {
        SeventhEngineeringQualityResult quality;string gateReason,oldState;
        if(!a7EngineeringQualities.TryGetValue(pair,out quality))quality=A7Quality(null);
        if(!a7ConfidenceGateReasons.TryGetValue(pair,out gateReason))gateReason="confidence_gate_not_finalized";
        if(!a7StateBeforeConfidence.TryGetValue(pair,out oldState))oldState=pair.State;
        bool passes=SeventhEngineeringQuality.PassesMinimum(quality,SeventhMinimumLocalizationConfidencePercent);
        return "{\"version\":"+Json(SeventhEngineeringQuality.Version)+",\"available\":"+(quality.Available?"true":"false")+
            ",\"percent\":"+NumberJson(quality.Percent)+",\"is_correctness_probability\":false,\"formula\":"+Json(SeventhEngineeringQuality.Formula)+
            ",\"core_band_correlation\":"+NumberJson(quality.CoreCorrelation)+",\"long_band_correlation\":"+NumberJson(quality.LongCorrelation)+
            ",\"raw_main_correlation\":"+NumberJson(quality.RawCorrelation)+",\"incident_snr\":"+NumberJson(quality.IncidentSnr)+
            ",\"reflection_snr\":"+NumberJson(quality.ReflectionSnr)+",\"noise_quality_component\":"+NumberJson(quality.NoiseQuality)+
            ",\"weakest_component\":"+Json(quality.WeakestComponent)+",\"quality_reason\":"+Json(quality.Reason)+
            ",\"threshold_enabled\":"+(SeventhMinimumLocalizationConfidencePercent>0?"true":"false")+
            ",\"minimum_percent\":"+NumberJson(SeventhMinimumLocalizationConfidencePercent)+",\"threshold_passed\":"+(passes?"true":"false")+
            ",\"final_physical_selected_before_confidence\":"+(a7SelectedBeforeConfidence.Contains(pair.EdgeId)?"true":"false")+
            ",\"published_primary\":"+(a7PublishedAfterConfidence.Contains(pair.EdgeId)?"true":"false")+
            ",\"state_before_confidence\":"+Json(oldState)+",\"gate_reason\":"+Json(gateReason)+",\"legacy_confidence_unchanged\":true}";
    }
    private string A7ConfidenceGateSummaryJson()
    {
        return "{\"version\":"+Json(SeventhEngineeringQuality.Version)+",\"formula\":"+Json(SeventhEngineeringQuality.Formula)+
            ",\"minimum_percent\":"+NumberJson(SeventhMinimumLocalizationConfidencePercent)+",\"enabled\":"+(SeventhMinimumLocalizationConfidencePercent>0?"true":"false")+
            ",\"finalized\":"+(a7ConfidenceGateFinalized?"true":"false")+",\"is_correctness_probability\":false,\"legacy_C33_unchanged\":true,"+
            "\"applies_to_current_analysis_only\":true,\"historical_input_rows_rethresholded\":false,"+
            "\"policy\":\"post_complete_graph_and_final_physical_checks_publish_gate_only_no_rerank_no_replacement_no_review_promotion\","+
            "\"final_physical_selected_count\":"+a7SelectedBeforeConfidence.Count+",\"published_primary_count\":"+a7PublishedAfterConfidence.Count+
            ",\"confidence_withheld_count\":"+(a7SelectedBeforeConfidence.Count-a7PublishedAfterConfidence.Count)+
            ",\"empty_result_means\":\"no_primary_pair_passing_current_publish_gate_not_no_discharge\"}";
    }
    private string A7ConfidenceWithheldPacketLinksJson(IList<A4Pair> all)
    {
        // Retained unpaired rows have their own acquisition/inventory reasons.
        // This explicit association is the authoritative publish-gate record;
        // it never assigns a valid location to those unpaired rows.
        return "["+String.Join(",",all.Where(p=>a7SelectedBeforeConfidence.Contains(p.EdgeId)&&!a7PublishedAfterConfidence.Contains(p.EdgeId)).Select(p=>
            "{\"candidate_id\":"+p.EdgeId+",\"incident_detection_id\":"+p.Incident.Id+",\"reflection_detection_id\":"+p.Reflection.Id+
            ",\"incident_peak\":"+p.OutputIncident+",\"reflection_peak\":"+p.OutputReflection+
            ",\"state\":\"confidence_withheld\",\"reason\":"+Json(a7ConfidenceGateReasons[p])+"}"))+"]";
    }
}
