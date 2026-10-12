using System;
using System.Collections.Generic;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    public sealed partial class UnifiedSpectrumControl
    {
        // In-memory transaction token, not a serialization format. The owner
        // restores its authoritative source matrix before restoring this token.
        sealed class ArchiveViewState
        {
            internal UnifiedSpectrumControl Owner;
            internal int Rows,Columns,Mode,PhaseFilter,PositionBins,PhaseBins,ChargeBins,ChargeUnit,Shape,Action,ActionSequence,ColorReference;
            internal bool AcceptedOnly,ShowJoints,ShowPhaseReference,ShowClusters,DataLoaded,Compensated,Classification,FtAuto,PreviewDirty;
            internal bool ChargeCounts;
            internal bool[] Visible,ReviewVisibility,RegionMask;
            internal double[] CountRange,HitIds,RestoredFlags;
            internal int[] Deleted,LastRows,HitRows,RestoredRows,RegionRows;
            internal double[,] LastMatrix;
            internal long ReviewRevision;
            internal double LowThreshold,HighThreshold,FtX0,FtX1,FtY0,FtY1,ClusterTolerance,ReviewClusterTolerance;
            internal int ClusterMinimum,SelectedRow;
            internal double SelectedMap,SelectedRecord,SelectedLoc;
            internal string Interaction,DischargeHeader,Error,SelfCheckCode;
            internal bool SelfCheckPassed;
            internal readonly Dictionary<int,ViewState> States=new Dictionary<int,ViewState>();
            internal readonly Dictionary<int,CameraState> Cameras=new Dictionary<int,CameraState>();
            internal RegionDeletion[] Undo;
            internal int RegionMode,RegionRectangles,RegionEllipses;
            internal double RegionX0,RegionX1,RegionY0,RegionY1,PriorFtX0,PriorFtX1,PriorFtY0,PriorFtY1;
            internal bool PriorFtAuto;
            internal readonly Dictionary<int,RegionViewRange> RegionPrior=new Dictionary<int,RegionViewRange>();
            internal Func<int,double,double> Projection;
            internal Func<int,double> LengthProvider;
            internal Func<int,double,double[]> JointProvider;
            internal double CurrentLength;
            internal double[] CurrentJoints;
            internal bool GeometryDirty,AutomaticConclusions,ShowConclusions,ConclusionProvided;
            internal string ConclusionContext,ConclusionStatus,ConclusionCode,ConclusionApi,ConclusionVoltageSource,Task15Path;
            internal SpectrumConclusionMarker[] Conclusions;
            internal double OperatingVoltage,ServiceYears,UsedVoltage;
            internal int BodyType,JointType;
        }
        static SelectionRegion CopyArchiveRegion(SelectionRegion r)
        {return new SelectionRegion{X0=r.X0,X1=r.X1,Y0=r.Y0,Y1=r.Y1,Circle=r.Circle};}
        static ViewState CopyArchiveView(ViewState s)
        {
            var result=new ViewState{X0=s.X0,X1=s.X1,Y0=s.Y0,Y1=s.Y1,AutoX0=s.AutoX0,AutoX1=s.AutoX1,AutoY0=s.AutoY0,AutoY1=s.AutoY1,
                HasAutomaticRange=s.HasAutomaticRange,ReviewRangeDirty=s.ReviewRangeDirty};
            result.Regions.AddRange(s.Regions.Select(CopyArchiveRegion));return result;
        }
        static CameraState CopyArchiveCamera(CameraState c)
        {var result=new CameraState{PlaneIndex=c.PlaneIndex,Azimuth=c.Azimuth,Elevation=c.Elevation};result.UpdateProjection();return result;}
        static RegionDeletion CopyArchiveDeletion(RegionDeletion d)
        {return new RegionDeletion{Rows=(int[])d.Rows.Clone(),Flags=(double[])d.Flags.Clone(),Mode=d.Mode,Regions=d.Regions.Select(CopyArchiveRegion).ToArray()};}
        static RegionViewRange CopyArchivePrior(RegionViewRange r)
        {return new RegionViewRange{X0=r.X0,X1=r.X1,Y0=r.Y0,Y1=r.Y1,Automatic=r.Automatic};}
        static SpectrumConclusionMarker[] CopyArchiveConclusions(SpectrumConclusionMarker[] markers)
        {return markers.Select(m=>new SpectrumConclusionMarker{PhaseMask=m.PhaseMask,ActionCode=m.ActionCode,Position=m.Position}).ToArray();}
        static T[] CopyArchiveArray<T>(T[] values){return values==null?null:(T[])values.Clone();}

        internal object CaptureArchiveViewState()
        {
            var s=new ArchiveViewState{
                Owner=this,Rows=source.GetLength(0),Columns=source.GetLength(1),Mode=mode,PhaseFilter=phaseFilter,PositionBins=positionBins,PhaseBins=phaseBins,ChargeBins=chargeBins,
                ChargeUnit=chargeUnit,Shape=shape,Action=regionActionCommand,ActionSequence=RegionActionSequence,ColorReference=lPrpdColorReference,
                AcceptedOnly=acceptedOnly,ShowJoints=showJoints,ShowPhaseReference=showPhaseReference,ShowClusters=showLocationClusters,DataLoaded=dataLoaded,
                ChargeCounts=showChargeCountOverlay,
                Compensated=useCompensatedCharge,Classification=confidenceClassificationEnabled,FtAuto=frequencyTimeAutoRange,PreviewDirty=reviewPreviewDirty,
                Visible=CopyArchiveArray(visible),ReviewVisibility=CopyArchiveArray(reviewVisibility),CountRange=CopyArchiveArray(reviewCountRange),
                HitIds=CopyArchiveArray(hitIds),RestoredFlags=CopyArchiveArray(restoredRegionFlags),Deleted=deleted.ToArray(),LastRows=CopyArchiveArray(lastRows),
                HitRows=CopyArchiveArray(hitRows),RestoredRows=CopyArchiveArray(restoredRegionRows),LastMatrix=(double[,])lastMatrix.Clone(),ReviewRevision=reviewRevision,
                LowThreshold=classificationLowThresholdPercent,HighThreshold=classificationHighThresholdPercent,FtX0=ftX0,FtX1=ftX1,FtY0=ftY0,FtY1=ftY1,
                ClusterTolerance=clusterTolerance,ClusterMinimum=clusterMinimum,ReviewClusterTolerance=reviewLocationClusterTolerancePercent,
                SelectedRow=SelectedSourceRowIndex,SelectedMap=SelectedPdMapId,SelectedRecord=SelectedRecordId,SelectedLoc=SelectedLocId,
                Interaction=LastInteractionStatus,DischargeHeader=dischargeIdentificationText,Error=lastError,SelfCheckCode=selfCheckCode,SelfCheckPassed=selfCheckPassed,
                RegionMask=CopyArchiveArray(regionStatisticsMask),RegionRows=CopyArchiveArray(regionStatisticsRows),RegionMode=regionStatisticsMode,
                RegionRectangles=regionStatisticsRectangleCount,RegionEllipses=regionStatisticsEllipseCount,RegionX0=regionStatisticsX0,RegionX1=regionStatisticsX1,RegionY0=regionStatisticsY0,RegionY1=regionStatisticsY1,
                PriorFtAuto=regionStatisticsPriorFtAuto,PriorFtX0=regionStatisticsPriorFtX0,PriorFtX1=regionStatisticsPriorFtX1,PriorFtY0=regionStatisticsPriorFtY0,PriorFtY1=regionStatisticsPriorFtY1,
                Projection=reviewPositionProjection,LengthProvider=reviewCableLengthProvider,JointProvider=reviewJointProvider,CurrentLength=currentLength,CurrentJoints=CopyArchiveArray(currentJoints),GeometryDirty=metadataGeometryDirty,
                AutomaticConclusions=automaticConclusions,ShowConclusions=showConclusionAnnotations,ConclusionProvided=conclusionProvided,ConclusionContext=conclusionContext,
                ConclusionStatus=conclusionStatus,ConclusionCode=conclusionStatusCode,ConclusionApi=conclusionApi,ConclusionVoltageSource=conclusionVoltageSource,Task15Path=task15RuntimePath,
                Conclusions=CopyArchiveConclusions(conclusionMarkers),OperatingVoltage=operatingVoltageKv,ServiceYears=conclusionServiceYears,UsedVoltage=conclusionUsedVoltage,BodyType=conclusionBodyType,JointType=conclusionJointType,
                Undo=regionDeletionHistory.Select(CopyArchiveDeletion).ToArray()
            };
            foreach(var pair in states)s.States.Add(pair.Key,CopyArchiveView(pair.Value));
            foreach(var pair in cameras)s.Cameras.Add(pair.Key,CopyArchiveCamera(pair.Value));
            foreach(var pair in regionStatisticsPriorViews)s.RegionPrior.Add(pair.Key,CopyArchivePrior(pair.Value));
            return s;
        }

        internal void RestoreArchiveViewState(object snapshot)
        {
            var s=snapshot as ArchiveViewState;
            if(s==null||!ReferenceEquals(s.Owner,this)||source.GetLength(0)!=s.Rows||source.GetLength(1)!=s.Columns)
                throw new ArgumentException("Restore the same control's authoritative source matrix before its archive view state.","snapshot");
            notificationDepth++;
            try
            {
                CancelGesture();mode=s.Mode;phaseFilter=s.PhaseFilter;positionBins=s.PositionBins;phaseBins=s.PhaseBins;chargeBins=s.ChargeBins;
                showChargeCountOverlay=s.ChargeCounts;
                chargeUnit=s.ChargeUnit;shape=s.Shape;regionActionCommand=s.Action;RegionActionSequence=s.ActionSequence;lPrpdColorReference=s.ColorReference;
                acceptedOnly=s.AcceptedOnly;showJoints=s.ShowJoints;showPhaseReference=s.ShowPhaseReference;showLocationClusters=s.ShowClusters;dataLoaded=s.DataLoaded;
                useCompensatedCharge=s.Compensated;confidenceClassificationEnabled=s.Classification;frequencyTimeAutoRange=s.FtAuto;
                visible=CopyArchiveArray(s.Visible);reviewVisibility=CopyArchiveArray(s.ReviewVisibility);reviewCountRange=CopyArchiveArray(s.CountRange);
                hitIds=CopyArchiveArray(s.HitIds);restoredRegionFlags=CopyArchiveArray(s.RestoredFlags);deleted.Clear();foreach(int row in s.Deleted)deleted.Add(row);
                lastRows=CopyArchiveArray(s.LastRows);hitRows=CopyArchiveArray(s.HitRows);restoredRegionRows=CopyArchiveArray(s.RestoredRows);lastMatrix=(double[,])s.LastMatrix.Clone();reviewRevision=s.ReviewRevision;
                classificationLowThresholdPercent=s.LowThreshold;classificationHighThresholdPercent=s.HighThreshold;ftX0=s.FtX0;ftX1=s.FtX1;ftY0=s.FtY0;ftY1=s.FtY1;
                clusterTolerance=s.ClusterTolerance;clusterMinimum=s.ClusterMinimum;reviewLocationClusterTolerancePercent=s.ReviewClusterTolerance;
                SelectedSourceRowIndex=s.SelectedRow;SelectedPdMapId=s.SelectedMap;SelectedRecordId=s.SelectedRecord;SelectedLocId=s.SelectedLoc;LastInteractionStatus=s.Interaction;
                dischargeIdentificationText=s.DischargeHeader;
                states.Clear();foreach(var pair in s.States)states.Add(pair.Key,CopyArchiveView(pair.Value));
                cameras.Clear();foreach(var pair in s.Cameras)cameras.Add(pair.Key,CopyArchiveCamera(pair.Value));
                regionStatisticsMask=CopyArchiveArray(s.RegionMask);regionStatisticsRows=CopyArchiveArray(s.RegionRows);regionStatisticsCombinedMask=regionStatisticsCombinedReview=null;
                regionStatisticsMode=s.RegionMode;regionStatisticsRectangleCount=s.RegionRectangles;regionStatisticsEllipseCount=s.RegionEllipses;
                regionStatisticsX0=s.RegionX0;regionStatisticsX1=s.RegionX1;regionStatisticsY0=s.RegionY0;regionStatisticsY1=s.RegionY1;
                regionStatisticsPriorFtAuto=s.PriorFtAuto;regionStatisticsPriorFtX0=s.PriorFtX0;regionStatisticsPriorFtX1=s.PriorFtX1;regionStatisticsPriorFtY0=s.PriorFtY0;regionStatisticsPriorFtY1=s.PriorFtY1;
                regionStatisticsPriorViews.Clear();foreach(var pair in s.RegionPrior)regionStatisticsPriorViews.Add(pair.Key,CopyArchivePrior(pair.Value));
                regionDeletionHistory.Clear();for(int i=s.Undo.Length-1;i>=0;i--)regionDeletionHistory.Push(CopyArchiveDeletion(s.Undo[i]));
                reviewPositionProjection=s.Projection;reviewCableLengthProvider=s.LengthProvider;reviewJointProvider=s.JointProvider;
                automaticConclusions=s.AutomaticConclusions;showConclusionAnnotations=s.ShowConclusions;conclusionProvided=s.ConclusionProvided;conclusionContext=s.ConclusionContext;
                conclusionStatus=s.ConclusionStatus;conclusionStatusCode=s.ConclusionCode;conclusionApi=s.ConclusionApi;conclusionVoltageSource=s.ConclusionVoltageSource;task15RuntimePath=s.Task15Path;
                conclusionMarkers=CopyArchiveConclusions(s.Conclusions);operatingVoltageKv=s.OperatingVoltage;conclusionServiceYears=s.ServiceYears;conclusionUsedVoltage=s.UsedVoltage;conclusionBodyType=s.BodyType;conclusionJointType=s.JointType;
                ClearDataViews();currentLength=s.CurrentLength;currentJoints=CopyArchiveArray(s.CurrentJoints);metadataGeometryDirty=false;
                // Rebuild only local drawing data: no filesystem/model evaluation,
                // no external selection/deletion/acceptance notification.
                reviewPreviewDirty=true;
                try {Rebuild(false);}
                finally {reviewPreviewDirty=s.PreviewDirty;metadataGeometryDirty=s.GeometryDirty;}
                // A pending automatic-range flag is state too: rebuilding must
                // not silently consume a saved range/selection snapshot.
                states.Clear();foreach(var pair in s.States)states.Add(pair.Key,CopyArchiveView(pair.Value));
                lastError=s.Error;selfCheckCode=s.SelfCheckCode;selfCheckPassed=s.SelfCheckPassed;
                SyncClassificationControl();SyncColorModeButton();SyncCameraToolbar();LayoutSpectrumToolbar();Invalidate();
            }
            finally{notificationDepth--;}
        }
    }
}
