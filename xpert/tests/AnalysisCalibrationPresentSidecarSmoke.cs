using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using HVX.CalibrationStorage;

internal static class AnalysisCalibrationPresentSidecarSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Assembly asm;static Type scope,engine;static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,F);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,F).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(Type t,string n,params object[] a){return t.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(null,a);}
    static void Reject(Action action,string why){try{action();}catch(Exception e){while(e is TargetInvocationException)e=e.InnerException;Check(e is IOException||e is InvalidDataException||e is UnauthorizedAccessException,why);return;}throw new Exception("Accepted: "+why);}
    static double[] Wave(){return Enumerable.Range(0,4096).Select(i=>.1*Math.Exp(-Math.Pow((i-500)/5d,2))+.06*Math.Exp(-Math.Pow((i-750)/5d,2))+.00001*Math.Sin(i*.73)).ToArray();}
    static string Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        var h=new[]{"Cable length\t170","Velocity (m/us)\t170","Delta x\t8E-9","Phase\t0","Voltage\t12.0","Reserved1\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Data"};
        var w=Enumerable.Range(0,4096).Select(i=>1000*(Math.Exp(-Math.Pow((i-500)/10d,2))+.6*Math.Exp(-Math.Pow((i-700)/10d,2))));
        File.WriteAllText(Path.Combine(root,"A_12.0kV_fixture.loc"),String.Join("\r\n",h.Concat(w.Select(v=>(v*100000).ToString("R",Inv)))),new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"A_12.0kV_fixture.dem"),"Hz\t50\r\nPhase\t40\r\n",new UTF8Encoding(true));
        string cal=Path.Combine(root,"ALL_5nC.cal");double first=3.1280000000000004e-6,second=5.2e-6;
        var ch=new[]{"Date\t2026-10-10","Time\t12:00:00","Cable length\t170","Cal.value\t5000","Velocity (m/us)\t170","NI_Calib_factor\t50000","Start_point (s)\t"+first.ToString("R",Inv),"Final_point (s)\t"+second.ToString("R",Inv),"SR\t125000000","Calib_Attenuation\t4.436974992327127","Phase\tALL","Units\tV","Data"};
        File.WriteAllText(cal,String.Join("\r\n",ch.Concat(Wave().Select(x=>x.ToString("R",Inv))))+"\r\n",new UTF8Encoding(false));return cal;
    }
    static void WriteContract(string cal)
    {
        new CalibrationPacketIdentityContract{ProjectedCalSha256=CalibrationPacketIdentityContract.HashFile(cal),SourceCalSha256=new string('A',64),SampleSha256=CalibrationPacketIdentityContract.HashSamples(Wave()),
            Phase="ALL",TimingMode="FirstArrival",RangeIndex=8,SampleCount=4096,ChargePc=5000,SampleRateHz=125e6,CoefficientPcPerV=50000,
            IncidentSeconds=3.1280000000000004e-6,ReflectionSeconds=5.2e-6,IncidentPeakIndex=500,ReflectionPeakIndex=750}.WriteNew(cal);
    }
    static object Analyze(string p,int method)
    {
        object r=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest",true));Set(r,"ProjectPath",p);Set(r,"LocationFilePath",Path.Combine(p,"A_12.0kV_fixture.loc"));
        Set(r,"AlgorithmIndex",method);Set(r,"LocationDataId",42L);Set(r,"OscillationFrequencyHz",50d);Set(r,"ManualThresholdEnabled",true);Set(r,"ManualThreshold",100d);Set(r,"MaxPulses",10);Set(r,"AutomaticAcceptance",false);
        return Call(engine,"Analyze",r);
    }
    static bool Same(double[,] x,double[,] y){return x.GetLength(0)==y.GetLength(0)&&x.GetLength(1)==y.GetLength(1)&&Enumerable.Range(0,x.GetLength(0)).All(i=>Enumerable.Range(0,x.GetLength(1)).All(j=>x[i,j]==y[i,j]||Double.IsNaN(x[i,j])&&Double.IsNaN(y[i,j])));}
    static void Cases(string build,string qa)
    {
        Directory.CreateDirectory(qa);asm=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));scope=asm.GetType("HVX.PdReview.ReviewCalibrationInputScope",true);engine=asm.GetType("HVX.PdReview.ReviewAnalysisEngine",true);
        asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"prefs.json")));
        asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
        string p=Path.Combine(qa,"legacy-only"),cal=Project(p),meta=CalibrationPacketIdentityContract.SidecarPath(cal);string original=CalibrationPacketIdentityContract.HashFile(cal);
        Check(CalibrationPacketIdentityContract.ReadOptionalFile(cal)==null,"absent contract bypasses new canonical reader");
        object oldB=Analyze(p,7);Check((bool)Get(oldB,"Success"),"legacy B completes before adding optional contract");
        WriteContract(cal);byte[] metadata=File.ReadAllBytes(meta);var contract=CalibrationPacketIdentityContract.ReadOptionalFile(cal);
        Check(contract.IncidentSeconds==3.1280000000000004e-6,"pre-entry reads exact seconds, never integer catalog marker reconstruction");
        using(var input=(IDisposable)Call(scope,"Create",p,Path.Combine(p,"A_12.0kV_fixture.loc"))){Check(!(bool)Get(input,"IsProjected")&&(string)Get(input,"AlgorithmProjectPath")==p,"legal legacy sidecar does not replace B project path");
            File.AppendAllText(meta," ");Reject(()=>input.GetType().GetMethod("VerifySources",F).Invoke(input,null),"legacy-only sidecar mutation rejected after analysis binding");File.WriteAllBytes(meta,metadata);}
        object validB=Analyze(p,7);Check((bool)Get(validB,"Success")&&Same((double[,])Get(oldB,"Parameters"),(double[,])Get(validB,"Parameters")),"B exact matrix unchanged with valid identity; original parser/gates retained");
        foreach(string field in new[]{"RangeIndex","ProjectedCalSha256","SampleSha256","IncidentSeconds","ReflectionPeakIndex","Schema"})
        {
            File.WriteAllBytes(meta,metadata);var doc=XDocument.Load(meta);if(field=="RangeIndex")doc.Root.Element(field).Remove();else doc.Root.Element(field).Value=field=="Schema"?"bad":field=="IncidentSeconds"?"0":field=="ReflectionPeakIndex"?"4096":new string('0',64);doc.Save(meta);
            foreach(int method in new[]{5,7}){object result=Analyze(p,method);Check(!(bool)Get(result,"Success")&&((string)Get(result,"Status")).Contains("InvalidDataException"),"present bad "+field+" explicitly rejected before A/B method "+method);}
        }
        File.WriteAllBytes(meta,metadata);string text=File.ReadAllText(cal);File.WriteAllText(cal,text.Replace("NI_Calib_factor\t50000","NI_Calib_factor\t0"),new UTF8Encoding(false));
        foreach(int method in new[]{5,7})Check(!(bool)Get(Analyze(p,method),"Success"),"invalid catalog candidate cannot hide present sidecar method "+method);
        File.WriteAllText(cal,text,new UTF8Encoding(false));File.WriteAllText(meta,"<!DOCTYPE x [<!ENTITY x 'ALL'>]><CalibrationPacketIdentity>&x;</CalibrationPacketIdentity>");
        foreach(int method in new[]{5,7})Check(!(bool)Get(Analyze(p,method),"Success"),"DTD sidecar not ignored by method "+method);
        File.WriteAllBytes(meta,metadata);File.Delete(meta);Directory.CreateDirectory(meta);foreach(int method in new[]{5,7})Check(!(bool)Get(Analyze(p,method),"Success"),"sidecar directory is present invalid, not missing method "+method);Directory.Delete(meta);
        Check(CalibrationPacketIdentityContract.HashFile(cal)==original,"all source CAL edits confined to QA fixture and exactly restored");
        object restored=Analyze(p,7);Check((bool)Get(restored,"Success")&&Same((double[,])Get(oldB,"Parameters"),(double[,])Get(restored,"Parameters")),"absent-sidecar B full matrix restored exactly");
        // B also recognizes COMMON, whereas the historical catalog does not.
        // Filename phase must therefore select strict checking, not Valid/Phase
        // of that catalog (which would otherwise silently skip this contract).
        string common=Path.Combine(p,"COMMON_5nC.cal");File.Move(cal,common);WriteContract(common);
        Check(CalibrationPacketIdentityContract.ReadOptionalFile(common)!=null,"COMMON filename has the same explicit ALL contract identity");
        File.WriteAllText(CalibrationPacketIdentityContract.SidecarPath(common),"bad");
        foreach(int method in new[]{5,7})Check(!(bool)Get(Analyze(p,method),"Success"),"present COMMON sidecar never hidden by invalid historical catalog phase method "+method);
    }
    [STAThread]static int Main(string[] args){try{if(args.Length!=2)throw new ArgumentException("Pass isolated build and owned QA cases.");Cases(Path.GetFullPath(args[0]),Path.GetFullPath(args[1]));Console.WriteLine("ANALYSIS_CAL_PRESENT_SIDECAR_PASS="+checks+"; HARDWARE=0; NO_FULL=true");return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
