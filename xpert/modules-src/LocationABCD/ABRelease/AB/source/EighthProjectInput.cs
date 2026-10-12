using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HVX.Geometry;

namespace HVX.Task17.Location;

internal sealed class EighthCurrentOutput
{
    internal double[,] Parameters,Raw,Filtered,Equivalent;
    internal string Status;
}

// Method-8-only diagnostics carrier. The public history/matrix transaction is unchanged.
internal sealed class EighthResourceDiagnosticException:Exception
{
    internal readonly string DiagnosticJson;
    internal EighthResourceDiagnosticException(string message,string diagnostic,Exception inner):base(message,inner){DiagnosticJson=diagnostic;}
}

internal sealed partial class LocationEngine
{
    private const int EighthMaximumSamples=8388608;
    private const int EighthMaximumOutputWidth=1048576;
    private const long EighthMaximumMatrixCells=4194304;
    private const long EighthMaximumTextFileBytes=268435456;
    private sealed class EighthCalFile
    {
        internal ParsedCalibration Data;
        internal EighthCalibration Evidence;
        internal string Path,Phase,Reason;
        internal bool Valid;
    }

    // Static deliberately: method 8 does not instantiate frozen DAC/sixth/onset engines.
    internal static EighthCurrentOutput AnalyzeEighthCurrent(ProjectLocationAnalyzer owner,
        string projectPath,string locationFilePath,double locSampleHz,double[] locData,bool useLocData,
        long locationDataId,bool automaticAnalysis,bool firstArrival,bool attenuationRequested,bool widthRequested,
        int thresholdType,double manualThreshold,double thresholdMultiplier,int suppression,int maximumIncidents,
        int preRequested,int postRequested,double initialPhase,double requestedFrequency,bool displayFilter,
        double[] displayCutoffs,bool customEquivalent,double[] equivalentWindowNs)
    {
        EighthResourceReceipt kernelReceipt=null,calibrationReceipt=null;
        try
        {
        if(locationDataId<0||locationDataId>9007199254740991L)throw new ArgumentOutOfRangeException("locationDataId");
        if(thresholdType < -1||thresholdType>1)throw new ArgumentOutOfRangeException("thresholdType");
        if(suppression < -1)throw new ArgumentOutOfRangeException("multipleReflectionSuppressionCount");
        if(!Finite(initialPhase)||!Finite(requestedFrequency)||requestedFrequency<0)
            throw new ArgumentException("Phase and oscillation frequency must be finite; frequency cannot be negative.");
        if(!Finite(owner.ConnectingCableLengthM)||owner.ConnectingCableLengthM<0)
            throw new ArgumentOutOfRangeException("ConnectingCableLengthM");
        if(String.IsNullOrWhiteSpace(projectPath)&&String.IsNullOrWhiteSpace(locationFilePath))
            throw new ArgumentException("Provide projectPath or locationFilePath.");
        string root=String.IsNullOrWhiteSpace(projectPath)?Path.GetDirectoryName(Path.GetFullPath(locationFilePath)):Path.GetFullPath(projectPath);
        if(File.Exists(root))root=Path.GetDirectoryName(root);
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("Project directory is unavailable.");
        string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
        string item=A4Named(files,"ITEM.in",true),cablePath=A4Named(files,"CABLE.in",true),identity;
        if(String.IsNullOrWhiteSpace(locationFilePath))
        {
            string[] locs=files.Where(f=>String.Equals(Path.GetExtension(f),".loc",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(locs.Length!=1)throw new ArgumentException("Select one LOC record explicitly.");
            identity=locs[0];
        }
        else identity=Path.GetFullPath(Path.IsPathRooted(locationFilePath)?locationFilePath:Path.Combine(root,locationFilePath));
        if(!String.Equals(Path.GetExtension(identity),".loc",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("LOC identity must have a .loc extension.");
        ParsedLocation loc;string velocityInputRule="memory_input_no_LOC_velocity_override";
        if(useLocData)
        {
            if(locData==null||locData.Length<16||locData.Length>EighthMaximumSamples||locData.Any(v=>!Finite(v)))
                throw new ArgumentException("Memory LOC requires at least 16 finite engineering-value pC samples.");
            loc=new ParsedLocation {Path=identity,Samples=(double[])locData.Clone(),SampleRate=locSampleHz,
                CableLength=double.NaN,VelocityMPerUs=double.NaN,PhaseDeg=double.NaN};
            ParseMeasurementName(Path.GetFileNameWithoutExtension(identity),out loc.Phase,out loc.VoltageKv);
        }
        else
        {
            string path=RequireFile(identity,".loc","locationFilePath");
            A8ValidateTextFileSize(path);
            loc=A8ReadLocation(path,owner.Method8ConfirmedLegacyVelocityUnits,out velocityInputRule);
        }
        if(loc.Samples.Length>EighthMaximumSamples)throw new InvalidDataException("Method 8 raw sample budget exceeded.");
        if(String.IsNullOrEmpty(loc.Phase))throw new InvalidDataException("LOC phase identity is missing.");
        ReadProject(item);
        CableMetadata cable=ReadCable(cablePath,loc.Phase);
        double totalLength=FirstPositive(cable.CableLength,loc.CableLength);
        double headerVelocity=FirstPositive(cable.VelocityMPerUs,loc.VelocityMPerUs);
        string demPath=A4MatchedDem(files,identity,loc.Phase,loc.VoltageKv);
        ParsedDem dem=String.IsNullOrEmpty(demPath)?null:ReadDem(demPath);
        double fs=A8ResolveNativeSampleRate(locSampleHz,loc.SampleRate,useLocData);
        // No CAL/DEM sampling rate is substituted for an unlabelled LOC axis.
        if(!Finite(fs)||fs<=0)throw new InvalidDataException("A positive native LOC sampling rate (Hz) is required.");
        double expectedCalibrationRt=double.NaN;
        if(Finite(totalLength)&&totalLength>0&&Finite(headerVelocity)&&headerVelocity>0&&
            owner.ConnectingCableLengthM<totalLength)
        {
            double[] expectedJoints=EnsureCableEnds(cable.JointPositionsIncludingEnds,totalLength);
            double[] expectedSpeeds=A4SegmentVelocities(cable,expectedJoints,headerVelocity);
            double time=0;
            for(int k=0;k<expectedSpeeds.Length;k++)
            {
                double leadPart=Math.Max(0,Math.Min(expectedJoints[k+1],owner.ConnectingCableLengthM)-expectedJoints[k]);
                double dutPart=expectedJoints[k+1]-expectedJoints[k]-leadPart;
                time+=2*(leadPart/ConnectingCableGeometry.ConnectingCableVelocityMPerUs+dutPart/expectedSpeeds[k])*1e-6;
            }
            if(Finite(time)&&time>0)expectedCalibrationRt=time;
        }
        List<EighthCalFile> candidates;
        EighthCalFile selected=A8ResolveCalibration(files,loc.Phase,totalLength,expectedCalibrationRt,out candidates);
        EighthCalibration calibration=selected==null?null:selected.Evidence;
        calibrationReceipt=calibration==null?null:calibration.ResourceUsage;
        if(!Finite(totalLength)||totalLength<=0)
            totalLength=selected==null?double.NaN:selected.Data.CableLength;
        if(!Finite(headerVelocity)||headerVelocity<=0)
            headerVelocity=selected==null?double.NaN:selected.Data.VelocityMPerUs;
        if(!Finite(totalLength)||totalLength<=0)throw new InvalidDataException("Current-phase cable length is required.");
        double lead=owner.ConnectingCableLengthM;
        if(lead>=totalLength)throw new ArgumentException("Connecting lead must be shorter than total cable length.");
        double[] joints=EnsureCableEnds(cable.JointPositionsIncludingEnds,totalLength);
        double[] velocities=A4SegmentVelocities(cable,joints,headerVelocity);
        bool[] known=new bool[velocities.Length];
        for(int k=0;k<known.Length;k++)
        {
            var segment=cable.Segments.FirstOrDefault(s=>s.StartM<=joints[k]+1e-6&&s.EndM>=joints[k+1]-1e-6);
            double v;known[k]=segment!=null&&A4TryVelocity(segment.InsulationType??"",out v);
        }
        // The CAL path diagnostic is native-peak evidence even when LOC uses onset.
        // Preserve its original metadata before mode-dependent velocity solving/clipping.
        double[] calPathDeviceJoints=(double[])joints.Clone();
        double[] calPathMetadataVelocities=(double[])velocities.Clone();
        bool[] calPathExplicitVelocities=(bool[])known.Clone();
        bool onsetGeometryUsable=EighthPulseMath.OnsetCalibrationUsable(calibration);
        double calibrationRt=calibration==null?double.NaN:
            firstArrival&&onsetGeometryUsable?calibration.OnsetRoundTripSeconds:calibration.RoundTripSeconds;
        if(calibration!=null&&(!Finite(calibrationRt)||calibrationRt<=0))
        {calibration=null;calibrationRt=double.NaN;}
        string geometryReason="native_geometry_without_valid_CAL_no_auto_accept";
        double length=totalLength;
        if(lead>0)
        {
            if(calibration==null)throw new InvalidDataException("Lead compensation requires a usable same-phase/ALL CAL.");
            var geometry=ConnectingCableGeometry.Build(totalLength,lead,true,calibrationRt*1e6,joints,velocities,known,2e6/selected.Data.SampleRate);
            if(!geometry.CanUseCalibrationGeometry)throw new InvalidDataException("CAL travel time conflicts with explicit DUT segment velocities.");
            length=geometry.DutLengthM;joints=geometry.DutJointsM;velocities=geometry.DutSegmentVelocitiesMPerUs;
            geometryReason="CAL_time_minus_device_lead; measured_PD_delay_unchanged";
        }
        else if(calibration!=null)
        {
            double knownTime=0,unknownLength=0;
            for(int k=0;k<velocities.Length;k++)
                if(known[k])knownTime+=(joints[k+1]-joints[k])/velocities[k]*1e-6;
                else unknownLength+=joints[k+1]-joints[k];
            if(unknownLength>0)
            {
                double remaining=calibrationRt/2-knownTime;
                double speed=unknownLength/remaining*1e-6;
                if(!Finite(speed)||speed<50||speed>300)
                    throw new InvalidDataException("CAL implies a nonphysical cable velocity; check CAL markers and cable length.");
                for(int k=0;k<velocities.Length;k++)if(!known[k])velocities[k]=speed;
                geometryReason="CAL_measured_timing_resolves_unspecified_velocity; explicit_segment_velocities_preserved";
            }
            else if(Math.Abs(2*knownTime-calibrationRt)>2/selected.Data.SampleRate+1e-15)
                throw new InvalidDataException("CAL measured timing conflicts with explicit segment velocities.");
            else geometryReason="explicit_segment_geometry_confirmed_by_CAL";
        }
        double rt=0;
        for(int k=0;k<velocities.Length;k++)
        {
            if(!Finite(velocities[k])||velocities[k]<50||velocities[k]>300)
                throw new InvalidDataException("Method 8 segment velocity must be in the confirmed 50..300 m/us range.");
            rt+=2*(joints[k+1]-joints[k])/velocities[k]*1e-6;
        }
        double exactSamples=rt*fs;
        if(!Finite(exactSamples)||exactSamples<=0||exactSamples>int.MaxValue/4d)
            throw new InvalidDataException("Unrepresentable cable round-trip sample span.");
        double[] raw=loc.Samples;
        // Unlike a threshold capped at the peak, noise does not force itself into output.
        int sampleCount=Math.Min(65536,raw.Length);
        var estimate=new double[sampleCount];
        for(int i=0;i<sampleCount;i++)estimate[i]=raw[(int)((long)i*raw.Length/sampleCount)];
        double baseline=Median(estimate),sigma=EighthPulseMath.EstimateNoise(raw);
        double peak=raw.Max(v=>Math.Abs(v-baseline));
        sigma=Math.Max(sigma,Math.Max(1e-15,peak*1e-10));
        double multiplier=thresholdType==1?(thresholdMultiplier>0?thresholdMultiplier:3):1;
        double threshold=thresholdType==-1?Math.Abs(manualThreshold):
            Math.Max(5,Math.Sqrt(2*Math.Log(Math.Max(2,raw.Length))))*sigma*multiplier;
        if(!Finite(threshold))throw new ArgumentException("Computed threshold overflowed.");
        double frequency=requestedFrequency>0?requestedFrequency:dem==null?double.NaN:dem.FrequencyHz;
        initialPhase=NormalizeDegrees(initialPhase);
        bool[] quadrants=owner.AnalysisPhaseQuadrants;
        if(quadrants.Any(v=>v)&&!quadrants.All(v=>v)&&(!Finite(frequency)||frequency<=0))
            throw new InvalidDataException("A partial phase mask requires a known positive oscillation frequency.");
        if(Finite(frequency)&&(!Finite(360*frequency*(raw.Length-1)/fs)||frequency>fs/2))
            throw new InvalidDataException("Oscillation frequency cannot be resolved at this sampling rate.");
        int limit=ReviewedAnalysisLimit(maximumIncidents);
        // CAL templates are measured device-side. The shared lead cancels from
        // PD delay, so only the search-geometry metadata is shifted, never samples.
        EighthCalibration searchCalibration=A8SearchCalibration(calibration,
            2*lead/ConnectingCableGeometry.ConnectingCableVelocityMPerUs*1e-6);
        // A device-side CAL injection and a lead length alone do not identify
        // the reflecting near boundary. Positive-lead automatic roles remain
        // review until an explicit endpoint provenance protocol is supplied.
        EighthAnalysis analysis=EighthPulseMath.AnalyzeWithClocks(raw,fs,rt,threshold,firstArrival,searchCalibration,
            lead>0?new EighthLoopClockContext {OriginalCalibration=calibration,BoundaryKind="unknown",BoundarySource="formal_wrapper_positive_lead_no_reflection_boundary_evidence"}:
                EighthResolvedClocks.Legacy(calibration),limit,.85,
            sample=>AnalysisPhaseQuadrantPolicy.Eligible(quadrants,initialPhase,frequency,fs,sample),suppression);
        kernelReceipt=analysis.ResourceUsage;
        if(!analysis.Valid)throw new InvalidDataException("Eighth analysis failed: "+analysis.Reason);
        string filterReason;bool enabled;
        double[] normalizedCutoffs=A4NormalizeFilter(displayFilter,displayCutoffs,fs,out enabled,out filterReason);
        double[] display=enabled?A4ZeroPhase(raw,fs,normalizedCutoffs):raw;
        int extensionLimit=A4DisplayExtensionLimit(exactSamples);
        int pre=A4DisplayExtensionPoints(preRequested,extensionLimit),post=A4DisplayExtensionPoints(postRequested,extensionLimit);
        var pr=new List<double[]>();var wr=new List<double[]>();var wf=new List<double[]>();var we=new List<double[]>();var roles=new List<string>();
        var pairs=analysis.Pairs.ToDictionary(p=>p.Incident.Peak);
        var outputs=analysis.Pairs.Select(p=>p.Incident).Concat(analysis.Unpaired).GroupBy(p=>p.Peak).Select(g=>g.First()).OrderBy(p=>p.Peak).ToArray();
        int widest=0,widestEquivalent=0;
        foreach(EighthPulse pulse in outputs)
        {
            EighthPair pair;pairs.TryGetValue(pulse.Peak,out pair);
            double t0=pair==null?(firstArrival&&pulse.Onset>=0?pulse.Onset:pulse.Peak):pair.T0;
            double t1=pair==null?double.NaN:pair.T1;
            if(!Finite(t0)||t0<0||t0>=raw.Length||t0!=Math.Floor(t0))
                throw new InvalidDataException("Method 8 lost a canonical original-sample timing anchor.");
            if(pair!=null&&(!Finite(t1)||t1<=t0||t1>=raw.Length||t1!=Math.Floor(t1)))
                throw new InvalidDataException("Method 8 returned an invalid reflected timing anchor.");
            long wantedEnd=(long)t0+(long)Math.Ceiling(exactSamples)+post;
            int start=Math.Max(0,Math.Min(pulse.Start,(int)t0-pre));
            int end=(int)Math.Min(raw.Length-1L,Math.Max(wantedEnd,pair==null?pulse.End:pair.Reflection.End));
            widest=Math.Max(widest,end-start+1);
            if(widest>EighthMaximumOutputWidth||(long)outputs.Length*widest>EighthMaximumMatrixCells)
                throw new InvalidDataException("Method 8 output waveform budget exceeded; no partial accepted result was committed.");
            double[] row=NewNaNRow();
            row[0]=PhaseCode(loc.Phase);row[1]=locationDataId;row[2]=loc.VoltageKv;
            row[3]=Finite(frequency)&&frequency>0?NormalizeDegrees(initialPhase+360*frequency*pulse.Peak/fs):initialPhase;
            row[4]=threshold;row[5]=length;row[6]=2*length/rt*1e-6;row[7]=fs;row[8]=locationDataId;row[9]=pr.Count;
            row[10]=t0;row[14]=exactSamples;row[15]=pulse.AbsAmplitude;
            row[19]=row[25]=dem==null?double.NaN:dem.TanDelta;row[20]=t0-start;
            row[26]=7;row[27]=suppression;row[28]=analysis.NoiseSigma;
            double pulseBackground=analysis.BaselineCorrectionUsed?pulse.LocalBaseline:analysis.Baseline;
            row[29]=pulseBackground+threshold;row[30]=pulseBackground-threshold;
            row[31]=start;row[32]=end;row[34]=automaticAnalysis?1:0;row[35]=0;
            string reason=pair==null?"unpaired_no_fabricated_reflection":pair.Reason;
            if(pair!=null)
            {
                row[11]=t1;row[12]=t1-t0;row[13]=A4BoundaryOutputPosition((int)row[12],fs,joints,velocities,length);
                row[16]=row[15];row[17]=1;
                row[21]=Math.Abs(pair.Correlation);row[22]=Math.Abs(pair.Gain);
                row[33]=Finite(pair.Score)?Clamp(pair.Score,0,1):double.NaN;
                bool coverage=EighthPulseMath.SampleWindowCovered(raw.Length-1-t0,fs,rt);
                bool inside=Finite(row[13])&&row[13]>=0&&row[13]<=length;
                bool strictDelay=EighthPulseMath.SampleDelayFitsWindow(row[12],fs,rt);
                bool finiteContract=new[]{row[3],row[5],row[6],row[7],row[10],row[11],row[12],row[13],row[15],row[21],row[33]}.All(Finite);
                bool timingEvidence=!firstArrival||(!pair.FirstArrivalUnresolved&&
                    EighthPulseMath.OnsetCalibrationUsable(searchCalibration)&&
                    EighthPulseMath.OnsetUsable(pair.Incident)&&EighthPulseMath.OnsetUsable(pair.Reflection)&&
                    pair.Reflection.OnsetEarliest-pair.Incident.OnsetLatest>0&&
                    EighthPulseMath.SampleDelayFitsWindow(pair.Reflection.OnsetLatest-pair.Incident.OnsetEarliest,fs,rt));
                row[35]=pair.Accepted&&calibration!=null&&inside&&coverage&&strictDelay&&finiteContract&&timingEvidence?1:0;
                if(row[35]==0&&Finite(row[33]))row[33]=Math.Min(.49,row[33]);
                if(!coverage)reason+="; record_does_not_cover_complete_round_trip";
                if(!inside||!strictDelay)reason+="; measured_position_or_delay_outside_DUT_not_clipped";
                if(!finiteContract)reason+="; critical_parameter_not_finite";
                if(!timingEvidence)reason+="; first_arrival_support_or_CAL_clock_interval_unresolved";
            }
            double[] equivalent=A8Equivalent(raw,pulse,fs,analysis.Baseline,analysis.BackgroundSamples,customEquivalent,equivalentWindowNs);
            widestEquivalent=Math.Max(widestEquivalent,equivalent.Length);
            if((long)outputs.Length*widestEquivalent>EighthMaximumMatrixCells)
                throw new InvalidDataException("Method 8 equivalent matrix budget exceeded; no partial accepted result was committed.");
            A8Features(equivalent,fs,row);
            if(row.Any(v=>Double.IsInfinity(v)))throw new InvalidDataException("Method 8 parameter arithmetic overflow; no partial result committed.");
            pr.Add(row);wr.Add(A4Slice(raw,start,end));wf.Add(A4Slice(display,start,end));we.Add(equivalent);
            roles.Add("{\"row\":"+(pr.Count-1)+",\"canonical_incident_peak\":"+pulse.Peak+
                ",\"canonical_reflection_peak\":"+(pair==null?"null":pair.Reflection.Peak.ToString(Inv))+
                ",\"state\":"+Json(pair==null?"unpaired":row[35]==1?"accepted_pair":"review_pair")+
                ",\"acceptance_semantics\":\"engineering_candidate_not_manual_or_physical_confirmation\""+
                ",\"source_identity_supported\":"+(pair!=null&&pair.SourceIdentitySupported?"true":"false")+
                ",\"source_identity_requires_review\":"+(pair!=null&&!pair.SourceIdentitySupported?"true":"false")+
                ",\"source_identity_reason\":"+Json(pair==null?"no_pair":pair.SourceIdentityReason)+
                ",\"incident_packet_spectrum\":"+EighthSourceIdentity.Json(pulse)+
                ",\"reflection_packet_spectrum\":"+EighthSourceIdentity.Json(pair==null?null:pair.Reflection)+
                ",\"reason\":"+Json(reason)+",\"relative_polarity\":"+(pair==null?"null":pair.RelativePolarity.ToString(Inv))+
                ",\"propagation_role\":"+Json(pair==null?"unpaired":pair.Role)+",\"family_root_peak\":"+(pair==null?"null":pair.FamilyRootPeak.ToString(Inv))+
                ",\"echo_order\":"+(pair==null?"null":pair.EchoOrder.ToString(Inv))+",\"timing_uncertainty_seconds\":"+
                    NumberJson(pair==null?(firstArrival?pulse.OnsetTimingUncertaintySeconds:pulse.TimingUncertaintySeconds):pair.TimingUncertaintySeconds)+
                ",\"timing_mode\":"+Json(firstArrival?"first_arrival":"peak")+
                ",\"selected_native_indices\":{\"incident\":"+NumberJson(t0)+",\"reflection\":"+NumberJson(t1)+"}"+
                ",\"incident_timing_evidence\":"+A8PulseTimingJson(pulse)+
                ",\"reflection_timing_evidence\":"+A8PulseTimingJson(pair==null?null:pair.Reflection)+"}");
        }
        string calJson=selected==null?"null":"{\"path\":"+Json(selected.Path)+",\"phase\":"+Json(selected.Phase)+
            ",\"valid\":"+(calibration!=null?"true":"false")+",\"reason\":"+Json(selected.Reason)+
            ",\"signed_reflection_to_incident_ratio\":"+NumberJson(selected.Evidence.SignedGain)+
            ",\"sample_rate_hz\":"+NumberJson(selected.Data.SampleRate)+",\"selected_timing_round_trip_seconds\":"+NumberJson(calibrationRt)+
            ",\"measurement_timing_uncertainty_seconds\":"+NumberJson(selected.Evidence.MeasurementTimingUncertaintySeconds)+
            ",\"onset_clock_identifiable\":"+(EighthPulseMath.OnsetCalibrationUsable(searchCalibration)?"true":"false")+
            ",\"onset_clock_reason\":"+Json(selected.Evidence.OnsetClockReason)+
            ",\"onset_round_trip_minimum_seconds\":"+NumberJson(searchCalibration==null?double.NaN:searchCalibration.OnsetRoundTripMinimumSeconds)+
            ",\"onset_round_trip_maximum_seconds\":"+NumberJson(searchCalibration==null?double.NaN:searchCalibration.OnsetRoundTripMaximumSeconds)+
            ",\"onset_measurement_uncertainty_seconds\":"+NumberJson(selected.Evidence.OnsetMeasurementTimingUncertaintySeconds)+
            ",\"peak_onset_marker_discrepancy_seconds\":"+NumberJson(selected.Evidence.ModeDiscrepancySeconds)+
            ",\"propagation_search_timing_allowance_seconds\":"+NumberJson(selected.Evidence.TimingUncertaintySeconds)+
            ",\"template_reliable\":"+(selected.Evidence.TemplateReliable?"true":"false")+"}";
        bool selectedClockUsable=calibration!=null&&(!firstArrival||EighthPulseMath.OnsetCalibrationUsable(searchCalibration));
        string calPathEvidence=EighthCalibrationPathEvidence.DescribeFromCable(calibration,calPathDeviceJoints,
            calPathMetadataVelocities,calPathExplicitVelocities,lead,fs,analysis);
        string suitability=!selectedClockUsable?"requested_CAL_clock_unusable_review_coordinates_only":
            length<=50||Math.Abs(calibration.SignedGain)>1?"recommended_short_or_strong_reflection":"outside_recommended_application_requires_validation";
        string status="{\"ok\":true,\"build\":"+Json(Task17BuildInfo.Version)+",\"method\":8,\"algorithm_index\":7,"+
            "\"localization_available\":"+(selectedClockUsable?"true":"false")+",\"application_status\":"+Json(suitability)+
            ",\"zero_accepted_means_no_discharge\":false,\"automatic_fallback_to_other_method\":false,"+
            "\"name\":\"adaptive_native_propagation_method8\",\"method8_source_version\":"+Json(EighthPulseMath.SourceVersion)+",\"source_loc\":"+Json(identity)+
            ",\"requested_timing_mode\":"+Json(firstArrival?"first_arrival":"peak")+
            ",\"geometry_timing_mode\":"+Json(firstArrival&&onsetGeometryUsable?"first_arrival":firstArrival?"peak_geometry_for_review_only_CAL_onset_unresolved":"peak")+
            ",\"resolved_calibration\":"+Json(selected==null?"":selected.Path)+",\"native_sample_rate_hz\":"+NumberJson(fs)+
            ",\"sampling_domain\":\"original_unresampled_raw; no_fixed_3MHz_analysis_filter\",\"recommended_application\":\"cable_below_50m_OR_CAL_reflection_larger_than_incident\","+
            "\"length_at_most_50m\":"+(length<=50?"true":"false")+",\"calibration_amplitude_ratio_above_one\":"+(calibration!=null&&Math.Abs(calibration.SignedGain)>1?"true":"false")+
            ",\"length_limit_is_hard_gate\":false,\"calibration\":"+calJson+",\"CAL_path_evidence\":"+calPathEvidence+",\"geometry_basis\":"+Json(geometryReason)+
            ",\"connecting_lead_m\":"+NumberJson(lead)+",\"sample_resolution_m\":"+NumberJson(velocities.Max()*1e6/(2*fs))+
            ",\"uncertainty_note\":\"sample resolution is not an accuracy guarantee; unresolved overlap never auto accepts\","+
            "\"raw_charge_preserved\":true,\"gain_based_charge_compensation_applied\":false,\"attenuation_error_tolerance_percent_requested\":"+NumberJson(owner.AttenuationErrorTolerancePercent)+
            ",\"attenuation_tolerance_used_as_gate\":false,\"attenuation_switch_requested\":"+(attenuationRequested?"true":"false")+
            ",\"width_switch_requested\":"+(widthRequested?"true":"false")+",\"required_independent_support_and_calibration_not_bypassable\":true,"+
            "\"suppression_requested\":"+suppression+",\"suppression_effective\":\"native_propagation_family_role_review\","+
            "\"velocity_input_rule\":"+Json(velocityInputRule)+",\"legacy_velocity_override_enabled\":"+(owner.Method8ConfirmedLegacyVelocityUnits?"true":"false")+","+
            "\"geometry\":{\"cable_length_m\":"+NumberJson(length)+",\"segment_edges_m\":["+String.Join(",",joints.Select(NumberJson))+
            "],\"segment_velocities_m_per_us\":["+String.Join(",",velocities.Select(NumberJson))+"],\"exact_round_trip_samples\":"+NumberJson(exactSamples)+
            ",\"effective_velocity_m_per_us\":"+NumberJson(2*length/rt*1e-6)+"},"+
            "\"candidate_count\":"+analysis.CandidateCount+",\"echo_count\":"+analysis.EchoCount+",\"review_count\":"+analysis.ReviewCount+
            ",\"propagation_complete\":"+(analysis.PropagationComplete?"true":"false")+",\"propagation_summary\":"+Json(analysis.Reason)+
            ",\"graph_evaluation_complete\":"+(analysis.GraphEvaluationComplete?"true":"false")+
            ",\"publication_limited\":"+(analysis.PublicationLimited?"true":"false")+",\"publication_complete\":"+(analysis.PublicationComplete?"true":"false")+
            ",\"publication_eligible_count\":"+analysis.PublicationEligibleCount+",\"publication_published_count\":"+analysis.PublishedCount+",\"publication_omitted_count\":"+analysis.PublicationOmittedCount+
            ",\"publication_policy\":\"bounded_review_not_complete_localization; limited_revokes_all_automatic_acceptance_and_echo; actual_resource_failure_is_atomic\""+
            ",\"kernel_resource_usage\":"+A8ResourceJson(kernelReceipt)+",\"calibration_resource_usage\":"+A8ResourceJson(calibrationReceipt)+
            ",\"propagation_evidence\":"+(String.IsNullOrWhiteSpace(analysis.PropagationSummary)?"null":analysis.PropagationSummary)+
            ",\"affine_guard_recovery\":{\"experimental\":true,\"not_an_accuracy_claim\":true,"+
            "\"verification\":\"three_complete_observed_pairs_two_adjacent_hops_with_original_adjacent_branch_anchor\","+
            "\"admitted\":["+String.Join(",",analysis.Pulses.Where(p=>p.Reason.Contains("affine_guard")).Select(A8RecoveryPacketJson))+
            "],\"unverified\":["+String.Join(",",analysis.RecoveryUnverified.Select(A8RecoveryPacketJson))+
            "],\"original_low_snr_replaced\":["+String.Join(",",analysis.RecoveryReplacedLowSnr.Select(A8RecoveryPacketJson))+
            "],\"unverified_is_not_proved_noise\":true},".TrimEnd(',')+
            ",\"baseline_stable\":"+(analysis.BaselineStable?"true":"false")+",\"noise_sigma\":"+NumberJson(analysis.NoiseSigma)+
            ",\"packet_noise_basis\":\"original_local_absolute_residual_floor;recovered_packets_use_independently_checked_affine_guards_plus_observed_family;raw_samples_unchanged\""+
            ",\"packet_noise_window_native_samples\":"+analysis.LocalNoiseWindowSamples+
            ",\"global_noise_is_not_each_packet_noise\":true"+
            ",\"baseline_correction_used\":"+(analysis.BaselineCorrectionUsed?"true":"false")+",\"baseline_correction_summary\":"+Json(analysis.BaselineCorrectionSummary)+
            ",\"charge_definition\":\"original_peak_minus_measured_local_background; no_gain_compensation\",\"raw_display_samples_unchanged\":true"+
            ",\"joint_local_paths_modelled\":false,"+
            "\"output_threshold\":"+NumberJson(threshold)+",\"threshold_rule\":\"positive_raw_amplitude_greater_than_or_equal_to_threshold; automatic_threshold_not_clamped_to_maximum\","+
            "\"phase_filter_and_column3_anchor\":\"canonical_original_incident_peak\",\"display_filter\":"+Json(filterReason)+
            ",\"expected_CAL_round_trip_seconds_from_independent_geometry\":"+NumberJson(expectedCalibrationRt)+",\"maximum_incidents\":"+limit+",\"limit_reached\":"+(analysis.LimitReached?"true":"false")+
            ",\"ambiguous_count\":"+analysis.AmbiguousCount+",\"score_interpretation\":\"engineering_consistency_not_probability\","+
            "\"calibration_candidates\":["+String.Join(",",candidates.Select(c=>"{\"path\":"+Json(c.Path)+",\"valid\":"+(c.Valid?"true":"false")+",\"reason\":"+Json(c.Reason)+",\"calibration_resource_usage\":"+A8ResourceJson(c.Evidence==null?null:c.Evidence.ResourceUsage)+"}"))+
            "],\"output_pulse_roles\":{\"rows\":["+String.Join(",",roles)+"]}}";
        return new EighthCurrentOutput {Parameters=A4Matrix(pr,36),Raw=A4Matrix(wr,0),Filtered=A4Matrix(wf,0),Equivalent=A4Matrix(we,0),Status=status};
        }
        catch(Exception ex)
        {
            string diagnostic="{\"ok\":false,\"method\":8,\"method8_source_version\":"+Json(EighthPulseMath.SourceVersion)+",\"reason\":"+Json(ex.Message)+
                ",\"kernel_resource_usage\":"+A8ResourceJson(kernelReceipt)+",\"calibration_resource_usage\":"+A8ResourceJson(calibrationReceipt)+"}";
            throw new EighthResourceDiagnosticException(ex.Message,diagnostic,ex);
        }
    }

    private static string A8ResourceJson(EighthResourceReceipt receipt)
    {return receipt==null?"null":new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(receipt);}

    private static string A8RecoveryPacketJson(EighthPulse p)
    {
        return "{\"peak\":"+p.Peak+",\"start\":"+p.Start+",\"end\":"+p.End+
            ",\"valid\":"+(p.Valid?"true":"false")+",\"reason\":"+Json(p.Reason)+
            ",\"abs_amplitude\":"+NumberJson(p.AbsAmplitude)+",\"noise_sigma\":"+NumberJson(p.Noise)+
            ",\"local_baseline\":"+NumberJson(p.LocalBaseline)+",\"timing\":"+A8PulseTimingJson(p)+"}";
    }

    private static EighthCalibration A8SearchCalibration(EighthCalibration calibration,double leadSeconds)
    {
        if(!Finite(leadSeconds)||leadSeconds<0)throw new ArgumentOutOfRangeException("leadSeconds");
        if(calibration==null||leadSeconds==0)return calibration;
        // Preserve measured packet indices and every timing capability field.
        // A shifted nonpositive onset interval is invalid evidence, not clipped.
        return new EighthCalibration {
            ResourceUsage=calibration.ResourceUsage,
            Valid=calibration.Valid,Reason=calibration.Reason,
            Incident=calibration.Incident,Reflection=calibration.Reflection,SignedGain=calibration.SignedGain,
            RoundTripSeconds=calibration.RoundTripSeconds-leadSeconds,
            RemovedLeadRoundTripSeconds=calibration.RemovedLeadRoundTripSeconds+leadSeconds,
            OnsetRoundTripSeconds=calibration.OnsetRoundTripSeconds-leadSeconds,
            SampleRateHz=calibration.SampleRateHz,NoiseSigma=calibration.NoiseSigma,Baseline=calibration.Baseline,
            Quality=calibration.Quality,TimingUncertaintySeconds=calibration.TimingUncertaintySeconds,
            MeasurementTimingUncertaintySeconds=calibration.MeasurementTimingUncertaintySeconds,
            ModeDiscrepancySeconds=calibration.ModeDiscrepancySeconds,
            OnsetClockIdentifiable=calibration.OnsetClockIdentifiable,OnsetClockReason=calibration.OnsetClockReason,
            OnsetRoundTripMinimumSeconds=calibration.OnsetRoundTripMinimumSeconds-leadSeconds,
            OnsetRoundTripMaximumSeconds=calibration.OnsetRoundTripMaximumSeconds-leadSeconds,
            OnsetMeasurementTimingUncertaintySeconds=calibration.OnsetMeasurementTimingUncertaintySeconds,
            ResolutionSeconds=calibration.ResolutionSeconds,ResolutionSamples=calibration.ResolutionSamples,
            RelativePolarity=calibration.RelativePolarity,Template=calibration.Template,TemplateReliable=calibration.TemplateReliable,
            ObservedPackets=calibration.ObservedPackets,RecordSampleCount=calibration.RecordSampleCount};
    }

    private static string A8PulseTimingJson(EighthPulse p)
    {
        return p==null?"null":"{\"peak\":"+p.Peak+",\"onset\":"+p.Onset+
            ",\"onset_earliest\":"+p.OnsetEarliest+",\"onset_latest\":"+p.OnsetLatest+
            ",\"onset_identifiable\":"+(EighthPulseMath.OnsetUsable(p)?"true":"false")+
            ",\"onset_reason\":"+Json(p.OnsetReason)+
            ",\"observed_front_starts\":["+String.Join(",",p.ObservedFrontStarts)+"]"+
            ",\"onset_uncertainty_seconds\":"+NumberJson(p.OnsetTimingUncertaintySeconds)+
            ",\"uncertainty_basis\":\"farthest_observed_front_from_selected_onset_plus_noise;not_statistical_confidence\"}";
    }

    private static EighthCalFile A8ResolveCalibration(string[] files,string phase,double targetLength,double expectedRt,out List<EighthCalFile> all)
    {
        all=new List<EighthCalFile>();
        foreach(string path in files.Where(f=>String.Equals(Path.GetExtension(f),".cal",StringComparison.OrdinalIgnoreCase)).OrderBy(f=>f,StringComparer.OrdinalIgnoreCase))
        {
            var candidate=new EighthCalFile {Path=path,Phase="",Reason="invalid_name"};all.Add(candidate);
            Match name=Regex.Match(Path.GetFileNameWithoutExtension(path),@"^(A|B|C|L1|L2|L3|ALL|COMMON)_([0-9]+(?:\.[0-9]+)?)(PC|NC)$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            if(!name.Success)continue;
            candidate.Phase=A4StrictPhase(name.Groups[1].Value);
            if(candidate.Phase!=phase&&candidate.Phase!="ALL"){candidate.Reason="other_phase_not_used_for_eighth_CAL";continue;}
            try
            {
                A8ValidateTextFileSize(path);
                string[] lines=ReadAllLinesSmart(path);double[] body=AuditedNumericBody(lines,13,16,EighthMaximumSamples);
                if(body.Length>EighthMaximumSamples)throw new InvalidDataException("Method 8 CAL sample budget exceeded.");
                var header=A8Header(lines,13);
                string declaredPhase;
                if(header.TryGetValue("Phase",out declaredPhase)&&!String.IsNullOrWhiteSpace(declaredPhase))
                {
                    string normalizedPhase=A4StrictPhase(declaredPhase);
                    if(normalizedPhase.Length==0||normalizedPhase!=candidate.Phase)
                        throw new InvalidDataException("CAL header phase is unknown or disagrees with its filename.");
                }
                var cal=new ParsedCalibration {Path=path,Phase=candidate.Phase,Samples=body,
                    CableLength=NumContains(header,"Cable length"),VelocityMPerUs=A8HeaderVelocity(header),
                    CalibrationValuePc=NumContains(header,"Cal.value"),CalibrationCoefficient=NumContains(header,"NI_Calib_factor"),
                    StartTime=A8HeaderTimeSeconds(header,"Start_point"),FinalTime=A8HeaderTimeSeconds(header,"Final_point"),
                    SampleRate=A8CalibrationSampleRate(header),AttenuationLossDb=NumContains(header,"Calib_Attenuation")};
                candidate.Data=cal;
                double coefficient=NumContains(header,"NI_Calib_factor"),value=cal.CalibrationValuePc;
                double named=Double.Parse(name.Groups[2].Value,Inv)*(name.Groups[3].Value.Equals("NC",StringComparison.OrdinalIgnoreCase)?1000:1);
                if(!Finite(coefficient)||coefficient<=0||!Finite(value)||value<=0||Math.Abs(value-named)>Math.Max(.01,named*.001))
                    throw new InvalidDataException("CAL value/coefficient is invalid or disagrees with filename.");
                if(!Finite(cal.StartTime)||!Finite(cal.FinalTime))
                    throw new InvalidDataException("CAL start/reflection timing marker is missing or nonfinite; native sampling rate was decoded separately.");
                if(cal.StartTime<0||cal.FinalTime<=cal.StartTime||cal.FinalTime*cal.SampleRate>body.Length-1)
                    throw new InvalidDataException("CAL requires ordered in-record start/reflection timing markers.");
                if(Finite(targetLength)&&targetLength>0&&Finite(cal.CableLength)&&cal.CableLength>0&&
                    Math.Abs(targetLength-cal.CableLength)>Math.Max(.02*targetLength,2*299.792458e6/cal.SampleRate))
                    throw new InvalidDataException("CAL cable length disagrees with the current phase geometry.");
                double a=cal.StartTime*cal.SampleRate,b=cal.FinalTime*cal.SampleRate;
                if(!Finite(a)||!Finite(b)||a>int.MaxValue||b>int.MaxValue)throw new InvalidDataException("CAL marker overflow.");
                candidate.Evidence=EighthPulseMath.Calibrate(body,cal.SampleRate,(int)Math.Round(a),(int)Math.Round(b),
                    Finite(expectedRt)&&expectedRt>0?expectedRt:cal.FinalTime-cal.StartTime);
                candidate.Valid=candidate.Evidence.Valid;candidate.Reason=candidate.Evidence.Reason;
            }
            catch(InvalidDataException ex){candidate.Reason="invalid_CAL: "+ex.Message;}
            catch(IOException ex){candidate.Reason="invalid_CAL: "+ex.Message;}
            catch(UnauthorizedAccessException ex){candidate.Reason="unreadable_CAL: "+ex.Message;}
            catch(ArgumentException ex){candidate.Reason="invalid_CAL: "+ex.Message;}
            catch(OverflowException ex){candidate.Reason="invalid_CAL: "+ex.Message;}
            catch(FormatException ex){candidate.Reason="invalid_CAL: "+ex.Message;}
        }
        return all.Where(c=>c.Valid).OrderBy(c=>c.Phase==phase?0:1).ThenByDescending(c=>c.Data.CalibrationValuePc)
            .ThenBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static double[] A8Equivalent(double[] raw,EighthPulse pulse,double fs,double baseline,double[] background,bool requested,double[] times)
    {
        int start=pulse.Start,end=pulse.End;
        if(!requested)return (double[])pulse.Samples.Clone();
        if(requested&&times!=null&&times.Length>=2)
        {
            // Public units stay ns, without a 30 ns lower limit for short pulses.
            if(times[0]<0||times[1]<=0)throw new ArgumentException("Equivalent window requires nonnegative pre-ns and positive total-ns.");
            double before=Math.Ceiling(times[0]*fs/1e9),count=Math.Ceiling(times[1]*fs/1e9);
            if(!Finite(before)||!Finite(count)||before>int.MaxValue||count>int.MaxValue)
                throw new ArgumentException("Equivalent window sample count is unrepresentable.");
            if(count<=before)throw new ArgumentException("Equivalent total window must include the selected peak.");
            long first=(long)pulse.Peak-(long)before,last=first+(long)count-1;
            start=(int)Math.Max(0,first);end=(int)Math.Min(raw.Length-1L,last);
        }
        if(start<0||end<start||end>=raw.Length)throw new InvalidDataException("Equivalent pulse support is out of bounds.");
        if(end-start+1>EighthMaximumOutputWidth)throw new InvalidDataException("Method 8 equivalent output width budget exceeded.");
        var equivalent=new double[end-start+1];
        for(int i=start;i<=end;i++)equivalent[i-start]=raw[i]-(background!=null&&background.Length==raw.Length?background[i]:baseline);
        return equivalent;
    }
    private static double A8ResolveNativeSampleRate(double requestedHz,double fileHz,bool memory)
    {
        if(!Finite(requestedHz)||requestedHz<0)throw new InvalidDataException("Requested sample rate must be finite nonnegative Hz.");
        if(memory)
        {
            if(requestedHz<=0)throw new InvalidDataException("Memory input requires explicit positive native Hz.");
            return requestedHz;
        }
        if(!Finite(fileHz)||fileHz<=0)throw new InvalidDataException("File native sample-rate metadata is invalid.");
        if(requestedHz>0&&Math.Abs(requestedHz-fileHz)>Math.Max(1e-6,Math.Abs(fileHz)*1e-12))
            throw new InvalidDataException("Requested LOC sample rate disagrees with the native file axis; no implicit resampling or time-axis relabeling is allowed.");
        return fileHz;
    }
    private static double A8HeaderTimeSeconds(Dictionary<string,string> header,string fragment)
    {
        string key=header.Keys.FirstOrDefault(k=>k.IndexOf(fragment,StringComparison.OrdinalIgnoreCase)>=0);
        if(key==null)return double.NaN;
        string label=key.Replace('（','(').Replace('）',')');
        const string units="s|sec|ms|msec|us|usec|μs|µs|ns|nsec|秒|毫秒|微秒|纳秒";
        Match number=Regex.Match(header[key].Trim(),@"^(?<n>"+A4NumericToken+@")\s*(?<u>"+units+@")?\s*$",RegexOptions.IgnoreCase);
        Match unit=Regex.Match(label,@"\(("+units+@")\)",RegexOptions.IgnoreCase);
        if(!number.Success||((label.Contains("(")||label.Contains(")"))&&!unit.Success))
            throw new InvalidDataException("Invalid CAL marker value or time-label unit: "+key);
        string valueUnit=number.Groups["u"].Value.ToLowerInvariant(),labelUnit=unit.Groups[1].Value.ToLowerInvariant();
        Func<string,double> scale=u=>u=="ms"||u=="msec"||u=="毫秒"?1e-3:
            u=="us"||u=="usec"||u=="μs"||u=="µs"||u=="微秒"?1e-6:
            u=="ns"||u=="nsec"||u=="纳秒"?1e-9:1;
        if(valueUnit.Length>0&&labelUnit.Length>0&&scale(valueUnit)!=scale(labelUnit))
            throw new InvalidDataException("Conflicting CAL marker time units: "+key);
        double n;
        if(!Double.TryParse(number.Groups["n"].Value,NumberStyles.Float,Inv,out n)||!Finite(n))
            throw new InvalidDataException("Nonfinite CAL marker time: "+key);
        double result=n*scale(valueUnit.Length>0?valueUnit:labelUnit);
        if(!Finite(result))throw new InvalidDataException("CAL marker time conversion overflow: "+key);
        return result;
    }
    private static void A8ValidateTextFileSize(string path)
    {
        if(new FileInfo(path).Length>EighthMaximumTextFileBytes)
            throw new InvalidDataException("Method 8 text input exceeds the bounded read budget.");
    }
    private static void A8Features(double[] samples,double fs,double[] row)
    {
        double scale=samples.Max(v=>Math.Abs(v));
        if(!Finite(scale)||scale<=0){row[18]=0;return;}
        double e=0,d=0,previous=0;
        for(int i=0;i<samples.Length;i++)
        {double v=samples[i]/scale;e+=v*v;if(i>0)d+=(v-previous)*(v-previous);previous=v;}
        row[18]=(scale/fs)*scale*e;row[23]=e/fs*1e9;row[24]=fs/(2*Math.PI)*Math.Sqrt(d/e);
        if(!Finite(row[18]))row[18]=double.NaN;
    }
}
