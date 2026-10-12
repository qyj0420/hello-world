using System;
using System.Reflection;

namespace Xiongxin.FactoryTest
{
    // No SDK or synthetic native handles: all calls below either validate pure
    // settings or reject at a managed entry guard, before a native operation.
    internal static class FactoryNativeSessionTests
    {
        internal static string Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Native session: "+why);checks++;};
            var requested=Settings();
            requested.FeedbackWiringConfirmed=false;requested.IndependentDischargeConfirmed=false;
            var opened=FactoryNativeSessionPolicy.PrepareOpen(requested);
            check(opened.Input.RealHardware && !opened.FeedbackWiringConfirmed && !opened.IndependentDischargeConfirmed,
                "read-only session validation does not fabricate output permission");
            check(!requested.Input.RealHardware && !Object.ReferenceEquals(opened,requested) &&
                !Object.ReferenceEquals(opened.Input,requested.Input),"open freezes an independent settings snapshot");
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareRound(requested,false)) is ArgumentException,
                "first output round still requires explicit current permission");

            requested=Settings();
            var round=FactoryNativeSessionPolicy.PrepareRound(requested,false);
            requested.Input.RangeIndex=6;requested.TargetKv=2;
            check(round.Input.RangeIndex==5 && round.TargetKv==.5,"UI changes cannot mutate the active round");
            var next=FactoryNativeSessionPolicy.PrepareRound(requested,false);
            check(next.Input.RangeIndex==6 && next.TargetKv==2 && FactoryNativeSessionPolicy.SameDeviceSession(round,next),
                "same exact devices accept freshly validated next-round parameters");
            next.IndependentDischargeConfirmed=false;
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareRound(next,false)) is ArgumentException,
                "last round's confirmation cannot fill a missing next-round confirmation");
            foreach(Action<FactorySettings> change in new Action<FactorySettings>[] {
                s=>s.Input.ArtSerial="987654321",s=>s.Input.PicoSerial="FAKE/0002",
                s=>s.Input.ArtDevice="Dev2",s=>s.Input.ArtLibraryDirectory="other-art-runtime",
                s=>s.Input.PicoLibraryDirectory="other-pico-runtime",s=>s.Simulation=true})
            {
                var changed=round.Clone();change(changed);
                check(!FactoryNativeSessionPolicy.SameDeviceSession(round,changed),"different device, driver or simulation cannot reuse old handles");
            }
            var same=round.Clone();same.Input.ArtSerial=" "+same.Input.ArtSerial+" ";
            check(FactoryNativeSessionPolicy.SameDeviceSession(round,same),"identity comparison preserves exact serial with harmless outer whitespace");
            same=round.Clone();same.Input.PicoSerial="fake/0001";
            check(!FactoryNativeSessionPolicy.SameDeviceSession(round,same),"Pico serial comparison is ordinal, not case-folded");

            var invalid=Settings();invalid.Input.ArtSerial="0";
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareOpen(invalid)) is ArgumentException,"zero ART serial fails before SDK access");
            invalid=Settings();invalid.Input.ArtTerminal=0;
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareOpen(invalid)) is ArgumentException,"unconfirmed terminal mapping fails before SDK access");
            invalid=Settings();invalid.Input.CaptureDurationMs=61;
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareRound(invalid,false)) is ArgumentException,
                "resident round cannot bypass the 60 ms new-capture limit");
            invalid=Settings();invalid.TargetKv=0;
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareRound(invalid,false)) is InvalidOperationException,
                "zero free input must use the independent input-only path");
            invalid.ZeroSynchronizedOptical=true;
            check(FactoryNativeSessionPolicy.PrepareRound(invalid,false).ZeroSynchronizedOptical,
                "explicit zero optical mode retains a separately validated contract");
            invalid=Settings();invalid.CalibrationSourceConfirmed=false;
            invalid.TargetKv=0;
            check(Expect(()=>FactoryNativeSessionPolicy.PrepareRound(invalid,true)) is ArgumentException,
                "calibration requires its own confirmation");
            invalid.CalibrationSourceConfirmed=true;
            check(FactoryNativeSessionPolicy.PrepareRound(invalid,true).CalibrationSourceConfirmed,
                "calibration settings are accepted only with their explicit permission");

            var backend=new NativeFactoryBackend();
            string id=backend.SessionId;
            check(!String.IsNullOrEmpty(id) && !backend.SessionOpen && !backend.CanReuseSession && !backend.RoundActive,
                "constructing a session opens nothing");
            invalid=Settings();invalid.Simulation=true;
            check(Expect(()=>backend.OpenSession(invalid)) is ArgumentException && !backend.SessionOpen,
                "simulation cannot open the native session");
            check(Expect(()=>backend.BeginRound(Settings(),false)) is InvalidOperationException,"unopened backend cannot begin a round");
            backend.EndRound();
            check(backend.SessionId==id && !backend.SessionOpen,"ending an unopened/idle round does not allocate resources");

            // Synthetic managed lifecycle flags only; no task/Pico handle is
            // assigned and none of these calls is allowed past its first guard.
            Set(backend,"initialized",true);Set(backend,"settings",round);Set(backend,"sessionIdentity",round.Clone());
            foreach(Action action in new Action[] { ()=>backend.WriteAo(0),()=>backend.SetHv(false),
                ()=>backend.SetOptical(false),()=>backend.SetPressureIndicator(false),
                ()=>backend.Arm(false),backend.PrepareFaultMonitoring })
                check(Expect(action) is InvalidOperationException,"idle public acquisition/output operation cannot write/create tasks");
            Set(backend,"roundActive",true);
            check(Expect(()=>backend.BeginRound(Settings(),false)) is InvalidOperationException,"overlapping rounds reject before SDK configuration");
            Set(backend,"roundActive",false);Set(backend,"faulted",true);
            check(Expect(()=>backend.BeginRound(Settings(),false)) is InvalidOperationException && backend.Faulted,
                "new settings cannot reset a faulted session");
            backend.EndRound();
            check(backend.Faulted && !backend.CanReuseSession,"idle EndRound preserves fault latch and does not revive the session");
            check(Math.Abs(NativeFactoryBackend.DriverIoTimeoutSeconds-.020)<1e-12,
                "native SDK request timeout remains 20 ms");
            int names=0;string lastKey=null,lastModule=null;
            Action<string,string> receiveName=(key,module)=>{names++;lastKey=key;lastModule=module;};
            NativeFactoryBackend.ReportIdleIdentity("Dev1",receiveName);
            NativeFactoryBackend.ReportIdleIdentity("Dev1",receiveName);
            check(names==2 && lastKey=="LINK_ART_NAME" && lastModule=="Dev1",
                "healthy idle publication repeats the bare verified module for late watch subscribers");
            NativeFactoryBackend.ReportIdleIdentity("Dev1",null);
            check(names==2,"no idle observer means no report callback or device operation");
            return "Native session: "+checks+" managed checks PASS; immutable rounds/identity/permissions/pre-SDK lifecycle guards; HardwareAccessed=False; native reuse requires device validation";
        }

        private static FactorySettings Settings()
        {
            var settings=new FactoryTests.Fixture().Settings;
            settings.Simulation=false;settings.VmonAfterHvStartup=true;
            settings.FeedbackWiringConfirmed=true;settings.IndependentDischargeConfirmed=true;
            return settings;
        }
        private static void Set(NativeFactoryBackend backend,string name,object value)
        {typeof(NativeFactoryBackend).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(backend,value);}
        private static Exception Expect(Action action)
        {try{action();}catch(Exception ex){return ex;}throw new Exception("Expected native-session rejection.");}
    }
}
