using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        void ApplyCatalogToControls()
        {
            FillChoiceCombo(startTypeCombo, catalog.TerminalTypes, StartTerminalType);
            FillChoiceCombo(endTypeCombo, catalog.TerminalTypes, EndTerminalType);
            FillChoiceCombo(attachmentTypeCombo, catalog.AttachmentTypes, GetChoiceValue(attachmentTypeCombo));
            FillMaterialChoice(bodyTypeCombo, GetChoiceValue(bodyTypeCombo));
            FillMaterialChoice(beforeBodyTypeCombo, GetChoiceValue(beforeBodyTypeCombo));
            FillChoiceCombo(phaseVoltageCombo, catalog.PhaseVoltages, PhaseVoltage);
            FillCombo(organizationCombo, catalog.Organizations, Organization);
            FillCombo(operatorCombo, catalog.Operators, OperatorName);
            if (cableModeCombo.SelectedIndex < 0) cableModeCombo.SelectedIndex = 0;
            if (phaseVoltageCombo.SelectedIndex < 0 && phaseVoltageCombo.Items.Count > 0) phaseVoltageCombo.SelectedIndex = Math.Min(1, phaseVoltageCombo.Items.Count - 1);
            if (startTypeCombo.SelectedIndex < 0 && startTypeCombo.Items.Count > 0) startTypeCombo.SelectedIndex = 0;
            if (endTypeCombo.SelectedIndex < 0 && endTypeCombo.Items.Count > 0) endTypeCombo.SelectedIndex = 0;
            if (attachmentTypeCombo.SelectedIndex < 0 && attachmentTypeCombo.Items.Count > 0) attachmentTypeCombo.SelectedIndex = 0;
            if (bodyTypeCombo.SelectedIndex < 0 && bodyTypeCombo.Items.Count > 0) bodyTypeCombo.SelectedIndex = 0;
            if (beforeBodyTypeCombo.SelectedIndex < 0 && beforeBodyTypeCombo.Items.Count > 0) beforeBodyTypeCombo.SelectedIndex = 0;
            if (selectedPhaseCombo.SelectedIndex < 0) selectedPhaseCombo.SelectedIndex = 0;
            if (pressureModeCombo.SelectedIndex < 0) SelectChoice(pressureModeCombo, catalog.LastPressureMode);
            AttachReferenceMenus();
        }

        static void FillCombo(ComboBox combo, IEnumerable<string> values, string selected)
        {
            if (combo == null) return;
            string keep = selected ?? combo.Text;
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                foreach (string value in values.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase)) combo.Items.Add(value);
                SelectOrAdd(combo, keep);
            }
            finally { combo.EndUpdate(); }
        }

        void AttachReferenceMenus()
        {
            DisposeReferenceMenus();
            startTypeCombo.ContextMenuStrip = BuildSimpleMenu("终端类型", catalog.TerminalTypes, delegate { ApplyCatalogToControls(); });
            endTypeCombo.ContextMenuStrip = startTypeCombo.ContextMenuStrip;
            attachmentTypeCombo.ContextMenuStrip = BuildSimpleMenu("附件类型", catalog.AttachmentTypes, delegate { ApplyCatalogToControls(); });
            phaseVoltageCombo.ContextMenuStrip = BuildSimpleMenu("相电压", catalog.PhaseVoltages, delegate { ApplyCatalogToControls(); });
            AttachMaterialCatalogMenu();
        }

        void DisposeReferenceMenus()
        {
            HashSet<ContextMenuStrip> owned = new HashSet<ContextMenuStrip>();
            foreach (Control control in new Control[] { startTypeCombo, endTypeCombo, attachmentTypeCombo, phaseVoltageCombo, bodyTypeCombo, beforeBodyTypeCombo })
            {
                if (control == null || control.ContextMenuStrip == null) continue;
                owned.Add(control.ContextMenuStrip);
                control.ContextMenuStrip = null;
            }
            foreach (ContextMenuStrip menu in owned) RetireReferenceMenu(menu);
            if (Disposing || IsDisposed) DisposeRetiredReferenceMenus();
        }

        ContextMenuStrip BuildSimpleMenu(string label, List<string> target, Action refresh)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(L("新增") + L(label) + "…", null, delegate
            {
                using (TextPromptDialog dialog = new TextPromptDialog(L("新增") + L(label), L("请输入") + L(label) + "：", string.Empty, languageIndex))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.Value)) return;
                    if (!target.Contains(dialog.Value, StringComparer.OrdinalIgnoreCase)) target.Add(dialog.Value);
                    SaveCatalogQuietly(); refresh(); MarkDirty();
                }
            });
            menu.Items.Add(L("删除当前") + L(label), null, delegate
            {
                ComboBox owner = menu.SourceControl as ComboBox;
                if (owner == null || target.Count <= 1) return;
                target.RemoveAll(item => string.Equals(item, GetChoiceValue(owner), StringComparison.OrdinalIgnoreCase));
                SaveCatalogQuietly(); refresh(); MarkDirty();
            });
            menu.Items.Add(L("恢复") + L(label) + L("默认值"), null, delegate
            {
                ReferenceCatalog defaults = ReferenceCatalog.CreateDefaults();
                IEnumerable<string> source = label == "终端类型" ? defaults.TerminalTypes : label == "附件类型" ? defaults.AttachmentTypes : defaults.PhaseVoltages;
                target.Clear(); target.AddRange(source); SaveCatalogQuietly(); refresh(); MarkDirty();
            });
            return menu;
        }

        string SaveCatalogQuietly()
        {
            try { catalog.Save(settingsPath); return string.Empty; }
            catch (Exception exception) { string warning = "配置缓存未更新 · " + exception.Message; SetStatus(warning, false); return warning; }
        }

        void AttachChangeTracking()
        {
            endNameText.TextChanged += delegate { RefreshCapacitorIdCache(); };
            dispatchEndText.TextChanged += delegate { RefreshCapacitorIdCache(); };
            Control[] textControls = { organizationCombo, operatorCombo, testLocationText, weatherText, startNameText, startTypeCombo,
                dispatchStartText, endNameText, endTypeCombo, dispatchEndText, lineNameText, cableModelText, phaseVoltageCombo,
                insulationA1Text, insulationA2Text, insulationB1Text, insulationB2Text, insulationC1Text, insulationC2Text, notesText };
            foreach (Control control in textControls) control.TextChanged += delegate { MarkDirty(); };
            phaseVoltageCombo.TextChanged += delegate { RefreshPressurePlanPreview(); };
            startTypeCombo.SelectedIndexChanged+=delegate{RefreshTopologyTerminations();};
            endTypeCombo.SelectedIndexChanged+=delegate{RefreshTopologyTerminations();};
            dualEndedCheck.CheckedChanged += delegate { MarkDirty(); };
            testDatePicker.ValueChanged += delegate { MarkDirty(); };
            layingDatePicker.ValueChanged += delegate { MarkDirty(); };
            humidityNumber.ValueChanged += delegate { MarkDirty(); };
            temperatureNumber.ValueChanged += delegate { MarkDirty(); };
            lengthANumber.ValueChanged += delegate
            {
                if (!suppressChanges && CableMode == ProjectModes.ThreeCore)
                {
                    suppressChanges = true;
                    try { lengthBNumber.Value = lengthANumber.Value; lengthCNumber.Value = lengthANumber.Value; }
                    finally { suppressChanges = false; }
                }
                MarkDirty(); RefreshCableView();
            };
            lengthBNumber.ValueChanged += delegate { MarkDirty(); RefreshCableView(); };
            lengthCNumber.ValueChanged += delegate { MarkDirty(); RefreshCableView(); };
            cableModeCombo.SelectedIndexChanged += delegate { if (!suppressChanges) { ApplyModeState(true); MarkDirty(); } };
            pressureModeCombo.SelectedIndexChanged += delegate { if (!suppressChanges) { LoadPressurePreset(); MarkDirty(); } };
            bodyTypeCombo.SelectedIndexChanged += delegate
            {
                if (suppressChanges) return;
                jointAfterMaterialChanged = true;
            };
            beforeBodyTypeCombo.SelectedIndexChanged += delegate { if (!suppressChanges) jointBeforeMaterialChanged = true; };
            beforeBodyTypeCombo.DropDown += delegate { RefreshSharedMaterialChoices(); };
            bodyTypeCombo.DropDown += delegate { RefreshSharedMaterialChoices(); };
            selectedPhaseCombo.SelectedIndexChanged += delegate { RefreshCableView(); RefreshJointMaterialFromPosition(); };
            jointPositionNumber.ValueChanged += delegate { if (!suppressChanges) { jointEditorPositionChanged = true; RefreshJointMaterialFromPosition(); } };
            languageCombo.SelectedIndexChanged += delegate { if (!suppressChanges && languageCombo.SelectedIndex >= 0) LanguageIndex = languageCombo.SelectedIndex - 1; };
            acquireHardwareButton.Click += delegate { RequestHardwareAcquisition(); };
            weatherRefreshButton.Click += delegate { RequestWeatherRefresh(); };
            if (hardwareToggleButton != null)
                hardwareToggleButton.Click += delegate { EnvironmentDetailsExpanded = !EnvironmentDetailsExpanded; };
            pressureEditButton.Click += delegate { EditPressureList(); };
            capacitorIdRefreshButton.Click += delegate { RefreshCapacitorId(); };
            addJointButton.Click += delegate { AddJointFromUi(); };
            editJointButton.Click += delegate { EditSelectedJoint(); };
            deleteJointButton.Click += delegate { DeleteSelectedJoint(); };
            saveButton.Click += delegate { SaveProject(); };
            loadButton.Click += delegate { RequestProjectManager(); };
            clearButton.Click += delegate { ClearProject(); };
            exitButton.Click += delegate { RequestExit(); };
            cableGrid.SelectionChanged += delegate { PopulateJointEditorFromSelection(); };
            EventHandler unlock = delegate
            {
                if (canEditGeometry || string.IsNullOrWhiteSpace(loadedProjectPath)) return;
                if (MessageBox.Show(this, "原项目已有测试数据。是否基于当前资料新建项目后再修改？\r\n原项目不会被改动。", "几何信息受保护", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    StartNewProjectFromLoaded();
            };
            lengthANumber.DoubleClick += unlock; lengthBNumber.DoubleClick += unlock; lengthCNumber.DoubleClick += unlock; cableGrid.DoubleClick += unlock;
        }

        void ApplyModeState(bool normalizeValues)
        {
            string mode = CableMode;
            bool enteringCapacitor = mode == ProjectModes.Capacitor && lastAppliedCableMode != ProjectModes.Capacitor;
            bool leavingCapacitor = mode != ProjectModes.Capacitor && lastAppliedCableMode == ProjectModes.Capacitor;
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                if (enteringCapacitor)
                {
                    if (normalizeValues)
                    {
                        CaptureCableStateBeforeCapacitor();
                    }
                    else
                    {
                        string loadedId = NormalizeCapacitorId(DispatchNumberEnd);
                        if (string.IsNullOrWhiteSpace(loadedId)) loadedId = NormalizeCapacitorId(EndName);
                        if (!string.IsNullOrWhiteSpace(loadedId)) capacitorId = loadedId;
                    }
                    if (string.IsNullOrWhiteSpace(capacitorId)) capacitorId = CreateCapacitorId();
                    ApplyCapacitorIdentity(normalizeValues);
                }
                else if (leavingCapacitor && normalizeValues)
                {
                    RestoreCableStateAfterCapacitor();
                }
                if (normalizeValues)
                {
                    if (mode == ProjectModes.ThreeCore)
                    {
                        decimal value = lengthANumber.Value > 0 ? lengthANumber.Value : Math.Max(lengthBNumber.Value, lengthCNumber.Value);
                        lengthANumber.Value = lengthBNumber.Value = lengthCNumber.Value = value;
                    }
                    else if (mode == ProjectModes.SinglePhase)
                    {
                        lengthBNumber.Value = 0; lengthCNumber.Value = 0;
                        joints.RemoveAll(item => item.Phase != "A相");
                    }
                    else if (mode == ProjectModes.ThreePhaseSingleCore)
                    {
                        if (lengthBNumber.Value == 0) lengthBNumber.Value = lengthANumber.Value;
                        if (lengthCNumber.Value == 0) lengthCNumber.Value = lengthANumber.Value;
                    }
                }
                if (mode == ProjectModes.Capacitor)
                {
                    lengthANumber.Value = lengthBNumber.Value = lengthCNumber.Value = 0;
                    joints.Clear();
                }
                bool cable = mode != ProjectModes.Capacitor && canEditGeometry;
                lengthANumber.Enabled = cable;
                lengthBNumber.Enabled = cable && mode == ProjectModes.ThreePhaseSingleCore;
                lengthCNumber.Enabled = cable && mode == ProjectModes.ThreePhaseSingleCore;
                selectedPhaseCombo.Enabled = cable && mode == ProjectModes.ThreePhaseSingleCore;
                if (!selectedPhaseCombo.Enabled) selectedPhaseCombo.SelectedIndex = 0;
                jointPositionNumber.Enabled = attachmentTypeCombo.Enabled = beforeBodyTypeCombo.Enabled = bodyTypeCombo.Enabled = cable;
                addJointButton.Enabled = editJointButton.Enabled = deleteJointButton.Enabled = cable;
                if (CanAppendMeasuredJoint)
                {
                    // The shared dialog confirms actual phase scope and materials.
                    // Length and endpoints remain locked. Interior joints use
                    // separate hash-checked append/edit/delete transactions.
                    selectedPhaseCombo.Enabled = mode == ProjectModes.ThreePhaseSingleCore;
                    jointPositionNumber.Enabled = addJointButton.Enabled = attachmentTypeCombo.Enabled = beforeBodyTypeCombo.Enabled = bodyTypeCombo.Enabled = true;
                    editJointButton.Enabled = deleteJointButton.Enabled = true;
                }
                if (cableGrid.ContextMenuStrip != null)
                    foreach (ToolStripItem item in cableGrid.ContextMenuStrip.Items) item.Enabled = cable || CanAppendMeasuredJoint;
                RefreshLoadModePresentation();
            }
            finally { suppressChanges = previous; }
            lastAppliedCableMode = mode;
            RefreshProjectPageTitle();
            RefreshCapacitorIdCache();
            RefreshCableView();
        }

        void RefreshLoadModePresentation()
        {
            if (cableModeCombo == null || electricalFields == null) return;
            string mode = CableMode;
            bool capacitor = mode == ProjectModes.Capacitor;
            bool independentPhases = mode == ProjectModes.ThreePhaseSingleCore;
            bool singlePhase = mode == ProjectModes.SinglePhase;
            bool chinese = Xiongxin.OWTS.Localization.UiLanguage.IsChinese(languageIndex);

            // Three-core draft joints apply to every enabled phase. Display the
            // actual common scope, retaining the original canonical phase input
            // used by DraftJointPhases; never write an invented phase to CABLE.
            if (selectedPhaseCombo != null)
            {
                bool previous = suppressChanges; suppressChanges = true;
                try
                {
                    if (mode == ProjectModes.ThreeCore)
                    {
                        if (selectedPhaseCombo.Items.Count != 1 || selectedPhaseCombo.Items[0].ToString() != "ALL")
                        {
                            selectedPhaseCombo.Items.Clear();
                            selectedPhaseCombo.Items.Add(new LocalizedChoice("A相", "ALL"));
                            selectedPhaseCombo.SelectedIndex = 0;
                        }
                    }
                    else if (selectedPhaseCombo.Items.Count != ProjectModes.Phases.Length)
                        FillChoiceCombo(selectedPhaseCombo, ProjectModes.Phases, "A相");
                }
                finally { suppressChanges = previous; }
            }

            SetDynamicCaption(startNameCaptionLabel, capacitor ? L("设备名称") : L("始端名称"));
            SetDynamicCaption(endNameCaptionLabel, capacitor ? L("电容器名称") : L("终端名称"));
            SetDynamicCaption(routeCaptionLabel, capacitor ? L("测试连接") : L("线路区间"));
            SetDynamicCaption(loadTypeCaptionLabel, L("负载类型"));

            string lengthAText = independentPhases
                ? L("A相长度 / m")
                : singlePhase
                    ? L("A相长度 / m")
                    : L("所有相长度 / m");
            SetDynamicCaption(lengthACaptionLabel, lengthAText);
            SetDynamicCaption(lengthBCaptionLabel, L("B相长度 / m"));
            SetDynamicCaption(lengthCCaptionLabel, L("C相长度 / m"));
            lengthACaptionLabel.ForeColor = independentPhases || singlePhase ? ProjectCreationTheme.PhaseA : ProjectCreationTheme.SecondaryInk;
            lengthBCaptionLabel.ForeColor = ProjectCreationTheme.PhaseB;
            lengthCCaptionLabel.ForeColor = ProjectCreationTheme.PhaseC;

            lengthAField.Visible = !capacitor;
            lengthBField.Visible = independentPhases;
            lengthCField.Visible = independentPhases;
            lengthANumber.Visible = !capacitor;
            lengthBNumber.Visible = independentPhases;
            lengthCNumber.Visible = independentPhases;
            electricalFields.SetColumnSpan(lengthAField, independentPhases ? 1 : 3);

            if (lengthARequiredMark != null) lengthARequiredMark.Visible = !capacitor;
            if (lengthBRequiredMark != null) lengthBRequiredMark.Visible = independentPhases;
            if (lengthCRequiredMark != null) lengthCRequiredMark.Visible = independentPhases;
            lengthANumber.AccessibleName = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,
                independentPhases ? "A相长度" : "电缆长度", independentPhases ? "L1 length" : "Cable length");
            lengthBNumber.AccessibleName = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, "B相长度", "L2 length");
            lengthCNumber.AccessibleName = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, "C相长度", "L3 length");

            capacitorIdRefreshButton.Visible = capacitor;
            capacitorIdRefreshButton.Enabled = capacitor && canEditGeometry;
            capacitorRefreshColumn.Width = capacitor ? 36 : 0;

            SetDynamicCaption(endpointStartTypeCaptionLabel, capacitor ? L("设备类型") : L("始端类型"));
            SetDynamicCaption(endpointEndTypeCaptionLabel, capacitor ? L("电容类型") : L("终端类型"));
            SetDynamicCaption(endpointStartIdCaptionLabel, capacitor ? L("设备编号") : L("始端调度号"));
            SetDynamicCaption(endpointEndIdCaptionLabel, capacitor ? L("电容编号") : L("终端调度号"));
            SetDynamicCaption(endpointLineCaptionLabel, capacitor ? L("测试回路") : L("线路名称"));
            SetDynamicCaption(endpointModelCaptionLabel, capacitor ? L("电容型号") : L("电缆型号"));
        }

        static void SetDynamicCaption(Label label, string text)
        {
            if (label == null) return;
            label.Tag = "dynamic";
            label.Text = text ?? string.Empty;
        }

        void ApplyCapacitorIdentity(bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(capacitorId)) capacitorId = CreateCapacitorId();
            string endName = "电容侧-" + capacitorId;
            if (overwrite || string.IsNullOrWhiteSpace(StartName) || IsAutomaticCapacitorStart(StartName)) StartName = "设备侧";
            settingCapacitorIdentity = true;
            try { EndName = endName; DispatchNumberEnd = capacitorId; }
            finally { settingCapacitorIdentity = false; }
        }

        void RefreshCapacitorIdCache()
        {
            if (settingCapacitorIdentity || suppressChanges || CableMode != ProjectModes.Capacitor) return;
            string resolved = NormalizeCapacitorId(DispatchNumberEnd);
            if (string.IsNullOrWhiteSpace(resolved)) resolved = NormalizeCapacitorId(EndName);
            if (!string.IsNullOrWhiteSpace(resolved)) capacitorId = resolved;
        }

        string ResolveCapacitorId()
        {
            string resolved = NormalizeCapacitorId(DispatchNumberEnd);
            if (string.IsNullOrWhiteSpace(resolved)) resolved = NormalizeCapacitorId(EndName);
            if (!string.IsNullOrWhiteSpace(resolved)) capacitorId = resolved;
            if (string.IsNullOrWhiteSpace(capacitorId)) capacitorId = CreateCapacitorId();
            return capacitorId;
        }

        static string NormalizeCapacitorId(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant();
            int separator = text.LastIndexOf('-');
            if (separator >= 0 && separator < text.Length - 1) text = text.Substring(separator + 1);
            if (text.Length != 4 || text[0] != 'C') return string.Empty;
            for (int index = 1; index < text.Length; index++)
            {
                char valueCharacter = text[index];
                if (!((valueCharacter >= '0' && valueCharacter <= '9') ||
                      (valueCharacter >= 'A' && valueCharacter <= 'F'))) return string.Empty;
            }
            return text;
        }

        static string CreateCapacitorId()
        {
            uint value = unchecked((uint)Guid.NewGuid().GetHashCode()) & 0xFFFU;
            return "C" + value.ToString("X3", CultureInfo.InvariantCulture);
        }

        void CaptureCableStateBeforeCapacitor()
        {
            cableStartNameBeforeCapacitor = StartName;
            cableEndNameBeforeCapacitor = EndName;
            cableDispatchEndBeforeCapacitor = DispatchNumberEnd;
            cableLengthABeforeCapacitor = lengthANumber.Value;
            cableLengthBBeforeCapacitor = lengthBNumber.Value;
            cableLengthCBeforeCapacitor = lengthCNumber.Value;
            cableJointsBeforeCapacitor.Clear();
            cableJointsBeforeCapacitor.AddRange(joints.Select(item => item.Clone()));
            hasCableStateBeforeCapacitor = true;
        }

        void RestoreCableStateAfterCapacitor()
        {
            if (hasCableStateBeforeCapacitor)
            {
                StartName = cableStartNameBeforeCapacitor;
                EndName = cableEndNameBeforeCapacitor;
                DispatchNumberEnd = cableDispatchEndBeforeCapacitor;
                lengthANumber.Value = cableLengthABeforeCapacitor;
                lengthBNumber.Value = cableLengthBBeforeCapacitor;
                lengthCNumber.Value = cableLengthCBeforeCapacitor;
                joints.Clear();
                joints.AddRange(cableJointsBeforeCapacitor.Select(item => item.Clone()));
            }
            else
            {
                if (IsAutomaticCapacitorStart(StartName)) StartName = string.Empty;
                if (IsAutomaticCapacitorEnd(EndName)) EndName = string.Empty;
                if (!string.IsNullOrWhiteSpace(NormalizeCapacitorId(DispatchNumberEnd))) DispatchNumberEnd = string.Empty;
            }
            ClearCableStateBeforeCapacitor();
        }

        void ResetCapacitorLifecycleState()
        {
            capacitorId = string.Empty;
            lastAppliedCableMode = string.Empty;
            ClearCableStateBeforeCapacitor();
        }

        void ClearCableStateBeforeCapacitor()
        {
            cableStartNameBeforeCapacitor = string.Empty;
            cableEndNameBeforeCapacitor = string.Empty;
            cableDispatchEndBeforeCapacitor = string.Empty;
            cableLengthABeforeCapacitor = 0;
            cableLengthBBeforeCapacitor = 0;
            cableLengthCBeforeCapacitor = 0;
            cableJointsBeforeCapacitor.Clear();
            hasCableStateBeforeCapacitor = false;
        }

        static bool IsAutomaticCapacitorStart(string value)
        {
            return string.Equals((value ?? string.Empty).Trim(), "设备侧", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAutomaticCapacitorEnd(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.StartsWith("电容侧-C", StringComparison.OrdinalIgnoreCase);
        }

        void ApplyGeometryProtection()
        {
            cableModeCombo.Enabled = canEditGeometry;
            ApplyModeState(false);
        }

        void LoadPressurePreset()
        {
            currentPressureSteps = catalog.GetPressureSteps(PressureMode);
            voltagePlanName = string.Empty;
            RefreshPressurePlanPreview();
        }

        void EditPressureList()
        {
            if (PressureMode == ProjectPressureModes.Arbitrary) return;
            string mode = PressureMode;
            using (PressureEditorDialog dialog = new PressureEditorDialog(mode, currentPressureSteps, ReferenceCatalog.CreateDefaults().GetPressureSteps(mode), languageIndex))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                currentPressureSteps = dialog.Steps.Select(item => item.Clone()).ToList();
                catalog.PressurePresets[mode] = currentPressureSteps.Select(item => item.Clone()).ToList();
                SaveCatalogQuietly(); MarkDirty();
                RefreshPressurePlanPreview();
            }
        }

        void AddJointFromUi()
        {
            if (!AddJointWithMaterials(SelectedPhaseCanonical(), GetChoiceValue(attachmentTypeCombo),
                jointBeforeMaterialChanged ? GetChoiceValue(beforeBodyTypeCombo) : "", jointAfterMaterialChanged ? GetChoiceValue(bodyTypeCombo) : "",
                Decimal.ToDouble(jointPositionNumber.Value)) && ShowDialogMessages)
                MessageBox.Show(this, CustomerJointFailure(LastError), L("接头数据无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        void RefreshJointMaterialFromPosition()
        {
            if (suppressChanges || bodyTypeCombo == null || CableMode == ProjectModes.Capacitor) return;
            // Position edits are not an instruction to change the selected
            // measured joint's following material. Addition has its own dialog
            // and resolves insertion materials from each real cable block.
            if (cableGrid != null && cableGrid.SelectedRows.Count > 0 && cableGrid.SelectedRows[0].Tag is JointData) return;
            bool previous = suppressChanges; suppressChanges = true;
            try
            {
                string material = MaterialAt(SelectedPhaseCanonical(), Decimal.ToDouble(jointPositionNumber.Value));
                if (!jointBeforeMaterialChanged) SelectChoice(beforeBodyTypeCombo, material);
                if (!jointAfterMaterialChanged) SelectChoice(bodyTypeCombo, material);
            }
            finally { suppressChanges = previous; }
        }

        void EditSelectedJoint()
        {
            if (cableGrid.SelectedRows.Count == 0) return;
            JointData selected = cableGrid.SelectedRows[0].Tag as JointData;
            if (selected == null) return;
            if (!canEditGeometry) { ChangeMeasuredJoint(selected, false, jointEditorPositionChanged ? Decimal.ToDouble(jointPositionNumber.Value) : selected.Position,
                GetChoiceValue(attachmentTypeCombo), jointAfterMaterialChanged ? GetChoiceValue(bodyTypeCombo) : "", jointBeforeMaterialChanged ? GetChoiceValue(beforeBodyTypeCombo) : ""); return; }
            try { EditDraftJoint(selected, jointEditorPositionChanged ? Decimal.ToDouble(jointPositionNumber.Value) : selected.Position,
                GetChoiceValue(attachmentTypeCombo), jointBeforeMaterialChanged ? GetChoiceValue(beforeBodyTypeCombo) : "", jointAfterMaterialChanged ? GetChoiceValue(bodyTypeCombo) : ""); }
            catch (Exception error) { CompleteOperation("EditJoint", false, error.Message);string notice=CustomerJointFailure(error.ToString());SetStatus(notice, false); if (ShowDialogMessages) MessageBox.Show(this, notice, L("接头数据无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void DeleteSelectedJoint()
        {
            if (cableGrid.SelectedRows.Count == 0) return;
            JointData selected = cableGrid.SelectedRows[0].Tag as JointData;
            if (selected == null) return;
            if (!canEditGeometry) { ChangeMeasuredJoint(selected, true, 0, "", ""); return; }
            try { RemoveDraftJoint(selected); }
            catch (Exception error) { CompleteOperation("RemoveJoint", false, error.Message);string notice=CustomerJointFailure(error.ToString());SetStatus(notice, false); if (ShowDialogMessages) MessageBox.Show(this, notice, L("接头数据无效"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        bool jointEditorPositionChanged;
        void PopulateJointEditorFromSelection()
        {
            jointEditorPositionChanged = false;
            jointBeforeMaterialChanged = jointAfterMaterialChanged = false;
            if (topology != null) { topology.SelectedJointPosition = double.NaN; topology.Invalidate(); }
            if (cableGrid.SelectedRows.Count == 0) return;
            JointData selected = cableGrid.SelectedRows[0].Tag as JointData;
            if (selected == null) return;
            if (topology != null) { topology.SelectedPhase = selected.Phase; topology.SelectedJointPosition = selected.Position; }
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                if (CanAppendMeasuredJoint && selected.SourceCableLength > 0)
                    jointPositionNumber.Maximum = (decimal)selected.SourceCableLength;
                SelectChoice(attachmentTypeCombo, selected.AttachmentType);
                SelectChoice(bodyTypeCombo, selected.Material);
                SelectChoice(beforeBodyTypeCombo, JointMaterialBefore(selected));
                if (selected.Position >= 0 && selected.Position <= Decimal.ToDouble(jointPositionNumber.Maximum)) SetNumber(jointPositionNumber, selected.Position);
            }
            finally { suppressChanges = previous; }
        }

        JointData[] topologyJointSnapshot = new JointData[0];
        void RefreshTopologyJointSnapshot()
        {
            bool changed = topologyJointSnapshot.Length != joints.Count;
            for (int i = 0; !changed && i < joints.Count; i++)
            {
                JointData current = joints[i], previous = topologyJointSnapshot[i];
                changed = current.Phase != previous.Phase || current.Name != previous.Name ||
                    current.Position != previous.Position || current.AttachmentType != previous.AttachmentType ||
                    current.Material != previous.Material;
            }
            if (changed) topologyJointSnapshot = joints.Select(item => item.Clone()).ToArray();
            topology.Joints = topologyJointSnapshot;
        }

        void RefreshTopologyTerminations()
        {
            if(topology==null)return;
            topology.StartType=StartTerminalType;topology.EndType=EndTerminalType;topology.Invalidate();
        }

        void RefreshCableView()
        {
            if (topology == null || cableGrid == null) return;
            if (jointPositionNumber != null)
            {
                double currentLength = PhaseLength(SelectedPhaseCanonical());
                decimal maximum = IsFinite(currentLength) ? (decimal)Math.Max(0, Math.Min(1000000, currentLength)) : 0;
                // The input follows the actual active cable rather than the
                // generic million-metre editor range. Endpoints are visible
                // limits, but the shared writer still requires 0 < joint < L.
                jointPositionNumber.Minimum = 0;
                jointPositionNumber.Maximum = maximum;
            }
            RefreshTopologyJointSnapshot();
            string phase = SelectedPhaseCanonical();
            topology.CableMode = CableMode; topology.LengthA = CableLengthA; topology.LengthB = CableLengthB; topology.LengthC = CableLengthC;
            topology.StartType=StartTerminalType;topology.EndType=EndTerminalType;
            topology.StartName = StartName; topology.EndName = EndName; topology.SelectedPhase = phase; topology.LanguageIndex = languageIndex; topology.Invalidate();
            if (maxLengthLabel != null)
                maxLengthLabel.Text = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, "最大长度  ", "Max length  ")
                    + ProjectText.LengthMetres(CableLengthMetres) + " m";
            if (CableMode == ProjectModes.Capacitor)
                lengthSummaryLabel.Text = L("当前负载：电容") + " · " + L("长度参数不适用");
            else
                lengthSummaryLabel.Text = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex,
                    "三相长度可独立设置；定位时按当前相长度计算。",
                    "Each phase length is independent; location uses the active phase.");
            cableGrid.Rows.Clear();
            if (CableMode == ProjectModes.Capacitor)
            {
                return;
            }
            FillJointGridRows(phase);
            cableGrid.ClearSelection();
        }

        void MarkDirty()
        {
            if (suppressChanges) return;
            emptyState = false;
            IsDirty = true; IsSaved = false;
            if (saveButton != null) { saveButton.Enabled = true; saveButton.Text = L("保存项目"); }
            if (savedBadge != null) { savedBadge.Text = "●  " + L("有未保存更改"); savedBadge.BackColor = ProjectCreationTheme.Surface; savedBadge.ForeColor = ProjectCreationTheme.Warning; }
            RefreshValidationIfActive();
        }

        void UpdateSavedState(bool saved, bool clean)
        {
            IsSaved = saved; IsDirty = !clean && !saved;
            if (saveButton != null) { saveButton.Enabled = !saved; saveButton.Text = L(saved ? "已保存" : "保存项目"); }
            if (savedBadge != null)
            {
                savedBadge.Text = "●  " + L(saved ? "已保存" : clean ? "未保存" : "有未保存更改");
                savedBadge.BackColor = ProjectCreationTheme.Surface;
                savedBadge.ForeColor = saved ? ProjectCreationTheme.Active : ProjectCreationTheme.Warning;
            }
        }

        void SetStatus(string text, bool success)
        {
            if (statusLabel == null) return;
            lastStatusRawText = text ?? string.Empty;
            lastStatusSucceeded = success;
            statusLabel.Text = "  " + TranslateDynamic(lastStatusRawText).Replace("\r\n", "；").Replace("\n", "；");
            statusLabel.AutoEllipsis = true;
            statusLabel.BackColor = ProjectCreationTheme.SurfaceAlt;
            statusLabel.ForeColor = ProjectCreationTheme.SecondaryInk;
        }
    }
}
