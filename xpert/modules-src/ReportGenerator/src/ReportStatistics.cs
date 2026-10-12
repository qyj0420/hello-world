using System;
using System.Collections.Generic;

namespace HVX.ReportGenerator
{
    /// <summary>Safe statistics over unified location-result matrices.</summary>
    public static class ReportStatistics
    {
        private sealed class Accumulator
        {
            internal int Count;
            internal int ChargeCount;
            internal double Sum;
            internal double Maximum = double.NaN;

            internal void Add(bool hasCharge, double charge)
            {
                Count++;
                if (!hasCharge) return;
                ChargeCount++;
                Sum += charge;
                if (double.IsNaN(Maximum) || charge > Maximum) Maximum = charge;
            }

            internal PhaseTestMetrics Finish(ReportPhase phase)
            {
                return new PhaseTestMetrics
                {
                    Phase = phase,
                    Count = Count,
                    ChargeSampleCount = ChargeCount,
                    MaximumChargePc = ChargeCount == 0 ? double.NaN : Maximum,
                    AverageChargePc = ChargeCount == 0 ? double.NaN : Sum / ChargeCount
                };
            }
        }

        public static ThreePhaseTestMetrics Compute(double[,] locationMatrix, double deviceU0Kv,
            VoltageInterval interval, LocationMatrixColumnMap columns)
        {
            VoltageInterval effectiveInterval = interval ?? VoltageInterval.AllResults();
            LocationMatrixColumnMap map = columns ?? LocationMatrixColumnMap.UnifiedLocationMatrix();
            var result = EmptyResult(deviceU0Kv, effectiveInterval);

            if (locationMatrix == null)
            {
                result.Status = "Location matrix is null.";
                return result;
            }

            if (!IsFinite(deviceU0Kv) || deviceU0Kv <= 0.0)
            {
                result.Status = "Device U0 must be finite and greater than zero.";
                return result;
            }

            int rows = locationMatrix.GetLength(0);
            int columnCount = locationMatrix.GetLength(1);
            if (rows == 0 || columnCount == 0)
            {
                result.Status = "Location matrix is empty.";
                return result;
            }

            if (!ValidColumn(map.VoltageKvColumn, columnCount))
            {
                result.Status = "Voltage column is unavailable.";
                return result;
            }

            if (map.OnlyAcceptedRows && !ValidColumn(map.AcceptedColumn, columnCount))
            {
                result.Status = "Accepted-row column is unavailable.";
                return result;
            }

            bool hasPhaseColumn = ValidColumn(map.PhaseColumn, columnCount);
            bool hasPrimaryCharge = ValidColumn(map.ChargePcColumn, columnCount);
            bool hasFallbackCharge = ValidColumn(map.FallbackChargePcColumn, columnCount);
            var all = new Accumulator();
            var phaseAccumulators = new[] { new Accumulator(), new Accumulator(), new Accumulator(), new Accumulator() };

            for (int row = 0; row < rows; row++)
            {
                double voltage = locationMatrix[row, map.VoltageKvColumn];
                if (!IsFinite(voltage) || voltage < 0.0) continue;
                double ratio = voltage / deviceU0Kv;
                if (!IsFinite(ratio) || ratio < 0.0) continue;
                if (!effectiveInterval.IsAllResults && (ratio > 2.0 ||
                    ratio < effectiveInterval.MinimumU0 || ratio > effectiveInterval.MaximumU0)) continue;

                if (map.OnlyAcceptedRows)
                {
                    double accepted = locationMatrix[row, map.AcceptedColumn];
                    if (!IsFinite(accepted) || Math.Abs(accepted - map.AcceptedValue) > 1e-9) continue;
                }

                int phase = hasPhaseColumn ? DecodePhase(locationMatrix[row, map.PhaseColumn]) : 0;
                if (hasPhaseColumn && phase == 0) continue;

                double charge;
                bool hasCharge = TryReadCharge(locationMatrix, row, map, hasPrimaryCharge, hasFallbackCharge, out charge);
                all.Add(hasCharge, charge);
                if (phase >= 1 && phase <= 3) phaseAccumulators[phase].Add(hasCharge, charge);
            }

            result.All = all.Finish(ReportPhase.All);
            result.A = phaseAccumulators[1].Finish(ReportPhase.A);
            result.B = phaseAccumulators[2].Finish(ReportPhase.B);
            result.C = phaseAccumulators[3].Finish(ReportPhase.C);
            result.IsValid = true;
            if (!hasPhaseColumn)
                result.Status = "OK; phase column unavailable, therefore only ALL metrics are populated.";
            else if (!hasPrimaryCharge && !hasFallbackCharge)
                result.Status = "OK; charge columns unavailable, therefore max/average are NaN.";
            else
                result.Status = "OK";
            return result;
        }

        public static ThreePhaseTestMetrics Compute(double[,] locationMatrix, double deviceU0Kv,
            VoltageInterval interval)
        {
            return Compute(locationMatrix, deviceU0Kv, interval, LocationMatrixColumnMap.UnifiedLocationMatrix());
        }

        public static ThreePhaseTestMetrics Compute(double[,] locationMatrix, double deviceU0Kv,
            double minimumU0, double maximumU0, LocationMatrixColumnMap columns)
        {
            if (!IsFinite(minimumU0) || !IsFinite(maximumU0) || minimumU0 < 0.0 ||
                maximumU0 > 2.0 || minimumU0 > maximumU0)
            {
                var invalid = EmptyResult(deviceU0Kv, VoltageInterval.AllResults());
                invalid.Status = "Voltage interval must be a closed subset of 0..2U0.";
                return invalid;
            }

            return Compute(locationMatrix, deviceU0Kv,
                new VoltageInterval("custom", string.Empty, minimumU0, maximumU0), columns);
        }

        public static ThreePhaseTestMetrics Calculate(double[,] locationMatrix, double deviceU0Kv,
            VoltageInterval interval, LocationMatrixColumnMap columns)
        {
            return Compute(locationMatrix, deviceU0Kv, interval, columns);
        }

        /// <summary>
        /// Returns the locked all-results range plus the two requested default ranges. All ranges are closed;
        /// consequently a row exactly at U0 belongs to both adjacent presentation ranges.
        /// </summary>
        public static VoltageInterval[] CreateDefaultIntervals(bool projectOlderThanOneYear)
        {
            double upper = projectOlderThanOneYear ? 1.7 : 2.0;
            return new[]
            {
                VoltageInterval.AllResults(),
                new VoltageInterval("le_u0", "≤U0", 0.0, 1.0, true, false),
                new VoltageInterval("u0_to_limit", "U0≤U≤" + upper.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "U0",
                    1.0, upper, true, false)
            };
        }

        public static ThreePhaseTestMetrics[] ComputeIntervals(double[,] locationMatrix, double deviceU0Kv,
            IEnumerable<VoltageInterval> intervals, LocationMatrixColumnMap columns)
        {
            if (intervals == null) return new ThreePhaseTestMetrics[0];
            var output = new List<ThreePhaseTestMetrics>();
            foreach (VoltageInterval interval in intervals)
                if (interval != null) output.Add(Compute(locationMatrix, deviceU0Kv, interval, columns));
            return output.ToArray();
        }

        private static ThreePhaseTestMetrics EmptyResult(double deviceU0Kv, VoltageInterval interval)
        {
            return new ThreePhaseTestMetrics
            {
                DeviceU0Kv = deviceU0Kv,
                Interval = interval ?? VoltageInterval.AllResults(),
                IsValid = false
            };
        }

        private static bool TryReadCharge(double[,] matrix, int row, LocationMatrixColumnMap map,
            bool hasPrimary, bool hasFallback, out double charge)
        {
            if (hasPrimary)
            {
                double value = matrix[row, map.ChargePcColumn];
                if (IsFinite(value))
                {
                    charge = Math.Abs(value);
                    return true;
                }
            }

            if (hasFallback)
            {
                double value = matrix[row, map.FallbackChargePcColumn];
                if (IsFinite(value))
                {
                    charge = Math.Abs(value);
                    return true;
                }
            }

            charge = double.NaN;
            return false;
        }

        private static int DecodePhase(double value)
        {
            if (!IsFinite(value)) return 0;
            int phase = (int)Math.Round(value);
            return phase >= 1 && phase <= 3 && Math.Abs(value - phase) <= 1e-9 ? phase : 0;
        }

        private static bool ValidColumn(int column, int columnCount)
        {
            return column >= 0 && column < columnCount;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
