using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xiongxin.OWTS.Data;

internal static class LocationCycleWindowSmoke
{
    static int count;
    static void Check(bool ok,string message){count++;if(!ok)throw new Exception(message);}
    static void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}catch(InvalidDataException){rejected=true;}Check(rejected,message);}
    static int Main(){try{
        foreach(double hz in new[]{20d,37.2,500d,850d})foreach(double sign in new[]{1d,-1d})foreach(int cycles in new[]{1,2,5}){
            const double rate=200000;
            var voltage=Enumerable.Range(0,(int)Math.Ceiling((cycles+1.1)*rate/hz)).Select(i=>sign*10*Math.Cos(2*Math.PI*hz*i/rate)).ToArray();
            var window=OscillationCycleWindow.Find(voltage,rate,cycles,10);
            Check(window.Success,"valid crossing window "+hz);
            Check(window.CrossingSeconds.Length==cycles*2+1,"complete count");
            Check(window.FirstCrossingPositive==(sign<0),"first crossing arbitrary direction");
            LocationCycleReference reference;
            var charge=Enumerable.Range(0,voltage.Length).Select(i=>i*.031).ToArray();
            var original=(double[])charge.Clone();var cropped=window.Crop(charge,rate,0,out reference);
            Check(charge.SequenceEqual(original),"source immutable");
            Check(cropped.Length>0&&cropped.Length<=cycles*rate/hz+2,"only complete cycle samples");
            Check(Math.Abs(reference.AverageFrequencyHz-hz)<.001,"measured frequency");
            var parsed=LocationCycleReference.ParseHeader(new[]{reference.HeaderLine()},cropped.Length,rate);
            Check(Math.Abs(parsed.InitialPhaseDegrees-reference.InitialPhaseDegrees)<1e-12,"header round trip");
            for(int i=0;i<cropped.Length;i+=Math.Max(1,cropped.Length/30)){
                double expected=(360*hz*(reference.CaptureStartSeconds+i/rate)+(sign<0?180:0)+90)%360;
                double delta=Math.Abs(parsed.PhaseAt(i)-expected);delta=Math.Min(delta,360-delta);
                Check(delta<.01,"actual record phase");
            }
            Reject(()=>LocationCycleReference.ParseHeader(new[]{reference.HeaderLine()},cropped.Length-1,rate),"mismatched count");
            Reject(()=>LocationCycleReference.ParseHeader(new[]{reference.HeaderLine()},cropped.Length,rate*2),"mismatched rate");
            Reject(()=>window.Crop(charge.Take(5).ToArray(),rate,0,out reference),"no padding to length");
        }
        Check(!OscillationCycleWindow.Find(new double[1000],10000,1,10).Success,"zero voltage abnormality");
        Check(!OscillationCycleWindow.Find(Enumerable.Repeat(10d,1000).ToArray(),10000,1,10).Success,"dc has no crossing");
        Check(!OscillationCycleWindow.Find(new[]{1d,Double.NaN,-1},10000,1,10).Success,"nonfinite voltage");
        var half=Enumerable.Range(0,600).Select(i=>10*Math.Cos(2*Math.PI*20*i/10000)).ToArray();
        Check(OscillationCycleWindow.Find(half,10000,1,10).FailureCode=="INCOMPLETE_CYCLES","half cycle not fabricated");
        Check(LocationCycleReference.ParseHeader(new[]{"legacy"},100,10000)==null,"old header unchanged");
        Reject(()=>OscillationCycleWindow.Find(half,10000,0,10),"zero cycles rejected");
        Reject(()=>OscillationCycleWindow.Find(half,10000,101,10),"unbounded cycles rejected");
        foreach(double invalidTarget in new[]{-1d,Double.NaN,Double.PositiveInfinity})
            Reject(()=>OscillationCycleWindow.Find(half,10000,1,invalidTarget),"invalid expected peak rejected");
        Check(OscillationCycleWindow.Find(new[]{1d,1d,-1d,-1d,1d,1d,-1d,-1d},1000,1,1).Success,"exact two-sample half cycle survives floating point round trip");
        Check(OscillationCycleWindow.Find(new[]{1d,-1d,1d,-1d,1d},1000,1,1).FailureCode=="UNRESOLVED_CROSSINGS","sub-two-sample spacing still rejected");
        var extreme=OscillationCycleWindow.Find(new[]{Double.MaxValue,Double.MaxValue,-Double.MaxValue,-Double.MaxValue,Double.MaxValue,Double.MaxValue,-Double.MaxValue,-Double.MaxValue},1000,1,10);
        Check(extreme.Success&&extreme.CrossingSeconds.All(v=>!Double.IsNaN(v)&&!Double.IsInfinity(v)),"finite extreme crossing interpolation does not overflow");
        Check(Math.Abs(extreme.CrossingSeconds[0]-.0015)<1e-12&&Math.Abs(extreme.CrossingSeconds[2]-.0055)<1e-12,"overflow-safe crossing keeps actual interpolation time");
        Reject(()=>LocationCycleReference.ParseHeader(new[]{LocationCycleReference.HeaderKey+"{}"},100,1000),"malformed new metadata never fallback");
        Console.WriteLine("LOCATION_CYCLE_WINDOW_PASS="+count);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
