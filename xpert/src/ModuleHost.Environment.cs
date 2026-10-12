using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class ModuleHost
    {
        CancellationTokenSource environmentCancellation;
        CancellationTokenSource environmentGnssCancellation;
        Task environmentTemperatureTask;
        Task environmentGnssTask;
        bool environmentTemperatureAutomatic;
        bool environmentGnssAutomatic;
        Control environmentTemperatureOwner;
        Control environmentGnssOwner;
        long environmentTemperatureContext;
        long environmentGnssContext;
        EventHandler environmentRequestHandler;
        EventInfo environmentRequestEvent;
        Control environmentControl;
        System.Windows.Forms.Timer environmentTimer;
        EventInfo environmentSectionEvent;
        EventHandler environmentVisibilityHandler;
        string environmentOwnershipPauseReason;
        long environmentProviderGeneration;
        int environmentHardwareHandoffs;
        internal Func<CancellationToken,Task> EnvironmentOwnedTaskRelease = ArtEnvironmentReader.ReleaseRetainedTaskAsync;
        internal Func<CancellationToken,Task<TemperatureHumidityReading>> EnvironmentTemperatureReader =
            token => new ArtEnvironmentReader().ReadAsync("Dev1", token);
        internal Func<CancellationToken,Task<GnssReading>> EnvironmentGnssReader =
            token => new GnssUdpReader().ReadAsync(token);
        void ConnectEnvironmentProvider(Control control)
        {
            DisposeEnvironmentProvider();
            environmentControl=control;
            environmentRequestEvent=control.GetType().GetEvent("HardwareAcquisitionRequested");
            environmentRequestHandler=async delegate {
                long generation=environmentProviderGeneration;
                try { await ReadEnvironmentAsync(control); }
                catch(Exception error) {
                    diagnostics.Warning("Environment","Manual read did not complete: "+error.GetType().Name);
                    if(IsCurrentEnvironmentProvider(control,generation))
                        TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"))?
                            "环境读取未完成，请重试；仍失败时请检查连接。":"Environmental reading did not complete. Retry, then check connections if it still fails."});
                }
            };
            if(environmentRequestEvent!=null) environmentRequestEvent.AddEventHandler(control,environmentRequestHandler);
            environmentTimer=new System.Windows.Forms.Timer { Interval=1000 };
            environmentTimer.Tick+=async delegate {
                bool enabled=CanAutoReadEnvironment(control);
                PublishEnvironmentAvailability(control,enabled);
                if(!enabled){CancelInactiveEnvironment(control);return;}
                // GNSS reception has its own timeout and must not delay the one-second
                // temperature/humidity stream or be starved by its in-flight task.
                await Task.WhenAll(ReadEnvironmentAsync(control,true),ReadGnssAsync(control,true));
            };
            environmentVisibilityHandler=delegate { CancelInactiveEnvironment(control); };
            control.VisibleChanged+=environmentVisibilityHandler;
            environmentSectionEvent=control.GetType().GetEvent("EnvironmentDetailsExpandedChanged");
            if(environmentSectionEvent!=null)environmentSectionEvent.AddEventHandler(control,environmentVisibilityHandler);
            PublishEnvironmentAvailability(control,CanAutoReadEnvironment(control));
            environmentTimer.Start();
            control.Disposed+=delegate { if(ReferenceEquals(environmentControl,control))DisposeEnvironmentProvider(); };
        }
        bool CanAutoReadEnvironment(Control control)
        {
            return !disposed&&control!=null&&!control.IsDisposed&&control.Visible&&state.DeviceConnected&&
                Convert.ToBoolean(control.GetType().GetProperty("EnvironmentDetailsExpanded")?.GetValue(control,null)??false)&&
                !EnvironmentalReadBlocked();
        }
        void CancelInactiveEnvironment(Control control)
        {
            bool enabled=CanAutoReadEnvironment(control);
            if(control!=null&&!control.IsDisposed)PublishEnvironmentAvailability(control,enabled);
            bool leftScope=disposed||control==null||control.IsDisposed||!control.Visible||
                !Convert.ToBoolean(control.GetType().GetProperty("EnvironmentDetailsExpanded")?.GetValue(control,null)??false)||EnvironmentalReadBlocked();
            // A disconnected global HV/Pico status disables automatic polling, but
            // must not cancel the user's explicit read-only ART/UDP request every tick.
            if(environmentCancellation!=null&&(leftScope||!EnvironmentTaskBelongsTo(control,true)||(!enabled&&environmentTemperatureAutomatic)))environmentCancellation.Cancel();
            if(environmentGnssCancellation!=null&&(leftScope||!EnvironmentTaskBelongsTo(control,false)||(!enabled&&environmentGnssAutomatic)))environmentGnssCancellation.Cancel();
        }
        bool EnvironmentalReadBlocked()
        {
            // Hidden pages and asynchronous/failed cleanup still own ART tasks.
            // Visibility is not a device-release receipt.
            if(Volatile.Read(ref environmentHardwareHandoffs)!=0||HasPressureDeviceOwner||HasExternalCalibrationProcess)return true;
            foreach(var pair in controls)
            {
                if(pair.Key!=PressureTest&&pair.Key!=CalibrationTest)continue;
                Control c=pair.Value;
                if(EnvironmentTestOwnsInputs(c))return true;
            }
            return false;
        }
        // Acquire on the UI thread before preparing any calibration/pressure
        // native owner. Hold until that owner is established or abandoned.
        internal async Task<IDisposable> DrainEnvironmentForHardwareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref environmentHardwareHandoffs);
            var lease=new EnvironmentHandoffLease(this);
            try {
                if(environmentCancellation!=null)environmentCancellation.Cancel();
                if(environmentGnssCancellation!=null)environmentGnssCancellation.Cancel();
                Task pending=Task.WhenAll(environmentTemperatureTask??Task.CompletedTask,environmentGnssTask??Task.CompletedTask);
                // Stop/emergency remain responsive. Abandoning navigation does
                // not erase the cancelled reader's task or native ownership.
                var cancelled=new TaskCompletionSource<bool>();
                using(token.Register(()=>cancelled.TrySetResult(true))) {
                    if(await Task.WhenAny(pending,cancelled.Task)!=pending)token.ThrowIfCancellationRequested();
                    await pending;
                }
                token.ThrowIfCancellationRequested();
                await EnvironmentOwnedTaskRelease(token);
                token.ThrowIfCancellationRequested();
                if(disposed)throw new OperationCanceledException("窗口已关闭。");
                return lease;
            } catch { lease.Dispose();throw; }
        }
        sealed class EnvironmentHandoffLease : IDisposable
        {
            ModuleHost owner;
            internal EnvironmentHandoffLease(ModuleHost value){owner=value;}
            public void Dispose(){var value=Interlocked.Exchange(ref owner,null);if(value!=null)Interlocked.Decrement(ref value.environmentHardwareHandoffs);}
        }
        static bool EnvironmentTestOwnsInputs(Control control)
        {
            if(control==null||control.IsDisposed)return false;
            // Calibration-readiness lookup creates a cached, parentless control.
            // WinForms reports Visible=true for it although it has never entered
            // a page or opened a device. Only a mounted visible page owns scope.
            if(control.Parent!=null&&control.Visible)return true;
            foreach(string name in new[]{"HasActiveHardwareSession","IsRunning"}) {
                PropertyInfo property;
                try { property=control.GetType().GetProperty(name); }
                catch { return true; }
                if(property==null)continue;
                // Hidden owners and failed/unknown cleanup remain exclusive.
                if(property.PropertyType!=typeof(bool)||!property.CanRead)return true;
                try { if((bool)property.GetValue(control,null))return true; }
                catch { return true; }
            }
            return false;
        }
        string EnvironmentOwnerPauseReason(bool zh)
        {
            if(HasPressureDeviceOwner)return zh?"加压任务尚未释放，请先完成试验退出。":
                "Pressure tasks have not been released. Finish leaving the test first.";
            return zh?"校准或加压任务占用中，请先退出试验后再读取。":
                "Leave calibration or voltage testing before reading environmental inputs.";
        }
        void PublishEnvironmentAvailability(Control control,bool enabled)
        {
            TryInvoke(control,"SetEnvironmentAutoRefreshState",new object[]{enabled});
            string reason=EnvironmentalReadBlocked()?EnvironmentOwnerPauseReason(AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"))):String.Empty;
            if(reason==environmentOwnershipPauseReason)return;
            bool resumed=!String.IsNullOrEmpty(environmentOwnershipPauseReason)&&reason.Length==0;
            environmentOwnershipPauseReason=reason;
            if(reason.Length!=0)TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{reason});
            else if(resumed)TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"))?
                "可以读取环境数据。":"Environmental inputs are available to read."});
        }
        async Task ReadEnvironmentAsync(Control control,bool temperatureOnly=false)
        {
            if(disposed||control==null||control.IsDisposed)return;
            if(temperatureOnly&&!CanAutoReadEnvironment(control))return;
            bool zh=AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"));
            if(EnvironmentalReadBlocked())
            {
                if(!temperatureOnly)TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{EnvironmentOwnerPauseReason(zh)});return;
            }
            if(!temperatureOnly)
            {
                TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{zh?"正在读取温湿度与 GPS；已有采集将等待完成…":"Reading T/RH and GPS; waiting for any active acquisition…"});
                long requestContext=ReadInt64(control,"EnvironmentContextSequence");
                long requestGeneration=environmentProviderGeneration;
                Task previousTemperature=Task.CompletedTask,previousGnss=Task.CompletedTask;
                if(environmentCancellation!=null&&(!EnvironmentTaskBelongsTo(control,true)||environmentCancellation.IsCancellationRequested))
                { environmentCancellation.Cancel();previousTemperature=environmentTemperatureTask??Task.CompletedTask; }
                if(environmentGnssCancellation!=null&&(!EnvironmentTaskBelongsTo(control,false)||environmentGnssCancellation.IsCancellationRequested))
                { environmentGnssCancellation.Cancel();previousGnss=environmentGnssTask??Task.CompletedTask; }
                UpdateEnvironmentBusy(control);
                await Task.WhenAll(previousTemperature,previousGnss);
                // Never share a cancelled task across pages/projects. Await its
                // actual release first; do not start another ART owner meanwhile.
                if(!IsCurrentEnvironmentProvider(control,requestGeneration)||
                    requestContext!=ReadInt64(control,"EnvironmentContextSequence")||!control.Visible||
                    !Convert.ToBoolean(control.GetType().GetProperty("EnvironmentDetailsExpanded")?.GetValue(control,null)??false))return;
                if(EnvironmentalReadBlocked()){
                    TryInvoke(control,"ReportEnvironmentAcquisitionProgress",new object[]{EnvironmentOwnerPauseReason(zh)});return;
                }
                if(environmentCancellation!=null)environmentTemperatureAutomatic=false;
                Task temperature=environmentCancellation==null?ReadTemperatureHumidityAsync(control,false):(environmentTemperatureTask??Task.CompletedTask);
                await Task.WhenAll(temperature,ReadGnssAsync(control,false));
                return;
            }
            if(environmentCancellation!=null)return;
            await ReadTemperatureHumidityAsync(control,true);
        }
        Task ReadTemperatureHumidityAsync(Control control,bool automatic)
        {
            if(environmentCancellation!=null)return environmentTemperatureTask??Task.CompletedTask;
            var cancellation=new CancellationTokenSource();environmentCancellation=cancellation;
            environmentTemperatureOwner=control;environmentTemperatureContext=ReadInt64(control,"EnvironmentContextSequence");
            environmentTemperatureAutomatic=automatic;
            environmentTemperatureTask=ReadTemperatureHumidityCoreAsync(control,automatic,cancellation);
            return environmentTemperatureTask;
        }
        async Task ReadTemperatureHumidityCoreAsync(Control control,bool automatic,CancellationTokenSource cancellation)
        {
            bool zh=AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"));
            long context=ReadInt64(control,"EnvironmentContextSequence");
            long generation=environmentProviderGeneration;
            UpdateEnvironmentBusy(control);
            try
            {
                // The driver runs off the UI thread and exposes no output API.
                var t=await EnvironmentTemperatureReader(cancellation.Token);
                if(!IsCurrentEnvironmentProvider(control,generation)||cancellation.IsCancellationRequested||ReadInt64(control,"EnvironmentContextSequence")!=context)return;
                if(automatic&&environmentTemperatureAutomatic&&!CanAutoReadEnvironment(control))return;
                bool tv=t!=null&&t.Success&&t.TemperatureC.HasValue&&t.HumidityPercent.HasValue&&t.CapturedUtc.HasValue&&
                    t.CapturedUtc.Value!=DateTimeOffset.MinValue&&FiniteEnvironmentValue(t.TemperatureC.Value,-50,120)&&FiniteEnvironmentValue(t.HumidityPercent.Value,0,100);
                if(tv)TryInvoke(control,"ApplyTemperatureHumiditySample",new object[]{t.TemperatureC.Value,t.HumidityPercent.Value,t.CapturedUtc.Value.LocalDateTime});
                TryInvoke(control,"ReportTemperatureHumidityAcquisitionResult",new object[]{tv?(zh?"温湿度已更新":"T/RH updated"):EnvironmentFailure(t==null?"invalid_data":t.ErrorCode,zh,true),tv});
            }
            catch(OperationCanceledException) { }
            catch(Exception ex)
            {
                diagnostics.Warning("Environment","Read failed: "+ex.GetType().Name);
                if(IsCurrentEnvironmentProvider(control,generation)&&!cancellation.IsCancellationRequested&&ReadInt64(control,"EnvironmentContextSequence")==context)
                    TryInvoke(control,"ReportTemperatureHumidityAcquisitionResult",new object[]{zh?"温湿度采集未完成，请检查连接":"T/RH reading failed; check connections",false});
            }
            finally
            {
                if(environmentCancellation==cancellation)environmentCancellation=null;
                UpdateEnvironmentBusy(control);
                cancellation.Dispose();
            }
        }
        Task ReadGnssAsync(Control control,bool automatic)
        {
            if(disposed||control==null||control.IsDisposed||(automatic&&!CanAutoReadEnvironment(control))||EnvironmentalReadBlocked())return Task.CompletedTask;
            if(environmentGnssCancellation!=null)
            {
                if(!EnvironmentTaskBelongsTo(control,false)){environmentGnssCancellation.Cancel();return environmentGnssTask??Task.CompletedTask;}
                if(!automatic)environmentGnssAutomatic=false;
                return environmentGnssTask??Task.CompletedTask;
            }
            var cancellation=new CancellationTokenSource();environmentGnssCancellation=cancellation;
            environmentGnssOwner=control;environmentGnssContext=ReadInt64(control,"EnvironmentContextSequence");
            environmentGnssAutomatic=automatic;
            environmentGnssTask=ReadGnssCoreAsync(control,automatic,cancellation);
            return environmentGnssTask;
        }
        async Task ReadGnssCoreAsync(Control control,bool automatic,CancellationTokenSource cancellation)
        {
            bool zh=AppLanguages.IsChinese(LateBound.ReadString(settings,"LanguageCode"));
            long context=ReadInt64(control,"EnvironmentContextSequence");UpdateEnvironmentBusy(control);
            long generation=environmentProviderGeneration;
            try
            {
                var g=await EnvironmentGnssReader(cancellation.Token);
                if(!IsCurrentEnvironmentProvider(control,generation)||cancellation.IsCancellationRequested||ReadInt64(control,"EnvironmentContextSequence")!=context||(automatic&&environmentGnssAutomatic&&!CanAutoReadEnvironment(control)))return;
                bool valid=g!=null&&g.Success&&g.Latitude.HasValue&&g.Longitude.HasValue&&g.UtcTime.HasValue&&
                    g.UtcTime.Value!=DateTimeOffset.MinValue&&FiniteEnvironmentValue(g.Latitude.Value,-90,90)&&FiniteEnvironmentValue(g.Longitude.Value,-180,180);
                if(valid)TryInvoke(control,"ApplyGnssFix",new object[]{g.Latitude.Value,g.Longitude.Value,g.AltitudeMeters??Double.NaN,g.UtcTime.Value.UtcDateTime});
                TryInvoke(control,"ReportGnssAcquisitionResult",new object[]{valid?(zh?"北斗定位已更新":"GNSS updated"):EnvironmentFailure(g==null?"invalid_data":g.ErrorCode,zh,false),valid});
            }
            catch(OperationCanceledException) { }
            catch(Exception ex)
            {
                diagnostics.Warning("Environment","GNSS read failed: "+ex.GetType().Name);
                if(IsCurrentEnvironmentProvider(control,generation)&&!cancellation.IsCancellationRequested&&ReadInt64(control,"EnvironmentContextSequence")==context)
                    TryInvoke(control,"ReportGnssAcquisitionResult",new object[]{zh?"GPS 采集未完成，请检查 UDP 8088 数据源":"GPS reading failed; check the UDP 8088 source",false});
            }
            finally
            {
                if(environmentGnssCancellation==cancellation)environmentGnssCancellation=null;
                UpdateEnvironmentBusy(control);cancellation.Dispose();
            }
        }
        void UpdateEnvironmentBusy(Control control)
        {
            if(!disposed&&control!=null&&!control.IsDisposed&&ReferenceEquals(environmentControl,control))
                TryInvoke(control,"SetEnvironmentAcquisitionBusy",new object[]{environmentCancellation!=null||environmentGnssCancellation!=null});
        }
        static bool FiniteEnvironmentValue(double value,double minimum,double maximum)
        { return !Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=minimum&&value<=maximum; }
        bool IsCurrentEnvironmentProvider(Control control,long generation)
        { return !disposed&&control!=null&&!control.IsDisposed&&ReferenceEquals(environmentControl,control)&&generation==environmentProviderGeneration; }
        bool EnvironmentTaskBelongsTo(Control control,bool temperature)
        {
            return control!=null&&!control.IsDisposed&&ReferenceEquals(control,temperature?environmentTemperatureOwner:environmentGnssOwner)&&
                ReadInt64(control,"EnvironmentContextSequence")== (temperature?environmentTemperatureContext:environmentGnssContext);
        }
        static string EnvironmentFailure(string code,bool zh,bool temperature)
        {
            string prefix=temperature?(zh?"温湿度：":"T/RH: "):(zh?"北斗：":"GNSS: ");
            switch(code??"")
            {
                case "driver_missing":return prefix+(zh?"采集驱动未就绪":"input driver unavailable");
                case "busy": case "port_in_use":return prefix+(zh?"采集接口被占用":"input interface in use");
                case "cleanup_pending":return prefix+(zh?"上次采集资源尚未释放，暂停新采集":"previous acquisition cleanup pending; new acquisition paused");
                case "timeout":return prefix+(temperature?(zh?"等待信号超时":"signal timeout"):(zh?"UDP 8088 等待定位数据超时":"UDP 8088 fix timeout"));
                case "invalid_data": case "no_fix":return prefix+(zh?"暂无有效测量":"no valid measurement");
                case "cancelled":return prefix+(zh?"已取消":"cancelled");
                default:return prefix+(zh?"未连接或暂无有效数据":"not connected or no valid data");
            }
        }
        void DisposeEnvironmentProvider()
        {
            ++environmentProviderGeneration;
            if(environmentTimer!=null){environmentTimer.Stop();environmentTimer.Dispose();environmentTimer=null;}
            if(environmentCancellation!=null)environmentCancellation.Cancel();
            if(environmentGnssCancellation!=null)environmentGnssCancellation.Cancel();
            if(environmentControl!=null&&environmentVisibilityHandler!=null)
            {
                environmentControl.VisibleChanged-=environmentVisibilityHandler;
                if(environmentSectionEvent!=null)try{environmentSectionEvent.RemoveEventHandler(environmentControl,environmentVisibilityHandler);}catch{}
            }
            if(environmentRequestEvent!=null&&environmentControl!=null&&environmentRequestHandler!=null)
                try{environmentRequestEvent.RemoveEventHandler(environmentControl,environmentRequestHandler);}catch{}
            environmentControl=null;environmentRequestEvent=null;environmentRequestHandler=null;
            environmentSectionEvent=null;environmentVisibilityHandler=null;
            environmentOwnershipPauseReason=null;
        }
    }
}
