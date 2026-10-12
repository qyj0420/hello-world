using System;
using System.IO;
using System.Linq;
using Xiongxin.OWTS.Desktop;

internal static class PreviewProfileIsolationSmoke
{
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }

    static int Main(string[] args)
    {
        try
        {
            // No default Load/Save, diagnostic write or audit write is invoked.
            // All fixture I/O stays inside a new task-owned QA directory.
            string output = Path.GetFullPath(args[0]);
            if (output.IndexOf("\\build\\", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("QA output must remain under build.");
            string localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldRoot = Path.Combine(localRoot, "XIONGXIN", "HVXUnifiedDesktop");
            string expected = Path.Combine(oldRoot, "Preview-1.1.3");
            Check(AppPaths.ProfileDirectoryName == "Preview-1.1.3", "Explicit versioned preview profile");
            Check(AppPaths.LocalDataDirectory == expected, "Preview has its own LocalAppData subdirectory");
            Check(AppPaths.SettingsFile == Path.Combine(expected, "settings.xml"), "Settings path is preview-only");
            Check(AppPaths.SettingsFile != Path.Combine(oldRoot, "settings.xml"), "No shared release settings file");
            Check(AppPaths.DiagnosticsDirectory == Path.Combine(expected, "diagnostics"), "Diagnostics follow the profile");
            Check(new Diagnostics().CurrentLogPath.StartsWith(AppPaths.DiagnosticsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Default diagnostics never use the legacy root");
            Check(OperationAuditLog.Default.DirectoryPath == Path.Combine(expected, "Logs"), "Default audit path follows the profile");
            Check(OperationAuditLog.Default.PendingEntries == 0, "Reading audit path does not enqueue writes");
            var defaults = new AppSettings();
            Check(!defaults.Hardware.Enabled, "A new preview profile defaults to offline");
            Check(defaults.LanguageCode == "system", "New preview language follows the system");
            Check(defaults.DataRoot == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XIONGXIN", "HVX Data"), "Project storage default is unchanged");

            string fixture = Path.Combine(output, "profile-" + Guid.NewGuid().ToString("N"));
            string legacy = Path.Combine(fixture, "settings.xml");
            string preview = Path.Combine(fixture, AppPaths.ProfileDirectoryName, "settings.xml");
            string project = Path.Combine(fixture, "existing-project");
            Directory.CreateDirectory(project);
            string projectFile = Path.Combine(project, "preserve.bin");
            byte[] projectBytes = new byte[] { 0, 15, 32, 128, 255 };
            File.WriteAllBytes(projectFile, projectBytes);
            var oldSettings = new AppSettings { LanguageCode = "en", DataRoot = project };
            oldSettings.Hardware.Enabled = true;
            oldSettings.Hardware.SelectedHubMac = "AABBCCDDEEFF";
            oldSettings.Hardware.ControllerLocation = "H01";
            oldSettings.Hardware.AcquisitionLocation = "S01";
            oldSettings.Save(legacy);
            byte[] oldBytes = File.ReadAllBytes(legacy);

            var firstRun = AppSettings.Load(preview);
            Check(!firstRun.Hardware.Enabled, "Absent preview settings never inherit parent's enabled hardware mode");
            Check(firstRun.LanguageCode == "system", "Absent preview settings do not inherit parent's preference");
            Check(firstRun.DataRoot == defaults.DataRoot, "No parent project path is silently imported");
            Check(!Directory.Exists(Path.GetDirectoryName(preview)), "Loading missing preview settings does not create or migrate files");
            Check(File.ReadAllBytes(legacy).SequenceEqual(oldBytes), "First read leaves old settings unchanged");
            firstRun.DataRoot = project;
            firstRun.LanguageCode = "zh-TW";
            firstRun.Save(preview);
            var reopened = AppSettings.Load(preview);
            Check(!reopened.Hardware.Enabled && reopened.LanguageCode == "zh-TW", "Preview preferences round-trip independently");
            Check(reopened.DataRoot == project, "An explicitly selected project storage path is preserved");
            Check(File.ReadAllBytes(legacy).SequenceEqual(oldBytes), "Preview save does not overwrite old settings");
            Check(File.ReadAllBytes(projectFile).SequenceEqual(projectBytes), "Project data remain untouched");
            Check(!File.Exists(legacy + ".bak"), "No old-profile backup or replacement side effect");
            Console.WriteLine("PREVIEW_PROFILE_ISOLATION_PASS=" + checks + "; USER_PROFILE_IO=0; HARDWARE_COMMANDS=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
