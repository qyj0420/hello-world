using System;
using System.Collections.Generic;
using System.Linq;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private readonly Dictionary<int, HashSet<string>> persistedJointIds = new Dictionary<int, HashSet<string>>();
        private readonly Dictionary<int, HashSet<double>> persistedJointPositions = new Dictionary<int, HashSet<double>>();
        private string persistedJointProject = String.Empty, persistedJointHash = String.Empty;
        private bool jointDialogOpen, jointConfirmationOpen;
        public event EventHandler CableStructureChanged;

        // Host retries busy pages; no captured waveform or pending candidate is replaced.
        public bool RefreshCableStructure()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<bool>(RefreshCableStructure));
            if (IsBusy || jointDialogOpen || jointConfirmationOpen) return false;
            return RefreshPersistedJointProjection();
        }

        private void ClearPersistedJointProjection()
        {
            persistedJointIds.Clear(); persistedJointPositions.Clear();
            persistedJointProject = persistedJointHash = String.Empty;
        }

        private bool RefreshPersistedJointProjection()
        {
            if (SimulationMode || IsCapacitiveLoad || String.IsNullOrWhiteSpace(ProjectPath)) return true;
            try
            {
                var snapshot = CableJointStore.Read(ProjectPath);
                string hash = CableJointStore.Hash(snapshot.Original);
                if (String.Equals(persistedJointProject, ProjectPath, StringComparison.OrdinalIgnoreCase) && hash == persistedJointHash) return true;
                var targets = new List<KeyValuePair<int, CalibrationJoint>>();
                foreach (var blocks in snapshot.Blocks.GroupBy(b => b.Phase))
                {
                    int index = JointPhaseIndex(blocks.Key);
                    if (index < -1) continue;
                    var nodes = blocks.SelectMany(b => Enumerable.Range(1, Math.Max(0, b.Positions.Count - 2)).Select(i => new { Block = b, Index = i }));
                    foreach (var samePosition in nodes.GroupBy(n => n.Block.Positions[n.Index]))
                    {
                        string[] names = samePosition.Select(n => snapshot.Lines[n.Block.AccessoryLines[n.Index]].Split('\t')[1]).Distinct().ToArray();
                        string before = JointMaterialSummary(samePosition.Select(n => snapshot.Lines[n.Block.BodyLines[n.Index - 1]].Split('\t')[2]));
                        string after = JointMaterialSummary(samePosition.Select(n => snapshot.Lines[n.Block.BodyLines[n.Index]].Split('\t')[2]));
                        targets.Add(new KeyValuePair<int, CalibrationJoint>(index, new CalibrationJoint(Guid.NewGuid().ToString("N"),
                            String.Join(" / ", names), samePosition.Key, before, GetPhaseVelocityMPerUs(index), after, GetPhaseVelocityMPerUs(index))));
                    }
                }
                if (snapshot.Mode == CableJointMode.SharedThreeCore && !targets.Any(t => t.Key == -1))
                    foreach (var position in targets.GroupBy(t => t.Value.DistanceMeters).ToArray())
                        targets.Add(new KeyValuePair<int, CalibrationJoint>(-1, new CalibrationJoint(Guid.NewGuid().ToString("N"),
                            String.Join(" / ", position.Select(t => t.Value.Name).Distinct()), position.Key,
                            JointMaterialSummary(position.Select(t => t.Value.MaterialBefore)), GetPhaseVelocityMPerUs(-1),
                            JointMaterialSummary(position.Select(t => t.Value.MaterialAfter)), GetPhaseVelocityMPerUs(-1))));
                lock (stateLock)
                {
                    foreach (int index in new[] { -1, 0, 1, 2 })
                    {
                        var joints = GetPhaseStateNoLock(index).Joints;
                        HashSet<string> oldIds;
                        persistedJointIds.TryGetValue(index, out oldIds);
                        var old = joints.ToArray();
                        // Deselected auto candidates remain reviewable; deleted saved markers do not linger.
                        if (oldIds != null) joints.RemoveAll(j => oldIds.Contains(j.Id) && !(j.AutomaticallyDetected && !j.Confirmed));
                        var ids = new HashSet<string>(); var positions = new HashSet<double>();
                        foreach (var target in targets.Where(t => t.Key == index).Select(t => t.Value))
                        {
                            CalibrationJoint existing = old.FirstOrDefault(j => Math.Abs(j.DistanceMeters - target.DistanceMeters) < .001 &&
                                ((j.AutomaticallyDetected && oldIds != null && oldIds.Contains(j.Id)) ||
                                (j.Name == target.Name && j.MaterialBefore == target.MaterialBefore && j.MaterialAfter == target.MaterialAfter)));
                            CalibrationJoint projected = existing ?? target;
                            if(existing!=null && (existing.Name!=target.Name || existing.MaterialBefore!=target.MaterialBefore || existing.MaterialAfter!=target.MaterialAfter)) {
                                // Preserve detection evidence, not stale editable
                                // metadata, when a saved candidate was renamed.
                                projected=target;
                                projected.AutomaticallyDetected=existing.AutomaticallyDetected;
                                projected.Confirmed=existing.Confirmed;
                                projected.ConfidencePercent=existing.ConfidencePercent;
                            }
                            if (!joints.Any(j => j.Id == projected.Id)) joints.Add(projected);
                            ids.Add(projected.Id); positions.Add(target.DistanceMeters);
                            if (!jointMaterials.Contains(target.MaterialBefore)) jointMaterials.Add(target.MaterialBefore);
                            if (!jointMaterials.Contains(target.MaterialAfter)) jointMaterials.Add(target.MaterialAfter);
                        }
                        persistedJointIds[index] = ids; persistedJointPositions[index] = positions;
                    }
                }
                persistedJointProject = ProjectPath; persistedJointHash = hash;
                Invalidate(); return true;
            }
            catch (Exception ex)
            {
                WriteCalibrationDiagnostic("Joint marker refresh: "+ex);
                lastCalibrationError=CalibrationText("接头显示未更新，请刷新项目后重试。", "Joint display was not updated. Refresh the project and retry.");
                return false;
            }
        }

        private static int JointPhaseIndex(string phase)
        { return phase == "ALL" ? -1 : phase == "A" ? 0 : phase == "B" ? 1 : phase == "C" ? 2 : -2; }

        private void TrackPublishedJoint(int phase, CalibrationJoint joint)
        {
            if (SimulationMode) return;
            HashSet<double> positions; HashSet<string> ids;
            if (persistedJointPositions.TryGetValue(phase, out positions) && positions.Any(p => Math.Abs(p - joint.DistanceMeters) < .001) &&
                persistedJointIds.TryGetValue(phase, out ids)) ids.Add(joint.Id);
        }

        private void NotifyPersistedCableStructureChanged()
        {
            if (SimulationMode || String.IsNullOrWhiteSpace(ProjectPath)) return;
            string previous = persistedJointHash;
            if (!RefreshPersistedJointProjection() || previous == persistedJointHash) return;
            var handler = CableStructureChanged;
            if (handler != null) foreach (EventHandler listener in handler.GetInvocationList())
                try { listener(this, EventArgs.Empty); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("CableStructureChanged listener: " + ex.Message); }
        }
    }
}
