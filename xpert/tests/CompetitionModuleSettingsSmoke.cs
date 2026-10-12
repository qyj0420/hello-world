using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class CompetitionModuleSettingsSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();
            string qa=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string file=Path.Combine(qa,"settings.xml");File.WriteAllText(file,"<HVXUnifiedDesktopSettings></HVXUnifiedDesktopSettings>");
            Check(!AppSettings.Load(file).EnableCompetitionDefectModule,"legacy configurations leave optional module disabled");
            foreach(bool enabled in new[]{false,true}){
                var settings=new AppSettings {EnableCompetitionDefectModule=enabled};settings.Save(file);
                var loaded=AppSettings.Load(file);Check(loaded.EnableCompetitionDefectModule==enabled,"persist both feature states");
                using(var dialog=new SettingsForm(loaded)){
                    Check(Get(dialog,"enableCompetitionDefectModule")==null,"ordinary settings do not expose manufacturer switch");
                    Check(((AppSettings)Call(dialog,"CaptureValues")).EnableCompetitionDefectModule==enabled,"ordinary save preserves manufacturer choice");
                    using(var parameters=(Control)Call(dialog,"BuildPlusDataPanel")){
                        Call(dialog,"LoadPlusDataValues",loaded);var box=(CheckBox)Get(dialog,"enableCompetitionDefectModule");
                        Check(parameters.Contains(box)&&box.Checked==enabled,"Plus Data page hosts saved option");box.Checked=!enabled;
                        Check(((AppSettings)Call(dialog,"CaptureValues")).EnableCompetitionDefectModule==enabled,"locked hidden edit cannot change setting");
                        typeof(SettingsForm).GetField("plusDataUnlocked",Hidden).SetValue(dialog,true);
                        Check(((AppSettings)Call(dialog,"CaptureValues")).EnableCompetitionDefectModule==!enabled,"fixture authorized Plus Data capture");
                    }
                }
            }
            string project=(string)typeof(Program).GetMethod("CreateFixtureProject",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{Path.Combine(qa,"project")});
            var source=new AppSettings();source.Hardware.Enabled=false;
            var state=new ApplicationState();
            using(var form=new MainForm(source,state,true)){
                form.Show();Application.DoEvents();
                Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=False;Enabled=False"),"disabled factory feature hidden");
                source.EnableCompetitionDefectModule=true;Call(form,"ApplyState");
                Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=True;Enabled=False"),"no current project: visible but disabled");
                string message;Check(state.TryLoadProject(project,out message),"load synthetic project: "+message);Application.DoEvents();
                Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=True;Enabled=True"),"selected project enables module without hardware");
                Check(Get(form,"competitionDefectLauncher")==null,"state refresh never starts external program");
                form.RenderToPng(Path.Combine(qa,"competition-enabled-home.png"));
                state.ClearProject();Application.DoEvents();Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=True;Enabled=False"),"cleared project disables entry");
                source.EnableCompetitionDefectModule=false;Call(form,"ApplyState");Check(form.StateSnapshot().Contains("CompetitionDefect:Visible=False;Enabled=False"),"turn off hides entry again");
                form.Close();
            }
            Console.WriteLine("COMPETITION_MODULE_SETTINGS_PASS="+checks+"; HARDWARE_ACCESSED=False;LIVE_SETTINGS_MODIFIED=False;EVIDENCE="+qa);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
