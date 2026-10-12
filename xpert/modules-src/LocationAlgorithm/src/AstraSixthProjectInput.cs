using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HVX.Unified.Internal;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    /// <summary>Project resolution and output only. All sixth-method scoring is delegated to A6Match.</summary>
    internal bool AnalyzeSixthProjectCurrent(
        string projectPath,string locationFilePath,double locSampleHz,double[] locData,bool useLocData,
        long locationDataId,bool automaticAnalysis,bool useFirstArrivalTiming,bool useAttenuationReference,
        bool usePulseWidthComparison,int thresholdType,double manualThresholdAmplitude,
        double automaticThresholdMultiplier,int multipleReflectionSuppressionCount,int maximumDischargeCount,
        int incidentPreExtensionPoints,int roundTripPostExtensionPoints,double initialPhaseDegrees,
        double oscillationFrequencyHz,bool applyLinearFilter,double[] linearFilterCutoffsHz,
        double minimumCorrelationPercent,double attenuationErrorPercent,double pulseWidthErrorPercent,
        double[] scoreWeights,out double[,] parameterMatrix2DOutput,out double[,] pulseEnvelopeMatrix2DOutput,
        out double[,] filteredPulseEnvelopeMatrix2DOutput,out string statusMessage)
    {
        parameterMatrix2DOutput=new double[0,36];pulseEnvelopeMatrix2DOutput=new double[0,0];
        filteredPulseEnvelopeMatrix2DOutput=new double[0,0];statusMessage="";
        Reset();equivalentContext=null;equivalentCurrentPackets.Clear();A6ResetOutputPulseBindings();
        canonicalOutputIdentities.Clear();canonicalPhysicalRows=new A4CanonicalOutputIdentity[0];
        EffectiveAutomaticDetectionThreshold=EffectiveOutputFilterThreshold=double.NaN;
        DetectedEventCountBeforeOutputFilter=OutputThresholdRejectedCount=0;
        ResolvedMatchedDemFilePath="";ResolvedTanDelta=double.NaN;Algorithm4DiagnosticJson="{}";
        ResolvedSegmentVelocitiesMPerUs=new double[0];
        string stage="settings_validation";
        try
        {
            A4ValidateConnectingCableSetting();
            if(SeventhMode)
            {
                a7RequestedAttenuation=useAttenuationReference;a7RequestedWidth=usePulseWidthComparison;
                // Method 7 is a mandatory calibrated pipeline. Legacy Boolean
                // port positions remain, but cannot silently bypass its stages.
                useAttenuationReference=true;usePulseWidthComparison=true;
                a7FirstArrival=useFirstArrivalTiming;
                if(double.IsNaN(attenuationErrorPercent)||attenuationErrorPercent==-1)attenuationErrorPercent=15;
                if(double.IsNaN(pulseWidthErrorPercent)||pulseWidthErrorPercent==-1)pulseWidthErrorPercent=35;
                if(scoreWeights==null||scoreWeights.Length==0)scoreWeights=new[]{40d,35d,15d,10d};
            }
            // Method 6 uses the user's declared 15% default. Do not silently
            // expand a -1/NaN request to the previous 60% automatic floor.
            if(!SeventhMode&&(double.IsNaN(attenuationErrorPercent)||attenuationErrorPercent==-1))attenuationErrorPercent=15;
            A6Settings options=A6Settings.Create(minimumCorrelationPercent,attenuationErrorPercent,pulseWidthErrorPercent,
                scoreWeights,useAttenuationReference,usePulseWidthComparison);
            if(locationDataId<0||locationDataId>9007199254740991L)
                throw new ArgumentOutOfRangeException("locationDataId","The ID must be an exact nonnegative integer in 0..2^53-1.");
            if(multipleReflectionSuppressionCount < -1)throw new ArgumentOutOfRangeException("multipleReflectionSuppressionCount");
            if(thresholdType < -1||thresholdType>1)throw new ArgumentOutOfRangeException("thresholdType");
            if(!Finite(initialPhaseDegrees)||!Finite(oscillationFrequencyHz)||oscillationFrequencyHz<0)
                throw new ArgumentException("Invalid phase or oscillation frequency.");
            if(!Finite(automaticThresholdMultiplier)||!Finite(manualThresholdAmplitude))
                throw new ArgumentException("Threshold controls must be finite.");
            stage="project_and_loc_resolution";
            bool defaultProject=String.IsNullOrWhiteSpace(projectPath);
            if(defaultProject&&String.IsNullOrWhiteSpace(locationFilePath))
                throw new ArgumentException("projectPath and locationFilePath cannot both be empty.");
            string root=defaultProject?Path.GetDirectoryName(Path.GetFullPath(locationFilePath)):Path.GetFullPath(projectPath);
            if(File.Exists(root))root=Path.GetDirectoryName(root);
            if(!Directory.Exists(root))throw new DirectoryNotFoundException("projectPath must identify the project directory or its ITEM file.");
            string[] files=Directory.GetFiles(root,"*",SearchOption.TopDirectoryOnly);
            string item=A4Named(files,"ITEM.in",true),cablePath=A4Named(files,"CABLE.in",true);
            string identity;
            if(!String.IsNullOrWhiteSpace(locationFilePath))
                identity=Path.GetFullPath(Path.IsPathRooted(locationFilePath)?locationFilePath:Path.Combine(root,locationFilePath));
            else
            {
                string[] locs=files.Where(f=>String.Equals(Path.GetExtension(f),".loc",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(locs.Length!=1)throw new ArgumentException("locationFilePath is needed when project LOC identity is not unique.");
                identity=locs[0];
            }
            if(!String.Equals(Path.GetExtension(identity),".loc",StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("locationFilePath must retain a .loc record identity.");
            ParsedLocation loc;
            if(useLocData)
            {
                if(!Finite(locSampleHz)||locSampleHz<=0||locData==null||locData.Length<16||locData.Any(v=>!Finite(v)))
                    throw new ArgumentException("Memory LOC requires positive locSampleHz and at least 16 finite engineering-value samples.");
                loc=new ParsedLocation {Path=identity,Samples=(double[])locData.Clone(),SampleRate=locSampleHz,
                    CableLength=double.NaN,VelocityMPerUs=double.NaN,PhaseDeg=double.NaN};
                ParseMeasurementName(Path.GetFileNameWithoutExtension(identity),out loc.Phase,out loc.VoltageKv);
            }
            else loc=ReadLocation(RequireFile(identity,".loc","locationFilePath"));
            if(String.IsNullOrEmpty(loc.Phase))
                throw new InvalidDataException("LOC filename requires an explicit A/B/C, L1/L2/L3 or ALL/COMMON phase token before the first underscore.");
            stage="phase_calibration_and_geometry";
            CableMetadata cable=ReadCable(cablePath,loc.Phase);
            double targetPhaseLength=FirstPositive(cable.CableLength,loc.CableLength);
            string calPath,calSource,priority,calSelectionDiagnostic;int calCount;
            ParsedCalibration cal=A4ResolvePhaseCalibration(root,loc.Phase,targetPhaseLength,
                out calPath,out calSource,out priority,out calCount,out calSelectionDiagnostic);
            int calibrationPolarity=A4CalibrationReference(cal);
            // Keep project validation even though unlabelled ITEM geometry must not override the current phase.
            ReadProject(item);
            string demPath=A4MatchedDem(files,identity,loc.Phase,loc.VoltageKv);
            ParsedDem dem=String.IsNullOrEmpty(demPath)?null:ReadDem(demPath);
            double fs=useLocData?locSampleHz:ResolveSampleRate(locSampleHz,loc.SampleRate,cal.SampleRate,dem?.SampleRate??double.NaN);
            A4ValidateCycleSampleRate(loc.CycleReference,fs);
            bool calibrationGeometryAllowed=cal.Phase==loc.Phase||cal.Phase=="ALL";
            double length=FirstPositive(cable.CableLength,loc.CableLength,calibrationGeometryAllowed?cal.CableLength:double.NaN);
            double velocity=FirstPositive(cable.VelocityMPerUs,loc.VelocityMPerUs,calibrationGeometryAllowed?cal.VelocityMPerUs:double.NaN);
            if(!(length>0)||!Finite(length)||(ConnectingCableLengthM==0&&(!(velocity>0)||!Finite(velocity))))
                throw new InvalidDataException("Current-phase cable length and velocity are unavailable; only same-phase/explicit COMMON CABLE, the current LOC header, or same-phase/ALL CAL may supply geometry. Unlabelled ITEM and other-phase geometry are not used.");
            string geometryDiagnostic=A4GeometryDiagnostic(cable,loc,cal,length,velocity);
            if(loc.Samples.Length<16||loc.Samples.Any(v=>!Finite(v)))
                throw new InvalidDataException("LOC samples must be finite and contain at least 16 values.");
            stage="fixed_analysis_sample_rate_validation";
            if(!Finite(fs)||fs<6000000)
                throw new InvalidDataException("Unsupported LOC sample rate: the fixed 3 MHz correlation/measurement band requires locSampleHz >= 6000000 Hz. No discharge conclusion was computed; caller history is preserved.");
            double[] joints=EnsureCableEnds(cable.JointPositionsIncludingEnds,length);
            double[] velocities=A4SegmentVelocities(cable,joints,velocity);
            string connectingCableDiagnostic=A4ApplyConnectingCable(ref cable,cal,calibrationGeometryAllowed,
                ref length,ref velocity,ref joints,ref velocities);
            double roundTripExact=0;
            for(int k=0;k<velocities.Length;k++)roundTripExact+=2*(joints[k+1]-joints[k])/velocities[k]*1e-6*fs;
            if(!Finite(roundTripExact)||roundTripExact<=0||roundTripExact>(int.MaxValue-1024)/8d)
                throw new InvalidDataException("Cable round-trip sample span is invalid or too large.");
            velocity=2*length*1e-6*fs/roundTripExact;
            int roundTrip=Math.Max(2,(int)Math.Round(roundTripExact));
            double[] raw=loc.Samples,analysisCutoffs=new[]{10000d,Math.Min(3000000d,fs/2)};
            bool requestedFilter=applyLinearFilter;string filterResolution;
            linearFilterCutoffsHz=A4NormalizeFilter(applyLinearFilter,linearFilterCutoffsHz,fs,out applyLinearFilter,out filterResolution);
            A4MeasurementSignal measurement=A4PrepareMeasurementSignal(raw,fs,roundTrip);
            double rawCenter=measurement.DetectionCenter,peak=measurement.DetectionRaw.Max(v=>Math.Abs(v-rawCenter));
            double rawSigma=measurement.RawSigma;
            double sigma=Math.Max(rawSigma,Math.Max(1e-15,peak*1e-8));
            double multiplier=automaticThresholdMultiplier>0?Math.Abs(automaticThresholdMultiplier):3;
            double discovery=Math.Min(peak,4.5*sigma),rawDiscovery=Math.Min(peak,4.5*rawSigma);
            double outputThreshold=thresholdType==-1?Math.Abs(manualThresholdAmplitude):
                Math.Max(5,Math.Sqrt(2*Math.Log(Math.Max(2,raw.Length))))*sigma*(thresholdType==1?Math.Max(.1,multiplier):1);
            if(thresholdType!=-1)outputThreshold=Math.Min(peak,outputThreshold);
            if(!Finite(outputThreshold))throw new ArgumentException("Output threshold must be finite.");
            double frequency=oscillationFrequencyHz>0?oscillationFrequencyHz:dem?.FrequencyHz??double.NaN;
            int extensionLimit=A4DisplayExtensionLimit(roundTripExact);
            int pre=A4DisplayExtensionPoints(incidentPreExtensionPoints,extensionLimit);
            int post=A4DisplayExtensionPoints(roundTripPostExtensionPoints,extensionLimit);
            var context=new A4Context {Raw=raw,Signal=measurement.DetectionRaw,MeasurementSignal=measurement,Center=rawCenter,RawCenter=rawCenter,
                RawDiscoveryThreshold=rawDiscovery,Sigma=sigma,Threshold=discovery,Fs=fs,Length=length,Velocity=velocity,
                RoundTrip=roundTrip,RoundTripExact=roundTripExact,Joints=joints,Velocities=velocities,Calibration=cal,
                Filtered=true,FirstArrival=false,FirstArrivalPhysicalTiming=useFirstArrivalTiming,IndependentTiming=true,Attenuation=useAttenuationReference,
                Width=usePulseWidthComparison,Suppression=multipleReflectionSuppressionCount,
                CalibrationPolarity=calibrationPolarity,EffectiveCutoffs=analysisCutoffs,
                AnalysisLimit=A4AnalysisLimit(maximumDischargeCount),IncidentThreshold=outputThreshold,Algorithm=5,SixthHybridGuardSearch=true};
            equivalentContext=context;
            A4ConfigureAnalysisPhase(context,initialPhaseDegrees,frequency,loc.CycleReference);
            context.ConnectingCableLengthM=ConnectingCableLengthM;
            context.CalibrationToleranceAdaptationLimitPercent=CalibrationToleranceAdaptationLimitPercent;
            context.LowBand=A4BuildLowBandReference(context);
            stage="fixed_band_candidate_discovery";
            // Baseline-corrected raw charge seeds precede filtering. User display bandwidth never enters discovery or scoring.
            List<A4Pulse> pulses=A4DiscoverRawSeedWindows(context,analysisCutoffs,200,500);
            A6RetentionInventory retention=A6BuildRetentionInventory(context,pulses);
            stage=SeventhMode?"seventh_threshold_calibration_window_dual_lobe_pipeline":"sixth_strict_evidence_matching";
            A6SetSpatialIdentity(PhaseCode(loc.Phase),locationDataId);
            if(SeventhMode)A7PreparePrior(context,root,cablePath,calPath,PhaseCode(loc.Phase));
            List<A4Pair> pairs=SeventhMode?A7SequentialLocate(context,pulses,options,retention.Packets):A6Match(context,pulses,options);
            DetectedEventCountBeforeOutputFilter=pairs.Count;
            List<A4Pair> selected=pairs.Where(p=>A4RawCharge(context,A4ChargeIndex(p.Incident))>=outputThreshold)
                .OrderByDescending(p=>A4RawCharge(context,A4ChargeIndex(p.Incident)))
                .ThenBy(p=>A4ChargeIndex(p.Incident)).ThenBy(p=>A4ChargeIndex(p.Reflection)).ToList();
            OutputThresholdRejectedCount=pairs.Count-selected.Count;
            int limit=context.AnalysisLimit;
            MaximumDischargeCountReached=context.AnalysisLimitReached||selected.Count>limit;
            stage="current_output";
            var parameterRows=new List<double[]>();var rawRows=new List<double[]>();var filteredRows=new List<double[]>();
            int phase=PhaseCode(loc.Phase),firstArrivalFallbacks=0;
            // Display-filter the complete record once. This array is output-only and never supplies an analysis sample.
            double[] displaySignal=applyLinearFilter?A4ZeroPhase(raw,fs,linearFilterCutoffsHz):raw;
            foreach(A4Pair pair in selected)
            {
                A4Pulse a=pair.Incident,b=pair.Reflection;
                int peakA=pair.OutputIncident,peakB=pair.OutputReflection;
                if(peakA<0||peakB<=peakA||peakB>=raw.Length||peakA!=A4ChargeIndex(a)||peakB!=A4ChargeIndex(b))
                    throw new InvalidOperationException("Sixth matching did not retain the fixed raw absolute peak identities.");
                int t0=peakA,t1=peakB;
                pair.OutputTimingReason="sixth_fixed_raw_absolute_peaks";
                Tuple<int,int> seventhTiming;
                if(SeventhMode&&a7OutputTimings.TryGetValue(pair,out seventhTiming)&&seventhTiming.Item2>seventhTiming.Item1)
                {t0=seventhTiming.Item1;t1=seventhTiming.Item2;pair.OutputTimingReason="seventh_pre_matching_physical_timing";}
                if(useFirstArrivalTiming&&!SeventhMode)
                {
                    int onsetA=A4RawLeadingEdge(context,a,peakA),onsetB=A4RawLeadingEdge(context,b,peakB);
                    if(onsetA>=0&&onsetA<=peakA&&onsetB>=0&&onsetB<=peakB&&onsetB>onsetA)
                    {t0=onsetA;t1=onsetB;pair.OutputTimingReason="sixth_fixed_peak_anchored_raw_leading_edges";}
                    else throw new InvalidOperationException("Reliable first-arrival timing could not be resolved. This record was not localized; no peak timing was substituted.");
                }
                if(useFirstArrivalTiming&&SeventhMode&&(!a7OutputTimings.TryGetValue(pair,out seventhTiming)||seventhTiming.Item1<0||seventhTiming.Item2<=seventhTiming.Item1))
                    throw new InvalidOperationException("Reliable first-arrival timing is unavailable for the selected pair; no peak timing was substituted.");
                // Peak identities remain immutable for charge/shape measurements;
                // physical scoring and published timing use the same selected mode.
                double displayPosition=A4BoundaryOutputPosition(t1-t0,fs,joints,velocities,length);
                HVX.SignalEvidence.SeventhPipelineResult seventhProjection;
                if(SeventhMode&&a7PipelineResults.TryGetValue(pair,out seventhProjection))displayPosition=seventhProjection.ProjectedPositionM;
                int start,end;
                if(!A4CompletePdmapCrop(context,SeventhMode?t0:peakA,t0,pre,post,out start,out end))continue;
                double[] row=NewNaNRow();
                row[0]=phase;row[1]=locationDataId;row[2]=loc.VoltageKv;
                row[3]=A4PhaseAt(context,t0);
                row[4]=outputThreshold;row[5]=length;row[6]=velocity;row[7]=fs;row[8]=locationDataId;
                row[9]=parameterRows.Count;row[10]=t0;row[11]=t1;row[12]=t1-t0;row[13]=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(displayPosition,length);
                row[14]=roundTrip;row[15]=A4RawCharge(context,peakA);
                row[16]=useAttenuationReference?A4ContextCompensateCharge(context,row[15],pair.Position):row[15];
                row[17]=row[15]>0?row[16]/row[15]:1;
                row[18]=a.Energy/fs;row[19]=dem?.TanDelta??double.NaN;row[20]=t0-start;
                row[21]=pair.Correlation;row[22]=pair.AmplitudeRatio;row[23]=a.EquivalentSamples/fs*1e9;
                row[24]=a.BandwidthHz;row[25]=row[19];row[26]=SeventhMode?6:5;row[27]=multipleReflectionSuppressionCount;
                row[28]=sigma;row[29]=A4RawBaselineAt(context,peakA)+outputThreshold;row[30]=A4RawBaselineAt(context,peakA)-outputThreshold;
                row[31]=start;row[32]=end;row[33]=Finite(pair.Confidence)?Clamp(pair.Confidence,0,1):double.NaN;
                row[34]=automaticAnalysis?1:0;row[35]=pair.State=="accepted"?1:0;
                if(SeventhMode&&(pair.Ambiguous||(a7PipelineResults.TryGetValue(pair,out seventhProjection)&&
                    seventhProjection.EndpointApplicable&&seventhProjection.EndpointDecision=="Indeterminate")))
                {if(!a7PipelineResults.TryGetValue(pair,out seventhProjection)||(!seventhProjection.RelaxedAttenuationFallback&&!seventhProjection.ReviewHypothesis))row[33]=double.NaN;row[35]=0;}
                // Use the explicit-identity overload; the legacy context overload may choose a different-polarity lobe.
                A4RegisterCanonicalOutputIdentity(row,peakA,peakB,pair);
                parameterRows.Add(row);rawRows.Add(A4Slice(raw,start,end));filteredRows.Add(A4Slice(displaySignal,start,end));
                equivalentCurrentPackets[A4RowKey(row)]=a;
            }
            A4SortRowsByCharge(parameterRows,rawRows,filteredRows);
            A6CapturePairedOutputRows(parameterRows);
            // Append only remaining unmatched incidents. Method 7 may already
            // have measured a review proposal from the supplemental inventory.
            A6AppendUnpairedOutputs(context,pulses.Concat(retention.Packets),selected,retention.Reasons,
                phase,locationDataId,loc.VoltageKv,frequency,initialPhaseDegrees,useFirstArrivalTiming,
                automaticAnalysis,multipleReflectionSuppressionCount,dem?.TanDelta??double.NaN,
                pre,post,displaySignal,parameterRows,rawRows,filteredRows);
            A4SortRowsByCharge(parameterRows,rawRows,filteredRows);
            A6BindCurrentOutputRows(parameterRows);
            ParameterMatrix2D=A4Matrix(parameterRows,36);PulseEnvelopeMatrix2D=A4Matrix(rawRows,0);
            FilteredPulseEnvelopeMatrix2D=A4Matrix(filteredRows,0);
            AddedRowCount=parameterRows.Count;AppliedMultipleReflectionSuppressionCount=context.EffectiveSuppression;
            EffectiveSampleRateHz=fs;EffectiveAutomaticDetectionThreshold=discovery;EffectiveOutputFilterThreshold=outputThreshold;
            AppliedMaximumDischargeCount=limit;DetectedDischargeCountBeforeLimit=pairs.Count;
            EffectiveIncidentPreExtensionPoints=pre;EffectiveRoundTripPostExtensionPoints=post;
            EffectiveInitialPhaseDegrees=NormalizeDegrees(context.AnalysisInitialPhaseDegrees);EffectiveOscillationFrequencyHz=context.AnalysisFrequencyHz;
            ResolvedCalibrationFilePath=calPath;ResolvedCableFilePath=cablePath;ResolvedProjectFilePath=item;
            ResolvedMatchedDemFilePath=ResolvedTestFilePath=demPath??"";ResolvedTanDelta=dem?.TanDelta??double.NaN;
            CalibrationSelectionPriority=priority;ResolvedCableLengthM=length;
            ResolvedJointPositionsIncludingEndsM=joints;ResolvedInternalJointPositionsM=cable.InternalJointPositions.ToArray();
            ResolvedSegmentVelocitiesMPerUs=velocities;
            ResolvedSegmentStartPositionsM=cable.Segments.Select(s=>s.StartM).ToArray();
            ResolvedSegmentEndPositionsM=cable.Segments.Select(s=>s.EndM).ToArray();
            ResolvedSegmentInsulationTypesJson=StringArrayJson(cable.Segments.Select(s=>s.InsulationType));
            ResolvedAccessoryTypesJson=StringArrayJson(cable.Accessories.Select(s=>s.Type));
            ParameterMatrix=Flatten(ParameterMatrix2D);ParameterRows=parameterRows.Count;ParameterColumns=36;
            RawWaveformMatrix=Flatten(PulseEnvelopeMatrix2D);RawWaveformRows=rawRows.Count;RawWaveformColumns=PulseEnvelopeMatrix2D.GetLength(1);
            FilteredWaveformMatrix=Flatten(FilteredPulseEnvelopeMatrix2D);FilteredWaveformRows=filteredRows.Count;FilteredWaveformColumns=FilteredPulseEnvelopeMatrix2D.GetLength(1);
            ValidatedRuntimePositionM=parameterRows.Select(r=>r[13]).ToArray();
            Algorithm4DiagnosticJson=SeventhMode?A7PipelineDiagnosticsJson(context,pairs,options):A6DiagnosticsJson(context,pulses,pairs,options);
            Success=true;Error="";
            StatusJson="{\"ok\":true,\"interface\":\"AnalyzeSixthLocationIncremental\",\"build\":"+Json(Task17BuildInfo.Version)+",\"algorithm_index\":5,"+
                "\"algorithm_strategy\":\"sixth_strict_calibrated_weighted_evidence\",\"recommended_type\":\"HVX.Task17.Location.SixthLocationAnalyzer\","+
                "\"location_identity\":"+Json(identity)+",\"project_path\":"+Json(root)+",\"project_path_defaulted_from_loc\":"+(defaultProject?"true":"false")+
                ",\"resolved_item\":"+Json(item)+",\"resolved_cable\":"+Json(cablePath)+",\"resolved_calibration\":"+Json(calPath)+
                ",\"optional_calibration\":{\"source\":"+Json(calSource)+",\"priority\":"+Json(priority)+",\"available\":"+(cal.Samples!=null&&cal.Samples.Length>=16?"true":"false")+
                ",\"attenuation_loss_db\":"+NumberJson(cal.AttenuationLossDb)+"},\"phase_geometry\":"+geometryDiagnostic+",\"connecting_cable\":"+connectingCableDiagnostic+",\"phase_calibration_selection\":"+calSelectionDiagnostic+
                ",\"resolved_dem\":"+Json(demPath??"")+",\"tan_delta\":"+NumberJson(ResolvedTanDelta)+",\"sample_rate_hz\":"+NumberJson(fs)+",\"cable_length_m\":"+NumberJson(length)+
                ",\"segment_edges_m\":"+NumberArrayJson(joints)+",\"segment_velocities_m_per_us\":"+NumberArrayJson(velocities)+
                ",\"loc_body_read\":"+(!useLocData?"true":"false")+",\"memory_loc_units\":\"engineering_pc\",\"analysis_filter_cutoffs_hz\":"+NumberArrayJson(analysisCutoffs)+
                ",\"analysis_band_rule\":\"fixed_10khz_3mhz_requires_sample_rate_at_least_6mhz\",\"display_filter_independent_of_analysis\":true,\"filter_requested\":"+(requestedFilter?"true":"false")+
                ",\"filter_enabled\":"+(applyLinearFilter?"true":"false")+",\"effective_filter_cutoffs_hz\":"+NumberArrayJson(linearFilterCutoffsHz)+",\"filter_normalization\":"+Json(filterResolution)+
                ",\"equivalent_signal_basis\":\"fixed_analysis_band_not_user_display_filter\",\"raw_output\":\"signed_raw_loc_window\",\"filtered_output\":\"output_only_user_filter_of_complete_record\",\"filtered_output_peak_normalized\":false"+
                ",\"physical_timing_basis\":"+Json(useFirstArrivalTiming?"raw_peak_anchored_leading_edge_pair":"fixed_raw_absolute_peak_pair")+",\"first_arrival_applied_after_pair_commit\":"+(useFirstArrivalTiming?"false":"true")+",\"pairing_independent_of_timing_switch\":"+(useFirstArrivalTiming?"false":"true")+",\"timing_point_source\":"+Json(useFirstArrivalTiming?"raw_peak_anchored_leading_edge":"raw_absolute_peak")+
                ",\"first_arrival_fallback_rows\":"+firstArrivalFallbacks+",\"charge_compensation_position_basis\":"+Json(useFirstArrivalTiming?"first_arrival_pair_delay_with_endpoint_clamp_only_for_compensation":"canonical_raw_peak_delay_with_endpoint_clamp_only_for_compensation")+",\"out_of_range_display_position\":\"signed_endpoint_extrapolation\""+
                ",\"display_window\":{\"anchor\":\"raw_incident_absolute_peak\",\"requested_pre_points\":"+incidentPreExtensionPoints+",\"requested_post_points\":"+roundTripPostExtensionPoints+
                ",\"effective_pre_points\":"+pre+",\"effective_post_points\":"+post+",\"extension_limit_points\":"+extensionLimit+",\"independent_of_analysis\":true,\"analysis_context_pre_points\":200,\"analysis_context_post_points\":500}"+
                ",\"preprocessing\":"+A4MeasurementSignalJson(measurement)+",\"detection_threshold\":"+NumberJson(discovery)+",\"output_threshold\":"+NumberJson(outputThreshold)+",\"threshold_domain\":\"baseline_corrected_raw_pc\",\"raw_maximum_absolute_pc\":"+NumberJson(peak)+
                ",\"analysis_phase_filter\":"+A4AnalysisPhaseJson(context)+",\"maximum_analysis_count\":"+limit+",\"analyzed_incident_count\":"+context.AnalysisVisitIds.Count+",\"analysis_limit_reached\":"+(MaximumDischargeCountReached?"true":"false")+
                ",\"detected_pairs_before_output_filter\":"+pairs.Count+",\"output_threshold_rejected\":"+OutputThresholdRejectedCount+",\"discovered_pulses\":"+pulses.Count+",\"added_rows\":"+AddedRowCount+
                ",\"accepted_rows\":"+parameterRows.Count(r=>r[35]==1)+",\"review_rows\":"+parameterRows.Count(r=>r[35]!=1)+",\"review_rows_in_main_matrices\":true,\"legacy_review_rescoring_used\":false,\"legacy_project_acceptance_used\":false"+
                ",\"paired_review_rows\":"+selected.Count(p=>p.State!="accepted")+",\"unpaired_rows\":"+a6CurrentOutputPulses.Count(p=>p.Pair==null)+",\"review_rows_includes_unpaired\":true"+
                ",\"unpaired_preservation\":"+A6UnpairedOutputJson()+",\"hybrid_boundary_review\":"+a6BoundaryReviewDiagnosticJson+",\"shape_unresolved_review\":"+a6ShapeReviewDiagnosticJson+
                ",\"endpoint_hypotheses\":"+(SeventhMode?"{\"policy\":\"diagnostic_only_no_automatic_flip\"}":A6EndpointHypothesisJson(context,selected,pulses))+
                ",\"output_pulse_roles\":"+A4AllOutputPulseRoleJson(A4Matrix(parameterRows,36),a6CurrentOutputPulses.Select(p=>A4ChargeIndex(p.Pulse)).ToArray(),
                    a6CurrentOutputPulses.Select(p=>p.Pair==null?-1:A4ChargeIndex(p.Pair.Reflection)).ToArray())+",\"raw_retention_inventory\":"+A6RetentionInventoryJson(retention)+
                ",\"output_sort\":\"raw_charge_descending_current_only\",\"charge_source\":\"baseline_corrected_raw_incident_peak\",\"column_12_invariant\":\"column11-column10\",\"column26_algorithm_index\":5,\"column33_range\":[0,1],\"column35_rule\":\"A6_state_equals_accepted\""+
                ",\"requested_suppression\":"+multipleReflectionSuppressionCount+",\"effective_suppression\":"+context.EffectiveSuppression+",\"suppression_resolution\":"+Json(context.SuppressionResolution)+
                ",\"use_attenuation_reference\":"+(useAttenuationReference?"true":"false")+",\"use_pulse_width_comparison\":"+(usePulseWidthComparison?"true":"false")+",\"diagnostics\":"+Algorithm4DiagnosticJson+"}";
            if(SeventhMode)StatusJson=A7OutputStatusJson(StatusJson);
            LastResultJson=StatusJson;
            StatusMessage="Task17 sixth algorithm: "+selected.Count+" current accepted/review pairs and "+a6CurrentOutputPulses.Count(p=>p.Pair==null)+" unpaired pulse rows; unpaired score 0 means not scored, not zero probability. Explicit caller history may guide ambiguous review selection but cannot create accepted pairs or hidden retained state.";
            if(SeventhMode)StatusMessage="Task17 seventh algorithm: "+selected.Count+" current accepted/review pairs and "+a6CurrentOutputPulses.Count(p=>p.Pair==null)+" unpaired pulse rows. Original absolute peaks above threshold, CAL incident correlation, physical window, same-polarity dual-lobe attenuation and width gates precede correlation ranking. Independent concentration may break near-score ties without changing evidence scores. Calibrated endpoint projection retains original timing; unresolved endpoint identity or multiple reflections has NaN location confidence.";
            parameterMatrix2DOutput=ParameterMatrix2D;pulseEnvelopeMatrix2DOutput=PulseEnvelopeMatrix2D;
            filteredPulseEnvelopeMatrix2DOutput=FilteredPulseEnvelopeMatrix2D;statusMessage=StatusMessage;return true;
        }
        catch(Exception ex)
        {
            Success=false;Error=RuntimeBridge.OneLine(ex);StatusMessage="Task17 sixth input/analysis failed: "+Error;
            StatusJson="{\"ok\":false,\"build\":"+Json(Task17BuildInfo.Version)+",\"algorithm_index\":5,\"stage\":"+Json(stage)+",\"message\":"+Json(Error)+"}";
            if(SeventhMode){StatusJson=A7OutputStatusJson(StatusJson);StatusMessage=StatusMessage.Replace("sixth","seventh");}
            LastResultJson=StatusJson;statusMessage=StatusMessage;
            ParameterMatrix2D=parameterMatrix2DOutput;PulseEnvelopeMatrix2D=pulseEnvelopeMatrix2DOutput;
            FilteredPulseEnvelopeMatrix2D=filteredPulseEnvelopeMatrix2DOutput;return false;
        }
    }
}
