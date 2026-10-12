using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;
using Xiongxin.OWTS.Noise;
using HVX.PdReview;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    // Reuse the exact released review implementation, including region statistics,
    // classification, export, camera and all eleven visible spectrum tabs.
    internal sealed partial class PressureSpectrumDashboardView : UserControl
    {
        internal readonly UnifiedSpectrumControl Review;
        private bool applying;
        private bool busy;
        private bool pendingSpectrumView;
        private readonly SelectionProbe probe;
        internal event Action<string> UserAction;
        internal event EventHandler DisplayRecommendationChanged;
        private readonly Font compactFont;
        private readonly Panel filterBar;
        private readonly CheckBox statisticsCheck;
        private readonly Label strengthLabel, strengthValue;
        private readonly TrackBar strengthSlider;
        private double[,] sourceMatrix = new double[0,36];
        private int[] retainedRows = new int[0];
        private bool statisticsSync;
        private readonly CheckBox automaticCheck;
        private readonly Label adviceLabel;
        private readonly ToolTip adviceTip;
        private readonly ReviewRecommendationWorker advisor;
        private ReviewDisplayRecommendation advice;
        private bool automaticDisplay=true;
        private readonly NoiseReferencePanel noiseReferencePanel;
        private readonly Label noiseOnlyMessage;
        private readonly Label zeroAnalysisNotice;
        private readonly Panel spectrumViewport, spectrumContent;
        private bool arrangingSpectrumContent;
        private bool zeroAnalysis;
        internal PressureSpectrumDashboardView()
        {
            compactFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            spectrumViewport=new Panel{Name="PressureSpectrumViewport",Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.White};
            spectrumContent=new Panel{Name="PressureSpectrumContent",Margin=Padding.Empty,BackColor=Color.White};
            spectrumViewport.Controls.Add(spectrumContent);
            Controls.Add(spectrumViewport);
            Review = new UnifiedSpectrumControl { Font = compactFont };
            Review.AutomaticConclusions = false;
            filterBar=new Panel{Name="PressureStatisticsFilterBar",Height=42,BackColor=PressureTestTheme.SectionBar,Font=compactFont};
            statisticsCheck=new CheckBox{Name="PressureStatisticsFilter",Font=compactFont,AutoSize=false};
            strengthLabel=new Label{Name="PressureFilterStrengthLabel",Font=compactFont,TextAlign=ContentAlignment.MiddleLeft};
            strengthValue=new Label{Name="PressureFilterStrengthValue",Font=compactFont,TextAlign=ContentAlignment.MiddleRight};
            strengthSlider=new TrackBar{Name="PressureFilterStrength",Minimum=0,Maximum=100,Value=40,TickStyle=TickStyle.None,AutoSize=false};
            automaticCheck=new CheckBox{Name="PressureAutoDisplay",Checked=true,Font=compactFont};
            adviceLabel=new Label{Name="PressureDisplayAdvice",Visible=false,Font=compactFont,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};adviceTip=new ToolTip();
            filterBar.Controls.AddRange(new Control[]{statisticsCheck,strengthLabel,strengthSlider,strengthValue,automaticCheck,adviceLabel});
            InitializeLocationFilterChoice();
            statisticsCheck.CheckedChanged+=(s,e)=>{if(!statisticsSync){ManualDisplayChoice();ApplyStatisticsFilter();Notify("STATISTICS_FILTER");}};
            strengthSlider.ValueChanged+=(s,e)=>{if(!statisticsSync){ManualDisplayChoice();statisticsSync=true;statisticsCheck.Checked=true;statisticsSync=false;ApplyStatisticsFilter();Notify("STATISTICS_FILTER");}};
            automaticCheck.CheckedChanged+=(s,e)=>{if(!statisticsSync)AutomaticDisplayRecommendation=automaticCheck.Checked;};
            advisor=new ReviewRecommendationWorker(this,()=>{
                var matrix=(double[,])sourceMatrix.Clone();double strength=strengthSlider.Value,low=Review.ClassificationLowThresholdPercent,high=Review.ClassificationHighThresholdPercent;
                string algorithm=locationFilterAlgorithmId;var options=GetLocationFilterBOptions();
                return ()=>{
                    try{return ReviewDisplayRecommendation.Evaluate(matrix,Enumerable.Repeat(true,matrix.GetLength(0)).ToArray(),strength,low,high,algorithm,options);}
                    catch(ArgumentException error){return new ReviewDisplayRecommendation{AlgorithmId=algorithm,ReasonCode="Unavailable",Error=error.Message};}
                };
            },result=>{
                if(!HasRecommendationData||result.AlgorithmId!=locationFilterAlgorithmId)return;
                advice=result;statisticsSync=true;bool priorApplying=applying;applying=true;
                try{
                    if(automaticDisplay){statisticsCheck.Checked=result.Filter;Review.ConfidenceClassificationEnabled=result.Classification;ApplyStatisticsFilter(result.GetCandidateMask());}
                    // An advisory may turn filtering off because its boundary or input is unknown.
                    // Publish its notice after that transition so disabling the filter cannot erase it.
                    locationFilteringDiagnostics=String.IsNullOrEmpty(result.Error)?result.DiagnosticsJson??"{}":
                        new JavaScriptSerializer().Serialize(new{AlgorithmId=locationFilterAlgorithmId,Parameters=GetLocationFilterBOptions(),Error=result.Error});
                    UpdateLocationFilterNotice(result.UnknownRows,result.Error);RefreshAdvice();
                }
                finally{statisticsSync=false;applying=priorApplying;}
                Notify("DISPLAY_RECOMMENDATION");
            });
            Review.ClassificationManuallyChanged+=(s,e)=>{ManualDisplayChoice();Notify("DISPLAY_MANUAL");};
            spectrumContent.Controls.Add(Review);
            spectrumContent.Controls.Add(filterBar);
            noiseReferencePanel=new NoiseReferencePanel{Font=compactFont};Controls.Add(noiseReferencePanel);
            zeroAnalysisNotice=new Label{Name="ZeroVoltageAnalysisNotice",Dock=DockStyle.Top,AutoSize=false,Height=compactFont.Height*2+8,Font=compactFont,Visible=false,TextAlign=ContentAlignment.MiddleLeft,BackColor=Color.FromArgb(235,246,240)};Controls.Add(zeroAnalysisNotice);
            noiseOnlyMessage=new Label{Name="NoiseReferenceOnlyMessage",Dock=DockStyle.Fill,Font=compactFont,TextAlign=ContentAlignment.MiddleCenter,BackColor=Color.White,ForeColor=PressureTestTheme.SecondaryInk,Visible=false};Controls.Add(noiseOnlyMessage);noiseOnlyMessage.BringToFront();
            InitializeAnalysisOptions();
            filterBar.SizeChanged+=(s,e)=>{LayoutStatisticsFilter();ArrangeSpectrumContent();};
            spectrumViewport.SizeChanged+=(s,e)=>ArrangeSpectrumContent();
            Review.SizeChanged+=(s,e)=>ArrangeSpectrumContent();
            Review.FontChanged+=(s,e)=>ArrangeSpectrumContent();
            SizeChanged+=(s,e)=>ArrangeSpectrumContent();
            SetLanguageIndex(0);
            Review.ViewChanged += delegate {
                if(Review.IsCameraViewNotification){
                    if(Review.Is3DRotating){pendingSpectrumView=true;return;}
                    pendingSpectrumView=false;Notify("SPECTRUM_CAMERA_VIEW");return;
                }
                // Phase/filter/classification/reset notifications retain their
                // operation contract. Only camera-only changes bypass it.
                Notify("SPECTRUM_VIEW");
            };
            Review.MouseCaptureChanged += delegate { FlushSpectrumView(); };
            Review.SelectionShapeChanged += delegate { Notify("SELECTION_SHAPE"); };
            Review.RegionActionCompleted += delegate { Notify("REGION_ACTION"); };
            Review.RegionStatisticsChanged += delegate { Notify("REGION_STATISTICS"); };
            Review.RegionDeletionRestored += delegate { Notify("REGION_UNDO"); };
            probe = new SelectionProbe(this);
            Review.HandleCreated += delegate { probe.Attach(Review.Handle); };
            Review.HandleDestroyed += delegate { probe.Detach(); };
        }
        void FlushSpectrumView()
        {
            if(!pendingSpectrumView||Review.Is3DRotating||IsDisposed||Disposing)return;
            pendingSpectrumView=false;Notify("SPECTRUM_CAMERA_VIEW");
        }
        void Notify(string code) {
            if(applying)return;
            // Automatic display advice has no hardware action or ACK. Never
            // acquire, replace, or release the current pressure action mailbox.
            if(code.StartsWith("DISPLAY_",StringComparison.Ordinal)) {
                var changed=DisplayRecommendationChanged;if(changed!=null)changed(this,EventArgs.Empty);return;
            }
            if(UserAction!=null)UserAction(code);
            if(code=="STATISTICS_FILTER") {var changed=DisplayRecommendationChanged;if(changed!=null)changed(this,EventArgs.Empty);}
        }
        internal void SetBusy(bool value) { busy = value; Review.Enabled = !value; }
        internal bool StatisticsFilterEnabled {get{return statisticsCheck.Checked;}set{ManualDisplayChoice();statisticsCheck.Checked=value;}}
        internal double StatisticsFilterStrength {get{return strengthSlider.Value;}set{if(Double.IsNaN(value)||Double.IsInfinity(value)||value<0||value>100)throw new ArgumentOutOfRangeException("value");ManualDisplayChoice();strengthSlider.Value=(int)Math.Round(value);if(!statisticsCheck.Checked)statisticsCheck.Checked=true;}}
        internal bool AutomaticDisplayRecommendation {get{return automaticDisplay;}set{automaticDisplay=value;advisor.Invalidate();RefreshAdvice();if(value)RequestDisplayAdvice();Notify("DISPLAY_AUTO_MODE");}}
        internal string DisplayRecommendationJson {get{return new JavaScriptSerializer{MaxJsonLength=16*1024*1024}.Serialize(new{Automatic=automaticDisplay,AppliedFilter=statisticsCheck.Checked,AppliedClassification=Review.ConfidenceClassificationEnabled,LocationFilterAlgorithmId=locationFilterAlgorithmId,LocationFilterBOptions=GetLocationFilterBOptions(),LocationFilteringDiagnostics=locationFilteringDiagnostics,Advice=advice});}}
        void ManualDisplayChoice(){automaticDisplay=false;if(advisor!=null)advisor.Invalidate();RefreshAdvice();}
        bool HasRecommendationData {get{return sourceMatrix.GetLength(0)>0&&!referenceOnly&&referencePreviewMessage.Length==0;}}
        void RequestDisplayAdvice()
        {
            if(HasRecommendationData)advisor.Request();
            else {advisor.Invalidate();advice=null;RefreshAdvice();}
        }
        void RefreshAdvice(){if(automaticCheck==null)return;bool prior=statisticsSync;statisticsSync=true;try{
            bool zh=Xiongxin.OWTS.Localization.UiLanguage.IsChinese(LanguageIndex);automaticCheck.Checked=automaticDisplay;
            automaticCheck.Text=AnalysisOptionText("自动推荐","Auto advice");
            adviceLabel.Visible=false;
            adviceLabel.Text=referenceOnly?AnalysisOptionText("零电压噪声对照，不进行定位分析或自动推荐","Zero-voltage noise comparison; no location analysis or automatic advice"):
                referencePreviewMessage.Length>0?AnalysisOptionText("参考波形，不进行定位分析或自动推荐","Reference waveform; no location analysis or automatic advice"):
                (automaticDisplay?AnalysisOptionText("自动设置。 ","Automatic. "):AnalysisOptionText("手动设置。 ","Manual. "))+
                (advice==null?AnalysisOptionText("等待分析数据","Waiting for analysis data"):
                    LanguageIndex==18?Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(advice.Describe(true)):advice.Describe(zh));
            adviceLabel.AccessibleDescription=adviceLabel.Text;adviceTip.SetToolTip(adviceLabel,adviceLabel.Text);
        }finally{statisticsSync=prior;}}
        internal int[] StatisticsFilteredSourceRows {get{return (int[])retainedRows.Clone();}}
        internal int LanguageIndex {get{return Review.LanguageIndex;}}
        ZeroVoltageNoiseReference visibleNoiseReference;bool pendingNoiseReference,referenceOnly;
        string referencePreviewMessage=String.Empty;
        internal void SetReferencePreviewMessage(string message)
        {
            referencePreviewMessage=message??String.Empty;
            SetNoiseReference(visibleNoiseReference,pendingNoiseReference,referenceOnly,LanguageIndex);
        }
        internal void SetNoiseReference(ZeroVoltageNoiseReference value,bool pending,bool only,int language)
        {
            visibleNoiseReference=value;pendingNoiseReference=pending;referenceOnly=only;
            if(pending)noiseReferencePanel.SetPending(language);else noiseReferencePanel.SetReference(value,language);
            if(zeroAnalysis)noiseReferencePanel.Visible=false;
            zeroAnalysisNotice.Text=Xiongxin.OWTS.Localization.UiLanguage.Text(language,"0 kV 数据分析 · 无电压相位参考；不用于起始放电电压判断。","0 kV data analysis · No voltage-phase reference; excluded from inception-voltage assessment.");
            zeroAnalysisNotice.Visible=zeroAnalysis;
            noiseOnlyMessage.Text=Xiongxin.OWTS.Localization.UiLanguage.Text(language,"本次采集仅作零电压噪声对照。\r\n不计入放电次数、起始电压或诊断结论。","This acquisition is a zero-voltage noise comparison only.\r\nExcluded from discharge count, inception voltage and diagnosis.");
            if(referencePreviewMessage.Length>0)noiseOnlyMessage.Text=referencePreviewMessage;
            bool comparisonOnly=only||referencePreviewMessage.Length>0;
            noiseOnlyMessage.Visible=comparisonOnly;Review.Visible=!comparisonOnly;filterBar.Enabled=!comparisonOnly;
            if(comparisonOnly){advisor.Invalidate();advice=null;}
            RefreshAdvice();
        }
        internal void SetZeroVoltageAnalysis(bool value){zeroAnalysis=value;SetNoiseReference(visibleNoiseReference,pendingNoiseReference,referenceOnly,LanguageIndex);}
        internal void SetLanguageIndex(int value)
        {Review.LanguageIndex=value;statisticsCheck.Text=Review.StatisticsFilterCaption;strengthLabel.Text=Review.StatisticsFilterStrengthCaption;RefreshLocationFilterChoice();RefreshAdvice();LayoutStatisticsFilter();RefreshAnalysisOptionsLanguage();if(noiseReferencePanel!=null&&noiseOnlyMessage!=null)SetNoiseReference(visibleNoiseReference,pendingNoiseReference,referenceOnly,value);ArrangeSpectrumContent();}
        private void ArrangeSpectrumContent()
        {
            if(arrangingSpectrumContent||analysisOptions==null||spectrumViewport==null||IsDisposed)return;
            arrangingSpectrumContent=true;
            try
            {
                // One allocation owns all three regions. Wrapped translated
                // options must not force a Dock.Fill chart over the filter bar.
                // Scroll only this content vertically when its measured minimum
                // cannot fit; preserve the original controls, values and fonts.
                int fullWidth=Math.Max(1,spectrumViewport.Width),availableHeight=Math.Max(1,spectrumViewport.Height);
                Review.CompactHostLayout=fullWidth<1050||availableHeight<380;
                bool scroll=false;int width=fullWidth,optionsHeight=0,minimumHeight=0;
                for(int pass=0;pass<3;pass++)
                {
                    width=Math.Max(1,fullWidth-(scroll?SystemInformation.VerticalScrollBarWidth:0));
                    analysisOptions.Width=width;
                    optionsHeight=analysisOptions.GetPreferredSize(new Size(width,0)).Height;
                    filterBar.Width=width;LayoutStatisticsFilter();
                    Review.Width=width;
                    minimumHeight=optionsHeight+Review.MinimumSize.Height+filterBar.Height;
                    bool next=minimumHeight>availableHeight;
                    if(next==scroll)break;
                    scroll=next;
                }
                int contentHeight=Math.Max(availableHeight,minimumHeight);
                spectrumViewport.AutoScrollMinSize=scroll?new Size(0,contentHeight):Size.Empty;
                spectrumContent.Size=new Size(width,contentHeight);
                analysisOptions.SetBounds(0,0,width,optionsHeight);
                analysisOptions.PerformLayout();
                int filterTop=contentHeight-filterBar.Height;
                filterBar.SetBounds(0,filterTop,width,filterBar.Height);
                Review.SetBounds(0,optionsHeight,width,filterTop-optionsHeight);
                spectrumContent.Location=spectrumViewport.AutoScrollPosition;
                // Recalculate the scroll extent after all child bounds have
                // changed, not from the old wider content during resize.
                spectrumViewport.PerformLayout();
            }
            finally{arrangingSpectrumContent=false;}
        }
        private void LayoutStatisticsFilter()
        {
            if(filterBar==null)return;
            int line=Math.Max(30,compactFont.Height+12),width=Math.Max(1,filterBar.ClientSize.Width);
            int checkWidth=TextRenderer.MeasureText(statisticsCheck.Text,compactFont,Size.Empty,TextFormatFlags.NoPadding).Width+28;
            int labelWidth=TextRenderer.MeasureText(strengthLabel.Text,compactFont,Size.Empty,TextFormatFlags.NoPadding).Width+8;
            bool twoRows=width<checkWidth+labelWidth+160;
            int adviceTop=(twoRows?line*2:line)+4;
            bool hasNotice=locationFilterNotice!=null&&locationFilterNotice.Text.Length>0;
            filterBar.Height=adviceTop+line*(hasNotice?2:1)+4;
            statisticsCheck.SetBounds(6,4,Math.Min(checkWidth,width-12),line);
            int left=twoRows?6:checkWidth+12,top=twoRows?line+4:4;
            strengthLabel.SetBounds(left,top,labelWidth,line);left+=labelWidth;
            strengthValue.SetBounds(Math.Max(left,width-51),top,45,line);
            strengthSlider.SetBounds(left,top,Math.Max(30,width-left-55),line);
            strengthValue.Text=strengthSlider.Value.ToString();
            int autoWidth=Math.Min(width-12,TextRenderer.MeasureText(automaticCheck.Text,compactFont).Width+28);
            automaticCheck.SetBounds(6,adviceTop,autoWidth,line);adviceLabel.SetBounds(autoWidth+12,adviceTop,Math.Max(0,width-autoWidth-18),line);
            if(locationFilterNotice!=null){
                if(hasNotice)locationFilterNotice.SetBounds(6,adviceTop+line,Math.Max(1,width-12),line);
                else locationFilterNotice.SetBounds(0,0,0,0);
            }
        }
        private void ApplyStatisticsFilter(bool[] candidate=null)
        {
            bool[] mask=Enumerable.Repeat(true,sourceMatrix.GetLength(0)).ToArray();
            if(statisticsCheck.Checked&&sourceMatrix.GetLength(0)>0)
            {
                mask=candidate??EvaluateLocationFilter();
            }
            else {locationFilteringDiagnostics=new JavaScriptSerializer().Serialize(new{Enabled=false,AlgorithmId=locationFilterAlgorithmId});UpdateLocationFilterNotice(0,null);}
            retainedRows=Enumerable.Range(0,mask.Length).Where(i=>mask[i]).ToArray();
            Review.SetReviewVisibility(mask);strengthValue.Text=strengthSlider.Value.ToString();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (busy && e.Button == MouseButtons.Left) Review.SelectPointAt(e.X, e.Y);
        }
        // Observe a finished native gesture, after the review control has stored
        // its selected region. No timer or posted callback is needed.
        private sealed class SelectionProbe : NativeWindow
        {
            readonly PressureSpectrumDashboardView owner;
            internal SelectionProbe(PressureSpectrumDashboardView owner) { this.owner = owner; }
            internal void Attach(IntPtr handle) { if (Handle != IntPtr.Zero) ReleaseHandle(); AssignHandle(handle); }
            internal void Detach() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
            protected override void WndProc(ref Message message)
            {
                bool up = message.Msg == 0x202;
                int before = up ? owner.Review.SelectionRegionCount : 0;
                base.WndProc(ref message);
                if(up)owner.FlushSpectrumView();
                if (up && !owner.IsDisposed && !owner.busy && owner.Review.SelectionRegionCount != before)
                    owner.Notify("REGION_SELECTION");
            }
        }
        internal string SelectedSpectrumCode
        {
            get { return Review.DisplayModeName; }
            set {
                string[] names = { "Q-L", "N-L", "Q-PHI", "N-PHI", "F-T", "U-Q-L", "U-N-L",
                    "PHI-Q-L", "PHI-N-L", "n-Q-U", "n-N-U", "PRPD", "L-PRPD" };
                int index = Array.FindIndex(names, n => String.Equals(n, value, StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new ArgumentException("Unknown spectrum code.");
                applying = true;
                try { Review.DisplayModeIndex = index; } finally { applying = false; }
            }
        }
        internal int RegionCount { get { return 1; } }
        internal int DataPointCount { get { return Review.SourceRowCount; } }
        internal void LoadMatrix(double[,] matrix)
        {
            applying = true;
            try { sourceMatrix=(double[,])matrix.Clone();Review.LoadDataMatrix(sourceMatrix);RefreshPressureGeometry();
                if(automaticDisplay&&HasRecommendationData){statisticsSync=true;statisticsCheck.Checked=false;statisticsSync=false;}
                ApplyStatisticsFilter();advice=null;RefreshAdvice();RequestDisplayAdvice(); } finally { applying = false; }
        }
        internal void SetOwnedLocationData(double[] positions, double[] charges, double cableLength)
        {
            int count = Math.Min(positions.Length, charges.Length);
            var matrix = new double[count, 36];
            for (int r = 0; r < count; r++) {
                // Unavailable phase/time/frequency/confidence fields stay NaN:
                // legacy location input must never fabricate statistical data.
                for (int c = 0; c < 36; c++) matrix[r, c] = Double.NaN;
                matrix[r, 0] = 0; matrix[r, 1] = r; matrix[r, 5] = cableLength;
                matrix[r, 13] = positions[r]; matrix[r, 15] = charges[r];
                matrix[r, 16] = charges[r]; matrix[r, 35] = 1;
            }
            LoadMatrix(matrix);
        }
        internal void ClearData() { LoadMatrix(new double[0, 36]); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { UserAction = null;DisplayRecommendationChanged=null;EndpointReviewRequested=null; if (probe != null) probe.Detach();if(advisor!=null)advisor.Dispose();if(adviceTip!=null)adviceTip.Dispose(); }
            base.Dispose(disposing);
            if (disposing) compactFont.Dispose();
        }
    }
}
