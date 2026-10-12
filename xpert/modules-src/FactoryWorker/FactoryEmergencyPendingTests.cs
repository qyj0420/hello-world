using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Managed fake delegates only. Production monitor, snapshot and adapter;
    // never calls Program.Main, an SDK, an output or a real device.
    internal static class FactoryEmergencyPendingTests
    {
        static int checks;
        static readonly BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static void Check(bool condition,string why){++checks;if(!condition)throw new Exception("Pending DI: "+why);}
        static string State(FactoryEmergencyMonitor m,out DateTime utc){string detail;return m.ConnectionState(out detail,out utc);}
        static void Until(Func<bool> value,string why){Check(SpinWait.SpinUntil(value,2000),why);}
        internal static string Run()
        {
            checks=0;HealthyGap();TerminalGap();BadReader();
            return "DI bounded PENDING / production bridge: "+checks+" managed checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        static void HealthyGap()
        {
            int reads=0,faults=0,permission=0;Thread output=null;
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var m=new FactoryEmergencyMonitor(()=>{if(Interlocked.Increment(ref reads)==2){entered.Set();release.WaitOne();}return true;},()=>Interlocked.Increment(ref faults)))
            {
                try {
                    m.Start();Check(entered.WaitOne(2000),"sole fake reader is in-flight");
                    var clock=(Stopwatch)typeof(FactoryEmergencyMonitor).GetField("clock",B).GetValue(m);
                    Until(()=>clock.ElapsedMilliseconds>=126,"fake reader remains inside 500ms total budget");
                    clock.Stop();var gate=typeof(FactoryEmergencyMonitor).GetField("gate",B).GetValue(m);
                    lock(gate)typeof(FactoryEmergencyMonitor).GetField("sampleAt",B).SetValue(m,clock.ElapsedMilliseconds-126);
                    string detail;DateTime old;Check(m.ConnectionState(out detail,out old)=="PENDING"&&detail.Contains("PendingUntilUtc="),"126ms stale HIGH is bounded waiting, not terminal UNKNOWN");
                    output=new Thread(()=>{if(m.ReadReleased())Interlocked.Exchange(ref permission,1);}){IsBackground=true};output.Start();
                    Thread.Sleep(20);Check(Volatile.Read(ref permission)==0&&Volatile.Read(ref faults)==0,"pending never authorizes a new output or signals terminal fault");
                    var backend=new NativeFactoryBackend();var settings=new FactoryTests.Fixture().Settings;settings.Simulation=false;
                    typeof(NativeFactoryBackend).GetField("settings",B).SetValue(backend,settings);
                    typeof(NativeFactoryBackend).GetField("emergencyMonitor",B).SetValue(backend,m);
                    string nativeStage=null,nativeDetail=null;backend.ConnectionReport=(s,d)=>{nativeStage=s;nativeDetail=d;};
                    backend.ReportConnectionSnapshot();Check(nativeStage=="LINK_ART_PENDING"&&nativeDetail.Contains("PendingUntilUtc="),"real native snapshot retains bounded waiting metadata");
                    string mapped=null;UnifiedPressureBridge.AdaptProgress(false,(s,d)=>mapped=s)(nativeStage,nativeDetail);
                    Check(mapped=="EMERGENCY_PENDING","production bridge does not collapse pending into UNKNOWN");
                    UnifiedPressureBridge.AdaptProgress(true,(s,d)=>mapped=s)(nativeStage,nativeDetail);Check(mapped=="SIM_EMERGENCY_PENDING","simulation cannot issue a real pending status");
                    foreach(string stage in new[]{"LINK_ART_UNKNOWN","LINK_ART_ERROR","LINK_ART_CLOSED"}) {
                        UnifiedPressureBridge.AdaptProgress(false,(s,d)=>mapped=s)(stage,"terminal");Check(mapped=="EMERGENCY_UNKNOWN","terminal "+stage+" stays terminal");
                    }
                    clock.Start();Thread.Sleep(44);release.Set();Check(output.Join(1000),"fresh return resolves only the waiting output command");
                    DateTime fresh;Check(State(m,out fresh)=="HIGH"&&fresh>old&&Volatile.Read(ref permission)==1&&Volatile.Read(ref faults)==0,"44ms later actual HIGH restores freshness without fault cancellation");
                    Check(FactoryEmergencyReadPolicy.OutputFreshnessMs==100&&FactoryEmergencyReadPolicy.NoValidDataDeadlineMs==500,"existing output and total deadlines unchanged");
                    Check(m.Stop(600),"healthy owner stops once");DateTime closed;Check(State(m,out closed)=="UNKNOWN","closed owner never reports PENDING");
                } finally {release.Set();if(output!=null)output.Join(600);m.Stop(600);}
            }
        }
        static void TerminalGap()
        {
            int reads=0,faults=0;
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var m=new FactoryEmergencyMonitor(()=>{if(Interlocked.Increment(ref reads)>1){entered.Set();release.WaitOne();}return true;},()=>Interlocked.Increment(ref faults))) {
                try {m.Start();Check(entered.WaitOne(2000),"deadline fake is in-flight");DateTime utc=DateTime.MinValue;
                    Until(()=>State(m,out utc)=="PENDING","predeadline wait is pending");
                    Until(()=>State(m,out utc)=="ERROR","500ms independent observer becomes terminal");
                    Until(()=>Volatile.Read(ref faults)==1,"real deadline callback revoked output exactly once");
                    bool rejected=false;try{m.ReadReleased();}catch(InvalidOperationException){rejected=true;}Check(rejected,"terminal deadline cannot grant permission");
                }finally{release.Set();Check(m.Stop(600),"late read exits before disposal");}
                DateTime after;Check(State(m,out after)=="ERROR"&&Volatile.Read(ref faults)==1,"late HIGH cannot clear deadline fault");
            }
        }
        static void BadReader()
        {
            int calls=0,faults=0;
            using(var m=new FactoryEmergencyMonitor(()=>{if(Interlocked.Increment(ref calls)>1)throw new InvalidOperationException("fake invalid handle -200088");return true;},()=>Interlocked.Increment(ref faults))) {
                m.Start();DateTime utc=DateTime.MinValue;Until(()=>State(m,out utc)=="ERROR","driver errors are never pending");
                Until(()=>Volatile.Read(ref faults)==1,"driver failure signals once");Check(m.Stop(600)&&calls==2,"bad handle never retries or creates another reader");
            }
        }
    }
}
