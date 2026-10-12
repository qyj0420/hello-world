using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    // No connection, SDK or persistence operations occur in this editor.
    internal sealed class DeviceParametersForm : Form
    {
        readonly AppSettings basis;
        readonly SettingsNumber divider, trigger, inductance, resistance, maximum, compensation, fineThreshold;
        readonly CheckBox fineRamp, fineAutomatic;
        readonly ComboBox vlf;
        readonly bool zh;
        internal AppSettings Candidate { get; private set; }
        internal DeviceParametersForm(AppSettings value, string pair, bool first, bool chinese)
        {
            basis = value.Clone(); zh = chinese;
            Text = (zh ? "设备参数 — " : "Device parameters — ") + pair.Replace("|", " / ");
            AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(570, 640); MinimumSize = new Size(540, 480);
            Font = AppTheme.Font(10, FontStyle.Regular); BackColor = AppTheme.Canvas; AppTheme.ApplyDialogIcon(this);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(18) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            divider = Number(grid, "DividerRatio", T("分压比（V/V）", "Divider ratio (V/V)"), .000001m, 1000000000m, value.DividerRatio);
            trigger = Number(grid, "DeviceTriggerVolts", T("触发阈值 / V（Pico EXT）", "Trigger threshold / V (Pico EXT)"), -4.999999m, 4.999999m, value.DeviceTriggerVolts);
            vlf = new ComboBox { Name = "VlfSupport", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
            vlf.Items.AddRange(new object[] { T("不启用", "Disabled"), T("余弦超低频", "Cosine VLF"), T("综合超低频", "Combined VLF"), T("余弦与综合超低频", "Cosine and combined VLF") });
            vlf.SelectedIndex = (value.SupportsCosineVlf ? 1 : 0) + (value.SupportsCombinedVlf ? 2 : 0);
            Row(grid, T("超低频选择", "VLF selection"), vlf);
            inductance = Number(grid, "InductanceHenry", T("电感量 / H", "Inductance / H"), .000000001m, 1000000000m, value.InductanceHenry);
            resistance = Number(grid, "InductorDcResistanceOhm", T("电阻 / Ω", "Resistance / Ω"), 0, 1000000000m, value.InductorDcResistanceOhm);
            maximum = Number(grid, "MaximumVoltageKv", T("最高限压 / kV", "Maximum voltage / kV"), .000001m, 1000000m, value.MaximumVoltageKv);
            maximum.AccessibleDescription = T("0–5 V AO 对应 0–最高限压；加压比自动计算。", "0–5 V AO maps to 0–maximum voltage; the control ratio is calculated automatically.");
            compensation = Number(grid, "CompensationCapacitanceUf", T("补偿电容量 / μF", "Compensation capacitance / μF"), 0, 1000000m, value.CompensationCapacitanceUf);
            fineRamp = new CheckBox { Name = "EnableFinePressureRamp", Text = T("启用", "Enabled"), AutoSize = true,
                Dock = DockStyle.Top, Checked = value.EnableFinePressureRamp };
            Row(grid, T("精细化加压", "Fine voltage ramp"), fineRamp);
            fineAutomatic = new CheckBox { Name = "FinePressureThresholdAutomatic", Text = T("按最高限压的15%", "15% of maximum voltage"), AutoSize = true,
                Dock = DockStyle.Top, Checked = value.FinePressureThresholdAutomatic };
            Row(grid, T("自动设置阈值", "Automatic threshold"), fineAutomatic);
            fineThreshold = Number(grid, "FinePressureThresholdKv", T("适用最高目标峰值 / kV", "Maximum target peak / kV"), .00000015m, 1000000m, value.FinePressureThresholdKv);
            fineThreshold.AccessibleDescription = T("启用后，目标峰值大于0且不超过此值时使用精细化加压。", "When enabled, applies to target peaks above zero and at or below this value.");
            Action updateFine = delegate {
                fineAutomatic.Enabled = fineRamp.Checked;
                fineThreshold.Enabled = fineRamp.Checked && !fineAutomatic.Checked;
                if (fineAutomatic.Checked) fineThreshold.LoadSource(maximum.ReadSource() * .15);
            };
            fineRamp.CheckedChanged += delegate { updateFine(); };
            fineAutomatic.CheckedChanged += delegate { updateFine(); };
            maximum.ValueChanged += delegate { updateFine(); };
            updateFine();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 64, Padding = new Padding(12), WrapContents = false };
            var cancel = AppTheme.Button(T("取消", "Cancel"), false); cancel.DialogResult = DialogResult.Cancel;
            var save = AppTheme.Button(first ? T("保存并完成配置", "Save configuration") : T("保存修改", "Save changes"), true); save.AutoSize = true;
            save.Click += delegate {
                try { Candidate = Capture(); DialogResult = DialogResult.OK; Close(); }
                catch (ArgumentException) { MessageBox.Show(this, T("参数未保存，请检查各项数值是否在允许范围内。", "Parameters were not saved. Check that each value is within its allowed range."), T("参数未保存", "Parameters not saved"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); Controls.Add(grid); Controls.Add(buttons);
            AcceptButton = save; CancelButton = cancel;
        }
        string T(string chinese, string english) { return zh ? chinese : english; }
        static SettingsNumber Number(TableLayoutPanel grid, string name, string label, decimal min, decimal max, double value)
        {
            var n = new SettingsNumber { Name = name, Minimum = min, Maximum = max, DecimalPlaces = 6, Increment = .01m, Dock = DockStyle.Top };
            n.LoadSource(value); Row(grid, label, n); return n;
        }
        static void Row(TableLayoutPanel grid, string label, Control value)
        {
            int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 8, 8, 16) }, 0, row);
            value.Margin = new Padding(0, 6, 0, 14); grid.Controls.Add(value, 1, row);
        }
        internal new AppSettings Capture()
        {
            var s = basis.Clone(); s.DividerRatio = divider.ReadSource(); s.DeviceTriggerVolts = trigger.ReadSource();
            s.InductanceHenry = inductance.ReadSource(); s.InductorDcResistanceOhm = resistance.ReadSource();
            s.MaximumVoltageKv = maximum.ReadSource(); s.PressureRatio = DeviceParameterProfiles.PressureRatioFor(s.MaximumVoltageKv);
            s.CompensationCapacitanceUf = compensation.ReadSource();
            s.EnableFinePressureRamp = fineRamp.Checked; s.FinePressureThresholdAutomatic = fineAutomatic.Checked;
            s.FinePressureThresholdKv = fineThreshold.ReadSource();
            s.SupportsCosineVlf = (vlf.SelectedIndex & 1) != 0; s.SupportsCombinedVlf = (vlf.SelectedIndex & 2) != 0;
            string message; if (!s.TryValidate(out message)) throw new ArgumentException(message);
            return s;
        }
    }
}
