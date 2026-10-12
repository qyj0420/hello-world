using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm : Form
    {
        private const uint ScClose = 0xF060;
        private const uint MfByCommand = 0;
        private const uint MfEnabled = 0;
        private const uint MfGrayed = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint EnableMenuItem(IntPtr menu, uint item, uint enable);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawMenuBar(IntPtr window);

        private AppSettings settings;
        private readonly ApplicationState state;
        private readonly bool verificationMode;
        private readonly bool pressureStandalone;
        private Button standaloneLoadProjectButton;
        private Button standaloneDebugButton;
        private PressureDebugForm pressureDebugWindow;
        private readonly DeviceAssetCatalog deviceAssetCatalog;
        private readonly string deviceAssetCatalogLoadError;
        private ModuleHost moduleHost;
        private HardwareStatusAdapter hardwareAdapter;
        private CompetitionDefectLauncher competitionDefectLauncher;
        private readonly Dictionary<string, ModuleTile> tiles = new Dictionary<string, ModuleTile>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> tileVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> tileAvailability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private TableLayoutPanel rootLayout;
        private Panel headerPanel;
        private Panel commandBarPanel;
        private Panel titleArea;
        private FlowLayoutPanel headerActions;
        private FlowLayoutPanel commandActions;
        private FlowLayoutPanel commandStatuses;
        private Panel projectStatusCard;
        private Panel deviceStatusCard;
        private PictureBox brandLogo;
        private Label productTitle;
        private Label productSubtitle;
        private Button settingsButton;
        private Button helpButton;
        private Button backButton;
        private Button openProjectButton;
        private Button refreshDeviceButton;
        private Label projectStatus;
        private Label projectDetail;
        private Label deviceStatus;
        private Label deviceDetail;
        private Panel projectDot;
        private Panel deviceDot;
        private ToolTip deviceSetupTip;
        private Panel contentPanel;
        private Panel homePanel;
        private Panel welcomePanel;
        private FlowLayoutPanel tilePanel;
        private Panel modulePanel;
        private Panel modulePageHeader;
        private Label moduleTitle;
        private Label moduleContext;
        private readonly ProjectHeaderIdentity projectHeaderIdentity = new ProjectHeaderIdentity();
        private Label welcomeTitle;
        private Label welcomeSubtitle;
        private Label footerLabel;
        private Control activeModule;
        private OfflineSimulationBridge offlineSimulationBridge;
        private bool activeTestSimulationMode;
        private Timer devicePollTimer;
        private bool hardwareRefreshInProgress;
        private Action<HardwareStatusSnapshot> pendingHardwareNavigation;
        private bool settingsDialogActive;
        private bool responsiveLayoutUpdating;
        private bool tileLayoutUpdating;
        private bool compactDisplayChrome;
        private bool driverPromptShown;
        private bool closeButtonDisabled;
        private long navigationRequestSequence;
        private long hardwareRefreshGeneration;
        private long hardwareRefreshRequestEpoch;

        public MainForm(AppSettings value)
            : this(value, new ApplicationState(), false)
        {
        }

        protected override bool ShowWithoutActivation { get { return verificationMode || base.ShowWithoutActivation; } }

        internal MainForm(AppSettings value, ApplicationState applicationState, bool suppressExternalChecks)
            : this(value, applicationState, suppressExternalChecks, false) { }

        internal MainForm(AppSettings value, ApplicationState applicationState, bool suppressExternalChecks, bool standalonePressure)
        {
            settings = value ?? AppSettings.Load();
            state = applicationState ?? new ApplicationState();
            verificationMode = suppressExternalChecks;
            pressureStandalone = standalonePressure;
            deviceAssetCatalog = DeviceAssetCatalog.TryLoadDefault(out deviceAssetCatalogLoadError);

            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = AppTheme.Canvas;
            ClientSize = new Size(1260, 790);
            MinimumSize = new Size(1040, 680);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AppTheme.ApplyDialogIcon(this);

            BuildInterface();
            BuildTiles();
            Load += delegate { RestoreWindowLayout(); };
            ResizeEnd += delegate { SaveWindowLayout(); };
            ResponsiveFormLayout.Attach(this, new Size(1100, 720));
            Resize += delegate { RequestResponsiveChrome(); };
            headerPanel.SizeChanged += delegate { RequestResponsiveChrome(); };
            commandActions.Parent.SizeChanged += delegate { RequestResponsiveChrome(); };
            // A DPI scale may only change RowStyles while child resize is
            // suspended. Recheck the cached shell once that table lays out.
            rootLayout.Layout += delegate { QueueResponsiveChrome(); };
            DpiChanged += delegate { QueueResponsiveChrome(); };
            FormClosing += MainFormClosing;
            HandleCreated += delegate { UpdateCloseButton(); QueueResponsiveChrome(); };
            HandleDestroyed += delegate { chromeLayoutQueued = false; };
            ApplyResponsiveChrome();
            state.Changed += StateChanged;
            RecreateIntegrationServices();
            ApplyLanguageAndBranding();
            ApplyState();
            InitializeStandalonePressureChrome();

            devicePollTimer = new Timer { Interval = 1000 };
            devicePollTimer.Tick += delegate
            {
                if (!verificationMode) PollHomeDeviceConnection();
            };

            Shown += delegate
            {
                if (verificationMode) return;
                RecordOperation(OperationAuditAction.ApplicationStart, AuditOutcome.Succeeded);
                if (IsHardwareEnabled)
                {
                    BeginHardwareRefresh(true, true);
                    devicePollTimer.Start();
                }
            };
            Activated += MainFormActivated;
            FormClosed += MainFormClosed;
        }

        private bool IsChinese
        {
            get { return AppLanguages.IsChinese(settings.LanguageCode); }
        }

        private bool IsHardwareEnabled
        {
            get { return settings.Hardware != null && settings.Hardware.Enabled; }
        }

        private void BuildInterface()
        {
            rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = AppTheme.Canvas
            };
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));

            Panel header = BuildHeader();
            Panel commandBar = BuildCommandBar();
            contentPanel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Canvas };
            footerLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(233, 240, 246),
                ForeColor = AppTheme.Muted,
                Font = AppTheme.Font(8.2f, FontStyle.Regular),
                AutoEllipsis = true
            };

            homePanel = BuildHomePanel();
            modulePanel = BuildModulePanel();
            contentPanel.Controls.Add(modulePanel);
            contentPanel.Controls.Add(homePanel);

            rootLayout.Controls.Add(header, 0, 0);
            rootLayout.Controls.Add(commandBar, 0, 1);
            rootLayout.Controls.Add(contentPanel, 0, 2);
            rootLayout.Controls.Add(footerLabel, 0, 3);
            Controls.Add(rootLayout);
        }

        private Panel BuildHeader()
        {
            headerPanel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface, Padding = new Padding(24, 12, 22, 10) };
            Panel accentLine = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = AppTheme.Accent };

            brandLogo = new PictureBox
            {
                Dock = DockStyle.Left,
                Width = 295,
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = Padding.Empty
            };

            titleArea = new Panel { Dock = DockStyle.Left, Width = 390, Padding = new Padding(22, 4, 0, 0) };
            productTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 35,
                ForeColor = AppTheme.Navy,
                Font = AppTheme.Font(15.5f, FontStyle.Bold),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.BottomLeft
            };
            productSubtitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 25,
                ForeColor = AppTheme.Muted,
                Font = AppTheme.Font(8.8f, FontStyle.Regular),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            titleArea.Controls.Add(productSubtitle);
            titleArea.Controls.Add(productTitle);

            headerActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 230,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 15, 0, 0),
                BackColor = AppTheme.Surface
            };
            helpButton = CreateHeaderButton();
            helpButton.Click += ShowHelp;
            settingsButton = CreateHeaderButton();
            settingsButton.Click += delegate
            {
                if (activeModule == null) ShowSettings(settingsButton, EventArgs.Empty);
                else ShowHome();
            };
            headerActions.Controls.Add(helpButton);
            headerActions.Controls.Add(settingsButton);

            headerPanel.Controls.Add(headerActions);
            headerPanel.Controls.Add(titleArea);
            headerPanel.Controls.Add(brandLogo);
            headerPanel.Controls.Add(accentLine);
            return headerPanel;
        }

        private static Button CreateHeaderButton()
        {
            Button button = new HVX.Presentation.R2RoundedButton
            {
                Width = 102,
                Height = 40,
                Margin = new Padding(7, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = AppTheme.Ink,
                Cursor = Cursors.Hand,
                Font = AppTheme.Font(9.5f, FontStyle.Regular)
            };
            button.FlatAppearance.BorderColor = AppTheme.Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(241, 249, 251);
            return button;
        }

        private Panel BuildCommandBar()
        {
            // The measured absolute table row owns this height. AutoSize can
            // grow a dock-filled panel beyond that row after minimize/restore.
            commandBarPanel = new Panel { Dock = DockStyle.Fill, AutoSize = false,
                BackColor = Color.FromArgb(234, 241, 247), Padding = new Padding(22, 11, 22, 9) };
            commandActions = new FlowLayoutPanel
            {
                Dock = DockStyle.None,
                Width = 430,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent
            };
            backButton = AppTheme.Button(String.Empty, false);
            backButton.Width = 128;
            backButton.Visible = false;
            backButton.Click += delegate { ShowHome(); };
            openProjectButton = AppTheme.Button(String.Empty, true);
            openProjectButton.Width = 128;
            openProjectButton.Click += ChooseProject;
            refreshDeviceButton = AppTheme.Button(String.Empty, false);
            refreshDeviceButton.Width = 128;
            refreshDeviceButton.Click += delegate
            {
                ManageDetectionDevices();
            };
            commandActions.Controls.Add(backButton);
            commandActions.Controls.Add(openProjectButton);
            commandActions.Controls.Add(refreshDeviceButton);

            commandStatuses = new FlowLayoutPanel
            {
                Dock = DockStyle.None,
                Width = 590,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.Transparent
            };
            commandStatuses.Controls.Add(BuildStatusCard(false));
            commandStatuses.Controls.Add(BuildStatusCard(true));
            commandBarPanel.Controls.Add(commandStatuses);
            commandBarPanel.Controls.Add(commandActions);
            return commandBarPanel;
        }

        private Panel BuildStatusCard(bool project)
        {
            Panel card = new Panel
            {
                Width = 275,
                Height = 56,
                BackColor = Color.White,
                Margin = new Padding(8, 0, 0, 0),
                Padding = new Padding(11, 5, 9, 4)
            };
            Panel dot = new Panel { Left = 11, Top = 16, Width = 10, Height = 10, BackColor = AppTheme.Muted };
            Label primary = new Label
            {
                Left = 31,
                Top = 4,
                Width = 230,
                Height = 22,
                Font = AppTheme.Font(8.8f, FontStyle.Bold),
                ForeColor = AppTheme.Ink,
                AutoEllipsis = true
            };
            Label detail = new Label
            {
                Left = 31,
                Top = 25,
                Width = 230,
                Height = 26,
                Font = AppTheme.Font(7.8f, FontStyle.Regular),
                ForeColor = AppTheme.Muted,
                AutoEllipsis = true
            };
            card.Controls.Add(dot);
            card.Controls.Add(primary);
            card.Controls.Add(detail);
            if (project)
            {
                projectStatusCard = card;
                projectDot = dot;
                projectStatus = primary;
                projectDetail = detail;
                AddProjectReleaseButton(card);
            }
            else
            {
                deviceStatusCard = card;
                deviceDot = dot;
                deviceStatus = primary;
                deviceDetail = detail;
                deviceSetupTip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 450, ReshowDelay = 100 };
                AddDeviceStatusActions(card);
                card.Click += OpenDeviceParameterSettings;
                dot.Click += OpenDeviceParameterSettings;
                primary.Click += OpenDeviceParameterSettings;
                detail.Click += OpenDeviceParameterSettings;
            }
            return card;
        }

        private void ApplyResponsiveChrome()
        {
            if (WindowState == FormWindowState.Minimized)
            { chromeRestorePending = true; lastChromeMetricsKey = null; return; }
            if (headerPanel == null || titleArea == null || headerActions == null ||
                commandActions == null || commandStatuses == null || commandBarPanel == null ||
                responsiveLayoutUpdating || WindowState == FormWindowState.Minimized ||
                ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            string metricsKey = ChromeMetricsKey();
            if (String.Equals(metricsKey, lastChromeMetricsKey, StringComparison.Ordinal) &&
                !ChromeAbsoluteRowsUnsettled()) return;
            responsiveLayoutUpdating = true;
            try
            {
            bool rootRowsChanged = false;
            using (BeginChromeLayout())
            {
            bool onModule = activeModule != null;
            if (ClientSize.Height > 820) compactDisplayChrome = false;
            else if (ClientSize.Height <= 760 && (DeviceDpi >= 120 ||
                settingsButton.Height > 45 || compactDisplayChrome)) compactDisplayChrome = true;
            bool compactMetrics = onModule || compactDisplayChrome;
            Func<int, int> unit = pixels => compactMetrics ? pixels : ScaleLogical(pixels);
            // Device refresh and the two status cards belong to the home screen.
            // Every module keeps the common header and its project/mode context.
            commandBarPanel.Visible = !onModule;
            // Font metrics are physical pixels at the active DPI. Fixed 31/21 px
            // sub-page rows clip the brand at 150% even on a maximized window.
            int brandTitleHeight = Math.Max(onModule ? 31 : unit(35),
                TextRenderer.MeasureText("Ag中", productTitle.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 2);
            int brandSubtitleHeight = Math.Max(onModule ? 21 : unit(25),
                TextRenderer.MeasureText("Ag中", productSubtitle.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 2);
            int headerHeight = Math.Max(onModule ? 72 : unit(92),
                brandTitleHeight + (onModule || !compactDisplayChrome ? brandSubtitleHeight : 0) + headerPanel.Margin.Vertical +
                (onModule ? 11 : unit(26)) + ScaleLogical(3) + 2);
            if (Math.Abs(rootLayout.RowStyles[0].Height - headerHeight) > 1)
            {
                rootLayout.RowStyles[0].Height = headerHeight;
                rootRowsChanged = true;
            }
            int footerHeight = onModule ? 24 : unit(32);
            if (Math.Abs(rootLayout.RowStyles[3].Height - footerHeight) > 1)
            {
                rootLayout.RowStyles[3].Height = footerHeight;
                rootRowsChanged = true;
            }
            headerPanel.Padding = onModule ? new Padding(18, 6, 18, 5) :
                new Padding(unit(24), unit(12), unit(22), unit(10));
            headerActions.Padding = onModule ? new Padding(0, 8, 0, 0) :
                new Padding(0, unit(15), 0, 0);
            settingsButton.Height = onModule ? 38 : unit(40);
            helpButton.Height = settingsButton.Height;
            productTitle.Height = brandTitleHeight;
            productSubtitle.Height = brandSubtitleHeight;
            titleArea.Padding = onModule ? new Padding(15, 0, 0, 0) :
                new Padding(unit(22), unit(4), 0, 0);
            if (modulePanel != null)
                modulePanel.Padding = onModule ? new Padding(8, 0, 8, 6) :
                    new Padding(unit(24), 0, unit(24), unit(22));
            if (modulePageHeader != null)
            {
                modulePageHeader.Height = onModule
                    ? Math.Max(Math.Max(38, TextRenderer.MeasureText("Ag中", moduleContext.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height * 2 + 8), TextRenderer.MeasureText("Ag中", moduleTitle.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 8)
                    : unit(56);
                modulePageHeader.Padding = onModule ? new Padding(5, 3, 5, 3) :
                    new Padding(5, unit(11), 5, unit(5));
            }
            if (moduleContext != null)
                moduleContext.Width = Math.Min(720, Math.Max(140, modulePanel.ClientSize.Width * 2 / 3));

            int headerWidth = Math.Max(1, headerPanel.DisplayRectangle.Width);
            bool compactHeader = headerWidth < unit(930);
            int headerActionWidth;
            if (compactHeader)
            {
                headerActionWidth = Math.Min(unit(230), Math.Max(unit(190), headerWidth / 3));
                brandLogo.Width = Math.Min(unit(295),
                    Math.Max(unit(150), (headerWidth - headerActionWidth) * 2 / 5));
                titleArea.Width = Math.Max(0, headerWidth - headerActionWidth - brandLogo.Width);
                productSubtitle.Visible = !compactDisplayChrome && titleArea.Width >= unit(240);
                productTitle.Visible = titleArea.Width >= unit(120);
            }
            else
            {
                headerActionWidth = unit(230);
                brandLogo.Width = unit(295);
                titleArea.Width = unit(390);
                productTitle.Visible = true;
                productSubtitle.Visible = !compactDisplayChrome;
            }
            int headerButtonWidth = Math.Max(unit(74),
                Math.Min(unit(102), (headerActionWidth - unit(14)) / 2));
            headerButtonWidth = Math.Max(headerButtonWidth, Math.Max(ButtonTextWidth(settingsButton), ButtonTextWidth(helpButton)));
            headerActions.Width = Math.Max(headerActionWidth, headerButtonWidth * (pressureStandalone ? 4 : 2) + unit(32));
            brandLogo.Visible = brandLogo.Image != null;
            if (brandLogo.Image == null)
            {
                // Hidden controls are not docked again by WinForms. Do not keep
                // their pre-scale height outside the compact header viewport.
                brandLogo.Dock = DockStyle.None;
                brandLogo.Bounds = new Rectangle(headerPanel.Padding.Left,
                    headerPanel.Padding.Top, 0, 0);
                titleArea.Padding = new Padding(0, titleArea.Padding.Top, 0, titleArea.Padding.Bottom);
            }
            else brandLogo.Dock = DockStyle.Left;
            titleArea.Width = Math.Max(0, Math.Min(titleArea.Width,
                headerWidth - headerActions.Width - brandLogo.Width));
            if (standaloneLoadProjectButton != null) { standaloneLoadProjectButton.Width = headerButtonWidth; standaloneLoadProjectButton.Height = settingsButton.Height; }
            if (standaloneDebugButton != null) { standaloneDebugButton.Width = headerButtonWidth; standaloneDebugButton.Height = settingsButton.Height; }
            settingsButton.Width = headerButtonWidth;
            helpButton.Width = headerButtonWidth;

            commandBarPanel.Padding = onModule ? new Padding(16, 6, 16, 5) :
                new Padding(unit(22), unit(11), unit(22), unit(9));
            int left = commandBarPanel.Padding.Left;
            int top = commandBarPanel.Padding.Top;
            int available = Math.Max(1, commandBarPanel.ClientSize.Width - commandBarPanel.Padding.Horizontal);
            int measuredActionsWidth = 0;
            foreach (Button button in new[] { backButton, openProjectButton, refreshDeviceButton })
                if (button.Visible) measuredActionsWidth += ButtonTextWidth(button) + unit(10);
            int minimumProjectCardWidth = MinimumHomeStatusCardWidth(projectStatus, releaseProjectButton, false);
            int minimumDeviceCardWidth = MinimumHomeStatusCardWidth(deviceStatus, disconnectDeviceButton, true);
            int statusCardMargins = projectStatusCard.Margin.Horizontal + deviceStatusCard.Margin.Horizontal;
            int minimumHomeStatusesWidth = minimumProjectCardWidth + minimumDeviceCardWidth + statusCardMargins;
            // Full-height card actions need their own width in addition to the
            // title. Stack the two command groups before either title is clipped.
            bool stacked = !onModule && (available < unit(1080) ||
                available < Math.Max(unit(400), measuredActionsWidth) + unit(8) + minimumHomeStatusesWidth);
            int actionsHeight = onModule ? 38 : unit(40);
            int statusesHeight = onModule ? 48 : unit(56);
            if (!onModule) statusesHeight = Math.Max(statusesHeight,
                Math.Max(projectStatus.Font.Height + projectDetail.Font.Height, deviceStatus.Font.Height + deviceDetail.Font.Height) + unit(15));
            int rowHeight = onModule ? 65 : commandBarPanel.Padding.Vertical +
                (stacked ? actionsHeight + unit(5) + statusesHeight : Math.Max(actionsHeight + unit(8), statusesHeight));
            commandBarPanel.MinimumSize = new Size(0, rowHeight);
            // A dock-filled AutoSize panel can exceed its TableLayout auto row
            // at high DPI after its child bounds change. Reserve the measured
            // row explicitly so the home content cannot paint over the cards.
            float commandRowHeight = onModule ? 0 : rowHeight + commandBarPanel.Margin.Vertical;
            if (rootLayout.RowStyles[1].SizeType != SizeType.Absolute ||
                rootLayout.RowStyles[1].Height != commandRowHeight)
            {
                rootLayout.RowStyles[1].SizeType = SizeType.Absolute;
                rootLayout.RowStyles[1].Height = commandRowHeight;
                rootRowsChanged = true;
            }
            if (stacked)
            {
                commandActions.Bounds = new Rectangle(left, top, available, actionsHeight);
                commandStatuses.Bounds = new Rectangle(left, top + actionsHeight + unit(5), available, statusesHeight);
            }
            else
            {
                int actionsWidth = onModule ? Math.Min(300, Math.Max(170, available * 25 / 100)) :
                    Math.Min(unit(400), Math.Max(unit(280), available * 37 / 100));
                actionsWidth = Math.Max(actionsWidth, measuredActionsWidth);
                commandActions.Bounds = new Rectangle(left, top + unit(8), actionsWidth, actionsHeight);
                commandStatuses.Bounds = new Rectangle(left + actionsWidth + unit(8), top,
                    Math.Max(1, available - actionsWidth - unit(8)), statusesHeight);
            }

            int actionButtonWidth = Math.Max(unit(86),
                Math.Min(unit(128), (commandActions.ClientSize.Width - unit(18)) / 3));
            int actionButtonHeight = onModule ? 34 : unit(38);
            foreach (Button button in new[] { backButton, openProjectButton, refreshDeviceButton })
            {
                button.Height = actionButtonHeight;
                button.Margin = compactMetrics ? new Padding(4, 0, 4, 0) :
                    new Padding(unit(4));
            }
            backButton.Width = Math.Max(actionButtonWidth, ButtonTextWidth(backButton));
            openProjectButton.Width = Math.Max(actionButtonWidth, ButtonTextWidth(openProjectButton));
            refreshDeviceButton.Width = Math.Max(actionButtonWidth, ButtonTextWidth(refreshDeviceButton));

            int cardWidth = Math.Max(onModule ? 145 : unit(150),
                (commandStatuses.ClientSize.Width - statusCardMargins) / 2);
            int projectCardWidth = cardWidth, deviceCardWidth = cardWidth;
            if (!onModule)
            {
                int totalCardWidth = Math.Max(1, commandStatuses.ClientSize.Width - statusCardMargins);
                // Different localized titles/actions have different minimums.
                // Do not give the shorter project title unused width while the
                // device title is clipped (especially at native high DPI).
                deviceCardWidth = Math.Max(minimumDeviceCardWidth, totalCardWidth / 2);
                projectCardWidth = totalCardWidth - deviceCardWidth;
                if (projectCardWidth < minimumProjectCardWidth)
                { projectCardWidth = minimumProjectCardWidth; deviceCardWidth = totalCardWidth - projectCardWidth; }
            }
            ResizeStatusCard(projectStatusCard, projectStatus, projectDetail, projectCardWidth, statusesHeight);
            ResizeStatusCard(deviceStatusCard, deviceStatus, deviceDetail, deviceCardWidth, statusesHeight);
            if (activeModule == null) ApplyHomeLayout();
            }
            // A changed auto-sized row needs one final pass after child layout.
            // Status refreshes with identical metrics must not relayout every module.
            bool commandRowUnsettled = ChromeAbsoluteRowsUnsettled();
            if (rootRowsChanged || commandRowUnsettled || chromeRestorePending)
            {
                // Invalidate the table's old strip assignment explicitly after
                // scale/restore. Parameterless PerformLayout can reuse deferred
                // child-layout arguments and leave old row heights untouched.
                rootLayout.PerformLayout(null, "Rows");
            }
            // Native restore can retain a child height larger than its cached
            // table cell even though GetRowHeights already reports the right row.
            // This panel is display-only and its measured row is authoritative.
            if (activeModule == null && commandBarPanel.Height != commandBarPanel.MinimumSize.Height)
                commandBarPanel.Height = commandBarPanel.MinimumSize.Height;
            chromeRestorePending = false;
            if (activeModule == null)
            { homePanel.PerformLayout(); ApplyHomeLayout(); }
            // Cache the metrics actually measured, not the post-layout viewport.
            // Docking may resize a child during ResumeLayout; one settled pass
            // must measure that new width before it is eligible for reuse.
            lastChromeMetricsKey = metricsKey;
            ResponsiveChromeBuildCount++;
            }
            finally { responsiveLayoutUpdating = false; }
            if (!IsDisposed && WindowState != FormWindowState.Minimized &&
                (chromeLayoutRequested || !String.Equals(ChromeMetricsKey(), lastChromeMetricsKey, StringComparison.Ordinal)))
            { chromeLayoutRequested = false; ApplyResponsiveChrome(); }
        }

        private void ResizeStatusCard(Panel card, Label primary, Label detail, int width, int height)
        {
            if (card == null || primary == null || detail == null) return;
            bool onModule = activeModule != null;
            Func<int, int> unit = pixels => compactDisplayChrome ? pixels : ScaleLogical(pixels);
            card.Width = width;
            card.Height = height;
            int textLeft = onModule ? 27 : unit(31);
            int textWidth = Math.Max(48, width - textLeft - 9);
            primary.Left = textLeft;
            primary.Top = onModule ? 2 : unit(4);
            primary.Height = onModule ? 20 : Math.Max(unit(22), primary.Font.Height + 2);
            primary.Width = textWidth;
            detail.Left = textLeft;
            detail.Top = onModule ? 23 : Math.Max(unit(27), primary.Bottom + unit(3));
            detail.Height = Math.Max(onModule ? 18 : unit(20), card.Height - detail.Top - unit(4));
            detail.Width = textWidth;
            detail.Visible = true;
            if (Object.ReferenceEquals(card, projectStatusCard)) ArrangeProjectReleaseButton(card, primary, detail);
            Panel dot = Object.ReferenceEquals(card, projectStatusCard) ? projectDot : deviceDot;
            if (dot != null)
            {
                dot.Left = onModule ? 9 : unit(11);
                dot.Top = (card.Height - dot.Height) / 2;
            }
            if (Object.ReferenceEquals(card, deviceStatusCard)) ArrangeDeviceStatusActions(card, primary, detail);
        }

        private static int ButtonTextWidth(Button button)
        {
            button.AutoEllipsis = false;
            return TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + button.Padding.Horizontal + 24;
        }

        private int ScaleLogical(int pixels)
        {
            double scale = Math.Max(1.0, Math.Max(96, DeviceDpi) / 96.0);
            // DeviceDpi is authoritative in a PerMonitorV2 process. The height
            // ratio also keeps deterministic Scale(...) layout tests honest,
            // where WinForms scales controls without changing DeviceDpi.
            if (settingsButton != null && settingsButton.Height > 0)
                scale = Math.Max(scale, settingsButton.Height / 40.0);
            return (int)Math.Ceiling(pixels * scale);
        }

        private void ApplyHomeLayout()
        {
            if (tileLayoutUpdating || welcomePanel == null || tilePanel == null ||
                welcomeTitle == null || welcomeSubtitle == null || homePanel == null ||
                WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            tileLayoutUpdating = true;
            try
            {
                // The compact chrome uses physical pixels, while text keeps its DPI-aware
                // font. Use the same spacing policy here and measure the actual font so
                // the welcome row does not consume the third row of home tiles at 150%.
                Func<int, int> homeUnit = pixels => compactDisplayChrome ? pixels : ScaleLogical(pixels);
                bool shortHome = compactDisplayChrome && homePanel.ClientSize.Height < 390;
                welcomePanel.Padding = new Padding(homeUnit(48), homeUnit(shortHome ? 6 : 18),
                    homeUnit(38), homeUnit(shortHome ? 4 : 10));
                int textWidth = Math.Max(homeUnit(180),
                    homePanel.ClientSize.Width - welcomePanel.Padding.Horizontal);
                TextFormatFlags textFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                welcomeTitle.Height = Math.Max(homeUnit(35),
                    TextRenderer.MeasureText(welcomeTitle.Text, welcomeTitle.Font,
                        new Size(textWidth, Int32.MaxValue), textFlags).Height + homeUnit(3));
                welcomeSubtitle.Height = Math.Max(homeUnit(29),
                    TextRenderer.MeasureText(welcomeSubtitle.Text, welcomeSubtitle.Font,
                        new Size(textWidth, Int32.MaxValue), textFlags).Height + homeUnit(3));
                welcomePanel.Height = welcomePanel.Padding.Vertical + welcomeTitle.Height + welcomeSubtitle.Height;

                // Width includes an existing scrollbar; ClientSize does not. Start
                // with the scrollbar-free viewport so a previous compact/three-row
                // layout cannot trap a resized large window in three columns.
                int normalViewport = tilePanel.Width - ScaleLogical(68);
                if (normalViewport < ScaleLogical(220)) return;
                int normalMargin = ScaleLogical(20);
                int normalColumns = Math.Max(1, Math.Min(4,
                    (normalViewport + normalMargin) / (ScaleLogical(232) + normalMargin)));
                int visibleTileCount=0;
                foreach(var pair in tiles){bool shown; if(tileVisibility.TryGetValue(pair.Key,out shown)?shown:pair.Key!="CompetitionDefect")visibleTileCount++;}
                int normalRows = (visibleTileCount + normalColumns - 1) / normalColumns;
                // A large home may be slightly shorter than two ideal 174-DIP rows
                // after the DPI-aware header/status rows. That is not a reason to
                // hide every description and collapse cards to 72 physical pixels.
                // Reserve compact cards for genuinely short-window chrome; otherwise
                // share the available height between readable normal cards.
                bool compact = compactDisplayChrome && tilePanel.ClientSize.Height <
                    normalRows * (ScaleLogical(166) + normalMargin) + ScaleLogical(36);
                Padding panelPadding = compact ? new Padding(10, 4, 10, 4) :
                    new Padding(ScaleLogical(38), ScaleLogical(8), ScaleLogical(30), ScaleLogical(28));
                if (tilePanel.Padding != panelPadding) tilePanel.Padding = panelPadding;
                int margin = compact ? 8 : normalMargin;
                int minimumTileWidth = compact ? 190 : ScaleLogical(232);
                int columns = 1, rows = 1, tileWidth = 1, tileHeight = 1;
                bool needsScroll = false;
                for (int pass = 0; pass < 2; pass++)
                {
                    int viewport = tilePanel.Width - tilePanel.Padding.Horizontal -
                        (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0);
                    columns = Math.Max(1, Math.Min(4, (viewport + margin) / (minimumTileWidth + margin)));
                    tileWidth = Math.Max(compact ? 176 : ScaleLogical(220), (viewport - columns * margin) / columns);
                    rows = (visibleTileCount + columns - 1) / columns;
                    // The normal artwork reserves 92 DIP before its description
                    // and 38 after it. Keep 36 DIP for text; smaller cards either
                    // use the explicit compact layout or remain scroll reachable.
                    // The monitor-DPI minimum (166 DIP) can exceed the actual
                    // two-row viewport after the header has been laid out. The
                    // tile artwork and description remain readable at 160 px;
                    // share the available height before enabling a scrollbar.
                    int availableTileHeight = (tilePanel.ClientSize.Height - tilePanel.Padding.Vertical - 2) /
                        Math.Max(1, rows) - margin;
                    // A normal card with a mode badge needs at least 36 pixels
                    // of description below its title, independent of resizing.
                    tileHeight = compact ? 72 : Math.Max(177,
                        Math.Min(ScaleLogical(186), availableTileHeight));
                    needsScroll = rows * (tileHeight + margin) + tilePanel.Padding.Vertical > tilePanel.ClientSize.Height;
                    if (!needsScroll) break;
                }
                foreach (ModuleTile tile in tiles.Values)
                {
                    tile.CompactMode = compact;
                    Padding tileMargin = compact ? new Padding(4) : new Padding(ScaleLogical(10));
                    if (tile.Margin != tileMargin) tile.Margin = tileMargin;
                    Size next = new Size(tileWidth, tileHeight);
                    if (tile.Size != next) tile.Size = next;
                }
                if (tilePanel.AutoScroll != needsScroll) tilePanel.AutoScroll = needsScroll;
                if (!needsScroll && tilePanel.AutoScrollPosition != Point.Empty) tilePanel.AutoScrollPosition = Point.Empty;
                tilePanel.PerformLayout();
                homePanel.PerformLayout();
            }
            finally { tileLayoutUpdating = false; }
        }

        private Panel BuildHomePanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Canvas };
            welcomePanel = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = AppTheme.Canvas,
                Padding = new Padding(48, 18, 38, 10) };
            welcomeTitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 35,
                ForeColor = AppTheme.Navy,
                Font = AppTheme.Font(18f, FontStyle.Bold),
                AutoEllipsis = true
            };
            welcomeSubtitle = new Label
            {
                Dock = DockStyle.Top,
                Height = 29,
                ForeColor = AppTheme.Muted,
                Font = AppTheme.Font(9.5f, FontStyle.Regular),
                AutoEllipsis = true
            };
            welcomePanel.Controls.Add(welcomeSubtitle);
            welcomePanel.Controls.Add(welcomeTitle);

            tilePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(38, 8, 30, 28),
                BackColor = AppTheme.Canvas
            };
            panel.Controls.Add(tilePanel);
            panel.Controls.Add(welcomePanel);
            panel.SizeChanged += delegate { ApplyHomeLayout(); };
            tilePanel.SizeChanged += delegate { ApplyHomeLayout(); };
            return panel;
        }

        private Panel BuildModulePanel()
        {
            Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Canvas, Visible = false, Padding = new Padding(24, 0, 24, 22) };
            modulePageHeader = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = AppTheme.Canvas, Padding = new Padding(5, 11, 5, 5) };
            moduleTitle = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.Navy,
                Font = AppTheme.Font(14f, FontStyle.Bold),
                AutoEllipsis = true
            };
            moduleContext = new Label
            {
                Dock = DockStyle.Right,
                Width = 270,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = AppTheme.Muted,
                Font = AppTheme.Font(8.5f, FontStyle.Regular),
                AutoEllipsis = true
            };
            modulePageHeader.Controls.Add(moduleTitle);
            modulePageHeader.Controls.Add(moduleContext);
            Panel hostSurface = new Panel { Name = "ModuleSurface", Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(1) };
            panel.Controls.Add(hostSurface);
            panel.Controls.Add(modulePageHeader);
            BuildReferenceSimulationStrip(panel);
            return panel;
        }

        private void BuildTiles()
        {
            AddTile("ProjectManagement", "folder", AppTheme.Accent);
            AddTile("ProjectCreation", "plus", Color.FromArgb(41, 151, 111));
            AddTile("DataBrowser", "search", Color.FromArgb(69, 112, 190));
            AddTile("DataAnalysis", "chart", Color.FromArgb(123, 87, 188));
            AddTile("ReportGenerator", "report", Color.FromArgb(202, 127, 36));
            AddTile("PressureTest", "sine", Color.FromArgb(204, 76, 76));
            AddTile("CalibrationTest", "pulse", Color.FromArgb(0, 143, 153));
            AddTile("WithstandTest", "square", Color.FromArgb(69, 112, 190));
            AddTile("CompetitionDefect", "chart", Color.FromArgb(123, 87, 188));
        }

        private void AddTile(string id, string glyph, Color color)
        {
            ModuleTile tile = new ModuleTile(id, glyph) { AccentColor = color };
            tile.Click += delegate { OpenModule(id); };
            tiles.Add(id, tile);
            tilePanel.Controls.Add(tile);
        }

        private void RecreateIntegrationServices()
        {
            // Invalidate every callback issued by the previous adapter before replacing
            // services. Disposal cancels the old adapter's owned process, while generation
            // isolation prevents any already-posted result from changing the new
            // configuration or opening a page under the wrong device state.
            hardwareRefreshGeneration++;
            hardwareRefreshRequestEpoch++;
            hardwareRefreshInProgress = false;
            pendingHardwareNavigation = null;
            hardwareConnectionQueued = false;
            if (refreshDeviceButton != null && !refreshDeviceButton.IsDisposed)
                refreshDeviceButton.Enabled = true;
            RecreateModuleHost();
            IDisposable oldAdapter = (object)hardwareAdapter as IDisposable;
            if (oldAdapter != null) oldAdapter.Dispose();
            hardwareAdapter = new HardwareStatusAdapter(settings);
        }

        private void RecreateModuleHost()
        {
            if(moduleHost!=null&&moduleHost.HasPressureDeviceOwner)
                throw new InvalidOperationException("原设备任务尚未释放，不能替换模块宿主。 / The original device tasks have not released; the module host cannot be replaced.");
            if (moduleHost != null)
            {
                moduleHost.PressureEmergencyStatusChanged -= PressureEmergencyStatusChanged;
                moduleHost.ProjectLoadRequested -= ProjectLoadRequested;
                moduleHost.ProjectExitRequested -= ProjectExitRequested;
                moduleHost.StopProjectManagerPolling();
                IDisposable disposable = (object)moduleHost as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
            // Offline verification must use the candidate's staged modules, not
            // extract an embedded payload into the user's default cache. The
            // normal application path retains embedded integrity verification.
            moduleHost = verificationMode
                ? new ModuleHost(settings, state, null,
                    Path.Combine(Path.GetDirectoryName(typeof(MainForm).Assembly.Location), "modules"))
                : new ModuleHost(settings, state);
            moduleHost.ProjectLoadRequested += ProjectLoadRequested;
            moduleHost.ProjectExitRequested += ProjectExitRequested;
            moduleHost.AuditEnabled = !verificationMode;
            moduleHost.PressureEmergencyStatusChanged += PressureEmergencyStatusChanged;
        }

        private string Localize(string builtInText) { return AppLanguages.Display(settings.LanguageCode, builtInText); }

        private void ApplyLanguageAndBranding()
        {
            string model = String.IsNullOrWhiteSpace(settings.SoftwareModel) ? "HV-DAC Xpert" : settings.SoftwareModel.Trim();
            Text = String.IsNullOrWhiteSpace(settings.WindowTitle)
                ? model + " · " + AppLanguages.Text("AppTitle", settings.LanguageCode) : settings.WindowTitle.Trim();
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(MainForm).Assembly.Location).FileVersion;
            if (!String.IsNullOrWhiteSpace(version)) Text += "  " + version;
            productTitle.Text = model;
            productSubtitle.Text = AppLanguages.Text("AppTitle", settings.LanguageCode);
            settingsButton.Text = AppLanguages.Text("Settings", settings.LanguageCode);
            helpButton.Text = AppLanguages.Text("Help", settings.LanguageCode);
            openProjectButton.Text = AppLanguages.Text("OpenProject", settings.LanguageCode);
            refreshDeviceButton.Text = AppLanguages.Text("RefreshDevice", settings.LanguageCode);
            backButton.Text = AppLanguages.Text("Back", settings.LanguageCode);
            welcomeTitle.Text = Localize(IsChinese ? "工作台" : "Workspace");
            welcomeSubtitle.Text = IsChinese
                ? Localize("选择一个功能开始工作；测试入口会根据项目与指定设备状态自动控制。")
                : Localize("Choose a module. Test access follows the active project and designated-device state.");
            ApplyHomeLayout();
            footerLabel.Text = String.IsNullOrWhiteSpace(settings.CopyrightText)
                ? "(C) 2026. All rights reserved."
                : settings.CopyrightText;

            SetTileText("ProjectManagement", "ProjectManagement",
                IsChinese ? Localize("查找、打开并维护检测项目") : "Find, open and maintain test projects");
            SetTileText("ProjectCreation", "ProjectCreation",
                IsChinese ? Localize("建立电缆与检测任务档案") : "Create cable and test records");
            SetTileText("DataBrowser", "DataBrowser",
                IsChinese ? Localize("查看当前项目的采集数据") : "Review acquisition data in this project");
            SetTileText("DataAnalysis", "DataAnalysis",
                IsChinese ? Localize("运行定位、识别与统一分析") : "Run location, recognition and unified analysis");
            SetTileText("ReportGenerator", "ReportGenerator",
                IsChinese ? Localize("按配置输出 Word 与 PDF 报告") : "Generate configured Word and PDF reports");
            SetTileText("PressureTest", "PressureTest",
                IsChinese ? Localize("加压采集与分析复核") : "Voltage test acquisition and analysis review");
            SetTileText("CalibrationTest", "CalibrationTest",
                IsChinese ? Localize("TDR 测距与多档局放校准") : "TDR ranging and multi-level PD calibration");
            SetTileText("WithstandTest", "WithstandTest",
                IsChinese ? Localize("超低频、直流与护套耐压试验") : "VLF, DC and sheath withstand tests");
            SetTileText("CompetitionDefect", "CompetitionDefect",
                IsChinese ? Localize("基于当前项目分析的谱图、缺陷识别与学习") : "Spectra, recognition and learning from saved project analyses");

            ReplaceLogo();
            ApplyReferenceSimulationLanguage();
            ApplyNavigationChrome();
            ApplyResponsiveChrome();
            ApplyState();
        }

        private void SetTileText(string id, string resourceKey, string description)
        {
            ModuleTile tile;
            if (!tiles.TryGetValue(id, out tile)) return;
            tile.Title = AppLanguages.Text(resourceKey, settings.LanguageCode);
            tile.Description = Localize(description);
            tile.AccessibleName = tile.Title;
            tile.AccessibleDescription = description;
        }

        private void ReplaceLogo() { RequestOemBranding(); }

        private void StateChanged(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ApplyState));
                return;
            }
            ApplyState();
        }

        private void ApplyState()
        {
            if (projectStatus == null || deviceStatus == null) return;
            bool projectLoaded = state.ProjectLoaded;
            HardwareStatusSnapshot hardware = state.Hardware;

            projectDot.BackColor = projectLoaded ? AppTheme.Success : AppTheme.Muted;
            projectStatus.Text = AppLanguages.Text("CurrentProject", settings.LanguageCode);
            projectDetail.Text = projectLoaded
                ? ShortPath(state.ProjectPath, 44)
                : Localize(IsChinese ? "加载有效项目后继续" : "Load a project to continue");
            projectDetail.Tag = state.ProjectPath;
            UpdateProjectReleaseUi();
            UpdateModuleContext();

            // Transport discovery does not hold the vendor SDK handle. Another
            // application may own it even while both USB nodes remain present.
            deviceDot.BackColor = state.DeviceConnected ? AppTheme.Accent :
                (hardware.HardwareEnabled ? AppTheme.Warning : AppTheme.Muted);
            bool deviceParametersNeedReview = !DeviceSetupStatus.IdentityMatches(settings, hardware);
            deviceStatus.Text = AppLanguages.Text("DeviceConnectionStatus", settings.LanguageCode);
            if (hardwareDisconnectInProgress)
                deviceDetail.Text = IsChinese ? Localize("正在断开指定设备并核验…") : "Disconnecting and verifying the designated device…";
            else if (hardwareManuallyDisconnected)
                deviceDetail.Text = DeviceConnectionStatusText(hardware);
            else if (hardwareRefreshInProgress && !hardwareRefreshSilent)
                deviceDetail.Text = state.DeviceConnected
                    ? (IsChinese ? Localize("上次检查已连接 · 正在复核…") : "Connected at last check · Rechecking…")
                    : (IsChinese ? Localize("正在检查指定设备…") : "Checking the designated device…");
            else if (!IsHardwareEnabled)
                deviceDetail.Text = Localize(IsChinese ? "离线模式（未发现或连接设备）" : "Offline mode (no discovery or connection)");
            else if (state.DeviceConnected)
                deviceDetail.Text = IsChinese ? Localize("指定设备已识别 · 占用状态未确认") : "Designated devices detected · Availability unconfirmed";
            else
                deviceDetail.Text = DeviceConnectionStatusText(hardware);
            ApplyPressureEmergencyHomeStatus();
            deviceDetail.Tag = hardware.Diagnostic;
            if (deviceStatusCard != null)
            {
                Cursor cursor = activeModule == null ? Cursors.Hand : Cursors.Default;
                deviceStatusCard.Cursor = cursor;
                deviceStatus.Cursor = cursor;
                deviceDetail.Cursor = cursor;
                deviceDot.Cursor = cursor;
                deviceDetail.ForeColor = deviceParametersNeedReview ? AppTheme.Accent : AppTheme.Muted;
                string tip = deviceDetail.Text + Environment.NewLine + (IsChinese
                    ? Localize("点击连接设备或查看当前设备参数。")
                    : "Click to connect the device or review its parameters.");
                if(state.DeviceConnected)tip+=Environment.NewLine+(IsChinese
                    ? Localize("识别结果不代表设备未被其他程序占用，也不是高压许可。开始采集时以实际设备打开结果为准；进入页面不重复完整识别。")
                    : "Detection does not confirm exclusive availability or high-voltage permission. Acquisition uses the actual device-open result; navigation does not repeat discovery.")+
                    Environment.NewLine+DeviceAssetStatusText(hardware);
                if (deviceSetupTip != null)
                {
                    deviceSetupTip.SetToolTip(deviceStatusCard, tip);
                    deviceSetupTip.SetToolTip(deviceStatus, tip);
                    deviceSetupTip.SetToolTip(deviceDetail, tip);
                    deviceSetupTip.SetToolTip(deviceDot, tip);
                }
            }

            UpdateDeviceStatusActions();
            string projectHint = AppLanguages.Text("RequiresProject", settings.LanguageCode);
            SetTileState("DataBrowser", true, projectLoaded, projectHint);
            SetTileState("DataAnalysis", true, projectLoaded, projectHint);
            SetTileState("ReportGenerator", true, projectLoaded, projectHint);
            SetTileState("CompetitionDefect", settings.EnableCompetitionDefectModule, settings.EnableCompetitionDefectModule && projectLoaded, projectHint);
            if(competitionDefectLauncher!=null)competitionDefectLauncher.UpdateContext(settings,state);
            SetTileState("ProjectManagement", true, true, String.Empty);
            SetTileState("ProjectCreation", true, true, String.Empty);

            bool testVisible = state.AcquisitionModulesVisible;
            bool testEnabled = state.AcquisitionModulesEnabled;
            SetTileState("PressureTest", testVisible, testEnabled, projectHint);
            SetTileState("CalibrationTest", testVisible, testEnabled, projectHint);
            bool withstandConfigured = settings.SupportsCosineVlf || settings.SupportsCombinedVlf;
            SetTileState("WithstandTest", true, withstandConfigured && projectLoaded,
                withstandConfigured ? projectHint : (IsChinese
                    ? Localize("请在设备参数中启用余弦或综合超低频")
                    : "Enable cosine or combined VLF in device settings"));
            SetTestModeBadge("PressureTest", CanOpenLiveCalibration());
            // The connected entry still performs its own acquisition interlocks.
            SetCalibrationModeBadge();
            SetTestModeBadge("WithstandTest", false); // This preview has simulation only.
            UpdateActiveModuleTitle();
            ApplyHomeLayout();
            tilePanel.PerformLayout();

            string activeId = activeModule == null ? String.Empty : activeModule.Tag as string;
            bool activeTest = String.Equals(activeId, "PressureTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(activeId, "CalibrationTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(activeId, "WithstandTest", StringComparison.OrdinalIgnoreCase);
            if (activeTest && !activeTestSimulationMode && !state.DeviceConnected)
                HandleLiveConnectionLoss(activeModule);
            else if (state.DeviceConnected) notifiedDisconnectedModule = null;

            if (!projectLoaded && activeModule != null && IsProjectDependentModule(activeModule.Tag as string))
                ShowHome();
        }

        private static bool IsProjectDependentModule(string id)
        {
            return String.Equals(id, "DataBrowser", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "DataAnalysis", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "CompetitionDefect", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "ReportGenerator", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "PressureTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "CalibrationTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase);
        }

        private void SetTestModeBadge(string id, bool hardwareReady)
        {
            ModuleTile tile;
            if (!tiles.TryGetValue(id, out tile)) return;
            tile.ModeBadge = hardwareReady ? "HV DAC" : (IsChinese ? Localize("仿真") : "SIM");
            tile.ModeBadgeColor = hardwareReady ? AppTheme.Success : AppTheme.Warning;
        }

        private void SetCalibrationModeBadge()
        {
            ModuleTile tile;
            if (!tiles.TryGetValue(ModuleHost.CalibrationTest, out tile)) return;
            bool connected = CanOpenLiveCalibration();
            tile.ModeBadge = connected ? "HV DAC" : (IsChinese ? Localize("仿真") : "SIM");
            tile.ModeBadgeColor = connected ? AppTheme.Accent : AppTheme.Warning;
        }

        private void UpdateActiveModuleTitle()
        {
            if (moduleTitle == null || activeModule == null) return;
            string id = activeModule.Tag as string;
            if (String.IsNullOrWhiteSpace(id)) return;
            moduleTitle.Text = BuildModuleTitle(id);
            UpdateModuleContext();
        }

        private string BuildModuleTitle(string id)
        {
            ModuleTile tile;
            string title = tiles.TryGetValue(id, out tile) ? tile.Title : id;
            if (String.Equals(id, "PressureTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "CalibrationTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase))
            {
                bool simulationMode = activeModule != null &&
                    String.Equals(activeModule.Tag as string, id, StringComparison.OrdinalIgnoreCase)
                        ? activeTestSimulationMode
                        : !state.DeviceConnected;
                title += simulationMode
                    ? (IsChinese ? Localize(" · 仿真") : " · SIMULATION")
                    : String.Equals(id,"CalibrationTest",StringComparison.OrdinalIgnoreCase) ? "" : (IsChinese ? Localize(" · 实机") : " · LIVE");
            }
            return title;
        }

        private void UpdateModuleContext()
        {
            if (moduleContext == null) return;
            string projectName = String.Empty;
            if (state.ProjectLoaded && !String.IsNullOrWhiteSpace(state.ProjectPath))
                projectName = Path.GetFileName(state.ProjectPath.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
            projectHeaderIdentity.Load(state.ProjectLoaded ? state.ProjectPath : String.Empty);
            string prefix = IsChinese ? Localize("项目：") : "Project: ";
            string projectText = HVX.ProjectModel.AnalysisDateText.DisplayProjectName(projectName);
            string start = projectHeaderIdentity.StartName, end = projectHeaderIdentity.EndName;
            string route = String.IsNullOrEmpty(start) && String.IsNullOrEmpty(end) ? String.Empty :
                (String.IsNullOrEmpty(start) ? "—" : start) + " → " + (String.IsNullOrEmpty(end) ? "—" : end);
            moduleContext.Text = String.IsNullOrWhiteSpace(projectName)
                ? (IsChinese ? Localize("未加载项目") : "No project loaded")
                : prefix + (route.Length == 0 ? projectText : route + Environment.NewLine + projectText)
                    + "  ·  " + Localize(projectHeaderIdentity.LengthCaption(!IsChinese));
            moduleContext.AccessibleDescription = moduleContext.Text;
            if (deviceSetupTip != null) deviceSetupTip.SetToolTip(moduleContext, moduleContext.Text);
        }

        private void SetTileState(string id, bool visible, bool enabled, string hint)
        {
            if (pressureStandalone && id != ModuleHost.PressureTest) visible = false;
            ModuleTile tile;
            if (!tiles.TryGetValue(id, out tile)) return;
            tileVisibility[id] = visible;
            tileAvailability[id] = enabled;
            tile.Visible = visible;
            tile.Enabled = enabled;
            tile.DisabledHint = enabled ? String.Empty : hint;
        }

        private async void OpenModule(string id)
        {
            if (settingsDialogActive || settingsWorkflowActive || hardwareDisconnectInProgress || pressureExitInProgress) return;
            if(activeModule!=null&&String.Equals(activeModule.Tag as string,ModuleHost.PressureTest,StringComparison.OrdinalIgnoreCase)||
                moduleHost!=null&&moduleHost.HasPressureDeviceOwner){
                if(activeModule!=null&&!String.Equals(activeModule.Tag as string,ModuleHost.PressureTest,StringComparison.OrdinalIgnoreCase)&&
                    (!CanLeaveActiveAnalysis()||!CanLeaveActiveCalibration()||!CanLeaveProjectEditor()))return;
                if(!await ShowHomeAsync()||IsDisposed||Disposing||settingsDialogActive||settingsWorkflowActive||hardwareDisconnectInProgress)return;
            }
            if (pressureStandalone && id != ModuleHost.PressureTest) return;
            long navigationRequest = ++navigationRequestSequence;
            CancelPendingCalibrationNavigation();
            if(String.Equals(id,"CompetitionDefect",StringComparison.OrdinalIgnoreCase)&&!settings.EnableCompetitionDefectModule)
            {
                ShowRequirement(IsChinese ? "该模块未在厂家维护中启用。" : "This module is disabled in manufacturer maintenance.");
                return;
            }
            if (String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase) &&
                !settings.SupportsCosineVlf && !settings.SupportsCombinedVlf)
            {
                ShowRequirement(IsChinese ? Localize("请先在授权设备参数中启用余弦或综合超低频。")
                    : "Enable cosine or combined VLF in authorized device settings first.");
                return;
            }
            if (IsProjectDependentModule(id))
            {
                string validation;
                if (!RevalidateCurrentProject(out validation))
                {
                    ShowRequirement(validation);
                    return;
                }
            }

            if(String.Equals(id,"CompetitionDefect",StringComparison.OrdinalIgnoreCase))
            {
                try{
                    if(competitionDefectLauncher==null)competitionDefectLauncher=new CompetitionDefectLauncher(this);
                    competitionDefectLauncher.Open(settings,state);
                }catch(Exception error){ShowModuleError(error.Message);}
                return;
            }

            if (String.Equals(id, "PressureTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "CalibrationTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase))
            {
                OpenTestModule(id, navigationRequest);
                return;
            }

            using (BeginModulePresentation())
            {
            Control control;
            string error;
            if (!moduleHost.TryCreateControl(id, out control, out error) || control == null)
            {
                ShowModuleError(error);
                return;
            }

            ShowEmbeddedModule(id, control);
            if (String.Equals(id, "ProjectManagement", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "ProjectCreation", StringComparison.OrdinalIgnoreCase))
                moduleHost.StartProjectManagerPolling();
            }
        }

        private void OpenTestModule(string id, long navigationRequest)
        {
            if (navigationRequest != navigationRequestSequence) return;
            if (pressureStandalone && !CanOpenLiveCalibration())
            {
                ShowRequirement(IsChinese ? "请先连接设备并完成设备参数配置；独立加压测试不会自动切换到仿真。" :
                    "Connect and configure the device first. This test does not switch to simulation automatically.");
                return;
            }

            if (String.Equals(id, ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase))
            {
                if (CanOpenLiveCalibration()) BeginLiveTestNavigation(id,navigationRequest);
                else OpenTestModuleInSimulation(id, DeviceConnectionStatusText(state.Hardware), navigationRequest);
                return;
            }

            if (!state.DeviceConnected || hardwareManuallyDisconnected || hardwareDisconnectInProgress)
            {
                OpenTestModuleInSimulation(id, String.Empty, navigationRequest);
                return;
            }

            if (!String.Equals(id, ModuleHost.CalibrationTest, StringComparison.OrdinalIgnoreCase))
            {
                Control candidate;
                string calibrationCheckError;
                if (!moduleHost.TryCreateControl(id, out candidate, out calibrationCheckError) || candidate == null)
                {
                    ShowModuleError(calibrationCheckError);
                    return;
                }
                if (!CheckCalibrationBeforeTest(id, candidate, false)) return;
            }

            // Re-inspect immediately before a real-hardware page is entered. If the
            // device disappeared, the same page remains available in explicit SIM mode.
            BeginHardwareRefresh(false, true, delegate(HardwareStatusSnapshot snapshot)
            {
                if (navigationRequest != navigationRequestSequence || IsDisposed) return;
                if (snapshot == null || !snapshot.Ready)
                {
                    OpenTestModuleInSimulation(id, DeviceConnectionStatusText(snapshot), navigationRequest);
                    return;
                }
                if (String.Equals(id, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase))
                {
                    OpenTestModuleControl(id, navigationRequest, false);
                    return;
                }
                // This preview has no live request consumer for these embedded
                // controls. Never present a request counter as successful hardware
                // acquisition, and never silently substitute simulated results.
                MessageBox.Show(this,
                    IsChinese
                        ? Localize("本测试版的主界面尚未接通实机采集和加压控制，不能开始真实试验。\r\n\r\n实机联调须在独立调试程序中由专业人员核查后进行。检查仿真界面时，请在主页设置中关闭硬件模式。")
                        : "Live acquisition and high-voltage control are not connected to the main window in this preview. A real test cannot start here.\r\n\r\nUse the separate commissioning tool only under qualified supervision. To review simulated operation, turn off hardware mode in home settings.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        private void OpenTestModuleInSimulation(string id, string hardwareDetail, long navigationRequest)
        {
            if (navigationRequest != navigationRequestSequence || IsDisposed) return;
            if (pressureStandalone)
            {
                ShowRequirement(IsChinese ? "设备尚未就绪，请连接设备并核对设备参数后再进入测试。\r\n" + hardwareDetail :
                    "The device is not ready. Connect it and check its settings before opening the test.\r\n" + hardwareDetail);
                return;
            }
            string message = IsChinese
                ? Localize("当前未连接硬件，将进入仿真模式。\r\n仿真数据不得作为实测结果。")
                : "No hardware is connected. The module will open in simulation mode.\r\nSimulation data must not be used as measured results.";
            if (!String.IsNullOrWhiteSpace(hardwareDetail)) message += "\r\n\r\n" + hardwareDetail.Trim();
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (navigationRequest == navigationRequestSequence && !IsDisposed)
                OpenTestModuleControl(id, navigationRequest, true);
        }

        private void OpenTestModuleControl(string id, long navigationRequest, bool simulationMode)
        {
            if(!verificationMode && !simulationMode && String.Equals(id,ModuleHost.CalibrationTest,StringComparison.OrdinalIgnoreCase))
            {
                OpenLiveCalibrationControlAsync(navigationRequest);
                return;
            }
            OpenTestModuleControlCore(id,navigationRequest,simulationMode);
        }

        private Control OpenTestModuleControlCore(string id, long navigationRequest, bool simulationMode)
        {
            if (navigationRequest != navigationRequestSequence || IsDisposed || Disposing) return null;
            string validation = String.Empty;
            if (!RevalidateCurrentProject(out validation))
            {
                ShowRequirement(validation);
                return null;
            }

            using (BeginModulePresentation())
            {
            Control testControl;
            string moduleError;
            if (!moduleHost.TryCreateControl(id, out testControl, out moduleError) || testControl == null)
            {
                ShowModuleError(moduleError);
                return null;
            }
            if (!CheckCalibrationBeforeTest(id, testControl, simulationMode)) return null;
            if(!verificationMode && !simulationMode && String.Equals(id,ModuleHost.CalibrationTest,StringComparison.OrdinalIgnoreCase))
                RequireCalibrationEntryAsyncMethod(testControl);
            ShowEmbeddedModule(id, testControl, simulationMode);
            return Object.ReferenceEquals(activeModule,testControl)&&!testControl.IsDisposed?testControl:null;
            }
        }

        private void ShowEmbeddedModule(string id, Control control, bool? requestedSimulationMode = null)
        {
            // Async callers must first await ShowHomeAsync. A synchronous
            // continuation is never permission to dispose an unclosed owner.
            if(pressureExitInProgress||activeModule!=null&&String.Equals(activeModule.Tag as string,ModuleHost.PressureTest,StringComparison.OrdinalIgnoreCase))return;
            if (!CanLeaveActivePressure() || !CanLeaveActiveAnalysis() || !CanLeaveActiveCalibration() || !CanLeaveProjectEditor()) return;
            using (BeginModulePresentation())
            {
            moduleHost.StopProjectManagerPolling();
            DisposeActiveModule();
            Panel surface = modulePanel.Controls["ModuleSurface"] as Panel;
            if (surface == null) return;
            using (BeginChromeLayout())
            {
            activeModule = control;
            activeModule.Visible = false;
            activeModule.Tag = id;
            // The analysis workspace needs two readable charts and its action panel.
            MinimumSize = new Size(MinimumSize.Width,
                String.Equals(id, "DataAnalysis", StringComparison.OrdinalIgnoreCase) ? 1200 : 720);
            bool isTestModule = String.Equals(id, "PressureTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "CalibrationTest", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase);
            activeTestSimulationMode = isTestModule &&
                (requestedSimulationMode.HasValue ? requestedSimulationMode.Value : !state.DeviceConnected);
            moduleHost.SetAuditSimulationMode(id, activeTestSimulationMode);
            AuditModule auditModule;
            if (!Enum.TryParse(id, out auditModule)) auditModule = AuditModule.None;
            RecordOperation(OperationAuditAction.ModuleOpen, activeTestSimulationMode ? AuditOutcome.Simulation : AuditOutcome.Succeeded, state.ProjectPath, auditModule);
            PropertyInfo simulationProperty = activeModule.GetType().GetProperty("SimulationMode");
            if (simulationProperty != null && simulationProperty.CanWrite && simulationProperty.PropertyType == typeof(bool))
                simulationProperty.SetValue(activeModule, activeTestSimulationMode, null);
            if (!activeTestSimulationMode && String.Equals(id, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase))
                moduleHost.SetPressureInputFailClosed(activeModule);
            activeModule.Dock = DockStyle.Fill;
            surface.Controls.Clear();
            rootLayout.SuspendLayout();
            try
            {
            moduleTitle.Text = BuildModuleTitle(id);
            UpdateModuleContext();
            homePanel.Visible = false;
            modulePanel.Visible = true;
            modulePanel.BringToFront();
            if (moduleLoadingCover != null) moduleLoadingCover.BringToFront();
            backButton.Visible = false;
            openProjectButton.Visible = false;
            ApplyNavigationChrome();
            ApplyResponsiveChrome();
            }
            finally { rootLayout.ResumeLayout(true); }
            }
            // The shell now has its final size. Attach only after it stops changing.
            modulePanel.PerformLayout();
            surface.PerformLayout();
            activeModule.Bounds = surface.DisplayRectangle;
            surface.Controls.Add(activeModule);
            activeModule.Visible = true;
            activeModule.PerformLayout();
            QueueProjectCreationGuide(id,activeModule);
            // Live calibration entry is awaited by OpenLiveCalibrationControlAsync
            // under the environment handoff lease, never queued independently.
            if (!activeTestSimulationMode && String.Equals(id, ModuleHost.PressureTest, StringComparison.OrdinalIgnoreCase))
                ConfigurePressureInputPage(activeModule);
            if (moduleLoadingCover != null) moduleLoadingCover.BringToFront();

            if (activeTestSimulationMode && !String.Equals(id, "WithstandTest", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    offlineSimulationBridge = new OfflineSimulationBridge(true);
                    offlineSimulationBridge.Start(activeModule, id);
                    BindReferenceSimulationSource();
                }
                catch (Exception error)
                {
                    if (offlineSimulationBridge != null)
                    {
                        try { offlineSimulationBridge.Dispose(); } catch { }
                        offlineSimulationBridge = null;
                    }
                    ShowModuleError((IsChinese ? Localize("无法启动离线仿真：") : "Could not start offline simulation: ") + error.Message);
                    ShowHome();
                }
            }
            }
        }

        private void ShowHome()
        { var pending=ShowHomeAsync(); }

        private bool ShowHomeCore()
        {
            if (!projectReleaseAuthorized && (!CanLeaveActivePressure() || !CanLeaveActiveAnalysis() || !CanLeaveActiveCalibration() || !CanLeaveProjectEditor())) return false;
            using (BeginChromeLayout())
            {
            if (activeModule != null) RecordOperation(OperationAuditAction.ReturnHome, AuditOutcome.Succeeded, state.ProjectPath, AuditModule.Home);
            navigationRequestSequence++;
            CancelPendingCalibrationNavigation();
            moduleHost.StopProjectManagerPolling();
            DisposeActiveModule();
            modulePanel.Visible = false;
            homePanel.Visible = true;
            homePanel.BringToFront();
            MinimumSize = new Size(MinimumSize.Width, 720);
            backButton.Visible = false;
            openProjectButton.Visible = true;
            ApplyNavigationChrome();
            ApplyResponsiveChrome();
            }
            return true;
        }

        private void ApplyNavigationChrome()
        {
            bool onModule = activeModule != null;
            if (settingsButton != null)
            {
                settingsButton.Text = onModule
                    ? (IsChinese ? Localize("返回主页") : "Home")
                    : AppLanguages.Text("Settings", settings.LanguageCode);
                settingsButton.AccessibleDescription = onModule
                    ? (IsChinese ? Localize("返回主界面") : "Return to the home screen")
                    : (IsChinese ? Localize("打开软件设置") : "Open application settings");
            }
            ApplyStandalonePressureText();
            UpdateCloseButton();
        }

        private void UpdateCloseButton()
        {
            closeButtonDisabled = activeModule != null;
            if (!IsHandleCreated) return;
            IntPtr menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return;
            EnableMenuItem(menu, ScClose, MfByCommand | (closeButtonDisabled ? MfGrayed : MfEnabled));
            DrawMenuBar(Handle);
        }

        private bool NativeCloseButtonDisabled()
        {
            if (!IsHandleCreated) return closeButtonDisabled;
            IntPtr menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero) return false;
            uint state = GetMenuState(menu, ScClose, MfByCommand);
            return state != UInt32.MaxValue && (state & MfGrayed) != 0;
        }

        private async void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (hardwareDisconnectInProgress || firstBindingInProgress || detectionSelectionBusy || pressureExitInProgress) { e.Cancel = true; return; }
            if(activeModule!=null&&String.Equals(activeModule.Tag as string,ModuleHost.PressureTest,StringComparison.OrdinalIgnoreCase)||
                moduleHost!=null&&moduleHost.HasPressureDeviceOwner) {
                e.Cancel=true;
                bool closeApplication=e.CloseReason!=CloseReason.UserClosing;
                if(await ShowHomeAsync()&&closeApplication&&!IsDisposed&&!Disposing)Close();
                return;
            }
            if (e.CloseReason != CloseReason.UserClosing || activeModule == null) return;
            e.Cancel = true;
            ShowHome();
        }

        private void DisposeActiveModule()
        {
            UnbindReferenceSimulationSource();
            if (offlineSimulationBridge != null)
            {
                try { offlineSimulationBridge.Dispose(); } catch { }
                offlineSimulationBridge = null;
            }
            activeTestSimulationMode = false;
            if (activeModule == null) return;
            Control old = activeModule;
            activeModule = null;
            if (old.Parent != null) old.Parent.Controls.Remove(old);
            old.Dispose();
        }

        private void ChooseProject(object sender, EventArgs e)
        {
            if(pressureExitInProgress||moduleHost!=null&&moduleHost.HasPressureDeviceOwner)return;
            if (pressureStandalone) { ChooseStandalonePressureProject(); return; }
            navigationRequestSequence++;
            CancelPendingCalibrationNavigation();
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = IsChinese
                    ? Localize("请选择项目目录（顶层须包含 ITEM.in 和 CABLE.in）")
                    : "Select a project folder containing ITEM.in and CABLE.in at its top level";
                dialog.ShowNewFolderButton = false;
                if (state.ProjectLoaded && Directory.Exists(state.ProjectPath)) dialog.SelectedPath = state.ProjectPath;
                else if (!String.IsNullOrWhiteSpace(settings.DataRoot) && Directory.Exists(settings.DataRoot)) dialog.SelectedPath = settings.DataRoot;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                LoadProject(dialog.SelectedPath, true);
            }
        }

        private void ProjectLoadRequested(string path)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ProjectLoadRequested), path);
                return;
            }
            if(pressureExitInProgress||moduleHost!=null&&moduleHost.HasPressureDeviceOwner)return;
            // Navigate only after a validated manager activation or a successful
            // editor save notification. Selection and failed saves publish neither.
            bool returnHome = activeModule != null &&
                (String.Equals(activeModule.Tag as string, ModuleHost.ProjectManagement,
                    StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(activeModule.Tag as string, ModuleHost.ProjectCreation,
                    StringComparison.OrdinalIgnoreCase) &&
                 !String.Equals(LateBound.ReadString(activeModule, "LastSavedProjectOperationCode"),
                    "JOINT_UPDATE", StringComparison.OrdinalIgnoreCase));
            if (LoadProject(path, true) && returnHome) ShowHome();
        }

        private bool LoadProject(string path, bool showError)
        {
            string message;
            if (state.TryLoadProject(path, out message))
            {
                projectHeaderIdentity.Invalidate();
                RecordOperation(OperationAuditAction.ProjectLoaded, AuditOutcome.Succeeded, state.ProjectPath);
                ApplyState();
                return true;
            }
            RecordOperation(OperationAuditAction.ProjectLoadFailed, AuditOutcome.Failed, path);
            if (showError)
                MessageBox.Show(this, message, AppLanguages.Text("OpenProject", settings.LanguageCode),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private bool RevalidateCurrentProject(out string message)
        {
            if (!state.ProjectLoaded)
            {
                message = AppLanguages.Text("RequiresProject", settings.LanguageCode);
                return false;
            }

            string currentPath = state.ProjectPath;
            if (state.TryLoadProject(currentPath, out message)) return true;

            // Only clear the project that was actually revalidated. If another valid
            // project became current meanwhile, it must not be discarded by this check.
            if (String.Equals(currentPath, state.ProjectPath, StringComparison.OrdinalIgnoreCase))
                state.ClearProject();
            return false;
        }

        private void MainFormActivated(object sender, EventArgs e)
        {
            if (!state.ProjectLoaded) return;
            string ignored;
            RevalidateCurrentProject(out ignored);
        }

        private void BeginHardwareRefresh(bool requestConnection, bool initiatedByUserOrStartup,
            Action<HardwareStatusSnapshot> completed = null)
        {
            if (deviceManagerOpening) return;
            // No connector inspection/open/release while a test owns the SDK,
            // including asynchronous cleanup after its page has closed.
            if(pressureExitInProgress||activeModule!=null||moduleHost!=null&&(moduleHost.HasPressureDeviceOwner||moduleHost.HasExternalCalibrationProcess))return;
            if ((verificationMode && RefreshDeviceForTests == null) || !IsHardwareEnabled || settingsDialogActive || detectionSelectionBusy || hardwareManuallyDisconnected || hardwareDisconnectInProgress) return;
            if (hardwareRefreshInProgress)
            {
                // A user connection request must not disappear behind a silent
                // inspect-only heartbeat. The next idle tick consumes it once.
                if (requestConnection) hardwareConnectionQueued = true;
                // Navigation joins the in-flight inspection instead of being lost.
                // Only the latest navigation is relevant; it has its own epoch guard.
                if (completed != null) { pendingHardwareNavigation = completed; hardwareRefreshSilent = false; ApplyState(); }
                return;
            }
            if (hardwareConnectionQueued) { requestConnection = true; initiatedByUserOrStartup = true; }
            hardwareConnectionQueued = false;
            if (initiatedByUserOrStartup) homeConnectionRetryStep = 0;
            NoteHomeConnectionAttempt();
            if (devicePollTimer != null) devicePollTimer.Stop();
            hardwareRefreshInProgress = true;
            hardwareRefreshSilent = !requestConnection && !initiatedByUserOrStartup && completed == null;
            if (initiatedByUserOrStartup) RecordOperation(OperationAuditAction.DeviceCheckRequested, AuditOutcome.Requested);
            if (!hardwareRefreshSilent) { refreshDeviceButton.Enabled = false; ApplyState(); }
            long generation = hardwareRefreshGeneration;
            long requestEpoch = ++hardwareRefreshRequestEpoch;
            HardwareStatusAdapter adapter = hardwareAdapter;
            try
            {
                Func<bool, Action<HardwareStatusSnapshot>, bool> refresh = RefreshDeviceForTests;
                if (refresh == null && adapter != null) refresh = adapter.BeginRefresh;
                bool started = refresh != null && refresh(requestConnection, delegate(HardwareStatusSnapshot snapshot)
                {
                    Action apply = delegate
                    {
                        if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                            !Object.ReferenceEquals(adapter, hardwareAdapter) || IsDisposed)
                            return;
                        try
                        {
                            // A settings dialog edits a cloned snapshot. An in-flight
                            // hardware result must not write parameters behind it.
                            if (settingsDialogActive) return;
                            PublishHardwareInspection(snapshot, hardwareRefreshSilent);
                            if (initiatedByUserOrStartup) RecordOperation(snapshot.Connected ? OperationAuditAction.DeviceCheckCompleted : OperationAuditAction.DeviceCheckFailed,
                                snapshot.Connected ? AuditOutcome.Succeeded : AuditOutcome.Failed);
                            if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                                !Object.ReferenceEquals(adapter, hardwareAdapter) || IsDisposed)
                                return;
                            if (initiatedByUserOrStartup) OfferHardwareConfigurationIfNeeded(snapshot);
                            if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                                !Object.ReferenceEquals(adapter, hardwareAdapter) || IsDisposed)
                                return;
                            Action<HardwareStatusSnapshot> navigation = hardwareConnectionQueued ? null : pendingHardwareNavigation ?? completed;
                            if (!hardwareConnectionQueued) pendingHardwareNavigation = null;
                            else if (completed != null && pendingHardwareNavigation == null) pendingHardwareNavigation = completed;
                            // Clear the UI checking state before displaying any modal
                            // completion dialog. Driver callback release follows on return.
                            hardwareRefreshInProgress = false;
                            if (!hardwareRefreshSilent) ApplyState();
                            if (navigation != null) navigation(snapshot);
                            else if (initiatedByUserOrStartup && !hardwareConnectionQueued) {
                                if (requestConnection) unlistedDeviceNoticeQueued.Remove(DeviceParameterProfiles.Key(snapshot));
                                QueueUnlistedDeviceNotice(snapshot);
                            }
                        }
                        finally
                        {
                            if (generation == hardwareRefreshGeneration && requestEpoch == hardwareRefreshRequestEpoch &&
                                Object.ReferenceEquals(adapter, hardwareAdapter) && !IsDisposed)
                            {
                                hardwareRefreshInProgress = false;
                                if (refreshDeviceButton != null && !refreshDeviceButton.IsDisposed)
                                    refreshDeviceButton.Enabled = true;
                                if (!hardwareRefreshSilent) ApplyState();
                                if (devicePollTimer != null && IsHardwareEnabled && !settingsDialogActive)
                                    devicePollTimer.Start();
                            }
                        }
                    };
                    if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                        !Object.ReferenceEquals(adapter, hardwareAdapter) || IsDisposed)
                        return;
                    if (InvokeRequired)
                    {
                        try { BeginInvoke(apply); } catch { }
                    }
                    else apply();
                });
                if (!started)
                {
                    if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                        !Object.ReferenceEquals(adapter, hardwareAdapter)) return;
                    hardwareRefreshInProgress = false;
                    hardwareConnectionQueued |= requestConnection;
                    refreshDeviceButton.Enabled = true;
                    if (completed != null) pendingHardwareNavigation = completed;
                    ApplyState();
                    if (devicePollTimer != null && IsHardwareEnabled && !settingsDialogActive)
                        devicePollTimer.Start();
                }
            }
            catch (Exception error)
            {
                if (generation != hardwareRefreshGeneration || requestEpoch != hardwareRefreshRequestEpoch ||
                    !Object.ReferenceEquals(adapter, hardwareAdapter)) return;
                HardwareStatusSnapshot failure = new HardwareStatusSnapshot
                {
                    HardwareEnabled = true,
                    StatusCode = "IntegrationError",
                    Message = IsChinese ? Localize("硬件检查失败") : "Hardware check failed",
                    Diagnostic = error.ToString(),
                    CheckedUtc = DateTime.UtcNow
                };
                try
                {
                    state.SetHardware(failure);
                }
                finally
                {
                    if (generation == hardwareRefreshGeneration && requestEpoch == hardwareRefreshRequestEpoch &&
                        Object.ReferenceEquals(adapter, hardwareAdapter) && !IsDisposed)
                    {
                        hardwareRefreshInProgress = false;
                        if (refreshDeviceButton != null && !refreshDeviceButton.IsDisposed)
                            refreshDeviceButton.Enabled = true;
                        pendingHardwareNavigation = null;
                        ApplyState();
                        if (devicePollTimer != null && IsHardwareEnabled && !settingsDialogActive)
                            devicePollTimer.Start();
                    }
                }
            }
        }

        private void OfferHardwareConfigurationIfNeeded(HardwareStatusSnapshot snapshot)
        {
            if (verificationMode || snapshot == null || !snapshot.HardwareEnabled || driverPromptShown)
                return;
            bool configurationRequired = !snapshot.DriverInstalled ||
                String.Equals(snapshot.StatusCode, "MissingDriver", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(snapshot.StatusCode, "MissingComponents", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(snapshot.StatusCode, "NeedsConfiguration", StringComparison.OrdinalIgnoreCase);
            if (!configurationRequired)
                return;
            driverPromptShown = true;
            MessageBox.Show(this,
                IsChinese
                    ? Localize("所需硬件组件或驱动未通过检查。请重新运行本软件的完整安装包修复；不要从旧配置路径启动独立硬件工具。修复后在设备状态页重新检查。")
                    : "A required hardware component or driver did not pass validation. Re-run this application's complete installer to repair it; do not launch a legacy standalone hardware tool. Then recheck in Device Status.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void MatchDeviceAssetAndApply(HardwareStatusSnapshot snapshot)
        {
            if (snapshot == null || settingsDialogActive) return;
            if (LoadSavedDeviceParameters(snapshot)) return;
            if (deviceAssetCatalog == null)
            {
                snapshot.AssetMatchStatus = "CatalogUnavailable";
                snapshot.AssetMatchMessage = IsChinese
                    ? Localize("资产目录不可用，已保留手工参数。")
                    : "Asset catalog unavailable; manual parameters retained.";
                snapshot.Diagnostic = AppendAudit(snapshot.Diagnostic,
                    "AssetMatch=CatalogUnavailable" +
                    (String.IsNullOrWhiteSpace(deviceAssetCatalogLoadError) ? String.Empty : "; CatalogError=LoadFailed"));
                return;
            }

            DeviceAssetMatchResult match = deviceAssetCatalog.Match(snapshot);
            snapshot.AssetMatchStatus = match.Status;
            snapshot.AssetMatchMessage = match.Message;
            snapshot.AssetCatalogVersion = deviceAssetCatalog.CatalogVersion;
            if (!match.IsMatch)
            {
                snapshot.Diagnostic = AppendAudit(snapshot.Diagnostic,
                    "AssetMatch=" + match.Status + "; Catalog=" + deviceAssetCatalog.CatalogVersion);
                return;
            }

            DeviceAssetRecord record = match.Record;
            snapshot.MatchedFactoryNumber = record.FactoryNumber;
            snapshot.MatchedDeviceModel = record.DeviceModel;
            snapshot.MatchedCouplingCapacitanceNanoFarad = record.CouplingCapacitanceNanoFarad;
            if (record.Compensation != null)
            {
                snapshot.MatchedCompensationSelected = record.Compensation.Selected;
                snapshot.MatchedCompensationCapacitanceNanoFarad = record.Compensation.CapacitanceNanoFarad;
                snapshot.MatchedCompensationVoltageKilovolt = record.Compensation.VoltageKilovolt;
            }
            snapshot.MatchedCalibrationCoefficient = record.CalibrationCoefficient;

            snapshot.AssetParametersApplied = false;
            snapshot.AssetMatchMessage = "已找到出厂默认参数，请完成首次设备配置。";
            snapshot.Diagnostic = AppendAudit(snapshot.Diagnostic,
                "AssetMatch=" + snapshot.AssetMatchStatus + "; Factory=" + record.FactoryNumber +
                "; Catalog=" + deviceAssetCatalog.CatalogVersion);
        }

        internal static bool NeedsDeviceParameterReview(HardwareStatusSnapshot hardware)
        {
            if (hardware == null || !hardware.Ready) return false;
            switch (hardware.AssetMatchStatus ?? String.Empty)
            {
                case "Unknown":
                case "Conflict":
                case "Rejected":
                case "InvalidIdentity":
                case "ActualSerialUnavailable":
                case "ApplyFailed":
                case "CatalogUnavailable":
                    return true;
                default:
                    return false;
            }
        }

        private string DeviceConnectionStatusText(HardwareStatusSnapshot hardware)
        {
            string code = hardware == null ? String.Empty : hardware.StatusCode ?? String.Empty;
            switch (code)
            {
                case "Disconnected":
                    return hardwareManuallyDisconnected
                        ? (IsChinese ? Localize("设备已断开 · 自动连接已暂停") : "Disconnected · Automatic connection paused")
                        : (IsChinese ? "设备连接已中断 · 等待已配置设备重新接入" : "Connection lost · Waiting for the configured device");
                case "DisconnectFailed":
                    return IsChinese ? Localize("断开未确认 · 自动连接已暂停，请重试") : "Disconnection unconfirmed · Automatic connection paused; retry";
                case "MissingDriver":
                case "MissingComponents":
                    return IsChinese ? Localize("缺少硬件组件或驱动，请检查设置") : "Hardware component or driver missing; check settings";
                case "NeedsConfiguration":
                    return IsChinese ? Localize("设备尚未配置，请打开设置") : "Device setup is incomplete; open settings";
                case "ExcludedDevice":
                case "IdentityMismatch":
                    return IsChinese ? Localize("设备与配置不一致，请检查设置") : "Device does not match settings; check configuration";
                case "NotReady":
                    return IsChinese ? Localize("设备尚未就绪，请检查连接") : "Device is not ready; check connection";
                case "Busy":
                    return IsChinese ? Localize("设备正在使用，暂时无法检查") : "Device in use; check temporarily unavailable";
                case "PresenceUnknown":
                    return IsChinese ? Localize("设备连接状态无法确认 · 请检查后刷新设备") : "Device presence unconfirmed · Check connection and refresh devices";
                case "StopReleaseUnconfirmed":
                    return Localize(IsChinese ? "停止尚未确认，加压已禁用" : "Stop unconfirmed; high voltage disabled");
                case "ResourceCleanupFailed":
                    return Localize(IsChinese ? "设备收尾未完成，加压已禁用" : "Device cleanup incomplete; high voltage disabled");
                case "SessionEndedAfterFailedRelease":
                case "WorkerSessionExited":
                    return Localize(IsChinese ? "控制会话已结束，请检查后重新连接" : "Control session ended; check before reconnecting");
                case "Offline":
                    return IsChinese ? Localize("尚未连接指定设备") : "Designated device is not connected";
                default:
                    return IsChinese ? Localize("设备检查未完成，请检查连接和设置") : "Device check incomplete; check connection and settings";
            }
        }

        private string DeviceAssetStatusText(HardwareStatusSnapshot hardware)
        {
            if (hardware == null) return IsChinese ? Localize("尚未检查设备") : "Device not checked";
            if (hardware.Ready && DeviceSetupStatus.ActualIdentity(hardware).Length > 0 &&
                (String.Equals(hardware.AssetMatchStatus, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                 String.Equals(hardware.AssetMatchStatus, "CatalogUnavailable", StringComparison.OrdinalIgnoreCase)))
                return DeviceSetupStatus.IdentityMatches(settings, hardware)
                    ? (IsChinese ? Localize("振荡波设备 · 参数已确认") : "DAC device · parameters reviewed")
                    : (IsChinese ? Localize("振荡波设备 · 参数待确认") : "DAC device · parameters need review");
            return DeviceSetupStatus.Summary(settings, hardware, IsChinese);
        }

        private string LegacyDeviceAssetStatusText(HardwareStatusSnapshot hardware)
        {
            string status;
            switch (hardware.AssetMatchStatus ?? String.Empty)
            {
                case "Applied": status = IsChinese ? Localize("设备参数已更新") : "Device parameters updated"; break;
                case "Matched": status = IsChinese ? Localize("设备参数已核对") : "Device parameters verified"; break;
                case "Conflict": status = IsChinese ? Localize("需核对设备参数，点击设置") : "Check device parameters; click to set"; break;
                case "Rejected": status = IsChinese ? Localize("需核对设备参数，点击设置") : "Check device parameters; click to set"; break;
                case "InvalidIdentity": status = IsChinese ? Localize("需核对设备参数，点击设置") : "Check device parameters; click to set"; break;
                case "ActualSerialUnavailable": status = IsChinese ? Localize("需核对设备参数，点击设置") : "Check device parameters; click to set"; break;
                case "Unknown": status = IsChinese ? Localize("需配置设备参数，点击设置") : "Set device parameters"; break;
                case "ApplyFailed": status = IsChinese ? Localize("参数更新失败，点击设置") : "Parameter update failed; click to set"; break;
                case "CatalogUnavailable": status = IsChinese ? Localize("需核对设备参数，点击设置") : "Check device parameters; click to set"; break;
                default:
                    return IsChinese ? Localize("设备已连接") : "Device connected";
            }
            return status;
        }

        private static string AppendAudit(string diagnostic, string audit)
        {
            if (String.IsNullOrWhiteSpace(diagnostic)) return audit ?? String.Empty;
            if (String.IsNullOrWhiteSpace(audit)) return diagnostic;
            return diagnostic.TrimEnd() + "; " + audit;
        }

        private void ShowSettings(object sender, EventArgs e)
        {
            OpenSettings(SettingsSection.General);
        }

        private async void OpenDeviceParameterSettings(object sender, EventArgs e)
        {
            if (activeModule != null) return;
            if (state.Hardware.Ready) await EditDeviceParameters();
            else if (IsHardwareEnabled) ReconnectCurrentDevice();
            else OpenSettings(SettingsSection.SystemAdmin);
        }

        private bool settingsWorkflowActive;
        private async void OpenSettings(SettingsSection initialSection)
        {
            if (hardwareDisconnectInProgress || detectionSelectionBusy || settingsDialogActive || settingsWorkflowActive || pressureExitInProgress) return;
            if (initialSection == SettingsSection.Device && DeviceParameterProfiles.Key(state.Hardware).Length != 0) { await EditDeviceParameters(); return; }
            settingsWorkflowActive = true;
            try
            {
                RecordOperation(OperationAuditAction.SettingsOpened, AuditOutcome.Succeeded, null, AuditModule.Settings);
                navigationRequestSequence++;
                CancelPendingCalibrationNavigation();
                bool saved = false;
                AppSettings pendingDraft = null;
                AppSettings lastBasis = settings.Clone();
                bool? searchResult = null;
                while (!IsDisposed && !Disposing)
                {
                    AppSettings basis = settings.Clone(), requestedDraft = null;
                    lastBasis = basis;
                    settingsDialogActive = true;
                    if (devicePollTimer != null) devicePollTimer.Stop();
                    try
                    {
                        using (SettingsForm dialog = new SettingsForm(pendingDraft ?? basis, initialSection, state.Hardware))
                        {
                            dialog.AuditEnabled = !verificationMode;
                            dialog.DeviceSearchAvailable = activeModule == null && !settings.SessionHardwareForcedOffline && !hardwareRefreshInProgress &&
                                !hardwareDisconnectInProgress && !firstBindingInProgress && !hardwareConnectionQueued && pendingHardwareNavigation == null;
                            if (searchResult.HasValue) dialog.ShowDeviceSearchResult(searchResult.Value);
                            DialogResult result = dialog.ShowDialog(this);
                            if (result == DialogResult.OK && dialog.Settings != null)
                            {
                                settings = dialog.Settings;
                                driverPromptShown = false;
                                saved = true;
                            }
                            else if (result == DialogResult.Retry) requestedDraft = dialog.DeviceSearchDraft;
                        }
                    }
                    finally { settingsDialogActive = false; }
                    if (requestedDraft == null) break;
                    // The modal editor is closed before entering the existing discovery
                    // workflow. Its guards and single-owner hardware transaction remain intact.
                    bool connected = await SearchAndConnectDevices(true);
                    if (IsDisposed || Disposing) return;
                    bool connectionUpdated = connected || DeviceSetupStatus.ParameterHash(basis) != DeviceSetupStatus.ParameterHash(settings) ||
                        basis.Hardware.Enabled != settings.Hardware.Enabled;
                    pendingDraft = DeviceSearchSettingsDraft.Rebase(basis, requestedDraft, settings, connectionUpdated);
                    initialSection = SettingsSection.SystemAdmin;
                    searchResult = connected;
                }
                if (IsDisposed || Disposing) return;
                settingsDialogActive = true;
                await FinishSettingsWorkflow(saved,lastBasis);
            }
            finally { settingsDialogActive = false; settingsWorkflowActive = false; if (!IsDisposed) ApplyState(); }
        }

        private void ShowHelp(object sender, EventArgs e)
        {
            navigationRequestSequence++;
            CancelPendingCalibrationNavigation();
            string moduleId = activeModule == null ? String.Empty : activeModule.Tag as string;
            using (HelpForm dialog = new HelpForm(settings, moduleId)) dialog.ShowDialog(this);
        }

        private void ShowRequirement(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowModuleError(string error)
        {
            new Diagnostics().Error("Module", error ?? "Module operation failed.");
            string message = IsChinese ? Localize("操作未完成。请检查项目文件后重试；如仍失败，请联系维护人员。")
                : "The operation could not be completed. Check the project files and retry. If it still fails, contact support.";
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void MainFormClosed(object sender, FormClosedEventArgs e)
        {
            CancelPendingCalibrationNavigation();
            SaveWindowLayout();
            detectionSelectionCancellation.Cancel();
            detectionSelectionCancellation.Dispose();
            if(competitionDefectLauncher!=null){competitionDefectLauncher.Dispose();competitionDefectLauncher=null;}
            RecordOperation(OperationAuditAction.ApplicationExit, AuditOutcome.Succeeded, state.ProjectPath);
            hardwareRefreshGeneration++;
            hardwareRefreshRequestEpoch++;
            hardwareRefreshInProgress = false;
            if (devicePollTimer != null)
            {
                devicePollTimer.Stop();
                devicePollTimer.Dispose();
            }
            if (deviceSetupTip != null) deviceSetupTip.Dispose();
            state.Changed -= StateChanged;
            DisposeActiveModule();
            if (moduleHost != null)
            {
                moduleHost.ProjectLoadRequested -= ProjectLoadRequested;
                moduleHost.StopProjectManagerPolling();
                IDisposable hostDisposable = (object)moduleHost as IDisposable;
                if (hostDisposable != null) hostDisposable.Dispose();
            }
            IDisposable adapterDisposable = (object)hardwareAdapter as IDisposable;
            if (adapterDisposable != null) adapterDisposable.Dispose();
            Image logo = brandLogo == null ? null : brandLogo.Image;
            if (brandLogo != null) brandLogo.Image = null;
            if (logo != null) logo.Dispose();
            if (!verificationMode) OperationAuditLog.Default.Shutdown(TimeSpan.FromMilliseconds(300));
        }

        internal void RenderToPng(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Preview path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            bool wasVisible = Visible;
            bool oldTaskbar = ShowInTaskbar;
            FormStartPosition oldStart = StartPosition;
            Point oldLocation = Location;
            if (!wasVisible)
            {
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-30000, -30000);
                Show();
            }
            PerformLayout();
            Application.DoEvents();
            // Form.DrawToBitmap includes the non-client frame.  Use the outer
            // dimensions so the footer/copyright row is not clipped from previews.
            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                bitmap.Save(fullPath, ImageFormat.Png);
            }
            if (!wasVisible)
            {
                Hide();
                ShowInTaskbar = oldTaskbar;
                StartPosition = oldStart;
                Location = oldLocation;
            }
        }

        internal string StateSnapshot()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("ProjectLoaded=" + state.ProjectLoaded);
            builder.AppendLine("DeviceConnected=" + state.DeviceConnected);
            builder.AppendLine("DeviceStatus=" + (deviceStatus == null ? String.Empty : deviceStatus.Text));
            builder.AppendLine("DeviceDetail=" + (deviceDetail == null ? String.Empty : deviceDetail.Text));
            builder.AppendLine("ActiveModule=" + (activeModule == null ? String.Empty : activeModule.Tag as string));
            builder.AppendLine("HeaderAction=" + (settingsButton == null ? String.Empty : settingsButton.Text));
            builder.AppendLine("CloseButtonDisabled=" + closeButtonDisabled);
            builder.AppendLine("NativeCloseButtonDisabled=" + NativeCloseButtonDisabled());
            builder.AppendLine("ClientSize=" + ClientSize.Width + "x" + ClientSize.Height);
            string[] ids = { "ProjectManagement", "ProjectCreation", "DataBrowser", "DataAnalysis",
                "ReportGenerator", "PressureTest", "CalibrationTest", "WithstandTest", "CompetitionDefect" };
            foreach (string id in ids)
            {
                bool visible;
                bool enabled;
                if (!tileVisibility.TryGetValue(id, out visible)) visible = tiles[id].Visible;
                if (!tileAvailability.TryGetValue(id, out enabled)) enabled = tiles[id].Enabled;
                string badge = tiles[id].ModeBadge;
                builder.AppendLine(id + ":Visible=" + visible + ";Enabled=" + enabled +
                    (String.IsNullOrWhiteSpace(badge) ? String.Empty : ";Badge=" + badge));
            }
            return builder.ToString();
        }

        private static string ShortPath(string path, int maximum)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length <= maximum) return path ?? String.Empty;
            int tailLength = Math.Max(8, maximum - 4);
            return "…\\" + path.Substring(path.Length - tailLength).TrimStart('\\', '/');
        }
    }
}
