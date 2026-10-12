using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class DeviceManagerWorkflowSmoke
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool pass,string text){checks++;if(!pass)throw new Exception(text);}
    static object Call(object f,string method,params object[] args){return f.GetType().GetMethod(method,Flags).Invoke(f,args);}
    static object Field(object f,string name){return f.GetType().GetField(name,Flags).GetValue(f);}
    static void Pump(){for(int i=0;i<30;i++){Application.DoEvents();System.Threading.Thread.Sleep(5);}}
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        var settings=new AppSettings{DataRoot=args[0]};var state=new ApplicationState();
        using(var f=new MainForm(settings,state,true)){
            f.ShowInTaskbar=false;f.Opacity=0;f.Location=new Point(-20000,-20000);f.Show();Application.DoEvents();
            var done=new TaskCompletionSource<bool>();int launches=0,scans=0;string notice="";Action notifyStarted=null;
            var log=new Diagnostics(Path.Combine(args[0],"device-manager-logs"));f.DeviceManagerDiagnosticsForTests=log;
            f.RunDeviceManagerWithStartForTests=started=>{launches++;notifyStarted=started;return done.Task;};
            f.DeviceConnectionNoticeForTests=x=>{notice=x;
                Check(!(bool)Field(f,"deviceManagerOpening"),"failure notice is shown only after the opening latch is restored");
                Check(!((Button)Field(f,"refreshDeviceButton")).Text.Contains("已打开"),"failure notice never retains the falsely-opened label");};
            f.DiscoverDetectorsForTests=token=>{scans++;throw new IOException("fake discovery failed");};
            Check(((Button)Field(f,"refreshDeviceButton")).Text=="管理设备","manage label");
            Call(f,"ManageInstalledDetectionDevices");
            Check(launches==1&&scans==0,"opens driver directly without discovery");
            Check(((Button)Field(f,"refreshDeviceButton")).Text=="正在打开设备管理…"&&state.Hardware.StatusCode!="DeviceManagement","pending verification/start is not an opened process");
            Check(!f.CanRefreshDetectionDevices(),"no competing discovery during manager");
            Call(f,"ManageInstalledDetectionDevices");Check(launches==1,"duplicate click rejected");
            Check(!state.DeviceConnected,"manager opening does not assert Ready");
            Call(f,"PollHomeDeviceConnection");Check(scans==0,"home polling paused");
            Task.Run(notifyStarted).GetAwaiter().GetResult();Pump();
            Check(((Button)Field(f,"refreshDeviceButton")).Text=="设备管理已打开…"&&state.Hardware.StatusCode=="DeviceManagement","only successful managed start notification publishes open state");
            done.SetResult(true);Pump();
            Check(scans==1,"fresh readback follows driver exit");
            Check(!state.DeviceConnected,"zero driver exit cannot fabricate ready");
            Check(notice.Contains("fake discovery failed"),"post-manager failure visible");
            Check(!(bool)Field(f,"deviceManagerOpening"),"manager latch released");
            DateTime now=DateTime.UtcNow;f.HomeConnectionUtcForTests=()=>now;
            f.GetType().GetField("homeConnectionRetryStep",Flags).SetValue(f,1);
            Call(f,"PublishHardwareInspection",new HardwareStatusSnapshot{StatusCode="Offline",HardwareEnabled=true},false);
            Check((DateTime)Field(f,"homeConnectionNotBeforeUtc")==now.AddSeconds(2),"backoff measured from completion");
            f.RunDeviceManagerWithStartForTests=started=>{throw new OperationCanceledException("fake UAC cancel");};
            Call(f,"ManageInstalledDetectionDevices");Pump();Check(scans==1,"cancel does not run discovery");
            Check(!(bool)Field(f,"deviceManagerOpening"),"cancel releases UI latch");
            Check(notice.Contains("fake UAC cancel")&&File.ReadAllText(log.CurrentLogPath).Contains("fake UAC cancel"),"actual failure reason is retained in the isolated internal log");
            Check(File.ReadAllText(log.CurrentLogPath).Contains("Verified driver process start succeeded"),"success trace distinguishes process start from binding/readiness");
            // A delayed UI callback from the old attempt cannot label a later
            // pending verification as opened.
            var pending=new TaskCompletionSource<bool>();Action currentStarted=null;
            f.RunDeviceManagerWithStartForTests=started=>{currentStarted=started;return pending.Task;};
            Call(f,"ManageInstalledDetectionDevices");notifyStarted();Pump();
            Check(((Button)Field(f,"refreshDeviceButton")).Text=="正在打开设备管理…","old start callback cannot update a new attempt");
            var mismatch=new IOException("fake hash mismatch");mismatch.Data["ExpectedSHA256"]="reviewed-fixture";mismatch.Data["ActualSHA256"]="rejected-fixture";
            pending.SetException(mismatch);Pump();
            Check(scans==1&&notice.Contains("fake hash mismatch"),"verification failure never discovers or claims binding");
            Check(File.ReadAllText(log.CurrentLogPath).Contains("ExpectedSHA256=reviewed-fixture")&&File.ReadAllText(log.CurrentLogPath).Contains("ActualSHA256=rejected-fixture"),"version mismatch internal diagnostic retains reviewed/observed evidence without changing the gate");
            currentStarted();Pump();
            Check(((Button)Field(f,"refreshDeviceButton")).Text=="管理设备","late callback cannot reopen a completed failed attempt");
            f.Close();
        }
        Console.WriteLine("DEVICE_MANAGER_PASS="+checks+"; HARDWARE=0; DRIVER_PROCESS=0");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
