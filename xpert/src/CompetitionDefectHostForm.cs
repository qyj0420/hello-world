using System;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    // Deliberately does not instantiate MainForm, ModuleHost, a hardware adapter or an acquisition worker.
    internal sealed class CompetitionDefectHostForm : Form
    {
        private readonly Func<Control> controlFactory;
        private readonly Label status;
        private readonly Label headerTitle, headerSubtitle, projectCaption;
        private readonly PictureBox headerLogo;
        private readonly Panel projectHeader;
        private readonly ToolTip projectHint;
        private readonly Font titleFont, captionFont, workspaceFont;
        private readonly Panel content;
        private readonly Button settingsButton;
        private readonly string preferencesPath;
        private CompetitionDefectPreferences preferences;
        private string preferencesWarning, currentProject = String.Empty;
        private bool syncConnected = true;
        private readonly System.Windows.Forms.Timer contextTimer;
        private readonly object receiveGate = new object();
        private CompetitionDefectContext pending;
        private Control recognition;
        private NamedPipeClientStream channel;
        private string moduleDirectory, appliedSignature;
        private long appliedRevision, activation;
        private int receivedTick;
        private volatile bool disconnected;

        internal CompetitionDefectHostForm(CompetitionDefectContext initial)
            : this(initial, null, null, CompetitionDefectPreferences.DefaultPath) { }

        // Isolated UI tests inject an inert control, so the test never starts the desktop or touches a device.
        internal CompetitionDefectHostForm(CompetitionDefectContext initial, Func<Control> factory)
            : this(initial, factory, new CompetitionDefectPreferences(), null) { }

        internal CompetitionDefectHostForm(CompetitionDefectContext initial, Func<Control> factory,
            CompetitionDefectPreferences independentPreferences, string independentPreferencesPath)
        {
            controlFactory = factory ?? LoadRecognitionControl;
            preferencesPath = independentPreferencesPath;
            preferences = independentPreferences == null ? CompetitionDefectPreferences.Load(preferencesPath, out preferencesWarning) : independentPreferences.Clone();
            if (!String.IsNullOrEmpty(preferencesWarning))
                new Diagnostics().Error("CompetitionDefectPreferencesRead", preferencesWarning);
            preferences.Validate();
            Text = "HV-DAC Xpert · AI缺陷识别";
            StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1280, 820); MinimumSize = new Size(760, 600);
            WindowState = FormWindowState.Maximized;
            workspaceFont = AppTheme.Font(10f, FontStyle.Regular);
            BackColor = AppTheme.Canvas; Font = workspaceFont;
            AppTheme.ApplyDialogIcon(this);
            AppTheme.FitDialogToWorkingArea(this);
            titleFont = AppTheme.Font(17f, FontStyle.Bold);
            captionFont = AppTheme.Font(10f, FontStyle.Bold);
            projectHint = new ToolTip { AutoPopDelay = 20000, InitialDelay = 350, ShowAlways = true };
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var heading = new TableLayoutPanel { Name = "CompetitionHeading", Dock = DockStyle.Fill, AutoSize = true,
                ColumnCount = 3, RowCount = 1, BackColor = AppTheme.Surface, Padding = new Padding(16, 12, 16, 12), Margin = Padding.Empty };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLogo = new PictureBox { Name = "CompetitionBrand", Image = AppTheme.LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(202, 50), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 24, 0), TabStop = false };
            var titles = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            titles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            titles.RowStyles.Add(new RowStyle(SizeType.AutoSize)); titles.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            headerTitle = new Label { Name = "CompetitionHeadingTitle", Dock = DockStyle.Fill, AutoSize = true,
                Font = titleFont, ForeColor = AppTheme.Navy, Margin = new Padding(0, 0, 0, 3) };
            headerSubtitle = new Label { Name = "CompetitionHeadingSubtitle", Dock = DockStyle.Fill, AutoSize = true,
                ForeColor = AppTheme.Muted, Margin = Padding.Empty };
            titles.Controls.Add(headerTitle, 0, 0); titles.Controls.Add(headerSubtitle, 0, 1);
            heading.Controls.Add(headerLogo, 0, 0); heading.Controls.Add(titles, 1, 0);
            settingsButton = AppTheme.Button("模块设置", false); settingsButton.Name = "CompetitionModuleSettings";
            settingsButton.AutoSize = true; settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settingsButton.Margin = new Padding(12, 2, 0, 0); settingsButton.Click += delegate { OpenPreferences(); };
            heading.Controls.Add(settingsButton, 2, 0);
            projectHeader = new Panel { Name = "CompetitionProjectHeader", Dock = DockStyle.Fill, Height = 40,
                Padding = new Padding(14, 7, 14, 7), BackColor = Color.FromArgb(232, 241, 247), Margin = new Padding(0, 1, 0, 8) };
            projectCaption = new Label { Name = "CompetitionProjectCaption", Dock = DockStyle.Left, AutoSize = false,
                Font = captionFont, ForeColor = AppTheme.Ink, TextAlign = ContentAlignment.MiddleLeft };
            status = new Label { Name = "CompetitionProjectStatus", Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true,
                ForeColor = AppTheme.Muted, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            projectHeader.Controls.Add(status); projectHeader.Controls.Add(projectCaption);
            status.TextChanged += delegate { projectHint.SetToolTip(status, status.Text); status.AccessibleDescription = status.Text; };
            content = new Panel { Name = "CompetitionWorkspace", Dock = DockStyle.Fill, BackColor = AppTheme.Surface, Margin = Padding.Empty };
            layout.Controls.Add(heading, 0, 0); layout.Controls.Add(projectHeader, 0, 1); layout.Controls.Add(content, 0, 2); Controls.Add(layout);
            bool arrangingHeader = false;
            Action fitHeading = delegate
            {
                if (arrangingHeader) return;
                arrangingHeader = true;
                try
                {
                    int width = Math.Max(140, heading.ClientSize.Width - heading.Padding.Horizontal);
                    int inlineWidth = width - headerLogo.Width - headerLogo.Margin.Horizontal - settingsButton.Width - settingsButton.Margin.Horizontal;
                    bool stacked = inlineWidth < Math.Max(340, Font.Height * 17);
                    heading.SuspendLayout();
                    heading.SetColumnSpan(titles, stacked ? 3 : 1);
                    heading.SetCellPosition(titles, new TableLayoutPanelCellPosition(stacked ? 0 : 1, stacked ? 1 : 0));
                    heading.RowCount = stacked ? 2 : 1; heading.RowStyles.Clear();
                    heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    if (stacked) heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    titles.Margin = stacked ? new Padding(0, 8, 0, 0) : Padding.Empty;
                    int titleWidth = stacked ? width : Math.Max(140, inlineWidth);
                    headerTitle.MaximumSize = new Size(titleWidth, 0); headerSubtitle.MaximumSize = new Size(titleWidth, 0);
                    heading.ResumeLayout(true); FitProjectHeader();
                }
                finally { arrangingHeader = false; }
            };
            layout.SizeChanged += delegate { fitHeading(); };
            heading.SizeChanged += delegate { fitHeading(); };
            FontChanged += delegate { fitHeading(); };
            settingsButton.SizeChanged += delegate { fitHeading(); };
            contextTimer = new System.Windows.Forms.Timer { Interval = 250 };
            contextTimer.Tick += delegate
            {
                CompetitionDefectContext next; lock (receiveGate) { next = pending; pending = null; }
                // Apply the last project/clear message BEFORE EOF, so disconnect never revives an old project.
                if (next != null) { bool resumed = !syncConnected; syncConnected = true; ApplyContext(next); if (resumed) RefreshHeader(); }
                if (disconnected || unchecked((uint)(Environment.TickCount - Volatile.Read(ref receivedTick))) > 12000u)
                {
                    MarkDisconnected();
                    if (disconnected) contextTimer.Stop();
                }
            };
            ApplyPreferencesToControl();
            ApplyContext(initial);
        }

        internal void BeginReceive(NamedPipeClientStream pipe)
        {
            if (pipe == null) throw new ArgumentNullException("pipe");
            channel = pipe; Volatile.Write(ref receivedTick, Environment.TickCount); contextTimer.Start();
            Task.Run(delegate
            {
                try
                {
                    while (true)
                    {
                        CompetitionDefectContext context = CompetitionDefectProtocol.Deserialize(CompetitionDefectProtocol.ReadText(pipe));
                        Volatile.Write(ref receivedTick, Environment.TickCount);
                        lock (receiveGate)
                        {
                            if (pending == null || context.Revision > pending.Revision) pending = context;
                        }
                    }
                }
                catch { disconnected = true; }
            });
        }

        internal void ApplyContext(CompetitionDefectContext context)
        {
            if (IsDisposed || Disposing || context == null || context.Protocol != 2 || context.Revision <= appliedRevision) return;
            appliedRevision = context.Revision;
            RefreshHeader();
            string project = String.Empty, error = String.Empty;
            bool hasProject = ProjectValidator.TryValidate(context.ProjectPath, out project, out error);
            if (!hasProject)
            {
                ClearRecognition(); appliedSignature = null; currentProject = String.Empty;
                status.Text = UiLanguage.Text(preferences.LanguageIndex, "请在主程序选择有效项目；旧项目数据已清空。",
                    "Select a valid project in the main application. Old results have been cleared.");
                return;
            }
            string signature = project;
            try
            {
                if (!String.Equals(appliedSignature, signature, StringComparison.OrdinalIgnoreCase))
                {
                    if (recognition == null)
                    {
                        recognition = controlFactory();
                        if (recognition == null || recognition.IsDisposed) throw new InvalidOperationException("The recognition control is unavailable.");
                        // Its internal viewport owns vertical scrolling. A fixed root minimum height
                        // would make Dock.Fill extend below this window at high DPI or large fonts.
                        recognition.MinimumSize = new Size(recognition.MinimumSize.Width, 0);
                        recognition.Dock = DockStyle.Fill; content.Controls.Add(recognition);
                    }
                    // Only a project change comes from the desktop. Analysis preferences belong to this program.
                    recognition.Enabled = false; SetRequired("ProjectPath", String.Empty);
                    ApplyPreferencesToControl();
                    SetRequired("ProjectPath", project);
                    recognition.Enabled = true; appliedSignature = signature;
                }
                currentProject = project; status.Text = project;
                if (context.Activation > activation)
                {
                    activation = context.Activation;
                    WindowState = FormWindowState.Maximized;
                    if (Visible) { BringToFront(); Activate(); }
                }
            }
            catch (Exception exception)
            {
                ClearRecognition(); appliedSignature = null;
                Exception actual = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
                status.Text = UiLanguage.Text(preferences.LanguageIndex,
                    "缺陷识别未能加载。请重新打开；如仍失败，请修复安装或联系维护人员。",
                    "Defect recognition could not be loaded. Reopen it. If the problem persists, repair the installation or contact support.");
                new Diagnostics().Error("CompetitionDefect", "Recognition module could not load the selected project.", actual);
            }
        }

        internal void ApplyPreferences(CompetitionDefectPreferences value, bool persist)
        {
            if (value == null) throw new ArgumentNullException("value");
            var next = value.Clone(); next.Validate();
            if (persist)
            {
                if (String.IsNullOrWhiteSpace(preferencesPath)) throw new InvalidOperationException("No independent preferences path was configured.");
                next.Save(preferencesPath);
            }
            preferences = next; preferencesWarning = String.Empty; ApplyPreferencesToControl();
            if (currentProject.Length == 0) status.Text = UiLanguage.Text(preferences.LanguageIndex,
                "请在主程序选择有效项目；旧项目数据已清空。", "Select a valid project in the main application. Old results have been cleared.");
        }

        private void ApplyPreferencesToControl()
        {
            RefreshHeader();
            if (recognition == null) return;
            SetRequired("LanguageIndex", preferences.LanguageIndex);
            SetRequired("EnableAiFeatures", preferences.EnableAiFeatures);
            string model = DefectModelCatalog.Resolve(preferences.ModelKind,preferences.ModelLibraryPath,preferences.CompetitionModelPath);
            SetRequired("ModelLibraryPath", model ?? String.Empty);
        }

        private void OpenPreferences()
        {
            using (var dialog = new CompetitionDefectPreferencesDialog(preferences))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;
                try { ApplyPreferences(dialog.Result, true); }
                catch (Exception error)
                {
                    new Diagnostics().Error("CompetitionDefectPreferencesSave", "Independent recognition settings could not be saved or applied.", error);
                    MessageBox.Show(this, UiLanguage.Text(preferences.LanguageIndex,
                        "设置未能完成。请重新打开模块设置，核对后再保存；如仍失败，请联系维护人员。",
                        "Settings could not be completed. Reopen Module settings, review them and save again. If the problem persists, contact support."),
                        dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void RefreshHeader()
        {
            int language = preferences.LanguageIndex;
            Text = UiLanguage.Text(language, "HV-DAC Xpert · AI缺陷识别", "HV-DAC Xpert · AI defect recognition");
            headerTitle.Text = UiLanguage.Text(language, "AI缺陷识别", "AI defect recognition");
            headerSubtitle.Text = UiLanguage.Text(language, "局部放电谱图分析  /  人工确认学习", "Partial discharge spectra  /  Human-verified learning");
            if (!syncConnected) headerSubtitle.Text += "\r\n" + UiLanguage.Text(language,
                currentProject.Length > 0 ? "主程序已断开，使用最后同步项目。" : "主程序已断开，当前未加载项目。",
                currentProject.Length > 0 ? "Main application disconnected; using the last synchronized project." : "Main application disconnected; no project is loaded.");
            if (!String.IsNullOrEmpty(preferencesWarning)) headerSubtitle.Text += "\r\n" + UiLanguage.Text(language,
                "设置读取失败，暂用默认设置。请检查模块设置。", "Settings could not be read; defaults are active. Check Module settings.");
            projectCaption.Text = UiLanguage.Text(language, "当前项目", "Current project");
            settingsButton.Text = UiLanguage.Text(language, "模块设置", "Module settings");
            FitProjectHeader();
        }

        internal void MarkDisconnected()
        {
            if (!syncConnected) return;
            syncConnected = false; RefreshHeader();
            // No data or AI state is discarded: this is an independent program, not a child page.
        }

        private void FitProjectHeader()
        {
            projectCaption.Width = TextRenderer.MeasureText(projectCaption.Text, projectCaption.Font,
                Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 18;
            projectHeader.Height = Math.Max(40, Math.Max(projectCaption.Font.Height, status.Font.Height) + projectHeader.Padding.Vertical + 4);
        }

        private void SetRequired(string name, object value)
        {
            PropertyInfo property = recognition.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite) throw new MissingMemberException(recognition.GetType().FullName, name);
            property.SetValue(recognition, value, null);
        }

        private void ClearRecognition()
        {
            if (recognition == null) return;
            recognition.Enabled = false;
            try { SetRequired("ProjectPath", String.Empty); }
            catch { recognition.Dispose(); recognition = null; }
        }

        private Control LoadRecognitionControl()
        {
            moduleDirectory = Path.Combine(EmbeddedModulePayload.ResolveModulesRoot(), "DataAnalysis");
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            Assembly assembly = Assembly.LoadFrom(Path.Combine(moduleDirectory, "HVX.PdReview.V1.0.dll"));
            Type type = assembly.GetType("HVX.PdReview.CompetitionDefectRecognitionControl", true, false);
            if (!typeof(UserControl).IsAssignableFrom(type)) throw new TypeLoadException("Invalid defect recognition control type.");
            return (Control)Activator.CreateInstance(type);
        }

        private Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            if (String.IsNullOrWhiteSpace(moduleDirectory) || args.RequestingAssembly == null) return null;
            string requesting;
            try { requesting = Path.GetFullPath(args.RequestingAssembly.Location); } catch { return null; }
            string prefix = Path.GetFullPath(moduleDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!requesting.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            string simpleName = new AssemblyName(args.Name).Name;
            if (String.IsNullOrWhiteSpace(simpleName) || simpleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string path = Path.GetFullPath(Path.Combine(moduleDirectory, simpleName + ".dll"));
            if (!String.Equals(Path.GetDirectoryName(path), Path.GetFullPath(moduleDirectory), StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            return Assembly.LoadFrom(path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                contextTimer.Stop(); contextTimer.Dispose();
                if (channel != null) { try { channel.Dispose(); } catch { } channel = null; }
                AppDomain.CurrentDomain.AssemblyResolve -= ResolveDependency;
                ClearRecognition();
                projectHint.Dispose();
                Image logo = headerLogo.Image; headerLogo.Image = null; if (logo != null) logo.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) { titleFont.Dispose(); captionFont.Dispose(); workspaceFont.Dispose(); }
        }
    }
}
