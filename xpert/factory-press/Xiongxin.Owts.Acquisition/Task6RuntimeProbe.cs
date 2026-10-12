using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Xiongxin.Owts.Acquisition
{
    /// <summary>
    /// Locates Task6 WavePreprocess DLL trio (dac-source dependencies/Task6).
    /// Env: <see cref="EnvRuntimeDir"/> → directory or …/Task6 subfolder.
    /// </summary>
    public static class Task6RuntimeProbe
    {
        public const string EnvRuntimeDir = "OWTS_TASK6_RUNTIME_DIR";

        /// <summary>Managed engine hosting OWTS.WavePreprocess.WavePreprocessEngine.</summary>
        public const string EngineDllFileName = "05f21d64e56010f23ed61cc9.V1.0.6.dll";
        /// <summary>Managed dependency loaded before engine.</summary>
        public const string DependencyDllFileName = "7e4a9c2d6f1b8053a7c4e912.V1.0.6.dll";
        /// <summary>Native/compute dependency; must exist beside engine.</summary>
        public const string NativeDllFileName = "2c8a82000748444a44aa0ad8.V1.0.6.dll";

        public const string EngineDllSha256 = "006F73F20EE9353CB64B14FBFC2A9C74558C0D4414E3BEF7DF00D9FBC2C6380E";
        public const string DependencyDllSha256 = "1F4A6E59EDA06AEB1583F33AB1B58983F284B052983277FD522A851DA3EACC4E";
        public const string NativeDllSha256 = "1F3F18F640A94051E4A6C94E9A08347BE6BCEEB6739B11748330F120A2CF0A37";

        public const string EngineTypeName = "OWTS.WavePreprocess.WavePreprocessEngine";
        public const string AnalyzeMethodName = "AnalyzeWithLinearity";
        public const int AnalyzeParameterCount = 29;

        public sealed class ProbeResult
        {
            public bool TrioPresent;
            public string Directory;
            public string EngineDllPath;
            public string DependencyDllPath;
            public string NativeDllPath;
            public string Detail;
            public bool EngineSha256Matched;
            public bool EngineSha256Checked;
        }

        public static ProbeResult Probe(string explicitDirectory = null)
        {
            var result = new ProbeResult();
            string configured = FirstNonEmpty(explicitDirectory, Environment.GetEnvironmentVariable(EnvRuntimeDir));
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string root = configured.Trim().Trim('"');
                TryDirectory(root, "configured", result);
                if (!result.TrioPresent)
                    TryDirectory(Path.Combine(root, "Task6"), "configured/Task6", result);
                if (!result.TrioPresent)
                {
                    result.Detail = "Task6 trio missing under " + EnvRuntimeDir + "/config=" + root
                        + " (expected " + EngineDllFileName + " + " + DependencyDllFileName + " + " + NativeDllFileName + ")";
                }
                return result;
            }

            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                TryDirectory(baseDir, "AppContext.BaseDirectory", result);
                if (result.TrioPresent) return result;
                TryDirectory(Path.Combine(baseDir, "Task6"), "AppContext/Task6", result);
                if (result.TrioPresent) return result;
            }

            try
            {
                string asmDir = Path.GetDirectoryName(typeof(Task6RuntimeProbe).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    TryDirectory(asmDir, "Acquisition assembly dir", result);
                    if (result.TrioPresent) return result;
                    TryDirectory(Path.Combine(asmDir, "Task6"), "Acquisition/Task6", result);
                    if (result.TrioPresent) return result;
                }
            }
            catch (Exception)
            {
                // ignore
            }

            result.Detail = "Task6 trio not found (set " + EnvRuntimeDir + " to install Task6 folder).";
            return result;
        }

        static void TryDirectory(string directory, string source, ProbeResult result)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            string eng = Path.Combine(directory, EngineDllFileName);
            string dep = Path.Combine(directory, DependencyDllFileName);
            string nat = Path.Combine(directory, NativeDllFileName);
            if (!File.Exists(eng) || !File.Exists(dep) || !File.Exists(nat)) return;

            result.TrioPresent = true;
            result.Directory = Path.GetFullPath(directory);
            result.EngineDllPath = Path.GetFullPath(eng);
            result.DependencyDllPath = Path.GetFullPath(dep);
            result.NativeDllPath = Path.GetFullPath(nat);
            result.Detail = "Trio present (" + source + "): " + result.Directory;
            try
            {
                string sha = Sha256Hex(result.EngineDllPath);
                result.EngineSha256Checked = true;
                result.EngineSha256Matched = string.Equals(sha, EngineDllSha256, StringComparison.OrdinalIgnoreCase);
                if (!result.EngineSha256Matched)
                    result.Detail += "; sha256=warn_mismatch_try_anyway";
            }
            catch (Exception ex)
            {
                result.Detail += "; sha256_check_failed=" + OneLine(ex);
            }
        }

        static string Sha256Hex(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("X2"));
                return sb.ToString();
            }
        }

        static string FirstNonEmpty(string a, string b)
        {
            if (!string.IsNullOrWhiteSpace(a)) return a;
            if (!string.IsNullOrWhiteSpace(b)) return b;
            return null;
        }

        static string OneLine(Exception ex)
        {
            if (ex == null) return string.Empty;
            string m = ex.GetType().Name + ": " + (ex.Message ?? string.Empty);
            return m.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
