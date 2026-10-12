using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class SettingsAccessSmoke
{
    private static int checks;

    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(description);
    }

    private static T Field<T>(object owner, string name) where T : class
    {
        FieldInfo field = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Check(field != null, "field exists: " + name);
        return field.GetValue(owner) as T;
    }

    private static bool Open(SettingsForm form, SettingsSection section)
    {
        MethodInfo method = typeof(SettingsForm).GetMethod("OpenSection", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(method != null, "gate exists");
        method.Invoke(form, new object[] { section }); return true;
    }

    private static AppSettings Capture(SettingsForm form)
    {
        MethodInfo method = typeof(SettingsForm).GetMethod("CaptureValues", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(method != null, "capture exists");
        return (AppSettings)method.Invoke(form, null);
    }

    private static void CheckPathEditors(Control root, string label)
    {
        foreach (TableLayoutPanel editor in root.Controls.OfType<TableLayoutPanel>().Concat(
            root.Controls.Cast<Control>().SelectMany(child => FindPathEditors(child))))
        {
            TextBox path = editor.Controls.OfType<TextBox>().FirstOrDefault();
            Button browse = editor.Controls.OfType<Button>().FirstOrDefault(button => button.Text == "浏览…");
            if (path == null || browse == null) continue;
            Check(path.Right + 4 <= browse.Left, label + " path and browse button do not overlap");
            Check(browse.Right <= editor.ClientSize.Width && browse.Bottom <= editor.ClientSize.Height,
                label + " browse button remains within path editor: editor=" + editor.ClientSize +
                " path=" + path.Bounds + " browse=" + browse.Bounds);
        }
    }

    private static System.Collections.Generic.IEnumerable<TableLayoutPanel> FindPathEditors(Control root)
    {
        foreach (Control child in root.Controls)
        {
            TableLayoutPanel panel = child as TableLayoutPanel;
            if (panel != null) yield return panel;
            foreach (TableLayoutPanel nested in FindPathEditors(child)) yield return nested;
        }
    }

    private static void CheckExpertLayout(float scale)
    {
        using (SettingsForm form = new SettingsForm(new AppSettings(), SettingsSection.Expert))
        {
            Check(Open(form, SettingsSection.Expert), "expert layout authorized");
            form.StartPosition = FormStartPosition.Manual;
            form.Opacity = 0;
            form.Location = new Point(-20000, -20000);
            form.Show();
            Application.DoEvents();
            if (Math.Abs(scale - 1f) > 0.001f) form.Scale(new SizeF(scale, scale));
            ResponsiveFormLayout.FitToBounds(form, new Rectangle(0, 0, 1366, 728),
                (int)Math.Round(96 * scale), new Size(760, 520));
            TabControl mainTabs = Field<TabControl>(form, "tabs");
            for (int page = 0; page < 2; page++)
            {
                mainTabs.SelectedIndex = page;
                form.PerformLayout();
                Application.DoEvents();
                CheckPathEditors(mainTabs.SelectedTab, "routine page " + page + " at " + scale);
            }
            mainTabs.SelectedTab = Field<TabPage>(form, "expertTab");
            TabControl expertTabs = Field<TabControl>(form, "expertTabs");
            for (int page = 0; page < expertTabs.TabPages.Count; page++)
            {
                expertTabs.SelectedIndex = page;
                form.PerformLayout();
                Application.DoEvents();
                CheckPathEditors(expertTabs.SelectedTab, "expert page " + page + " at " + scale);
            }
            expertTabs.SelectedIndex = 0;
            form.PerformLayout();
            Application.DoEvents();
            CheckBox[] quadrants = new[] {
                Field<CheckBox>(form, "minus3Quadrant0To90"),
                Field<CheckBox>(form, "minus3Quadrant90To180"),
                Field<CheckBox>(form, "minus3Quadrant180To270"),
                Field<CheckBox>(form, "minus3Quadrant270To360")
            };
            Check(quadrants.All(item => item != null && item.Visible), "all quadrants visible at " + scale);
            TableLayoutPanel quadrantGrid = quadrants[0].Parent as TableLayoutPanel;
            TableLayoutPanel settingsGrid = quadrantGrid == null ? null : quadrantGrid.Parent as TableLayoutPanel;
            Check(quadrantGrid != null && settingsGrid != null, "quadrant grid exists at " + scale);
            Label quadrantLabel = settingsGrid.GetControlFromPosition(0, settingsGrid.GetRow(quadrantGrid)) as Label;
            Check(quadrantLabel != null && !quadrantLabel.Bounds.IntersectsWith(quadrantGrid.Bounds),
                "quadrant label does not overlap choices at " + scale);
            for (int index = 0; index < quadrants.Length; index++)
            {
                Check(quadrants[index].Parent.ClientRectangle.Contains(quadrants[index].Bounds),
                    "quadrant remains in its panel at " + scale);
                for (int other = index + 1; other < quadrants.Length; other++)
                    Check(!quadrants[index].Bounds.IntersectsWith(quadrants[other].Bounds),
                        "quadrants do not overlap at " + scale);
            }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppSettings source = new AppSettings();
        source.DividerRatio = 117.0;
        source.MaximumVoltageKv = 27.0;
        source.Task35ModelPath = "protected-model.onnx";
        source.ReportTemplatePath = "protected-template.docx";
        source.SoftwareModel = "protected-oem-model";
        source.Hardware.SelectedHubMac = "1CBCEC5A9C5C";

        using (SettingsForm form = new SettingsForm(source))
        {
            TabControl tabs = Field<TabControl>(form, "tabs");
            Check(tabs.TabPages.Count == 3, "routine tabs and locked Plus Data entry appear initially");
            Check(Field<NumericUpDown>(form, "dividerRatio") == null, "device control is not built");
            Check(Field<TextBox>(form, "task35Model") == null, "expert control is not built");
            Check(Field<TextBox>(form, "reportTemplatePath") == null, "report template is not exposed in routine tab");
            Check(Field<TextBox>(form, "softwareModel") == null, "OEM control is not built");

            AppSettings routine = Capture(form);
            Check(routine.DividerRatio == 117.0 && routine.MaximumVoltageKv == 27.0,
                "routine save preserves device parameters");
            Check(routine.Task35ModelPath == "protected-model.onnx", "routine save preserves expert parameters");
            Check(routine.ReportTemplatePath == "protected-template.docx", "routine save preserves report template");
            Check(routine.SoftwareModel == "protected-oem-model", "routine save preserves OEM parameters");

            Check(Open(form, SettingsSection.Device), "device credential accepted");
            NumericUpDown divider = Field<NumericUpDown>(form, "dividerRatio");
            Check(divider != null, "device control built after authorization");
            divider.Value = 118M;
            AppSettings deviceEdited = Capture(form);
            Check(deviceEdited.DividerRatio == 118.0, "authorized device edit captured");
            Check(deviceEdited.Task35ModelPath == "protected-model.onnx", "device edit preserves expert settings");

            Check(Open(form, SettingsSection.Expert), "expert credential accepted");
            TextBox model = Field<TextBox>(form, "task35Model");
            Check(model == null, "expert authorization cannot expose Plus Data model editor");
            Check(Field<TextBox>(form, "reportTemplatePath") != null, "template editor built after expert authorization");
            Field<TextBox>(form, "reportTemplatePath").Text = "updated-template.docx";
            AppSettings expertEdited = Capture(form);
            Check(expertEdited.Task35ModelPath == "protected-model.onnx", "expert save preserves Plus Data model");
            Check(expertEdited.ReportTemplatePath == "updated-template.docx", "non-migrated expert edit still captured");
            Check(expertEdited.SoftwareModel == "protected-oem-model", "expert edit preserves OEM settings");

            Check(Open(form, SettingsSection.Oem), "OEM credential accepted");
            TextBox softwareModel = Field<TextBox>(form, "softwareModel");
            Check(softwareModel != null, "OEM control built after authorization");
            softwareModel.Text = "updated-oem-model";
            Check(Capture(form).SoftwareModel == "updated-oem-model", "authorized OEM edit captured");
        }

        using (SettingsForm direct = new SettingsForm(source, SettingsSection.Device))
        {
            Check(Field<TabControl>(direct, "tabs").TabPages.Count == 4, "direct device entry adds one locked tab beside Plus Data entry");
            Check(Field<NumericUpDown>(direct, "dividerRatio") != null, "direct entry opens without a password");
        }

        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f })
            CheckExpertLayout(scale);

        Console.WriteLine("SETTINGS_ACCESS_SMOKE_PASS=" + checks);
        return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("SETTINGS_ACCESS_SMOKE_FAIL=" + error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }
}
