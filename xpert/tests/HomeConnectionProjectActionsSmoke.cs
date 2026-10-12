using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class HomeConnectionProjectActionsSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Hidden).GetValue(owner); }
    static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Hidden).SetValue(owner, value); }
    static void Call(object owner, string name) { owner.GetType().GetMethod(name, Hidden).Invoke(owner, null); }
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Pump() { for (int i = 0; i < 5; i++) Application.DoEvents(); }
    static HardwareStatusSnapshot Snapshot(bool connected, bool ready = false, string code = null)
    {
        return new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true,
            Connected = connected, IdentityValid = ready, StatusCode = code ?? (ready ? "Ready" : connected ? "NeedsConfiguration" : "Disconnected"), CheckedUtc = DateTime.UtcNow };
    }
    sealed class BusyPage : UserControl { public bool IsBusy { get; set; } }
    static string Caption(string language, string chinese, string english)
    { return language == "en" ? english : AppLanguages.Display(language, chinese); }
    static void Action(MainForm form, string expected, bool enabled)
    {
        var button = Field<Button>(form, "disconnectDeviceButton");
        Check(button.Visible && button.Text == expected, "actual connection selects expected action: " + expected);
        Check(button.Enabled == enabled, "action availability: " + expected + " / " + enabled);
        Check(!String.IsNullOrWhiteSpace(button.AccessibleDescription), "action has explanatory accessible text");
        Panel card = Field<Panel>(form, "deviceStatusCard");
        Check(card.ClientRectangle.Contains(button.Bounds), "connection action remains inside card");
        Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + 20, "connection action caption is not clipped");
        Panel lamp=Field<Panel>(form,"deviceDot");
        using(var bitmap=new Bitmap(lamp.Width,lamp.Height))
        {
            lamp.DrawToBitmap(bitmap,new Rectangle(Point.Empty,lamp.Size));
            Check(bitmap.GetPixel(lamp.Width/4,lamp.Height/4).ToArgb()==lamp.BackColor.ToArgb(),"flat lamp interior has no 3D highlight");
            Check(bitmap.GetPixel(lamp.Width/2,lamp.Height/2).ToArgb()==lamp.BackColor.ToArgb(),"lamp keeps its real state color");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[0]), "home-actions-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(qa);
            string first = Path.Combine(qa, "fixture-1"), second = Path.Combine(qa, "fixture-2");
            foreach (string path in new[] { first, second })
            {
                Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n");
                File.WriteAllText(Path.Combine(path, "CABLE.in"), "[CABLE]\r\n");
            }
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
            {
                var settings = new AppSettings { DataRoot = qa, LanguageCode = language }; settings.Hardware.Enabled = true;
                var state = new ApplicationState();
                string connect = Caption(language, "连接设备", "Connect device"), disconnect = Caption(language, "断开设备", "Disconnect device");
                using (var form = new MainForm(settings, state, true))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                    form.ShowInTaskbar = false; form.ClientSize = new Size(1065, 720); form.Show(); Pump();
                    var host = Field<ModuleHost>(form, "moduleHost");
                    Set(host, "diagnostics", new Diagnostics(Path.Combine(qa, "diagnostics")));
                    var release = Field<Button>(form, "releaseProjectButton"); var button = Field<Button>(form, "disconnectDeviceButton");
                    Check(!release.Visible && !release.Enabled, "no project hides release instead of only disabling it");
                    Action(form, connect, true);
                    Check(!state.Hardware.Connected, "enabling hardware mode is not a connection snapshot");
                    settings.Hardware.Enabled = false; Call(form, "ApplyState"); Action(form, connect, false);
                    settings.Hardware.Enabled = true; Call(form, "ApplyState");

                    int requests = 0, disconnects = 0, confirmations = 0; Action<HardwareStatusSnapshot> complete = null;
                    form.RefreshDeviceForTests = (requestConnection, callback) =>
                    {
                        Check(requestConnection, "connect action uses existing explicit-connect refresh path"); requests++; complete = callback; return true;
                    };
                    form.ConfirmDeviceDisconnectForTests = () => { confirmations++; return true; };
                    form.DeviceConnectionNoticeForTests = ignored => { };
                    form.ProjectReleaseError = ignored => { };
                    var disconnected = new TaskCompletionSource<HardwareStatusSnapshot>();
                    form.DisconnectDeviceForTests = () => { disconnects++; return disconnected.Task; };
                    button.PerformClick(); Pump();
                    Check(requests == 1 && disconnects == 0 && confirmations == 0, "initial click connects, never disconnects or opens a high-voltage path");
                    Action(form, connect, false); button.PerformClick();
                    Check(requests == 1, "in-flight connection cannot be repeated");
                    complete(Snapshot(true)); Pump(); Action(form, disconnect, true);
                    Check(!state.DeviceConnected && state.Hardware.Connected, "unconfigured physical connection is still a disconnect action");

                    Set(form, "hardwareRefreshInProgress", true); Set(form, "hardwareRefreshSilent", true); Call(form, "ApplyState");
                    Action(form, disconnect, true);
                    Set(form, "hardwareRefreshSilent", false); Call(form, "ApplyState"); Action(form, disconnect, true);
                    Set(form, "hardwareRefreshInProgress", false); Call(form, "ApplyState");
                    form.ConfirmDeviceDisconnectForTests = () => { confirmations++; return false; };
                    button.PerformClick(); Pump();
                    Check(disconnects == 0 && state.Hardware.Connected, "cancelled disconnection keeps actual connection and action");
                    Action(form, disconnect, true);
                    form.ConfirmDeviceDisconnectForTests = () => { confirmations++; return true; };
                    button.PerformClick(); Pump(); Action(form, disconnect, false);
                    Check(disconnects == 1, "connected action uses existing guarded disconnect path");
                    button.PerformClick(); Check(disconnects == 1, "in-flight disconnect cannot repeat");
                    disconnected.SetResult(Snapshot(false)); Pump(); Action(form, connect, true);
                    Check(Field<bool>(form, "hardwareManuallyDisconnected"), "manual disconnection remains paused until explicit reconnect");
                    Check(!Field<Timer>(form, "devicePollTimer").Enabled, "successful disconnect does not auto-reconnect");
                    button.PerformClick(); Pump(); Check(requests == 2 && !Field<bool>(form, "hardwareManuallyDisconnected"), "connect button explicitly resumes a manually disconnected session");
                    complete(Snapshot(false)); Pump(); Action(form, connect, true);
                    Check(!state.DeviceConnected, "failed connection does not fabricate connected status");

                    Set(form, "hardwareManuallyDisconnected", true); state.SetHardware(Snapshot(false, false, "DisconnectFailed")); Pump();
                    Action(form, disconnect, true);
                    Check(Field<Panel>(form, "deviceDot").BackColor == AppTheme.Warning, "unconfirmed disconnect stays amber rather than claiming disconnected");
                    form.DisconnectDeviceForTests = () => { disconnects++; return Task.FromResult(Snapshot(false)); };
                    button.PerformClick(); Pump(); Check(requests == 2 && disconnects == 2, "unconfirmed release retries disconnect without reconnecting"); Action(form, connect, true);
                    Set(form, "hardwareManuallyDisconnected", false);
                    Set(form, "hardwareRefreshInProgress", true); Set(form, "hardwareRefreshSilent", false); Call(form, "ApplyState");
                    Action(form, connect, false); button.PerformClick(); Check(requests == 2, "unknown connection while checking cannot start a duplicate request");
                    Set(form, "hardwareRefreshInProgress", false); Set(form, "settingsDialogActive", true); Call(form, "ApplyState");
                    Action(form, connect, false); button.PerformClick(); Check(requests == 2, "settings edit cannot start a device request");
                    Set(form, "settingsDialogActive", false); Call(form, "ApplyState");

                    string error; Check(state.TryLoadProject(first, out error), "isolated project loads"); Pump();
                    Check(release.Visible && release.Enabled, "loaded project immediately shows release");
                    Check(release.Text == Caption(language, "释放项目", "Release project"), "release caption follows language");
                    Check(state.TryLoadProject(second, out error), "isolated project switches"); Pump();
                    Check(release.Visible && (string)Field<Label>(form, "projectDetail").Tag == second, "project switch keeps release and updates card context");
                    using (var busy = new BusyPage { IsBusy = true })
                    {
                        var controls = Field<Dictionary<string, Control>>(host, "controls"); controls[ModuleHost.DataAnalysis] = busy;
                        release.PerformClick(); Pump(); Check(state.ProjectLoaded && release.Visible, "busy release refusal retains project and button");
                        controls.Remove(ModuleHost.DataAnalysis);
                    }
                    release.PerformClick(); Pump(); Check(!state.ProjectLoaded && !release.Visible && !release.Enabled, "successful actual release immediately hides action");
                    Check(File.Exists(Path.Combine(second, "ITEM.in")) && File.Exists(Path.Combine(second, "CABLE.in")), "release never deletes project files");
                    Check(File.ReadAllText(Path.Combine(second, "CABLE.in")) == "[CABLE]\r\n", "connection and release do not alter cable records");
                    Call(form, "ApplyResponsiveChrome"); Pump(); Check(!release.Visible, "resize/layout cannot resurrect a hidden empty-project action");
                    Panel projectCard = Field<Panel>(form, "projectStatusCard"); Label projectDetail = Field<Label>(form, "projectDetail");
                    Check(projectDetail.Right >= projectCard.ClientSize.Width - 16, "empty-project caption recovers hidden action space");
                    foreach (var size in new[] { new Size(1065, 720), new Size(1920, 1080) })
                    {
                        form.ClientSize = size; Call(form, "ApplyResponsiveChrome"); Pump(); Action(form, connect, true);
                        Check(!release.Visible, "release remains hidden at " + size);
                    }
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(qa, "empty-disconnected-" + language + ".png")); }
                    state.TryLoadProject(first, out error); state.SetHardware(Snapshot(true, true)); Pump(); Action(form, disconnect, true);
                    Check(release.Visible, "reloading after release restores action");
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(qa, "loaded-connected-" + language + ".png")); }
                    using (var module = new Panel())
                    {
                        Set(form, "activeModule", module); Call(form, "UpdateDeviceStatusActions"); Call(form, "UpdateProjectReleaseUi");
                        Check(!button.Visible && !release.Visible, "home-only actions stay hidden in modules");
                        Set(form, "activeModule", null);
                    }
                    Check(requests == 2 && disconnects == 2, "all operations remain limited to explicit fixture calls");
                    form.Close();
                }
            }
            Console.WriteLine("HOME_CONNECTION_PROJECT_ACTIONS_PASS=" + checks + "; HARDWARE_COMMANDS=0; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
    }
}
