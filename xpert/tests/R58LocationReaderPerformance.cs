using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using HVX.PdReview;

internal static class R58LocationReaderPerformance
{
    static int checks;
    static void Require(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
    static string Header(){return "LOC regression\nReceived UTC\t2026-10-12T00:00:00Z\nPhase\t0\nTarget peak kV\t6\nCable-length\t175.4\nVelocity (m/us)\t156.7\nDelta x [s]\t8E-09\nCharge unit\tpC x 100000\nCapture start seconds\t0\nAcquisition context\t{}\nReserved\tfixture\n";}
    static string SampleDigest(double[] values)
    {using(var hash=SHA256.Create()){var bytes=new byte[values.Length*sizeof(double)];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","");}}
    static string Put(string root,string name,string body,Encoding encoding=null)
    {string path=Path.Combine(root,name+".loc");File.WriteAllText(path,Header()+body,encoding??new UTF8Encoding(false));return path;}
    static void Contract(string root)
    {
        string[] valid={"100000"," -200000 ","+3e5",".25e6","100000\tlegacy","100000 other","100000,legacy","1,234","-0"," 1.5e5\tsecond\tthird"};
        double[] expected={1,-2,3,2.5,1,1,1,.00001,0,1.5};
        for(int i=0;i<valid.Length;i++){
            string path=Put(root,"valid-"+i,String.Join("\n",Enumerable.Repeat(valid[i],16))+"\n\n");
            var result=ReviewLocationFileReader.Read(path,root);
            Require(result.ChargeSamples.Length==16&&result.ChargeSamples.All(x=>BitConverter.DoubleToInt64Bits(x)==BitConverter.DoubleToInt64Bits(expected[i])),"legacy numeric contract "+i);
            ReviewLocationFileReader.ValidateSamples(path,root);checks++;
        }
        foreach(string invalid in new[]{"NaN","Infinity","-Infinity","1e309","foo","1e106",",2","1e,2"}){
            string path=Put(root,"invalid-"+checks,String.Join("\n",Enumerable.Repeat(invalid,16)));
            bool rejected=false;try{ReviewLocationFileReader.Read(path,root);}catch(InvalidDataException){rejected=true;}
            Require(rejected,"invalid sample remains rejected: "+invalid);
        }
        string interior=Put(root,"interior",String.Join("\n",Enumerable.Repeat("100000",16))+"\n\n100000");
        bool missing=false;try{ReviewLocationFileReader.Read(interior,root);}catch(InvalidDataException e){missing=e.Message.Contains("interior");}Require(missing,"interior blank not dropped");
        string shortFile=Put(root,"short","100000\n");bool tooShort=false;try{ReviewLocationFileReader.Read(shortFile,root);}catch(InvalidDataException){tooShort=true;}Require(tooShort,"minimum sample count");
        string pending=Put(root,"pending",String.Join("\n",Enumerable.Repeat("100000",16)));File.WriteAllText(Path.ChangeExtension(pending,".record.pending"),"pending");
        bool unpublished=false;try{ReviewLocationFileReader.Read(pending,root);}catch(InvalidDataException){unpublished=true;}Require(unpublished,"unpublished record rejected");
        using(var cancellation=new CancellationTokenSource()){cancellation.Cancel();bool cancelled=false;try{ReviewLocationFileReader.ReadCancellable(shortFile,root,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}Require(cancelled,"cancellation preserved");}
    }
    static Dictionary<string,object> Measure(string path,string root)
    {
        var expected=ReviewLocationFileReader.Read(path,root);string digest=SampleDigest(expected.ChargeSamples);
        var readTimes=new List<double>();var validateTimes=new List<double>();int collections=0;
        for(int i=0;i<7;i++){
            GC.Collect();GC.WaitForPendingFinalizers();int before=GC.CollectionCount(0);var watch=Stopwatch.StartNew();
            var actual=ReviewLocationFileReader.Read(path,root);watch.Stop();readTimes.Add(watch.Elapsed.TotalMilliseconds);collections+=GC.CollectionCount(0)-before;
            Require(SampleDigest(actual.ChargeSamples)==digest,"all retained samples bit identical");
            watch.Restart();ReviewLocationFileReader.ValidateSamples(path,root);watch.Stop();validateTimes.Add(watch.Elapsed.TotalMilliseconds);
        }
        return new Dictionary<string,object>{{"FileName",Path.GetFileName(path)},{"Bytes",new FileInfo(path).Length},{"Samples",expected.ChargeSamples.Length},{"SampleSHA256",digest},
            {"ReadMilliseconds",readTimes.ToArray()},{"ReadMedianMilliseconds",readTimes.OrderBy(x=>x).ElementAt(3)},
            {"ValidateMilliseconds",validateTimes.ToArray()},{"ValidateMedianMilliseconds",validateTimes.OrderBy(x=>x).ElementAt(3)},{"ReadGeneration0Collections",collections}};
    }
    static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);Contract(root);
            string synthetic=Path.Combine(root,"million-samples.loc");
            if(!File.Exists(synthetic))using(var writer=new StreamWriter(synthetic,false,new UTF8Encoding(false),65536)){
                writer.Write(Header());for(int i=0;i<1200000;i++)writer.WriteLine((i%2000-1000).ToString(CultureInfo.InvariantCulture));
            }
            var measured=new List<object>{Measure(synthetic,root)};
            if(args.Length>2&&File.Exists(args[2]))measured.Add(Measure(args[2],Path.GetDirectoryName(args[2])));
            var evidence=new Dictionary<string,object>{{"Schema","HVX.R58.LocationReader.Performance/1"},{"Checks",checks},{"Tests",measured.ToArray()},
                {"HardwareAccessed",false},{"SDKCalled",false},{"ProductEntryExecuted",false},{"PhysicalAccuracyVerified",false},{"Scope","Actual strict LOC parser only; no candidate algorithm or GUI executed"}};
            File.WriteAllText(args[1],new JavaScriptSerializer{MaxJsonLength=1024*1024}.Serialize(evidence),new UTF8Encoding(false));
            Console.WriteLine("R58_LOCATION_READER_PASS="+checks+"; proof="+args[1]);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
