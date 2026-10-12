using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Data;

internal static class LocationCycleUpToSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type control;static Assembly module;
    static void Check(bool value,string why){++checks;if(!value)throw new Exception(why);}
    static double[] Voltage(int cycles){return Enumerable.Range(0,(int)Math.Ceiling((cycles+.35)*40)).Select(i=>10*Math.Cos(2*Math.PI*500*i/20000)).ToArray();}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static bool Await(Task<bool> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(1);}Check(t.IsCompleted,"bounded managed save");return t.GetAwaiter().GetResult();}
    static void Pure()
    {
        foreach(int available in new[]{1,2,4,5,6})foreach(int maximum in new[]{1,2,5}){
            var v=Voltage(available);var before=(double[])v.Clone();int expected=Math.Min(available,maximum);
            var actual=OscillationCycleWindow.FindUpTo(v,20000,maximum,v.Length,20000,0,10);
            Check(actual.Success&&actual.CycleCount==expected,"actual complete cycles, capped at maximum "+available+"/"+maximum);
            var strict=OscillationCycleWindow.Find(v,20000,expected,10);
            Check(actual.CrossingSeconds.SequenceEqual(strict.CrossingSeconds),"strict measured crossings unchanged");
            LocationCycleReference r;var charge=Enumerable.Range(0,v.Length).Select(i=>i*.031).ToArray();var saved=actual.Crop(charge,20000,0,out r);
            Check(saved.All(x=>charge.Contains(x))&&v.SequenceEqual(before),"no resample/pad/raw mutation");
            Check(r.CycleCount==expected&&r.HeaderLine().Contains("\"cycleCount\":"+expected),"actual count written to phase header");
            Check(LocationCycleReference.ParseHeader(new[]{r.HeaderLine()},saved.Length,20000).CycleCount==expected,"actual count parsed");
            bool rejected=false;try{LocationCycleReference.ParseHeader(new[]{r.HeaderLine().Replace("\"cycleCount\":"+expected,"\"cycleCount\":99")},saved.Length,20000);}catch(InvalidDataException){rejected=true;}Check(rejected,"fabricated count rejected");
        }
        var full=Voltage(6);const double picoRate=125000;int shortPd=(int)Math.Ceiling(.0047*picoRate);
        var bounded=OscillationCycleWindow.FindUpTo(full,20000,5,shortPd,picoRate,0,10);
        Check(bounded.Success&&bounded.CycleCount==2,"independent shorter Pico bounds actual cycles");
        Check(OscillationCycleWindow.FindUpTo(Voltage(1),20000,5,50000,picoRate,0,10).CycleCount==1,"shorter ART bounds actual cycles");
        foreach(int count in new[]{16,80})Check(!OscillationCycleWindow.FindUpTo(full,20000,5,count,picoRate,0,10).Success,"no complete shared cycle not accepted");
        Check(!OscillationCycleWindow.FindUpTo(full,20000,5,50000,picoRate,.001,10).Success,"first crossing not covered by PD is rejected");
        Check(!OscillationCycleWindow.FindUpTo(new double[200],20000,5,200,20000,0,10).Success,"no voltage crossing rejected");
        Check(!OscillationCycleWindow.FindUpTo(new[]{1d,Double.NaN,-1},20000,5,100,20000,0,10).Success,"nonfinite voltage rejected");
        foreach(double rate in new[]{0d,Double.NaN,Double.PositiveInfinity})Check(!OscillationCycleWindow.FindUpTo(full,20000,5,200,rate,0,10).Success,"invalid Pico timebase rejected");
        foreach(int n in new[]{0,6,100}){bool bad=false;try{OscillationCycleWindow.FindUpTo(full,20000,n,full.Length,20000,0,10);}catch(ArgumentOutOfRangeException){bad=true;}Check(bad,"maximum1..5 enforced");}
        Check(!OscillationCycleWindow.Find(Voltage(2),20000,5,10).Success,"strict requested-five helper remains strict");
    }
    static Control Make(string qa){Directory.CreateDirectory(qa);var c=(Control)Activator.CreateInstance(control);Set(c,"ProjectPath",qa);Set(c,"SimulationMode",true);Set(c,"MaximumPeakVoltageKv",50d);Set(c,"PlanVoltagesArePeak",true);Set(c,"LocationSaveCycleCount",5);Set(c,"FrequencyReminderEnabled",false);Set(c,"AutomaticPostProcessEnabled",true);Set(c,"BackgroundPressureAnalysisEnabled",true);Call(c,"SetPressurePlan",new[]{1d},new[]{2},new[]{10d});Call(c,"SetPressureProgramMode","PLAN");var h=c.Handle;Call(c,"RequestPressure");return c;}
    static void Install(Control c,double[] voltage,double[] charge){Call(c,"SetAcquisitionWaveforms",voltage,charge,20000d,new Dictionary<string,object>{{"locationAcquisitionId",Get(c,"LocationAcquisitionId")}});}
    static void Managed(string qa)
    {
        foreach(int available in new[]{1,2,4,5,6})using(var c=Make(Path.Combine(qa,"n"+available))){
            var v=Voltage(available);var q=Enumerable.Range(0,v.Length).Select(i=>i*.25).ToArray();Install(c,v,q);int expected=Math.Min(5,available);
            Check((int)Get(c,"LocationActualCycleCount")==expected,"formal entry chooses actual "+available);
            Call(c,"SetPressureState","COMPLETE",0d);Check((bool)Call(c,"ReportAcquisitionCompleted",false),"complete short capture accepted");
            Check((string)Get(c,"PostProcessStateCode")=="SAVE_REQUESTED"&&!(bool)Get(c,"DataSaved"),"does not claim persisted before save ACK");
            string path=Path.Combine((string)Get(c,"ProjectPath"),"A_10.00kV_2026-10-10-08-30-00.loc");
            Check(Await((Task<bool>)Call(c,"SaveLocationCycleDataAsync",Get(c,"LocationAcquisitionId"),path,q,20000d,0d,170d)),"real managed LOC persistence succeeds: "+Get(c,"LastPostProcessError"));
            var lines=File.ReadAllLines(path);var r=LocationCycleReference.ParseHeader(lines,lines.Length-11,20000);
            Check(r.CycleCount==expected&&lines[9].Contains("\"requestedMaximumCycles\":5")&&lines[9].Contains("\"actualCycleCount\":"+expected),"formal record states requested maximum and actual count");
            Check((bool)Call(c,"CompleteSave",true,"")&&(bool)Get(c,"DataSaved"),"ACK only after actual file publication");
            Check((string)Get(c,"PostProcessStateCode")=="COMPLETE","valid complete short record can reach post-save terminal");
        }
        using(var c=Make(Path.Combine(qa,"partial"))){
            var v=Voltage(1).Take(30).ToArray();Install(c,v,new double[v.Length]);Call(c,"SetPressureState","COMPLETE",0d);
            Check(!(bool)Call(c,"ReportAcquisitionCompleted",false),"partial cycle not formal completed round");
            Check((string)Get(c,"PostProcessStateCode")=="ACQUISITION_INVALID"&&!(bool)Get(c,"DataSaved"),"partial remains invalid and unsaved");
            Check(!(bool)Call(c,"CompleteSave",true,"")&&!(bool)Call(c,"MayContinueIssuedPressurePlanNoLock"),"partial cannot ACK or advance automatic plan");
            Check(!Directory.GetFiles((string)Get(c,"ProjectPath"),"*.loc").Any(),"partial produces no false valid LOC");
        }
        using(var c=Make(Path.Combine(qa,"clocks"))){
            var v=Voltage(6);var q=new double[188];Install(c,v,q);
            var capture=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.BenchCapture",true));Field(capture,"PicoRateHz",40000d);
            var result=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult",true));Field(result,"Capture",capture);Field(result,"ChargePc",Field(c,"lastDischargePc"));
            Field(c,"lastPressureCapture",result);Field(c,"lastPressureAcquisitionId",Get(c,"LocationAcquisitionId"));Call(c,"EvaluateLocationCycleWindow");
            Check((int)Get(c,"LocationActualCycleCount")==2,"actual capture path uses Pico clock, not ART clock");
        }
        using(var c=(Control)Activator.CreateInstance(control))foreach(int n in new[]{0,6,100}){bool bad=false;try{Set(c,"LocationSaveCycleCount",n);}catch(TargetInvocationException ex){bad=ex.InnerException is ArgumentOutOfRangeException;}Check(bad,"public module maximum limit "+n);}
    }
    [STAThread]static int Main(string[] args){try{Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Pure();if(args.Length==2){module=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));control=module.GetType("HVX.Controls.PressureTestControl",true);Managed(Path.GetFullPath(args[1]));}Console.WriteLine("PASS "+checks+" up-to actual complete cycle checks; no SDK/worker/product Main/user project writes.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
