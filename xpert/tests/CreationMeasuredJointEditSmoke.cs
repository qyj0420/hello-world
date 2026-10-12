using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class CreationMeasuredJointEditSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static int checks;
    static object Field(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
    static object Get(object value, string name) { return value.GetType().GetProperty(name, All).GetValue(value, null); }
    static void Set(object value, string name, object content) { value.GetType().GetProperty(name, All).SetValue(value, content, null); }
    static object Call(object value, string name, params object[] args) { Type type = value as Type ?? value.GetType(); return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args); }
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    static string Block(string phase, string material)
    { return "0\t" + phase + "\t\t\r\n1\t人工起点\t户内冷缩\t0m\r\n2\t本体\t" + material + "\t\r\n1\t接头 1\t户内冷缩\t80.000m\r\n2\t本体\t油纸\t\r\n1\t人工名称\t户内冷缩\t160m\r\n2\t本体\tPVC\t\r\n1\t接头 3\t户内冷缩\t260m\r\n2\t本体\tEPR\t\r\n1\t人工终点\t户内冷缩\t400m\r\n"; }
    static string Fixture(string root, string name, string mode, string cable)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n始端地点 = QA start\r\n终端地点 = QA end\r\n电缆芯数 = " + mode + "\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\nB相电缆长度 = 400\r\nC相电缆长度 = 400\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), cable, new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(path, "raw.dem"), new byte[] { 7, 2, 9 }); return path;
    }
    static DataGridViewRow Select(Control module, int block, double position)
    {
        var grid = (DataGridView)Field(module, "cableGrid"); grid.ClearSelection();
        DataGridViewRow row = grid.Rows.Cast<DataGridViewRow>().Single(r => (int)Field(r.Tag, "SourceBlockIndex") == block && Math.Abs((double)Field(r.Tag, "Position") - position) < .000001);
        row.Selected = true; Pump(); return row;
    }
    static double[][] Positions(Assembly assembly, string path)
    {
        object snapshot = Call(assembly.GetType("HVX.ProjectCables.CableJointStore", true), "Read", path);
        return ((IEnumerable)Field(snapshot, "Blocks")).Cast<object>().Select(b => ((IEnumerable)Field(b, "Positions")).Cast<double>().ToArray()).ToArray();
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "ce-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(qa);
            Assembly assembly = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            string originalCable = Block("A相", "A-first") + Block("A相", "A-second") + Block("B相", "B-only");
            string path = Fixture(qa, "measured", "三相单芯", originalCable);
            using (var module = (Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectCreationControl", true)))
            using (var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(1256, 750) })
            {
                Set(module, "ShowDialogMessages", false); Set(module, "EmbeddedMode", true); Set(module, "OnlineWeatherEnabled", false); Set(module, "AutoRefreshWeatherOnHardwareSample", false);
                Set(module, "ConfirmJointMaterialMerge", new Func<string, bool>(message => !String.IsNullOrWhiteSpace(message)));
                Set(module, "SettingsPath", Path.Combine(qa, "settings.ini")); Set(module, "DataRootDirectory", qa); Set(module, "LanguageIndex", 0);
                int changes = 0, saved = 0;
                module.GetType().GetEvent("CableStructureChanged").AddEventHandler(module, new EventHandler(delegate { changes++; }));
                module.GetType().GetEvent("ProjectSaved").AddEventHandler(module, new EventHandler(delegate { saved++; }));
                host.Controls.Add(module); module.Dock = DockStyle.Fill; host.Show(); Pump();
                Check(((Label)Field(module, "projectPageTitle")).Text == "创建项目", "unloaded module title describes creation");
                Check((bool)Call(module, "LoadProject", path), "measured project loads with custom endpoint names");
                Check(((Label)Field(module, "projectPageTitle")).Text == "编辑项目", "loaded module title describes editing");
                Call(module, "SetOptionalSectionExpanded", "JointsAndBody", true); Pump();
                Check(!(bool)Get(module, "CanEditCableGeometry") && !((Control)Field(module, "lengthANumber")).Enabled && !((Control)Field(module, "cableModeCombo")).Enabled, "length and core mode remain protected");
                var grid = (DataGridView)Field(module, "cableGrid");
                Check(grid.Rows.Count == 6, "true interior topology excludes manually named endpoints");
                foreach (string field in new[] { "addJointButton", "editJointButton", "deleteJointButton" }) Check(((Control)Field(module, field)).Parent != null && ((Control)Field(module, field)).Enabled, field + " is attached and enabled");
                Check(grid.ContextMenuStrip.Items.Cast<ToolStripItem>().All(i => i.Enabled), "right-click edit/delete available");
                Set(module, "Notes", "retain this unsaved draft");
                byte[] before = File.ReadAllBytes(Path.Combine(path, "CABLE.in")), item = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                Select(module, 1, 80); ((NumericUpDown)Field(module, "jointPositionNumber")).Value = 220;
                ((Button)Field(module, "editJointButton")).PerformClick(); Pump();
                Check((bool)Get(module, "LastOperationSucceeded") && (string)Get(module, "LastOperation") == "EditJoint", "actual edit button commits successfully");
                double[][] positions = Positions(assembly, path);
                Check(positions[0].Contains(80) && !positions[1].Contains(80) && positions[1].Contains(220) && positions[2].Contains(80), "only selected duplicate A block moves");
                Check(File.ReadAllText(Path.Combine(path, "CABLE.in")).Contains("1\t接头 2\t户内冷缩\t220m\r\n2\t本体\t油纸\t"), "editing only position does not silently replace following material with destination material");
                string confirmation = (string)Get(module, "LastJointChangeConfirmation");
                Check(confirmation.Contains("影响 1") && confirmation.Contains("A / 2") && confirmation.Contains("A-second + 油纸 → A-second") && confirmation.Contains("重新核对"), "confirmation identifies exact scope, merge rule and analysis review requirement");
                Check(Directory.GetFiles(path, "CABLE.before-joint-*.bak").Any(p => File.ReadAllBytes(p).SequenceEqual(before)), "actual UI edit has exact recoverable backup");
                Check((string)Get(module, "Notes") == "retain this unsaved draft" && (bool)Get(module, "IsDirty"), "dirty non-geometry fields retained");
                Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))) && File.ReadAllBytes(Path.Combine(path, "raw.dem")).SequenceEqual(new byte[] { 7, 2, 9 }), "actual UI does not rewrite ITEM or measurements");
                Check(changes == 1 && saved == 1, "successful edit emits one persisted-change and one project notification");
                DataGridViewRow selected = Select(module, 1, 160); Check((string)Field(selected.Tag, "Name") == "人工名称", "manual name preserved through re-sort and refresh");
                before = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
                grid.ContextMenuStrip.Items[1].PerformClick(); Pump();
                Check((bool)Get(module, "LastOperationSucceeded") && (string)Get(module, "LastOperation") == "RemoveJoint", "actual context delete commits successfully");
                positions = Positions(assembly, path); Check(positions[0].Contains(160) && !positions[1].Contains(160) && positions[2].Contains(160), "right-click deletion changes selected real block only");
                Check(grid.Rows.Count == 5 && changes == 2 && saved == 2, "grid and persisted-change notifications reflect deletion immediately");
                var canvas = (Control)Field(module, "topology");
                using (var bitmap = new Bitmap(canvas.Width, canvas.Height)) { canvas.DrawToBitmap(bitmap, canvas.ClientRectangle); bitmap.Save(Path.Combine(qa, "joint-edit-topology.png")); }
                Check(((IEnumerable)Field(module, "topologyJointSnapshot")).Cast<object>().Count(j => (int)Field(j, "SourceBlockIndex") == 1) == 2, "structure drawing source snapshot refreshes after delete");
                bool clearRejected = false; try { Call(module, "ClearJoints", "A相"); } catch (TargetInvocationException ex) { clearRejected = ex.InnerException is InvalidOperationException; }
                Check(clearRejected, "bulk clear not unlocked");
                before = File.ReadAllBytes(Path.Combine(path, "CABLE.in")); int prior = changes;
                Check(!(bool)Call(module, "EditJoint", "A相", 0, 400d, "", ""), "public API refuses endpoint");
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))) && changes == prior, "rejected edit cannot signal success or mutate bytes");
                File.SetAttributes(Path.Combine(path, "CABLE.in"), FileAttributes.ReadOnly);
                try { Check((bool)Call(module, "RemoveJoint", "A相", 0), "explicit public removal uses protected metadata transaction"); Check((File.GetAttributes(Path.Combine(path, "CABLE.in")) & FileAttributes.ReadOnly) != 0, "successful transaction restores read-only attribute"); }
                finally { File.SetAttributes(Path.Combine(path, "CABLE.in"), FileAttributes.Normal); }
                Check(!before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))) && changes == prior + 1, "read-only transaction commits one change");
                before = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
                File.AppendAllText(Path.Combine(path, "ITEM.in"), "external=changed\r\n");
                Check(!(bool)Call(module, "RemoveJoint", "A相", 0), "loaded ITEM hash conflict rejected");
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))) && (string)Get(module, "Notes") == "retain this unsaved draft", "conflict retains cable and user draft");
                Check((bool)Call(module, "LoadProject", path), "reload confirms new baseline");
                File.AppendAllText(Path.Combine(path, "CABLE.in"), "\r\n"); before = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
                Check(!(bool)Call(module, "RemoveJoint", "A相", 0) && before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))), "loaded CABLE hash conflict rejected");

                string shared = Fixture(qa, "shared", "三芯", Block("A相", "M1") + Block("A相", "M2") + Block("A相", "M3"));
                Check((bool)Call(module, "LoadProject", shared), "shared repeated-A project loads");
                Check((bool)Call(module, "RemoveJoint", "A相", 0), "explicit material decision confirms shared removal when host suppresses dialogs");
                Check(Positions(assembly, shared).All(p => !p.Contains(80)), "one shared joint removed from all corresponding blocks");
                Check(((string)Get(module, "LastJointChangeConfirmation")).Contains("影响 3") && ((string)Get(module, "LastJointChangeConfirmation")).Contains("M3"), "shared confirmation lists all impacted blocks and material differences");
                Check(!File.ReadAllText(Path.Combine(shared, "CABLE.in")).Contains("0\tB相") && !File.ReadAllText(Path.Combine(shared, "CABLE.in")).Contains("0\tC相"), "shared mutation never assigns missing phase names");
                Check((bool)Call(module, "EditJoint", "A相", 0, 160d, "手工附件", "手工材料"), "explicit material and attachment edit is supported");
                confirmation = (string)Get(module, "LastJointChangeConfirmation");
                Check(confirmation.Contains("附件类型 A / 1: 户内冷缩 → 手工附件") && confirmation.Contains("接头后材料 A / 3: PVC → 手工材料"), "explicit material and attachment changes are listed for every shared target");
                string inconsistent = Fixture(qa, "inconsistent", "三芯", Block("A相", "M1") + Block("A相", "M2").Replace("80.000m", "90m"));
                Check((bool)Call(module, "LoadProject", inconsistent), "inconsistent shared structure remains browseable");
                Check(!(bool)Call(module, "RemoveJoint", "A相", 0) && ((string)Get(module, "LastError")).Contains("位置不一致"), "inconsistent shared scope explicitly refused");

                string precise = Fixture(qa, "precise", "三相单芯", Block("A相", "M1").Replace("80.000m", "80.123m"));
                Check((bool)Call(module, "LoadProject", precise), "sub-decimal position fixture loads");
                Select(module, 0, 80.123); Check(((NumericUpDown)Field(module, "jointPositionNumber")).Value == 80.1m, "UI still displays requested one decimal");
                ((Button)Field(module, "editJointButton")).PerformClick(); Pump();
                Check(Positions(assembly, precise)[0].Contains(80.123) && File.ReadAllText(Path.Combine(precise, "CABLE.in")).Contains("80.123m"), "no position edit preserves exact stored coordinate instead of rounded display");
                Check(((string)Get(module, "LastJointChangeConfirmation")).Contains("80.1 [80.123]"), "confirmation exposes retained full precision alongside one-decimal display");
                Select(module, 0, 80.123); ((NumericUpDown)Field(module, "jointPositionNumber")).Value = 81.2m; ((Button)Field(module, "editJointButton")).PerformClick(); Pump();
                Check(Positions(assembly, precise)[0].Contains(81.2), "explicit position change is applied");
                string unequal = Fixture(qa, "unequal", "三相单芯", Block("A相", "M1").Replace("260m", "500m").Replace("400m", "600m") + Block("A相", "M2"));
                Check((bool)Call(module, "LoadProject", unequal), "different-length repeated A blocks load without relabeling");
                Select(module, 0, 500); Check(((NumericUpDown)Field(module, "jointPositionNumber")).Maximum == 600m && ((NumericUpDown)Field(module, "jointPositionNumber")).Value == 500m, "selected long source block uses its own maximum and coordinate");
                ((NumericUpDown)Field(module, "jointPositionNumber")).Value = 550m; ((Button)Field(module, "editJointButton")).PerformClick(); Pump();
                Check(Positions(assembly, unequal)[0].Contains(550) && Positions(assembly, unequal)[1].Last() == 400, "long selected block can move inside its real range without changing shorter duplicate");
                Select(module, 1, 260); Check(((NumericUpDown)Field(module, "jointPositionNumber")).Maximum == 400m, "short selected source block restores its own smaller limit");

                Check((bool)Call(module, "LoadProject", shared), "restore layout fixture");
                Call(module, "SetOptionalSectionExpanded", "JointsAndBody", true);
                foreach (int language in new[] { 0, 1, 18 }) foreach (int width in new[] { 1065, 1256, 1850 })
                {
                    Set(module, "LanguageIndex", language); host.ClientSize = new Size(width, 620); Pump();
                    if (language == 1) {
                        string title=((Label)Field(module, "projectPageTitle")).Text;
                        Check(String.Equals(title,"Edit project",StringComparison.OrdinalIgnoreCase),"edit title follows UI language: "+title);
                    }
                    var actions = (FlowLayoutPanel)((Control)Field(module, "editJointButton")).Parent;
                    foreach (Control action in actions.Controls)
                    {
                        Check(actions.ClientRectangle.Contains(action.Bounds), "edit actions fully contained at language/width " + language + "/" + width);
                        Check(action.Height >= action.Font.Height + 10 && action.Width >= TextRenderer.MeasureText(action.Text, action.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 20, "full single-line action text and comfortable height");
                    }
                    var buttons = actions.Controls.Cast<Control>().ToArray();
                    for (int i = 0; i < buttons.Length; i++) for (int j = i + 1; j < buttons.Length; j++) Check(!buttons[i].Bounds.IntersectsWith(buttons[j].Bounds), "actions do not overlap");
                }
                Set(module, "LanguageIndex", 1); host.ClientSize = new Size(1065, 500);
                foreach (string action in new[] { "addJointButton", "editJointButton", "deleteJointButton" }) ((Control)Field(module, action)).Font = new Font("Microsoft YaHei UI", 18);
                Pump();
                var parent = ((Control)Field(module, "editJointButton")).Parent;
                foreach (Control button in parent.Controls) Check(parent.ClientRectangle.Contains(button.Bounds), "18pt narrow-window actions wrap without clipping");
                var viewport = (ScrollableControl)parent.Parent.Parent; viewport.ScrollControlIntoView(parent); Pump();
                using (var image = new Bitmap(module.Width, module.Height)) { module.DrawToBitmap(image, module.ClientRectangle); image.Save(Path.Combine(qa, "joint-actions-en-18pt-1065.png")); }
            }
            Console.WriteLine("CREATION_MEASURED_JOINT_EDIT_PASS=" + checks + "; HARDWARE=False; EVIDENCE=" + qa); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException ? ((TargetInvocationException)ex).InnerException : ex); return 1; }
    }
}
