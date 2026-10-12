using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

// Runs against the delivered assemblies. No SDK discovery, connection, sampling,
// output or user-settings save is called. Gate fixtures contain inert bytes only.
public sealed class OfflineDiscoveryGateProbe : MarshalByRefObject
{
    public string Read(string assemblyPath, string method)
    {
        var type = Assembly.LoadFrom(assemblyPath).GetType("Xiongxin.OWTS.Desktop.FirstHardwareProvisioning", true);
        return (string)type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }
}

static class DeviceConnectionPasswordFreeSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static Assembly host;
    static Type T(string name) { return host.GetType("Xiongxin.OWTS.Desktop." + name, true); }
    static object Make(string name, params object[] args) { return Activator.CreateInstance(T(name), All, null, args, null); }
    static object Get(object obj, string name) { return obj.GetType().GetProperty(name, All).GetValue(obj, null); }
    static void Set(object obj, string name, object value) { obj.GetType().GetProperty(name, All).SetValue(obj, value, null); }
    static object Field(object obj, string name) { return obj.GetType().GetField(name, All).GetValue(obj); }
    static object Call(object obj, string name, params object[] args)
    {
        Type type = obj as Type ?? obj.GetType();
        return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(obj is Type ? null : obj, args);
    }
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
    static bool HasPassword(Control control)
    {
        var input = control as TextBox;
        return (input != null && input.UseSystemPasswordChar) || control.Controls.Cast<Control>().Any(HasPassword);
    }
    static void Ui(string build, string output)
    {
        Check(host.GetType("Xiongxin.OWTS.Desktop.SettingsAccessControl") == null, "Removed embedded host password verifier");
        foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
        {
            object settings = Make("AppSettings"); Set(settings, "LanguageCode", language); Set(settings, "DividerRatio", 12500d);
            string before = (string)Call(T("DeviceSetupStatus"), "ParameterHash", settings);
            using (var form = (Form)Make("SettingsForm", settings))
            {
                Set(form, "AuditEnabled", false);
                Check(Field(form, "dividerRatio") == null, "General settings does not implicitly confirm device parameters");
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
                form.Show(); Application.DoEvents();
                foreach (string section in new[] { "Device", "Expert", "Oem", "SystemAdmin", "PlusData" })
                {
                    Call(form, "SelectSection", Enum.Parse(T("SettingsSection"), section)); Application.DoEvents();
                    string field = section == "Device" ? "dividerRatio" : section == "Expert" ? "minus3Attenuation" : section == "Oem" ? "softwareModel" : section == "SystemAdmin" ? "hardwareEnabled" : "minus3AlgorithmIndex";
                    Check(Field(form, field) is Control, "Editor opens directly: " + language + " " + section);
                    Check(!HasPassword(form), "No password UI: " + language + " " + section);
                    Check(form.DialogResult == DialogResult.None, "Navigation never saves settings");
                }
                Check(((NumericUpDown)Field(form, "dividerRatio")).Value == 12500m, "Existing device value preserved");
                Check(!(bool)Call(T("DeviceSetupStatus"), "ParametersSaved", settings), "Opening device settings does not review parameters");
                Check(before == (string)Call(T("DeviceSetupStatus"), "ParameterHash", settings), "Draft controls never mutate source settings");
                if (language == "en")
                {
                    Call(form, "SelectSection", Enum.Parse(T("SettingsSection"), "Device"));
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, "device-settings-no-password.png")); }
                }
                Call(form, "SelectSection", Enum.Parse(T("SettingsSection"), "SystemAdmin")); Application.DoEvents();
                var search = form.Controls.Find("SearchAndConnectAvailableDevice", true).Single() as Button;
                Check(search != null && search.Enabled && search.Visible, "Delivered search button is accessible: " + language);
                Check(search.Parent.ClientRectangle.Contains(search.Bounds), "Delivered search button fits its container");
                Check(language != "en" || search.Text == "Search and connect", "Delivered search button language");
                if (language == "zh-CN")
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, "settings-auto-connect.png")); }
                ((TextBox)Field(form, "reportOutputPath")).Text = "unsaved-delivery-test";
                ((CheckBox)Field(form, "hardwareEnabled")).Checked = true;
                search.PerformClick();
                Check(form.DialogResult == DialogResult.Retry && Get(form, "DeviceSearchDraft") != null, "Delivered editor requests discovery with empty identity fields");
                Check((string)Get(Get(form, "DeviceSearchDraft"), "ReportOutputPath") == "unsaved-delivery-test", "Delivered editor retains draft");
                Check(before == (string)Call(T("DeviceSetupStatus"), "ParameterHash", settings), "Delivered search request does not mutate original settings");
                form.Close();
            }
        }
        var reviewAssembly = Assembly.LoadFrom(Path.Combine(build, "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
        Check(reviewAssembly.GetType("HVX.PdReview.ReviewAdvancedSettingsSecurity") == null, "Removed analysis password verifier");
        Check(reviewAssembly.GetType("HVX.PdReview.ReviewDiagnosticAccess") == null, "Removed diagnostic password verifier");
        reviewAssembly.GetType("HVX.PdReview.ReviewAnalysisPreferences", true).GetField("PathProvider", All).SetValue(null, (Func<string>)(() => Path.Combine(output, "preferences.json")));
        using (var review = (Control)Activator.CreateInstance(reviewAssembly.GetType("HVX.PdReview.PdReviewControl", true)))
        {
            Set(review, "EnableAiFeatures", false);
            Check((bool)Call(review, "AuthorizeDiagnostics"), "Diagnostics open without a prompt");
            foreach (bool managed in new[] { false, true })
            {
                Set(review, "ProtectedAnalysisSettingsManaged", managed);
                using (var dialog = (Form)Activator.CreateInstance(reviewAssembly.GetType("HVX.PdReview.ReviewAnalysisSettingsDialog", true), All, null, new object[] { review }, null))
                {
                    Check(!HasPassword(dialog), "No analysis password input");
                    Check((bool)Get(dialog, "AdvancedUnlocked") == !managed, "Standalone editable / host single-owner settings preserved");
                    Check(((Control)Field(dialog, "algorithm")).Enabled == !managed, "Algorithm setting follows ownership, not a password");
                }
            }
        }
    }
    static void ConnectionState(string build, string output)
    {
        object settings = Make("AppSettings"); Set(settings, "DataRoot", output);
        object hardware = Get(settings, "Hardware");
        var state = Make("ApplicationState");
        using (var form = (Form)Make("MainForm", settings, state, true))
        {
            object catalog = Call(T("DeviceAssetCatalog"), "Load", Path.Combine(build, "assets", "device-asset-catalog.v1.json"));
            form.GetType().GetField("deviceAssetCatalog", All).SetValue(form, catalog);
            Call(form, "UpdateDeviceStatusActions");
            Check(((Button)Field(form, "disconnectDeviceButton")).Enabled, "Offline connect button can open discovery without editing configuration");
            Set(settings, "SessionHardwareForcedOffline", true); Call(form, "UpdateDeviceStatusActions");
            Check(!((Button)Field(form, "disconnectDeviceButton")).Enabled, "Incomplete installation cannot enable connection");
            Set(settings, "SessionHardwareForcedOffline", false); Set(hardware, "Enabled", true); Set(hardware, "SelectedHubMac", "001122334455");
            object snapshot = Make("HardwareStatusSnapshot");
            foreach (string name in new[] { "HardwareEnabled", "DriverInstalled", "Connected", "IdentityValid" }) Set(snapshot, name, true);
            Set(snapshot, "StatusCode", "Ready"); Set(snapshot, "HubMac", "001122334455");
            Set(snapshot, "ControllerSerial", "9999999999"); Set(snapshot, "AcquisitionSerial", "99999/9999"); Set(snapshot, "CheckedUtc", DateTime.UtcNow);
            string hash = (string)Call(T("DeviceSetupStatus"), "ParameterHash", settings);
            Call(form, "MatchDeviceAssetAndApply", snapshot);
            Check((string)Get(snapshot, "AssetMatchStatus") == "Unknown", "Unknown serial not mapped to another factory record");
            Check((bool)Get(snapshot, "Ready"), "Missing CSV record never changes verified connection readiness");
            Check(hash == (string)Call(T("DeviceSetupStatus"), "ParameterHash", settings), "Unknown record does not apply another device's parameters");
            Check(!(bool)Call(T("DeviceSetupStatus"), "IdentityMatches", settings, snapshot), "Connection does not confirm unreviewed parameters");
            string status = (string)Call(form, "DeviceAssetStatusText", snapshot);
            Check(status.Contains("振荡波设备") && status.Contains("待确认"), "Unknown connected architecture displayed honestly");
            Call(state, "SetHardware", snapshot); Call(form, "UpdateDeviceStatusActions");
            Check(((Button)Field(form, "disconnectDeviceButton")).Text.Contains("断开"), "Unknown but connected device offers disconnect");
            Check(catalog != null, "Shipped factory catalog remains available");
            Set(snapshot, "ControllerSerial", "1023417009"); Set(snapshot, "AcquisitionSerial", "13000/0059");
            object match = Call(catalog, "Match", snapshot);
            Check((bool)Get(match, "IsMatch"), "Known serial still follows original unique catalog match");
        }
    }
    static string Gate(string assembly, string folder, string method)
    {
        var domain = AppDomain.CreateDomain("OfflineGate" + Guid.NewGuid().ToString("N"), null, new AppDomainSetup { ApplicationBase = folder });
        try
        {
            var worker = (OfflineDiscoveryGateProbe)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OfflineDiscoveryGateProbe).FullName);
            return worker.Read(assembly, method);
        }
        finally { AppDomain.Unload(domain); }
    }
    static void Gates(string build, string output)
    {
        string assembly = Path.Combine(build, "Xiongxin.OWTS.Desktop.exe"), folder = Path.Combine(output, "inert-gate-fixture");
        Check(Gate(assembly, build, "DiscoveryGate") == "", "Delivered standalone payload passes discovery integrity gate without executing connector");
        Directory.CreateDirectory(Path.Combine(folder, "modules", "HardwareIntegration"));
        string connector = Path.Combine(folder, "modules", "HardwareIntegration", "HardwareIntegration.exe"), manifest = Path.Combine(folder, "module-manifest.json");
        File.WriteAllBytes(connector, new byte[] { 1, 2, 3, 4 });
        string digest; using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(connector))).Replace("-", "");
        string entry = "{\"Path\":\"modules/HardwareIntegration/HardwareIntegration.exe\",\"Bytes\":4,\"Sha256\":\"" + digest + "\"}";
        string valid = "{\"Schema\":\"XIONGXIN.UnifiedDesktop.Modules.v2\",\"Modules\":[" + entry + "]}";
        File.WriteAllText(manifest, valid);
        Check(Gate(assembly, folder, "DiscoveryGate") == "", "Standalone verified connector does not require parent manifest for discovery");
        Check(Gate(assembly, folder, "InstallationGate").Length > 0, "First-time elevated binding retains installer prerequisite");
        File.WriteAllBytes(connector, new byte[] { 1, 2, 3, 5 });
        Check(Gate(assembly, folder, "DiscoveryGate").Length > 0, "Same-length altered connector rejected");
        File.WriteAllBytes(connector, new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(manifest, valid.Replace("Modules.v2", "Modules.v0"));
        Check(Gate(assembly, folder, "DiscoveryGate").Length > 0, "Wrong module manifest schema rejected");
        File.WriteAllText(manifest, valid.Replace(entry, entry + "," + entry));
        Check(Gate(assembly, folder, "DiscoveryGate").Length > 0, "Duplicate connector entry rejected");
        File.WriteAllText(manifest, valid); Directory.CreateDirectory(Path.Combine(folder, "private"));
        File.WriteAllBytes(Path.Combine(folder, "private", "ParentHost.exe"), new byte[] { 0 });
        Check(Gate(assembly, folder, "DiscoveryGate").Length > 0, "Partial parent installation never falls back to standalone access");
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            string build = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            host = Assembly.LoadFrom(Path.Combine(build, "Xiongxin.OWTS.Desktop.exe"));
            Ui(build, output); ConnectionState(build, output); Gates(build, output);
            Console.WriteLine("DEVICE_CONNECTION_PASSWORD_FREE_PASS=" + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
