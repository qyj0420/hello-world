using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class CompetitionDefectPreferencesDialog : Form
    {
        private readonly CompetitionDefectPreferences original;
        private readonly ComboBox language;
        private readonly CheckBox ai;
        private readonly TextBox model;
        internal CompetitionDefectPreferences Result { get; private set; }
        internal CompetitionDefectPreferencesDialog(CompetitionDefectPreferences value)
        {
            original = value.Clone(); int lang = value.LanguageIndex;
            Text = UiLanguage.Text(lang, "AI缺陷识别设置", "AI defect recognition settings");
            StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(740, 400); ClientSize = new Size(800, 400);
            AutoScaleMode = AutoScaleMode.Dpi; Font = AppTheme.Font(10f, FontStyle.Regular); BackColor = AppTheme.Canvas;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; AppTheme.ApplyDialogIcon(this);
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(16), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var explanation = new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14),
                Text = UiLanguage.Text(lang, "这些设置仅用于本窗口，不修改主程序设置。路径留空时使用已安装的标准模型；若通过路径加载了模型，则以加载的模型为准。",
                    "Settings apply only to this window. An empty path uses the installed standard model; a loaded path takes precedence.") };
            layout.Controls.Add(explanation, 0, 0); layout.SetColumnSpan(explanation, 2);
            language = new SettingsComboBox { Name = "CompetitionLanguage", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            language.Items.AddRange(AppLanguages.SelectionItems); AppLanguages.InitializeSelection(language, value.LanguageCode);
            ai = new CheckBox { Name = "CompetitionAiEnabled", AutoSize = false, Dock = DockStyle.Fill,
                Margin = new Padding(0, 8, 0, 8), Checked = value.EnableAiFeatures,
                Text = UiLanguage.Text(lang, "启用缺陷识别及人工确认学习", "Enable recognition and human-verified learning") };
            model = new TextBox { Name = "CompetitionModelPath", Dock = DockStyle.Fill, Text = value.ModelLibraryPath ?? String.Empty };
            layout.Controls.Add(new Label { AutoSize = true, Text = UiLanguage.Text(lang, "界面语言", "Language"), Margin = new Padding(0, 6, 12, 10) }, 0, 1);
            layout.Controls.Add(language, 1, 1); layout.Controls.Add(ai, 0, 2); layout.SetColumnSpan(ai, 2);
            Action fitAi = delegate
            {
                int width = Math.Max(120, layout.ClientSize.Width - layout.Padding.Horizontal - ai.Margin.Horizontal);
                int glyph; using (var graphics = ai.CreateGraphics()) glyph = CheckBoxRenderer.GetGlyphSize(graphics,
                    System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal).Width;
                int height = TextRenderer.MeasureText(ai.Text, ai.Font, new Size(Math.Max(80, width - glyph - 12), Int32.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height + 12;
                ai.MinimumSize = new Size(0, Math.Max(glyph + 12, height)); ai.Height = ai.MinimumSize.Height;
            };
            layout.SizeChanged += delegate { fitAi(); }; ai.FontChanged += delegate { fitAi(); }; Shown += delegate { fitAi(); };
            layout.Controls.Add(new Label { AutoSize = true, Text = UiLanguage.Text(lang, "模型文件（可选）", "Model file (optional)"), Margin = new Padding(0, 6, 12, 10) }, 0, 3);
            var modelRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var browse = AppTheme.Button(UiLanguage.Text(lang, "浏览", "Browse"), false); browse.AutoSize = true;
            browse.Click += delegate { using (var dialog = new OpenFileDialog { Filter = DefectModelCatalog.FileFilter, CheckFileExists = true }) if (dialog.ShowDialog(this) == DialogResult.OK) model.Text = dialog.FileName; };
            modelRow.Controls.Add(model, 0, 0); modelRow.Controls.Add(browse, 1, 0); layout.Controls.Add(modelRow, 1, 3);
            var footer = new FlowLayoutPanel { Name = "CompetitionSettingsFooter", Dock = DockStyle.Bottom, AutoSize = true,
                Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            var cancel = AppTheme.Button(UiLanguage.Text(lang, "取消", "Cancel"), false); cancel.AutoSize = true; cancel.DialogResult = DialogResult.Cancel;
            var save = AppTheme.Button(UiLanguage.Text(lang, "保存", "Save"), true); save.AutoSize = true;
            save.Click += delegate
            {
                try
                {
                    var next = original.Clone(); next.LanguageCode = AppLanguages.CaptureSelectionCode(language, original.LanguageCode);
                    next.EnableAiFeatures = ai.Checked; next.ModelLibraryPath = model.Text.Trim();
                    next.ModelKind = DefectModelCatalog.Standard; next.CompetitionModelPath = String.Empty; next.Validate();
                    if (next.ModelLibraryPath.Length > 0 && !File.Exists(next.ModelLibraryPath))
                        throw new FileNotFoundException(UiLanguage.Text(lang, "所选模型文件不存在。", "The selected model does not exist."));
                    Result = next; DialogResult = DialogResult.OK; Close();
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            footer.Controls.Add(cancel); footer.Controls.Add(save);
            var viewport = new Panel { Name = "CompetitionSettingsViewport", Dock = DockStyle.Fill, AutoScroll = true };
            viewport.Controls.Add(layout); Controls.Add(viewport); Controls.Add(footer);
            AcceptButton = save; CancelButton = cancel;
            Shown += delegate
            {
                fitAi();layout.PerformLayout();
                int needed=layout.GetPreferredSize(new Size(viewport.ClientSize.Width,0)).Height+footer.Height+12;
                var work=Screen.FromControl(this).WorkingArea;
                int chrome=Height-ClientSize.Height;
                ClientSize=new Size(ClientSize.Width,Math.Min(Math.Max(ClientSize.Height,needed),Math.Max(200,work.Height-chrome-24)));
                Location=new Point(Math.Max(work.Left,Math.Min(Left,work.Right-Width)),Math.Max(work.Top,Math.Min(Top,work.Bottom-Height)));
            };
            AppTheme.FitDialogToWorkingArea(this);
        }
    }
}
