using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Xiongxin.OWTS.Data;

// Offline LOC format/native phase boundary audit only; no device code.
internal static class LocationCycleV2SpanSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static bool Same(double a,double b){return Double.IsNaN(a)?Double.IsNaN(b):!Double.IsNaN(b)&&Math.Abs(a-b)<2e-9;}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(InvalidDataException){rejected=true;}Check(rejected,why);}
    static int Main(string[] args)
    {
        try
        {
            const double rate=2000000;
            foreach(double last in new[]{.0419995,.042})
            {
                var endpoint=new LocationCycleReference{SampleRateHz=rate,SampleCount=84000,CaptureStartSeconds=0,FirstCrossingPositive=true,
                    CrossingSeconds=new[]{0d,last/2,last},SavedDurationSeconds=.042};endpoint.Validate();
                var indices=new[]{-1d,Double.NaN,Double.PositiveInfinity,0d,10000.125,83999d,83999.5,84000d,84000.25,84001d};
                var batch=endpoint.PhasesAt(indices);
                for(int i=0;i<indices.Length;i++)Check(Same(endpoint.PhaseAt(indices[i]),batch[i]),"scalar/batch parity with true saved count: last="+last.ToString("R")+" index="+indices[i]);
                Check(Double.IsNaN(batch[7])&&Double.IsNaN(batch[8]),"virtual native endpoint never exposes unsaved samples");
                var loaded=LocationCycleReference.ParseHeader(new[]{endpoint.HeaderLine()},endpoint.SampleCount,rate);
                Check(loaded.SavedDurationSeconds==.042&&Double.IsNaN(loaded.PhaseAt(84000)),"v2 endpoint roundtrip keeps true count");
            }
            const double vr=1000000;var volts=new double[43126];var samples=new double[86251];
            for(int i=0;i<volts.Length;i++)volts[i]=10*Math.Cos(2*Math.PI*400*i/vr);
            for(int i=0;i<samples.Length;i++)samples[i]=(i%101)*.1234567;
            var window=OscillationCycleWindow.FindWholeMilliseconds(volts,vr,samples.Length,rate,10);
            Check(window.Success&&Math.Abs(window.SavedDurationSeconds-.042)<1e-12,"42.5ms available span floors to42ms");
            LocationCycleReference record;var cropped=window.Crop(samples,rate,0,out record);
            int first=(int)Math.Round(record.CaptureStartSeconds*rate);
            Check(cropped.Length==84000&&cropped.Select((v,i)=>v==samples[first+i]).All(x=>x),"unaltered contiguous84000 source samples, no interpolation or padding");
            Check(record.SampleRateHz==rate&&record.CaptureStartSeconds==first/rate,"true rate and sample origin retained");
            var loadedRecord=LocationCycleReference.ParseHeader(new[]{record.HeaderLine()},84000,rate);
            var probes=Enumerable.Range(0,84000).Select(x=>(double)x).Concat(new[]{84000d,Double.NaN}).ToArray();
            var values=loadedRecord.PhasesAt(probes);
            for(int i=0;i<probes.Length;i++)Check(Same(values[i],loadedRecord.PhaseAt(probes[i])),"cropped native/scalar sample "+i);
            Check(Double.IsNaN(values[83999]),"unbracketed real tail not assigned invented phase");
            Reject(()=>LocationCycleReference.ParseHeader(new[]{record.HeaderLine()},83999,rate),"sample count mismatch rejected");
            Reject(()=>LocationCycleReference.ParseHeader(new[]{record.HeaderLine()},84000,rate/2),"sample rate mismatch rejected");
            var legacyWindow=OscillationCycleWindow.Find(volts,vr,1,10);LocationCycleReference legacy;
            legacyWindow.Crop(samples,rate,0,out legacy);Check(legacy.HeaderLine().Contains("HVX.LocationCycles/1"),"v1 remains v1");
            Check(LocationCycleReference.ParseHeader(new[]{legacy.HeaderLine()},legacy.SampleCount,rate).PhaseAt(0)==legacy.PhaseAt(0),"v1 phase roundtrip preserved");
            if(args.Length>0)ReadThroughConsumer(args[0],args[1],record,cropped);
            Console.WriteLine("LOCATION_CYCLE_V2_SPAN_PASS="+checks+"; HYBRID="+HybridPhaseCore.Enabled+"; HARDWARE=False");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static void ReadThroughConsumer(string assemblyFile,string directory,LocationCycleReference record,double[] cropped)
    {
        Directory.CreateDirectory(directory);string file=Path.Combine(directory,"A_12.00kV_v2-offline.loc");
        var headers=new[]{"HVX LOC cycle record / engineering pC x 100000","Received UTC\t2026-10-08T00:00:00Z","Phase\t"+record.InitialPhaseDegrees.ToString("R",CultureInfo.InvariantCulture),
            "Target peak kV\t12","Cable-length\t170","Velocity (m/us)\t170","Delta x [s]\t"+(1/record.SampleRateHz).ToString("R",CultureInfo.InvariantCulture),
            "Charge unit\tpC x 100000","Capture start seconds\t"+record.CaptureStartSeconds.ToString("R",CultureInfo.InvariantCulture),"Acquisition context\t{}",record.HeaderLine()};
        File.WriteAllLines(file,headers.Concat(cropped.Select(v=>(v*100000).ToString("R",CultureInfo.InvariantCulture))));
        var original=File.ReadAllBytes(file);var a=Assembly.LoadFrom(Path.GetFullPath(assemblyFile));var engine=a.GetType("HVX.Task17.Location.LocationEngine",true);
        var parsed=engine.GetMethod("ReadLocation",All).Invoke(null,new object[]{file});
        Func<object,string,object> field=(o,n)=>{
            if(o==null)throw new Exception("Missing consumer object for "+n);
            var f=o.GetType().GetField(n,All);
            if(f==null)throw new Exception("Missing field "+o.GetType().FullName+"."+n);
            return f.GetValue(o);
        };
        Check((double)field(parsed,"SampleRate")==record.SampleRateHz,"real data-analysis reader retains sample rate");
        var restored=(double[])field(parsed,"Samples");Check(restored.Length==cropped.Length&&restored.Select((v,i)=>Math.Abs(v-cropped[i])<1e-10).All(x=>x),"real data-analysis reader preserves charge and count");
        var reference=field(parsed,"CycleReference");Check((double)field(reference,"SavedDurationSeconds")==.042,"data-analysis consumer recognizesv2 metadata");
        var context=Activator.CreateInstance(engine.GetNestedType("A4Context",BindingFlags.NonPublic),true);context.GetType().GetField("Fs",All).SetValue(context,record.SampleRateHz);
        var owner=Activator.CreateInstance(engine,true);engine.GetMethod("A4ConfigureAnalysisPhase",All).Invoke(owner,new[]{context,(object)13d,50d,reference});
        var review=engine.GetMethod("A4ReviewContext",All).Invoke(null,new[]{context});
        Check(ReferenceEquals(field(review,"CycleReference"),reference),"manual review uses samev2 recorded reference");
        foreach(double index in new[]{0d,2500d,80000d,83999d}){
            double phase=(double)engine.GetMethod("A4PhaseAt",All).Invoke(null,new[]{review,(object)index});
            Check(Same(record.PhaseAt(index),phase),"review v2 phase sample "+index);
        }
        Check(original.SequenceEqual(File.ReadAllBytes(file)),"consumer leaves saved LOC bytes unchanged");
    }
}
