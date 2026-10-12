using System;

namespace HVX.Task17.Location;

/// <summary>
/// Independent seventh method: descending original absolute incident peaks above
/// threshold, CAL incident correlation, physical-window and same-polarity dual-lobe
/// attenuation/width gates, then correlation ranking. Optional independent spatial
/// concentration only breaks near-score ties after all physical gates pass.
/// Default attenuation tolerance is 15%; explicit values are respected.
/// Weights: pair correlation, main-lobe attenuation, soft width, CAL incident similarity.
/// Attenuation and width are mandatory stages despite retained Boolean port positions.
/// Scores are engineering consistency, not probabilities. Existing six methods are unchanged.
/// </summary>
public sealed class SeventhLocationAnalyzer
{
    private bool[] analysisPhaseQuadrants=new[]{true,true,true,true};
    /// <summary>Incident analysis quadrants [0,90), [90,180), [180,270), [270,360) degrees. Applied before the maximum incident count; reflections retain their physical search window.</summary>
    public bool[] AnalysisPhaseQuadrants
    {get{return (bool[])analysisPhaseQuadrants.Clone();}set{analysisPhaseQuadrants=AnalysisPhaseQuadrantPolicy.Copy(value);}}
    private double calibrationToleranceAdaptationLimitPercent=HVX.SignalEvidence.CalibrationQualityMath.DefaultAdaptationLimitPercent;
    /// <summary>Maximum automatic CAL-noise-based tolerance expansion, percent. Never reduces the explicit requested tolerance.</summary>
    public double CalibrationToleranceAdaptationLimitPercent
    {get{return calibrationToleranceAdaptationLimitPercent;}set{if(double.IsNaN(value)||double.IsInfinity(value)||value<0||value>100)
        throw new ArgumentOutOfRangeException("CalibrationToleranceAdaptationLimitPercent");calibrationToleranceAdaptationLimitPercent=value;}}
    public SeventhLocationAnalyzer() { }

    /// <summary>Connecting lead included in CABLE total length, metres. Zero disables compensation.
    /// Positive values assume device-side CAL injection and a fixed lead velocity of 150 m/us.</summary>
    public double ConnectingCableLengthM { get; set; }
    public string EndpointPolicy { get; set; }

    /// <summary>Optional next_prior JSON from a preceding seventh analysis.
    /// Prior seeds are used only after context and explicit previous matrices match.
    /// Invalid or absent JSON disables cross-call prior use, not current analysis.
    /// Only locally unique, independently accepted seeds are exported; history-selected
    /// results never seed a later call. This is provenance checking, not authentication.</summary>
    public string PreviousSeventhPriorJson { get; set; }

    public bool AnalyzeSeventhLocationIncremental(
        string projectPath,string locationFilePath,double locSampleHz,double[] locData,bool useLocData,
        double[,] previousParameterMatrix,double[,] previousPulseEnvelopeMatrix,
        double[,] previousFilteredPulseEnvelopeMatrix,long locationDataId,bool automaticAnalysis,
        bool useFirstArrivalTiming,bool useAttenuationReference,bool usePulseWidthComparison,
        int thresholdType,double manualThresholdAmplitude,double automaticThresholdMultiplier,
        int multipleReflectionSuppressionCount,int maximumDischargeCount,
        int incidentPreExtensionPoints,int roundTripPostExtensionPoints,double initialPhaseDegrees,
        double oscillationFrequencyHz,bool applyLinearFilter,double[] linearFilterCutoffsHz,
        double[,] previousEquivalentPulseWaveformMatrix,bool useEquivalentPulseWindow,
        double[] equivalentPulseWindowNs,double minimumCorrelationPercent,double attenuationErrorPercent,
        double pulseWidthErrorPercent,double[] scoreWeights,out double[,] parameterMatrix2DOutput,
        out double[,] pulseEnvelopeMatrix2DOutput,out double[,] filteredPulseEnvelopeMatrix2DOutput,
        out double[,] equivalentPulseWaveformMatrix,out string statusMessage,
        out double[,] currentParameterMatrix2DOutput,out double[,] currentPulseEnvelopeMatrix2DOutput,
        out double[,] currentFilteredPulseEnvelopeMatrix2DOutput,out double[,] currentEquivalentPulseWaveformMatrix)
    {
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
            stage="engine_initialization";
#pragma warning disable 618
            var engine=new LocationEngine();
#pragma warning restore 618
            engine.ConnectingCableLengthM=ConnectingCableLengthM;
            engine.ManufacturerEndpointPolicy=HVX.SignalEvidence.RawEndpointPolicy.Parse(EndpointPolicy);
            engine.AnalysisPhaseQuadrants=AnalysisPhaseQuadrants;
            engine.CalibrationToleranceAdaptationLimitPercent=CalibrationToleranceAdaptationLimitPercent;
            engine.SeventhMode=true;
            engine.A7SetPreviousPrior(PreviousSeventhPriorJson,previousParameterMatrix,previousPulseEnvelopeMatrix);
            string detail;double[,] nextP,nextR,nextF;
            stage="seventh_current_analysis";
            bool ok=engine.AnalyzeSixthProjectCurrent(projectPath,locationFilePath,locSampleHz,locData,useLocData,
                locationDataId,automaticAnalysis,useFirstArrivalTiming,useAttenuationReference,usePulseWidthComparison,
                thresholdType,manualThresholdAmplitude,automaticThresholdMultiplier,multipleReflectionSuppressionCount,
                maximumDischargeCount,incidentPreExtensionPoints,roundTripPostExtensionPoints,initialPhaseDegrees,
                oscillationFrequencyHz,applyLinearFilter,linearFilterCutoffsHz,minimumCorrelationPercent,
                attenuationErrorPercent,pulseWidthErrorPercent,scoreWeights,out nextP,out nextR,out nextF,out detail);
            analysisStatus=engine.StatusJson;
            if(!ok)
            {
                statusMessage=SeventhHistoryStatus(false,stage,analysisStatus,AstraExplicitHistory.Rows(previousParameterMatrix),0,detail);
                return false;
            }
            stage="current_equivalent_waveform";
            double[,] nextE=engine.A6CompleteEquivalentOutput(nextP,useEquivalentPulseWindow,equivalentPulseWindowNs);
            engine.A7CompletePrior(nextP,nextR);
            // Seventh fixes corresponding timing before selection and never runs
            // the legacy endpoint-flip, confidence-promotion or candidate-recovery stages.
            analysisStatus=engine.StatusJson;
            // No legacy review, mixed-output rescoring or project acceptance stage runs here.
            // Joint A7 evidence supplied accepted/review pairs; output-only retention also
            // preserves actual unpaired pulses without manufacturing reflections.
            stage="explicit_history_append";
            double[,] totalP,totalR,totalF,totalE;
            AstraExplicitHistory.Append(previousParameterMatrix,previousPulseEnvelopeMatrix,
                previousFilteredPulseEnvelopeMatrix,previousEquivalentPulseWaveformMatrix,
                nextP,nextR,nextF,nextE,out totalP,out totalR,out totalF,out totalE);
            double[,] currentP=AstraExplicitHistory.Copy(nextP,36),currentR=AstraExplicitHistory.Copy(nextR,0),
                currentF=AstraExplicitHistory.Copy(nextF,0),currentE=AstraExplicitHistory.Copy(nextE,0);
            AstraExplicitHistory.NumberRows(currentP);
            string completedStatus=SeventhHistoryStatus(true,"completed",analysisStatus,
                AstraExplicitHistory.Rows(previousParameterMatrix),currentP.GetLength(0),"");
            parameterMatrix2DOutput=totalP;pulseEnvelopeMatrix2DOutput=totalR;
            filteredPulseEnvelopeMatrix2DOutput=totalF;equivalentPulseWaveformMatrix=totalE;
            currentParameterMatrix2DOutput=currentP;currentPulseEnvelopeMatrix2DOutput=currentR;
            currentFilteredPulseEnvelopeMatrix2DOutput=currentF;currentEquivalentPulseWaveformMatrix=currentE;
            statusMessage=completedStatus;return true;
        }
        catch(Exception ex)
        {
            statusMessage=SeventhHistoryStatus(false,stage,analysisStatus,
                AstraExplicitHistory.Rows(previousParameterMatrix),0,HVX.Unified.Internal.RuntimeBridge.OneLine(ex));
            return false;
        }
    }

    private static string SeventhHistoryStatus(bool ok,string stage,string current,int previousRows,int currentRows,string message)
    {
        return AstraExplicitHistory.Status(ok,stage,current,previousRows,currentRows,message,false);
    }
}
