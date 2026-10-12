using System;
using System.Globalization;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        string analysisProgressAcquisition="";
        int analyzedRecordCount,analysisRecordCount;
        string backgroundPressureAnalysisStatus = "", backgroundPressureAnalysisError = "";
        public string BackgroundPressureAnalysisStatusText { get { return backgroundPressureAnalysisStatus; } }
        public string BackgroundPressureAnalysisError { get { return backgroundPressureAnalysisError; } }
        public void ReportBackgroundPressureAnalysis(string acquisitionId, string group, int completed, int total, int queued, string error)
        {
            RunOnUiThread(() => {
                if (!BackgroundPressureAnalysisEnabled) return;
                if (!String.IsNullOrWhiteSpace(error)) backgroundPressureAnalysisError = error;
                string label = String.IsNullOrWhiteSpace(group) ? acquisitionId : group;
                if (!String.IsNullOrWhiteSpace(error))
                    backgroundPressureAnalysisStatus = PressureUiText("后台分析未完成：", "Background analysis incomplete: ") + label;
                else if (total > 0 && completed >= total && queued == 0)
                    backgroundPressureAnalysisStatus = PressureUiText("后台分析完成：", "Background analysis completed: ") + label;
                else
                    backgroundPressureAnalysisStatus = PressureUiText("后台分析：", "Background analysis: ") + label +
                        (total > 0 ? " " + completed.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture) : "") +
                        (queued > 0 ? PressureUiText(" · 排队 ", " · queued ") + queued.ToString(CultureInfo.InvariantCulture) : "");
                if (String.IsNullOrWhiteSpace(error) && backgroundPressureAnalysisError.Length > 0)
                    backgroundPressureAnalysisStatus += PressureUiText(" · 另有分析未完成", " · other analysis incomplete");
                UpdateTaskInformationUi();
            });
        }
        // Informational only: never acknowledges analysis or unlocks a new round.
        public void ReportPressureAnalysisProgress(string acquisitionId,int completed,int total)
        {
            if(String.IsNullOrWhiteSpace(acquisitionId)||total<1||completed<0||completed>total)return;
            RunOnUiThread(()=>{
                if(PostProcessStateCode!="ANALYSIS_REQUESTED"||LocationAcquisitionId!=acquisitionId)return;
                analysisProgressAcquisition=acquisitionId;analyzedRecordCount=completed;analysisRecordCount=total;
                UpdatePressureUi();
            });
        }
        string PressureAnalysisProgressTitle()
        {
            string title=PressureUiText("正在分析","Analyzing");
            if(analysisProgressAcquisition==LocationAcquisitionId&&analysisRecordCount>0)
                title+=" "+analyzedRecordCount.ToString(CultureInfo.InvariantCulture)+"/"+analysisRecordCount.ToString(CultureInfo.InvariantCulture);
            return title;
        }
    }
}
