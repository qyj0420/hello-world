using System;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // Customer prompts describe the failed action and next step, never the
        // exception payload. The original detail remains in the operation log.
        string CustomerFailure(string action,string diagnostic,string english,string chinese)
        {
            if(!String.IsNullOrWhiteSpace(diagnostic))RecordOperation(action,diagnostic);
            return L(english,chinese);
        }
        string ArchiveCustomerFailure(bool loading=false)
        {
            return loading?CustomerFailure("AnalysisLoadRejected",AnalysisArchiveStatus,
                "The analysis could not be loaded. Reopen the project and review the saved data, then retry.",
                "无法加载分析。请重新打开项目并核对已保存数据后重试。"):
                CustomerFailure("AnalysisSaveRejected",AnalysisArchiveStatus,
                "The analysis was not saved. Check the analysis name and storage access, then retry.",
                "分析未保存。请检查分析名称及保存位置是否可写后重试。");
        }
        string LocalAiSuggestionText(LocalAiAnalysisSuggestion suggestion)
        {
            if(suggestion==null)return L("No local suggestion is available.","暂无本地建议。");
            if(!suggestion.Available)return suggestion.Status;
            RecordOperation("LocalAiSuggestionReviewed","samples="+suggestion.SampleCount+"; similarity="+suggestion.Confidence+"; reflectionIndex="+suggestion.SuggestedReflectionIndex+"; thresholdPc="+suggestion.SuggestedThresholdPc+"; "+suggestion.Status);
            // Sample counts, neighbour selection and array indices remain in
            // diagnostic evidence; applying still uses the exact reviewed pick.
            return L("A local suggestion is available for manual review. It does not confirm the location.",
                "已有本地建议可供人工复核，不代表定位已确认。");
        }
    }
}
