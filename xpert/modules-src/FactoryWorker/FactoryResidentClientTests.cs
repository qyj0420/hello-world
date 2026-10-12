using System;
using System.IO;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryResidentClientTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(v,t)=>{if(!v)throw new Exception("Resident client: "+t);checks++;};
            var s=new FactorySettings {Simulation=false,TargetKv=0};
            check(Program.IsReadOnlyPressure(s,FactoryRunKind.Pressure),"pure zero has no outputs");
            s.ZeroSynchronizedOptical=true;
            check(!Program.IsReadOnlyPressure(s,FactoryRunKind.Pressure),"zero optical owner must never enter readonly kill branch");
            s.ZeroSynchronizedOptical=false;s.TargetKv=5;
            check(!Program.IsReadOnlyPressure(s,FactoryRunKind.Pressure),"pressure owner never readonly");
            s.TargetKv=0;
            check(!Program.IsReadOnlyPressure(s,FactoryRunKind.Calibration),"calibration not classified by voltage alone");
            // Constructor deliberately allocates managed resources only. No
            // process Start and no native SDK is called in these checks.
            var client=new FactoryResidentDeviceClient(s);
            check(!client.Alive,"construction alone cannot open a child/device");
            string record=Path.Combine(Path.GetTempPath(),"Xiongxin-CancelledSubmission-"+Guid.NewGuid().ToString("N"));
            bool canceled=false;
            try { client.RunRound(s,FactoryRunKind.Pressure,record,null,new CancellationToken(true)).GetAwaiter().GetResult(); }
            catch(OperationCanceledException) {canceled=true;}
            check(canceled && !Directory.Exists(record),"already canceled round is refused before record or request publication");
            client.Close().GetAwaiter().GetResult();client.Close().GetAwaiter().GetResult();
            check(!client.Alive,"unstarted client closes idempotently without native cleanup fiction");
            var faultClient=new FactoryResidentDeviceClient(s);
            string sessionDirectory=Path.GetDirectoryName(faultClient.ProgressPath);
            FactorySessionFaultStore.Write(sessionDirectory,new FactorySessionFault {SessionId="offline-client",Device="ART",
                Category=FactorySessionFaultCategory.EmergencyInputLow,Phase="Idle",OccurredUtc=DateTime.UtcNow,Detail="injected valid low"},false);
            FactorySessionFaultException observed=null;
            try{faultClient.ThrowIfExited();}catch(FactorySessionFaultException ex){observed=ex;}
            check(observed!=null && observed.Fault.Category==FactorySessionFaultCategory.EmergencyInputLow && observed.Fault.Phase=="Idle",
                "client transports structured LOW rather than inventing an ART communication exception");
            File.WriteAllText(Path.Combine(sessionDirectory,"cleanup-error.txt"),"injected cleanup failure");
            observed=null;try{faultClient.ThrowIfExited();}catch(FactorySessionFaultException ex){observed=ex;}
            check(observed!=null && observed.Fault.Category==FactorySessionFaultCategory.CleanupError,
                "client exit prioritizes cleanup failure over earlier LOW");
            faultClient.Close().GetAwaiter().GetResult();
            return "Resident client: "+checks+" managed checks PASS; no process started; HardwareAccessed=False";
        }
    }
}
