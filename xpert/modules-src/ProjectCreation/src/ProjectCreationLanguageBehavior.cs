using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        sealed class LocalizedChoice
        {
            internal readonly string Value;
            readonly string display;

            internal LocalizedChoice(string value, string display)
            {
                Value = value ?? string.Empty;
                this.display = display ?? Value;
            }

            public override string ToString() { return display; }
        }

        string lastStatusRawText = string.Empty;
        bool lastStatusSucceeded;

        public event EventHandler LanguageChanged;

        [Browsable(false)]
        public long LanguageChangedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollLanguageChangedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollLanguageChangedSequence, value); }
        }
        long pollLanguageChangedSequence;

        public void SetLanguageIndex(int value)
        {
            if (IsDisposed || Disposing) return;
            int normalized = ProjectCreationLocalization.NormalizeLanguage(value);
            if (InvokeRequired)
            {
                Invoke(new Action<int>(SetLanguageIndex), value);
                return;
            }
            if (languagePreference == value && languageIndex == normalized && languageCombo != null &&
                languageCombo.Items.Count == ProjectCreationLanguageContract.Names.Length + 1 &&
                languageCombo.SelectedIndex == value + 1) return;
            languagePreference = value;
            languageIndex = normalized;
            ApplyLanguage();
            System.Threading.Interlocked.Increment(ref pollLanguageChangedSequence);
            RaiseSafely(LanguageChanged);
        }

        void ApplyLanguage()
        {
            if (cableModeCombo == null) return;
            string cableMode = GetChoiceValue(cableModeCombo);
            string pressureMode = GetChoiceValue(pressureModeCombo);
            string phase = GetChoiceValue(selectedPhaseCombo);
            string startType = GetChoiceValue(startTypeCombo);
            string endType = GetChoiceValue(endTypeCombo);
            string attachment = GetChoiceValue(attachmentTypeCombo);
            string material = GetChoiceValue(bodyTypeCombo);
            string beforeMaterial = GetChoiceValue(beforeBodyTypeCombo);
            string phaseVoltage = GetChoiceValue(phaseVoltageCombo);
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                EnsureLanguageChoices();
                MarkDynamicLanguageControls();
                CaptureLanguageKeys(this);
                ApplyControlLanguage(this);
                FillLoadTypeCombo(ProjectModes.Normalize(cableMode));
                FillChoiceCombo(pressureModeCombo, ProjectPressureModes.All, ProjectPressureModes.Normalize(pressureMode));
                FillChoiceCombo(selectedPhaseCombo, ProjectModes.Phases, ProjectModes.NormalizePhaseOrDefault(phase));
                FillChoiceCombo(startTypeCombo, catalog.TerminalTypes, startType);
                FillChoiceCombo(endTypeCombo, catalog.TerminalTypes, endType);
                FillChoiceCombo(attachmentTypeCombo, catalog.AttachmentTypes, attachment);
                FillMaterialChoice(bodyTypeCombo, material);
                FillMaterialChoice(beforeBodyTypeCombo, beforeMaterial);
                FillChoiceCombo(phaseVoltageCombo, catalog.PhaseVoltages, phaseVoltage);
                if (languageCombo != null) languageCombo.SelectedIndex = languagePreference + 1;
                if (cableGrid != null && cableGrid.Columns.Count >= 5)
                {
                    cableGrid.Columns[0].HeaderText = L("相别");
                    cableGrid.Columns[1].HeaderText = L("节点");
                    cableGrid.Columns[2].HeaderText = L("附件类型");
                    cableGrid.Columns[3].HeaderText = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, "位置 / m", "m");
                    cableGrid.Columns[3].ToolTipText = L("位置 / m");
                    cableGrid.Columns[4].HeaderText = L("接头后绝缘");
                    cableGrid.Columns["MaterialBefore"].HeaderText = L("接头前绝缘");
                    ConfigureJointGridColumns(cableGrid);
                }
                if (topology != null) topology.LanguageIndex = languageIndex;
                if (fixStatusValue != null) fixStatusValue.Text = ProjectCreationLocalization.TranslateCanonical(languageIndex, beidouFixStatusRaw);
                RefreshHardwareSourceLabel();
                if (WeatherCode >= 0 && weatherText != null) weatherText.Text = ProjectCreationLocalization.Weather(languageIndex, WeatherCode);
                if (hardwareToggleButton != null)
                {
                    hardwareToggleButton.Text = L(environmentDetailsExpanded ? "收起" : "展开");
                    hardwareToggleButton.AccessibleName = L(environmentDetailsExpanded ? "收起环境与北斗" : "展开环境与北斗");
                }
                RefreshWeatherButtonUi();
                RefreshSavedStateLanguage();
                RefreshOptionalSectionLanguage();
                RefreshValidationLanguage();
                RefreshLoadModePresentation();
                RefreshDeviceStatePresentation();
                if (statusLabel != null && lastStatusRawText.Length > 0)
                {
                    statusLabel.Text = "  " + TranslateDynamic(lastStatusRawText);
                    // Keep the footer quiet and neutral. Success is communicated
                    // by text and the compact device-state indicator, not a full-width
                    // green (or dark) status strip.
                    statusLabel.BackColor = ProjectCreationTheme.SurfaceAlt;
                    statusLabel.ForeColor = ProjectCreationTheme.SecondaryInk;
                }
            }
            finally { suppressChanges = previous; }
            AttachReferenceMenus();
            RefreshCableView();
            RefreshPressurePlanPreview();
            LayoutProjectActions();
            RefreshProjectPageTitle();
        }

        void RefreshDeviceStatePresentation()
        {
            if (deviceStateLabel != null)
            {
                deviceStateLabel.Ready = deviceReady;
                deviceStateLabel.Text = "●  " + L(deviceReady ? "设备就绪" : "设备离线");
            }
            if (acquireHardwareButton != null)
            {
                // Environmental input is independent of the HV / Pico connection.
                bool enabled = hardwareAcquisitionAvailable && !EnvironmentAcquisitionBusy;
                acquireHardwareButton.Enabled = enabled;
                acquireHardwareButton.Cursor = enabled ? Cursors.Hand : Cursors.Default;
            }
        }

        void EnsureLanguageChoices()
        {
            if (languageCombo == null) return;
            string follow = Xiongxin.OWTS.Localization.UiLanguage.Text(languageIndex, "跟随系统", "Follow system");
            bool matches = languageCombo.Items.Count == ProjectCreationLanguageContract.Names.Length + 1 && Convert.ToString(languageCombo.Items[0]) == follow;
            if (matches)
                for (int index = 0; index < ProjectCreationLanguageContract.Names.Length; index++)
                    if (!String.Equals(Convert.ToString(languageCombo.Items[index + 1]), ProjectCreationLanguageContract.Names[index], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
            if (matches) return;
            languageCombo.BeginUpdate();
            try
            {
                languageCombo.Items.Clear();
                languageCombo.Items.Add(follow);
                languageCombo.Items.AddRange(ProjectCreationLanguageContract.Names.Cast<object>().ToArray());
            }
            finally { languageCombo.EndUpdate(); }
        }

        string L(string key) { return ProjectCreationLocalization.T(languageIndex, key); }

        string TranslateDynamic(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string[] prefixes = { "已保存 · ", "已加载 · ", "保存失败 · ", "加载失败 · ", "硬件数据已更新 · 北斗 ", "硬件数据无效 · ", "天气数据已更新 · ", "天气获取失败 · " };
            foreach (string prefix in prefixes)
                if (text.StartsWith(prefix, StringComparison.Ordinal)) return L(prefix) + text.Substring(prefix.Length);
            return L(text);
        }

        static void CaptureLanguageKeys(Control root)
        {
            foreach (Control control in root.Controls)
            {
                if ((control is Label || control is Button || control is CheckBox) && control.Tag == null && !string.IsNullOrWhiteSpace(control.Text))
                    control.Tag = "i18n:" + control.Text;
                if (control.HasChildren) CaptureLanguageKeys(control);
            }
        }

        void MarkDynamicLanguageControls()
        {
            Control[] dynamicControls = { latitudeValue, longitudeValue, altitudeValue, fixStatusValue, sampleTimeValue,
                hardwareSourceLabel, lengthSummaryLabel, statusLabel, savedBadge, saveButton, weatherRefreshButton,
                hardwareToggleButton, languageCombo };
            foreach (Control control in dynamicControls) if (control != null) control.Tag = "dynamic";
        }

        void ApplyControlLanguage(Control root)
        {
            foreach (Control control in root.Controls)
            {
                string tag = control.Tag as string;
                if (tag != null && tag.StartsWith("i18n:", StringComparison.Ordinal))
                    control.Text = L(tag.Substring(5));
                if (control.HasChildren) ApplyControlLanguage(control);
            }
        }

        void RefreshSavedStateLanguage()
        {
            if (saveButton != null) saveButton.Text = L(IsSaved ? "已保存" : "保存项目");
            if (savedBadge != null) savedBadge.Text = "●  " + L(IsSaved ? "已保存" : IsDirty ? "有未保存更改" : "未保存");
        }

        void RefreshHardwareSourceLabel()
        {
            if (hardwareSourceLabel != null)
                RefreshEnvironmentPresentation();
        }

        string GetChoiceValue(ComboBox combo)
        {
            if (combo == null) return string.Empty;
            LocalizedChoice choice = combo.SelectedItem as LocalizedChoice;
            return choice == null ? combo.Text : choice.Value;
        }

        void SelectChoice(ComboBox combo, string value)
        {
            if (combo == null) return;
            string requested = value ?? string.Empty;
            for (int index = 0; index < combo.Items.Count; index++)
            {
                LocalizedChoice choice = combo.Items[index] as LocalizedChoice;
                string candidate = choice == null ? Convert.ToString(combo.Items[index]) : choice.Value;
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = index;
                    return;
                }
            }
            if (requested.Length == 0) { combo.SelectedIndex = -1; return; }
            combo.Items.Add(new LocalizedChoice(requested, ProjectCreationLocalization.TranslateCanonical(languageIndex, requested)));
            combo.SelectedIndex = combo.Items.Count - 1;
        }

        void FillChoiceCombo(ComboBox combo, IEnumerable<string> values, string selected)
        {
            if (combo == null) return;
            string keep = selected ?? GetChoiceValue(combo);
            combo.BeginUpdate();
            try
            {
                combo.Items.Clear();
                foreach (string value in values.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
                    combo.Items.Add(new LocalizedChoice(value, ProjectCreationLocalization.TranslateCanonical(languageIndex, value)));
                SelectChoice(combo, keep);
            }
            finally { combo.EndUpdate(); }
        }

        void FillLoadTypeCombo(string selected)
        {
            if (cableModeCombo == null) return;
            string keep = ProjectModes.Normalize(selected);
            cableModeCombo.BeginUpdate();
            try
            {
                cableModeCombo.Items.Clear();
                foreach (string value in ProjectModes.All)
                {
                    string display = ProjectCreationLocalization.TranslateCanonical(languageIndex, ProjectModes.DisplayName(value));
                    cableModeCombo.Items.Add(new LocalizedChoice(value, display));
                }
                SelectChoice(cableModeCombo, keep);
            }
            finally { cableModeCombo.EndUpdate(); }
        }

        string SelectedPhaseCanonical()
        {
            return ProjectModes.NormalizePhaseOrDefault(GetChoiceValue(selectedPhaseCombo));
        }
    }
}
