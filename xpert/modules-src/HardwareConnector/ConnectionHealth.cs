using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
namespace Xiongxin.HvDacXpert
{
    static class ConnectionHealth
    {
        [DllImport("Sxuptp.dll", CharSet = CharSet.Ansi)] static extern uint SxuptpGetInstanceId(uint ip, string location, out IntPtr ids);
        [DllImport("Sxuptp.dll")] static extern bool SxuptpFreeInstanceId(IntPtr ids);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint dev, string id, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint dev, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Reenumerate_DevNode(uint dev, uint flags);
        public static bool Connected(Usb u)
        {
            return !string.IsNullOrWhiteSpace(u.Port) && Array.TrueForAll(u.Port.Split(','), p => Native.SxuptpGetDeviceStatus(u.Server.RawIp,p) == 2);
        }
        public static string RequestInstallation(Usb u)
        {
            if (!Connected(u)) return "尚未建立通信连接，等待连接后加载驱动";
            IntPtr ids = IntPtr.Zero;
            try
            {
                uint count = SxuptpGetInstanceId(u.Server.RawIp,u.Location,out ids);
                if (count == 0 || count > 128 || ids == IntPtr.Zero) return "等待 Windows 生成当前模块的设备实例";
                var failures = new List<string>();
                for (int i=0;i<count;i++)
                {
                    string id=Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(ids,i*IntPtr.Size)); uint dev;
                    if (!Matches(u,id) || CM_Locate_DevNodeW(out dev,id,0)!=0) { failures.Add("实例尚未枚举"); continue; }
                    uint code=CM_Reenumerate_DevNode(dev,2); // RETRY_INSTALLATION, this selected device only.
                    if(code!=0) failures.Add("系统安装请求状态="+code);
                }
                return failures.Count==0 ? "已请求系统为当前模块匹配/加载驱动，等待状态校验" : string.Join("；",failures);
            }
            finally { if(ids!=IntPtr.Zero) SxuptpFreeInstanceId(ids); }
        }
        static bool Matches(Usb u,string id)
        {
            string prefix="USB\\VID_"+u.Vid.ToString("X4")+"&PID_"+u.Pid.ToString("X4");
            return id!=null && (id.StartsWith(prefix+"\\",StringComparison.OrdinalIgnoreCase) || id.StartsWith(prefix+"&",StringComparison.OrdinalIgnoreCase));
        }
        public static bool Ready(Usb u)
        {
            if (!Connected(u)) return false;
            IntPtr ids = IntPtr.Zero;
            try
            {
                uint count = SxuptpGetInstanceId(u.Server.RawIp, u.Location, out ids);
                if (count == 0 || count > 128 || ids == IntPtr.Zero) return false;
                for (int i = 0; i < count; i++)
                {
                    string id = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(ids, i * IntPtr.Size)); uint dev, status, problem;
                    if (!Matches(u,id) || CM_Locate_DevNodeW(out dev, id, 0) != 0 || CM_Get_DevNode_Status(out status, out problem, dev, 0) != 0 || problem != 0 || (status & 8) == 0) return false;
                }
                return true;
            }
            finally { if (ids != IntPtr.Zero) SxuptpFreeInstanceId(ids); }
        }
    }
}
