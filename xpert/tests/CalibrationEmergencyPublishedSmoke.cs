using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// Loads the exact staged executable as a library. No Main, worker process,
// production connector, SDK method or hardware adapter is instantiated.
internal static class CalibrationEmergencyPublishedSmoke
{
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly product;
    static Type monitorType,artErrorType;
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    static void Until(Func<bool> ready,string name,int timeout=2000){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<timeout)Thread.Sleep(2);Check(ready(),name);}
    static void Reject(Task<bool> task,string name){Until(()=>task.IsCompleted,name+" completes");try{task.GetAwaiter().GetResult();}catch{checks++;return;}throw new Exception(name+" unexpectedly accepted");}
    static Exception ArtError(int code){return (Exception)Activator.CreateInstance(artErrorType,Instance,null,new object[]{code},null);}
    sealed class Monitor : IDisposable
    {
        readonly object owner;
        internal Monitor(Func<int,bool> read,Func<bool> context,Action<Exception> cancel,Action rebuild=null,Action<object> changed=null)
        {
            var ctor=monitorType.GetConstructors(Instance).Single();
            Delegate notification=null;
            if(changed!=null){
                Type delegateType=ctor.GetParameters()[4].ParameterType;
                var parameter=Expression.Parameter(delegateType.GetGenericArguments()[0],"snapshot");
                notification=Expression.Lambda(delegateType,Expression.Invoke(Expression.Constant(changed),Expression.Convert(parameter,typeof(object))),parameter).Compile();
            }
            owner=ctor.Invoke(new object[]{read,context,cancel,rebuild,notification});
        }
        internal void Start(){monitorType.GetMethod("Start",Instance).Invoke(owner,null);}
        internal Task<bool> Fresh(){return (Task<bool>)monitorType.GetMethod("ReadFreshAsync",Instance).Invoke(owner,null);}
        internal bool Stop(int ms=1000){return (bool)monitorType.GetMethod("Stop",Instance).Invoke(owner,new object[]{ms});}
        internal object Snapshot {get{return monitorType.GetProperty("Snapshot",Instance).GetValue(owner,null);}}
        internal object Field(string name){return Snapshot.GetType().GetField(name,Instance).GetValue(Snapshot);}
        internal Exception Error {get{return (Exception)Field("Error");}}
        internal long Sequence {get{return (long)Field("Sequence");}}
        internal bool? High {get{return (bool?)Field("High");}}
        internal bool InFlight {get{return (bool)Field("ReadInFlight");}}
        public void Dispose(){((IDisposable)owner).Dispose();}
    }
    static object Field(object value,string name){return value.GetType().GetField(name,Instance).GetValue(value);}
    static void ConstantsAndFresh()
    {
        foreach(var pair in new[]{new KeyValuePair<string,int>("MinimumDelayMs",10),new KeyValuePair<string,int>("FirstReadTimeoutMs",300),new KeyValuePair<string,int>("NoDataDeadlineMs",500)})
            Check((int)monitorType.GetField(pair.Key,BindingFlags.Static|BindingFlags.NonPublic).GetRawConstantValue()==pair.Value,"published "+pair.Key);
        var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int calls=0,cancels=0,inside=0,maxInside=0;
        var clock=Stopwatch.StartNew();var starts=new List<long>();var ends=new List<long>();
        using(var monitor=new Monitor(ms=>{
            int depth=Interlocked.Increment(ref inside);maxInside=Math.Max(depth,maxInside);
            lock(starts)starts.Add(clock.ElapsedTicks);
            if(Interlocked.Increment(ref calls)==1){entered.Set();release.WaitOne();}else Thread.Sleep(5);
            lock(starts)ends.Add(clock.ElapsedTicks);Interlocked.Decrement(ref inside);return false;
        },()=>true,e=>Interlocked.Increment(ref cancels))){
            try{
                monitor.Start();Check(entered.WaitOne(1000),"first fake published read entered");var fresh=monitor.Fresh();
                Check(!fresh.IsCompleted,"fresh check rejects an earlier in-flight result");release.Set();
                Until(()=>fresh.IsCompleted,"new read satisfies fresh request");Check(!fresh.Result&&calls>=2,"post-request LOW accepted");
                Until(()=>Volatile.Read(ref calls)>=4,"independent cycles continue");Check(monitor.Stop(),"normal reader acknowledged stop");
                lock(starts)for(int i=1;i<starts.Count;i++)Check((starts[i]-ends[i-1])*1000.0/Stopwatch.Frequency>=9.8,"at least 10 ms after read returns");
                Check(maxInside==1&&cancels==0,"single owner and no false cancel");Reject(monitor.Fresh(),"stopped session rejects fresh request");
            }finally{release.Set();monitor.Stop();}
        }
        entered.Dispose();release.Dispose();
    }
    static void TimeoutAndErrors()
    {
        var budgets=new List<int>();int calls=0,cancels=0;bool sawUnknown=false;
        using(var monitor=new Monitor(ms=>{
            budgets.Add(ms);if(Interlocked.Increment(ref calls)==1){Thread.Sleep(280);throw ArtError(-26802);}return false;
        },()=>true,e=>Interlocked.Increment(ref cancels),null,s=>{if((bool)Field(s,"RetryPending")&&Field(s,"High")==null)sawUnknown=true;})){
            monitor.Start();var pending=monitor.Fresh();Until(()=>pending.IsCompleted,"bounded exact-code retry completes");
            Check(!pending.Result,"timely retry returned LOW");Check(monitor.Stop(),"retry reader stopped");
            Check(budgets[0]>0&&budgets[0]<=300&&budgets[1]>0&&budgets[1]<=220,"at most 300 ms initial and original-deadline remaining retry budget");
            Check(sawUnknown&&cancels==0,"retry publishes unknown without inventing fault or LOW");
        }
        foreach(int code in new[]{-200284,-200474,-50401,-200088,-987654}){
            int reads=0,stops=0,rebuilds=0;
            using(var monitor=new Monitor(ms=>{Interlocked.Increment(ref reads);throw ArtError(code);},()=>true,
                e=>Interlocked.Increment(ref stops),()=>Interlocked.Increment(ref rebuilds))){
                monitor.Start();Until(()=>Volatile.Read(ref stops)==1,"official/nonclassified error cancelled");Check(monitor.Stop(),"error reader exited");
                Check(reads==1&&rebuilds==0&&(int)artErrorType.GetField("Status",Instance).GetValue(monitor.Error)==code,"non-timeout status preserved without retry");
            }
        }
        int highCalls=0,highCancels=0;
        using(var monitor=new Monitor(ms=>{Interlocked.Increment(ref highCalls);return true;},()=>true,e=>Interlocked.Increment(ref highCancels))){
            monitor.Start();Until(()=>Volatile.Read(ref highCancels)==1,"HIGH cancels immediately");Check(monitor.Stop(),"HIGH reader stops");
            Check(highCalls==1&&monitor.High==true&&monitor.Error.GetType().Name=="CalibrationEmergencyReleasedException","dangerous HIGH retained accurately");
        }
    }
    static void SecondTimeoutRebuild()
    {
        int reads=0,inside=0,cancels=0,rebuilds=0;bool overlap=false;var order=new List<string>();
        using(var monitor=new Monitor(ms=>{Interlocked.Increment(ref inside);Interlocked.Increment(ref reads);Thread.Sleep(30);Interlocked.Decrement(ref inside);throw ArtError(-26802);},()=>true,
            e=>{lock(order)order.Add("cancel");Interlocked.Increment(ref cancels);},()=>{
                overlap=Volatile.Read(ref inside)!=0;lock(order)order.Add("rebuild");Interlocked.Increment(ref rebuilds);
            })){
            monitor.Start();var fresh=monitor.Fresh();Reject(fresh,"second timeout rejects current round");Until(()=>Volatile.Read(ref rebuilds)==1,"bounded DI rebuild attempted");
            Check(monitor.Stop(),"rebuild worker stops");Check(reads==2&&cancels==1&&rebuilds==1&&!overlap,"read returns before unique recovery");
            lock(order)Check(String.Join(",",order)=="cancel,rebuild","cancellation precedes recovery");
            Check(monitor.High==null&&monitor.Error!=null,"recovery supplies no LOW and cannot resume acquisition");
        }
    }
    static void DeadlineDuringGapAndBudget()
    {
        int reads=0,cancels=0;var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);
        using(var monitor=new Monitor(ms=>{Interlocked.Increment(ref reads);return false;},()=>true,e=>Interlocked.Increment(ref cancels),null,s=>{
            if(Field(s,"Error")==null&&(long)Field(s,"Sequence")==1){entered.Set();release.WaitOne();}
        })){
            try{
                monitor.Start();Check(entered.WaitOne(1000),"valid result then notification scheduling gap");var pending=monitor.Fresh();
                Until(()=>Volatile.Read(ref cancels)==1,"500 ms deadline includes inter-read gap",1500);Reject(pending,"gap invalidates waiting permission");
                Check(!monitor.InFlight&&monitor.High==null&&reads==1,"past LOW expires without an active native call");
            }finally{release.Set();Check(monitor.Stop(),"notification gap owner exits");}
        }
        entered.Dispose();release.Dispose();
        reads=cancels=0;int secondBudget=0;
        using(var monitor=new Monitor(ms=>{if(Interlocked.Increment(ref reads)==1)return false;secondBudget=ms;Thread.Sleep(ms+35);return false;},()=>true,
            e=>Interlocked.Increment(ref cancels),null,s=>{if(Field(s,"Error")==null&&(long)Field(s,"Sequence")==1)Thread.Sleep(270);})){
            monitor.Start();Until(()=>Volatile.Read(ref reads)==2,"post-gap read entered");var pending=monitor.Fresh();
            Until(()=>Volatile.Read(ref cancels)==1,"post-gap late result faults");Reject(pending,"late LOW cannot refresh permit");Check(monitor.Stop(),"late read returned");
            Check(secondBudget>0&&secondBudget<=230&&monitor.Sequence==1&&monitor.High==null,"remaining budget and last-valid sequence preserved");
        }
    }
    static void HungReadAndSessionIsolation()
    {
        var entered=new ManualResetEvent(false);var release=new ManualResetEvent(false);int oldReads=0,oldCancels=0,rebuilds=0,newCancels=0;
        using(var old=new Monitor(ms=>{Interlocked.Increment(ref oldReads);entered.Set();release.WaitOne();return false;},()=>true,
            e=>Interlocked.Increment(ref oldCancels),()=>Interlocked.Increment(ref rebuilds))){
            try{
                old.Start();Check(entered.WaitOne(1000),"hung old session entered");var waiting=old.Fresh();
                Until(()=>Volatile.Read(ref oldCancels)==1,"watchdog cancels while read remains blocked",1500);Reject(waiting,"hung old session waiters rejected");
                Check(old.InFlight&&!old.Stop(10)&&oldReads==1&&rebuilds==0,"timeout not mistaken for call exit or authority to clear/rebuild");
                using(var current=new Monitor(ms=>false,()=>true,e=>Interlocked.Increment(ref newCancels))){
                    current.Start();var fresh=current.Fresh();Until(()=>fresh.IsCompleted,"independent new fake session receives its own result");Check(!fresh.Result,"new fake session LOW valid");
                    release.Set();Check(old.Stop(),"old callback genuinely ended");
                    Check(old.High==null&&old.Error!=null&&current.Error==null&&newCancels==0,"late old LOW cannot clear old fault or update independent new owner");
                    Check(current.Stop(),"new fake session stops");
                }
            }finally{release.Set();old.Stop();}
        }
        entered.Dispose();release.Dispose();
    }
    static int Main(string[] args)
    {
        try{
            string assemblyPath=Path.Combine(Path.GetFullPath(args[0]),"modules","CalibrationAcquisition","XIONGXIN.Calibration.Verification.exe");
            product=Assembly.LoadFrom(assemblyPath);
            monitorType=product.GetType("Xiongxin.Calibration.CalibrationEmergencyMonitor",true);
            artErrorType=product.GetType("Xiongxin.Calibration.CalibrationArtException",true);
            ConstantsAndFresh();TimeoutAndErrors();SecondTimeoutRebuild();DeadlineDuringGapAndBudget();HungReadAndSessionIsolation();
            Check((IntPtr)product.GetType("Xiongxin.Calibration.Art",true).GetField("module",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==IntPtr.Zero,"ART native module never loaded");
            Console.WriteLine("CALIBRATION_EMERGENCY_PUBLISHED_PASS="+checks+"; exact staged executable, reflection + fake delegates only; no Main, worker, SDK or hardware.");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
