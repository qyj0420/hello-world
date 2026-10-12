using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class CreationSaveValidationSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Control control;
    static Type type;
    static int checks, savedEvents, exitEvents;
    const string Original = "客户电缆 后台记录 / 用户原文";
    static object Get(string name) { return type.GetProperty(name, All).GetValue(control, null); }
    static void Set(string name, object value) { type.GetProperty(name, All).SetValue(control, value, null); }
    static object Field(string name) { return type.GetField(name, All).GetValue(control); }
    static object Call(string name, params object[] args) { return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(control, args); }
    static void Check(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
    static void Pump() { for (int n = 0; n < 3; n++) Application.DoEvents(); }
    sealed class QaForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    }
    static void Valid()
    {
        Call("ClearProject"); Set("CableMode", "三相单芯");
        Set("StartName", Original); Set("EndName", "客户终端"); Set("PhaseVoltage", "8.7kV");
        Call("SetPhaseCableLengths", 170d, 180d, 190d); Set("Notes", Original); Set("Organization", Original);
        Set("InsulationA1", "未测 / customer note"); Set("InsulationA2", "1000");
        Set("PressureMode", "新电缆"); Set("RelativeHumidity", 45d);
        Call("SetOptionalSectionExpanded", "InsulationAndNotes", true); Pump();
    }
    static void Fail(string output, string field, string focusName, int expectedCount)
    {
        string noWriteRoot = Path.Combine(output, "must-not-create-" + Guid.NewGuid().ToString("N"));
        long saved = (long)Get("ProjectSavedSequence"), operation = (long)Get("LastOperationSequence"), failed = (long)Get("SaveValidationFailureSequence");
        int eventCount = savedEvents;
        string start = (string)Get("StartName"), end = (string)Get("EndName"), voltage = (string)Get("PhaseVoltage");
        string notes = (string)Get("Notes"), organization = (string)Get("Organization"), insulation = (string)Get("InsulationA1");
        Check(!(bool)Call("SaveProjectToRoot", noWriteRoot), "Invalid save is rejected: " + field);
        Pump();
        Check(!Directory.Exists(noWriteRoot), "Validation does not create output directories");
        Check((long)Get("ProjectSavedSequence") == saved && savedEvents == eventCount && exitEvents == 0, "Failure does not signal save or navigation");
        Check((long)Get("LastOperationSequence") == operation + 1, "One operation result per save attempt");
        Check((long)Get("SaveValidationFailureSequence") == failed + 1, "One validation rejection per attempt");
        Check(!(bool)Get("LastOperationSucceeded") && (string)Get("LastOperation") == "SaveProject", "Failure is never recorded as success");
        Check(!(bool)Get("IsBusy") && (bool)Get("HasValidationErrors"), "Failed save returns to editable validation state");
        Check((string)Get("FirstValidationErrorField") == field, "Correct first field: " + field);
        var fields = (string[])Get("ValidationErrorFields"); Check(fields.Length == expectedCount, "Every invalid required field is listed separately");
        string message = (string)Get("LastValidationDialogMessage");
        Check(message.Contains("1. ") && message.Contains(expectedCount + ". "), "Numbered requirements in dialog");
        Check(message == (string)Get("LastError"), "Audit error and shown message use the same validation result");
        Check((string)Get("StartName") == start && (string)Get("EndName") == end && (string)Get("PhaseVoltage") == voltage, "Required values are retained exactly");
        Check((string)Get("Notes") == notes && (string)Get("Organization") == organization && (string)Get("InsulationA1") == insulation,
            "Other user entries are retained exactly: " + Get("Notes") + " / " + Get("Organization") + " / " + Get("InsulationA1"));
        if (focusName != null)
        {
            Control target = (Control)Field(focusName);
            Check(target.Visible && target.CanSelect, "First field becomes visible and selectable");
            Check(target.ContainsFocus || ((ContainerControl)control).ActiveControl == target, "First invalid field selected: " + focusName);
        }
    }
    static void DraftReject(string member, object value, string expectedField)
    {
        object draft = Call("BuildDraft", true); draft.GetType().GetField(member, All).SetValue(draft, value);
        var issues = (IEnumerable)Call("ValidateDraftDetailed", draft);
        Check(issues.Cast<object>().Any(i => (string)i.GetType().GetField("FieldCode", All).GetValue(i) == expectedField), "Draft rule catches " + member + "=" + value);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass release root and QA output directory.");
            string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            type = assembly.GetType("HVX.Controls.ProjectCreationControl", true);
            using (control = (Control)Activator.CreateInstance(type))
            using (var host = new QaForm { ClientSize = new Size(1256, 620), AutoScaleMode = AutoScaleMode.None })
            {
                Set("EmbeddedMode", true); Set("ShowDialogMessages", false); Set("OnlineWeatherEnabled", false); Set("AutoRefreshWeatherOnHardwareSample", false);
                Set("SettingsPath", Path.Combine(output, "qa-settings.ini")); Set("DataRootDirectory", Path.Combine(output, "projects"));
                type.GetEvent("ProjectSaved").AddEventHandler(control, new EventHandler((s, e) => savedEvents++));
                type.GetEvent("ExitRequested").AddEventHandler(control, new EventHandler((s, e) => exitEvents++));
                host.Controls.Add(control); control.Dock = DockStyle.Fill; host.Show(); Pump();
                foreach (int language in new[] { 0, 1, 18 })
                {
                    Set("LanguageIndex", language); Valid();
                    Set("StartName", " "); Fail(output, "StartName", "startNameText", 1);
                    Valid(); Set("EndName", ""); Fail(output, "EndName", "endNameText", 1);
                    Valid(); ((ComboBox)Field("phaseVoltageCombo")).SelectedIndex = -1; ((ComboBox)Field("phaseVoltageCombo")).Text = "";
                    Fail(output, "PhaseVoltage", "phaseVoltageCombo", 1);
                    foreach (string invalid in new[] { "abc", "0kV", "-1", "NaN", "Infinity" })
                    { Valid(); Set("PhaseVoltage", invalid); Fail(output, "PhaseVoltage", "phaseVoltageCombo", 1); }
                    foreach (int phase in new[] { 0, 1, 2 })
                    {
                        Valid(); ((NumericUpDown)Field(new[] { "lengthANumber", "lengthBNumber", "lengthCNumber" }[phase])).Value = 0;
                        Fail(output, new[] { "CableLengthA", "CableLengthB", "CableLengthC" }[phase], new[] { "lengthANumber", "lengthBNumber", "lengthCNumber" }[phase], 1);
                    }
                    Valid(); Set("StartName", ""); Set("EndName", ""); ((ComboBox)Field("phaseVoltageCombo")).SelectedIndex = -1; ((ComboBox)Field("phaseVoltageCombo")).Text = "";
                    Call("SetPhaseCableLengths", 0d, 0d, 0d); Fail(output, "StartName", "startNameText", 6);
                    string message = (string)Get("LastValidationDialogMessage"), title = (string)Get("LastValidationDialogTitle");
                    Check(title == (language == 0 ? "项目尚未保存" : language == 18 ? "項目尚未保存" : "Project not saved"), "Localized dialog title");
                    Check(message.Contains(language == 1 ? "L1 length" : "A相長度".Replace('長', language == 0 ? '长' : '長')), "Explicit phase A requirement");
                    Check(message.Contains(language == 1 ? "L2 length" : "B相長度".Replace('長', language == 0 ? '长' : '長')), "Explicit phase B requirement");
                    Check(message.Contains(language == 1 ? "L3 length" : "C相長度".Replace('長', language == 0 ? '长' : '長')), "Explicit phase C requirement");
                    if (language == 18)
                        Check(message.Contains("修改後") && message.Contains("提示後") && message.Contains("大於 0"), "Traditional contextual wording is exact");
                    File.WriteAllText(Path.Combine(output, "validation-message-" + language + ".txt"), title + "\r\n" + message);
                    Valid(); ((IList)Field("currentPressureSteps")).Clear(); Fail(output, "PressurePlan", "pressureEditButton", 1);
                    Check((string)Get("ExpandedOptionalSectionCode") == "TestConfiguration", "Invalid voltage plan opens its page");
                    Valid(); var joints = (IList)Field("joints"); Type jt = assembly.GetType("HVX.Controls.JointData", true); object joint = Activator.CreateInstance(jt, true);
                    jt.GetField("Name", All).SetValue(joint, Original); jt.GetField("Position", All).SetValue(joint, 999d); joints.Add(joint);
                    Fail(output, "CableStructure", "cableGrid", 1);
                    Check((string)Get("ExpandedOptionalSectionCode") == "JointsAndBody", "Invalid joint opens its page");
                    Check(((string)Get("LastValidationDialogMessage")).Contains(Original), "Joint name is not translated in the dialog");
                    Valid(); long saved = (long)Get("ProjectSavedSequence"); int events = savedEvents;
                    Check((bool)Call("SaveProject"), "Corrected valid project saves");
                    Check((long)Get("ProjectSavedSequence") == saved + 1 && savedEvents == events + 1, "Successful save signals host exactly once");
                    Check((bool)Get("LastOperationSucceeded") && !(bool)Get("HasValidationErrors") && (string)Get("LastError") == "", "Success clears validation failure state");
                    Check((string)Get("LastValidationDialogMessage") == "" && (string)Get("FirstValidationErrorField") == "", "Success clears old modal notice");
                    Check((string)Get("Notes") == Original && (string)Get("StartName") == Original, "Success preserves original input");
                }
                Valid(); foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1d, 0d }) DraftReject("LengthA", value, "CableLengthA");
                foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1d, 101d }) DraftReject("Humidity", value, "RelativeHumidity");
                Set("CableMode", "单相"); Call("SetPhaseCableLengths", 170d, 0d, 0d); Check(((string[])Call("ValidateProject")).Length == 0, "Single-phase only requires active length");
                Set("CableMode", "电容局放"); Check(((string[])Call("ValidateProject")).Length == 0, "Capacitor does not require cable length");
                Valid(); string blocked = Path.Combine(output, "not-a-directory"); File.WriteAllText(blocked, "QA");
                long previous = (long)Get("ProjectSavedSequence"); Check(!(bool)Call("SaveProjectToRoot", blocked), "Storage failure is not success");
                Check((long)Get("ProjectSavedSequence") == previous && !(bool)Get("LastOperationSucceeded"), "Storage failure does not trigger navigation");
                Check((string)Get("LastValidationDialogMessage") == "", "Storage error is not misreported as missing required fields");
            }
            Console.WriteLine("CREATION_SAVE_VALIDATION_PASS=" + checks + "; HARDWARE_COMMANDS=0; NETWORK_REQUESTS=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
