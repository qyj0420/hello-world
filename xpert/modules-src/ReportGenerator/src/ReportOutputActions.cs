using System;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;

namespace HVX.ReportGenerator
{
    public sealed partial class ReportGeneratorControl
    {
        // Injection seams keep regression checks from opening applications or printing.
        internal Func<string> DefaultPrinterProvider = delegate {
            var settings = new PrinterSettings();
            if (!settings.IsValid || string.IsNullOrWhiteSpace(settings.PrinterName))
                throw new InvalidOperationException("没有可用的系统默认打印机 / No valid system default printer.");
            return settings.PrinterName;
        };
        internal Action<ProcessStartInfo> ReportShellLauncher = delegate(ProcessStartInfo info) { Process.Start(info); };

        public bool OpenReportDirectory()
        {
            if (busy) return false;
            string path = FindOpenableReportPath();
            if (path.Length == 0) return Fail(ReportLanguages.Ui(languageIndex, "尚无可打开的报告，请先生成报告。", "No generated report is available."), true);
            try {
                ReportShellLauncher(new ProcessStartInfo(Path.GetDirectoryName(Path.GetFullPath(path))) { UseShellExecute = true });
                return true;
            } catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(), "无法打开报告文件夹。请检查文件夹是否可访问后重试。", "The report folder could not be opened. Check folder access and retry."), true); }
        }

        public bool PrintGeneratedReport()
        {
            if (busy) return false;
            string path = FindOpenableReportPath();
            if (path.Length == 0) return Fail(ReportLanguages.Ui(languageIndex, "尚无可打印的报告，请先生成报告。", "No generated report is available to print."), true);
            try {
                string printer = DefaultPrinterProvider();
                ReportShellLauncher(CreateReportPrintRequest(path, printer));
                SetStatus(ReportLanguages.Ui(languageIndex, "已请求关联程序打印至系统默认打印机：", "Print requested through the associated application to the system default printer: ") + printer, false);
                return true;
            } catch (Exception ex) {
                return Fail(ReportOutputMessage(ex.ToString(), "无法打印报告。请检查默认打印机和 PDF/Word 程序是否可用后重试。", "The report could not be printed. Check the default printer and PDF/Word application, then retry."), true);
            }
        }

        internal static ProcessStartInfo CreateReportPrintRequest(string path, string printer)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Report not found.", path);
            string extension = Path.GetExtension(path);
            if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only PDF and Word reports can be printed.");
            if (string.IsNullOrWhiteSpace(printer) || printer.IndexOf('"') >= 0 || printer.IndexOf('\r') >= 0 || printer.IndexOf('\n') >= 0)
                throw new ArgumentException("No valid system default printer.");
            return new ProcessStartInfo(Path.GetFullPath(path)) {
                UseShellExecute = true, Verb = "printto", Arguments = "\"" + printer + "\"",
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        void SetProjectInformationPreview(bool show)
        {
            if (show) {
                if (previewGrid.Parent != projectInformationPanel) projectInformationPanel.Controls.Add(previewGrid, 0, 0);
                projectStructure.SetProject(projectData, languageIndex);
                projectInformationPanel.Visible = true;
                projectInformationPanel.BringToFront();
            } else {
                if (previewGrid.Parent == projectInformationPanel) projectInformationPanel.Parent.Controls.Add(previewGrid);
                projectInformationPanel.Visible = false;
            }
            previewGrid.ColumnHeadersVisible = !show;
        }
    }
}
