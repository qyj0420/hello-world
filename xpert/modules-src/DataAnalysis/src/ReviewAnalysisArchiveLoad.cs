using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        int archiveLoadGeneration;
        sealed class SavedAnalysisLoadPayload
        {
            internal string AnalysisId,Project,Directory,Fingerprint;
            internal ArchiveManifest Manifest;
            internal SessionPlan Plan;
            internal List<ReportChartSnapshot> Charts;
            internal long ChartBytes;
            internal bool Stale;
        }
        sealed class PendingChartBytes
        {
            internal ArchiveChart Entry;
            internal byte[] Png,CsvBytes;
            internal double[,] Matrix;
        }

        /// <summary>UI entry: background IO + progress. Sync wrapper pumps messages so the shell stays responsive.</summary>
        public bool LoadSavedAnalysis(string analysisId)
        {
            if(IsDisposed||Disposing)return false;
            if(InvokeRequired)return (bool)Invoke(new Func<string,bool>(LoadSavedAnalysis),analysisId);
            if(!IsHandleCreated)return LoadSavedAnalysisSynchronous(analysisId,null,CancellationToken.None);
            var task=LoadSavedAnalysisAsync(analysisId);
            while(!task.IsCompleted)Application.DoEvents();
            return task.GetAwaiter().GetResult();
        }
        public Task<bool> LoadSavedAnalysisAsync(string analysisId)
        {
            if(IsDisposed||Disposing)return Task.FromResult(false);
            if(InvokeRequired)return (Task<bool>)Invoke(new Func<string,Task<bool>>(LoadSavedAnalysisAsync),analysisId);
            return LoadSavedAnalysisAsyncCore(analysisId);
        }
        async Task<bool> LoadSavedAnalysisAsyncCore(string analysisId)
        {
            if(IsAnalysisRunning){ArchiveFailure(new InvalidOperationException("Wait for analysis before loading."));return false;}
            int token=Interlocked.Increment(ref archiveLoadGeneration);
            var cts=new CancellationTokenSource();
            ReviewAnalysisProgressDialog dialog=null;
            try
            {
                dialog=new ReviewAnalysisProgressDialog(languageIndex,Font);
                dialog.CancelRequested+=(s,e)=>{try{cts.Cancel();}catch{/* ignore */}};
                dialog.PrepareForDisplay(FindForm());
                dialog.BeginReadingSavedAnalysis(0,6);
                var owner=FindForm();
                if(owner!=null&&owner.Visible)dialog.Show(owner);else dialog.Show();
                Action<int,int,string> progress=(done,total,label)=>{
                    if(dialog==null||dialog.IsDisposed||token!=archiveLoadGeneration)return;
                    try{
                        if(dialog.InvokeRequired)dialog.BeginInvoke(new Action(()=>dialog.UpdateReadingSavedAnalysis(label,done,total)));
                        else dialog.UpdateReadingSavedAnalysis(label,done,total);
                    }catch(InvalidOperationException){/* disposed */}
                };
                SavedAnalysisLoadPayload payload=await Task.Run(()=>BuildSavedAnalysisLoadPayload(analysisId,progress,cts.Token),cts.Token).ConfigureAwait(true);
                if(token!=archiveLoadGeneration||IsDisposed||Disposing)return false;
                cts.Token.ThrowIfCancellationRequested();
                progress(5,6,"ApplySession");
                return ApplySavedAnalysisLoadPayload(payload);
            }
            catch(OperationCanceledException){AnalysisArchiveStatus=L("Reading saved analysis cancelled.","已取消读取旧有分析。");LastError=AnalysisArchiveStatus;NotifyReviewHost(AnalysisArchiveChanged);return false;}
            catch(Exception ex){if(IsFatalHostException(ex))throw;return ArchiveFailure(ex);}
            finally
            {
                if(dialog!=null){try{dialog.FinishAndClose();}catch{/* best-effort */}try{dialog.Dispose();}catch{/* best-effort */}}
                cts.Dispose();
            }
        }
        bool LoadSavedAnalysisSynchronous(string analysisId,Action<int,int,string> progress,CancellationToken cancel)
        {
            try{return ApplySavedAnalysisLoadPayload(BuildSavedAnalysisLoadPayload(analysisId,progress,cancel));}
            catch(Exception ex){if(IsFatalHostException(ex))throw;return ArchiveFailure(ex);}
        }
        SavedAnalysisLoadPayload BuildSavedAnalysisLoadPayload(string analysisId,Action<int,int,string> progress,CancellationToken cancel)
        {
            if(IsAnalysisRunning)throw new InvalidOperationException("Wait for analysis before loading.");
            if(string.IsNullOrWhiteSpace(analysisId)||analysisId!=Path.GetFileName(analysisId)||analysisId.StartsWith(".",StringComparison.Ordinal))
                throw new ArgumentException("Use a saved analysis ID from the current project.");
            cancel.ThrowIfCancellationRequested();
            progress=progress??((a,b,c)=>{});
            string project=SessionRoot(projectPath),directory=ArchiveChild(ArchiveRoot(project),analysisId);
            progress(0,6,"analysis.json");
            string fingerprint=AnalysisDirectoryFingerprint(directory);
            cancel.ThrowIfCancellationRequested();
            var manifest=ReadArchiveManifest(directory,analysisId,true);
            progress(1,6,"parameters.bin");
            cancel.ThrowIfCancellationRequested();
            var parameters=ReadMatrix(ArchiveChild(directory,"parameters.bin"),10000000);
            if(parameters.GetLength(0)!=manifest.SourceRowCount)throw new InvalidDataException("Archive source row count does not match its matrix.");
            progress(2,6,"pdmap.bin");
            cancel.ThrowIfCancellationRequested();
            var waves=ReadMatrix(ArchiveChild(directory,"pdmap.bin"),IntPtr.Size==4?8000000:ReviewAnalysisMetadataBudget.ArchivedPdmapCellsX64);
            progress(3,6,"session.json");
            cancel.ThrowIfCancellationRequested();
            byte[] session=ReadLimited(ArchiveChild(directory,"session.json"),ReviewAnalysisMetadataBudget.SessionMetadataBytes);
            var payload=ArchiveSessionJson().DeserializeObject(ArchiveEncoding.GetString(session)) as Dictionary<string,object>;
            if(payload==null||!payload.ContainsKey("schema")||!object.Equals(payload["schema"],"HVX.AnalysisSession.Utf8")||!object.Equals(payload["version"],1))
                throw new InvalidDataException("Invalid analysis session schema.");
            string[,] sessionFiles=SessionTable(payload["files"],11),sessionSettings=SessionTable(payload["settings"],2);
            string savedDiagnostics=null;
            for(int row=0;row<sessionSettings.GetLength(0);row++)if(sessionSettings[row,0]=="AlgorithmDiagnostics"){savedDiagnostics=sessionSettings[row,1];break;}
            if(IsSegmentedDiagnosticsEnvelope(savedDiagnostics))VerifyDiagnosticsPartsInManifest(manifest,savedDiagnostics);
            bool packed=manifest.Files.Any(file=>file.Path==ReviewDiagnosticArchivePack.MemberName);
            if(!packed&&savedDiagnostics!=null&&savedDiagnostics.IndexOf(ReviewDiagnosticSpill.ReferencePrefix,StringComparison.Ordinal)>=0)
                throw new InvalidDataException("The analysis archive is missing its algorithm diagnostics ("+ReviewDiagnosticArchivePack.MemberName+").");
            Action<int,int,string> diagnosticsProgress=(completed,total,part)=>{
                cancel.ThrowIfCancellationRequested();
                progress(4,6,string.IsNullOrEmpty(part)?(packed?ReviewDiagnosticArchivePack.MemberName:"AlgorithmDiagnostics"):part);
            };
            progress(4,6,IsSegmentedDiagnosticsEnvelope(savedDiagnostics)?"diagnostics-part":packed?ReviewDiagnosticArchivePack.MemberName:"AlgorithmDiagnostics");
            cancel.ThrowIfCancellationRequested();
            var plan=PrepareSession(project,parameters,waves,sessionFiles,sessionSettings,directory,diagnosticsProgress);
            bool stale=plan.ProjectChanged||plan.Changed>0||plan.Missing>0;
            var choices=ReadArchiveSelection(directory,manifest.Charts.Select(chart=>chart.Sequence));
            var pendingCharts=new List<PendingChartBytes>();
            long bytes=0;int chartIndex=0;
            foreach(var entry in manifest.Charts)
            {
                cancel.ThrowIfCancellationRequested();
                progress(4,6,"chart_"+entry.Sequence.ToString("D6"));
                byte[] png=ReadLimited(ArchiveChild(directory,entry.Image),ReportChartBytesLimit);
                if(png.Length<24||png[0]!=137||png[1]!=80||png[2]!=78||png[3]!=71||png[12]!=73||png[13]!=72||png[14]!=68||png[15]!=82||
                    PngDimension(png,16)!=entry.Width||PngDimension(png,20)!=entry.Height)throw new InvalidDataException("Invalid report PNG header or dimensions.");
                using(var stream=new MemoryStream(png))using(var bitmap=Image.FromStream(stream,true,true))
                    if(bitmap.Width!=entry.Width||bitmap.Height!=entry.Height)throw new InvalidDataException("Report PNG dimensions do not match the manifest.");
                byte[] csvBytes=ReadLimited(ArchiveChild(directory,entry.Csv),ReportChartBytesLimit);
                var matrix=ReadMatrix(ArchiveChild(directory,entry.Parameters),ReportChartBytesLimit/8);
                if(matrix.GetLength(0)!=entry.Rows.Length||matrix.GetLength(1)<36)throw new InvalidDataException("Report rows do not match the archived parameter matrix.");
                pendingCharts.Add(new PendingChartBytes{Entry=entry,Png=png,CsvBytes=csvBytes,Matrix=matrix});
                bytes+=png.LongLength+csvBytes.LongLength+matrix.LongLength*8L;
                if(bytes>ReportChartBytesLimit)throw new InvalidDataException("Archived report attachments exceed 64 MiB.");
                chartIndex++;
            }
            if(AnalysisDirectoryFingerprint(directory)!=fingerprint)throw new IOException("The saved analysis changed while loading. Reload the latest archive.");
            var charts=new List<ReportChartSnapshot>();
            foreach(var pending in pendingCharts)
            {
                var entry=pending.Entry;
                string csv=ArchiveEncoding.GetString(pending.CsvBytes);
                var chart=new ReportChartSnapshot(entry.Sequence,entry.Kind,entry.Title,entry.Region,project,entry.Mode,entry.Language,pending.Png,entry.Width,entry.Height,pending.Matrix,(int[])entry.Rows.Clone(),csv);
                chart.SetCreatedUtc(entry.CreatedUtc);
                if(entry.Historical)chart.MarkHistorical(entry.StatusReason??"Previously retained historical snapshot.",english);
                chart.SetReportSelection(choices.Contains(entry.Sequence));chart.SetSavedPaths(ArchiveChild(directory,entry.Image),ArchiveChild(directory,entry.Csv));
                if(stale)chart.MarkHistorical("Project or source file fingerprint changed; review the historical snapshot before including it.",english);
                if(entry.AcquisitionReferenceOnly||HasAcquisitionRiskInMatrix(pending.Matrix,plan.AcquisitionQualities))chart.MarkAcquisitionReferenceOnly(english);
                charts.Add(chart);
            }
            return new SavedAnalysisLoadPayload{
                AnalysisId=analysisId,Project=project,Directory=directory,Fingerprint=fingerprint,
                Manifest=manifest,Plan=plan,Charts=charts,ChartBytes=bytes,Stale=stale
            };
        }
        bool ApplySavedAnalysisLoadPayload(SavedAnalysisLoadPayload loaded)
        {
            bool priorDirty=analysisDirty,priorContent=analysisHadContent,loadedSuccessfully=false,loadedModified=false;
            string priorStamp=savedAnalysisParameterStamp,priorFingerprint=loadedAnalysisFingerprint,priorBackup=LastAnalysisBackupPath;
            suppressAnalysisDirty++;bool previousSuppress=suppressArchivePrompt;suppressArchivePrompt=true;
            Action rollback=null;bool applying=false;ArchiveConclusion priorConclusion=null;
            try
            {
                priorConclusion=CurrentConclusionSnapshot();
                using(var probe=new PdReviewControl()){probe.PromptSavedAnalysisOnProjectLoad=false;probe.ApplySession(loaded.Plan);}
                loadedModified=loaded.Stale;
                rollback=CaptureNamedAnalysisRollback();applying=true;ApplySession(loaded.Plan);
                AnalysisSessionStatus="SessionLoaded; rows="+SourceRowCount+"; missing="+loaded.Plan.Missing+"; changed="+loaded.Plan.Changed+"; new="+loaded.Plan.NewFiles+"; zeroVoltage="+loaded.Plan.ZeroVoltage;
                reportCharts.Clear();reportCharts.AddRange(loaded.Charts);reportChartBytes=loaded.ChartBytes;
                ChartReportRequestSequence=Math.Max(ChartReportRequestSequence,loaded.Charts.Count==0?0:loaded.Charts.Max(chart=>chart.Sequence));
                DisplayModeIndex=loaded.Manifest.DisplayMode;spectrum.ViewPlaneIndex=loaded.Manifest.ViewPlane;
                if(loaded.Manifest.SelectedIdentity.Length==3)
                    for(int row=0;row<SourceRowCount;row++)
                        if(working[row,0]==loaded.Manifest.SelectedIdentity[0]&&working[row,1]==loaded.Manifest.SelectedIdentity[1]&&working[row,9]==loaded.Manifest.SelectedIdentity[2])
                        {SelectSourceRowCore(row,false);break;}
                analysisTree.RestoreNodeView(loaded.Manifest.ExpandedNodes,loaded.Manifest.SelectedNode,loaded.Manifest.TopNode);
                LoadedAnalysisId=loaded.AnalysisId;loadedArchiveRoot=loaded.Project;
                loadedAnalysisFingerprint=loaded.Fingerprint;LastAnalysisBackupPath="";
                evaluationResult=null;evaluationContext=null;
                RememberSavedConclusion(loaded.Stale?null:loaded.Manifest.Conclusion);
                AnalysisArchiveStatus=L("Analysis loaded: ","分析已加载：")+HVX.ProjectModel.AnalysisDateText.DisplayAutomaticName(loaded.Manifest.Name);
                if(loaded.Plan.ProjectChanged)AnalysisArchiveStatus+=L("; cable/project metadata changed: previous results are invalid; run analysis again.","；电缆结构或项目参数已变化，旧结果已失效，请重新分析。");
                if(loaded.Plan.Missing>0||loaded.Plan.Changed>0)AnalysisArchiveStatus+=L("; unavailable source records skipped: ","；已跳过不可用记录：")+(loaded.Plan.Missing+loaded.Plan.Changed);
                if(loaded.Stale)AnalysisArchiveStatus+=L("; changed source data: report snapshots unchecked.","；源数据已变化，历史报告图表已取消勾选。");
                ChartReportStatusMessage=L("Report attachments restored: ","已恢复报告附件：")+loaded.Charts.Count;
                loadedSuccessfully=true;NotifyReviewHost(AnalysisArchiveChanged);NotifyReviewHost(ChartReportStatusChanged);return true;
            }
            catch(Exception ex)
            {
                if(IsFatalHostException(ex))throw;
                if(applying&&rollback!=null)
                    try{rollback();RememberSavedConclusion(priorConclusion);}catch(Exception restoreError){if(IsFatalHostException(restoreError))throw;return ArchiveFailure(new InvalidOperationException(ex.Message+"; rollback failed: "+restoreError.Message));}
                return ArchiveFailure(ex);
            }
            finally
            {
                suppressArchivePrompt=previousSuppress;suppressAnalysisDirty--;
                if(loadedSuccessfully){MarkAnalysisClean();if(loadedModified)MarkAnalysisDirty();}
                else{analysisDirty=priorDirty;analysisHadContent=priorContent;savedAnalysisParameterStamp=priorStamp;loadedAnalysisFingerprint=priorFingerprint;LastAnalysisBackupPath=priorBackup;}
            }
        }
    }
}
