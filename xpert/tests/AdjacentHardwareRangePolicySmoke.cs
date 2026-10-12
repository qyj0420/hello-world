using System;
using HVX.Acquisition;
using HVX.Controls;

internal static class AdjacentHardwareRangePolicySmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static bool Reject(Action action){try{action();return false;}catch(ArgumentOutOfRangeException){return true;}}
    static int Main()
    {
        try {
            foreach(int count in new[]{1,2,11})for(int current=0;current<count;current++)for(int recommended=0;recommended<count;recommended++) {
                int expected=current+Math.Sign(recommended-current);
                Check(AdjacentHardwareRangePolicy.Next(current,recommended,count)==expected,"shared adjacent strategy");
                Check(PressureRangeSingleStep.Next(current,recommended,count)==expected,"UI delegates to shared adjacent strategy");
            }
            Check(Reject(()=>AdjacentHardwareRangePolicy.Next(0,0,0)),"empty hardware table rejected");
            Check(Reject(()=>AdjacentHardwareRangePolicy.Next(-1,0,11)),"invalid current range rejected");
            Check(Reject(()=>AdjacentHardwareRangePolicy.Next(11,0,11)),"current outside table rejected");
            Check(Reject(()=>AdjacentHardwareRangePolicy.Next(0,-1,11)),"negative recommendation rejected");
            Check(Reject(()=>AdjacentHardwareRangePolicy.Next(0,11,11)),"recommendation outside table rejected");
            Console.WriteLine("ADJACENT_HARDWARE_RANGE_POLICY_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0;ProductEntryStarted=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
