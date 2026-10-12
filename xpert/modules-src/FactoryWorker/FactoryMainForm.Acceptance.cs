using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private FactoryAcceptanceEvidence acceptanceEvidence = new FactoryAcceptanceEvidence();
        private bool bindingAcceptanceEvidence;
        private readonly NumericUpDown acceptanceLoad = Number(200, .001M, 1000000, 3), acceptanceMaximum = Number(28, .001M, 100, 3),
            acceptanceReferenceVoltage = Number(0, 0, 100, 4), acceptanceKnownCharge = Number(100, .001M, 1000000, 3);
        private readonly TextBox voltageReferenceId = new TextBox(), pdReferenceId = new TextBox();
        private readonly CheckBox standardVoltage = new CheckBox { Text = "使用独立标准分压器 / 示波器", AutoSize = true },
            standardPd = new CheckBox { Text = "使用独立标准校准器", AutoSize = true },
            voltage80Percent = new CheckBox { Text = "本轮为80%最高试验电压工况", AutoSize = true },
            voltageCoverage = new CheckBox { Text = "已核对零至最高电压均匀5点", AutoSize = true };
        private readonly Button recordCharge = Button("记录标准电量读数", 230), recordVoltage = Button("记录标准电压读数", 230), saveEvidence = Button("保存核验证据", 170), loadEvidence = Button("加载核验证据", 170);
        private readonly DataGridView acceptanceGrid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AutoGenerateColumns = false, BackgroundColor = Color.White, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BorderStyle = BorderStyle.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, RowTemplate = { Height = 42 } };
        private readonly Label acceptanceSummary = new Label { Dock = DockStyle.Fill, Padding = new Padding(8), AutoSize = false, BackColor = Color.White };

        private TabPage BuildAcceptancePage()
        {
            var page = new TabPage("出厂核验");
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 410)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var inputs = Group("检定器具与本轮独立证据");
            Add(inputs, "实际负载电容 / nF", acceptanceLoad); Add(inputs, "设备最高电压 / kV", acceptanceMaximum);
            Add(inputs, "振荡波工况", voltage80Percent); Add(inputs, "电压参考确认", standardVoltage);
            Add(inputs, "分压器 / 示波器编号", voltageReferenceId); Add(inputs, "独立峰值读数 / kV", acceptanceReferenceVoltage);
            Add(inputs, "本轮电压记录", recordVoltage); Add(inputs, "电压范围覆盖", voltageCoverage);
            Add(inputs, "电量参考确认", standardPd); Add(inputs, "标准校准器编号", pdReferenceId);
            Add(inputs, "独立注入电量 / pC", acceptanceKnownCharge); Add(inputs, "本轮电量记录", recordCharge);
            Add(inputs, "证据保存", saveEvidence);
            Add(inputs, "继续核验", loadEvidence);
            Add(inputs, "独立校验步骤", new Label { AutoSize = true, Text = "先校准并显式应用系数，再换独立标准校准器验证。使用采集时的旧系数×实测峰值，绝不用该次新候选系数证明自身正确。八档每档需5份不同采集；重复点击同一轮不会增加次数。" });
            Add(inputs, "负载区别", new Label { AutoSize = true, Text = "1932检定负载150±7.5 nF；约200 nF仅为参考负载，不自动判正式检定合格。定位项目不纳入本工具；安规/频带/器具资质等须另行验证。" });
            var left = BuildScrollableGroup(inputs); grid.Controls.Add(left, 0, 0);
            var results = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            results.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); results.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            acceptanceGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            acceptanceGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(233, 239, 244); acceptanceGrid.EnableHeadersVisualStyles = false;
            AddCheckColumn("项目", 120); AddCheckColumn("状态", 86); AddCheckColumn("条款", 125); AddCheckColumn("要求", 155); AddCheckColumn("实测 / 缺项", 175, true);
            results.Controls.Add(acceptanceSummary, 0, 0); results.Controls.Add(acceptanceGrid, 0, 1); grid.Controls.Add(results, 1, 0); page.Controls.Add(grid);
            foreach (var control in new Control[] { acceptanceLoad, acceptanceMaximum, acceptanceReferenceVoltage, acceptanceKnownCharge, voltageReferenceId, pdReferenceId })
            { if (control is NumericUpDown) ((NumericUpDown)control).ValueChanged += (s,e) => UpdateAcceptanceDisplay(); else control.TextChanged += (s,e) => UpdateAcceptanceDisplay(); }
            foreach (var control in new[] { standardVoltage, standardPd, voltage80Percent, voltageCoverage }) control.CheckedChanged += (s,e) => UpdateAcceptanceDisplay();
            recordCharge.Click += (s,e) => RecordAcceptance(true); recordVoltage.Click += (s,e) => RecordAcceptance(false);
            saveEvidence.Click += (s,e) => { try { status.Text = "证据已保存：" + SaveAcceptanceEvidence(); } catch (Exception ex) { status.Text = "证据保存失败：" + ex.Message; } };
            loadEvidence.Click += (s,e) => LoadAcceptanceEvidence();
            acceptanceGrid.CellDoubleClick += (s,e) => ShowAcceptanceDetail(e.RowIndex);
            return page;
        }
        private void AddCheckColumn(string caption, int width, bool fill = false)
        { acceptanceGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = caption, Width = width, AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None, MinimumWidth = 80 }); }
        private void ReadAcceptanceEvidence()
        {
            acceptanceEvidence.LoadCapacitanceNf = (double)acceptanceLoad.Value; acceptanceEvidence.RatedMaximumKv = (double)acceptanceMaximum.Value;
            acceptanceEvidence.VoltageWaveformAt80PercentConfirmed = voltage80Percent.Checked; acceptanceEvidence.VoltageReferenceConfirmed = standardVoltage.Checked;
            acceptanceEvidence.VoltageReferenceId = voltageReferenceId.Text.Trim(); acceptanceEvidence.IndependentVoltageReferenceKv = (double)acceptanceReferenceVoltage.Value;
            acceptanceEvidence.VoltageRangeCoverageConfirmed = voltageCoverage.Checked; acceptanceEvidence.PdReferenceConfirmed = standardPd.Checked;
            acceptanceEvidence.PdReferenceId = pdReferenceId.Text.Trim();
        }
        private void UpdateAcceptanceDisplay()
        {
            if (bindingAcceptanceEvidence || acceptanceGrid.Columns.Count == 0) return;
            ReadAcceptanceEvidence(); acceptanceGrid.Rows.Clear();
            foreach (var check in FactoryAcceptanceChecks.Evaluate(latest, acceptanceEvidence))
            {
                int row = acceptanceGrid.Rows.Add(check.Name, AcceptanceState(check.Status), check.Clause, check.Requirement, check.Actual);
                acceptanceGrid.Rows[row].Tag = check;
                acceptanceGrid.Rows[row].DefaultCellStyle.BackColor = check.Status == FactoryAcceptanceStatus.Passed ? Color.FromArgb(225, 244, 232) :
                    check.Status == FactoryAcceptanceStatus.Failed ? Color.FromArgb(255, 220, 220) : check.Status == FactoryAcceptanceStatus.DiagnosticOnly ? Color.FromArgb(231, 241, 249) : Color.FromArgb(255, 247, 214);
            }
            acceptanceSummary.Text = FactoryAcceptanceChecks.ScopeNote + "\n已记录电量 " + acceptanceEvidence.ChargeReadings.Count + " 轮，电压 " + acceptanceEvidence.VoltageReadings.Count + " 点。双击表格查看要求、证据和缺项。";
            recordCharge.Enabled = !busy && latest != null && latest.Kind == FactoryRunKind.Calibration && latest.Calibration != null && latest.Calibration.Valid;
            recordVoltage.Enabled = !busy && latest != null && latest.Kind == FactoryRunKind.Pressure;
        }
        private static string AcceptanceState(FactoryAcceptanceStatus state)
        { return state == FactoryAcceptanceStatus.Passed ? "单项通过" : state == FactoryAcceptanceStatus.Failed ? "未通过" : state == FactoryAcceptanceStatus.DiagnosticOnly ? "参考诊断" : state == FactoryAcceptanceStatus.ExternalRecordRequired ? "另行验证" : "待验证"; }
        private void RecordAcceptance(bool charge)
        {
            if (busy || latest == null) return;
            bool added = false;
            try
            {
                ReadAcceptanceEvidence();
                if (charge)
                { var reading = FactoryAcceptanceChecks.AddChargeReading(acceptanceEvidence, latest, (double)acceptanceKnownCharge.Value, pdReferenceId.Text); status.Text = "独立电量实测 " + reading.MeasuredPc.ToString("0.###") + " pC / 参考 " + reading.KnownPc.ToString("0.###") + " pC；使用采集时系数。"; }
                else
                { var reading = FactoryAcceptanceChecks.AddVoltageReading(acceptanceEvidence, latest, (double)acceptanceReferenceVoltage.Value, voltageReferenceId.Text); status.Text = "独立电压实测 " + reading.MeasuredKv.ToString("0.###") + " kV / 参考 " + reading.ReferenceKv.ToString("0.###") + " kV。"; }
                added = true; SaveAcceptanceEvidence(); UpdateAcceptanceDisplay();
            }
            catch (Exception ex) { UpdateAcceptanceDisplay(); status.Text = added ? "读数已留在本会话内存，但保存失败；请再次保存：" + ex.Message : "校验记录未接受：" + ex.Message; }
        }
        private string SaveAcceptanceEvidence()
        {
            if (smokeMode) throw new InvalidOperationException("UI测试不写入正式验收证据。");
            ReadAcceptanceEvidence(); string directory = NewDirectory("EVIDENCE"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "factory-acceptance-evidence.xml");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) new XmlSerializer(typeof(FactoryAcceptanceEvidence)).Serialize(stream, acceptanceEvidence);
            using (var stream = new FileStream(Path.Combine(directory, "checks.xml"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(System.Collections.Generic.List<FactoryAcceptanceCheck>)).Serialize(stream, FactoryAcceptanceChecks.Evaluate(latest, acceptanceEvidence));
            return path;
        }
        private void LoadAcceptanceEvidence()
        {
            if (busy || smokeMode) return;
            using (var dialog = new OpenFileDialog { Title = "加载已保存的出厂核验证据", Filter = "出厂核验证据 (*.xml)|*.xml", FileName = "factory-acceptance-evidence.xml" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (acceptanceEvidence.ChargeReadings.Count + acceptanceEvidence.VoltageReadings.Count > 0 &&
                        MessageBox.Show(this, "加载会替换本会话的证据集合，原始文件不会删除。请先保存尚未保存的证据。继续？", "加载核验证据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                    FactoryAcceptanceEvidence loaded;
                    using (var reader = System.Xml.XmlReader.Create(dialog.FileName, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
                        loaded = (FactoryAcceptanceEvidence)new XmlSerializer(typeof(FactoryAcceptanceEvidence)).Deserialize(reader);
                    if (loaded == null || !FactorySettings.Positive(loaded.LoadCapacitanceNf) || !FactorySettings.Positive(loaded.RatedMaximumKv) ||
                        loaded.LoadCapacitanceNf > (double)acceptanceLoad.Maximum || loaded.RatedMaximumKv > (double)acceptanceMaximum.Maximum)
                        throw new InvalidDataException("核验证据参数无效。");
                    loaded.ChargeReadings = loaded.ChargeReadings ?? new System.Collections.Generic.List<FactoryChargeVerificationReading>();
                    loaded.VoltageReadings = loaded.VoltageReadings ?? new System.Collections.Generic.List<FactoryVoltageVerificationReading>();
                    bindingAcceptanceEvidence = true;
                    try
                    {
                        acceptanceEvidence = loaded; acceptanceLoad.Value = (decimal)loaded.LoadCapacitanceNf; acceptanceMaximum.Value = (decimal)loaded.RatedMaximumKv;
                        voltageReferenceId.Text = loaded.VoltageReferenceId; pdReferenceId.Text = loaded.PdReferenceId;
                    // Certificate/context acknowledgements are not automatically
                    // turned into permission for today's equipment or settings.
                        standardVoltage.Checked = standardPd.Checked = voltage80Percent.Checked = voltageCoverage.Checked = false;
                    }
                    finally { bindingAcceptanceEvidence = false; }
                    UpdateAcceptanceDisplay(); status.Text = "已加载历史证据；仅同设备、同测量链且条件完整的读数参与核验，未自动认可当前工况。";
                }
                catch (Exception ex) { status.Text = "核验证据加载失败：" + ex.Message; }
            }
        }
        private void ShowAcceptanceDetail(int row)
        {
            if (row < 0 || row >= acceptanceGrid.Rows.Count) return;
            var check = acceptanceGrid.Rows[row].Tag as FactoryAcceptanceCheck; if (check == null) return;
            var dialog = new Form { Text = "出厂核验 · " + check.Name, Icon = Icon, Size = new Size(800, 420), StartPosition = FormStartPosition.CenterParent, Font = Font };
            dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
                Text = check.Clause + "\r\n要求：" + check.Requirement + "\r\n实测：" + check.Actual + "\r\n状态：" + AcceptanceState(check.Status) + "\r\n\r\n" + check.Detail });
            dialog.FormClosed += (s,e) => dialog.Dispose(); dialog.Show(this);
        }
    }
}
