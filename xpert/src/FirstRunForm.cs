using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class FirstRunForm : Form
    {
        private readonly AppSettings basis;
        private readonly string savePath;
        private TextBox dataRoot;
        private CheckBox enableHardware;
        private Button saveButton;
        private bool bindingInProgress;
        internal Action BeforeFirstSaveForTests;
        internal Action<string> SaveFailureNoticeForTests;
#if FIRST_RUN_TESTS
        // The isolated smoke build substitutes a synthetic receipt. These
        // bypasses do not exist in the shipped application binary.
        internal Func<AppSettings, Action, Task<ProvisioningActivation>> FirstBindingForTests;
        internal Func<AppSettings, ProvisioningActivation, AppSettings> ApplyBindingForTests;
        internal Func<DateTime> CommitClockForTests;
#endif

        public AppSettings Settings { get; private set; }

        public FirstRunForm()
            : this(new AppSettings())
        {
        }

        public FirstRunForm(AppSettings current)
            : this(current, AppPaths.SettingsFile)
        {
        }

        internal FirstRunForm(AppSettings current, string firstSavePath)
        {
            basis = current == null ? new AppSettings() : current.Clone();
            savePath = String.IsNullOrWhiteSpace(firstSavePath) ? AppPaths.SettingsFile : Path.GetFullPath(firstSavePath);
            Settings = basis.Clone();
            BuildUi();
            dataRoot.Text = basis.DataRoot;
            // First-run remains deliberately offline unless the operator opts in here.
            enableHardware.Checked = false;
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (bindingInProgress) e.Cancel = true;
            };
        }

        private void BuildUi()
        {
            Text = "首次运行设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 470);
            BackColor = AppTheme.Canvas;
            Font = AppTheme.Font(9.0f, FontStyle.Regular);
            AppTheme.ApplyDialogIcon(this);

            Panel banner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 112,
                BackColor = AppTheme.Navy,
                Padding = new Padding(24, 18, 24, 14)
            };
            PictureBox logo = new PictureBox
            {
                Dock = DockStyle.Left,
                Width = 148,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = AppTheme.LoadLogo(),
                BackColor = AppTheme.Navy,
                Margin = Padding.Empty
            };
            Label heading = new Label
            {
                Dock = DockStyle.Fill,
                Text = "欢迎使用振荡波局放检测系统\r\n首次运行将默认采用安全的离线模式",
                ForeColor = Color.White,
                Font = AppTheme.Font(15.0f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 0, 0)
            };
            banner.Controls.Add(heading);
            banner.Controls.Add(logo);
            Controls.Add(banner);

            // A plain scroll viewport owns the scroll bars. The content table must
            // grow vertically, not stretch a percentage row inside its own scroll
            // canvas; that made the final buttons unreachable at large DPI.
            Panel contentViewport = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = AppTheme.Canvas
            };
            TableLayoutPanel content = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(28, 22, 28, 14),
                BackColor = AppTheme.Canvas,
                Margin = Padding.Empty
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label rootLabel = new Label
            {
                Text = "数据根目录",
                AutoSize = true,
                ForeColor = AppTheme.Ink,
                Font = AppTheme.Font(9.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8)
            };
            content.Controls.Add(rootLabel, 0, 0);

            TableLayoutPanel pathRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 0, 18)
            };
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            dataRoot = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 3, 10, 3)
            };
            Button browse = AppTheme.Button("浏览…", false);
            browse.Width = 88;
            browse.Height = 32;
            browse.Dock = DockStyle.Fill;
            browse.Margin = Padding.Empty;
            browse.Click += BrowseClicked;
            pathRow.Controls.Add(dataRoot, 0, 0);
            pathRow.Controls.Add(browse, 1, 0);
            content.Controls.Add(pathRow, 0, 1);

            Panel hardwarePanel = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                BackColor = AppTheme.Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(16),
                Margin = new Padding(0, 0, 0, 12)
            };
            enableHardware = new CheckBox
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Text = "启用硬件模式（可选）",
                ForeColor = AppTheme.Navy,
                Font = AppTheme.Font(10.0f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8)
            };
            Label explanation = new Label
            {
                Dock = DockStyle.Top,
                Height = 76,
                Text = "不勾选时，软件不会发现或连接设备；项目管理、数据浏览、分析和报告等离线功能仍可使用。\r\n" +
                       "勾选后，“保存并继续”会打开一次性绑定界面（需管理员确认），并在本软件再次核验当前设备后才启用自动连接；不会加压或开始测试。",
                ForeColor = AppTheme.Muted,
                Font = AppTheme.Font(9.0f, FontStyle.Regular),
                Padding = new Padding(24, 8, 4, 0)
            };
            hardwarePanel.Controls.Add(explanation);
            hardwarePanel.Controls.Add(enableHardware);
            content.Controls.Add(hardwarePanel, 0, 2);

            Label safety = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(650, 0),
                Text = "首次配置工具或驱动安装界面可能需要管理员权限，并且只会在用户明确操作时打开。",
                ForeColor = AppTheme.Warning,
                Margin = new Padding(0, 4, 0, 0)
            };
            content.Controls.Add(safety, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                Padding = new Padding(28, 8, 28, 14),
                Margin = Padding.Empty
            };
            saveButton = AppTheme.Button("保存并继续", true);
            saveButton.Width = 124;
            saveButton.Click += SaveClicked;
            Button cancel = AppTheme.Button("取消", false);
            cancel.Width = 92;
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(saveButton);
            actions.Controls.Add(cancel);
            contentViewport.Controls.Add(content);
            Controls.Add(actions);
            Controls.Add(contentViewport);
            contentViewport.BringToFront();
            AcceptButton = saveButton;
            CancelButton = cancel;
            bool arranging = false;
            Action fitContent = delegate
            {
                if (arranging || content.ClientSize.Width <= content.Padding.Horizontal) return;
                arranging = true;
                try
                {
                    int available = Math.Max(1, content.ClientSize.Width - content.Padding.Horizontal);
                    safety.MaximumSize = new Size(available, 0);
                    int explanationWidth = Math.Max(1, available - hardwarePanel.Padding.Horizontal - explanation.Padding.Horizontal - 2);
                    explanation.Height = TextRenderer.MeasureText(explanation.Text, explanation.Font,
                        new Size(explanationWidth, Int32.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height + explanation.Padding.Vertical + 4;
                    pathRow.ColumnStyles[1].Width = Math.Max(browse.Width,
                        TextRenderer.MeasureText(browse.Text, browse.Font).Width + 24);
                }
                finally { arranging = false; }
            };
            content.Layout += delegate { fitContent(); };
            contentViewport.SizeChanged += delegate { fitContent(); };
            ResponsiveFormLayout.Attach(this, new Size(600, 420));
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择数据根目录";
                if (Directory.Exists(dataRoot.Text)) dialog.SelectedPath = dataRoot.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) dataRoot.Text = dialog.SelectedPath;
            }
        }

        private async void SaveClicked(object sender, EventArgs e)
        {
            if (bindingInProgress) return;
            try
            {
                ProvisioningActivation activation = null;
                AppSettings candidate = basis.Clone();
                candidate.DataRoot = dataRoot.Text;
                if (candidate.Hardware == null) candidate.Hardware = new HardwareSettings();
                // Never set Enabled merely because a box is checked.
                candidate.Hardware.Enabled = false;
                string validation;
                if (!candidate.TryValidate(out validation))
                {
                    MessageBox.Show(this, validation, "首次运行设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (enableHardware.Checked)
                {
                    Func<AppSettings, Action, Task<ProvisioningActivation>> launchBinding =
                        FirstHardwareProvisioning.RunAsync;
                    Func<AppSettings, ProvisioningActivation, AppSettings> applyBinding =
                        FirstHardwareProvisioning.ApplyToClone;
                    bool simulatedBinding = false;
#if FIRST_RUN_TESTS
                    if (FirstBindingForTests != null && ApplyBindingForTests != null)
                    {
                        launchBinding = FirstBindingForTests;
                        applyBinding = ApplyBindingForTests;
                        simulatedBinding = true;
                    }
#endif
                    if (!simulatedBinding)
                    {
                        string gate = FirstHardwareProvisioning.InstallationGate();
                        if (gate.Length > 0)
                        {
                            if (FirstHardwareProvisioning.CanResumeInstallation() &&
                                MessageBox.Show(this, gate + "\r\n\r\n现在以管理员身份继续安装？完成后请关闭并重新打开软件，再进行首次绑定。",
                                    "首次运行设置", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                                FirstHardwareProvisioning.ResumeInstallation();
                            else if (!FirstHardwareProvisioning.CanResumeInstallation())
                                MessageBox.Show(this, gate, "首次运行设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        if (MessageBox.Show(this,
                            "将打开一次性的硬件绑定窗口，并请求管理员确认。请只选当前系统的 Controller 与 Acquisition 模块。是否继续？",
                            "首次运行设置", MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                    }
                    bindingInProgress = true;
                    Enabled = false;
                    UseWaitCursor = true;
                    try { activation = await launchBinding(candidate,delegate {
                        if (IsDisposed) return;
                        try { BeginInvoke(new Action(delegate {
                            if (!IsDisposed) MessageBox.Show(this,
                                "首次绑定已超过 15 分钟；本次结果不会导入。请关闭仍在运行的旧绑定窗口，软件将保持等待，避免重复 Apply。",
                                "首次运行设置",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                        })); } catch {}
                    }); }
                    finally
                    {
                        bindingInProgress = false;
                        if (!IsDisposed) { Enabled = true; UseWaitCursor = false; }
                    }
                    if (IsDisposed) return;
                    candidate = applyBinding(candidate, activation);
                    if (candidate == null || candidate.Hardware == null || !candidate.Hardware.Enabled)
                        throw new InvalidOperationException("首次绑定结果未启用当前硬件，保持离线。");
                }
                if (File.Exists(savePath))
                    throw new InvalidOperationException("首次配置期间其他程序已创建配置文件；为防止覆盖，本次没有保存。请重新打开软件核对。");
                if (BeforeFirstSaveForTests != null) BeforeFirstSaveForTests();
                Func<DateTime> commitClock = () => DateTime.UtcNow;
#if FIRST_RUN_TESTS
                if (CommitClockForTests != null) commitClock = CommitClockForTests;
#endif
                Func<bool> commitGuard = activation == null ? null :
                    (Func<bool>)(() => ProvisioningImportAdapter.IsCurrentActivation(activation, commitClock()));
                if (!candidate.TrySaveIfUnchanged(savePath,null,true,commitGuard))
                    throw new InvalidOperationException("提交前配置已由其他程序创建；本次没有覆盖，请重新打开软件核对。");
                Settings = candidate;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                new Diagnostics().Error("FirstRun", "Could not save first-run settings.", error);
                const string message="首次运行设置未保存。请检查设置和文件权限后重试。";
                if (SaveFailureNoticeForTests != null) SaveFailureNoticeForTests(message);
                else MessageBox.Show(this, message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
