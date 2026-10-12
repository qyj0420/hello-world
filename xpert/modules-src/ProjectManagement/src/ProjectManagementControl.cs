using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    [ToolboxItem(true)]
    public sealed partial class ProjectManagementControl : UserControl
    {
        readonly TextBox searchBox = new TextBox();
        readonly ComboBox sortBox = new ComboBox();
        readonly InstrumentButton directionButton = new InstrumentButton();
        readonly InstrumentButton clearSearchButton = new InstrumentButton();
        readonly InstrumentButton loadButton = new InstrumentButton();
        readonly InstrumentButton refreshButton = new InstrumentButton();
        readonly InstrumentButton importButton = new InstrumentButton();
        readonly InstrumentButton exportDataButton = new InstrumentButton();
        readonly InstrumentButton exportReportsButton = new InstrumentButton();
        readonly InstrumentButton mergeButton = new InstrumentButton();
        readonly InstrumentButton deleteButton = new InstrumentButton();
        readonly CheckBox selectAll = new CheckBox();
        readonly Label resultBadge = new Label();
        readonly Label status = new Label();
        readonly Label titleLabel = new Label();
        readonly Label editionLabel = new Label();
        readonly Label searchLabel = new Label();
        readonly Label sortLabel = new Label();
        readonly DataGridView grid = new DataGridView();
        TableLayoutPanel layoutShell;
        Control brandHeader;
        bool embeddedMode;
        readonly TableLayoutPanel filterLayout = new TableLayoutPanel();
        readonly FlowLayoutPanel actionFlow = new FlowLayoutPanel();
        readonly HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile ProjectRecord[] projects = new ProjectRecord[0];
        volatile ProjectRecord[] visibleProjects = new ProjectRecord[0];
        volatile ProjectSortField sortField = ProjectSortField.TestTime;
        volatile ProjectSortDirection sortDirection = ProjectSortDirection.Descending;
        volatile bool english;
        volatile int languageIndex;
        volatile bool busy;
        bool rendering;
        bool updatingSortSelection;
        Func<string> statusTextFactory;
        int scanGeneration;
        ScanCompletion pendingScan;
        long loadRequestSequence;
        volatile string rootDirectory = string.Empty;
        volatile string modelName = "HV-DAC Xpert";
        volatile string lastError = string.Empty;
        volatile string lastOperation = string.Empty;
        volatile string lastLoadedProjectPath = string.Empty;
        volatile string selectedProjectPath = string.Empty;
        volatile string searchKeyword = string.Empty;
        volatile string[] selectedProjectPaths = new string[0];
        readonly int ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        int disposeStarted;
        bool raisingLoadRequest;
        string preferredProjectPath = string.Empty;
        bool repairingSearchText;
        bool fittingActionButtons;
        bool fittingFilterColumns;
        bool fittingGridColumns;
        volatile bool exitViOnProjectLoad = true;
        int exitViRequested;
        long exitViRequestSequence;

        public ProjectManagementControl()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = ProjectUiTheme.Font(this, 9.5f);
            BackColor = ProjectUiTheme.Canvas;
            ForeColor = ProjectUiTheme.Ink;
            Size = new Size(1920, 1080);
            MinimumSize = new Size(1280, 720);
            AutoScroll = false;
            DoubleBuffered = true;
            BuildLayout();
            ConfigureGrid();
            WireEvents();
            ApplyLanguage();
            UpdateState();
        }

        [Category("Data"), Description("OWTS project data storage root. Setting this property does not scan until LoadRoot or BeginLoadRoot is called.")]
        public string RootDirectory
        {
            get { return rootDirectory; }
            set
            {
                ThrowIfDisposed();
                rootDirectory = value ?? string.Empty;
            }
        }

        [Category("Appearance"), DefaultValue("HV-DAC Xpert"), Description("Model name shown at the top right. Set this string from a LabVIEW .NET Property Node.")]
        public string ModelName
        {
            get { return modelName; }
            set
            {
                EnsureUiAccess();
                if (InvokeRequired) { Invoke(new Action<string>(v => ModelName = v), value); return; }
                modelName = value ?? string.Empty;
                UpdateModelHeader();
            }
        }

        [Category("Appearance"), DefaultValue(false), Description("Hide the standalone title strip when hosted inside the unified desktop shell.")]
        public bool EmbeddedMode
        {
            get { return embeddedMode; }
            set
            {
                EnsureUiAccess();
                if (InvokeRequired) { Invoke(new Action<bool>(v => EmbeddedMode = v), value); return; }
                if (embeddedMode == value) return;
                embeddedMode = value;
                // A standalone minimum must not constrain the docked control:
                // at 150% DPI it previously kept a 720 px-high canvas inside a
                // much shorter notebook viewport, hiding the entire action row.
                MinimumSize = value ? Size.Empty : new Size(1280, 720);
                if (brandHeader != null) brandHeader.Visible = !value;
                if (layoutShell != null)
                {
                    layoutShell.RowStyles[0].Height = value ? 0F : 56F;
                    layoutShell.PerformLayout();
                }
            }
        }

        [Category("Language"), DefaultValue(0)]
        public int LanguageIndex
        {
            get { return languageIndex; }
            set
            {
                EnsureUiAccess();
                if (InvokeRequired) { Invoke(new Action<int>(v => LanguageIndex = v), value); return; }
                ProjectManagementLanguages.Validate(value);
                if (languageIndex == value) return;
                languageIndex = value;
                english = !Xiongxin.OWTS.Localization.UiLanguage.IsChinese(value);
                ApplyLanguage();
                RefreshLocalizedRows();
            }
        }

        [Category("Language"), DefaultValue(false)]
        public bool EnglishVersion { get { return english; } set { LanguageIndex = value ? 1 : 0; } }
        [Browsable(false)] public string[] AvailableLanguages { get { return (string[])ProjectManagementLanguages.Names.Clone(); } }
        [Browsable(false)] public string LanguageCode { get { return ProjectManagementLanguages.Codes[languageIndex]; } }
        public void SetLanguageIndex(int index) { LanguageIndex = index; }
        public void SetLanguageCode(string code)
        {
            int index = ProjectManagementLanguages.IndexOfCode(code);
            if (index < 0) throw new ArgumentException("Unsupported language code.", "code");
            LanguageIndex = index;
        }
        [Browsable(false)] public bool IsBusy { get { return busy; } }
        [Browsable(false)] public string LastError { get { return lastError; } }
        [Browsable(false)] public string LastOperation { get { return lastOperation; } }
        [Browsable(false)] public int ProjectCount { get { return projects.Length; } }
        [Browsable(false)] public int VisibleProjectCount { get { return visibleProjects.Length; } }
        [Browsable(false)] public long LoadRequestSequence { get { return Interlocked.Read(ref loadRequestSequence); } }
        [Browsable(false)] public string LastLoadedProjectPath { get { return lastLoadedProjectPath; } }
        [Category("Behavior"), DefaultValue(true), Description("Latch an exit request for the host VI after a project path is emitted.")]
        public bool ExitViOnProjectLoad { get { return exitViOnProjectLoad; } set { ThrowIfDisposed(); exitViOnProjectLoad = value; } }
        [Browsable(false)] public bool ExitViRequested { get { return Volatile.Read(ref exitViRequested) != 0; } }
        [Browsable(false)] public long ExitViRequestSequence { get { return Interlocked.Read(ref exitViRequestSequence); } }
        [Browsable(false)] public string SearchKeyword
        {
            get { return searchKeyword; }
            set { EnsureUiAccess(); if (InvokeRequired) { Invoke(new Action<string>(v => SearchKeyword = v), value); return; } searchBox.Text = NormalizeSearchInput(value); }
        }
        [Browsable(false)] public ProjectSortField SortField { get { return sortField; } set { SetSort(value, sortDirection); } }
        [Browsable(false)] public ProjectSortDirection SortDirection { get { return sortDirection; } set { SetSort(sortField, value); } }
        [Browsable(false)] public ProjectRecord[] Projects { get { return projects.ToArray(); } }

        [Browsable(false)]
        public string SelectedProjectPath
        {
            get
            {
                return selectedProjectPath;
            }
        }

        [Browsable(false)]
        public string[] SelectedProjectPaths
        {
            get
            {
                return (string[])selectedProjectPaths.Clone();
            }
        }

        public event EventHandler ProjectsLoaded;
        public event EventHandler SelectionChanged;
        public event EventHandler<ProjectLoadRequestedEventArgs> ProjectLoadRequested;
        public event EventHandler<ProjectLoadRequestedEventArgs> HostExitRequested;
        public event EventHandler<ProjectOperationEventArgs> OperationCompleted;

        public string Health()
        {
            return "OK|ProjectManagementControl|1.0.10|x64|LabVIEW2026|TreeProjectInput|ApprovedUi=True|AlignedFilter|EqualActions|ModelNameInput=True|Languages=18|HostExitRequest=True|Task19Scan|Task20SortSearch|WordMerge=True|LightBlueWorkbench=True";
        }

        public bool LoadRoot(string path)
        {
            EnsureUiAccess();
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(LoadRoot), path);
            if (busy) return Error(T("控件正在执行其他操作。", "The control is busy."));
            SetBusy(true, null);
            SetStatus(() => T("正在扫描项目数据根目录…", "Scanning the project data root…"), false);
            try
            {
                ProjectScanResult result = ProjectScanner.Scan(path);
                FinishScan(result);
                return result.Ok;
            }
            finally { SetBusy(false, null); }
        }

        public bool BeginLoadRoot(string path)
        {
            return BeginLoadRootCore(path, string.Empty);
        }

        bool BeginLoadRootCore(string path, string preferredPath)
        {
            EnsureUiAccess();
            if (InvokeRequired)
                return (bool)Invoke(new Func<string, string, bool>(BeginLoadRootCore), new object[] { path, preferredPath });
            if (busy) return Error(T("控件正在执行其他操作。", "The control is busy."));
            if (!IsHandleCreated) CreateControl();
            // Scanning is not a new sorting or project-context choice.
            // Preserve the current search, sort and host-confirmed loaded path.
            if (!string.IsNullOrWhiteSpace(preferredPath)) preferredProjectPath = NormalizePathOrEmpty(preferredPath);
            int generation = ++scanGeneration;
            string requested = path;
            SetBusy(true, null);
            SetStatus(() => T("正在扫描项目数据根目录…", "Scanning the project data root…"), false);
            var weak = new WeakReference(this);
            Task.Run(delegate { return ProjectScanner.Scan(requested); }).ContinueWith(task =>
            {
                ProjectScanResult result = task.IsFaulted
                    ? new ProjectScanResult { Ok = false, Error = task.Exception.GetBaseException().Message }
                    : task.Result;
                var target = weak.Target as ProjectManagementControl;
                if (target == null || target.LifetimeEnded) return;
                if (!target.PublishScanCompletion(generation, result)) return;
                try
                {
                    target.BeginInvoke((Action)delegate
                    {
                        var live = weak.Target as ProjectManagementControl;
                        if (live != null) live.CompletePendingScan();
                    });
                }
                catch (InvalidOperationException) { }
            });
            return true;
        }

        sealed class ScanCompletion
        {
            internal readonly int Generation;
            internal readonly ProjectScanResult Result;
            internal ScanCompletion(int generation, ProjectScanResult result) { Generation = generation; Result = result; }
        }

        bool PublishScanCompletion(int generation, ProjectScanResult result)
        {
            Interlocked.Exchange(ref pendingScan, new ScanCompletion(generation, result));
            if (!LifetimeEnded) return true;
            Interlocked.Exchange(ref pendingScan, null);
            return false;
        }

        void CompletePendingScan()
        {
            var completion = Interlocked.Exchange(ref pendingScan, null);
            if (LifetimeEnded || completion == null || completion.Generation != scanGeneration) return;
            try { FinishScan(completion.Result); }
            finally { SetBusy(false, null); }
        }

        public bool RefreshProjects()
        {
            EnsureUiAccess();
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(RefreshProjects));
            if (string.IsNullOrWhiteSpace(rootDirectory))
                return Error(T("尚未设置项目数据根目录。", "The project data root has not been set."));
            return BeginLoadRootCore(rootDirectory, string.Empty);
        }

        /// <summary>
        /// Adds or refreshes one project supplied by the external tree control,
        /// pins it to the first visible row, and makes it the first selected item.
        /// </summary>
        public bool LoadProjectPath(string projectPath)
        {
            EnsureUiAccess();
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(LoadProjectPath), projectPath);
            if (busy) return Error(T("控件正在执行其他操作。", "The control is busy."));
            try
            {
                string path = ProjectScanner.NormalizeExistingDirectory(projectPath);
                if (!ProjectScanner.IsProjectDirectory(path))
                    return Error(T("树形项目路径不是有效项目目录。", "The tree project path is not a valid project directory."));
                ProjectRecord record = ProjectScanner.ReadProject(path);
                projects = projects.Where(p => !string.Equals(p.ProjectPath, path, StringComparison.OrdinalIgnoreCase))
                    .Concat(new[] { record }).ToArray();
                PrepareNewestFirst(path);
                checkedPaths.Clear();
                checkedPaths.Add(path);
                lastOperation = "LoadProjectPath";
                lastError = string.Empty;
                RenderGrid();
                SetStatus(() => T("已从树形项目加载并置顶：", "Loaded from the project tree and pinned first: ") + record.ProjectName, false);
                ProjectsLoaded?.Invoke(this, EventArgs.Empty);
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex) { return Error(ex.Message); }
        }

        public bool RequestLoadSelectedProject()
        {
            EnsureUiAccess();
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(RequestLoadSelectedProject));
            if (raisingLoadRequest) return false;
            if (busy) return Error(T("正在更新项目列表，请稍后重试。", "The project list is updating. Please try again shortly."));
            ProjectRecord record = CurrentRecord();
            if (record == null)
            {
                ProjectRecord[] selected = SelectedRecords();
                record = selected.Length == 1 ? selected[0] : null;
            }
            if (record == null) return Error(T("请选择一个要加载的项目。", "Select one project to load."));
            // The directory may have been moved or disconnected since the list
            // was scanned. Keep the selection/page and do not publish a request
            // for an unreadable project.
            if (!ProjectScanner.IsProjectDirectory(record.ProjectPath))
                return Error(T("无法加载所选项目。项目文件不可读取或已移动，请刷新列表后重试。",
                    "Cannot open this project. Its files are unavailable or have moved. Refresh the list and try again."));
            lastError = string.Empty;
            lastLoadedProjectPath = record.ProjectPath;
            Interlocked.Increment(ref loadRequestSequence);
            lastOperation = "LoadProject";
            SetStatus(() => T("正在打开项目：", "Opening project: ") + record.ProjectName, false);
            var args = new ProjectLoadRequestedEventArgs(lastLoadedProjectPath, loadRequestSequence);
            raisingLoadRequest = true;
            try
            {
                ProjectLoadRequested?.Invoke(this, args);
                if (!LifetimeEnded && exitViOnProjectLoad)
                {
                    Interlocked.Increment(ref exitViRequestSequence);
                    Interlocked.Exchange(ref exitViRequested, 1);
                    HostExitRequested?.Invoke(this, args);
                }
            }
            finally { raisingLoadRequest = false; }
            return true;
        }

        /// <summary>Displays a host validation failure without changing selection or closing the page.</summary>
        public void ReportProjectLoadFailure(string message)
        {
            EnsureUiAccess();
            if (InvokeRequired) { Invoke(new Action<string>(ReportProjectLoadFailure), message); return; }
            Error(T("项目加载失败，请检查项目文件后重试。", "The project could not be opened. Check its files and try again."));
        }

        public bool ConsumeExitViRequest()
        {
            return Interlocked.Exchange(ref exitViRequested, 0) != 0;
        }

        public void ResetExitViRequest()
        {
            Interlocked.Exchange(ref exitViRequested, 0);
        }

        public void SetSelectedProjectPaths(string[] paths)
        {
            EnsureUiAccess();
            if (InvokeRequired) { Invoke((Action<string[]>)SetSelectedProjectPaths, new object[] { paths }); return; }
            checkedPaths.Clear();
            HashSet<string> available = new HashSet<string>(projects.Select(p => p.ProjectPath), StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string full;
                try { full = Path.GetFullPath(path); } catch { continue; }
                if (available.Contains(full)) checkedPaths.Add(full);
            }
            RefreshCheckedRows();
        }

        public void ClearSelection()
        {
            EnsureUiAccess();
            if (InvokeRequired) { Invoke((Action)ClearSelection); return; }
            checkedPaths.Clear();
            grid.ClearSelection();
            grid.CurrentCell = null;
            RefreshCheckedRows();
        }

        public void SetSort(ProjectSortField field, ProjectSortDirection direction)
        {
            EnsureUiAccess();
            if (InvokeRequired) { Invoke(new Action<ProjectSortField, ProjectSortDirection>(SetSort), field, direction); return; }
            if (!Enum.IsDefined(typeof(ProjectSortField), field)) throw new ArgumentOutOfRangeException("field");
            if (!Enum.IsDefined(typeof(ProjectSortDirection), direction)) throw new ArgumentOutOfRangeException("direction");
            if (sortField == field && sortDirection == direction) return;
            sortField = field;
            sortDirection = direction;
            SetSortBoxSelection();
            UpdateDirectionButton();
            RenderGrid();
        }

        bool LifetimeEnded { get { return Volatile.Read(ref disposeStarted) != 0; } }

        void ThrowIfDisposed()
        {
            if (LifetimeEnded) throw new ObjectDisposedException(nameof(ProjectManagementControl));
        }

        void EnsureUiAccess()
        {
            ThrowIfDisposed();
            if (Thread.CurrentThread.ManagedThreadId != ownerThreadId && !IsHandleCreated)
                throw new InvalidOperationException("Create the control handle on its owner UI thread before cross-thread commands.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposeStarted, 1) == 0)
            {
                ++scanGeneration;
                Interlocked.Exchange(ref pendingScan, null);
                busy = false;
                Interlocked.Exchange(ref exitViRequested, 0);
                statusTextFactory = null;
                ProjectsLoaded = null;
                SelectionChanged = null;
                ProjectLoadRequested = null;
                HostExitRequested = null;
                OperationCompleted = null;
                searchBox.TextChanged -= SearchBoxTextChanged;
                grid.CellContentClick -= GridCellContentClick;
                grid.ColumnHeaderMouseClick -= GridColumnHeaderClick;
                grid.CellPainting -= GridCellPainting;
            }
            base.Dispose(disposing);
        }

        void BuildLayout()
        {
            var shell = new TableLayoutPanel { Name = "ProjectManagementShell", Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 5 };
            layoutShell = shell;
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Controls.Add(shell);

            var brand = new BrandHeaderPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12) };
            brandHeader = brand;
            titleLabel.AutoSize = true; titleLabel.Location = new Point(12, 15); titleLabel.Font = ProjectUiTheme.Font(this, 12f, FontStyle.Bold); titleLabel.ForeColor = Color.White; titleLabel.BackColor = Color.Transparent;
            editionLabel.AutoSize = false; editionLabel.AutoEllipsis = true; editionLabel.Dock = DockStyle.Right; editionLabel.Width = 190; editionLabel.Font = ProjectUiTheme.Font(this, 9.5f); editionLabel.ForeColor = Color.White; editionLabel.BackColor = Color.Transparent; editionLabel.TextAlign = ContentAlignment.MiddleRight;
            brand.Controls.Add(editionLabel); brand.Controls.Add(titleLabel); shell.Controls.Add(brand, 0, 0);
            brand.SizeChanged += delegate { FitModelHeader(); };

            var filterHost = new InstrumentPanel { Dock = DockStyle.Fill, Margin = new Padding(12, 8, 12, 8) };
            filterLayout.Name = "FilterLayout"; filterLayout.Dock = DockStyle.Fill; filterLayout.ColumnCount = 8; filterLayout.RowCount = 1; filterLayout.Margin = Padding.Empty; filterLayout.Padding = Padding.Empty;
            filterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72)); filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50)); filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128)); filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68)); filterLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            searchLabel.Dock = DockStyle.Fill; searchLabel.Margin = new Padding(0, 0, 8, 0); searchLabel.Font = ProjectUiTheme.Font(this, 9.5f); searchLabel.TextAlign = ContentAlignment.MiddleLeft;
            searchBox.AutoSize = false; searchBox.Multiline = true; searchBox.AcceptsReturn = false; searchBox.WordWrap = false; searchBox.ScrollBars = ScrollBars.None; searchBox.Dock = DockStyle.Fill; searchBox.Margin = Padding.Empty; searchBox.BorderStyle = BorderStyle.None; searchBox.Font = ProjectUiTheme.Font(this, 9.5f); searchBox.ImeMode = ImeMode.On; searchBox.BackColor = ProjectUiTheme.Surface; searchBox.ForeColor = ProjectUiTheme.Ink;
            var searchFrame = new InstrumentPanel { Name = "SearchFrame", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(6, 5, 6, 3) };
            searchFrame.Controls.Add(searchBox);
            clearSearchButton.MinimumSize = Size.Empty; clearSearchButton.Dock = DockStyle.Fill; clearSearchButton.Margin = new Padding(0, 2, 6, 2); clearSearchButton.Text = "×"; clearSearchButton.Compact = true;
            sortLabel.Dock = DockStyle.Fill; sortLabel.Margin = new Padding(0, 0, 8, 0); sortLabel.Font = ProjectUiTheme.Font(this, 9.5f); sortLabel.TextAlign = ContentAlignment.MiddleCenter;
            sortBox.Dock = DockStyle.None; sortBox.Margin = Padding.Empty; sortBox.DropDownStyle = ComboBoxStyle.DropDownList; sortBox.FlatStyle = FlatStyle.Flat; sortBox.Font = ProjectUiTheme.Font(this, 9.5f); sortBox.BackColor = ProjectUiTheme.Surface; sortBox.ForeColor = ProjectUiTheme.Ink;
            var sortFrame = new InstrumentPanel { Name = "SortFrame", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Padding = new Padding(1) };
            sortFrame.Controls.Add(sortBox);
            sortFrame.Layout += delegate { sortBox.SetBounds(1, Math.Max(1, (sortFrame.Height - sortBox.Height) / 2), Math.Max(1, sortFrame.Width - 2), sortBox.Height); };
            directionButton.MinimumSize = Size.Empty; directionButton.Dock = DockStyle.Fill; directionButton.Margin = new Padding(0, 2, 6, 2); directionButton.IconKind = InstrumentIcon.None; directionButton.Compact = true;
            clearSearchButton.Margin = new Padding(0, 0, 8, 0); directionButton.Margin = new Padding(0, 0, 8, 0);
            selectAll.Dock = DockStyle.Fill; selectAll.Margin = new Padding(0, 0, 8, 0); selectAll.TextAlign = ContentAlignment.MiddleLeft; selectAll.FlatStyle = FlatStyle.Flat; selectAll.Font = ProjectUiTheme.Font(this, 9.5f);
            resultBadge.Dock = DockStyle.Fill; resultBadge.TextAlign = ContentAlignment.MiddleCenter; resultBadge.Font = ProjectUiTheme.Font(this, 9.5f); resultBadge.ForeColor = ProjectUiTheme.Ink; resultBadge.BackColor = ProjectUiTheme.Header; resultBadge.Margin = Padding.Empty;
            filterLayout.Controls.Add(searchLabel, 0, 0); filterLayout.Controls.Add(searchFrame, 1, 0); filterLayout.Controls.Add(clearSearchButton, 2, 0); filterLayout.Controls.Add(sortLabel, 3, 0); filterLayout.Controls.Add(sortFrame, 4, 0); filterLayout.Controls.Add(directionButton, 5, 0); filterLayout.Controls.Add(selectAll, 6, 0); filterLayout.Controls.Add(resultBadge, 7, 0);
            filterHost.Controls.Add(filterLayout); shell.Controls.Add(filterHost, 0, 1);
            filterLayout.SizeChanged += delegate { FitFilterColumns(); };
            var gridHost = new InstrumentPanel { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 12, 8) };
            gridHost.Controls.Add(grid); shell.Controls.Add(gridHost, 0, 2);

            var actionHost = new InstrumentPanel { Dock = DockStyle.Fill, Margin = new Padding(12, 0, 12, 8) };
            actionFlow.Dock = DockStyle.Fill; actionFlow.WrapContents = true; actionFlow.AutoScroll = false; actionFlow.FlowDirection = FlowDirection.LeftToRight; actionFlow.Margin = Padding.Empty; actionFlow.Padding = Padding.Empty; actionFlow.BackColor = ProjectUiTheme.Surface;
            ConfigureAction(loadButton, InstrumentIcon.Load, true, false, 114); ConfigureAction(refreshButton, InstrumentIcon.Refresh, false, false, 88);
            ConfigureAction(importButton, InstrumentIcon.Import, false, false, 88); ConfigureAction(exportDataButton, InstrumentIcon.Export, false, false, 94);
            ConfigureAction(exportReportsButton, InstrumentIcon.Report, false, false, 94); ConfigureAction(mergeButton, InstrumentIcon.Merge, false, false, 94);
            ConfigureAction(deleteButton, InstrumentIcon.Delete, false, true, 94);
            actionFlow.Controls.Add(loadButton); actionFlow.Controls.Add(refreshButton); actionFlow.Controls.Add(importButton); actionFlow.Controls.Add(exportDataButton); actionFlow.Controls.Add(exportReportsButton); actionFlow.Controls.Add(mergeButton); actionFlow.Controls.Add(deleteButton);
            actionFlow.SizeChanged += delegate { FitActionButtons(); };
            foreach (Control action in actionFlow.Controls) action.FontChanged += delegate { FitActionButtons(); };
            actionHost.Controls.Add(actionFlow); shell.Controls.Add(actionHost, 0, 3);

            status.Dock = DockStyle.Fill; status.Margin = Padding.Empty; status.Padding = new Padding(12, 0, 12, 0); status.TextAlign = ContentAlignment.MiddleLeft; status.BackColor = ProjectUiTheme.GraphiteSoft; status.ForeColor = ProjectUiTheme.Ink; status.AutoEllipsis = true; status.Font = ProjectUiTheme.Font(this, 9.5f);
            shell.Controls.Add(status, 0, 4);
        }

        static void ConfigureAction(InstrumentButton button, InstrumentIcon icon, bool primary, bool danger, int width)
        {
            button.Width = width; button.Height = 38; button.Margin = new Padding(0, 0, 8, 0); button.IconKind = InstrumentIcon.None; button.Primary = primary; button.Danger = danger;
            button.AccessibleDescription = icon.ToString();
        }

        void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill;
            grid.Margin = Padding.Empty;
            grid.BackgroundColor = ProjectUiTheme.Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.ReadOnly = false;
            grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.RowHeadersVisible = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ScrollBars = ScrollBars.Both;
            grid.ColumnHeadersHeight = 40;
            grid.ColumnHeadersDefaultCellStyle.BackColor = ProjectUiTheme.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = ProjectUiTheme.Navy;
            grid.ColumnHeadersDefaultCellStyle.Font = ProjectUiTheme.Font(this, 10.5f, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            grid.DefaultCellStyle.BackColor = ProjectUiTheme.Surface;
            grid.DefaultCellStyle.ForeColor = ProjectUiTheme.Ink;
            grid.DefaultCellStyle.SelectionBackColor = ProjectUiTheme.Selection;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = ProjectUiTheme.Font(this, 9.5f);
            grid.AlternatingRowsDefaultCellStyle.BackColor = ProjectUiTheme.SurfaceAlt;
            grid.GridColor = ProjectUiTheme.Grid;
            grid.RowTemplate.Height = 38;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = string.Empty, Width = 42, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable, FlatStyle = FlatStyle.Flat });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TestTime", Width = 178, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic, DefaultCellStyle = { WrapMode = DataGridViewTriState.False } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProjectName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 240, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "CableLength", Width = 120, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Programmatic, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Report", Width = 136, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, WrapMode = DataGridViewTriState.False } });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "InsulationStatus", Width = 148, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter, WrapMode = DataGridViewTriState.False, Padding = new Padding(4) } });
            grid.SizeChanged += delegate { FitGridColumns(); };
            grid.FontChanged += delegate { FitGridColumns(); };
        }

        void WireEvents()
        {
            searchBox.TextChanged += SearchBoxTextChanged;
            searchBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; e.Handled = true; } };
            clearSearchButton.Click += delegate { searchBox.Clear(); searchBox.Focus(); };
            sortBox.SelectedIndexChanged += delegate { if (!rendering && !updatingSortSelection && sortBox.SelectedIndex >= 0 && sortField != (ProjectSortField)sortBox.SelectedIndex) { sortField = (ProjectSortField)sortBox.SelectedIndex; RenderGrid(); } };
            directionButton.Click += delegate { sortDirection = sortDirection == ProjectSortDirection.Descending ? ProjectSortDirection.Ascending : ProjectSortDirection.Descending; UpdateDirectionButton(); RenderGrid(); };
            selectAll.CheckedChanged += delegate { if (!rendering) SetVisibleChecked(selectAll.Checked); };
            grid.CellContentClick += GridCellContentClick;
            grid.CellDoubleClick += GridCellDoubleClick;
            grid.SelectionChanged += delegate { if (!rendering) { UpdateState(); SelectionChanged?.Invoke(this, EventArgs.Empty); } };
            grid.ColumnHeaderMouseClick += GridColumnHeaderClick;
            grid.CellPainting += GridCellPainting;
            loadButton.Click += delegate { RequestLoadSelectedProject(); };
            refreshButton.Click += delegate { RefreshProjects(); };
            importButton.Click += async delegate { await ImportProjectFromDialog(); };
            exportDataButton.Click += async delegate { await ExportSelectedData(); };
            exportReportsButton.Click += async delegate { await ExportSelectedReports(); };
            mergeButton.Click += async delegate { await MergeSelectedReports(); };
            deleteButton.Click += async delegate { await DeleteSelectedProjects(); };
        }

        void GridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            // Header/selection-checkbox gestures are not project activation.
            if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count || e.ColumnIndex < 0 ||
                e.ColumnIndex >= grid.Columns.Count || grid.Columns[e.ColumnIndex].Name == "Selected") return;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            RequestLoadSelectedProject();
        }

        void FinishScan(ProjectScanResult result)
        {
            if (LifetimeEnded) return;
            if (result == null || !result.Ok)
            {
                projects = new ProjectRecord[0]; visibleProjects = new ProjectRecord[0];
                externalCurrentProject = null;
                IncludeCurrentProject();
                Error(result == null ? T("扫描失败。", "Scan failed.") : result.Error);
                RenderGrid();
                return;
            }
            rootDirectory = result.RootDirectory;
            projects = result.Projects ?? new ProjectRecord[0];
            externalCurrentProject = null;
            IncludeCurrentProject();
            var availablePaths = new HashSet<string>(projects.Select(p => p.ProjectPath), StringComparer.OrdinalIgnoreCase);
            checkedPaths.RemoveWhere(path => !availablePaths.Contains(path));
            lastError = string.Empty;
            lastOperation = "ScanRoot";
            RenderGrid();
            int warningCount = result.Warnings == null ? 0 : result.Warnings.Length;
            SetStatus(() => string.Format(CultureInfo.InvariantCulture,
                T("已加载 {0} 个项目", "Loaded {0} projects"), projects.Length) +
                (warningCount == 0 ? string.Empty : string.Format(CultureInfo.InvariantCulture,
                    T("；已跳过 {0} 个不可读目录", "; skipped {0} unread directories"), warningCount)), false);
            ProjectsLoaded?.Invoke(this, EventArgs.Empty);
        }

        void RenderGrid()
        {
            if (LifetimeEnded || grid.Columns.Count == 0) return;
            ProjectRecord currentRecord = CurrentRecord();
            string pinnedPath = currentProjectPath.Length > 0 ? currentProjectPath : preferredProjectPath;
            string currentPath = !string.IsNullOrWhiteSpace(pinnedPath)
                ? pinnedPath
                : (currentRecord == null ? string.Empty : currentRecord.ProjectPath);
            rendering = true;
            try
            {
                visibleProjects = ProjectQuery.FilterAndSort(projects, searchBox.Text, sortField, sortDirection);
                ProjectRecord preferred = visibleProjects.FirstOrDefault(p => SameProjectPath(p.ProjectPath, pinnedPath));
                if (preferred != null)
                    visibleProjects = new[] { preferred }.Concat(visibleProjects.Where(p => !ReferenceEquals(p, preferred))).ToArray();
                grid.SuspendLayout();
                grid.Rows.Clear();
                foreach (ProjectRecord project in visibleProjects)
                {
                    string length = Finite(project.CableLengthMetres) ? project.CableLengthMetres.ToString("0.0###", CultureInfo.InvariantCulture) + " m" : "—";
                    string reports = LocalizedReportSummary(project);
                    string displayTime = LocalizedTestTime(project);
                    int rowIndex = grid.Rows.Add(checkedPaths.Contains(project.ProjectPath), displayTime, project.ProjectName, length, reports, LocalizedInsulationStatus(project));
                    DataGridViewRow row = grid.Rows[rowIndex]; row.Tag = project; row.Cells["Report"].Tag = project.HasReport;
                    row.Cells["TestTime"].ToolTipText = project.TestTimeSource;
                    row.Cells["CableLength"].ToolTipText = project.CableLengthSource;
                    row.Cells["ProjectName"].ToolTipText = ProjectPathToolTip(project);
                    row.Cells["Report"].ToolTipText = project.HasReport ? string.Join(Environment.NewLine, project.Reports.Select(r => r.RelativePath)) : T("未发现 DOC、DOCX 或 PDF 报告", "No DOC, DOCX or PDF report found");
                    row.Cells["InsulationStatus"].ToolTipText = InsulationStatusToolTip(project);
                }
                grid.ClearSelection();
                DataGridViewRow selectedRow = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => row.Tag is ProjectRecord && string.Equals(((ProjectRecord)row.Tag).ProjectPath, currentPath, StringComparison.OrdinalIgnoreCase));
                if (selectedRow == null && grid.Rows.Count > 0) selectedRow = grid.Rows[0];
                if (selectedRow != null)
                {
                    selectedRow.Selected = true;
                    grid.CurrentCell = selectedRow.Cells["ProjectName"];
                }
                resultBadge.Text = visibleProjects.Length.ToString(CultureInfo.InvariantCulture) + " / " + projects.Length.ToString(CultureInfo.InvariantCulture);
                selectAll.Checked = visibleProjects.Length > 0 && visibleProjects.All(p => checkedPaths.Contains(p.ProjectPath));
                UpdateColumnHeaders();
            }
            finally { grid.ResumeLayout(); rendering = false; }
            UpdateState();
            RefreshProjectContextStatus();
        }

        void RefreshLocalizedRows()
        {
            if (grid.Rows.Count != visibleProjects.Length) { RenderGrid(); return; }
            rendering = true;
            grid.SuspendLayout();
            try
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    ProjectRecord project = row.Tag as ProjectRecord;
                    if (project == null) continue;
                    row.Cells["TestTime"].Value = LocalizedTestTime(project);
                    row.Cells["ProjectName"].ToolTipText = ProjectPathToolTip(project);
                    row.Cells["Report"].Value = LocalizedReportSummary(project);
                    row.Cells["InsulationStatus"].Value = LocalizedInsulationStatus(project);
                    row.Cells["InsulationStatus"].ToolTipText = InsulationStatusToolTip(project);
                    if (!project.HasReport)
                        row.Cells["Report"].ToolTipText = T("未发现 DOC、DOCX 或 PDF 报告", "No DOC, DOCX or PDF report found");
                }
            }
            finally { grid.ResumeLayout(); rendering = false; }
            FitGridColumns();
            grid.Invalidate();
            RefreshProjectContextStatus();
        }

        void RefreshCheckedRows()
        {
            rendering = true;
            try
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    ProjectRecord project = row.Tag as ProjectRecord;
                    if (project != null) row.Cells["Selected"].Value = checkedPaths.Contains(project.ProjectPath);
                }
                selectAll.Checked = visibleProjects.Length > 0 && visibleProjects.All(p => checkedPaths.Contains(p.ProjectPath));
            }
            finally { rendering = false; }
            UpdateState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        void SetSortBoxSelection()
        {
            if (sortBox.Items.Count <= (int)sortField || sortBox.SelectedIndex == (int)sortField) return;
            updatingSortSelection = true;
            try { sortBox.SelectedIndex = (int)sortField; }
            finally { updatingSortSelection = false; }
        }

        void UpdateColumnHeaders()
        {
            grid.Columns["TestTime"].HeaderText = T("项目建立时间", "Project time") + SortMarker(ProjectSortField.TestTime);
            grid.Columns["ProjectName"].HeaderText = T("项目名称（始端 – 终端）", "Project (start – end)") + SortMarker(ProjectSortField.ProjectName);
            grid.Columns["CableLength"].HeaderText = T("电缆长度", "Cable length") + SortMarker(ProjectSortField.CableLength);
            grid.Columns["Report"].HeaderText = T("报告状态", "Report status");
            grid.Columns["InsulationStatus"].HeaderText = T("结论", "Conclusion");
            FitGridColumns();
        }

        string SortMarker(ProjectSortField field)
        {
            return sortField == field ? (sortDirection == ProjectSortDirection.Descending ? "  ▼" : "  ▲") : string.Empty;
        }

        void GridColumnHeaderClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            ProjectSortField? field = null;
            string name = grid.Columns[e.ColumnIndex].Name;
            if (name == "TestTime") field = ProjectSortField.TestTime;
            else if (name == "ProjectName") field = ProjectSortField.ProjectName;
            else if (name == "CableLength") field = ProjectSortField.CableLength;
            if (!field.HasValue) return;
            if (sortField == field.Value)
                sortDirection = sortDirection == ProjectSortDirection.Descending ? ProjectSortDirection.Ascending : ProjectSortDirection.Descending;
            else { sortField = field.Value; sortDirection = field.Value == ProjectSortField.ProjectName ? ProjectSortDirection.Ascending : ProjectSortDirection.Descending; }
            SetSortBoxSelection();
            UpdateDirectionButton();
            RenderGrid();
        }

        void GridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Selected") return;
            grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            ProjectRecord project = grid.Rows[e.RowIndex].Tag as ProjectRecord;
            if (project == null) return;
            bool value = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells["Selected"].EditedFormattedValue);
            if (value) checkedPaths.Add(project.ProjectPath); else checkedPaths.Remove(project.ProjectPath);
            UpdateState();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == "Selected")
            {
                e.PaintBackground(e.CellBounds, true);
                bool check = Convert.ToBoolean(e.FormattedValue);
                bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
                Rectangle box = new Rectangle(e.CellBounds.Left + (e.CellBounds.Width - 14) / 2,
                    e.CellBounds.Top + (e.CellBounds.Height - 14) / 2, 14, 14);
                using (var brush = new SolidBrush(check ? ProjectUiTheme.Cyan : ProjectUiTheme.Surface)) e.Graphics.FillRectangle(brush, box);
                using (var pen = new Pen(selected ? Color.White : check ? ProjectUiTheme.Cyan : ProjectUiTheme.Border)) e.Graphics.DrawRectangle(pen, box);
                if (check)
                    using (var pen = new Pen(Color.White, 2)) e.Graphics.DrawLines(pen, new[] {
                        new Point(box.Left + 3, box.Top + 7), new Point(box.Left + 6, box.Top + 10), new Point(box.Left + 11, box.Top + 4) });
                e.Handled = true;
                return;
            }
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Report") return;
            e.PaintBackground(e.CellBounds, true);
            ProjectRecord project = grid.Rows[e.RowIndex].Tag as ProjectRecord;
            if (project == null) return;
            string text = Convert.ToString(e.FormattedValue) ?? string.Empty;
            Font font = e.CellStyle.Font ?? grid.Font;
            Size size = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Rectangle badge = new Rectangle(e.CellBounds.Left + Math.Max(8, (e.CellBounds.Width - size.Width - 20) / 2),
                e.CellBounds.Top + 8, Math.Min(e.CellBounds.Width - 16, size.Width + 20), e.CellBounds.Height - 16);
            Color fill = project.HasReport ? ProjectUiTheme.SuccessPale : Color.FromArgb(238, 241, 244);
            Color ink = project.HasReport ? ProjectUiTheme.Success : ProjectUiTheme.Muted;
            using (var path = ProjectUiTheme.Rounded(badge, 9))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            TextRenderer.DrawText(e.Graphics, text, font, badge, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            e.Handled = true;
        }

        void SetVisibleChecked(bool value)
        {
            foreach (ProjectRecord project in visibleProjects)
            {
                if (value) checkedPaths.Add(project.ProjectPath); else checkedPaths.Remove(project.ProjectPath);
            }
            RefreshCheckedRows();
        }

        ProjectRecord CurrentRecord()
        {
            if (grid.CurrentRow != null) return grid.CurrentRow.Tag as ProjectRecord;
            return null;
        }

        ProjectRecord[] SelectedRecords()
        {
            ProjectRecord[] checkedRecords = projects.Where(p => checkedPaths.Contains(p.ProjectPath)).ToArray();
            if (checkedRecords.Length > 0) return checkedRecords;
            ProjectRecord current = CurrentRecord();
            return current == null ? new ProjectRecord[0] : new[] { current };
        }

        void UpdateState()
        {
            if (LifetimeEnded) return;
            ProjectRecord[] selected = SelectedRecords();
            ProjectRecord current = CurrentRecord();
            selectedProjectPath = current == null ? checkedPaths.FirstOrDefault() ?? string.Empty : current.ProjectPath;
            selectedProjectPaths = selected.Select(p => p.ProjectPath).ToArray();
            bool hasSelection = selected.Length > 0 && !busy;
            loadButton.Enabled = !busy && (CurrentRecord() != null || selected.Length == 1);
            refreshButton.Enabled = !busy && !string.IsNullOrWhiteSpace(rootDirectory);
            importButton.Enabled = !busy && !string.IsNullOrWhiteSpace(rootDirectory);
            exportDataButton.Enabled = hasSelection;
            exportReportsButton.Enabled = hasSelection && selected.Any(p => p.HasReport);
            mergeButton.Enabled = !busy && selected.Count(p => !string.IsNullOrWhiteSpace(p.LatestMergeableReportPath)) >= 2;
            deleteButton.Enabled = hasSelection;
            grid.Enabled = !busy;
        }

        void SetBusy(bool value, string message)
        {
            if (LifetimeEnded) return;
            busy = value;
            UseWaitCursor = value;
            if (!string.IsNullOrWhiteSpace(message)) SetStatus(message, false);
            UpdateState();
        }

        bool Error(string message)
        {
            lastError = message ?? string.Empty;
            SetStatus(lastError, true);
            return false;
        }

        void SetStatus(string message, bool error)
        {
            statusTextFactory = null;
            statusBaseText = message ?? string.Empty;
            statusIsError = error;
            RefreshProjectContextStatus();
        }

        void SetStatus(Func<string> textFactory, bool error)
        {
            statusTextFactory = textFactory;
            statusBaseText = textFactory == null ? string.Empty : textFactory();
            statusIsError = error;
            RefreshProjectContextStatus();
        }

        void ApplyLanguage()
        {
            titleLabel.Text = T("项目管理", "PROJECT MANAGEMENT");
            UpdateModelHeader();
            searchLabel.Text = T("项目检索", "SEARCH");
            searchBox.AccessibleDescription = T("名称、时间、长度、路径、报告或结论", "Name, time, length, path, report or conclusion");
            sortLabel.Text = T("排序", "SORT");
            updatingSortSelection = true;
            sortBox.BeginUpdate();
            try
            {
                sortBox.Items.Clear();
                sortBox.Items.Add(T("项目建立时间", "Project time")); sortBox.Items.Add(T("项目名称", "Project name")); sortBox.Items.Add(T("电缆长度", "Cable length"));
                sortBox.SelectedIndex = (int)sortField;
            }
            finally { sortBox.EndUpdate(); updatingSortSelection = false; }
            selectAll.Text = T("全选", "Select all");
            loadButton.Text = T("加载选中项目", "Load project");
            refreshButton.Text = T("刷新列表", "Refresh");
            importButton.Text = T("导入数据", "Import");
            exportDataButton.Text = T("导出数据", "Export data");
            exportReportsButton.Text = T("导出报告", "Export reports");
            mergeButton.Text = T("合并报告", "Merge reports");
            deleteButton.Text = T("删除项目", "Delete");
            FitFilterColumns();
            FitActionButtons();
            UpdateDirectionButton();
            UpdateColumnHeaders();
            if (statusTextFactory != null) { statusBaseText = statusTextFactory(); RefreshProjectContextStatus(); }
            else if (projects.Length == 0 && string.IsNullOrEmpty(status.Text))
                SetStatus(() => T("等待树形项目控件传入项目根目录或项目路径。", "Waiting for the project tree to provide a root or project path."), false);
        }

        void UpdateModelHeader()
        {
            editionLabel.Text = modelName;
            editionLabel.AccessibleName = modelName;
            FitModelHeader();
        }

        void FitModelHeader()
        {
            Control brand = editionLabel.Parent;
            if (brand == null) return;
            int usableWidth = Math.Max(0, brand.ClientSize.Width - brand.Padding.Horizontal);
            int availableAfterTitle = Math.Max(0, brand.ClientSize.Width - brand.Padding.Right - titleLabel.Right - 16);
            int maxWidth = Math.Min((int)(usableWidth * 0.55), availableAfterTitle);
            int textWidth = TextRenderer.MeasureText(modelName, editionLabel.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 16;
            editionLabel.Width = Math.Min(maxWidth, Math.Max(190, textWidth));
        }

        void UpdateDirectionButton()
        {
            directionButton.Text = sortDirection == ProjectSortDirection.Descending ? "▼" : "▲";
            directionButton.AccessibleName = sortDirection == ProjectSortDirection.Descending ? T("降序", "Descending") : T("升序", "Ascending");
        }

        void FitActionButtons()
        {
            if (LifetimeEnded || fittingActionButtons) return;
            fittingActionButtons = true;
            try
            {
                InstrumentButton[] buttons = { loadButton, refreshButton, importButton, exportDataButton, exportReportsButton, mergeButton, deleteButton };
                int available = actionFlow.ClientSize.Width;
                if (available <= 0) return;
                const int gap = 8;
                int preferredWidth = buttons.Max(button => Math.Max(96,
                    TextRenderer.MeasureText(button.Text ?? string.Empty, button.Font, Size.Empty,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 24));
                int columns = Math.Max(1, Math.Min(buttons.Length, (available + gap) / (preferredWidth + gap)));
                int rows = (buttons.Length + columns - 1) / columns;
                int commonWidth = Math.Max(1, (available - gap * (columns - 1)) / columns);
                int commonHeight = 38;
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].Width = commonWidth;
                    bool rowEnd = i % columns == columns - 1 || i == buttons.Length - 1;
                    buttons[i].Margin = new Padding(0, 0, rowEnd ? 0 : gap, i / columns < rows - 1 ? gap : 0);
                    actionFlow.SetFlowBreak(buttons[i], rowEnd);
                    Size measured = TextRenderer.MeasureText(buttons[i].Text ?? string.Empty, buttons[i].Font,
                        new Size(Math.Max(1, commonWidth - 20), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    commonHeight = Math.Max(commonHeight, measured.Height + 16);
                }
                foreach (InstrumentButton button in buttons) button.Height = commonHeight;
                var shell = actionFlow.Parent == null ? null : actionFlow.Parent.Parent as TableLayoutPanel;
                if (shell != null)
                {
                    shell.RowStyles[3].Height = commonHeight * rows + gap * (rows - 1) + 32;
                    shell.PerformLayout();
                }
                actionFlow.PerformLayout();
            }
            finally { fittingActionButtons = false; }
        }

        void FitFilterColumns()
        {
            if (filterLayout.ColumnStyles.Count < 8 || fittingFilterColumns) return;
            fittingFilterColumns = true;
            try
            {
            float searchWidth = BoundedTextWidth(searchLabel.Text, searchLabel.Font, 14, 72, 240);
            float sortWidth = BoundedTextWidth(sortLabel.Text, sortLabel.Font, 14, 50, 200);
            int itemWidth = sortBox.Items.Cast<object>().Select(x => Convert.ToString(x) ?? string.Empty)
                .Select(x => TextRenderer.MeasureText(x, sortBox.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width)
                .DefaultIfEmpty(88).Max();
            float selectWidth = BoundedTextWidth(selectAll.Text, selectAll.Font, 36, 80, 300);
            float sortBoxWidth = Math.Max(140, itemWidth + 42);
            // Reserve 120 usable pixels inside the search input, not merely in its
            // table column. The frame's padding/margin previously consumed another
            // 20 pixels, leaving a clipped 119-pixel input at the one-row boundary.
            Control searchFrame = searchBox.Parent;
            int searchChrome = searchBox.Margin.Horizontal + (searchFrame == null ? 0 :
                searchFrame.Padding.Horizontal + searchFrame.Margin.Horizontal + Math.Max(0, searchFrame.Width - searchFrame.ClientSize.Width));
            int minimumSearchColumnWidth = 120 + searchChrome + 2;
            bool twoRows = filterLayout.ClientSize.Width - filterLayout.Padding.Horizontal <
                searchWidth + sortWidth + sortBoxWidth + selectWidth + 44 + 44 + 100 + minimumSearchColumnWidth;
            Control[] controls = { searchLabel, searchBox.Parent, clearSearchButton, sortLabel, sortBox.Parent, directionButton, selectAll, resultBadge };
            filterLayout.SuspendLayout();
            filterLayout.RowCount = twoRows ? 2 : 1;
            filterLayout.RowStyles.Clear();
            filterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, twoRows ? 50 : 100));
            if (twoRows) filterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            float[] widths = twoRows
                ? new[] { Math.Max(searchWidth, sortWidth), 50f, 0f, 44f, 50f, 0f, 44f, 100f }
                : new[] { searchWidth, 100f, 44f, sortWidth, sortBoxWidth, 44f, selectWidth, 100f };
            for (int i = 0; i < widths.Length; i++)
            {
                filterLayout.ColumnStyles[i].SizeType = i == 1 || (twoRows && i == 4) ? SizeType.Percent : SizeType.Absolute;
                filterLayout.ColumnStyles[i].Width = widths[i];
            }
            for (int i = 0; i < controls.Length; i++)
            {
                filterLayout.SetColumnSpan(controls[i], 1);
                filterLayout.SetCellPosition(controls[i], new TableLayoutPanelCellPosition(i, 0));
            }
            if (twoRows)
            {
                filterLayout.SetColumnSpan(searchBox.Parent, 5);
                filterLayout.SetCellPosition(clearSearchButton, new TableLayoutPanelCellPosition(6, 0));
                filterLayout.SetCellPosition(sortLabel, new TableLayoutPanelCellPosition(0, 1));
                filterLayout.SetCellPosition(sortBox.Parent, new TableLayoutPanelCellPosition(1, 1));
                filterLayout.SetColumnSpan(sortBox.Parent, 2);
                filterLayout.SetCellPosition(directionButton, new TableLayoutPanelCellPosition(3, 1));
                filterLayout.SetCellPosition(selectAll, new TableLayoutPanelCellPosition(4, 1));
                filterLayout.SetColumnSpan(selectAll, 4);
            }
            sortBox.DropDownWidth = Math.Max(sortBox.Width, itemWidth + 42);
            int rowHeight = Math.Max(36, sortBox.PreferredHeight + 4);
            if (layoutShell != null) layoutShell.RowStyles[1].Height = rowHeight * (twoRows ? 2 : 1) + 40;
            filterLayout.ResumeLayout(true);
            if (layoutShell != null) layoutShell.PerformLayout();
            }
            finally { fittingFilterColumns = false; }
        }

        static float BoundedTextWidth(string text, Font font, int chrome, int minimum, int maximum)
        {
            int width = TextRenderer.MeasureText(text ?? string.Empty, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + chrome;
            return Math.Max(minimum, Math.Min(maximum, width));
        }

        void FitGridColumns()
        {
            if (grid.Columns.Count < 6 || fittingGridColumns || grid.ClientSize.Width <= 0) return;
            fittingGridColumns = true;
            try
            {
                string[] names = { "TestTime", "CableLength", "Report" };
                int available = Math.Max(1, grid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - grid.Columns["Selected"].Width - 4);
                int projectMinimum = Math.Min(240, Math.Max(120, available / 4));
                grid.Columns["ProjectName"].MinimumWidth = projectMinimum;
                FitReportStatusColumn();
                grid.Columns["TestTime"].MinimumWidth = Math.Max(178,
                    TextRenderer.MeasureText("2026-09-29 23:59:59", grid.DefaultCellStyle.Font ?? grid.Font,
                        Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 24);
                int[] desired = names.Select(name => Math.Max(name == "TestTime" ? 220 : name == "Report" ? 200 : 160,
                    TextRenderer.MeasureText(grid.Columns[name].HeaderText, grid.ColumnHeadersDefaultCellStyle.Font,
                        Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 28)).ToArray();
                double factor = Math.Min(1d, (double)Math.Max(3, available - projectMinimum) / desired.Sum());
                for (int i = 0; i < names.Length; i++) grid.Columns[names[i]].Width = Math.Max(grid.Columns[names[i]].MinimumWidth, Math.Max(24, (int)(desired[i] * factor)));
                FitInsulationStatusColumn();
                int headerHeight = 40;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    Size measured = TextRenderer.MeasureText(column.HeaderText, grid.ColumnHeadersDefaultCellStyle.Font,
                        new Size(Math.Max(1, column.Width - 20), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    headerHeight = Math.Max(headerHeight, measured.Height + 14);
                }
                grid.ColumnHeadersHeight = headerHeight;
            }
            finally { fittingGridColumns = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FitActionButtons();
            if (!DesignMode && !LifetimeEnded) BeginInvoke((MethodInvoker)delegate { FitActionButtons(); CompletePendingScan(); });
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FitFilterColumns();
            FitActionButtons();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            FitFilterColumns();
            FitActionButtons();
        }

        void PrepareNewestFirst(string preferredPath)
        {
            sortField = ProjectSortField.TestTime;
            sortDirection = ProjectSortDirection.Descending;
            preferredProjectPath = NormalizePathOrEmpty(preferredPath);
            SetSortBoxSelection();
            UpdateDirectionButton();
        }

        void SearchBoxTextChanged(object sender, EventArgs e)
        {
            if (LifetimeEnded) return;
            if (!repairingSearchText)
            {
                string repaired = NormalizeSearchInput(searchBox.Text);
                if (!string.Equals(repaired, searchBox.Text, StringComparison.Ordinal))
                {
                    repairingSearchText = true;
                    searchBox.Text = repaired;
                    searchBox.SelectionStart = repaired.Length;
                    repairingSearchText = false;
                    return;
                }
            }
            searchKeyword = searchBox.Text;
            RenderGrid();
        }

        static string NormalizeSearchInput(string value)
        {
            string text = value ?? string.Empty;
            if (text.Length == 0 || CountCjk(text) > 0) return text;
            int suspicious = text.Count(c => c >= 0x80 && c <= 0xFF) +
                text.Count(c => c == '\uFFFD' || c == '锟' || c == 'Ã' || c == 'Â');
            if (suspicious < 2) return text;
            foreach (Encoding target in new[] { Encoding.UTF8, Encoding.GetEncoding(936) })
            {
                try
                {
                    Encoding source = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    string candidate = target.GetString(source.GetBytes(text));
                    if (CountCjk(candidate) >= 2 && candidate.IndexOf('\uFFFD') < 0) return candidate;
                }
                catch (Exception) { }
            }
            return text;
        }

        static int CountCjk(string value)
        {
            return (value ?? string.Empty).Count(c => (c >= '\u3400' && c <= '\u9FFF') || (c >= '\uF900' && c <= '\uFAFF'));
        }

        static string NormalizePathOrEmpty(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                string full = Path.GetFullPath(path.Trim());
                string root = Path.GetPathRoot(full);
                return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
            }
            catch { return string.Empty; }
        }

        string T(string zh, string en) { return ProjectManagementLanguages.Text(languageIndex, en, zh); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        string LocalizedReportSummary(ProjectRecord project)
        {
            if (!project.HasReport) return T("无报告", "No report");
            int word = project.Reports.Count(r => r.Extension == ".doc" || r.Extension == ".docx");
            int pdf = project.Reports.Count(r => r.Extension == ".pdf");
            if (word > 0 && pdf > 0) return "DOCX " + word + "  ·  PDF " + pdf;
            return word > 0 ? "DOCX " + word : "PDF " + pdf;
        }

        string LocalizedTestTime(ProjectRecord project)
        {
            return project.HasTestTime ? project.TestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : project.DisplayTestTime;
        }
    }
}
