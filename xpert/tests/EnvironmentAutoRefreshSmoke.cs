using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

// Compile only with ModuleHost.Environment.cs and EnvironmentalReadings.cs.
// All hardware/UDP dependencies below throw if the injected seam is not used.
namespace Xiongxin.OWTS.Desktop
{
    internal sealed class ArtEnvironmentReader { public Task<TemperatureHumidityReading> ReadAsync(string name,CancellationToken token){throw new Exception("REAL HARDWARE NOT PERMITTED");} internal static Task ReleaseRetainedTaskAsync(CancellationToken token){throw new Exception("REAL HARDWARE NOT PERMITTED");} }
    internal sealed class GnssUdpReader { public Task<GnssReading> ReadAsync(CancellationToken token){throw new Exception("NETWORK NOT PERMITTED");} }
    internal static class AppLanguages { internal static bool IsChinese(string code){return true;} }
    internal static class LateBound { internal static string ReadString(object o,string p){return "zh-CN";} }
    internal sealed class FakeState { internal bool DeviceConnected=true; }
    internal sealed class FakeDiagnostics { internal void Warning(string area,string text){} }
    internal sealed partial class ModuleHost : IDisposable
    {
        const string PressureTest="Pressure",CalibrationTest="Calibration";
        bool disposed;readonly object settings=new object();readonly FakeDiagnostics diagnostics=new FakeDiagnostics();
        readonly FakeState state=new FakeState();readonly Dictionary<string,Control> controls=new Dictionary<string,Control>();
        internal bool HasPressureDeviceOwner { get; set; }
        internal bool HasExternalCalibrationProcess { get; set; }
        static long ReadInt64(object control,string property){return Convert.ToInt64(control.GetType().GetProperty(property).GetValue(control,null));}
        static bool TryInvoke(object control,string name,object[] args){control.GetType().GetMethod(name).Invoke(control,args);return true;}
        internal void Attach(Control c){ConnectEnvironmentProvider(c);}
        internal void PauseTimer(){environmentTimer.Stop();}
        internal bool CanAuto(Control c){return CanAutoReadEnvironment(c);}
        internal void PublishAvailability(Control c){PublishEnvironmentAvailability(c,CanAutoReadEnvironment(c));}
        internal Task AutoRead(Control c){return ReadEnvironmentAsync(c,true);}
        internal int Interval{get{return environmentTimer.Interval;}}
        internal void Disconnect(){state.DeviceConnected=false;CancelInactiveEnvironment(environmentControl);}
        internal void Reconnect(){state.DeviceConnected=true;}
        internal void Block(Control c){controls[CalibrationTest]=c;}
        internal void Unblock(){controls.Clear();}
        public void Dispose(){disposed=true;DisposeEnvironmentProvider();}
    }
}
internal sealed class EnvironmentFixture : Control
{
    public event EventHandler HardwareAcquisitionRequested;
    public event EventHandler EnvironmentDetailsExpandedChanged;
    bool expanded=true;
    public bool EnvironmentDetailsExpanded{get{return expanded;}set{expanded=value;EnvironmentDetailsExpandedChanged?.Invoke(this,EventArgs.Empty);}}
    long environmentContext;
    public bool FailNextContextRead;
    public long EnvironmentContextSequence{get{if(FailNextContextRead){FailNextContextRead=false;throw new InvalidOperationException("injected context read failure");}return environmentContext;}set{environmentContext=value;}}
    public bool Busy,AutoEnabled;public int Samples,Results,GnssSamples,GnssResults;public bool GnssValid=true;public string Progress;
    public void SetEnvironmentAutoRefreshState(bool enabled){AutoEnabled=enabled;}
    public void SetEnvironmentAcquisitionBusy(bool value){Busy=value;}
    public bool ApplyTemperatureHumiditySample(double t,double h,DateTime time){Samples++;return true;}
    public bool ApplyGnssFix(double a,double b,double c,DateTime d){GnssSamples++;GnssValid=true;return true;}
    public void ReportEnvironmentAcquisitionResult(string t,string g,bool tv,bool gv){throw new Exception("auto must not overwrite GNSS");}
    public void ReportTemperatureHumidityAcquisitionResult(string status,bool valid){Results++;}
    public void ReportGnssAcquisitionResult(string status,bool valid){GnssResults++;GnssValid=valid;}
    public void ReportEnvironmentAcquisitionProgress(string status){Progress=status;}
    public void ManualRead(){HardwareAcquisitionRequested?.Invoke(this,EventArgs.Empty);}
}
internal sealed class EnvironmentOwnedTestFixture : Control
{
    public bool OwnsSession,Running,OwnershipUnknown;
    public bool HasActiveHardwareSession { get { if(OwnershipUnknown)throw new InvalidOperationException("unknown cleanup");return OwnsSession; } }
    public bool IsRunning { get { return Running; } }
}
internal static class EnvironmentAutoRefreshSmoke
{
    static int checks;static void Check(bool b,string m){checks++;if(!b)throw new Exception(m);}
    static void Pump(int ms){var watch=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(5);}while(watch.ElapsedMilliseconds<ms);}
    static TemperatureHumidityReading Good(){return new TemperatureHumidityReading{TemperatureSuccess=true,HumiditySuccess=true,TemperatureC=23,HumidityPercent=50,CapturedUtc=DateTimeOffset.UtcNow};}
    static void CachedCalibrationAndReleasedOwnership()
    {
        using(var form=new Form{ShowInTaskbar=false})using(var control=new EnvironmentFixture{Dock=DockStyle.Fill})using(var module=new ModuleHost())
        using(var cached=new Control())using(var owned=new EnvironmentOwnedTestFixture()) {
            form.Controls.Add(control);form.Show();Pump(5);int temperatureCalls=0,gnssCalls=0;
            module.EnvironmentTemperatureReader=token=>{temperatureCalls++;return Task.FromResult(Good());};
            module.EnvironmentGnssReader=token=>{gnssCalls++;return Task.FromResult(new GnssReading{Success=true,Latitude=30,Longitude=110,UtcTime=DateTimeOffset.UtcNow});};
            module.Attach(control);module.PauseTimer();module.Block(cached);
            Check(cached.Parent==null&&cached.Visible,"reproduce WinForms cached readiness control Visible=true without a page");
            Check(module.CanAuto(control),"parentless idle calibration-readiness cache is not a hardware owner");
            control.ManualRead();Pump(5);
            Check(temperatureCalls==1&&gnssCalls==1&&control.Samples==1,"manual environment read works after pressure exit despite idle calibration cache");
            form.Controls.Add(cached);cached.Show();
            Check(!module.CanAuto(control),"mounted visible test page still excludes environmental inputs");
            control.ManualRead();Check(temperatureCalls==1&&control.Progress.Contains("退出"),"active test manual request gives a short visible action");
            form.Controls.Remove(cached);cached.Hide();module.Block(owned);owned.OwnsSession=true;owned.Hide();
            Check(!module.CanAuto(control),"hidden real calibration session remains exclusive");
            control.ManualRead();Check(temperatureCalls==1,"hidden owner cannot be stolen by manual environment read");
            owned.OwnsSession=false;owned.Running=true;
            Check(!module.CanAuto(control),"detached running task remains exclusive");
            owned.Running=false;owned.OwnershipUnknown=true;
            control.ManualRead();Check(temperatureCalls==1&&!module.CanAuto(control),"unknown cleanup state fails closed with no reader started");
            owned.OwnershipUnknown=false;module.HasPressureDeviceOwner=true;module.PublishAvailability(control);
            control.ManualRead();Check(temperatureCalls==1&&control.Progress.Contains("释放"),"pending or failed pressure cleanup retains ownership and reports required exit");
            module.HasPressureDeviceOwner=false;module.PublishAvailability(control);
            Check(control.AutoEnabled&&control.Progress.Contains("可以读取"),"confirmed owner release removes the paused message and restores auto availability");
            control.ManualRead();Pump(5);
            Check(temperatureCalls==2&&gnssCalls==2&&control.Samples==2,"explicit read resumes without resetting another task or safety state");
            module.HasExternalCalibrationProcess=true;control.ManualRead();
            Check(temperatureCalls==2&&control.Progress.Contains("占用"),"external calibration process remains a real ownership barrier");
            module.HasExternalCalibrationProcess=false;module.Unblock();form.Close();
        }
    }
    static void CancelledReaderAndProviderGeneration()
    {
        using(var form=new Form{ShowInTaskbar=false})using(var control=new EnvironmentFixture{Dock=DockStyle.Fill})using(var module=new ModuleHost()) {
            form.Controls.Add(control);form.Show();Pump(5);int calls=0;
            var old=new TaskCompletionSource<TemperatureHumidityReading>();var next=new TaskCompletionSource<TemperatureHumidityReading>();
            CancellationToken oldToken=default(CancellationToken);
            module.EnvironmentTemperatureReader=token=>{calls++;oldToken=token;return old.Task;};
            module.EnvironmentGnssReader=token=>Task.FromResult(new GnssReading{Success=true,Latitude=30,Longitude=110,UtcTime=DateTimeOffset.UtcNow});
            module.Attach(control);module.PauseTimer();control.ManualRead();
            Check(calls==1&&control.Busy,"manual read owns one pending input task");
            control.EnvironmentDetailsExpanded=false;control.EnvironmentDetailsExpanded=true;
            module.EnvironmentTemperatureReader=token=>{calls++;return next.Task;};control.ManualRead();
            Check(oldToken.IsCancellationRequested&&calls==1,"same-project retry waits for cancelled old input task to actually finish");
            old.SetResult(Good());Pump(10);
            Check(calls==2&&control.Samples==0&&control.Busy,"cancelled value rejected; replacement starts once after release");
            next.SetResult(Good());Pump(10);Check(control.Samples==1&&!control.Busy,"replacement manual result reaches the page without an automatic poll");
            old=new TaskCompletionSource<TemperatureHumidityReading>();next=new TaskCompletionSource<TemperatureHumidityReading>();
            module.EnvironmentTemperatureReader=token=>{calls++;oldToken=token;return old.Task;};control.ManualRead();
            module.Attach(control);module.PauseTimer();module.EnvironmentTemperatureReader=token=>{calls++;return next.Task;};control.ManualRead();
            Check(oldToken.IsCancellationRequested&&calls==3,"new provider generation cannot overlap old native-task ownership");
            old.SetException(new InvalidOperationException("old provider failure"));Pump(10);
            Check(calls==4&&control.Samples==1&&control.Busy,"old generation failure cannot publish or consume the replacement request");
            next.SetResult(Good());Pump(10);Check(control.Samples==2&&!control.Busy,"only current provider generation can publish its successful reading");
            module.EnvironmentTemperatureReader=token=>Task.FromException<TemperatureHumidityReading>(new InvalidOperationException("injected current failure"));
            int results=control.Results;control.ManualRead();Pump(5);
            Check(control.Results==results+1&&!control.Busy,"current reader failure is reported and busy state ends");
            control.FailNextContextRead=true;control.ManualRead();Pump(5);
            Check(control.Progress.Contains("环境读取未完成")&&!control.Busy,"manual dispatch failure is visible instead of an unhandled async callback or silent click");
            form.Close();
        }
    }
    static void HardwareHandoff()
    {
        using(var form=new Form{ShowInTaskbar=false})using(var control=new EnvironmentFixture{Dock=DockStyle.Fill})using(var module=new ModuleHost()) {
            form.Controls.Add(control);form.Show();Pump(5);
            int reads=0,releases=0;CancellationToken readerToken=default(CancellationToken);
            var reader=new TaskCompletionSource<TemperatureHumidityReading>();
            var cleanup=new TaskCompletionSource<bool>();
            module.EnvironmentTemperatureReader=token=>{reads++;readerToken=token;return reader.Task;};
            module.EnvironmentGnssReader=token=>Task.FromResult(new GnssReading());
            module.EnvironmentOwnedTaskRelease=token=>{releases++;return cleanup.Task;};
            module.Attach(control);module.PauseTimer();control.ManualRead();
            var handoff=module.DrainEnvironmentForHardwareAsync(CancellationToken.None);
            Check(readerToken.IsCancellationRequested&&!handoff.IsCompleted&&releases==0,"handoff cancels but does not mistake cancellation for native release");
            control.ManualRead();Check(reads==1&&!module.CanAuto(control),"handoff lease suppresses new environment ownership");
            reader.SetResult(Good());Pump(10);
            Check(!handoff.IsCompleted&&releases==1&&control.Samples==0,"cancelled result ignored; native retained cleanup explicitly awaited");
            cleanup.SetResult(true);Pump(10);var lease=handoff.GetAwaiter().GetResult();
            Check(!module.CanAuto(control),"successful drain holds exclusion until new hardware owner established");
            module.EnvironmentOwnedTaskRelease=token=>Task.CompletedTask;
            var second=module.DrainEnvironmentForHardwareAsync(CancellationToken.None).GetAwaiter().GetResult();
            lease.Dispose();lease.Dispose();Check(!module.CanAuto(control),"old/double lease disposal cannot release another handoff");
            second.Dispose();Check(module.CanAuto(control),"only last handoff disposal releases preparation exclusion");
            module.EnvironmentOwnedTaskRelease=token=>Task.FromException(new System.IO.IOException("injected owned Clear failure"));
            bool failed=false;try{module.DrainEnvironmentForHardwareAsync(CancellationToken.None).GetAwaiter().GetResult();}catch(System.IO.IOException){failed=true;}
            Check(failed,"unconfirmed native clear propagates and cannot authorize hardware entry");
            reader=new TaskCompletionSource<TemperatureHumidityReading>();
            module.EnvironmentTemperatureReader=token=>{reads++;readerToken=token;return reader.Task;};control.ManualRead();
            using(var cancel=new CancellationTokenSource()) {
                var waiting=module.DrainEnvironmentForHardwareAsync(cancel.Token);cancel.Cancel();Pump(10);
                Check(waiting.IsCanceled&&readerToken.IsCancellationRequested&&control.Busy,"aborted navigation stays responsive while original reader still owns resources");
            }
            reader.SetResult(Good());Pump(10);form.Close();
        }
    }
    [STAThread] static int Main()
    {
        try{
            using(var host=new Form{ShowInTaskbar=false})using(var c=new EnvironmentFixture{Dock=DockStyle.Fill})using(var module=new ModuleHost()){
                host.Controls.Add(c);host.Show();Pump(20);int calls=0;CancellationToken pendingToken=default(CancellationToken);
                var pending=new TaskCompletionSource<TemperatureHumidityReading>();
                module.EnvironmentGnssReader=token=>Task.FromResult(new GnssReading{Success=true,Latitude=30,Longitude=110,UtcTime=DateTimeOffset.UtcNow});
                module.EnvironmentTemperatureReader=token=>{calls++;pendingToken=token;return pending.Task;};module.Attach(c);
                Check(module.Interval==1000,"auto timer exactly 1 second");Pump(1150);Check(calls==1&&c.Busy,"visible environment page starts one read automatically");
                Check(c.GnssSamples==1&&c.GnssResults==1,"automatic GNSS progresses while T/RH task is pending");
                Task ignored=module.AutoRead(c);Check(ignored.IsCompleted&&calls==1,"no re-entry while prior read pending");
                c.EnvironmentDetailsExpanded=false;Check(pendingToken.IsCancellationRequested&&!c.AutoEnabled,"leaving section cancels read and clears live-reading indication");pending.SetResult(Good());Pump(30);
                Check(c.Samples==0&&!c.Busy,"cancelled late result not applied");
                c.EnvironmentDetailsExpanded=true;module.EnvironmentTemperatureReader=token=>{calls++;return Task.FromResult(Good());};Task done=module.AutoRead(c);Pump(20);
                Check(done.IsCompleted&&c.Samples==1&&c.Results==1&&c.GnssValid,"auto result updates T/RH independently without clearing GNSS");
                pending=new TaskCompletionSource<TemperatureHumidityReading>();module.EnvironmentTemperatureReader=token=>{calls++;pendingToken=token;return pending.Task;};
                var changed=module.AutoRead(c);c.EnvironmentContextSequence++;pending.SetResult(Good());Pump(20);Check(c.Samples==1,"project-switch result discarded");
                pending=new TaskCompletionSource<TemperatureHumidityReading>();var disconnecting=module.AutoRead(c);module.Disconnect();Check(pendingToken.IsCancellationRequested&&!c.AutoEnabled,"disconnect cancels pending read and marks previous readings historical");pending.SetResult(Good());Pump(20);
                int stopped=calls;module.AutoRead(c).GetAwaiter().GetResult();Check(calls==stopped&&c.Samples==1,"disconnected page does not schedule hardware reads");
                module.Reconnect();using(var busy=new Control()){host.Controls.Add(busy);busy.Show();module.Block(busy);module.AutoRead(c).GetAwaiter().GetResult();Check(calls==stopped,"calibration/pressure owns hardware so automatic read skips without dialog");module.Unblock();}
                module.HasPressureDeviceOwner=true;c.ManualRead();Pump(20);
                Check(calls==stopped&&c.Progress!=null&&c.Progress.Contains("释放"),"hidden or failed pressure owner prevents competing ART reads with a visible release reason");
                module.AutoRead(c).GetAwaiter().GetResult();Check(calls==stopped,"automatic environment read also respects hidden pressure owner");
                module.HasPressureDeviceOwner=false;module.HasExternalCalibrationProcess=true;c.ManualRead();Pump(20);
                Check(calls==stopped&&c.Progress.Contains("占用"),"standalone calibration ownership blocks environment read visibly");module.HasExternalCalibrationProcess=false;
                pending=new TaskCompletionSource<TemperatureHumidityReading>();var hidden=module.AutoRead(c);c.Hide();Check(pendingToken.IsCancellationRequested,"hidden page cancels pending read");pending.SetResult(Good());Pump(20);Check(c.Samples==1,"hidden page never receives stale result");
                c.Show();pending=new TaskCompletionSource<TemperatureHumidityReading>();var disposed=module.AutoRead(c);module.Dispose();Check(pendingToken.IsCancellationRequested,"provider disposal cancels read and stops timer");pending.SetResult(Good());Pump(30);Check(c.Samples==1,"disposed provider ignores late completion");host.Close();
            }
            CachedCalibrationAndReleasedOwnership();CancelledReaderAndProviderGeneration();HardwareHandoff();
            Console.WriteLine("ENVIRONMENT_AUTO_REFRESH_PASS="+checks+";hardware=false;network=false");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
