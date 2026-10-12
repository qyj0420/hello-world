using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryIdleEmergencyRetryTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,name)=>{++checks;if(!ok)throw new InvalidOperationException("Idle P0.4 retry: "+name);};
            Func<Exception> timeout=()=>new FactoryEmergencyReadTimeoutException(-26802,new InvalidOperationException("fake SDK timeout"));
            check(NativeFactoryBackend.IsEmergencySdkTimeout(-26802) &&
                NativeFactoryBackend.IsEmergencySdkTimeout(-200284) && !NativeFactoryBackend.IsEmergencySdkTimeout(-1) &&
                !NativeFactoryBackend.IsEmergencySdkTimeout(0),"only exact official pending status candidates; actual zero is separately required by the native receipt adapter");
            int faults=0,reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++reads;throw timeout();},()=>++faults))
            {
                Expect(monitor.Start);
                check(reads==1 && faults==1,"default/active first timeout remains latched");
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{if(++reads==1)throw timeout();return true;},()=>++faults))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();
                check(monitor.ReadReleased() && faults==0 && reads>=2,"idle short returned timeout retries and accepts new high");
                var events=new List<string>();monitor.DrainRetryEvents((key,value)=>events.Add(key+"|"+value));
                check(events.Count==2 && events[0].StartsWith("P04_IDLE_RETRY_PENDING|") && events[1].StartsWith("P04_IDLE_RETRY_RECOVERED|"),"first timeout and recovery retained");
                check(events[0].Contains("firstTimeoutUtc=") && events[0].Contains("delegateReturned=0") && events[1].Contains("retryReturnUtc="),"trace retains first and retry timestamps without inventing input");
            }
            faults=reads=0;
            using(var entered=new ManualResetEvent(false))
            using(var release=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(()=>{
                int n=Interlocked.Increment(ref reads);if(n==2)throw timeout();if(n==3){entered.Set();release.WaitOne();}return true;
            },()=>Interlocked.Increment(ref faults)))
            {
                monitor.EnableIdleTimeoutRetry();monitor.Start();
                try
                {
                    check(entered.WaitOne(1000),"single owner entered retry");
                    string detail;
                    check(monitor.ConnectionState(out detail)=="UNKNOWN" && detail.Contains("lastAcceptedLevel=HIGH（仅历史") && detail.Contains("lastAcceptedSampleUtc="),"unknown preserves explicitly historical high timestamp");
                    check(!monitor.ReadIdleReleased().HasValue,"idle pending does not report high");
                    check(Expect(()=>monitor.ReadReleased()) is FactoryEmergencyRetryPendingException &&
                        Expect(monitor.EnterActiveReadMode) is FactoryEmergencyRetryPendingException,"pending cannot grant positive output or enter active round");
                    check(reads==3,"no overlapping DI call while retry is blocked");
                    check(!monitor.Stop(1),"pending native retry must return before task clear is allowed");
                }
                finally {release.Set();}
                check(monitor.Stop(1000),"retry stop joins before clear");
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++reads;throw timeout();},()=>++faults))
            {
                monitor.EnableIdleTimeoutRetry();Expect(monitor.Start);
                check(reads==2 && faults==1 && !monitor.IdleTimeoutRebuildRequired,"second timeout latches when rebuild not authorized");
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++reads;throw timeout();},()=>++faults))
            {
                monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();monitor.Start();
                string detail;
                check(reads==2 && faults==0 && monitor.IdleTimeoutRebuildRequired && monitor.ConnectionState(out detail)=="UNKNOWN","double short idle timeout defers only to owner rebuild, no global reset needed");
                check(Expect(monitor.EnterActiveReadMode) is FactoryEmergencyRetryPendingException,"exhausted retry never permits new round");
                var keys=new List<string>();monitor.DrainRetryEvents((key,value)=>keys.Add(key));
                check(keys.Count==2 && keys[1]=="P04_IDLE_RETRY_EXHAUSTED","rebuild-required is not terminal communication fault");
            }
            foreach(int failureCase in new[]{0,1,2})
            {
                faults=reads=0;
                using(var monitor=new FactoryEmergencyMonitor(()=>{
                    int n=++reads;
                    if(failureCase==0)return false;
                    if(failureCase==1)throw new InvalidOperationException("not timeout");
                    if(n==1)throw timeout();return false;
                },()=>++faults))
                {
                    monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();
                    if(failureCase==1)Expect(monitor.Start);else monitor.Start();
                    check(faults==1 && !monitor.IdleTimeoutRebuildRequired && reads==(failureCase==2?2:1),"LOW/non-timeout never reopens or retries: "+failureCase);
                    if(failureCase!=1)check(!monitor.ReadReleased(),"valid low remains latched: "+failureCase);
                }
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++reads;Thread.Sleep(125);throw timeout();},()=>++faults))
            {
                monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();Expect(monitor.Start);
                check(reads==1 && faults==1 && !monitor.IdleTimeoutRebuildRequired,"returned timeout beyond 100ms is not retried/rebuilt");
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{++reads;Thread.Sleep(60);throw timeout();},()=>++faults))
            {
                monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();Expect(monitor.Start);
                check(reads==2 && faults==1 && !monitor.IdleTimeoutRebuildRequired,"two individually short reads cannot extend aggregate 100ms deadline");
            }
            faults=reads=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>{if(++reads==1)throw timeout();throw new InvalidOperationException("second read not timeout");},()=>++faults))
            {
                monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();Expect(monitor.Start);
                check(reads==2 && faults==1 && !monitor.IdleTimeoutRebuildRequired,"second non-timeout error latches, no task rebuild");
            }
            faults=reads=0;
            using(var ready=new ManualResetEvent(false))
            using(var release=new ManualResetEvent(false))
            using(var monitor=new FactoryEmergencyMonitor(()=>{int n=Interlocked.Increment(ref reads);if(n==2){ready.Set();release.WaitOne();throw timeout();}return true;},()=>Interlocked.Increment(ref faults)))
            {
                monitor.AllowOneIdleRebuildDeferral();monitor.EnableIdleTimeoutRetry();monitor.Start();
                check(ready.WaitOne(1000),"idle read in flight before active boundary");
                try {monitor.EnterActiveReadMode();} finally {release.Set();}
                check(SpinWait.SpinUntil(()=>faults!=0,1000) && reads==2 && !monitor.IdleTimeoutRebuildRequired,"late idle timeout after active boundary cannot retry");
            }
            foreach(int failAt in new[]{0,1,2,3,4})
            {
                var policy=new FactoryIdleDiRebuildPolicy();var order=new List<int>();
                Action<int> step=i=>{order.Add(i);if(i==failAt)throw new InvalidOperationException("fake ownership failure "+i);};
                if(failAt==0)policy.Rebuild(true,()=>step(1),()=>step(2),()=>step(3),()=>step(4));
                else Expect(()=>policy.Rebuild(true,()=>step(1),()=>step(2),()=>step(3),()=>step(4)));
                check(order.Count==(failAt==0?4:failAt),"stop / identity probe / clear / create advance only after success: "+failAt);
                Expect(()=>policy.Rebuild(true,()=>step(1),()=>step(2),()=>step(3),()=>step(4)));
                check(policy.Attempted && order.Count==(failAt==0?4:failAt),"only one rebuild attempt per session even after failure: "+failAt);
            }
            var denied=new FactoryIdleDiRebuildPolicy();bool called=false;
            Expect(()=>denied.Rebuild(false,()=>called=true,()=>called=true,()=>called=true,()=>called=true));
            check(!called && !denied.Attempted,"non-idle or any output possibility cannot touch task");
            check(FactoryEmergencyMonitor.MaximumAgeMs==100 && FactoryEmergencyMonitor.RequestedPeriodMs==10 && NativeFactoryBackend.DriverIoTimeoutSeconds==.020,
                "read ages and SDK protection unchanged");
            return "Idle P0.4 timeout retry/rebuild: "+checks+" checks PASS; fake delegates/resources only; no hardware.";
        }
        private static Exception Expect(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new InvalidOperationException("Expected rejection did not occur.");}
    }
}
