
using System;

namespace Xiongxin.Owts.Contracts.Report
{
    /// <summary>← ReportOutputFormat</summary>
    public enum ReportOutputFormatDto
    {
        PdfOnly = 0,
        WordOnly = 1,
        PdfAndWord = 2
    }

    public sealed class ReportExportRequest
    {
        public string ProjectDbPath { get; set; }
        public string ProjectPath { get; set; }
        public Guid AnalysisId { get; set; }
        public ReportOutputFormatDto Format { get; set; }
        public string TemplatePath { get; set; }
        public int LanguageIndex { get; set; }
        public double VoltageMinRatio { get; set; }
        public double VoltageMaxRatio { get; set; }
        public bool IncludeAppendix { get; set; }

        public ReportExportRequest()
        {
            ProjectDbPath = string.Empty;
            ProjectPath = string.Empty;
            TemplatePath = string.Empty;
            VoltageMinRatio = double.NaN;
            VoltageMaxRatio = double.NaN;
        }
    }

    public sealed class ReportExportResult
    {
        public bool Success { get; set; }
        public string[] OutputPaths { get; set; }
        public string StatusMessage { get; set; }
        public Guid UsedAnalysisId { get; set; }
        public string[] Warnings { get; set; }

        public ReportExportResult()
        {
            OutputPaths = new string[0];
            StatusMessage = string.Empty;
            Warnings = new string[0];
        }
    }

    /// <summary>← ReportImageInfo</summary>
    public sealed class ReportImageInfoDto
    {
        public string ChartKind { get; set; }
        public int ChartMode { get; set; }
        public string Category { get; set; }
        public string Title { get; set; }
        public string ImagePath { get; set; }

        public ReportImageInfoDto()
        {
            ChartKind = string.Empty;
            Category = string.Empty;
            Title = string.Empty;
            ImagePath = string.Empty;
        }
    }
}
