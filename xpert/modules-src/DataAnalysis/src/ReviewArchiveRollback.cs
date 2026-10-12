using System;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        Action CaptureNamedAnalysisRollback()
        {
            double[,] p,w;string[,] files,settings;
            if(!ExportAnalysisSession(out p,out w,out files,out settings))throw new InvalidOperationException("Cannot safely replace this analysis: "+LastError);
            var prior=PrepareSession(projectPath,p,w,files,settings);
            // A failed load must preserve the exact current values, not reclassify
            // the active analysis against files that happened to change on disk.
            prior.Parameters=p;prior.Waves=w;
            prior.Reviewed=(bool[])reviewed.Clone();
            prior.Removed=(bool[])removed.Clone();
            prior.AcceptanceConfirmed=(bool[])acceptanceConfirmed.Clone();
            prior.AutomaticallyAccepted=(bool[])automaticAcceptanceRows.Clone();
            prior.ExactManualPositions=(bool[])exactManualPositionRows.Clone();
            prior.AlgorithmDiagnostics=new System.Collections.Generic.Dictionary<string,string>(algorithmDiagnostics,StringComparer.OrdinalIgnoreCase);
            prior.AcquisitionQualities=new System.Collections.Generic.Dictionary<string,ReviewAcquisitionQuality>(acquisitionQualities,StringComparer.Ordinal);
            prior.DetachedTreeCatalog=analysisTree.GetDataCatalog();prior.Paths=analysisTree.GetDataPaths();prior.Ids=analysisTree.GetDataIds();
            prior.Analyzed=analysisTree.GetAnalyzedDataPaths();prior.Selected=analysisTree.GetSelectedDataPaths();
            var oldWorking=(double[,])working.Clone();var oldOriginal=(double[,])original.Clone();var oldMatching=(double[,])matching.Clone();
            var oldSources=(int[])confidenceSources.Clone();var charts=reportCharts.ToArray();long bytes=reportChartBytes;
            string oldLoaded=LoadedAnalysisId,oldSaved=LastSavedAnalysisId,oldRoot=loadedArchiveRoot,oldChartStatus=ChartReportStatusMessage;
            string oldProject=projectPath,oldAnalysisStatus=AnalysisStatus,oldSessionStatus=AnalysisSessionStatus;
            string oldScope=spectrumScopePath,oldOverview=overviewPath;bool oldOverviewMode=locationOverviewMode;
            int oldSelected=selected,oldPreview=previewFilteredRow,oldReportSequence=ChartReportRequestSequence;
            int oldResultSequence=ResultChangeSequence,oldSelectionSequence=SelectionSequence,oldComplete=AnalysisCompletedFileCount;
            var expanded=analysisTree.GetExpandedNodeKeys();string selectedNode=analysisTree.GetSelectedNodeKey(),topNode=analysisTree.GetTopNodeKey();
            object view=spectrum.CaptureArchiveViewState();
            var range=new{rangeCountEnabled,rangeChargeEnabled,rangeVoltageEnabled,minimumRangeCount,voltageShortcutMode,rangeChargeMin,rangeChargeMax,rangeVoltageMin,rangeVoltageMax};
            return delegate
            {
                bool oldSuppression=analysisTree.SuppressArchiveNotifications;
                reviewNotificationDepth++;analysisTree.SuppressArchiveNotifications=true;
                try
                {
                    ApplySession(prior);
                    // Preserve legacy waveform row layout and original-input confidence separately.
                    if(!LoadDataMatricesCore(oldWorking,oldMatching,true,prior.ExactManualPositions,prior.AutomaticallyAccepted,prior.AcquisitionQualities,false))throw new InvalidOperationException(LastError);
                    RestoreReviewedRows(prior.Reviewed);
                    RestoreRemovedRows(prior.Removed);
                    RestoreAcceptanceConfirmedRows(prior.AcceptanceConfirmed);
                    algorithmDiagnostics=new System.Collections.Generic.Dictionary<string,string>(prior.AlgorithmDiagnostics,StringComparer.OrdinalIgnoreCase);
                    original=oldOriginal;confidenceSources=oldSources;projectPath=oldProject;
                    analysisTree.MarkAnalyzed(prior.Analyzed);foreach(string path in prior.Selected)analysisTree.SetDataSelected(path,true);
                    rangeCountEnabled=range.rangeCountEnabled;rangeChargeEnabled=range.rangeChargeEnabled;rangeVoltageEnabled=range.rangeVoltageEnabled;
                    minimumRangeCount=range.minimumRangeCount;voltageShortcutMode=range.voltageShortcutMode;
                    rangeChargeMin=range.rangeChargeMin;rangeChargeMax=range.rangeChargeMax;rangeVoltageMin=range.rangeVoltageMin;rangeVoltageMax=range.rangeVoltageMax;
                    spectrumScopePath=oldScope;RefreshStatistics();
                    if(oldSelected>=0)SelectSourceRowCore(oldSelected,false);previewFilteredRow=oldPreview;
                    spectrum.RestoreArchiveViewState(view);
                    if(oldOverviewMode&&!string.IsNullOrEmpty(oldOverview))ShowLocationOverview(oldOverview);else if(!oldOverviewMode)ShowPdMapWorkspace();
                    analysisTree.RestoreNodeView(expanded,selectedNode,topNode);
                    reportCharts.Clear();reportCharts.AddRange(charts);reportChartBytes=bytes;ChartReportRequestSequence=oldReportSequence;
                    LoadedAnalysisId=oldLoaded;LastSavedAnalysisId=oldSaved;loadedArchiveRoot=oldRoot;ChartReportStatusMessage=oldChartStatus;
                    AnalysisStatus=oldAnalysisStatus;AnalysisSessionStatus=oldSessionStatus;AnalysisCompletedFileCount=oldComplete;
                    ResultChangeSequence=oldResultSequence;SelectionSequence=oldSelectionSequence;RefreshReviewUi();
                }
                finally{analysisTree.SuppressArchiveNotifications=oldSuppression;reviewNotificationDepth--;}
            };
        }
    }
}
