using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Compile this file + ModuleHost.Environment.cs + EnvironmentalReadings.cs.
// Actual ProjectCreation UI comes from args[0]/modules. No real project saves,
// native drivers, sockets or weather requests. Missing injection fails closed.
namespace Xiongxin.OWTS.Desktop
{
    internal sealed class ArtEnvironmentReader
    { public Task<TemperatureHumidityReading> ReadAsync(string device,CancellationToken token) { throw new Exception("HARDWARE FORBIDDEN"); } internal static Task ReleaseRetainedTaskAsync(CancellationToken token){throw new Exception("HARDWARE FORBIDDEN");} }
    internal sealed class GnssUdpReader
    { public Task<GnssReading> ReadAsync(CancellationToken token) { throw new Exception("NETWORK FORBIDDEN"); } }
    internal static class AppLanguages { internal static bool IsChinese(string code) { return true; } }
    internal static class LateBound { internal static string ReadString(object o,string p) { return "zh-CN"; } }
    internal sealed class BridgeState { internal bool DeviceConnected=true; }
    internal sealed class BridgeDiagnostics { internal void Warning(string area,string text) { Console.WriteLine(area+": "+text); } }
    internal sealed partial class ModuleHost : IDisposable
    {
        const string PressureTest="Pressure",CalibrationTest="Calibration";
        bool disposed; readonly object settings=new object(); readonly BridgeDiagnostics diagnostics=new BridgeDiagnostics();
        readonly BridgeState state=new BridgeState(); readonly Dictionary<string,Control> controls=new Dictionary<string,Control>();
        internal bool HasPressureDeviceOwner { get; set; }
        internal bool HasExternalCalibrationProcess { get; set; }
        static long ReadInt64(object c,string p) { return Convert.ToInt64(c.GetType().GetProperty(p).GetValue(c,null)); }
        static bool TryInvoke(object c,string p,object[] args) { c.GetType().GetMethod(p).Invoke(c,args); return true; }
        internal void Attach(Control c) { ConnectEnvironmentProvider(c); environmentTimer.Stop(); }
        internal void TickOnce() { typeof(System.Windows.Forms.Timer).GetMethod("OnTick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(environmentTimer,new object[]{EventArgs.Empty}); }
        internal Task Auto(Control c) { return ReadEnvironmentAsync(c,true); }
        internal bool CanAuto(Control c) { return CanAutoReadEnvironment(c); }
        internal void SetConnected(bool value) { state.DeviceConnected=value; CancelInactiveEnvironment(environmentControl); }
        internal void Block(Control c) { controls[CalibrationTest]=c; CancelInactiveEnvironment(environmentControl); }
        internal void Unblock() { controls.Clear(); }
        public void Dispose() { disposed=true; DisposeEnvironmentProvider(); }
    }
}
internal static class EnvironmentUiBridgeSmoke
{
    sealed class ActiveHardwareOwner : Control
    {
        public bool HasActiveHardwareSession { get { return true; } }
    }
    static int checks; static Type controlType;
    static void Check(bool b,string m) { checks++; if(!b)throw new Exception(m); }
    static void Pump(int ms) { var s=Stopwatch.StartNew(); do { Application.DoEvents(); Thread.Sleep(2); }while(s.ElapsedMilliseconds<ms); }
    static object Read(object c,string n) { return c.GetType().GetProperty(n).GetValue(c,null); }
    static void Set(object c,string n,object v) { c.GetType().GetProperty(n).SetValue(c,v,null); }
    static object Field(object c,string n) { return c.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c); }
    static void SetField(object c,string n,object v) { c.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,v); }
    static void Invoke(object c,string n,params object[] args) { c.GetType().GetMethod(n).Invoke(c,args); }
    static void Click(Control c) { c.GetType().GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,new object[]{EventArgs.Empty}); }
    static TemperatureHumidityReading GoodT() { return new TemperatureHumidityReading { TemperatureSuccess=true,HumiditySuccess=true,TemperatureC=23,HumidityPercent=50,CapturedUtc=DateTimeOffset.UtcNow }; }
    static GnssReading GoodG() { return new GnssReading { Success=true,Latitude=39.9,Longitude=116.4,AltitudeMeters=45,UtcTime=DateTimeOffset.UtcNow,CapturedUtc=DateTimeOffset.UtcNow }; }
    sealed class Fixture : IDisposable
    {
        internal readonly Form Form; internal readonly Control Control;
        internal readonly ModuleHost Host=new ModuleHost();
        internal int TemperatureCalls,GnssCalls;
        internal CancellationToken TemperatureToken,GnssToken;
        internal TaskCompletionSource<TemperatureHumidityReading> PendingT;
        internal TaskCompletionSource<GnssReading> PendingG;
        internal Fixture()
        {
            Form=new Form { ShowInTaskbar=false,Width=1200,Height=800 };
            Control=(Control)Activator.CreateInstance(controlType);
            Set(Control,"OnlineWeatherEnabled",false); Set(Control,"AutoRefreshWeatherOnHardwareSample",false);
            SetField(Control,"environmentLimitsEnabled",false);SetField(Control,"environmentPopupEnabled",false);
            Set(Control,"LanguageIndex",0);Set(Control,"HardwareAcquisitionAvailable",true);Set(Control,"DeviceReady",true);
            Control.Dock=DockStyle.Fill;Form.Controls.Add(Control);Form.Show();Pump(5);
            Readers(()=>Task.FromResult(GoodT()),()=>Task.FromResult(GoodG()));Host.Attach(Control);
        }
        internal Control Button { get { return (Control)Field(Control,"acquireHardwareButton"); } }
        internal string Status { get { return ((Control)Field(Control,"statusLabel")).Text; } }
        internal bool Busy { get { return (bool)Read(Control,"EnvironmentAcquisitionBusy"); } }
        internal bool TValid { get { return (bool)Read(Control,"HasValidEnvironmentSample"); } }
        internal bool GValid { get { return (bool)Read(Control,"HasValidGnssFix"); } }
        internal void Readers(Func<Task<TemperatureHumidityReading>> t,Func<Task<GnssReading>> g)
        {
            Host.EnvironmentTemperatureReader=token=>{TemperatureCalls++;TemperatureToken=token;return t();};
            Host.EnvironmentGnssReader=token=>{GnssCalls++;GnssToken=token;return g();};
        }
        internal void Pending()
        {
            PendingT=new TaskCompletionSource<TemperatureHumidityReading>();PendingG=new TaskCompletionSource<GnssReading>();
            Readers(()=>PendingT.Task,()=>PendingG.Task);
        }
        internal void Tab(int column) { Click(((TableLayoutPanel)Field(Control,"tabStrip")).GetControlFromPosition(column,0)); }
        internal void Manual() { Click(Button);Pump(3); }
        internal void Finish() { if(PendingT!=null)PendingT.TrySetResult(GoodT());if(PendingG!=null)PendingG.TrySetResult(GoodG());Pump(15); }
        public void Dispose()
        {
            Host.Dispose();Finish();
            Check((long)Read(Control,"WeatherRefreshRequestSequence")==0,"network weather never requested");
            Check(Convert.ToString(Read(Control,"LastHostCallbackError")).Length==0,"no swallowed real UI callback error");
            Control.Dispose();Form.Dispose();
        }
    }
    static void BindingAndIndependentAuto()
    {
        using(var f=new Fixture()) {
            Check(!f.Host.CanAuto(f.Control),"joint tab excludes auto reads");
            long before=(long)Read(f.Control,"EnvironmentDetailsExpandedSequence");f.Tab(6);
            Check((bool)Read(f.Control,"EnvironmentDetailsExpanded")&&f.Host.CanAuto(f.Control),"actual environment tab enables scope");
            Check((long)Read(f.Control,"EnvironmentDetailsExpandedSequence")==before+1,"actual tab publishes section change");
            f.Pending();f.Host.TickOnce();
            Check(f.TemperatureCalls==1&&f.GnssCalls==1,"auto tick starts independent T/RH and GNSS");
            Check(f.Busy&&!f.Button.Enabled,"real button busy during work");
            f.PendingG.SetResult(GoodG());Pump(10);
            Check(f.GValid&&!f.TValid&&f.Busy,"GNSS completes while temperature pending");
            Check(f.Status.Contains("北斗定位已更新"),"GNSS feedback immediately visible");
            f.Host.TickOnce();
            Check(f.TemperatureCalls==1&&f.GnssCalls==2,"GNSS next tick not starved by T/RH");
            f.PendingT.SetResult(GoodT());Pump(10);
            Check(f.TValid&&f.GValid&&!f.Busy&&f.Button.Enabled,"button releases only after both sources finish");
            Check(f.Status.Contains("温湿度已更新")&&f.Status.Contains("北斗定位已更新"),"temperature feedback retains GNSS result");
        }
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Host.TickOnce();f.PendingT.SetResult(GoodT());Pump(10);
            Check(f.TValid&&!f.GValid&&f.Busy,"temperature does not await GNSS timeout");
            f.PendingG.SetResult(GnssReading.Failure("timeout","injected"));Pump(10);
            Check(f.TValid&&!f.GValid&&f.Button.Enabled,"GNSS timeout does not invalidate temperature");
            Check(f.Status.Contains("温湿度已更新")&&f.Status.Contains("UDP 8088"),"combined success and timeout remain visible");
        }
    }
    static void OfflineManualAndJoin()
    {
        using(var f=new Fixture()) {
            f.Tab(6);f.Host.SetConnected(false);f.Pending();f.Manual();
            Check(!f.Host.CanAuto(f.Control)&&f.TemperatureCalls==1&&f.GnssCalls==1,"offline manual starts both sources");
            Pump(1050);f.Host.TickOnce();Pump(1050);f.Host.TickOnce();
            Check(!f.TemperatureToken.IsCancellationRequested&&!f.GnssToken.IsCancellationRequested,"two offline ticks cannot cancel explicit manual");
            Check(f.Busy&&f.TemperatureCalls==1&&f.GnssCalls==1,"ticks do not duplicate manual requests");
            f.Finish();Check(f.TValid&&f.GValid&&f.Button.Enabled,"offline manual measurements reach actual UI");
        }
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();Task initial=f.Host.Auto(f.Control);
            Check(f.TemperatureCalls==1&&f.GnssCalls==0,"temperature-only API remains independent");
            Invoke(f.Control,"RequestHardwareAcquisition");Pump(3);
            Check(f.TemperatureCalls==1&&f.GnssCalls==1,"manual joins T/RH and starts missing GNSS");
            f.Host.SetConnected(false);f.Host.TickOnce();
            Check(!f.TemperatureToken.IsCancellationRequested&&!f.GnssToken.IsCancellationRequested,"manual promotion protects joined samples");
            f.PendingG.SetResult(GoodG());Pump(8);
            Check(f.GValid&&f.Busy,"joined GNSS feedback visible while T/RH pending");
            f.PendingT.SetResult(GoodT());Pump(8);
            Check(initial.IsCompleted&&f.TValid&&f.GValid&&!f.Busy,"joined results applied without duplicate read");
        }
    }
    static void CancellationAndContext()
    {
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Manual();f.Tab(1);
            Check(f.TemperatureToken.IsCancellationRequested&&f.GnssToken.IsCancellationRequested,"leaving tab cancels both");
            f.Finish();Check(!f.TValid&&!f.GValid&&!f.Busy,"late tab results discarded");
        }
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Manual();f.Control.Hide();
            Check(f.TemperatureToken.IsCancellationRequested&&f.GnssToken.IsCancellationRequested,"hiding page cancels both");
            f.Finish();Check(!f.TValid&&!f.GValid,"hidden page rejects late values");
        }
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Manual();long before=(long)Read(f.Control,"EnvironmentContextSequence");
            Invoke(f.Control,"ClearProject");string status=f.Status;
            Check((long)Read(f.Control,"EnvironmentContextSequence")>before,"real ClearProject rotates context");
            f.Finish();Check(!f.TValid&&!f.GValid&&!f.Busy&&f.Status==status,"old project values and status cannot overwrite new project");
        }
        using(var f=new Fixture())using(var blocked=new ActiveHardwareOwner()) {
            f.Tab(6);f.Pending();f.Manual();f.Host.Block(blocked);
            Check(f.TemperatureToken.IsCancellationRequested&&f.GnssToken.IsCancellationRequested,"conflicting owner cancels both sources");
            f.Finish();Invoke(f.Control,"RequestHardwareAcquisition");
            Check(f.TemperatureCalls==1&&f.GnssCalls==1&&!f.Busy,"blocked manual never starts readers");
            Check(f.Status.Contains("请先退出"),"blocked manual explains why");f.Host.Unblock();
        }
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Manual();f.Host.Dispose();
            Check(f.TemperatureToken.IsCancellationRequested&&f.GnssToken.IsCancellationRequested,"provider disposal cancels both");
            f.Finish();Check(!f.TValid&&!f.GValid,"disposed provider cannot publish late values");
        }
    }
    static void ReplacementScopeAndRangeWarning()
    {
        using(var f=new Fixture()) {
            f.Tab(6);f.Pending();f.Manual();
            var oldT=f.PendingT;var oldG=f.PendingG;var oldTToken=f.TemperatureToken;var oldGToken=f.GnssToken;
            Invoke(f.Control,"ClearProject");f.Tab(6);f.Pending();Invoke(f.Control,"RequestHardwareAcquisition");Pump(5);
            Check(oldTToken.IsCancellationRequested&&oldGToken.IsCancellationRequested,"new project request cancels both old scoped readers");
            Check(f.TemperatureCalls==1&&f.GnssCalls==1&&f.Busy,"new project request waits for actual old owner release");
            oldT.SetResult(GoodT());Pump(8);
            Check(f.TemperatureCalls==1&&f.GnssCalls==1&&f.Busy,"one old source releasing cannot start replacement early");
            oldG.SetResult(GoodG());Pump(8);
            Check(f.TemperatureCalls==2&&f.GnssCalls==2&&f.Busy,"replacement starts exactly once after both old sources release");
            Check(!f.TValid&&!f.GValid&&!f.TemperatureToken.IsCancellationRequested&&!f.GnssToken.IsCancellationRequested,"old values rejected and replacement owns uncancelled tokens");
            f.Finish();Check(f.TValid&&f.GValid&&!f.Busy&&f.Button.Enabled,"new project results complete with correct final busy state");
        }
        using(var old=new Fixture())using(var next=new Fixture()) {
            old.Tab(6);old.Pending();old.Manual();
            var oldT=old.PendingT;var oldG=old.PendingG;var oldTToken=old.TemperatureToken;var oldGToken=old.GnssToken;
            // The next real UI instance has no competing provider. Reuse the first
            // production host so ownership from its prior control is still pending.
            next.Host.Dispose();next.Tab(6);old.Host.Attach(next.Control);old.Pending();next.Manual();
            Check(oldTToken.IsCancellationRequested&&oldGToken.IsCancellationRequested,"Attach replacement control cancels old reader ownership");
            Check(old.TemperatureCalls==1&&old.GnssCalls==1&&next.Busy,"new control waits rather than joining old-control results");
            oldG.SetResult(GoodG());Pump(8);
            Check(old.TemperatureCalls==1&&old.GnssCalls==1&&next.Busy,"replacement control waits for remaining old temperature lease");
            oldT.SetResult(GoodT());Pump(8);
            Check(old.TemperatureCalls==2&&old.GnssCalls==2&&next.Busy,"new control starts each replacement source once");
            Check(!old.TValid&&!old.GValid&&!next.TValid&&!next.GValid,"old-control completion cannot publish into either current scope");
            old.Finish();Check(next.TValid&&next.GValid&&!next.Busy&&next.Button.Enabled,"replacement control receives own values and correct released busy state");
        }
        using(var f=new Fixture()) {
            f.Tab(6);SetField(f.Control,"environmentLimitsEnabled",true);SetField(f.Control,"environmentPopupEnabled",false);
            SetField(f.Control,"environmentMinTemperature",-50d);SetField(f.Control,"environmentMaxTemperature",20d);
            SetField(f.Control,"environmentMinHumidity",0d);SetField(f.Control,"environmentMaxHumidity",100d);
            f.Pending();f.Manual();f.PendingT.SetResult(GoodT());Pump(8);
            Check(f.TValid&&f.Status.Contains("超出您设置的范围"),"valid 23C sample retains configured 20C maximum warning after temperature feedback");
            Check(!(bool)Field(f.Control,"lastStatusSucceeded"),"range-warning footer is not success");
            f.PendingG.SetResult(GoodG());Pump(8);
            Check(f.TValid&&f.GValid&&f.Status.Contains("温湿度已更新")&&f.Status.Contains("北斗定位已更新")&&f.Status.Contains("超出您设置的范围"),"GNSS completion cannot overwrite measured range warning");
            Check(!(bool)Field(f.Control,"lastStatusSucceeded")&&!f.Busy&&f.Button.Enabled,"warning remains non-success without disabling completed UI");
            Invoke(f.Control,"ReportTemperatureHumidityAcquisitionResult","温湿度已更新",true);
            Check(f.Status.Contains("超出您设置的范围")&&!(bool)Field(f.Control,"lastStatusSucceeded"),"later temperature success cannot erase active range warning");
        }
    }
    static void InvalidReadingsAndRecovery()
    {
        using(var f=new Fixture()) {
            f.Tab(6);f.Readers(()=>Task.FromException<TemperatureHumidityReading>(new InvalidDataException("injected temperature failure")),()=>Task.FromResult(GoodG()));f.Manual();
            Check(!f.TValid&&f.GValid&&!f.Busy&&f.Button.Enabled,"T/RH failure retains GNSS and restores button");
            Check(f.Status.Contains("温湿度采集未完成")&&f.Status.Contains("北斗定位已更新"),"source error and success both visible");
            f.Readers(()=>Task.FromResult(GoodT()),()=>Task.FromException<GnssReading>(new InvalidDataException("injected GNSS failure")));f.Manual();
            Check(f.TValid&&!f.GValid&&!f.Busy&&f.Button.Enabled,"GNSS failure retains T/RH and restores button");
            Check(f.Status.Contains("温湿度已更新")&&f.Status.Contains("GPS 采集未完成"),"opposite-source results retained");
            f.Readers(()=>Task.FromResult(GoodT()),()=>Task.FromResult(GoodG()));f.Manual();
            Check(f.TValid&&f.GValid&&f.Button.Enabled,"both sources recover on retry");
            var invalidT=new List<TemperatureHumidityReading>();
            var t=GoodT();t.CapturedUtc=null;invalidT.Add(t);t=GoodT();t.CapturedUtc=DateTimeOffset.MinValue;invalidT.Add(t);
            t=GoodT();t.TemperatureC=Double.NaN;invalidT.Add(t);t=GoodT();t.HumidityPercent=101;invalidT.Add(t);invalidT.Add(null);
            foreach(var bad in invalidT) { f.Readers(()=>Task.FromResult(bad),()=>Task.FromResult(GoodG()));f.Manual();Check(!f.TValid&&f.GValid&&f.Button.Enabled,"invalid T/RH time/range rejected independently"); }
            var invalidG=new List<GnssReading>();
            var g=GoodG();g.UtcTime=null;invalidG.Add(g);g=GoodG();g.UtcTime=DateTimeOffset.MinValue;invalidG.Add(g);
            g=GoodG();g.Latitude=Double.NaN;invalidG.Add(g);g=GoodG();g.Latitude=91;invalidG.Add(g);
            g=GoodG();g.Longitude=181;invalidG.Add(g);g=GoodG();g.Longitude=Double.PositiveInfinity;invalidG.Add(g);invalidG.Add(null);
            foreach(var bad in invalidG) { f.Readers(()=>Task.FromResult(GoodT()),()=>Task.FromResult(bad));f.Manual();Check(f.TValid&&!f.GValid&&f.Button.Enabled,"invalid GNSS time/coordinate rejected independently");Check(((Control)Field(f.Control,"latitudeValue")).Text=="—","invalid fix clears stale coordinate"); }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);
            controlType=Assembly.LoadFrom(Path.Combine(root,"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll")).GetType("HVX.Controls.ProjectCreationControl",true);
            Check(controlType.GetMethod("ReportGnssAcquisitionResult")!=null&&controlType.GetMethod("ReportEnvironmentAcquisitionProgress")!=null,"actual module implements independent feedback API");
            BindingAndIndependentAuto();OfflineManualAndJoin();CancellationAndContext();ReplacementScopeAndRangeWarning();InvalidReadingsAndRecovery();
            Console.WriteLine("ENVIRONMENT_UI_BRIDGE_PASS="+checks+";hardware=false;network=false;userProjectsModified=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
