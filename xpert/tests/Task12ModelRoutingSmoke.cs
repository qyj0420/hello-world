using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class Task12ModelRoutingSmoke
{
    static int checks;const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static bool SamePath(string left,string right)
    {return String.Equals(Path.GetFullPath(left).TrimEnd('\\','/'),Path.GetFullPath(right).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);}
    static void CheckDefaultModelRouting(Control control,AppSettings settings,string build,string qa)
    {
        string executableRoot=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        Check(Path.GetFullPath(qa).StartsWith(executableRoot.TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),
            "default-root fixture is confined to the isolated test executable directory");
        string staged=Path.Combine(executableRoot,"model-payload");Directory.CreateDirectory(staged);
        // Keep the synthetic cache identity short: model filenames themselves
        // contain 64 hex characters, and the supported CLR still has MAX_PATH.
        string cacheModules=Path.Combine(qa,"cache","bundle","modules");
        string misleadingModels=Path.Combine(Path.GetDirectoryName(cacheModules),"model-payload");
        Directory.CreateDirectory(cacheModules);Directory.CreateDirectory(misleadingModels);
        foreach(string kind in new[]{DefectModelCatalog.Standard,DefectModelCatalog.Competition}){
            string fileName=DefectModelCatalog.FileName(kind);
            // The runner gives each invocation a fresh QA directory. Never overwrite
            // either a deployed model or an existing test fixture.
            File.Copy(Path.Combine(build,"model-payload",fileName),Path.Combine(staged,fileName),false);
            File.Copy(Path.Combine(build,"model-payload",fileName),Path.Combine(misleadingModels,fileName),false);
        }
        foreach(string defaultDirectory in new string[]{null,""," "})
        using(var host=new ModuleHost(settings,new ApplicationState(),new Diagnostics(Path.Combine(qa,"default-diagnostics")),defaultDirectory)){
            var modelRoot=typeof(ModuleHost).GetField("modelRoot",All);
            Check(modelRoot!=null&&SamePath((string)modelRoot.GetValue(host),executableRoot),
                "default module host uses the executable directory as modelRoot");
            // A source-built smoke executable has no embedded module payload. Only
            // redirect this test instance's module root to the isolated cache-shaped
            // fixture; leave modelRoot untouched. This exercises both real routing
            // methods without extracting into the user's actual module cache.
            typeof(ModuleHost).GetField("modulesRoot",All).SetValue(host,cacheModules);
            Check(!SamePath((string)modelRoot.GetValue(host),Path.GetDirectoryName(host.ModulesRoot)),
                "default model root is independent of the embedded cache parent");
            object descriptor=typeof(ModuleHost).GetMethod("FindDescriptor",All).Invoke(host,new object[]{ModuleHost.DataAnalysis});
            foreach(string kind in new[]{DefectModelCatalog.Standard,DefectModelCatalog.Competition}){
                settings.DefectModelKind=kind;
                string expected=Path.Combine(staged,DefectModelCatalog.FileName(kind));
                Check(SamePath(DefectModelCatalog.Resolve(kind,null,null,Path.GetDirectoryName(cacheModules)),Path.Combine(misleadingModels,DefectModelCatalog.FileName(kind))),
                    "cache-parent decoy exists so a wrong root cannot pass by installed-model fallback");
                typeof(ModuleHost).GetMethod("ApplyContext",All).Invoke(host,new object[]{descriptor,control,true});
                Check(SamePath((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null),expected),
                    "default data analysis selects executable-side "+kind+" model, not embedded cache parent");
                typeof(ModuleHost).GetMethod("ApplyPressureAnalysisSettings",All).Invoke(host,new object[]{control,settings});
                Check(SamePath((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null),expected),
                    "default cumulative pressure selects executable-side "+kind+" model, not embedded cache parent");
            }
        }
    }
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();string build=args[0],qa=args[1];Directory.CreateDirectory(qa);
        var settings=new AppSettings{DataRoot=qa};string config=Path.Combine(qa,"settings.xml");
        var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
        var preference=assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences").GetField("PathProvider",All);preference.SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        using(var host=new ModuleHost(settings,new ApplicationState(),new Diagnostics(Path.Combine(qa,"diagnostics")),Path.Combine(build,"modules"))){
            Check(SamePath((string)typeof(ModuleHost).GetField("modelRoot",All).GetValue(host),build),"explicit modules directory preserves the supplied build root for models");
            Control control;string error;Check(host.TryCreateControl(ModuleHost.DataAnalysis,out control,out error),"host loads analysis: "+error);
            using(control){
            CheckDefaultModelRouting(control,settings,build,qa);
            // The preceding isolated hosts deliberately changed this control.
            // Restore its explicit host context rather than relying on an unchanged
            // settings signature to refresh a control changed outside that host.
            object explicitDescriptor=typeof(ModuleHost).GetMethod("FindDescriptor",All).Invoke(host,new object[]{ModuleHost.DataAnalysis});
            typeof(ModuleHost).GetMethod("ApplyContext",All).Invoke(host,new object[]{explicitDescriptor,control,true});
            foreach(string kind in new[]{DefectModelCatalog.Standard,DefectModelCatalog.Competition}){
                settings.DefectModelKind=kind;settings.Save(config);var loaded=AppSettings.Load(config);Check(loaded.DefectModelKind==kind,"selector persisted");
                host.RefreshContext();string expected=DefectModelCatalog.Resolve(kind,null,null,build);
                Check((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null)==expected,"real analysis receives selected model");
                typeof(ModuleHost).GetMethod("ApplyPressureAnalysisSettings",All).Invoke(host,new object[]{control,loaded});
                Check((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null)==expected,"cumulative pressure receives identical model");
                DefectModelCatalog.ValidateHeader(expected);Check(Path.GetExtension(expected)==".hxm","nonsemantic deployed extension");
                Type runtimeType=assembly.GetType("HVX.PdReview.ReviewTask12Runtime",true);
                using(var runtime=(IDisposable)Activator.CreateInstance(runtimeType,true)){
                    double[] phase=Enumerable.Range(0,120).Select(i=>(i%2==0?60d:240d)+i%15).ToArray();
                    double[] charge=Enumerable.Range(0,120).Select(i=>100d+i%17).ToArray();
                    var result=(Dictionary<string,object>)runtimeType.GetMethod("Classify",All).Invoke(runtime,new object[]{expected,phase,charge});
                    Check(result!=null&&result.Count>0,"actual encrypted "+kind+" inference returned structured result");
                    Check(result.ContainsKey("ok")&&Convert.ToBoolean(result["ok"]),"actual "+kind+" inference succeeded: "+(result.ContainsKey("error")?Convert.ToString(result["error"]):"no status"));
                    string identity=(string)runtimeType.GetField("identity",All).GetValue(runtime);
                    Check(identity.Contains(kind==DefectModelCatalog.Standard?DefectModelCatalog.StandardHash:DefectModelCatalog.CompetitionHash),"runtime session uses requested model bytes");
                    Console.WriteLine(kind+" actual inference keys: "+String.Join(",",result.Keys));
                }
            }}
        }
        string missingRoot=Path.Combine(qa,"no-models");string missing=DefectModelCatalog.Resolve("competition",null,null,missingRoot);
        Check(missing==Path.Combine(DefectModelCatalog.InstalledDirectory,DefectModelCatalog.FileName("competition")),"missing competition never falls back to standard");
        var independent=new CompetitionDefectPreferences{ModelKind="competition",ModelLibraryPath="",CompetitionModelPath=""};
        independent.Save(Path.Combine(qa,"independent.json"));string warning;
        Check(CompetitionDefectPreferences.Load(Path.Combine(qa,"independent.json"),out warning).ModelKind=="competition"&&warning.Length==0,"independent selection roundtrip");
        Console.WriteLine("TASK12_MODEL_ROUTING_PASS="+checks+"; HARDWARE_ACCESSED=False; TRAINING=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
