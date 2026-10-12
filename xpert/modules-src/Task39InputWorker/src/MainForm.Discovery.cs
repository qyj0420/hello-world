using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.ZeroBench
{
    internal sealed partial class MainForm
    {
        // Exercises real enumeration through the same UI path. It never calls
        // StartRun, never opens cards, and does not alter saved user settings.
        internal async Task ScanSmokeAsync(string screenshotPath)
        {
            smokeMode = true;
            try
            {
                mode.SelectedIndex = 1;
                bool ready = await DiscoverCards();
                if (!ready || !SelectedCardsAvailable()) throw new InvalidOperationException("只读枚举界面未形成可选择的双卡结果：" + status.Text);
                if (!artChoice.Enabled || !picoChoice.Enabled || !settingsPanel.Enabled || discovering)
                    throw new InvalidOperationException("枚举完成后设备选择控件仍被禁用。");
                int artIndex = artChoice.SelectedIndex, picoIndex = picoChoice.SelectedIndex;
                string expectedArt = artSerial.Text, expectedPico = picoSerial.Text;
                artChoice.SelectedIndex = picoChoice.SelectedIndex = -1;
                if (SelectedCardsAvailable()) throw new InvalidOperationException("未选择设备仍被认定可用。");
                artChoice.SelectedIndex = artIndex; picoChoice.SelectedIndex = picoIndex;
                if (!SelectedCardsAvailable() || artSerial.Text != expectedArt || picoSerial.Text != expectedPico)
                    throw new InvalidOperationException("重新选择条目后串号未能正确更新。");
                string path = Path.GetFullPath(screenshotPath); Directory.CreateDirectory(Path.GetDirectoryName(path));
                SaveSmokeImage(path);
                File.WriteAllText(path + ".txt", "UI_DISCOVERY_CHECK=PASS" + Environment.NewLine +
                    "ART=" + artDevice.Text + " / " + artSerial.Text + Environment.NewLine + "PICO=" + picoSerial.Text + Environment.NewLine +
                    "两卡已自动填入；控件已启用；取消选择后重新选择成功。只读枚举，未初始化、未采集、未写输出。", System.Text.Encoding.UTF8);
            }
            finally { smokeMode = false; }
        }

        private async Task<bool> DiscoverCards()
        {
            if (discovering || cancellation != null || exportInProgress || IsDisposed) return false;
            discovering = true; discoveryCancellation = new CancellationTokenSource();
            start.Enabled = discover.Enabled = export.Enabled = settingsPanel.Enabled = mode.Enabled = false;
            string priorArt = artSerial.Text.Trim(), priorPico = picoSerial.Text.Trim();
            status.Text = "正在枚举 ART 与 Pico，只读取设备信息，不打开采集卡、不写输出……";
            try
            {
                var request = new BenchSettings { ArtLibraryDirectory = artLibrary.Text.Trim(), PicoLibraryDirectory = picoLibrary.Text.Trim() };
                DiscoveryResult scan = await DiscoveryRunner.RunAsync(request, (key, message) => Progress("INFO", message), discoveryCancellation.Token);
                discoveryCancellation.Token.ThrowIfCancellationRequested();
                ApplyDiscovery(scan, priorArt, priorPico);
                Progress("INFO", "枚举结果：ART " + scan.ArtDevices.Count + " 台，Pico " + scan.PicoSerials.Count + " 台；枚举成功不代表已完成采集初始化。");
                if (!String.IsNullOrEmpty(scan.ArtError)) Progress("INFO", "ART 枚举：" + scan.ArtError);
                if (!String.IsNullOrEmpty(scan.PicoError)) Progress("INFO", "Pico 枚举：" + scan.PicoError);
                if (SelectedCardsAvailable())
                {
                    status.Text = "已获取 ART " + artDevice.Text + " / " + artSerial.Text + "，Pico " + picoSerial.Text + "；点击开始才初始化采集。";
                    PersistIdentitySelection();
                    return true;
                }
                status.Text = (!String.IsNullOrEmpty(scan.ArtError) || !String.IsNullOrEmpty(scan.PicoError))
                    ? "设备枚举未全部成功，详见日志；请检查运行库与连接后重新扫描。"
                    : "已列出在线设备：请选择两卡。原卡缺失时不会自动切换到其他序列号。";
                return false;
            }
            catch (OperationCanceledException) { status.Text = "设备扫描已取消。"; return false; }
            catch (Exception ex)
            {
                detectedDevices = null; artChoice.Items.Clear(); picoChoice.Items.Clear();
                status.Text = "扫描失败，不能认定两卡可用；请查看日志后重新扫描。";
                Progress("INFO", "扫描失败：" + ex.Message); return false;
            }
            finally
            {
                discoveryCancellation.Dispose(); discoveryCancellation = null; discovering = false;
                if (!IsDisposed && !Disposing)
                {
                    start.Enabled = discover.Enabled = settingsPanel.Enabled = mode.Enabled = true;
                    export.Enabled = latest != null && analysis != null;
                    if (closeAfterStop) Close();
                }
            }
        }

        private void ApplyDiscovery(DiscoveryResult scan, string priorArt, string priorPico)
        {
            detectedDevices = scan; applyingDiscovery = true;
            try
            {
                artChoice.Items.Clear(); picoChoice.Items.Clear();
                foreach (var item in scan.ArtDevices) artChoice.Items.Add(item.Name + " / " + item.Serial);
                foreach (string serial in scan.PicoSerials) picoChoice.Items.Add(serial);
                var selection = DiscoverySelection.Resolve(scan, priorArt, priorPico);
                int ai = selection.ArtDevice == null ? -1 : scan.ArtDevices.IndexOf(selection.ArtDevice);
                int pi = scan.PicoSerials.IndexOf(selection.PicoSerial);
                Progress("INFO", selection.ArtMessage + "；" + selection.PicoMessage);
                if (!String.IsNullOrEmpty(scan.ArtError)) ai = -1;
                if (!String.IsNullOrEmpty(scan.PicoError)) pi = -1;
                artChoice.SelectedIndex = ai; picoChoice.SelectedIndex = pi;
                // Keep the previous identity text when missing. It remains a lock, not a live result.
                if (ai < 0) artChoice.Text = "";
                if (pi < 0) picoChoice.Text = "";
            }
            finally { applyingDiscovery = false; }
            ApplyArtChoice(); ApplyPicoChoice();
        }

        private void ApplyArtChoice()
        {
            if (applyingDiscovery || detectedDevices == null || artChoice.SelectedIndex < 0) return;
            var item = detectedDevices.ArtDevices[artChoice.SelectedIndex];
            bool changed = artDevice.Text != item.Name || artSerial.Text != item.Serial;
            artDevice.Text = item.Name; artSerial.Text = item.Serial;
            if (changed) rangeState.Reset();
            if (!discovering) PersistIdentitySelection();
        }
        private void ApplyPicoChoice()
        {
            if (applyingDiscovery || detectedDevices == null || picoChoice.SelectedIndex < 0) return;
            bool changed = picoSerial.Text != detectedDevices.PicoSerials[picoChoice.SelectedIndex];
            picoSerial.Text = detectedDevices.PicoSerials[picoChoice.SelectedIndex];
            if (changed) rangeState.Reset();
            if (!discovering) PersistIdentitySelection();
        }
        private void PersistIdentitySelection()
        {
            if (smokeMode) return;
            try
            {
                // Enumeration changes identity, not the last acquisition parameters.
                BenchSettings saved = File.Exists(LastSettingsPath) ? BenchSettings.Load(LastSettingsPath) : new BenchSettings();
                saved.ArtDevice = artDevice.Text.Trim(); saved.ArtSerial = artSerial.Text.Trim(); saved.PicoSerial = picoSerial.Text.Trim();
                saved.ArtLibraryDirectory = artLibrary.Text.Trim(); saved.PicoLibraryDirectory = picoLibrary.Text.Trim();
                saved.ArtTrigger = "/" + saved.ArtDevice + "/PFI" + trigger.SelectedIndex;
                saved.Save(LastSettingsPath);
            }
            catch (Exception ex) { Progress("INFO", "身份已选，但保存失败：" + ex.Message); }
        }
        private bool SelectedCardsAvailable()
        {
            return detectedDevices != null && String.IsNullOrEmpty(detectedDevices.ArtError) && String.IsNullOrEmpty(detectedDevices.PicoError) &&
                artChoice.SelectedIndex >= 0 && picoChoice.SelectedIndex >= 0 &&
                detectedDevices.ArtDevices[artChoice.SelectedIndex].Name == artDevice.Text &&
                detectedDevices.ArtDevices[artChoice.SelectedIndex].Serial == artSerial.Text &&
                detectedDevices.PicoSerials[picoChoice.SelectedIndex] == picoSerial.Text;
        }

        // UI regression only: caller sets smokeMode, so synthetic selections
        // cannot be persisted and no native discovery or acquisition is invoked.
        private void VerifyDiscoveryUi()
        {
            if (!smokeMode) throw new InvalidOperationException("设备选择界面自检只允许离线 smoke 模式。");
            var pair = new DiscoveryResult();
            pair.ArtDevices.Add(new ArtDeviceIdentity { Name = "SIM-Dev1", Model = "USB3135", Serial = "900000001" });
            pair.PicoSerials.Add("SIM-PICO-1");
            ApplyDiscovery(pair, "", "");
            if (!SelectedCardsAvailable() || artDevice.Text != "SIM-Dev1" || picoSerial.Text != "SIM-PICO-1")
                throw new Exception("唯一双卡未自动填入。");
            pair.ArtDevices.Add(new ArtDeviceIdentity { Name = "SIM-Dev2", Model = "USB3135", Serial = "900000002" });
            pair.PicoSerials.Add("SIM-PICO-2");
            ApplyDiscovery(pair, "", "");
            if (SelectedCardsAvailable() || artChoice.SelectedIndex != -1 || picoChoice.SelectedIndex != -1)
                throw new Exception("多卡未保留人工选择。");
            artChoice.SelectedIndex = 1; picoChoice.SelectedIndex = 1;
            if (!SelectedCardsAvailable() || artSerial.Text != "900000002" || picoSerial.Text != "SIM-PICO-2")
                throw new Exception("多卡手选未更新锁定身份。");
            ApplyDiscovery(pair, "900000002", "SIM-PICO-2");
            if (!SelectedCardsAvailable() || artChoice.SelectedIndex != 1 || picoChoice.SelectedIndex != 1)
                throw new Exception("再次扫描未保留原串号。");
            pair.ArtDevices.RemoveAt(1); pair.PicoSerials.RemoveAt(1);
            ApplyDiscovery(pair, "900000002", "SIM-PICO-2");
            if (SelectedCardsAvailable() || artSerial.Text != "900000002" || picoSerial.Text != "SIM-PICO-2")
                throw new Exception("原卡缺失不应自动替换身份。");
            pair.PicoError = "离线自检注入错误";
            ApplyDiscovery(pair, "900000001", "SIM-PICO-1");
            if (SelectedCardsAvailable() || picoChoice.SelectedIndex != -1)
                throw new Exception("枚举错误不得认定两卡可用。");
            Progress("INFO", "设备选择界面自检通过：唯一双卡、多卡手选、原卡保留、缺卡不替换、枚举错误阻断；未调用 SDK。");
        }
    }

    internal static class DiscoveryRunner
    {
        internal static async Task<DiscoveryResult> RunAsync(BenchSettings settings, Action<string, string> report, CancellationToken cancellation)
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "records", "SCAN_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            string settingsPath = Path.Combine(directory, "settings.xml"); settings.Save(settingsPath);
            using (var log = new StreamWriter(Path.Combine(directory, "scan.log"), false, System.Text.Encoding.UTF8))
            {
                object gate = new object();
                await BenchRunner.RunCommandAsync("--discover", settingsPath, directory, 20000, (stage, message) =>
                { lock (gate) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + stage + " | " + message); log.Flush(); } report(stage, message); }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                return DiscoveryResult.Load(Path.Combine(directory, "devices.xml"));
            }
        }
    }
}
