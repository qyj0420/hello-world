using System;
using System.Collections.Generic;
using System.Globalization;

namespace HVX.Controls
{
    /// <summary>
    /// Selects which calibrated partial-discharge pulses participate in the
    /// real-time maximum calculation.
    /// </summary>
    internal enum PdPolarity
    {
        Both = 0,
        Positive = 1,
        Negative = -1
    }

    /// <summary>
    /// The numeric enum value is the full-scale charge in pC.  Keeping the
    /// engineering value in the enum makes it safe for a LabVIEW ring control
    /// while still allowing an exact conversion without a lookup table.
    /// </summary>
    internal enum PdMeasurementRange
    {
        Uncalibrated = -1,
        Pc20 = 20,
        Pc50 = 50,
        Pc100 = 100,
        Pc200 = 200,
        Pc500 = 500,
        Nc1 = 1000,
        Nc2 = 2000,
        Nc5 = 5000,
        Nc10 = 10000,
        Nc20 = 20000,
        Nc50 = 50000,
        Nc100 = 100000,
        Nc150 = 150000,
        Nc200 = 200000
    }

    internal static class PdMeasurementRangeInfo
    {
        internal static double ToPicocoulombs(PdMeasurementRange range)
        {
            return range == PdMeasurementRange.Uncalibrated
                ? double.NaN
                : (double)(int)range;
        }

        internal static string GetDisplayName(PdMeasurementRange range)
        {
            if (range == PdMeasurementRange.Uncalibrated)
            {
                return "未校准";
            }

            int value = (int)range;
            if (value < 1000)
            {
                return value.ToString(CultureInfo.InvariantCulture) + " pC";
            }

            double nanocoulombs = value / 1000.0;
            return nanocoulombs.ToString("0.###", CultureInfo.InvariantCulture) + " nC";
        }

        internal static IList<PdMeasurementRange> GetOrderedValues()
        {
            return new[]
            {
                PdMeasurementRange.Pc20,
                PdMeasurementRange.Pc50,
                PdMeasurementRange.Pc100,
                PdMeasurementRange.Pc200,
                PdMeasurementRange.Pc500,
                PdMeasurementRange.Nc1,
                PdMeasurementRange.Nc2,
                PdMeasurementRange.Nc5,
                PdMeasurementRange.Nc10,
                PdMeasurementRange.Nc20,
                PdMeasurementRange.Nc50,
                PdMeasurementRange.Nc100,
                PdMeasurementRange.Nc150,
                PdMeasurementRange.Nc200,
                PdMeasurementRange.Uncalibrated
            };
        }
    }

    [Flags]
    internal enum PressureTestMetricValidity
    {
        None = 0,
        PartialDischarge = 1 << 0,
        Frequency = 1 << 1,
        EquivalentCapacitance = 1 << 2,
        DissipationFactor = 1 << 3,
        VoltageAtPartialDischarge = 1 << 4,
        PartialDischargePhase = 1 << 5
    }

    [Flags]
    internal enum PressureTestAnalysisStatus
    {
        None = 0,
        InvalidSampleRate = 1 << 0,
        InsufficientVoltageSamples = 1 << 1,
        InsufficientPartialDischargeSamples = 1 << 2,
        NonFiniteInputIgnored = 1 << 3,
        ChannelLengthMismatch = 1 << 4,
        ChannelNotAligned = 1 << 5,
        NoPartialDischargeEvent = 1 << 6,
        FrequencyNotFound = 1 << 7,
        FrequencyOutOfRange = 1 << 8,
        InsufficientCycles = 1 << 9,
        EnvelopeFitPoor = 1 << 10,
        NonDecayingEnvelope = 1 << 11,
        InvalidInductance = 1 << 12,
        DampingCorrectionSkipped = 1 << 13,
        SystemLossCalibrationMissing = 1 << 14,
        SystemLossExceedsMeasuredLoss = 1 << 15
    }

    /// <summary>
    /// Optional analysis controls.  The waveform values supplied to the
    /// analyzer are already expected to be calibrated to kV and pC.
    /// </summary>
    internal sealed class PressureTestAnalysisOptions
    {
        internal PressureTestAnalysisOptions()
        {
            MinimumFrequencyHz = 1.0;
            MaximumFrequencyHz = 5000.0;
            MinimumUsableCycles = 2;
            PdDetectionThresholdPc = 0.0;
            PdChannelDelaySamples = 0;
            SmoothingSamples = 0;
            MinimumEnvelopeFitRSquared = 0.85;
            SystemLossPercent = double.NaN;
        }

        /// <summary>Lowest accepted oscillation frequency.</summary>
        internal double MinimumFrequencyHz { get; set; }

        /// <summary>Highest accepted oscillation frequency.</summary>
        internal double MaximumFrequencyHz { get; set; }

        /// <summary>Minimum number of complete periods used for frequency.</summary>
        internal int MinimumUsableCycles { get; set; }

        /// <summary>
        /// Charge threshold after removal of the PD baseline.  Zero retains all
        /// pulses; a positive value rejects noise below that many pC.
        /// </summary>
        internal double PdDetectionThresholdPc { get; set; }

        /// <summary>
        /// Positive when the PD channel arrives later than the voltage channel.
        /// It is subtracted from the PD sample index for waveform correlation.
        /// </summary>
        internal int PdChannelDelaySamples { get; set; }

        /// <summary>Odd smoothing width; zero selects an automatic width.</summary>
        internal int SmoothingSamples { get; set; }

        internal double MinimumEnvelopeFitRSquared { get; set; }

        /// <summary>
        /// Calibrated loss of the measuring circuit in percentage points.
        /// NaN means that only uncorrected damping loss is available.
        /// </summary>
        internal double SystemLossPercent { get; set; }
        internal bool SkipDissipationFactor { get; set; }
    }

    /// <summary>
    /// Full-precision result.  Rounding to one decimal place belongs to the UI
    /// layer; invalid numeric fields remain NaN and are identified by Validity.
    /// </summary>
    internal sealed class PressureTestAnalysisResult
    {
        internal PressureTestAnalysisResult()
        {
            PartialDischargeMaximumPc = double.NaN;
            PartialDischargeSignedPc = double.NaN;
            PartialDischargeEventTimeSeconds = double.NaN;
            VoltageAtPartialDischargeKv = double.NaN;
            PartialDischargePhaseDegrees = double.NaN;
            OscillationFrequencyHz = double.NaN;
            NaturalFrequencyHz = double.NaN;
            DampingCoefficientPerSecond = double.NaN;
            EquivalentCapacitanceUf = double.NaN;
            RawDissipationFactorPercent = double.NaN;
            DissipationFactorPercent = double.NaN;
            EnvelopeFitRSquared = double.NaN;
            FrequencyConfidence = double.NaN;
        }

        internal PressureTestMetricValidity Validity { get; set; }
        internal PressureTestAnalysisStatus Status { get; set; }

        internal double PartialDischargeMaximumPc { get; set; }
        internal double PartialDischargeSignedPc { get; set; }
        internal int PartialDischargeSampleIndex { get; set; }
        internal double PartialDischargeEventTimeSeconds { get; set; }
        internal double VoltageAtPartialDischargeKv { get; set; }
        internal double PartialDischargePhaseDegrees { get; set; }

        /// <summary>Measured damped frequency, fd.</summary>
        internal double OscillationFrequencyHz { get; set; }

        /// <summary>Undamped natural frequency used for the LC calculation.</summary>
        internal double NaturalFrequencyHz { get; set; }

        internal double DampingCoefficientPerSecond { get; set; }
        internal double EquivalentCapacitanceUf { get; set; }
        internal double RawDissipationFactorPercent { get; set; }
        internal double DissipationFactorPercent { get; set; }
        internal double EnvelopeFitRSquared { get; set; }
        internal double FrequencyConfidence { get; set; }

        internal bool HasMetric(PressureTestMetricValidity metric)
        {
            return (Validity & metric) == metric;
        }

        internal static double RoundForDisplay(double value)
        {
            return IsFinite(value)
                ? Math.Round(value, 1, MidpointRounding.AwayFromZero)
                : double.NaN;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class PressurePlanRow
    {
        internal PressurePlanRow(int sequence, double u0Multiplier, int repetitionCount, double voltageKv)
        {
            Sequence = sequence;
            U0Multiplier = u0Multiplier;
            RepetitionCount = repetitionCount;
            VoltageKv = voltageKv;
        }

        /// <summary>One-based display order.</summary>
        internal int Sequence { get; private set; }
        internal double U0Multiplier { get; private set; }
        internal int RepetitionCount { get; private set; }
        internal double VoltageKv { get; private set; }
    }

    internal sealed class ProjectPressurePlan
    {
        private readonly IList<PressurePlanRow> rows;
        private readonly IList<string> warnings;

        internal ProjectPressurePlan(
            double phaseVoltageKv,
            IList<PressurePlanRow> rows,
            IList<string> warnings,
            bool isFreeValue = false)
        {
            PhaseVoltageKv = phaseVoltageKv;
            IsFreeValue = isFreeValue;
            this.rows = rows ?? new List<PressurePlanRow>();
            this.warnings = warnings ?? new List<string>();
        }

        internal double PhaseVoltageKv { get; private set; }
        internal bool IsFreeValue { get; private set; }
        internal IList<PressurePlanRow> Rows { get { return rows; } }
        internal IList<string> Warnings { get { return warnings; } }
        internal bool IsValid
        {
            get
            {
                if (!IsFinite(PhaseVoltageKv) || PhaseVoltageKv <= 0.0 || (!IsFreeValue && rows.Count == 0) || warnings.Count > 0) return false;
                if (IsFreeValue) return rows.Count == 0;
                foreach (PressurePlanRow row in rows)
                    if (!IsFinite(row.U0Multiplier) || row.U0Multiplier < 0.0 || row.RepetitionCount <= 0 ||
                        !IsFinite(row.VoltageKv) || row.VoltageKv < 0.0) return false;
                return true;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
