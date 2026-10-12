using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;

// Pure managed selected-LOC profiling. No UI constructor, Handle property,
// message loop, SDK, worker, runtime algorithm or device call. The uninitialized
// overview shell is used only for its pure sample/statistics private methods.
static class R58SingleLocReaderProfileOffline
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static string Hash(string path){using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");}
    static double Ms(Action action){var timer=Stopwatch.StartNew();action();timer.Stop();return timer.Elapsed.TotalMilliseconds;}
    static string N(double value){return value.ToString("F3",CultureInfo.InvariantCulture);}
    static void Profile(string stage,Action action)
    {double first=Ms(action);var times=new List<double>();for(int repeat=0;repeat<7;repeat++)times.Add(Ms(action));times.Sort();Console.WriteLine("stage="+stage+";firstCallMs="+N(first)+";warmMedianMs="+N(times[times.Count/2])+";warmMinMs="+N(times[0])+";warmMaxMs="+N(times[times.Count-1]));}
    static int Main(string[] args)
    {
        try {
            if(args.Length!=3)throw new ArgumentException("module.dll project-root selected.loc");
            string module=Path.GetFullPath(args[0]),project=Path.GetFullPath(args[1]),path=Path.GetFullPath(args[2]),before=Hash(path);
            var assembly=Assembly.LoadFrom(module);var reader=assembly.GetType("HVX.PdReview.ReviewLocationFileReader",true);
            var engine=assembly.GetType("HVX.PdReview.ReviewAnalysisEngine",true);var requestType=assembly.GetType("HVX.PdReview.ReviewAnalysisRequest",true);
            MethodInfo metadata=reader.GetMethod("ReadMetadata",Flags),read=reader.GetMethod("Read",Flags),validate=reader.GetMethod("ValidateSamples",Flags),cancelRead=reader.GetMethod("ReadCancellable",Flags),snapshot=engine.GetMethod("Snapshot",Flags);
            object data=null;
            Profile("header-metadata-11-lines",()=>metadata.Invoke(null,new object[]{path}));
            object request=Activator.CreateInstance(requestType);requestType.GetField("ProjectPath",Flags).SetValue(request,project);requestType.GetField("LocationFilePath",Flags).SetValue(request,path);requestType.GetField("AlgorithmIndex",Flags).SetValue(request,9);requestType.GetField("LocationDataId",Flags).SetValue(request,92L);
            Profile("engine-snapshot-nonzero-Eplus",()=>snapshot.Invoke(null,new[]{request}));
            Profile("legacy-separate-validate-full-body",()=>validate.Invoke(null,new object[]{path,project}));
            Profile("EAnalysis-strict-full-read",()=>data=read.Invoke(null,new object[]{path,project}));
            Profile("UI-preview-cancellable-full-read",()=>cancelRead.Invoke(null,new object[]{path,project,CancellationToken.None}));
            var samples=(double[])data.GetType().GetField("ChargeSamples",Flags).GetValue(data);double rate=(double)data.GetType().GetField("SampleRateHz",Flags).GetValue(data);
            var overviewType=assembly.GetType("HVX.PdReview.ReviewLocationOverview",true);
            object overview=FormatterServices.GetUninitializedObject(overviewType);overviewType.GetField("samples",Flags).SetValue(overview,samples);
            var baseline=overviewType.GetMethod("EstimateBaseline",Flags);var threshold=overviewType.GetMethod("EstimateAutomaticThreshold",Flags);
            Profile("UI-preview-clone-and-finite-range-validation",()=>{var copy=(double[])samples.Clone();foreach(double v in copy)if(!Double.IsNaN(v)&&!Double.IsInfinity(v)&&Math.Abs(v)>1e100)throw new Exception("display range");});
            Profile("UI-preview-baseline-and-maximum-existing-pure-method",()=>baseline.Invoke(overview,new object[0]));
            Profile("UI-threshold-existing-pure-method",()=>threshold.Invoke(overview,new object[]{3d}));
            if(Hash(path)!=before)throw new Exception("selected source LOC changed");
            Console.WriteLine("PROFILE_PASS;LOC="+Path.GetFileName(path)+";fileBytes="+new FileInfo(path).Length+";samples="+samples.Length+";sampleRateHz="+rate.ToString("R",CultureInfo.InvariantCulture)+";durationMs="+N(1000d*samples.Length/rate)+";sourceSha256="+before+";moduleSha256="+Hash(module)+";UIConstructorsCalled=false;RuntimeAlgorithmCalled=false;SDKCalled=false;HardwareUsed=false;SourceProjectWritten=false;FirstCallNotDiskColdGuarantee=true");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
