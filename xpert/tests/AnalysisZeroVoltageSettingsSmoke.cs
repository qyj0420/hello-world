using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class AnalysisZeroVoltageSettingsSmoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    sealed class Probe:UserControl
    {
        public string EndpointPolicy{get;set;}
        public bool AnalyzeZeroVoltageData{get;set;}
        public bool ProtectedAnalysisSettingsManaged{get;set;}
    }
    [STAThread]static int Main(string[] args){try{
        string qa=Path.Combine(Path.GetFullPath(args[0]),"zero-settings");Directory.CreateDirectory(qa);
        var setting=new AppSettings();using(var view=new Probe())using(var owner=new ModuleHost(setting,new ApplicationState(),new Diagnostics(qa),Path.Combine(qa,"modules"))){
            var method=typeof(ModuleHost).GetMethods(BindingFlags.Instance|BindingFlags.NonPublic).Single(m=>m.Name=="ApplyDataAnalysisSettings"&&m.GetParameters().Length==4);
            Action<object> apply=source=>method.Invoke(owner,new object[]{view,"",new[]{true,true,true,true},source});
            Check(!setting.AnalyzeZeroVoltageData,"new defaults remain off");view.AnalyzeZeroVoltageData=true;apply(setting);Check(!view.AnalyzeZeroVoltageData,"host applies false instead of leaving stale true");
            setting.AnalyzeZeroVoltageData=true;var snapshot=setting.Clone();setting.AnalyzeZeroVoltageData=false;apply(snapshot);Check(view.AnalyzeZeroVoltageData,"captured pressure settings preserve the round choice");
            snapshot.Save(Path.Combine(qa,"settings.xml"));apply(AppSettings.Load(Path.Combine(qa,"settings.xml")));Check(view.AnalyzeZeroVoltageData,"persisted setting reaches data analysis");
            apply(new object());Check(!view.AnalyzeZeroVoltageData,"legacy missing setting does not inherit a prior opt-in");
        }
        Console.WriteLine("ANALYSIS_ZERO_VOLTAGE_SETTINGS_PASS="+checks+";hardware=false");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
