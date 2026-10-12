using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Offline fakes only. No NativeFactoryBackend, SDK import, product entry,
    // real device/configuration, filesystem or process is used by these tests.
    internal static class FactoryEmergencyTimingContractTests
    {
        private static int checks;
        private static void Check(bool value,string message)
        { if(!value)throw new Exception(message);++checks; }
        private static void Rejected(Action action,string message)
        { bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message); }
        private static void Wait(WaitHandle signal,string message)
        { Check(signal.WaitOne(2000),message); }
        internal static int Run()
        {
            checks=0;Policy();AfterReturn();PendingOutput(-26802);PendingOutput(-200284);StartupDeadline();
            TotalRetryDeadline(-26802);TotalRetryDeadline(-200284);
            ModeBoundary();ImmediateFaults();IdleReadOnly();IdleLowDeadline();IdleRebuild();return checks;
        }
        private static void Policy()
        {
            Check(!FactoryEmergencyReadPolicy.DeadlineReached(1499,1000),"499 ms is inside fixed budget");
            Check(FactoryEmergencyReadPolicy.DeadlineReached(1500,1000),"500 ms is deadline, inclusive");
            Check(FactoryEmergencyReadPolicy.ReadBudgetMs(1000,1000)==300,"first SDK budget 300 ms");
            Check(FactoryEmergencyReadPolicy.ReadBudgetMs(1310,1000)==190,"retry budget not reset");
            Check(FactoryEmergencyReadPolicy.ReadBudgetMs(1499,1000)==1,"last millisecond bounded");
            Check(FactoryEmergencyReadPolicy.ReadBudgetMs(1500,1000)==0,"no new SDK read at deadline");
            Check(FactoryEmergencyReadPolicy.IsFresh(1100,1000),"old 100 ms freshness preserved");
            Check(!FactoryEmergencyReadPolicy.IsFresh(1101,1000),"101 ms HIGH not new output permit");
            Check(FactoryEmergencyReadPolicy.IsRetryableStatus(-26802),"official DI timeout candidate");
            Check(FactoryEmergencyReadPolicy.IsRetryableStatus(-200284),"official DI samples-not-yet-available candidate");
            foreach(int status in new[]{-26802,-200284})foreach(int actual in new[]{-1,0,1,2})
                Check(FactoryEmergencyReadPolicy.IsZeroSamplePending(status,actual)==(actual==0),
                    "only SDK explicit zero permits typed pending "+status+"/"+actual);
            foreach(int code in new[]{-200474,-200088,-50401,-229779,-1,0})
            {
                Check(!FactoryEmergencyReadPolicy.IsRetryableStatus(code),"not retry status "+code);
                Check(!FactoryEmergencyReadPolicy.IsZeroSamplePending(code,0),"unknown/success zero is not a retry receipt "+code);
            }
            var evidence=new FactoryArtCommunicationEvidence();
            Check(!evidence.Snapshot().HasObservation,"other ART begins unknown");
            evidence.Observe(0,"existing AI read");Check(!evidence.Snapshot().HasFailure,"existing operation success recorded");
            evidence.Observe(-200088,"existing AO write");evidence.Observe(0,"later read");
            Check(evidence.Snapshot().HasFailure && evidence.Snapshot().Status==-200088,"success cannot erase ART fault");
            var unstarted=new FactoryEmergencyMonitor(()=>true,()=>{});
            Check(unstarted.Stop(1),"unstarted cleanup owns no reader");unstarted.Dispose();unstarted.Dispose();
            Rejected(()=>unstarted.Start(),"disposed monitor cannot restart");
        }
        private static void AfterReturn()
        {
            long lastReturn=-1;int calls=0;bool delayed=true,budgetValid=true;
            long minimumTicks=(long)Math.Ceiling(Stopwatch.Frequency*0.010);
            var intervals=new List<long>();
            using(var enough=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(ms=>{
                long begin=Stopwatch.GetTimestamp();
                if(lastReturn>=0){intervals.Add(begin-lastReturn);if(begin-lastReturn<minimumTicks)delayed=false;}
                if(ms<=0 || ms>300)budgetValid=false;
                Thread.Sleep(2);lastReturn=Stopwatch.GetTimestamp();
                if(Interlocked.Increment(ref calls)>=40)enough.Set();return true;
            },()=>{},null))
            {
                monitor.Start();Wait(enough,"forty independent reads");Check(monitor.Stop(600),"reader stops");
                Check(delayed,"delay is after return, not start-to-start");Check(budgetValid,"every read has bounded budget");
                Check(intervals.Count>=39,"many high-precision intervals measured");
                foreach(long ticks in intervals)Check(ticks>=minimumTicks,"actual after-return monotonic gap >= 10 ms: ticks="+ticks);
            }
        }
        private static void PendingOutput(int status)
        {
            int calls=0,inside=0,maxInside=0,permit=0;Exception commandError=null;
            long timeoutReturnedTicks=0,retryStartedTicks=0;
            using(var retryEntered=new ManualResetEvent(false))
            using(var returnRetry=new ManualResetEvent(false))
            using(var commandDone=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(ms=>{
                int n=Interlocked.Increment(ref calls);int active=Interlocked.Increment(ref inside);maxInside=Math.Max(maxInside,active);
                try{
                    if(n==2){timeoutReturnedTicks=Stopwatch.GetTimestamp();throw new FactoryEmergencyReadTimeoutException(status,null);}
                    if(n==3){retryStartedTicks=Stopwatch.GetTimestamp();retryEntered.Set();returnRetry.WaitOne();}return true;
                }finally{Interlocked.Decrement(ref inside);}
            },()=>{},null))
            {
                monitor.Start();Wait(retryEntered,"single retry starts");
                Check(retryStartedTicks-timeoutReturnedTicks>=(long)Math.Ceiling(Stopwatch.Frequency*0.010),"timeout-to-retry also waits actual >= 10 ms");
                var command=new Thread(()=>{try{if(monitor.ReadReleased())Interlocked.Exchange(ref permit,1);}catch(Exception ex){commandError=ex;}finally{commandDone.Set();}});
                command.Start();
                try{
                    Check(!commandDone.WaitOne(60) && Volatile.Read(ref permit)==0,"pending cannot issue output using old HIGH");
                    returnRetry.Set();Wait(commandDone,"fresh retry releases wait without gate deadlock");
                    Check(commandError==null && permit==1,"fresh read may permit still-authorized owner");Check(maxInside==1,"no overlapping DI reads");
                }finally{returnRetry.Set();command.Join(1000);monitor.Stop(600);}
            }
        }
        private static void StartupDeadline()
        {
            int calls=0,faults=0;Exception startError=null;var clock=Stopwatch.StartNew();long faultAt=-1;
            using(var entered=new ManualResetEvent(false))
            using(var release=new ManualResetEvent(false))
            using(var fault=new ManualResetEvent(false))
            using(var done=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(ms=>{Interlocked.Increment(ref calls);entered.Set();release.WaitOne();return true;},
                ()=>{Interlocked.Increment(ref faults);faultAt=clock.ElapsedMilliseconds;fault.Set();},null))
            {
                var starter=new Thread(()=>{try{monitor.Start();}catch(Exception ex){startError=ex;}finally{done.Set();}});starter.Start();
                try{
                    Wait(entered,"first read immediate");Wait(fault,"watchdog fires while native fake remains blocked");
                    Wait(done,"startup returns failure without waiting for native return");
                    Check(startError!=null && faultAt>=480 && faultAt<1500,"startup has 500 ms logical bound with scheduling tolerance");
                    Check(!monitor.Stop(20),"pending reader prevents handle cleanup");
                    release.Set();Check(monitor.Stop(600),"old reader must exit before cleanup");
                    string detail;Check(monitor.ConnectionState(out detail)=="ERROR" && faults==1 && calls==1,"late HIGH cannot clear latch or start another read");
                    Rejected(()=>monitor.ReadReleased(),"stopped monitor cannot authorize");
                }finally{release.Set();starter.Join(1000);monitor.Stop(600);}
            }
        }
        private static void TotalRetryDeadline(int status)
        {
            int calls=0,retryBudget=0;long lastGood=0,faultAt=0;var clock=Stopwatch.StartNew();
            using(var fault=new ManualResetEvent(false))
            using(var lateReturned=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(ms=>{
                int n=Interlocked.Increment(ref calls);
                if(n==1){lastGood=clock.ElapsedMilliseconds;return true;}
                if(n==2){Thread.Sleep(ms);throw new FactoryEmergencyReadTimeoutException(status,null);}
                retryBudget=ms;Thread.Sleep(ms+100);lateReturned.Set();return true;
            },()=>{faultAt=clock.ElapsedMilliseconds;fault.Set();},null))
            {
                monitor.Start();Wait(fault,"fixed no-valid-data watchdog expires during retry");
                Check(!lateReturned.WaitOne(0),"fault does not wait for late native return");
                Check(faultAt-lastGood>=480 && faultAt-lastGood<900,"retry does not reset 500 ms origin");
                Check(retryBudget>0 && retryBudget<200,"retry receives only remaining budget");
                Rejected(()=>monitor.ReadReleased(),"faulted retry cannot authorize AO/HV");
                Wait(lateReturned,"late retry eventually returns");monitor.Stop(600);
                string detail;Check(monitor.ConnectionState(out detail)=="ERROR" && calls==3,"late retry HIGH remains faulted");
            }
        }
        private static void ModeBoundary()
        {
            int calls=0;Exception activeError=null;
            using(var oldEntered=new ManualResetEvent(false))using(var releaseOld=new ManualResetEvent(false))
            using(var freshEntered=new ManualResetEvent(false))using(var releaseFresh=new ManualResetEvent(false))
            using(var activeDone=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(ms=>{
                int n=Interlocked.Increment(ref calls);
                if(n==2){oldEntered.Set();releaseOld.WaitOne();}
                if(n==3){freshEntered.Set();releaseFresh.WaitOne();}return true;
            },()=>{},null))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();Wait(oldEntered,"old idle read in flight");
                var startActive=new Thread(()=>{try{monitor.EnterActiveReadMode();}catch(Exception ex){activeError=ex;}finally{activeDone.Set();}});
                startActive.Start();
                try{
                    Check(!activeDone.WaitOne(30),"new active mode waits for new epoch");releaseOld.Set();Wait(freshEntered,"fresh active read begins serially");
                    Check(!activeDone.WaitOne(30),"late old idle HIGH cannot authorize new round");
                    releaseFresh.Set();Wait(activeDone,"fresh active sample authorizes");Check(activeError==null,"mode switch succeeds only after fresh sample");
                }finally{releaseOld.Set();releaseFresh.Set();startActive.Join(1000);monitor.Stop(600);}
            }
        }
        private static void ImmediateFaults()
        {
            int faults=0,calls=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++calls;return false;},()=>++faults))
            {monitor.Start();Check(!monitor.ReadReleased() && faults==1 && calls>=1,"LOW immediately latches once; later display reads cannot authorize output");}
            foreach(Exception failure in new Exception[]{new TimeoutException("-26802 timeout text"),
                new FactoryEmergencyReadTimeoutException(-200474,null),new InvalidOperationException("ART -200088")})
            {
                faults=0;calls=0;
                using(var monitor=new FactoryEmergencyMonitor(()=>{++calls;throw failure;},()=>++faults))
                {Rejected(()=>monitor.Start(),"nonexact timeout rejected immediately");Check(faults==1 && calls==1,"no error string guessing or retry");}
            }
            var evidence=new FactoryArtCommunicationEvidence();evidence.Observe(-50401,"existing AI read");calls=0;
            using(var monitor=new FactoryEmergencyMonitor(ms=>{++calls;throw new FactoryEmergencyReadTimeoutException(-26802,null);},()=>{},evidence.Snapshot))
            {Rejected(()=>monitor.Start(),"other ART known failure vetoes retry");Check(calls==1,"no retry with recorded communications fault");}
        }
        private static void IdleReadOnly()
        {
            int level=0,faults=0,reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{Interlocked.Increment(ref reads);return Volatile.Read(ref level)==1;},()=>Interlocked.Increment(ref faults)))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();
                Check(monitor.ReadIdleReleased()==false && faults==0,"startup LOW remains known, connected and read-only");
                Check(monitor.IdleLowObserved,"idle LOW invalidates continuation permission");
                Interlocked.Exchange(ref level,1);
                Check(SpinWait.SpinUntil(()=>monitor.ReadIdleReleased()==true,1000),"same DI owner observes release without reconnect");
                Check(monitor.IdleLowObserved && faults==0 && reads>1,"HIGH does not erase idle interruption evidence");
                var events=new List<string>();monitor.DrainRetryEvents((key,value)=>events.Add(key));
                Check(events.Contains("P04_IDLE_ACTIVE"),"transient idle LOW retained for host queue cancellation");
                Rejected(()=>monitor.EnterActiveReadMode(true),"idle LOW is checked atomically before batch continuation");
                monitor.EnterActiveReadMode();
                Check(monitor.ReadReleased() && !monitor.IdleLowObserved,"explicit new round requires new HIGH epoch");
                Interlocked.Exchange(ref level,0);
                Check(SpinWait.SpinUntil(()=>Volatile.Read(ref faults)==1,1000),"active LOW immediately faults");
                Interlocked.Exchange(ref level,1);
                Check(!monitor.ReadReleased() && faults==1,"release never resets active emergency latch");
                Rejected(monitor.EnableIdleTimeoutRetry,"active LOW cannot be converted into idle recovery");
            }
        }
        private static void IdleLowDeadline()
        {
            int calls=0,faults=0;
            using(var blocked=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(()=>{
                if(Interlocked.Increment(ref calls)==1)return false;
                blocked.Set();release.WaitOne();return true;
            },()=>Interlocked.Increment(ref faults)))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();
                try
                {
                    Wait(blocked,"idle LOW does not stop subsequent DI reads");
                    Check(SpinWait.SpinUntil(()=>Volatile.Read(ref faults)==1,1500),"no input for 500 ms still faults after idle LOW");
                    string detail;Check(monitor.ConnectionState(out detail)=="ERROR","expired idle LOW is not a current level");
                    Check(!monitor.Stop(5),"blocked idle reader still owns handle");
                }
                finally {release.Set();Check(monitor.Stop(600),"late idle read exits before cleanup");}
                Rejected(()=>monitor.ReadReleased(),"late HIGH never recovers fault");
            }
        }
        private static void IdleRebuild()
        {
            int calls=0,faults=0;var order=new List<string>();
            using(var monitor=new FactoryEmergencyMonitor(ms=>{++calls;throw new FactoryEmergencyReadTimeoutException(-26802,null);},()=>++faults,null))
            {
                monitor.EnableIdleTimeoutRetry();monitor.AllowOneIdleRebuildDeferral();monitor.Start();
                Check(monitor.IdleTimeoutRebuildRequired && calls==2 && faults==1,"second timeout both latches and requests idle rebuild");
                Rejected(()=>monitor.ReadReleased(),"requesting rebuild is not output permission");
                var policy=new FactoryIdleDiRebuildPolicy();
                policy.Rebuild(true,()=>{Check(monitor.Stop(600),"old read exits");order.Add("exit");},
                    ()=>order.Add("identity"),()=>order.Add("clear"),()=>order.Add("create"));
                Check(String.Join(",",order)=="exit,identity,clear,create" && faults==1,"rebuild serialized and original fault retained");
                Rejected(()=>policy.Rebuild(true,()=>{},()=>{},()=>{},()=>{}),"one rebuild per session only");
                var activePolicy=new FactoryIdleDiRebuildPolicy();bool created=false;
                Rejected(()=>activePolicy.Rebuild(false,()=>{},()=>{},()=>{},()=>created=true),"active/output-owning state cannot rebuild");
                Check(!created,"unsafe state creates no task");
                var pendingPolicy=new FactoryIdleDiRebuildPolicy();
                Rejected(()=>pendingPolicy.Rebuild(true,()=>{throw new Exception("read pending");},()=>{},()=>created=true,()=>created=true),"pending old reader blocks all task changes");
                Check(!created,"no clear or create after failed reader stop");
                foreach(int failAt in new[]{1,2,3,4})
                {
                    var failing=new FactoryIdleDiRebuildPolicy();var stages=new List<int>();
                    Action<int> stage=i=>{stages.Add(i);if(i==failAt)throw new Exception("fake failed stage "+i);};
                    Rejected(()=>failing.Rebuild(true,()=>stage(1),()=>stage(2),()=>stage(3),()=>stage(4)),"lifecycle failure is retained "+failAt);
                    Check(stages.Count==failAt,"no later lifecycle action after failure "+failAt);
                    Rejected(()=>failing.Rebuild(true,()=>stage(1),()=>stage(2),()=>stage(3),()=>stage(4)),"failed rebuild cannot spin "+failAt);
                    Check(stages.Count==failAt && failing.Attempted,"ownership/attempt retained "+failAt);
                }
            }
        }
    }
}
