using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;

namespace Xiongxin.OWTS.Desktop
{
    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class ConfigIgnoreAttribute : Attribute
    {
    }

    internal sealed class HardwareSettings
    {
        public bool Enabled { get; set; }
        public string SelectedHubMac { get; set; }
        public string ControllerLocation { get; set; }
        public string AcquisitionLocation { get; set; }
        public string ControllerSerial { get; set; }
        public string AcquisitionSerial { get; set; }
        public string ExcludedAcquisitionSerial { get; set; }
        public string DriverMarkerPath { get; set; }
        public string DriverPackagePath { get; set; }
        public string StatusJsonPath { get; set; }
        public string ConnectorExecutablePath { get; set; }
        public string IntegrationExecutablePath { get; set; }
        public string FirstConfigurationToolPath { get; set; }
        public string DriverInstallerPath { get; set; }
        public string Task39InputSettingsPath { get; set; }

        public HardwareSettings()
        {
            Enabled = false;
            SelectedHubMac = String.Empty;
            ControllerLocation = String.Empty;
            AcquisitionLocation = String.Empty;
            ControllerSerial = String.Empty;
            AcquisitionSerial = String.Empty;
            ExcludedAcquisitionSerial = String.Empty;
            DriverMarkerPath = String.Empty;
            DriverPackagePath = String.Empty;
            StatusJsonPath = String.Empty;
            ConnectorExecutablePath = String.Empty;
            IntegrationExecutablePath = DefaultHardwareToolPath("HardwareIntegration.exe");
            FirstConfigurationToolPath = DefaultHardwareToolPath("HV-DAC-Xpert.exe");
            DriverInstallerPath = String.Empty;
            Task39InputSettingsPath = String.Empty;
        }

        private static string DefaultHardwareToolPath(string fileName)
        {
            string assemblyDirectory = Path.GetDirectoryName(typeof(HardwareSettings).Assembly.Location);
            if (String.IsNullOrWhiteSpace(assemblyDirectory)) assemblyDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return Path.GetFullPath(Path.Combine(
                assemblyDirectory,
                "modules", "HardwareIntegration", fileName));
        }

        public HardwareSettings Clone()
        {
            return (HardwareSettings)MemberwiseClone();
        }
    }

    internal sealed class TaskMinus3AnalysisSettings
    {
        public double AttenuationTolerancePercent { get; set; }
        public double JointApproximationErrorPercent { get; set; }
        public string EndpointPolicy { get; set; }
        public double CorrelationErrorPercent { get; set; }
        public double AutomaticThresholdMultiplier { get; set; }
        public int MaximumPulseCount { get; set; }
        public int CalibrationAverageCount { get; set; }
        public int MultipleReflectionSuppressionCount { get; set; }
        public int AlgorithmIndex { get; set; }
        public bool UseFirstArrivalTiming { get; set; }
        public bool PhaseFilterEnabled { get; set; }
        public bool Quadrant0To90 { get; set; }
        public bool Quadrant90To180 { get; set; }
        public bool Quadrant180To270 { get; set; }
        public bool Quadrant270To360 { get; set; }
        public string ModelLibraryPath { get; set; }

        public TaskMinus3AnalysisSettings()
        {
            AttenuationTolerancePercent = 15.0;
            JointApproximationErrorPercent = 1.0;
            EndpointPolicy = "2;1.5;OR;OR";
            CorrelationErrorPercent = 30.0;
            AutomaticThresholdMultiplier = 3.0;
            MaximumPulseCount = 30;
            CalibrationAverageCount = 16;
            MultipleReflectionSuppressionCount = 1;
            AlgorithmIndex = 5;
            UseFirstArrivalTiming = false;
            PhaseFilterEnabled = false;
            Quadrant0To90 = true;
            Quadrant90To180 = true;
            Quadrant180To270 = true;
            Quadrant270To360 = true;
            ModelLibraryPath = String.Empty;
        }

        public TaskMinus3AnalysisSettings Clone()
        {
            return (TaskMinus3AnalysisSettings)MemberwiseClone();
        }
    }

    internal sealed class AppSettings
    {
        public const int CurrentSchemaVersion = 3;
        internal const int MaximumSettingsBytes = 4 * 1024 * 1024;
        internal const int MaximumSettingsCharacters = 4 * 1024 * 1024;

        public int SchemaVersion { get; set; }
        public string DataRoot { get; set; }
        public double DividerRatio { get; set; }
        public double PressureRatio { get; set; }
        public double PdSampleRateHz { get; set; }
        public int PdSampleBits { get; set; }
        public double PdCalibrationCorrectionFactor { get; set; }
        public double InductanceHenry { get; set; }
        public double InductorDcResistanceOhm { get; set; }
        public double MaximumVoltageKv { get; set; }
        public bool EnableFinePressureRamp { get; set; }
        public double PressureRampStepV { get; set; }
        public int PressureRampIntervalMs { get; set; }
        public double EndResidualPercent { get; set; }
        public bool FinePressureThresholdAutomatic { get; set; }
        double finePressureThresholdKv;
        public double FinePressureThresholdKv
        {
            get { return FinePressureThresholdAutomatic ? MaximumVoltageKv * .15 : finePressureThresholdKv; }
            set { finePressureThresholdKv = value; }
        }
        public double DeviceTriggerVolts { get; set; }
        public string DeviceProfilesXml { get; set; }
        public string ActiveDeviceProfileKey { get; set; }
        [ConfigIgnore] public string DeviceProfilesOriginXml { get; set; }
        [ConfigIgnore] public bool DeviceProfileEditAuthorized { get; set; }
        // A manufacturer-settings grant covers only the fields owned by that
        // page; it must not become a blanket device-parameter edit grant.
        [ConfigIgnore] public bool ExpertDeviceProfileEditAuthorized { get; set; }
        public double NoiseSimilarityTolerancePercent {get;set;}
        public bool SupportsCosineVlf { get; set; }
        public bool SupportsCombinedVlf { get; set; }
        public double OscillatingPdCaptureDurationMs { get; set; }
        public double FrequencyReminderMinHz { get; set; }
        public double FrequencyReminderMaxHz { get; set; }
        public double FrequencyDangerThresholdHz { get; set; }
        public double CapacitanceNormalMinUf { get; set; }
        public double CapacitanceNormalMaxUf { get; set; }
        public bool UseCompensationCapacitance { get; set; }
        public double CompensationCapacitanceUf { get; set; }
        public bool FrequencyReminderEnabled { get; set; }
        public int AutomaticPressureCountdownSeconds { get; set; }
        public bool AutomaticPressureCountdownEnabled { get; set; }
        public int LocationSaveCycleCount { get; set; }
        public bool PressureOscillationCircuitConfirmed { get; set; }
        public int PressureRangeEvaluationCycleCount { get; set; }
        public string CycleCountMigrationHistory { get; set; }
        public bool AnalyzeZeroVoltageData { get; set; }
        // Location-result filtering, independent of TaskMinus3's locator method.
        public string LocationFilterAlgorithmId { get; set; }
        public double LocationSuspiciousMinimumPercent { get; set; }
        public double LocationManualAcceptanceMinimumPercent { get; set; }
        public double LocationAutomaticAcceptanceMinimumPercent { get; set; }
        public bool AutomaticRangeReminderEnabled { get; set; }
        public bool ShowProjectCreationGuide { get; set; }
        public HvupVoltageSource HvupVoltageSource { get; set; }
        public ReportOutputFormat ReportOutputFormat { get; set; }
        public bool GenerateStateGridReport { get; set; }
        public bool EnableAiFeatures { get; set; }
        public bool EnableDefectRecognition { get; set; }
        public string DefectModelKind { get; set; }
        public string CompetitionModelPath { get; set; }
        public bool EnableCompetitionDefectModule { get; set; }
        public string Task35ModelPath { get; set; }
        public string Task36ModelPath { get; set; }
        public string Task37ModelPath { get; set; }
        public string Task38ModelPath { get; set; }
        public string UnifiedAnalysisModelPath { get; set; }
        public string Task6Path { get; set; }
        public string ReportPath { get; set; }
        public string ReportOutputPath { get; set; }
        public string ReportTemplatePath { get; set; }
        public string SoftwareModel { get; set; }
        public string WindowTitle { get; set; }
        public string LogoPath { get; set; }
        public string IconPath { get; set; }
        public string ManualPath { get; set; }
        public string CopyrightText { get; set; }
        public string LanguageCode { get; set; }
        public TaskMinus3AnalysisSettings TaskMinus3 { get; set; }
        public HardwareSettings Hardware { get; set; }
        public string DeviceParametersReviewedHash { get; set; }
        public string DeviceParametersReviewedIdentity { get; set; }
        public string DeviceParametersReviewedUtc { get; set; }

        [ConfigIgnore]
        public bool SessionHardwareForcedOffline { get; set; }

        // A load-time compatibility notice is session state, never an XML field.
        internal string SessionConfigurationNotice { get; private set; }
        internal string SessionLocationScoreDiagnostic { get; private set; }

        [ConfigIgnore]
        public double VoltageGain
        {
            get { return PressureRatio; }
            set { PressureRatio = value; }
        }

        [ConfigIgnore]
        public string GlobalLanguageCode
        {
            get { return LanguageCode; }
            set { LanguageCode = value; }
        }

        public AppSettings()
        {
            SchemaVersion = CurrentSchemaVersion;
            NoiseSimilarityTolerancePercent=1;
            DataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "XIONGXIN", "HVX Data");
            DividerRatio = 12.0;
            PressureRatio = 6000.0;
            // PicoScope 5242D R6 acquisition baseline: timebase 3 returns 8 ns
            // (125 MS/s).  In 15-bit mode the +/-10 mV range has 13 hardware bits.
            PdSampleRateHz = 125000000.0;
            PdSampleBits = 15;
            PdCalibrationCorrectionFactor = 1.0;
            InductanceHenry = 1.15;
            InductorDcResistanceOhm = 26.0;
            MaximumVoltageKv = 30.0;
            EnableFinePressureRamp = false;
            PressureRampStepV = .01;
            PressureRampIntervalMs = 1;
            EndResidualPercent = 4;
            FinePressureThresholdAutomatic = true;
            FinePressureThresholdKv = MaximumVoltageKv * .15;
            DeviceTriggerVolts = 1.5;
            OscillatingPdCaptureDurationMs = 60.0;
            FrequencyReminderMinHz = 20;
            FrequencyReminderMaxHz = 500;
            FrequencyDangerThresholdHz = 850;
            CapacitanceNormalMinUf = 0.02;
            CapacitanceNormalMaxUf = 8;
            UseCompensationCapacitance = false;
            CompensationCapacitanceUf = 0.2;
            FrequencyReminderEnabled = true;
            AutomaticPressureCountdownSeconds = 1;
            AutomaticPressureCountdownEnabled = true;
            LocationSaveCycleCount = 1;
            LocationFilterAlgorithmId = "B";
            LocationSuspiciousMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultSuspiciousMinimumPercent;
            LocationManualAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultManualAcceptanceMinimumPercent;
            LocationAutomaticAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultAutomaticAcceptanceMinimumPercent;
            // Legacy feature preference only, never a site/HV attestation.
            // Oscillation metrics are evaluated from the captured waveform.
            PressureOscillationCircuitConfirmed = true;
            PressureRangeEvaluationCycleCount = 1;
            CycleCountMigrationHistory = String.Empty;
            ShowProjectCreationGuide = true;
            HvupVoltageSource = HvupVoltageSource.Divider;
            ReportOutputFormat = ReportOutputFormat.PdfAndWord;
            GenerateStateGridReport = false;
            EnableAiFeatures = true;
            // Missing legacy field preserves the previous recognition capability.
            EnableDefectRecognition = true;
            DefectModelKind = DefectModelCatalog.Standard;
            CompetitionModelPath = String.Empty;
            Task35ModelPath = String.Empty;
            Task36ModelPath = String.Empty;
            Task37ModelPath = String.Empty;
            Task38ModelPath = String.Empty;
            UnifiedAnalysisModelPath = String.Empty;
            Task6Path = String.Empty;
            ReportPath = String.Empty;
            ReportOutputPath = Path.Combine(DataRoot, "Reports");
            ReportTemplatePath = String.Empty;
            SoftwareModel = "HV-DAC Xpert";
            WindowTitle = String.Empty;
            LogoPath = String.Empty;
            IconPath = String.Empty;
            ManualPath = String.Empty;
            CopyrightText = "(C) 2026. All rights reserved.";
            LanguageCode = "system";
            TaskMinus3 = new TaskMinus3AnalysisSettings();
            Hardware = new HardwareSettings();
        }

        public AppSettings Clone()
        {
            AppSettings copy = (AppSettings)MemberwiseClone();
            copy.TaskMinus3 = TaskMinus3 == null
                ? new TaskMinus3AnalysisSettings()
                : TaskMinus3.Clone();
            copy.Hardware = Hardware == null ? new HardwareSettings() : Hardware.Clone();
            return copy;
        }

        public static AppSettings Load()
        {
            return Load(AppPaths.SettingsFile);
        }

        public static AppSettings Load(string path)
        {
            AppSettings settings;
            string message;
            if (TryLoad(path, out settings, out message)) return settings;
            return new AppSettings();
        }

        public static bool TryLoad(string path, out AppSettings settings, out string message)
        {
            settings = new AppSettings();
            message = String.Empty;
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    message = "配置文件路径为空，已使用默认配置。";
                    return false;
                }
                if (!File.Exists(path))
                {
                    message = "配置文件不存在，已使用默认配置。";
                    return true;
                }

                XmlDocument document = ReadSettingsDocument(path);

                XmlElement root = document.DocumentElement;
                if (root == null || !String.Equals(root.Name, "HVXUnifiedDesktopSettings", StringComparison.Ordinal))
                    throw new InvalidDataException("配置文件根节点无效。");

                ReadObject(root, settings);
                if (settings.TaskMinus3 == null) settings.TaskMinus3 = new TaskMinus3AnalysisSettings();
                MigrateLegacyMinus3(root, settings.TaskMinus3);
                if (settings.Hardware == null) settings.Hardware = new HardwareSettings();
                settings.Normalize();
                bool cyclesMigrated=settings.MigrateCycleCountLimits();
                settings.MigrateCaptureDuration();
                if(!HVX.LocationScoring.LocationScorePolicy.IsValid(settings.LocationSuspiciousMinimumPercent,settings.LocationManualAcceptanceMinimumPercent,settings.LocationAutomaticAcceptanceMinimumPercent))
                {
                    settings.LocationSuspiciousMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultSuspiciousMinimumPercent;
                    settings.LocationManualAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultManualAcceptanceMinimumPercent;
                    settings.LocationAutomaticAcceptanceMinimumPercent=HVX.LocationScoring.LocationScorePolicy.DefaultAutomaticAcceptanceMinimumPercent;
                    settings.SessionLocationScoreDiagnostic="Invalid persisted location-score limits; using safe review defaults 30/45/50 percent. Original configuration was not modified.";
                }
                settings.DeviceProfilesOriginXml = settings.DeviceProfilesXml;

                string validation;
                if (!settings.TryValidate(out validation))
                    throw new InvalidDataException(validation);

                message = cyclesMigrated ? "配置已加载。原有周期数超过 5，已调整为 5；迁移记录将在保存设置时保留。" : "配置已加载。";
                return true;
            }
            catch (Exception error)
            {
                // A damaged or incompatible file must never implicitly enable hardware.
                settings = new AppSettings();
                settings.Hardware.Enabled = false;
                message = "配置读取失败，已安全回退为离线默认配置：" + error.Message;
                settings.SessionLocationScoreDiagnostic=message;
                return false;
            }
        }

        public void Save()
        {
            Save(AppPaths.SettingsFile);
        }

        public void Save(string path)
        {
            SaveCore(path, null, false, false, null);
        }

        // Compare-and-save uses the same cross-process gate as every ordinary
        // Save. The caller may not turn a previously reviewed offline profile
        // into hardware mode if another process saved a different profile.
        public bool TrySaveIfUnchanged(string expectedDigest, bool expectedAbsent)
        {
            return TrySaveIfUnchanged(AppPaths.SettingsFile, expectedDigest, expectedAbsent);
        }

        public bool TrySaveIfUnchanged(string path, string expectedDigest, bool expectedAbsent)
        {
            return TrySaveIfUnchanged(path,expectedDigest,expectedAbsent,null);
        }

        internal bool TrySaveIfUnchanged(string expectedDigest, bool expectedAbsent,
            Func<bool> commitGuard)
        {
            return TrySaveIfUnchanged(AppPaths.SettingsFile,expectedDigest,expectedAbsent,commitGuard);
        }

        internal bool TrySaveIfUnchanged(string path, string expectedDigest, bool expectedAbsent,
            Func<bool> commitGuard)
        {
            if (!expectedAbsent && String.IsNullOrWhiteSpace(expectedDigest))
                throw new ArgumentException("必须提供原配置摘要。", "expectedDigest");
            return SaveCore(path, expectedDigest, expectedAbsent, true, commitGuard);
        }

        private bool SaveCore(string path, string expectedDigest, bool expectedAbsent, bool conditional,
            Func<bool> commitGuard)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置文件路径不能为空。", "path");

            Normalize();
            string validation;
            if (!TryValidate(out validation)) throw new InvalidDataException(validation);

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (String.IsNullOrEmpty(directory)) directory = Environment.CurrentDirectory;
            Directory.CreateDirectory(directory);

            using (FileStream gate = AcquireSaveGate(fullPath))
            {
                bool liveProfile = String.Equals(fullPath,Path.GetFullPath(AppPaths.SettingsFile),
                    StringComparison.OrdinalIgnoreCase);
                if (Hardware.Enabled && (SessionHardwareForcedOffline ||
                    (liveProfile && FirstHardwareProvisioning.RequiresSessionOffline())))
                    throw new InvalidOperationException("当前一体化安装未完成硬件阶段，不能启用硬件模式。");
                if (conditional)
                {
                    bool exists = File.Exists(fullPath);
                    if (expectedAbsent ? exists : !exists ||
                        !String.Equals(FileDigest(fullPath), expectedDigest, StringComparison.Ordinal))
                        return false;
                }
                RequireCommitGuard(commitGuard);
                AppSettings persisted = Clone();
                if (SessionHardwareForcedOffline && File.Exists(fullPath))
                {
                    AppSettings onDisk;
                    string loadMessage;
                    if (!TryLoad(fullPath,out onDisk,out loadMessage))
                        throw new InvalidDataException("原配置读取失败；不能在强制离线会话中覆盖：" + loadMessage);
                    if (!SameHardwareExceptEnabled(Hardware,onDisk.Hardware))
                        throw new InvalidOperationException("本次离线会话不允许修改原有硬件身份；请重新安装硬件组件后重启软件。");
                    persisted = Clone();
                    persisted.Hardware = onDisk.Hardware.Clone();
                    if (!persisted.TryValidate(out validation))
                        throw new InvalidDataException("保留的原硬件配置与当前设置不兼容：" + validation);
                }
                DeviceParameterProfiles.PrepareSave(persisted, fullPath);
                if (!persisted.TryValidate(out validation)) throw new InvalidDataException(validation);

            string temporaryPath = Path.Combine(
                directory,
                Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backupPath = fullPath + ".bak";

            try
            {
                XmlWriterSettings options = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = true,
                    NewLineChars = Environment.NewLine,
                    NewLineHandling = NewLineHandling.Replace,
                    CloseOutput = false
                };
                using (FileStream stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (XmlWriter writer = XmlWriter.Create(stream, options))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("HVXUnifiedDesktopSettings");
                    writer.WriteAttributeString("schemaVersion", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
                    WriteObject(writer, persisted);
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                    writer.Flush();
                    stream.Flush(true);
                }

                // Escaping and UTF-8 can make a valid string budget exceed the
                // on-disk budget. Validate the staged document before replacing
                // anything; failure leaves the previous configuration intact.
                ReadSettingsDocument(temporaryPath);

                // Recheck after serialization: an identity receipt may expire
                // while writing the temporary XML, even after the gate is held.
                RequireCommitGuard(commitGuard);
                if (File.Exists(fullPath))
                {
                    File.Replace(temporaryPath, fullPath, backupPath, true);
                    TryDelete(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
                DeviceProfilesXml = persisted.DeviceProfilesXml;
                DeviceProfilesOriginXml = persisted.DeviceProfilesXml;
                ActiveDeviceProfileKey = persisted.ActiveDeviceProfileKey;
                PressureRatio = persisted.PressureRatio;
                DeviceParametersReviewedHash = persisted.DeviceParametersReviewedHash;
                DeviceParametersReviewedIdentity = persisted.DeviceParametersReviewedIdentity;
                DeviceParametersReviewedUtc = persisted.DeviceParametersReviewedUtc;
                DeviceProfileEditAuthorized = false;
                ExpertDeviceProfileEditAuthorized = false;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
            }
            return true;
        }

        private static void RequireCommitGuard(Func<bool> commitGuard)
        {
            if (commitGuard != null && !commitGuard())
                throw new InvalidOperationException("首次绑定的实时核验凭据已过期或改变；未写入配置，请重新绑定。");
        }

        private static FileStream AcquireSaveGate(string fullPath)
        {
            // Keep the empty lock file: deleting it lets a waiting process lock
            // a different file object and defeats cross-process serialization.
            string gatePath = fullPath + ".write.lock";
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { return new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException("等待其他程序保存设置超时。");
                    Thread.Sleep(50);
                }
            }
        }

        internal static string FileDigest(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private static bool SameHardwareExceptEnabled(HardwareSettings current,HardwareSettings saved)
        {
            if (current == null || saved == null) return current == saved;
            foreach (PropertyInfo property in typeof(HardwareSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.Name == "Enabled" || !property.CanRead) continue;
                object a=property.GetValue(current,null), b=property.GetValue(saved,null);
                if (!Object.Equals(a,b)) return false;
            }
            return true;
        }

        public bool TryValidate(out string message)
        {
            if (!ValidateStringBudgets(this, out message)) return false;
            message = String.Empty;
            if (!ValidPath(DataRoot, false)) return Fail("数据根目录不是有效路径。", out message);
            if (!FiniteBetween(DividerRatio, 0.000001, 1000000000.0)) return Fail("分压比必须是大于 0 的有限数值。", out message);
            if (!FiniteBetween(PressureRatio, 0.000001, 1000000000.0)) return Fail("加压比必须是大于 0 的有限数值。", out message);
            if (!FiniteBetween(PressureRampStepV,.0001,.1)||PressureRampIntervalMs<1||PressureRampIntervalMs>1000)
                return Fail("普通加压步长须为0.0001～0.1 V，循环请求间隔须为1～1000 ms。",out message);
            if (!FiniteBetween(EndResidualPercent,.01,100))
                return Fail("结束残压百分比须大于0且不超过100%。",out message);
            if (!FiniteBetween(PdSampleRateHz, 1.0, 1000000000000.0)) return Fail("局放采样率必须在 1 Hz 到 1 THz 之间。", out message);
            if (PdSampleBits < 1 || PdSampleBits > 64) return Fail("采样位数必须在 1 到 64 位之间。", out message);
            if (!FiniteBetween(PdCalibrationCorrectionFactor, 0.000001, 1000000.0)) return Fail("校准系数修正必须是大于 0 的有限数值。", out message);
            if (!FiniteBetween(InductanceHenry, 0.000000001, 1000000000.0)) return Fail("电感量必须是大于 0 的有限数值。", out message);
            if (!FiniteBetween(InductorDcResistanceOhm, 0.0, 1000000000.0)) return Fail("电感直流电阻必须是非负有限数值。", out message);
            if (!FiniteBetween(MaximumVoltageKv, 0.000001, 1000000.0)) return Fail("最高限压必须是大于 0 的有限数值。", out message);
            if (!FiniteBetween(FinePressureThresholdKv, 0.00000015, 1000000.0) || FinePressureThresholdKv > MaximumVoltageKv)
                return Fail("精细化加压阈值须为大于0且不超过设备最高限压的有限数值。", out message);
            if (!FiniteBetween(DeviceTriggerVolts, -4.999999, 4.999999)) return Fail("触发阈值必须在 −5 V 与 +5 V 之间。", out message);
            if (!FiniteBetween(OscillatingPdCaptureDurationMs, 0.001, 60.0)) return Fail("局放采集长度必须在 0.001 ms 到 60 ms 之间。", out message);
            if (!FiniteBetween(FrequencyReminderMinHz, 0.001, 1000000) ||
                !FiniteBetween(FrequencyReminderMaxHz, 0.001, 1000000) ||
                !FiniteBetween(FrequencyDangerThresholdHz, 0.001, 1000000) ||
                FrequencyReminderMinHz >= FrequencyReminderMaxHz || FrequencyReminderMaxHz >= FrequencyDangerThresholdHz)
                return Fail("振荡频率阈值须满足 0 < 下限 < 上限 < 红色警告阈值（最大 1 MHz）。", out message);
            if (!FiniteBetween(CapacitanceNormalMinUf, 0.000001, 1000000) ||
                !FiniteBetween(CapacitanceNormalMaxUf, 0.000001, 1000000) || CapacitanceNormalMinUf >= CapacitanceNormalMaxUf)
                return Fail("电容正常范围须满足 0 < 下限 < 上限（单位 μF）。", out message);
            if (AutomaticPressureCountdownSeconds < 1 || AutomaticPressureCountdownSeconds > 10)
                return Fail("自动加压间隔倒计时必须为 1 至 10 秒。", out message);
            if (!FiniteBetween(CompensationCapacitanceUf, 0, 1000000))
                return Fail("补偿电容须为非负有限数值（单位 μF）。", out message);
            if (LocationSaveCycleCount < 1 || LocationSaveCycleCount > 5) return Fail("定位保存周期数必须为 1 至 5 的整数。", out message);
            if(LocationFilterAlgorithmId!="B")
                return Fail("定位过滤算法无效，请检查设置。",out message);
            if(!HVX.LocationScoring.LocationScorePolicy.IsValid(LocationSuspiciousMinimumPercent,LocationManualAcceptanceMinimumPercent,LocationAutomaticAcceptanceMinimumPercent))
                return Fail("定位评分限值须满足 0 ≤ 可疑下限 ≤ 手动接受下限 ≤ 自动接受下限 ≤ 100。",out message);
            if (PressureRangeEvaluationCycleCount < 1 || PressureRangeEvaluationCycleCount > 5) return Fail("量程评估周期数必须为 1 至 5 的整数。", out message);
            if (!Enum.IsDefined(typeof(HvupVoltageSource), HvupVoltageSource)) return Fail("电压反馈源无效，请选择 DIV 或 Vmp。", out message);
            if (!Enum.IsDefined(typeof(ReportOutputFormat), ReportOutputFormat)) return Fail("报告出具版式无效。", out message);
            if (!KnownLanguage(LanguageCode)) return Fail("全局语言代码无效。", out message);

            string[] optionalPaths =
            {
                Task35ModelPath, Task36ModelPath, Task37ModelPath, Task38ModelPath,
                UnifiedAnalysisModelPath, Task6Path, ReportPath, ReportOutputPath, ReportTemplatePath,
                LogoPath, IconPath, ManualPath
            };
            for (int index = 0; index < optionalPaths.Length; index++)
                if (!ValidPath(optionalPaths[index], true)) return Fail("配置中包含无效的文件或目录路径。", out message);
            if (!OemBranding.ValidateAssetPath(LogoPath, out message) ||
                !OemBranding.ValidateAssetPath(IconPath, out message)) return false;

            if (TaskMinus3 == null) return Fail("分析参数缺失。", out message);
            double endpointWidth, endpointRatio;
            bool endpointFirstAnd, endpointSecondAnd;
            if (!TryParseEndpointPolicy(TaskMinus3.EndpointPolicy, out endpointWidth, out endpointRatio,
                out endpointFirstAnd, out endpointSecondAnd, out message)) return false;
            if (!FiniteBetween(TaskMinus3.AttenuationTolerancePercent, 0.0, 100.0)) return Fail("衰减容差必须在 0% 到 100% 之间。", out message);
            if (!FiniteBetween(TaskMinus3.JointApproximationErrorPercent, 0.0, 100.0)) return Fail("近似接头误差必须在 0% 到 100% 之间。", out message);
            if(!FiniteBetween(NoiseSimilarityTolerancePercent,0,10))return Fail("干扰模板相关容差必须在 0%～10% 之间。",out message);
            if(TaskMinus3.CalibrationAverageCount<3||TaskMinus3.CalibrationAverageCount>256)return Fail("校准平均次数 N 必须为 3～256。",out message);
            if (!FiniteBetween(TaskMinus3.CorrelationErrorPercent, 0.0, 100.0)) return Fail("相关误差必须在 0% 到 100% 之间。", out message);
            if (!FiniteBetween(TaskMinus3.AutomaticThresholdMultiplier, 0.0, 1000.0)) return Fail("自动阈值倍数必须在 0 到 1000 之间。", out message);
            if (TaskMinus3.MaximumPulseCount < 1 || TaskMinus3.MaximumPulseCount > 100) return Fail("最大脉冲数必须在 1 到 100 之间。", out message);
            if (TaskMinus3.MultipleReflectionSuppressionCount < -1 || TaskMinus3.MultipleReflectionSuppressionCount > 100)
                return Fail("多重反射抑制次数必须为 -1（自动）或 0 到 100。", out message);
            // Retain legacy configuration values for viewing and unrelated settings
            // edits. The UI offers A/B/E/E+; execution rejects discontinued methods.
            if (TaskMinus3.AlgorithmIndex < 0 || TaskMinus3.AlgorithmIndex > 9) return Fail("定位方法设置无效，请重新选择 A、B、E 或 E+。", out message);
            if (!ValidPath(TaskMinus3.ModelLibraryPath, true)) return Fail("模型库路径无效。", out message);
            if (DefectModelKind != DefectModelCatalog.Standard) return Fail("缺陷识别仅支持标准模型。", out message);
            if (!ValidPath(CompetitionModelPath, true)) return Fail("竞赛模型路径无效。", out message);

            if (Hardware == null) return Fail("硬件配置缺失。", out message);
            if (!String.IsNullOrWhiteSpace(Hardware.SelectedHubMac) && !ValidMac(Hardware.SelectedHubMac))
                return Fail("设备网络地址必须为 12 位十六进制字符。", out message);
            if (Hardware.Enabled && String.IsNullOrWhiteSpace(Hardware.SelectedHubMac))
                return Fail("启用硬件模式时必须指定设备网络地址。", out message);
            if (Hardware.Enabled && String.IsNullOrWhiteSpace(Hardware.ControllerLocation))
                return Fail("启用硬件模式时必须指定 Controller Location。", out message);
            if (Hardware.Enabled && String.IsNullOrWhiteSpace(Hardware.AcquisitionLocation))
                return Fail("启用硬件模式时必须指定 Acquisition Location。", out message);
            if (!String.IsNullOrWhiteSpace(Hardware.AcquisitionSerial) &&
                !String.IsNullOrWhiteSpace(Hardware.ExcludedAcquisitionSerial) &&
                String.Equals(Hardware.AcquisitionSerial.Trim(), Hardware.ExcludedAcquisitionSerial.Trim(), StringComparison.OrdinalIgnoreCase))
                return Fail("采集设备序列号不能与排除设备序列号相同。", out message);

            string[] hardwarePaths =
            {
                Hardware.DriverMarkerPath, Hardware.DriverPackagePath, Hardware.StatusJsonPath,
                Hardware.ConnectorExecutablePath, Hardware.IntegrationExecutablePath,
                Hardware.FirstConfigurationToolPath, Hardware.DriverInstallerPath, Hardware.Task39InputSettingsPath
            };
            for (int index = 0; index < hardwarePaths.Length; index++)
                if (!ValidPath(hardwarePaths[index], true)) return Fail("高级硬件配置中包含无效路径。", out message);

            return true;
        }

        internal static bool TryParseEndpointPolicy(string value, out double width, out double ratio,
            out bool firstAnd, out bool secondAnd, out string message)
        {
            width = ratio = 0; firstAnd = secondAnd = false; message = String.Empty;
            string[] parts = (value ?? String.Empty).Split(';');
            if (parts.Length != 4 ||
                !Double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width) ||
                !Double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) ||
                !FiniteBetween(width, 1, 100) || !FiniteBetween(ratio, 1, 100) ||
                (parts[2] != "AND" && parts[2] != "OR") || (parts[3] != "AND" && parts[3] != "OR"))
                return Fail("首末端组合规则无效：两倍率须为 1～100 的有限数值，逻辑关系须为 AND 或 OR；未使用默认值替代。", out message);
            firstAnd = parts[2] == "AND"; secondAnd = parts[3] == "AND";
            return true;
        }

        private bool MigrateCycleCountLimits()
        {
            // Only previously valid legacy values are upgraded on file load.
            // New edits must satisfy validation; loading never writes the file
            // or applies an acquisition/hardware request.
            bool changed=false;
            if(LocationSaveCycleCount>5&&LocationSaveCycleCount<=100)
            {RecordCycleCountMigration("LocationSaveCycleCount",LocationSaveCycleCount);LocationSaveCycleCount=5;changed=true;}
            if(PressureRangeEvaluationCycleCount>5&&PressureRangeEvaluationCycleCount<=100)
            {RecordCycleCountMigration("PressureRangeEvaluationCycleCount",PressureRangeEvaluationCycleCount);PressureRangeEvaluationCycleCount=5;changed=true;}
            return changed;
        }
        private void RecordCycleCountMigration(string parameter,int previous)
        {
            string entry=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)+" "+parameter+"="+previous.ToString(CultureInfo.InvariantCulture)+"->5";
            CycleCountMigrationHistory=String.IsNullOrWhiteSpace(CycleCountMigrationHistory)?entry:CycleCountMigrationHistory+"\n"+entry;
        }
        private void Normalize()
        {
            SchemaVersion = CurrentSchemaVersion;
            DataRoot = Clean(DataRoot);
            Task35ModelPath = Clean(Task35ModelPath);
            Task36ModelPath = Clean(Task36ModelPath);
            Task37ModelPath = Clean(Task37ModelPath);
            Task38ModelPath = Clean(Task38ModelPath);
            UnifiedAnalysisModelPath = Clean(UnifiedAnalysisModelPath);
            Task6Path = Clean(Task6Path);
            ReportPath = Clean(ReportPath);
            ReportOutputPath = Clean(ReportOutputPath);
            ReportTemplatePath = Clean(ReportTemplatePath);
            SoftwareModel = Clean(SoftwareModel);
            LogoPath = Clean(LogoPath);
            IconPath = Clean(IconPath);
            ManualPath = Clean(ManualPath);
            CopyrightText = CopyrightText ?? String.Empty;
            LanguageCode = Clean(LanguageCode);
            // Only the historical zero preference is upgraded. An explicitly
            // saved 2..10 second countdown remains the user's chosen value.
            if(AutomaticPressureCountdownSeconds==0)AutomaticPressureCountdownSeconds=1;
            LocationFilterAlgorithmId=Clean(LocationFilterAlgorithmId).ToUpperInvariant();
            // The former A option is retired; retain every other explicit
            // setting and all saved-analysis numeric filter parameters.
            if(LocationFilterAlgorithmId=="A")LocationFilterAlgorithmId="B";
            if (TaskMinus3 == null) TaskMinus3 = new TaskMinus3AnalysisSettings();
            TaskMinus3.ModelLibraryPath = Clean(TaskMinus3.ModelLibraryPath);
            CompetitionModelPath = Clean(CompetitionModelPath);
            // Defect recognition keeps a single standard baseline. A former
            // competition-only path migrates into the loaded-model slot.
            if(String.IsNullOrWhiteSpace(TaskMinus3.ModelLibraryPath)&&!String.IsNullOrWhiteSpace(CompetitionModelPath))
                TaskMinus3.ModelLibraryPath=CompetitionModelPath;
            DefectModelKind = DefectModelCatalog.Standard;
            CompetitionModelPath = String.Empty;
            if (Hardware == null) Hardware = new HardwareSettings();
            Hardware.SelectedHubMac = NormalizeMac(Hardware.SelectedHubMac);
            Hardware.ControllerLocation = Clean(Hardware.ControllerLocation);
            Hardware.AcquisitionLocation = Clean(Hardware.AcquisitionLocation);
            Hardware.ControllerSerial = Clean(Hardware.ControllerSerial);
            Hardware.AcquisitionSerial = Clean(Hardware.AcquisitionSerial);
            Hardware.ExcludedAcquisitionSerial = Clean(Hardware.ExcludedAcquisitionSerial);
            Hardware.DriverMarkerPath = Clean(Hardware.DriverMarkerPath);
            Hardware.DriverPackagePath = Clean(Hardware.DriverPackagePath);
            Hardware.StatusJsonPath = Clean(Hardware.StatusJsonPath);
            Hardware.ConnectorExecutablePath = Clean(Hardware.ConnectorExecutablePath);
            Hardware.IntegrationExecutablePath = Clean(Hardware.IntegrationExecutablePath);
            Hardware.FirstConfigurationToolPath = Clean(Hardware.FirstConfigurationToolPath);
            Hardware.DriverInstallerPath = Clean(Hardware.DriverInstallerPath);
            Hardware.Task39InputSettingsPath = Clean(Hardware.Task39InputSettingsPath);
        }

        private static void MigrateLegacyMinus3(XmlElement root, TaskMinus3AnalysisSettings target)
        {
            XmlElement section = DirectChild(root, "TaskMinus3");
            if (section == null || target == null) return;

            double number;
            bool flag;
            if (DirectChild(section, "AttenuationTolerancePercent") == null &&
                TryLegacyDouble(section, "AttenuationDb", out number))
                target.AttenuationTolerancePercent = number;
            if (DirectChild(section, "AutomaticThresholdMultiplier") == null &&
                TryLegacyDouble(section, "TriggerThreshold", out number))
                target.AutomaticThresholdMultiplier = number;
            if (DirectChild(section, "MultipleReflectionSuppressionCount") == null &&
                TryLegacyDouble(section, "ReflectionSuppression", out number))
            {
                if (number != Math.Truncate(number) || number < Int32.MinValue || number > Int32.MaxValue)
                    throw new InvalidDataException("反射抑制次数必须为有效整数，请检查配置文件。");
                target.MultipleReflectionSuppressionCount = (int)number;
            }
            if (DirectChild(section, "UseFirstArrivalTiming") == null &&
                TryLegacyBool(section, "UseFirstArrival", out flag))
                target.UseFirstArrivalTiming = flag;
            if (DirectChild(section, "PhaseFilterEnabled") == null &&
                TryLegacyBool(section, "EnablePhaseFilter", out flag))
                target.PhaseFilterEnabled = flag;
            if (DirectChild(section, "ModelLibraryPath") == null)
            {
                XmlElement oldModel = DirectChild(section, "ModelPath");
                if (oldModel != null) target.ModelLibraryPath = oldModel.InnerText;
            }

            if (DirectChild(section, "Quadrant0To90") == null &&
                DirectChild(section, "Quadrant90To180") == null &&
                DirectChild(section, "Quadrant180To270") == null &&
                DirectChild(section, "Quadrant270To360") == null)
            {
                XmlElement oldQuadrants = DirectChild(section, "PhaseQuadrants");
                if (oldQuadrants != null) ApplyLegacyQuadrants(oldQuadrants.InnerText, target);
            }
        }

        private static bool TryLegacyDouble(XmlElement section, string name, out double value)
        {
            value = 0.0;
            XmlElement element = DirectChild(section, name);
            if (element == null) return false;
            if (!Double.TryParse(element.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                Double.IsNaN(value) || Double.IsInfinity(value))
                throw new InvalidDataException(ParameterLabel(name) + "不是有效数值，请检查配置文件。");
            return true;
        }

        private static bool TryLegacyBool(XmlElement section, string name, out bool value)
        {
            value = false;
            XmlElement element = DirectChild(section, name);
            if (element == null) return false;
            if (!Boolean.TryParse(element.InnerText, out value))
                throw new InvalidDataException(ParameterLabel(name) + "不是有效选项，请检查配置文件。");
            return true;
        }

        private static void ApplyLegacyQuadrants(string text, TaskMinus3AnalysisSettings target)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            bool first = false, second = false, third = false, fourth = false, recognized = false;
            string[] parts = text.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                string token = parts[index].Trim().ToUpperInvariant();
                if (token == "I" || token == "Q1" || token == "1" || token == "0-90") { first = true; recognized = true; }
                else if (token == "II" || token == "Q2" || token == "2" || token == "90-180") { second = true; recognized = true; }
                else if (token == "III" || token == "Q3" || token == "3" || token == "180-270") { third = true; recognized = true; }
                else if (token == "IV" || token == "Q4" || token == "4" || token == "270-360") { fourth = true; recognized = true; }
            }
            if (!recognized) return;
            target.Quadrant0To90 = first;
            target.Quadrant90To180 = second;
            target.Quadrant180To270 = third;
            target.Quadrant270To360 = fourth;
        }

        private static bool KnownLanguage(string code)
        {
            if (String.IsNullOrWhiteSpace(code)) return false;
            if (String.Equals(code, "system", StringComparison.OrdinalIgnoreCase)) return true;
            for (int index = 0; index < AppLanguages.All.Length; index++)
                if (String.Equals(AppLanguages.All[index].Code, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool FiniteBetween(double value, double minimum, double maximum)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= minimum && value <= maximum;
        }

        private static bool ValidPath(string value, bool allowEmpty)
        {
            if (String.IsNullOrWhiteSpace(value)) return allowEmpty;
            try
            {
                Path.GetFullPath(value.Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidMac(string value)
        {
            string normalized = NormalizeMac(value);
            if (normalized.Length != 12) return false;
            for (int index = 0; index < normalized.Length; index++)
                if (!Uri.IsHexDigit(normalized[index])) return false;
            return true;
        }

        private static string NormalizeMac(string value)
        {
            return Clean(value).Replace(":", String.Empty).Replace("-", String.Empty).ToUpperInvariant();
        }

        private static string Clean(string value)
        {
            return value == null ? String.Empty : value.Trim();
        }

        private static bool Fail(string value, out string message)
        {
            // Internal task identifiers are not operator-facing validation text.
            message = value.StartsWith("-3 ", StringComparison.Ordinal) ? value.Substring(3) : value;
            return false;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!String.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Temporary or backup cleanup is best effort; the committed settings remain valid.
            }
        }

        private static bool IsSimple(Type type)
        {
            return type == typeof(string) || type == typeof(bool) || type == typeof(int) ||
                   type == typeof(long) || type == typeof(double) || type == typeof(float) ||
                   type == typeof(decimal) || type.IsEnum;
        }

        private static void WriteObject(XmlWriter writer, object value)
        {
            PropertyInfo[] properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (int index = 0; index < properties.Length; index++)
            {
                PropertyInfo property = properties[index];
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                if (Attribute.IsDefined(property, typeof(ConfigIgnoreAttribute))) continue;

                object item = property.GetValue(value, null);
                writer.WriteStartElement(property.Name);
                if (item != null)
                {
                    if (IsSimple(property.PropertyType))
                        writer.WriteString(ToInvariantText(item, property.PropertyType));
                    else
                        WriteObject(writer, item);
                }
                writer.WriteEndElement();
            }
        }

        private static XmlDocument ReadSettingsDocument(string path)
        {
            var document = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumSettingsBytes)
                    throw new InvalidDataException("配置文件过大，请恢复有效的配置文件。");
                var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = MaximumSettingsCharacters,
                    MaxCharactersFromEntities = 1024, CloseInput = false };
                using (var reader = XmlReader.Create(stream, options)) document.Load(reader);
            }
            return document;
        }

        private void MigrateCaptureDuration()
        {
            // This is an explicit compatibility change for previously legal values,
            // not a recovery path for malformed or out-of-range configuration.
            if (!FiniteBetween(OscillatingPdCaptureDurationMs, 0.001, 86400000.0) ||
                OscillatingPdCaptureDurationMs <= 60.0) return;
            string previous = OscillatingPdCaptureDurationMs.ToString("R", CultureInfo.InvariantCulture);
            OscillatingPdCaptureDurationMs = 60.0;
            SessionConfigurationNotice = "原配置的局放采集长度为 " + previous +
                " ms，超过当前单段 60 ms 上限。本次运行已调整为 60 ms；其他配置保留，原配置文件未改动。请在设置中核对并保存。设备参数需要核对时，完成确认后才可加压。\r\n\r\n" +
                "The saved acquisition duration (" + previous +
                " ms) exceeds the 60 ms limit per segment. This session uses 60 ms; other settings and the original file are preserved. Review and save the settings. Any required device-parameter confirmation must be completed before applying voltage.";
        }

        private static int StringBudget(string name)
        {
            switch (name)
            {
                case "DeviceProfilesXml": return 1024 * 1024;
                case "SoftwareModel": case "WindowTitle": return 512;
                case "CopyrightText": return 2048;
                case "LanguageCode": case "DefectModelKind": return 64;
                default: return 32768;
            }
        }

        private static string ParameterLabel(string name)
        {
            switch (name)
            {
                case "MaximumVoltageKv": return "最高限压";
                case "DividerRatio": return "分压比";
                case "PressureRatio": return "加压比";
                case "DeviceTriggerVolts": case "TriggerThreshold": return "触发阈值";
                case "AnalyzeZeroVoltageData": return "零电压数据分析选项";
                case "SoftwareModel": return "软件型号";
                case "WindowTitle": return "窗口标题";
                case "CopyrightText": return "版权信息";
                case "LogoPath": return "标识图片路径";
                case "IconPath": return "图标路径";
                case "DeviceProfilesXml": return "设备配置记录";
                case "Hardware": return "硬件设置";
                case "TaskMinus3": return "分析设置";
                case "UseFirstArrivalTiming": case "UseFirstArrival": return "传播计时选项";
                default: return "配置参数";
            }
        }

        private static bool ValidateStringBudgets(object value, out string message)
        {
            message = String.Empty;
            if (value == null) return true;
            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 ||
                    Attribute.IsDefined(property, typeof(ConfigIgnoreAttribute))) continue;
                object item = property.GetValue(value, null);
                if (property.PropertyType == typeof(string))
                {
                    if (item != null && ((string)item).Length > StringBudget(property.Name))
                        return Fail(ParameterLabel(property.Name) + "内容过长，请缩短后重试。", out message);
                }
                else if (!IsSimple(property.PropertyType) && !ValidateStringBudgets(item, out message)) return false;
            }
            return true;
        }

        private static void ReadObject(XmlElement parent, object target)
        {
            PropertyInfo[] properties = target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (int index = 0; index < properties.Length; index++)
            {
                PropertyInfo property = properties[index];
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                if (Attribute.IsDefined(property, typeof(ConfigIgnoreAttribute))) continue;

                XmlElement element = DirectChild(parent, property.Name);
                if (element == null) continue;
                if (IsSimple(property.PropertyType))
                {
                    foreach (XmlNode child in element.ChildNodes)
                        if (child is XmlElement)
                            throw new InvalidDataException(ParameterLabel(property.Name) + "格式无效，请检查配置文件。");
                    string text = element.InnerText;
                    if (text.Length > (property.PropertyType == typeof(string) ? StringBudget(property.Name) : 256))
                        throw new InvalidDataException(ParameterLabel(property.Name) + "内容过长，请检查配置文件。");
                    object converted;
                    if (!TryConvert(text, property.PropertyType, out converted))
                        throw new InvalidDataException(ParameterLabel(property.Name) + "格式无效，请检查配置文件。");
                    property.SetValue(target, converted, null);
                }
                else
                {
                    foreach (XmlNode child in element.ChildNodes)
                        if ((child is XmlText || child is XmlCDataSection) && !String.IsNullOrWhiteSpace(child.Value))
                            throw new InvalidDataException(ParameterLabel(property.Name) + "格式无效，请检查配置文件。");
                    object nested = property.GetValue(target, null);
                    if (nested == null) nested = Activator.CreateInstance(property.PropertyType);
                    ReadObject(element, nested);
                    property.SetValue(target, nested, null);
                }
            }
        }

        private static XmlElement DirectChild(XmlElement parent, string name)
        {
            XmlElement result = null;
            for (XmlNode node = parent.FirstChild; node != null; node = node.NextSibling)
            {
                XmlElement element = node as XmlElement;
                if (element != null && String.Equals(element.Name, name, StringComparison.Ordinal))
                {
                    if (result != null) throw new InvalidDataException(ParameterLabel(name) + "重复出现，请检查配置文件。");
                    result = element;
                }
            }
            return result;
        }

        private static string ToInvariantText(object value, Type type)
        {
            if (type == typeof(bool)) return ((bool)value) ? "true" : "false";
            if (type.IsEnum) return Enum.GetName(type, value) ?? Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            IFormattable formattable = value as IFormattable;
            return formattable == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        private static bool TryConvert(string text, Type type, out object value)
        {
            value = null;
            try
            {
                if (type == typeof(string)) value = text ?? String.Empty;
                else if (type == typeof(bool)) value = Boolean.Parse(text);
                else if (type == typeof(int)) value = Int32.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (type == typeof(long)) value = Int64.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
                else if (type == typeof(double)) value = Double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                else if (type == typeof(float)) value = Single.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                else if (type == typeof(decimal)) value = Decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);
                else if (type.IsEnum) value = Enum.Parse(type, text, true);
                else return false;
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }
    }
}
