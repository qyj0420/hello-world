using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class HelpForm : Form
    {
        private readonly AppSettings settings;
        private readonly string moduleId;
        private readonly Label heading;
        private readonly Label modelLabel;
        private readonly TextBox information;
        private readonly TextBox manualPath;
        private readonly Button openManualButton;
        private readonly Button closeButton;

        public HelpForm(AppSettings value)
            : this(value, String.Empty)
        {
        }

        public HelpForm(AppSettings value, string activeModuleId)
        {
            settings = value ?? AppSettings.Load();
            moduleId = activeModuleId ?? String.Empty;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = AppTheme.Canvas;
            ClientSize = new Size(720, 540);
            MinimumSize = new Size(660, 490);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            AppTheme.ApplyDialogIcon(this);

            Panel header = new Panel { Dock = DockStyle.Top, Height = 94, BackColor = AppTheme.Surface, Padding = new Padding(24, 15, 24, 12) };
            PictureBox logo = new PictureBox { Dock = DockStyle.Left, Width = 260, SizeMode = PictureBoxSizeMode.Zoom, Image = AppTheme.LoadLogo() };
            heading = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = AppTheme.Navy,
                Font = AppTheme.Font(16f, FontStyle.Bold) };
            header.Controls.Add(heading);
            header.Controls.Add(logo);

            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 20),
                BackColor = AppTheme.Canvas, AutoScroll = true };
            modelLabel = new Label { Dock = DockStyle.Top, Height = 32, ForeColor = AppTheme.Ink, Font = AppTheme.Font(11f, FontStyle.Bold) };
            information = new TextBox { Dock = DockStyle.Top, Height = 250, Multiline = true, ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle, BackColor = AppTheme.Surface, ForeColor = AppTheme.Ink,
                Font = AppTheme.Font(9.5f, FontStyle.Regular), ScrollBars = ScrollBars.Vertical, TabStop = false };

            Label manualCaption = new Label { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 12, 0, 0),
                ForeColor = AppTheme.Muted, Font = AppTheme.Font(9f, FontStyle.Regular) };
            Panel manualRow = new Panel { Dock = DockStyle.Top, Height = 42 };
            openManualButton = AppTheme.Button(String.Empty, true);
            openManualButton.Dock = DockStyle.Right;
            openManualButton.Width = 126;
            openManualButton.Click += OpenManual;
            manualPath = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White,
                ForeColor = AppTheme.Muted, Font = AppTheme.Font(9f, FontStyle.Regular) };
            manualRow.Controls.Add(manualPath);
            manualRow.Controls.Add(openManualButton);

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 49,
                FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 0) };
            closeButton = AppTheme.Button(String.Empty, false);
            closeButton.Width = 100;
            closeButton.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(closeButton);
            var glossaryButton = AppTheme.Button(IsChinese ? "专业术语表" : "Technical glossary", false);
            glossaryButton.Name = "TechnicalGlossaryButton";
            glossaryButton.AutoSize = true;
            glossaryButton.Click += delegate
            {
                string glossary = ResolveGlossaryPath();
                if (File.Exists(glossary)) Process.Start(new ProcessStartInfo(glossary) { UseShellExecute = true });
                else MessageBox.Show(this, IsChinese ? "当前程序目录中未找到专业术语表。" : "The technical glossary is missing from this application folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            buttons.Controls.Add(glossaryButton);

            body.Controls.Add(buttons);
            body.Controls.Add(manualRow);
            body.Controls.Add(manualCaption);
            body.Controls.Add(information);
            body.Controls.Add(modelLabel);
            Controls.Add(body);
            Controls.Add(header);
            CancelButton = closeButton;
            body.Layout += delegate
            {
                ResponsiveFormLayout.MaintainVerticalCanvas(body,
                    modelLabel, information, manualCaption, manualRow, buttons);
            };
            ResponsiveFormLayout.Attach(this, new Size(560, 420));

            manualCaption.Text = AppLanguages.Display(settings.LanguageCode, IsChinese ? "说明书路径" : "User manual");
            ApplyLanguage();
        }

        private bool IsChinese
        {
            get { return AppLanguages.IsChinese(settings.LanguageCode); }
        }

        private void ApplyLanguage()
        {
            string moduleHelp = ModuleHelpText();
            string moduleTitle = ModuleHelpTitle();
            Text = moduleTitle.Length == 0
                ? AppLanguages.Text("Help", settings.LanguageCode)
                : moduleTitle + " · " + AppLanguages.Text("Help", settings.LanguageCode);
            heading.Text = moduleTitle.Length == 0 ? AppLanguages.Text("Help", settings.LanguageCode) : moduleTitle;
            modelLabel.Text = (IsChinese ? "软件型号：" : "Software model: ") +
                (String.IsNullOrWhiteSpace(settings.SoftwareModel) ? "HV-DAC Xpert" : settings.SoftwareModel);
            manualPath.Text = ResolveManualPath();
            openManualButton.Text = IsChinese ? "打开说明书" : "Open manual";
            closeButton.Text = IsChinese ? "关闭" : "Close";
            openManualButton.Text = AppLanguages.Display(settings.LanguageCode, openManualButton.Text);
            closeButton.Text = AppLanguages.Display(settings.LanguageCode, closeButton.Text);
            openManualButton.Width = Math.Max(openManualButton.Width, TextRenderer.MeasureText(openManualButton.Text, openManualButton.Font).Width + 28);
            closeButton.Width = Math.Max(closeButton.Width, TextRenderer.MeasureText(closeButton.Text, closeButton.Font).Width + 28);
            information.Text = moduleHelp.Length > 0 ? moduleHelp : IsChinese
                ? "使用说明\r\n\r\n" +
                  "加载或建立项目后，可进行数据浏览、分析、报告、校准和加压测试。离线模式可用于项目管理和数据分析，不会连接设备、安装驱动或进行实机测试。\r\n\r\n" +
                  "安全提示\r\n\r\n" +
                  "实机测试前须加载有效项目，连接指定设备，核对并保存该设备的参数，完成适用校准。首次设置取消或未保存时不能进入实机加压。仿真结果不能作为实测数据。请由具备资质的人员按现场安全规程操作，并在接线或维护前断电、独立放电并确认隔离。"
                  + "\r\n\r\n每轮加压前，在页面内核对本轮条件后选择“确认本轮并开始”。自动模式不会代替本轮确认。急停、设备失联或反馈异常时请停止操作并核查原因；解除急停不会自动继续加压。设备连接、参数保存、校准完成或采集结束，均不表示设备已无残压或可以接近。"
                : "Using the application\r\n\r\n" +
                  "Load or create a project to browse data, analyse results, generate reports, calibrate and run voltage tests. Offline mode supports project management and data analysis without connecting devices, installing drivers or running a live test.\r\n\r\n" +
                  "Safety\r\n\r\n" +
                  "Before a live test, load a valid project, connect the designated device, check and save its settings, and complete the applicable calibration. Live voltage testing is unavailable if first-time settings are cancelled or left unsaved. Simulation results are not measured data. Qualified personnel must follow site procedures, disconnect power, independently discharge the equipment and confirm isolation before wiring or maintenance.\r\n\r\n" +
                  "Before every voltage-test round, check the conditions on the page and select Confirm this round and start. Automatic mode does not replace this confirmation. Stop and investigate an emergency stop, device disconnection or feedback fault. Releasing the emergency stop does not resume voltage output. A connection, saved settings, completed calibration or completed acquisition does not mean residual voltage is absent or the equipment is safe to approach.";
            information.Text = AppLanguages.Display(settings.LanguageCode, information.Text);
        }

        private string ModuleHelpTitle()
        {
            switch (moduleId)
            {
                case "ProjectManagement":
                case "ProjectCreation":
                case "DataBrowser":
                case "DataAnalysis":
                case "ReportGenerator":
                case "PressureTest":
                case "CalibrationTest":
                    return AppLanguages.Text(moduleId, settings.LanguageCode);
                default:
                    return String.Empty;
            }
        }

        private string ModuleHelpText()
        {
            if (IsChinese)
            {
                switch (moduleId)
                {
                    case "ProjectManagement": return "项目管理\r\n\r\n按项目名称或测试时间查找记录，选择项目并加载。加载成功后，该项目成为当前项目；数据浏览、分析、报告、校准和加压入口随之启用。项目目录应包含 ITEM.in 和 CABLE.in。\r\n\r\n对删除和导入操作，请先核对目标项目。";
                    case "ProjectCreation": return "创建项目\r\n\r\n录入检测单位、电缆起终端、长度、型号和测试信息，然后保存项目。保存成功后可将它作为当前项目，继续校准、加压或离线数据处理。";
                    case "DataBrowser": return "数据浏览\r\n\r\n加载项目后浏览已保存的采集记录与波形。选择记录可查看对应的测试时间、通道和数据；此页面不会启动硬件测试。";
                    case "DataAnalysis": return "数据分析\r\n\r\n加载项目数据后，使用自动或人工分析检查脉冲、反射定位及放电类型。学习模式由人工确认候选和结论，修正与评分保存在本机供后续参考。保存结果后可用于报告。";
                    case "ReportGenerator": return "生成报告\r\n\r\n选择当前项目的分析结果，检查报告内容和结论，再按设置导出 Word、PDF 或两者。若没有可用分析结果，请先完成数据分析。";
                    case "PressureTest": return "加压测试\r\n\r\n先加载有效项目并确认设备连接状态。仿真标识表示不会得到实测结果；实机页面中的操作由现场人员按设备联锁与安全规程执行。若提示需配置设备参数，先核对分压比、最高限压等关键参数。";
                    case "CalibrationTest": return "校准测试\r\n\r\n先加载有效项目并检查设备连接及参数配置。选择相别与已知注入电量，采集后核对波形和提示，再保存校准。三相电缆长度不一致时须分别校准；等长时可使用适用的单相或 ALL 校准。仿真结果不能作为实测校准系数。校准完成不表示可以加压，每轮加压仍须确认现场条件。";
                }
            }
            else
            {
                switch (moduleId)
                {
                    case "ProjectManagement": return "Project management\r\n\r\nFind records by project name or test time, then load a project. A loaded project becomes active and enables browsing, analysis, reports, calibration and pressure-test pages. The project folder must contain ITEM.in and CABLE.in.\r\n\r\nCheck the target carefully before deleting or importing data.";
                    case "ProjectCreation": return "Create project\r\n\r\nEnter the organization, cable ends, length, model and test details, then save the project. A saved project can become active for calibration, voltage testing or offline processing.";
                    case "DataBrowser": return "Data browser\r\n\r\nBrowse saved acquisitions and waveforms in the active project. Select a record to inspect its test time, channel and data. This page does not start a hardware test.";
                    case "DataAnalysis": return "Data analysis\r\n\r\nLoad project data and review pulses, reflection location and discharge type with automatic or manual analysis. Learning mode keeps human confirmations, corrections and conclusion ratings locally for later reference. Save the result for reporting.";
                    case "ReportGenerator": return "Report generation\r\n\r\nSelect analysis results for the active project, review report content and conclusions, then export Word, PDF or both as configured. Complete analysis first if no result is available.";
                    case "PressureTest": return "Voltage test\r\n\r\nLoad a valid project and check device connection. A simulation badge means the output is not measured data. Live controls are operated by qualified personnel under the equipment interlocks and site procedure. If prompted, verify the divider ratio, voltage limit and other key device settings first.";
                    case "CalibrationTest": return "Calibration test\r\n\r\nLoad a valid project and check the device connection and settings. Select the phase and known injected charge, acquire the waveform, review the waveform and messages, then save the calibration. Unequal phase lengths require separate calibrations; equal lengths may share an applicable single-phase or ALL calibration. Simulation results are not measured calibration coefficients. Completed calibration does not authorize voltage output; confirm site conditions before every voltage-test round.";
                }
            }
            return String.Empty;
        }

        private string ResolveManualPath()
        {
            string packaged = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "说明书",
                IsChinese ? "HV-DAC_Xpert_Operation_Manual_ZH.pdf" : "HV-DAC_Xpert_Operation_Manual_EN.pdf");
            if(File.Exists(packaged))return packaged;
            string path = (settings.ManualPath ?? String.Empty).Trim();
            return path.Length == 0
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "manual", IsChinese ? "index.html" : "manual-en.html")
                : path;
        }

        private string ResolveGlossaryPath()
        {
            string packaged=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"专业术语","Electrical_Testing_Terminology_EN-ZH.pdf");
            if(File.Exists(packaged))return packaged;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "docs", "international", "electrical-glossary.html");
        }

        private void OpenManual(object sender, EventArgs e)
        {
            string path = ResolveManualPath();

            Uri uri;
            bool isWeb = Uri.TryCreate(path, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            if (!isWeb && !File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show(this,
                    IsChinese ? "说明书未找到。请在设置中重新选择说明书。" : "The manual was not found. Select it again in Settings.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                new Diagnostics().Error("Manual", "Could not open the manual.", error);
                MessageBox.Show(this,
                    IsChinese ? "无法打开说明书。请检查文件后重试。" : "Could not open the manual. Check the file and retry.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
