using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class HomeDeviceActionsSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static T Field<T>(object value, string name) { return (T)value.GetType().GetField(name, Hidden).GetValue(value); }
    static void Set(object value, string name, object content) { value.GetType().GetField(name, Hidden).SetValue(value, content); }
    static void Call(object value, string name) { value.GetType().GetMethod(name, Hidden).Invoke(value, null); }
    static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    static void CheckAction(MainForm form, string cardField, string buttonField, string primaryField, string detailField)
    {
        Panel card = Field<Panel>(form, cardField); Button button = Field<Button>(form, buttonField);
        Label primary = Field<Label>(form, primaryField), detail = Field<Label>(form, detailField);
        Check(button.Parent == card && button.Visible && card.ClientRectangle.Contains(button.Bounds), buttonField + " stays inside its status card");
        Check(button.Top >= 3 && card.ClientSize.Height - button.Bottom >= 3 && button.Height >= card.ClientSize.Height * .75, buttonField + " fills card height with padding");
        Check(Math.Abs(button.Top - (card.ClientSize.Height - button.Bottom)) <= 1, buttonField + " has balanced top/bottom padding");
        Check(primary.Right < button.Left && detail.Right < button.Left, buttonField + " cannot cover either line of text");
        Check(primary.Width >= TextRenderer.MeasureText(primary.Text, primary.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width,
            primaryField + " title remains complete: " + primary.Text + "; client=" + form.ClientSize + "; card=" + card.Bounds + "; title=" + primary.Bounds + "; button=" + button.Bounds + "; font=" + primary.Font);
        Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + 20, buttonField + " text fully fits");
        Check(button.Height >= button.Font.Height + 8, buttonField + " font fits height");
        Check(primary.Height >= primary.Font.Height, primaryField + " title height fits native DPI font");
        Check(card.Parent.ClientRectangle.Contains(card.Bounds), cardField + " remains wholly inside status row");
        Rectangle visible = card.RectangleToScreen(card.ClientRectangle);
        for (Control ancestor = card.Parent; ancestor != null; ancestor = ancestor.Parent)
            Check(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(visible), cardField + " is not clipped by ancestor " + ancestor.GetType().Name + "; ancestor=" + ancestor.ClientSize + "; cardScreen=" + visible);
        var command = Field<Panel>(form, "commandBarPanel"); var content = Field<Panel>(form, "contentPanel");
        Check(command.Bottom <= content.Top, "main content cannot overlap or cover the command/status row: client=" + form.ClientSize + "; command=" + command.Bounds + "; content=" + content.Bounds + "; row=" + Field<TableLayoutPanel>(form,"rootLayout").RowStyles[1].Height);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[0]), "home-device-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            string project = Path.Combine(qa, "fixture-project"); Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "ITEM.in"), "[ITEM]\r\n"); File.WriteAllText(Path.Combine(project, "CABLE.in"), "[CABLE]\r\n");
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
            {
                var settings = new AppSettings { LanguageCode = language }; settings.Hardware.Enabled = true;
                var state = new ApplicationState(); string error; Check(state.TryLoadProject(project, out error), "fixture project loaded");
                using (var form = new MainForm(settings, state, true))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false; form.Show(); Pump();
                    Button disconnect = Field<Button>(form, "disconnectDeviceButton"), release = Field<Button>(form, "releaseProjectButton");
                    Panel lamp = Field<Panel>(form, "deviceDot");
                    state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true, Connected = true, IdentityValid = true, StatusCode = "Ready" }); Pump();
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Success, "ready snapshot enables disconnect and lights green");
                    Check(lamp.AccessibleRole == AccessibleRole.Indicator && !String.IsNullOrWhiteSpace(lamp.AccessibleName), "connection lamp supplies a non-color status");
                    foreach (var size in new[] { new Size(1065, 720), new Size(1280, 800), new Size(1600, 900), new Size(1920, 1080) })
                    {
                        form.ClientSize = size; Call(form, "ApplyResponsiveChrome"); Pump();
                        CheckAction(form, "projectStatusCard", "releaseProjectButton", "projectStatus", "projectDetail");
                        CheckAction(form, "deviceStatusCard", "disconnectDeviceButton", "deviceStatus", "deviceDetail");
                        Check(release.Height == disconnect.Height && release.Top == disconnect.Top, "both right-edge actions have matching full height");
                        Check(lamp.Width >= 18 && lamp.Height >= 18 && lamp.Right < Field<Label>(form, "deviceStatus").Left, "large lamp does not cover text");
                        Check(Field<Panel>(form, "deviceStatusCard").ClientRectangle.Contains(lamp.Bounds), "lamp remains inside device card");
                        if (size.Width == 1280) using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(qa, "home-connected-" + language + ".png")); }
                    }
                    Set(form, "hardwareRefreshInProgress", true); Set(form, "hardwareRefreshSilent", true); Call(form, "ApplyState"); Pump();
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Success, "silent background refresh preserves connected lamp and allows disconnect");
                    Set(form, "hardwareRefreshSilent", false); Call(form, "ApplyState"); Pump();
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Warning, "explicit check shows amber and remains cancellable");
                    Set(form, "hardwareRefreshInProgress", false); Set(form, "hardwareDisconnectInProgress", true); Call(form, "ApplyState");
                    Check(!disconnect.Enabled && lamp.BackColor == AppTheme.Warning, "disconnect in progress prevents duplicate clicks");
                    Set(form, "hardwareDisconnectInProgress", false); Set(form, "hardwareManuallyDisconnected", true); Call(form, "ApplyState");
                    Check(disconnect.Enabled && lamp.BackColor != AppTheme.Success && Field<Label>(form, "deviceDetail").Text == lamp.AccessibleName, "manual disconnect offers explicit reconnection without claiming green");
                    state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = true, Connected = false, StatusCode = "DisconnectFailed" });
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Warning && Field<Label>(form, "deviceDetail").Text == lamp.AccessibleName, "failed disconnect permits retry and indicates uncertainty rather than claiming disconnected");
                    Set(form, "hardwareManuallyDisconnected", false);
                    state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = true, DriverInstalled = true, Connected = true, IdentityValid = false, StatusCode = "IdentityMismatch" });
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Warning, "connected but not ready still permits disconnect without a green-ready indication");
                    state.SetHardware(new HardwareStatusSnapshot { HardwareEnabled = true, StatusCode = "NotConnected" });
                    Check(disconnect.Enabled && lamp.BackColor != AppTheme.Success, "disconnected state offers an explicit connection action");
                    settings.Hardware.Enabled = false; Call(form, "ApplyState");
                    Check(disconnect.Enabled && lamp.BackColor == AppTheme.Muted, "offline mode uses gray while connection opens explicit discovery");
                    foreach (float points in new[] { 11f, 14f })
                    {
                        release.Font = AppTheme.Font(points, FontStyle.Regular); disconnect.Font = AppTheme.Font(points, FontStyle.Regular);
                        form.ClientSize = new Size(1065, 720); Call(form, "ApplyResponsiveChrome"); Pump();
                        CheckAction(form, "projectStatusCard", "releaseProjectButton", "projectStatus", "projectDetail");
                        CheckAction(form, "deviceStatusCard", "disconnectDeviceButton", "deviceStatus", "deviceDetail");
                    }
                    using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(qa, "home-font14-" + language + ".png")); }
                    state.ClearProject(); Check(!release.Enabled && !release.Visible, "empty project release action is hidden");
                    using (var dummy = new Panel())
                    {
                        Set(form, "activeModule", dummy); Call(form, "UpdateProjectReleaseUi"); Call(form, "UpdateDeviceStatusActions");
                        Check(!release.Visible && !disconnect.Visible, "home actions do not leak into module chrome");
                        Set(form, "activeModule", null);
                    }
                    // No device button or refresh action is clicked. Only fixture
                    // snapshots are supplied; no hardware driver is opened.
                    form.Close();
                }
            }
            Console.WriteLine("HOME_DEVICE_ACTIONS_PASS=" + checks + "; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error is TargetInvocationException ? ((TargetInvocationException)error).InnerException : error); return 1; }
    }
}
