using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    // UI-side owner. Only this gate may submit rounds or replace a worker. A
    // cancelled idle subscription never closes a handle needed by the next run.
    internal static class FactoryResidentDevices
    {
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1,1);
        private static FactoryResidentDeviceClient current;
        internal static bool HasSession { get { return current != null; } }
        internal static void RevokeBatch()
        { var client=current; if(client!=null)client.RevokeBatch(); }
        internal static async Task Watch(FactorySettings settings,Action<string,string> report,CancellationToken token)
        {
            FactoryResidentDeviceClient client;
            long delivered;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                var previous=current;
                client=await Ensure(settings,report,token).ConfigureAwait(false);
                delivered=!ReferenceEquals(previous,client)?client.OpenProgressDelivered:
                    (File.Exists(client.ProgressPath)?new FileInfo(client.ProgressPath).Length:0);
            }
            finally { gate.Release(); }
            // Old idle notifications are never replayed into a new round/watch.
            while(!token.IsCancellationRequested)
            {
                Program.Drain(client.ProgressPath,report,ref delivered);
                client.ThrowIfExited();
                await Task.Delay(100,token).ConfigureAwait(false);
            }
        }
        internal static async Task RunRound(FactorySettings settings,FactoryRunKind kind,string directory,Action<string,string> report,CancellationToken token)
        {
            if(settings.AutomaticBatch && settings.BatchContinuation)
                settings.ValidateBeforeOperatorConfirmation(kind); // Candidate only; server issues the non-XML permit.
            else settings.Validate(kind);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var client=await Ensure(settings,report,token).ConfigureAwait(false);
                await client.RunRound(settings,kind,directory,report,token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        internal static async Task ExclusiveLegacy(Func<Task> operation)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await CloseLocked().ConfigureAwait(false);await operation().ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        private static async Task<FactoryResidentDeviceClient> Ensure(FactorySettings settings,Action<string,string> report,CancellationToken token)
        {
            if(current!=null && (!current.Alive || !current.Matches(settings)))await CloseLocked().ConfigureAwait(false);
            if(current==null)
            {
                // Publish ownership before waiting: startup/cleanup failure must
                // not leave an untracked child or allow a second Open.
                current=new FactoryResidentDeviceClient(settings);
                var starting=current;
                await FactoryResidentLaunchLifecycle.StartAsync(starting.Start,()=>starting.WaitReady(report,token),CloseLocked,token).ConfigureAwait(false);
            }
            current.ThrowIfExited();return current;
        }
        internal static async Task CloseAsync()
        {
            // A run holds gate. Request cooperative cancellation before waiting
            // for it, never kill an output owner to obtain the lock.
            var c=current;if(c!=null)c.RequestClose();
            await gate.WaitAsync().ConfigureAwait(false);
            try { await CloseLocked().ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        private static async Task CloseLocked()
        {
            var c=current;if(c==null)return;
            await c.Close().ConfigureAwait(false);
            if(ReferenceEquals(current,c))current=null;
        }
        internal static void AttachLifetime(Form form)
        {
            bool closing=false,closed=false;
            form.FormClosing+=async(s,e)=>
            {
                if(closed || !HasSession)return;
                e.Cancel=true;if(closing)return;closing=true;
                try { await CloseAsync();closed=true;form.Close(); }
                catch(Exception ex)
                {
                    closing=false;
                    MessageBox.Show(form,"设备会话关闭未确认，保留窗口等待处理；不代表输出已关闭。\r\n"+ex.Message,
                        "设备清理异常",MessageBoxButtons.OK,MessageBoxIcon.Error);
                }
            };
        }
    }

    internal sealed class FactoryResidentDeviceClient
    {
        private readonly FactorySettings identity;
        private readonly string directory;
        private readonly Process process;
        private readonly UnifiedResidentLaunchRecord launchEvidence;
        private readonly string settingsPath;
        private readonly System.Threading.Timer heartbeat;
        private readonly object heartbeatGate=new object();
        private Exception heartbeatError;
        private string activeDirectory;
        private volatile bool closing,disposed,started;
        internal string ProgressPath { get { return Path.Combine(directory,"progress.log"); } }
        internal bool Alive { get { return started && !disposed && !process.HasExited; } }
        internal bool Matches(FactorySettings other) { return FactoryNativeSessionPolicy.SameDeviceSession(identity,other); }
        internal void RevokeBatch()
        {
            // Independent of the per-round gate: Stop must revoke a batch even
            // while waiting between rounds and without closing healthy handles.
            string marker=Path.Combine(directory,"batch.cancel");
            if(File.Exists(marker))return;
            string pending=marker+".pending."+Guid.NewGuid().ToString("N");
            File.WriteAllText(pending,"operator batch stop "+DateTime.UtcNow.ToString("O"));
            try { File.Move(pending,marker); }
            catch(IOException) { if(!File.Exists(marker))throw; }
        }
        internal FactoryResidentDeviceClient(FactorySettings settings)
        {
            identity=settings.Clone();
            directory=Path.Combine(Path.GetTempPath(),"Xiongxin-Factory-Sessions",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"session-settings.xml");settings.Save(path);settingsPath=path;
            File.WriteAllText(Path.Combine(directory,"session.heartbeat"),"alive");
            launchEvidence=UnifiedResidentLaunchEvidence.Create(directory,path,Process.GetCurrentProcess().Id,typeof(Program).Assembly.Location);
            process=new Process {StartInfo=new ProcessStartInfo(typeof(Program).Assembly.Location,
                "--device-session "+Quote(path)+" "+Quote(directory)+" "+Process.GetCurrentProcess().Id)
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}};
            heartbeat=new System.Threading.Timer(_=>
            {
                lock(heartbeatGate)
                {
                    if(disposed || closing || heartbeatError!=null)return;
                    try { File.WriteAllText(Path.Combine(directory,"session.heartbeat"),"alive"); }
                    catch(Exception ex) { heartbeatError=ex; }
                }
            },null,Timeout.Infinite,Timeout.Infinite);
        }
        internal void Start()
        {
            if(started || disposed || closing)throw new InvalidOperationException("设备工作进程不能重复启动或在关闭时启动。");
            try {
                if(!process.Start())throw new IOException("设备会话进程未启动；未提交原生设备操作。");
                started=true;
            }catch {
                // A launch API exception is not itself proof that no child was
                // created. If Process acquired an identity, retain that owner
                // and make Close wait for its actual exit/release evidence.
                try {started=process.Id>0;}catch(InvalidOperationException){}
                throw;
            }
            heartbeat.Change(0,400);
        }
        private static string Quote(string v) { return "\""+v.Replace("\"","\\\"")+"\""; }
        internal long OpenProgressDelivered {get;private set;}
        internal async Task WaitReady(Action<string,string> report,CancellationToken token)
        {
            var watch=Stopwatch.StartNew();
            long delivered=0;
            while(!File.Exists(Path.Combine(directory,"ready.flag")))
            {
                token.ThrowIfCancellationRequested();ThrowIfExited();
                Program.Drain(ProgressPath,report,ref delivered);
                if(watch.ElapsedMilliseconds>30000)throw new TimeoutException("设备会话打开超过30秒；请求清理并等待旧调用退出，不重叠重新打开。");
                await Task.Delay(80,token).ConfigureAwait(false);
            }
            ThrowIfExited();
            Program.Drain(ProgressPath,report,ref delivered);
            OpenProgressDelivered=delivered;
            knownSessionId=File.ReadAllText(Path.Combine(directory,"ready.flag"));
        }
        private string knownSessionId;
        internal void ThrowIfExited()
        {
            if(!Alive)
            {
                var fault=FactorySessionFaultStore.Read(directory,knownSessionId);
                // The original exit status must not disappear behind a later
                // missing-cleanup exception from startup cancellation.
                if(started&&!disposed)fault.Detail=(fault.Detail??"")+Environment.NewLine+"WorkerExitCode="+process.ExitCode+"; session="+directory;
                throw new FactorySessionFaultException(fault);
            }
            if(heartbeatError!=null)throw new IOException("设备会话监督心跳写入失败，禁止提交新操作。",heartbeatError);
        }
        internal async Task RunRound(FactorySettings settings,FactoryRunKind kind,string record,Action<string,string> report,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfExited();if(closing)throw new InvalidOperationException("设备会话正在关闭。");
            Directory.CreateDirectory(record);
            if(Directory.GetFileSystemEntries(record).Length!=0)throw new InvalidOperationException("本轮记录目录非空，禁止覆盖。");
            string settingsPath=Path.Combine(record,"factory-settings.xml");settings.Save(settingsPath);
            string lease=Path.Combine(record,"supervisor.heartbeat"),cancel=Path.Combine(record,"cancel.flag");
            string resultPath=Path.Combine(record,"session-result.txt"),progress=Path.Combine(record,"progress.log");
            File.WriteAllText(lease,"alive");
            string id=Guid.NewGuid().ToString("N");
            var request=new FactorySessionRequest {Id=id,Command=kind.ToString(),SettingsPath=Path.GetFullPath(settingsPath),Directory=Path.GetFullPath(record)};
            request.InputStopContext=FactoryInputStopEvidence.Create(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id);
            request.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id);
            string pending=Path.Combine(directory,"request.pending."+id+".xml");
            long delivered=0;bool stopped=false;Exception supervisionError=null;var elapsed=Stopwatch.StartNew();
            using(var cancelRegistration=token.Register(()=>{try{
                File.WriteAllText(Path.Combine(record,FactoryInputStopEvidence.RequestStopFile),id);
                File.WriteAllText(cancel,"operator stop");}catch{}}))
            try
            {
                activeDirectory=record;
                using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write))new XmlSerializer(typeof(FactorySessionRequest)).Serialize(stream,request);
                token.ThrowIfCancellationRequested();
                if(closing)throw new OperationCanceledException("设备会话关闭中，未投递下一轮。");
                File.Move(pending,Path.Combine(directory,"request.xml"));
                while(!File.Exists(resultPath))
                {
                    if(!Alive)break;
                    try
                    {
                        if(supervisionError==null) { File.WriteAllText(lease,"alive");Program.Drain(progress,report,ref delivered); }
                        if(!stopped && (token.IsCancellationRequested || closing || elapsed.Elapsed.TotalSeconds>
                            settings.RampTimeoutSeconds+settings.DischargeTimeoutSeconds+settings.Input.TriggerTimeoutMs/1000.0+30))
                        {
                            stopped=true;File.WriteAllText(cancel,"stop requested");
                            if(report!=null)report("STOP","已请求停止本轮，等待关断、停采和缓冲释放；不会强杀输出工作进程。");
                        }
                        if(heartbeatError!=null)throw new IOException("常驻会话监督失败",heartbeatError);
                    }
                    catch(Exception ex)
                    {
                        if(supervisionError==null)supervisionError=ex;
                        stopped=true;try { File.WriteAllText(cancel,"supervision failure"); } catch { }
                        // No further round heartbeat: child independently cancels.
                    }
                    await Task.Delay(80).ConfigureAwait(false);
                }
                Program.Drain(progress,report,ref delivered);
                if(supervisionError!=null)throw new InvalidOperationException("本轮监督失败，已等待设备工作完成/退出，不能计为完成。",supervisionError);
                if(!File.Exists(resultPath))ThrowIfExited();
                int code;
                bool resultParsed=Int32.TryParse(await ReadPublishedRoundResultAsync(resultPath).ConfigureAwait(false),out code);
                if(resultParsed&&settings.TargetKv==0&&!settings.ZeroSynchronizedOptical&&token.IsCancellationRequested&&!closing&&
                    heartbeatError==null&&FactoryInputStopEvidence.Verify(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id,Alive,code))
                {
                    ThrowIfExited();
                    if(File.ReadAllText(Path.Combine(directory,"ready.flag"))!=knownSessionId)throw new InvalidDataException("设备会话代号已改变，停止证明已失效。");
                    throw new FactoryInputStoppedException();
                }
                if(!resultParsed || code!=0)
                {
                    var emergencyStop=resultParsed&&!closing&&heartbeatError==null?
                        ReadEmergencyRoundStop(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id,Alive,code):null;
                    if(emergencyStop!=null)
                    {
                        ThrowIfExited();
                        throw emergencyStop;
                    }
                    if(resultParsed&&token.IsCancellationRequested&&!closing&&heartbeatError==null&&
                        FactoryHealthyRoundStopEvidence.Verify(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id,Alive,code))
                    {
                        ThrowIfExited();
                        if(File.ReadAllText(Path.Combine(directory,"ready.flag"))!=knownSessionId)throw new InvalidDataException("设备会话代号已改变，本轮停止证明失效。");
                        throw new FactoryHealthyRoundStoppedException();
                    }
                    string error=Path.Combine(record,"error.txt");
                    string detail=File.Exists(error)?File.ReadAllText(error):null;
                    if(resultParsed&&heartbeatError==null&&FactoryFailedRoundReleaseEvidence.CanAwaitExit(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id,code))
                    {
                        // The bound receipt is written only at the end of native
                        // cleanup. Await real process completion, not a guessed
                        // exit code/closed marker; do not close/reopen any device.
                        while(!process.HasExited)await Task.Delay(80).ConfigureAwait(false);
                        if(FactoryFailedRoundReleaseEvidence.Verify(request,directory,knownSessionId,Process.GetCurrentProcess().Id,process.Id,process.HasExited,code))
                            throw new FactoryFailedRoundReleasedException(FactoryResidentRoundOutcome.Classify(
                                false,resultParsed,code,detail,FactoryResidentRoundOutcome.ReadTerminalFault(directory,knownSessionId)));
                    }
                    // Result 1 is also emitted after an ordinary batch Stop while
                    // the resident remains alive. It is NOT native cleanup proof
                    // or proof that the worker exited. Without the separately
                    // bound input-stop/release receipt above, retain fail-closed
                    // ownership and the original cause; never restore monitoring.
                    throw FactoryResidentRoundOutcome.Classify(Alive,resultParsed,code,detail,
                        FactoryResidentRoundOutcome.ReadTerminalFault(directory,knownSessionId));
                }
                if(stopped) { token.ThrowIfCancellationRequested();throw new InvalidOperationException("本轮已收到停止/期限请求，不能自动继续。"); }
                token.ThrowIfCancellationRequested();
                if(closing)throw new OperationCanceledException("设备会话关闭中，不能继续下一轮。");
            }
            finally { activeDirectory=null; }
        }
        internal static async Task<string> ReadPublishedRoundResultAsync(string path,
            Func<string,string> read=null,Func<int,Task> delay=null)
        {
            read=read??File.ReadAllText;delay=delay??(milliseconds=>Task.Delay(milliseconds));
            var budget=Stopwatch.StartNew();IOException lastSharingFailure=null;
            while(true)
            {
                if(lastSharingFailure!=null&&budget.ElapsedMilliseconds>=100)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastSharingFailure).Throw();
                try{return read(path);}
                catch(IOException error) when((error.HResult&0xFFFF)==32||(error.HResult&0xFFFF)==33)
                {
                    // Publication is atomic, but a short-lived file sharing lock
                    // may outlast visibility. Retry this read only, not a round or
                    // device operation; all other I/O failures remain unchanged.
                    lastSharingFailure=error;
                    int remaining=100-(int)budget.ElapsedMilliseconds;
                    if(remaining<=0)throw;
                    await delay(Math.Min(10,remaining)).ConfigureAwait(false);
                }
            }
        }
        // Shared by the real client and isolated integration tests. A result
        // code or exception text alone never keeps a connection alive.
        internal static FactoryEmergencyRoundStoppedException ReadEmergencyRoundStop(FactorySessionRequest request,
            string sessionDirectory,string sessionId,int parentPid,int workerPid,bool alive,int resultCode)
        {
            if(!FactoryEmergencyRoundStopEvidence.Verify(request,sessionDirectory,sessionId,parentPid,workerPid,alive,resultCode))return null;
            if(File.ReadAllText(Path.Combine(sessionDirectory,"ready.flag"))!=sessionId)
                throw new InvalidDataException("设备会话代号已改变，急停停止回执失效。");
            return new FactoryEmergencyRoundStoppedException();
        }
        internal void RequestClose()
        {
            lock(heartbeatGate)
            {
                if(disposed)return;closing=true;
                // Stopping the heartbeat is the independent fallback if the
                // request file cannot be written. Keep owning the process.
                try { File.WriteAllText(Path.Combine(directory,"close.flag"),"close requested"); } catch { }
                string record=activeDirectory;
                if(record!=null)try { File.WriteAllText(Path.Combine(record,"cancel.flag"),"session closing"); } catch { }
            }
        }
        internal async Task Close()
        {
            if(disposed)return;RequestClose();
            if(!started) { disposed=true;heartbeat.Dispose();process.Dispose();return; }
            while(!process.HasExited)await Task.Delay(100).ConfigureAwait(false);
            string cleanupError=Path.Combine(directory,"cleanup-error.txt");
            if(File.Exists(cleanupError) || File.Exists(Path.Combine(directory,FactorySessionFaultStore.CleanupFile)))
                throw new FactorySessionFaultException(FactorySessionFaultStore.Read(directory,knownSessionId));
            if(!File.Exists(Path.Combine(directory,"closed.flag"))&&
                UnifiedResidentLaunchEvidence.VerifyNeverEntered(launchEvidence,settingsPath,launchEvidence.WorkerSHA256,process.Id,process.HasExited,process.ExitCode)==null)
                throw new FactorySessionReleaseUnconfirmedException("旧设备工作进程已退出但无清理完成记录，禁止自动重开；请检查现场设备状态。WorkerExitCode="+process.ExitCode+"；记录："+directory);
            lock(heartbeatGate) { disposed=true;heartbeat.Dispose(); }
            process.Dispose();
            // Session audit files intentionally retained, never erase evidence.
        }
    }

    internal sealed class FactorySessionReleaseUnconfirmedException : IOException
    {
        internal FactorySessionReleaseUnconfirmedException(string detail):base(detail) { }
    }

    // This exception changes diagnostic facts only. In particular it is not an
    // OperationCanceledException, a release receipt, or a healthy-session permit.
    internal sealed class FactoryResidentRoundIncompleteException : InvalidOperationException
    {
        public bool WorkerAliveWhenObserved {get{return true;}}
        public bool InputResourcesReleased {get{return false;}}
        public bool CaptureResourcesReleased {get{return false;}}
        public string OutcomeCode {get{return "ResidentRoundIncompleteUnconfirmed";}}
        internal readonly int? ResultCode;
        internal FactoryResidentRoundIncompleteException(string detail,int? code)
            :base("本轮未完成或已被取消；工作进程仍在运行，但本轮停止及资源释放尚未证实，不能计为完成或自动继续。"+
                (String.IsNullOrWhiteSpace(detail)?"请检查本轮原始记录。":Environment.NewLine+detail))
        {ResultCode=code;}
    }

    internal static class FactoryResidentRoundOutcome
    {
        internal static FactorySessionFault ReadTerminalFault(string directory,string sessionId)
        {
            if(!File.Exists(Path.Combine(directory,FactorySessionFaultStore.FaultFile))&&
                !File.Exists(Path.Combine(directory,FactorySessionFaultStore.CleanupFile))&&
                !File.Exists(Path.Combine(directory,"cleanup-error.txt")))return null;
            return FactorySessionFaultStore.Read(directory,sessionId);
        }
        // No result code, text, Alive check or fault record grants reuse. Strong
        // bound input-stop and native-release verification remain at their
        // original callers, with no change to either evidence contract.
        internal static Exception Classify(bool workerAlive,bool resultParsed,int resultCode,
            string roundDetail,FactorySessionFault fault)
        {
            if(resultParsed&&resultCode==0)throw new ArgumentException("成功结果不能分类为失败。");
            if(fault!=null)
            {
                fault=fault.Copy();
                if(!String.IsNullOrWhiteSpace(roundDetail)&&
                    (fault.Detail??"").IndexOf(roundDetail,StringComparison.Ordinal)<0)
                    fault.Detail=(fault.Detail??"")+Environment.NewLine+"本轮原始原因："+roundDetail;
                // Cleanup uncertainty takes priority even if the process is
                // alive, or a prior physical LOW/native error was recorded.
                if(fault.Category==FactorySessionFaultCategory.CleanupError||workerAlive)
                    return new FactorySessionFaultException(fault);
            }
            string detail=fault==null?roundDetail:fault.Detail;
            if(!workerAlive)return new FactoryWorkerExitedException(String.IsNullOrWhiteSpace(detail)?
                "本轮未完成，设备工作进程已退出；请检查原始记录与清理状态，不自动恢复输出。":detail);
            return new FactoryResidentRoundIncompleteException(detail,resultParsed?(int?)resultCode:null);
        }
    }
}
