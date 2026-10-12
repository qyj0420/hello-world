using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

// Synthetic arrays and files only. Never load a driver or call a device.
static class PicoHighPassConsistencySmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static object Get(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static double[] Filter(Type t,double[] x,double fs){return (double[])t.GetMethod("Apply",B).Invoke(null,new object[]{x,fs});}
    static double Gain(Type t,double hz,double fs)
    {
        int n=(int)Math.Round(fs*.012);var x=new double[n];
        for(int i=0;i<n;i++)x[i]=Math.Sin(2*Math.PI*hz*i/fs);
        var y=Filter(t,x,fs);int guard=(int)Math.Ceiling(fs*.001);double a=0,b=0;
        for(int i=guard;i<n-guard;i++){a+=x[i]*x[i];b+=y[i]*y[i];}
        return Math.Sqrt(b/a);
    }
    static void Rejected(Action work,string name)
    {try{work();}catch(TargetInvocationException e){if(e.InnerException is ArgumentException||e.InnerException is OperationCanceledException){Check(true,name);return;}throw;}throw new Exception(name);}
    static void Response(Type cal,Type pressure)
    {
        Check((string)cal.GetField("FilterId").GetValue(null)==(string)pressure.GetField("FilterId").GetValue(null),"identical calibration and pressure filter identity");
        foreach(double fs in new[]{100000d,1000000d,125000000d})
        {
            Check(Math.Abs(Gain(pressure,10000,fs)-1/Math.Sqrt(2))<.001,"10 kHz -3 dB corner at actual rate "+fs);
            Check(Gain(pressure,1000,fs)<.015,"1 kHz rejection "+fs);
            Check(Gain(pressure,40000,fs)>.98,"PD passband "+fs);
            int n=(int)Math.Ceiling(fs*.002)+1,mid=n/2;var x=new double[n];x[mid]=1;
            var y=Filter(pressure,x,fs);var z=Filter(cal,x,fs);
            Check(y.SequenceEqual(z),"calibration/pressure coefficients produce identical samples "+fs);
            Check(Array.IndexOf(y,y.Max())==mid,"zero-phase peak position "+fs);
            Check(Enumerable.Range(0,mid).All(i=>Math.Abs(y[mid-i]-y[mid+i])<1e-12),"symmetric impulse "+fs);
            var a=new double[n];var b=new double[n];for(int i=0;i<n;i++){a[i]=Math.Sin(i*.03);b[i]=Math.Cos(i*.14);}
            var mix=a.Select((v,i)=>2*v-3*b[i]).ToArray();var fa=Filter(pressure,a,fs);var fb=Filter(pressure,b,fs);var fm=Filter(pressure,mix,fs);
            Check(Enumerable.Range(0,n).All(i=>Math.Abs(fm[i]-(2*fa[i]-3*fb[i]))<1e-10),"linear superposition "+fs);
            Check(Filter(pressure,Enumerable.Repeat(12d,n).ToArray(),fs).All(v=>Math.Abs(v)<1e-10),"constant removed "+fs);
            Check(x[mid]==1&&x.Sum()==1,"input preserved "+fs);
        }
        Rejected(()=>Filter(pressure,new[]{double.NaN},1e6),"nonfinite rejected");
        Rejected(()=>Filter(pressure,new[]{1d},80000),"insufficient sampling rejected");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();Rejected(()=>pressure.GetMethod("ApplyCancellable").Invoke(null,new object[]{new double[100],1e6,cancel.Token}),"cancelled processing does not publish output");
        }
    }
    static void Capture(Assembly a,Type hp,string qa,double sampleRate)
    {
        var settings=Activator.CreateInstance(a.GetType("Xiongxin.ZeroBench.BenchSettings",true));
        foreach(var pair in new[]{new object[]{"RealHardware",true},new object[]{"ArtSerial","OFFLINE-ART"},new object[]{"PicoSerial","OFFLINE-PICO"},new object[]{"PicoCoupling",0},new object[]{"RequestedPicoRateHz",1e6},new object[]{"CaptureDurationMs",60d},new object[]{"RangeIndex",6},new object[]{"PdCalibrationPcPerV",1000d},new object[]{"DividerVPerV",11000d}})Set(settings,(string)pair[0],pair[1]);
        Set(settings,"RequestedPicoRateHz",sampleRate);
        var adc=new short[(int)(sampleRate*.060)];for(int i=0;i<adc.Length;i++)adc[i]=(short)(2000+Math.Round(700*Math.Sin(2*Math.PI*1000*i/sampleRate)+1300*Math.Sin(2*Math.PI*100000*i/sampleRate)));
        adc[adc.Length/2]=10000;adc[adc.Length/2+1000]=-10000;
        var voltage=Enumerable.Range(0,4800).Select(i=>.2+.03*Math.Sin(i*.07)).ToArray();
        var capture=Activator.CreateInstance(a.GetType("Xiongxin.ZeroBench.BenchCapture",true));
        foreach(var pair in new[]{new object[]{"Settings",settings},new object[]{"Simulation",false},new object[]{"CapturedUtc",DateTime.UtcNow},new object[]{"ArtSerial","OFFLINE-ART"},new object[]{"PicoSerial","OFFLINE-PICO"},new object[]{"Runtime","synthetic-offline-fixture-no-devices"},new object[]{"ArtRateHz",80000d},new object[]{"PicoRateHz",1e6},new object[]{"MaximumAdc",32767},new object[]{"PicoFullScaleV",1d},new object[]{"HardwareBits",14},new object[]{"Overflow",true},new object[]{"VoltageRaw",voltage},new object[]{"PdAdc",adc}})Set(capture,(string)pair[0],pair[1]);
        Set(capture,"PicoRateHz",sampleRate);
        string folder=Path.Combine(qa,"synthetic-60ms-"+sampleRate);capture.GetType().GetMethod("Save").Invoke(capture,new object[]{folder});
        byte[] before=File.ReadAllBytes(Path.Combine(folder,"capture.bin"));
        var adapter=a.GetType("HVX.Controls.PressureZeroVoltageCapture",true);
        var result=adapter.GetMethod("Read",B,null,new[]{typeof(string),settings.GetType(),typeof(DateTime),typeof(CancellationToken)},null)
            .Invoke(null,new object[]{folder,settings,DateTime.UtcNow.AddMinutes(-1),CancellationToken.None});
        double[] actual=(double[])Get(result,"ChargePc");var sorted=(short[])adc.Clone();Array.Sort(sorted);int median=sorted[sorted.Length/2];
        var expected=Filter(hp,adc.Select(v=>(v-median)*(1000d/32767)).ToArray(),sampleRate);
        Check(actual.SequenceEqual(expected),"actual pressure file reader filters full 60 ms exactly once before cropping");
        Check(Math.Abs((double)Get(result,"PeakPc")-actual.Max(v=>Math.Abs(v)))<1e-12,"displayed peak uses filtered data");
        Check((string)Get(result,"HighPassFilterId")== (string)hp.GetField("FilterId").GetValue(null),"derived capture records high-pass identity");
        Check(((double[])Get(result,"VoltageKv")).SequenceEqual(voltage.Select(v=>v*11)),"voltage channel never high-passed or centered");
        Check((bool)Get(Get(result,"Capture"),"Overflow"),"native overflow flag retained");
        Check(((short[])Get(Get(result,"Capture"),"PdAdc")).SequenceEqual(adc),"native ADC samples retained");
        Check(File.ReadAllBytes(Path.Combine(folder,"capture.bin")).SequenceEqual(before),"raw capture file byte-identical after read");
        var twice=Filter(hp,actual,sampleRate);Check(actual.Where((v,i)=>Math.Abs(v-twice[i])>1e-5).Any(),"fixture distinguishes accidental double filtering");
    }
    [STAThread]static int Main(string[] args)
    {try{
        Directory.CreateDirectory(args[1]);
        var analysis=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        var cal=Assembly.LoadFrom(Path.Combine(args[0],"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll"));
        var pressure=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        Type hp=analysis.GetType("HVX.SignalProcessing.PdLinearHighPass",true);
        Check(pressure.GetType("HVX.SignalProcessing.PdLinearHighPass",false)==null&&pressure.GetReferencedAssemblies().Any(r=>r.Name==analysis.GetName().Name),"pressure references the shared analysis FIR instead of compiling a divergent duplicate");
        Response(cal.GetType("HVX.SignalProcessing.PdLinearHighPass",true),hp);
        Capture(pressure,hp,args[1],1e6);Capture(pressure,hp,args[1],125e6);
        Console.WriteLine("PICO_HIGH_PASS_CONSISTENCY_PASS="+checks+"; HARDWARE_ACCESSED=False; USER_PROJECT_WRITES=False; SYNTHETIC_ONLY=True");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
