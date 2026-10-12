
namespace Xiongxin.Owts.Contracts.Report
{
    /// <summary>← ReportEngine.GenerateReport* / ReportInputSnapshot；只读已保存分析</summary>
    public interface IReportExporter
    {
        ReportExportResult Export(ReportExportRequest request);
    }
}
