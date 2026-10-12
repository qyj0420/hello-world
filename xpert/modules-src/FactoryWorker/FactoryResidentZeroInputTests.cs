using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryResidentZeroInputTests
    {
        internal static string Run()
        {
            int n=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Resident zero: "+why);n++;};
            var settings=Settings();
            check(FactoryNativeSessionPolicy.PrepareQueuedRound(settings,false).TargetKv==0,"manual zero can use resident queue");
            var manual=settings.Clone();manual.ManualZeroInputWithoutEmergency=true;
            check(FactoryNativeSessionPolicy.PrepareQueuedRound(manual,false).ManualZeroInputWithoutEmergency,"explicit manual zero has input-only capability");
            check(!FactoryNativeSessionPolicy.SameDeviceSession(settings,manual),"DI-permitted and display-only modes require verified close before session switch");
            check(FactoryNativeSessionPolicy.SameDeviceSession(manual,manual.Clone()),"manual zero consecutive rounds reuse the same input capability");
            foreach(Action<FactorySettings> change in new Action<FactorySettings>[] {
                s=>s.TargetKv=1,s=>s.ZeroSynchronizedOptical=true,s=>s.AutomaticBatch=true,s=>s.BatchContinuation=true,s=>s.BatchStartConfirmed=true})
            {
                var forbidden=manual.Clone();change(forbidden);
                check(Throws(()=>forbidden.Validate(FactoryRunKind.Pressure))&&Throws(()=>FactoryNativeSessionPolicy.PrepareOpen(forbidden)),
                    "DI-less manual capability cannot open output or automatic requests");
            }
            check(Throws(()=>manual.Validate(FactoryRunKind.Calibration)),"manual pressure zero capability cannot enter calibration");
            var bad=settings.Clone();bad.AutomaticBatch=true;
            check(Throws(()=>FactoryNativeSessionPolicy.PrepareQueuedRound(bad,false)),"manual LOW whitelist never permits automatic zero");
            bad=settings.Clone();bad.BatchContinuation=true;
            check(Throws(()=>FactoryNativeSessionPolicy.PrepareZeroInputRound(bad)),"continuation cannot reuse manual zero permission");
            bad=settings.Clone();bad.TargetKv=1;
            check(Throws(()=>FactoryNativeSessionPolicy.PrepareZeroInputRound(bad)),"positive output cannot enter input adapter");
            var fake=new Session();var first=new ResidentFactoryZeroInput(fake);first.Initialize(settings);
            long old=fake.Generation;check(!first.EmergencyHigh,"fresh LOW is valid for manual zero input");
            first.Arm();first.Dispose();first.Dispose();
            check(fake.Begins==1&&fake.Stops==1&&!fake.Active,"Dispose stops only once without closing connection");
            check(Throws(first.Arm),"disposed zero adapter cannot arm again");
            var second=new ResidentFactoryZeroInput(fake);second.Initialize(settings);
            check(fake.Generation>old&&fake.Begins==2,"zero→zero creates new round lease, not connection");
            check(Throws(()=>fake.EndZeroInput(old))&&fake.Active,"old lease cannot stop newer zero round");
            fake.FailStop=true;check(Throws(second.Dispose)&&fake.Active&&fake.Faulted,"failed AI Stop retains round ownership and fault");
            check(Throws(()=>new ResidentFactoryZeroInput(fake).Initialize(settings)),"failed cleanup prevents reopening");
            fake.FailStop=false;second.Dispose();check(!fake.Active&&fake.Faulted,"later cleanup never clears fault latch");
            int ai=0,pico=0,released=0,fault=0;
            check(Throws(()=>FactoryZeroInputCleanup.Stop(()=>{ai++;throw new Exception("AI Stop failure");},()=>pico++,()=>released++,()=>fault++))&&
                ai==1&&pico==1&&released==0&&fault==1,"actual cleanup helper attempts both stops but does not release failed round");
            FactoryZeroInputCleanup.Stop(()=>ai++,()=>pico++,()=>released++,()=>fault++);
            check(released==1&&fault==1,"successful stop releases round without clearing former fault");
            // Native guards are exercised without assigning ANY SDK handle.
            var backend=new NativeFactoryBackend();Set(backend,"initialized",true);Set(backend,"settings",settings);
            Set(backend,"roundActive",true);Set(backend,"zeroInputRound",true);Set(backend,"zeroInputGeneration",9L);
            foreach(Action output in new Action[]{()=>backend.WriteAo(0),()=>backend.WriteAo(1),()=>backend.SetHv(false),()=>backend.SetHv(true),
                ()=>backend.SetOptical(false),()=>backend.SetOptical(true),()=>backend.SetPressureIndicator(false),()=>backend.SetPressureIndicator(true),
                ()=>backend.Arm(false),backend.ArmBeforeShutdown,backend.PrepareOpticalRetry,backend.PrepareFaultMonitoring,backend.Shutdown})
                check(Throws(output),"zero native round rejects output/ordinary pressure entry before SDK");
            var native=(IFactoryResidentZeroInputSession)backend;
            Set(backend,"settings",manual);
            check(!Throws(()=>typeof(NativeFactoryBackend).GetMethod("RequireZeroInputReadiness",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null)),
                "explicit manual zero readiness does not read absent DI or SDK");
            check(!Throws(()=>typeof(NativeFactoryBackend).GetMethod("CheckZeroReadFault",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null)),
                "manual zero completion ignores absent DI but retains fault guard");
            Set(backend,"faulted",true);
            check(Throws(()=>typeof(NativeFactoryBackend).GetMethod("RequireZeroInputReadiness",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null)),
                "real input/session faults still refuse manual zero");
            Set(backend,"faulted",false);Set(backend,"settings",settings);
            check(Throws(()=>native.ArmZeroInput(8))&&Throws(()=>native.EndZeroInput(8))&&backend.RoundActive,
                "native old-generation arm/stop cannot affect current round");
            int level=0;
            using(var monitor=new FactoryEmergencyMonitor(ms=>Volatile.Read(ref level)!=0,()=>{},null))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();Set(backend,"emergencyMonitor",monitor);
                check(!native.ReadZeroEmergency(9),"native zero reads actual LOW from sole fake DI loop");
                Thread.Sleep(180);check(!native.ReadZeroEmergency(9),"LOW remains timely without resetting DI reader");
                Volatile.Write(ref level,1);Thread.Sleep(40);check(native.ReadZeroEmergency(9),"fresh HIGH is observed without creating a new DI task");
                check(Throws(()=>backend.SetHv(true)),"HIGH return does not convert zero lease into output permission");
                DateTime sample=DateTime.MinValue;backend.ConnectionReport=(key,text)=>{if(key=="LINK_ART_HIGH")sample=DateTime.Parse(text.Split('|')[0],null,System.Globalization.DateTimeStyles.RoundtripKind);};
                backend.ReportConnectionSnapshot();check(sample.Kind==DateTimeKind.Utc&&sample<=DateTime.UtcNow&&sample>DateTime.UtcNow.AddSeconds(-1),"resident publication carries real accepted UTC sample");
            }
            check(Throws(()=>native.ReadZeroEmergency(9)),"stopped DI never uses old HIGH as zero acquisition permission");
            // Use the production DI-monitor factory with only a fake delegate:
            // one reader publishes real LOW/HIGH, but display failures do not
            // become an emergency permit or deny explicit manual zero input.
            Set(backend,"settings",manual);Set(backend,"sessionIdentity",manual.Clone());Set(backend,"faulted",false);
            int displayLevel=0,readerThread=0,reads=0;
            Func<int,bool> displayRead=ms=>
            {
                int id=Thread.CurrentThread.ManagedThreadId;
                int firstId=Interlocked.CompareExchange(ref readerThread,id,0);
                if(firstId!=0&&firstId!=id)throw new Exception("competing DI reader");
                Interlocked.Increment(ref reads);
                int value=Volatile.Read(ref displayLevel);
                if(value==2)throw new InvalidOperationException("fake display-only DI IO failure");
                return value==1;
            };
            var createMonitor=typeof(NativeFactoryBackend).GetMethod("CreateEmergencyMonitor",BindingFlags.Instance|BindingFlags.NonPublic);
            using(var display=(FactoryEmergencyMonitor)createMonitor.Invoke(backend,new object[]{displayRead}))
            {
                display.EnableIdleTimeoutRetry();display.Start();Set(backend,"emergencyMonitor",display);
                string published=null,text=null;
                backend.ConnectionReport=(key,detail)=>{published=key;text=detail;};
                backend.ReportConnectionSnapshot();
                check(published=="LINK_ART_LOW"&&!backend.Faulted,"manual zero publishes real LOW without locking capture");
                check(!Throws(()=>typeof(NativeFactoryBackend).GetMethod("RequireZeroInputReadiness",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null)),
                    "manual readiness ignores displayed LOW");
                Volatile.Write(ref displayLevel,1);
                check(SpinWait.SpinUntil(()=>{string detail;return display.ConnectionState(out detail)=="HIGH";},1000),"same sole reader observes new HIGH");
                backend.ReportConnectionSnapshot();
                DateTime actual=DateTime.Parse(text.Split('|')[0],null,System.Globalization.DateTimeStyles.RoundtripKind);
                check(published=="LINK_ART_HIGH"&&actual.Kind==DateTimeKind.Utc&&actual<=DateTime.UtcNow&&!backend.Faulted,
                    "displayed HIGH uses actual accepted sample UTC, not device online");
                check(Throws(()=>backend.SetHv(true))&&Throws(()=>backend.WriteAo(1)),"display HIGH cannot authorize zero lease output");
                Volatile.Write(ref displayLevel,2);
                check(SpinWait.SpinUntil(()=>{string detail;return display.ConnectionState(out detail)=="ERROR";},1000),"real display IO failure retained in monitor");
                backend.ReportConnectionSnapshot();
                check(published=="LINK_ART_UNKNOWN"&&text.Contains("fake display-only DI IO failure")&&!backend.Faulted,
                    "manual DI failure publishes truthful unknown without pressure-session fault");
                check(!Throws(()=>typeof(NativeFactoryBackend).GetMethod("RequireZeroInputReadiness",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null))&&
                    !Throws(()=>typeof(NativeFactoryBackend).GetMethod("CheckZeroReadFault",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(backend,null)),
                    "manual start/completion are independent of display DI failure");
                check(reads>=3&&readerThread!=0,"all display samples use one DI reader");
            }
            Set(backend,"emergencyMonitor",null);Set(backend,"emergencyDisplayFailure",new Exception("fake DI creation failure"));
            string unavailable=null;backend.ConnectionReport=(key,detail)=>unavailable=key;
            backend.ReportConnectionSnapshot();
            check(unavailable=="LINK_ART_UNKNOWN"&&!backend.Faulted,"failed diagnostic creation keeps unknown, no invented zero or capture denial");
            // A supervised session retains the original genuine fault latch.
            Set(backend,"settings",settings);Set(backend,"sessionIdentity",settings.Clone());Set(backend,"faulted",false);
            using(var supervised=(FactoryEmergencyMonitor)createMonitor.Invoke(backend,new object[]{(Func<int,bool>)(ms=>{throw new Exception("fake supervised DI failure");})}))
            {
                supervised.EnableIdleTimeoutRetry();check(Throws(supervised.Start)&&backend.Faulted,"supervised DI failure still locks output session");
            }
            // Cancellation before/after Arm always ends the adapter round.
            foreach(bool beforeArm in new[]{true,false})
            {
                var f=new Session();var adapter=new ResidentFactoryZeroInput(f);
                using(var cts=new CancellationTokenSource())
                {
                    check(Throws(()=>FactoryZeroInputRunner.Run(settings,adapter,new Clock(),(key,text)=>
                        {if(key==(beforeArm?"STEP_INIT_PASSED":"STEP_ARM_PASSED"))cts.Cancel();},cts.Token))&&!f.Active&&f.Stops==1,
                        "cancellation stops held zero round without closing a connection");
                }
            }
            foreach(var method in typeof(IFactoryResidentZeroInputSession).GetMethods())
                check(!method.Name.Contains("Close")&&!method.Name.Contains("Hv")&&!method.Name.Contains("Optical")&&!method.Name.Contains("Ao"),"resident input capability has no connection close or output API");
            return "Resident zero: "+n+" fake/pre-SDK checks PASS; HardwareAccessed=False; SDKCalled=False";
        }
        private static FactorySettings Settings(){var s=new FactoryTests.Fixture().Settings;s.Simulation=false;s.TargetKv=0;return s;}
        private static bool Throws(Action a){try{a();return false;}catch{return true;}}
        private static void Set(object value,string name,object field){value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(value,field);}
        private sealed class Clock:IFactoryClock{public long ElapsedMilliseconds{get;private set;}public void Pause(int ms,CancellationToken token){token.ThrowIfCancellationRequested();ElapsedMilliseconds+=ms;}}
        private sealed class Session:IFactoryResidentZeroInputSession
        {
            internal long Generation;internal bool Active,FailStop,Faulted;internal int Begins,Stops;
            public long BeginZeroInput(FactorySettings s){if(Active||Faulted)throw new InvalidOperationException();Active=true;Begins++;return ++Generation;}
            private void Guard(long g){if(!Active||g!=Generation)throw new InvalidOperationException();}
            public bool ReadZeroEmergency(long g){Guard(g);return false;}
            public void ArmZeroInput(long g){Guard(g);}
            public bool ZeroArtComplete(long g){Guard(g);return false;}
            public bool ZeroPicoComplete(long g){Guard(g);return false;}
            public BenchCapture ReadZeroCapture(long g){Guard(g);throw new InvalidOperationException("no synthetic result on canceled round");}
            public void EndZeroInput(long g){Guard(g);Stops++;if(FailStop){Faulted=true;throw new Exception("AI stop failed");}Active=false;}
        }
    }
}
