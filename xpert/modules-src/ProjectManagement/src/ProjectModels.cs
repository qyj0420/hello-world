using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HVX.Controls
{
    public enum ProjectSortField
    {
        TestTime = 0,
        ProjectName = 1,
        CableLength = 2
    }

    public enum ProjectSortDirection
    {
        Descending = -1,
        Ascending = 1
    }

    public sealed class ProjectReportFile
    {
        public string FullPath { get; internal set; }
        public string RelativePath { get; internal set; }
        public string Extension { get; internal set; }
        public DateTime LastWriteTime { get; internal set; }
        public long Length { get; internal set; }

        public bool CanMerge
        {
            get
            {
                return string.Equals(Extension, ".doc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Extension, ".docx", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Extension, ".pdf", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public sealed class ProjectRecord
    {
        readonly List<ProjectReportFile> reports = new List<ProjectReportFile>();
        readonly IReadOnlyList<ProjectReportFile> reportsView;
        string projectPath;
        string projectFolderName = string.Empty;
        string reportSummary = "无报告";
        string latestMergeableReportPath = string.Empty;

        public string ProjectPath
        {
            get { return projectPath; }
            internal set
            {
                projectPath = value;
                try { projectFolderName = new DirectoryInfo(value).Name; }
                catch { projectFolderName = string.Empty; }
            }
        }
        public string ItemPath { get; internal set; }
        public string CablePath { get; internal set; }
        public string StartTerminal { get; internal set; }
        public string EndTerminal { get; internal set; }
        public string ProjectName { get; internal set; }
        public string RawDate { get; internal set; }
        public string RawTime { get; internal set; }
        public string DisplayTestTime { get; internal set; }
        public DateTime TestTime { get; internal set; }
        public bool HasTestTime { get; internal set; }
        public string TestTimeSource { get; internal set; }
        public double CableLengthMetres { get; internal set; }
        public string CableLengthSource { get; internal set; }
        public string Warning { get; internal set; }
        public ProjectInsulationStatus InsulationStatus { get; internal set; }
        public IReadOnlyList<ProjectReportFile> Reports { get { return reportsView; } }
        internal List<ProjectReportFile> MutableReports { get { return reports; } }

        public bool HasReport { get { return reports.Count > 0; } }
        public int ReportCount { get { return reports.Count; } }

        public string ReportSummary { get { return reportSummary; } }
        public string LatestMergeableReportPath { get { return latestMergeableReportPath; } }
        public string ProjectFolderName { get { return projectFolderName; } }

        // Called once after discovery, before publishing the immutable scan record.
        // Preserve the original Word/PDF preference and tie-break ordering exactly.
        internal void CompleteReports()
        {
                if (reports.Count == 0) { reportSummary = "无报告"; latestMergeableReportPath = string.Empty; return; }
                int word = reports.Count(r => string.Equals(r.Extension, ".doc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Extension, ".docx", StringComparison.OrdinalIgnoreCase));
                int pdf = reports.Count(r => string.Equals(r.Extension, ".pdf", StringComparison.OrdinalIgnoreCase));
                reportSummary = word > 0 && pdf > 0 ? "DOCX " + word + "  ·  PDF " + pdf : word > 0 ? "DOCX " + word : "PDF " + pdf;
                ProjectReportFile file = reports.Where(r => r.CanMerge)
                    .OrderBy(r => string.Equals(r.Extension, ".pdf", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(r => r.LastWriteTime)
                    .ThenByDescending(r => string.Equals(r.Extension, ".docx", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                latestMergeableReportPath = file == null ? string.Empty : file.FullPath;
        }

        public ProjectRecord()
        {
            reportsView = reports.AsReadOnly();
            ProjectPath = ItemPath = CablePath = StartTerminal = EndTerminal = ProjectName = string.Empty;
            RawDate = RawTime = DisplayTestTime = TestTimeSource = CableLengthSource = Warning = string.Empty;
            CableLengthMetres = double.NaN;
        }
    }

    public sealed class ProjectScanResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }
        public string RootDirectory { get; internal set; }
        public ProjectRecord[] Projects { get; internal set; }
        public string[] Warnings { get; internal set; }

        public ProjectScanResult()
        {
            Error = RootDirectory = string.Empty;
            Projects = new ProjectRecord[0];
            Warnings = new string[0];
        }
    }

    public sealed class ProjectOperationResult
    {
        public bool Ok { get; internal set; }
        public string Error { get; internal set; }
        public string Message { get; internal set; }
        public string[] OutputPaths { get; internal set; }
        public string[] RecoveryPaths { get; internal set; }
        public int ProcessedCount { get; internal set; }
        public int SkippedCount { get; internal set; }

        public ProjectOperationResult()
        {
            Error = Message = string.Empty;
            OutputPaths = new string[0];
            RecoveryPaths = new string[0];
        }

        internal static ProjectOperationResult Failure(string error)
        {
            return new ProjectOperationResult { Ok = false, Error = error ?? string.Empty };
        }
    }

    public sealed class ProjectLoadRequestedEventArgs : EventArgs
    {
        public string ProjectPath { get; private set; }
        public long Sequence { get; private set; }

        public ProjectLoadRequestedEventArgs(string projectPath, long sequence)
        {
            ProjectPath = projectPath ?? string.Empty;
            Sequence = sequence;
        }
    }

    public sealed class ProjectOperationEventArgs : EventArgs
    {
        public string Operation { get; private set; }
        public ProjectOperationResult Result { get; private set; }

        public ProjectOperationEventArgs(string operation, ProjectOperationResult result)
        {
            Operation = operation ?? string.Empty;
            Result = result ?? ProjectOperationResult.Failure("未知结果");
        }
    }
}
