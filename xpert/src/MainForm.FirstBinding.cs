using System;
using System.IO;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private bool firstBindingInProgress;

        private void BeginFirstHardwareBinding()
        {
            ManageDetectionDevices();
        }

        // Retained only as the old installer handoff reference. No current UI
        // entry may start a separate private binding implementation.
        private async void LegacyPrivateHardwareBinding()
        {
            if (verificationMode || firstBindingInProgress || settingsDialogActive ||
                activeModule != null || IsHardwareEnabled || hardwareDisconnectInProgress) return;
            string installationIssue = FirstHardwareProvisioning.InstallationGate();
            if (installationIssue.Length > 0)
            {
                if (FirstHardwareProvisioning.CanResumeInstallation() &&
                    MessageBox.Show(this, installationIssue + "\r\n\r\n现在继续安装？Windows 可能要求管理员确认，且驱动可能要求重启。",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    try { FirstHardwareProvisioning.ResumeInstallation(); }
                    catch (Exception error)
                    { MessageBox.Show(this, "无法继续安装：" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
                else if (!FirstHardwareProvisioning.CanResumeInstallation())
                    MessageBox.Show(this, installationIssue, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (settings.SessionHardwareForcedOffline)
            {
                MessageBox.Show(this,"本次软件会话按安装状态保持离线。请完成硬件组件安装并重新打开软件后再首次绑定。",
                    Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this,
                "首次绑定将打开设备配置界面并请求管理员权限。请仅选择当前信号汇集器上的一张 Controller 和一张 Acquisition；完成后本软件会再做精确身份检查。是否继续？",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            string initialSettingsDigest;
            try { initialSettingsDigest = SettingsDigest(); }
            catch (Exception error)
            {
                MessageBox.Show(this, "无法读取当前配置：" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            firstBindingInProgress = true;
            settingsDialogActive = true;
            if (devicePollTimer != null) devicePollTimer.Stop();
            Enabled = false;
            UseWaitCursor = true;
            ProvisioningActivation activation = null;
            Exception failure = null;
            AppSettings basis = settings.Clone();
            try { activation = await FirstHardwareProvisioning.RunAsync(basis,delegate {
                if (IsDisposed) return;
                try { BeginInvoke(new Action(delegate {
                    if (!IsDisposed) MessageBox.Show(this,
                        "首次绑定已超过 15 分钟；本次结果不会导入。请关闭仍在运行的旧绑定窗口，软件将保持等待，避免重复 Apply。",
                        Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);
                })); } catch {}
            }); }
            catch (Exception error) { failure = error; }
            finally
            {
                if (!IsDisposed) { Enabled = true; UseWaitCursor = false; }
                settingsDialogActive = false;
                firstBindingInProgress = false;
            }
            if (IsDisposed) return;
            if (failure != null)
            {
                MessageBox.Show(this, "首次绑定未完成，仍保持离线：\r\n" + failure.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                AppSettings latest;
                string diagnostic = String.Empty;
                if (!File.Exists(AppPaths.SettingsFile) ||
                    !AppSettings.TryLoad(AppPaths.SettingsFile,out latest,out diagnostic))
                    throw new InvalidDataException("无法重新核对当前配置文件：" + diagnostic);
                if (latest.Hardware == null || latest.Hardware.Enabled)
                    throw new InvalidOperationException("其他操作已更改硬件配置；本次结果未覆盖，请重新打开软件核对。");
                AppSettings next = FirstHardwareProvisioning.ApplyToClone(latest,activation);
                if (!next.TrySaveIfUnchanged(initialSettingsDigest,false,
                    () => ProvisioningImportAdapter.IsCurrentActivation(activation,DateTime.UtcNow)))
                    throw new InvalidOperationException("绑定期间配置已被其他程序修改；本次结果未覆盖。");
                settings = next;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "本次设备结果未保存；请核对当前配置：\r\n" + error.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // The atomic commit has succeeded. Any subsequent UI/service failure
            // must not be reported as an unsaved/offline binding.
            try
            {
                driverPromptShown = false;
                hardwareManuallyDisconnected = false;
                RecreateIntegrationServices();
                ApplyLanguageAndBranding();
                ApplyState();
                BeginHardwareRefresh(true,true);
                if (devicePollTimer != null) devicePollTimer.Start();
                MessageBox.Show(this,
                    "首次绑定已通过本次设备身份检查。后续打开本软件会连接指定设备，无需再次打开首次配置窗口。设备关键参数仍须在正式试验前核对。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                MessageBox.Show(this,
                    "设备绑定配置已保存，但当前窗口或连接服务初始化失败。请关闭并重新打开软件核对设备状态：\r\n" + error.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string SettingsDigest()
        {
            if (!File.Exists(AppPaths.SettingsFile))
                throw new FileNotFoundException("设置文件不存在。");
            return AppSettings.FileDigest(AppPaths.SettingsFile);
        }
    }
}
