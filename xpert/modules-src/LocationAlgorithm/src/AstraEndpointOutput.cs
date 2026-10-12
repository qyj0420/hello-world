using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    private readonly HashSet<int> endpointPreclassifiedRows=new HashSet<int>();
    // Final current-call projection only. Physical validators, equivalents and
    // acceptance have finished; explicit caller history has not yet been added.
    // Do not write through the pair, measurement caches or raw identity maps.
    internal void CompleteEndpointPreclassification(ref double[,] parameters)
    {
        endpointPreclassifiedRows.Clear();
        var c=equivalentContext;
        if(c==null||(c.Algorithm!=4&&c.Algorithm!=5))return;
        string originalStatus=StatusJson;
        // A malformed upstream status must not turn an auxiliary projection
        // into a fatal LOC error. Leave all matrices and status untouched.
        if(String.IsNullOrEmpty(originalStatus)||!originalStatus.TrimEnd().EndsWith("}",StringComparison.Ordinal))return;
        int statusEnd=originalStatus.LastIndexOf('}');
        string statusPrefix=originalStatus.Substring(0,statusEnd);
        string statusSeparator=statusPrefix.TrimEnd().EndsWith("{",StringComparison.Ordinal)?"":",";
        if(ConnectingCableLengthM>0)
        {
            StatusJson=LastResultJson=statusPrefix+statusSeparator+"\"endpoint_preclassification\":{\"enabled\":false,"+
                "\"applied_to_current_matrix\":false,\"reason\":\"connecting_cable_endpoint_role_not_validated\","+
                "\"acceptance_changed\":false,\"history_reclassified\":false,\"rows\":[]}}";
            return;
        }
        string payload;
        try
        {
            int count=parameters.GetLength(0);
            var pairs=new A4Pair[count];
            if(c.Algorithm==4)
            {
                if(canonicalMixedRows.Length!=count)throw new InvalidOperationException("Endpoint current index4 row binding unavailable.");
                for(int r=0;r<count;r++)pairs[r]=A4CurrentMixedPair(parameters,r);
            }
            else
            {
                if(a6CurrentOutputPulses.Length!=count)throw new InvalidOperationException("Endpoint current method6 row binding unavailable.");
                for(int r=0;r<count;r++)pairs[r]=a6CurrentOutputPulses[r].Pair;
            }
            var current=pairs.Where(p=>p!=null).Distinct().ToList();
            var packets=c.Algorithm==4?widthDiagnosticPackets:
                a6CurrentOutputPulses.Select(q=>q.Pulse).Concat(current.SelectMany(p=>new[]{p.Incident,p.Reflection})).Distinct().ToList();
            var result=c.Algorithm==4?A4PreclassifyEndpoints(c,current,packets,c.CandidatePool):A6PreclassifyEndpoints(c,current,packets);
            var decisions=result.Decisions.ToDictionary(d=>d.Source);
            var next=(double[,])parameters.Clone();var ledger=new List<string>();
            int positionChanges=0,scoreChanges=0;
            for(int r=0;r<count;r++)
            {
                var pair=pairs[r];
                if(pair==null)continue; // Genuine unpaired rows never acquire a position or reflection.
                A4EndpointPredecision d;decisions.TryGetValue(pair,out d);
                double oldPosition=parameters[r,13],oldScore=parameters[r,33],oldQ=parameters[r,16],oldFactor=parameters[r,17];
                bool usable=result.Complete&&d!=null&&d.InventoryComplete;
                bool reinterpreted=false;
                string action="unchanged";
                if(usable)
                {
                    if(d.PositionProposed&&Finite(d.NewPosition)&&d.NewPosition>=0&&d.NewPosition<=c.Length&&Finite(oldPosition))
                    {
                        double charge=c.Attenuation&&c.Calibration!=null?
                            A4ContextCompensateCharge(c,parameters[r,15],d.NewPosition):parameters[r,15];
                        double factor=parameters[r,15]>0?charge/parameters[r,15]:1;
                        // Reject the projection atomically for this row if its
                        // compensation is not representable. Never publish a
                        // new position with the old position's compensation.
                        if(Finite(charge)&&charge>=0&&Finite(factor)&&factor>=0)
                        {next[r,13]=d.NewPosition;next[r,16]=charge;next[r,17]=factor;reinterpreted=true;endpointPreclassifiedRows.Add(r);action="endpoint_preliminary_position";}
                        else action="position_proposal_not_applied_invalid_compensation";
                    }
                    // The actual output score can be lower than pair.Confidence
                    // after project review. Do not raise a capped review row.
                    if(Finite(oldScore)&&oldScore>=0&&oldScore<=1)
                    {
                        if(d.RegionHigh&&d.PhysicalPassed&&oldScore>=.70)
                        {next[r,33]=Math.Max(oldScore,.90);action+=";endpoint_region_score";}
                        else if(!d.RegionHigh&&Finite(d.ScoreFactor)&&d.ScoreFactor>=.5&&d.ScoreFactor<1)
                        {next[r,33]=oldScore*d.ScoreFactor;action+=";body_ambiguity_score";}
                    }
                }
                bool pc=next[r,13]!=oldPosition,sc=next[r,33]!=oldScore;
                if(pc)positionChanges++;if(sc)scoreChanges++;
                ledger.Add("{\"row\":"+r+",\"canonical_raw_incident\":"+A4ChargeIndex(pair.Incident)+
                    ",\"canonical_raw_reflection\":"+A4ChargeIndex(pair.Reflection)+",\"original_position\":"+NumberJson(oldPosition)+
                    ",\"original_score\":"+NumberJson(oldScore)+",\"original_compensated_charge\":"+NumberJson(oldQ)+
                    ",\"original_compensation_factor\":"+NumberJson(oldFactor)+",\"new_position\":"+NumberJson(next[r,13])+
                    ",\"new_score\":"+NumberJson(next[r,33])+",\"new_compensated_charge\":"+NumberJson(next[r,16])+
                    ",\"new_compensation_factor\":"+NumberJson(next[r,17])+",\"position_changed\":"+(pc?"true":"false")+
                    ",\"position_reinterpreted\":"+(reinterpreted?"true":"false")+
                    ",\"score_changed\":"+(sc?"true":"false")+",\"action\":"+Json(action)+",\"original_timing_samples\":["+
                    NumberJson(parameters[r,10])+","+NumberJson(parameters[r,11])+","+NumberJson(parameters[r,12])+
                    "],\"acceptance_unchanged\":true,\"decision_available\":"+(d!=null?"true":"false")+
                    ",\"decision\":"+(d==null?"null":A4PredecisionJson(d))+"}");
            }
            payload="{\"enabled\":true,\"applied_to_current_matrix\":true,\"complete\":"+(result.Complete?"true":"false")+
                ",\"reason\":"+Json(result.Reason)+",\"experimental_not_confirmed\":true,\"score_is_probability\":false,"+
                "\"score_meaning\":\"endpoint_region_or_downgraded_body_engineering_score_not_direction_confidence\","+
                "\"position_basis\":\"preliminary_endpoint_override_when_changed_else_original_timing_geometry\","+
                "\"original_timing_reinterpreted\":false,\"acceptance_changed\":false,\"history_reclassified\":false,"+
                "\"row_space\":\"final_current_output_before_explicit_history_append\",\"allowed_changed_columns_zero_based\":[13,16,17,33],"+
                "\"position_changed_rows\":"+positionChanges+",\"score_changed_rows\":"+scoreChanges+
                ",\"compensation_rule\":\"recompute_only_applied_position_proposal_using_selected_CAL_loss_and_clamp(position,0,L); even_if_display_already_equals_endpoint; raw_charge_unchanged\","+
                "\"source_pair_decisions\":"+result.DiagnosticJson+",\"rows\":["+String.Join(",",ledger)+"]}";
            // Prepare all derived output copies before committing this stage.
            var flat=Flatten(next);var positions=Enumerable.Range(0,count).Select(r=>next[r,13]).ToArray();
            string nextStatus=statusPrefix+statusSeparator+"\"endpoint_preclassification\":"+payload+"}";
            parameters=next;ParameterMatrix2D=next;ParameterMatrix=flat;ParameterRows=count;ParameterColumns=36;
            ValidatedRuntimePositionM=positions;
            StatusJson=LastResultJson=nextStatus;
            return;
        }
        catch(Exception ex)
        {
            endpointPreclassifiedRows.Clear();
            // Projection failure preserves complete original matrices instead
            // of clearing a valid LOC or publishing a partially written result.
            payload="{\"enabled\":true,\"applied_to_current_matrix\":false,\"complete\":false,"+
                "\"reason\":"+Json("endpoint_output_projection_unavailable:"+ex.GetType().Name)+
                ",\"acceptance_changed\":false,\"history_reclassified\":false,\"rows\":[]}";
        }
        StatusJson=statusPrefix+statusSeparator+"\"endpoint_preclassification\":"+payload+"}";
        LastResultJson=StatusJson;
    }
}
