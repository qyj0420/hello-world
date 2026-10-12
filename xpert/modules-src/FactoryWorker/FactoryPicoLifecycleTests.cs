using System;
using System.Reflection;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryPicoLifecycleTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Pico lifecycle: "+why);checks++;};
            var pico=new PicoInputCapture();
            var failure=Expect(()=>pico.HandleTransportStatus(0x11));
            check(failure is InvalidOperationException && failure.ToString().Contains("PICO_STATUS=0x00000011"),
                "SDK status still produces its original error when no reporter is attached");
            var reportFailure=new InvalidOperationException("injected communication logger failure");
            pico.CommunicationError=detail=>{throw reportFailure;};
            var combined=Expect(()=>pico.HandleTransportStatus(0x11)) as AggregateException;
            check(combined!=null && combined.InnerExceptions.Count==2 &&
                combined.InnerExceptions[0] is InvalidOperationException && combined.InnerExceptions[0].ToString().Contains("PICO_STATUS=0x00000011") &&
                Object.ReferenceEquals(combined.InnerExceptions[1],reportFailure),"communication callback cannot replace the original SDK status; both errors survive");
            check(!pico.ConnectionOpen && !pico.Armed,"pure status injection has not opened or armed hardware");
            check(Expect(()=>pico.Configure(new BenchSettings())) is InvalidOperationException,"Configure refuses a missing live handle before SDK access");

            // Synthetic fields exercise guards only. Never Dispose or pass this
            // TEST-only handle to an SDK; both paths reject before native calls.
            var type=typeof(PicoInputCapture);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            type.GetField("handle",flags).SetValue(pico,(short)17);
            type.GetField("captureDurationMs",flags).SetValue(pico,61.0);
            check(Expect(pico.Arm) is ArgumentOutOfRangeException,"Pico Arm itself blocks >60 ms before buffer registration or RunBlock");
            type.GetField("<Armed>k__BackingField",flags).SetValue(pico,true);
            check(Expect(()=>pico.Configure(new BenchSettings())) is InvalidOperationException,"Configure cannot replace an active segment");
            type.GetField("<Armed>k__BackingField",flags).SetValue(pico,false);
            type.GetField("cooldownReleasePending",flags).SetValue(pico,true);
            check(Expect(()=>pico.Configure(new BenchSettings())) is InvalidOperationException,"Configure cannot bypass pending release ownership");
            return "Pico lifecycle: "+checks+" managed checks PASS; status/report exceptions and pre-SDK Configure/Arm guards; HardwareAccessed=False";
        }
        private static Exception Expect(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected Pico lifecycle rejection.");}
    }
}
