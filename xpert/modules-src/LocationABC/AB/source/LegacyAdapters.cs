using System;
using System.IO;

namespace HVX.Task17.Location;

internal sealed partial class LocationEngine
{
    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool AnalyzeLocationFileIncrementalWithLinearFilter(string calibrationFilePath,string locationFilePath,
        string testFilePath,string cableFilePath,string projectFilePath,double[,] previousParameterMatrix,
        double[,] previousPulseEnvelopeMatrix,double[,] previousFilteredPulseEnvelopeMatrix,long locationDataId,
        bool automaticAnalysis,int locationAlgorithm,bool useFirstArrivalTiming,bool useAttenuationReference,
        bool usePulseWidthComparison,double sampleRateHz,int thresholdType,double manualThresholdAmplitude,
        double automaticThresholdMultiplier,int multipleReflectionSuppressionCount,int maximumDischargeCount,
        int incidentPreExtensionPoints,int roundTripPostExtensionPoints,bool applyLinearFilter,double[] linearFilterCutoffsHz,
        out double[,] parameterMatrix2DOutput,out double[,] pulseEnvelopeMatrix2DOutput,out double[,] filteredPulseEnvelopeMatrix2DOutput)
    {
        return AnalyzeLocationFileIncrementalWithPhaseReferenceAndLinearFilter(calibrationFilePath,locationFilePath,
            testFilePath,cableFilePath,projectFilePath,previousParameterMatrix,previousPulseEnvelopeMatrix,
            previousFilteredPulseEnvelopeMatrix,locationDataId,automaticAnalysis,locationAlgorithm,useFirstArrivalTiming,
            useAttenuationReference,usePulseWidthComparison,sampleRateHz,thresholdType,manualThresholdAmplitude,
            automaticThresholdMultiplier,multipleReflectionSuppressionCount,maximumDischargeCount,incidentPreExtensionPoints,
            roundTripPostExtensionPoints,0,0,applyLinearFilter,linearFilterCutoffsHz,out parameterMatrix2DOutput,
            out pulseEnvelopeMatrix2DOutput,out filteredPulseEnvelopeMatrix2DOutput);
    }
    [Obsolete("Legacy compatibility only. Use AnalyzeProjectLocationIncremental.")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public bool AnalyzeLocationFileIncrementalWithPhaseReferenceAndLinearFilter(string calibrationFilePath,string locationFilePath,
        string testFilePath,string cableFilePath,string projectFilePath,double[,] previousParameterMatrix,
        double[,] previousPulseEnvelopeMatrix,double[,] previousFilteredPulseEnvelopeMatrix,long locationDataId,
        bool automaticAnalysis,int locationAlgorithm,bool useFirstArrivalTiming,bool useAttenuationReference,
        bool usePulseWidthComparison,double sampleRateHz,int thresholdType,double manualThresholdAmplitude,
        double automaticThresholdMultiplier,int multipleReflectionSuppressionCount,int maximumDischargeCount,
        int incidentPreExtensionPoints,int roundTripPostExtensionPoints,double initialPhaseDegrees,double oscillationFrequencyHz,
        bool applyLinearFilter,double[] linearFilterCutoffsHz,out double[,] parameterMatrix2DOutput,
        out double[,] pulseEnvelopeMatrix2DOutput,out double[,] filteredPulseEnvelopeMatrix2DOutput)
    {
        if(locationAlgorithm>=0&&locationAlgorithm<4)
        {
            bool ok=AnalyzeLocationFileIncrementalWithPhaseReference(calibrationFilePath,locationFilePath,testFilePath,
                cableFilePath,projectFilePath,previousParameterMatrix,previousPulseEnvelopeMatrix,locationDataId,
                automaticAnalysis,locationAlgorithm,sampleRateHz,thresholdType,manualThresholdAmplitude,
                automaticThresholdMultiplier,multipleReflectionSuppressionCount,maximumDischargeCount,
                incidentPreExtensionPoints,roundTripPostExtensionPoints,initialPhaseDegrees,oscillationFrequencyHz,
                out parameterMatrix2DOutput,out pulseEnvelopeMatrix2DOutput);
            filteredPulseEnvelopeMatrix2DOutput=pulseEnvelopeMatrix2DOutput;FilteredPulseEnvelopeMatrix2D=pulseEnvelopeMatrix2DOutput;
            return ok;
        }
        string project=projectFilePath;
        if(String.IsNullOrWhiteSpace(project))project=Path.GetDirectoryName(Path.GetFullPath(locationFilePath));
        if(File.Exists(project))project=Path.GetDirectoryName(project);
        string status;
        return AnalyzeProjectLocationIncremental(project,locationFilePath,sampleRateHz,new double[0],false,
            previousParameterMatrix,previousPulseEnvelopeMatrix,previousFilteredPulseEnvelopeMatrix,locationDataId,
            automaticAnalysis,4,useFirstArrivalTiming,useAttenuationReference,usePulseWidthComparison,thresholdType,
            manualThresholdAmplitude,automaticThresholdMultiplier,multipleReflectionSuppressionCount,maximumDischargeCount,
            incidentPreExtensionPoints,roundTripPostExtensionPoints,initialPhaseDegrees,oscillationFrequencyHz,
            applyLinearFilter,linearFilterCutoffsHz,out parameterMatrix2DOutput,out pulseEnvelopeMatrix2DOutput,
            out filteredPulseEnvelopeMatrix2DOutput,out status);
    }
}

