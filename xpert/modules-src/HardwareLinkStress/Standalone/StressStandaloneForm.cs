using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace HVX.HardwareLinkStress.Standalone
{
    internal sealed class StressStandaloneForm : Form
    {
        private readonly ComboBox previewTier = new ComboBox();
        private readonly Label previewTierDetail = new Label();

        internal StressStandaloneForm(IStressUiHost verifiedHost)
        {
            Text = "HV DAC Xpert · 链路稳定性诊断";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 620);
            ClientSize = new Size(940, 720);
            BackColor = Color.FromArgb(246, 249, 252);
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = LoadBrandIcon();

            var page = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(22, 18, 22, 18),
                BackColor = BackColor
            };
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(page);

            page.Controls.Add(CreateHeader(verifiedHost != null), 0, 0);
            page.Controls.Add(CreateNotice(verifiedHost != null), 0, 1);
            page.Controls.Add(verifiedHost == null
                ? CreatePreviewPanel()
                : (Control)new StressMonitorControl(verifiedHost), 0, 2);
        }

        private Control CreateHeader(bool hasHost)
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 282));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var brand = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = LoadBrandImage(),
                Margin = new Padding(0, 5, 18, 5)
            };
            header.Controls.Add(brand, 0, 0);

            var title = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            title.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            title.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            title.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "硬件链路稳定性诊断",
                Font = new Font(Font.FontFamily, 20F, FontStyle.Bold),
                ForeColor = Color.FromArgb(23, 76, 143),
                TextAlign = ContentAlignment.BottomLeft
            }, 0, 0);
            title.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = hasHost ? "控制模块 · 采集模块 · 汇集器" : "离线界面预览 · 不访问任何硬件",
                ForeColor = Color.FromArgb(84, 101, 121),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);
            header.Controls.Add(title, 1, 0);
            return header;
        }

        private static Control CreateNotice(bool hasHost)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = hasHost ? Color.FromArgb(232, 242, 252) : Color.FromArgb(255, 247, 225),
                Margin = new Padding(0, 6, 0, 8),
                Padding = new Padding(14, 9, 14, 9)
            };
            panel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = hasHost
                    ? "已载入设备诊断组件。请核对设备身份；是否在线、传输量和测试结果以实际检测记录为准。"
                    : "当前是离线预览：未搜索、连接或读取设备，也未产生测试结果。设备测试按钮暂不可用。",
                ForeColor = hasHost ? Color.FromArgb(23, 76, 143) : Color.FromArgb(125, 78, 12),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = false
            });
            return panel;
        }

        private Control CreatePreviewPanel()
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.White,
                Padding = new Padding(18),
                AutoScroll = true,
                AutoScrollMinSize = new Size(0, 500)
            };
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));

            var status = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            for (int index = 0; index < 3; index++)
                status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / 3F));
            status.Controls.Add(CreateStatusCard("汇集器", "未检测"), 0, 0);
            status.Controls.Add(CreateStatusCard("控制模块", "未检测"), 1, 0);
            status.Controls.Add(CreateStatusCard("采集模块", "未检测"), 2, 0);
            body.Controls.Add(status, 0, 0);

            var tierPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            tierPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152));
            tierPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tierPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            tierPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tierPanel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "参考数据量档位",
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            previewTier.DropDownStyle = ComboBoxStyle.DropDownList;
            previewTier.Width = 165;
            previewTier.Items.AddRange(new object[] { "低", "中", "高" });
            tierPanel.Controls.Add(previewTier, 1, 0);
            previewTierDetail.Dock = DockStyle.Fill;
            previewTierDetail.ForeColor = Color.FromArgb(74, 91, 108);
            previewTierDetail.TextAlign = ContentAlignment.MiddleLeft;
            tierPanel.Controls.Add(previewTierDetail, 0, 1);
            tierPanel.SetColumnSpan(previewTierDetail, 2);
            previewTier.SelectedIndexChanged += (sender, args) => UpdateTierPreview();
            previewTier.SelectedIndex = 0;
            body.Controls.Add(tierPanel, 0, 1);

            var reconnect = new CheckBox
            {
                Dock = DockStyle.Fill,
                Text = "掉线后自动重连并继续测试（需接入经过验证的设备诊断组件）",
                Checked = false,
                Enabled = false
            };
            body.Controls.Add(reconnect, 0, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            var start = new Button { Text = "开始设备测试", Width = 154, Height = 34, Enabled = false };
            var close = new Button { Text = "关闭", Width = 108, Height = 34 };
            close.Click += (sender, args) => Close();
            actions.Controls.Add(start);
            actions.Controls.Add(close);
            body.Controls.Add(actions, 0, 3);

            var explanation = new Label
            {
                Dock = DockStyle.Fill,
                Text = "连接状态：未知（未检测）\r\n" +
                    "传输速度：未测量\r\n" +
                    "事故日志：本次未启动测试，因此没有生成。\r\n\r\n" +
                    "参考档位仅说明计划的数据量，不代表设备已连接或能够达到该速度。",
                ForeColor = Color.FromArgb(74, 91, 108),
                BackColor = Color.FromArgb(246, 249, 252),
                Padding = new Padding(14),
                Margin = new Padding(0, 4, 0, 0)
            };
            body.Controls.Add(explanation, 0, 4);
            return body;
        }

        private void UpdateTierPreview()
        {
            switch (previewTier.SelectedIndex)
            {
                case 0:
                    previewTierDetail.Text = "低：标称约 2.5 MB/段；完成一轮后至少休息 500 ms。";
                    break;
                case 1:
                    previewTierDetail.Text = "中：标称约 10 MB/段；完成一轮后至少休息 250 ms。";
                    break;
                default:
                    previewTierDetail.Text = "高：标称约 20 MB/段；实际数据量可能因设备容量受限。";
                    break;
            }
        }

        private static Control CreateStatusCard(string title, string state)
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(246, 249, 252),
                Margin = new Padding(0, 0, 12, 12),
                Padding = new Padding(13)
            };
            card.Controls.Add(new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Text = title,
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(28, 69, 123)
            });
            card.Controls.Add(new Label
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Text = state,
                ForeColor = Color.FromArgb(111, 120, 128),
                TextAlign = ContentAlignment.MiddleLeft
            });
            return card;
        }

        private static Icon LoadBrandIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Brand.ico"))
            {
                if (stream == null) throw new InvalidOperationException("程序图标资源缺失。");
                using (var source = new Icon(stream)) return (Icon)source.Clone();
            }
        }

        private static Image LoadBrandImage()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Brand.png"))
            {
                if (stream == null) throw new InvalidOperationException("品牌图像资源缺失。");
                using (var source = Image.FromStream(stream)) return new Bitmap(source);
            }
        }
    }
}
