using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

internal static class PressureProjectPlanCorrespondenceSmoke
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static AppSettings activeHostSettings;
    static void SetLanguage(Control control,int index)
    {
        // An attached host owns application language and reapplies its context
        // on page events. Change the host setting as well as the isolated page.
        if(activeHostSettings!=null)activeHostSettings.LanguageCode=index==1?"en":index==18?"zh-TW":"zh";
        Set(control,"LanguageIndex",index);
    }
    static readonly double[] Multipliers = { 0, .5, .6, 1.2, 1.5, 2, 1.2, .5, 0 };
    static readonly int[] Repetitions = { 1, 3, 2, 4, 2, 1, 5, 2, 3 };
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, All).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Call(object owner, string name, params object[] arguments) { return owner.GetType().GetMethod(name, All).Invoke(owner, arguments); }
    static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    static void Near(double expected, double actual, string label) { Check(Math.Abs(expected - actual) < 1e-9, label + ": " + actual); }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass modules root and isolated QA output directory.");
            string modules = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            Application.EnableVisualStyles();
            Assembly creation = Assembly.LoadFrom(Path.Combine(modules, "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            Assembly pressure = Assembly.LoadFrom(Path.Combine(modules, "PressureTest", "Xiongxin.OWTS.PressureTest.dll"));
            Type draftType = creation.GetType("HVX.Controls.ProjectDraft", true);
            Type stepType = creation.GetType("HVX.Controls.PressureStep", true);
            object draft = Activator.CreateInstance(draftType, true);
            draftType.GetField("PhaseVoltage", All).SetValue(draft, "8.7kV");
            IList steps = (IList)Field(draft, "PressureSteps");
            for (int index = 0; index < Multipliers.Length; index++)
            {
                object step = Activator.CreateInstance(stepType, true);
                stepType.GetField("Multiplier", All).SetValue(step, Multipliers[index]);
                stepType.GetField("Repetitions", All).SetValue(step, Repetitions[index]);
                steps.Add(step);
            }
            string content = (string)creation.GetType("HVX.Controls.ProjectCreationStorage", true)
                .GetMethod("BuildItem", All).Invoke(null, new[] { draft });
            Check(content.Contains("加压倍数") && content.Contains("加压次数"), "Fixture uses the real project editor ITEM writer");
            string project = MakeProject(output, "writer-plan", content, new UTF8Encoding(false));
            byte[] original = File.ReadAllBytes(Path.Combine(project, "ITEM.in"));
            Type controlType = pressure.GetType("HVX.Controls.PressureTestControl", true);
            using (Control control = (Control)Activator.CreateInstance(controlType))
            using (Control editor = (Control)Activator.CreateInstance(creation.GetType("HVX.Controls.ProjectCreationControl", true)))
            {
                Set(editor, "ShowDialogMessages", false);
                Set(editor, "SettingsPath", Path.Combine(output, "editor-settings.ini"));
                Call(editor, "ApplyDraft", draft);
                DataGridView editorGrid = (DataGridView)Field(editor, "pressurePreviewGrid");
                Set(control, "MaximumPeakVoltageKv", 100d);
                Set(control, "PlanVoltagesArePeak", true); // Loading a project must reset external peak interpretation.
                Check((bool)Call(control, "LoadProjectPlan", project), "Canonical editor ITEM loads");
                Check((string)Get(control, "PressureProgramModeCode") == "PLAN", "Project load selects saved plan");
                Check((int)Get(control, "SelectedPlanStepIndex") == 0, "First zero-voltage row selected");
                Check(!(bool)Get(control, "PlanVoltagesArePeak"), "Project calculated values identified as RMS");
                DataGridView plan = (DataGridView)Field(control, "pressurePlanGrid");
                Check(plan.Rows.Count == editorGrid.Rows.Count && plan.Rows.Count == Multipliers.Length, "One pressure row per editor row; repeats and descending rows retained");
                double[] actualMultipliers = (double[])Call(control, "GetPressurePlanMultipliers");
                int[] actualRepetitions = (int[])Call(control, "GetPressurePlanRepetitions");
                double[] actualRms = (double[])Call(control, "GetPressurePlanVoltagesKv");
                for (int index = 0; index < Multipliers.Length; index++)
                {
                    Near(Multipliers[index], actualMultipliers[index], "Saved multiplier " + index);
                    Check(Repetitions[index] == actualRepetitions[index], "Saved count " + index);
                    Near(Multipliers[index] * 8.7, actualRms[index], "Calculated RMS " + index);
                    for (int column = 0; column < 2; column++)
                        Check(Convert.ToString(plan.Rows[index].Cells[column].Value, CultureInfo.InvariantCulture) ==
                            Convert.ToString(editorGrid.Rows[index].Cells[column + 1].Value, CultureInfo.InvariantCulture), "Editor/pressure cell corresponds row " + index + " column " + column);
                    // The compact pressure table intentionally uses one decimal;
                    // the editor retains extra decimals. Verify the same RMS
                    // value before rounding, not identical display precision.
                    Near(actualRms[index], Convert.ToDouble(editorGrid.Rows[index].Cells[3].Value, CultureInfo.InvariantCulture), "Editor RMS value " + index);
                    Check(Convert.ToString(plan.Rows[index].Cells[2].Value, CultureInfo.InvariantCulture) == actualRms[index].ToString("0.0", CultureInfo.InvariantCulture), "Compact RMS display " + index);
                    Call(control, "SetSelectedPlanStep", index);
                    Near(actualRms[index] * Math.Sqrt(2), (double)Get(control, "CalculatedTargetPeakVoltageKv"), "Exactly one RMS-to-peak conversion " + index);
                    Check((int)Get(control, "CurrentPlanRepetitionTotal") == Repetitions[index], "Selected row count " + index);
                    Check(!(bool)Get(control, "IsHighVoltageReady"), "Plan selection cannot bypass physical P0.4 " + index);
                    if (Multipliers[index] > 0)
                        Check(!((Control)Field(control, "pressureActionButton")).Enabled, "Nonzero plan disabled without P0.4 " + index);
                }
                Check((string)Get(control, "PlanVoltageDisplayMode") == "RMS" && plan.Columns[2].HeaderText.Contains("有效值"), "Localized table labels project calculated RMS");
                Check(((Control)Field(control, "pressureActionButton")).AccessibleDescription.Contains("峰值"), "Compact action retains target peak explanation in accessibility text");
                Check((long)Get(control, "PressureRequestSequence") == 0, "No pressure action published during project/row inspection");
                Call(control, "SetSelectedPlanStep", 1);
                RenderPressurePlan(control, Path.Combine(output, "pressure-plan-1065x416.png"), new Size(1065, 416));
                RenderPressurePlan(control, Path.Combine(output, "pressure-plan-1065x650.png"), new Size(1065, 650));
                Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(project, "ITEM.in"))), "Loading and selecting plan leaves original ITEM bytes unchanged");
                Call(control, "SetPressureProgramMode", "CUSTOM");
                Check((bool)Call(control, "LoadProjectPlan", project) && (string)Get(control, "PressureProgramModeCode") == "CUSTOM", "Same project reload preserves custom/plan choice");
                foreach (Encoding encoding in new[] { Encoding.GetEncoding(936), Encoding.Unicode, Encoding.BigEndianUnicode })
                    Check((bool)Call(control, "LoadProjectPlan", MakeProject(output, "encoding-" + encoding.CodePage, content, encoding)), "Read-only legacy/BOM encoding " + encoding.CodePage);
                ValidateHostCorrespondence(control, project, modules, output, content);
                string[] invalid = {
                    "", "相电压 = 8.7kV", "相电压 = 8.7kV\n加压倍数 = 0,1\n加压次数 = 1",
                    "相电压 = 8.7kV\n加压倍数 = 0,1\n加压次数 = 1,2,3",
                    "相电压 = 8.7kV\n加压倍数 = 0,bad,1\n加压次数 = 1,2,3",
                    "相电压 = 8.7kV\n加压倍数 = 0,,1\n加压次数 = 1,2",
                    "相电压 = 8.7kV\n加压倍数 = 0,-1,1\n加压次数 = 1,2,3",
                    "相电压 = 8.7kV\n加压倍数 = 0,NaN,1\n加压次数 = 1,2,3",
                    "相电压 = 8.7kV\n加压倍数 = 0,1\n加压次数 = 1,0",
                    "相电压 = 8.7kV\n加压倍数 = 0,1\n加压次数 = 1,1.5",
                    "相电压 = 8.7kV\n加压倍数 = 1e308\n加压次数 = 1",
                    "相电压 = 8.7kV\n加压计划 = 1,2,99",
                    "相电压 = 8.7kV\n加压倍数 = \"\"\n加压次数 = 1"
                };
                for (int index = 0; index < invalid.Length; index++)
                {
                    Check((bool)Call(control, "LoadProjectPlan", project), "Seed valid plan before invalid fixture " + index);
                    Check(!(bool)Call(control, "LoadProjectPlan", MakeProject(output, "invalid-" + index, invalid[index], Encoding.UTF8)), "Invalid plan explicitly fails " + index);
                    AssertCleared(control, "bad plan " + index);
                }
                Check((bool)Call(control, "LoadProjectPlan", project), "Seed before no-project context");
                Check(!(bool)Call(control, "LoadProjectPlan", ""), "No project fails explicitly");
                AssertCleared(control, "no project");
                string explicitProject = MakeProject(output, "legacy-rows", "相电压 = 8.7kV\n加压步骤 = 0,2,0\n加压步骤 = 1,3,8.7\n加压步骤 = 0,1,0", Encoding.UTF8);
                Check((bool)Call(control, "LoadProjectPlan", explicitProject), "Legacy explicit zero/descending rows retained");
                Check(((double[])Call(control, "GetPressurePlanMultipliers")).SequenceEqual(new double[] { 0, 1, 0 }), "Legacy explicit order preserved");
                Check((long)Get(control, "PressureRequestSequence") == 0 && !(bool)Get(control, "IsBusy"), "All regression checks finish idle with zero pressure commands");
            }
            using (Control overflow = (Control)Activator.CreateInstance(pressure.GetType("HVX.Controls.PressureTestControl", true)))
            {
                Set(overflow, "FrequencyReminderEnabled", false);
                overflow.GetType().GetMethod("SetPressurePlan", new[] { typeof(double[]), typeof(int[]), typeof(double[]) }).Invoke(overflow, new object[] { new double[] { 2 }, new int[] { 1 }, new double[] { 20 } });
                Set(overflow, "BaseU0VoltageKv", Double.MaxValue); // Legal finite input; multiplication overflows internally.
                Call(overflow, "SetPressureProgramMode", "PLAN");
                Check(Double.IsInfinity(((double[])Call(overflow, "GetPressurePlanVoltagesKv"))[0]) && Double.IsInfinity((double)Get(overflow, "CalculatedTargetPeakVoltageKv")), "Overflow remains internally invalid; display never replaces it with zero/default U0");
                var overflowGrid = (DataGridView)Field(overflow, "pressurePlanGrid");
                Check(Convert.ToString(overflowGrid.Rows[0].Cells[2].Value) == "—", "Real plan voltage cell shows unknown rather than Infinity or fake zero");
                foreach (int language in new[] { 0, 1, 18 })
                {
                    SetLanguage(overflow, language);
                    Call(overflow, "UpdateCustomVoltageUi");
                    AssertUnknownTargetCaptions(overflow, "overflow/" + language, language);
                }
                Call(overflow, "ReportP04Status", true, true, DateTime.UtcNow); // Explicit synthetic status, no producer/driver.
                Call(overflow, "RequestPressure");
                Check((long)Get(overflow, "PressureRequestSequence") == 0 && !(bool)Get(overflow, "IsBusy"), "Overflow target cannot bypass invalid-voltage gate even with synthetic high P0.4");
            }
            Console.WriteLine("PRESSURE_PROJECT_PLAN_CORRESPONDENCE_PASS=" + checks + "; HARDWARE_COMMANDS=0; SDK_LOADS=0; DEVICE_ENUMERATIONS=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void ValidateHostCorrespondence(Control control, string project, string modules, string output, string content)
    {
        var settings = new AppSettings { LanguageCode = "zh", MaximumVoltageKv = 100 };
        settings.Hardware.Enabled = false;
        var state = new ApplicationState();
        string message;
        Check(state.TryLoadProject(project, out message), "Synthetic project passes host read-only validation");
        using (var host = new ModuleHost(settings, state, new Diagnostics(Path.Combine(output, "diagnostics")), modules))
        {
            activeHostSettings=settings;
            ModuleDescriptor descriptor = ModuleHost.Descriptors.Single(candidate => candidate.Id == ModuleHost.PressureTest);
            Call(host, "ApplyContext", descriptor, control, true);
            Check(((double[])Call(control, "GetPressurePlanMultipliers")).SequenceEqual(Multipliers), "Host applies editor plan rather than only ProjectPath");
            Call(control, "SetPressureProgramMode", "CUSTOM");
            Call(control, "SetSelectedPlanStep", 4);
            Call(host, "ApplyContext", descriptor, control, true);
            Check((string)Get(control, "PressureProgramModeCode") == "CUSTOM" && (int)Get(control, "SelectedPlanStepIndex") == 4, "Unchanged forced context preserves mode and row choice");
            string item = Path.Combine(project, "ITEM.in"), previousSignature = ModuleHost.PressureProjectPlanSignature(project);
            File.WriteAllText(item, content.Replace("8.7kV", "6.0kV"), new UTF8Encoding(false));
            byte[] edited = File.ReadAllBytes(item);
            Check(previousSignature != ModuleHost.PressureProjectPlanSignature(project), "Same-path saved ITEM changes signature");
            Call(host, "ApplyContext", descriptor, control, false);
            Near(6, (double)Get(control, "BaseU0VoltageKv"), "Same-path editor save reaches pressure module");
            Near(.6 * 6, ((double[])Call(control, "GetPressurePlanVoltagesKv"))[2], "Same-path calculated voltage updates");
            Check((string)Get(control, "PressureProgramModeCode") == "CUSTOM", "Saved same-project plan preserves user custom mode");
            Check(edited.SequenceEqual(File.ReadAllBytes(item)), "Host does not rewrite edited ITEM");
            File.WriteAllText(item, "相电压=6kV\n加压倍数=0,1\n加压次数=1", Encoding.UTF8);
            Call(host, "ApplyContext", descriptor, control, false);
            AssertCleared(control, "host bad-plan context");
            File.WriteAllText(item, content, new UTF8Encoding(false));
            Call(host, "ApplyContext", descriptor, control, false);
            Check((int)Get(control, "SelectedPlanStepIndex") == 0 && (string)Get(control, "PressureProgramModeCode") == "PLAN", "Repaired ITEM reloads saved first row");
            state.ClearProject();
            Call(host, "ApplyContext", descriptor, control, false);
            AssertCleared(control, "host no-project context");
        }
        activeHostSettings=null;
    }

    static string MakeProject(string output, string name, string item, Encoding encoding)
    {
        string project = Path.Combine(output, name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "ITEM.in"), item, encoding);
        File.WriteAllText(Path.Combine(project, "CABLE.in"), "0 = \"ALL,200000,0,0\"\n1 = \"始端,0\"\n1 = \"终端,200\"", Encoding.UTF8);
        return project;
    }

    static void RenderPressurePlan(Control control, string path, Size size)
    {
        using (var form = new Form { ShowInTaskbar = false, ClientSize = size, AutoScaleMode = AutoScaleMode.None })
        {
            control.Dock = DockStyle.Fill;
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Controls.Remove(control);
            form.Close();
        }
    }

    static void AssertCleared(Control control, string label)
    {
        Check(((double[])Call(control, "GetPressurePlanMultipliers")).Length == 0 && ((DataGridView)Field(control, "pressurePlanGrid")).Rows.Count == 0, "No old plan remains after " + label);
        Check((int)Get(control, "SelectedPlanStepIndex") == -1 && (string)Get(control, "ProjectPath") == "", "No old project identity after " + label);
        Check(Double.IsNaN((double)Get(control, "BaseU0VoltageKv")), "No old phase voltage after " + label);
        foreach (int language in new[] { 0, 1, 18 })
        {
            SetLanguage(control, language);
            string caption = ((Control)Field(control, "customModeHintLabel")).Text;
            Check(caption.IndexOf("NaN", StringComparison.OrdinalIgnoreCase) < 0 && caption.IndexOf("Infinity", StringComparison.OrdinalIgnoreCase) < 0, "Real target-hint caption has no numeric invalid token after " + label + "/" + language);
            Check(UnknownHint(caption,language), "Real target-hint caption explains unknown target after " + label + "/" + language+"; actual="+caption+"; pageLanguage="+Get(control,"LanguageIndex"));
            Check(Double.IsNaN((double)Get(control, "CalculatedTargetPeakVoltageKv")) && !((Control)Field(control, "pressureActionButton")).Enabled, "Unknown target remains internally invalid and blocked after " + label + "/" + language);
            AssertUnknownTargetCaptions(control, label + "/" + language, language);
        }
        SetLanguage(control, 0);
        Check(!String.IsNullOrWhiteSpace((string)Get(control, "LastError")), "Explicit failure message after " + label);
        Check(!(bool)Get(control, "IsHighVoltageReady") && (long)Get(control, "PressureRequestSequence") == 0, "P0.4 unchanged and no output after " + label);
    }
    static void AssertUnknownTargetCaptions(Control control, string label, int language)
    {
        // Refresh the actual button directly, independently of project-error
        // labels or surrounding layout hiding its idle voltage line.
        Call(control, "UpdatePressureUi");
        var button = (Control)Field(control, "pressureActionButton");
        Check(button.Text.IndexOf("NaN", StringComparison.OrdinalIgnoreCase) < 0 && button.Text.IndexOf("Infinity", StringComparison.OrdinalIgnoreCase) < 0, "Actual idle pressure caption contains no invalid numeric token " + label);
        Check(button.Text.EndsWith("—", StringComparison.Ordinal) || button.Text.Contains(Environment.NewLine + "— kV") || button.Text.Contains(Environment.NewLine + "—  ·"), "Actual idle pressure caption positively shows unknown target, not fake 0.0 kV " + label);
        string unknown = language == 1 ? "unknown (cannot calculate)" : language == 18 ? "未知（無法計算）" : "未知（无法计算）";
        Check(button.AccessibleDescription.Contains(unknown), "Actual idle pressure description explains unknown target " + label);
        var hint = (Control)Field(control, "customModeHintLabel");
        Check(UnknownHint(hint.Text,language) && hint.Text.IndexOf("NaN", StringComparison.OrdinalIgnoreCase) < 0 && hint.Text.IndexOf("Infinity", StringComparison.OrdinalIgnoreCase) < 0, "Actual custom hint explains unknown without invalid token " + label);
    }
    static bool UnknownHint(string text,int language)
    {
        // Host localization uses the concise approved English hint; the page
        // and accessibility description retain the longer explanation.
        return text.Contains(language==1?"unknown (cannot calculate)":language==18?"未知（無法計算）":"未知（无法计算）") ||
            language==1&&text=="Target peak unknown";
    }
}
