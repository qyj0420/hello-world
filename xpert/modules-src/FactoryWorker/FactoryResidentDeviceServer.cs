using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactorySessionRequest
    {
        public string Id;
        public string Command;
        public string SettingsPath;
        public string Directory;
        public FactoryInputStopRecord InputStopContext;
        public FactoryFailedRoundReleaseRecord FailureReleaseContext;
        [XmlIgnore] internal FactorySettings FrozenSettings;
        [XmlIgnore] internal FactoryResult ArchivedResult;
        [XmlIgnore] internal bool InputStopCompleted;
        [XmlIgnore] internal bool HealthyRoundStopCompleted;
        [XmlIgnore] internal bool EmergencyRoundStopCompleted;
    }

    internal interface IFactoryResidentSession : IDisposable
    {
        string SessionId {get;}
        bool Faulted {get;}
        bool CanReuseSession {get;}
        Action<string,string> ConnectionReport {set;}
        void OpenSession(FactorySettings settings);
        void PollIdle();
        void EndRound();
        void DetachRoundCallbacks();
    }

    internal static class FactoryResidentDeviceServer
    {
        internal static int Run(string[] args)
        {
            return RunCore(args,()=>new NativeSession(),(request,session)=>Program.Worker(
                new[]{"--worker",request.Command,request.SettingsPath,request.Directory,args[3]},((NativeSession)session).Backend,
                request.FrozenSettings,result=>request.ArchivedResult=result,()=>request.InputStopCompleted=true,()=>request.HealthyRoundStopCompleted=true,
                ()=>request.EmergencyRoundStopCompleted=true),
                ()=>FactoryDeviceLease.Acquire());
        }

        // The production loop itself is exercised offline with managed session,
        // round and lease implementations; tests never instantiate NativeSession.
        internal static int RunCore(string[] args,Func<IFactoryResidentSession> createSession,
            Func<FactorySessionRequest,IFactoryResidentSession,int> runRound,Func<IDisposable> acquireLease)
        {
            string sessionDirectory=null,failedRoundDirectory=null;
            FactorySessionRequest failedAcceptedRequest=null;
            IFactoryResidentSession backend=null;IDisposable lease=null;SessionSupervisor supervisor=null;
            FactoryEmergencySnapshotPublisher journal=null;
            bool journalStopped=true;
            FactoryBatchAuthorization batch=null;
            var events=new ConcurrentQueue<Tuple<string,string>>();
            var faultTracker=new FactorySessionFaultTracker();
            Action<string,string> queue=(stage,detail)=>{faultTracker.Observe(stage,detail);events.Enqueue(Tuple.Create(stage,detail));};
            int exit=0,parent=0;bool cleanupFailed=false;Exception failure=null;FactorySessionFault fault=null;
            bool batchCancellationObserved=false;
            try
            {
                if(args==null || args.Length!=4 || args[0]!="--device-session" || !Int32.TryParse(args[3],out parent) || parent<=0)
                    throw new ArgumentException("常驻设备会话参数无效。");
                string settingsPath=RequireAbsolute(args[1]);sessionDirectory=RequireAbsolute(args[2]);
                if(!Directory.Exists(sessionDirectory))throw new DirectoryNotFoundException("会话目录不存在。");
                var settings=FactoryNativeSessionPolicy.PrepareOpen(FactorySettings.Load(settingsPath));
                supervisor=new SessionSupervisor(sessionDirectory,parent);supervisor.Start();
                // The SDK owner only queues status. This independent managed
                // journal drains while Open/Pico/AI is busy, without reading any
                // native input or making readiness/output decisions.
                journal=new FactoryEmergencySnapshotPublisher(10,"Factory resident status journal",
                    ()=>Drain(events,sessionDirectory),error=>supervisor.RequestPublicationFailure(error));
                journalStopped=false;
                journal.Start();
                if(supervisor.StopRequested)throw new InvalidOperationException(supervisor.StopReason);
                lease=acquireLease();
                backend=createSession();faultTracker.SessionId=backend.SessionId;
                // Open owns the SDK thread. Publish its already-read DI snapshot
                // immediately rather than holding it behind a slow Pico Open.
                // This is input status only: ready.flag still requires full Open.
                backend.ConnectionReport=queue;
                batch=new FactoryBatchAuthorization(backend.SessionId);
                backend.OpenSession(settings);backend.ConnectionReport=queue;Drain(events,sessionDirectory);
                if(backend.Faulted)throw new InvalidOperationException("只读会话急停输入为低或已发生通信故障；已锁存，不开放输出。");
                if(supervisor.StopRequested)throw new OperationCanceledException(supervisor.StopReason);
                WriteNew(Path.Combine(sessionDirectory,"ready.flag"),backend.SessionId);
                WriteProgress(sessionDirectory,"SESSION_READY","原序列号设备会话已打开；空闲只读，无AO/DO或AI采集。SessionId="+backend.SessionId);
                while(!supervisor.StopRequested)
                {
                    string batchCancelPath=Path.Combine(sessionDirectory,"batch.cancel");
                    if(File.Exists(batchCancelPath) && !batchCancellationObserved)
                    {
                        batch.Revoke(); batchCancellationObserved=true;
                        WriteProgress(sessionDirectory,"BATCH_REVOKED","停止请求撤销本会话批次；保持只读监测，续轮请求不得恢复授权。");
                    }
                    string requestPath=Path.Combine(sessionDirectory,"request.xml");
                    if(File.Exists(requestPath))
                    {
                        faultTracker.Phase="Running";
                        FactorySessionRequest request=ReadRequest(requestPath);
                        ValidateRequest(request);
                        var next=FactoryNativeSessionPolicy.PrepareQueuedRound(FactoryFailedRoundReleaseEvidence.LoadInputSettings(
                            request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id),request.Command=="Calibration");
                        if(!FactoryNativeSessionPolicy.SameDeviceSession(settings,next))
                            throw new InvalidOperationException("轮次请求设备身份/驱动与常驻会话不一致；未执行轮次或输出。");
                        string accepted=Path.Combine(sessionDirectory,"request."+Guid.Parse(request.Id).ToString("N")+".accepted.xml");
                        File.Move(requestPath,accepted); // Existing ID fails instead of replaying.
                        failedRoundDirectory=request.Directory;
                        failedAcceptedRequest=request;
                        var latchedSession=backend as IFactoryEmergencyLatchedSessionEvidence;
                        if(latchedSession!=null&&latchedSession.EmergencyOutputLatched)
                        {
                            // A later HIGH or new queued request is not a reset.
                            // Decline it without ending the healthy DI owner.
                            backend.PollIdle();
                            if(!latchedSession.CanRetainEmergencyDisplaySession||backend.Faulted)
                                throw new InvalidOperationException("急停锁存后的只读监测故障；保留真实错误，不恢复输出。");
                            batch.Revoke();batchCancellationObserved=true;
                            File.WriteAllText(Path.Combine(request.Directory,"error.txt"),"急停输出仍锁存；本请求未执行。只读DI继续，不能自动恢复或续轮。",new UTF8Encoding(false));
                            WriteNew(Path.Combine(request.Directory,"session-result.txt"),"1");
                            failedRoundDirectory=null;failedAcceptedRequest=null;continue;
                        }
                        if(batchCancellationObserved || File.Exists(batchCancelPath))
                        {
                            if(next.BatchContinuation || (next.AutomaticBatch && !next.BatchStartConfirmed))
                                throw new InvalidOperationException("批次已由停止撤销，续轮请求不能清除停止状态。");
                            // Only a newly explicit start may acknowledge an OLD
                            // cancellation. A racing new request stays at the original
                            // path and is checked again before/after round execution.
                            if(File.Exists(batchCancelPath))File.Move(batchCancelPath,batchCancelPath+".accepted."+Guid.NewGuid().ToString("N"));
                            batchCancellationObserved=false;
                        }
                        if(File.Exists(batchCancelPath))throw new OperationCanceledException("新开始授权期间再次收到停止。");
                        request.FrozenSettings=batch.Authorize(next);
                        supervisor.ActiveRoundDirectory=request.Directory;
                        int result;Exception roundFailure=null;
                        try
                        {
                            if(supervisor.StopRequested || File.Exists(batchCancelPath))throw new OperationCanceledException(supervisor.StopReason??"批次停止。");
                            var stopEvidence=backend as IFactoryHealthyRoundStopSessionEvidence;
                            if(stopEvidence!=null)stopEvidence.ResetHealthyRoundStopEvidence();
                            result=runRound(request,backend);
                        }
                        catch(Exception ex)
                        {
                            roundFailure=ex;result=1;File.WriteAllText(Path.Combine(request.Directory,"error.txt"),ex.ToString(),new UTF8Encoding(false));
                        }
                        finally
                        {
                            backend.ConnectionReport=queue;
                            backend.DetachRoundCallbacks();
                        }
                        bool batchCancelled=File.Exists(batchCancelPath);
                        if(batchCancelled)
                        {
                            batch.Revoke(); batchCancellationObserved=true;
                        }
                        if(result==FactoryInputStopEvidence.ResultCode&&request.InputStopCompleted&&
                            request.FrozenSettings.TargetKv==0&&!request.FrozenSettings.ZeroSynchronizedOptical&&!supervisor.StopRequested)
                        {
                            // Poll the existing sole input monitor. LOW is a
                            // valid input-only state; communication errors are not.
                            backend.PollIdle();Drain(events,sessionDirectory);
                            var stopHealth=backend as IFactoryInputStopSessionEvidence;
                            if(stopHealth!=null&&stopHealth.InputStopHealthy&&backend.CanReuseSession&&!backend.Faulted&&FactoryInputStopEvidence.CanPublish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id))
                            {
                                batch.Revoke();batchCancellationObserved=true;
                                FactoryInputStopEvidence.Publish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id);
                                WriteProgress(sessionDirectory,"INPUT_STOPPED_IDLE","本轮0 kV输入停止与缓冲释放已确认；保留只读会话，停止不代表放电，不自动续轮。");
                                WriteNew(Path.Combine(request.Directory,"session-result.txt"),FactoryInputStopEvidence.ResultCode.ToString(CultureInfo.InvariantCulture));
                                failedRoundDirectory=null;failedAcceptedRequest=null;supervisor.ActiveRoundDirectory=null;continue;
                            }
                        }
                        // A normal user Stop is not a device fault. Once the
                        // existing runner has shut down and ended all round-owned
                        // resources, retain the healthy session in read-only idle.
                        if(batchCancelled && !supervisor.StopRequested && backend.CanReuseSession && !backend.Faulted)
                        {
                            var stopEvidence=backend as IFactoryHealthyRoundStopSessionEvidence;
                            // A Stop can arrive after Worker has completed its
                            // final save/journal drain and returned 0. That is not
                            // a cleanup failure; exact request-bound Stop plus
                            // the same real EndRound facts are still mandatory.
                            bool verified=roundFailure==null&&
                                ((result==1&&request.HealthyRoundStopCompleted)||(result==0&&request.ArchivedResult!=null))&&stopEvidence!=null&&
                                stopEvidence.HealthyRoundStopFacts!=null&&stopEvidence.HealthyRoundStopFacts.Complete&&
                                FactoryHealthyRoundStopEvidence.CanPublish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id);
                            if(verified)FactoryHealthyRoundStopEvidence.Publish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id,
                                stopEvidence.HealthyRoundStopFacts,backend.CanReuseSession);
                            WriteProgress(sessionDirectory,verified?"ROUND_STOPPED_IDLE":"BATCH_CANCELLED_IDLE",verified?
                                "本轮关断、停采及缓冲释放已核验；批次撤销，保持连接只读待机，不自动续轮，不代表已放电。":
                                "批次停止且会话仍在运行；本轮完整停止回执尚未核验，不计为成功，不自动续轮。");
                            WriteNew(Path.Combine(request.Directory,"session-result.txt"),(verified?FactoryHealthyRoundStopEvidence.ResultCode:1).ToString(CultureInfo.InvariantCulture));
                            failedRoundDirectory=null;failedAcceptedRequest=null; supervisor.ActiveRoundDirectory=null;
                            continue;
                        }
                        var emergencyStop=backend as IFactoryEmergencyLatchedSessionEvidence;
                        if(roundFailure==null&&result==1&&request.EmergencyRoundStopCompleted&&!supervisor.StopRequested&&
                            emergencyStop!=null&&emergencyStop.EmergencyOutputLatched&&!backend.Faulted)
                        {
                            // Re-read only the current monitor's published sample.
                            // Driver/deadline failures still throw and terminate.
                            backend.PollIdle();Drain(events,sessionDirectory);
                            if(emergencyStop.CanRetainEmergencyDisplaySession&&
                                FactoryEmergencyRoundStopEvidence.CanPublish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id))
                            {
                                batch.Revoke();batchCancellationObserved=true;
                                FactoryEmergencyRoundStopEvidence.Publish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id,
                                    emergencyStop.HealthyRoundStopFacts,emergencyStop.CanRetainEmergencyDisplaySession);
                                WriteProgress(sessionDirectory,"EMERGENCY_STOPPED_DISPLAY_ONLY","急停本轮关断、停采、缓冲释放已返回；原DI只读继续，输出锁存，不自动恢复或续轮，不代表已放电。");
                                WriteNew(Path.Combine(request.Directory,"session-result.txt"),FactoryEmergencyRoundStopEvidence.ResultCode.ToString(CultureInfo.InvariantCulture));
                                failedRoundDirectory=null;failedAcceptedRequest=null;supervisor.ActiveRoundDirectory=null;continue;
                            }
                        }
                        if(result!=0 || !backend.CanReuseSession)
                        {
                            failedRoundDirectory=request.Directory;exit=result==0?1:result;
                            throw new InvalidOperationException("本轮未成功结束或原设备会话不可复用；清理后退出，不自动恢复输出。",roundFailure);
                        }
                        if(!supervisor.StopRequested&&File.Exists(batchCancelPath)&&request.ArchivedResult!=null)
                        {
                            var stopEvidence=backend as IFactoryHealthyRoundStopSessionEvidence;
                            if(stopEvidence!=null&&stopEvidence.HealthyRoundStopFacts!=null&&stopEvidence.HealthyRoundStopFacts.Complete&&
                                !backend.Faulted&&backend.CanReuseSession&&
                                FactoryHealthyRoundStopEvidence.CanPublish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id))
                            {
                                batch.Revoke();batchCancellationObserved=true;
                                FactoryHealthyRoundStopEvidence.Publish(request,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id,
                                    stopEvidence.HealthyRoundStopFacts,backend.CanReuseSession);
                                WriteProgress(sessionDirectory,"ROUND_STOPPED_IDLE","本轮收尾已核验；已停止，不自动继续加压，不代表已放电。");
                                WriteNew(Path.Combine(request.Directory,"session-result.txt"),FactoryHealthyRoundStopEvidence.ResultCode.ToString(CultureInfo.InvariantCulture));
                                failedRoundDirectory=null;failedAcceptedRequest=null;supervisor.ActiveRoundDirectory=null;continue;
                            }
                        }
                        if(supervisor.StopRequested || File.Exists(batchCancelPath) || File.Exists(Path.Combine(request.Directory,"cancel.flag")))
                            throw new OperationCanceledException("轮次结束边缘收到停止，批次授权已撤销，不开始下一轮。");
                        if(request.FrozenSettings.AutomaticBatch)
                        {
                            batch.Complete(request.FrozenSettings,request.ArchivedResult);
                            WriteProgress(sessionDirectory,"BATCH_ROUND_ARCHIVED","批次 "+request.FrozenSettings.SequenceId+
                                " 第 "+request.FrozenSettings.SequenceRound+" 轮归档与资源收尾完成；仅正常同批次续轮，不是放电确认。");
                        }
                        // EndRound and worker record/diagnostic writes have all
                        // returned before a successful result can be observed.
                        WriteNew(Path.Combine(request.Directory,"session-result.txt"),"0");
                        failedRoundDirectory=null;
                        failedAcceptedRequest=null;
                        supervisor.ActiveRoundDirectory=null;
                    }
                    else
                    {
                        faultTracker.Phase="Idle";
                        backend.PollIdle();Drain(events,sessionDirectory);
                        if(backend.Faulted)throw new InvalidOperationException("空闲设备通信或P0.4状态故障已锁存；原会话退出，不自动恢复输出。");
                        supervisor.Wait(250);
                    }
                }
                WriteProgress(sessionDirectory,"SESSION_STOP",supervisor.StopReason);
            }
            catch(Exception ex)
            {
                failure=ex;if(exit==0)exit=1;
                fault=faultTracker.Freeze(ex);
                if(sessionDirectory!=null){TryWriteError(sessionDirectory,"error.txt",ex);TryWriteFault(sessionDirectory,fault,false);}
            }
            finally
            {
                if(batch!=null)batch.Revoke();
                faultTracker.Phase="Cleanup";
                var cleanup=new List<Exception>();
                bool roundEndReturned=false,nativeDisposeReturned=false,leaseReleaseReturned=false;
                if(supervisor!=null)Attempt(supervisor.Dispose,cleanup);
                if(backend!=null)
                {
                    Attempt(()=>backend.ConnectionReport=queue,cleanup);
                    Attempt(()=>{backend.EndRound();roundEndReturned=true;},cleanup);
                    Attempt(()=>{backend.Dispose();nativeDisposeReturned=true;},cleanup);
                }
                if(journal!=null)
                {
                    Attempt(()=>{journal.Dispose();journalStopped=true;},cleanup);
                    if(journal.Failure!=null)cleanup.Add(journal.Failure);
                }
                if(cleanup.Count==0 && lease!=null)Attempt(()=>{lease.Dispose();leaseReleaseReturned=true;},cleanup);
                // On failed native cleanup retain the mutex until process exit;
                // its abandonment makes the next process report unknown state.
                cleanupFailed=cleanup.Count!=0;
                if(cleanupFailed)
                {
                    exit=1;var error=new AggregateException("常驻设备清理未完成；不能确认关断或释放。",cleanup);
                    if(sessionDirectory!=null)
                    {
                        TryWriteError(sessionDirectory,"cleanup-error.txt",error);
                        TryWriteFault(sessionDirectory,new FactorySessionFault {SessionId=faultTracker.SessionId,Device="Session",
                            Category=FactorySessionFaultCategory.CleanupError,Phase="Cleanup",OccurredUtc=DateTime.UtcNow,
                            Detail=error.ToString(),PriorDetail=fault==null?null:fault.Detail},true);
                    }
                }
                if(sessionDirectory!=null)
                {
                    try
                    {
                        if(journalStopped)Drain(events,sessionDirectory);
                        if(!cleanupFailed)WriteNew(Path.Combine(sessionDirectory,"closed.flag"),"Native cleanup completed; physical discharge not certified.");
                        // Publish only after the accepted round's explicit native
                        // EndRound (Stop/buffer release), full Dispose and lease
                        // release have all returned. This never certifies a round,
                        // residual safety, or permission to reopen/output.
                        if(!cleanupFailed&&failedAcceptedRequest!=null&&failedAcceptedRequest.FailureReleaseContext!=null)
                            FactoryFailedRoundReleaseEvidence.Publish(failedAcceptedRequest,sessionDirectory,backend.SessionId,parent,Process.GetCurrentProcess().Id,
                                roundEndReturned,nativeDisposeReturned,leaseReleaseReturned);
                        if(failedRoundDirectory!=null)WriteNew(Path.Combine(failedRoundDirectory,"session-result.txt"),exit.ToString(CultureInfo.InvariantCulture));
                    }
                    catch(Exception ex)
                    {
                        exit=1;TryWriteError(sessionDirectory,"cleanup-error.txt",new AggregateException("清理/轮次终态无法可靠记录。",failure??ex,ex));
                    }
                }
            }
            return exit;
        }

        internal static FactorySessionRequest ReadRequest(string path)
        {
            if(new FileInfo(path).Length>65536)throw new InvalidDataException("会话命令超过允许长度。");
            var options=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536};
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var reader=XmlReader.Create(stream,options))return (FactorySessionRequest)new XmlSerializer(typeof(FactorySessionRequest)).Deserialize(reader);
        }
        internal static void ValidateRequest(FactorySessionRequest request)
        {
            Guid id;
            if(request==null || !Guid.TryParse(request.Id,out id) || id==Guid.Empty)throw new InvalidDataException("会话命令代号须为非空GUID。");
            if(request.Command!="Pressure" && request.Command!="Calibration")throw new InvalidDataException("常驻会话仅接受加压或校准轮次，不接受任意命令。");
            string directory=RequireAbsolute(request.Directory),settings=RequireAbsolute(request.SettingsPath);
            string expected=Path.Combine(directory,"factory-settings.xml");
            if(!String.Equals(settings,expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("轮次参数必须是该绝对记录目录下的factory-settings.xml。");
            request.Directory=directory;request.SettingsPath=settings;
        }
        internal static string RequireAbsolute(string path)
        {
            if(String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.IndexOf('"')>=0)
                throw new InvalidDataException("会话协议必须使用绝对路径。");
            string full=Path.GetFullPath(path);
            // C:relative and drive-relative \\path must not inherit worker CWD.
            if(!String.Equals(full.TrimEnd('\\','/'),path.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("会话路径必须为规范化绝对路径，不能包含相对段。");
            return full.TrimEnd('\\','/');
        }
        private static void WriteNew(string path,string text)
        {
            string pending=path+".pending."+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);
            File.Move(pending,path); // A parent never observes an empty/partial completion marker.
        }
        private static readonly object progressGate=new object();
        private static void WriteProgress(string directory,string stage,string detail)
        {
            lock(progressGate)
            {
            string line=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz",CultureInfo.InvariantCulture)+"\t"+stage+"\t"+
                (detail??"").Replace("\r"," ").Replace("\n"," ").Replace("\t"," ")+"\r\n";
            using(var stream=new FileStream(Path.Combine(directory,"progress.log"),FileMode.Append,FileAccess.Write,FileShare.ReadWrite))
            using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(line);
            }
        }
        private static void Drain(ConcurrentQueue<Tuple<string,string>> events,string directory)
        {lock(progressGate){Tuple<string,string> entry;while(events.TryDequeue(out entry))WriteProgress(directory,entry.Item1,entry.Item2);}}
        private static void TryWriteError(string directory,string name,Exception failure)
        {try{File.WriteAllText(Path.Combine(directory,name),failure.ToString(),new UTF8Encoding(false));}catch{/* Exit remains failure; no readiness/result is manufactured. */}}
        private static void TryWriteFault(string directory,FactorySessionFault fault,bool cleanup)
        {try{FactorySessionFaultStore.Write(directory,fault,cleanup);}catch(Exception ex){TryWriteError(directory,"fault-record-error.txt",ex);}}
        private static void Attempt(Action action,List<Exception> failures)
        {try{action();}catch(Exception ex){failures.Add(ex);}}

        internal sealed class NativeSession : IFactoryResidentSession,IFactoryInputStopSessionEvidence,IFactoryEmergencyLatchedSessionEvidence
        {
            internal readonly NativeFactoryBackend Backend=new NativeFactoryBackend();
            public string SessionId {get{return Backend.SessionId;}}
            public bool Faulted {get{return Backend.Faulted;}}
            public bool CanReuseSession {get{return Backend.CanReuseSession;}}
            public bool InputStopHealthy {get{return Backend.InputStopHealthy;}}
            public bool EmergencyOutputLatched {get{return Backend.EmergencyOutputLatched;}}
            public bool CanRetainEmergencyDisplaySession {get{return Backend.CanRetainEmergencyDisplaySession;}}
            public FactoryHealthyRoundStopFacts HealthyRoundStopFacts {get{return Backend.HealthyRoundStopFacts;}}
            public void ResetHealthyRoundStopEvidence(){Backend.ResetHealthyRoundStopEvidence();}
            public Action<string,string> ConnectionReport {set{Backend.ConnectionReport=value;}}
            public void OpenSession(FactorySettings settings){Backend.OpenSession(settings);}
            public void PollIdle(){Backend.PollIdle();}
            public void EndRound(){Backend.EndRound();}
            public void Dispose(){Backend.Dispose();}
            public void DetachRoundCallbacks(){Backend.RawFeedbackRead=null;Backend.MonitorChanged=null;}
        }

        // File/process supervision only. Native operations never run here.
        private sealed class SessionSupervisor : IDisposable
        {
            private readonly string directory;private readonly int parent;
            private readonly ManualResetEvent stopped=new ManualResetEvent(false);
            private readonly ManualResetEvent quit=new ManualResetEvent(false);
            private Thread thread;
            internal volatile string ActiveRoundDirectory;
            internal volatile bool StopRequested;
            internal volatile string StopReason;
            internal SessionSupervisor(string directory,int parent){this.directory=directory;this.parent=parent;}
            internal void Start(){Check();thread=new Thread(Loop){IsBackground=true,Name="Factory resident file supervision"};thread.Start();}
            internal void Wait(int milliseconds){stopped.WaitOne(milliseconds);}
            internal void RequestPublicationFailure(Exception error)
            {Request("会话状态日志发布失败；停止原会话，不恢复输出："+error.Message);}
            private void Loop(){while(!quit.WaitOne(100)){Check();}}
            private void Check()
            {
                try
                {
                    string heartbeat=Path.Combine(directory,"session.heartbeat");
                    if(File.Exists(Path.Combine(directory,"close.flag")))Request("设备界面请求关闭，等待关断与资源释放。");
                    else if(!File.Exists(heartbeat) || DateTime.UtcNow-File.GetLastWriteTimeUtc(heartbeat)>TimeSpan.FromSeconds(2))Request("设备界面心跳失效，停止并清理原会话。");
                    else {using(var process=Process.GetProcessById(parent))if(process.HasExited)Request("设备界面进程已退出，停止并清理原会话。");}
                }
                catch(Exception ex){Request("会话监督不可用，停止原会话："+ex.Message);}
                string round=ActiveRoundDirectory;
                if((StopRequested || File.Exists(Path.Combine(directory,"batch.cancel"))) && round!=null)
                    try{File.WriteAllText(Path.Combine(round,"cancel.flag"),"session or batch supervision stopped");}catch{/* The round's independent lease/parent monitor remains active. */}
            }
            private void Request(string reason){if(!StopRequested){StopReason=reason;StopRequested=true;stopped.Set();}}
            public void Dispose(){quit.Set();if(thread!=null && !thread.Join(1000))return;quit.Dispose();stopped.Dispose();}
        }
    }
}
