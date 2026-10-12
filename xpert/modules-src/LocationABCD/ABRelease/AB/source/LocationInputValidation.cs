using System;

namespace HVX.Task17.Location;

/// <summary>Reject invalid numeric controls before starting a current analysis.
/// Called only by the audited Project and Seventh public entry points. Legacy
/// finite defaults, optional switches and method-six behaviour are preserved.</summary>
internal static class LocationInputValidation
{
    internal static void ValidateSampleRate(double sampleRateHz, bool useMemory)
    {
        RequireFinite(sampleRateHz, "locSampleHz");
        if(sampleRateHz<0 || (useMemory&&sampleRateHz==0))
            throw new ArgumentOutOfRangeException("locSampleHz", "Use explicit positive Hz, or zero for file-header auto detection.");
    }
    internal static void Validate(int thresholdType, double manualThresholdAmplitude,
        double automaticThresholdMultiplier, bool applyLinearFilter,
        double[] linearFilterCutoffsHz, bool useEquivalentPulseWindow,
        double[] equivalentPulseWindowNs)
    {
        // Project methods historically ignore the unused threshold fields.
        // Do not turn legacy NaN sentinels in disabled controls into errors.
        // Seventh's existing engine validation remains independently stricter.
        if (thresholdType == -1) RequireFinite(manualThresholdAmplitude, "manualThresholdAmplitude");
        if (thresholdType == 1) RequireFinite(automaticThresholdMultiplier, "automaticThresholdMultiplier");
        ValidateActivePair(applyLinearFilter, linearFilterCutoffsHz, "linearFilterCutoffsHz");
        ValidateActivePair(useEquivalentPulseWindow, equivalentPulseWindowNs, "equivalentPulseWindowNs");
    }

    private static void ValidateActivePair(bool enabled, double[] values, string name)
    {
        // Null/short arrays retain the established automatic/disabled fallback.
        // Disabled controls are intentionally ignored, as in the existing API.
        if (!enabled || values == null || values.Length < 2) return;
        RequireFinite(values[0], name + "[0]");
        RequireFinite(values[1], name + "[1]");
    }

    private static void RequireFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "The active numeric control must be finite.");
    }
}
