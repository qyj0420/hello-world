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
        string cacheModules=Path.Combine(qa,"cache","bundle","modules");
        string misleadingModels=Path.Combine(Path.GetDirectoryName(cacheModules),"model-payload");
        Directory.CreateDirectory(cacheModules);Directory.CreateDirectory(misleadingModels);
        foreach(string kind in new[]{DefectModelCatalog.Standard,DefectModelCatalog.Competition}){
            string fileName=DefectModelCatalog.FileName(kind);
            File.Copy(Path.Combine(build,"model-payload",fileName),Path.Combine(staged,fileName),false);
            File.Copy(Path.Combine(build,"model-payload",fileName),Path.Combine(misleadingModels,fileName),false);
        }
        foreach(string defaultDirectory in new string[]{null,""," "})
        using(var host=new ModuleHost(settings,new ApplicationState(),new Diagnostics(Path.Combine(qa,"default-diagnostics")),defaultDirectory)){
            var modelRoot=typeof(ModuleHost).GetField("modelRoot",All);
            Check(modelRoot!=null&&SamePath((string)modelRoot.GetValue(host),executableRoot),
                "default module host uses the executable directory as modelRoot");
            typeof(ModuleHost).GetField("modulesRoot",All).SetValue(host,cacheModules);
            Check(!SamePath((string)modelRoot.GetValue(host),Path.GetDirectoryName(host.ModulesRoot)),
                "default model root is independent of the embedded cache parent");
            object descriptor=typeof(ModuleHost).GetMethod("FindDescriptor",All).Invoke(host,new object[]{ModuleHost.DataAnalysis});
            settings.TaskMinus3.ModelLibraryPath="";
            string expected=Path.Combine(staged,DefectModelCatalog.FileName(DefectModelCatalog.Standard));
            Check(SamePath(DefectModelCatalog.ResolveActive(null,Path.GetDirectoryName(cacheModules)),Path.Combine(misleadingModels,DefectModelCatalog.FileName(DefectModelCatalog.Standard))),
                "cache-parent decoy exists so a wrong root cannot pass by installed-model fallback");
            typeof(ModuleHost).GetMethod("ApplyContext",All).Invoke(host,new object[]{descriptor,control,true});
            Check(SamePath((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null),expected),
                "default data analysis selects executable-side standard model, not embedded cache parent");
            typeof(ModuleHost).GetMethod("ApplyPressureAnalysisSettings",All).Invoke(host,new object[]{control,settings});
            Check(SamePath((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null),expected),
                "default cumulative pressure selects executable-side standard model, not embedded cache parent");
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
            object explicitDescriptor=typeof(ModuleHost).GetMethod("FindDescriptor",All).Invoke(host,new object[]{ModuleHost.DataAnalysis});
            typeof(ModuleHost).GetMethod("ApplyContext",All).Invoke(host,new object[]{explicitDescriptor,control,true});
            // Empty path → standard; loaded path wins.
            settings.TaskMinus3.ModelLibraryPath="";settings.DefectModelKind=DefectModelCatalog.Competition;settings.Save(config);
            var loaded=AppSettings.Load(config);
            Check(loaded.DefectModelKind==DefectModelCatalog.Standard,"legacy competition selection normalizes to standard");
            host.RefreshContext();string standard=DefectModelCatalog.ResolveActive(null,build);
            Check((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null)==standard,"empty path resolves to standard");
            typeof(ModuleHost).GetMethod("ApplyPressureAnalysisSettings",All).Invoke(host,new object[]{control,loaded});
            Check((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null)==standard,"pressure path also uses standard when unloaded");
            string loadedPath=DefectModelCatalog.Resolve(DefectModelCatalog.Competition,null,null,build);
            settings.TaskMinus3.ModelLibraryPath=loadedPath;settings.Save(config);loaded=AppSettings.Load(config);
            Check(SamePath(loaded.TaskMinus3.ModelLibraryPath,loadedPath),"loaded model path persisted");
            host.RefreshContext();
            Check(SamePath((string)control.GetType().GetProperty("ModelLibraryPath").GetValue(control,null),loadedPath),"loaded path wins over standard");
            Check(SamePath(DefectModelCatalog.ResolveActive(loadedPath,build),loadedPath),"ResolveActive prefers loaded path");
            DefectModelCatalog.ValidateHeader(standard);Check(Path.GetExtension(standard)==".hxm","nonsemantic deployed extension");
            Type runtimeType=assembly.GetType("HVX.PdReview.ReviewTask12Runtime",true);
            foreach(string kind in new[]{DefectModelCatalog.Standard,DefectModelCatalog.Competition}){
                string model=DefectModelCatalog.Resolve(kind,null,null,build);
                using(var runtime=(IDisposable)Activator.CreateInstance(runtimeType,true)){
                    double[] phase=Enumerable.Range(0,120).Select(i=>(i%2==0?60d:240d)+i%15).ToArray();
                    double[] charge=Enumerable.Range(0,120).Select(i=>100d+i%17).ToArray();
                    var result=(Dictionary<string,object>)runtimeType.GetMethod("Classify",All).Invoke(runtime,new object[]{model,phase,charge});
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
        Check(CompetitionDefectPreferences.Load(Path.Combine(qa,"independent.json"),out warning).ModelKind=="standard"&&warning.Length==0,"independent prefs normalize to standard");
        string competitionFile=DefectModelCatalog.Resolve(DefectModelCatalog.Competition,null,null,build);
        var migrated=new CompetitionDefectPreferences{ModelKind="competition",ModelLibraryPath="",CompetitionModelPath=competitionFile};
        migrated.Validate();
        Check(SamePath(migrated.ModelLibraryPath,competitionFile)&&migrated.ModelKind==DefectModelCatalog.Standard&&migrated.CompetitionModelPath.Length==0,
            "former competition path migrates into loaded-model slot");
        Check(SamePath(DefectModelCatalog.ResolveActive(migrated.ModelLibraryPath,build),competitionFile),"ResolveActive uses migrated loaded path");
        Console.WriteLine("TASK12_MODEL_ROUTING_PASS="+checks+"; HARDWARE_ACCESSED=False; TRAINING=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
