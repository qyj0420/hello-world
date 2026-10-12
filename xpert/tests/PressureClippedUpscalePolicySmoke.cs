using System;
using System.Collections.Generic;
using HVX.Acquisition;

// Pure scheduling arithmetic only; no product, worker, SDK or device entry.
internal static class PressureClippedUpscalePolicySmoke
{
    static int checks;
    static void Check(bool pass,string why){checks++;if(!pass)throw new Exception(why);}
    static int Next(int current,int goal,bool window,int count,int clipped,int run)
    {return BoundedClippedRangePolicy.Next(current,goal,11,window,count,clipped,run);}
    static int Available(double[] pc,int current,int goal,ISet<int> visited,int retries,int clipped=3,int run=3,int count=10000)
    {return BoundedClippedRangePolicy.NextCalibrated(pc,current,goal,visited,retries,true,count,clipped,run);}
    static bool Reject(Action action){try{action();return false;}catch(ArgumentOutOfRangeException){return true;}}
    static int Main()
    {
        try
        {
            for(int current=0;current<11;current++)for(int goal=0;goal<11;goal++)
            {
                int adjacent=current+Math.Sign(goal-current),two=goal>current?Math.Min(current+2,goal):current;
                Check(Next(current,goal,true,10000,3,3)==two,"all 11x11 sustained clipping pairs bounded to +2 and cannot downrange");
                Check(Next(current,goal,true,10000,1,1)==(goal>current?adjacent:current),"isolated clipping stays adjacent upward or holds");
                Check(Next(current,goal,false,10000,100,10)==adjacent,"outside/unknown window cannot enable two steps");
                Check(Next(current,goal,true,10000,0,0)==adjacent,"low occupancy without clipping stays adjacent");
                Check(AdjacentHardwareRangePolicy.Next(current,goal,11)==adjacent,"original adjacent primitive unchanged");
            }
            Check(Next(5,9,true,3000,3,1)==7,"exact 0.1 percent distributed clipping permits bounded two steps");
            Check(Next(5,9,true,3001,3,1)==6,"below distributed threshold remains one step");
            Check(Next(5,9,true,1000000,2,2)==6,"two-sample isolated plateau remains one step");
            Check(Next(5,9,true,1000000,3,3)==7,"three consecutive clipped samples need no guessed true peak");
            foreach(var bad in new[]{new[]{0,1,1},new[]{10,11,3},new[]{10,3,4},new[]{10,3,0},new[]{10,-1,1}})
                Check(Next(5,9,true,bad[0],bad[1],bad[2])==6,"inconsistent severity cannot enable a double step");
            Check(Reject(()=>Next(-1,5,true,10,3,3)),"invalid current rejected");
            Check(Reject(()=>Next(5,11,true,10,3,3)),"out of table target rejected");

            var pc=new double[11];for(int i=0;i<pc.Length;i++)pc[i]=100*(i+1);
            for(int current=0;current<11;current++)for(int goal=0;goal<11;goal++)
            {
                int expected=current==10?-1:goal>=current+2?current+2:current+1;
                Check(Available(pc,current,goal,new HashSet<int>(),0)==expected,"finite calibrated table stays within two upward steps");
            }
            var source=(double[])pc.Clone();
            var visited=new HashSet<int>{5};
            Check(Available(pc,5,10,visited,0)==7,"current may already be visited, future target may not");
            visited.Add(7);Check(Available(pc,5,10,visited,0)==6,"visited double target falls back to available adjacent range");
            visited.Add(6);Check(Available(pc,5,10,visited,0)==-1,"visited intermediate never skipped");
            foreach(double absent in new[]{Double.NaN,Double.PositiveInfinity,0d,99d,600d})
            {
                pc[7]=absent;Check(Available(pc,5,10,new HashSet<int>(),0)==6,"invalid/non-increasing target falls back one");
                pc[7]=source[7];pc[6]=absent;Check(Available(pc,5,10,new HashSet<int>(),0)==-1,"invalid/non-increasing intermediate is not skipped");pc[6]=source[6];
            }
            Check(Available(pc,9,10,new HashSet<int>(),0)==10,"one remaining hardware step is honored");
            Check(Available(pc,10,10,new HashSet<int>(),0)==-1,"maximum has no greater usable range");
            Check(Available(pc,5,10,new HashSet<int>(),10)==-1,"finite retry budget unchanged");
            Check(Available(pc,5,10,null,0)==-1,"no visited ledger means no automatic target");
            Check(Available(pc,5,-1,new HashSet<int>(),0)==6,"invalid old optimum cannot create a double step");
            Check(Available(pc,5,10,new HashSet<int>(),0,1,1)==6,"actual isolated raw clipping overrides a severe optimum");
            for(int i=0;i<pc.Length;i++)Check(pc[i]==source[i],"pure calculation never changes calibration input");
            Check(visited.SetEquals(new[]{5,6,7}),"pure calculation never mutates visited ownership");
            Console.WriteLine("PRESSURE_CLIPPED_UPSCALE_POLICY_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0;ProductStarted=false;TruePeakRecovered=false");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
