using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        bool automaticDisplay=true,applyingDisplayRecommendation;
        CheckBox autoDisplayBox;Label displayRecommendationLabel;ReviewRecommendationWorker displayAdvisor;
        LinkLabel displayAdviceDetails;
        ReviewDisplayRecommendation displayRecommendation;
        [DefaultValue(true)]public bool AutomaticDisplayRecommendation
        {get{return automaticDisplay;}set{automaticDisplay=value;if(displayAdvisor!=null){displayAdvisor.Invalidate();if(value)displayAdvisor.Request();}RefreshDisplayAdviceUi();}}
        [Browsable(false)]public string DisplayRecommendationJson
        {get{return new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=24}.Serialize(new{Automatic=automaticDisplay,AppliedFilter=locationFilterEnabled,AppliedClassification=spectrum.ConfidenceClassificationEnabled,Advice=displayRecommendation});}}
        public event EventHandler DisplayRecommendationChanged;
        void InitializeDisplayAdvisor()
        {
            autoDisplayBox=new CheckBox{Name="ReviewAutoDisplay",Text=L("Auto recommendation","自动推荐"),Checked=true,Width=156,Height=25};
            displayRecommendationLabel=new Label{Name="ReviewDisplayAdvice",Visible=false,Width=156,Height=58,Text=L("Waiting for analysis data","等待分析数据")};
            displayRecommendationLabel.FontChanged+=(s,e)=>FitDisplayAdviceLabel();
            displayRecommendationLabel.SizeChanged+=(s,e)=>FitDisplayAdviceLabel();
            displayAdviceDetails=new LinkLabel{Name="ReviewDisplayAdviceDetails",Visible=false,TabStop=false,Width=156,Height=24,Text=L("Why this suggestion?","查看推荐依据")};
            displayAdviceDetails.LinkClicked+=(s,e)=>MessageBox.Show(FindForm(),displayRecommendationLabel.AccessibleDescription,L("Display suggestions","显示建议"),MessageBoxButtons.OK,MessageBoxIcon.Information);
            side.Controls.Add(autoDisplayBox);side.Controls.Add(displayRecommendationLabel);side.Controls.Add(displayAdviceDetails);
            autoDisplayBox.CheckedChanged+=(s,e)=>{if(!applyingDisplayRecommendation){AutomaticDisplayRecommendation=autoDisplayBox.Checked;PublishDisplayAdvice();}};
            spectrum.ClassificationManuallyChanged+=(s,e)=>ManualDisplayChoice();
            displayAdvisor=new ReviewRecommendationWorker(this,()=>{
                var matrix=(double[,])working.Clone();var complete=BuildWaveformCompletenessMask();double strength=locationFilterStrength,low=spectrum.ClassificationLowThresholdPercent,high=spectrum.ClassificationHighThresholdPercent;
                string algorithmId=locationFilterAlgorithmId;var filterB=CaptureLocationFilterBOptions();
                bool capacitor=capacitorProject;
                return ()=>{
                    if(capacitor)return new ReviewDisplayRecommendation{AlgorithmId=algorithmId,ReasonCode="Unavailable"};
                    try{return ReviewDisplayRecommendation.Evaluate(matrix,complete,strength,low,high,algorithmId,filterB);}
                    catch(ArgumentException error){return new ReviewDisplayRecommendation{AlgorithmId=algorithmId,ReasonCode="Unavailable",Error=error.Message};}
                };
            },advice=>{
                if(advice.AlgorithmId!=locationFilterAlgorithmId)return;
                if(locationFilterAlgorithmId=="B"&&!String.IsNullOrEmpty(advice.Error)){
                    displayRecommendation=advice;locationFilterError=advice.Error;
                    LocationFilteringDiagnosticsJson=new JavaScriptSerializer().Serialize(new{algorithmId="B",enabled=locationFilterEnabled,source="Recovered-ClusterStrength",version=HVX.PD.LocationFiltering.LocationClusterStrengthFilterB.Version,parameters=CaptureLocationFilterBOptions(),error=advice.Error});
                    RefreshDisplayAdviceUi();RefreshReviewUi();PublishDisplayAdvice();return;
                }
                displayRecommendation=advice;applyingDisplayRecommendation=true;
                try{if(automaticDisplay){
                    // Reuse the background result, not a second UI-thread score pass.
                    locationScores=locationFilterAlgorithmId=="A"?advice.Scores:null;locationScoreCompletenessMask=advice.CompleteMask;locationFilterError="";
                    if(locationFilterAlgorithmId=="B"){
                        locationFilterBCached=advice.FilterBResult;locationFilterBComplete=advice.CompleteMask;locationFilterBMatrix=working;locationFilterBCachedStrength=advice.Strength;
                    }
                    locationFilterEnabled=advice.Filter;spectrum.ConfidenceClassificationEnabled=advice.Classification;RefreshStatistics(true);
                }RefreshDisplayAdviceUi();}
                finally{applyingDisplayRecommendation=false;}
                PublishDisplayAdvice();
            });
        }
        void ManualDisplayChoice()
        {
            if(applyingDisplayRecommendation)return;
            automaticDisplay=false;if(displayAdvisor!=null)displayAdvisor.Invalidate();RefreshDisplayAdviceUi();PublishDisplayAdvice();
        }
        void PublishDisplayAdvice(){var handler=DisplayRecommendationChanged;if(handler!=null)handler(this,EventArgs.Empty);}
        void RefreshDisplayAdviceUi()
        {
            if(autoDisplayBox==null)return;bool prior=applyingDisplayRecommendation;applyingDisplayRecommendation=true;
            try{autoDisplayBox.Checked=automaticDisplay;autoDisplayBox.Text=L("Auto recommendation","自动推荐");
                displayRecommendationLabel.Visible=displayAdviceDetails.Visible=false;
                displayRecommendationLabel.Text=displayRecommendation==null?L("Waiting for analysis data","等待分析数据"):
                    (automaticDisplay?L("Automatically set","已自动设置"):L("Manual selection","当前为手动选择"))+Environment.NewLine+
                    L("Statistics filter: ","统计过滤：")+(locationFilterEnabled?L("On","开启"):L("Off","关闭"))+Environment.NewLine+
                    L("Classification: ","分类管理：")+(spectrum.ConfidenceClassificationEnabled?L("On","开启"):L("Off","关闭"));
                displayRecommendationLabel.AccessibleDescription=displayRecommendation==null?displayRecommendationLabel.Text:
                    displayRecommendationLabel.Text+Environment.NewLine+Environment.NewLine+(languageIndex==18?
                    ReviewTraditionalChinese.Text(displayRecommendation.Describe(true)):displayRecommendation.Describe(!english));
                displayAdviceDetails.Enabled=false;
                displayAdviceDetails.Text=L("Why this suggestion?","查看推荐依据");
                // LinkLabel can re-enable tab navigation when its links/text change.
                displayAdviceDetails.TabStop=false;
                FitDisplayAdviceLabel();
            }finally{applyingDisplayRecommendation=prior;}
        }
        void FitDisplayAdviceLabel()
        {
            if(displayRecommendationLabel==null||displayRecommendationLabel.Width<1)return;
            int height=TextRenderer.MeasureText(displayRecommendationLabel.Text,displayRecommendationLabel.Font,
                new Size(displayRecommendationLabel.Width,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height+4;
            if(displayRecommendationLabel.Height!=height)displayRecommendationLabel.Height=height;
        }
        void QueueDisplayAdvice(){if(displayAdvisor!=null){displayRecommendation=null;RefreshDisplayAdviceUi();displayAdvisor.Request();}}
    }
}
