using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace HVX.OWTS.Task38
{
    public static class PicoRuntimeLocator
    {
        public static string Resolve(string configured)
        { return ResolveFrom(Candidates(configured, AppDomain.CurrentDomain.BaseDirectory)); }

        public static string[] Candidates(string configured, string applicationDirectory)
        {
            var paths = new List<string>();
            string requested = (configured ?? "").Trim();
            // Migrate only the original hard-coded default. An explicit custom selection
            // must fail visibly if missing; never silently substitute a different version.
            bool legacy = String.Equals(requested.TrimEnd('\\', '/'), @"C:\Program Files\Pico Technology\SDK\lib", StringComparison.OrdinalIgnoreCase);
            if (requested.Length > 0 && !legacy && !requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                string absolute = Path.IsPathRooted(requested) ? requested : Path.Combine(applicationDirectory, requested);
                paths.Add(Path.GetExtension(absolute).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? absolute : Path.Combine(absolute, "ps5000a.dll"));
                return paths.ToArray();
            }
            paths.Add(Path.Combine(applicationDirectory, @"runtime\pico\x64\ps5000a.dll"));
            paths.Add(Path.Combine(applicationDirectory, @"runtime\Acquisition\x64\ps5000a.dll"));
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string root in new[] { Path.Combine(pf, "HV DAC Xpert"), Path.Combine(pf, @"XIONGXIN\HV-DAC Xpert") })
                foreach (string suffix in new[] { @"Acquisition\lib", @"Acquisition", @"runtime\pico\x64", @"runtime\Acquisition\x64" })
                    paths.Add(Path.Combine(root, suffix, "ps5000a.dll"));
            paths.Add(Path.Combine(pf, @"Pico Technology\SDK\lib\ps5000a.dll"));
            paths.Add(Path.Combine(pf, @"Pico Technology\PicoScope 7 T&M Stable\ps5000a.dll"));
            return paths.ToArray();
        }
        public static string ResolveFrom(IEnumerable<string> candidates)
        {
            var checkedPaths = new List<string>();
            foreach (string candidate in candidates)
            {
                string path = Path.GetFullPath(candidate); checkedPaths.Add(path);
                if (!File.Exists(path)) continue;
                // An incomplete higher-priority installation is an error, not a reason
                // to mix its main DLL with another installation's dependency.
                VerifyX64(path);
                string dependency = Path.Combine(Path.GetDirectoryName(path), "picoipp.dll");
                if (!File.Exists(dependency)) throw new FileNotFoundException("局放运行库不完整：同目录缺少配套 picoipp.dll。", dependency);
                VerifyX64(dependency);
                return path;
            }
            throw new FileNotFoundException("未找到局放采集运行库 ps5000a.dll。已安装USB驱动或仅有psospa.dll不等于具备本程序A系列接口。请指定完整64位运行库目录。已检查：\n" + String.Join("\n", checkedPaths));
        }
        public static void VerifyX64(string path)
        {
            using (var stream = File.OpenRead(path)) using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new BadImageFormatException("不是有效Windows运行库：" + path);
                stream.Position = 0x3C; int offset = reader.ReadInt32();
                if (offset < 64 || offset > stream.Length - 26) throw new BadImageFormatException("运行库PE头无效：" + path);
                stream.Position = offset;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new BadImageFormatException("局放运行库必须为x64，不能使用32位或其他架构：" + path);
            }
        }
        public static string Describe(string path)
        { return path + "\n版本 " + FileVersionInfo.GetVersionInfo(path).FileVersion + " · x64"; }
    }
}
