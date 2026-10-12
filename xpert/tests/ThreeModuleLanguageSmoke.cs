using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class ThreeModuleLanguageSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static readonly List<string> issues = new List<string>();
    const string Original = "客户电缆 后台记录 / 用户原文";
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, All).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Call(object owner, string name, params object[] args)
    { return owner.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner, args); }
    static object Static(Type type, string name, params object[] args)
    { return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(null, args); }
    static void Check(bool ok, string message) { checks++; if (!ok) issues.Add(message); }
    static void Pump() { for (int i = 0; i < 3; i++) Application.DoEvents(); }
    static void Wait(Func<bool> ready)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!ready() && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
        if (!ready()) throw new TimeoutException("Offline fixture did not finish loading.");
        Pump();
    }
    sealed class QaForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    }
    static Form Host(Control control)
    {
        var form = new QaForm { ClientSize = new Size(1256, 720), StartPosition = FormStartPosition.Manual, Location = new Point(10, 10), AutoScaleMode = AutoScaleMode.None };
        control.Dock = DockStyle.Fill; form.Controls.Add(control); form.Show(); Pump(); return form;
    }
    static void Shot(Form form, string path)
    { Pump(); using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path); } }
    static void Contains(Control c, string context)
    {
        Check(c.Parent != null && c.Parent.ClientRectangle.Contains(c.Bounds), context + " contained: " + c.Text + " " + c.Bounds);
    }
    static Assembly Module(string root, string name)
    { return Assembly.LoadFrom(Path.Combine(root, "modules", name, "Xiongxin.OWTS." + name + ".dll")); }
    static string Fixture(string output)
    {
        string root = Path.Combine(output, "fixture-客户电缆-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ITEM.in"), "[ITEM]\r\n电缆长度: = \"170\"\r\n始端地点: = \"" + Original + "\"\r\n终端地点: = \"客户终端\"\r\n备注: = \"" + Original + "\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "CABLE.in"), "0\tALL\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t170m\r\n", new UTF8Encoding(false));
        var text = new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");
        for (int n = 0; n < 8; n++) text.Append("Field").Append(n).Append("\t0\r\n");
        for (int n = 0; n < 1001; n++) text.Append((Pulse(n * .01, 1.05, .1, 1) + Pulse(n * .01, 3.05, .12, .4)).ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(Path.Combine(root, "ALL_5nC.cal"), text.ToString(), new UTF8Encoding(false)); return root;
    }
    static double Pulse(double x, double center, double half, double peak) { return peak * Math.Max(0, 1 - Math.Abs(x - center) / half); }
    static void Browser(string root, string output, string fixture)
    {
        Assembly module = Module(root, "DataBrowser");
        Type words = module.GetType("HVX.Controls.BrowserLanguages", true), itemWords = module.GetType("LabVIEWItemInformation.UiText", true);
        var vocabulary = (IDictionary)words.GetField("Words", All).GetValue(null);
        var itemVocabulary = (IDictionary)itemWords.GetField("Text", All).GetValue(null);
        for (int language = 0; language < 40; language++)
        {
            foreach (string key in vocabulary.Keys) Check(Static(words, "Text", language, "校准数据", key) != null, "Browser dictionary " + language + " " + key);
            foreach (string key in itemVocabulary.Keys) Check(!string.IsNullOrEmpty((string)Static(itemWords, "Get", key, language)), "Item dictionary " + language + " " + key);
        }
        Check((string)Static(words, "Text", 18, "保存校准", "Save calibration") == "保存校準", "Browser Traditional exact caption");
        using (Control browser = (Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectDataBrowserControl", true)))
        {
            Set(browser, "EmbeddedMode", true);
            using (Form host = Host(browser))
            {
                Check(((string[])Call(browser, "GetLanguageNames")).Length == 40, "Browser exposes 40 locales");
                Check((bool)Call(browser, "LoadProject", fixture), "Browser loads synthetic project");
                Check((bool)Call(browser, "SelectFile", Path.Combine(fixture, "ALL_5nC.cal")), "Browser selects calibration");
                Wait(() => !(bool)Get(browser, "IsBusy"));
                var item = (Control)Field(browser, "item");
                Check((bool)Call(item, "LoadItem", fixture, 0), "Metadata loads fixture");
                foreach (int width in new[] { 1065, 1600 })
                for (int language = 0; language < 40; language++)
                {
                    host.ClientSize = new Size(width, 720); Set(browser, "LanguageIndex", language); Pump();
                    Check((int)Get(browser, "LanguageIndex") == language, "Browser keeps language index " + language);
                    Check((string)Get(browser, "SelectedFilePath") == Path.Combine(fixture, "ALL_5nC.cal"), "Browser path exact " + language);
                    Check((string)Call(item, "GetFieldValue", "StartName") == Original, "Browser metadata original " + language);
                    Check((string)Call(item, "GetFieldValue", "Remarks") == Original, "Browser notes original " + language);
                    foreach (string name in new[] { "editButton", "saveButton", "cancelButton", "jointButton" })
                    {
                        Control button = (Control)Field(browser, name);
                        Check(button.Width >= TextRenderer.MeasureText(button.Text, button.Font).Width + 12, "Browser full button " + language + " " + name);
                        Contains(button, "Browser " + width + " language " + language);
                    }
                    var calibration = Field(browser, "calibration");
                    Check((string)Call(calibration, "Text", "时间", "Time") == (string)Static(words, "Text", language, "时间", "Time"), "Browser embedded chart translator " + language);
                    if (language == 18 && width == 1065) Shot(host, Path.Combine(output, "browser-traditional-144dpi.png"));
                }
                Set(browser, "LanguageIndex", 18); Check((string)Call(item, "Caption", "A1") == "A 絕緣電阻 [MΩ] 1", "Traditional A phase preserved in tooltip");
                Check((bool)Call(browser, "SetLanguageCode", "zh-Hant-TW"), "Browser accepts Traditional alias");
            }
        }
    }
    static void Management(string root, string output, string fixture)
    {
        Assembly module = Module(root, "ProjectManagement"); Type words = module.GetType("HVX.Controls.ProjectManagementLanguages", true);
        var vocabulary = (IDictionary)words.GetField("Texts", All).GetValue(null);
        for (int language = 0; language < 40; language++)
            foreach (string key in vocabulary.Keys) Check(Static(words, "Text", language, key, "项目管理") != null, "Management dictionary " + language + " " + key);
        using (Control control = (Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectManagementControl", true)))
        {
            Set(control, "EmbeddedMode", true); Set(control, "ModelName", Original);
            using (Form host = Host(control))
            {
                Check((bool)Call(control, "LoadProjectPath", fixture), "Management loads synthetic project");
                var search = (TextBox)Field(control, "searchBox"); search.Text = "";
                string projectName = Convert.ToString(((DataGridView)Field(control, "grid")).Rows[0].Cells["ProjectName"].Value);
                Check(projectName.Contains(Original), "Management shows original customer name");
                foreach (int width in new[] { 720, 1065, 1600 })
                for (int language = 0; language < 40; language++)
                {
                    host.ClientSize = new Size(width, 720); Set(control, "LanguageIndex", language); Pump();
                    Check((int)Get(control, "LanguageIndex") == language, "Management language " + language);
                    Check((string)Get(control, "SelectedProjectPath") == fixture, "Management selected path exact " + language);
                    Check((string)Get(control, "ModelName") == Original, "Management model name original " + language);
                    Check(Convert.ToString(((DataGridView)Field(control, "grid")).Rows[0].Cells["ProjectName"].Value) == projectName, "Management name not translated " + language);
                    foreach (string name in new[] { "loadButton", "refreshButton", "importButton", "exportDataButton", "exportReportsButton", "mergeButton", "deleteButton" })
                    {
                        Control button = (Control)Field(control, name); Contains(button, "Management " + width + " language " + language);
                        Size measured = TextRenderer.MeasureText(button.Text, button.Font, new Size(button.Width - 20, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                        Check(measured.Width <= button.Width - 20 && measured.Height <= button.Height - 8, "Management full button " + width + " " + language + " " + name);
                    }
                    var flow = (Control)Field(control, "actionFlow"); Contains(flow.Parent, "Management action host " + width + " " + language);
                    {
                        foreach (Control child in ((Control)Field(control, "filterLayout")).Controls) Contains(child, "Management filter " + width + " " + language);
                        Check(search.Width >= 120, "Management search usable " + width + " " + language + " width " + search.Width);
                        var grid = (DataGridView)Field(control, "grid");
                        var insulation = grid.Columns["InsulationStatus"];
                        Check(insulation != null && insulation.Visible, "Management insulation column present " + width + " " + language);
                        if (insulation != null)
                        {
                            Check(insulation.DefaultCellStyle.WrapMode == DataGridViewTriState.False, "Management status is single line " + width + " " + language);
                            if (grid.Columns.Cast<DataGridViewColumn>().Sum(c => c.Width) > grid.ClientSize.Width)
                            {
                                Check(grid.ScrollBars == ScrollBars.Both || grid.ScrollBars == ScrollBars.Horizontal, "Management narrow grid permits horizontal access " + width + " " + language);
                                grid.FirstDisplayedScrollingColumnIndex = insulation.Index; Pump();
                                Rectangle cell = grid.GetCellDisplayRectangle(insulation.Index, 0, true);
                                Check(cell.Width >= insulation.Width - 2, "Management status reachable without clipping " + width + " " + language);
                                grid.HorizontalScrollingOffset = 0;
                            }
                        }
                    }
                    if ((width == 1065 && language == 18) || (width == 720 && language == 5)) Shot(host, Path.Combine(output, "management-" + language + "-" + width + "-144dpi.png"));
                }
                Call(control, "SetLanguageCode", "zh-Hant-TW");
                Check((int)Get(control, "LanguageIndex") == 18, "Management accepts Traditional alias");
            }
        }
    }
    static void Calibration(string root, string output)
    {
        Assembly module = Module(root, "CalibrationTest"); Type type = module.GetType("HVX.Controls.CalibrationProgramControl", true);
        using (Control control = (Control)Activator.CreateInstance(type))
        {
            Set(control, "EmbeddedMode", true); Set(control, "SelectedPhaseIndex", 0); Set(control, "SelectedLevelCode", "TDR");
            Call(control, "SetPhaseParameters", 0, 170d, 170d, false, false);
            var samples = Enumerable.Range(0, 1001).Select(i => Pulse(i * .01, 1.05, .1, 1) + Pulse(i * .01, 3.05, .12, .4)).ToArray();
            Call(control, "SetPhaseWaveform", 0, "TDR", samples, 0d, 1e-8);
            Call(control, "SetPhaseMarkers", 0, "TDR", 1e-6, 3e-6, 3e-6, 170d);
            Call(control, "AddPhaseJoint", 0, Original, 80d);
            using (Form host = Host(control))
            {
                for (int language = 0; language < 40; language++)
                {
                    Set(control, "LanguageIndex", language); Pump();
                    Check((int)Get(control, "LanguageIndex") == language, "Calibration accepts language " + language);
                    using (var image = new Bitmap(1256, 720)) control.DrawToBitmap(image, new Rectangle(0, 0, 1256, 720));
                    Check((int)type.GetField("drawingLanguage", All).GetValue(null) == 0, "Drawing language thread state restored " + language);
                    var phase = Call(control, "GetPhaseStateNoLock", 0);
                    var raw = ((IDictionary)Field(phase, "Waveforms"))["TDR"];
                    Check(samples.SequenceEqual((double[])Field(raw, "Samples")), "Raw waveform unchanged " + language);
                    var markers = ((IDictionary)Field(phase, "Markers"))["TDR"];
                    Check((double)Field(markers, "StartTimeSeconds") == 1e-6 && (double)Field(markers, "DistanceMarkerSeconds") == 3e-6, "Timing cursor data unchanged " + language);
                    var joint = ((Array)Call(control, "GetPhaseJoints", 0)).GetValue(0);
                    Check((string)Get(joint, "Name") == Original, "User joint name unchanged " + language);
                    if (language == 18)
                    {
                        Call(control, "ClearHoverMeasurement");
                        Shot(host, Path.Combine(output, "calibration-traditional-144dpi.png"));
                    }
                }
                type.GetField("drawingLanguage", All).SetValue(null, 18);
                try
                {
                    foreach (string[] pair in new[] { new[] { "带宽增强", "帶寬增強" }, new[] { "相电缆长度", "相電纜長度" }, new[] { "时间 / 秒", "時間 / 秒" }, new[] { "当前量程：全部档位", "當前量程：全部檔位" }, new[] { "实时反射信号与定位标记", "實時反射信號與定位標記" } })
                        Check((string)Static(type, "BuiltIn", pair[0]) == pair[1], "Calibration precise Traditional " + pair[0]);
                }
                finally { type.GetField("drawingLanguage", All).SetValue(null, 0); }
            }
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Pass release root and QA output directory.");
            string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string fixture = Fixture(output); var before = Directory.GetFiles(fixture).ToDictionary(p => p, File.ReadAllBytes);
            Browser(root, output, fixture); Management(root, output, fixture); Calibration(root, output);
            foreach (var entry in before) Check(entry.Value.SequenceEqual(File.ReadAllBytes(entry.Key)), "No fixture project data changed by display/language: " + Path.GetFileName(entry.Key));
            File.WriteAllLines(Path.Combine(output, "issues.txt"), issues);
            Console.WriteLine("THREE_MODULE_LANGUAGE_CHECKS=" + checks + "; ISSUES=" + issues.Count + "; HARDWARE_COMMANDS=0; NETWORK_REQUESTS=0");
            foreach (string issue in issues.Take(35)) Console.Error.WriteLine(issue);
            return issues.Count == 0 ? 0 : 1;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
