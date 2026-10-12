using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class ModuleDescriptor
    {
        public string Id { get; private set; }
        public string DisplayNameKey { get; private set; }
        public string DirectoryName { get; private set; }
        public string AssemblyFileName { get; private set; }
        public string ControlTypeName { get; private set; }

        public ModuleDescriptor(string id, string displayNameKey, string directoryName, string assemblyFileName, string controlTypeName)
        {
            Id = id;
            DisplayNameKey = displayNameKey;
            DirectoryName = directoryName;
            AssemblyFileName = assemblyFileName;
            ControlTypeName = controlTypeName;
        }
    }

    /// <summary>
    /// Hosts the standalone task controls without taking a compile-time dependency on
    /// any task assembly.  Dependencies are resolved only beside their requesting
    /// module, which prevents a missing/old task dependency from being copied into the
    /// desktop executable directory.
    /// </summary>
    internal sealed partial class ModuleHost : IDisposable
    {
        public const string ProjectCreation = "ProjectCreation";
        public const string ProjectManagement = "ProjectManagement";
        public const string DataBrowser = "DataBrowser";
        public const string DataAnalysis = "DataAnalysis";
        public const string ReportGenerator = "ReportGenerator";
        public const string PressureTest = "PressureTest";
        public const string CalibrationTest = "CalibrationTest";
        public const string WithstandTest = "WithstandTest";

        private static readonly ModuleDescriptor[] KnownDescriptors =
        {
            new ModuleDescriptor(ProjectCreation, "ProjectCreation", ProjectCreation,
                "Xiongxin.OWTS.ProjectCreation.dll", "HVX.Controls.ProjectCreationControl"),
            new ModuleDescriptor(ProjectManagement, "ProjectManagement", ProjectManagement,
                "Xiongxin.OWTS.ProjectManagement.dll", "HVX.Controls.ProjectManagementControl"),
            new ModuleDescriptor(DataBrowser, "DataBrowser", DataBrowser,
                "Xiongxin.OWTS.DataBrowser.dll", "HVX.Controls.ProjectDataBrowserControl"),
            new ModuleDescriptor(DataAnalysis, "DataAnalysis", DataAnalysis,
                "HVX.PdReview.V1.0.dll", "HVX.PdReview.PdReviewControl"),
            new ModuleDescriptor(ReportGenerator, "ReportGenerator", ReportGenerator,
                "Xiongxin.OWTS.ReportGenerator.dll", "HVX.ReportGenerator.ReportGeneratorControl"),
            new ModuleDescriptor(PressureTest, "PressureTest", PressureTest,
                "Xiongxin.OWTS.PressureTest.dll", "HVX.Controls.PressureTestControl"),
            new ModuleDescriptor(CalibrationTest, "CalibrationTest", CalibrationTest,
                "Xiongxin.OWTS.CalibrationTest.dll", "HVX.Controls.CalibrationProgramControl"),
            new ModuleDescriptor(WithstandTest, "WithstandTest", WithstandTest,
                "Xiongxin.OWTS.Desktop.exe", "Xiongxin.OWTS.Desktop.WithstandTestControl")
        };

        [ThreadStatic]
        private static string activeModuleDirectory;

        private readonly object settings;
        private readonly ApplicationState state;
        private readonly Diagnostics diagnostics;
        private readonly string modulesRoot;
        private readonly string modelRoot;
        private readonly Dictionary<string, Control> controls = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> contextSignatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> appliedProjectPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Assembly, string> assemblyDirectories = new Dictionary<Assembly, string>();
        private readonly object assemblyGate = new object();
        private System.Windows.Forms.Timer projectPollTimer;
        private Control projectManagerControl;
        private Control projectCreationControl;
        private long lastLoadRequestSequence;
        private long lastProjectSavedSequence;
        private long warnedLoadRequestSequence;
        private long warnedProjectSavedSequence;
        private bool projectPollInProgress;
        private Process calibrationAcquisitionProcess;
        private bool disposed;

        public event Action<string> ProjectLoadRequested;
        public event Action ProjectExitRequested;

        public ModuleHost(object appSettings, ApplicationState applicationState)
            : this(appSettings, applicationState, null, null)
        {
        }

        public ModuleHost(object appSettings, ApplicationState applicationState, Diagnostics integrationDiagnostics, string moduleDirectory)
        {
            settings = appSettings;
            state = applicationState ?? new ApplicationState();
            diagnostics = integrationDiagnostics ?? new Diagnostics();
            var scoreDiagnostic=LateBound.ReadString(settings,"SessionLocationScoreDiagnostic");
            // The diagnostic is internal session state, not a hardware attestation.
            var typedSettings=settings as AppSettings;
            if(typedSettings!=null)scoreDiagnostic=typedSettings.SessionLocationScoreDiagnostic;
            if(!String.IsNullOrWhiteSpace(scoreDiagnostic))diagnostics.Warning("LocationScorePolicy",scoreDiagnostic);
            modulesRoot = Path.GetFullPath(String.IsNullOrWhiteSpace(moduleDirectory)
                ? EmbeddedModulePayload.ResolveModulesRoot()
                : moduleDirectory);
            // Embedded modules live in a content-addressed cache, while models
            // are staged beside the executable or installed in ProgramData.
            // An explicit module root belongs to the isolated test/deployment root.
            modelRoot = String.IsNullOrWhiteSpace(moduleDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(modulesRoot);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveModuleAssembly;
            state.Changed += StateChanged;
        }

        public static ModuleDescriptor[] Descriptors
        {
            get { return (ModuleDescriptor[])KnownDescriptors.Clone(); }
        }

        public string ModulesRoot
        {
            get { return modulesRoot; }
        }

        public bool TryCreateControl(string moduleId, out Control control, out string message)
        {
            control = null;
            message = String.Empty;
            if (disposed)
            {
                message = "模块宿主已关闭。";
                return false;
            }

            ModuleDescriptor descriptor = FindDescriptor(moduleId);
            if (descriptor == null)
            {
                message = "未知模块：" + (moduleId ?? String.Empty);
                return false;
            }

            Control cached;
            if (controls.TryGetValue(descriptor.Id, out cached) && cached != null && !cached.IsDisposed)
            {
                EnableEmbeddedMode(descriptor, cached);
                ApplyContext(descriptor, cached, false);
                RefreshPendingCableStructure(cached);
                if (String.Equals(descriptor.Id, DataAnalysis, StringComparison.OrdinalIgnoreCase))
                    TryInvoke(cached, "ReloadProjectCalibration", new object[0]);
                control = cached;
                return true;
            }

            if (String.Equals(descriptor.Id, WithstandTest, StringComparison.OrdinalIgnoreCase))
            {
                control = new WithstandTestControl { Dock = DockStyle.Fill };
                controls[descriptor.Id] = control;
                appliedProjectPaths.Remove(descriptor.Id);
                ApplyContext(descriptor, control, true);
                ConnectModuleAudit(descriptor.Id,control);
                ConnectCableStructure(descriptor.Id,control);
                return true;
            }

            string directory = Path.GetFullPath(Path.Combine(modulesRoot, descriptor.DirectoryName));
            string assemblyPath = Path.GetFullPath(Path.Combine(directory, descriptor.AssemblyFileName));
            if (!IsDirectChild(assemblyPath, directory) || !File.Exists(assemblyPath))
            {
                message = "模块文件不存在：" + assemblyPath;
                diagnostics.Warning("ModuleHost", message);
                return false;
            }

            string previousDirectory = activeModuleDirectory;
            activeModuleDirectory = directory;
            try
            {
                Assembly assembly = LoadModuleAssembly(assemblyPath, directory);
                Type type = assembly.GetType(descriptor.ControlTypeName, false, false);
                if (type == null) type = FindUserControlType(assembly);
                if (type == null || !typeof(UserControl).IsAssignableFrom(type) || type.IsAbstract)
                    throw new TypeLoadException("未找到公共 WinForms UserControl：" + descriptor.ControlTypeName);
                ConstructorInfo constructor = type.GetConstructor(Type.EmptyTypes);
                if (constructor == null || !constructor.IsPublic)
                    throw new MissingMethodException(type.FullName, ".ctor()");

                object instance = constructor.Invoke(null);
                control = instance as Control;
                if (control == null) throw new InvalidCastException(type.FullName + " 不是 WinForms Control。");
                control.Dock = DockStyle.Fill;
                EnableEmbeddedMode(descriptor, control);
                controls[descriptor.Id] = control;
                appliedProjectPaths.Remove(descriptor.Id);
                ApplyContext(descriptor, control, true);

                ConnectModuleAudit(descriptor.Id,control);
                ConnectCableStructure(descriptor.Id,control);
                if (String.Equals(descriptor.Id, DataAnalysis, StringComparison.OrdinalIgnoreCase))
                    ConnectAnalysisSettings(control);
                if (String.Equals(descriptor.Id, PressureTest, StringComparison.OrdinalIgnoreCase))
                {
                    ConnectPressurePreferences(control);
                    ConnectPressureInputActions(control);
                }

                if (String.Equals(descriptor.Id, ProjectManagement, StringComparison.OrdinalIgnoreCase))
                {
                    projectManagerControl = control;
                    lastLoadRequestSequence = ReadInt64(control, "LoadRequestSequence");
                    StartProjectManagerPolling();
                }
                else if (String.Equals(descriptor.Id, ProjectCreation, StringComparison.OrdinalIgnoreCase))
                {
                    projectCreationControl = control;
                    EventInfo exitEvent=control.GetType().GetEvent("ExitRequested");
                    if(exitEvent!=null&&exitEvent.EventHandlerType==typeof(EventHandler))
                        exitEvent.AddEventHandler(control,new EventHandler((sender,args)=>{var handler=ProjectExitRequested;if(handler!=null)handler();}));
                    ConnectEnvironmentProvider(control);
                    lastProjectSavedSequence = ReadInt64(control, "ProjectSavedSequence");
                    StartProjectManagerPolling();
                }
                diagnostics.Info("ModuleHost", "Loaded " + descriptor.Id + " from " + assemblyPath);
                return true;
            }
            catch (Exception error)
            {
                if (control != null)
                {
                    try { control.Dispose(); } catch { }
                    control = null;
                }
                message = "模块加载失败：" + RootMessage(error);
                diagnostics.Error("ModuleHost", descriptor.Id + " failed to load.", error);
                return false;
            }
            finally
            {
                activeModuleDirectory = previousDirectory;
            }
        }

        private void EnableEmbeddedMode(ModuleDescriptor descriptor, Control control)
        {
            if (descriptor == null || control == null) return;
            PropertyInfo property = control.GetType().GetProperty("EmbeddedMode", BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                TrySet(control, "EmbeddedMode", true);
        }

        public void RefreshContext()
        {
            contextSignatures.Clear();
            foreach (KeyValuePair<string, Control> pair in controls)
            {
                ModuleDescriptor descriptor = FindDescriptor(pair.Key);
                ApplyOnControlThread(descriptor, pair.Value, true);
            }
        }

        public bool LaunchTask38(string page, out string error)
        {
            // Retain the historical API shape for compatibility, but never
            // launch the independent legacy HV controller from this host.
            // Current calibration and pressure use their own exclusive,
            // reviewed acquisition/session paths instead of a second chain.
            error = "此独立硬件控制入口已停用，请使用主界面的校准或加压测试。";
            diagnostics.Warning("ModuleHost", error);
            return false;
        }

        public bool LaunchTask38(string page)
        {
            string ignored;
            return LaunchTask38(page, out ignored);
        }

        public bool LaunchCalibrationAcquisition(string projectPath, out string error)
        {
            error = String.Empty;
            if (disposed || String.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
            {
                error = "请先加载有效项目，再开始校准。";
                return false;
            }
            string directory = Path.GetFullPath(Path.Combine(modulesRoot, "CalibrationAcquisition"));
            string executable = Path.GetFullPath(Path.Combine(directory, "XIONGXIN.Calibration.Verification.exe"));
            if (!IsDirectChild(executable, directory) || !File.Exists(executable))
            {
                error = "校准采集程序不存在，请修复安装。";
                diagnostics.Warning("ModuleHost", error);
                return false;
            }
            try
            {
                if (calibrationAcquisitionProcess != null)
                {
                    if (!calibrationAcquisitionProcess.HasExited)
                    {
                        error = "校准采集窗口已打开，请切换到该窗口；为避免同时占用 Pico 和控制箱，不会重复启动。";
                        return false;
                    }
                    calibrationAcquisitionProcess.Dispose();
                    calibrationAcquisitionProcess = null;
                }
                string fullProject = Path.GetFullPath(projectPath);
                // A Windows path cannot contain a quote; quote the argument for spaces.
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--project \"" + fullProject + "\" --integrated",
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = false
                };
                calibrationAcquisitionProcess = Process.Start(start);
                if (calibrationAcquisitionProcess == null) throw new IOException("进程未启动。");
                diagnostics.Info("ModuleHost", "Started project-scoped calibration acquisition.");
                return true;
            }
            catch (Exception ex)
            {
                error = "无法启动校准采集：" + ex.Message;
                diagnostics.Error("ModuleHost", error, ex);
                return false;
            }
        }

        public void StartProjectManagerPolling()
        {
            if (disposed) return;
            Control owner = ActiveProjectPollingControl();
            if (owner == null) return;
            if (owner.InvokeRequired)
            {
                try { owner.BeginInvoke(new Action(StartProjectManagerPolling)); } catch { }
                return;
            }
            if (projectPollTimer == null)
            {
                projectPollTimer = new System.Windows.Forms.Timer { Interval = 250 };
                projectPollTimer.Tick += PollProjectModules;
            }
            projectPollTimer.Start();
        }

        public void StopProjectManagerPolling()
        {
            if (disposed)
            {
                if (projectPollTimer != null) projectPollTimer.Stop();
                return;
            }

            Control owner = ActiveProjectPollingControl();
            if (owner != null && owner.InvokeRequired)
            {
                try { owner.Invoke(new Action(StopProjectManagerPolling)); } catch { }
                return;
            }

            // A save/load request can be raised immediately before the user presses
            // Back. Poll once synchronously so that event becomes the current project
            // before its control is removed and disposed.
            if (!projectPollInProgress) PollProjectModules(this, EventArgs.Empty);
            if (projectPollTimer != null) projectPollTimer.Stop();
        }

        private Control ActiveProjectPollingControl()
        {
            if (projectManagerControl != null && !projectManagerControl.IsDisposed) return projectManagerControl;
            if (projectCreationControl != null && !projectCreationControl.IsDisposed) return projectCreationControl;
            return null;
        }

        private void PollProjectModules(object sender, EventArgs e)
        {
            if (disposed || projectPollInProgress) return;
            projectPollInProgress = true;
            try
            {
                PollProjectManagerRequest();
                PollProjectCreationSave();
            }
            finally
            {
                projectPollInProgress = false;
            }
        }

        private void PollProjectManagerRequest()
        {
            Control manager = projectManagerControl;
            if (manager == null || manager.IsDisposed) return;
            try
            {
                long sequence = ReadInt64(manager, "LoadRequestSequence");
                if (sequence <= 0 || sequence == lastLoadRequestSequence) return;
                string path = LateBound.ReadString(manager, "LastLoadedProjectPath").Trim();
                if (path.Length == 0) return;

                string status;
                if (!state.TryLoadProject(path, out status))
                {
                    if (warnedLoadRequestSequence != sequence)
                    {
                        diagnostics.Warning("ModuleHost", "Project manager returned an invalid project: " + status);
                        TryInvoke(manager, "ReportProjectLoadFailure", new object[] { status });
                        warnedLoadRequestSequence = sequence;
                    }
                    return;
                }

                // Commit the sequence only after the path is complete and valid. Some
                // providers publish their sequence just before the project files become
                // visible; leaving it pending allows the next timer/final poll to retry.
                lastLoadRequestSequence = sequence;
                warnedLoadRequestSequence = 0;
                Action<string> handler = ProjectLoadRequested;
                if (handler != null)
                {
                    try { handler(state.ProjectPath); }
                    catch (Exception ex) { diagnostics.Error("ModuleHost", "ProjectLoadRequested subscriber failed.", ex); }
                }
            }
            catch (Exception ex)
            {
                diagnostics.Error("ModuleHost", "Project-manager polling failed.", ex);
            }
        }

        private void PollProjectCreationSave()
        {
            Control creator = projectCreationControl;
            if (creator == null || creator.IsDisposed) return;
            try
            {
                long sequence = ReadInt64(creator, "ProjectSavedSequence");
                if (sequence <= 0 || sequence == lastProjectSavedSequence) return;
                string path = LateBound.ReadString(creator, "LastSavedProjectPath").Trim();
                if (path.Length == 0) return;

                string status;
                if (!state.TryLoadProject(path, out status))
                {
                    if (warnedProjectSavedSequence != sequence)
                    {
                        diagnostics.Warning("ModuleHost", "Project creator returned an invalid saved project: " + status);
                        warnedProjectSavedSequence = sequence;
                    }
                    return;
                }
                lastProjectSavedSequence = sequence;
                AuditProjectSave(creator);
                warnedProjectSavedSequence = 0;
                RaiseProjectLoadRequested(state.ProjectPath);
            }
            catch (Exception ex)
            {
                diagnostics.Error("ModuleHost", "Project-creation polling failed.", ex);
            }
        }

        private void RaiseProjectLoadRequested(string path)
        {
            Action<string> handler = ProjectLoadRequested;
            if (handler == null) return;
            try { handler(path); }
            catch (Exception ex) { diagnostics.Error("ModuleHost", "ProjectLoadRequested subscriber failed.", ex); }
        }

        private void StateChanged(object sender, EventArgs e)
        {
            foreach (KeyValuePair<string, Control> pair in controls)
            {
                ModuleDescriptor descriptor = FindDescriptor(pair.Key);
                ApplyOnControlThread(descriptor, pair.Value, false);
            }
        }

        private void ApplyOnControlThread(ModuleDescriptor descriptor, Control control, bool force)
        {
            if (descriptor == null || control == null || control.IsDisposed) return;
            if (control.InvokeRequired)
            {
                try { control.BeginInvoke(new Action<ModuleDescriptor, Control, bool>(ApplyContext), descriptor, control, force); }
                catch (Exception ex) { diagnostics.Error("ModuleHost", "Could not marshal module settings to the UI thread.", ex); }
                return;
            }
            ApplyContext(descriptor, control, force);
        }

        private void ApplyContext(ModuleDescriptor descriptor, Control control, bool force)
        {
            if (descriptor == null || control == null || control.IsDisposed) return;
            if (String.Equals(descriptor.Id, CalibrationTest, StringComparison.OrdinalIgnoreCase))
                ApplyCalibrationHardwareIdentity(control);
            string language = LateBound.ReadString(settings, "LanguageCode");
            language = AppLanguages.ResolveCode(language);
            int languageIndex = AppLanguages.IndexOf(language);
            string dataRoot = LateBound.ReadString(settings, "DataRoot", "DataRootDirectory");
            string projectPath = state.ProjectPath;
            string pressurePlanSignature = String.Equals(descriptor.Id, PressureTest, StringComparison.OrdinalIgnoreCase)
                ? PressureProjectPlanSignature(projectPath) : String.Empty;
            string modelName = LateBound.ReadString(settings, "SoftwareModel", "Software.Model", "DeviceModel", "DeviceModelName");
            if (modelName.Length == 0) modelName = "HV-DAC Xpert";
            string unifiedModel = LateBound.ReadString(settings, "UnifiedAnalysisModelPath", "TaskMinus3.ModelPath");
            string analysisModel = DefectModelCatalog.Resolve(LateBound.ReadString(settings,"DefectModelKind"),
                LateBound.ReadString(settings,"TaskMinus3.ModelLibraryPath"),LateBound.ReadString(settings,"CompetitionModelPath"),modelRoot);
            bool[] analysisQuadrants = ReadAnalysisQuadrants();
            string signature = String.Join("|", new[]
            {
                language, dataRoot, projectPath, modelName, unifiedModel, analysisModel,
                SettingText("SupportsCosineVlf"), SettingText("SupportsCombinedVlf"), SettingText("MaximumVoltageKv"),
                state.DeviceConnected ? "device-ready" : "device-offline",
                LateBound.ReadString(settings, "Task35ModelPath"), LateBound.ReadString(settings, "Task36ModelPath"),
                LateBound.ReadString(settings, "Task37ModelPath"), LateBound.ReadString(settings, "Task38ModelPath"),
                LateBound.ReadString(settings, "ReportTemplatePath"), LateBound.ReadString(settings, "ReportOutputPath"),
                SettingText("ReportOutputFormat"), SettingText("GenerateStateGridReport"), SettingText("EnableAiFeatures"), SettingText("EnableDefectRecognition"), LateBound.ReadString(settings, "ReportPath", "ReportEnginePath"),
                SettingText("TaskMinus3.AttenuationTolerancePercent", "TaskMinus3.AttenuationDb"),
                SettingText("TaskMinus3.CorrelationErrorPercent"),
                SettingText("TaskMinus3.AutomaticThresholdMultiplier", "TaskMinus3.TriggerThreshold"),
                SettingText("TaskMinus3.MaximumPulseCount"),
                SettingText("TaskMinus3.CalibrationAverageCount"),
                SettingText("NoiseSimilarityTolerancePercent"),
                SettingText("FrequencyReminderMinHz"), SettingText("FrequencyReminderMaxHz"), SettingText("FrequencyDangerThresholdHz"),
                SettingText("CapacitanceNormalMinUf"), SettingText("CapacitanceNormalMaxUf"), SettingText("FrequencyReminderEnabled"),
                SettingText("UseCompensationCapacitance"), SettingText("CompensationCapacitanceUf"),
                SettingText("AutomaticPressureCountdownSeconds"), SettingText("AutomaticPressureCountdownEnabled"), SettingText("LocationSaveCycleCount"),
                SettingText("AutomaticRangeReminderEnabled"), SettingText("AnalyzeZeroVoltageData"), SettingText("LocationFilterAlgorithmId"),
                SettingText("LocationSuspiciousMinimumPercent"), SettingText("LocationManualAcceptanceMinimumPercent"), SettingText("LocationAutomaticAcceptanceMinimumPercent"),
                SettingText("PressureOscillationCircuitConfirmed"), SettingText("PressureRangeEvaluationCycleCount"),
                SettingText("TaskMinus3.MultipleReflectionSuppressionCount", "TaskMinus3.ReflectionSuppression"),
                SettingText("TaskMinus3.AlgorithmIndex"), SettingText("TaskMinus3.JointApproximationErrorPercent"), SettingText("TaskMinus3.EndpointPolicy"),
                SettingText("TaskMinus3.UseFirstArrivalTiming", "TaskMinus3.UseFirstArrival"),
                SettingText("TaskMinus3.PhaseFilterEnabled", "TaskMinus3.EnablePhaseFilter"),
                QuadrantSignature(analysisQuadrants), pressurePlanSignature
            });
            // The default check is cheap and value-gated even when the remaining
            // context signature is unchanged (for a genuinely new empty session).
            if(String.Equals(descriptor.Id,DataAnalysis,StringComparison.OrdinalIgnoreCase)||
                String.Equals(descriptor.Id,PressureTest,StringComparison.OrdinalIgnoreCase))
                ApplyLocationFilterDefaults(control,String.Equals(descriptor.Id,PressureTest,StringComparison.OrdinalIgnoreCase),settings,projectPath);
            string previous;
            if (!force && contextSignatures.TryGetValue(descriptor.Id, out previous) && String.Equals(previous, signature, StringComparison.Ordinal)) return;

            string appliedProjectPath;
            bool projectPathChanged = !appliedProjectPaths.TryGetValue(descriptor.Id, out appliedProjectPath) ||
                !String.Equals(appliedProjectPath ?? String.Empty, projectPath ?? String.Empty, StringComparison.OrdinalIgnoreCase);
            if (projectPathChanged) appliedProjectPaths[descriptor.Id] = projectPath ?? String.Empty;

            WithstandTestControl withstand = control as WithstandTestControl;
            if (withstand != null)
            {
                withstand.LanguageIndex = languageIndex;
                withstand.ConfigureCapabilities(LateBound.ReadBoolean(settings, false, "SupportsCosineVlf"),
                    LateBound.ReadBoolean(settings, false, "SupportsCombinedVlf"));
                if (projectPathChanged)
                {
                    double maximum;
                    if (!TryReadDouble(out maximum, "MaximumVoltageKv")) maximum = 0.0;
                    withstand.SetContext(projectPath, "A", 0.0, 60,
                        maximum);
                }
                contextSignatures[descriptor.Id] = signature;
                return;
            }

            TryInvoke(control, "SetLanguageCode", new object[] { language });
            TrySet(control, "LanguageIndex", languageIndex);
            TrySet(control, "VlfPolarityAvailable", LateBound.ReadBoolean(settings, false, "SupportsCosineVlf") ||
                LateBound.ReadBoolean(settings, false, "SupportsCombinedVlf"));
            TrySet(control, "DataRoot", dataRoot);
            double noiseTolerance;if(TryReadDouble(out noiseTolerance,"NoiseSimilarityTolerancePercent"))TrySet(control,"NoiseSimilarityTolerancePercent",noiseTolerance);
            double pressureLimit;
            TrySet(control, "MaximumPeakVoltageKv", TryReadDouble(out pressureLimit, "MaximumVoltageKv") ? pressureLimit : 0.0);
            TrySet(control, "DataRootDirectory", dataRoot);
            TrySet(control, "ModelName", modelName);
            TrySet(control, "ConclusionSystemName", modelName);
            ApplyModelPaths(control, unifiedModel);
            TrySet(control, "EnableAiFeatures", LateBound.ReadBoolean(settings, true, "EnableAiFeatures"));
            TrySet(control, "EnableDefectRecognition", LateBound.ReadBoolean(settings, true, "EnableDefectRecognition"));

            if (String.Equals(descriptor.Id, ReportGenerator, StringComparison.OrdinalIgnoreCase))
            {
                TrySet(control, "DiagnosticSink", new Action<string>(message => diagnostics.Error("ReportConclusion", message)));
                ApplyReportSettings(control);
            }
            if(String.Equals(descriptor.Id,ProjectCreation,StringComparison.OrdinalIgnoreCase)||
                String.Equals(descriptor.Id,DataBrowser,StringComparison.OrdinalIgnoreCase))
                TrySet(control,"DiagnosticSink",new Action<string>(message=>diagnostics.Error(descriptor.Id,message)));
            if(String.Equals(descriptor.Id,PressureTest,StringComparison.OrdinalIgnoreCase))
                TrySet(control,"PressureDiagnosticSink",new Action<string>(message=>diagnostics.Error("PressureInput",message)));

            if (String.Equals(descriptor.Id, PressureTest, StringComparison.OrdinalIgnoreCase))
            {
                TrySet(control, "DiagnosticSink", new Action<string>(message => diagnostics.Error("PressurePlan", message)));
                ApplyPressurePreferences(control);
            }

            if (String.Equals(descriptor.Id, DataAnalysis, StringComparison.OrdinalIgnoreCase))
                ApplyDataAnalysisSettings(control, analysisModel, analysisQuadrants);

            if (String.Equals(descriptor.Id, CalibrationTest, StringComparison.OrdinalIgnoreCase))
            {
                TrySet(control, "UseFirstArrivalTiming", LateBound.ReadBoolean(settings, false,
                    "TaskMinus3.UseFirstArrivalTiming", "TaskMinus3.UseFirstArrival"));
                double averageCount;
                if(TryReadDouble(out averageCount,"TaskMinus3.CalibrationAverageCount"))TrySet(control,"CalibrationAverageCount",(int)averageCount);
            }

            if (String.Equals(descriptor.Id, ProjectManagement, StringComparison.OrdinalIgnoreCase))
            {
                TrySet(control, "RootDirectory", dataRoot);
                TrySet(control, "CurrentProjectPath", projectPath ?? "");
                TrySet(control, "ExitViOnProjectLoad", false);
                if (!String.IsNullOrWhiteSpace(dataRoot)) TryInvoke(control, "BeginLoadRoot", new object[] { dataRoot });
            }
            else if (String.Equals(descriptor.Id, ProjectCreation, StringComparison.OrdinalIgnoreCase))
            {
                // The desktop already owns the page title and device-state chrome.
                // New controls hide their duplicate header; older releases safely ignore this late-bound property.
                TrySet(control, "EmbeddedMode", true);
                TrySet(control, "DeviceReady", state.DeviceConnected);
                // A dedicated read-only ART + GNSS provider does not energize outputs.
                TrySet(control, "HardwareAcquisitionAvailable", true);
                if (projectPathChanged && !String.IsNullOrWhiteSpace(projectPath))
                    TryInvoke(control, "LoadProject", new object[] { projectPath });
            }
            else if (String.Equals(descriptor.Id, PressureTest, StringComparison.OrdinalIgnoreCase))
            {
                ApplyPressureProjectPlan(control, projectPath, pressurePlanSignature);
            }
            else if (projectPathChanged && !String.IsNullOrWhiteSpace(projectPath))
            {
                if (!TrySet(control, "ProjectPath", projectPath))
                {
                    if (!TryInvoke(control, "BeginLoadProject", new object[] { projectPath }))
                        TryInvoke(control, "LoadProject", new object[] { projectPath });
                }
            }
            contextSignatures[descriptor.Id] = signature;
        }

        private void ApplyModelPaths(Control control, string unifiedModel)
        {
            string task35 = LateBound.ReadString(settings, "Task35ModelPath");
            string task36 = LateBound.ReadString(settings, "Task36ModelPath");
            string task37 = LateBound.ReadString(settings, "Task37ModelPath");
            string task38 = LateBound.ReadString(settings, "Task38ModelPath");
            TrySet(control, "Task35ModelPath", task35);
            TrySet(control, "Task36ModelPath", task36);
            TrySet(control, "Task37ModelPath", task37);
            TrySet(control, "Task38ModelPath", task38);
            TrySet(control, "UnifiedAnalysisModelPath", unifiedModel);
            if (!String.IsNullOrWhiteSpace(unifiedModel))
            {
                TrySet(control, "ModelLibraryPath", unifiedModel);
                TrySet(control, "ModelPath", unifiedModel);
            }
        }

        private void ApplyReportSettings(Control control)
        {
            string templatePath = LateBound.ReadString(settings, "ReportTemplatePath").Trim();
            if (templatePath.Length > 0) TrySet(control, "TemplatePath", templatePath);

            // An empty output setting deliberately restores the control's project-root default.
            TrySet(control, "OutputDirectory", LateBound.ReadString(settings, "ReportOutputPath").Trim());

            string outputFormat = SettingText("ReportOutputFormat").Trim();
            if (outputFormat.Length > 0) TrySet(control, "OutputFormat", outputFormat);
            // Also apply false: turning off this optional companion report must
            // reach an already-created report page without recreating it.
            TrySet(control, "GenerateStateGridReport", LateBound.ReadBoolean(settings, false, "GenerateStateGridReport"));

            string enginePath = ResolveConfiguredReportEnginePath();
            if (enginePath.Length > 0) TrySet(control, "ReportEnginePath", enginePath);
        }

        private string ResolveConfiguredReportEnginePath()
        {
            string configured = LateBound.ReadString(settings, "ReportEnginePath", "ReportPath").Trim();
            if (configured.Length == 0) return String.Empty;
            try
            {
                string full = Path.GetFullPath(configured);
                string[] candidates;
                if (Directory.Exists(full))
                {
                    candidates = new[]
                    {
                        Path.Combine(full, "HVX.Report.Engine.dll"),
                        Path.Combine(full, "runtime", "HVX.Report.Engine.dll")
                    };
                }
                else
                {
                    if (File.Exists(full) && String.Equals(Path.GetFileName(full), "HVX.Report.Engine.dll", StringComparison.OrdinalIgnoreCase))
                        return full;
                    string directory = Path.GetDirectoryName(full) ?? String.Empty;
                    candidates = new[]
                    {
                        Path.Combine(directory, "HVX.Report.Engine.dll"),
                        Path.Combine(directory, "runtime", "HVX.Report.Engine.dll")
                    };
                }
                for (int i = 0; i < candidates.Length; i++)
                    if (File.Exists(candidates[i])) return Path.GetFullPath(candidates[i]);
                diagnostics.Warning("ModuleHost", "ReportPath does not identify HVX.Report.Engine.dll or a report runtime directory; the report control will use its packaged runtime.");
            }
            catch (Exception ex)
            {
                diagnostics.Error("ModuleHost", "Invalid ReportPath; the packaged report runtime will be used.", ex);
            }
            return String.Empty;
        }

        private void ApplyDataAnalysisSettings(Control control, string modelPath, bool[] quadrants)
        {
            ApplyDataAnalysisSettings(control, modelPath, quadrants, settings);
        }

        private void ApplyDataAnalysisSettings(Control control, string modelPath, bool[] quadrants, object source)
        {
            // Detached pressure-analysis workers receive an immutable settings
            // snapshot. The ordinary page already got its project-aware default
            // before the context fast path; do not key it to the former project.
            if(!Object.ReferenceEquals(source,settings))ApplyLocationFilterDefaults(control,false,source,LateBound.ReadString(control,"ProjectPath"));
            TrySet(control, "ProtectedAnalysisSettingsManaged", true);
            double suspicious,manual,automatic;
            if(!TryReadDoubleFrom(source,out suspicious,"LocationSuspiciousMinimumPercent")||
                !TryReadDoubleFrom(source,out manual,"LocationManualAcceptanceMinimumPercent")||
                !TryReadDoubleFrom(source,out automatic,"LocationAutomaticAcceptanceMinimumPercent")||
                !HVX.LocationScoring.LocationScorePolicy.IsValid(suspicious,manual,automatic)||
                !TryInvoke(control,"SetLocationScoreThresholds",new object[]{suspicious,manual,automatic}))
                throw new InvalidOperationException("定位评分设置无法应用，请检查设置与组件版本。 / Location score settings could not be applied; check settings and component versions.");
            bool zeroAnalysis;
            TrySet(control, "AnalyzeZeroVoltageData", Boolean.TryParse(SettingTextFrom(source, "AnalyzeZeroVoltageData"), out zeroAnalysis) && zeroAnalysis);
            string endpointPolicy = SettingTextFrom(source, "TaskMinus3.EndpointPolicy");
            if (String.IsNullOrEmpty(endpointPolicy)) endpointPolicy = "2;1.5;OR;OR";
            if (!TrySet(control, "EndpointPolicy", endpointPolicy))
                throw new InvalidOperationException("首末端厂家参数无法应用，已停止加载分析模块，请检查配置与组件版本。");
            double number;
            if (TryReadDoubleFrom(source, out number, "TaskMinus3.JointApproximationErrorPercent") && InRange(number, 0.0, 100.0))
                TrySet(control, "JointApproximationErrorPercent", number);
            int integer;
            bool flag;
            if (TryReadDoubleFrom(source, out number, "TaskMinus3.AttenuationTolerancePercent", "TaskMinus3.AttenuationDb") && InRange(number, 0.0, 100.0))
                TrySet(control, "AnalysisAttenuationTolerancePercent", number);
            else LogInvalidAnalysisSetting("AnalysisAttenuationTolerancePercent", "0..100");

            if (TryReadDoubleFrom(source, out number, "TaskMinus3.CorrelationErrorPercent") && InRange(number, 0.0, 100.0))
                TrySet(control, "AnalysisCorrelationErrorPercent", number);
            else LogInvalidAnalysisSetting("AnalysisCorrelationErrorPercent", "0..100");

            if (TryReadDoubleFrom(source, out number, "TaskMinus3.AutomaticThresholdMultiplier", "TaskMinus3.TriggerThreshold") && InRange(number, 0.0, 1000.0))
                TrySet(control, "AnalysisAutomaticThresholdMultiplier", number);
            else LogInvalidAnalysisSetting("AnalysisAutomaticThresholdMultiplier", "0..1000");

            if (TryReadInt32From(source, out integer, "TaskMinus3.MaximumPulseCount") && integer >= 1 && integer <= 100)
                TrySet(control, "AnalysisMaximumPulseCount", integer);
            else LogInvalidAnalysisSetting("AnalysisMaximumPulseCount", "1..100");

            if (TryReadInt32From(source, out integer, "TaskMinus3.MultipleReflectionSuppressionCount", "TaskMinus3.ReflectionSuppression") && integer >= -1 && integer <= 100)
                TrySet(control, "AnalysisMultipleReflectionSuppressionCount", integer);
            else LogInvalidAnalysisSetting("AnalysisMultipleReflectionSuppressionCount", "-1..100");

            if (TryReadInt32From(source, out integer, "TaskMinus3.AlgorithmIndex") && integer >= 0 && integer <= 9)
                TrySet(control, "AnalysisAlgorithmIndex", integer);
            else LogInvalidAnalysisSetting("AnalysisAlgorithmIndex", "0..9 (A=5; B=7; E=8; E+=9)");

            if (Boolean.TryParse(SettingTextFrom(source, "TaskMinus3.UseFirstArrivalTiming", "TaskMinus3.UseFirstArrival"), out flag))
                TrySet(control, "AnalysisUseFirstArrivalTiming", flag);
            if (Boolean.TryParse(SettingTextFrom(source, "TaskMinus3.PhaseFilterEnabled", "TaskMinus3.EnablePhaseFilter"), out flag))
                TrySet(control, "AnalysisPhaseFilterEnabled", flag);
            if (quadrants != null && quadrants.Length == 4)
                TrySet(control, "AnalysisQuadrants", (bool[])quadrants.Clone());
            if (!String.IsNullOrWhiteSpace(modelPath))
                TrySet(control, "ModelLibraryPath", modelPath);
        }

        private bool[] ReadAnalysisQuadrants()
        {
            return ReadAnalysisQuadrants(settings);
        }

        private static bool[] ReadAnalysisQuadrants(object source)
        {
            object direct = LateBound.Read(source, "TaskMinus3.AnalysisQuadrants", "TaskMinus3.Quadrants");
            bool[] directArray = direct as bool[];
            if (directArray != null && directArray.Length == 4) return (bool[])directArray.Clone();

            object q1 = LateBound.Read(source, "TaskMinus3.Quadrant0To90");
            object q2 = LateBound.Read(source, "TaskMinus3.Quadrant90To180");
            object q3 = LateBound.Read(source, "TaskMinus3.Quadrant180To270");
            object q4 = LateBound.Read(source, "TaskMinus3.Quadrant270To360");
            if (q1 != null && q2 != null && q3 != null && q4 != null)
            {
                bool parsed1, parsed2, parsed3, parsed4;
                if (Boolean.TryParse(Convert.ToString(q1, CultureInfo.InvariantCulture), out parsed1) &&
                    Boolean.TryParse(Convert.ToString(q2, CultureInfo.InvariantCulture), out parsed2) &&
                    Boolean.TryParse(Convert.ToString(q3, CultureInfo.InvariantCulture), out parsed3) &&
                    Boolean.TryParse(Convert.ToString(q4, CultureInfo.InvariantCulture), out parsed4))
                    return new[] { parsed1, parsed2, parsed3, parsed4 };
            }

            string text = LateBound.ReadString(source, "TaskMinus3.PhaseQuadrants", "TaskMinus3.PhaseFilter");
            if (String.IsNullOrWhiteSpace(text)) return new[] { true, true, true, true };
            bool[] selected = new bool[4];
            string[] tokens = text.ToUpperInvariant().Split(new[] { ',', ';', '|', ' ', '，', '；' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token == "I" || token == "1") selected[0] = true;
                else if (token == "II" || token == "2") selected[1] = true;
                else if (token == "III" || token == "3") selected[2] = true;
                else if (token == "IV" || token == "4") selected[3] = true;
            }
            return selected;
        }

        private string SettingText(params string[] paths)
        {
            return SettingTextFrom(settings, paths);
        }

        private static string SettingTextFrom(object source, params string[] paths)
        {
            object value = LateBound.Read(source, paths);
            return value == null ? String.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
        }

        private bool TryReadDouble(out double value, params string[] paths)
        {
            return TryReadDoubleFrom(settings, out value, paths);
        }

        private static bool TryReadDoubleFrom(object source, out double value, params string[] paths)
        {
            return Double.TryParse(SettingTextFrom(source, paths), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   !Double.IsNaN(value) && !Double.IsInfinity(value);
        }

        private bool TryReadInt32(out int value, params string[] paths)
        {
            return TryReadInt32From(settings, out value, paths);
        }

        private static bool TryReadInt32From(object source, out int value, params string[] paths)
        {
            string text = SettingTextFrom(source, paths);
            if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
            double number;
            if (!Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                Double.IsNaN(number) || Double.IsInfinity(number) || Math.Truncate(number) != number ||
                number < Int32.MinValue || number > Int32.MaxValue) return false;
            value = (int)number;
            return true;
        }

        private bool TryReadBoolean(out bool value, params string[] paths)
        {
            return Boolean.TryParse(SettingText(paths), out value);
        }

        private void LogInvalidAnalysisSetting(string name, string expectedRange)
        {
            diagnostics.Warning("ModuleHost", "Skipped invalid -3 setting " + name + "; expected " + expectedRange + ".");
        }

        private static bool InRange(double value, double minimum, double maximum)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && value >= minimum && value <= maximum;
        }

        private static string QuadrantSignature(bool[] quadrants)
        {
            if (quadrants == null || quadrants.Length != 4) return String.Empty;
            return (quadrants[0] ? "1" : "0") + (quadrants[1] ? "1" : "0") +
                   (quadrants[2] ? "1" : "0") + (quadrants[3] ? "1" : "0");
        }

        private bool TrySet(object target, string name, object value)
        {
            string error;
            bool changed = LateBound.TrySet(target, name, value, out error);
            if (!changed && error.Length > 0) diagnostics.Warning("ModuleHost", name + ": " + error);
            return changed;
        }

        private bool TryInvoke(object target, string name, object[] arguments)
        {
            object result;
            string error;
            bool invoked = LateBound.TryInvoke(target, name, arguments, out result, out error);
            if (!invoked && error.Length > 0) diagnostics.Warning("ModuleHost", name + ": " + error);
            return invoked;
        }

        private Assembly LoadModuleAssembly(string path, string directory)
        {
            Assembly assembly = Assembly.LoadFrom(path);
            lock (assemblyGate) assemblyDirectories[assembly] = directory;
            return assembly;
        }

        private Assembly ResolveModuleAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string directory = null;
                if (args.RequestingAssembly != null)
                {
                    lock (assemblyGate) assemblyDirectories.TryGetValue(args.RequestingAssembly, out directory);
                    if (String.IsNullOrWhiteSpace(directory))
                    {
                        string location = SafeLocation(args.RequestingAssembly);
                        if (!String.IsNullOrWhiteSpace(location) && IsUnderModulesRoot(location)) directory = Path.GetDirectoryName(location);
                    }
                }
                if (String.IsNullOrWhiteSpace(directory)) directory = activeModuleDirectory;
                if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

                string simpleName = new AssemblyName(args.Name).Name;
                if (String.IsNullOrWhiteSpace(simpleName) || simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                string candidate = Path.GetFullPath(Path.Combine(directory, simpleName + ".dll"));
                if (!IsDirectChild(candidate, directory) || !File.Exists(candidate))
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, simpleName + ".exe"));
                    if (!IsDirectChild(candidate, directory) || !File.Exists(candidate)) return null;
                }
                Assembly loaded = Assembly.LoadFrom(candidate);
                lock (assemblyGate) assemblyDirectories[loaded] = directory;
                return loaded;
            }
            catch (Exception ex)
            {
                diagnostics.Error("ModuleHost", "Dependency resolution failed for " + (args == null ? String.Empty : args.Name), ex);
                return null;
            }
        }

        private Type FindUserControlType(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetExportedTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            if (types == null) return null;
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type != null && type.IsPublic && !type.IsAbstract && typeof(UserControl).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                    return type;
            }
            return null;
        }

        private ModuleDescriptor FindDescriptor(string id)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            for (int i = 0; i < KnownDescriptors.Length; i++)
                if (String.Equals(KnownDescriptors[i].Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return KnownDescriptors[i];
            return null;
        }

        private bool IsUnderModulesRoot(string path)
        {
            string root = modulesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDirectChild(string path, string directory)
        {
            string parent = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeLocation(Assembly assembly)
        {
            try { return assembly == null ? String.Empty : assembly.Location; }
            catch { return String.Empty; }
        }

        private static long ReadInt64(object target, string name)
        {
            object value = LateBound.Read(target, name);
            if (value == null) return 0L;
            long parsed;
            return Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L;
        }

        private static string RootMessage(Exception error)
        {
            Exception root = error;
            while (root is TargetInvocationException && root.InnerException != null) root = root.InnerException;
            return root.Message;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            DisposeModuleAudit();
            DisposePressureInputActions();
            DisposeCableStructureSync();
            DisposeEnvironmentProvider();
            state.Changed -= StateChanged;
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveModuleAssembly;
            if (projectPollTimer != null)
            {
                projectPollTimer.Stop();
                projectPollTimer.Tick -= PollProjectModules;
                projectPollTimer.Dispose();
                projectPollTimer = null;
            }
            controls.Clear();
            if (calibrationAcquisitionProcess != null)
            {
                // Closing the desktop must not interrupt an in-progress capture.
                calibrationAcquisitionProcess.Dispose();
                calibrationAcquisitionProcess = null;
            }
            contextSignatures.Clear();
            appliedProjectPaths.Clear();
            lock (assemblyGate) assemblyDirectories.Clear();
        }
    }
}
