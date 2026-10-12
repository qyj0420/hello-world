using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    [DefaultEvent("ProjectSaved")]
    [DefaultProperty("DataRootDirectory")]
    public partial class ProjectCreationControl : UserControl
    {
        ReferenceCatalog catalog;
        readonly List<JointData> joints = new List<JointData>();
        readonly Dictionary<string, string> phaseFirstMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string projectDefaultMaterial = string.Empty;
        List<PressureStep> currentPressureSteps = new List<PressureStep>();
        bool suppressChanges;
        bool emptyState = true;
        bool canEditGeometry = true;
        volatile bool exitRequested;
        volatile string health = "Healthy";
        volatile string lastError = string.Empty;
        volatile string cachedCableMode = ProjectModes.ThreeCore;
        bool settingCapacitorIdentity;
        bool exitDispatching;
        bool hardwareDispatching;
        bool projectManagerDispatching;
        // Safe customer-mode defaults: the host must positively attest both
        // connection and an acquisition provider before the button is usable.
        volatile bool deviceReady;
        volatile bool hardwareAcquisitionAvailable;
        string dataRootDirectory = string.Empty;
        string settingsPath;
        string currentProjectPath = string.Empty;
        string loadedProjectPath = string.Empty;
        string lastSavedProjectPath = string.Empty;
        double loadedLengthA;
        double loadedLengthB;
        double loadedLengthC;
        string loadedGeometryFingerprint = string.Empty;
        int languageIndex = ProjectCreationLocalization.NormalizeLanguage(-1);
        int languagePreference = -1;
        string beidouFixStatusRaw = "未采集";
        volatile string capacitorId = string.Empty;
        string lastAppliedCableMode = string.Empty;
        string cableStartNameBeforeCapacitor = string.Empty;
        string cableEndNameBeforeCapacitor = string.Empty;
        string cableDispatchEndBeforeCapacitor = string.Empty;
        decimal cableLengthABeforeCapacitor;
        decimal cableLengthBBeforeCapacitor;
        decimal cableLengthCBeforeCapacitor;
        readonly List<JointData> cableJointsBeforeCapacitor = new List<JointData>();
        bool hasCableStateBeforeCapacitor;
        bool embeddedMode;

        public event EventHandler ProjectSaved;
        public event EventHandler CableStructureChanged;
        public event EventHandler ProjectLoaded;
        public event EventHandler ProjectCleared;
        public event EventHandler ProjectManagerRequested;
        public event EventHandler ExitRequested;
        public event EventHandler HardwareAcquisitionRequested;

        public ProjectCreationControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Font = ProjectCreationTheme.UiFont(9f);
            BackColor = ProjectCreationTheme.Workspace;
            // 720 x 490 is the supported logical viewport floor.  This also
            // covers a 1120 x 740 work area at 150% display scaling without
            // relying on a page scrollbar.
            MinimumSize = new System.Drawing.Size(720, 490);
            Size = new System.Drawing.Size(1000, 700);
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HVX", "ProjectCreation", "project-creation.ini");
            catalog = ReferenceCatalog.Load(settingsPath);
            InitializeLayout();
            LoadEnvironmentLimits();
            cableModeCombo.SelectedIndexChanged += delegate { cachedCableMode = ProjectModes.Normalize(GetChoiceValue(cableModeCombo)); };
            ApplyCatalogToControls();
            LoadPressurePreset();
            projectDefaultMaterial = GetChoiceValue(bodyTypeCombo);
            ApplyLanguage();
            AttachChangeTracking();
            ApplyModeState(false);
            UpdateSavedState(false, true);
            SetStatus(catalog.RecoveryRequired ? "配置缓存读取异常，原件已保留 · " + catalog.LoadWarning : "就绪 · 请选择负载类型并填写项目资料", false);
        }

        [Category("HVX")]
        [Description("新项目的数据根目录。保存时在此目录下创建线路/时间目录。")]
        public string DataRootDirectory { get { return dataRootDirectory; } set { dataRootDirectory = (value ?? string.Empty).Trim(); } }

        [Category("HVX")]
        public string SettingsPath { get { return settingsPath; } set { settingsPath = (value ?? string.Empty).Trim(); } }

        [Category("HVX")]
        [DefaultValue(-1)]
        public int LanguageIndex { get { return languagePreference; } set { SetLanguageIndex(value); } }
        [Browsable(false)] public int EffectiveLanguageIndex { get { return languageIndex; } }

        [Category("HVX")]
        [DefaultValue(false)]
        [Description("嵌入已有页面标题的宿主时隐藏控件自身的蓝色标题/设备状态栏；独立运行默认保留。")]
        public bool EmbeddedMode
        {
            get { return embeddedMode; }
            set
            {
                if (embeddedMode == value) return;
                if (InvokeRequired) { Invoke(new Action<bool>(SetEmbeddedMode), value); return; }
                SetEmbeddedMode(value);
            }
        }

        void SetEmbeddedMode(bool value)
        {
            embeddedMode = value;
            ApplyEmbeddedMode();
        }

        [Browsable(false)] public string LanguageCode { get { return languagePreference == -1 ? "system" : ProjectCreationLanguageContract.Codes[languageIndex]; } }

        [Browsable(false)] public string EnglishVersion { get { return "Project Creation Control 1.4.1"; } }
        [Browsable(false)] public string Health { get { return health; } }
        [Browsable(false)] public bool IsBusy
        {
            get { return pollIsBusy; }
            private set { pollIsBusy = value; }
        }
        volatile bool pollIsBusy;
        [Browsable(false)] public bool IsDirty
        {
            get { return pollIsDirty; }
            private set { pollIsDirty = value; }
        }
        volatile bool pollIsDirty;
        [Browsable(false)] public bool IsSaved
        {
            get { return pollIsSaved; }
            private set { pollIsSaved = value; }
        }
        volatile bool pollIsSaved;
        [Browsable(false)] public string LastError
        {
            get { return lastError; }
            private set { lastError = value ?? string.Empty; health = lastError.Length == 0 ? "Healthy" : "Attention: " + lastError; }
        }
        [Browsable(false)] public string LastOperation
        {
            get { return pollLastOperation; }
            private set { pollLastOperation = value; }
        }
        volatile string pollLastOperation = "Ready";
        [Browsable(false)] public bool LastOperationSucceeded
        {
            get { return pollLastOperationSucceeded; }
            private set { pollLastOperationSucceeded = value; }
        }
        volatile bool pollLastOperationSucceeded = true;
        [Browsable(false)] public long LastOperationSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollLastOperationSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollLastOperationSequence, value); }
        }
        long pollLastOperationSequence;
        [Browsable(false)] public string CurrentProjectPath { get { return currentProjectPath; } }
        [Browsable(false)] public string LoadedProjectPath { get { return loadedProjectPath; } }
        [Browsable(false)] public string LastSavedProjectPath { get { return lastSavedProjectPath; } }
        [Browsable(false)] public string LastSavedProjectOperationCode { get; private set; }
        [Browsable(false)] public bool HasExistingTestData
        {
            get { return pollHasExistingTestData; }
            private set { pollHasExistingTestData = value; }
        }
        volatile bool pollHasExistingTestData;
        [Browsable(false)] public bool CanEditCableGeometry { get { return canEditGeometry; } }
        [Browsable(false)] public bool CanEditCableCoreMode { get { return canEditGeometry && CableMode != ProjectModes.Capacitor; } }
        [Browsable(false)] public long ProjectSavedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollProjectSavedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollProjectSavedSequence, value); }
        }
        long pollProjectSavedSequence;
        [Browsable(false)] public long ProjectLoadedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollProjectLoadedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollProjectLoadedSequence, value); }
        }
        long pollProjectLoadedSequence;
        [Browsable(false)] public long ProjectClearedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollProjectClearedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollProjectClearedSequence, value); }
        }
        long pollProjectClearedSequence;
        [Browsable(false)] public long ProjectManagerRequestSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollProjectManagerRequestSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollProjectManagerRequestSequence, value); }
        }
        long pollProjectManagerRequestSequence;
        [Browsable(false)] public long ExitRequestSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollExitRequestSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollExitRequestSequence, value); }
        }
        long pollExitRequestSequence;
        [Category("LabVIEW")]
        [Description("只读退出状态。初始为 False，点击退出后为 True；轮询读取不触发退出请求或界面刷新。")]
        // LabVIEW polling hot path: no UI access, Invoke, events, allocation, or I/O.
        public bool Exit { get { return exitRequested; } }

        [Description("仅在主 VI 初始化或重新打开控件时调用一次，清除上次退出状态；不刷新界面、不触发事件。")]
        public void ResetExit() { exitRequested = false; }
        [Browsable(false)] public long HardwareAcquisitionRequestSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollHardwareAcquisitionRequestSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollHardwareAcquisitionRequestSequence, value); }
        }
        long pollHardwareAcquisitionRequestSequence;
        [Browsable(false)] public long HardwareSampleSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollHardwareSampleSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollHardwareSampleSequence, value); }
        }
        long pollHardwareSampleSequence;
        [Browsable(false)] public string LastHostCallbackError
        {
            get { return pollLastHostCallbackError; }
            private set { pollLastHostCallbackError = value; }
        }
        volatile string pollLastHostCallbackError = string.Empty;

        [Category("项目")] public string Organization { get { return organizationCombo.Text; } set { SelectOrAdd(organizationCombo, value); } }
        [Category("项目")] public string OperatorName { get { return operatorCombo.Text; } set { SelectOrAdd(operatorCombo, value); } }
        [Category("项目")] public string TestLocation { get { return testLocationText.Text; } set { testLocationText.Text = value ?? string.Empty; } }
        [Category("项目")] public DateTime TestDateTime { get { return testDatePicker.Value; } set { testDatePicker.Value = ClampDate(value, testDatePicker); } }
        [Category("项目")] public bool IsDoubleEnded { get { return dualEndedCheck.Checked; } set { dualEndedCheck.Checked = value; } }
        [Browsable(false)] public string DataFileNameSuffix { get { return IsDoubleEnded ? "D" : string.Empty; } }

        [Category("硬件采集")] public double RelativeHumidity { get { return Decimal.ToDouble(humidityNumber.Value); } set { SetNumber(humidityNumber, value); } }
        [Category("硬件采集")] public double TemperatureCelsius { get { return Decimal.ToDouble(temperatureNumber.Value); } set { SetNumber(temperatureNumber, value); } }
        [Category("环境")] public string Weather { get { return weatherText.Text; } set { SetManualWeatherText(value); } }
        [Category("硬件采集")] public double BeidouLatitude { get { return ParseDisplayNumber(latitudeValue.Text); } }
        [Category("硬件采集")] public double BeidouLongitude { get { return ParseDisplayNumber(longitudeValue.Text); } }
        [Category("硬件采集")] public double BeidouAltitude { get { return ParseDisplayNumber(altitudeValue.Text); } }
        [Category("硬件采集")] public string BeidouFixStatus { get { return beidouFixStatusRaw; } }
        [Category("硬件采集")] public DateTime HardwareSampleTime { get; private set; }
        [Category("硬件采集")] public bool HardwareValuesReadOnly
        {
            get { return humidityNumber.ReadOnly && temperatureNumber.ReadOnly; }
            set { humidityNumber.ReadOnly = value; temperatureNumber.ReadOnly = value; RefreshHardwareSourceLabel(); }
        }

        [Category("硬件采集"), DefaultValue(false)]
        [Description("宿主确认的设备连接状态。统一桌面在未连接指定设备时将其设为 False。")]
        public bool DeviceReady
        {
            get { return deviceReady; }
            set
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired)
                {
                    try { BeginInvoke(new Action(delegate { DeviceReady = value; })); } catch { }
                    return;
                }
                deviceReady = value;
                RefreshDeviceStatePresentation();
            }
        }

        [Category("硬件采集"), DefaultValue(false)]
        [Description("宿主是否已接入可用的非 LabVIEW 硬件采样提供器。False 时禁用“读取硬件”。")]
        public bool HardwareAcquisitionAvailable
        {
            get { return hardwareAcquisitionAvailable; }
            set
            {
                if (IsDisposed || Disposing) return;
                if (InvokeRequired)
                {
                    try { BeginInvoke(new Action(delegate { HardwareAcquisitionAvailable = value; })); } catch { }
                    return;
                }
                hardwareAcquisitionAvailable = value;
                RefreshDeviceStatePresentation();
            }
        }

        [Category("线路")] public string StartName { get { return startNameText.Text; } set { startNameText.Text = value ?? string.Empty; } }
        [Category("线路")] public string StartTerminalType { get { return GetChoiceValue(startTypeCombo); } set { SelectChoice(startTypeCombo, value); } }
        [Category("线路")] public string DispatchNumberStart { get { return dispatchStartText.Text; } set { dispatchStartText.Text = value ?? string.Empty; } }
        [Category("线路")] public string EndName { get { return endNameText.Text; } set { endNameText.Text = value ?? string.Empty; } }
        [Category("线路")] public string EndTerminalType { get { return GetChoiceValue(endTypeCombo); } set { SelectChoice(endTypeCombo, value); } }
        [Category("线路")] public string DispatchNumberEnd { get { return dispatchEndText.Text; } set { dispatchEndText.Text = value ?? string.Empty; } }
        [Category("线路")] public string LineName { get { return lineNameText.Text; } set { lineNameText.Text = value ?? string.Empty; } }
        [Category("线路")] public string CableModel { get { return cableModelText.Text; } set { cableModelText.Text = value ?? string.Empty; } }

        [Category("电缆")]
        public string CableMode
        {
            get { return cachedCableMode; }
            set { EnsureGeometryEditable(); SelectChoice(cableModeCombo, ProjectModes.Normalize(value)); ApplyModeState(true); }
        }
        [Category("负载")]
        public string LoadType
        {
            get { return ProjectModes.DisplayName(CableMode); }
            set { CableMode = value; }
        }
        [Category("电容")]
        public string CapacitorId
        {
            get { return capacitorId; }
            set
            {
                capacitorId = NormalizeCapacitorId(value);
                if (CableMode == ProjectModes.Capacitor && !string.IsNullOrWhiteSpace(capacitorId))
                {
                    ApplyCapacitorIdentity(false);
                    MarkDirty();
                }
            }
        }
        public string RefreshCapacitorId()
        {
            EnsureGeometryEditable();
            capacitorId = CreateCapacitorId();
            if (CableMode == ProjectModes.Capacitor) ApplyCapacitorIdentity(true);
            MarkDirty();
            return capacitorId;
        }
        [Browsable(false)] public string AssetMode { get { return emptyState ? "Empty" : CableMode == ProjectModes.Capacitor ? "CapacitancePd" : "Cable"; } }
        [Category("电缆")] public double CableLengthA { get { return Decimal.ToDouble(lengthANumber.Value); } set { SetPhaseCableLength(0, value); } }
        [Category("电缆")] public double CableLengthB { get { return Decimal.ToDouble(lengthBNumber.Value); } set { SetPhaseCableLength(1, value); } }
        [Category("电缆")] public double CableLengthC { get { return Decimal.ToDouble(lengthCNumber.Value); } set { SetPhaseCableLength(2, value); } }
        [Browsable(false)] public double CableLengthMetres { get { return Math.Max(CableLengthA, Math.Max(CableLengthB, CableLengthC)); } }
        [Category("电缆")] public string PhaseVoltage { get { return GetChoiceValue(phaseVoltageCombo); } set { SelectChoice(phaseVoltageCombo, value); } }
        [Category("电缆")] public DateTime LayingDate { get { return layingDatePicker.Value.Date; } set { layingDatePicker.Value = ClampDate(value, layingDatePicker); } }
        [Category("试验")]
        public string PressureMode { get { return GetChoiceValue(pressureModeCombo); } set { SelectChoice(pressureModeCombo, value); LoadPressurePreset(); } }
        [Category("项目")] [DefaultValue("新建项目")] public string SaveDisposition { get; set; } = "新建项目";
        [Category("项目")] [DefaultValue(true)] public bool ShowDialogMessages { get; set; } = true;

        [Category("绝缘")] public string InsulationA1 { get { return insulationA1Text.Text; } set { insulationA1Text.Text = value ?? string.Empty; } }
        [Category("绝缘")] public string InsulationA2 { get { return insulationA2Text.Text; } set { insulationA2Text.Text = value ?? string.Empty; } }
        [Category("绝缘")] public string InsulationB1 { get { return insulationB1Text.Text; } set { insulationB1Text.Text = value ?? string.Empty; } }
        [Category("绝缘")] public string InsulationB2 { get { return insulationB2Text.Text; } set { insulationB2Text.Text = value ?? string.Empty; } }
        [Category("绝缘")] public string InsulationC1 { get { return insulationC1Text.Text; } set { insulationC1Text.Text = value ?? string.Empty; } }
        [Category("绝缘")] public string InsulationC2 { get { return insulationC2Text.Text; } set { insulationC2Text.Text = value ?? string.Empty; } }
        [Category("项目")] public string Notes { get { return notesText.Text; } set { notesText.Text = value ?? string.Empty; } }

        public bool ReloadSettings()
        {
            try {
                bool useRememberedMode = emptyState && !IsDirty && String.IsNullOrEmpty(loadedProjectPath);
                catalog = ReferenceCatalog.Load(settingsPath);
                bool previous = suppressChanges; suppressChanges = true;
                try {
                    ApplyCatalogToControls();
                    if (useRememberedMode) { SelectChoice(pressureModeCombo, catalog.LastPressureMode); LoadPressurePreset(); }
                } finally { suppressChanges = previous; }
                string warning = catalog.RecoveryRequired ? "配置缓存读取异常，原件已保留 · " + catalog.LoadWarning : string.Empty;
                CompleteOperation("ReloadSettings", !catalog.RecoveryRequired, warning);
                if (catalog.RecoveryRequired) SetStatus(warning, false);
                return !catalog.RecoveryRequired;
            }
            catch (Exception exception) { CompleteOperation("ReloadSettings", false, exception.Message); return false; }
        }

        public bool SaveSettings()
        {
            try { CaptureHistory(); catalog.Save(settingsPath); CompleteOperation("SaveSettings", true, string.Empty); return true; }
            catch (Exception exception) { CompleteOperation("SaveSettings", false, exception.Message); return false; }
        }

        static DateTime ClampDate(DateTime value, DateTimePicker picker)
        {
            if (value < picker.MinDate) return picker.MinDate;
            if (value > picker.MaxDate) return picker.MaxDate;
            return value;
        }

        static void SetNumber(NumericUpDown control, double value)
        {
            decimal converted = RequirePresentableNumber(control, value);
            control.Value = decimal.Round(converted, control.DecimalPlaces, MidpointRounding.AwayFromZero);
        }

        static void SetLoadedLength(NumericUpDown control, double value)
        {
            decimal converted = RequirePresentableNumber(control, value);
            // DecimalPlaces controls the visible precision, not the authoritative
            // loaded length. Rounding that length falsely dirties locked geometry.
            control.Value = converted;
        }

        static decimal RequirePresentableNumber(NumericUpDown control, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException("value");
            decimal converted = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (converted < control.Minimum || converted > control.Maximum) throw new ArgumentOutOfRangeException("value");
            return converted;
        }

        static double ParseDisplayNumber(string text)
        {
            double value;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        static void SelectOrAdd(ComboBox combo, string value)
        {
            string text = value ?? string.Empty;
            int index = combo.FindStringExact(text);
            if (index < 0 && text.Length > 0) { combo.Items.Add(text); index = combo.FindStringExact(text); }
            if (index >= 0) combo.SelectedIndex = index;
            else combo.Text = text;
        }
    }
}
