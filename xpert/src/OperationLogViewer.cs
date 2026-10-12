using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    // Read-only viewer. Access control is the responsibility of its caller.
    // No clear/delete/export/upload actions and no authentication values here.
    internal sealed class OperationLogViewer : Form
    {
        readonly OperationAuditLog log;
        readonly int language;
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly DateTimePicker from = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Checked = false, Width = 208 };
        readonly DateTimePicker to = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm", ShowCheckBox = true, Checked = false, Width = 208 };
        readonly TextBox keyword = new TextBox { Width = 240, MaxLength = 128 };
        readonly Label status = new Label { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4), ForeColor = AppTheme.Muted };
        readonly TextBox selected = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
        readonly Button refresh, close;
        readonly DataGridView[] grids = new DataGridView[2];
        CancellationTokenSource pending;
        int requestSequence;
        bool closing;
        bool CannotUpdateUi { get { return closing || Disposing || IsDisposed; } }
        internal bool IsLoading { get; private set; }
        internal OperationAuditQueryResult LastQueryResult { get; private set; }
        string T(string zh, string en) { return UiLanguage.Text(language, zh, en); }

        internal OperationLogViewer(OperationAuditLog auditLog, int languageIndex)
        {
            log = auditLog ?? throw new ArgumentNullException("auditLog");
            language = UiLanguage.ResolveIndex(languageIndex);
            Text = T("软件日志", "Software logs");
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            Font = AppTheme.Font(9.5f, FontStyle.Regular); ForeColor = AppTheme.Ink; BackColor = AppTheme.Canvas;
            StartPosition = FormStartPosition.CenterParent; Size = new Size(980, 760); MinimumSize = new Size(820, 700);
            ShowInTaskbar = false; MinimizeBox = false; AppTheme.ApplyDialogIcon(this);
            refresh = AppTheme.Button(T("刷新", "Refresh"), true); close = AppTheme.Button(T("关闭", "Close"), false);
            refresh.AutoSize = close.AutoSize = true; refresh.MinimumSize = close.MinimumSize = new Size(96, 38);
            close.DialogResult = DialogResult.Cancel; CancelButton = close;
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var introduction = new Label { AutoSize = true, Margin = new Padding(4, 0, 4, 8),
                Text = T("本机日志只读浏览：记录时间为 UTC，筛选使用本机时间。日志自动轮换，不记录密码及参数值。",
                    "Read-only local logs: entries use UTC, filters use local time. Logs rotate automatically; passwords and parameter values are excluded.") };
            shell.Controls.Add(introduction, 0, 0);
            var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            filters.Controls.Add(Filter(T("从（可选）", "From (optional)"), from));
            filters.Controls.Add(Filter(T("至（可选）", "To (optional)"), to));
            filters.Controls.Add(Filter(T("关键词（动作码或项目路径）", "Keyword (action code or project path)"), keyword));
            var refreshBox = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, Font.Height + 3, 0, 0) };
            refreshBox.Controls.Add(refresh); filters.Controls.Add(refreshBox); shell.Controls.Add(filters, 0, 1);
            for (int i = 0; i < 2; i++)
            {
                var page = new TabPage(i == 0 ? T("项目建立记录", "Project records") : T("操作记录", "Operation records")) { Padding = new Padding(4), BackColor = Color.White };
                var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                    AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                    ScrollBars = ScrollBars.Vertical, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
                grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.DefaultCellStyle.Padding = new Padding(4);
                grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.Canvas;
                grid.AlternatingRowsDefaultCellStyle.BackColor = AppTheme.Canvas; grid.GridColor = AppTheme.Border;
                grid.Columns.Add("Time", T("时间（UTC）", "Time (UTC)")); grid.Columns.Add("Action", T("动作", "Action"));
                grid.Columns.Add("Module", T("功能模块", "Module")); grid.Columns.Add("Outcome", T("结果", "Result")); grid.Columns.Add("Project", T("项目路径", "Project path"));
                grid.Columns[0].FillWeight = 22; grid.Columns[1].FillWeight = 21; grid.Columns[2].FillWeight = 17; grid.Columns[3].FillWeight = 13; grid.Columns[4].FillWeight = 27;
                foreach (DataGridViewColumn column in grid.Columns) { column.MinimumWidth = 32; column.SortMode = DataGridViewColumnSortMode.NotSortable; }
                grid.SelectionChanged += (s, e) => ShowSelection();
                page.Controls.Add(grid); grids[i] = grid; tabs.TabPages.Add(page);
            }
            shell.Controls.Add(tabs, 0, 2); shell.Controls.Add(status, 0, 3); shell.Controls.Add(selected, 0, 4);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.RightToLeft };
            footer.Controls.Add(close); shell.Controls.Add(footer, 0, 5); Controls.Add(shell);
            shell.SizeChanged += (s, e) =>
            {
                if (CannotUpdateUi) return;
                introduction.MaximumSize = new Size(Math.Max(200, shell.ClientSize.Width - 40), 0);
                status.MaximumSize = new Size(Math.Max(200, shell.ClientSize.Width - 40), 0);
            };
            refresh.Click += async (s, e) => await RefreshLogsAsync();
            tabs.SelectedIndexChanged += async (s, e) => { if (!CannotUpdateUi && IsHandleCreated) await RefreshLogsAsync(); };
            keyword.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshLogsAsync(); } };
            Shown += async (s, e) =>
            {
                if (CannotUpdateUi) return;
                from.Width = to.Width = TextRenderer.MeasureText("2026-09-28 23:59", from.Font).Width + 52;
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 24), Math.Min(MinimumSize.Height, area.Height - 24));
                Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
                await RefreshLogsAsync();
            };
        }
        Control Filter(string label, Control control)
        {
            var group = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 12, 8) };
            group.Controls.Add(new Label { AutoSize = true, Text = label }); group.Controls.Add(control); return group;
        }
        internal async Task RefreshLogsAsync()
        {
            if (CannotUpdateUi) return;
            int sequence = ++requestSequence;
            if (pending != null) pending.Cancel();
            var token = new CancellationTokenSource(); pending = token;
            DateTimeOffset? start = from.Checked ? LocalTime(from.Value) : (DateTimeOffset?)null;
            DateTimeOffset? end = to.Checked ? LocalTime(to.Value) : (DateTimeOffset?)null;
            string filter = keyword.Text; int category = tabs.SelectedIndex;
            IsLoading = true; refresh.Enabled = false; status.Text = T("正在读取本机日志…", "Reading local logs…");
            try
            {
                OperationAuditQueryResult result = await Task.Run(() => log.Query(category == 0 ? AuditLogKind.Project : AuditLogKind.Operation, start, end, filter, 500, token.Token));
                if (CannotUpdateUi || sequence != requestSequence) return;
                LastQueryResult = result;
                DataGridView grid = grids[category]; grid.Rows.Clear();
                foreach (var entry in result.Entries)
                {
                    int row = grid.Rows.Add(entry.TimeUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), ActionText(entry.Action), ModuleText(entry.Module), OutcomeText(entry.Outcome), entry.ProjectPathOmitted ? T("路径已省略", "Path omitted") : entry.ProjectPath);
                    grid.Rows[row].Tag = entry;
                }
                status.Text = string.Format(CultureInfo.CurrentCulture, T("显示 {0} 条；损坏行 {1}，未完成尾行 {2}。", "Showing {0} entries; invalid lines {1}, incomplete tail lines {2}."), result.Entries.Count, result.InvalidLines, result.PartialLines);
                if (result.IsLimited) status.Text += " " + T("已达到查询上限，请缩小时间范围。", "Query limit reached; narrow the time range.");
                if (result.PendingEntries > 0) status.Text += " " + T("正在后台保存记录：", "Entries still queued: ") + result.PendingEntries.ToString(CultureInfo.CurrentCulture);
                if (result.ReadFailureCode.Length > 0) status.Text += " " + T("读取未完成：", "Read incomplete: ") + FailureText(result.ReadFailureCode);
                if (result.LastWriteFailureCode.Length > 0) status.Text += " " + T("最近一次记录失败：", "Most recent logging failure: ") + FailureText(result.LastWriteFailureCode) + " (" + result.LastWriteFailureUtc.Value.ToString("u") + ")";
                status.ForeColor = result.ReadFailureCode.Length > 0 || result.LastWriteFailureCode.Length > 0 ? AppTheme.Warning : AppTheme.Muted;
                ShowSelection();
            }
            catch (Exception) { if (!CannotUpdateUi && sequence == requestSequence) status.Text = T("日志暂时无法读取，软件其他功能不受影响。", "Logs are temporarily unavailable. Other application functions are unaffected."); }
            finally
            {
                if (sequence == requestSequence) { IsLoading = false; if (!CannotUpdateUi) refresh.Enabled = true; pending = null; }
                token.Dispose();
            }
        }
        static DateTimeOffset LocalTime(DateTime value) { return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local)); }
        void ShowSelection()
        {
            if (CannotUpdateUi) return;
            var grid = grids[Math.Max(0, tabs.SelectedIndex)];
            var entry = grid == null || grid.CurrentRow == null ? null : grid.CurrentRow.Tag as OperationAuditEntry;
            selected.Text = entry == null ? T("选择一条记录查看完整信息。", "Select an entry to view full details.") :
                entry.TimeUtc.ToString("o", CultureInfo.InvariantCulture) + " · " + entry.Action + " · " + entry.Module + " · " + entry.Outcome + Environment.NewLine +
                ActionText(entry.Action) + Environment.NewLine + (entry.ProjectPathOmitted ? T("含敏感内容或格式不正确的路径未记录。", "A sensitive or invalid path was not recorded.") : entry.ProjectPath);
        }
        string ActionText(string action)
        {
            string chinese;
            switch (action)
            {
                case "ProjectCreated": chinese = "建立项目"; break; case "ProjectUpdated": chinese = "更新项目"; break;
                case "ApplicationStart": chinese = "启动软件"; break; case "ApplicationExit": chinese = "退出软件"; break;
                case "ModuleOpen": chinese = "进入功能模块"; break; case "ReturnHome": chinese = "返回主页"; break;
                case "SettingsOpened": chinese = "打开设置"; break; case "SettingsSaved": chinese = "保存设置"; break; case "SettingsSaveFailed": chinese = "设置保存失败"; break;
                case "AuthorizationAccepted": chinese = "受保护功能验证通过"; break; case "AuthorizationRejected": chinese = "受保护功能验证未通过"; break;
                case "DeviceCheckRequested": chinese = "请求检查设备"; break; case "DeviceCheckCompleted": chinese = "设备检查完成"; break; case "DeviceCheckFailed": chinese = "设备检查失败"; break;
                case "ProjectLoaded": chinese = "加载项目"; break; case "ProjectLoadFailed": chinese = "项目加载失败"; break;
                case "PressureRequested": chinese = "请求加压试验"; break; case "CalibrationRequested": chinese = "请求校准试验"; break;
                case "CalibrationSaved": chinese = "校准保存完成"; break;
                case "WithstandRequested": chinese = "请求耐压试验"; break; case "WithstandStopped": chinese = "停止耐压试验"; break;
                case "AnalysisSaveRequested": chinese = "请求保存分析"; break; case "AnalysisSaved": chinese = "分析保存完成"; break;
                case "AnalysisSettingsConfirmed": chinese = "确认分析设置"; break; case "ReportGenerated": chinese = "报告生成完成"; break;
                default: chinese = "操作"; break;
            }
            return T(chinese, OperationAuditLog.Description(AuditLogKind.Operation, action));
        }
        string OutcomeText(AuditOutcome value)
        {
            switch (value)
            {
                case AuditOutcome.Requested: return T("已请求", "Requested"); case AuditOutcome.Succeeded: return T("成功", "Succeeded");
                case AuditOutcome.Failed: return T("失败", "Failed"); case AuditOutcome.Cancelled: return T("已取消", "Cancelled");
                case AuditOutcome.Simulation: return T("仿真", "Simulation"); case AuditOutcome.Partial: return T("部分完成", "Partial");
                default: return T("结果待确认", "Unconfirmed");
            }
        }
        string ModuleText(AuditModule module)
        {
            switch (module)
            {
                case AuditModule.Home: return T("主页", "Home"); case AuditModule.ProjectManagement: return T("项目管理", "Project management");
                case AuditModule.ProjectCreation: return T("创建项目", "Create project"); case AuditModule.DataBrowser: return T("数据浏览", "Data browser");
                case AuditModule.DataAnalysis: return T("数据分析", "Data analysis"); case AuditModule.ReportGenerator: return T("生成报告", "Report generator");
                case AuditModule.PressureTest: return T("加压试验", "Voltage test"); case AuditModule.CalibrationTest: return T("校准试验", "Calibration");
                case AuditModule.WithstandTest: return T("耐压试验", "Withstand test"); case AuditModule.Settings: return T("设置", "Settings");
                default: return "—";
            }
        }
        string FailureText(string code)
        {
            switch (code)
            {
                case "invalid_query": return T("筛选条件不正确", "Invalid filters"); case "write_busy": return T("记录繁忙", "Logging busy");
                case "access_denied": return T("没有日志目录访问权限", "Log access denied"); case "cancelled": return T("已取消", "Cancelled");
                case "invalid_action": case "invalid_outcome": case "invalid_module": return T("记录类型不受支持", "Unsupported entry type");
                case "invalid_path": return T("日志位置不正确", "Invalid log location"); case "entry_too_large": return T("记录超过容量限制", "Entry exceeds size limit");
                case "queue_full": return T("日志队列已满，部分记录未被接收", "Log queue full; some entries were not accepted");
                case "queue_closed": return T("日志队列已关闭", "Log queue closed");
                case "flush_timeout": return T("日志排空达到时间上限", "Log flush time budget reached");
                case "worker_unavailable": case "queue_unavailable": return T("日志后台服务不可用", "Background log service unavailable");
                default: return T("日志存储不可用", "Log storage unavailable");
            }
        }
        private void StopQueriesForClose()
        {
            if (closing) return;
            closing = true;
            ++requestSequence;
            IsLoading = false;
            CancellationTokenSource query = pending;
            pending = null;
            // The async query owns disposal in its finally block. Invalidate
            // its result here, but never dispose a token it may still be using.
            if (query != null)
                try { query.Cancel(); } catch (ObjectDisposedException) { }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (!e.Cancel) StopQueriesForClose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) StopQueriesForClose();
            base.Dispose(disposing);
        }
        protected override void SetClientSizeCore(int x, int y)
        {
            // WinForms programmatic ClientSize assignments can otherwise bypass
            // WM_GETMINMAXINFO and make docked footer controls leave the window.
            Size frame = SizeFromClientSize(Size.Empty);
            base.SetClientSizeCore(Math.Max(x, Math.Max(0, MinimumSize.Width - frame.Width)),
                Math.Max(y, Math.Max(0, MinimumSize.Height - frame.Height)));
        }
    }
}
