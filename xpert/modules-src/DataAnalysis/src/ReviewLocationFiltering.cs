using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using HVX.PD.LocationFiltering;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool locationFilterEnabled;
        double locationFilterStrength = 40;
        string locationFilterAlgorithmId = "B";
        LocationFilterBOptions locationFilterBOptions = new LocationFilterBOptions();
        LocationFilterBResult locationFilterBCached;
        bool[] locationFilterBComplete;
        double[,] locationFilterBMatrix;
        double locationFilterBCachedStrength;
        int locationFilterBUnknownRows;
        [Browsable(false), DefaultValue("B")]
        public string LocationFilterAlgorithmId
        {
            get{return locationFilterAlgorithmId;}
            set{ValidateLocationFilterAlgorithm(value);value="B";if(value==locationFilterAlgorithmId)return;ManualDisplayChoice();locationFilterAlgorithmId=value;LocationFilterOptionsChanged();}
        }
        [Category("Location filtering B"), DefaultValue(20d)] public double LocationFilterBChargeThresholdPc
        {get{return locationFilterBOptions.ChargeThresholdPc;}set{var options=locationFilterBOptions.Clone();options.ChargeThresholdPc=value;SetLocationFilterBOptions(options);}}
        [Category("Location filtering B")] public double LocationFilterBVoltageLimitKv
        {get{return locationFilterBOptions.VoltageLimitKv;}set{var options=locationFilterBOptions.Clone();options.VoltageLimitKv=value;SetLocationFilterBOptions(options);}}
        [Category("Location filtering B"), DefaultValue(3)] public int LocationFilterBMinimumClusterSize
        {get{return locationFilterBOptions.MinimumClusterSize;}set{var options=locationFilterBOptions.Clone();options.MinimumClusterSize=value;SetLocationFilterBOptions(options);}}
        [Category("Location filtering B"), DefaultValue(0)] public int LocationFilterBFromM
        {get{return locationFilterBOptions.FromM;}set{var options=locationFilterBOptions.Clone();options.FromM=value;SetLocationFilterBOptions(options);}}
        [Category("Location filtering B")] public int? LocationFilterBToM
        {get{return locationFilterBOptions.ToM;}set{var options=locationFilterBOptions.Clone();options.ToM=value;SetLocationFilterBOptions(options);}}
        internal static void ValidateLocationFilterAlgorithm(string value)
        {if(value!="A"&&value!="B")throw new ArgumentException("Location filter algorithm must be A or B.");}
        internal static void ValidateLocationFilterBOptions(LocationFilterBOptions options)
        {LocationClusterStrengthFilterB.ValidateOptions(options);}
        LocationFilterBOptions CaptureLocationFilterBOptions()
        {var options=locationFilterBOptions.Clone();options.Strength=locationFilterStrength;return options;}
        void SetLocationFilterBOptions(LocationFilterBOptions options)
        {ValidateLocationFilterBOptions(options);ManualDisplayChoice();locationFilterBOptions=options.Clone();LocationFilterOptionsChanged();}
        void LocationFilterOptionsChanged()
        {InvalidateLocationScores();if(displayAdvisor!=null)displayAdvisor.Invalidate();displayRecommendation=null;MarkAnalysisDirty();RefreshStatistics();PublishDisplayAdvice();}
        double[,] locationScores;
        bool[] locationScoreCompletenessMask;
        string locationScoreStatus = "{}", locationFilterError = "";
        readonly LocationClusterFilterEngine locationFilter = new LocationClusterFilterEngine();
        const string LocationInputAdapterJson = "{\"sourceSchema\":\"Task17-current\",\"sourceColumn22\":\"AmplitudeRatioIgnored\",\"standaloneDischargeEvidenceAvailable\":false,\"task28Column22Input\":\"NaN\",\"task28MissingEvidenceFallback\":0.5,\"column33UsedAsEvidence\":false}";

        [Category("Location filtering"), DefaultValue(false)]
        public bool LocationFilterEnabled
        {
            get { return locationFilterEnabled; }
            set { ManualDisplayChoice();if(locationFilterEnabled!=value){locationFilterEnabled=value;MarkAnalysisDirty();RefreshStatistics();}PublishDisplayAdvice(); }
        }
        [Category("Location filtering"), DefaultValue(40.0)]
        [Description("Location filter B strength, 0..100: recovered ClusterStrength density multiplier. Setting enables filtering.")]
        public double LocationFilterStrength
        {
            get { return locationFilterStrength; }
            set
            {
                if(!Finite(value)||value<0||value>100)throw new ArgumentOutOfRangeException("LocationFilterStrength");
                ManualDisplayChoice();
                if(locationFilterEnabled&&locationFilterStrength.Equals(value))return;
                if(locationFilterAlgorithmId=="B"&&!locationFilterStrength.Equals(value))displayRecommendation=null;
                locationFilterStrength=value;locationFilterEnabled=true;MarkAnalysisDirty();RefreshStatistics();PublishDisplayAdvice();
            }
        }
        [Browsable(false)] public string LocationFilteringDiagnosticsJson { get; private set; } = "{}";
        [Browsable(false)] public int LocationScoreBuildCount { get; private set; }

        void InvalidateLocationScores() { locationScores=null;locationFilterError="";locationFilterBCached=null;locationFilterBComplete=null;locationFilterBMatrix=null;locationFilterBUnknownRows=0; }
        static double[,] CreateTask28Input(double[,] source)
        {
            LocationClusterFilterEngine.ValidateInputMatrix(source);
            // Current Task17 column 22 is an amplitude ratio, not the historical
            // discharge-evidence fraction expected by Task28's input contract.
            // There is no equivalent standalone evidence column. NaN requests
            // Task28's existing neutral 0.5 missing-evidence fallback. In
            // particular, confidence column 33 must not be substituted here.
            // This private scoring copy never becomes the review/output matrix.
            var adapted=(double[,])source.Clone();
            if(adapted.GetLength(1)>22)
                for(int r=0;r<adapted.GetLength(0);r++)adapted[r,22]=double.NaN;
            return adapted;
        }
        void ApplyLocationFilter(bool[] mask,bool[] completeMask)
        {
            if(locationFilterAlgorithmId=="B")
            {
                var options=CaptureLocationFilterBOptions();locationFilterError="";locationFilterBUnknownRows=0;
                if(!locationFilterEnabled||capacitorProject){LocationFilteringDiagnosticsJson=new JavaScriptSerializer().Serialize(new{enabled=false,algorithmId="B",source="Recovered-ClusterStrength",version=LocationClusterStrengthFilterB.Version,parameters=options});return;}
                try
                {
                    if(locationFilterBCached==null||!Object.ReferenceEquals(locationFilterBMatrix,working)||locationFilterBComplete==null||!locationFilterBComplete.SequenceEqual(completeMask)||locationFilterBCachedStrength!=options.Strength){
                        locationFilterBCached=LocationClusterStrengthFilterB.Evaluate(working,completeMask,options);
                        locationFilterBMatrix=working;locationFilterBComplete=(bool[])completeMask.Clone();locationFilterBCachedStrength=options.Strength;
                    }
                    var result=locationFilterBCached;locationFilterBUnknownRows=result.UnknownRows;
                    for(int r=0;r<mask.Length;r++)mask[r]&=result.KeepMask[r];
                    LocationFilteringDiagnosticsJson=result.DiagnosticsJson;
                }
                catch(ArgumentException error)
                {
                    locationFilterError=error.Message;Array.Clear(mask,0,mask.Length);
                    LocationFilteringDiagnosticsJson=new JavaScriptSerializer().Serialize(new{enabled=true,algorithmId="B",source="Recovered-ClusterStrength",version=LocationClusterStrengthFilterB.Version,parameters=options,error=error.Message});
                }
                return;
            }
            if(!locationFilterEnabled||capacitorProject)
            { LocationFilteringDiagnosticsJson="{\"enabled\":false,\"source\":\"Task28-v1.2.1.0\",\"inputSchemaAdapter\":"+LocationInputAdapterJson+"}";return; }
            if(locationScores==null||locationScoreCompletenessMask==null||!locationScoreCompletenessMask.SequenceEqual(completeMask))
            {
                try
                {
                    double[,] retained,gated,summary;int[] indices;
                    // Task 28 v1.2.1.0. Threshold zero computes all
                    // eligible scores once; slider changes only apply its exact
                    // keep predicate. Never write the gated col35 back to input.
                    var completeRows=Enumerable.Range(0,working.GetLength(0)).Where(r=>completeMask[r]).ToArray();
                    LocationClusterFilterEngine.ValidateInputDimensions(completeRows.Length,working.GetLength(1));
                    var eligible=new double[completeRows.Length,working.GetLength(1)];
                    for(int i=0;i<completeRows.Length;i++)for(int c=0;c<eligible.GetLength(1);c++)eligible[i,c]=working[completeRows[i],c];
                    double[,] localScores;
                    locationFilter.FilterLocationMatrix(CreateTask28Input(eligible),0,out retained,out gated,out indices,
                        out localScores,out summary,out locationScoreStatus);
                    locationScores=new double[mask.Length,8];
                    for(int r=0;r<mask.Length;r++)locationScores[r,0]=r;
                    for(int i=0;i<completeRows.Length;i++)
                    {
                        int source=completeRows[i];
                        for(int c=1;c<8;c++)locationScores[source,c]=localScores[i,c];
                    }
                    locationScoreCompletenessMask=(bool[])completeMask.Clone();
                    locationFilterError="";LocationScoreBuildCount++;
                }
                catch(ArgumentException error)
                {
                    locationFilterError=error.Message;
                    locationScores=new double[mask.Length,8];locationScoreStatus="{}";
                    locationScoreCompletenessMask=(bool[])completeMask.Clone();
                }
            }
            int retainedCount=0,visibleCount=0;
            for(int r=0;r<mask.Length;r++)
            {
                bool keep=locationScores[r,1]==1&&(locationFilterStrength==0||locationScores[r,6]==2||locationScores[r,5]>=locationFilterStrength);
                if(keep)retainedCount++;
                mask[r]&=keep;if(mask[r])visibleCount++;
            }
            LocationFilteringDiagnosticsJson="{\"enabled\":true,\"source\":\"Task28-v1.2.1.0\",\"strength\":"+
                locationFilterStrength.ToString("R",CultureInfo.InvariantCulture)+",\"retainedByLocation\":"+retainedCount+
                ",\"inputSchemaAdapter\":"+LocationInputAdapterJson+
                ",\"retainedAfterTask7\":"+visibleCount+",\"error\":"+new JavaScriptSerializer().Serialize(locationFilterError)+
                ",\"scoreBuildAtStrengthZero\":"+locationScoreStatus+"}";
        }
    }
}
