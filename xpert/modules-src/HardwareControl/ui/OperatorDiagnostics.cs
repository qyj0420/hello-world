using System;
using System.Collections.Generic;
using System.IO;
using HVX.OWTS.Task38;

namespace HVX.OWTS.Task38.Desktop
{
    internal static class OperatorDiagnostics
    {
        private static readonly HashSet<string> recorded = new HashSet<string>();
        internal static string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OHV", "Task38", "diagnostics");
        internal static string LastWriteFailure = String.Empty;
        internal static string Report(Exception error) { Record(error.ToString()); return DeviceMessages.Describe(error.Message); }
        internal static string Report(string raw) { Record(raw); return DeviceMessages.Describe(raw); }
        internal static void Record(string raw)
        {
            if (String.IsNullOrWhiteSpace(raw)) return;
            lock (recorded)
            {
                if (recorded.Contains(raw)) return;
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    string path = Path.Combine(DirectoryPath, "devices-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(path, DateTime.Now.ToString("O") + "\r\n" + raw + "\r\n\r\n");
                    if (recorded.Count > 512) recorded.Clear();
                    recorded.Add(raw); LastWriteFailure = String.Empty;
                }
                catch (Exception ex) { LastWriteFailure = "诊断日志未能保存：" + ex.Message; }
            }
        }
    }
}
