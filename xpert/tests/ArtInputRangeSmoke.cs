using System;
using System.IO;
using System.Linq;
using Xiongxin.FactoryTest;
using Xiongxin.ZeroBench;
internal static class ArtInputRangeSmoke
{
    static int n;
    static void Check(bool b,string why){n++;if(!b)throw new Exception(why);}
    static void Reject(Action a,string why){bool b=false;try{a();}catch(ArgumentException){b=true;}catch(InvalidOperationException){b=true;}Check(b,why);}
    static int Main(string[] args)
    {
        try{
            foreach(double kv in new[]{1.0,5,28,30,60})foreach(double ratio in new[]{11000.0,20000}){
                var s=new FactorySettings{MaximumKv=kv,TargetKv=0};s.Input.DividerVPerV=ratio;
                var p=new FactoryArtFeedbackPlan(s);double expected=kv*1000/ratio*1.5;
                Check(Math.Abs(p.DividerRangeV-expected)<1e-12,"divider exact formula");
                Check(p.ImonRangeV==5.5&&p.VmonRangeV==5.5,"DC fixed upper bounds");
                var f=p.Parse(new[]{0.0,0.0,0.0},1,0,true,0);Check(f.ImonHalfCodeV==5.5/8192&&f.DividerHalfCodeV==expected/4096,"unipolar/bipolar quantization metadata");
                Reject(()=>p.Parse(new[]{-.01,0.0,0.0},1,0,true,0),"negative DC rejected");
                Reject(()=>p.Parse(new[]{0.0,5.5,0.0},1,0,true,0),"DC clipping rejected");
                Reject(()=>p.Parse(new[]{0.0,0.0,expected*.999},1,0,true,0),"positive divider clipping rejected");
                Reject(()=>p.Parse(new[]{0.0,0.0,-expected*.999},1,0,true,0),"negative divider clipping rejected");
                FactoryArtFeedbackNative.ValidateRange(0,5.5,0,5.5);n++;
                FactoryArtFeedbackNative.ValidateRange(-expected,expected,-expected,expected);n++;
                Reject(()=>FactoryArtFeedbackNative.ValidateRange(-5.5,5.5,0,5.5),"wrong DC lower readback rejected");
                Reject(()=>FactoryArtFeedbackNative.ValidateRange(-10,10,-expected,expected),"coerced range rejected");
            }
            var bad=new FactorySettings{MaximumKv=100};bad.Input.DividerVPerV=1000;Reject(()=>FactoryArtFeedbackPlan.DividerFullScale(bad),"unsupported divider range rejected");
            bad.Input.DividerVPerV=Double.NaN;Reject(()=>FactoryArtFeedbackPlan.DividerFullScale(bad),"NaN divider rejected");
            DateTime now=DateTime.UtcNow;
            var clipped=FactoryFinalAi4Observation.Evaluate(Enumerable.Repeat(2.0,800).ToArray(),80000,11000,false,now,now.AddMilliseconds(10),2);
            Check(!clipped.Available,"final observation uses actual range not old 10V");
            Console.WriteLine(FactoryStartupFeedbackTests.Run());Console.WriteLine(FactoryFaultResidualTests.Run());
            Console.WriteLine("ART_INPUT_RANGE_PASS="+n+";HARDWARE_CALLS=0;DRIVERS_LOADED=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
