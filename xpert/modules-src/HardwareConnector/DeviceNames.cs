using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace Xiongxin.HvDacXpert
{
    static class DeviceNames
    {
        static readonly object Gate = new object();
        static readonly string Backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XIONGXIN", "HvDacXpert", "device-names.xml");
        [StructLayout(LayoutKind.Sequential)] struct Info { public uint Size; public Guid Class; public uint DevInst; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(IntPtr g, string e, IntPtr w, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr list, uint index, ref Info info);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr list);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr list, ref Info info, uint p, out uint type, byte[] data, uint length, out uint needed);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr list, ref Info info, uint p, byte[] data, uint length);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr list, ref Info info, StringBuilder id, int length, out uint needed);
        static string Read(IntPtr list, ref Info info, uint property)
        {
            uint type, needed; var b = new byte[16384];
            if (!SetupDiGetDeviceRegistryPropertyW(list, ref info, property, out type, b, (uint)b.Length, out needed)) return null;
            return Encoding.Unicode.GetString(b, 0, (int)Math.Min(needed, b.Length)).TrimEnd('\0');
        }
        static string Match(string ids)
        {
            foreach (string id in (ids ?? "").Split('\0'))
            {
                if (id.Equals(@"ROOT\SXUPTP", StringComparison.OrdinalIgnoreCase)) return "HV-DAC Xpert 600";
                if (IdMatches(id, @"USB\VID_3063&PID_3135")) return "HV-DAC Xpert art";
                if (IdMatches(id, @"USB\VID_0CE9&PID_1218")) return "HV-DAC Xpert scope";
            }
            return null;
        }
        static bool IdMatches(string id, string prefix) { return id.Equals(prefix, StringComparison.OrdinalIgnoreCase) || id.StartsWith(prefix + "&", StringComparison.OrdinalIgnoreCase); }
        [DllImport("cfgmgr32.dll")] static extern uint CM_Reenumerate_DevNode(uint dev, uint flags);
        public static void ScanCommunicationBus()
        {
            IntPtr list = SetupDiGetClassDevsW(IntPtr.Zero,null,IntPtr.Zero,6);
            if(list==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for(uint i=0;;i++)
                {
                    var info=new Info{Size=(uint)Marshal.SizeOf(typeof(Info))};
                    if(!SetupDiEnumDeviceInfo(list,i,ref info)) break;
                    if(Match(Read(list,ref info,1))!="HV-DAC Xpert 600") continue;
                    uint code=CM_Reenumerate_DevNode(info.DevInst,0);
                    if(code!=0) throw new InvalidOperationException("通信总线枚举请求失败，系统状态="+code);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(list); }
        }
        public static HashSet<string> PresentArtInstances()
        {
            var result=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr list=SetupDiGetClassDevsW(IntPtr.Zero,"USB",IntPtr.Zero,6);
            if(list==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for(uint i=0;;i++)
                {
                    var info=new Info{Size=(uint)Marshal.SizeOf(typeof(Info))};
                    if(!SetupDiEnumDeviceInfo(list,i,ref info)){if(Marshal.GetLastWin32Error()!=259)throw new Win32Exception(Marshal.GetLastWin32Error());break;}
                    var id=new StringBuilder(1024);uint needed;
                    if(!SetupDiGetDeviceInstanceIdW(list,ref info,id,id.Capacity,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());
                    if(id.ToString().StartsWith(@"USB\VID_3063&PID_3135\",StringComparison.OrdinalIgnoreCase))result.Add(id.ToString());
                }
            }
            finally{SetupDiDestroyDeviceInfoList(list);}
            return result;
        }
        public static string Apply(bool preview, bool restore = false)
        {
            lock (Gate)
            {
                int matched = 0, changed = 0; var errors = new List<string>();
                XDocument backup = File.Exists(Backup) ? XDocument.Load(Backup) : new XDocument(new XElement("DeviceNames"));
                IntPtr list = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, restore ? 4u : 6u);
                if (list == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    for (uint index = 0; ; index++)
                    {
                        var info = new Info { Size = (uint)Marshal.SizeOf(typeof(Info)) };
                        if (!SetupDiEnumDeviceInfo(list, index, ref info))
                        { if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error()); break; }
                        string name = Match(Read(list, ref info, 1)); if (name == null) continue;
                        var id = new StringBuilder(1024); uint needed;
                        if (!SetupDiGetDeviceInstanceIdW(list, ref info, id, id.Capacity, out needed)) continue;
                        var saved = backup.Root.Elements("Device").FirstOrDefault(x => (string)x.Attribute("id") == id.ToString());
                        if (restore && saved == null) continue;
                        matched++;
                        string current = Read(list, ref info, 12);
                        string target = restore ? ((string)saved.Attribute("hadName") == "true" ? (string)saved.Element("Original") : null) : L10n.DeviceName(name);
                        if (current == target || preview) continue;
                        if (!restore && saved == null)
                        {
                            backup.Root.Add(new XElement("Device", new XAttribute("id", id.ToString()), new XAttribute("hadName", current != null ? "true" : "false"), new XElement("Original", current ?? "")));
                            Directory.CreateDirectory(Path.GetDirectoryName(Backup)); backup.Save(Backup);
                        }
                        byte[] value = target == null ? null : Encoding.Unicode.GetBytes(target + "\0");
                        if (SetupDiSetDeviceRegistryPropertyW(list, ref info, 12, value, value == null ? 0u : (uint)value.Length)) changed++;
                        else errors.Add("Win32=" + Marshal.GetLastWin32Error());
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(list); }
                if (errors.Count > 0) throw new InvalidOperationException("设备显示名称设置失败：" + string.Join(",", errors));
                return (preview ? "名称检查" : restore ? "名称恢复" : "名称同步") + "：匹配 " + matched + " 个已枚举设备，更新 " + changed + " 个。";
            }
        }
    }
}
