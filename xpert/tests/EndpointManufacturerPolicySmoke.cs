using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
class EndpointManufacturerPolicySmoke
{
    static int checks;
    const BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool v,string m){if(!v)throw new Exception(m);checks++;}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethod(name,Hidden).Invoke(o,args);}
    static object Field(object o,string name){return o.GetType().GetField(name,Hidden).GetValue(o);}
    static void Put(object o,string name,object v){o.GetType().GetField(name,Hidden).SetValue(o,v);}
    static void CheckMath(Assembly a)
    {
        Type shape=a.GetType("HVX.SignalEvidence.RawEndpointShape",true),policy=a.GetType("HVX.SignalEvidence.RawEndpointPolicy",true);
        Type math=a.GetType("HVX.SignalEvidence.RawEndpointMath",true);
        var parse=policy.GetMethod("Parse",BindingFlags.Static|BindingFlags.NonPublic);
        var decide=math.GetMethod("Decide",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{shape,shape,shape,policy},null);
        object cal=Activator.CreateInstance(shape,true);Put(cal,"Valid",true);Put(cal,"Main",3d);Put(cal,"Opposite",1d);Put(cal,"FwhmNs",10d);
        foreach(bool firstAnd in new[]{false,true})foreach(bool secondAnd in new[]{false,true})
        {
            string config="2;1.5;"+(firstAnd?"AND":"OR")+";"+(secondAnd?"AND":"OR");
            object p=parse.Invoke(null,new object[]{config});
            for(int mask=0;mask<8;mask++)
            {
                bool split=(mask&1)!=0,width=(mask&2)!=0,ratio=(mask&4)!=0;
                object incident=Activator.CreateInstance(shape,true),reflection=Activator.CreateInstance(shape,true);
                Put(incident,"Valid",true);Put(incident,"FwhmNs",width?30d:10d);Put(incident,"Main",ratio?1d:3d);Put(incident,"Opposite",1d);
                Put(reflection,"Valid",true);Put(reflection,"Split",split);
                object result=decide.Invoke(null,new[]{incident,reflection,cal,p});
                bool expected=firstAnd?(split&&width):(split||width);
                expected=secondAnd?(expected&&ratio):(expected||ratio);
                Check(((string)Field(result,"Direction")=="Far")==expected,"predicate truth table: "+config+" / "+mask);
                Put(incident,"FwhmNs",Double.NaN);
                result=decide.Invoke(null,new[]{incident,reflection,cal,p});
                Check((string)Field(result,"Direction")=="Indeterminate","missing evidence cannot be overridden by OR");
            }
        }
        foreach(string invalid in new[]{"NaN;1.5;OR;OR","0;1.5;OR;OR","2;Infinity;OR;OR","2;1.5;X;OR"})
        {
            bool rejected=false;try{parse.Invoke(null,new object[]{invalid});}catch(TargetInvocationException e){rejected=e.InnerException is ArgumentException;}
            Check(rejected,"invalid policy rejected");
        }
        object probe=Activator.CreateInstance(shape,true),echo=Activator.CreateInstance(shape,true);
        Put(probe,"Valid",true);Put(probe,"FwhmNs",20d);Put(probe,"Main",3d);Put(probe,"Opposite",1d);
        Put(echo,"Valid",true);
        object baseline=parse.Invoke(null,new object[]{"2;1.5;OR;OR"});
        object stricter=parse.Invoke(null,new object[]{"3;1.5;OR;OR"});
        Check((string)Field(decide.Invoke(null,new[]{probe,echo,cal,baseline}),"Direction")=="Far","width inclusive boundary");
        Check((string)Field(decide.Invoke(null,new[]{probe,echo,cal,stricter}),"Direction")!="Far","configured width actually used");
        Put(probe,"FwhmNs",10d);Put(probe,"Main",2d);
        Check((string)Field(decide.Invoke(null,new[]{probe,echo,cal,baseline}),"Direction")=="Far","ratio inclusive boundary");
        stricter=parse.Invoke(null,new object[]{"2;2;OR;OR"});
        Check((string)Field(decide.Invoke(null,new[]{probe,echo,cal,stricter}),"Direction")!="Far","configured ratio actually used");
        Check((string)Field(decide.Invoke(null,new[]{probe,echo,null,baseline}),"Direction")=="Indeterminate","missing calibration");
        Check((string)Field(decide.Invoke(null,new[]{probe,null,cal,baseline}),"Direction")=="Indeterminate","missing reflection");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Directory.CreateDirectory(args[1]);
            var settings=new AppSettings();settings.TaskMinus3.EndpointPolicy="3;2;AND;OR";
            string path=Path.Combine(args[1],"endpoint-settings.xml");settings.Save(path);
            Check(AppSettings.Load(path).TaskMinus3.EndpointPolicy==settings.TaskMinus3.EndpointPolicy,"XML roundtrip");
            using(var form=new SettingsForm(settings))
            {
                Check(Field(form,"endpointWidth")==null,"no manufacturer editors constructed before authorization");
                Check(Field(form,"endpointWidth")==null,"failed authorization has no editors");
                Check(((AppSettings)Call(form,"CaptureValues")).TaskMinus3.EndpointPolicy==settings.TaskMinus3.EndpointPolicy,"ordinary save preserves manufacturer policy");
                bool blocked=false;try{Call(form,"CaptureEndpointPolicy");}catch(TargetInvocationException e){blocked=e.InnerException is InvalidOperationException;}
                Check(blocked,"manufacturer capture explicitly guarded");
            }
            string module=Path.Combine(args[0],"modules","DataAnalysis");
            var review=Assembly.LoadFrom(Path.Combine(module,"HVX.PdReview.V1.0.dll"));
            CheckMath(review);
            var algorithm=Assembly.LoadFrom(Path.Combine(module,"fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
            CheckMath(algorithm);
            foreach(string stem in new[]{"Project","Sixth","Seventh"})
            {
                object analyzer=Activator.CreateInstance(algorithm.GetType("HVX.Task17.Location."+stem+"LocationAnalyzer",true));
                var prop=analyzer.GetType().GetProperty("EndpointPolicy");
                prop.SetValue(analyzer,"3;2;AND;OR",null);
                Check((string)prop.GetValue(analyzer,null)=="3;2;AND;OR","algorithm boundary "+stem);
            }
            Console.WriteLine("PASS endpoint manufacturer policy: "+checks+" checks");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
