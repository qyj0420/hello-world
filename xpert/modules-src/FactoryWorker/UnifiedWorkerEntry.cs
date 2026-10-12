using System;
using System.Diagnostics;
using System.IO;

namespace Xiongxin.FactoryTest
{
    // The unified desktop exposes only supervised pressure/session paths.
    // Factory calibration, discovery, debug forms and manual benches remain
    // source references, not standalone entry points in this executable.
    internal static class UnifiedWorkerEntry
    {
        [STAThread]
        internal static int Main(string[] args)
        {
            if (!AcceptsArguments(args)) return 2;
            bool resident=args[0]=="--device-session",residentEntered=false;
            int parent=0;string directory=null;
            try
            {
                parent = Int32.Parse(args[resident?3:4]);
                directory = Path.GetFullPath(args[resident?2:3]);
                try {using (var process = Process.GetProcessById(parent)) if (process.HasExited)
                    return RejectBeforeResidentEntry(args,resident,directory,parent,UnifiedResidentLaunchEvidence.ParentUnavailable);}
                catch(ArgumentException error) {return RejectBeforeResidentEntry(args,resident,directory,parent,UnifiedResidentLaunchEvidence.ParentUnavailable,error);}
                string lease = Path.Combine(directory,resident?"session.heartbeat":"supervisor.heartbeat");
                if (!File.Exists(lease)) return RejectBeforeResidentEntry(args,resident,directory,parent,UnifiedResidentLaunchEvidence.HeartbeatInvalid);
                if (File.Exists(Path.Combine(directory,resident?"close.flag":"cancel.flag"))) {
                    // Only this known cancellation branch can prove that the
                    // one-shot wrapper returned before Program/SDK entry.
                    if(!resident)UnifiedWorkerLaunchEvidence.WriteCanceledBeforeNative(directory,args[2],parent,Process.GetCurrentProcess().Id);
                    return RejectBeforeResidentEntry(args,resident,directory,parent,UnifiedResidentLaunchEvidence.Canceled);
                }
                double age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(lease)).TotalSeconds;
                if (age < -0.25 || age > 2) return RejectBeforeResidentEntry(args,resident,directory,parent,UnifiedResidentLaunchEvidence.HeartbeatInvalid);
                if(!resident)return Program.Main(args);
                // Persist the conservative boundary BEFORE any server/SDK-capable
                // delegate. Missing/invalid startup requests cannot enter either.
                UnifiedResidentLaunchEvidence.MarkServerEntry(directory,args[1],parent,Process.GetCurrentProcess().Id,typeof(UnifiedWorkerEntry).Assembly.Location);
                residentEntered=true;
                return RunResidentCore(args,()=>new PressureSession(),(request,session)=>RunPressureRound(
                    request,((PressureSession)session).Backend,args[3],Program.Worker),()=>FactoryDeviceLease.Acquire());
            }
            catch(Exception error) {
                if(resident&&!residentEntered&&directory!=null&&parent>0)
                    return RejectBeforeResidentEntry(args,true,directory,parent,UnifiedResidentLaunchEvidence.GuardFailed,error);
                return 2;
            }
        }
        static int RejectBeforeResidentEntry(string[] args,bool resident,string directory,int parent,string reason,Exception error=null)
        {
            if(resident)try {
                UnifiedResidentLaunchEvidence.WriteRejectedBeforeNative(directory,args[1],parent,Process.GetCurrentProcess().Id,
                    typeof(UnifiedWorkerEntry).Assembly.Location,reason,error);
            }catch { /* No valid child proof means ownership remains unconfirmed. */ }
            return 2;
        }
        internal static bool AcceptsArguments(string[] args)
        {
            if(args==null)return false;
            bool resident=args.Length==4&&args[0]=="--device-session";
            if(!resident&&(args.Length!=5||args[0]!="--worker"||args[1]!="Pressure"))return false;
            int parent;
            if (!Int32.TryParse(args[resident?3:4], out parent) || parent <= 0 || parent == Process.GetCurrentProcess().Id) return false;
            try
            {
                string settings=args[resident?1:2],directory=args[resident?2:3];
                if (!Path.IsPathRooted(settings) || !Path.IsPathRooted(directory)) return false;
                return String.Equals(Path.GetFullPath(settings), Path.Combine(Path.GetFullPath(directory),resident?"session-settings.xml":"factory-settings.xml"), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        internal static void ValidateSessionRequest(FactorySessionRequest request)
        {
            if(request==null||request.Command!="Pressure")
                throw new InvalidDataException("汇总加压驻留入口只接受Pressure；校准必须由独立校准流程处理。");
        }
        // The wrapper is also tested with managed fakes. Validation precedes the
        // injected worker delegate, so a file request cannot enter calibration.
        internal static int RunResidentCore(string[] args,Func<IFactoryResidentSession> createSession,
            Func<FactorySessionRequest,IFactoryResidentSession,int> runRound,Func<IDisposable> acquireLease)
        {
            return FactoryResidentDeviceServer.RunCore(args,createSession,(request,session)=>{
                ValidateSessionRequest(request);return runRound(request,session);
            },acquireLease);
        }
        // Keep the production callback binding in one place. A completed stop
        // flag is only the notification; the server still requires the native
        // facts and the accepted round identity before publishing a receipt.
        internal static int RunPressureRound(FactorySessionRequest request,NativeFactoryBackend backend,string parent,
            Func<string[],NativeFactoryBackend,FactorySettings,Action<FactoryResult>,Action,Action,Action,int> runWorker)
        {
            ValidateSessionRequest(request);
            if(runWorker==null)throw new ArgumentNullException("runWorker");
            return runWorker(new[]{"--worker","Pressure",request.SettingsPath,request.Directory,parent},backend,
                request.FrozenSettings,result=>request.ArchivedResult=result,()=>request.InputStopCompleted=true,
                ()=>request.HealthyRoundStopCompleted=true,()=>request.EmergencyRoundStopCompleted=true);
        }
        // The restricted entry must forward every capability of the same held
        // owner. A physical emergency latch withdraws output permission, not
        // ownership of its healthy, display-only DI task. The injectable owner
        // keeps the exact production adapter in managed integration tests.
        internal sealed class PressureSession:IFactoryResidentSession,IFactoryInputStopSessionEvidence,IFactoryEmergencyLatchedSessionEvidence
        {
            readonly IFactoryResidentSession owner;
            internal readonly NativeFactoryBackend Backend;
            internal PressureSession():this(new FactoryResidentDeviceServer.NativeSession()){}
            internal PressureSession(IFactoryResidentSession owner)
            {
                this.owner=owner??throw new ArgumentNullException("owner");
                var native=owner as FactoryResidentDeviceServer.NativeSession;
                Backend=native==null?null:native.Backend;
            }
            IFactoryHealthyRoundStopSessionEvidence StopEvidence{get{return owner as IFactoryHealthyRoundStopSessionEvidence;}}
            IFactoryEmergencyLatchedSessionEvidence EmergencyEvidence{get{return owner as IFactoryEmergencyLatchedSessionEvidence;}}
            public string SessionId{get{return owner.SessionId;}}
            public bool Faulted{get{return owner.Faulted;}}
            public bool CanReuseSession{get{return owner.CanReuseSession;}}
            public bool InputStopHealthy{get{var evidence=owner as IFactoryInputStopSessionEvidence;return evidence!=null&&evidence.InputStopHealthy;}}
            public bool EmergencyOutputLatched{get{var evidence=EmergencyEvidence;return evidence!=null&&evidence.EmergencyOutputLatched;}}
            public bool CanRetainEmergencyDisplaySession{get{var evidence=EmergencyEvidence;return evidence!=null&&evidence.CanRetainEmergencyDisplaySession;}}
            public FactoryHealthyRoundStopFacts HealthyRoundStopFacts{get{var evidence=StopEvidence;return evidence==null?null:evidence.HealthyRoundStopFacts;}}
            public void ResetHealthyRoundStopEvidence(){var evidence=StopEvidence;if(evidence!=null)evidence.ResetHealthyRoundStopEvidence();}
            public Action<string,string> ConnectionReport{set{owner.ConnectionReport=value;}}
            public void OpenSession(FactorySettings settings){owner.OpenSession(settings);}
            public void PollIdle(){owner.PollIdle();}
            public void EndRound(){owner.EndRound();}
            public void DetachRoundCallbacks(){owner.DetachRoundCallbacks();}
            public void Dispose(){owner.Dispose();}
        }
    }
}
