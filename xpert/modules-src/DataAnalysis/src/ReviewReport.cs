using System;
using System.ComponentModel;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        PdPulseReportParameters lastReport;
        [Category("Report")] public string ReportPulseName { get; set; } = "";
        [Browsable(false)] public int ReportRequestSequence { get; private set; }
        [Browsable(false)] public PdPulseReportParameters LatestReportParameters { get { return lastReport; } }
        public event EventHandler ReportParametersRequested;
        public PdPulseReportParameters GetLatestReportParameters() { return lastReport; }
        /// <summary>Freeze the selected PDpulse parameters. No report rendering, filesystem or source mutation.</summary>
        public bool RequestReportParameters()
        {
            if(selected<0){LastError=L("Select a PDmap before exporting parameters.","请先选择PDmap再输出报告参数。");RefreshReviewUi();return false;}
            if(CurrentAcquisitionReferenceOnly){LastError=AcquisitionRiskText();RefreshReviewUi();return false;}
            if(!CanReportParameters){LastError=L("The reflection is unresolved or outside this waveform. Review it before exporting.","反射波未确定或不在当前波形内，请先复核后输出报告参数。");RefreshReviewUi();return false;}
            try
            {
                string name=string.IsNullOrWhiteSpace(ReportPulseName)?"PDmap-"+PdMapId:ReportPulseName;
                var snapshot=PdPulseReportParameters.Capture(pdmap,name);
                // Freeze the requested row before final filtering can select a
                // different one; the snapshot already contains the latest cursor.
                pdmap.CompleteReviewPreview();
                lastReport=snapshot;ReportRequestSequence++;LastError="";RefreshReviewUi();
                NotifyReviewHost(ReportParametersRequested);return true;
            }
            catch(Exception ex) { LastError=ex.Message;RefreshReviewUi();return false; }
        }
    }
}
