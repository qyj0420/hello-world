using System;
using System.Drawing;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        CheckBox[] filterChoices;
        Panel filterParametersHost;
        FlowLayoutPanel bandParameters,waveletParameters;
        TrackBar highPassSlider,lowPassSlider,levelsSlider,ignoreSlider;
        int filterModeIndex,filterUiSequence=-1;
        int SelectedFilterMode { get { return filterModeIndex; } }

        void InitializeFilterControls()
        {
            var choices=new FlowLayoutPanel { Width=156,Height=30,WrapContents=false,Margin=new Padding(0) };
            filterChoices=new CheckBox[3];
            for(int i=0;i<3;i++)
            {
                int index=i;
                var choice=new RoundedReviewToggle { Name="ReviewFilterMode"+i,Appearance=Appearance.Button,Width=i==2?54:48,Height=27,
                    Margin=new Padding(1),TextAlign=ContentAlignment.MiddleCenter,FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false };
                choice.FlatAppearance.BorderColor=ReviewTheme.Accent;
                choice.CheckedChanged+=(s,e)=>{if(uiSync)return;if(choice.Checked){SetFilterSelection(index);ApplyFilterFromUi();}else SetFilterSelection(filterModeIndex);};
                filterChoices[i]=choice;choices.Controls.Add(choice);
            }
            side.Controls.Add(choices);
            filterParametersHost=new Panel { Name="ReviewFilterParameters",Width=156,Height=0,Margin=new Padding(0) };
            bandParameters=FilterPage("ReviewBandParameters");waveletParameters=FilterPage("ReviewWaveletParameters");
            filterParametersHost.Controls.Add(bandParameters);filterParametersHost.Controls.Add(waveletParameters);side.Controls.Add(filterParametersHost);
            highPass=Number(10,3000,50,10);highPass.Name="ReviewHighPassValue";
            lowPass=Number(1,60,7,.1M);lowPass.DecimalPlaces=3;lowPass.Name="ReviewLowPassValue";
            levels=Number(1,20,5,1);levels.Name="ReviewLevelsValue";ignore=Number(0,19,2,1);ignore.Name="ReviewIgnoreValue";
            highPassSlider=FilterSlider("ReviewHighPassSlider",0,1000);
            lowPassSlider=FilterSlider("ReviewLowPassSlider",0,1000);
            levelsSlider=FilterSlider("ReviewLevelsSlider",1,20);
            ignoreSlider=FilterSlider("ReviewIgnoreSlider",0,19);
            highLabel=AddSliderField(bandParameters,highPass,highPassSlider);
            lowLabel=AddSliderField(bandParameters,lowPass,lowPassSlider);
            wavelet=new ReviewComboInput { Name="ReviewWavelet",DropDownStyle=ComboBoxStyle.DropDownList,Width=68 };
            wavelet.Items.AddRange(new object[]{"db2","db4","db8"});wavelet.SelectedIndex=1;
            waveLabel=AddLabeled(wavelet,waveletParameters);
            levelsLabel=AddSliderField(waveletParameters,levels,levelsSlider);
            ignoreLabel=AddSliderField(waveletParameters,ignore,ignoreSlider);
            highPass.ValueChanged+=(s,e)=>ApplyFilterFromUi(1);lowPass.ValueChanged+=(s,e)=>ApplyFilterFromUi(2);
            levels.ValueChanged+=(s,e)=>ApplyFilterFromUi();ignore.ValueChanged+=(s,e)=>ApplyFilterFromUi();
            wavelet.SelectedIndexChanged+=(s,e)=>ApplyFilterFromUi();
            BindSlider(highPassSlider,highPass,true);BindSlider(lowPassSlider,lowPass,true);
            BindSlider(levelsSlider,levels,false);BindSlider(ignoreSlider,ignore,false);
            // The waveform's own filtered-curve checkbox also changes the mode.
            // Observe only changed filter state; cursor paint invalidations do no work.
            pdmap.Invalidated+=(s,e)=>{if(layoutReady&&!uiSync&&(filterUiSequence!=pdmap.FilterRefreshSequence||filterModeIndex!=WaveformFilterMode)){SyncFilterUi();RefreshReviewUi();}};
        }
        static FlowLayoutPanel FilterPage(string name)
        { return new FlowLayoutPanel { Name=name,Location=Point.Empty,Width=156,Height=136,Margin=new Padding(0),FlowDirection=FlowDirection.TopDown,WrapContents=false }; }
        static TrackBar FilterSlider(string name,int minimum,int maximum)
        { return new TrackBar { Name=name,Minimum=minimum,Maximum=maximum,AutoSize=false,Width=156,Height=27,TickStyle=TickStyle.None,SmallChange=1,LargeChange=Math.Max(1,(maximum-minimum)/20),Margin=new Padding(0) }; }
        Label AddSliderField(FlowLayoutPanel parent,NumericUpDown value,TrackBar slider)
        {
            int inputHeight=Math.Max(27,value.PreferredSize.Height+3);
            var row=new Panel { Width=156,Height=inputHeight+slider.Height+3,Margin=new Padding(0,1,0,1) };
            var label=new Label { Location=new Point(0,0),Width=88,Height=inputHeight-2,TextAlign=ContentAlignment.MiddleLeft };
            value.Width=68;value.Location=new Point(88,1);slider.Location=new Point(0,inputHeight);
            row.Controls.Add(label);row.Controls.Add(value);row.Controls.Add(slider);parent.Controls.Add(row);return label;
        }
        void BindSlider(TrackBar slider,NumericUpDown value,bool logarithmic)
        {
            slider.ValueChanged+=(s,e)=>
            {
                if(uiSync)return;
                double next=logarithmic?(double)value.Minimum*Math.Pow((double)(value.Maximum/value.Minimum),slider.Value/1000.0):slider.Value;
                uiSync=true;
                try { value.Value=Math.Max(value.Minimum,Math.Min(value.Maximum,(decimal)Math.Round(next,value.DecimalPlaces))); }
                finally { uiSync=false; }
                ScheduleReviewFilterDrag(slider,logarithmic?(value==highPass?1:2):0);
            };
            slider.MouseUp+=(s,e)=>FlushReviewFilterDrag();
            slider.MouseCaptureChanged+=(s,e)=>{if(!slider.Capture)FlushReviewFilterDrag();};
        }
        void SetFilterSelection(int mode)
        {
            bool prior=uiSync;uiSync=true;filterModeIndex=mode;
            try { for(int i=0;i<3;i++){filterChoices[i].Checked=i==mode;filterChoices[i].BackColor=i==mode?ReviewTheme.Active:ReviewTheme.Surface;filterChoices[i].ForeColor=i==mode?Color.White:ReviewTheme.Ink;filterChoices[i].FlatAppearance.BorderColor=i==mode?ReviewTheme.Active:ReviewTheme.Border;} }
            finally { uiSync=prior; }
        }
        void UpdateFilterLanguage()
        {
            string[] labels=L(new[]{"Off","Band","Wavelet"},new[]{"不滤波","带通","小波"});
            if(languageIndex==17)labels[1]="Dải";
            string[] names=L(new[]{"No filtering","Band-pass filter","Wavelet filter"},new[]{"不滤波","带通滤波","小波滤波"});
            for(int i=0;i<3;i++){filterChoices[i].Width=english?(i==2?66:50):(i==0?62:44);filterChoices[i].Text=labels[i];filterChoices[i].AccessibleName=names[i];}
        }
        void SyncFilterSliders()
        {
            highPassSlider.Value=LogSliderValue(highPass);lowPassSlider.Value=LogSliderValue(lowPass);
            levelsSlider.Value=(int)levels.Value;ignoreSlider.Value=(int)ignore.Value;
            filterUiSequence=pdmap.FilterRefreshSequence;
        }
        static int LogSliderValue(NumericUpDown value)
        { return value.Maximum==value.Minimum?0:Math.Max(0,Math.Min(1000,(int)Math.Round(1000*Math.Log10((double)(value.Value/value.Minimum))/Math.Log10((double)(value.Maximum/value.Minimum))))); }
        void UpdateFilterEnabled()
        {
            int mode=SelectedFilterMode;
            side.SuspendLayout();filterParametersHost.SuspendLayout();
            try
            {
                bandParameters.Visible=mode==1;waveletParameters.Visible=mode==2;
                bandParameters.Height=FlowContentHeight(bandParameters);waveletParameters.Height=FlowContentHeight(waveletParameters);
                filterParametersHost.Visible=mode!=0;filterParametersHost.Height=mode==1?bandParameters.Height:mode==2?waveletParameters.Height:0;
                highPass.Enabled=lowPass.Enabled=highPassSlider.Enabled=lowPassSlider.Enabled=mode==1;
                wavelet.Enabled=levels.Enabled=ignore.Enabled=levelsSlider.Enabled=ignoreSlider.Enabled=mode==2;
            }
            finally {filterParametersHost.ResumeLayout();side.ResumeLayout();}
        }
    }
}
