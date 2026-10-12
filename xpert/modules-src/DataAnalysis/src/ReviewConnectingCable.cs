using System;
using System.ComponentModel;
using HVX.Geometry;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool inputGeometryAlreadyCompensated, rebuildingConnectingGeometry;
        string connectingGeometryStatus="Disabled";
        bool manualPositionEditsSinceLoad;
        double appliedConnectingCableLengthM;
        string lastConnectingGeometryUiError="";
        ConnectingCableGeometryResult[] pendingConnectingModels=new ConnectingCableGeometryResult[0];
        readonly System.Windows.Forms.ToolTip connectingGeometryTip=new System.Windows.Forms.ToolTip { ShowAlways=true };
        [Category("Cable"),DefaultValue(0.0)]
        public double ConnectingCableLengthM
        { get { return pdmap.ConnectingCableLengthM; } set { pdmap.ConnectingCableLengthM=value; } }
        [Category("Cable"),DefaultValue(false)]
        [Description("Set true before loading Task17 rows whose geometry already excludes the connecting cable. Never mix total and DUT-only geometry in one matrix.")]
        public bool InputGeometryAlreadyCompensated
        {
            get { return inputGeometryAlreadyCompensated; }
            set
            {
                if(inputGeometryAlreadyCompensated==value)return;bool old=inputGeometryAlreadyCompensated;inputGeometryAlreadyCompensated=value;
                try { RebuildConnectingGeometry(); } catch {inputGeometryAlreadyCompensated=old;throw;}
            }
        }
        [Category("Appearance"),DefaultValue(false)]
        public bool ReversePositionDisplay
        { get { return pdmap.ReversePositionDisplay; } set { pdmap.ReversePositionDisplay=value; } }
        [Browsable(false)] public string ConnectingCableGeometryStatus { get { return connectingGeometryStatus; } }
        [Browsable(false)] public bool ResultGeometryAlreadyCompensated { get { return inputGeometryAlreadyCompensated||ConnectingCableLengthM>0; } }
        [Browsable(false)] public double PresentedLocationM { get { return selected<0?double.NaN:ProjectAccessoryLocation(selected,working[selected,13]); } }

        void InitializeConnectingGeometry()
        {
            // The owner transforms source geometry once; the child always receives DUT rows.
            pdmap.InputGeometryAlreadyCompensated=true;
            pdmap.ConnectingCableSettingsChanged+=(s,e)=>RebuildConnectingGeometry();
            pdmap.ConnectingCableUiFeedback+=(s,e)=>
            {
                string next=pdmap.ConnectingCableUiError;
                if(next.Length>0)
                {
                    LastError=next;
                    if(next!=lastConnectingGeometryUiError)
                        AnalysisDiagnosticText+=Environment.NewLine+"Rejected lead edit (previous geometry retained): "+next;
                }
                else if(LastError==lastConnectingGeometryUiError)LastError="";
                lastConnectingGeometryUiError=next;
                RefreshReviewUi();
            };
            Disposed+=(s,e)=>connectingGeometryTip.Dispose();
        }
        double PresentReviewPosition(int row,double physical)
        { return ReversePositionDisplay&&row>=0&&row<SourceRowCount&&Finite(physical)&&Finite(working[row,5])?working[row,5]-physical:physical; }

        double[,] PrepareConnectingGeometry(double[,] input,double[,] current,bool configurationChange)
        {
            var next=(double[,])current.Clone();
            var models=new ConnectingCableGeometryResult[input.GetLength(0)];
            if(capacitorProject){pendingConnectingModels=models;return next;}
            if(!configurationChange&&(ConnectingCableLengthM<=0||inputGeometryAlreadyCompensated))
            {
                if(ConnectingCableLengthM>0)
                    for(int r=0;r<input.GetLength(0);r++)models[r]=pdmap.ResolveProvidedDutGeometry(Row(input,r));
                pendingConnectingModels=models;return next;
            }
            for(int r=0;r<input.GetLength(0);r++)
            {
                if(!Finite(input[r,7])||input[r,7]<=0)throw new ArgumentException("连接线补偿需要有效的采样率。 / Valid sample rate is required.");
                if(configurationChange&&ConnectingCableLengthM==0&&!inputGeometryAlreadyCompensated&&!manualPositionEditsSinceLoad)
                {
                    foreach(int column in new[]{5,6,14,13})next[r,column]=input[r,column];
                    next[r,33]=double.NaN;continue;
                }
                ConnectingCableGeometryResult model=configurationChange&&inputGeometryAlreadyCompensated
                    ?pdmap.ResolveChangedProvidedReviewGeometry(Row(current,r),appliedConnectingCableLengthM)
                    :pdmap.ResolveConnectingGeometry(Row(input,r));
                models[r]=model;
                next[r,5]=model.DutLengthM;
                next[r,6]=2*model.DutLengthM/model.GeometryDutRoundTripUs;
                double roundTripSamples=model.GeometryDutRoundTripUs*input[r,7]/1e6;
                // Same diagnostic column contract and downstream arithmetic headroom as Task17.
                if(!Finite(roundTripSamples)||roundTripSamples<=0||roundTripSamples>(int.MaxValue-1024)/8d)
                    throw new ArgumentException("补偿后的往返采样数超出有效范围。 / Compensated round-trip sample count is outside the supported range.");
                next[r,14]=Math.Max(2,Math.Round(roundTripSamples));
                double delayUs=next[r,12]*1e6/input[r,7];
                next[r,13]=Finite(delayUs)&&next[r,12]>0
                    ?(ConnectingCableLengthM==0&&!pdmap.MixedCableCalculation?model.DutLengthM-delayUs*next[r,6]*.5:model.PositionFromMeasuredDelayUs(delayUs))
                    :double.NaN;
                // A score from the former geometry is not evidence for a new location.
                next[r,33]=double.NaN;
            }
            pendingConnectingModels=models;return next;
        }
        void RebuildConnectingGeometry()
        {
            if(rebuildingConnectingGeometry)return;
            if(SourceRowCount==0)
            {
                connectingGeometryStatus=ConnectingCableLengthM>0?"WaitingForData":"Disabled";
                RefreshApproximationProjection();return;
            }
            spectrum.FlushReviewPreview();
            var oldPendingModels=pendingConnectingModels;
            var next=PrepareConnectingGeometry(original,working,true);
            var oldModels=pdmap.SnapshotReviewConnectingGeometry();double oldLead=appliedConnectingCableLengthM;
            var oldWorking=working;var oldSources=confidenceSources;string oldStatus=connectingGeometryStatus;
            int oldSelected=selected,oldPreviewFilteredRow=previewFilteredRow,oldLastSelected=lastSelectedForFilter,oldSelectionSequence=SelectionSequence,oldResultSequence=ResultChangeSequence;
            var oldMask=reviewMask;var oldFiltered=filteredRows;var oldScores=locationScores;
            string oldFilterDiagnostics=FilteringDiagnosticsJson,oldLocationDiagnostics=LocationFilteringDiagnosticsJson,oldLocationStatus=locationScoreStatus,oldFilterError=locationFilterError;
            int oldRefreshSequence=FilterRefreshSequence,oldScoreBuildCount=LocationScoreBuildCount;
            rebuildingConnectingGeometry=true;bool oldSync=synchronizing,oldLoading=loadingData;synchronizing=true;loadingData=true;
            try
            {
                // Validate all rows before changing any live matrix or selection.
                NormalizeEndpointPositions(next);
                pdmap.SetReviewConnectingGeometry(pendingConnectingModels);
                pdmap.BeginConnectingGeometryReload();
                try { if(selected>=0&&!pdmap.LoadDataMatricesAtRow(next,matching,selected))throw new ArgumentException(pdmap.LastError); }
                finally {pdmap.EndConnectingGeometryReload();}
                working=next;
                confidenceSources=new int[next.GetLength(0)];
                if(selected>=0)
                {
                    var measured=pdmap.ReevaluateConnectingGeometryConfidence();
                    working[selected,21]=measured[0];working[selected,33]=measured[1];confidenceSources[selected]=1;
                    pdmap.CommitReviewRow(selected,Row(working,selected));
                }
                LoadSpectrumDisplayData();spectrum.AcceptedOnly=true;
                connectingGeometryStatus=ConnectingCableLengthM<=0?"Disabled":inputGeometryAlreadyCompensated?"InputAlreadyCompensated":"GeometryChangedConfidenceNeedsReview";
                RefreshApproximationProjection();InvalidateLocationScores();RefreshStatisticsAfterEdit();
                if(selected>=0)spectrum.HighlightSourceRow(selected);
                ResultChangeSequence++;LastError="";RefreshReviewUi();
                appliedConnectingCableLengthM=ConnectingCableLengthM;
            }
            catch
            {
                pendingConnectingModels=oldPendingModels;
                working=oldWorking;confidenceSources=oldSources;connectingGeometryStatus=oldStatus;
                selected=oldSelected;previewFilteredRow=oldPreviewFilteredRow;lastSelectedForFilter=oldLastSelected;SelectionSequence=oldSelectionSequence;ResultChangeSequence=oldResultSequence;
                reviewMask=oldMask;filteredRows=oldFiltered;locationScores=oldScores;
                FilteringDiagnosticsJson=oldFilterDiagnostics;LocationFilteringDiagnosticsJson=oldLocationDiagnostics;locationScoreStatus=oldLocationStatus;locationFilterError=oldFilterError;
                FilterRefreshSequence=oldRefreshSequence;LocationScoreBuildCount=oldScoreBuildCount;
                pdmap.RestoreConnectingLengthWithoutNotification(oldLead);pdmap.SetReviewConnectingGeometry(oldModels);
                if(selected>=0)
                { pdmap.BeginConnectingGeometryReload();try {pdmap.LoadDataMatricesAtRow(working,matching,selected);}finally{pdmap.EndConnectingGeometryReload();} }
                LoadSpectrumDisplayData();spectrum.AcceptedOnly=true;
                spectrum.SetReviewVisibility(reviewMask);RefreshApproximationProjection();spectrum.HighlightSourceRow(selected);RefreshReviewUi();
                throw;
            }
            finally {synchronizing=oldSync;loadingData=oldLoading;rebuildingConnectingGeometry=false;}
            if(selected!=oldSelected){SelectionSequence++;NotifyReviewHost(CurrentPdMapChanged);}
            NotifyReviewHost(ResultsChanged);
        }
        void SetConnectingGeometryLoadedStatus()
        { manualPositionEditsSinceLoad=Array.Exists(exactManualPositionRows,value=>value);appliedConnectingCableLengthM=ConnectingCableLengthM;connectingGeometryStatus=ConnectingCableLengthM<=0?"Disabled":inputGeometryAlreadyCompensated?"InputAlreadyCompensated":"GeometryChangedConfidenceNeedsReview";pdmap.ClearConnectingCableUiFeedback(); }
        void UpdateConnectingGeometryTip()
        { if(metricValues!=null)connectingGeometryTip.SetToolTip(metricValues[0],metricValues[0].AccessibleDescription); }
    }
}
