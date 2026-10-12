using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using System.IO;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private ContextMenuStrip jointMenu;
        private bool jointMarkersVisible;
        [DefaultValue(false)]
        public bool JointMarkersVisible
        {
            get { return jointMarkersVisible; }
            set { RunOnUiThread(delegate { if (jointMarkersVisible == value) return; jointMarkersVisible = value; DisposeJointMenu(); Invalidate(); }); }
        }
        private RectangleF JointVisibilityBounds
        { get { return UseCompactLayout ? new RectangleF(CompactLayout.QualityTab.Right + 16, CompactLayout.QualityTab.Top, 150, 34) : new RectangleF(1168, 106, 185, 28); } }
        [Browsable(false)] public string LastJointCommitWarning { get; private set; } = "";

        private sealed class JointMarkerLayout
        {
            internal CalibrationJoint Joint;
            internal float X;
            internal RectangleF Tag;
            internal string Label;
        }

        private List<JointMarkerLayout> GetJointMarkerLayout(Graphics g, Font font, WaveformData waveform,
            IEnumerable<CalibrationJoint> joints, MarkerData markers, double velocityMPerUs)
        {
            var result = new List<JointMarkerLayout>();
            if (!JointMarkersVisible) return result;
            RectangleF plot = CurrentPlotRectangle;
            double xMin, xMax, yMin, yMax;
            GetWaveformView(waveform, out xMin, out xMax, out yMin, out yMax);
            if (xMax <= xMin || velocityMPerUs <= 0) return result;
            foreach (CalibrationJoint joint in joints.OrderBy(j=>j.DistanceMeters))
            {
                double velocity = joint.VelocityBeforeMPerUs > 0 ? joint.VelocityBeforeMPerUs : velocityMPerUs;
                double time = markers.StartTimeSeconds + 2.0 * joint.DistanceMeters / (velocity * 1000000.0);
                if (time < xMin || time > xMax) continue;
                float x = plot.Left + (float)((time - xMin) / (xMax - xMin)) * plot.Width;
                string label = joint.Name + "  " + joint.DistanceMeters.ToString("0.0", CultureInfo.InvariantCulture) + " m" + (joint.AutomaticallyDetected?" · "+joint.ConfidencePercent.ToString("0")+"%":"");
                // Compact names keep the waveform readable; full names remain in
                // the joint review list. Pack tags into separate rows with leaders.
                if(label.Length>48)label=label.Substring(0,45)+"…";
                SizeF size = g.MeasureString(label, font);
                size.Width=Math.Min(size.Width,plot.Width-16);
                float left = Math.Max(plot.Left + 2F, Math.Min(plot.Right - size.Width - 12F, x + 5F));
                RectangleF tag=new RectangleF(left,plot.Bottom-size.Height-7F,size.Width+10F,size.Height+4F);
                while(result.Any(p=>p.Tag.IntersectsWith(tag))&&tag.Top>plot.Top+size.Height+10F)tag.Offset(0,-size.Height-8F);
                if(result.Any(p=>p.Tag.IntersectsWith(tag)))continue; // Remaining joints remain available in the review list.
                result.Add(new JointMarkerLayout { Joint = joint, X = x, Label = label,
                    Tag = tag });
            }
            return result;
        }

        private bool TryShowJointMenu(PointF point, Point clientPoint)
        {
            DisposeJointMenu();
            if (!JointMarkersVisible) return false;
            PhaseState phase; WaveformData waveform; MarkerData markers; string level; int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out markers, out level, out phaseIndex) || markers == null)
                return false;
            List<JointMarkerLayout> layouts;
            using (var bitmap = new Bitmap(1, 1))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = CreateFont(8.4F, FontStyle.Bold))
                layouts = GetJointMarkerLayout(graphics, font, waveform, GetPhaseJoints(phaseIndex), markers, phase.VelocityMPerUs);
            RectangleF content = GetContentBounds(ClientRectangle);
            float tolerance = UseCompactLayout ? 6F : 6F * ArtworkWidth / Math.Max(1F, content.Width);
            var hits = layouts.Where(item => Math.Abs(point.X - item.X) <= tolerance || item.Tag.Contains(point)).Reverse().ToArray();
            if (hits.Length == 0) return false;
            EndPlotGesture();
            ClearHoverMeasurement();
            if(jointMenu==null||jointMenu.IsDisposed)jointMenu = new ContextMenuStrip { Name = "JointContextMenu" };
            while(jointMenu.Items.Count>0){var item=jointMenu.Items[0];jointMenu.Items.RemoveAt(0);item.Dispose();}
            foreach (var hit in hits)
            {
                CalibrationJoint target = hit.Joint;
                var item = new ToolStripMenuItem("删除接头：" + target.Name + "（" +
                    target.DistanceMeters.ToString("0.0", CultureInfo.InvariantCulture) + " m）")
                    { Name = "DeleteJoint_" + target.Id, Tag = target.Id };
                item.Click += delegate { ConfirmJointRemoval(phaseIndex, level, target); };
                jointMenu.Items.Add(item);
            }
            jointMenu.Items.Add(new ToolStripSeparator());
            var cancel = new ToolStripMenuItem("取消") { Name = "CancelJointMenu" };
            jointMenu.Items.Add(cancel);
            jointMenu.Show(this, clientPoint);
            return true;
        }

        private void ConfirmJointRemoval(int phaseIndex, string level, CalibrationJoint target)
        {
            if (jointMenu != null&&!jointMenu.IsDisposed) jointMenu.Close();
            if (IsBusy || jointDialogOpen || jointConfirmationOpen) return;
            if (SelectedPhaseIndex != phaseIndex || SelectedLevelCode != level ||
                !GetPhaseJoints(phaseIndex).Any(item => item.Id == target.Id)) return;
            HashSet<string> persisted;
            bool saved = !SimulationMode && persistedJointIds.TryGetValue(phaseIndex, out persisted) && persisted.Contains(target.Id);
            if (saved && !target.AutomaticallyDetected)
            {
                MessageBox.Show(FindForm(), CalibrationText("此接头已保存，请在项目编辑页删除。", "This joint is saved. Delete it on the project editing page."),
                    CalibrationText("接头只读", "Read-only joint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string originalProject = ProjectPath; bool originalSimulation = SimulationMode;
            string expectedCableHash = persistedJointHash;
            jointDialogOpen = true;
            try
            {
            using (var dialog = new DeleteJointDialog(phaseIndex, target, Font, LanguageIndex))
            {
                Form owner = FindForm();
                DialogResult result = owner == null ? dialog.ShowDialog(this) : dialog.ShowDialog(owner);
                // A host may change state while the modal dialog pumps messages. Never act on a new selection.
                if (result == DialogResult.OK)
                {
                    try
                    {
                        RequireJointReviewContext(originalProject,phaseIndex,level,originalSimulation);
                        if (saved)
                        {
                            if (CableJointStore.Hash(CableJointStore.Read(ProjectPath).Original) != expectedCableHash)
                                throw new InvalidOperationException("项目接头已在其他页面修改，请刷新后重新选择；本次未删除。");
                            string[] phases = JointTargetPhases(phaseIndex, true);
                            if (phases.Length == 0) return;
                            RequireJointReviewContext(originalProject,phaseIndex,level,originalSimulation);
                            if (CableJointStore.Hash(CableJointStore.Read(ProjectPath).Original) != expectedCableHash)
                                throw new InvalidOperationException(CalibrationText("项目接头已修改，请刷新后重新选择；本次未删除。", "Project joints changed. Refresh and select again; nothing was deleted."));
                            CalibrationProjectStore.RemoveGeneratedJoints(ProjectPath, phases, new[] { target.DistanceMeters });
                            MarkLiveProjectChanged();
                            // The shared writer may preserve a material boundary. Do
                            // not imply a successful deletion while it is still stored.
                            HashSet<double> positions;
                            if (persistedJointPositions.TryGetValue(phaseIndex, out positions) && positions.Any(p => Math.Abs(p - target.DistanceMeters) < .001))
                                throw new InvalidOperationException("该接头仍承担材料边界，未删除。请在允许编辑的项目结构中核对。");
                        }
                        RemovePhaseJoint(phaseIndex, target.Id);
                    }
                    catch (Exception ex) { ShowCalibrationCustomerFailure(FindForm(),"Joint removal",ex,"接头未删除","Joint not deleted"); }
                }
            }
            }
            finally { jointDialogOpen = false; }
        }

        private void DisposeJointMenu()
        {
            if(jointMenu==null)return;
            // Parameter actions can run within a menu-click handler. Closing is
            // safe there; destruction belongs to the owning control's disposal.
            if(lifetimeEnded!=0||IsDisposed||Disposing){jointMenu.Dispose();jointMenu=null;}
            else if(!jointMenu.IsDisposed)jointMenu.Close();
        }

        private readonly List<string> jointMaterials = new List<string>
        {
            "未指定", "交联聚乙烯 (XLPE)", "聚乙烯 (PE)", "聚氯乙烯 (PVC)", "油纸绝缘"
        };

        public string[] GetJointMaterialOptions()
        {
            lock (stateLock) return jointMaterials.ToArray();
        }

        public void AddJointMaterial(string material)
        {
            if (String.IsNullOrWhiteSpace(material)) throw new ArgumentException("A material name is required.", "material");
            material = material.Trim();
            if (material.Length > 80) throw new ArgumentOutOfRangeException("material", "A material name cannot exceed 80 characters.");
            lock (stateLock)
                if (!jointMaterials.Any(item => String.Equals(item, material, StringComparison.OrdinalIgnoreCase)))
                    jointMaterials.Add(material);
        }

        public CalibrationJoint[] GetPhaseJoints(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            lock (stateLock) return GetPhaseStateNoLock(phaseIndex).Joints.OrderBy(item => item.DistanceMeters).ToArray();
        }

        public CalibrationJoint AddPhaseJoint(int phaseIndex, string name, double distanceMeters)
        {
            return AddPhaseJoint(phaseIndex, name, distanceMeters, "未指定", GetPhaseVelocityMPerUs(phaseIndex),
                "未指定", GetPhaseVelocityMPerUs(phaseIndex));
        }

        public CalibrationJoint AddPhaseJoint(int phaseIndex, string name, double distanceMeters,
            string materialBefore, double velocityBeforeMPerUs, string materialAfter, double velocityAfterMPerUs)
        {
            ValidatePhaseIndex(phaseIndex);
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentException("A joint name is required.", "name");
            if (name.Trim().Length > 80) throw new ArgumentOutOfRangeException("name", "The joint name cannot exceed 80 characters.");
            ValidateFinitePositive(distanceMeters, "distanceMeters");
            if (String.IsNullOrWhiteSpace(materialBefore) || String.IsNullOrWhiteSpace(materialAfter))
                throw new ArgumentException("Both cable materials are required.");
            materialBefore = materialBefore.Trim();
            materialAfter = materialAfter.Trim();
            if (materialBefore.Length > 80 || materialAfter.Length > 80)
                throw new ArgumentOutOfRangeException("materialBefore", "A material name cannot exceed 80 characters.");
            ValidateFinitePositive(velocityBeforeMPerUs, "velocityBeforeMPerUs");
            ValidateFinitePositive(velocityAfterMPerUs, "velocityAfterMPerUs");
            CalibrationJoint added = null;
            RunOnUiThread(delegate
            {
                long sequence;
                string level;
                lock (stateLock)
                {
                    added = new CalibrationJoint(Guid.NewGuid().ToString("N"), name.Trim(), distanceMeters,
                        materialBefore, velocityBeforeMPerUs, materialAfter, velocityAfterMPerUs);
                    GetPhaseStateNoLock(phaseIndex).Joints.Add(added);
                    if (!jointMaterials.Any(item => String.Equals(item, materialBefore, StringComparison.OrdinalIgnoreCase)))
                        jointMaterials.Add(materialBefore);
                    if (!jointMaterials.Any(item => String.Equals(item, materialAfter, StringComparison.OrdinalIgnoreCase)))
                        jointMaterials.Add(materialAfter);
                    sequence = System.Threading.Interlocked.Increment(ref jointChangedSequence);
                    level = selectedLevelCode;
                }
                Invalidate();
                var handler = JointAdded;
                if (handler != null) handler(this, new CalibrationJointEventArgs(sequence, phaseIndex, level, added));
            });
            return added;
        }

        public bool RemovePhaseJoint(int phaseIndex, string jointId)
        {
            ValidatePhaseIndex(phaseIndex);
            if (String.IsNullOrWhiteSpace(jointId)) return false;
            CalibrationJoint removed = null;
            RunOnUiThread(delegate
            {
                long sequence = 0;
                string level = String.Empty;
                lock (stateLock)
                {
                    var joints = GetPhaseStateNoLock(phaseIndex).Joints;
                    removed = joints.FirstOrDefault(item => String.Equals(item.Id, jointId, StringComparison.Ordinal));
                    if (removed == null) return;
                    joints.Remove(removed);
                    sequence = System.Threading.Interlocked.Increment(ref jointChangedSequence);
                    level = selectedLevelCode;
                }
                Invalidate();
                var handler = JointRemoved;
                if (handler != null) handler(this, new CalibrationJointEventArgs(sequence, phaseIndex, level, removed));
            });
            return removed != null;
        }

        private bool TryOpenJointDialog(PointF point)
        {
            if (IsBusy)
            {
                MessageBox.Show(FindForm(), CalibrationText("请等待当前采集完成或停止采集后添加接头。", "Finish or stop acquisition before adding a joint."),
                    CalibrationText("添加接头", "Add joint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            if (IsCapacitiveLoad)
            {
                MessageBox.Show(FindForm(), CalibrationText("当前为电容负载，仅进行放电量校准，不进行电缆测距或接头定位。", "Capacitive loads support charge calibration, not cable ranging or joint location."),
                    CalibrationText("添加接头", "Add joint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            PhaseState phase;
            WaveformData waveform;
            MarkerData marker;
            string level;
            int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out marker, out level, out phaseIndex))
            {
                MessageBox.Show(FindForm(), CalibrationText("请先采集测距或校准波形，再双击添加接头。", "Acquire a ranging or calibration waveform before adding a joint."),
                    CalibrationText("添加接头", "Add joint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            double proposedDistance = 0;
            string warning = CalibrationText("尚无可靠的入射和反射游标，不能将点击位置自动换算为距离。请手动输入距始端的接头位置。", "Reliable incident/reflection cursors are unavailable. Enter the joint distance from the cable start manually.");
            double time = ReferenceXToTime(point.X);
            if (marker != null && IsFinite(phase.VelocityMPerUs) && phase.VelocityMPerUs > 0 &&
                marker.DistanceMarkerSeconds > marker.StartTimeSeconds)
            {
                double candidate = (time - marker.StartTimeSeconds) * phase.VelocityMPerUs * 1000000.0 / 2.0;
                if (IsFinite(candidate) && candidate > 0 && candidate < phase.CableLengthMeters)
                { proposedDistance = candidate; warning = String.Empty; }
                else warning = CalibrationText("点击位置不在当前电缆范围内，请手动输入两端之间的接头位置。", "The clicked position is outside the cable. Enter a joint distance inside the cable.");
            }
            EndPlotGesture();
            OpenSharedJointDialog(phaseIndex, level, proposedDistance, warning);
            return true;
        }

        private CableJointSnapshot JointSnapshotForCurrentContext(int phaseIndex)
        {
            if (!String.IsNullOrWhiteSpace(ProjectPath)) return CableJointStore.Read(ProjectPath);
            if (!SimulationMode) throw new InvalidOperationException("请先载入项目，再添加接头。");
            return CableJointStore.CreatePreview(PhaseCodeFromIndex(phaseIndex),
                GetPhaseStateNoLock(phaseIndex).CableLengthMeters, "未指定");
        }

        // Analysis, calibration and browser all use this same dialog. A dialog
        // prepares a plan only; file replacement must succeed before any marker
        // is published. Simulation reads geometry but never commits project data.
        private void OpenSharedJointDialog(int phaseIndex, string level, double proposedDistance)
        { OpenSharedJointDialog(phaseIndex, level, proposedDistance, String.Empty); }

        private void OpenSharedJointDialog(int phaseIndex, string level, double proposedDistance, string warning)
        {
            if (jointDialogOpen || jointConfirmationOpen || IsBusy || IsCapacitiveLoad || IsDisposed || Disposing) return;
            string project = ProjectPath; bool simulation = SimulationMode;
            jointDialogOpen = true;
            try
            {
                var snapshot = JointSnapshotForCurrentContext(phaseIndex);
                using (var ownedIcon = CalibrationBranding.LoadIcon())
                using (var dialog = new CableJointDialog(snapshot, proposedDistance, PhaseCodeFromIndex(phaseIndex),
                    LanguageIndex, ownedIcon))
                {
                    ConfigureJointCustomerDiagnostics(dialog);
                    if (warning.Length > 0 || simulation)
                        dialog.AdditionalWarning = warning + (simulation ? (warning.Length > 0 ? "\r\n" : "") + CalibrationText("仿真只添加显示标记，不写入项目。", "Simulation changes markers only; project files are unchanged.") : "");
                    dialog.ConfigureCommit((plan, operation) => {
                        RequireJointReviewContext(project,phaseIndex,level,simulation);
                        CommitSharedJointPlan(plan);
                    });
                    var owner = FindForm();
                    if ((owner == null ? dialog.ShowDialog(this) : dialog.ShowDialog(owner)) != DialogResult.OK) return;
                    if (!String.IsNullOrEmpty(LastJointCommitWarning))
                        ShowSavedJointCustomerWarning(FindForm());
                }
            }
            catch (Exception ex)
            { ShowCalibrationCustomerFailure(FindForm(),"Joint write",ex,"接头未写入","Joint not saved"); }
            finally { jointDialogOpen = false; }
        }

        private void CommitSharedJointPlan(CableJointPlan plan)
        {
            LastJointCommitWarning = "";
            if (plan == null || plan.ResolvedTargets == null || plan.ResolvedTargets.Length == 0)
                throw new InvalidOperationException("没有已确认的接头写入计划。");
            bool removal = plan.OperationCode == "RemoveJoint", editing = plan.OperationCode == "EditJoint";
            var markers = new List<KeyValuePair<int, CalibrationJoint>>();
            foreach (var group in plan.ResolvedTargets.Where(t => !removal).GroupBy(t => t.Phase + ":" + t.PositionM.ToString("R", CultureInfo.InvariantCulture)))
            {
                var target = group.First();
                int index = target.Phase == "ALL" ? -1 : target.Phase == "A" ? 0 : target.Phase == "B" ? 1 : target.Phase == "C" ? 2 : -2;
                ValidatePhaseIndex(index);
                double velocity = GetPhaseVelocityMPerUs(index);
                ValidateFinitePositive(velocity, "velocity");
                ValidateFinitePositive(target.PositionM, "distanceMeters");
                if (String.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 80 || group.Any(t => String.IsNullOrWhiteSpace(t.MaterialBefore) || String.IsNullOrWhiteSpace(t.MaterialAfter)))
                    throw new InvalidOperationException("接头名称或材料无法显示，项目尚未改写。");
                markers.Add(new KeyValuePair<int, CalibrationJoint>(index, new CalibrationJoint(Guid.NewGuid().ToString("N"),
                    target.Name, target.PositionM, JointMaterialSummary(group.Select(t=>t.MaterialBefore)), velocity,
                    JointMaterialSummary(group.Select(t=>t.MaterialAfter)), velocity)));
            }
            // Shared three-core files may retain historical A/B/C headers. Keep
            // markers in the actual stored phases and an ALL display mirror so
            // the unified three-core waveform immediately shows the saved joint.
            if (!removal && plan.SharedThreeCore && !markers.Any(m=>m.Key==-1))
                markers.Add(new KeyValuePair<int, CalibrationJoint>(-1, new CalibrationJoint(Guid.NewGuid().ToString("N"),
                    plan.Name, plan.PositionM, JointMaterialSummary(plan.ResolvedTargets.Select(t=>t.MaterialBefore)),
                    GetPhaseVelocityMPerUs(-1), JointMaterialSummary(plan.ResolvedTargets.Select(t=>t.MaterialAfter)), GetPhaseVelocityMPerUs(-1))));
            if (!SimulationMode)
            {
                string current = Path.Combine(Path.GetFullPath(ProjectPath), "CABLE.in");
                if (!String.Equals(current, plan.CablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("接头计划不属于当前项目，未写入。");
                var committed = CableJointStore.CommitWithResult(plan);
                LastJointCommitWarning = committed.Warning;
                MarkLiveProjectChanged();
                if (editing || removal) {
                    // Reloaded persisted projection handles moves, removal and
                    // automatic renumbering, including ALL mirror markers.
                    if (!RefreshPersistedJointProjection()) LastJointCommitWarning += Environment.NewLine + LastCalibrationError;
                    return;
                }
            }
            else if (editing || removal) lock(stateLock) {
                var phases=plan.ResolvedTargets.Select(t=>JointPhaseIndex(t.Phase)).Distinct().ToList();
                if(plan.SharedThreeCore&&!phases.Contains(-1))phases.Add(-1);
                foreach(int index in phases) GetPhaseStateNoLock(index).Joints.RemoveAll(j=>Math.Abs(j.DistanceMeters-plan.OriginalPositionM)<.001);
                System.Threading.Interlocked.Increment(ref jointChangedSequence);
                Invalidate();
            }
            PublishCommittedJointMarkers(markers);
        }

        private static string JointMaterialSummary(IEnumerable<string> materials)
        {
            var values=materials.Distinct().ToArray();
            string result=values.Length==1?values[0]:"多段材料："+String.Join(" / ",values);
            return result.Length<=4000?result:result.Substring(0,3950)+"…（完整材料见项目结构）";
        }

        private void PublishCommittedJointMarkers(IEnumerable<KeyValuePair<int, CalibrationJoint>> markers)
        {
            foreach (var pair in markers)
            {
                long sequence;
                lock (stateLock)
                {
                    var joints = GetPhaseStateNoLock(pair.Key).Joints;
                    joints.RemoveAll(j => Math.Abs(j.DistanceMeters - pair.Value.DistanceMeters) < .001);
                    joints.Add(pair.Value);
                    TrackPublishedJoint(pair.Key, pair.Value);
                    foreach (string material in new[] { pair.Value.MaterialBefore, pair.Value.MaterialAfter })
                        if (!jointMaterials.Contains(material)) jointMaterials.Add(material);
                    sequence = System.Threading.Interlocked.Increment(ref jointChangedSequence);
                }
                var handler = JointAdded;
                if (handler != null) try { handler(this, new CalibrationJointEventArgs(sequence, pair.Key, SelectedLevelCode, pair.Value)); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("JointAdded listener: " + ex.Message); }
            }
            Invalidate();
        }

        private void DrawJointMarkers(Graphics g, WaveformData waveform, System.Collections.Generic.IEnumerable<CalibrationJoint> joints,
            MarkerData markers, double velocityMPerUs)
        {
            if (!JointMarkersVisible || velocityMPerUs <= 0.0) return;
            RectangleF plot = CurrentPlotRectangle;
            double xMin, xMax, yMin, yMax;
            GetWaveformView(waveform, out xMin, out xMax, out yMin, out yMax);
            if (xMax <= xMin) return;
            GraphicsState clip = g.Save();
            g.SetClip(plot);
            using (var pen = new Pen(Color.FromArgb(225, 141, 25), 1.2F))
            using (var font = CreateFont(8.4F, FontStyle.Bold))
            using (var ink = new SolidBrush(Color.FromArgb(142, 77, 11)))
            using (var badge = new SolidBrush(Color.FromArgb(255, 247, 226)))
            {
                pen.DashStyle = DashStyle.Dash;
                var layouts = GetJointMarkerLayout(g, font, waveform, joints, markers, velocityMPerUs);
                GraphicsState lineClip = g.Save();
                if (Object.ReferenceEquals(paintedBadgeMarkers, markers))
                    foreach (var badgeBounds in new[] { incidentBadgeHit, reflectionBadgeHit, velocityBadgeHit })
                    {
                        var bounds = badgeBounds;
                        if (compactRendering && EmbeddedMode) bounds.Offset(0, 40);
                        g.ExcludeClip(Rectangle.Ceiling(bounds));
                    }
                foreach (var item in layouts)
                {
                    int alpha=item.Joint.ConfidencePercent>=80?255:85;
                    pen.Color=Color.FromArgb(alpha,225,141,25);
                    pen.Width=item.Joint.ConfidencePercent>=80?1.6F:1F;
                    g.DrawLine(pen, item.X, plot.Top, item.X, plot.Bottom);
                    g.DrawLine(pen,item.X,item.Tag.Top+item.Tag.Height/2,item.Tag.Left,item.Tag.Top+item.Tag.Height/2);
                }
                g.Restore(lineClip);
                foreach (var item in layouts)
                {
                    int alpha=item.Joint.ConfidencePercent>=80?255:85;
                    ink.Color=Color.FromArgb(alpha,142,77,11);
                    badge.Color=Color.FromArgb(item.Joint.ConfidencePercent>=80?255:95,255,247,226);
                    g.FillRectangle(badge, item.Tag);
                    g.DrawString(item.Label, font, ink, item.Tag.Left + 5F, item.Tag.Top + 2F);
                }
            }
            g.Restore(clip);
        }
    }

    internal sealed class DeleteJointDialog : CalibrationDialogForm
    {
        internal DeleteJointDialog(int phaseIndex, CalibrationJoint joint)
            : this(phaseIndex,joint,null,0) { }
        internal DeleteJointDialog(int phaseIndex, CalibrationJoint joint, Font font, int language)
            : base("DeleteJointDialog", Xiongxin.OWTS.Localization.UiLanguage.Text(language,"删除指定接头","Delete selected joint"),font,new Size(540,320))
        {
            Func<string,string,string> t=(zh,en)=>Xiongxin.OWTS.Localization.UiLanguage.Text(language,zh,en);
            string phase = phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
            var context = new TextBox { Name="DeleteJointDetails",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=true,BorderStyle=BorderStyle.None,BackColor=Color.White,
                Text = t("确认删除以下接头？\r\n相别：","Delete this joint?\r\nPhase: ") + phase + t("    位置：","    Position: ") +
                    joint.DistanceMeters.ToString("0.0", CultureInfo.InvariantCulture) + " m\r\n" +t("接头：","Joint: ")+ joint.Name +
                    "\r\n"+t("仅删除该接头标记与记录，不删除波形或其他接头。","Only this joint marker and record will be removed. Waveforms and other joints are retained.") };
            var confirm = new HVX.Presentation.R2RoundedButton { Name = "ConfirmDeleteJoint", Text = t("确认删除","Confirm deletion"),
                DialogResult = DialogResult.OK, BackColor = Color.FromArgb(255, 245, 245),
                ForeColor = ColorTranslator.FromHtml("#C44747") };
            var cancel = new HVX.Presentation.R2RoundedButton { Name = "CancelDeleteJoint", Text = t("取消","Cancel"),DialogResult = DialogResult.Cancel };
            foreach (var button in new[] { confirm, cancel })
            {
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = Color.White;
                button.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CAD8E5");
                button.FlatAppearance.BorderSize = 1;
            }
            SetContent(context,cancel,confirm);
            AcceptButton = cancel;
            CancelButton = cancel;
            Shown += delegate { cancel.Focus(); };
        }
    }

    internal sealed class AddJointDialog : Form
    {
        private Font ownedFont;
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing && ownedFont != null) { ownedFont.Dispose(); ownedFont = null; } }
        }
        private readonly TextBox nameBox;
        private readonly NumericUpDown distanceBox;
        private readonly ComboBox materialBeforeBox;
        private readonly ComboBox materialAfterBox;
        private readonly NumericUpDown velocityBeforeBox;
        private readonly NumericUpDown velocityAfterBox;
        private bool updatingDistance;
        private bool distanceManuallyChanged;

        internal AddJointDialog(int phaseIndex, int ordinal, double proposedDistance, double maximumDistance,
            double phaseVelocityMPerUs, string[] materials)
        {
            Icon = CalibrationBranding.LoadIcon();
            Text = "添加接头";
            Name = "AddJointDialog";
            ownedFont = new Font("Microsoft YaHei UI", 9.5F);
            Font = ownedFont;
            BackColor = Color.White;
            ForeColor = ColorTranslator.FromHtml("#18334D");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(450, 366);

            string phase = phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
            var context = new Label { Left = 20, Top = 18, Width = 410, Height = 23,
                Text = "相别：" + phase + "    反射位置：" + maximumDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m" };
            var nameLabel = new Label { Left = 20, Top = 58, Width = 95, Height = 24, Text = "接头名称" };
            nameBox = new TextBox { Name = "JointName", Left = 130, Top = 54, Width = 290, MaxLength = 64,
                Text = "接头 " + ordinal.ToString(CultureInfo.InvariantCulture) };
            var distanceLabel = new Label { Left = 20, Top = 101, Width = 95, Height = 24, Text = "位置 / m" };
            decimal max = (decimal)Math.Min(100000.0, maximumDistance);
            distanceBox = new NumericUpDown { Name = "JointDistance", Left = 130, Top = 97, Width = 160,
                DecimalPlaces = 1, Minimum = 0.1M, Maximum = max, Increment = 0.1M };
            distanceBox.Value = Math.Max(distanceBox.Minimum, Math.Min(max, Math.Round((decimal)proposedDistance, 1)));
            var beforeMaterialLabel = new Label { Left = 20, Top = 145, Width = 105, Height = 24, Text = "接头前材料" };
            materialBeforeBox = new ComboBox { Name = "JointMaterialBefore", Left = 130, Top = 141, Width = 290,
                DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 64 };
            var beforeVelocityLabel = new Label { Left = 20, Top = 185, Width = 110, Height = 24, Text = "前波速 / m/μs" };
            velocityBeforeBox = new NumericUpDown { Name = "JointVelocityBefore", Left = 130, Top = 181, Width = 160,
                DecimalPlaces = 2, Minimum = 0.01M, Maximum = 100000M, Increment = 0.5M };
            var afterMaterialLabel = new Label { Left = 20, Top = 225, Width = 105, Height = 24, Text = "接头后材料" };
            materialAfterBox = new ComboBox { Name = "JointMaterialAfter", Left = 130, Top = 221, Width = 290,
                DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 64 };
            var afterVelocityLabel = new Label { Left = 20, Top = 265, Width = 110, Height = 24, Text = "后波速 / m/μs" };
            velocityAfterBox = new NumericUpDown { Name = "JointVelocityAfter", Left = 130, Top = 261, Width = 160,
                DecimalPlaces = 2, Minimum = 0.01M, Maximum = 100000M, Increment = 0.5M };
            foreach (string material in materials)
            {
                materialBeforeBox.Items.Add(material);
                materialAfterBox.Items.Add(material);
            }
            materialBeforeBox.Text = "未指定";
            materialAfterBox.Text = "未指定";
            decimal initialVelocity = Math.Max(velocityBeforeBox.Minimum,
                Math.Min(velocityBeforeBox.Maximum, Math.Round((decimal)phaseVelocityMPerUs, 2)));
            velocityBeforeBox.Value = initialVelocity;
            velocityAfterBox.Value = initialVelocity;
            distanceBox.ValueChanged += delegate { if (!updatingDistance) distanceManuallyChanged = true; };
            velocityBeforeBox.ValueChanged += delegate
            {
                double adjustedMaximum = maximumDistance * (double)velocityBeforeBox.Value / phaseVelocityMPerUs;
                decimal adjustedMax = (decimal)Math.Min(100000.0, adjustedMaximum);
                updatingDistance = true;
                try
                {
                    distanceBox.Maximum = Math.Max(distanceBox.Minimum, adjustedMax);
                    if (!distanceManuallyChanged)
                    {
                        decimal adjustedDistance = (decimal)(proposedDistance * (double)velocityBeforeBox.Value / phaseVelocityMPerUs);
                        distanceBox.Value = Math.Max(distanceBox.Minimum,
                            Math.Min(distanceBox.Maximum, Math.Round(adjustedDistance, 1)));
                    }
                }
                finally { updatingDistance = false; }
                context.Text = "相别：" + phase + "    反射位置：" + adjustedMaximum.ToString("0.0", CultureInfo.InvariantCulture) + " m";
            };
            var hint = new Label { Left = 130, Top = 298, Width = 290, Height = 24,
                ForeColor = ColorTranslator.FromHtml("#60758A"), Text = "可在材料框中输入新材料名称" };
            var add = new HVX.Presentation.R2RoundedButton { Name = "AddJoint", Text = "确认添加", Left = 241, Top = 322, Width = 88, Height = 34,
                BackColor = ColorTranslator.FromHtml("#00A3C7"), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                DialogResult = DialogResult.OK };
            add.FlatAppearance.BorderSize = 0;
            var cancel = new HVX.Presentation.R2RoundedButton { Name = "CancelJoint", Text = "取消", Left = 340, Top = 322, Width = 80, Height = 34,
                DialogResult = DialogResult.Cancel };
            add.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#0089AA");
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.BackColor = Color.White;
            cancel.ForeColor = ColorTranslator.FromHtml("#18334D");
            cancel.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#CAD8E5");
            Controls.AddRange(new Control[] { context, nameLabel, nameBox, distanceLabel, distanceBox,
                beforeMaterialLabel, materialBeforeBox, beforeVelocityLabel, velocityBeforeBox,
                afterMaterialLabel, materialAfterBox, afterVelocityLabel, velocityAfterBox, hint, add, cancel });
            AcceptButton = add;
            CancelButton = cancel;
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (DialogResult != DialogResult.OK) return;
                if (String.IsNullOrWhiteSpace(nameBox.Text) || String.IsNullOrWhiteSpace(materialBeforeBox.Text) ||
                    String.IsNullOrWhiteSpace(materialAfterBox.Text) || distanceBox.Value <= 0M ||
                    distanceBox.Value >= distanceBox.Maximum)
                {
                    args.Cancel = true;
                    MessageBox.Show(this, "请填写接头名称及前后材料，并将位置设在入射波与反射波之间。", "添加接头",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
        }

        internal string JointName { get { return nameBox.Text.Trim(); } }
        internal double DistanceMeters { get { return (double)distanceBox.Value; } }
        internal string MaterialBefore { get { return materialBeforeBox.Text.Trim(); } }
        internal double VelocityBeforeMPerUs { get { return (double)velocityBeforeBox.Value; } }
        internal string MaterialAfter { get { return materialAfterBox.Text.Trim(); } }
        internal double VelocityAfterMPerUs { get { return (double)velocityAfterBox.Value; } }
    }
}
