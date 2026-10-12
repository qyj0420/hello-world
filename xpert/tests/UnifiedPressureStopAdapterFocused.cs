using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Xiongxin.FactoryTest;

// Compiled with the real worker closure as an isolated executable. No product
// Main, Program.Worker, native constructor, monitor or SDK entry is called.
internal static class UnifiedPressureStopAdapterFocused
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,why);}
    static void Binding()
    {
        var request=new FactorySessionRequest {Command="Pressure",SettingsPath="owned-settings",Directory="owned-round",FrozenSettings=new FactorySettings()};
        var other=new FactorySessionRequest {Command="Pressure"};
        var result=new FactoryResult();int calls=0;
        int code=UnifiedWorkerEntry.RunPressureRound(request,null,"123",(args,backend,settings,saved,input,healthy,emergency)=>{
            calls++;
            Check(args.Length==5&&args[0]=="--worker"&&args[1]=="Pressure"&&args[2]==request.SettingsPath&&args[3]==request.Directory&&args[4]=="123","production adapter passes exact owned pressure arguments");
            Check(backend==null&&Object.ReferenceEquals(settings,request.FrozenSettings),"production adapter preserves provided backend and immutable accepted settings");
            Check(saved!=null&&input!=null&&healthy!=null&&emergency!=null,"all four production callbacks are present");
            Check(!request.InputStopCompleted&&!request.HealthyRoundStopCompleted&&request.ArchivedResult==null,"callbacks are not pre-acknowledged");
            saved(result);Check(Object.ReferenceEquals(request.ArchivedResult,result)&&!request.InputStopCompleted&&!request.HealthyRoundStopCompleted,"archive callback cannot grant stop evidence");
            healthy();Check(request.HealthyRoundStopCompleted&&!request.InputStopCompleted,"healthy stop callback reaches the actual request, not the input-stop proof");
            input();Check(request.InputStopCompleted,"separate input-stop notification remains bound");
            Check(!request.EmergencyRoundStopCompleted,"ordinary healthy/input stop cannot manufacture emergency evidence");
            emergency();Check(request.EmergencyRoundStopCompleted,"actual restricted-entry emergency notification reaches the accepted request");
            Check(!other.HealthyRoundStopCompleted&&!other.InputStopCompleted&&!other.EmergencyRoundStopCompleted&&other.ArchivedResult==null,"callbacks cannot acknowledge an unrelated request");
            return 1;
        });
        Check(code==1&&calls==1,"worker failure/stop result remains unchanged; adapter does not invent result 4");
        request=new FactorySessionRequest {Command="Calibration"};
        Reject(()=>UnifiedWorkerEntry.RunPressureRound(request,null,"123",(a,b,s,r,i,h,e)=>{calls++;return 0;}),"calibration is rejected before an injected worker is entered");
        Check(calls==1,"invalid purpose cannot enter worker");
        request=new FactorySessionRequest {Command="Pressure"};
        code=UnifiedWorkerEntry.RunPressureRound(request,null,"123",(a,b,s,r,i,h,e)=>1);
        Check(code==1&&!request.HealthyRoundStopCompleted&&!request.InputStopCompleted&&!request.EmergencyRoundStopCompleted,"result 1 alone creates no stop evidence");
        bool thrown=false;try{UnifiedWorkerEntry.RunPressureRound(request,null,"123",(a,b,s,r,i,h,e)=>{throw new IOException("original offline cleanup failure");});}catch(IOException e){thrown=e.Message=="original offline cleanup failure";}
        Check(thrown&&!request.HealthyRoundStopCompleted,"native/cleanup exception facts are preserved rather than reported as healthy cancellation");
    }
    static void ActualSessionProxy()
    {
        var wrapper=typeof(UnifiedWorkerEntry).GetNestedType("PressureSession",BindingFlags.NonPublic);
        Check(wrapper!=null&&typeof(IFactoryHealthyRoundStopSessionEvidence).IsAssignableFrom(wrapper),"actual production PressureSession implements healthy-stop evidence interface");
        Check(typeof(IFactoryEmergencyLatchedSessionEvidence).IsAssignableFrom(wrapper),"actual restricted-entry session exposes emergency display retention, not only healthy-stop");
        // Managed-only objects avoid even the native constructor. The exact
        // production proxy methods are exercised, not a fake session interface.
        var backend=(NativeFactoryBackend)FormatterServices.GetUninitializedObject(typeof(NativeFactoryBackend));
        var native=(FactoryResidentDeviceServer.NativeSession)FormatterServices.GetUninitializedObject(typeof(FactoryResidentDeviceServer.NativeSession));
        typeof(FactoryResidentDeviceServer.NativeSession).GetField("Backend",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(native,backend);
        var session=new UnifiedWorkerEntry.PressureSession(native);
        Check(Object.ReferenceEquals(session.Backend,backend),"real restricted adapter uses the exact native owner, not replacement handles");
        var evidence=(IFactoryHealthyRoundStopSessionEvidence)session;
        var facts=new FactoryHealthyRoundStopFacts(true,true,true,true,true);
        typeof(NativeFactoryBackend).GetField("healthyRoundStopFacts",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(backend,facts);
        Check(Object.ReferenceEquals(evidence.HealthyRoundStopFacts,facts)&&evidence.HealthyRoundStopFacts.Complete,"production proxy returns exact native cleanup facts without manufacturing them");
        evidence.ResetHealthyRoundStopEvidence();
        Check(evidence.HealthyRoundStopFacts==null&&backend.HealthyRoundStopFacts==null,"production reset clears stale round facts in backend");
        // A RAMP fault is independent of EndRound's successful output/Stop
        // calls: complete resource facts may not clear the safety fault latch.
        typeof(NativeFactoryBackend).GetField("faulted",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(backend,true);
        typeof(NativeFactoryBackend).GetField("healthyRoundStopFacts",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(backend,facts);
        Check(((IFactoryResidentSession)session).Faulted&&!((IFactoryResidentSession)session).CanReuseSession,"actual faulted RAMP session remains non-reusable despite complete cleanup facts");
        evidence.ResetHealthyRoundStopEvidence();
        Check(((IFactoryResidentSession)session).Faulted&&evidence.HealthyRoundStopFacts==null,"reset does not clear actual emergency/native fault latch");
        backend.EndRound();
        Check(evidence.HealthyRoundStopFacts==null,"an unopened/non-active owner cannot manufacture EndRound evidence");
        foreach(var missing in new[]{new FactoryHealthyRoundStopFacts(false,true,true,true,true),new FactoryHealthyRoundStopFacts(true,false,true,true,true),
            new FactoryHealthyRoundStopFacts(true,true,false,true,true),new FactoryHealthyRoundStopFacts(true,true,true,false,true),new FactoryHealthyRoundStopFacts(true,true,true,true,false)})
            Check(!missing.Complete,"every actual output/ART/Pico/buffer/end fact is independently required");
        Reject(()=>FactoryHealthyRoundStopEvidence.Publish(new FactorySessionRequest{Command="Pressure"},"unbound","unbound",1,2,facts,false),"complete-looking facts plus an unusable/unbound session cannot publish healthy receipt");
        Check(!FactoryHealthyRoundStopEvidence.IsOnlyCanceledFailure(new FactoryRunException(new FactoryResult(),new Exception[]{new OperationCanceledException(),new IOException("EndRound failed")})),"RAMP cancel plus cleanup failure is not a pure healthy stop");
    }
    static int Main()
    {
        try{Binding();ActualSessionProxy();Console.WriteLine("UNIFIED_PRESSURE_STOP_ADAPTER_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;ProductExecuted=false;NativeConstructorCalled=false");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
