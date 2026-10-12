using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    internal sealed class NavigationButton : Button
    {
        internal string Number;
        internal bool Selected;
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Selected) using (Brush accent = new SolidBrush(Color.FromArgb(77, 222, 193))) e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
            using (Font small = new Font(Font.FontFamily, 8, FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, Number, small, new Rectangle(16, 12, Width - 20, 17), Color.FromArgb(181, 202, 218), TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(0, 33, Width, 35), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(7, 7, Width - 14, Height - 14), ForeColor, BackColor);
        }
    }
    internal sealed partial class MainForm
    {
        private readonly Label identityLine = new Label { Dock = DockStyle.Fill, ForeColor = Navy, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label recordLine = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(77, 101, 119), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        private readonly Button recordDetails = Button("记录信息");
        private Task38RunRecord activeRecord;
        private string RecordDirectory { get { return Path.Combine(dataRoot, activeMode == 0 ? "records-demo" : "records-hardware"); } }
        private string activeRecordDirectory;

        private Control BuildBrandedHeader()
        {
            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(6, 0, 6, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 268));
            Panel titles = new Panel { Dock = DockStyle.Fill };
            titles.Controls.Add(new Label { Text = "振荡波加压与同步采集", Dock = DockStyle.Top, Height = 38, Font = new Font(Font.FontFamily, 21, FontStyle.Bold), ForeColor = Navy });
            titles.Controls.Add(new Label { Text = "校准采集  /  加压试验  /  硬件配置", Dock = DockStyle.Bottom, Height = 25, ForeColor = Color.FromArgb(108, 128, 144) });
            header.Controls.Add(titles, 0, 0);
            Panel modePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 18, 8, 0) };
            mode.Dock = DockStyle.Top; modePanel.Controls.Add(mode); header.Controls.Add(modePanel, 1, 0);
            PictureBox brand = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(12, 10, 0, 10) };
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Task38.Brand.Logo"))
            using (Image source = Image.FromStream(stream)) brand.Image = new Bitmap(source);
            brand.Disposed += (s, e) => { if (brand.Image != null) brand.Image.Dispose(); };
            header.Controls.Add(brand, 2, 0); return header;
        }
        private Control BuildTraceabilityStrip()
        {
            TableLayoutPanel strip = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.White, Padding = new Padding(14, 2, 10, 2), Margin = new Padding(4, 0, 4, 8) };
            strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); strip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            strip.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); strip.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            strip.Controls.Add(identityLine, 0, 0); strip.Controls.Add(recordLine, 0, 1); strip.Controls.Add(recordDetails, 2, 0); strip.SetRowSpan(recordDetails, 2);
            identityLine.AutoEllipsis = true;
            AddRawCaptureActions(strip);
            strip.Controls.Add(inspectConnection, 1, 0); strip.SetRowSpan(inspectConnection, 2);
            inspectConnection.Anchor = AnchorStyles.Right; inspectConnection.Click += async (s, e) => await InspectConnection();
            recordDetails.Anchor = AnchorStyles.Right; recordDetails.Click += (s, e) => ShowRecordDetails(); return strip;
        }
        private void RefreshTraceability(ConnectionSnapshot connection)
        {
            bool current = connection != null && (connection.Phase == ConnectionPhase.Ready || connection.Phase == ConnectionPhase.ReconnectedAwaitingConfirmation);
            inspectConnection.Enabled = !connectionInspectionBusy && !connectionInspectionBlocked && session == null && !calibrationBusy && !analyzing && !changingMode && !resetting;
            inspectConnection.Text = connectionInspectionBusy ? "校验中…" : "连接校验";
            identityLine.Text = current
                ? "设备身份   ART  " + Known(connection.ArtSerial) + "     |     PICO  " + Known(connection.PicoSerial) + (activeMode == 0 ? "     仿真标识" : "     自动读取") + (connection.ControlBoxVerification ? "   控制箱校验·禁止加压" : "   设备验证")
                : connectionInspectionBusy ? "正在分别校验控制模块和局放模块，超时 10 秒，不执行输出…"
                : session == null && inspectionKey == InspectionKey && inspectionIdentity.Length != 0 ? inspectionIdentity + "  （未初始化）"
                : "设备身份   ART  未获取     |     PICO  未获取     请先连接校验";
            recordLine.Text = activeRecord == null ? "试验编号将在开始加压时生成，绑定两台设备、分压比、加压比和时间。"
                : "本机记录   " + activeRecord.RecordId + "    ·    " + RecordState(activeRecord.Status);
            recordDetails.Enabled = activeRecord != null;
            if (current && connection.ControlBoxVerification)
                recordLine.Text = "控制箱未作为整机验证；仅诊断原始输入，本状态不产生加压试验记录。";
            if (activeRecord != null && activeRecord.Status == "Running" && connection != null &&
                (connection.Phase == ConnectionPhase.Reconnecting || connection.Phase == ConnectionPhase.TimedOut))
            {
                activeRecord.Status = "Interrupted"; activeRecord.Detail = connection.LastError; activeRecord.FinishedUtc = DateTime.UtcNow; PersistRecordUpdate();
            }
            if (current && !String.IsNullOrEmpty(connection.IdentityDiagnostic)) OperatorDiagnostics.Record(connection.IdentityDiagnostic);
        }
        private static string Known(string value) { return String.IsNullOrWhiteSpace(value) ? "未获取" : value; }
        private static string RecordState(string value)
        {
            switch (value) { case "Prepared": return "已登记"; case "Running": return "试验中"; case "Completed": return "采集完成"; case "Failed": return "试验未完成"; case "Interrupted": return "通信中断"; case "NotStarted": return "未启动"; default: return value; }
        }
        private void StartRecordedCycle()
        {
            if(rawSaveBusy)throw new InvalidOperationException("原始数据正在保存，请等待完成。");
            if (calibrationBusy) throw new InvalidOperationException("请先结束校准采集。");
            if (dirty) throw new InvalidOperationException("参数已修改，请先点击“连接 / 应用设置”。");
            if (analyzing || session == null) return;
            ConnectionSnapshot connection = session.GetConnectionStatus();
            if (connection.Phase != ConnectionPhase.Ready || session.Controller == null || !session.Controller.GetStatus().StartAllowed)
                throw new InvalidOperationException("当前联锁或设备状态不允许加压。");
            Task38RunRecord record = Task38RunRecord.Create(connection.ArtSerial, connection.PicoSerial, profile, activeMode == 0, DateTimeOffset.Now);
            string directory = RecordDirectory;
            record.SaveNew(directory); // Registration failure must happen before any HV cycle starts.
            activeRecord = record; activeRecordDirectory = directory;
            if (!session.StartCycle(record.RecordId))
            {
                record.Status = "NotStarted"; record.Detail = "启动时设备状态已变化"; PersistRecordUpdate();
                throw new InvalidOperationException("当前联锁或设备状态不允许加压。");
            }
            record.Status = "Running"; PersistRecordUpdate();
            dc.Clear(); synchronized.Clear(); processed = null;
            analysis.Text = profile.Hardware.TargetVoltageV == 0
                ? "零压检查进行中：不启高压，检查残压、电源反馈与电压段；不执行振荡检波。"
                : "本轮采集中，完成后评估首零相位后的量程。";
        }
        private void CompleteRunRecord(Task38CycleResult result)
        {
            if (activeRecord == null || result.RecordId != activeRecord.RecordId) return;
            activeRecord.Status = result.Success ? "Completed" : "Failed";
            activeRecord.Success = result.Success; activeRecord.FinishedUtc = result.CompletedUtc;
            activeRecord.IsZeroVoltageCheck = result.IsBackgroundCapture;
            activeRecord.MeasuredVoltagePeakV = result.PeakActualVoltageV;
            activeRecord.PicoCaptureMetadataAvailable = result.PicoRawAdc.Length > 0 && result.PicoResolutionModeBits > 0;
            activeRecord.PicoActualSampleRateHz = result.PicoSampleRateHz;
            activeRecord.PicoActualIntervalNs = result.PicoSampleIntervalNanoseconds;
            activeRecord.PicoActualSampleCount = result.PicoRawAdc.Length;
            activeRecord.PicoResolutionModeBits = result.PicoResolutionModeBits;
            activeRecord.PicoRangeHardwareBits = result.PicoRangeHardwareBits;
            activeRecord.Detail = result.ExitReason + " " + result.ErrorMessage; PersistRecordUpdate();
            AutoSaveRawCapture(result);
        }
        private void PersistRecordUpdate()
        {
            try { activeRecord.SaveUpdate(activeRecordDirectory); }
            catch (Exception ex) { uiError = "试验记录状态保存失败，已保留初始登记文件：" + OperatorDiagnostics.Report(ex); }
        }
        private void ShowRecordDetails()
        {
            if (activeRecord == null) return;
            Task38RunRecord r = activeRecord;
            using (BrandedForm dialog = new BrandedForm { Text = "试验记录与设备身份", Size = new Size(820, 470), StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
                    Text = "记录编号：" + r.RecordId + "\r\n状态：" + RecordState(r.Status) + "\r\n控制模块序列号：" + r.ArtSerial + "\r\n局放模块序列号：" + r.PicoSerial +
                    "\r\n直流分压比：" + r.DcDividerRatio + "\r\n振荡分压比：" + r.AcDividerRatio + "\r\n加压比：" + r.VoltageGain +
                    "\r\n本地时间及偏移：" + r.LocalTimeWithOffset + "\r\nUTC：" + r.CreatedUtc.ToString("O") + "\r\n完整指纹：" + r.FingerprintSha256 +
                    "\r\n记录文件：" + Path.Combine(activeRecordDirectory, r.RecordId + ".xml") +
                    "\r\n原始数据：" + (String.IsNullOrEmpty(r.RawCapturePath) ? "未保存" : r.RawCapturePath) +
                    "\r\n恢复位置：" + (r.RawCaptureRecoveryPath ?? "") +
                    "\r\n\r\n记录文件保存编号、身份、参数及完成状态；原始数据另存为 HXRAW 文件，不代表硬件校准或放电证明。" });
                dialog.ShowDialog(this);
            }
        }
    }
}
