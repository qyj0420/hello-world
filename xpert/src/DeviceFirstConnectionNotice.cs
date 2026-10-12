using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    // Acknowledgment is local to this Windows profile and this physical identity.
    // It never changes a device's connection state or grants permission to energize HV.
    internal static class DeviceFirstConnectionNotice
    {
        private static readonly object Gate = new object();
        private static string FilePath { get { return Path.Combine(AppPaths.LocalDataDirectory, "device-first-connection-ack.v1.txt"); } }

        internal static string IdentityKey(HardwareStatusSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Ready) return String.Empty;
            string identity = DeviceSetupStatus.ActualIdentity(snapshot);
            if (identity.Length == 0) return String.Empty; // Do not suppress a different, unidentified unit.
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes("XIONGXIN.FirstConnection.v1|" + identity));
                return BitConverter.ToString(digest).Replace("-", String.Empty);
            }
        }

        internal static bool WasAcknowledged(string key)
        {
            if (String.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return false;
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                        if (String.Equals(line.Trim(), key, StringComparison.Ordinal)) return true;
                }
                catch { /* A missing or damaged acknowledgment is not connection failure. */ }
                return false;
            }
        }

        internal static bool Acknowledge(string key)
        {
            if (String.IsNullOrEmpty(key)) return false;
            lock (Gate)
            {
                try
                {
                    if (WasAcknowledged(key)) return true;
                    Directory.CreateDirectory(AppPaths.LocalDataDirectory);
                    // Append is sufficient: duplicate lines are harmless and no device settings are overwritten.
                    File.AppendAllText(FilePath, key + Environment.NewLine, new UTF8Encoding(false));
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
