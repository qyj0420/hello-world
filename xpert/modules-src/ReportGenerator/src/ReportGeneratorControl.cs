using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.Controls;
using HVX.ProjectModel;

namespace HVX.ReportGenerator
{
    public enum ReportOutputFormat
    {
        PdfOnly = 0,
        WordOnly = 1,
        PdfAndWord = 2
    }

    [DefaultEvent("ReportGenerated")]
    public sealed partial class ReportGeneratorControl : UserControl
    {
        static readonly Size DesignReferenceSize = new Size(1920, 1080);
        sealed class LocationSelection
        {
            internal VoltageInterval Interval;
            internal ReportPhase Phase;
            internal bool ChargeLocation;
        }

        enum ExistingOutputChoice { Cancel, SaveAs, Overwrite }

        readonly AnalysisPackageReader analysisReader = new AnalysisPackageReader();
        readonly ConclusionService conclusionService = new ConclusionService();
        readonly ReportDocumentService documentService = new ReportDocumentService();
        readonly List<VoltageInterval> locationIntervals = new List<VoltageInterval>();
        readonly List<StatisticsRangeOption> statisticsOptions = new List<StatisticsRangeOption>();
        readonly List<string> previewTemporaryFiles = new List<string>();
        // Keep only a small set of rendered files; never retain full-size bitmaps per node.
        const int PreviewCacheLimit = 8;
        readonly Dictionary<string, string> previewCache = new Dictionary<string, string>();
        readonly Queue<string> previewCacheOrder = new Queue<string>();
        string activePreviewKey = string.Empty;
        string previewDirectory = string.Empty;
        string conclusionSnapshot = string.Empty;
        readonly TreeView tree = new ReportTreeView { CheckBoxes = true, Dock = DockStyle.Fill };
        readonly Label projectHeader = new Label();
        readonly Label moduleTitle = new Label();
        bool embeddedMode;
        public bool EmbeddedMode { get { return embeddedMode; } set { embeddedMode = value; moduleTitle.Visible = !value; } }
        readonly Label contentHeading = new Label();
        readonly Label status = new Label();
        readonly Label previewDescription = new Label();
        readonly PictureBox previewImage = new PictureBox();
        readonly DataGridView previewGrid = new DataGridView();
        readonly RichTextBox previewText = new RichTextBox();
        readonly RichTextBox conclusion = new RichTextBox();
        readonly Label conclusionHeading = new Label();
        readonly Label recommendation = new Label();
        readonly CheckBox briefConclusion = new CheckBox { Checked = true, AutoSize = true };
        readonly CheckBox professionalConclusion = new CheckBox { Checked = false, AutoSize = true };
        readonly Button loadButton = new ReportButton();
        readonly Button settingsButton = new ReportButton();
        readonly Button aiButton = new ReportButton();
        readonly Button clearButton = new ReportButton();
        readonly Button generateButton = new ReportButton();
        readonly Button openReportButton = new ReportButton();
        readonly Button openReportDirectoryButton = new ReportButton();
        readonly Button printReportButton = new ReportButton();
        readonly TableLayoutPanel projectInformationPanel = new TableLayoutPanel();
        readonly ReportCableStructurePreview projectStructure = new ReportCableStructurePreview();
        readonly ProgressBar reportProgress = new ProgressBar();
        readonly ContextMenuStrip locationMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem addRangeMenu = new ToolStripMenuItem();
        readonly ToolStripMenuItem deleteRangeMenu = new ToolStripMenuItem();

        string projectPath = string.Empty;
        string analysisPath = string.Empty;
        string templatePath = string.Empty;
        string reportEnginePath = string.Empty;
        string task15RuntimePath = string.Empty;
        string outputDirectory = string.Empty;
        ReportOutputFormat outputFormat = ReportOutputFormat.PdfAndWord;
        string conclusionSystemName = "HV-DAC Xpert";
        string lastGeneratedDocxPath = string.Empty;
        string lastGeneratedPdfPath = string.Empty;
        string lastError = string.Empty;
        int languageIndex;
        bool busy, changingChecks, promptQueued, promptShownForProject;
        ReportAnalysisPackage analysisPackage;
        bool preferSavedConclusionSnapshot;
        bool conclusionGenerationRequested;
        bool changingConclusionMode;
        ReportAnalysisPackage renderedConclusionPackage;
        readonly Dictionary<int, string> savedConclusionPresentations = new Dictionary<int, string>();
        BrowserProject waveformProject;
        ProjectReportData projectData;
        double[,] locationMatrix = new double[0, 0];

        public event EventHandler AnalysisLoaded;
        public event EventHandler ReportGenerated;
        public event EventHandler SelectionChanged;

        public ReportGeneratorControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = ReportTheme.UiFont(9.5f, FontStyle.Regular);
            BackColor = ReportTheme.Canvas;
            ForeColor = ReportTheme.Ink;
            Size = DesignReferenceSize;
            // 1280x720 is the OUTER LabVIEW window, including its title/path bars.
            // Do not force the embedded control beyond the remaining host client area.
            MinimumSize = Size.Empty;
            templatePath = ResolveDefaultTemplate();
            BuildLayout();
            BindEvents();
            BuildDefaultIntervals(false);
            ApplyLanguage();
            BuildTree();
            UpdateInteractionState();
        }

        [Category("Project")]
        public string ProjectPath
        {
            get { return projectPath; }
            set { if (!string.Equals(projectPath, value ?? string.Empty, StringComparison.OrdinalIgnoreCase)) LoadProject(value); }
        }

        [Browsable(false)] public string AnalysisPath { get { return analysisPath; } }
        [Browsable(false)] public bool HasLoadedAnalysis { get { return !string.IsNullOrEmpty(projectPath) && (analysisPackage != null || locationMatrix.GetLength(0) > 0); } }
        [Category("Report")] public string TemplatePath { get { return templatePath; } set { templatePath = value ?? string.Empty; } }
        [Category("Report")] public string ReportEnginePath { get { return reportEnginePath; } set { reportEnginePath = value ?? string.Empty; } }
        [Category("Report")] public string Task15RuntimePath { get { return task15RuntimePath; } set { task15RuntimePath = value ?? string.Empty; conclusionService.RuntimeDllPath = task15RuntimePath; } }
        [Category("Report")] public string OutputDirectory { get { return outputDirectory; } set { outputDirectory = (value ?? string.Empty).Trim(); } }
        [Category("Report"), DefaultValue(ReportOutputFormat.PdfAndWord)]
        public ReportOutputFormat OutputFormat
        {
            get { return outputFormat; }
            set
            {
                if (!Enum.IsDefined(typeof(ReportOutputFormat), value)) throw new InvalidEnumArgumentException("value", (int)value, typeof(ReportOutputFormat));
                outputFormat = value;
            }
        }
        [Category("Behavior"), DefaultValue(true)] public bool PromptAnalysisOnLoad { get; set; } = true;
        [Browsable(false)] public bool IsBusy { get { return busy; } }
        [Browsable(false)] public string LastError { get { return lastError; } }
        [Browsable(false)] public string LastConclusionDiagnostic { get { return conclusionService.LastDiagnostic; } }
        [Browsable(false)] public Action<string> DiagnosticSink { get { return conclusionService.DiagnosticSink; } set { conclusionService.DiagnosticSink=value; } }
        [Browsable(false)] public string LastGeneratedDocxPath { get { return lastGeneratedDocxPath; } }
        [Browsable(false)] public string LastGeneratedPdfPath { get { return lastGeneratedPdfPath; } }
        [Browsable(false)] public string[] AvailableLanguages { get { return ReportLanguages.GetLanguageNames(); } }

        [Category("Localization"), DefaultValue(0)]
        public int LanguageIndex { get { return languageIndex; } set { SetLanguageIndex(value); } }
        [Browsable(false)] public string LanguageCode { get { return ReportLanguages.Codes[languageIndex]; } set { SetLanguageCode(value); } }
        [Category("Conclusion")] public string ConclusionText { get { return conclusionSnapshot; } set { SetConclusionText(value); UpdateRecommendation(); } }
        [Category("Conclusion"), DefaultValue("HV-DAC Xpert")]
        public string ConclusionSystemName
        {
            get { return conclusionSystemName; }
            set { conclusionSystemName = (value ?? string.Empty).Trim(); if (HasAnalysisData) RefreshConclusion(); }
        }

        public bool SetLanguageIndex(int index)
        {
            try { index = Xiongxin.OWTS.Localization.UiLanguage.ResolveIndex(index); ReportLanguages.Validate(index); } catch (Exception ex) { return Fail(ex.Message, false); }
            if (languageIndex == index) return true;
            languageIndex = index;
            ApplyLanguage(); BuildTree(); RefreshConclusion(); UpdateInteractionState();
            return true;
        }

        public bool SetLanguageCode(string code)
        {
            int index = ReportLanguages.FindCode(code);
            return index >= 0 && SetLanguageIndex(index);
        }

        public bool LoadProject(string path)
        {
            if (busy) return false;
            try
            {
                string full = Path.GetFullPath(path ?? string.Empty);
                if (!Directory.Exists(full)) return Fail(L(ReportLanguages.UiKeys.InvalidProjectPath), true);
                projectPath = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                projectData = ProjectReportDataReader.Read(projectPath);
                BuildDefaultIntervals(projectData != null && projectData.OlderThanOneYear);
                analysisPath = string.Empty; analysisPackage = null; waveformProject = null; locationMatrix = new double[0, 0]; promptShownForProject = false;
                lastGeneratedDocxPath = string.Empty; lastGeneratedPdfPath = string.Empty; lastError = string.Empty;
                UpdateHeader(); BuildTree(); ShowProjectInformation(); RefreshConclusion(); UpdateInteractionState();
                SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false); QueueAnalysisPrompt();
                return true;
            }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(),"无法加载项目。请检查项目文件及访问权限后重试。","The project could not be loaded. Check project files and access permissions, then retry."), true); }
        }

        public bool LoadAnalysis(string path)
        {
            if (busy) return false;
            ReportAnalysisPackage package; string error;
            if (!analysisReader.TryLoad(path, out package, out error)) return Fail(ReportOutputMessage(error,"无法加载分析。请在数据分析中核对并重新保存后重试。","The analysis could not be loaded. Review and save it again in Data analysis, then retry."), true);
            return ApplyAnalysisPackage(package);
        }

        public void SetLocationMatrix(double[,] matrix)
        {
            preferSavedConclusionSnapshot=false;
            locationMatrix = matrix == null ? new double[0, 0] : (double[,])matrix.Clone();
            UpdatePhaseDischargeVoltagesFromMatrix();
            RefreshConclusion(); BuildTree(); UpdateInteractionState();
        }

        public bool AddVoltageRange(double minimumU0, double maximumU0, string title)
        {
            if (!HasAnalysisData) return Fail(L(ReportLanguages.UiKeys.AnalysisRequired), true);
            try
            {
                if (minimumU0 < 0.0 || maximumU0 > 2.0 || minimumU0 >= maximumU0) return Fail(L(ReportLanguages.UiKeys.RangeOutsideBounds), true);
                if (locationIntervals.Any(item => Math.Abs(item.MinimumU0 - minimumU0) < 1e-9 && Math.Abs(item.MaximumU0 - maximumU0) < 1e-9)) return Fail(L(ReportLanguages.UiKeys.RangeOverlap), true);
                string display = string.IsNullOrWhiteSpace(title) ? VoltageRangeDialog.FormatRange(minimumU0, maximumU0) : title.Trim();
                locationIntervals.Add(new VoltageInterval("custom_" + Guid.NewGuid().ToString("N"), display, minimumU0, maximumU0, true, false));
                BuildTree(); return true;
            }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(),"无法添加电压范围。请核对范围后重试。","The voltage range could not be added. Check the range and retry."), true); }
        }

        public bool RemoveVoltageRange(string intervalId)
        {
            if (!HasAnalysisData) return Fail(L(ReportLanguages.UiKeys.AnalysisRequired), true);
            VoltageInterval interval = locationIntervals.FirstOrDefault(item => string.Equals(item.Id, intervalId, StringComparison.OrdinalIgnoreCase));
            if (interval == null || !interval.CanDelete || interval.IsAllResults) return false;
            locationIntervals.Remove(interval); BuildTree(); return true;
        }

        public bool ShowLoadAnalysisDialog()
        {
            if (busy) return false;
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
                return Fail(L(ReportLanguages.UiKeys.InvalidProjectPath), true);
            string[] paths = analysisReader.EnumeratePackagePaths(projectPath);
            var cache = new Dictionary<string, ReportAnalysisPackage>(StringComparer.OrdinalIgnoreCase);
            using (var form = new SavedAnalysisPicker(languageIndex, L(ReportLanguages.UiKeys.LoadAnalysis),
                paths.Length == 0 ? L(ReportLanguages.UiKeys.NoAnalysisFound) : L(ReportLanguages.UiKeys.SelectAnalysisPrompt),
                L(ReportLanguages.UiKeys.SkipLoading), L(ReportLanguages.UiKeys.LoadAnalysis), paths, Font, DialogIcon()))
            {
                var message = form.Message; var load = form.LoadButton; var attachments = form.AttachmentsButton;
                Func<string, ReportAnalysisPackage> getPackage = delegate(string selectedPath) { ReportAnalysisPackage found; if (!cache.TryGetValue(selectedPath, out found)) { found = analysisReader.Load(selectedPath); cache[selectedPath] = found; } return found; };
                attachments.Click += (s, e) => { if (form.SelectedPath.Length > 0) try { ShowAttachmentSelection(getPackage(form.SelectedPath)); } catch (Exception ex) { message.ForeColor = ReportTheme.Error; message.Text = ReportOutputMessage(ex.ToString(),"无法读取报告附件。请核对并重新保存分析后重试。","Report attachments could not be read. Review and save the analysis again, then retry."); } };
                Action loadSelected = delegate {
                    if (form.SelectedPath.Length == 0) return;
                    try { if (ApplyAnalysisPackage(getPackage(form.SelectedPath))) { form.DialogResult = DialogResult.OK; form.Close(); } else { message.ForeColor = ReportTheme.Error; message.Text = lastError; } }
                    catch (Exception ex) { message.ForeColor = ReportTheme.Error; message.Text = ReportOutputMessage(ex.ToString(),"无法加载分析。请在数据分析中核对并重新保存后重试。","The analysis could not be loaded. Review and save it again in Data analysis, then retry."); }
                };
                load.Click += (s, e) => loadSelected(); form.Grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) {form.Grid.ClearSelection();form.Grid.Rows[e.RowIndex].Selected=true;loadSelected();} };
                form.DeleteButton.Click += delegate {
                    string[] chosen=form.SelectedPaths;if(chosen.Length==0)return;
                    if(MessageBox.Show(form,ReportLanguages.Ui(languageIndex,"将所选 "+chosen.Length+" 条分析移至可恢复目录？原始测试数据不会删除。","Move the selected "+chosen.Length+" analyses to recovery? Raw test data will not be deleted."),ReportLanguages.Ui(languageIndex,"删除分析","Remove analyses"),MessageBoxButtons.OKCancel,MessageBoxIcon.Question)!=DialogResult.OK)return;
                    var completed=new List<string>();
                    try {
                        string[] allowed=analysisReader.EnumeratePackagePaths(projectPath);
                        foreach(string selected in chosen) {
                            string recovered=AnalysisRecovery.Move(projectPath,selected,allowed,analysisPackage==null?null:analysisPackage.SourcePath);
                            completed.Add(recovered);cache.Remove(selected);
                            foreach(DataGridViewRow row in form.Grid.Rows.Cast<DataGridViewRow>().Where(r=>String.Equals(Convert.ToString(r.Tag),selected,StringComparison.OrdinalIgnoreCase)).ToArray())form.Grid.Rows.Remove(row);
                        }
                        RecordReportOutputDiagnostic("Analyses moved to recovery: "+String.Join(";",completed));
                        message.ForeColor=ReportTheme.Muted;message.Text=ReportLanguages.Ui(languageIndex,"已移入恢复目录：","Moved to recovery: ")+completed.Count;
                    }catch(Exception ex){message.ForeColor=ReportTheme.Error;message.Text=ReportLanguages.Ui(languageIndex,"已移入恢复目录 "+completed.Count+" 条。","Moved "+completed.Count+" analyses to recovery.")+" "+ReportOutputMessage(ex+"; completed="+String.Join(";",completed),"其余未完成，请检查访问权限后重试。","The remaining analyses were not removed. Check access permissions and retry.");}
                };
                bool loaded = form.ShowDialog(FindForm()) == DialogResult.OK;
                if (!loaded && !HasAnalysisData)
                {
                    UpdateInteractionState();
                    SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false);
                }
                return loaded;
            }
        }

        public bool GenerateReport()
        {
            if (busy) return false;
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return Fail(L(ReportLanguages.UiKeys.InvalidProjectPath), true);
            if (!HasAnalysisData) return Fail(L(ReportLanguages.UiKeys.AnalysisRequired), true);
            if (!GenerateStateGridReport && (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))) return Fail(L(ReportLanguages.UiKeys.TemplateMissing), true);
            string outputRoot;
            try
            {
                outputRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(outputDirectory) ? projectPath : outputDirectory);
                Directory.CreateDirectory(outputRoot);
            }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(), "报告输出目录不可用，请选择可写入的文件夹。", "The report output folder is unavailable. Select a writable folder."), true); }
            string outputBase;
            try { outputBase = BuildDefaultOutputBase(outputRoot, DateTime.Now); }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(), "无法确定报告文件名，请检查输出目录后重试。", "The report file name could not be prepared. Check the output folder and retry."), true); }
            string docx = outputBase + ".docx", pdf = outputBase + ".pdf"; bool overwrite = false;
            bool pdfRequested = outputFormat != ReportOutputFormat.WordOnly;
            // The default is always a fresh, paired name. A competing writer
            // between selection and generation is rejected by Overwrite=false.
            if (outputFormat == ReportOutputFormat.PdfOnly)
                docx = Path.Combine(Path.GetDirectoryName(pdf), ".hvx-pdf-source-" + Guid.NewGuid().ToString("N") + ".docx");
            SetBusy(true, L(ReportLanguages.UiKeys.GeneratingReport));
            StateGridReportResult stateGridResult=null;
            try
            {
                reportProgress.Visible = true;
                UpdateReportProgress(5, "prepare");
                if (projectData == null) projectData = ProjectReportDataReader.Read(projectPath);
                projectData.RequireCurrentMetadata();
                // Independent supplementary output. A missing custom normal
                // template cannot suppress it, and its failure cannot replace
                // or remove a successfully generated normal report.
                stateGridResult=GenerateStateGridSupplement(pdf);
                ReportGenerationPlan plan = BuildGenerationPlan(docx, pdf, overwrite);
                plan.GeneratePdf = pdfRequested;
                UpdateReportProgress(35, "prepare");
                ReportGenerationResult result = documentService.Generate(plan, UpdateReportProgress);
                if (!result.Success) return Fail(ReportOutputMessage(result.Error, "报告未生成，请检查模板和输出目录后重试。", "The report was not generated. Check the template and output folder, then retry.")+StateGridSummary(stateGridResult), true);
                lastGeneratedDocxPath = File.Exists(result.DocxPath) ? result.DocxPath : string.Empty;
                lastGeneratedPdfPath = result.PdfCreated && File.Exists(result.PdfPath) ? result.PdfPath : string.Empty;
                if (outputFormat == ReportOutputFormat.PdfOnly && !result.PdfCreated)
                {
                    string failure = L(ReportLanguages.UiKeys.PdfConversionFailed);
                    RecordReportOutputDiagnostic(result.Warning);
                    if (lastGeneratedDocxPath.Length > 0)
                        failure += Environment.NewLine + ReportLanguages.Ui(languageIndex, "已保留 Word 文件，可在报告目录中查看。", "A Word file was retained in the report folder.");
                    return Fail(failure+StateGridSummary(stateGridResult), true);
                }

                string cleanupWarning = string.Empty;
                if (outputFormat == ReportOutputFormat.PdfOnly && lastGeneratedDocxPath.Length > 0)
                {
                    try { File.Delete(lastGeneratedDocxPath); lastGeneratedDocxPath = string.Empty; }
                    catch (Exception ex)
                    {
                        cleanupWarning = ReportOutputMessage(ex.ToString(), "PDF 已生成，另保留了一份临时 Word 文件。", "The PDF was created. A temporary Word file was also retained.");
                    }
                }

                string primaryPath = outputFormat == ReportOutputFormat.PdfOnly ? lastGeneratedPdfPath : lastGeneratedDocxPath;
                RecordReportOutputDiagnostic("Report generated: "+primaryPath);
                string message = ReportLanguages.Ui(languageIndex,"报告已生成，可点击“打开报告”查看。","The report was generated. Select Open report to view it.");
                bool pdfWarning = outputFormat == ReportOutputFormat.PdfAndWord && !result.PdfCreated;
                if (pdfWarning) { RecordReportOutputDiagnostic(result.Warning); message += Environment.NewLine + L(ReportLanguages.UiKeys.PdfConversionFailed); }
                if (cleanupWarning.Length > 0) message += Environment.NewLine + cleanupWarning;
                message+=StateGridSummary(stateGridResult);
                bool warning = pdfWarning || cleanupWarning.Length > 0 || StateGridWarning(stateGridResult);
                SetStatus(message.Replace(Environment.NewLine, "  "), warning);
                MessageBox.Show(FindForm(), message, L(ReportLanguages.UiKeys.Title), MessageBoxButtons.OK, warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                if (ReportGenerated != null) ReportGenerated(this, EventArgs.Empty); return true;
            }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(), "报告未完成，请检查模板和输出目录后重试。", "The report could not be completed. Check the template and output folder, then retry.")+StateGridSummary(stateGridResult), true); }
            finally { reportProgress.Visible = false; SetBusy(false, string.Empty); }
        }

        public bool OpenGeneratedReport()
        {
            string path = FindOpenableReportPath();
            if (path.Length == 0) return Fail(ReportLanguages.Ui(languageIndex, "尚无可打开的报告，请先生成报告。", "No generated report is available. Generate a report first."), true);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
            catch (Exception ex) { return Fail(ReportOutputMessage(ex.ToString(),"无法打开报告。请确认报告文件和 PDF/Word 程序可用后重试。","The report could not be opened. Check the report file and PDF/Word application, then retry."), true); }
        }

        internal string BuildDefaultOutputBase(string outputRoot, DateTime time)
        {
            if (projectData == null) projectData = ProjectReportDataReader.Read(projectPath);
            return ReportFileNames.AvailableBase(outputRoot, new DirectoryInfo(projectPath).Name,
                projectData.StartName, projectData.EndName, languageIndex, time, GenerateStateGridReport);
        }

        string FindOpenableReportPath()
        {
            if (File.Exists(lastGeneratedPdfPath)) return lastGeneratedPdfPath;
            if (File.Exists(lastGeneratedDocxPath)) return lastGeneratedDocxPath;
            if (!HasValidProject) return string.Empty;
            try
            {
                string reportRoot = string.IsNullOrWhiteSpace(outputDirectory) ? projectPath : Path.GetFullPath(outputDirectory);
                if (!Directory.Exists(reportRoot)) return string.Empty;
                if (projectData == null) projectData = ProjectReportDataReader.Read(projectPath);
                return ReportFileNames.FindLatest(reportRoot, new DirectoryInfo(projectPath).Name,
                    projectData.StartName, projectData.EndName, languageIndex);
            }
            catch (IOException) { return string.Empty; }
            catch (UnauthorizedAccessException) { return string.Empty; }
        }

        void BuildLayout()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty, ColumnCount = 1, RowCount = 4, BackColor = ReportTheme.Canvas };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); Controls.Add(shell);
            var top = new Panel { Dock = DockStyle.Fill, BackColor = ReportTheme.SurfaceAlt, Padding = new Padding(16, 0, 12, 0) };
            moduleTitle.Text = "生成报告"; moduleTitle.Dock = DockStyle.Left; moduleTitle.Width = 140;
            moduleTitle.ForeColor = ReportTheme.Ink; moduleTitle.Font = ReportTheme.UiFont(12f, FontStyle.Bold);
            moduleTitle.TextAlign = ContentAlignment.MiddleLeft;
            projectHeader.Dock = DockStyle.Fill; projectHeader.ForeColor = ReportTheme.Muted; projectHeader.Font = ReportTheme.UiFont(9.5f, FontStyle.Regular);
            projectHeader.TextAlign = ContentAlignment.MiddleCenter; projectHeader.AutoEllipsis = true;
            loadButton.Dock = DockStyle.Right; loadButton.Width = 160; loadButton.Margin = new Padding(8, 7, 0, 7); ReportTheme.StylePrimaryButton(loadButton);
            openReportButton.Dock = DockStyle.Right; openReportButton.Width = 160; openReportButton.Margin = new Padding(8, 7, 0, 7); ReportTheme.StyleSecondaryButton(openReportButton);
            openReportDirectoryButton.Dock = DockStyle.Right; ReportTheme.StyleSecondaryButton(openReportDirectoryButton);
            printReportButton.Dock = DockStyle.Right; ReportTheme.StyleSecondaryButton(printReportButton);
            top.Controls.Add(projectHeader); top.Controls.Add(moduleTitle); top.Controls.Add(openReportButton); top.Controls.Add(openReportDirectoryButton); top.Controls.Add(printReportButton); top.Controls.Add(loadButton); shell.Controls.Add(top, 0, 0);
            shell.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = ReportTheme.Border }, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5, Size = new Size(1920, 900), BackColor = ReportTheme.Border };
            split.Panel1MinSize = 220; split.Panel2MinSize = 600; split.SplitterDistance = 384;
            split.SizeChanged += (s, e) =>
            {
                int maximum = split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth;
                if (maximum < split.Panel1MinSize) return;
                int desired = Math.Max(250, Math.Min(420, split.ClientSize.Width / 5));
                desired = Math.Min(maximum, desired);
                if (split.SplitterDistance != desired) split.SplitterDistance = desired;
            };
            split.Panel1.BackColor = ReportTheme.Surface; split.Panel1.Padding = new Padding(12); split.Panel1.Controls.Add(tree);
            shell.Controls.Add(split, 0, 2); ReportTheme.StyleTree(tree);
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = ReportTheme.Surface };
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); split.Panel2.Controls.Add(right);
            contentHeading.Dock = DockStyle.Fill; contentHeading.BackColor = ReportTheme.SectionBar; contentHeading.ForeColor = ReportTheme.Navy; contentHeading.Font = ReportTheme.UiFont(10.5f, FontStyle.Bold);
            contentHeading.TextAlign = ContentAlignment.MiddleLeft; contentHeading.Padding = new Padding(14, 0, 8, 0); contentHeading.AutoEllipsis = true; contentHeading.Paint += ReportTheme.DrawHeadingAccent;
            right.Controls.Add(contentHeading, 0, 0);
            var previewCard = new Panel { Dock = DockStyle.Fill, BackColor = ReportTheme.Canvas, Padding = new Padding(0, 8, 0, 8), Margin = Padding.Empty };
            var previewSurface = new Panel { Dock = DockStyle.Fill, BackColor = ReportTheme.Surface, Padding = new Padding(12) }; previewSurface.Paint += ReportTheme.DrawCardBorder;
            previewCard.Controls.Add(previewSurface);
            right.Controls.Add(previewConclusionSplit, 0, 1);
            previewConclusionSplit.Panel1.Controls.Add(previewCard);
            previewDescription.Dock = DockStyle.Top; previewDescription.Height = 30; previewDescription.ForeColor = ReportTheme.Muted; previewDescription.TextAlign = ContentAlignment.MiddleLeft; previewDescription.AutoEllipsis = true;
            previewImage.Dock = DockStyle.Fill; previewImage.BackColor = ReportTheme.Surface; previewImage.SizeMode = PictureBoxSizeMode.Zoom; ConfigurePreviewGrid();
            previewText.Dock = DockStyle.Fill; previewText.ReadOnly = true; previewText.BorderStyle = BorderStyle.None; previewText.BackColor = ReportTheme.Surface; previewText.ForeColor = ReportTheme.Ink;
            previewText.Font = ReportTheme.UiFont(9.5f, FontStyle.Regular); previewText.WordWrap = true; previewText.ScrollBars = RichTextBoxScrollBars.Vertical;
            previewSurface.Controls.Add(previewImage); previewSurface.Controls.Add(previewGrid); previewSurface.Controls.Add(previewText); previewSurface.Controls.Add(previewDescription);
            projectInformationPanel.Dock = DockStyle.Fill; projectInformationPanel.ColumnCount = 1; projectInformationPanel.RowCount = 2;
            projectInformationPanel.Margin = Padding.Empty; projectInformationPanel.BackColor = ReportTheme.Surface;
            projectInformationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            projectInformationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            projectInformationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            projectStructure.Dock = DockStyle.Fill; projectStructure.Margin = Padding.Empty;
            projectInformationPanel.Controls.Add(projectStructure, 0, 1);
            previewSurface.Controls.Add(projectInformationPanel); projectInformationPanel.Visible = false;
            var conclusionCard = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = ReportTheme.Surface, Padding = Padding.Empty, Margin = new Padding(0, 0, 0, 8), ColumnCount = 1, RowCount = 3 };
            conclusionCard.Paint += ReportTheme.DrawCardBorder;
            conclusionCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); conclusionCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); conclusionCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            conclusionHeading.Dock = DockStyle.Fill; conclusionHeading.Font = ReportTheme.UiFont(10.5f, FontStyle.Bold); conclusionHeading.ForeColor = ReportTheme.Navy;
            conclusionHeading.BackColor = ReportTheme.SectionBar; conclusionHeading.Padding = new Padding(12, 0, 12, 0); conclusionHeading.Margin = Padding.Empty; conclusionHeading.TextAlign = ContentAlignment.MiddleLeft;
            var conclusionSurface = new Panel { Dock = DockStyle.Fill, BackColor = ReportTheme.Border, Padding = new Padding(1), Margin = new Padding(12, 4, 12, 8) };
            conclusion.Dock = DockStyle.Fill; conclusion.BorderStyle = BorderStyle.None; conclusion.BackColor = ReportTheme.Surface; conclusion.ForeColor = ReportTheme.Ink;
            conclusion.Font = ReportTheme.UiFont(9.5f, FontStyle.Regular); conclusion.ScrollBars = RichTextBoxScrollBars.Vertical;
            conclusionSurface.Controls.Add(conclusion);
            briefConclusion.ForeColor = ReportTheme.Ink; professionalConclusion.ForeColor = ReportTheme.Ink;
            briefConclusion.AutoSize = false; professionalConclusion.AutoSize = false;
            briefConclusion.AutoEllipsis = false; professionalConclusion.AutoEllipsis = false;
            briefConclusion.Size = new Size(112, 34); professionalConclusion.Size = new Size(112, 34);
            briefConclusion.TextAlign = ContentAlignment.MiddleLeft; professionalConclusion.TextAlign = ContentAlignment.MiddleLeft;
            briefConclusion.Margin = new Padding(0, 2, 0, 0); professionalConclusion.Margin = new Padding(0, 2, 0, 0);
            recommendation.Dock = DockStyle.Fill; recommendation.ForeColor = ReportTheme.Muted; recommendation.Font = ReportTheme.UiFont(9.5f, FontStyle.Regular); recommendation.AutoEllipsis = true; recommendation.Padding = new Padding(12, 0, 12, 0);
            var conclusionHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = ReportTheme.SectionBar };
            ReportTheme.StyleSecondaryButton(conclusionFeedbackButton);
            conclusionFeedbackButton.Dock = DockStyle.Right; conclusionFeedbackButton.Width = 192;
            conclusionHeader.Controls.Add(conclusionHeading); conclusionHeader.Controls.Add(conclusionFeedbackButton);
            conclusionCard.Controls.Add(conclusionHeader, 0, 0); conclusionCard.Controls.Add(conclusionSurface, 0, 1); conclusionCard.Controls.Add(recommendation, 0, 2);
            previewConclusionSplit.Panel2.Controls.Add(conclusionCard);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(8, 5, 8, 5), BackColor = ReportTheme.SurfaceAlt };
            foreach (float weight in new[] { 12f, 14f, 18f, 22f, 10f, 24f }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weight));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            ReportTheme.StylePrimaryButton(generateButton); ReportTheme.StyleSecondaryButton(clearButton); ReportTheme.StyleSecondaryButton(aiButton); ReportTheme.StyleSecondaryButton(settingsButton);
            Control[] actionControls = { briefConclusion, professionalConclusion, settingsButton, aiButton, clearButton, generateButton };
            for (int i = 0; i < actionControls.Length; i++)
            {
                actionControls[i].Dock = DockStyle.Fill;
                actionControls[i].Margin = new Padding(3, 2, 3, 2);
                actions.Controls.Add(actionControls[i], i, 0);
            }
            right.Controls.Add(actions, 0, 2);
            // Reserve footer space first; only the preview absorbs a smaller host client area.
            // Measure translated labels so two/three-line captions never disappear off-screen.
            bool updatingLayout = false;
            Action fitLayout = () =>
            {
                if (updatingLayout || right.ClientSize.Width <= 0) return;
                updatingLayout = true;
                try
                {
                    bool compact = ClientSize.Height < 760;
                    // Fit the translated caption rather than assuming all languages
                    // fit the English percentage widths. Font sizes never change.
                    int footerHeight = ReportActionLayout.Arrange(actions, actionControls, 6, 3, 2, 1);
                    right.RowStyles[0].Height = 32;
                    right.RowStyles[2].Height = footerHeight;
                    loadButton.Width = TextRenderer.MeasureText(loadButton.Text, loadButton.Font).Width + 24;
                    openReportButton.Width = TextRenderer.MeasureText(openReportButton.Text, openReportButton.Font).Width + 24;
                    openReportDirectoryButton.Width = TextRenderer.MeasureText(openReportDirectoryButton.Text, openReportDirectoryButton.Font).Width + 24;
                    printReportButton.Width = TextRenderer.MeasureText(printReportButton.Text, printReportButton.Font).Width + 24;
                    conclusionFeedbackButton.Width = TextRenderer.MeasureText(conclusionFeedbackButton.Text, conclusionFeedbackButton.Font).Width + 24;
                    shell.RowStyles[0].Height = Math.Max(48, Math.Max(loadButton.Font.Height, openReportButton.Font.Height) + 20);
                    conclusionCard.RowStyles[0].Height = Math.Max(36, conclusionFeedbackButton.Font.Height + 12);
                    conclusionCard.RowStyles[2].Height = string.IsNullOrWhiteSpace(recommendation.Text) ? 0 : 24;
                    previewDescription.Height = compact ? 24 : 30;
                }
                finally { updatingLayout = false; }
            };
            right.SizeChanged += (s, e) => fitLayout();
            actions.Layout += (s, e) => fitLayout();
            recommendation.TextChanged += (s, e) => fitLayout();
            foreach (Control action in actionControls) action.TextChanged += (s, e) => fitLayout();
            loadButton.TextChanged += (s, e) => fitLayout();
            openReportButton.TextChanged += (s, e) => fitLayout();
            openReportDirectoryButton.TextChanged += (s, e) => fitLayout();
            printReportButton.TextChanged += (s, e) => fitLayout();
            conclusionFeedbackButton.TextChanged += (s, e) => fitLayout();
            var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = ReportTheme.Canvas };
            statusRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
            status.Dock = DockStyle.Fill; status.BackColor = ReportTheme.Canvas; status.ForeColor = ReportTheme.Muted; status.TextAlign = ContentAlignment.MiddleLeft; status.Padding = new Padding(8, 0, 0, 0); status.AutoEllipsis = true;
            reportProgress.Dock = DockStyle.Fill; reportProgress.Margin = new Padding(4, 5, 8, 5); reportProgress.Style = ProgressBarStyle.Continuous; reportProgress.Visible = false;
            statusRow.Controls.Add(status, 0, 0); statusRow.Controls.Add(reportProgress, 1, 0);
            shell.Controls.Add(statusRow, 0, 3); addRangeMenu.Click += (s, e) => AddRangeFromDialog(); deleteRangeMenu.Click += (s, e) => DeleteSelectedRange();
            locationMenu.Items.Add(addRangeMenu); locationMenu.Items.Add(deleteRangeMenu);
        }

        void ConfigurePreviewGrid()
        {
            previewGrid.Dock = DockStyle.Fill; previewGrid.ReadOnly = true; previewGrid.AllowUserToAddRows = false; previewGrid.AllowUserToDeleteRows = false; previewGrid.AllowUserToResizeRows = false;
            previewGrid.RowHeadersVisible = false; previewGrid.BackgroundColor = ReportTheme.Surface; previewGrid.BorderStyle = BorderStyle.None; previewGrid.EnableHeadersVisualStyles = false;
            previewGrid.ColumnHeadersDefaultCellStyle.BackColor = ReportTheme.Header; previewGrid.ColumnHeadersDefaultCellStyle.ForeColor = ReportTheme.Ink; previewGrid.ColumnHeadersDefaultCellStyle.Font = ReportTheme.UiFont(9.5f, FontStyle.Bold);
            previewGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            previewGrid.ColumnHeadersHeight = Math.Max(34, previewGrid.ColumnHeadersDefaultCellStyle.Font.Height + 12);
            previewGrid.DefaultCellStyle.ForeColor = ReportTheme.Ink; previewGrid.DefaultCellStyle.SelectionBackColor = ReportTheme.Selection; previewGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            previewGrid.AlternatingRowsDefaultCellStyle.BackColor = ReportTheme.SurfaceAlt; previewGrid.GridColor = ReportTheme.Border; previewGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; previewGrid.RowTemplate.Height = 27;
            previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", FillWeight = 38 });
            previewGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 62 });
        }

        void BindEvents()
        {
            loadButton.Click += (s, e) => ShowLoadAnalysisDialog(); openReportButton.Click += (s, e) => OpenGeneratedReport(); settingsButton.Click += (s, e) => ShowSettings(); aiButton.Click += (s, e) => { preferSavedConclusionSnapshot=false; conclusionGenerationRequested=true; RefreshConclusion(); };
            openReportDirectoryButton.Click += (s, e) => OpenReportDirectory();
            printReportButton.Click += (s, e) => PrintGeneratedReport();
            conclusionFeedbackButton.Click += (s, e) => ShowConclusionFeedback();
            clearButton.Click += (s, e) => ClearOptionalSelections(); generateButton.Click += (s, e) => GenerateReport(); conclusion.TextChanged += (s, e) => { conclusionSnapshot = conclusion.Text; UpdateRecommendation(); };
            briefConclusion.CheckedChanged += (s, e) => OnConclusionModeChanged(briefConclusion);
            professionalConclusion.CheckedChanged += (s, e) => OnConclusionModeChanged(professionalConclusion);
            tree.BeforeCheck += (s, e) => { if (!changingChecks && !e.Node.Checked && !IsNodeAvailable(e.Node)) e.Cancel = true; };
            tree.AfterSelect += (s, e) => ShowNode(e.Node); tree.AfterCheck += OnTreeAfterCheck; tree.NodeMouseClick += OnTreeNodeMouseClick;
            tree.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) { TreeNode node = tree.GetNodeAt(e.Location); if (node != null) tree.SelectedNode = node; } };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); QueueAnalysisPrompt(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) QueueAnalysisPrompt(); }

        void QueueAnalysisPrompt()
        {
            if (!PromptAnalysisOnLoad || promptQueued || promptShownForProject || !HasValidProject || !Visible || !IsHandleCreated || IsDisposed) return;
            promptQueued = true;
            BeginInvoke((MethodInvoker)delegate { promptQueued = false; if (IsDisposed || !Visible || promptShownForProject || !PromptAnalysisOnLoad) return; promptShownForProject = true; ShowLoadAnalysisDialog(); });
        }

        bool ApplyAnalysisPackage(ReportAnalysisPackage package)
        {
            if (package == null) return false;
            if(package.ReportInputContextPresent&&!package.HasReportInputSnapshot)
            {
                conclusionService.RecordDiagnosticMessage("ReportInputRejected; "+package.ReportInputStatus);
                return Fail(ReportLanguages.Ui(languageIndex,"保存的报告输入或接头近似参数不完整，请重新打开并保存最终分析。","The saved report input or joint-approximation settings are incomplete. Reopen and save the final analysis."),false);
            }
            if(package.Kind==AnalysisPackageKind.NamedAnalysis&&!package.HasEffectiveSelectionSnapshot){
                conclusionService.RecordDiagnosticMessage("ReportEffectiveSelectionRejected; Source="+package.SourcePath+"; "+package.EffectiveSelectionStatus);
                return Fail(ReportLanguages.Ui(languageIndex,"无法核对已保存分析的筛选结果。请在数据分析中重新打开并保存后再生成报告。","The saved analysis selection could not be verified. Reopen and save it in Data analysis before generating a report."),false);
            }
            analysisPackage = package; analysisPath = package.SourcePath;
            conclusionGenerationRequested=false;
            if(package.HasSavedConclusionSnapshot&&!package.UsesEvidenceV3)
                conclusionService.RecordDiagnosticMessage("SavedLegacyConclusion; Source="+package.SourcePath+"; Api="+package.ConclusionApi+"; presentation-only; no reassessment");
            preferSavedConclusionSnapshot=!package.HasReportInputSnapshot && package.HasSavedConclusionSnapshot && (!package.UsesEvidenceV3 || briefConclusion.Checked);
            if (!string.IsNullOrWhiteSpace(package.ProjectPath) && Directory.Exists(package.ProjectPath))
            {
                string nextProject = Path.GetFullPath(package.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string currentProject = string.IsNullOrWhiteSpace(projectPath) ? string.Empty :
                    Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                ProjectReportData nextData = ProjectReportDataReader.Read(nextProject);
                if (!string.Equals(currentProject, nextProject, StringComparison.OrdinalIgnoreCase))
                {
                    lastGeneratedDocxPath = string.Empty; lastGeneratedPdfPath = string.Empty;
                }
                projectPath = nextProject; projectData = nextData;
            }
            try { waveformProject = BrowserReader.LoadProject(projectPath); }
            catch (Exception) { waveformProject = null; }
            locationMatrix = package.StatisticsMatrix;
            UpdatePhaseDischargeVoltagesFromMatrix();
            BuildDefaultIntervals(projectData != null && projectData.OlderThanOneYear); UpdateHeader(); BuildTree(); RefreshConclusion();
            UpdateInteractionState();
            string loadNotice=String.Join(" ",new[]{PartialSourceLoadWarning(),AcquisitionRiskShortWarning()}.Where(x=>x.Length>0));
            SetStatus(loadNotice.Length>0?loadNotice:(ReportLanguages.Ui(languageIndex, "已加载分析：", "Analysis loaded: ")) + AnalysisDateText.DisplayAutomaticName(package.AnalysisName), false);
            if (AnalysisLoaded != null) AnalysisLoaded(this, EventArgs.Empty); return true;
        }

        void BuildDefaultIntervals(bool older)
        {
            VoltageInterval[] custom = locationIntervals.Where(item => item.Id.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)).ToArray();
            locationIntervals.Clear(); locationIntervals.AddRange(ReportStatistics.CreateDefaultIntervals(older)); locationIntervals.AddRange(custom);
        }

        void BuildTree()
        {
            ClearPreviewCache();
            bool analysisReady = HasAnalysisData;
            changingChecks = true;
            try
            {
                tree.BeginUpdate(); tree.Nodes.Clear();
                TreeNode project = CreateTreeNode(new ReportTreeNodeModel { Key = "project", Text = L(ReportLanguages.UiKeys.ProjectInformation), Kind = ReportTreeNodeKind.ProjectInformation, IsChecked = true, IsCheckLocked = true });
                tree.Nodes.Add(project);
                project.Checked = true;
                if (analysisReady)
                {
                    var locationModel = new ReportTreeNodeModel { Key = "location", Text = L(ReportLanguages.UiKeys.LocationResults), Kind = ReportTreeNodeKind.LocationResults, IsChecked = true, CanAddChild = true };
                    TreeNode location = CreateTreeNode(locationModel); tree.Nodes.Add(location);
                    foreach (VoltageInterval interval in locationIntervals) location.Nodes.Add(BuildIntervalNode(interval));
                    AddImageRoot(ReportTreeNodeKind.CalibrationData, ReportImageCategory.Calibration, L(ReportLanguages.UiKeys.CalibrationData));
                    AddImageRoot(ReportTreeNodeKind.TestData, ReportImageCategory.TestData, L(ReportLanguages.UiKeys.TestData));
                    AddImageRoot(ReportTreeNodeKind.RegionStatistics, ReportImageCategory.RegionStatistics, L(ReportLanguages.UiKeys.RegionalStatisticsChart));
                    AddImageRoot(ReportTreeNodeKind.PdMap, ReportImageCategory.PdMap, L(ReportLanguages.UiKeys.PdmapChart));
                    AddTrendRoot();
                    location.Checked = true; location.Expand(); foreach (TreeNode child in location.Nodes) child.Expand();
                }
                foreach (TreeNode node in tree.Nodes) ApplyAvailability(node);
                SyncModels(tree.Nodes);
                if (tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0];
            }
            finally { tree.EndUpdate(); changingChecks = false; }
            ShowNode(tree.SelectedNode);
        }

        TreeNode BuildIntervalNode(VoltageInterval interval)
        {
            var model = new ReportTreeNodeModel { Key = "interval_" + interval.Id, Text = IntervalText(interval), Kind = ReportTreeNodeKind.LocationInterval, VoltageInterval = interval, IsChecked = interval.IsAllResults, CanDelete = interval.CanDelete && HasAnalysisData };
            TreeNode node = CreateTreeNode(model); node.Nodes.Add(BuildMetricNode(interval, true)); node.Nodes.Add(BuildMetricNode(interval, false)); return node;
        }

        TreeNode BuildMetricNode(VoltageInterval interval, bool charge)
        {
            var model = new ReportTreeNodeModel { Key = "metric_" + interval.Id + (charge ? "_q" : "_n"), Text = L(charge ? ReportLanguages.UiKeys.ChargeLocation : ReportLanguages.UiKeys.CountLocation), Kind = ReportTreeNodeKind.Item, VoltageInterval = interval, ImageCategory = charge ? ReportImageCategory.LocationChargeDistance : ReportImageCategory.LocationCountDistance, IsChecked = interval.IsAllResults };
            TreeNode node = CreateTreeNode(model);
            foreach (ReportPhase phase in new[] { ReportPhase.All, ReportPhase.A, ReportPhase.B, ReportPhase.C })
            {
                var selection = new LocationSelection { Interval = interval, Phase = phase, ChargeLocation = charge };
                var child = new ReportTreeNodeModel { Key = model.Key + "_" + phase, Text = PhaseText(phase), Kind = ReportTreeNodeKind.LocationPhase, Phase = phase, VoltageInterval = interval, ImageCategory = model.ImageCategory, IsChecked = interval.IsAllResults, Tag = selection };
                node.Nodes.Add(CreateTreeNode(child));
            }
            return node;
        }

        void AddImageRoot(ReportTreeNodeKind kind, ReportImageCategory category, string text)
        {
            var model = new ReportTreeNodeModel { Key = "images_" + category, Text = text, Kind = kind, ImageCategory = category, IsChecked = false };
            TreeNode root = CreateTreeNode(model);
            IEnumerable<ReportImageInfo> images = analysisPackage == null ? new ReportImageInfo[0] : analysisPackage.ReportImages.Where(item => NormalizeCategory(item) == category).OrderBy(item => item.Sequence).ToArray();
            foreach (ReportImageInfo image in images)
            {
                var child = new ReportTreeNodeModel { Key = "image_" + image.Sequence, Text = ReportAttachmentText.Title(image, languageIndex), Kind = ReportTreeNodeKind.AnalysisAttachment, ImageCategory = category, SourcePath = image.ImagePath, IsChecked = false, Tag = image };
                TreeNode node = CreateTreeNode(child); node.ToolTipText = ReportAttachmentText.RegionSummary(image, languageIndex); root.Nodes.Add(node);
            }
            if (waveformProject != null && category == ReportImageCategory.Calibration)
                foreach (BrowserRecord record in waveformProject.Calibrations)
                    AddWaveformLeaf(root, record);
            if (waveformProject != null && category == ReportImageCategory.TestData)
                foreach (string phase in new[] { "A", "B", "C", "ALL" })
                {
                    BrowserRecord[] records = waveformProject.Tests.Where(item => string.Equals(item.Phase, phase, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (records.Length == 0) continue;
                    TreeNode phaseNode = CreateTreeNode(new ReportTreeNodeModel { Key = "test_phase_" + phase, Text = ReportCableStructurePreview.PhaseCaption(languageIndex,phase), Kind = ReportTreeNodeKind.Item, ImageCategory = category, IsChecked = false });
                    foreach (BrowserRecord record in records) AddWaveformLeaf(phaseNode, record);
                    root.Nodes.Add(phaseNode);
                }
            tree.Nodes.Add(root);
        }

        void AddWaveformLeaf(TreeNode root, BrowserRecord record)
        {
            string phaseLabel = ReportCableStructurePreview.PhaseCaption(languageIndex,record.Phase);
            string title = record.Kind == "cal" ? LocalizedWaveformName(record) :
                phaseLabel + "_" + record.VoltageKv.ToString("0.00", CultureInfo.InvariantCulture) + "kV_" + record.Order.ToString(CultureInfo.InvariantCulture);
            var model = new ReportTreeNodeModel { Key = "waveform_" + record.Kind + "_" + record.Name, Text = title,
                Kind = ReportTreeNodeKind.AnalysisAttachment, ImageCategory = record.Kind == "cal" ? ReportImageCategory.Calibration : ReportImageCategory.TestData,
                SourcePath = record.Path, IsChecked = false, Tag = record };
            TreeNode node = CreateTreeNode(model); node.ToolTipText = record.IsValid ? record.Path : record.Warning; root.Nodes.Add(node);
        }

        void AddTrendRoot()
        {
            var rootModel = new ReportTreeNodeModel { Key = "trend_statistics", Text = L(ReportLanguages.UiKeys.TrendStatisticsChart), Kind = ReportTreeNodeKind.TrendStatistics, ImageCategory = ReportImageCategory.TrendStatistics, IsChecked = false };
            TreeNode root = CreateTreeNode(rootModel);
            AddTrendFamily(root, TrendChartKind.CountVoltage, "n-N-U");
            AddTrendFamily(root, TrendChartKind.ChargeVoltage, "n-Q-U");
            AddTrendFamily(root, TrendChartKind.DacTanDelta, "DAC-TD");
            tree.Nodes.Add(root);
        }

        void AddTrendFamily(TreeNode root, TrendChartKind kind, string name)
        {
            var familyModel = new ReportTreeNodeModel { Key = "trend_" + kind, Text = name, Kind = ReportTreeNodeKind.Item, ImageCategory = ReportImageCategory.TrendStatistics, IsChecked = false };
            TreeNode family = CreateTreeNode(familyModel);
            foreach (ReportPhase phase in new[] { ReportPhase.All, ReportPhase.A, ReportPhase.B, ReportPhase.C })
            {
                string title = name + " · " + PhaseText(phase);
                var selection = new TrendSelection { Kind = kind, Phase = phase, Title = title };
                family.Nodes.Add(CreateTreeNode(new ReportTreeNodeModel { Key = "trend_" + kind + "_" + phase, Text = PhaseText(phase), Kind = ReportTreeNodeKind.AnalysisAttachment, ImageCategory = ReportImageCategory.TrendStatistics, Phase = phase, IsChecked = false, Tag = selection }));
            }
            root.Nodes.Add(family);
        }

        static ReportImageCategory NormalizeCategory(ReportImageInfo image)
        {
            if (image.Category != ReportImageCategory.Unknown && image.Category != ReportImageCategory.AnalysisAttachment) return image.Category;
            string kind = (image.ChartKind + " " + image.Title).ToLowerInvariant();
            if (kind.Contains("cal")) return ReportImageCategory.Calibration;
            if (kind.Contains("pdmap") || kind.Contains("prpd")) return ReportImageCategory.PdMap;
            if (kind.Contains("trend") || kind.Contains("dac-td")) return ReportImageCategory.TrendStatistics;
            if (kind.Contains("region")) return ReportImageCategory.RegionStatistics;
            return ReportImageCategory.TestData;
        }

        static TreeNode CreateTreeNode(ReportTreeNodeModel model)
        {
            return new TreeNode(model.Text) { Name = model.Key, Checked = model.IsChecked, Tag = model, ToolTipText = model.Text };
        }

        void OnTreeAfterCheck(object sender, TreeViewEventArgs e)
        {
            if (changingChecks || e.Node == null) return;
            ReportTreeNodeModel model = e.Node.Tag as ReportTreeNodeModel;
            if (e.Node.Checked && !IsNodeAvailable(e.Node))
            {
                changingChecks = true;
                try { e.Node.Checked = false; SetChildrenChecked(e.Node, false); SyncModels(tree.Nodes); }
                finally { changingChecks = false; }
                return;
            }
            if (!HasAnalysisData && (model == null || model.Kind != ReportTreeNodeKind.ProjectInformation))
            {
                changingChecks = true;
                try { e.Node.Checked = false; SetChildrenChecked(e.Node, false); UpdateParentsChecked(e.Node.Parent); SyncModels(tree.Nodes); }
                finally { changingChecks = false; }
                SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false);
                return;
            }
            if (model != null && model.IsCheckLocked && !e.Node.Checked) { changingChecks = true; e.Node.Checked = true; changingChecks = false; return; }
            changingChecks = true;
            try { SetChildrenChecked(e.Node, e.Node.Checked); UpdateParentsChecked(e.Node.Parent); SyncModels(tree.Nodes); }
            finally { changingChecks = false; }
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        static void SetChildrenChecked(TreeNode node, bool value) { foreach (TreeNode child in node.Nodes) { child.Checked = value && IsNodeAvailable(child); SetChildrenChecked(child, child.Checked); } }
        static void UpdateParentsChecked(TreeNode node) { while (node != null) { node.Checked = node.Nodes.Cast<TreeNode>().Any(child => child.Checked); node = node.Parent; } }
        static void SyncModels(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes) { ReportTreeNodeModel model = node.Tag as ReportTreeNodeModel; if (model != null) model.IsChecked = node.Checked; SyncModels(node.Nodes); }
        }

        void OnTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node == null) return;
            if (e.Button == MouseButtons.Left)
            {
                // AfterSelect already previews a changed selection. A second ShowNode
                // here used to render and save the same high-resolution image twice.
                tree.SelectedNode = e.Node;
                return;
            }
            if (e.Button != MouseButtons.Right) return;
            tree.SelectedNode = e.Node; ReportTreeNodeModel model = e.Node.Tag as ReportTreeNodeModel;
            bool root = model != null && model.Kind == ReportTreeNodeKind.LocationResults, interval = model != null && model.Kind == ReportTreeNodeKind.LocationInterval;
            if (!root && !interval) return;
            addRangeMenu.Text = L(ReportLanguages.UiKeys.AddVoltageRange); deleteRangeMenu.Text = L(ReportLanguages.UiKeys.DeleteVoltageRange);
            addRangeMenu.Visible = root; addRangeMenu.Enabled = root && HasAnalysisData; deleteRangeMenu.Visible = interval;
            deleteRangeMenu.Enabled = HasAnalysisData && interval && model.VoltageInterval != null && model.VoltageInterval.CanDelete && !model.VoltageInterval.IsAllResults;
            locationMenu.Show(tree, e.Location);
        }

        void AddRangeFromDialog()
        {
            using (var dialog = new VoltageRangeDialog(languageIndex, null)) if (dialog.ShowDialog(FindForm()) == DialogResult.OK) AddVoltageRange(dialog.ResultInterval.MinimumU0, dialog.ResultInterval.MaximumU0, dialog.ResultInterval.Title);
        }

        void DeleteSelectedRange()
        {
            ReportTreeNodeModel model = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as ReportTreeNodeModel;
            if (model == null || model.VoltageInterval == null || model.VoltageInterval.IsAllResults || !model.VoltageInterval.CanDelete) return;
            if (MessageBox.Show(FindForm(), L(ReportLanguages.UiKeys.ConfirmDeleteRange), L(ReportLanguages.UiKeys.DeleteVoltageRange), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) RemoveVoltageRange(model.VoltageInterval.Id);
        }

        void ShowSettings()
        {
            if (!HasAnalysisData) { SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false); return; }
            using (var dialog = new ReportSettingsDialog(languageIndex, statisticsOptions))
            { if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return; statisticsOptions.Clear(); statisticsOptions.AddRange(dialog.Options.Select(item => item.Clone())); }
        }

        void ClearOptionalSelections()
        {
            if (!HasAnalysisData) { SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false); return; }
            changingChecks = true;
            try
            {
                foreach (TreeNode node in tree.Nodes)
                {
                    ReportTreeNodeModel model = node.Tag as ReportTreeNodeModel;
                    if (model != null && (model.Kind == ReportTreeNodeKind.ProjectInformation || model.Kind == ReportTreeNodeKind.LocationResults)) continue;
                    node.Checked = false; SetChildrenChecked(node, false);
                }
                SyncModels(tree.Nodes);
            }
            finally { changingChecks = false; }
        }

        void ShowNode(TreeNode node)
        {
            if (node == null || changingChecks || IsDisposed || Disposing) return;
            ReportTreeNodeModel model = node.Tag as ReportTreeNodeModel; contentHeading.Text = node.Text;
            if (!IsNodeAvailable(node)) { ShowPlaceholder(node.Text, UnavailableSelectionText); return; }
            activePreviewKey = node.Name;
            string cached;
            if (previewCache.TryGetValue(activePreviewKey, out cached))
            {
                if (File.Exists(cached)) { ShowImageFile(cached, node.Text); return; }
                previewCache.Remove(activePreviewKey);
            }
            PrunePreviewFiles();
            if (model == null) { ShowText(L(ReportLanguages.UiKeys.NoData)); return; }
            ReportImageInfo reportImage = model.Tag as ReportImageInfo; if (reportImage != null) { ShowReportImage(reportImage); return; }
            BrowserRecord waveform = model.Tag as BrowserRecord; if (waveform != null) { ShowWaveformPreview(waveform); return; }
            TrendSelection trend = model.Tag as TrendSelection; if (trend != null) { ShowTrendPreview(trend); return; }
            LocationSelection selection = model.Tag as LocationSelection; if (selection != null) { ShowLocationPreview(selection); return; }
            if (model.Kind == ReportTreeNodeKind.ProjectInformation) { ShowProjectInformation(); return; }
            if (model.Kind == ReportTreeNodeKind.LocationInterval) { ShowIntervalStatistics(model.VoltageInterval); return; }
            if (model.Kind == ReportTreeNodeKind.LocationResults)
            {
                if (!HasAnalysisData) { ShowText(L(ReportLanguages.UiKeys.AnalysisRequired)); return; }
                ShowText(ReportLanguages.Ui(languageIndex, "定位结果已默认全选。右键“定位结果”可增加0–2U0内的任意闭区间；右键区间可删除，所有定位结果不可删除。", "All location results are selected by default. Right-click Location results to add any closed interval within 0–2U0; right-click an interval to remove it. All results is protected."));
                return;
            }
            ShowText(ReportLanguages.Ui(languageIndex, "请选择下一级项目查看图表预览；勾选需要纳入报告的内容。", "Select a child item to preview its chart, and check the content to include in the report."));
        }

        void ShowTrendPreview(TrendSelection selection)
        {
            string path = CreatePreviewTempPath("trend-preview.png");
            try { TrendStatisticsChartRenderer.Render(locationMatrix, projectPath, selection, languageIndex, path); ShowImageFile(path, selection.Title); }
            catch (Exception ex) { ShowPlaceholder(selection.Title, ex.Message); }
            finally { PrunePreviewFiles(); }
        }

        void ShowProjectInformation()
        {
            SetProjectInformationPreview(true);
            previewImage.Visible = false; previewText.Visible = false; previewGrid.Visible = true; previewGrid.BringToFront(); previewGrid.Rows.Clear();
            previewGrid.Columns[0].HeaderText = ReportLanguages.Ui(languageIndex, "字段", "Field"); previewGrid.Columns[1].HeaderText = ReportLanguages.Ui(languageIndex, "值", "Value");
            if (projectData == null) { previewGrid.Rows.Add(L(ReportLanguages.UiKeys.NoData), string.Empty); return; }
            AddInfoRow(ReportLanguages.Ui(languageIndex, "项目", "Project"), AnalysisDateText.DisplayProjectName(projectData.ProjectName));
            AddInfoRow(ReportLanguages.Ui(languageIndex, "检测单位", "Inspection organization"), projectData.InspectionOrganization);
            AddInfoRow(ReportLanguages.Ui(languageIndex, "测试人员", "Test personnel"), projectData.TestPersonnel);
            AddInfoRow(ReportLanguages.Ui(languageIndex, "测试地点", "Test location"), projectData.TestLocation);
            AddInfoRow(ReportLanguages.Ui(languageIndex, "始端 / 终端", "Start / End"), projectData.StartName + " / " + projectData.EndName);
            AddInfoRow(ReportLanguages.Ui(languageIndex, "电缆长度", "Cable length"), FormatNumber(projectData.CableLengthM) + " m");
            AddInfoRow(ReportLanguages.Ui(languageIndex, "相电压", "Phase voltage"), FormatNumber(projectData.DeviceU0Kv) + " kV");
            AddInfoRow(ReportLanguages.Ui(languageIndex, "加压模式", "Voltage mode"), projectData.VoltageMode);
            AddInfoRow(ReportLanguages.Ui(languageIndex, "新旧电缆", "Cable age"), CableAgeText());
            AddInfoRow(ReportLanguages.Ui(languageIndex, "接头", "Joints"), projectData.JointSummary);
            previewDescription.Text = L(ReportLanguages.UiKeys.ReportOutputFolder);
            try { projectData.RequireCurrentMetadata(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is InvalidDataException)
            {
                string notice = ReportLanguages.Ui(languageIndex, "项目资料已变化：当前图表保留加载时快照，请重新加载并核对后生成报告。", "Project metadata changed: the table and diagram retain the loaded snapshot. Reload and review before generating a report.");
                AddInfoRow(ReportLanguages.Ui(languageIndex, "需要重新加载", "Reload required"), notice);
                previewDescription.Text = notice;
            }
        }

        void AddInfoRow(string field, string value) { previewGrid.Rows.Add(field, string.IsNullOrWhiteSpace(value) ? ProjectReportData.Missing : value); }

        void ShowIntervalStatistics(VoltageInterval interval)
        {
            SetProjectInformationPreview(false);
            previewImage.Visible = false; previewText.Visible = false; previewGrid.Visible = true; previewGrid.BringToFront(); previewGrid.Rows.Clear();
            previewGrid.Columns[0].HeaderText = ReportLanguages.Ui(languageIndex, "相别 / 指标", "Phase / Metric"); previewGrid.Columns[1].HeaderText = interval == null ? string.Empty : IntervalText(interval);
            if (interval == null || locationMatrix.GetLength(0) == 0 || projectData == null) { previewGrid.Rows.Add(L(ReportLanguages.UiKeys.NoData), string.Empty); return; }
            ThreePhaseTestMetrics metrics = ReportStatistics.Compute(locationMatrix, projectData.DeviceU0Kv, interval, LocationMatrixColumnMap.UnifiedLocationMatrix());
            foreach (PhaseTestMetrics phase in metrics.ToArray())
            {
                string prefix = PhaseText(phase.Phase) + " · ";
                previewGrid.Rows.Add(prefix + (ReportLanguages.Ui(languageIndex, "最大放电量", "Maximum charge")), FormatCharge(phase.MaximumChargePc));
                previewGrid.Rows.Add(prefix + (ReportLanguages.Ui(languageIndex, "平均放电量", "Average charge")), FormatCharge(phase.AverageChargePc));
                previewGrid.Rows.Add(prefix + (ReportLanguages.Ui(languageIndex, "放电次数", "Discharge count")), phase.Count.ToString(CultureInfo.CurrentCulture));
            }
            previewDescription.Text = metrics.Status;
        }

        void ShowLocationPreview(LocationSelection selection)
        {
            string title = IntervalText(selection.Interval) + " · " + PhaseText(selection.Phase) + " · " + (selection.ChargeLocation ? "Q-L" : "N-L");
            if (locationMatrix.GetLength(0) == 0 || projectData == null) { ShowPlaceholder(title, L(ReportLanguages.UiKeys.NoData)); return; }
            string path = CreatePreviewTempPath("location-preview.png");
            try
            {
                if (selection.ChargeLocation) LocationChartRenderer.RenderChargeLocation(locationMatrix, selection.Interval, selection.Phase, projectData.CableLengthM, projectData.DeviceU0Kv, languageIndex, title, path);
                else LocationChartRenderer.RenderCountLocation(locationMatrix, selection.Interval, selection.Phase, projectData.CableLengthM, projectData.DeviceU0Kv, languageIndex, title, path);
                ShowImageFile(path, title);
            }
            catch (Exception ex) { ShowPlaceholder(title, ex.Message); }
            finally { PrunePreviewFiles(); }
        }

        void ShowReportImage(ReportImageInfo image)
        {
            ReportImageInfo localized = ReportAttachmentText.ForRendering(image, languageIndex);
            try
            {
                string region = TryRenderRegion(localized, CreatePreviewTempPath("region-preview.png"));
                if (region.Length > 0) { ShowImageFile(region, localized.Title); return; }
                string pdmap = TryRenderPdmap(localized, CreatePreviewTempPath("pdmap-preview.png"));
                if (pdmap.Length > 0) { ShowImageFile(pdmap, localized.Title); return; }
                string highResolution = string.Empty;
                if (!string.IsNullOrWhiteSpace(image.ImagePath) &&
                    (image.Title ?? string.Empty).IndexOf("PRPD", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    File.Exists(Path.ChangeExtension(image.ImagePath, ".csv")))
                    highResolution = TryRenderPrpd(localized, CreatePreviewTempPath("prpd-preview.png"));
                if (highResolution.Length > 0) { ShowImageFile(highResolution, localized.Title); return; }
                if (languageIndex == 0)
                {
                    if (!string.IsNullOrWhiteSpace(image.ImagePath) && File.Exists(image.ImagePath)) { ShowImageFile(image.ImagePath, localized.Title); return; }
                    if (image.HasPngData) { using (var stream = new MemoryStream(image.PngData)) using (Image source = Image.FromStream(stream)) ShowImage((Image)source.Clone(), localized.Title); return; }
                }
            }
            catch (Exception ex) { ShowText(ex.Message); return; }
            finally { PrunePreviewFiles(); }
            ShowPlaceholder(localized.Title, L(ReportLanguages.UiKeys.AttachmentUnavailable));
        }

        string TryRenderPrpd(ReportImageInfo image, string outputPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(image.ImagePath) ||
                (image.Title ?? string.Empty).IndexOf("PRPD", StringComparison.OrdinalIgnoreCase) < 0) return string.Empty;
            string csvPath = Path.ChangeExtension(image.ImagePath, ".csv");
            try { return PrpdChartRenderer.TryRender(csvPath, image.Title, languageIndex, outputPath) ? outputPath : string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        string TryRenderPdmap(ReportImageInfo image, string outputPath)
        {
            try { return PdmapChartRenderer.TryRender(image, projectData, languageIndex, outputPath) ? outputPath : string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        string TryRenderRegion(ReportImageInfo image, string outputPath)
        {
            try { return RegionStatisticsChartRenderer.TryRender(image, projectData, projectPath, languageIndex, outputPath) ? outputPath : string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        void ShowImageFile(string path, string description)
        {
            // A detached bitmap releases the decoder's file handle immediately.
            using (Image source = Image.FromFile(path)) ShowImage(new Bitmap(source), description);
            if (activePreviewKey.Length == 0 || !previewTemporaryFiles.Contains(path) || previewCache.ContainsKey(activePreviewKey)) return;
            previewCache[activePreviewKey] = path;
            previewCacheOrder.Enqueue(activePreviewKey);
            while (previewCacheOrder.Count > PreviewCacheLimit)
                previewCache.Remove(previewCacheOrder.Dequeue());
            PrunePreviewFiles();
        }
        void ShowWaveformPreview(BrowserRecord record)
        {
            if (!record.IsValid) { ShowText(record.Warning); return; }
            string path = CreatePreviewTempPath("waveform-preview.png");
            try { ProjectWaveformChartRenderer.Render(record, languageIndex, path); ShowImageFile(path, LocalizedWaveformName(record)); }
            catch (Exception ex) { ShowText(ex.Message); }
            finally { PrunePreviewFiles(); }
        }
        void ShowImage(Image image, string description)
        {
            Image previous = previewImage.Image; previewImage.Image = image; if (previous != null) previous.Dispose();
            SetProjectInformationPreview(false);
            previewGrid.Visible = false; previewText.Visible = false; previewImage.Visible = true; previewImage.BringToFront(); previewDescription.Text = description ?? string.Empty;
        }
        void ShowPlaceholder(string title, string detail) { ShowImage(ReportTheme.BuildPlaceholder(new Size(900, 470), title, detail), detail); }
        void ShowText(string text) { SetProjectInformationPreview(false); previewGrid.Visible = false; previewImage.Visible = false; previewText.Visible = true; previewText.BringToFront(); previewText.Text = text ?? string.Empty; previewDescription.Text = string.Empty; }

        void RefreshConclusion()
        {
            if (!HasAnalysisData)
            {
                originalGeneratedConclusion = string.Empty;
                SetConclusionText(L(ReportLanguages.UiKeys.AnalysisRequired));
                recommendation.Text = string.Empty; recommendation.Visible = false;
                return;
            }
            if(preferSavedConclusionSnapshot&&analysisPackage!=null&&analysisPackage.HasSavedConclusionSnapshot &&
                (!analysisPackage.UsesEvidenceV3 || (briefConclusion.Checked && ReportLanguages.IsChinese(languageIndex)==ReportAttachmentText.ContainsHan(analysisPackage.ConclusionText))))
            {
                string saved=analysisPackage.ConclusionText;
                if(!analysisPackage.UsesEvidenceV3)saved=ReportLanguages.Ui(languageIndex,"历史版本已保存结论；如需更新，请在数据分析重新生成并保存：","Saved conclusion from an earlier version. To update it, regenerate and save in Data analysis:")+Environment.NewLine+saved;
                if(!ReportLanguages.IsChinese(languageIndex)&&ReportAttachmentText.ContainsHan(saved))saved=L(ReportLanguages.UiKeys.ArchivedConclusionUnavailable);
                originalGeneratedConclusion=ConclusionService.FormatForReport(saved,conclusionSystemName,ReportLanguages.IsChinese(languageIndex));
                SetConclusionText(originalGeneratedConclusion);UpdateRecommendation();return;
            }
            string text = string.Empty;
            if(!conclusionGenerationRequested)
            {
                originalGeneratedConclusion=String.Empty;
                SetConclusionText(ReportLanguages.Ui(languageIndex,"已加载保存的分析范围，请点击“生成结论”统一评估。","The saved analysis scope is loaded. Select Generate conclusion to assess it."));
                recommendation.Text=String.Empty;recommendation.Visible=false;return;
            }
            if (locationMatrix.GetLength(0) > 0 && projectData != null)
            {
                conclusionService.RuntimeDllPath = task15RuntimePath; var generatedParts = new List<string>(); string generated;
                double serviceYears = projectData.OlderThanOneYear ? 2.0 : 0.5;
                double[,] input;double[][] joints;double u0;
                if(!TryGetConclusionInput(out input,out joints,out u0))
                {
                    SetConclusionText(ReportLanguages.Ui(languageIndex,"此分析缺少可验证的报告输入范围或接头近似参数。请在数据分析中重新打开并保存最终分析，再回到报告生成结论；未使用未筛选数据或猜测参数回退。","This analysis has no verifiable report input scope or joint-approximation settings. Reopen and save the final analysis in Data analysis, then generate the conclusion here; no unfiltered data or guessed settings were used."));
                    originalGeneratedConclusion=conclusion.Text;UpdateRecommendation();return;
                }
                if (briefConclusion.Checked && TryRenderConclusion(input, joints, u0, serviceYears, 1, out generated))
                    generatedParts.Add(professionalConclusion.Checked ? ((ReportLanguages.Ui(languageIndex, "简洁版", "Brief")) + Environment.NewLine + generated) : generated);
                if (professionalConclusion.Checked && TryRenderConclusion(input, joints, u0, serviceYears, 2, out generated))
                    generatedParts.Add(briefConclusion.Checked ? ((ReportLanguages.Ui(languageIndex, "专业版", "Professional")) + Environment.NewLine + generated) : generated);
                text = string.Join(Environment.NewLine + Environment.NewLine, generatedParts.ToArray());
                if(analysisPackage!=null&&analysisPackage.PendingSelectedRecordCount>0&&!String.IsNullOrWhiteSpace(text))
                    text=String.Format(ReportLanguages.Ui(languageIndex,"本结论仅覆盖已保存的有效分析范围；另有 {0} 条已选记录尚未分析，不代表全项目分析完成。","This conclusion covers only the saved valid analysis scope; {0} selected records remain unanalyzed. It is not a complete-project finding."),analysisPackage.PendingSelectedRecordCount)+Environment.NewLine+text;
                if (string.IsNullOrWhiteSpace(text)) SetStatus(conclusionService.LastError, true);
            }
            if (!ReportLanguages.IsChinese(languageIndex) && ReportAttachmentText.ContainsHan(text)) text = L(ReportLanguages.UiKeys.ArchivedConclusionUnavailable);
            if (string.IsNullOrWhiteSpace(text)) text = ReportLanguages.Ui(languageIndex, "尚未生成结论。加载分析后可点击“生成结论”调用最新结论算法。", "No conclusion is available. Load an analysis and select Generate conclusion to run the latest conclusion algorithm.");
            originalGeneratedConclusion = ConclusionService.FormatForReport(text, conclusionSystemName, ReportLanguages.IsChinese(languageIndex));
            SetConclusionText(originalGeneratedConclusion); UpdateRecommendation();
        }

        bool TryRenderConclusion(double[,] input, double[][] joints, double u0, double years, int audience, out string text)
        {
            bool english = !ReportLanguages.IsChinese(languageIndex);
            bool saved = analysisPackage != null && !analysisPackage.HasReportInputSnapshot && analysisPackage.HasSavedConclusionSnapshot && analysisPackage.UsesEvidenceV3;
            if (!ReferenceEquals(renderedConclusionPackage, analysisPackage))
            {
                savedConclusionPresentations.Clear();
                renderedConclusionPackage = analysisPackage;
            }
            int key = audience + (english ? 10 : 0);
            if (saved && savedConclusionPresentations.TryGetValue(key, out text)) return true;
            bool ok = saved ? conclusionService.TryRenderSavedPresentation(analysisPackage,english,audience,out text)
                : conclusionService.TryGenerate(input, joints[0], joints[1], joints[2], u0, years, english, audience, out text);
            if (ok && saved) savedConclusionPresentations[key] = text;
            if (!ok && saved)
            {
                SetStatus(conclusionService.LastError,true);
                text=analysisPackage.ConclusionText;
                return true; // Preserve saved evidence, never substitute a new grade.
            }
            return ok;
        }

        bool TryGetConclusionInput(out double[,] input,out double[][] joints,out double u0)
        {
            input=null;joints=null;u0=0;
            if(analysisPackage!=null)
            {
                if(!analysisPackage.HasReportInputSnapshot||analysisPackage.ConclusionInput==null||analysisPackage.ConclusionJoints==null)return false;
                input=analysisPackage.ConclusionInput;joints=analysisPackage.ConclusionJoints;u0=analysisPackage.ConclusionOperatingVoltage;return true;
            }
            try
            {
                // Direct-matrix API: use explicit project phase structures, never a union
                // of unrelated joints or an inferred operating voltage.
                var cable=HVX.ProjectCables.CableJointStore.Read(projectPath);joints=new double[3][];
                for(int p=0;p<3;p++)
                {
                    string phase=new[]{"A","B","C"}[p];
                    var blocks=cable.Blocks.Where(b=>cable.Mode==HVX.ProjectCables.CableJointMode.SharedThreeCore||b.Phase==phase||b.Phase=="ALL").ToArray();
                    if(blocks.Length>1&&blocks.Skip(1).Any(b=>!b.Positions.SequenceEqual(blocks[0].Positions)))return false;
                    joints[p]=blocks.Length==0?new double[0]:blocks[0].Positions.ToArray();
                }
                input=locationMatrix;u0=projectData.DeviceU0Kv;return true;
            }
            catch(Exception ex){if(ex is OutOfMemoryException||ex is StackOverflowException)throw;SetStatus(ex.Message,true);return false;}
        }

        void SetConclusionText(string value)
        {
            conclusion.Text = WithAcquisitionRiskWarning(value);
            // RichTextBox may not raise TextChanged before its HWND is created.
            conclusionSnapshot = conclusion.Text;
        }

        void UpdateRecommendation()
        {
            recommendation.Text = conclusionService.GetIeeeRecommendation(languageIndex, conclusion.Text); recommendation.Visible = !string.IsNullOrWhiteSpace(recommendation.Text);
        }

        ReportGenerationPlan BuildGenerationPlan(string docx, string pdf, bool overwrite)
        {
            if (projectData == null) projectData = ProjectReportDataReader.Read(projectPath);
            projectData.RequireCurrentMetadata();
            projectData.RefreshValues();
            var plan = new ReportGenerationPlan { ProjectPath = projectPath, TemplatePath = templatePath, OutputDocxPath = docx, OutputPdfPath = pdf, Overwrite = overwrite, EngineDllPath = reportEnginePath, LocationMatrix = locationMatrix.GetLength(0) == 0 ? null : (double[,])locationMatrix.Clone() };
            foreach (KeyValuePair<string, string> pair in projectData.Values) plan.Values[pair.Key] = pair.Value;
            ApplyReportValues(plan.Values); AddCableTopology(plan); AddStatisticsRows(plan); AddSelectedLocationSections(plan); AddSelectedAttachmentSections(plan); return plan;
        }

        void AddCableTopology(ReportGenerationPlan plan)
        {
            string temp = CreateGenerationTempDirectory(); string path = Path.Combine(temp, "cable_topology.png");
            CableTopologyRenderer.Render(projectData, languageIndex, path); plan.TemporaryFiles.Add(path);
            plan.Images["cable_topology"] = new Dictionary<string, object> { { "path", path }, { "max_width_in", 6.7 }, { "max_height_in", 2.3 }, { "center", true }, { "caption_from_filename", false } };
        }

        void ApplyReportValues(Dictionary<string, string> values)
        {
            values["report.title"] = L(ReportLanguages.UiKeys.ReportTitle);
            values["report.header"] = ReportLanguages.Ui(languageIndex, "振荡波电缆检测", "OWTS cable diagnostics");
            values["report.language_name"] = ReportLanguages.Names[languageIndex] + "  ·  "; values["report.date"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            values["report.generated_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            values["project.short_title"] = BuildReportHeading();
            values["project.name"] = ReportProjectName(); values["project.start_name"] = projectData.StartName; values["project.end_name"] = projectData.EndName;
            values["project.test_date"] = ResolveTestDate(values); values["project.test_organization"] = projectData.InspectionOrganization; values["project.test_personnel"] = projectData.TestPersonnel;
            values["project.test_location"] = projectData.TestLocation; values["project.phase_voltage"] = FormatNumber(projectData.DeviceU0Kv) + " kV";
            values["project.voltage_mode"] = projectData.VoltageMode; values["project.cable_age_class"] = CableAgeText();
            values["cable.length"] = FormatNumber(projectData.CableLengthM) + " m"; values["cable.model"] = projectData.CableModel;
            values["cable.insulation_material"] = projectData.CableInsulation; values["cable.accessory_model"] = projectData.AccessorySummary;
            values["cable.joint_count"] = projectData.JointPositions.Length.ToString(CultureInfo.CurrentCulture); values["cable.accessories"] = projectData.AccessorySummary;
            values["cable.topology_note"] = projectData.JointSummary;
            for (int i = 0; i < 3; i++) values["cable.joint_" + (i + 1)] = projectData.JointPositions.Length > i ? FormatNumber(projectData.JointPositions[i]) + " m" : string.Empty;
            foreach (ReportPhase phase in new[] { ReportPhase.A, ReportPhase.B, ReportPhase.C })
            {
                ReportPhaseSummary summary = projectData.ForPhase(phase); string prefix = "phase." + phase.ToString().ToLowerInvariant() + ".";
                values[prefix + "noise"] = FormatNumber(summary.NoisePc); values[prefix + "pdiv"] = FormatVoltagePair(summary.PDIV); values[prefix + "pdev"] = FormatVoltagePair(summary.PDEV);
                values[prefix + "frequency"] = FormatNumber(summary.FrequencyHz); values[prefix + "capacitance"] = FormatNumber(summary.CapacitanceUf);
                values[prefix + "tan_delta"] = FormatNumber(summary.DielectricLossPercent); values[prefix + "insulation_before"] = summary.InsulationBeforeMohm;
                values[prefix + "insulation_after"] = summary.InsulationAfterMohm;
            }
            string localizedConclusion = !ReportLanguages.IsChinese(languageIndex) && ReportAttachmentText.ContainsHan(conclusion.Text)
                ? L(ReportLanguages.UiKeys.ArchivedConclusionUnavailable) : conclusion.Text;
            bool candidateLocations=Enumerable.Range(0,locationMatrix.GetLength(0)).Any(r=>locationMatrix.GetLength(1)>26&&(locationMatrix[r,26]==5||locationMatrix[r,26]==7));
            if(candidateLocations||(analysisPackage!=null&&!String.IsNullOrEmpty(analysisPackage.AlgorithmDiagnosticsJson)))
                localizedConclusion+=Environment.NewLine+ReportLanguages.Ui(languageIndex,
                    "定位复核说明：自动定位结果需结合原始波形确认。质量分数不代表定位准确率，纳入统计不等于已人工确认。历史结果缺少来源依据时仍标为未核验；人工计时评分仅供参考。",
                    "Location review: verify automatic locations against the original waveforms. Quality scores are not positioning accuracy, and statistical inclusion does not mean operator confirmation. Historical results without source evidence remain unverified; manual timing scores are for reference only.");
            values["report.ieee_recommendation"] = conclusionService.GetIeeeRecommendation(languageIndex, localizedConclusion);
            string fixtureHistory=HVX.ProjectEvidence.PressureFixtureJournal.Summary(projectPath,ReportLanguages.IsChinese(languageIndex));
            if(!String.IsNullOrWhiteSpace(fixtureHistory))localizedConclusion+=Environment.NewLine+fixtureHistory;
            values["report.conclusion"] = WithAcquisitionRiskWarning(localizedConclusion);
            values["report.standard_basis"] = ReportLanguages.Ui(languageIndex, "说明：结论与建议用于辅助状态评估，最终处置应由具备资质的专业人员结合现场、历史和适用标准复核。", "Note: conclusions and recommendations support condition assessment; a qualified professional shall review the final action against site evidence, history and applicable standards.");
            values["report.location_intro"] = ReportLanguages.Ui(languageIndex, "以下内容按照勾选的电压区间、Q–L/N–L指标和ALL/A/B/C相别生成。", "The following results are generated from the checked voltage ranges, Q–L/N–L metrics and ALL/L1/L2/L3 phases.");
            values["report.appendix_intro"] = ReportLanguages.Ui(languageIndex, "以下附图来自本次分析中勾选的校准、测试、区域统计、PDmap与趋势图。", "The following figures are the checked calibration, test, region, PDmap and trend charts from this analysis.");
            ApplyReportLabels(values);
            if (locationMatrix.GetLength(0) > 0 && projectData.DeviceU0Kv > 0.0)
            {
                ThreePhaseTestMetrics metrics = ReportStatistics.Compute(locationMatrix, projectData.DeviceU0Kv, new VoltageInterval("operating", "U0", 0.95, 1.05, false, false));
                foreach (ReportPhase phase in new[] { ReportPhase.A, ReportPhase.B, ReportPhase.C })
                {
                    PhaseTestMetrics item = metrics.ForPhase(phase); string prefix = "phase." + phase.ToString().ToLowerInvariant() + ".";
                    values[prefix + "u0_max"] = FormatCharge(item.MaximumChargePc, false); values[prefix + "u0_average"] = FormatCharge(item.AverageChargePc, false); values[prefix + "u0_count"] = item.Count.ToString(CultureInfo.CurrentCulture);
                }
            }
        }

        void ApplyReportLabels(Dictionary<string, string> values)
        {
            bool zh = languageIndex == 0;
            values["label.generated_by"] = L(ReportLanguages.UiKeys.GeneratedBy);
            values["label.project_information"] = L(ReportLanguages.UiKeys.ProjectInformation); values["label.cable_information"] = L(ReportLanguages.UiKeys.CableInformation);
            values["label.test_information"] = L(ReportLanguages.UiKeys.TestInformation); values["label.location_results"] = L(ReportLanguages.UiKeys.LocationResults);
            values["label.conclusion_and_recommendation"] = L(ReportLanguages.UiKeys.ConclusionAndRecommendation); values["label.appendix_figures"] = L(ReportLanguages.UiKeys.AppendixFigures);
            values["label.project_name"] = ReportLanguages.Ui(languageIndex, "项目名称", "Project name"); values["label.test_date"] = ReportLanguages.Ui(languageIndex, "测试日期", "Test date");
            values["label.test_organization"] = ReportLanguages.Ui(languageIndex, "检测单位", "Inspection organization"); values["label.test_personnel"] = ReportLanguages.Ui(languageIndex, "测试人员", "Test personnel");
            values["label.test_location"] = ReportLanguages.Ui(languageIndex, "测试地点", "Test location"); values["label.phase_voltage"] = ReportLanguages.Ui(languageIndex, "相电压", "Phase voltage");
            values["label.start_terminal"] = ReportLanguages.Ui(languageIndex, "始端", "Start terminal"); values["label.end_terminal"] = ReportLanguages.Ui(languageIndex, "终端", "End terminal");
            values["label.voltage_mode"] = ReportLanguages.Ui(languageIndex, "加压模式", "Voltage mode"); values["label.cable_age_class"] = ReportLanguages.Ui(languageIndex, "新旧电缆", "Cable age class");
            values["label.cable_length"] = ReportLanguages.Ui(languageIndex, "电缆长度", "Cable length"); values["label.cable_model"] = ReportLanguages.Ui(languageIndex, "电缆型号", "Cable model");
            values["label.insulation_material"] = ReportLanguages.Ui(languageIndex, "绝缘材料", "Insulation material"); values["label.accessory_model"] = ReportLanguages.Ui(languageIndex, "附件型号", "Accessory model");
            values["label.joint_count"] = ReportLanguages.Ui(languageIndex, "接头数量", "Joint count"); values["label.accessories"] = ReportLanguages.Ui(languageIndex, "附件", "Accessories");
            values["label.cable_topology"] = ReportLanguages.Ui(languageIndex, "电缆结构示意", "Cable topology"); values["label.test_summary_note"] = ReportLanguages.Ui(languageIndex, "A/B/C三相测试数据与运行电压下放电统计", "L1/L2/L3 phase test data and discharge statistics at operating voltage");
            values["label.metric"] = ReportLanguages.Ui(languageIndex, "指标", "Metric"); values["label.unit"] = ReportLanguages.Ui(languageIndex, "单位", "Unit"); values["label.noise"] = L(ReportLanguages.UiKeys.Noise);
            values["label.phase_l1"] = ReportLanguages.Ui(languageIndex, "A", "L1"); values["label.phase_l2"] = ReportLanguages.Ui(languageIndex, "B", "L2"); values["label.phase_l3"] = ReportLanguages.Ui(languageIndex, "C", "L3");
            values["label.u0_max"] = ReportLanguages.Ui(languageIndex, "运行电压最大放电量", "Maximum charge at U0"); values["label.u0_average"] = ReportLanguages.Ui(languageIndex, "运行电压平均放电量", "Average charge at U0");
            values["label.u0_count"] = ReportLanguages.Ui(languageIndex, "运行电压放电次数", "Discharge count at U0"); values["label.frequency"] = L(ReportLanguages.UiKeys.Frequency);
            values["label.capacitance"] = L(ReportLanguages.UiKeys.Capacitance); values["label.dielectric_loss"] = L(ReportLanguages.UiKeys.DielectricLoss);
            values["label.insulation_before"] = L(ReportLanguages.UiKeys.InsulationBeforeTest); values["label.insulation_after"] = L(ReportLanguages.UiKeys.InsulationAfterTest);
            values["label.additional_u0_statistics"] = ReportLanguages.Ui(languageIndex, "附加U0区间统计", "Additional U0-range statistics"); values["label.u0_range"] = ReportLanguages.Ui(languageIndex, "U0区间", "U0 range");
            values["label.phase"] = L(ReportLanguages.UiKeys.Phase); values["label.maximum"] = L(ReportLanguages.UiKeys.MaximumCharge); values["label.average"] = L(ReportLanguages.UiKeys.AverageCharge);
            values["label.count"] = L(ReportLanguages.UiKeys.DischargeCount); values["label.ai_conclusion"] = ReportLanguages.Ui(languageIndex, "结论", "Conclusion");
            values["label.ieee_recommendation"] = ReportLanguages.Ui(languageIndex, "工程建议", "IEEE-oriented engineering recommendation");
        }

        void AddStatisticsRows(ReportGenerationPlan plan)
        {
            if (locationMatrix.GetLength(0) > 0 && projectData != null)
            {
                foreach (StatisticsRangeOption option in statisticsOptions.Where(item => item.Include))
                {
                    ThreePhaseTestMetrics metrics = ReportStatistics.Compute(locationMatrix, projectData.DeviceU0Kv, option.Interval);
                    foreach (PhaseTestMetrics phase in metrics.ToArray())
                        plan.U0StatisticsRows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                            { "range", option.Interval.Title }, { "phase", PhaseText(phase.Phase) },
                            { "maximum", option.Maximum ? FormatCharge(phase.MaximumChargePc, false) : ProjectReportData.Missing },
                            { "average", option.Average ? FormatCharge(phase.AverageChargePc, false) : ProjectReportData.Missing },
                            { "count", option.Count ? phase.Count.ToString(CultureInfo.CurrentCulture) : ProjectReportData.Missing }
                        });
                }
            }
            if (plan.U0StatisticsRows.Count == 0)
                plan.U0StatisticsRows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    { "range", ReportLanguages.Ui(languageIndex, "未设置附加区间", "No additional range configured") },
                    { "phase", ProjectReportData.Missing }, { "maximum", ProjectReportData.Missing },
                    { "average", ProjectReportData.Missing }, { "count", ProjectReportData.Missing }
                });
        }

        void AddSelectedLocationSections(ReportGenerationPlan plan)
        {
            if (locationMatrix.GetLength(0) == 0 || projectData == null) return;
            string temp = CreateGenerationTempDirectory(); int index = 0;
            TreeNode locationRoot = tree.Nodes.Cast<TreeNode>().FirstOrDefault(node => ((ReportTreeNodeModel)node.Tag).Kind == ReportTreeNodeKind.LocationResults);
            if (locationRoot == null) return;
            foreach (TreeNode leaf in Descendants(locationRoot).Where(node => node.Nodes.Count == 0 && node.Checked))
            {
                ReportTreeNodeModel model = leaf.Tag as ReportTreeNodeModel; LocationSelection selection = model == null ? null : model.Tag as LocationSelection; if (selection == null || !IsSelectionAvailable(model)) continue;
                string title = IntervalText(selection.Interval) + " · " + PhaseText(selection.Phase) + " · " + (selection.ChargeLocation ? "Q-L" : "N-L");
                string path = Path.Combine(temp, "location_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png");
                if (selection.ChargeLocation) LocationChartRenderer.RenderChargeLocation(locationMatrix, selection.Interval, selection.Phase, projectData, languageIndex, title, path);
                else LocationChartRenderer.RenderCountLocation(locationMatrix, selection.Interval, selection.Phase, projectData, languageIndex, title, path);
                plan.TemporaryFiles.Add(path);
                plan.Sections.Add(new ReportDynamicSection { Anchor = "location_sections", Title = title, Subtitle = selection.ChargeLocation ? (ReportLanguages.Ui(languageIndex, "放电量—距离", "Charge–distance")) : (ReportLanguages.Ui(languageIndex, "放电次数—距离", "Count–distance")), ImagePath = path, Caption = title, PageBreakBefore = index > 0 });
                index++;
            }
        }

        string LocalizedTrendSubtitle(TrendChartKind kind)
        {
            string identifier;
            switch (kind)
            {
                case TrendChartKind.CountVoltage: identifier = "n-N-U"; break;
                case TrendChartKind.ChargeVoltage: identifier = "n-Q-U"; break;
                case TrendChartKind.DacTanDelta: identifier = "DAC-TD"; break;
                default: throw new InvalidDataException("Unknown report trend kind.");
            }
            return L(ReportLanguages.UiKeys.TrendStatisticsChart) + " · " + identifier;
        }

        void AddSelectedAttachmentSections(ReportGenerationPlan plan)
        {
            string temp = string.Empty; int index = 0; int unavailable = 0;
            foreach (TreeNode root in tree.Nodes)
            {
                ReportTreeNodeModel rootModel = root.Tag as ReportTreeNodeModel;
                if (rootModel == null || rootModel.Kind == ReportTreeNodeKind.LocationResults || rootModel.Kind == ReportTreeNodeKind.ProjectInformation) continue;
                foreach (TreeNode leaf in Descendants(root).Where(node => node.Nodes.Count == 0 && node.Checked))
                {
                    ReportTreeNodeModel model = leaf.Tag as ReportTreeNodeModel;
                    if (!IsSelectionAvailable(model)) { unavailable++; continue; }
                    TrendSelection trend = model == null ? null : model.Tag as TrendSelection;
                    if (trend != null)
                    {
                        if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        string trendPath = Path.Combine(temp, "trend_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png");
                        TrendStatisticsChartRenderer.Render(locationMatrix, projectPath, trend, languageIndex, trendPath); plan.TemporaryFiles.Add(trendPath);
                        plan.Sections.Add(new ReportDynamicSection { Anchor = "attachment_sections", Title = trend.Title, Subtitle = LocalizedTrendSubtitle(trend.Kind), ImagePath = trendPath, Caption = trend.Title, PageBreakBefore = index > 0 }); index++; continue;
                    }
                    BrowserRecord waveform = model == null ? null : model.Tag as BrowserRecord;
                    if (waveform != null)
                    {
                        if (!waveform.IsValid) continue;
                        if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        string waveformPath = Path.Combine(temp, "waveform_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png");
                        ProjectWaveformChartRenderer.Render(waveform, languageIndex, waveformPath);
                        plan.TemporaryFiles.Add(waveformPath);
                        string waveformName = LocalizedWaveformName(waveform);
                        plan.Sections.Add(new ReportDynamicSection { Anchor = "attachment_sections", Title = waveformName,
                            Subtitle = waveform.Kind == "cal" ? L(ReportLanguages.UiKeys.CalibrationData) : L(ReportLanguages.UiKeys.TestData),
                            ImagePath = waveformPath, Caption = waveformName, PageBreakBefore = index > 0 });
                        index++; continue;
                    }
                    ReportImageInfo image = model == null ? null : model.Tag as ReportImageInfo; if (image == null) continue;
                    ReportImageInfo localized = ReportAttachmentText.ForRendering(image, languageIndex);
                    string path = string.Empty;
                    if ((image.ChartKind ?? string.Empty).IndexOf("Spectrum", StringComparison.OrdinalIgnoreCase) >= 0 || !string.IsNullOrWhiteSpace(image.RegionSummary))
                    {
                        if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        string rendered = TryRenderRegion(localized, Path.Combine(temp, "region_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                        if (rendered.Length > 0) { path = rendered; plan.TemporaryFiles.Add(path); }
                    }
                    if ((image.ChartKind ?? string.Empty).IndexOf("PDmap", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        string rendered = TryRenderPdmap(localized, Path.Combine(temp, "pdmap_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                        if (rendered.Length > 0) { path = rendered; plan.TemporaryFiles.Add(path); }
                    }
                    if ((image.Title ?? string.Empty).IndexOf("PRPD", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        string rendered = TryRenderPrpd(localized, Path.Combine(temp, "prpd_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png"));
                        if (rendered.Length > 0) { path = rendered; plan.TemporaryFiles.Add(path); }
                    }
                    if (path.Length == 0 && languageIndex == 0) path = image.ImagePath;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        if (languageIndex != 0) { unavailable++; continue; }
                        if (!image.HasPngData) { unavailable++; continue; } if (temp.Length == 0) temp = CreateGenerationTempDirectory();
                        path = Path.Combine(temp, "attachment_" + index.ToString("000", CultureInfo.InvariantCulture) + ".png"); File.WriteAllBytes(path, image.PngData); plan.TemporaryFiles.Add(path);
                    }
                    if (!ImageContainsContent(path)) { image.StatusReason = ReportLanguages.Ui(languageIndex, "图片无有效图形数据，未加入报告。", "The image contains no meaningful chart data and was skipped."); continue; }
                    plan.Sections.Add(new ReportDynamicSection { Anchor = "attachment_sections", Title = localized.Title, Subtitle = localized.RegionSummary, ImagePath = path, Caption = localized.Title, PageBreakBefore = index > 0 }); index++;
                }
            }
            plan.OmitEmptyAppendix = index == 0;
            if (index == 0) plan.Values["report.appendix_intro"] = string.Empty;
            if (index > 0 && unavailable > 0) plan.Values["report.appendix_intro"] += " " + L(ReportLanguages.UiKeys.AttachmentUnavailable);
        }

        static bool ImageContainsContent(string path)
        {
            try
            {
                using (var image = new Bitmap(path))
                {
                    int colored = 0, sampled = 0;
                    for (int y = 0; y < image.Height; y += Math.Max(1, image.Height / 48))
                        for (int x = 0; x < image.Width; x += Math.Max(1, image.Width / 64))
                        { Color c = image.GetPixel(x, y); sampled++; if (c.A > 20 && (c.R < 242 || c.G < 242 || c.B < 242)) colored++; }
                    return sampled > 0 && colored >= Math.Max(8, sampled / 400);
                }
            }
            catch { return false; }
        }

        IEnumerable<TreeNode> Descendants(TreeNode root) { foreach (TreeNode child in root.Nodes) { yield return child; foreach (TreeNode nested in Descendants(child)) yield return nested; } }
        string CreateGenerationTempDirectory() { string directory = Path.Combine(Path.GetTempPath(), "HVX_Report_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
        string CreatePreviewTempPath(string name)
        {
            if (previewDirectory.Length == 0)
                previewDirectory = Path.Combine(Path.GetTempPath(), "HVX_ReportPreview_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(previewDirectory);
            string path = Path.Combine(previewDirectory, Guid.NewGuid().ToString("N") + "_" + name);
            previewTemporaryFiles.Add(path);
            return path;
        }

        void PrunePreviewFiles()
        {
            for (int i = previewTemporaryFiles.Count - 1; i >= 0; i--)
            {
                string path = previewTemporaryFiles[i];
                if (previewCache.ContainsValue(path)) continue;
                try { File.Delete(path); previewTemporaryFiles.RemoveAt(i); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (previewTemporaryFiles.Count == 0 && previewDirectory.Length > 0)
            {
                try { if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory, false); previewDirectory = string.Empty; }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        void ClearPreviewCache()
        {
            Image displayed = previewImage.Image; previewImage.Image = null;
            if (displayed != null) displayed.Dispose();
            activePreviewKey = string.Empty;
            previewCache.Clear(); previewCacheOrder.Clear();
            PrunePreviewFiles();
            if (previewDirectory.Length > 0)
            {
                try { if (Directory.Exists(previewDirectory)) Directory.Delete(previewDirectory, false); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (previewTemporaryFiles.Count == 0) previewDirectory = string.Empty;
            }
        }

        void ShowAttachmentSelection(ReportAnalysisPackage package)
        {
            using (var form = CreateDialog(ReportLanguages.Ui(languageIndex, "报告附件", "Report attachments"), new Size(560, 320)))
            {
                var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, BorderStyle = BorderStyle.None, HorizontalScrollbar = true };
                ReportImageInfo[] images = package.ReportImages.OrderBy(item => item.Sequence).ToArray();
                for (int i = 0; i < images.Length; i++) list.Items.Add(ReportAttachmentText.Title(images[i], languageIndex), images[i].IncludedInReport);
                list.ItemCheck += (s, e) => {if(images[e.Index].AcquisitionReferenceOnly)e.NewValue=CheckState.Unchecked;images[e.Index].IncludedInReport = e.NewValue == CheckState.Checked;};
                var close = new ReportButton { Dock = DockStyle.Bottom, Height = 36, Text = L(ReportLanguages.UiKeys.Close), DialogResult = DialogResult.OK }; ReportTheme.StylePrimaryButton(close);
                close.FontChanged+=(s,e)=>close.Height=Math.Max(36,close.Font.Height+14);
                form.Controls.Add(list); form.Controls.Add(close); form.AcceptButton = close; form.CancelButton = close;
                ReportTheme.ReserveDialogBody(form,close,new Size(360,240));form.ShowDialog(FindForm());
            }
        }

        Form CreateDialog(string title, Size client)
        {
            var form=new Form { Text = title, ClientSize = client, MinimumSize = new Size(360,240), Padding = new Padding(12), Font = Font, BackColor = ReportTheme.Surface, FormBorderStyle = FormBorderStyle.Sizable, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
            ReportTheme.ApplyDialogIcon(form);return form;
        }

        Icon DialogIcon()
        {
            Form owner = FindForm();
            if (owner != null && owner.Icon != null) return owner.Icon;
            return HVX.Presentation.OemUiBranding.LoadIcon();
        }

        ExistingOutputChoice ShowExistingOutputDialog()
        {
            ExistingOutputChoice choice = ExistingOutputChoice.Cancel;
            using (var form = CreateDialog(L(ReportLanguages.UiKeys.OutputConflictTitle), new Size(510, 176)))
            {
                var message = new Label { Dock = DockStyle.Fill, Text = L(ReportLanguages.UiKeys.OutputExistsPrompt), ForeColor = ReportTheme.Ink, Padding = new Padding(6), TextAlign = ContentAlignment.MiddleLeft };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
                var cancel = new ReportButton { Text = L(ReportLanguages.UiKeys.Cancel), Width = 96, DialogResult = DialogResult.Cancel };
                var overwriteButton = new ReportButton { Text = L(ReportLanguages.UiKeys.Overwrite), Width = 96 };
                var saveAs = new ReportButton { Text = L(ReportLanguages.UiKeys.SaveAs), Width = 96 };
                ReportTheme.StyleSecondaryButton(cancel); ReportTheme.StylePrimaryButton(overwriteButton); ReportTheme.StyleSecondaryButton(saveAs);
                overwriteButton.Click += (s, e) => { choice = ExistingOutputChoice.Overwrite; form.DialogResult = DialogResult.OK; form.Close(); };
                saveAs.Click += (s, e) => { choice = ExistingOutputChoice.SaveAs; form.DialogResult = DialogResult.OK; form.Close(); };
                footer.Controls.Add(cancel); footer.Controls.Add(overwriteButton); footer.Controls.Add(saveAs); form.Controls.Add(message); form.Controls.Add(footer); form.CancelButton = cancel; form.ShowDialog(FindForm());
            }
            return choice;
        }

        void ApplyLanguage()
        {
            moduleTitle.Text = L(ReportLanguages.UiKeys.Title);
            conclusionFeedbackButton.Text = ReportLanguages.Ui(languageIndex, "本地 AI · 评分修正", "Local AI · Review");
            loadButton.Text = L(ReportLanguages.UiKeys.LoadAnalysis); openReportButton.Text = L(ReportLanguages.UiKeys.OpenReport); settingsButton.Text = ReportLanguages.Ui(languageIndex, "统计设置", "Statistics");
            openReportDirectoryButton.Text = ReportLanguages.Ui(languageIndex, "打开报告目录", "Report folder");
            printReportButton.Text = ReportLanguages.Ui(languageIndex, "打印报告", "Print report");
            aiButton.Text = L(ReportLanguages.UiKeys.GenerateConclusion); clearButton.Text = L(ReportLanguages.UiKeys.Clear); generateButton.Text = L(ReportLanguages.UiKeys.GenerateReport);
            briefConclusion.Text = languageIndex == 0 ? "简洁结论" : languageIndex == 1 ? "Brief" : L(ReportLanguages.UiKeys.SimpleConclusion);
            professionalConclusion.Text = languageIndex == 0 ? "专业结论" : languageIndex == 1 ? "Expert" : L(ReportLanguages.UiKeys.ProfessionalConclusion);
            conclusionHeading.Text = L(ReportLanguages.UiKeys.Conclusion); addRangeMenu.Text = L(ReportLanguages.UiKeys.AddVoltageRange); deleteRangeMenu.Text = L(ReportLanguages.UiKeys.DeleteVoltageRange);
            UpdateHeader(); UpdateRecommendation(); UpdateInteractionState();
            if (!HasValidProject) SetStatus(L(ReportLanguages.UiKeys.InvalidProjectPath), true);
            else if (!HasAnalysisData) SetStatus(L(ReportLanguages.UiKeys.RestrictedBrowse), false);
            else if (string.IsNullOrWhiteSpace(status.Text)) SetStatus(L(ReportLanguages.UiKeys.Ready), false);
        }

        void UpdateHeader()
        {
            string project = projectData == null || string.IsNullOrWhiteSpace(projectData.ProjectName) ? (string.IsNullOrWhiteSpace(projectPath) ? L(ReportLanguages.UiKeys.Title) : new DirectoryInfo(projectPath).Name) : projectData.ProjectName;
            project = AnalysisDateText.DisplayProjectName(project);
            string details = projectData == null ? string.Empty : "  ·  " + projectData.StartName + "–" + projectData.EndName + "  ·  " + FormatNumber(projectData.CableLengthM) + " m";
            projectHeader.Text = project + details;
        }

        string IntervalText(VoltageInterval interval)
        {
            if (interval == null) return string.Empty;
            if (interval.IsAllResults) return L(ReportLanguages.UiKeys.AllLocationResults);
            if (string.Equals(interval.Id, "le_u0", StringComparison.OrdinalIgnoreCase)) return L(ReportLanguages.UiKeys.AtOrBelowU0LocationResults);
            if (string.Equals(interval.Id, "u0_to_limit", StringComparison.OrdinalIgnoreCase)) return ReportLanguages.Format(languageIndex, ReportLanguages.UiKeys.ServiceBandLocationResults, "U0", interval.MaximumU0.ToString("0.0", CultureInfo.CurrentCulture) + "U0");
            return interval.Title;
        }

        string PhaseText(ReportPhase phase)
        {
            if (phase == ReportPhase.A) return L(ReportLanguages.UiKeys.PhaseA); if (phase == ReportPhase.B) return L(ReportLanguages.UiKeys.PhaseB); if (phase == ReportPhase.C) return L(ReportLanguages.UiKeys.PhaseC); return L(ReportLanguages.UiKeys.AllPhases);
        }

        string LocalizedWaveformName(BrowserRecord record)
        {
            if (ReportLanguages.IsChinese(languageIndex) || !ReportAttachmentText.ContainsHan(record.Name)) return record.Name;
            string kind = record.Kind == "cal" ? L(ReportLanguages.UiKeys.CalibrationData) : L(ReportLanguages.UiKeys.TestData);
            return kind + " " + Math.Max(1, record.Order).ToString(CultureInfo.InvariantCulture);
        }

        void UpdatePhaseDischargeVoltagesFromMatrix()
        {
            if (projectData == null || locationMatrix == null || locationMatrix.GetLength(0) == 0 || locationMatrix.GetLength(1) <= 35) return;
            foreach (ReportPhase phase in new[] { ReportPhase.A, ReportPhase.B, ReportPhase.C })
            {
                double minimum = double.NaN;
                for (int row = 0; row < locationMatrix.GetLength(0); row++)
                {
                    double phaseValue = locationMatrix[row, 0], voltage = locationMatrix[row, 2], accepted = locationMatrix[row, 35];
                    if (!ProjectReportData.IsFinite(phaseValue) || Math.Abs(phaseValue - (int)phase) > 1e-6 ||
                        !ProjectReportData.IsFinite(voltage) || voltage <= 0.0 ||
                        !ProjectReportData.IsFinite(accepted) || Math.Abs(accepted - 1.0) > 1e-9) continue;
                    if (!ProjectReportData.IsFinite(minimum) || voltage < minimum) minimum = voltage;
                }
                if (ProjectReportData.IsFinite(minimum)) projectData.ForPhase(phase).PDIV = minimum;
            }
            projectData.RefreshValues();
        }

        static string ResolveTestDate(Dictionary<string, string> values)
        {
            string date;
            values.TryGetValue("project.date", out date);
            if (string.IsNullOrWhiteSpace(date) || date == ProjectReportData.Missing) return ProjectReportData.Missing;
            DateTime parsed;
            if (DateTime.TryParse(date, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out parsed) ||
                DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var match = System.Text.RegularExpressions.Regex.Match(date, @"(?<y>\d{4})[年/-](?<m>\d{1,2})[月/-](?<d>\d{1,2})");
            if (match.Success) return match.Groups["y"].Value + "-" + match.Groups["m"].Value.PadLeft(2, '0') + "-" + match.Groups["d"].Value.PadLeft(2, '0');
            return date;
        }

        string BuildReportHeading()
        {
            string start = projectData == null ? ProjectReportData.Missing : projectData.StartName;
            string end = projectData == null ? ProjectReportData.Missing : projectData.EndName;
            string length = projectData != null && ProjectReportData.IsFinite(projectData.CableLengthM)
                ? FormatNumber(projectData.CableLengthM) + " m" : ProjectReportData.Missing;
            if (start != ProjectReportData.Missing && end != ProjectReportData.Missing)
                return start + " → " + end + (length == ProjectReportData.Missing ? string.Empty : " · " + length);
            string name = ReportProjectName();
            return name != ProjectReportData.Missing ? name : L(ReportLanguages.UiKeys.ReportTitle);
        }

        string ReportProjectName()
        {
            if (projectData == null) return ProjectReportData.Missing;
            if (!string.IsNullOrWhiteSpace(projectData.LineName) && projectData.LineName != ProjectReportData.Missing)
                return projectData.LineName;
            string name = projectData.ProjectName;
            if (string.IsNullOrWhiteSpace(name) || name == ProjectReportData.Missing ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{4}年\d{1,2}月\d{1,2}日\d{1,2}时\d{1,2}分"))
                return ProjectReportData.Missing;
            return name;
        }

        string CableAgeText()
        {
            return projectData != null && projectData.OlderThanOneYear ? ReportLanguages.Ui(languageIndex, "旧电缆（一年以上）", "Old cable (> 1 year)") :
                ReportLanguages.Ui(languageIndex, "新电缆（一年以内）", "New cable (≤ 1 year)");
        }

        string FormatVoltagePair(double voltageKv)
        {
            if (!ProjectReportData.IsFinite(voltageKv)) return ProjectReportData.Missing;
            if (projectData == null || !ProjectReportData.IsFinite(projectData.DeviceU0Kv) || projectData.DeviceU0Kv <= 0.0) return FormatNumber(voltageKv);
            return FormatNumber(voltageKv) + " / " + (voltageKv / projectData.DeviceU0Kv).ToString("0.0", CultureInfo.CurrentCulture);
        }

        void OnConclusionModeChanged(CheckBox changed)
        {
            if (changingConclusionMode) return;
            changingConclusionMode = true;
            try
            {
                // These are two presentations of one conclusion, not two
                // independent report sections. Always keep exactly one chosen.
                if (changed.Checked)
                    (changed == briefConclusion ? professionalConclusion : briefConclusion).Checked = false;
                else if (!briefConclusion.Checked && !professionalConclusion.Checked)
                {
                    changed.Checked = true;
                    return;
                }
            }
            finally { changingConclusionMode = false; }
            // A saved V3 input has verified row, joint and U0 bindings. An
            // explicit presentation choice may render that exact scope, never
            // the unfiltered archive matrix or an unbound legacy snapshot.
            if (analysisPackage != null && analysisPackage.HasSavedConclusionSnapshot && analysisPackage.UsesEvidenceV3)
                preferSavedConclusionSnapshot = false;
            if (HasAnalysisData) RefreshConclusion();
        }

        string L(string key) { return ReportLanguages.Text(languageIndex, key); }
        bool HasValidProject { get { return !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath); } }
        bool HasAnalysisData { get { return HasValidProject && (analysisPackage != null || (locationMatrix != null && locationMatrix.GetLength(0) > 0)); } }
        void UpdateInteractionState()
        {
            bool projectReady = HasValidProject, analysisReady = HasAnalysisData;
            tree.Enabled = !busy && projectReady;
            loadButton.Enabled = !busy && projectReady;
            openReportButton.Enabled = !busy && FindOpenableReportPath().Length > 0;
            openReportDirectoryButton.Enabled = openReportButton.Enabled;
            printReportButton.Enabled = openReportButton.Enabled;
            settingsButton.Enabled = !busy && analysisReady;
            aiButton.Enabled = !busy && analysisReady;
            conclusionFeedbackButton.Enabled = !busy && analysisReady;
            clearButton.Enabled = !busy && analysisReady;
            generateButton.Enabled = !busy && analysisReady;
            conclusion.ReadOnly = busy || !analysisReady;
            briefConclusion.Enabled = !busy && analysisReady; professionalConclusion.Enabled = !busy && analysisReady;
        }
        void SetBusy(bool value, string text)
        {
            busy = value; UpdateInteractionState();
            UseWaitCursor = value; if (!string.IsNullOrWhiteSpace(text)) SetStatus(text, false); Application.DoEvents();
        }
        void UpdateReportProgress(int percent, string stage)
        {
            reportProgress.Value = Math.Max(0, Math.Min(100, percent));
            string detail = stage == "prepare" ? ReportLanguages.Ui(languageIndex, "准备图表和数据", "Preparing figures and data") :
                stage == "docx" ? ReportLanguages.Ui(languageIndex, "生成 Word 报告", "Creating Word report") :
                stage == "pdf" ? ReportLanguages.Ui(languageIndex, "转换 PDF", "Converting PDF") : ReportLanguages.Ui(languageIndex, "完成", "Complete");
            SetStatus(L(ReportLanguages.UiKeys.GeneratingReport) + " " + percent.ToString(CultureInfo.CurrentCulture) + "% · " + detail, false);
            Application.DoEvents();
        }
        void SetStatus(string text, bool error) { status.Text = text ?? string.Empty; status.ForeColor = error ? ReportTheme.Error : ReportTheme.Muted; }
        bool Fail(string message, bool show)
        {
            lastError = message ?? string.Empty; SetStatus(lastError, true);
            if (show && !string.IsNullOrWhiteSpace(lastError)) MessageBox.Show(FindForm(), lastError, L(ReportLanguages.UiKeys.Title), MessageBoxButtons.OK, MessageBoxIcon.Error); return false;
        }

        static string ResolveDefaultTemplate()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory, assemblyDirectory = Path.GetDirectoryName(typeof(ReportGeneratorControl).Assembly.Location);
            string[] candidates = { Path.Combine(baseDirectory, "templates", "EPFT_OWTS_Report_Template_v1.0.docx"), Path.Combine(assemblyDirectory, "templates", "EPFT_OWTS_Report_Template_v1.0.docx") };
            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        static string SafeFileName(string value) { string text = value ?? "OWTS"; foreach (char invalid in Path.GetInvalidFileNameChars()) text = text.Replace(invalid, '_'); return string.IsNullOrWhiteSpace(text) ? "OWTS" : text.Trim(); }
        static DateTime SafeLastWrite(string path) { try { return Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; } }
        static string FormatNumber(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? ProjectReportData.Missing : value.ToString("0.###", CultureInfo.CurrentCulture); }
        static string FormatCharge(double value) { return FormatCharge(value, true); }
        static string FormatCharge(double value, bool unit) { string text = FormatNumber(value); return text == ProjectReportData.Missing || !unit ? text : text + " pC"; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Image image = previewImage.Image; previewImage.Image = null; if (image != null) image.Dispose();
                ClearPreviewCache();
                locationMenu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
