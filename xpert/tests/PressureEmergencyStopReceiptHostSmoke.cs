using System;
using Xiongxin.OWTS.Desktop;

// Test-only contract shape; no runtime/driver assembly or entry point is loaded.
namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryEmergencyRoundStoppedException : OperationCanceledException
    {
        public bool CaptureResourcesReleased {get;set;}
        public bool OutputLatched {get;set;}
        public string StopEvidenceCode {get;set;}
    }
}
internal static class PressureEmergencyStopReceiptHostSmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static int Main()
    {
        try {
            var assembly=typeof(PressureEmergencyStopReceiptHostSmoke).Assembly;
            var stop=new Xiongxin.FactoryTest.FactoryEmergencyRoundStoppedException {
                CaptureResourcesReleased=true,OutputLatched=true,StopEvidenceCode="VerifiedEmergencyRoundStopRetained"};
            Check(ModuleHost.IsVerifiedEmergencyRoundStop(stop,assembly),"exact accepted contract recognized");
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(stop,typeof(string).Assembly),"same-shaped untrusted assembly rejected");
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(new OperationCanceledException(),assembly),"ordinary cancellation is not a stop receipt");
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(null,assembly),"missing error rejected");
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(stop,null),"missing expected assembly rejected");
            stop.CaptureResourcesReleased=false;
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(stop,assembly),"unconfirmed release not retained");
            stop.CaptureResourcesReleased=true;stop.OutputLatched=false;
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(stop,assembly),"unlatched output not retained");
            stop.OutputLatched=true;stop.StopEvidenceCode="Unknown";
            Check(!ModuleHost.IsVerifiedEmergencyRoundStop(stop,assembly),"unknown evidence rejected");
            Console.WriteLine("PRESSURE_EMERGENCY_STOP_RECEIPT_HOST_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0;ProductEntryStarted=false;RealCredentialsRead=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
