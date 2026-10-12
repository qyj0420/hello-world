using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using System.Windows.Forms;
using System.Runtime.CompilerServices;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // No SDK work occurs in the desktop process. The existing supervised worker
    // owns all native calls, emergency watchdog and shutdown sequence.
    public static class UnifiedPressureBridge
    {
        static readonly ConditionalWeakTable<Form,object> lifetimeForms=new ConditionalWeakTable<Form,object>();
        static int realHighVoltageAttempted;
        internal static async Task<bool> WaitForExitCleanup(Task cleanup,int timeoutMs)
        {
            if(cleanup==null)throw new ArgumentNullException("cleanup");
            if(timeoutMs<1)throw new ArgumentOutOfRangeException("timeoutMs");
            if(await Task.WhenAny(cleanup,Task.Delay(timeoutMs))!=cleanup)
            {
                cleanup.ContinueWith(t=>System.Diagnostics.Trace.TraceError(t.Exception.ToString()),TaskContinuationOptions.OnlyOnFaulted);
                return false;
            }
            try { await cleanup;return true; }
            catch(Exception error){System.Diagnostics.Trace.TraceError(error.ToString());return false;}
        }
        public static void AttachLifetime(Form form)
        {
            if(form==null||form.IsDisposed)throw new InvalidOperationException("加压会话须绑定有效主窗口。");
            lock(lifetimeForms){object attached;if(lifetimeForms.TryGetValue(form,out attached))return;lifetimeForms.Add(form,new object());}
            bool closing=false,exitConfirmed=false;
            form.FormClosing+=async(sender,e)=>{
                // Respect the desktop's earlier handler (X may return Home,
                // not exit). Never turn its canceled navigation into an exit.
                if(e.Cancel||exitConfirmed)return;
                if(System.Threading.Volatile.Read(ref realHighVoltageAttempted)!=0 && !closing &&
                    MessageBox.Show(form,"完成试验请关闭安全控制盒，并安全放电！","确认退出",
                        MessageBoxButtons.OKCancel,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.OK)
                {e.Cancel=true;return;}
                if(!FactoryResidentDevices.HasSession)return;
                e.Cancel=true;if(closing)return;closing=true;
                try{RevokeBatch();await WaitForExitCleanup(CloseSessionAsync(),3000);}
                catch(Exception error){System.Diagnostics.Trace.TraceError(error.ToString());}
                // UI exit is not a successful cleanup acknowledgement. The worker
                // retains its own stop/watchdog and fault records; never kill it,
                // reset ownership, or grant permission to restart outputs here.
                exitConfirmed=true;if(!form.IsDisposed)form.Close();
            };
        }
        internal static string CleanupFailureSummary(Exception error)
        {
            // Presentation only. Keep the original exception and persisted
            // cleanup-error/session-fault records unchanged; no fault reset.
            string detail=error==null?"":error.Message??"";
            if(detail.Length>32768)detail=detail.Substring(0,32768);
            var codes=System.Text.RegularExpressions.Regex.Matches(detail,@"(?:ART\s*[:：]?\s*|Status\s+Code\s*[:：]\s*)(-?\d{1,10})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Cast<System.Text.RegularExpressions.Match>()
                .Select(m=>m.Groups[1].Value).Distinct().Take(3).ToArray();
            return "设备清理未确认。输出状态未知，禁止继续加压或自动重连。"+
                (codes.Length==0?"":"\r\n驱动错误码："+String.Join("、",codes))+ 
                "\r\n请核查设备状态；完整原因请查看调试信息及设备会话清理日志。";
        }
        public static string CreateConfiguration(string inputXml, double pressureRatio, double maximumKv)
        { return CreateConfigurationWithReview(inputXml,pressureRatio,maximumKv,8); }
        public static string CreateConfigurationWithReview(string inputXml,double pressureRatio,double maximumKv,int rangeCycles)
        { return CreateConfigurationWithFineRamp(inputXml,pressureRatio,maximumKv,rangeCycles,false,maximumKv*.15); }
        public static string CreateConfigurationWithFineRamp(string inputXml,double pressureRatio,double maximumKv,int rangeCycles,
            bool enableFinePressureRamp,double finePressureThresholdKv)
        { return CreateConfigurationCore(inputXml,pressureRatio,maximumKv,rangeCycles,enableFinePressureRamp,finePressureThresholdKv,false,0,true); }
        public static string CreateConfigurationWithEndResidual(string inputXml,double pressureRatio,double maximumKv,int rangeCycles,
            bool enableFinePressureRamp,double finePressureThresholdKv,double endResidualPercent)
        {return CreateConfigurationWithFeedbackSource(inputXml,pressureRatio,maximumKv,rangeCycles,enableFinePressureRamp,finePressureThresholdKv,endResidualPercent,"DIV");}
        public static string CreateConfigurationWithFeedbackSource(string inputXml,double pressureRatio,double maximumKv,int rangeCycles,
            bool enableFinePressureRamp,double finePressureThresholdKv,double endResidualPercent,string sourceCode)
        {
            // maximumKv is the device's configured highest PEAK voltage, not
            // the later internal min(30, maximumKv) command/feedback ceiling.
            if (!FactorySettings.Positive(maximumKv) || maximumKv > 100 ||
                !FactorySettings.Positive(endResidualPercent) || endResidualPercent > 100)
                throw new ArgumentException("设备最高峰值限压或结束残压百分比无效。");
            if(sourceCode!="DIV"&&sourceCode!="VMON")throw new ArgumentException("电压反馈源须为DIV或VMON。");
            return CreateConfigurationCore(inputXml,pressureRatio,maximumKv,rangeCycles,enableFinePressureRamp,
                finePressureThresholdKv,true,maximumKv*1000*endResidualPercent/100,sourceCode=="DIV");
        }
        public static string CreateConfigurationWithoutResidual(string inputXml,double pressureRatio,double maximumKv,int rangeCycles,
            bool enableFinePressureRamp,double finePressureThresholdKv,string sourceCode)
        {
            if(!FactorySettings.Positive(maximumKv)||maximumKv>100)
                throw new ArgumentException("设备最高峰值限压无效。");
            if(sourceCode!="DIV"&&sourceCode!="VMON")throw new ArgumentException("电压反馈源须为DIV或VMON。");
            var settings=Read<FactorySettings>(CreateConfigurationCore(inputXml,pressureRatio,maximumKv,rangeCycles,enableFinePressureRamp,
                finePressureThresholdKv,false,0,sourceCode=="DIV"));
            // Only this new entry opts in. Do not migrate historical bridge APIs,
            // calibration or manual 0 kV by changing CreateConfigurationCore.
            settings.PrecisionRampControl=true;
            settings.DesktopPostProcessing=true;
            settings.Validate(FactoryRunKind.Pressure);
            return Xml(settings);
        }
        private static string CreateConfigurationCore(string inputXml,double pressureRatio,double maximumKv,int rangeCycles,
            bool enableFinePressureRamp,double finePressureThresholdKv,bool endResidualCheckEnabled,double endResidualThresholdV,bool dividerOnlyFeedback)
        {
            if (Double.IsNaN(finePressureThresholdKv) || Double.IsInfinity(finePressureThresholdKv) ||
                finePressureThresholdKv <= 0 || finePressureThresholdKv > maximumKv)
                throw new ArgumentException("精细化加压阈值须大于0且不超过设备最高限压。");
            var input = Read<BenchSettings>(inputXml);
            input.PicoCoupling = 0; input.PicoAnalogOffsetVolts = 0;
            input.AutomaticSharedTrigger = false; input.ArmDelayMs = 500; input.TriggerPulseMs = BenchSettings.OpticalHoldMilliseconds;
            var settings = new FactorySettings { Input = input, Simulation = !input.RealHardware,
                TargetKv = 0, MaximumKv = Math.Min(30, maximumKv), MaximumFeedbackKv = Math.Min(30, maximumKv),
                MaximumAoV = 5, PressureVPerV = pressureRatio, SafeResidualV = 50, TargetTolerancePercent = 2,
                EndResidualCheckEnabled=endResidualCheckEnabled,EndResidualThresholdV=endResidualThresholdV,
                EnableFinePressureRamp=enableFinePressureRamp,FinePressureThresholdKv=finePressureThresholdKv,
                VmonAfterHvStartup = true, StartupVmonLimitV = 500, ZeroBackgroundLimitV = 500,
                EndAfterPressureCapture = true, OpticalTimeoutRetry=true,
                IndependentDischargeConfirmed = false,RangeEvaluationCycleCount=rangeCycles,LocationSaveCycleCount=rangeCycles,
                DividerOnlyFeedback = dividerOnlyFeedback, ReferenceCapacitorEnabled = false, FeedbackWiringConfirmed = false };
            settings.Validate(FactoryRunKind.Pressure);
            return Xml(settings);
        }
        public static async Task<string> CaptureAsync(string configuration, string inputXml, double targetKv,
            string directory, bool wiringConfirmed, Action<string,string> progress, CancellationToken token)
        {
            var settings=PrepareRequest(configuration,inputXml,targetKv);
            settings = FactoryStartConfirmation.Accept(settings, FactoryRunKind.Pressure, wiringConfirmed || targetKv == 0);
            if (settings == null) throw new OperationCanceledException("本轮加压尚未确认。");
            return await CapturePreparedAsync(settings,directory,progress,token).ConfigureAwait(false);
        }
        // The desktop uses one request-bound boundary for pre-native validation.
        // Only the prepare block can issue a rejection receipt. Nothing thrown
        // by StartWorker or the resident/native path is converted to harmless.
        public static async Task<string> CaptureIdentifiedAsync(string configuration,string inputXml,double targetKv,
            string directory,string hostSessionId,string hostRequestId,bool manualZero,bool automatic,
            string batchId,int round,int count,bool confirmed,bool continuation,
            Action<string,string> progress,CancellationToken token)
        {
            var settings=PrepareIdentifiedRequest(hostSessionId,hostRequestId,()=>{
                var prepared=automatic?PrepareBatchSettings(configuration,inputXml,targetKv,batchId,round,count,confirmed,continuation):
                    PrepareRequest(configuration,inputXml,targetKv);
                if(!automatic) {
                    if(manualZero)prepared.ManualZeroInputWithoutEmergency=true;
                    else prepared=FactoryStartConfirmation.Accept(prepared,FactoryRunKind.Pressure,confirmed||targetKv==0);
                    if(prepared==null)throw new OperationCanceledException("本轮尚未开始。");
                }
                if(prepared.BatchContinuation)prepared.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
                else prepared.Validate(FactoryRunKind.Pressure);
                token.ThrowIfCancellationRequested();
                return prepared;
            });
            try { return await CapturePreparedAsync(settings,directory,progress,token).ConfigureAwait(false); }
            catch(FactoryNativeNotStartedException error) when(error.NativeNeverEnteredConfirmed)
            { throw new FactoryRequestRejectedException(hostSessionId,hostRequestId,error); }
        }
        internal static T PrepareIdentifiedRequest<T>(string session,string request,Func<T> prepare)
        {
            Guid sid,rid;
            if(!Guid.TryParseExact(session,"N",out sid)||sid==Guid.Empty||!Guid.TryParseExact(request,"N",out rid)||rid==Guid.Empty)
                throw new ArgumentException("请求身份无效。");
            try { return prepare(); }
            catch(Exception error) when(error is ArgumentException||error is InvalidDataException||
                error is InvalidOperationException||error is XmlException||error is OperationCanceledException)
            { throw new FactoryRequestRejectedException(session,request,error); }
        }
        public static Task<string> CaptureBatchAsync(string configuration,string inputXml,double targetKv,
            string directory,string batchId,int round,int count,bool explicitStartConfirmed,bool continuation,
            Action<string,string> progress,CancellationToken token)
        {
            var settings=PrepareBatchSettings(configuration,inputXml,targetKv,batchId,round,count,explicitStartConfirmed,continuation);
            return CapturePreparedAsync(settings,directory,progress,token);
        }
        public static Task<string> CaptureManualZeroAsync(string configuration,string inputXml,double targetKv,
            string directory,bool wiringConfirmed,Action<string,string> progress,CancellationToken token)
        {
            var settings=PrepareRequest(configuration,inputXml,targetKv);
            settings.ManualZeroInputWithoutEmergency=true;
            settings.Validate(FactoryRunKind.Pressure);
            return CapturePreparedAsync(settings,directory,progress,token);
        }
        internal static FactorySettings PrepareBatchSettings(string configuration,string inputXml,double targetKv,
            string batchId,int round,int count,bool explicitStartConfirmed,bool continuation)
        {
            var settings=PrepareRequest(configuration,inputXml,targetKv);
            Guid id;
            if(settings.Simulation||targetKv<=0||!Guid.TryParseExact(batchId,"N",out id)||
                count<1||count>100||round<1||round>count||settings.Input.RunNumber!=round||
                (continuation?(round<2||explicitStartConfirmed):(round!=1||!explicitStartConfirmed)))
                throw new InvalidDataException("批次、轮次或本次启动确认无效，未发出硬件请求。");
            settings.AutomaticBatch=true;settings.SequenceId=batchId;settings.SequenceRound=round;
            settings.SequenceCount=count;settings.SequenceAttempt=1;
            settings.BatchStartConfirmed=false;settings.BatchContinuation=continuation;
            settings.IndependentDischargeConfirmed=settings.FeedbackWiringConfirmed=false;
            settings.FinalSequenceAi4Observation=round==count;
            if(!continuation)settings=FactoryStartConfirmation.Accept(settings,FactoryRunKind.Pressure,explicitStartConfirmed);
            // Serialized continuation metadata is not a permit. Only the original
            // device server may issue one after native cleanup and archive receipt.
            settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            return settings;
        }
        static FactorySettings PrepareRequest(string configuration,string inputXml,double targetKv)
        {
            var settings=Read<FactorySettings>(configuration);
            var requested=Read<BenchSettings>(inputXml);
            // Only range, coefficient and retry number may change within a session.
            foreach (var field in typeof(BenchSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                if (field.Name != "RangeIndex" && field.Name != "PdCalibrationPcPerV" && field.Name != "AutoRange" && field.Name != "RunNumber" &&
                    !Object.Equals(field.GetValue(settings.Input), field.GetValue(requested)))
                    throw new InvalidDataException("采集配置已改变，请重新打开加压页：" + field.Name);
            settings.Input=requested;settings.TargetKv=targetKv;
            settings.ManualZeroInputWithoutEmergency=false;
            settings.AutomaticBatch=settings.BatchStartConfirmed=settings.BatchContinuation=false;
            settings.IndependentDischargeConfirmed=settings.FeedbackWiringConfirmed=false;
            settings.FinalSequenceAi4Observation=false;settings.BatchPermit=null;
            settings.SequenceId=null;settings.SequenceRound=settings.SequenceCount=settings.SequenceAttempt=1;
            return settings;
        }
        static async Task<string> CapturePreparedAsync(FactorySettings settings,string directory,Action<string,string> progress,CancellationToken token)
        {
            if(settings.BatchContinuation)settings.ValidateBeforeOperatorConfirmation(FactoryRunKind.Pressure);
            else settings.Validate(FactoryRunKind.Pressure);
            if(token.IsCancellationRequested)throw new FactoryNativeNotStartedException("ParentCanceledBeforeLaunch");
            if(!settings.Simulation&&settings.TargetKv>0)Interlocked.Exchange(ref realHighVoltageAttempted,1);
            var result = await Program.StartWorker(settings, FactoryRunKind.Pressure, directory,AdaptProgress(settings.Simulation,progress),token).ConfigureAwait(false);
            if (!result.CompleteCaptureAvailable() && !result.CaptureOnlyAvailable())
                throw new InvalidDataException("本轮未完成，原始数据已保留，不计入有效统计。");
            if(result.FinalAi4Observation!=null&&progress!=null)progress("FINAL_AI4_OBSERVATION",result.FinalAi4Observation.Describe());
            return directory;
        }
        // Offline adapter: the desktop consumes the SAME frozen advisor as
        // the factory UI. It never reinterprets a nonzero capture as a zero run.
        public static string ReviewCapturedRound(string directory, string requestedXml, double targetKv)
        {
            var requested=Read<BenchSettings>(requestedXml);
            FactoryResult round;
            using(var stream=File.OpenRead(Path.Combine(directory,"result.xml")))
            using(var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=67108864}))
                round=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(reader);
            round.Capture=BenchCapture.Load(directory);round.DirectoryPath=Path.GetFullPath(directory);
            if(round.Settings==null||round.Settings.Input==null||round.Kind!=FactoryRunKind.Pressure||round.Settings.TargetKv!=targetKv||
                round.Settings.Simulation==requested.RealHardware||round.Capture.Simulation==requested.RealHardware||
                !(round.CompleteCaptureAvailable()||round.CaptureOnlyAvailable()))
                throw new InvalidDataException("本轮记录来源、目标或完成状态不一致，不能评估下一轮量程。");
            foreach(var field in typeof(BenchSettings).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance))
                if(!Object.Equals(field.GetValue(requested),field.GetValue(round.Capture.Settings))||
                    !Object.Equals(field.GetValue(requested),field.GetValue(round.Settings.Input)))
                    throw new InvalidDataException("本轮量程评估配置不一致："+field.Name);
            round.Capture.ValidateCapture();
            var quality=round.CaptureOnlyAvailable()?FactoryRangeReview.EvaluateCapturedData(round):FactoryRangeReview.Evaluate(round);
            var review=new UnifiedPressureRangeReview{Quality=quality,CurrentRangeIndex=requested.RangeIndex,
                CanApply=FactoryNextRangeReview.CanApply(round,quality,requested.RangeIndex),
                NeedsPrompt=FactoryNextRangeReview.NeedsPrompt(round,quality,true)};
            try{review.NextRangeIndex=FactoryPressureSequence.NextRange(requested.RangeIndex,quality);}
            catch(InvalidOperationException error){review.NextRoundBlocked=true;review.BlockReason=error.Message;review.NextRangeIndex=requested.RangeIndex;}
            return Xml(review);
        }
        public static Task MonitorAsync(string configuration, string directory, Action<string,string> progress, CancellationToken token)
        {
            var settings = Read<FactorySettings>(configuration);
            return Program.WatchConnection(settings,directory,AdaptProgress(settings.Simulation,progress),token);
        }
        public static Task MonitorManualZeroAsync(string configuration,string directory,Action<string,string> progress,CancellationToken token)
        {
            var settings=Read<FactorySettings>(configuration);settings.TargetKv=0;
            settings.ManualZeroInputWithoutEmergency=true;settings.Validate(FactoryRunKind.Pressure);
            return Program.WatchConnection(settings,directory,AdaptProgress(settings.Simulation,progress),token);
        }
        public static void RevokeBatch(){FactoryResidentDevices.RevokeBatch();}
        public static bool HasDeviceSession { get { return FactoryResidentDevices.HasSession; } }
        public static Task CloseSessionAsync(){return FactoryResidentDevices.CloseAsync();}
        internal static Action<string,string> AdaptProgress(bool simulation,Action<string,string> progress)
        {
            return (stage,detail)=>{
                if(progress==null)return;
                string mapped=stage;
                if(stage=="LINK_ART_HIGH")mapped="EMERGENCY_RELEASED";
                else if(stage=="LINK_ART_LOW"||stage=="P04_IDLE_ACTIVE")mapped="EMERGENCY_ACTIVE";
                else if(stage=="LINK_ART_LATCHED_HIGH")mapped="EMERGENCY_LATCHED_RELEASED";
                else if(stage=="LINK_ART_LATCHED_LOW")mapped="EMERGENCY_LATCHED_ACTIVE";
                else if(stage=="LINK_ART_PENDING")mapped="EMERGENCY_PENDING";
                else if(stage=="LINK_ART_ERROR"||stage=="LINK_ART_UNKNOWN"||stage=="LINK_ART_CLOSED")mapped="EMERGENCY_UNKNOWN";
                if(simulation&&mapped.StartsWith("EMERGENCY_",StringComparison.Ordinal))mapped="SIM_"+mapped;
                progress(mapped,detail);
            };
        }
        internal static void Monitor(FactorySettings settings, Action<string,string> report, CancellationToken token)
        {
            if (settings.Simulation) throw new InvalidOperationException("仿真不会生成实机急停状态。");
            FactoryP04Diagnostic.Validate(settings.Input);
            var native = new P04ReadOnlyNative();
            try {
                native.Load(settings.Input.ArtLibraryDirectory);
                if (native.Identity(settings.Input.ArtDevice) != "USB3135 / " + settings.Input.ArtSerial)
                    throw new InvalidOperationException("控制设备序列号不符，急停状态未知。");
                native.OpenLine(settings.Input.ArtDevice, 0);
                while (!token.IsCancellationRequested) {
                    var value = native.ReadLine();
                    DateTime utc = DateTime.UtcNow;
                    bool valid = value != null && value.Status >= 0 && value.Samples == 1 && value.Bytes == 1 && value.Raw <= 1;
                    report(valid ? value.Raw == 1 ? "EMERGENCY_RELEASED" : "EMERGENCY_ACTIVE" : "EMERGENCY_UNKNOWN", utc.ToString("O"));
                    if (!valid) throw new IOException("急停回读失败，设备可能不可用或被占用。");
                    if (token.WaitHandle.WaitOne(100)) break;
                }
            } finally { native.CloseLine(); }
        }
        public static T Read<T>(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml) || xml.Length > 65536) throw new InvalidDataException("无效的采集配置。");
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 }))
                return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
        }
        public static string Xml(object value)
        { using (var text = new StringWriter(CultureInfo.InvariantCulture)) { new XmlSerializer(value.GetType()).Serialize(text, value); return text.ToString(); } }
    }
    public sealed class UnifiedPressureRangeReview
    {
        public string Schema="HVX.Pressure.RangeReview/V1.0.30.9";
        public LocationRangeResult Quality;
        public int CurrentRangeIndex,NextRangeIndex;
        public bool CanApply,NeedsPrompt,NextRoundBlocked;
        public string BlockReason="";
    }
    public sealed class FactoryRequestRejectedException : Exception
    {
        public string HostSessionId { get; private set; }
        public string HostRequestId { get; private set; }
        public bool NativeRequestNotSubmitted { get { return true; } }
        internal FactoryRequestRejectedException(string session,string request,Exception cause):
            base("本轮未开始，请核对当前设置后重试。",cause)
        { HostSessionId=session;HostRequestId=request; }
    }
}
