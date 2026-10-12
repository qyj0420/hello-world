using System;
using System.ComponentModel;

namespace LabVIEWPdMap
{
    public sealed class PdMapAnalysisChangedEventArgs : EventArgs
    {
        readonly double[] parameterRow;
        public int SourceRowIndex { get; private set; }
        public int PdMapId { get; private set; }
        public int ConfidenceSourceCode { get; private set; }
        public bool IsPreview { get; private set; }
        public string ChangeReason { get; private set; }
        public double[] ParameterRow { get { return (double[])parameterRow.Clone(); } }
        internal PdMapAnalysisChangedEventArgs(int row,int id,double[] values,int confidenceSource,bool preview,string reason)
        {
            SourceRowIndex=row; PdMapId=id; parameterRow=(double[])values.Clone();
            ConfidenceSourceCode=confidenceSource; IsPreview=preview; ChangeReason=reason;
        }
    }

    public sealed partial class PdMapControl
    {
        int reviewSourceRowOverride=-1, reviewLoadDepth, reviewConfidenceSourceCode;
        double[] lastReviewNotification=Array.Empty<double>();
        bool lastReviewNotificationWasPreview;
        bool reviewDerivedDirty;

        public event EventHandler<PdMapAnalysisChangedEventArgs> AnalysisEdited;
        [Browsable(false)] public int CurrentConfidenceSourceCode { get { return reviewConfidenceSourceCode; } }
        [Browsable(false)] public string CurrentConfidenceSourceName { get { return reviewConfidenceSourceCode==1?"ManualWaveformEstimate":"SourceMatrix"; } }
        [Browsable(false)] public double OriginalSourceConfidence { get { return originalParameterRow.Length>33?originalParameterRow[33]:double.NaN; } }
        [Browsable(false)] public double CurrentSampleIntervalNs { get { return dtNs; } }
        [Browsable(false)] public long ReviewEditSequence { get; private set; }

        // This wrapper never emits an edit event. Loading and refreshing options do not accept a candidate.
        public void SetPdMapData(double[,] positioningResult,double[,] pdmapMatchingMatrix,double pdmapId,bool[] visibility,double[] filters,double[,] previousPositioningResults,double[] connectorPositionsM,double acceptedErrorPercent,bool englishVersion)
        {
            double[] before=originalParameterRow;
            reviewLoadDepth++;
            try { SetPdMapDataCore(positioningResult,pdmapMatchingMatrix,pdmapId,visibility,filters,previousPositioningResults,connectorPositionsM,acceptedErrorPercent,englishVersion); }
            finally
            {
                reviewLoadDepth--;
                if(!ReferenceEquals(before,originalParameterRow))
                {
                    reviewConfidenceSourceCode=0;
                    // Geometry options may recalculate on load, but cannot invent an input confidence.
                    confidence=reflectionSelected&&originalParameterRow.Length>33&&IsFinite(originalParameterRow[33])?ToPercent(originalParameterRow[33]):double.NaN;
                    NormalizeLoadedCoincidentConfidence();
                    ValidateReflectionSelectionWindow();
                    lastReviewNotification=GetReviewParameterRow();
                    lastReviewNotificationWasPreview=false;
                }
            }
        }

        public bool LoadDataMatricesAtRow(double[,] positioningResult,double[,] pdmapMatchingMatrix,int sourceRow)
        {
            double[,] candidate=positioningResult??positioningMatrix;
            double[,] matching=pdmapMatchingMatrix??matchingMatrix;
            if(!IsUsableMatrix(candidate,36)||!IsUsableMatrix(matching,1)||sourceRow<0||sourceRow>=candidate.GetLength(0))
            { LastError=L("Invalid source row or data matrices.","源矩阵行或输入矩阵无效。"); return false; }
            double id=candidate[sourceRow,9];
            if(!IsFinite(id)||id<int.MinValue||id>int.MaxValue||id!=Math.Round(id))
            { LastError=L("The selected row does not contain an integer PDmap ID.","所选源矩阵行不包含有效整数 PDmap ID。"); return false; }
            int previousOverride=reviewSourceRowOverride;
            reviewSourceRowOverride=sourceRow;
            try
            {
                previousOverlayExplicit=false; standaloneOverlay=false; previousPositioningMatrix=null;
                ApplyIndependentDataInput(positioningResult,pdmapMatchingMatrix,(int)id);
                return LastError.Length==0&&currentParameterRowIndex==sourceRow&&currentPdMapId==(int)id;
            }
            finally { reviewSourceRowOverride=previousOverride; }
        }

        public bool SelectSourceRow(int sourceRow) { return LoadDataMatricesAtRow(null,null,sourceRow); }

        // Explicit archive/new-analysis reload is different from an unchanged
        // host refresh: cursor state may have changed since these inputs loaded.
        internal void RequireReviewInputReload() { forceReloadNextInput=true; }

        public double[] GetReviewParameterRow()
        {
            if(originalParameterRow.Length==0)return Array.Empty<double>();
            double[] row=GetSelectedParameterRow();
            // Accessory approximation has a precise display location in addition to the integer sample location.
            row[13]=DisplayedLocationM;
            row[33]=reviewConfidenceSourceCode==1?confidence/100.0:originalParameterRow[33];
            if(reflectionSelected&&reflection<=incident)row[33]=double.NaN;
            if(positioningMatrix!=null&&currentParameterRowIndex>=0&&currentParameterRowIndex<positioningMatrix.GetLength(0))
                row[35]=positioningMatrix[currentParameterRowIndex,35];
            // Missing reflection is a distinct state, not a zero-delay pair.
            // Manual selection at the original incident sample is still a real edit.
            if(!reflectionSelected)ClearUnselectedPairColumns(row);
            return row;
        }

        // Feedback from the owning shared matrix: no waveform load, cursor reset, score recompute or edit event.
        public bool CommitReviewRow(int sourceRow,double[] row)
        { return CommitReviewRow(sourceRow,row,false); }

        internal bool CommitReviewRow(int sourceRow,double[] row,bool preview)
        {
            if(positioningMatrix==null||row==null||row.Length!=positioningMatrix.GetLength(1)||sourceRow<0||sourceRow>=positioningMatrix.GetLength(0))return false;
            if(!row[9].Equals(positioningMatrix[sourceRow,9]))return false;
            bool changed=false;
            for(int c=0;c<row.Length;c++)if(!positioningMatrix[sourceRow,c].Equals(row[c])){changed=true;break;}
            if(!changed){if(!preview)FlushReviewPreview();return true;}
            for(int c=0;c<row.Length;c++)positioningMatrix[sourceRow,c]=row[c];
            reviewDerivedDirty=true;
            if(!preview)FlushReviewPreview();
            Invalidate(); return true;
        }

        internal void FlushReviewPreview()
        {
            if(!reviewDerivedDirty)return;reviewDerivedDirty=false;
            countedMatrix=null; builtOverlayMatrix=null;
            BuildPreviousOverlay(); CountSamePositionPulses(DisplayedLocationM);
            Invalidate();
        }

        internal void CompleteReviewPreview() { EndCursorDrag("review commit"); }

        void NotifyReviewAnalysisEdited(string reason,bool preview=false,bool restored=false)
        {
            if(reviewLoadDepth!=0||!matrixDriven||originalParameterRow.Length<36||currentParameterRowIndex<0)return;
            double[] candidate=GetReviewParameterRow();
            // A confidence-only re-evaluation must not compare the immutable
            // input score merely because no cursor index changed.
            if(!restored)candidate[33]=confidence/100.0;
            bool actualChange=!SameReviewAnalysis(candidate,lastReviewNotification);
            if(!restored&&!actualChange&&(preview||!lastReviewNotificationWasPreview))return;
            if(restored)reviewConfidenceSourceCode=0;
            else if(actualChange)reviewConfidenceSourceCode=1;
            double[] row=GetReviewParameterRow();
            bool same=SameValues(row,lastReviewNotification);
            if(same&&(preview||!lastReviewNotificationWasPreview))return;
            lastReviewNotification=(double[])row.Clone(); lastReviewNotificationWasPreview=preview;
            ReviewEditSequence++;
            var handler=AnalysisEdited;
            if(handler!=null)
            {
                var args=new PdMapAnalysisChangedEventArgs(currentParameterRowIndex,currentPdMapId,row,reviewConfidenceSourceCode,preview,reason);
                foreach(EventHandler<PdMapAnalysisChangedEventArgs> subscriber in handler.GetInvocationList())
                    try { subscriber(this,args); }
                    catch(Exception ex)
                    {
                        if(ex is OutOfMemoryException||ex is StackOverflowException||ex is AccessViolationException||ex is System.Threading.ThreadAbortException)throw;
                        LastError=(L("Host event handler failed: ","宿主事件处理失败："))+ex.Message;
                    }
            }
        }

        static bool SameReviewAnalysis(double[] a,double[] b)
        {
            if(a.Length<36||b.Length<36)return false;
            int[] columns={10,11,12,13,16,20,21,33};
            foreach(int column in columns)if(!a[column].Equals(b[column]))return false;
            return true;
        }

        /// <summary>Curve 0..5: raw PD, aligned calibration, damping, filtered PD, filtered calibration, filtered damping.
        /// Returns [local sample index, amplitude]. Unavailable curves are empty, never fabricated zero or NaN curves.</summary>
        public double[,] GetReportCurve(int curveIndex)
        {
            if(curveIndex<0||curveIndex>5)throw new ArgumentOutOfRangeException("curveIndex");
            double[] values; int first=0,last;
            switch(curveIndex)
            {
                case 0: values=pulse; break;
                case 1: values=hasAlignedCalibrationCurve?alignedCalibration:Array.Empty<double>(); break;
                case 2:
                    values=HasDampingCurve?alignedAttenuation:Array.Empty<double>();
                    UpdateDampingDisplayRange(); first=dampingDisplayStart; break;
                case 3: values=filteredPulse; break;
                case 4: values=hasAlignedCalibrationCurve?filteredCalibration:Array.Empty<double>(); break;
                default: values=HasFilteredDampingCurve?filteredAttenuation:Array.Empty<double>(); first=filteredDampingStart; break;
            }
            last=curveIndex==2?dampingDisplayEnd:curveIndex==5?filteredDampingEnd:values.Length-1;
            first=Math.Max(0,first); last=Math.Min(values.Length-1,last);
            if(last<first)return new double[0,2];
            for(int i=first;i<=last;i++)if(!IsFinite(values[i]))return new double[0,2];
            var result=new double[last-first+1,2];
            for(int i=first;i<=last;i++){result[i-first,0]=i;result[i-first,1]=values[i];}
            return result;
        }

        public bool[] GetReportCurveVisibility()
        {
            return new[]{Visible(0,true),Visible(1,false)&&showCalibration&&hasAlignedCalibrationCurve,
                Visible(2,false)&&showAttenuation&&HasDampingCurve,Visible(3,false),
                Visible(4,false)&&showCalibration&&hasAlignedCalibrationCurve,Visible(5,false)&&showAttenuation&&HasFilteredDampingCurve};
        }
    }
}
