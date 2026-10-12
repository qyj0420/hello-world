using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

internal static class ProjectSaveTransactionSmoke
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static Type storage, catalogType, controlType;
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static object Call(object owner, string method, params object[] args) {
        Type t = owner as Type ?? owner.GetType();
        try { return t.GetMethods(F).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    static object Get(object o, string n) { return o.GetType().GetProperty(n, F).GetValue(o, null); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, F).SetValue(o, v, null); }
    static void Field(object o, string n, object v) { o.GetType().GetField(n, F).SetValue(o, v); }
    static Exception Failure(Action action) { try { action(); } catch (Exception e) { return e; } throw new Exception("Expected a refusal/failure."); }
    static string Hash(byte[] bytes) { using (var s = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(s.ComputeHash(bytes)); }
    static byte[] Bytes(string p, string name) { return File.ReadAllBytes(Path.Combine(p, name)); }
    static bool Same(byte[] a, byte[] b) { return a.SequenceEqual(b); }
    static string Fixture(string root, string name, bool duplicate) {
        string p = Path.Combine(root, name); Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "ITEM.in"), "[ITEM]\r\n始端地点 = QA start\r\n终端地点 = QA end\r\n电缆芯数 = 一体三芯\r\n相电压 = 8.7kV\r\nA相电缆长度 = 400\r\nB相电缆长度 = 400\r\nC相电缆长度 = 400\r\nVendorCalibrationVersion = preserve-me\r\n", Encoding.UTF8);
        string block = "0\tA相\t\t\r\n1\t始端\t户内冷缩\t0m\r\n2\t本体\t塑料绝缘\t\r\n1\t接头 1\t户内冷缩\t80m\r\n2\t本体\t橡胶绝缘\t\r\n1\t终端\t户内冷缩\t400m\r\n";
        File.WriteAllText(Path.Combine(p, "CABLE.in"), block + (duplicate ? block.Replace("塑料绝缘", "PVC").Replace("橡胶绝缘", "XLPE") : ""), Encoding.UTF8);
        return p;
    }
    static Control Page(string ini) {
        var page = (Control)Activator.CreateInstance(controlType);
        Set(page, "ShowDialogMessages", false); Set(page, "OnlineWeatherEnabled", false);
        Set(page, "AutoRefreshWeatherOnHardwareSample", false); Set(page, "SettingsPath", ini);
        return page;
    }
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa = Path.Combine(Path.GetFullPath(args[1]), "project-save-" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(qa);
            var assembly = Assembly.LoadFrom(Path.Combine(args[0], "modules", "ProjectCreation", "Xiongxin.OWTS.ProjectCreation.dll"));
            storage = assembly.GetType("HVX.Controls.ProjectCreationStorage", true); catalogType = assembly.GetType("HVX.Controls.ReferenceCatalog", true); controlType = assembly.GetType("HVX.Controls.ProjectCreationControl", true);
            string p = Fixture(qa, "second-file", false); object draft = Call(storage, "Load", p);
            byte[] item = Bytes(p, "ITEM.in"), cable = Bytes(p, "CABLE.in");
            Field(draft, "Notes", "new notes"); Field(draft, "LengthA", 450.0); Field(draft, "LengthB", 450.0); Field(draft, "LengthC", 450.0);
            Exception failure;
            using (var gate = new FileStream(Path.Combine(p, "CABLE.in"), FileMode.Open, FileAccess.Read, FileShare.Read))
                failure = Failure(() => Call(storage, "UpdateExisting", draft, p));
            Check(Same(item, Bytes(p, "ITEM.in")) && Same(cable, Bytes(p, "CABLE.in")), "second-file failure restores exact first bytes");
            Check(failure.Message.Contains("已回滚") && failure.Message.Contains(".bak"), "failure explains rollback and backup");
            Check(Directory.GetFiles(p, "*.bak").Any(file => Same(item, File.ReadAllBytes(file))), "original backup retained");
            Check(Directory.GetFiles(p, "*.tmp").Length == 0, "owned pending files cleaned");
            Check((string)Call(storage, "UpdateExisting", draft, p) == p, "retry after rollback succeeds using original baseline");
            Check(!Same(item, Bytes(p, "ITEM.in")) && !Same(cable, Bytes(p, "CABLE.in")), "retry commits both files");
            foreach (string changed in new[] { "ITEM.in", "CABLE.in" }) {
                p = Fixture(qa, "stale-" + changed, false); draft = Call(storage, "Load", p);
                File.AppendAllText(Path.Combine(p, changed), changed == "ITEM.in" ? "ExternalOwner = keep\r\n" : "\r\n", Encoding.UTF8);
                item = Bytes(p, "ITEM.in"); cable = Bytes(p, "CABLE.in");
                failure = Failure(() => Call(storage, "UpdateExisting", draft, p));
                Check(failure.Message.Contains("重新加载"), "stale " + changed + " rejected");
                Check(Same(item, Bytes(p, "ITEM.in")) && Same(cable, Bytes(p, "CABLE.in")), "stale refusal preserves both files");
                Check(Directory.GetFiles(p, "*.bak").Length == 0, "stale fails before commit");
            }
            p = Fixture(qa, "rollback-external", false); item = Bytes(p, "ITEM.in"); cable = Bytes(p, "CABLE.in");
            byte[] replacement = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(item) + "Changed = yes\r\n"), external = Encoding.UTF8.GetBytes("external metadata must survive");
            Action<int> changeExternal = index => { File.WriteAllBytes(Path.Combine(p, "ITEM.in"), external); throw new IOException("fixture later failure"); };
            failure = Failure(() => Call(storage, "CommitProjectPair", p, replacement, cable, Hash(item), Hash(cable), changeExternal));
            Check(Same(external, Bytes(p, "ITEM.in")), "rollback never overwrites newer external content");
            Check(failure.Message.Contains("未能完全回滚") && failure.Message.Contains(".bak"), "partial rollback explicit with recovery backup");
            Check(Directory.GetFiles(p, "*.bak").Any(file => Same(item, File.ReadAllBytes(file))), "external race retains original recovery backup");

            p = Fixture(qa, "readonly", false); draft = Call(storage, "Load", p); Field(draft, "Notes", "never written"); item = Bytes(p, "ITEM.in");
            File.SetAttributes(Path.Combine(p, "ITEM.in"), FileAttributes.ReadOnly);
            try { Failure(() => Call(storage, "UpdateExisting", draft, p)); Check((File.GetAttributes(Path.Combine(p, "ITEM.in")) & FileAttributes.ReadOnly) != 0, "ordinary save never removes readonly"); }
            finally { File.SetAttributes(Path.Combine(p, "ITEM.in"), FileAttributes.Normal); }
            Check(Same(item, Bytes(p, "ITEM.in")), "readonly original untouched");

            string ini = Path.Combine(qa, "references.ini"); Call(Call(catalogType, "CreateDefaults"), "Save", ini);
            using (var page = Page(ini)) {
                Check((bool)Call(page, "ReloadSettings"), "private valid catalog loaded");
                p = Fixture(qa, "ui-duplicate", true); cable = Bytes(p, "CABLE.in");
                Check((bool)Call(page, "LoadProject", p), "duplicate phase fixture load"); Set(page, "Notes", "updated notes");
                bool duplicateSaved = (bool)Call(page, "SaveProject");
                Check(duplicateSaved, "notes-only duplicate phase save succeeds: " + Get(page, "LastError"));
                Check(Same(cable, Bytes(p, "CABLE.in")), "notes-only preserves every cable byte and independent duplicate blocks");
                Check(File.ReadAllText(Path.Combine(p, "ITEM.in"), Encoding.GetEncoding(936)).Contains("preserve-me"), "unknown ITEM key retained by UI save");
                item = Bytes(p, "ITEM.in"); Set(page, "Notes", "success despite cache error"); File.SetAttributes(ini, FileAttributes.ReadOnly);
                try { Check((bool)Call(page, "SaveProject"), "cache failure after commit returns success"); }
                finally { File.SetAttributes(ini, FileAttributes.Normal); }
                Check(!(bool)Get(page, "IsDirty") && !(bool)Get(page, "IsBusy"), "successful project commit becomes clean and idle");
                Check(!Same(item, Bytes(p, "ITEM.in")) && !String.IsNullOrWhiteSpace((string)Get(page, "LastProjectSaveWarning")), "committed data plus explicit cache warning");
                Check((bool)Call(page, "SaveProject"), "retry cache works with current committed baseline");
                Check(String.IsNullOrEmpty((string)Get(page, "LastProjectSaveWarning")), "warning resets after successful cache update");
                draft = Call(storage, "Load", p); Field(draft, "LengthA", 460.0);
                item = Bytes(p, "ITEM.in"); failure = Failure(() => Call(storage, "UpdateExisting", draft, p));
                Check(failure.Message.Contains("重复相别") && Same(item, Bytes(p, "ITEM.in")) && Same(cable, Bytes(p, "CABLE.in")), "ambiguous duplicate-block geometry changes refused losslessly");
                File.WriteAllText(ini, "[Materials]\r\nitem=CORRUPT-CUSTOM|not-a-number\r\n", Encoding.UTF8); byte[] corrupt = File.ReadAllBytes(ini);
                Check(!(bool)Call(page, "ReloadSettings"), "damaged catalog load explicitly fails");
                Check((bool)Call(page, "LoadProject", p), "project remains usable with blocked damaged cache");
                Check(Same(corrupt, File.ReadAllBytes(ini)), "load project cannot overwrite damaged catalog");
                Set(page, "Notes", "saved despite damaged cache"); Check((bool)Call(page, "SaveProject"), "project save succeeds with damaged cache warning");
                Check(Same(corrupt, File.ReadAllBytes(ini)) && ((string)Get(page, "LastProjectSaveWarning")).Contains("原件"), "damaged original and diagnostic remain visible");
                object catalog = Call(catalogType, "Load", ini); Failure(() => Call(catalog, "Save", ini));
                string backup = (string)Call(catalog, "RecoverAndSave", ini);
                Check(File.Exists(backup) && Same(corrupt, File.ReadAllBytes(backup)), "explicit recovery retains exact damaged original");
                Check(!(bool)Get(Call(catalogType, "Load", ini), "RecoveryRequired"), "explicit recovery yields a readable catalog");
                Check((bool)Call(page, "ReloadSettings"), "reload accepts repaired catalog");
                File.WriteAllText(ini, "[Materials]\r\nitem=bad|NaN\r\n", Encoding.UTF8); catalog = Call(catalogType, "Load", ini);
                File.AppendAllText(ini, "; concurrent update\r\n", Encoding.UTF8); corrupt = File.ReadAllBytes(ini);
                Failure(() => Call(catalog, "RecoverAndSave", ini)); Check(Same(corrupt, File.ReadAllBytes(ini)), "recovery rejects concurrent change without overwrite");
            }
            Console.WriteLine("PROJECT_SAVE_TRANSACTION_PASS=" + checks + "; hardware=false; QA=" + qa); return 0;
        }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}
