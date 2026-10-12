using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class CalibrationNavigationSmoke
{
    static int checks;
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args);}
    static Control Active(MainForm form){return (Control)typeof(MainForm).GetField("activeModule",Hidden).GetValue(form);}
    sealed class Calibration:UserControl
    {
        public bool HasUnsavedCalibrationChanges{get;set;}
        public bool HasCalibrationData{get;set;}
        public bool IsCalibrationSavedValid{get;set;}
        public string UnsavedCalibrationSummary{get;set;}
        public bool SaveResult,KeepDirty,KeepInvalid,ThrowSave;
        public int Saves;
        public void DiscardCalibrationChanges(){HasUnsavedCalibrationChanges=false;HasCalibrationData=false;IsCalibrationSavedValid=false;}
        public bool SaveCalibrationFile()
        {
            Saves++;if(ThrowSave)throw new IOException("PRIVATE-NATIVE-PATH fixture failure");
            if(SaveResult){if(!KeepDirty)HasUnsavedCalibrationChanges=false;if(!KeepInvalid)IsCalibrationSavedValid=true;}
            return SaveResult;
        }
    }
    sealed class MissingSavedValidity:UserControl
    {
        public bool HasUnsavedCalibrationChanges{get;set;}
        public bool HasCalibrationData{get{return true;}}
        public bool SaveCalibrationFile(){HasUnsavedCalibrationChanges=false;return true;}
        public void DiscardCalibrationChanges(){HasUnsavedCalibrationChanges=false;}
    }
    sealed class OptionalTdrCalibration:UserControl
    {
        public bool HasUnsavedChargeCalibrationChanges{get;set;}
        public bool HasUnsavedCalibrationChanges{get{return true;}}
        public bool HasCalibrationData{get{return true;}}
        public bool IsCalibrationSavedValid{get{return false;}}
        public bool Discarded,ThrowDiscard;
        public bool SaveCalibrationFile(){HasUnsavedChargeCalibrationChanges=false;return true;}
        public void DiscardCalibrationChanges(){Discarded=true;if(ThrowDiscard)throw new IOException("isolated archive reload failure");}
    }
    sealed class BadDirtyType:UserControl{public string HasUnsavedCalibrationChanges{get{return "false";}}}
    sealed class ReadyCalibration:UserControl
    {
        public string Reason="not calibrated";
        public bool ThrowReadiness,LastSimulation,LastFirstArrival;
        public string LastPhase;
        public int Reads;
        public string GetCalibrationReadiness(string phase,bool simulation,bool firstArrival)
        {Reads++;LastPhase=phase;LastSimulation=simulation;LastFirstArrival=firstArrival;if(ThrowReadiness)throw new IOException("PRIVATE-NATIVE-PATH");return Reason;}
    }
    sealed class TestPage:UserControl{public string SelectedPhaseCode{get;set;}}
    static T Open<T>(MainForm form,T control) where T:Control
    {Call(form,"ShowEmbeddedModule",ModuleHost.CalibrationTest,control,(bool?)false);Check(Active(form)==control,"calibration page active without hardware or simulation start");return control;}
    static void SetCalibrationCache(MainForm form,Control control)
    {
        object host=typeof(MainForm).GetField("moduleHost",Hidden).GetValue(form);
        var cache=(Dictionary<string,Control>)typeof(ModuleHost).GetField("controls",Hidden).GetValue(host);
        Control old;if(cache.TryGetValue(ModuleHost.CalibrationTest,out old)&&old!=control)old.Dispose();
        cache[ModuleHost.CalibrationTest]=control;
        // A replaced fixture is a new control instance, not the previously
        // configured cached page. Let the normal host context bind it again.
        foreach(string field in new[]{"contextSignatures","appliedProjectPaths"})
            ((Dictionary<string,string>)typeof(ModuleHost).GetField(field,Hidden).GetValue(host)).Remove(ModuleHost.CalibrationTest);
    }
    static bool Ready(MainForm form,Control page,bool simulation,string id=ModuleHost.PressureTest)
    {return (bool)Call(form,"CheckCalibrationBeforeTest",id,page,simulation);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var settings=new AppSettings{LanguageCode="zh-TW"};settings.Hardware.Enabled=false;
            using(var form=new MainForm(settings,new ApplicationState(),true))
            {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                DialogResult choice=DialogResult.Cancel;int prompts=0,errors=0;
                form.UnsavedCalibrationPrompt=()=>{prompts++;return choice;};
                form.CalibrationNavigationError=text=>{errors++;Check(!String.IsNullOrWhiteSpace(text)&&!text.Contains("PRIVATE-NATIVE-PATH"),"customer-facing save error excludes native details");};
                var calibration=Open(form,new Calibration());Call(form,"ShowHome");
                Check(prompts==0&&calibration.IsDisposed&&Active(form)==null,"empty clean calibration returns home without prompt");
                var optional=Open(form,new OptionalTdrCalibration());Call(form,"ShowHome");
                Check(prompts==0&&Active(form)==null,"optional dirty TDR does not prompt when charge calibration is saved");
                optional=Open(form,new OptionalTdrCalibration{HasUnsavedChargeCalibrationChanges=true});
                choice=DialogResult.No;Call(form,"ShowHome");
                Check(optional.Discarded&&Active(form)==null,"No returns even if reloaded archive still reports stale state");
                optional=Open(form,new OptionalTdrCalibration{HasUnsavedChargeCalibrationChanges=true,ThrowDiscard=true});
                Call(form,"ShowHome");
                Check(optional.Discarded&&Active(form)==null,"No returns even when existing-archive reload fails after discard");
                optional=Open(form,new OptionalTdrCalibration{HasUnsavedChargeCalibrationChanges=true});
                choice=DialogResult.Yes;Call(form,"ShowHome");
                Check(Active(form)==null,"saving charge returns without requiring optional TDR validity");
                choice=DialogResult.Cancel;
                using(var described=new Calibration{UnsavedCalibrationSummary="ALL · TDR: ranging result / cable length not saved\r\nB · 100nC: calibration coefficient changed"})
                {
                    string message=(string)Call(form,"BuildUnsavedCalibrationNavigationMessage",described);
                    Check(message.Contains(described.UnsavedCalibrationSummary),"exit prompt preserves actual phase/level summary");
                    Check(message.Contains("TDR")&&message.Contains("100nC"),"exit prompt distinguishes ranging and another-phase charge");
                    Check(message.Contains("已保存")||message.Contains("已儲存"),"discard wording states saved files are preserved");
                    Check(!described.HasUnsavedCalibrationChanges&&described.Saves==0,"constructing prompt cannot save or alter calibration");
                }
                using(var older=new UserControl())
                {
                    string fallback=(string)Call(form,"BuildUnsavedCalibrationNavigationMessage",older);
                    Check(!String.IsNullOrWhiteSpace(fallback)&&!fallback.Contains("ALL · TDR"),"older modules retain non-invented fallback prompt");
                }
                calibration=Open(form,new Calibration{HasUnsavedCalibrationChanges=true,HasCalibrationData=true});Call(form,"ShowHome");
                Check(Active(form)==calibration&&!calibration.IsDisposed&&calibration.Saves==0,"Cancel retains data and page");
                choice=DialogResult.Yes;Call(form,"ShowHome");Check(Active(form)==calibration&&calibration.Saves==1,"save false cannot leave");
                calibration.SaveResult=true;calibration.KeepDirty=true;Call(form,"ShowHome");Check(Active(form)==calibration,"save success flag with dirty data cannot leave");
                calibration.KeepDirty=false;calibration.KeepInvalid=true;calibration.IsCalibrationSavedValid=false;Call(form,"ShowHome");Check(Active(form)==calibration,"clean flag with invalid file cannot leave");
                calibration.ThrowSave=true;Call(form,"ShowHome");Check(errors==1&&Active(form)==calibration,"save exception retains calibration");
                calibration.ThrowSave=false;calibration.KeepInvalid=false;Call(form,"ShowHome");Check(Active(form)==null&&calibration.IsDisposed,"confirmed valid save returns home");
                calibration=Open(form,new Calibration{HasCalibrationData=true,IsCalibrationSavedValid=false});choice=DialogResult.Cancel;int before=prompts;Call(form,"ShowHome");
                Check(prompts==before+1&&Active(form)==calibration,"deleted or corrupt saved file prompts despite clean dirty flag");
                choice=DialogResult.No;Call(form,"ShowHome");Check(calibration.IsDisposed&&calibration.Saves==0,"explicit discard leaves without pretending to save");
                calibration=Open(form,new Calibration{HasUnsavedCalibrationChanges=true,HasCalibrationData=true});choice=DialogResult.Cancel;
                using(var destination=new Panel()){Call(form,"ShowEmbeddedModule",ModuleHost.DataBrowser,destination,null);Check(Active(form)==calibration&&!calibration.IsDisposed,"module switch cannot bypass calibration prompt");}
                form.Close();Check(!form.IsDisposed&&Active(form)==calibration,"child close respects Cancel");
                form.UnsavedCalibrationPrompt=()=>{Call(form,"ShowHome");Check(Active(form)==calibration,"reentrant navigation blocked");return DialogResult.Cancel;};
                Call(form,"ShowHome");Check(Active(form)==calibration,"outer cancelled prompt retains page");
                form.UnsavedCalibrationPrompt=()=>choice;choice=DialogResult.No;Call(form,"ShowHome");
                var legacy=Open(form,new UserControl());choice=DialogResult.Cancel;Call(form,"ShowHome");Check(Active(form)==legacy,"missing dirty contract is not assumed clean");
                choice=DialogResult.Yes;Call(form,"ShowHome");Check(Active(form)==legacy,"missing save contract cannot report success");choice=DialogResult.No;Call(form,"ShowHome");Check(Active(form)==legacy,"missing discard contract cannot abandon cached calibration");typeof(MainForm).GetField("activeModule",Hidden).SetValue(form,null);legacy.Dispose();
                var invalid=Open(form,new MissingSavedValidity());choice=DialogResult.Yes;Call(form,"ShowHome");Check(Active(form)==invalid,"data requires independently verified saved-valid contract");choice=DialogResult.No;Call(form,"ShowHome");
                var bad=Open(form,new BadDirtyType());Call(form,"ShowHome");Check(Active(form)==bad,"invalid dirty property type fails closed");typeof(MainForm).GetField("activeModule",Hidden).SetValue(form,null);bad.Dispose();
                using(var unrelated=new Panel()){Call(form,"ShowEmbeddedModule",ModuleHost.DataBrowser,unrelated,null);Call(form,"ShowHome");Check(Active(form)==null,"unrelated module has no calibration save prompt");}

                var ready=new ReadyCalibration();SetCalibrationCache(form,ready);
                using(var page=new TestPage{SelectedPhaseCode="B"})
                {
                    int missingPrompts=0,navigations=0;bool displayedSimulation=false;string displayed="";
                    form.MissingCalibrationPrompt=(text,simulation)=>{missingPrompts++;displayed=text;displayedSimulation=simulation;return choice;};
                    form.CalibrationRequiredNavigation=()=>navigations++;
                    choice=DialogResult.No;Check(!Ready(form,page,false),"live No cannot bypass absent calibration");Check(!displayedSimulation&&displayed.Contains("B"),"live warning identifies selected phase");
                    Check(Ready(form,page,true)&&displayedSimulation,"only explicit simulation No permits demonstration");
                    choice=DialogResult.Cancel;Check(!Ready(form,page,true)&&!Ready(form,page,false),"Cancel always denies test entry");
                    choice=DialogResult.Yes;Check(!Ready(form,page,false)&&navigations==1,"Yes requests calibration instead of live test");
                    Check(!Ready(form,page,true)&&navigations==2,"simulation Yes also requests calibration first");
                    settings.TaskMinus3.UseFirstArrivalTiming=true;ready.Reason="";before=missingPrompts;Check(Ready(form,page,false),"verified applicable saved calibration satisfies entry check only");
                    Check(ready.LastPhase=="B"&&!ready.LastSimulation&&ready.LastFirstArrival&&missingPrompts==before,"phase simulation and first-arrival contract passed unchanged");
                    page.SelectedPhaseCode="";Check(Ready(form,page,true)&&ready.LastPhase=="A"&&ready.LastSimulation,"blank phase defaults to A with explicit simulation flag");
                    ready.Reason=null;choice=DialogResult.Cancel;Check(!Ready(form,page,false),"null readiness result is unknown, not a success claim");
                    ready.ThrowReadiness=true;Check(!Ready(form,page,false)&&!displayed.Contains("PRIVATE-NATIVE-PATH"),"readiness exception fails closed without native detail leak");
                    SetCalibrationCache(form,new UserControl());Check(!Ready(form,page,false),"missing readiness contract fails closed");
                    before=missingPrompts;Check(Ready(form,page,false,ModuleHost.CalibrationTest)&&missingPrompts==before,"calibration page itself is never blocked by missing calibration");
                }
            }
            if(args.Length>1)RealCalibrationIntegration(Path.GetFullPath(args[0]),Path.GetFullPath(args[1]));
            Console.WriteLine("CALIBRATION_NAVIGATION_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static object PublicCall(object owner,string name,params object[] args){return owner.GetType().GetMethod(name).Invoke(owner,args);}
    static void Set(object owner,string name,object value){owner.GetType().GetProperty(name).SetValue(owner,value,null);}
    static object Get(object owner,string name){return owner.GetType().GetProperty(name).GetValue(owner,null);}
    static void RealCalibrationIntegration(string release,string qa)
    {
        string dll=Path.Combine(release,"modules","CalibrationTest","Xiongxin.OWTS.CalibrationTest.dll");
        var type=Assembly.LoadFrom(dll).GetType("HVX.Controls.CalibrationProgramControl",true);
        Check(type.GetMethod("GetCalibrationReadiness")!=null,"release calibration includes persistence readiness contract");
        string project=Path.Combine(qa,"calibration-navigation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"Fixture project");
        File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n");
        var state=new ApplicationState();string error;Check(state.TryLoadProject(project,out error),"isolated QA project loads");
        var settings=new AppSettings{LanguageCode="en"};settings.Hardware.Enabled=false;settings.TaskMinus3.UseFirstArrivalTiming=true;
        using(var form=new MainForm(settings,state,true))
        using(var page=new TestPage{SelectedPhaseCode="ALL"})
        {
            form.UnsavedCalibrationPrompt=()=>DialogResult.Cancel;form.CalibrationNavigationError=text=>{};form.MissingCalibrationPrompt=(text,sim)=>DialogResult.Cancel;
            var cal=(Control)Activator.CreateInstance(type);Set(cal,"UseFirstArrivalTiming",true);Set(cal,"ProjectPath",project);Set(cal,"SimulationMode",true);
            PublicCall(cal,"SetPhaseParameters",-1,170d,170d,false,false);
            var wave=new double[2000];for(int i=0;i<wave.Length;i++)wave[i]=Math.Max(0,1-Math.Abs(i-300)/15d)+.4*Math.Max(0,1-Math.Abs(i-800)/18d);
            PublicCall(cal,"SetPhaseWaveform",-1,"5nC",wave,0d,4e-9);PublicCall(cal,"AutoAnalyzePhaseWaveform",-1,"5nC");
            Check((bool)Get(cal,"HasCalibrationData")&&(bool)Get(cal,"HasUnsavedCalibrationChanges"),"real simulated waveform remains unsaved");
            cal.Tag=ModuleHost.CalibrationTest;typeof(MainForm).GetField("activeModule",Hidden).SetValue(form,cal);Call(form,"ShowHome");Check(Active(form)==cal,"real calibration Cancel preserves waveform");
            string file=(string)Get(cal,"CalibrationFilePath");form.UnsavedCalibrationPrompt=()=>DialogResult.Yes;Call(form,"ShowHome");Check(Active(form)==null&&File.Exists(file),"host Yes writes real calibration archive before leaving");
            cal=(Control)Activator.CreateInstance(type);SetCalibrationCache(form,cal);
            bool reloaded=Ready(form,page,true);
            Check(reloaded,"host sets simulation and reloads persisted matching ALL calibration; project="+Get(cal,"ProjectPath")+"; simulation="+Get(cal,"SimulationMode")+"; error="+Get(cal,"LastCalibrationError")+"; readiness="+PublicCall(cal,"GetCalibrationReadiness","ALL",true,true));
            page.SelectedPhaseCode="A";Check(Ready(form,page,true),"equal-length phases may use the saved ALL calibration");page.SelectedPhaseCode="ALL";
            Check(!Ready(form,page,false),"simulation archive cannot certify real hardware");Check(Ready(form,page,true),"simulation context restores only its own saved archive");
            byte[] preserved=File.ReadAllBytes(file);File.WriteAllBytes(file,new byte[]{1,2,3});
            Check(!Ready(form,page,true),"corrupt calibration archive blocks test entry");File.WriteAllBytes(file,preserved);
            Check(Ready(form,page,true),"restored QA archive remains verifiable");
            settings.TaskMinus3.UseFirstArrivalTiming=false;Check(!Ready(form,page,true),"different timing method requires applicable saved calibration");
            settings.TaskMinus3.UseFirstArrivalTiming=true;
            File.Move(file,file+".qa-preserved");Check(!Ready(form,page,true),"removed archive cannot remain ready from memory");
            cal.Tag=ModuleHost.CalibrationTest;typeof(MainForm).GetField("activeModule",Hidden).SetValue(form,cal);form.UnsavedCalibrationPrompt=()=>DialogResult.No;
            Call(form,"ShowHome");Check(Active(form)==null&&!(bool)Get(cal,"HasCalibrationData"),"explicit discard clears real cached calibration data");
        }
    }
}
