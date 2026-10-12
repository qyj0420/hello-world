using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    internal enum ImportSelectionMode { None, Folder, ItemFile }

    internal sealed class ImportModeDialog : Form
    {
        public ImportSelectionMode SelectionMode { get; private set; }

        readonly int languageIndex;

        public ImportModeDialog(int language)
        {
            ProjectManagementLanguages.Validate(language);
            languageIndex = language;
            Text = L("Import project", "导入项目");
            Font = ProjectUiTheme.Font(this, 9.5f);
            BackColor = ProjectUiTheme.Canvas;
            ForeColor = ProjectUiTheme.Ink;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(720, 320);
            AutoScaleMode = AutoScaleMode.Dpi;
            ProjectUiTheme.ApplyDialogIcon(this);
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 2 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var title = new Label
            {
                Dock = DockStyle.Fill,
                Text = L("Choose an import source", "选择项目导入来源"),
                Font = ProjectUiTheme.Font(this, 12f, FontStyle.Bold),
                ForeColor = ProjectUiTheme.Graphite,
                TextAlign = ContentAlignment.MiddleLeft
            };
            shell.Controls.Add(title, 0, 0); shell.SetColumnSpan(title, 2);
            var folder = Card(L("Project folder", "项目文件夹"),
                L("Select a folder whose top level contains ITEM.in and CABLE.in.", "选择顶层包含 ITEM.in 与 CABLE.in 的项目文件夹。"), InstrumentIcon.Folder);
            var item = Card("ITEM.in", L("Select ITEM.in inside the project folder.", "选择项目文件夹内的 ITEM.in 文件。"), InstrumentIcon.Report);
            folder.Click += delegate { SelectionMode = ImportSelectionMode.Folder; DialogResult = DialogResult.OK; };
            item.Click += delegate { SelectionMode = ImportSelectionMode.ItemFile; DialogResult = DialogResult.OK; };
            shell.Controls.Add(folder, 0, 1); shell.Controls.Add(item, 1, 1);
            var cancel = new InstrumentButton { Text = L("Cancel", "取消"), Width = 110, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            shell.Controls.Add(cancel, 1, 2);
            Controls.Add(shell);
            CancelButton = cancel;
        }

        string L(string en, string zh) { return ProjectManagementLanguages.Text(languageIndex, en, zh); }

        static Control Card(string title, string description, InstrumentIcon icon)
        {
            var button = new InstrumentButton
            {
                IconKind = icon,
                Text = title + "\r\n" + description,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 0, 4, 8)
            };
            return button;
        }
    }

    internal sealed class MergeReportDialog : Form
    {
        readonly TextBox introduction = new TextBox();
        readonly DataGridView table = new DataGridView();
        readonly ProjectRecord[] projects;
        readonly int languageIndex;

        public string Introduction { get { return introduction.Text.Trim(); } }
        public MergeReportItem[] Items { get; private set; }

        public MergeReportDialog(IEnumerable<ProjectRecord> selected, int language)
        {
            projects = (selected ?? Enumerable.Empty<ProjectRecord>()).Where(p => p != null).ToArray();
            ProjectManagementLanguages.Validate(language);
            languageIndex = language;
            Text = L("Consolidate reports", "合并项目报告");
            Font = ProjectUiTheme.Font(this, 9.5f);
            BackColor = ProjectUiTheme.Canvas;
            ForeColor = ProjectUiTheme.Ink;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(780, 560);
            ClientSize = new Size(930, 640);
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ProjectUiTheme.ApplyDialogIcon(this);

            Padding = new Padding(12);
            var viewport = new Panel { Name = "MergeReportViewport", Dock = DockStyle.Fill, AutoScroll = true };
            var shell = new TableLayoutPanel { Dock = DockStyle.Top, RowCount = 6, ColumnCount = 1 };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            shell.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = L("Consolidated report content", "设置汇总报告简介与项目备注"),
                Font = ProjectUiTheme.Font(this, 12f, FontStyle.Bold),
                ForeColor = ProjectUiTheme.Graphite,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            shell.Controls.Add(LabelText(L("Introduction", "简介内容")), 0, 1);
            introduction.Dock = DockStyle.Fill;
            introduction.Multiline = true;
            introduction.ScrollBars = ScrollBars.Vertical;
            introduction.BorderStyle = BorderStyle.None;
            introduction.Font = ProjectUiTheme.Font(this, 9.5f);
            introduction.BackColor = ProjectUiTheme.Surface;
            introduction.ForeColor = ProjectUiTheme.Ink;
            introduction.Text = L("This consolidated report groups the following project reports for centralized review and archival.",
                "本汇总报告收录以下项目的试验报告，用于集中审阅、对比与归档。");
            var inputFrame = new InstrumentPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
            inputFrame.Controls.Add(introduction); shell.Controls.Add(inputFrame, 0, 2);
            shell.Controls.Add(LabelText(L("Project list and remarks", "项目列表与备注")), 0, 3);
            ConfigureTable();
            shell.Controls.Add(table, 0, 4);
            shell.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = L("Headings and figure/table captions will be renumbered across the merged document.", "合并后将按汇总文档统一调整标题层级，并连续重排图、表题注。"),
                ForeColor = ProjectUiTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 5);
            var actions = new FlowLayoutPanel { Name = "MergeReportActions", Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Padding = new Padding(0, 6, 0, 0) };
            var ok = new InstrumentButton { Text = L("Continue", "继续选择保存位置"), Primary = true, IconKind = InstrumentIcon.Merge, Width = 190, DialogResult = DialogResult.OK };
            var cancel = new InstrumentButton { Text = L("Cancel", "取消"), Width = 110, DialogResult = DialogResult.Cancel };
            ok.Click += delegate { CaptureItems(); };
            actions.Controls.Add(ok); actions.Controls.Add(cancel);
            viewport.Controls.Add(shell);Controls.Add(viewport);Controls.Add(actions);
            AcceptButton = ok; CancelButton = cancel;
            bool arranging=false;
            Action fit=()=>{
                if(arranging||IsDisposed)return;arranging=true;
                try {
                    int available=Math.Max(100,actions.ClientSize.Width-actions.Padding.Horizontal),used=0,rowHeight=0,footerHeight=actions.Padding.Vertical;
                    foreach(InstrumentButton button in actions.Controls){
                        button.Width=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+(button.IconKind==InstrumentIcon.None?28:56);
                        button.Height=Math.Max(38,button.Font.Height+16);
                        int width=button.Width+button.Margin.Horizontal,height=button.Height+button.Margin.Vertical;
                        if(used>0&&used+width>available){footerHeight+=rowHeight;used=0;rowHeight=0;}used+=width;rowHeight=Math.Max(rowHeight,height);
                    }
                    actions.Height=footerHeight+rowHeight+4;
                    int bodyWidth=Math.Max(100,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth),fixedHeight=0;
                    foreach(int row in new[]{0,1,3,5}){
                        Control caption=shell.GetControlFromPosition(0,row);
                        int height=TextRenderer.MeasureText(caption.Text,caption.Font,new Size(Math.Max(80,bodyWidth-caption.Padding.Horizontal-caption.Margin.Horizontal),int.MaxValue),TextFormatFlags.WordBreak).Height+caption.Padding.Vertical+caption.Margin.Vertical+12;
                        shell.RowStyles[row].Height=height;fixedHeight+=height;
                    }
                    int editorHeight=Math.Max(102,introduction.Font.Height*3+inputFrame.Padding.Vertical+inputFrame.Margin.Vertical+12);
                    shell.RowStyles[2].Height=editorHeight;fixedHeight+=editorHeight;
                    shell.Height=Math.Max(viewport.ClientSize.Height,fixedHeight+Math.Max(180,table.ColumnHeadersHeight+table.Font.Height*4+SystemInformation.HorizontalScrollBarHeight)+table.Margin.Vertical);
                    int timeWidth=Math.Max(190,TextRenderer.MeasureText("2026-09-29 23:59:59",table.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine).Width+24);
                    table.Columns["TestTime"].MinimumWidth=timeWidth;table.Columns["TestTime"].Width=timeWidth;
                    if(IsHandleCreated){
                        Rectangle area=Screen.FromControl(Owner??this).WorkingArea;
                        int width=Math.Max(1,area.Width-24),height=Math.Max(1,area.Height-24);
                        MinimumSize=new Size(Math.Min(780,width),Math.Min(Math.Max(560,Height-ClientSize.Height+Padding.Vertical+actions.Height+Math.Max(160,Font.Height*5)),height));
                        Size=new Size(Math.Min(Width,width),Math.Min(Height,height));
                        Location=new Point(Math.Max(area.Left,Math.Min(Left,area.Right-Width)),Math.Max(area.Top,Math.Min(Top,area.Bottom-Height)));
                    }
                }finally{arranging=false;}
            };
            viewport.SizeChanged+=(s,e)=>fit();actions.SizeChanged+=(s,e)=>fit();introduction.FontChanged+=(s,e)=>fit();table.FontChanged+=(s,e)=>fit();table.ColumnHeadersHeightChanged+=(s,e)=>fit();
            foreach(Control caption in shell.Controls)caption.FontChanged+=(s,e)=>fit();foreach(Control button in actions.Controls)button.FontChanged+=(s,e)=>fit();
            FontChanged+=(s,e)=>fit();Shown+=(s,e)=>fit();DpiChanged+=(s,e)=>fit();
        }

        Label LabelText(string text)
        {
            return new Label { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(12, 0, 12, 0), BackColor = ProjectUiTheme.Header, Text = text, Font = ProjectUiTheme.Font(this, 10.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        }

        string L(string en, string zh) { return ProjectManagementLanguages.Text(languageIndex, en, zh); }

        void ConfigureTable()
        {
            table.Dock = DockStyle.Fill;
            table.AllowUserToAddRows = table.AllowUserToDeleteRows = false;
            table.AllowUserToResizeRows = false;
            table.RowHeadersVisible = false;
            table.BackgroundColor = ProjectUiTheme.Surface;
            table.BorderStyle = BorderStyle.None;
            table.EnableHeadersVisualStyles = false;
            table.ColumnHeadersDefaultCellStyle.BackColor = ProjectUiTheme.Header;
            table.ColumnHeadersDefaultCellStyle.ForeColor = ProjectUiTheme.Navy;
            table.ColumnHeadersDefaultCellStyle.Font = ProjectUiTheme.Font(this, 10.5f, FontStyle.Bold);
            table.ColumnHeadersHeight = 40;
            table.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            table.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            table.DefaultCellStyle.BackColor = ProjectUiTheme.Surface;
            table.DefaultCellStyle.ForeColor = ProjectUiTheme.Ink;
            table.DefaultCellStyle.SelectionBackColor = ProjectUiTheme.Selection;
            table.DefaultCellStyle.SelectionForeColor = Color.White;
            table.AlternatingRowsDefaultCellStyle.BackColor = ProjectUiTheme.SurfaceAlt;
            table.GridColor = ProjectUiTheme.Grid;
            table.RowTemplate.Height = 36;
            table.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            table.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sequence", HeaderText = L("No.", "序号"), Width = 58, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            int timeWidth = Math.Max(190, TextRenderer.MeasureText("2026-09-29 23:59:59", table.Font,
                Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 24);
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = "TestTime", HeaderText = L("Test time", "试验时间"), Width = timeWidth, MinimumWidth = timeWidth, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = { WrapMode = DataGridViewTriState.False } });
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProjectName", HeaderText = L("Project", "项目名称"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 240, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            table.Columns.Add(new DataGridViewTextBoxColumn { Name = "Remark", HeaderText = L("Remark", "备注"), Width = 230, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
            for (int i = 0; i < projects.Length; i++)
            {
                ProjectRecord project = projects[i];
                int row = table.Rows.Add(i + 1, LocalizedTestTime(project), project.ProjectName, string.Empty);
                table.Rows[row].Tag = project;
            }
        }

        void CaptureItems()
        {
            List<MergeReportItem> items = new List<MergeReportItem>();
            foreach (DataGridViewRow row in table.Rows)
            {
                ProjectRecord project = row.Tag as ProjectRecord;
                if (project == null) continue;
                items.Add(new MergeReportItem
                {
                    ProjectName = project.ProjectName,
                    ProjectPath = project.ProjectPath,
                    TestTime = LocalizedTestTime(project),
                    ReportPath = project.LatestMergeableReportPath,
                    Remark = Convert.ToString(row.Cells["Remark"].Value) ?? string.Empty
                });
            }
            Items = items.ToArray();
        }

        string LocalizedTestTime(ProjectRecord project)
        {
            return project.HasTestTime ? project.TestTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : project.DisplayTestTime;
        }
    }
}
