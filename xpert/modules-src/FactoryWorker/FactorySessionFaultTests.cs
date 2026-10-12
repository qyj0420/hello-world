using System;
using System.IO;

namespace Xiongxin.FactoryTest
{
    internal static class FactorySessionFaultTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("Session fault metadata: "+why);checks++;};
            var tracker=new FactorySessionFaultTracker {SessionId="session-a",Phase="Idle"};
            tracker.Observe("LINK_ART_LOW",FactoryConnectionWatch.Stamp("valid low sample"));
            tracker.Observe("LINK_ART_CLOSED","cleanup finished");
            tracker.Observe("LINK_PICO_ONLINE","later healthy status");
            var low=tracker.Freeze(new InvalidOperationException("session ended"));
            check(low.Category==FactorySessionFaultCategory.EmergencyInputLow && low.Device=="ART" && low.Phase=="Idle",
                "LOW source survives normal cleanup/healthy snapshots");
            check(low.SessionId=="session-a" && low.Detail.Contains("valid low sample") && low.Detail.Contains("session ended"),
                "source and terminal exception details both retained");
            tracker=new FactorySessionFaultTracker {SessionId="session-b",Phase="Opening"};
            tracker.Observe("LINK_PICO_ERROR","Pico transport failed");
            tracker.Observe("LINK_ART_CLOSED","ART released");
            var pico=tracker.Freeze(new InvalidOperationException("closed"));
            check(pico.Category==FactorySessionFaultCategory.PicoError && pico.Device=="Pico","Pico cause never defaults to ART");
            tracker=new FactorySessionFaultTracker {SessionId="session-c",Phase="Idle"};
            tracker.Observe("LINK_ART_ERROR","DI read failed");
            var timeout=tracker.Freeze(new InvalidOperationException("latched",new TimeoutException("read deadline")));
            check(timeout.Category==FactorySessionFaultCategory.ReadTimeout && timeout.Device=="ART","typed nested timeout keeps its observed device");
            tracker=new FactorySessionFaultTracker {SessionId="session-d",Phase="Idle"};
            tracker.Observe("LINK_ART_UNKNOWN","retry pending");
            tracker.Observe("P04_IDLE_RETRY_PENDING","one retry");
            tracker.Observe("P04_IDLE_RETRY_EXHAUSTED","eligible for no-output task rebuild");
            tracker.Observe("P04_IDLE_REBUILD_BEGIN","readonly new task");
            tracker.Observe("P04_IDLE_REBUILD_RECOVERED","fresh high");
            tracker.Observe("P04_IDLE_RETRY_RECOVERED","fresh high");
            check(tracker.Freeze(new InvalidOperationException("unrelated failure")).Category==FactorySessionFaultCategory.Unknown,
                "pending/recovered observations do not manufacture a fault cause");
            tracker.Observe("P04_IDLE_RETRY_FAILED","typed retry deadline event");
            check(tracker.Freeze(null).Category==FactorySessionFaultCategory.ReadTimeout,"explicit failed timeout retry retains timeout category");
            tracker=new FactorySessionFaultTracker {SessionId="session-e",Phase="Idle"};
            tracker.Observe("P04_IDLE_REBUILD_FAILED","unspecified rebuild failure");
            check(tracker.Freeze(null).Category==FactorySessionFaultCategory.Unknown,"unspecified rebuild error cannot be guessed to be timeout");
            tracker.Observe("P04_IDLE_TERMINAL_TIMEOUT","confirmed terminal timeout");
            check(tracker.Freeze(null).Category==FactorySessionFaultCategory.ReadTimeout,"explicit terminal rebuild timeout is classified");
            var unknown=new FactorySessionFaultTracker().Freeze(new InvalidOperationException("Pico P0.4 low timeout"));
            check(unknown.Category==FactorySessionFaultCategory.Unknown && unknown.Device=="Unknown","message words never infer hardware or category");
            var unknownTimeout=new FactorySessionFaultTracker().Freeze(new TimeoutException("deadline"));
            check(unknownTimeout.Category==FactorySessionFaultCategory.ReadTimeout && unknownTimeout.Device=="Unknown",
                "typed timeout without a device source does not become an ART fault");
            string directory=Path.Combine(Path.GetTempPath(),"Xiongxin-SessionFaultTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            FactorySessionFaultStore.Write(directory,low,false);
            var loaded=FactorySessionFaultStore.Read(directory,"session-a");
            check(loaded.Category==low.Category && loaded.SessionId==low.SessionId && loaded.Detail==low.Detail,"atomic XML roundtrip preserves metadata");
            check(FactorySessionFaultStore.Read(directory,"another-session").Category==FactorySessionFaultCategory.Unknown,"wrong session metadata never relabels current session");
            File.WriteAllText(Path.Combine(directory,"cleanup-error.txt"),"injected clear failure");
            var cleanup=FactorySessionFaultStore.Read(directory,"session-a");
            check(cleanup.Category==FactorySessionFaultCategory.CleanupError && cleanup.Device=="Session" && cleanup.PriorDetail==low.Detail,
                "cleanup failure takes priority over earlier LOW and retains the first reason");
            var structured=new FactorySessionFaultException(loaded);
            check(Object.ReferenceEquals(structured.Fault,loaded) && structured.Message==loaded.Detail,"UI exception carries original structured fault");
            string corrupt=Path.Combine(directory,"corrupt");Directory.CreateDirectory(corrupt);
            File.WriteAllText(Path.Combine(corrupt,FactorySessionFaultStore.FaultFile),"not XML");
            check(FactorySessionFaultStore.Read(corrupt).Category==FactorySessionFaultCategory.Unknown,"corrupt metadata is unknown, not false LOW or ART failure");
            return "Session fault metadata: "+checks+" managed checks PASS; low/timeout/device/unknown/cleanup/identity; HardwareAccessed=False";
        }
    }
}
