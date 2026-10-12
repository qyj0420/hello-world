using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Strict-reader and input-preparation benchmark only. No UI, algorithm process,
// product entry, worker, SDK or device is constructed or started.
static class LocationESingleReadOfflineSmoke
{
    static readonly BindingFlags Flags=BindingFlags.Static|BindingFlags.NonPublic;
    static Type reader;static int checks;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
    static object Call(string name,params object[] args)
    {try{return reader.GetMethod(name,Flags).Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    static double[] Samples(object data)
    {return (double[])data.GetType().GetField("ChargeSamples",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(data);}
    static string Hash(string path)
    {using(var input=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(input)).Replace("-","");}
    static string Fixture(string root,string id,IEnumerable<string> samples,int declaredCount=0)
    {
        string path=Path.Combine(root,"A_13.40kV_"+id+".loc");
        var lines=new List<string>{"Phase\tA","Voltage\t13.4 kV","Velocity\t169.6 m/us","SampleRate\t125000000 Hz","ChargeUnit\tpC"};
        if(declaredCount>0)lines.Add("HVXCycleReference\t{\"schema\":\"HVX.LocationCycles/1\",\"pdAcquisitionFilterId\":\"PD-HP10K-SYMMETRIC-FIR4-v1\",\"pdAcquisitionFilterScope\":\"full-segment-before-cycle-crop\",\"sampleCount\":"+declaredCount+",\"sampleRateHz\":125000000}");
        while(lines.Count<11)lines.Add("Reserved\t");
        File.WriteAllLines(path,lines.Concat(samples),new UTF8Encoding(false));return path;
    }
    static void Reject(string path,string root,string reason)
    {
        foreach(string method in new[]{"ValidateSamples","Read"}) {
            try{Call(method,path,root);throw new Exception("Accepted "+reason+" via "+method);}
            catch(InvalidDataException){checks++;}
        }
    }
    static object Benchmark(string path,string root)
    {
        string before=Hash(path);long bytes=new FileInfo(path).Length;
        // Warm the pure managed parser equally before both measured paths.
        Call("Read",path,root);
        var baseline=new List<double>();var single=new List<double>();string sampleHash=null;int count=0;
        for(int trial=0;trial<3;trial++) {
            var timer=Stopwatch.StartNew();Call("ValidateSamples",path,root);var prior=Samples(Call("Read",path,root));timer.Stop();baseline.Add(timer.Elapsed.TotalMilliseconds);
            timer.Restart();var current=Samples(Call("Read",path,root));timer.Stop();single.Add(timer.Elapsed.TotalMilliseconds);
            Check(prior.SequenceEqual(current),"All source samples are bit-identical after removing only the validation pass.");
            count=current.Length;using(var sha=SHA256.Create()) {
                byte[] data=new byte[current.Length*8];Buffer.BlockCopy(current,0,data,0,data.Length);
                string currentHash=BitConverter.ToString(sha.ComputeHash(data)).Replace("-","");
                Check(sampleHash==null||sampleHash==currentHash,"Sample fingerprint remains unchanged across trials.");sampleHash=currentHash;
            }
        }
        Check(Hash(path)==before,"Read-only source unchanged.");
        return new{file=path,bytes=bytes,samples=count,sourceSHA256=before,samplesSHA256=sampleHash,
            oldValidateThenReadMilliseconds=baseline,newSingleStrictReadMilliseconds=single,
            oldMedianMs=baseline.OrderBy(x=>x).ElementAt(1),newMedianMs=single.OrderBy(x=>x).ElementAt(1),scope="Input preparation only; not an E+ end-to-end or field latency measurement."};
    }
    static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=new UTF8Encoding(false);
            if(args.Length<3||args.Length>4)throw new ArgumentException("Expected module, engine source, fresh owned QA fixture root, optional read-only real LOC.");
            string module=Path.GetFullPath(args[0]),engine=File.ReadAllText(args[1],Encoding.UTF8),root=Path.GetFullPath(args[2]);
            if(Directory.Exists(root)||File.Exists(root))throw new IOException("Fresh fixture directory required.");Directory.CreateDirectory(root);
            reader=Assembly.LoadFrom(module).GetType("HVX.PdReview.ReviewLocationFileReader",true);
            int route=engine.IndexOf("if(ReviewEAnalysis.IsMethod(r.AlgorithmIndex))",StringComparison.Ordinal),scan=engine.IndexOf("if(!r.ZeroVoltageRecord)ReviewLocationFileReader.ValidateSamples",StringComparison.Ordinal);
            Check(route>=0&&scan>route,"E branch delegates to its strict reader before legacy full scan.");
            Check(engine.Contains("if(!capacitorDem)ReviewLocationFileReader.Read(loc,projectRoot);"),"Zero-voltage Snapshot validation retained.");
            Check(engine.Contains("inputGuard=new FileStream")&&engine.Contains("FileShare.Read);"),"Source still held read-only through the algorithm read.");
            Check((int)reader.GetField("MaximumSamples",Flags).GetRawConstantValue()==16000000,"Existing sample hard limit remains unchanged.");
            string[] good=Enumerable.Repeat("100000",32).ToArray();
            string valid=Fixture(root,"valid",good,32);Check(Samples(Call("Read",valid,root)).Length==32,"Valid declared count accepted.");
            foreach(string invalid in new[]{"NaN","Infinity","-Infinity","1e309","not-a-number","1e110"}) {
                var samples=(string[])good.Clone();samples[7]=invalid;Reject(Fixture(root,"invalid-"+checks,samples),root,"non-finite, invalid or out-of-range sample");
            }
            var blank=(string[])good.Clone();blank[7]="";Reject(Fixture(root,"interior-empty",blank),root,"interior blank cannot shift sample times");
            Reject(Fixture(root,"count-mismatch",good,33),root,"declared sample count mismatch");
            Reject(Fixture(root,"too-short",good.Take(15)),root,"less than 16 samples");
            string trailing=Fixture(root,"trailing-empty",good.Concat(new[]{"",""}));Check(Samples(Call("Read",trailing,root)).Length==32,"Permitted trailing blanks do not add samples.");
            string benchmark=Fixture(root,"benchmark",Enumerable.Range(0,500000).Select(i=>((i%257-128)*3125).ToString(CultureInfo.InvariantCulture)));
            var runs=new List<object>{Benchmark(benchmark,root)};
            if(args.Length==4)runs.Add(Benchmark(Path.GetFullPath(args[3]),Path.GetDirectoryName(Path.GetFullPath(args[3]))));
            var output=new{passed=true,checks=checks,moduleSHA256=Hash(module),engineSourceSHA256=Hash(args[1]),benchmarks=runs,
                productStarted=false,uiConstructed=false,algorithmInvoked=false,workerStarted=false,sdkCalled=false,hardwareUsed=false,userDataWritten=false};
            Console.WriteLine(new JavaScriptSerializer{MaxJsonLength=1024*1024}.Serialize(output));return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
