using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    public sealed class FactoryNextRangeDecision
    {
        public DateTime DecidedUtc;
        public int CurrentRangeIndex, RecommendedRangeIndex;
        public bool ApplyNextRound;
        public bool AutomaticRetryRequested; // Always false: this dialog cannot start a worker.
        public string Meaning;
        public LocationRangeResult OriginalQuality;
        public string ArtSerial, PicoSerial, SequenceId, RecordDirectory, Action, Reason;
        public int SequenceRound, SequenceCount, SequenceAttempt, CompletedRounds, RemainingRounds, FinalRangeIndex;
        public bool Simulation, ReminderEnabled, WasAutomaticDecision;
        public bool OptionalPromptSuppressed, DisableOptionalPromptsRequested;
        public double CurrentFullScaleV, RecommendedFullScaleV, FinalFullScaleV, ActualRecordMs, EvaluationWindowMs;
    }

    internal static class FactoryNextRangeReview
    {
        internal static bool NeedsPrompt(FactoryResult round, LocationRangeResult quality, bool remind)
        {
            var context=FactoryRangeReview.CreateMeasurementContext(round,quality);
            return quality.AtMaximumAndClipped || quality.MaximumRangeInsufficient || FactoryTransientRangePolicy.BlocksForOverflow(quality,round.Capture.Settings.RangeIndex) ||
                RangeDecisionPolicy.IsMaximumRangeRisk(context) || RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context) ||
                remind && (!quality.WindowValid || quality.Change || !FactoryTransientRangePolicy.WindowQualityPassed(quality));
        }
        internal static bool CanApply(FactoryResult round, LocationRangeResult quality, int current)
        {
            return round != null && (round.CompleteCaptureAvailable() || round.CaptureOnlyAvailable()) && round.Capture != null && quality != null &&
                !quality.AtMaximumAndClipped && !quality.MaximumRangeInsufficient && !FactoryTransientRangePolicy.BlocksForOverflow(quality,current) &&
                !(FactoryTransientRangePolicy.BlocksDownrange(quality) && quality.RecommendedIndex<current) &&
                quality.WindowValid && quality.Change && quality.RecommendedIndex >= 0 &&
                quality.RecommendedIndex < BenchSettings.Ranges.Length && quality.RecommendedIndex != current &&
                current == round.Capture.Settings.RangeIndex && current == round.Settings.Input.RangeIndex;
        }
        internal static FactoryNextRangeDecision Decide(FactoryResult round, LocationRangeResult quality, int current, bool apply)
        {
            if (round == null || !(round.CompleteCaptureAvailable() || round.CaptureOnlyAvailable()) || quality == null)
                throw new InvalidOperationException("缺少完整本轮及量程评估，不能更新下一轮量程。");
            if (apply && !CanApply(round, quality, current))
                throw new InvalidOperationException("量程建议无效或参数已改变；请重新核对，不自动采用。");
            return new FactoryNextRangeDecision { DecidedUtc = DateTime.UtcNow, CurrentRangeIndex = current,
                RecommendedRangeIndex = quality.RecommendedIndex, ApplyNextRound = apply, AutomaticRetryRequested = false,
                OriginalQuality = RangeDecisionPolicy.CopyQuality(quality),
                ArtSerial = round.Capture.ArtSerial, PicoSerial = round.Capture.PicoSerial, Simulation = round.Settings.Simulation,
                SequenceId = round.Settings.SequenceId, SequenceRound = round.Settings.SequenceRound,
                SequenceCount = round.Settings.SequenceCount, SequenceAttempt = round.Settings.SequenceAttempt,
                CompletedRounds = round.Settings.SequenceRound, RemainingRounds = Math.Max(0, round.Settings.SequenceCount - round.Settings.SequenceRound),
                RecordDirectory = round.DirectoryPath, ReminderEnabled = round.Settings.Input.AutoRange,
                FinalRangeIndex = apply ? quality.RecommendedIndex : current,
                CurrentFullScaleV = BenchSettings.Ranges[current],
                RecommendedFullScaleV = quality.RecommendedIndex >= 0 && quality.RecommendedIndex < BenchSettings.Ranges.Length ? BenchSettings.Ranges[quality.RecommendedIndex] : Double.NaN,
                FinalFullScaleV = BenchSettings.Ranges[apply ? quality.RecommendedIndex : current],
                ActualRecordMs = round.Capture.PdAdc.Length * 1000.0 / round.Capture.PicoRateHz,
                EvaluationWindowMs = quality.WindowValid ? quality.LocationSampleCount * 1000.0 / round.Capture.PicoRateHz : 0,
                Action = apply ? "ApplyNextRound" : "KeepCurrent", Reason = quality.Reason + "；" + quality.WindowDefinition,
                Meaning = apply ? "已确认推荐量程，仅供下一轮参数；本次量程选择不启动硬件或重做本轮，后续仍走原有开始/队列确认。本轮原始数据及质量标记不变。"
                    : "本次未调整量程；未请求重加压或重采集，本轮原始数据及质量标记不变。" };
        }
        internal static FactoryNextRangeDecision Silent(FactoryResult round, LocationRangeResult quality, bool automaticNext)
        {
            int current = round.Capture.Settings.RangeIndex;
            bool apply = automaticNext && !round.Settings.Input.AutoRange && CanApply(round,quality,current);
            var d = Decide(round,quality,current,apply);
            d.WasAutomaticDecision = true;
            d.Action = apply ? "AutomaticAdjustNextRound" : !automaticNext ? "NoQueuedNextRound" : "KeepCurrent";
            d.Meaning = "量程决定（未弹窗）：" + (apply ? "自动调整下一轮" : !automaticNext ? "无后续队列轮次，不改档" : "保留当前档") +
                "；原 ±" + d.CurrentFullScaleV + " V，建议 ±" + d.RecommendedFullScaleV + " V，最终 ±" + d.FinalFullScaleV +
                " V；已完成 " + d.CompletedRounds + " / " + d.SequenceCount + "，剩余 " + d.RemainingRounds + "。" + d.Reason;
            return d;
        }
        internal static void Save(FactoryResult round, FactoryNextRangeDecision decision)
        {
            if (String.IsNullOrEmpty(round.DirectoryPath) || !Directory.Exists(round.DirectoryPath))
                throw new DirectoryNotFoundException("缺少本轮记录目录，量程决定未保存，不应用调整。");
            string path = Path.Combine(round.DirectoryPath, "next-range-decision-" + Guid.NewGuid().ToString("N") + ".xml");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(FactoryNextRangeDecision)).Serialize(stream, decision);
        }
    }

    internal sealed class FactoryNextRangeDialog : Form
    {
        internal bool ApplyNext { get; private set; }
        internal bool KeepCurrent { get; private set; }
        internal Button ApplyButton { get; private set; }
        internal Button KeepButton { get; private set; }
        internal readonly CheckBox DoNotShow=new CheckBox {Text="不再显示可选提示（主界面可恢复；不代表同意自动调档）",AutoSize=true};
        private readonly bool canSuppress;
        internal bool DisableOptionalPrompts { get { return canSuppress && DoNotShow.Checked; } }
        internal FactoryNextRangeDialog(FactoryResult round, LocationRangeResult quality, int current, Icon icon, Action stopQueue=null,bool allowRepeatSuppression=false)
        {
            Text = "本轮量程评估 · 确认调整下一轮"; Icon = icon;
            Font = new Font("Microsoft YaHei UI", 10); Size = new Size(810, 540); MinimumSize = new Size(720, 450);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            canSuppress=allowRepeatSuppression && FactoryOptionalPromptPolicy.CanSuppressRange(round,quality);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,canSuppress?38:0));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65)); Controls.Add(layout);
            string recommendation = quality.RecommendedIndex >= 0 && quality.RecommendedIndex < BenchSettings.Ranges.Length
                ? "±" + BenchSettings.Ranges[quality.RecommendedIndex] + " V" : "无有效建议";
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "本轮 ±" + round.Capture.PicoFullScaleV + " V  →  建议 " + recommendation +
                "\n只改下一轮设置；不重做本轮，后续仍走原有开始/队列确认。", Font = new Font(Font, FontStyle.Bold), ForeColor = Color.Teal }, 0, 0);
            var context = FactoryRangeReview.CreateMeasurementContext(round, quality);
            string warning = RangeDecisionPolicy.IsMaximumRangeRisk(context) ? RangeDecisionPolicy.MaximumRangeWarning + "\r\n\r\n" : "";
            bool blocked=!quality.WindowValid || quality.AtMaximumAndClipped || quality.MaximumRangeInsufficient ||
                FactoryTransientRangePolicy.BlocksForOverflow(quality,current);
            if(blocked)warning="本轮数据已保存；当前量程评估不能支持下一轮，连续队列将停止。\r\n\r\n"+warning;
            if (RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context)) warning += RangeDecisionPolicy.ExpectedCalibrationMaximumWarning + "\r\n\r\n";
            layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White, Text = warning + quality.WindowDefinition + "\r\n\r\n" + quality.Reason + "\r\n" + quality.LocationQuality +
                "\r\n\r\n原始ADC、实采量程、削顶/溢出标记保持不变。关闭本窗不会标记质量合格，也不会确认设备已放电。" }, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
            var close = new Button { Text = "关闭", Width = 90, Height = 38, DialogResult = DialogResult.Cancel };
            KeepButton = new Button { Text = "保留当前量程", Width = 150, Height = 38, Enabled = !blocked };
            ApplyButton = new Button { Text = "调整下一轮量程", Width = 185, Height = 38, Enabled = FactoryNextRangeReview.CanApply(round, quality, current) };
            KeepButton.Click += (s,e) => { KeepCurrent = true; DialogResult = DialogResult.OK; Close(); };
            ApplyButton.Click += (s,e) => { ApplyNext = true; DialogResult = DialogResult.OK; Close(); };
            DoNotShow.Visible=DoNotShow.Enabled=canSuppress;layout.Controls.Add(DoNotShow,0,2);
            buttons.Controls.Add(close); buttons.Controls.Add(KeepButton); buttons.Controls.Add(ApplyButton); layout.Controls.Add(buttons, 0, 3);
            if(stopQueue!=null)
            {
                var stop=new Button {Text="停止队列",Width=105,Height=38,ForeColor=Color.DarkRed};
                stop.Click+=(s,e)=>{stopQueue();DialogResult=DialogResult.Cancel;Close();};buttons.Controls.Add(stop);
            }
            CancelButton = close; // No default Enter-key acceptance of a range change.
            Shown += (s,e) => { ActiveControl = close; };
        }
    }
}
