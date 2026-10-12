using System;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Explicit host navigation refresh, including a newly saved CAL in the
        // same project. This does not reset LOCs, matrices or manual decisions.
        public void ReloadProjectCalibration()
        {
            if(IsAnalysisRunning)throw new InvalidOperationException("分析正在进行，请稍后刷新校准。");
            pdmap.ReloadProjectCalibration();RefreshReviewUi();
        }
    }
}
