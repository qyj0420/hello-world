using System;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace Xiongxin.Owts.HvPressTest
{
    /// <summary>启动日志用构建时间戳（编译期写入；DLL mtime 作回退）。</summary>
    internal static class FactoryPressBuildInfo
    {
        /// <summary>编译写入的权威戳；发布脚本可改此常量。</summary>
        public const string CompileStamp = "2026-10-12T19:11:51+08:00";

        public static string Stamp
        {
            get
            {
                try
                {
                    string asmPath = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(asmPath) && File.Exists(asmPath))
                    {
                        DateTime m = File.GetLastWriteTime(asmPath);
                        return CompileStamp + " | DLL mtime="
                            + m.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                            + " CST | " + Path.GetFileName(asmPath);
                    }
                }
                catch { }
                return CompileStamp;
            }
        }
    }
}
