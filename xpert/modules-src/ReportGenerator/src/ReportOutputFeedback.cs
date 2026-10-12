using System;

namespace HVX.ReportGenerator
{
    public partial class ReportGeneratorControl
    {
        // Detailed paths, converter errors and implementation exceptions belong
        // in diagnostics. The operator gets the outcome and an actionable next step.
        string ReportOutputMessage(string diagnostic, string chinese, string english)
        {
            RecordReportOutputDiagnostic(diagnostic);
            return ReportLanguages.Ui(languageIndex, chinese, english);
        }

        void RecordReportOutputDiagnostic(string diagnostic)
        {
            if (string.IsNullOrWhiteSpace(diagnostic)) return;
            try { conclusionService.RecordDiagnosticMessage("Report output: " + diagnostic); }
            catch { /* A diagnostic sink failure must not replace the report outcome. */ }
        }
    }
}
