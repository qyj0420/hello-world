using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Only managed fake DI and pre-SDK native lifecycle helpers are invoked.
    // Actual Open/Initialize/ReadDigital/AI/AO/DO/Pico are never called.
    internal static class FactoryEmergencyDisplayTests
    {
        private static int checks;
        private static void Check(bool ok,string why){if(!ok)throw new Exception("DI display: "+why);++checks;}
        private static void Rejected(Action action,string why){bool failed=false;try{action();}catch{failed=true;}Check(failed,why);}
        private static string State(FactoryEmergencyMonitor monitor,out DateTime utc)
        {string detail;return monitor.ConnectionState(out detail,out utc);}
        private static void Wait(Func<bool> predicate,string why){Check(SpinWait.SpinUntil(predicate,2000),why);}
        internal static string Run()
        {
            checks=0;ActiveLevels();IdleLevels();ReadModeDisplay();ActiveDeadline();BadReads();NativeFaultSeparation();NativePublication();PublisherLifecycle();
            return "Independent DI live display / permanent output latch: "+checks+" managed fake checks PASS; HardwareAccessed=False; SDKCalled=False; physical timing not verified";
        }
        private static void ActiveLevels()
        {
            int level=1,faults=0,reads=0,readerId=0,overlap=0,maxOverlap=0;
            using(var monitor=new FactoryEmergencyMonitor(ms=>{
                int id=Thread.CurrentThread.ManagedThreadId;
                int first=Interlocked.CompareExchange(ref readerId,id,0);
                if(first!=0&&first!=id)throw new Exception("second DI owner");
                int active=Interlocked.Increment(ref overlap);maxOverlap=Math.Max(maxOverlap,active);
                try{Interlocked.Increment(ref reads);return Volatile.Read(ref level)==1;}
                finally{Interlocked.Decrement(ref overlap);}
            },()=>Interlocked.Increment(ref faults),null))
            {
                monitor.Start();DateTime highUtc;Check(State(monitor,out highUtc)=="HIGH"&&monitor.ReadReleased(),"first active HIGH is a fresh permit");
                Volatile.Write(ref level,0);DateTime lowUtc=DateTime.MinValue;
                Wait(()=>State(monitor,out lowUtc)=="LATCHED_LOW","active LOW is published with independent fault flag");
                Check(lowUtc>highUtc&&lowUtc<=DateTime.UtcNow,"LOW records its own actual returned timestamp, not prior HIGH");
                // The cache/permission latch is committed under the monitor
                // gate; its once-only backend callback deliberately runs
                // outside that gate. Observing LOW does not synchronize this
                // separate callback, so wait for it without weakening the
                // independent output-false or exactly-one assertions.
                Wait(()=>Volatile.Read(ref faults)>0,"actual LOW fault callback completed");
                Check(!monitor.ReadReleased()&&Volatile.Read(ref faults)==1,"LOW revokes output once");
                int afterLow=Volatile.Read(ref reads);Volatile.Write(ref level,1);DateTime renewed=DateTime.MinValue;
                Wait(()=>State(monitor,out renewed)=="LATCHED_HIGH","same reader continues to observe actual HIGH after active LOW");
                Check(renewed>lowUtc&&!monitor.ReadReleased()&&Volatile.Read(ref faults)==1,"new displayed HIGH never clears output latch");
                Rejected(monitor.EnableIdleTimeoutRetry,"faulted input cannot switch into an output-recovery mode");
                Rejected(monitor.EnterActiveReadMode,"displayed HIGH cannot reopen this round or acknowledge its fault");
                Wait(()=>Volatile.Read(ref reads)>=afterLow+4,"DI continues during faulted display-only state");
                Check(Volatile.Read(ref maxOverlap)==1&&Volatile.Read(ref readerId)!=Thread.CurrentThread.ManagedThreadId,"one independent serial SDK-read delegate owner");
                Volatile.Write(ref level,0);DateTime nextLow=DateTime.MinValue;
                Wait(()=>State(monitor,out nextLow)=="LATCHED_LOW","later physical LOW remains real live display");
                Check(nextLow>renewed&&Volatile.Read(ref faults)==1&&!monitor.ReadReleased(),"later level changes preserve first fault and cannot authorize");
                Check(monitor.Stop(600),"reader and deadline observer stop before disposal");
                DateTime stopped;Check(State(monitor,out stopped)=="UNKNOWN","stopped session no longer reports cached LOW as current");
            }
        }
        private static void IdleLevels()
        {
            int level=0,faults=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>Volatile.Read(ref level)==1,()=>Interlocked.Increment(ref faults)))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();DateTime low;
                Check(State(monitor,out low)=="LOW"&&monitor.ReadIdleReleased()==false&&Volatile.Read(ref faults)==0,"healthy idle LOW remains known but denies output");
                Volatile.Write(ref level,1);DateTime high=DateTime.MinValue;
                Wait(()=>State(monitor,out high)=="HIGH","idle LOW->HIGH is observed without another reader or reconnect");
                Check(high>low&&Volatile.Read(ref faults)==0&&monitor.IdleLowObserved,"idle transition preserves real timestamps and interrupted batch evidence");
                Rejected(()=>monitor.EnterActiveReadMode(true),"idle LOW history still blocks automatic batch continuation");
                monitor.EnterActiveReadMode();Check(monitor.ReadReleased(),"explicit new active epoch needs new valid HIGH");
            }
        }
        private static void ReadModeDisplay()
        {
            int reads=0,faults=0,activeReturned=0;Exception activeError=null;
            using(var oldEntered=new ManualResetEvent(false))using(var oldRelease=new ManualResetEvent(false))
            using(var freshEntered=new ManualResetEvent(false))using(var freshRelease=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(()=>{
                int read=Interlocked.Increment(ref reads);
                if(read==2){oldEntered.Set();oldRelease.WaitOne();}
                if(read==3){freshEntered.Set();freshRelease.WaitOne();}
                return true;
            },()=>Interlocked.Increment(ref faults)))
            {
                Thread active=null;
                // Freeze only this managed fake's age clock during the forced
                // mode-boundary interleaving; native production time is untouched.
                var clock=(System.Diagnostics.Stopwatch)typeof(FactoryEmergencyMonitor).GetField("clock",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor);
                var gate=typeof(FactoryEmergencyMonitor).GetField("gate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor);
                var generation=typeof(FactoryEmergencyMonitor).GetField("readModeGeneration",BindingFlags.Instance|BindingFlags.NonPublic);
                clock.Stop();
                try
                {
                    monitor.Start();Check(oldEntered.WaitOne(2000),"same owned DI has an in-flight read at the round boundary");
                    DateTime before;Check(State(monitor,out before)=="HIGH","real fresh HIGH was accepted before the boundary");
                    monitor.EnableIdleTimeoutRetry();DateTime idle;
                    Check(State(monitor,out idle)=="HIGH"&&idle==before,"end-round idle policy does not manufacture UNKNOWN or refresh the accepted sample time");
                    int idleGeneration;lock(gate)idleGeneration=(int)generation.GetValue(monitor);
                    active=new Thread(()=>{
                        try{monitor.EnterActiveReadMode();Interlocked.Exchange(ref activeReturned,1);}
                        catch(Exception error){activeError=error;Interlocked.Exchange(ref activeReturned,-1);}
                    }){IsBackground=true};active.Start();
                    Wait(()=>{lock(gate)return (int)generation.GetValue(monitor)!=idleGeneration;},"new active authorization epoch has begun");
                    DateTime activeDisplay;
                    Check(State(monitor,out activeDisplay)=="HIGH"&&activeDisplay==before&&Volatile.Read(ref activeReturned)==0,
                        "same-task physical display is not new-mode output authorization");
                    oldRelease.Set();Check(freshEntered.WaitOne(2000),"a read begun in the new active mode is required");
                    DateTime afterOld;
                    Check(State(monitor,out afterOld)=="HIGH"&&afterOld==before&&Volatile.Read(ref activeReturned)==0,
                        "late old-mode HIGH neither refreshes the sample nor releases the waiting output command");
                    clock.Start();
                    // The production clock stores integer milliseconds. Do not
                    // release a fake read in the same frozen millisecond and
                    // then mistake equal timestamps for a stale-mode permit.
                    Wait(()=>clock.ElapsedMilliseconds>=2,"resumed fake clock advances before the new-mode read returns");
                    freshRelease.Set();Wait(()=>Volatile.Read(ref activeReturned)!=0,"actual new-mode HIGH resolves authorization");
                    DateTime fresh;
                    Check(activeError==null&&Volatile.Read(ref activeReturned)==1&&monitor.ReadReleased()&&State(monitor,out fresh)=="HIGH"&&fresh>before,
                        "only an accepted new-mode read supplies current output permission and its own timestamp");
                    Check(Volatile.Read(ref faults)==0,"healthy policy changes are not communication or emergency faults");
                }
                finally
                {
                    oldRelease.Set();freshRelease.Set();clock.Start();
                    if(active!=null)Check(active.Join(600),"waiting fake output command leaves before monitor disposal");
                    Check(monitor.Stop(600),"same-task mode display fixture releases its sole DI owner");
                }
            }
            using(var replacement=new FactoryEmergencyMonitor(()=>true,()=>{}))
            {
                DateTime absent;Check(State(replacement,out absent)=="WAIT"&&absent==DateTime.MinValue,
                    "a new task owner cannot inherit an old monitor's displayed HIGH");
            }
        }
        private static void ActiveDeadline()
        {
            int reads=0,faults=0;
            using(var blocked=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(()=>{
                if(Interlocked.Increment(ref reads)==1)return false;
                blocked.Set();release.WaitOne();return true;
            },()=>Interlocked.Increment(ref faults)))
            {
                monitor.Start();Check(blocked.WaitOne(2000),"fault-latched monitor keeps reading on sole owned task");
                try
                {
                    DateTime low;Check(State(monitor,out low)=="LATCHED_LOW","first real LOW is fresh");
                    DateTime stale=DateTime.MinValue;
                    Wait(()=>State(monitor,out stale)=="UNKNOWN","fault latch cannot turn expired LOW into current display");
                    Check(stale==low&&!monitor.ReadReleased(),"stale sample time is preserved and output remains revoked");
                    DateTime error=DateTime.MinValue;
                    Wait(()=>State(monitor,out error)=="ERROR","500ms no-data observer remains active after physical LOW");
                    Check(error==low&&Volatile.Read(ref faults)==1&&Volatile.Read(ref reads)==2,"error does not invent a sample or duplicate first output-fault notification");
                    Rejected(()=>monitor.ReadReleased(),"real communication failure remains an exception");
                    Check(!monitor.Stop(5),"blocked native delegate retains task ownership until it exits");
                }
                finally{release.Set();Check(monitor.Stop(600),"late delegate returns before cleanup");}
                DateTime final;Check(State(monitor,out final)=="ERROR"&&Volatile.Read(ref reads)==2,"late HIGH after deadline never clears fault or starts another call");
            }
        }
        private static void BadReads()
        {
            foreach(Exception error in new Exception[]{new InvalidOperationException("ART invalid handle -200088"),new FactoryEmergencyReadTimeoutException(-200474,null)})
            {
                int reads=0,faults=0;
                using(var monitor=new FactoryEmergencyMonitor(()=>{if(Interlocked.Increment(ref reads)==1)return false;throw error;},()=>Interlocked.Increment(ref faults)))
                {
                    monitor.Start();DateTime utc=DateTime.MinValue;
                    Wait(()=>State(monitor,out utc)=="ERROR","real error following LOW stops further DI calls");
                    Check(monitor.Stop(600)&&Volatile.Read(ref reads)==2&&Volatile.Read(ref faults)==1,"bad handle/error is not retried, ignored, rebuilt or relabeled HIGH");
                    string detail;Check(monitor.ConnectionState(out detail)=="ERROR"&&detail.Contains(error.Message),"original error is retained after display sampling stops");
                }
            }
        }
        private static void Set(object target,string name,object value)
        {target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);}
        private static void Call(object target,string name)
        {target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,null);}
        private static void NativeFaultSeparation()
        {
            var backend=new NativeFactoryBackend();int level=1;Exception readError=null;
            var create=typeof(NativeFactoryBackend).GetMethod("CreateEmergencyMonitor",BindingFlags.Instance|BindingFlags.NonPublic);
            Func<int,bool> fake=ms=>{var error=readError;if(error!=null)throw error;return Volatile.Read(ref level)==1;};
            using(var monitor=(FactoryEmergencyMonitor)create.Invoke(backend,new object[]{fake}))
            {
                Set(backend,"initialized",true);Set(backend,"emergencyMonitor",monitor);
                monitor.Start();Check(backend.ReadEmergencyReleased(),"pre-SDK native helper reads sole fake monitor HIGH");
                Volatile.Write(ref level,0);Wait(()=>monitor.OutputStopLatched,"native callback receives actual fake LOW");
                Check(!backend.ReadEmergencyReleased()&&!backend.Faulted&&backend.EmergencyOutputLatched,
                    "actual native callback distinguishes permanent output LOW latch from communication fault");
                Volatile.Write(ref level,1);DateTime utc=DateTime.MinValue;Wait(()=>State(monitor,out utc)=="LATCHED_HIGH","same native-owned monitor displays later HIGH");
                Check(!backend.ReadEmergencyReleased()&&!backend.Faulted,"native later HIGH does not clear latch or fabricate reusable output");
                readError=new InvalidOperationException("managed real DI driver error after LOW");
                Wait(()=>monitor.HasReadFailure,"original reader failure remains visible after once-only LOW notification");
                Rejected(()=>backend.ReadEmergencyReleased(),"native cache consumer preserves real driver failure");
                Check(backend.Faulted,"a driver error following LOW still marks communication unhealthy");
                Set(backend,"emergencyMonitor",null);Set(backend,"initialized",false);
            }
        }
        private static void NativePublication()
        {
            var backend=new NativeFactoryBackend();var settings=new FactoryTests.Fixture().Settings;settings.Simulation=false;
            Set(backend,"settings",settings);Set(backend,"sessionIdentity",settings.Clone());Set(backend,"initializing",true);Set(backend,"emergencyTaskGeneration",7);
            int level=1,faults=0;
            var events=new ConcurrentQueue<Tuple<string,string>>();
            using(var monitor=new FactoryEmergencyMonitor(()=>Volatile.Read(ref level)==1,()=>Interlocked.Increment(ref faults)))
            {
                monitor.Start();Set(backend,"emergencyMonitor",monitor);
                backend.ConnectionReport=(key,value)=>events.Enqueue(Tuple.Create(key,value));
                Call(backend,"StartEmergencyPublisher");
                try
                {
                    Wait(()=>events.Count>=3,"native managed snapshot loop publishes while fake Pico initialization is still blocked");
                    Check(!backend.SessionOpen&&!backend.RoundActive,"publication is not full Open, a round or output permission");
                    Rejected(()=>Call(backend,"StartEmergencyPublisher"),"native permits only one periodic snapshot producer");
                    Volatile.Write(ref level,0);Wait(()=>Contains(events,"LINK_ART_LATCHED_LOW"),"native publishes fault-latched LOW stage");
                    Volatile.Write(ref level,1);Wait(()=>Contains(events,"LINK_ART_LATCHED_HIGH"),"native publishes subsequent real HIGH as display-only stage");
                    Wait(()=>Volatile.Read(ref faults)>0,"native fake monitor delivered its actual fault callback");
                    Check(!monitor.ReadReleased()&&Volatile.Read(ref faults)==1,"native snapshot publication cannot clear output latch");
                    Tuple<string,string> low=null,high=null;
                    foreach(var entry in events){if(entry.Item1=="LINK_ART_LATCHED_LOW")low=entry;if(entry.Item1=="LINK_ART_LATCHED_HIGH")high=entry;}
                    DateTime lowUtc=DateTime.Parse(low.Item2.Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                    DateTime highUtc=DateTime.Parse(high.Item2.Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
                    Check(highUtc>lowUtc&&highUtc<=DateTime.UtcNow,"native stage preserves actual new sample times, not publish time");
                    Check(high.Item2.Contains("DiTaskGeneration=7")&&high.Item2.Contains("SessionId="+backend.SessionId),"display stage remains bound to same native session and DI generation");
                    string mapped=null;var bridge=UnifiedPressureBridge.AdaptProgress(false,(key,value)=>mapped=key);
                    bridge(low.Item1,low.Item2);Check(mapped=="EMERGENCY_LATCHED_ACTIVE","bridge separates latched LOW from ordinary permit cache");
                    bridge(high.Item1,high.Item2);Check(mapped=="EMERGENCY_LATCHED_RELEASED"&&mapped!="EMERGENCY_RELEASED","latched HIGH never masquerades as ordinary HIGH authorization");
                    UnifiedPressureBridge.AdaptProgress(true,(key,value)=>mapped=key)(high.Item1,high.Item2);
                    Check(mapped=="SIM_EMERGENCY_LATCHED_RELEASED","simulation cannot masquerade as real fault-latched input");
                    var tracker=new FactorySessionFaultTracker {SessionId=backend.SessionId};tracker.Observe(low.Item1,low.Item2);tracker.Observe(high.Item1,high.Item2);
                    Check(tracker.Freeze(null).Category==FactorySessionFaultCategory.EmergencyInputLow,"displayed HIGH cannot erase genuine EmergencyInputLow attribution");
                }
                finally{Call(backend,"StopEmergencyPublisher");}
                int stopped=events.Count;Thread.Sleep(120);Check(events.Count==stopped,"producer join prevents callbacks after lifecycle stop");
                Check(monitor.Stop(600),"DI owner is separately stopped after managed publication exits");
                Set(backend,"emergencyMonitor",null);
            }
        }
        private static bool Contains(ConcurrentQueue<Tuple<string,string>> events,string stage)
        {foreach(var entry in events)if(entry.Item1==stage)return true;return false;}
        private static void PublisherLifecycle()
        {
            int calls=0,failures=0;
            using(var publisher=new FactoryEmergencySnapshotPublisher(10,"fake failing publisher",()=>{Interlocked.Increment(ref calls);throw new Exception("queue failure");},error=>Interlocked.Increment(ref failures)))
            {
                publisher.Start();Wait(()=>Volatile.Read(ref failures)==1,"publication failure is retained outside the DI reader");
                Check(publisher.Stop(600)&&Volatile.Read(ref calls)==1&&publisher.Failure.Message=="queue failure","failed publication loop exits without retries or another task");
                Rejected(publisher.Start,"failed publisher cannot restart itself");
            }
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var publisher=new FactoryEmergencySnapshotPublisher(10,"fake blocked queue",()=>{entered.Set();release.WaitOne();},error=>{}))
            {
                publisher.Start();Check(entered.WaitOne(2000),"managed blocked publication is independent of DI ownership");
                try{Check(!publisher.Stop(5),"failed join does not claim callback ownership released");}
                finally{release.Set();Check(publisher.Stop(600),"actual callback exit allows ownership release");}
                Rejected(publisher.Start,"stopped publisher cannot be reused for a different DI generation");
            }
        }
    }
}
