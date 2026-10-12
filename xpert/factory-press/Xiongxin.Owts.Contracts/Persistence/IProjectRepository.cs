
using System;
using System.Collections.Generic;
using Xiongxin.Owts.Contracts.Project;

namespace Xiongxin.Owts.Contracts.Persistence
{
    /// <summary>← SecureProjectStore.ProjectRecordKind + ProjectScanner；新真源 SQLite</summary>
    public interface IProjectRepository : IDisposable
    {
        void OpenOrCreate(string projectDbPath);
        Guid ProjectId { get; }

        void UpsertProject(ProjectDraftDto draft);
        ProjectDraftDto LoadProject();

        Guid InsertAcquisition(AcquisitionRecordDto record, IReadOnlyList<string> waveformFilePaths);
        Guid InsertCalibration(CalibrationRecordDto record);
        /// <summary>登记一条波形文件（通道、相对路径、采样率、点数）。</summary>
        Guid InsertWaveformFile(Guid acquisitionId, string channel, string relPath, double sampleRateHz, int sampleCount);
        Guid InsertAnalysis(AnalysisPackageDto package);
        Guid InsertReportArtifact(ReportArtifactDto artifact);

        IReadOnlyList<AcquisitionRecordDto> ListAcquisitions(AcquisitionQuery query);
        /// <summary>列出某次采集关联的波形旁路文件（含 channel/采样率/点数）。</summary>
        IReadOnlyList<WaveformFileDto> ListWaveformFiles(Guid acquisitionId);
        AnalysisPackageDto LoadAnalysis(Guid analysisId);

        MigrationReport ImportLegacyFolder(string legacyProjectDir);
    }
}
