using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using HVX.CalibrationStorage;

public sealed class CalibrationLegacyContractDomain : MarshalByRefObject
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    Assembly asm;
    public void Load(string path){asm=Assembly.LoadFrom(path);}
    static object Get(object x,string n){return x.GetType().GetField(n,F).GetValue(x);}
    static void Set(object x,string n,object v){x.GetType().GetField(n,F).SetValue(x,v);}
    static object Call(Type t,string n,params object[] a){return CalibrationProtectedTestReflection.Call(t,n,a);}
    public string Snapshot(string path)
    {
        Type t=CalibrationProtectedTestReflection.Type(asm,"HVX.Task17.Location.LocationEngine");object cal=Call(t,"ReadCalibration",path);
        object c=Activator.CreateInstance(CalibrationProtectedTestReflection.Type(asm,"HVX.Task17.Location.LocationEngine+A4Context"),true);Set(c,"Calibration",cal);Set(c,"Attenuation",true);Set(c,"Fs",Get(cal,"SampleRate"));
        object r=Call(t,"A4BuildAttenuationScoringReference",c,false),a7=Call(t,"A7BuildCalibrationCapabilities",c);
        var d=new Dictionary<string,object>();
        foreach(string n in new[]{"Valid","Reason","Retention","RawRetention","IncidentSnr","ReflectionSnr","TravelSeconds","UncertaintyDb","IndependentCompletePackets","Unclipped"})d[n]=Get(r,n);
        foreach(string n in new[]{"StartTime","FinalTime","CalibrationCoefficient","AttenuationLossDb"})d["CAL_"+n]=Get(cal,n);
        foreach(string n in new[]{"MarkerTimingValid","TimingValid","ChargeCalibrationValid","TemplateReliable","AttenuationReliable","NonmonotonicAmplitude","TravelSeconds"})d["A7_"+n]=Get(a7,n);
        d["referenceJson"]=Call(t,"A4AttenuationReferenceJson",r);d["a7Json"]=Get(a7,"DiagnosticJson");
        foreach(string kind in new[]{"A4BuildLowBandReference","A4BuildBandReference"}) {
            object b=kind=="A4BuildLowBandReference"?Call(t,kind,c):Call(t,kind,c,new double[]{1000000,5000000,10000000});d[kind]=b==null?null:Inspect(b);
        }
        return new JavaScriptSerializer().Serialize(d);
    }
    public double[,] LegacyMatrix(string directory,int method,bool first)
    {
        object result=typeof(LocationCyclePhaseSmoke).GetMethod("Analyze",F).Invoke(null,new object[]{asm,directory,method,false,first,null,125e6,false});
        if(!(bool)Get(result,"Ok"))throw new Exception("Legacy full entry failed: "+Get(result,"Status"));return (double[,])Get(result,"Rows");
    }
    static object Inspect(object value)
    {
        return value.GetType().GetFields(F).Where(f=>f.FieldType==typeof(bool)||f.FieldType==typeof(int)||f.FieldType==typeof(double)||f.FieldType==typeof(string)||
            f.FieldType==typeof(bool[])||f.FieldType==typeof(int[])||f.FieldType==typeof(double[]))
            .ToDictionary(f=>f.Name,f=>f.GetValue(value));
    }
    public override object InitializeLifetimeService(){return null;}
}

internal static class AnalysisCalibrationPacketContractSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Type engine;static string qa;static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static void Check(bool x,string why){checks++;if(!x)throw new Exception(why);}
    static object Get(object x,string n){return x.GetType().GetField(n,F).GetValue(x);}
    static void Set(object x,string n,object v){x.GetType().GetField(n,F).SetValue(x,v);}
    static object Call(Type t,string n,params object[] a){return CalibrationProtectedTestReflection.Call(t,n,a);}
    static object Read(string p){return Call(engine,"ReadCalibration",p);}
    static object Context(object cal)
    {object c=Activator.CreateInstance(CalibrationProtectedTestReflection.Type(engine.Assembly,"HVX.Task17.Location.LocationEngine+A4Context"),true);Set(c,"Calibration",cal);Set(c,"Attenuation",true);Set(c,"Fs",Get(cal,"SampleRate"));return c;}
    static object Ref(object cal){return Call(engine,"A4BuildAttenuationScoringReference",Context(cal),false);}
    static void Reject(Action action,string why)
    {
        try{action();}catch(Exception e){while(e is TargetInvocationException)e=e.InnerException;Check(e is InvalidDataException||e is IOException||e is UnauthorizedAccessException,why+" rejects explicitly: "+e.GetType().Name);return;}
        throw new Exception("Did not reject: "+why);
    }
    static double[] Wave(int first=510,int second=800,bool clip=false,bool one=false)
    {
        return Enumerable.Range(0,4096).Select(i=>{
            double a=.1*Math.Exp(-Math.Pow((i-first)/5d,2));double b=one?0:.06*Math.Exp(-Math.Pow((i-second)/5d,2));
            return clip&&i>=first-1&&i<=first+1?.1:a+b+.00001*Math.Sin(i*.73);}).ToArray();
    }
    static string Cal(string directory,double[] wave,double first=493d/125e6,double second=781d/125e6)
    {
        Directory.CreateDirectory(directory);string p=Path.Combine(directory,"ALL_5nC.cal");
        var h=new[]{"Date\t2026-10-10","Time\t12:00:00","Cable length\t170","Cal.value\t5000","Velocity (m/us)\t170","NI_Calib_factor\t50000",
            "Start_point (s)\t"+first.ToString("R",Inv),"Final_point (s)\t"+second.ToString("R",Inv),"SR\t125000000","Calib_Attenuation\t4.436974992327127","Phase\tALL","Units\tV","Data"};
        File.WriteAllText(p,String.Join("\r\n",h.Concat(wave.Select(x=>x.ToString("R",Inv))))+"\r\n",new UTF8Encoding(false));return p;
    }
    static CalibrationPacketIdentityContract Contract(string p,int first=510,int second=800)
    {
        object parsed=Read(p);return new CalibrationPacketIdentityContract {ProjectedCalSha256=CalibrationPacketIdentityContract.HashFile(p),SourceCalSha256=new string('A',64),
            SampleSha256=CalibrationPacketIdentityContract.HashSamples((double[])Get(parsed,"Samples")),Phase="ALL",TimingMode="FirstArrival",RangeIndex=8,SampleCount=4096,
            ChargePc=5000,SampleRateHz=125e6,CoefficientPcPerV=50000,IncidentSeconds=(double)Get(parsed,"StartTime"),ReflectionSeconds=(double)Get(parsed,"FinalTime"),
            IncidentPeakIndex=first,ReflectionPeakIndex=second};
    }
    static void Mutate(string original,string name,Action<XDocument> edit)
    {
        string p=Cal(Path.Combine(qa,"bad-"+name),Wave());File.Copy(CalibrationPacketIdentityContract.SidecarPath(original),CalibrationPacketIdentityContract.SidecarPath(p));
        XDocument doc=XDocument.Load(CalibrationPacketIdentityContract.SidecarPath(p));edit(doc);doc.Save(CalibrationPacketIdentityContract.SidecarPath(p));Reject(()=>Read(p),name);
        // The frozen candidate reader does not catch InvalidDataException: this
        // propagates a present broken contract, never a silent lower-CAL fallback.
        Reject(()=>Call(engine,"A4ReadPhaseCalCandidate",p,false),name+" cannot silently select or fall back");
    }
    static void MainCases(string algorithm,string old)
    {
        var asm=Assembly.LoadFrom(algorithm);CalibrationProtectedTestReflection.InitializeFor(asm);engine=CalibrationProtectedTestReflection.Type(asm,"HVX.Task17.Location.LocationEngine");
        string p=Cal(Path.Combine(qa,"good"),Wave());object legacy=Read(p),lr=Ref(legacy);
        Check(Get(legacy,"PacketIdentity")==null,"absent contract uses legacy only");
        Check(!(bool)Get(lr,"Valid")&&(string)Get(lr,"Reason")=="calibration_marked_original_packets_not_separable","onset outside observed packet retains legacy rejection");
        double[] raw=((double[])Get(legacy,"Samples")).ToArray();double ta=(double)Get(legacy,"StartTime"),tb=(double)Get(legacy,"FinalTime");
        Contract(p).WriteNew(p);object parsed=Read(p),r=Ref(parsed);
        Check((bool)Get(r,"Valid"),"contract associates original independent packets without widening supports: "+Get(r,"Reason"));
        Check((double)Get(parsed,"StartTime")==ta&&(double)Get(parsed,"FinalTime")==tb&&(double)Get(r,"TravelSeconds")==tb-ta,"first-arrival clock retained exactly, unequal peak offsets not substituted");
        Check(Math.Abs((double)Get(r,"TravelSeconds")-290d/125e6)>1e-12,"travel is not displayed raw peak delay");
        Check(raw.SequenceEqual((double[])Get(parsed,"Samples")),"sample identity unchanged");
        Check(Math.Abs((double)Get(r,"RawRetention")-.6)<.002,"attenuation uses observed local peaks, not onset samples");
        Check((string)Get(r,"TravelClockPolicy")=="saved_CAL_time_unchanged","local policy explicitly traceable");
        object a7=Call(engine,"A7BuildCalibrationCapabilities",Context(parsed));Check((bool)Get(a7,"TimingValid")&&(double)Get(a7,"TravelSeconds")==tb-ta,"inactive A7 has consistent packet identity but same travel clock");
        var original=(double[])Get(parsed,"Samples");original[0]+=.01;Reject(()=>Ref(parsed),"post-parser sample mutation");original[0]-=.01;
        object clock=Read(p);Set(clock,"FinalTime",tb+1/125e6);Reject(()=>Ref(clock),"post-parser clock mutation");
        foreach(var pair in new[]{Tuple.Create("missing","RangeIndex",""),Tuple.Create("bad-hash","ProjectedCalSha256",new string('0',64)),
            Tuple.Create("source-hash","SourceCalSha256","bad"),Tuple.Create("sample-hash","SampleSha256",new string('0',64)),Tuple.Create("phase","Phase","A"),
            Tuple.Create("charge","ChargePc","1000"),Tuple.Create("rate","SampleRateHz","62500000"),Tuple.Create("count","SampleCount","4095"),
            Tuple.Create("coefficient","CoefficientPcPerV","49999"),Tuple.Create("clock","IncidentSeconds","0"),Tuple.Create("oob","ReflectionPeakIndex","4096"),
            Tuple.Create("order","IncidentPeakIndex","800"),Tuple.Create("negative","IncidentPeakIndex","-1"),Tuple.Create("range","RangeIndex","11"),
            Tuple.Create("nan","ChargePc","NaN"),Tuple.Create("mode","TimingMode","Unknown"),Tuple.Create("schema","Schema","HVX.CalibrationPacketIdentity/2")})
            Mutate(p,pair.Item1,d=>{var e=d.Root.Element(pair.Item2);if(pair.Item1=="missing")e.Remove();else e.Value=pair.Item3;});
        Mutate(p,"duplicate",d=>d.Root.Add(new XElement(d.Root.Element("RangeIndex"))));
        Mutate(p,"unknown-field",d=>d.Root.Add(new XElement("SafetyProof","true")));
        Mutate(p,"attribute",d=>d.Root.Element("RangeIndex").SetAttributeValue("permit","true"));
        Mutate(p,"nested",d=>d.Root.Element("Phase").Add(new XElement("nested","ALL")));
        Mutate(p,"comment",d=>d.Root.Add(new XComment("ignored?")));
        string malformed=Cal(Path.Combine(qa,"bad-xml"),Wave());File.WriteAllText(CalibrationPacketIdentityContract.SidecarPath(malformed),"<!DOCTYPE x [<!ENTITY x 'ALL'>]><CalibrationPacketIdentity>&x;</CalibrationPacketIdentity>");Reject(()=>Read(malformed),"DTD/entity");
        string oversized=Cal(Path.Combine(qa,"bad-size"),Wave());File.WriteAllText(CalibrationPacketIdentityContract.SidecarPath(oversized),new string('X',16385));Reject(()=>Read(oversized),"oversized metadata");
        string folder=Cal(Path.Combine(qa,"bad-directory"),Wave());Directory.CreateDirectory(CalibrationPacketIdentityContract.SidecarPath(folder));Reject(()=>Read(folder),"directory cannot mean no sidecar");
        string same=Cal(Path.Combine(qa,"same-packet"),Wave(),ta,tb);Contract(same,510,511).WriteNew(same);object sr=Ref(Read(same));Check(!(bool)Get(sr,"Valid")&&(string)Get(sr,"Reason")=="calibration_marked_original_packets_not_separable","contract cannot split one packet");
        string clipped=Cal(Path.Combine(qa,"clipped"),Wave(510,800,true),ta,tb);Contract(clipped).WriteNew(clipped);Check(!(bool)Get(Ref(Read(clipped)),"Valid"),"plateau clipping guard unchanged");
        string absent=Cal(Path.Combine(qa,"missing-reflection"),Wave(510,800,false,true),ta,tb);Contract(absent).WriteNew(absent);Check(!(bool)Get(Ref(Read(absent)),"Valid"),"identity cannot invent reflection");
        string zero=Cal(Path.Combine(qa,"zero-wave"),new double[4096],ta,tb);Contract(zero).WriteNew(zero);Check(!(bool)Get(Ref(Read(zero)),"Valid"),"identity cannot replace missing SNR/quiet evidence");
        string noisy=Cal(Path.Combine(qa,"noisy"),Wave().Select((x,i)=>x+.025*Math.Sin(i*.91)).ToArray(),ta,tb);Contract(noisy).WriteNew(noisy);
        Check(!(bool)Get(Ref(Read(noisy)),"Valid"),"independent packet identity cannot waive original guarded noise/SNR evidence");
        // This sandbox prohibits creating junctions. Inspect an existing Windows
        // junction read-only instead; the same production ancestor walk rejects
        // it without following its target or creating a competing fixture link.
        string linked=Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),"Users","All Users");
        Check((File.GetAttributes(linked)&FileAttributes.ReparsePoint)!=0,"read-only Windows junction is an actual reparse point");
        Reject(()=>CalibrationPacketIdentityContract.CheckUnlinked(Path.Combine(linked,"HVX-missing-CAL","ALL_5nC.cal")),"reparse ancestor rejected before following or fallback");
        // Exact legacy comparison in a separate AppDomain avoids the identical
        // assembly identity binding to the wrong version in one load context.
        var prior=(CalibrationLegacyContractDomain)AppDomain.CreateDomain("legacyCAL").CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(CalibrationLegacyContractDomain).FullName);prior.Load(old);
        var current=new CalibrationLegacyContractDomain();current.Load(algorithm);
        foreach(string fixture in new[]{"legacy-good","legacy-onset","legacy-clipped","legacy-zero","legacy-same"}) {
            double[] w=fixture=="legacy-zero"?new double[4096]:fixture=="legacy-clipped"?Wave(510,800,true):Wave();
            double first=fixture=="legacy-onset"?ta:510d/125e6,second=fixture=="legacy-same"?511d/125e6:800d/125e6;
            string path=Cal(Path.Combine(qa,fixture),w,first,second);string before=CalibrationPacketIdentityContract.HashFile(path);
            string expected=prior.Snapshot(path),actual=current.Snapshot(path);
            Check(expected==actual,"no-sidecar exact reference/A7/Band/LowBand regression "+fixture);Check(CalibrationPacketIdentityContract.HashFile(path)==before,"legacy CAL read-only "+fixture);
        }
        var lowWave=Enumerable.Range(0,4096).Select(i=>.1*Math.Exp(-Math.Pow((i-500)/25d,2))+.06*Math.Exp(-Math.Pow((i-2500)/25d,2))+.00001*Math.Sin(i*.73)).ToArray();
        string lowPath=Cal(Path.Combine(qa,"legacy-lowband-positive"),lowWave,500d/125e6,2500d/125e6);
        string lowExpected=prior.Snapshot(lowPath),lowActual=current.Snapshot(lowPath);Check(lowExpected==lowActual,"wide-separated legacy LowBand positive unchanged against original");
        var lowSnapshot=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(lowActual);
        Check((bool)((Dictionary<string,object>)lowSnapshot["A4BuildLowBandReference"])["Trusted"],"actual original LowBand positive guard path exercised, not only matching rejections");
        var goodSnapshot=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(current.Snapshot(Path.Combine(qa,"legacy-good","ALL_5nC.cal")));
        Check(goodSnapshot["A4BuildBandReference"]!=null,"actual original Band positive guard path exercised");
        var zeroSnapshot=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(current.Snapshot(Path.Combine(qa,"legacy-zero","ALL_5nC.cal")));
        Check(zeroSnapshot["A4BuildBandReference"]==null&&!(bool)((Dictionary<string,object>)zeroSnapshot["A4BuildLowBandReference"])["Trusted"],"Band and LowBand negative evidence still reject");
        string project=(string)typeof(LocationCyclePhaseSmoke).GetMethod("WriteFixture",F).Invoke(null,new object[]{Path.Combine(qa,"legacy-matrix")});
        typeof(LocationCyclePhaseSmoke).GetMethod("WriteLoc",F).Invoke(null,new object[]{project,null});
        foreach(int method in new[]{5,7})foreach(bool first in new[]{false,true}) {
            double[,] expected=prior.LegacyMatrix(project,method,first),actual=current.LegacyMatrix(project,method,first);
            Check(expected.GetLength(0)>0&&expected.GetLength(0)==actual.GetLength(0)&&expected.GetLength(1)==actual.GetLength(1),"no-sidecar full A/B rows unchanged method="+method+" first="+first);
            for(int row=0;row<expected.GetLength(0);row++)for(int col=0;col<expected.GetLength(1);col++)
                Check(Double.IsNaN(expected[row,col])?Double.IsNaN(actual[row,col]):expected[row,col]==actual[row,col],"no-sidecar exact full A/B matrix method="+method+" row="+row+" col="+col);
        }
        string metadata=CalibrationPacketIdentityContract.SidecarPath(p);byte[] originalMeta=File.ReadAllBytes(metadata);object retained=Read(p);
        File.AppendAllText(metadata," ");Reject(()=>Ref(retained),"post-parser sidecar mutation rejected at use");File.WriteAllBytes(metadata,originalMeta);
        string originalCal=File.ReadAllText(p);File.AppendAllText(p," ");Reject(()=>Ref(retained),"post-parser projected CAL mutation rejected at use");File.WriteAllText(p,originalCal,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(qa,"reference-positive.json"),Call(engine,"A4AttenuationReferenceJson",r).ToString(),new UTF8Encoding(false));
    }
    static int Main(string[] args)
    {
        try{if(args.Length!=3)throw new ArgumentException("Pass new algorithm, legacy algorithm, owned QA case directory.");qa=Path.GetFullPath(args[2]);Directory.CreateDirectory(qa);MainCases(args[0],args[1]);Console.WriteLine("ANALYSIS_CAL_PACKET_CONTRACT_PASS="+checks+"; HARDWARE=0; NO_FULL=true");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
