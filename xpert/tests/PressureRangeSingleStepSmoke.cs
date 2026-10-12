using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

internal static class PressureRangeSingleStepSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static Assembly a;static int checks;
    static Type T(string name){return a.GetType(name,true);}
    static object Call(string type,string method,params object[] args){return T(type).GetMethod(method,B).Invoke(null,args);}
    static void Put(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Get(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static string Xml(object value){using(var text=new StringWriter()){new XmlSerializer(value.GetType()).Serialize(text,value);return text.ToString();}}
    static object Quality(int next){var q=Activator.CreateInstance(T("Xiongxin.ZeroBench.LocationRangeResult"));Put(q,"WindowValid",true);Put(q,"RecommendedIndex",next);Put(q,"Change",true);return q;}
    static object Capture(out object q)
    {
        var c=Activator.CreateInstance(T("Xiongxin.ZeroBench.BenchCapture"));
        var s=Activator.CreateInstance(T("Xiongxin.ZeroBench.BenchSettings"));Put(s,"RealHardware",true);Put(c,"Settings",s);
        Put(c,"Simulation",false);Put(c,"MaximumAdc",32767);Put(c,"Overflow",true);
        var raw=new short[100];raw[2]=32767;raw[90]=-32767;Put(c,"PdAdc",raw);
        q=Quality(2);Put(q,"FactoryCycleWindow",true);Put(q,"OutsideCycleClippingExcluded",true);
        Put(q,"WindowStartSample",10);Put(q,"WindowEndExclusive",60);Put(q,"LocationSampleCount",50);Put(q,"FullSampleCount",100);
        Put(q,"FullClippedSamples",2);Put(q,"OutsideClippedSamples",2);Put(q,"DriverOverflow",true);
        return c;
    }
    static void Run()
    {
        var ranges=(double[])T("Xiongxin.ZeroBench.BenchSettings").GetField("Ranges",B).GetValue(null);
        for(int current=0;current<ranges.Length;current++)for(int goal=0;goal<ranges.Length;goal++)
        {
            int next=(int)Call("HVX.Controls.PressureRangeSingleStep","Next",current,goal,ranges.Length);
            Check(next==current+Math.Sign(goal-current),"all range pairs have exactly one adjacent step");
            var q=Quality(goal);string before=Xml(q);
            var bounded=Call("HVX.Controls.PressureChargeRangeFloor","Bound",q,current,100/ranges[0]);
            Check((int)Get(bounded,"RecommendedIndex")==next,"production charge-floor adapter obeys step at "+current+" / "+goal);
            Check(Xml(q)==before,"original amplitude/quality recommendation remains immutable");
        }
        var exact=new double[]{100,200,500,1000};
        Check((int)Call("HVX.Controls.PressureOverflowUpscale","Next",exact,0,new HashSet<int>(),0)==1,"overflow one step");
        exact[1]=Double.NaN;
        Check((int)Call("HVX.Controls.PressureOverflowUpscale","Next",exact,0,new HashSet<int>(),0)==-1,"unavailable adjacent range never skipped");
        exact[1]=200;
        Check((int)Call("HVX.Controls.PressureOverflowUpscale","Next",exact,0,new HashSet<int>{1},0)==-1,"visited adjacent range never skipped");
        Check((int)Call("HVX.Controls.PressureOverflowUpscale","Next",exact,3,new HashSet<int>(),0)==-1,"maximum no next");
        object window;var c=Capture(out window);string qualityBefore=Xml(window);
        Put(window,"EvaluationCycles",5);
        using(var reader=new StringReader(Xml(window)))
        {
            var restored=new XmlSerializer(window.GetType()).Deserialize(reader);
            Check((bool)Get(restored,"FactoryCycleWindow")&&(bool)Get(restored,"OutsideCycleClippingExcluded")&&(int)Get(restored,"EvaluationCycles")==5,"producer window evidence survives consumer XML round trip");
        }
        qualityBefore=Xml(window);
        Check((bool)Call("HVX.Controls.PressureOverflowUpscale","HasActualOverflow",c,window),"full overflow evidence retained");
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","HasWindowOverflow",c,window),"outside-only does not cause automatic upscale");
        Check((bool)Call("HVX.Controls.PressureOverflowUpscale","ProvenOutsideCycleClipping",c,window),"known outside clipping classified separately");
        Check(Xml(window)==qualityBefore,"classification never erases overflow flags");
        ((short[])Get(c,"PdAdc"))[30]=-32767;Put(window,"LocationClippedSamples",1);
        Check((bool)Call("HVX.Controls.PressureOverflowUpscale","HasWindowOverflow",c,window),"negative in-window clipping triggers adjacent upscale");
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","ProvenOutsideCycleClipping",c,window),"inside clipping never excluded");
        Put(window,"WindowValid",false);
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","HasWindowOverflow",c,window),"invalid window cannot drive an automatic retry");
        c=Capture(out window);Put(window,"FactoryCycleWindow",false);
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","HasWindowOverflow",c,window),"no nonzero cycle evidence cannot authorize retry");
        c=Capture(out window);Put(window,"WindowEndExclusive",101);
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","HasWindowOverflow",c,window),"out-of-bounds cannot authorize retry");
        c=Capture(out window);Put(window,"OutsideClippedSamples",1);
        Check(!(bool)Call("HVX.Controls.PressureOverflowUpscale","ProvenOutsideCycleClipping",c,window),"mismatched outside counts not excluded");
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            string dir=Path.Combine(Path.GetFullPath(args[0]),"modules","PressureTest");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string f=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");return File.Exists(f)?Assembly.LoadFrom(f):null;};
            a=Assembly.LoadFrom(Path.Combine(dir,"Xiongxin.OWTS.PressureTest.dll"));Run();
            string worker=Path.Combine(Path.GetFullPath(args[0]),"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe");
            if(File.Exists(worker)){
                var w=Assembly.LoadFrom(worker);var qt=w.GetType("Xiongxin.ZeroBench.LocationRangeResult",true);
                var next=w.GetType("Xiongxin.FactoryTest.FactoryPressureSequence",true).GetMethod("NextRange",B);
                for(int current=0;current<11;current++)for(int goal=0;goal<11;goal++){
                    var q=Activator.CreateInstance(qt);Put(q,"WindowValid",true);Put(q,"RecommendedIndex",goal);
                    Check((int)next.Invoke(null,new object[]{current,q})==current+Math.Sign(goal-current),"worker also changes one adjacent range without running entry/SDK");
                }
            }
            Console.WriteLine("RANGE_SINGLE_STEP_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
