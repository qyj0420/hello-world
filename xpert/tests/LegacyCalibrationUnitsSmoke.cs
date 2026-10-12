using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

internal static class LegacyCalibrationUnitsSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Type binding,browser,recordType;static int checks;static string root;
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Near(double a,double b,string why){Check(Math.Abs(a-b)<=Math.Max(1e-14,Math.Abs(b)*1e-12),why+": "+a+" != "+b);}
    static object Candidate(){return binding.GetMethod("ReadCandidate",B).Invoke(null,new object[]{root,Path.Combine(root,"ALL_5nC.cal")});}
    static object Wave(){var r=Activator.CreateInstance(recordType);recordType.GetField("Path").SetValue(r,Path.Combine(root,"ALL_5nC.cal"));recordType.GetField("Kind").SetValue(r,"cal");return browser.GetMethod("LoadWaveform",new[]{recordType}).Invoke(null,new[]{r});}
    static string Cal(string rate="SR(N)\t8",string start="Start_point (us)\t0.16",string end="Final_point (us)\t0.56")
    {
        var h=new[]{"Date\t2026/08/26","Time\t19:48","Comment\t旧档编码","Cable length (m)\t405.3","V (m/usec)\t169.6","Cal.value (pC)\t5000",
            "Wideband\t0",start,end,rate,"NI_Calib_factor\t1845.1965311409","Filterstate\t0","Calib_Attenuation\t1"};
        return String.Join("\r\n",h)+"\r\n"+String.Join("\r\n",Enumerable.Range(0,96).Select(i=>(Math.Sin(i*.31)*.123).ToString("R",CultureInfo.InvariantCulture)))+"\r\n";
    }
    static void Put(string text,Encoding encoding=null){File.WriteAllText(Path.Combine(root,"ALL_5nC.cal"),text,encoding??new UTF8Encoding(false));}
    static void Valid(string label,double rate=125e6,double incident=.16e-6,double reflection=.56e-6)
    {
        var before=File.ReadAllBytes(Path.Combine(root,"ALL_5nC.cal"));var c=Candidate();var w=Wave();
        Near((double)Field(c,"SampleRateHz"),rate,label+" binding rate");Near((double)Field(w,"SampleRateHz"),rate,label+" browser rate");
        Near((double)Field(c,"HistoricalIncidentSeconds"),incident,label+" binding incident");Near((double)Field(w,"IncidentSeconds"),incident,label+" browser incident");
        Near((double)Field(c,"HistoricalReflectionSeconds"),reflection,label+" binding reflection");Near((double)Field(w,"ReflectionSeconds"),reflection,label+" browser reflection");
        Check((int)Field(c,"WaveformSampleCount")==96&&((double[])Field(w,"Y")).Length==96,label+" sample count unchanged");
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"ALL_5nC.cal"))),label+" original bytes unchanged");
    }
    static void Reject(Action act,string reason){bool rejected=false;try{act();}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidDataException||ex.InnerException is IOException;}Check(rejected,reason);}
    [STAThread] static int Main(string[] args)
    {
        try{
            string build=Path.GetFullPath(args[0]);root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"start\tQA\r\n");File.WriteAllText(Path.Combine(root,"CABLE.in"),LegacyCalibrationBindingSmoke.Geometry(false));
            var a=Assembly.LoadFrom(Path.Combine(build,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            binding=a.GetType("HVX.CalibrationStorage.LegacyCalibrationBinding",true);browser=a.GetType("HVX.Controls.BrowserReader",true);recordType=a.GetType("HVX.Controls.BrowserRecord",true);
            foreach(var enc in new Encoding[]{new UTF8Encoding(false),new UTF8Encoding(true),Encoding.Unicode,Encoding.BigEndianUnicode,Encoding.GetEncoding(936)}){Put(Cal(),enc);Valid("encoding "+enc.WebName);}
            foreach(string rate in new[]{"SR(N)\t125000000","SR\t8E-9","SR (ns):\t8","SR (s)\t8e-9","SR (Hz)\t125000000"}){Put(Cal(rate));Valid(rate);}
            foreach(string rate in new[]{"SR(N)\t125000004","SR (Hz)\t125000004","SR［Hz］\t125000004"}){
                Put(Cal(rate));Valid("exact historical correction "+rate);
                Check((double)Field(Candidate(),"RecordedSampleRateHz")==125000004,"recorded error remains identifiable "+rate);
            }
            foreach(double rate in new[]{125000003d,125000005d,125000004.25d,124999999d}){
                Put(Cal("SR (Hz)\t"+rate.ToString("R",CultureInfo.InvariantCulture)));Valid("neighbor rate remains exact "+rate,rate);
            }
            Put(Cal("SR(N)\t8","Start_point\t0.16","Final_point\t0.56"));Valid("unlabelled legacy us");
            Put(Cal("SR(N)\t8","Start_point (s)\t0.00000016","Final_point (s)\t0.00000056"));Valid("explicit seconds");
            Put(Cal("SR(N)\t8","Start_point (µs)\t0.16","Final_point (μs)\t0.56"));Valid("micro symbols");
            Put(Cal("SR（N）\t125000000","Start_point（s）\t0.00000016","Final_point（s）\t0.00000056"),Encoding.GetEncoding(936));Valid("legacy full-width parentheses in GBK");
            Put(Cal("SR［ns］\t8","Start_point［us］\t0.16","Final_point［us］\t0.56"));Valid("full-width square brackets");
            Put(Cal().Replace("\t",":\t"));Valid("colon headers");
            Put(Cal("Delta_x\t125000000").Replace("V (m/usec)","Velocity (m/usec)"));Valid("identified Delta_x Hz");
            Put(Cal("Delta_x\t125000004").Replace("V (m/usec)","Velocity (m/usec)"));Valid("identified Delta_x known correction");
            Put(Cal()+"\r\n\r\n");Valid("trailing blank allowed");
            Put(Cal("SR (Hz)\t8"));Reject(()=>Candidate(),"explicit 8Hz not guessed as 8ns");
            Put(Cal("SR(N)\tNaN").Replace("Comment\t旧档编码","Delta_x\t125000000").Replace("V (m/usec)","Velocity (m/usec)"));Reject(()=>Candidate(),"invalid present SR no Delta_x fallback");Reject(()=>Wave(),"browser invalid SR no fallback");
            Put(Cal().Replace("Comment\t旧档编码","SR\t125000000"));Reject(()=>Candidate(),"ambiguous SR rejected");Reject(()=>Wave(),"browser ambiguous SR rejected");
            Put(Cal().Replace("Comment\t旧档编码","Start_point\t0.16"));Reject(()=>Candidate(),"ambiguous time rejected");Reject(()=>Wave(),"browser ambiguous time rejected");
            Put(Cal("SR(kHz)\t125000"));Reject(()=>Candidate(),"unknown rate convention not invented");Reject(()=>Wave(),"browser unknown rate rejected");
            Put(Cal().Replace("Start_point (us)","Start_point (ms)"));Reject(()=>Candidate(),"unknown time unit rejected");Reject(()=>Wave(),"browser unknown time rejected");
            var lines=Cal().Split(new[]{"\r\n"},StringSplitOptions.None);lines[30]="";Put(String.Join("\r\n",lines));Reject(()=>Candidate(),"interior missing sample cannot shift timing");Reject(()=>Wave(),"browser interior missing sample cannot shift timing");
            Put(Cal().Replace("Comment\t旧档编码","SR(N)\t125000000"));Reject(()=>Candidate(),"duplicate rate field rejected");Reject(()=>Wave(),"browser duplicate rate field rejected");
            Put(Cal().Replace("Comment\t旧档编码","SR（N）\t125000000"));Reject(()=>Candidate(),"full-width duplicate rate rejected");Reject(()=>Wave(),"browser full-width duplicate rejected");
            Put(Cal());var c=Candidate();Check(binding.GetMethod("FindForProject",B).Invoke(null,new object[]{root,"A",true,"PICO-QA","ART-QA"})==null,"reading never invents operator binding");
            Check((string)Field(c,"SourcePhase")=="ALL"&&(string)Field(c,"Level")=="5nC","ALL_5nC filename remains explicit common-phase calibration");
            var cable=Path.Combine(root,"CABLE.in");var geometry=File.ReadAllBytes(cable);
            File.WriteAllText(cable,LegacyCalibrationBindingSmoke.Geometry(false).Replace("0\tA\t","0\tNOTAPHASE\t"));
            Reject(()=>binding.GetMethod("CurrentLengthForProject",B).Invoke(null,new object[]{root,"ALL"}),"unknown geometry is not inferred from ALL calibration filename");
            File.WriteAllBytes(cable,geometry);c=Candidate();
            var b=binding.GetMethod("Confirm",B).Invoke(null,new object[]{root,c,"A","PICO-QA","ART-QA",true,true,"Offline fixture explicit applicability"});
            Check(binding.GetMethod("LoadAndValidate",B).Invoke(null,new object[]{root,Field(b,"BindingPath"),"PICO-QA","ART-QA",true})!=null,"normalized legacy binding reloads");
            Console.WriteLine("LEGACY_CALIBRATION_UNITS_PASS="+checks+"; HARDWARE_CALLS=0; SYNTHETIC_PROJECTS_ONLY=TRUE");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
