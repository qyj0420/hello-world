using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

// Diagnostic repros only, NOT a release acceptance test. Never opens hardware.
internal static class ProjectUiAudit117ExpansionProbe
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Assembly creation, management; static Type controlType, operations, scanner; static string qa;
    static readonly List<string> lines = new List<string>(); static int reproduced;
    static object Get(object o, string n) { var p = o.GetType().GetProperty(n, All); return p != null ? p.GetValue(o, null) : o.GetType().GetField(n, All).GetValue(o); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, All).SetValue(o, v, null); }
    static object Call(object o, string n, params object[] args)
    {
        Type t = o as Type ?? o.GetType();
        try { return t.GetMethods(All).Single(m => m.Name == n && m.GetParameters().Length == args.Length).Invoke(o is Type ? null : o, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static void Require(bool v, string message) { if (!v) throw new Exception(message); }
    static void Case(string id, Action action) { try { action(); reproduced++; lines.Add(id + " REPRODUCED"); } catch (Exception e) { lines.Add(id + " NOT_REPRODUCED " + e); } }
    static Control Page(string name)
    {
        var p = (Control)Activator.CreateInstance(controlType);
        Set(p, "ShowDialogMessages", false); Set(p, "EmbeddedMode", true); Set(p, "OnlineWeatherEnabled", false); Set(p, "AutoRefreshWeatherOnHardwareSample", false);
        Set(p, "SettingsPath", Path.Combine(qa, name + ".ini")); Set(p, "DataRootDirectory", qa); return p;
    }
    static string Block(string phase, bool joint)
    { return "0\t" + phase + "\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n" + (joint ? "1\t人工接头\t户内冷缩\t80m\r\n2\t本体\t油纸\t\r\n" : "") + "1\t终端\t户内冷缩\t400m\r\n"; }
    static string Fixture(string name, string mode = "单相", bool joint = false, string extra = "")
    {
        string p = Path.Combine(qa, name); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n所属单位 = OriginalOrg\r\n始端地点 = QA start\r\n终端地点 = QA end\r\n电缆芯数 = " + mode + "\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\nB相电缆长度 = " + (mode == "单相" ? "0" : "400") + "\r\nC相电缆长度 = " + (mode == "单相" ? "0" : "400") + "\r\n" + extra, Encoding.UTF8);
        File.WriteAllText(Path.Combine(p, "CABLE.in"), Block("A相", joint) + (mode == "单相" ? "" : Block("B相", joint) + Block("C相", joint)), Encoding.UTF8); return p;
    }
    static object Record(string p) { return Call(scanner, "ReadProject", p); }
    static Array Records(params string[] paths) { Array result = Array.CreateInstance(management.GetType("HVX.Controls.ProjectRecord", true), paths.Length); for (int i = 0; i < paths.Length; i++) result.SetValue(Record(paths[i]), i); return result; }
    static string Read(string p, string n) { byte[] b = File.ReadAllBytes(Path.Combine(p, n)); try { return new UTF8Encoding(false, true).GetString(b); } catch { return Encoding.GetEncoding(936).GetString(b); } }
    [STAThread] static int Main(string[] args)
    {
        Application.EnableVisualStyles(); qa = Path.Combine(Path.GetFullPath(args[2]), "project-ui-expanded-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(qa);
        creation = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]), "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
        management = Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[1]), "modules", "ProjectManagement", "Xiongxin.OWTS.ProjectManagement.dll"));
        controlType = creation.GetType("HVX.Controls.ProjectCreationControl", true); scanner = management.GetType("HVX.Controls.ProjectScanner", true); operations = management.GetType("HVX.Controls.ProjectFileOperations", true);
        Case("J1 shared three-core addition only writes A", delegate {
            string p = Fixture("shared-add", "一体三芯"); using (var page = Page("shared")) {
                Require((bool)Call(page, "LoadProject", p), "load"); Require((bool)Call(page, "AddJoint", "A相", "户内冷缩", "油纸", 80.0), "add");
                Require((bool)Call(page, "SaveProject"), "save: " + Get(page, "LastError"));
                string[] blocks = Read(p, "CABLE.in").Split(new[] { "0\t" }, StringSplitOptions.RemoveEmptyEntries);
                int[] counts = blocks.Select(b => b.Split(new[] { "\r\n" }, StringSplitOptions.None).Count(l => l.StartsWith("1\t")) - 2).ToArray();
                lines.Add("J1 persisted per-block interior counts=" + string.Join(",", counts)); Require(counts.SequenceEqual(new[] { 1, 0, 0 }), "expected mismatched shared block counts");
            }
        });
        Case("J2 failed load contaminates old project draft", delegate {
            string p = Fixture("old-project"), bad = Fixture("bad-new-project", extra: "温度 = NaN\r\n");
            string badItem = Read(bad, "ITEM.in").Replace("OriginalOrg", "IncomingBadOrg"); File.WriteAllText(Path.Combine(bad, "ITEM.in"), badItem, Encoding.UTF8);
            using (var page = Page("badload")) {
                Require((bool)Call(page, "LoadProject", p), "initial load"); Require(!(bool)Call(page, "LoadProject", bad), "bad load should fail");
                Require((string)Get(page, "CurrentProjectPath") == p && (string)Get(page, "Organization") == "IncomingBadOrg", "old path + new partial draft");
                lines.Add("J2 failed load retains old path=" + Get(page, "CurrentProjectPath") + " but organization=" + Get(page, "Organization") + "; dirty=" + Get(page, "IsDirty"));
                Set(page, "Notes", "subsequent old-project notes"); Require((bool)Call(page, "SaveProject"), "subsequent save");
                Require(Read(p, "ITEM.in").Contains("IncomingBadOrg"), "wrong org persisted into old project");
            }
        });
        Case("J3 literal quotation changed during metadata save", delegate {
            string p = Fixture("quotes", extra: "VendorLiteral = \"A \"quoted\" B\"\r\n"); File.WriteAllBytes(Path.Combine(p, "raw.dem"), new byte[] { 1, 2, 3 });
            using (var page = Page("quotes")) { Require((bool)Call(page, "LoadProject", p), "load quotes"); Set(page, "Notes", "ordinary note"); Require((bool)Call(page, "SaveProject"), "save"); }
            string line = Read(p, "ITEM.in").Split('\n').Single(s => s.Contains("VendorLiteral")); lines.Add("J3 persisted unknown field=" + line.Trim()); Require(line.Contains("A ''quoted'' B"), "quote substitution");
        });
        Case("J4 legacy hardware sample reported successful but invalid after load", delegate {
            string p = Fixture("legacy-sample"); using (var page = Page("sample")) {
                Require((bool)Call(page, "LoadProject", p), "load"); Require((bool)Call(page, "ApplyHardwareSample", 23.4, 55.6, 39.9, 116.4, 50.0, "定位有效", new DateTime(2026, 9, 30, 1, 2, 3)), "legacy apply succeeds");
                Require(!(bool)Get(page, "HasValidEnvironmentSample") && !(bool)Get(page, "HasValidGnssFix"), "success does not set validity");
                Require((bool)Call(page, "SaveProject"), "save"); Require((bool)Call(page, "LoadProject", p), "reload");
                string latitude = ((Label)Get(page, "latitudeValue")).Text; lines.Add("J4 valid flags false; reload latitude=" + latitude + "; temperature control text=" + ((NumericUpDown)Get(page, "temperatureNumber")).Text);
                Require(latitude == "—", "saved legacy fix hidden after reload");
            }
        });
        Case("J5 unmeasured different-material deletion has no confirmation", delegate {
            string p = Fixture("merge-material", joint: true); using (var page = Page("merge")) {
                Require((bool)Call(page, "LoadProject", p), "load"); Require((bool)Call(page, "RemoveJoint", "A相", 0), "delete without material choice");
                Require(string.IsNullOrEmpty((string)Get(page, "LastJointChangeConfirmation")), "no merge confirmation constructed"); Require((bool)Call(page, "SaveProject"), "save");
                string cable = Read(p, "CABLE.in"); lines.Add("J5 former oil-paper segment lost; no confirmation=" + string.IsNullOrEmpty((string)Get(page, "LastJointChangeConfirmation"))); Require(!cable.Contains("油纸"), "following material lost");
            }
        });
        Case("J6 drive-root export resolves to per-drive working directory", delegate {
            string drive = Path.GetPathRoot(Environment.CurrentDirectory), normalized = (string)Call(operations, "NormalizeDestination", drive);
            string actual = (string)Call(operations, "SafeCombine", normalized, "QA-no-write");
            lines.Add("J6 chosen=" + drive + "; normalized=" + normalized + "; planned=" + actual); Require(!string.Equals(actual, Path.Combine(drive, "QA-no-write"), StringComparison.OrdinalIgnoreCase), "drive root incorrectly rewritten");
        });
        Case("J7 batch export partial completion is reported as zero", delegate {
            string p1 = Fixture("export-one"), p2 = Fixture("export-two"), locked = Path.Combine(p2, "sample.dem"), target = Path.Combine(qa, "exports"); File.WriteAllBytes(locked, new byte[] { 1, 2, 3 });
            object result; using (var gate = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) result = Call(operations, "ExportProjects", Records(p1, p2), target);
            int written = Directory.GetFiles(target, "ITEM.in", SearchOption.AllDirectories).Length;
            lines.Add("J7 resultOk=" + Get(result, "Ok") + "; resultCount=" + Get(result, "ProcessedCount") + "; actualProjects=" + written + "; resultPaths=" + ((string[])Get(result, "OutputPaths")).Length);
            Require(!(bool)Get(result, "Ok") && (int)Get(result, "ProcessedCount") == 0 && written == 1, "partial result hidden");
        });
        Case("J8 failed recursive deletion destroys part of failed project", delegate {
            string p = Fixture("delete-partial"), locked = Path.Combine(p, "zz-readonly.dem");
            Require(Path.GetFullPath(p).StartsWith(qa + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "isolated delete scope");
            File.WriteAllBytes(locked, new byte[] { 1, 2, 3 }); File.SetAttributes(locked, FileAttributes.ReadOnly);
            try {
                object result = Call(operations, "DeleteProjects", qa, Records(p));
                bool item = File.Exists(Path.Combine(p, "ITEM.in")), cable = File.Exists(Path.Combine(p, "CABLE.in"));
                lines.Add("J8 reported deleted=" + Get(result, "ProcessedCount") + "; itemExists=" + item + "; cableExists=" + cable + "; rawExists=" + File.Exists(locked));
                Require(!(bool)Get(result, "Ok") && (int)Get(result, "ProcessedCount") == 0 && (!item || !cable) && File.Exists(locked), "partial destruction in uncounted failed directory");
            } finally { if (File.Exists(locked)) File.SetAttributes(locked, FileAttributes.Normal); }
        });
        File.WriteAllLines(Path.Combine(qa, "evidence.txt"), lines, Encoding.UTF8);
        foreach (string line in lines) Console.WriteLine(line);
        Console.WriteLine("AUDIT_REPRODUCED=" + reproduced + "; NOT_A_RELEASE_PASS; HARDWARE=False; EVIDENCE=" + qa); return 0;
    }
}
