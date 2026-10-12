#if OFFLINE_EMERGENCY_RETAINED_TEST
using System;
using System.IO;
namespace Xiongxin.FactoryTest
{
    // Separate explicit entry point: never invoke product/worker/SDK entry paths.
    internal static class FactoryEmergencyRetainedOfflineHarness
    {
        private static int Main(string[] args)
        {
            try{
                FactoryResidentDeviceServerTests.FixtureRoot=Path.GetFullPath(args[0]);
                Directory.CreateDirectory(FactoryResidentDeviceServerTests.FixtureRoot);
                Console.WriteLine(FactoryResidentDeviceServerTests.RunEmergencyRoundStop());
                Console.WriteLine(FactoryResidentDeviceServerTests.RunHealthyRoundStop());
                Console.WriteLine(FactoryEmergencyDisplayTests.Run());
                Console.WriteLine(FactoryBufferEmergencyCurrentTests.Run());
                Console.WriteLine("DI timing/deadline policy: "+FactoryEmergencyTimingContractTests.Run()+" managed checks PASS; SDKCalled=False");
                Console.WriteLine("Isolated managed entry only; no product/worker/SDK/hardware entry executed.");return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
    }
}
#endif
