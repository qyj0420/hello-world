using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HVX.SignalProcessing;
using Xiongxin.ZeroBench;
using Xiongxin.FactoryTest;

// Dedicated synthetic entry point. Never calls FactoryRunner, a device backend,
// worker Main, capture file loading, or SDK. Setup/GC are outside timed sections.
internal static class FactoryPostStopCpuTimingSmoke
{
    static long checks;
    static object retained;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;
    static readonly MethodInfo Stats = typeof(FactoryAnalysis).GetMethod("Statistics", BindingFlags.NonPublic | BindingFlags.Static);
    static readonly MethodInfo Osc = typeof(FactoryAnalysis).GetMethod("AnalyzeOscillation", BindingFlags.NonPublic | BindingFlags.Static);
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Bits(double[] a, double[] b, string text)
    {
        Check(a.Length == b.Length, text + " length");
        for (int i=0;i<a.Length;i++)
        {
            checks++;
            if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i]))
                throw new Exception(text + " first mismatch=" + i);
        }
    }
    static object Reflect(MethodInfo method, int index, int count)
    {
        try { return method.Invoke(null, new object[]{index,count}); }
        catch(TargetInvocationException e) { return e.InnerException.GetType().FullName; }
    }
    static void ReflectionEquivalence()
    {
        var old=typeof(FactoryHighPassLegacyOracle).GetMethod("Reflect",BindingFlags.NonPublic|BindingFlags.Static);
        var fast=typeof(FactoryHighPassFastCandidate).GetMethod("Reflect",BindingFlags.NonPublic|BindingFlags.Static);
        var actual=typeof(PdLinearHighPass).GetMethod("Reflect",BindingFlags.NonPublic|BindingFlags.Static);
        // All representable integer indices are covered algebraically by the
        // guarded identity interval; sampled extreme/outside cases test unchanged
        // modulo/overflow/exception behavior, not exhaustive 2^64 enumeration.
        int[] counts={Int32.MinValue,Int32.MinValue+1,-17,-1,0,1,2,3,7,1024,50000000,1073741824,1073741825,Int32.MaxValue};
        int[] edges={Int32.MinValue,Int32.MinValue+1,-50000001,-1025,-2,-1,0,1,2,1024,50000000,Int32.MaxValue-1,Int32.MaxValue};
        var r=new Random(271828);
        foreach(int count in counts)
        {
            foreach(int i in edges) { var expected=Reflect(old,i,count); Check(Object.Equals(expected,Reflect(fast,i,count)),"candidate reflect edge"); Check(Object.Equals(expected,Reflect(actual,i,count)),"actual reflect edge"); }
            for(int k=0;k<1000;k++) { int i=unchecked((r.Next()<<1)|(r.Next()&1)); var expected=Reflect(old,i,count); Check(Object.Equals(expected,Reflect(fast,i,count)),"candidate reflect random"); Check(Object.Equals(expected,Reflect(actual,i,count)),"actual reflect random"); }
            if(count>=0 && count<=1024)
                for(int i=-4096;i<=4096;i++) { var expected=Reflect(old,i,count); Check(Object.Equals(expected,Reflect(fast,i,count)),"candidate reflect exhaustive local"); Check(Object.Equals(expected,Reflect(actual,i,count)),"actual reflect exhaustive local"); }
        }
    }
    static double[] Data(int count, int seed)
    {
        var x=new double[count]; uint state=(uint)seed;
        for(int i=0;i<count;i++) { state=unchecked(state*1664525+1013904223); x[i]=((int)(state>>8)%20001-10000)/10000.0+.125; }
        if(count>5) { x[0]=-0.0; x[1]=0.0; x[count-1]=.7; }
        return x;
    }
    static void FilterEquivalence()
    {
        foreach(double fs in new[]{100000.0,1000000.0,125000000.0,1000000000.0})
        foreach(int n in new[]{0,1,2,3,20,401,4001})
        {
            var x=Data(n,n+11); var frozen=(double[])x.Clone();
            var old=FactoryHighPassLegacyOracle.Apply(x,fs);
            Bits(old,FactoryHighPassFastCandidate.Apply(x,fs),"four-pass candidate "+fs+"/"+n);
            Bits(old,PdLinearHighPass.Apply(x,fs),"four-pass actual "+fs+"/"+n);
            Bits(frozen,x,"raw unchanged");
        }
        foreach(int n in new[]{1,2,20,4001,100001})
        {
            var x=new double[n]; for(int i=0;i<n;i++)x[i]=.3;
            Bits(FactoryHighPassLegacyOracle.Apply(x,125000000),FactoryHighPassFastCandidate.Apply(x,125000000),"constant");
            x[n/2]=1; Bits(FactoryHighPassLegacyOracle.Apply(x,125000000),PdLinearHighPass.Apply(x,125000000),"impulse");
        }
        foreach(double bad in new[]{Double.NaN,Double.NegativeInfinity,Double.PositiveInfinity})
        {
            string expected=Error(()=>FactoryHighPassLegacyOracle.Apply(new[]{1.0,bad},125000000));
            Check(expected==Error(()=>FactoryHighPassFastCandidate.Apply(new[]{1.0,bad},125000000)),"nonfinite candidate unchanged");
            Check(expected==Error(()=>PdLinearHighPass.Apply(new[]{1.0,bad},125000000)),"nonfinite actual unchanged");
        }
        Check(Error(()=>PdLinearHighPass.Apply(null,125000000))==Error(()=>FactoryHighPassLegacyOracle.Apply(null,125000000)),"null unchanged");
        foreach(double badRate in new[]{0.0,99999.0,1000000001.0,Double.NaN,Double.PositiveInfinity})
            Check(Error(()=>PdLinearHighPass.Apply(new double[0],badRate))==Error(()=>FactoryHighPassLegacyOracle.Apply(new double[0],badRate)),"invalid rate unchanged");
    }
    static string Error(Action action) { try { action();return "none"; } catch(Exception e){return e.GetType().FullName+"|"+e.Message;} }
    static double Time(string stage,int iteration,Action action)
    {
        var sw=Stopwatch.StartNew(); action(); sw.Stop();
        Console.WriteLine("CPU_STAGE|"+stage+"|"+iteration+"|"+sw.Elapsed.TotalMilliseconds.ToString("F6",C));
        return sw.Elapsed.TotalMilliseconds;
    }
    static BenchCapture Capture(int pdCount)
    {
        var s=new BenchSettings { RequestedPicoRateHz=125000000,CaptureDurationMs=pdCount/125000.0,
            RangeIndex=5,ResolutionBits=14,RealHardware=false,OscillationCircuitConnected=true };
        var c=new BenchCapture {Settings=s,VoltageRaw=new double[s.ExpectedArtCount],PdAdc=new short[pdCount],
            ArtRateHz=80000,PicoRateHz=125000000,PicoFullScaleV=.5,MaximumAdc=32767,
            HardwareBits=PicoCaptureConfiguration.HardwareBits(14,5),Simulation=true,
            ArtSerial="SIM-ART",PicoSerial="SIM-PICO",Runtime="offline-synthetic-only",CapturedUtc=DateTime.UtcNow};
        uint state=1701;
        for(int i=0;i<pdCount;i++) { state=unchecked(state*1664525+1013904223); c.PdAdc[i]=(short)((int)(state>>24)-128); }
        for(int i=125000;i<pdCount;i+=250000)c.PdAdc[i]=12000;
        for(int i=0;i<c.VoltageRaw.Length;i++) { double t=i/c.ArtRateHz; c.VoltageRaw[i]=.001+.05*Math.Exp(-25*t)*Math.Sin(2*Math.PI*400*t); }
        c.ValidateCapture(); return c;
    }
    static FactoryResult Result(BenchCapture c)
    {
        return new FactoryResult {Kind=FactoryRunKind.Pressure,Capture=c,AcquisitionCompleted=true,PostCaptureMonitoringSkipped=true,
            Settings=new FactorySettings{Input=c.Settings,Simulation=true,TargetKv=10,EndAfterPressureCapture=true}};
    }
    static FactoryMetrics Oscillation(BenchCapture c)
    {
        var m=new FactoryMetrics(); int begin=(int)Math.Min(c.VoltageRaw.Length,Math.Ceiling((c.Settings.PreTriggerDurationMs+c.Settings.TransientExclusionMs)*c.ArtRateHz/1000));
        Osc.Invoke(null,new object[]{c,begin,m,new List<string>()}); return m;
    }
    static void Collect() { retained=null;GC.Collect();GC.WaitForPendingFinalizers();GC.Collect(); }
    static void Profile()
    {
        const int n=6250000;
        var warmup=Result(Capture(125000));FactoryAnalysis.Analyze(warmup);FactoryHighPassLegacyOracle.Apply(Data(2000,11),125000000);FactoryHighPassFastCandidate.Apply(Data(2000,11),125000000);
        var data=Data(n,9123); var immutable=(double[])data.Clone();
        double[] old=null,fast=null,actual=null;
        for(int iteration=1;iteration<=7;iteration++)
        {
            Collect();
            if((iteration&1)==1) {
                Time("filter-legacy-direct",iteration,()=>old=FactoryHighPassLegacyOracle.Apply(data,125000000));
                Time("filter-fast-test-candidate-direct",iteration,()=>fast=FactoryHighPassFastCandidate.Apply(data,125000000));
            } else {
                Time("filter-fast-test-candidate-direct",iteration,()=>fast=FactoryHighPassFastCandidate.Apply(data,125000000));
                Time("filter-legacy-direct",iteration,()=>old=FactoryHighPassLegacyOracle.Apply(data,125000000));
            }
            Bits(old,fast,"6250000 every output bit legacy/candidate");
            Time("filter-factory-actual-direct",iteration,()=>actual=PdLinearHighPass.Apply(data,125000000));
            Bits(old,actual,"6250000 every output bit legacy/actual");
            old=null;fast=null;actual=null;
        }
        Bits(immutable,data,"large raw unchanged");data=null;immutable=null;Collect();
        for(int iteration=1;iteration<=3;iteration++)
        {
            var c=Capture(n); var result=Result(c);Collect();
            Time("adc-statistics",iteration,()=>retained=Stats.Invoke(null,new object[]{c}));
            double[] filtered=null;
            Time("pd-get-cold",iteration,()=>filtered=FactoryPdSignal.Get(c));
            Time("pd-get-warm-exact-snapshot-scan",iteration,()=>retained=FactoryPdSignal.Get(c));
            Check(Object.ReferenceEquals(filtered,retained),"warm reuses exact filtered array");
            Time("detected-build-warm",iteration,()=>{double a,b;retained=FactoryDetectedWaveforms.Build(c,out a,out b);});
            FactoryMetrics m=null;
            Time("oscillation-art-only",iteration,()=>m=Oscillation(c));Check(m.FrequencyValid,"synthetic oscillation fitted");
            Time("range-phase-art-only",iteration,()=>retained=FactoryRangePhase.Analyze(c));
            Time("reference-art-only",iteration,()=>retained=FactoryReferenceAnalysis.Analyze(c.VoltageRaw,c.ArtRateHz,c.Settings.DividerVPerV,c.Settings.TransientExclusionMs,m.FrequencyHz,m.DampingAlphaPerSecond,m.FrequencyValid,200,.0024,c.Settings.ResistanceOhms,8,true));
            Time("full-analysis-warm",iteration,()=>result.Metrics=FactoryAnalysis.Analyze(result));
            Time("range-review-after-runner-separate",iteration,()=>retained=FactoryRangeReview.EvaluateCapturedData(result));
            c=null;result=null;filtered=null;Collect();
            var cold=Result(Capture(n));Collect();
            Time("full-analysis-cold",iteration,()=>retained=FactoryAnalysis.Analyze(cold));cold=null;Collect();
        }
    }
    public static int Main()
    {
        try {
            Console.WriteLine("SCOPE|synthetic 6250000 ADC + 4000 ART; Stopwatch CPU wall durations; no hardware/SDK/worker Main/product Main/file data; not measured field speedup");
            ReflectionEquivalence();FilterEquivalence();Profile();
            Console.WriteLine(FactoryPdSignalTests.Run());Console.WriteLine(FactoryAnalysisTests.Run());
            Console.WriteLine("PASS|checks="+checks);return 0;
        } catch(Exception e) { Console.Error.WriteLine(e);return 1; }
    }
}
