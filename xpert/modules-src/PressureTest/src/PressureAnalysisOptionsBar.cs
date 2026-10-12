using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using HVX.PdReview;

namespace HVX.Controls
{
    internal sealed partial class PressureSpectrumDashboardView
    {
        FlowLayoutPanel analysisOptions;
        CheckBox manualThreshold,thresholdAll,compensatedCharge,snapJoint,reversePosition,endpointReview;
        NumericUpDown thresholdPc;
        PressureSpectrumGeometry analysisGeometry;
        internal event EventHandler EndpointReviewRequested;
        internal event EventHandler AnalysisThresholdChanged;
        internal bool ManualAnalysisThreshold {get{return manualThreshold.Checked;}}
        internal double AnalysisThresholdPc {get{return (double)thresholdPc.Value;}}
        internal bool AnalysisThresholdAll {get{return thresholdAll.Checked;}}
        internal void InitializeAnalysisOptions()
        {
            analysisOptions=new FlowLayoutPanel{Name="PressureAnalysisOptionsBar",WrapContents=true,
                Padding=new Padding(4),BackColor=Color.White,Font=compactFont};
            manualThreshold=new CheckBox{Name="PressureManualThreshold",AutoSize=true,Margin=new Padding(3,6,3,3)};
            thresholdPc=new NumericUpDown{Name="PressureThresholdPc",Width=100,DecimalPlaces=2,Minimum=0,Maximum=1000000000,Value=100,Enabled=false};
            thresholdAll=new CheckBox{Name="PressureThresholdAll",AutoSize=true,Margin=new Padding(8,6,3,3)};
            compensatedCharge=new CheckBox{Name="PressureCompensatedCharge",AutoSize=true,Margin=new Padding(8,6,3,3)};
            snapJoint=new CheckBox{Name="PressureSnapJoint",Checked=true,AutoSize=true,Margin=new Padding(8,6,3,3)};
            reversePosition=new CheckBox{Name="PressureReversePosition",AutoSize=true,Margin=new Padding(8,6,3,3)};
            endpointReview=new CheckBox{Name="PressureEndpointReview",AutoSize=true,Enabled=false,Margin=new Padding(8,6,3,3)};
            analysisOptions.Controls.AddRange(new Control[]{manualThreshold,thresholdPc,new Label{Text="pC",AutoSize=true,Margin=new Padding(0,6,3,3)},thresholdAll,snapJoint,reversePosition,compensatedCharge,endpointReview});
            manualThreshold.CheckedChanged+=(s,e)=>{thresholdPc.Enabled=manualThreshold.Checked;RaiseAnalysisThresholdChanged();};
            thresholdPc.ValueChanged+=(s,e)=>RaiseAnalysisThresholdChanged();
            compensatedCharge.CheckedChanged+=(s,e)=>Review.UseCompensatedCharge=compensatedCharge.Checked;
            snapJoint.CheckedChanged+=(s,e)=>RefreshPressureGeometry();reversePosition.CheckedChanged+=(s,e)=>RefreshPressureGeometry();
            endpointReview.CheckedChanged+=(s,e)=>{if(endpointReview.Checked){var handler=EndpointReviewRequested;if(handler!=null)handler(this,EventArgs.Empty);else endpointReview.Checked=false;}};
            // The dashboard allocates the complete measured strip, chart and
            // filter together, with local vertical scrolling when necessary.
            spectrumContent.Controls.Add(analysisOptions);
            analysisOptions.FontChanged+=(s,e)=>ArrangeSpectrumContent();
        }
        internal void SetAnalysisOptionsLocked(bool locked){analysisOptions.Enabled=!locked;}
        void RaiseAnalysisThresholdChanged(){var handler=AnalysisThresholdChanged;if(handler!=null)handler(this,EventArgs.Empty);}
        internal void SetEndpointReviewAvailable(bool available){endpointReview.Enabled=available;if(!available)endpointReview.Checked=false;}
        internal void ResetEndpointReview(){endpointReview.Checked=false;}
        internal void SetAnalysisThreshold(bool manual,double value,bool all)
        {
            if(Double.IsNaN(value)||Double.IsInfinity(value)||value<0||value>1e9)throw new ArgumentOutOfRangeException("value");
            manualThreshold.Checked=manual;thresholdPc.Value=(decimal)value;thresholdAll.Checked=all;
        }
        internal void SetAnalysisGeometry(PressureSpectrumGeometry geometry){analysisGeometry=geometry;RefreshPressureGeometry();}
        void RefreshPressureGeometry(){if(analysisGeometry!=null)analysisGeometry.Apply(Review,snapJoint.Checked,reversePosition.Checked);else PressureSpectrumGeometry.Clear(Review);}
        internal void ResetAnalysisOptions(){analysisGeometry=null;SetEndpointReviewAvailable(false);SetAnalysisThreshold(false,100,false);compensatedCharge.Checked=false;snapJoint.Checked=true;reversePosition.Checked=false;}
        void RefreshAnalysisOptionsLanguage()
        {
            if(analysisOptions==null)return;
            manualThreshold.Text=AnalysisOptionText("手动阈值（未勾选：自动）","Manual threshold (unchecked: automatic)");
            thresholdAll.Text=AnalysisOptionText("应用于所有","Apply to all");
            compensatedCharge.Text=AnalysisOptionText("补偿放电量","Compensated charge");
            snapJoint.Text=AnalysisOptionText("近似接头","Snap to joints");reversePosition.Text=AnalysisOptionText("定位反转","Reverse location");
            endpointReview.Text=AnalysisOptionText("近远端识别","Near/far ends");
            adviceTip.SetToolTip(endpointReview,AnalysisOptionText("只读提取本轮已保存定位数据的 MAP 进行识别；不修改已保存结果，不启动加压。","Read-only extraction and endpoint review of this saved round; no saved results or pressure output are changed."));
            string hint=AnalysisOptionText("阈值用于下一次分析；应用于所有指本次试验已保存的轮次。更改不启动采集或加压。","Threshold applies on the next analysis; all means saved rounds in this trial. Editing never starts acquisition or pressure.");
            adviceTip.SetToolTip(manualThreshold,hint);adviceTip.SetToolTip(thresholdAll,hint);adviceTip.SetToolTip(thresholdPc,hint);
            adviceTip.SetToolTip(compensatedCharge,AnalysisOptionText("仅切换已分析结果中的补偿电量，不修改原始数据。","Displays the analyzed compensated charge without changing raw data."));
            adviceTip.SetToolTip(snapJoint,AnalysisOptionText("按已确认接头及当前容差近似显示定位结果，保留原始位置。","Display locations near confirmed joints using the current tolerance; original positions are retained."));
            adviceTip.SetToolTip(reversePosition,AnalysisOptionText("反转定位显示方向，不改变已保存的原始位置。","Reverse the location display direction without changing saved original positions."));
        }
        string AnalysisOptionText(string chinese,string english)
        {return Xiongxin.OWTS.Localization.UiLanguage.Text(LanguageIndex,chinese,english);}
    }
    public sealed partial class PressureTestControl
    {
        void InitializePressureThresholdGuide()
        {
            waveformView.ManualThresholdGuideCanDrag=()=>!PressureParametersLocked&&!IsBusy;
            waveformView.ManualThresholdGuideDragged+=(s,e)=>
            {
                if(!spectrumDashboardView.ManualAnalysisThreshold||PressureParametersLocked||IsBusy)
                {
                    waveformView.SetManualThresholdGuide(spectrumDashboardView.ManualAnalysisThreshold,spectrumDashboardView.AnalysisThresholdPc);
                    return;
                }
                spectrumDashboardView.SetAnalysisThreshold(true,waveformView.ManualThresholdGuidePc,spectrumDashboardView.AnalysisThresholdAll);
            };
            spectrumDashboardView.AnalysisThresholdChanged+=(s,e)=>
                waveformView.SetManualThresholdGuide(spectrumDashboardView.ManualAnalysisThreshold,spectrumDashboardView.AnalysisThresholdPc);
            waveformView.SetManualThresholdGuide(spectrumDashboardView.ManualAnalysisThreshold,spectrumDashboardView.AnalysisThresholdPc);
        }
        bool endpointInspectionBusy;
        long endpointInspectionSequence;
        public event EventHandler EndpointReviewRequested;
        [Browsable(false)] public long EndpointReviewSequence {get{return endpointInspectionSequence;}}
        [Browsable(false)] public bool EndpointInspectionBusy {get{return endpointInspectionBusy;}}
        public void SetEndpointReviewAvailable(bool value){RunOnUiThread(()=>spectrumDashboardView.SetEndpointReviewAvailable(value));}
        void BeginEndpointReview()
        {
            if(PressureParametersLocked||IsBusy||EndpointReviewRequested==null){spectrumDashboardView.ResetEndpointReview();return;}
            endpointInspectionBusy=true;endpointInspectionSequence++;UpdateActionBusyUi();Raise(EndpointReviewRequested);
        }
        public void CompleteEndpointReview(long sequence)
        {
            RunOnUiThread(()=>{if(!endpointInspectionBusy||sequence!=endpointInspectionSequence)return;
                endpointInspectionBusy=false;spectrumDashboardView.ResetEndpointReview();UpdateActionBusyUi();});
        }
        [Browsable(false)] public bool ManualAnalysisThresholdEnabled {get{return spectrumDashboardView.ManualAnalysisThreshold;}}
        [Browsable(false)] public double ManualAnalysisThresholdPc {get{return spectrumDashboardView.AnalysisThresholdPc;}}
        [Browsable(false)] public bool ApplyAnalysisThresholdToAllRounds {get{return spectrumDashboardView.AnalysisThresholdAll;}}
        public void SetPressureAnalysisThreshold(bool manual,double value,bool all)
        {
            RunOnUiThread(()=>{
                if(PressureParametersLocked||IsBusy)throw new InvalidOperationException(PressureUiText("本轮尚未结束，不能修改分析阈值。","This round is not complete; the analysis threshold cannot be changed."));
                spectrumDashboardView.SetAnalysisThreshold(manual,value,all);
            });
        }
        public void SetPressureSpectrumGeometry(PressureSpectrumGeometry geometry)
        {RunOnUiThread(()=>spectrumDashboardView.SetAnalysisGeometry(geometry));}
    }
}
