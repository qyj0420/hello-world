using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Xiongxin.FactoryTest;

internal static class FactoryArtCleanupFaultSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool ok,string why) { checks++;if(!ok)throw new Exception(why); }
    static object Get(object o,string f) { return o.GetType().GetField(f,B).GetValue(o); }
    static void Set(object o,string f,object v) { o.GetType().GetField(f,B).SetValue(o,v); }
    static Exception Reject(Action run) { try { run(); } catch(Exception e) { return e; } throw new Exception("Expected failure"); }
    sealed class Fake : IFactoryArtTaskIo
    {
        internal readonly List<string> Calls=new List<string>();
        internal readonly Dictionary<string,int> Failures=new Dictionary<string,int>();
        internal readonly HashSet<IntPtr> Released=new HashSet<IntPtr>();
        internal string ThrowOn;
        int Op(string op,IntPtr task)
        {
            FactoryArtCleanupFaultSmoke.Check(task!=IntPtr.Zero&&!Released.Contains(task),"never use zero/released task: "+op);
            Calls.Add(op);if(op==ThrowOn)throw new Exception("injected native throw: "+op);
            int result;return Failures.TryGetValue(op,out result)?result:0;
        }
        public int Stop(IntPtr task) { return Op("stop:"+task,task); }
        public int Clear(IntPtr task) { int code=Op("clear:"+task,task);if(code>=0)Released.Add(task);return code; }
        public int WriteLines(IntPtr task,int count,uint start,double timeout,uint layout,byte[] data,out int written,IntPtr reserved)
        {
            FactoryArtCleanupFaultSmoke.Check(count==1&&start==1&&timeout==.020&&layout==0&&data.Length==1&&data[0]==0&&reserved==IntPtr.Zero,"only unchanged bounded single-line LOW write");
            written=1;return Op("low:"+task,task);
        }
        public int WriteAnalog(IntPtr task,int count,uint start,double timeout,uint layout,double[] data,out int written,IntPtr reserved)
        {
            FactoryArtCleanupFaultSmoke.Check(count==1&&start==1&&timeout==.020&&layout==0&&data.Length==1&&data[0]==0&&reserved==IntPtr.Zero,"only unchanged bounded AO zero write");
            written=1;return Op("zero:"+task,task);
        }
        public void Check(int code,string operation) { if(code<0)throw new InvalidOperationException(operation+"; FAKE ART "+code); }
    }
    static readonly string[] Names={"optical","hv","ao","indicator","currentMode","finite","monitor","emergency"};
    static NativeFactoryBackend Backend(Fake f,bool tasks=true)
    {
        var backend=new NativeFactoryBackend(f);
        Set(backend,"identitiesVerified",true);
        if(tasks)for(int i=0;i<Names.Length;i++) {
            object task=Get(backend,Names[i]);Set(task,"Handle",new IntPtr(i+1));Set(task,"Configured",true);Set(task,"Started",true);
        }
        // In-memory command history only; no positive output call.
        ((FactoryHvCommandState)Get(backend,"hvCommands")).Write(true,_=>{});
        return backend;
    }
    static IntPtr Handle(NativeFactoryBackend b,string task) { return (IntPtr)Get(Get(b,task),"Handle"); }
    static void AssertNoDrivers()
    {
        foreach(ProcessModule module in Process.GetCurrentProcess().Modules)
            Check(!module.ModuleName.Equals("Art_DAQ.dll",StringComparison.OrdinalIgnoreCase)&&!module.ModuleName.StartsWith("ps5000",StringComparison.OrdinalIgnoreCase),"fixture must not load ART/Pico drivers");
    }
    static int Main()
    {
        try {
            AssertNoDrivers();
            var f=new Fake();var b=Backend(f);var reports=new List<string>();b.ConnectionReport=(k,v)=>reports.Add(k);
            b.Dispose();Check(f.Released.Count==8&&Names.All(n=>Handle(b,n)==IntPtr.Zero),"successful clear relinquishes all exact handles");
            Check(f.Calls.Take(5).SequenceEqual(new[]{"low:1","low:2","zero:3","low:4","low:5"}),"shutdown all outputs before clearing tasks");
            Check(reports.Contains("LINK_ART_CLOSED")&&reports.Contains("LINK_PICO_CLOSED"),"successful release emits lifecycle evidence, not physical discharge");
            int calls=f.Calls.Count;b.Dispose();Check(f.Calls.Count==calls,"successful duplicate dispose does not call native IO");

            f=new Fake();f.Failures["low:1"]=-88709;b=Backend(f);reports.Clear();b.ConnectionReport=(k,v)=>reports.Add(k);
            Exception first=Reject(b.Dispose);
            Check(first.ToString().Contains("-88709")&&b.Faulted,"aborted/removed task error is retained and fault latched");
            Check(f.Calls.Contains("low:2")&&f.Calls.Contains("zero:3")&&f.Released.Count==8,"one LOW error cannot suppress independent shutdown or clear attempts");
            Check(!reports.Contains("LINK_ART_CLOSED"),"release alone cannot publish clean close after LOW failure");
            calls=f.Calls.Count;Exception again=Reject(b.Dispose);
            Check(ReferenceEquals(first,again)&&f.Calls.Count==calls,"repeated dispose retains original failure without touching released handles");
            Check(!b.CanReuseSession,"failed cleanup cannot reopen/reuse session");

            f=new Fake();f.Failures["stop:6"]=-88709;b=Backend(f);
            first=Reject(b.Dispose);Check(first.ToString().Contains("-88709")&&f.Released.Count==8,"failed Stop still attempts Clear but does not hide error");
            calls=f.Calls.Count;Reject(b.Dispose);Check(calls==f.Calls.Count,"Stop failure remains terminal despite successful Clear");

            f=new Fake();f.Failures["clear:1"]=-88709;b=Backend(f);reports.Clear();b.ConnectionReport=(k,v)=>reports.Add(k);
            Reject(b.Dispose);Check(Handle(b,"optical")==new IntPtr(1)&&f.Released.Count==7,"failed Clear retains exact owned handle only");
            Check(!(bool)Get(Get(b,"optical"),"Started")&&(bool)Get(Get(b,"optical"),"Configured"),"successful Stop and failed Clear are recorded separately");
            f.Failures.Clear();first=Reject(b.Dispose);
            Check(Handle(b,"optical")==IntPtr.Zero&&f.Released.Count==8&&first.ToString().Contains("-88709"),"retry can release remaining resources without erasing historical failure");
            Check(!reports.Contains("LINK_ART_CLOSED")&&!b.CanReuseSession,"retry never produces clean ACK or new output permission");
            calls=f.Calls.Count;Reject(b.Dispose);Check(f.Calls.Count==calls,"fully released failed retry performs no additional IO");

            f=new Fake{ThrowOn="clear:6"};b=Backend(f);Reject(b.Dispose);
            Check(Handle(b,"finite")==new IntPtr(6)&&f.Released.Count==7,"thrown Clear retains handle and does not stop other tasks releasing");
            f.ThrowOn=null;Reject(b.Dispose);Check(f.Released.Count==8,"managed/native exception history also persists after retry");

            f=new Fake();f.Failures["low:1"]=-88709;b=Backend(f);b.ConnectionReport=(k,v)=>{throw new Exception("injected logger failure");};
            first=Reject(b.Dispose);Check(first.ToString().Contains("-88709")&&first.ToString().Contains("injected logger failure"),"reporter cannot replace original ART error");
            Check(f.Released.Count==8,"report failure does not interrupt remaining cleanup");

            f=new Fake();b=Backend(f);reports.Clear();b.ConnectionReport=(k,v)=>{reports.Add(k);throw new Exception("injected close reporter failure");};
            first=Reject(b.Dispose);calls=f.Calls.Count;again=Reject(b.Dispose);
            Check(reports.Contains("LINK_ART_CLOSED")&&reports.Contains("LINK_PICO_CLOSED"),"failed close reporter does not prevent independent close result report");
            Check(ReferenceEquals(first,again)&&f.Calls.Count==calls,"terminal reporting error cannot turn into later clean Dispose");

            f=new Fake();b=Backend(f);b.StopCapture();
            Check(f.Calls.SequenceEqual(new[]{"stop:6","clear:6"}),"StopCapture stops finite input only, not DO/AO/connection");
            Check(Handle(b,"hv")==new IntPtr(2)&&Handle(b,"optical")==new IntPtr(1),"normal round stop preserves output ownership");b.Dispose();

            f=new Fake();b=Backend(f);Set(b,"zeroInputRound",true);b.Dispose();
            Check(!f.Calls.Any(c=>c.StartsWith("low:")||c.StartsWith("zero:")),"input-only close never writes outputs");
            f=new Fake();b=Backend(f,false);b.Dispose();Check(f.Calls.Count==0,"never-opened backend disposal has no SDK calls");
            AssertNoDrivers();Console.WriteLine("FACTORY_ART_CLEANUP_FAULT_PASS="+checks+"; HARDWARE_CALLS=0; DRIVERS_LOADED=0; SYNTHETIC_HANDLES_ONLY=TRUE");return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
