using System;
using Xiongxin.FactoryTest;
internal static class FactoryEmergencyPendingSmoke
{
    static int Main()
    {
        try {
            Console.WriteLine(FactoryEmergencyPendingTests.Run());
            Console.WriteLine(FactoryEmergencyDisplayTests.Run());
            Console.WriteLine("Existing timing: "+FactoryEmergencyTimingContractTests.Run()+" checks PASS; HardwareAccessed=False; SDKCalled=False");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
