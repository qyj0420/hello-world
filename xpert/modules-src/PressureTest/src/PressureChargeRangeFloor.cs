using System;
using System.IO;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    // Application charge floor, not a change to the frozen ADC advisor.
    // Preserve the original advisor result and every measured quality flag.
    internal static class PressureChargeRangeFloor
    {
        internal static int MinimumIndex(double coefficient)
        {
            if(Double.IsNaN(coefficient)||Double.IsInfinity(coefficient)||coefficient<=0)return -1;
            for(int i=0;i<BenchSettings.Ranges.Length;i++)
                if(BenchSettings.Ranges[i]*coefficient>=100)return i;
            return -1;
        }
        internal static LocationRangeResult Bound(LocationRangeResult original,int current,double coefficient)
        {
            int minimum=MinimumIndex(coefficient);
            if(minimum<0||current<minimum||current>=BenchSettings.Ranges.Length)
                throw new InvalidDataException("当前校准与量程不满足最低100 pC、最高20 V的测试范围。");
            if(original==null||original.RecommendedIndex<0||original.RecommendedIndex>=BenchSettings.Ranges.Length)
                return original; // Invalid evidence stays invalid, not a fabricated recommendation.
            int next=BoundedNext(original,current,Math.Max(minimum,original.RecommendedIndex));
            if(next==original.RecommendedIndex)return original;
            var bounded=RangeDecisionPolicy.CopyQuality(original);
            bounded.RecommendedIndex=next;
            bounded.RecommendedFullScaleVolts=BenchSettings.Ranges[next];
            bounded.Change=next!=current;
            bounded.Reason=(original.Reason??"")+"；真实定位窗削顶按有界预估最多升两档，降档仍仅相邻一档，且不低于100 pC；真实峰值未知，原幅值、窗口及质量标志不变。";
            return bounded;
        }
        static int BoundedNext(LocationRangeResult original,int current,int recommended)
        {
            return HVX.Acquisition.BoundedClippedRangePolicy.Next(current,recommended,BenchSettings.Ranges.Length,
                original.WindowValid&&original.FactoryCycleWindow,original.LocationSampleCount,
                original.LocationClippedSamples,original.LocationLongestClippedRun);
        }
        internal static bool IsLowOnlyAtFloor(LocationRangeResult quality,int current,double coefficient)
        {
            // Pressure capture does not drive a calibration source. At the
            // charge floor a stored expected calibration pulse may suggest
            // increasing range even though the actual input is tiny. Keep that
            // prediction in evidence, without adjusting a valid low-only input.
            return current==MinimumIndex(coefficient)&&HasOnlyLowOccupancy(quality,current)&&
                quality.ExpectedCalibrationVolts<=BenchSettings.Ranges[BenchSettings.Ranges.Length-1];
        }
        internal static void BoundReview(PressureRangeReviewSnapshot review,int current,double coefficient)
        {
            if(review==null||review.Quality==null)return;
            var original=review.Quality;
            review.Quality=Bound(original,current,coefficient);
            if(!Object.ReferenceEquals(original,review.Quality))review.OriginalQuality=original;
            review.NextRangeIndex=BoundedNext(original,current,Math.Max(review.NextRangeIndex,MinimumIndex(coefficient)));
            if(IsLowOnlyAtFloor(review.Quality,current,coefficient))review.NextRangeIndex=current;
            review.CanApply=review.CanApply&&review.NextRangeIndex!=current;
        }
        internal static bool IsLowOnlyWithoutAdjustment(LocationRangeResult quality,int current)
        {
            return HasOnlyLowOccupancy(quality,current)&&
                quality.ExpectedCalibrationVolts<=BenchSettings.Ranges[current]&&
                quality.RecommendedIndex==current&&!quality.Change;
        }
        internal static bool IsOrdinaryLowRangeAdvice(LocationRangeResult quality,int current)
        {
            // Only a valid, clean low-occupancy capture is ordinary advice.
            // Calibration demand, clipping and overflow keep their own gates.
            return HasOnlyLowOccupancy(quality,current)&&quality.Change&&
                quality.RecommendedIndex<current&&
                quality.ExpectedCalibrationVolts<=BenchSettings.Ranges[BenchSettings.Ranges.Length-1];
        }
        static bool HasOnlyLowOccupancy(LocationRangeResult quality,int current)
        {
            return quality!=null&&current>=0&&current<BenchSettings.Ranges.Length&&quality.WindowValid&&
                quality.LocationLowOccupancy&&!quality.LocationHighOccupancy&&
                quality.LocationClippedSamples==0&&quality.FullClippedSamples==0&&quality.OutsideClippedSamples==0&&
                !quality.DriverOverflow&&!quality.DriverOverflowUncertain&&!quality.RecommendationUncertain&&
                !quality.MaximumRangeInsufficient&&!quality.AtMaximumAndClipped&&
                !Double.IsNaN(quality.ExpectedCalibrationVolts)&&!Double.IsInfinity(quality.ExpectedCalibrationVolts)&&quality.ExpectedCalibrationVolts>=0&&
                quality.RecommendedIndex>=0&&quality.RecommendedIndex<BenchSettings.Ranges.Length;
        }
        internal static bool MustHoldAtMaximum(LocationRangeResult quality,int current)
        {
            // Reaching 20 V removes the adjustment dialog, not the measured
            // risk or the permission to save/analyze/continue automatically.
            return current==BenchSettings.Ranges.Length-1&&(quality==null||!quality.WindowValid||
                quality.LocationHighOccupancy||quality.LocationClippedSamples>0||quality.FullClippedSamples>0||quality.OutsideClippedSamples>0||
                quality.DriverOverflow||quality.DriverOverflowUncertain||quality.RecommendationUncertain||
                quality.MaximumRangeInsufficient||quality.AtMaximumAndClipped||
                Double.IsNaN(quality.ExpectedCalibrationVolts)||Double.IsInfinity(quality.ExpectedCalibrationVolts)||
                quality.ExpectedCalibrationVolts>BenchSettings.Ranges[current]);
        }
    }
}
