using System;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.OWTS.Desktop
{
    internal static class SettingsAccessControl
    {
#if MAINTENANCE_PASSWORD_GUARD
        internal const bool Enabled = true;
#else
        internal const bool Enabled = false;
#endif
        internal static bool RequiresDeviceAuthorization(bool firstConfiguration) { return Enabled && !firstConfiguration; }

        // Only digests are embedded. Access is session-scoped and never written to settings.xml.
        private const string OemDigest = "+3Kn02h0RcMPNSU7S3EfT7TA7KmD/Pw7ykPO3nrNMFo=";
        private const string ExpertDigest = "lngsff2Dyz5aMxVmCr5kmLJVwmdiae5kR+b6ESTKW/I=";
        private const string DeviceDigest = "/W/gIdwkDXUsydz8UGjwHdiuOQTiUi4JxvK6dIByaeQ=";

        internal static bool Verify(SettingsSection section, string password)
        {
            if (String.IsNullOrEmpty(password)) return false;
            string expected;
            switch (section)
            {
                case SettingsSection.SystemAdmin: expected = "zkLDiRXtx4zV+s9sTqh3fHt7vMNdSWIwSv4AE6ZpTq0="; break;
                case SettingsSection.Oem: expected = OemDigest; break;
                case SettingsSection.Expert: expected = ExpertDigest; break;
                case SettingsSection.Device: expected = DeviceDigest; break;
                case SettingsSection.PlusData: expected = "7V7uP91W7/U9Wbo2FnIJQyY9slSZx7dwLCFIrWUfLS4="; break;
                default: return false;
            }

            byte[] provided;
            using (SHA256 sha = SHA256.Create())
                provided = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            byte[] reference = Convert.FromBase64String(expected);
            int difference = provided.Length ^ reference.Length;
            for (int index = 0; index < provided.Length && index < reference.Length; index++)
                difference |= provided[index] ^ reference[index];
            return difference == 0;
        }
    }
}
