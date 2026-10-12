using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
class ModulePresentationSmoke
{
    static int count;
    static readonly BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static object Field(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,Hidden).Invoke(o,a);}
    static void Check(bool v,string m){if(!v)throw new Exception(m);count++;}
    sealed class Probe:UserControl
    {
        public int FirstVisibleWidth;public int FinalSurfaceWidth;public bool ParentAtFirstVisible;
        protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible&&Parent!=null&&FirstVisibleWidth==0){FirstVisibleWidth=Width;FinalSurfaceWidth=Parent.DisplayRectangle.Width;ParentAtFirstVisible=true;}}
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            var settings=new AppSettings();settings.Hardware.Enabled=false;
            using(var form=new MainForm(settings,new ApplicationState(),true)){
                form.ShowInTaskbar=false;form.Opacity=0;form.Show();Application.DoEvents();
                var outer=(IDisposable)Call(form,"BeginModulePresentation");
                var first=Field(form,"moduleLoadingCover");
                using((IDisposable)Call(form,"BeginModulePresentation")){Check(Object.ReferenceEquals(first,Field(form,"moduleLoadingCover")),"nested scope reuses cover");}
                Check(Field(form,"moduleLoadingCover")!=null,"inner dispose retains cover");outer.Dispose();outer.Dispose();
                Check(Field(form,"moduleLoadingCover")==null,"idempotent release");
                try{using((IDisposable)Call(form,"BeginModulePresentation")){throw new IOException("fixture");}}catch(IOException){}
                Check(Field(form,"moduleLoadingCover")==null,"exception releases cover");
                var chrome=(IDisposable)Call(form,"BeginChromeLayout");
                using((IDisposable)Call(form,"BeginChromeLayout")){Check((int)Field(form,"chromeLayoutDepth")==2,"nested chrome layout balanced");}
                chrome.Dispose();chrome.Dispose();Check((int)Field(form,"chromeLayoutDepth")==0,"chrome release idempotent");
                try{using((IDisposable)Call(form,"BeginChromeLayout")){throw new IOException("fixture");}}catch(IOException){}
                Check((int)Field(form,"chromeLayoutDepth")==0,"exception releases chrome layout scope");
                var shell=(Control)Field(form,"rootLayout");int resumed=0;LayoutEventHandler fail=(s,e)=>{throw new IOException("fixture layout failure");};
                var failing=(IDisposable)Call(form,"BeginChromeLayout");shell.Layout+=fail;shell.PerformLayout();
                bool rejected=false;try{failing.Dispose();}catch(InvalidOperationException){rejected=true;}finally{shell.Layout-=fail;}
                Check(rejected&&(int)Field(form,"chromeLayoutDepth")==0,"layout callback failure still releases every scope");
                LayoutEventHandler observe=(s,e)=>resumed++;shell.Layout+=observe;shell.PerformLayout();shell.Layout-=observe;
                Check(resumed>0,"shell remains layout-responsive after failed page");
                foreach(var size in new[]{new Size(1280,800),new Size(1920,1200)}){
                    form.Size=size;
                    var page=new Probe();Call(form,"ShowEmbeddedModule",ModuleHost.ProjectCreation,page,(bool?)false);
                    Check(page.ParentAtFirstVisible,"first visibility has parent");
                    Check(page.FirstVisibleWidth==page.FinalSurfaceWidth,"first visible at final surface width");
                    Check(page.Visible&&page.Width==page.Parent.DisplayRectangle.Width,"final page fills viewport");
                    Check(Field(form,"moduleLoadingCover")==null,"successful navigation removes cover");
                    Check((int)Field(form,"modulePresentationDepth")==0,"balanced navigation scopes");
                    Call(form,"ShowHome");Check(page.IsDisposed,"no stale module retained");
                }
            }
            Console.WriteLine("MODULE_PRESENTATION_PASS="+count+"; HARDWARE_COMMANDS=0");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
