using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        void RefreshAnalysisArchiveActions()
        {
            bool available=!IsAnalysisRunning&&!archiveDialogOpen&&!analysisLeaveDialogOpen&&!string.IsNullOrWhiteSpace(projectPath);
            if(evaluateButton!=null)evaluateButton.Enabled=available;
            if(reportButton!=null)reportButton.Enabled=available&&SourceRowCount>0;
        }
        internal bool suppressArchivePrompt;
        bool archiveDialogOpen,archivePromptQueued;
        int archivePromptGeneration;
        string lastAutoArchivePrompt="";
        [Category("Analysis"),DefaultValue(true)] public bool PromptSavedAnalysisOnProjectLoad {get;set;}=true;
        void ScheduleSavedAnalysisPrompt()
        {
            if(suppressArchivePrompt||!PromptSavedAnalysisOnProjectLoad||archiveDialogOpen||archivePromptQueued||!IsHandleCreated||!Visible||IsDisposed||projectPath==lastAutoArchivePrompt||string.IsNullOrWhiteSpace(projectPath))return;
            string expected=projectPath;int generation=++archivePromptGeneration;archivePromptQueued=true;
            try{BeginInvoke((MethodInvoker)(()=>{
                if(generation!=archivePromptGeneration)return;
                archivePromptQueued=false;if(IsDisposed||Disposing||suppressArchivePrompt||archiveDialogOpen||IsAnalysisRunning||expected!=projectPath)return;
                if(EnumerateSavedAnalyses().Length>0){lastAutoArchivePrompt=expected;ShowSavedAnalysisDialog();}}));}
            catch(InvalidOperationException){if(generation==archivePromptGeneration)archivePromptQueued=false;}
        }
        protected override void OnHandleDestroyed(EventArgs e){archivePromptGeneration++;archivePromptQueued=false;base.OnHandleDestroyed(e);}
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ScheduleSavedAnalysisPrompt();}
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible)ScheduleSavedAnalysisPrompt();}
        public bool RefreshSavedAnalysisList(){return ShowSavedAnalysisDialog();}
        bool analysisLeaveDialogOpen;
        public bool ConfirmLeaveAnalysis()
        {
            if(!HasUnsavedAnalysisChanges)return true;
            if(IsAnalysisRunning||archiveDialogOpen||analysisLeaveDialogOpen)return false;
            analysisLeaveDialogOpen=true;
            try {
                string project=projectPath,id=LoadedAnalysisId,choice;
                bool overwrite=CanOverwriteLoadedAnalysis;
                if(AnalysisLeaveChoiceProvider!=null)choice=AnalysisLeaveChoiceProvider(overwrite);
                else using(var form=CreateAnalysisLeaveDialog(overwrite)) {
                    DialogResult answer=form.ShowDialog(FindForm());
                    choice=answer==DialogResult.Yes?"SaveNew":answer==DialogResult.Retry?"Overwrite":answer==DialogResult.No?"Discard":"Cancel";
                }
                if(IsDisposed||projectPath!=project||LoadedAnalysisId!=id)return false;
                if(choice=="Discard")return true;
                if(choice=="SaveNew")return ShowSaveAnalysisDialog()&&!HasUnsavedAnalysisChanges;
                if(choice=="Overwrite"&&overwrite) {
                    bool saved=OverwriteLoadedAnalysis();
                    if(!saved&&AnalysisLeaveChoiceProvider==null)MessageBox.Show(FindForm(),ArchiveCustomerFailure(),L("Analysis not saved","分析未保存"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    return saved&&!HasUnsavedAnalysisChanges;
                }
                return false;
            } finally { analysisLeaveDialogOpen=false; }
        }
        Form CreateAnalysisLeaveDialog(bool canOverwrite)
        {
            var form=ArchiveDialog(L("Unsaved analysis","保存分析提示"),new Size(620,340));
            form.MinimumSize=new Size(500,350);
            var viewport=new Panel{Name="AnalysisLeaveViewport",Dock=DockStyle.Fill,AutoScroll=true};
            var layout=new TableLayoutPanel{Name="AnalysisLeaveActions",Dock=DockStyle.Bottom,ColumnCount=1,RowCount=4,Padding=new Padding(4)};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var label=new Label{Name="AnalysisLeaveWarning",Dock=DockStyle.Top,AutoSize=true,Text=L("The current analysis has changes. Save a new analysis or replace the loaded analysis before leaving. Replacing retains a complete recoverable backup.","当前分析有调整，请选择离开前的处理方式。覆盖当前分析会保留完整备份，可恢复。"),ForeColor=ReviewTheme.SecondaryInk};
            viewport.Controls.Add(label);
            string[] names={"SaveNewAnalysis","OverwriteCurrentAnalysis","DiscardAnalysisChanges","CancelAnalysisExit"};
            string[] captions={L("Save as a new analysis…","另存为新分析…"),L("Overwrite current analysis (keep backup)","覆盖当前分析（保留备份）"),L("Leave without saving","不保存退出"),L("Cancel; keep editing","取消，继续编辑")};
            DialogResult[] results={DialogResult.Yes,DialogResult.Retry,DialogResult.No,DialogResult.Cancel};
            for(int i=0;i<names.Length;i++) {
                // Native text rendering supports wrapped decisions; the regular
                // archive action painter intentionally renders one-line captions.
                var button=new HVX.Presentation.R2RoundedButton{Name=names[i],Text=captions[i],Dock=DockStyle.Fill,Font=Font,FlatStyle=FlatStyle.Flat,AutoEllipsis=false,UseCompatibleTextRendering=false};
                ReviewTheme.ButtonRole(button,false);button.DialogResult=results[i];button.Enabled=i!=1||canOverwrite;button.Margin=new Padding(0,4,0,4);
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));layout.Controls.Add(button,0,i);
                if(i==3)form.CancelButton=button;
            }
            bool arranging=false;
            Action fit=()=>{
                if(arranging)return;arranging=true;
                try {
                    label.MaximumSize=new Size(Math.Max(100,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth),0);
                    int height=layout.Padding.Vertical,width=Math.Max(100,layout.ClientSize.Width-layout.Padding.Horizontal-20);
                    for(int i=0;i<4;i++){
                        var button=layout.GetControlFromPosition(0,i);
                        int row=Math.Max(48,TextRenderer.MeasureText(button.Text,button.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+16)+button.Margin.Vertical;
                        layout.RowStyles[i].Height=row;height+=row;
                    }
                    layout.Height=height;
                }finally{arranging=false;}
            };
            form.Controls.Add(viewport);form.Controls.Add(layout);
            viewport.SizeChanged+=(s,e)=>fit();layout.SizeChanged+=(s,e)=>fit();label.FontChanged+=(s,e)=>fit();
            foreach(Control button in layout.Controls)button.FontChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();
            ReviewTheme.ReserveDialogBody(form,layout,new Size(500,350));return form;
        }
        public bool ShowSaveAnalysisDialog()
        {
            if(IsAnalysisRunning||archiveDialogOpen)return false;
            archiveDialogOpen=true;
            try
            {
                string suggested=SuggestedAnalysisName;
                if(AnalysisSaveNameProvider!=null)
                {
                    string expectedProject=projectPath;string chosen=AnalysisSaveNameProvider(suggested);
                    if(chosen==null||projectPath!=expectedProject)return false;
                    return SaveNamedAnalysis(chosen);
                }
                using(var form=ArchiveDialog(L("Save analysis","保存分析"),new Size(620,260)))
                {
                    form.MinimumSize=new Size(540,270);
                    var nameLabel=new Label{Text=L("Analysis name","分析名称"),Dock=DockStyle.Top,Height=Font.Height+12,TextAlign=ContentAlignment.MiddleLeft};
                    var input=new TextBox{Name="AnalysisName",Dock=DockStyle.Top,MaxLength=160,Text=suggested};
                    var message=new Label{Dock=DockStyle.Fill,Text=L("Keep the suggested project name and sequence, or enter a unique name. Existing analyses will not be overwritten.","可保留建议的项目名称与序号，也可输入其他名称；不会覆盖已保存的分析。"),ForeColor=ReviewTheme.SecondaryInk,Padding=new Padding(0,12,0,0)};
                    var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=Math.Max(40,Font.Height+20),FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                    var cancel=ActionButton(86);cancel.Text=L("Cancel","取消");cancel.DialogResult=DialogResult.Cancel;
                    var save=ActionButton(86);save.Text=L("Save","保存");ReviewTheme.ButtonRole(save,true);
                    var charts=ActionButton(128);charts.Text=L("Report attachments","报告附件");charts.Click+=(s,e)=>ShowReportAttachmentSelection();
                    footer.Controls.Add(cancel);footer.Controls.Add(save);footer.Controls.Add(charts);
                    form.Controls.Add(message);form.Controls.Add(input);form.Controls.Add(nameLabel);form.Controls.Add(footer);form.AcceptButton=save;form.CancelButton=cancel;
                    // Measure after parenting, when inherited font and DPI are final.
                    ReviewTheme.BindDialogFooter(form,footer);
                    save.Click+=(s,e)=>{save.Enabled=false;form.UseWaitCursor=true;
                        try{if(SaveNamedAnalysis(input.Text)){SetReviewOperationStatus(L("Analysis saved: ","分析已保存：")+HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(input.Text.Trim()));form.DialogResult=DialogResult.OK;form.Close();}
                            else{message.Text=ArchiveCustomerFailure();message.ForeColor=Color.Firebrick;}}
                        finally{form.UseWaitCursor=false;save.Enabled=true;}};
                    form.Shown+=(s,e)=>{input.Focus();input.SelectAll();};
                    return form.ShowDialog(FindForm())==DialogResult.OK;
                }
            }
            finally{archiveDialogOpen=false;}
        }
        public bool ShowSavedAnalysisDialog()
        {
            if(IsAnalysisRunning||archiveDialogOpen)return false;
            archiveDialogOpen=true;
            try
            {
                var saved=EnumerateSavedAnalyses().OrderByDescending(a=>a.CreatedUtc).ToArray();
                using(var form=CreateSavedAnalysisDialog(saved))
                    return form.ShowDialog(FindForm())==DialogResult.OK;
            }
            finally{archiveDialogOpen=false;}
        }
        Form CreateSavedAnalysisDialog(SavedAnalysisInfo[] saved)
        {
                    var form=ArchiveDialog(L("Load analysis","加载分析"),new Size(820,490));
                    form.MinimumSize=new Size(580,390);
                    var list=new ArchiveListView{Name="SavedAnalysisList",Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=true,HideSelection=false,ShowItemToolTips=true};
                    list.Columns.Add(L("Time","时间"),190);list.Columns.Add(L("Analysis name","分析名称"),560);
                    foreach(var item in saved){var row=new ListViewItem(HVX.ProjectModel.AnalysisDateText.FormatLocalTime(item.CreatedLocal));row.SubItems.Add(item.DisplayName);row.Tag=item;list.Items.Add(row);}
                    foreach(ListViewItem row in list.Items)row.ToolTipText=row.SubItems[1].Text;
                    var message=new Label{Dock=DockStyle.Top,Height=Math.Max(46,Font.Height*2+12),ForeColor=ReviewTheme.SecondaryInk,Text=saved.Length==0?L("No saved analysis in this project.","此项目尚无已保存的分析。"):L("Select an analysis to view or continue editing.","选择一次分析，加载后可查看或继续修改。")};
                    var detail=new TextBox{Name="SelectedAnalysisName",Dock=DockStyle.Bottom,Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Height=Font.Height*3+18,BorderStyle=BorderStyle.FixedSingle,BackColor=ReviewTheme.SurfaceAlt,Text=L("Select a row to see the full analysis name.","选择记录以查看完整分析名称。")};
                    var footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=Font.Height+24,Padding=new Padding(0,5,0,0),FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                    var cancel=ActionButton(90);cancel.Text=L("Close","关闭");cancel.DialogResult=DialogResult.Cancel;
                    var load=ActionButton(110);load.Text=L("Load analysis","加载分析");load.Enabled=false;ReviewTheme.ButtonRole(load,true);
                    var attachments=ActionButton(126);attachments.Text=L("Report attachments","报告附件");attachments.Click+=(s,e)=>ShowReportAttachmentSelection();
                    foreach(var button in new[]{cancel,load,attachments}){button.Width=Math.Max(button.Width,ReadableTextWidth(button,28));button.Height=Font.Height+10;}
                    footer.Controls.Add(cancel);footer.Controls.Add(load);footer.Controls.Add(attachments);
                    var remove=ActionButton(110);remove.Name="DeleteSavedAnalyses";remove.Text=L("Delete selected","删除所选");remove.Enabled=false;remove.Height=Font.Height+10;footer.Controls.Add(remove);
                    ReviewTheme.BindDialogFooter(form,footer);
                    list.SelectedIndexChanged+=(s,e)=>{remove.Enabled=list.SelectedItems.Count>0;};
                    remove.Click+=(s,e)=>{
                        var selected=list.SelectedItems.Cast<ListViewItem>().ToArray();if(selected.Length==0)return;
                        if(MessageBox.Show(form,L("Move selected analyses to recovery? Raw acquisition data will not be changed. Count: ","将所选分析移入恢复目录？原始采集数据不会改变。数量：")+selected.Length,L("Delete analysis","删除分析"),MessageBoxButtons.OKCancel,MessageBoxIcon.Warning)!=DialogResult.OK)return;
                        int removed=0;string failure="";
                        foreach(var row in selected){if(RemoveSavedAnalysis(((SavedAnalysisInfo)row.Tag).Id)){list.Items.Remove(row);removed++;}else failure=CustomerFailure("AnalysisRemoveRejected",AnalysisArchiveStatus,"Some analyses could not be removed. Check storage access and retry.","部分分析未能删除，请检查保存位置的访问权限后重试。");}
                        message.Text=L("Removed: ","已移入恢复目录：")+removed+" / "+selected.Length+(failure.Length==0?"":"；"+failure);
                        detail.Text=message.Text;
                    };
                    var viewport=new Panel{Name="SavedAnalysisViewport",Dock=DockStyle.Fill,AutoScroll=true};
                    var body=new Panel{Dock=DockStyle.Top};body.Controls.Add(list);body.Controls.Add(message);body.Controls.Add(detail);
                    viewport.Controls.Add(body);form.Controls.Add(viewport);form.Controls.Add(footer);form.CancelButton=cancel;form.AcceptButton=load;
                    bool arranging=false;
                    Action fitBody=()=>{
                        if(arranging)return;arranging=true;
                        try {
                            int width=Math.Max(100,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth);
                            message.Height=Math.Max(46,TextRenderer.MeasureText(message.Text,message.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+12);
                            detail.Height=Math.Max(66,detail.Font.Height*3+18);
                            body.Height=Math.Max(viewport.ClientSize.Height,message.Height+detail.Height+Math.Max(170,list.Font.Height*6));
                        }finally{arranging=false;}
                    };
                    viewport.SizeChanged+=(s,e)=>fitBody();foreach(Control child in body.Controls)child.FontChanged+=(s,e)=>fitBody();message.TextChanged+=(s,e)=>fitBody();form.Shown+=(s,e)=>fitBody();
                    ReviewTheme.ReserveDialogBody(form,footer,new Size(580,390));
                    Action fitColumns=()=>{int width=Math.Max(100,list.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4);int time=Math.Min(width/2,TextRenderer.MeasureText("2026-09-28 23:59:59",list.Font).Width+20);list.Columns[0].Width=time;list.Columns[1].Width=Math.Max(50,width-time);};
                    list.Resize+=(s,e)=>fitColumns();form.Shown+=(s,e)=>fitColumns();
                    list.SelectedIndexChanged+=(s,e)=>{load.Enabled=list.SelectedItems.Count==1;if(load.Enabled){var entry=(SavedAnalysisInfo)list.SelectedItems[0].Tag;detail.Text=entry.DisplayName+Environment.NewLine+HVX.ProjectModel.AnalysisDateText.FormatLocalTime(entry.CreatedLocal);}};
                    load.Click+=async(s,e)=>{
                        if(list.SelectedItems.Count!=1)return;
                        if(SourceRowCount>0&&MessageBox.Show(form,L("Loading replaces the current working view. Save changes first if needed. Continue?","加载将替换当前工作视图；如需保留修改，请先保存。是否继续？"),L("Load analysis","加载分析"),MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
                        var item=(SavedAnalysisInfo)list.SelectedItems[0].Tag;load.Enabled=false;form.UseWaitCursor=true;
                        try{
                            bool ok=await LoadSavedAnalysisAsync(item.Id).ConfigureAwait(true);
                            if(ok){SetReviewOperationStatus(L("Analysis loaded: ","已加载分析：")+item.DisplayName);form.DialogResult=DialogResult.OK;form.Close();}
                            else{message.Text=ArchiveCustomerFailure(true);message.ForeColor=Color.Firebrick;}
                        }
                        catch(Exception ex){if(IsFatalHostException(ex))throw;message.Text=ArchiveCustomerFailure(true);message.ForeColor=Color.Firebrick;}
                        finally{if(!form.IsDisposed){load.Enabled=true;form.UseWaitCursor=false;}}
                    };
                    list.DoubleClick+=(s,e)=>{if(load.Enabled)load.PerformClick();};
                    return form;
        }
        sealed class ArchiveListView:ListView
        {
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if((m.Msg==0x5||m.Msg==0xF)&&IsHandleCreated)ShowScrollBar(Handle,0,false);
            }
            [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool ShowScrollBar(IntPtr hwnd,int bar,bool show);
        }
        Form ArchiveDialog(string title,Size client)
        {
            var form=new Form{Text=title,ClientSize=client,Padding=new Padding(12),Font=Font,BackColor=ReviewTheme.Surface,AutoScaleMode=AutoScaleMode.Font,
                FormBorderStyle=FormBorderStyle.Sizable,MaximizeBox=false,MinimizeBox=false,MinimumSize=new Size(340,220),StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false};
            ReviewTheme.ApplyDialogIcon(form);
            form.Shown+=(s,e)=>ReviewTheme.FitDialogToWorkingArea(form);
            form.DpiChanged+=(s,e)=>{if(!form.IsDisposed)form.BeginInvoke((MethodInvoker)(()=>ReviewTheme.FitDialogToWorkingArea(form)));};
            return form;
        }
        public void ShowReportAttachmentSelection()
        {
            using(var form=CreateReportAttachmentDialog())form.ShowDialog(FindForm());
        }
        Form CreateReportAttachmentDialog()
        {
            var form=ArchiveDialog(L("Report attachments","报告附件"),new Size(560,320));
            {
                var list=new CheckedListBox{Name="ReportAttachmentSelection",Dock=DockStyle.Fill,CheckOnClick=true,BorderStyle=BorderStyle.None,HorizontalScrollbar=true};
                var charts=GetPendingReportCharts();
                for(int i=0;i<charts.Length;i++)list.Items.Add(charts[i].Title,IsReportChartSelected(charts[i].Sequence));
                var message=new Label{Dock=DockStyle.Top,Height=40,Text=L("Checked charts are available to the report generator. Original data is retained when unchecked.","勾选的图表供报告生成程序采用；取消勾选不会删除原始数据。"),ForeColor=ReviewTheme.SecondaryInk};
                var close=ActionButton(110);close.Dock=DockStyle.Bottom;close.Text=L("Close","关闭");close.DialogResult=DialogResult.OK;
                var viewport=new Panel{Name="ReviewAttachmentViewport",Dock=DockStyle.Fill,AutoScroll=true};
                var body=new Panel{Dock=DockStyle.Top};body.Controls.Add(list);body.Controls.Add(message);viewport.Controls.Add(body);
                bool arranging=false;
                Action fit=()=>{
                    if(arranging)return;arranging=true;
                    try {int width=Math.Max(80,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth);message.Height=TextRenderer.MeasureText(message.Text,message.Font,new Size(width,int.MaxValue),TextFormatFlags.WordBreak).Height+8;close.Height=Math.Max(36,close.Font.Height+14);body.Height=Math.Max(viewport.ClientSize.Height,message.Height+Math.Max(170,list.Font.Height*6));}
                    finally{arranging=false;}
                };
                viewport.Resize+=(s,e)=>fit();message.FontChanged+=(s,e)=>fit();message.TextChanged+=(s,e)=>fit();list.FontChanged+=(s,e)=>fit();close.FontChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();
                list.ItemCheck+=(s,e)=>{if(!SetReportChartSelected(charts[e.Index].Sequence,e.NewValue==CheckState.Checked)){e.NewValue=e.CurrentValue;message.Text=charts[e.Index].AcquisitionReferenceOnly?AcquisitionRiskText():CustomerFailure("ReportChartSelectionFailed",AnalysisArchiveStatus,"The chart selection was not changed. Check the analysis and retry.","图表选择未更改。请检查当前分析后重试。");}};
                form.Controls.Add(viewport);form.Controls.Add(close);form.CancelButton=close;
                ReviewTheme.ReserveDialogBody(form,close,new Size(340,300));return form;
            }
        }
    }
}
