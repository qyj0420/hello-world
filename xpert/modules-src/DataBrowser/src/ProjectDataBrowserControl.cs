using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using CalibrationWaveform;
using LabVIEWItemInformation;
namespace HVX.Controls
{
    [ToolboxItem(true)]
    public sealed partial class ProjectDataBrowserControl : UserControl
    {
        readonly TreeView tree=new VerticalTreeView();
        readonly Panel detail=new Panel(),informationHost=new Panel();
        readonly TableLayoutPanel shellLayout=new TableLayoutPanel();
        readonly Panel projectToolbar=new Panel(),toolbarAccent=new Panel(),statisticsGap=new Panel();
        readonly SplitContainer navigationSplit=new SplitContainer();
        readonly Label navigationSelection=new Label();
        bool arrangingNavigation,navigationUserSized;
        int requestedNavigationWidth;
        readonly TableLayoutPanel rightLayout=new TableLayoutPanel();
        readonly Label heading=new Label(),status=new Label();
        readonly TextBox information=new TextBox();
        readonly FlowLayoutPanel actions=new DeferredBrowserActionsPanel();
        readonly BrowserButton openFolder=new BrowserButton();
        readonly BrowserButton editButton=new BrowserButton(),saveButton=new BrowserButton(),cancelButton=new BrowserButton(),lengthButton=new BrowserButton(),jointButton=new BrowserButton();
        readonly ToolTip actionTips=new ToolTip{ShowAlways=true};
        readonly FlowLayoutPanel phaseButtonsHost=new FlowLayoutPanel();
        readonly BrowserPhaseButton[] phaseButtons=new BrowserPhaseButton[3];
        readonly ItemInformationControl item=new ItemInformationControl();
        readonly Panel itemViewport=new Panel{AutoScroll=true,BackColor=BrowserTheme.Surface};
        readonly CalibrationWaveformControl calibration=new CalibrationWaveformControl();
        readonly BrowserTestWaveformControl testWaveform=new BrowserTestWaveformControl();
        readonly BrowserPlot plot=new BrowserPlot();
        readonly DataGridView table=new DataGridView();
        readonly Panel statsHost=new Panel();
        readonly object waveGate=new object();
        BrowserProject project;BrowserRecord selectedRecord;BrowserWaveform selectedWave;
        bool english,editing,sync,loading,saving,loadingWave,waveLoadFailed,arrangingProjectInformation,arrangingActions,embeddedMode;volatile int projectGeneration,selectionGeneration;int statsMode;CancellationTokenSource projectCancellation=new CancellationTokenSource(),waveCancellation=new CancellationTokenSource();
        string projectPath="",lastError="",selectedCalHash="";double switchOnTimeSeconds;string reloadRoot="",reloadFile="",reloadKind="";long selectionSequence;
        public ProjectDataBrowserControl()
        {
            AutoScaleMode=AutoScaleMode.None;Font=new Font("Microsoft YaHei UI",10f);BackColor=BrowserTheme.Canvas;ForeColor=BrowserTheme.Ink;Size=new Size(1920,1080);MinimumSize=Size.Empty;
            var shell=shellLayout;shell.Dock=DockStyle.Fill;shell.ColumnCount=1;shell.RowCount=4;shell.Margin=Padding.Empty;shell.Padding=Padding.Empty;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,48));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,2));shell.RowStyles.Add(new RowStyle(SizeType.Percent,100));shell.RowStyles.Add(new RowStyle(SizeType.Absolute,28));Controls.Add(shell);
            var top=projectToolbar;top.Name="ProjectToolbar";top.Dock=DockStyle.Fill;top.Padding=new Padding(8,3,8,3);top.BackColor=BrowserTheme.Navy;
            openFolder.Name="ProjectInformationButton";openFolder.Dock=DockStyle.Fill;openFolder.Margin=Padding.Empty;openFolder.BackColor=BrowserTheme.Navy;openFolder.Font=new Font("Microsoft YaHei UI",11f,FontStyle.Bold);
            top.Controls.Add(openFolder);shell.Controls.Add(top,0,0);toolbarAccent.Dock=DockStyle.Fill;toolbarAccent.BackColor=BrowserTheme.Cyan;shell.Controls.Add(toolbarAccent,0,1);
            var split=navigationSplit;split.Dock=DockStyle.Fill;split.Orientation=Orientation.Vertical;split.FixedPanel=FixedPanel.Panel1;split.SplitterWidth=7;split.BackColor=BrowserTheme.Border;split.Size=new Size(1920,1000);split.Panel1MinSize=220;split.Panel2MinSize=560;split.SplitterDistance=360;shell.Controls.Add(split,0,2);split.SizeChanged+=(s,e)=>AdjustNavigationWidth();
            split.SplitterMoving+=(s,e)=>{if(!arrangingNavigation){navigationUserSized=true;requestedNavigationWidth=e.SplitX;}};
            split.SplitterMoved+=(s,e)=>UpdateNavigationSelection();
            tree.Dock=DockStyle.Fill;tree.BorderStyle=BorderStyle.None;tree.HideSelection=false;tree.FullRowSelect=true;tree.ItemHeight=27;tree.ShowNodeToolTips=true;tree.DrawMode=TreeViewDrawMode.OwnerDrawText;tree.DrawNode+=DrawNavigationNode;tree.Font=Font;tree.BackColor=BrowserTheme.Surface;tree.ForeColor=BrowserTheme.Ink;tree.LineColor=BrowserTheme.Border;split.Panel1.Padding=new Padding(8,7,6,7);split.Panel1.BackColor=BrowserTheme.Surface;split.Panel1.Controls.Add(tree);
            tree.Indent=14;tree.ShowLines=false;InitializeNavigationIcons();
            tree.FontChanged+=(s,e)=>{tree.ItemHeight=Math.Max(27,tree.Font.Height+6);};
            navigationSelection.Name="NavigationSelection";navigationSelection.Dock=DockStyle.Bottom;navigationSelection.BackColor=BrowserTheme.Header;navigationSelection.ForeColor=BrowserTheme.Ink;navigationSelection.Font=Font;navigationSelection.Padding=new Padding(8);navigationSelection.UseMnemonic=false;navigationSelection.AutoEllipsis=false;navigationSelection.Height=100;split.Panel1.Controls.Add(navigationSelection);
            split.Panel1.SizeChanged+=(s,e)=>UpdateNavigationSelection();
            navigationSelection.FontChanged+=(s,e)=>UpdateNavigationSelection();
            navigationSelection.PaddingChanged+=(s,e)=>UpdateNavigationSelection();
            navigationSelection.DpiChangedAfterParent+=(s,e)=>UpdateNavigationSelection();
            var right=rightLayout;right.Dock=DockStyle.Fill;right.ColumnCount=1;right.RowCount=4;right.BackColor=BrowserTheme.Surface;right.Margin=Padding.Empty;right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.Absolute,39));right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.Absolute,50));right.RowStyles.Add(new RowStyle(SizeType.Absolute,112));split.Panel2.Controls.Add(right);
            heading.Dock=DockStyle.Fill;heading.BackColor=BrowserTheme.Header;heading.ForeColor=BrowserTheme.Ink;heading.Font=new Font("Microsoft YaHei UI",10.5f,FontStyle.Bold);heading.TextAlign=ContentAlignment.MiddleLeft;heading.Padding=new Padding(14,0,0,0);heading.AutoEllipsis=true;heading.Paint+=(s,e)=>{using(var accent=new SolidBrush(BrowserTheme.Cyan))e.Graphics.FillRectangle(accent,0,0,3,heading.Height);};right.Controls.Add(heading,0,0);
            detail.Dock=DockStyle.Fill;detail.Padding=new Padding(5);right.Controls.Add(detail,0,1);
            actions.Dock=DockStyle.Fill;actions.Margin=Padding.Empty;actions.Padding=new Padding(7,5,7,5);actions.BackColor=BrowserTheme.SurfaceAlt;actions.WrapContents=true;actions.AutoScroll=false;actions.Paint+=(s,e)=>{using(var line=new Pen(BrowserTheme.Border))e.Graphics.DrawLine(line,0,0,actions.Width,0);};right.Controls.Add(actions,0,2);
            foreach(var b in new[]{editButton,saveButton,cancelButton,jointButton,lengthButton}){b.Width=128;b.Margin=new Padding(3,0,3,0);actions.Controls.Add(b);}
            actions.SizeChanged+=(s,e)=>AdjustPhaseActionHeight();
            actions.VisibleChanged+=(s,e)=>AdjustPhaseActionHeight();
            actions.Layout+=(s,e)=>AdjustPhaseActionHeight();
            FontChanged+=(s,e)=>AdjustPhaseActionHeight();
            phaseButtonsHost.Name="StatisticsPhaseButtons";phaseButtonsHost.AutoSize=true;phaseButtonsHost.AutoSizeMode=AutoSizeMode.GrowAndShrink;phaseButtonsHost.WrapContents=false;phaseButtonsHost.FlowDirection=FlowDirection.LeftToRight;phaseButtonsHost.Margin=Padding.Empty;actions.Controls.Add(phaseButtonsHost);phaseButtonsHost.SizeChanged+=(s,e)=>AdjustPhaseActionHeight();
            Color[] colors={BrowserTheme.PhaseA,BrowserTheme.PhaseB,BrowserTheme.PhaseC};
            for(int i=0;i<3;i++){var option=new BrowserPhaseButton{ Name="StatisticsPhase"+(char)('A'+i),Checked=true,PhaseColor=colors[i],TabIndex=i,Margin=new Padding(3,0,3,0)};phaseButtons[i]=option;phaseButtonsHost.Controls.Add(option);option.CheckedChanged+=(s,e)=>{if(!sync&&SelectedNodeKind.StartsWith("stats"))ShowStatistics(statsMode);};}
            informationHost.Dock=DockStyle.Fill;informationHost.Padding=new Padding(10,8,8,7);informationHost.BackColor=BrowserTheme.Canvas;informationHost.Paint+=(s,e)=>{using(var line=new Pen(BrowserTheme.Border))e.Graphics.DrawLine(line,0,0,informationHost.Width,0);};information.Dock=DockStyle.Fill;information.Multiline=true;information.ReadOnly=true;information.WordWrap=true;information.ScrollBars=ScrollBars.Vertical;information.BorderStyle=BorderStyle.None;information.BackColor=informationHost.BackColor;information.ForeColor=BrowserTheme.Ink;information.Font=new Font("Microsoft YaHei UI",10f);informationHost.Controls.Add(information);right.Controls.Add(informationHost,0,3);right.SizeChanged+=(s,e)=>AdjustInformationHeight();
            status.Dock=DockStyle.Fill;status.TextAlign=ContentAlignment.MiddleLeft;status.Padding=new Padding(8,0,0,0);status.BackColor=BrowserTheme.Canvas;status.ForeColor=BrowserTheme.Muted;status.AutoEllipsis=true;shell.Controls.Add(status,0,3);
            itemViewport.Controls.Add(item);itemViewport.Resize+=(s,e)=>ArrangeProjectInformation();item.MetadataReadOnly=true;item.InsulationReadOnly=true;
            item.DpiChangedAfterParent+=(s,e)=>ArrangeProjectInformation();
            calibration.AllowCursorEditing=false;calibration.UseExternalConnectorDialog=true;calibration.ShowCalculatedDistance=false;calibration.BackColor=BrowserTheme.Surface;calibration.TextTranslator=(zh,en)=>T(zh,en);
            testWaveform.Name="TestWaveformPlot";
            table.Font=new Font("Microsoft YaHei UI",10f);table.DefaultCellStyle.Font=table.Font;table.ColumnHeadersDefaultCellStyle.Font=new Font("Microsoft YaHei UI",10f,FontStyle.Bold);table.RowTemplate.Height=27;table.ColumnHeadersHeight=46;table.Dock=DockStyle.Bottom;table.Height=128;table.ReadOnly=true;table.AllowUserToResizeRows=false;table.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.None;table.AllowUserToAddRows=false;table.AllowUserToDeleteRows=false;table.RowHeadersVisible=false;table.BackgroundColor=BrowserTheme.Surface;table.BorderStyle=BorderStyle.None;table.EnableHeadersVisualStyles=false;table.ColumnHeadersDefaultCellStyle.BackColor=BrowserTheme.Header;table.ColumnHeadersDefaultCellStyle.ForeColor=BrowserTheme.Ink;table.DefaultCellStyle.BackColor=BrowserTheme.Surface;table.DefaultCellStyle.ForeColor=BrowserTheme.Ink;table.DefaultCellStyle.SelectionBackColor=BrowserTheme.Selection;table.DefaultCellStyle.SelectionForeColor=BrowserTheme.Navy;table.AlternatingRowsDefaultCellStyle.BackColor=BrowserTheme.SurfaceAlt;table.GridColor=BrowserTheme.Border;table.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;table.ScrollBars=ScrollBars.Vertical;
            table.DefaultCellStyle.SelectionForeColor=Color.White;
            statisticsGap.Dock=DockStyle.Bottom;statisticsGap.Height=14;statisticsGap.BackColor=BrowserTheme.Canvas;
            statsHost.Controls.Add(plot);statsHost.Controls.Add(statisticsGap);statsHost.Controls.Add(table);table.CellDoubleClick+=(sender,e)=>NavigateTableRow(e.RowIndex);plot.Dock=DockStyle.Fill;statsHost.SizeChanged+=(s,e)=>AdjustStatisticsLayout();
            openFolder.Click+=(s,e)=>{if(project!=null){OpenProjectFolder();return;}using(var chooser=new FolderBrowserDialog()){if(chooser.ShowDialog(FindForm())==DialogResult.OK)BeginLoadProject(chooser.SelectedPath);}};
            tree.BeforeSelect+=(s,e)=>{if(editing||saving){e.Cancel=true;SetStatus(T("请先保存或取消当前编辑。","Save or cancel the current edit first."));}};
            tree.AfterSelect+=(s,e)=>{UpdateNavigationSelection();if(!sync&&!suppressTreeActivate)Activate(e.Node);};
            tree.NodeMouseClick+=(s,e)=>{if(e.Button==MouseButtons.Left&&e.Node==tree.SelectedNode&&waveLoadFailed&&!loadingWave&&!editing&&!saving&&!suppressTreeActivate&&(ModifierKeys&Keys.Control)==0)Activate(e.Node);};
            InitializeTreeMultiSelect();
            editButton.Click+=(s,e)=>BeginCalibrationEdit();saveButton.Click+=(s,e)=>SaveCalibrationEdit();cancelButton.Click+=(s,e)=>CancelCalibrationEdit();lengthButton.Click+=(s,e)=>ShowLengthDialog();jointButton.Click+=(s,e)=>ShowJoints(double.NaN);
            calibration.CursorChanged+=(s,e)=>{if(editing){double dt=calibration.TimeDifference;SetStatus(T("编辑中：时间差 ","Editing: delay ")+dt.ToString("0.0000",CultureInfo.InvariantCulture)+" μs    "+T("波速 ","Velocity ")+(dt>0?2*selectedWave.CableLengthMetres/dt:double.NaN).ToString("0.00",CultureInfo.InvariantCulture)+" m/μs");}};
            calibration.ConnectorDialogRequested+=(s,e)=>{if(!saving&&!loadingWave&&ValidCalibrationGeometry()){double v=editing?2*selectedWave.CableLengthMetres/calibration.TimeDifference:selectedWave.VelocityMPerUs;double d=(calibration.RequestedConnectorTime-calibration.IncidentCursorX)*v/2;ShowJoints(d);}};
            InitializeLocationAnalysisView();InitializeCalibrationComparison();InitializeProjectLoadingView();InitializeDetailLoadingView();ApplyLanguage();ClearDetail();SetStatus(T("等待加载项目。","Waiting for a project."));
        }
        void AdjustNavigationWidth()
        {
            if(arrangingNavigation)return;
            int available=navigationSplit.Width-navigationSplit.SplitterWidth-navigationSplit.Panel2MinSize;
            if(available<navigationSplit.Panel1MinSize)return;
            int preferred=navigationUserSized?requestedNavigationWidth:Math.Max(280,Math.Min(440,(int)Math.Round(navigationSplit.Width*.26)));
            preferred=Math.Max(navigationSplit.Panel1MinSize,Math.Min(preferred,available));
            arrangingNavigation=true;
            try{if(navigationSplit.SplitterDistance!=preferred)navigationSplit.SplitterDistance=preferred;}
            finally{arrangingNavigation=false;}
            UpdateNavigationSelection();
        }
        void UpdateNavigationSelection()
        {
            TreeNode node=tree.SelectedNode;
            var data=node==null?null:node.Tag as NodeData;
            var record=data==null?null:data.Value as BrowserRecord;
            string title=T("当前选择","Selected");
            string text=node==null?T("请选择左侧的数据分类或记录。","Select a category or record."):node.Text;
            string tooltip=text;
            if(record!=null)
            {
                string category=record.Kind=="cal"?T("校准数据","Calibration"):record.Kind=="dem"?T("测试数据","Test data"):T("定位数据","Location data");
                title=category+" · "+(record.Phase=="ALL"?T("全部相","All phases"):record.Phase);
                text=Path.GetFileName(record.Path);
                tooltip=text+(string.IsNullOrWhiteSpace(record.Warning)?"":Environment.NewLine+record.Warning);
            }
            string multi=MultiSelectionSummary();
            if(multi.Length>0){title=multi;tooltip=multi+(tooltip.Length>0?Environment.NewLine+tooltip:"");}
            if(actionTips.GetToolTip(navigationSelection)!=tooltip)actionTips.SetToolTip(navigationSelection,tooltip);
            string display=title+Environment.NewLine+text;
            if(navigationSelection.Text!=display)navigationSelection.Text=display;
            if(navigationSelection.AccessibleName!=title)navigationSelection.AccessibleName=title;
            if(navigationSelection.AccessibleDescription!=text)navigationSelection.AccessibleDescription=text;
            int width=Math.Max(80,navigationSplit.Panel1.ClientSize.Width-navigationSplit.Panel1.Padding.Horizontal-navigationSelection.Padding.Horizontal);
            int measured=MeasureNavigationSelection(display,width);
            int height=Math.Max(76,Math.Min(Math.Max(100,navigationSplit.Panel1.Height/3),measured+navigationSelection.Padding.Vertical+8));
            if(navigationSelection.Height!=height)navigationSelection.Height=height;
        }
        void DrawNavigationNode(object sender, DrawTreeNodeEventArgs e)
        {
            if(e.Node==null)return;
            int width=Math.Max(1,tree.ClientSize.Width-e.Bounds.Left-3);
            var textBounds=new Rectangle(e.Bounds.Left,e.Bounds.Top,width,e.Bounds.Height);
            bool selected=IsTreeNodeHighlighted(e.Node);
            using(var background=new SolidBrush(selected?Color.FromArgb(0,163,199):tree.BackColor))
                // OwnerDrawText runs after native branch glyphs/icons. Never paint
                // over their area: doing so makes populated nodes look unexpandable.
                e.Graphics.FillRectangle(background,textBounds);
            Color ink=selected?Color.White:e.Node.ForeColor.IsEmpty?tree.ForeColor:e.Node.ForeColor;
            TextRenderer.DrawText(e.Graphics,e.Node.Text,tree.Font,textBounds,ink,
                TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine);
        }
        void AdjustStatisticsLayout()
        {
            if(!SelectedNodeKind.StartsWith("stats",StringComparison.Ordinal))return;
            bool showTable=statsHost.ClientSize.Height>=400;
            table.Visible=showTable;
            statisticsGap.Visible=showTable;
            if(showTable)
            {
                int desired=Math.Max(126,Math.Min(210,statsHost.ClientSize.Height/4));
                if(table.Height!=desired)table.Height=desired;
            }
        }
        // This control lays out its contents from the available client area. A host
        // scaling pass must not enlarge its minimum or scale those layouts twice.
        // 1280 x 720 is the host outer window, not the embedded client area.
        protected override bool ScaleChildren { get { return false; } }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
            Size minimum = MinimumSize;
            base.ScaleControl(factor, specified);
            MinimumSize = minimum;
        }
        [Browsable(false)]public string ProjectPath{get{return projectPath;}set{BeginLoadProject(value);}}
        [Category("Appearance"),DefaultValue(false)]public bool EmbeddedMode
        {
            get{return embeddedMode;}
            set
            {
                if(InvokeRequired){Invoke(new Action<bool>(v=>EmbeddedMode=v),value);return;}
                if(embeddedMode==value)return;
                embeddedMode=value;
                shellLayout.RowStyles[0].Height=value?0:48;
                shellLayout.RowStyles[1].Height=value?0:2;
                projectToolbar.Visible=!value;
                toolbarAccent.Visible=!value;
                shellLayout.PerformLayout();
            }
        }
        [Browsable(false)]public string LastError{get{return lastError;}}
        [Browsable(false)]public string ProjectDiagnosticText{get{return project==null?"":string.Join(Environment.NewLine,project.Warnings);}}
        [Browsable(false)]public bool IsBusy{get{return loading||saving||loadingWave;}}
        [Browsable(false)]public bool IsEditingCalibration{get{return editing;}}
        [Browsable(false)]public string SelectedNodeKind{get;private set;}="";
        [Browsable(false)]public string SelectedFilePath{get;private set;}="";
        [Browsable(false)]public string LastBackupDirectory{get;private set;}="";
        [Browsable(false)]public int LastChangedFileCount{get;private set;}
        [Browsable(false)]public long SelectionSequence{get{return selectionSequence;}}
        [Category("Language"),DefaultValue(false)]
        public bool EnglishVersion{get{return LanguageIndex==1;}set{LanguageIndex=value?1:0;}}
        [Browsable(false),EditorBrowsable(EditorBrowsableState.Never),DefaultValue(0.0)]public double SwitchOnTimeSeconds
        {
            get{return switchOnTimeSeconds;}
            set{if(!BrowserPlot.Finite(value)||value<0||value>1000)throw new ArgumentOutOfRangeException("SwitchOnTimeSeconds");Ui(()=>{switchOnTimeSeconds=value;});}
        }
        public event EventHandler ProjectLoaded,SelectionChanged,ProjectSaved;
        string T(string zh,string en){return BrowserLanguages.Text(languageIndex,zh,en);}
        void Ui(Action action){if(IsDisposed)return;if(InvokeRequired)Invoke(action);else action();}
        void Post(Action action){if(IsDisposed||Disposing||!IsHandleCreated)return;try{BeginInvoke((Action)(()=>{if(!IsDisposed&&!Disposing)action();}));}catch(InvalidOperationException){}}
        void SetStatus(string text){status.Text=text;}
        void UpdateProjectHeader()
        {
            if(project==null)
            {
                openFolder.Text=T("本项目","This project");
                openFolder.AccessibleDescription="";
                return;
            }
            string length=BrowserPlot.Finite(project.CableLengthMetres)
                ? T("，电缆长度 ","  ·  cable length ")+F(project.CableLengthMetres,"0.0")+" m" : "";
            openFolder.Text=DisplayProjectTitle()+length;
            openFolder.AccessibleDescription=projectPath;
        }
        string DisplayProjectTitle()
        {
            if(project==null)return "";
            string name=Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
            return project.Title.StartsWith(name,StringComparison.Ordinal)?HVX.ProjectModel.AnalysisDateText.DisplayProjectName(name)+project.Title.Substring(name.Length):project.Title;
        }
        bool Error(string text){lastError=text;SetStatus(text);return false;}
        void ApplyLanguage()
        {
            ApplyDetailLoadingLanguage();
            findComparisonJointsButton.Text=T("接头查找","Find joints");
            UpdateProjectHeader();editButton.Text=T("编辑校准","Edit calibration");saveButton.Text=T("保存校准","Save calibration");cancelButton.Text=T("取消编辑","Cancel edit");lengthButton.Text=T("修正长度","Correct length");lengthButton.AccessibleName=T("电缆长度修正","Correct cable length");actionTips.SetToolTip(lengthButton,lengthButton.AccessibleName);jointButton.Text=T("添加接头","Add joint");UpdatePhaseButtonText();item.SetLanguage(languageIndex);calibration.EnglishVersion=english;if(selectedRecord!=null&&selectedRecord.Kind=="dem"&&selectedWave!=null)testWaveform.SetWaveform(selectedWave,languageIndex);ApplyLocationAnalysisLanguage(languageIndex);AdjustPhaseActionHeight();
        }
        bool CanLoad(){if(saving||editing)return Error(T("请先完成保存或取消编辑。","Finish saving or cancel editing first."));return true;}
        public bool BeginLoadProject(string path)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<string,bool>(BeginLoadProject),path);if(!CanLoad())return false;
            if(!IsHandleCreated)CreateControl();int generation=StartLoad(path);var cancellation=projectCancellation.Token;double switchTime=switchOnTimeSeconds;
            Task.Run(()=>{try{var p=PrepareProjectLoad(path,switchTime,cancellation);Post(()=>{if(generation==projectGeneration&&!cancellation.IsCancellationRequested)FinishLoad(p);});}catch(Exception ex){Post(()=>{if(generation==projectGeneration)FailProjectLoad(ex);});}});return true;
        }
        public bool LoadProject(string path)
        {
            if(InvokeRequired)return (bool)Invoke(new Func<string,bool>(LoadProject),path);if(!CanLoad())return false;StartLoad(path);
            try{return FinishLoad(PrepareProjectLoad(path,switchOnTimeSeconds,projectCancellation.Token));}catch(Exception ex){FailProjectLoad(ex);return false;}
        }
        int StartLoad(string path){ShowProjectLoadingView(T("正在读取项目…","Loading project…"));projectCancellation.Cancel();projectCancellation.Dispose();projectCancellation=new CancellationTokenSource();waveCancellation.Cancel();projectGeneration++;selectionGeneration++;ResetCalibrationComparison();loading=true;loadingWave=false;lastError="";project=null;projectPath="";openFolder.Text=T("正在读取项目…","Loading project…");openFolder.AccessibleDescription=path??"";tree.Nodes.Clear();tree.Enabled=false;ClearDetail();SetStatus(T("正在读取项目…","Loading project…"));return projectGeneration;}
        bool FinishLoad(PreparedProjectLoad prepared)
        {
            var p=prepared.Project;
            int generation=projectGeneration;
            try {
            rightLayout.SuspendLayout();detail.SuspendLayout();itemViewport.SuspendLayout();
            try{project=p;projectPath=p.ProjectPath;UpdateProjectHeader();item.ApplyPreparedItem(prepared.Item,languageIndex,prepared.ItemError);BuildTree();ShowProjectInfo();}
            finally{itemViewport.ResumeLayout(true);detail.ResumeLayout(true);rightLayout.ResumeLayout(true);}
            ArrangeProjectInformation();
            bool savedHere=string.Equals(saveNoticeRoot,p.ProjectPath,StringComparison.OrdinalIgnoreCase);string notice=savedHere?saveNotice:"",next=savedHere?pendingSelection:"";saveNotice="";pendingSelection="";saveNoticeRoot="";
            SetStatus(T("项目已加载：","Project loaded: ")+p.Records.Count+T(" 个文件"," files"));
            if(!string.IsNullOrEmpty(notice)){information.Text=notice+Environment.NewLine+information.Text;SetStatus(T("项目已保存并重新加载。","Project saved and reloaded."));}
            if(!string.IsNullOrEmpty(next)&&generation==projectGeneration)SelectFile(next);
            bool restore=string.Equals(reloadRoot,p.ProjectPath,StringComparison.OrdinalIgnoreCase);string restoreFile=reloadFile,restoreKind=reloadKind;reloadRoot=reloadFile=reloadKind="";
            if(restore&&generation==projectGeneration){if(restoreKind.StartsWith("stats")){int m;if(int.TryParse(restoreKind.Substring(5),out m))SelectStatistics(m);}else if(!string.IsNullOrEmpty(restoreFile))SelectFile(restoreFile);}
            RevealProjectContent();loading=false;tree.Enabled=true;
            }catch(Exception ex){if(generation==projectGeneration)FailProjectLoad(ex);return false;}
            try{ProjectLoaded?.Invoke(this,EventArgs.Empty);}
            catch(Exception ex){if(generation==projectGeneration){lastError=ex.Message;SetStatus(T("项目已加载，但部分显示未能更新。请重新打开页面。","Project loaded, but part of the display could not be updated. Reopen the page."));}}
            return true;
        }
        TreeNode Node(string text,string kind,object value=null){return new TreeNode(text){Tag=new NodeData{Kind=kind,Value=value},Name=kind,ImageKey=NavigationIconKey(kind),SelectedImageKey=NavigationIconKey(kind)};}
        sealed class NodeData{internal string Kind;internal object Value;}
        void BuildTree()
        {
            ClearTreeMultiSelection();
            sync=true;tree.BeginUpdate();try{tree.Nodes.Clear();tree.Nodes.Add(Node(T("项目信息","Project information"),"project"));
            AddGroup(T("校准数据","Calibration data"),"cal",project.Calibrations);AddGroup(T("测试数据","Test data"),"dem",project.Tests);AddGroup(T("定位数据","Location data"),"loc",project.Locations);
            if(project.Calibrations.Count>1)tree.Nodes[1].Nodes.Insert(0,Node(T("全部校准","All calibrations"),"cal-comparison"));
            var stats=Node(T("统计数据","Statistics")+" (3)","stats");
            stats.ToolTipText=T("统计数据：3 类","Statistics: 3 views");
            var voltage=Node(T("电压趋势","Voltage"),"stats1");voltage.ToolTipText=T("加压次序－电压","Sequence – voltage");stats.Nodes.Add(voltage);
            var discharge=Node(T("局放统计","Maximum PD"),"stats2");discharge.ToolTipText=T("加压次序－电压－最大放电量","Sequence – voltage – maximum discharge");stats.Nodes.Add(discharge);
            var loss=Node(T("介损趋势","tan δ"),"stats3");loss.ToolTipText=T("加压次序－电压－介损","Sequence – voltage – tan δ");stats.Nodes.Add(loss);
            tree.Nodes.Add(stats);stats.Expand();tree.SelectedNode=tree.Nodes[0];}finally{tree.EndUpdate();sync=false;}AdjustNavigationWidth();UpdateNavigationSelection();
        }
        void AddGroup(string title,string kind,List<BrowserRecord> records)
        {
            var group=Node(title+" ("+records.Count+")",kind+"Group",records);group.ToolTipText=group.Text;tree.Nodes.Add(group);
            foreach(var phase in records.GroupBy(r=>r.Phase)){string phaseName=phase.Key=="ALL"?T("全部相","All phases"):english?"L"+("ABC".IndexOf(phase.Key)+1):phase.Key+"相";var p=Node(phaseName+" ("+phase.Count()+")",kind+"Group",phase.ToList());p.ToolTipText=p.Text;group.Nodes.Add(p);
                foreach(var r in phase){var n=Node(r.Kind=="cal"?r.Name:r.Order.ToString("000")+"  "+F(r.VoltageKv,"0.00")+" kV · "+r.Repeat,kind,r);n.ToolTipText=r.Name+(string.IsNullOrWhiteSpace(r.Warning)?"":Environment.NewLine+r.Warning);if(!r.IsValid)n.ForeColor=BrowserTheme.Error;p.Nodes.Add(n);}}
            if(kind=="cal")group.ExpandAll();
        }
        void ClearDetail(){HideDetailLoading();detail.Controls.Clear();actions.Visible=false;rightLayout.RowStyles[2].Height=0;information.Clear();selectedRecord=null;selectedWave=null;selectedCalHash="";waveLoadFailed=false;SelectedNodeKind="";SelectedFilePath="";heading.Text=T("数据浏览","Data browser");}
        void ShowControl(Control c){HideDetailLoading();detail.Controls.Clear();c.Dock=DockStyle.Fill;detail.Controls.Add(c);}
        void UpdatePhaseButtonText()
        {
            for(int i=0;i<phaseButtons.Length;i++){phaseButtons[i].Text=english?"L"+(i+1):((char)('A'+i))+"相";phaseButtons[i].AccessibleName=phaseButtons[i].Text;}
        }
        void AdjustPhaseActionHeight()
        {
            if(arrangingActions||rightLayout.RowStyles.Count<=2)return;
            arrangingActions=true;
            try
            {
                if(shellLayout.RowStyles.Count>3)shellLayout.RowStyles[3].Height=Math.Max(28,status.Font.Height+10);
                foreach(var button in actions.Controls.OfType<BrowserButton>())
                {
                    button.Font=Font;button.AutoEllipsis=false;
                    button.Width=Math.Max(116,TextRenderer.MeasureText(button.Text,button.Font).Width+28);
                    button.Height=Math.Max(36,button.Font.Height+14);
                    button.Margin=new Padding(4,3,4,3);
                }
                if(!actions.Visible)return;
                int available=Math.Max(120,actions.ClientSize.Width-actions.Padding.Horizontal),used=0,rowHeight=0,total=0;
                foreach(Control child in actions.Controls)
                {
                    if(!child.Visible)continue;
                    int width=child.Width+child.Margin.Horizontal,height=child.Height+child.Margin.Vertical;
                    if(used>0&&used+width>available){total+=rowHeight;used=0;rowHeight=0;}
                    used+=width;rowHeight=Math.Max(rowHeight,height);
                }
                int required=Math.Max(50,total+rowHeight+actions.Padding.Vertical);
                if((int)rightLayout.RowStyles[2].Height!=required)
                {
                    rightLayout.RowStyles[2].Height=required;
                    // This can run during the parent's current layout pass; that pass
                    // already measured the old height. Apply the new row after it ends.
                    Post(()=>{rightLayout.PerformLayout();AdjustInformationHeight();});
                }
            }
            finally{arrangingActions=false;}
        }
        void Buttons(bool isCal,bool isProject,bool isStats){actions.FlowDirection=FlowDirection.RightToLeft;bool hasActions=isCal||isProject||isStats;rightLayout.RowStyles[2].Height=hasActions?50:0;actions.Visible=hasActions;editButton.Visible=saveButton.Visible=cancelButton.Visible=isCal;jointButton.Visible=isCal;comparisonButton.Visible=isCal&&SelectedNodeKind=="cal-comparison";comparisonButton.Enabled=!editing;findComparisonJointsButton.Visible=comparisonButton.Visible;findComparisonJointsButton.Enabled=CanUseComparisonGeometry();lengthButton.Visible=isProject;phaseButtonsHost.Visible=isStats;editButton.Enabled=!editing;saveButton.Enabled=cancelButton.Enabled=editing;jointButton.Enabled=!editing;AdjustPhaseActionHeight();}
        void Activate(TreeNode node)
        {
            if(node==null||project==null)return;var n=node.Tag as NodeData;if(n==null)return;waveCancellation.Cancel();selectionGeneration++;loadingWave=false;projectLoadingView.Visible=false;shellLayout.Visible=true;ClearDetail();SelectedNodeKind=n.Kind;selectionSequence++;
            if(n.Kind=="project")RefreshProjectInformationAsync();else if(n.Kind=="cal-comparison")ActivateAllCalibrations();else if(n.Kind.StartsWith("stats")){statsMode=n.Kind=="stats"?1:int.Parse(n.Kind.Substring(5));ShowStatistics(statsMode);}else if(n.Value is BrowserRecord)LoadRecord((BrowserRecord)n.Value);else {heading.Text=node.Text;ShowRecords((List<BrowserRecord>)n.Value);}
            AdjustInformationHeight();
            SelectionChanged?.Invoke(this,EventArgs.Empty);
        }
        void ArrangeProjectInformation()
        {
            if(itemViewport.IsDisposed||arrangingProjectInformation)return;
            arrangingProjectInformation=true;
            try
            {
                // Reserve the scrollbar width once so a narrow host reflows instead of clipping its last column.
                int width=Math.Max(1,itemViewport.Width-SystemInformation.VerticalScrollBarWidth-4);
                int availableHeight=Math.Max(1,itemViewport.ClientSize.Height);
                if(projectInformationLayout!=null&&projectInformationLayout.Matches(item,itemViewport,width,availableHeight,languageIndex))return;
                Size preferred=item.PreferredSizeForViewport(width,availableHeight);
                int contentWidth=Math.Max(width,preferred.Width);
                int contentHeight=Math.Max(availableHeight,preferred.Height);
                Rectangle bounds=new Rectangle(0,0,contentWidth,contentHeight);
                if(item.Bounds!=bounds)item.Bounds=bounds;
                Size extent=new Size(0,contentHeight);
                if(itemViewport.AutoScrollMinSize!=extent)itemViewport.AutoScrollMinSize=extent;
                // WinForms can keep the previous wide display rectangle after a shrink.
                if(itemViewport.HorizontalScroll.Visible &&
                   contentWidth<=itemViewport.Width-SystemInformation.VerticalScrollBarWidth)
                {
                    itemViewport.HorizontalScroll.Visible=false;
                    itemViewport.PerformLayout();
                }
                projectInformationLayout=new ProjectInformationLayoutSnapshot(item,itemViewport,width,availableHeight,languageIndex);
            }
            finally{arrangingProjectInformation=false;}
        }
        void ShowProjectInfo()
        {
            SelectedNodeKind="project";SelectedFilePath=project.ItemPath??"";heading.Text=T("项目信息","Project information");
            if(item.IsLoaded){ShowControl(itemViewport);ArrangeProjectInformation();}else {ShowControl(new Label{Text=ItemLoadMessage(item.LastError),TextAlign=ContentAlignment.MiddleCenter});}
            Buttons(false,true,false);information.Text=ProjectSummary();AdjustInformationHeight();
        }
        string ItemLoadMessage(string error)
        {
            if(string.Equals(error,"The file contains no recognized ITEM information fields.",StringComparison.Ordinal))
                return T("ITEM.in 中未识别到项目信息，请检查文件内容。","No project information recognized in ITEM.in. Check its contents.");
            return error;
        }
        static string FileHash(string path){using(var hash=SHA256.Create())using(var file=File.OpenRead(path))return Convert.ToBase64String(hash.ComputeHash(file));}
        bool ValidCalibrationGeometry()
        {
            var w=selectedWave;return selectedRecord!=null&&selectedRecord.Kind=="cal"&&w!=null&&w.X.Length>1&&BrowserPlot.Finite(w.SampleRateHz)&&w.SampleRateHz>0&&BrowserPlot.Finite(w.CableLengthMetres)&&w.CableLengthMetres>0&&BrowserPlot.Finite(w.VelocityMPerUs)&&w.VelocityMPerUs>0&&BrowserPlot.Finite(w.IncidentSeconds)&&BrowserPlot.Finite(w.ReflectionSeconds)&&w.IncidentSeconds>=0&&w.ReflectionSeconds>w.IncidentSeconds&&w.ReflectionSeconds<=w.X.Last();
        }
        static string F(double v,string format){return BrowserPlot.Finite(v)?v.ToString(format,CultureInfo.InvariantCulture):"—";}
        void PrepareTable()
        {
            table.Columns.Clear();
            string[] names={T("相别","Phase"),T("加压次序","Sequence"),T("电压 / kV","Voltage / kV"),T("最大放电量 / pC","Maximum PD / pC"),"tan δ / %",T("文件","File")};
            int[] minimumWidths={45,64,78,112,68,145};
            float[] fillWeights={7,10,13,17,11,42};
            for(int i=0;i<names.Length;i++)
            {
                int index=table.Columns.Add(names[i],names[i]);
                table.Columns[index].MinimumWidth=minimumWidths[i];
                table.Columns[index].FillWeight=fillWeights[i];
            }
            table.Rows.Clear();
        }
        void FillTable(IEnumerable<BrowserRecord> rows)
        {
            PrepareTable();
            foreach (var record in rows)
            {
                int index = table.Rows.Add(record.Phase, record.Order, F(record.VoltageKv, "0.0"),
                    F(record.MaxPdPc, "0"), F(record.TanDelta, "0.####"), record.Name);
                table.Rows[index].Tag = record;
                table.Rows[index].Cells[5].ToolTipText=record.Name;
            }
        }
        void ShowRecords(List<BrowserRecord> rows){if(rows==null)return;table.Visible=true;table.Dock=DockStyle.Fill;ShowControl(table);FillTable(rows);information.Clear();}
        void NavigateTableRow(int rowIndex)
        {
            if (project == null || rowIndex < 0 || rowIndex >= table.Rows.Count) return;
            var record = table.Rows[rowIndex].Tag as BrowserRecord;
            if (record != null) SelectFile(record.Path);
        }
        void LoadRecord(BrowserRecord record)
        {
            selectedRecord=record;SelectedFilePath=record.Path;SelectedNodeKind=record.Kind;heading.Text=record.Name;
            waveLoadFailed=false;lastError="";
            information.Text=T("正在读取波形…","Loading waveform…");loadingWave=true;SetStatus(T("正在读取波形…","Loading waveform…"));int gen=selectionGeneration,pg=projectGeneration;waveCancellation.Dispose();waveCancellation=new CancellationTokenSource();var cancellation=waveCancellation.Token;
            ShowDetailLoading(gen,pg);
            Task.Run(()=>
            {
                try
                {
                    BrowserWaveform wave;string fileHash="";
                    lock(waveGate)
                    {
                        if(gen!=selectionGeneration||pg!=projectGeneration)return;
                        cancellation.ThrowIfCancellationRequested();
                        if(record.Kind=="cal")fileHash=FileHash(record.Path);
                        wave=BrowserReader.LoadWaveform(record,cancellation);
                        if(record.Kind=="cal"&&fileHash!=FileHash(record.Path))throw new IOException("CAL changed while loading; reload the project.");
                    }
                    if(record.Kind=="dem")BrowserTestWaveformData.Prepare(wave,cancellation);
                    Post(()=>
                    {
                        if(gen!=selectionGeneration||pg!=projectGeneration)return;
                        try
                        {
                            cancellation.ThrowIfCancellationRequested();
                            selectedWave=wave;selectedCalHash=fileHash;
                            RenderWave();
                        }
                        catch(Exception ex){FailWaveLoad(ex,gen,pg);}
                        finally{if(gen==selectionGeneration&&pg==projectGeneration){loadingWave=false;HideDetailLoading(gen,pg);}}
                    });
                }
                catch(Exception ex){Post(()=>FailWaveLoad(ex,gen,pg));}
            });
        }
        void FailWaveLoad(Exception error,int generation,int projectId)
        {
            if(generation!=selectionGeneration||projectId!=projectGeneration)return;
            // A failed display must not leave a usable/editable result behind.
            // Keep only its selected identity so an explicit same-file retry works.
            selectedWave=null;selectedCalHash="";waveLoadFailed=true;lastError=error.Message;
            try
            {
                detail.Controls.Clear();
                if(generation!=selectionGeneration||projectId!=projectGeneration)return;
                Buttons(false,false,false);editButton.Enabled=false;jointButton.Enabled=false;
                if(generation!=selectionGeneration||projectId!=projectGeneration)return;
                string message=error is OperationCanceledException
                    ?T("已取消读取。可重新选择此文件重试。","Loading was cancelled. Select this file again to retry.")
                    :T("无法显示此文件的波形。可重新选择此文件重试。","The waveform could not be displayed. Select this file again to retry.");
                information.Text=message;SetStatus(message);
            }
            finally{if(generation==selectionGeneration&&projectId==projectGeneration){loadingWave=false;HideDetailLoading(generation,projectId);}}
        }
        void RenderWave()
        {
            var w=selectedWave;var r=selectedRecord;if(w==null||r==null)return;information.Text=r.Kind=="cal"?CalibrationSummary(r,w):r.Kind=="dem"?TestSummary(r,w):w.Information+Environment.NewLine+r.Warning;AdjustInformationHeight();
            if(r.Kind=="cal")
            {
                if(w.MeasurementKind=="Capacitor")
                {
                    int index=Math.Max(0,Math.Min(w.Y.Length-1,(int)Math.Round(w.IncidentSampleIndex)));
                    plot.SetData(T("电容局放校准 · 无定位","Capacitor PD calibration · no localization"),T("时间 / μs","Time / μs"),w.YLabel,"",
                        new BrowserSeries{Name=r.Name,X=w.X.Select(x=>x*1e6).ToArray(),Y=w.Y,Color=BrowserTheme.Cyan},
                        new BrowserSeries{Name=T("入射波","Incident"),X=new[]{w.IncidentSeconds*1e6,w.IncidentSeconds*1e6},Y=new[]{w.Y.Min(),w.Y[index]},Color=Color.DarkOrange});
                    ShowControl(plot);Buttons(true,false,false);editButton.Enabled=false;jointButton.Visible=false;
                    SetStatus(T("电容局放：仅入射波和校准系数，无定位。","Capacitor PD: incident and calibration coefficient only; no localization."));return;
                }
                if(!ValidCalibrationGeometry())
                {
                    plot.SetSingleCalibrationData(T("时间 / μs","Time / μs"),w.YLabel,new BrowserSeries{Name=r.Name,X=w.X.Select(x=>x*1e6).ToArray(),Y=w.Y,Color=BrowserTheme.Cyan});ShowControl(plot);Buttons(true,false,false);editButton.Enabled=false;jointButton.Enabled=false;
                    SetStatus(T("入射/反射时间、采样率、长度或波速缺失或越界，已禁用距离编辑。","Invalid markers, sample rate, length or velocity. Distance editing is disabled."));return;
                }
                calibration.SetWaveformXY(w.X.Select(x=>x*1e6).ToArray(),w.Y);calibration.ChartTitle=T("校准波形","Calibration waveform");calibration.YAxisTitle=T("原始校准幅值 / V","Calibration amplitude / V");calibration.XAxisTitle=T("时间 / μs","Time / μs");calibration.IncidentCaption=T("入射波","Incident");calibration.ReflectionCaption=T("反射波","Reflection");
                if(BrowserPlot.Finite(w.VelocityMPerUs)&&w.VelocityMPerUs>0&&w.CableLengthMetres>0)calibration.SetCableParameters(w.VelocityMPerUs,w.CableLengthMetres);
                if(BrowserPlot.Finite(w.IncidentSeconds)&&BrowserPlot.Finite(w.ReflectionSeconds))calibration.SetCursorPositions(w.IncidentSeconds*1e6,w.ReflectionSeconds*1e6);
                if(IsFormalCalibration(r.Path))calibration.SetDecayPulsePeakReferences(w.IncidentSampleIndex,w.ReflectionSampleIndex);
                else calibration.ClearDecayPulsePeakReferences();
                calibration.ShowDecayCurve=ShouldShowCalibrationDecay(r);
                calibration.FitPulseView();
                RefreshCalibrationJointMarkers();calibration.AllowCursorEditing=editing;ShowControl(calibration);Buttons(true,false,false);
                editButton.Enabled=!IsFormalCalibration(r.Path)&&BrowserPlot.Finite(w.IncidentSeconds)&&w.ReflectionSeconds>w.IncidentSeconds&&w.CableLengthMetres>0;
            }
            else if(r.Kind=="dem")
            {
                testWaveform.SetWaveform(w,languageIndex);
                ShowControl(testWaveform);Buttons(false,false,false);
            }
            else {RenderLocationWaveform(r,w);return;}
            SetStatus(T("已显示：","Displayed: ")+r.Name+(r.Kind=="cal"&&!String.IsNullOrEmpty(calibration.PulseViewMessage)?" · "+calibration.PulseViewMessage:""));
        }
        internal static bool ShouldShowCalibrationDecay(BrowserRecord record)
        {
            string kind;
            // Only an explicitly parsed TDR archive changes this display flag.
            // Names or unknown historical records do not invent a TDR identity.
            return record==null||!record.Metadata.TryGetValue("记录类型",out kind)||kind!="TDR测距；采用波速，不是电量校准";
        }
        void RefreshCalibrationJointMarkers()
        {
            if(project==null||selectedRecord==null||selectedRecord.Kind!="cal"||selectedWave==null)return;
            double[] displayedJoints=project.JointPositionsMetres,phaseJoints;
            if(selectedRecord.Phase!="ALL")
            {
                if(project.JointPositionsByPhase.TryGetValue(selectedRecord.Phase,out phaseJoints))displayedJoints=phaseJoints;
                else if(project.JointPositionsByPhase.TryGetValue("ALL",out phaseJoints))displayedJoints=phaseJoints;
                else displayedJoints=new double[0];
            }
            calibration.SetCablePositions(displayedJoints.Where(x=>x>=0&&x<=calibration.CableLengthMeters).ToArray());
        }
        public bool SelectFile(string filePath){if(InvokeRequired)return (bool)Invoke(new Func<string,bool>(SelectFile),filePath);if(editing||saving)return Error(T("请先保存或取消。","Save or cancel first."));if(string.IsNullOrWhiteSpace(filePath))return Error(T("未找到数据文件。","Data file not found."));try{filePath=Path.GetFullPath(filePath);}catch(Exception ex) when (ex is ArgumentException||ex is NotSupportedException||ex is PathTooLongException){return Error(T("数据文件路径无效。","Invalid data file path."));}foreach(TreeNode root in tree.Nodes){TreeNode found=FindFile(root,filePath);if(found!=null){bool restoreSelection=tree.SelectedNode==found&&(SelectedNodeKind=="cal-comparison"||(waveLoadFailed&&!loadingWave));tree.SelectedNode=found;if(restoreSelection)Activate(found);found.EnsureVisible();return true;}}return Error(T("未找到数据文件。","Data file not found."));}
        TreeNode FindFile(TreeNode n,string p){var d=n.Tag as NodeData;var r=d==null?null:d.Value as BrowserRecord;if(r!=null&&string.Equals(r.Path,p,StringComparison.OrdinalIgnoreCase))return n;foreach(TreeNode child in n.Nodes){var f=FindFile(child,p);if(f!=null)return f;}return null;}
        public bool SelectStatistics(int mode){if(mode<1||mode>3)return Error("Statistics mode must be 1, 2 or 3.");if(InvokeRequired)return (bool)Invoke(new Func<int,bool>(SelectStatistics),mode);if(project==null||editing||saving)return false;tree.SelectedNode=tree.Nodes[4].Nodes[mode-1];return true;}
        public void SetPhaseVisibility(bool phaseA,bool phaseB,bool phaseC){Ui(()=>{bool previousSync=sync;sync=true;try{phaseButtons[0].Checked=phaseA;phaseButtons[1].Checked=phaseB;phaseButtons[2].Checked=phaseC;}finally{sync=previousSync;}if(!sync&&SelectedNodeKind.StartsWith("stats"))ShowStatistics(statsMode);});}
        public bool OpenProjectFolder(){try{if(project==null||!Directory.Exists(projectPath))return Error(T("请先加载项目。","Load a project first."));Process.Start(new ProcessStartInfo{FileName=projectPath,UseShellExecute=true});return true;}catch(Exception ex){return Error(ex.Message);}}
        protected override void Dispose(bool disposing){if(disposing&&!IsDisposed){projectCancellation.Cancel();waveCancellation.Cancel();projectCancellation.Dispose();waveCancellation.Dispose();actionTips.Dispose();navigationIcons.Dispose();projectGeneration++;selectionGeneration++;DisposeDetailLoadingView();DisposeLocationAnalysisView();DisposeCalibrationComparison();foreach(var c in new Control[]{itemViewport,item,calibration,testWaveform,plot,table,statsHost,statisticsPlot})if(!c.IsDisposed)c.Dispose();}base.Dispose(disposing);}
    }
}
