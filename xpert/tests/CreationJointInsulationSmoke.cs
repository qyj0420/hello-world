using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationJointInsulationSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Field(object o, string n) { return o.GetType().GetField(n, All).GetValue(o); }
    static object Get(object o, string n) { return o.GetType().GetProperty(n, All).GetValue(o, null); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, All).SetValue(o, v, null); }
    static object Call(object o, string n, params object[] a) { Type t = o as Type ?? o.GetType(); return t.GetMethods(All).Single(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(o is Type ? null : o, a); }
    static void Check(bool v, string n) { checks++; if (!v) throw new Exception(n); }
    static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    static string Block(string first, string middle)
    { return "0\tA相\t\t\r\n1\t始端人工名\t户内冷缩\t0m\r\n2\t本体\t" + first + "\t\r\n1\t人工接头\t户内冷缩\t80.123m\r\n2\t本体\t" + middle + "\t\r\n1\t接头 2\t户内冷缩\t160m\r\n2\t本体\t尾段材料\t\r\n1\t终端人工名\t户内冷缩\t400m\r\n"; }
    static string Fixture(string root, string name, string mode, string cable, bool measured = true)
    {
        string p = Path.Combine(root, name); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n始端地点 = QA始端\r\n终端地点 = QA终端\r\n电缆芯数 = " + mode + "\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\nB相电缆长度 = 400\r\nC相电缆长度 = 400\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(p, "CABLE.in"), cable, new UTF8Encoding(true));
        if (measured) File.WriteAllBytes(Path.Combine(p, "raw.dem"), new byte[] { 3, 1, 4 });
        return p;
    }
    static object Snapshot(Assembly a, string p) { return Call(a.GetType("HVX.ProjectCables.CableJointStore", true), "Read", p); }
    static object Target(Assembly a, string p, int b, int i)
    { return ((IEnumerable)Call(Snapshot(a, p), "ExistingTargets", b, i)).Cast<object>().Single(t => (int)Field(t, "BlockIndex") == b); }
    static void Materials(Assembly a, string p, int b, int i, string before, string after)
    {
        object t = Target(a, p, b, i);
        Check((string)Field(t, "MaterialBefore") == before, "block " + b + " before material = " + before);
        Check((string)Field(t, "MaterialAfter") == after, "block " + b + " after material = " + after);
    }
    static void Select(Control c, int block, double pos)
    {
        var g = (DataGridView)Field(c, "cableGrid"); g.ClearSelection();
        g.Rows.Cast<DataGridViewRow>().Single(r => (int)Field(r.Tag, "SourceBlockIndex") == block && Math.Abs((double)Field(r.Tag, "Position") - pos) < .000001).Selected = true; Pump();
    }
    static string Value(Control c, string field) { return (string)Call(c, "GetChoiceValue", Field(c, field)); }
    static void Choice(Control c, string field, string value) { Call(c, "SelectChoice", Field(c, field), value); }
    static void EditClick(Control c) { ((Button)Field(c, "editJointButton")).PerformClick(); Pump(); Check((bool)Get(c, "LastOperationSucceeded"), "actual edit succeeds: " + Get(c, "LastError")); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "ji-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            Assembly a = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            using (var c = (Control)Activator.CreateInstance(a.GetType("HVX.Controls.ProjectCreationControl", true)))
            using (var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(1280, 800) })
            {
                Set(c, "ShowDialogMessages", false); Set(c, "EmbeddedMode", true); Set(c, "OnlineWeatherEnabled", false); Set(c, "AutoRefreshWeatherOnHardwareSample", false);
                Set(c, "ConfirmJointMaterialMerge", new Func<string, bool>(message => !String.IsNullOrWhiteSpace(message)));
                Set(c, "SettingsPath", Path.Combine(qa, "settings.ini")); Set(c, "DataRootDirectory", qa); Set(c, "LanguageIndex", 0);
                host.Controls.Add(c); c.Dock = DockStyle.Fill; host.Show(); Pump();
                string path = Fixture(qa, "measured", "三相单芯", Block("A前一", "A后一") + Block("A前二", "A后二"));
                Check((bool)Call(c, "LoadProject", path), "load duplicate A materials"); Call(c, "SetOptionalSectionExpanded", "JointsAndBody", true); Pump();
                byte[] raw = File.ReadAllBytes(Path.Combine(path, "raw.dem")), item = File.ReadAllBytes(Path.Combine(path, "ITEM.in")), original = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
                Select(c, 1, 80.123);
                Check(Value(c, "beforeBodyTypeCombo") == "A前二" && Value(c, "bodyTypeCombo") == "A后二", "selection defaults from its actual source block, not first repeated phase");
                Choice(c, "beforeBodyTypeCombo", "交联聚乙烯"); Choice(c, "bodyTypeCombo", "油纸"); EditClick(c);
                Materials(a, path, 1, 1, "交联聚乙烯", "油纸"); Materials(a, path, 0, 1, "A前一", "A后一");
                Check((double)Field(Target(a, path, 1, 1), "PositionM") == 80.123 && (string)Field(Target(a, path, 1, 1), "Name") == "人工接头", "insulation-only edit preserves precise position and manual name");
                Check(Directory.GetFiles(path, "CABLE.before-joint-*.bak").Any(p => File.ReadAllBytes(p).SequenceEqual(original)), "two-sided UI edit creates exact recoverable backup");
                Check(raw.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "raw.dem"))) && item.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))), "two-sided edit never rewrites raw samples or ITEM");
                Check((bool)Call(c, "LoadProject", path), "reload dual materials"); Select(c, 1, 80.123);
                Check(Value(c, "beforeBodyTypeCombo") == "交联聚乙烯" && Value(c, "bodyTypeCombo") == "油纸", "reloaded UI shows saved sides");
                ((NumericUpDown)Field(c, "jointPositionNumber")).Value = 220; EditClick(c);
                Materials(a, path, 1, 2, "尾段材料", "油纸");
                Select(c, 1, 220); Choice(c, "beforeBodyTypeCombo", "前段明确修改"); Choice(c, "bodyTypeCombo", "后段明确修改");
                ((NumericUpDown)Field(c, "jointPositionNumber")).Value = 120; EditClick(c);
                Materials(a, path, 1, 1, "前段明确修改", "后段明确修改");
                Check((bool)Call(c, "AddJointWithMaterials", "A相", "预制接头", "新增前绝缘", "新增后绝缘", 300d), "measured addition accepts independently selected sides");
                Materials(a, path, 0, 3, "新增前绝缘", "新增后绝缘"); Materials(a, path, 1, 3, "新增前绝缘", "新增后绝缘");
                string shared = Fixture(qa, "shared", "三芯", Block("前甲", "后甲") + Block("前乙", "后乙") + Block("前丙", "后丙"));
                object snap = Snapshot(a, shared);
                using (var dialog = (Form)Activator.CreateInstance(a.GetType("HVX.ProjectCables.CableJointDialog", true), All, null, new object[] { snap, 40d, "ALL", false, null }, null))
                {
                    Call(dialog, "ConfigureMaterials", "弹窗前绝缘", "弹窗后绝缘");
                    object[] prepareArgs = { null };
                    Check((bool)dialog.GetType().GetMethod("TryPrepare", All).Invoke(dialog, prepareArgs), "shared addition dialog accepts two independent prefilled materials");
                    object plan = Get(dialog, "Plan");
                    Check(((IEnumerable)Field(plan, "ResolvedTargets")).Cast<object>().All(t => (string)Field(t, "MaterialBefore") == "弹窗前绝缘" && (string)Field(t, "MaterialAfter") == "弹窗后绝缘"), "actual dialog prepares both sides for every selected block");
                }
                Check((bool)Call(c, "LoadProject", shared), "shared topology loads"); Select(c, 0, 80.123); EditClick(c);
                Materials(a, shared, 0, 1, "前甲", "后甲"); Materials(a, shared, 1, 1, "前乙", "后乙"); Materials(a, shared, 2, 1, "前丙", "后丙");
                Select(c, 0, 80.123); Choice(c, "beforeBodyTypeCombo", "共用前绝缘"); EditClick(c);
                Materials(a, shared, 0, 1, "共用前绝缘", "后甲"); Materials(a, shared, 1, 1, "共用前绝缘", "后乙"); Materials(a, shared, 2, 1, "共用前绝缘", "后丙");
                Check(!File.ReadAllText(Path.Combine(shared, "CABLE.in")).Contains("0\tB相"), "shared materials do not manufacture B/C phase labels");
                Check((bool)Call(c, "AddJointWithMaterials", "A相", "预制接头", "", "", 40d), "blank shared insertion inherits each true segment");
                Materials(a, shared, 0, 1, "共用前绝缘", "共用前绝缘"); Materials(a, shared, 2, 1, "共用前绝缘", "共用前绝缘");
                string draft = Fixture(qa, "draft", "单芯", Block("初始前", "初始后"), false);
                Check((bool)Call(c, "LoadProject", draft) && (bool)Get(c, "CanEditCableGeometry"), "unmeasured project uses draft geometry");
                Check((bool)Call(c, "AddJointWithMaterials", "A相", "预制接头", "新前", "新后", 40d), "draft insertion changes both surrounding segments");
                Check((bool)Call(c, "SaveProject"), "draft with dual insulation saves: " + Get(c, "LastError"));
                string draftSaved = (string)Get(c, "CurrentProjectPath"); Materials(a, draftSaved, 0, 1, "新前", "新后");
                Check((bool)Call(c, "EditJointWithMaterials", "A相", 1, 80.123d, "", "再次前", "再次后"), "draft edit updates preceding body and following body");
                Check((bool)Call(c, "SaveProject"), "draft dual edit saves"); draftSaved = (string)Get(c, "CurrentProjectPath"); Materials(a, draftSaved, 0, 2, "再次前", "再次后");
                Check((bool)Call(c, "LoadProject", shared), "restore layout fixture"); Call(c, "SetOptionalSectionExpanded", "JointsAndBody", true);
                foreach (int lang in new[] { 0, 1, 18 }) foreach (int width in new[] { 1065, 1280, 1920 })
                {
                    Set(c, "LanguageIndex", lang); host.ClientSize = new Size(width, 650); Pump(); Select(c, 0, 80.123);
                    foreach (string field in new[] { "beforeBodyTypeCombo", "bodyTypeCombo" })
                    {
                        var box = (ComboBox)Field(c, field); Check(box.Enabled && box.Parent.ClientRectangle.Contains(box.Bounds), "both insulation inputs are usable without clipping");
                        Check(box.Width >= 120 && box.Height >= box.PreferredHeight, "insulation choice has real clickable width and height");
                    }
                    var actions = ((Control)Field(c, "editJointButton")).Parent;
                    foreach (Control b in actions.Controls) Check(actions.ClientRectangle.Contains(b.Bounds), "dual-material row does not overlap actions");
                    if (width == 1280) using (var bitmap = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(bitmap, c.ClientRectangle); bitmap.Save(Path.Combine(qa, "insulation-lang-" + lang + ".png")); }
                }
                Set(c, "LanguageIndex", 1); host.ClientSize = new Size(1065, 600);
                foreach (string field in new[] { "beforeBodyTypeCombo", "bodyTypeCombo" }) ((Control)Field(c, field)).Font = new Font("Microsoft YaHei UI", 18);
                Pump();
                foreach (string field in new[] { "beforeBodyTypeCombo", "bodyTypeCombo" })
                { var box = (ComboBox)Field(c, field); Check(box.Parent.ClientRectangle.Contains(box.Bounds) && box.Height >= box.PreferredHeight, "18pt insulation controls keep sufficient height"); }
                var pane = (ScrollableControl)((Control)Field(c, "cableGrid")).Parent.Parent; pane.ScrollControlIntoView(((Control)Field(c, "editJointButton")).Parent); Pump();
                using (var image = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(image, c.ClientRectangle); image.Save(Path.Combine(qa, "insulation-en-18pt-1065.png")); }
            }
            Console.WriteLine("CREATION_JOINT_INSULATION_PASS=" + checks + "; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e is TargetInvocationException ? ((TargetInvocationException)e).InnerException : e); return 1; }
    }
}
