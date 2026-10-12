using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
internal static class CalibrationResidentNavigationSmoke
{
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args);}
    static Control Active(MainForm form){return (Control)typeof(MainForm).GetField("activeModule",Hidden).GetValue(form);}
    sealed class Calibration:UserControl
    {
        internal bool Owned=true,Reusable=true,Fail,IdleCleanupOnly;
        internal int Releases;
        internal readonly TaskCompletionSource<bool> Gate=new TaskCompletionSource<bool>();
        public bool HasActiveHardwareSession{get{return Owned;}}
        public bool HasReusableHardwareSession{get{return Reusable;}}
        public bool CanReleaseHardwareSession{get{return IdleCleanupOnly;}}
        public bool HasUnsavedCalibrationChanges{get{return false;}}
        public bool HasCalibrationData{get{return false;}}
        public async Task<bool> ReleaseHardwareSessionAsync(){Releases++;Reusable=false;await Gate.Task;if(Fail)throw new Exception("PRIVATE-NATIVE-PATH");Owned=false;return true;}
    }
    static Calibration Open(MainForm form)
    {
        var c=new Calibration();Call(form,"ShowEmbeddedModule",ModuleHost.CalibrationTest,c,(bool?)false);
        Check(Active(form)==c,"fake calibration opened without capture backend");return c;
    }
    static async Task Exercise()
    {
        var settings=new AppSettings{LanguageCode="zh-CN"};settings.Hardware.Enabled=false;
        using(var form=new MainForm(settings,new ApplicationState(),true))
        {
            form.ShowInTaskbar=false;form.Opacity=0;form.Show();
            int notices=0;form.DeviceConnectionNoticeForTests=text=>{notices++;Check(!text.Contains("PRIVATE-NATIVE-PATH"),"release error is user-facing not native exception");};
            var c=Open(form);Call(form,"ShowHome");
            Check(c.Releases==1&&Active(form)==c&&!c.IsDisposed,"home waits for owned native release acknowledgement");
            Call(form,"ShowHome");Check(c.Releases==1,"second home click cannot start duplicate release");
            c.Gate.SetResult(true);await Task.Delay(30);
            Check(Active(form)==null&&c.IsDisposed,"home only after release completed");
            c=Open(form);c.Fail=true;Call(form,"ShowHome");c.Gate.SetResult(true);await Task.Delay(30);
            Check(notices==1&&Active(form)==c&&!c.IsDisposed&&c.Owned,"release failure leaves page and ownership intact");
            c.Reusable=true;c.Fail=false;Call(form,"ShowHome");await Task.Delay(30);
            Check(c.Releases==2&&c.IsDisposed&&Active(form)==null,"explicit retry can leave after successful release");
            c=Open(form);c.Reusable=false;Call(form,"ShowHome");
            Check(c.Releases==0&&Active(form)==c,"running state cannot be mistaken for safely releasable idle");
            c.Owned=false;Call(form,"ShowHome");
            Check(c.IsDisposed,"fixture release completed without invoking hardware");
            c=Open(form);c.Reusable=false;c.IdleCleanupOnly=true;c.Fail=true;Call(form,"ShowHome");
            Check(c.Releases==1&&Active(form)==c&&!c.IsDisposed,"idle failed DI owner permits explicit Home cleanup without claiming it is reusable");
            c.Gate.SetResult(true);await Task.Delay(30);
            Check(notices==2&&c.Owned&&!c.IsDisposed&&Active(form)==c,"failed DI cleanup still retains ownership and page");
            c.Fail=false;Call(form,"ShowHome");await Task.Delay(30);
            Check(c.Releases==2&&c.IsDisposed&&Active(form)==null,"explicit successful Home cleanup retry is not a navigation deadlock");
        }
    }
    [STAThread]static int Main()
    {
        Exception failure=null;Application.EnableVisualStyles();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0})
        {
            pump.Shown+=async delegate{try{await Exercise();Console.WriteLine("CALIBRATION_RESIDENT_NAVIGATION_PASS="+checks+"; verification-mode host and fake page only; no SDK or product entry.");}catch(Exception error){failure=error;Console.Error.WriteLine(error);}finally{pump.Close();}};
            Application.Run(pump);
        }
        return failure==null?0:1;
    }
}
