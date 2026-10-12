using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

// Test-only, detached managed control. Never configure/invoke a capture backend,
// worker, native session, product Main, UI request/start method, or device SDK.
// Usage: PressureEmergencyUiReceiptSmoke.exe <absolute PressureTest UI DLL>
internal static class PressureEmergencyUiReceiptSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object o,string name){var f=o.GetType().GetField(name,F);if(f==null)throw new MissingFieldException(o.GetType().FullName,name);return f.GetValue(o);}
    static void Set(object o,string name,object value){var f=o.GetType().GetField(name,F);if(f==null)throw new MissingFieldException(o.GetType().FullName,name);f.SetValue(o,value);}
    static object Property(object o,string name){return o.GetType().GetProperty(name,F).GetValue(o,null);}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static readonly string[] StateFields={
        "simulationMode","p04SampleKnown","p04BitHigh","p04LatchedDisplayKnown","p04LatchedDisplayHigh",
        "pressureEmergencyLatched","highVoltageClosed","highVoltageFeedbackKnown","requestedTargetPeakVoltageKv",
        "pressureStateCode","pressureRequestSequence","actionSequence","actionPending","actionBusy",
        "automaticPressureEnabled","automaticPlanInterrupted","finishCurrentRoundRequested",
        "zeroHardwareRunning","zeroHardwareGeneration","zeroLastClaimedActionSequence",
        "zeroHardwareCapture","pressureHardwareCapture","zeroHardwareCancellation"
    };
    static Dictionary<string,object> Snapshot(object control)
    {
        var result=new Dictionary<string,object>();foreach(string name in StateFields)result[name]=Get(control,name);
        result["IsHighVoltageReady"]=Property(control,"IsHighVoltageReady");
        result["EmergencyStopStatusCode"]=Property(control,"EmergencyStopStatusCode");
        return result;
    }
    static void Unchanged(object control,Dictionary<string,object> before,string why)
    {
        var after=Snapshot(control);foreach(var item in before)Check(Object.Equals(item.Value,after[item.Key]),why+": "+item.Key+" unchanged");
    }
    static object Owner(Type controlType,long sequence,bool dispatched)
    {
        var t=controlType.GetNestedType("ZeroActionOwner",BindingFlags.NonPublic);
        if(t==null)throw new MissingMemberException("Missing exact private action owner.");
        var value=Activator.CreateInstance(t,true);Set(value,"Sequence",sequence);Set(value,"DispatchCommitted",dispatched);
        Set(value,"Cancellation",new CancellationTokenSource());return value;
    }
    static bool Receipt(object control,long sequence)
    {
        var m=control.GetType().GetMethod("ReportPressureEmergencyRoundStopConfirmed",F,null,new[]{typeof(long)},null);
        if(m==null||m.ReturnType!=typeof(bool))throw new MissingMethodException("Missing exact managed UI receipt contract.");
        return (bool)m.Invoke(control,new object[]{sequence});
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            if(args==null||args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Pass one absolute PressureTest UI DLL path.");
            string path=Path.GetFullPath(args[0]);
            if(!String.Equals(Path.GetExtension(path),".dll",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Only managed UI DLL input is accepted, not product/worker EXE.");
            string folder=Path.GetDirectoryName(path),beforeHash=Hash(path);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string candidate=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");
                return File.Exists(candidate)?Assembly.LoadFrom(candidate):null;
            };
            var assembly=Assembly.LoadFrom(path);var type=assembly.GetType("HVX.Controls.PressureTestControl",true);
            Check(String.Equals(Path.GetFullPath(assembly.Location),path,StringComparison.OrdinalIgnoreCase),"exact supplied UI assembly loaded");
            using(var control=(Control)Activator.CreateInstance(type)) {
                Check(control.Parent==null&&!control.IsHandleCreated,"managed control remains detached, no window or message pump");
                int starts=0;EventHandler onStart=(s,e)=>starts++;type.GetEvent("PressureRequested").AddEventHandler(control,onStart);
                Set(control,"pressureEmergencyLatched",true);Set(control,"p04SampleKnown",false);
                Set(control,"p04BitHigh",true);Set(control,"requestedTargetPeakVoltageKv",12.3d);
                Set(control,"pressureStateCode","RUNNING");Set(control,"pressureRequestSequence",9L);
                Set(control,"actionSequence",41L);Set(control,"actionPending",true);Set(control,"actionBusy",true);
                Check(Get(control,"zeroHardwareCapture")==null&&Get(control,"pressureHardwareCapture")==null,"no acquisition backend configured");
                var state=Snapshot(control);
                Check(!Receipt(control,41L),"missing/expired action owner rejects receipt");Unchanged(control,state,"missing owner");
                var owner=Owner(type,41L,false);Set(control,"zeroActionOwner",owner);
                Check(!Receipt(control,41L)&&!(bool)Get(owner,"EmergencyRoundStopConfirmed"),"current sequence without dispatch commit rejects receipt");
                Unchanged(control,state,"uncommitted owner");Set(owner,"DispatchCommitted",true);
                Check(!Receipt(control,40L)&&!(bool)Get(owner,"EmergencyRoundStopConfirmed"),"old sequence rejects receipt");
                Check(!Receipt(control,42L)&&!(bool)Get(owner,"EmergencyRoundStopConfirmed"),"different future sequence rejects receipt");
                Check(Receipt(control,41L)&&(bool)Get(owner,"EmergencyRoundStopConfirmed"),"only exact current dispatched owner accepts receipt");
                foreach(string name in new[]{"InputStopConfirmed","RoundStopConfirmed","NativeNeverEnteredConfirmed","OperatorStopRequested","OperatorCanceledBeforeDispatch"})
                    Check(!(bool)Get(owner,name),"emergency round receipt does not fabricate "+name);
                Check(Get(owner,"FailureOutcomeStatus")==null&&!((CancellationTokenSource)Get(owner,"Cancellation")).IsCancellationRequested,"receipt does not manufacture failure or invoke cancellation");
                Unchanged(control,state,"accepted receipt");
                Check(Receipt(control,41L),"duplicate exact receipt is idempotently accepted");Unchanged(control,state,"duplicate receipt");
                var next=Owner(type,42L,true);Set(control,"zeroActionOwner",next);Set(control,"actionSequence",42L);
                Set(control,"p04BitHigh",false);state=Snapshot(control);
                Check(!Receipt(control,41L)&&!(bool)Get(next,"EmergencyRoundStopConfirmed"),"replaced owner rejects late previous-round receipt");
                Check(Receipt(control,42L)&&(bool)Get(next,"EmergencyRoundStopConfirmed"),"new current dispatched owner accepts its own receipt");
                Unchanged(control,state,"LOW display receipt");Set(control,"zeroActionOwner",null);
                Check(!Receipt(control,42L),"released/invalidated owner rejects its late receipt");Unchanged(control,state,"released owner");
                Check(starts==0&&(long)Property(control,"PressureRequestSequence")==9L,"receipt never starts or resumes pressure");
                Check(!(bool)Property(control,"IsHighVoltageReady")&&(bool)Get(control,"pressureEmergencyLatched"),"output permission stays blocked and emergency latch stays set");
                Check(Get(control,"zeroHardwareCapture")==null&&Get(control,"pressureHardwareCapture")==null&&!control.IsHandleCreated,"no backend, worker, or window created by receipt");
                ((CancellationTokenSource)Get(owner,"Cancellation")).Dispose();((CancellationTokenSource)Get(next,"Cancellation")).Dispose();
            }
            Check(Hash(path)==beforeHash,"tested UI assembly unchanged");
            Console.WriteLine("PRESSURE_EMERGENCY_UI_RECEIPT_PASS="+checks+";UI_SHA256="+beforeHash+";HardwareAccessed=False;SDKCalled=False;WorkersStarted=0;ProductEntryStarted=False;BackendConfigured=False;DetachedControl=True;UserProjectsWritten=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
