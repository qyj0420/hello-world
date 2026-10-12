using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Synthetic project and managed modal UI only. No backend, SDK or user data.
internal static class CalibrationCandidateReviewSmoke
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly module; static Type pageType, storeType, dialogType; static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    static object Call(object owner, string name, params object[] args) {
        Type type = owner as Type ?? owner.GetType();
        return type.GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p,i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i]) || p.IsOut).All(v => v))
            .Invoke(owner is Type ? null : owner, args);
    }
    static object Get(object owner, string name) { return owner.GetType().GetProperty(name, All).GetValue(owner, null); }
    static void Set(object owner, string name, object value) { owner.GetType().GetProperty(name, All).SetValue(owner, value, null); }
    static object Field(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
    static void Field(object owner, string name, object value) { owner.GetType().GetField(name, All).SetValue(owner, value); }
    static Control Find(Control parent, string name) { return parent.Controls.Find(name, true).Single(); }
    static object[] Joints(Control page, int phase = 0) { return ((Array)Call(page, "GetPhaseJoints", phase)).Cast<object>().ToArray(); }
    static string Block(string phase, bool manual = false) {
        return "0\t" + phase + "\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n" +
            (manual ? "1\t接头 1\tColdShrink\t200.123456m\r\n2\tBody\tPVC\t\r\n" : "") + "1\tEnd\tTerminal\t400m\r\n";
    }
    static string Fixture(string root, string name, string mode = "单相", bool manual = false) {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "电缆芯数: = \"" + mode + "\"\r\n电缆长度: = \"400\"\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), Block("A", manual) + (mode == "单芯三相" ? Block("B") + Block("C") : ""), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(path, "protected.raw"), new byte[] { 1, 2, 3, 4 }); return path;
    }
    static Control Page(string path, bool simulation = false) {
        var page = (Control)Activator.CreateInstance(pageType); Set(page, "SimulationMode", simulation); Set(page, "ProjectPath", path);
        Set(page, "SelectedPhaseIndex", 0); Set(page, "SelectedLevelCode", "TDR"); Set(page, "LanguageIndex", 1); return page;
    }
    static object Candidate(Control page, double distance) {
        var joint = Call(page, "AddPhaseJoint", 0, "Pending " + distance, distance);
        Set(joint, "AutomaticallyDetected", true); Set(joint, "Confirmed", false); Set(joint, "ConfidencePercent", 98d); return joint;
    }
    static object Plan(string path, double distance, string name = "Reviewed") {
        return Call(Call(storeType, "Read", path), "PrepareCandidateWithNamePolicy", new[] { "A" }, distance, name, "HeatShrink", "PE", "PVC", (bool?)false);
    }
    static Form Dialog(string path, double distance, bool clearPhases = false) {
        object snapshot = Call(storeType, "Read", path);
        var dialog = (Form)Activator.CreateInstance(dialogType, All, null, new object[] { snapshot, distance, "A", 1, null }, null);
        Call(dialog, "ConfigureCandidateReview", distance, clearPhases); return dialog;
    }
    static bool Prepared(Form dialog, out object plan) {
        object[] args = { null }; bool ok = (bool)Call(dialog, "TryPrepare", args); plan = Get(dialog, "Plan"); return ok;
    }
    static void Reject(Action action, string message) {
        bool rejected = false; try { action(); } catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException || ex.InnerException is IOException || ex.InnerException is InvalidDataException; }
        Check(rejected, message);
    }
    static void DirectGuards(string root) {
        string path = Fixture(root, "precision", manual: true); byte[] original = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
        using (var dialog = Dialog(path, 80.123456789)) {
            Check(!Find(dialog, "ExistingJoints").Enabled && !Find(dialog, "DeleteJoint").Enabled && !Find(dialog, "NewJoint").Enabled, "candidate editor cannot alter existing manual joints");
            var position = (NumericUpDown)Find(dialog, "JointPosition"); Check(position.DecimalPlaces == 1 && position.Value == 80.1m, "candidate presentation stays 0.1m");
            Find(dialog, "JointName").Text = "Reviewed"; object plan; Check(Prepared(dialog, out plan), "exact unedited candidate prepares");
            Check((double)Field(plan, "PositionM") == 80.123456789, "unedited candidate precision not rounded by display or store");
            Check(Encoding.UTF8.GetString((byte[])Field(plan, "Replacement")).Contains("80.123456789m"), "precise candidate encoded with roundtrip representation");
            object[] removal = { null }; Check(!(bool)Call(dialog, "TryPrepareRemoval", removal), "candidate editor refuses direct existing removal");
            position.Value = 80.2m; Check(Prepared(dialog, out plan) && (double)Field(plan, "PositionM") == 80.2, "explicit position edit supplies entered value");
            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))), "prepare alone writes nothing");
        }
        string independent = Fixture(root, "phases", "单芯三相");
        using (var dialog = Dialog(independent, 90.123456, true)) {
            Check(!((CheckBox)Find(dialog, "JointPhase_A")).Checked && !((CheckBox)Find(dialog, "JointPhase_B")).Checked && !((CheckBox)Find(dialog, "JointPhase_C")).Checked, "ALL candidate never automatically selects independent phases");
            object plan; Check(!Prepared(dialog, out plan), "no explicitly selected phase cannot commit");
            ((CheckBox)Find(dialog, "JointPhase_B")).Checked = true;
            Check(Prepared(dialog, out plan) && ((string[])Field(plan, "Phases")).SequenceEqual(new[] { "B" }), "explicit actual phase only is prepared");
            Check((double)Field(plan, "PositionM") == 90.123456, "phase choice does not truncate unedited candidate");
        }
        foreach (string scenario in new[] { "project", "phase", "level", "simulation", "busy", "candidate", "confirmed", "stale-cable", "stale-item", "wrong-operation", "duplicate" }) {
            path = Fixture(root, "guard-" + scenario, manual: true);
            using (var page = Page(path)) {
                object candidate = Candidate(page, 85.123456), plan = Plan(path, 85.123456);
                if (scenario == "project") Set(page, "ProjectPath", independent);
                if (scenario == "phase") Set(page, "SelectedPhaseIndex", 1);
                if (scenario == "level") Set(page, "SelectedLevelCode", "5nC");
                if (scenario == "simulation") Set(page, "SimulationMode", true);
                if (scenario == "busy") Field(page, "hostAcquisitionActive", true);
                if (scenario == "candidate") ((IList)Field(Call(page, "GetPhaseStateNoLock", 0), "Joints")).Remove(candidate);
                if (scenario == "confirmed") Set(candidate, "Confirmed", true);
                if (scenario == "stale-cable") File.AppendAllText(Path.Combine(path, "CABLE.in"), "\r\n");
                if (scenario == "stale-item") File.AppendAllText(Path.Combine(path, "ITEM.in"), "changed\r\n");
                if (scenario == "wrong-operation") Field(plan, "OperationCode", "RemoveJoint");
                if (scenario == "duplicate") plan = Call(Call(storeType, "Read", path), "PrepareEdit", 0, 1, 200.123456d, "NoOverwrite", "HeatShrink", "PE", "PVC", true);
                byte[] before = File.ReadAllBytes(Path.Combine(path, "CABLE.in")); object chosen = plan;
                Reject(() => Call(page, "CommitReviewedJointCandidate", candidate, path, 0, "TDR", false, chosen, scenario == "duplicate" ? "EditJoint" : "AddJoint"), "candidate guard rejects " + scenario);
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))), "rejected candidate leaves cable bytes unchanged " + scenario);
                Check(Directory.GetFiles(path, "*.bak").Length == 0, "rejected candidate creates no false commit backup " + scenario);
                Field(page, "hostAcquisitionActive", false);
            }
        }
        path = Fixture(root, "moved-candidate", manual: true);
        using (var page = Page(path)) {
            var candidate = Candidate(page, 75.123456); var untouched = Candidate(page, 95.123456);
            var plan = Plan(path, 80.123456789, "Reviewed moved");
            Call(page, "CommitReviewedJointCandidate", candidate, path, 0, "TDR", false, plan, "AddJoint");
            var joints = Joints(page);
            Check(!joints.Any(j => Object.ReferenceEquals(j, candidate)) && joints.Any(j => Object.ReferenceEquals(j, untouched)), "only committed candidate identity removed even when position moved");
            Check(joints.Any(j => (string)Get(j, "Name") == "Reviewed moved" && (bool)Get(j, "Confirmed") && !(bool)Get(j, "AutomaticallyDetected")), "formal marker published only after explicit commit");
            Check(!(bool)Get(untouched, "Confirmed"), "unselected candidate remains pending");
            string text = File.ReadAllText(Path.Combine(path, "CABLE.in"));
            Check(text.Contains("1\t接头 1\tColdShrink\t200.123456m"), "candidate insertion never renumbers or overwrites existing manual joint");
            Check(Directory.GetFiles(path, "CABLE.before-joint-*.bak").Length == 1, "explicit confirmation retains pre-edit backup");
            Check(File.ReadAllBytes(Path.Combine(path, "protected.raw")).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "raw acquisition never modified");
        }
        path = Fixture(root, "simulation"); byte[] real = File.ReadAllBytes(Path.Combine(path, "CABLE.in"));
        using (var page = Page(path, true)) {
            var candidate = Candidate(page, 65.123456); Call(page, "CommitReviewedJointCandidate", candidate, path, 0, "TDR", true, Plan(path, 65.123456), "AddJoint");
            Check(real.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))) && Directory.GetFiles(path, "*.bak").Length == 0, "simulation confirmation changes markers only");
        }
    }
    static void Modal(string root, bool originallyVisible, bool cancelReview) {
        string path = Fixture(root, "modal-" + originallyVisible + "-" + cancelReview, manual: true);
        byte[] original = File.ReadAllBytes(Path.Combine(path, "CABLE.in")); byte[] item = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
        using (var page = Page(path)) using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000,-3000), ClientSize = new Size(1000,700) }) {
            object selected = Candidate(page, 80.123456789), unselected = Candidate(page, 120.123456789);
            Set(page, "JointMarkersVisible", originallyVisible); page.Dock = DockStyle.Fill; host.Controls.Add(page); host.Show(); Application.DoEvents();
            int stage = 0, ticks = 0; Exception failure = null;
            using (var timer = new Timer { Interval = 20 }) {
                timer.Tick += delegate {
                    try {
                        if (++ticks > 200) throw new Exception("Candidate modal review timed out");
                        var review = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == "CalibrationJointConfirmation");
                        var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == "CableJointDialog");
                        if (stage == 0 && review != null) {
                            Check((bool)Get(page, "JointMarkersVisible"), "review temporarily opens joint display");
                            var list = (CheckedListBox)Find(review, "CalibrationJointCandidates");
                            int index = ((IEnumerable)Field(Call(page, "GetPhaseStateNoLock", 0), "Joints")).Cast<object>().ToList().FindIndex(j => Object.ReferenceEquals(j, selected));
                            Check(!list.GetItemChecked(index), "automatic candidate starts unchecked and unconfirmed");
                            list.SetItemChecked(index, true); stage = 1; ((Button)Find(review, "ConfirmCalibrationJoints")).PerformClick();
                        } else if (stage == 1 && dialog != null) {
                            Check((bool)Get(page, "JointMarkersVisible"), "nested candidate editor keeps display visible");
                            Check(Find(dialog, "JointAttachment") is ComboBox && Find(dialog, "JointMaterialBefore") is ComboBox && Find(dialog, "JointMaterialAfter") is ComboBox, "each candidate gets actual type and both insulation fields");
                            Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))), "selection alone has not written formal joint");
                            stage = 2;
                            if (cancelReview) { ((Button)Find(dialog, "CancelJoint")).PerformClick(); }
                            else {
                                Find(dialog, "JointName").Text = "Explicit candidate"; Find(dialog, "JointAttachment").Text = "HeatShrink";
                                Find(dialog, "JointMaterialBefore").Text = "PE"; Find(dialog, "JointMaterialAfter").Text = "PVC";
                                ((Button)Find(dialog, "ConfirmJoint")).PerformClick();
                            }
                        } else if (stage == 2 && cancelReview && review != null && dialog == null) {
                            Check(!(bool)Get(selected, "Confirmed"), "cancel nested editor keeps selected candidate pending");
                            stage = 3; ((Button)Find(review, "CancelCalibrationJoints")).PerformClick(); timer.Stop();
                        }
                    } catch (Exception ex) {
                        failure = ex; timer.Stop(); foreach (Form form in Application.OpenForms.Cast<Form>().Where(f => f != host).Reverse().ToArray()) { form.DialogResult = DialogResult.Cancel; form.Close(); }
                    }
                };
                timer.Start(); Call(page, "ShowJointConfirmation"); timer.Stop();
            }
            if (failure != null) throw failure;
            Check(stage >= 2, "per-candidate modal editor was actually opened");
            Check((bool)Get(page, "JointMarkersVisible") == originallyVisible, "closing review restores original joint display state");
            Check(!(bool)Field(page, "jointDialogOpen") && !(bool)Field(page, "jointConfirmationOpen"), "both modal ownership flags released");
            Check(Joints(page).Any(j => Object.ReferenceEquals(j, unselected)) && !(bool)Get(unselected, "Confirmed"), "unchecked candidate persists after review");
            Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))), "candidate confirmation does not update cable length");
            if (cancelReview) Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "CABLE.in"))) && Directory.GetFiles(path, "*.bak").Length == 0, "cancel leaves original files and no backup changes");
            else {
                string text = File.ReadAllText(Path.Combine(path, "CABLE.in"));
                Check(text.Contains("Explicit candidate\tHeatShrink\t80.123456789m"), "explicit fields and unedited exact candidate position committed");
                Check(text.Contains("2\tBody\tPE\t") && text.Contains("2\tBody\tPVC\t"), "both user-reviewed insulation values saved");
                Check(!Joints(page).Any(j => Object.ReferenceEquals(j, selected)) && Joints(page).Any(j => (string)Get(j, "Name") == "Explicit candidate"), "confirmed candidate replaced with formal marker only after saved bytes");
            }
        }
    }
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            module = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "CalibrationTest", "Xiongxin.OWTS.CalibrationTest.dll"));
            pageType = module.GetType("HVX.Controls.CalibrationProgramControl", true); storeType = module.GetType("HVX.ProjectCables.CableJointStore", true); dialogType = module.GetType("HVX.ProjectCables.CableJointDialog", true);
            string root = Path.Combine(Path.GetFullPath(args[1]), "candidate-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            // Functional negative is the real modal missing the required fields,
            // before tests refer to any newly added method.
            Modal(root, false, false);
            DirectGuards(root); Modal(root, true, false); Modal(root, false, true); Modal(root, true, true);
            Console.WriteLine("CalibrationCandidateReviewSmoke PASS " + checks + " checks; managed fake only; hardware=false; sdk=false; userWrites=false"); return 0;
        } catch (Exception error) { Console.Error.WriteLine("CalibrationCandidateReviewSmoke FAIL after " + checks + " checks: " + error); return 1; }
    }
}
