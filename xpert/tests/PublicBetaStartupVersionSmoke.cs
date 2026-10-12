using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Loads the final host only for metadata and its pure XML predicate. Does not
// invoke Main, read installed state, extract payloads or access hardware.
static class PublicBetaStartupVersionSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static int checks;
    static void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
    static string Gate(MethodInfo method, string manifest, string state)
    { return (string)method.Invoke(null, new object[] { manifest, state, "", false }); }
    static int Main(string[] args)
    {
        try {
            string program = Path.Combine(Path.GetFullPath(args[0]), "Xiongxin.OWTS.Desktop.exe");
            Check(FileVersionInfo.GetVersionInfo(program).FileVersion == "1.1.2.0", "actual file version is 1.1.2.0");
            Assembly host = Assembly.LoadFrom(program);
            Check(host.GetName().Version.ToString() == "1.1.2.0", "actual assembly version is 1.1.2.0");
            Check(host.EntryPoint != null, "host metadata includes its entry point without executing it");
            var title = (AssemblyTitleAttribute)Attribute.GetCustomAttribute(host, typeof(AssemblyTitleAttribute));
            var product = (AssemblyProductAttribute)Attribute.GetCustomAttribute(host, typeof(AssemblyProductAttribute));
            Check(title != null && title.Title.Contains("V1.1.2"), "actual assembly title uses V1.1.2");
            Check(product != null && product.Product.Contains("V1.1.2"), "actual product name uses V1.1.2");
            Type paths = host.GetType("Xiongxin.OWTS.Desktop.AppPaths", true);
            Check((string)paths.GetField("ProfileDirectoryName", All).GetRawConstantValue() == "PublicBeta-NoDriver-1.1.2-20261011", "isolated V1.1.2 configuration profile");
            string settings = (string)paths.GetProperty("SettingsFile", All).GetValue(null, null);
            Check(settings.EndsWith(@"\PublicBeta-NoDriver-1.1.2-20261011\settings.xml", StringComparison.OrdinalIgnoreCase), "settings path is not the previous release profile");
            Type provisioning = host.GetType("Xiongxin.OWTS.Desktop.FirstHardwareProvisioning", true);
            string statePath = (string)provisioning.GetField("ParentState", All).GetValue(null);
            Check(statePath.EndsWith(@"\XIONGXIN\HVXPublicBetaNoDriver11220261011\install-state.xml", StringComparison.OrdinalIgnoreCase), "installation state is isolated without reading it");
            MethodInfo validate = provisioning.GetMethod("ValidateParentStateDocuments", All);
            Check(validate != null, "actual pure installation predicate remains available");
            string row = "fixture|" + new string('A', 64) + "|core", package;
            using (var sha = SHA256.Create()) package = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(row))).Replace("-", "");
            string manifest = "<ParentManifest Schema=\"1\" PackageId=\"" + package + "\"><File Path=\"fixture\" Sha256=\"" + new string('A', 64) + "\" Scope=\"core\" /></ParentManifest>";
            string state = "<ParentInstallState Schema=\"1\" PackageId=\"" + package + "\" Stage=\"Complete\" Hardware=\"True\" />";
            Check(Gate(validate, manifest, state).Length == 0, "matching synthetic completed documents pass the pure predicate only");
            foreach (string invalid in new[] { state.Replace(package, new string('B', 64)), state.Replace("Complete", "Pico"), state.Replace("True", "False"), state.Replace("True", "invalid"), "<invalid/>", "<!DOCTYPE x [<!ENTITY z SYSTEM 'file:///x'>]><ParentInstallState>&z;</ParentInstallState>" })
                Check(Gate(validate, manifest, invalid).Length > 0, "mismatched/incomplete/malformed/DTD documents remain rejected");
            Check(Gate(validate, manifest.Replace(package, new string('B', 64)), state).Length > 0, "manifest identity remains validated");
            Console.WriteLine("PUBLIC_BETA_STARTUP_VERSION_PASS=" + checks + "; PRODUCT_ENTRY=False; INSTALLED_STATE_ACCESSED=False; HARDWARE=False");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
