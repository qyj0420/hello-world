using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HVX.Unified.Internal;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    public double[,] FilteredPulseEnvelopeMatrix2D { get; private set; } = new double[0,0];
    public string Algorithm4DiagnosticJson { get; private set; } = "{}";
    public string RecommendedEntryPoint => "HVX.Task17.Location.ProjectLocationAnalyzer.AnalyzeProjectLocationIncremental";
    public string Algorithm4BuildIdentity => Task17BuildInfo.Version+"-index4-quiet-proof-review-and-output-validation";
    public string Algorithm4RebuiltEvidenceWeightsJson => "{\"correlation_evidence\":\"fixed_3mhz_guarded_signed_Pearson\",\"reference\":\"CALfixed^clamp(canonical_delay_seconds/CAL_round_trip_seconds,0,1)\",\"floor\":\"max(CALlower,reference-(CALfixed-CALlower))\",\"fallback_floor\":0.70,\"normalized_correlation_score\":\"clamp(measured/reference,0,1)\",\"full_wave_and_envelope\":\"diagnostic_only\",\"minimum_output_confidence\":0.70,\"confidence_capped_by_measured_correlation\":false,\"nominal_weights\":{\"correlation\":0.50,\"attenuation\":0.35,\"other\":0.15},\"unused_attenuation_weight\":\"redistributed_50_to_15\",\"manual_review_cap\":0.69,\"column21\":\"measured_signed_guarded_3mhz_Pearson\",\"position_history_boost\":0}";
    public double EffectiveAutomaticDetectionThreshold { get; private set; }
    public double EffectiveOutputFilterThreshold { get; private set; }
    public int DetectedEventCountBeforeOutputFilter { get; private set; }
    public int OutputThresholdRejectedCount { get; private set; }
    public string ResolvedMatchedDemFilePath { get; private set; }
    public double ResolvedTanDelta { get; private set; }
    public double[] ResolvedSegmentVelocitiesMPerUs { get; private set; } = new double[0];

    /// <summary>Only recommended LabVIEW entry. Memory input is engineering-value pC;
    /// true never opens LOC, including its header. All companion inputs come from projectPath.</summary>
    public bool AnalyzeProjectLocationIncremental(
        string projectPath, string locationFilePath, double locSampleHz, double[] locData, bool useLocData,
        double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix,
        double[,] previousFilteredPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis,
        int locationAlgorithm, bool useFirstArrivalTiming, bool useAttenuationReference,
        bool usePulseWidthComparison, int thresholdType, double manualThresholdAmplitude,
        double automaticThresholdMultiplier, int multipleReflectionSuppressionCount,
        int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints,
        double initialPhaseDegrees, double oscillationFrequencyHz, bool applyLinearFilter,
        double[] linearFilterCutoffsHz, out double[,] parameterMatrix2DOutput,
        out double[,] pulseEnvelopeMatrix2DOutput, out double[,] filteredPulseEnvelopeMatrix2DOutput,
        out string statusMessage)
    {
        parameterMatrix2DOutput = previousParameterMatrix ?? new double[0,36];
        pulseEnvelopeMatrix2DOutput = previousPulseEnvelopeMatrix ?? new double[0,0];
        filteredPulseEnvelopeMatrix2DOutput = previousFilteredPulseEnvelopeMatrix ?? new double[0,0];
        statusMessage = "";
        Reset();
        equivalentContext=null;equivalentCurrentPackets.Clear();
        reviewCurrentParameterRows.Clear();reviewCurrentRawRows.Clear();reviewCurrentFilteredRows.Clear();
        reviewCurrentPhase=0;reviewCurrentRecordId=locationDataId;
        EffectiveAutomaticDetectionThreshold=EffectiveOutputFilterThreshold=double.NaN;
        DetectedEventCountBeforeOutputFilter=OutputThresholdRejectedCount=0;
        ResolvedMatchedDemFilePath="";ResolvedTanDelta=double.NaN;Algorithm4DiagnosticJson="{}";
        ResolvedSegmentVelocitiesMPerUs=new double[0];
        try
        {
            A4ValidateConnectingCableSetting();
            if (automaticAnalysis && locationAlgorithm == -1) locationAlgorithm = 4;
            if (locationAlgorithm < 0 || locationAlgorithm > 4)
                throw new ArgumentOutOfRangeException("locationAlgorithm","Select an algorithm index in 0..4.");
            if (locationDataId < 0 || locationDataId > 9007199254740991L)
                throw new ArgumentOutOfRangeException("locationDataId","The ID must be an exact nonnegative integer in the double matrix range 0..2^53-1.");
            if (multipleReflectionSuppressionCount < -1) throw new ArgumentOutOfRangeException("multipleReflectionSuppressionCount");
            if (thresholdType < -1 || thresholdType > 1) throw new ArgumentOutOfRangeException("thresholdType");
            if (!Finite(initialPhaseDegrees) || !Finite(oscillationFrequencyHz) || oscillationFrequencyHz < 0)
                throw new ArgumentException("Invalid phase or oscillation frequency.");
            bool defaultProject=String.IsNullOrWhiteSpace(projectPath);
            if(defaultProject&&String.IsNullOrWhiteSpace(locationFilePath))throw new ArgumentException("projectPath and locationFilePath cannot both be empty.");
            string root = defaultProject?Path.GetDirectoryName(Path.GetFullPath(locationFilePath)):Path.GetFullPath(projectPath);
            if (File.Exists(root)) root = Path.GetDirectoryName(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("projectPath must identify the project directory or its ITEM file.");
            string[] files = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly);
            string item = A4Named(files, "ITEM.in", true);
            string cablePath = A4Named(files, "CABLE.in", true);
            string identity;
            if (!string.IsNullOrWhiteSpace(locationFilePath))
                identity = Path.GetFullPath(Path.IsPathRooted(locationFilePath) ? locationFilePath : Path.Combine(root, locationFilePath));
            else
            {
                string[] locs = files.Where(f=>String.Equals(Path.GetExtension(f),".loc",StringComparison.OrdinalIgnoreCase)).ToArray();
                if (locs.Length != 1) throw new ArgumentException("locationFilePath is needed when project LOC identity is not unique.");
                identity = locs[0];
            }
            if (!String.Equals(Path.GetExtension(identity),".loc",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("locationFilePath must retain a .loc record identity.");
            ParsedLocation loc;
            if (useLocData)
            {
                if (!Finite(locSampleHz) || locSampleHz <= 0 || locData == null || locData.Length < 16 || locData.Any(v=>!Finite(v)))
                    throw new ArgumentException("Memory LOC requires positive locSampleHz and at least 16 finite engineering-value samples.");
                loc = new ParsedLocation { Path=identity, Samples=(double[])locData.Clone(), SampleRate=locSampleHz,
                    CableLength=double.NaN, VelocityMPerUs=double.NaN, PhaseDeg=double.NaN };
                ParseMeasurementName(Path.GetFileNameWithoutExtension(identity), out loc.Phase, out loc.VoltageKv);
            }
            else loc = AuditedReadLocation(RequireFile(identity, ".loc", "locationFilePath"));
            if(loc.Phase.Length==0)throw new InvalidDataException("LOC filename requires an explicit A/B/C, L1/L2/L3 or ALL/COMMON phase token before the first underscore.");
            CableMetadata cable = ReadCable(cablePath,loc.Phase);
            // Resolve the current phase geometry BEFORE cross-phase calibration selection.
            // In memory mode loc has no header geometry and no LOC is opened here.
            double targetPhaseLength=FirstPositive(cable.CableLength,loc.CableLength);
            string calPath,calSource,priority,calSelectionDiagnostic;int calCount;
            ParsedCalibration cal=A4ResolvePhaseCalibration(root,loc.Phase,targetPhaseLength,
                out calPath,out calSource,out priority,out calCount,out calSelectionDiagnostic);
            int calibrationPolarity=A4CalibrationReference(cal);
            ProjectMetadata project = ReadProject(item);
            string demName = Path.GetFileNameWithoutExtension(identity)+".dem";
            string demPath = A4MatchedDem(files,identity,loc.Phase,loc.VoltageKv);
            ParsedDem dem = String.IsNullOrEmpty(demPath) ? null : ReadDem(demPath);
            // Public locSampleHz is always Hz. Only metadata may use the legacy
            // sample-interval notation understood by ResolveSampleRate.
            double fs = useLocData ? locSampleHz : locSampleHz>0 ? locSampleHz :
                AuditedResolveSampleRate(loc.SampleRate,cal.SampleRate,dem?.SampleRate ?? double.NaN);
            if(!Finite(fs)||fs<6000000)throw new InvalidDataException("The fixed 3 MHz correlation measurement requires a sample rate of at least 6000000 Hz; unsupported sample rate is not a no-discharge result.");
            bool calibrationGeometryAllowed=cal.Phase==loc.Phase||cal.Phase=="ALL";
            double length = FirstPositive(cable.CableLength,loc.CableLength,calibrationGeometryAllowed?cal.CableLength:double.NaN);
            double velocity = FirstPositive(cable.VelocityMPerUs,loc.VelocityMPerUs,calibrationGeometryAllowed?cal.VelocityMPerUs:double.NaN);
            if (!(length>0) || !Finite(length) || (ConnectingCableLengthM==0&&(!(velocity>0)||!Finite(velocity)))) throw new InvalidDataException("Current-phase cable length and velocity are unavailable; only same-phase/explicit COMMON CABLE, the current LOC header, or same-phase/ALL CAL may supply geometry. Unlabelled ITEM and other-phase geometry are not used.");
            string geometryDiagnostic=A4GeometryDiagnostic(cable,loc,cal,length,velocity);
            if (loc.Samples.Length < 16 || loc.Samples.Any(v=>!Finite(v))) throw new InvalidDataException("LOC samples must be finite and contain at least 16 values.");
            double[] joints = EnsureCableEnds(cable.JointPositionsIncludingEnds,length);
            double[] velocities = A4SegmentVelocities(cable,joints,velocity);
            string connectingCableDiagnostic=A4ApplyConnectingCable(ref cable,cal,calibrationGeometryAllowed,
                ref length,ref velocity,ref joints,ref velocities);
            double roundTripExact = 0;
            for(int k=0;k<velocities.Length;k++) roundTripExact += 2*(joints[k+1]-joints[k])/velocities[k]*1e-6*fs;
            // Validate every geometry before the narrowing conversion. An extreme
            // zero-lead cable can overflow just as a lead-adjusted one can; an
            // unchecked conversion must never silently turn its round trip into 2.
            int roundTrip = A4ValidatedRoundTripSamples(roundTripExact);
            velocity=2*length*1e-6*fs/roundTripExact;
            double[] raw = loc.Samples;
            bool requestedFilter=applyLinearFilter;string filterResolution;
            linearFilterCutoffsHz=A4NormalizeFilter(applyLinearFilter,linearFilterCutoffsHz,fs,out applyLinearFilter,out filterResolution);
            // Remove slow bias before noise estimation / raw amplitude discovery.
            // Preserve original samples for the raw output and keep display filtering separate.
            A4MeasurementSignal measurement=A4PrepareMeasurementSignal(raw,fs,roundTrip);
            double center=measurement.DetectionCenter;
            double peak=measurement.DetectionRaw.Max(v=>Math.Abs(v-center));
            double sigma=Math.Max(measurement.RawSigma,Math.Max(1e-15,peak*1e-8));
            double multiplier = automaticThresholdMultiplier > 0 ? Math.Abs(automaticThresholdMultiplier) : 3.0;
            // F171/F178: output threshold controls must not change physical candidate discovery.
            double discovery = Math.Min(peak,4.5*sigma);
            double rawCenter=center,rawSigma=measurement.RawSigma;
            double rawDiscovery=Math.Min(peak,4.5*rawSigma);
            double outputThreshold = thresholdType == -1 ? Math.Abs(manualThresholdAmplitude) :
                Math.Max(5.0,Math.Sqrt(2*Math.Log(Math.Max(2,raw.Length))))*sigma*(thresholdType==1?Math.Max(0.1,multiplier):1);
            // Automatic threshold may exceed every observed peak: zero
            // candidates is preferable to forcing a noise maximum into output.
            if(!Finite(outputThreshold)) throw new ArgumentException("Output threshold must be finite.");
            double frequency = oscillationFrequencyHz>0?oscillationFrequencyHz:dem?.FrequencyHz??double.NaN;
            A4ValidateCycleSampleRate(loc.CycleReference,fs);
            if(loc.CycleReference!=null){initialPhaseDegrees=loc.CycleReference.InitialPhaseDegrees;frequency=loc.CycleReference.AverageFrequencyHz;}
            ReviewedValidatePhaseClock(frequency,fs,raw.Length);
            initialPhaseDegrees=NormalizeDegrees(initialPhaseDegrees);
            int extensionLimit=A4DisplayExtensionLimit(roundTripExact);
            int pre=A4DisplayExtensionPoints(incidentPreExtensionPoints,extensionLimit);
            int post=A4DisplayExtensionPoints(roundTripPostExtensionPoints,extensionLimit);
            A4Context context = new A4Context { Raw=raw, Signal=measurement.DetectionRaw,MeasurementSignal=measurement, Center=center, RawCenter=rawCenter,RawDiscoveryThreshold=rawDiscovery,
                Sigma=sigma, Threshold=discovery, Fs=fs, Length=length, Velocity=velocity,
                RoundTrip=roundTrip,RoundTripExact=roundTripExact,Joints=joints, Velocities=velocities, Calibration=cal,
                Filtered=applyLinearFilter, FirstArrival=useFirstArrivalTiming,FirstArrivalPhysicalTiming=useFirstArrivalTiming,
                Attenuation=useAttenuationReference, Width=usePulseWidthComparison,
                Suppression=multipleReflectionSuppressionCount,CalibrationPolarity=calibrationPolarity,
                EffectiveCutoffs=linearFilterCutoffsHz,AnalysisLimit=ReviewedAnalysisLimit(maximumDischargeCount),IncidentThreshold=outputThreshold,Algorithm=locationAlgorithm,
                Index4ExtendedGuardSearch=locationAlgorithm==4 };
            equivalentContext=context;
            A4ConfigureAnalysisPhase(context,initialPhaseDegrees,frequency,loc.CycleReference);
            context.ConnectingCableLengthM=ConnectingCableLengthM;
            context.AttenuationTolerancePercent=AttenuationErrorTolerancePercent;
            context.CalibrationToleranceAdaptationLimitPercent=CalibrationToleranceAdaptationLimitPercent;
            projectCurrentVoltage=loc.VoltageKv;projectCurrentAutomatic=automaticAnalysis;
            context.Bands=useAttenuationReference?A4BuildBandReference(context,linearFilterCutoffsHz):null;
            context.LowBand=A4BuildLowBandReference(context);
            // Display cropping must never change filtering, noise estimates, packet identity or matching.
            // Preserve the already validated 200/500-point analysis-context baseline independently.
            List<A4Pulse> pulses = A4DiscoverRawSeedWindows(context,linearFilterCutoffsHz,200,500);
            widthDiagnosticPackets=pulses;
            A4PrepareAttenuationScoring(context,pulses);
            List<A4Pair> pairs = A4Match(context,pulses);
            DetectedEventCountBeforeOutputFilter=pairs.Count;
            List<A4Pair> selected=pairs.Where(p=>A4RawCharge(context,A4ChargeIndex(p.Incident))>=outputThreshold)
                .OrderByDescending(p=>A4RawCharge(context,A4ChargeIndex(p.Incident))).ThenBy(p=>p.Incident.Start).ThenBy(p=>p.Reflection.Start).ToList();
            OutputThresholdRejectedCount=pairs.Count-selected.Count;
            int limit=context.AnalysisLimit;
            MaximumDischargeCountReached=context.AnalysisLimitReached||selected.Count>limit;
            selected=selected.Take(limit).ToList();
            List<double[]> parameterRows = new List<double[]>(), rawRows=new List<double[]>(), filteredRows=new List<double[]>();
            A4ValidatePrevious(previousParameterMatrix,previousPulseEnvelopeMatrix,previousFilteredPulseEnvelopeMatrix);
            int phase=PhaseCode(loc.Phase);
            reviewCurrentPhase=phase;
            int removedLegacyLowQualityRows=0,replacedCurrentRecordRows=0,timingRejectedRows=0;
            if(previousParameterMatrix!=null)
                for(int r=0;r<previousParameterMatrix.GetLength(0);r++)
                    if(previousParameterMatrix[r,0]==phase&&previousParameterMatrix[r,1]==locationDataId)replacedCurrentRecordRows++;
            // Same phase/record ID is replaced atomically. No position-history feedback changes future timing.
            if(previousParameterMatrix!=null)
                for(int r=0;r<previousParameterMatrix.GetLength(0);r++)
                    if(previousParameterMatrix[r,0]!=phase || previousParameterMatrix[r,1]!=locationDataId)
                    {
                        // Explicit historical evidence belongs to the caller.
                        // A new call cannot silently reclassify or delete it using
                        // a new algorithm version without the original samples.
                        parameterRows.Add(A4Row(previousParameterMatrix,r));
                        rawRows.Add(A4Row(previousPulseEnvelopeMatrix,r));
                        filteredRows.Add(A4Row(previousFilteredPulseEnvelopeMatrix,r));
                    }
            int retained=parameterRows.Count;
            var displayPositions=new Dictionary<double[],double>();
            foreach(A4Pair pair in selected)
            {
                A4Pulse a=pair.Incident,b=pair.Reflection;
                int t0,t1;
                if(!A4OutputTiming(context,pair,useFirstArrivalTiming,out t0,out t1))
                    throw new InvalidOperationException(useFirstArrivalTiming?
                        "Reliable first-arrival timing could not be resolved. This record was not localized; no peak timing was substituted.":
                        "A committed peak pair has invalid raw timing identity; no partial output is published.");
                // Display timing changes coordinates only. Physical acceptance and
                // charge compensation retain the canonical peak position.
                int delay=t1-t0;
            double displayPosition=A4BoundaryOutputPosition(delay,fs,joints,velocities,length);
                int displayPeak=A4ChargeIndex(a);
                int start,end;
                if(!A4CompletePdmapCrop(context,displayPeak,t0,pre,post,out start,out end))continue;
                double[] row=NewNaNRow();
                row[0]=phase;row[1]=locationDataId;row[2]=loc.VoltageKv;
                row[3]=context.CycleReference!=null?A4PhaseAt(context,A4ChargeIndex(a)):Finite(frequency)&&frequency>0?NormalizeDegrees(initialPhaseDegrees+360*frequency*A4ChargeIndex(a)/fs):NormalizeDegrees(initialPhaseDegrees);
                row[4]=outputThreshold;row[5]=length;row[6]=velocity;row[7]=fs;row[8]=locationDataId;
                row[9]=parameterRows.Count;row[10]=t0;row[11]=t1;row[12]=t1-t0;row[13]=pair.Position;
                row[14]=roundTrip;row[15]=A4RawCharge(context,A4ChargeIndex(a));
                row[16]=useAttenuationReference?A4ContextCompensateCharge(context,row[15],pair.Position):row[15];
                row[17]=row[15]>0?row[16]/row[15]:1;
                row[18]=a.Energy/fs;row[19]=dem?.TanDelta??double.NaN;row[20]=t0-start;
                row[21]=pair.Correlation;row[22]=A4OutputAttenuationRatio(context,pair.Incident,pair.Reflection);row[23]=a.EquivalentSamples/fs*1e9;
                row[24]=a.BandwidthHz;row[25]=row[19];row[26]=locationAlgorithm;row[27]=multipleReflectionSuppressionCount;
                row[28]=sigma;row[29]=A4RawBaselineAt(context,A4ChargeIndex(a))+outputThreshold;row[30]=A4RawBaselineAt(context,A4ChargeIndex(a))-outputThreshold;
                row[31]=start;row[32]=end;row[33]=pair.Confidence;row[34]=automaticAnalysis?1:0;
                row[35]=pair.Confidence>=A4MinimumOutputConfidence && !pair.Ambiguous && !pair.Blind && !pair.Amplified&&
                    (!context.Attenuation||(pair.CalibrationDeviation!=null&&pair.CalibrationDeviation.Applied&&pair.CalibrationDeviation.Passed))?1:0;
                displayPositions[row]=displayPosition;
                A4RegisterCanonicalOutputIdentity(row,context,pair);
                ReviewedMatrixBudget(parameterRows.Count+1,Math.Max(end-start+1,rawRows.Select(v=>v.Length).DefaultIfEmpty(0).Max()));
                parameterRows.Add(row);rawRows.Add(A4Slice(raw,start,end));filteredRows.Add(A4MatchedFilteredView(context,a,start,end));
                equivalentCurrentPackets[A4RowKey(row)]=a;
                A4RememberWidthPair(row,pair);
            }
            string manualReviewJson=A4CollectManualReview(context,pulses,phase,locationDataId,loc.VoltageKv,frequency,initialPhaseDegrees,automaticAnalysis,pre,post,dem?.TanDelta??double.NaN);
            string quietReviewJson=locationAlgorithm==4?
                A4CollectManualReview(context,pulses,phase,locationDataId,loc.VoltageKv,frequency,initialPhaseDegrees,automaticAnalysis,pre,post,dem?.TanDelta??double.NaN,true):"null";
            A4SortRowsByCharge(parameterRows,rawRows,filteredRows);
            A4CaptureCanonicalPhysicalRows(parameterRows);
            string projectAcceptance=A4ProjectAcceptance(parameterRows,automaticAnalysis);
            foreach(var outputPosition in displayPositions)outputPosition.Key[13]=outputPosition.Value;
            string reviewedWindows=ReviewedWindowJson(context,parameterRows.Where(r=>r[0]==phase&&r[1]==locationDataId));
            ParameterMatrix2D=A4Matrix(parameterRows,36);PulseEnvelopeMatrix2D=A4Matrix(rawRows,0);
            FilteredPulseEnvelopeMatrix2D=A4Matrix(filteredRows,0);
            AddedRowCount=parameterRows.Count-retained;AppliedMultipleReflectionSuppressionCount=context.EffectiveSuppression;
            EffectiveSampleRateHz=fs;EffectiveAutomaticDetectionThreshold=discovery;EffectiveOutputFilterThreshold=outputThreshold;
            AppliedMaximumDischargeCount=limit;DetectedDischargeCountBeforeLimit=pairs.Count;
            EffectiveIncidentPreExtensionPoints=pre;EffectiveRoundTripPostExtensionPoints=post;
            EffectiveInitialPhaseDegrees=NormalizeDegrees(initialPhaseDegrees);EffectiveOscillationFrequencyHz=frequency;
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
            ValidatedRuntimePositionM=parameterRows.Where(r=>r[0]==phase&&r[1]==locationDataId).Select(r=>r[13]).ToArray();
            Algorithm4DiagnosticJson=A4Diagnostics(context,pulses,pairs);
            Success=true;Error="";
            string endpointJson=locationAlgorithm==4?A4EndpointHypothesisJson(context,
                widthDiagnosticPairs.Values.Distinct().ToList(),pulses,context.CandidatePool):
                "{\"enabled\":false,\"reason\":\"not_index4_unchanged\"}";
            StatusJson="{\"ok\":true,\"interface\":\"AnalyzeProjectLocationIncremental\",\"build\":"+Json(Task17BuildInfo.Version)+",\"baseline\":\"2.8.8\",\"location_identity\":"+Json(identity)+
                ",\"project_path_defaulted_from_loc\":"+(defaultProject?"true":"false")+",\"algorithm_index\":"+locationAlgorithm+",\"algorithm_strategy\":"+Json(A4StrategyName(locationAlgorithm))+
                ",\"incremental_output_includes_history\":true,\"incremental_external_concatenation_required\":false,\"record_replacement_key\":\"phase_and_locationDataId\",\"replaced_current_record_rows\":"+replacedCurrentRecordRows+",\"output_timing_rejected_rows\":"+timingRejectedRows+
                ",\"pairing_independent_of_timing_switch\":"+(useFirstArrivalTiming?"false":"true")+",\"timing_point_source\":"+Json(useFirstArrivalTiming?"raw_sustained_leading_edge":"raw_packet_extremum_with_homologous_reflection_polarity")+
                ",\"recommended_type\":\"HVX.Task17.Location.ProjectLocationAnalyzer\",\"project_path\":"+Json(root)+
                ",\"resolved_item\":"+Json(item)+",\"resolved_cable\":"+Json(cablePath)+",\"resolved_calibration\":"+Json(calPath)+
                ",\"optional_calibration\":{\"source\":"+Json(calSource)+",\"priority\":"+Json(priority)+",\"available\":"+(cal.Samples!=null&&cal.Samples.Length>=16?"true":"false")+",\"attenuation_loss_db\":"+NumberJson(cal.AttenuationLossDb)+"}"+
                ",\"phase_geometry\":"+geometryDiagnostic+",\"connecting_cable\":"+connectingCableDiagnostic+",\"phase_calibration_selection\":"+calSelectionDiagnostic+
                ",\"physical_timing_basis\":"+Json(useFirstArrivalTiming?"raw_peak_anchored_leading_edge_pair":"canonical_raw_peaks")+",\"charge_compensation_position_basis\":"+Json(useFirstArrivalTiming?"first_arrival_pair_delay":"canonical_raw_peak_delay")+",\"first_arrival_applied_after_pair_commit\":"+(useFirstArrivalTiming?"false":"true")+",\"out_of_range_display_position\":\"signed_endpoint_extrapolation\""+
                ",\"resolved_dem\":"+Json(demPath??"")+",\"tan_delta\":"+NumberJson(ResolvedTanDelta)+
                ",\"sample_rate_hz\":"+NumberJson(fs)+",\"cable_length_m\":"+NumberJson(length)+
                ",\"display_window\":{\"anchor\":\"raw_incident_absolute_peak_before_filter\",\"start_rule\":\"available_canonical_pre_and_requested_timing_error_window\",\"end_rule\":\"available_max_incident_plus_piecewise_hard_window_plus_post\",\"minimum_physical_roundtrip_coverage_percent\":95,\"requested_pre_points\":"+incidentPreExtensionPoints+",\"requested_post_points\":"+roundTripPostExtensionPoints+
                ",\"effective_pre_points\":"+pre+",\"effective_post_points\":"+post+",\"extension_limit_points\":"+extensionLimit+",\"extension_limit_rule\":\"floor(1.5*round_trip_exact)\",\"independent_of_analysis\":true,\"analysis_context_pre_points\":200,\"analysis_context_post_points\":500,\"outside_analysis_filtered_view\":\"fixed_maximum_display_context_preserving_existing_overlap\"}"+
                ",\"segment_edges_m\":"+NumberArrayJson(joints)+",\"segment_velocities_m_per_us\":"+NumberArrayJson(velocities)+
                ",\"preprocessing\":"+A4MeasurementSignalJson(measurement)+",\"detection_threshold\":"+NumberJson(discovery)+",\"output_threshold\":"+NumberJson(outputThreshold)+
                ",\"phase_filter_and_column3_anchor\":\"canonical_original_incident_peak\",\"automatic_threshold_capped_at_peak\":false,\"window_review\":"+reviewedWindows+
                ",\"coverage_review\":"+ReviewedCoverageJson(context,pulses,maximumDischargeCount)+
                ",\"analysis_phase_filter\":"+A4AnalysisPhaseJson(context)+",\"maximum_analysis_count\":"+limit+",\"analyzed_incident_count\":"+context.AnalysisVisitIds.Count+",\"analysis_limit_reached\":"+(MaximumDischargeCountReached?"true":"false")+
                ",\"raw_maximum_absolute_pc\":"+NumberJson(peak)+",\"filter_requested\":"+(requestedFilter?"true":"false")+",\"filter_enabled\":"+(applyLinearFilter?"true":"false")+
                ",\"effective_filter_cutoffs_hz\":"+NumberArrayJson(linearFilterCutoffsHz)+",\"filter_normalization\":"+Json(filterResolution)+
                ",\"detected_pairs_before_output_filter\":"+pairs.Count+",\"output_threshold_rejected\":"+OutputThresholdRejectedCount+
                ",\"project_acceptance\":"+projectAcceptance+
                ",\"removed_legacy_low_quality_rows\":"+removedLegacyLowQualityRows+",\"retained_history_rows\":"+retained+
                ",\"history_validation\":\"matrix_quality_only; signed_corr_and_lowband_not_revalidated; rerun_all_records_on_version_change\""+
                ",\"loc_body_read\":"+(!useLocData?"true":"false")+",\"selected_signal\":"+Json(applyLinearFilter?"zero_phase_filtered":"raw")+
                ",\"raw_output\":\"signed_raw_loc_window\",\"filtered_output_peak_normalized\":false,\"charge_source\":\"baseline_corrected_raw_incident_peak_before_window_filter\",\"threshold_domain\":\"baseline_corrected_raw_pc\",\"filter_peak_mapping_max_samples\":2,\"correlation_refinement_max_samples_per_named_anchor\":2,\"timing\":"+Json(useFirstArrivalTiming?"raw_sustained_first_arrival":"raw_absolute_peak_homologous_polarity")+
                ",\"output_sort\":\"abs(column15)_descending_global\",\"pulse_id_column9\":\"zero_based_sorted_row_index\",\"source_id_columns\":[1,8]"+
                ",\"column_12_invariant\":\"column11-column10 (zero-based)\",\"suppression_unlimited\":false,\"requested_suppression\":"+multipleReflectionSuppressionCount+
                ",\"effective_suppression\":"+context.EffectiveSuppression+",\"suppression_resolution\":"+Json(context.SuppressionResolution)+
                ",\"use_attenuation_reference\":"+(useAttenuationReference?"true":"false")+",\"use_pulse_width_comparison\":"+(usePulseWidthComparison?"true":"false")+
                ",\"discovered_pulses\":"+pulses.Count+",\"added_rows\":"+AddedRowCount+",\"manual_review\":"+manualReviewJson+",\"quiet_guard_review\":"+quietReviewJson+",\"endpoint_hypotheses\":"+endpointJson+",\"diagnostics\":"+Algorithm4DiagnosticJson+"}";
            LastResultJson=StatusJson;StatusMessage="Task17 Astra rebuild: "+AddedRowCount+" matched pulse pairs; "+context.Suppressed+" owned later echoes suppressed.";
            parameterMatrix2DOutput=ParameterMatrix2D;pulseEnvelopeMatrix2DOutput=PulseEnvelopeMatrix2D;
            filteredPulseEnvelopeMatrix2DOutput=FilteredPulseEnvelopeMatrix2D;statusMessage=StatusMessage;return true;
        }
        catch(Exception ex)
        {
            Success=false;Error=RuntimeBridge.OneLine(ex);StatusMessage="Task17 input/analysis failed: "+Error;
            StatusJson="{\"ok\":false,\"message\":"+Json(Error)+"}";LastResultJson=StatusJson;statusMessage=StatusMessage;
            ParameterMatrix2D=parameterMatrix2DOutput;PulseEnvelopeMatrix2D=pulseEnvelopeMatrix2DOutput;
            FilteredPulseEnvelopeMatrix2D=filteredPulseEnvelopeMatrix2DOutput;return false;
        }
    }

    private static int A4ValidatedRoundTripSamples(double roundTripExact)
    {
        // Downstream discovery guards use up to eight round trips plus padding.
        // Reserve their integer arithmetic before rounding the diagnostic span.
        if(!Finite(roundTripExact)||roundTripExact<=0||roundTripExact>(int.MaxValue-1024)/8d)
            throw new InvalidDataException("Cable round-trip sample span is invalid or too large.");
        return Math.Max(2,checked((int)Math.Round(roundTripExact)));
    }
    private static string A4Named(IEnumerable<string> files,string name,bool required)
    {
        string path=files.FirstOrDefault(f=>String.Equals(Path.GetFileName(f),name,StringComparison.OrdinalIgnoreCase));
        if(required&&path==null)throw new FileNotFoundException("Project top-level "+name+" was not found.");return path;
    }
    private static string A4MatchedDem(string[] files,string identity,string phase,double voltage)
    {
        string exact=A4Named(files,Path.GetFileNameWithoutExtension(identity)+".dem",false);if(exact!=null)return exact;
        DateTime when;if(!A4MeasurementTime(identity,out when))return null;
        return files.Where(f=>String.Equals(Path.GetExtension(f),".dem",StringComparison.OrdinalIgnoreCase)).Select(f=>
        {
            string p;double v;DateTime t;ParseMeasurementName(Path.GetFileNameWithoutExtension(f),out p,out v);
            double diff=A4MeasurementTime(f,out t)?Math.Abs((t-when).TotalSeconds):double.PositiveInfinity;
            return new {Path=f,Phase=p,Voltage=v,Diff=diff};
        }).Where(x=>String.Equals(x.Phase,phase,StringComparison.OrdinalIgnoreCase)&&Math.Abs(x.Voltage-voltage)<1e-6&&x.Diff<=1)
          .OrderBy(x=>x.Diff).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).Select(x=>x.Path).FirstOrDefault();
    }
    private static bool A4MeasurementTime(string path,out DateTime time)
    {
        string fileName=Path.GetFileNameWithoutExtension(path);
        Match compact=Regex.Match(fileName,@"(20\d{12})$");
        if(compact.Success&&DateTime.TryParseExact(compact.Value,"yyyyMMddHHmmss",Inv,System.Globalization.DateTimeStyles.None,out time))return true;
        time=default(DateTime);Match m=Regex.Match(Path.GetFileNameWithoutExtension(path),@"(20\d{2})[-_](\d{2})[-_](\d{2})[-_](\d{2})[':_-](\d{2})[':_-](\d{2})$");
        if(!m.Success)return false;
        try{time=new DateTime(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),int.Parse(m.Groups[4].Value),int.Parse(m.Groups[5].Value),int.Parse(m.Groups[6].Value));return true;}catch{return false;}
    }
    private static int A4CalibrationReference(ParsedCalibration cal)
    {
        double original=cal.AttenuationLossDb;cal.AttenuationLossDb=double.NaN;
        // The ordered comparisons below already reject NaN in the original CLR
        // semantics. Keep that rejection explicit as well: the release control-
        // flow protector must not turn missing timing/rate metadata into a valid
        // waveform reference. These redundant guards do not change finite or
        // infinite input behavior and do not relax calibration acceptance.
        if(cal.Samples!=null&&cal.Samples.Length>16&&
            !Double.IsNaN(cal.FinalTime)&&!Double.IsNaN(cal.StartTime)&&!Double.IsNaN(cal.SampleRate)&&
            cal.FinalTime>cal.StartTime&&cal.SampleRate>0)
        {
            int a=IndexFromTime(cal.StartTime,cal.SampleRate,cal.Samples.Length,0);
            int b=IndexFromTime(cal.FinalTime,cal.SampleRate,cal.Samples.Length,cal.Samples.Length-1);
            // Local packet identity affects amplitude/polarity only. The saved
            // first-arrival/peak clock and all downstream geometry stay intact.
            if(cal.PacketIdentity!=null){cal.PacketIdentity.ValidateBinding(cal.Phase,cal.CalibrationValuePc,cal.SampleRate,cal.Samples,
                cal.CalibrationCoefficient,cal.StartTime,cal.FinalTime);a=cal.PacketIdentity.IncidentPeakIndex;b=cal.PacketIdentity.ReflectionPeakIndex;}
            int radius=Math.Max(2,Math.Min(64,(b-a)/8));double center=Median(cal.Samples);
            int pa=AbsoluteMaximumIndex(cal.Samples,Math.Max(0,a-radius),Math.Min(b-1,a+radius),center);
            int pb=AbsoluteMaximumIndex(cal.Samples,Math.Max(pa+1,b-radius),Math.Min(cal.Samples.Length-1,b+radius),center);
            double va=cal.Samples[pa]-center,vb=cal.Samples[pb]-center;
            if(Math.Abs(va)>1e-12&&Math.Abs(vb)>1e-12)
            {
                double loss=20*Math.Log10(Math.Abs(va/vb));if(Finite(loss)&&loss>=-60&&loss<=120)cal.AttenuationLossDb=loss;
                return va*vb<0?-1:1;
            }
        }
        if(Finite(original)&&original>=-60&&original<=120)cal.AttenuationLossDb=original;
        return 1;
    }
    private static double[] A4SegmentVelocities(CableMetadata cable,double[] joints,double fallback)
    {
        double[] values=Enumerable.Repeat(fallback,joints.Length-1).ToArray();
        for(int i=0;i<values.Length;i++)
        {
            CableSegment segment=cable.Segments.FirstOrDefault(s=>s.StartM<=joints[i]+1e-6&&s.EndM>=joints[i+1]-1e-6);
            if(segment==null)continue;
            string text=segment.InsulationType??"";
            double v;if(A4TryVelocity(text,out v))values[i]=v;
            else if(Regex.IsMatch(text,@"velocity|波速|\bv\s*[:=]|m\s*/",RegexOptions.IgnoreCase))
                throw new InvalidDataException("Invalid or unsupported segment velocity value/unit.");
        }
        return values;
    }
    private static void A4SortRowsByCharge(List<double[]> rows,List<double[]> raw,List<double[]> filtered)
    {
        if(rows.Count!=raw.Count||rows.Count!=filtered.Count)throw new InvalidDataException("Three output matrices lost their row correspondence.");
        // Sort the complete merged history, not just this record. Source IDs and sample indices never change.
        int[] order=Enumerable.Range(0,rows.Count)
            .OrderByDescending(i=>Finite(rows[i][15])?Math.Abs(rows[i][15]):double.NegativeInfinity)
            .ThenBy(i=>rows[i][0]).ThenBy(i=>rows[i][1]).ThenBy(i=>rows[i][10]).ThenBy(i=>rows[i][11])
            .ThenBy(i=>rows[i][26]).ThenBy(i=>i).ToArray();
        double[][] sorted=order.Select(i=>rows[i]).ToArray(),sortedRaw=order.Select(i=>raw[i]).ToArray(),sortedFiltered=order.Select(i=>filtered[i]).ToArray();
        rows.Clear();rows.AddRange(sorted);raw.Clear();raw.AddRange(sortedRaw);filtered.Clear();filtered.AddRange(sortedFiltered);
        for(int i=0;i<rows.Count;i++)rows[i][9]=i;
    }
    private static void A4ValidatePrevious(double[,] p,double[,] raw,double[,] filtered)
    {
        if(p==null||p.GetLength(0)==0)return;
        if(p.GetLength(1)!=36||raw==null||filtered==null||raw.GetLength(0)!=p.GetLength(0)||filtered.GetLength(0)!=p.GetLength(0))
            throw new ArgumentException("Previous 36-column parameters, raw and filtered matrices must have corresponding rows.");
        for(int r=0;r<p.GetLength(0);r++)
            if(!Finite(p[r,1])||p[r,1]<0||p[r,1]>9007199254740991d||p[r,1]!=Math.Floor(p[r,1]))
                throw new ArgumentException("Previous record IDs must be exact nonnegative integers in 0..2^53-1.");
    }
    private static double[] A4Row(double[,] a,int r)
    {double[] b=new double[a.GetLength(1)];for(int c=0;c<b.Length;c++)b[c]=a[r,c];return b;}
    private static double[] A4Slice(double[] a,int start,int end)
    {double[] b=new double[end-start+1];Array.Copy(a,start,b,0,b.Length);return b;}
    private static double[,] A4Matrix(List<double[]> rows,int width)
    {
        int columns=Math.Max(width,rows.Select(r=>r.Length).DefaultIfEmpty(0).Max());double[,] result=new double[rows.Count,columns];
        for(int r=0;r<rows.Count;r++)for(int c=0;c<columns;c++)result[r,c]=c<rows[r].Length?rows[r][c]:double.NaN;
        return result;
    }
    private static double A4Position(int delay,double fs,double[] joints,double[] velocities,double length)
    {
        // Original v2.4.0 piecewise travel-time integrator, traversed from the reflecting far end.
        double[] reversedJoints=new double[joints.Length];double[] reversedVelocities=velocities.Reverse().ToArray();
        for(int i=0;i<joints.Length;i++)reversedJoints[i]=length-joints[joints.Length-1-i];
        return length-PositionFromFirstArrivalDelay(delay,fs,reversedJoints,reversedVelocities,length);
    }
}
