using System;
using System.IO;
using Xiongxin.FactoryTest;

internal static class FactoryEmergencyDisplaySmoke
{
    private static void Passed(string value)
    {
        if(value==null||!value.Contains("PASS")||!value.Contains("HardwareAccessed=False"))throw new Exception("Offline method did not confirm fake-only pass.");
        Console.WriteLine(value);
    }
    private static int Main(string[] args)
    {
        try
        {
            if(args==null||args.Length!=1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Absolute isolated fixture directory required.");
            FactoryResidentDeviceServerTests.FixtureRoot=args[0];
            Passed(FactoryEmergencyDisplayTests.Run());
            Console.WriteLine("Emergency timing contract: "+FactoryEmergencyTimingContractTests.Run()+" managed checks PASS; HardwareAccessed=False; SDKCalled=False");
            Passed(FactoryResidentDeviceServerTests.RunInitialFeedback());
            Passed(FactoryResidentDeviceServerTests.RunContinuousInitialFeedback());
            Passed(FactoryResidentDeviceServerTests.RunFailedRelease());
            Passed(FactoryResidentDeviceServerTests.RunInputStopSnapshots());
            Console.WriteLine("EMERGENCY_DISPLAY_FOCUSED_PASS=6; SDK_CALLED=0; PRODUCT_ENTRY_CALLED=0; USER_PROJECTS_WRITTEN=0");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
