using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
internal static class ReportWaveformGeometrySmoke
{
    const BindingFlags F=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;static Type renderer,waveType;
    static void Check(bool b,string m){checks++;if(!b)throw new Exception(m);}
    static object Call(string n,params object[] a){return renderer.GetMethod(n,F).Invoke(null,a);}
    static object Wave(int pd,int v,double pr,double vr){object w=Activator.CreateInstance(waveType);waveType.GetField("Y").SetValue(w,new double[pd]);waveType.GetField("VoltageKv").SetValue(w,Enumerable.Range(0,v).Select(i=>i%2==0?0.00001:-0.00001).ToArray());waveType.GetField("SampleRateHz").SetValue(w,pr);waveType.GetField("VoltageSampleRateHz").SetValue(w,vr);return w;}
    [STAThread] static int Main(string[] args){try{
        var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));renderer=asm.GetType("HVX.ReportGenerator.ProjectWaveformChartRenderer",true);waveType=asm.GetType("HVX.Controls.BrowserWaveform",true);
        foreach(var rates in new[]{new[]{1000d,1000d},new[]{2000d,1000d},new[]{1000d,4000d}}){
            object w=Wave(1001,101,rates[0],rates[1]);var geo=(double[])Call("TestGeometry",w);double expected=Math.Min(1000/rates[0],100/rates[1])*1000;
            Check(Math.Abs(geo[0]-expected)<1e-10,"common measured duration");Check(geo[1]==1000/rates[0]&&geo[2]==1000/rates[1],"individual sample steps");
            for(int channel=0;channel<2;channel++){int n=channel==0?1001:101;double step=geo[channel+1];int count=(int)Call("VisibleCount",n,step,geo[0]);Check(count>0&&(count-1)*step<=expected+1e-8,"last visible sample included");Check(count==n||count*step>expected,"no tail beyond common window");}
        }
        object unknown=Wave(1001,101,1000,double.NaN);Check((double)Call("HorizontalExtent",unknown,false)==100,"unknown voltage timebase uses common sample indices");Check((string)Call("HorizontalLabel",unknown,false,1)=="Sample index","unknown rate does not claim milliseconds");
        object only=Wave(101,0,1000,double.NaN);Check((double)Call("HorizontalExtent",only,false)==100,"single channel valid timebase preserved");
        foreach(double amplitude in new[]{0d,.00001,2,80}){var b=(double[])Call("VoltageBounds",new[]{-amplitude,amplitude,999d},2);Check(b[0]<=-5&&b[1]>=5,"minimum voltage span");Check(b[0]<=-amplitude&&b[1]>=amplitude,"real extrema retained");Check(b[1]<999,"excluded tail not used for scaling");}
        Check((int)Call("VisibleCount",1000,0.3,0.9)==4,"floating endpoint retained");
        object renderWave=Wave(1001,101,1000,1000);var recordType=asm.GetType("HVX.Controls.BrowserRecord",true);object record=Activator.CreateInstance(recordType);recordType.GetField("Kind").SetValue(record,"dem");recordType.GetField("Phase").SetValue(record,"A");
        using(var bmp=new Bitmap(3000,1500))using(var g=Graphics.FromImage(bmp)){Call("Draw",g,record,renderWave,0);bmp.Save(Path.Combine(args[1],"report-near-zero-common-time.png"));}
        Check(((double[])waveType.GetField("Y").GetValue(renderWave)).Length==1001,"source array not truncated");
        Console.WriteLine("REPORT_WAVEFORM_GEOMETRY_PASS="+checks);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
