using System;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        private TabPage plusDataTab;
        private bool plusDataUnlocked;
        private CheckBox enableDefectRecognition;
        private Label locationMethodNote;
        private ComboBox defectModelKind;
        private TextBox competitionModelPath;

        private Control BuildPlusDataPanel()
        {
            var pages = new TabControl { Name = "PlusDataTabs", Dock = DockStyle.Fill, Font = AppTheme.Font(9.0f, FontStyle.Regular) };
            var features = CreateGrid();
            enableAiFeatures = new CheckBox { Name = "EnableAiFeatures", AutoSize = true,
                Text = "启用 AI 学习与分析建议" };
            enableDefectRecognition = new CheckBox { Name = "EnableDefectRecognition", AutoSize = true,
                Text = "启用缺陷识别（需同时启用 AI）" };
            enableCompetitionDefectModule = new CheckBox { Name = "ManufacturerCompetitionDefectModule", AutoSize = true,
                Text = "显示独立缺陷识别模块入口" };
            AddWideRow(features, enableAiFeatures);
            AddWideRow(features, enableDefectRecognition);
            AddWideRow(features, enableCompetitionDefectModule);
            AddWideRow(features, CreateNote("请先选择项目，再打开独立缺陷识别。隐藏入口不会关闭已打开的窗口；独立窗口中的设置单独保存。"));
            AddWideRow(features, CreateNote("保存设置不会连接设备或开始试验。关闭缺陷识别后仍可进行常规数据分析。"));
            pages.TabPages.Add(PlusDataTab("功能开关", features));

            var parameters = CreateGrid();
            pdSampleRate = CreateNumeric(1M, 1000000000000M, 0, 1000000M);
            pdSampleRate.Name = "PlusDataPdSampleRate";
            pdSampleRate.ThousandsSeparator = true;
            AddRow(parameters, "局放采样率 / Hz", pdSampleRate);
            pdSampleBits = CreateNumeric(1M, 64M, 0, 1M);
            pdSampleBits.Name = "PlusDataPdSampleBits";
            AddRow(parameters, "采样位数 / bit", pdSampleBits);
            AddWideRow(parameters, CreateNote("采样参数须符合设备能力，请勿超出设备允许范围。"));
            minus3MaximumPulses = CreateNumeric(1M, 100M, 0, 1M);
            minus3MaximumPulses.Name = "PlusDataMaximumPulses";
            AddRow(parameters, "最大脉冲数", minus3MaximumPulses);
            calibrationAverageCount = CreateNumeric(3M, 256M, 0, 1M);
            calibrationAverageCount.Name = "PlusDataCalibrationAverageCount";
            AddRow(parameters, "校准平均次数 N", calibrationAverageCount);
            noiseSimilarityTolerance = CreateNumeric(0M, 10M, 2, .1M);
            noiseSimilarityTolerance.Name = "ManufacturerNoiseSimilarityTolerance";
            AddRow(parameters, "干扰模板相关容差 / %", noiseSimilarityTolerance);
            AddWideRow(parameters, CreateNote("默认 1%，相似度 ≥99% 的脉冲排除在校准/定位提取之外，原始数据保留。0% 表示仅排除完全相关的脉冲，并非关闭过滤。"));
            minus3ReflectionSuppression = CreateNumeric(-1M, 100M, 0, 1M);
            minus3ReflectionSuppression.Name = "PlusDataReflectionSuppression";
            AddRow(parameters, "多重反射抑制次数（-1 自动）", minus3ReflectionSuppression);
            minus3AlgorithmIndex = new SettingsComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
            minus3AlgorithmIndex.Name = "PlusDataAlgorithmIndex";
            minus3AlgorithmIndex.Items.AddRange(new object[] { "A", "B", "E", "E+" });
            AddRow(parameters, "定位方法", minus3AlgorithmIndex);
            locationMethodNote=CreateNote("更改定位方法后，请重新分析数据。原有分析结果保持不变。");
            AddWideRow(parameters, locationMethodNote);
            minus3AlgorithmIndex.SelectedIndexChanged += delegate { RefreshLocationMethodNote(); };
            pages.TabPages.Add(PlusDataTab("采样与分析参数", parameters));

            var endpoints = CreateGrid();
            AddEndpointPolicyRows(endpoints);
            pages.TabPages.Add(PlusDataTab("首末端组合规则", endpoints));

            var models = CreateGrid();
            defectModelKind = new SettingsComboBox { Name="DefectModelKind", DropDownStyle=ComboBoxStyle.DropDownList, Width=260 };
            defectModelKind.Items.AddRange(new object[] { SettingsText("标准模型","Standard model"), SettingsText("竞赛模型","Competition model") });
            AddRow(models, "缺陷识别模型", defectModelKind);
            minus3ModelPath = CreateTextBox();
            minus3ModelPath.Name = "PlusDataAnalysisModelPath";
            AddRow(models, "标准模型文件（可选）", CreatePathEditor(minus3ModelPath, false, DefectModelCatalog.FileFilter));
            competitionModelPath = CreateTextBox(); competitionModelPath.Name="CompetitionModelPath";
            AddRow(models, "竞赛模型文件（可选）", CreatePathEditor(competitionModelPath, false, DefectModelCatalog.FileFilter));
            AddWideRow(models, CreateNote("文件路径留空时使用已安装的对应模型。模型选择同时用于数据分析和加压后的缺陷识别；独立缺陷识别窗口仍单独保存设置。"));
            unifiedModel = CreateTextBox();
            AddRow(models, "最终统一分析模型路径", CreatePathEditor(unifiedModel, false, ModelFilter()));
            AddWideRow(models, CreateNote("扩展模型仅在对应功能支持时使用。"));
            task35Model = CreateTextBox();
            AddRow(models, "扩展模型 A 路径", CreatePathEditor(task35Model, false, ModelFilter()));
            task36Model = CreateTextBox();
            AddRow(models, "扩展模型 B 路径", CreatePathEditor(task36Model, false, ModelFilter()));
            task37Model = CreateTextBox();
            AddRow(models, "扩展模型 C 路径", CreatePathEditor(task37Model, false, ModelFilter()));
            task38Model = CreateTextBox();
            AddRow(models, "扩展模型 D 路径", CreatePathEditor(task38Model, false, ModelFilter()));
            pages.TabPages.Add(PlusDataTab("AI 模型加载", models));
            return pages;
        }

        private static TabPage PlusDataTab(string title, TableLayoutPanel grid)
        {
            // Long explanations and checkbox labels wrap within the scrolling page;
            // the outer Save/Cancel footer always remains outside this viewport.
            grid.SizeChanged += delegate
            {
                int available = Math.Max(100, grid.ClientSize.Width - grid.Padding.Horizontal - 16);
                foreach (Control control in grid.Controls)
                    if (grid.GetColumnSpan(control) == 2 && (control is Label || control is CheckBox))
                        control.MaximumSize = new Size(available, 0);
            };
            return CreateTab(title, grid);
        }

        private void LoadPlusDataValues(AppSettings value)
        {
            enableAiFeatures.Checked = value.EnableAiFeatures;
            enableDefectRecognition.Checked = value.EnableDefectRecognition;
            enableCompetitionDefectModule.Checked = value.EnableCompetitionDefectModule;
            SetNumeric(pdSampleRate, value.PdSampleRateHz);
            SetNumeric(pdSampleBits, value.PdSampleBits);
            TaskMinus3AnalysisSettings analysis = value.TaskMinus3 ?? new TaskMinus3AnalysisSettings();
            SetNumeric(minus3MaximumPulses, analysis.MaximumPulseCount);
            SetNumeric(calibrationAverageCount, analysis.CalibrationAverageCount);
            SetNumeric(noiseSimilarityTolerance, value.NoiseSimilarityTolerancePercent);
            SetNumeric(minus3ReflectionSuppression, analysis.MultipleReflectionSuppressionCount);
            minus3AlgorithmIndex.SelectedIndex = analysis.AlgorithmIndex == 5 ? 0 : analysis.AlgorithmIndex == 7 ? 1 : analysis.AlgorithmIndex == 8 ? 2 : analysis.AlgorithmIndex == 9 ? 3 : -1;
            RefreshLocationMethodNote();
            LoadEndpointPolicy(analysis.EndpointPolicy);
            minus3ModelPath.Text = analysis.ModelLibraryPath;
            defectModelKind.SelectedIndex = DefectModelCatalog.Normalize(value.DefectModelKind)==DefectModelCatalog.Competition?1:0;
            competitionModelPath.Text = value.CompetitionModelPath;
            unifiedModel.Text = value.UnifiedAnalysisModelPath;
            task35Model.Text = value.Task35ModelPath;
            task36Model.Text = value.Task36ModelPath;
            task37Model.Text = value.Task37ModelPath;
            task38Model.Text = value.Task38ModelPath;
        }

        private void CapturePlusDataValues(AppSettings value)
        {
            if (!plusDataUnlocked) throw new InvalidOperationException("HV DAC Xpert Plus Data 尚未授权。");
            value.EnableAiFeatures = enableAiFeatures.Checked;
            value.EnableDefectRecognition = enableDefectRecognition.Checked;
            value.EnableCompetitionDefectModule = enableCompetitionDefectModule.Checked;
            value.PdSampleRateHz = ReadNumber(pdSampleRate);
            value.PdSampleBits = Decimal.ToInt32(pdSampleBits.Value);
            value.NoiseSimilarityTolerancePercent = ReadNumber(noiseSimilarityTolerance);
            value.TaskMinus3 = value.TaskMinus3 ?? new TaskMinus3AnalysisSettings();
            value.TaskMinus3.MaximumPulseCount = Decimal.ToInt32(minus3MaximumPulses.Value);
            value.TaskMinus3.CalibrationAverageCount = Decimal.ToInt32(calibrationAverageCount.Value);
            value.TaskMinus3.MultipleReflectionSuppressionCount = Decimal.ToInt32(minus3ReflectionSuppression.Value);
            if (minus3AlgorithmIndex.SelectedIndex < 0) throw new InvalidOperationException(SettingsText("请选择定位方法 A、B、E 或 E+。", "Select location method A, B, E or E+."));
            value.TaskMinus3.AlgorithmIndex = new[]{5,7,8,9}[minus3AlgorithmIndex.SelectedIndex];
            value.LocationFilterAlgorithmId="B";
            value.TaskMinus3.EndpointPolicy = CaptureEndpointPolicy();
            value.TaskMinus3.ModelLibraryPath = minus3ModelPath.Text;
            value.DefectModelKind = defectModelKind.SelectedIndex==1?DefectModelCatalog.Competition:DefectModelCatalog.Standard;
            value.CompetitionModelPath = competitionModelPath.Text;
            value.UnifiedAnalysisModelPath = unifiedModel.Text;
            value.Task35ModelPath = task35Model.Text;
            value.Task36ModelPath = task36Model.Text;
            value.Task37ModelPath = task37Model.Text;
            value.Task38ModelPath = task38Model.Text;
        }
        private void RefreshLocationMethodNote()
        {
            if(locationMethodNote==null)return;
            string caption=minus3AlgorithmIndex.SelectedIndex<0?"原定位方法已停用，请选择 A、B、E 或 E+ 后保存。原有分析结果保持不变。":minus3AlgorithmIndex.SelectedIndex>=2?"E/E+ 使用原始峰值计时与当前相别 CABLE 等效波速；E+ 增加时域抑制。分析前滤波、首达计时和连接线扣除不适用。研究分数不是准确率。更改后须重新分析。":"更改定位方法后，请重新分析数据。原有分析结果保持不变。";
            settingsCaptions[locationMethodNote]=caption;
            locationMethodNote.Text=SettingsCaption(caption);
        }
    }
}
