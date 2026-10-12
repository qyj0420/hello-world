// Pure synthetic algorithm comparisons only. No host/worker entry, hardware,
// native SDK, device discovery, model loading or inference is invoked.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class OwnedAlgorithmBodyProbe
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static readonly List<string> rows=new List<string>();
    static void Check(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException(message);}
    static void Serialize(BinaryWriter w,object value,int depth=0)
    {
        if(value==null){w.Write("null");return;}
        Type t=value.GetType();w.Write(t.FullName);
        if(value is double){w.Write((double)value);return;}
        if(value is float){w.Write((float)value);return;}
        if(value is string||t.IsPrimitive||t.IsEnum||value is decimal){w.Write(Convert.ToString(value,CultureInfo.InvariantCulture));return;}
        var array=value as Array;
        if(array!=null){w.Write(array.Rank);for(int i=0;i<array.Rank;i++)w.Write(array.GetLength(i));foreach(object v in array)Serialize(w,v,depth+1);return;}
        if(depth>4)throw new InvalidOperationException("Unexpected recursive synthetic result");
        foreach(var f in t.GetFields(BindingFlags.Public|BindingFlags.Instance).OrderBy(x=>x.Name,StringComparer.Ordinal)){w.Write(f.Name);Serialize(w,f.GetValue(value),depth+1);}
        foreach(var p in t.GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(x=>x.CanRead&&x.GetIndexParameters().Length==0).OrderBy(x=>x.Name,StringComparer.Ordinal)){w.Write(p.Name);Serialize(w,p.GetValue(value,null),depth+1);}
    }
    static void Record(string name,object value)
    {
        using(var stream=new MemoryStream()){
            using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))Serialize(writer,value);
            using(var sha=SHA256.Create())rows.Add(name+"="+BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-",""));
        }checks++;
    }
    static object Call(Type type,string method,params object[] args)
    {
        var found=type.GetMethods(All).Where(m=>m.Name==method&&m.GetParameters().Length==args.Length).Where(m=>m.GetParameters().Select((p,i)=>args[i]==null||p.ParameterType.IsInstanceOfType(args[i])).All(x=>x)).ToArray();
        Check(found.Length==1,"unique pure method "+type.FullName+"::"+method);return found[0].Invoke(null,args);
    }
    static void Invalid(Type type,string method,params object[] args)
    {
        try{Call(type,method,args);throw new InvalidOperationException("invalid synthetic case unexpectedly accepted");}
        catch(TargetInvocationException e){Check(e.InnerException is ArgumentException||e.InnerException is OperationCanceledException,"bounded expected rejection");Record(type.Name+"-invalid-"+rows.Count,e.InnerException.GetType().FullName);}
    }
    static void Signals(Assembly assembly)
    {
        double[] signal=Enumerable.Range(0,4096).Select(i=>.04+Math.Sin(i*.023)+.2*Math.Cos(i*.31)).ToArray();
        Type hp=assembly.GetType("HVX.SignalProcessing.PdLinearHighPass",false);
        if(hp!=null){
            foreach(double rate in new[]{1e6,10e6,125e6}){var copy=(double[])signal.Clone();var result=(double[])Call(hp,"Apply",copy,rate);Check(copy.SequenceEqual(signal),"HP does not mutate input");Check(result.Length==copy.Length,"HP length");Record("HP-"+rate,result);}
            Invalid(hp,"Apply",new double[]{0,double.NaN,1},1e6);
            var cancellable=hp.GetMethod("ApplyCancellable",All);
            if(cancellable!=null)Invalid(hp,"ApplyCancellable",signal,1e6,new CancellationToken(true));
        }
        Type lp=assembly.GetType("HVX.SignalProcessing.PdLinearLowPass",false);
        if(lp!=null){foreach(double cutoff in new[]{1d,5d,60d})Record("LP-"+cutoff,Call(lp,"Apply",signal,1e-8,cutoff,129));Invalid(lp,"Apply",signal,0d,1d,129);}
        Type bandwidth=assembly.GetType("HVX.SignalProcessing.ReflectionBandwidth",false);
        if(bandwidth!=null)Record("reflection-ratio",Call(bandwidth,"Ratio",signal,400,2500,20));
    }
    static void Calibration(Assembly assembly)
    {
        foreach(string name in new[]{"HVX.OWTS.Task38.CalibrationWindowSettings","Xiongxin.FactoryTest.Compatibility100.CalibrationWindowSettings"}){
            Type windowType=assembly.GetType(name,false);if(windowType==null)continue;
            object settings=Activator.CreateInstance(windowType);
            foreach(double length in new[]{26d,100d,756d}){windowType.GetField("CableLengthM").SetValue(settings,length);Record(name+"-"+length,windowType.GetProperty("WindowSamples").GetValue(settings,null));}
            FieldInfo capacitive=windowType.GetField("CapacitiveLoad");
            if(capacitive!=null){capacitive.SetValue(settings,true);Record(name+"-capacitive",windowType.GetProperty("WindowSamples").GetValue(settings,null));capacitive.SetValue(settings,false);}
            windowType.GetField("SampleRateHz").SetValue(settings,double.NaN);
            try{windowType.GetMethod("Validate").Invoke(settings,null);throw new InvalidOperationException("bad rate accepted");}
            catch(TargetInvocationException e){Check(e.InnerException is ArgumentException,"window rejects bad rate");}
        }
        double[][] pulses=Enumerable.Range(0,8).Select(k=>Enumerable.Range(0,600).Select(i=>.003*Math.Sin(i*.8)+(.5+k*.0001)*Math.Exp(-Math.Pow((i-360)/8d,2))).ToArray()).ToArray();
        Type quality=assembly.GetType("HVX.OWTS.Task38.CalibrationIncidentQuality",false);
        if(quality!=null){foreach(bool clipped in new[]{false,true})Record("incident-quality-"+clipped,Call(quality,"Analyze",pulses,200,336,464,5000d,1d,clipped,"synthetic",null));}
        Type planning=assembly.GetType("Xiongxin.Calibration.AcquisitionPlanning",false);
        if(planning!=null){
            Type resultType=assembly.GetType("HVX.OWTS.Task38.CalibrationWindowResult",true);
            object input=Activator.CreateInstance(resultType);resultType.GetField("Windows").SetValue(input,pulses);resultType.GetField("Message").SetValue(input,"synthetic");
            Record("consistent-pulses",Call(planning,"SelectConsistentPulses",input));Record("review-average",Call(planning,"ContinueForReview",input));
            Record("screening-threshold",Call(planning,"ResolveScreeningThreshold",(double?)null,.01d,.5d));
        }
        Type arrival=assembly.GetType("HVX.Controls.CalibrationArrivalMath",false);
        if(arrival!=null)Record("arrival",Call(arrival,"Find",pulses[0],360,0,500,400,0d,.003d));
        Type ui=assembly.GetType("HVX.Controls.CalibrationProgramControl",false);
        if(ui!=null){
            Record("display-window",Call(ui,"CalculateDisplayWindow",0d,.00001d,1e-8,100d,160d,.000001d,.000003d));
            // No Control constructor or window is created. This pure helper only
            // reads the supplied wave and assigns its three private index fields.
            object bare=FormatterServices.GetUninitializedObject(ui);GC.SuppressFinalize(bare);
            Type waveType=ui.GetNestedType("WaveformData",All);
            object wave=Activator.CreateInstance(waveType,All,null,new object[]{pulses[0],0d,1e-8},CultureInfo.InvariantCulture);
            ui.GetMethod("SelectNoiseEnvelope",All).Invoke(bare,new object[]{wave,345,380});
            Record("noise-envelope",new[]{(int)ui.GetField("noiseSelectionStart",All).GetValue(bare),(int)ui.GetField("noiseSelectionEnd",All).GetValue(bare),(int)ui.GetField("noiseSelectionPeak",All).GetValue(bare)});
        }
    }
    static void Ranges(Assembly assembly)
    {
        Type advisor=assembly.GetType("Xiongxin.ZeroBench.LocationRangeAdvisor",false);
        if(advisor!=null){foreach(short amplitude in new short[]{0,10,9000,30000,32767}){short[] data=Enumerable.Range(0,1000).Select(i=>(short)(i%13==0?amplitude:0)).ToArray();Record("range-"+amplitude,Call(advisor,"Evaluate",data,0,1000,32767,.5d,5,5000d,10000d,false));}}
        Type window=assembly.GetType("Xiongxin.ZeroBench.LocationRangeWindow",false);
        if(window!=null){foreach(double target in new[]{0d,10000d})Record("window-"+target,Call(window,"Select",target,0,100000,1e6,2,.001d,100d,true));}
        Type analysis=assembly.GetType("Xiongxin.ZeroBench.SignalAnalysis",false);
        if(analysis!=null)Record("initial-range",Call(analysis,"InitialRangeFromPulse",5000d,10000d));
    }
    static double[,] FilterBRows(params double[][] events)
    {
        var matrix=new double[events.Length,36];
        for(int i=0;i<events.Length;i++){
            matrix[i,0]=events[i][0];matrix[i,5]=events[i][1];matrix[i,13]=events[i][2];
            matrix[i,15]=events[i][3];matrix[i,2]=events[i][4];matrix[i,35]=1;matrix[i,1]=7;
        }return matrix;
    }
    static void FilterBExpect(Type engine,object options,string name,double[,] matrix,string keep,string unknown)
    {
        var original=(double[,])matrix.Clone();
        object result=Call(engine,"Evaluate",matrix,Enumerable.Repeat(true,matrix.GetLength(0)).ToArray(),options);
        Type rt=result.GetType();
        var retained=(bool[])rt.GetProperty("KeepMask").GetValue(result,null);
        var uncertain=(bool[])rt.GetProperty("UnknownMask").GetValue(result,null);
        Check(retained.Length==keep.Length&&uncertain.Length==unknown.Length,"B result shape "+name);
        for(int i=0;i<keep.Length;i++){
            Check(retained[i]==(keep[i]=='1'),"B hand-count keep "+name+"/"+i);
            Check(uncertain[i]==(unknown[i]=='1'),"B hand-count unknown "+name+"/"+i);
        }
        Check(matrix.Cast<double>().SequenceEqual(original.Cast<double>()),"B preserves matrix "+name);
        Check((int)rt.GetProperty("RetainedRows").GetValue(result,null)==retained.Count(x=>x),"B retained count "+name);
        Check((int)rt.GetProperty("UnknownRows").GetValue(result,null)==uncertain.Count(x=>x),"B unknown count "+name);
        Record("filter-B-"+name,result);
    }
    static void FilterB(Assembly assembly)
    {
        Type engine=assembly.GetType("HVX.PD.LocationFiltering.LocationClusterStrengthFilterB",false);
        if(engine==null)return;
        Type optionType=assembly.GetType("HVX.PD.LocationFiltering.LocationFilterBOptions",true);
        object options=Activator.CreateInstance(optionType);
        Check((double)optionType.GetProperty("Strength").GetValue(options,null)==40d,"B original default remains 40");
        FilterBExpect(engine,options,"count-phase-length",FilterBRows(new[]{1d,1000d,500d,21d,10d},new[]{1d,1000d,501d,1d,10d},new[]{1d,1000d,502d,20d,10d},new[]{2d,1000d,501d,1d,10d},new[]{0d,1000d,501d,1d,10d},new[]{1d,999d,501d,1d,10d}),"111000","000000");
        FilterBExpect(engine,options,"strict-f32-seed",FilterBRows(new[]{1d,1000d,500d,20.0000001d,10d}),"0","0");
        FilterBExpect(engine,options,"left-mirror",FilterBRows(new[]{1d,200d,0d,21d,10d},new[]{1d,200d,1d,1d,10d}),"10","00");
        FilterBExpect(engine,options,"right-unknown-terminal",FilterBRows(new[]{1d,200d,199d,21d,10d},new[]{1d,200d,200d,1d,10d}),"11","10");
        FilterBExpect(engine,options,"quantized-zero-voltage",FilterBRows(new[]{1d,100d,50d,21d,1e-50d}),"0","0");
        optionType.GetProperty("Strength").SetValue(options,5d,null);
        FilterBExpect(engine,options,"nearest-even",FilterBRows(new[]{1d,100d,50.5d,21d,10d},new[]{1d,100d,49d,1d,10d},new[]{1d,100d,52d,1d,10d}),"101","000");
        optionType.GetProperty("VoltageLimitKv").SetValue(options,10d,null);
        FilterBExpect(engine,options,"rounded-voltage",FilterBRows(new[]{1d,1000d,500d,21d,10.04d},new[]{1d,1000d,501d,1d,10.06d}),"10","00");
        optionType.GetProperty("Strength").SetValue(options,101d,null);
        Invalid(engine,"ValidateOptions",options);
        optionType.GetProperty("Strength").SetValue(options,40d,null);
        Invalid(engine,"Evaluate",new double[1,35],new[]{true},options);
        Invalid(engine,"Evaluate",new double[1,36],new bool[0],options);
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            string path=Path.GetFullPath(args[0]);if(!path.StartsWith("D:\\codex\\HVXUnifiedDesktop\\build\\",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("QA build copy required");
            var assembly=Assembly.LoadFrom(path);Signals(assembly);Calibration(assembly);Ranges(assembly);FilterB(assembly);
            Check(rows.Count>0,"at least one numerical group must run");
            foreach(string row in rows)Console.WriteLine(row);Console.WriteLine("CHECKS="+checks+"; HARDWARE=False; PRODUCT_ENTRY=False; MODEL_INFERENCE=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
