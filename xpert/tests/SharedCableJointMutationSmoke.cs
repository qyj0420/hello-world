using System;
using System.IO;
using System.Linq;
using System.Text;
using HVX.ProjectCables;

internal static class SharedCableJointMutationSmoke
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    static string Block(string phase, string first = "XLPE", string next = "油纸")
    {
        return "0\t" + phase + "\t\t\r\n1\t人工起点\t端头原型\t0.000m\r\n2\t原始本体说明\t" + first + "\t\r\n" +
            "1\t接头 1\t接头原型\t80.000m\n2\t保留段说明\t" + next + "\t\r\n" +
            "1\t客户自定名称\t人工附件\t160m\r\n2\t本体二区\tPVC\t\r\n" +
            "1\t接头 3\t接头原型\t260m\r\n2\t本体三区\tEPR\t\r\n1\t人工终点\t端头原型\t400m\r\n";
    }
    static string Fixture(string root, string mode, string cable, Encoding encoding)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N").Substring(0, 16)); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ITEM.in"), "电缆芯数 = " + mode + "\r\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(path, "CABLE.in"), cable, encoding);
        File.WriteAllBytes(Path.Combine(path, "original.dem"), new byte[] { 1, 8, 7, 9 }); return path;
    }
    static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(Path.GetFullPath(args[0]), "jm-" + Guid.NewGuid().ToString("N").Substring(0, 12)); Directory.CreateDirectory(root);
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.GetEncoding(936), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
            {
                string path = Fixture(root, "三相单芯", Block("A相") + Block("A相", "另一A材料", "另一A后材") + Block("B相"), encoding);
                CableJointSnapshot s = CableJointStore.Read(path); byte[] original = s.Original, item = File.ReadAllBytes(Path.Combine(path, "ITEM.in"));
                Check(s.ExistingTargets(0, 1).Length == 1, "non-shared repeated A identifies exactly selected source block");
                Reject(() => s.PrepareRemove(0, 1, false), "different segment materials require explicit removal merge confirmation");
                Reject(() => s.PrepareEdit(0, 1, 220, "", "", "", false), "different-material move requires merge confirmation");
                foreach (int endpoint in new[] { 0, 4 }) { Reject(() => s.PrepareRemove(0, endpoint, true), "endpoints cannot be removed"); Reject(() => s.PrepareEdit(0, endpoint, 120, "", "", "", true), "endpoints cannot be moved"); }
                foreach (double invalid in new[] { 0, -1, 160, 400, 401, Double.NaN, Double.PositiveInfinity })
                    Reject(() => s.PrepareEdit(0, 1, invalid, "", "", "", true), "invalid or duplicate joint position rejected");
                Reject(() => s.PrepareEdit(0, 1, 100, "客户自定名称", "", "", true), "duplicate human name rejected");
                Reject(() => s.PrepareEdit(0, 1, 100, "unsafe\nname", "", "", true), "line injection rejected");
                CableJointPlan plan = s.PrepareEdit(0, 1, 220, "", "修正附件", "", true);
                Check(original.SequenceEqual(File.ReadAllBytes(s.CablePath)), "preparing edit has no side effects");
                string backup = CableJointStore.Commit(plan); s = CableJointStore.Read(path);
                Check(File.ReadAllBytes(backup).SequenceEqual(original), "edit stores exact byte backup " + encoding.WebName);
                Check(s.Encoding.CodePage == encoding.CodePage && s.Preamble.SequenceEqual(encoding.GetPreamble()), "source encoding and BOM preserved");
                Check(s.Blocks[0].Positions.SequenceEqual(new[] { 0d, 160, 220, 260, 400 }), "cross-joint move orders actual coordinates");
                string result = s.Encoding.GetString(s.Original.Skip(s.Preamble.Length).ToArray());
                Check(result.EndsWith(Block("A相", "另一A材料", "另一A后材") + Block("B相"), StringComparison.Ordinal), "other duplicate A and B remain byte text identical");
                Check(result.Contains("1\t客户自定名称\t人工附件\t160m\r\n2\t本体二区\tPVC\t\r\n1\t接头 2\t修正附件\t220m\n2\t保留段说明\t油纸\t\r\n"), "following material travels with moved joint, unrelated materials and mixed line endings survive");
                Check(result.StartsWith("0\tA相\t\t\r\n1\t人工起点\t端头原型\t0.000m\r\n2\t原始本体说明\tXLPE\t\r\n", StringComparison.Ordinal), "original start row and preceding material remain exact");
                byte[] beforeRemove = s.Original; plan = s.PrepareRemove(0, 1, true); backup = CableJointStore.Commit(plan); s = CableJointStore.Read(path);
                Check(File.ReadAllBytes(backup).SequenceEqual(beforeRemove), "delete stores exact byte backup");
                Check(s.Blocks[0].Positions.SequenceEqual(new[] { 0d, 220, 260, 400 }), "one selected human-named interior joint removed");
                Check(s.Lines[s.Blocks[0].BodyLines[0]].Contains("XLPE"), "merged original interval explicitly retains preceding material");
                Check(s.Lines[s.Blocks[0].AccessoryLines[1]].Split('\t')[1] == "接头 1" && s.Lines[s.Blocks[0].AccessoryLines[2]].Split('\t')[1] == "接头 2", "reserved automatic names renumber in spatial order");
                Check(s.Blocks.Count == 3 && s.Blocks[0].Phase == "A" && s.Blocks[1].Phase == "A" && s.Blocks[2].Phase == "B", "phase labels never synthesized");
                Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "ITEM.in"))) && File.ReadAllBytes(Path.Combine(path, "original.dem")).SequenceEqual(new byte[] { 1, 8, 7, 9 }), "project parameters and raw data untouched");
                Reject(() => CableJointStore.Commit(plan), "stale removal plan rejected");
                plan = s.PrepareEdit(0, 1, 230, "", "", "", true); File.AppendAllText(Path.Combine(path, "ITEM.in"), "external=1\r\n");
                byte[] concurrent = File.ReadAllBytes(s.CablePath); Reject(() => CableJointStore.Commit(plan), "concurrent ITEM edit rejected");
                Check(concurrent.SequenceEqual(File.ReadAllBytes(s.CablePath)), "ITEM conflict does not mutate cable");
                s = CableJointStore.Read(path); plan = s.PrepareRemove(0, 1, true);
                File.SetAttributes(s.CablePath, File.GetAttributes(s.CablePath) | FileAttributes.ReadOnly);
                try { backup = CableJointStore.Commit(plan); Check(File.ReadAllBytes(backup).SequenceEqual(concurrent), "read-only edit retains original byte backup"); Check((File.GetAttributes(s.CablePath) & FileAttributes.ReadOnly) != 0, "original read-only attribute is restored after saving"); }
                finally { File.SetAttributes(s.CablePath, FileAttributes.Normal); }
                Check(!CableJointStore.Read(path).Blocks[0].Positions.Contains(220), "read-only project permits the explicitly requested joint edit");
            }
            string shared = Fixture(root, "三芯", Block("A相") + Block("A相", "异材1", "异材2") + Block("A相"), new UTF8Encoding(false));
            CableJointSnapshot common = CableJointStore.Read(shared);
            Check(common.ExistingTargets(1, 2).Length == 3, "shared three-core identifies corresponding node in all actual blocks");
            CableJointStore.Commit(common.PrepareEdit(1, 2, 190, "", "", "", true)); common = CableJointStore.Read(shared);
            Check(common.Blocks.All(b => b.Positions.Contains(190)) && common.Blocks.All(b => b.Phase == "A"), "shared edit updates all corresponding nodes while keeping repeated A labels");
            Check(common.Lines.Any(l => l.Contains("异材1")) && common.Lines.Any(l => l.Contains("异材2")), "shared operation retains per-block distinct materials");
            CableJointStore.Commit(common.PrepareRemove(0, 2, true)); common = CableJointStore.Read(shared);
            Check(common.Blocks.All(b => !b.Positions.Contains(190)), "shared delete updates all blocks");
            string inconsistent = Fixture(root, "三芯", Block("A相") + Block("A相").Replace("80.000m", "85m"), new UTF8Encoding(false));
            common = CableJointStore.Read(inconsistent); byte[] unchanged = common.Original;
            Reject(() => common.PrepareRemove(0, 1, true), "inconsistent shared blocks explicitly rejected without guessing scope");
            Reject(() => common.PrepareEdit(0, 1, 120, "", "", "", true), "inconsistent shared edit rejected");
            Check(unchanged.SequenceEqual(File.ReadAllBytes(common.CablePath)), "inconsistent shared structure remains untouched");
            Console.WriteLine("SHARED_CABLE_JOINT_MUTATION_PASS=" + checks + "; EVIDENCE=" + root); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
