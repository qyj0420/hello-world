using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        public void SetPhaseCableLengths(double phaseA, double phaseB, double phaseC)
        {
            EnsureGeometryEditable();
            ValidateFiniteNonNegative(phaseA, "A相长度");
            ValidateFiniteNonNegative(phaseB, "B相长度");
            ValidateFiniteNonNegative(phaseC, "C相长度");
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                SetNumber(lengthANumber, phaseA);
                SetNumber(lengthBNumber, phaseB);
                SetNumber(lengthCNumber, phaseC);
            }
            finally { suppressChanges = previous; }
            ApplyModeState(false);
            MarkDirty();
        }

        public void ApplyDefaultCableLengthToUnsetPhases(double value)
        {
            EnsureGeometryEditable();
            ValidateFiniteNonNegative(value, "默认长度");
            SetPhaseCableLengths(CableLengthA <= 0 ? value : CableLengthA,
                CableLengthB <= 0 ? value : CableLengthB,
                CableLengthC <= 0 ? value : CableLengthC);
        }

        public double[] GetPhaseCableLengths()
        {
            return new[] { CableLengthA, CableLengthB, CableLengthC };
        }

        public bool SetPhaseStructure(int phaseIndex, double length, double[] jointPositions, string[] jointAttachmentTypes, string[] segmentBodyTypes)
        {
            try
            {
                EnsureGeometryEditable();
                string phase = PhaseFromIndex(phaseIndex);
                ValidateFiniteNonNegative(length, phase + "长度");
                if (length <= 0) throw new ArgumentOutOfRangeException("length", "电缆相长度必须大于0。");
                double[] positions = jointPositions ?? new double[0];
                string[] attachments = jointAttachmentTypes ?? new string[0];
                string[] segments = segmentBodyTypes ?? new string[0];
                if (attachments.Length != positions.Length) throw new ArgumentException("接头类型数量必须与接头位置数量一致。");
                if (segments.Length != positions.Length + 1) throw new ArgumentException("本体分段类型数量必须等于接头数 + 1。");
                string firstMaterial = string.IsNullOrWhiteSpace(segments[0]) ? "塑料绝缘" : segments[0].Trim();
                List<JointData> replacement = new List<JointData>();
                double previous = 0;
                for (int index = 0; index < positions.Length; index++)
                {
                    double position = positions[index];
                    if (!IsFinite(position) || position <= 0 || position >= length)
                        throw new ArgumentOutOfRangeException("jointPositions", phase + "接头位置必须满足 0 < 位置 < " + ProjectText.Number(length) + " m。");
                    if (index > 0 && position <= previous) throw new ArgumentException("同一相的接头位置必须严格递增且不能重复。");
                    replacement.Add(new JointData
                    {
                        Phase = phase,
                        Name = CableJointStore.AutomaticJointName(index + 1),
                        AttachmentType = string.IsNullOrWhiteSpace(attachments[index]) ? "户内冷缩" : attachments[index].Trim(),
                        Material = string.IsNullOrWhiteSpace(segments[index + 1]) ? "塑料绝缘" : segments[index + 1].Trim(),
                        Position = position
                    });
                    previous = position;
                }
                bool wasSuppressed = suppressChanges;
                suppressChanges = true;
                try { SetNumber(LengthControl(phaseIndex), length); }
                finally { suppressChanges = wasSuppressed; }
                joints.RemoveAll(item => item.Phase == phase);
                joints.AddRange(replacement);
                phaseFirstMaterials[phase] = firstMaterial;
                MarkDirty();
                RefreshCableView();
                CompleteOperation("SetPhaseStructure", true, string.Empty);
                return true;
            }
            catch (Exception exception)
            {
                CompleteOperation("SetPhaseStructure", false, exception.Message);
                return false;
            }
        }

        public double[] GetPhaseJointPositions(int phaseIndex)
        {
            string phase = PhaseFromIndex(phaseIndex);
            return joints.Where(item => item.Phase == phase).OrderBy(item => item.Position).Select(item => item.Position).ToArray();
        }

        public string[] GetPhaseJointAttachmentTypes(int phaseIndex)
        {
            string phase = PhaseFromIndex(phaseIndex);
            return joints.Where(item => item.Phase == phase).OrderBy(item => item.Position).Select(item => item.AttachmentType).ToArray();
        }

        public string[] GetPhaseSegmentBodyTypes(int phaseIndex)
        {
            string phase = PhaseFromIndex(phaseIndex);
            List<JointData> items = joints.Where(item => item.Phase == phase).OrderBy(item => item.Position).ToList();
            List<string> result = new List<string> { FirstSegmentMaterial(phase) };
            result.AddRange(items.Select(item => item.Material));
            return result.ToArray();
        }

        public string[] GetPhaseJointRows(string phase)
        {
            string normalized = NormalizePhase(phase);
            return joints.Where(item => item.Phase == normalized).OrderBy(item => item.Position)
                .Select(item => item.Name + "\t" + item.AttachmentType + "\t" + ProjectText.Number(item.Position) + "\t" + item.Material).ToArray();
        }

        public bool AddJoint(string phase, string attachmentType, string bodyType, double position)
        { return AddJointWithMaterials(phase, attachmentType, "", bodyType, position); }

        public bool AddJointWithMaterials(string phase, string attachmentType, string beforeMaterial, string afterMaterial, double position)
        {
            try
            {
                if (!canEditGeometry) return AppendMeasuredJoint(phase, attachmentType, afterMaterial, position, beforeMaterial);
                EnsureGeometryEditable();
                string normalized = NormalizePhase(phase);
                string[] phases = DraftJointPhases(normalized);
                double length = PhaseLength(normalized);
                if (!IsFinite(position) || position <= 0 || position >= length)
                    throw new ArgumentOutOfRangeException("position", normalized + "接头位置必须满足 0 < 位置 < " + ProjectText.Number(length) + " m。");
                if (joints.Any(item => item.Phase == normalized && Math.Abs(item.Position - position) < 0.000001))
                    throw new InvalidOperationException(normalized + "在该位置已存在接头。");
                position = Math.Round(position, 1, MidpointRounding.AwayFromZero);
                if (position <= 0 || position >= length || joints.Any(item => item.Phase == normalized && Math.Abs(item.Position - position) < .000001))
                    throw new ArgumentOutOfRangeException("position", "接头位置按一位小数后必须在电缆内且不重复。");
                var additions = phases.Select(targetPhase => new JointData
                {
                    Phase = targetPhase,
                    Name = CableJointStore.AutomaticJointName(joints.Count(item => item.Phase == targetPhase && item.Position < position) + 1),
                    AttachmentType = string.IsNullOrWhiteSpace(attachmentType) ? "户内冷缩" : attachmentType.Trim(),
                    Material = string.IsNullOrWhiteSpace(afterMaterial) ? MaterialAt(targetPhase, position) : afterMaterial.Trim(),
                    Position = position
                }).ToArray();
                foreach (var added in additions) { joints.Add(added); SetPrecedingSegmentMaterial(added, beforeMaterial); }
                joints.Sort((left, right) => left.Phase == right.Phase ? left.Position.CompareTo(right.Position) : string.CompareOrdinal(left.Phase, right.Phase));
                foreach (string targetPhase in phases) RenumberAutomaticJoints(targetPhase);
                MarkDirty();
                RefreshCableView();
                ResetJointMaterialDefaults();
                CompleteOperation("AddJoint", true, string.Empty);
                return true;
            }
            catch (Exception exception)
            {
                CompleteOperation("AddJoint", false, exception.Message);
                return false;
            }
        }

        public bool RemoveJoint(string phase, int zeroBasedIndex)
        {
            try
            {
                string normalized = NormalizePhase(phase);
                List<JointData> items = joints.Where(item => item.Phase == normalized).OrderBy(item => item.Position).ToList();
                if (zeroBasedIndex < 0 || zeroBasedIndex >= items.Count) throw new ArgumentOutOfRangeException("zeroBasedIndex");
                if (!canEditGeometry) return ChangeMeasuredJoint(items[zeroBasedIndex], true, 0, "", "");
                return RemoveDraftJoint(items[zeroBasedIndex]);
            }
            catch (Exception exception)
            {
                CompleteOperation("RemoveJoint", false, exception.Message);
                return false;
            }
        }

        public bool EditJoint(string phase, int zeroBasedIndex, double position, string attachmentType, string bodyType)
        { return EditJointWithMaterials(phase, zeroBasedIndex, position, attachmentType, "", bodyType); }

        public bool EditJointWithMaterials(string phase, int zeroBasedIndex, double position, string attachmentType, string beforeMaterial, string afterMaterial)
        {
            try
            {
                string normalized = NormalizePhase(phase);
                var items = joints.Where(item => item.Phase == normalized).OrderBy(item => item.Position).ToList();
                if (zeroBasedIndex < 0 || zeroBasedIndex >= items.Count) throw new ArgumentOutOfRangeException("zeroBasedIndex");
                JointData selected = items[zeroBasedIndex];
                if (!canEditGeometry) return ChangeMeasuredJoint(selected, false, position, attachmentType ?? "", afterMaterial ?? "", beforeMaterial ?? "");
                return EditDraftJoint(selected, position, attachmentType, beforeMaterial, afterMaterial);
            }
            catch (Exception error) { CompleteOperation("EditJoint", false, error.Message); return false; }
        }

        public void ClearJoints(string phase)
        {
            EnsureGeometryEditable();
            string[] phases = DraftJointPhases(NormalizePhase(phase));
            if (!ConfirmDraftMaterialMerge(joints.Where(j => phases.Contains(j.Phase)).ToArray(), false))
                throw new InvalidOperationException("未确认材料合并，接头未清空。");
            joints.RemoveAll(item => phases.Contains(item.Phase));
            MarkDirty();
            RefreshCableView();
        }

        public bool RequestHardwareAcquisition()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return InvokeCommandSafely(RequestHardwareAcquisition);
            if (hardwareDispatching) return true;
            hardwareDispatching = true;
            try
            {
                System.Threading.Interlocked.Increment(ref pollHardwareAcquisitionRequestSequence);
                CompleteOperation("HardwareAcquisitionRequested", true, string.Empty);
                SetStatus(EnvironmentText("正在采集温湿度与北斗定位…", "Reading temperature, humidity and GNSS…"), false);
                RaiseSafely(HardwareAcquisitionRequested);
                return true;
            }
            finally { hardwareDispatching = false; }
        }

        public bool ApplyHardwareSample(double temperatureCelsius, double relativeHumidity, double latitude, double longitude,
            double altitude, string fixStatus, DateTime sampleTime)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired)
            {
                try
                {
                    return (bool)Invoke(new Func<bool>(delegate
                    {
                        return ApplyHardwareSample(temperatureCelsius, relativeHumidity, latitude, longitude, altitude, fixStatus, sampleTime);
                    }));
                }
                catch (Exception exception)
                {
                    CompleteOperation("ApplyHardwareSample", false, exception.GetBaseException().Message);
                    return false;
                }
            }
            try
            {
                if (!IsFinite(temperatureCelsius) || temperatureCelsius < -80 || temperatureCelsius > 120) throw new ArgumentOutOfRangeException("temperatureCelsius");
                if (!IsFinite(relativeHumidity) || relativeHumidity < 0 || relativeHumidity > 100) throw new ArgumentOutOfRangeException("relativeHumidity");
                if (!IsFinite(latitude) || latitude < -90 || latitude > 90) throw new ArgumentOutOfRangeException("latitude");
                if (!IsFinite(longitude) || longitude < -180 || longitude > 180) throw new ArgumentOutOfRangeException("longitude");
                if (!IsFinite(altitude) || altitude < -1000 || altitude > 20000) throw new ArgumentOutOfRangeException("altitude");
                bool previous = suppressChanges;
                suppressChanges = true;
                try
                {
                    SetNumber(temperatureNumber, temperatureCelsius);
                    SetNumber(humidityNumber, relativeHumidity);
                    latitudeValue.Text = latitude.ToString("0.000000", CultureInfo.InvariantCulture);
                    longitudeValue.Text = longitude.ToString("0.000000", CultureInfo.InvariantCulture);
                    altitudeValue.Text = altitude.ToString("0.0", CultureInfo.InvariantCulture);
                    beidouFixStatusRaw = string.IsNullOrWhiteSpace(fixStatus) ? "未知" : fixStatus.Trim();
                    fixStatusValue.Text = ProjectCreationLocalization.TranslateCanonical(languageIndex, beidouFixStatusRaw);
                    HardwareSampleTime = sampleTime == DateTime.MinValue ? DateTime.Now : sampleTime;
                    sampleTimeValue.Text = HardwareSampleTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    HasValidEnvironmentSample = true;
                    EnvironmentSampleTime = HardwareSampleTime;
                    string status = (fixStatus ?? "").Trim();
                    HasValidGnssFix = status == "定位有效" || status == "定位成功" || status == "已定位" ||
                        status.Equals("Valid fix", StringComparison.OrdinalIgnoreCase) || status.Equals("3D fix", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("2D fix", StringComparison.OrdinalIgnoreCase);
                    HasGnssAltitude = HasValidGnssFix;
                    GnssSampleTime = HasValidGnssFix ? HardwareSampleTime.ToUniversalTime() : DateTime.MinValue;
                    if (!HasValidGnssFix) latitudeValue.Text = longitudeValue.Text = altitudeValue.Text = "—";
                }
                finally { suppressChanges = previous; }
                RefreshEnvironmentPresentation();
                System.Threading.Interlocked.Increment(ref pollHardwareSampleSequence);
                MarkDirty();
                CompleteOperation("ApplyHardwareSample", true, string.Empty);
                SetStatus("硬件数据已更新 · 北斗 " + ProjectCreationLocalization.TranslateCanonical(languageIndex, beidouFixStatusRaw), true);
                MaybeAutoRefreshWeather();
                return true;
            }
            catch (Exception exception)
            {
                CompleteOperation("ApplyHardwareSample", false, exception.Message);
                SetStatus("硬件数据无效 · " + exception.Message, false);
                return false;
            }
        }

        public string ApplyDataFileNameSuffix(string fileName)
        {
            return IsDoubleEnded ? ProjectCreationStorage.ApplyDoubleEndSuffix(fileName) : fileName ?? string.Empty;
        }

        public bool IsOppositeEndDataFile(string fileName)
        {
            string extension = Path.GetExtension(fileName ?? string.Empty);
            if (!extension.Equals(".cal", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".dem", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".loc", StringComparison.OrdinalIgnoreCase)) return false;
            return Path.GetFileNameWithoutExtension(fileName).EndsWith("D", StringComparison.OrdinalIgnoreCase);
        }

        public double GetDisplayPosition(int phaseIndex, double rawPosition, bool oppositeEnd)
        {
            if (!IsFinite(rawPosition)) throw new ArgumentOutOfRangeException("rawPosition");
            double length = PhaseLength(PhaseFromIndex(phaseIndex));
            if (rawPosition < 0 || rawPosition > length) throw new ArgumentOutOfRangeException("rawPosition");
            return oppositeEnd ? length - rawPosition : rawPosition;
        }

        void SetPhaseCableLength(int index, double value)
        {
            EnsureGeometryEditable();
            ValidateFiniteNonNegative(value, PhaseFromIndex(index) + "长度");
            SetNumber(LengthControl(index), value);
            if (CableMode == ProjectModes.ThreeCore && !suppressChanges)
            {
                bool previous = suppressChanges;
                suppressChanges = true;
                try { SetNumber(lengthANumber, value); SetNumber(lengthBNumber, value); SetNumber(lengthCNumber, value); }
                finally { suppressChanges = previous; }
            }
        }

        NumericUpDown LengthControl(int phaseIndex)
        {
            if (phaseIndex == 1) return lengthBNumber;
            if (phaseIndex == 2) return lengthCNumber;
            if (phaseIndex != 0) throw new ArgumentOutOfRangeException("phaseIndex", "相序号必须为 0、1 或 2。");
            return lengthANumber;
        }

        string PhaseFromIndex(int index)
        {
            if (index < 0 || index > 2) throw new ArgumentOutOfRangeException("phaseIndex", "相序号必须为 0、1 或 2。");
            return ProjectModes.Phases[index];
        }

        string NormalizePhase(string phase)
        {
            string text = (phase ?? string.Empty).Trim().ToUpperInvariant();
            if (text == "A" || text == "A相" || text == "A PHASE" || text == "PHASE A") return "A相";
            if (text == "B" || text == "B相" || text == "B PHASE" || text == "PHASE B") return "B相";
            if (text == "C" || text == "C相" || text == "C PHASE" || text == "PHASE C") return "C相";
            throw new ArgumentException("相别必须为 A相、B相或C相。", "phase");
        }

        double PhaseLength(string phase)
        {
            if (phase == "B相") return CableLengthB;
            if (phase == "C相") return CableLengthC;
            return CableLengthA;
        }

        string CurrentMaterial { get { return string.IsNullOrWhiteSpace(projectDefaultMaterial) ? "塑料绝缘" : projectDefaultMaterial; } }

        string FirstSegmentMaterial(string phase)
        {
            string material;
            return phaseFirstMaterials.TryGetValue(phase, out material) && !string.IsNullOrWhiteSpace(material)
                ? material
                : CurrentMaterial;
        }
        string MaterialAt(string phase, double position)
        {
            JointData preceding = joints.Where(j => j.Phase == phase && j.Position < position).OrderBy(j => j.Position).LastOrDefault();
            return preceding == null ? FirstSegmentMaterial(phase) : preceding.Material;
        }
        void RenumberAutomaticJoints(string phase)
        {
            int number = 0;
            foreach (JointData joint in joints.Where(j => j.Phase == phase).OrderBy(j => j.Position))
            { number++; if (CableJointStore.IsAutomaticJointName(joint.Name)) joint.Name = CableJointStore.AutomaticJointName(number); }
        }
        static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        void ValidateFiniteNonNegative(double value, string name)
        {
            if (!IsFinite(value) || value < 0 || value > 1000000)
                throw new ArgumentOutOfRangeException(name, name + "必须是 0 至 1,000,000 之间的有限数。");
        }

        void EnsureGeometryEditable()
        {
            if (!canEditGeometry)
                throw new InvalidOperationException("原项目已有测试数据，几何结构已锁定。请先调用 StartNewProjectFromLoaded() 创建新项目副本。");
        }
    }
}
