using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            if (args != null && args.Length > 0)
            {
                try
                {
                    if (String.Equals(args[0], "--render-preview", StringComparison.OrdinalIgnoreCase))
                        return RenderPreview(args);
                    if (String.Equals(args[0], "--state-smoke", StringComparison.OrdinalIgnoreCase))
                        return RunStateSmoke(args);
                    if (String.Equals(args[0], "--module-smoke", StringComparison.OrdinalIgnoreCase))
                        return RunModuleSmoke(args);
                    if (String.Equals(args[0], "--render-settings", StringComparison.OrdinalIgnoreCase))
                        return RenderSettings(args);
                    if (String.Equals(args[0], "--render-integrated-module", StringComparison.OrdinalIgnoreCase))
                        return RenderIntegratedModule(args);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }
            }

            Xiongxin.OWTS.Ui.DialogPlacement.Install();
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                RecordFailure(e.Exception);
                MessageBox.Show("程序遇到未处理的错误。诊断信息已保存到本机数据目录。\r\n\r\n" + e.Exception.Message,
                    "HV-DAC Xpert", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                RecordFailure(e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject)));
            };

            try
            {
                if(args!=null&&args.Length>0&&String.Equals(args[0],"--defect-recognition",StringComparison.OrdinalIgnoreCase))
                {
                    if(args.Length!=3)throw new ArgumentException("This module must be opened from the main application.");
                    return CompetitionDefectLauncher.RunChild(args[1],args[2]);
                }
                AppSettings settings; string configurationDiagnostic;
                if (!AppSettings.TryLoad(AppPaths.SettingsFile, out settings, out configurationDiagnostic))
                {
                    // Do not silently skip recovery merely because the damaged
                    // file exists. Continue only with the explicit offline defaults;
                    // this notice neither rewrites the original nor enables hardware.
                    DialogResult choice = MessageBox.Show(configurationDiagnostic +
                        "\r\n\r\n原配置文件保留未改动 / Original configuration preserved:\r\n" + AppPaths.SettingsFile +
                        "\r\n\r\n是否以离线默认配置继续？可在参数设置中检查配置。\r\nContinue with offline defaults? Review the configuration in Settings.",
                        "配置读取失败 / Configuration load failed", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (choice != DialogResult.OK) return 0;
                }
                if (!String.IsNullOrWhiteSpace(settings.SessionConfigurationNotice))
                    MessageBox.Show(settings.SessionConfigurationNotice,
                        "采集长度已调整 / Acquisition duration adjusted", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                if (FirstHardwareProvisioning.RequiresSessionOffline())
                {
                    bool wasEnabled = settings.Hardware != null && settings.Hardware.Enabled;
                    settings = settings.Clone();
                    if (settings.Hardware == null) settings.Hardware = new HardwareSettings();
                    settings.Hardware.Enabled = false; // session only; never overwrite the old disk identity
                    settings.SessionHardwareForcedOffline = true;
                    if (wasEnabled)
                        MessageBox.Show("本次安装尚未完成硬件模式或选择了离线安装。原设备配置保留在磁盘，本次运行仅使用离线功能；完成同一安装包的硬件阶段并重新打开软件后再连接设备。",
                            "硬件暂时离线", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (!File.Exists(AppPaths.SettingsFile))
                {
                    using (FirstRunForm firstRun = new FirstRunForm(settings))
                    {
                        if (firstRun.ShowDialog() != DialogResult.OK) return 0;
                        if (firstRun.Settings != null) settings = firstRun.Settings;
                    }
                }
#if PRESSURE_STANDALONE_20261008
                bool ownsTest;
                using (var testOwner = new Mutex(true, @"Local\HVX.PressureStandalone.Operator", out ownsTest))
                {
                    if (!ownsTest) { MessageBox.Show("独立加压测试已打开，请使用现有窗口。", "加压测试", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }
                    try { Application.Run(new MainForm(settings, new ApplicationState(), false, true)); }
                    finally { testOwner.ReleaseMutex(); }
                }
#else
                Application.Run(new MainForm(settings));
#endif
                return 0;
            }
            catch (Exception error)
            {
                RecordFailure(error);
                MessageBox.Show("程序无法启动：\r\n" + error.Message, "HV-DAC Xpert",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int RenderPreview(string[] args)
        {
            if (args.Length != 2 || String.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException("Usage: --render-preview <png>");
            AppSettings settings = AppSettings.Load();
            using (MainForm form = new MainForm(settings, new ApplicationState(), true))
                form.RenderToPng(args[1]);
            Console.WriteLine(Path.GetFullPath(args[1]));
            return 0;
        }

        private static int RenderIntegratedModule(string[] args)
        {
            if (args.Length != 5) throw new ArgumentException(
                "Usage: --render-integrated-module <module-id> <png> <width> <height> (offline fixtures only)");
            string id = args[1];
            if (id != ModuleHost.DataAnalysis && id != ModuleHost.DataBrowser &&
                id != ModuleHost.ProjectManagement && id != ModuleHost.ProjectCreation &&
                id != ModuleHost.ReportGenerator && id != ModuleHost.PressureTest && id != ModuleHost.CalibrationTest)
                throw new ArgumentException("Unknown preview module.");
            int width, height;
            if (!Int32.TryParse(args[3], out width) || !Int32.TryParse(args[4], out height) ||
                width < 760 || height < 600 || width > 4096 || height > 2160)
                throw new ArgumentException("Preview dimensions are outside supported test bounds.");
            string output = Path.GetFullPath(args[2]);
            string fixtureRoot = Path.Combine(Path.GetDirectoryName(output), "integrated-fixture");
            Directory.CreateDirectory(fixtureRoot);
            string fixture = CreateFixtureProject(fixtureRoot);
            AppSettings settings = AppSettings.Load().Clone();
            settings.LanguageCode = "zh-CN";
            if (settings.Hardware == null) settings.Hardware = new HardwareSettings();
            settings.Hardware.Enabled = false;
            ApplicationState state = new ApplicationState();
            string message;
            if (!state.TryLoadProject(fixture, out message)) throw new InvalidDataException(message);
            using (MainForm form = new MainForm(settings, state, true))
            {
                // Create the native handle before sizing/opening a module.  Showing a
                // pre-sized form later runs its first-show fit logic and silently
                // changes the requested viewport, invalidating compact UI checks.
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Application.DoEvents();
                // width/height describe a simulated monitor working area, not
                // an impossible physical window size at high Windows scaling.
                // The same fit path as a real 13-inch display clamps MinimumSize.
                ResponsiveFormLayout.FitToBounds(form,
                    new Rectangle(0, 0, width, height), form.DeviceDpi,
                    new Size(760, 620));
                Size fittedSize = form.Size;
                Size fittedMinimum = form.MinimumSize;
                form.PerformLayout();
                Application.DoEvents();
                if (form.Width > width - 24 || form.Height > height - 24)
                    throw new InvalidOperationException("Integrated preview exceeds simulated screen " +
                        width + "x" + height + ": " + form.Width + "x" + form.Height +
                        "; fit=" + fittedSize.Width + "x" + fittedSize.Height +
                        "; fitMin=" + fittedMinimum.Width + "x" + fittedMinimum.Height +
                        "; min=" + form.MinimumSize.Width + "x" + form.MinimumSize.Height +
                        "; dpi=" + form.DeviceDpi + ".");
                bool testPage = id == ModuleHost.PressureTest || id == ModuleHost.CalibrationTest;
                if (testPage || id == ModuleHost.ReportGenerator)
                {
                    // Rendering is strictly offline. Do not enter the normal test
                    // navigation path (which can inspect hardware or show dialogs).
                    FieldInfo hostField = typeof(MainForm).GetField("moduleHost", BindingFlags.Instance | BindingFlags.NonPublic);
                    ModuleHost host = (ModuleHost)hostField.GetValue(form);
                    if (!testPage) state.ClearProject();
                    Control control;
                    string error;
                    if (!host.TryCreateControl(id, out control, out error)) throw new InvalidOperationException(error);
                    if (!testPage)
                    {
                        SetRequiredPublicProperty(control, "PromptAnalysisOnLoad", false);
                        if (!state.TryLoadProject(fixture, out message)) throw new InvalidDataException(message);
                    }
                    MethodInfo show = typeof(MainForm).GetMethod("ShowEmbeddedModule", BindingFlags.Instance | BindingFlags.NonPublic);
                    show.Invoke(form, new object[] { id, control, testPage ? (bool?)true : null });
                }
                else
                {
                    MethodInfo open = typeof(MainForm).GetMethod("OpenModule", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (open == null) throw new MissingMethodException("MainForm.OpenModule");
                    open.Invoke(form, new object[] { id });
                }
                form.RenderToPng(output);
                string snapshot = form.StateSnapshot();
                if (!snapshot.Contains("ActiveModule=" + id))
                    throw new InvalidOperationException("Module did not open: " + snapshot);
                File.WriteAllText(output + ".state.txt", snapshot, Encoding.UTF8);
            }
            return 0;
        }

        private static int RunStateSmoke(string[] args)
        {
            if (args.Length != 2 || String.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException("Usage: --state-smoke <directory>");

            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            string fixture = Path.Combine(output, "fixture-project");
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "ITEM.in"), "STATE-SMOKE\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(fixture, "CABLE.in"), "STATE-SMOKE\r\n", Encoding.UTF8);

            // A regression fixture must not depend on the operator's saved setup.
            AppSettings settings = new AppSettings();
            if (settings.Hardware == null) settings.Hardware = new HardwareSettings();
            settings.LanguageCode = "zh-CN";
            settings.Hardware.Enabled = true;
            ApplicationState state = new ApplicationState();
            StringBuilder log = new StringBuilder();

            using (MainForm form = new MainForm(settings, state, true))
            {
                form.RenderToPng(Path.Combine(output, "01-offline-no-project.png"));
                log.AppendLine("[offline-no-project]");
                string offlineNoProject = form.StateSnapshot();
                log.Append(offlineNoProject);
                RequireContains(offlineNoProject, "PressureTest:Visible=True;Enabled=False;Badge=仿真");
                RequireContains(offlineNoProject, "CalibrationTest:Visible=True;Enabled=False;Badge=仿真");

                state.SetHardware(new HardwareStatusSnapshot
                {
                    HardwareEnabled = true,
                    DriverInstalled = true,
                    Connected = true,
                    IdentityValid = true,
                    StatusCode = "Ready",
                    Message = "指定 DS600 已就绪",
                    AssetMatchStatus = "Unknown",
                    HubMac = settings.Hardware.SelectedHubMac,
                    ControllerSerial = settings.Hardware.ControllerSerial,
                    AcquisitionSerial = settings.Hardware.AcquisitionSerial,
                    CheckedUtc = DateTime.UtcNow
                });
                form.RenderToPng(Path.Combine(output, "02-connected-no-project.png"));
                log.AppendLine("[connected-no-project]");
                string connectedNoProject = form.StateSnapshot();
                log.Append(connectedNoProject);
                RequireContains(connectedNoProject, "PressureTest:Visible=True;Enabled=False;Badge=实机");
                RequireContains(connectedNoProject, "CalibrationTest:Visible=True;Enabled=False;Badge=HV DAC");
                RequireContains(connectedNoProject, "DeviceConnected=True");
                RequireContains(connectedNoProject, "DeviceStatus=设备连接状态");
                RequireContains(connectedNoProject, "DeviceDetail=已连接 · 未列入出厂档案，可选择配置或忽略");
                if (connectedNoProject.Contains("DS600") || connectedNoProject.Contains("not found"))
                    throw new InvalidOperationException("Internal device labels leaked into customer-facing status.");

                string validation;
                if (!state.TryLoadProject(fixture, out validation))
                    throw new InvalidOperationException("Smoke project could not be loaded: " + validation);
                form.RenderToPng(Path.Combine(output, "03-connected-project.png"));
                log.AppendLine("[connected-project]");
                string connectedProject = form.StateSnapshot();
                log.Append(connectedProject);
                RequireContains(connectedProject, "PressureTest:Visible=True;Enabled=True;Badge=实机");
                RequireContains(connectedProject, "CalibrationTest:Visible=True;Enabled=True;Badge=HV DAC");
                RequireContains(connectedProject, "DeviceStatus=设备连接状态");

                state.SetHardware(new HardwareStatusSnapshot
                {
                    HardwareEnabled = true,
                    StatusCode = "Offline",
                    Message = "DS600 internal transport not found",
                    CheckedUtc = DateTime.UtcNow
                });
                form.RenderToPng(Path.Combine(output, "04-offline-project-simulation.png"));
                log.AppendLine("[offline-project-simulation]");
                string offlineProject = form.StateSnapshot();
                log.Append(offlineProject);
                RequireContains(offlineProject, "PressureTest:Visible=True;Enabled=True;Badge=仿真");
                RequireContains(offlineProject, "CalibrationTest:Visible=True;Enabled=True;Badge=仿真");
                RequireContains(offlineProject, "DeviceDetail=尚未连接指定设备");
                if (offlineProject.Contains("DS600") || offlineProject.Contains("not found"))
                    throw new InvalidOperationException("Raw connector message leaked into customer-facing status.");

                MethodInfo showEmbedded = typeof(MainForm).GetMethod("ShowEmbeddedModule",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (showEmbedded == null) throw new MissingMethodException("MainForm.ShowEmbeddedModule");
                Size homeSize = form.ClientSize;
                for (int navigation = 0; navigation < 3; navigation++)
                {
                    Panel dummy = new Panel();
                    showEmbedded.Invoke(form, new object[] { "DataBrowser", dummy, null });
                    string child = form.StateSnapshot();
                    RequireContains(child, "ActiveModule=DataBrowser");
                    RequireContains(child, "HeaderAction=返回主页");
                    RequireContains(child, "CloseButtonDisabled=True");
                    RequireContains(child, "NativeCloseButtonDisabled=True");
                    if (form.ClientSize != homeSize)
                        throw new InvalidOperationException("Client size changed when entering a module.");
                    if (navigation == 0)
                        form.RenderToPng(Path.Combine(output, "05-embedded-navigation.png"));

                    // A user close gesture on a child page returns home rather than
                    // terminating the application. Repeat to catch layout drift.
                    form.Close();
                    if (form.IsDisposed)
                        throw new InvalidOperationException("User close disposed the window while a module was active.");
                    string returned = form.StateSnapshot();
                    RequireContains(returned, "ActiveModule=");
                    RequireContains(returned, "HeaderAction=设置");
                    RequireContains(returned, "CloseButtonDisabled=False");
                    RequireContains(returned, "NativeCloseButtonDisabled=False");
                    if (form.ClientSize != homeSize)
                        throw new InvalidOperationException("Client size changed when returning home.");
                }
                log.AppendLine("[navigation] repeated enter/close-to-home: 3; client size stable");
                using (HelpForm contextualHelp = new HelpForm(settings, "DataBrowser"))
                    RequireContains(contextualHelp.Text, "数据浏览");
            }

            File.WriteAllText(Path.Combine(output, "state-smoke.txt"), log.ToString(), Encoding.UTF8);
            Console.WriteLine(output);
            return 0;
        }

        private static int RunModuleSmoke(string[] args)
        {
            if (args.Length != 2 || String.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException("Usage: --module-smoke <directory>");

            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            string fixture = CreateFixtureProject(output);
            AppSettings settings = AppSettings.Load().Clone();
            if (settings.Hardware == null) settings.Hardware = new HardwareSettings();
            settings.Hardware.Enabled = false;
            settings.DataRoot = output;
            string reportModuleRoot = Path.Combine(EmbeddedModulePayload.ResolveModulesRoot(), "ReportGenerator");
            settings.ReportTemplatePath = Path.Combine(reportModuleRoot, "templates", "EPFT_OWTS_Report_Template_v1.0.docx");
            settings.ReportOutputPath = Path.Combine(output, "configured-reports");
            settings.ReportOutputFormat = ReportOutputFormat.WordOnly;
            settings.ReportPath = Path.Combine(reportModuleRoot, "runtime", "HVX.Report.Engine.dll");
            ApplicationState state = new ApplicationState();
            string projectMessage;
            if (!state.TryLoadProject(fixture, out projectMessage))
                throw new InvalidOperationException("Module-smoke project could not be loaded: " + projectMessage);

            string[] moduleIds =
            {
                ModuleHost.ProjectCreation,
                ModuleHost.ProjectManagement,
                ModuleHost.DataBrowser,
                ModuleHost.DataAnalysis,
                ModuleHost.ReportGenerator,
                ModuleHost.PressureTest,
                ModuleHost.CalibrationTest
            };
            StringBuilder log = new StringBuilder();
            log.AppendLine("ModuleSmoke=HVXUnifiedDesktop");
            log.AppendLine("ProjectPath=" + state.ProjectPath);
            log.AppendLine("HardwareEnabled=False");
            int failures = 0;
            string progressPath = Path.Combine(output, "module-smoke-progress.txt");

            using (ModuleHost host = new ModuleHost(settings, state))
            {
                log.AppendLine("ModulesRoot=" + host.ModulesRoot);
                for (int i = 0; i < moduleIds.Length; i++)
                {
                    string id = moduleIds[i];
                    Control control = null;
                    try
                    {
                        log.AppendLine(id + ":Begin");
                        File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);

                        // ReportGenerator normally offers an interactive analysis-file
                        // prompt as soon as ProjectPath is assigned.  Smoke mode must be
                        // non-interactive, so create it first, disable that prompt, and
                        // then propagate the already validated fixture project.
                        if (String.Equals(id, ModuleHost.ReportGenerator, StringComparison.OrdinalIgnoreCase))
                            state.ClearProject();
                        if (String.Equals(id, ModuleHost.ProjectCreation, StringComparison.OrdinalIgnoreCase))
                            state.ClearProject();
                        string loadError;
                        if (!host.TryCreateControl(id, out control, out loadError) || control == null)
                        {
                            failures++;
                            log.AppendLine(id + ":Load=False;Error=" + SingleLine(loadError));
                            File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);
                            continue;
                        }
                        log.AppendLine(id + ":ControlCreated;Type=" + control.GetType().AssemblyQualifiedName);
                        if (String.Equals(id, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase))
                            SetRequiredPublicProperty(control, "SimulationMode", true);
                        if (String.Equals(id, ModuleHost.DataAnalysis, StringComparison.OrdinalIgnoreCase) &&
                            String.IsNullOrWhiteSpace(settings.TaskMinus3.ModelLibraryPath))
                        {
                            string bundledModel = DefectModelCatalog.Resolve(settings.DefectModelKind,
                                settings.TaskMinus3.ModelLibraryPath, settings.CompetitionModelPath);
                            RequirePublicProperty(control, "ModelLibraryPath", bundledModel);
                            if (!File.Exists(bundledModel)) throw new FileNotFoundException("Bundled analysis model is missing.", bundledModel);
                            log.AppendLine(id + ":BundledModelApplied=True");
                        }
                        File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);

                        if (String.Equals(id, ModuleHost.ProjectCreation, StringComparison.OrdinalIgnoreCase))
                        {
                            string creationRoot = Path.Combine(output, "project-creation-integration");
                            Directory.CreateDirectory(creationRoot);
                            SetRequiredPublicProperty(control, "ShowDialogMessages", false);
                            SetRequiredPublicProperty(control, "AutoRefreshWeatherOnHardwareSample", false);
                            SetRequiredPublicProperty(control, "DataRootDirectory", creationRoot);
                            SetRequiredPublicProperty(control, "SettingsPath", Path.Combine(output, "project-creation-smoke.ini"));
                            SetRequiredPublicProperty(control, "StartName", "冒烟始端");
                            SetRequiredPublicProperty(control, "EndName", "冒烟终端");
                            SetRequiredPublicProperty(control, "LoadType", "三相单芯");
                            SetRequiredPublicProperty(control, "PhaseVoltage", "8.7kV");
                            InvokeRequiredPublicMethod(control, "SetPhaseCableLengths", 400.0, 406.0, 403.0);
                            object saveResult = InvokeRequiredPublicMethod(control, "SaveProject");
                            if (!(saveResult is bool) || !(bool)saveResult)
                                throw new InvalidOperationException("ProjectCreation SaveProject returned false.");
                            string savedPath = Convert.ToString(ReadRequiredPublicProperty(control, "LastSavedProjectPath"));
                            // Model the real user action: save and immediately leave the
                            // page, before the 250 ms timer gets another tick. Stop performs
                            // one final synchronous poll and must still adopt the saved path.
                            host.StopProjectManagerPolling();
                            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                            while ((!state.ProjectLoaded || !String.Equals(state.ProjectPath, savedPath, StringComparison.OrdinalIgnoreCase)) &&
                                   DateTime.UtcNow < deadline)
                            {
                                Application.DoEvents();
                                Thread.Sleep(20);
                            }
                            if (!state.ProjectLoaded || !String.Equals(state.ProjectPath, savedPath, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("A saved project did not become the current project. Saved='" +
                                    savedPath + "', current='" + state.ProjectPath + "'.");
                            log.AppendLine(id + ":SaveBecameCurrent=True;Path=" + savedPath);
                            File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);
                        }

                        if (String.Equals(id, ModuleHost.ReportGenerator, StringComparison.OrdinalIgnoreCase))
                        {
                            RequirePublicProperty(control, "TemplatePath", settings.ReportTemplatePath);
                            RequirePublicProperty(control, "OutputDirectory", settings.ReportOutputPath);
                            RequirePublicProperty(control, "OutputFormat", settings.ReportOutputFormat.ToString());
                            RequirePublicProperty(control, "ReportEnginePath", settings.ReportPath);
                            log.AppendLine(id + ":SettingsMapped=True;OutputFormat=" + settings.ReportOutputFormat);
                            PropertyInfo prompt = control.GetType().GetProperty("PromptAnalysisOnLoad",
                                BindingFlags.Instance | BindingFlags.Public);
                            if (prompt != null && prompt.CanWrite) prompt.SetValue(control, false, null);
                            string reloadMessage;
                            if (!state.TryLoadProject(fixture, out reloadMessage))
                                throw new InvalidOperationException("Could not restore the fixture for ReportGenerator: " + reloadMessage);
                            log.AppendLine(id + ":FixtureApplied");
                            File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);
                        }

                        string imagePath = Path.Combine(output, (i + 1).ToString("00") + "-" + id + ".png");
                        string renderError;
                        bool rendered = TryRenderModule(control, imagePath, out renderError);
                        if (!rendered) failures++;
                        log.AppendLine(id + ":Load=True;Type=" + control.GetType().AssemblyQualifiedName +
                            ";Render=" + rendered + (rendered ? ";Png=" + Path.GetFileName(imagePath) : ";Error=" + SingleLine(renderError)));
                        File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);
                    }
                    catch (Exception error)
                    {
                        failures++;
                        log.AppendLine(id + ":Load=False;Error=" + SingleLine(error.ToString()));
                        File.WriteAllText(progressPath, log.ToString(), Encoding.UTF8);
                    }
                    finally
                    {
                        if (String.Equals(id, ModuleHost.ProjectManagement, StringComparison.OrdinalIgnoreCase) ||
                            String.Equals(id, ModuleHost.ProjectCreation, StringComparison.OrdinalIgnoreCase))
                            host.StopProjectManagerPolling();
                        if (control != null)
                        {
                            try
                            {
                                if (control.Parent != null) control.Parent.Controls.Remove(control);
                                control.Dispose();
                            }
                            catch { }
                        }
                        Application.DoEvents();
                    }
                }
            }

            log.AppendLine("Failures=" + failures);
            File.WriteAllText(Path.Combine(output, "module-smoke.txt"), log.ToString(), Encoding.UTF8);
            Console.WriteLine(output);
            return failures == 0 ? 0 : 1;
        }

        private static int RenderSettings(string[] args)
        {
            if (args.Length != 2 || String.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException("Usage: --render-settings <png>");
            string path = Path.GetFullPath(args[1]);
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            using (SettingsForm form = new SettingsForm(AppSettings.Load()))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                form.PerformLayout();
                Application.DoEvents();
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(path, ImageFormat.Png);
                }
                form.Hide();
            }
            Console.WriteLine(path);
            return 0;
        }

        private static string CreateFixtureProject(string output)
        {
            string fixture = Path.Combine(output, "fixture-project");
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "ITEM.in"), "MODULE-SMOKE\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(fixture, "CABLE.in"), "MODULE-SMOKE\r\n", Encoding.UTF8);
            return fixture;
        }

        private static bool TryRenderModule(Control control, string path, out string error)
        {
            error = String.Empty;
            try
            {
                using (Form surface = new Form())
                {
                    surface.FormBorderStyle = FormBorderStyle.None;
                    surface.ShowInTaskbar = false;
                    surface.StartPosition = FormStartPosition.Manual;
                    surface.Location = new Point(-30000, -30000);
                    surface.ClientSize = new Size(1180, 720);
                    surface.BackColor = Color.White;
                    control.Dock = DockStyle.Fill;
                    surface.Controls.Add(control);
                    surface.Show();
                    surface.PerformLayout();
                    control.PerformLayout();
                    Application.DoEvents();
                    control.Refresh();
                    using (Bitmap bitmap = new Bitmap(surface.ClientSize.Width, surface.ClientSize.Height,
                        PixelFormat.Format32bppArgb))
                    {
                        surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, surface.ClientSize));
                        bitmap.Save(path, ImageFormat.Png);
                    }
                    surface.Controls.Remove(control);
                    surface.Hide();
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.ToString();
                return false;
            }
        }

        private static string SingleLine(string value)
        {
            return (value ?? String.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static void RequireContains(string text, string expected)
        {
            if ((text ?? String.Empty).IndexOf(expected ?? String.Empty, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("State smoke assertion failed. Missing: " + expected);
        }

        private static void RequirePublicProperty(object target, string name, string expected)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead)
                throw new MissingMemberException(target.GetType().FullName, name);
            string actual = Convert.ToString(property.GetValue(target, null));
            string normalizedExpected = expected ?? String.Empty;
            if (!String.Equals(actual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(name + " was not mapped. Expected '" + normalizedExpected + "', actual '" + actual + "'.");
        }

        private static object ReadRequiredPublicProperty(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead)
                throw new MissingMemberException(target.GetType().FullName, name);
            return property.GetValue(target, null);
        }

        private static void SetRequiredPublicProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                throw new MissingMemberException(target.GetType().FullName, name);
            property.SetValue(target, value, null);
        }

        private static object InvokeRequiredPublicMethod(object target, string name, params object[] arguments)
        {
            Type[] argumentTypes = new Type[arguments == null ? 0 : arguments.Length];
            for (int i = 0; i < argumentTypes.Length; i++) argumentTypes[i] = arguments[i].GetType();
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public,
                null, argumentTypes, null);
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, arguments);
        }

        private static void RecordFailure(Exception error)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
                string path = Path.Combine(AppPaths.DiagnosticsDirectory,
                    "desktop-failure-" + DateTime.Now.ToString("yyyyMMdd-HHmmss",System.Globalization.CultureInfo.InvariantCulture) + ".log");
                File.WriteAllText(path, DateTime.Now.ToString("O",System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine + error, Encoding.UTF8);
            }
            catch
            {
                // Error reporting must not trigger a second application failure.
            }
        }
    }
}
