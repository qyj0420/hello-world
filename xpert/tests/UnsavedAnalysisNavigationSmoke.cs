using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class UnsavedAnalysisNavigationSmoke
{
    static int checks;
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool value,string message) { checks++; if(!value) throw new Exception(message); }
    static object Call(object owner,string name,params object[] args)
    { return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args); }
    static Control Active(MainForm form)
    { return (Control)typeof(MainForm).GetField("activeModule",Hidden).GetValue(form); }
    sealed class Analysis : UserControl
    {
        public bool HasUnsavedAnalysisChanges { get; set; }
        public bool SaveResult,KeepDirty,Throw;
        public int Saves;
        public bool ShowSaveAnalysisDialog()
        {
            Saves++;
            if(Throw) throw new IOException("Fixture save failure");
            if(SaveResult && !KeepDirty) HasUnsavedAnalysisChanges=false;
            return SaveResult;
        }
    }
    sealed class FourWayAnalysis : UserControl
    {
        public bool HasUnsavedAnalysisChanges {get;set;}
        public Func<bool> Decision;
        public int Decisions;
        public bool ConfirmLeaveAnalysis(){Decisions++;return Decision==null?false:Decision();}
    }
    static Analysis Open(MainForm form,bool dirty)
    {
        var module=new Analysis();
        Call(form,"ShowEmbeddedModule",ModuleHost.DataAnalysis,module,null);
        Check(Active(form)==module,"analysis page active");
        module.HasUnsavedAnalysisChanges=dirty;
        return module;
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            var settings=new AppSettings{LanguageCode="zh-TW"};settings.Hardware.Enabled=false;
            using(var form=new MainForm(settings,new ApplicationState(),true))
            {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                int prompts=0,errors=0;DialogResult choice=DialogResult.Cancel;
                form.UnsavedAnalysisPrompt=()=>{prompts++;return choice;};
                form.AnalysisNavigationError=text=>{errors++;Check(!String.IsNullOrWhiteSpace(text),"localized save error");};
                var analysis=Open(form,false);Call(form,"ShowHome");
                Check(prompts==0 && Active(form)==null && analysis.IsDisposed,"clean analysis leaves without prompt");
                analysis=Open(form,true);Call(form,"ShowHome");
                Check(prompts==1 && Active(form)==analysis && !analysis.IsDisposed && analysis.Saves==0,"cancel keeps analysis");
                choice=DialogResult.Yes;Call(form,"ShowHome");
                Check(analysis.Saves==1 && Active(form)==analysis && analysis.HasUnsavedAnalysisChanges,"cancelled naming/failed write keeps changes");
                analysis.SaveResult=true;analysis.KeepDirty=true;Call(form,"ShowHome");
                Check(Active(form)==analysis && !analysis.IsDisposed,"success flag alone cannot discard dirty analysis");
                analysis.Throw=true;Call(form,"ShowHome");
                Check(errors==1 && Active(form)==analysis,"save exception keeps page");
                analysis.Throw=false;analysis.KeepDirty=false;Call(form,"ShowHome");
                Check(Active(form)==null && analysis.IsDisposed,"confirmed saved snapshot returns home");
                analysis=Open(form,true);choice=DialogResult.No;Call(form,"ShowHome");
                Check(analysis.Saves==0 && analysis.IsDisposed && Active(form)==null,"explicit discard leaves without save");
                analysis=Open(form,true);choice=DialogResult.Cancel;
                using(var destination=new Panel())
                {
                    Call(form,"ShowEmbeddedModule",ModuleHost.DataBrowser,destination,null);
                    Check(Active(form)==analysis && !analysis.IsDisposed,"switching page cannot bypass prompt");
                }
                form.Close();
                Check(!form.IsDisposed && Active(form)==analysis,"window close cancellation preserves analysis");
                form.UnsavedAnalysisPrompt=()=>{
                    Call(form,"ShowHome");Check(Active(form)==analysis,"reentrant navigation blocked");
                    return DialogResult.Cancel;
                };
                Call(form,"ShowHome");Check(Active(form)==analysis,"outer cancellation remains active");
                form.UnsavedAnalysisPrompt=()=>DialogResult.No;form.Close();
                Check(!form.IsDisposed && Active(form)==null && analysis.IsDisposed,"child close returns home after explicit discard");
                using(var nonAnalysis=new Panel())
                {
                    Call(form,"ShowEmbeddedModule",ModuleHost.DataBrowser,nonAnalysis,null);
                    int before=prompts;Call(form,"ShowHome");
                    Check(Active(form)==null && prompts==before,"unrelated page does not prompt analysis save");
                }
                // Construction and all event pumping remain in verification mode.
                // Only this synchronous, hardware-free decision is exercised as a
                // production call; the fake module never opens an actual dialog.
                var modern=new FourWayAnalysis{HasUnsavedAnalysisChanges=true};
                Call(form,"ShowEmbeddedModule",ModuleHost.DataAnalysis,modern,null);
                form.UnsavedAnalysisPrompt=null;
                var verification=typeof(MainForm).GetField("verificationMode",Hidden);
                try {
                    verification.SetValue(form,false);
                    modern.Decision=()=>false;
                    Check(!(bool)Call(form,"CanLeaveActiveAnalysis")&&modern.Decisions==1,"new module cancellation/failure blocks host navigation");
                    modern.Decision=()=>true;
                    Check((bool)Call(form,"CanLeaveActiveAnalysis")&&modern.Decisions==2,"new module explicit discard permits exit despite dirty memory");
                    modern.Decision=()=>{throw new IOException("modern save failure");};
                    int priorErrors=errors;
                    Check(!(bool)Call(form,"CanLeaveActiveAnalysis")&&errors==priorErrors+1,"new module save exception remains in analysis");
                    modern.Decision=()=>{Check(!(bool)Call(form,"CanLeaveActiveAnalysis"),"new module reentrant decision denied");return false;};
                    Check(!(bool)Call(form,"CanLeaveActiveAnalysis"),"new module outer cancellation remains denied");
                    modern.HasUnsavedAnalysisChanges=false;int priorDecisions=modern.Decisions;
                    Check((bool)Call(form,"CanLeaveActiveAnalysis")&&modern.Decisions==priorDecisions,"new module unchanged analysis never prompts");
                } finally { verification.SetValue(form,true);modern.HasUnsavedAnalysisChanges=false;Call(form,"ShowHome"); }
            }
            Console.WriteLine("UNSAVED_ANALYSIS_NAVIGATION_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }
}
