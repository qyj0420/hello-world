using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        readonly List<ReportChartSnapshot> reportCharts = new List<ReportChartSnapshot>();
        const int ReportChartLimit=32;
        const long ReportChartBytesLimit=64L*1024*1024;
        long reportChartBytes;
        bool capturingReportChart;
        [Browsable(false)] public int PendingReportChartCount { get { return reportCharts.Count; } }
        [Browsable(false)] public int ChartReportRequestSequence { get; private set; }
        [Browsable(false)] public string ChartReportStatusMessage { get; private set; } = "";
        [Browsable(false)] public int MaximumReportChartCount { get { return ReportChartLimit; } }
        [Browsable(false)] public long MaximumReportChartBytes { get { return ReportChartBytesLimit; } }
        public event EventHandler ChartReportRequested;
        public event EventHandler ChartReportStatusChanged;
        public ReportChartSnapshot[] GetPendingReportCharts() { return reportCharts.ToArray(); }

        void InitializeReportChartExports()
        {
            pdmap.ConfigureChartReportAction(()=>RequestPdMapReportChart(),()=>!IsAnalysisRunning&&selected>=0);
            spectrum.ConfigureChartReportAction(()=>RequestSpectrumReportChart(),()=>!IsAnalysisRunning&&spectrum.VisibleSourceRowCount>0);
        }
        public bool RequestPdMapReportChart() { return CaptureReportChart(true); }
        public bool RequestSpectrumReportChart() { return CaptureReportChart(false); }
        bool CaptureReportChart(bool waveform)
        {
            if(capturingReportChart)return false;
            if(IsAnalysisRunning)return ReportChartFailure(L("Wait for analysis before adding a chart.","请等待分析结束后添加图表。"));
            if(reportCharts.Count>=ReportChartLimit)return ReportChartFailure(L("Report attachment limit reached (32). Remove attachments before adding more.","报告附件已达到32项，请移除部分附件后再添加。"));
            if(waveform&&(selected<0||pdmap.CurrentParameterRowIndex!=selected))return ReportChartFailure(L("Select a PDmap first.","请先选择PDmap。"));
            if(!waveform&&spectrum.VisibleSourceRowCount==0)return ReportChartFailure(L("No displayed spectrum data to add.","当前谱图没有可添加的数据。"));
            if(waveform&&CurrentAcquisitionReferenceOnly||!waveform&&spectrum.VisibleSourceRowIndices.Any(IsAcquisitionReferenceOnly))
                return ReportChartFailure(AcquisitionRiskText());
            capturingReportChart=true;
            try
            {
                // Capture does not complete a review preview or alter acceptance/filtering.
                int next=checked(ChartReportRequestSequence+1);
                ReportChartSnapshot attachment=waveform?pdmap.CaptureReportChart(next,projectPath):spectrum.CaptureReportChart(next);
                if(attachment.StorageBytes>ReportChartBytesLimit-reportChartBytes)
                    return ReportChartFailure(L("Report attachments exceed the 64 MiB memory limit. Remove attachments before adding more.","报告附件超过64 MiB内存上限，请移除部分附件后再添加。"));
                reportCharts.Add(attachment);reportChartBytes+=attachment.StorageBytes;ChartReportRequestSequence=next;
                ChartReportStatusMessage=L("Chart added to report attachments","图表已加入报告附件")+" ("+reportCharts.Count+")";
                MarkAnalysisDirty();NotifyReviewHost(ChartReportRequested);NotifyReviewHost(ChartReportStatusChanged);return true;
            }
            catch(Exception ex)
            {
                if(IsFatalHostException(ex))throw;
                return ReportChartFailure(L("Could not add chart: ","添加图表失败：")+ex.Message);
            }
            finally{capturingReportChart=false;}
        }
        bool ReportChartFailure(string message)
        { ChartReportStatusMessage=message;NotifyReviewHost(ChartReportStatusChanged);return false; }
        public bool RemoveReportChart(int sequence)
        {
            int index=reportCharts.FindIndex(chart=>chart.Sequence==sequence);if(index<0)return false;
            reportChartBytes-=reportCharts[index].StorageBytes;reportCharts.RemoveAt(index);
            ChartReportStatusMessage=L("Report attachment removed","已移除报告附件")+" ("+reportCharts.Count+")";
            MarkAnalysisDirty();NotifyReviewHost(ChartReportStatusChanged);return true;
        }
        public void ClearReportCharts()
        {
            bool changed=reportCharts.Count>0;
            reportCharts.Clear();reportChartBytes=0;
            if(changed)MarkAnalysisDirty();ChartReportStatusMessage=L("Report attachments cleared","报告附件已清空");NotifyReviewHost(ChartReportStatusChanged);
        }
    }
}
