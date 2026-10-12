using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        [Browsable(false)] public bool HasValidEnvironmentSample { get; private set; }
        [Browsable(false)] public bool HasValidGnssFix { get; private set; }
        [Browsable(false)] public bool HasGnssAltitude { get; private set; }
        [Browsable(false)] public DateTime GnssSampleTime { get; private set; }
        [Browsable(false)] public DateTime EnvironmentSampleTime { get; private set; }
        [Browsable(false)] public bool EnvironmentAcquisitionBusy { get; private set; }
        [Browsable(false)] public long EnvironmentContextSequence { get; private set; }
        bool environmentAutoRefreshEnabled;
        string temperatureAcquisitionStatus=String.Empty;
        string gnssAcquisitionStatus=String.Empty;

        public void SetEnvironmentAutoRefreshState(bool enabled)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetEnvironmentAutoRefreshState), enabled); return; }
            if (environmentAutoRefreshEnabled == enabled) return;
            environmentAutoRefreshEnabled = enabled; RefreshEnvironmentPresentation();
        }

        internal string EnvironmentText(string zh, string en)
        { return ProjectCreationLanguageContract.Text(languageIndex, zh, en); }

        public void SetEnvironmentAcquisitionBusy(bool busy)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke(new Action<bool>(SetEnvironmentAcquisitionBusy), busy); return; }
            EnvironmentAcquisitionBusy = busy;
            RefreshDeviceStatePresentation();
        }

        public bool ApplyTemperatureHumiditySample(double temperature, double humidity, DateTime sampledAt)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<double,double,DateTime,bool>(ApplyTemperatureHumiditySample), temperature, humidity, sampledAt);
            if (!IsFinite(temperature) || temperature < -50 || temperature > 120 || !IsFinite(humidity) || humidity < 0 || humidity > 100)
                return false;
            bool previous = suppressChanges; suppressChanges = true;
            try { SetNumber(temperatureNumber, temperature); SetNumber(humidityNumber, humidity); }
            finally { suppressChanges = previous; }
            HasValidEnvironmentSample = true;
            EnvironmentSampleTime = sampledAt == DateTime.MinValue ? DateTime.Now : sampledAt;
            HardwareSampleTime = EnvironmentSampleTime;
            System.Threading.Interlocked.Increment(ref pollHardwareSampleSequence);
            RefreshEnvironmentPresentation(); MarkDirty();
            CheckEnvironmentLimits();
            return true;
        }

        public bool ApplyGnssFix(double latitude, double longitude, double altitude, DateTime utcTime)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<double,double,double,DateTime,bool>(ApplyGnssFix), latitude, longitude, altitude, utcTime);
            if (!IsFinite(latitude) || latitude < -90 || latitude > 90 || !IsFinite(longitude) || longitude < -180 || longitude > 180 || utcTime == DateTime.MinValue) return false;
            latitudeValue.Text = latitude.ToString("0.000000", CultureInfo.InvariantCulture);
            longitudeValue.Text = longitude.ToString("0.000000", CultureInfo.InvariantCulture);
            HasGnssAltitude = IsFinite(altitude) && altitude >= -1000 && altitude <= 20000;
            altitudeValue.Text = HasGnssAltitude ? altitude.ToString("0.0", CultureInfo.InvariantCulture) : "—";
            HasValidGnssFix = true; GnssSampleTime = utcTime.ToUniversalTime();
            beidouFixStatusRaw = EnvironmentText("定位有效", "Valid fix");
            fixStatusValue.Text = beidouFixStatusRaw;
            RefreshEnvironmentPresentation(); MarkDirty(); MaybeAutoRefreshWeather();
            return true;
        }

        public void ReportEnvironmentAcquisitionResult(string temperatureStatus, string gnssStatus, bool temperatureValid, bool gnssValid)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke(new Action<string,string,bool,bool>(ReportEnvironmentAcquisitionResult), temperatureStatus, gnssStatus, temperatureValid, gnssValid); return; }
            if (!temperatureValid) HasValidEnvironmentSample = false;
            if (!gnssValid)
            {
                HasValidGnssFix = false; HasGnssAltitude = false;
                beidouFixStatusRaw = EnvironmentText("未定位", "No fix"); fixStatusValue.Text = beidouFixStatusRaw;
                latitudeValue.Text = longitudeValue.Text = altitudeValue.Text = "—";
                MaybeAutoRefreshWeather();
            }
            RefreshEnvironmentPresentation();
            SetStatus((temperatureStatus ?? "") + " · " + (gnssStatus ?? ""), temperatureValid || gnssValid);
            MarkDirty();
        }
        public void ReportTemperatureHumidityAcquisitionResult(string status, bool valid)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { BeginInvoke(new Action<string,bool>(ReportTemperatureHumidityAcquisitionResult), status, valid); return; }
            if (!valid) HasValidEnvironmentSample = false;
            RefreshEnvironmentPresentation();
            temperatureAcquisitionStatus=status??"";
            RefreshAcquisitionStatus();
            // A periodic T/RH read does not invalidate a previous GNSS fix or
            // turn a failed/disconnected sample into a new project measurement.
        }
        public void ReportEnvironmentAcquisitionProgress(string status)
        {
            if(IsDisposed||Disposing)return;
            if(InvokeRequired){BeginInvoke(new Action<string>(ReportEnvironmentAcquisitionProgress),status);return;}
            SetStatus(status??"",false);
        }
        public void ReportGnssAcquisitionResult(string status,bool valid)
        {
            if(IsDisposed||Disposing)return;
            if(InvokeRequired){BeginInvoke(new Action<string,bool>(ReportGnssAcquisitionResult),status,valid);return;}
            if(!valid)
            {
                HasValidGnssFix=false;HasGnssAltitude=false;
                beidouFixStatusRaw=EnvironmentText("未定位","No fix");fixStatusValue.Text=beidouFixStatusRaw;
                latitudeValue.Text=longitudeValue.Text=altitudeValue.Text="—";
                MaybeAutoRefreshWeather();
            }
            gnssAcquisitionStatus=status??"";
            RefreshEnvironmentPresentation();RefreshAcquisitionStatus();
        }
        void RefreshAcquisitionStatus()
        {
            string status=temperatureAcquisitionStatus;
            if(gnssAcquisitionStatus.Length!=0)status+=(status.Length==0?"":" · ")+gnssAcquisitionStatus;
            bool limitsWarning=environmentLimitsEnabled&&HasValidEnvironmentSample&&
                (TemperatureCelsius<environmentMinTemperature||TemperatureCelsius>environmentMaxTemperature||
                    RelativeHumidity<environmentMinHumidity||RelativeHumidity>environmentMaxHumidity);
            if(limitsWarning)status+=(status.Length==0?"":" · ")+EnvironmentText("实测温湿度超出您设置的范围，请核对现场条件。","Measured conditions are outside your limits. Check the test environment.");
            SetStatus(status,!limitsWarning&&(HasValidEnvironmentSample||HasValidGnssFix));
        }

        void RefreshEnvironmentPresentation()
        {
            if (temperatureNumber == null) return;
            temperatureNumber.Enabled = humidityNumber.Enabled = HasValidEnvironmentSample;
            temperatureNumber.Increment = humidityNumber.Increment = 0;
            if (!HasValidEnvironmentSample) { temperatureNumber.Text = String.Empty; humidityNumber.Text = String.Empty; }
            hardwareSourceLabel.Text = HasValidEnvironmentSample
                ? environmentAutoRefreshEnabled ? EnvironmentText("设备实测 · 每秒自动读取", "Device measured · auto read every second")
                    : EnvironmentText("历史读数 · 自动读取已暂停", "Previous reading · auto reading paused")
                : environmentAutoRefreshEnabled ? EnvironmentText("等待温湿度自动采集", "Waiting for automatic T/RH reading")
                    : deviceReady ? EnvironmentText("温湿度未采集 · 自动读取已暂停", "No T/RH reading · auto reading paused")
                        : EnvironmentText("设备未就绪 · 自动读取暂停，可手动读取", "Device not ready · auto reading paused; manual read available");
            sampleTimeValue.Text = EnvironmentSampleTime == DateTime.MinValue ? "—" : EnvironmentSampleTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (HasValidGnssFix) sampleTimeValue.Text += "  |  UTC " + GnssSampleTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        void RestoreEnvironmentState(ProjectDraft draft)
        {
            EnvironmentContextSequence++;
            temperatureAcquisitionStatus=gnssAcquisitionStatus=String.Empty;
            HasValidEnvironmentSample = draft.EnvironmentSampleValid;
            EnvironmentSampleTime = draft.EnvironmentSampleTime;
            HasValidGnssFix = draft.GnssFixValid; HasGnssAltitude = draft.GnssAltitudeValid;
            GnssSampleTime = draft.GnssSampleTime;
            if (!HasValidGnssFix) latitudeValue.Text = longitudeValue.Text = "—";
            if (!HasGnssAltitude) altitudeValue.Text = "—";
            RefreshEnvironmentPresentation();
        }
    }
}
