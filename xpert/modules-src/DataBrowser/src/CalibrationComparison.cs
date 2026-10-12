using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly BrowserButton comparisonButton=new BrowserButton();
        bool loadingComparison;
        bool comparisonHasDisplayed,comparisonDisplayedAlign;
        int activeComparisonGeneration=-1;
        readonly Panel comparisonHost=new Panel(),comparisonOptions=new Panel(),comparisonOptionsContent=new Panel();
        readonly CheckedListBox comparisonRecordsList=new CheckedListBox();
        readonly TextBox comparisonSearch=new TextBox();
        readonly CheckBox comparisonNormalize=new CheckBox(),comparisonAlign=new CheckBox(),comparisonAverage=new CheckBox();
        readonly Label comparisonCount=new Label(),comparisonSearchLabel=new Label();
        readonly Label comparisonAverageNotice=new Label();
        readonly Button comparisonSelectAll=new HVX.Presentation.R2RoundedButton(),comparisonClearAll=new HVX.Presentation.R2RoundedButton();
        readonly Button comparisonFold=new HVX.Presentation.R2RoundedButton();
        readonly ToolTip comparisonTips=new ToolTip();
        bool arrangingComparison;
        readonly System.Windows.Forms.Timer comparisonDebounce=new System.Windows.Forms.Timer{Interval=150};
        readonly HashSet<string> comparisonSelected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,ComparisonCachedWave> comparisonCache=new Dictionary<string,ComparisonCachedWave>(StringComparer.OrdinalIgnoreCase);
        readonly object comparisonCacheGate=new object();
        BrowserRecord[] comparisonRecords=new BrowserRecord[0];string comparisonRoot="";bool updatingComparison;
        void InitializeCalibrationComparison()
        {
            comparisonButton.Text="校准对比";comparisonButton.Width=128;comparisonButton.Margin=new Padding(3,0,3,0);
            actions.Controls.Add(comparisonButton);comparisonButton.Click+=delegate{if(loadingComparison){comparisonDebounce.Stop();waveCancellation.Cancel();return;}if(SelectedNodeKind=="cal-comparison")QueueComparison();else OpenAllCalibrations();};
            InitializeComparisonJoints();
            comparisonHost.Name="AllCalibrationsView";comparisonHost.BackColor=BrowserTheme.Surface;
            comparisonOptions.Name="CalibrationComparisonOptions";comparisonOptions.Dock=DockStyle.Right;comparisonOptions.Width=280;comparisonOptions.AutoScroll=true;comparisonOptions.BackColor=BrowserTheme.Canvas;
            comparisonOptionsContent.Name="CalibrationComparisonOptionsContent";comparisonOptions.Controls.Add(comparisonOptionsContent);
            comparisonFold.Name="CollapseCalibrationList";comparisonFold.Text="▶";comparisonFold.Width=26;comparisonFold.Dock=DockStyle.Right;
            comparisonFold.Click+=delegate{comparisonOptions.Visible=!comparisonOptions.Visible;comparisonFold.Text=comparisonOptions.Visible?"▶":"◀";ArrangeComparisonOptions();};
            comparisonRecordsList.Name="CalibrationComparisonRecords";comparisonRecordsList.CheckOnClick=true;comparisonRecordsList.IntegralHeight=false;comparisonRecordsList.HorizontalScrollbar=true;
            comparisonSearch.Name="ComparisonSearch";comparisonSearch.AccessibleName="搜索校准记录";
            comparisonSearchLabel.Name="ComparisonSearchLabel";comparisonSearchLabel.Text="搜索校准记录";
            comparisonNormalize.Name="NormalizeCalibrationPeaks";comparisonNormalize.Text="归一化（基线校正、峰值统一）";comparisonNormalize.Checked=true;
            comparisonAlign.Name="AlignCalibrationIncident";comparisonAlign.Text="入射游标对齐";comparisonAlign.Checked=false;
            comparisonAverage.Name="AverageSelectedCalibrations";comparisonAverage.Text="多校准平均（仅显示平均）";comparisonAverage.Checked=false;
            comparisonSelectAll.Name="ComparisonSelectAll";comparisonSelectAll.Text="全选";
            comparisonClearAll.Name="ComparisonClearAll";comparisonClearAll.Text="清选";
            comparisonCount.Name="ComparisonSelectionSummary";
            comparisonAverageNotice.Name="ComparisonAverageNotice";comparisonAverageNotice.ForeColor=Color.DarkOrange;
            comparisonOptionsContent.Controls.AddRange(new Control[]{comparisonAverageNotice,comparisonSearchLabel,comparisonSearch,comparisonNormalize,comparisonAlign,comparisonAverage,comparisonSelectAll,comparisonClearAll,comparisonRecordsList,comparisonCount});
            comparisonTips.SetToolTip(comparisonNormalize,"仅对显示副本进行基线校正和绝对峰值归一化，不修改校准系数或原始文件。");
            comparisonTips.SetToolTip(comparisonAverage,"对勾选的有效校准等权平均；按当前归一化/入射对齐设置，在共同时间范围按实际采样时间插值后平均。仅显示，不写入项目。");
            comparisonHost.Controls.Add(comparisonFold);comparisonHost.Controls.Add(comparisonOptions);
            comparisonHost.SizeChanged+=delegate{ArrangeComparisonOptions();};comparisonOptions.SizeChanged+=delegate{ArrangeComparisonOptions();};
            foreach(Control control in comparisonOptionsContent.Controls)control.FontChanged+=delegate{ArrangeComparisonOptions();};
            comparisonCount.TextChanged+=delegate{ArrangeComparisonOptions();};
            comparisonAverageNotice.TextChanged+=delegate{ArrangeComparisonOptions();};
            comparisonSelectAll.Click+=delegate{foreach(var r in comparisonRecords)comparisonSelected.Add(r.Path);SynchronizeComparisonChecks();QueueComparison();};
            comparisonClearAll.Click+=delegate{comparisonSelected.Clear();SynchronizeComparisonChecks();QueueComparison();};
            comparisonSearch.TextChanged+=delegate{if(!updatingComparison)RefreshComparisonList();};
            comparisonRecordsList.ItemCheck+=delegate(object sender,ItemCheckEventArgs e){if(updatingComparison)return;var choice=(ComparisonChoice)comparisonRecordsList.Items[e.Index];if(e.NewValue==CheckState.Checked)comparisonSelected.Add(choice.Record.Path);else comparisonSelected.Remove(choice.Record.Path);QueueComparison();};
            comparisonNormalize.CheckedChanged+=delegate{QueueComparison();};comparisonAlign.CheckedChanged+=delegate{QueueComparison();};comparisonAverage.CheckedChanged+=delegate{QueueComparison();};
            comparisonDebounce.Tick+=async delegate{comparisonDebounce.Stop();if(SelectedNodeKind!="cal-comparison"||project==null)return;await CompareCalibrationsAsync(comparisonSelected.ToArray(),comparisonNormalize.Checked,comparisonAlign.Checked);};
            ArrangeComparisonOptions();
        }
        // Explicit, font-measured rows avoid fixed FlowLayout heights clipping
        // checkboxes at 125/150/200% DPI. A small host scrolls the complete panel.
        void ArrangeComparisonOptions()
        {
            if(arrangingComparison||comparisonOptions.IsDisposed)return;arrangingComparison=true;
            try
            {
                int pad=Math.Max(8,comparisonNormalize.Font.Height/2),gap=Math.Max(4,comparisonNormalize.Font.Height/4);
                int desired=Math.Max(280,TextRenderer.MeasureText(comparisonNormalize.Text,comparisonNormalize.Font).Width+pad*2+30);
                int cap=Math.Max(220,(int)(comparisonHost.ClientSize.Width*.48));
                comparisonOptions.Width=Math.Min(desired,cap);comparisonFold.Width=Math.Max(26,comparisonFold.Font.Height+10);
                // Reserve a scrollbar gutter even before a short host needs it;
                // its appearance then cannot alter text wrapping recursively.
                int width=Math.Max(160,comparisonOptions.ClientSize.Width-pad*2-SystemInformation.VerticalScrollBarWidth);
                int y=0;
                comparisonAverageNotice.SetBounds(0,y,width,ComparisonTextHeight(comparisonAverageNotice,width));y+=comparisonAverageNotice.Height+gap;
                comparisonSearchLabel.SetBounds(0,y,width,ComparisonTextHeight(comparisonSearchLabel,width));y+=comparisonSearchLabel.Height+gap;
                comparisonSearch.SetBounds(0,y,width,comparisonSearch.PreferredHeight);y+=comparisonSearch.Height+gap;
                foreach(var box in new[]{comparisonNormalize,comparisonAlign,comparisonAverage})
                {int h=Math.Max(box.Font.Height+10,ComparisonTextHeight(box,Math.Max(60,width-28))+8);box.SetBounds(0,y,width,h);y+=h+gap;}
                int buttonHeight=Math.Max(30,Math.Max(comparisonSelectAll.Font.Height,comparisonClearAll.Font.Height)+14),half=(width-gap)/2;
                comparisonSelectAll.SetBounds(0,y,half,buttonHeight);comparisonClearAll.SetBounds(half+gap,y,width-half-gap,buttonHeight);y+=buttonHeight+gap;
                int countHeight=Math.Max(comparisonCount.Font.Height*2+8,ComparisonTextHeight(comparisonCount,width)+8);
                int listHeight=Math.Max(comparisonRecordsList.Font.Height*4+12,comparisonOptions.ClientSize.Height-pad*2-y-gap-countHeight);
                comparisonRecordsList.SetBounds(0,y,width,listHeight);y+=listHeight+gap;
                comparisonCount.SetBounds(0,y,width,countHeight);y+=countHeight;
                comparisonOptionsContent.SetBounds(pad+comparisonOptions.AutoScrollPosition.X,pad+comparisonOptions.AutoScrollPosition.Y,width,y);
                comparisonOptions.AutoScrollMinSize=new Size(0,y+pad*2);
            }
            finally{arrangingComparison=false;}
        }
        static int ComparisonTextHeight(Control control,int width)
        {return TextRenderer.MeasureText(String.IsNullOrEmpty(control.Text)?" ":control.Text,control.Font,new Size(Math.Max(1,width),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;}
        public bool OpenAllCalibrations()
        {
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(OpenAllCalibrations));
            if(project==null||editing||saving)return false;
            TreeNode node=tree.Nodes.Cast<TreeNode>().SelectMany(n=>n.Nodes.Cast<TreeNode>()).FirstOrDefault(n=>n.Name=="cal-comparison");
            if(node==null)return Error("至少需要两条校准记录才能叠加对比。");
            if(tree.SelectedNode!=node)tree.SelectedNode=node;else ActivateAllCalibrations();return true;
        }
        void ActivateAllCalibrations()
        {
            updatingComparison=true;
            try
            {
                comparisonRecords=project.Calibrations.ToArray();
                if(!String.Equals(comparisonRoot,projectPath,StringComparison.OrdinalIgnoreCase)){comparisonRoot=projectPath;comparisonSelected.Clear();foreach(var r in comparisonRecords)comparisonSelected.Add(r.Path);comparisonNormalize.Checked=true;comparisonAlign.Checked=false;comparisonAverage.Checked=false;comparisonSearch.Text="";}
                comparisonSelected.RemoveWhere(p=>!comparisonRecords.Any(r=>String.Equals(r.Path,p,StringComparison.OrdinalIgnoreCase)));
                selectedRecord=null;selectedWave=null;SelectedNodeKind="cal-comparison";SelectedFilePath="";heading.Text="全部校准 · 波形叠加";
                comparisonHasDisplayed=false;plot.SetData("正在准备校准对比…","时间 / μs",comparisonNormalize.Checked?"归一化幅值":"幅值 / V","");
                RefreshComparisonList();ShowComparisonView();
            }
            finally{updatingComparison=false;}
            QueueComparison();
        }
        void ShowComparisonView()
        {
            if(plot.Parent!=comparisonHost){comparisonHost.Controls.Add(plot);plot.Dock=DockStyle.Fill;plot.BringToFront();}
            // Do not detach/re-add the complete page on every checked record.
            if(comparisonHost.Parent!=detail)ShowControl(comparisonHost);
            ArrangeComparisonOptions();Buttons(true,false,false);editButton.Enabled=saveButton.Enabled=cancelButton.Enabled=jointButton.Enabled=false;
            editButton.Visible=saveButton.Visible=cancelButton.Visible=jointButton.Visible=false;AdjustPhaseActionHeight();
        }
        void RefreshComparisonList()
        {
            bool old=updatingComparison;updatingComparison=true;comparisonRecordsList.BeginUpdate();
            int oldTop=comparisonRecordsList.TopIndex;
            var selected=comparisonRecordsList.SelectedItem as ComparisonChoice;
            string selectedPath=selected==null?null:selected.Record.Path;
            try{
                comparisonRecordsList.Items.Clear();string search=comparisonSearch.Text.Trim();
                foreach(var r in comparisonRecords.Where(r=>search.Length==0||r.Name.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0||r.Phase.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0))comparisonRecordsList.Items.Add(new ComparisonChoice(r),comparisonSelected.Contains(r.Path));
                for(int i=0;i<comparisonRecordsList.Items.Count;i++)if(String.Equals(((ComparisonChoice)comparisonRecordsList.Items[i]).Record.Path,selectedPath,StringComparison.OrdinalIgnoreCase)){comparisonRecordsList.SelectedIndex=i;break;}
                if(comparisonRecordsList.Items.Count>0)comparisonRecordsList.TopIndex=Math.Min(oldTop,comparisonRecordsList.Items.Count-1);
                UpdateComparisonCount();
            }
            finally{comparisonRecordsList.EndUpdate();updatingComparison=old;}
        }
        void SynchronizeComparisonChecks()
        {
            bool old=updatingComparison;updatingComparison=true;comparisonRecordsList.BeginUpdate();
            try{for(int i=0;i<comparisonRecordsList.Items.Count;i++){bool wanted=comparisonSelected.Contains(((ComparisonChoice)comparisonRecordsList.Items[i]).Record.Path);if(comparisonRecordsList.GetItemChecked(i)!=wanted)comparisonRecordsList.SetItemChecked(i,wanted);}UpdateComparisonCount();}
            finally{comparisonRecordsList.EndUpdate();updatingComparison=old;}
        }
        void UpdateComparisonCount(){comparisonCount.Text="已勾选 "+comparisonSelected.Count+" / "+comparisonRecords.Length+" 条\r\n搜索仅隐藏列表，不取消已勾选项。";UpdateComparisonAverageNotice(-1);}
        void UpdateComparisonAverageNotice(int contributing)
        {
            int count=contributing>=0?contributing:comparisonSelected.Count;
            comparisonAverageNotice.Text=comparisonAverage.Checked&&count==1
                ?T("仅 1 条参与，结果与该条一致；未形成多记录平均。请勾选更多记录。","Only one record contributes, so the result equals that record. Check more records for a multi-record mean.")
                :T("复选框决定参与记录；高亮行只表示当前选择。","Checkboxes select contributors; a highlighted row alone does not include a record.");
        }
        void QueueComparison()
        {
            if(updatingComparison||SelectedNodeKind!="cal-comparison")return;
            UpdateComparisonCount();waveCancellation.Cancel();selectionGeneration++;loadingWave=false;comparisonDebounce.Stop();
            ClearComparisonGeometry();
            if(comparisonSelected.Count==0){comparisonHasDisplayed=false;plot.SetData("校准对比",comparisonAlign.Checked?"相对入射时间 / μs":"时间 / μs",comparisonNormalize.Checked?"归一化幅值":"幅值 / V","");SetStatus("未选择校准记录，请勾选需要显示的波形。");return;}
            comparisonDebounce.Start();
        }
        void ResetCalibrationComparison()
        {
            comparisonDebounce.Stop();comparisonHasDisplayed=false;comparisonRoot="";comparisonSelected.Clear();comparisonRecords=new BrowserRecord[0];lock(comparisonCacheGate)comparisonCache.Clear();
            ClearComparisonGeometry();
        }
        void DisposeCalibrationComparison(){comparisonDebounce.Stop();comparisonDebounce.Dispose();comparisonTips.Dispose();comparisonHost.Dispose();lock(comparisonCacheGate)comparisonCache.Clear();}
        sealed class ComparisonChoice
        {
            internal readonly BrowserRecord Record;internal ComparisonChoice(BrowserRecord record){Record=record;}public override string ToString(){return Record.Name;}
        }
        bool RejectComparisonSelection(string message)
        {
            // An invalid request replaces the comparison intent as well as its
            // displayed evidence. An earlier worker must not install its mean
            // after this refusal, or restore busy/button state in its finally.
            comparisonDebounce.Stop();waveCancellation.Cancel();selectionGeneration++;activeComparisonGeneration=-1;
            loadingWave=loadingComparison=false;comparisonHasDisplayed=false;ClearComparisonGeometry();
            comparisonButton.Text="校准对比";comparisonButton.Enabled=!editing;
            if(SelectedNodeKind=="cal-comparison")
            {
                plot.SetData(T("校准对比失败","Calibration comparison failed"),comparisonAlign.Checked?"相对入射时间 / μs":"时间 / μs",comparisonNormalize.Checked?"归一化幅值":"幅值 / V","");
                information.Text=message;AdjustInformationHeight();
            }
            return Error(message);
        }
        // Null paths explicitly means every calibration in the currently loaded project.
        public async Task<bool> CompareCalibrationsAsync(string[] paths,bool normalize,bool align)
        {
            if(InvokeRequired)throw new InvalidOperationException("Start calibration comparison on the UI thread.");
            if(project==null||editing||saving||(loadingWave&&!loadingComparison))return false;
            BrowserRecord[] chosen=paths==null?project.Calibrations.ToArray():project.Calibrations.Where(r=>paths.Any(p=>String.Equals(p,r.Path,StringComparison.OrdinalIgnoreCase))).ToArray();
            if(chosen.Length<1||(paths!=null&&chosen.Length!=paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()))return RejectComparisonSelection(T("请选择当前项目的有效校准记录。","Choose valid calibration records from this project."));
            bool inPlace=SelectedNodeKind=="cal-comparison"&&comparisonHost.Parent==detail&&plot.Parent==comparisonHost;
            bool sameRecords=String.Equals(comparisonRoot,projectPath,StringComparison.OrdinalIgnoreCase)&&comparisonRecords.SequenceEqual(project.Calibrations);
            comparisonDebounce.Stop();updatingComparison=true;
            try{comparisonRoot=projectPath;comparisonRecords=project.Calibrations.ToArray();comparisonSelected.Clear();foreach(var r in chosen)comparisonSelected.Add(r.Path);comparisonNormalize.Checked=normalize;comparisonAlign.Checked=align;if(sameRecords)SynchronizeComparisonChecks();else RefreshComparisonList();}
            finally{updatingComparison=false;}
            waveCancellation.Cancel();waveCancellation.Dispose();waveCancellation=new System.Threading.CancellationTokenSource();
            var cancellation=waveCancellation.Token;int generation=++selectionGeneration,pg=projectGeneration;
            ClearComparisonGeometry();
            bool average=comparisonAverage.Checked;
            int budget=Math.Max(64,Math.Min(8192,2000000/Math.Max(1,comparisonRecords.Length)));var allRecords=comparisonRecords;
            selectedRecord=null;selectedWave=null;SelectedNodeKind="cal-comparison";SelectedFilePath="";lastError="";
            if(!inPlace){comparisonHasDisplayed=false;plot.SetData("正在读取所选校准…",align?"相对入射时间 / μs":"时间 / μs",normalize?"归一化幅值":"幅值 / V","");ShowComparisonView();}
            loadingWave=loadingComparison=true;activeComparisonGeneration=generation;comparisonButton.Text="取消对比";SetStatus("正在读取 "+chosen.Length+" 条校准对比波形"+(comparisonHasDisplayed?"，完成前保留上次曲线":"")+"，可取消或切换记录…");
            try {
                var prepared=await Task.Run(()=>{
                    if(average)return PrepareAverageComparison(chosen,normalize,align,cancellation);
                    var result=new ComparisonPrepared();
                    foreach(var record in chosen)
                    {
                        cancellation.ThrowIfCancellationRequested();try {
                        var wave=ComparisonWave(record,budget,pg,cancellation);int index=Array.IndexOf(allRecords,record);
                        if(align&&(!BrowserPlot.Finite(wave.IncidentSeconds)||wave.IncidentSeconds<wave.Preview.X.First()/1e6||wave.IncidentSeconds>wave.Preview.X.Last()/1e6))throw new InvalidDataException("缺少有效入射游标，不能假定对齐点；可取消入射对齐。");
                        double baseline=normalize?wave.Baseline:0,peak=normalize?wave.Peak:1;if(peak<=1e-15)throw new InvalidDataException(record.Name+"：零幅值波形不能归一化。");
                        result.Series.Add(new BrowserSeries{Name=(index+1)+". "+record.Name,Color=ComparisonColor(index),Dashed=index%2!=0,
                            X=wave.Preview.X.Select(x=>x-(align?wave.IncidentSeconds*1e6:0)).ToArray(),Y=wave.Preview.Y.Select(y=>(y-baseline)/peak).ToArray()});
                        }catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is InvalidOperationException||ex is FormatException||ex is OverflowException){result.Errors.Add(record.Name+"："+ex.Message);}
                    }
                    return result;
                },cancellation);
                cancellation.ThrowIfCancellationRequested();
                if(IsDisposed||generation!=selectionGeneration||pg!=projectGeneration)return false;
                bool displayed=false;Ui(()=>{
                if(IsDisposed||generation!=selectionGeneration||pg!=projectGeneration||cancellation.IsCancellationRequested)return;
                bool previousSync=sync;sync=true;try{tree.SelectedNode=tree.Nodes.Cast<TreeNode>().SelectMany(n=>n.Nodes.Cast<TreeNode>()).FirstOrDefault(n=>n.Name=="cal-comparison");}finally{sync=previousSync;}
                SelectedNodeKind="cal-comparison";SelectedFilePath="";heading.Text="多校准波形对比";
                plot.UpdateData((average?"多校准平均 · ":"校准对比 · ")+(normalize?"绝对峰值归一化":"原始幅值"),align?"相对入射时间 / μs":"时间 / μs",normalize?"归一化幅值":"幅值 / V","",comparisonHasDisplayed&&comparisonDisplayedAlign==align,prepared.Series.ToArray());
                comparisonHasDisplayed=prepared.Series.Count>0;comparisonDisplayedAlign=align;
                ApplyComparisonGeometry(prepared);
                string summary=average?"所选 "+chosen.Length+" 条，参与平均 "+prepared.SourceCount+" 条，显示 "+prepared.Series.Count+" 条平均曲线，排除 "+prepared.Errors.Count+" 条。":"所选 "+chosen.Length+" 条，已显示 "+prepared.Series.Count+" 条，未显示 "+prepared.Errors.Count+" 条。";
                information.Text=summary+"仅对显示副本保峰抽样，不修改校准文件、系数或原始波形。归一化使用完整波形峰值。\r\n"+prepared.Details+(prepared.Errors.Count>0?"排除原因：\r\n"+String.Join("\r\n",prepared.Errors)+"\r\n":"")+String.Join("\r\n",chosen.Select((r,i)=>(i+1)+". "+r.Path));
                if(comparisonJointProjectionWarning.Length>0)information.Text=comparisonJointProjectionWarning+Environment.NewLine+information.Text;
                comparisonCount.Text="已勾选 "+chosen.Length+" / "+comparisonRecords.Length+" 条；"+(average?prepared.SourceCount+" 条参与平均":"显示 "+prepared.Series.Count+" 条")+(prepared.Errors.Count>0?"\r\n排除 "+prepared.Errors.Count+" 条，原因见下方。":"\r\n搜索不会取消隐藏项的勾选。");
                UpdateComparisonAverageNotice(average?prepared.SourceCount:-1);
                string locationHint=average&&prepared.Geometry!=null&&prepared.Geometry.Kind!="Capacitor"?"平均图双击可复核接头。":average&&prepared.Geometry==null?"当前仅显示时间轴平均，距离及接头功能不可用。":"";
                lastError=String.Join(Environment.NewLine,prepared.Errors);AdjustInformationHeight();SetStatus(summary+"滚轮水平缩放，Shift+滚轮纵向缩放，左拖框选，右键恢复；"+locationHint+"归一化仅用于形态比较。");displayed=prepared.Series.Count>0;
                if(comparisonJointProjectionWarning.Length>0)SetStatus(comparisonJointProjectionWarning);
                });return displayed;
            }catch(OperationCanceledException){Ui(()=>{if(!IsDisposed&&generation==selectionGeneration&&pg==projectGeneration)SetStatus("已取消校准对比"+(comparisonHasDisplayed?"，保留上次显示结果，当前勾选尚未应用":"")+"，原始数据不变。");});return false;}
            catch(Exception ex){Ui(()=>{if(!IsDisposed&&generation==selectionGeneration&&pg==projectGeneration){comparisonHasDisplayed=false;plot.SetData("校准对比失败",align?"相对入射时间 / μs":"时间 / μs",normalize?"归一化幅值":"幅值 / V","");Error(ex.Message);information.Text=ex.Message;}});return false;}
            finally{Ui(()=>{if(!IsDisposed&&activeComparisonGeneration==generation){loadingComparison=false;comparisonButton.Text="校准对比";comparisonButton.Enabled=!editing;if(generation==selectionGeneration&&pg==projectGeneration)loadingWave=false;findComparisonJointsButton.Enabled=CanUseComparisonGeometry();}});}
        }
        sealed class ComparisonPrepared {internal readonly List<BrowserSeries> Series=new List<BrowserSeries>();internal readonly List<string> Errors=new List<string>();internal readonly Dictionary<string,string> SourceHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);internal int SourceCount;internal string Details="",GeometryWarning="";internal CalibrationTraceGeometry Geometry;internal double[] MeanX,MeanY;}
        sealed class ComparisonCachedWave
        {
            internal string Hash;internal int Budget;internal BrowserSeries Preview;internal double Baseline,Peak,IncidentSeconds;
        }
        ComparisonCachedWave ComparisonWave(BrowserRecord record,int budget,int projectVersion,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();string hash=ComparisonSourceHash(record.Path,cancellation);ComparisonCachedWave cached;
            // Formal archives also verify their external raw dependencies every read.
            lock(comparisonCacheGate)if(!IsFormalCalibration(record.Path)&&comparisonCache.TryGetValue(record.Path,out cached)&&cached.Hash==hash&&cached.Budget==budget)return cached;
            var wave=BrowserReader.LoadWaveform(record,cancellation);
            if(wave.Y.Length<16||wave.X.Length!=wave.Y.Length||wave.Y.Any(v=>!BrowserPlot.Finite(v))||wave.X.Any(v=>!BrowserPlot.Finite(v)))throw new InvalidDataException(record.Name+"：波形数据不完整。");
            int baselineCount=BrowserPlot.Finite(wave.IncidentSampleIndex)&&wave.IncidentSampleIndex>=16?Math.Min(200,(int)wave.IncidentSampleIndex/2):Math.Max(8,Math.Min(200,wave.Y.Length/20));
            double baseline=wave.Y.Take(baselineCount).Average(),peak=wave.Y.Max(v=>Math.Abs(v-baseline));
            cached=new ComparisonCachedWave{Hash=hash,Budget=budget,Baseline=baseline,Peak=peak,IncidentSeconds=wave.IncidentSeconds,Preview=ComparisonPreview(wave,0,record.Name,0,1,0,budget,cancellation)};
            cancellation.ThrowIfCancellationRequested();if(hash!=ComparisonSourceHash(record.Path,cancellation))throw new IOException(record.Name+"：文件在读取中发生变化，请重新加载。");
            lock(comparisonCacheGate)if(projectVersion==projectGeneration)comparisonCache[record.Path]=cached;return cached;
        }
        static void RequireComparisonLength(long length)
        {
            // Keep the same input-file bound as BrowserReader before hashing or parsing.
            if(length==0)throw new InvalidDataException("文件为空。");
            if(length>512L*1024*1024)throw new InvalidDataException("文件超过512 MiB限制。");
        }
        static string ComparisonSourceHash(string path,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();RequireComparisonLength(new FileInfo(path).Length);
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan))
                return ComparisonStreamHash(stream,cancellation);
        }
        static string ComparisonStreamHash(Stream stream,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();RequireComparisonLength(stream.Length);
            using(var sha=SHA256.Create())
            {
                var buffer=new byte[65536];long readTotal=0;
                while(true)
                {
                    cancellation.ThrowIfCancellationRequested();int count=stream.Read(buffer,0,buffer.Length);
                    cancellation.ThrowIfCancellationRequested();if(count==0)break;readTotal+=count;RequireComparisonLength(readTotal);
                    sha.TransformBlock(buffer,0,count,buffer,0);
                }
                cancellation.ThrowIfCancellationRequested();sha.TransformFinalBlock(new byte[0],0,0);return Convert.ToBase64String(sha.Hash);
            }
        }
        static Color ComparisonColor(int index)
        {
            double hue=(index*.618033988749895)%1*6,c=.72,x=c*(1-Math.Abs(hue%2-1)),m=.13;double r=0,g=0,bv=0;
            if(hue<1){r=c;g=x;}else if(hue<2){r=x;g=c;}else if(hue<3){g=c;bv=x;}else if(hue<4){g=x;bv=c;}else if(hue<5){r=x;bv=c;}else{r=c;bv=x;}
            return Color.FromArgb((int)((r+m)*255),(int)((g+m)*255),(int)((bv+m)*255));
        }
        static BrowserSeries ComparisonPreview(BrowserWaveform wave,int index,string name,double baseline,double peak,double origin,int budget,CancellationToken cancellation)
        {
            int n=wave.Y.Length;var keep=new System.Collections.Generic.List<int>();
            if(n<=budget){for(int i=0;i<n;i++)keep.Add(i);}
            else
            {
                keep.Add(0);int buckets=Math.Max(1,(budget-2)/2);
                for(int b=0;b<buckets;b++)
                {
                    cancellation.ThrowIfCancellationRequested();int start=1+(int)((long)(n-2)*b/buckets),end=1+(int)((long)(n-2)*(b+1)/buckets);
                    if(end<=start)continue;int min=start,max=start;
                    for(int i=start+1;i<end;i++){if((i&65535)==0)cancellation.ThrowIfCancellationRequested();if(wave.Y[i]<wave.Y[min])min=i;if(wave.Y[i]>wave.Y[max])max=i;}
                    keep.Add(Math.Min(min,max));if(min!=max)keep.Add(Math.Max(min,max));
                }
                keep.Add(n-1);
            }
            return new BrowserSeries{Name=(index+1)+". "+name,Color=ComparisonColor(index),Dashed=index%2!=0,
                X=keep.Select(i=>(wave.X[i]-origin)*1e6).ToArray(),Y=keep.Select(i=>(wave.Y[i]-baseline)/peak).ToArray()};
        }
    }
}
