using System;
using System.ComponentModel;
using System.IO;

namespace HVX.ReportGenerator
{
    public sealed partial class ReportGeneratorControl
    {
        [Category("Report"),DefaultValue(false)]
        public bool GenerateStateGridReport { get; set; }
        [Browsable(false)] public string LastStateGridDocxPath { get; private set; }="";
        [Browsable(false)] public string LastStateGridPdfPath { get; private set; }="";
        [Browsable(false)] public string LastStateGridStatus { get; private set; }="";

        internal StateGridReportResult GenerateStateGridSupplement(string normalPdfPath)
        {
            LastStateGridDocxPath=LastStateGridPdfPath=LastStateGridStatus="";
            if(!GenerateStateGridReport)return null;
            string normalBase=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(normalPdfPath)),Path.GetFileNameWithoutExtension(normalPdfPath));
            string outputBase=ReportFileNames.AvailableGridBase(normalBase,languageIndex);
            string docx=outputBase+".docx",pdf=outputBase+".pdf";
            // Preserve the existing synchronous report/Office apartment contract.
            // Measurement selection caches only per-record scalar values to avoid
            // loading each large DEM repeatedly for the three voltage/noise slots.
            StateGridReportResult result=StateGridReportDocument.Generate(projectPath,null,locationMatrix,WithAcquisitionRiskWarning(conclusion.Text),docx,pdf,outputFormat);
            LastStateGridDocxPath=result.DocxPath;LastStateGridPdfPath=result.PdfPath;LastStateGridStatus=StateGridSummary(result);
            return result;
        }

        string StateGridSummary(StateGridReportResult result)
        {
            if(result==null)return "";
            RecordReportOutputDiagnostic("StateGrid supplement; skipped="+result.Skipped+"; success="+result.Success+"; docx="+result.DocxPath+"; pdf="+result.PdfPath+"; error="+result.Error+"; warning="+result.Warning);
            string message=result.Skipped?ReportLanguages.Ui(languageIndex,"国网附加报告未生成，请检查项目资料。","The supplementary report was not generated. Check the project information."):
                !result.Success?ReportLanguages.Ui(languageIndex,"国网附加报告未完成，请检查项目资料及输出目录后重试。","The supplementary report is incomplete. Check project information and the output folder, then retry."):
                result.Warning.Length>0?ReportLanguages.Ui(languageIndex,"国网附加报告部分完成，请核对已生成文件。","The supplementary report is partially complete. Review the generated files."):
                ReportLanguages.Ui(languageIndex,"国网附加报告已生成。","The supplementary report was generated.");
            return Environment.NewLine+message;
        }
        static bool StateGridWarning(StateGridReportResult result)
        {return result!=null&&(!result.Success||result.Warning.Length>0);}
    }
}
