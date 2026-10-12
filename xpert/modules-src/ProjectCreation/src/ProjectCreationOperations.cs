using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        public string LastProjectSaveWarning { get; private set; }
        public string[] ValidateProject()
        {
            List<ProjectValidationIssue> issues = ValidateDraftDetailed(BuildDraft(false));
            ApplyValidationIssues(issues, false);
            return issues.Select(item => item.Message).Distinct().ToArray();
        }

        public bool SaveProject()
        {
            return SaveProjectToRoot(dataRootDirectory);
        }

        public bool SaveProjectToRoot(string rootDirectory)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(SaveProjectToRoot), rootDirectory);
            if (IsBusy) return false;
            IsBusy = true;
            LastProjectSaveWarning = string.Empty;
            ResetSaveValidationNotice();
            try
            {
                ProjectDraft draft = BuildDraft(true);
                bool cableStructureChanged = GeometryChangedFromLoaded();
                List<ProjectValidationIssue> validationIssues = ValidateDraftDetailed(draft);
                if (!cableStructureChanged && !String.IsNullOrEmpty(loadedProjectPath) && SaveDisposition == "更新原项目")
                    // Legacy duplicate phase blocks may share joint positions.
                    // The transaction validates each original physical block and
                    // preserves its bytes; the flattened editor is not rewritten.
                    validationIssues.RemoveAll(issue => issue.FieldCode == "CableStructure");
                ApplyValidationIssues(validationIssues, false);
                if (validationIssues.Count > 0)
                    return RejectInvalidSave(validationIssues);
                string savedPath, committedCableHash = null, committedItemHash = null;
                bool updatingExisting=string.Equals(SaveDisposition, "更新原项目", StringComparison.OrdinalIgnoreCase);
                if (updatingExisting)
                {
                    if (string.IsNullOrWhiteSpace(loadedProjectPath)) throw new InvalidOperationException("当前没有可更新的已加载项目。");
                    bool measured = ProjectCreationStorage.HasExistingTestData(loadedProjectPath);
                    if (measured && GeometryChangedFromLoaded())
                        throw new InvalidOperationException("原项目已有测试数据，禁止原位修改电缆长度、芯数或接头。请先调用 StartNewProjectFromLoaded()，再保存为新项目。");
                    if (measured)
                    {
                        committedCableHash = loadedJointCableHash;
                        savedPath = ProjectCreationStorage.UpdateMeasuredMetadata(draft, loadedProjectPath, loadedJointCableHash, loadedJointItemHash, out committedItemHash);
                    }
                    else savedPath = ProjectCreationStorage.UpdateExisting(draft, loadedProjectPath,
                        loadedJointCableHash, loadedJointItemHash, out committedCableHash, out committedItemHash, !cableStructureChanged);
                }
                else savedPath = ProjectCreationStorage.SaveNew(draft, rootDirectory);
                currentProjectPath = savedPath;
                lastSavedProjectPath = savedPath;
                loadedProjectPath = savedPath;
                loadedLengthA = draft.LengthA;
                loadedLengthB = draft.LengthB;
                loadedLengthC = draft.LengthC;
                loadedGeometryFingerprint = GeometryFingerprint(draft);
                HasExistingTestData = ProjectCreationStorage.HasExistingTestData(savedPath);
                canEditGeometry = !HasExistingTestData;
                if (committedItemHash != null)
                {
                    // The form represents our own write, not a newer external
                    // edit that happened after it. Preserve that exact baseline.
                    loadedJointCableHash = committedCableHash;
                    loadedJointItemHash = committedItemHash;
                }
                else CaptureLoadedJointMetadata();
                SaveDisposition = "更新原项目";
                ApplyGeometryProtection();
                CaptureHistory();
                catalog.LastPressureMode = ProjectPressureModes.Normalize(draft.PressureMode);
                try { catalog.Save(settingsPath); }
                catch (Exception cacheError) {
                    LastProjectSaveWarning = MaterialText("项目已保存，但配置缓存未更新：", "Project saved, but the reference cache was not updated: ") + cacheError.Message;
                    CustomerProjectMessage(cacheError.ToString(),"项目已保存，但常用选项未保存。下次打开时请核对。","The project was saved, but preferences were not saved. Check them when reopening.");
                }
                bool previousSuppression = suppressChanges;
                suppressChanges = true;
                try { ApplyCatalogToControls(); }
                finally { suppressChanges = previousSuppression; }
                LastSavedProjectOperationCode=updatingExisting?"UPDATE":"CREATE";
                System.Threading.Interlocked.Increment(ref pollProjectSavedSequence);
                UpdateSavedState(true, false);
                CompleteOperation("SaveProject", true, string.Empty);
                string savedNotice=MaterialText("项目已保存。","The project was saved.")+(String.IsNullOrEmpty(LastProjectSaveWarning)?"":" "+MaterialText("常用选项未保存，下次打开时请核对。","Preferences were not saved. Check them when reopening."));
                SetStatus(savedNotice, String.IsNullOrEmpty(LastProjectSaveWarning));
                RaiseSafely(ProjectSaved);
                if (cableStructureChanged) RaiseSafely(CableStructureChanged);
                if (ShowDialogMessages && !String.IsNullOrEmpty(LastProjectSaveWarning))
                    MessageBox.Show(this, savedNotice, L("项目建立"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
            catch (Exception exception)
            {
                CompleteOperation("SaveProject", false, exception.Message);
                string notice=CustomerProjectMessage(exception.ToString(),"项目未保存。请检查必填信息及保存位置是否可写后重试。","The project was not saved. Check required information and storage access, then retry.");
                SetStatus(notice, false);
                if (ShowDialogMessages) MessageBox.Show(this, notice, L("保存失败"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { IsBusy = false; }
        }

        public bool LoadProject(string projectPath)
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return (bool)Invoke(new Func<string, bool>(LoadProject), projectPath);
            if (IsBusy) return false;
            IsBusy = true;
            try
            {
                string projectDirectory = Path.GetFullPath(projectPath);
                string cableFile = Path.Combine(projectDirectory, "CABLE.in"), itemFile = Path.Combine(projectDirectory, "ITEM.in");
                string cableHash = HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(cableFile));
                string itemHash = HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(itemFile));
                ProjectDraft loaded = ProjectCreationStorage.Load(projectDirectory);
                if (cableHash != HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(cableFile)) ||
                    itemHash != HVX.ProjectCables.CableJointStore.Hash(File.ReadAllBytes(itemFile)))
                    throw new InvalidOperationException("项目在加载过程中被其他操作修改，请重新加载；当前页面内容仍保留。");
                ApplyDraft(loaded);
                ClearValidationPresentation();
                loadedProjectPath = projectDirectory;
                currentProjectPath = loadedProjectPath;
                HasExistingTestData = ProjectCreationStorage.HasExistingTestData(loadedProjectPath);
                canEditGeometry = !HasExistingTestData;
                loadedLengthA = loaded.LengthA;
                loadedLengthB = loaded.LengthB;
                loadedLengthC = loaded.LengthC;
                loadedGeometryFingerprint = GeometryFingerprint(BuildDraft(false));
                // Keep the baseline paired with the verified draft. Re-reading it
                // after applying the form could bless a concurrent external edit.
                loadedJointCableHash = cableHash;
                loadedJointItemHash = itemHash;
                SaveDisposition = "更新原项目";
                CaptureHistory();
                catalog.LastPressureMode = ProjectPressureModes.Normalize(loaded.PressureMode);
                CaptureLoadedReferenceData(loaded);
                string cacheWarning = SaveCatalogQuietly();
                bool previousSuppression = suppressChanges;
                suppressChanges = true;
                try { ApplyCatalogToControls(); }
                finally { suppressChanges = previousSuppression; }
                ApplyGeometryProtection();
                System.Threading.Interlocked.Increment(ref pollProjectLoadedSequence);
                UpdateSavedState(true, false);
                CompleteOperation("LoadProject", true, string.Empty);
                SetStatus((HasExistingTestData ? "已加载 · 长度、芯数和始终端受保护；可增删改中间接头，既有分析需重核" : "已加载 · 修改后保存到原项目") +
                    (String.IsNullOrEmpty(cacheWarning) ? "" : " · " + cacheWarning), true);
                RaiseSafely(ProjectLoaded);
                return true;
            }
            catch (Exception exception)
            {
                CompleteOperation("LoadProject", false, exception.Message);
                string notice=CustomerProjectMessage(exception.ToString(),"无法加载项目。请检查项目文件及访问权限后重试。","The project could not be loaded. Check project files and access permissions, then retry.");
                SetStatus(notice, false);
                if (ShowDialogMessages) MessageBox.Show(this, notice, MaterialText("加载失败","Load failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { IsBusy = false; }
        }

        public bool StartNewProjectFromLoaded()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return InvokeCommandSafely(StartNewProjectFromLoaded);
            if (string.IsNullOrWhiteSpace(loadedProjectPath)) return false;
            loadedProjectPath = string.Empty;
            currentProjectPath = string.Empty;
            loadedGeometryFingerprint = string.Empty;
            HasExistingTestData = false;
            canEditGeometry = true;
            SaveDisposition = "新建项目";
            ApplyGeometryProtection();
            MarkDirty();
            SetStatus("已从原项目复制资料 · 后续保存将创建新项目，原目录不变", false);
            return true;
        }

        public void ClearProject()
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { Invoke(new Action(ClearProject)); return; }
            ProjectDraft empty = new ProjectDraft();
            empty.LengthA = empty.LengthB = empty.LengthC = 0;
            empty.PressureMode = catalog.LastPressureMode;
            empty.PressureSteps.AddRange(catalog.GetPressureSteps(empty.PressureMode));
            ApplyDraft(empty);
            ClearValidationPresentation();
            loadedProjectPath = currentProjectPath = lastSavedProjectPath = string.Empty;
            loadedGeometryFingerprint = string.Empty;
            HasExistingTestData = false;
            canEditGeometry = true;
            SaveDisposition = "新建项目";
            emptyState = true;
            ApplyGeometryProtection();
            System.Threading.Interlocked.Increment(ref pollProjectClearedSequence);
            UpdateSavedState(false, true);
            CompleteOperation("ClearProject", true, string.Empty);
            SetStatus("已清空 · 磁盘上的既有项目未被删除", false);
            RaiseSafely(ProjectCleared);
        }

        public bool RequestProjectManager()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return InvokeCommandSafely(RequestProjectManager);
            if (projectManagerDispatching) return true;
            projectManagerDispatching = true;
            try
            {
                System.Threading.Interlocked.Increment(ref pollProjectManagerRequestSequence);
                CompleteOperation("ProjectManagerRequested", true, string.Empty);
                SetStatus("已请求打开任务35项目管理 · 等待 LabVIEW 返回项目路径", false);
                RaiseSafely(ProjectManagerRequested);
                return true;
            }
            finally { projectManagerDispatching = false; }
        }

        public bool RequestExit()
        {
            if (IsDisposed || Disposing) return false;
            if (InvokeRequired) return InvokeCommandSafely(RequestExit);
            if (exitDispatching) return true;
            exitDispatching = true;
            try
            {
                exitRequested = true;
                System.Threading.Interlocked.Increment(ref pollExitRequestSequence);
                CompleteOperation("ExitRequested", true, string.Empty);
                SetStatus(EmbeddedMode?"已请求退出当前项目并返回主页":"已向宿主发出退出请求", false);
                RaiseSafely(ExitRequested);
                return true;
            }
            finally { exitDispatching = false; }
        }

        bool GeometryChangedFromLoaded()
        {
            return string.IsNullOrEmpty(loadedGeometryFingerprint) ||
                   !string.Equals(loadedGeometryFingerprint, GeometryFingerprint(BuildDraft(false)), StringComparison.Ordinal);
        }

        static string GeometryFingerprint(ProjectDraft draft)
        {
            List<string> parts = new List<string>
            {
                draft.CableMode,
                draft.StartType,
                draft.EndType,
                ProjectText.Number(draft.LengthA),
                ProjectText.Number(draft.LengthB),
                ProjectText.Number(draft.LengthC)
            };
            foreach (string phase in ProjectModes.Phases)
            {
                parts.Add(phase);
                parts.Add(draft.FirstMaterialFor(phase) ?? string.Empty);
                foreach (JointData joint in draft.Joints.Where(item => item.Phase == phase).OrderBy(item => item.Position))
                {
                    parts.Add(joint.Name ?? string.Empty);
                    parts.Add(joint.AttachmentType ?? string.Empty);
                    parts.Add(joint.Material ?? string.Empty);
                    parts.Add(ProjectText.Number(joint.Position));
                }
            }
            return string.Join("\u001f", parts.Select(part => (part ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture) + ":" + (part ?? string.Empty)));
        }

        ProjectDraft BuildDraft(bool includePressure)
        {
            ProjectDraft draft = new ProjectDraft
            {
                Organization = Organization,
                OperatorName = OperatorName,
                TestLocation = TestLocation,
                TestDateTime = TestDateTime,
                DualEnded = IsDoubleEnded,
                Humidity = RelativeHumidity,
                Temperature = TemperatureCelsius,
                Weather = Weather,
                WeatherCode = WeatherCode,
                WeatherProvider = WeatherProvider,
                WeatherUpdatedAt = WeatherUpdatedAt,
                Latitude = BeidouLatitude,
                Longitude = BeidouLongitude,
                Altitude = BeidouAltitude,
                BeidouFixStatus = BeidouFixStatus,
                HardwareSampleTime = HardwareSampleTime,
                EnvironmentSampleValid = HasValidEnvironmentSample,
                EnvironmentSampleTime = EnvironmentSampleTime,
                GnssFixValid = HasValidGnssFix,
                GnssAltitudeValid = HasGnssAltitude,
                GnssSampleTime = GnssSampleTime,
                StartName = StartName,
                StartType = StartTerminalType,
                DispatchNumberStart = DispatchNumberStart,
                EndName = EndName,
                EndType = EndTerminalType,
                DispatchNumberEnd = DispatchNumberEnd,
                LineName = LineName,
                CableModel = CableModel,
                CableMode = CableMode,
                DefaultMaterial = CurrentMaterial,
                LengthA = CableLengthA,
                LengthB = CableLengthB,
                LengthC = CableLengthC,
                PhaseVoltage = PhaseVoltage,
                PressureMode = PressureMode,
                LayingDate = LayingDate,
                InsulationA1 = InsulationA1,
                InsulationA2 = InsulationA2,
                InsulationB1 = InsulationB1,
                InsulationB2 = InsulationB2,
                InsulationC1 = InsulationC1,
                InsulationC2 = InsulationC2,
                InsulationReferenceProfile = insulationReferenceProfile,
                InsulationConditionsComparable = InsulationConditionsComparable,
                Notes = Notes
            };
            foreach (JointData joint in joints) draft.Joints.Add(joint.Clone());
            foreach (KeyValuePair<string, string> pair in phaseFirstMaterials) draft.FirstSegmentMaterials[pair.Key] = pair.Value;
            if (draft.PressureMode != ProjectPressureModes.Arbitrary && (includePressure || currentPressureSteps.Count > 0))
                foreach (PressureStep step in currentPressureSteps) draft.PressureSteps.Add(step.Clone());
            return draft;
        }

        List<string> ValidateDraft(ProjectDraft draft)
        {
            return ValidateDraftDetailed(draft).Select(item => item.Message).Distinct().ToList();
        }

        void ApplyDraft(ProjectDraft draft)
        {
            // Validate every bounded input before changing any live editor or
            // lifecycle state. A rejected incoming project must leave the old
            // project's path, fields and unsaved draft paired and untouched.
            RequirePresentableNumber(humidityNumber, draft.Humidity);
            RequirePresentableNumber(temperatureNumber, draft.Temperature);
            RequirePresentableNumber(lengthANumber, draft.LengthA);
            RequirePresentableNumber(lengthBNumber, draft.LengthB);
            RequirePresentableNumber(lengthCNumber, draft.LengthC);
            if (draft.PressureSteps.Any(step => Double.IsNaN(step.Multiplier) || Double.IsInfinity(step.Multiplier)))
                throw new InvalidDataException("加压倍数必须为有限数值，当前项目未更改。");
            ResetCapacitorLifecycleState();
            suppressChanges = true;
            try
            {
                Organization = draft.Organization; OperatorName = draft.OperatorName; TestLocation = draft.TestLocation;
                TestDateTime = draft.TestDateTime; IsDoubleEnded = draft.DualEnded; RelativeHumidity = draft.Humidity;
                TemperatureCelsius = draft.Temperature; Weather = draft.Weather;
                latitudeValue.Text = draft.Latitude.ToString("0.000000", CultureInfo.InvariantCulture);
                longitudeValue.Text = draft.Longitude.ToString("0.000000", CultureInfo.InvariantCulture);
                altitudeValue.Text = draft.Altitude.ToString("0.0", CultureInfo.InvariantCulture);
                beidouFixStatusRaw = string.IsNullOrWhiteSpace(draft.BeidouFixStatus) ? "未采集" : draft.BeidouFixStatus;
                fixStatusValue.Text = ProjectCreationLocalization.TranslateCanonical(languageIndex, beidouFixStatusRaw); HardwareSampleTime = draft.HardwareSampleTime;
                sampleTimeValue.Text = HardwareSampleTime == DateTime.MinValue ? "—" : HardwareSampleTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                RestoreEnvironmentState(draft);
                StartName = draft.StartName; StartTerminalType = draft.StartType; DispatchNumberStart = draft.DispatchNumberStart;
                EndName = draft.EndName; EndTerminalType = draft.EndType; DispatchNumberEnd = draft.DispatchNumberEnd;
                LineName = draft.LineName; CableModel = draft.CableModel; SelectChoice(cableModeCombo, draft.CableMode);
                SelectChoice(bodyTypeCombo, draft.DefaultMaterial);
                SelectChoice(beforeBodyTypeCombo, draft.DefaultMaterial);
                projectDefaultMaterial = draft.DefaultMaterial;
                SetLoadedLength(lengthANumber, draft.LengthA); SetLoadedLength(lengthBNumber, draft.LengthB); SetLoadedLength(lengthCNumber, draft.LengthC);
                PhaseVoltage = draft.PhaseVoltage; LayingDate = draft.LayingDate; SelectChoice(pressureModeCombo, ProjectPressureModes.Normalize(draft.PressureMode));
                InsulationA1 = draft.InsulationA1; InsulationA2 = draft.InsulationA2; InsulationB1 = draft.InsulationB1;
                InsulationB2 = draft.InsulationB2; InsulationC1 = draft.InsulationC1; InsulationC2 = draft.InsulationC2; Notes = draft.Notes;
                InsulationReferenceProfile = InsulationReferenceRules.Find(draft.InsulationReferenceProfile) == null
                    ? string.Empty : draft.InsulationReferenceProfile;
                InsulationConditionsComparable = draft.InsulationConditionsComparable;
                joints.Clear(); foreach (JointData joint in draft.Joints) joints.Add(joint.Clone());
                phaseFirstMaterials.Clear();
                foreach (KeyValuePair<string, string> pair in draft.FirstSegmentMaterials) phaseFirstMaterials[pair.Key] = pair.Value;
                currentPressureSteps = draft.PressureMode == ProjectPressureModes.Arbitrary ? new List<PressureStep>() :
                    draft.PressureSteps.Count > 0 ? draft.PressureSteps.Select(step => step.Clone()).ToList() : catalog.GetPressureSteps(draft.PressureMode);
                voltagePlanName = string.Empty;
                emptyState = false;
                RestoreWeatherMetadata(draft.WeatherCode, draft.WeatherProvider, draft.WeatherUpdatedAt);
            }
            finally { suppressChanges = false; }
            ApplyModeState(false);
            RefreshCableView();
            ResetJointMaterialDefaults();
            RefreshPressurePlanPreview();
        }

        void CaptureHistory()
        {
            AddHistory(catalog.Organizations, Organization);
            AddHistory(catalog.Operators, OperatorName);
        }

        void CaptureLoadedReferenceData(ProjectDraft draft)
        {
            AddUnique(catalog.TerminalTypes, draft.StartType);
            AddUnique(catalog.TerminalTypes, draft.EndType);
            AddUnique(catalog.PhaseVoltages, draft.PhaseVoltage);
            foreach (JointData joint in draft.Joints)
            {
                AddUnique(catalog.AttachmentTypes, joint.AttachmentType);
            }
            // Historical project material names remain visible in the editors,
            // but are not reinserted into the catalog with an invented velocity.
            // Adding a material and its velocity is an explicit catalog operation.
        }

        static void AddUnique(List<string> target, string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length > 0 && !target.Contains(text, StringComparer.OrdinalIgnoreCase)) target.Add(text);
        }

        static void AddHistory(List<string> target, string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return;
            target.RemoveAll(item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));
            target.Insert(0, text);
            while (target.Count > 50) target.RemoveAt(target.Count - 1);
        }

        void RaiseSafely(EventHandler handler)
        {
            if (handler == null) return;
            foreach (EventHandler callback in handler.GetInvocationList())
            {
                if (IsDisposed || Disposing) break;
                try { callback(this, EventArgs.Empty); }
                catch (Exception exception) { LastHostCallbackError = exception.GetBaseException().Message; }
            }
        }

        void CompleteOperation(string operation, bool succeeded, string error)
        {
            LastOperation = operation;
            LastOperationSucceeded = succeeded;
            LastError = error ?? string.Empty;
            System.Threading.Interlocked.Increment(ref pollLastOperationSequence);
        }
    }
}
