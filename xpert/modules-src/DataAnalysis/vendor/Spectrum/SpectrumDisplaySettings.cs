using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        int chargeUnit;
        bool showPhaseReference = true, showLocationClusters = true, dataLoaded;
        bool frequencyTimeAutoRange = true;
        static readonly double[] FrequencyTimeNiceMultipliers = {1,1.2,1.5,2,2.5,3,4,5,6,8,10};
        double ftX0 = 0, ftX1 = 5000000, ftY0 = 0, ftY1 = 10000;
        double clusterTolerance = 2;
        int clusterMinimum = 3;
        double[,] clusters = new double[0, 5];
        // Keep the public raw min/max/mean cluster matrix backward compatible.
        // Only these display guides use projected positions and physical error.
        double[,] locationMedianMarkers = new double[0, 4];
        double reviewLocationClusterTolerancePercent = double.NaN;
        SpectrumView clusterView;
        double cachedClusterTolerance;
        int cachedClusterMinimum;
        [Category("Appearance"), DefaultValue(0), Description("0=pC, 1=nC, 2=uC. Display conversion only; matrix stays pC.")]
        public int ChargeUnitIndex
        {
            get { return chargeUnit; }
            set { if(value<0||value>2)throw new ArgumentOutOfRangeException("ChargeUnitIndex"); if(chargeUnit==value)return; chargeUnit=value; foreach(int key in states.Keys.Where(k=>k!=4).ToArray())states.Remove(key); DropDrawingCaches(); Rebuild(mode!=4); }
        }
        [Browsable(false)] public string ChargeUnitLabel { get { return new[]{"pC","nC","µC"}[chargeUnit]; } }
        double ChargeDisplayScale { get { return chargeUnit==0?1:chargeUnit==1?1000:1000000; } }
        [Category("Appearance"), DefaultValue(true)]
        public bool ShowPhaseReference { get { return showPhaseReference; } set { if(showPhaseReference==value)return; showPhaseReference=value; DropDrawingCaches(); LayoutSpectrumToolbar(); Invalidate(); } }
        [Category("Appearance"), DefaultValue(true)]
        public bool ShowLocationClusters { get { return showLocationClusters; } set { if(showLocationClusters==value)return; showLocationClusters=value; DropDrawingCaches(); Invalidate(); } }
        [Category("Appearance"), DefaultValue(2.0)]
        public double LocationClusterToleranceM { get { return clusterTolerance; } set { if(!Finite(value)||value<=0)throw new ArgumentOutOfRangeException("LocationClusterToleranceM");clusterTolerance=value;RefreshLocationClusters();DropDrawingCaches();Invalidate(); } }
        [Category("Appearance"), DefaultValue(3)]
        public int LocationClusterMinimumCount { get { return clusterMinimum; } set { if(value<3)throw new ArgumentOutOfRangeException("LocationClusterMinimumCount");clusterMinimum=value;RefreshLocationClusters();DropDrawingCaches();Invalidate(); } }
        [Browsable(false), Description("Phase, minimum L, maximum L, mean L, count. Display clustering, not a conclusion tier.")]
        public double[,] LocationClusters { get { return (double[,])clusters.Clone(); } }
        [Category("F-T range"), DefaultValue(true), Description("Fit F-T maxima to all valid displayed points, with a zero origin and headroom. Explicit range assignments disable automatic fitting. ResetView refits; display-only changes retain manual zoom.")]
        public bool FrequencyTimeAutoRange
        {
            get { return frequencyTimeAutoRange; }
            set { if(frequencyTimeAutoRange==value)return; frequencyTimeAutoRange=value; states.Remove(4); DropDrawingCaches(); if(mode==4)Rebuild(true); }
        }
        [Category("F-T fixed range")] public double FrequencyMinimumHz { get { return ftX0; } set { SetFrequencyTimeRange(value,ftX1,ftY0,ftY1); } }
        [Category("F-T fixed range")] public double FrequencyMaximumHz { get { return ftX1; } set { SetFrequencyTimeRange(ftX0,value,ftY0,ftY1); } }
        [Category("F-T fixed range")] public double DurationMinimumNs { get { return ftY0; } set { SetFrequencyTimeRange(ftX0,ftX1,value,ftY1); } }
        [Category("F-T fixed range")] public double DurationMaximumNs { get { return ftY1; } set { SetFrequencyTimeRange(ftX0,ftX1,ftY0,value); } }
        public void SetFrequencyTimeRange(double minimumHz,double maximumHz,double minimumNs,double maximumNs)
        {
            if(minimumHz<0||minimumNs<0||!ValidRange(minimumHz,maximumHz)||!ValidRange(minimumNs,maximumNs))throw new ArgumentException("F-T ranges must be finite, nonnegative and increasing.");
            if(!frequencyTimeAutoRange&&ftX0==minimumHz&&ftX1==maximumHz&&ftY0==minimumNs&&ftY1==maximumNs)return;
            frequencyTimeAutoRange=false;
            ftX0=minimumHz;ftX1=maximumHz;ftY0=minimumNs;ftY1=maximumNs;
            states.Remove(4);DropDrawingCaches();if(mode==4)Rebuild(true);
        }
        [Browsable(false)] public double[] CurrentAxisRanges { get { return new[]{State.X0,State.X1,State.Y0,State.Y1}; } }
        void SetDefaultDisplayRange(ViewState state)
        {
            state.X0=view.XMin;state.X1=view.XMax;state.Y0=view.YMin;state.Y1=view.YMax;
            if(mode==4)
            {
                if(frequencyTimeAutoRange)
                {
                    double maximumFrequency=0,maximumDuration=0;
                    // Iterate already validated/filter-selected plot points; never
                    // trim outliers or fit against a previous zoomed viewport.
                    foreach(var point in view.Points)
                    { maximumFrequency=Math.Max(maximumFrequency,point.X); maximumDuration=Math.Max(maximumDuration,point.Y); }
                    state.X0=state.Y0=0;
                    state.X1=FrequencyTimeMaximum(maximumFrequency,1000000);
                    state.Y1=FrequencyTimeMaximum(maximumDuration,1000);
                }
                else {state.X0=ftX0;state.X1=ftX1;state.Y0=ftY0;state.Y1=ftY1;}
            }
            if(view.YKey=="Q"||view.YKey=="N")
            {
                double unit=view.YKey=="Q"?ChargeDisplayScale:1;
                double padding=visibleRows.Length>0?Math.Max(unit,(state.Y1-state.Y0)*.04):0;
                double low=Math.Floor((state.Y0<0?state.Y0-padding:state.Y0)/unit)*unit,high=Math.Ceiling((state.Y1+padding)/unit)*unit;
                if(ValidRange(low,high)){state.Y0=low;state.Y1=high;}
            }
            state.AutoX0=state.X0;state.AutoX1=state.X1;state.AutoY0=state.Y0;state.AutoY1=state.Y1;
            state.HasAutomaticRange=true;state.ReviewRangeDirty=false;
        }
        static double FrequencyTimeMaximum(double maximum,double emptyMaximum)
        {
            if(!Finite(maximum)||maximum<=0)return emptyMaximum;
            double padded=maximum>double.MaxValue/1.12?double.MaxValue:maximum*1.12;
            double decade=Math.Pow(10,Math.Floor(Math.Log10(padded)));
            if(!Finite(decade)||decade<=0)return padded;
            // Prefer readable five-division limits, e.g. 2.5 MHz or 0.5 µs,
            // instead of a limit such as 0.48 µs with 0.096 µs tick spacing.
            foreach(double multiplier in FrequencyTimeNiceMultipliers)
            {
                double candidate=multiplier*decade;
                if(Finite(candidate)&&candidate>=padded)return candidate;
            }
            // At Double.MaxValue there is no larger finite nice candidate;
            // retain the representable endpoint without clipping the data.
            return padded;
        }
        [Browsable(false)] public string EmptyStateText
        {
            get
            {
                if(competitionProfile)
                {
                    if(!dataLoaded||source.GetLength(0)==0)return L("No analysis data loaded","尚未加载分析数据");
                    if(lastError.Length>0||!selfCheckPassed)return L("Data / display error; see LastError","数据或显示异常，请查看 LastError");
                    return visibleRows.Length==0?L("No visible pulses in the current selection","当前筛选无可见脉冲"):"";
                }
                if(!dataLoaded)return L("No data loaded","尚未加载数据");
                if(lastError.Length>0||!selfCheckPassed)return L("Data / display error; see LastError","数据或显示异常，请查看 LastError");
                if(!visible.Any(v=>v)||phaseFilter>=1&&!visible[phaseFilter-1])return L("All selected phases are hidden","相别已关闭");
                if(visibleRows.Length>0)return "";
                if(source.GetLength(0)==0)return L("No analysis data loaded","尚未加载分析数据");
                EnsureSummaryStatistics();
                if(LocationCoordinatesRequired&&!capacitorStatisticsMode&&summaryRows.Length>0&&summaryLocationCount==0)
                    return L("Pulses: ","已选脉冲：")+summaryRows.Length.ToString(CultureInfo.InvariantCulture)+
                        L("; valid locations: 0\nMissing location: ","；有效定位：0\n未定位：")+summaryMissingLocationCount.ToString(CultureInfo.InvariantCulture)+
                        L("; outside cable: ","；超出电缆：")+summaryOutOfRangeLocationCount.ToString(CultureInfo.InvariantCulture)+
                        L("\nReview reflection/localization; charge and phase data are retained.","\n请复核反射波及定位；放电量和相位数据仍保留。");
                bool anyCandidate=false,anyAccepted=false;
                for(int r=0;r<source.GetLength(0);r++)
                {
                    double ph=source[r,0];
                    if(deleted.Contains(r)||!IsReviewRowVisible(r)||!IsRegionStatisticsRow(r)||!Finite(ph)||ph!=Math.Truncate(ph)||ph<0||ph>3)continue;
                    if(phaseFilter>=0?ph!=phaseFilter:ph>0&&!visible[(int)ph-1])continue;
                    anyCandidate=true;if(source[r,35]==1)anyAccepted=true;
                }
                // A visibility/acceptance mask is not evidence of physical
                // absence of discharge. The source rows are still retained.
                if(!anyCandidate||acceptedOnly&&!anyAccepted)return L("No visible pulses in the current selection","当前筛选无可见脉冲");
                return L("No valid data for this chart","当前图谱没有有效数据");
            }
        }
        void RefreshLocationClusters()
        {
            if(ReferenceEquals(view,clusterView)&&cachedClusterTolerance==clusterTolerance&&cachedClusterMinimum==clusterMinimum)return;
            clusterView=view;cachedClusterTolerance=clusterTolerance;cachedClusterMinimum=clusterMinimum;
            var result=new List<double[]>();
            if(view!=null&&(mode==0||mode==1))
            {
                foreach(var phase in visibleRows.GroupBy(r=>source[r,0]))
                {
                    var positions=phase.Select(r=>source[r,13]).Where(Finite).OrderBy(x=>x).ToArray();
                    int start=0;
                    while(start<positions.Length)
                    {
                        int end=start+1;while(end<positions.Length&&positions[end]-positions[start]<=clusterTolerance)end++;
                        int count=end-start;
                        if(count>=clusterMinimum){double mean=0;for(int i=start;i<end;i++)mean+=positions[i]/count;result.Add(new[]{phase.Key,positions[start],positions[end-1],mean,(double)count});}
                        start=end;
                    }
                }
            }
            clusters=new double[result.Count,5];for(int r=0;r<result.Count;r++)for(int c=0;c<5;c++)clusters[r,c]=result[r][c];
            RefreshLocationMedianMarkers();
        }
        internal void SetReviewLocationClusterTolerancePercent(double percent)
        {
            if(!Finite(percent)||percent<0)throw new ArgumentOutOfRangeException("percent");
            if(reviewLocationClusterTolerancePercent.Equals(percent))return;
            reviewLocationClusterTolerancePercent=percent;clusterView=null;
            RefreshLocationClusters();DropDrawingCaches();Invalidate();
        }
        void RefreshLocationMedianMarkers()
        {
            var markers=new List<double[]>();
            if(view!=null&&(mode==0||mode==1))
            {
                // Count source rows, not histogram bins. Never mix phases or cables.
                foreach(var group in visibleRows.GroupBy(r=>new{Phase=source[r,0],Length=source[r,5]}))
                {
                    double radius=clusterTolerance;
                    if(Finite(reviewLocationClusterTolerancePercent))
                    {
                        if(!Finite(group.Key.Length)||group.Key.Length<=0)continue;
                        radius=group.Key.Length*(reviewLocationClusterTolerancePercent/100);
                        if(double.IsPositiveInfinity(radius))radius=double.MaxValue;
                    }
                    var positions=group.Select(r=>reviewPositionProjection==null?source[r,13]:reviewPositionProjection(r,source[r,13]))
                        .Where(Finite).OrderBy(x=>x).ToArray();
                    int start=0;
                    while(start<positions.Length)
                    {
                        int end=start+1;
                        // Each group's +/-error intervals share a common point.
                        // A first-point bound prevents unlimited neighbour chains.
                        while(end<positions.Length&&positions[end]/2-positions[start]/2<=radius)end++;
                        int count=end-start;
                        if(count>=clusterMinimum)
                        {
                            int middle=start+count/2;
                            double median=count%2==1?positions[middle]:positions[middle-1]/2+positions[middle]/2;
                            markers.Add(new[]{group.Key.Phase,group.Key.Length,median,(double)count});
                        }
                        start=end;
                    }
                }
            }
            locationMedianMarkers=new double[markers.Count,4];
            for(int r=0;r<markers.Count;r++)for(int c=0;c<4;c++)locationMedianMarkers[r,c]=markers[r][c];
        }
        void DrawLocationClusters(Graphics g,Rectangle a)
        {
            if(!showLocationClusters||mode>1)return;
            using(var pen=new Pen(HVX.PdReview.ReviewTheme.SecondaryInk,1){DashStyle=DashStyle.Dash})
            for(int i=0;i<locationMedianMarkers.GetLength(0);i++)
            {
                double median=locationMedianMarkers[i,2];
                if(median<State.X0||median>State.X1)continue;
                float x=Project(new SpectrumPoint{X=median,Y=0},a).X;
                if(Finite(x))g.DrawLine(pen,x,a.Top,x,a.Bottom);
            }
        }
        bool HasPhaseReference { get { return view!=null&&showPhaseReference&&(view.XKey=="PHI"||view.ZKey=="PHI"); } }
        void DrawPhaseReference(Graphics g,Rectangle a)
        {
            if(CompetitionVoltagePhaseView)
            {
                // These modes put phase on X and measured voltage on Z. Do not
                // draw degrees as kV using the ordinary PHI-Q-L adapter below.
                if(!HasPhaseReference||Camera.PlaneIndex>1)return;
                var reference=new PointF[181];
                for(int i=0;i<reference.Length;i++)
                {
                    double phase=i*2,normal=(1+Math.Sin(phase*Math.PI/180))/2;
                    reference[i]=Project(new SpectrumPoint{X=phase,Y=State.Y0+normal*(State.Y1-State.Y0),Z=view.ZMin},a);
                }
                using(var pen=new Pen(Color.FromArgb(130,HVX.PdReview.ReviewTheme.SecondaryInk),1){DashStyle=DashStyle.Dash})g.DrawLines(pen,reference);
                return;
            }
            if(!HasPhaseReference||view.Is3D&&Camera.PlaneIndex!=0&&!IsPhaseHorizontalPlane)return;
            var points=new PointF[181];
            for(int i=0;i<points.Length;i++)
            {
                double phase=i*2,normal=(1+Math.Sin(phase*Math.PI/180))/2;
                points[i]=IsPhaseHorizontalPlane?new PointF((float)(a.Left+(phase-view.ZMin)/(view.ZMax-view.ZMin)*a.Width),(float)(a.Bottom-normal*a.Height)):
                    view.Is3D?Project(new SpectrumPoint{X=State.X0,Y=State.Y0+normal*(State.Y1-State.Y0),Z=phase},a):new PointF((float)(a.Left+(phase-State.X0)/(State.X1-State.X0)*a.Width),(float)(a.Bottom-normal*a.Height));
            }
            using(var pen=new Pen(Color.FromArgb(130,HVX.PdReview.ReviewTheme.SecondaryInk),1){DashStyle=DashStyle.Dash})g.DrawLines(pen,points);
        }
        void DrawPhaseReferenceLabel(Graphics g,Rectangle a)
        {
            if(!HasPhaseReference||CompetitionVoltagePhaseView&&Camera.PlaneIndex!=1||!CompetitionVoltagePhaseView&&view.Is3D&&Camera.PlaneIndex!=0&&!IsPhaseHorizontalPlane)return;
            using(var ink=new SolidBrush(HVX.PdReview.ReviewTheme.SecondaryInk))
            {
                if(!view.Is3D||IsPhaseHorizontalPlane||CompetitionVoltagePhaseView&&Camera.PlaneIndex==1)
                {
                    g.DrawString("1",Font,ink,a.Right+16,a.Top-6);g.DrawString("0",Font,ink,a.Right+16,a.Top+a.Height/2-6);g.DrawString("−1",Font,ink,a.Right+16,a.Bottom-7);
                }
                // The 3D reference caption is measured as a header row, so it
                // cannot overlap the summary, classification or runtime warnings.
            }
        }
    }
}
