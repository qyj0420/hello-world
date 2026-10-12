using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    /// <summary>
    /// Drives the Task101 action mailboxes with deterministic, in-memory data when the
    /// application has explicitly entered simulation mode.  This class deliberately
    /// uses late binding so the desktop executable has no compile-time dependency on
    /// either Task101 module assembly.
    /// </summary>
    internal sealed class OfflineSimulationBridge : IDisposable
    {
        private const string CalibrationModuleId = "CalibrationTest";
        private const string PressureModuleId = "PressureTest";
        private const double CalibrationSampleIntervalSeconds = 8.0e-9; // 125 MS/s

        private readonly bool simulation;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private Timer pollTimer;
        private Control target;
        private ModuleKind moduleKind;
        private bool processing;
        private bool disposed;
        private bool referenceProfileEnabled;
        private ReferenceSimulationProfile referenceProfile;
        private string sourceDescription = String.Empty;
        public event EventHandler DescriptionChanged;
        public bool ReferenceProfileEnabled
        {
            get { return referenceProfileEnabled; }
            set
            {
                if (disposed) throw new ObjectDisposedException(GetType().FullName);
                if (!simulation) throw new InvalidOperationException("Reference examples are available in simulation only.");
                Action change = delegate {
                    if (processing || ReadBooleanProperty(target, "IsBusy") || ReadBooleanProperty(target, "ActionPending"))
                        throw new InvalidOperationException("Finish the current simulation action before changing its data source.");
                    if (referenceProfileEnabled == value) return;
                    // Load and verify the asset before changing any visible data.
                    if (value && referenceProfile == null) referenceProfile = ReferenceSimulationProfile.LoadDefault();
                    if (target != null)
                    {
                        if (moduleKind == ModuleKind.Calibration)
                        {
                            object cleared = InvokeRequired(target, "ClearSimulationDisplay");
                            if (!(cleared is bool) || !(bool)cleared)
                                throw new InvalidOperationException("Save or explicitly discard calibration changes before changing the simulation data source.");
                        }
                        else if (moduleKind == ModuleKind.Pressure) InvokeRequired(target, "ClearDisplayData");
                    }
                    referenceProfileEnabled = value;
                    SetSourceDescription(value ? DescriptionText("项目参考仿真 · 非实测；按当前参数适配。", "Project-reference simulation · Not measured; adapted to current settings.") :
                        DescriptionText("合成仿真 · 非实测。", "Synthetic simulation · Not measured."));
                };
                if (target == null) change(); else RunOnControlThread(target, change);
            }
        }
        public string SourceDescription { get { return sourceDescription; } }
        public bool IsBusy { get { return processing || ReadBooleanProperty(target, "IsBusy") || ReadBooleanProperty(target, "ActionPending"); } }

        private void SetSourceDescription(string value)
        {
            sourceDescription = value ?? String.Empty;
            EventHandler handler = DescriptionChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        private string DescriptionText(string chinese, string english)
        {
            PropertyInfo p = target == null ? null : target.GetType().GetProperty("LanguageIndex");
            int language = p == null ? 0 : Convert.ToInt32(p.GetValue(target, null), CultureInfo.InvariantCulture);
            return Xiongxin.OWTS.Localization.UiLanguage.Text(language, chinese, english);
        }
        private static bool ReadBooleanProperty(object instance, string name)
        {
            if (instance == null) return false;
            PropertyInfo p = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return p != null && p.PropertyType == typeof(bool) && (bool)p.GetValue(instance, null);
        }

        private enum ModuleKind
        {
            None,
            Calibration,
            Pressure
        }

        /// <param name="simulation">
        /// Must be true to enable a module action mailbox.  Passing false leaves the
        /// module untouched, which prevents this bridge from ever taking over real
        /// hardware operation accidentally.
        /// </param>
        public OfflineSimulationBridge(bool simulation)
        {
            this.simulation = simulation;
        }

        public bool IsRunning
        {
            get { return target != null && pollTimer != null && pollTimer.Enabled; }
        }

        public void Start(Control control, string moduleId)
        {
            if (disposed) throw new ObjectDisposedException(GetType().FullName);
            if (control == null) throw new ArgumentNullException("control");

            // The caller must have made an explicit simulation=true decision when the
            // bridge was constructed.  A real-device path is therefore a hard no-op.
            if (!simulation)
            {
                Stop();
                return;
            }

            ModuleKind requested = IdentifyModule(control, moduleId);
            if (requested == ModuleKind.None)
                throw new ArgumentException("The control is not a supported Task101 simulation module.", "moduleId");

            RunOnControlThread(control, delegate
            {
                StopCore();
                VerifyMailboxContract(control);

                target = control;
                moduleKind = requested;
                SetProperty(control, "ActionHandshakeEnabled", true);

                pollTimer = new Timer();
                pollTimer.Interval = 60;
                pollTimer.Tick += PollTimerOnTick;
                pollTimer.Start();
                SetSourceDescription(CurrentSourceDescription());
            });
        }

        public void Stop()
        {
            Control current = target;
            if (current != null && !current.IsDisposed && !current.Disposing)
            {
                RunOnControlThread(current, StopCore);
                return;
            }

            StopCore();
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            DescriptionChanged = null;
            disposed = true;
        }

        private void PollTimerOnTick(object sender, EventArgs e)
        {
            Control current = target;
            if (processing || current == null || current.IsDisposed || current.Disposing)
            {
                if (current == null || current.IsDisposed || current.Disposing) StopCore();
                return;
            }

            long sequence;
            string actionCode;
            string parametersJson;
            if (!TryReadPendingAction(current, out sequence, out actionCode, out parametersJson)) return;

            processing = true;
            bool succeeded = false;
            string error = String.Empty;
            try
            {
                IDictionary<string, object> parameters = ParseParameters(parametersJson);
                if (moduleKind == ModuleKind.Calibration)
                    ProcessCalibrationAction(current, actionCode, parameters);
                else if (moduleKind == ModuleKind.Pressure)
                    ProcessPressureAction(current, actionCode, parameters);
                succeeded = true;
            }
            catch (Exception failure)
            {
                Exception actual = Unwrap(failure);
                error = "离线仿真操作失败：" + actual.Message;
                if (error.Length > 1024) error = error.Substring(0, 1024);
            }
            finally
            {
                try
                {
                    InvokeRequired(current, "CompleteAction", sequence, succeeded, error);
                }
                catch
                {
                    // The control may have been closed while the deterministic data was
                    // being prepared.  There is no safe target left to acknowledge.
                }
                processing = false;
            }
        }

        private static ModuleKind IdentifyModule(Control control, string moduleId)
        {
            if (String.Equals(moduleId, CalibrationModuleId, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(control.GetType().Name, "CalibrationProgramControl", StringComparison.Ordinal))
                return ModuleKind.Calibration;
            if (String.Equals(moduleId, PressureModuleId, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(control.GetType().Name, "PressureTestControl", StringComparison.Ordinal))
                return ModuleKind.Pressure;
            return ModuleKind.None;
        }

        private static void VerifyMailboxContract(Control control)
        {
            PropertyInfo enabled = control.GetType().GetProperty("ActionHandshakeEnabled", BindingFlags.Instance | BindingFlags.Public);
            MethodInfo read = FindMethod(control, "ReadPendingAction", 3);
            MethodInfo complete = FindMethod(control, "CompleteAction", 3);
            if (enabled == null || !enabled.CanWrite || enabled.PropertyType != typeof(bool) || read == null || complete == null)
                throw new MissingMethodException(control.GetType().FullName, "Task101 action mailbox contract");
        }

        private void ProcessCalibrationAction(Control control, string actionCode, IDictionary<string, object> parameters)
        {
            string code = (actionCode ?? String.Empty).Trim().ToUpperInvariant();
            switch (code)
            {
                case "START":
                    if (referenceProfileEnabled) InstallReferenceCalibration(control, parameters);
                    else InstallCalibrationSimulation(control, parameters);
                    SetSourceDescription(CurrentSourceDescription());
                    TryInvoke(control, "AutoSaveQualifiedCalibration");
                    break;
                case "DELETE":
                    ClearCalibrationSimulation(control, parameters);
                    object deleted = InvokeRequired(control, "SaveCalibrationFile");
                    if (!(deleted is bool) || !(bool)deleted)
                        throw new InvalidOperationException("校准删除尚未保存到档案，请重试保存；原档案未确认为已删除。");
                    break;
                case "SAVE":
                    // Explicit simulation-only archive; the control verifies its
                    // provenance and reads the file back before reporting success.
                    object saved = InvokeRequired(control, "SaveCalibrationFile");
                    if (!(saved is bool) || !(bool)saved)
                        throw new InvalidOperationException("仿真校准未保存。请检查校准页面的保存提示；不能作为实机校准。");
                    break;
                case "EXIT":
                case "PHASE":
                case "CALIBRATION_SCOPE":
                case "LEVEL":
                case "CABLE_LENGTH":
                case "VELOCITY":
                case "SHORT_CABLE":
                case "BANDWIDTH":
                    // Parameter changes are already committed by the Task101 control
                    // before it publishes its immutable action snapshot.
                    break;
                default:
                    // Future UI-only Task101 actions are safe no-ops in simulation.
                    break;
            }
        }

        private static void InstallCalibrationSimulation(Control control, IDictionary<string, object> parameters)
        {
            int phaseIndex = GetInt(parameters, "phaseIndex", -1);
            string selectedLevel = GetString(parameters, "levelCode", "TDR");
            bool allLevels = GetBoolean(parameters, "allLevels", false);
            string[] levelCodes = allLevels ? GetStrings(parameters, "levelCodes") : new[] { selectedLevel };
            if (levelCodes.Length == 0) levelCodes = new[] { selectedLevel };

            double cableLength = Clamp(GetDouble(parameters, "cableLengthMeters", 414.0), 0.1, 3000.0);
            double velocity = Clamp(GetDouble(parameters, "velocityMPerUs", 170.0), 1.0, 400.0);
            SetCalibrationOrigin(control, parameters, false);
            double[] noise = CreateCalibrationNoise(8192, phaseIndex);
            InvokeRequired(control, "SetPhaseNoiseWaveform", phaseIndex, noise, 0.0, CalibrationSampleIntervalSeconds);

            double largestPeak = 0.0;
            double largestNoise = 0.0;
            for (int index = 0; index < noise.Length; index++)
                largestNoise = Math.Max(largestNoise, Math.Abs(noise[index]));

            foreach (string requestedLevel in levelCodes)
            {
                string level = String.IsNullOrWhiteSpace(requestedLevel) ? selectedLevel : requestedLevel.Trim();
                double[] waveform = CreateCalibrationWaveform(8192, phaseIndex, level, cableLength, velocity);
                for (int index = 0; index < waveform.Length; index++)
                    largestPeak = Math.Max(largestPeak, Math.Abs(waveform[index]));

                InvokeRequired(control, "SetPhaseWaveform", phaseIndex, level, waveform, 0.0, CalibrationSampleIntervalSeconds);
                TryInvoke(control, "AutoAnalyzePhaseWaveform", phaseIndex, level);
            }

            double signalToNoise = largestNoise > 0.0 ? largestPeak / largestNoise : 0.0;
            TryInvoke(control, "SetSignalMetrics", signalToNoise, largestPeak, largestNoise);
        }

        private static void ClearCalibrationSimulation(Control control, IDictionary<string, object> parameters)
        {
            int phaseIndex = GetInt(parameters, "phaseIndex", -1);
            string[] levels = GetStrings(parameters, "levelCodes");
            if (levels.Length == 0) levels = new[] { GetString(parameters, "levelCode", "TDR") };
            foreach (string level in levels)
                TryInvoke(control, "ClearPhaseWaveform", phaseIndex, level);
        }

        private static double[] CreateCalibrationNoise(int count, int phaseIndex)
        {
            double[] samples = new double[count];
            double phase = (phaseIndex + 2) * 0.37;
            for (int index = 0; index < samples.Length; index++)
            {
                samples[index] = 0.0040 * Math.Sin(index * 0.173 + phase) +
                                 0.0022 * Math.Sin(index * 0.031 + 1.7 + phase) +
                                 0.0011 * Math.Cos(index * 0.419 + 0.3);
            }
            return samples;
        }

        private static double[] CreateCalibrationWaveform(int count, int phaseIndex, string levelCode,
            double cableLengthMeters, double velocityMPerUs)
        {
            double[] waveform = CreateCalibrationNoise(count, phaseIndex);
            int levelHash = StableHash(levelCode ?? String.Empty);
            double levelScale = 0.82 + (Math.Abs(levelHash) % 19) / 100.0;
            int incidentIndex = 96;
            double roundTripSeconds = 2.0 * cableLengthMeters / (velocityMPerUs * 1000000.0);
            int delay = (int)Math.Round(roundTripSeconds / CalibrationSampleIntervalSeconds);
            delay = Math.Max(36, Math.Min(count - incidentIndex - 128, delay));
            int reflectionIndex = incidentIndex + delay;
            double reflectionPolarity = phaseIndex == 1 ? -1.0 : 1.0;

            AddGaussian(waveform, incidentIndex, 5.2, levelScale);
            AddDampedRing(waveform, incidentIndex, 0.34 * levelScale, 44.0, 0.56);
            AddGaussian(waveform, reflectionIndex, 7.0, reflectionPolarity * 0.28 * levelScale);
            AddDampedRing(waveform, reflectionIndex, reflectionPolarity * 0.12 * levelScale, 62.0, 0.43);

            // A smaller joint echo makes the simulated trace useful for zoom and
            // cursor interactions while remaining fully deterministic.
            int jointIndex = incidentIndex + Math.Max(18, delay * 2 / 5);
            AddGaussian(waveform, jointIndex, 6.0, -0.11 * levelScale);
            return waveform;
        }

        private static void AddGaussian(double[] samples, int centre, double width, double amplitude)
        {
            int radius = (int)Math.Ceiling(width * 6.0);
            int start = Math.Max(0, centre - radius);
            int end = Math.Min(samples.Length - 1, centre + radius);
            for (int index = start; index <= end; index++)
            {
                double normalized = (index - centre) / width;
                samples[index] += amplitude * Math.Exp(-0.5 * normalized * normalized);
            }
        }

        private static void AddDampedRing(double[] samples, int start, double amplitude, double decaySamples,
            double radiansPerSample)
        {
            for (int offset = 0; offset < 320 && start + offset < samples.Length; offset++)
                samples[start + offset] += amplitude * Math.Exp(-offset / decaySamples) * Math.Sin(offset * radiansPerSample);
        }

        private void ProcessPressureAction(Control control, string actionCode, IDictionary<string, object> parameters)
        {
            string code = (actionCode ?? String.Empty).Trim().ToUpperInvariant();
            switch (code)
            {
                case "PRESSURE":
                    if (referenceProfileEnabled) InstallReferencePressure(control, parameters);
                    else InstallPressureSimulation(control, parameters);
                    break;
                case "DELETE":
                    TryInvoke(control, "ClearDisplayData");
                    break;
                default:
                    // POLARITY, PLAN_STEP, RANGE, voltage, spectrum and region actions
                    // have already updated the control before publication.  They need
                    // acknowledgement but no synthetic acquisition.
                    break;
            }
        }

        private static void InstallPressureSimulation(Control control, IDictionary<string, object> parameters)
        {
            double targetPeakKv = Clamp(GetDouble(parameters, "targetPeakVoltageKv", 12.0), 0.0, 500.0);
            bool positivePolarity = GetBoolean(parameters, "positivePolarity", true);
            double lengthM = Clamp(GetDouble(parameters, "cableLengthMeters", 175.4), .1, 100000);
            string selectedPhase = GetString(parameters, "phaseCode", "A");
            int phaseCode = selectedPhase == "B" ? 2 : selectedPhase == "C" ? 3 : 1;
            var data = targetPeakKv == 0 ? DampedPressureSimulation.CreateZeroNoise(phaseCode) :
                DampedPressureSimulation.Create(targetPeakKv, lengthM, phaseCode, positivePolarity);
            TryInvoke(control, "SetPressureState", "RUNNING", 0.0);
            double[] positions = data.Pulses.ConvertAll(p => p.Position).ToArray();
            double[] charges = data.Pulses.ConvertAll(p => p.Charge).ToArray();
            PropertyInfo projectProperty = control.GetType().GetProperty("ProjectPath", BindingFlags.Instance | BindingFlags.Public);
            string projectPath = projectProperty == null ? String.Empty : Convert.ToString(projectProperty.GetValue(control, null));
            var referenceMetadata = new Dictionary<string, object>(StringComparer.Ordinal) {
                { "schema", "HVX.NoiseReference/1" }, { "projectPath", projectPath },
                { "phaseCode", selectedPhase }, { "channelId", "SIM_PD" }, { "deviceId", "SIM_VIRTUAL_GENERATOR_V1" },
                { "sampleRateHz", DampedPressureSimulation.SampleRateHz },
                { "resolutionBits", DampedPressureSimulation.VirtualResolutionBits },
                { "rangeCode", DampedPressureSimulation.VirtualRangeCode },
                { "fullScaleV", DampedPressureSimulation.VirtualFullScaleVolts },
                { "sampleUnit", "pC" }, { "simulation", true },
                { "chargeScalePcPerVolt", DampedPressureSimulation.VirtualPicocoulombsPerVolt },
                { "calibrationId", "SIM_VIRTUAL_V1" },
                { "sourceId", "SIM-" + Guid.NewGuid().ToString("N") },
                { "capturedUtc", DateTimeOffset.UtcNow }, { "targetVoltageKv", targetPeakKv },
                { "generatorZero", targetPeakKv == 0 }
            };
            string acquisitionId = GetString(parameters, "locationAcquisitionId", String.Empty);
            if (!String.IsNullOrWhiteSpace(acquisitionId)) referenceMetadata.Add("locationAcquisitionId", acquisitionId);
            // The combined API classifies reference acquisition before any PD
            // statistics callback; old modules cannot silently accept it as PD.
            InvokeRequired(control, "SetAcquisitionWaveforms", data.Voltage, data.Discharge,
                DampedPressureSimulation.SampleRateHz, referenceMetadata);
            TryInvoke(control, "ApplyRealtimeMetrics", data.MaximumCharge, .2, DampedPressureSimulation.FrequencyHz, DampedPressureSimulation.LossPercent);
            TryInvoke(control, "SetLocationData", positions, charges, lengthM, 92.0);
            TryInvoke(control, "SetSpectrumDataMatrix", data.Matrix(lengthM, phaseCode));

            TryInvoke(control, "SetPressureState", "COMPLETE", targetPeakKv);
            TryInvoke(control, "ReportAcquisitionCompleted", false);
            // Displaying generated waveforms/matrices is neither a disk commit
            // nor a completed project analysis. Never acknowledge either as saved.
            TryInvoke(control, "CompleteSave", false,
                "SIM: generated data is displayed only; acquisition files were not saved.");
            TryInvoke(control, "CompleteAnalysis", false,
                "SIM: generated statistics are a preview; project analysis was not performed.");
        }

        private void InstallReferenceCalibration(Control control, IDictionary<string, object> parameters)
        {
            if (!simulation || !ReadBooleanProperty(control, "SimulationMode"))
                throw new InvalidOperationException("Reference calibration is simulation only.");
            ReferenceSimulationProfile profile = referenceProfile ?? (referenceProfile = ReferenceSimulationProfile.LoadDefault());
            int phase = GetInt(parameters, "phaseIndex", -1);
            if (phase < -1 || phase > 2) throw new ArgumentOutOfRangeException("phaseIndex");
            double length = RequiredPositive(parameters, "cableLengthMeters");
            double velocity = RequiredPositive(parameters, "velocityMPerUs");
            string selected = GetString(parameters, "levelCode", "TDR");
            string[] levels = GetBoolean(parameters, "allLevels", false) ? GetStrings(parameters, "levelCodes") : new[] { selected };
            if (levels.Length == 0) levels = new[] { selected };
            // Prepare every output first: an invalid level must not leave a
            // partially replaced multi-level calibration in the UI.
            var adapted = new List<Tuple<string, double[], double>>();
            foreach (string level in levels)
            {
                double requestedCharge = ParseChargePc(level);
                ReferenceSimulationProfile.Calibration donor = null;
                double distance = Double.PositiveInfinity;
                foreach (var candidate in profile.Calibrations)
                {
                    double score = requestedCharge == 0 ? -candidate.ChargePc : Math.Abs(Math.Log(candidate.ChargePc / requestedCharge));
                    if (score < distance) { donor = candidate; distance = score; }
                }
                if (donor == null) throw new InvalidOperationException("The reference profile has no calibration example.");
                double timeScale = (2 * length / (velocity * 1e6)) / (donor.SourceReflectionSeconds - donor.SourceIncidentSeconds);
                double dx = timeScale / donor.SampleRateHz;
                if (!FinitePositive(dx)) throw new InvalidOperationException("Invalid reference calibration time scale.");
                double amplitudeScale = requestedCharge == 0 ? 1 : requestedCharge / donor.ChargePc;
                var waveform = new double[donor.VoltageSamples.Length];
                for (int i = 0; i < waveform.Length; i++) waveform[i] = donor.VoltageSamples[i] * amplitudeScale;
                adapted.Add(Tuple.Create(level, waveform, dx));
            }
            // No separate background recording exists in this minimal asset.
            // Clear the old synthetic noise rather than inventing source noise.
            SetCalibrationOrigin(control, parameters, true);
            InvokeRequired(control, "SetPhaseNoiseWaveform", phase, new double[0], 0.0, CalibrationSampleIntervalSeconds);
            foreach (var item in adapted)
            {
                InvokeRequired(control, "SetPhaseWaveform", phase, item.Item1, item.Item2, 0.0, item.Item3);
                TryInvoke(control, "AutoAnalyzePhaseWaveform", phase, item.Item1);
            }
            SetSourceDescription(DescriptionText(
                "项目参考仿真 · 非实测。采用 ALL 相参考校准形态；幅值按所选电荷档适配，时间轴按当前长度/波速缩放。历史标记仅作适配依据，定位仍由当前算法重算；不代表当前相别实测。",
                "Project-reference simulation · Not measured. ALL-phase calibration shapes are charge-scaled and time-scaled to current length/velocity. Historical markers guide adaptation only; the current algorithm recomputes timing. Not a measurement of the selected phase."));
        }

        private void InstallReferencePressure(Control control, IDictionary<string, object> parameters)
        {
            if (!simulation || !ReadBooleanProperty(control, "SimulationMode"))
                throw new InvalidOperationException("Reference pressure data is simulation only.");
            ReferenceSimulationProfile profile = referenceProfile ?? (referenceProfile = ReferenceSimulationProfile.LoadDefault());
            double targetVoltage = GetDouble(parameters, "targetPeakVoltageKv", Double.NaN);
            if (Double.IsNaN(targetVoltage) || Double.IsInfinity(targetVoltage) || targetVoltage < 0 || targetVoltage > 500)
                throw new ArgumentOutOfRangeException("targetPeakVoltageKv");
            string phase = GetString(parameters, "phaseCode", String.Empty).ToUpperInvariant();
            ReferenceSimulationProfile.PressureRecord donor = null;
            double distance = Double.PositiveInfinity;
            foreach (var candidate in profile.PressureRecords)
            {
                if (candidate.Phase != phase || (candidate.NominalVoltageKv == 0) != (targetVoltage == 0)) continue;
                double score = Math.Abs(candidate.NominalVoltageKv - targetVoltage);
                if (score < distance) { donor = candidate; distance = score; }
            }
            if (donor == null) throw new InvalidOperationException("No same-phase reference example is available.");
            int count = Math.Min(donor.VoltageKv.Length, donor.DischargePc.Length);
            var voltage = new double[count]; var discharge = new double[count];
            double signedPeak = 0;
            for (int i = 0; i < count; i++) if (Math.Abs(donor.VoltageKv[i]) > Math.Abs(signedPeak)) signedPeak = donor.VoltageKv[i];
            double voltageScale = targetVoltage == 0 ? 1 : targetVoltage / Math.Abs(signedPeak);
            if (targetVoltage != 0 && ((signedPeak >= 0) != GetBoolean(parameters, "positivePolarity", true))) voltageScale = -voltageScale;
            for (int i = 0; i < count; i++) { voltage[i] = donor.VoltageKv[i] * voltageScale; discharge[i] = donor.DischargePc[i]; }
            PropertyInfo path = control.GetType().GetProperty("ProjectPath");
            var metadata = new Dictionary<string, object> {
                { "schema", "HVX.NoiseReference/1" }, { "projectPath", path == null ? String.Empty : Convert.ToString(path.GetValue(control, null)) },
                { "targetVoltageKv", targetVoltage }, { "phaseCode", phase }, { "simulation", true },
                { "sampleRateHz", donor.SampleRateHz }, { "sampleUnit", "pC" },
                { "sourceId", "REFERENCE-SIM-" + profile.Id + "-" + donor.Id }, { "referencePreview", true }
            };
            string acquisitionId = GetString(parameters, "locationAcquisitionId", String.Empty);
            if (!String.IsNullOrWhiteSpace(acquisitionId)) metadata.Add("locationAcquisitionId", acquisitionId);
            // Source ADC/range/calibration evidence is unavailable. In particular,
            // a zero example stays pending, never inherits virtual-ADC credentials,
            // and keeps the recorded residual voltage instead of fabricating zero.
            InvokeRequired(control, "SetAcquisitionWaveforms", voltage, discharge, donor.SampleRateHz, metadata);
            InvokeRequired(control, "SetReferencePreviewStatus", DescriptionText(
                "未提供经过核验的定位事件，仅显示参考波形。\r\n不生成定位统计或当前工况诊断结论。",
                "No verified location events are provided; reference waveforms only.\r\nNo location statistics or diagnosis of current conditions is generated."));
            TryInvoke(control, "SetSpectrumDataMatrix", new double[0, 36]);
            TryInvoke(control, "SetPressureState", "COMPLETE", Double.NaN);
            TryInvoke(control, "ReportAcquisitionCompleted", false);
            TryInvoke(control, "CompleteSave", false, DescriptionText("参考仿真仅供显示，未写入采集文件。", "Reference preview only; no acquisition file was saved."));
            TryInvoke(control, "CompleteAnalysis", false, DescriptionText("未运行项目分析；参考波形不构成当前工况的放电判断。", "Project analysis was not performed; reference waveforms do not diagnose current conditions."));
            string detail = String.Format(CultureInfo.InvariantCulture, DescriptionText(
                "项目参考仿真 · 非实测。{0}相原记录标注 {1:0.##} kV → 仿真目标峰值 {2:0.##} kV；{3}电荷保持原始 pC。\r\n{4}时间轴 {5:0.###} kS/s；对齐显示 {6} 点。无核验定位事件。",
                "Project-reference simulation · Not measured. Phase {0}, original record label {1:0.##} kV → simulated target peak {2:0.##} kV; {3}charge retains original pC.\r\n{4}time axis {5:0.###} kS/s; {6} aligned samples. No verified location events."),
                phase, donor.NominalVoltageKv, targetVoltage,
                targetVoltage == 0 ? DescriptionText("保留原记录电压残余，零位待核验；", "recorded voltage residual retained, zero unverified; ") : DescriptionText("仅电压幅值/极性适配，", "voltage amplitude/polarity adapted only; "),
                donor.RateEstimated ? DescriptionText("估算", "Estimated ") : String.Empty, donor.SampleRateHz / 1000, count);
            detail += "\r\n" + DescriptionText("来源记录：", "Source record: ") + Path.GetFileName(donor.SourceFile);
            if(FinitePositive(donor.RecordedFrequencyHz))detail+=" · "+donor.RecordedFrequencyHz.ToString("0.###",CultureInfo.InvariantCulture)+" Hz";
            if(donor.RecordedCapacitanceUf.HasValue)detail+=" · "+donor.RecordedCapacitanceUf.Value.ToString("0.###",CultureInfo.InvariantCulture)+" µF";
            if(donor.RecordedLossPercent.HasValue)detail+=" · "+donor.RecordedLossPercent.Value.ToString("0.###",CultureInfo.InvariantCulture)+" %";
            detail += DescriptionText("（来源原始参数，非当前工况测量值）", " (original source parameters, not measurements of current conditions)");
            SetSourceDescription(detail);
        }

        private static bool FinitePositive(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value) && value > 0; }
        private static double RequiredPositive(IDictionary<string, object> values, string name)
        {
            double value = GetDouble(values, name, Double.NaN);
            if (!FinitePositive(value)) throw new ArgumentOutOfRangeException(name);
            return value;
        }
        private static double ParseChargePc(string code)
        {
            string text = (code ?? String.Empty).Trim();
            if (String.Equals(text, "TDR", StringComparison.OrdinalIgnoreCase)) return 0;
            double multiplier = text.EndsWith("nC", StringComparison.OrdinalIgnoreCase) ? 1000 : text.EndsWith("pC", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            double value;
            if (multiplier == 0 || !Double.TryParse(text.Substring(0, text.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !FinitePositive(value))
                throw new ArgumentException("Unsupported calibration charge level.", "code");
            return value * multiplier;
        }

        private string CurrentSourceDescription()
        {
            string current = referenceProfileEnabled ? DescriptionText("新采集来源：项目参考仿真（非实测）。", "New acquisition source: project-reference simulation (not measured).") :
                DescriptionText("新采集来源：合成仿真（非实测）。", "New acquisition source: synthetic simulation (not measured).");
            if (moduleKind == ModuleKind.Calibration && ReadBooleanProperty(target, "HasCalibrationData"))
            {
                PropertyInfo property = target.GetType().GetProperty("SimulationDataOrigin");
                string origin = property == null ? String.Empty : Convert.ToString(property.GetValue(target, null));
                return current + "\r\n" + DescriptionText("已载校准来源：", "Loaded calibration origin: ") +
                    (String.IsNullOrWhiteSpace(origin) ? DescriptionText("旧仿真档案未记录来源。", "Legacy simulation archive did not record its source.") : origin);
            }
            return current;
        }
        private static void SetCalibrationOrigin(Control control, IDictionary<string,object> parameters, bool reference)
        {
            PropertyInfo property = control.GetType().GetProperty("SimulationDataOrigin");
            string existing = property == null ? String.Empty : Convert.ToString(property.GetValue(control, null));
            const string recent = "\nLatest simulated assignment: ";
            int previous = existing.IndexOf(recent, StringComparison.Ordinal);
            if(previous>=0)existing=existing.Substring(0,previous);
            string marker = reference ? "REFERENCE[rdfz-jmf-v1]" : "SYNTHETIC[V1]";
            string detail = reference ? "REFERENCE[rdfz-jmf-v1]: ALL-phase historical donor; charge amplitude and time axis adapted to current settings, historical timing markers are not current first-arrival evidence." :
                "SYNTHETIC[V1]: generated pulse/noise example; no measured source.";
            string origin;
            if(String.IsNullOrWhiteSpace(existing))origin="SIM: "+detail;
            else if(existing.IndexOf(marker,StringComparison.Ordinal)>=0)origin=existing;
            else origin="SIM: Mixed simulation origins / 混合仿真来源. "+existing+"\n"+detail;
            int phase=GetInt(parameters,"phaseIndex",-1);
            string[] levels=GetBoolean(parameters,"allLevels",false)?GetStrings(parameters,"levelCodes"):new[]{GetString(parameters,"levelCode","TDR")};
            origin+=recent+(phase==-1?"ALL":phase==0?"A":phase==1?"B":"C")+" / "+String.Join(",",levels);
            if(origin.Length>4096)throw new InvalidOperationException("The retained simulation-source history is full. Explicitly clear the simulation display before using another source.");
            object saved=InvokeRequired(control,"SetSimulationDataOrigin",origin);
            if(!(saved is bool)||!(bool)saved)throw new InvalidOperationException("Simulation data origin could not be recorded; no new waveform was installed.");
        }

        private static double PeriodicGaussian(double degrees, double centre, double width)
        {
            double distance = Math.Abs(degrees - centre);
            distance = Math.Min(distance, 360.0 - distance);
            double normalized = distance / width;
            return Math.Exp(-0.5 * normalized * normalized);
        }

        private static void CreateLocationData(double cableLengthMeters, out double[] positions, out double[] charges)
        {
            const int count = 360;
            positions = new double[count];
            charges = new double[count];
            for (int index = 0; index < count; index++)
            {
                bool firstCluster = index % 5 < 3;
                double centre = cableLengthMeters * (firstCluster ? 0.205 : 0.642);
                double jitter = Math.Sin(index * 1.913) * (firstCluster ? 7.2 : 10.5) +
                                Math.Cos(index * 0.377) * 2.1;
                positions[index] = Clamp(centre + jitter, 0.0, cableLengthMeters);
                double magnitude = (firstCluster ? 118.0 : 76.0) + 18.0 * Math.Sin(index * 0.219);
                charges[index] = (index % 7 < 4 ? 1.0 : -1.0) * magnitude;
            }
        }

        private static double[,] CreateSpectrumMatrix(double[] positions, double[] charges, double[] voltage,
            double sampleRateHz, double frequencyHz, double cableLengthMeters)
        {
            int count = Math.Min(positions.Length, charges.Length);
            double[,] matrix = new double[count, 36];
            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < 36; column++) matrix[row, column] = Double.NaN;
                int waveformIndex = row * 31 % voltage.Length;
                matrix[row, 0] = row % 3 + 1;                    // phase code
                matrix[row, 1] = waveformIndex / sampleRateHz;   // event time
                matrix[row, 2] = Math.Abs(voltage[waveformIndex]);
                matrix[row, 3] = row * 137 % 360;                 // phase angle
                matrix[row, 5] = cableLengthMeters;
                matrix[row, 13] = positions[row];
                matrix[row, 15] = charges[row];
                matrix[row, 16] = charges[row] * 0.96;
                matrix[row, 23] = frequencyHz + row % 17;
                matrix[row, 24] = 1.0e6 + (row % 24) * 0.1e6;
                matrix[row, 33] = 0.92;
                matrix[row, 35] = 1.0;
            }
            return matrix;
        }

        private void StopCore()
        {
            if (pollTimer != null)
            {
                pollTimer.Stop();
                pollTimer.Tick -= PollTimerOnTick;
                pollTimer.Dispose();
                pollTimer = null;
            }

            Control current = target;
            target = null;
            moduleKind = ModuleKind.None;
            processing = false;
            if (current == null || current.IsDisposed || current.Disposing) return;

            try
            {
                long sequence;
                string code;
                string parameters;
                if (TryReadPendingAction(current, out sequence, out code, out parameters))
                    TryInvoke(current, "CompleteAction", sequence, false, "仿真模式已结束。");
                SetProperty(current, "ActionHandshakeEnabled", false);
            }
            catch
            {
                // Shutdown must not keep a form alive merely to update a detached module.
            }
        }

        private static bool TryReadPendingAction(Control control, out long sequence, out string code,
            out string parametersJson)
        {
            object[] arguments = { 0L, String.Empty, String.Empty };
            object result = InvokeRequired(control, "ReadPendingAction", arguments);
            sequence = Convert.ToInt64(arguments[0], CultureInfo.InvariantCulture);
            code = Convert.ToString(arguments[1], CultureInfo.InvariantCulture) ?? String.Empty;
            parametersJson = Convert.ToString(arguments[2], CultureInfo.InvariantCulture) ?? String.Empty;
            return result is bool && (bool)result;
        }

        private IDictionary<string, object> ParseParameters(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            object parsed = json.DeserializeObject(text);
            IDictionary<string, object> dictionary = parsed as IDictionary<string, object>;
            return dictionary ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private static object InvokeRequired(object instance, string methodName, params object[] arguments)
        {
            MethodInfo method = FindMethod(instance, methodName, arguments == null ? 0 : arguments.Length);
            if (method == null) throw new MissingMethodException(instance.GetType().FullName, methodName);
            return method.Invoke(instance, arguments);
        }

        private static bool TryInvoke(object instance, string methodName, params object[] arguments)
        {
            MethodInfo method = FindMethod(instance, methodName, arguments == null ? 0 : arguments.Length);
            if (method == null) return false;
            method.Invoke(instance, arguments);
            return true;
        }

        private static MethodInfo FindMethod(object instance, string methodName, int argumentCount)
        {
            foreach (MethodInfo method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (String.Equals(method.Name, methodName, StringComparison.Ordinal) &&
                    method.GetParameters().Length == argumentCount)
                    return method;
            }
            return null;
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                throw new MissingMemberException(instance.GetType().FullName, propertyName);
            property.SetValue(instance, value, null);
        }

        private static void RunOnControlThread(Control control, Action action)
        {
            if (control == null || control.IsDisposed || control.Disposing) return;
            if (control.InvokeRequired)
            {
                if (!control.IsHandleCreated)
                    throw new InvalidOperationException("The simulation module has no UI handle.");
                control.Invoke(action);
            }
            else
            {
                action();
            }
        }

        private static Exception Unwrap(Exception error)
        {
            TargetInvocationException invocation = error as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ? invocation.InnerException : error;
        }

        private static object Value(IDictionary<string, object> values, string name)
        {
            if (values == null) return null;
            object value;
            if (values.TryGetValue(name, out value)) return value;
            foreach (KeyValuePair<string, object> pair in values)
                if (String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return null;
        }

        private static string GetString(IDictionary<string, object> values, string name, string fallback)
        {
            object value = Value(values, name);
            string text = value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
            return String.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        }

        private static bool GetBoolean(IDictionary<string, object> values, string name, bool fallback)
        {
            object value = Value(values, name);
            if (value is bool) return (bool)value;
            bool parsed;
            return value != null && Boolean.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed)
                ? parsed : fallback;
        }

        private static int GetInt(IDictionary<string, object> values, string name, int fallback)
        {
            object value = Value(values, name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return fallback; }
        }

        private static double GetDouble(IDictionary<string, object> values, string name, double fallback)
        {
            object value = Value(values, name);
            if (value == null) return fallback;
            try
            {
                double parsed = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return Double.IsNaN(parsed) || Double.IsInfinity(parsed) ? fallback : parsed;
            }
            catch (Exception) { return fallback; }
        }

        private static string[] GetStrings(IDictionary<string, object> values, string name)
        {
            object value = Value(values, name);
            if (value == null) return new string[0];
            string direct = value as string;
            if (direct != null) return new[] { direct };

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) return new string[0];
            List<string> result = new List<string>();
            foreach (object item in enumerable)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!String.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }
            return result.ToArray();
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in text ?? String.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
