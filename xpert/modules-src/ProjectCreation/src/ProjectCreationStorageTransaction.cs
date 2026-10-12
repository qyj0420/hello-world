using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HVX.ProjectCables;

namespace HVX.Controls
{
    internal static partial class ProjectCreationStorage
    {
        sealed class LoadedProjectBaseline
        {
            internal string Directory, ItemHash, CableHash;
        }
        static readonly ConditionalWeakTable<ProjectDraft, LoadedProjectBaseline> loadedBaselines =
            new ConditionalWeakTable<ProjectDraft, LoadedProjectBaseline>();

        static string ProjectFileHash(string path) { return BoundedProjectMetadata.HashFile(path); }

        internal static string UpdateExisting(ProjectDraft draft, string projectPath, string expectedCableHash,
            string expectedItemHash, out string committedCableHash, out string committedItemHash, bool preserveUneditedCable = false)
        {
            committedCableHash = committedItemHash = null;
            string directory = Path.GetFullPath(projectPath);
            RequireProjectBaseline(directory, expectedItemHash, expectedCableHash);
            var snapshot = CableJointStore.Read(directory);
            var currentDraft = Load(directory);
            bool cableChanged = !preserveUneditedCable && !String.Equals(BuildCable(currentDraft), BuildCable(draft), StringComparison.Ordinal);
            if (cableChanged && HasExistingTestData(directory))
                throw new InvalidOperationException("已有采集数据，不能通过普通保存改写电缆结构；请使用接头编辑。");
            if (cableChanged && snapshot.Blocks.GroupBy(block => block.Phase, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("该项目含重复相别的独立电缆段，普通结构保存无法保证原段不丢失；请使用接头编辑或另存新项目。原项目未改写。");
            // A notes-only save must not flatten duplicate phase blocks or alter
            // legacy material rows. Retain exact cable bytes unless edited.
            byte[] cableBytes = cableChanged ? EncodeProjectText(BuildCable(draft)) : snapshot.Original;
            foreach (var pair in ParseItem(ReadCompatible(Path.Combine(directory, "ITEM.in"))))
                if (!draft.UnknownItemValues.ContainsKey(pair.Key)) draft.UnknownItemValues[pair.Key] = pair.Value;
            byte[] itemBytes = BuildExistingItemBytes(directory, draft, currentDraft);
            CommitProjectPair(directory, itemBytes, cableBytes, expectedItemHash, expectedCableHash, null);
            committedItemHash = CableJointStore.Hash(itemBytes);
            committedCableHash = CableJointStore.Hash(cableBytes);
            return directory;
        }

        static void RequireProjectBaseline(string directory, string itemHash, string cableHash)
        {
            if (String.IsNullOrEmpty(itemHash) || String.IsNullOrEmpty(cableHash))
                throw new InvalidOperationException("项目状态未确认，请重新加载后保存。");
            // This reader validates ancestors, physical files and cable structure.
            var snapshot = CableJointStore.Read(directory);
            if (snapshot.ItemHash != itemHash || CableJointStore.Hash(snapshot.Original) != cableHash)
                throw new IOException("项目已被其他操作修改，请重新加载，未覆盖其他修改。");
        }

        sealed class ProjectWrite
        {
            internal string Path, Stage, Backup, BeforeHash, AfterHash, BackupHash;
            internal bool Replaced;
        }

        // The observer is a deterministic fixture seam; production always passes
        // null. Both files share the existing project writer lock. Windows has no
        // cross-file atomic replace: checked rollback handles a second-file error.
        internal static void CommitProjectPair(string directory, byte[] itemBytes, byte[] cableBytes,
            string expectedItemHash, string expectedCableHash, Action<int> afterCommit)
        {
            directory = Path.GetFullPath(directory);
            RequireProjectBaseline(directory, expectedItemHash, expectedCableHash);
            string token = Guid.NewGuid().ToString("N"), gatePath = Path.Combine(directory, ".DataBrowser.write.lock");
            var writes = new[] {
                new ProjectWrite { Path = Path.Combine(directory, "ITEM.in"), BeforeHash = expectedItemHash, AfterHash = CableJointStore.Hash(itemBytes) },
                new ProjectWrite { Path = Path.Combine(directory, "CABLE.in"), BeforeHash = expectedCableHash, AfterHash = CableJointStore.Hash(cableBytes) }
            };
            for (int i = 0; i < writes.Length; i++) {
                writes[i].Stage = Path.Combine(directory, ".p" + i + "-" + token + ".tmp");
                writes[i].Backup = Path.Combine(directory, ".p" + i + "-" + token + ".bak");
                CableJointStore.RequireWritePathLengths(writes[i].Stage, writes[i].Backup, gatePath);
            }
            if (File.Exists(gatePath) && (File.GetAttributes(gatePath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("项目编辑锁不能是链接。");
            using (var gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    RequireProjectBaseline(directory, expectedItemHash, expectedCableHash);
                    File.WriteAllBytes(writes[0].Stage, itemBytes); File.WriteAllBytes(writes[1].Stage, cableBytes);
                    RequireProjectBaseline(directory, expectedItemHash, expectedCableHash);
                    for (int i = 0; i < writes.Length; i++)
                    {
                        var write = writes[i];
                        if (write.BeforeHash == write.AfterHash) continue;
                        // Recheck both names before every commit (including the
                        // already committed counterpart), never bless a newer edit.
                        foreach (var peer in writes)
                            if ((File.GetAttributes(peer.Path) & FileAttributes.ReparsePoint) != 0 ||
                                ProjectFileHash(peer.Path) != (peer.Replaced ? peer.AfterHash : peer.BeforeHash))
                                throw new IOException("提交期间项目发生变化，停止保存。");
                        File.Replace(write.Stage, write.Path, write.Backup, false);
                        write.Replaced = true;
                        write.BackupHash = ProjectFileHash(write.Backup);
                        if (write.BackupHash != write.BeforeHash || ProjectFileHash(write.Path) != write.AfterHash)
                            throw new IOException("提交校验失败，停止保存并尝试恢复原件。");
                        if (afterCommit != null) afterCommit(i);
                    }
                    foreach (var write in writes)
                        if (ProjectFileHash(write.Path) != write.AfterHash) throw new IOException("保存后项目校验失败。");
                }
                catch (Exception failure)
                {
                    List<string> rollbackErrors = new List<string>();
                    foreach (var write in writes.Reverse().Where(value => value.Replaced))
                    {
                        try
                        {
                            if ((File.GetAttributes(write.Path) & FileAttributes.ReparsePoint) != 0 || ProjectFileHash(write.Path) != write.AfterHash)
                                throw new IOException("当前文件已被外部操作更改，未覆盖该更改");
                            if (String.IsNullOrEmpty(write.BackupHash)) write.BackupHash = ProjectFileHash(write.Backup);
                            if (ProjectFileHash(write.Backup) != write.BackupHash) throw new IOException("备份已改变，未自动恢复");
                            File.Copy(write.Backup, write.Stage, false);
                            if (ProjectFileHash(write.Path) != write.AfterHash) throw new IOException("恢复前文件再次改变");
                            File.Replace(write.Stage, write.Path, null, false);
                            if (ProjectFileHash(write.Path) != write.BackupHash) throw new IOException("恢复后校验失败");
                        }
                        catch (Exception rollback) { rollbackErrors.Add(Path.GetFileName(write.Path) + "：" + rollback.Message); }
                    }
                    string backups = String.Join("；", writes.Where(value => value.Replaced).Select(value => value.Backup));
                    string result = rollbackErrors.Count == 0 ? "本次已提交的修改已回滚。" : "未能完全回滚：" + String.Join("；", rollbackErrors) + "。";
                    throw new IOException("项目保存未完成。" + result + (backups.Length == 0 ? "" : " 可恢复备份：" + backups) + " 原因：" + failure.Message, failure);
                }
                finally
                {
                    foreach (var write in writes) try { if (File.Exists(write.Stage)) File.Delete(write.Stage); } catch { }
                }
            }
        }
    }
}
