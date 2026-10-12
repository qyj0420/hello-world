using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryResidentDeviceServerTests
    {
        internal static string FixtureRoot=Path.GetTempPath();
        // Exercises the exact restricted-release adapter, real managed runner,
        // resident request/receipt protocol and unique fake DI loop together.
        // Only the I/O owner and Program.Worker's outer archive body are fake;
        // neither executable entry nor a native Open/API is invoked.
        internal static string RunUnifiedEmergencyRoundStop(Action<string,string> display=null,Action<Exception> stopReceipt=null,Action returnHome=null,Action homeSamples=null)
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Unified emergency chain: "+why);checks++;};
            int pid=Process.GetCurrentProcess().Id;
            foreach(string scenario in new[]{"physical-low","user-stop-race","off-failed","di-failed","buffer-failed"})using(var f=new Fixture())
            {
                bool clean=scenario=="physical-low"||scenario=="user-stop-race";
                int level=1,reads=0,readerThread=0;Exception diError=null;
                FactorySessionRequest accepted=null;Exception runnerFailure=null;var ramp=new FactoryTests.Fixture();
                using(var monitor=new FactoryEmergencyMonitor(()=>{
                    int id=Thread.CurrentThread.ManagedThreadId,prior=Interlocked.CompareExchange(ref readerThread,id,0);
                    if(prior!=0&&prior!=id)throw new Exception("second fake DI reader");
                    Interlocked.Increment(ref reads);if(diError!=null)throw diError;return Volatile.Read(ref level)==1;
                },()=>{}))
                {
                    f.UnifiedAdapter=true;f.Session.InputMonitor=monitor;
                    f.Session.BeforeOpen=()=>{monitor.Start();if(!monitor.ReadReleased())throw new Exception("initial HIGH missing");};
                    ramp.Backend.EmergencyRule=b=>{
                        if(b.PositiveAoWrites>0&&!monitor.OutputStopLatched){
                            ramp.Backend.FailShutdown=scenario=="off-failed";
                            Volatile.Write(ref level,0);var wait=Stopwatch.StartNew();
                            while(!monitor.OutputStopLatched){if(wait.ElapsedMilliseconds>2000)throw new TimeoutException("fake LOW not observed");Thread.Sleep(1);}
                            if(scenario=="user-stop-race"){
                                File.WriteAllText(Path.Combine(f.Directory,"batch.cancel"),"user stop racing actual LOW");
                                ramp.Cancellation.Cancel();
                            }
                        }
                        return monitor.ReadReleased();
                    };
                    f.Round=(r,s)=>UnifiedWorkerEntry.RunPressureRound(r,((UnifiedWorkerEntry.PressureSession)s).Backend,pid.ToString(),
                        (args,backend,settings,saved,input,healthy,emergency)=>{
                            Interlocked.Increment(ref f.RoundCount);accepted=r;
                            try{ramp.Run();throw new Exception("expected actual runner stop");}
                            catch(FactoryRunException error){runnerFailure=error;}
                            s.EndRound();
                            if(scenario=="buffer-failed")f.Session.StopFacts=new FactoryHealthyRoundStopFacts(true,true,true,false,true);
                            if(scenario=="di-failed"){
                                diError=new IOException("fake DI real read failure after physical LOW");
                                if(!SpinWait.SpinUntil(()=>monitor.HasReadFailure,2000))throw new Exception("DI failure not observed");
                            }
                            if(FactoryEmergencyRoundStopEvidence.IsOnlyStopFailure(runnerFailure)&&
                                ((IFactoryEmergencyLatchedSessionEvidence)s).CanRetainEmergencyDisplaySession)emergency();
                            return 1;
                        });
                    f.Start();f.WaitReady();
                    string round=f.Submit(scenario,s=>s.TargetKv=6.2,
                        r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                    if(clean)f.WaitResult(round);else f.WaitExit();
                    check(ramp.Backend.PositiveAoWrites>0&&ramp.Backend.ShutdownCount>0&&!ramp.Backend.Hv&&!ramp.Backend.Optical&&ramp.Backend.Ao==0,
                        "actual managed HV UP takes physical LOW then calls HV OFF/AO0: "+scenario);
                    if(!clean){
                        check(f.Session.Disposes==1&&!File.Exists(Path.Combine(round,FactoryEmergencyRoundStopEvidence.ProofFile)),
                            "shutdown/DI/buffer failure still ends owner, never claims retained healthy connection: "+scenario);continue;
                    }
                    check(accepted.EmergencyRoundStopCompleted&&File.ReadAllText(Path.Combine(round,"session-result.txt"))=="5",
                        "real release RunPressureRound fourth callback reaches real RunResidentCore emergency branch");
                    check(FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,5)&&f.Session.Disposes==0,
                        "bound complete receipt retains exact owner, not failure-release/reconnect");
                    var clientReceipt=FactoryResidentDeviceClient.ReadEmergencyRoundStop(accepted,f.Directory,f.Session.SessionId,pid,pid,true,5);
                    check(clientReceipt!=null&&FactoryResidentDeviceClient.ReadEmergencyRoundStop(accepted,f.Directory,f.Session.SessionId,pid,pid,false,5)==null,
                        "real client contract classifies only a live same-owner bound stop receipt");
                    if(stopReceipt!=null&&scenario=="physical-low")stopReceipt(clientReceipt);
                    int oldReads=Volatile.Read(ref reads);Volatile.Write(ref level,1);
                    f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_HIGH"&&Volatile.Read(ref reads)>oldReads+2;},"same retained DI HIGH");
                    check(!monitor.ReadReleased()&&f.Session.Opens==1&&f.Session.Disposes==0,"new actual HIGH cannot resume outputs or open a replacement DI");
                    if(display!=null&&scenario=="physical-low"){
                        DateTime lowUtc=DateTime.UtcNow;Volatile.Write(ref level,0);
                        f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_LOW"&&utc>lowUtc;},"retained actual LOW for host");
                        Action publish=()=>{string detail;DateTime utc;string state=monitor.ConnectionState(out detail,out utc);
                            UnifiedPressureBridge.AdaptProgress(false,display)("LINK_ART_"+state,utc.ToString("O",CultureInfo.InvariantCulture)+"|"+detail);};
                        publish();Volatile.Write(ref level,1);
                        f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_HIGH";},"retained actual HIGH for host");publish();
                    }
                    if(returnHome!=null&&scenario=="physical-low")returnHome();
                    if(display!=null&&scenario=="physical-low"){
                        int before=Volatile.Read(ref reads);DateTime afterHome=DateTime.UtcNow;Volatile.Write(ref level,0);
                        f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_LOW"&&utc>afterHome;},"fresh LOW after Home");
                        Action publish=()=>{string detail;DateTime utc;string state=monitor.ConnectionState(out detail,out utc);
                            UnifiedPressureBridge.AdaptProgress(false,display)("LINK_ART_"+state,utc.ToString("O",CultureInfo.InvariantCulture)+"|"+detail);};
                        publish();afterHome=DateTime.UtcNow;Volatile.Write(ref level,1);
                        f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_HIGH"&&utc>afterHome;},"fresh HIGH after Home");publish();
                        check(Volatile.Read(ref reads)>before&&f.Session.Opens==1&&f.Session.Disposes==0&&!monitor.ReadReleased(),
                            "Home still receives original reader's fresh physical changes; output never resumes");
                        if(homeSamples!=null)homeSamples();
                    }
                    string denied=f.Submit("no-auto-resume",s=>s.TargetKv=6.2);
                    f.WaitResult(denied);check(f.RoundCount==1&&f.Session.Disposes==0,"later request cannot execute HV UP or drop healthy DI");
                    f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1,"explicit page/session retirement closes owner exactly once");
                }
            }
            return "Unified emergency chain: "+checks+" real-adapter/runner/resident-protocol/fake-I-O integration checks PASS; ProductMain=False; SDKCalled=False; HardwareAccessed=False";
        }
        internal static string RunUnifiedIdleEmergency()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{checks++;if(!ok)throw new Exception("Unified idle DI: "+why);};
            foreach(bool firstPending in new[]{false,true})using(var f=new Fixture())
            using(var pendingRead=new ManualResetEvent(false))using(var returnRead=new ManualResetEvent(false))
            {
                int calls=0,level=0,reader=0;
                using(var monitor=new FactoryEmergencyMonitor(()=>{
                    int id=Thread.CurrentThread.ManagedThreadId,old=Interlocked.CompareExchange(ref reader,id,0);
                    if(old!=0&&old!=id)throw new Exception("idle second DI reader");
                    int n=Interlocked.Increment(ref calls);
                    if(firstPending&&n==1){pendingRead.Set();if(!returnRead.WaitOne(300))throw new TimeoutException("fake initial DI hold");}
                    return Volatile.Read(ref level)==1;
                },()=>{}))
                {
                    f.UnifiedAdapter=true;f.Session.InputMonitor=monitor;
                    f.Session.BeforeOpen=()=>{monitor.EnableIdleTimeoutRetry();monitor.Start();};
                    f.Start();
                    if(firstPending){
                        if(!pendingRead.WaitOne(2000))throw new Exception("initial DI did not enter read");
                        string detail;DateTime utc;
                        check(monitor.ConnectionState(out detail,out utc)=="WAIT"&&utc==DateTime.MinValue&&!File.Exists(Path.Combine(f.Directory,"ready.flag"))&&f.RoundCount==0,
                            "first DI pending is not a capture, Ready, LOW fault or a new output request");returnRead.Set();
                    }
                    f.WaitReady();f.Wait(()=>Volatile.Read(ref calls)>=3,"idle LOW repeats");
                    check(!monitor.OutputStopLatched&&!f.Session.Faulted&&f.Session.Disposes==0&&f.RoundCount==0&&f.Session.Opens==1,
                        "entering page at LOW leaves same healthy input session alive without starting any pressure round");
                    int prior=Volatile.Read(ref calls);Volatile.Write(ref level,1);
                    f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="HIGH"&&Volatile.Read(ref calls)>prior;},"idle new HIGH");
                    check(!monitor.OutputStopLatched&&f.RoundCount==0&&f.Session.Ends==0,"idle LOW/HIGH are fresh display only; no HV UP/OFF round or fabricated capture");
                    f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1,"explicit idle Close owns cleanup exactly once");
                }
            }
            foreach(Exception cause in new Exception[]{new DllNotFoundException("fake missing official SDK"),new InvalidOperationException("fake actual binding mismatch")})using(var f=new Fixture())
            {
                f.UnifiedAdapter=true;f.Session.BeforeOpen=()=>{throw cause;};f.Start();f.WaitExit();
                check(f.Exit!=0&&!File.Exists(Path.Combine(f.Directory,"ready.flag"))&&f.RoundCount==0&&f.Session.Disposes==1,
                    "real initialization error is not suppressed by idle LOW policy: "+cause.GetType().Name);
            }
            return "Unified idle DI: "+checks+" managed initial LOW/Pending/HIGH and initialization-error checks PASS; SDKCalled=False; HardwareAccessed=False";
        }
        internal static string RunEmergencyRoundStop()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Emergency retained stop: "+why);checks++;};
            int pid=Process.GetCurrentProcess().Id;
            foreach(bool cancelRace in new[]{false,true})using(var f=new Fixture())
            {
                int level=1,reads=0,owner=0;FactorySessionRequest accepted=null;
                using(var monitor=new FactoryEmergencyMonitor(()=>{
                    int id=Thread.CurrentThread.ManagedThreadId,prior=Interlocked.CompareExchange(ref owner,id,0);
                    if(prior!=0&&prior!=id)throw new Exception("second DI reader");
                    Interlocked.Increment(ref reads);return Volatile.Read(ref level)==1;
                },()=>{}))
                {
                    f.Session.InputMonitor=monitor;
                    f.Session.BeforeOpen=()=>{monitor.Start();if(!monitor.ReadReleased())throw new Exception("fake initial HIGH missing");};
                    f.Round=(r,s)=>{
                        Interlocked.Increment(ref f.RoundCount);accepted=r;
                        Volatile.Write(ref level,0);var wait=Stopwatch.StartNew();
                        while(!monitor.OutputStopLatched){if(wait.ElapsedMilliseconds>2000)throw new TimeoutException("fake LOW not read");Thread.Sleep(1);}
                        if(cancelRace)File.WriteAllText(Path.Combine(f.Directory,"batch.cancel"),"offline user Stop racing LOW");
                        f.Session.EndRound();r.EmergencyRoundStopCompleted=true;return 1;
                    };
                    f.Start();f.WaitReady();string round=f.Submit("physical-low-"+cancelRace,s=>s.TargetKv=6.2,
                        r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                    f.WaitResult(round);
                    check(File.ReadAllText(Path.Combine(round,"session-result.txt"))=="5"&&f.Session.Disposes==0&&f.Lease.Disposes==0,"LOW stop retains live original owner after cleanup, including user Stop race");
                    check(!f.Session.CanReuseSession&&f.Session.CanRetainEmergencyDisplaySession,"display-only is not reusable output permission");
                    check(FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,5),"same accepted input and complete native-return facts verify distinct receipt");
                    check(!FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,false,5)&&
                        !FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,"wrong",pid,pid,true,5)&&
                        !FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid+1,true,5),"dead/replacement session or worker cannot retain");
                    int before=Volatile.Read(ref reads);Volatile.Write(ref level,1);
                    f.Wait(()=>{DateTime utc;string detail;return monitor.ConnectionState(out detail,out utc)=="LATCHED_HIGH"&&Volatile.Read(ref reads)>=before+3;},"same sole DI reader publishes HIGH after stop");
                    check(!monitor.ReadReleased()&&owner!=Thread.CurrentThread.ManagedThreadId&&f.Session.Disposes==0,"later real HIGH never releases output latch or DI owner");
                    string declined=f.Submit("forbidden-restart",s=>s.TargetKv=6.2,
                        r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                    f.WaitResult(declined);check(f.RoundCount==1&&File.ReadAllText(Path.Combine(declined,"session-result.txt"))=="1"&&f.Session.Disposes==0,"new queued request is declined, never auto-resumes or closes healthy DI");
                    string proof=Path.Combine(round,FactoryEmergencyRoundStopEvidence.ProofFile);byte[] original=File.ReadAllBytes(proof);
                    foreach(string field in new[]{"OutputShutdownReturned","ArtRoundResourcesReleased","PicoStopCaptureReturned","PicoBufferReleaseReturned","RoundEndReturned","OutputLatched","DisplaySessionRetained"})
                    {
                        FactoryEmergencyRoundStopRecord p;using(var stream=File.OpenRead(proof))p=(FactoryEmergencyRoundStopRecord)new XmlSerializer(typeof(FactoryEmergencyRoundStopRecord)).Deserialize(stream);
                        typeof(FactoryEmergencyRoundStopRecord).GetField(field).SetValue(p,false);
                        using(var stream=File.Create(proof))new XmlSerializer(typeof(FactoryEmergencyRoundStopRecord)).Serialize(stream,p);
                        check(!FactoryEmergencyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,5),"missing return/latch fact rejects: "+field);File.WriteAllBytes(proof,original);
                    }
                    f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1&&f.Lease.Disposes==1,"only explicit close disposes healthy owner");
                }
            }
            foreach(string fault in new[]{"off-failed","buffer-failed","di-driver","di-deadline","untyped-record-error"})using(var f=new Fixture())
            {
                f.Round=(r,s)=>{
                    f.Session.Latched=true;f.Session.EndRound();r.EmergencyRoundStopCompleted=true;
                    if(fault=="off-failed")f.Session.StopFacts=new FactoryHealthyRoundStopFacts(false,true,true,true,true);
                    if(fault=="buffer-failed")f.Session.StopFacts=new FactoryHealthyRoundStopFacts(true,true,true,false,true);
                    if(fault=="di-driver")f.Session.PollError=new IOException("actual managed DI driver failure");
                    if(fault=="di-deadline")f.Session.PollError=new TimeoutException("actual managed DI deadline");
                    if(fault=="untyped-record-error")throw new IOException("actual managed recording failure");return 1;
                };
                f.Start();f.WaitReady();string round=f.Submit(fault,s=>s.TargetKv=6.2,
                    r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                f.WaitExit();check(f.Exit!=0&&f.Session.Disposes==1&&!File.Exists(Path.Combine(round,FactoryEmergencyRoundStopEvidence.ProofFile)),"real failure is never retained or promoted to ready: "+fault);
            }
            // Run the actual managed ramp/cleanup code with its existing fake,
            // not Program/worker entry or any device SDK.
            foreach(bool shutdownFailure in new[]{false,true})
            {
                var f=new FactoryTests.Fixture();
                f.Backend.EmergencyRule=b=>b.PositiveAoWrites==0;f.Backend.FailShutdown=shutdownFailure;
                Exception failure=Expect(()=>f.Run(null,FactoryRunKind.Pressure));
                check(!f.Backend.Hv&&!f.Backend.Optical&&f.Backend.Ao==0&&f.Backend.ShutdownCount>0,"HV UP LOW leaves ramp and invokes HV OFF/AO0 before return");
                check(FactoryEmergencyRoundStopEvidence.IsOnlyStopFailure(failure)==!shutdownFailure,"shutdown error cannot be converted to pure stop");
            }
            var backend=new NativeFactoryBackend();int sessionEvents=0,roundEvents=0;
            backend.ConnectionReport=(k,v)=>sessionEvents++;Program.AttachRoundConnectionReport(backend,(k,v)=>roundEvents++);
            backend.ConnectionReport("LINK_ART_LATCHED_LOW","managed fanout only");
            check(sessionEvents==1&&roundEvents==1,"round callback no longer overwrites resident fault-tracker subscription");
            return "Emergency retained stop: "+checks+" managed real-loop/sole-DI/ramp-shutdown/receipt-negative checks PASS; HardwareAccessed=False; SDKCalled=False; NativeSessionCreated=False";
        }
        internal static string RunHealthyRoundStop()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Healthy round stop: "+why);checks++;};
            check(FactoryHealthyRoundStopEvidence.IsOnlyCanceledFailure(new FactoryRunException(new FactoryResult(),new[]{new OperationCanceledException()})),"single runner cancellation is eligible only with separate resource facts");
            check(!FactoryHealthyRoundStopEvidence.IsOnlyCanceledFailure(new OperationCanceledException()),"a bare cancellation has no runner cleanup contract");
            check(!FactoryHealthyRoundStopEvidence.IsOnlyCanceledFailure(new FactoryRunException(new FactoryResult(),new Exception[]{new OperationCanceledException(),new IOException("release failed")})),"cancel plus cleanup or publication error cannot become healthy stop");
            int pid=Process.GetCurrentProcess().Id;
            using(var f=new Fixture())using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                FactorySessionRequest accepted=null;
                f.Session.BeforeEndRound=()=>{entered.Set();if(!release.WaitOne(5000))throw new TimeoutException("fake EndRound not released");};
                f.Round=(r,s)=>{
                    Interlocked.Increment(ref f.RoundCount);accepted=r;
                    File.WriteAllText(Path.Combine(f.Directory,"batch.cancel"),"offline explicit operator stop");
                    File.WriteAllText(Path.Combine(r.Directory,"cancel.flag"),"offline operator stop");
                    File.WriteAllText(Path.Combine(r.Directory,FactoryInputStopEvidence.RequestStopFile),r.Id);
                    f.Session.EndRound();r.HealthyRoundStopCompleted=true;return 1;
                };
                f.Start();f.WaitReady();
                string round=f.Submit("nonzero-stop",s=>s.TargetKv=6.2,r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                f.Wait(()=>entered.WaitOne(0),"actual managed EndRound entered");
                check(!File.Exists(Path.Combine(round,FactoryHealthyRoundStopEvidence.ProofFile))&&!File.Exists(Path.Combine(round,"session-result.txt")),"stop request alone does not publish release or complete while owner cleanup is blocked");
                try{release.Set();f.WaitResult(round);}finally{release.Set();}
                check(FactoryResidentDeviceClient.ReadPublishedRoundResultAsync(Path.Combine(round,"session-result.txt")).GetAwaiter().GetResult()=="4"&&f.Session.Disposes==0&&f.Lease.Disposes==0,"real server publishes distinct stop result without closing held device session/lease");
                check(FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"actual accepted input, stop ID, complete fake EndRound facts and live session verify");
                foreach(int code in new[]{0,1,2,3,-1})check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,code),"other result is not a healthy stop: "+code);
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,false,4),"dead worker never retains session permission");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,"wrong",pid,pid,true,4),"wrong session rejected");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid+1,pid,true,4)&&!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid+1,true,4),"parent and worker PID binding required");
                string proof=Path.Combine(round,FactoryHealthyRoundStopEvidence.ProofFile);byte[] bytes=File.ReadAllBytes(proof);
                foreach(string field in new[]{"OutputShutdownReturned","ArtRoundResourcesReleased","PicoStopCaptureReturned","PicoBufferReleaseReturned","RoundEndReturned","SessionReusable"})
                {
                    FactoryHealthyRoundStopRecord p;using(var stream=File.OpenRead(proof))p=(FactoryHealthyRoundStopRecord)new XmlSerializer(typeof(FactoryHealthyRoundStopRecord)).Deserialize(stream);
                    typeof(FactoryHealthyRoundStopRecord).GetField(field).SetValue(p,false);
                    using(var stream=File.Create(proof))new XmlSerializer(typeof(FactoryHealthyRoundStopRecord)).Serialize(stream,p);
                    check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"independent missing release fact rejected: "+field);File.WriteAllBytes(proof,bytes);
                }
                string stop=Path.Combine(round,FactoryInputStopEvidence.RequestStopFile);File.WriteAllText(stop,Guid.NewGuid().ToString("N"));
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"wrong operator request cannot stop a new round");File.WriteAllText(stop,accepted.Id);
                string nonce=accepted.FailureReleaseContext.Nonce;accepted.FailureReleaseContext.Nonce=Guid.NewGuid().ToString("N");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"copied nonce cannot authorize stopped result");accepted.FailureReleaseContext.Nonce=nonce;
                string input=accepted.FailureReleaseContext.InputSettingsPath;byte[] inputBytes=File.ReadAllBytes(input);File.AppendAllText(input," ");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"mutated immutable input rejected");File.WriteAllBytes(input,inputBytes);
                string acceptedPath=FactoryFailedRoundReleaseEvidence.AcceptedPath(accepted,f.Directory);byte[] acceptedBytes=File.ReadAllBytes(acceptedPath);File.AppendAllText(acceptedPath," ");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"accepted-file SHA binding detects edits even if XML parses identically");File.WriteAllBytes(acceptedPath,acceptedBytes);
                string cancel=Path.Combine(round,"cancel.flag");File.Move(cancel,cancel+".retained");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"missing canceled-round marker cannot grant stop");File.Move(cancel+".retained",cancel);
                string fault=Path.Combine(f.Directory,"cleanup-error.txt");File.WriteAllText(fault,"offline recorded cleanup failure");
                check(!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"recorded cleanup error defeats an earlier healthy receipt");File.Move(fault,fault+".retained");
                check(FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"restored exact bound facts verify, never a successful capture");
                f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1&&f.Lease.Disposes==1&&!FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"explicit close ends retained session; old receipt cannot revive it");
            }
            foreach(string invalid in new[]{"missing-fact","not-pure-cancel","round-error","unusable"})using(var f=new Fixture())
            {
                f.Round=(r,s)=>{
                    Interlocked.Increment(ref f.RoundCount);File.WriteAllText(Path.Combine(f.Directory,"batch.cancel"),"offline stop");
                    File.WriteAllText(Path.Combine(r.Directory,"cancel.flag"),"offline stop");File.WriteAllText(Path.Combine(r.Directory,FactoryInputStopEvidence.RequestStopFile),r.Id);
                    f.Session.EndRound();r.HealthyRoundStopCompleted=invalid!="not-pure-cancel";
                    if(invalid=="missing-fact")f.Session.StopFacts=new FactoryHealthyRoundStopFacts(true,true,true,false,true);
                    if(invalid=="unusable")f.Session.IsFaulted=true;
                    if(invalid=="round-error")throw new IOException("original managed recording failure");return 1;
                };
                f.Start();f.WaitReady();string round=f.Submit(invalid,s=>s.TargetKv=6.2,r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                f.WaitResult(round);
                check(FactoryResidentDeviceClient.ReadPublishedRoundResultAsync(Path.Combine(round,"session-result.txt")).GetAwaiter().GetResult()!="4"&&!File.Exists(Path.Combine(round,FactoryHealthyRoundStopEvidence.ProofFile)),"incomplete/error/unusable path never grants healthy stop: "+invalid);
                if(invalid=="unusable"){f.WaitExit();check(f.Exit!=0&&f.Session.Disposes==1,"unusable session cleanup exits rather than remaining Ready");}else{f.Close();f.WaitExit();}
            }
            return "Healthy round stop: "+checks+" managed real-loop/accepted-input/owner-cleanup/receipt negative checks PASS; HardwareAccessed=False; SDKCalled=False; NativeSessionCreated=False";
        }
        internal static string RunLateHealthyRoundStop()
        {
            int checks=0,pid=Process.GetCurrentProcess().Id;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Late healthy Stop: "+why);checks++;};
            foreach(string scenario in new[]{"after-record","after-snapshot","no-archive","output-failed","art-failed","pico-failed","buffer-failed","end-failed","wrong-nonce","old-stop","drain-failed","unusable"})
            using(var f=new Fixture())
            {
                FactorySessionRequest accepted=null;
                bool expected=scenario=="after-record"||scenario=="after-snapshot";
                f.Round=(r,s)=>{
                    Interlocked.Increment(ref f.RoundCount);accepted=r;f.Session.EndRound();
                    // Only fake round callbacks are executed. No Program.Worker,
                    // NativeSession, SDK or product entry is invoked.
                    if(scenario!="no-archive")r.ArchivedResult=new FactoryResult {AcquisitionCompleted=true};
                    Action stop=()=>{
                        File.WriteAllText(Path.Combine(r.Directory,FactoryInputStopEvidence.RequestStopFile),scenario=="old-stop"?Guid.NewGuid().ToString("N"):r.Id);
                        File.WriteAllText(Path.Combine(r.Directory,"cancel.flag"),"offline exact operator Stop");
                        File.WriteAllText(Path.Combine(f.Directory,"batch.cancel"),"offline exact operator Stop");
                    };
                    if(scenario=="after-snapshot")f.Session.BeforeCanReuse=()=>{f.Session.BeforeCanReuse=null;stop();};else stop();
                    if(scenario.EndsWith("-failed",StringComparison.Ordinal)&&scenario!="drain-failed")
                        f.Session.StopFacts=new FactoryHealthyRoundStopFacts(scenario!="output-failed",scenario!="art-failed",scenario!="pico-failed",scenario!="buffer-failed",scenario!="end-failed");
                    if(scenario=="wrong-nonce")r.FailureReleaseContext.Nonce=Guid.NewGuid().ToString("N");
                    if(scenario=="unusable")f.Session.IsFaulted=true;
                    if(scenario=="drain-failed")throw new IOException("actual managed terminal drain failed after archive callback");
                    return 0;
                };
                f.Start();f.WaitReady();string round=f.Submit(scenario,s=>s.TargetKv=6.2,r=>r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid));
                f.WaitResult(round);
                string code=FactoryResidentDeviceClient.ReadPublishedRoundResultAsync(Path.Combine(round,"session-result.txt")).GetAwaiter().GetResult();
                check((code=="4")==expected,"only returned-zero+archive+actual facts+exact new Stop qualifies: "+scenario);
                check(File.Exists(Path.Combine(round,FactoryHealthyRoundStopEvidence.ProofFile))==expected,"failed/stale/incomplete paths never create a healthy receipt: "+scenario);
                if(expected)
                {
                    check(FactoryHealthyRoundStopEvidence.Verify(accepted,f.Directory,f.Session.SessionId,pid,pid,true,4),"same-live-session receipt verifies: "+scenario);
                    check(f.Session.Disposes==0&&f.Lease.Disposes==0&&f.Session.Ends==1&&f.RoundCount==1,"Stop keeps only the healthy existing owner, no automatic retry or output: "+scenario);
                    string rejected=f.Submit("stale-continuation",s=>{s.TargetKv=6.2;s.AutomaticBatch=true;s.BatchContinuation=true;});
                    f.WaitExit();check(f.RoundCount==1&&f.Exit!=0&&!File.Exists(Path.Combine(rejected,FactoryHealthyRoundStopEvidence.ProofFile)),"stopped batch cannot silently continue: "+scenario);
                }
                else if(scenario=="unusable"){f.WaitExit();check(f.Exit!=0&&f.Session.Disposes==1,"unhealthy owner is cleaned up, not retained");}
                else{f.Close();f.WaitExit();}
            }
            return "Late healthy round stop: "+checks+" exact request/late-edge/archive/EndRound/drain negative checks PASS; HardwareAccessed=False; SDKCalled=False; ProductEntryExecuted=False; WorkerEntryExecuted=False";
        }
        internal static string RunContinuousInitialFeedback()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Continuous initial feedback: "+why);checks++;};
            using(var f=new Fixture())using(var entered=new ManualResetEvent(false))using(var finishOpen=new ManualResetEvent(false))
            {
                int sequence=0;
                f.Session.BeforeOpen=()=>{
                    using(var snapshots=new FactoryEmergencySnapshotPublisher(20,"fake initial DI snapshots",()=>{
                        int n=Interlocked.Increment(ref sequence);
                        f.Session.Report(n%2==0?"LINK_ART_HIGH":"LINK_ART_LOW",DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+"|initial sample "+n);
                    },error=>{throw new Exception("fake publication failed",error);}))
                    {
                        snapshots.Start();entered.Set();
                        if(!finishOpen.WaitOne(5000))throw new TimeoutException("fake Pico Open never released");
                    }
                };
                try
                {
                    f.Start();f.Wait(()=>entered.WaitOne(0),"fake Open is blocked");
                    long cursor=0;var seen=new List<Tuple<string,string>>();
                    string path=Path.Combine(f.Directory,"progress.log");
                    f.Wait(()=>{Program.Drain(path,(key,value)=>seen.Add(Tuple.Create(key,value)),ref cursor);
                        return seen.Count>=6;},"independent production journal delivers multiple fresh samples during blocked Open");
                    check(!File.Exists(Path.Combine(f.Directory,"ready.flag"))&&f.RoundCount==0&&f.Session.Opens==1,"new samples do not fabricate full ready or invoke a round");
                    check(seen.Exists(item=>item.Item1=="LINK_ART_HIGH")&&seen.Exists(item=>item.Item1=="LINK_ART_LOW"),"slow Open does not hide physical changes behind initialization");
                    DateTime prior=DateTime.MinValue;
                    foreach(var item in seen)
                    {
                        DateTime actual=DateTime.Parse(item.Item2.Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                        check(actual>prior&&actual<=DateTime.UtcNow,"journal preserves each ordered actual sample timestamp");prior=actual;
                    }
                    long delivered=cursor;int count=seen.Count;
                    Program.Drain(path,(key,value)=>seen.Add(Tuple.Create(key,value)),ref cursor);
                    check(cursor>=delivered&&seen.Count>=count,"consumer cursor never rewinds or replays opening samples");
                    finishOpen.Set();f.WaitReady();f.Close();f.WaitExit();
                    check(f.Exit==0&&f.Session.Disposes==1&&f.Lease.Disposes==1,"explicit Close releases fake native owner and journal normally");
                    var lines=File.ReadAllLines(path);int samples=0;
                    foreach(string line in lines)if(line.Contains("initial sample ")){check(line.Split('\t').Length==3,"serialized journal preserves complete unmerged lines");samples++;}
                    check(samples>=6&&samples==Volatile.Read(ref sequence),"each queued opening snapshot is drained once before final close");
                }
                finally{finishOpen.Set();}
            }
            return "Continuous initial feedback: "+checks+" managed real-journal/blocked-Open checks PASS; HardwareAccessed=False; SDKCalled=False; NativeSessionCreated=False";
        }
        internal static string RunInitialFeedback()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Initial resident feedback: "+why);checks++;};
            foreach(string state in new[]{"HIGH","LOW","ERROR"})
            using(var f=new Fixture())
            using(var entered=new ManualResetEvent(false))
            using(var finishOpen=new ManualResetEvent(false))
            {
                string stage="LINK_ART_"+state,detail=null;
                DateTime readUtc=DateTime.MinValue;
                f.Session.BeforeOpen=()=>{
                    // A real UTC read time, not a future timestamp, clock
                    // adjustment, fabricated ready marker or output permit.
                    readUtc=DateTime.UtcNow;
                    detail=readUtc.ToString("O",CultureInfo.InvariantCulture)+"|managed initial "+state+"; SessionId="+f.Session.SessionId;
                    f.Session.Report(stage,detail);entered.Set();
                    if(!finishOpen.WaitOne(5000))throw new TimeoutException("managed Open hold was not released");
                    if(state=="ERROR")f.Session.IsFaulted=true;
                };
                try
                {
                    f.Start();f.Wait(()=>entered.WaitOne(0),"managed Open reported initial state");
                    check(f.Session.Opens==1&&f.Session.Disposes==0&&f.RoundCount==0,"fake owner is still inside Open without a round: "+state);
                    check(!File.Exists(Path.Combine(f.Directory,"ready.flag")),"initial input report never publishes ready before Open returns: "+state);
                    long delivered=0;var seen=new List<Tuple<string,string>>();
                    string progress=Path.Combine(f.Directory,"progress.log");
                    f.Wait(()=>{Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                        return seen.Exists(item=>item.Item1==stage&&item.Item2==detail);},"independent journal delivered initial input while Open remained blocked");
                    Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                    check(seen.Count==1&&seen[0].Item1==stage&&seen[0].Item2==detail,"pure production Drain sees exact initial HIGH/LOW/UNKNOWN during blocked Open: "+state);
                    DateTime decoded;
                    check(DateTime.TryParse(detail.Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out decoded)&&
                        decoded.Kind==DateTimeKind.Utc&&decoded==readUtc&&DateTime.UtcNow-readUtc<TimeSpan.FromSeconds(2),
                        "initial sample preserves actual UTC capture time rather than renewing it during file delivery: "+state);
                    long openCursor=delivered;
                    Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                    check(seen.Count==1&&delivered==openCursor&&delivered>0,"open cursor consumes the input once, not replaying old status: "+state);
                    finishOpen.Set();
                    if(state=="ERROR")
                    {
                        f.WaitExit();
                        Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                        check(f.Exit!=0&&!File.Exists(Path.Combine(f.Directory,"ready.flag"))&&f.RoundCount==0,"unknown/read failure cannot be promoted to ready or execute an output round");
                        check(f.Session.Disposes==1&&f.Lease.Disposes==1,"unknown Open is cleaned by the same managed owner");
                    }
                    else
                    {
                        f.WaitReady();
                        // ready.flag and its diagnostic line are distinct
                        // atomic writes; allow the latter to finish naturally.
                        f.Wait(()=>{Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                            return seen.Exists(item=>item.Item1=="SESSION_READY");},"session-ready progress after successful Open");
                        check(seen.Exists(item=>item.Item1=="SESSION_READY")&&seen.FindAll(item=>item.Item1==stage).Count==1,
                            "ready follows successful Open and cursor does not repeat the initial state: "+state);
                        string next=FactoryConnectionWatch.Stamp("fresh status after Open "+state);
                        f.Session.Report("LINK_ART_LOW",next);
                        f.Wait(()=>{Program.Drain(progress,(key,value)=>seen.Add(Tuple.Create(key,value)),ref delivered);
                            return seen.Exists(item=>item.Item1=="LINK_ART_LOW"&&item.Item2==next);},"fresh idle report after opening");
                        check(seen.FindAll(item=>item.Item2==next).Count==1&&seen.FindAll(item=>item.Item2==detail).Count==1,
                            "continued cursor delivers fresh idle input once without replaying Open input: "+state);
                        check(f.RoundCount==0,"session-ready and input delivery alone never call a round or output: "+state);
                        f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1&&f.Lease.Disposes==1,"explicit Close preserves one-owner cleanup: "+state);
                    }
                }
                finally {finishOpen.Set();}
            }
            return "Initial resident feedback: "+checks+" managed real-loop/blocked-Open/progress-cursor checks PASS; HardwareAccessed=False; SDKCalled=False; NativeSessionCreated=False; ClockAdjusted=False";
        }
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Resident server: "+why);checks++;};
            using(var f=new Fixture())
            {
                f.Start();f.WaitReady();
                foreach(double kv in new[]{0.0,0.0,.5,0.0,.5})
                {
                    string round=f.Submit("mixed-"+f.RoundCount,s=>s.TargetKv=kv);f.WaitResult(round);
                    check(File.ReadAllText(Path.Combine(round,"session-result.txt"))=="0" &&
                        f.Session.Opens==1 && f.Session.Disposes==0,"0→0 / positive→0→positive request routing retains one owner and connection");
                }
                check(f.RoundCount==5,"all five mixed-mode requests reached the resident owner");
                f.Close();f.WaitExit();check(f.Exit==0&&f.Session.Disposes==1,"mixed-mode session closes only on explicit close");
            }
            using(var f=new Fixture())
            {
                var secondStarted=new ManualResetEvent(false);var finishSecond=new ManualResetEvent(false);
                Action<string,string> oldCallback=null;
                f.Round=(request,session)=>
                {
                    int n=Interlocked.Increment(ref f.RoundCount);
                    if(n==1)oldCallback=f.Session.Report;
                    else {secondStarted.Set();if(!finishSecond.WaitOne(5000))throw new Exception("second fake round not released");}
                    f.Session.EndRound();return 0;
                };
                f.Start();f.WaitReady();string first=f.Submit("first");f.WaitResult(first);
                check(File.ReadAllText(Path.Combine(first,"session-result.txt"))=="0" && f.Session.Opens==1 && f.Session.Disposes==0,
                    "first completed round returns without closing the session");
                string second=f.Submit("second");f.Wait(()=>secondStarted.WaitOne(0),"second started");
                oldCallback("LINK_PICO_ONLINE","late old round status callback");
                check(!File.Exists(Path.Combine(second,"session-result.txt")),"late old status callback cannot complete the new round");
                finishSecond.Set();f.WaitResult(second);
                check(f.RoundCount==2 && f.Session.Opens==1 && f.Session.Disposes==0 && f.Session.Detaches==2,
                    "two actual request/accept/result cycles reuse one open session");
                f.Close();f.WaitExit();
                check(f.Exit==0 && f.Session.Disposes==1 && f.Lease.Disposes==1 && File.Exists(Path.Combine(f.Directory,"closed.flag")),
                    "explicit close cleans once on owner thread and publishes closed only afterwards");
                secondStarted.Dispose();finishSecond.Dispose();
            }
            using(var f=new Fixture())
            {
                var cleanupEntered=new ManualResetEvent(false);var releaseCleanup=new ManualResetEvent(false);
                f.Round=(request,session)=>{Interlocked.Increment(ref f.RoundCount);f.Session.IsFaulted=true;return 0;};
                f.Session.BeforeDispose=()=>{cleanupEntered.Set();if(!releaseCleanup.WaitOne(5000))throw new Exception("fake cleanup not released");};
                f.Start();f.WaitReady();string failed=f.Submit("faulted");f.Wait(()=>cleanupEntered.WaitOne(0),"fault cleanup entered");
                string queued=f.Submit("must-not-run");
                check(!File.Exists(Path.Combine(failed,"session-result.txt")),"fault result is withheld while native cleanup is still running");
                releaseCleanup.Set();f.WaitExit();
                check(f.Exit!=0 && f.RoundCount==1 && File.ReadAllText(Path.Combine(failed,"session-result.txt"))!="0" &&
                    !File.Exists(Path.Combine(queued,"session-result.txt")),"fault cleans then reports failure; queued next round is never executed");
                check(f.Session.Disposes==1 && File.Exists(Path.Combine(f.Directory,"closed.flag")),"fault history remains failure even when cleanup succeeds");
                cleanupEntered.Dispose();releaseCleanup.Dispose();
            }
            using(var f=new Fixture())
            {
                f.Round=(request,session)=>{Interlocked.Increment(ref f.RoundCount);return 1;};
                f.Session.BeforeDispose=()=>{throw new IOException("injected cleanup failure");};
                f.Start();f.WaitReady();string failed=f.Submit("cleanup-failure");f.WaitExit();
                check(f.Exit!=0 && File.Exists(Path.Combine(f.Directory,"cleanup-error.txt")) &&
                    !File.Exists(Path.Combine(f.Directory,"closed.flag")) && File.ReadAllText(Path.Combine(failed,"session-result.txt"))!="0",
                    "cleanup failure cannot masquerade as closed or successful round");
                check(f.Lease.Disposes==0,"failed native cleanup retains exclusive ownership until worker exit");
            }
            using(var f=new Fixture())
            {
                f.Session.FaultOnPoll=true;f.Start();f.WaitExit();
                check(f.Exit!=0 && f.RoundCount==0 && f.Session.Disposes==1,"idle P0.4/communication fault cleans without executing output round");
                var unknown=FactorySessionFaultStore.Read(f.Directory,f.Session.SessionId);
                check(unknown.Category==FactorySessionFaultCategory.Unknown && unknown.Device=="Unknown","unsourced idle failure is not guessed to be ART");
            }
            foreach(string faultEvent in new[]{"LINK_ART_LOW","LINK_ART_ERROR","LINK_PICO_ERROR","P04_IDLE_RETRY_FAILED","P04_IDLE_TERMINAL_TIMEOUT"})
            using(var f=new Fixture())
            {
                f.Session.FaultOnPoll=true;f.Session.FaultEvent=faultEvent;f.Start();f.WaitExit();
                var recorded=FactorySessionFaultStore.Read(f.Directory,f.Session.SessionId);
                var expected=faultEvent=="LINK_ART_LOW"?FactorySessionFaultCategory.EmergencyInputLow:
                    faultEvent=="LINK_ART_ERROR"?FactorySessionFaultCategory.DriverError:
                    faultEvent=="LINK_PICO_ERROR"?FactorySessionFaultCategory.PicoError:FactorySessionFaultCategory.ReadTimeout;
                check(recorded.Category==expected && recorded.Phase=="Idle" && recorded.Device==(faultEvent=="LINK_PICO_ERROR"?"Pico":"ART"),
                    "real loop preserves structured idle cause across cleanup: "+faultEvent);
                check(File.Exists(Path.Combine(f.Directory,"closed.flag")) && f.RoundCount==0,"classified fault does not change safe cleanup or execute a round");
            }
            using(var f=new Fixture())
            {
                f.Session.FaultOnPoll=true;f.Session.FaultEvent="LINK_ART_ERROR";f.Session.PollError=new TimeoutException("injected typed read timeout");
                f.Start();f.WaitExit();
                check(FactorySessionFaultStore.Read(f.Directory,f.Session.SessionId).Category==FactorySessionFaultCategory.ReadTimeout,
                    "original typed read exception distinguishes timeout from generic driver error");
            }
            using(var f=new Fixture())
            {
                f.Session.FaultOnPoll=true;f.Session.FaultEvent="LINK_ART_LOW";f.Session.BeforeDispose=()=>{throw new IOException("injected clear failure after LOW");};
                f.Start();f.WaitExit();var recorded=FactorySessionFaultStore.Read(f.Directory,f.Session.SessionId);
                check(recorded.Category==FactorySessionFaultCategory.CleanupError && recorded.PriorDetail!=null && !File.Exists(Path.Combine(f.Directory,"closed.flag")),
                    "cleanup uncertainty wins over LOW; neither cause nor failed ownership cleanup is hidden");
            }
            foreach(bool wrongIdentity in new[]{false,true})
            using(var f=new Fixture())
            {
                f.Start();f.WaitReady();f.Submit("invalid",wrongIdentity? (Action<FactorySettings>)(s=>s.Input.PicoSerial="OTHER/0002") : null,
                    wrongIdentity?null:(Action<FactorySessionRequest>)(r=>r.SettingsPath=Path.Combine(f.Directory,"session-settings.xml")));
                f.WaitExit();
                check(f.Exit!=0 && f.RoundCount==0 && f.Session.Disposes==1,
                    wrongIdentity?"mismatched exact identity rejects before fake round invocation":"mismatched request settings path rejects before round invocation");
            }
            using(var f=new Fixture())
            {
                f.Start();f.WaitReady();string id=Guid.NewGuid().ToString("N");string first=f.Submit("one",null,r=>r.Id=id);f.WaitResult(first);
                f.Submit("replayed",null,r=>r.Id=id);f.WaitExit();
                check(f.Exit!=0 && f.RoundCount==1,"accepted GUID cannot be replayed as another round");
            }
            using(var f=new Fixture())
            {
                var entered=new ManualResetEvent(false);
                f.Round=(request,session)=>
                {
                    Interlocked.Increment(ref f.RoundCount);entered.Set();var timer=Stopwatch.StartNew();
                    while(!File.Exists(Path.Combine(request.Directory,"cancel.flag")))
                    {if(timer.ElapsedMilliseconds>5000)throw new Exception("session stop did not reach active round");Thread.Sleep(10);}
                    return 1;
                };
                f.Start();f.WaitReady();string round=f.Submit("cancelled");f.Wait(()=>entered.WaitOne(0),"active round entered");f.Close();f.WaitExit();
                check(f.Exit!=0 && File.Exists(Path.Combine(round,"cancel.flag")) && f.Session.Disposes==1,
                    "session close cooperatively cancels an active round and waits for its cleanup");
                entered.Dispose();
            }
            foreach(string scenario in new[]{"healthy","di-error","di-unknown","cleanup-fault","missing-stop","nonzero","untyped"})
            using(var f=new Fixture())
            {
                FactorySessionRequest expected=null;int pid=Process.GetCurrentProcess().Id;
                f.Round=(request,session)=>{
                    Interlocked.Increment(ref f.RoundCount);request.InputStopCompleted=scenario!="untyped";
                    if(scenario=="di-error")f.Session.PollError=new IOException("injected current DI failure");
                    if(scenario=="di-unknown")f.Session.StopHealthUnknown=true;
                    if(scenario=="cleanup-fault")f.Session.IsFaulted=true;
                    return FactoryInputStopEvidence.ResultCode;
                };
                f.Start();f.WaitReady();
                string round=f.Submit("input-stop-"+scenario,s=>s.TargetKv=scenario=="nonzero"?.5:0,r=>{
                    r.InputStopContext=FactoryInputStopEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid);expected=r;
                    if(scenario!="missing-stop")File.WriteAllText(Path.Combine(r.Directory,FactoryInputStopEvidence.RequestStopFile),r.Id);
                });
                f.WaitResult(round);
                bool receipt=File.Exists(Path.Combine(round,FactoryInputStopEvidence.ProofFile));
                check(receipt==(scenario=="healthy"),"only healthy current zero-input typed stop publishes receipt: "+scenario);
                if(scenario=="healthy"){
                    check(File.ReadAllText(Path.Combine(round,"session-result.txt"))=="3"&&f.Session.Disposes==0,"input-stop result is distinct from successful acquisition and retains idle owner");
                    check(FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"current live owner accepts exact receipt");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,false,3),"session death invalidates receipt");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid+1,true,3),"replacement worker PID rejects old proof");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,"new-session",pid,pid,true,3),"replacement session rejects old proof");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid+1,pid,true,3),"different parent rejected");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"generic failed result cannot authorize release");
                    string originalNonce=expected.InputStopContext.Nonce;expected.InputStopContext.Nonce=Guid.NewGuid().ToString("N");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"old proof nonce replay rejected");expected.InputStopContext.Nonce=originalNonce;
                    string originalId=expected.Id;expected.Id=Guid.NewGuid().ToString("N");
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"different request cannot reuse proof");expected.Id=originalId;
                    string originalSettings=File.ReadAllText(expected.SettingsPath);File.AppendAllText(expected.SettingsPath," ");
                    check(FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"archive changes retain exact original input-stop identity");File.WriteAllText(expected.SettingsPath,originalSettings);
                    string originalDirectory=expected.Directory;expected.Directory=f.Directory;
                    check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"another record directory rejected");expected.Directory=originalDirectory;
                    f.Close();f.WaitExit();check(f.Exit==0,"explicit close follows stopped input session normally");
                }else{f.WaitExit();check(f.Exit!=0,"unproven/safety-fault result remains failure: "+scenario);}
            }
            var invalidRequest=new FactorySessionRequest {Id="not-guid",Command="Pressure",Directory=Path.GetTempPath(),SettingsPath=Path.Combine(Path.GetTempPath(),"factory-settings.xml")};
            check(Expect(()=>FactoryResidentDeviceServer.ValidateRequest(invalidRequest)) is InvalidDataException,"invalid GUID cannot choose acceptance filename");
            invalidRequest.Id=Guid.NewGuid().ToString("N");invalidRequest.Command="Discover";
            check(Expect(()=>FactoryResidentDeviceServer.ValidateRequest(invalidRequest)) is InvalidDataException,"session cannot dispatch unrestricted worker command");
            check(Expect(()=>FactoryResidentDeviceServer.RequireAbsolute("relative\\factory-settings.xml")) is InvalidDataException,"relative protocol paths rejected");
            return "Resident server: "+checks+" injected real-loop checks PASS; two rounds/late status/fault/cleanup/cancel/replay/identity/path; HardwareAccessed=False";
        }

        internal static string RunFailedRelease()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Failed-round release: "+why);checks++;};
            int pid=Process.GetCurrentProcess().Id;
            foreach(string scenario in new[]{"clean","faulted","archive-settings","end-failure","dispose-failure","lease-failure","no-context","unaccepted","input-changed","snapshot-changed"})
            using(var f=new Fixture())
            {
                FactorySessionRequest expected=null;
                f.Round=(request,session)=>{
                    Interlocked.Increment(ref f.RoundCount);
                    if(scenario=="archive-settings"){
                        var archived=request.FrozenSettings.Clone();archived.TargetKv+=.25;
                        // Exercise the actual managed archive writer that caused
                        // the integration failure, not a synthetic hash override.
                        Program.SaveResult(new FactoryResult {Settings=archived,Kind=FactoryRunKind.Pressure},request.Directory,true);
                    }
                    if(scenario=="faulted"){f.Session.IsFaulted=true;return 0;}
                    throw new InvalidOperationException("original injected acquisition failure");
                };
                if(scenario=="end-failure")f.Session.BeforeEndRound=()=>{throw new IOException("injected Stop/buffer-release failure");};
                if(scenario=="dispose-failure")f.Session.BeforeDispose=()=>{throw new IOException("injected native Close failure");};
                if(scenario=="lease-failure")f.Lease.BeforeDispose=()=>{throw new IOException("injected lease release failure");};
                f.Start();f.WaitReady();
                string record=f.Submit("failed-release-"+scenario,scenario=="unaccepted"?(Action<FactorySettings>)(s=>s.Input.PicoSerial="OTHER/0002"):null,r=>{
                    expected=r;if(scenario!="no-context")r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid);
                    if(scenario=="input-changed")File.AppendAllText(r.SettingsPath," ");
                    if(scenario=="snapshot-changed")File.AppendAllText(r.FailureReleaseContext.InputSettingsPath," ");
                });
                f.WaitExit();bool complete=scenario=="clean"||scenario=="faulted"||scenario=="archive-settings";
                check(f.Exit!=0,"failure remains unsuccessful: "+scenario);
                check(File.Exists(Path.Combine(record,FactoryFailedRoundReleaseEvidence.ProofFile))==complete,"receipt only follows accepted round with all native cleanup returned: "+scenario);
                check(FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1)==complete,"proof exact accepted identity and cleanup outcome: "+scenario);
                if(scenario=="unaccepted"||scenario=="input-changed"||scenario=="snapshot-changed")
                    check(f.RoundCount==0&&!File.Exists(Path.Combine(record,"session-result.txt")),"identity/input rejection before acceptance cannot execute or release another pending round: "+scenario);
                if(!complete)continue;
                if(scenario=="archive-settings")check(File.Exists(Path.Combine(record,"partial-result.xml"))&&
                    FactorySettings.Load(expected.SettingsPath).TargetKv!=FactorySettings.Load(expected.FailureReleaseContext.InputSettingsPath).TargetKv,
                    "actual SaveResult rewrites archived settings while original submitted snapshot is retained");
                check(f.Session.Ends==1&&f.Session.Disposes==1&&f.Lease.Disposes==1,"proof records actual owner-thread EndRound, native Dispose and lease return");
                check(File.ReadAllText(Path.Combine(record,"session-result.txt"))!="0","receipt does not turn failed result into success");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,false,1),"proof alone before worker exit cannot release cadence");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,0),"successful result is not failed-round proof");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,"replacement-session",pid,pid,true,1),"replacement session cannot consume receipt");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid+1,pid,true,1),"replacement parent cannot consume receipt");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid+1,true,1),"replacement worker cannot consume receipt");
                string nonce=expected.FailureReleaseContext.Nonce;expected.FailureReleaseContext.Nonce=Guid.NewGuid().ToString("N");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"another round nonce cannot reuse receipt");expected.FailureReleaseContext.Nonce=nonce;
                string id=expected.Id;expected.Id=Guid.NewGuid().ToString("N");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"another accepted ID cannot reuse receipt");expected.Id=id;
                string settings=File.ReadAllText(expected.SettingsPath);File.AppendAllText(expected.SettingsPath," ");
                check(FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"later archive settings changes do not invalidate original accepted input receipt");File.WriteAllText(expected.SettingsPath,settings);
                string snapshot=expected.FailureReleaseContext.InputSettingsPath;byte[] snapshotBytes=File.ReadAllBytes(snapshot);File.AppendAllText(snapshot," ");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"changed immutable input snapshot rejects receipt");File.WriteAllBytes(snapshot,snapshotBytes);
                File.Delete(snapshot);
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"missing immutable input snapshot rejects receipt");File.WriteAllBytes(snapshot,snapshotBytes);
                string accepted=Path.Combine(f.Directory,"request."+id+".accepted.xml"),acceptedText=File.ReadAllText(accepted);File.AppendAllText(accepted," ");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"changed accepted request bytes invalidate receipt");File.WriteAllText(accepted,acceptedText);
                string closed=Path.Combine(f.Directory,"closed.flag"),closedText=File.ReadAllText(closed);File.Delete(closed);
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"incomplete session closure fails closed");File.WriteAllText(closed,closedText);
                foreach(string marker in new[]{"cleanup-error.txt",FactorySessionFaultStore.CleanupFile,"fault-record-error.txt"}){
                    string path=Path.Combine(f.Directory,marker);File.WriteAllText(path,"injected cleanup uncertainty");
                    check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"cleanup uncertainty overrides receipt: "+marker);File.Delete(path);
                }
                string proofPath=Path.Combine(record,FactoryFailedRoundReleaseEvidence.ProofFile),proofText=File.ReadAllText(proofPath);File.Delete(proofPath);
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"exit plus closed marker alone never authorizes release");
                File.WriteAllText(Path.Combine(record,FactoryInputStopEvidence.ProofFile),proofText);
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"input-only stopped receipt cannot substitute failure release");
                File.WriteAllText(proofPath,"<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///nonexistent'>]><FactoryFailedRoundReleaseRecord>&x;</FactoryFailedRoundReleaseRecord>");
                check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"DTD or malformed proof rejected without external expansion");
                foreach(Action<FactoryFailedRoundReleaseRecord> corrupt in new Action<FactoryFailedRoundReleaseRecord>[] {
                    p=>p.RoundEndReturned=false,p=>p.NativeDisposeReturned=false,p=>p.LeaseReleaseReturned=false,
                    p=>p.Outcome="StoppedInputOnly",p=>p.SettingsSHA256="wrong",p=>p.AcceptedRequestSHA256="wrong",p=>p.InputSettingsPath=expected.SettingsPath,
                    p=>p.Directory=f.Directory,p=>p.Schema="HVX.FailedRoundRelease/1"}){
                    FactoryFailedRoundReleaseRecord proof;using(var reader=new StringReader(proofText))proof=(FactoryFailedRoundReleaseRecord)new XmlSerializer(typeof(FactoryFailedRoundReleaseRecord)).Deserialize(reader);
                    corrupt(proof);using(var writer=new StreamWriter(proofPath,false))new XmlSerializer(typeof(FactoryFailedRoundReleaseRecord)).Serialize(writer,proof);
                    check(!FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"incomplete/mismatched structured receipt cannot release");
                }
                File.WriteAllText(proofPath,proofText);
                check(FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"restored exact complete proof verified");
            }
            using(var f=new Fixture())using(var entered=new ManualResetEvent(false))using(var finish=new ManualResetEvent(false)){
                FactorySessionRequest expected=null;f.Round=(r,s)=>{throw new InvalidOperationException("original failure retained");};
                f.Session.BeforeDispose=()=>{entered.Set();if(!finish.WaitOne(5000))throw new IOException("fake cleanup not released");};
                f.Start();f.WaitReady();string record=f.Submit("cleanup-in-progress",null,r=>{expected=r;r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid);});
                f.Wait(()=>entered.WaitOne(0),"failed-round native cleanup entered");
                check(!File.Exists(Path.Combine(record,"session-result.txt"))&&!File.Exists(Path.Combine(record,FactoryFailedRoundReleaseEvidence.ProofFile)),"unfinished native cleanup cannot publish result or proof");
                finish.Set();f.WaitExit();check(FactoryFailedRoundReleaseEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,1),"proof appears only after actual cleanup returns");
            }
            var original=new InvalidOperationException("original STEP_HV_DELAY_ERROR");var released=new FactoryFailedRoundReleasedException(original);
            check(released.CaptureResourcesReleased&&released.ReleaseEvidenceCode=="VerifiedOwnedFailedRoundNativeCleanup"&&Object.ReferenceEquals(released.InnerException,original)&&released.Message==original.Message,"typed metadata preserves original failure, not success or physical discharge");
            return "Failed-round release: "+checks+" injected real-loop checks PASS; accepted identity/Stop-buffer cleanup/native Close/lease/no replay; HardwareAccessed=False";
        }

        internal static string RunInputStopSnapshots()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Input-stop snapshot: "+why);checks++;};
            int pid=Process.GetCurrentProcess().Id;
            foreach(string scenario in new[]{"clean","archive-settings","stop-failure","di-error","di-unknown","untyped","input-changed","snapshot-changed"})
            using(var f=new Fixture())
            {
                FactorySessionRequest expected=null;
                if(scenario=="stop-failure")f.Session.BeforeEndRound=()=>{throw new IOException("injected Stop/buffer failure");};
                f.Round=(request,session)=>{
                    Interlocked.Increment(ref f.RoundCount);session.EndRound();request.InputStopCompleted=scenario!="untyped";
                    if(scenario=="di-error")f.Session.PollError=new IOException("injected DI read failure");
                    if(scenario=="di-unknown")f.Session.StopHealthUnknown=true;
                    if(scenario=="archive-settings"){
                        var archived=request.FrozenSettings.Clone();archived.ZeroBackgroundLimitV+=1;
                        Program.SaveResult(new FactoryResult {Settings=archived,Kind=FactoryRunKind.Pressure,ReadOnlyAcquisition=true},request.Directory,true);
                    }
                    return FactoryInputStopEvidence.ResultCode;
                };
                f.Start();f.WaitReady();
                string record=f.Submit("input-stop-snapshot-"+scenario,s=>s.TargetKv=0,r=>{
                    expected=r;r.InputStopContext=FactoryInputStopEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid);
                    // Production creates both contexts. They must share, not
                    // overwrite, one request-owned immutable input snapshot.
                    r.FailureReleaseContext=FactoryFailedRoundReleaseEvidence.Create(r,f.Directory,f.Session.SessionId,pid,pid);
                    File.WriteAllText(Path.Combine(r.Directory,FactoryInputStopEvidence.RequestStopFile),r.Id);
                    if(scenario=="input-changed")File.AppendAllText(r.SettingsPath," ");
                    if(scenario=="snapshot-changed")File.AppendAllText(r.InputStopContext.InputSettingsPath," ");
                });
                bool complete=scenario=="clean"||scenario=="archive-settings";
                if(complete)f.WaitResult(record);else f.WaitExit();
                check(File.Exists(Path.Combine(record,FactoryInputStopEvidence.ProofFile))==complete,"only typed, healthy, same accepted input stop publishes: "+scenario);
                check(FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,complete,3)==complete,"receipt binds immutable accepted input: "+scenario);
                check(expected.InputStopContext.InputSettingsPath==expected.FailureReleaseContext.InputSettingsPath&&
                    expected.InputStopContext.SettingsSHA256==expected.FailureReleaseContext.SettingsSHA256,"both receipt contexts bind the same preserved input bytes: "+scenario);
                if(!complete){check(f.Exit!=0,"unsafe or unaccepted input remains failure: "+scenario);continue;}
                check(f.Session.Ends==1&&f.Session.Disposes==0&&File.ReadAllText(Path.Combine(record,"session-result.txt"))=="3","typed Stop retains the already-open input-only session, not acquisition success");
                if(scenario=="archive-settings")check(File.Exists(Path.Combine(record,"partial-result.xml"))&&
                    FactorySettings.Load(expected.SettingsPath).ZeroBackgroundLimitV!=FactorySettings.Load(expected.InputStopContext.InputSettingsPath).ZeroBackgroundLimitV,
                    "actual SaveResult rewrite no longer invalidates stopped-input proof");
                string snapshot=expected.InputStopContext.InputSettingsPath;byte[] bytes=File.ReadAllBytes(snapshot);File.AppendAllText(snapshot," ");
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"changed input snapshot rejects stopped-input proof");File.WriteAllBytes(snapshot,bytes);
                string accepted=Path.Combine(f.Directory,"request."+expected.Id+".accepted.xml");bytes=File.ReadAllBytes(accepted);File.AppendAllText(accepted," ");
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"changed accepted request bytes reject stopped-input proof");File.WriteAllBytes(accepted,bytes);
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid+1,true,3),"replacement worker rejects stopped-input proof");
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,"replacement-session",pid,pid,true,3),"replacement session rejects stopped-input proof");
                string stop=Path.Combine(record,FactoryInputStopEvidence.RequestStopFile);File.WriteAllText(stop,Guid.NewGuid().ToString("N"));
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"another round stop ID cannot release input");File.WriteAllText(stop,expected.Id);
                string proof=Path.Combine(record,FactoryInputStopEvidence.ProofFile);bytes=File.ReadAllBytes(proof);
                FactoryInputStopRecord p;using(var stream=File.OpenRead(proof))p=(FactoryInputStopRecord)new XmlSerializer(typeof(FactoryInputStopRecord)).Deserialize(stream);
                p.Schema="HVX.InputStop/1";using(var stream=File.Create(proof))new XmlSerializer(typeof(FactoryInputStopRecord)).Serialize(stream,p);
                check(!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"legacy mutable-hash receipt cannot substitute snapshot-bound proof");File.WriteAllBytes(proof,bytes);
                check(FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,true,3),"restored exact same-live-owner proof verifies");
                f.Close();f.WaitExit();check(f.Exit==0&&!FactoryInputStopEvidence.Verify(expected,f.Directory,f.Session.SessionId,pid,pid,false,3),"explicit close still kills input-stop permission; no automatic reopening");
            }
            return "Input-stop snapshot: "+checks+" injected real-loop checks PASS; accepted immutable input/actual SaveResult/Stop-DI failure/no replay; HardwareAccessed=False";
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Directory=Path.Combine(FixtureRoot,"Xiongxin-Factory-ResidentTests-"+Guid.NewGuid().ToString("N"));
            internal readonly FakeSession Session=new FakeSession();internal readonly FakeLease Lease=new FakeLease();
            internal Func<FactorySessionRequest,IFactoryResidentSession,int> Round;
            internal int Exit=-999,RoundCount;internal bool UnifiedAdapter;private Thread worker;private Exception error;
            internal Fixture(){System.IO.Directory.CreateDirectory(Directory);Settings().Save(Path.Combine(Directory,"session-settings.xml"));Round=(r,s)=>{Interlocked.Increment(ref RoundCount);Session.EndRound();return 0;};}
            internal void Start()
            {
                Beat();worker=new Thread(()=>{try{
                    string[] args={"--device-session",Path.Combine(Directory,"session-settings.xml"),Directory,Process.GetCurrentProcess().Id.ToString()};
                    Exit=UnifiedAdapter?UnifiedWorkerEntry.RunResidentCore(args,()=>new UnifiedWorkerEntry.PressureSession(Session),Round,()=>Lease):
                        FactoryResidentDeviceServer.RunCore(args,()=>Session,Round,()=>Lease);
                }catch(Exception ex){error=ex;}}){IsBackground=true};worker.Start();
            }
            internal void WaitReady(){Wait(()=>File.Exists(Path.Combine(Directory,"ready.flag")),"ready flag");}
            internal string Submit(string name,Action<FactorySettings> change=null,Action<FactorySessionRequest> rewrite=null)
            {
                string record=Path.Combine(Directory,name);System.IO.Directory.CreateDirectory(record);var settings=Settings();if(change!=null)change(settings);
                string path=Path.Combine(record,"factory-settings.xml");settings.Save(path);
                var request=new FactorySessionRequest {Id=Guid.NewGuid().ToString("N"),Command="Pressure",SettingsPath=path,Directory=record};if(rewrite!=null)rewrite(request);
                string pending=Path.Combine(Directory,"request.pending.xml");using(var stream=new FileStream(pending,FileMode.CreateNew,FileAccess.Write))new XmlSerializer(typeof(FactorySessionRequest)).Serialize(stream,request);
                File.Move(pending,Path.Combine(Directory,"request.xml"));return record;
            }
            internal void WaitResult(string record){Wait(()=>File.Exists(Path.Combine(record,"session-result.txt")),"round result");}
            internal void WaitExit(){Wait(()=>!worker.IsAlive,"worker exit");if(error!=null)throw error;}
            internal void Wait(Func<bool> condition,string why)
            {
                var watch=Stopwatch.StartNew();while(!condition())
                {if(error!=null)throw error;if(worker!=null&&!worker.IsAlive){string failure=Path.Combine(Directory,"error.txt");throw new InvalidOperationException("Offline server fixture exited before "+why+"; Exit="+Exit+"; "+(File.Exists(failure)?File.ReadAllText(failure):"no failure log"));}if(watch.ElapsedMilliseconds>6000)throw new TimeoutException("Offline server fixture: "+why+"; "+Directory);Beat();Thread.Sleep(10);}
            }
            private void Beat(){File.WriteAllText(Path.Combine(Directory,"session.heartbeat"),"offline parent alive");}
            internal void Close(){File.WriteAllText(Path.Combine(Directory,"close.flag"),"offline explicit close");}
            public void Dispose(){Close();if(worker!=null && worker.IsAlive)worker.Join(6000);/* Retain tiny fixture journals for review; no user file deletion. */}
            private static FactorySettings Settings(){var s=new FactoryTests.Fixture().Settings;s.Simulation=false;s.VmonAfterHvStartup=true;s.IndependentDischargeConfirmed=s.FeedbackWiringConfirmed=true;return s;}
        }
        private sealed class FakeSession : IFactoryResidentSession,IFactoryInputStopSessionEvidence,IFactoryEmergencyLatchedSessionEvidence
        {
            internal int Opens,Disposes,Detaches,Ends;internal bool IsFaulted,FaultOnPoll;internal Action BeforeOpen,BeforeDispose,BeforeEndRound;internal Action<string,string> Report;
            internal string FaultEvent;internal Exception PollError;
            internal bool StopHealthUnknown;internal Action BeforeCanReuse;
            internal bool Latched;internal FactoryEmergencyMonitor InputMonitor;
            public bool EmergencyOutputLatched {get{return Latched||InputMonitor!=null&&InputMonitor.OutputStopLatched;}}
            public bool CanRetainEmergencyDisplaySession {get{return EmergencyOutputLatched&&!IsFaulted&&Disposes==0&&
                StopFacts!=null&&StopFacts.Complete&&(InputMonitor==null||!InputMonitor.HasReadFailure);}}
            internal FactoryHealthyRoundStopFacts StopFacts;
            public FactoryHealthyRoundStopFacts HealthyRoundStopFacts {get{return StopFacts;}}
            public void ResetHealthyRoundStopEvidence(){StopFacts=null;}
            private int owner;
            public string SessionId {get{return "OFFLINE-FAKE-SESSION";}}
            public bool Faulted {get{return IsFaulted;}}
            public bool CanReuseSession {get{if(BeforeCanReuse!=null)BeforeCanReuse();return !IsFaulted && Disposes==0&&!EmergencyOutputLatched;}}
            public bool InputStopHealthy {get{return CanReuseSession&&!StopHealthUnknown;}}
            public Action<string,string> ConnectionReport {set{Report=value;}}
            public void OpenSession(FactorySettings settings){owner=Thread.CurrentThread.ManagedThreadId;Opens++;if(BeforeOpen!=null)BeforeOpen();}
            public void PollIdle(){AssertOwner();if(InputMonitor!=null)InputMonitor.ReadIdleReleased();if(FaultOnPoll){if(FaultEvent!=null && Report!=null)Report(FaultEvent,FactoryConnectionWatch.Stamp("injected source observation"));IsFaulted=true;}if(PollError!=null)throw PollError;}
            public void EndRound(){AssertOwner();Ends++;if(BeforeEndRound!=null)BeforeEndRound();StopFacts=new FactoryHealthyRoundStopFacts(true,true,true,true,true);}
            public void DetachRoundCallbacks(){AssertOwner();Detaches++;}
            public void Dispose(){AssertOwner();Disposes++;if(BeforeDispose!=null)BeforeDispose();if(Report!=null){Report("LINK_ART_CLOSED","fake cleanup");Report("LINK_PICO_CLOSED","fake cleanup");}}
            private void AssertOwner(){if(owner!=Thread.CurrentThread.ManagedThreadId)throw new Exception("SDK session left its owner thread");}
        }
        private sealed class FakeLease : IDisposable{internal int Disposes;internal Action BeforeDispose;public void Dispose(){Disposes++;if(BeforeDispose!=null)BeforeDispose();}}
        private static Exception Expect(Action action){try{action();}catch(Exception ex){return ex;}throw new Exception("Expected resident protocol rejection");}
    }

    internal static class FactoryDeviceLeaseTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Device lease: "+why);checks++;};
            string name="Local\\Xiongxin-Factory-Offline-Lease-"+Guid.NewGuid().ToString("N");
            var lease=FactoryDeviceLease.AcquireNamed(name);
            check(Expect(()=>FactoryDeviceLease.AcquireNamed(name)) is InvalidOperationException,"same-thread mutex recursion does not create a second device owner");
            Exception otherThread=null;
            var other=new Thread(()=>{try{lease.Dispose();}catch(Exception ex){otherThread=ex;}});other.Start();other.Join();
            check(otherThread is InvalidOperationException,"non-owner thread cannot release exclusive session");
            lease.Dispose();using(FactoryDeviceLease.AcquireNamed(name))check(true,"normal owner release permits a later explicit acquisition");
            string busyName=name+"-busy";
            using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                var holder=new Thread(()=>{using(var kernel=new Mutex(false,busyName)){kernel.WaitOne();held.Set();release.WaitOne(3000);kernel.ReleaseMutex();}});
                holder.Start();held.WaitOne(3000);
                check(Expect(()=>FactoryDeviceLease.AcquireNamed(busyName)) is InvalidOperationException,
                    "kernel mutex owned outside the process-local bookkeeping rejects immediately");
                release.Set();holder.Join();
            }
            string abandonedName=name+"-abandoned";Mutex orphan=null;
            var abandon=new Thread(()=>{orphan=new Mutex(false,abandonedName);orphan.WaitOne();});abandon.Start();abandon.Join();
            var failure=Expect(()=>FactoryDeviceLease.AcquireNamed(abandonedName));
            check(failure is InvalidOperationException && failure.InnerException is AbandonedMutexException && failure.Message.Contains("未知"),
                "abandoned owner is explicit unknown hardware state, not successful acquisition");
            if(orphan!=null)orphan.Dispose();
            var cleanLease=new CountingLease();int reports=0;
            FactoryDeviceLease.ReleaseAfterCleanup(cleanLease,false,_=>reports++);
            check(cleanLease.Disposes==1 && reports==0,"successful manual cleanup releases the owner lease once");
            var failedLease=new CountingLease();
            FactoryDeviceLease.ReleaseAfterCleanup(failedLease,true,_=>reports++);
            check(failedLease.Disposes==0 && reports==1,"failed manual cleanup reports uncertainty without releasing the lease");
            var reportError=new IOException("injected retained-lease log failure");
            check(Object.ReferenceEquals(Expect(()=>FactoryDeviceLease.ReleaseAfterCleanup(failedLease,true,_=>{throw reportError;})),reportError) && failedLease.Disposes==0,
                "retained-lease logging failure cannot fall through to successful unlock");
            FactoryDeviceLease.ReleaseAfterCleanup(null,true,_=>reports++);
            check(reports==1,"missing lease never fabricates an acquisition or release");
            return "Device lease: "+checks+" unique named-mutex managed checks PASS; HardwareAccessed=False; legacy versions without this lock are not protected";
        }
        private sealed class CountingLease : IDisposable {internal int Disposes;public void Dispose(){Disposes++;}}
        private static Exception Expect(Action action){try{action();}catch(Exception ex){return ex;}throw new Exception("Expected device lease rejection");}
    }
}
