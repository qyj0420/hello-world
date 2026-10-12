using System;

namespace HVX.PdReview
{
    /// <summary>Immutable report attachment. Hosts consume the PNG and data to build their report; capturing an attachment does not write a report file.</summary>
    public sealed class ReportChartSnapshot
    {
        readonly byte[] png;
        readonly double[,] parameters;
        readonly int[] sourceRows;
        public int Sequence { get; private set; }
        public string ChartKind { get; private set; }
        public string Title { get; private set; }
        public string RegionSummary { get; private set; }
        public string ProjectPath { get; private set; }
        public string CreatedUtc { get; private set; }
        public int DisplayModeIndex { get; private set; }
        public int LanguageIndex { get; private set; }
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public string CsvData { get; private set; }
        public bool IncludedInReport { get; private set; } = true;
        public string ImagePath { get; private set; } = "";
        public string CsvPath { get; private set; } = "";
        public bool IsHistoricalOrStale { get; private set; }
        public bool AcquisitionReferenceOnly { get; private set; }
        public string StatusReason { get; private set; } = "";
        public string SchemaVersion { get { return "1.0"; } }
        public byte[] PngImage { get { return (byte[])png.Clone(); } }
        public double[,] ParameterMatrix { get { return (double[,])parameters.Clone(); } }
        public int[] SourceRowIndices { get { return (int[])sourceRows.Clone(); } }
        public int SourceRowCount { get { return sourceRows.Length; } }
        internal long StorageBytes { get { return png.LongLength + parameters.LongLength * 8 + sourceRows.LongLength * 4 + (long)CsvData.Length * 2; } }

        internal ReportChartSnapshot(int sequence, string kind, string title, string region, string project,
            int mode, int language, byte[] image, int width, int height, double[,] matrix, int[] rows, string csv)
        {
            Sequence=sequence;ChartKind=kind;Title=title??"";RegionSummary=region??"";ProjectPath=project??"";
            DisplayModeIndex=mode;LanguageIndex=language;ImageWidth=width;ImageHeight=height;
            CreatedUtc=DateTime.UtcNow.ToString("o",System.Globalization.CultureInfo.InvariantCulture);
            // Buffers are freshly built for this snapshot, never borrowed from live controls.
            png=image;parameters=matrix;sourceRows=rows;CsvData=csv??"";
        }
        internal void SetReportSelection(bool selected) { IncludedInReport=selected&&!AcquisitionReferenceOnly; }
        internal void MarkAcquisitionReferenceOnly(bool english)
        {
            AcquisitionReferenceOnly=true;IncludedInReport=false;
            StatusReason=english?"Acquisition overrange / uncertain peaks; reference only, excluded from reports.":"采集超量程／峰值不确定；仅供参考，不纳入有效报告。";
            if(!Title.StartsWith("Reference only · ",StringComparison.Ordinal)&&!Title.StartsWith("仅供参考 · ",StringComparison.Ordinal))
                Title=(english?"Reference only · ":"仅供参考 · ")+Title;
        }
        internal void SetSavedPaths(string image,string csv) { ImagePath=image;CsvPath=csv; }
        internal void SetCreatedUtc(string value) { CreatedUtc=value; }
        internal void MarkHistorical(string reason,bool english)
        {
            IsHistoricalOrStale=true;StatusReason=reason;IncludedInReport=false;
            if(!Title.StartsWith("Historical snapshot (source changed) · ",StringComparison.Ordinal)&&!Title.StartsWith("历史快照（源数据已变） · ",StringComparison.Ordinal))
                Title=(english?"Historical snapshot (source changed) · ":"历史快照（源数据已变） · ")+Title;
        }
    }
}
