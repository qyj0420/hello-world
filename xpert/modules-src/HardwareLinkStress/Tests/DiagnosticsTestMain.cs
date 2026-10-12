using System;
using HVX.HardwareLinkStress.Tests;

internal static class DiagnosticsTestMain
{
    private static void Main()
    {
        try
        {
            Console.WriteLine(TransferTierPlannerTests.Run());
            Console.WriteLine(StressEvidenceCoordinatorTests.Run());
        }
        catch (Exception error)
        {
            Console.WriteLine("OFFLINE_TEST_FAILED=" + error.GetType().FullName);
            Console.WriteLine("MESSAGE=" + error.Message);
            Environment.ExitCode = 1;
        }
    }
}
