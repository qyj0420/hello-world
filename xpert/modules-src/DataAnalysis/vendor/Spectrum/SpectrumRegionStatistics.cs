using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // A display-only row-identity layer, independent of host filters,
        // acceptance flags and the input used for equipment evaluation.
        bool[] regionStatisticsMask, regionStatisticsCombinedMask, regionStatisticsCombinedReview;
        int[] regionStatisticsRows = new int[0];
        int regionStatisticsMode, regionStatisticsRectangleCount, regionStatisticsEllipseCount;
        double regionStatisticsX0, regionStatisticsX1, regionStatisticsY0, regionStatisticsY1;
        bool regionStatisticsPriorFtAuto;
        double regionStatisticsPriorFtX0, regionStatisticsPriorFtX1, regionStatisticsPriorFtY0, regionStatisticsPriorFtY1;
        readonly Dictionary<int, RegionViewRange> regionStatisticsPriorViews = new Dictionary<int, RegionViewRange>();
        sealed class RegionViewRange
        {
            internal double X0, X1, Y0, Y1;
            internal bool Automatic;
        }

        [Browsable(false)] public bool RegionStatisticsActive { get { return regionStatisticsMask != null; } }
        [Browsable(false), Description("Original source rows selected when entering regional statistics; existing filters still apply independently.")]
        public int[] RegionStatisticsSourceRowIndices { get { return (int[])regionStatisticsRows.Clone(); } }
        public event EventHandler RegionStatisticsChanged;

        [Browsable(false)]
        public string RegionStatisticsSummary
        {
            get
            {
                if (!RegionStatisticsActive) return "";
                string shape = regionStatisticsEllipseCount == 0 ? L("rectangles", "矩形")
                    : regionStatisticsRectangleCount == 0 ? L("ellipses", "椭圆") : L("mixed shapes", "混合形状");
                string x = regionStatisticsMode == 4 ? "F" : regionStatisticsMode == 2 ? "φ" : "L", y = regionStatisticsMode == 4 ? "T" : "Q";
                return ModeCaption(regionStatisticsMode) + " " + L("selection bounds", "选区边界") + ": " +
                    RegionStatisticsRange(x, regionStatisticsX0, regionStatisticsX1) + "; " +
                    RegionStatisticsRange(y, regionStatisticsY0, regionStatisticsY1) + "; " +
                    (regionStatisticsRectangleCount + regionStatisticsEllipseCount).ToString(CultureInfo.InvariantCulture) + " " + shape +
                    "; " + L("visible rows", "当前数据") + ": " + SummaryTotalDischargeCount.ToString(CultureInfo.InvariantCulture);
            }
        }
        string RegionStatisticsRange(string key, double minimum, double maximum)
        {
            double scale = key == "F" ? 1e6 : key == "T" ? 1000 : key == "Q" ? ChargeDisplayScale : 1;
            string unit = key == "F" ? "MHz" : key == "T" ? "µs" : key == "Q" ? ChargeUnitLabel : key == "φ" ? "°" : "m";
            return key + " " + (minimum / scale).ToString("G5", CultureInfo.InvariantCulture) + "–" +
                (maximum / scale).ToString("G5", CultureInfo.InvariantCulture) + " " + unit;
        }
        string SpectrumTitleText { get { return DisplayModeName + (capacitorStatisticsMode?L(" · Capacitor PD · location not measured"," · 电容局放 · 不测量位置"):"") + (RegionStatisticsActive ? " · " + L("Regional statistics", "区域统计") : ""); } }
        bool IsRegionStatisticsRow(int row)
        { return regionStatisticsMask == null || row >= 0 && row < regionStatisticsMask.Length && regionStatisticsMask[row]; }
        bool[] DrawingReviewVisibility()
        {
            if (regionStatisticsMask == null) return reviewVisibility;
            if (regionStatisticsCombinedMask != null && ReferenceEquals(regionStatisticsCombinedReview, reviewVisibility)) return regionStatisticsCombinedMask;
            var mask = new bool[source.GetLength(0)];
            for (int r = 0; r < mask.Length; r++) mask[r] = regionStatisticsMask[r] && IsReviewRowVisible(r);
            regionStatisticsCombinedReview = reviewVisibility;
            return regionStatisticsCombinedMask = mask;
        }
        bool EnterRegionStatistics(int[] rows)
        {
            if (State.Regions.Count == 0) return false;
            if (!RegionStatisticsActive)
            {
                regionStatisticsPriorViews.Clear();
                regionStatisticsPriorFtAuto = frequencyTimeAutoRange;
                regionStatisticsPriorFtX0 = ftX0; regionStatisticsPriorFtX1 = ftX1;
                regionStatisticsPriorFtY0 = ftY0; regionStatisticsPriorFtY1 = ftY1;
                foreach (var pair in states)
                {
                    var state = pair.Value;
                    regionStatisticsPriorViews.Add(pair.Key, new RegionViewRange {
                        X0 = state.X0, X1 = state.X1, Y0 = state.Y0, Y1 = state.Y1,
                        Automatic = (pair.Key != 4 || frequencyTimeAutoRange) && state.HasAutomaticRange && state.X0 == state.AutoX0 && state.X1 == state.AutoX1 && state.Y0 == state.AutoY0 && state.Y1 == state.AutoY1 });
                }
            }
            regionStatisticsMode = mode;
            regionStatisticsRectangleCount = State.Regions.Count(r => !r.Circle);
            regionStatisticsEllipseCount = State.Regions.Count(r => r.Circle);
            regionStatisticsX0 = State.Regions.Min(r => r.X0); regionStatisticsX1 = State.Regions.Max(r => r.X1);
            regionStatisticsY0 = State.Regions.Min(r => r.Y0); regionStatisticsY1 = State.Regions.Max(r => r.Y1);
            regionStatisticsRows = (int[])rows.Clone();
            regionStatisticsMask = new bool[source.GetLength(0)];
            foreach (int row in rows) regionStatisticsMask[row] = true;
            regionStatisticsCombinedMask = regionStatisticsCombinedReview = null;
            ClearHit(); InvalidateAutomaticReviewRanges(); ClearDataViews(); Rebuild(false);
            return true;
        }
        /// <summary>Removes only the regional display layer; never resets host, phase, voltage or acceptance filters.</summary>
        public bool ExitRegionStatistics()
        {
            if (!RegionStatisticsActive) return false;
            FlushReviewPreview();
            regionStatisticsMask = regionStatisticsCombinedMask = regionStatisticsCombinedReview = null;
            regionStatisticsRows = new int[0];
            frequencyTimeAutoRange = regionStatisticsPriorFtAuto;
            ftX0 = regionStatisticsPriorFtX0; ftX1 = regionStatisticsPriorFtX1;
            ftY0 = regionStatisticsPriorFtY0; ftY1 = regionStatisticsPriorFtY1;
            foreach (var pair in states)
            {
                RegionViewRange prior;
                var state = pair.Value;
                if (regionStatisticsPriorViews.TryGetValue(pair.Key, out prior) && !prior.Automatic)
                {
                    state.X0 = prior.X0; state.X1 = prior.X1; state.Y0 = prior.Y0; state.Y1 = prior.Y1;
                    state.HasAutomaticRange = false; state.ReviewRangeDirty = false;
                }
                else state.ReviewRangeDirty = true;
            }
            regionStatisticsPriorViews.Clear();
            ClearHit(); ClearDataViews(); Rebuild(false);
            NotifyHost(RegionStatisticsChanged); NotifyHost(ViewChanged);
            return true;
        }
        void ClearRegionStatisticsForNewSource()
        {
            regionStatisticsMask = regionStatisticsCombinedMask = regionStatisticsCombinedReview = null;
            regionStatisticsRows = new int[0]; regionStatisticsPriorViews.Clear();
        }
    }
}
