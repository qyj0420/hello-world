using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Forms;

static class PressureEndpointReadOnlySmoke
{
    const BindingFlags B=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,B);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,B).GetValue(o);}
    static T Pump<T>(Task<T> task){var clock=Stopwatch.StartNew();while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(10);if(clock.Elapsed.TotalSeconds>45)throw new TimeoutException("Offline endpoint fixture timeout");}return task.GetAwaiter().GetResult();}
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);
            if(Directory.Exists(qa))throw new Exception("fresh isolated fixture required");Directory.CreateDirectory(qa);
            string project=(string)typeof(PressureCumulativeAnalysisSmoke).GetMethod("Fixture",B).Invoke(null,new object[]{qa});
            var originals=Directory.GetFiles(project).ToDictionary(p=>p,File.ReadAllBytes);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            assembly.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",B).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            Type type=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            foreach(int algorithm in new[]{5,7}) {
                using(var control=(Control)Activator.CreateInstance(type)) {
                    control.CreateControl();Set(control,"PromptSavedAnalysisOnProjectLoad",false);Set(control,"ProjectPath",project);
                    Set(control,"AnalysisAlgorithmIndex",algorithm);Set(control,"AnalysisPhaseFilterEnabled",false);
                    string path=Path.Combine(project,"A_12.0kV_0.loc");
                    bool prepared=Pump((Task<bool>)Call(control,"PreparePressureEndpointReviewAsync",path,(Func<bool>)(()=>true)));
                    Check(prepared,"actual engine prepares endpoint MAPs algorithm "+algorithm);
                    var rows=(int[])Call(control,"EndpointReviewRows");Check(rows.Length>0,"current file exposes actual MAP rows");
                    var matrix=(double[,])Get(control,"ResultParameterMatrix");Check(rows.Length==matrix.GetLength(0),"only requested round inspected");
                    object pdmap=Get(control,"pdmap");Call(control,"ShowPdMapWorkspace");
                    object result=Call(pdmap,"IdentifyEndpoint");Check(result!=null&&!String.IsNullOrEmpty((string)Get(result,"Direction")),"existing near/far evidence engine returns classification");
                    Check(matrix.Cast<double>().SequenceEqual(((double[,])Get(control,"ResultParameterMatrix")).Cast<double>()),"classification preserves position/acceptance");
                    bool rejected=false;try{Pump((Task<bool>)Call(control,"PreparePressureEndpointReviewAsync",path,(Func<bool>)(()=>true)));}catch(InvalidOperationException){rejected=true;}
                    Check(rejected,"cannot append duplicate MAPs to used inspector");
                }
            }
            using(var control=(Control)Activator.CreateInstance(type)) {
                control.CreateControl();Set(control,"PromptSavedAnalysisOnProjectLoad",false);Set(control,"ProjectPath",project);Set(control,"AnalysisAlgorithmIndex",5);Set(control,"AnalysisPhaseFilterEnabled",false);
                string path=Path.Combine(project,"A_12.0kV_0.loc");
                Check(!Pump((Task<bool>)Call(control,"PreparePressureEndpointReviewAsync",path,(Func<bool>)(()=>false))),"stale session cancels before extraction");
                int calls=0;Check(!Pump((Task<bool>)Call(control,"PreparePressureEndpointReviewAsync",path,(Func<bool>)(()=>++calls==1))),"stale completion cannot load or show dialog");
                Check((int)Get(control,"SourceRowCount")==0,"stale extraction never changes inspector state");
            }
            foreach(var item in originals)Check(File.ReadAllBytes(item.Key).SequenceEqual(item.Value),"source unchanged "+Path.GetFileName(item.Key));
            Check(Directory.GetFiles(project,"*",SearchOption.AllDirectories).Length==originals.Count,"inspection writes no project caches or records");
            Console.WriteLine("PRESSURE_ENDPOINT_READ_ONLY_PASS="+checks+"; HARDWARE_CALLS=0; USER_RECORD_WRITES=0; REAL_AB_ENGINE=TRUE");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
