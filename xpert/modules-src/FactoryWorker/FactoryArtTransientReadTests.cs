using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Pure managed receipt/clock fakes. No production DLL, SDK or device load.
    internal static class FactoryArtTransientReadTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> ok=(value,why)=>{if(!value)throw new Exception("ART transient read: "+why);checks++;};
            var plan=new FactoryArtFeedbackPlan(new FactorySettings {TargetKv=5,MaximumKv=5,FeedbackWindowMs=10});
            foreach(int status in new[]{-200284,-26802})
            {
                var b=new Buffer(status,2){ReadMs=20};
                var f=Read(plan,b);
                ok(b.Reads==3 && b.Requests.All(n=>n==14) && b.Consumed==14 && b.Now==63,
                    "zero sample recovery retains the same requested block and does not consume empty data "+status);
                ok(f.SampleCount==14 && f.ReadBatchCount==1 && f.ReadElapsedMs==63 &&
                    f.WindowSampleCount==10 && f.FreshWindowBoundaryQueued==4 && f.FreshWindowSamplesAfterBoundary==10,
                    "only the successful complete batch supplies sample count, fresh window and timestamp "+status);
                ok(f.ImonMaximumRawV==4.9 && f.DividerMinimumRawV==-.3 && f.DividerMedianRawV==.02,
                    "all original old/new raw peaks remain protected after empty waits "+status);
                ok(b.EmergencyChecks>=8 && b.Checks==1 && b.MaxConcurrentReads==1,
                    "DI checks continue, reads are serial, zero pending never latches communication failure "+status);

                b=new Buffer(status,Int32.MaxValue){ReadMs=19};
                var error=Fail(()=>Read(plan,b));
                ok(b.Now==100 && b.Reads==5 && b.Consumed==0 && error.Message.Contains("100 ms") &&
                    error.InnerException is FactoryAnalogReadPendingException &&
                    (int)error.InnerException.Data["ArtStatus"]==status,
                    "persistent empty reads fail at the original deadline with the original status "+status);

                foreach(int actual in new[]{-1,1,7,14})
                {
                    b=new Buffer(status,Int32.MaxValue){Actual=actual};
                    error=Fail(()=>Read(plan,b));
                    ok(!(error is FactoryAnalogReadPendingException) && b.Reads==1 && b.Consumed==0 && b.Checks==1 &&
                        error.Message.Contains("status="+status) && error.Message.Contains("actualPerChannel="+actual),
                        "unwritten sentinel and every negative partial result fail without retry "+status+"/"+actual);
                }
                b=new Buffer(status,Int32.MaxValue){ClockFrozen=true};
                error=Fail(()=>Read(plan,b));
                ok(b.Reads==FactoryFeedbackStream.MaximumZeroSampleRetries+1 && b.Consumed==0 &&
                    error.InnerException is FactoryAnalogReadPendingException,
                    "a frozen clock still has a finite empty-read cap "+status);

                b=new Buffer(status,1);b.AfterRead=()=>b.Low=true;
                error=Fail(()=>Read(plan,b));
                ok(error is FactoryEmergencyStopException && b.Reads==1 && b.Consumed==0,
                    "real LOW interrupts immediately after an empty return "+status);
                b=new Buffer(status,1);b.AfterRead=()=>b.Unknown=true;
                error=Fail(()=>Read(plan,b));
                ok(Object.ReferenceEquals(error,b.UnknownFailure) && b.Reads==1 && b.Consumed==0,
                    "unknown DI is never treated as pending/high "+status);
                using(var cancel=new CancellationTokenSource())
                {
                    b=new Buffer(status,1);b.AfterRead=cancel.Cancel;
                    error=Fail(()=>Read(plan,b,cancel.Token));
                    ok(error is OperationCanceledException && b.Reads==1 && b.Consumed==0,
                        "round cancellation interrupts the empty-read branch "+status);
                }
                b=new Buffer(status,1){FailAvailableAt=3};
                error=Fail(()=>Read(plan,b));
                ok(Object.ReferenceEquals(error,b.AvailableFailure) && b.Reads==1 && b.Consumed==0,
                    "availability getter failure remains original and is not retried "+status);
                b=new Buffer(status,1){OldDivider=Double.NaN};
                error=Fail(()=>Read(plan,b));
                ok(b.Reads==2 && error.Message.Contains("非有限"),"raw NaN protection is not bypassed by prior empty read "+status);
                b=new Buffer(status,1){OldDivider=1};
                error=Fail(()=>Read(plan,b));
                ok(b.Reads==2 && error.Message.Contains("饱和"),"raw saturation protection is not bypassed "+status);
                b=new Buffer(status,1){ReadMs=1};
                error=Fail(()=>FactoryFeedbackStream.Read(plan,b,0,()=>b.Now,true,true,default(CancellationToken),b.Wait,3));
                ok(b.Now==3 && b.Reads==1 && error.Message.Contains("3 ms") &&
                    error.InnerException is FactoryAnalogReadPendingException,
                    "short remaining startup budget is neither reset nor expanded "+status);
            }
            foreach(int status in new[]{-1,-200474,-200088,-50401})
            {
                var b=new Buffer(status,1);var error=Fail(()=>Read(plan,b));
                ok(b.Reads==1 && b.Checks==1 && !(error is FactoryAnalogReadPendingException) &&
                    error.Message.Contains("status="+status),"unknown/hard status fails even with zero count "+status);
            }
            CheckManagedObservation(ok);
            CheckInteropAndNoOutputs(ok);
            return "ART explicit-zero transient reads: "+checks+" offline checks PASS; fake receipts/clock only; HardwareAccessed=False; SDKCalled=False";
        }
        private static void CheckManagedObservation(Action<bool,string> ok)
        {
            var method=typeof(NativeFactoryBackend).GetMethod("ObserveMonitorRead",BindingFlags.NonPublic|BindingFlags.Instance);
            foreach(int status in new[]{-200284,-26802})
            {
                var io=new TaskIo();var backend=new NativeFactoryBackend(io);int reports=0;
                Set(backend,"initialized",true);
                var pico=typeof(NativeFactoryBackend).GetField("pico",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(backend);
                Set(pico,"handle",(short)1); // managed state fixture only, never disposed/called as a device
                backend.ConnectionReport=(key,detail)=>{if(key=="AI_MONITOR_READ_RESULT" && detail.Contains("status="+status))reports++;};
                var error=Fail(()=>Invoke(method,backend,new FactoryAnalogReadReceipt(status,14,0,42,14,1)));
                var evidence=(FactoryArtCommunicationEvidence)typeof(NativeFactoryBackend)
                    .GetField("artCommunication",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(backend);
                ok(error is FactoryAnalogReadPendingException && reports==1 && io.Checks==0 && io.Outputs==0 &&
                    !backend.Faulted && !evidence.Snapshot().HasFailure && backend.CanReuseSession,
                    "real managed observation retains diagnostic status without latching pending as communications fault "+status);
                Invoke(method,backend,new FactoryAnalogReadReceipt(0,14,14,42,14,1));
                ok(!backend.Faulted && backend.CanReuseSession && !evidence.Snapshot().HasFailure && io.Checks==1,
                    "successful receipt after pending does not lock an otherwise reusable managed session "+status);
                backend.ConnectionReport=(key,detail)=>{throw new InvalidOperationException("diagnostic callback failed");};
                error=Fail(()=>Invoke(method,backend,new FactoryAnalogReadReceipt(status,14,0,42,14,1)));
                ok(error is AggregateException && error.ToString().Contains("diagnostic callback failed") &&
                    error.ToString().Contains("status="+status),"report callback error cannot be swallowed as retry "+status);
                backend.ConnectionReport=null;
                error=Fail(()=>Invoke(method,backend,new FactoryAnalogReadReceipt(status,14,7,42,14,1)));
                ok(!(error is FactoryAnalogReadPendingException) && backend.Faulted && evidence.Snapshot().HasFailure &&
                    evidence.Snapshot().Status==status && io.Checks==2 && io.Outputs==0 && !backend.CanReuseSession,
                    "negative partial receipt still latches original fault and issues no outputs "+status);
                Set(pico,"handle",(short)0);
            }
            var terminalIo=new TaskIo();var terminal=new NativeFactoryBackend(terminalIo);
            var terminalPico=typeof(NativeFactoryBackend).GetField("pico",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(terminal);
            var monitor=typeof(NativeFactoryBackend).GetField("monitor",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(terminal);
            Set(terminal,"initialized",true);Set(terminal,"settings",new FactorySettings());Set(terminalPico,"handle",(short)1);
            Set(monitor,"Handle",new IntPtr(1));Set(monitor,"Started",true);
            // A null plan fails inside the existing ReadFeedback try/catch,
            // before any Available()/SDK call. This checks the terminal latch
            // separately from the fake stream's persistent-empty deadline test.
            var terminalError=Fail(()=>terminal.ReadFeedback());
            ok(terminalError is ArgumentNullException && terminal.Faulted && !terminal.CanReuseSession &&
                terminalIo.Checks==0 && terminalIo.Outputs==0,
                "terminal managed feedback failure still locks the session; no implicit output or recovery");
            Set(terminalPico,"handle",(short)0);Set(monitor,"Handle",IntPtr.Zero);Set(monitor,"Started",false);
        }
        private static void CheckInteropAndNoOutputs(Action<bool,string> ok)
        {
            foreach(string name in new[]{"ReadAnalogReceipt","ReadDigitalReceipt"})
            {
                var method=typeof(InputOnlyArtNative).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static);
                var import=(DllImportAttribute)Attribute.GetCustomAttribute(method,typeof(DllImportAttribute));
                var search=(DefaultDllImportSearchPathsAttribute)Attribute.GetCustomAttribute(method,typeof(DefaultDllImportSearchPathsAttribute));
                ok(import.Value=="HvxAcquisitionNative.dll" && import.CallingConvention==CallingConvention.StdCall &&
                    import.EntryPoint==(name=="ReadAnalogReceipt"?"ArtDAQ_ReadAnalogF64":"ArtDAQ_ReadDigitalLines"),
                    "sentinel alias retains original ABI and bridge entry "+name);
                var count=method.GetParameters()[6];
                ok(count.ParameterType==typeof(int).MakeByRefType() && count.IsIn && count.IsOut,
                    "caller-provided -1 count is an In/Out ref, not a zero-initialized out "+name);
                ok(search.Paths==(DllImportSearchPath.AssemblyDirectory|DllImportSearchPath.System32),
                    "alias retains restricted DLL search "+name);
            }
            var monitor=typeof(NativeFactoryBackend).GetNestedType("MonitorBuffer",BindingFlags.NonPublic);
            ok(Calls(monitor.GetMethod("Read")).Count(m=>m.Name=="ReadAnalogReceipt")==1,
                "feedback adapter calls only the sentinel-aware AI import");
            var di=Calls(typeof(NativeFactoryBackend).GetMethod("ReadEmergencyCore",BindingFlags.NonPublic|BindingFlags.Instance));
            ok(di.Count(m=>m.Name=="ReadDigitalReceipt")==1 && di.Any(m=>m.Name=="IsZeroSamplePending"),
                "DI adapter uses sentinel import and explicit zero classification");
            foreach(var method in typeof(FactoryFeedbackStream).GetMethods(BindingFlags.NonPublic|BindingFlags.Static))
                ok(Calls(method).All(m=>!m.IsDefined(typeof(DllImportAttribute),false) &&
                    m.Name!="WriteAo" && m.Name!="WriteAnalog" && m.Name!="WriteLines" && m.Name!="SetHv" &&
                    m.Name!="CreateTask" && m.Name!="StartTask" && m.Name!="Arm"),
                    "feedback wait/parse helper has no native/output/lifecycle calls: "+method.Name);
        }
        private static FactoryFeedback Read(FactoryArtFeedbackPlan plan,Buffer b,CancellationToken token=default(CancellationToken))
        {return FactoryFeedbackStream.Read(plan,b,.75,()=>b.Now,true,true,token,b.Wait);}
        private static void Invoke(MethodInfo method,object owner,object receipt)
        {try{method.Invoke(owner,new[]{receipt});}catch(TargetInvocationException error){throw error.InnerException;}}
        private static Exception Fail(Action action)
        {try{action();}catch(Exception error){return error;}throw new Exception("Expected transient read rejection.");}
        private static void Set(object owner,string name,object value)
        {owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance).SetValue(owner,value);}
        private sealed class Buffer:IFactoryFeedbackBuffer
        {
            private readonly int status,pendingReads;private int availableCalls,inside;
            internal long Now;internal int Reads,Checks,Consumed,EmergencyChecks,MaxConcurrentReads;
            internal int Actual=0,ReadMs,FailAvailableAt;internal bool Low,Unknown,ClockFrozen;
            internal double OldDivider=-.3;internal Action AfterRead;
            internal readonly Exception AvailableFailure=new InvalidOperationException("fake availability getter error -200088");
            internal readonly Exception UnknownFailure=new InvalidOperationException("fake unknown DI");
            internal readonly List<int> Requests=new List<int>();
            internal Buffer(int code,int emptyReads){status=code;pendingReads=emptyReads;}
            public uint Available()
            {
                if(++availableCalls==FailAvailableAt)throw AvailableFailure;
                return availableCalls==1?4u:checked((uint)(14-Consumed));
            }
            public bool EmergencyReleased(){EmergencyChecks++;if(Unknown)throw UnknownFailure;return !Low;}
            internal void Wait(int ms){if(!ClockFrozen)Now+=ms;}
            public double[] Read(int requested)
            {
                MaxConcurrentReads=Math.Max(MaxConcurrentReads,++inside);Reads++;Requests.Add(requested);
                try
                {
                    int code=Reads<=pendingReads?status:0,actual=Reads<=pendingReads?Actual:requested;
                    if(!ClockFrozen)Now+=ReadMs;
                    if(AfterRead!=null)AfterRead();
                    new FactoryAnalogReadReceipt(code,requested,actual,requested*3,14,ReadMs).Validate((s,d)=>{
                        Checks++;if(s<0)throw new InvalidOperationException(d);
                    });
                    var raw=new double[requested*3];
                    for(int i=0;i<requested;i++){bool old=i<4;raw[i*3]=old?4.9:.01;raw[i*3+1]=old?.2:.002;raw[i*3+2]=old?OldDivider:.02;}
                    Consumed+=requested;return raw;
                }
                finally{--inside;}
            }
        }
        private sealed class TaskIo:IFactoryArtTaskIo
        {
            internal int Checks,Outputs;
            public int Stop(IntPtr task){throw new Exception("Unexpected lifecycle call.");}
            public int Clear(IntPtr task){throw new Exception("Unexpected lifecycle call.");}
            public int WriteLines(IntPtr task,int samples,uint autoStart,double timeout,uint layout,byte[] data,out int written,IntPtr reserved)
            {Outputs++;written=0;throw new Exception("Unexpected output.");}
            public int WriteAnalog(IntPtr task,int samples,uint autoStart,double timeout,uint layout,double[] data,out int written,IntPtr reserved)
            {Outputs++;written=0;throw new Exception("Unexpected output.");}
            public void Check(int code,string operation){Checks++;if(code<0)throw new InvalidOperationException(operation);}
        }
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
