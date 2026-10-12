using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.ProjectCables
{
    // Shared by analysis, calibration and browsing. The owning workflow supplies
    // the commit callback; a failed commit keeps this editor and its inputs open.
    internal sealed class CableJointDialog : Form
    {
        readonly CableJointSnapshot snapshot;
        readonly bool english;
        readonly int languageIndex;
        readonly List<CheckBox> phaseBoxes = new List<CheckBox>();
        readonly CheckBox selectAll;
        readonly NumericUpDown position;
        readonly TextBox jointName;
        readonly ComboBox attachment, before, after;
        readonly Label scope, warning;
        readonly TextBox message;
        readonly ProgressBar saveBusy = new ProgressBar { Name = "JointSaveBusy", Dock = DockStyle.Bottom, Height = 6, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 0, Visible = false };
        readonly Button confirm;
        readonly Button remove;
        readonly DataGridView existing;
        int selectedBlock = -1, selectedAccessory = -1;
        double originalPosition;
        bool fillingExisting, existingPositionChanged, customAttachment;
        bool candidateReviewOnly, candidatePositionEdited;
        double candidateProposedPosition;
        string materialCatalogPath = JointMaterialCatalog.DefaultPath;
        readonly List<ContextMenuStrip> materialMenus = new List<ContextMenuStrip>();
        internal string OperationCode { get; private set; } = "AddJoint";
        internal Func<string, bool> ConfirmChange { get; set; }
        bool updating, updatingPosition, updatingName, customJointName, updatingMaterial, customBefore, customAfter;
        Func<string[], double> positionResolver;
        Action<CableJointPlan, string> commit;
        Func<CableJointPlan, string, CancellationToken, Task> commitAsync;
        CancellationTokenSource commitCancellation;
        bool commitPending;
        internal bool IsCommitPending { get { return commitPending; } }
        internal Action<Exception> DiagnosticError {get;set;}
        internal string LastDiagnosticError {get;private set;}
        static readonly Color Ink = Color.FromArgb(24, 51, 77), Muted = Color.FromArgb(96, 117, 138), Cyan = Color.FromArgb(0, 163, 199);

        internal CableJointPlan Plan { get; private set; }
        internal string[] SelectedPhases
        {
            get { return snapshot.Mode == CableJointMode.SharedThreeCore ? new[] { "ALL" } : phaseBoxes.Where(c => c.Checked).Select(c => (string)c.Tag).ToArray(); }
        }
        internal string AdditionalWarning { set { warning.Text = value ?? ""; } }

        internal CableJointDialog(CableJointSnapshot snapshot, double proposed, string preferredPhase, bool english, Icon icon)
            : this(snapshot, proposed, preferredPhase, english ? 1 : 0, icon) { }

        internal CableJointDialog(CableJointSnapshot snapshot, double proposed, string preferredPhase, int languageIndex, Icon icon)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            this.snapshot = snapshot;
            this.languageIndex=Xiongxin.OWTS.Localization.UiLanguage.ResolveIndex(languageIndex);
            this.english=!Xiongxin.OWTS.Localization.UiLanguage.IsChinese(this.languageIndex);
            Text = T("电缆接头 · 新增 / 编辑", "Cable joints · Add / edit"); Name = "CableJointDialog";
            Font = new Font("Microsoft YaHei UI", 9.5f); ForeColor = Ink; BackColor = Color.White;
            ClientSize = new Size(900, 780); MinimumSize = new Size(650, 600);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MaximizeBox = false; MinimizeBox = false; AutoScaleMode = AutoScaleMode.Dpi;
            if (icon != null) Icon = icon;
            else
            {
                Icon brand = LoadBrandIcon(); Icon = brand;
                Disposed += delegate { brand.Dispose(); };
            }

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, RowCount = 10, Padding = new Padding(18) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) body.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 55 : i == 6 ? 85 : 43));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            var phases = new FlowLayoutPanel { Name = "JointPhases", Dock = DockStyle.Fill, AutoSize = false, WrapContents = true };
            selectAll = new CheckBox { Name = "JointSelectAll", Text = T("全选", "All"), AutoSize = true, Margin = new Padding(0, 8, 16, 0) };
            if (snapshot.Mode == CableJointMode.SharedThreeCore)
                phases.Controls.Add(new Label { Text = T("三芯共用（不分相）", "Shared three-core cable (no phase split)"), AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            else
            {
                if (snapshot.PhaseChoices.Length > 1) phases.Controls.Add(selectAll);
                foreach (string phase in snapshot.PhaseChoices)
                {
                    int count = snapshot.Blocks.Count(b => b.Phase == phase);
                    string label = phase + (count > 1 ? T("（" + count + " 段）", " (" + count + " blocks)") : "");
                    var check = new CheckBox { Name = "JointPhase_" + phase, Tag = phase, Text = label, AutoSize = true,
                        Margin = new Padding(0, 8, 16, 0), Checked = snapshot.PhaseChoices.Length == 1 || String.Equals(preferredPhase, "ALL", StringComparison.OrdinalIgnoreCase) || String.Equals(phase, preferredPhase, StringComparison.OrdinalIgnoreCase),
                        Enabled = snapshot.PhaseChoices.Length > 1 };
                    phaseBoxes.Add(check); phases.Controls.Add(check);
                    check.CheckedChanged += delegate { PhaseSelectionChanged(); };
                }
            }
            selectAll.CheckedChanged += delegate
            {
                if (updating) return; updating = true;
                try { foreach (var check in phaseBoxes) check.Checked = selectAll.Checked; }
                finally { updating = false; }
                PhaseSelectionChanged();
            };
            position = new NumericUpDown { Name = "JointPosition", Dock = DockStyle.Fill, DecimalPlaces = 1, Minimum = 0, Maximum = 1000000, Increment = .1m };
            if (!Double.IsNaN(proposed) && !Double.IsInfinity(proposed)) position.Value = Math.Round((decimal)Math.Max(0, Math.Min(1000000, proposed)), 1, MidpointRounding.AwayFromZero);
            jointName = new TextBox { Name = "JointName", Dock = DockStyle.Fill, MaxLength = 80,
                Text = snapshot.SuggestedJointName(SelectedPhases, (double)position.Value) };
            jointName.TextChanged += delegate { if (!updatingName) customJointName = true; };
            attachment = Choices("JointAttachment", new[] { "中间接头", "冷缩接头", "热缩接头", "预制接头", "其他" },
                new[] { "Intermediate joint", "Cold-shrink joint", "Heat-shrink joint", "Prefabricated joint", "Other" });
            attachment.SelectedIndex = 0;
            attachment.TextChanged += delegate { if (!fillingExisting) customAttachment = true; };
            before = MaterialChoices("JointMaterialBefore"); after = MaterialChoices("JointMaterialAfter");
            before.TextChanged += delegate { if (!updatingMaterial) customBefore = true; };
            after.TextChanged += delegate { if (!updatingMaterial) customAfter = true; };
            scope = new Label { Name = "JointScope", Dock = DockStyle.Fill, ForeColor = Muted };
            Control[] fields = { phases, jointName, position, attachment, before, after, scope };
            string[] labels = { T("相别 / 写入范围", "Phases / write scope"), T("接头名称", "Joint name"), T("距项目始端位置 / m", "From project start / m"),
                T("附件类型", "Attachment type"), T("接头前材料", "Material before joint"), T("接头后材料", "Material after joint"), T("影响范围", "Affected structure") };
            for (int row = 0; row < fields.Length; row++)
            {
                body.Controls.Add(new Label { Text = labels[row], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                fields[row].Margin = new Padding(0, 5, 0, 5); body.Controls.Add(fields[row], 1, row);
            }
            var hint = new Label { Dock = DockStyle.Fill, ForeColor = Muted, Text = T(
                "前后材料留空：分别保留各相当前区段的材料，不会用某一相覆盖其他相。填写材料后，仅修改选中相内被此接头拆分的对应区段。",
                "Blank material fields preserve each selected phase's existing segment material. An explicit material applies only to the split segment in the selected phases.") };
            body.Controls.Add(hint, 0, 7); body.SetColumnSpan(hint, 2);
            warning = new Label { Dock = DockStyle.Fill, ForeColor = Muted, Text = T(
                "确认后修改项目电缆结构并保留修改前备份；原始波形不变。已有分析需要重新核对。关闭或取消本窗口不会修改项目。",
                "Confirm updates the project cable structure with a pre-edit backup. Raw waveforms are unchanged; existing analysis needs review. Closing or cancelling makes no project change.") };
            body.Controls.Add(warning, 0, 8); body.SetColumnSpan(warning, 2);
            var actions = new FlowLayoutPanel { Name="JointActions", Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            confirm = Button("ConfirmJoint", T("确认添加", "Confirm addition"), true);
            var cancel = Button("CancelJoint", T("取消", "Cancel"), false); cancel.DialogResult = DialogResult.Cancel;
            remove = Button("DeleteJoint", T("删除选中", "Delete selected"), false); remove.Enabled = false;
            var add = Button("NewJoint", T("新增接头", "New joint"), false);
            actions.Controls.Add(confirm); actions.Controls.Add(remove); actions.Controls.Add(add); actions.Controls.Add(cancel);
            body.RowStyles[9].Height = 0; body.RowStyles[9].SizeType = SizeType.Absolute;
            // Long source/transaction failures must remain readable and copyable.
            // This is presentation only: callbacks still publish the same text,
            // and the separately laid-out commit/cancel actions remain reachable.
            message = new TextBox { Name = "JointValidation", Dock = DockStyle.Bottom, Height = 45,
                ReadOnly = true, Multiline = true, WordWrap = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Color.Firebrick, ShortcutsEnabled = true };
            existing = new DataGridView { Name = "ExistingJoints", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
            existing.Columns.Add("Scope", T("范围 / 原始结构段", "Scope / source block")); existing.Columns.Add("Name", T("接头名称", "Joint name"));
            existing.Columns.Add("Position", T("位置 / m", "Position / m")); existing.Columns.Add("AttachmentType", T("附件类型", "Attachment type")); existing.Columns.Add("Before", T("接头前绝缘", "Before insulation")); existing.Columns.Add("After", T("接头后绝缘", "After insulation"));
            foreach (DataGridViewColumn column in existing.Columns) { column.MinimumWidth = 100; column.SortMode = DataGridViewColumnSortMode.NotSortable; }
            existing.Columns["Scope"].MinimumWidth = 140;
            var layout = new TableLayoutPanel { Name = "JointManagerLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 185)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            var editorViewport = new Panel { Name = "JointEditorViewport", Dock = DockStyle.Fill, AutoScroll = true };
            body.Dock = DockStyle.Top; body.Height = 530; editorViewport.Controls.Add(body);
            layout.Controls.Add(existing, 0, 0); layout.Controls.Add(editorViewport, 0, 1); layout.Controls.Add(actions, 0, 2);
            Controls.Add(layout); Controls.Add(message); Controls.Add(saveBusy); AcceptButton = confirm; CancelButton = cancel;
            bool arranging = false;
            Action arrange = delegate
            {
                if (arranging || IsDisposed) return; arranging = true;
                try
                {
                    foreach (DataGridViewColumn column in existing.Columns)
                        column.MinimumWidth = Math.Max(column.Name == "Scope" ? 140 : 100, TextRenderer.MeasureText(column.HeaderText, existing.Font).Width + 22);
                    int available = Math.Max(260, editorViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - body.Padding.Horizontal - 12);
                    int labelWidth = Math.Min(270, Math.Max(175, available * 38 / 100));
                    body.ColumnStyles[0].Width = labelWidth;
                    int valueWidth = Math.Max(90, available - labelWidth);
                    int height = body.Padding.Vertical;
                    for (int row = 0; row < 7; row++)
                    {
                        Control label = body.GetControlFromPosition(0, row);
                        int textHeight = TextRenderer.MeasureText(label.Text, label.Font, new Size(labelWidth - 6, 10000), TextFormatFlags.WordBreak).Height;
                        int fieldHeight = row == 6 ? TextRenderer.MeasureText(scope.Text, scope.Font, new Size(valueWidth, 10000), TextFormatFlags.WordBreak).Height :
                            row == 0 ? phases.GetPreferredSize(new Size(valueWidth, 0)).Height : Font.Height + 12;
                        int h = Math.Max(43, Math.Max(textHeight, fieldHeight) + 12);
                        body.RowStyles[row].SizeType = SizeType.Absolute; body.RowStyles[row].Height = h; height += h;
                    }
                    for (int row = 7; row <= 8; row++)
                    {
                        Control label = body.GetControlFromPosition(0, row);
                        int h = TextRenderer.MeasureText(label.Text, label.Font, new Size(available, 10000), TextFormatFlags.WordBreak).Height + 16;
                        body.RowStyles[row].SizeType = SizeType.Absolute; body.RowStyles[row].Height = h; height += h;
                    }
                    body.Height = height;
                    layout.RowStyles[2].Height = Math.Max(58, actions.GetPreferredSize(new Size(Math.Max(100, actions.ClientSize.Width), 0)).Height + 6);
                    message.Height=String.IsNullOrWhiteSpace(message.Text)?0:Math.Max(45,Font.Height*2+8);
                    layout.RowStyles[0].Height=Math.Min(185,Math.Max(Font.Height+30,layout.ClientSize.Height-(int)layout.RowStyles[2].Height-96-layout.Padding.Vertical));
                }
                finally { arranging = false; }
            };
            Shown += delegate { FitToWorkingArea(Screen.FromControl(Owner??this).WorkingArea); arrange(); };
            SizeChanged += delegate { arrange(); }; FontChanged += delegate { arrange(); }; scope.TextChanged += delegate { arrange(); };
            warning.TextChanged += delegate { arrange(); }; confirm.TextChanged += delegate { arrange(); }; message.TextChanged += delegate { arrange(); };
            confirm.Click += delegate { if (commitPending) return; string error; if (TryPrepare(out error)) SubmitPrepared(); else message.Text = error; };
            remove.Click += delegate { if (commitPending) return; string error; if (TryPrepareRemoval(out error)) SubmitPrepared(); else message.Text = error; };
            add.Click += delegate { BeginAddition(); };
            existing.SelectionChanged += delegate { if (!candidateReviewOnly && !fillingExisting && existing.SelectedRows.Count == 1) SelectExisting((ExistingJoint)existing.SelectedRows[0].Tag); };
            position.ValueChanged += delegate
            {
                if (candidateReviewOnly && !updatingPosition) candidatePositionEdited = true;
                if (selectedBlock >= 0 && !fillingExisting) existingPositionChanged = true;
                if (selectedBlock < 0 && !customJointName)
                {
                    updatingName = true;
                    try { jointName.Text = snapshot.SuggestedJointName(SelectedPhases, (double)position.Value); }
                    finally { updatingName = false; }
                }
                if (!updatingPosition) { message.Text = ""; confirm.Enabled = SelectedPhases.Length > 0; }
                if (selectedBlock < 0) RefreshMaterialDefaults();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (commitPending) {
                    e.Cancel = true; DialogResult = DialogResult.None; commitCancellation.Cancel();
                    message.Text = T("正在取消；若已进入文件提交，将等待保存结果。", "Cancelling; if file commit has started, wait for the save result.");
                } else if (DialogResult != DialogResult.OK) Plan = null;
            };
            Disposed += delegate { if (commitCancellation != null) commitCancellation.Cancel(); };
            FormClosed += delegate { foreach (var menu in materialMenus) menu.Dispose(); };
            LoadExistingRows();
            PhaseSelectionChanged();
            ConfigureMaterialCatalog(materialCatalogPath);
        }
        void FitToWorkingArea(Rectangle area)
        {
            int width=Math.Max(1,area.Width-24),height=Math.Max(1,area.Height-24);
            MinimumSize=new Size(Math.Min(650,width),Math.Min(600,height));
            Size=new Size(Math.Min(Width,width),Math.Min(Height,height));
            Location=new Point(Math.Max(area.Left+12,Math.Min(Left,area.Right-Width-12)),Math.Max(area.Top+12,Math.Min(Top,area.Bottom-Height-12)));
        }

        string T(string zh, string en) { return Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,zh,en); }
        string CustomerFailure(string operation,Exception error)
        {
            string diagnostic=operation+": "+error;
            LastDiagnosticError=diagnostic.Length<=16384?diagnostic:diagnostic.Substring(0,16384);
            var report=DiagnosticError;if(report!=null)try{report(error);}catch(Exception logError){System.Diagnostics.Trace.WriteLine("Joint diagnostic failed: "+logError);}
            if(error is CableJointCommittedException)
                return T("接头已保存，请勿重复提交；请核对项目，必要时联系维护人员。", "Joint saved. Do not submit again. Check the project and contact support if needed.");
            return T("操作未完成，请核对项目和输入参数后重试。", "Operation not completed. Check the project and input values, then retry.");
        }
        static Icon LoadBrandIcon()
        {
            return HVX.Presentation.OemUiBranding.LoadIcon();
        }
        ComboBox MaterialChoices(string name)
        { return Choices(name, new[] { "", "塑料绝缘", "交联聚乙烯", "油纸", "其他" }, new[] { "", "Plastic insulation", "XLPE", "Oil-impregnated paper", "Other" }); }
        ComboBox Choices(string name, string[] values, string[] captions)
        {
            var box = new ComboBox { Name = name, Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 80 };
            for (int i = 0; i < values.Length; i++) box.Items.Add(new Choice(values[i], T(values[i],captions[i])));
            return box;
        }
        static string Value(ComboBox box)
        {
            var choice = box.Items.Cast<Choice>().FirstOrDefault(c => String.Equals(c.Caption, box.Text.Trim(), StringComparison.Ordinal));
            return choice == null ? box.Text.Trim() : choice.Value;
        }
        static Button Button(string name, string text, bool primary)
        {
            var button = new HVX.Presentation.R2RoundedButton { Name = name, Text = text, AutoSize = true, MinimumSize = new Size(primary ? 138 : 90, 36), FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Cyan : Color.White, ForeColor = primary ? Color.White : Ink, UseVisualStyleBackColor = false, Margin = new Padding(8, 7, 0, 0) };
            button.FlatAppearance.BorderColor = Color.FromArgb(202, 216, 229); return button;
        }
        internal void ConfigurePositionResolver(Func<string[], double> resolver)
        { positionResolver = resolver; PhaseSelectionChanged(); }
        internal void ConfigureCommit(Action<CableJointPlan, string> callback) { commit = callback; commitAsync = null; }
        internal void ConfigureCommitAsync(Func<CableJointPlan, string, CancellationToken, Task> callback) { commitAsync = callback; commit = null; }
        async void SubmitPrepared()
        {
            if (commitPending) return;
            if (SynchronizationContext.Current == null || SynchronizationContext.Current.GetType() == typeof(SynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (commitAsync == null) {
                try { if (commit != null) commit(Plan, OperationCode); DialogResult = DialogResult.OK; Close(); }
                catch (Exception error) { Plan = null; message.Text = CustomerFailure("Joint save",error); }
                return;
            }
            // Disable only editor inputs, not Cancel or the message pump. The
            // callback defines the commit point; a late cancellation cannot turn
            // an already saved edit into a misleading 'not saved' result.
            var disabled = new List<Control>();
            foreach (Control control in EnumerateInputs(this))
                if (control.Enabled && control.Name != "CancelJoint") { disabled.Add(control); control.Enabled = false; }
            commitPending = true; commitCancellation = new CancellationTokenSource();
            saveBusy.Visible = true; saveBusy.MarqueeAnimationSpeed = 30;
            message.Text = T("正在校验来源并保存，可取消尚未提交的操作…", "Validating sources and saving; work not yet committed can be cancelled…");
            bool completed = false;
            try { await commitAsync(Plan, OperationCode, commitCancellation.Token); completed = true; }
            catch (OperationCanceledException) { Plan = null; if (!IsDisposed) message.Text = T("已取消，未保存。", "Cancelled; nothing was saved."); }
            catch (Exception error) { Plan = null; string notice=CustomerFailure("Joint save",error); if (!IsDisposed) message.Text=notice; }
            finally {
                commitPending = false; commitCancellation.Dispose(); commitCancellation = null;
                if (!IsDisposed) { saveBusy.MarqueeAnimationSpeed = 0; saveBusy.Visible = false; }
                if (!IsDisposed) foreach (Control control in disabled) if (!control.IsDisposed) control.Enabled = true;
            }
            if (completed && !IsDisposed) { DialogResult = DialogResult.OK; Close(); }
        }
        static IEnumerable<Control> EnumerateInputs(Control parent)
        {
            foreach (Control child in parent.Controls) {
                var text = child as TextBox; if (text != null && text.ReadOnly) continue;
                if (child is Button || child is CheckBox || child is TextBox || child is ComboBox || child is NumericUpDown || child is DataGridView) yield return child;
                else foreach (Control nested in EnumerateInputs(child)) yield return nested;
            }
        }
        internal void ConfigureMaterialCatalog(string path)
        {
            materialCatalogPath = String.IsNullOrWhiteSpace(path) ? JointMaterialCatalog.DefaultPath : path;
            RefreshMaterialCatalog();
            if (materialMenus.Count > 0) return;
            var menu = new ContextMenuStrip(); materialMenus.Add(menu);
            menu.Items.Add(T("新增 / 更新材料…", "Add / update material…"), null, delegate
            {
                var box = menu.SourceControl as ComboBox ?? before;
                using (var editor = new Form { Name = "JointMaterialEditor", Text = T("材料与建议波速", "Material and suggested velocity"), Font = Font, StartPosition = FormStartPosition.CenterParent,
                    ClientSize = new Size(500, 230), MinimumSize = new Size(500, 270), ShowInTaskbar = false, Icon = Icon, MinimizeBox = false, MaximizeBox = false })
                {
                    var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 3 };
                    grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
                    grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    var name = new TextBox { Name = "MaterialName", Dock = DockStyle.Fill, MaxLength = 80, Text = Value(box) };
                    var velocity = new NumericUpDown { Name = "MaterialVelocity", Dock = DockStyle.Fill, DecimalPlaces = 2, Minimum = .01m, Maximum = 1000000, Value = 170 };
                    try { var entry = JointMaterialCatalog.Read(materialCatalogPath).Entries.FirstOrDefault(e => e.Name == name.Text); if (entry != null && entry.Velocity <= 1000000) velocity.Value = (decimal)entry.Velocity; } catch { }
                    grid.Controls.Add(new Label { Text = T("材料名称", "Material name"), AutoSize = true, Dock = DockStyle.Fill }, 0, 0); grid.Controls.Add(name, 1, 0);
                    grid.Controls.Add(new Label { Text = T("建议波速 / m/μs", "Velocity / m/μs"), AutoSize = true, Dock = DockStyle.Fill }, 0, 1); grid.Controls.Add(velocity, 1, 1);
                    var ok = Button("SaveMaterial", T("保存材料", "Save material"), true); var cancel = Button("CancelMaterial", T("取消", "Cancel"), false); cancel.DialogResult = DialogResult.Cancel;
                    grid.Controls.Add(cancel, 0, 2); grid.Controls.Add(ok, 1, 2); editor.Controls.Add(grid); editor.CancelButton = cancel; editor.AcceptButton = ok;
                    // Size from the inherited font and translated labels, not
                    // fixed pixel columns or percentage-height input rows.
                    int labelWidth=grid.Controls.OfType<Label>().Select(c=>TextRenderer.MeasureText(c.Text,c.Font).Width).DefaultIfEmpty(180).Max();
                    int buttonWidth=new[]{ok,cancel}.Select(c=>TextRenderer.MeasureText(c.Text,c.Font).Width+30).Max();
                    var minimumClient=new Size(Math.Max(500,Math.Max(labelWidth,buttonWidth)*2+52),Math.Max(230,(editor.Font.Height+24)*3+36));
                    editor.ClientSize=minimumClient;editor.MinimumSize=editor.Size;
                    ok.Click += delegate
                    {
                        bool overwrite = false;
                        try { overwrite = JointMaterialCatalog.Read(materialCatalogPath).Entries.Any(e => String.Equals(e.Name, name.Text.Trim(), StringComparison.OrdinalIgnoreCase)); } catch { }
                        if (overwrite && MessageBox.Show(editor, T("同名材料已存在，更新建议波速？已有项目不变。", "Update this material's suggested velocity? Existing projects remain unchanged."), editor.Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                        string error; if (TrySaveMaterial(name.Text.Trim(), (double)velocity.Value, overwrite, out error)) { box.Text = name.Text.Trim(); editor.DialogResult = DialogResult.OK; editor.Close(); }
                        else MessageBox.Show(editor, error, editor.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    };
                    editor.ShowDialog(this);
                }
            });
            menu.Items.Add(T("从字典删除当前材料", "Remove current material from catalog"), null, delegate
            {
                var box = menu.SourceControl as ComboBox ?? before;
                if (MessageBox.Show(this, T("仅删除字典条目，不改变已有项目材料。确认删除：", "Remove from the catalog only; existing project materials remain unchanged: ") + Value(box), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                string error; if (!TryDeleteMaterial(Value(box), out error)) message.Text = error;
            });
            before.ContextMenuStrip = after.ContextMenuStrip = menu;
            before.DropDown += delegate { RefreshMaterialCatalog(); }; after.DropDown += delegate { RefreshMaterialCatalog(); };
        }
        void RefreshMaterialCatalog()
        {
            try
            {
                var catalog = JointMaterialCatalog.Read(materialCatalogPath);
                updatingMaterial = true;
                try { foreach (ComboBox box in new[] { before, after }) { string value = Value(box); box.Items.Clear(); box.Items.Add(new Choice("", "")); foreach (var entry in catalog.Entries) box.Items.Add(new Choice(entry.Name, entry.Name)); box.Text = value; } }
                finally { updatingMaterial = false; }
            }
            catch (Exception error) { message.Text = CustomerFailure("Material catalog read",error); }
        }
        internal bool TrySaveMaterial(string name, double velocity, bool replace, out string error)
        {
            error = "";
            try
            {
                var catalog = JointMaterialCatalog.Read(materialCatalogPath);
                var entry = catalog.Entries.FirstOrDefault(e => String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (entry != null && !replace) throw new InvalidOperationException(T("材料已存在，请明确确认更新。", "Material exists; explicitly confirm an update."));
                if (entry == null) catalog.Entries.Add(new JointMaterialEntry { Name = name, Velocity = velocity }); else entry.Velocity = velocity;
                catalog.Save(); RefreshMaterialCatalog(); return true;
            }
            catch (Exception e) { error=CustomerFailure("Material save",e); return false; }
        }
        internal bool TryDeleteMaterial(string name, out string error)
        {
            error = "";
            try
            {
                var catalog = JointMaterialCatalog.Read(materialCatalogPath);
                if (catalog.Entries.RemoveAll(e => String.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) == 0) throw new InvalidOperationException(T("当前材料不在字典中，项目记录未改。", "This project material is not in the catalog; project unchanged."));
                catalog.Save(); RefreshMaterialCatalog(); return true;
            }
            catch (Exception e) { error=CustomerFailure("Material removal",e); return false; }
        }
        internal void ConfigureMaterials(string beforeMaterial, string afterMaterial)
        {
            updatingMaterial = true;
            try
            {
                if (!String.IsNullOrWhiteSpace(beforeMaterial)) { before.Text = beforeMaterial.Trim(); customBefore = true; }
                if (!String.IsNullOrWhiteSpace(afterMaterial)) { after.Text = afterMaterial.Trim(); customAfter = true; }
            }
            finally { updatingMaterial = false; }
        }
        // Candidate review is an addition only. The 0.1 m presentation must not
        // silently alter an unedited measurement, or select an existing joint.
        internal void ConfigureCandidateReview(double proposed, bool requirePhaseSelection)
        {
            if (Double.IsNaN(proposed) || Double.IsInfinity(proposed) || proposed <= 0)
                throw new ArgumentException(T("接头候选位置无效。", "Invalid candidate joint position."));
            candidateProposedPosition = proposed; candidatePositionEdited = false;
            candidateReviewOnly = true;
            selectedBlock = selectedAccessory = -1; OperationCode = "AddJoint";
            existing.Enabled = false; existing.ClearSelection(); remove.Enabled = false;
            var add = Controls.Find("NewJoint", true).FirstOrDefault(); if (add != null) add.Enabled = false;
            // A candidate's confirmation must not renumber an existing manually
            // confirmed joint merely because both use generated-looking names.
            customJointName = true;
            if (requirePhaseSelection) {
                updating = true;
                try { selectAll.Checked = false; foreach (var box in phaseBoxes) box.Checked = false; }
                finally { updating = false; }
                PhaseSelectionChanged();
            }
            Text = T("核对接头候选", "Review joint candidate"); confirm.Text = T("确认此接头", "Confirm this joint");
            RefreshMaterialDefaults();
        }
        double AdditionPosition { get { return candidateReviewOnly && !candidatePositionEdited ? candidateProposedPosition : (double)position.Value; } }
        void PhaseSelectionChanged()
        {
            if (updating || position == null) return;
            if (selectedBlock >= 0) return;
            string[] phases = SelectedPhases;
            updating = true; selectAll.Checked = phaseBoxes.Count > 0 && phaseBoxes.All(c => c.Checked); updating = false;
            int affected = snapshot.Mode == CableJointMode.SharedThreeCore ? snapshot.Blocks.Count : snapshot.Blocks.Count(b => phases.Contains(b.Phase));
            var selectedBlocks = snapshot.Blocks.Where(b => snapshot.Mode == CableJointMode.SharedThreeCore || phases.Contains(b.Phase)).ToArray();
            // Changing A to B commonly passes through a moment with no boxes
            // checked. Disable submission then, but do not erase the entered position.
            if (selectedBlocks.Length > 0) {
                bool wasUpdating = updatingPosition; updatingPosition = true;
                try { position.Maximum = Math.Max(0, Math.Min(1000000m,
                    Decimal.Floor((decimal)selectedBlocks.Min(b => b.Length) * 10m) / 10m)); }
                finally { updatingPosition = wasUpdating; }
            }
            string description = english ? (snapshot.Mode == CableJointMode.SharedThreeCore ? "Shared three-core structure. " :
                snapshot.Mode == CableJointMode.SinglePhase ? "Single-phase structure. " : snapshot.Mode == CableJointMode.Unknown ? "Uncertain load type; use actual file phases only. " : "Select actual phases. ") +
                "All blocks bearing a selected phase label are affected; phase headers are not renamed." : T(snapshot.ScopeDescription,snapshot.ScopeDescription);
            scope.Text = T("本次：", "Selected: ") + String.Join(" / ", phases) + T("，共 " + affected + " 段。\n", ", " + affected + " blocks.\n") + description;
            message.Text = phases.Length == 0 ? T("请至少选择一个实际相别。", "Select at least one existing phase.") : "";
            confirm.Enabled = phases.Length > 0;
            if (positionResolver != null && phases.Length > 0)
            {
                try
                {
                    double value = positionResolver(phases);
                    if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > (double)position.Maximum) throw new InvalidOperationException(T("位置转换无效或超出所选电缆范围，请直接输入距项目始端的位置。", "Invalid or out-of-range coordinate conversion. Enter distance from the project start."));
                    updatingPosition = true; position.Value = Math.Round((decimal)value, 1, MidpointRounding.AwayFromZero); updatingPosition = false;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.IO.InvalidDataException)
                { updatingPosition = false; confirm.Enabled = false; message.Text=CustomerFailure("Joint position conversion",ex); }
            }
            if (!customJointName)
            { updatingName = true; try { jointName.Text = snapshot.SuggestedJointName(phases, (double)position.Value); } finally { updatingName = false; } }
            RefreshMaterialDefaults();
        }
        void RefreshMaterialDefaults()
        {
            if (before == null || after == null) return;
            string[] materials = snapshot.InsertionMaterials(SelectedPhases, AdditionPosition);
            string material = materials.Length == 1 ? materials[0] : "";
            updatingMaterial = true;
            try
            {
                foreach (var box in new[] { before, after })
                {
                    if (box == before ? customBefore : customAfter) continue;
                    var item = box.Items.Cast<Choice>().FirstOrDefault(c => c.Value == material);
                    if (item != null) box.SelectedItem = item; else { box.SelectedIndex = -1; box.Text = material; }
                }
            }
            finally { updatingMaterial = false; }
        }
        internal bool TryPrepare(out string error)
        {
            Plan = null; error = "";
            try
            {
                if (!confirm.Enabled) throw new InvalidOperationException(message.Text);
                if (selectedBlock >= 0)
                {
                    if (candidateReviewOnly) throw new InvalidOperationException(T("候选确认不能修改已有接头。", "Candidate review cannot edit an existing joint."));
                    double value = existingPositionChanged ? (double)position.Value : originalPosition;
                    var prepared = snapshot.PrepareEdit(selectedBlock, selectedAccessory, value, customJointName ? jointName.Text.Trim() : "",
                        customAttachment ? Value(attachment) : "", customBefore ? Value(before) : "", customAfter ? Value(after) : "", true);
                    if (!ConfirmExisting(false, value)) { error = T("已取消，项目未修改。", "Cancelled; project not changed."); return false; }
                    Plan = prepared; OperationCode = "EditJoint";
                }
                else
                {
                    Plan = candidateReviewOnly ? snapshot.PrepareCandidateWithNamePolicy(SelectedPhases, AdditionPosition, jointName.Text.Trim(), Value(attachment),
                        customBefore ? Value(before) : "", customAfter ? Value(after) : "", !customJointName) :
                        snapshot.PrepareWithNamePolicy(SelectedPhases, (double)position.Value, jointName.Text.Trim(), Value(attachment),
                        customBefore ? Value(before) : "", customAfter ? Value(after) : "", !customJointName);
                    OperationCode = "AddJoint";
                }
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.IO.InvalidDataException || ex is System.IO.IOException)
            { error=CustomerFailure("Joint input validation",ex); return false; }
        }
        internal bool TryPrepareRemoval(out string error)
        {
            Plan = null; error = "";
            try
            {
                if (candidateReviewOnly) throw new InvalidOperationException(T("候选确认不能删除已有接头。", "Candidate review cannot remove an existing joint."));
                if (selectedBlock < 0) throw new InvalidOperationException(T("请先选择既有接头。", "Select an existing joint first."));
                var prepared = snapshot.PrepareRemove(selectedBlock, selectedAccessory, true);
                if (!ConfirmExisting(true, originalPosition)) { error = T("已取消，项目未修改。", "Cancelled; project not changed."); return false; }
                Plan = prepared; OperationCode = "RemoveJoint"; return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.IO.IOException) { error=CustomerFailure("Joint removal validation",ex); return false; }
        }
        bool ConfirmExisting(bool deleting, double nextPosition)
        {
            var targets = snapshot.ExistingTargets(selectedBlock, selectedAccessory);
            string text = T(deleting ? "确认删除选中接头？" : "确认保存接头修改？", deleting ? "Delete the selected joint?" : "Save the joint changes?") + Environment.NewLine +
                String.Join(Environment.NewLine, targets.Select(t => t.Phase + " / " + T("结构段 ", "block ") + (t.BlockIndex + 1) + " · " + t.Name + " · " + t.PositionM.ToString("R", CultureInfo.InvariantCulture) + " m · " + t.MaterialBefore + " → " + t.MaterialAfter));
            if (!deleting) text += Environment.NewLine + T("新位置：", "New position: ") + nextPosition.ToString("R", CultureInfo.InvariantCulture) + " m";
            if (!deleting && customJointName) text += Environment.NewLine + T("接头名称：", "Joint name: ") + jointName.Text.Trim();
            if (!deleting && customAttachment) text += Environment.NewLine + T("附件类型：", "Attachment type: ") + Value(attachment);
            if (deleting || Math.Abs(nextPosition - originalPosition) > .0000001)
                text += Environment.NewLine + T("原位置合并后保留前段材料；移动时接头后材料随接头移动。", "At the original position, merging retains the preceding material. A moved joint carries its following material.");
            if (!deleting && customBefore) text += Environment.NewLine + T("新位置前绝缘：", "Before insulation at new position: ") + Value(before);
            if (!deleting && customAfter) text += Environment.NewLine + T("后绝缘：", "After insulation: ") + Value(after);
            text += Environment.NewLine + T("原相别、总长度及原始波形不变；保存备份，已有分析需重新核对。", "Original phase labels, total lengths and raw waveforms are unchanged. A backup is retained; review existing analysis.");
            return ConfirmChange != null ? ConfirmChange(text) : MessageBox.Show(this, text, T("确认接头变更", "Confirm joint change"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }
        sealed class ExistingJoint { internal int Block, Accessory; }
        void LoadExistingRows()
        {
            fillingExisting = true;
            try
            {
                bool common = snapshot.Mode == CableJointMode.SharedThreeCore && snapshot.Blocks.All(b => b.Positions.SequenceEqual(snapshot.Blocks[0].Positions));
                foreach (var block in snapshot.Blocks)
                {
                    if (common && block.Index != 0) continue;
                    for (int i = 1; i < block.Positions.Count - 1; i++)
                    {
                        int index = i;
                        var blocks = common ? snapshot.Blocks.ToArray() : new[] { block };
                        Func<Func<CableJointBlock,string>, string> summary = f => { string[] values = blocks.Select(f).Distinct().ToArray(); return values.Length == 1 ? values[0] : T("各结构段不同", "Varies by block"); };
                        string scopeText = common ? T("ALL · 共用", "ALL · shared") : block.Phase + " · " + T("结构段 ", "block ") + (block.Index + 1);
                        int r = existing.Rows.Add(scopeText, summary(b => snapshot.Lines[b.AccessoryLines[index]].Split('\t')[1]), block.Positions[i].ToString("0.0", CultureInfo.InvariantCulture), summary(b => snapshot.Lines[b.AccessoryLines[index]].Split('\t')[2]),
                            summary(b => snapshot.Lines[b.BodyLines[index - 1]].Split('\t')[2]), summary(b => snapshot.Lines[b.BodyLines[index]].Split('\t')[2]));
                        existing.Rows[r].Tag = new ExistingJoint { Block = block.Index, Accessory = i };
                        string details = String.Join(Environment.NewLine, blocks.Select(b => b.Phase + " / " + (b.Index + 1) + ": " + snapshot.Lines[b.AccessoryLines[index]].Split('\t')[1] + "; " + snapshot.Lines[b.BodyLines[index - 1]].Split('\t')[2] + " → " + snapshot.Lines[b.BodyLines[index]].Split('\t')[2]));
                        foreach (DataGridViewCell cell in existing.Rows[r].Cells) cell.ToolTipText = details;
                    }
                }
                existing.ClearSelection();
            }
            finally { fillingExisting = false; }
        }
        void SelectExisting(ExistingJoint row)
        {
            fillingExisting = updatingMaterial = updatingName = true;
            try
            {
                selectedBlock = row.Block; selectedAccessory = row.Accessory;
                var block = snapshot.Blocks[row.Block]; string[] values = snapshot.Lines[block.AccessoryLines[row.Accessory]].Split('\t');
                originalPosition = block.Positions[row.Accessory]; position.Maximum = (decimal)Math.Min(1000000, block.Length); position.Value = (decimal)originalPosition;
                jointName.Text = values[1]; attachment.Text = values[2];
                var targets = snapshot.ExistingTargets(row.Block, row.Accessory);
                before.Text = targets.Select(t => t.MaterialBefore).Distinct().Count() == 1 ? targets[0].MaterialBefore : "";
                after.Text = targets.Select(t => t.MaterialAfter).Distinct().Count() == 1 ? targets[0].MaterialAfter : "";
                scope.Text = String.Join(" / ", targets.Select(t => t.Phase + " · " + T("结构段 ", "block ") + (t.BlockIndex + 1)));
                confirm.Enabled = remove.Enabled = true; message.Text = "";
            }
            catch (Exception error) { confirm.Enabled = remove.Enabled = false; message.Text=CustomerFailure("Joint selection",error); }
            finally
            {
                customJointName = customBefore = customAfter = customAttachment = existingPositionChanged = false;
                fillingExisting = updatingMaterial = updatingName = false;
                foreach (var box in phaseBoxes) box.Enabled = false; selectAll.Enabled = false;
                confirm.Text = T("保存修改", "Save changes"); OperationCode = "EditJoint";
            }
        }
        void BeginAddition()
        {
            if (candidateReviewOnly) return;
            fillingExisting = true; try { existing.ClearSelection(); } finally { fillingExisting = false; }
            selectedBlock = selectedAccessory = -1; OperationCode = "AddJoint"; Plan = null;
            customJointName = customBefore = customAfter = customAttachment = existingPositionChanged = false;
            foreach (var box in phaseBoxes) box.Enabled = snapshot.PhaseChoices.Length > 1; selectAll.Enabled = true;
            remove.Enabled = false; confirm.Text = T("确认添加", "Confirm addition"); PhaseSelectionChanged();
        }
        sealed class Choice
        {
            internal readonly string Value, Caption;
            internal Choice(string value, string caption) { Value = value; Caption = caption; }
            public override string ToString() { return Caption; }
        }
    }
}
