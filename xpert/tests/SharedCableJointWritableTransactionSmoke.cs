using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HVX.ProjectCables;

internal static class SharedCableJointWritableTransactionSmoke
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static Exception Reject(Action action, string message)
    {
        Exception failure = null;
        try { action(); }
        catch (InvalidDataException ex) { failure = ex; }
        catch (IOException ex) { failure = ex; }
        catch (UnauthorizedAccessException ex) { failure = ex; }
        catch (InvalidOperationException ex) { failure = ex; }
        catch (AggregateException ex) { failure = ex; }
        Check(failure != null, message); return failure;
    }
    static string Block(string phase, string before, string after, string far)
    {
        return "0\t" + phase + "\t\t\r\n1\t始端\t冷缩\t0.000m\r\n2\t原前体\t" + before + "\t\r\n" +
            "1\t接头 1\t原附件\t80.123m\n2\t原后体\t" + after + "\t\r\n" +
            "1\t人工接头\t保留附件\t160m\r\n2\t远段\t" + far + "\t\r\n1\t终端\t冷缩\t400m\r\n";
    }
    static string Fixture(string root, string name, string mode, string text)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "电缆芯数 = " + mode + "\r\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "CABLE.in"), text, new UTF8Encoding(true));
        File.WriteAllBytes(Path.Combine(path, "original.dem"), new byte[] { 6, 1, 8, 9 }); return path;
    }
    static CableJointPlan Append(CableJointSnapshot snapshot)
    { return snapshot.Prepare(snapshot.PhaseChoices, 250, "验证新增", "预制接头", "", ""); }
    static string Material(CableJointSnapshot snapshot, int block, int body)
    { return snapshot.Lines[snapshot.Blocks[block].BodyLines[body]].Split('\t')[2]; }
    static void Intact(CableJointSnapshot snapshot, FileAttributes flags, string message)
    {
        Check(File.GetAttributes(snapshot.CablePath) == flags, message + " exact attributes");
        Check(File.ReadAllBytes(snapshot.CablePath).SequenceEqual(snapshot.Original), message + " bytes");
        Check(Directory.GetFiles(Path.GetDirectoryName(snapshot.CablePath), ".cj-*.tmp").Length == 0, message + " staging cleanup");
    }
    static void CheckSaved(string path, CableJointSnapshot old, CableJointPlan plan, FileAttributes flags, string label)
    {
        string acl = File.GetAccessControl(old.CablePath, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        string backup = CableJointStore.Commit(plan);
        Check(File.GetAttributes(old.CablePath) == flags, label + " restores exact original flags");
        Check(File.GetAccessControl(old.CablePath, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) == acl, label + " retains original ACL");
        Check(File.ReadAllBytes(backup).SequenceEqual(old.Original), label + " exact byte backup including BOM/endings");
        Check(File.ReadAllBytes(old.CablePath).SequenceEqual(plan.Replacement), label + " expected replacement");
        Check(File.ReadAllBytes(Path.Combine(path, "original.dem")).SequenceEqual(new byte[] { 6, 1, 8, 9 }), label + " raw data untouched");
        Check(CableJointStore.Read(path).Blocks.Count == old.Blocks.Count, label + " rereads strict structure");
    }
    static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(Path.GetFullPath(args[0]), "joint-write-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(root);
            string source = Block("A", "XLPE", "油纸", "PVC");
            foreach (bool readOnly in new[] { false, true })
            {
                string path = Fixture(root, readOnly ? "sealed" : "writable", "单相", source);
                string cable = Path.Combine(path, "CABLE.in");
                File.SetAttributes(cable, FileAttributes.Archive | FileAttributes.Hidden | (readOnly ? FileAttributes.ReadOnly : 0));
                FileAttributes flags = File.GetAttributes(cable);
                var snapshot = CableJointStore.Read(path); var plan = Append(snapshot);
                Intact(snapshot, flags, "prepare does not unlock");
                CheckSaved(path, snapshot, plan, flags, "append " + readOnly);
                snapshot = CableJointStore.Read(path);
                plan = snapshot.PrepareEdit(0, 1, 90, "", "", "前修订", "后修订", true);
                CheckSaved(path, snapshot, plan, flags, "edit " + readOnly);
                snapshot = CableJointStore.Read(path); plan = snapshot.PrepareRemove(0, 1, true);
                CheckSaved(path, snapshot, plan, flags, "remove " + readOnly);
                // No file from a real project is involved. Leave evidence sealed
                // exactly as this test originally created it.
            }

            string faultPath = Fixture(root, "replace-failure", "单相", source);
            var fault = CableJointStore.Read(faultPath); var faultPlan = Append(fault);
            File.SetAttributes(fault.CablePath, FileAttributes.Archive | FileAttributes.ReadOnly);
            FileAttributes faultFlags = File.GetAttributes(fault.CablePath);
            // Sharing allows attribute updates, but denies replacement/deletion.
            // This exercises the failure after the temporary read-only clear.
            using (var hold = new FileStream(fault.CablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Reject(() => CableJointStore.Commit(faultPlan), "replacement sharing violation is not swallowed");
            Intact(fault, faultFlags, "replacement failure");
            Check(Directory.GetFiles(faultPath, "CABLE.before-joint-*.bak").Length == 0, "failed replacement creates no false successful backup");

            string stalePath = Fixture(root, "stale", "单相", source);
            var stale = CableJointStore.Read(stalePath); var stalePlan = Append(stale);
            File.AppendAllText(Path.Combine(stalePath, "ITEM.in"), "revision = 2\r\n");
            File.SetAttributes(stale.CablePath, FileAttributes.ReadOnly | FileAttributes.Hidden);
            FileAttributes staleFlags = File.GetAttributes(stale.CablePath);
            // Denying attribute writes proves an ITEM/hash rejection happens
            // before any attempt to unlock, rather than unlocking then restoring.
            var sid = WindowsIdentity.GetCurrent().User;
            FileSecurity originalAcl = File.GetAccessControl(stale.CablePath, AccessControlSections.Access);
            var denyAttributes = File.GetAccessControl(stale.CablePath, AccessControlSections.Access);
            denyAttributes.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.WriteAttributes, AccessControlType.Deny));
            try
            {
                File.SetAccessControl(stale.CablePath, denyAttributes);
                // Windows may mark Archive when the ACL itself is edited.
                // Capture the real pre-commit flags after that test setup.
                staleFlags = File.GetAttributes(stale.CablePath);
                Exception staleFailure = Reject(() => CableJointStore.Commit(stalePlan), "stale plan rejected before unlock");
                Check(staleFailure is InvalidDataException && staleFailure.Message.Contains("Project changed"), "stale error is hash mismatch, not attribute permission");
                Intact(stale, staleFlags, "stale plan");
                var fresh = Append(CableJointStore.Read(stalePath));
                Exception denied = Reject(() => CableJointStore.Commit(fresh), "ACL attribute denial propagated");
                Check(denied is UnauthorizedAccessException, "genuine permission denial stays actionable");
                Intact(stale, staleFlags, "ACL denial");
            }
            finally { File.SetAccessControl(stale.CablePath, originalAcl); }
            string lockPath = Path.Combine(stalePath, ".DataBrowser.write.lock");
            using (var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Reject(() => CableJointStore.Commit(Append(CableJointStore.Read(stalePath))), "editor lock still enforced");
            Intact(stale, staleFlags, "editor lock");

            string materialsPath = Fixture(root, "dual-materials", "三芯", source + Block("A", "EPR", "胶纸", "PE"));
            var materials = CableJointStore.Read(materialsPath);
            var edited = materials.PrepareEdit(0, 1, 220, "", "新附件", "", "", true);
            Check(edited.ResolvedTargets.Select(t => t.MaterialBefore).SequenceEqual(new[] { "PVC", "PE" }), "move reports actual destination front materials per block");
            Check(edited.ResolvedTargets.Select(t => t.MaterialAfter).SequenceEqual(new[] { "油纸", "胶纸" }), "move preserves actual following materials per block");
            Check(edited.ResolvedTargets.All(t => t.PositionM == 220 && t.AttachmentType == "新附件"), "all resolved targets match requested shared move");
            Reject(() => materials.PrepareEdit(0, 1, 220, "", "", "新前", "新后", false), "explicit new materials do not bypass old-position merge confirmation");
            edited = materials.PrepareEdit(0, 1, 220, "", "新附件", "新前", "新后", true);
            Check(edited.ResolvedTargets.All(t => t.MaterialBefore == "新前" && t.MaterialAfter == "新后"), "explicit dual materials reflected in plan");
            CableJointStore.Commit(edited); materials = CableJointStore.Read(materialsPath);
            foreach (int block in new[] { 0, 1 })
            {
                Check(materials.Blocks[block].Positions.SequenceEqual(new[] { 0d, 160, 220, 400 }), "shared move correctly sorted");
                Check(Material(materials, block, 1) == "新前" && Material(materials, block, 2) == "新后", "dual materials write at final sorted adjacent segments");
            }
            Check(Material(materials, 0, 0) == "XLPE" && Material(materials, 1, 0) == "EPR", "old merged intervals retain each original preceding material");
            Check(materials.Blocks.All(b => b.Phase == "A"), "shared repeated A never relabeled");
            var legacy = materials.PrepareEdit(0, 2, 220, "", "兼容附件", "兼容后材", true);
            Check(legacy.ResolvedTargets.All(t => t.MaterialBefore == "新前" && t.MaterialAfter == "兼容后材"), "legacy seven-argument overload preserves front material");
            CableJointStore.Commit(legacy); materials = CableJointStore.Read(materialsPath);
            var frontOnly = materials.PrepareEdit(0, 2, 220, "", "", "仅前段", "", false);
            Check(frontOnly.ResolvedTargets.All(t => t.MaterialBefore == "仅前段" && t.MaterialAfter == "兼容后材"), "stationary front-only edit preserves actual following material");
            var afterOnly = materials.PrepareEdit(0, 2, 220, "", "", "", "仅后段", false);
            Check(afterOnly.ResolvedTargets.All(t => t.MaterialBefore == "新前" && t.MaterialAfter == "仅后段"), "stationary after-only edit preserves actual front material");
            Check(File.ReadAllBytes(materials.CablePath).SequenceEqual(materials.Original), "uncommitted side-only edits leave source untouched");
            var backwards = materials.PrepareEdit(0, 2, 40, "", "", "", "", true);
            Check(backwards.ResolvedTargets.Select(t => t.MaterialBefore).SequenceEqual(new[] { "XLPE", "EPR" }), "backward move resolves actual front by new ordering");
            Check(backwards.ResolvedTargets.All(t => t.MaterialAfter == "兼容后材"), "backward move keeps per-joint following material");
            foreach (string invalid in new[] { "bad\nmaterial", "bad\tmaterial", new string('x', 81) })
                Reject(() => materials.PrepareEdit(0, 2, 220, "", "", invalid, "", true), "unsafe front material rejected");
            var originalBytes = materials.Original;
            materials.PrepareEdit(0, 2, 220, "", "", "未提交", "未提交", true);
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(materials.CablePath)), "preparing/cancelling dual-material edit never writes");
            Console.WriteLine("SHARED_CABLE_JOINT_WRITABLE_TRANSACTION_PASS=" + checks + "; EVIDENCE=" + root); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
