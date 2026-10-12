using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        private readonly Button diagnoseP04 = Button("P0.4 只读诊断（不加压）", 240);
        private readonly Button diagnoseAi = Button("AI / P0.4 只读诊断（不加压）",280);
        private async Task DiagnoseAi()
        {
            if (busy || scanning || exporting || labelProcessing || smokeMode) return;
            FactorySettings settings;
            try { settings = ReadSettings(); FactoryAiDiagnostic.Validate(settings); }
            catch (Exception ex) { status.Text = ex.Message; tabs.SelectedTab = parametersPage; return; }
            scanning = true; cancellation = new CancellationTokenSource(); SetBusy(true);
            string directory = NewDirectory("AI_DIAG");
            try
            {
                await SuspendConnectionMonitor();
                string result = await Program.DiagnoseFeedback(settings,directory,Progress,cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                status.Text = "AI / P0.4 只读诊断已保存：" + directory;
                if (!closeAfterStop) using (var dialog = new Form { Text = "AI / P0.4 原始反馈 · 未操作输出", Icon = Icon, Font = Font,
                    Size = new Size(1060,690), StartPosition = FormStartPosition.CenterParent })
                { dialog.Controls.Add(new TextBox { Text = result, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill }); dialog.ShowDialog(this); }
            }
            catch(OperationCanceledException) {status.Text="AI / P0.4 诊断已停止，等待资源释放；不再弹出结果窗口。";}
            catch (Exception ex) { Progress("AI_DIAG_ERROR",ex.ToString()); }
            finally { scanning = false; cancellation.Dispose(); cancellation = null; SetBusy(false); if (closeAfterStop) Close(); }
        }
        private async Task DiagnoseP04()
        {
            if (busy || scanning || exporting || labelProcessing || smokeMode) return;
            FactorySettings settings;
            try { settings = ReadSettings(); FactoryP04Diagnostic.Validate(settings.Input); }
            catch (Exception ex) { status.Text = ex.Message; tabs.SelectedTab = parametersPage; return; }
            scanning = true; cancellation = new CancellationTokenSource(); SetBusy(true);
            string directory = NewDirectory("P04_DIAG");
            try
            {
                await SuspendConnectionMonitor();
                Progress("P04_DIAG", "只配置 P0.4 输入，不创建 AO/DO，不打开 Pico；诊断结果不会改变加压许可。");
                string result = await Program.DiagnoseP04(settings, directory, Progress, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                status.Text = "P0.4 只读诊断已保存：" + directory;
                if (!closeAfterStop) ShowP04Diagnostic(result + "\r\n记录目录：" + directory);
            }
            catch(OperationCanceledException) {status.Text="P0.4 诊断已停止，等待资源释放；不再弹出结果窗口。";}
            catch (Exception ex)
            {
                Progress("P04_DIAG_ERROR", ex.Message);
                string progress = Path.Combine(directory, "progress.log");
                if (!closeAfterStop) ShowP04Diagnostic((File.Exists(progress) ? File.ReadAllText(progress) : "") + "\r\n诊断未完成：" + ex.Message + "\r\n记录目录：" + directory);
            }
            finally { scanning = false; cancellation.Dispose(); cancellation = null; SetBusy(false); if (closeAfterStop) Close(); }
        }
        private void ShowP04Diagnostic(string text)
        {
            using (var dialog = new Form { Text = "P0.4 只读诊断 · 不构成加压许可", Icon = Icon, Font = Font, Size = new Size(1060, 690),
                MinimumSize = new Size(750, 450), StartPosition = FormStartPosition.CenterParent })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(10) };
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                layout.Controls.Add(new Label { Text = "仅 P0.4 输入与 Port0 原始只读回读；没有 AO/DO 写入。高/低均只是诊断，不验证残压或完整联锁。", Dock = DockStyle.Fill, ForeColor = Color.DarkRed }, 0, 0);
                layout.Controls.Add(new TextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill }, 0, 1);
                var copy = Button("复制全部诊断", 180); copy.Click += (s,e) => { try { Clipboard.SetText(text); copy.Text = "已复制"; } catch (Exception ex) { copy.Text = "复制失败"; status.Text = ex.Message; } };
                layout.Controls.Add(copy, 0, 2); dialog.Controls.Add(layout); dialog.ShowDialog(this);
            }
        }
    }
}
