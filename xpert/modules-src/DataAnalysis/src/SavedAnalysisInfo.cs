namespace HVX.PdReview
{
    public sealed class SavedAnalysisInfo
    {
        public string Id { get; internal set; }
        public string Name { get; internal set; }
        public string DisplayName {get{return HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(Name);}}
        public string CreatedUtc { get; internal set; }
        public System.DateTime CreatedLocal { get; internal set; }
        public string DirectoryPath { get; internal set; }
        public int SourceRowCount { get; internal set; }
        public int ReportChartCount { get; internal set; }
    }
}
