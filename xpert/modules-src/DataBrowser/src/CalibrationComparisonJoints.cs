using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.ProjectCables;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        readonly BrowserButton findComparisonJointsButton = new BrowserButton { Name = "FindComparisonJoints" };
        CalibrationTraceGeometry comparisonGeometry;
        double[] comparisonMeanX, comparisonMeanY;
        CalibrationJointCandidate[] comparisonJointCandidates = new CalibrationJointCandidate[0];
        string comparisonJointProjectionWarning = "";
        Dictionary<string,string> comparisonMeanSourceHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string comparisonJointSearchMessage="";
        void InitializeComparisonJoints()
        {
            findComparisonJointsButton.Text = T("接头查找", "Find joints");
            actions.Controls.Add(findComparisonJointsButton);
            findComparisonJointsButton.Click += async delegate { await FindComparisonJointsWithFeedbackAsync(); };
            plot.CableDoubleClick += x => { if (CanUseComparisonGeometry()) ShowJoints(comparisonGeometry.DistanceAt(x * 1e-6)); };
        }
        bool CanUseComparisonGeometry()
        { return SelectedNodeKind == "cal-comparison" && comparisonAverage.Checked && comparisonGeometry != null && comparisonGeometry.Kind != "Capacitor" && !IsBusy && !editing; }
        void ClearComparisonGeometry()
        {
            comparisonGeometry = null; comparisonMeanX = comparisonMeanY = null;
            comparisonJointCandidates = new CalibrationJointCandidate[0];
            comparisonJointProjectionWarning = "";
            comparisonMeanSourceHashes.Clear();comparisonJointSearchMessage="";
            plot.SetCableOverlay(Double.NaN, Double.NaN, Double.NaN);
            findComparisonJointsButton.Enabled = false;
        }
        void ApplyComparisonGeometry(ComparisonPrepared prepared)
        {
            comparisonGeometry = prepared.Geometry; comparisonMeanX = prepared.MeanX; comparisonMeanY = prepared.MeanY;
            comparisonMeanSourceHashes=new Dictionary<string,string>(prepared.SourceHashes,StringComparer.OrdinalIgnoreCase);
            comparisonJointCandidates = new CalibrationJointCandidate[0]; RefreshComparisonOverlay();
        }
        void RefreshComparisonOverlay()
        {
            var g = comparisonGeometry;
            if (g == null || g.Kind == "Capacitor") { plot.SetCableOverlay(Double.NaN, Double.NaN, Double.NaN); return; }
            var positions = comparisonJointCandidates.Select(c => c.Seconds * 1e6).ToList();
            var labels = comparisonJointCandidates.Select(CandidateText).ToList();
            string priorWarning = comparisonJointProjectionWarning;
            comparisonJointProjectionWarning = "";
            try
            {
                // A phase dictionary loses the saved block scope: a shared cable
                // may have three A headers and no ALL key. Use the actual blocks,
                // but never infer shared joints from an unrelated phase union.
                var blocks = ComparisonProjectBlocks(CableJointStore.Read(projectPath), g);
                foreach (double distance in blocks[0].Positions.Skip(1).Take(blocks[0].Positions.Count - 2))
                { positions.Add(g.IncidentSeconds * 1e6 + 2 * distance / g.VelocityMPerUs); labels.Add(T("已保存接头 ", "Saved joint ") + distance.ToString("0.0", CultureInfo.InvariantCulture) + " m"); }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                comparisonJointProjectionWarning = T("未投影已保存接头：", "Saved joints were not projected: ") + ex.Message;
            }
            if (priorWarning.Length > 0) information.Text = information.Text.Replace(priorWarning + Environment.NewLine, "");
            if (comparisonJointProjectionWarning.Length > 0)
            { information.Text = comparisonJointProjectionWarning + Environment.NewLine + information.Text; SetStatus(comparisonJointProjectionWarning); }
            plot.SetCableOverlay(g.IncidentSeconds * 1e6, g.EndSeconds * 1e6, g.VelocityMPerUs, positions.ToArray(), labels.ToArray());
        }
        string CandidateText(CalibrationJointCandidate c)
        { return c.DistanceM.ToString("0.0", CultureInfo.InvariantCulture) + " m · " + T("评分 ", "Score ") + c.Score.ToString("0.0", CultureInfo.InvariantCulture) + "/100 · r=" + c.Correlation.ToString("0.000", CultureInfo.InvariantCulture) + " · SNR=" + c.SignalToNoise.ToString("0.0", CultureInfo.InvariantCulture) + Environment.NewLine + c.Reason; }
        public async Task<bool> FindComparisonJointsAsync()
        {
            if (InvokeRequired) throw new InvalidOperationException("Start on the UI thread.");
            comparisonJointSearchMessage="";
            if (!CanUseComparisonGeometry()) {comparisonJointSearchMessage=T("请选择相容的校准并勾选多校准平均，完成平均后查找接头。", "Select compatible calibrations and enable averaging before finding joints.");return Error(comparisonJointSearchMessage);}
            var geometry = comparisonGeometry; var x = comparisonMeanX; var y = comparisonMeanY;
            var sources=new Dictionary<string,string>(comparisonMeanSourceHashes,StringComparer.OrdinalIgnoreCase);
            int pg = projectGeneration, selection = selectionGeneration; var token = waveCancellation.Token;string sourceRoot=projectPath;
            loadingWave = true; findComparisonJointsButton.Enabled = false;
            SetStatus(T("正在分析平均波形，仅生成待确认候选…", "Screening the mean waveform; candidates require confirmation…"));
            try
            {
                var candidates = await Task.Run(() => {
                    ValidateComparisonMeanSources(sourceRoot,sources,token);
                    var result=CalibrationJointCandidates.Find(x,y,geometry,token);
                    ValidateComparisonMeanSources(sourceRoot,sources,token);return result;
                }, token);
                if (IsDisposed || pg != projectGeneration || selection != selectionGeneration || token.IsCancellationRequested) return false;
                comparisonJointCandidates = candidates; RefreshComparisonOverlay();
                string summary = comparisonJointProjectionWarning + (comparisonJointProjectionWarning.Length > 0 ? Environment.NewLine : "") + T("待人工确认接头候选：", "Joint candidates for manual review: ") + candidates.Length + T("；评分不是置信概率，尚未写入项目。", "; scores are not probabilities. Nothing has been saved.");
                information.Text = summary + Environment.NewLine + String.Join(Environment.NewLine, candidates.Select(CandidateText)) + Environment.NewLine + information.Text;
                AdjustInformationHeight(); SetStatus(summary); return true;
            }
            catch (OperationCanceledException) { if(!IsDisposed&&pg==projectGeneration&&selection==selectionGeneration)SetStatus(T("已取消接头查找。","Joint search cancelled."));return false; }
            catch (Exception ex) { if (!IsDisposed && pg == projectGeneration && selection == selectionGeneration && !token.IsCancellationRequested) { comparisonJointCandidates = new CalibrationJointCandidate[0]; RefreshComparisonOverlay();comparisonJointSearchMessage=ex.Message; Error(ex.Message); information.Text = ex.Message + Environment.NewLine + information.Text;AdjustInformationHeight(); } return false; }
            finally { if (!IsDisposed && pg == projectGeneration && selection == selectionGeneration) { loadingWave = false; findComparisonJointsButton.Enabled = CanUseComparisonGeometry(); } }
        }
        void ValidateComparisonMeanSources(string root,Dictionary<string,string> sources,System.Threading.CancellationToken token)
        { ValidateComparisonMeanSourcesCore(root,sources,token,english); }
        static void ValidateComparisonMeanSourcesCore(string root,Dictionary<string,string> sources,System.Threading.CancellationToken token,bool useEnglish)
        {
            if(sources.Count==0)throw new InvalidDataException(useEnglish?"Mean source information is unavailable. Recalculate before finding joints.":"平均来源信息已失效，请重新计算后查找接头。");
            foreach(var source in sources) {
                token.ThrowIfCancellationRequested();
                if(!String.Equals(source.Value,ComparisonSourceHash(source.Key,token),StringComparison.Ordinal))
                    throw new InvalidDataException((useEnglish?"A mean source has changed. Recalculate before finding joints: ":"参与平均的校准文件已改变，请重新计算后查找接头：")+Path.GetFileName(source.Key));
                // The formal record is only a manifest: raw waveforms and the
                // ITEM/CABLE identity remain part of its evidence contract.
                if(IsFormalCalibration(source.Key)) {
                    HVX.CalibrationStorage.LiveCalibrationRecord.Load(root,source.Key,true);
                    if(!String.Equals(source.Value,ComparisonSourceHash(source.Key,token),StringComparison.Ordinal))
                        throw new InvalidDataException("Calibration source changed during validation: "+Path.GetFileName(source.Key));
                }
                token.ThrowIfCancellationRequested();
            }
        }
        public async Task<bool> FindComparisonJointsWithFeedbackAsync()
        {
            if(InvokeRequired)throw new InvalidOperationException("Start on the UI thread.");
            int pg=projectGeneration,selection=selectionGeneration;
            bool completed=await FindComparisonJointsAsync();
            if(IsDisposed||Disposing||pg!=projectGeneration||selection!=selectionGeneration)return false;
            if(completed&&CanUseComparisonGeometry()&&comparisonJointCandidates.Length>0)ShowComparisonJointCandidates();
            else if(completed)ShowComparisonJointSearchResult(T("未检出可复核的接头候选。","No joint candidates were detected for review."),false);
            else if(comparisonJointSearchMessage.Length>0)ShowComparisonJointSearchResult(comparisonJointSearchMessage,true);
            return completed;
        }
        Icon ComparisonDialogIcon()
        {
            var owner=FindForm();if(owner!=null&&owner.Icon!=null)return (Icon)owner.Icon.Clone();
            return HVX.Presentation.OemUiBranding.LoadIcon();
        }
        void ShowComparisonJointSearchResult(string reason,bool stopped)
        {
            using(var icon=ComparisonDialogIcon())
            using(var dialog=new Form {Name="ComparisonJointSearchResultDialog",Text=stopped?T("接头查找已停止","Joint search stopped"):T("接头查找结果","Joint search result"),
                StartPosition=FormStartPosition.CenterParent,ClientSize=new Size(650,330),MinimumSize=new Size(500,270),Font=Font,Icon=icon,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false})
            {
                var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(14)};
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var details=new TextBox {Name="ComparisonJointSearchDetails",Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=dialog.BackColor,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,
                    Text=ComparisonJointCustomerMessage(reason,stopped)+
                    Environment.NewLine+Environment.NewLine+T("本次没有写入接头。您仍可在平均图上双击位置，打开接头列表进行人工复核。","No joints have been saved. Double-click a position on the valid mean to open the joint list for manual review.")};
                var close=new HVX.Presentation.R2RoundedButton {Name="ComparisonJointSearchClose",Text=T("关闭","Close"),AutoSize=true,DialogResult=DialogResult.OK,Anchor=AnchorStyles.Right};
                layout.Controls.Add(details,0,0);layout.Controls.Add(close,0,1);dialog.Controls.Add(layout);dialog.AcceptButton=close;dialog.CancelButton=close;dialog.ShowDialog(FindForm());
            }
        }
        void ShowComparisonJointCandidates()
        {
            if (!CanUseComparisonGeometry() || comparisonJointCandidates.Length == 0) return;
            var candidates = comparisonJointCandidates; int generation = selectionGeneration;
            using(var icon=ComparisonDialogIcon())
            using (var dialog = new Form { Name="ComparisonJointCandidatesDialog",Text = T("接头候选 · 人工确认", "Joint candidates · Manual review"), StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(720, 480), MinimumSize = new Size(540, 360), Font = Font, Icon = icon, ShowInTaskbar = false })
            {
                var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
                foreach (var candidate in candidates) list.Items.Add(candidate.DistanceM.ToString("0.0", CultureInfo.InvariantCulture) + " m · " + T("评分 ", "Score ") + candidate.Score.ToString("0.0", CultureInfo.InvariantCulture));
                var explanation = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
                var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
                var review = new HVX.Presentation.R2RoundedButton { Text = T("复核并打开接头编辑", "Review in joint editor"), AutoSize = true, DialogResult = DialogResult.OK };
                var close = new HVX.Presentation.R2RoundedButton { Text = T("关闭", "Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
                footer.Controls.Add(review); footer.Controls.Add(close); layout.Controls.Add(list, 0, 0); layout.Controls.Add(explanation, 0, 1); layout.Controls.Add(footer, 0, 2); dialog.Controls.Add(layout); dialog.CancelButton = close;
                list.SelectedIndexChanged += delegate { if (list.SelectedIndex >= 0) explanation.Text = CandidateText(candidates[list.SelectedIndex]); }; list.SelectedIndex = 0;
                if (dialog.ShowDialog(FindForm()) == DialogResult.OK && generation == selectionGeneration && CanUseComparisonGeometry() && list.SelectedIndex >= 0)
                    ShowJoints(candidates[list.SelectedIndex].DistanceM);
            }
        }
        void RequireComparisonProjectGeometry(CableJointSnapshot snapshot)
        {
            if (!CanUseComparisonGeometry()) throw new InvalidOperationException("平均波形或选择已改变，请重新计算。 / Recalculate the current mean.");
            // A modal may remain open while another process changes calibration
            // evidence. Revalidate on confirmation, before any CABLE write.
            ValidateComparisonMeanSources(projectPath,comparisonMeanSourceHashes,System.Threading.CancellationToken.None);
            ComparisonProjectBlocks(snapshot, comparisonGeometry);
        }
        static CableJointBlock[] ComparisonProjectBlocks(CableJointSnapshot snapshot, CalibrationTraceGeometry g)
        {
            bool shared = snapshot.Mode == CableJointMode.SharedThreeCore;
            if (!shared && String.Equals(g.Phase, "ALL", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ALL校准未对应明确的三芯共用结构，请选择实际相别。 / ALL calibration requires explicit shared three-core geometry; select the actual phase.");
            var blocks = snapshot.Blocks.Where(b => shared || String.Equals(b.Phase.TrimEnd('相'), g.Phase.TrimEnd('相'), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (blocks.Length == 0 || blocks.Any(b => Math.Abs(b.Positions.Last() - g.LengthM) > Math.Max(.001, g.LengthM * 1e-6)))
                throw new InvalidDataException("当前项目本相长度与平均校准不一致，不能使用该图的距离写入接头。 / Project geometry differs from the displayed mean.");
            if (blocks.Skip(1).Any(b => !b.Positions.SequenceEqual(blocks[0].Positions)))
                throw new InvalidDataException("对应电缆块的接头位置不一致，未合并或推测共有接头。 / Corresponding cable blocks disagree; no shared joints were inferred or merged.");
            return blocks;
        }
    }
}
