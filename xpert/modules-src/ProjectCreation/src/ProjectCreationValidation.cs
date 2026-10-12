using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        sealed class ProjectValidationIssue
        {
            internal string FieldCode;
            internal string Message;
            internal Control Control;
        }

        readonly Dictionary<Control, Color> validationOriginalColors = new Dictionary<Control, Color>();
        readonly List<string> validationErrorFields = new List<string>();
        ErrorProvider validationProvider;
        bool validationPresentationActive;
        volatile string validationSummary = string.Empty;
        static readonly string[] EmptyValidationFields = new string[0];
        volatile string[] validationErrorSnapshot = EmptyValidationFields;
        volatile string lastValidationDialogTitle = string.Empty;
        volatile string lastValidationDialogMessage = string.Empty;
        volatile string firstValidationErrorField = string.Empty;
        long saveValidationFailureSequence;

        // Also available when an offline test host disables modal messages.
        [Browsable(false)] public string LastValidationDialogTitle { get { return lastValidationDialogTitle; } }
        [Browsable(false)] public string LastValidationDialogMessage { get { return lastValidationDialogMessage; } }
        [Browsable(false)] public string FirstValidationErrorField { get { return firstValidationErrorField; } }
        [Browsable(false)] public long SaveValidationFailureSequence { get { return System.Threading.Interlocked.Read(ref saveValidationFailureSequence); } }

        public event EventHandler ValidationChanged;

        [Browsable(false)] public bool HasValidationErrors
        {
            get { return pollHasValidationErrors; }
            private set { pollHasValidationErrors = value; }
        }
        volatile bool pollHasValidationErrors;
        [Browsable(false)] public string[] ValidationErrorFields { get { return (string[])validationErrorSnapshot.Clone(); } }
        [Browsable(false)] public long ValidationChangedSequence
        {
            get { return System.Threading.Interlocked.Read(ref pollValidationChangedSequence); }
            private set { System.Threading.Interlocked.Exchange(ref pollValidationChangedSequence, value); }
        }
        long pollValidationChangedSequence;
        [Browsable(false)] public string ValidationSummary { get { return validationSummary; } }

        void InitializeValidationPresentation()
        {
            validationSummaryLabel.TextChanged += delegate { validationSummary = validationSummaryLabel.Text; };
            validationSummary = validationSummaryLabel.Text;
            validationProvider = new ErrorProvider
            {
                BlinkStyle = ErrorBlinkStyle.NeverBlink,
                ContainerControl = this
            };
            RegisterValidationControl(startNameText);
            RegisterValidationControl(endNameText);
            RegisterValidationControl(phaseVoltageCombo);
            RegisterValidationControl(lengthANumber);
            RegisterValidationControl(lengthBNumber);
            RegisterValidationControl(lengthCNumber);
            RegisterValidationControl(humidityNumber);
            RegisterValidationControl(cableGrid);
            RegisterValidationControl(pressureEditButton);
            ShowValidationIdle();
        }

        void RegisterValidationControl(Control control)
        {
            if (control == null || validationOriginalColors.ContainsKey(control)) return;
            validationOriginalColors[control] = control.BackColor;
            validationProvider.SetIconAlignment(control, ErrorIconAlignment.MiddleRight);
            validationProvider.SetIconPadding(control, 2);
        }

        List<ProjectValidationIssue> ValidateDraftDetailed(ProjectDraft draft)
        {
            List<ProjectValidationIssue> issues = new List<ProjectValidationIssue>();
            if (string.IsNullOrWhiteSpace(draft.StartName)) AddIssue(issues, "StartName", L("始端名称必须填写"), startNameText);
            if (string.IsNullOrWhiteSpace(draft.EndName)) AddIssue(issues, "EndName", L("终端名称必须填写"), endNameText);
            if (string.IsNullOrWhiteSpace(draft.PhaseVoltage)) AddIssue(issues, "PhaseVoltage", L("请选择相电压"), phaseVoltageCombo);
            else if (!IsValidPhaseVoltage(draft.PhaseVoltage)) AddIssue(issues, "PhaseVoltage", L("相电压必须是大于 0 的有效数值，单位为 kV。"), phaseVoltageCombo);
            if (!IsFinite(draft.Humidity) || draft.Humidity < 0 || draft.Humidity > 100)
                AddIssue(issues, "RelativeHumidity", L("相对湿度必须在 0% 至 100% 之间。"), humidityNumber);

            if (draft.CableMode == ProjectModes.Capacitor)
            {
                if (draft.LengthA != 0) AddIssue(issues, "CableLengthA", L("电容局放模式的三相长度必须全部为 0。"), lengthANumber);
                if (draft.LengthB != 0) AddIssue(issues, "CableLengthB", L("电容局放模式的三相长度必须全部为 0。"), lengthBNumber);
                if (draft.LengthC != 0) AddIssue(issues, "CableLengthC", L("电容局放模式的三相长度必须全部为 0。"), lengthCNumber);
                if (draft.Joints.Count > 0) AddIssue(issues, "CableStructure", L("电容局放模式不能包含电缆接头。"), cableGrid);
            }
            else if (draft.CableMode == ProjectModes.SinglePhase)
            {
                if (!IsFinite(draft.LengthA) || draft.LengthA <= 0) AddIssue(issues, "CableLengthA", L("电缆长度必须大于 0"), lengthANumber);
                if (draft.LengthB != 0) AddIssue(issues, "CableLengthB", L("单相模式的 B/C 相长度必须为 0。"), lengthBNumber);
                if (draft.LengthC != 0) AddIssue(issues, "CableLengthC", L("单相模式的 B/C 相长度必须为 0。"), lengthCNumber);
            }
            else
            {
                string message = L("电缆长度必须大于 0");
                if (!IsFinite(draft.LengthA) || draft.LengthA <= 0) AddIssue(issues, "CableLengthA", message, lengthANumber);
                if (!IsFinite(draft.LengthB) || draft.LengthB <= 0) AddIssue(issues, "CableLengthB", message, lengthBNumber);
                if (!IsFinite(draft.LengthC) || draft.LengthC <= 0) AddIssue(issues, "CableLengthC", message, lengthCNumber);
                if (draft.CableMode == ProjectModes.ThreeCore &&
                    (Math.Abs(draft.LengthA - draft.LengthB) > 0.000001 || Math.Abs(draft.LengthA - draft.LengthC) > 0.000001))
                {
                    AddIssue(issues, "CableLengthB", L("三芯模式使用统一长度；长度不一致时请选择“三相单芯”。"), lengthBNumber);
                    AddIssue(issues, "CableLengthC", L("三芯模式使用统一长度；长度不一致时请选择“三相单芯”。"), lengthCNumber);
                }
            }

            foreach (IGrouping<string, JointData> group in draft.Joints.GroupBy(item => item.Phase))
            {
                double length = draft.LengthFor(group.Key);
                double previous = -1;
                foreach (JointData joint in group.OrderBy(item => item.Position))
                {
                    if (!IsFinite(joint.Position) || joint.Position <= 0 || joint.Position >= length)
                        AddIssue(issues, "CableStructure", string.Format(CultureInfo.CurrentCulture,
                            L("{0}接头“{1}”的位置必须大于 0 且小于本相长度 {2} m。"),
                            ProjectCreationLocalization.TranslateCanonical(languageIndex, group.Key), joint.Name, ProjectText.Number(length)), cableGrid);
                    if (previous >= 0 && Math.Abs(previous - joint.Position) < 0.000001)
                        AddIssue(issues, "CableStructure", string.Format(CultureInfo.CurrentCulture,
                            L("{0}存在重复接头位置。"), ProjectCreationLocalization.TranslateCanonical(languageIndex, group.Key)), cableGrid);
                    previous = joint.Position;
                }
            }
            try { if (draft.PressureMode != ProjectPressureModes.Arbitrary) VoltagePlanFile.Validate(draft.PressureSteps); }
            catch (InvalidDataException) { AddIssue(issues, "PressurePlan", L(VoltagePlanFile.InvalidMessage), pressureEditButton); }
            return issues;
        }

        static bool IsValidPhaseVoltage(string text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.EndsWith("kV", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 2).TrimEnd();
            double voltage;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out voltage) && IsFinite(voltage) && voltage > 0;
        }

        string ValidationFieldCaption(string field)
        {
            switch (field)
            {
                case "StartName": return startNameCaptionLabel.Text.Trim(' ', '*', '：', ':');
                case "EndName": return endNameCaptionLabel.Text.Trim(' ', '*', '：', ':');
                case "PhaseVoltage": return L("相电压");
                case "RelativeHumidity": return L("湿度 / %");
                case "CableLengthA": return L("A相长度 / m");
                case "CableLengthB": return L("B相长度 / m");
                case "CableLengthC": return L("C相长度 / m");
                case "CableStructure": return L("接头与本体结构");
                case "PressurePlan": return L("加压计划");
                default: return field;
            }
        }

        void ResetSaveValidationNotice()
        {
            lastValidationDialogTitle = lastValidationDialogMessage = firstValidationErrorField = string.Empty;
        }

        bool RejectInvalidSave(List<ProjectValidationIssue> issues)
        {
            lastValidationDialogTitle = L("项目尚未保存");
            firstValidationErrorField = issues[0].FieldCode;
            string[] lines = issues.Select((issue, index) => (index + 1).ToString(CultureInfo.InvariantCulture) + ". "
                + ValidationFieldCaption(issue.FieldCode) + ": " + issue.Message).ToArray();
            lastValidationDialogMessage = L("请完成以下修改后再保存：") + "\r\n\r\n" + string.Join("\r\n", lines)
                + "\r\n\r\n" + L("已填写的内容将保留。关闭提示后，将定位到第一个需要修改的位置。");
            System.Threading.Interlocked.Increment(ref saveValidationFailureSequence);
            CompleteOperation("SaveProject", false, lastValidationDialogMessage);
            SetStatus("保存失败 · " + string.Join("；", lines), false);
            if (ShowDialogMessages)
                MessageBox.Show(this, lastValidationDialogMessage, lastValidationDialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            FocusValidationIssue(issues[0]);
            return false;
        }

        void FocusValidationIssue(ProjectValidationIssue issue)
        {
            if (issue == null || issue.Control == null) return;
            Control target = issue.Control;
            foreach (OptionalSectionState section in optionalSections.Values)
                if (section.Content == target || section.Content.Contains(target))
                { SetOptionalSectionExpanded(section.Code, true); break; }
            // Inapplicable or protected geometry may be hidden. Select the
            // available common length or mode selector rather than a hidden field.
            if (!target.CanSelect && issue.FieldCode.StartsWith("CableLength", StringComparison.Ordinal))
                target = lengthANumber.CanSelect ? (Control)lengthANumber : cableModeCombo;
            if (target.CanSelect)
            {
                target.Select(); target.Focus();
                var text = target as TextBoxBase;
                if (text != null) text.SelectAll();
                var number = target as NumericUpDown;
                if (number != null) number.Select(0, number.Text.Length);
            }
        }

        static void AddIssue(List<ProjectValidationIssue> issues, string code, string message, Control control)
        {
            issues.Add(new ProjectValidationIssue { FieldCode = code, Message = message, Control = control });
        }

        void ApplyValidationIssues(List<ProjectValidationIssue> issues, bool focusFirst)
        {
            bool previousHasErrors = HasValidationErrors;
            string previousFields = string.Join("|", validationErrorFields.ToArray());
            validationPresentationActive = true;
            validationErrorFields.Clear();
            foreach (KeyValuePair<Control, Color> pair in validationOriginalColors)
            {
                validationProvider.SetError(pair.Key, string.Empty);
                pair.Key.BackColor = pair.Value;
            }
            foreach (ProjectValidationIssue issue in issues)
            {
                if (!validationErrorFields.Contains(issue.FieldCode)) validationErrorFields.Add(issue.FieldCode);
                if (issue.Control == null) continue;
                validationProvider.SetError(issue.Control, issue.Message);
                issue.Control.BackColor = Color.FromArgb(255, 239, 240);
            }
            validationErrorSnapshot = validationErrorFields.ToArray();
            HasValidationErrors = issues.Count > 0;
            if (validationSummaryLabel != null)
            {
                validationSummaryLabel.Text = HasValidationErrors
                    ? "⚠ " + L("请填写所有必填项后再保存")
                    : "✓ " + L("带 * 的字段为必填项");
                validationSummaryLabel.ForeColor = HasValidationErrors ? ProjectCreationTheme.Danger : ProjectCreationTheme.SecondaryInk;
            }
            string fields = string.Join("|", validationErrorFields.ToArray());
            if (previousHasErrors != HasValidationErrors || !string.Equals(previousFields, fields, StringComparison.Ordinal))
            {
                System.Threading.Interlocked.Increment(ref pollValidationChangedSequence);
                RaiseSafely(ValidationChanged);
            }
            if (focusFirst && issues.Count > 0) FocusValidationIssue(issues[0]);
        }

        void ClearValidationPresentation()
        {
            bool changed = HasValidationErrors || validationErrorFields.Count > 0;
            validationPresentationActive = false;
            ResetSaveValidationNotice();
            validationErrorFields.Clear();
            validationErrorSnapshot = EmptyValidationFields;
            HasValidationErrors = false;
            if (validationProvider != null)
            {
                foreach (KeyValuePair<Control, Color> pair in validationOriginalColors)
                {
                    validationProvider.SetError(pair.Key, string.Empty);
                    pair.Key.BackColor = pair.Value;
                }
            }
            ShowValidationIdle();
            if (changed)
            {
                System.Threading.Interlocked.Increment(ref pollValidationChangedSequence);
                RaiseSafely(ValidationChanged);
            }
        }

        void ShowValidationIdle()
        {
            if (validationSummaryLabel == null) return;
            validationSummaryLabel.Text = L("带 * 的字段为必填项");
            validationSummaryLabel.ForeColor = ProjectCreationTheme.SecondaryInk;
        }

        void RefreshValidationIfActive()
        {
            if (!validationPresentationActive || suppressChanges) return;
            ApplyValidationIssues(ValidateDraftDetailed(BuildDraft(false)), false);
        }

        void RefreshValidationLanguage()
        {
            if (validationPresentationActive) ApplyValidationIssues(ValidateDraftDetailed(BuildDraft(false)), false);
            else ShowValidationIdle();
        }
    }
}
