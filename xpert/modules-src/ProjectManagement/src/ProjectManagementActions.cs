using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class ProjectManagementControl
    {
        async Task ImportProjectFromDialog()
        {
            if (busy || string.IsNullOrWhiteSpace(rootDirectory)) return;
            string selected = string.Empty;
            using (var mode = new ImportModeDialog(languageIndex))
            {
                if (mode.ShowDialog(FindForm()) != DialogResult.OK) return;
                if (mode.SelectionMode == ImportSelectionMode.Folder)
                {
                    using (var chooser = new FolderBrowserDialog { Description = T("选择包含 ITEM.in 与 CABLE.in 的项目文件夹", "Select a project folder containing ITEM.in and CABLE.in") })
                        if (chooser.ShowDialog(FindForm()) == DialogResult.OK) selected = chooser.SelectedPath;
                }
                else if (mode.SelectionMode == ImportSelectionMode.ItemFile)
                {
                    using (var chooser = new OpenFileDialog
                    {
                        Title = T("选择项目 ITEM.in", "Select the project ITEM.in"),
                        Filter = "ITEM.in|ITEM.in|" + T("所有文件", "All files") + "|*.*",
                        Multiselect = false,
                        CheckFileExists = true
                    }) if (chooser.ShowDialog(FindForm()) == DialogResult.OK) selected = chooser.FileName;
                }
            }
            if (string.IsNullOrWhiteSpace(selected)) return;
            string source;
            try { source = ProjectFileOperations.ProjectDirectoryFromSelection(selected); }
            catch (Exception ex) { ShowOperationError(T("导入项目", "Import project"), ex.Message); return; }
            ProjectOperationResult result = await RunOperation("ImportProject",
                T("正在导入项目数据…", "Importing project data…"),
                delegate { return ProjectFileOperations.ImportProject(source, rootDirectory); });
            if (!LifetimeEnded && result.Ok)
            {
                string importedPath = result.OutputPaths == null ? string.Empty : result.OutputPaths.FirstOrDefault();
                checkedPaths.Clear();
                if (!string.IsNullOrWhiteSpace(importedPath)) checkedPaths.Add(importedPath);
                BeginLoadRootCore(rootDirectory, importedPath);
            }
        }

        async Task ExportSelectedData()
        {
            ProjectRecord[] selected = SelectedRecords();
            if (selected.Length == 0) { Error(T("请选择要导出的项目。", "Select projects to export.")); return; }
            string destination = ChooseDestination(T("选择项目数据导出文件夹", "Choose the project data export folder"));
            if (string.IsNullOrWhiteSpace(destination)) return;
            await RunOperation("ExportData", T("正在导出项目数据…", "Exporting project data…"),
                delegate { return ProjectFileOperations.ExportProjects(selected, destination); });
        }

        async Task ExportSelectedReports()
        {
            ProjectRecord[] selected = SelectedRecords();
            if (!selected.Any(p => p.HasReport)) { Error(T("选中项目中没有可导出的报告。", "The selected projects have no reports.")); return; }
            string destination = ChooseDestination(T("选择报告导出文件夹", "Choose the report export folder"));
            if (string.IsNullOrWhiteSpace(destination)) return;
            await RunOperation("ExportReports", T("正在导出项目报告…", "Exporting project reports…"),
                delegate { return ProjectFileOperations.ExportReports(selected, destination); });
        }

        async Task MergeSelectedReports()
        {
            ProjectRecord[] selected = SelectedRecords();
            ProjectRecord[] eligible = selected.Where(p => !string.IsNullOrWhiteSpace(p.LatestMergeableReportPath)).ToArray();
            if (eligible.Length < 2)
            {
                Error(T("合并报告至少需要选中两个存在 DOC、DOCX 或 PDF 报告的项目。", "Select at least two projects with DOC, DOCX or PDF reports."));
                return;
            }
            MergeReportItem[] items;
            string introduction;
            using (var dialog = new MergeReportDialog(eligible, languageIndex))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                items = dialog.Items;
                introduction = dialog.Introduction;
            }
            string output;
            using (var chooser = new SaveFileDialog
            {
                Title = T("保存汇总报告", "Save consolidated report"),
                Filter = "Word document (*.docx)|*.docx",
                AddExtension = true,
                DefaultExt = "docx",
                OverwritePrompt = true,
                FileName = T("汇总报告_", "Consolidated_Report_") + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".docx"
            })
            {
                if (chooser.ShowDialog(FindForm()) != DialogResult.OK) return;
                output = chooser.FileName;
            }
            MergeReportRequest request = new MergeReportRequest
            {
                OutputPath = output,
                Introduction = introduction,
                Items = items
            };
            await RunOperation("MergeReports", T("正在合并报告并重排标题、图表题注…", "Merging reports and renumbering headings and captions…"),
                delegate { return ReportMergeService.Merge(request); });
        }

        async Task DeleteSelectedProjects()
        {
            ProjectRecord[] selected = SelectedRecords();
            if (selected.Length == 0) { Error(T("请选择要删除的项目。", "Select projects to delete.")); return; }
            string preview = string.Join(Environment.NewLine, selected.Take(6).Select(p => "• " + p.ProjectName + "  |  " + p.DisplayTestTime));
            if (selected.Length > 6) preview += Environment.NewLine + "…";
            string message = T(
                "将以下 " + selected.Length + " 个项目移出列表，并在同父目录保存可恢复隔离副本。原始文件和只读属性保留，不释放磁盘空间；完成后显示恢复位置。\r\n\r\n" + preview + "\r\n\r\n确认继续？",
                "Remove " + selected.Length + " project(s) from the list by a recoverable rename in the same parent directory. Original files and attributes remain intact; disk space is not freed. Recovery paths will be shown.\r\n\r\n" + preview + "\r\n\r\nContinue?");
            if (MessageBox.Show(FindForm(), message, T("确认删除项目", "Confirm project deletion"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            ProjectOperationResult result = await RunOperation("DeleteProjects", T("正在将项目移入可恢复隔离目录…", "Moving projects to recoverable quarantine…"),
                delegate { return ProjectFileOperations.DeleteProjects(rootDirectory, selected); });
            if (!LifetimeEnded && (result.Ok || result.ProcessedCount > 0))
            {
                foreach (string removedPath in result.OutputPaths ?? new string[0])
                    checkedPaths.Remove(removedPath);
                BeginLoadRootCore(rootDirectory, string.Empty);
                if (result.Ok) MessageBox.Show(FindForm(), T(
                    "原始数据已保留，未释放磁盘空间。关闭使用该项目的程序后，可将下列隔离目录重命名回原目录（不得覆盖已有目录）：\r\n",
                    "Original data is preserved; no disk space was freed. Close applications using the project, then rename the recovery folder back to its original name. Never overwrite an existing folder.\r\n") +
                    string.Join("\r\n\r\n", result.OutputPaths.Select((original, index) =>
                        result.RecoveryPaths[index] + "\r\n→ " + original)), T("可恢复移出完成", "Recoverable removal complete"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        async Task<ProjectOperationResult> RunOperation(string operation, string progress, Func<ProjectOperationResult> work)
        {
            if (LifetimeEnded) return ProjectOperationResult.Failure("Control disposed.");
            SetBusy(true, progress);
            ProjectOperationResult result;
            try { result = await Task.Run(work); }
            catch (Exception ex) { result = ProjectOperationResult.Failure(ex.Message); }
            if (LifetimeEnded) return result ?? ProjectOperationResult.Failure("Control disposed.");
            SetBusy(false, null);
            lastOperation = operation;
            if (result == null) result = ProjectOperationResult.Failure(T("操作没有返回结果。", "The operation returned no result."));
            if (result.Ok)
            {
                lastError = string.Empty;
                int completed = result.ProcessedCount;
                SetStatus(() => string.Format(CultureInfo.InvariantCulture,
                    T("操作完成：{0} 项", "Completed: {0} item(s)"), completed), false);
            }
            else
            {
                lastError = result.Error;
                SetStatus(() => T("操作失败；请查看错误详情。", "Operation failed; see error details."), true);
                ShowOperationError(OperationDisplayName(operation), result.Error);
            }
            OperationCompleted?.Invoke(this, new ProjectOperationEventArgs(operation, result));
            return result;
        }

        string ChooseDestination(string description)
        {
            using (var chooser = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
                return chooser.ShowDialog(FindForm()) == DialogResult.OK ? chooser.SelectedPath : string.Empty;
        }

        void ShowOperationError(string title, string message)
        {
            MessageBox.Show(FindForm(), message ?? string.Empty, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        string OperationDisplayName(string operation)
        {
            switch (operation)
            {
                case "ImportProject": return T("导入项目失败", "Import failed");
                case "ExportData": return T("导出数据失败", "Data export failed");
                case "ExportReports": return T("导出报告失败", "Report export failed");
                case "MergeReports": return T("合并报告失败", "Report merge failed");
                case "DeleteProjects": return T("删除项目失败", "Project deletion failed");
                default: return T("项目管理操作失败", "Project operation failed");
            }
        }
    }
}
