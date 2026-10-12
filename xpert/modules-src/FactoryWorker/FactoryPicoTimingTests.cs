using System;
using System.Collections.Generic;
using System.Reflection;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPicoTimingTests
    {
        // Reflect only the managed observation helper. Do not Initialize, Arm,
        // ReadCompleted, StopCapture or Dispose a device, or invoke any SDK.
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Pico timing regression: "+why);checks++;};
            var pico=new PicoInputCapture();
            MethodInfo begin=typeof(PicoInputCapture).GetMethod("BeginTiming",BindingFlags.Instance|BindingFlags.NonPublic);
            check(begin!=null,"passive timing helper exists");
            Func<string,IDisposable> scope=name=>(IDisposable)begin.Invoke(pico,new object[]{name});
            check(scope("no-observer")==null,"absent observer is a safe null scope");

            int starts=0,ends=0,communicationEvents=0;
            pico.CommunicationError=detail=>communicationEvents++;
            pico.CommunicationOk=()=>communicationEvents++;
            pico.CommunicationBusy=detail=>communicationEvents++;
            string observed=null;
            pico.TimingScope=name=>{starts++;observed=name;return null;};
            check(scope("Pico.GetValues")==null && starts==1 && observed=="Pico.GetValues",
                "null-returning observer is called exactly once with the original operation name");

            bool bodyRan=false;
            pico.TimingScope=name=>{starts++;throw new InvalidOperationException("fake timing begin failed");};
            using(scope("begin-fails")){bodyRan=true;}
            check(bodyRan && starts==2,"observer begin failure cannot prevent the enclosed operation");

            var order=new List<string>();
            pico.TimingScope=name=>{order.Add("begin:"+name);return new ActionScope(()=>{ends++;order.Add("end:"+name);});};
            var normal=scope("ordered");
            using(normal){order.Add("body");}
            normal.Dispose();
            check(String.Join(",",order.ToArray())=="begin:ordered,body,end:ordered" && ends==1,
                "normal observer preserves operation order and is disposed once even if requested twice");

            pico.TimingScope=name=>new ActionScope(()=>{ends++;throw new InvalidOperationException("fake timing dispose failed");});
            var broken=scope("dispose-fails");
            bodyRan=false;using(broken){bodyRan=true;}broken.Dispose();
            check(bodyRan && ends==2,"observer disposal failure is isolated and not repeated");
            var original=new InvalidOperationException("original business failure");
            Exception caught=null;
            try {using(scope("business-fails")){throw original;}}
            catch(Exception ex){caught=ex;}
            check(Object.ReferenceEquals(caught,original) && ends==3,
                "observer cleanup cannot replace the original business exception");

            var timing=new FactoryIoTimingDiagnostics(8);
            pico.TimingScope=timing.BeginOperation;
            using(scope("Pico.ReadCompleted"))
            {
                check(timing.CurrentOperation=="Pico.ReadCompleted","production callback publishes the outer passive context");
                using(scope("Pico.GetValues"))
                    check(timing.CurrentOperation=="Pico.GetValues","nested SDK context is visible without invoking the SDK");
                check(timing.CurrentOperation=="Pico.ReadCompleted","nested observer cleanup restores the outer context");
            }
            check(timing.CurrentOperation=="" && timing.OperationsStarted==2 && timing.OperationsCompleted==2 &&
                timing.SnapshotEntries().Length==2,"production timing callback unwinds both scopes and retains their host durations");
            check(communicationEvents==0 && !pico.ConnectionOpen && !pico.Armed,
                "managed timing probes neither produce transport events nor create a device connection");
            return "Pico passive timing: "+checks+" offline checks PASS; reflection invokes managed timing helper only; HardwareAccessed=False";
        }

        private sealed class ActionScope : IDisposable
        {
            private readonly Action dispose;
            internal ActionScope(Action dispose){this.dispose=dispose;}
            public void Dispose(){dispose();}
        }
    }
}
