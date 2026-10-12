using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        // The integrated host owns authentication and persistence. Standalone
        // control consumers retain their existing advanced-settings workflow.
        [Browsable(false),DefaultValue(false)]
        public bool ProtectedAnalysisSettingsManaged{get;set;}
        [Browsable(false)]
        public string EndpointPolicy
        {
            get{return pdmap.ManufacturerEndpointPolicy==null?"":pdmap.ManufacturerEndpointPolicy.Serialized;}
            set{pdmap.ManufacturerEndpointPolicy=HVX.SignalEvidence.RawEndpointPolicy.Parse(String.IsNullOrEmpty(value)?"2;1.5;OR;OR":value); }
        }
        double analysisAttenuationTolerancePercent=15,analysisCorrelationErrorPercent=30,analysisAutomaticThresholdMultiplier=3;
        int analysisMaximumPulseCount=30,analysisMultipleReflectionSuppressionCount=1;
        bool analysisPhaseFilterEnabled,analysisSettingsDialogBusy;
        [Category("Analysis"),DefaultValue(false),Description("Use first-arrival timing instead of pulse-peak timing for localization.")]
        public bool AnalysisUseFirstArrivalTiming
        {
            get { return pdmap.PositioningMode == 0; }
            set { pdmap.PositioningMode = value ? 0 : 1; }
        }
        bool[] analysisQuadrants={true,true,true,true};

        [Category("Analysis"),DefaultValue(15D)]
        public double AnalysisAttenuationTolerancePercent
        {get{return analysisAttenuationTolerancePercent;}set{ValidateAnalysisNumber(value,0,100,"AnalysisAttenuationTolerancePercent");analysisAttenuationTolerancePercent=value;SyncManualConfidenceTolerances();}}
        [Category("Analysis"),DefaultValue(30D),Description("Correlation minimum percent is 100 minus this error percent.")]
        public double AnalysisCorrelationErrorPercent
        {get{return analysisCorrelationErrorPercent;}set{ValidateAnalysisNumber(value,0,100,"AnalysisCorrelationErrorPercent");analysisCorrelationErrorPercent=value;SyncManualConfidenceTolerances();}}
        [Category("Analysis"),DefaultValue(3D),Description("Automatic threshold multiplier; 0 delegates to the algorithm default (currently 3).")]
        public double AnalysisAutomaticThresholdMultiplier
        {get{return analysisAutomaticThresholdMultiplier;}set{ValidateAnalysisNumber(value,0,1000,"AnalysisAutomaticThresholdMultiplier");analysisAutomaticThresholdMultiplier=value;}}
        [Category("Analysis"),DefaultValue(30)]
        public int AnalysisMaximumPulseCount
        {get{return analysisMaximumPulseCount;}set{if(value<1||value>100)throw new ArgumentOutOfRangeException("AnalysisMaximumPulseCount");analysisMaximumPulseCount=value;}}
        [Category("Analysis"),DefaultValue(1),Description("-1 = automatic suppression; 0..100 = configured count.")]
        public int AnalysisMultipleReflectionSuppressionCount
        {get{return analysisMultipleReflectionSuppressionCount;}set{if(value< -1||value>100)throw new ArgumentOutOfRangeException("AnalysisMultipleReflectionSuppressionCount");analysisMultipleReflectionSuppressionCount=value;}}
        [Category("Analysis"),DefaultValue(false)]
        public bool AnalysisPhaseFilterEnabled
        {get{return analysisPhaseFilterEnabled;}set{analysisPhaseFilterEnabled=value;}}
        [Category("Analysis"),Description("Four selected phase quadrants in order [0,90), [90,180), [180,270), [270,360). Defensive copy; only applied when phase filtering is enabled.")]
        public bool[] AnalysisQuadrants
        {
            get{return (bool[])analysisQuadrants.Clone();}
            set{if(value==null||value.Length!=4)throw new ArgumentException("Exactly four phase-quadrant selections are required.","AnalysisQuadrants");analysisQuadrants=(bool[])value.Clone();}
        }
        [Browsable(false)] public bool IsAnalysisSettingsDialogOpen{get{return analysisSettingsDialogBusy;}}
        [Browsable(false)] public int AnalysisSettingsDialogOpenSequence{get;private set;}
        // Fired only after explicit dialog confirmation; loading preferences, property
        // application and Cancel must not overwrite the host's persisted configuration.
        public event EventHandler AnalysisSettingsChanged;

        static void ValidateAnalysisNumber(double value,double minimum,double maximum,string name)
        {if(double.IsNaN(value)||double.IsInfinity(value)||value<minimum||value>maximum)throw new ArgumentOutOfRangeException(name);}

        // Validate the complete draft before committing any field. Editing or
        // cancelling a dialog never changes analysis or review data.
        public void SetAnalysisSettings(double attenuationTolerancePercent,double correlationErrorPercent,
            double automaticThresholdMultiplier,int maximumPulseCount,bool phaseFilterEnabled,bool[] quadrants)
        {
            ValidateAnalysisNumber(attenuationTolerancePercent,0,100,"attenuationTolerancePercent");
            ValidateAnalysisNumber(correlationErrorPercent,0,100,"correlationErrorPercent");
            ValidateAnalysisNumber(automaticThresholdMultiplier,0,1000,"automaticThresholdMultiplier");
            if(maximumPulseCount<1||maximumPulseCount>100)throw new ArgumentOutOfRangeException("maximumPulseCount");
            if(quadrants==null||quadrants.Length!=4)throw new ArgumentException("Exactly four phase-quadrant selections are required.","quadrants");
            bool[] next=(bool[])quadrants.Clone();
            analysisAttenuationTolerancePercent=attenuationTolerancePercent;analysisCorrelationErrorPercent=correlationErrorPercent;
            analysisAutomaticThresholdMultiplier=automaticThresholdMultiplier;analysisMaximumPulseCount=maximumPulseCount;
            analysisPhaseFilterEnabled=phaseFilterEnabled;analysisQuadrants=next;
            SyncManualConfidenceTolerances();
        }
        void SyncManualConfidenceTolerances()
        {
            if(pdmap==null)return;
            pdmap.ConfidenceAttenuationTolerancePercent=analysisAttenuationTolerancePercent;
            pdmap.SeventhManualCorrelationFloor=(100-analysisCorrelationErrorPercent)/100;
        }
        public void ResetAnalysisSettings(){SetAnalysisSettings(15,30,3,30,false,new[]{true,true,true,true});}

        // Only an explicit method/button call opens this dialog. Property writes,
        // paints, reloads and setting changes have no modal side effects.
        public bool ShowAnalysisSettingsDialog()
        {
            if(IsDisposed||Disposing||analysisSettingsDialogBusy)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(ShowAnalysisSettingsDialog));
            analysisSettingsDialogBusy=true;
            try
            {
                using(var dialog=new ReviewAnalysisSettingsDialog(this))
                {
                    RecordOperation("AnalysisSettings","Opened");
                    HookOperationControls(dialog);
                    EventHandler closeOnDispose=(s,e)=>dialog.Dispose();Disposed+=closeOnDispose;
                    try
                    {
                        AnalysisSettingsDialogOpenSequence++;
                        if(dialog.ShowDialog(FindForm())!=DialogResult.OK||IsDisposed||Disposing){RecordOperation("AnalysisSettings","Canceled");return false;}
                        // Revalidate before any owner mutation, including programmatic OK.
                        bool applyAdvanced=dialog.AdvancedUnlocked&&!ProtectedAnalysisSettingsManaged;
                        string nextModelPath=applyAdvanced?dialog.ValidatedModelLibraryPath():ModelLibraryPath;
                        SetAnalysisSettings(dialog.AttenuationTolerancePercent,dialog.CorrelationErrorPercent,
                            dialog.AutomaticThresholdMultiplier,applyAdvanced?dialog.MaximumPulseCount:AnalysisMaximumPulseCount,dialog.PhaseFilterEnabled,dialog.Quadrants);
                        if(applyAdvanced)
                        {
                            AnalysisAlgorithmIndex=dialog.AlgorithmIndex;
                            AnalysisMultipleReflectionSuppressionCount=dialog.MultipleReflectionSuppressionCount;
                            AnalysisUseFirstArrivalTiming=dialog.UseFirstArrivalTiming;
                            ModelLibraryPath=nextModelPath;
                        }
                        SaveAnalysisPreferences();
                        NotifyReviewHost(AnalysisSettingsChanged);
                        RecordOperation("AnalysisSettings","Applied; attenuation="+AnalysisAttenuationTolerancePercent+"; correlationError="+AnalysisCorrelationErrorPercent+"; threshold="+AnalysisAutomaticThresholdMultiplier+"; maxPulses="+AnalysisMaximumPulseCount+"; algorithm="+AnalysisAlgorithmIndex+"; phaseFilter="+AnalysisPhaseFilterEnabled+"; quadrants="+String.Join(",",AnalysisQuadrants)+"; modelPath="+ModelLibraryPath);
                        return true;
                    }
                    finally{Disposed-=closeOnDispose;}
                }
            }
            catch(Exception error){if(IsFatalHostException(error))throw;LastError=error.Message;if(!IsDisposed)RefreshReviewUi();return false;}
            finally{analysisSettingsDialogBusy=false;}
        }
    }

    internal sealed class ReviewAnalysisSettingsDialog:Form
    {
        readonly int languageIndex;
        readonly bool protectedSettingsManaged;
        readonly Label managedSettingsNote;
        readonly TextBox password;
        readonly Label unlockFeedback;
        readonly RoundedReviewButton unlockAdvanced;
        readonly NumericUpDown attenuation,correlation,threshold,maximum;
        readonly NumericUpDown reflectionSuppression;
        readonly ComboBox algorithm;
        readonly TextBox modelPath;
        readonly RoundedReviewButton browseModel;
        readonly Label modelPathHint;
        readonly string originalModelPath;
        readonly CheckBox phaseEnabled,firstArrival;
        readonly ReviewPhaseSelector phaseSelector;
        readonly CheckBox[] quadrants;
        readonly Label correlationHint;
        readonly TableLayoutPanel basicSettingsTable;
        readonly GroupBox advancedGroup,phaseGroup;
        readonly Panel advancedOptions;
        readonly Panel settingsViewport,settingsContent;
        readonly RoundedReviewButton[] footerButtons;
        bool arrangingSettings;
        internal double AttenuationTolerancePercent{get{return (double)attenuation.Value;}}
        internal double CorrelationErrorPercent{get{return (double)correlation.Value;}}
        internal double AutomaticThresholdMultiplier{get{return (double)threshold.Value;}}
        internal int MaximumPulseCount{get{return (int)maximum.Value;}}
        internal bool PhaseFilterEnabled{get{return phaseEnabled.Checked;}}
        internal bool[] Quadrants{get{return quadrants.Select(q=>q.Checked).ToArray();}}
        internal bool AdvancedUnlocked{get;private set;}
        internal int AlgorithmIndex{get{if(algorithm.SelectedIndex<0)throw new ArgumentException(L("Select location method A, B, E or E+.","请选择定位方法 A、B、E 或 E+。"));return new[]{5,7,8,9}[algorithm.SelectedIndex];}}
        internal int MultipleReflectionSuppressionCount{get{return (int)reflectionSuppression.Value;}}
        internal bool UseFirstArrivalTiming{get{return firstArrival.Checked;}}
        string L(string en,string zh){return ReviewAnalysisSettingsText.Text(languageIndex,en,zh);}

        internal ReviewAnalysisSettingsDialog(PdReviewControl owner)
        {
            languageIndex=owner.LanguageIndex;protectedSettingsManaged=owner.ProtectedAnalysisSettingsManaged;Name="ReviewAnalysisSettingsDialog";
            AdvancedUnlocked=!protectedSettingsManaged&&!ReviewAdvancedSettingsSecurity.Enabled;
            var version=(System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(PdReviewControl).Assembly,typeof(System.Reflection.AssemblyFileVersionAttribute));
            Text=L("Analysis settings","分析设置")+"  ·  HV-DAC Xpert "+(version==null?typeof(PdReviewControl).Assembly.GetName().Version.ToString():version.Version);Font=owner.Font;
            FormBorderStyle=FormBorderStyle.FixedDialog;StartPosition=FormStartPosition.CenterParent;
            ShowInTaskbar=false;MaximizeBox=MinimizeBox=false;ClientSize=new Size(620,650);
            BackColor=ReviewTheme.SurfaceAlt;ForeColor=ReviewTheme.Ink;
            attenuation=Number("AnalysisAttenuationTolerance",0,100,2,.5M);
            correlation=Number("AnalysisCorrelationError",0,100,2,.5M);
            threshold=Number("AnalysisThresholdMultiplier",0,1000,2,.1M);
            maximum=Number("AnalysisMaximumPulseCount",1,100,0,1M);
            var table=basicSettingsTable=new TableLayoutPanel{Name="AnalysisValues",Location=new Point(20,119),Size=new Size(580,146),ColumnCount=2,RowCount=3};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,70));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));
            string[] names={L("Attenuation tolerance / %","衰减容差 / %"),L("Correlation error / %","波形相关度误差 / %"),
                L("Automatic threshold multiplier","自动阈值倍数"),L("Maximum pulses per LOC","单个 LOC 最大分析脉冲数")};
            NumericUpDown[] editors={attenuation,correlation,threshold,maximum};
            for(int i=0;i<3;i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Percent,100F/3));
                table.Controls.Add(new Label{Text=names[i],AutoSize=false,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft},0,i);
                editors[i].Anchor=AnchorStyles.Left|AnchorStyles.Right;editors[i].Margin=new Padding(4,12,4,8);table.Controls.Add(editors[i],1,i);
            }
            correlationHint=new Label{Name="AnalysisCorrelationMinimum",Location=new Point(24,211),Size=new Size(280,22),ForeColor=ReviewTheme.Ink};
            var group=phaseGroup=new GroupBox{Name="AnalysisPhaseGroup",Text=L("Phase filtering","相位过滤"),Location=new Point(20,242),Size=new Size(580,186)};
            phaseEnabled=new CheckBox{Name="AnalysisPhaseFilterEnabled",Text=L("Enable","开启"),Location=new Point(14,23),AutoSize=true};
            var phaseHint=new Label{Text=L("Selected quadrants are analyzed.","选中表示分析该象限。"),Location=new Point(112,23),Size=new Size(450,24),TextAlign=ContentAlignment.MiddleRight,AutoEllipsis=true};
            phaseSelector=new ReviewPhaseSelector{Name="AnalysisPhaseSelector",Location=new Point(14,51),Size=new Size(552,125),
                AccessibleName=L("Phase filtering","相位过滤"),AccessibleDescription=L("Selected quadrants are analyzed.","选中表示分析该象限。")} ;
            quadrants=phaseSelector.Quadrants;
            foreach(var toggle in quadrants)toggle.AccessibleDescription=phaseSelector.AccessibleDescription;
            group.Controls.AddRange(new Control[]{phaseEnabled,phaseHint,phaseSelector});
            phaseEnabled.CheckedChanged+=(s,e)=>phaseSelector.Enabled=phaseEnabled.Checked;
            correlation.ValueChanged+=(s,e)=>UpdateCorrelationHint();
            var advanced=advancedGroup=new GroupBox{Name="AnalysisAdvancedGroup",Text=L("Advanced settings","高级设置"),Location=new Point(20,16),Size=new Size(580,93)};
            if(ReviewAdvancedSettingsSecurity.Enabled&&!protectedSettingsManaged)
            {
                var passwordLabel=new Label{Text=L("Password","密码"),Location=new Point(14,27),Size=new Size(110,28),TextAlign=ContentAlignment.MiddleLeft};
                password=new TextBox{Name="AnalysisAdvancedPassword",Location=new Point(125,28),Width=210,MaxLength=256,UseSystemPasswordChar=true,TabIndex=0,AccessibleName=L("Password","密码")};
                unlockAdvanced=Button("UnlockAdvancedAnalysis",L("Unlock","解锁"),350,25,214,31);
                unlockFeedback=new Label{Name="AnalysisUnlockFeedback",Location=new Point(14,62),Size=new Size(548,22),ForeColor=ReviewTheme.Ink,Visible=false};
                password.KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Enter){UnlockAdvanced();e.SuppressKeyPress=true;}};
                unlockAdvanced.Click+=(s,e)=>UnlockAdvanced();
                FormClosed+=(s,e)=>password.Clear();
                advanced.Controls.AddRange(new Control[]{passwordLabel,password,unlockAdvanced,unlockFeedback});
            }
            advancedOptions=new Panel{Name="AnalysisAdvancedOptions",Location=new Point(0,24),Size=new Size(580,224),Visible=true};
            var methodLabel=new Label{Text=L("Analysis method","分析方法"),Location=new Point(14,3),Size=new Size(110,30),TextAlign=ContentAlignment.MiddleLeft};
            algorithm=new ComboBox{Name="AnalysisAlgorithmChoice",Location=new Point(125,6),Size=new Size(134,29),DropDownStyle=ComboBoxStyle.DropDownList,Enabled=false};
            algorithm.Items.AddRange(new object[]{"A","B","E","E+"});
            algorithm.SelectedIndex=owner.AnalysisAlgorithmIndex==5?0:owner.AnalysisAlgorithmIndex==7?1:owner.AnalysisAlgorithmIndex==8?2:owner.AnalysisAlgorithmIndex==9?3:-1;
            var suppressionLabel=new Label{Text=L("Reflection suppression count","多次反射抑制次数"),Location=new Point(275,3),Size=new Size(183,30),TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
            var suppressionTooltip=new ToolTip();suppressionTooltip.SetToolTip(suppressionLabel,suppressionLabel.Text);
            FormClosed+=(s,e)=>suppressionTooltip.Dispose();
            reflectionSuppression=Number("AnalysisReflectionSuppression",-1,100,0,1M);
            reflectionSuppression.Location=new Point(465,6);reflectionSuppression.Size=new Size(98,29);reflectionSuppression.Enabled=false;
            reflectionSuppression.Value=owner.AnalysisMultipleReflectionSuppressionCount;
            var suppressionHint=new Label{Text=L("-1 = automatic","-1 = 自动"),Location=new Point(14,37),Size=new Size(548,22),ForeColor=ReviewTheme.Ink};
            firstArrival=new CheckBox{Name="AnalysisUseFirstArrivalTiming",Text=L("First-arrival localization","首达端定位模式"),Location=new Point(14,63),Size=new Size(548,26),AutoSize=false,
                Enabled=false,Checked=owner.AnalysisUseFirstArrivalTiming};
            algorithm.SelectedIndexChanged+=(s,e)=>{
                bool eMethod=algorithm.SelectedIndex>=2;
                if(eMethod)firstArrival.Checked=false;
                firstArrival.Enabled=reflectionSuppression.Enabled=AdvancedUnlocked&&!eMethod;
                suppressionHint.Text=eMethod?L("E/E+: raw peaks; E+ optional/experimental research score. Scores are not accuracy.","E/E+：原始峰值；E+ 为可选实验方法。分数不是准确率。"):L("-1 = automatic","-1 = 自动");
            };
            if(algorithm.SelectedIndex>=2){firstArrival.Checked=false;suppressionHint.Text=L("E/E+: raw peaks; E+ optional/experimental research score. Scores are not accuracy.","E/E+：原始峰值；E+ 为可选实验方法。分数不是准确率。");}
            advancedOptions.Controls.AddRange(new Control[]{methodLabel,algorithm,suppressionLabel,reflectionSuppression,suppressionHint,firstArrival});
            maximum.Location=new Point(405,100);maximum.Size=new Size(158,29);maximum.Enabled=false;
            advancedOptions.Controls.Add(new Label{Name="AnalysisMaximumPulseCountLabel",Text=names[3],Location=new Point(14,97),Size=new Size(380,30),TextAlign=ContentAlignment.MiddleLeft});
            advancedOptions.Controls.Add(maximum);
            originalModelPath=owner.ModelLibraryPath;
            var modelLabel=new Label{Name="AnalysisModelLibraryPathLabel",Text=L("Defect recognition model path","缺陷识别模型路径"),Location=new Point(14,135),Size=new Size(548,26),TextAlign=ContentAlignment.MiddleLeft};
            modelPath=new TextBox{Name="AnalysisModelLibraryPath",Text=originalModelPath,Location=new Point(14,164),Size=new Size(442,26),MaxLength=4096,Enabled=false,AccessibleName=modelLabel.Text};
            browseModel=Button("BrowseAnalysisModel",L("Browse","浏览"),465,161,98,31);
            ReviewTheme.ButtonRole(browseModel,false);browseModel.Enabled=false;
            modelPathHint=new Label{Name="AnalysisModelLibraryPathHint",Text=L("PDM file or model folder; leave blank for the bundled model.","PDM 文件或模型目录；留空使用软件自带模型。"),Location=new Point(14,197),Size=new Size(548,24),AutoEllipsis=true,ForeColor=ReviewTheme.SecondaryInk};
            browseModel.Click+=(s,e)=>{
                if(!AdvancedUnlocked)return;
                using(var picker=new OpenFileDialog{Title=modelLabel.Text,Filter=Xiongxin.OWTS.Desktop.DefectModelCatalog.FileFilter,CheckFileExists=true,Multiselect=false,RestoreDirectory=true})
                    if(picker.ShowDialog(this)==DialogResult.OK)modelPath.Text=picker.FileName;
            };
            advancedOptions.Controls.AddRange(new Control[]{modelLabel,modelPath,browseModel,modelPathHint});
            advanced.Controls.Add(advancedOptions);
            var apply=Button("ApplyAnalysisSettings",L("Apply","确定"),20);
            var defaults=Button("ResetAnalysisSettings",L("Default","默认"),222);
            var cancel=Button("CancelAnalysisSettings",L("Cancel","取消"),424);
            ReviewTheme.ButtonRole(defaults,false);ReviewTheme.ButtonRole(cancel,false);
            footerButtons=new[]{apply,defaults,cancel};
            apply.Click+=(s,e)=>{
                try{if(AdvancedUnlocked){int selected=AlgorithmIndex;ValidatedModelLibraryPath();}DialogResult=DialogResult.OK;}
                catch(Exception error) when(error is System.IO.IOException||error is ArgumentException||error is UnauthorizedAccessException||error is System.Security.SecurityException||error is NotSupportedException){
                    modelPathHint.Text=algorithm.SelectedIndex<0?L("Select location method A or B.","请选择定位方法 A 或 B。"):L("Invalid model path. Select an existing PDM file or an unambiguous model folder.","模型路径无效，请选择已有 PDM 文件或仅含一个模型的目录。");modelPathHint.ForeColor=Color.Firebrick;modelPath.Focus();}
            };
            cancel.DialogResult=DialogResult.Cancel;
            defaults.Click+=(s,e)=>{SetDraft(15,30,3,AdvancedUnlocked?30:MaximumPulseCount,false,new[]{true,true,true,true});
                if(AdvancedUnlocked){algorithm.SelectedIndex=0;reflectionSuppression.Value=1;firstArrival.Checked=false;modelPath.Clear();}};
            // Keep all draft controls in stable content coordinates. Native
            // scrolling moves only this content panel, never the phase/footer
            // coordinates used while expanding and collapsing advanced options.
            settingsViewport=new Panel{Name="AnalysisSettingsViewport",Dock=DockStyle.Fill,AutoScroll=true,BackColor=BackColor};
            settingsContent=new Panel{Name="AnalysisSettingsContent",Location=Point.Empty,BackColor=BackColor,Size=ClientSize};
            settingsContent.Controls.AddRange(new Control[]{advanced,table,correlationHint,group,apply,defaults,cancel});
            managedSettingsNote=new Label{Name="AnalysisProtectedSettingsNotice",AutoSize=false,ForeColor=ReviewTheme.SecondaryInk,
                Text=L("Analysis parameters and model selection are managed in Settings → HV DAC Xpert Plus Data.","分析参数与模型选择请前往：参数设置 → HV DAC Xpert Plus Data。"),Visible=protectedSettingsManaged};
            settingsContent.Controls.Add(managedSettingsNote);
            advanced.TabIndex=0;table.TabIndex=1;group.TabIndex=2;apply.TabIndex=3;defaults.TabIndex=4;cancel.TabIndex=5;
            settingsViewport.Controls.Add(settingsContent);Controls.Add(settingsViewport);AcceptButton=apply;CancelButton=cancel;
            SetDraft(owner.AnalysisAttenuationTolerancePercent,owner.AnalysisCorrelationErrorPercent,owner.AnalysisAutomaticThresholdMultiplier,
                owner.AnalysisMaximumPulseCount,owner.AnalysisPhaseFilterEnabled,owner.AnalysisQuadrants);
            LayoutAdvancedControls();
        }
        static NumericUpDown Number(string name,decimal minimum,decimal maximum,int digits,decimal increment)
        {return new NumericUpDown{Name=name,Minimum=minimum,Maximum=maximum,DecimalPlaces=digits,Increment=increment,ThousandsSeparator=false};}
        internal string ValidatedModelLibraryPath()
        {
            if(!AdvancedUnlocked)return originalModelPath;
            string path=modelPath.Text.Trim();
            // A missing, unchanged model must not block unrelated settings.
            if(path==originalModelPath||path.Length==0)return path;
            return ReviewTask12Runtime.ResolveModelPath(path);
        }
        internal bool TryUnlockAdvanced(string candidate)
        {
            if(protectedSettingsManaged)return false;
            if(ReviewAdvancedSettingsSecurity.Enabled&&!ReviewAdvancedSettingsSecurity.VerifyPassword(candidate))return false;
            AdvancedUnlocked=true;
            LayoutAdvancedControls();
            return true;
        }
        void UnlockAdvanced()
        {
            if(password==null||unlockFeedback==null)return;
            bool accepted;
            try { accepted=TryUnlockAdvanced(password.Text); }
            finally { password.Clear(); }
            unlockFeedback.Visible=true;
            unlockFeedback.Text=accepted?L("Unlocked","已解锁"):L("Wrong password","密码错误");
            unlockFeedback.ForeColor=accepted?ReviewTheme.Accent:Color.Firebrick;
        }
        void LayoutAdvancedControls()
        {
            if(arrangingSettings)return;arrangingSettings=true;
            SuspendLayout();
            try{
                double scale=advancedGroup.Width/580.0;
                advancedGroup.Visible=!protectedSettingsManaged;
                basicSettingsTable.Enabled=phaseGroup.Enabled=true;
                phaseSelector.Enabled=phaseEnabled.Checked;
                footerButtons[0].Enabled=footerButtons[1].Enabled=true;
                maximum.Enabled=algorithm.Enabled=AdvancedUnlocked;reflectionSuppression.Enabled=firstArrival.Enabled=AdvancedUnlocked&&algorithm.SelectedIndex<2;
                modelPath.Enabled=browseModel.Enabled=AdvancedUnlocked;
                advancedOptions.Visible=AdvancedUnlocked;
                int lockHeight=ReviewAdvancedSettingsSecurity.Enabled&&!protectedSettingsManaged?80:0;
                advancedOptions.Top=(int)Math.Round((24+lockHeight)*scale);
                advancedGroup.Height=(int)Math.Round((AdvancedUnlocked?254+lockHeight:93)*scale);
                if(password!=null)password.Enabled=!AdvancedUnlocked;
                if(unlockAdvanced!=null)unlockAdvanced.Enabled=!AdvancedUnlocked;
                // Keep the advanced options above ordinary settings in the scrollable content.
                basicSettingsTable.Top=advancedGroup.Bottom+(int)Math.Round(10*scale);
                if(protectedSettingsManaged)
                {
                    int noteWidth=advancedGroup.Width;
                    int noteHeight=TextRenderer.MeasureText(managedSettingsNote.Text,Font,new Size(noteWidth,int.MaxValue),TextFormatFlags.WordBreak).Height+12;
                    managedSettingsNote.Bounds=new Rectangle(advancedGroup.Left,advancedGroup.Top,noteWidth,noteHeight);
                    basicSettingsTable.Top=managedSettingsNote.Bottom+(int)Math.Round(10*scale);
                }
                correlationHint.Top=basicSettingsTable.Bottom+(int)Math.Round(scale);
                phaseGroup.Top=correlationHint.Bottom+(int)Math.Round(9*scale);
                int footerTop=phaseGroup.Bottom+(int)Math.Round(17*scale);
                foreach(var button in footerButtons)button.Top=footerTop;
                var contentSize=new Size(advancedGroup.Right+(int)Math.Round(20*scale),footerButtons[0].Bottom+(int)Math.Round(14*scale));
                settingsContent.Size=contentSize;settingsViewport.AutoScrollMinSize=contentSize;
                FitSettingsToWorkingArea(contentSize);
            }finally{ResumeLayout(true);arrangingSettings=false;}
        }
        void FitSettingsToWorkingArea(Size contentSize)
        {
            var area=Screen.FromControl(this).WorkingArea;
            int availableWidth=Math.Max(1,area.Width-(Width-ClientSize.Width));
            int availableHeight=Math.Max(1,area.Height-(Height-ClientSize.Height));
            int width=Math.Min(availableWidth,contentSize.Width+(contentSize.Height>availableHeight?SystemInformation.VerticalScrollBarWidth:0));
            int height=Math.Min(availableHeight,contentSize.Height+(contentSize.Width>width?SystemInformation.HorizontalScrollBarHeight:0));
            ClientSize=new Size(width,height);
            if(IsHandleCreated)
            {
                Left=Math.Max(area.Left,Math.Min(Left,area.Right-Width));
                Top=Math.Max(area.Top,Math.Min(Top,area.Bottom-Height));
            }
        }
        protected override void OnShown(EventArgs e)
        {
            // Font/DPI auto-scaling and the native window frame are finalized
            // by this point. Refit before exposing the first usable dialog.
            LayoutAdvancedControls();base.OnShown(e);
        }
        static RoundedReviewButton Button(string name,string caption,int x,int y=600,int width=176,int height=36)
        {
            var b=new RoundedReviewButton{Name=name,Text=caption,Location=new Point(x,y),Size=new Size(width,height),BackColor=ReviewTheme.Accent,
                ForeColor=Color.White,FlatStyle=FlatStyle.Flat,UseVisualStyleBackColor=false};b.FlatAppearance.BorderColor=ReviewTheme.Accent;return b;
        }
        void SetDraft(double attenuationPercent,double correlationPercent,double multiplier,int pulseCount,bool filter,bool[] selected)
        {
            attenuation.Value=(decimal)attenuationPercent;correlation.Value=(decimal)correlationPercent;threshold.Value=(decimal)multiplier;maximum.Value=pulseCount;
            phaseEnabled.Checked=filter;
            phaseSelector.Enabled=filter;
            for(int i=0;i<4;i++)quadrants[i].Checked=selected[i];
            UpdateCorrelationHint();
        }
        void UpdateCorrelationHint(){correlationHint.Text="ρ ≥ "+(100-correlation.Value).ToString("0.##")+" %";}
    }

    // Analysis-only vocabulary stays beside the dialog; the common language
    // catalog continues to translate shared Apply/Default/Cancel labels.
    internal static class ReviewAnalysisSettingsText
    {
        static readonly string[] Keys={"Analysis settings","Attenuation tolerance / %","Correlation error / %","Automatic threshold multiplier","Maximum pulses per LOC","Phase filtering","Selected quadrants are analyzed.","Advanced settings","Password","Unlock","Analysis method","Reflection suppression count","Wrong password","Unlocked","-1 = automatic","First-arrival localization"};
        static readonly string[][] Labels={
            "Настройки за анализ|Postavke analize|Nastavení analýzy|Paramètres d’analyse|Analyseeinstellungen|Elemzési beállítások|Impostazioni di analisi|Ustawienia analizy|Setări de analiză|Настройки анализа|Podešavanja analize|Nastavenia analýzy|Nastavitve analize|Configuración de análisis|Analiz ayarları|Cài đặt phân tích".Split('|'),
            "Толеранс на затихване / %|Tolerancija prigušenja / %|Tolerance útlumu / %|Tolérance d’atténuation / %|Dämpfungstoleranz / %|Csillapítási tűrés / %|Tolleranza di attenuazione / %|Tolerancja tłumienia / %|Toleranța atenuării / %|Допуск затухания / %|Tolerancija slabljenja / %|Tolerancia útlmu / %|Toleranca dušenja / %|Tolerancia de atenuación / %|Sönüm toleransı / %|Dung sai suy giảm / %".Split('|'),
            "Грешка на корелацията / %|Pogreška korelacije / %|Chyba korelace / %|Erreur de corrélation / %|Korrelationsfehler / %|Korrelációs hiba / %|Errore di correlazione / %|Błąd korelacji / %|Eroarea corelației / %|Погрешность корреляции / %|Greška korelacije / %|Chyba korelácie / %|Napaka korelacije / %|Error de correlación / %|Korelasyon hatası / %|Sai số tương quan / %".Split('|'),
            "Множител на автоматичния праг|Množitelj automatskog praga|Násobitel automatického prahu|Multiplicateur du seuil automatique|Multiplikator der automatischen Schwelle|Automatikus küszöb szorzója|Moltiplicatore della soglia automatica|Mnożnik progu automatycznego|Multiplicatorul pragului automat|Множитель автоматического порога|Množilac automatskog praga|Násobiteľ automatického prahu|Množitelj samodejnega praga|Multiplicador del umbral automático|Otomatik eşik çarpanı|Hệ số ngưỡng tự động".Split('|'),
            "Максимум импулси за LOC|Najviše impulsa po LOC-u|Maximum impulzů na LOC|Impulsions maximales par LOC|Maximale Impulszahl pro LOC|Legnagyobb impulzusszám LOC-onként|Impulsi massimi per LOC|Maksymalna liczba impulsów na LOC|Număr maxim de impulsuri per LOC|Максимум импульсов на LOC|Najviše impulsa po LOC-u|Maximum impulzov na LOC|Največ impulzov na LOC|Máximo de impulsos por LOC|LOC başına en fazla darbe|Số xung tối đa mỗi LOC".Split('|'),
            "Фазов филтър|Fazno filtriranje|Filtrování fáze|Filtrage de phase|Phasenfilter|Fázisszűrés|Filtro di fase|Filtrowanie fazy|Filtrarea fazei|Фильтр по фазе|Fazno filtriranje|Filtrovanie fázy|Filtriranje faze|Filtrado de fase|Faz filtresi|Lọc pha".Split('|'),
            "Избраните квадранти се анализират.|Analiziraju se odabrani kvadranti.|Analyzují se vybrané kvadranty.|Les quadrants sélectionnés sont analysés.|Ausgewählte Quadranten werden analysiert.|A kijelölt negyedek elemzése történik.|Vengono analizzati i quadranti selezionati.|Analizowane są wybrane kwadranty.|Sunt analizate cadranele selectate.|Анализируются выбранные квадранты.|Analiziraju se izabrani kvadranti.|Analyzujú sa vybrané kvadranty.|Analizirajo se izbrani kvadranti.|Se analizan los cuadrantes seleccionados.|Seçilen çeyrekler analiz edilir.|Phân tích các góc phần tư được chọn.".Split('|'),
            "Разширени настройки|Napredne postavke|Pokročilá nastavení|Paramètres avancés|Erweiterte Einstellungen|Speciális beállítások|Impostazioni avanzate|Ustawienia zaawansowane|Setări avansate|Дополнительные настройки|Napredna podešavanja|Rozšírené nastavenia|Napredne nastavitve|Configuración avanzada|Gelişmiş ayarlar|Cài đặt nâng cao".Split('|'),
            "Парола|Lozinka|Heslo|Mot de passe|Passwort|Jelszó|Password|Hasło|Parolă|Пароль|Lozinka|Heslo|Geslo|Contraseña|Parola|Mật khẩu".Split('|'),
            "Отключи|Otključaj|Odemknout|Déverrouiller|Entsperren|Feloldás|Sblocca|Odblokuj|Deblochează|Разблокировать|Otključaj|Odomknúť|Odkleni|Desbloquear|Kilidi aç|Mở khóa".Split('|'),
            "Метод на анализ|Metoda analize|Metoda analýzy|Méthode d’analyse|Analysemethode|Elemzési módszer|Metodo di analisi|Metoda analizy|Metodă de analiză|Метод анализа|Metod analize|Metóda analýzy|Metoda analize|Método de análisis|Analiz yöntemi|Phương pháp phân tích".Split('|'),
            "Брой потискани отражения|Broj potisnutih odraza|Počet potlačených odrazů|Nombre de réflexions supprimées|Anzahl unterdrückter Reflexionen|Elnyomott visszaverődések száma|Numero di riflessioni soppresse|Liczba tłumionych odbić|Număr de reflexii suprimate|Число подавляемых отражений|Broj potisnutih refleksija|Počet potlačených odrazov|Število zatrtih odbojev|Número de reflexiones suprimidas|Bastırılan yansıma sayısı|Số lần khử phản xạ".Split('|'),
            "Грешна парола|Pogrešna lozinka|Nesprávné heslo|Mot de passe incorrect|Falsches Passwort|Hibás jelszó|Password errata|Nieprawidłowe hasło|Parolă incorectă|Неверный пароль|Pogrešna lozinka|Nesprávne heslo|Napačno geslo|Contraseña incorrecta|Yanlış parola|Sai mật khẩu".Split('|'),
            "Отключено|Otključano|Odemčeno|Déverrouillé|Entsperrt|Feloldva|Sbloccato|Odblokowano|Deblocat|Разблокировано|Otključano|Odomknuté|Odklenjeno|Desbloqueado|Kilit açıldı|Đã mở khóa".Split('|'),
            "-1 = автоматично|-1 = automatski|-1 = automaticky|-1 = automatique|-1 = automatisch|-1 = automatikus|-1 = automatico|-1 = automatycznie|-1 = automat|-1 = автоматически|-1 = automatski|-1 = automaticky|-1 = samodejno|-1 = automático|-1 = otomatik|-1 = tự động".Split('|'),
            "Локализация по първо пристигане|Lociranje prema prvom dolasku|Lokalizace podle prvního příchodu|Localisation au premier front|Ortung nach Erstankunft|Helymeghatározás az első beérkezésből|Localizzazione al primo arrivo|Lokalizacja według pierwszego nadejścia|Localizare după prima sosire|Локализация по первому приходу|Lociranje prema prvom dolasku|Lokalizácia podľa prvého príchodu|Lociranje po prvem prihodu|Localización por primera llegada|İlk varışa göre konumlandırma|Định vị theo thời điểm đến đầu tiên".Split('|')
        };
        internal static string Text(int language,string en,string zh)
        {
            string common=ReviewLanguages.Text(language,en,zh);
            if(language<2||language>17||common!=en)return common;
            int index=Array.IndexOf(Keys,en);return index<0?common:Labels[index][language-2];
        }
    }
}
