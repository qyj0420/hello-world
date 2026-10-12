using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LabVIEWLocationTree;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        LocationTreeControl analysisTree;
        ReviewLocationOverview locationOverview;
        Panel overviewHost;
        Panel workspaceBody,workspaceTreePanel,workspaceTreeSeparator,workspaceHeaderDivider,workspaceRightPanel;
        Panel workspaceCanvas;
        TableLayoutPanel workspaceHeader;
        Button clearAnalysisButton,automaticAnalysisButton,manualAnalysisButton,analysisSettingsButton,localizationComparisonButton;
        CheckBox manualThresholdBox,thresholdAllBox,overviewSnap,overviewReverse,overviewCompensated;
        NumericUpDown thresholdNumber;
        TrackBar thresholdSlider;
        Label analysisProgress;
        Label reviewBuildLabel;
        Button analysisDiagnosticsButton;
        bool treeSync,thresholdSync,locationOverviewMode,treeMapFocusRequested;
        string overviewPath="",spectrumScopePath="";
        readonly Dictionary<string,ThresholdChoice> fileThresholds=new Dictionary<string,ThresholdChoice>(StringComparer.OrdinalIgnoreCase);
        ThresholdChoice globalThreshold=new ThresholdChoice();
        bool thresholdAll;
        CancellationTokenSource analysisCancellation;
        int analysisGeneration;
        bool analysisBusy;
        sealed class ThresholdChoice { internal bool Manual,HumanConfirmed;internal double Value=100;internal double Actual=double.NaN,ActualMultiplier=double.NaN;internal ThresholdChoice Copy(){return (ThresholdChoice)MemberwiseClone();} }
        [Browsable(false)] public LocationTreeControl AnalysisTree {get{return analysisTree;}}
        [Browsable(false)] public bool IsLocationOverviewMode {get{return locationOverviewMode;}}
        [Browsable(false)] public string DisplayedLocationFilePath {get{return overviewPath;}}
        [Browsable(false)] public bool IsAnalysisRunning {get{return analysisBusy;}}
        [Browsable(false)] public string AnalysisStatus {get;private set;}="";
        [Browsable(false)] public int AnalysisCompletedFileCount {get;private set;}
        [Category("Analysis"),DefaultValue(5)] public int AnalysisAlgorithmIndex {get{return analysisAlgorithmIndex;}set{if(value<0||value>9)throw new ArgumentOutOfRangeException("AnalysisAlgorithmIndex");analysisAlgorithmIndex=value;}}
        int analysisAlgorithmIndex=5;
        [Category("Analysis"),DefaultValue(false)] public bool ApplyThresholdToAllFiles {get{return thresholdAll;}set{if(thresholdAll==value)return;if(value)globalThreshold=ThresholdFor(overviewPath).Copy();thresholdAll=value;SyncThresholdUi();}}
        [Browsable(false)] public double CurrentAnalysisThresholdPc {get{return locationOverview==null?double.NaN:locationOverview.CurrentThresholdPc;}}
        public event EventHandler AnalysisCompleted;
        public event EventHandler LocalizationComparisonRequested;

        void InitializeProjectWorkspace()
        {
            // Retain a canvas fallback for unsupported tiny hosts and extreme
            // system fonts. Supported notebook viewports lay out in-place.
            AutoScroll=false;
            var shell=workspaceCanvas=new Panel{Name="ReviewWorkspace",BackColor=Color.White,Location=Point.Empty};
            Controls.Remove(outer);Controls.Add(shell);
            workspaceBody=new Panel{Name="ReviewWorkspaceBody",Dock=DockStyle.Fill,BackColor=ReviewTheme.Workspace,Padding=new Padding(12)};
            var left=workspaceTreePanel=new Panel{Name="ReviewAnalysisTreePanel",Dock=DockStyle.Left,Width=228,Padding=new Padding(10),BackColor=Color.White};
            var separator=workspaceTreeSeparator=new Panel{Name="ReviewFixedTreeSeparator",Dock=DockStyle.Left,Width=8,BackColor=ReviewTheme.Workspace,Cursor=Cursors.Default};
            var right=workspaceRightPanel=new Panel{Name="ReviewWorkspaceRight",Dock=DockStyle.Fill,BackColor=Color.White};
            workspaceBody.Controls.Add(right);workspaceBody.Controls.Add(separator);workspaceBody.Controls.Add(left);shell.Controls.Add(workspaceBody);
            analysisTree=new LocationTreeControl{Name="ReviewAnalysisTree",Dock=DockStyle.Fill};left.Controls.Add(analysisTree);
            var footer=new Panel{Dock=DockStyle.Bottom,Height=42,Padding=new Padding(3,5,3,3)};left.Controls.Add(footer);
            clearAnalysisButton=ActionButton(208);clearAnalysisButton.Name="ReviewClearAllAnalysis";clearAnalysisButton.Dock=DockStyle.Fill;footer.Controls.Add(clearAnalysisButton);
            clearAnalysisButton.Click+=(s,e)=>ClearAllAnalysis();
            right.Controls.Add(outer);
            var header=workspaceHeader=new TableLayoutPanel{Name="ReviewAnalysisHeader",Dock=DockStyle.Top,Height=42,ColumnCount=3,RowCount=1,Padding=new Padding(5,3,5,3),BackColor=Color.White};
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,0));
            automaticAnalysisButton=ActionButton(160);automaticAnalysisButton.Name="ReviewAutomaticAnalysis";automaticAnalysisButton.Dock=DockStyle.Fill;
            ReviewTheme.ButtonRole(automaticAnalysisButton,true);
            manualAnalysisButton=ActionButton(160);manualAnalysisButton.Name="ReviewManualAnalysis";manualAnalysisButton.Dock=DockStyle.Fill;
            header.Controls.Add(automaticAnalysisButton,0,0);header.Controls.Add(manualAnalysisButton,1,0);
            localizationComparisonButton=ActionButton(160);localizationComparisonButton.Name="ReviewLocalizationComparison";localizationComparisonButton.Dock=DockStyle.Fill;
            localizationComparisonButton.Visible=false;localizationComparisonButton.TabStop=false;
            header.Controls.Add(localizationComparisonButton,2,0);
            localizationComparisonButton.Click+=(s,e)=>{
                if(analysisBusy)return;
                var handler=LocalizationComparisonRequested;
                if(handler!=null)handler(this,EventArgs.Empty);
                else SetReviewOperationStatus(L("Open localization review in the desktop application settings.","请从汇总软件设置打开定位复核。"));
            };
            workspaceHeaderDivider=new Panel{Name="ReviewAnalysisHeaderDivider",Dock=DockStyle.Top,Height=2,BackColor=ReviewTheme.Border,Cursor=Cursors.Default,TabStop=false};
            shell.Controls.Add(workspaceHeaderDivider);shell.Controls.Add(header);
            automaticAnalysisButton.Click+=(s,e)=>RequestSelectedAnalysisFromUi(true);manualAnalysisButton.Click+=(s,e)=>RequestSelectedAnalysisFromUi(false);
            analysisProgress=new Label{Name="ReviewAnalysisProgress",Width=156,Height=48,Margin=new Padding(0,2,0,2),AutoEllipsis=true,ForeColor=ReviewTheme.Ink,Visible=false};side.Controls.Add(analysisProgress);
            SetupAnalysisDiagnostics();
            // InitializeReviewStatusBar docks the actual instance version at the
            // bottom right, where it remains visible even during diagnostics.
            reviewBuildLabel=new Label{Name="ReviewControlBuildVersion",Width=156,Height=20,Margin=Padding.Empty,Text=ControlBuildLabel,ForeColor=ReviewTheme.SecondaryInk};
            reviewBuildLabel.Cursor=Cursors.Hand;
            reviewBuildLabel.DoubleClick+=(s,e)=>ShowAnalysisDiagnostics();
            analysisDiagnosticsButton=ActionButton(152);analysisDiagnosticsButton.Name="ReviewAnalysisDiagnostics";analysisDiagnosticsButton.Height=28;
            analysisDiagnosticsButton.Visible=false;analysisDiagnosticsButton.TabStop=false;
            analysisDiagnosticsButton.Click+=(s,e)=>ShowAnalysisDiagnostics();side.Controls.Add(analysisDiagnosticsButton);
            overviewHost=new Panel{Name="ReviewLocationOverviewHost",Dock=DockStyle.Fill,Visible=false,BackColor=Color.White};charts.Panel1.Controls.Add(overviewHost);
            locationOverview=new ReviewLocationOverview{Name="ReviewLocationOverview",Dock=DockStyle.Fill};overviewHost.Controls.Add(locationOverview);
            thresholdSlider=new TrackBar{Name="ReviewThresholdSlider",Dock=DockStyle.Right,Orientation=Orientation.Vertical,AutoSize=false,Width=28,Minimum=0,Maximum=1000,TickStyle=TickStyle.None,BackColor=Color.White};overviewHost.Controls.Add(thresholdSlider);
            // Let the available width determine the row count; a fixed height left
            // a large empty strip on wide hosts and could clip wrapped controls.
            var options=new FlowLayoutPanel{Name="ReviewLocationOptions",Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(5,3,5,3),WrapContents=true,BackColor=Color.White};overviewHost.Controls.Add(options);
            manualThresholdBox=new CheckBox{Name="ReviewManualThreshold",AutoSize=true};thresholdNumber=new NumericUpDown{Name="ReviewThresholdValue",Width=104,DecimalPlaces=2,Minimum=0,Maximum=1000000000,Value=100};
            thresholdAllBox=new CheckBox{Name="ReviewThresholdAllFiles",AutoSize=true};
            overviewSnap=new CheckBox{Name="ReviewOverviewSnap",AutoSize=true};overviewReverse=new CheckBox{Name="ReviewOverviewReverse",AutoSize=true};overviewCompensated=new CheckBox{Name="ReviewOverviewCompensated",AutoSize=true};
            options.Controls.AddRange(new Control[]{manualThresholdBox,thresholdNumber,new Label{Text="pC",AutoSize=true,Padding=new Padding(0,4,0,0)},thresholdAllBox,overviewSnap,overviewReverse,overviewCompensated});
            InitializeEndpointReview(options);
            InitializeLocationOverviewLoading(options);
            manualThresholdBox.CheckedChanged+=(s,e)=>SaveThresholdUi();thresholdNumber.ValueChanged+=(s,e)=>SaveThresholdUi();
            thresholdAllBox.CheckedChanged+=(s,e)=>{if(!thresholdSync)ApplyThresholdToAllFiles=thresholdAllBox.Checked;};
            thresholdSlider.ValueChanged+=(s,e)=>{if(thresholdSync)return;double max=OverviewThresholdMaximum();thresholdSync=true;thresholdNumber.Value=(decimal)Math.Min((double)thresholdNumber.Maximum,max*thresholdSlider.Value/1000.0);manualThresholdBox.Checked=true;thresholdSync=false;SaveThresholdUi();};
            overviewSnap.CheckedChanged+=(s,e)=>{if(!thresholdSync)pdmap.ApproximateCablePositions=overviewSnap.Checked;};
            overviewReverse.CheckedChanged+=(s,e)=>{if(!thresholdSync)ReversePositionDisplay=overviewReverse.Checked;};
            overviewCompensated.CheckedChanged+=(s,e)=>{if(!thresholdSync)spectrum.UseCompensatedCharge=overviewCompensated.Checked;};
            analysisTree.TreeLeftClicked+=(s,e)=>{if(!treeSync)ClearSpectrumFileScope();};
            analysisTree.SelectedOutputChanged+=async(s,e)=>{
                if(treeSync)return;
                if(analysisTree.SelectedNodeType==3)await BeginLocationOverviewLoad(analysisTree.SelectedDataPath);
                else if(analysisTree.SelectedNodeType==4)ViewSourceRow(analysisTree.SelectedMatrixRowIndex);
                else CancelLocationOverviewLoad();
            };
            analysisTree.RecordDisplayFilterRequested+=async(s,e)=>{
                if(treeSync)return;string path=analysisTree.RequestedDisplayDataPath;
                if(!string.IsNullOrEmpty(path)&&!await BeginLocationOverviewLoad(path))return;
                spectrumScopePath=path;ApplyTreeSpectrumVisibility();
            };
            analysisTree.AnalysisDeleteRequested+=(s,e)=>{if(!treeSync)DeleteLocationAnalysis(analysisTree.RequestedDeleteDataPath);};
            analysisTree.ProjectDataDeleteRequested+=(s,e)=>{if(!treeSync)DeleteProjectLocationDataFiles(analysisTree.RequestedDeleteDataPaths);};
            analysisTree.MapAcceptanceChanged+=(s,e)=>{
                if(treeSync)return;int row=analysisTree.LastChangedMatrixRowIndex;bool value=analysisTree.LastChangedAccepted;
                int prior=ResultChangeSequence;
                if(SelectSourceRow(row)){if(value)AcceptCurrent();else DeleteCurrent();}
                // A committed review already publishes ResultsChanged once. If
                // it was rejected, restore the authoritative row rather than
                // leaving the tree's optimistic icon as a false acceptance.
                if(prior==ResultChangeSequence)SyncAnalysisTree(false);
            };
            ResultsChanged+=(s,e)=>SyncAnalysisTree(false);
            analysisTree.PendingSelectionChanged+=(s,e)=>RefreshWorkspaceUi();
            Disposed+=(s,e)=>{CancelLocationOverviewLoad();analysisGeneration++;CloseAnalysisProgress();if(analysisCancellation!=null){analysisCancellation.Cancel();analysisCancellation.Dispose();analysisCancellation=null;}};
            InitializeOverviewMeasurement();
        }
        void UpdateWorkspaceLanguage()
        {
            if(analysisTree==null)return;
            bool prior=treeSync;treeSync=true;try{analysisTree.LabelTranslator=(en,zh)=>ReviewWorkspaceText.Text(languageIndex,en,zh);analysisTree.EnglishVersion=english;}finally{treeSync=prior;}
            locationOverview.LabelTranslator=(en,zh)=>ReviewWorkspaceText.Text(languageIndex,en,zh);locationOverview.EnglishVersion=english;
            UpdateLocationOverviewLoadingText();
            analysisSettingsButton.Text=ReviewAnalysisSettingsText.Text(languageIndex,"Analysis settings","分析设置");
            clearAnalysisButton.Text=ReviewWorkspaceText.Text(languageIndex,"Delete all","全部删除");automaticAnalysisButton.Text=ReviewWorkspaceText.Text(languageIndex,"Automatic analysis","自动分析");manualAnalysisButton.Text=ReviewWorkspaceText.Text(languageIndex,"Manual analysis","手动分析");
            analysisDiagnosticsButton.Text=ReviewWorkspaceText.Text(languageIndex,"Diagnostics","诊断");
            localizationComparisonButton.Text=L("Location cross-check","定位结果复核");
            localizationComparisonButton.AccessibleDescription=L("Cross-check the selected record using other methods; original results and review decisions are retained.","使用其他方法复核当前记录的定位结果，不覆盖原结果和已作出的确认。");
            manualThresholdBox.Text=ReviewWorkspaceText.Text(languageIndex,"Manual threshold (unchecked: automatic)","手动阈值（未勾选：自动）");thresholdAllBox.Text=ReviewWorkspaceText.Text(languageIndex,"Apply to all files","应用于所有");
            overviewSnap.Text=L("Snap to joints","近似接头");overviewReverse.Text=L("Reverse","定位反转");overviewCompensated.Text=ReviewWorkspaceText.Text(languageIndex,"Compensated charge","补偿放电量");
            UpdateCompactTabLanguage();
            if(overviewEndpointButton!=null){overviewEndpointButton.Text=L("Near/far ends","近远端识别");overviewEndpointButton.AccessibleDescription=L("Inspect endpoint MAPs extracted from this LOC file without changing accepted results.","识别本 LOC 文件已提取的端部 MAP，不修改已接受结果。");}
        }
        void RefreshWorkspaceUi()
        {
            if(analysisTree==null)return;
            string blocked=AnalysisStartBlockReason;
            // Keep the commands reachable so blocked users receive a reason and,
            // for learning mode, an explicit opt-in choice to leave that mode.
            automaticAnalysisButton.Enabled=manualAnalysisButton.Enabled=true;
            ReviewTheme.ButtonRole(automaticAnalysisButton,blocked.Length==0);
            automaticAnalysisButton.AccessibleDescription=manualAnalysisButton.AccessibleDescription=blocked;
            clearAnalysisButton.Enabled=SourceRowCount>0||analysisBusy||overviewLoading||analysisTree.GetAnalyzedDataPaths().Length>0;
            analysisProgress.Text=analysisOutcomeWarning;analysisProgress.Visible=!analysisBusy&&analysisOutcomeWarning.Length>0;
            UpdateAnalysisDiagnosticHints();
            pdmap.Enabled=spectrum.Enabled=analysisTree.Enabled=!analysisBusy;
            analysisSettingsButton.Enabled=overviewHost.Enabled=!analysisBusy;
            locationOverview.Enabled=overviewOptions.Enabled=!overviewLoading;
            localizationComparisonButton.Enabled=!analysisBusy;
            RefreshAiLearningUi();
            overviewHost.Visible=locationOverviewMode;if(locationOverviewMode)overviewHost.BringToFront();
            if(filterChoices!=null&&filterChoices.Length>0)filterChoices[0].Parent.Visible=!locationOverviewMode;
            filterParametersHost.Visible=!locationOverviewMode&&SelectedFilterMode!=0;
            statisticsEnabledBox.Visible=strengthLabel.Visible=statisticsSlider.Visible=!locationOverviewMode;
            autoDisplayBox.Visible=!locationOverviewMode;
            displayRecommendationLabel.Visible=displayAdviceDetails.Visible=false;
            // Review commands and numerical waveform details have no meaning in whole-file mode.
            actions.Visible=!locationOverviewMode;
            actions.Parent.Visible=!compactWorkspace||!compactFilterSelected;
            side.Visible=!compactWorkspace||compactFilterSelected;
            UpdateWorkspaceActionHeight();
        }
        void LoadTreeProject()
        {
            CancelLocationOverviewLoad();
            if(analysisTree==null)return;treeSync=true;
            try{
                analysisOutcomeWarning="";
                treeMapFocusRequested=false;
                manualReviewAutoAdvance=false;
                fileThresholds.Clear();globalThreshold=new ThresholdChoice();thresholdAll=false;spectrumScopePath="";overviewPath="";locationOverview.ClearData();
                // A new project must never show the preceding project's accepted rows.
                if(SourceRowCount>0)ForceReloadDataMatrices(new double[0,36],new double[0,0]);
                string root=File.Exists(projectPath)?Path.GetDirectoryName(Path.GetFullPath(projectPath)):projectPath;
                bool ok=string.IsNullOrEmpty(root)?analysisTree.LoadRecords(new string[0],new int[0],new double[0],new int[0],new double[0]):analysisTree.LoadProject(root);
                if(!ok)LastError=analysisTree.LastError;
            }finally{treeSync=false;}
            SyncAnalysisTree(true);ApplyTreeSpectrumVisibility();
        }
        public bool SetProjectLocationIds(string[] paths,double[] ids)
        {bool ok;treeSync=true;try{ok=analysisTree.SetDataIds(paths,ids);}finally{treeSync=false;}if(!ok){LastError=analysisTree.LastError;return false;}SyncAnalysisTree(true);return true;}
        void SyncAnalysisTree(bool rebuild)
        {
            if(analysisTree==null||treeSync)return;treeSync=true;
            try{
                // Matrix-only callers keep a navigable tree without pretending a virtual path is a real LOC.
                if(string.IsNullOrEmpty(projectPath)&&(rebuild||analysisTree.GetDataPaths().Length==0)){
                    var groups=Enumerable.Range(0,SourceRowCount).GroupBy(r=>working[r,0].ToString("R",CultureInfo.InvariantCulture)+"|"+working[r,1].ToString("R",CultureInfo.InvariantCulture)).Select(g=>g.First()).ToArray();
                    analysisTree.LoadRecords(groups.Select(r=>Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"memory-"+working[r,0]+"-"+working[r,1]+".loc")).ToArray(),groups.Select(r=>(int)working[r,0]).ToArray(),groups.Select(r=>working[r,2]).ToArray(),groups.Select((r,i)=>i+1).ToArray(),groups.Select(r=>working[r,1]).ToArray());
                }
                if(!analysisTree.SetParameterMatrixAndReviewStates(working,reviewed,removed,acceptanceConfirmed))AnalysisStatus=L("Tree identity mapping required: ","树形需要正确的文件 ID 映射：")+analysisTree.LastError;
                // Loading a matrix selects a waveform internally, but must not
                // expand the initial project/session tree. Only a deliberate
                // MAP selection or previous/next action opens its branch.
                if(treeMapFocusRequested&&!locationOverviewMode&&selected>=0&&
                    (analysisTree.SelectedNodeType!=4||analysisTree.SelectedMatrixRowIndex!=selected))analysisTree.SelectSourceRow(selected);
            }finally{treeSync=false;}
        }
        void SyncTreeSelection()
        {if(analysisTree==null||treeSync||selected<0)return;treeMapFocusRequested=true;treeSync=true;try{analysisTree.SelectSourceRow(selected);}finally{treeSync=false;}}
        void ShowPdMapWorkspace(){CancelLocationOverviewLoad();locationOverviewMode=false;RefreshWorkspaceUi();}
        public bool ShowLocationOverview(string path)
        {
            if(analysisBusy)return false;
            if(string.IsNullOrEmpty(path))return false;
            CancelLocationOverviewLoad();
            if(!String.Equals(path,overviewPath,StringComparison.OrdinalIgnoreCase)||locationOverview.SampleCount==0){
                string root=File.Exists(projectPath)?Path.GetDirectoryName(Path.GetFullPath(projectPath)):projectPath;
                if(!locationOverview.LoadFile(path,root)){overviewPath=path;LastError=locationOverview.LastError;locationOverviewMode=true;RefreshReviewUi();return false;}
                overviewPath=path;
            }
            RefreshOverviewMeasurementGeometry(path);
            locationOverviewMode=true;SyncThresholdUi();RefreshReviewUi();return true;
        }
        ThresholdChoice ThresholdFor(string path)
        {if(thresholdAll)return globalThreshold;ThresholdChoice result;if(!fileThresholds.TryGetValue(path??"",out result)){result=new ThresholdChoice();fileThresholds[path??""]=result;}return result;}
        double OverviewThresholdMaximum(){return Math.Max(1,locationOverview.MaximumAmplitudePc);}
        void SyncThresholdUi()
        {
            if(locationOverview==null)return;thresholdSync=true;
            try{var choice=ThresholdFor(overviewPath);if(AiLearningMode)choice.Manual=true;ThresholdChoice measured;fileThresholds.TryGetValue(overviewPath,out measured);manualThresholdBox.Checked=choice.Manual;manualThresholdBox.Enabled=!AiLearningMode;thresholdAllBox.Checked=thresholdAll;
                if(choice.Manual)locationOverview.SetThreshold(choice.Value,false);
                else if(measured!=null&&Finite(measured.Actual)&&measured.ActualMultiplier==AnalysisAutomaticThresholdMultiplier)locationOverview.SetThreshold(measured.Actual,false);
                else locationOverview.SetAutomaticThresholdPreview(AnalysisAutomaticThresholdMultiplier);
                double value=locationOverview.CurrentThresholdPc;thresholdNumber.Value=(decimal)Math.Max(0,Math.Min((double)thresholdNumber.Maximum,Finite(value)?value:choice.Value));
                thresholdNumber.Enabled=thresholdSlider.Enabled=locationOverview.SampleCount>0&&choice.Manual;
                thresholdSlider.Value=(int)Math.Round(Math.Max(0,Math.Min(1000,(double)thresholdNumber.Value/OverviewThresholdMaximum()*1000)));
                overviewSnap.Checked=pdmap.ApproximateCablePositions;overviewReverse.Checked=ReversePositionDisplay;overviewCompensated.Checked=spectrum.UseCompensatedCharge;
            }finally{thresholdSync=false;}
        }
        void SaveThresholdUi()
        {if(thresholdSync)return;var choice=ThresholdFor(overviewPath);choice.Manual=AiLearningMode||manualThresholdBox.Checked;choice.HumanConfirmed=choice.Manual;choice.Value=(double)thresholdNumber.Value;choice.Actual=double.NaN;SyncThresholdUi();}
        public void SetFileAnalysisThreshold(string path,bool manual,double value)
        {if(!Finite(value)||value<0||value>1e9)throw new ArgumentOutOfRangeException("value");bool effectiveManual=AiLearningMode||manual;fileThresholds[path??""]=new ThresholdChoice{Manual=effectiveManual,HumanConfirmed=effectiveManual,Value=value};if(String.Equals(path,overviewPath,StringComparison.OrdinalIgnoreCase))SyncThresholdUi();}
        public void ClearSpectrumFileScope(){spectrumScopePath="";ApplyTreeSpectrumVisibility();}
        public bool ShowLocationGroup(string path)
        {if(!ShowLocationOverview(path))return false;spectrumScopePath=path;ApplyTreeSpectrumVisibility();return true;}
        void ApplyTreeSpectrumVisibility()
        {
            if(spectrum==null)return;var mask=(bool[])reviewMask.Clone();
            if(!string.IsNullOrEmpty(spectrumScopePath)&&analysisTree!=null){
                string[] paths=analysisTree.GetDataPaths();double[,] catalog=analysisTree.GetDataCatalog();int i=Array.FindIndex(paths,p=>String.Equals(p,spectrumScopePath,StringComparison.OrdinalIgnoreCase));
                for(int r=0;r<mask.Length;r++)mask[r]&=i>=0&&working[r,0]==catalog[i,0]&&working[r,1]==catalog[i,1];
            }
            spectrum.SetReviewVisibility(mask.Length==SourceRowCount?mask:null);
        }
        public bool ClearAllAnalysis()
        {
            CancelLocationOverviewLoad();
            analysisGeneration++;if(analysisCancellation!=null)analysisCancellation.Cancel();analysisBusy=false;
            CloseAnalysisProgress();
            bool old=treeSync;treeSync=true;
            try{
                fileThresholds.Clear();globalThreshold=new ThresholdChoice();thresholdAll=false;overviewPath="";spectrumScopePath="";locationOverview.ClearData();locationOverviewMode=true;treeMapFocusRequested=false;
                if(!ForceReloadDataMatrices(new double[0,36],new double[0,0]))return false;
                inputGeometryAlreadyCompensated=false;ConnectingCableLengthM=0;ReversePositionDisplay=false;pdmap.ApproximateCablePositions=!AiLearningMode;spectrum.UseCompensatedCharge=false;
                analysisTree.ResetAnalysisForPaths(analysisTree.GetDataPaths());
                analysisTree.SetParameterMatrix(new double[0,36]);
                analysisTree.CollapseAll();
                // Clearing analysis data must not undo the operator's saved settings.
                analysisOutcomeWarning="";AnalysisStatus="";AnalysisCompletedFileCount=0;
                ResultChangeSequence++;NotifyReviewHost(ResultsChanged);SyncThresholdUi();RefreshReviewUi();return true;
            }finally{treeSync=old;}
        }
        public bool DeleteLocationAnalysis(string path)
        {
            if(analysisBusy){LastError=L("Wait for analysis or delete all to cancel it.","请等待分析完成，或全部删除以取消分析。");return false;}
            var paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();int i=Array.FindIndex(paths,item=>String.Equals(item,path,StringComparison.OrdinalIgnoreCase));if(i<0)return false;
            var rows=Enumerable.Range(0,SourceRowCount).Where(r=>working[r,0]!=catalog[i,0]||working[r,1]!=catalog[i,1]).ToArray();
            double[,] p,w;SubsetWorkspace(rows,out p,out w);treeSync=true;
            try{if(!ReloadWithRetainedReview(p,w,rows))return false;analysisTree.ResetAnalysisForPaths(new[]{path});if(!analysisTree.SetParameterMatrix(working)){LastError=analysisTree.LastError;return false;}SyncReviewedTree();}
            finally{treeSync=false;}
            fileThresholds.Remove(path);spectrumScopePath="";ApplyTreeSpectrumVisibility();ResultChangeSequence++;NotifyReviewHost(ResultsChanged);ShowLocationOverview(path);return true;
        }
        /// <summary>
        /// Permanently delete selected location data files, then refresh the tree
        /// and analysis workspace that depend on those files — without resetting
        /// unrelated review settings.
        /// </summary>
        public bool DeleteProjectLocationDataFiles(string[] paths)
        {
            if(analysisBusy||overviewLoading){LastError=L("Wait for analysis to finish before deleting data files.","请等待分析完成后再删除数据文件。");return false;}
            if(paths==null||paths.Length==0)return false;
            string root=File.Exists(projectPath)?Path.GetDirectoryName(Path.GetFullPath(projectPath)):projectPath;
            if(String.IsNullOrWhiteSpace(root)){LastError=L("No project is loaded.","未加载项目。");return false;}
            var remove=new HashSet<string>(paths.Where(p=>!String.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath),StringComparer.OrdinalIgnoreCase);
            var treePaths=analysisTree.GetDataPaths();
            var catalog=analysisTree.GetDataCatalog();
            var removeKeys=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<treePaths.Length;i++)
            {
                string full;try{full=Path.GetFullPath(treePaths[i]);}catch{continue;}
                if(remove.Contains(full))removeKeys.Add(((int)catalog[i,0]).ToString(CultureInfo.InvariantCulture)+"|"+catalog[i,1].ToString("R",CultureInfo.InvariantCulture));
            }
            var rows=Enumerable.Range(0,SourceRowCount).Where(r=>{
                string key=((int)working[r,0]).ToString(CultureInfo.InvariantCulture)+"|"+working[r,1].ToString("R",CultureInfo.InvariantCulture);
                return !removeKeys.Contains(key);
            }).ToArray();
            CancelLocationOverviewLoad();
            treeSync=true;
            try
            {
                if(rows.Length!=SourceRowCount)
                {
                    double[,] p,w;SubsetWorkspace(rows,out p,out w);
                    if(!ReloadWithRetainedReview(p,w,rows))return false;
                }
                HVX.ProjectModel.ProjectDataFileDeletion.Delete(root,remove);
                if(!analysisTree.LoadProject(root)){LastError=analysisTree.LastError;return false;}
                if(!analysisTree.SetParameterMatrixAndReviewStates(working,reviewed,removed,acceptanceConfirmed))
                {LastError=analysisTree.LastError;return false;}
                SyncReviewedTree();
            }
            catch(Exception ex){LastError=ex.Message;AnalysisStatus=L("Delete failed: ","删除失败：")+ex.Message;return false;}
            finally{treeSync=false;}
            foreach(string path in remove)fileThresholds.Remove(path);
            if(PathListed(spectrumScopePath,remove))spectrumScopePath="";
            if(PathListed(overviewPath,remove)){overviewPath="";locationOverview.ClearData();}
            ApplyTreeSpectrumVisibility();
            ResultChangeSequence++;NotifyReviewHost(ResultsChanged);
            AnalysisStatus=L("Deleted ","已删除 ")+remove.Count+L(" location data file(s) (irreversible)."," 个定位数据文件（不可恢复）。");
            // Only enablement/status that depend on remaining data — not a full review chrome rebuild.
            RefreshWorkspaceUi();
            return true;
        }
        static bool PathListed(string path,HashSet<string> set)
        {
            if(String.IsNullOrWhiteSpace(path)||set==null||set.Count==0)return false;
            try{return set.Contains(Path.GetFullPath(path));}catch{return false;}
        }
        void SubsetWorkspace(int[] rows,out double[,] p,out double[,] w)
        {
            int n=SourceRowCount,blocks=n>0&&matching.GetLength(0)==3L*n?3:1;
            p=new double[rows.Length,working.GetLength(1)];w=new double[rows.Length*blocks,matching.GetLength(1)];
            for(int i=0;i<rows.Length;i++){int row=rows[i];for(int c=0;c<p.GetLength(1);c++)p[i,c]=working[row,c];int map=matching.GetLength(0)==n||blocks==3?row:(int)original[row,9];
                for(int b=0;b<blocks;b++)for(int c=0;c<w.GetLength(1);c++)w[i+b*rows.Length,c]=matching[map+b*n,c];}
        }
    }
}
