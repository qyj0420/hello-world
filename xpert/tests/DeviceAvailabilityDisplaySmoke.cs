using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class DeviceAvailabilityDisplaySmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Assembly host;
    static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static object New(string name,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+name,true).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==args.Length).Invoke(args);}
    static object Field(object o,string name){return o.GetType().GetField(name,F).GetValue(o);}
    static void Field(object o,string name,object value){o.GetType().GetField(name,F).SetValue(o,value);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,F).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,F).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    [STAThread]static int Main(string[] args){try{
        host=Assembly.LoadFrom(Path.Combine(args[0],"Xiongxin.OWTS.Desktop.exe"));
        var settings=New("AppSettings");Set(settings,"LanguageCode","zh-CN");Set(Get(settings,"Hardware"),"Enabled",true);
        var state=New("ApplicationState");var ready=New("HardwareStatusSnapshot");
        foreach(string name in new[]{"HardwareEnabled","DriverInstalled","Connected","IdentityValid","FromLiveIntegrationProcess"})Set(ready,name,true);
        Set(ready,"StatusCode","Ready");Set(ready,"ControllerSerial","ART-OFFLINE-TEST");Set(ready,"AcquisitionSerial","PICO-OFFLINE-TEST");
        Set(ready,"ControllerInstanceId",@"USB\OFFLINE_ART\TEST");Set(ready,"AcquisitionInstanceId",@"USB\OFFLINE_PICO\TEST");Set(ready,"CheckedUtc",DateTime.UtcNow.AddHours(-2));
        Call(state,"SetHardware",ready);
        using(var form=(Form)New("MainForm",settings,state,true)){
            string expectedModules=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host.Location),"modules"));
            Check(String.Equals((string)Get(Field(form,"moduleHost"),"ModulesRoot"),expectedModules,StringComparison.OrdinalIgnoreCase),"verification uses candidate-side modules, not the default embedded cache");
            Check(!String.Equals(expectedModules,Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules")),StringComparison.OrdinalIgnoreCase),"assembly location is used rather than the smoke runner directory");
            Call(form,"RecreateModuleHost");
            Check(String.Equals((string)Get(Field(form,"moduleHost"),"ModulesRoot"),expectedModules,StringComparison.OrdinalIgnoreCase),"recreated verification host keeps explicit staged modules and bypasses cache extraction");
            form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
            Call(form,"RememberValidatedDevice",ready);int probes=0,refreshes=0,entries=0,notices=0;
            Field(form,"DevicePresentForTests",(Func<string,bool>)(id=>{probes++;return true;}));
            // Verification mode has no production refresh route. A refresh flag
            // becoming true would still catch an accidental navigation recheck.
            Field(form,"CalibrationNavigationForTests",(Action<bool>)(sim=>{Check(!sim,"transport cache allows page, not an HV permission");entries++;}));
            Field(form,"DeviceConnectionNoticeForTests",(Action<string>)(s=>notices++));
            string text=((Control)Field(form,"deviceDetail")).Text;
            Check(text.Contains("已识别")&&text.Contains("占用状态未确认")&&!text.StartsWith("已连接"),"transport presence is not mislabeled SDK availability");
            var success=(Color)host.GetType("Xiongxin.OWTS.Desktop.AppTheme").GetField("Success",F).GetValue(null);
            Check(((Control)Field(form,"deviceDot")).BackColor!=success,"no green availability lamp based on discovery only");
            var checkedUtc=(DateTime)Get(Get(state,"Hardware"),"CheckedUtc");
            long sequence=(long)Field(form,"navigationRequestSequence");
            for(int i=0;i<20;i++){Call(form,"BeginLiveCalibrationNavigation",sequence);if((bool)Field(form,"hardwareRefreshInProgress"))refreshes++;}
            Check(entries==20&&probes==40&&refreshes==0,"20 navigations only use exact passive node fixtures; zero SDK/connector calls");
            Check((DateTime)Get(Get(state,"Hardware"),"CheckedUtc")==checkedUtc,"reuse does not manufacture fresh inspection time");
            Field(form,"DevicePresentForTests",(Func<string,bool>)(id=>{throw new IOException("fixture-only query failure");}));
            Check(!(bool)Call(form,"TryReuseValidatedDevice")&&!(bool)Get(state,"DeviceConnected"),"failed node query invalidates historical Ready");
            Check((string)Get(Get(state,"Hardware"),"StatusCode")=="PresenceUnknown","unknown preserved rather than false disconnected/busy claim");
            for(int i=0;i<10;i++)Call(form,"PollHomeDeviceConnection");
            Call(form,"BeginLiveCalibrationNavigation",sequence);
            Check(!(bool)Field(form,"hardwareRefreshInProgress")&&entries==20&&notices==1,"unknown waits for explicit refresh, never silently probes on navigation");
            Check(((Control)Field(form,"deviceDetail")).Text.Contains("无法确认"),"unknown visible immediately");
            Set(settings,"LanguageCode","en");Call(state,"SetHardware",ready);Call(form,"ApplyLanguageAndBranding");
            Check(((Control)Field(form,"deviceDetail")).Text.Contains("Availability unconfirmed"),"English connection wording has same meaning");
        }
        var feedback=host.GetType("Xiongxin.OWTS.Desktop.PressurePostProcessFeedback",true);
        foreach(Exception error in new Exception[]{new IOException("PRIVATE-PATH"),new InvalidDataException("PRIVATE-PATH"),new UnauthorizedAccessException("PRIVATE-PATH"),new FileNotFoundException("PRIVATE-PATH"),new TargetInvocationException(new Exception("PRIVATE-PATH"))})
            foreach(bool saving in new[]{false,true})foreach(string lang in new[]{"zh-CN","zh-TW","en"}){
                string value=(string)feedback.GetMethod("Describe",F).Invoke(null,new object[]{error,saving,lang});
                Check(!value.Contains("PRIVATE-PATH")&&value.Length>20,"operation details stay in logs: "+error.GetType().Name+" "+lang);
                if(lang=="en")Check(value.All(c=>c<128),"English error feedback has no Chinese fragments");
            }
        Console.WriteLine("DEVICE_AVAILABILITY_DISPLAY_PASS="+checks+"; HardwareAccessed=false; DeviceProbes=fixtures-only");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
