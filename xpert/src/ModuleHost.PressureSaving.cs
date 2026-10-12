using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.ProjectEvidence;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        readonly Dictionary<Control,PressureAnalysisTrial> pressureAnalysisTrials=new Dictionary<Control,PressureAnalysisTrial>();
        PressureAnalysisTrial GetPressureAnalysisTrial(Control page,string project)
        {
            PressureAnalysisTrial trial;
            if(pressureAnalysisTrials.TryGetValue(page,out trial)){
                if(trial.IsForProject(project))return trial;
                trial.End();
            }else page.Disposed+=PressureAnalysisPageDisposed;
            trial=new PressureAnalysisTrial(project);pressureAnalysisTrials[page]=trial;
            diagnostics.Info("PressureAnalysis","Trial started="+trial.Id+"; project="+trial.Project);
            return trial;
        }
        void PressureAnalysisPageDisposed(object sender,EventArgs args)
        {
            var page=(Control)sender;PressureAnalysisTrial trial;
            if(pressureAnalysisTrials.TryGetValue(page,out trial)){
                trial.End();pressureAnalysisTrials.Remove(page);
                diagnostics.Info("PressureAnalysis","Trial ended="+trial.Id+"; saved files retained.");
            }
            FactoryPressureSession session;
            if(factoryPressureSessions.TryGetValue(page,out session)){RetirePressureSession(session);factoryPressureSessions.Remove(page);}
            page.Disposed-=PressureAnalysisPageDisposed;
        }
        bool IsCurrentPressureSession(Control page,FactoryPressureSession session)
        {
            FactoryPressureSession current;
            return !disposed&&!page.IsDisposed&&!session.Trial.Ended&&!String.IsNullOrWhiteSpace(state.ProjectPath)&&session.Trial.IsForProject(state.ProjectPath)&&
                factoryPressureSessions.TryGetValue(page,out current)&&Object.ReferenceEquals(session,current);
        }
        bool IsCurrentPressureAnalysisTrial(Control page,PressureAnalysisTrial trial)
        {
            PressureAnalysisTrial current;
            return !disposed&&!page.IsDisposed&&!trial.Ended&&!String.IsNullOrWhiteSpace(state.ProjectPath)&&
                trial.IsForProject(state.ProjectPath)&&pressureAnalysisTrials.TryGetValue(page,out current)&&Object.ReferenceEquals(trial,current);
        }
        bool IsCurrentPressureAnalysisEpoch(Control page,PressureAnalysisTrial trial,PressureBackgroundAnalysisQueue queue)
        {return IsCurrentPressureAnalysisTrial(page,trial)&&Object.ReferenceEquals(trial.AnalysisQueue,queue)&&!queue.Cancellation.IsCancellationRequested;}
        void ConnectPressureRoundSaving(Control page,FactoryPressureSession session,double velocity)
        {
            bool saving=false;
            AppSettings endpointSettings=null;
            string endpointRecord="",endpointAcquisition="";
            bool endpointManual=false;double endpointThreshold=100;
            TrySet(page,"BackgroundPressureAnalysisEnabled",true);
            TryInvoke(page,"SetEndpointReviewAvailable",new object[]{false});
            var endpoint=ModuleAuditSubscription.TryCreate(page,"EndpointReviewRequested",async()=>{
                long sequence=ReadInt64(page,"EndpointReviewSequence");Control inspector=null;
                try {
                    if(session.Trial.AnalysisQueue.PendingCount>0||saving||endpointSettings==null||!IsCurrentPressureSession(page,session)||
                        LateBound.ReadString(page,"LocationAcquisitionId")!=endpointAcquisition||LateBound.ReadBoolean(page,true,"HasActiveHardwareSession"))
                        throw new InvalidOperationException("当前轮次尚未完成分析，不能进行近远端识别。");
                    string path=endpointRecord,id=endpointAcquisition;
                    Func<bool> current=()=>IsCurrentPressureSession(page,session)&&LateBound.ReadString(page,"LocationAcquisitionId")==id&&
                        ReadInt64(page,"EndpointReviewSequence")==sequence&&!LateBound.ReadBoolean(page,true,"HasActiveHardwareSession");
                    string module=Path.Combine(modulesRoot,"DataAnalysis","HVX.PdReview.V1.0.dll");
                    inspector=(Control)Activator.CreateInstance(System.Reflection.Assembly.LoadFrom(module).GetType("HVX.PdReview.PdReviewControl",true));
                    ApplyPressureAnalysisSettings(inspector,endpointSettings);
                    TrySet(inspector,"PromptSavedAnalysisOnProjectLoad",false);
                    if(!TrySet(inspector,"ProjectPath",session.Project))throw new InvalidOperationException("无法载入本次分析项目。");
                    inspector.GetType().GetMethod("SetFileAnalysisThreshold").Invoke(inspector,new object[]{path,endpointManual,endpointThreshold});
                    var inspect=inspector.GetType().GetMethod("ShowPressureEndpointReviewAsync");
                    if(inspect==null)throw new InvalidOperationException("近远端识别接口不匹配，请修复安装。");
                    bool shown=await (Task<bool>)inspect.Invoke(inspector,new object[]{path,current});
                    if(!shown&&current())MessageBox.Show(page.FindForm(),LateBound.ReadString(inspector,"AnalysisStatus"),"近远端识别",MessageBoxButtons.OK,MessageBoxIcon.Information);
                }catch(Exception error){
                    diagnostics.Error("PressureEndpointReview","Read-only inspection failed; saved results retained.",error);
                    if(IsCurrentPressureSession(page,session))MessageBox.Show(page.FindForm(),PressurePostProcessFeedback.Describe(error,false,LateBound.ReadString(settings,"LanguageCode")),"近远端识别未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }finally{
                    try{if(inspector!=null)inspector.Dispose();}
                    finally{if(!page.IsDisposed)TryInvoke(page,"CompleteEndpointReview",new object[]{sequence});}
                }
            });
            session.KeepPostProcessSubscription(endpoint);
            var save=ModuleAuditSubscription.TryCreate(page,"SaveRequested",async()=>{
                if(saving||!IsCurrentPressureSession(page,session))return;
                saving=true;
                var elapsed=Stopwatch.StartNew();
                string timingAcquisition="";
                try{
                    string acquisitionId=LateBound.ReadString(page,"LocationAcquisitionId");
                    timingAcquisition=acquisitionId;
                    if(String.IsNullOrWhiteSpace(acquisitionId))throw new InvalidDataException("本轮采集标识缺失，未保存。");
                    if(session.Trial.HasCommitted(acquisitionId)){TryInvoke(page,"CompleteSave",new object[]{true,""});return;}
                    string path=await (Task<string>)page.GetType().GetMethod("SaveCurrentPressureRoundAsync").Invoke(page,new object[]{velocity});
                    if(!IsCurrentPressureSession(page,session)){session.RevokeBatch();return;}
                    if(LateBound.ReadString(page,"LocationAcquisitionId")!=acquisitionId)throw new InvalidOperationException("保存期间采集轮次已改变，原始记录保留，未加入累计分析。");
                    var trial=session.Trial;
                    var prepared=await trial.PrepareCommitAsync(acquisitionId,path);
                    // Hashing is file-only background work, not a save ACK.
                    // Navigation, Stop and a new acquisition may win that await.
                    if(!IsCurrentPressureSession(page,session)||!Object.ReferenceEquals(session.Trial,trial)||pressurePagesExiting.Contains(page)){session.RevokeBatch();return;}
                    if(LateBound.ReadString(page,"LocationAcquisitionId")!=acquisitionId||
                        LateBound.ReadString(page,"PostProcessStateCode")!="SAVE_REQUESTED"||
                        LateBound.ReadString(page,"PressureStateCode")!="COMPLETE"||
                        LateBound.ReadBoolean(page,false,"HasActiveHardwareSession")||
                        LateBound.ReadString(page,"EmergencyStopStatusCode")=="ACTIVE_LATCHED")
                        throw new InvalidOperationException("保存期间当前轮次已改变，记录保留，未加入累计分析。");
                    trial.CommitPrepared(prepared);
                    TryInvoke(page,"CompleteSave",new object[]{true,""});
                }catch(Exception error){session.RevokeBatch();diagnostics.Error("PressureSave","Round was not committed.",error);if(IsCurrentPressureSession(page,session)&&!pressurePagesExiting.Contains(page)&&LateBound.ReadString(page,"LocationAcquisitionId")==timingAcquisition&&LateBound.ReadString(page,"PostProcessStateCode")=="SAVE_REQUESTED")TryInvoke(page,"CompleteSave",new object[]{false,PressurePostProcessFeedback.Describe(error,true,LateBound.ReadString(settings,"LanguageCode"))});}
                finally{TracePressurePostProcessTiming(session,timingAcquisition,"save",elapsed);saving=false;}
            });
            session.KeepPostProcessSubscription(save);
            var delete=ModuleAuditSubscription.TryCreate(page,"DeleteRequested",async()=>{
                if(saving||!IsCurrentPressureSession(page,session))return;
                string acquisition=LateBound.ReadString(page,"LocationAcquisitionId");long action=ReadInt64(page,"ActionSequence");
                bool succeeded=false;string failure="";
                try{
                    Func<bool> current=()=>ReadPressureUi(page,()=>IsCurrentPressureSession(page,session)&&LateBound.ReadString(page,"LocationAcquisitionId")==acquisition&&
                        ReadInt64(page,"ActionSequence")==action&&LateBound.ReadString(page,"ActionCode")=="DELETE"&&LateBound.ReadBoolean(page,false,"ActionPending")&&
                        !LateBound.ReadBoolean(page,true,"HasActiveHardwareSession")&&LateBound.ReadBoolean(page,false,"DataSaved"));
                    if(!current())throw new InvalidOperationException("当前轮次已改变，未删除记录。");
                    session.RevokeBatch();session.Trial.CancelAnalysis();
                    string recovery=await Task.Run(()=>session.Trial.RecoverCommittedRound(acquisition,current));
                    if(!current())throw new InvalidOperationException("当前页面已改变，请核对本轮记录。");
                    succeeded=(bool)page.GetType().GetMethod("CompleteCurrentRoundDeleted").Invoke(page,new object[]{acquisition});
                    if(!succeeded)throw new InvalidOperationException("本轮记录已移至恢复目录，界面未确认，请核对项目。");
                    endpointSettings=null;endpointRecord=endpointAcquisition="";TryInvoke(page,"SetEndpointReviewAvailable",new object[]{false});
                    diagnostics.Info("PressureDelete","Current acquisition="+acquisition+"; exact committed records moved to recovery="+recovery+"; raw and calibration retained.");
                }catch(Exception error){session.RevokeBatch();diagnostics.Error("PressureDelete","Current round deletion was not acknowledged.",error);failure=PressurePostProcessFeedback.Describe(error,true,LateBound.ReadString(settings,"LanguageCode"));}
                finally{if(!page.IsDisposed)TryInvoke(page,"CompleteAction",new object[]{action,succeeded,failure});}
            });
            session.KeepPostProcessSubscription(delete);
            var overRange=ModuleAuditSubscription.TryCreate(page,"OverRangeDecisionRequested",async()=>{
                if(!IsCurrentPressureSession(page,session))return;
                var elapsed=Stopwatch.StartNew();
                string timingAcquisition="";
                try {
                    timingAcquisition=LateBound.ReadString(page,"LocationAcquisitionId");
                    string decision=await (Task<string>)page.GetType().GetMethod("ReviewPressureOverRangeAsync").Invoke(page,null);
                    if(!IsCurrentPressureSession(page,session)){session.RevokeBatch();return;}
                    diagnostics.Info("PressureQuality","Project="+session.Project+"; decision="+decision+"; "+LateBound.ReadString(page,"LastPressureRangeReviewDetails"));
                    if(decision=="Pending"||decision=="Ignored")return;
                    session.RevokeBatch();
                    if(decision=="Retry")page.GetType().GetMethod("RequestReviewedRangeRetry").Invoke(page,null);
                }catch(Exception error){session.RevokeBatch();diagnostics.Error("PressureQuality","量程处理未完成，未启动重试；原始记录保留。",error);if(IsCurrentPressureSession(page,session))TryInvoke(page,"InterruptPressureContinuation",null);}
                finally{TracePressurePostProcessTiming(session,timingAcquisition,"quality-choice-including-operator-wait",elapsed);}
            });
            session.KeepPostProcessSubscription(overRange);
            var analysis=ModuleAuditSubscription.TryCreate(page,"AnalysisRequested",async()=>{
                if(LateBound.ReadBoolean(page,false,"EndpointInspectionBusy")||LateBound.ReadBoolean(page,false,"AutomaticPressureStopped")||!IsCurrentPressureSession(page,session))return;
                var analysisQueue=session.Trial.AnalysisQueue;
                Func<bool> currentAnalysis=()=>IsCurrentPressureAnalysisEpoch(page,session.Trial,analysisQueue);
                Task queuedWork=null;
                string acquisitionId=LateBound.ReadString(page,"LocationAcquisitionId");
                try{
                    if(analysisQueue.Contains(acquisitionId))return;
                    // Freeze committed inputs and options at the save ACK. No
                    // subsequent capture arrays or live page options enter work.
                    var analysisSettings=((AppSettings)settings).Clone();
                    string[] rounds=session.Trial.Snapshot();
                    string savedRecord=session.Trial.CommittedPath(acquisitionId);
                    bool manualWarning=LateBound.ReadBoolean(page,false,"CurrentRoundManualOverRangeWarning");
                    bool explicitSave=LateBound.ReadBoolean(page,false,"CurrentRoundExplicitSave");
                    var manualOption=page.GetType().GetProperty("ManualAnalysisThresholdEnabled");
                    var thresholdOption=page.GetType().GetProperty("ManualAnalysisThresholdPc");
                    var allOption=page.GetType().GetProperty("ApplyAnalysisThresholdToAllRounds");
                    if(manualOption==null||thresholdOption==null||allOption==null)throw new InvalidOperationException("加压分析参数接口不匹配，请修复安装。");
                    var thresholdChoices=session.Trial.SnapshotThresholds(savedRecord,(bool)manualOption.GetValue(page,null),
                        Convert.ToDouble(thresholdOption.GetValue(page,null),CultureInfo.InvariantCulture),(bool)allOption.GetValue(page,null));
                    var hashes=session.Trial.SnapshotHashes();
                    string group=Path.GetFileNameWithoutExtension(savedRecord);
                    int actualRange=-1;var range=page.GetType().GetProperty("CapturedPicoRangeIndex");
                    if(range!=null)actualRange=Convert.ToInt32(range.GetValue(page,null),CultureInfo.InvariantCulture);
                    string metadataHash=await Task.Run(()=>PressureFixtureJournal.MetadataHash(session.Project));
                    if(!currentAnalysis())throw new OperationCanceledException("本次分析已停止，后台结果未应用。");
                    if(analysisQueue.Contains(acquisitionId))return;
                    var analysisSetupAllowed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var setupCancellation=analysisQueue.Cancellation.Register(()=>analysisSetupAllowed.TrySetCanceled());
                    var analysisDispatcher=(Control)page.FindForm()??page;
                    var analysisContext=PressureBackgroundAnalysisQueue.CreateDispatcher(analysisDispatcher,analysisQueue.Cancellation);
                    queuedWork=analysisQueue.Enqueue(acquisitionId,()=>analysisContext.Run(async()=>{
                        // Do not let synchronous review construction/catalog
                        // loading win the next-round preparation UI turn.
                        // This is managed scheduling, not a device cooldown.
                        await analysisSetupAllowed.Task;
                        Control review=null;PressureAnalysisReviewOwnership reviewOwnership=null;var elapsed=Stopwatch.StartNew();
                        string stage="setup-and-load";
                        Action<int,int,string> status=(completed,total,error)=>{
                            if(!currentAnalysis()||!page.IsHandleCreated||analysisQueue.ActiveAcquisitionId!=acquisitionId)return;
                            TryInvoke(page,"ReportBackgroundPressureAnalysis",new object[]{acquisitionId,group,completed,total,
                                Math.Max(0,analysisQueue.PendingCount-1),error??""});
                        };
                        try {
                            await RequirePressureAnalysisSnapshotAsync(page,session.Trial,analysisQueue,metadataHash,hashes);
                            status(0,rounds.Length,"");
                            string file=Path.Combine(modulesRoot,"DataAnalysis","HVX.PdReview.V1.0.dll");
                            review=(Control)Activator.CreateInstance(System.Reflection.Assembly.LoadFrom(file).GetType("HVX.PdReview.PdReviewControl",true));
                            reviewOwnership=new PressureAnalysisReviewOwnership(analysisDispatcher,review);
                            ApplyPressureAnalysisSettings(review,analysisSettings);
                            TrySet(review,"PressureAnalysisCacheScope",session.Trial.Id+":"+analysisQueue.Id);
                            TrySet(review,"PromptSavedAnalysisOnProjectLoad",false);
                            if(!TrySet(review,"ProjectPath",session.Project))throw new InvalidOperationException("无法载入本次分析项目。");
                            var setThreshold=review.GetType().GetMethod("SetFileAnalysisThreshold");
                            if(setThreshold==null)throw new InvalidOperationException("分析阈值接口不匹配，请修复安装。");
                            foreach(var choice in thresholdChoices)setThreshold.Invoke(review,new object[]{choice.Key,choice.Value.Manual,choice.Value.Pc});
                            TracePressurePostProcessTiming(session,acquisitionId,stage,elapsed,rounds.Length);
                            stage="read-and-analyze-saved-rounds";elapsed.Restart();
                            var analyze=review.GetType().GetMethod("AnalyzeSavedPressureFilesWithProgressAsync");
                            if(analyze==null)throw new InvalidOperationException("分析组件版本不匹配，请修复安装。");
                            Action<int,int> report=(completed,total)=>{
                                if(page.IsDisposed||!page.IsHandleCreated)return;
                                try{page.BeginInvoke((Action)(()=>status(completed,total,"")));}catch(InvalidOperationException){}
                            };
                            if(!currentAnalysis())throw new OperationCanceledException("本次分析已停止。");
                            var engineTask=(Task<double[,]>)analyze.Invoke(review,new object[]{rounds,report});
                            reviewOwnership.Watch(engineTask,analysisQueue.Cancellation);
                            double[,] matrix=await engineTask;
                            diagnostics.Info("PressureAnalysis","Background engine finished; acquisition="+acquisitionId+"; verifying committed snapshot.");
                            await RequirePressureAnalysisSnapshotAsync(page,session.Trial,analysisQueue,metadataHash,hashes);
                            TracePressurePostProcessTiming(session,acquisitionId,stage,elapsed,rounds.Length);
                            diagnostics.Info("PressureAnalysis","Trial="+session.Trial.Id+"; acquisition="+acquisitionId+"; saved rounds="+rounds.Length+"; rows="+matrix.GetLength(0)+"; reused="+LateBound.ReadString(review,"PressureAnalysisCacheHits"));
                            // Late results may update only their own idle saved
                            // round. They never touch a new capture or mailbox.
                            if(currentAnalysis()&&IsCurrentPressureSession(page,session)&&LateBound.ReadString(page,"LocationAcquisitionId")==acquisitionId&&
                                !LateBound.ReadBoolean(page,true,"HasActiveHardwareSession")&&!LateBound.ReadBoolean(page,false,"IsBusy")&&
                                LateBound.ReadBoolean(page,false,"DataSaved")&&LateBound.ReadString(page,"PostProcessStateCode")=="COMPLETE"&&
                                LateBound.ReadString(page,"PressureStateCode")=="COMPLETE"){
                                var geometry=review.GetType().GetMethod("CapturePressureSpectrumGeometry");
                                var setGeometry=page.GetType().GetMethod("SetPressureSpectrumGeometry");
                                if(geometry==null||setGeometry==null)throw new InvalidOperationException("谱图几何接口不匹配，请修复安装。");
                                if(!manualWarning){setGeometry.Invoke(page,new[]{geometry.Invoke(review,null)});TryInvoke(page,"SetSpectrumDataMatrix",new object[]{matrix});}
                                TryInvoke(page,"CompleteBackgroundPressureAnalysis",new object[]{acquisitionId,true,""});
                                if(manualWarning){TryInvoke(page,"ReportManualOverRangeAnalysisComplete",new object[]{acquisitionId});endpointSettings=null;endpointRecord=endpointAcquisition="";TryInvoke(page,"SetEndpointReviewAvailable",new object[]{false});}
                                else {endpointSettings=analysisSettings;endpointRecord=savedRecord;endpointAcquisition=acquisitionId;
                                    endpointManual=thresholdChoices[savedRecord].Manual;endpointThreshold=thresholdChoices[savedRecord].Pc;TryInvoke(page,"SetEndpointReviewAvailable",new object[]{true});}
                            }
                            if(!manualWarning)await ReviewSavedPressureCalibrationChargeAsync(page,session,review,matrix,savedRecord,acquisitionId,actualRange,currentAnalysis);
                            stage="saved-endpoint-review";elapsed.Restart();
                            if(!manualWarning)await ReviewSavedPressureFixtureAsync(page,session,review,matrix,savedRecord,acquisitionId,metadataHash,hashes[savedRecord],currentAnalysis);
                            status(rounds.Length,rounds.Length,"");
                        }catch(OperationCanceledException){throw;}
                        catch(Exception error){
                            diagnostics.Error("PressureAnalysis","Saved records retained; background analysis failed for acquisition="+acquisitionId,error);
                            if(currentAnalysis()){
                                TryInvoke(page,"InterruptPressureContinuation",null); // future scheduling only; no device cancellation
                                status(0,rounds.Length,PressurePostProcessFeedback.Describe(error,false,analysisSettings.LanguageCode));
                                TryInvoke(page,"CompleteBackgroundPressureAnalysis",new object[]{acquisitionId,false,error.Message});
                            }
                            throw;
                        }finally{TracePressurePostProcessTiming(session,acquisitionId,stage,elapsed,rounds.Length);if(reviewOwnership!=null)reviewOwnership.Dispose();else if(review!=null)review.Dispose();}
                    }));
                    // Saving has already been acknowledged. The existing next
                    // range/cadence/intent checks still own actual continuation.
                    try {
                        if(!explicitSave&&!manualWarning&&IsCurrentPressureSession(page,session)){
                            try{await (Task<bool>)page.GetType().GetMethod("ContinueCompletedPressurePlanAsync").Invoke(page,null);}
                            catch(Exception error){TryInvoke(page,"InterruptPressureContinuation",null);session.RevokeBatch();diagnostics.Error("PressureContinuation","Saved round retained; next-round preparation failed.",error);}
                        }
                    } finally {analysisSetupAllowed.TrySetResult(true);}
                    try{await queuedWork;}finally{setupCancellation.Dispose();} // observe managed result, not a next-round gate
                }catch(OperationCanceledException){ }
                catch(Exception error){
                    diagnostics.Error("PressureAnalysisQueue","Saved records retained; queued analysis did not complete.",error);
                    if(currentAnalysis()){
                        TryInvoke(page,"InterruptPressureContinuation",null);
                        // A running job has already published its identity-bound
                        // failure. Do not overwrite the next FIFO job's status.
                        if(queuedWork==null)TryInvoke(page,"ReportBackgroundPressureAnalysis",new object[]{acquisitionId,acquisitionId,0,0,0,PressurePostProcessFeedback.Describe(error,false,LateBound.ReadString(settings,"LanguageCode"))});
                    }
                }
            });
            session.KeepPostProcessSubscription(analysis);
        }
        async Task RequirePressureAnalysisSnapshotAsync(Control page,PressureAnalysisTrial trial,PressureBackgroundAnalysisQueue queue,string metadataHash,Dictionary<string,string> hashes)
        {
            if(!IsCurrentPressureAnalysisEpoch(page,trial,queue))throw new OperationCanceledException("本次分析已停止，后台结果未应用。");
            await Task.Run(()=>{
                if(PressureFixtureJournal.MetadataHash(trial.Project)!=metadataHash)throw new InvalidDataException("项目参数已改变，原保存记录保留，请重新分析。");
                foreach(var entry in hashes) {
                    PressureFixtureJournal.NoLinks(entry.Key);
                    if(PressureFixtureJournal.Hash(entry.Key)!=entry.Value)throw new InvalidDataException("已保存的定位记录已改变，后台结果未应用。");
                }
            });
            if(!IsCurrentPressureAnalysisEpoch(page,trial,queue))throw new OperationCanceledException("本次分析已停止，后台结果未应用。");
        }
        async Task ReviewSavedPressureFixtureAsync(Control page,FactoryPressureSession originalSession,Control review,double[,] matrix,
            string savedRecord,string acquisitionId,string metadataHash,string sourceHash,Func<bool> currentAnalysis)
        {
            if(!currentAnalysis())return;
            var method=review.GetType().GetMethod("GetPressureTerminationEvidenceAsync");
            if(method==null)throw new InvalidDataException("端部分析暂不可用。");
            var endpoints=await (Task<double[,]>)method.Invoke(review,new object[]{matrix,savedRecord});
            if(!currentAnalysis())return;
            if(PressureFixtureJournal.MetadataHash(originalSession.Project)!=metadataHash||PressureFixtureJournal.Hash(savedRecord)!=sourceHash)
                throw new InvalidDataException("原轮次或项目参数已改变，端部结果未应用。");
            var evidence=PressureFixtureJournal.Select(originalSession.Project,endpoints);
            if(evidence.Length==0)return;
            // Analysis may finish during another valid native round. Stop only
            // future scheduling now; never request Stop/HVoff from this queue.
            TryInvoke(page,"InterruptPressureContinuation",null);
            while(currentAnalysis()&&
                (LateBound.ReadBoolean(page,true,"HasActiveHardwareSession")||LateBound.ReadBoolean(page,false,"IsBusy")||LateBound.ReadBoolean(page,false,"ActionPending")))
                await Task.Delay(50);
            if(!currentAnalysis())return;
            FactoryPressureSession current;
            if(factoryPressureSessions.TryGetValue(page,out current))current.RevokeBatch();
            await ConfirmSavedPressureFixtureAsync(page,originalSession,review,matrix,savedRecord,acquisitionId,metadataHash,sourceHash,evidence,currentAnalysis);
        }
        async Task ReviewSavedPressureCalibrationChargeAsync(Control page,FactoryPressureSession session,Control review,double[,] matrix,
            string savedRecord,string acquisitionId,int actualRange,Func<bool> currentAnalysis)
        {
            try {
                if(!currentAnalysis())return;
                if(actualRange<0||actualRange>10)throw new InvalidDataException("本轮真实采集量程无法确认。");
                var method=review.GetType().GetMethod("GetPressureCalibrationChargeReview");
                if(method==null)throw new InvalidDataException("当前采集的校准量程复核接口不匹配。");
                object result=method.Invoke(review,new object[]{matrix,savedRecord,actualRange});
                if(LateBound.ReadString(result,"Schema")!="HVX.Pressure.CalibrationChargeReview/V1")throw new InvalidDataException("本轮校准量程复核结果无法确认。");
                int count=Convert.ToInt32(result.GetType().GetProperty("IndependentPulseCount").GetValue(result,null),CultureInfo.InvariantCulture);
                diagnostics.Info("PressureCalibrationChargeReview","acquisition="+acquisitionId+"; captureRange="+actualRange+"; independentPulsesAbove200nC="+count);
                if(!LateBound.ReadBoolean(result,false,"ShouldRemind")||!session.CalibrationChargeReminders.Add(acquisitionId))return;
                while(currentAnalysis()&&
                    (LateBound.ReadBoolean(page,true,"HasActiveHardwareSession")||LateBound.ReadBoolean(page,false,"IsBusy")||LateBound.ReadBoolean(page,false,"ActionPending")))await Task.Delay(50);
                if(currentAnalysis())MessageBox.Show(page.FindForm(),"请检查校准源是否存在低电量或校准档位混淆。","校准核对",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }catch(Exception error){diagnostics.Error("PressureCalibrationChargeReview","Saved-round supplementary review failed; original quality and hardware permission unchanged.",error);}
        }
        void TracePressurePostProcessTiming(FactoryPressureSession session,string acquisition,string stage,Stopwatch elapsed,int rounds=-1)
        {
            // Diagnostic elapsed time includes async scheduling and any named
            // operator wait. It never grants success, changes ACKs or caches data.
            try{diagnostics.Info("PressurePostProcessTiming","Trial="+session.Trial.Id+"; acquisition="+acquisition+
                "; stage="+stage+"; elapsedMs="+elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)+
                "; cumulativeRounds="+rounds.ToString(CultureInfo.InvariantCulture));}catch{}
        }
        void ReviewCurrentRoundCalibrationCharge(Control page,FactoryPressureSession session,Control review,double[,] matrix,string savedRecord,string acquisitionId)
        {
            // Input is the fresh extraction, before any manual classifications.
            // A cumulative spectrum is filtered by its current record identity.
            if(!IsCurrentPressureSession(page,session)||LateBound.ReadString(page,"LocationAcquisitionId")!=acquisitionId)return;
            try {
                var range=page.GetType().GetProperty("CapturedPicoRangeIndex");
                var method=review.GetType().GetMethod("GetPressureCalibrationChargeReview");
                if(range==null||method==null)throw new InvalidDataException("当前采集的校准量程复核接口不匹配。");
                int actualRange=(int)range.GetValue(page,null);
                if(actualRange<0||actualRange>10)throw new InvalidDataException("本轮真实采集量程无法确认。");
                object result=method.Invoke(review,new object[]{matrix,savedRecord,actualRange});
                if(LateBound.ReadString(result,"Schema")!="HVX.Pressure.CalibrationChargeReview/V1")throw new InvalidDataException("本轮校准量程复核结果无法确认。");
                int count=Convert.ToInt32(result.GetType().GetProperty("IndependentPulseCount").GetValue(result,null),CultureInfo.InvariantCulture);
                diagnostics.Info("PressureCalibrationChargeReview","acquisition="+acquisitionId+"; captureRange="+actualRange+"; independentPulsesAbove200nC="+count);
                if(LateBound.ReadBoolean(result,false,"ShouldRemind")&&session.CalibrationChargeReminders.Add(acquisitionId))
                    MessageBox.Show(page.FindForm(),"请检查校准源是否存在低电量或校准档位混淆。","校准核对",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }catch(Exception error){
                // A supplementary reminder is not a quality/safety permission.
                diagnostics.Error("PressureCalibrationChargeReview","本轮补充校准核对未完成；未改变原采集质量或高压许可。",error);
            }
        }
        internal Func<string,bool> PressureFixtureChoiceForTests;
        Task<bool> ConfirmPressureFixtureAsync(Control page,FactoryPressureSession session,Control review,double[,] matrix,string savedRecord,string acquisitionId,string metadataHash,string sourceHash)
        {return ConfirmSavedPressureFixtureAsync(page,session,review,matrix,savedRecord,acquisitionId,metadataHash,sourceHash,null);}

        async Task<bool> ConfirmSavedPressureFixtureAsync(Control page,FactoryPressureSession session,Control review,double[,] matrix,string savedRecord,string acquisitionId,string metadataHash,string sourceHash,PressureFixtureEvidence[] savedEvidence)
        {return await ConfirmSavedPressureFixtureAsync(page,session,review,matrix,savedRecord,acquisitionId,metadataHash,sourceHash,savedEvidence,null);}
        async Task<bool> ConfirmSavedPressureFixtureAsync(Control page,FactoryPressureSession session,Control review,double[,] matrix,string savedRecord,string acquisitionId,string metadataHash,string sourceHash,PressureFixtureEvidence[] savedEvidence,Func<bool> currentAnalysis)
        {
            Action current=()=>{
                if(currentAnalysis!=null&&!currentAnalysis()||(savedEvidence==null?!IsCurrentPressureSession(page,session)||LateBound.ReadString(page,"LocationAcquisitionId")!=acquisitionId:!IsCurrentPressureAnalysisTrial(page,session.Trial))||
                    LateBound.ReadBoolean(page,true,"HasActiveHardwareSession")||LateBound.ReadBoolean(page,false,"IsBusy")||LateBound.ReadBoolean(page,false,"ActionPending")||PressureFixtureJournal.MetadataHash(session.Project)!=metadataHash||PressureFixtureJournal.Hash(savedRecord)!=sourceHash)
                    throw new InvalidOperationException("采集轮次或项目参数已改变，后续轮次未继续。");
            };
            current();
            var method=review.GetType().GetMethod("GetPressureTerminationEvidenceAsync");
            if(method==null)throw new InvalidDataException("端部分析暂不可用。");
            var endpoints=savedEvidence==null?await (Task<double[,]>)method.Invoke(review,new object[]{matrix,savedRecord}):null;
            current();
            var evidence=savedEvidence??PressureFixtureJournal.Select(session.Project,endpoints);
            if(evidence.Length==0)return true;
            // No dialog can run while the acquisition owns the hardware. A
            // decline stops only the software queue, not a claimed discharge.
            bool chinese=LateBound.ReadString(settings,"LanguageCode").StartsWith("zh",StringComparison.OrdinalIgnoreCase);
            string details=String.Join(Environment.NewLine,evidence.Select(e=>String.Format(CultureInfo.InvariantCulture,
                chinese?"{0}相{1}：{2:0.###} nC，{3}":"Phase {0}, {1}: {2:0.###} nC, {3}","ABC"[e.Phase-1],
                e.Component==2?(chinese?"始端":"start termination"):(chinese?"末端":"remote termination"),e.ChargePc/1000,e.TerminalType)));
            string prompt=chinese?"本轮分析发现高压插拔头端部放电超过 10 nC。\r\n"+details+
                "\r\n\r\n后续轮次正在等待确认。请先按现场规程独立确认设备已放电，再检查工装。\r\n是否已安装所需工装，并选择继续后续试验？\r\n选择“否”将停止后续轮次；本轮数据保留。该选择不代表软件已验证工装或放电安全。":
                "This round contains discharge above 10 nC at a high-voltage separable termination.\r\n"+details+
                "\r\n\r\nSubsequent rounds await your decision. Independently verify discharge safety under site procedures before inspecting the fixture.\r\nHave you installed the required fixture and chosen to continue?\r\nNo stops subsequent rounds and retains this round. This statement does not verify fixture or discharge safety.";
            if(savedEvidence!=null)prompt+=chinese?
                "\r\n\r\n确认后如需继续，请点击“开始加压”重新开始后续试验；本次确认不会自动加压。":
                "\r\n\r\nTo continue after confirming, click Start pressure to begin the subsequent test. This confirmation never starts pressure automatically.";
            bool accepted=PressureFixtureChoiceForTests!=null?PressureFixtureChoiceForTests(prompt):MessageBox.Show(page.FindForm(),prompt,chinese?"端部工装确认":"Termination fixture confirmation",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
            current();
            string root=Path.GetFullPath(session.Project).TrimEnd(Path.DirectorySeparatorChar),source=Path.GetFullPath(savedRecord);
            if(!source.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("本轮记录不属于当前项目。");
            string journal=PressureFixtureJournal.Save(root,new PressureFixtureDecision{
                Trial=session.Trial.Id,Acquisition=acquisitionId,SourceRecord=source.Substring(root.Length+1),SourceSha256=sourceHash,
                MetadataSha256=metadataHash,Evidence=evidence,Choice=accepted?"FixtureConfirmedContinue":"StopQueue"});
            diagnostics.Info("PressureFixture","Project="+root+"; trial="+session.Trial.Id+"; acquisition="+acquisitionId+"; operator chose continue="+accepted+"; journal="+journal);
            if(!accepted){session.RevokeBatch();TryInvoke(page,"InterruptPressureContinuation",null);}
            return accepted;
        }
        void ApplyPressureAnalysisSettings(Control review,AppSettings snapshot)
        {
            string model=DefectModelCatalog.Resolve(snapshot.DefectModelKind,snapshot.TaskMinus3.ModelLibraryPath,snapshot.CompetitionModelPath,modelRoot);
            ApplyDataAnalysisSettings(review,model,ReadAnalysisQuadrants(snapshot),snapshot);
            TrySet(review,"NoiseSimilarityTolerancePercent",snapshot.NoiseSimilarityTolerancePercent);
            TrySet(review,"EnableAiFeatures",snapshot.EnableAiFeatures);
            TrySet(review,"EnableDefectRecognition",snapshot.EnableDefectRecognition);
        }
    }
}
