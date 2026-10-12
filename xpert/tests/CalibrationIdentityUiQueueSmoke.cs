using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Diagnostic-only: distinguish an unrestricted background ApplicationState producer
// from the production MainForm refresh publication path. No product entry or SDK.
internal static class CalibrationIdentityUiQueueSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Field(object owner,string name){return owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static object Call(object owner,string name,params object[] args){return owner.GetType().GetMethod(name,Hidden).Invoke(owner,args);}
    sealed class IdentityPage:Control
    {
        internal int Applications;
        internal string LastKey;
        internal bool Rejected;
        Func<string[]> provider;
        public Func<string[]> CalibrationHardwareIdentityProvider
        {
            get{return provider;}
            set
            {
                provider=value;Applications++;
                try {LastKey=provider()[0];Rejected=false;}
                catch(InvalidOperationException){LastKey=null;Rejected=true;}
            }
        }
    }
    static AppSettings Config()
    {
        var app=new AppSettings{LanguageCode="zh-CN"};
        app.DataRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"empty-projects");
        app.Hardware.Enabled=true;app.Hardware.SelectedHubMac="001122334455";
        app.Hardware.ControllerLocation="H11";app.Hardware.AcquisitionLocation="S51";
        app.Hardware.ControllerSerial="1023417009";app.Hardware.AcquisitionSerial="ABCDE/1234";
        return app;
    }
    static HardwareStatusSnapshot Snapshot(bool ready)
    {
        return new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=ready,IdentityValid=true,
            StatusCode=ready?"Ready":"Disconnected",HubMac="001122334455",FromLiveIntegrationProcess=true,
            IntegrationRequestMode="--inspect",IntegrationExecutablePathUsed="fake-not-executed/connector.exe",
            RequestedHubMac="001122334455",RequestedControllerLocation="H11",RequestedAcquisitionLocation="S51",
            CheckedUtc=DateTime.UtcNow,ControllerSerial="1023417009",AcquisitionSerial="ABCDE/1234",
            ControllerInstanceId="USB\\VID_3063&PID_3135\\1023417009",AcquisitionInstanceId="USB\\VID_0CE9&PID_1218\\FAKE-PORT",
            ControllerDeviceName="Dev7"};
    }
    static void Register(ModuleHost host,Form parent,IdentityPage page)
    {
        parent.Controls.Add(page);var unused=page.Handle;
        ((Dictionary<string,Control>)Field(host,"controls")).Add(ModuleHost.CalibrationTest,page);
        Call(host,"ApplyCalibrationHardwareIdentity",page);
        Check(page.IsHandleCreated&&page.LastKey!=null,"fake calibration page has an actual UI handle and initial verified binding");
    }
    static void Background(Action action)
    {
        // Intentionally block this UI thread only until the finite fake producer
        // returns. This guarantees queued UI delegates cannot execute early.
        var task=Task.Run(action);
        if(!task.Wait(5000))throw new Exception("Fake producer exceeded 5 seconds.");
        task.GetAwaiter().GetResult();
    }
    static async Task Until(Func<bool> predicate)
    {
        var until=DateTime.UtcNow.AddSeconds(5);
        while(!predicate()) {if(DateTime.UtcNow>until)throw new Exception("UI queue did not drain.");await Task.Delay(5);}
    }
    static async Task DirectBackgroundProducer()
    {
        var state=new ApplicationState();state.SetHardware(Snapshot(true));
        using(var ui=new Form{ShowInTaskbar=false,Opacity=0})
        using(var host=new ModuleHost(Config(),state,new Diagnostics(),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"absent-modules")))
        {
            ui.Show();var page=new IdentityPage();Register(host,ui,page);
            string first=page.LastKey;int before=page.Applications;
            Background(()=>{state.SetHardware(Snapshot(false));state.SetHardware(Snapshot(true));});
            Check(page.Applications==before,"unrestricted background producer queues both UI applications");
            await Until(()=>page.Applications>=before+2);
            Check(page.LastKey==first&&!page.Rejected,"direct background back-to-back state updates reproduce observer coalescing");
            Console.WriteLine("RAW_STATE_API_LIMITATION_REPRODUCED: both delayed ApplyContext delegates observe final Ready; not evidence of a production producer.");
        }
    }
    static async Task ActualRefreshPublication()
    {
        int uiThread=Thread.CurrentThread.ManagedThreadId;
        var state=new ApplicationState();state.SetHardware(Snapshot(true));
        using(var form=new MainForm(Config(),state,true))
        {
            form.ShowInTaskbar=false;form.Opacity=0;form.Show();
            form.DevicePresentForTests=unused=>throw new Exception("Passive OS probe not permitted.");
            var host=(ModuleHost)Field(form,"moduleHost");var page=new IdentityPage();Register(host,form,page);
            string first=page.LastKey;int starts=0,publications=0,nonUiPublications=0;
            state.Changed+=delegate{publications++;if(Thread.CurrentThread.ManagedThreadId!=uiThread)nonUiPublications++;};
            Action<HardwareStatusSnapshot> callback=null;
            form.RefreshDeviceForTests=(connect,completed)=>{starts++;callback=completed;return true;};
            form.DeviceConnectionNoticeForTests=unused=>{};
            Action begin=()=>Call(form,"BeginHardwareRefresh",false,false,null);
            begin();Check(starts==1&&callback!=null,"actual refresh method uses only injected fake producer");
            int before=page.Applications;
            Background(()=>callback(Snapshot(false)));
            Check(state.Hardware.Ready&&page.Applications==before,"background refresh callback does not publish state before UI dispatch");
            begin();Check(starts==1,"in-flight refresh blocks a second producer while first callback is queued");
            await Until(()=>!(bool)Field(form,"hardwareRefreshInProgress"));
            Check(!state.Hardware.Ready&&page.Rejected,"actual offline publication is observed by calibration before a new refresh");
            Check(publications==1&&nonUiPublications==0,"offline state is synchronously published on UI thread");
            begin();Check(starts==2,"explicit next refresh starts only after first publication completes");
            Background(()=>callback(Snapshot(true)));
            Check(!state.Hardware.Ready,"reconnect Ready remains queued rather than overwriting state on background thread");
            await Until(()=>!(bool)Field(form,"hardwareRefreshInProgress"));
            Check(state.Hardware.Ready&&!page.Rejected&&page.LastKey!=first,"same serial and instance reconnect through actual publication receives a new epoch");
            Check(publications==2&&nonUiPublications==0,"both actual refresh publications occurred on UI thread");
            int applications=page.Applications;string reconnected=page.LastKey;
            begin();Background(()=>callback(Snapshot(true)));
            await Until(()=>!(bool)Field(form,"hardwareRefreshInProgress"));
            Check(page.LastKey==reconnected&&page.Applications==applications,"unchanged silent health refresh does not retire the session");
            Console.WriteLine("PRODUCTION_REFRESH_PATH_NOT_REPRODUCED: UI publication and single-flight guard preserve offline epoch; no direct background state update used.");
        }
    }
    [STAThread]static int Main()
    {
        Exception failure=null;Application.EnableVisualStyles();
        using(var pump=new Form{ShowInTaskbar=false,Opacity=0})
        {
            pump.Shown+=async delegate
            {
                try {await DirectBackgroundProducer();await ActualRefreshPublication();Console.WriteLine("CALIBRATION_IDENTITY_UI_QUEUE_PASS="+checks+"; fake evidence only; no SDK, connector, capture, product entry, or user settings writes.");}
                catch(Exception error){failure=error;Console.Error.WriteLine(error);}
                finally{pump.Close();}
            };
            Application.Run(pump);
        }
        return failure==null?0:1;
    }
}
