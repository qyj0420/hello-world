using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Pixel-geometry and cache verification only; no product entry or SDK.
internal static class PressureVoltagePolylineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object o,string name){return o.GetType().GetField(name,F).GetValue(o);}
    static void Set(object o,string name,object value){o.GetType().GetField(name,F).SetValue(o,value);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,F).Invoke(o,args);}
    static PointF[] Points(object view,Rectangle plot,double duration,double peak)
    {return (PointF[])Call(view,"GetVoltageDrawingPoints",plot,duration,peak);}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            Type type=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"))
                .GetType("HVX.Controls.PressureWaveformView",true);
            using(var view=(Control)Activator.CreateInstance(type,true)){
                var raw=new double[128001];
                for(int i=0;i<raw.Length;i++)raw[i]=8*Math.Sin(i*.00012)+.3*Math.Sin(i*Math.PI/4);
                var before=(double[])raw.Clone();double rate=2560000,duration=(raw.Length-1)/rate;
                Call(view,"SetData",raw,rate,new double[0],0d);
                var retained=(double[])Get(view,"voltage");var display=(double[])Get(view,"displayVoltage");
                double maximum=(double)Get(view,"cachedVoltageAbs");var plot=new Rectangle(80,20,1000,300);
                PointF[] points=Points(view,plot,duration,maximum);
                Check(points.Length==plot.Width+2,"dense voltage has one averaged point per pixel plus actual endpoints, not two extrema saw teeth");
                for(int b=0;b<plot.Width;b++){
                    int first=(int)((long)b*display.Length/plot.Width),end=(int)((long)(b+1)*display.Length/plot.Width);
                    double mean=0;for(int i=first;i<end;i++)mean+=display[i]/(end-first);
                    double index=(first+end-1)/2d;
                    double x=plot.Left+index/rate/duration*plot.Width;
                    double y=plot.Top+(1-mean/maximum)/3*plot.Height;
                    Check(Math.Abs(points[b+1].X-x)<.0002&&Math.Abs(points[b+1].Y-y)<.0002,"all native samples contribute to the display pixel mean "+b);
                }
                Check(points[0].X==plot.Left&&points[points.Length-1].X==plot.Right,"native sample endpoints preserve time extent");
                Check(points.Zip(points.Skip(1),(a,b)=>a.X<=b.X).All(v=>v),"drawing time increases without synthetic duplicate extrema teeth");
                Check(raw.SequenceEqual(before)&&retained.SequenceEqual(before),"display averaging retains every raw sample unchanged");
                Check(maximum>=raw.Max(Math.Abs),"axis uses retained original peak, not pixel mean");
                long builds=(long)Get(view,"voltageDrawingBuildCount");var watch=Stopwatch.StartNew();
                for(int i=0;i<50;i++)Check(Object.ReferenceEquals(points,Points(view,plot,duration,maximum)),"unchanged paint reuses geometry "+i);
                watch.Stop();Check((long)Get(view,"voltageDrawingBuildCount")==builds,"ordinary paint does not rescan waveform");
                Console.WriteLine("VOLTAGE_CACHED_50_CALLS_MS="+watch.Elapsed.TotalMilliseconds.ToString("0.000"));
                var resized=Points(view,new Rectangle(80,20,1001,301),duration,maximum);
                Check(!Object.ReferenceEquals(points,resized)&&resized.Length==1003,"width/height or DPI-equivalent geometry invalidates once");
                var scaled=Points(view,plot,duration,maximum+1);Check(!Object.ReferenceEquals(points,scaled),"new measured axis cannot reuse stale Y coordinates");
                Call(view,"SetDisplayWindow","ONE",400d);
                var window=Points(view,plot,.0025,maximum);Check(!Object.ReferenceEquals(points,window),"window/duration changes invalidate drawing cache");
                Call(view,"SetDisplayWindow","MAXIMUM",0d);
                double[] sparse={0,2,0,-2,0};Call(view,"SetData",sparse,1000d,new double[0],0d);
                var zoom=Points(view,plot,.004,5);Check(zoom.Length==sparse.Length,"sparse/zoomed input renders actual display samples instead of fitting sine");
                var actual=(double[])Get(view,"displayVoltage");
                for(int i=0;i<zoom.Length;i++)Check(Math.Abs(zoom[i].Y-(plot.Top+(1-actual[i]/5)/3*plot.Height))<.0002,"sparse sample value preserved "+i);
                Call(view,"ClearData");Check(Get(view,"cachedVoltageDrawingSource")==null&&((PointF[])Get(view,"cachedVoltageDrawingPoints")).Length==0,"clear releases cached sample reference, not only visible graphics");
                Check(Points(view,plot,.004,5).Length==0,"clear never retains old voltage trace");
                var extremes=new double[4096];for(int i=0;i<extremes.Length;i++)extremes[i]=i%2==0?Double.MaxValue:-Double.MaxValue;
                Set(view,"displayVoltage",extremes);Set(view,"voltage",(double[])extremes.Clone());Set(view,"cachedTimeKnown",false);
                var bounded=Points(view,new Rectangle(0,0,128,300),4095,Double.MaxValue);
                Check(bounded.All(p=>!Single.IsNaN(p.Y)&&!Single.IsInfinity(p.Y)),"valid finite extreme samples have finite convex-average pixels");
                Set(view,"displayVoltage",new double[4096]);Set(view,"voltage",new double[4096]);
                var zero=Points(view,new Rectangle(0,0,128,300),4095,5);
                Check(zero.All(p=>p.Y==100),"zero still draws one thin true-zero trajectory");
                // Render one representative dense measured/noisy trace for QA.
                view.Size=new Size(1000,400);Call(view,"SetData",raw,rate,new double[0],0d);
                using(var bitmap=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(bitmap,view.ClientRectangle);bitmap.Save(Path.Combine(qa,"dense-voltage-display.png"));}
            }
            Console.WriteLine("PRESSURE_VOLTAGE_POLYLINE_PASS="+checks+";HardwareAccessed=False;SDKCalled=False;ProductEntryExecuted=False;UserProjectsWritten=False;PhysicalAcceptance=False");return 0;
        }catch(Exception error){Console.Error.WriteLine("PRESSURE_VOLTAGE_POLYLINE_FAIL="+checks+": "+error);return 1;}
    }
}
