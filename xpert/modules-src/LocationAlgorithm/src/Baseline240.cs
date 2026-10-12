using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HVX.Unified.Internal;

namespace HVX.Task17.Location;

[Obsolete("Legacy compatibility type. New LabVIEW VIs must construct HVX.Task17.Location.ProjectLocationAnalyzer, which exposes only one analysis method.")]
internal sealed partial class LocationEngine
{
	private sealed class ParsedCalibration
	{
		internal string Path;

		internal string Phase;

		internal double CalibrationValuePc;

		internal double CableLength;

		internal double VelocityMPerUs;

		internal double SampleRate;

		internal double CalibrationCoefficient;

		internal double StartTime;

		internal double FinalTime;

		internal double AttenuationLossDb;

		internal double[] Samples;
	}

	private sealed class ParsedLocation
	{
		internal Xiongxin.OWTS.Data.LocationCycleReference CycleReference;
		internal string Path;

		internal string Phase;

		internal double VoltageKv;

		internal double PhaseDeg;

		internal double CableLength;

		internal double VelocityMPerUs;

		internal double SampleRate;

		internal double[] Samples;
	}

	private sealed class ParsedDem
	{
		internal string Path;

		internal double TanDelta;

		internal double FrequencyHz;

		internal double SampleRate;
	}

	private sealed class ProjectMetadata
	{
		internal double CableLength;
	}

	private sealed class CableMetadata
	{
		internal string SelectedPhase = "";
		internal string SelectionSource = "missing_requested_phase";
		internal bool SamePhaseAvailable;
		internal int DuplicateBlocksCollapsed;
		internal string PhaseLengthsJson = "{}";
		internal double CableLength;

		internal double VelocityMPerUs;

		internal List<CableAccessory> Accessories;

		internal List<CableSegment> Segments;

		internal List<double> JointPositionsIncludingEnds;

		internal List<double> InternalJointPositions;
	}

	private sealed class CableAccessory
	{
		internal string Name;

		internal string Type;

		internal double PositionM;
	}

	private sealed class CableSegment
	{
		internal string InsulationType;

		internal double StartM;

		internal double EndM;
	}

	private sealed class LocateResult
	{
		internal int ReflectionIndex;

		internal double PositionM;

		internal double TailError;

		internal double Probability;

		internal double QualityPercent;

		internal bool Accepted;
	}

	private sealed class AlgorithmTimingResult
	{
		internal int ReflectionPeak;

		internal int ReflectionOnset;

		internal int CandidateCount;

		internal int SimilarCandidateCount;

		internal int LaterEchoRejectedCount;

		internal double Score;

		internal double CorrelationScore;

		internal double HighFrequencyConsistencyScore;

		internal double AttenuationConsistencyScore;

		internal double CalibrationMorphologyScore;

		internal double AmbiguityPenalty;

		internal double HistoryBoost;

		internal bool Valid;

		internal bool HighConfidence;

		internal bool FirstArrivalPreserved;
	}

	private sealed class AnalyzedPulse
	{
		internal double[] Source;

		internal int DirectPeak;

		internal int ReflectionPeak;

		internal int DirectOnset;

		internal int ReflectionOnset;

		internal int AmplitudePeak;

		internal int EventBlind;

		internal double AbsoluteAmplitude;

		internal double PositionM;

		internal double ChargePc;

		internal double IncidentWidthSamples;

		internal double ValidatedRuntimePositionM;

		internal double AlgorithmScore;

		internal double AlgorithmAccepted;
	}

	private sealed class PhysicalEventCandidate
	{
		internal double[] Source;

		internal int DirectPeak;

		internal int ReflectionPeak;

		internal int EventBlind;

		internal double AbsoluteAmplitude;
	}

	private sealed class SignalFeature
	{
		internal double PeakAbsolute;

		internal double WidthSamples;

		internal double RiseSamples;

		internal double HighFrequencyDensity;

		internal double PositiveIntegral;

		internal double NegativeIntegral;

		internal double AbsoluteIntegral;
	}

	private sealed class CalibrationReference
	{
		internal bool Valid;

		internal double[] Samples;

		internal double Center;

		internal int DirectPeak;

		internal int ReflectionPeak;

		internal SignalFeature DirectFeature;
	}

	private sealed class MultiFactorCandidate
	{
		internal int Peak;

		internal int Onset;

		internal double PositionM;

		internal double Correlation;

		internal double HighFrequency;

		internal double Attenuation;

		internal double CalibrationMorphology;

		internal double HistoryBoost;

		internal double BaseScore;

		internal double AdjustedScore;
	}

	private sealed class PositionHistoryEvidence
	{
		internal double PositionM;

		internal double ConfidenceSum;

		internal int HighConfidenceCount;
	}

	private readonly object dacEngine;

	private readonly object mixedLocator;

	private readonly object projectFinalizer;

	private readonly object firstArrivalEngine;

	private readonly object calibrationOnsetEngine;

	private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

	private const int MatrixColumns = 36;

	private const int AlgorithmCount = 5;

	private const double LocationStoredIntegerScale = 100000.0;

	private const string AlgorithmNames = "[\"validated_first_arrival_standard\",\"validated_first_arrival_correlation\",\"validated_first_arrival_energy\",\"validated_first_arrival_consensus\",\"six_factor_propagation_evidence\"]";

	private const double Algorithm4CorrelationWeight = 0.45;

	private const double Algorithm4HighFrequencyWeight = 0.23;

	private const double Algorithm4AttenuationWeight = 0.17;

	private const double Algorithm4CalibrationWeight = 0.15;

	private readonly List<PositionHistoryEvidence> algorithm4PositionHistory = new List<PositionHistoryEvidence>();

	private string algorithm4HistoryProjectKey = string.Empty;

	public bool Success { get; private set; }

	public string Error { get; private set; }

	public string StatusJson { get; private set; }

	public string LastResultJson { get; private set; }

	public double[] ParameterMatrix { get; private set; }

	public long ParameterRows { get; private set; }

	public int ParameterColumns { get; private set; }

	public double[] RawWaveformMatrix { get; private set; }

	public long RawWaveformRows { get; private set; }

	public int RawWaveformColumns { get; private set; }

	public double[] FilteredWaveformMatrix { get; private set; }

	public long FilteredWaveformRows { get; private set; }

	public int FilteredWaveformColumns { get; private set; }

	public double[] ProjectCandidateMatrix { get; private set; }

	public long ProjectCandidateRows { get; private set; }

	public int ProjectCandidateColumns { get; private set; }

	public double[] PositionMatrix { get; private set; }

	public int PositionRows { get; private set; }

	public int PositionColumns { get; private set; }

	public double[] ClusterMatrix { get; private set; }

	public int ClusterRows { get; private set; }

	public int ClusterColumns { get; private set; }

	public double[] CandidateAnnotationMatrix { get; private set; }

	public int CandidateAnnotationRows { get; private set; }

	public int CandidateAnnotationColumns { get; private set; }

	public double[,] ParameterMatrix2D { get; private set; }

	public double[,] PulseEnvelopeMatrix2D { get; private set; }

	public string StatusMessage { get; private set; }

	public int AddedRowCount { get; private set; }

	public int AppliedMultipleReflectionSuppressionCount { get; private set; }

	public double EffectiveSampleRateHz { get; private set; }

	public int AppliedMaximumDischargeCount { get; private set; }

	public int DetectedDischargeCountBeforeLimit { get; private set; }

	public bool MaximumDischargeCountReached { get; private set; }

	public int EffectiveIncidentPreExtensionPoints { get; private set; }

	public int EffectiveRoundTripPostExtensionPoints { get; private set; }

	public string ResolvedCalibrationFilePath { get; private set; }

	public string ResolvedTestFilePath { get; private set; }

	public string ResolvedCableFilePath { get; private set; }

	public string ResolvedProjectFilePath { get; private set; }

	public string CalibrationSelectionPriority { get; private set; }

	public double ResolvedCableLengthM { get; private set; }

	public double[] ResolvedJointPositionsIncludingEndsM { get; private set; }

	public double[] ResolvedInternalJointPositionsM { get; private set; }

	public double[] ResolvedSegmentStartPositionsM { get; private set; }

	public double[] ResolvedSegmentEndPositionsM { get; private set; }

	public string ResolvedSegmentInsulationTypesJson { get; private set; }

	public string ResolvedAccessoryTypesJson { get; private set; }

	public double EffectiveInitialPhaseDegrees { get; private set; }

	public double EffectiveOscillationFrequencyHz { get; private set; }

	public double[] ValidatedRuntimePositionM { get; private set; }

	public int LocationAlgorithmCount => 5;

	public string LocationAlgorithmNamesJson => "[\"validated_first_arrival_standard\",\"validated_first_arrival_correlation\",\"validated_first_arrival_energy\",\"validated_first_arrival_consensus\",\"six_factor_propagation_evidence\"]";

	public int Algorithm4HistoryPositionCount => algorithm4PositionHistory.Count;

    [Obsolete("Legacy v2.4.0 metadata only. Use Algorithm4RebuiltEvidenceWeightsJson.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public string Algorithm4WeightsJson => "{\"incident_reflection_correlation\":0.45,\"high_frequency_attenuation_consistency\":0.23,\"exponential_amplitude_and_bipolar_integral_attenuation\":0.17,\"terminal_calibration_morphology\":0.15,\"ambiguous_candidate_penalty\":\"multiplicative\",\"repeated_position_history_boost\":\"0_to_0.12\"}";

	public LocationEngine()
	{
		dacEngine = RuntimeBridge.Create("abf8baa185b4308416932703.V1.0.17.dll", "OWTS.DAC2X.Dac2xEngine");
		mixedLocator = RuntimeBridge.Create("abf8baa185b4308416932703.V1.0.17.dll", "OWTS.DAC2X.MixedCableLocator");
		projectFinalizer = RuntimeBridge.Create("abf8baa185b4308416932703.V1.0.17.dll", "OWTS.DAC2X.Dac2xProjectFinalizer");
		firstArrivalEngine = RuntimeBridge.Create("e0e6f70cda0647b68830372c.V1.0.17.dll", "OWTS.DAC2X.FirstArrivalEngine");
		calibrationOnsetEngine = RuntimeBridge.Create("4a02e6517789139b5c6b673f.V1.0.5.dll", "OWTS.Calibration.CalibrationOnsetEngine");
		Reset();
	}

	public string Health()
	{
		return Convert.ToString(RuntimeBridge.Invoke(dacEngine, "HealthJson"));
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeSingle(double[] locSamples, double[] calSamples, long calibrationDirectIndex, long calibrationReflectionIndex, double sampleRateHz, double voltageKv, double voltagePeriodSamples, double initialPhaseDeg, double[] jointPositionsIncludingEndsM, double[] segmentVelocitiesMPerUs, int peakPreSamples, int peakPostSamples, int thresholdMode, double thresholdSigma, double fixedThresholdValue, int analysisDepth, double blindAreaFraction, double distanceResolutionM, double acceptableErrorM, int maxPulsePairs, double calibrationChargePc, int pressurizationOrder, string locRecordName, int outputMode, string expertConfigJson)
	{
		Reset();
		try
		{
			object result = RuntimeBridge.Invoke(dacEngine, "AnalyzeSingleLocV4", locSamples, calSamples, calibrationDirectIndex, calibrationReflectionIndex, sampleRateHz, voltageKv, voltagePeriodSamples, initialPhaseDeg, jointPositionsIncludingEndsM, segmentVelocitiesMPerUs, peakPreSamples, peakPostSamples, thresholdMode, thresholdSigma, fixedThresholdValue, analysisDepth, blindAreaFraction, distanceResolutionM, acceptableErrorM, maxPulsePairs, calibrationChargePc, pressurizationOrder, locRecordName, outputMode, expertConfigJson);
			CopySingle(result);
			return Success;
		}
		catch (Exception error)
		{
			Error = RuntimeBridge.OneLine(error);
			return false;
		}
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool ResolveFromDelaySamples(double[] delaySamples, double sampleRateHz, double[] jointPositionsIncludingEndsM, double[] segmentVelocitiesMPerUs, double cableLengthM, double toleranceM)
	{
		return CopyMixedSafe("ResolveFromDelaySamplesV1", delaySamples, sampleRateHz, jointPositionsIncludingEndsM, segmentVelocitiesMPerUs, cableLengthM, toleranceM);
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool ResolveFromPairIndices(double[] firstIndices, double[] secondIndices, double sampleRateHz, double[] jointPositionsIncludingEndsM, double[] segmentVelocitiesMPerUs, double cableLengthM, double toleranceM)
	{
		return CopyMixedSafe("ResolveFromPairIndicesV1", firstIndices, secondIndices, sampleRateHz, jointPositionsIncludingEndsM, segmentVelocitiesMPerUs, cableLengthM, toleranceM);
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool FinalizeProject(double[] parameterMatrix, int parameterRows, int parameterColumns, int validLocCountA, int validLocCountB, int validLocCountC, double cableLengthM, double clusterRadiusM, double[] jointPositionsIncludingEndsM, double[] verifiedPositionsM, double verifiedToleranceM)
	{
		Reset();
		try
		{
			object obj = RuntimeBridge.Invoke(projectFinalizer, "FinalizeProjectV1", parameterMatrix, parameterRows, parameterColumns, validLocCountA, validLocCountB, validLocCountC, cableLengthM, clusterRadiusM, jointPositionsIncludingEndsM, verifiedPositionsM, verifiedToleranceM);
			Success = RuntimeBridge.Get(obj, "Success", fallback: false);
			Error = RuntimeBridge.Get(obj, "ErrorJson", string.Empty);
			StatusJson = RuntimeBridge.Get(obj, "StatusJson", string.Empty);
			ClusterMatrix = RuntimeBridge.Get(obj, "ClusterMatrix", new double[0]);
			ClusterRows = RuntimeBridge.Get(obj, "ClusterRows", 0);
			ClusterColumns = RuntimeBridge.Get(obj, "ClusterColumns", 0);
			CandidateAnnotationMatrix = RuntimeBridge.Get(obj, "CandidateAnnotationMatrix", new double[0]);
			CandidateAnnotationRows = RuntimeBridge.Get(obj, "CandidateAnnotationRows", 0);
			CandidateAnnotationColumns = RuntimeBridge.Get(obj, "CandidateAnnotationColumns", 0);
			LastResultJson = RuntimeBridge.ToJson(obj);
			return Success;
		}
		catch (Exception error)
		{
			Error = RuntimeBridge.OneLine(error);
			return false;
		}
	}

	private bool CopyMixedSafe(string method, params object[] arguments)
	{
		Reset();
		try
		{
			object obj = RuntimeBridge.Invoke(mixedLocator, method, arguments);
			Success = RuntimeBridge.Get(obj, "Success", fallback: false);
			Error = RuntimeBridge.Get(obj, "Error", string.Empty);
			StatusJson = RuntimeBridge.Get(obj, "StatusJson", string.Empty);
			PositionMatrix = RuntimeBridge.Get(obj, "PositionMatrix", new double[0]);
			PositionRows = RuntimeBridge.Get(obj, "Rows", 0);
			PositionColumns = RuntimeBridge.Get(obj, "Columns", 0);
			LastResultJson = RuntimeBridge.ToJson(obj);
			return Success;
		}
		catch (Exception error)
		{
			Error = RuntimeBridge.OneLine(error);
			return false;
		}
	}

	private void CopySingle(object result)
	{
		Success = RuntimeBridge.Get(result, "Success", fallback: false);
		Error = RuntimeBridge.Get(result, "ErrorJson", string.Empty);
		StatusJson = RuntimeBridge.Get(result, "StatusJson", string.Empty);
		ParameterMatrix = RuntimeBridge.Get(result, "ParameterMatrix", new double[0]);
		ParameterRows = RuntimeBridge.Get(result, "ParameterRows", 0L);
		ParameterColumns = RuntimeBridge.Get(result, "ParameterColumns", 0);
		RawWaveformMatrix = RuntimeBridge.Get(result, "RawWaveformMatrix", new double[0]);
		RawWaveformRows = RuntimeBridge.Get(result, "RawWaveformRows", 0L);
		RawWaveformColumns = RuntimeBridge.Get(result, "RawWaveformColumns", 0);
		FilteredWaveformMatrix = RuntimeBridge.Get(result, "FilteredWaveformMatrix", new double[0]);
		FilteredWaveformRows = RuntimeBridge.Get(result, "FilteredWaveformRows", 0L);
		FilteredWaveformColumns = RuntimeBridge.Get(result, "FilteredWaveformColumns", 0);
		ProjectCandidateMatrix = RuntimeBridge.Get(result, "ProjectCandidateMatrix", new double[0]);
		ProjectCandidateRows = RuntimeBridge.Get(result, "ProjectCandidateRows", 0L);
		ProjectCandidateColumns = RuntimeBridge.Get(result, "ProjectCandidateColumns", 0);
		LastResultJson = RuntimeBridge.ToJson(result);
	}

	private void Reset()
	{
		Success = false;
		string text = (LastResultJson = string.Empty);
		string error = (StatusJson = text);
		Error = error;
		double[] array = (ProjectCandidateMatrix = new double[0]);
		double[] array3 = (FilteredWaveformMatrix = array);
		double[] parameterMatrix = (RawWaveformMatrix = array3);
		ParameterMatrix = parameterMatrix;
		array3 = (CandidateAnnotationMatrix = new double[0]);
		parameterMatrix = (ClusterMatrix = array3);
		PositionMatrix = parameterMatrix;
		long num = (ProjectCandidateRows = 0L);
		long num3 = (FilteredWaveformRows = num);
		long parameterRows = (RawWaveformRows = num3);
		ParameterRows = parameterRows;
		int num6 = (ProjectCandidateColumns = 0);
		int num8 = (FilteredWaveformColumns = num6);
		int parameterColumns = (RawWaveformColumns = num8);
		ParameterColumns = parameterColumns;
		num6 = (ClusterColumns = 0);
		num8 = (ClusterRows = num6);
		parameterColumns = (PositionColumns = num8);
		PositionRows = parameterColumns;
		parameterColumns = (CandidateAnnotationColumns = 0);
		CandidateAnnotationRows = parameterColumns;
		ResetIncremental();
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncremental(string calibrationFilePath, string locationFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput)
	{
		return AnalyzeLocationFileIncremental(calibrationFilePath, locationFilePath, string.Empty, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, 0, incidentPreExtensionPoints, roundTripPostExtensionPoints, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncremental(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput)
	{
		return AnalyzeLocationFileIncrementalCore(calibrationFilePath, locationFilePath, testFilePath, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints, 0.0, 0.0, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncrementalWithInitialPhase(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, double initialPhaseDegrees, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput)
	{
		return AnalyzeLocationFileIncrementalCore(calibrationFilePath, locationFilePath, testFilePath, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints, initialPhaseDegrees, 0.0, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncrementalWithPhaseReference(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, double initialPhaseDegrees, double oscillationFrequencyHz, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput)
	{
		return AnalyzeLocationFileIncrementalCore(calibrationFilePath, locationFilePath, testFilePath, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints, initialPhaseDegrees, oscillationFrequencyHz, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
	}

	private bool AnalyzeLocationFileIncrementalCore(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, double initialPhaseDegrees, double oscillationFrequencyHz, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput)
	{
		parameterMatrix2DOutput = previousParameterMatrix ?? new double[0, 36];
		pulseEnvelopeMatrix2DOutput = previousPulseEnvelopeMatrix ?? new double[0, 0];
		Reset();
		try
		{
			ValidateModes(locationAlgorithm, thresholdType, sampleRateHz, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints);
			if (!Finite(initialPhaseDegrees))
			{
				throw new ArgumentOutOfRangeException("initialPhaseDegrees", "Initial phase must be finite; use 0 when no phase offset is required.");
			}
			if (!Finite(oscillationFrequencyHz) || oscillationFrequencyHz < 0.0)
			{
				throw new ArgumentOutOfRangeException("oscillationFrequencyHz", "Use 0 for the matched test-file frequency or a positive oscillation frequency.");
			}
			double num = NormalizeDegrees(initialPhaseDegrees);
			int num2 = ValidatePreviousMatrices(previousParameterMatrix, previousPulseEnvelopeMatrix);
			string text = RequireFile(locationFilePath, ".loc", "locationFilePath");
			ParsedLocation parsedLocation = ReadLocation(text);
			string resolvedPath;
			string source;
			string priority;
			int candidateCount;
			ParsedCalibration parsedCalibration = ResolveCalibration(calibrationFilePath, text, projectFilePath, parsedLocation.Phase, out resolvedPath, out source, out priority, out candidateCount);
			string text2 = ResolveCompanionFile(cableFilePath, text, "CABLE.in", required: true);
			string text3 = ResolveCompanionFile(projectFilePath, text, "ITEM.in", required: true);
			ProjectMetadata projectMetadata = ReadProject(text3);
			CableMetadata cableMetadata = ReadCable(text2, parsedLocation.Phase);
			string source2;
			ParsedDem parsedDem = ResolveDem(testFilePath, text, text3, out source2);
			double num3 = ResolveSampleRate(sampleRateHz, parsedLocation.SampleRate, parsedCalibration.SampleRate, parsedDem?.SampleRate ?? double.NaN);
			double num4 = ((oscillationFrequencyHz > 0.0) ? oscillationFrequencyHz : ((parsedDem != null && Finite(parsedDem.FrequencyHz) && parsedDem.FrequencyHz > 0.0) ? parsedDem.FrequencyHz : double.NaN));
			var phaseContext = new A4Context { Fs = num3 };
			A4ConfigureAnalysisPhase(phaseContext, num, num4, parsedLocation.CycleReference);
			if (parsedLocation.CycleReference != null) { num = phaseContext.AnalysisInitialPhaseDegrees; num4 = phaseContext.AnalysisFrequencyHz; }
			double num5 = FirstPositive(parsedLocation.CableLength, cableMetadata.CableLength, parsedCalibration.CableLength, projectMetadata.CableLength);
			double num6 = FirstPositive(parsedLocation.VelocityMPerUs, cableMetadata.VelocityMPerUs, parsedCalibration.VelocityMPerUs);
			if (!Finite(num5) || num5 <= 0.0)
			{
				throw new ArgumentException("Cable length must be greater than zero in LOC, CABLE, CAL or ITEM input.");
			}
			if (!Finite(num6) || num6 <= 0.0)
			{
				throw new ArgumentException("Propagation velocity must be greater than zero in LOC, CABLE or CAL input.");
			}
			if (parsedLocation.Samples.Length < 16)
			{
				throw new InvalidDataException("LOC waveform has fewer than 16 valid samples.");
			}
			double num7 = Median(parsedLocation.Samples);
			double num8 = RobustSigma(parsedLocation.Samples, num7);
			double num9 = ResolveThreshold(parsedLocation.Samples, num7, num8, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier);
			int num10 = ((multipleReflectionSuppressionCount == -1) ? AutoSuppressionCount(parsedCalibration, num8, num9) : multipleReflectionSuppressionCount);
			int num11 = Math.Max(1, (int)Math.Round(num5 / num6 * 1E-06 * num3));
			int num12 = Math.Max(1, (int)Math.Round(2.0 * num5 / num6 * 1E-06 * num3));
			bool flag = incidentPreExtensionPoints == -1 && roundTripPostExtensionPoints == -1;
			int num13 = (flag ? Math.Max(1, (int)Math.Ceiling((double)num11 * 0.5)) : ((incidentPreExtensionPoints == -1) ? num11 : incidentPreExtensionPoints));
			int num14 = ((roundTripPostExtensionPoints == -1) ? num11 : roundTripPostExtensionPoints);
			double[] array = EnsureCableEnds(cableMetadata.JointPositionsIncludingEnds, num5);
			double[] array2 = Enumerable.Repeat(num6, Math.Max(1, array.Length - 1)).ToArray();
			double[] array3 = Enumerable.Repeat((Finite(parsedCalibration.AttenuationLossDb) && parsedCalibration.AttenuationLossDb > 0.0) ? Clamp(parsedCalibration.AttenuationLossDb * 1000.0 / num5, 0.0, 1000.0) : 0.0, array2.Length).ToArray();
			PrepareAlgorithm4History(text3, num5);
			int num15 = ((thresholdType == -1 && Finite(manualThresholdAmplitude) && manualThresholdAmplitude > 0.0) ? 1 : 0);
			double num16 = ((num15 == 1) ? Math.Abs(manualThresholdAmplitude) : Clamp(num9 / Math.Max(num8, 1E-12), 2.0, 50.0));
			double num17 = ((Finite(parsedLocation.VoltageKv) && Math.Abs(parsedLocation.VoltageKv) > 1E-12) ? Math.Abs(parsedLocation.VoltageKv) : 1E-09);
			object instance = RuntimeBridge.Invoke(firstArrivalEngine, "AnalyzeSingleLocThresholdDetailedV2", parsedLocation.Samples, num3, array2, array3, parsedCalibration.Samples, array, new double[1] { num17 }, Math.Max(0L, locationDataId), num15, num16, false, num10 != 0, PhaseCode(parsedLocation.Phase), parsedCalibration.CalibrationValuePc);
			if (!RuntimeBridge.Get(instance, "Success", fallback: false))
			{
				string text4 = RuntimeBridge.Get(instance, "ErrorJson", string.Empty);
				throw new InvalidOperationException("Task17 validated first-arrival analysis failed: " + text4);
			}
			double[] array4 = RuntimeBridge.Get(instance, "PulseParameterMatrix", new double[0]);
			int num18 = RuntimeBridge.Get(instance, "PulseParameterColumns", 0);
			long val = RuntimeBridge.Get(instance, "PulseParameterRows", 0L);
			int num19 = checked((int)Math.Min(2147483647L, Math.Max(0L, val)));
			if (num18 < 36 || array4.Length < num19 * num18)
			{
				throw new InvalidDataException("Task17 validated first-arrival runtime returned an invalid parameter matrix.");
			}
			int num20 = (automaticAnalysis ? 512 : 128);
			int num21 = ((maximumDischargeCount > 0) ? maximumDischargeCount : num20);
			List<PhysicalEventCandidate> list = new List<PhysicalEventCandidate>();
			int num22 = 0;
			int num23 = 0;
			int num24 = 0;
			int num25 = 0;
			double num26 = 0.0;
			double num27 = 0.0;
			double num28 = 0.0;
			double num29 = 0.0;
			double num30 = 0.0;
			double num31 = 0.0;
			double num32 = 0.0;
			int num33 = 0;
			int num34 = 0;
			int num35 = 0;
			int num36 = 0;
			int num37 = 0;
			int num38 = 0;
			string text5 = string.Empty;
			int num39 = int.MaxValue;
			int num40 = 0;
			int num41 = int.MaxValue;
			int num42 = 0;
			int num43 = int.MaxValue;
			int num44 = 0;
			int num45 = 0;
			for (int i = 0; i < num19; i++)
			{
				int sourceIndex = i * num18;
				double[] array5 = new double[num18];
				Array.Copy(array4, sourceIndex, array5, 0, num18);
				int num46 = ClampIndex((int)Math.Round(array5[10]), parsedLocation.Samples.Length);
				int num47 = ClampIndex((int)Math.Round(array5[11]), parsedLocation.Samples.Length);
				if (num47 <= num46)
				{
					continue;
				}
				int num48 = ((num18 > 104 && Finite(array5[104])) ? Math.Max(1, (int)Math.Round(array5[104])) : Math.Max(3, num12 / 200));
				num39 = Math.Min(num39, num48);
				num40 = Math.Max(num40, num48);
				int val2 = Math.Max(3, (int)Math.Round((double)num12 * 0.06));
				int val3 = Math.Max(3, num12 / 4);
				int num49 = Math.Max(num48, Math.Min(val3, val2));
				num43 = Math.Min(num43, num49);
				num44 = Math.Max(num44, num49);
				int num50 = Math.Max(0, num46 - num49 / 2);
				int num51 = Math.Min(num47 - 1, Math.Min(parsedLocation.Samples.Length - 1, num46 + num49));
				if (num51 < num50)
				{
					continue;
				}
				int num52 = AbsoluteMaximumIndex(parsedLocation.Samples, num50, num51, num7);
				if (!A4AnalysisPhaseEligible(phaseContext, num52)) continue;
				double num53 = Math.Abs(parsedLocation.Samples[num52] - num7);
				if (num53 < num9)
				{
					num45++;
					continue;
				}
				int num54 = Math.Max(num52 + 1, num47 - num49 / 2);
				int num55 = Math.Min(parsedLocation.Samples.Length - 1, num47 + num49);
				if (num55 >= num54)
				{
					int reflectionPeak = AbsoluteMaximumIndex(parsedLocation.Samples, num54, num55, num7);
					PhysicalEventCandidate candidate = new PhysicalEventCandidate();
					candidate.Source = array5;
					candidate.DirectPeak = num52;
					candidate.ReflectionPeak = reflectionPeak;
					candidate.AbsoluteAmplitude = num53;
					candidate.EventBlind = num49;
					int num56 = list.FindIndex((PhysicalEventCandidate existing) => Math.Abs(existing.DirectPeak - candidate.DirectPeak) <= Math.Max(existing.EventBlind, candidate.EventBlind));
					if (num56 < 0)
					{
						list.Add(candidate);
					}
					else if (candidate.AbsoluteAmplitude > list[num56].AbsoluteAmplitude)
					{
						list[num56] = candidate;
					}
				}
			}
			int num57 = 0;
			if (num10 != 0 && list.Count > 1)
			{
				List<PhysicalEventCandidate> list2 = new List<PhysicalEventCandidate>();
				foreach (PhysicalEventCandidate candidate2 in list.OrderBy((PhysicalEventCandidate x) => x.DirectPeak))
				{
					if (list2.Any((PhysicalEventCandidate prior) => Math.Abs(candidate2.DirectPeak - prior.ReflectionPeak) <= Math.Max(candidate2.EventBlind, prior.EventBlind)))
					{
						num57++;
					}
					else
					{
						list2.Add(candidate2);
					}
				}
				list = list2;
			}
			list = (from x in list
				orderby x.AbsoluteAmplitude descending, x.DirectPeak
				select x).ToList();
			int count = list.Count;
			bool flag2 = list.Count > num21;
			if (flag2)
			{
				list = list.Take(num21).ToList();
			}
			List<AnalyzedPulse> list3 = new List<AnalyzedPulse>();
			foreach (PhysicalEventCandidate item in list)
			{
				double[] source3 = item.Source;
				int directPeak = item.DirectPeak;
				int reflectionPeak2 = item.ReflectionPeak;
				double absoluteAmplitude = item.AbsoluteAmplitude;
				int eventBlind = item.EventBlind;
				int num58 = directPeak;
				int num59 = reflectionPeak2;
				int val4 = eventBlind;
				double incidentWidthSamples = double.NaN;
				bool flag3 = false;
				try
				{
					int num60 = Math.Max(num11, eventBlind * 4);
					int num61 = Math.Max(0, directPeak - num60);
					double[] array6 = new double[Math.Min(parsedLocation.Samples.Length - 1, reflectionPeak2 + num60) - num61 + 1];
					Array.Copy(parsedLocation.Samples, num61, array6, 0, array6.Length);
					object instance2 = RuntimeBridge.Invoke(calibrationOnsetEngine, "AnalyzeConfiguredWithDischargeCalibration", (long)(directPeak - num61), (long)(reflectionPeak2 - num61), array6, num3, num5, 0.06, 0.7, 0.3, true, absoluteAmplitude);
					checked
					{
						val4 = Math.Max(eventBlind, (int)Math.Min(2147483647L, RuntimeBridge.Get(instance2, "TimeDomainBlindZoneSamples", unchecked((long)eventBlind))));
					}
					if (RuntimeBridge.Get(instance2, "Success", fallback: false))
					{
						long[] array7 = RuntimeBridge.Get(instance2, "StartIndicesInteger", new long[0]);
						if (array7.Length >= 2 && array7[0] >= 0 && array7[1] > array7[0] && array7[1] < array6.Length)
						{
							num58 = num61 + (int)array7[0];
							num59 = num61 + (int)array7[1];
							num22++;
							flag3 = true;
						}
						incidentWidthSamples = RuntimeBridge.Get(instance2, "IncidentPulseWidthSamples", double.NaN);
					}
					else if (text5.Length == 0)
					{
						text5 = RuntimeBridge.Get(instance2, "ErrorJson", "task5 returned Success=false");
					}
				}
				catch (Exception error)
				{
					if (text5.Length == 0)
					{
						text5 = RuntimeBridge.OneLine(error);
					}
				}
				if (!flag3)
				{
					num58 = FindFirstArrivalLikeTask5(parsedLocation.Samples, directPeak, eventBlind, num7, num8);
					num59 = FindFirstArrivalLikeTask5(parsedLocation.Samples, reflectionPeak2, eventBlind, num7, num8);
					if (num59 <= num58)
					{
						continue;
					}
					num23++;
				}
				int num62 = num59;
				AlgorithmTimingResult algorithmTimingResult = RefineReflectionTiming(locationAlgorithm, parsedLocation.Samples, num7, num8, num9, num58, directPeak, num59, reflectionPeak2, Math.Max(eventBlind, val4), num12, parsedCalibration, num3, num5);
				if (locationAlgorithm != 0)
				{
					if (algorithmTimingResult.Valid)
					{
						num59 = algorithmTimingResult.ReflectionOnset;
						reflectionPeak2 = algorithmTimingResult.ReflectionPeak;
						num24++;
						num26 += algorithmTimingResult.Score;
						if (locationAlgorithm == 4)
						{
							num33++;
							num27 += algorithmTimingResult.CorrelationScore;
							num28 += algorithmTimingResult.HighFrequencyConsistencyScore;
							num29 += algorithmTimingResult.AttenuationConsistencyScore;
							num30 += algorithmTimingResult.CalibrationMorphologyScore;
							num31 += algorithmTimingResult.AmbiguityPenalty;
							num32 += algorithmTimingResult.HistoryBoost;
							if (algorithmTimingResult.SimilarCandidateCount > 1)
							{
								num34++;
							}
							if (algorithmTimingResult.HistoryBoost > 0.0)
							{
								num35++;
							}
							if (algorithmTimingResult.HighConfidence)
							{
								num36++;
							}
							num37 += algorithmTimingResult.LaterEchoRejectedCount;
							if (algorithmTimingResult.FirstArrivalPreserved)
							{
								num38++;
							}
						}
					}
					else
					{
						num25++;
					}
				}
				num41 = Math.Min(num41, val4);
				num42 = Math.Max(num42, val4);
				int num63 = Math.Max(eventBlind, val4);
				int num64 = Math.Min(num59 - 1, Math.Min(parsedLocation.Samples.Length - 1, Math.Max(directPeak, num58) + num63));
				if (num64 < num58)
				{
					num64 = Math.Max(num58, directPeak);
				}
				int num65 = AbsoluteMaximumIndex(parsedLocation.Samples, num58, num64, num7);
				double num66 = Math.Abs(parsedLocation.Samples[num65] - num7);
				if (num66 < num9)
				{
					num45++;
					continue;
				}
				double num67 = PositionFromFirstArrivalDelay(num59 - num58, num3, array, array2, num5);
				double num68 = PositionFromFirstArrivalDelay(num62 - num58, num3, array, array2, num5);
				double num69 = ((source3.Length > 13) ? source3[13] : double.NaN);
				double positionM = num67;
				if (Finite(num69) && num69 >= 0.0 && num69 <= num5)
				{
					double num70 = Math.Abs(num69 - num68);
					double num71 = Math.Abs(num69 - (num5 - num68));
					double num72 = ((num70 <= num71) ? 1.0 : (-1.0)) * (num67 - num68);
					double num73 = Math.Max(1.0, Math.Min(5.0, num5 * 0.01));
					if (Math.Abs(num72) > num73)
					{
						num72 = 0.0;
					}
					positionM = Clamp(num69 + num72, 0.0, num5);
				}
				double chargePc = num66;
				AnalyzedPulse pulse = new AnalyzedPulse();
				pulse.Source = source3;
				pulse.DirectPeak = directPeak;
				pulse.ReflectionPeak = reflectionPeak2;
				pulse.DirectOnset = num58;
				pulse.ReflectionOnset = num59;
				pulse.AmplitudePeak = num65;
				pulse.AbsoluteAmplitude = num66;
				pulse.EventBlind = num63;
				pulse.PositionM = positionM;
				pulse.ChargePc = chargePc;
				pulse.IncidentWidthSamples = incidentWidthSamples;
				pulse.ValidatedRuntimePositionM = num69;
				pulse.AlgorithmScore = ((locationAlgorithm != 4) ? (algorithmTimingResult.Valid ? algorithmTimingResult.Score : ((source3.Length > 33) ? source3[33] : double.NaN)) : (algorithmTimingResult.Valid ? algorithmTimingResult.Score : 0.0));
				pulse.AlgorithmAccepted = ((locationAlgorithm != 4) ? ((source3.Length > 35) ? source3[35] : 1.0) : ((algorithmTimingResult.Valid && algorithmTimingResult.Score >= 0.72) ? 1.0 : 0.0));
				int num74 = list3.FindIndex((AnalyzedPulse existing) => Math.Abs(existing.DirectOnset - pulse.DirectOnset) <= Math.Max(existing.EventBlind, pulse.EventBlind));
				if (num74 < 0)
				{
					list3.Add(pulse);
				}
				else if (pulse.AbsoluteAmplitude > list3[num74].AbsoluteAmplitude)
				{
					list3[num74] = pulse;
				}
			}
			if (num10 != 0 && list3.Count > 1)
			{
				List<AnalyzedPulse> list4 = new List<AnalyzedPulse>();
				foreach (AnalyzedPulse pulse2 in list3.OrderBy((AnalyzedPulse x) => x.DirectOnset))
				{
					if (list4.Any((AnalyzedPulse prior) => Math.Abs(pulse2.DirectOnset - prior.ReflectionOnset) <= Math.Max(pulse2.EventBlind, prior.EventBlind)))
					{
						num57++;
					}
					else
					{
						list4.Add(pulse2);
					}
				}
				list3 = list4;
			}
			list3 = (from x in list3
				orderby x.AbsoluteAmplitude descending, x.DirectOnset
				select x).ToList();
			ValidatedRuntimePositionM = list3.Select((AnalyzedPulse x) => x.ValidatedRuntimePositionM).ToArray();
			double num75 = parsedDem?.TanDelta ?? double.NaN;
			List<double[]> list5 = new List<double[]>();
			List<double[]> list6 = new List<double[]>();
			for (int num76 = 0; num76 < list3.Count; num76++)
			{
				AnalyzedPulse analyzedPulse = list3[num76];
				int num77 = Math.Max(0, analyzedPulse.DirectOnset - num13);
				int num78 = Math.Min(parsedLocation.Samples.Length - 1, analyzedPulse.DirectOnset + num12 + num14);
				if (num78 < num77)
				{
					num78 = num77;
				}
				double[] array8 = new double[num78 - num77 + 1];
				Array.Copy(parsedLocation.Samples, num77, array8, 0, array8.Length);
				list6.Add(array8);
				double[] array9 = NewNaNRow();
				Array.Copy(analyzedPulse.Source, array9, Math.Min(36, analyzedPulse.Source.Length));
				array9[0] = PhaseCode(parsedLocation.Phase);
				array9[1] = locationDataId;
				array9[2] = parsedLocation.VoltageKv;
				array9[3] = parsedLocation.CycleReference != null ? A4PhaseAt(phaseContext, analyzedPulse.DirectOnset) :
					((Finite(num4) && num4 > 0.0) ? A4PhaseAt(phaseContext, analyzedPulse.DirectOnset) : (Finite(parsedLocation.PhaseDeg) ? NormalizeDegrees(parsedLocation.PhaseDeg) : num));
				array9[4] = num9;
				array9[5] = num5;
				array9[6] = num6;
				array9[7] = num3;
				array9[8] = locationDataId;
				array9[9] = num2 + list5.Count;
				array9[10] = analyzedPulse.DirectOnset;
				array9[11] = analyzedPulse.ReflectionOnset;
				array9[12] = analyzedPulse.ReflectionOnset - analyzedPulse.DirectOnset;
				array9[13] = analyzedPulse.PositionM;
				array9[14] = num12;
				array9[15] = analyzedPulse.ChargePc;
				array9[16] = CompensateCharge(analyzedPulse.ChargePc, analyzedPulse.PositionM, num5, parsedCalibration.AttenuationLossDb);
				array9[20] = analyzedPulse.DirectOnset - num77;
				array9[23] = (Finite(analyzedPulse.IncidentWidthSamples) ? (analyzedPulse.IncidentWidthSamples / num3 * 1000000000.0) : PulseWidthNsAbsolute(parsedLocation.Samples, analyzedPulse.AmplitudePeak, num7, Math.Max(num9 * 0.5, analyzedPulse.AbsoluteAmplitude * 0.5), num3));
				array9[24] = parsedDem?.FrequencyHz ?? double.NaN;
				array9[25] = num75;
				array9[26] = locationAlgorithm;
				array9[27] = num10;
				array9[28] = num8;
				array9[29] = num7 + num9;
				array9[30] = num7 - num9;
				array9[31] = num77;
				array9[32] = num78;
				array9[33] = analyzedPulse.AlgorithmScore;
				array9[34] = (automaticAnalysis ? 1.0 : 0.0);
				array9[35] = analyzedPulse.AlgorithmAccepted;
				list5.Add(array9);
			}
			ParameterMatrix2D = AppendParameters(previousParameterMatrix, list5);
			PulseEnvelopeMatrix2D = AppendEnvelopes(previousPulseEnvelopeMatrix, list6);
			AddedRowCount = list5.Count;
			AppliedMultipleReflectionSuppressionCount = num10;
			EffectiveSampleRateHz = num3;
			AppliedMaximumDischargeCount = num21;
			DetectedDischargeCountBeforeLimit = count;
			MaximumDischargeCountReached = flag2;
			EffectiveIncidentPreExtensionPoints = num13;
			EffectiveRoundTripPostExtensionPoints = num14;
			EffectiveInitialPhaseDegrees = num;
			EffectiveOscillationFrequencyHz = num4;
			ResolvedCalibrationFilePath = resolvedPath;
			ResolvedTestFilePath = ((parsedDem == null) ? string.Empty : parsedDem.Path);
			ResolvedCableFilePath = text2;
			ResolvedProjectFilePath = text3;
			CalibrationSelectionPriority = priority;
			ResolvedCableLengthM = num5;
			ResolvedJointPositionsIncludingEndsM = cableMetadata.JointPositionsIncludingEnds.ToArray();
			ResolvedInternalJointPositionsM = cableMetadata.InternalJointPositions.ToArray();
			ResolvedSegmentStartPositionsM = cableMetadata.Segments.Select((CableSegment x) => x.StartM).ToArray();
			ResolvedSegmentEndPositionsM = cableMetadata.Segments.Select((CableSegment x) => x.EndM).ToArray();
			ResolvedSegmentInsulationTypesJson = StringArrayJson(cableMetadata.Segments.Select((CableSegment x) => x.InsulationType));
			ResolvedAccessoryTypesJson = StringArrayJson(cableMetadata.Accessories.Select((CableAccessory x) => x.Type));
			ParameterMatrix = Flatten(ParameterMatrix2D);
			ParameterRows = ParameterMatrix2D.GetLength(0);
			ParameterColumns = 36;
			FilteredWaveformMatrix = Flatten(PulseEnvelopeMatrix2D);
			FilteredWaveformRows = PulseEnvelopeMatrix2D.GetLength(0);
			FilteredWaveformColumns = PulseEnvelopeMatrix2D.GetLength(1);
			Success = true;
			Error = string.Empty;
			StatusJson = BuildStatusJson(text, resolvedPath, source, priority, candidateCount, (parsedDem == null) ? string.Empty : parsedDem.Path, source2, text2, text3, cableMetadata, list5.Count, num2, locationAlgorithm, num3, num10, num9, num75, maximumDischargeCount, num21, count, flag2, incidentPreExtensionPoints, roundTripPostExtensionPoints, num13, num14, num11, num19, num22, num23, num24, num25, (num24 > 0) ? (num26 / (double)num24) : double.NaN, text5, (num39 != int.MaxValue) ? num39 : 0, num40, (num41 != int.MaxValue) ? num41 : 0, num42, (num43 != int.MaxValue) ? num43 : 0, num44, num45, num57, num, oscillationFrequencyHz, num4, flag, BuildAlgorithm4MetricsJson(num33, num27, num28, num29, num30, num31, num32, num34, num35, num36, algorithm4PositionHistory.Count, num37, num38));
			if (parsedLocation.CycleReference != null)
				StatusJson = StatusJson.Replace("normalize(initial_phase_deg+360*oscillation_frequency_hz*direct_first_arrival_sample/sample_rate_hz)",
					"piecewise_linear_phase_between_recorded_alternating_zero_crossings")
					.Replace("loc_recorded_phase_when_no_frequency_is_available", "invalid_recorded_cycle_reference_is_rejected");
			StatusJson = StatusJson.Substring(0, StatusJson.Length - 1) + ",\"analysis_phase_filter\":" + A4AnalysisPhaseJson(phaseContext) + "}";
			LastResultJson = StatusJson;
			StatusMessage = BuildIncrementalStatusMessage();
			parameterMatrix2DOutput = ParameterMatrix2D;
			pulseEnvelopeMatrix2DOutput = PulseEnvelopeMatrix2D;
			return true;
		}
		catch (Exception error2)
		{
			ParameterMatrix2D = previousParameterMatrix ?? new double[0, 36];
			PulseEnvelopeMatrix2D = previousPulseEnvelopeMatrix ?? new double[0, 0];
			parameterMatrix2DOutput = ParameterMatrix2D;
			pulseEnvelopeMatrix2DOutput = PulseEnvelopeMatrix2D;
			Success = false;
			Error = RuntimeBridge.OneLine(error2);
			StatusJson = "{\"ok\":false,\"interface\":\"AnalyzeLocationFileIncremental\",\"message\":" + Json(Error) + "}";
			LastResultJson = StatusJson;
			StatusMessage = BuildIncrementalStatusMessage();
			return false;
		}
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncrementalWithStatus(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput, out string statusMessage)
	{
		bool result = AnalyzeLocationFileIncremental(calibrationFilePath, locationFilePath, testFilePath, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
		statusMessage = StatusMessage;
		return result;
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public bool AnalyzeLocationFileIncrementalWithStatusAndPhaseReference(string calibrationFilePath, string locationFilePath, string testFilePath, string cableFilePath, string projectFilePath, double[,] previousParameterMatrix, double[,] previousPulseEnvelopeMatrix, long locationDataId, bool automaticAnalysis, int locationAlgorithm, double sampleRateHz, int thresholdType, double manualThresholdAmplitude, double automaticThresholdMultiplier, int multipleReflectionSuppressionCount, int maximumDischargeCount, int incidentPreExtensionPoints, int roundTripPostExtensionPoints, double initialPhaseDegrees, double oscillationFrequencyHz, out double[,] parameterMatrix2DOutput, out double[,] pulseEnvelopeMatrix2DOutput, out string statusMessage)
	{
		bool result = AnalyzeLocationFileIncrementalWithPhaseReference(calibrationFilePath, locationFilePath, testFilePath, cableFilePath, projectFilePath, previousParameterMatrix, previousPulseEnvelopeMatrix, locationDataId, automaticAnalysis, locationAlgorithm, sampleRateHz, thresholdType, manualThresholdAmplitude, automaticThresholdMultiplier, multipleReflectionSuppressionCount, maximumDischargeCount, incidentPreExtensionPoints, roundTripPostExtensionPoints, initialPhaseDegrees, oscillationFrequencyHz, out parameterMatrix2DOutput, out pulseEnvelopeMatrix2DOutput);
		statusMessage = StatusMessage;
		return result;
	}

	private static string RequireFile(string path, string extension, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException(parameterName + " is required.");
		}
		string fullPath = Path.GetFullPath(path.Trim());
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException(parameterName + " was not found.", fullPath);
		}
		if (!string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(parameterName + " must be a " + extension + " file.");
		}
		return fullPath;
	}

	private static string ResolveCompanionFile(string path, string locPath, string expectedName, bool required)
	{
		if (!string.IsNullOrWhiteSpace(path))
		{
			string text = Path.GetFullPath(path.Trim());
			if (Directory.Exists(text))
			{
				text = FindNamedFile(text, expectedName);
			}
			if (!string.IsNullOrEmpty(text) && File.Exists(text))
			{
				return text;
			}
			if (required)
			{
				throw new FileNotFoundException(expectedName + " was not found.", text);
			}
			return string.Empty;
		}
		string text2 = FindNamedFile(Path.GetDirectoryName(locPath), expectedName);
		if (!string.IsNullOrEmpty(text2))
		{
			return text2;
		}
		if (required)
		{
			throw new FileNotFoundException(expectedName + " was not found in the LOC directory.", Path.Combine(Path.GetDirectoryName(locPath), expectedName));
		}
		return string.Empty;
	}

	private static string FindNamedFile(string directory, string expectedName)
	{
		if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
		{
			return string.Empty;
		}
		string path = Path.Combine(directory, expectedName);
		if (File.Exists(path))
		{
			return Path.GetFullPath(path);
		}
		return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault((string x) => string.Equals(Path.GetFileName(x), expectedName, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
	}

	private static ParsedCalibration ResolveCalibration(string explicitPath, string locPath, string projectPath, string requestedPhase, out string resolvedPath, out string source, out string priority, out int candidateCount)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath))
		{
			resolvedPath = RequireFile(explicitPath, ".cal", "calibrationFilePath");
			source = "explicit_input";
			priority = "explicit_input";
			candidateCount = 1;
			return ReadCalibration(resolvedPath);
		}
		List<string> list = new List<string> { Path.GetDirectoryName(locPath) };
		if (!string.IsNullOrWhiteSpace(projectPath))
		{
			string fullPath = Path.GetFullPath(projectPath.Trim());
			list.Add(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath));
		}
		List<ParsedCalibration> list2 = new List<ParsedCalibration>();
		foreach (string item in list.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			foreach (string item2 in from x in Directory.EnumerateFiles(item, "*", SearchOption.TopDirectoryOnly)
				where string.Equals(Path.GetExtension(x), ".cal", StringComparison.OrdinalIgnoreCase)
				select x)
			{
				try
				{
					list2.Add(ReadCalibration(item2));
				}
				catch
				{
				}
			}
		}
		candidateCount = list2.Count;
		if (candidateCount == 0)
		{
			throw new FileNotFoundException("calibrationFilePath is blank and no usable CAL file was found in the LOC/project directory.");
		}
		string phase = NormalizePhase(requestedPhase);
		List<ParsedCalibration> same = list2.Where((ParsedCalibration x) => string.Equals(x.Phase, phase, StringComparison.OrdinalIgnoreCase)).ToList();
		List<ParsedCalibration> all = list2.Where((ParsedCalibration x) => string.Equals(x.Phase, "ALL", StringComparison.OrdinalIgnoreCase)).ToList();
		List<ParsedCalibration> candidates = list2.Where((ParsedCalibration x) => !same.Contains(x) && !all.Contains(x)).ToList();
		ParsedCalibration parsedCalibration = SelectCalibrationExtreme(same, maximum: true);
		priority = "same_phase_max";
		if (parsedCalibration == null)
		{
			parsedCalibration = SelectCalibrationExtreme(same, maximum: false);
			priority = "same_phase_min";
		}
		if (parsedCalibration == null)
		{
			parsedCalibration = SelectCalibrationExtreme(all, maximum: true);
			priority = "all_phase_max";
		}
		if (parsedCalibration == null)
		{
			parsedCalibration = SelectCalibrationExtreme(all, maximum: false);
			priority = "all_phase_min";
		}
		if (parsedCalibration == null)
		{
			parsedCalibration = SelectCalibrationExtreme(candidates, maximum: true);
			priority = "other_phase_max";
		}
		if (parsedCalibration == null)
		{
			parsedCalibration = SelectCalibrationExtreme(candidates, maximum: false);
			priority = "other_phase_min";
		}
		if (parsedCalibration == null)
		{
			throw new InvalidDataException("No usable calibration candidate remained after phase-priority selection.");
		}
		resolvedPath = parsedCalibration.Path;
		source = "automatic_search";
		return parsedCalibration;
	}

	private static ParsedCalibration SelectCalibrationExtreme(List<ParsedCalibration> candidates, bool maximum)
	{
		if (candidates == null || candidates.Count == 0)
		{
			return null;
		}
		return (maximum ? candidates.OrderByDescending((ParsedCalibration x) => (!Finite(x.CalibrationValuePc)) ? double.MinValue : x.CalibrationValuePc) : candidates.OrderBy((ParsedCalibration x) => (!Finite(x.CalibrationValuePc)) ? double.MaxValue : x.CalibrationValuePc)).ThenBy((ParsedCalibration x) => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
	}

	private static ParsedDem ResolveDem(string explicitPath, string locPath, string itemPath, out string source)
	{
		if (!string.IsNullOrWhiteSpace(explicitPath))
		{
			string path = RequireFile(explicitPath, ".dem", "testFilePath");
			source = "explicit_input";
			return ReadDem(path);
		}
		string expectedName = Path.GetFileNameWithoutExtension(locPath) + ".dem";
		foreach (string item in new string[2]
		{
			Path.GetDirectoryName(locPath),
			Path.GetDirectoryName(itemPath)
		}.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string text = FindNamedFile(item, expectedName);
			if (!string.IsNullOrEmpty(text))
			{
				source = "same_basename_search";
				return ReadDem(text);
			}
		}
		source = "not_found_optional";
		return null;
	}

	private static ParsedDem ReadDem(string path)
	{
		string[] array = ReadAllLinesSmart(path);
		Dictionary<string, string> h = Header(array, Math.Min(16, array.Length));
		return new ParsedDem
		{
			Path = path,
			TanDelta = NumContains(h, "D-losses"),
			FrequencyHz = NumContains(h, "Hz"),
			SampleRate = NormalizeSampleRate(NumContains(h, "Delta-x"))
		};
	}

	private static ParsedCalibration ReadCalibration(string path)
	{
		string[] array = ReadAllLinesSmart(path);
		if (array.Length < 16)
		{
			throw new InvalidDataException("CAL file is truncated.");
		}
		Dictionary<string, string> h = Header(array, Math.Min(16, array.Length));
		List<double> list = NumericTail(array, 13);
		if (list.Count < 16)
		{
			list = NumericTail(array, 0);
		}
		ParsedCalibration parsedCalibration = new ParsedCalibration();
		parsedCalibration.Path = path;
		parsedCalibration.Phase = PhaseFromFileName(path);
		parsedCalibration.CableLength = NumContains(h, "Cable length");
		parsedCalibration.CalibrationValuePc = NumContains(h, "Cal.value");
		parsedCalibration.VelocityMPerUs = A4HeaderVelocity(h);
		parsedCalibration.CalibrationCoefficient = FirstFinite(NumContains(h, "NI_Calib_factor"), NumContains(h, "Calib_factor"));
		parsedCalibration.StartTime = A4HeaderTimeSeconds(h, "Start_point");
		parsedCalibration.FinalTime = A4HeaderTimeSeconds(h, "Final_point");
		parsedCalibration.SampleRate = NormalizeSampleRate(NumContains(h, "SR"));
		parsedCalibration.AttenuationLossDb = NumContains(h, "Calib_Attenuation");
		parsedCalibration.Samples = list.ToArray();
		if (!Finite(parsedCalibration.CalibrationCoefficient) || parsedCalibration.CalibrationCoefficient <= 0.0)
		{
			parsedCalibration.CalibrationCoefficient = 1.0;
		}
		if (!Finite(parsedCalibration.AttenuationLossDb) && parsedCalibration.Samples.Length > 8)
		{
			int center = IndexFromTime(parsedCalibration.StartTime, parsedCalibration.SampleRate, parsedCalibration.Samples.Length, parsedCalibration.Samples.Length / 5);
			int center2 = IndexFromTime(parsedCalibration.FinalTime, parsedCalibration.SampleRate, parsedCalibration.Samples.Length, parsedCalibration.Samples.Length * 4 / 5);
			double num = WindowPeak(parsedCalibration.Samples, center, Math.Max(2, parsedCalibration.Samples.Length / 200));
			double num2 = WindowPeak(parsedCalibration.Samples, center2, Math.Max(2, parsedCalibration.Samples.Length / 200));
			if (num > 0.0 && num2 > 0.0)
			{
				parsedCalibration.AttenuationLossDb = 20.0 * Math.Log10(num / num2);
			}
		}
		return parsedCalibration;
	}

	private static ParsedLocation ReadLocation(string path)
	{
		string[] array = ReadAllLinesSmart(path);
		if (array.Length < 12)
		{
			throw new InvalidDataException("LOC file is truncated.");
		}
		Dictionary<string, string> h = Header(array, 11);
		List<double> list = NumericTail(array, 11);
		if (list.Count < 16)
		{
			list = NumericTail(array, 0);
		}
		ParsedLocation parsedLocation = new ParsedLocation();
		parsedLocation.Path = path;
		ParseMeasurementName(Path.GetFileNameWithoutExtension(path), out parsedLocation.Phase, out parsedLocation.VoltageKv);
		if (parsedLocation.Phase.Length == 0) throw new InvalidDataException("LOC filename requires an explicit A/B/C, L1/L2/L3 or ALL/COMMON phase token before the first underscore.");
		parsedLocation.PhaseDeg = FirstFinite(NumContains(h, "Phase"), NumContains(h, "Angle"));
		parsedLocation.CableLength = FirstFinite(NumContains(h, "Cable-length"), NumContains(h, "Cable length"));
		parsedLocation.VelocityMPerUs = A4HeaderVelocity(h);
		parsedLocation.SampleRate = NormalizeSampleRate(NumContains(h, "Delta x"));
		parsedLocation.Samples = list.Select((double value) => value / 100000.0).ToArray();
		parsedLocation.CycleReference = Xiongxin.OWTS.Data.LocationCycleReference.ParseHeader(array, parsedLocation.Samples.Length, parsedLocation.SampleRate);
		return parsedLocation;
	}

	private static ProjectMetadata ReadProject(string path)
	{
		ProjectMetadata projectMetadata = new ProjectMetadata();
		projectMetadata.CableLength = double.NaN;
		string[] array = ReadAllLinesSmart(path);
		foreach (string text in array)
		{
			if (text.ToLowerInvariant().Contains("length") || text.Contains("长度") || text.Contains("長度"))
			{
				double num = ParseNumber(text);
				if (Finite(num) && num > 0.0)
				{
					projectMetadata.CableLength = num;
				}
			}
		}
		return projectMetadata;
	}

	private static CableMetadata ReadCable(string path, string phase)
	{
		return A4ReadPhaseCable(path, phase);
	}

	// Frozen historical parser, no longer called. Its unscoped velocity scan and
	// repeated-block concatenation must not be used by the current entry point.
	private static CableMetadata ReadCableLegacyUnscoped(string path, string phase)
	{
		CableMetadata x = new CableMetadata();
		x.CableLength = (x.VelocityMPerUs = double.NaN);
		x.Accessories = new List<CableAccessory>();
		x.Segments = new List<CableSegment>();
		string text = NormalizePhase(phase);
		bool flag = text == "ALL";
		double num = double.NaN;
		string text2 = string.Empty;
		string[] array = ReadAllLinesSmart(path);
		for (int i = 0; i < array.Length; i++)
		{
			string text3 = array[i].Trim();
			if (text3.Length == 0)
			{
				continue;
			}
			string text4 = text3.ToUpperInvariant();
			if (text4.Contains("M/USEC") || text4.Contains("M/US") || text3.Contains("波速"))
			{
				double num2 = ParseNumber(text3);
				if (Finite(num2) && num2 > 0.0)
				{
					x.VelocityMPerUs = num2;
				}
			}
			Match match = Regex.Match(text3, "^\\s*([012])(?:\\s+|\\t+)(.*)$");
			if (!match.Success)
			{
				continue;
			}
			int num3 = int.Parse(match.Groups[1].Value, Inv);
			string text5 = match.Groups[2].Value.Trim();
			if (num3 == 0)
			{
				string text6 = NormalizePhase(text5);
				flag = ((text == "ALL") ? (x.Accessories.Count == 0) : (text6 == text));
				if (flag)
				{
					num = double.NaN;
					text2 = string.Empty;
				}
			}
			else
			{
				if (!flag)
				{
					continue;
				}
				if (num3 == 2)
				{
					text2 = Regex.Replace(text5, "^(?:本体|Body)\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
					if (text2.Length == 0)
					{
						text2 = text5;
					}
					continue;
				}
				Match match2 = Regex.Match(text5, "([-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+))\\s*(?:m|米)?\\s*$", RegexOptions.IgnoreCase);
				if (match2.Success && double.TryParse(match2.Groups[1].Value, NumberStyles.Float, Inv, out var result) && !(result < 0.0))
				{
					string text7 = text5.Substring(0, match2.Index).Trim();
					string[] array2 = (from t in Regex.Split(text7, "\\s+")
						where t.Length > 0
						select t).ToArray();
					string type = ((array2.Length == 0) ? string.Empty : array2[array2.Length - 1]);
					string text8 = ((array2.Length <= 1) ? text7 : string.Join(" ", array2.Take(array2.Length - 1).ToArray()));
					if (text8.Length == 0)
					{
						text8 = text7;
					}
					if (Finite(num) && result >= num)
					{
						x.Segments.Add(new CableSegment
						{
							StartM = num,
							EndM = result,
							InsulationType = text2
						});
					}
					x.Accessories.Add(new CableAccessory
					{
						Name = text8,
						Type = type,
						PositionM = result
					});
					num = result;
					text2 = string.Empty;
				}
			}
		}
		if (x.Accessories.Count > 0)
		{
			x.CableLength = x.Accessories.Max((CableAccessory a) => a.PositionM);
		}
		x.JointPositionsIncludingEnds = UniqueSorted(x.Accessories.Select((CableAccessory a) => a.PositionM).Concat((!Finite(x.CableLength)) ? new double[0] : new double[2] { 0.0, x.CableLength }));
		x.InternalJointPositions = UniqueSorted(from a in x.Accessories
			where IsJointName(a.Name)
			select a.PositionM into p
			where p > 0.0 && (!Finite(x.CableLength) || p < x.CableLength)
			select p);
		return x;
	}

	private static List<double> UniqueSorted(IEnumerable<double> values)
	{
		List<double> list = new List<double>();
		foreach (double item in from v in values.Where(Finite)
			orderby v
			select v)
		{
			if (list.Count == 0 || Math.Abs(list[list.Count - 1] - item) > 1E-06)
			{
				list.Add(item);
			}
		}
		return list;
	}

	private static bool IsJointName(string name)
	{
		if ((name ?? string.Empty).IndexOf("接头", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return (name ?? string.Empty).IndexOf("joint", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}

	private static string NormalizePhase(string text)
	{
		string text2 = (text ?? string.Empty).Trim().ToUpperInvariant();
		if (text2.Contains("全相") || text2.StartsWith("ALL"))
		{
			return "ALL";
		}
		if (text2.StartsWith("A"))
		{
			return "A";
		}
		if (text2.StartsWith("B"))
		{
			return "B";
		}
		if (text2.StartsWith("C"))
		{
			return "C";
		}
		return "ALL";
	}

	private static string PhaseFromFileName(string path)
	{
		return A4StrictPhase(Path.GetFileNameWithoutExtension(path).Split('_')[0]);
	}

	private static StreamReader OpenReader(string path)
	{
		byte[] array = new byte[3];
		using (FileStream fileStream = File.OpenRead(path))
		{
			fileStream.Read(array, 0, Math.Min(3, (int)fileStream.Length));
		}
		Encoding encoding = ((array[0] == 239 && array[1] == 187 && array[2] == 191) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true) : Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
		return new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
	}

	private static string[] ReadAllLinesSmart(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		string text;
		try
		{
			text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
		}
		catch (DecoderFallbackException)
		{
			text = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
		}
		using StringReader stringReader = new StringReader(text.TrimStart('\ufeff'));
		List<string> list = new List<string>();
		string item;
		while ((item = stringReader.ReadLine()) != null)
		{
			list.Add(item);
		}
		return list.ToArray();
	}

	private static Dictionary<string, string> Header(string[] lines, int count)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < Math.Min(count, lines.Length); i++)
		{
			string text = lines[i];
			int num = text.IndexOf('\t');
			int num2 = text.IndexOf(':');
			int num3 = ((num >= 0) ? num : num2);
			if (num3 >= 0)
			{
				dictionary[text.Substring(0, num3).Trim().TrimEnd(':', '：')] = text.Substring(num3 + 1).Trim();
			}
		}
		return dictionary;
	}

	private static List<double> NumericTail(string[] lines, int start)
	{
		List<double> list = new List<double>();
		for (int i = Math.Max(0, start); i < lines.Length; i++)
		{
			string text = lines[i];
			int num = text.IndexOf('\t');
			if (num >= 0)
			{
				text = text.Substring(0, num);
			}
			if (double.TryParse(text.Trim(), NumberStyles.Float, Inv, out var result) && Finite(result))
			{
				list.Add(result);
			}
		}
		return list;
	}

	private static double NumContains(Dictionary<string, string> h, string fragment)
	{
		string text = h.Keys.FirstOrDefault((string k) => k.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0);
		if (text != null)
		{
			return ParseNumber(h[text]);
		}
		return double.NaN;
	}

	private static double ParseNumber(string s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return double.NaN;
		}
		Match match = Regex.Match(s.Replace('：', ':'), "[-+]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:[eE][-+]?\\d+)?");
		if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, Inv, out var result))
		{
			return double.NaN;
		}
		return result;
	}

	private static void ParseMeasurementName(string name, out string phase, out double voltage)
	{
		phase = "";
		voltage = double.NaN;
		string[] array = (name ?? string.Empty).Split('_');
		if (array.Length != 0)
		{
			phase = A4StrictPhase(array[0]);
		}
		if (array.Length > 1)
		{
			voltage = ParseNumber(array[1]);
		}
	}

	private void ResetIncremental()
	{
		ParameterMatrix2D = new double[0, 36];
		PulseEnvelopeMatrix2D = new double[0, 0];
		StatusMessage = string.Empty;
		AddedRowCount = 0;
		AppliedMultipleReflectionSuppressionCount = 0;
		EffectiveSampleRateHz = 0.0;
		AppliedMaximumDischargeCount = 0;
		DetectedDischargeCountBeforeLimit = 0;
		MaximumDischargeCountReached = false;
		EffectiveIncidentPreExtensionPoints = 0;
		EffectiveRoundTripPostExtensionPoints = 0;
		EffectiveInitialPhaseDegrees = 0.0;
		EffectiveOscillationFrequencyHz = double.NaN;
		ValidatedRuntimePositionM = new double[0];
		string text = (ResolvedProjectFilePath = string.Empty);
		string text2 = (ResolvedCableFilePath = text);
		string resolvedCalibrationFilePath = (ResolvedTestFilePath = text2);
		ResolvedCalibrationFilePath = resolvedCalibrationFilePath;
		CalibrationSelectionPriority = string.Empty;
		ResolvedCableLengthM = double.NaN;
		double[] resolvedJointPositionsIncludingEndsM = (ResolvedInternalJointPositionsM = new double[0]);
		ResolvedJointPositionsIncludingEndsM = resolvedJointPositionsIncludingEndsM;
		resolvedJointPositionsIncludingEndsM = (ResolvedSegmentEndPositionsM = new double[0]);
		ResolvedSegmentStartPositionsM = resolvedJointPositionsIncludingEndsM;
		resolvedCalibrationFilePath = (ResolvedAccessoryTypesJson = "[]");
		ResolvedSegmentInsulationTypesJson = resolvedCalibrationFilePath;
	}

	private string BuildIncrementalStatusMessage()
	{
		if (!Success)
		{
			if (!string.IsNullOrWhiteSpace(Error))
			{
				return "错误：" + Error;
			}
			return "错误：定位分析失败，未返回详细错误信息。";
		}
		if (AddedRowCount <= 0)
		{
			return "非正常：定位分析已完成，但本次未检测到满足阈值及定位条件的有效放电脉冲；输出矩阵仅保留原有数据。请检查待分析LOC文件、阈值、采样率和校准文件。";
		}
		if (MaximumDischargeCountReached)
		{
			return "非正常：检测到" + DetectedDischargeCountBeforeLimit.ToString(Inv) + "个候选放电脉冲，已达到单次最大分析数量" + AppliedMaximumDischargeCount.ToString(Inv) + "，本次输出" + AddedRowCount.ToString(Inv) + "行，剩余候选未继续分析。";
		}
		return "正常：定位分析完成，本次新增" + AddedRowCount.ToString(Inv) + "行，累计" + ParameterMatrix2D.GetLength(0).ToString(Inv) + "行。";
	}

	private static void ValidateModes(int algorithm, int thresholdType, double sampleRate, int suppression, int maximumDischargeCount, int pre, int post)
	{
		if (algorithm < 0 || algorithm >= 5)
		{
			throw new ArgumentOutOfRangeException("locationAlgorithm", "Use 0, 1, 2, 3 or 4.");
		}
		if (thresholdType < -1 || thresholdType > 1)
		{
			throw new ArgumentOutOfRangeException("thresholdType", "Use -1=manual, 0=automatic times three (or unscaled when all threshold ports are blank), or 1=automatic times multiplier.");
		}
		if (sampleRate < 0.0)
		{
			throw new ArgumentOutOfRangeException("sampleRateHz", "Use 0 for the file value or a positive value.");
		}
		if (suppression < -1)
		{
			throw new ArgumentOutOfRangeException("multipleReflectionSuppressionCount", "Use -1 for automatic or a non-negative count.");
		}
		if (maximumDischargeCount < 0)
		{
			throw new ArgumentOutOfRangeException("maximumDischargeCount", "Use 0 for the legacy safety cap or a positive maximum count.");
		}
		if (pre < -1 || post < -1)
		{
			throw new ArgumentOutOfRangeException("extensionPoints", "Use -1 for automatic cable-flight extension or a non-negative count.");
		}
	}

	private static int ValidatePreviousMatrices(double[,] parameters, double[,] envelopes)
	{
		int num = parameters?.GetLength(0) ?? 0;
		int num2 = parameters?.GetLength(1) ?? 36;
		int num3 = envelopes?.GetLength(0) ?? 0;
		if (num > 0 && num2 != 36)
		{
			throw new ArgumentException("previousParameterMatrix must have exactly 36 columns.");
		}
		if (num != num3)
		{
			throw new ArgumentException("Previous parameter and pulse-envelope matrices must have equal row counts.");
		}
		return num;
	}

	private static double ResolveSampleRate(params double[] values)
	{
		for (int i = 0; i < values.Length; i++)
		{
			double num = NormalizeSampleRate(values[i]);
			if (Finite(num) && num > 0.0)
			{
				return num;
			}
		}
		throw new ArgumentException("sampleRateHz is 0 and no valid sample rate was found in LOC, CAL or DEM.");
	}

	private static double NormalizeSampleRate(double x)
	{
		if (!Finite(x) || x <= 0.0)
		{
			return double.NaN;
		}
		if (x < 0.001)
		{
			return 1.0 / x;
		}
		if (x < 1000.0)
		{
			return 1000000000.0 / x;
		}
		return x;
	}

	private static double ResolveThreshold(double[] samples, double center, double sigma, int type, double manual, double multiplier)
	{
		if (type == -1 && Finite(manual) && manual > 0.0)
		{
			return Math.Abs(manual);
		}
		double d = Math.Max(2, samples.Count(Finite));
		double num = Clamp(Math.Sqrt(2.0 * Math.Log(d)), 5.0, 6.5) * sigma;
		bool flag = type == 0 && (!Finite(manual) || manual == 0.0) && (!Finite(multiplier) || multiplier == 0.0);
		if (type == 0 && !flag)
		{
			num *= 3.0;
		}
		else if (type == 1)
		{
			num *= ((Finite(multiplier) && multiplier > 0.0) ? multiplier : 1.0);
		}
		if (!Finite(num) || num <= 0.0)
		{
			throw new ArgumentException("Automatic threshold cannot estimate random noise from the LOC waveform.");
		}
		return num;
	}

	private static double RobustSigma(double[] data, double fallbackCenter)
	{
		List<double> list = new List<double>();
		for (int i = 0; i < data.Length; i += 4096)
		{
			double[] array = data.Skip(i).Take(Math.Min(4096, data.Length - i)).Where(Finite)
				.ToArray();
			if (array.Length >= 8)
			{
				double c = Median(array);
				double num = 1.4826 * Median(array.Select((double v) => Math.Abs(v - c)).ToArray());
				if (Finite(num) && num > 0.0)
				{
					list.Add(num);
				}
			}
		}
		if (list.Count == 0)
		{
			return 1.4826 * Median((from v in data.Where(Finite)
				select Math.Abs(v - fallbackCenter)).ToArray());
		}
		double med = Median(list.ToArray());
		double[] array2 = list.Where((double s) => s >= med * 0.5 && s <= med * 1.5).ToArray();
		return Median((array2.Length != 0) ? array2 : new double[1] { med });
	}

	private static double[] SmoothAbsolute(double[] data, double center)
	{
		double[] array = new double[data.Length];
		for (int i = 0; i < data.Length; i++)
		{
			array[i] = Math.Abs(data[i] - center);
		}
		int num = 2;
		double[] array2 = new double[data.Length];
		double num2 = 0.0;
		for (int j = 0; j < data.Length; j++)
		{
			num2 += array[j];
			if (j - num - 1 >= 0)
			{
				num2 -= array[j - num - 1];
			}
			array2[j] = num2 / (double)Math.Min(num + 1, j + 1);
		}
		return array2;
	}

	private static List<int> DetectIncidents(double[] envelope, double threshold, int deadSamples)
	{
		List<int> list = new List<int>();
		int i = 0;
		while (i < envelope.Length)
		{
			for (; i < envelope.Length && envelope[i] < threshold; i++)
			{
			}
			if (i >= envelope.Length)
			{
				break;
			}
			int num = Math.Min(envelope.Length, i + Math.Max(1, deadSamples));
			int num2 = i;
			for (int j = i + 1; j < num; j++)
			{
				if (envelope[j] > envelope[num2])
				{
					num2 = j;
				}
			}
			list.Add(num2);
			i = num;
		}
		return list;
	}

	private static List<int> SuppressRepeatIncidents(List<int> incidents, int roundTripSamples, int count)
	{
		if (count <= 0 || incidents.Count < 2)
		{
			return incidents;
		}
		List<int> list = new List<int>();
		int num = Math.Max(3, roundTripSamples / 50);
		foreach (int incident in incidents)
		{
			bool flag = false;
			foreach (int item in list)
			{
				for (int i = 1; i <= count; i++)
				{
					if (Math.Abs(incident - (item + i * roundTripSamples)) <= num)
					{
						flag = true;
					}
				}
			}
			if (!flag)
			{
				list.Add(incident);
			}
		}
		return list;
	}

	private static int AutoSuppressionCount(ParsedCalibration cal, double noiseSigma, double threshold)
	{
		double attenuationLossDb = cal.AttenuationLossDb;
		if (!Finite(attenuationLossDb) || attenuationLossDb <= 0.25)
		{
			return 1;
		}
		double num = ((cal.Samples == null || cal.Samples.Length == 0) ? (threshold * 10.0) : cal.Samples.Max((double v) => Math.Abs(v)));
		double num2 = Math.Max(threshold, noiseSigma * 6.0);
		if (num <= num2)
		{
			return 0;
		}
		int val = (int)Math.Ceiling(20.0 * Math.Log10(num / num2) / attenuationLossDb);
		return Math.Max(0, Math.Min(8, val));
	}

	private AlgorithmTimingResult RefineReflectionTiming(int algorithm, double[] samples, double center, double sigma, double threshold, int directOnset, int directPeak, int standardReflectionOnset, int standardReflectionPeak, int eventBlind, int roundTripSamples, ParsedCalibration calibration, double sampleRateHz, double cableLength)
	{
		AlgorithmTimingResult result = new AlgorithmTimingResult();
		if (algorithm == 0 || samples == null || samples.Length == 0)
		{
			return result;
		}
		AlgorithmTimingResult algorithmTimingResult = FindCorrelationReflection(samples, center, sigma, threshold, directOnset, directPeak, standardReflectionPeak, eventBlind, roundTripSamples);
		if (algorithm == 1)
		{
			return algorithmTimingResult;
		}
		AlgorithmTimingResult algorithmTimingResult2 = FindEnergyReflection(samples, center, sigma, threshold, directOnset, standardReflectionPeak, eventBlind, roundTripSamples);
		switch (algorithm)
		{
		case 2:
			return algorithmTimingResult2;
		case 4:
			return FindMultiFactorReflection(samples, center, sigma, threshold, directOnset, directPeak, standardReflectionOnset, standardReflectionPeak, eventBlind, roundTripSamples, calibration, sampleRateHz, cableLength);
		default:
		{
			List<int> list = new List<int>();
			list.Add(standardReflectionOnset);
			if (algorithmTimingResult.Valid)
			{
				list.Add(algorithmTimingResult.ReflectionOnset);
			}
			if (algorithmTimingResult2.Valid)
			{
				list.Add(algorithmTimingResult2.ReflectionOnset);
			}
			if (list.Count < 2)
			{
				return result;
			}
			list.Sort();
			int num = (int)Math.Round(Median(((IEnumerable<int>)list).Select((Func<int, double>)((int x) => x)).ToArray()));
			int reflectionPeak = AbsoluteMaximumIndex(samples, num, Math.Min(samples.Length - 1, num + Math.Max(3, eventBlind)), center);
			if (!ValidReflectionTiming(directOnset, num, roundTripSamples))
			{
				return result;
			}
			AlgorithmTimingResult obj = new AlgorithmTimingResult
			{
				Valid = true,
				ReflectionOnset = num,
				ReflectionPeak = reflectionPeak
			};
			double num2 = 1.0;
			int num3 = 1;
			if (algorithmTimingResult.Valid)
			{
				num2 += algorithmTimingResult.Score;
				num3++;
			}
			if (algorithmTimingResult2.Valid)
			{
				num2 += algorithmTimingResult2.Score;
				num3++;
			}
			obj.Score = Clamp(num2 / (double)num3, 0.0, 1.0);
			return obj;
		}
		}
	}

	private static AlgorithmTimingResult FindCorrelationReflection(double[] samples, double center, double sigma, double threshold, int directOnset, int directPeak, int standardReflectionPeak, int eventBlind, int roundTripSamples)
	{
		AlgorithmTimingResult result = new AlgorithmTimingResult();
		int num = Math.Max(4, Math.Min(48, Math.Max(4, eventBlind / 2)));
		int num2 = AlgorithmReflectionSearchRadius(eventBlind, roundTripSamples);
		int num3 = Math.Max(directPeak + num + 2, standardReflectionPeak - num2);
		int num4 = Math.Min(samples.Length - num - 1, standardReflectionPeak + num2);
		if (num4 < num3)
		{
			return result;
		}
		double num5 = -1.0;
		int num6 = num3;
		for (int i = num3; i <= num4; i++)
		{
			double num7 = 0.0;
			double num8 = 0.0;
			double num9 = 0.0;
			for (int j = -num; j <= num; j++)
			{
				double num10 = samples[ClampIndex(directPeak + j, samples.Length)] - center;
				double num11 = samples[i + j] - center;
				num7 += num10 * num10;
				num8 += num11 * num11;
				num9 += num10 * num11;
			}
			double num12 = ((num7 > 0.0 && num8 > 0.0) ? (Math.Abs(num9) / Math.Sqrt(num7 * num8)) : 0.0);
			if (num12 > num5)
			{
				num5 = num12;
				num6 = i;
			}
		}
		int peak = AbsoluteMaximumIndex(samples, num6 - num, num6 + num, center);
		return BuildAlgorithmTiming(samples, center, sigma, threshold, directOnset, peak, eventBlind, roundTripSamples, num5);
	}

	private static AlgorithmTimingResult FindEnergyReflection(double[] samples, double center, double sigma, double threshold, int directOnset, int standardReflectionPeak, int eventBlind, int roundTripSamples)
	{
		AlgorithmTimingResult result = new AlgorithmTimingResult();
		int num = Math.Max(3, Math.Min(32, Math.Max(3, eventBlind / 3)));
		int num2 = AlgorithmReflectionSearchRadius(eventBlind, roundTripSamples);
		int num3 = Math.Max(directOnset + 1, standardReflectionPeak - num2);
		int num4 = Math.Min(samples.Length - 1, standardReflectionPeak + num2);
		if (num4 < num3)
		{
			return result;
		}
		double num5 = -1.0;
		int num6 = num3;
		for (int i = num3; i <= num4; i++)
		{
			double num7 = 0.0;
			int num8 = Math.Max(num3, i - num);
			int num9 = Math.Min(num4, i + num);
			for (int j = num8; j <= num9; j++)
			{
				double num10 = samples[j] - center;
				num7 += num10 * num10;
			}
			if (num7 > num5)
			{
				num5 = num7;
				num6 = i;
			}
		}
		int peak = AbsoluteMaximumIndex(samples, num6 - num, num6 + num, center);
		double num11 = Math.Sqrt(Math.Max(0.0, num5) / (double)Math.Max(1, 2 * num + 1));
		double score = ((threshold > 0.0) ? Clamp(num11 / threshold, 0.0, 1.0) : 0.0);
		return BuildAlgorithmTiming(samples, center, sigma, threshold, directOnset, peak, eventBlind, roundTripSamples, score);
	}

	private static AlgorithmTimingResult BuildAlgorithmTiming(double[] samples, double center, double sigma, double threshold, int directOnset, int peak, int eventBlind, int roundTripSamples, double score)
	{
		AlgorithmTimingResult algorithmTimingResult = new AlgorithmTimingResult();
		int reflectionOnset = FindFirstArrivalLikeTask5(samples, peak, eventBlind, center, sigma);
		double num = Math.Abs(samples[ClampIndex(peak, samples.Length)] - center);
		double num2 = Math.Max(threshold, Math.Max(0.0, sigma) * 3.0);
		if (!ValidReflectionTiming(directOnset, reflectionOnset, roundTripSamples) || num < num2)
		{
			return algorithmTimingResult;
		}
		algorithmTimingResult.Valid = true;
		algorithmTimingResult.ReflectionPeak = peak;
		algorithmTimingResult.ReflectionOnset = reflectionOnset;
		algorithmTimingResult.Score = Clamp(score, 0.0, 1.0);
		return algorithmTimingResult;
	}

	private static bool ValidReflectionTiming(int directOnset, int reflectionOnset, int roundTripSamples)
	{
		int num = reflectionOnset - directOnset;
		if (num > 0)
		{
			return num <= Math.Max(1, roundTripSamples);
		}
		return false;
	}

	private static int AlgorithmReflectionSearchRadius(int eventBlind, int roundTripSamples)
	{
		int val = Math.Max(Math.Max(12, eventBlind * 2), roundTripSamples / 30);
		return Math.Min(Math.Max(12, roundTripSamples / 5), val);
	}

	private static LocateResult LocateOne(int algorithm, double[] waveform, double[] envelope, ParsedCalibration cal, int incident, int roundTripSamples, int suppression, double threshold, double fs, double velocity, double cableLength)
	{
		LocateResult locateResult = LocateByPeak(envelope, incident, roundTripSamples, threshold, fs, velocity, cableLength);
		if (algorithm == 0)
		{
			return locateResult;
		}
		LocateResult locateResult2 = LocateByCorrelation(waveform, incident, roundTripSamples, threshold, fs, velocity, cableLength);
		if (algorithm == 1)
		{
			return locateResult2;
		}
		LocateResult locateResult3 = LocateByEnergy(envelope, incident, roundTripSamples, threshold, fs, velocity, cableLength);
		if (algorithm == 2)
		{
			return locateResult3;
		}
		double[] array = (from x in new double[3] { locateResult.PositionM, locateResult2.PositionM, locateResult3.PositionM }.Where(Finite)
			orderby x
			select x).ToArray();
		LocateResult locateResult4 = new LocateResult();
		locateResult4.PositionM = ((array.Length == 0) ? double.NaN : Median(array));
		locateResult4.ReflectionIndex = incident + (int)Math.Round(2.0 * locateResult4.PositionM / velocity * 1E-06 * fs);
		double num = ((array.Length < 2) ? cableLength : (array.Max() - array.Min()));
		locateResult4.QualityPercent = Clamp(100.0 - 200.0 * num / Math.Max(cableLength, 1.0), 0.0, 100.0);
		locateResult4.Probability = locateResult4.QualityPercent / 100.0;
		locateResult4.TailError = num;
		locateResult4.Accepted = array.Length >= 2 && locateResult4.QualityPercent >= 45.0;
		return locateResult4;
	}

	private static LocateResult LocateByPeak(double[] e, int incident, int span, double threshold, double fs, double velocity, double length)
	{
		int num = Math.Min(e.Length - 1, incident + Math.Max(3, span / 100));
		int num2 = Math.Min(e.Length - 1, incident + span);
		int num3 = num;
		for (int i = num + 1; i <= num2; i++)
		{
			if (e[i] > e[num3])
			{
				num3 = i;
			}
		}
		return MakeLocate(incident, num3, e[num3], threshold, fs, velocity, length, 0.0);
	}

	private static LocateResult LocateByEnergy(double[] e, int incident, int span, double threshold, double fs, double velocity, double length)
	{
		int num = Math.Max(2, span / 200);
		int num2 = Math.Min(e.Length - 1, incident + Math.Max(3, span / 100));
		int num3 = Math.Min(e.Length - 1, incident + span);
		double num4 = -1.0;
		int reflection = num2;
		for (int i = num2; i <= num3; i++)
		{
			double num5 = 0.0;
			for (int j = Math.Max(num2, i - num); j <= Math.Min(num3, i + num); j++)
			{
				num5 += e[j] * e[j];
			}
			if (num5 > num4)
			{
				num4 = num5;
				reflection = i;
			}
		}
		return MakeLocate(incident, reflection, Math.Sqrt(Math.Max(0.0, num4) / (double)Math.Max(1, 2 * num + 1)), threshold, fs, velocity, length, 0.0);
	}

	private static LocateResult LocateByCorrelation(double[] w, int incident, int span, double threshold, double fs, double velocity, double length)
	{
		int num = Math.Max(4, Math.Min(32, span / 80));
		int num2 = incident + Math.Max(num + 2, span / 100);
		int num3 = Math.Min(w.Length - num - 1, incident + span);
		double num4 = -1.0;
		int reflection = Math.Min(w.Length - 1, Math.Max(0, num2));
		for (int i = num2; i <= num3; i++)
		{
			double num5 = 0.0;
			double num6 = 0.0;
			double num7 = 0.0;
			for (int j = -num; j <= num; j++)
			{
				int num8 = ClampIndex(incident + j, w.Length);
				double num9 = w[num8];
				double num10 = w[i + j];
				num5 += num9 * num9;
				num6 += num10 * num10;
				num7 += num9 * num10;
			}
			double num11 = ((num5 > 0.0 && num6 > 0.0) ? (Math.Abs(num7) / Math.Sqrt(num5 * num6)) : 0.0);
			if (num11 > num4)
			{
				num4 = num11;
				reflection = i;
			}
		}
		LocateResult locateResult = MakeLocate(incident, reflection, num4 * threshold * 2.0, threshold, fs, velocity, length, 1.0 - num4);
		locateResult.QualityPercent = Clamp(num4 * 100.0, 0.0, 100.0);
		locateResult.Probability = locateResult.QualityPercent / 100.0;
		locateResult.Accepted = num4 >= 0.35;
		return locateResult;
	}

	private static LocateResult MakeLocate(int incident, int reflection, double amplitude, double threshold, double fs, double velocity, double length, double tail)
	{
		LocateResult obj = new LocateResult
		{
			ReflectionIndex = reflection,
			PositionM = Clamp((double)(reflection - incident) / fs * 1000000.0 * velocity / 2.0, 0.0, length)
		};
		double num = ((threshold > 0.0) ? (amplitude / threshold) : 0.0);
		obj.QualityPercent = Clamp(25.0 * num, 0.0, 100.0);
		obj.Probability = obj.QualityPercent / 100.0;
		obj.TailError = tail;
		obj.Accepted = amplitude >= threshold && reflection > incident;
		return obj;
	}

	private static double[] EnsureCableEnds(IEnumerable<double> positions, double cableLength)
	{
		List<double> source = UniqueSorted((positions ?? new double[0]).Concat(new double[2] { 0.0, cableLength }));
		source = source.Where((double x) => x >= 0.0 && x <= cableLength).ToList();
		if (source.Count < 2)
		{
			return new double[2] { 0.0, cableLength };
		}
		source[0] = 0.0;
		source[source.Count - 1] = cableLength;
		return source.ToArray();
	}

	private static int AbsoluteMaximumIndex(double[] samples, int start, int end, double center)
	{
		int num = ClampIndex(Math.Min(start, end), samples.Length);
		int num2 = ClampIndex(Math.Max(start, end), samples.Length);
		int result = num;
		double num3 = -1.0;
		for (int i = num; i <= num2; i++)
		{
			double num4 = Math.Abs(samples[i] - center);
			if (num4 > num3)
			{
				num3 = num4;
				result = i;
			}
		}
		return result;
	}

	private static int FindFirstArrivalLikeTask5(double[] samples, int peakIndex, int searchSpan, double center, double sigma)
	{
		int num = ClampIndex(peakIndex, samples.Length);
		int num2 = Math.Max(0, num - Math.Max(3, searchSpan));
		double num3 = Math.Abs(samples[num] - center);
		double num4 = Math.Max(Math.Max(0.0, sigma) * 3.0, num3 * 0.05);
		double num5 = Math.Max(Math.Max(0.0, sigma) * 5.0, num3 * 0.15);
		int num6 = Math.Max(4, Math.Min(12, Math.Max(4, searchSpan / 24)));
		for (int i = num2; i <= num; i++)
		{
			if (Math.Abs(samples[i] - center) < num4)
			{
				continue;
			}
			bool flag = true;
			for (int j = Math.Max(num2, i - 3); j < i; j++)
			{
				if (Math.Abs(samples[j] - center) >= num4)
				{
					flag = false;
					break;
				}
			}
			if (!flag)
			{
				continue;
			}
			bool flag2 = false;
			for (int k = i; k <= Math.Min(num, i + num6); k++)
			{
				if (Math.Abs(samples[k] - center) >= num5)
				{
					flag2 = true;
					break;
				}
			}
			if (flag2)
			{
				return i;
			}
		}
		return num;
	}

	private static double PositionFromFirstArrivalDelay(int delaySamples, double sampleRateHz, double[] joints, double[] velocities, double cableLength)
	{
		if (delaySamples <= 0 || sampleRateHz <= 0.0)
		{
			return 0.0;
		}
		double num = (double)delaySamples / sampleRateHz * 1000000.0 / 2.0;
		double num2 = 0.0;
		int num3 = Math.Min((velocities != null) ? velocities.Length : 0, (joints != null) ? Math.Max(0, joints.Length - 1) : 0);
		for (int i = 0; i < num3; i++)
		{
			double num4 = Math.Max(0.0, joints[i + 1] - joints[i]);
			double num5 = velocities[i];
			if (Finite(num5) && !(num5 <= 0.0))
			{
				double num6 = num4 / num5;
				if (num <= num6)
				{
					return Clamp(num2 + num * num5, 0.0, cableLength);
				}
				num -= num6;
				num2 += num4;
			}
		}
		return Clamp(num2, 0.0, cableLength);
	}

	private static double[,] AppendParameters(double[,] previous, List<double[]> rows)
	{
		int num = previous?.GetLength(0) ?? 0;
		double[,] array = new double[num + rows.Count, 36];
		for (int i = 0; i < num; i++)
		{
			for (int j = 0; j < 36; j++)
			{
				array[i, j] = previous[i, j];
			}
		}
		for (int k = 0; k < rows.Count; k++)
		{
			for (int l = 0; l < 36; l++)
			{
				array[num + k, l] = rows[k][l];
			}
		}
		return array;
	}

	private static double[,] AppendEnvelopes(double[,] previous, List<double[]> rows)
	{
		int num = previous?.GetLength(0) ?? 0;
		int num2 = previous?.GetLength(1) ?? 0;
		int val = ((rows.Count != 0) ? rows.Max((double[] r) => r.Length) : 0);
		int num3 = Math.Max(num2, val);
		double[,] array = new double[num + rows.Count, num3];
		for (int num4 = 0; num4 < array.GetLength(0); num4++)
		{
			for (int num5 = 0; num5 < array.GetLength(1); num5++)
			{
				array[num4, num5] = double.NaN;
			}
		}
		for (int num6 = 0; num6 < num; num6++)
		{
			for (int num7 = 0; num7 < num2; num7++)
			{
				array[num6, num7] = previous[num6, num7];
			}
		}
		for (int num8 = 0; num8 < rows.Count; num8++)
		{
			for (int num9 = 0; num9 < rows[num8].Length; num9++)
			{
				array[num + num8, num9] = rows[num8][num9];
			}
		}
		return array;
	}

	private static double[] Flatten(double[,] matrix)
	{
		int num = matrix?.GetLength(0) ?? 0;
		int num2 = matrix?.GetLength(1) ?? 0;
		double[] array = new double[num * num2];
		int num3 = 0;
		for (int i = 0; i < num; i++)
		{
			for (int j = 0; j < num2; j++)
			{
				array[num3++] = matrix[i, j];
			}
		}
		return array;
	}

	private static double[] NewNaNRow()
	{
		double[] array = new double[36];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = double.NaN;
		}
		return array;
	}

	private static double PulseWidthNs(double[] e, int peak, double level, double fs)
	{
		int num = peak;
		int i = peak;
		while (num > 0 && e[num - 1] >= level)
		{
			num--;
		}
		for (; i + 1 < e.Length && e[i + 1] >= level; i++)
		{
		}
		return (double)(i - num + 1) / fs * 1000000000.0;
	}

	private static double PulseWidthNsAbsolute(double[] samples, int peak, double center, double level, double fs)
	{
		int num = peak;
		int i = peak;
		while (num > 0 && Math.Abs(samples[num - 1] - center) >= level)
		{
			num--;
		}
		for (; i + 1 < samples.Length && Math.Abs(samples[i + 1] - center) >= level; i++)
		{
		}
		return (double)(i - num + 1) / fs * 1000000000.0;
	}

	private static double CompensateCharge(double q, double position, double cableLength, double attenuationLossDb)
	{
		if (!Finite(attenuationLossDb) || attenuationLossDb <= 0.0 || cableLength <= 0.0)
		{
			return q;
		}
		// CAL incident/reflection loss is an end-to-end ROUND TRIP, not a single
		// pass. This engineering compensation assumes |R_endpoint|=1 and uniform
		// loss per metre; it cannot identify reflection loss or segment loss from
		// one CAL. Raw charge is always retained separately by the public outputs.
		return q * Math.Pow(10.0, attenuationLossDb * Clamp(position / cableLength, 0.0, 1.0) / 40.0);
	}

	private static int PhaseCode(string phase)
	{
		if (string.Equals(phase, "A", StringComparison.OrdinalIgnoreCase))
		{
			return 1;
		}
		if (string.Equals(phase, "B", StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}
		if (string.Equals(phase, "C", StringComparison.OrdinalIgnoreCase))
		{
			return 3;
		}
		return 0;
	}

	private static double FirstPositive(params double[] values)
	{
		foreach (double num in values)
		{
			if (Finite(num) && num > 0.0)
			{
				return num;
			}
		}
		return double.NaN;
	}

	private static double FirstFinite(params double[] values)
	{
		foreach (double num in values)
		{
			if (Finite(num))
			{
				return num;
			}
		}
		return double.NaN;
	}

	private static int IndexFromTime(double time, double fs, int length, int fallback)
	{
		if (!Finite(time) || !Finite(fs) || fs <= 0.0)
		{
			return ClampIndex(fallback, length);
		}
		// Internal times are seconds; textual units are normalized at the header.
		// Never infer microseconds from the magnitude of an otherwise valid time.
		double sample = Math.Round(time * fs);
		if (length <= 0) return 0;
		if (sample <= 0) return 0;
		if (!Finite(sample) || sample >= length - 1) return length - 1;
		return (int)sample;
	}

	private static double WindowPeak(double[] data, int center, int radius)
	{
		double num = 0.0;
		for (int i = Math.Max(0, center - radius); i <= Math.Min(data.Length - 1, center + radius); i++)
		{
			num = Math.Max(num, Math.Abs(data[i]));
		}
		return num;
	}

	private static double Median(double[] values)
	{
		if (values == null || values.Length == 0)
		{
			return double.NaN;
		}
		double[] array = values.Where(Finite).ToArray();
		if (array.Length == 0)
		{
			return double.NaN;
		}
		Array.Sort(array);
		int num = array.Length / 2;
		if ((array.Length & 1) != 1)
		{
			return 0.5 * (array[num - 1] + array[num]);
		}
		return array[num];
	}

	private static double Clamp(double value, double minimum, double maximum)
	{
		return Math.Max(minimum, Math.Min(maximum, value));
	}

	private static double NormalizeDegrees(double value)
	{
		if (!Finite(value))
		{
			return double.NaN;
		}
		double num = value % 360.0;
		if (num < 0.0)
		{
			num += 360.0;
		}
		return num;
	}

	private static int ClampIndex(int value, int length)
	{
		return Math.Max(0, Math.Min(length - 1, value));
	}

	private static bool Finite(double value)
	{
		if (!double.IsNaN(value))
		{
			return !double.IsInfinity(value);
		}
		return false;
	}

	private static string Json(string value)
	{
		StringBuilder stringBuilder = new StringBuilder("\"");
		string text = value ?? string.Empty;
		foreach (char c in text)
		{
			if (c == '\\')
			{
				stringBuilder.Append("\\\\");
			}
			else if (c == '"')
			{
				stringBuilder.Append("\\\"");
			}
			else if (c == '\r')
			{
				stringBuilder.Append("\\r");
			}
			else if (c == '\n')
			{
				stringBuilder.Append("\\n");
			}
			else if (c == '\t')
			{
				stringBuilder.Append("\\t");
			}
			else if (c < ' ')
			{
				int num = c;
				stringBuilder.Append("\\u" + num.ToString("x4", Inv));
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.Append('"').ToString();
	}

	private static string NumberJson(double value)
	{
		if (!Finite(value))
		{
			return "null";
		}
		return value.ToString("R", Inv);
	}

	private static string StringArrayJson(IEnumerable<string> values)
	{
		return "[" + string.Join(",", (values ?? new string[0]).Select(Json).ToArray()) + "]";
	}

	private static string NumberArrayJson(IEnumerable<double> values)
	{
		return "[" + string.Join(",", (values ?? new double[0]).Select(NumberJson).ToArray()) + "]";
	}

	private static string BuildStatusJson(string loc, string calibration, string calibrationSource, string calibrationPriority, int calibrationCandidateCount, string dem, string testSource, string cablePath, string itemPath, CableMetadata cable, int added, int previous, int algorithm, double fs, int suppression, double threshold, double tanDelta, int requestedMaximum, int effectiveMaximum, int detectedBeforeLimit, bool maximumReached, int requestedPre, int requestedPost, int effectivePre, int effectivePost, int oneWaySamples, int firstArrivalRuntimeRows, int task5OnsetSuccessCount, int task5EquivalentFallbackCount, int algorithmRefinementAppliedCount, int algorithmRefinementFallbackCount, double algorithmRefinementMeanScore, string task5OnsetFailureSample, int firstArrivalBlindMinimum, int firstArrivalBlindMaximum, int task5BlindMinimum, int task5BlindMaximum, int cableBlindMinimum, int cableBlindMaximum, int eventAmplitudeRejectedCount, int reflectionEchoRejectedCount, double initialPhaseDegrees, double requestedOscillationFrequencyHz, double effectiveOscillationFrequencyHz, bool pairedAutomaticWindow, string algorithm4MetricsJson)
	{
		return "{\"ok\":true,\"interface\":\"AnalyzeLocationFileIncremental\",\"parameter_columns\":36,\"location_file\":" + Json(loc) + ",\"calibration_file\":" + Json(calibration) + ",\"calibration_source\":" + Json(calibrationSource) + ",\"calibration_selection_priority\":" + Json(calibrationPriority) + ",\"calibration_candidate_count\":" + calibrationCandidateCount.ToString(Inv) + ",\"matched_dem_file\":" + Json(dem) + ",\"test_file_source\":" + Json(testSource) + ",\"cable_file\":" + Json(cablePath) + ",\"project_file\":" + Json(itemPath) + ",\"previous_rows\":" + previous.ToString(Inv) + ",\"added_rows\":" + added.ToString(Inv) + ",\"total_rows\":" + (previous + added).ToString(Inv) + ",\"algorithm_index\":" + algorithm.ToString(Inv) + ",\"algorithm_names\":[\"validated_first_arrival_standard\",\"validated_first_arrival_correlation\",\"validated_first_arrival_energy\",\"validated_first_arrival_consensus\",\"six_factor_propagation_evidence\"],\"sample_rate_hz\":" + NumberJson(fs) + ",\"reflection_suppression_count\":" + suppression.ToString(Inv) + ",\"threshold_absolute\":" + NumberJson(threshold) + ",\"matched_dem_tan_delta\":" + NumberJson(tanDelta) + ",\"requested_maximum_discharge_count\":" + requestedMaximum.ToString(Inv) + ",\"applied_maximum_discharge_count\":" + effectiveMaximum.ToString(Inv) + ",\"detected_discharge_count_before_limit\":" + detectedBeforeLimit.ToString(Inv) + ",\"maximum_discharge_count_reached\":" + (maximumReached ? "true" : "false") + ",\"requested_incident_pre_extension_points\":" + requestedPre.ToString(Inv) + ",\"requested_reflection_post_extension_points\":" + requestedPost.ToString(Inv) + ",\"effective_incident_pre_extension_points\":" + effectivePre.ToString(Inv) + ",\"effective_reflection_post_extension_points\":" + effectivePost.ToString(Inv) + ",\"one_way_cable_propagation_points\":" + oneWaySamples.ToString(Inv) + ",\"paired_automatic_window_rule_active\":" + (pairedAutomaticWindow ? "true" : "false") + ",\"paired_automatic_window_policy\":\"incident_pre=ceil(0.5*one_way_points);reflection_post=one_way_points_after_full_round_trip\",\"initial_phase_degrees\":" + NumberJson(initialPhaseDegrees) + ",\"requested_oscillation_frequency_hz\":" + NumberJson(requestedOscillationFrequencyHz) + ",\"effective_oscillation_frequency_hz\":" + NumberJson(effectiveOscillationFrequencyHz) + ",\"pulse_phase_formula\":\"normalize(initial_phase_deg+360*oscillation_frequency_hz*direct_first_arrival_sample/sample_rate_hz)\",\"pulse_phase_time_reference\":\"incident_first_arrival\",\"pulse_phase_fallback\":\"loc_recorded_phase_when_no_frequency_is_available\",\"first_arrival_runtime_rows_before_event_deduplication\":" + firstArrivalRuntimeRows.ToString(Inv) + ",\"event_rows_after_peak_trough_deduplication\":" + detectedBeforeLimit.ToString(Inv) + ",\"peak_trough_event_deduplicated\":true,\"task5_first_arrival_success_count\":" + task5OnsetSuccessCount.ToString(Inv) + ",\"task5_equivalent_first_arrival_fallback_count\":" + task5EquivalentFallbackCount.ToString(Inv) + ",\"algorithm_refinement_applied_count\":" + algorithmRefinementAppliedCount.ToString(Inv) + ",\"algorithm_refinement_fallback_count\":" + algorithmRefinementFallbackCount.ToString(Inv) + ",\"algorithm_refinement_mean_score\":" + NumberJson(algorithmRefinementMeanScore) + ",\"task5_first_arrival_failure_sample\":" + Json(task5OnsetFailureSample) + ",\"task17_adaptive_blind_min_samples\":" + firstArrivalBlindMinimum.ToString(Inv) + ",\"task17_adaptive_blind_max_samples\":" + firstArrivalBlindMaximum.ToString(Inv) + ",\"task5_adaptive_blind_min_samples\":" + task5BlindMinimum.ToString(Inv) + ",\"task5_adaptive_blind_max_samples\":" + task5BlindMaximum.ToString(Inv) + ",\"cable_flight_adaptive_blind_min_samples\":" + cableBlindMinimum.ToString(Inv) + ",\"cable_flight_adaptive_blind_max_samples\":" + cableBlindMaximum.ToString(Inv) + ",\"below_event_threshold_candidates_rejected\":" + eventAmplitudeRejectedCount.ToString(Inv) + ",\"reflection_echo_candidates_rejected\":" + reflectionEchoRejectedCount.ToString(Inv) + ",\"candidate_search_order\":\"absolute_discharge_amplitude_descending\",\"maximum_limit_stage\":\"before_task5_first_arrival_analysis\",\"task5_waveform_scope\":\"local_incident_reflection_event_window\",\"batch_append_policy\":\"append_each_loc_batch_without_resorting_previous_shift_register_rows\",\"pdmap_id_rule\":\"previous_total_row_count_plus_current_loc_batch_index\",\"loc_storage_scale_divisor\":" + NumberJson(100000.0) + ",\"apparent_discharge_source\":\"restored_loc_absolute_incident_maximum\",\"calibration_factor_reapplied_to_loc_charge\":false,\"resolved_cable_length_m\":" + NumberJson(cable.CableLength) + ",\"joint_positions_including_ends_m\":" + NumberArrayJson(cable.JointPositionsIncludingEnds) + ",\"internal_joint_positions_m\":" + NumberArrayJson(cable.InternalJointPositions) + ",\"segment_start_positions_m\":" + NumberArrayJson(cable.Segments.Select((CableSegment x) => x.StartM)) + ",\"segment_end_positions_m\":" + NumberArrayJson(cable.Segments.Select((CableSegment x) => x.EndM)) + ",\"segment_insulation_types\":" + StringArrayJson(cable.Segments.Select((CableSegment x) => x.InsulationType)) + ",\"accessory_types\":" + StringArrayJson(cable.Accessories.Select((CableAccessory x) => x.Type)) + ",\"pulse_output_kind\":\"signed_raw_loc_window\",\"pulse_detection_source\":\"task17_validated_first_arrival_v1.6\",\"timing_index_kind\":\"task5_calibration_first_arrival_or_equivalent_stable_onset_fallback\",\"amplitude_index_kind\":\"absolute_maximum_inside_incident_event\",\"blind_method\":\"cable_length_velocity_round_trip_plus_calibration_bandwidth_adaptive\",\"location_algorithm_policy\":\"0_standard_task5_first_arrival;1_local_normalized_correlation;2_local_energy;3_robust_median_consensus;4_six_factor_propagation_evidence\",\"algorithm4_multi_factor\":" + (string.IsNullOrWhiteSpace(algorithm4MetricsJson) ? "{}" : algorithm4MetricsJson) + ",\"row_correspondence\":\"parameter_row_i_equals_signed_raw_loc_window_row_i\"}";
	}

    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental for rebuilt algorithm 4.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
	public void ResetAlgorithm4History()
	{
		algorithm4PositionHistory.Clear();
		algorithm4HistoryProjectKey = string.Empty;
	}

	private void PrepareAlgorithm4History(string projectPath, double cableLength)
	{
		string b = (string.IsNullOrWhiteSpace(projectPath) ? string.Empty : Path.GetFullPath(projectPath).ToUpperInvariant()) + "|" + cableLength.ToString("R", Inv);
		if (algorithm4HistoryProjectKey.Length > 0 && !string.Equals(algorithm4HistoryProjectKey, b, StringComparison.Ordinal))
		{
			algorithm4PositionHistory.Clear();
		}
		algorithm4HistoryProjectKey = b;
	}

	private AlgorithmTimingResult FindMultiFactorReflection(double[] samples, double center, double sigma, double threshold, int directOnset, int directPeak, int standardReflectionOnset, int standardReflectionPeak, int eventBlind, int roundTripSamples, ParsedCalibration calibration, double sampleRateHz, double cableLength)
	{
		AlgorithmTimingResult result = new AlgorithmTimingResult();
		if (samples == null || samples.Length < 16 || roundTripSamples <= 0)
		{
			return result;
		}
		int val = Math.Max(AlgorithmReflectionSearchRadius(eventBlind, roundTripSamples) * 3, Math.Max(eventBlind * 4, roundTripSamples / 12));
		val = Math.Min(Math.Max(24, roundTripSamples / 3), val);
		int num = Math.Max(directPeak + Math.Max(4, eventBlind / 3), standardReflectionPeak - val);
		int num2 = Math.Min(Math.Min(samples.Length - 2, directOnset + roundTripSamples), standardReflectionPeak + val);
		if (num2 <= num)
		{
			return result;
		}
		double num3 = Math.Max(threshold, Math.Max(0.0, sigma) * 3.0);
		int separation = Math.Max(3, Math.Min(Math.Max(3, eventBlind / 3), Math.Max(4, roundTripSamples / 60)));
		List<int> list = FindSeparatedAbsolutePeaks(samples, center, num, num2, num3, separation, 16);
		AddPeakCandidate(list, samples, center, standardReflectionPeak, num, num2, separation);
		if (list.Count == 0)
		{
			return result;
		}
		int num4 = Math.Max(8, Math.Min(96, Math.Max(8, eventBlind)));
		SignalFeature incident = MeasureSignalFeature(samples, center, directOnset, directPeak, num4);
		CalibrationReference calibration2 = BuildCalibrationReference(calibration, sampleRateHz, eventBlind);
		double num5 = Clamp((double)(standardReflectionOnset - directOnset) / Math.Max(1.0, roundTripSamples), 0.0, 1.0);
		bool flag = num5 <= 0.04;
		bool flag2 = num5 >= 0.96;
		int num6 = Math.Max(4, Math.Min(Math.Max(4, eventBlind / 2), Math.Max(6, roundTripSamples / 50)));
		int num7 = 0;
		List<MultiFactorCandidate> list2 = new List<MultiFactorCandidate>();
		foreach (int item in list)
		{
			int num8 = FindFirstArrivalLikeTask5(samples, item, Math.Max(eventBlind, num4), center, sigma);
			if (!ValidReflectionTiming(directOnset, num8, roundTripSamples))
			{
				continue;
			}
			if (num8 > standardReflectionOnset + num6)
			{
				num7++;
				continue;
			}
			SignalFeature signalFeature = MeasureSignalFeature(samples, center, num8, item, num4);
			if (!(signalFeature.PeakAbsolute < num3))
			{
				double num9 = BestPropagationCorrelation(samples, center, directPeak, item, num4);
				double num10 = HighFrequencyAttenuationConsistency(incident, signalFeature);
				double num11 = Clamp((double)(num8 - directOnset) / Math.Max(1.0, roundTripSamples), 0.0, 1.0);
				if ((!flag || !(num11 > 0.06)) && (!flag2 || !(num11 < 0.94)))
				{
					double num12 = ExponentialAttenuationConsistency(incident, signalFeature, num11, calibration?.AttenuationLossDb ?? double.NaN);
					double num13 = TerminalCalibrationMorphology(samples, center, directPeak, item, num4, num11, calibration2);
					double num14 = 0.45 * num9 + 0.23 * num10 + 0.17 * num12 + 0.15 * num13;
					double positionM = num11 * cableLength;
					double num15 = HistoryBoostForPosition(positionM, cableLength);
					MultiFactorCandidate multiFactorCandidate = new MultiFactorCandidate();
					multiFactorCandidate.Peak = item;
					multiFactorCandidate.Onset = num8;
					multiFactorCandidate.PositionM = positionM;
					multiFactorCandidate.Correlation = num9;
					multiFactorCandidate.HighFrequency = num10;
					multiFactorCandidate.Attenuation = num12;
					multiFactorCandidate.CalibrationMorphology = num13;
					multiFactorCandidate.HistoryBoost = num15;
					multiFactorCandidate.BaseScore = Clamp(num14, 0.0, 1.0);
					multiFactorCandidate.AdjustedScore = Clamp(num14 + num15, 0.0, 1.0);
					list2.Add(multiFactorCandidate);
				}
			}
		}
		if (list2.Count == 0)
		{
			return result;
		}
		list2 = (from x in list2
			orderby x.AdjustedScore descending, x.Correlation descending, Math.Abs(x.Peak - standardReflectionPeak)
			select x).ToList();
		MultiFactorCandidate best = list2[0];
		if (flag || flag2)
		{
			MultiFactorCandidate multiFactorCandidate2 = list2.OrderBy((MultiFactorCandidate x) => Math.Abs(x.Peak - standardReflectionPeak)).First();
			if (best.AdjustedScore - multiFactorCandidate2.AdjustedScore < 0.15)
			{
				best = multiFactorCandidate2;
			}
		}
		double similarityBand = Math.Max(0.055, best.AdjustedScore * 0.08);
		int num16 = list2.Count((MultiFactorCandidate x) => Math.Abs(best.AdjustedScore - x.AdjustedScore) <= similarityBand);
		double num17 = ((list2.Count > 1) ? Math.Abs(best.AdjustedScore - (from x in list2
			where x != best
			orderby x.AdjustedScore descending
			select x).First().AdjustedScore) : 1.0);
		double num18 = Math.Min(0.45, 0.12 * (double)Math.Max(0, num16 - 1) + ((num17 < 0.03) ? 0.08 : 0.0));
		double num19 = Clamp(best.AdjustedScore * (1.0 - num18), 0.0, 1.0);
		if (num19 >= 0.72)
		{
			RecordHighConfidencePosition(best.PositionM, num19, cableLength);
		}
		return new AlgorithmTimingResult
		{
			Valid = true,
			ReflectionPeak = best.Peak,
			ReflectionOnset = best.Onset,
			Score = num19,
			CorrelationScore = best.Correlation,
			HighFrequencyConsistencyScore = best.HighFrequency,
			AttenuationConsistencyScore = best.Attenuation,
			CalibrationMorphologyScore = best.CalibrationMorphology,
			AmbiguityPenalty = num18,
			HistoryBoost = best.HistoryBoost,
			CandidateCount = list2.Count,
			SimilarCandidateCount = num16,
			LaterEchoRejectedCount = num7,
			FirstArrivalPreserved = (Math.Abs(best.Onset - standardReflectionOnset) <= num6),
			HighConfidence = (num19 >= 0.72)
		};
	}

	private static List<int> FindSeparatedAbsolutePeaks(double[] samples, double center, int begin, int end, double minimum, int separation, int limit)
	{
		List<int> list = new List<int>();
		for (int i = Math.Max(1, begin); i <= Math.Min(samples.Length - 2, end); i++)
		{
			double num = Math.Abs(samples[i] - center);
			if (!(num < minimum) && num >= Math.Abs(samples[i - 1] - center) && num > Math.Abs(samples[i + 1] - center))
			{
				list.Add(i);
			}
		}
		List<int> list2 = new List<int>();
		foreach (int peak in list.OrderByDescending((int num2) => Math.Abs(samples[num2] - center)))
		{
			if (!list2.Any((int existing) => Math.Abs(existing - peak) < separation))
			{
				list2.Add(peak);
				if (list2.Count >= limit)
				{
					break;
				}
			}
		}
		return list2;
	}

	private static void AddPeakCandidate(List<int> peaks, double[] samples, double center, int requestedPeak, int begin, int end, int separation)
	{
		int peak = AbsoluteMaximumIndex(samples, Math.Max(begin, requestedPeak - Math.Max(2, separation / 2)), Math.Min(end, requestedPeak + Math.Max(2, separation / 2)), center);
		if (peak >= begin && peak <= end && !peaks.Any((int existing) => Math.Abs(existing - peak) < separation))
		{
			peaks.Add(peak);
		}
	}

	private static SignalFeature MeasureSignalFeature(double[] samples, double center, int onset, int peak, int radius)
	{
		int num = Math.Max(0, Math.Min(onset, peak - radius));
		int num2 = Math.Min(samples.Length - 1, Math.Max(peak + radius, onset + radius));
		double num3 = 0.0;
		double num4 = 0.0;
		double num5 = 0.0;
		double num6 = 0.0;
		double num7 = 0.0;
		int num8 = ClampIndex(peak, samples.Length);
		for (int i = num; i <= num2; i++)
		{
			double num9 = samples[i] - center;
			num3 += num9 * num9;
			if (num9 >= 0.0)
			{
				num5 += num9;
			}
			else
			{
				num6 += 0.0 - num9;
			}
			num7 = Math.Max(num7, Math.Abs(num9));
			if (i > num)
			{
				double num10 = samples[i] - samples[i - 1];
				num4 += num10 * num10;
			}
		}
		double num11 = Math.Max(num7 * 0.5, 1E-12);
		int num12 = num8;
		int j = num8;
		while (num12 > num && Math.Abs(samples[num12 - 1] - center) >= num11)
		{
			num12--;
		}
		for (; j < num2 && Math.Abs(samples[j + 1] - center) >= num11; j++)
		{
		}
		return new SignalFeature
		{
			PeakAbsolute = num7,
			WidthSamples = Math.Max(1, j - num12 + 1),
			RiseSamples = Math.Max(1, num8 - ClampIndex(onset, samples.Length) + 1),
			HighFrequencyDensity = num4 / Math.Max(num3, 1E-18),
			PositiveIntegral = num5,
			NegativeIntegral = num6,
			AbsoluteIntegral = num5 + num6
		};
	}

	private static double BestPropagationCorrelation(double[] samples, double center, int incidentPeak, int reflectionPeak, int radius)
	{
		double num = 0.0;
		double[] array = new double[5] { 0.82, 0.92, 1.0, 1.1, 1.2 };
		foreach (double num2 in array)
		{
			for (int j = -2; j <= 2; j++)
			{
				double num3 = 0.0;
				double num4 = 0.0;
				double num5 = 0.0;
				for (int k = -radius; k <= radius; k++)
				{
					int num6 = ClampIndex(incidentPeak + k, samples.Length);
					int num7 = ClampIndex(reflectionPeak + j + (int)Math.Round((double)k * num2), samples.Length);
					double num8 = samples[num6] - center;
					double num9 = samples[num7] - center;
					num3 += num8 * num8;
					num4 += num9 * num9;
					num5 += num8 * num9;
				}
				double val = ((num3 > 0.0 && num4 > 0.0) ? (Math.Abs(num5) / Math.Sqrt(num3 * num4)) : 0.0);
				num = Math.Max(num, val);
			}
		}
		return Clamp(num, 0.0, 1.0);
	}

	private static double HighFrequencyAttenuationConsistency(SignalFeature incident, SignalFeature reflection)
	{
		double num = reflection.HighFrequencyDensity / Math.Max(incident.HighFrequencyDensity, 1E-12);
		double num2 = ((num <= 1.2) ? Math.Exp((0.0 - Math.Abs(Math.Log(Math.Max(num, 1E-06) / 0.72))) / 1.15) : Math.Exp((0.0 - (num - 1.2)) * 2.4));
		double num3 = Math.Exp((0.0 - Math.Abs(Math.Log(Math.Max(reflection.WidthSamples / Math.Max(1.0, incident.WidthSamples), 1E-06) / 1.2))) / 0.95);
		double num4 = Math.Exp((0.0 - Math.Abs(Math.Log(Math.Max(reflection.RiseSamples / Math.Max(1.0, incident.RiseSamples), 1E-06) / 1.15))) / 1.05);
		return Clamp(0.55 * num2 + 0.27 * num3 + 0.18 * num4, 0.0, 1.0);
	}

	private static double ExponentialAttenuationConsistency(SignalFeature incident, SignalFeature reflection, double normalizedPosition, double attenuationLossDb)
	{
		double value = ((Finite(attenuationLossDb) && attenuationLossDb > 0.0) ? Math.Pow(10.0, (0.0 - attenuationLossDb) * normalizedPosition / 20.0) : Math.Exp(-0.38 * normalizedPosition));
		value = Clamp(value, 0.08, 1.0);
		double observed = reflection.PeakAbsolute / Math.Max(incident.PeakAbsolute, 1E-12);
		double observed2 = reflection.AbsoluteIntegral / Math.Max(incident.AbsoluteIntegral, 1E-12);
		double num = LogRatioConsistency(observed, value, 1.1);
		double num2 = LogRatioConsistency(observed2, value, 1.25);
		double expected = incident.PositiveIntegral / Math.Max(incident.NegativeIntegral, 1E-12);
		double num3 = LogRatioConsistency(reflection.PositiveIntegral / Math.Max(reflection.NegativeIntegral, 1E-12), expected, 1.6);
		return Clamp(0.48 * num + 0.37 * num2 + 0.15 * num3, 0.0, 1.0);
	}

	private static double LogRatioConsistency(double observed, double expected, double tolerance)
	{
		if (!Finite(observed) || !Finite(expected) || observed <= 0.0 || expected <= 0.0)
		{
			return 0.0;
		}
		return Clamp(Math.Exp((0.0 - Math.Abs(Math.Log(observed / expected))) / Math.Max(0.05, tolerance)), 0.0, 1.0);
	}

	private static CalibrationReference BuildCalibrationReference(ParsedCalibration calibration, double sampleRateHz, int eventBlind)
	{
		CalibrationReference calibrationReference = new CalibrationReference();
		if (calibration == null || calibration.Samples == null || calibration.Samples.Length < 16)
		{
			return calibrationReference;
		}
		double[] samples = calibration.Samples;
		double center = Median(samples);
		int fallback = AbsoluteMaximumIndex(samples, 0, Math.Max(0, samples.Length / 2), center);
		int num = IndexFromTime(calibration.StartTime, sampleRateHz, samples.Length, fallback);
		num = AbsoluteMaximumIndex(samples, num - Math.Max(4, eventBlind), num + Math.Max(4, eventBlind), center);
		int num2 = Math.Min(samples.Length - 1, num + Math.Max(4, eventBlind));
		int fallback2 = ((num2 < samples.Length - 1) ? AbsoluteMaximumIndex(samples, num2, samples.Length - 1, center) : num);
		int num3 = IndexFromTime(calibration.FinalTime, sampleRateHz, samples.Length, fallback2);
		num3 = AbsoluteMaximumIndex(samples, num3 - Math.Max(4, eventBlind), num3 + Math.Max(4, eventBlind), center);
		calibrationReference.Valid = num3 > num;
		calibrationReference.Samples = samples;
		calibrationReference.Center = center;
		calibrationReference.DirectPeak = num;
		calibrationReference.ReflectionPeak = num3;
		calibrationReference.DirectFeature = MeasureSignalFeature(samples, center, Math.Max(0, num - Math.Max(4, eventBlind)), num, Math.Max(8, Math.Min(96, eventBlind)));
		return calibrationReference;
	}

	private static double TerminalCalibrationMorphology(double[] samples, double center, int directPeak, int reflectionPeak, int radius, double normalizedPosition, CalibrationReference calibration)
	{
		if (calibration == null || !calibration.Valid)
		{
			return 0.55;
		}
		if (!(normalizedPosition <= 0.04) && !(normalizedPosition >= 0.96))
		{
			return 0.55;
		}
		double num = CrossWaveformCorrelation(samples, center, reflectionPeak, calibration.Samples, calibration.Center, calibration.ReflectionPeak, radius);
		double num2 = LogRatioConsistency(MeasureSignalFeature(samples, center, Math.Max(0, directPeak - radius), directPeak, radius).WidthSamples, calibration.DirectFeature.WidthSamples, 0.85);
		double num3 = Math.Max(Math.Exp((0.0 - normalizedPosition) / 0.028), Math.Exp((0.0 - (1.0 - normalizedPosition)) / 0.028));
		return Clamp(0.57 * num + 0.23 * num2 + 0.2 * num3, 0.0, 1.0);
	}

	private static double CrossWaveformCorrelation(double[] first, double firstCenter, int firstPeak, double[] second, double secondCenter, int secondPeak, int radius)
	{
		double num = 0.0;
		double num2 = 0.0;
		double num3 = 0.0;
		for (int i = -radius; i <= radius; i++)
		{
			double num4 = first[ClampIndex(firstPeak + i, first.Length)] - firstCenter;
			double num5 = second[ClampIndex(secondPeak + i, second.Length)] - secondCenter;
			num += num4 * num4;
			num2 += num5 * num5;
			num3 += num4 * num5;
		}
		if (!(num > 0.0) || !(num2 > 0.0))
		{
			return 0.0;
		}
		return Clamp(Math.Abs(num3) / Math.Sqrt(num * num2), 0.0, 1.0);
	}

	private double HistoryBoostForPosition(double positionM, double cableLength)
	{
		PositionHistoryEvidence positionHistoryEvidence = FindHistory(positionM, cableLength);
		if (positionHistoryEvidence == null || positionHistoryEvidence.HighConfidenceCount < 2)
		{
			return 0.0;
		}
		double num = positionHistoryEvidence.ConfidenceSum / (double)Math.Max(1, positionHistoryEvidence.HighConfidenceCount);
		return Clamp(0.025 * (double)positionHistoryEvidence.HighConfidenceCount + 0.035 * num, 0.0, 0.12);
	}

	private void RecordHighConfidencePosition(double positionM, double confidence, double cableLength)
	{
		PositionHistoryEvidence positionHistoryEvidence = FindHistory(positionM, cableLength);
		if (positionHistoryEvidence == null)
		{
			positionHistoryEvidence = new PositionHistoryEvidence();
			positionHistoryEvidence.PositionM = positionM;
			algorithm4PositionHistory.Add(positionHistoryEvidence);
		}
		double num = (double)positionHistoryEvidence.HighConfidenceCount + 1.0;
		positionHistoryEvidence.PositionM = (positionHistoryEvidence.PositionM * (double)positionHistoryEvidence.HighConfidenceCount + positionM) / num;
		positionHistoryEvidence.HighConfidenceCount++;
		positionHistoryEvidence.ConfidenceSum += confidence;
	}

	private PositionHistoryEvidence FindHistory(double positionM, double cableLength)
	{
		double tolerance = Math.Max(1.5, Math.Min(8.0, cableLength * 0.008));
		return (from x in algorithm4PositionHistory
			where Math.Abs(x.PositionM - positionM) <= tolerance
			orderby Math.Abs(x.PositionM - positionM)
			select x).FirstOrDefault();
	}

	private static string BuildAlgorithm4MetricsJson(int count, double correlationSum, double highFrequencySum, double attenuationSum, double calibrationSum, double ambiguityPenaltySum, double historyBoostSum, int ambiguousCount, int historyBoostCount, int highConfidenceCount, int rememberedPositionCount, int laterEchoRejectedCount, int firstArrivalPreservedCount)
	{
		double num = Math.Max(1, count);
		return "{\"enabled\":" + ((count > 0) ? "true" : "false") + ",\"weights\":{\"incident_reflection_correlation\":0.45,\"high_frequency_attenuation_consistency\":0.23,\"exponential_amplitude_and_bipolar_integral_attenuation\":0.17,\"terminal_calibration_morphology\":0.15},\"evaluated_pulse_count\":" + count.ToString(Inv) + ",\"mean_incident_reflection_correlation\":" + NumberJson((count > 0) ? (correlationSum / num) : double.NaN) + ",\"mean_high_frequency_attenuation_consistency\":" + NumberJson((count > 0) ? (highFrequencySum / num) : double.NaN) + ",\"mean_exponential_and_bipolar_integral_attenuation_consistency\":" + NumberJson((count > 0) ? (attenuationSum / num) : double.NaN) + ",\"mean_terminal_calibration_morphology\":" + NumberJson((count > 0) ? (calibrationSum / num) : double.NaN) + ",\"mean_ambiguity_penalty\":" + NumberJson((count > 0) ? (ambiguityPenaltySum / num) : double.NaN) + ",\"ambiguous_pulse_count\":" + ambiguousCount.ToString(Inv) + ",\"mean_repeated_position_history_boost\":" + NumberJson((count > 0) ? (historyBoostSum / num) : double.NaN) + ",\"history_boost_applied_count\":" + historyBoostCount.ToString(Inv) + ",\"high_confidence_count\":" + highConfidenceCount.ToString(Inv) + ",\"remembered_high_confidence_position_count\":" + rememberedPositionCount.ToString(Inv) + ",\"later_joint_or_terminal_echo_candidate_rejected_count\":" + laterEchoRejectedCount.ToString(Inv) + ",\"first_arrival_anchor_preserved_pulse_count\":" + firstArrivalPreservedCount.ToString(Inv) + ",\"reflection_selection_policy\":\"task17_task5_first_arrival_anchor;later_correlated_joint_or_terminal_rereflections_cannot_replace_first_arrival\",\"ambiguity_policy\":\"multiple_near_equal_reflection_candidates_reduce_probability\",\"history_policy\":\"same_project_repeated_high_confidence_positions_raise_later_ambiguous_or_noisy_candidate_weight\",\"row_33_meaning\":\"final_discharge_probability_for_algorithm_4\",\"row_35_meaning\":\"algorithm_4_high_confidence_probability_at_least_0.72\"}";
	}
}
