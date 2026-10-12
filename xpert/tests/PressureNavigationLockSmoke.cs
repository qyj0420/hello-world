using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Compile with all src/*.cs and /main:PressureNavigationLockSmoke.
// Only managed probe controls and an isolated Program fixture are used. The
// verification-mode shell never starts a real connector, SDK, worker or socket.
internal static class PressureNavigationLockSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks; static string qa,project;
    static void Check(bool b,string message){checks++;if(!b)throw new Exception(message);}
    static object Field(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,Hidden).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,Hidden).Invoke(o,a);}
    class LegacyProbe : UserControl
    {
        internal string ThrowFlag; internal int Stops,DisposeCalls;
        internal bool Busy,Hardware,Pending;
        protected bool Flag(string name,bool value){if(ThrowFlag==name)throw new InvalidDataException("injected unavailable pressure state");return value;}
        public bool IsBusy {get{return Flag("IsBusy",Busy);}}
        public bool HasActiveHardwareSession {get{return Flag("HasActiveHardwareSession",Hardware);}}
        public bool ActionPending {get{return Flag("ActionPending",Pending);}}
        public string PressureStateCode {get{return "WAITING";}}
        public bool RequestHardwareStop(){Stops++;return true;}
        protected override void Dispose(bool disposing){if(disposing)DisposeCalls++;base.Dispose(disposing);}
    }
    sealed class Probe : LegacyProbe
    {
        internal bool NavigationLocked;
        public bool PressureNavigationLocked {get{return Flag("PressureNavigationLocked",NavigationLocked);}}
    }
    static AppSettings Settings(){var s=new AppSettings{LanguageCode="zh-CN"};s.Hardware.Enabled=false;return s;}
    static ApplicationState State(){var s=new ApplicationState();string e;Check(s.TryLoadProject(project,out e),"isolated project loads: "+e);return s;}
    static void Attach(MainForm form,LegacyProbe p,bool active)
    {
        p.Tag=ModuleHost.PressureTest;var host=(ModuleHost)Field(form,"moduleHost");
        Set(host,"diagnostics",new Diagnostics(Path.Combine(qa,"diagnostics")));
        ((Dictionary<string,Control>)Field(host,"controls"))[ModuleHost.PressureTest]=p;
        if(active)Set(form,"activeModule",p);
    }
    static void LockedCase(string label,LegacyProbe p)
    {
        var state=State();using(p)using(var form=new MainForm(Settings(),state,true))using(var next=new UserControl())
        {
            Attach(form,p,true);var messages=new List<string>();form.DeviceConnectionNoticeForTests=messages.Add;
            form.ProjectReleaseError=messages.Add;
            int disposed=p.DisposeCalls;
            Check(!(bool)Call(form,"CanLeaveActivePressure"),label+": explicit guard refuses");
            Call(form,"ShowHome");
            Check(ReferenceEquals(Field(form,"activeModule"),p)&&!p.IsDisposed&&state.ProjectLoaded,label+": home keeps current page and project");
            Check(p.Stops==0&&p.DisposeCalls==disposed,label+": home cannot send stop or dispose current round");
            Call(form,"ShowEmbeddedModule",ModuleHost.DataBrowser,next,(bool?)null);
            Check(ReferenceEquals(Field(form,"activeModule"),p)&&next.Parent==null,label+": direct module switch cannot bypass guard");
            var closing=new FormClosingEventArgs(CloseReason.UserClosing,false);
            Call(form,"MainFormClosing",form,closing);
            Check(closing.Cancel&&ReferenceEquals(Field(form,"activeModule"),p),label+": form close retains pressure page");
            Check(!form.TryReleaseCurrentProject()&&state.ProjectLoaded&&ReferenceEquals(Field(form,"activeModule"),p),label+": release cannot bypass lock");
            Check(p.Stops==0&&p.DisposeCalls==disposed,label+": all rejected navigation remains non-interrupting");
            if(p.ThrowFlag==null)Check(messages.Count>0&&messages.Any(x=>x.Contains("不能返回主页")||x.Contains("正在采集")),label+": actionable notice is supplied");
        }
    }
    static void CachedSessionBlocksReleaseAndDisconnect()
    {
        var state=State();var settings=Settings();settings.Hardware.Enabled=true;
        using(var p=new Probe{NavigationLocked=true})using(var form=new MainForm(settings,state,true))
        {
            Attach(form,p,false);int disconnects=0,prompts=0;form.DeviceConnectionNoticeForTests=x=>{};form.ProjectReleaseError=x=>{};
            form.ConfirmDeviceDisconnectForTests=()=>{prompts++;return true;};
            form.DisconnectDeviceForTests=()=>{disconnects++;return Task.FromResult(new HardwareStatusSnapshot());};
            Check(!form.TryReleaseCurrentProject()&&state.ProjectLoaded&&!p.IsDisposed,"inactive locked pressure still blocks project release");
            Check(!form.TryDisconnectCurrentDevice().GetAwaiter().GetResult(),"inactive locked pressure blocks device disconnect");
            Check(disconnects==0&&prompts==0&&p.Stops==0,"blocked release/disconnect cannot send worker/stop commands or imply consent");
        }
    }
    static void IdleNavigation()
    {
        var state=State();using(var p=new Probe())using(var form=new MainForm(Settings(),state,true))
        {
            Attach(form,p,true);int notices=0;form.DeviceConnectionNoticeForTests=x=>notices++;
            Check((bool)Call(form,"CanLeaveActivePressure"),"idle current-pressure guard permits home");
            Call(form,"ShowHome");
            Check(Field(form,"activeModule")==null&&p.IsDisposed&&state.ProjectLoaded,"idle home disposes page but retains project");
            Check(p.Stops==0&&notices==0,"idle home never requests hardware stop");
        }
        state=State();using(var p=new LegacyProbe())using(var form=new MainForm(Settings(),state,true))
        {
            Attach(form,p,true);Check(form.TryReleaseCurrentProject()&&!state.ProjectLoaded&&p.IsDisposed,"idle legacy pressure can release project normally");
            Check(p.Stops==0,"idle project release does not manufacture a stop request");
        }
        state=State();using(var p=new Probe{NavigationLocked=true})using(var form=new MainForm(Settings(),state,true))
        {
            Attach(form,p,true);form.DeviceConnectionNoticeForTests=x=>{};
            Call(form,"ShowHome");Check(ReferenceEquals(Field(form,"activeModule"),p),"AUTO/countdown lock blocks even when other flags false");
            p.NavigationLocked=false;Call(form,"ShowHome");
            Check(Field(form,"activeModule")==null&&p.IsDisposed&&p.Stops==0,"only explicit finish and lock release enables later home");
        }
    }
    static Dictionary<string,string> Hashes()
    {using(var hash=SHA256.Create())return Directory.GetFiles(project,"*",SearchOption.AllDirectories).ToDictionary(x=>x,x=>Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(x))));}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            qa=Path.Combine(Path.GetFullPath(args[0]),"pressure-navigation-"+Guid.NewGuid().ToString("N").Substring(0,10));Directory.CreateDirectory(qa);
            project=(string)typeof(Program).GetMethod("CreateFixtureProject",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{qa});
            var original=Hashes();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            LockedCase("AUTO workflow lock",new Probe{NavigationLocked=true});
            LockedCase("countdown lock",new Probe{NavigationLocked=true});
            LockedCase("busy acquisition",new Probe{Busy=true});
            LockedCase("worker still owns device",new Probe{Hardware=true});
            LockedCase("pending action",new Probe{Pending=true});
            LockedCase("legacy busy",new LegacyProbe{Busy=true});
            LockedCase("legacy worker",new LegacyProbe{Hardware=true});
            LockedCase("legacy pending",new LegacyProbe{Pending=true});
            foreach(string flag in new[]{"PressureNavigationLocked","IsBusy","HasActiveHardwareSession","ActionPending"})
                LockedCase("throwing "+flag,new Probe{ThrowFlag=flag});
            CachedSessionBlocksReleaseAndDisconnect();IdleNavigation();
            var final=Hashes();Check(original.Count==final.Count&&original.All(x=>final.ContainsKey(x.Key)&&final[x.Key]==x.Value),"all navigation tests preserve fixture project bytes");
            Console.WriteLine("PRESSURE_NAVIGATION_LOCK_PASS="+checks+";hardware=false;network=false;QA="+qa);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
