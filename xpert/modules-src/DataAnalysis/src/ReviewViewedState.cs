using System;
using System.ComponentModel;
using System.Linq;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // A reviewed, unaccepted, non-removed row is an explicit uncertain decision.
        // Viewing alone never makes a review decision. Versioned session metadata
        // distinguishes this from the historical viewed/undecided state.
        bool[] reviewed = new bool[0];
        bool[] removed = new bool[0];
        // Explicit operator acceptance is separate from column 35, which an
        // algorithm may set to include an engineering candidate in statistics.
        // Keep its prior confirmation while removed so region undo is reversible.
        bool[] acceptanceConfirmed = new bool[0];
        // Exact cursor selection is not acceptance and does not add a row to statistics.
        bool[] exactManualPositionRows = new bool[0];
        [Browsable(false)] public bool[] ExactManualPositionRows { get { return (bool[])exactManualPositionRows.Clone(); } }
        bool IsExactManualPositionRow(int row)
        {return row>=0&&row<exactManualPositionRows.Length&&exactManualPositionRows[row];}
        void RememberExactManualPosition(int row)
        {
            if(synchronizing||loadingData||row!=selected||row<0||row>=exactManualPositionRows.Length||exactManualPositionRows[row])return;
            exactManualPositionRows[row]=true;MarkAnalysisDirty();
        }
        [Browsable(false)] public bool[] ReviewedRows { get { return (bool[])reviewed.Clone(); } }
        [Browsable(false)] public bool[] RemovedRows { get { return (bool[])removed.Clone(); } }
        [Browsable(false)] public bool[] AcceptanceConfirmedRows { get { return (bool[])acceptanceConfirmed.Clone(); } }
        [Browsable(false)] public int ReviewedRowCount { get { return reviewed.Count(value => value); } }
        [Browsable(false)] public bool CurrentUncertain { get { return selected>=0&&!CurrentAccepted&&!removed[selected]&&reviewed[selected]; } }
        [Browsable(false)] public bool CanMarkUncertain { get { return !IsAnalysisRunning&&selected>=0&&!CurrentUncertain&&IsReviewRowComplete(selected)&&pdmap.CurrentParameterRowIndex==selected; } }
        [Browsable(false)] public string CurrentReviewState { get { return selected<0?"None":CurrentAccepted?"Accepted":removed[selected]?"Removed":CurrentUncertain?"Uncertain":"Pending"; } }

        bool ViewSourceRow(int row)
        { return !IsAnalysisRunning && SelectSourceRowCore(row,true); }

        public bool MarkCurrentReviewed()
        { return MarkCurrentUncertain(); }

        public bool MarkCurrentUncertain()
        {
            if(!CanMarkUncertain)return false;
            int sourceRow=selected,priorResultSequence=ResultChangeSequence;
            var row=Row(working,sourceRow);row[35]=0;
            previewFilteredRow=sourceRow;
            acceptanceConfirmed[selected]=false;removed[selected]=false;reviewed[selected]=true;
            CommitRow(sourceRow,row);
            SyncReviewedTree();MarkAnalysisDirty();RefreshReviewUi();
            if(ResultChangeSequence==priorResultSequence){ResultChangeSequence++;NotifyReviewHost(ResultsChanged);}
            RecordOperation("ReviewUncertain",OperationContext()+"; source waveform retained; excluded from accepted statistics");
            return true;
        }

        void SetRowRemoved(int row,bool value)
        {
            if(row<0||row>=removed.Length||removed[row]==value)return;
            removed[row]=value;SyncReviewedTree();MarkAnalysisDirty();
        }

        void MarkRowsReviewed(params int[] rows)
        {
            bool changed = false;
            foreach (int row in rows)
                if (row >= 0 && row < reviewed.Length && !reviewed[row]) { reviewed[row] = true; changed = true; }
            if (!changed) return;
            SyncReviewedTree(); MarkAnalysisDirty();
        }

        void SyncReviewedTree()
        {
            if (analysisTree != null && !analysisTree.IsDisposed)
            {
                if (!analysisTree.SetReviewStates(reviewed,removed,acceptanceConfirmed)) LastError = analysisTree.LastError;
            }
        }

        void RestoreRemovedRows(bool[] values)
        {
            if(values==null||values.Length!=SourceRowCount)throw new ArgumentException("Removed state must match the source row count.");
            removed=(bool[])values.Clone();if(!treeSync)SyncReviewedTree();
        }

        void RestoreReviewedRows(bool[] values)
        {
            if (values == null || values.Length != SourceRowCount) throw new ArgumentException("Reviewed state must match the source row count.");
            reviewed = (bool[])values.Clone(); if (!treeSync) SyncReviewedTree();
        }

        void RestoreAcceptanceConfirmedRows(bool[] values)
        {
            if(values==null||values.Length!=SourceRowCount)throw new ArgumentException("Acceptance confirmation must match the source row count.");
            acceptanceConfirmed=(bool[])values.Clone();
            for(int row=0;row<acceptanceConfirmed.Length;row++)if(IsAcquisitionReferenceOnly(row))acceptanceConfirmed[row]=false;
            if(!treeSync)SyncReviewedTree();
        }

        // The caller supplies exact retained row indices, never an identity guess.
        // -1 represents newly analyzed data and starts unreviewed.
        bool ReloadWithRetainedReview(double[,] parameters, double[,] waves, int[] sourceRows)
        {return ReloadWithRetainedReviewCore(parameters,waves,sourceRows,false);}
        bool ReloadWithRetainedReviewCore(double[,] parameters,double[,] waves,int[] sourceRows,bool automatic,System.Collections.Generic.Dictionary<string,ReviewAcquisitionQuality> qualities=null)
        {
            if (sourceRows == null || sourceRows.Length != parameters.GetLength(0) || sourceRows.Any(r => r < -1 || r >= reviewed.Length))
                throw new ArgumentException("Invalid retained review-row mapping.");
            bool[] keep = sourceRows.Select(r => r >= 0 && reviewed[r]).ToArray();
            bool[] keepRemoved = sourceRows.Select(r => r >= 0 && removed[r]).ToArray();
            bool[] keepConfirmed = sourceRows.Select(r => r >= 0 && acceptanceConfirmed[r]).ToArray();
            bool[] keepExact = sourceRows.Select(r => r >= 0 && exactManualPositionRows[r]).ToArray();
            bool[] keepAutomatic=RetainAutomaticAcceptanceRows(automaticAcceptanceRows,sourceRows,parameters,automatic);
            var keepDiagnostics=algorithmDiagnostics;
            if (!LoadDataMatricesCore(parameters, waves, true, keepExact,keepAutomatic,qualities??acquisitionQualities)) return false;
            algorithmDiagnostics=keepDiagnostics;
            RefreshTimeFrequencyDisplayIfChanged();ApplyTreeSpectrumVisibility();
            RestoreReviewedRows(keep);RestoreRemovedRows(keepRemoved);RestoreAcceptanceConfirmedRows(keepConfirmed); return true;
        }

        static bool[] ParseReviewedRows(string value, int count)
        {
            if (value == null || value.Length != count || value.Any(c => c != '0' && c != '1'))
                throw new ArgumentException("Invalid reviewed-row state in analysis session.");
            return value.Select(c => c == '1').ToArray();
        }
    }
}
