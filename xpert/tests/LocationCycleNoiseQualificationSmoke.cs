using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using Xiongxin.OWTS.Data;

internal static class LocationCycleNoiseQualificationSmoke
{
    static int checks;
    static void Check(bool condition,string detail){++checks;if(!condition)throw new Exception(detail);}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static void Equal(double actual,double expected,string detail){Check(Math.Abs(actual-expected)<1e-12,detail);}
    static int Main(string[] args)
    {
        try {
            // Two real samples are still the minimum resolved half-cycle. A
            // one-point return inside a transition does not move its first bracket.
            var simple=new[]{1d,1,-1,-1,1,1,-1,-1};
            var normal=OscillationCycleWindow.Find(simple,1000,1,1);
            Check(normal.Success,"two-sample boundary remains accepted");
            Equal(normal.CrossingSeconds[0],.0015,"first real bracket");
            Equal(normal.CrossingSeconds[2],.0055,"last real bracket");
            var bounce=new[]{1d,1,-1,.2,-1,-1,-1,1,-.2,1,1,1,-1,.2,-1,-1};
            foreach(double polarity in new[]{1d,-1d}) {
                var raw=bounce.Select(x=>x*polarity).ToArray();var snapshot=(double[])raw.Clone();
                var result=OscillationCycleWindow.Find(raw,1000,1,1);
                Check(result.Success,"one sample bounce is not a half-cycle");
                Equal(result.CrossingSeconds[0],.0015,"first pending bracket not shifted by bounce");
                Equal(result.CrossingSeconds[1],.0065,"second pending bracket not shifted by bounce");
                Equal(result.CrossingSeconds[2],.0115,"third pending bracket not shifted by bounce");
                Check(result.FirstCrossingPositive==(polarity<0),"measured direction retained");
                Check(raw.SequenceEqual(snapshot),"no raw mutation");
            }
            foreach(int n in new[]{5,7,101}) {
                var unresolved=Enumerable.Range(0,n).Select(i=>i%2==0?1d:-1d).ToArray();
                Check(OscillationCycleWindow.Find(unresolved,1000,1,1).FailureCode=="UNRESOLVED_CROSSINGS","alternating under-sampled input rejected");
            }
            var isolated=new[]{1d,1,-1,1,1,1,-1,1,1,1};
            Check(!OscillationCycleWindow.Find(isolated,1000,1,1).Success,"isolated spikes cannot fabricate full cycle");
            Check(!OscillationCycleWindow.Find(new double[100],1000,1,1).Success,"zero signal rejected");
            Check(!OscillationCycleWindow.Find(Enumerable.Repeat(1d,100).ToArray(),1000,1,1).Success,"dc rejected");
            Check(OscillationCycleWindow.Find(new[]{1d,Double.NaN,-1},1000,1,1).FailureCode=="INVALID_REFERENCE","NaN never smoothed away");
            foreach(double frequency in new[]{20d,37.2,390,850})foreach(double polarity in new[]{-1d,1d}) {
                const double rate=80000;var raw=Enumerable.Range(0,(int)Math.Ceiling(4.1*rate/frequency)).Select(i=>polarity*10*Math.Cos(2*Math.PI*frequency*i/rate)).ToArray();
                var result=OscillationCycleWindow.Find(raw,rate,3,10);
                Check(result.Success&&result.CrossingSeconds.Length==7,"all genuine boundaries retained");
                for(int k=0;k<7;k++)Check(Math.Abs(result.CrossingSeconds[k]-(.25+k*.5)/frequency)<1e-8,"unfitted actual crossing time");
                Check(OscillationCycleWindow.Find(raw,rate,10,10).FailureCode=="INCOMPLETE_CYCLES","insufficient requested count is not silently reduced");
            }
            Check(args.Length==1,"exact recorded regression input required");
            string file=Path.Combine(args[0],"capture.bin"),settingsFile=Path.Combine(args[0],"settings.xml");
            string before=Hash(file),settingsBefore=Hash(settingsFile);
            Check(before=="A86E6A3F96ADF3986A9CF6C86B3018BAAE72B79A6E0B59BCA95138ED40676D81","exact failed real capture bytes");
            var settings=new XmlDocument{XmlResolver=null};settings.Load(settingsFile);
            double divider=Double.Parse(settings.SelectSingleNode("/BenchSettings/DividerVPerV").InnerText,CultureInfo.InvariantCulture)/1000;
            double[] voltage;double actualRate;
            using(var r=new BinaryReader(File.OpenRead(file))) {
                string format=r.ReadString();Check(format=="XIONGXIN-ZERO-BENCH-2","known complete capture schema");
                Check(!r.ReadBoolean(),"real input, not simulation");
                r.ReadInt64();r.ReadString();r.ReadString();r.ReadString();
                actualRate=r.ReadDouble();r.ReadDouble();r.ReadInt32();r.ReadDouble();r.ReadInt32();r.ReadBoolean();r.ReadDouble();
                int count=r.ReadInt32();Check(count==4000&&actualRate==80000,"bounded actual timebase");
                voltage=new double[count];for(int i=0;i<count;i++)voltage[i]=r.ReadDouble()*divider;
            }
            var actualCopy=(double[])voltage.Clone();
            Console.WriteLine("RECORDED_FIRST_RAW="+String.Join(",",Enumerable.Range(43,12).Select(i=>i+":"+voltage[i].ToString("R",CultureInfo.InvariantCulture))));
            foreach(int cycles in new[]{1,2,5}) {
                var result=OscillationCycleWindow.Find(voltage,actualRate,cycles,6.152);
                Check(result.Success&&result.CrossingSeconds.Length==cycles*2+1,"failed real record now resolves requested complete cycles");
                // Independent interpolation from actual first brackets; not
                // frequency or ideal sine reference, and not a later crossing.
                double first=(49+Math.Abs(voltage[49])/(Math.Abs(voltage[49])+Math.Abs(voltage[50])))/actualRate;
                double second=(151+Math.Abs(voltage[151])/(Math.Abs(voltage[151])+Math.Abs(voltage[152])))/actualRate;
                Equal(result.CrossingSeconds[0],first,"first raw bracket unchanged");
                Equal(result.CrossingSeconds[1],second,"single positive spike cannot replace first negative bracket");
                Check(result.CrossingSeconds.Zip(result.CrossingSeconds.Skip(1),(a,b)=>(b-a)*actualRate).All(x=>x>=2-1e-9),"true halfcycles remain sample-resolved");
            }
            Check(!OscillationCycleWindow.Find(voltage,actualRate,26,6.152).Success,"50ms cannot invent 26 cycles or waive an unresolved tail");
            Check(voltage.SequenceEqual(actualCopy),"recorded array remains exact");
            Check(Hash(file)==before&&Hash(settingsFile)==settingsBefore,"original captures/settings remain unchanged");
            Console.WriteLine("PASS "+checks+" raw crossing qualification checks; recorded SHA256="+before+"; no SDK, worker, output or original writes.");
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
