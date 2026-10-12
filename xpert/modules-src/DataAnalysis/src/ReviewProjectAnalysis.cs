using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        sealed class CompletedFile { internal ReviewAnalysisRequest Request;internal ReviewAnalysisResult Result; }
        double[,] analysisSourceSnapshot;
        int analysisSourceRevision;
        string analysisProjectSnapshot;
        Dictionary<string,double[]> analysisCatalogSnapshot;
        bool analysisDiagnosticsHooked,analysisDiagnosticsDialogBusy;
        ToolTip analysisDiagnosticsTip;
        ReviewAnalysisProgressDialog activeAnalysisDialog;
        ReviewAnalysisDebugTrace.Scope activeAnalysisTrace;
        bool manualReviewAutoAdvance;
        string analysisOutcomeWarning="";
        [Browsable(false)] public bool IsAnalysisCancellationRequested {get{return analysisBusy&&analysisCancellation!=null&&analysisCancellation.IsCancellationRequested;}}
        // A user cancellation stops between LOC files, never discards a file
        // that the engine has already started. ClearAllAnalysis is a separate,
        // destructive reset that invalidates the generation instead.
        public bool CancelSelectedAnalysis()
        {
            if(IsDisposed||Disposing)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(CancelSelectedAnalysis));
            if(!analysisBusy||analysisCancellation==null)return false;
            analysisCancellation.Cancel();
            AnalysisStatus=L("Stopping after the current LOC finishes…","等待当前 LOC 分析完成后停止…");
            if(activeAnalysisDialog!=null&&!activeAnalysisDialog.IsDisposed)activeAnalysisDialog.SetStopping();
            RefreshReviewUi();return true;
        }
        void OpenAnalysisProgress(int total)
        {
            CloseAnalysisProgress();
            activeAnalysisDialog=new ReviewAnalysisProgressDialog(languageIndex,Font);
            activeAnalysisDialog.CancelRequested+=(s,e)=>CancelSelectedAnalysis();
            activeAnalysisDialog.UpdateProgress("",0,total);
            var owner=FindForm();
            activeAnalysisDialog.PrepareForDisplay(owner);
            if(owner!=null&&owner.Visible)activeAnalysisDialog.Show(owner);else activeAnalysisDialog.Show();
        }
        void CloseAnalysisProgress()
        {
            var dialog=activeAnalysisDialog;activeAnalysisDialog=null;
            if(dialog==null||dialog.IsDisposed)return;
            dialog.FinishAndClose();dialog.Dispose();
        }
        [Browsable(false)] public string AnalysisDiagnosticText {get;private set;}="";
        [Browsable(false)] public string ExpectedAnalysisAlgorithmPath {get{return ReviewAnalysisEngine.ExpectedAlgorithmPath;}}
        [Browsable(false)] public string AnalysisControlAssemblyPath {get{return typeof(PdReviewControl).Assembly.Location;}}
        [Browsable(false)] public string AnalysisControlSourcePath {get{return ReviewAnalysisEngine.ControlSourcePath;}}
        void SetupAnalysisDiagnostics()
        {
            if(analysisDiagnosticsHooked||analysisProgress==null||statusLabel==null)return;
            analysisDiagnosticsHooked=true;
            analysisDiagnosticsTip=new ToolTip{ShowAlways=true,AutoPopDelay=20000};
            var menu=new ContextMenuStrip();
            var show=menu.Items.Add("");show.Click+=(s,e)=>ShowAnalysisDiagnostics();
            var copy=menu.Items.Add("");copy.Click+=(s,e)=>CopyAnalysisDiagnosticText();
            menu.Opening+=(s,e)=>{show.Text=L("View complete diagnostics","查看完整诊断");copy.Text=L("Copy complete diagnostics","复制完整诊断");};
            foreach(var label in new[]{analysisProgress,statusLabel})
            {
                label.Cursor=Cursors.Hand;label.ContextMenuStrip=menu;
                label.DoubleClick+=(s,e)=>ShowAnalysisDiagnostics();
            }
            Disposed+=(s,e)=>{analysisDiagnosticsTip.Dispose();menu.Dispose();};
            UpdateAnalysisDiagnosticHints();
        }
        void UpdateAnalysisDiagnosticHints()
        {
            if(analysisDiagnosticsTip==null)return;
            string hint=L("Double-click or right-click for complete diagnostics.","双击或右键查看完整诊断。");
            foreach(var label in new[]{analysisProgress,statusLabel})
            {analysisDiagnosticsTip.SetToolTip(label,hint);label.AccessibleDescription=hint;}
        }
        string AnalysisDiagnosticContents()
        {
            string debug=ReviewAnalysisDebugTrace.Enabled?Environment.NewLine+"Analysis timing log: "+ReviewAnalysisDebugTrace.LogDirectory:"";
            if(!String.IsNullOrEmpty(AnalysisDiagnosticText))return "Control file version: "+ControlBuildVersion+debug+Environment.NewLine+AnalysisDiagnosticText;
            return "Control file version: "+ControlBuildVersion+debug+Environment.NewLine+ReviewAnalysisEngine.DiagnosticHeader(projectPath)+
                Environment.NewLine+(String.IsNullOrEmpty(LastError)?"No analysis error recorded.":LastError);
        }
        bool CopyAnalysisDiagnosticText(string text=null)
        {
            try
            {
                if(text==null){if(!AuthorizeDiagnostics())return false;text=AnalysisDiagnosticContents()+Environment.NewLine+CurrentDiagnosticContext();}if(String.IsNullOrEmpty(text))return false;
                Clipboard.SetText(text,TextDataFormat.UnicodeText);return true;
            }
            catch(Exception error){if(IsFatalHostException(error))throw;LastError=ReviewAnalysisEngine.DescribeException(error);RefreshReviewUi();return false;}
        }
        // Explicit user/host invocation only: no automatic popup on errors,
        // paints, property writes or progress updates.
        public bool ShowAnalysisDiagnostics()
        {
            if(IsDisposed||Disposing||analysisDiagnosticsDialogBusy)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<bool>(ShowAnalysisDiagnostics));
            analysisDiagnosticsDialogBusy=true;
            try
            {
                if(!AuthorizeDiagnostics()||IsDisposed||Disposing)return false;
                using(var dialog=new Form{Name="AnalysisDiagnosticsDialog",Font=Font,Text=L("Analysis diagnostics","分析诊断"),StartPosition=FormStartPosition.CenterParent,
                    ShowInTaskbar=false,MinimizeBox=false,ClientSize=new Size(900,540),MinimumSize=new Size(640,360),
                    BackColor=Color.FromArgb(245,248,250),AutoScaleMode=AutoScaleMode.Font,Padding=new Padding(12)})
                using(var body=new TextBox{Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,
                    Dock=DockStyle.Fill,Text=AnalysisDiagnosticContents()+Environment.NewLine+CurrentDiagnosticContext(),BackColor=Color.White,ForeColor=ReviewTheme.Ink})
                {
                    ReviewTheme.ApplyDialogIcon(dialog);
                    var tabs=new TabControl{Dock=DockStyle.Fill};
                    var information=new TabPage(L("Current information","当前信息"));information.Controls.Add(body);tabs.TabPages.Add(information);
                    var history=new TabPage(L("Operation log","操作日志"));tabs.TabPages.Add(history);
                    var log=new TextBox{Name="OperationLogText",Multiline=true,ReadOnly=true,WordWrap=false,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,BackColor=Color.White};
                    var paging=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=40};
                    var prev=new HVX.Presentation.R2RoundedButton{Text=L("Previous page","上一页"),Width=100};var next=new HVX.Presentation.R2RoundedButton{Text=L("Next page","下一页"),Width=100};var refresh=new HVX.Presentation.R2RoundedButton{Text=L("Latest","最新"),Width=100};
                    var pageLabel=new Label{AutoSize=true,Padding=new Padding(0,8,0,0)};int page=0;const int pageSize=200;
                    Action showPage=()=>{int pages=Math.Max(1,(operationJournal.Count+pageSize-1)/pageSize);page=Math.Max(0,Math.Min(page,pages-1));log.Text=operationJournal.ReadPage(page,pageSize);log.SelectionLength=0;pageLabel.Text=(page+1)+" / "+pages+"  ("+operationJournal.Count+")";prev.Enabled=page>0;next.Enabled=page<pages-1;};
                    prev.Click+=(s,e)=>{page--;showPage();};next.Click+=(s,e)=>{page++;showPage();};refresh.Click+=(s,e)=>{page=operationJournal.Count/pageSize;showPage();};
                    paging.Controls.AddRange(new Control[]{prev,next,refresh,pageLabel});history.Controls.Add(log);history.Controls.Add(paging);showPage();
                    ReviewTheme.BindDialogFooter(dialog,paging);
                    var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,FlowDirection=FlowDirection.RightToLeft,WrapContents=false};
                    var close=new RoundedReviewButton{Text=L("Close","关闭"),Width=130,Height=36,FlatStyle=FlatStyle.Flat,
                        BackColor=ReviewTheme.Accent,ForeColor=Color.White,DialogResult=DialogResult.Cancel};
                    var copy=new RoundedReviewButton{Text=L("Copy diagnostics","复制完整诊断"),Width=170,Height=36,FlatStyle=FlatStyle.Flat,
                        BackColor=ReviewTheme.Accent,ForeColor=Color.White};
                    close.FlatAppearance.BorderColor=copy.FlatAppearance.BorderColor=ReviewTheme.Accent;
                    copy.Click+=(s,e)=>{if(CopyAnalysisDiagnosticText(body.Text))copy.Text=L("Copied","已复制");};
                    var export=new RoundedReviewButton{Text=L("Export full log","导出完整日志"),Width=160,Height=36};
                    export.Click+=(s,e)=>{using(var picker=new SaveFileDialog{Filter="JSON Lines (*.jsonl)|*.jsonl",FileName="operation-log.jsonl"})if(picker.ShowDialog(dialog)==DialogResult.OK){try{RecordOperation("LogExport",picker.FileName);operationJournal.Flush();File.Copy(operationJournal.FilePath,picker.FileName,true);showPage();}catch(Exception ex){if(IsFatalHostException(ex))throw;MessageBox.Show(dialog,CustomerFailure("LogExportFailed",ex.ToString(),"The log was not exported. Choose a writable folder and retry.","日志未导出。请选择可写入的文件夹后重试。"),L("Export failed","导出失败"));}}};
                    buttons.Controls.Add(close);buttons.Controls.Add(copy);buttons.Controls.Add(export);dialog.Controls.Add(tabs);dialog.Controls.Add(buttons);dialog.CancelButton=close;
                    ReviewTheme.BindDialogFooter(dialog,buttons);
                    // Both the outer actions and the operation-log paging row
                    // can wrap. Reserve readable log space beneath the tabs.
                    bool arranging=false;
                    Action fitMinimum=()=>{
                        if(arranging||dialog.IsDisposed||!dialog.IsHandleCreated)return;arranging=true;
                        try {
                            Rectangle area=Screen.FromControl(dialog.Owner??dialog).WorkingArea;
                            int chrome=Math.Max(dialog.Font.Height+18,tabs.Height-tabs.DisplayRectangle.Height);
                            int height=dialog.Height-dialog.ClientSize.Height+dialog.Padding.Vertical+buttons.Height+chrome+paging.Height+Math.Max(160,dialog.Font.Height*5);
                            dialog.MinimumSize=new Size(Math.Min(640,Math.Max(1,area.Width-24)),Math.Min(Math.Max(360,height),Math.Max(1,area.Height-24)));
                            ReviewTheme.FitDialogToWorkingArea(dialog);
                        }finally{arranging=false;}
                    };
                    paging.SizeChanged+=(s,e)=>fitMinimum();buttons.SizeChanged+=(s,e)=>fitMinimum();dialog.FontChanged+=(s,e)=>fitMinimum();dialog.Shown+=(s,e)=>fitMinimum();dialog.DpiChanged+=(s,e)=>fitMinimum();
                    EventHandler closeOnDispose=(s,e)=>dialog.Dispose();Disposed+=closeOnDispose;
                    try{dialog.ShowDialog(FindForm());return true;}finally{Disposed-=closeOnDispose;}
                }
            }
            catch(Exception error){if(IsFatalHostException(error))throw;LastError=ReviewAnalysisEngine.DescribeException(error);if(!IsDisposed)RefreshReviewUi();return false;}
            finally{analysisDiagnosticsDialogBusy=false;}
        }
        public bool CheckAnalysisEnvironment()
        {
            if(analysisBusy)return false;
            SetupAnalysisDiagnostics();UpdateAnalysisDiagnosticHints();
            string detail;bool ok=ReviewAnalysisEngine.CheckEnvironment(projectPath,AnalysisAlgorithmIndex,out detail);
            AnalysisDiagnosticText=ReviewAnalysisEngine.DiagnosticHeader(projectPath)+Environment.NewLine+detail;
            if(!ok){AnalysisCompletedFileCount=0;LastError=detail;AnalysisStatus=L("Analysis not started: environment check failed. Read AnalysisDiagnosticText for details.","未开始分析：环境检查失败，请读取 AnalysisDiagnosticText 查看完整诊断。");}
            else{LastError="";AnalysisStatus=L("Analysis environment check passed.","分析环境检查通过。");}
            RefreshReviewUi();return ok;
        }
        public bool StartSelectedAnalysis(bool automaticAcceptance)
        {
            if(analysisBusy)return false;
            if(AiLearningMode)
            {
                LastError=L("AI learning mode records manual review only; automatic and batch analysis are paused.","AI 学习模式仅记录人工复核，自动与批量分析均已暂停。");
                AnalysisStatus=LastError;RefreshReviewUi();return false;
            }
            string[] chosen=analysisTree.GetSelectedDataPaths();if(chosen.Length==0){LastError=L("Select pending LOC files in the tree first.","请先在树形勾选待分析的 LOC 文件。");RefreshReviewUi();return false;}
            string[] paths=analysisTree.GetDataPaths();var catalog=analysisTree.GetDataCatalog();
            var requests=new List<ReviewAnalysisRequest>();
            foreach(string path in chosen){int i=Array.FindIndex(paths,p=>String.Equals(p,path,StringComparison.OrdinalIgnoreCase));if(i<0||!CanAnalyzeRecord(path,catalog[i,2]))continue;
                requests.Add(CreateFileAnalysisRequest(path,(long)catalog[i,1],automaticAcceptance,(int)catalog[i,0]));
            }
            if(requests.Count==0)return false;
            // Common deployment errors stop once, before the per-file loop.
            if(!CheckAnalysisEnvironment())return false;
            PrepareAHistoryBatch(requests);
            pdmap.ClearConnectingCableUiFeedback();
            pdmap.CompleteReviewPreview();pdmap.FlushReviewPreview();
            AnalysisDiagnosticText=ReviewAnalysisEngine.DiagnosticHeader(projectPath)+Environment.NewLine+"Selected LOC files: "+requests.Count;
            // Force handle creation on the owning UI thread, never in the worker.
            var handle=Handle;int generation=++analysisGeneration;
            if(activeAnalysisTrace!=null)activeAnalysisTrace.End("superseded");
            activeAnalysisTrace=ReviewAnalysisDebugTrace.Begin("batch","generation="+generation+"; files="+requests.Count+"; automaticAcceptance="+automaticAcceptance+"; Project="+projectPath);
            var batchTrace=activeAnalysisTrace;
            analysisSourceSnapshot=working;analysisSourceRevision=ResultChangeSequence;
            CaptureAnalysisCatalogSnapshot();
            var cancellation=new CancellationTokenSource();if(analysisCancellation!=null)analysisCancellation.Dispose();analysisCancellation=cancellation;
            foreach(var request in requests){request.Cancellation=cancellation.Token;if(request.AHistoryBatch!=null)request.AHistoryBatch.Cancellation=cancellation.Token;}
            analysisOutcomeWarning="";
            CancelLocationOverviewLoad();
            analysisBusy=true;AnalysisCompletedFileCount=0;AnalysisStatus=L("Analyzing selected files…","正在分析所选文件…");LastError="";RefreshReviewUi();
            try{OpenAnalysisProgress(requests.Count);}
            catch(Exception ex){if(IsFatalHostException(ex))throw;batchTrace.Failure(ex);batchTrace.End("progress-dialog-failed");analysisBusy=false;CloseAnalysisProgress();LastError=ReviewAnalysisEngine.DescribeException(ex);RefreshReviewUi();return false;}
            Task.Run(()=>{
                var completed=new List<CompletedFile>();
                try{
                    foreach(var request in requests){if(cancellation.IsCancellationRequested)break;
                        string currentPath=request.LocationFilePath;int before=completed.Count;
                        PostAnalysis(generation,()=>{if(activeAnalysisDialog!=null&&!activeAnalysisDialog.IsDisposed)activeAnalysisDialog.UpdateProgress(currentPath,before,requests.Count);});
                        var result=ReviewAnalysisEngine.Analyze(request);
                        // The engine keeps its failure-return contract. A user
                        // cancellation is not a failed LOC and must not publish
                        // a failure row or failure warning in the stopped batch.
                        if(result.Canceled)break;
                        completed.Add(new CompletedFile{Request=request,Result=result});
                        batchTrace.Point("file-complete","processed="+completed.Count+"; success="+result.Success+"; rows="+result.Parameters.GetLength(0)+"; file="+currentPath);
                        int done=completed.Count;PostAnalysis(generation,()=>{AnalysisCompletedFileCount=done;
                            if(!cancellation.IsCancellationRequested)AnalysisStatus=L("Processed files: ","已处理文件：")+done+" / "+requests.Count+
                                (done==requests.Count?L("; organizing results…","；正在整理结果…"):"");
                            if(activeAnalysisDialog!=null&&!activeAnalysisDialog.IsDisposed)activeAnalysisDialog.UpdateProgress(currentPath,done,requests.Count);
                            // Only progress changed. Keep the disabled workspace
                            // stable instead of repainting/reflowing every action
                            // and plot for each completed LOC file.
                            analysisProgress.Text=AnalysisStatus;RefreshBottomStatus();});
                    }
                    PostAnalysis(generation,()=>CompleteProjectAnalysis(generation,completed,automaticAcceptance,cancellation.IsCancellationRequested,requests.Count));
                }catch(Exception ex){if(IsFatalHostException(ex))throw;
                    batchTrace.Failure(ex);
                    string failure=ReviewAnalysisEngine.DescribeException(ex);
                    PostAnalysis(generation,()=>CompleteProjectAnalysis(generation,completed,automaticAcceptance,cancellation.IsCancellationRequested,requests.Count,failure));}
            });return true;
        }
        void PostAnalysis(int generation,Action action)
        {
            if(IsDisposed||!IsHandleCreated){ReviewAnalysisDebugTrace.Event("ui-post-dropped","generation="+generation+"; disposed-or-no-handle");return;}
            long posted=System.Diagnostics.Stopwatch.GetTimestamp();
            try{BeginInvoke(new Action(()=>{
                if(!IsDisposed&&generation==analysisGeneration){
                    if(activeAnalysisTrace!=null)activeAnalysisTrace.Point("ui-post-delivered","queueMs="+(1000d*(System.Diagnostics.Stopwatch.GetTimestamp()-posted)/System.Diagnostics.Stopwatch.Frequency).ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                    action();
                }else ReviewAnalysisDebugTrace.Event("ui-post-dropped","generation="+generation+"; current="+analysisGeneration);
            }));}
            catch(InvalidOperationException error){ReviewAnalysisDebugTrace.Event("ui-post-failed",error.ToString());/* Host disposed its .NET container while a file completed. */}
        }
        bool IsCurrentAnalysis(int generation){return !IsDisposed&&!Disposing&&generation==analysisGeneration;}
        void CompleteProjectAnalysis(int generation,List<CompletedFile> results,bool automaticAcceptance,bool canceled=false,int requestedCount=0,string batchFailure="")
        {
            var completingDialog=activeAnalysisDialog;
            var trace=activeAnalysisTrace;
            try{
                if(!IsCurrentAnalysis(generation))return;
                if(trace!=null)trace.Point("merge-start","processed="+results.Count+"; requested="+requestedCount+"; canceled="+canceled);
                if(!ReferenceEquals(analysisSourceSnapshot,working)||analysisSourceRevision!=ResultChangeSequence)
                    throw new InvalidOperationException("Review data changed during analysis. Results were not merged; select the files and analyze again.");
                var good=results.Where(x=>x.Result.Success).ToArray();var failed=results.Where(x=>!x.Result.Success).ToArray();
                var diagnostics=new StringBuilder(ReviewAnalysisEngine.DiagnosticHeader(projectPath));
                diagnostics.AppendLine().AppendLine("Completed: "+good.Length+" / "+results.Count+"; failed: "+failed.Length);
                if(canceled)diagnostics.AppendLine("Cancellation requested: stopped between LOC files; completed results retained.");
                if(!String.IsNullOrEmpty(batchFailure))diagnostics.AppendLine(batchFailure);
                foreach(var file in failed)diagnostics.AppendLine("LOC: "+file.Request.LocationFilePath).AppendLine(file.Result.Status);
                AnalysisDiagnosticText=diagnostics.ToString().TrimEnd();
                int priorRows=SourceRowCount;
                if(good.Length==0){
                    FinishPublishedProjectAnalysis(generation,completingDialog,trace,good,failed,results,automaticAcceptance,canceled,requestedCount,batchFailure,priorRows,null);
                    return;
                }
                // Validate on the UI thread, then merge diagnostics + waveforms off
                // the UI so 117–462 LOC location batches do not freeze the shell.
                ValidateAnalysisCommitTargets(good);
                if(completingDialog!=null&&!completingDialog.IsDisposed)completingDialog.BeginOrganizingResults(good.Length);
                AnalysisStatus=L("Processed files: ","已处理文件：")+results.Count+" / "+Math.Max(requestedCount,results.Count)+L("; organizing results…","；正在整理结果…");
                analysisProgress.Text=AnalysisStatus;RefreshBottomStatus();
                var workingSnap=working;var matchingSnap=matching;var originalSnap=original;
                var priorDiagnostics=new Dictionary<string,string>(algorithmDiagnostics,StringComparer.OrdinalIgnoreCase);
                string project=projectPath;
                var resultArray=good.Select(x=>x.Result).ToArray();
                Task.Run(()=>{
                    try{
                        var mergedDiagnostics=MergeCompletedAlgorithmDiagnostics(priorDiagnostics,project,good);
                        string diagnosticsWarning=null;
                        Dictionary<string,EncodedCandidateStatus> validated=null;
                        List<KeyValuePair<string,byte[]>> parts=null;
                        // Heavy JSON packing stays off the UI thread. Location
                        // matrices still merge even when diagnostics hit a ceiling.
                        try{SerializeAlgorithmDiagnosticsCore(mergedDiagnostics,null,false,out validated,out parts);}
                        catch(Exception diagEx){
                            if(IsFatalHostException(diagEx))throw;
                            diagnosticsWarning=diagEx.Message;validated=null;parts=null;
                        }
                        double[,] p,w;MergeAnalysisResultsCore(workingSnap,matchingSnap,originalSnap,resultArray,out p,out w);
                        if(trace!=null)trace.Point("matrices-merged","rows="+p.GetLength(0)+"; waveformColumns="+w.GetLength(1));
                        var adoptedValidated=validated;var adoptedParts=parts;var warning=diagnosticsWarning;
                        PostAnalysis(generation,()=>PublishMergedProjectAnalysis(generation,completingDialog,trace,good,failed,results,automaticAcceptance,canceled,requestedCount,batchFailure,priorRows,p,w,mergedDiagnostics,adoptedValidated,adoptedParts,warning));
                    }catch(Exception ex){
                        if(IsFatalHostException(ex))throw;
                        PostAnalysis(generation,()=>FailProjectAnalysisMerge(generation,completingDialog,trace,results,ex));
                    }
                });
            }catch(Exception ex){
                FailProjectAnalysisMerge(generation,completingDialog,trace,results,ex);
            }
        }
        void PublishMergedProjectAnalysis(int generation,ReviewAnalysisProgressDialog completingDialog,ReviewAnalysisDebugTrace.Scope trace,
            CompletedFile[] good,CompletedFile[] failed,List<CompletedFile> results,bool automaticAcceptance,bool canceled,int requestedCount,string batchFailure,
            int priorRows,double[,] p,double[,] w,Dictionary<string,string> completedDiagnostics,
            Dictionary<string,EncodedCandidateStatus> validated,List<KeyValuePair<string,byte[]>> parts,string diagnosticsWarning)
        {
            try{
                if(!IsCurrentAnalysis(generation))return;
                if(!ReferenceEquals(analysisSourceSnapshot,working)||analysisSourceRevision!=ResultChangeSequence)
                    throw new InvalidOperationException("Review data changed during analysis. Results were not merged; select the files and analyze again.");
                ValidateAnalysisCommitTargets(good);
                if(validated!=null)AdoptSerializedAlgorithmDiagnostics(validated,parts);
                else{
                    string ignored;
                    TrySerializeAlgorithmDiagnosticsForCommit(completedDiagnostics,out ignored);
                    if(diagnosticsWarning==null)diagnosticsWarning=ignored;
                }
                bool priorCompensation=inputGeometryAlreadyCompensated;inputGeometryAlreadyCompensated=true;
                var retained=Enumerable.Range(0,p.GetLength(0)).Select(r=>r<priorRows?r:-1).ToArray();
                if(!ReloadWithRetainedReviewCore(p,w,retained,automaticAcceptance,WithCompletedAcquisitionQualities(good))){inputGeometryAlreadyCompensated=priorCompensation;throw new InvalidOperationException(LastError);}
                if(trace!=null)trace.Point("ui-results-published","rows="+SourceRowCount);
                if(!IsCurrentAnalysis(generation))return;
                ValidateAnalysisCommitTargets(good,true);
                algorithmDiagnostics=completedDiagnostics;
                CommitAHistoryReceipts(good.Select(x=>x.Result));
                treeSync=true;try{
                    if(!analysisTree.MarkAnalyzed(good.Select(x=>x.Request.LocationFilePath).ToArray()))
                        throw new InvalidOperationException(L("The record list could not be updated. ","记录列表未能更新。 ")+analysisTree.LastError);
                }finally{treeSync=false;}
                foreach(var file in good){ThresholdChoice choice;
                    if(!fileThresholds.TryGetValue(file.Request.LocationFilePath,out choice)){choice=new ThresholdChoice();fileThresholds[file.Request.LocationFilePath]=choice;}
                    choice.Actual=file.Request.ManualThresholdEnabled?double.NaN:file.Result.AppliedThreshold;choice.ActualMultiplier=file.Request.AutomaticThresholdMultiplier;
                    if(String.Equals(overviewPath,file.Request.LocationFilePath,StringComparison.OrdinalIgnoreCase)&&Finite(file.Result.AppliedThreshold))locationOverview.SetThreshold(file.Result.AppliedThreshold,false);
                }
                ResultChangeSequence++;NotifyReviewHost(ResultsChanged);
                if(!IsCurrentAnalysis(generation))return;
                ValidateAnalysisCommitTargets(good,true);
                FinishPublishedProjectAnalysis(generation,completingDialog,trace,good,failed,results,automaticAcceptance,canceled,requestedCount,batchFailure,priorRows,diagnosticsWarning);
            }catch(Exception ex){FailProjectAnalysisMerge(generation,completingDialog,trace,results,ex);}
        }
        void FinishPublishedProjectAnalysis(int generation,ReviewAnalysisProgressDialog completingDialog,ReviewAnalysisDebugTrace.Scope trace,
            CompletedFile[] good,CompletedFile[] failed,List<CompletedFile> results,bool automaticAcceptance,bool canceled,int requestedCount,string batchFailure,
            int priorRows,string diagnosticsWarning)
        {
            try{
                if(!IsCurrentAnalysis(generation))return;
                AnalysisStatus=L("Analysis complete: ","分析完成：")+good.Length+" / "+results.Count;
                string firstArrivalWarning=results.Any(x=>x.Result.FirstArrivalUnresolved)?
                    L("Some first arrivals could not be established. No peak timing was substituted; review the original waveforms.",
                      "部分首达点无法可靠确定，未改用峰值定位，请复核原始波形。"):"";
                if(canceled)AnalysisStatus=L("Stopped: completed ","已停止：完成 ")+good.Length+" / "+Math.Max(requestedCount,results.Count)+L("; not started ","；未开始 ")+Math.Max(0,requestedCount-results.Count);
                if(failed.Length>0){LastError=Path.GetFileName(failed[0].Request.LocationFilePath)+": "+failed[0].Result.Status;AnalysisStatus+="; "+L("failed ","失败 ")+failed.Length;}
                if(!String.IsNullOrEmpty(batchFailure)){LastError=batchFailure;AnalysisStatus+="; "+L("Batch interrupted","批次中断");}
                if(!String.IsNullOrEmpty(diagnosticsWarning))
                    AnalysisStatus+="; "+L("location results kept; diagnostics archive spill deferred: ","定位结果已保留；诊断分段延后：")+diagnosticsWarning;
                analysisBusy=false;
                AnalysisCompletedFileCount=results.Count;
                if(firstArrivalWarning.Length>0){AnalysisStatus+="; "+firstArrivalWarning;LastError=firstArrivalWarning;}
                if(failed.Length>0||!String.IsNullOrEmpty(batchFailure))analysisOutcomeWarning=AnalysisStatus+L(". Some records could not be analysed; review their status and retry.","。部分记录未能分析，请检查记录状态后重试。");
                if(IsCurrentAnalysis(generation)&&ReferenceEquals(activeAnalysisDialog,completingDialog))CloseAnalysisProgress();
                manualReviewAutoAdvance=!automaticAcceptance&&good.Length>0;
                if(!automaticAcceptance&&SourceRowCount>priorRows)SelectSourceRowCore(priorRows,false);
                if(!IsCurrentAnalysis(generation))return;
                SyncAnalysisTree(true);if(!IsCurrentAnalysis(generation))return;
                if(good.Length>0)ValidateAnalysisCommitTargets(good,true);
                var analyzedPaths=new HashSet<string>(analysisTree.GetAnalyzedDataPaths(),StringComparer.OrdinalIgnoreCase);
                if(good.Any(x=>!analyzedPaths.Contains(x.Request.LocationFilePath)))
                    throw new InvalidOperationException(L("The record list changed before results could be confirmed.","结果确认前记录列表已发生变化。"));
                RefreshReviewUi();if(trace!=null)trace.Point("ui-final-refresh","good="+good.Length+"; failed="+failed.Length+"; sourceRows="+SourceRowCount);
                NotifyReviewHost(AnalysisCompleted);
                if(trace!=null)trace.End("completed; good="+good.Length+"; failed="+failed.Length+"; sourceRows="+SourceRowCount);
            }catch(Exception ex){FailProjectAnalysisMerge(generation,completingDialog,trace,results,ex);}
            finally{if(trace!=null)trace.Dispose();}
        }
        void FailProjectAnalysisMerge(int generation,ReviewAnalysisProgressDialog completingDialog,ReviewAnalysisDebugTrace.Scope trace,List<CompletedFile> results,Exception ex)
        {
            if(IsFatalHostException(ex))throw ex;
            if(!IsCurrentAnalysis(generation)){if(trace!=null)trace.Dispose();return;}
            if(trace!=null){trace.Failure(ex);trace.End("merge-or-publication-failed");trace.Dispose();}
            analysisBusy=false;LastError=ReviewAnalysisEngine.DescribeException(ex);AnalysisDiagnosticText+=Environment.NewLine+LastError;
            AnalysisCompletedFileCount=results.Count;
            analysisOutcomeWarning=L("Analysis could not finish. Check the record status and retry with fewer records. Contact support if this continues.",
                "分析未能正常完成。请检查记录状态，减少本次选择的记录后重试；若仍失败，请联系维护人员。");
            AnalysisStatus=analysisOutcomeWarning;SetReviewOperationStatus(analysisOutcomeWarning);
            RecordOperation("AnalysisFailed",LastError);RefreshReviewUi();
            if(IsCurrentAnalysis(generation)&&ReferenceEquals(activeAnalysisDialog,completingDialog))CloseAnalysisProgress();
        }
        Dictionary<string,double[]> ReadAnalysisCatalog()
        {
            string[] paths=analysisTree.GetDataPaths();double[,] catalog=analysisTree.GetDataCatalog();
            if(catalog.GetLength(0)!=paths.Length||catalog.GetLength(1)<3)throw new InvalidOperationException("Analysis catalog dimensions changed.");
            var result=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
            for(int i=0;i<paths.Length;i++)result.Add(Path.GetFullPath(paths[i]),new[]{catalog[i,0],catalog[i,1],catalog[i,2]});
            return result;
        }
        void CaptureAnalysisCatalogSnapshot()
        {analysisProjectSnapshot=SessionRoot(projectPath);analysisCatalogSnapshot=ReadAnalysisCatalog();}
        void ValidateAnalysisCommitTargets(CompletedFile[] completed,bool afterPublication=false)
        {
            string root=SessionRoot(projectPath);var current=ReadAnalysisCatalog();
            // A synchronous host callback may already have observed/changed a
            // published snapshot. Do not claim that those changes were rolled back.
            string changed=afterPublication?
                L("The project record list changed while organizing results. This batch could not be confirmed; reload the project and retry.",
                  "整理结果期间项目记录列表发生变化，未能确认本批结果。请重新加载项目后重试。"):
                L("The project record list changed during analysis. Results were not added; reload the project and retry.",
                  "分析期间项目记录列表发生变化，未加入本批结果。请重新加载项目后重试。");
            if(analysisCatalogSnapshot==null||!String.Equals(root,analysisProjectSnapshot,StringComparison.OrdinalIgnoreCase)||current.Count!=analysisCatalogSnapshot.Count)
                throw new InvalidOperationException(changed);
            foreach(var entry in analysisCatalogSnapshot){double[] identity;
                if(!current.TryGetValue(entry.Key,out identity)||!identity.SequenceEqual(entry.Value))throw new InvalidOperationException(changed);
            }
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in completed){
                var request=item.Request;double[] identity;
                if(request==null||String.IsNullOrWhiteSpace(request.LocationFilePath)||String.IsNullOrWhiteSpace(request.ProjectPath))throw new InvalidOperationException(changed);
                string path=Path.GetFullPath(request.LocationFilePath);SessionRelative(root,path);
                if(!String.Equals(SessionRoot(request.ProjectPath),root,StringComparison.OrdinalIgnoreCase)||!seen.Add(path)||
                    !current.TryGetValue(path,out identity)||identity[0]!=request.AHistoryPhase||identity[1]!=request.LocationDataId||!CanAnalyzeRecord(path,identity[2]))
                    throw new InvalidOperationException(changed);
                // The algorithm result must belong to this file, not merely to
                // some other valid file in the same project catalog.
                var p=item.Result.Parameters;
                if(p==null||p.GetLength(1)<36)throw new InvalidOperationException(changed);
                for(int row=0;row<p.GetLength(0);row++)
                    if(p[row,0]!=identity[0]||p[row,1]!=identity[1]||!Finite(p[row,2])||Math.Abs(p[row,2]-identity[2])>0.011d+1e-12d)
                        throw new InvalidOperationException(changed);
            }
        }
        void MergeAnalysisResults(ReviewAnalysisResult[] results,out double[,] parameters,out double[,] waveforms)
        {
            MergeAnalysisResultsCore(working,matching,original,results,out parameters,out waveforms);
        }
        internal static void MergeAnalysisResultsCore(double[,] working,double[,] matching,double[,] original,ReviewAnalysisResult[] results,out double[,] parameters,out double[,] waveforms)
        {
            int oldN=working.GetLength(0),total=checked(oldN+results.Sum(x=>x.Parameters.GetLength(0)));
            int columns=Math.Max(36,working.GetLength(1)),width=matching.GetLength(1);
            foreach(var result in results)width=Math.Max(width,Math.Max(result.Raw.GetLength(1),Math.Max(result.Filtered.GetLength(1),result.Equivalent.GetLength(1))));
            if((long)3*total*width>ReviewAnalysisMetadataBudget.MergedWaveformCells)
                throw new InvalidOperationException("Combined waveform matrix exceeds the supported memory budget.");
            parameters=new double[total,columns];waveforms=new double[checked(3*total),width];
            FillAnalysisWaveformsWithNaN(waveforms);
            for(int r=0;r<oldN;r++){
                for(int c=0;c<columns;c++)parameters[r,c]=working[r,c];
                bool blocks=matching.GetLength(0)==3L*oldN;int raw=matching.GetLength(0)==oldN||blocks?r:(int)original[r,9];
                CopyAnalysisWaveformRow(matching,raw,waveforms,r);
                CopyAnalysisWaveformRow(matching,blocks?r+oldN:raw,waveforms,r+total);
                if(blocks)CopyAnalysisWaveformRow(matching,r+2*oldN,waveforms,r+2*total);
            }
            int offset=oldN;
            foreach(var result in results){
                for(int r=0;r<result.Parameters.GetLength(0);r++){
                    for(int c=0;c<columns;c++)parameters[offset+r,c]=c<result.Parameters.GetLength(1)?result.Parameters[r,c]:double.NaN;
                    CopyAnalysisWaveformRow(result.Raw,r,waveforms,offset+r);
                    CopyAnalysisWaveformRow(result.Filtered,r,waveforms,offset+r+total);
                    CopyAnalysisWaveformRow(result.Equivalent,r,waveforms,offset+r+2*total);
                }offset+=result.Parameters.GetLength(0);
            }
            // Row-aligned matrices need unique IDs across the merged project.
            for(int r=0;r<total;r++)parameters[r,9]=r;
        }
        static void FillAnalysisWaveformsWithNaN(double[,] values)
        {
            if(values.Length==0)return;
            // Primitive rectangular arrays are row-major. Doubling a canonical
            // NaN prefix preserves the old padding bits without per-cell writes.
            values[0,0]=double.NaN;
            int bytes=checked(values.Length*sizeof(double)),filled=sizeof(double);
            while(filled<bytes){int count=Math.Min(filled,bytes-filled);Buffer.BlockCopy(values,0,values,filled,count);filled+=count;}
        }
        static void CopyAnalysisWaveformRow(double[,] source,int sourceRow,double[,] target,int targetRow)
        {
            int width=source.GetLength(1);if(width==0)return;
            Buffer.BlockCopy(source,checked(sourceRow*width*sizeof(double)),target,
                checked(targetRow*target.GetLength(1)*sizeof(double)),checked(width*sizeof(double)));
        }
    }
}
