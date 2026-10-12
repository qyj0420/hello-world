using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    internal static class ConnectionInspectionWorker
    {
        internal static void Execute(string requestPath, string module, string outputPath)
        {
            ConnectionInspectionResult result;
            try
            {
                ConnectionInspectionRequest request;
                using (var s = File.OpenRead(requestPath)) request = (ConnectionInspectionRequest)new XmlSerializer(typeof(ConnectionInspectionRequest)).Deserialize(s);
                result = ConnectionInspection.Inspect(request, module);
            }
            catch (Exception ex) { result = new ConnectionInspectionResult { Message = "读取连接校验请求失败。", Diagnostic = ex.ToString() }; }
            using (var s = new FileStream(outputPath, FileMode.CreateNew)) new XmlSerializer(typeof(ConnectionInspectionResult)).Serialize(s, result);
        }
        internal static Task<ConnectionInspectionResult> Run(string requestPath, string module, string outputPath, int timeoutMilliseconds = 10000)
        {
            return Task.Run(() =>
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(Application.ExecutablePath,
                        "--connection-check-worker \"" + requestPath + "\" " + module + " \"" + outputPath + "\"")
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                    try
                    {
                        process.Start();
                        if (!process.WaitForExit(timeoutMilliseconds))
                        {
                            // Only this owned discovery helper is terminated, never a device session.
                            process.Kill();
                            bool stopped = process.WaitForExit(2000);
                            return new ConnectionInspectionResult { WorkerStopped = stopped, Message = "连接校验超过 10 秒，已请求结束校验进程。请检查供电、通讯线和驱动后重试；这不是放电确认。" };
                        }
                        if (process.ExitCode != 0 || !File.Exists(outputPath)) return ConnectionInspection.Fail("校验进程异常退出，未取得有效结果。");
                        using (var s = File.OpenRead(outputPath)) return (ConnectionInspectionResult)new XmlSerializer(typeof(ConnectionInspectionResult)).Deserialize(s);
                    }
                    catch (Exception ex)
                    {
                        bool stopped = true;
                        try { if (!process.HasExited) { process.Kill(); stopped = process.WaitForExit(2000); } }
                        catch (InvalidOperationException) { } // process never started
                        catch { stopped = false; }
                        return new ConnectionInspectionResult { WorkerStopped = stopped, Message = "连接校验执行失败；详见诊断信息。", Diagnostic = ex.ToString() };
                    }
                }
            });
        }
    }
    internal sealed partial class MainForm
    {
        private readonly Button inspectConnection = Button("连接校验");
        private bool connectionInspectionBusy, connectionInspectionBlocked;
        private string inspectionIdentity = "", inspectionDetail = "", inspectionKey = "";
        private string InspectionKey { get { return activeMode + "|" + artName.Text.Trim() + "|" + picoSerial.Text.Trim() + "|" + picoRuntimeDirectory.Text.Trim(); } }
        private async Task InspectConnection(bool showDialog = true)
        {
            if (connectionInspectionBusy || connectionInspectionBlocked || calibrationBusy || session != null || analyzing || changingMode || resetting) return;
            connectionInspectionBusy = true; inspectionIdentity = ""; inspectionDetail = "";
            inspectionKey = InspectionKey; RefreshView();
            try
            {
                // Do not call ReadProfile/EffectiveHardware/Connect: unfilled HV parameters are irrelevant here.
                var request = new ConnectionInspectionRequest { ArtName = artName.Text.Trim(), PicoSerial = picoSerial.Text.Trim(),
                    PicoLibraryDirectory = picoRuntimeDirectory.Text.Trim(), Simulation = activeMode == 0 };
                string folder = Path.Combine(dataRoot, "connection-checks", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                string input = Path.Combine(folder, "request.xml");
                using (var s = new FileStream(input, FileMode.CreateNew)) new XmlSerializer(typeof(ConnectionInspectionRequest)).Serialize(s, request);
                var results = await Task.WhenAll(ConnectionInspectionWorker.Run(input, "art", Path.Combine(folder, "control.xml")),
                    ConnectionInspectionWorker.Run(input, "pico", Path.Combine(folder, "discharge.xml")));
                var art = results[0]; var pico = results[1];
                connectionInspectionBlocked = !art.WorkerStopped || !pico.WorkerStopped;
                string stamp = DateTime.Now.ToString("HH:mm:ss");
                inspectionIdentity = "校验快照 " + stamp + "  ART " + (art.Passed ? art.Serial : "未通过") + "  |  PICO " + (pico.Passed ? pico.Serial : "未通过");
                inspectionDetail = "控制模块：" + art.Message + "\r\n序列号：" + Known(art.Serial) + "\r\n\r\n局放模块：" + pico.Message + "\r\n序列号：" + Known(pico.Serial) +
                    "\r\n\r\n本次只枚举设备，没有建立加压会话、没有读写AO/DO或启动采集。校验快照不是持续连接状态，也不代表设备已放电。\r\n正式试验仍需填写有效参数并点击“连接 / 应用设置”。真实校准采集尚未接入。";
                if (connectionInspectionBlocked) inspectionDetail += "\r\n校验进程未确认退出，已禁止新连接；请退出并重新启动程序。";
                if (!String.IsNullOrEmpty(pico.RuntimePath)) inspectionDetail += "\r\n\r\n实际局放运行库：" + pico.RuntimePath;
                OperatorDiagnostics.Record(inspectionDetail + "\r\n" + art.Diagnostic + "\r\n" + pico.Diagnostic);
                uiError = art.Passed && pico.Passed ? "连接校验通过（仅设备枚举，未初始化）；请完成参数设置后正式连接。" : "连接校验未通过，详见结果和诊断信息。";
            }
            catch (Exception ex) { uiError = "连接校验失败：" + OperatorDiagnostics.Report(ex); inspectionDetail = uiError; }
            finally { connectionInspectionBusy = false; RefreshView(); }
            if (showDialog) using (var dialog = new BrandedForm { Text = "连接校验结果 · 不执行端口输出", Size = new Size(820, 430), StartPosition = FormStartPosition.CenterParent, Font = Font })
            {
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = inspectionDetail });
                dialog.ShowDialog(this);
            }
        }
    }
}
