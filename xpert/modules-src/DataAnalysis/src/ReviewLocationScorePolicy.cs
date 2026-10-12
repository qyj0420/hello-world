using System;
using System.ComponentModel;
using System.Globalization;
using HVX.LocationScoring;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        LocationScorePolicy locationScorePolicy=LocationScorePolicy.Defaults();
        [Browsable(false)] public double LocationSuspiciousMinimumPercent{get{return locationScorePolicy.SuspiciousMinimumPercent;}}
        [Browsable(false)] public double LocationManualAcceptanceMinimumPercent{get{return locationScorePolicy.ManualAcceptanceMinimumPercent;}}
        [Browsable(false)] public double LocationAutomaticAcceptanceMinimumPercent{get{return locationScorePolicy.AutomaticAcceptanceMinimumPercent;}}
        public void SetLocationScoreThresholds(double suspiciousPercent,double manualPercent,double automaticPercent)
        {
            if(IsAnalysisRunning)throw new InvalidOperationException("Wait for the current analysis before changing location score limits.");
            var next=new LocationScorePolicy(suspiciousPercent,manualPercent,automaticPercent);
            spectrum.SetClassificationThresholds(next.SuspiciousMinimumPercent,next.AutomaticAcceptanceMinimumPercent);
            locationScorePolicy=next;
            RefreshReviewUi();
        }
        bool ManualLocationScoreEligible{get{return IsCapacitorRow(selected)||locationScorePolicy.CanManuallyAccept(CurrentConfidence);}}
        string ManualLocationScoreReason()
        {
            return L("The score is below the manual acceptance minimum (","评分未达到手动接受下限（")+
                LocationManualAcceptanceMinimumPercent.ToString("0.##",CultureInfo.InvariantCulture)+"%"+L("). Select another reflection or reanalyse.","）。请选择其他反射波或重新分析。");
        }
    }
    internal static class ReviewLocationScoreAcceptance
    {
        // A score limit is necessary, never sufficient: retain the algorithm's
        // independent waveform, timing, phase and qualification decisions.
        internal static void Apply(ReviewAnalysisResult result,ReviewAnalysisRequest request)
        {
            var policy=new LocationScorePolicy(request.LocationSuspiciousMinimumPercent,request.LocationManualAcceptanceMinimumPercent,request.LocationAutomaticAcceptanceMinimumPercent);
            for(int row=0;row<result.Parameters.GetLength(0);row++)
                if(!request.AutomaticAcceptance||!policy.CanAutomaticallyAccept(result.Parameters[row,33]))result.Parameters[row,35]=0;
        }
    }
}
