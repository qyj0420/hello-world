using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class DeviceSetupNavigationSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static object Get(object value, string name) { return value.GetType().GetProperty(name, All).GetValue(value, null); }
    static void Set(object value, string name, object content) { value.GetType().GetProperty(name, All).SetValue(value, content, null); }
    static object Call(object value, string name, params object[] args)
    { Type type = value as Type ?? value.GetType(); return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args); }
    static void Check(bool ok, string text) { checks++; if (!ok) throw new Exception(text); }
    static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }
    static void FontTree(Control owner, Font font) { owner.Font = font; foreach (Control child in owner.Controls) FontTree(child, font); }
    static Form Create(Type formType, object settings, object actual, bool checking)
    { return (Form)Activator.CreateInstance(formType, All, null, new object[] { settings, actual, false, checking }, CultureInfo.InvariantCulture); }

    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass BuildRoot and QA directory.");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "setup-nav-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "Xiongxin.OWTS.Desktop.exe"));
            Type accessType = assembly.GetType("Xiongxin.OWTS.Desktop.SettingsAccessControl", true);
            bool maintenancePasswordEnabled = (bool)accessType.GetField("Enabled", All).GetRawConstantValue();
            Type statusType = assembly.GetType("Xiongxin.OWTS.Desktop.DeviceSetupStatus", true), formType = assembly.GetType("Xiongxin.OWTS.Desktop.DeviceSetupForm", true);
            Type settingsType = assembly.GetType("Xiongxin.OWTS.Desktop.AppSettings", true), actualType = assembly.GetType("Xiongxin.OWTS.Desktop.HardwareStatusSnapshot", true);
            object settings = Activator.CreateInstance(settingsType, true), actual = Activator.CreateInstance(actualType, true);
            object hardware = Get(settings, "Hardware"); Set(hardware, "SelectedHubMac", "1CBCEC5A9C5C");
            foreach (string name in new[] { "HardwareEnabled", "DriverInstalled", "Connected", "IdentityValid" }) Set(actual, name, true);
            Set(actual, "StatusCode", "Ready"); Set(actual, "HubMac", "1CBCEC5A9C5C"); Set(actual, "ControllerSerial", "AB123/4567"); Set(actual, "AcquisitionSerial", "1234567890"); Set(actual, "AssetMatchStatus", "Unknown");
            string beforeHash = (string)Call(statusType, "ParameterHash", settings);
            foreach (string mode in new[] { "offline", "online", "checking", "missing-identity" })
            {
                bool enabled = mode != "offline", checking = mode == "checking";
                Set(hardware, "Enabled", enabled); Set(actual, "AcquisitionSerial", mode == "missing-identity" ? "" : "1234567890");
                int[] expected = new[] { 1, 1, checking ? 0 : enabled ? 3 : 1, 1, 2, 2, checking ? 0 : !enabled ? 1 : mode == "missing-identity" ? 3 : 2, 4, 0 };
                IList steps = (IList)Call(statusType, "Steps", settings, actual, false, checking, true);
                for (int i = 0; i < 9; i++)
                {
                    Check(Convert.ToInt32(Field(steps[i], "Action")) == expected[i], mode + " routes step " + i);
                    Check(!string.IsNullOrWhiteSpace((string)Field(steps[i], "ActionLabel")) && ((string)Field(steps[i], "Detail")).Contains("\r\n"), "Each step explains evidence and completion instructions");
                    using (Form form = Create(formType, settings, actual, checking))
                    {
                        Check(Convert.ToInt32(Get(form, "RequestedAction")) == 0, "Opening setup does not trigger navigation");
                        bool requested = (bool)Call(form, "TryRequestStep", i);
                        Check(requested == (expected[i] != 0), "Only actionable steps request navigation");
                        Check(Convert.ToInt32(Get(form, "RequestedAction")) == expected[i], "Stable requested-action contract");
                        Check(expected[i] == 0 ? form.DialogResult != DialogResult.OK : form.DialogResult == DialogResult.OK, "Only actual navigation closes with OK");
                        if (requested) Check(!(bool)Call(form, "TryRequestStep", i), "Double delivery cannot repeat navigation");
                    }
                }
                Check(!(bool)Call(statusType, "ParametersSaved", settings), "Navigation never saves/marks device parameters reviewed");
            }
            Check((string)Call(statusType, "ParameterHash", settings) == beforeHash, "No setup action modifies parameter values");
            Type sectionType = assembly.GetType("Xiongxin.OWTS.Desktop.SettingsSection", true);
            Type settingsFormType = assembly.GetType("Xiongxin.OWTS.Desktop.SettingsForm", true);
            object generalSection = Enum.Parse(sectionType, "General");
            using (Form routineForm = (Form)Activator.CreateInstance(settingsFormType, All, null,
                new[] { settings, generalSection, actual }, CultureInfo.InvariantCulture))
            {
                Set(routineForm, "AuditEnabled", false);
                Check((bool)Call(routineForm, "IsSectionAuthorized", generalSection), "Routine settings remain password-free in both build profiles");
                Check(Field(routineForm, "dataRoot") is TextBox && Field(routineForm, "reportOutputPath") is TextBox, "Routine data and report editors remain available");
                Check(!(bool)Field(routineForm, "systemAdminUnlocked") && !(bool)Field(routineForm, "deviceUnlocked"), "Routine navigation does not open maintenance editors");
            }
            foreach (string section in new[] { "SystemAdmin", "Device" })
            {
                object requestedSection = Enum.Parse(sectionType, section);
                string unlockedField = section == "SystemAdmin" ? "systemAdminUnlocked" : "deviceUnlocked";
                string editorField = section == "SystemAdmin" ? "hardwareEnabled" : "dividerRatio";
                using (Form protectedForm = (Form)Activator.CreateInstance(settingsFormType, All, null,
                    new[] { settings, requestedSection, actual }, CultureInfo.InvariantCulture))
                {
                    Set(protectedForm, "AuditEnabled", false);
                    Check((bool)Field(protectedForm, unlockedField) == !maintenancePasswordEnabled, "Direct section entry honors the actual build authorization profile " + section);
                    Check((bool)Call(protectedForm, "IsSectionAuthorized", requestedSection) == !maintenancePasswordEnabled, "Section authorization agrees with the build profile " + section);
                    if (maintenancePasswordEnabled)
                    {
                        Check(Field(protectedForm, editorField) == null, "Protected editor is absent before authorization " + section);
                        Check(protectedForm.Controls.Find("UnlockSettings" + section, true).Length == 1, "Protected navigation presents an explicit unlock entry " + section);
                        Call(protectedForm, "OpenSection", requestedSection);
                        Check(!(bool)Field(protectedForm, unlockedField) && Field(protectedForm, editorField) == null, "Direct OpenSection cannot bypass maintenance authorization " + section);
                        foreach (string rejectedPassword in new[] { null, Guid.NewGuid().ToString("N") })
                        {
                            Check(!(bool)Call(accessType, "Verify", requestedSection, rejectedPassword), "Missing or incorrect credential is rejected " + section);
                            Check(!(bool)Call(protectedForm, "TryUnlockSection", requestedSection, rejectedPassword), "Rejected unlock cannot navigate into protected settings " + section);
                            Check(!(bool)Call(protectedForm, "IsSectionAuthorized", requestedSection) && !(bool)Field(protectedForm, unlockedField) && Field(protectedForm, editorField) == null, "Rejected unlock leaves authorization and protected editors unchanged " + section);
                        }
                        Check(protectedForm.DialogResult == DialogResult.None, "Protected navigation and rejection do not save settings " + section);
                    }
                    else
                    {
                        Check(Field(protectedForm, editorField) is Control, "Password-free profile retains directly accessible editor " + section);
                        Check(protectedForm.Controls.Find("UnlockSettings" + section, true).Length == 0, "Password-free profile adds no maintenance prompt " + section);
                    }
                    Check((string)Call(statusType, "ParameterHash", Call(protectedForm, "CaptureValues")) == beforeHash, "Opening or rejected authorization preserves device parameter values " + section);
                    Check(!(bool)Call(statusType, "ParametersSaved", settings), "Opening the editor does not confirm device parameters");
                }
            }

            Set(hardware, "Enabled", false);
            foreach (string language in new[] { "zh-CN", "en", "zh-TW" })
                foreach (float size in new[] { 10.5f, 18f })
                {
                    Set(settings, "LanguageCode", language);
                    using (Form form = Create(formType, settings, actual, false))
                    using (var font = new Font("Microsoft YaHei UI", size))
                    {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.ShowInTaskbar = false;
                        FontTree(form, font); form.Size = new Size(760, 500); form.Show(); Pump();
                        var grid = (DataGridView)form.Controls.Find("DeviceSetupSteps", true)[0];
                        var instructions = (TextBox)form.Controls.Find("SetupInstructions", true)[0];
                        Check(grid.ReadOnly && instructions.ReadOnly && instructions.ScrollBars == ScrollBars.Vertical, "Read-only steps and scrollable instructions");
                        Check(instructions.ClientSize.Height >= instructions.Font.Height, "At least one full instruction line remains visible at large font");
                        Check(!((Button)form.Controls.Find("RecheckConnection", true)[0]).Enabled, "Offline connection request disabled");
                        Check(grid.Columns.Count == 3 && grid.ScrollBars == ScrollBars.Vertical, "Concise step list avoids long hidden evidence cells");
                        foreach (string name in new[] { "SetupIntroduction", "DeviceSetupSteps", "SetupInstructionsPanel", "SetupActions", "CloseSetup", "ReviewParameters", "RecheckConnection", "FirstHardwareBinding" })
                        {
                            Control c = form.Controls.Find(name, true)[0]; Rectangle bounds = form.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
                            Check(form.ClientRectangle.Contains(bounds) && c.Width > 0 && c.Height > 0, "Visible layout " + language + "/" + size + "/" + name);
                        }
                        foreach (int row in Enumerable.Range(0, grid.Rows.Count))
                        {
                            grid.CurrentCell = grid.Rows[row].Cells[0]; grid.FirstDisplayedScrollingRowIndex = row; Pump();
                            Check(instructions.Text.StartsWith(grid.Rows[row].Cells[0].Value.ToString()), "Selecting row changes full completion guide");
                            Check(grid.Rows[row].Height <= grid.ClientSize.Height - grid.ColumnHeadersHeight, "Visible row fits grid viewport at large font: " + language + "/" + size + "/row=" + row + "/height=" + grid.Rows[row].Height + "/available=" + (grid.ClientSize.Height-grid.ColumnHeadersHeight));
                        }
                        Check(grid.Rows[8].Cells[2] is DataGridViewTextBoxCell, "Unavailable preview step is explanatory rather than a fake launch link");
                        if (language == "zh-TW") Check(form.Text.Contains("狀態") && grid.Columns[0].HeaderText.Contains("驟") && instructions.Text.Contains("高壓"), "Traditional Chinese reaches title, grid and detailed guide");
                        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(qa, language + "-" + size.ToString(CultureInfo.InvariantCulture) + ".png")); }
                        form.Close();
                    }
                }
            // Verify the actual event bindings, not just their common handler.
            Set(hardware, "Enabled", true);
            foreach (string eventName in new[] { "OnCellDoubleClick", "OnCellContentClick", "OnKeyDown" })
                using (Form form = Create(formType, settings, actual, false))
                {
                    var grid = (DataGridView)form.Controls.Find("DeviceSetupSteps", true)[0]; grid.CurrentCell = grid.Rows[5].Cells[0];
                    object evt = eventName == "OnKeyDown" ? (object)new KeyEventArgs(Keys.Enter) : new DataGridViewCellEventArgs(eventName == "OnCellContentClick" ? 2 : 0, 5);
                    typeof(DataGridView).GetMethod(eventName, All).Invoke(grid, new[] { evt });
                    Check(Convert.ToInt32(Get(form, "RequestedAction")) == 2, "Wired event requests Device parameters: " + eventName);
                }
            Console.WriteLine("DEVICE_SETUP_NAVIGATION_PASS=" + checks + "; MaintenancePasswordEnabled=" + maintenancePasswordEnabled + "; HARDWARE_COMMANDS=0; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
