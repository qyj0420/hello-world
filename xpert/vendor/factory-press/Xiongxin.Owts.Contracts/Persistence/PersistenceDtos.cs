using System;
using System.Collections.Generic;
using Xiongxin.Owts.Contracts.Project;

namespace Xiongxin.Owts.Contracts.Persistence
{
    public sealed class AcquisitionRecordDto
    {
        public Guid Id { get; set; }
        public string Phase { get; set; }
        public double TargetVoltageKv { get; set; }
        public DateTime CapturedUtc { get; set; }
        public string PicoSerial { get; set; }
        public string ArtSerial { get; set; }
        public string[] WaveformRelPaths { get; set; }
        public string CycleReferenceJson { get; set; }
        public string ExitReason { get; set; }

        public AcquisitionRecordDto()
        {
            Phase = string.Empty;
            PicoSerial = string.Empty;
            ArtSerial = string.Empty;
            WaveformRelPaths = new string[0];
            CycleReferenceJson = string.Empty;
            ExitReason = string.Empty;
        }
    }

    public sealed class CalibrationRecordDto
    {
        public Guid Id { get; set; }
        public string Phase { get; set; }
        public string MetaJson { get; set; }
        public DateTime CapturedUtc { get; set; }
        public CalibrationRecordDto()
        {
            Phase = string.Empty;
            MetaJson = "{}";
        }
    }

    public sealed class AnalysisPackageDto
    {
        public Guid Id { get; set; }
        public Guid AcquisitionId { get; set; }
        /// <summary>如 Task17 fc40b1d6ae7648e9b4abf7b9.V1.0.17</summary>
        public string AlgorithmId { get; set; }
        public string ParametersRelPath { get; set; }
        public string SessionJson { get; set; }
        public string ReportInputContext { get; set; }
        public DateTime CreatedUtc { get; set; }

        public AnalysisPackageDto()
        {
            AlgorithmId = string.Empty;
            ParametersRelPath = string.Empty;
            SessionJson = string.Empty;
            ReportInputContext = string.Empty;
        }
    }

    public sealed class ReportArtifactDto
    {
        public Guid Id { get; set; }
        public Guid AnalysisId { get; set; }
        public string Format { get; set; }
        public string RelPath { get; set; }
        public DateTime CreatedUtc { get; set; }

        public ReportArtifactDto()
        {
            Format = string.Empty;
            RelPath = string.Empty;
        }
    }

    public sealed class AcquisitionQuery
    {
        public string Phase { get; set; }
        public DateTime? AfterUtc { get; set; }
        public DateTime? BeforeUtc { get; set; }
        public AcquisitionQuery() { Phase = string.Empty; }
    }

    public sealed class MigrationReport
    {
        public bool Success { get; set; }
        public List<string> ImportedFiles { get; private set; }
        public List<string> Warnings { get; private set; }
        public bool LegacySourceKept { get; set; }
        public ProjectDraftDto Draft { get; set; }
        public string Message { get; set; }

        public MigrationReport()
        {
            ImportedFiles = new List<string>();
            Warnings = new List<string>();
            Message = string.Empty;
        }
    }

    /// <summary>waveform_file 一行（旁路文件相对工程目录）。</summary>
    public sealed class WaveformFileDto
    {
        public Guid Id { get; set; }
        public Guid AcquisitionId { get; set; }
        public string Channel { get; set; }
        public string RelPath { get; set; }
        public double SampleRateHz { get; set; }
        public int SampleCount { get; set; }
        public string FilterId { get; set; }

        public WaveformFileDto()
        {
            Channel = string.Empty;
            RelPath = string.Empty;
            FilterId = string.Empty;
        }
    }
}
