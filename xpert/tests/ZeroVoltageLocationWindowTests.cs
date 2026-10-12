using System;
using System.IO;
using HVX.Controls;

internal static class ZeroVoltageLocationWindowTests
{
    static int checks;
    static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    static void Reject(int count,double rate){bool rejected=false;try{ZeroVoltageLocationWindow.SampleCount(count,rate);}catch(InvalidDataException){rejected=true;}Check(rejected,"invalid input rejected");}
    static int Main()
    {
        try {
            Check(ZeroVoltageLocationWindow.SampleCount(6000,100000)==2000,"60ms source limited to20ms");
            Check(ZeroVoltageLocationWindow.SampleCount(1000,100000)==1000,"10ms source not padded");
            Check(ZeroVoltageLocationWindow.SampleCount(2000,100000)==2000,"20ms unchanged");
            Check(ZeroVoltageLocationWindow.SampleCount(16000000,200000000)==4000000,"high speed budget");
            Check(ZeroVoltageLocationWindow.SampleCount(1000,12345)==246,"fractional budget rounds down");
            Check(ZeroVoltageLocationWindow.SampleCount(100,Double.MaxValue)==100,"no narrowing overflow");
            foreach(double rate in new[]{50d,100d,1000d,12345d,100000d,200000000d})
                for(int count=1;count<10000;count+=137){int saved=ZeroVoltageLocationWindow.SampleCount(count,rate);Check(saved<=count,"never pads");Check(saved/rate<=.020+1e-15,"never exceeds20ms");Check(saved==count||saved==Math.Floor(rate*.020),"retains exact prefix budget");}
            Reject(0,100000);Reject(-1,100000);Reject(100,0);Reject(100,-1);Reject(100,Double.NaN);Reject(100,Double.PositiveInfinity);Reject(100,1);
            Console.WriteLine("PASS "+checks+" | pure storage policy, no devices");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
