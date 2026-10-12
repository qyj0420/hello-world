using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class VoltagePlanFeedbackSmoke
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static Type fileType, stepType;

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass release root and QA output directory.");
            string release = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Assembly module = Assembly.LoadFrom(Path.Combine(release, "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            fileType = module.GetType("HVX.Controls.VoltagePlanFile", true);
            stepType = module.GetType("HVX.Controls.PressureStep", true);
            Check(module.GetManifestResourceNames().Contains("XIONGXIN.Brand.ico"), "Brand icon embedded");
            string defaultDirectory = (string)fileType.GetProperty("DefaultDirectory", Static).GetValue(null, null);
            Check(defaultDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XIONGXIN", "HV DAC Xpert", "Voltage Plans"), "Dedicated Documents path; no directory creation during inspection");
            string path = Path.Combine(output, "客户试验计划.hvplan");
            IList valid = Steps(0, 0.5, 1, 1.5, 2, 1, 0);
            CallFile("Save", path, "客户试验计划", valid);
            object loaded = CallFile("Load", path);
            Check((string)Get(loaded, "Name") == "客户试验计划", "UTF8 named plan round trip");
            Check(((IList)Get(loaded, "Steps")).Count == 7, "Step order and count retained");
            Check((double)Get(((IList)Get(loaded, "Steps"))[4], "Multiplier") == 2, "Step multiplier retained");
            string json = File.ReadAllText(path);
            Check(json.Contains("XIONGXIN.VoltagePlan") && json.Contains("\"version\":1"), "Versioned file identity");
            Check(!json.Contains("voltageLimit") && !json.Contains("serial") && !json.Contains("project"), "No device or project identity in template");
            CallFile("Save", path, "Updated plan", Steps(0, 0.6, 1.2, 0));
            Check((string)Get(CallFile("Load", path), "Name") == "Updated plan", "Atomic replacement valid");
            byte[] original = File.ReadAllBytes(path);
            ExpectReject(delegate { CallFile("Save", path, "invalid", Steps(double.NaN)); }, "NaN save rejected");
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Invalid save preserves destination bytes");
            ExpectReject(delegate { CallFile("Save", path, "invalid", Steps(double.PositiveInfinity)); }, "Infinity rejected");
            ExpectReject(delegate { CallFile("Save", path, "invalid", Steps(-0.1)); }, "Negative multiplier rejected");
            ExpectReject(delegate { CallFile("Save", path, "invalid", Steps(101)); }, "Multiplier bound");
            ExpectReject(delegate { CallFile("Save", path, new string('x', 101), valid); }, "Long name rejected");
            ExpectReject(delegate { CallFile("Save", path, " ", valid); }, "Empty name rejected");
            ExpectReject(delegate { CallFile("Save", path, "plan\nname", valid); }, "Control characters rejected");
            ExpectReject(delegate { CallFile("Save", path, "empty", Steps()); }, "Empty steps rejected");
            ExpectReject(delegate { CallFile("Save", path, "too many", Steps(Enumerable.Repeat(1d, 513).ToArray())); }, "513 steps rejected");
            IList excessive = Steps(Enumerable.Repeat(1d, 101).ToArray());
            foreach (object row in excessive) row.GetType().GetField("Repetitions", Hidden).SetValue(row, 10000);
            ExpectReject(delegate { CallFile("Save", path, "too many repetitions", excessive); }, "Total repetitions bound");
            CallFile("Save", Path.Combine(output, "maximum.hvplan"), "Maximum rows", Steps(Enumerable.Repeat(1d, 512).ToArray()));
            Check(((IList)Get(CallFile("Load", Path.Combine(output, "maximum.hvplan")), "Steps")).Count == 512, "512 rows supported");
            string transferred = Path.Combine(output, "another-user", "移交.hvplan");
            Directory.CreateDirectory(Path.GetDirectoryName(transferred));
            File.Copy(path, transferred, true);
            Check((string)Get(CallFile("Load", transferred), "Name") == "Updated plan", "Portable file not account-bound");
            Check(!Directory.GetFiles(output, "*.tmp").Any(), "No staging fragments after save");

            string[] invalidDocuments =
            {
                "", "{", "[]", "{\"format\":\"XIONGXIN.VoltagePlan\",\"version\":2,\"name\":\"x\",\"steps\":[]}",
                "{\"format\":\"OTHER\",\"version\":1,\"name\":\"x\",\"steps\":[{\"multiplier\":1,\"repetitions\":1}]}",
                "{\"format\":\"XIONGXIN.VoltagePlan\",\"version\":\"1\",\"name\":\"x\",\"steps\":[{\"multiplier\":1,\"repetitions\":1}]}",
                Document("{\"multiplier\":-1,\"repetitions\":1}"), Document("{\"multiplier\":1,\"repetitions\":0}"),
                Document("{\"multiplier\":1,\"repetitions\":1.5}"), Document("{\"multiplier\":1,\"repetitions\":10001}"),
                Document("{\"multiplier\":\"NaN\",\"repetitions\":1}"), Document("{\"multiplier\":1e308,\"repetitions\":1}"),
                Document("{\"multiplier\":1,\"repetitions\":1,\"deviceLimit\":100}"),
                Document("{\"multiplier\":1,\"repetitions\":1}").Replace("\"version\":1", "\"version\":1,\"deviceLimit\":100"),
                new string(' ', 262145)
            };

            Type controlType = module.GetType("HVX.Controls.ProjectCreationControl", true);
            using (Form host = new Form { AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96, 96), Text = "Voltage plan offline QA", StartPosition = FormStartPosition.Manual, Location = new Point(16, 16) })
            using (Control control = (Control)Activator.CreateInstance(controlType))
            {
                controlType.GetProperty("ShowDialogMessages").SetValue(control, false, null);
                controlType.GetProperty("EmbeddedMode").SetValue(control, true, null);
                controlType.GetProperty("SettingsPath").SetValue(control, Path.Combine(output, "qa-settings.ini"), null);
                controlType.GetProperty("LanguageIndex").SetValue(control, 1, null);
                controlType.GetProperty("PressureMode").SetValue(control, "新电缆", null);
                controlType.GetProperty("PhaseVoltage").SetValue(control, "8.7kV", null);
                controlType.GetMethod("SetOptionalSectionExpanded").Invoke(control, new object[] { "TestConfiguration", true });
                control.Dock = DockStyle.Fill;
                host.Controls.Add(control);
                host.Show();
                host.ClientSize = new Size(1065, 650);
                Application.DoEvents();
                Call(control, "ApplyVoltagePlanFile", path);
                Check((string)controlType.GetProperty("PressureMode").GetValue(control, null) == "自定义", "Loaded template selects custom mode");
                DataGridView preview = (DataGridView)Get(control, "pressurePreviewGrid");
                Check(preview.Visible && preview.Rows.Count == 4, "Inline plan table immediately visible");
                Check(Convert.ToString(preview.Rows[2].Cells[3].Value) == "10.44", "Voltage preview uses current phase voltage without changing multiplier");
                Check(preview.ScrollBars == ScrollBars.Vertical && preview.ReadOnly, "No horizontal table scrollbar; preview read-only");
                string state = State(control);
                for (int index = 0; index < invalidDocuments.Length; index++)
                {
                    string bad = Path.Combine(output, "invalid-" + index + ".hvplan");
                    File.WriteAllText(bad, invalidDocuments[index], new UTF8Encoding(false));
                    ExpectReject(delegate { Call(control, "ApplyVoltagePlanFile", bad); }, "Invalid load " + index);
                    Check(state == State(control), "Invalid load leaves current plan unchanged " + index);
                }
                foreach (int language in new[] { 0, 1, 6 })
                {
                    controlType.GetProperty("LanguageIndex").SetValue(control, language, null);
                    foreach (Size size in new[] { new Size(760, 520), new Size(1065, 650), new Size(1600, 900) })
                    {
                        host.ClientSize = size;
                        host.PerformLayout();
                        Application.DoEvents();
                        Check(preview.Visible && preview.Height > 50, "Inline plan remains usable at " + size + " language " + language);
                        foreach (string field in new[] { "saveButton", "loadButton", "clearButton", "exitButton", "pressureEditButton", "pressureSaveAsButton", "pressureLoadButton" })
                        {
                            Button button = (Button)Get(control, field);
                            int needed = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                            Check(button.Width >= needed + 12, "Full button text " + field + " at " + size + " language " + language);
                            Check(button.Parent.ClientRectangle.Contains(button.Bounds), "Button contained " + field + " at " + size + " language " + language);
                        }
                        if (language <= 1 && size.Width != 1600) Render(host, Path.Combine(output, "voltage-plan-" + language + "-" + size.Width + ".png"));
                    }
                }
                controlType.GetProperty("LanguageIndex").SetValue(control, 1, null);
                Type dialogType = module.GetType("HVX.Controls.PressureEditorDialog", true);
                using (Form dialog = (Form)Activator.CreateInstance(dialogType, Hidden, null, new object[] { "新电缆", valid, valid, 1 }, null))
                {
                    dialog.Show(host);
                    Application.DoEvents();
                    Check(dialog.Icon != null, "Editor icon present");
                    foreach (Button button in Descendants(dialog).OfType<Button>())
                    {
                        int needed = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                        Check(button.Width >= needed + 12, "Dialog full action text " + button.Text);
                        Check(button.Parent.ClientRectangle.Contains(button.Bounds), "Dialog button contained " + button.Text);
                    }
                    DataGridView editorGrid = Descendants(dialog).OfType<DataGridView>().Single();
                    Check(editorGrid.ColumnHeadersHeight >= editorGrid.Font.Height + 4, "Editor table header not cropped");
                    Check(editorGrid.ScrollBars == ScrollBars.Vertical, "Editor no horizontal scrollbar");
                    Render(dialog, Path.Combine(output, "voltage-plan-editor-en.png"));
                    dialog.Close();
                }
                host.Close();
            }
            Console.WriteLine("VOLTAGE_PLAN_FEEDBACK_PASS=" + checks);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static IList Steps(params double[] multipliers)
    {
        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stepType));
        foreach (double multiplier in multipliers)
        {
            object step = Activator.CreateInstance(stepType, true);
            stepType.GetField("Multiplier", Hidden).SetValue(step, multiplier);
            stepType.GetField("Repetitions", Hidden).SetValue(step, 1);
            list.Add(step);
        }
        return list;
    }

    static object Get(object value, string field) { return value.GetType().GetField(field, Hidden).GetValue(value); }
    static object CallFile(string method, params object[] args) { return fileType.GetMethod(method, Static).Invoke(null, args); }
    static object Call(object owner, string method, params object[] args) { return owner.GetType().GetMethod(method, Hidden).Invoke(owner, args); }
    static void Check(bool ok, string label) { checks++; if (!ok) throw new InvalidOperationException(label); }
    static void ExpectReject(Action action, string label)
    {
        try { action(); }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Check(error is InvalidDataException || error is IOException || error is ArgumentException || error is InvalidOperationException, label + " returns a file/validation error: " + error.GetType().Name);
            return;
        }
        throw new InvalidOperationException(label + " unexpectedly accepted");
    }
    static string Document(string rows) { return "{\"format\":\"XIONGXIN.VoltagePlan\",\"version\":1,\"name\":\"Invalid test\",\"steps\":[" + rows + "]}"; }
    static string State(object control)
    {
        IList steps = (IList)Get(control, "currentPressureSteps");
        return Get(control, "voltagePlanName") + "|" + string.Join(";", steps.Cast<object>().Select(s => Get(s, "Multiplier") + ":" + Get(s, "Repetitions")));
    }
    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
    }
    static void Render(Form form, string path)
    {
        using (Bitmap image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
    }
}
