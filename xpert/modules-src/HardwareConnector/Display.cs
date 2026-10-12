using System;
using System.IO;
using System.Reflection;
using System.ComponentModel;
using System.ServiceProcess;

[assembly: AssemblyTitle("HV DAC Xpert")]
[assembly: AssemblyProduct("HV DAC Xpert")]
[assembly: AssemblyCompany("雄新电力科技")]
[assembly: AssemblyDescription("信号汇集器与采集模块连接管理")]
[assembly: AssemblyVersion("1.4.1.0")]
[assembly: AssemblyFileVersion("1.4.1.0")]

namespace Xiongxin.HvDacXpert
{
    static class Display
    {
        public static string Hub {get{return L10n.DeviceName("HV-DAC Xpert 600");}}
        public static string Device(string raw)
        {
            string name = (raw ?? "").Trim();
            if (name.IndexOf("Pico", StringComparison.OrdinalIgnoreCase) >= 0)
                return L10n.DeviceName("HV-DAC Xpert scope");
            if (name.StartsWith("ART", StringComparison.OrdinalIgnoreCase) || name.IndexOf("ART Device", StringComparison.OrdinalIgnoreCase) >= 0)
                return L10n.DeviceName("HV-DAC Xpert art");
            return L10n.T("unknown");
        }
        // Never show raw exception messages: native errors can contain supplier and file names.
        public static string Error(Exception ex)
        {
            if(ex is BindingTimeoutException)return "E302: "+L10n.T("timeout");
            if (ex is ArgumentException) return "请输入正确的汇集器 IPv4 地址，例如 192.168.2.5。";
            if (ex is DllNotFoundException || ex is FileNotFoundException || ex is BadImageFormatException)
                return "E101：通信组件缺失或版本不兼容，请重新解压完整程序包或联系售后。";
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                return "E102：权限不足，请以管理员身份运行程序。";
            if (ex is System.ServiceProcess.TimeoutException)
                return "E103：通信服务启动超时，请稍后重试或联系售后。";
            string reason = ex.Message ?? "";
            if(reason.StartsWith("ART")) return "E301: art Dev1…DevN — "+L10n.T("error")+". "+L10n.T("retry");
            if (reason.Contains("设备列表已变化")) return "E201：设备离线或列表发生变化，请刷新后重新勾选。";
            if (reason.Contains("尚未生成全部设备记录")) return "E202：采集模块初始化未完成，请稍后刷新重试；持续出现请联系售后。";
            if (reason.Contains("绑定格式")) return "E203：通信配置版本不兼容，请联系售后。";
            if (reason.Contains("搜索失败") || reason.Contains("列表读取失败") || reason.Contains("列表不完整"))
                return "E204：设备搜索未完成，请检查网线、IP 地址及通信驱动后刷新。";
            if (reason.Contains("服务") || ex is Win32Exception)
                return "E205：通信服务不可用，请确认已完成驱动安装，或联系售后。";
            return "E299：操作未完成，请刷新重试；若持续出现，请联系售后检查通信驱动与设备配置。";
        }
    }
}
