using System;
using System.Collections.Generic;
using Xiongxin.Owts.Contracts.Persistence;
using Xiongxin.Owts.Contracts.Project;

namespace Xiongxin.Owts.Persistence
{
    /// <summary>
    /// 编译占位。出厂加压调用链不经过 SQLite / .dac 导入。
    /// 真持久化实现不在 dac-FactoryPress-full-src 包内。
    /// </summary>
    public sealed class SqliteProjectRepository : IProjectRepository
    {
        Guid _id = Guid.Empty;

        public Guid ProjectId { get { return _id; } }

        public void OpenOrCreate(string projectDbPath)
        {
            _id = Guid.NewGuid();
        }

        public void UpsertProject(ProjectDraftDto draft) { }

        public ProjectDraftDto LoadProject()
        {
            return new ProjectDraftDto();
        }

        public Guid InsertAcquisition(AcquisitionRecordDto record, IReadOnlyList<string> waveformFilePaths)
        {
            return Guid.NewGuid();
        }

        public Guid InsertCalibration(CalibrationRecordDto record)
        {
            return Guid.NewGuid();
        }

        public Guid InsertWaveformFile(Guid acquisitionId, string channel, string relPath, double sampleRateHz, int sampleCount)
        {
            return Guid.NewGuid();
        }

        public Guid InsertAnalysis(AnalysisPackageDto package)
        {
            return Guid.NewGuid();
        }

        public Guid InsertReportArtifact(ReportArtifactDto artifact)
        {
            return Guid.NewGuid();
        }

        public IReadOnlyList<AcquisitionRecordDto> ListAcquisitions(AcquisitionQuery query)
        {
            return new AcquisitionRecordDto[0];
        }

        public IReadOnlyList<WaveformFileDto> ListWaveformFiles(Guid acquisitionId)
        {
            return new WaveformFileDto[0];
        }

        public AnalysisPackageDto LoadAnalysis(Guid analysisId)
        {
            return null;
        }

        public MigrationReport ImportLegacyFolder(string legacyProjectDir)
        {
            return new MigrationReport
            {
                Success = false,
                Message = "遗留工程导入不在出厂加压包内。"
            };
        }

        public void Dispose() { }
    }

    public static class DacFileImporter
    {
        public static DacMeasurementWaveform TryGetMeasurement(DacImportResult import, long measurementId)
        {
            if (import == null || import.Measurements == null) return null;
            foreach (DacMeasurementWaveform wave in import.Measurements)
            {
                if (wave != null && wave.MeasurementId == measurementId) return wave;
            }
            return null;
        }

        public static IList<DacPdMapEntry> BaselineEntriesFor(DacImportResult import, long measurementId)
        {
            var list = new List<DacPdMapEntry>();
            if (import == null || import.PdMapBaseline == null) return list;
            foreach (DacPdMapEntry entry in import.PdMapBaseline)
            {
                if (entry != null && entry.MeasurementId == measurementId) list.Add(entry);
            }
            return list;
        }

        public static bool TryDecodeHighSpeed(byte[] blob, out DacMeasurementWaveform wave, out string error)
        {
            wave = null;
            error = "高速波形解码不在出厂加压包内。";
            return false;
        }
    }

    /// <summary>对照标尺编译占位。加压量程与定位提取不走这里。</summary>
    public static class DacCalibrationScale
    {
        public const string DefaultEndpointPolicy = "2;1.5;OR;OR";
        public const string TerminationPdLocNote = "compile-shim";
        public const string FormulaId = "compile-shim";

        public static bool TryScaleVoltsToPc(double[] volts, double factor, out double[] locData, out string unitStatus)
        {
            volts = volts ?? new double[0];
            locData = new double[volts.Length];
            if (!(factor > 0) || double.IsNaN(factor) || double.IsInfinity(factor))
            {
                unitStatus = "unscaled";
                Array.Copy(volts, locData, volts.Length);
                return false;
            }
            for (int i = 0; i < volts.Length; i++) locData[i] = volts[i] * factor;
            unitStatus = "pC";
            return true;
        }

        public static string FormatFactor(double factor)
        {
            return factor.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static class DacEndpointAlign
    {
        public const string AlignId = "compile-shim";

        public static double[] AlignMany(double[] positions, double lengthM)
        {
            return positions ?? new double[0];
        }

        public static double NearestNeighborAbs(double[] baseline, double[] positions) { return double.NaN; }
        public static double NearestNeighborAbs(System.Collections.Generic.IList<DacPdMapEntry> baseline, double[] positions) { return double.NaN; }
        public static double NearestNeighborBodyAbs(System.Collections.Generic.IList<DacPdMapEntry> baseline, double[] positions) { return double.NaN; }
        public static bool FarEndpointHit(System.Collections.Generic.IList<DacPdMapEntry> baseline, double[] aligned, double lengthM) { return false; }
        public static string CorrespondenceNote() { return AlignId; }
        public static string FormatMeters(double meters)
        {
            return meters.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static class Task17ProjectStubBuilder
    {
        public sealed class StubResult
        {
            public bool Success { get; set; }
            public string Detail { get; set; }
            public string ProjectDirectory { get; set; }
            public string LocationFilePath { get; set; }

            public StubResult()
            {
                Detail = "stub=missing";
                ProjectDirectory = string.Empty;
                LocationFilePath = string.Empty;
            }
        }

        public static StubResult Build(DacImportResult import, DacMeasurementWaveform wave)
        {
            return new StubResult { Success = false, Detail = "Task17 工程桩不在出厂加压包内。" };
        }
    }
}
