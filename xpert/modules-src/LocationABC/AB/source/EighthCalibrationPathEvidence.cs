using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;

namespace HVX.Task17.Location
{
    // Evidence-only adapter. Neither the known discharge positions nor the
    // desired cluster count enters this module. It NEVER changes pair acceptance,
    // coordinates, charge, packet ownership, or the measured CAL waveform.
    internal static class EighthCalibrationPathEvidence
    {
        const int MaximumRelations=20000,MaximumWork=2000000;
        static bool Finite(double x){return !Double.IsNaN(x)&&!Double.IsInfinity(x);}
        static object N(double x){return Finite(x)?(object)x:null;}
        static Dictionary<string,object> D(params object[] x){var d=new Dictionary<string,object>();for(int i=0;i<x.Length;i+=2)d[(string)x[i]]=x[i+1];return d;}
        static string Serialize(object x){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=32}.Serialize(x);}
        static bool PacketUsable(EighthPulse p,int recordLength){return p!=null&&p.Valid&&p.Start>=0&&p.Start<=p.Peak&&p.Peak<=p.End&&p.End<recordLength&&Finite(p.SampleRateHz)&&p.SampleRateHz>0&&Finite(p.AbsAmplitude)&&p.AbsAmplitude>0&&Finite(p.Noise)&&p.Noise>0&&p.AbsAmplitude>=6*p.Noise&&Finite(p.TimingUncertaintySeconds)&&p.TimingUncertaintySeconds>=0;}
        // Canonical native-packet allowance, NOT a validated peak error bound.
        // The producer derives this legacy field from noise / onset-edge slope.
        // It is mode invariant; OnsetTimingUncertaintySeconds and pair.TU are not.
        // Match the actual delay endpoints. In particular I->late must not borrow
        // the early packet's allowance. No extra CAL term or fitted slack is added.
        static bool NativeRangePacket(EighthPulse p,double fs)
        {return p!=null&&p.Valid&&p.Start>=0&&p.Start<=p.Peak&&p.Peak<=p.End&&
            Finite(p.SampleRateHz)&&p.SampleRateHz==fs&&Finite(p.TimingUncertaintySeconds)&&p.TimingUncertaintySeconds>=0;}
        static bool NativeRangeAllowance(EighthPulse a,EighthPulse b,double fs,out double allowance)
        {
            allowance=0;
            if(!NativeRangePacket(a,fs)||!NativeRangePacket(b,fs))return false;
            allowance=a.TimingUncertaintySeconds+b.TimingUncertaintySeconds;
            return Finite(allowance)&&allowance>=0;
        }
        sealed class JointSupport {internal EighthPulse Packet;internal int Node;internal double M,TwoWay,Residual,Uncertainty;}
        // Explicit process-local reference identity; never merge separate observations
        // because their peak, support or samples happen to have equal values.
        sealed class ReferenceIdentity<T>:IEqualityComparer<T> where T:class
        {
            public bool Equals(T a,T b){return Object.ReferenceEquals(a,b);}
            public int GetHashCode(T value){return RuntimeHelpers.GetHashCode(value);}
        }

        static Dictionary<string,object> Result()
        {
            return D("schema","eighth-CAL-path-evidence-v1","timing_basis","native_peak_not_first_arrival",
                "diagnostic_only",true,"acceptance_or_coordinates_changed",false,"known_discharge_positions_used",false,
                "all_paths_identified",false,"joint_role_labels_are_hypotheses",true,
                "joint_local_paths_modelled_for_acceptance",false,
                "LOC_range_gate_uncertainty_basis","same_delay_endpoint_native_packet_legacy_onset_slope_allowance",
                "LOC_range_gate_independent_of_requested_mode_pair_TU",true,
                "LOC_range_allowance_validated_peak_error_bound",false,
                "LOC_scope","published_pairs_and_observed_later_packets_not_all_candidate_graph_edges");
        }

        // Bind the diagnostic clock BEFORE the requested LOC mode solves or clips geometry.
        // Explicit mixed speeds stay per segment. The shared unknown speed is an explicit
        // modelling assumption, not an inferred unique composition of unknown materials.
        internal static string DescribeFromCable(EighthCalibration cal,double[] deviceEdges,
            double[] metadataVelocities,bool[] explicitVelocityMask,double leadMetres,
            double locFs,EighthAnalysis analysis)
        {
            var result=Result();
            result["geometry_clock_basis"]="native_peak_CAL";
            result["geometry_supported"]=false;
            result["geometry_independent_of_requested_LOC_mode"]=true;
            Func<string,string> Unsupported=reason=>{
                result["complete"]=false;result["reason"]=reason;return Serialize(result);};
            if(cal==null||!cal.Valid||!Finite(cal.RoundTripSeconds)||cal.RoundTripSeconds<=0||
                !Finite(cal.SampleRateHz)||cal.SampleRateHz<=0)
                return Unsupported("CAL_native_peak_clock_unusable");
            if(deviceEdges==null||metadataVelocities==null||explicitVelocityMask==null||
                metadataVelocities.Length<1||deviceEdges.Length!=metadataVelocities.Length+1||
                explicitVelocityMask.Length!=metadataVelocities.Length||!Finite(leadMetres)||leadMetres<0)
                return Unsupported("invalid_peak_CAL_geometry_metadata");
            // This is a separate diagnostic-model bound, not the measurement packet cap.
            if(metadataVelocities.Length>4096)
                return Unsupported("CAL_geometry_segment_budget_exceeded_no_partial_path_claim");
            if(deviceEdges[0]!=0||!Finite(deviceEdges[deviceEdges.Length-1])||
                deviceEdges[deviceEdges.Length-1]<=leadMetres)
                return Unsupported("invalid_peak_CAL_geometry_metadata");
            for(int k=0;k<metadataVelocities.Length;k++)
                if(!Finite(deviceEdges[k])||!Finite(deviceEdges[k+1])||deviceEdges[k+1]<=deviceEdges[k]||
                    (explicitVelocityMask[k]&&(!Finite(metadataVelocities[k])||metadataVelocities[k]<50||metadataVelocities[k]>300)))
                    return Unsupported("invalid_peak_CAL_geometry_metadata");
            double[] edges=(double[])deviceEdges.Clone(),velocities=(double[])metadataVelocities.Clone();
            double leadSeconds=0,tolerance=2/cal.SampleRateHz;
            bool commonUnknown=false;
            if(!Finite(tolerance)||tolerance<0)return Unsupported("CAL_native_peak_clock_unusable");
            if(leadMetres>0)
            {
                double peakUs=cal.RoundTripSeconds*1e6,toleranceUs=2e6/cal.SampleRateHz;
                if(!Finite(peakUs)||!Finite(toleranceUs))return Unsupported("peak_CAL_lead_model_unsupported");
                HVX.Geometry.ConnectingCableGeometryResult geometry;
                try
                {
                    geometry=HVX.Geometry.ConnectingCableGeometry.Build(deviceEdges[deviceEdges.Length-1],leadMetres,
                        true,peakUs,deviceEdges,metadataVelocities,explicitVelocityMask,toleranceUs);
                }
                catch(ArgumentException){return Unsupported("peak_CAL_lead_model_unsupported");}
                if(!geometry.CanUseCalibrationGeometry)return Unsupported("peak_CAL_geometry_conflict");
                edges=geometry.DutJointsM;velocities=geometry.DutSegmentVelocitiesMPerUs;
                // Match the facade's native peak lead arithmetic, without a LOC clock input.
                leadSeconds=2*leadMetres/HVX.Geometry.ConnectingCableGeometry.ConnectingCableVelocityMPerUs*1e-6;
                commonUnknown=geometry.UsedCalibrationForUnknownVelocity;
            }
            else
            {
                double knownTime=0,unknownLength=0;
                for(int k=0;k<velocities.Length;k++)
                    if(explicitVelocityMask[k])knownTime+=(edges[k+1]-edges[k])/velocities[k]*1e-6;
                    else unknownLength+=edges[k+1]-edges[k];
                if(!Finite(knownTime)||!Finite(unknownLength))return Unsupported("peak_CAL_geometry_arithmetic_unsupported");
                if(unknownLength>0)
                {
                    double remaining=cal.RoundTripSeconds/2-knownTime;
                    double speed=unknownLength/remaining*1e-6;
                    if(!Finite(remaining)||remaining<=0||!Finite(speed)||speed<50||speed>300)
                        return Unsupported("peak_CAL_unknown_velocity_unsupported");
                    for(int k=0;k<velocities.Length;k++)if(!explicitVelocityMask[k])velocities[k]=speed;
                    commonUnknown=true;
                }
                else if(!Finite(2*knownTime)||Math.Abs(2*knownTime-cal.RoundTripSeconds)>tolerance+1e-15)
                    return Unsupported("peak_CAL_geometry_conflict");
            }
            if(!Finite(leadSeconds)||velocities.Any(v=>!Finite(v)||v<50||v>300))
                return Unsupported("peak_CAL_geometry_arithmetic_unsupported");
            double dutSeconds=0;
            for(int k=0;k<velocities.Length;k++)dutSeconds+=2*(edges[k+1]-edges[k])/velocities[k]*1e-6;
            if(!Finite(dutSeconds)||dutSeconds<=0||!Finite(dutSeconds+leadSeconds))
                return Unsupported("peak_CAL_geometry_arithmetic_unsupported");
            result["geometry_supported"]=true;
            result["common_unknown_velocity_assumption"]=commonUnknown;
            result["geometry_model"]=commonUnknown?"explicit_segments_plus_common_unknown_speed_from_native_peak_CAL":"explicit_segment_travel_times";
            result["geometry_peak_CAL_round_trip_seconds"]=cal.RoundTripSeconds;
            result["geometry_CAL_sample_rate_hz"]=cal.SampleRateHz;
            result["geometry_lead_round_trip_seconds"]=leadSeconds;
            result["geometry_DUT_round_trip_seconds"]=dutSeconds;
            result["geometry_DUT_edges_m"]=edges;
            result["geometry_segment_velocities_m_per_us"]=velocities;
            result["geometry_timing_tolerance_seconds"]=tolerance;
            return DescribeCore(cal,edges,velocities,leadSeconds,locFs,analysis,result);
        }

        // Legacy explicit-geometry entry remains for callers that already bind a peak model.
        internal static string Describe(EighthCalibration cal,double[] edges,double[] velocities,double leadSeconds,
            double locFs,EighthAnalysis analysis)
        {return DescribeCore(cal,edges,velocities,leadSeconds,locFs,analysis,Result());}

        static string DescribeCore(EighthCalibration cal,double[] edges,double[] velocities,double leadSeconds,
            double locFs,EighthAnalysis analysis,Dictionary<string,object> result)
        {
            Action<string> Fail=reason=>{result["complete"]=false;result["reason"]=reason;};
            if(cal==null||!cal.Valid||cal.Incident==null||cal.Reflection==null||!Finite(cal.SampleRateHz)||cal.SampleRateHz<=0||
                !Finite(cal.RoundTripSeconds)||cal.RoundTripSeconds<=0||
                !Finite(cal.TimingUncertaintySeconds)||cal.TimingUncertaintySeconds<0||
                !Finite(cal.MeasurementTimingUncertaintySeconds)||cal.MeasurementTimingUncertaintySeconds<0||
                !Finite(leadSeconds)||leadSeconds<0||
                !Finite(locFs)||locFs<=0||cal.RecordSampleCount<1||cal.ObservedPackets==null||
                !PacketUsable(cal.Incident,cal.RecordSampleCount)||!PacketUsable(cal.Reflection,cal.RecordSampleCount))
            {Fail("CAL_or_native_clock_unusable");return Serialize(result);}
            if(edges==null||velocities==null||edges.Length!=velocities.Length+1||velocities.Length<1||
                edges[0]!=0||edges.Any(x=>!Finite(x))||velocities.Any(x=>!Finite(x)||x<50||x>300))
            {Fail("invalid_current_phase_geometry");return Serialize(result);}
            double total=0;var twoWay=new double[edges.Length];
            for(int i=0;i<velocities.Length;i++){
                if(edges[i+1]<=edges[i]){Fail("unordered_current_phase_geometry");return Serialize(result);}
                total+=2*(edges[i+1]-edges[i])/velocities[i]*1e-6;twoWay[i+1]=total;
            }
            if(!Finite(total)||Math.Abs(total+leadSeconds-cal.RoundTripSeconds)>
                2/cal.SampleRateHz+Math.Max(0,cal.TimingUncertaintySeconds))
            {Fail("geometry_not_consistent_with_peak_CAL_clock");return Serialize(result);}
            if(cal.ObservedPackets.Count>4096){Fail("CAL_packet_budget_exceeded_no_partial_path_claim");return Serialize(result);}
            if(cal.ObservedPackets.Any(p=>p==null)){Fail("missing_CAL_packet");return Serialize(result);}
            if(cal.ObservedPackets.GroupBy(p=>p.Peak).Any(g=>g.Count()>1))
            {Fail("duplicate_original_CAL_packet_not_independent_evidence");return Serialize(result);}
            var observed=cal.ObservedPackets.OrderBy(p=>p.Peak).ToArray();
            var packetRows=new List<object>();
            foreach(var p in observed){
                if(p==null){Fail("missing_CAL_packet");return Serialize(result);}
                double corr=EighthPulseMath.ShapeCorrelation(cal.Incident,p);
                packetRows.Add(D("peak",p.Peak,"start",p.Start,"end",p.End,"valid_support",PacketUsable(p,cal.RecordSampleCount),
                    "delay_from_CAL_incident_seconds",N((p.Peak-cal.Incident.Peak)/cal.SampleRateHz),
                    "amplitude_ratio_to_CAL_incident",N(p.AbsAmplitude/cal.Incident.AbsAmplitude),
                    "signed_shape_correlation_to_CAL_incident",N(corr),"onset_identifiable",EighthPulseMath.OnsetUsable(p),
                    "selected_marker_role",Object.ReferenceEquals(p,cal.Incident)?"incident_marker":Object.ReferenceEquals(p,cal.Reflection)?"terminal_marker_candidate":"unassigned"));
            }
            var nodes=new List<object>();var supports=new List<JointSupport>();int work=0;
            for(int j=1;j<edges.Length-1;j++)foreach(var p in observed){
                if(++work>MaximumWork){Fail("CAL_geometry_budget_exceeded");return Serialize(result);}
                if(!PacketUsable(p,cal.RecordSampleCount)||p.Peak<=cal.Incident.Peak||p.Peak>=cal.Reflection.Peak)continue;
                double prediction=leadSeconds+twoWay[j],actual=(p.Peak-cal.Incident.Peak)/cal.SampleRateHz;
                double u=p.TimingUncertaintySeconds+cal.Incident.TimingUncertaintySeconds+
                    prediction/(total+leadSeconds)*cal.MeasurementTimingUncertaintySeconds;
                if(!Finite(u)||u<0)continue;
                if(Math.Abs(actual-prediction)>u+1e-15)continue;
                var support=new JointSupport{Packet=p,Node=j,M=edges[j],TwoWay=twoWay[j],Residual=actual-prediction,Uncertainty=u};supports.Add(support);
                nodes.Add(D("joint_index",j,"joint_m_DUT",edges[j],"CAL_peak",p.Peak,
                    "predicted_CAL_delay_seconds",prediction,"observed_CAL_delay_seconds",actual,
                    "residual_seconds",actual-prediction,"measurement_allowance_seconds",u,
                    "identity_confirmed",false,"basis","CABLE_geometry_plus_observed_CAL_packet_not_unique_path_proof"));
            }
            result["CAL_packets"]=packetRows;result["CAL_joint_hypotheses"]=nodes;
            result["CAL_packets_count"]=packetRows.Count;result["CAL_joint_hypothesis_count"]=nodes.Count;
            result["LOC_reference_identity_deduplication"]="reference_equality_only_distinct_objects_preserved";
            result["LOC_reference_indices_scope"]="first_input_list_occurrence_this_call_not_stable_or_physical_source_ID";
            result["LOC_collection_validation_complete"]=false;result["LOC_reference_counts_complete"]=false;
            result["LOC_pair_input_entries"]=analysis==null||analysis.Pairs==null?null:(object)analysis.Pairs.Count;
            result["LOC_pulse_input_entries"]=analysis==null||analysis.Pulses==null?null:(object)analysis.Pulses.Count;
            result["LOC_unique_pair_references"]=null;result["LOC_unique_pulse_references"]=null;
            result["LOC_unique_valid_pulse_references"]=null;
            result["LOC_duplicate_pair_reference_count"]=null;result["LOC_duplicate_pulse_reference_count"]=null;
            Action<string> FailCollection=reason=>{
                Fail(reason);result["LOC_role_hypotheses"]=new List<object>();
                result["LOC_role_hypothesis_count"]=0;result["work_count"]=work;
            };
            if(analysis==null){FailCollection("LOC_analysis_missing_no_partial_path_claim");return Serialize(result);}
            if(analysis.Pairs==null||analysis.Pulses==null)
            {FailCollection("LOC_collection_missing_no_partial_path_claim");return Serialize(result);}
            // Charge the original entries, not only the surviving references. If
            // this count alone exceeds remaining work, no complete null/identity
            // validation is claimed and no input-sized copies are allocated.
            long collectionWork=(long)analysis.Pairs.Count+analysis.Pulses.Count;
            result["LOC_collection_input_work_required"]=collectionWork;
            if(collectionWork>MaximumWork-work)
            {FailCollection("LOC_collection_budget_exceeded_hypotheses_withheld");return Serialize(result);}
            var pairIndices=new Dictionary<EighthPair,int>(new ReferenceIdentity<EighthPair>());
            var pulseIndices=new Dictionary<EighthPulse,int>(new ReferenceIdentity<EighthPulse>());
            var pairs=new List<EighthPair>();var uniquePulses=new List<EighthPulse>();
            int duplicatePairs=0,duplicatePulses=0;
            for(int k=0;k<analysis.Pairs.Count;k++){
                work++;var pair=analysis.Pairs[k];
                if(pair==null||pair.Incident==null||pair.Reflection==null)
                {FailCollection("LOC_collection_contains_missing_evidence_no_partial_path_claim");return Serialize(result);}
                if(pairIndices.ContainsKey(pair)){duplicatePairs++;continue;}
                pairIndices.Add(pair,k);pairs.Add(pair);
            }
            for(int k=0;k<analysis.Pulses.Count;k++){
                work++;var pulse=analysis.Pulses[k];
                if(pulse==null)
                {FailCollection("LOC_collection_contains_missing_evidence_no_partial_path_claim");return Serialize(result);}
                if(pulseIndices.ContainsKey(pulse)){duplicatePulses++;continue;}
                pulseIndices.Add(pulse,k);uniquePulses.Add(pulse);
            }
            var pulses=uniquePulses.Where(p=>p.Valid).OrderBy(p=>p.Peak).ToArray();
            result["LOC_collection_validation_complete"]=true;result["LOC_reference_counts_complete"]=true;
            result["LOC_unique_pair_references"]=pairs.Count;result["LOC_unique_pulse_references"]=uniquePulses.Count;
            result["LOC_unique_valid_pulse_references"]=pulses.Length;
            result["LOC_duplicate_pair_reference_count"]=duplicatePairs;result["LOC_duplicate_pulse_reference_count"]=duplicatePulses;
            var relations=new List<object>();
            bool budget=false,rangeUnsupported=false;
            foreach(var pair in pairs){
                double earlyAllowance;
                if(pair==null||!NativeRangeAllowance(pair.Incident,pair.Reflection,locFs,out earlyAllowance))
                {rangeUnsupported=true;break;}
                double delay=(pair.Reflection.Peak-pair.Incident.Peak)/locFs;
                if(!Finite(delay)){rangeUnsupported=true;break;}
                foreach(var s in supports){
                    // A candidate terminal pair may instead be I -> Rj. Search
                    // a separately observed later branch at the CAL Rj -> RL gap.
                    if(delay<=0||delay>s.TwoWay+earlyAllowance)continue;
                    double gap=(cal.Reflection.Peak-s.Packet.Peak)/cal.SampleRateHz;
                    foreach(var later in pulses){
                        if(++work>MaximumWork){budget=true;break;}
                        if(later.Start<=pair.Reflection.End)continue;
                        double lateAllowance;
                        if(!NativeRangeAllowance(pair.Incident,later,locFs,out lateAllowance))
                        {rangeUnsupported=true;break;}
                        double lateDelay=(later.Peak-pair.Incident.Peak)/locFs;
                        if(!Finite(lateDelay)){rangeUnsupported=true;break;}
                        // Sorted peaks do not sort peak-minus-allowance: a later
                        // packet may have a larger measured allowance. Never break
                        // on the first rejected packet's branch-specific bound.
                        if(lateDelay>total+lateAllowance)continue;
                        double observedGap=(later.Peak-pair.Reflection.Peak)/locFs;
                        double u=cal.Reflection.TimingUncertaintySeconds+s.Packet.TimingUncertaintySeconds+
                            pair.Reflection.TimingUncertaintySeconds+later.TimingUncertaintySeconds;
                        if(!Finite(u)||u<0||Math.Abs(observedGap-gap)>u+1e-15)continue;
                        relations.Add(D("kind","I_Rj_RL_competing_role","candidate_incident_peak",pair.Incident.Peak,
                            "LOC_pair_reference_index",pairIndices[pair],"LOC_late_reference_index",pulseIndices[later],
                            "candidate_early_peak",pair.Reflection.Peak,"candidate_late_peak",later.Peak,
                            "joint_m_DUT",s.M,"CAL_joint_peak",s.Packet.Peak,
                            "CAL_early_late_gap_seconds",gap,"LOC_early_late_gap_seconds",observedGap,
                            "gap_residual_seconds",observedGap-gap,"measurement_allowance_seconds",u,
                            "LOC_early_range_allowance_seconds",earlyAllowance,
                            "LOC_late_range_allowance_seconds",lateAllowance,
                            "I_to_late_signed_shape",N(EighthPulseMath.ShapeCorrelation(pair.Incident,later)),
                            "existing_pair_accepted",pair.Accepted,"independent_incident_or_terminal_roles_verified",false));
                        if(relations.Count>=MaximumRelations){budget=true;break;}
                    }
                    if(budget||rangeUnsupported)break;
                }
                if(budget||rangeUnsupported)break;
                // Both selected packets can be echoes: compare against two
                // DISTINCT CAL-supported joints, without assigning an ancestor.
                for(int i=0;i<supports.Count&&!budget;i++)for(int k=i+1;k<supports.Count&&!budget;k++){
                    if(++work>MaximumWork){budget=true;break;}
                    var a=supports[i];var b=supports[k];
                    if(a.Node==b.Node||a.Packet.Peak==b.Packet.Peak)continue;
                    double gap=Math.Abs(b.Packet.Peak-a.Packet.Peak)/cal.SampleRateHz;
                    double u=a.Packet.TimingUncertaintySeconds+b.Packet.TimingUncertaintySeconds+
                        pair.Incident.TimingUncertaintySeconds+pair.Reflection.TimingUncertaintySeconds;
                    if(!Finite(u)||u<0||Math.Abs(delay-gap)>u+1e-15)continue;
                    relations.Add(D("kind","Rj_Rk_pair_timing_alias","candidate_incident_peak",pair.Incident.Peak,
                        "LOC_pair_reference_index",pairIndices[pair],
                        "candidate_reflection_peak",pair.Reflection.Peak,"CAL_joint1_peak",a.Packet.Peak,"CAL_joint2_peak",b.Packet.Peak,
                        "joint1_m_DUT",a.M,"joint2_m_DUT",b.M,"gap_residual_seconds",delay-gap,"measurement_allowance_seconds",u,
                        "common_source_or_ancestor_verified",false,"existing_pair_accepted",pair.Accepted));
                    if(relations.Count>=MaximumRelations){budget=true;break;}
                }
                if(budget)break;
            }
            // Never expose the first portion of an incomplete search as complete.
            result["LOC_role_hypotheses"]=budget||rangeUnsupported?new List<object>():relations;
            result["LOC_role_hypothesis_count"]=budget||rangeUnsupported?0:relations.Count;result["work_count"]=work;
            result["complete"]=!budget&&!rangeUnsupported;result["reason"]=rangeUnsupported?
                "LOC_native_packet_range_allowance_unusable_no_partial_path_claim":budget?"LOC_path_budget_exceeded_hypotheses_withheld":
                "observed_CAL_paths_recorded_only_not_a_localization_verdict";
            result["unmodelled_or_unverified"]=new[]{"joint_scattering_direction_and_transmission","multi_bounce_path_identity","common_source_ancestry","missing_branch_visibility","independent_sources_coincidental_timing"};
            return Serialize(result);
        }
    }
}
