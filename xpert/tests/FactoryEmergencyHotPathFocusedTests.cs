using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryEmergencyHotPathFocusedTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("DI hot-path: "+why);checks++;};
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            var native=typeof(NativeFactoryBackend);
            var permission=Calls(native.GetMethod("ReadEmergencyReleased",flags));
            check(permission.Count(m=>m.DeclaringType==typeof(FactoryEmergencyMonitor)&&m.Name=="ReadReleased")==1,
                "permission consumes the existing sole DI monitor exactly once");
            check(!permission.Any(m=>m.Name=="CheckPicoConnection"||m.DeclaringType==typeof(PicoInputCapture)),
                "permission method has no Pico query/configuration/read/output call");
            check(permission.All(m=>m.DeclaringType==native||m.DeclaringType==typeof(FactoryEmergencyMonitor)),
                "permission path has only initialization/failure guards and monitor consumption");
            check(Calls(native.GetMethod("EnsureInitialized",flags)).All(m=>m.DeclaringType==typeof(InvalidOperationException)),
                "initialization guard only constructs its rejection exception, with no SDK or transport work");
            var begin=Calls(native.GetMethod("BeginRound",flags));
            check(begin.Count(m=>m.Name=="CheckPicoConnection")==2,"both former BeginRound transport checkpoints remain explicit");
            check(begin.Count(m=>m.Name=="ReadEmergencyReleased")==2,"both BeginRound DI boundaries remain");
            check(begin.FindIndex(m=>m.Name=="CheckPicoConnection")<begin.FindIndex(m=>m.Name=="VerifyArtIdentity"),
                "first transport check stays before the existing ART identity checkpoint");
            check(begin.FindLastIndex(m=>m.Name=="CheckPicoConnection")>begin.FindIndex(m=>m.Name=="CreateMonitor"),
                "last preparation transport check stays after monitor preparation");
            check(Calls(native.GetMethod("PollIdle",flags)).Count(m=>m.Name=="CheckPicoConnection")==2,
                "manual and supervised idle transport checks remain");
            check(Calls(native.GetMethod("OpenSession",flags)).Any(m=>m.DeclaringType==typeof(PicoInputCapture)&&m.Name=="Initialize"),
                "Open retains actual Pico initialization");
            var arm=Calls(native.GetMethod("ArmCore",flags));
            check(!arm.Any(m=>m.Name=="CheckPicoConnection"||m.Name=="CheckTransport"),"energized handoff adds no periodic Ping");
            string[] retained={"VerifyArtIdentity","ClearTask","ConfigureFinite","SetFreeRun","Arm","StartTask"};
            int previous=-1;
            foreach(string name in retained){int at=arm.FindIndex(m=>m.Name==name);check(at>previous,"handoff retains ordered boundary "+name);previous=at;}
            check(FactoryEmergencyReadPolicy.FirstReadTimeoutMs==300&&FactoryEmergencyReadPolicy.NoValidDataDeadlineMs==500&&
                FactoryEmergencyReadPolicy.OutputFreshnessMs==100&&FactoryEmergencyReadPolicy.AfterReturnDelayMs==10,
                "300/500 ms deadlines,100 ms freshness and10 ms after-return delay are unchanged");
            check(FactoryEmergencyReadPolicy.ReadBudgetMs(0,0)==300&&FactoryEmergencyReadPolicy.ReadBudgetMs(301,0)==199&&
                FactoryEmergencyReadPolicy.ReadBudgetMs(500,0)==0,"DI retry cannot extend the total deadline");
            check(FactoryEmergencyReadPolicy.IsFresh(100,0)&&!FactoryEmergencyReadPolicy.IsFresh(101,0)&&
                FactoryEmergencyReadPolicy.DeadlineReached(500,0),"stale HIGH and deadline still reject");

            var backend=new NativeFactoryBackend();Set(backend,"initialized",true);
            int reads=0,readerThread=0,caller=Thread.CurrentThread.ManagedThreadId;
            using(var monitor=new FactoryEmergencyMonitor(()=>{Interlocked.Increment(ref reads);readerThread=Thread.CurrentThread.ManagedThreadId;return true;},()=>Set(backend,"faulted",true)))
            {
                monitor.Start();Set(backend,"emergencyMonitor",monitor);
                for(int i=0;i<100;i++)check(backend.ReadEmergencyReleased(),"fresh HIGH permission "+i);
                check(readerThread!=caller&&Volatile.Read(ref reads)>0,"native boundary never becomes a second DI SDK reader");
                check(!backend.Faulted&&!backend.RoundActive&&!backend.SessionOpen,"DI consumption does not open/start a device or authorize a hardware round");
                monitor.Stop(600);check(Expect(backend.ReadEmergencyReleased) is InvalidOperationException&&backend.Faulted,
                    "stopped monitor rejects and preserves the backend fault latch");
            }
            backend=new NativeFactoryBackend();Set(backend,"initialized",true);
            int level=0;
            using(var monitor=new FactoryEmergencyMonitor(()=>Volatile.Read(ref level)==1,()=>Set(backend,"faulted",true)))
            {
                monitor.Start();Set(backend,"emergencyMonitor",monitor);
                check(!backend.ReadEmergencyReleased()&&backend.Faulted,"real LOW revokes permission and retains fault");
                Volatile.Write(ref level,1);Thread.Sleep(40);
                check(!backend.ReadEmergencyReleased()&&backend.Faulted,"later HIGH cannot clear the active LOW latch");
            }
            backend=new NativeFactoryBackend();Set(backend,"initialized",true);
            using(var unstarted=new FactoryEmergencyMonitor(()=>true,()=>{}))
            {Set(backend,"emergencyMonitor",unstarted);check(Expect(backend.ReadEmergencyReleased) is InvalidOperationException&&backend.Faulted,
                "unstarted/unknown monitor cannot become permission");}
            backend=new NativeFactoryBackend();Set(backend,"initialized",true);
            var original=new InvalidOperationException("fake native DI failure");
            using(var failed=new FactoryEmergencyMonitor(()=>{throw original;},()=>Set(backend,"faulted",true)))
            {
                Expect(()=>{failed.Start();return true;});Set(backend,"emergencyMonitor",failed);
                var error=Expect(backend.ReadEmergencyReleased);
                check(error is InvalidOperationException&&Object.ReferenceEquals(error.InnerException,original)&&backend.Faulted,
                    "original DI error remains chained and fail-closed");
            }
            backend=new NativeFactoryBackend();Set(backend,"initialized",true);
            using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            using(var delayed=new FactoryEmergencyMonitor(()=>{if(Interlocked.Increment(ref reads)>1){entered.Set();release.WaitOne();}return true;},()=>Set(backend,"faulted",true)))
            {
                reads=0;delayed.Start();Set(backend,"emergencyMonitor",delayed);
                check(entered.WaitOne(400),"fake second DI read is blocked independently of caller");Thread.Sleep(130);
                var error=Expect(backend.ReadEmergencyReleased);
                check(error is InvalidOperationException&&backend.Faulted,"stale HIGH cannot authorize while sole reader is blocked past total deadline");
                release.Set();check(delayed.Stop(600),"fake DI owner is actually joined before disposal");
                check(backend.Faulted,"late completion cannot auto-recover the backend");
            }
            var pico=new PicoInputCapture();int errors=0;pico.CommunicationError=detail=>errors++;
            var transport=Expect(()=>{pico.HandleTransportStatus(0x11);return true;});
            // Customer text is deliberately concise; the exact original uint32
            // status must remain in the technical exception and structured data.
            check(transport is InvalidOperationException&&transport.InnerException is InvalidOperationException&&
                transport.InnerException.Message.Contains("PICO_STATUS=0x00000011")&&
                (transport.Data["StatusDomain"] as string)=="PicoSdk"&&
                transport.Data["RawStatus"] is uint&&(uint)transport.Data["RawStatus"]==0x11u&&errors==1,
                "actual Pico nonzero status remains an error with original code");
            check(Expect(()=>{pico.HandleTransportStatus(0x27);return true;}) is InvalidOperationException,
                "unexpected Pico BUSY is not converted to online/permission");
            check(!pico.ConnectionOpen&&!pico.Armed,"status fakes invoke no SDK or hardware entry");
            return "Factory emergency hot-path: "+checks+" offline checks PASS; real Native permission method with sole managed fake DI; IL boundary checks; HardwareAccessed=False; SDKCalled=False";
        }
        private static Exception Expect(Func<bool> action)
        {try{action();}catch(Exception error){return error;}throw new Exception("Expected permission/status rejection.");}
        private static void Set(object target,string name,object value)
        {target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);}
        private static List<MethodBase> Calls(MethodInfo method)
        {
            var one=new OpCode[256];var two=new OpCode[256];
            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
            {var op=(OpCode)field.GetValue(null);ushort n=unchecked((ushort)op.Value);if(n<256)one[n]=op;else if((n&0xff00)==0xfe00)two[n&255]=op;}
            byte[] il=method.GetMethodBody().GetILAsByteArray();var calls=new List<MethodBase>();
            for(int i=0;i<il.Length;)
            {
                OpCode op=il[i]==0xfe?two[il[++i]]:one[il[i]];i++;
                if(op.OperandType==OperandType.InlineMethod&&(op==OpCodes.Call||op==OpCodes.Callvirt||op==OpCodes.Newobj))
                    calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il,i)));
                int size;
                switch(op.OperandType){case OperandType.InlineNone:size=0;break;case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:size=1;break;
                    case OperandType.InlineVar:size=2;break;case OperandType.InlineI8:case OperandType.InlineR:size=8;break;
                    case OperandType.InlineSwitch:size=4+4*BitConverter.ToInt32(il,i);break;default:size=4;break;}
                i+=size;
            }
            return calls;
        }
    }
}
