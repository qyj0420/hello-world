using System;

namespace HVX.Task17.Location;

/// <summary>Recommended LabVIEW type. Exactly one public constructor and one declared public method.
/// Detailed structured diagnostics are returned through statusMessage; no property-node chain is required.</summary>
public sealed class ProjectLocationAnalyzer
{
    private bool[] analysisPhaseQuadrants=new[]{true,true,true,true};
    /// <summary>Incident analysis quadrants [0,90), [90,180), [180,270), [270,360) degrees, applied before the maximum incident count.</summary>
    public bool[] AnalysisPhaseQuadrants
    {get{return (bool[])analysisPhaseQuadrants.Clone();}set{analysisPhaseQuadrants=AnalysisPhaseQuadrantPolicy.Copy(value);}}
    private double calibrationToleranceAdaptationLimitPercent=HVX.SignalEvidence.CalibrationQualityMath.DefaultAdaptationLimitPercent;
    /// <summary>Maximum automatic CAL-noise-based tolerance expansion, percent. Never reduces the explicit requested tolerance.</summary>
    public double CalibrationToleranceAdaptationLimitPercent
    {get{return calibrationToleranceAdaptationLimitPercent;}set{if(double.IsNaN(value)||double.IsInfinity(value)||value<0||value>100)
        throw new ArgumentOutOfRangeException("CalibrationToleranceAdaptationLimitPercent");calibrationToleranceAdaptationLimitPercent=value;}}
    public ProjectLocationAnalyzer() { }
    /// <summary>Capability marker for hosts: index 7 uses the independent eighth method.</summary>
    public bool SupportsUltraShortCableMethod { get { return true; } }

    /// <summary>Method 8 only: explicitly acknowledge the documented legacy LOC
    /// label bug (m/msec actually stores m/us). Off by default; original labels
    /// and the override are reported. Other algorithms never consume this flag.</summary>
    public bool Method8ConfirmedLegacyVelocityUnits { get; set; }

    /// <summary>Length included in CABLE total length between device and DUT near end, metres.
    /// Zero preserves legacy geometry. Positive values assume device-side CAL injection and 150 m/us lead velocity.</summary>
    public double ConnectingCableLengthM { get; set; }
    public string EndpointPolicy { get; set; }

    private double attenuationErrorTolerancePercent=15;
    /// <summary>Relative reflection amplitude error tolerance, percent (0..100). Default 15.
    /// Applies only when useAttenuationReference is enabled; existing method signature is unchanged.</summary>
    public double AttenuationErrorTolerancePercent
    {
        get { return attenuationErrorTolerancePercent; }
        set { if(double.IsNaN(value)||double.IsInfinity(value)||value<0||value>100)
            throw new ArgumentOutOfRangeException("AttenuationErrorTolerancePercent");attenuationErrorTolerancePercent=value; }
    }

    public bool AnalyzeProjectLocationIncremental(
        string projectPath,string locationFilePath,double locSampleHz,double[] locData,bool useLocData,
        double[,] previousParameterMatrix,double[,] previousPulseEnvelopeMatrix,
        double[,] previousFilteredPulseEnvelopeMatrix,long locationDataId,bool automaticAnalysis,
        int locationAlgorithm,bool useFirstArrivalTiming,bool useAttenuationReference,
        bool usePulseWidthComparison,int thresholdType,double manualThresholdAmplitude,
        double automaticThresholdMultiplier,int multipleReflectionSuppressionCount,
        int maximumDischargeCount,int incidentPreExtensionPoints,int roundTripPostExtensionPoints,
        double initialPhaseDegrees,double oscillationFrequencyHz,bool applyLinearFilter,
        double[] linearFilterCutoffsHz,double[,] previousEquivalentPulseWaveformMatrix,
        bool useEquivalentPulseWindow,double[] equivalentPulseWindowNs,out double[,] parameterMatrix2DOutput,
        out double[,] pulseEnvelopeMatrix2DOutput,out double[,] filteredPulseEnvelopeMatrix2DOutput,
        out double[,] equivalentPulseWaveformMatrix,out string statusMessage,
        out double[,] currentParameterMatrix2DOutput,out double[,] currentPulseEnvelopeMatrix2DOutput,
        out double[,] currentFilteredPulseEnvelopeMatrix2DOutput,out double[,] currentEquivalentPulseWaveformMatrix)
    {
        // Caller-owned shift registers are the only history store. Failure returns
        // a copy of those explicit inputs, never a partially completed analysis.
        parameterMatrix2DOutput=AstraExplicitHistory.Copy(previousParameterMatrix,36);
        pulseEnvelopeMatrix2DOutput=AstraExplicitHistory.Copy(previousPulseEnvelopeMatrix,0);
        filteredPulseEnvelopeMatrix2DOutput=AstraExplicitHistory.Copy(previousFilteredPulseEnvelopeMatrix,0);
        equivalentPulseWaveformMatrix=AstraExplicitHistory.Copy(previousEquivalentPulseWaveformMatrix,0);
        currentParameterMatrix2DOutput=new double[0,36];
        currentPulseEnvelopeMatrix2DOutput=new double[0,0];
        currentFilteredPulseEnvelopeMatrix2DOutput=new double[0,0];
        currentEquivalentPulseWaveformMatrix=new double[0,0];
        statusMessage="";string stage="explicit_history_validation",analysisStatus="null";
        try
        {
            AstraExplicitHistory.Validate(previousParameterMatrix,previousPulseEnvelopeMatrix,
                previousFilteredPulseEnvelopeMatrix,previousEquivalentPulseWaveformMatrix,"Explicit history");
            stage="input_validation";
            LocationInputValidation.ValidateSampleRate(locSampleHz,useLocData);
            LocationInputValidation.Validate(thresholdType,manualThresholdAmplitude,automaticThresholdMultiplier,
                applyLinearFilter,linearFilterCutoffsHz,useEquivalentPulseWindow,equivalentPulseWindowNs);
            if(locationAlgorithm==7)
            {
                stage="eighth_ultra_short_current_analysis";
                var eighth=LocationEngine.AnalyzeEighthCurrent(this,projectPath,locationFilePath,locSampleHz,locData,useLocData,
                    locationDataId,automaticAnalysis,useFirstArrivalTiming,useAttenuationReference,usePulseWidthComparison,
                    thresholdType,manualThresholdAmplitude,automaticThresholdMultiplier,multipleReflectionSuppressionCount,
                    maximumDischargeCount,incidentPreExtensionPoints,roundTripPostExtensionPoints,initialPhaseDegrees,
                    oscillationFrequencyHz,applyLinearFilter,linearFilterCutoffsHz,useEquivalentPulseWindow,equivalentPulseWindowNs);
                analysisStatus=eighth.Status;
                stage="explicit_history_append";
                double[,] ep,er,ef,ee;
                AstraExplicitHistory.Append(previousParameterMatrix,previousPulseEnvelopeMatrix,previousFilteredPulseEnvelopeMatrix,
                    previousEquivalentPulseWaveformMatrix,eighth.Parameters,eighth.Raw,eighth.Filtered,eighth.Equivalent,
                    out ep,out er,out ef,out ee);
                // Commit only after validation and all clones succeed, as for the original methods.
                double[,] cp=AstraExplicitHistory.Copy(eighth.Parameters,36),cr=AstraExplicitHistory.Copy(eighth.Raw,0),
                    cf=AstraExplicitHistory.Copy(eighth.Filtered,0),ce=AstraExplicitHistory.Copy(eighth.Equivalent,0);
                string completed=AstraExplicitHistory.Status(true,"completed",analysisStatus,
                    AstraExplicitHistory.Rows(previousParameterMatrix),cp.GetLength(0),"");
                parameterMatrix2DOutput=ep;pulseEnvelopeMatrix2DOutput=er;filteredPulseEnvelopeMatrix2DOutput=ef;
                equivalentPulseWaveformMatrix=ee;currentParameterMatrix2DOutput=cp;currentPulseEnvelopeMatrix2DOutput=cr;
                currentFilteredPulseEnvelopeMatrix2DOutput=cf;currentEquivalentPulseWaveformMatrix=ce;
                statusMessage=completed;return true;
            }
            // A new engine receives EMPTY history unconditionally. External history
            // cannot affect candidate selection, confidence, acceptance or ownership.
            stage="engine_initialization";
#pragma warning disable 618
            var engine=new LocationEngine();
#pragma warning restore 618
            engine.ConnectingCableLengthM=ConnectingCableLengthM;
            engine.ManufacturerEndpointPolicy=HVX.SignalEvidence.RawEndpointPolicy.Parse(EndpointPolicy);
            engine.AnalysisPhaseQuadrants=AnalysisPhaseQuadrants;
            engine.AttenuationErrorTolerancePercent=AttenuationErrorTolerancePercent;
            engine.CalibrationToleranceAdaptationLimitPercent=CalibrationToleranceAdaptationLimitPercent;
            string detail;double[,] nextP,nextR,nextF,nextE,nextReviewP,nextReviewR,nextReviewF;
            stage="analysis";
            bool ok=engine.AnalyzeProjectLocationIncremental(projectPath,locationFilePath,locSampleHz,locData,useLocData,
                new double[0,36],new double[0,0],new double[0,0],locationDataId,
                automaticAnalysis,locationAlgorithm,useFirstArrivalTiming,useAttenuationReference,usePulseWidthComparison,
                thresholdType,manualThresholdAmplitude,automaticThresholdMultiplier,multipleReflectionSuppressionCount,
                maximumDischargeCount,incidentPreExtensionPoints,roundTripPostExtensionPoints,initialPhaseDegrees,
                oscillationFrequencyHz,applyLinearFilter,linearFilterCutoffsHz,out nextP,out nextR,out nextF,out detail);
            analysisStatus=engine.StatusJson;
            if(!ok){statusMessage=AstraExplicitHistory.Status(false,stage,analysisStatus,
                AstraExplicitHistory.Rows(previousParameterMatrix),0,"Current analysis failed; only explicit history is returned.");return false;}
            stage="current_equivalent_waveform";
            nextE=engine.CompleteEquivalentOutput(new double[0,36],new double[0,0],
                nextP,useEquivalentPulseWindow,equivalentPulseWindowNs);
            stage="current_review_waveforms";
            engine.CompleteReviewOutput(new double[0,36],new double[0,0],
                new double[0,0],out nextReviewP,out nextReviewR,out nextReviewF);
            stage="current_mixed_output";
            engine.CompleteMixedReviewOutput(ref nextP,ref nextR,ref nextF,ref nextE,nextReviewP,nextReviewR,nextReviewF);
            stage="project_original_threshold_packet_retention";
            engine.CompleteProjectRetainedOutput(ref nextP,ref nextR,ref nextF,ref nextE,useEquivalentPulseWindow,equivalentPulseWindowNs);
            // Read-only diagnostics run after all physical and review outputs are fixed.
            engine.CompletePulseWidthDiagnostics(nextP);
            stage="current_endpoint_preclassification";
            engine.CompleteEndpointPreclassification(ref nextP);
            stage="current_homologous_peak_and_best_candidate_output";
            engine.CompletePeakTimingProjection(ref nextP,useFirstArrivalTiming);
            engine.CompleteUnifiedAttenuationReview(ref nextP);
            engine.CompleteReviewedSafetyContract(ref nextP,useFirstArrivalTiming);
            analysisStatus=engine.StatusJson;
            stage="explicit_history_append";
            double[,] totalP,totalR,totalF,totalE;
            AstraExplicitHistory.Append(previousParameterMatrix,previousPulseEnvelopeMatrix,
                previousFilteredPulseEnvelopeMatrix,previousEquivalentPulseWaveformMatrix,
                nextP,nextR,nextF,nextE,out totalP,out totalR,out totalF,out totalE);
            // Build independent current arrays and status before committing outputs.
            double[,] currentP=AstraExplicitHistory.Copy(nextP,36),currentR=AstraExplicitHistory.Copy(nextR,0),
                currentF=AstraExplicitHistory.Copy(nextF,0),currentE=AstraExplicitHistory.Copy(nextE,0);
            AstraExplicitHistory.NumberRows(currentP);
            string completedStatus=AstraExplicitHistory.Status(true,"completed",analysisStatus,
                AstraExplicitHistory.Rows(previousParameterMatrix),currentP.GetLength(0),"");
            parameterMatrix2DOutput=totalP;pulseEnvelopeMatrix2DOutput=totalR;
            filteredPulseEnvelopeMatrix2DOutput=totalF;equivalentPulseWaveformMatrix=totalE;
            currentParameterMatrix2DOutput=currentP;currentPulseEnvelopeMatrix2DOutput=currentR;
            currentFilteredPulseEnvelopeMatrix2DOutput=currentF;currentEquivalentPulseWaveformMatrix=currentE;
            statusMessage=completedStatus;return true;
        }
        catch(Exception ex)
        {
            if(locationAlgorithm==7&&ex is EighthResourceDiagnosticException)
                analysisStatus=((EighthResourceDiagnosticException)ex).DiagnosticJson;
            statusMessage=AstraExplicitHistory.Status(false,stage,analysisStatus,
                AstraExplicitHistory.Rows(previousParameterMatrix),0,HVX.Unified.Internal.RuntimeBridge.OneLine(ex));
            return false;
        }
    }
}
