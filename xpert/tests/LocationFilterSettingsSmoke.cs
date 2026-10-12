using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;

// Private, offline QA. Only test-owned fixtures and constructors of controls.
// It does not start the product, a worker, device discovery or an SDK entry.
static class LocationFilterSettingsSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool yes,string name){checks++;if(!yes)throw new InvalidOperationException(name);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,All);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,All).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static void Reject(Action action,Type expected,string name){try{action();throw new InvalidOperationException("not rejected: "+name);}catch(TargetInvocationException e){Check(expected.IsInstanceOfType(e.InnerException),name+": "+e.InnerException.GetType().Name);}}
    static bool Valid(object value){object[] a={null};return (bool)value.GetType().GetMethod("TryValidate",All).Invoke(value,a);}
    static IEnumerable<Control> Tree(Control c){yield return c;foreach(Control x in c.Controls)foreach(var y in Tree(x))yield return y;}
    static Control Caption(System.Collections.IDictionary captions,string text){foreach(System.Collections.DictionaryEntry item in captions)if((string)item.Value==text)return (Control)item.Key;throw new InvalidOperationException("Caption not found: "+text);}
    static void Apply(Type helper,Control c,string property,string value,string project){Call(helper,"Apply",c,property,value,project);}

    public sealed class Probe:Control
    {
        string algorithm="A",pressure="A";
        public int AnalysisWrites,PressureWrites;
        public string LocationFilterAlgorithmId{get{return algorithm;}set{algorithm=value;AnalysisWrites++;}}
        public string StatisticsLocationFilterAlgorithmId{get{return pressure;}set{pressure=value;PressureWrites++;}}
        public string LoadedAnalysisId{get;set;}="";
        public int SourceRowCount{get;set;}
        public bool DataSaved{get;set;}
        public bool IsAnalysisRunning{get;set;}
        public bool IsBusy{get;set;}
        public bool HasActiveHardwareSession{get;set;}
        public bool ActionPending{get;set;}
    }

    static void Configuration(Assembly host,string qa)
    {
        var type=host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true);
        Func<object> fresh=()=>{var s=Activator.CreateInstance(type);Set(s,"DataRoot",qa);Set(Get(s,"Hardware"),"Enabled",false);return s;};
        var settings=fresh();Check((string)Get(settings,"LocationFilterAlgorithmId")=="A","new settings default to original filter A");
        string path=Path.Combine(qa,"settings.xml");Call(settings,"Save",path);
        var xml=new XmlDocument();xml.Load(path);XmlNode node=xml.DocumentElement.SelectSingleNode("LocationFilterAlgorithmId");
        Check(node!=null&&node.InnerText=="A","XML contains distinct filter identity");xml.DocumentElement.RemoveChild(node);xml.Save(path);
        var old=Call(type,"Load",path);Check((string)Get(old,"LocationFilterAlgorithmId")=="A"&&Valid(old),"old XML missing field uses constructor A");
        foreach(string id in new[]{"A","B"}){
            Set(settings,"LocationFilterAlgorithmId",id);Call(settings,"Save",path);var loaded=Call(type,"Load",path);
            Check((string)Get(loaded,"LocationFilterAlgorithmId")==id&&Valid(loaded),id+" survives real atomic XML save/load");
            var clone=Call(loaded,"Clone");Check((string)Get(clone,"LocationFilterAlgorithmId")==id,id+" survives clone");
            Set(clone,"LocationFilterAlgorithmId",id=="A"?"B":"A");Check((string)Get(loaded,"LocationFilterAlgorithmId")==id,"clone does not mutate original");
        }
        Set(settings,"LocationFilterAlgorithmId"," b ");Call(settings,"Save",path);Check((string)Get(settings,"LocationFilterAlgorithmId")=="B","explicit normalize trims and canonicalizes B");
        foreach(string bad in new[]{"C","",null}){
            Set(settings,"LocationFilterAlgorithmId",bad);Check(!Valid(settings),"invalid selector rejected: "+bad);
            Reject(()=>Call(settings,"Save",Path.Combine(qa,"invalid.xml")),typeof(InvalidDataException),"invalid selector cannot persist");
        }
        xml.Load(path);xml.DocumentElement.SelectSingleNode("LocationFilterAlgorithmId").InnerText="C";xml.Save(path);
        object[] load={path,null,null};bool ok=(bool)type.GetMethod("TryLoad",All).Invoke(null,load);
        Check(!ok&&(string)Get(load[1],"LocationFilterAlgorithmId")=="A"&&!(bool)Get(Get(load[1],"Hardware"),"Enabled"),"invalid XML rejected with offline A fallback, not silently accepted");
        Set(settings,"LocationFilterAlgorithmId","B");var settingsType=host.GetType("Xiongxin.OWTS.Desktop.SettingsForm",true);
        using(var form=(Form)Activator.CreateInstance(settingsType,new[]{settings})){
            Check(!(bool)Get(form,"plusDataUnlocked"),"maintenance parameter page starts unopened");
            Check((string)Get(Call(form,"CaptureValues"),"LocationFilterAlgorithmId")=="B","general save cannot change protected filter default");
            Reject(()=>Call(form,"CapturePlusDataValues",fresh()),typeof(InvalidOperationException),"protected capture checks PlusData authorization before control access");
            var section=host.GetType("Xiongxin.OWTS.Desktop.SettingsSection",true);Call(form,"OpenSection",Enum.Parse(section,"PlusData"));
            var filter=(ComboBox)Get(form,"locationFilterAlgorithm");var locator=(ComboBox)Get(form,"minus3AlgorithmIndex");
            Check(filter.Name=="PlusDataLocationFilterAlgorithm"&&filter.Items.Cast<object>().Select(x=>x.ToString()).SequenceEqual(new[]{"A","B"}),"dedicated wheel-safe filter A/B selector");
            Check(!Object.ReferenceEquals(filter,locator)&&filter.SelectedIndex==1,"filter selector is independent of location method");
            locator.SelectedIndex=0;filter.SelectedIndex=1;var captured=Call(form,"CaptureValues");
            Check((string)Get(captured,"LocationFilterAlgorithmId")=="B"&&(int)Get(Get(captured,"TaskMinus3"),"AlgorithmIndex")==5,"filter B can pair with locator A");
            locator.SelectedIndex=1;filter.SelectedIndex=0;captured=Call(form,"CaptureValues");
            Check((string)Get(captured,"LocationFilterAlgorithmId")=="A"&&(int)Get(Get(captured,"TaskMinus3"),"AlgorithmIndex")==7,"filter A can pair with locator B");
            filter.SelectedIndex=-1;Reject(()=>Call(form,"CaptureValues"),typeof(InvalidOperationException),"missing filter selection is not rewritten as B");filter.SelectedIndex=1;
            Set(form,"plusDataUnlocked",false);filter.SelectedIndex=0;
            Check((string)Get(Call(form,"CaptureValues"),"LocationFilterAlgorithmId")=="B","unopened/unauthorized capture preserves original even if hidden control changed");
            Set(form,"plusDataUnlocked",true);
            var language=(ComboBox)Get(form,"language");var panel=(Control)Get(form,"plusDataTab");
            var captions=(System.Collections.IDictionary)Get(form,"settingsCaptions");
            var filterLabel=Caption(captions,"定位过滤算法");
            var methodLabel=Caption(captions,"定位方法");
            var traditional=host.GetType("Xiongxin.OWTS.Localization.UiLanguage",true).GetMethod("ToTraditional",All);
            foreach(string code in new[]{"en","zh-TW"}){
                for(int i=0;i<language.Items.Count;i++)if((string)Get(language.Items[i],"Code")==code){language.SelectedIndex=i;break;}
                Check(filterLabel.Text==(code=="en"?"Location filter algorithm":(string)traditional.Invoke(null,new object[]{"定位过滤算法"})),code+" independent filter label translated");
                Check(methodLabel.Text==(code=="en"?"Location method":(string)traditional.Invoke(null,new object[]{"定位方法"})),code+" original locator method remains independent");
                Check(filter.Items.Cast<object>().Select(x=>x.ToString()).SequenceEqual(new[]{"A","B"}),code+" stable language-neutral algorithm IDs");
            }
        }
    }

    static void DefaultBoundaries(Assembly host)
    {
        Type helper=host.GetType("Xiongxin.OWTS.Desktop.LocationFilterSettingsDefaults",true);
        using(var p=new Probe()){
            for(int i=0;i<100;i++)Apply(helper,p,"LocationFilterAlgorithmId","A","project1");Check(p.AnalysisWrites==0,"100 equal defaults do not invoke setter/redraw");
            Apply(helper,p,"LocationFilterAlgorithmId","B","project1");Check(p.LocationFilterAlgorithmId=="B"&&p.AnalysisWrites==1,"changed global default applies once to empty new analysis");
            for(int i=0;i<100;i++)Apply(helper,p,"LocationFilterAlgorithmId","B","project1");Check(p.AnalysisWrites==1,"100 same B contexts do not invoke setter");
            p.LocationFilterAlgorithmId="A";int manual=p.AnalysisWrites;
            Apply(helper,p,"LocationFilterAlgorithmId","B","project1");Check(p.LocationFilterAlgorithmId=="A"&&p.AnalysisWrites==manual,"same-page manual selection retained after force/context refresh");
            p.LoadedAnalysisId="saved-B";p.LocationFilterAlgorithmId="B";p.SourceRowCount=6;manual=p.AnalysisWrites;
            Apply(helper,p,"LocationFilterAlgorithmId","A","project1");Check(p.LocationFilterAlgorithmId=="B"&&p.AnalysisWrites==manual,"loaded/analyzed session B wins over global A");
            Apply(helper,p,"LocationFilterAlgorithmId","A","project2");Check(p.LocationFilterAlgorithmId=="A","explicit new project takes current global A before its session loads");
            p.LocationFilterAlgorithmId="B";manual=p.AnalysisWrites;Apply(helper,p,"LocationFilterAlgorithmId","A","project2");Check(p.AnalysisWrites==manual,"new project's loaded B also retained");
        }
        using(var p=new Probe{LoadedAnalysisId="already-loaded",SourceRowCount=6}){
            p.LocationFilterAlgorithmId="B";int n=p.AnalysisWrites;Apply(helper,p,"LocationFilterAlgorithmId","A","first");Check(p.AnalysisWrites==n,"first host attachment does not overwrite already loaded B");
            Apply(helper,p,"LocationFilterAlgorithmId","A","next");Check(p.LocationFilterAlgorithmId=="A","subsequent new project recognized after protected initial attachment");
        }
        foreach(string guard in new[]{"IsBusy","ActionPending","HasActiveHardwareSession","IsAnalysisRunning"})using(var p=new Probe()){
            Set(p,guard,true);Apply(helper,p,"StatisticsLocationFilterAlgorithmId","B","project");Check(p.PressureWrites==0,guard+" blocks a runtime default mutation");
            Set(p,guard,false);Apply(helper,p,"StatisticsLocationFilterAlgorithmId","B","project");Check(p.PressureWrites==1,"idle applies deferred default after "+guard);
        }
        using(var p=new Probe{DataSaved=true}){p.StatisticsLocationFilterAlgorithmId="B";int n=p.PressureWrites;Apply(helper,p,"StatisticsLocationFilterAlgorithmId","A","project");Check(p.PressureWrites==n,"saved pressure trial remains session-owned");}
        using(var p=new Probe())Reject(()=>Apply(helper,p,"LocationFilterAlgorithmId","C","p"),typeof(ArgumentException),"invalid global cannot reach a component");
        using(var legacy=new Control()){Apply(helper,legacy,"LocationFilterAlgorithmId","A","p");Check(true,"legacy A-only component remains compatible");Reject(()=>Apply(helper,legacy,"LocationFilterAlgorithmId","B","p"),typeof(InvalidOperationException),"legacy component must not falsely claim B");}
    }

    static void RealSessions(Assembly host,string root,string qa)
    {
        Type helper=host.GetType("Xiongxin.OWTS.Desktop.LocationFilterSettingsDefaults",true);
        var analysis=Assembly.LoadFrom(Path.Combine(root,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        var pressure=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
        analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度: = \"1000\"\r\n相电压: = \"8.7\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t终端\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t终端\t1000m\r\n",new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(project,"A_13.40kV_1.loc"),new byte[256]);
        using(var a=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true)))
        using(var p=(Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl",true))){
            Set(a,"PromptSavedAnalysisOnProjectLoad",false);Set(a,"AutomaticDisplayRecommendation",false);Set(p,"AutomaticDisplayRecommendation",false);
            Apply(helper,a,"LocationFilterAlgorithmId","B",project);Apply(helper,p,"StatisticsLocationFilterAlgorithmId","B",project);
            Check((string)Get(a,"LocationFilterAlgorithmId")=="B"&&(string)Get(p,"StatisticsLocationFilterAlgorithmId")=="B","real analysis and pressure controls share unified B default");
            long sequence=(long)Get(p,"PressureRequestSequence");Set(a,"ProjectPath",project);Set(Get(a,"PdMap"),"ApproximateCablePositions",false);
            var catalog=(double[,])Call(Get(a,"AnalysisTree"),"GetDataCatalog");Check(catalog.GetLength(0)==1,"test-owned source catalog is valid");
            double[] x={500,500.1,501,800,997,1000},q={200,5,20,40,200,200};var matrix=new double[6,36];var waves=new double[6,4096];
            for(int r=0;r<6;r++){
                matrix[r,0]=catalog[0,0];matrix[r,1]=catalog[0,1];matrix[r,2]=catalog[0,2];matrix[r,3]=45;matrix[r,5]=1000;matrix[r,6]=170;matrix[r,7]=125e6;matrix[r,9]=r;
                matrix[r,10]=100;matrix[r,11]=710;matrix[r,12]=4.88;matrix[r,13]=x[r];matrix[r,15]=matrix[r,16]=q[r];matrix[r,32]=4095;matrix[r,33]=.9;matrix[r,35]=1;
                for(int i=0;i<4096;i++)waves[r,i]=q[r]*Math.Exp(-Math.Pow((i-100)/14d,2));
            }
            Check((bool)Call(a,"LoadDataMatrices",matrix,waves),"real analysis accepts isolated complete rows");
            Set(a,"LocationFilterStrength",40d);Set(a,"LocationFilterBChargeThresholdPc",250d);
            Check((bool)Call(a,"SaveNamedAnalysis","saved B parameters"),"real named B session saved: "+Get(a,"LastError"));string id=(string)Get(a,"LastSavedAnalysisId");
            Set(a,"LocationFilterAlgorithmId","A");Set(a,"LocationFilterBChargeThresholdPc",20d);
            Check((bool)Call(a,"LoadSavedAnalysis",id),"real named B session loaded: "+Get(a,"LastError"));
            for(int i=0;i<100;i++)Apply(helper,a,"LocationFilterAlgorithmId","A",project);
            Check((string)Get(a,"LoadedAnalysisId")==id&&(string)Get(a,"LocationFilterAlgorithmId")=="B"&&(double)Get(a,"LocationFilterBChargeThresholdPc")==250d&&(double)Get(a,"LocationFilterStrength")==40d,"saved B ID and parameters survive 100 global A refreshes");
            var settings=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.AppSettings",true));Set(settings,"LocationFilterAlgorithmId","A");
            var captured=Call(host.GetType("Xiongxin.OWTS.Desktop.AnalysisSettingsPersistence",true),"Capture",a,settings);
            Check((string)Get(captured,"LocationFilterAlgorithmId")=="A","ordinary analysis parameter save does not promote a session B to global default");
            object pressureSaved=null;var argument=Expression.Parameter(settings.GetType(),"settings");
            var saveType=typeof(Action<>).MakeGenericType(settings.GetType());var save=Expression.Lambda(saveType,Expression.Invoke(Expression.Constant((Action<object>)(v=>pressureSaved=v)),Expression.Convert(argument,typeof(object))),argument).Compile();
            Call(host.GetType("Xiongxin.OWTS.Desktop.PressurePreferencesPersistence",true),"Save",p,settings,save);
            Check(pressureSaved!=null&&(string)Get(pressureSaved,"LocationFilterAlgorithmId")=="A"&&(string)Get(settings,"LocationFilterAlgorithmId")=="A","ordinary pressure reminder save preserves maintenance authority");
            // Exercise the real host boundary, including force=true and language
            // context changes, rather than only the isolated defaults helper.
            Set(settings,"DataRoot",qa);Set(settings,"EnableAiFeatures",false);Set(settings,"EnableDefectRecognition",false);
            var state=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.ApplicationState",true));object[] loadProject={project,null};
            Check((bool)state.GetType().GetMethod("TryLoadProject",All).Invoke(state,loadProject),"real host state loads only the test-owned project");
            var diagnostics=Activator.CreateInstance(host.GetType("Xiongxin.OWTS.Desktop.Diagnostics",true),new object[]{Path.Combine(qa,"host-diagnostics")});
            var hostType=host.GetType("Xiongxin.OWTS.Desktop.ModuleHost",true);
            using(var moduleHost=(IDisposable)Activator.CreateInstance(hostType,new object[]{settings,state,diagnostics,Path.Combine(root,"modules")})){
                var descriptors=((Array)hostType.GetProperty("Descriptors",All).GetValue(null,null)).Cast<object>().ToArray();
                var analysisDescriptor=descriptors.Single(d=>(string)Get(d,"Id")=="DataAnalysis");
                var pressureDescriptor=descriptors.Single(d=>(string)Get(d,"Id")=="PressureTest");
                foreach(string language in new[]{"zh-CN","en","zh-TW"}){
                    Set(settings,"LanguageCode",language);
                    for(int i=0;i<10;i++)Call(moduleHost,"ApplyContext",analysisDescriptor,a,true);
                    Check((string)Get(a,"LocationFilterAlgorithmId")=="B"&&(double)Get(a,"LocationFilterBChargeThresholdPc")==250d&&(double)Get(a,"LocationFilterStrength")==40d,"real force ApplyContext preserves saved B parameters under "+language);
                }
                Call(moduleHost,"ApplyContext",pressureDescriptor,p,true);
                Check((string)Get(p,"StatisticsLocationFilterAlgorithmId")=="A","real pressure host applies unified A to a new empty page");
                Set(p,"StatisticsLocationFilterAlgorithmId","B");
                for(int i=0;i<10;i++)Call(moduleHost,"ApplyContext",pressureDescriptor,p,true);
                Check((string)Get(p,"StatisticsLocationFilterAlgorithmId")=="B","real forced pressure context preserves same-page manual B");
                using(var fresh=(Control)Activator.CreateInstance(analysis.GetType("HVX.PdReview.PdReviewControl",true))){
                    Set(fresh,"PromptSavedAnalysisOnProjectLoad",false);Set(settings,"LocationFilterAlgorithmId","B");
                    Call(moduleHost,"ApplyContext",analysisDescriptor,fresh,true);
                    Check((string)Get(fresh,"LocationFilterAlgorithmId")=="B","real new analysis control takes latest global B default");
                }
            }
            Check((long)Get(p,"PressureRequestSequence")==sequence,"maintenance filtering changes issue no hardware requests");
        }
    }

    [STAThread]static int Main(string[] args)
    {
        try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string root=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new InvalidOperationException("Fresh test-owned fixture required.");Directory.CreateDirectory(qa);
            var host=Assembly.LoadFrom(Path.Combine(root,"CurrentUiHost.dll"));Configuration(host,qa);DefaultBoundaries(host);RealSessions(host,root,qa);
            Console.WriteLine("LOCATION_FILTER_SETTINGS_PASS="+checks+";HARDWARE=False;SDK=False;PRODUCT_MAIN=False;USER_PROJECT_WRITES=False");return 0;
        }catch(Exception e){Console.Error.WriteLine(e is TargetInvocationException?((TargetInvocationException)e).InnerException:e);return 1;}
    }
}
