using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class ModuleNavigationPerformanceSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static object Field(object value,string name){return value.GetType().GetField(name,Hidden).GetValue(value);}
    static void Set(object value,string name,object item){value.GetType().GetField(name,Hidden).SetValue(value,item);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,Hidden).Invoke(value,args);}
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static Dictionary<string,string> Hashes(string root)
    {using(var sha=SHA256.Create())return Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(p))));}
    sealed class Events
    {
        public int Layouts,Resizes;public bool Trace;
        public void Reset(){Layouts=Resizes=0;}
        public void Watch(Control value,bool recursive)
        {value.Layout+=(s,e)=>{Layouts++;if(Trace)Console.WriteLine("LAYOUT "+value.Name+"/"+value.GetType().Name+" "+e.AffectedProperty+" "+(e.AffectedControl==null?"":e.AffectedControl.Name+"/"+e.AffectedControl.GetType().Name));};value.SizeChanged+=(s,e)=>Resizes++;if(recursive)foreach(Control child in value.Controls)Watch(child,true);}
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"navigation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string fixture=(string)typeof(Program).GetMethod("CreateFixtureProject",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{qa});
            var original=Hashes(fixture);var settings=new AppSettings{DataRoot=qa,EnableAiFeatures=false};settings.Hardware.Enabled=false;
            var state=new ApplicationState();string error;Check(state.TryLoadProject(fixture,out error),"fixture loads");
            var analysis=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            analysis.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"analysis-preferences.json")));
            var rows=new StringBuilder("round,module,initialize_ms,present_ms,home_ms,host_layouts,host_resizes,module_tree_layouts,module_tree_resizes,home_layouts,home_resizes,idle_repeat_layouts\r\n");
            using(var form=new MainForm(settings,state,true))
            {
                ((ModuleHost)Field(form,"moduleHost")).Dispose();
                var moduleHost=new ModuleHost(settings,state,new Diagnostics(Path.Combine(qa,"diagnostics")),Path.Combine(build,"modules"));moduleHost.AuditEnabled=false;Set(form,"moduleHost",moduleHost);
                form.ShowInTaskbar=false;form.Opacity=0;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-20000,-20000);form.ClientSize=new Size(1680,950);form.Show();Application.DoEvents();
                var shell=new Events();shell.Watch(form,false);
                foreach(string name in new[]{"rootLayout","headerPanel","titleArea","headerActions","commandBarPanel","contentPanel","modulePanel","modulePageHeader","homePanel","tilePanel"})shell.Watch((Control)Field(form,name),false);
                for(int round=0;round<2;round++)foreach(string id in new[]{ModuleHost.ProjectManagement,ModuleHost.ProjectCreation,ModuleHost.DataBrowser,ModuleHost.DataAnalysis,ModuleHost.ReportGenerator,ModuleHost.CalibrationTest,ModuleHost.PressureTest})
                {
                    Console.WriteLine("NAV_STAGE="+round+":"+id+":create");
                    var clock=Stopwatch.StartNew();Control page;Check(moduleHost.TryCreateControl(id,out page,out error)&&page!=null,"real module loads: "+id+" "+error);clock.Stop();double init=clock.Elapsed.TotalMilliseconds;
                    foreach(string promptName in new[]{"PromptAnalysisOnLoad","PromptSavedAnalysisOnProjectLoad"}){var prompt=page.GetType().GetProperty(promptName);if(prompt!=null&&prompt.CanWrite)prompt.SetValue(page,false,null);}
                    var pageEvents=new Events();pageEvents.Watch(page,true);shell.Reset();
                    Console.WriteLine("NAV_STAGE="+round+":"+id+":present");
                    clock.Restart();Call(form,"ShowEmbeddedModule",id,page,(bool?)false);Application.DoEvents();clock.Stop();double present=clock.Elapsed.TotalMilliseconds;
                    int layouts=shell.Layouts,resizes=shell.Resizes,pageLayouts=pageEvents.Layouts,pageResizes=pageEvents.Resizes;
                    Check(layouts<=25,"bounded shell presentation layout count: "+id+" / "+layouts);
                    Check(page.Visible&&page.Parent!=null&&!page.IsDisposed,"module displayed: "+id);Check(page.Bounds==page.Parent.DisplayRectangle,"module fills final viewport: "+id);
                    Check(Field(form,"moduleLoadingCover")==null&&(int)Field(form,"modulePresentationDepth")==0,"balanced presentation: "+id);
                    shell.Reset();shell.Trace=args.Length>2&&args[2]=="trace";for(int i=0;i<5;i++)Call(form,"ApplyResponsiveChrome");shell.Trace=false;int idle=shell.Layouts;
                    Check(idle==0,"unchanged chrome update performs no layout: "+id+" / "+idle+" / DPI "+form.DeviceDpi);
                    Console.WriteLine("NAV_STAGE="+round+":"+id+":home");
                    shell.Reset();clock.Restart();Call(form,"ShowHome");Application.DoEvents();clock.Stop();
                    Check(page.IsDisposed,"leave-page disposal contract unchanged: "+id);
                    Check(shell.Layouts<=25,"bounded return-home layout count: "+id+" / "+shell.Layouts);
                    string row=String.Join(",",new[]{round.ToString(),id,init.ToString("F3",CultureInfo.InvariantCulture),present.ToString("F3",CultureInfo.InvariantCulture),clock.Elapsed.TotalMilliseconds.ToString("F3",CultureInfo.InvariantCulture),layouts.ToString(),resizes.ToString(),pageLayouts.ToString(),pageResizes.ToString(),shell.Layouts.ToString(),shell.Resizes.ToString(),idle.ToString()});rows.AppendLine(row);Console.WriteLine(row);
                }
                Check(!state.Hardware.HardwareEnabled,"all navigation stayed offline");
                form.Close();
            }
            var final=Hashes(fixture);Check(original.Count==final.Count&&original.All(pair=>final.ContainsKey(pair.Key)&&final[pair.Key]==pair.Value),"navigation preserves project fixture bytes");
            File.WriteAllText(Path.Combine(qa,"navigation.csv"),rows.ToString());
            Console.WriteLine("MODULE_NAVIGATION_PASS="+checks+";HARDWARE_COMMANDS=0;PROJECT_BYTES_UNCHANGED=True;QA="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
