using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.ProjectCables;

internal static class CableJointDialogSmoke
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    static T Find<T>(Control control, string name) where T : Control
    { return control.Controls.Find(name, true).Cast<T>().Single(); }
    static string Block(string phase, string material, int length = 400)
    { return "0\t" + phase + "\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t" + material + "\t\r\n1\t终端\t户内冷缩\t" + length + "m\r\n"; }
    static CableJointSnapshot Fixture(string root, string name, string mode, string blocks)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "[ITEM]\r\n电缆芯数: = \"" + mode + "\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), blocks, new UTF8Encoding(false)); return CableJointStore.Read(path);
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string root = Path.Combine(Path.GetFullPath(args[0]), "joint-dialog-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var split = Fixture(root, "split", "三相单芯", Block("A", "XLPE") + Block("B", "PVC") + Block("C", "油纸"));
            byte[] source = File.ReadAllBytes(split.CablePath);
            var unequal = Fixture(root, "unequal", "三相单芯", Block("A", "XLPE", 400) + Block("B", "PVC", 300) + Block("C", "油纸", 500));
            using (var dialog = new CableJointDialog(unequal, 200, "ALL", false, null))
            {
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "A", "B", "C" }) && Find<CheckBox>(dialog, "JointSelectAll").Checked, "ALL preferred scope initially selects all actual phases");
                var position = Find<NumericUpDown>(dialog, "JointPosition"); var name = Find<TextBox>(dialog, "JointName");
                Check(position.Minimum == 0 && position.Maximum == 300, "position limit uses shortest selected cable");
                string error; Check(dialog.TryPrepare(out error), "ALL scope is immediately submittable: " + error);
                position.Value = 250; Check(name.Text == "接头 1" && position.DecimalPlaces == 1, "generated joint name is ordinal and position editor uses one decimal");
                name.Text = "用户接头"; position.Value = 260; Check(name.Text == "用户接头", "manual name survives position edits");
                Find<CheckBox>(dialog, "JointPhase_B").Checked = false; Check(position.Maximum == 400, "unchecking shortest phase expands limit");
                position.Value = 390; Find<CheckBox>(dialog, "JointPhase_B").Checked = true;
                Check(position.Maximum == 300 && position.Value == 300 && name.Text == "用户接头", "reselecting shorter phase bounds value but retains manual name");
                Check(!dialog.TryPrepare(out error), "upper endpoint is not accepted as an internal joint");
                position.Value = 0; Check(!dialog.TryPrepare(out error), "zero endpoint is not accepted");
                position.Value = 250; Check(dialog.TryPrepare(out error) && dialog.Plan.ResolvedTargets.Length == 3, "valid corrected position includes all real blocks");
            }
            using (var dialog = new CableJointDialog(split, 200, "B", false, null))
            {
                Check(dialog.StartPosition == FormStartPosition.CenterParent && !dialog.ShowInTaskbar, "Initial parent-centred owned dialog");
                Check(dialog.Icon != null, "Embedded brand icon fallback available");
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "B" }), "Preferred real phase selected without broadening");
                Check(Find<ComboBox>(dialog, "JointMaterialBefore").Text == "PVC" && Find<ComboBox>(dialog, "JointMaterialAfter").Text == "PVC", "Default materials display current phase segment");
                string error;
                Check(dialog.TryPrepare(out error), "Selected phase creates a plan: " + error);
                Check(dialog.Plan.ResolvedTargets.Single().MaterialBefore == "PVC" && dialog.Plan.ResolvedTargets.Single().MaterialAfter == "PVC", "Blank materials preserve B material");
                Check(File.ReadAllBytes(split.CablePath).SequenceEqual(source), "Preparing never writes project data");
                Find<CheckBox>(dialog, "JointSelectAll").Checked = true;
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "A", "B", "C" }), "Select all enumerates real phases");
                Check(dialog.TryPrepare(out error) && dialog.Plan.ResolvedTargets.Length == 3, "All phases prepared together");
                Check(dialog.Plan.ResolvedTargets.Select(t => t.MaterialBefore).SequenceEqual(new[] { "XLPE", "PVC", "油纸" }), "All-phase default preserves heterogeneous materials");
                Find<CheckBox>(dialog, "JointPhase_B").Checked = false;
                Check(!Find<CheckBox>(dialog, "JointSelectAll").Checked && dialog.SelectedPhases.SequenceEqual(new[] { "A", "C" }), "Specific two-phase selection retained");
                Find<TextBox>(dialog, "JointName").Text = "检查接头";
                Find<ComboBox>(dialog, "JointMaterialBefore").Text = "新前材料";
                Find<ComboBox>(dialog, "JointMaterialAfter").Text = "新后材料";
                Check(dialog.TryPrepare(out error) && dialog.Plan.Name == "检查接头", "Name participates in shared plan");
                Check(dialog.Plan.ResolvedTargets.All(t => t.MaterialBefore == "新前材料" && t.MaterialAfter == "新后材料"), "Explicit before/after materials applied to selected phases");
                Find<CheckBox>(dialog, "JointPhase_A").Checked = false; Find<CheckBox>(dialog, "JointPhase_C").Checked = false;
                Check(!dialog.TryPrepare(out error) && dialog.Plan == null, "No phase selection blocks preparation");
                Find<CheckBox>(dialog, "JointPhase_A").Checked = true;
                Find<TextBox>(dialog, "JointName").Text = "始端";
                Check(!dialog.TryPrepare(out error), "Existing accessory name rejected");
                Find<TextBox>(dialog, "JointName").Text = "新接头";
                dialog.ConfigurePositionResolver(phases => { throw new InvalidOperationException("请编辑实际位置"); });
                Check(!dialog.TryPrepare(out error), "Ambiguous coordinate conversion cannot silently commit");
                Find<NumericUpDown>(dialog, "JointPosition").Value = 201;
                Check(dialog.TryPrepare(out error) && dialog.Plan.PositionM == 201, "Explicit position edit confirms physical position");
                Check(File.ReadAllBytes(split.CablePath).SequenceEqual(source), "Every dialog operation remains read-only until caller commits");
            }
            var duplicate = Fixture(root, "duplicate", "三相单芯", Block("A", "XLPE") + Block("A", "PVC") + Block("A", "油纸"));
            using (var dialog = new CableJointDialog(duplicate, 200, "ALL", false, null))
            {
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "A" }), "Duplicate A labels do not become fabricated B/C phases");
                Check(dialog.Controls.Find("JointPhase_B", true).Length == 0 && Find<CheckBox>(dialog, "JointPhase_A").Text.Contains("3"), "Duplicate label UI states affected block count");
                string error; Check(dialog.TryPrepare(out error) && dialog.Plan.ResolvedTargets.Length == 3, "One A choice explicitly covers all three A blocks");
            }
            var shared = Fixture(root, "shared", "一体三芯", Block("A", "XLPE") + Block("B", "PVC") + Block("C", "油纸"));
            using (var dialog = new CableJointDialog(shared, 150, "C", false, null))
            {
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "ALL" }), "Shared three-core has one shared write scope");
                Check(dialog.Controls.Find("JointPhase_A", true).Length == 0, "Shared three-core has no unnecessary phase picker");
                string error; Check(dialog.TryPrepare(out error) && dialog.Plan.ResolvedTargets.Length == 3, "Shared write includes every actual block without renaming headers");
            }
            var single = Fixture(root, "single", "单相单芯", Block("B", "XLPE"));
            using (var dialog = new CableJointDialog(single, 120, "A", true, null))
            {
                Check(dialog.SelectedPhases.SequenceEqual(new[] { "B" }), "Single-core phase comes from actual file");
                Check(dialog.Controls.Find("JointPhase_A", true).Length == 0, "No non-existing phase offered");
                string error; Check(dialog.TryPrepare(out error) && dialog.Plan.AttachmentType == "中间接头", "English choices store canonical attachment value");
            }
            using (var owner = new Form { ClientSize = new Size(1150, 780), StartPosition = FormStartPosition.Manual, Location = new Point(30, 30) })
            using (var dialog = new CableJointDialog(split, 200, "A", false, null))
            {
                owner.Show(); Application.DoEvents();
                dialog.Shown += delegate
                {
                    var confirm = Find<Button>(dialog, "ConfirmJoint");
                    Check(confirm.Visible && confirm.Width >= 138 && confirm.Height >= 36, "Confirmation button visible and usable");
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(root, "joint-dialog.png")); }
                    dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                };
                dialog.ShowDialog(owner); Check(dialog.Plan == null, "Cancel never leaves a prepared commit plan"); owner.Close();
            }
            if (args.Length > 1) CheckAnalysisIntegration(Path.GetFullPath(args[1]), root);
            Console.WriteLine("CABLE_JOINT_DIALOG_PASS=" + checks.ToString(CultureInfo.InvariantCulture)); Console.WriteLine("QA=" + root); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static object Invoke(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        return type.GetMethods(Members).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target is Type ? null : target, args);
    }
    static object Property(object target, string name) { return target.GetType().GetProperty(name, Members).GetValue(target, null); }
    static void Set(object target, string name, object value) { target.GetType().GetProperty(name, Members).SetValue(target, value, null); }
    static void CheckAnalysisIntegration(string release, string root)
    {
        var library = Assembly.LoadFrom(Path.Combine(release, "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
        // Plot geometry deliberately uses identical duplicate A blocks. The
        // heterogeneous-material case is tested above without forcing the
        // existing analysis pipeline to choose between conflicting geometries.
        var fixture = Fixture(root, "analysis-integration", "三相单芯", Block("A", "XLPE") + Block("A", "XLPE") + Block("A", "XLPE"));
        string directory = Path.GetDirectoryName(fixture.CablePath);
        byte[] original = File.ReadAllBytes(fixture.CablePath);
        var sharedStore = library.GetType("HVX.ProjectCables.CableJointStore", true);
        var snapshot = Invoke(sharedStore, "Read", directory);
        var plan = Invoke(snapshot, "Prepare", new[] { "A" }, 210d, "J001", "预制接头", "", "");
        using (var review = (Control)Activator.CreateInstance(library.GetType("HVX.PdReview.PdReviewControl", true)))
        using (var host = new Form { ShowInTaskbar = false, ClientSize = new Size(1150, 700) })
        {
            Set(review, "PromptSavedAnalysisOnProjectLoad", false); Set(review, "EnglishVersion", true);
            Set(review, "ProjectPath", directory); Set(review, "AnalysisAlgorithmIndex", 4); Set(review, "AnalysisAttenuationTolerancePercent", 22d);
            host.Controls.Add(review); review.Dock = DockStyle.Fill; host.Show(); Application.DoEvents();
            int broadcasts=0;review.GetType().GetEvent("CableStructureChanged").AddEventHandler(review,new EventHandler((s,e)=>broadcasts++));
            review.GetType().GetField("working", Members).SetValue(review, new double[1, 36]);
            Invoke(review, "ApplySharedCableJoint", plan);
            Check((int)Property(review, "SourceRowCount") == 0, "Shared analysis commit clears stale current results");
            Check((int)Property(review, "PendingReportChartCount") == 0, "Shared analysis commit clears stale chart attachments");
            Check((int)Property(review, "AnalysisAlgorithmIndex") == 4 && (double)Property(review, "AnalysisAttenuationTolerancePercent") == 22d, "Shared analysis commit retains expert settings");
            Check(((string)Property(review, "CableStructureEditStatus")).Contains("out of date"), "Shared analysis commit explicitly requires reanalysis");
            Check(((double[])Property(Property(review, "Spectrum"), "CurrentJointPositionsM")).SequenceEqual(new[] { 210d }), "Custom J001 name is plotted without treating endpoints as joints");
            object pdmap = Property(review, "PdMap"), geometry = Invoke(pdmap, "GetReviewConnectorGeometry");
            Check(((double[])Invoke(geometry, "JointPositionsForPhase", 1, 400d)).SequenceEqual(new[] { 210d }), "Review geometry includes J001 for joint approximation");
            var row = new double[36]; row[0] = 1; row[5] = 400;
            Invoke(pdmap, "ApplyProjectMetadataForMap", row);
            Check(((double[])Property(pdmap, "ProjectConnectorPositionsM")).SequenceEqual(new[] { 210d }), "Pulse-map metadata includes custom label J001");
            var metadata = Activator.CreateInstance(library.GetType("HVX.UnifiedSpectrum.ProjectMetadata", true), true);
            Invoke(metadata, "Load", directory);
            Check(((double[])Invoke(metadata, "JointsForPhase", 1)).SequenceEqual(new[] { 210d }), "Standalone spectrum reader uses internal accessories not labels");
            var updated = CableJointStore.Read(directory);
            Check(updated.Blocks.Count == 3 && updated.Blocks.All(b => b.Phase == "A" && b.Positions.Contains(210)), "Duplicate A integration adds every requested A block without renaming");
            Check(Directory.GetFiles(directory, "CABLE.before-joint-*.bak").Any(path => File.ReadAllBytes(path).SequenceEqual(original)), "Shared analysis commit retains exact pre-edit backup");
            Check(broadcasts==1,"analysis successful commit broadcasts a cable change");
            review.GetType().GetField("working",Members).SetValue(review,new double[1,36]);
            Check((bool)Invoke(review,"RefreshCableStructure")&&(int)Property(review,"SourceRowCount")==1,"unchanged metadata does not clear analysis again");
            byte[] afterLocal=File.ReadAllBytes(fixture.CablePath);
            CableJointStore.Commit(CableJointStore.Read(directory).Prepare(new[]{"A"},230,"external","冷缩接头","",""));
            Check((bool)Invoke(review,"RefreshCableStructure")&&(int)Property(review,"SourceRowCount")==0,"cross-page change invalidates obsolete analysis");
            Check(((double[])Property(Property(review,"Spectrum"),"CurrentJointPositionsM")).SequenceEqual(new[]{210d,230d}),"cross-page analysis marker refresh includes added joint");
            File.WriteAllBytes(fixture.CablePath,afterLocal);
            Check((bool)Invoke(review,"RefreshCableStructure")&&((double[])Property(Property(review,"Spectrum"),"CurrentJointPositionsM")).SequenceEqual(new[]{210d}),"cross-page analysis deletion removes stale markers");
            Check(broadcasts==1&&(int)Property(review,"AnalysisAlgorithmIndex")==4,"sync preserves chosen algorithm and does not rebroadcast");
            var conflict = Fixture(root, "analysis-conflicting-material", "三相单芯", Block("A", "XLPE") + Block("A", "PVC"));
            CableJointStore.Commit(conflict.Prepare(new[] { "A" }, 210, "J001", "预制接头", "", ""));
            string conflictPath = Path.GetDirectoryName(conflict.CablePath);
            Invoke(metadata, "Load", conflictPath);
            Check(((double[])Invoke(metadata, "JointsForPhase", 1)).Length == 0, "Spectrum still rejects duplicate phase blocks with conflicting materials");
            Set(pdmap, "ProjectPath", conflictPath);
            geometry = Invoke(pdmap, "GetReviewConnectorGeometry");
            Check(((double[])Invoke(geometry, "JointPositionsForPhase", 1, 400d)).Length == 0, "Review still refuses conflicting same-phase material geometry");
            Invoke(pdmap, "ApplyProjectMetadataForMap", row);
            Check(((double[])Property(pdmap, "ProjectConnectorPositionsM")).Length == 0, "Pulse-map still refuses conflicting same-phase geometry");
            host.Close();
        }
    }
}
