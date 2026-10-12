using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class StandalonePressureShellSmoke
{
    static int checks;
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Field(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,Hidden).Invoke(o,a);}
    public sealed class BusyPressure:UserControl
    {
        public bool IsBusy {get;set;}
        public bool PressureNavigationLocked{get{return IsBusy;}}
        public bool HasActiveHardwareSession{get{return false;}}
        public bool ActionPending{get{return false;}}
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));
            var st=assembly.GetType("Xiongxin.OWTS.Desktop.AppSettings",true);
            var at=assembly.GetType("Xiongxin.OWTS.Desktop.ApplicationState",true);
            var ft=assembly.GetType("Xiongxin.OWTS.Desktop.MainForm",true);
            var settings=Activator.CreateInstance(st);var state=Activator.CreateInstance(at);
            var hardware=st.GetProperty("Hardware").GetValue(settings,null);
            Check(!(bool)hardware.GetType().GetProperty("Enabled").GetValue(hardware,null),"fake shell starts hardware disabled");
            string project=Path.Combine(Path.GetFullPath(args[1]),"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"始端地点\tOffline\r\n终端地点\tFixture\r\n电缆长度\t400\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t400\r\n",Encoding.UTF8);
            var constructor=ft.GetConstructor(Hidden,null,new[]{st,at,typeof(bool),typeof(bool)},null);
            using(var form=(Form)constructor.Invoke(new[]{settings,state,(object)true,(object)true})) {
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                Check((bool)ft.GetProperty("StandalonePressureMode",Hidden).GetValue(form,null),"explicit standalone shell");
                var button=(Button)Field(form,"standaloneLoadProjectButton");Check(button.Visible&&button.Text.Length>0,"load project action present");
                var debug=(Button)Field(form,"standaloneDebugButton");Check(debug.Visible,"read-only debug action present");
                var debugLog=Activator.CreateInstance(assembly.GetType("Xiongxin.OWTS.Desktop.Diagnostics",true),new object[]{Path.Combine(args[1],"debug-logs")});
                var debugCtor=assembly.GetType("Xiongxin.OWTS.Desktop.PressureDebugForm",true).GetConstructors(Hidden)[0];
                using(var existing=(Form)debugCtor.Invoke(new object[]{debugLog,(Func<string>)(()=>"offline"),form.Icon})) {
                    // An already visible window must be raised, never shown a
                    // second time (WinForms rejects Show(owner) when visible).
                    existing.StartPosition=FormStartPosition.Manual;existing.Location=new Point(-30000,-30000);
                    existing.Show(form);ft.GetField("pressureDebugWindow",Hidden).SetValue(form,existing);
                    debug.PerformClick();debug.PerformClick();Check(existing.Visible&&!existing.IsDisposed,"repeated debug clicks retain the same window");
                    existing.Close();
                }
                var tiles=(IDictionary)Field(form,"tileVisibility");int visible=0;
                foreach(DictionaryEntry row in tiles)if((bool)row.Value){visible++;Check((string)row.Key=="PressureTest","no unrelated module entry");}
                Check(visible==1,"one pressure entry");
                var logo=(PictureBox)Field(form,"brandLogo");var title=(Panel)Field(form,"titleArea");
                Check(logo.Image==null&&logo.Width==0&&!logo.Visible&&title.Padding.Left==0,"no blank neutral logo reservation");
                int warnings=0;ft.GetField("DeviceConnectionNoticeForTests",Hidden).SetValue(form,new Action<string>(s=>warnings++));
                var busy=new BusyPressure{Tag="PressureTest",IsBusy=true};ft.GetField("activeModule",Hidden).SetValue(form,busy);
                Check(!(bool)Call(form,"LoadStandalonePressureProject",project)&&warnings==1,"load blocked while pressure active");
                Check(Object.ReferenceEquals(Field(form,"activeModule"),busy),"busy page retained");
                busy.IsBusy=false;
                Check(!(bool)Call(form,"LoadStandalonePressureProject",project+"-missing"),"invalid project rejected");
                Check(Object.ReferenceEquals(Field(form,"activeModule"),busy),"invalid folder does not discard page");
                var load=(Task<bool>)Call(form,"LoadStandalonePressureProjectAsync",project);
                var wait=System.Diagnostics.Stopwatch.StartNew();
                while(!load.IsCompleted){Application.DoEvents();Thread.Sleep(1);if(wait.ElapsedMilliseconds>5000)throw new TimeoutException("fake page release timed out");}
                Check(load.GetAwaiter().GetResult(),"valid real-format fixture loads only after awaited page release");
                Check((string)at.GetProperty("ProjectPath").GetValue(state,null)==project,"selected folder is the sole project context");
                Check(Field(form,"activeModule")==null&&busy.IsDisposed,"old page released before new project context");
                Check(!(bool)Field(form,"hardwareRefreshInProgress"),"project loading does not start hardware discovery in verification mode");
                Check(!((Timer)Field(form,"devicePollTimer")).Enabled,"no real device poll in test");
                form.Close();
            }
            Console.WriteLine("STANDALONE_PRESSURE_SHELL_PASS="+checks+"; verification-mode host and fake page; no product entry or SDK");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
