using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;
using Xiongxin.OWTS.Localization;

namespace HVX.PdReview
{
    /// <summary>Read-only saved-analysis spectra. Only explicit learning actions write a separate pending sample store.</summary>
    public sealed partial class CompetitionDefectRecognitionControl : UserControl
    {
        readonly UnifiedSpectrumControl spectrum=new UnifiedSpectrumControl(true,true){Name="CompetitionLeftSpectrum",Dock=DockStyle.Fill,AcceptedOnly=true};
        readonly ComboBox analyses=new ComboBox{Name="CompetitionSavedAnalysis",DropDownStyle=ComboBoxStyle.DropDownList,Width=380,DisplayMember="DisplayName"};
        readonly Label status=new Label{Name="CompetitionStatus",AutoSize=false};
        readonly Label archiveCaption=new Label{Name="CompetitionArchiveCaption",AutoSize=true,Anchor=AnchorStyles.Left};
        readonly Label summary=new Label{Name="CompetitionSummary",AutoSize=false},help=new Label{Name="CompetitionHelp",AutoSize=false},statusCaption=new Label{Name="CompetitionStatusCaption",AutoSize=false};
        readonly Panel viewport=new Panel{Name="CompetitionViewport",Dock=DockStyle.Fill,AutoScroll=true};
        readonly Panel content=new Panel{Name="CompetitionContent"},statusCard=new Panel{Name="CompetitionStatusCard"};
        readonly TableLayoutPanel sources=new TableLayoutPanel{Name="CompetitionSourceCard",ColumnCount=3,RowCount=1};
        readonly FlowLayoutPanel actions=new FlowLayoutPanel{Name="CompetitionActions",WrapContents=true,AutoSize=false};
        readonly ToolTip details=new ToolTip{AutoPopDelay=15000};
        readonly Button load=new HVX.Presentation.R2RoundedButton{Name="CompetitionLoad",AutoSize=true},recognize=new HVX.Presentation.R2RoundedButton{Name="CompetitionRecognize",AutoSize=true},
            add=new HVX.Presentation.R2RoundedButton{Name="CompetitionLearn",AutoSize=true},undo=new HVX.Presentation.R2RoundedButton{Name="CompetitionUndo",AutoSize=true},reset=new HVX.Presentation.R2RoundedButton{Name="CompetitionClear",AutoSize=true};
        readonly CheckBox accepted=new CheckBox{Name="CompetitionAcceptedOnly",AutoSize=true,Checked=true};
        readonly SemaphoreSlim archiveGate=new SemaphoreSlim(1,1);
        readonly object learningCommitGate=new object();
        string project="",model="",lastError="",loadedId="";
        volatile bool aiEnabled=true;
        int language,generation,loadGeneration;
        bool loading,workingAi,syncing;
        double[,] parameters=new double[0,36];
        ReviewTask12Runtime runtime;
        Task aiWorker;
        internal Func<string,string> LearningRootProviderForTests;
        DischargeRecognitionResult recognition;
        SavedAnalysisInfo[] available=new SavedAnalysisInfo[0];
        Font headingFont;
        bool arranging;
        public CompetitionDefectRecognitionControl()
        {
            var ownFont=new Font("Microsoft YaHei UI",10.5f,FontStyle.Regular);Font=ownFont;Disposed+=(s,e)=>ownFont.Dispose();BackColor=ReviewTheme.Workspace;ForeColor=ReviewTheme.Ink;
            MinimumSize=new Size(650,430);
            viewport.BackColor=content.BackColor=BackColor;sources.BackColor=statusCard.BackColor=Color.White;actions.BackColor=BackColor;
            sources.Padding=new Padding(12,8,12,8);sources.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));sources.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));sources.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            archiveCaption.Margin=new Padding(0,0,16,0);analyses.Dock=DockStyle.Fill;analyses.Margin=new Padding(0,2,12,2);analyses.IntegralHeight=false;analyses.DropDownHeight=280;load.Margin=new Padding(0);load.Anchor=AnchorStyles.Right;
            sources.Controls.Add(archiveCaption,0,0);sources.Controls.Add(analyses,1,0);sources.Controls.Add(load,2,0);
            analyses.SelectedIndexChanged+=(s,e)=>{analyses.AccessibleDescription=analyses.Text;details.SetToolTip(analyses,analyses.Text);};
            analyses.DropDown+=(s,e)=>{int itemWidth=analyses.Items.Cast<SavedAnalysisInfo>().Select(item=>TextRenderer.MeasureText(item.DisplayName,analyses.Font).Width+32).DefaultIfEmpty(analyses.Width).Max();analyses.DropDownWidth=Math.Min(Screen.FromControl(this).WorkingArea.Width-40,Math.Max(analyses.Width,itemWidth));};
            actions.Controls.AddRange(new Control[]{recognize,add,undo,reset,accepted});
            statusCard.Controls.AddRange(new Control[]{statusCaption,status,summary});
            InitializeLinkedSpectra();
            content.Controls.AddRange(new Control[]{sources,actions,statusCard,spectra,help});viewport.Controls.Add(content);Controls.Add(viewport);
            load.Click+=async(s,e)=>{var item=analyses.SelectedItem as SavedAnalysisInfo;if(item!=null)await LoadSavedAnalysisAsync(item.Id);};
            recognize.Click+=async(s,e)=>{try{await RecognizeCurrentDischargeAsync();}catch(Exception ex){Failure(ex);}};
            add.Click+=(s,e)=>ShowLearningDialog();
            undo.Click+=async(s,e)=>await LearningActionAsync("undo",null,null,null);
            reset.Click+=async(s,e)=>{if(MessageBox.Show(FindForm(),T("Archive this module's pending samples? History and the model are preserved.","清空本模块待训练样本？将保留历史备份，不改变模型。"),T("Clear pending samples","清空待训练样本"),MessageBoxButtons.OKCancel,MessageBoxIcon.Question)==DialogResult.OK)await LearningActionAsync("clear",null,null,null);};
            accepted.CheckedChanged+=(s,e)=>{spectrum.AcceptedOnly=secondarySpectrum.AcceptedOnly=accepted.Checked;ViewChanged();};
            Resize+=(s,e)=>ArrangeWorkspace();FontChanged+=(s,e)=>{ApplyTypography();ArrangeWorkspace();};
            ApplyTypography();
            ApplyLanguage();RefreshEnabled();
        }
        void ApplyTypography()
        {
            // Controls may measure sibling labels while a Font assignment
            // triggers layout. Retire the old owned font only after every
            // control has stopped referencing it.
            var priorHeadingFont=headingFont;headingFont=new Font(Font,FontStyle.Bold);
            archiveCaption.Font=statusCaption.Font=recognize.Font=headingFont;spectrum.Font=secondarySpectrum.Font=analyses.Font=status.Font=summary.Font=help.Font=accepted.Font=Font;
            foreach(var button in new[]{load,add,undo,reset,clearSelection})button.Font=Font;
            foreach(var button in new[]{load,recognize,add,undo,reset,clearSelection})StyleButton(button,button==recognize,button==reset);
            accepted.Padding=new Padding(8,0,0,0);accepted.Margin=new Padding(10,0,0,4);accepted.MinimumSize=new Size(0,Math.Max(38,Font.Height+18));accepted.ForeColor=ReviewTheme.Ink;
            summary.ForeColor=help.ForeColor=ReviewTheme.SecondaryInk;status.ForeColor=ReviewTheme.Ink;
            SyncSharedPhases();
            if(priorHeadingFont!=null)priorHeadingFont.Dispose();
        }
        static void StyleButton(Button button,bool primary,bool danger)
        {
            button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=1;button.UseVisualStyleBackColor=false;button.AutoSize=true;button.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            button.Padding=new Padding(13,6,13,6);button.MinimumSize=new Size(0,Math.Max(primary?40:38,button.Font.Height+18));button.Margin=new Padding(0,0,8,4);
            button.BackColor=primary?Color.FromArgb(0,123,155):danger?Color.FromArgb(255,247,247):Color.White;
            button.ForeColor=primary?Color.White:danger?Color.FromArgb(161,66,66):ReviewTheme.Ink;
            button.FlatAppearance.BorderColor=primary?button.BackColor:danger?Color.FromArgb(230,195,195):ReviewTheme.Border;
            button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(0,107,136):danger?Color.FromArgb(252,231,231):ReviewTheme.ActiveSoft;
            button.FlatAppearance.MouseDownBackColor=primary?Color.FromArgb(0,88,115):danger?Color.FromArgb(245,213,213):Color.FromArgb(220,232,241);
        }
        void ArrangeWorkspace()
        {
            if(arranging||viewport.ClientSize.Width<=0)return;arranging=true;
            try
            {
                // Reserve the scrollbar column consistently, so wrapping does not
                // oscillate when a long result or a large font makes scrolling necessary.
                int margin=12,gap=8;
                int pairMinimum=LinkedSpectra.Sum(plot=>plot.MinimumSize.Width+plot.Margin.Horizontal);
                int width=Math.Max(pairMinimum+margin*2,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth),inner=Math.Max(80,width-margin*2);
                content.Width=width;
                int top=margin,sourceHeight=ArrangeArchiveSource(inner);
                sources.SetBounds(margin,top,inner,sourceHeight);sources.PerformLayout();top+=sourceHeight+gap;
                actions.Width=inner;int actionsHeight=actions.GetPreferredSize(new Size(inner,0)).Height;actions.SetBounds(margin,top,inner,Math.Max(40,actionsHeight));actions.PerformLayout();top+=actions.Height+gap;
                int textWidth=Math.Max(40,inner-24),captionHeight=TextRenderer.MeasureText(statusCaption.Text,statusCaption.Font,new Size(textWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
                int statusHeight=TextRenderer.MeasureText(status.Text,status.Font,new Size(textWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
                int summaryHeight=TextRenderer.MeasureText(summary.Text,summary.Font,new Size(textWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height;
                statusCaption.SetBounds(12,9,textWidth,captionHeight+2);status.SetBounds(12,statusCaption.Bottom+4,textWidth,statusHeight+2);summary.SetBounds(12,status.Bottom+5,textWidth,summaryHeight+2);
                statusCard.SetBounds(margin,top,inner,(summary.Visible?summary.Bottom:status.Bottom)+9);top=statusCard.Bottom+gap;
                int helpHeight=TextRenderer.MeasureText(help.Text,help.Font,new Size(inner,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPadding).Height+4;
                int plotMinimum=LinkedSpectra.Max(plot=>plot.MinimumSize.Height)+sharedPhases.GetPreferredSize(new Size(inner,0)).Height;
                int plotHeight=Math.Max(Math.Max(300,plotMinimum),viewport.ClientSize.Height-top-gap-helpHeight-margin);
                spectra.SetBounds(margin,top,inner,plotHeight);top=spectra.Bottom+gap;help.SetBounds(margin,top,inner,helpHeight);
                content.Height=help.Bottom+margin;viewport.AutoScrollMinSize=new Size(width,content.Height);
            }
            finally{arranging=false;}
        }
        int ArrangeArchiveSource(int width)
        {
            // A tooltip supplements a truncated selected name; it cannot replace
            // the actual drop-down. Move the caption above the selection before
            // large text or a narrow viewport can collapse the clickable field.
            int captionWidth=TextRenderer.MeasureText(archiveCaption.Text,archiveCaption.Font).Width+archiveCaption.Margin.Horizontal;
            int loadWidth=load.GetPreferredSize(Size.Empty).Width,minimumSelection=Math.Max(200,Font.Height*10);
            bool stacked=width-sources.Padding.Horizontal<captionWidth+loadWidth+minimumSelection+12;
            int rowHeight=Math.Max(analyses.PreferredHeight+analyses.Margin.Vertical,load.GetPreferredSize(Size.Empty).Height);
            sources.SuspendLayout();
            try
            {
                sources.RowCount=stacked?2:1;sources.RowStyles.Clear();
                sources.ColumnStyles[0].SizeType=stacked?SizeType.Percent:SizeType.AutoSize;sources.ColumnStyles[0].Width=stacked?100:0;
                sources.ColumnStyles[1].SizeType=stacked?SizeType.Absolute:SizeType.Percent;sources.ColumnStyles[1].Width=stacked?0:100;
                sources.SetColumnSpan(archiveCaption,stacked?3:1);sources.SetColumnSpan(analyses,stacked?2:1);
                sources.SetCellPosition(archiveCaption,new TableLayoutPanelCellPosition(0,0));
                sources.SetCellPosition(analyses,new TableLayoutPanelCellPosition(stacked?0:1,stacked?1:0));
                sources.SetCellPosition(load,new TableLayoutPanelCellPosition(2,stacked?1:0));
                archiveCaption.MaximumSize=stacked?new Size(Math.Max(80,width-sources.Padding.Horizontal),0):Size.Empty;
                int captionHeight=stacked?archiveCaption.GetPreferredSize(new Size(Math.Max(80,width-sources.Padding.Horizontal),0)).Height+8:0;
                sources.RowStyles.Add(new RowStyle(stacked?SizeType.Absolute:SizeType.Percent,stacked?captionHeight:100));
                if(stacked)sources.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                return Math.Max(56,sources.Padding.Vertical+captionHeight+rowHeight);
            }
            finally{sources.ResumeLayout(false);}
        }
        string T(string en,string zh){return UiLanguage.Text(language,zh,en);}
        [DefaultValue(0)]public int LanguageIndex{get{return language;}set{int next=UiLanguage.ResolveIndex(value);if(next==language)return;language=next;spectrum.LanguageIndex=secondarySpectrum.LanguageIndex=next;ViewChanged();ApplyLanguage();}}
        [DefaultValue("")]public string ProjectPath{get{return project;}set{LoadProject(value);}}
        [DefaultValue(true)]public bool EnableAiFeatures
        {get{return aiEnabled;}set{lock(learningCommitGate){if(aiEnabled==value)return;aiEnabled=value;Interlocked.Increment(ref generation);}recognition=null;SetStatus(value?ViewSummary():T("AI features are disabled in this module; viewing remains available.","本模块已关闭 AI 功能；仍可查看谱图。"));RefreshEnabled();}}
        [DefaultValue("")]public string ModelLibraryPath{get{return model;}set{string next=(value??"").Trim();if(next==model)return;lock(learningCommitGate){model=next;Interlocked.Increment(ref generation);}recognition=null;RefreshEnabled();}}
        [Browsable(false)]public bool IsBusy{get{return loading||workingAi;}}
        [Browsable(false)]public string LastError{get{return lastError;}}
        [Browsable(false)]public string LoadedAnalysisId{get{return loadedId;}}
        [Browsable(false)]public int SourceRowCount{get{return parameters.GetLength(0);}}
        [Browsable(false)]public int[] VisibleSourceRowIndices{get{return spectrum.VisibleSourceRowIndices;}}
        [Browsable(false)]public DischargeRecognitionResult CurrentDischargeRecognition{get{return aiEnabled?recognition:null;}}
        [Browsable(false)]public SavedAnalysisInfo[] SavedAnalyses{get{return (SavedAnalysisInfo[])available.Clone();}}
        [Browsable(false)]public string[] SpectrumModes{get{return spectrum.GetVisibleModeIndices().Select(i=>new[]{"Q-L","N-L","Q-PHI","N-PHI","F-T","U-Q-L","U-N-L","PHI-Q-L","PHI-N-L","n-Q-U","n-N-U","PRPD","L-PRPD"}[i]).ToArray();}}
        public event EventHandler StateChanged;
        public bool LoadProject(string path)
        {
            string next="";try{if(!String.IsNullOrWhiteSpace(path))next=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}catch(Exception ex){LoadProject("");Failure(ex);return false;}
            if(String.Equals(project,next,StringComparison.OrdinalIgnoreCase))return next.Length>0&&Directory.Exists(next);
            lock(learningCommitGate){project=next;Interlocked.Increment(ref generation);Interlocked.Increment(ref loadGeneration);}
            loading=false;ClearData();available=new SavedAnalysisInfo[0];analyses.Items.Clear();
            try
            {
                spectrum.ProjectPath=secondarySpectrum.ProjectPath=project;
                if(project.Length>0&&Directory.Exists(project))available=PdReviewControl.CompetitionArchives(project);
                analyses.Items.AddRange(available.Cast<object>().ToArray());if(available.Length>0)analyses.SelectedIndex=0;
                lastError="";SetStatus(project.Length==0?T("Select a project in the main program.","请在主程序选择项目。"):available.Length==0?T("No valid saved analyses in the current project.","当前项目没有有效的已保存分析。"):T("Choose a saved analysis.","请选择已保存分析。"));RefreshEnabled();
                return project.Length>0&&Directory.Exists(project);
            }
            catch(Exception ex){Failure(ex);RefreshEnabled();return false;}
        }
        void ClearData(){syncing=true;try{parameters=new double[0,36];recognition=null;loadedId="";foreach(var plot in LinkedSpectra){plot.LoadDataMatrix(parameters);plot.ExitRegionStatistics();plot.SetReviewVisibility(null);}ResetLinkedFilters();}finally{syncing=false;}}
        int InvalidateContext(){lock(learningCommitGate)return Interlocked.Increment(ref generation);}
        void ViewChanged(){if(syncing)return;InvalidateContext();recognition=null;SetStatus(ViewSummary());RefreshEnabled();}
        string ViewSummary(){return T("Visible / saved pulses: ","可见 / 已保存脉冲：")+SharedSourceRowIndices.Length+" / "+SourceRowCount+"    ·    "+spectrum.DisplayModeName+"    ·    "+T("Original analysis is read-only","原分析只读");}
        void SetStatus(string text)
        {
            summary.Text="";summary.Visible=false;statusCaption.Text=recognition==null?T("WORK STATUS","工作状态"):T("RECOGNITION RESULT","识别结果");
            status.Text=text==ViewSummary()?(SourceRowCount==0?T("Choose an analysis above.","请在上方选择分析。"):SharedSourceRowIndices.Length==0?T("No pulses selected.","当前没有选中的脉冲。"):T("Ready. Select Recognize defect.","就绪，请点击缺陷识别。")):text;
            status.ForeColor=!String.IsNullOrEmpty(lastError)&&text==lastError?ReviewTheme.Danger:ReviewTheme.Ink;ArrangeWorkspace();var handler=StateChanged;if(handler!=null)handler(this,EventArgs.Empty);
        }
        void ApplyLanguage()
        {
            archiveCaption.Text=T("ANALYSIS ARCHIVE","分析存档");load.Text=T("Load analysis","加载分析");recognize.Text=T("Recognize defect","缺陷识别");add.Text=T("Add verified sample","加入人工确认样本");undo.Text=T("Undo sample","撤销样本");reset.Text=T("Clear pending samples","清空待训练样本");accepted.Text=T("Show accepted pulses only","仅显示已接受脉冲");
            clearSelection.Text=T("Show all pulses","显示全部脉冲");help.Text=T("Draw a region, then right-click Statistics selected region. Both charts use the selected pulses.","先框选，再右键点击“统计选中区域”。两图同步显示所选脉冲。");SyncSharedPhases();SetStatus(ViewSummary());
        }
        void RefreshEnabled(){bool hasProject=project.Length>0&&Directory.Exists(project);analyses.Enabled=hasProject;load.Enabled=hasProject&&analyses.Items.Count>0;accepted.Enabled=SourceRowCount>0;sharedPhases.Enabled=SourceRowCount>0;spectrum.Enabled=secondarySpectrum.Enabled=hasProject;bool learn=hasProject&&aiEnabled&&!workingAi&&!loading;recognize.Enabled=learn&&SharedSourceRowIndices.Length>=3;add.Enabled=recognize.Enabled;undo.Enabled=reset.Enabled=learn;}
        void Failure(Exception ex){if(ex is OutOfMemoryException||ex is AccessViolationException)throw ex;lastError=ex.Message;SetStatus(T("Unable to complete this operation. Please try again.","操作未完成，请重试。"));}
        bool Current(int token){return !IsDisposed&&!Disposing&&Volatile.Read(ref generation)==token;}
        bool CurrentLoad(int token){return !IsDisposed&&!Disposing&&Volatile.Read(ref loadGeneration)==token;}
        public Task<bool> LoadSavedAnalysisAsync(string id)
        {
            if(project.Length==0||!Directory.Exists(project))throw new InvalidOperationException("No project selected.");
            InvalidateContext();int token=Interlocked.Increment(ref loadGeneration);string root=project;ClearData();loading=true;SetStatus(T("Validating saved analysis…","正在校验已保存分析…"));RefreshEnabled();
            return OnWorker(()=>{archiveGate.Wait();try{return PdReviewControl.CompetitionParametersForDisplay(root,id,()=>CurrentLoad(token));}finally{archiveGate.Release();}},token,data=>ApplyLoaded(id,data.Parameters,data.DisplayParameters),()=>{if(CurrentLoad(token)){loading=false;RefreshEnabled();}},()=>CurrentLoad(token));
        }
        // Synchronous API for existing container clients. The interactive load button uses the background API above.
        public bool LoadSavedAnalysis(string id)
        {
            InvalidateContext();int token=Interlocked.Increment(ref loadGeneration);ClearData();loading=false;
            try{var data=PdReviewControl.CompetitionParametersForDisplay(project,id,()=>CurrentLoad(token));if(!CurrentLoad(token))return false;ApplyLoaded(id,data.Parameters,data.DisplayParameters);return true;}catch(Exception ex){Failure(ex);RefreshEnabled();return false;}
        }
        void ApplyLoaded(string id,double[,] matrix,double[,] displayMatrix)
        {syncing=true;try{parameters=matrix;loadedId=id;foreach(var plot in LinkedSpectra){plot.LoadDataMatrix(displayMatrix);plot.ExitRegionStatistics();plot.SetReviewVisibility(null);}ResetLinkedFilters();lastError="";}finally{syncing=false;}SyncSharedPhases();SetStatus(ViewSummary());RefreshEnabled();}
        Task<bool> OnWorker<TValue>(Func<TValue> work,int token,Action<TValue> publish,Action finish,Func<bool> stillCurrent=null,bool ownsRuntime=false)
        {
            var done=new TaskCompletionSource<bool>();var handle=Handle;
            var worker=Task.Run(work);if(ownsRuntime)aiWorker=worker;
            worker.ContinueWith(task=>{
                if(IsDisposed||Disposing){done.TrySetResult(false);return;}
                try{BeginInvoke((Action)(()=>{
                    try
                    {
                        bool valid=stillCurrent==null?Current(token):stillCurrent();
                        if(!valid){done.TrySetResult(false);return;}
                        if(task.IsFaulted){var ex=task.Exception.GetBaseException();if(!(ex is OperationCanceledException))Failure(ex);done.TrySetResult(false);}
                        else{publish(task.Result);done.TrySetResult(true);}
                    }
                    catch(Exception ex){Failure(ex);done.TrySetResult(false);}
                    finally{finish();}
                }));}
                catch(InvalidOperationException){done.TrySetResult(false);}
            },TaskScheduler.Default);return done.Task;
        }
        RecognitionSnapshot CaptureInput()
        {
            if(!aiEnabled)throw new InvalidOperationException(T("AI features are disabled.","AI 功能已关闭。"));
            if(workingAi)throw new InvalidOperationException(T("Wait for the current AI operation.","请等待当前 AI 操作完成。"));
            int[] rows=SharedSourceRowIndices.Where(r=>r>=0&&r<parameters.GetLength(0)&&Finite(parameters[r,0])&&parameters[r,0]>=0&&parameters[r,0]<=3&&parameters[r,0]==Math.Truncate(parameters[r,0])&&Finite(parameters[r,3])&&Finite(parameters[r,15])).ToArray();
            return new RecognitionSnapshot{Rows=rows,Phases=rows.Select(r=>(int)parameters[r,0]).ToArray(),Angles=rows.Select(r=>parameters[r,3]).ToArray(),Charges=rows.Select(r=>parameters[r,15]).ToArray(),Project=project,ModelPath=model,English=!UiLanguage.IsChinese(language),Context=generation.ToString(CultureInfo.InvariantCulture),Scope="Saved analysis "+loadedId+"; "+spectrum.DisplayModeName+"; visible source rows; raw Q=pC column15, physical phase column0, phase degrees column3"};
        }
        static bool Finite(double n){return !Double.IsNaN(n)&&!Double.IsInfinity(n);}
        ReviewTask12Runtime Runtime(){return runtime??(runtime=new ReviewTask12Runtime());}
        public Task<bool> RecognizeCurrentDischargeAsync()
        {
            var input=CaptureInput();int token=generation;var engine=Runtime();workingAi=true;recognition=null;SetStatus(T("Recognizing…","正在识别…"));RefreshEnabled();
            return OnWorker(()=>PdReviewControl.RunRecognitionCore(input,engine,()=>Current(token)&&aiEnabled),token,result=>{recognition=result;SetStatus(RecognitionSummary(result));},()=>{workingAi=false;RefreshEnabled();},null,true);
        }
        string LearningRoot(string modelPath){if(LearningRootProviderForTests!=null)return LearningRootProviderForTests(modelPath);return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HVX","CompetitionDefectRecognition","Task12Learning",ReviewTask12Runtime.HashFile(ReviewTask12Runtime.ResolveModelPath(modelPath)));}
        public Task<bool> AddConfirmedLearningSampleAsync(string canonicalKeys,string operatorName,string manualType)
        {return AddConfirmedLearningSampleAsync(canonicalKeys,operatorName,manualType,true,false);}
        public Task<bool> AddConfirmedLearningSampleAsync(string canonicalKeys,string operatorName,string manualType,bool expandLearning,bool conflictAcknowledged)
        {return LearningActionAsync("add",canonicalKeys,operatorName,manualType,expandLearning,conflictAcknowledged);}
        async Task<bool> LearningActionAsync(string action,string keys,string operatorName,string manual,bool expandLearning=true,bool conflictAcknowledged=false)
        {
            try
            {
                var input=CaptureInput();int token=generation;var engine=Runtime();workingAi=true;RefreshEnabled();
                return await OnWorker(()=>{
                    if(!Current(token)||!aiEnabled)throw new OperationCanceledException();string root=LearningRoot(input.ModelPath);
                    if(action=="add")PdReviewControl.AddCompetitionLearning(root,input,keys,manual,operatorName,engine,()=>Current(token)&&aiEnabled,learningCommitGate,expandLearning,conflictAcknowledged);
                    else lock(learningCommitGate){if(!Current(token)||!aiEnabled)throw new OperationCanceledException();if(action=="undo")ReviewLearningTransactions.Undo(root);else ReviewLearningTransactions.Reset(root);}
                    return true;
                },token,ok=>SetStatus(T("Samples updated.","样本已更新。")),()=>{workingAi=false;RefreshEnabled();},null,true);
            }
            catch(Exception ex){Failure(ex);return false;}
        }
        void ShowLearningDialog()
        {
            if(!aiEnabled||workingAi)return;
            using(var form=CreateLearningDialog())
            {
                var labels=PdReviewControl.CompetitionLearningLabels;var list=(CheckedListBox)form.Controls.Find("CompetitionSampleLabels",true).Single();
                var manual=(TextBox)form.Controls.Find("CompetitionSampleCustomType",true).Single();var who=(TextBox)form.Controls.Find("CompetitionSampleOperator",true).Single();
                var expand=(CheckBox)form.Controls.Find("CompetitionSampleExpand",true).Single();
                var confirm=(Button)form.Controls.Find("CompetitionSampleConfirm",true).Single();
                confirm.Click+=(s,e)=>{
                    string keys=String.Join("+",list.CheckedIndices.Cast<int>().Select(i=>labels[i,0]));
                    if(keys.Length==0&&String.IsNullOrWhiteSpace(manual.Text)){MessageBox.Show(form,T("Select a fixed type or enter a manually verified type.","请选择固定类型或输入人工确认的类型。"),form.Text,MessageBoxButtons.OK,MessageBoxIcon.Information);form.DialogResult=DialogResult.None;return;}
                    try{
                        var input=CaptureInput();double overlap;string conflictMessage;
                        bool conflict=PdReviewControl.ProbeCompetitionLearningConflict(input,Runtime(),keys,manual.Text,out overlap,out conflictMessage);
                        if(conflict){
                            string prompt=conflictMessage+T("\n\nContinue and retrain? Existing model data may change.","\n\n是否继续并重训？保存模型会影响已有模型数据。");
                            if(MessageBox.Show(form,prompt,T("Learning conflict","加入学习冲突提示"),MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK){form.DialogResult=DialogResult.None;return;}
                        }
                        form.Tag=new object[]{keys,who.Text,manual.Text,expand.Checked,conflict};
                        form.DialogResult=DialogResult.OK;
                    }catch(Exception ex){MessageBox.Show(form,ex.Message,form.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);form.DialogResult=DialogResult.None;}
                };
                if(form.ShowDialog(FindForm())==DialogResult.OK&&form.Tag is object[]){
                    var args=(object[])form.Tag;
                    var pending=AddConfirmedLearningSampleAsync((string)args[0],(string)args[1],(string)args[2],(bool)args[3],(bool)args[4]);
                }
            }
        }
        internal Form CreateLearningDialog()
        {
            var form=new Form{Name="CompetitionSampleDialog",Text=T("Confirm learning sample","确认学习样本"),StartPosition=FormStartPosition.CenterParent,Size=new Size(650,740),MinimumSize=new Size(520,580),Font=Font,ShowInTaskbar=false,BackColor=ReviewTheme.Workspace,ForeColor=ReviewTheme.Ink};
            HVX.Presentation.OemUiBranding.Apply(form);
            var titleFont=new Font(Font,FontStyle.Bold);form.Disposed+=(s,e)=>titleFont.Dispose();
            var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16)};
            var grid=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,RowCount=10,BackColor=Color.White,Padding=new Padding(16)};grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));for(int i=0;i<10;i++)grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title=new Label{Name="CompetitionSampleTitle",AutoSize=true,Font=titleFont,Text=T("Manually verified learning sample","人工确认学习样本"),Margin=new Padding(0,0,0,8)};
            var notice=new Label{Name="CompetitionSampleNotice",AutoSize=true,ForeColor=ReviewTheme.SecondaryInk,Text=T("Confirm one physical phase and the displayed pulse region. Confirming may expand learning variants and retrain with the best available algorithm.","请确认当前单一物理相别及显示的脉冲区域。确认后可先拓展学习，再按最佳算法重训模型。"),Margin=new Padding(0,0,0,14)};
            var list=new CheckedListBox{Name="CompetitionSampleLabels",Dock=DockStyle.Fill,CheckOnClick=true,IntegralHeight=false,Height=Math.Max(200,Font.Height*8+20),BorderStyle=BorderStyle.FixedSingle,BackColor=Color.White,ForeColor=ReviewTheme.Ink,Margin=new Padding(0,0,0,14)};
            var labels=PdReviewControl.CompetitionLearningLabels;for(int i=0;i<labels.GetLength(0);i++)list.Items.Add(T(labels[i,2],labels[i,1]));
            var expand=new CheckBox{Name="CompetitionSampleExpand",AutoSize=true,Checked=true,Text=T("Expanded learning (generate variants to broaden recognition)","拓展学习（对样本生成变体以扩大识别范围）"),Margin=new Padding(0,0,0,14)};
            var customCaption=new Label{Name="CompetitionSampleCustomCaption",AutoSize=true,Text=T("Optional custom type · pending taxonomy review","自定义类型（可选，需类别审核）"),Margin=new Padding(0,0,0,6)};
            var manual=new TextBox{Name="CompetitionSampleCustomType",Dock=DockStyle.Fill,MaxLength=160,Margin=new Padding(0,0,0,14)};
            var whoCaption=new Label{Name="CompetitionSampleOperatorCaption",AutoSize=true,Text=T("Operator","操作人员"),Margin=new Padding(0,0,0,6)};
            var who=new TextBox{Name="CompetitionSampleOperator",Dock=DockStyle.Fill,Text=Environment.UserName,MaxLength=100,Margin=new Padding(0,0,0,16)};
            var buttons=new FlowLayoutPanel{Name="CompetitionSampleActions",Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,WrapContents=true,Margin=Padding.Empty};
            var confirm=new HVX.Presentation.R2RoundedButton{Name="CompetitionSampleConfirm",Font=Font,Text=T("Add to learning","加入学习")};StyleButton(confirm,true,false);
            var cancel=new HVX.Presentation.R2RoundedButton{Name="CompetitionSampleCancel",Font=Font,Text=T("Cancel","取消"),DialogResult=DialogResult.Cancel};StyleButton(cancel,false,false);buttons.Controls.AddRange(new Control[]{confirm,cancel});
            Control[] rows={title,notice,list,expand,customCaption,manual,whoCaption,who,buttons};for(int i=0;i<rows.Length;i++)grid.Controls.Add(rows[i],0,i);
            scroll.Controls.Add(grid);form.Controls.Add(scroll);form.AcceptButton=confirm;form.CancelButton=cancel;
            Action resize=()=>{int w=Math.Max(100,scroll.ClientSize.Width-scroll.Padding.Horizontal-grid.Padding.Horizontal-SystemInformation.VerticalScrollBarWidth-6);foreach(var label in new[]{title,notice,customCaption,whoCaption})label.MaximumSize=new Size(w,0);expand.MaximumSize=new Size(w,0);};
            form.Resize+=(s,e)=>resize();resize();return form;
        }
        protected override void Dispose(bool disposing)
        {
            if(disposing){lock(learningCommitGate){Interlocked.Increment(ref generation);Interlocked.Increment(ref loadGeneration);}var engine=runtime;runtime=null;if(engine!=null){var pending=aiWorker;if(pending!=null&&!pending.IsCompleted)pending.ContinueWith(t=>engine.Dispose(),TaskScheduler.Default);else engine.Dispose();}}
            base.Dispose(disposing);
            if(disposing){details.Dispose();if(headingFont!=null){headingFont.Dispose();headingFont=null;}}
        }
    }
}
