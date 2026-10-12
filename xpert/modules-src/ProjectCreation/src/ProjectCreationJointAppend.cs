using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        string loadedJointCableHash, loadedJointItemHash;
        void CaptureLoadedJointMetadata()
        {
            loadedJointCableHash = loadedJointItemHash = null;
            if (String.IsNullOrWhiteSpace(loadedProjectPath)) return;
            // Loading/browsing old metadata is more permissive than editing it.
            // Do not turn an unfamiliar legacy structure into a load failure;
            // the strict shared writer will validate it if an edit is requested.
            string cable = Path.Combine(loadedProjectPath, "CABLE.in"), item = Path.Combine(loadedProjectPath, "ITEM.in");
            loadedJointCableHash = CableJointStore.Hash(File.ReadAllBytes(cable));
            loadedJointItemHash = CableJointStore.Hash(File.ReadAllBytes(item));
        }
        // Measured geometry remains locked; validated interior-joint operations
        // have their own narrow, hash-checked metadata transaction.
        bool CanAppendMeasuredJoint
        {
            get { return !canEditGeometry && HasExistingTestData && CableMode != ProjectModes.Capacitor && !String.IsNullOrWhiteSpace(loadedProjectPath); }
        }

        void OpenMeasuredJointDialog()
        {
            if (!CanAppendMeasuredJoint || IsBusy) return;
            string path = loadedProjectPath;
            try
            {
                if (GeometryChangedFromLoaded()) throw new InvalidOperationException("已采集项目的长度、芯数和原接头必须保持不变，请重新加载项目后再添加。");
                var snapshot = CableJointStore.Read(path);
                RequireLoadedJointMetadata(CableJointStore.Hash(snapshot.Original), snapshot.ItemHash);
                using (var dialog = new CableJointDialog(snapshot, Decimal.ToDouble(jointPositionNumber.Value),
                    CableJointStore.PhaseName(SelectedPhaseCanonical()), languageIndex, FindForm() == null ? null : FindForm().Icon))
                {
                    dialog.ConfigureMaterials(jointBeforeMaterialChanged ? GetChoiceValue(beforeBodyTypeCombo) : "", jointAfterMaterialChanged ? GetChoiceValue(bodyTypeCombo) : "");
                    if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Plan == null) return;
                    if (!String.Equals(path, loadedProjectPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("当前项目已经切换，未添加接头。");
                    CommitMeasuredJoint(dialog.Plan);
                }
            }
            catch (Exception ex)
            {
                CompleteOperation("AddJoint", false, ex.Message);string notice=CustomerJointFailure(ex.ToString());SetStatus(notice, false);
                if (ShowDialogMessages) MessageBox.Show(this, notice, L("接头数据无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        bool AppendMeasuredJoint(string phase, string attachment, string bodyType, double position, string beforeMaterial = "")
        {
            if (!CanAppendMeasuredJoint || IsBusy) throw new InvalidOperationException("当前项目不允许添加接头。");
            var snapshot = CableJointStore.Read(loadedProjectPath);
            RequireLoadedJointMetadata(CableJointStore.Hash(snapshot.Original), snapshot.ItemHash);
            string[] phases = snapshot.Mode == CableJointMode.SharedThreeCore ? new[] { "ALL" } : new[] { CableJointStore.PhaseName(phase) };
            string name = snapshot.SuggestedJointName(phases, position);
            var plan = snapshot.Prepare(phases, position, name, String.IsNullOrWhiteSpace(attachment) ? "中间接头" : attachment, beforeMaterial ?? "", bodyType ?? "");
            CommitMeasuredJoint(plan);
            return true;
        }

        void CommitMeasuredJoint(CableJointPlan plan)
        { CommitMeasuredJointChange(plan, plan != null && (plan.OperationCode == "EditJoint" || plan.OperationCode == "RemoveJoint") ? plan.OperationCode : "AddJoint"); }

        void CommitMeasuredJointChange(CableJointPlan plan, string operation)
        {
            if (!CanAppendMeasuredJoint || IsBusy || plan == null || GeometryChangedFromLoaded())
                throw new InvalidOperationException("电缆几何已变化或项目不可编辑，未修改接头。");
            string expected = Path.GetFullPath(Path.Combine(loadedProjectPath, "CABLE.in"));
            if (!String.Equals(expected, Path.GetFullPath(plan.CablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("接头不属于当前项目，未修改。");
            RequireLoadedJointMetadata(plan.ExpectedHash, plan.ExpectedItemHash);
            bool dirty = IsDirty;
            IsBusy = true;
            try
            {
                var committed = CableJointStore.CommitWithResult(plan);
                string backup = committed.BackupPath;
                // Reload only cable structure; preserve unrelated unsaved edits
                // in the project form rather than replacing the whole draft.
                Exception refreshFailure = null;
                try
                {
                    RefreshAfterMeasuredJoint(dirty, plan);
                }
                catch (Exception error)
                {
                    // The atomic write already succeeded. Do not invite the user
                    // to retry it as if nothing was saved; block further writes
                    // until the project is reloaded, and retain the exact backup.
                    refreshFailure = error; loadedGeometryFingerprint = "";
                    loadedJointCableHash = loadedJointItemHash = null;
                }
                lastSavedProjectPath = currentProjectPath = loadedProjectPath;
                LastSavedProjectOperationCode = "JOINT_UPDATE";
                System.Threading.Interlocked.Increment(ref pollProjectSavedSequence);
                CompleteOperation(operation, true, "");
                string action = operation == "RemoveJoint" ? "已删除接头" : operation == "EditJoint" ? "已修改接头" : "已添加接头";
                CustomerProjectMessage("Joint change committed; operation="+operation+"; backup="+backup+"; refresh="+refreshFailure+"; warning="+committed.Warning,"接头已保存。","The joint was saved.");
                string status = refreshFailure == null ? MaterialText(action+"。请重新核对既有定位分析。","The joint change was saved. Review existing location analysis again.")
                    : MaterialText("接头已保存，但页面未更新。请重新加载项目，不要重复保存。","The joint was saved, but the page did not update. Reload the project; do not save again.");
                if (!String.IsNullOrEmpty(committed.Warning)) status += " "+MaterialText("保存后的检查未完成，请重新加载项目核对。","The post-save check is incomplete. Reload the project and review it.");
                SetStatus(status, refreshFailure == null && String.IsNullOrEmpty(committed.Warning));
                RaiseSafely(ProjectSaved);
                RaiseSafely(CableStructureChanged);
                if ((refreshFailure != null || !String.IsNullOrEmpty(committed.Warning)) && ShowDialogMessages)
                    MessageBox.Show(this, status, MaterialText("接头已保存，请检查提示","Joint saved; review required"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { IsBusy = false; }
        }

        void RequireLoadedJointMetadata(string cableHash, string itemHash)
        {
            if (String.IsNullOrEmpty(loadedJointCableHash) || String.IsNullOrEmpty(loadedJointItemHash) ||
                cableHash != loadedJointCableHash || itemHash != loadedJointItemHash)
                throw new InvalidOperationException("项目已被其他操作修改，请重新加载后再修改接头；未保存的页面内容仍保留，未覆盖其他修改。");
        }

        [System.ComponentModel.Browsable(false)]
        public string LastJointChangeConfirmation { get; private set; }

        bool ChangeMeasuredJoint(JointData selected, bool remove, double position, string attachment, string material, string beforeMaterial = "")
        {
            string operation = remove ? "RemoveJoint" : "EditJoint";
            try
            {
                if (!CanAppendMeasuredJoint || IsBusy || selected == null || GeometryChangedFromLoaded())
                    throw new InvalidOperationException("当前项目状态不允许修改接头，请先重新加载。");
                var snapshot = CableJointStore.Read(loadedProjectPath);
                RequireLoadedJointMetadata(CableJointStore.Hash(snapshot.Original), snapshot.ItemHash);
                CableJointTarget[] targets = snapshot.ExistingTargets(selected.SourceBlockIndex, selected.SourceAccessoryIndex);
                CableJointTarget source = targets.Single(t => t.BlockIndex == selected.SourceBlockIndex);
                if (source.Name != selected.Name || Math.Abs(source.PositionM - selected.Position) > .000001)
                    throw new InvalidOperationException("选中接头来源已变化，请重新加载。");
                // An unchanged editor value means preserve each actual block's
                // material/type, not copy one block's value to all shared blocks.
                string requestedMaterial = material ?? "";
                string requestedBeforeMaterial = beforeMaterial ?? "";
                string requestedAttachment = attachment == selected.AttachmentType ? "" : attachment;
                CableJointPlan plan = remove ? snapshot.PrepareRemove(selected.SourceBlockIndex, selected.SourceAccessoryIndex, true)
                    : snapshot.PrepareEdit(selected.SourceBlockIndex, selected.SourceAccessoryIndex, position, "", requestedAttachment, requestedBeforeMaterial, requestedMaterial, true);
                bool chinese = Xiongxin.OWTS.Localization.UiLanguage.IsChinese(languageIndex);
                string affected = String.Join(Environment.NewLine, targets.Select(t => "  " + t.Phase + " / " + (t.BlockIndex + 1) + " · " + t.Name + " · " + JointConfirmationPosition(t.PositionM) + " m"));
                string message = (chinese ? (remove ? "确认删除中间接头？" : "确认修改中间接头？") : (remove ? "Remove the selected interior joint?" : "Edit the selected interior joint?")) + Environment.NewLine + affected + Environment.NewLine;
                message += chinese ? "影响 " + targets.Length + " 个实际结构段；始终端、总长度、原相别和原始采样文件保持不变。" : "Affected blocks: " + targets.Length + ". Endpoints, length, original phase labels and raw samples remain unchanged.";
                if (!remove) message += Environment.NewLine + (chinese ? "新位置：" : "New position: ") + JointConfirmationPosition(position) + " m";
                if (!remove && requestedAttachment.Length > 0)
                    foreach (var target in targets) message += Environment.NewLine + (chinese ? "附件类型 " : "Accessory type ") + target.Phase + " / " + (target.BlockIndex + 1) + ": " + target.AttachmentType + " → " + requestedAttachment;
                if (!remove && requestedMaterial.Length > 0)
                    foreach (var target in targets) message += Environment.NewLine + (chinese ? "接头后材料 " : "Following material ") + target.Phase + " / " + (target.BlockIndex + 1) + ": " + target.MaterialAfter + " → " + requestedMaterial;
                if (!remove && requestedBeforeMaterial.Length > 0)
                    foreach (var target in targets) message += Environment.NewLine + (chinese ? "新位置接头前绝缘 " : "Insulation before joint at new position ") + target.Phase + " / " + (target.BlockIndex + 1) + ": " + requestedBeforeMaterial;
                if (remove || Math.Abs(position - selected.Position) > .0000001)
                {
                    message += Environment.NewLine + (chinese ? "原位置相邻区段合并后保留前段材料；" : "At the old position, merged segments retain the preceding material. ");
                    if (!remove) message += chinese ? "接头后的材料随该接头移动，其余接头后的材料不变。" : "The edited joint carries its following material; other joints retain their following materials.";
                    foreach (var target in targets.Where(t => t.MaterialBefore != t.MaterialAfter))
                        message += Environment.NewLine + "  " + target.Phase + " / " + (target.BlockIndex + 1) + ": " + target.MaterialBefore + " + " + target.MaterialAfter + " → " + target.MaterialBefore;
                }
                message += Environment.NewLine + (chinese ? "既有定位分析不会自动重算，请重新核对。操作会保存原文件备份。" : "Existing location analysis is not recalculated: review it again. An exact original-file backup will be saved.");
                LastJointChangeConfirmation = message;
                // Suppressing UI dialogs never authorizes merging different
                // materials. An automation host must supply an explicit decision.
                if (!ShowDialogMessages && (remove || Math.Abs(position - selected.Position) > .0000001) && targets.Any(t => t.MaterialBefore != t.MaterialAfter) &&
                    (ConfirmJointMaterialMerge == null || !ConfirmJointMaterialMerge(message)))
                { CompleteOperation(operation, false, "未确认材料合并，接头未修改。"); return false; }
                if (ShowDialogMessages && MessageBox.Show(this, message, chinese ? "确认接头修改" : "Confirm joint change",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return false;
                CommitMeasuredJointChange(plan, operation);
                return true;
            }
            catch (Exception error)
            {
                CompleteOperation(operation, false, error.Message);string notice=CustomerJointFailure(error.ToString());SetStatus(notice, false);
                if (ShowDialogMessages) MessageBox.Show(this, notice, L("接头数据无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        static string JointConfirmationPosition(double value)
        {
            string display = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            return Math.Abs(value - Math.Round(value, 1, MidpointRounding.AwayFromZero)) < .0000001 ? display
                : display + " [" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "]";
        }

        // Host notification only reloads metadata. Unsaved ITEM form fields and
        // acquisition records are never replaced or written by this operation.
        public bool RefreshCableStructure()
        {
            if (IsDisposed || Disposing || IsBusy) return false;
            if (InvokeRequired) return InvokeCommandSafely(RefreshCableStructure);
            if (String.IsNullOrWhiteSpace(loadedProjectPath)) return true;
            try
            {
                if (GeometryChangedFromLoaded()) throw new InvalidOperationException("本页电缆结构有未保存修改，请先保存或重新加载后同步接头。");
                string cableFile = Path.Combine(loadedProjectPath, "CABLE.in"), itemFile = Path.Combine(loadedProjectPath, "ITEM.in");
                string itemHash = CableJointStore.Hash(File.ReadAllBytes(itemFile));
                if (String.IsNullOrEmpty(loadedJointItemHash) || itemHash != loadedJointItemHash)
                    throw new InvalidOperationException("项目基本资料已被其他操作修改，请重新加载；本页未保存内容仍保留。");
                string cableHash = CableJointStore.Hash(File.ReadAllBytes(cableFile));
                if (cableHash == loadedJointCableHash) return true;
                ProjectDraft saved = ProjectCreationStorage.Load(loadedProjectPath);
                if (itemHash != CableJointStore.Hash(File.ReadAllBytes(itemFile)) || cableHash != CableJointStore.Hash(File.ReadAllBytes(cableFile)))
                    throw new InvalidOperationException("项目在同步过程中被其他操作修改，请重新加载；本页内容仍保留。");
                bool dirty = IsDirty, previous = suppressChanges;
                JointData selected = cableGrid.SelectedRows.Count == 0 ? null : cableGrid.SelectedRows[0].Tag as JointData;
                suppressChanges = true;
                try
                {
                    SetLoadedLength(lengthANumber, saved.LengthA); SetLoadedLength(lengthBNumber, saved.LengthB); SetLoadedLength(lengthCNumber, saved.LengthC);
                    joints.Clear(); foreach (JointData joint in saved.Joints) joints.Add(joint.Clone());
                    phaseFirstMaterials.Clear(); foreach (var pair in saved.FirstSegmentMaterials) phaseFirstMaterials[pair.Key] = pair.Value;
                    loadedLengthA = saved.LengthA; loadedLengthB = saved.LengthB; loadedLengthC = saved.LengthC;
                    loadedJointCableHash = cableHash; loadedGeometryFingerprint = GeometryFingerprint(BuildDraft(false));
                    RefreshCableView(); ApplyGeometryProtection(); UpdateSavedState(!dirty, false);
                    if (selected != null)
                        foreach (DataGridViewRow row in cableGrid.Rows)
                        {
                            var joint = row.Tag as JointData;
                            if (joint != null && joint.Phase == selected.Phase && joint.Name == selected.Name && Math.Abs(joint.Position - selected.Position) < .000001)
                            { row.Selected = true; break; }
                        }
                }
                finally { suppressChanges = previous; }
                SetStatus("接头结构已同步 · 未保存的项目信息已保留", true);
                return true;
            }
            catch (Exception error)
            {
                SetStatus("接头结构未同步 · " + error.Message, false);
                return false;
            }
        }

        void RefreshAfterMeasuredJoint(bool dirty, CableJointPlan plan)
        {
            string expectedCableHash = CableJointStore.Hash(plan.Replacement);
            Action unchanged = delegate
            {
                var current = CableJointStore.Read(loadedProjectPath);
                if (CableJointStore.Hash(current.Original) != expectedCableHash || current.ItemHash != plan.ExpectedItemHash)
                    throw new InvalidOperationException("接头写入后项目又被其他操作修改，请重新加载。");
            };
            unchanged();
            ProjectDraft saved = ProjectCreationStorage.Load(loadedProjectPath);
            unchanged();
            bool previous = suppressChanges; suppressChanges = true;
            try
            {
                joints.Clear(); foreach (JointData joint in saved.Joints) joints.Add(joint.Clone());
                phaseFirstMaterials.Clear();
                foreach (var pair in saved.FirstSegmentMaterials) phaseFirstMaterials[pair.Key] = pair.Value;
                loadedGeometryFingerprint = GeometryFingerprint(BuildDraft(false));
                // Only advance the cable baseline to our own exact replacement.
                // Rebasing ITEM to a newly read file while preserving the old form
                // draft could authorize a later metadata save to erase external edits.
                loadedJointCableHash = expectedCableHash;
                loadedJointItemHash = plan.ExpectedItemHash;
                RefreshCableView(); ApplyGeometryProtection();
                UpdateSavedState(!dirty, false);
            }
            finally { suppressChanges = previous; }
        }
    }
}
