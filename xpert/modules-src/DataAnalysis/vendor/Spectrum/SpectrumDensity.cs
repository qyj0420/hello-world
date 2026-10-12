using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace HVX.UnifiedSpectrum
{
    /// <summary>Full filtered-data bins, not camera/screen bins. Ranges include the upper endpoint in the last bin.</summary>
    internal sealed class SpectrumDensityBins
    {
        public int PositionBins, PhaseBins, ChargeBins;
        public double PositionMinimum, PositionMaximum, PhaseMinimum, PhaseMaximum,
            ChargeMinimum, ChargeMaximum;
        public double? PositionBinWidth, PhaseBinWidth, ChargeBinWidth;
        public string PositionBinWidthText, PhaseBinWidthText, ChargeBinWidthText;
        public string Policy = "visible_source_pulse_count_per_bin;half_open_intervals;last_bin_includes_upper_endpoint";
    }

    internal static class SpectrumDensity
    {
        internal static SpectrumDensityBins DescribePrpd(int phaseBins, int chargeBins, double maximumCharge)
        {
            var bins = new SpectrumDensityBins {
                PhaseBins = phaseBins, ChargeBins = chargeBins, PhaseMinimum = 0, PhaseMaximum = 360,
                ChargeMinimum = -maximumCharge, ChargeMaximum = maximumCharge
            };
            bins.PhaseBinWidth = Width(0, 360, phaseBins);
            bins.ChargeBinWidth = Width(-maximumCharge, maximumCharge, chargeBins);
            bins.PhaseBinWidthText = WidthText(0, 360, phaseBins, bins.PhaseBinWidth);
            bins.ChargeBinWidthText = WidthText(-maximumCharge, maximumCharge, chargeBins, bins.ChargeBinWidth);
            return bins;
        }

        internal static void AssignLocationPrpd(SpectrumView view, int positionBins, int phaseBins, int chargeBins)
        {
            double xmin = view.XMin, xmax = view.XMax, maximumCharge = 0;
            foreach (SpectrumPoint point in view.Points)
            {
                // The adapter currently excludes invalid/out-of-cable locations. Do not
                // clamp if a later adapter legitimately supplies a displayed outer point.
                xmin = Math.Min(xmin, point.X); xmax = Math.Max(xmax, point.X);
                maximumCharge = Math.Max(maximumCharge, Math.Abs(point.Y));
            }
            if (!(maximumCharge > 0)) maximumCharge = 1;
            var bins = DescribePrpd(phaseBins, chargeBins, maximumCharge);
            bins.PositionBins = positionBins; bins.PositionMinimum = xmin; bins.PositionMaximum = xmax;
            bins.PositionBinWidth = Width(xmin, xmax, positionBins);
            bins.PositionBinWidthText = WidthText(xmin, xmax, positionBins, bins.PositionBinWidth);
            view.DensityBins = bins; view.XMin = xmin; view.XMax = xmax; view.DensityMaximum = 1;
            var counts = new Dictionary<long, int>();
            var keys = new long[view.Points.Count];
            for (int i = 0; i < view.Points.Count; i++)
            {
                SpectrumPoint point = view.Points[i];
                int l = RangeIndex(point.X, xmin, xmax, positionBins);
                int phi = RangeIndex(point.Z, 0, 360, phaseBins);
                int q = ChargeIndex(point.Y, maximumCharge, chargeBins);
                long key = ((long)l * phaseBins + phi) * chargeBins + q;
                keys[i] = key;
                int count; counts.TryGetValue(key, out count);
                // Each L-PRPD point has exactly one original row. Count rows, not Q,
                // marker size, pixels, file count, or weighted/normalised density.
                count += point.SourceRows.Length;
                counts[key] = count;
                view.DensityMaximum = Math.Max(view.DensityMaximum, count);
            }
            for (int i = 0; i < view.Points.Count; i++) view.Points[i].DensityCount = counts[keys[i]];
        }

        internal static int ChargeIndex(double charge, double maximumCharge, int bins)
        {
            // Dividing first never forms the potentially overflowing 2*max|Q|.
            // Preserve the side of zero even when tiny Q/max underflows or rounds to 0.
            double unit = Math.Abs(charge) / maximumCharge;
            double middle = bins * 0.5;
            int result = (int)Math.Floor(charge < 0 ? middle - unit * middle : middle + unit * middle);
            if (charge < 0) result = Math.Min(result, (bins - 1) / 2);
            return Math.Max(0, Math.Min(bins - 1, result));
        }

        internal static int RangeIndex(double value, double minimum, double maximum, int bins)
        {
            if (value <= minimum) return 0;
            if (value >= maximum) return bins - 1;
            double span = maximum - minimum;
            double fraction = double.IsInfinity(span)
                ? (value * 0.5 - minimum * 0.5) / (maximum * 0.5 - minimum * 0.5)
                : (value - minimum) / span;
            return Math.Max(0, Math.Min(bins - 1, (int)Math.Floor(fraction * bins)));
        }

        static double? Width(double minimum, double maximum, int bins)
        {
            double difference = maximum - minimum;
            double width = double.IsInfinity(difference)
                ? maximum / bins - minimum / bins : difference / bins;
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0 ? (double?)width : null;
        }

        static string WidthText(double minimum, double maximum, int bins, double? width)
        {
            return width.HasValue ? width.Value.ToString("R", CultureInfo.InvariantCulture)
                : "(" + maximum.ToString("R", CultureInfo.InvariantCulture) + " - "
                  + minimum.ToString("R", CultureInfo.InvariantCulture) + ") / " + bins.ToString(CultureInfo.InvariantCulture);
        }
    }

    public partial class UnifiedSpectrumControl
    {
        [Browsable(false), Description("Maximum colour-bin pulse count in PRPD/L-PRPD; 1 for an empty density view, 0 in other modes.")]
        public int DensityMaximumCount
        { get { return view != null && (mode == 11 || mode == 12) ? view.DensityMaximum : 0; } }

        /// <summary>One exact colour-bin pulse count per current displayed point. A fresh array is returned.</summary>
        public int[] GetDensityCounts()
        {
            if (view == null || mode != 11 && mode != 12) return new int[0];
            var counts = new int[view.Points.Count];
            for (int i = 0; i < counts.Length; i++) counts[i] = view.Points[i].DensityCount;
            return counts;
        }

        /// <summary>
        /// Fresh 3x3 matrix: rows L (m), PHI (degrees), signed Q (pC); columns minimum,
        /// maximum, bin count. L has bin count 0 in ordinary PRPD. Other modes return 0x0.
        /// Ranges describe the complete filtered data, never the current camera viewport.
        /// </summary>
        public double[,] GetDensityBinRanges()
        {
            if (view == null || mode != 11 && mode != 12 || view.DensityBins == null) return new double[0, 0];
            SpectrumDensityBins bins = view.DensityBins;
            return new double[,] {
                { bins.PositionMinimum, bins.PositionMaximum, bins.PositionBins },
                { bins.PhaseMinimum, bins.PhaseMaximum, bins.PhaseBins },
                { bins.ChargeMinimum, bins.ChargeMaximum, bins.ChargeBins }
            };
        }
    }
}
