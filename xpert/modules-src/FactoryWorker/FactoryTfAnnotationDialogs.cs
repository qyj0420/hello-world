using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryTfAnnotationEditDialog : Form
    {
        private readonly TextBox name = new TextBox { MaxLength = 80 }, defect = new TextBox { MaxLength = 200 }, source = new TextBox { MaxLength = 500 },
            author = new TextBox { MaxLength = 80 }, notes = new TextBox { MaxLength = 4000, Multiline = true, ScrollBars = ScrollBars.Vertical };
        private readonly ComboBox kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly CheckBox filtering = new CheckBox { Text = "作为干扰模板：仅严格匹配的派生统计参与过滤；原始数据/保护不变", AutoSize = true };
        private readonly FactoryTfAnnotationRegion original;
        internal FactoryTfAnnotationRegion Value
        {
            get { return new FactoryTfAnnotationRegion { Id = original.Id, CreatedUtc = original.CreatedUtc, ModifiedUtc = DateTime.UtcNow,
                Name = name.Text.Trim(), Kind = (FactoryTfLabelKind)kind.SelectedIndex, Defect = defect.Text.Trim(), PossibleInterferenceSource = source.Text.Trim(), Author = author.Text.Trim(), Notes = notes.Text.Trim(),
                FilterInterference = filtering.Checked && kind.SelectedIndex == (int)FactoryTfLabelKind.SuspectedInterference, MarkedSurface = original.MarkedSurface }; }
        }
        internal FactoryTfAnnotationEditDialog(Icon icon, FactoryTfRegionGeometry geometry, int count, FactoryTfAnnotationRegion initial, string recognition = "未识别", bool editing = false)
        {
            original = initial; Text = "雄新电力科技 · " + (editing ? "修改已有标签" : "TF人工标注"); Icon = icon; Font = new Font("Microsoft YaHei UI", 10);
            Size = new Size(760, 710); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 9 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 108, 44, 44, 44, 60, 44 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var heading = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = (editing ? "修改库内标签 · 保存更新此标签及其模板关联；取消不修改。" : initial.MarkedSurface + "标记 · " + count + " 个有效TF脉冲") +
                "\n自动识别：" + recognition + "\nT、F相对偏差及归一化原波、Hilbert包络逐点最大差异均≤1%；保存为人工标签，不是诊断结论。", ForeColor = Color.DarkSlateGray };
            grid.Controls.Add(heading, 0, 0); grid.SetColumnSpan(heading, 2);
            kind.Items.AddRange(new object[] { "待确定", "疑似缺陷", "疑似干扰", "其他人工标签" }); kind.SelectedIndex = (int)initial.Kind;
            filtering.Checked = initial.FilterInterference; filtering.Enabled = kind.SelectedIndex == 2;
            kind.SelectedIndexChanged += (s,e) => { filtering.Enabled = kind.SelectedIndex == 2; if (!filtering.Enabled) filtering.Checked = false; };
            name.Text = initial.Name; defect.Text = initial.Defect; source.Text = initial.PossibleInterferenceSource; notes.Text = initial.Notes; author.Text = initial.Author;
            Add(grid, "区域名称（必填）", name, 1); Add(grid, "人工标签类别", kind, 2); Add(grid, "疑似缺陷 / 现象", defect, 3);
            source.Multiline = true; Add(grid, "可能的干扰源", source, 4); Add(grid, "标注人（可选）", author, 5); Add(grid, "依据 / 备注", notes, 6);
            var bounds = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "F=" + geometry.MinimumFrequencyMHz.ToString("G5") + "～" + geometry.MaximumFrequencyMHz.ToString("G5") +
                " MHz；T=" + geometry.MinimumTimeUs.ToString("G5") + "～" + geometry.MaximumTimeUs.ToString("G5") + " µs。坐标保留，不因窗口大小变化而重圈。" };
            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true }; filterRow.Controls.Add(filtering);
            grid.Controls.Add(filterRow, 0, 7); grid.SetColumnSpan(filterRow, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var confirm = new Button { Text = "保存", Width = 110, Height = 34 }; var cancel = new Button { Text = "取消", Width = 90, Height = 34, DialogResult = DialogResult.Cancel };
            confirm.Click += (s,e) => { try { Value.ValidateText(); DialogResult = DialogResult.OK; } catch (Exception ex) { MessageBox.Show(this, ex.Message, "标注内容", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
            actions.Controls.Add(confirm); actions.Controls.Add(cancel); grid.Controls.Add(actions, 0, 8); grid.SetColumnSpan(actions, 2);
            Controls.Add(grid); AcceptButton = confirm; CancelButton = cancel;
        }
        private static void Add(TableLayoutPanel grid, string title, Control input, int row)
        { input.Dock = DockStyle.Fill; grid.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) }, 0, row); grid.Controls.Add(input, 1, row); }
    }
    internal sealed class FactoryTfAnnotationWindow : Form
    {
        private readonly FactoryTfAnnotationDocument document;
        private readonly DataGridView table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            MultiSelect = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        private readonly FactoryTfPlot plot = new FactoryTfPlot { Dock = DockStyle.Fill };
        private readonly FactoryWaveformControl pulse = new FactoryWaveformControl { Dock = DockStyle.Fill, Title = "历史脉冲 · 原始记录读取" };
        private readonly TextBox details = new TextBox { ReadOnly = true, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        private readonly Label notice = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DarkSlateGray };
        private readonly ComboBox draw = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        private readonly Action changed;
        private readonly Action<IWin32Window, FactoryTfAnnotationDocument, FactoryTfAnnotationRegion> useReference;
        private FactoryPulseRawCache cache;
        private FactoryTfDisplayPoint historyPoint;
        private System.Collections.Generic.Dictionary<string, FactoryPulseMatch> historyMatches = new System.Collections.Generic.Dictionary<string, FactoryPulseMatch>();
        private bool refreshing;
        internal bool SuppressClosePrompt;
        internal int VisibleRegions { get { return plot.VisibleRegionCount; } }
        internal FactoryTfAnnotationWindow(Icon icon, FactoryTfAnnotationDocument doc, Action changed,
            Action<IWin32Window, FactoryTfAnnotationDocument, FactoryTfAnnotationRegion> useReference,
            Action<IWin32Window,string,string> optionalNotice=null)
        {
            document = doc; this.changed = changed; this.useReference = useReference;
            Text = "雄新电力科技 · TF标注与历史浏览"; Icon = icon; Font = new Font("Microsoft YaHei UI", 9);
            Size = new Size(1180, 820); MinimumSize = new Size(940, 650); StartPosition = FormStartPosition.CenterParent;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
            draw.Items.AddRange(new object[] { "浏览", "矩形圈选", "圆 / 椭圆" }); draw.SelectedIndex = 0; draw.SelectedIndexChanged += (s,e) => plot.DrawingMode = (FactoryTfDrawMode)draw.SelectedIndex;
            Button edit = Btn("编辑标注"), remove = Btn("移除标注"), save = Btn("保存标注文件"), reference = Btn("用于本轮参考");
            actions.Controls.Add(draw); actions.Controls.Add(edit); actions.Controls.Add(remove); actions.Controls.Add(save); actions.Controls.Add(reference);
            actions.Controls.Add(new Label { Text = "仅历史浏览 / 人工标注，不访问硬件", AutoSize = true, Margin = new Padding(6, 10, 0, 0) });
            root.Controls.Add(actions, 0, 0);
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 650), SplitterDistance = 420, Panel1MinSize = 300, Panel2MinSize = 420, SplitterWidth = 8 };
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 58)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 42)); left.Controls.Add(table, 0, 0); left.Controls.Add(details, 0, 1); split.Panel1.Controls.Add(left);
            var right = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(650, 650), Orientation = Orientation.Horizontal, SplitterDistance = 350, Panel1MinSize = 160, Panel2MinSize = 150, SplitterWidth = 8 };
            right.Panel1.Controls.Add(plot); pulse.WrapToolbar = true; right.Panel2.Controls.Add(pulse); split.Panel2.Controls.Add(right); root.Controls.Add(split, 0, 1); root.Controls.Add(notice, 0, 2); Controls.Add(root);
            foreach (string title in new[] { "区域名称", "类别", "形状", "脉冲数" }) table.Columns.Add(title, title);
            table.SelectionChanged += (s,e) => { if (!refreshing) ShowRegion(); };
            plot.PulseClicked += (s,e) =>
            {
                historyPoint = e; plot.SetSelected(e);
                try
                {
                    var samples = FactoryTfPulseReader.Read(e, null, ref cache);
                    pulse.SetPulse(samples, e.Round.PicoRateHz, e.Round.CoefficientPcPerV);
                    pulse.SetPulseEnvelope(FactoryPulseLabels.Envelope(samples, e.Round.BaselineV), e.Round.CoefficientPcPerV);
                    var match = HistoryMatch(e); pulse.LabelStatus = match.Text; pulse.LabelCaption = match.Matched ? match.Label.Name : match.Conflict ? "标签冲突" : "未识别";
                    pulse.Title = "历史峰时 " + e.Pulse.TimeMs.ToString("0.######") + " ms";
                }
                catch (Exception ex) { pulse.ClearWave(); pulse.Title = "对应原始波形不可用"; notice.Text = ex.Message + "；TF快照与人工标注仍可查看。"; }
            };
            plot.LabelEditRequested += (s,e) => EditRegion(e.Region);
            pulse.ConfigurePulseLabelMenu(null, null, () => FactoryPulseLabels.EditableLabels(document, new[] { historyPoint }, HistoryMatch), EditRegion);
            plot.RegionSelected += (s,e) =>
            {
                if (e.Points.Count == 0) { notice.Text = "圈选内没有有效TF点，未记录空区域。"; draw.SelectedIndex = 0; return; }
                var recognized = new System.Collections.Generic.List<FactoryPulseMatch>();
                try
                {
                    foreach (var point in e.Points) recognized.Add(FactoryPulseLabels.Match(document, point, FactoryPulseLabels.Create(point, FactoryTfPulseReader.Read(point, null, ref cache))));
                    using (var dialog = new FactoryTfAnnotationEditDialog(Icon, e.Geometry, e.Points.Count, new FactoryTfAnnotationRegion { Name = "区域 " + (document.Regions.Count + 1) }, FactoryPulseLabels.Selection(recognized).Text))
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    { var text = dialog.Value; text.Templates = e.Points.Select(point => FactoryPulseLabels.Create(point, FactoryTfPulseReader.Read(point, null, ref cache))).ToList(); var added = document.Add(e.Geometry, e.Points, text); RefreshRows(added.Id); if (changed != null) changed(); }
                }
                catch (Exception ex) { notice.Text = "原始脉冲不可用，不能建立识别模板：" + ex.Message; }
                draw.SelectedIndex = 0;
            };
            edit.Click += (s,e) =>
                EditRegion(SelectedRegion);
            remove.Click += (s,e) =>
            {
                var region = SelectedRegion; if (region == null || MessageBox.Show(this, "从标签库移除“" + region.Name + "”？原始数据不会删除；主库会自动保存，旧库备份可恢复。", "移除人工标注", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                document.Remove(region); RefreshRows(null); if (changed != null) changed();
            };
            save.Click += (s,e) => SaveFile(this, document, optionalNotice);
            reference.Click += (s,e) => { if (SelectedRegion != null && this.useReference != null) this.useReference(this, document, SelectedRegion); };
            reference.Enabled = useReference != null;
            FormClosing += (s,e) =>
            {
                if (SuppressClosePrompt || !document.Dirty) return;
                var answer = MessageBox.Show(this, "标注有未保存变更。是否保存到文件？选择“否”不写入文件。", "保存人工标注", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveFile(this, document, optionalNotice))) e.Cancel = true;
            };
            RefreshRows(null);
        }
        private FactoryTfAnnotationRegion SelectedRegion
        { get { return table.SelectedRows.Count == 0 ? null : table.SelectedRows[0].Tag as FactoryTfAnnotationRegion; } }
        private FactoryPulseMatch HistoryMatch(FactoryTfDisplayPoint point)
        { FactoryPulseMatch match; return point != null && historyMatches.TryGetValue(FactoryTfAnnotationDocument.PointKey(point), out match) ? match : new FactoryPulseMatch(); }
        private void EditRegion(FactoryTfAnnotationRegion requested)
        {
            var region = requested == null ? null : document.Regions.FirstOrDefault(r => r.Id == requested.Id); if (region == null) return;
            using (var dialog = new FactoryTfAnnotationEditDialog(Icon, region.Geometry, region.Members.Count, region, "库内已有人工标签：“" + region.Name + "”", true))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                document.UpdateLabel(region.Id, dialog.Value); RefreshRows(region.Id); if (changed != null) changed();
            }
        }
        private void RefreshRows(string selectedId)
        {
            refreshing = true; table.Rows.Clear();
            foreach (var region in document.Regions)
            { int i = table.Rows.Add(region.Name, FactoryTfAnnotationRegion.KindText(region.Kind), FactoryTfAnnotationRegion.ShapeText(region.Geometry.Shape), region.Members.Count); table.Rows[i].Tag = region; }
            table.ClearSelection(); var chosen = table.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ((FactoryTfAnnotationRegion)r.Tag).Id == selectedId) ?? table.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (chosen != null) chosen.Selected = true; refreshing = false; ShowRegion();
        }
        private void ShowRegion()
        {
            var region = SelectedRegion; pulse.ClearWave(); plot.SetSelected(null); cache = null; historyPoint = null; historyMatches.Clear();
            if (region == null) { plot.SetData(new System.Collections.Generic.List<FactoryTfDisplayPoint>()); details.Text = "尚无标注。先在主TF图圈选有效点。"; return; }
            plot.SetData(document.PointsFor(region)); plot.SetAnnotations(document); plot.HighlightedRegionId = region.Id;
            var matches = new System.Collections.Generic.Dictionary<string, FactoryPulseMatch>();
            foreach (var point in document.PointsFor(region))
            {
                try { matches[FactoryTfAnnotationDocument.PointKey(point)] = FactoryPulseLabels.Match(document, point, FactoryPulseLabels.Create(point, FactoryTfPulseReader.Read(point, null, ref cache))); }
                catch { matches[FactoryTfAnnotationDocument.PointKey(point)] = new FactoryPulseMatch { Text = "未识别（对应原始数据不可用）" }; }
            }
            historyMatches = matches; plot.SetRecognition(matches, FactoryTfClustering.Analyze(document.PointsFor(region)));
            details.Text = "人工标注，非确认结论\r\n名称：" + region.Name + "\r\n类别：" + FactoryTfAnnotationRegion.KindText(region.Kind) + "\r\n疑似缺陷：" + region.Defect +
                "\r\n可能干扰源：" + region.PossibleInterferenceSource + "\r\n标注人：" + region.Author + "\r\n备注：" + region.Notes + "\r\n修改时间：" + region.ModifiedUtc.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss") + " +08:00\r\n采集链：" + document.ChainKey;
            notice.Text = "文件：" + (document.FilePath.Length == 0 ? "未保存" : document.FilePath) + "；" + document.Regions.Count + " 个区域。双击TF点查看脉冲；原始波形需对应记录目录。";
        }
        internal static bool SaveFile(IWin32Window owner, FactoryTfAnnotationDocument doc,Action<IWin32Window,string,string> optionalNotice=null)
        {
            using (var dialog = new SaveFileDialog { Title = "保存TF人工标注与关联特征", Filter = "TF标注文件 (*.tfann.xml)|*.tfann.xml", DefaultExt = "tfann.xml", AddExtension = true,
                FileName = doc.FilePath.Length == 0 ? "TF标注_" + DateTime.UtcNow.AddHours(8).ToString("yyyyMMdd_HHmmss") + ".tfann.xml" : doc.FilePath, OverwritePrompt = true })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                try { doc.Save(dialog.FileName); string message="TF区域、人工标签及关联特征已保存。原始波形仍需对应采集目录；覆盖时保留了旧文件备份。";
                    if(optionalNotice!=null)optionalNotice(owner,"保存完成",message);
                    else MessageBox.Show(owner,message,"保存完成",MessageBoxButtons.OK,MessageBoxIcon.Information);return true; }
                catch (Exception ex) { MessageBox.Show(owner, "保存未成功：" + ex.Message, "TF标注保存", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
            }
        }
        private static Button Btn(string title) { return new Button { Text = title, AutoSize = true, Height = 32, Margin = new Padding(3) }; }
    }
}
