using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public partial class ProjectCreationControl
    {
        DataGridView pressurePreviewGrid;
        Label pressurePlanSummary;
        ProjectCreationButton pressureSaveAsButton;
        ProjectCreationButton pressureLoadButton;
        FlowLayoutPanel pressurePlanActions;
        TableLayoutPanel pressurePlanLayout;
        FlowLayoutPanel projectActionButtons;
        string voltagePlanName = string.Empty;
        bool layingOutActions;

        void RefreshPressurePlanPreview()
        {
            if (pressurePreviewGrid == null) return;
            bool freeValue = PressureMode == ProjectPressureModes.Arbitrary;
            pressurePreviewGrid.Visible = !freeValue;
            pressureEditButton.Visible = pressureSaveAsButton.Visible = !freeValue;
            // Loading a saved plan remains available and explicitly selects Custom.
            pressurePreviewGrid.SuspendLayout();
            try
            {
                int selected = pressurePreviewGrid.CurrentCell == null ? 0 : pressurePreviewGrid.CurrentCell.RowIndex;
                pressurePreviewGrid.Rows.Clear();
                pressurePreviewGrid.Columns[0].HeaderText = L("序号");
                pressurePreviewGrid.Columns[1].HeaderText = L("加压倍数") + " × U₀";
                pressurePreviewGrid.Columns[2].HeaderText = L("次数");
                pressurePreviewGrid.Columns[3].HeaderText = L("计算电压 / kV");
                double voltage;
                string voltageText = (PhaseVoltage ?? string.Empty).Replace("kV", string.Empty).Replace("KV", string.Empty).Trim();
                bool hasVoltage = double.TryParse(voltageText, NumberStyles.Float, CultureInfo.InvariantCulture, out voltage) && IsFinite(voltage) && voltage > 0;
                for (int i = 0; !freeValue && i < currentPressureSteps.Count; i++)
                {
                    PressureStep step = currentPressureSteps[i];
                    pressurePreviewGrid.Rows.Add(i + 1, ProjectText.Number(step.Multiplier), step.Repetitions,
                        hasVoltage ? (step.Multiplier * voltage).ToString("0.0##", CultureInfo.InvariantCulture) : "—");
                }
                if (pressurePreviewGrid.Rows.Count > 0)
                    pressurePreviewGrid.CurrentCell = pressurePreviewGrid.Rows[Math.Min(selected, pressurePreviewGrid.Rows.Count - 1)].Cells[0];
                pressurePreviewGrid.ColumnHeadersDefaultCellStyle.Font = pressurePreviewGrid.Font;
                pressurePreviewGrid.RowTemplate.Height = Math.Max(24, pressurePreviewGrid.Font.Height + 10);
                foreach (DataGridViewRow row in pressurePreviewGrid.Rows) row.Height = pressurePreviewGrid.RowTemplate.Height;
                pressurePlanSummary.Text = freeValue ? L("任意值：在加压测试中直接设置电压。") : (string.IsNullOrEmpty(voltagePlanName) ? ProjectCreationLocalization.TranslateCanonical(languageIndex, PressureMode) : voltagePlanName)
                    + " · " + currentPressureSteps.Count.ToString(CultureInfo.InvariantCulture) + " " + L("个步骤")
                    + " · " + L("电压 = 相电压 × 倍数");
                if (pressurePlanActions != null) SizeActionButtons(pressurePlanActions);
            }
            finally { pressurePreviewGrid.ResumeLayout(); }
        }

        void SaveVoltagePlanAs()
        {
            if (PressureMode == ProjectPressureModes.Arbitrary) return;
            try
            {
                VoltagePlanFile.Validate(currentPressureSteps);
                Directory.CreateDirectory(VoltagePlanFile.DefaultDirectory);
                using (SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = L("另存加压计划"), Filter = L("加压计划 (*.hvplan)") + "|*.hvplan", DefaultExt = "hvplan",
                    AddExtension = true, OverwritePrompt = true, InitialDirectory = VoltagePlanFile.DefaultDirectory,
                    FileName = VoltagePlanFile.SuggestedFileName(string.IsNullOrEmpty(voltagePlanName) ? ProjectCreationLocalization.TranslateCanonical(languageIndex, PressureMode) : voltagePlanName),
                    RestoreDirectory = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    string name = Path.GetFileNameWithoutExtension(dialog.FileName).Trim();
                    VoltagePlanFile.Save(dialog.FileName, name, currentPressureSteps);
                    voltagePlanName = name;
                    RefreshPressurePlanPreview();
                    SetStatus(L("加压计划已另存") + " · " + name, true);
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            { ShowVoltagePlanError(error); }
        }

        void LoadVoltagePlanFromFile()
        {
            try
            {
                Directory.CreateDirectory(VoltagePlanFile.DefaultDirectory);
                using (OpenFileDialog dialog = new OpenFileDialog
                {
                    Title = L("加载加压计划"), Filter = L("加压计划 (*.hvplan)") + "|*.hvplan", DefaultExt = "hvplan",
                    InitialDirectory = VoltagePlanFile.DefaultDirectory, CheckFileExists = true, Multiselect = false, RestoreDirectory = true
                })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    ApplyVoltagePlanFile(dialog.FileName);
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            { ShowVoltagePlanError(error); }
        }

        void ApplyVoltagePlanFile(string path)
        {
            VoltagePlan plan = VoltagePlanFile.Load(path);
            // Complete validation precedes every UI/model change. Loading
            // a plan does not alter the device limits or preset catalog.
            bool previous = suppressChanges;
            suppressChanges = true;
            try
            {
                SelectChoice(pressureModeCombo, "自定义");
                currentPressureSteps = plan.Steps.Select(s => s.Clone()).ToList();
                voltagePlanName = plan.Name;
            }
            finally { suppressChanges = previous; }
            RefreshPressurePlanPreview();
            MarkDirty();
            SetStatus(L("加压计划已加载，请核对计算电压") + " · " + plan.Name, true);
        }

        void ShowVoltagePlanError(Exception error)
        {
            string message = CustomerProjectMessage(error.ToString(),"无法读取或保存加压计划。请核对计划内容及文件夹权限后重试。","The voltage plan could not be read or saved. Check the plan and folder access, then retry.");
            SetStatus(message, false);
            if (ShowDialogMessages) MessageBox.Show(this, message, L("加压计划"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        static void SizeActionButtons(FlowLayoutPanel panel)
        {
            if (panel == null) return;
            foreach (Control child in panel.Controls)
            {
                Button button = child as Button;
                if (button == null) continue;
                button.Width = Math.Max(76, TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 30);
                button.Height = Math.Max(30, button.Font.Height + 12);
            }
        }

        void LayoutProjectActions()
        {
            if (layingOutActions || projectActionButtons == null || actionBar == null) return;
            layingOutActions = true;
            try
            {
                SizeActionButtons(projectActionButtons);
                int width = 0, height = 30;
                foreach (Control child in projectActionButtons.Controls)
                { width += child.Width + child.Margin.Horizontal; height = Math.Max(height, child.Height + child.Margin.Vertical); }
                projectActionButtons.Width = width;
                projectActionButtons.Height = height;
                if (savedBadge != null)
                {
                    savedBadge.Width = Math.Max(0, actionBar.ClientSize.Width - actionBar.Padding.Horizontal - width - 12);
                    savedBadge.Height = height;
                    savedBadge.AutoEllipsis = true;
                }
                if (actionRowStyle != null) actionRowStyle.Height = height + actionBar.Padding.Vertical + 2;
                if (pressurePlanActions != null)
                {
                    SizeActionButtons(pressurePlanActions);
                    if (pressurePlanLayout != null) pressurePlanLayout.RowStyles[3].Height = Math.Max(38, pressureEditButton.Height + 8);
                }
            }
            finally { layingOutActions = false; }
        }
    }
}
