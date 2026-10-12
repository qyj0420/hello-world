using System;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryRangeRetention
    {
        internal static void ApplyCycleWindow(LocationRangeResult q,int current)
        {
            if(q==null || !q.WindowValid || current<0 || current>=BenchSettings.Ranges.Length ||
                !FactorySettings.Finite(q.LocationPeakFraction) || FactoryTransientRangePolicy.BlocksForOverflow(q,current))return;
            if(q.LocationClippedSamples>0 || q.LocationPeakFraction>=1)
            {q.WindowRangeQualityPassed=false;q.Reason="定位N周期内绝对峰值触及或超过量程，请提高量程。"+q.Reason;return;}
            if(q.LocationPeakFraction>=.25)
            {
                q.RecommendedIndex=current;q.RecommendedFullScaleVolts=BenchSettings.Ranges[current];q.Change=false;
                // A 65% display target must not turn a measured, unclipped
                // 25%-100% window at the largest range into an input fault.
                q.MaximumRangeInsufficient=false;
                q.WindowRangeQualityPassed=true;
                q.Reason="定位N周期幅值占用 "+(q.LocationPeakFraction*100).ToString("F2")+"%，处于25%至满量程内，保留当前档。"+q.Reason;
            }
            else
            {
                q.WindowRangeQualityPassed=false;
                q.Reason="幅值占用 "+(q.LocationPeakFraction*100).ToString("F2")+"% 低于25%，当前量程偏大，建议降低量程；按约65%单边满幅选择可用档位。"+q.Reason;
                if(q.RecommendedIndex==current)q.Reason+=" 当前已为满足峰值余量的最小可用档位，保持本档。";
            }
        }
        internal static void Apply(LocationRangeResult q,int current)
        {
            if(q==null || !q.WindowValid || current<0 || current>=BenchSettings.Ranges.Length ||
                !FactorySettings.Finite(q.LocationPeakFraction) || q.LocationPeakFraction<.25 ||
                q.LocationPeakFraction>=LocationRangeAdvisor.HighOccupancyFraction ||
                q.LocationClippedSamples!=0 || q.MaximumRangeInsufficient || q.AtMaximumAndClipped ||
                FactoryTransientRangePolicy.BlocksForOverflow(q,current))return;
            // Only suppress a downrange recommendation; never suppress an
            // uprange needed for calibration headroom or input limits.
            if(q.RecommendedIndex<0 || q.RecommendedIndex>current)return;
            q.RecommendedIndex=current;q.RecommendedFullScaleVolts=BenchSettings.Ranges[current];q.Change=false;
            q.Reason="保留当前量程：有效窗口绝对峰值占单边满量程 "+(q.LocationPeakFraction*100).ToString("F2")+
                "%（≥25%且<80%）；未见窗口削顶，转换过冲不参与本决定。仅表示量程可用，不代表信噪比或局放性质合格。原始评估："+q.Reason;
        }
    }
}
