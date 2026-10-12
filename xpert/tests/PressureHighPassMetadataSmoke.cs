using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static class PressureHighPassMetadataSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    const string Id="PD-HP10K-SYMMETRIC-FIR4-v1", Scope="full-segment-before-cycle-crop";
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Field(object item,string name){return item.GetType().GetField(name,All).GetValue(item);}
    static string Hash(string path){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path)));}
    static string[] Headers(int count,double rate,object filterId,object scope)
    {
        var cycle=new Dictionary<string,object>{{"schema","HVX.LocationCycles/1"},{"sampleRateHz",rate},{"sampleCount",count},
            {"captureStartSeconds",0d},{"firstCrossingPositive",true},{"crossingSeconds",new[]{0d,.005,.01}}};
        if(filterId!=null)cycle.Add("pdAcquisitionFilterId",filterId);
        if(scope!=null)cycle.Add("pdAcquisitionFilterScope",scope);
        return new[]{"HVX LOC cycle record / engineering pC x 100000","Received UTC\t2026-10-07T00:00:00Z","Phase\t0","Target peak kV\t5",
            "Cable-length\t170","Velocity (m/us)\t170","Delta x [s]\t0.000001","Charge unit\tpC x 100000","Capture start seconds\t0",
            "Acquisition context\t{}","HVX_CycleReference\t"+new JavaScriptSerializer().Serialize(cycle)};
    }
    static void Write(string path,string[] header,double[] samples)
    {using(var writer=new StreamWriter(path,false,new UTF8Encoding(true))){foreach(string line in header)writer.WriteLine(line);foreach(double value in samples)writer.WriteLine((value*100000).ToString("R",CultureInfo.InvariantCulture));}}
    static object Read(Type reader,string path,string root){return reader.GetMethod("Read",All).Invoke(null,new object[]{path,root});}
    static void Rejected(Type reader,string path,string root,string[] header,double[] samples)
    {
        Write(path,header,samples);bool failed=false;
        try{Read(reader,path,root);}catch(TargetInvocationException error){failed=error.InnerException is InvalidDataException;}
        Check(failed,"invalid filter identity/scope/count/rate must not silently suppress filtering");
    }
    static int Main(string[] args)
    {
        try
        {
            string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            Assembly module=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            Type reader=module.GetType("HVX.PdReview.ReviewLocationFileReader",true),cap=module.GetType("HVX.PdReview.ReviewCapacitorAnalysis",true);
            var resolve=cap.GetMethod("AcquisitionHighPassedSamples",All);
            var apply=module.GetType("HVX.SignalProcessing.PdLinearHighPass",true).GetMethod("Apply",All);
            double[] raw=Enumerable.Range(0,10001).Select(i=>23+5*Math.Sin(2*Math.PI*10000*i/1000000)).ToArray();
            double[] processed=(double[])apply.Invoke(null,new object[]{raw,1000000d});
            string path=Path.Combine(qa,"A_5kV_metadata.loc");var header=Headers(processed.Length,1000000,Id,Scope);
            Check(header.Length==11,"LOC retains exactly 11 header lines");Write(path,header,processed);string before=Hash(path);
            object data=Read(reader,path,qa);Check((string)Field(data,"AcquisitionFilterId")==Id,"recorded acquisition identity reaches analysis");
            double[] stored=(double[])Field(data,"ChargeSamples"),resolved=(double[])resolve.Invoke(null,new[]{data});
            Check(Object.ReferenceEquals(stored,resolved),"new acquisition is not filtered for a second time");
            Check(stored.Length==processed.Length && stored.Zip(processed,(a,b)=>Math.Abs(a-b)).Max()<1e-12,"stored charge samples are not changed");
            double[] twice=(double[])apply.Invoke(null,new object[]{stored,1000000d});
            Check(twice.Skip(1000).Take(8000).Zip(stored.Skip(1000),(a,b)=>Math.Abs(a-b)).Max()>.5,"fixture detects the frequency-response error from double filtering");
            Check(Hash(path)==before,"reading and analysis never rewrite source provenance");

            Assembly pressure=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            Type reference=pressure.GetType("Xiongxin.OWTS.Data.LocationCycleReference",true);
            object parsed=reference.GetMethod("ParseHeader",All).Invoke(null,new object[]{header,processed.Length,1000000d});
            Check((string)Field(parsed,"PdAcquisitionFilterId")==Id,"cycle reference roundtrip preserves filter ID");
            string roundtrip=(string)reference.GetMethod("HeaderLine",All).Invoke(parsed,null);
            Check(roundtrip.Contains(Id)&&roundtrip.Contains(Scope),"cycle reference writer retains filter provenance in one line");

            // Integer-millisecond v2 shares the exact same full-segment HP
            // provenance. Its final crossing may be at the virtual endpoint.
            var v2Samples=processed.Take(10000).ToArray();
            header=Headers(v2Samples.Length,1000000,Id,Scope);
            header[10]=header[10].Replace("HVX.LocationCycles/1","HVX.LocationCycles/2").Replace("\"sampleCount\":10000,","\"sampleCount\":10000,\"savedDurationSeconds\":0.01,");
            Write(path,header,v2Samples);before=Hash(path);data=Read(reader,path,qa);
            Check((string)Field(data,"AcquisitionFilterId")==Id,"v2 filter identity reaches data analysis");
            stored=(double[])Field(data,"ChargeSamples");resolved=(double[])resolve.Invoke(null,new[]{data});
            Check(Object.ReferenceEquals(stored,resolved),"v2 acquisition is not filtered twice");
            Check(stored.Length==10000&&stored.Zip(v2Samples,(a,b)=>Math.Abs(a-b)).Max()<1e-12,"v2 true saved sample span retained");
            parsed=reference.GetMethod("ParseHeader",All).Invoke(null,new object[]{header,10000,1000000d});
            Check((double)Field(parsed,"SavedDurationSeconds")==.01,"v2 duration roundtrips through shared reader");
            Check(Double.IsNaN((double)reference.GetMethod("PhaseAt",All).Invoke(parsed,new object[]{10000d})),"v2 virtual endpoint is not a saved sample");
            Check(Hash(path)==before,"v2 reader leaves original charge/provenance bytes unchanged");
            Rejected(reader,path,qa,header.Select(x=>x.Replace("HVX.LocationCycles/2","HVX.LocationCycles/99")).ToArray(),v2Samples);

            header=Headers(raw.Length,1000000,null,null);Write(path,header,raw);before=Hash(path);
            data=Read(reader,path,qa);Check((string)Field(data,"AcquisitionFilterId")==String.Empty,"legacy data is not labelled as new filtered acquisition");
            resolved=(double[])resolve.Invoke(null,new[]{data});
            Check(resolved.Zip(processed,(a,b)=>Math.Abs(a-b)).Max()<1e-11,"legacy derived capacitor analysis keeps the original single filtering behavior");
            Check(Hash(path)==before,"legacy analysis does not migrate or mutate the file");
            parsed=reference.GetMethod("ParseHeader",All).Invoke(null,new object[]{header,raw.Length,1000000d});
            roundtrip=(string)reference.GetMethod("HeaderLine",All).Invoke(parsed,null);
            Check(!roundtrip.Contains("pdAcquisitionFilter"),"legacy roundtrip does not fabricate filter identity");
            Rejected(reader,path,qa,Headers(raw.Length,1000000,"unknown-filter",Scope),raw);
            Rejected(reader,path,qa,Headers(raw.Length,1000000,Id,"after-crop"),raw);
            Rejected(reader,path,qa,Headers(raw.Length,1000000,Id,null),raw);
            Rejected(reader,path,qa,Headers(raw.Length,1000000,null,Scope),raw);
            Rejected(reader,path,qa,Headers(raw.Length+1,1000000,Id,Scope),raw);
            Rejected(reader,path,qa,Headers(raw.Length,2000000,Id,Scope),raw);
            Console.WriteLine("PRESSURE_HIGH_PASS_METADATA_PASS="+checks+";hardware=false;legacy_mutated=false");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error is TargetInvocationException?((TargetInvocationException)error).InnerException:error);return 1;}
    }
}
