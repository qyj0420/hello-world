using System;
using System.Collections.Generic;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // All reads below are in-memory, with an explicit monotonic fake clock.
    // Never instantiates NativeFactoryBackend or invokes any SDK/device.
    internal static class FactoryFeedbackAvailabilityTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> ok=(value,why)=>{if(!value)throw new Exception("AI availability: "+why);checks++;};
            var plan=new FactoryArtFeedbackPlan(new FactorySettings {TargetKv=5,MaximumKv=5,FeedbackWindowMs=10});
            foreach(int arrival in new[]{20,50,99})
            {
                var b=new Buffer(4,arrival,10);
                var f=Read(plan,b);
                ok(b.Reads==1 && b.FirstReadAt==arrival && b.Requests[0]==14,"late USB block read only after availability "+arrival);
                ok(f.FreshWindowBoundaryQueued==4 && f.FreshWindowSamplesAfterBoundary==10 && f.WindowSampleCount==10,
                    "fixed initial boundary and ten genuinely new samples "+arrival);
                ok(f.DividerMedianRawV==.02 && f.ImonMaximumRawV==4.9 && f.DividerMinimumRawV==-.3,
                    "new control window, every old current/voltage peak retained "+arrival);
                ok(b.EmergencyChecks>=arrival && b.ReadBeforeAvailable==0,"DI supervised while waiting; no future SDK request "+arrival);
            }
            var slow=new Buffer(4,100,10);var error=Fail(()=>Read(plan,slow));
            ok(error.Message.Contains("100 ms") && slow.Reads==0 && slow.Now==100,"arrival at deadline cannot extend 100 ms or read");
            var stale=new Buffer(4,Int32.MaxValue,10);error=Fail(()=>Read(plan,stale));
            ok(stale.Reads==0 && error.Message.Contains("100 ms"),"old queued samples never substitute fresh window");
            var low=new Buffer(4,50,10){LowAt=12};error=Fail(()=>Read(plan,low));
            ok(error is FactoryEmergencyStopException && low.Reads==0 && low.Now==12,"LOW during wait prevents read and continuation");
            var unknown=new Buffer(4,50,10){UnknownAt=12};error=Fail(()=>Read(plan,unknown));
            ok(error.Message.Contains("unknown DI") && unknown.Reads==0,"unknown DI cannot act as HIGH");
            using(var cancel=new CancellationTokenSource())
            {
                var b=new Buffer(4,50,10);b.AfterWait=()=>{if(b.Now==12)cancel.Cancel();};
                error=Fail(()=>Read(plan,b,cancel.Token));
                ok(error is OperationCanceledException && b.Reads==0 && b.Now==12,"same round token cancels pre-read wait promptly");
            }
            var longPlan=new FactoryArtFeedbackPlan(new FactorySettings {TargetKv=5,MaximumKv=5,FeedbackWindowMs=40});
            var chunks=new Buffer(4,20,10){ArrivalSpacing=10,ArrivalGroups=4};var full=Read(longPlan,chunks);
            ok(chunks.Reads==4 && chunks.Now==50 && full.WindowSampleCount==40 && full.FreshWindowSamplesAfterBoundary==40,
                "long fresh window accumulates separate available complete batches in same budget");
            ok(full.ImonMaximumRawV==4.9 && full.DividerMinimumRawV==-.3,"old peaks survive four chunks");
            var burst=new Buffer(4,20,30);full=Read(plan,burst);
            ok(full.BacklogAfter==20 && burst.Reads==1,"original final twenty-point backlog cap retained");
            burst=new Buffer(4,20,64);full=Read(plan,burst);
            ok(full.BacklogAfter==0 && burst.Reads==2 && full.SampleCount==68,"large available burst drained without dropping peaks");
            var queued=new Buffer(1001,0,0);error=Fail(()=>Read(plan,queued));
            ok(queued.Reads==0 && error.Message.Contains("1001"),"one-second queue cap unchanged");
            var nan=new Buffer(4,20,10){OldDivider=Double.NaN};error=Fail(()=>Read(plan,nan));
            ok(nan.Reads==1 && error.Message.Contains("非有限"),"old NaN cannot be dropped in favor of new tail");
            var saturated=new Buffer(4,20,10){OldDivider=1};error=Fail(()=>Read(plan,saturated));
            ok(saturated.Reads==1 && error.Message.Contains("饱和"),"old saturated peak still prevents valid feedback");
            var blocked=new Buffer(4,99,10){ReadDuration=2};error=Fail(()=>Read(plan,blocked));
            ok(blocked.Reads==1 && error.Message.Contains("100 ms"),"SDK return after total deadline rejected");
            var fixedClock=new Buffer(4,Int32.MaxValue,10);int waits=0;
            error=Fail(()=>FactoryFeedbackStream.Read(plan,fixedClock,0,()=>0,true,true,default(CancellationToken),ms=>waits++));
            ok(waits==100 && fixedClock.Reads==0 && error.Message.Contains("100次"),"broken injected clock cannot spin without a bound");

            foreach(int status in new[]{-200284,-26802,-1})foreach(int actual in new[]{-1,0,7,14})
            {
                var receipt=new FactoryAnalogReadReceipt(status,14,actual,42,14,20);int observations=0;
                error=Fail(()=>receipt.Validate((code,detail)=>{observations++;if(code<0)throw new InvalidOperationException(detail);}));
                bool pending=FactoryEmergencyReadPolicy.IsZeroSamplePending(status,actual);
                ok(observations==(pending?0:1) && (error is FactoryAnalogReadPendingException)==pending &&
                    error.Message.Contains("status="+status) && error.Message.Contains("actualPerChannel="+actual),
                    "negative status never accepted; only explicit zero pending is nonfatal "+status+"/"+actual);
            }
            foreach(int actual in new[]{-1,0,7,15})
            {
                var receipt=new FactoryAnalogReadReceipt(0,14,actual,42,14,1);
                ok(Fail(()=>receipt.Validate((s,d)=>{}))!=null,"successful status with wrong count remains invalid "+actual);
            }
            new FactoryAnalogReadReceipt(0,14,14,42,14,1).Validate((s,d)=>{});checks++;
            new FactoryAnalogReadReceipt(1,14,14,42,14,1).Validate((s,d)=>{});checks++;
            ok(Fail(()=>new FactoryAnalogReadReceipt(0,14,14,41,14,1).Validate((s,d)=>{}))!=null,"wrong interleaved array size rejected");
            CheckStartupWindow(plan,ok);
            return "ART AI availability: "+checks+" offline checks PASS; fake clock/SDK only; HardwareAccessed=False; SDKCalled=False";
        }
        private static void CheckStartupWindow(FactoryArtFeedbackPlan plan,Action<bool,string> ok)
        {
            ok(new FactorySettings().InitialFeedbackTimeoutMs==2000,"startup total uses the existing two-second default");
            var scheduled=new Buffer(0,99,10){WaitQuantum=16};
            var error=Fail(()=>Read(plan,scheduled));
            ok(error.Message.Contains("100 ms") && scheduled.Now==112 && scheduled.Reads==0,
                "reproduce running read rejection when a 1 ms wait is scheduled at 16 ms granularity");
            var startup=new FactoryFeedbackStartupWindow();startup.Started(2000,true);
            scheduled=new Buffer(0,99,10){WaitQuantum=16};
            var f=ReadInitial(plan,scheduled,startup);
            ok(scheduled.Now==112 && scheduled.Reads==1 && f.WindowSampleCount==10 && f.ReadElapsedMs==0,
                "zero-output first frame prepares at 112 ms then completes a separately bounded read");
            ok(!startup.NeedsPreparation(0,false,true),"only a valid complete first frame consumes startup preparation");
            scheduled=new Buffer(0,99,10){WaitQuantum=16};
            ok(Fail(()=>Read(plan,scheduled)).Message.Contains("100 ms") && scheduled.Reads==0,
                "the next running read is not granted another startup allowance");

            startup=new FactoryFeedbackStartupWindow();startup.Started(2000,true);
            ok(!startup.NeedsPreparation(.05,false,true) && !startup.NeedsPreparation(0,true,true) &&
                !startup.NeedsPreparation(0,false,false),"positive AO, pending post-AO window, or invalid lifecycle cannot use startup wait");
            startup.ObserveOutput(.05);
            ok(!startup.NeedsPreparation(0,false,true),"later zero AO cannot re-enable the first-frame allowance after positive output");
            startup.Started(2000,false);
            ok(!startup.NeedsPreparation(0,false,true),"fault/optical restart cannot acquire a startup allowance");
            startup.Started(2000,true);
            ok(startup.RemainingMilliseconds(1950)==50 && startup.RemainingMilliseconds(1999)==1,
                "time spent starting and scheduling is charged to the same existing total deadline");
            ok(Fail(()=>startup.RemainingMilliseconds(2000)).Message.Contains("2000 ms") &&
                Fail(()=>startup.RemainingMilliseconds(-1))!=null,"deadline boundary and a regressing clock fail closed");
            scheduled=new Buffer(0,2000,10){WaitQuantum=16};
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(error.Message.Contains("启动总期限") && scheduled.Now==2000 && scheduled.Reads==0,
                "a complete block at the startup deadline remains rejected without reading");

            startup=new FactoryFeedbackStartupWindow();startup.Started(2000,true);
            scheduled=new Buffer(0,1990,10){ReadDuration=11};
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(scheduled.Reads==1 && error.Message.Contains("10 ms"),"first native read cannot spend beyond remaining startup total budget");
            ok(startup.NeedsPreparation(0,false,true),"a failed first read never records startup success");
            ok(Fail(()=>FactoryFeedbackStream.Read(plan,new Buffer(10,0,0),0,()=>0,true,false,
                default(CancellationToken),ms=>{},101)) is ArgumentOutOfRangeException,
                "no caller can widen the running/drain deadline above 100 ms");

            startup=new FactoryFeedbackStartupWindow();startup.Started(2000,true);
            scheduled=new Buffer(0,300,10){LowAt=12};
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(error is FactoryEmergencyStopException && scheduled.Now==12 && scheduled.Reads==0,
                "LOW in zero-output startup wait interrupts before any AI read");
            scheduled=new Buffer(0,300,10){UnknownAt=12};
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(error.Message.Contains("unknown DI") && scheduled.Reads==0,"unknown DI is not ignored in startup preparation");
            using(var cancel=new CancellationTokenSource())
            {
                scheduled=new Buffer(0,300,10);scheduled.AfterWait=()=>{if(scheduled.Now==12)cancel.Cancel();};
                error=Fail(()=>ReadInitial(plan,scheduled,startup,cancel.Token));
                ok(error is OperationCanceledException && scheduled.Now==12 && scheduled.Reads==0,
                    "manual cancellation interrupts the initial wait independently of the two-second total");
            }
            scheduled=new Buffer(1001,0,0);
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(error.Message.Contains("1001") && scheduled.Reads==0,"startup does not relax the existing one-second backlog cap");
            scheduled=new Buffer(4,300,6){OldDivider=Double.NaN};
            error=Fail(()=>ReadInitial(plan,scheduled,startup));
            ok(error.Message.Contains("非有限") && scheduled.Reads==1,"old queued invalid data is inspected, not passed as a fresh valid frame");
            int waits=0;
            error=Fail(()=>FactoryFeedbackStream.WaitForInitialWindow(plan,new Buffer(0,Int32.MaxValue,10),
                ()=>0,startup,default(CancellationToken),ms=>waits++));
            ok(waits==10000 && error.Message.Contains("次数超限"),"broken startup clock has an independent finite poll bound");
            ok(Fail(()=>startup.Started(10001,true)) is ArgumentOutOfRangeException,
                "startup cannot exceed the existing validated ten-second configuration maximum");
        }
        private static FactoryFeedback ReadInitial(FactoryArtFeedbackPlan plan,Buffer buffer,
            FactoryFeedbackStartupWindow startup,CancellationToken token=default(CancellationToken))
        {
            FactoryFeedbackStream.WaitForInitialWindow(plan,buffer,()=>buffer.Now,startup,token,buffer.Wait);
            int budget=Math.Min(FactoryFeedbackStream.MaximumReadMs,startup.RemainingMilliseconds(buffer.Now));
            var f=FactoryFeedbackStream.Read(plan,buffer,0,()=>buffer.Now,true,false,token,buffer.Wait,budget);
            startup.RemainingMilliseconds(buffer.Now);startup.Complete();return f;
        }
        private static FactoryFeedback Read(FactoryArtFeedbackPlan plan,Buffer buffer,CancellationToken token=default(CancellationToken))
        {return FactoryFeedbackStream.Read(plan,buffer,.75,()=>buffer.Now,true,true,token,buffer.Wait);}
        private static Exception Fail(Action action)
        {try{action();}catch(Exception e){return e;}throw new Exception("Expected availability rejection.");}
        private sealed class Buffer:IFactoryFeedbackBuffer
        {
            internal long Now,FirstReadAt=-1;internal int Reads,ReadBeforeAvailable,EmergencyChecks;
            internal int LowAt=Int32.MaxValue,UnknownAt=Int32.MaxValue,ArrivalSpacing,ArrivalGroups=1,ReadDuration,WaitQuantum=1;
            internal double OldDivider=-.3;internal Action AfterWait;
            internal readonly List<int> Requests=new List<int>();
            private readonly int old,arrival,block;private int consumed;
            internal Buffer(int old,int arrival,int block){this.old=old;this.arrival=arrival;this.block=block;}
            public uint Available()
            {
                int groups=Now<arrival?0:ArrivalSpacing==0?ArrivalGroups:Math.Min(ArrivalGroups,1+(int)((Now-arrival)/ArrivalSpacing));
                return checked((uint)(old+groups*block-consumed));
            }
            public bool EmergencyReleased()
            {EmergencyChecks++;if(Now>=UnknownAt)throw new InvalidOperationException("unknown DI");return Now<LowAt;}
            internal void Wait(int ms){Now+=Math.Max(ms,WaitQuantum);if(AfterWait!=null)AfterWait();}
            public double[] Read(int requested)
            {
                if(Available()<requested){ReadBeforeAvailable++;throw new Exception("SDK was asked for future data.");}
                if(FirstReadAt<0)FirstReadAt=Now;Reads++;Requests.Add(requested);
                var raw=new double[requested*3];
                for(int i=0;i<requested;i++,consumed++)
                {bool prior=consumed<old;raw[i*3]=prior?4.9:.01;raw[i*3+1]=prior?.2:.002;raw[i*3+2]=prior?OldDivider:.02;}
                Now+=ReadDuration;return raw;
            }
        }
    }
}
