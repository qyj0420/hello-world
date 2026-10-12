using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using HVX.PD.LocationFiltering;
using HVX.PdReview;

namespace HVX.Controls
{
    internal sealed partial class PressureSpectrumDashboardView
    {
        Label locationFilterNotice;
        string locationFilterAlgorithmId="B", locationFilteringDiagnostics="{}";
        LocationFilterBOptions locationFilterBOptions=new LocationFilterBOptions();

        void InitializeLocationFilterChoice()
        {
            // B is the sole filter; old A settings migrate through the API.
            locationFilterNotice=new Label{Name="PressureLocationFilterNotice",Font=compactFont,AutoEllipsis=true,Visible=false};
            filterBar.Controls.Add(locationFilterNotice);
            RefreshLocationFilterChoice();
        }
        void RefreshLocationFilterChoice()
        {
            // Compatibility no-op: there is no independent filter selector.
        }
        internal string LocationFilterAlgorithmId
        {
            get{return locationFilterAlgorithmId;}
            set{
                if(value!="A"&&value!="B")throw new ArgumentOutOfRangeException("value","Expected location filter A or B.");
                value="B";
                if(locationFilterAlgorithmId==value)return;
                ManualDisplayChoice();advice=null;locationFilterAlgorithmId=value;
                RefreshLocationFilterChoice();ApplyStatisticsFilter();RefreshAdvice();Notify("STATISTICS_FILTER");
            }
        }
        internal LocationFilterBOptions GetLocationFilterBOptions()
        {var options=locationFilterBOptions.Clone();options.Strength=strengthSlider.Value;return options;}
        internal void SetLocationFilterBOptions(LocationFilterBOptions value)
        {
            if(value==null)throw new ArgumentNullException("value");
            var copy=value.Clone();LocationClusterStrengthFilterB.ValidateOptions(copy);
            // The visible trackbar has integer steps. Do not silently truncate a caller's setting.
            if(copy.Strength!=Math.Round(copy.Strength))throw new ArgumentOutOfRangeException("value","The display strength must be an integer.");
            ManualDisplayChoice();advice=null;locationFilterBOptions=copy;
            bool previous=statisticsSync;statisticsSync=true;
            try{strengthSlider.Value=(int)copy.Strength;}finally{statisticsSync=previous;}
            ApplyStatisticsFilter();RefreshAdvice();Notify("STATISTICS_FILTER");
        }
        internal string LocationFilteringDiagnosticsJson {get{return locationFilteringDiagnostics;}}
        void UpdateLocationFilterNotice(int unknown,string error)
        {
            if(locationFilterNotice==null)return;
            locationFilterNotice.Text=!String.IsNullOrEmpty(error)?AnalysisOptionText("定位过滤未完成：","Location filter failed: ")+error:
                unknown>0?AnalysisOptionText("边界待复核，已保留：","Boundary rows retained for review: ")+unknown:String.Empty;
            locationFilterNotice.Visible=locationFilterNotice.Text.Length>0;
            locationFilterNotice.AccessibleDescription=locationFilterNotice.Text;
            if(adviceTip!=null)adviceTip.SetToolTip(locationFilterNotice,locationFilterNotice.Text);
            LayoutStatisticsFilter();ArrangeSpectrumContent();
        }
        bool[] EvaluateLocationFilter()
        {
            try{
                var result=ReviewDisplayRecommendation.Evaluate(sourceMatrix,Enumerable.Repeat(true,sourceMatrix.GetLength(0)).ToArray(),
                    strengthSlider.Value,Review.ClassificationLowThresholdPercent,Review.ClassificationHighThresholdPercent,
                    locationFilterAlgorithmId,GetLocationFilterBOptions());
                locationFilteringDiagnostics=result.DiagnosticsJson??"{}";
                UpdateLocationFilterNotice(result.UnknownRows,result.Error);
                return result.GetCandidateMask();
            }catch(ArgumentException error){
                locationFilteringDiagnostics=new JavaScriptSerializer().Serialize(new{AlgorithmId=locationFilterAlgorithmId,Error=error.Message});
                advice=new ReviewDisplayRecommendation{AlgorithmId=locationFilterAlgorithmId,ReasonCode="Unavailable",Error=error.Message};
                RefreshAdvice();
                UpdateLocationFilterNotice(0,error.Message);
                return new bool[sourceMatrix.GetLength(0)];
            }
        }
    }

    public sealed partial class PressureTestControl
    {
        [Browsable(false),DefaultValue("B")]
        public string StatisticsLocationFilterAlgorithmId
        {get{return spectrumDashboardView.LocationFilterAlgorithmId;}set{if(value!="A"&&value!="B")throw new ArgumentOutOfRangeException("value");RunOnUiThread(()=>spectrumDashboardView.LocationFilterAlgorithmId=value);}}
        public LocationFilterBOptions GetStatisticsLocationFilterBOptions()
        {LocationFilterBOptions value=null;RunOnUiThread(()=>value=spectrumDashboardView.GetLocationFilterBOptions());return value;}
        public void SetStatisticsLocationFilterBOptions(LocationFilterBOptions value)
        {
            if(value==null)throw new ArgumentNullException("value");
            var copy=value.Clone();LocationClusterStrengthFilterB.ValidateOptions(copy);
            if(copy.Strength!=Math.Round(copy.Strength))throw new ArgumentOutOfRangeException("value","The display strength must be an integer.");
            RunOnUiThread(()=>spectrumDashboardView.SetLocationFilterBOptions(copy));
        }
        [Browsable(false)]public string StatisticsLocationFilteringDiagnosticsJson {get{return spectrumDashboardView.LocationFilteringDiagnosticsJson;}}
    }
}
