using System;
using System.ComponentModel;
using HVX.Geometry;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        ConnectingCableGeometryResult overviewGeometry;
        string overviewGeometryStatus="NoLocationFile";
        bool overviewGeometryMixed;
        double overviewGeometryLead=double.NaN;
        string overviewGeometryPath="";
        int overviewMetadataReads=-1,overviewCalibrationReads=-1;
        double overviewGeometryVelocity=double.NaN;
        [Browsable(false)]public bool OverviewMeasurementVisible {get{return locationOverview!=null&&locationOverview.CursorsVisible;}}
        [Browsable(false)]public int[] OverviewCursorIndices {get{return locationOverview==null?new[]{-1,-1}:new[]{locationOverview.Cursor1Index,locationOverview.Cursor2Index};}}
        [Browsable(false)]public double OverviewTimeDifferenceNs {get{return locationOverview==null?double.NaN:locationOverview.CursorDeltaNs;}}
        [Browsable(false)]public double OverviewLocationM {get{return locationOverview==null?double.NaN:locationOverview.CursorPositionM;}}
        [Browsable(false)]public string OverviewMeasurementStatus {get{EnsureOverviewMeasurementGeometry();if(locationOverview==null)return overviewGeometryStatus;double ignored=locationOverview.CursorPositionM;return locationOverview.MeasurementGeometryStatus;}}
        public event EventHandler OverviewMeasurementChanged;
        public void SetOverviewMeasurementCursors(int firstSampleIndex,int secondSampleIndex)
        {locationOverview.SetMeasurementCursors(firstSampleIndex,secondSampleIndex);}
        public void ZoomLocationOverview(int startSampleIndex,int endSampleIndex)
        {locationOverview.ZoomToSamples(startSampleIndex,endSampleIndex);}
        public void ResetLocationOverviewView(){locationOverview.ResetView();}
        void InitializeOverviewMeasurement()
        {
            locationOverview.PositionForDelayNs=OverviewPositionForDelay;
            locationOverview.RoundTripTimeNs=OverviewRoundTripTimeNs;
            locationOverview.MeasurementVelocityText=OverviewVelocityText;
            locationOverview.CursorsChanged+=(s,e)=>{if(locationOverview.SampleCount==0){overviewGeometry=null;overviewGeometryStatus="NoLocationFile";locationOverview.MeasurementGeometryStatus=overviewGeometryStatus;}NotifyReviewHost(OverviewMeasurementChanged);};
            pdmap.ConnectingCableSettingsChanged+=(s,e)=>{if(!string.IsNullOrEmpty(overviewPath))RefreshOverviewMeasurementGeometry(overviewPath);};
        }
        void EnsureOverviewMeasurementGeometry()
        {
            if(locationOverview==null||analysisTree==null)return;
            if(locationOverview.SampleCount==0){overviewGeometry=null;overviewGeometryStatus="NoLocationFile";locationOverview.MeasurementGeometryStatus=overviewGeometryStatus;return;}
            // Cached metadata only: no file read and no full-record scan per drag.
            if(!String.Equals(overviewGeometryPath,overviewPath,StringComparison.OrdinalIgnoreCase)||
                overviewGeometryLead!=ConnectingCableLengthM||overviewGeometryMixed!=pdmap.MixedCableCalculation||
                overviewMetadataReads!=pdmap.ProjectMetadataReadCount||overviewCalibrationReads!=pdmap.ProjectCalibrationReadCount||
                !overviewGeometryVelocity.Equals(locationOverview.VelocityMPerUs))RefreshOverviewMeasurementGeometry(overviewPath);
        }
        void RefreshOverviewMeasurementGeometry(string path)
        {
            overviewGeometry=null;overviewGeometryStatus="NoLocationFile";
            overviewGeometryLead=ConnectingCableLengthM;overviewGeometryMixed=pdmap.MixedCableCalculation;
            overviewGeometryPath=path;overviewMetadataReads=pdmap.ProjectMetadataReadCount;overviewCalibrationReads=pdmap.ProjectCalibrationReadCount;
            overviewGeometryVelocity=locationOverview.VelocityMPerUs;
            string[] paths=analysisTree.GetDataPaths();double[,] catalog=analysisTree.GetDataCatalog();
            int index=Array.FindIndex(paths,p=>string.Equals(p,path,StringComparison.OrdinalIgnoreCase));
            if(!capacitorProject&&index>=0)pdmap.TryResolveOverviewGeometry((int)catalog[index,0],locationOverview.VelocityMPerUs,out overviewGeometry,out overviewGeometryStatus);
            if(capacitorProject)overviewGeometryStatus="CapacitorNoLocation";
            locationOverview.MeasurementGeometryStatus=overviewGeometryStatus;locationOverview.Invalidate();
            NotifyReviewHost(OverviewMeasurementChanged);
        }
        double OverviewPositionForDelay(double delayNs)
        {
            EnsureOverviewMeasurementGeometry();
            if(overviewGeometry==null||!Finite(delayNs))return double.NaN;
            double raw=overviewGeometry.PositionFromMeasuredDelayUs(delayNs/1000.0);
            double position=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(raw,overviewGeometry.DutLengthM);
            // Arbitrary cursor pairs can exceed the physical interval. Show an
            // unavailable position rather than inventing an endpoint.
            if(position<0||position>overviewGeometry.DutLengthM){locationOverview.MeasurementGeometryStatus=L("Cursor delay exceeds the cable round trip.","游标时差超出电缆往返范围");return double.NaN;}
            locationOverview.MeasurementGeometryStatus=overviewGeometryStatus;
            return ReversePositionDisplay?overviewGeometry.DutLengthM-position:position;
        }
        double OverviewRoundTripTimeNs()
        {
            EnsureOverviewMeasurementGeometry();
            return overviewGeometry==null?double.NaN:overviewGeometry.GeometryDutRoundTripUs*1000.0;
        }
        string OverviewVelocityText()
        {
            EnsureOverviewMeasurementGeometry();
            if(overviewGeometry==null)return L("Velocity: unavailable","波速：不可用");
            double[] speeds=overviewGeometry.DutSegmentVelocitiesMPerUs;
            bool mixed=false;for(int i=1;i<speeds.Length;i++)if(Math.Abs(speeds[i]-speeds[0])>1e-6)mixed=true;
            if(mixed){
                string[] values=Array.ConvertAll(speeds,v=>v.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture));
                return L("Segment velocities: ","分段波速：")+string.Join(" / ",values)+" m/μs";
            }
            return L("Velocity: ","波速：")+speeds[0].ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" m/μs";
        }
    }
}
