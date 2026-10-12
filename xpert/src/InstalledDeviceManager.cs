using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    internal static class InstalledDeviceManager
    {
        internal const string ReviewedExecutableHash = "535F644FD7200B53C9C33B20CE0EC9ED908106C86732E00DC1482AA6FC608D5A";
        internal static string VerifiedPath()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HV DAC Xpert", "HV-DAC-Xpert.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("未找到已安装的振荡波驱动包，请先安装驱动包。", path);
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)); dir != null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动包目录不可为重定向链接。");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("驱动包程序不可为重定向链接。");
            string actual;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (actual != ReviewedExecutableHash) {
                var error = new InvalidDataException("驱动包版本与本次核查版本不一致，未自动启动。请核对新版本兼容性。");
                error.Data["ExecutablePath"] = path;
                error.Data["ExpectedSHA256"] = ReviewedExecutableHash;
                error.Data["ActualSHA256"] = actual;
                error.Data["FileVersion"] = FileVersionInfo.GetVersionInfo(path).FileVersion;
                throw error;
            }
            return path;
        }
        internal static Task RunAsync()
        { return RunAsync(null); }
        internal static Task RunAsync(Action started)
        {
            return Task.Run(delegate {
                string path = VerifiedPath();
                // Operator explicitly opens this interactive driver UI. No SDK
                // or binding operation is implemented by the desktop host.
                using (var process = Process.Start(new ProcessStartInfo {
                    FileName = path, WorkingDirectory = Path.GetDirectoryName(path),
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal }))
                {
                    if (process == null) throw new IOException("驱动包设备管理界面未启动。");
                    // Verification/UAC may still fail before this point. Only
                    // the actual successful Process.Start reports an open UI.
                    if (started != null) started();
                    process.WaitForExit();
                    if (process.ExitCode != 0) throw new IOException("驱动包退出码：" + process.ExitCode + "；未确认绑定成功。");
                }
                // Zero exit is not proof of binding or acquisition readiness.
            });
        }
    }
}
