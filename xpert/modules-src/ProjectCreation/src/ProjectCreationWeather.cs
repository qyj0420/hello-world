using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        double lastWeatherLatitude = double.NaN;
        double lastWeatherLongitude = double.NaN;
        double manualWeatherLatitude = double.NaN, manualWeatherLongitude = double.NaN;
        bool weatherDisclosureAccepted, weatherLifecycleReady, weatherApplying;
        int weatherGeneration;
        CancellationTokenSource weatherCancellation;
        MetWeatherClient weatherClient = MetWeatherClient.Shared;
        Func<bool> weatherNetworkAvailable = NetworkInterface.GetIsNetworkAvailable;
        MetWeatherResult weatherReference;
        ToolTip weatherToolTip;
        System.Windows.Forms.Timer weatherTimer;
        DateTime nextAutomaticWeatherUtc;

        [Browsable(false)] public double WeatherReferenceTemperatureCelsius { get { return weatherReference == null || weatherReference.Forecast == null ? double.NaN : weatherReference.Forecast.Temperature; } }
        [Browsable(false)] public double WeatherReferenceHumidity { get { return weatherReference == null || weatherReference.Forecast == null ? double.NaN : weatherReference.Forecast.Humidity; } }

        public event EventHandler WeatherAcquisitionRequested;
        public event EventHandler WeatherUpdated;
        public event EventHandler WeatherRefreshFailed;

        [Category("环境")]
        [DefaultValue(true)]
        public bool OnlineWeatherEnabled { get; set; } = true;

        [Category("环境")]
        [DefaultValue(true)]
        public bool AutoRefreshWeatherOnHardwareSample { get; set; } = true;

        [Browsable(false)] public int WeatherCode
        {
            get { return pollWeatherCode; }
            private set { pollWeatherCode = value; }
        }
        volatile int pollWeatherCode = -1;
        [Browsable(false)] public int WeatherStateCode
        {
            get { return pollWeatherStateCode; }
            private set { pollWeatherStateCode = value; }
        }
        volatile int pollWeatherStateCode;
        [Browsable(false)] public DateTime WeatherUpdatedAt { get; private set; }
        [Browsable(false)] public string WeatherProvider
        {
            get { return pollWeatherProvider; }
            private set { pollWeatherProvider = value; }
        }
        volatile string pollWeatherProvider = string.Empty;
        [Browsable(false)] public string WeatherLastError
        {
            get { return pollWeatherLastError; }
            private set { pollWeatherLastError = value; }
        }
        volatile string pollWeatherLastError = string.Empty;
        [Browsable(false)] public long WeatherRefreshRequestSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollWeatherRefreshRequestSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollWeatherRefreshRequestSequence, value); }
        }
        long pollWeatherRefreshRequestSequence;
        [Browsable(false)] public long WeatherSampleSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollWeatherSampleSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollWeatherSampleSequence, value); }
        }
        long pollWeatherSampleSequence;
        [Browsable(false)] public double PendingWeatherLatitude
        {
            get { return System.Threading.Volatile.Read(ref pollPendingWeatherLatitude); }
            private set { System.Threading.Volatile.Write(ref pollPendingWeatherLatitude, value); }
        }
        double pollPendingWeatherLatitude;
        [Browsable(false)] public double PendingWeatherLongitude
        {
            get { return System.Threading.Volatile.Read(ref pollPendingWeatherLongitude); }
            private set { System.Threading.Volatile.Write(ref pollPendingWeatherLongitude, value); }
        }
        double pollPendingWeatherLongitude;

        public bool RequestWeatherRefresh()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(RequestWeatherRefresh));
            EnsureWeatherLifecycle();
            if (!OnlineWeatherEnabled)
            {
                SetWeatherState(8, WeatherUi("联网天气已禁用", "Online weather is disabled"));
                return false;
            }
            double latitude, longitude;
            bool hardware = TryHardwareWeatherPosition(out latitude, out longitude);
            if (!hardware) { latitude = manualWeatherLatitude; longitude = manualWeatherLongitude; }
            if (!weatherDisclosureAccepted || double.IsNaN(latitude) || double.IsNaN(longitude) || (!hardware && ShowDialogMessages))
            {
                SetWeatherState(5, WeatherUi("等待天气位置确认", "Waiting for weather location confirmation"));
                if (!ShowDialogMessages || !Visible || !IsHandleCreated) return false;
                using (var dialog = new WeatherLocationDialog(languageIndex, hardware, latitude, longitude))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                    weatherDisclosureAccepted = true;
                    latitude = dialog.Latitude; longitude = dialog.Longitude;
                    if (!hardware) { manualWeatherLatitude = latitude; manualWeatherLongitude = longitude; }
                }
            }
            return BeginOnlineWeather(latitude, longitude);
        }

        bool BeginOnlineWeather(double latitude, double longitude)
        {
            if (!weatherDisclosureAccepted || !OnlineWeatherEnabled || IsDisposed || Disposing) return false;
            Uri key = MetWeatherClient.RequestUri(latitude, longitude);
            CancelWeatherWork();
            double roundedLatitude = Math.Round(latitude, 3, MidpointRounding.AwayFromZero);
            double roundedLongitude = Math.Round(longitude, 3, MidpointRounding.AwayFromZero);
            if ((lastWeatherLatitude != roundedLatitude || lastWeatherLongitude != roundedLongitude) &&
                WeatherProvider.StartsWith("MET Norway", StringComparison.Ordinal))
            {
                // A forecast belongs to its requested location, not merely to this page.
                weatherReference = null; WeatherCode = -1;
                WeatherProvider = string.Empty; WeatherUpdatedAt = DateTime.MinValue;
                bool previous = suppressChanges; suppressChanges = true;
                try { weatherText.Text = string.Empty; }
                finally { suppressChanges = previous; }
            }
            if (!weatherNetworkAvailable())
            {
                SetWeatherState(6, WeatherUi("离线 · 暂时无法更新天气", "Offline · weather cannot be updated"));
                return false;
            }
            PendingWeatherLatitude = roundedLatitude;
            PendingWeatherLongitude = roundedLongitude;
            lastWeatherLatitude = PendingWeatherLatitude;
            lastWeatherLongitude = PendingWeatherLongitude;
            System.Threading.Interlocked.Increment(ref pollWeatherRefreshRequestSequence);
            WeatherStateCode = 1;
            WeatherLastError = string.Empty;
            RefreshWeatherButtonUi();
            CompleteOperation("WeatherAcquisitionRequested", true, string.Empty);
            SetStatus(WeatherUi("正在获取联网天气参考…", "Getting online forecast reference…"), false);
            RaiseSafely(WeatherAcquisitionRequested);
            var cancellation = weatherCancellation = new CancellationTokenSource();
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));
            int generation = weatherGeneration;
            string project = WeatherProjectKey();
            Task ignored = CompleteOnlineWeather(latitude, longitude, key.Query, project, generation, cancellation.Token);
            return true;
        }

        async Task CompleteOnlineWeather(double latitude, double longitude, string location, string project, int generation, CancellationToken token)
        {
            MetWeatherResult result;
            try { result = await weatherClient.GetAsync(latitude, longitude, token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                PostWeather(delegate { if (generation == weatherGeneration && WeatherStateCode == 1) SetWeatherState(6, WeatherUi("天气请求超时，请稍后刷新", "Weather request timed out; refresh later")); });
                return;
            }
            catch { result = new MetWeatherResult { State = "service" }; }
            PostWeather(delegate
            {
                double currentLat, currentLon;
                bool hardware = TryHardwareWeatherPosition(out currentLat, out currentLon);
                if (!hardware) { currentLat = manualWeatherLatitude; currentLon = manualWeatherLongitude; }
                if (generation != weatherGeneration || !OnlineWeatherEnabled) return;
                if (project != WeatherProjectKey() || double.IsNaN(currentLat) || double.IsNaN(currentLon) ||
                    MetWeatherClient.RequestUri(currentLat, currentLon).Query != location)
                { CancelWeatherWork(); SetWeatherState(5, WeatherUi("项目或位置已改变，请刷新天气", "Project or position changed; refresh the forecast")); return; }
                ApplyOnlineWeatherResult(result);
            });
        }

        void PostWeather(Action action)
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { if (!IsDisposed && !Disposing) action(); })); }
            catch (InvalidOperationException) { }
        }

        string WeatherProjectKey() { return EnvironmentContextSequence.ToString(CultureInfo.InvariantCulture) + "\u001f" + currentProjectPath + "\u001f" + StartName + "\u001f" + EndName + "\u001f" + TestDateTime.Ticks.ToString(CultureInfo.InvariantCulture); }

        bool TryHardwareWeatherPosition(out double latitude, out double longitude)
        {
            latitude = BeidouLatitude; longitude = BeidouLongitude;
            return HasValidGnssFix && GnssSampleTime != DateTime.MinValue &&
                Math.Abs((DateTime.Now - GnssSampleTime.ToLocalTime()).TotalMinutes) <= 10 &&
                IsFinite(latitude) && IsFinite(longitude) && latitude >= -90 && latitude <= 90 && longitude >= -180 && longitude <= 180;
        }

        void ApplyOnlineWeatherResult(MetWeatherResult result)
        {
            weatherReference = result;
            nextAutomaticWeatherUtc = result.RetryUtc > DateTime.UtcNow ? result.RetryUtc : DateTime.UtcNow.AddMinutes(10 + new Random().NextDouble() * 2);
            if (result.Forecast != null)
            {
                weatherApplying = true;
                try { ApplyWeatherObservation(result.Forecast.Code, string.Empty, "MET Norway · Forecast reference (not measured)", result.Forecast.UpdatedUtc.ToLocalTime()); }
                finally { weatherApplying = false; }
            }
            int state = result.State == "offline" ? 6 : result.State == "throttled" ? 7 : result.State == "service" || result.State == "deprecated" ? 4 : result.Cached ? 3 : 2;
            string message = state == 6 ? WeatherUi("离线 · 保留天气参考缓存", "Offline · retaining cached forecast") :
                state == 7 ? WeatherUi("天气服务繁忙，稍后自动重试", "Weather service busy; retrying later") :
                state == 4 ? WeatherUi("天气服务暂不可用", "Weather service is temporarily unavailable") :
                WeatherUi("天气参考已更新 · MET Norway", "Forecast reference updated · MET Norway");
            SetWeatherState(state, message);
            if (state == 4 || state == 6 || state == 7) RaiseSafely(WeatherRefreshFailed);
        }

        void CancelWeatherWork()
        {
            weatherGeneration++;
            if (weatherCancellation != null) { weatherCancellation.Cancel(); weatherCancellation.Dispose(); weatherCancellation = null; }
        }

        public bool CancelWeatherRefresh()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(CancelWeatherRefresh));
            bool wasPending = WeatherStateCode == 1;
            CancelWeatherWork();
            if (wasPending) WeatherStateCode = WeatherCode >= 0 ? 3 : 0;
            RefreshWeatherButtonUi();
            return wasPending;
        }

        internal void MaybeAutoRefreshWeather()
        {
            EnsureWeatherLifecycle();
            if (!OnlineWeatherEnabled || !AutoRefreshWeatherOnHardwareSample) return;
            double latitude, longitude;
            if (!TryHardwareWeatherPosition(out latitude, out longitude))
            {
                latitude = manualWeatherLatitude; longitude = manualWeatherLongitude;
                if (double.IsNaN(latitude) || double.IsNaN(longitude))
                { CancelWeatherWork(); SetWeatherState(5, WeatherUi("等待天气位置", "Waiting for weather location")); return; }
            }
            if (!weatherDisclosureAccepted) { SetWeatherState(5, WeatherUi("点击刷新，确认联网天气位置", "Refresh to confirm the online weather location")); return; }
            bool same = Math.Abs(lastWeatherLatitude - Math.Round(latitude, 3, MidpointRounding.AwayFromZero)) < 0.00001 && Math.Abs(lastWeatherLongitude - Math.Round(longitude, 3, MidpointRounding.AwayFromZero)) < 0.00001;
            if (same && (WeatherStateCode == 1 || DateTime.UtcNow < nextAutomaticWeatherUtc)) return;
            if (Visible && IsHandleCreated) BeginOnlineWeather(latitude, longitude);
        }

        public bool ApplyWeatherObservation(int weatherCode, string description, string provider, DateTime observationTime)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired)
            {
                try
                {
                    return (bool)Invoke(new Func<bool>(delegate
                    {
                        return ApplyWeatherObservation(weatherCode, description, provider, observationTime);
                    }));
                }
                catch (Exception exception)
                {
                    CompleteOperation("ApplyWeatherObservation", false, exception.GetBaseException().Message);
                    return false;
                }
            }
            if (weatherCode < 0 || weatherCode > 999) return false;
            if (!weatherApplying) { CancelWeatherWork(); weatherReference = null; }
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                WeatherCode = weatherCode;
                WeatherProvider = string.IsNullOrWhiteSpace(provider) ? "Host weather provider" : provider.Trim();
                WeatherUpdatedAt = observationTime == DateTime.MinValue ? DateTime.Now : observationTime;
                WeatherLastError = string.Empty;
                WeatherStateCode = 2;
                string localized = ProjectCreationLocalization.Weather(languageIndex, weatherCode);
                string unknown = ProjectCreationLocalization.Weather(languageIndex, -1);
                weatherText.Text = localized == unknown && !string.IsNullOrWhiteSpace(description) ? description.Trim() : localized;
            }
            finally { suppressChanges = previous; }
            System.Threading.Interlocked.Increment(ref pollWeatherSampleSequence);
            MarkDirty();
            RefreshWeatherButtonUi();
            CompleteOperation("ApplyWeatherObservation", true, string.Empty);
            SetStatus("天气数据已更新" + (WeatherProvider.Length == 0 ? string.Empty : " · " + WeatherProvider), true);
            RaiseSafely(WeatherUpdated);
            return true;
        }

        public bool ReportWeatherFailure(string message)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(ReportWeatherFailure), message);
            ReportWeatherFailureInternal(message, true);
            return true;
        }

        void ReportWeatherFailureInternal(string message, bool raiseEvent)
        {
            WeatherLastError = message ?? string.Empty;
            WeatherStateCode = WeatherCode >= 0 ? 3 : 4;
            RefreshWeatherButtonUi();
            CompleteOperation("WeatherAcquisition", false, WeatherLastError);
            SetStatus("天气获取失败" + (WeatherLastError.Length == 0 ? string.Empty : " · " + WeatherLastError), false);
            if (raiseEvent) RaiseSafely(WeatherRefreshFailed);
        }

        void SetManualWeatherText(string value)
        {
            if (weatherText == null) return;
            CancelWeatherWork(); weatherReference = null;
            manualWeatherLatitude = manualWeatherLongitude = double.NaN;
            WeatherCode = -1;
            WeatherProvider = string.Empty;
            WeatherUpdatedAt = DateTime.MinValue;
            WeatherStateCode = 0;
            weatherText.Text = value ?? string.Empty;
        }

        internal void RestoreWeatherMetadata(int code, string provider, DateTime updatedAt)
        {
            CancelWeatherWork(); weatherReference = null;
            manualWeatherLatitude = manualWeatherLongitude = double.NaN;
            WeatherCode = code;
            WeatherProvider = provider ?? string.Empty;
            WeatherUpdatedAt = updatedAt;
            WeatherStateCode = code >= 0 ? 3 : 0;
            if (code >= 0 && weatherText != null) weatherText.Text = ProjectCreationLocalization.Weather(languageIndex, code);
            RefreshWeatherButtonUi();
        }

        void RefreshWeatherButtonUi()
        {
            EnsureWeatherLifecycle();
            if (weatherRefreshButton == null) return;
            weatherRefreshButton.Enabled = WeatherStateCode != 1;
            weatherRefreshButton.Text = L(WeatherStateCode == 1 ? "获取中…" : "刷新天气");
            RefreshWeatherReferenceText();
        }

        string WeatherUi(string chinese, string english) { return Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, chinese, english); }

        void SetWeatherState(int state, string message)
        {
            WeatherStateCode = state;
            WeatherLastError = state == 2 || state == 3 ? string.Empty : message;
            RefreshWeatherButtonUi();
            SetStatus(message, state == 2 || state == 3);
        }

        void RefreshWeatherReferenceText()
        {
            if (weatherText == null) return;
            bool previous = suppressChanges; suppressChanges = true;
            try
            {
                if (weatherReference != null && weatherReference.Forecast != null)
                {
                    var forecast = weatherReference.Forecast;
                    if ((weatherReference.CacheExpiresUtc != DateTime.MinValue && DateTime.UtcNow >= weatherReference.CacheExpiresUtc) ||
                        Math.Abs((DateTime.UtcNow - forecast.ForecastUtc).TotalHours) > 2) weatherReference.Stale = true;
                    string status = weatherReference.Stale ? WeatherUi("过期缓存", "Expired cache") : weatherReference.Cached ? WeatherUi("参考缓存", "Cached reference") : WeatherUi("预报参考", "Forecast reference");
                    weatherText.Text = ProjectCreationLocalization.Weather(languageIndex, forecast.Code) + " · " + status + " · MET Norway";
                    weatherToolTip.SetToolTip(weatherText, WeatherUi("联网预报，不是现场实测。", "Online forecast, not an on-site measurement.") + "\r\n" +
                        forecast.Temperature.ToString("0.0", CultureInfo.InvariantCulture) + " °C · " + forecast.Humidity.ToString("0.0", CultureInfo.InvariantCulture) + " % RH\r\n" +
                        WeatherUi("预报时刻：", "Forecast time: ") + forecast.ForecastUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" +
                        WeatherUi("数据更新：", "Data updated: ") + forecast.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" +
                        "Data from MET Norway · CC BY 4.0\r\n" + MetWeatherClient.SourceUrl + "\r\n" + MetWeatherClient.LicenseUrl + "\r\n" +
                        WeatherUi("天气类型按软件分类显示；温湿度未作为实测记录。", "Weather type adapted to application categories; forecast temperature/humidity are not measurement records."));
                }
                else if (WeatherCode >= 0 && WeatherProvider.StartsWith("MET Norway", StringComparison.Ordinal))
                {
                    weatherText.Text = ProjectCreationLocalization.Weather(languageIndex, WeatherCode) + " · " + WeatherUi("已保存预报参考", "Saved forecast reference") + " · MET Norway";
                    weatherToolTip.SetToolTip(weatherText, WeatherUi("项目保存的预报参考，不是当前实测。数据更新：", "Saved project forecast reference, not a current measurement. Data updated: ") +
                        WeatherUpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\nMET Norway · CC BY 4.0\r\n" + MetWeatherClient.SourceUrl + "\r\n" + MetWeatherClient.LicenseUrl);
                }
                else weatherToolTip.SetToolTip(weatherText, string.Empty);
                string hint = WeatherLastError.Length > 0 ? WeatherLastError : WeatherUi("联网天气仅作预报参考", "Online weather is forecast reference only");
                weatherText.AccessibleDescription = hint;
                weatherToolTip.SetToolTip(weatherRefreshButton, hint + "\r\nMET Norway · api.met.no\r\n" + WeatherUi("仅发送三位小数经纬度；服务端可见联网IP。", "Only three-decimal coordinates are sent; the service sees the connection IP."));
            }
            finally { suppressChanges = previous; }
        }

        void EnsureWeatherLifecycle()
        {
            if (weatherLifecycleReady || IsDisposed || Disposing) return;
            weatherLifecycleReady = true;
            weatherToolTip = new ToolTip { AutoPopDelay = 20000, InitialDelay = 400, ReshowDelay = 100 };
            weatherTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            weatherTimer.Tick += delegate { if (Visible && IsHandleCreated) MaybeAutoRefreshWeather(); };
            weatherTimer.Start();
            Disposed += delegate { CancelWeatherWork(); if (weatherTimer != null) weatherTimer.Dispose(); if (weatherToolTip != null) weatherToolTip.Dispose(); };
            ProjectLoaded += delegate { CancelWeatherWork(); manualWeatherLatitude = manualWeatherLongitude = double.NaN; };
            ProjectCleared += delegate { CancelWeatherWork(); manualWeatherLatitude = manualWeatherLongitude = double.NaN; };
        }
    }
}
