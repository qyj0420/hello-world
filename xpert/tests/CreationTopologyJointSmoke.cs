using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationTopologyJointSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Control module; static Type type; static int checks;
    static object Field(string name) { return type.GetField(name, All).GetValue(module); }
    static object Get(string name) { return type.GetProperty(name, All).GetValue(module, null); }
    static void Set(string name, object value) { type.GetProperty(name, All).SetValue(module, value, null); }
    static object Call(string name, params object[] args) { return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(module, args); }
    static object Member(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
    static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    static string Block(string phase, string material, double length)
    {
        return "0\t" + phase + "\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t" + material + "\t\r\n" +
            "1\t接头 1\t户内冷缩\t80m\r\n2\t本体\t" + material + "\t\r\n" +
            "1\t接头 2\t户内冷缩\t264.7m\r\n2\t本体\t" + material + "\t\r\n" +
            "1\t终端\t户内冷缩\t" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m\r\n";
    }
    static string Fixture(string root, string name, string mode, string cable)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n始端地点: = \"QA start\"\r\n终端地点: = \"QA end\"\r\n相电压: = \"8.7kV\"\r\n电缆芯数: = \"" + mode + "\"\r\nA相电缆长度: = \"414.8\"\r\nB相电缆长度: = \"500\"\r\nC相电缆长度: = \"600\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), cable, new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(path, "original.cal"), new byte[] { 4, 3, 2, 1 }); return path;
    }
    static object[] Markers(string screenshot)
    {
        var canvas = (Control)Field("topology");
        using (var bitmap = new Bitmap(canvas.Width, canvas.Height)) { canvas.DrawToBitmap(bitmap, canvas.ClientRectangle); if (screenshot != null) bitmap.Save(screenshot); }
        return ((IEnumerable)canvas.GetType().GetProperty("RenderedJointMarkers", All).GetValue(canvas, null)).Cast<object>().ToArray();
    }
    static void CheckGeometry(object[] markers)
    {
        var canvas = (Control)Field("topology");
        foreach (var marker in markers)
        {
            Check(canvas.ClientRectangle.Contains((Rectangle)Member(marker, "Bounds")), "joint glyph within canvas");
            Rectangle label = (Rectangle)Member(marker, "LabelBounds");
            if (!label.IsEmpty) Check(canvas.ClientRectangle.Contains(label), "joint label within canvas");
        }
        var labels = markers.Select(m => (Rectangle)Member(m, "LabelBounds")).Where(r => !r.IsEmpty).ToArray();
        for (int i = 0; i < labels.Length; i++) for (int j = i + 1; j < labels.Length; j++) Check(!labels[i].IntersectsWith(labels[j]), "joint labels never overlap");
    }
    static void AddThroughUi(decimal position, bool expectSuccess)
    {
        ((NumericUpDown)Field("jointPositionNumber")).Value = position;
        ((Button)Field("addJointButton")).PerformClick(); Pump();
        Check((bool)Get("LastOperationSucceeded") == expectSuccess, "actual append uses current page fields and validates endpoints");
        Check(!Application.OpenForms.Cast<Form>().Any(f => f.Name == "CableJointDialog"), "main editor does not request duplicate entry in another dialog");
    }
    sealed class QaForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); string root = Path.Combine(Path.GetFullPath(args[1]), "topology-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            type = assembly.GetType("HVX.Controls.ProjectCreationControl", true);
            using (module = (Control)Activator.CreateInstance(type)) using (var host = new QaForm { AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(1500, 900), ShowInTaskbar = false })
            {
                Set("EmbeddedMode", true); Set("ShowDialogMessages", false); Set("OnlineWeatherEnabled", false); Set("AutoRefreshWeatherOnHardwareSample", false);
                Set("SettingsPath", Path.Combine(root, "settings.ini")); Set("DataRootDirectory", root); Set("LanguageIndex", 0);
                module.Dock = DockStyle.Fill; host.Controls.Add(module); host.Show(); Pump();
                string path = Fixture(root, "repeated-a", "一体三芯", Block("A相", "材料一", 414.8) + Block("A相", "材料二", 414.8) + Block("A相", "材料三", 414.8));
                byte[] original = File.ReadAllBytes(Path.Combine(path, "CABLE.in")), originalItem = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                Check((bool)Call("LoadProject", path), "load exact duplicate-A scenario"); Call("SetOptionalSectionExpanded", "JointsAndBody", true); Pump();
                int broadcasts = 0; EventHandler changed = delegate { broadcasts++; };
                type.GetEvent("CableStructureChanged", All).AddEventHandler(module, changed);
                var grid = (DataGridView)Field("cableGrid"); Check(grid.Rows.Count == 2, "shared geometry is shown once while source records remain intact");
                Check(((NumericUpDown)Field("jointPositionNumber")).Minimum == 0 && ((NumericUpDown)Field("jointPositionNumber")).Maximum == 414.8m, "position input bounded by actual shared cable length");
                object[] markers = Markers(Path.Combine(root, "duplicate-a-joints.png"));
                Check(markers.Length == 2, "shared cable renders two positions not six stacked sleeves");
                Check(markers.All(m => ((Array)Member(m, "Sources")).Length == 3), "projection preserves three records at each joint");
                Check(markers.Any(m => (double)Member(m, "Position") == 80d) && markers.Any(m => (double)Member(m, "Position") == 264.7d), "positions follow source records");
                CheckGeometry(markers);
                grid.ClearSelection(); grid.Rows[1].Selected = true; Pump(); markers = Markers(Path.Combine(root, "selected-joint.png"));
                Check(markers.Count(m => (bool)Member(m, "Selected")) == 1 && (double)Member(markers.Single(m => (bool)Member(m, "Selected")), "Position") == 264.7, "table selection highlights matching shared joint");
                Check(!((Rectangle)Member(markers.Single(m => (bool)Member(m, "Selected")), "LabelBounds")).IsEmpty, "selected position remains labelled");
                Check(File.ReadAllBytes(Path.Combine(path, "CABLE.in")).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(path, "ITEM.in")).SequenceEqual(originalItem), "drawing and selection never rewrite source");
                foreach (decimal endpoint in new[] { 0m, 414.8m }) { AddThroughUi(endpoint, false); Check(File.ReadAllBytes(Path.Combine(path, "CABLE.in")).SequenceEqual(original), "endpoint rejection preserves cable bytes"); }
                AddThroughUi(300m, true); markers = Markers(Path.Combine(root, "after-add.png"));
                Check(broadcasts == 1, "successful UI append broadcasts one structure change");
                Check(grid.Rows.Count == 3 && markers.Length == 3 && markers.Any(m => (double)Member(m, "Position") == 300), "UI commit refreshes table and shared cable immediately");
                Check(!(bool)Get("CanEditCableGeometry") && !((Control)Field("lengthANumber")).Enabled && ((Control)Field("deleteJointButton")).Enabled, "append retains geometric protection and explicit interior-joint mutation access");
                string saved = File.ReadAllText(Path.Combine(path, "CABLE.in"));
                Check(saved.Split(new[] { "0\tA相" }, StringSplitOptions.None).Length == 4 && !saved.Contains("0\tB相") && !saved.Contains("0\tC相"), "duplicate phases not relabelled");
                Check(saved.Contains("材料一") && saved.Contains("材料二") && saved.Contains("材料三"), "all original materials preserved");
                Check(File.ReadAllBytes(Path.Combine(path, "ITEM.in")).SequenceEqual(originalItem) && File.ReadAllBytes(Path.Combine(path, "original.cal")).SequenceEqual(new byte[] { 4, 3, 2, 1 }), "UI append preserves ITEM and raw waveform");
                Check(Directory.GetFiles(path, "*before-joint*").Any(f => File.ReadAllBytes(f).SequenceEqual(original)), "exact pre-edit backup exists");
                byte[] afterLocal = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
                Set("Notes", "unsaved metadata must survive live structural updates");
                var store = assembly.GetType("HVX.ProjectCables.CableJointStore", true);
                object snapshot = store.GetMethod("Read", All).Invoke(null, new object[] { path });
                object plan = snapshot.GetType().GetMethod("Prepare", All).Invoke(snapshot, new object[] { new[] { "ALL" }, 350d, "External shared joint", "冷缩接头", "", "" });
                store.GetMethod("Commit", All).Invoke(null, new[] { plan });
                Check((bool)Call("RefreshCableStructure"), "host refresh accepts stable external joint append");
                Check(grid.Rows.Count == 4 && Markers(null).Length == 4, "host refresh updates table and diagram");
                Check((string)Get("Notes") == "unsaved metadata must survive live structural updates" && (bool)Get("IsDirty"), "metadata-only refresh preserves unsaved notes");
                Check(broadcasts == 1, "refresh does not rebroadcast in a loop");
                File.WriteAllBytes(Path.Combine(path, "CABLE.in"), afterLocal);
                Check((bool)Call("RefreshCableStructure") && Markers(null).Length == 3 && grid.Rows.Count == 3, "external joint deletion refreshes projection without stale nodes");
                File.AppendAllText(Path.Combine(path, "ITEM.in"), "备注: = \"external concurrent metadata\"\r\n", new UTF8Encoding(false));
                byte[] conflictItem = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                Check(!(bool)Call("RefreshCableStructure"), "different ITEM snapshot requires reload");
                Check((string)Get("Notes") == "unsaved metadata must survive live structural updates" && (bool)Get("IsDirty"), "conflict does not overwrite local notes");
                Check(!(bool)Call("SaveProject") && File.ReadAllBytes(Path.Combine(path, "ITEM.in")).SequenceEqual(conflictItem), "rejected refresh never blesses conflicting metadata baseline");
                string separate = Fixture(root, "separate", "三相单芯", Block("A相", "XLPE", 414.8) + Block("B相", "油纸", 500) + Block("C相", "塑料绝缘", 600));
                Check((bool)Call("LoadProject", separate), "load independent phases"); Pump(); markers = Markers(Path.Combine(root, "three-phase.png")); Check(markers.Length == 6, "separate cable rows each render their joints"); CheckGeometry(markers);
                var phases = (ComboBox)Field("selectedPhaseCombo");
                for (int i = 0; i < 3; i++) { phases.SelectedIndex = i; Pump(); Check(((NumericUpDown)Field("jointPositionNumber")).Maximum == new[] { 414.8m, 500m, 600m }[i], "position max follows active phase"); }
                foreach (Size size in new[] { new Size(1065, 600), new Size(1256, 750), new Size(1500, 900) }) { host.ClientSize = size; Pump(); CheckGeometry(Markers(null)); }
            }
            Console.WriteLine("PASS " + checks + " assertions; " + root); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
