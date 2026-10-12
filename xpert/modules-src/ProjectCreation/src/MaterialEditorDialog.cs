using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace HVX.Controls
{
    internal sealed class MaterialEditorDialog : Form
    {
        readonly TextBox nameInput = new TextBox { Dock = DockStyle.Top };
        readonly TextBox velocityInput = new TextBox { Dock = DockStyle.Top };
        readonly Label errorLabel = new Label { AutoSize = true, ForeColor = Color.Firebrick, Dock = DockStyle.Top, UseMnemonic = false };
        readonly int languageIndex;
        internal string MaterialName { get; private set; }
        internal double Velocity { get; private set; }

        internal MaterialEditorDialog(int language)
        {
            languageIndex = language;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Text = T("新增电缆材料", "Add cable insulation material");
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            MinimumSize = new Size(400, 270);
            ClientSize = new Size(520, 290);
            Font = ProjectCreationTheme.UiFont(9f);
            BackColor = ProjectCreationTheme.Workspace;
            ProjectDialogBrand.Apply(this);

            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 5, Padding = new Padding(2) };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.Controls.Add(new Label { Text = T("材料名称", "Material name"), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 5) }, 0, 0);
            fields.Controls.Add(nameInput, 0, 1);
            fields.Controls.Add(new Label { Text = T("建议波速 / m/μs", "Suggested velocity / m/μs"), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 12, 0, 5) }, 0, 2);
            fields.Controls.Add(velocityInput, 0, 3);
            fields.Controls.Add(errorLabel, 0, 4);
            viewport.Controls.Add(fields);
            viewport.SizeChanged += delegate { errorLabel.MaximumSize = new Size(Math.Max(100, viewport.ClientSize.Width - 30), 0); };
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
            var ok = new ProjectCreationButton { Text = T("确定", "OK"), AutoSize = true, MinimumSize = new Size(90, 34), Primary = true };
            var cancel = new ProjectCreationButton { Text = T("取消", "Cancel"), AutoSize = true, MinimumSize = new Size(90, 34), DialogResult = DialogResult.Cancel };
            ok.Click += delegate { if (TryAccept()) DialogResult = DialogResult.OK; };
            footer.Controls.Add(ok); footer.Controls.Add(cancel);
            outer.Controls.Add(viewport, 0, 0); outer.Controls.Add(footer, 0, 1);
            Controls.Add(outer);
            AcceptButton = ok; CancelButton = cancel;
        }

        string T(string chinese, string english) { return UiLanguage.Text(languageIndex, chinese, english); }

        internal bool TryAccept()
        {
            double velocity;
            string name = nameInput.Text.Trim();
            bool parsed = double.TryParse(velocityInput.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out velocity)
                || double.TryParse(velocityInput.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out velocity);
            if (!parsed || !ReferenceCatalog.IsValidMaterial(name, velocity))
            {
                errorLabel.Text = T("请填写材料名称和大于零的有限波速；名称不能含竖线或换行。", "Enter a material name and a finite velocity greater than zero; names cannot contain pipes or line breaks.");
                return false;
            }
            MaterialName = name; Velocity = velocity;
            errorLabel.Text = string.Empty;
            return true;
        }
    }
}
