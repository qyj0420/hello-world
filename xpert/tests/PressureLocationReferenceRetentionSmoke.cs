using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class PressureLocationReferenceRetentionSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type type,retention;static int checks;static string root;
    static void Check(bool ok,string why){++checks;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object F(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void F(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Await(Task task){int waited=0;while(!task.IsCompleted&&waited++<15000){Application.DoEvents();Thread.Sleep(1);}Check(task.IsCompleted,"bounded managed task");task.GetAwaiter().GetResult();}
    static void NewText(string path,IEnumerable<string> lines){using(var s=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var w=new StreamWriter(s))foreach(var l in lines)w.WriteLine(l);}
    static Control Make(int requested,out object result,int voltageCount,int pdCount)
    {
        string project=Path.Combine(root,Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(project);
        var c=(Control)Activator.CreateInstance(type);Set(c,"ProjectPath",project);Set(c,"SimulationMode",true);Set(c,"PlanVoltagesArePeak",true);
        Set(c,"MaximumPeakVoltageKv",50d);Set(c,"LocationSaveCycleCount",requested);Set(c,"FrequencyReminderEnabled",false);
        Set(c,"AutomaticPostProcessEnabled",true);Set(c,"BackgroundPressureAnalysisEnabled",true);
        Call(c,"SetPressurePlan",new[]{1d},new[]{2},new[]{10d});Call(c,"SetPressureProgramMode","PLAN");var handle=c.Handle;Call(c,"RequestPressure");
        double[] voltage=Enumerable.Range(0,voltageCount).Select(i=>10*Math.Cos(2*Math.PI*500*i/20000)).ToArray();
        double[] charge=Enumerable.Range(0,pdCount).Select(i=>i+.125).ToArray();
        Call(c,"SetAcquisitionWaveforms",voltage,charge,20000d,new Dictionary<string,object>{{"locationAcquisitionId",Get(c,"LocationAcquisitionId")}});
        object capture=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.BenchCapture",true));
        F(capture,"PicoRateHz",20000d);F(capture,"ArtRateHz",20000d);F(capture,"PdAdc",new short[pdCount]);F(capture,"VoltageRaw",voltage);
        result=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult",true));
        F(result,"Capture",capture);F(result,"ChargePc",F(c,"lastDischargePc"));F(result,"VoltageKv",F(c,"lastVoltageKv"));
        F(result,"RawSha256",new string('A',64));
        string dir=Path.Combine(project,"Acquisition","Pressure","round");Directory.CreateDirectory(dir);F(result,"Directory",dir);
        NewText(Path.Combine(dir,"capture.bin"),new[]{"managed retirement predicate fixture; not a native capture"});
        NewText(Path.Combine(dir,"settings.xml"),new[]{"<AuditFixture>No SDK</AuditFixture>"});
        using(var stream=File.OpenRead(Path.Combine(dir,"capture.bin")))using(var sha=System.Security.Cryptography.SHA256.Create())
            F(result,"RawSha256",BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""));
        F(c,"lastPressureCapture",result);F(c,"lastPressureAcquisitionId",Get(c,"LocationAcquisitionId"));Call(c,"EvaluateLocationCycleWindow");
        return c;
    }
    static string Marker(object result){return Path.Combine((string)F(result,"Directory"),"location-reference-only.json");}
    static object Context(Control c,object result){return Call(c,"CaptureLocationRawReferenceContext",result,Get(c,"LocationAcquisitionId"));}
    static void Retire(Control c,object result,object context)
    {
        string project=(string)Get(c,"ProjectPath"),loc=Path.Combine(project,"A_10.00kV_2026-10-10-09-00-00.loc");
        Await((Task)Call(c,"SaveLocationCycleDataAsync",Get(c,"LocationAcquisitionId"),loc,F(c,"lastDischargePc"),20000d,0d,170d));
        Check(File.Exists(loc),"formal complete LOC exists before retention decision");
        string dem=Path.ChangeExtension(loc,".dem");var headers=Enumerable.Range(0,16).Select(i=>"audit\t"+i).ToArray();headers[7]="Number of Samples:\t16";
        NewText(dem,headers.Concat(Enumerable.Repeat("0\t0",16)));
        Call(c,"RetirePublishedRawCapture",result,loc,dem,context);
        Check(File.Exists(loc)&&File.Exists(dem),"formal paired files unchanged");
    }
    static void Cases()
    {
        foreach(int count in new[]{106,186}){
            object result;using(var c=Make(5,out result,count,count)){
                object context=Context(c,result);Check((bool)F(context,"Retain")&& (int)F(context,"TailSamples")==15,"only actual short count with common real tail retained");
                Retire(c,result,context);
                Check(File.Exists(Path.Combine((string)F(result,"Directory"),"capture.bin"))&&File.Exists(Marker(result)),"raw retained and independent marker published");
                var m=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Marker(result)));
                Check(Object.Equals(m["referenceOnly"],true)&&Object.Equals(m["validLocationRecord"],false)&&Object.Equals(m["grantsSaveAck"],false)&&Object.Equals(m["includedInLocationCounts"],false)&&Object.Equals(m["tailPhaseKnown"],false),"reference marker cannot grant validity/ACK/count/phase");
                Check(Convert.ToInt32(m["actualCompleteCycles"])==(count==106?2:4)&&Convert.ToInt32(m["requestedMaximumCycles"])==5,"real complete count and maximum retained");
                string before=File.ReadAllText(Marker(result));Call(c,"PublishLocationRawReference",context);
                Check(File.ReadAllText(Marker(result))==before,"same round does not rewrite/duplicate marker");
                Check(!(bool)Get(c,"DataSaved"),"reference publication alone is not save ACK");
            }
        }
        foreach(var example in new[]{new[]{2,106,106},new[]{5,250,250},new[]{5,93,91}}){
            object result;using(var c=Make(example[0],out result,example[1],example[2])){
                object context=Context(c,result);Check(!(bool)F(context,"Retain"),"requested maximum reached or no real common tail does not keep raw forever");
                Retire(c,result,context);
                Check(!File.Exists(Path.Combine((string)F(result,"Directory"),"capture.bin"))&&!File.Exists(Marker(result)),"intentional max crop/no-tail keeps prior retirement");
            }
        }
        object invalid;using(var c=Make(5,out invalid,30,30)){
            Call(c,"SetPressureState","COMPLETE",0d);Check(!(bool)Call(c,"ReportAcquisitionCompleted",false),"no full cycle remains invalid");
            var task=F(c,"locationReferenceWriteTask") as Task;Check(task!=null,"owned invalid raw schedules metadata only");Await(task);
            Check(File.Exists(Marker(invalid))&&File.Exists(Path.Combine((string)F(invalid,"Directory"),"capture.bin")),"invalid raw retained and reference-only marked");
            Check((string)Get(c,"PostProcessStateCode")=="ACQUISITION_INVALID"&&!(bool)Get(c,"DataSaved"),"invalid semantics not promoted");
            Check(!(bool)Call(c,"CompleteSave",true,"")&&!(bool)Call(c,"MayContinueIssuedPressurePlanNoLock"),"reference cannot ACK or advance automatic plan");
            Check(Directory.GetFiles((string)Get(c,"ProjectPath"),"*.loc").Length==0,"invalid creates no formal LOC");
            var m=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Marker(invalid)));
            Check(Convert.ToInt32(m["actualCompleteCycles"])==0&&m["lastCompleteCrossingSeconds"]==null&&Object.Equals(m["tailPhaseKnown"],false),"no fabricated crossing/phase for partial");
        }
        object stale;using(var c=Make(5,out stale,106,106)){
            object context=Context(c,stale);F(c,"cycleAcquisitionId",Guid.NewGuid().ToString("N"));
            bool failed=false;try{Call(c,"PublishLocationRawReference",context);}catch(TargetInvocationException e){failed=e.InnerException is InvalidOperationException;}
            Check(failed&&!File.Exists(Marker(stale))&&File.Exists(Path.Combine((string)F(stale,"Directory"),"capture.bin")),"stale owner cannot publish or delete raw");
        }
        object race;using(var c=Make(5,out race,106,106)){
            object context=Context(c,race);int calls=0;bool failed=false;
            try{retention.GetMethod("Publish",B).Invoke(null,new object[]{context,new Func<bool>(()=>++calls==1),null});}
            catch(TargetInvocationException e){failed=e.InnerException is InvalidOperationException;}
            Check(failed&&!File.Exists(Marker(race))&&Directory.GetFiles((string)F(race,"Directory"),"*.tmp").Length==0,"owner revoked after pending write cannot publish; only own temporary cleaned");
            Check(File.Exists(Path.Combine((string)F(race,"Directory"),"capture.bin")),"late rejection retains original");
        }
        object outside;using(var c=Make(5,out outside,106,106)){
            object context=Context(c,outside);F(context,"Directory",(string)Get(c,"ProjectPath"));bool failed=false;
            try{retention.GetMethod("Publish",B).Invoke(null,new object[]{context,new Func<bool>(()=>true),null});}
            catch(TargetInvocationException e){failed=e.InnerException is System.IO.InvalidDataException;}
            Check(failed&&!File.Exists(Path.Combine((string)Get(c,"ProjectPath"),"location-reference-only.json")),"outside acquisition boundary rejected");
        }
        object bounds;using(var c=Make(5,out bounds,106,106)){
            object capture=F(bounds,"Capture");F(capture,"PicoRateHz",Double.NaN);bool failed=false;
            try{Context(c,bounds);}catch(TargetInvocationException e){failed=e.InnerException is InvalidDataException;}
            Check(failed,"invalid actual timebase cannot become a retained valid phase context");
        }
    }
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        root=Path.GetFullPath(args[1]);Directory.CreateDirectory(root);
        module=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        type=module.GetType("HVX.Controls.PressureTestControl",true);retention=module.GetType("HVX.Controls.PressureLocationReferenceRetention",true);
        Cases();Console.WriteLine("PASS "+checks+" raw reference retention/intentional maximum retirement/invalid no ACK/stale owner checks; only managed QA fixtures, no SDK/worker/product Main/user projects.");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
