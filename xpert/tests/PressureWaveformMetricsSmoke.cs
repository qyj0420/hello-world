using System;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using HVX.Controls;
using Xiongxin.OWTS.Data;

internal static class PressureWaveformMetricsSmoke
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static readonly BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Call(object obj,string name,params object[] args){return obj.GetType().GetMethod(name,All).Invoke(obj,args);}
    static object Field(object obj,string name){return obj.GetType().GetField(name,All).GetValue(obj);}
    [STAThread] static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            const double vr=1000000,pr=2000000;
            double[] v=new double[43126],p=new double[86251];
            for(int i=0;i<v.Length;i++)v[i]=10*Math.Cos(2*Math.PI*400*i/vr);
            p[0]=9000;p[20000]=500;p[p.Length-1]=8000;
            var w=OscillationCycleWindow.FindWholeMilliseconds(v,vr,p.Length,pr,10);
            Check(w.Success,"integer-ms source span valid");
            Check(Math.Abs(w.CrossingSeconds[0]-.000625)<1e-10,"first interpolated crossing");
            Check(Math.Abs(w.SavedDurationSeconds-.042)<1e-10,"42.5 ms floors to 42 ms");
            LocationCycleReference reference;var crop=w.Crop(p,pr,0,out reference);
            Check(crop.Length==84000,"42 ms contains exact native sample count");
            Check(reference.HeaderLine().Contains("HVX.LocationCycles/2"),"versioned partial-cycle record");
            var loaded=LocationCycleReference.ParseHeader(new[]{reference.HeaderLine()},crop.Length,pr);
            Check(loaded.SampleCount==crop.Length&&loaded.SavedDurationSeconds==.042,"new record reload span");
            Check(Double.IsNaN(loaded.PhaseAt(crop.Length-1)),"unbracketed tail has no invented phase");
            Check(!Double.IsNaN(loaded.PhaseAt(5000)),"measured crossing phase retained");
            Check(PressureMetricWindow.PeakAfterFirstCrossing(v,vr,p,pr)==500,"prefix and trimmed tail excluded from peak");
            Check(p[0]==9000&&p[p.Length-1]==8000,"raw samples remain intact");
            Check(!PressureMetricWindow.TanDeltaEligible(v,vr,10.2),"below0.7U0 hidden");
            Check(PressureMetricWindow.TanDeltaEligible(v,vr,10),"above0.7U0 eligible");
            Check(!PressureMetricWindow.TanDeltaEligible(new double[v.Length],vr,10),"zero voltage never eligible");
            Check(!PressureMetricWindow.TanDeltaEligible(v,vr,Double.NaN),"unknown U0 never eligible");
            for(int i=0;i<v.Length;i++)v[i]=.7*10*Math.Sqrt(2)*Math.Cos(2*Math.PI*400*i/vr);
            Check(PressureMetricWindow.TanDeltaEligible(v,vr,10),"exact0.7U0 inclusive");
            var old=OscillationCycleWindow.Find(v,vr,1,10);LocationCycleReference oldRef;old.Crop(p,pr,0,out oldRef);
            Check(oldRef.HeaderLine().Contains("HVX.LocationCycles/1"),"legacy complete-cycle contract preserved");
            var a=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            using(var view=(Control)Activator.CreateInstance(a.GetType("HVX.Controls.PressureWaveformView",true),true)){
                view.Size=new Size(1200,370);Call(view,"SetData",v,vr,p,pr);Call(view,"SetDisplayWindow","MAXIMUM",400d);
                double start=(double)view.GetType().GetProperty("DisplayStart",All).GetValue(view,null);
                Check(Math.Abs(start-.000625)<1e-10,"whole-view starts at crossing");
                Check(Math.Abs((double)Call(view,"ResolveDisplayDuration",.043125)-.042)<1e-10,"whole-view has42ms extent");
                Call(view,"SetDischargeScale",true,500d);
                Check((double)view.GetType().GetProperty("DisplayPdMaximum",All).GetValue(view,null)==500,"manual calibrated scale fixed");
                using(var bmp=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(bmp,view.ClientRectangle);bmp.Save(Path.Combine(qa,"wave-manual.png"));}
                Call(view,"SetDischargeScale",false,500d);
                Rectangle plot=new Rectangle(80,20,1000,250),pd=(Rectangle)Call(view,"CreatePdTrack",plot),volts=(Rectangle)Call(view,"CreateVoltageTrack",plot);
                Check(volts.Bottom<pd.Top,"auto tracks separated with original left/right Y axes");
                using(var bmp=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(bmp,view.ClientRectangle);bmp.Save(Path.Combine(qa,"wave-auto.png"));}
                Call(view,"SetDisplayWindow","ONE",400d);
                Check(Math.Abs((double)Call(view,"ResolveDisplayDuration",.043125)-.0025)<1e-10,"one cycle uses same origin");
                Call(view,"SetData",new double[60001],vr,new double[120001],pr);Call(view,"SetDisplayWindow","MAXIMUM",Double.NaN);
                Check((double)view.GetType().GetProperty("DisplayStart",All).GetValue(view,null)==0,"zero reference uses original whole segment");
                Check((double)Field(view,"cachedVoltageAbs")==5,"zero voltage axis at least +/-5kV");
            }
            Console.WriteLine("PASS "+checks+" pressure metrics, crop and waveform assertions; no hardware.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
