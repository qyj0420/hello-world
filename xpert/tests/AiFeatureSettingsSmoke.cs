using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class AiFeatureSettingsSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Call(object o,string method,params object[] args){return o.GetType().GetMethod(method,Hidden).Invoke(o,args);}
    sealed class Probe:UserControl {bool ai;public int Writes;public string EndpointPolicy{get;set;}public bool EnableAiFeatures{get{return ai;}set{ai=value;Writes++;}}}
    [STAThread]static int Main(string[] args)
    {
        try{
            string qa=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string path=Path.Combine(qa,"settings.xml");File.WriteAllText(path,"<HVXUnifiedDesktopSettings></HVXUnifiedDesktopSettings>");
            var settings=AppSettings.Load(path);Check(settings.EnableAiFeatures,"legacy missing field defaults enabled");
            foreach(bool on in new[]{false,true}){
                settings.EnableAiFeatures=on;settings.Save(path);var loaded=AppSettings.Load(path);Check(loaded.EnableAiFeatures==on,"save and load both states");
                using(var form=new SettingsForm(loaded)){
                    Check(form.GetType().GetField("enableAiFeatures",Hidden).GetValue(form)==null,"AI editor unavailable before Plus Data authorization");
                    Call(form,"BuildPlusDataPanel");Call(form,"LoadPlusDataValues",loaded);
                    var box=(CheckBox)form.GetType().GetField("enableAiFeatures",Hidden).GetValue(form);Check(box.Checked==on,"load checkbox");box.Checked=!on;
                    Check(((AppSettings)Call(form,"CaptureValues")).EnableAiFeatures==on,"locked draft cannot overwrite AI flag");
                    form.GetType().GetField("plusDataUnlocked",Hidden).SetValue(form,true);
                    Check(((AppSettings)Call(form,"CaptureValues")).EnableAiFeatures==!on,"capture checkbox");Check(loaded.EnableAiFeatures==on,"settings cancel preserves original");
                }
            }
            var descriptor=ModuleHost.Descriptors.Single(d=>d.Id==ModuleHost.DataAnalysis);
            using(var probe=new Probe())using(var host=new ModuleHost(settings,new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules"))){
                Call(host,"ApplyContext",descriptor,probe,false);Check(probe.EnableAiFeatures,"initial host maps enabled");int count=probe.Writes;
                Call(host,"ApplyContext",descriptor,probe,false);Check(probe.Writes==count,"unchanged settings no redundant writes");settings.EnableAiFeatures=false;
                Call(host,"ApplyContext",descriptor,probe,false);Check(!probe.EnableAiFeatures&&probe.Writes==count+1,"master flag alone reconfigures loaded module");
            }
            Console.WriteLine("AI_FEATURE_SETTINGS_PASS="+checks+"; HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
