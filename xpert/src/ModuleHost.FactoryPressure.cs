using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.CalibrationStorage;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        readonly Dictionary<Control,FactoryPressureSession> factoryPressureSessions=new Dictionary<Control,FactoryPressureSession>();
        readonly List<Task> pressureSessionClosures=new List<Task>();
        readonly HashSet<Control> pressurePagesExiting=new HashSet<Control>();
        readonly Dictionary<Control,List<Task>> pressurePagePreparations=new Dictionary<Control,List<Task>>();
        Control owtsFactoryPressPage;
        internal string PressureEmergencyStage{get;private set;}
        internal DateTime PressureEmergencyCapturedUtc{get;private set;}
        internal event Action PressureEmergencyStatusChanged;
        internal bool HasForegroundPressureDeviceOwner
        {get{return owtsFactoryPressPage!=null||pressurePagesExiting.Count!=0||pressureSessionClosures.Exists(t=>t.Status!=TaskStatus.RanToCompletion)||
            new List<KeyValuePair<Control,FactoryPressureSession>>(factoryPressureSessions).Exists(p=>p.Key.Visible&&p.Value.OwnsDeviceSession);}}
        internal void ObservePressureEmergency(FactoryPressureSession session,string identity,DateTime identifiedUtc,string stage,DateTime utc)
        {
            if(disposed||!factoryPressureSessions.ContainsValue(session)||state.Hardware.CheckedUtc!=identifiedUtc||
                DeviceSetupStatus.ActualIdentity(state.Hardware)!=identity||utc.Kind!=DateTimeKind.Utc||utc<=PressureEmergencyCapturedUtc)return;
            PressureEmergencyStage=stage;PressureEmergencyCapturedUtc=utc;
            var changed=PressureEmergencyStatusChanged;if(changed!=null)changed();
        }
        void ClearPressureEmergencyObservation()
        {
            PressureEmergencyStage=null;PressureEmergencyCapturedUtc=DateTime.MinValue;
            var changed=PressureEmergencyStatusChanged;if(changed!=null)changed();
        }
        internal bool HasPressureDeviceOwner
        {
            get {
                if(owtsFactoryPressPage!=null||pressurePagesExiting.Count!=0)return true;
                pressureSessionClosures.RemoveAll(t=>t.Status==TaskStatus.RanToCompletion);
                if(pressureSessionClosures.Count!=0)return true; // pending/failed cleanup still owns the device
                foreach(var session in factoryPressureSessions.Values)if(session.OwnsDeviceSession)return true;
                return false;
            }
        }
        void RetirePressureSession(FactoryPressureSession session)
        {
            session.Dispose();
            pressureSessionClosures.Add(session.CloseAsync());
        }
        // Historical API retained for old test consumers only. Navigation now
        // awaits ClosePressurePageAsync; hiding a page must not keep its owner.
        internal bool RetainPressurePageForHome(Control page)
        {
            return false;
        }
        internal void BeginPressurePageExit(Control page)
        {
            // Invalidate preparation before any await. A calibration lookup
            // that was already running may finish, but cannot install a new owner.
            ++pressurePreparationSequence;
            if(page!=null)pressurePagesExiting.Add(page);
            FactoryPressureSession session;
            if(page!=null&&factoryPressureSessions.TryGetValue(page,out session))session.BeginPageExit();
            if(page==null||page.IsDisposed)return;
            TryInvoke(page,"RequestCurrentPressureStop",null);
            TryInvoke(page,"RequestFinishCurrentRound",null);
            TryInvoke(page,"InterruptPressureContinuation",null);
            TryInvoke(page,"RequestHardwareStop",null);
            // The control's existing hide handler cancels input initialization.
            // Keep it undisposed until its capture/save owner has acknowledged.
            page.Visible=false;
        }
        internal async Task ClosePressurePageAsync(Control page)
        {
            await JoinPressurePagePreparationsAsync(page);
            await AwaitRetiredPressureSessionsAsync();
            FactoryPressureSession session;
            if(page!=null&&factoryPressureSessions.TryGetValue(page,out session)){
                session.BeginPageExit();
                await session.CloseAsync();
                session.Dispose();factoryPressureSessions.Remove(page);
            }
            if(page!=null){pressurePagesExiting.Remove(page);pressurePagePreparations.Remove(page);}
            if(factoryPressureSessions.Count==0)ClearPressureEmergencyObservation();
        }
        internal async Task CloseRetainedPressureSessionsAsync()
        {
            await AwaitRetiredPressureSessionsAsync();
            foreach(var pair in new List<KeyValuePair<Control,FactoryPressureSession>>(factoryPressureSessions))
            {
                if(pair.Key.Visible&&!pair.Key.IsDisposed)continue;
                // Stop subscription and obtain the original owner's Close result
                // before any other module may open competing device tasks.
                await pair.Value.CloseAsync();
                pair.Value.Dispose();factoryPressureSessions.Remove(pair.Key);
                if(!pair.Key.IsDisposed)pair.Key.Dispose();
            }
            await AwaitRetiredPressureSessionsAsync();
            if(factoryPressureSessions.Count==0)ClearPressureEmergencyObservation();
        }
        async Task AwaitRetiredPressureSessionsAsync()
        {
            // A page may already have been disposed/removed, while its original
            // native owner is still closing. Do not lose that owner by replacing
            // the host or configuring another page. A failed Task stays owned.
            while(pressureSessionClosures.Count!=0){
                Task closing=pressureSessionClosures[0];
                await closing;
                pressureSessionClosures.Remove(closing);
            }
        }
        internal Task ConfigureTask39PressureAsync(Control control)
        {
            if(control!=null&&pressurePagesExiting.Contains(control))return Task.FromResult(false);
            Task preparation=ConfigureTask39PressureAfterEnvironmentAsync(control);
            if(control!=null&&!preparation.IsCompleted){
                List<Task> pending;
                if(!pressurePagePreparations.TryGetValue(control,out pending))pressurePagePreparations.Add(control,pending=new List<Task>());
                pending.RemoveAll(task=>task.IsCompleted);pending.Add(preparation);
            }
            return preparation;
        }
        internal async Task JoinPressurePagePreparationsAsync(Control page)
        {
            List<Task> pending;
            if(page==null||!pressurePagePreparations.TryGetValue(page,out pending))return;
            foreach(Task task in pending.ToArray()){
                try{await task;}
                catch(Exception error){diagnostics.Error("PressurePageExit","Input preparation ended with an error; actual owner Close is still required.",error);}
            }
            pending.RemoveAll(task=>task.IsCompleted);
        }
        async Task ConfigureTask39PressureAfterEnvironmentAsync(Control control)
        {
            using(await DrainEnvironmentForHardwareAsync(CancellationToken.None))
                await ConfigureTask39PressureCoreAsync(control);
        }
        async Task ConfigureTask39PressureCoreAsync(Control control)
        {
            if(disposed||control==null||control.IsDisposed||LateBound.ReadBoolean(control,true,"SimulationMode"))return;
            if(LateBound.ReadBoolean(control,false,"HasActiveHardwareSession")||LateBound.ReadBoolean(control,false,"IsPressureNoiseInitializing"))
                throw new InvalidOperationException("请等待当前采集或初始化结束。");
            FactoryPressureSession previous;
            if(factoryPressureSessions.TryGetValue(control,out previous)){
                // A retained output latch never triggers another initialization
                // capture, even if the original initialization was canceled.
                // An explicit successful Close/reconnect is required first.
                if(previous.CanResumePage&&(previous.EmergencyOutputLatched||LateBound.ReadBoolean(control,false,"PressureNoiseInitializationReady"))){
                    bool unchanged=true;
                    try{previous.ValidatePageContext();}
                    catch(OperationCanceledException){unchanged=false;}
                    if(unchanged){previous.StartMonitor();return;}
                    // An explicit phase/project/configuration transition must
                    // first obtain the original owner's real Close result.
                }
                await previous.CloseAsync();previous.Dispose();factoryPressureSessions.Remove(control);
            }
            await CloseRetainedPressureSessionsAsync();
            // Exit may have begun while an older owner was closing. Never
            // issue a new preparation generation or install a late owner.
            if(disposed||control.IsDisposed||pressurePagesExiting.Contains(control))return;
            long generation=++pressurePreparationSequence;
            TrySet(control,"ActionHandshakeEnabled",true);
            TryInvoke(control,"ClearZeroVoltageHardwareBackend",new object[0]);
            string project=state.ProjectPath,phase=LateBound.ReadString(control,"SelectedPhaseCode");
            var app=((AppSettings)settings).Clone();
            if (!DeviceParameterProfiles.IsConfigured(app, state.Hardware)) throw new InvalidOperationException("请先完成当前设备的参数配置。");
            if(Double.IsNaN(app.PdSampleRateHz)||Double.IsInfinity(app.PdSampleRateHz)||app.PdSampleRateHz<100000||app.PdSampleRateHz>1e9)
                throw new InvalidOperationException("局放采样率须至少为100 kS/s，才能使用10 kHz高通。请修改采样设置后重新开始。");
            string identity=DeviceSetupStatus.ActualIdentity(state.Hardware);
            string serial=identity.Length>0?identity.Split('|')[1]:app.Hardware.AcquisitionSerial;
            if(!DeviceAssetCatalog.IsPicoSerial(serial))serial=app.Hardware.ControllerSerial;
            string artSerial=identity.Length>0?identity.Split('|')[2]:String.Empty;
            var calibration=await Task.Run(()=>PressureCalibrationSelection.Find(project,phase,app.TaskMinus3.UseFirstArrivalTiming,serial,artSerial));
            if(disposed||control.IsDisposed||generation!=pressurePreparationSequence||state.ProjectPath!=project||LateBound.ReadString(control,"SelectedPhaseCode")!=phase)return;
            if(calibration==null){TryInvoke(control,"ClearDischargeCalibration",new object[0]);throw new InvalidDataException("当前项目没有与相别、设备及计时方式匹配的已保存校准。");}
            string source=calibration.Source;
            DateTimeOffset utc=calibration.SourceUtc;
            var setCalibration=control.GetType().GetMethod("SetDischargeCalibrationWithEvidence");
            if(setCalibration==null)throw new InvalidDataException("加压组件版本不匹配，无法传递校准来源，请修复安装。");
            setCalibration.Invoke(control,new object[]{calibration.CoefficientPcPerV,source,utc,phase,false,calibration.ChargePc,calibration.SourceHash});
            if(!LateBound.ReadBoolean(control,false,"IsDischargeCalibrated"))throw new InvalidOperationException("校准系数传递失败。");
            if(identity.Length==0)throw new InvalidOperationException("校准已载入；设备尚未连接。");
            if(FactoryPressOwtsHost.IsRequested()){
                await RunOwtsFactoryPressAsync(control,project,app,identity,calibration);
                return;
            }
            string worker=Path.Combine(modulesRoot,"PressureRuntime","Xiongxin.Pressure.Runtime.exe");
            var assembly=Assembly.LoadFrom(worker);var inputType=assembly.GetType("Xiongxin.ZeroBench.BenchSettings",true);
            object input=Activator.CreateInstance(inputType);
            Action<string,object> set=(name,value)=>inputType.GetField(name).SetValue(input,value);
            set("RealHardware",true);set("ArtSerial",identity.Split('|')[2]);set("PicoSerial",identity.Split('|')[1]);
            set("RequestedPicoRateHz",app.PdSampleRateHz);set("ResolutionBits",app.PdSampleBits);
            double captureDuration=HVX.Acquisition.PicoCaptureCadence.PressureDuration(app.OscillatingPdCaptureDurationMs,app.PdSampleRateHz);
            set("CaptureDurationMs",captureDuration);set("DividerVPerV",app.DividerRatio);
            diagnostics.Info("CaptureBudget","requestedMs="+app.OscillatingPdCaptureDurationMs.ToString("R",CultureInfo.InvariantCulture)+"; actualRequestedMs="+captureDuration.ToString("R",CultureInfo.InvariantCulture)+"; unchangedRateHz="+app.PdSampleRateHz.ToString("R",CultureInfo.InvariantCulture));
            set("ExternalTriggerVolts",app.DeviceTriggerVolts);
            set("AoStepV",app.PressureRampStepV);set("LoopDelayMs",app.PressureRampIntervalMs);
            set("InductanceH",app.InductanceHenry);set("ResistanceOhms",app.InductorDcResistanceOhm);
            // Enable waveform-derived diagnostics by default. This legacy
            // feature flag is not evidence of wiring or high-voltage permission.
            set("OscillationCircuitConnected",true);
            int range=calibration.RecordedRangeIndex??Convert.ToInt32(control.GetType().GetProperty("SelectedPicoRangeCode").GetValue(control,null),CultureInfo.InvariantCulture);
            if(range<0||range>10)throw new InvalidDataException("请选择有效的局放输入量程。");
            set("PdCalibrationPcPerV",calibration.CoefficientPcPerV);set("RangeIndex",range);
            var bridge=assembly.GetType("Xiongxin.FactoryTest.UnifiedPressureBridge",true);
            bool batchCapable=bridge.GetMethod("CaptureBatchAsync")!=null&&bridge.GetMethod("CloseSessionAsync")!=null;
            if(batchCapable)bridge.GetMethod("AttachLifetime").Invoke(null,new object[]{control.FindForm()});
            set("PicoCoupling",0);set("AutomaticSharedTrigger",false);set("ArmDelayMs",500);set("TriggerPulseMs",1000);
            inputType.GetMethod("Validate").Invoke(input,null);
            string inputXml=Task39InputContract.Serialize(input);
            // A distinct method keeps old reflection consumers unambiguous. If
            // the runtime lacks the new contract, fail rather than ignore settings.
            var configureFine=bridge.GetMethod("CreateConfigurationWithoutResidual");
            if(configureFine==null)throw new InvalidDataException("加压组件版本不匹配，请修复安装后重试。");
            string configuration=(string)configureFine.Invoke(null,new object[]{inputXml,app.PressureRatio,app.MaximumVoltageKv,
                app.LocationSaveCycleCount,app.EnableFinePressureRamp,app.FinePressureThresholdKv,
                PressureVoltageFeedbackCode(app)});
            Action validate=()=>ReadPressureUi(control,()=>{
                if(disposed||control.IsDisposed||generation!=pressurePreparationSequence||state.ProjectPath!=project||
                    !DeviceParameterProfiles.IsConfigured((AppSettings)settings, state.Hardware)||
                    LateBound.ReadString(control,"SelectedPhaseCode")!=phase||DeviceSetupStatus.ActualIdentity(state.Hardware)!=identity||
                    DeviceSetupStatus.ParameterHash((AppSettings)settings)!=DeviceSetupStatus.ParameterHash(app)||
                    ((AppSettings)settings).LocationSaveCycleCount!=app.LocationSaveCycleCount)throw new OperationCanceledException("项目、相别、设备或参数已改变。");
                return true;
            });
            var trial=GetPressureAnalysisTrial(control,project);
            var session=new FactoryPressureSession(control,bridge,configuration,app,project,trial,validate,diagnostics,true);
            session.CaptureSerial=serial;
            factoryPressureSessions.Add(control,session);
            DateTime identifiedUtc=state.Hardware.CheckedUtc;
            session.ConnectionUnavailable=()=>MarkPressureConnectionUnavailable(control,session,identity,identifiedUtc);
            session.EmergencyObserved=(stage,sampleUtc)=>ObservePressureEmergency(session,identity,identifiedUtc,stage,sampleUtc);
            string hostCaptureSessionId=Guid.NewGuid().ToString("N");
            Func<string,double,CancellationToken,Task<string>> capture=async (xml,target,token)=>{
                bool operatorCanceled=false,emergencyStopped=false;
                long ownedActionSequence=0;
                string hostCaptureRequestId=Guid.NewGuid().ToString("N");
                try {
                token.ThrowIfCancellationRequested();validate();
                // Legacy calibration validation may hash original waveforms.
                // Preserve the evidence check, but never do that disk work in
                // the caller's UI thread before the first asynchronous wait.
                await Task.Run(()=>calibration.Revalidate(),token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();validate();
                string planFingerprint=PressureBatchPlanFingerprint(control);
                long requestSequence=ReadPressureUi(control,()=>Convert.ToInt64(control.GetType().GetProperty("PressureRequestSequence").GetValue(control,null),CultureInfo.InvariantCulture));
                long actionSequence=ReadPressureUi(control,()=>Convert.ToInt64(control.GetType().GetProperty("ActionSequence").GetValue(control,null),CultureInfo.InvariantCulture));
                // Freeze the input-only purpose before any await. The bridge
                // selector and its DI callback must belong to the same request,
                // not to whatever mode a later UI update happens to display.
                bool manualZeroInput=target==0&&ReadPressureUi(control,()=>LateBound.ReadBoolean(control,false,"AdvancedMode"));
                ownedActionSequence=actionSequence;
                if(!ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("TryConsumePressureCaptureIntent").Invoke(control,new object[]{requestSequence,target})))
                    throw new InvalidOperationException("本轮没有有效的开始操作，请核对当前项目和目标后重新点击开始。");
                var batch=ReadPressureUi(control,()=>session.Batch.Prepare(batchCapable&&target>0&&!LateBound.ReadBoolean(control,true,"AdvancedMode")&&LateBound.ReadString(control,"PressureProgramModeCode")=="PLAN",LateBound.ReadBoolean(control,false,"PressureRequestIsAutomaticContinuation"),
                    requestSequence,Convert.ToInt32(control.GetType().GetProperty("SelectedPlanStepIndex").GetValue(control,null)),
                    Convert.ToInt32(control.GetType().GetProperty("CurrentPlanRepetition").GetValue(control,null)),
                    Convert.ToInt32(control.GetType().GetProperty("CurrentPlanRepetitionTotal").GetValue(control,null)),target,planFingerprint));
                Action validateBatch=()=>{validate();if(planFingerprint!=PressureBatchPlanFingerprint(control)||
                    !ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("IsPressureOperatorStartIntentCurrent").Invoke(control,new object[]{requestSequence,target}))||
                    requestSequence!=ReadPressureUi(control,()=>Convert.ToInt64(control.GetType().GetProperty("PressureRequestSequence").GetValue(control,null))))
                    throw new OperationCanceledException("加压计划或请求已改变，原批次作废。");
                    if(batch.Automatic)session.Batch.RequireAuthorized(batch);};
                bool countdownCompleted=await ReadPressureUi(control,()=>(Task<bool>)control.GetType().GetMethod("RequestAutomaticCaptureCountdownAsync")
                    .Invoke(control,new object[]{token}));
                if(!countdownCompleted){
                    operatorCanceled=ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("WasPressurePreparationCanceled").Invoke(control,new object[]{actionSequence}));
                    throw new OperationCanceledException(operatorCanceled?"操作人员已取消本轮等待，采集尚未启动。":"本轮准备条件已失效，采集尚未启动；请核对设备状态和当前项目。");
                }
                // The actual start-button intent above is the operator's
                // confirmation. Hardware checks still run independently.
                bool confirmed=!batch.Continuation;
                if(batch.Automatic)
                {
                    if(!batch.Continuation)
                    {
                        session.Batch.Authorize(batch);
                    }
                    session.Batch.RequireAuthorized(batch);
                    // Do not manufacture an independent discharge confirmation
                    // for a continuation. Metadata is checked by the server.
                    confirmed=!batch.Continuation;
                }
                token.ThrowIfCancellationRequested();validateBatch();
                diagnostics.Info("PressureStart","Project="+project+"; phase="+phase+"; targetKv="+target.ToString("R",CultureInfo.InvariantCulture)+
                    "; batch="+batch.Id+"; round="+batch.Round+"/"+batch.Count+"; continuation="+batch.Continuation);
                // Cooldown precedes monitor shutdown; emergency feedback stays
                // live while waiting. No delay is inserted inside HV control.
                using(var cadence=await HVX.Acquisition.PicoCaptureCadence.Shared.EnterAsync(serial,token,
                    text=>diagnostics.Info("CaptureCadence",text),null,
                    ms=>TryInvoke(control,"ReportCaptureCooldown",new object[]{ms})).ConfigureAwait(false))
                {
                token.ThrowIfCancellationRequested();validateBatch();
                // Keep the existing monitor subscription alive while a healthy
                // DI read is pending. No reservation/worker/output starts here.
                var pendingRead=control.GetType().GetMethod("WaitForPendingP04ReadAsync");
                if(pendingRead!=null&&!await ReadPressureUi(control,()=>(Task<bool>)pendingRead.Invoke(control,new object[]{token})).ConfigureAwait(false))
                    throw new OperationCanceledException("急停回读未就绪，本轮尚未开始。",token);
                token.ThrowIfCancellationRequested();validateBatch();
                await session.StopMonitor().ConfigureAwait(false);
                bool resumeMonitor=true;
                try {
                    string parent=Path.Combine(project,"Acquisition","Pressure");Task39InputContract.RequireNoLinks(parent);
                    Directory.CreateDirectory(parent);
                    string directory=Path.Combine(parent,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N"));
                    // Confirmation and monitor shutdown both await external work.
                    // Recheck at the actual capture boundary, not only beforehand
                    // or after a round that may already have used stale calibration.
                    TryInvoke(control,"SetPressureState",new object[]{"RUNNING",Double.NaN});
                    var task=DispatchPreparedPressureCapture(control,token,
                        ()=>calibration.RevalidateForCapture(validateBatch,token),
                        ()=>cadence.BeginCapture(),
                        ()=>cadence.Released(0),
                        ()=>(Task<string>)bridge.GetMethod("CaptureIdentifiedAsync").Invoke(null,new object[]{configuration,xml,target,directory,
                            hostCaptureSessionId,hostCaptureRequestId,manualZeroInput,batch.Automatic,batch.Id,batch.Round,batch.Count,
                            confirmed,batch.Continuation,session.CaptureProgressForRound(actionSequence,requestSequence,manualZeroInput),token}));
                    string result=await task.ConfigureAwait(false);
                    // A successful bridge result follows native cleanup and the
                    // supervised worker's exit. A fault is not release evidence.
                    cadence.Released(captureDuration);validateBatch();calibration.Revalidate();
                    if(batch.Automatic)session.Batch.Complete(batch);
                    return result;
                } catch(Exception captureError) {
                    if(IsVerifiedRequestRejection(captureError,bridge.Assembly,hostCaptureSessionId,hostCaptureRequestId)) {
                        cadence.Released(0);
                        TryInvoke(control,"ReportPressureRequestRejected",new object[]{actionSequence});
                        session.RevokeBatch();
                        diagnostics.Info("PressureRequestRejected","当前请求在设备调用前被拒绝；原会话保留，本轮未开始，不自动续轮。");
                    }
                    else if(IsVerifiedPreNativeCancellation(captureError,bridge.Assembly)) {
                        // Only this round's verified pre-native result can
                        // abandon its own active reservation. It never clears
                        // an earlier BLOCKED file or claims SDK cleanup.
                        cadence.Released(0);
                        TryInvoke(control,"ReportPressureCaptureNeverStarted",new object[]{actionSequence});
                        operatorCanceled=ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("WasPressurePreparationCanceled").Invoke(control,new object[]{actionSequence}));
                        diagnostics.Info("PressureNotStartedEvidence","已核验本轮启动取消证明：工作进程未进入原生接口；只释放本轮未使用的采集预留。");
                    }
                    else if(target==0&&IsVerifiedInputStop(captureError,bridge.Assembly)) {
                        // Exact accepted request/session receipt, not an error
                        // message or process exit. Keep full finite-segment cooldown.
                        cadence.Released(captureDuration);
                        TryInvoke(control,"ReportPressureInputStopConfirmed",new object[]{actionSequence});
                        operatorCanceled=ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("WasPressureOperatorInputStopped").Invoke(control,new object[]{actionSequence}));
                        diagnostics.Info("PressureInputStopEvidence","本轮0 kV AI/Pico停止与缓冲释放已核验；仅释放本轮采集租约，会话保持只读，不代表放电。批次撤销，不自动续轮。");
                    }
                    else if(IsVerifiedEmergencyRoundStop(captureError,bridge.Assembly)) {
                        // The accepted request's native shutdown/round-release
                        // receipt retains this same healthy DI display owner.
                        // It is neither a disconnected device nor an HV permit.
                        cadence.Released(captureDuration);
                        object uiReceipt;string uiReceiptError;
                        bool uiReceiptInvoked=LateBound.TryInvoke(control,"ReportPressureEmergencyRoundStopConfirmed",
                            new object[]{actionSequence},out uiReceipt,out uiReceiptError);
                        bool uiReceiptAccepted=uiReceiptInvoked&&uiReceipt is bool&&(bool)uiReceipt;
                        emergencyStopped=true;
                        session.RevokeBatch();
                        diagnostics.Info("PressureEmergencyStopEvidence","急停停止回执已核验：HV OFF、轮次停采及缓冲释放返回，同一只读DI会话保留；输出仍锁存，不自动恢复或续轮，不代表已放电。");
                        if(!uiReceiptAccepted)diagnostics.Warning("PressureEmergencyStopUi",
                            "停止回执已核验，但当前界面动作未接受该回执；未修改其他轮次界面。"+(uiReceiptInvoked?String.Empty:" "+uiReceiptError));
                    }
                    else if(IsVerifiedHealthyRoundStop(captureError,bridge.Assembly)) {
                        cadence.Released(captureDuration);
                        TryInvoke(control,"ReportPressureRoundStopConfirmed",new object[]{actionSequence});
                        operatorCanceled=ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("WasPressureOperatorRoundStopped").Invoke(control,new object[]{actionSequence}));
                        diagnostics.Info("PressureRoundStopEvidence","本轮输入快照、停止请求、关断返回、ART轮次结束、Pico停采及缓冲释放、会话保持已逐项核验；仅停止当前轮，完整冷却保留，不代表放电，不自动续轮。");
                    }
                    else {
                        // A failed resident worker may have exited. Never open a
                        // replacement monitor/output session from a finally block.
                        resumeMonitor=false;
                        string outcomeStatus=PressureCaptureFailureStatus(captureError,bridge.Assembly);
                        TryInvoke(control,"ReportPressureCaptureFailureStatus",new object[]{actionSequence,outcomeStatus});
                        session.RequireExplicitReconnectAfterCaptureFailure(outcomeStatus);
                        if(IsVerifiedFailedRoundRelease(captureError,bridge.Assembly)) {
                            cadence.Released(captureDuration);
                            diagnostics.Info("PressureFailureCleanupEvidence","本轮原生停止、缓冲释放、进程退出及采集租约释放已核验。仅解除本轮采集占用，保留原失败及冷却，不代表已放电，不自动重连或续轮。");
                        }
                    }
                    throw;
                } finally { if(resumeMonitor&&IsCurrentPressureSession(control,session))session.StartMonitor(); }
                }
                } catch(Exception error) {session.RevokeBatch();
                    if(error is OperationCanceledException&&!operatorCanceled&&ownedActionSequence>0)
                        try {operatorCanceled=ReadPressureUi(control,()=>(bool)control.GetType().GetMethod("WasPressurePreparationCanceled").Invoke(control,new object[]{ownedActionSequence}));}
                        catch(InvalidOperationException){ /* Closed UI is not evidence of a harmless operator cancellation. */ }
                        catch(OperationCanceledException){ /* Preserve the original failure if the page has already closed. */ }
                    if(emergencyStopped&&error is OperationCanceledException)diagnostics.Info("PressureEmergencyStopped","本轮由真实急停终止；保留独立DI显示和原数据，不自动继续。");
                    else if(operatorCanceled&&error is OperationCanceledException)diagnostics.Info("PressureOperatorStopped","操作人员停止本轮；已核验未进入设备接口或本轮独立停止回执，不会自动继续。");
                    else diagnostics.Error("PressureCaptureFailed","本轮失败，未自动重试；详见项目 Acquisition/Pressure 与运行日志。",error);
                    throw;}
            };
            control.GetType().GetMethod("ConfigurePressureHardwareBackend").Invoke(control,new object[]{inputXml,capture});
            TrySet(control,"PressureBatchCaptureEnabled",batchCapable);
            Func<string,string,double,string> rangeReview=(directory,xml,target)=>{
                validate();
                return (string)bridge.GetMethod("ReviewCapturedRound").Invoke(null,new object[]{directory,xml,target});
            };
            control.GetType().GetMethod("ConfigurePressureRangeReview").Invoke(control,new object[]{rangeReview});
            ConnectPressureRoundSaving(control,session,calibration.VelocityMPerUs);
            session.StartMonitor();
            // Initialization is a separate, explicit zero-voltage INPUT request.
            // Its 20 ms settings do not alter the normal pressure capture budget,
            // user AO step, plan, countdown or round-to-round range policy.
            set("CaptureDurationMs",20d);
            set("RangeIndex",Convert.ToInt32(control.GetType().GetProperty("SelectedPicoRangeCode").GetValue(control,null),CultureInfo.InvariantCulture));
            string initializationXml=Task39InputContract.Serialize(input);
            string initializationConfiguration=(string)configureFine.Invoke(null,new object[]{initializationXml,app.PressureRatio,app.MaximumVoltageKv,
                app.LocationSaveCycleCount,app.EnableFinePressureRamp,app.FinePressureThresholdKv,PressureVoltageFeedbackCode(app)});
            Func<string,CancellationToken,Task<string>> initializeInput=async (xml,token)=>{
                token.ThrowIfCancellationRequested();validate();
                await Task.Run(()=>calibration.Revalidate(),token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();validate();
                var stage=System.Diagnostics.Stopwatch.StartNew();
                using(var cadence=await HVX.Acquisition.PicoCaptureCadence.Shared.EnterAsync(serial,token,
                    text=>diagnostics.Info("PressureInitializationCadence",text)).ConfigureAwait(false)) {
                    token.ThrowIfCancellationRequested();validate();calibration.Revalidate();
                    // During cooldown the independent DI monitor remains live.
                    // The initial monitor was opened INPUT-ONLY as well. Stop
                    // only its subscription, so this capture keeps that owner
                    // and cannot implicitly replace a normal HV capability.
                    await session.StopMonitor().ConfigureAwait(false);
                    bool resume=true;
                    try {
                        token.ThrowIfCancellationRequested();validate();calibration.Revalidate();
                        string parent=Path.Combine(project,"Acquisition","PressureInitialization");Task39InputContract.RequireNoLinks(parent);Directory.CreateDirectory(parent);
                        string directory=Path.Combine(parent,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N"));
                        cadence.BeginCapture();
                        string result=await ((Task<string>)bridge.GetMethod("CaptureManualZeroAsync").Invoke(null,new object[]{initializationConfiguration,xml,0d,directory,false,session.CreateScopedProgressSource(true),token})).ConfigureAwait(false);
                        cadence.Released(20);validate();calibration.Revalidate();
                        diagnostics.Info("PressureInitializationInput","segments=1; requestedMs=20; nativeAndWaitElapsedMs="+stage.ElapsedMilliseconds+"; source="+result+"; no AO/DO requested");
                        return result;
                    }catch(Exception error) {
                        if(IsVerifiedPreNativeCancellation(error,bridge.Assembly))cadence.Released(0);
                        else if(IsVerifiedInputStop(error,bridge.Assembly)||IsVerifiedFailedRoundRelease(error,bridge.Assembly))cadence.Released(20);
                        else {resume=false;session.RequireExplicitReconnectAfterCaptureFailure(PressureCaptureFailureStatus(error,bridge.Assembly));}
                        throw;
                    }finally {if(resume&&IsCurrentPressureSession(control,session))session.StartMonitor();}
                }
            };
            Func<CancellationToken,Task> finishInitialization=async token=>{
                token.ThrowIfCancellationRequested();validate();calibration.Revalidate();
                await session.CompleteInitializationInputOwnerAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();validate();
            };
            var initialize=control.GetType().GetMethod("InitializePressureNoiseWithHandoffAsync");
            if(initialize==null)throw new InvalidDataException("加压组件缺少只读噪声初始化入口，请重新生成程序。");
            if(!await ((Task<bool>)initialize.Invoke(control,new object[]{initializationXml,initializeInput,finishInitialization,CancellationToken.None})))
                throw new InvalidOperationException("背景噪声初始化未完成；加压仍禁用。");
            diagnostics.Info("Pressure","Calibration source="+(calibration.IsLegacy?"OperatorConfirmedLegacy; historical device unknown":"LiveAcquisition")+"; record="+source+"; runtime="+worker);
        }

        // The FactoryPress stack owns ART/Pico for the whole dialog. The legacy
        // worker, its DI monitor and this page's capture backend stay unconfigured,
        // so the two stacks never hold the same hardware.
        async Task RunOwtsFactoryPressAsync(Control control,string project,AppSettings app,string identity,PressureCalibrationSelection calibration)
        {
            int range=calibration.RecordedRangeIndex??Convert.ToInt32(control.GetType().GetProperty("SelectedPicoRangeCode").GetValue(control,null),CultureInfo.InvariantCulture);
            if(range<0||range>10)throw new InvalidDataException("请选择有效的局放输入量程。");
            await Task.Run(()=>calibration.Revalidate());
            if(disposed||control.IsDisposed||pressurePagesExiting.Contains(control)||state.ProjectPath!=project||
                DeviceSetupStatus.ActualIdentity(state.Hardware)!=identity)return;
            if(HasPressureDeviceOwner)throw new InvalidOperationException("上一加压会话尚未释放设备，请稍后重试。");
            string parent=Path.Combine(project,"Acquisition","PressureOwtsFactory");Task39InputContract.RequireNoLinks(parent);
            Directory.CreateDirectory(parent);
            diagnostics.Info("PressureOwtsFactory","FactoryPress hardware stack requested ("+FactoryPressOwtsHost.EnableEnvVar+" / "+
                FactoryPressOwtsHost.EnableMarkerFile+"); legacy PressureRuntime not configured for this page; fakeLive="+FactoryPressOwtsHost.FakeLiveRequested+"; records="+parent);
            owtsFactoryPressPage=control;
            try{
                string summary=FactoryPressOwtsHost.Run(control.FindForm(),modulesRoot,identity.Split('|')[2],identity.Split('|')[1],
                    app.PressureRatio,app.DividerRatio,app.MaximumVoltageKv,range,parent,(code,text)=>diagnostics.Info(code,text));
                diagnostics.Info("PressureOwtsFactory","Session ended: "+summary);
            }finally{owtsFactoryPressPage=null;}
        }

        internal static string PressureVoltageFeedbackCode(AppSettings app)
        {
            if(app==null)throw new ArgumentNullException("app");
            switch(app.HvupVoltageSource){
                case HvupVoltageSource.Divider:return "DIV";
                case HvupVoltageSource.VMonitor:return "VMON";
                default:throw new InvalidDataException("电压反馈源无效，请选择分压器或直流源反馈。");
            }
        }
        static string PressureBatchPlanFingerprint(Control page)
        {
            return ReadPressureUi(page,()=>{
            var type=page.GetType();
            var voltages=(double[])type.GetMethod("GetPressurePlanVoltagesKv").Invoke(page,null);
            var repetitions=(int[])type.GetMethod("GetPressurePlanRepetitions").Invoke(page,null);
            return LateBound.ReadString(page,"PressureProgramModeCode")+"|"+LateBound.ReadBoolean(page,true,"AdvancedMode")+"|"+
                LateBound.ReadBoolean(page,false,"PlanVoltagesArePeak")+"|"+LateBound.ReadString(page,"SelectedPhaseCode")+"|"+
                String.Join(",",Array.ConvertAll(voltages,v=>v.ToString("R",CultureInfo.InvariantCulture)))+"|"+String.Join(",",repetitions);
            });
        }

        internal static bool IsVerifiedRequestRejection(Exception error,Assembly expectedWorkerAssembly,string session,string request)
        {
            while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryRequestRejectedException")return false;
            var notSubmitted=type.GetProperty("NativeRequestNotSubmitted");
            var sid=type.GetProperty("HostSessionId");var rid=type.GetProperty("HostRequestId");
            return notSubmitted!=null&&sid!=null&&rid!=null&&(bool)notSubmitted.GetValue(error,null)&&
                !String.IsNullOrEmpty(session)&&!String.IsNullOrEmpty(request)&&
                (string)sid.GetValue(error,null)==session&&(string)rid.GetValue(error,null)==request;
        }
        internal static bool IsVerifiedPreNativeCancellation(Exception error,Assembly expectedWorkerAssembly)
        {
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryNativeNotStartedException")return false;
            var proof=type.GetProperty("NativeNeverEnteredConfirmed");var code=type.GetProperty("LaunchEvidenceCode");
            if(proof==null||code==null||!(bool)proof.GetValue(error,null))return false;
            string value=(string)code.GetValue(error,null);
            return value=="ParentCanceledBeforeLaunch"||value=="VerifiedWrapperCanceledBeforeNative";
        }
        internal static bool IsVerifiedInputStop(Exception error,Assembly expectedWorkerAssembly)
        {
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryInputStoppedException")return false;
            var released=type.GetProperty("InputResourcesReleased");var code=type.GetProperty("StopEvidenceCode");
            return released!=null&&code!=null&&(bool)released.GetValue(error,null)&&
                (string)code.GetValue(error,null)=="VerifiedOwnedZeroInputStop";
        }

        internal static bool IsVerifiedFailedRoundRelease(Exception error,Assembly expectedWorkerAssembly)
        {
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryFailedRoundReleasedException")return false;
            var released=type.GetProperty("CaptureResourcesReleased");var code=type.GetProperty("ReleaseEvidenceCode");
            return released!=null&&code!=null&&(bool)released.GetValue(error,null)&&
                (string)code.GetValue(error,null)=="VerifiedOwnedFailedRoundNativeCleanup";
        }
        internal static bool IsVerifiedHealthyRoundStop(Exception error,Assembly expectedWorkerAssembly)
        {
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryHealthyRoundStoppedException")return false;
            var released=type.GetProperty("CaptureResourcesReleased");var code=type.GetProperty("StopEvidenceCode");
            return released!=null&&code!=null&&(bool)released.GetValue(error,null)&&
                (string)code.GetValue(error,null)=="VerifiedOwnedHealthyRoundStop";
        }
        internal static string PressureCaptureFailureStatus(Exception error,Assembly worker)
        {
            if(IsVerifiedFailedRoundRelease(error,worker))return "SessionEndedAfterFailedRelease";
            if(error!=null&&worker!=null&&error.GetType().Assembly==worker){
                if(error.GetType().FullName=="Xiongxin.FactoryTest.FactoryResidentRoundIncompleteException")return "StopReleaseUnconfirmed";
                if(error.GetType().FullName=="Xiongxin.FactoryTest.FactoryWorkerExitedException")return "WorkerSessionExited";
            }
            return "PresenceUnknown";
        }

        internal static bool IsVerifiedEmergencyRoundStop(Exception error,Assembly expectedWorkerAssembly)
        {
            if(error==null||expectedWorkerAssembly==null)return false;
            var type=error.GetType();
            if(type.Assembly!=expectedWorkerAssembly||type.FullName!="Xiongxin.FactoryTest.FactoryEmergencyRoundStoppedException")return false;
            var released=type.GetProperty("CaptureResourcesReleased");var latched=type.GetProperty("OutputLatched");var code=type.GetProperty("StopEvidenceCode");
            return released!=null&&latched!=null&&code!=null&&(bool)released.GetValue(error,null)&&
                (bool)latched.GetValue(error,null)&&(string)code.GetValue(error,null)=="VerifiedEmergencyRoundStopRetained";
        }

        internal static T ReadPressureUi<T>(Control page,Func<T> read)
        {
            if(page==null||page.IsDisposed||page.Disposing||!page.IsHandleCreated)
                throw new OperationCanceledException("加压界面已关闭，原批次作废。");
            if(page.InvokeRequired)return (T)page.Invoke(read);
            return read();
        }

        // No awaited or user-interactive work follows the preparation commit.
        // If the worker has not been invoked, abandoning its unused reservation
        // does not assert that a running device stopped. Once invoked, even a
        // synchronous failure is indeterminate and retains fail-closed cleanup.
        internal static Task<string> DispatchPreparedPressureCapture(Control control,CancellationToken token,
            Action revalidate,Action reserve,Action abandonUnusedReservation,Func<Task<string>> startWorker)
        {
            token.ThrowIfCancellationRequested();
            revalidate();
            token.ThrowIfCancellationRequested();
            reserve();
            bool invoked=false;
            try
            {
                token.ThrowIfCancellationRequested();
                bool accepted=(bool)control.GetType().GetMethod("TryBeginPreparedHardwareCapture")
                    .Invoke(control,new object[]{token});
                if(!accepted)throw new OperationCanceledException("本轮准备已取消，请重新确认后开始。");
                token.ThrowIfCancellationRequested();
                invoked=true;
                return startWorker();
            }
            catch
            {
                if(!invoked)abandonUnusedReservation();
                throw;
            }
        }

        internal void MarkPressureConnectionUnavailable(Control page,FactoryPressureSession session,string identity,DateTime identifiedUtc)
        {
            if(!IsCurrentPressureSession(page,session))return;
            var actual=state.Hardware;
            // A late callback from an old session must not invalidate a new
            // connection, even when the same physical serials were reselected.
            if(actual.CheckedUtc!=identifiedUtc||DeviceSetupStatus.ActualIdentity(actual)!=identity)return;
            actual.Connected=false;actual.StatusCode=session.ConnectionFailureStatus??"PresenceUnknown";
            actual.Message=actual.StatusCode=="SessionEndedAfterFailedRelease"?
                "本轮采集未完成；原控制会话已主动结束，需要重新连接。原始数据保留，不能自动继续。":
                actual.StatusCode=="StopReleaseUnconfirmed"?
                "本轮停止及释放尚未核验，保留当前会话等待处理；不能计为完成或自动继续。":
                actual.StatusCode=="WorkerSessionExited"?
                "控制工作进程已退出；清理状态待核验，请检查原始记录后重新连接。":
                "设备通信状态无法确认，请检查连接或其他占用程序后重新连接。";
            // Preserve the original identity check time: a failed input is not
            // a new identity inspection, nor proof of external ownership.
            state.SetHardware(actual);
            diagnostics.Warning("PressureConnection","Current pressure-session feedback failed; cached readiness invalidated. No automatic reconnect.");
        }
    }
    internal sealed class FactoryPressureSession:IDisposable
    {
        readonly Control control;readonly Type bridge;readonly string configuration;readonly Action validate;readonly Diagnostics log;
        readonly object gate=new object();CancellationTokenSource monitor;Task monitorTask=Task.CompletedTask;volatile bool disposed;
        internal readonly AppSettings Settings;
        internal readonly PressureAnalysisTrial Trial;
        internal readonly HashSet<string> CalibrationChargeReminders=new HashSet<string>(StringComparer.Ordinal);
        readonly List<IDisposable> postProcessSubscriptions=new List<IDisposable>();
        internal readonly FactoryPressureBatchContext Batch=new FactoryPressureBatchContext();
        Task closeTask;
        internal string CaptureSerial;
        string lastLoggedEmergencyStage;
        internal Action ConnectionUnavailable;
        internal string ConnectionFailureStatus="PresenceUnknown";
        internal Action<string,DateTime> EmergencyObserved;
        internal bool MonitorRestartSuppressed{get{return monitorRestartSuppressed;}}
        internal bool CanResumePage{get{return !disposed&&!monitorRestartSuppressed&&closeTask==null&&!control.IsDisposed;}}
        internal bool EmergencyOutputLatched{get{return emergencyOutputLatched;}}
        internal void ValidatePageContext(){validate();}
        volatile bool emergencyOutputLatched;
        int unavailableNotified;
        volatile bool monitorRestartSuppressed;
        volatile bool initializationInputOnly;
        volatile bool pageExitRequested;
        bool initializationHandoff;
        bool? monitorInputOnly;
        internal readonly string Project;
        internal readonly Action<string,string> Progress;
        readonly Action<long,bool,string,string> progressForSource;
        long progressGeneration;
        long displayedProgressSource;
        internal FactoryPressureSession(Control page,Type type,string config,AppSettings app,string project,PressureAnalysisTrial trial,Action check,Diagnostics diagnostics,bool initializeInputOnly=false)
        {
            control=page;bridge=type;configuration=config;Settings=app;Project=project;Trial=trial;validate=check;log=diagnostics;
            initializationInputOnly=initializeInputOnly;
            control.GetType().GetMethod("ResetP04FeedbackSession").Invoke(control,null);
            progressForSource=(source,manualZeroInput,stage,detail)=>{
                if(disposed||control.IsDisposed)return; // an old callback must not revoke a new bridge batch
                bool latchedEmergency=stage=="EMERGENCY_LATCHED_RELEASED"||stage=="EMERGENCY_LATCHED_ACTIVE";
                bool pendingEmergency=stage=="EMERGENCY_PENDING";
                bool emergency=pendingEmergency||latchedEmergency||stage=="EMERGENCY_ACTIVE"||stage=="EMERGENCY_RELEASED"||stage=="EMERGENCY_UNKNOWN";
                // Log every workflow/error stage, but not every identical DI heartbeat.
                if(!emergency || stage!=lastLoggedEmergencyStage){
                    if(emergency)lastLoggedEmergencyStage=stage;
                    log.Info("PressureWorker."+stage,detail);
                }
                if(stage=="FINAL_AI4_OBSERVATION")log.Info("PressureFinalAi4",detail);
                if(stage=="LINK_PICO_ERROR"||stage=="LINK_PICO_CLOSED"||stage=="PRESSURE_SESSION_UNAVAILABLE"){
                    RevokeBatch();
                    if(!disposed&&!control.IsDisposed&&control.IsHandleCreated&&Interlocked.Exchange(ref unavailableNotified,1)==0)
                        try{control.BeginInvoke((Action)(()=>{if(!disposed&&!control.IsDisposed&&ConnectionUnavailable!=null)ConnectionUnavailable();}));}
                        catch(InvalidOperationException){}
                }
                if(emergency){
                    DateTime utc;
                    if(!DateTime.TryParse((detail??"").Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out utc))utc=DateTime.MinValue;
                    if(pendingEmergency&&utc.Kind==DateTimeKind.Utc) {
                        var latest=control.GetType().GetProperty("P04CapturedUtc");
                        if(latest!=null&&utc<=(DateTime)latest.GetValue(control,null))return;
                    }
                    DateTime pendingUntil=DateTime.MinValue;
                    if(pendingEmergency&&!TryReadPendingDeadline(detail,out pendingUntil))pendingEmergency=false;
                    if(stage!="EMERGENCY_RELEASED"&&!pendingEmergency) {
                        // A read-only manual 0 kV round owns no AO/DO or optical
                        // output. Its physical LOW/pending DI is display data,
                        // not an instruction to stop AI/Pico. Still revoke any
                        // future HV authorization locally. Terminal transport
                        // errors and a latched output fault retain real Stop.
                        if(manualZeroInput&&!latchedEmergency)Batch.Revoke();
                        else RevokeBatch();
                    }
                    if(!disposed&&!control.IsDisposed&&control.IsHandleCreated){
                        // The method applies the safety bit before its own UI dispatch.
                        var latched=source==0?null:control.GetType().GetMethod("ReportLatchedP04StatusForSource");
                        if(pendingEmergency) {
                            var pending=source==0?null:control.GetType().GetMethod("ReportPendingP04StatusForSource");
                            if(pending!=null)pending.Invoke(control,new object[]{source,utc,pendingUntil});
                            else { RevokeBatch();control.GetType().GetMethod("ReportP04Status").Invoke(control,new object[]{false,false,utc}); }
                        } else if(latchedEmergency) {
                            emergencyOutputLatched=true;
                            if(latched!=null)latched.Invoke(control,new object[]{source,stage=="EMERGENCY_LATCHED_RELEASED",utc});
                            else control.GetType().GetMethod("ReportP04Status").Invoke(control,new object[]{false,false,utc});
                        } else {
                            var scoped=source==0?null:control.GetType().GetMethod("ReportP04StatusForSource");
                            bool known=stage=="EMERGENCY_ACTIVE"||stage=="EMERGENCY_RELEASED";
                            if(scoped!=null)scoped.Invoke(control,new object[]{source,known,stage=="EMERGENCY_RELEASED",utc});
                            else control.GetType().GetMethod("ReportP04Status").Invoke(control,new object[]{known,stage=="EMERGENCY_RELEASED",utc});
                        }
                        PublishEmergencyObservation(source,stage=="EMERGENCY_PENDING"&&!pendingEmergency?"EMERGENCY_UNKNOWN":stage,utc);
                        // A pending/unknown DI read denies electrical permission,
                        // but is not evidence that the transport disconnected.
                        // A terminal monitor failure reports session-unavailable
                        // separately; HOME must not tear down a recovering reader.
                    }
                }
            };
            Progress=(stage,detail)=>progressForSource(0,false,stage,detail);
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"FinishCurrentRoundRequested",Trial.CancelAnalysis));
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"FinishCurrentRoundRequested",RevokeBatch));
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"PhaseChanged",RevokeBatch));
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"VoltageSettingChanged",RefreshMonitorMode));
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"AdvancedModeChanged",RefreshMonitorMode));
            KeepPostProcessSubscription(ModuleAuditSubscription.TryCreate(control,"PostProcessStateChanged",()=>{
                string state=LateBound.ReadString(control,"PostProcessStateCode");
                if(state.IndexOf("FAILED",StringComparison.OrdinalIgnoreCase)>=0||state=="CANCELED"||
                    state=="ACQUISITION_INVALID"||state=="OVERRANGE_REJECTED")RevokeBatch();
            }));
            control.VisibleChanged+=VisibilityChanged;control.Disposed+=OnDisposed;
        }
        internal Action<string,string> CaptureProgress(long actionSequence,long requestSequence)
        {
            bool manualZeroInput=ModuleHost.ReadPressureUi(control,()=>LateBound.ReadBoolean(control,false,"AdvancedMode")&&
                Convert.ToDouble(control.GetType().GetProperty("CalculatedTargetPeakVoltageKv").GetValue(control,null))==0);
            return CaptureProgressForRound(actionSequence,requestSequence,manualZeroInput);
        }
        internal Action<string,string> CaptureProgressForRound(long actionSequence,long requestSequence,bool manualZeroInput)
        {
            bool? dividerOnly=PressureFeedbackDisplay.DividerOnly(configuration);
            var sourceProgress=CreateScopedProgressSource(manualZeroInput);
            return (stage,detail)=> {
                if(disposed||control.IsDisposed)return;
                if(Convert.ToInt64(control.GetType().GetProperty("ActionSequence").GetValue(control,null))!=actionSequence||
                    Convert.ToInt64(control.GetType().GetProperty("PressureRequestSequence").GetValue(control,null))!=requestSequence)return;
                sourceProgress(stage,detail);
                bool workflowStage=stage=="STEP_HV_UP_PASSED"||stage!=null&&stage.StartsWith("STEP_",StringComparison.Ordinal)&&stage.EndsWith("_RUNNING",StringComparison.Ordinal);
                if((stage!="FEEDBACK_TREND"&&!workflowStage)||!control.IsHandleCreated)return;
                double kv=Double.NaN;string source="";
                if(!workflowStage&&!PressureFeedbackDisplay.TryRead(detail,dividerOnly,out kv,out source))return;
                try { control.BeginInvoke((Action)(()=> {
                    if(disposed||control.IsDisposed||!control.Visible||
                        Convert.ToInt64(control.GetType().GetProperty("ActionSequence").GetValue(control,null))!=actionSequence||
                        Convert.ToInt64(control.GetType().GetProperty("PressureRequestSequence").GetValue(control,null))!=requestSequence)return;
                    if(workflowStage)control.GetType().GetMethod("ReportPressureWorkerStage").Invoke(control,new object[]{requestSequence,stage});
                    else control.GetType().GetMethod("ReportPressureVoltageProgress").Invoke(control,new object[]{requestSequence,kv,source});
                })); }catch(InvalidOperationException){}
            };
        }
        async void VisibilityChanged(object sender,EventArgs args)
        {
            try
            {
                if(control.Visible)
                {
                    Task closing;lock(gate)closing=closeTask;
                    if(closing!=null)await closing;
                    lock(gate)if(Object.ReferenceEquals(closeTask,closing))closeTask=null;
                    StartMonitor();
                }
                else {
                    // Hide cancels future outputs and the page's initialization,
                    // not the independent DI owner. Full close is explicit at
                    // mode handoff, disconnect or host/application disposal.
                    RevokeBatch();var interrupt=control.GetType().GetMethod("InterruptPressureContinuation");
                    if(interrupt!=null)interrupt.Invoke(control,null);
                }
            }
            catch(Exception error){log.Error("PressureSessionClose","设备会话关闭尚未确认；不自动恢复输出。",error);}
        }
        void OnDisposed(object sender,EventArgs args){Dispose();}
        internal Action<string,string> CreateProgressSource()
        {
            return CreateScopedProgressSource(false);
        }
        // Only the native monitor can issue this bounded waiting category. Bad
        // or expired metadata remains terminal UNKNOWN, never an exemption.
        static bool TryReadPendingDeadline(string detail,out DateTime deadline)
        {
            deadline=DateTime.MinValue;DateTime observed;
            string[] parts=(detail??"").Split('|');
            if(parts.Length<2||!DateTime.TryParse(parts[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out observed)||observed.Kind!=DateTimeKind.Utc)return false;
            const string key="PendingUntilUtc=";
            int start=detail.IndexOf(key,StringComparison.Ordinal);if(start<0||detail.IndexOf(key,start+key.Length,StringComparison.Ordinal)>=0)return false;
            start+=key.Length;int end=detail.IndexOf(';',start);string value=end<0?detail.Substring(start):detail.Substring(start,end-start);
            return DateTime.TryParse(value.Trim(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out deadline)&&deadline.Kind==DateTimeKind.Utc&&
                deadline>observed&&(deadline-observed).TotalMilliseconds<=500&&deadline>DateTime.UtcNow&&observed<=DateTime.UtcNow.AddMilliseconds(250);
        }
        internal Action<string,string> CreateScopedProgressSource(bool manualZeroInput)
        {
            long generation=Interlocked.Increment(ref progressGeneration);
            var begin=control.GetType().GetMethod("BeginP04FeedbackSource");
            long source=begin==null?0:(long)begin.Invoke(control,null);
            Interlocked.Exchange(ref displayedProgressSource,source);
            return (stage,detail)=>{
                if(disposed||control.IsDisposed||generation!=Interlocked.Read(ref progressGeneration))return;
                progressForSource(source,manualZeroInput,stage,detail);
            };
        }
        void PublishEmergencyObservation(long source,string stage,DateTime utc)
        {
            if(utc.Kind!=DateTimeKind.Utc||utc>DateTime.UtcNow.AddMilliseconds(250))return;
            Action publish=()=>{
                // Dictionary/state ownership and home captions live on the UI
                // thread. Recheck the source after dispatch, not just before it.
                if(disposed||control.IsDisposed||source!=Interlocked.Read(ref displayedProgressSource))return;
                var observed=EmergencyObserved;if(observed!=null)observed(stage,utc);
            };
            if(control.InvokeRequired){try{control.BeginInvoke(publish);}catch(InvalidOperationException){}}
            else publish();
        }
        void CancelMonitor(){lock(gate)if(monitor!=null)monitor.Cancel();}
        internal async Task StopMonitor(){Task task;lock(gate){if(monitor!=null)monitor.Cancel();task=monitorTask;}try{await task.ConfigureAwait(false);}catch(OperationCanceledException){} }
        internal void RequireExplicitReconnectAfterCaptureFailure(string outcomeStatus="PresenceUnknown")
        {
            ConnectionFailureStatus=outcomeStatus;
            monitorRestartSuppressed=true;CancelMonitor();RevokeBatch();
            Action invalidate=()=>{
                if(disposed||control.IsDisposed)return;
                control.GetType().GetMethod("ResetP04FeedbackSession").Invoke(control,null);
                if(Interlocked.Exchange(ref unavailableNotified,1)==0&&ConnectionUnavailable!=null)ConnectionUnavailable();
            };
            if(control.InvokeRequired){try{control.BeginInvoke(invalidate);}catch(InvalidOperationException){}}
            else invalidate();
        }
        async void RefreshMonitorMode()
        {
            if(disposed||control.IsDisposed||LateBound.ReadBoolean(control,false,"HasActiveHardwareSession"))return;
            // A different UI voltage mode is not an explicit reconnect. Keep
            // the latched owner and its DI, never trade it for a fresh capability.
            if(emergencyOutputLatched){RevokeBatch();return;}
            bool inputOnly=initializationInputOnly||(LateBound.ReadBoolean(control,false,"AdvancedMode")&&
                Convert.ToDouble(control.GetType().GetProperty("CalculatedTargetPeakVoltageKv").GetValue(control,null))==0);
            // Changing one nonzero target to another does not change the idle
            // DI owner. Do not discard current samples/restart the subscription
            // for a voltage display or plan-selection change alone.
            lock(gate)if(monitorInputOnly.HasValue&&monitorInputOnly.Value==inputOnly&&!monitorTask.IsCompleted)return;
            try{Interlocked.Increment(ref progressGeneration);control.GetType().GetMethod("ResetP04FeedbackSession").Invoke(control,null);
                await StopMonitor();StartMonitor();}
            catch(Exception error){log.Error("PressureInputMode","设备输入模式切换未完成；不自动恢复输出。",error);}
        }
        internal bool OwnsDeviceSession
        { get { lock(gate)return monitor!=null&&(closeTask==null||closeTask.Status!=TaskStatus.RanToCompletion); } }
        internal void BeginPageExit()
        {
            pageExitRequested=true;
            RevokeBatch();Trial.CancelAnalysis();
        }
        internal Task CloseAsync()
        {
            RevokeBatch();
            lock(gate)return closeTask??(closeTask=CloseCoreAsync());
        }
        internal void RevokeBatch()
        {
            Batch.Revoke();
            try{var method=bridge.GetMethod("RevokeBatch");if(method!=null)method.Invoke(null,null);}
            catch(Exception error){log.Error("PressureBatchRevoke","批次撤销未获驱动进程确认；禁止自动继续。",error);}
        }
        async Task CloseCoreAsync()
        {
            await StopMonitor().ConfigureAwait(false);
            var ownedProperty=bridge.GetProperty("HasDeviceSession");
            bool owned=ownedProperty!=null&&(bool)ownedProperty.GetValue(null,null);
            var method=bridge.GetMethod("CloseSessionAsync");
            if(method!=null)await ((Task)method.Invoke(null,null)).ConfigureAwait(false);
            // A real, exclusively owned session has now closed successfully.
            // Only this cleanup evidence may clear a stale capture block; merely
            // clicking Start, seeing HIGH, or receiving a UI ACK cannot clear it.
            if(owned&&method!=null&&!String.IsNullOrEmpty(CaptureSerial))
                await HVX.Acquisition.PicoCaptureCadence.Shared.ConfirmReleasedAsync(CaptureSerial,
                    text=>log.Info("PressureSessionRelease",text)).ConfigureAwait(false);
        }
        // A mode handoff is explicit and failure-closed. The input-only owner
        // must really close before an HV-capable idle owner may be requested.
        // UI noise readiness is committed only after this task succeeds.
        internal async Task CompleteInitializationInputOwnerAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();validate();
            lock(gate) {
                if(disposed||monitorRestartSuppressed||closeTask!=null||!initializationInputOnly||initializationHandoff)
                    throw new InvalidOperationException("只读初始化会话无法交接；加压仍禁用。");
                initializationHandoff=true;
            }
            try {
                bool remainsInputOnly=ModuleHost.ReadPressureUi(control,()=>LateBound.ReadBoolean(control,false,"AdvancedMode")&&
                    Convert.ToDouble(control.GetType().GetProperty("CalculatedTargetPeakVoltageKv").GetValue(control,null))==0);
                if(remainsInputOnly) {
                    token.ThrowIfCancellationRequested();validate();
                    lock(gate){initializationInputOnly=false;initializationHandoff=false;}
                    StartMonitor();
                    log.Info("PressureInitializationHandoff","初始化与当前自定义0 kV均为只读输入；保留同一设备会话，无输出许可或重建。");
                    return;
                }
                await CloseCoreAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();validate();
                ModuleHost.ReadPressureUi(control,()=>{
                    token.ThrowIfCancellationRequested();validate();
                    Interlocked.Increment(ref progressGeneration);
                    control.GetType().GetMethod("ResetP04FeedbackSession").Invoke(control,null);
                    lock(gate){initializationInputOnly=false;initializationHandoff=false;monitorInputOnly=null;}
                    StartMonitor();
                    return true;
                });
                log.Info("PressureInitializationHandoff","只读初始化会话已成功释放；正式只读监测重新取得当前DI样本后才可人工开始加压。未自动启动高压。");
            }catch {
                lock(gate)initializationHandoff=false;
                RequireExplicitReconnectAfterCaptureFailure("StopReleaseUnconfirmed");
                throw;
            }
        }
        internal void StartMonitor()
        {
            if(control.IsDisposed||monitorRestartSuppressed||pageExitRequested)return;
            if(control.InvokeRequired){try{control.BeginInvoke((Action)StartMonitor);}catch(InvalidOperationException){}return;}
            bool manualZero=initializationInputOnly||(LateBound.ReadBoolean(control,false,"AdvancedMode")&&
                Convert.ToDouble(control.GetType().GetProperty("CalculatedTargetPeakVoltageKv").GetValue(control,null))==0);
            lock(gate){if(disposed||monitorRestartSuppressed||pageExitRequested||initializationHandoff||closeTask!=null||control.IsDisposed||!control.Visible||!monitorTask.IsCompleted)return;
                if(emergencyOutputLatched&&monitorInputOnly.HasValue)manualZero=monitorInputOnly.Value;
                monitorInputOnly=manualZero;
                if(monitor!=null)monitor.Dispose();monitor=new CancellationTokenSource();var token=monitor.Token;
                var monitorProgress=CreateScopedProgressSource(manualZero);
                monitorTask=Task.Run(async()=>{
                    while(!token.IsCancellationRequested){
                        try{
                            validate();string directory=Path.Combine(Path.GetTempPath(),"Xiongxin-pressure-status-"+Guid.NewGuid().ToString("N"));
                            await (Task)bridge.GetMethod(manualZero?"MonitorManualZeroAsync":"MonitorAsync").Invoke(null,new object[]{configuration,directory,monitorProgress,token});
                        }catch(Exception error){if(token.IsCancellationRequested||disposed||control.IsDisposed)break;monitorProgress("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));monitorProgress("PRESSURE_SESSION_UNAVAILABLE",error.Message);log.Error("PressureStatus","Read-only emergency status unavailable.",error);break;}
                        if(!token.IsCancellationRequested)await Task.Delay(5000,token);
                    }
                },token);
            }
        }
        internal void KeepPostProcessSubscription(IDisposable subscription){if(subscription!=null)postProcessSubscriptions.Add(subscription);}
        public void Dispose()
        {
            lock(gate){if(disposed)return;disposed=true;if(monitor!=null)monitor.Cancel();}
            Batch.Revoke();control.VisibleChanged-=VisibilityChanged;control.Disposed-=OnDisposed;
            foreach(var subscription in postProcessSubscriptions)if(subscription!=null)subscription.Dispose();postProcessSubscriptions.Clear();
            CloseAsync().ContinueWith(task=>log.Error("PressureSessionClose","设备会话释放失败，未把退出当作安全完成。",task.Exception),TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
