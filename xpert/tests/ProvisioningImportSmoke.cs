using System;
using System.Globalization;
using System.IO;

namespace Xiongxin.OWTS.Desktop
{
    internal static class ProvisioningImportSmoke
    {
        private static int assertions;
        private static void Check(bool condition,string name)
        { if (!condition) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
        private static void Reject(Action run,string name)
        {
            try { run(); } catch (InvalidDataException) { Check(true,name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        private static string Xml(DateTime utc, bool omitAcquisitionId = false)
        {
            return "<HardwareProvisioning Schema=\"1\" State=\"ReadyAtExport\" Mac=\"1CBCEC5A9C69\" IpAtExport=\"192.168.2.5\" PersistentMode=\"auto\" VerifiedUtc=\"" +
                utc.ToString("O",CultureInfo.InvariantCulture) + "\">" +
                "<Module Kind=\"Controller\" Vid=\"3063\" Pid=\"3135\" Location=\"H11\" InstanceId=\"USB\\VID_3063&amp;PID_3135\\H\"/>" +
                "<Module Kind=\"Acquisition\" Vid=\"0CE9\" Pid=\"1218\" Location=\"S51\"" +
                (omitAcquisitionId ? "" : " InstanceId=\"USB\\VID_0CE9&amp;PID_1218\\S\"") + "/></HardwareProvisioning>";
        }
        private static ExactInspection Ready(ProvisioningCandidate candidate,DateTime checkedUtc)
        {
            return new ExactInspection {
                FromExactConnectorInspect=true,Ready=true,StatusCode="Ready",
                HubMac=candidate.Mac,RequestedMac=candidate.Mac,
                RequestedControllerLocation=candidate.ControllerLocation,
                RequestedAcquisitionLocation=candidate.AcquisitionLocation,
                IntegrationExecutablePathUsed=@"C:\Product\modules\HardwareIntegration\HardwareIntegration.exe",
                ExpectedIntegrationExecutablePath=@"C:\Product\modules\HardwareIntegration\HardwareIntegration.exe",
                IntegrationRequestMode="--inspect",
                ControllerInstanceId=candidate.ControllerInstanceId,
                AcquisitionInstanceId=candidate.AcquisitionInstanceId,
                ControllerTransportSerial="H",AcquisitionTransportSerial="S",
                ControllerSerial="ART-REAL",AcquisitionSerial="PICO-REAL",
                CheckedUtc=checkedUtc
            };
        }
        private static int Main()
        {
            DateTime now=new DateTime(2026,9,30,8,0,0,DateTimeKind.Utc);
            var candidate=ProvisioningImportAdapter.Parse(Xml(now.AddMinutes(-1)),now);
            var result=ProvisioningImportAdapter.Inspect(candidate,c=>Ready(c,now),now);
            Check(result.HardwareEnabled && result.StrongTransportIdentity,"both PnP identities allow activation");
            Check(ProvisioningImportAdapter.IsCurrentActivation(result,now),"fresh unmodified activation accepted");
            Check(!ProvisioningImportAdapter.IsCurrentActivation(result,now.AddSeconds(31)),"activation expires before delayed save");
            var changed=ProvisioningImportAdapter.Inspect(
                ProvisioningImportAdapter.Parse(Xml(now.AddMinutes(-1)),now),c=>Ready(c,now),now);
            changed.Candidate.ControllerLocation="H12";
            Check(!ProvisioningImportAdapter.IsCurrentActivation(changed,now),"mutated candidate cannot activate");
            var changedSerial=ProvisioningImportAdapter.Inspect(
                ProvisioningImportAdapter.Parse(Xml(now.AddMinutes(-1)),now),c=>Ready(c,now),now);
            changedSerial.ControllerSerial="spoof";
            Check(!ProvisioningImportAdapter.IsCurrentActivation(changedSerial,now),"mutated activation cannot activate");
            var missing=ProvisioningImportAdapter.Parse(Xml(now.AddMinutes(-1),true),now);
            Check(!ProvisioningImportAdapter.Inspect(missing,c=>Ready(c,now),now).HardwareEnabled,
                "missing exported PnP identity is hard rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.ControllerInstanceId="";x.ControllerTransportSerial="";return x;},now).HardwareEnabled,
                "missing inspected PnP identity is hard rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.AcquisitionInstanceId="USB\\VID_0CE9&PID_1218\\CHANGED";return x;},now).HardwareEnabled,
                "same-location exchanged module is rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.FromExactConnectorInspect=false;return x;},now).HardwareEnabled,
                "status-file or cache result cannot activate");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.IntegrationRequestMode="--connect";return x;},now).HardwareEnabled,
                "connect result cannot substitute inspect");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.IntegrationExecutablePathUsed=@"C:\Old\HardwareIntegration.exe";return x;},now).HardwareEnabled,
                "old connector process path is rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.RequestedMac="1CBCEC5A9C70";return x;},now).HardwareEnabled,
                "different request MAC is rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.RequestedControllerLocation="H12";return x;},now).HardwareEnabled,
                "different request Controller location is rejected");
            Check(!ProvisioningImportAdapter.Inspect(candidate,c=>{var x=Ready(c,now);x.CheckedUtc=now.AddSeconds(-31);return x;},now).HardwareEnabled,
                "stale inspect result is rejected");
            int calls=0;
            DateTime almostExpired=now.AddMinutes(-5).AddSeconds(1);
            Reject(()=>ProvisioningImportAdapter.Inspect(
                ProvisioningImportAdapter.Parse(Xml(almostExpired),now),
                c=>{calls++;return Ready(c,now.AddSeconds(3));},
                new Func<DateTime>(delegate { return calls==0 ? now : now.AddSeconds(3); })),
                "slow inspect that crossed five-minute export TTL rejected");
            Check(calls==1,"slow callback was actually invoked before expiry");
            var late=ProvisioningImportAdapter.Inspect(candidate,c=>Ready(c,now),
                new Func<DateTime>(delegate { return now.AddSeconds(1); }));
            Check(!ProvisioningImportAdapter.IsCurrentActivation(late,now.AddMinutes(5)),
                "late UI commit cannot reuse earlier Ready result");
            Console.WriteLine("PASS " + assertions + " provisioning import assertions");
            return 0;
        }
    }
}
