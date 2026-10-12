// Source snapshot: D:/codex/HVXUnifiedDesktop/modules-src/PressureTest/vendor/Task39/RangeDecision.cs
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    public enum RangeDecisionAction { Dismiss, Ignore, AdjustRetry, StopAutomatic }

    public sealed class RangeDecisionContext
    {
        public LocationRangeResult Quality { get; private set; }
        public int CurrentRangeIndex { get; private set; }
        public int RecommendedRangeIndex { get; private set; }
        public double TargetVoltageV { get; private set; }
        public int RetriesUsed { get; private set; }
        public bool IsAutomaticFlow { get; private set; }
        public bool HasRealPressurization { get; private set; }
        public bool IsAutomaticRangeRetry { get; private set; }

        public RangeDecisionContext(LocationRangeResult quality, int currentRangeIndex, int recommendedRangeIndex,
            double targetVoltageV, int retriesUsed, bool isAutomaticFlow, bool hasRealPressurization, bool automaticRangeRetries = false)
        {
            if (quality == null) throw new ArgumentNullException("quality");
            if (currentRangeIndex < 0 || currentRangeIndex >= BenchSettings.Ranges.Length) throw new ArgumentOutOfRangeException("currentRangeIndex");
            if (recommendedRangeIndex < 0 || recommendedRangeIndex >= BenchSettings.Ranges.Length) throw new ArgumentOutOfRangeException("recommendedRangeIndex");
            if (Double.IsNaN(targetVoltageV) || Double.IsInfinity(targetVoltageV) || targetVoltageV < 0) throw new ArgumentOutOfRangeException("targetVoltageV");
            if (!hasRealPressurization && targetVoltageV != 0) throw new ArgumentException("本工具没有真实加压能力，不能把非零目标表述为重新加压。");
            if (retriesUsed < 0) throw new ArgumentOutOfRangeException("retriesUsed");
            Quality = RangeDecisionPolicy.CopyQuality(quality);
            CurrentRangeIndex = currentRangeIndex; RecommendedRangeIndex = recommendedRangeIndex;
            TargetVoltageV = targetVoltageV; RetriesUsed = retriesUsed;
            IsAutomaticFlow = isAutomaticFlow; HasRealPressurization = hasRealPressurization;
            IsAutomaticRangeRetry = automaticRangeRetries;
        }
    }

    // The operator's acceptance is separate from measured quality and never edits it.
    // This record is attached to a captured round; source data/settings remain unchanged.
    public sealed class RangeDecisionRecord
    {
        public DateTime DecidedUtc;
        public RangeDecisionAction Action;
        public int CurrentRangeIndex;
        public int RecommendedRangeIndex;
        public double TargetVoltageV;
        public int RetriesUsed;
        public int MaximumRetries = 1;
        public bool IsAutomaticFlow;
        public bool HasRealPressurization;
        public bool AutomaticRangeRetriesEnabled;
        public bool WasAutomaticDecision;
        public bool SuppressFurtherRangePrompts;
        public bool OperatorAcceptedData;
        public bool RequestedRetry;
        public bool RequestedStopAutomatic;
        public bool OriginalQualityPassed;
        public LocationRangeResult QualitySnapshot;
        public string Meaning;
        public string SafetyBoundary = "操作者接受仅表示继续保存/分析，不会清除削顶、溢出、量程不合格标志，也不证明硬件安全。弹窗不执行硬件命令。";
    }

    public static class RangeDecisionPolicy
    {
        public const int MaximumRetries = 1;
        public static readonly int MaximumAutomaticRetries = BenchSettings.Ranges.Length - 1;

        public static int RetryLimit(RangeDecisionContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            return context.IsAutomaticRangeRetry ? MaximumAutomaticRetries : MaximumRetries;
        }

        public static bool IsMaximumRangeRisk(RangeDecisionContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            return context.CurrentRangeIndex == BenchSettings.Ranges.Length - 1 &&
                (context.Quality.LocationClippedSamples > 0 || context.Quality.LocationHighOccupancy ||
                 context.Quality.DriverOverflow || context.Quality.DriverOverflowUncertain);
        }

        public const string MaximumRangeWarning = "已到最大输入量程：目前局放输入输入信号过大或无法判定真实峰值。若确为放电信号，继续试验可能存在设备老化或损坏风险，应暂停并人工检查。ADC 削顶/溢出本身不证明局放、老化或损坏。此警告不会因勾选不再提示而隐藏。";
        public const string ExpectedCalibrationMaximumWarning = "输入的校准脉冲与校准系数所对应的预期电压已经超过设备最大量程，禁止自动重采。请先人工检查输入、衰减及校准参数；这不是实测局放或设备老化的判定。此警告不会因勾选不再提示而隐藏。";

        public static bool IsExpectedCalibrationBeyondMaximum(RangeDecisionContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            return context.Quality.ExpectedCalibrationVolts > BenchSettings.Ranges[BenchSettings.Ranges.Length - 1];
        }

        public static bool CanAdjustAndRetry(RangeDecisionContext context, out string reason)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (!context.Quality.WindowValid)
            { reason = "局放输入分析窗口无效或不完整，禁止按该窗口建议自动调档重采；请先检查窗口设置。"; return false; }
            if (IsMaximumRangeRisk(context))
            { reason = MaximumRangeWarning; return false; }
            if (IsExpectedCalibrationBeyondMaximum(context))
            { reason = ExpectedCalibrationMaximumWarning; return false; }
            if (context.RetriesUsed >= RetryLimit(context))
            { reason = context.IsAutomaticRangeRetry ? "本轮自动调档已达到 10 次重采上限，停止继续重试；请人工检查，不能往复调档。" : "本次原始单轮已经重试过一次；不再重试。可保留本轮并注明质量，或关闭弹窗后人工检查。"; return false; }
            if (context.Quality.RecommendedIndex != context.RecommendedRangeIndex)
            { reason = "推荐档位与本轮量程诊断不一致，禁止重试。"; return false; }
            if (!context.Quality.Change || context.RecommendedRangeIndex == context.CurrentRangeIndex)
            { reason = "没有不同的可用推荐量程，不能用相同档位重复本轮。"; return false; }
            if (context.Quality.AtMaximumAndClipped)
            { reason = "最大档仍削顶；没有合适的推荐量程，须先人工检查输入。"; return false; }
            if (context.Quality.LocationClippedSamples > 0 && context.RecommendedRangeIndex < context.CurrentRangeIndex)
            { reason = "本轮局放输入窗口已削顶，不能按更小量程重试。"; return false; }
            reason = context.HasRealPressurization
                ? "保持本轮目标电压，仅调整局放量程后重新加压；仍须由调用方检查联锁、残压及设备就绪。"
                : "本轮仅调整量程并重新采集人工注入的校准脉冲；不操作 AO、HV ON/OFF 或 P0.7 光触发。";
            reason += context.IsAutomaticRangeRetry ? "本轮自动调档最多 10 次，外层必须阻止重复访问已试档位。" : "未启用自动调档时本轮最多重试一次。";
            if (context.Quality.MaximumRangeInsufficient)
                reason += "计算所需余量超过最大档，推荐已受上限限制；允许尝试尚未使用的最大档，但不保证合格，仍异常时必须人工检查。";
            return true;
        }

        public static string RetryButtonText(RangeDecisionContext context)
        { return context.HasRealPressurization ? "调整并重新加压" : "调整并重新采集"; }

        public static RangeDecisionRecord CreateRecord(RangeDecisionContext context, RangeDecisionAction action)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (!Enum.IsDefined(typeof(RangeDecisionAction), action)) throw new ArgumentOutOfRangeException("action");
            string reason;
            if (action == RangeDecisionAction.AdjustRetry && !CanAdjustAndRetry(context, out reason)) throw new InvalidOperationException(reason);
            if (action == RangeDecisionAction.StopAutomatic && !context.IsAutomaticFlow)
                throw new InvalidOperationException("手动单轮没有自动加压流程可停止；不显示或接受该操作。");
            string meaning = action == RangeDecisionAction.Ignore
                ? "操作者接受本轮数据并继续保存/分析；保留全部原始质量标志，不将削顶或无效峰值改为有效。"
                : action == RangeDecisionAction.AdjustRetry
                ? "请求在同一目标电压下仅改推荐量程重做；本轮重采上限 " + RetryLimit(context) + " 次，本记录不是执行完成证明。"
                : action == RangeDecisionAction.StopAutomatic
                ? "请求停止后续自动加压；不丢弃本轮原始记录，不等于设备已断高压或已放电。"
                : "关闭弹窗：未接受本轮质量，未请求调档重试或继续自动流程。";
            return new RangeDecisionRecord {
                DecidedUtc = DateTime.UtcNow, Action = action,
                CurrentRangeIndex = context.CurrentRangeIndex, RecommendedRangeIndex = context.RecommendedRangeIndex,
                TargetVoltageV = context.TargetVoltageV, RetriesUsed = context.RetriesUsed, MaximumRetries = RetryLimit(context),
                IsAutomaticFlow = context.IsAutomaticFlow, HasRealPressurization = context.HasRealPressurization,
                AutomaticRangeRetriesEnabled = context.IsAutomaticRangeRetry,
                OperatorAcceptedData = action == RangeDecisionAction.Ignore,
                RequestedRetry = action == RangeDecisionAction.AdjustRetry,
                RequestedStopAutomatic = action == RangeDecisionAction.StopAutomatic,
                OriginalQualityPassed = context.Quality.LocationQualityPassed,
                QualitySnapshot = CopyQuality(context.Quality), Meaning = meaning
            };
        }

        public static BenchSettings CreateRetrySettings(BenchSettings original, RangeDecisionContext context)
        {
            if (original == null) throw new ArgumentNullException("original");
            if (context == null) throw new ArgumentNullException("context");
            original.Validate();
            string reason;
            if (!CanAdjustAndRetry(context, out reason)) throw new InvalidOperationException(reason);
            if (original.RangeIndex != context.CurrentRangeIndex || original.TargetVoltageV != context.TargetVoltageV)
                throw new InvalidOperationException("重试上下文与原始单轮设置不一致，不使用已被编辑的目标或档位。");
            // All current public instance settings fields are scalar values or immutable strings.
            // No setting is read from the live UI: retain exact identity, trigger and calibration.
            var following = new BenchSettings();
            foreach (FieldInfo field in typeof(BenchSettings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(following, field.GetValue(original));
            following.RangeIndex = context.RecommendedRangeIndex;
            following.RunNumber = checked(original.RunNumber + 1);
            following.Validate();
            return following;
        }

        internal static LocationRangeResult CopyQuality(LocationRangeResult quality)
        {
            // A deep snapshot makes a later UI operation unable to rewrite an earlier decision's flags.
            var serializer = new XmlSerializer(typeof(LocationRangeResult));
            using (var stream = new MemoryStream())
            {
                serializer.Serialize(stream, quality); stream.Position = 0;
                return (LocationRangeResult)serializer.Deserialize(stream);
            }
        }
    }

    // Presentation and explicit decision only. No device API, scheduler, settings file
    // or source capture is accessed by this dialog.
    internal sealed class RangeDecisionDialog : Form
    {
        private readonly RangeDecisionContext context;
        private readonly CheckBox suppressPrompts = new CheckBox { AutoSize = true, Text = "本次会话不再弹普通量程提示，自动调档重采" };
        internal RangeDecisionAction Decision { get; private set; }
        internal RangeDecisionRecord Record { get; private set; }
        internal bool SuppressFurtherRangePrompts { get; private set; }

        internal RangeDecisionDialog(RangeDecisionContext context, Icon ownerIcon = null)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context; Decision = RangeDecisionAction.Dismiss;
            Text = "雄新电力科技 · 量程处理确认";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(930, 760); MinimumSize = new Size(830, 670);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            BackColor = Color.FromArgb(239, 244, 247);
            if (ownerIcon != null) Icon = (Icon)ownerIcon.Clone();
            else using (var stream = GetType().Assembly.GetManifestResourceStream("Factory.Brand.ico"))
                if (stream != null) using (var icon = new Icon(stream)) Icon = (Icon)icon.Clone();
            Disposed += (s, e) => { if (Icon != null) Icon.Dispose(); };

            bool maximumRisk = RangeDecisionPolicy.IsMaximumRangeRisk(context);
            bool expectedRisk = RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context);
            bool forceWarning = maximumRisk || expectedRisk;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, forceWarning ? 142 : 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            var title = new Label { Text = maximumRisk ? RangeDecisionPolicy.MaximumRangeWarning : expectedRisk ? RangeDecisionPolicy.ExpectedCalibrationMaximumWarning : "本轮量程需要您决定\r\n忽略仅表示接受继续分析；削顶、溢出和质量诊断仍完整保留。", Dock = DockStyle.Fill,
                ForeColor = forceWarning ? Color.DarkRed : Color.FromArgb(34, 65, 79), Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
                BackColor = forceWarning ? Color.FromArgb(255, 220, 220) : BackColor, Padding = new Padding(5) };
            layout.Controls.Add(title, 0, 0);
            string retryReason; bool canRetry = RangeDecisionPolicy.CanAdjustAndRetry(context, out retryReason);
            var detail = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Text = BuildDetails(context, retryReason) };
            layout.Controls.Add(detail, 0, 1);
            var promptOptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 8, 0, 0) };
            promptOptions.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); promptOptions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            suppressPrompts.Text = context.HasRealPressurization
                ? "本次会话不再弹普通量程提示，自动调档重新加压"
                : "本次会话不再弹普通量程提示，自动调档重采";
            suppressPrompts.Checked = context.IsAutomaticRangeRetry;
            string automaticReason;
            suppressPrompts.Enabled = RangeDecisionPolicy.CanAdjustAndRetry(WithAutomaticRetries(true), out automaticReason);
            promptOptions.Controls.Add(suppressPrompts, 0, 0);
            promptOptions.Controls.Add(new Label { Dock = DockStyle.Fill, Text = context.HasRealPressurization
                ? "仅选择“调整并重新加压”时生效，可能多次重复同一目标电压；须持续满足安全联锁。最高档仍异常的风险警告永不隐藏。"
                : "仅选择“调整并重新采集”时生效，可能多次采集校准脉冲；本轮不启动 HV、不执行 AO、不发光。最高档风险警告永不隐藏。",
                ForeColor = Color.FromArgb(135, 75, 20) }, 0, 1);
            layout.Controls.Add(promptOptions, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
                Padding = new Padding(0, 12, 0, 0), FlowDirection = FlowDirection.LeftToRight };
            AddDecision(buttons, "忽略，保存并继续分析", RangeDecisionAction.Ignore, true);
            var retry = AddDecision(buttons, RangeDecisionPolicy.RetryButtonText(context), RangeDecisionAction.AdjustRetry, canRetry);
            var tip = new ToolTip { AutoPopDelay = 20000 }; tip.SetToolTip(retry, retryReason); Disposed += (s, e) => tip.Dispose();
            suppressPrompts.CheckedChanged += (s, e) => {
                var effective = WithAutomaticRetries(suppressPrompts.Checked);
                string updatedReason;
                retry.Enabled = RangeDecisionPolicy.CanAdjustAndRetry(effective, out updatedReason);
                detail.Text = BuildDetails(effective, updatedReason);
                tip.SetToolTip(retry, updatedReason);
            };
            if (context.IsAutomaticFlow) AddDecision(buttons, "停止加压（退出自动流程）", RangeDecisionAction.StopAutomatic, true);
            var dismiss = AddDecision(buttons, "关闭，暂不决定", RangeDecisionAction.Dismiss, true);
            CancelButton = dismiss; // Enter is deliberately not assigned to acceptance or retry.
            layout.Controls.Add(buttons, 0, 3);
            Shown += (s, e) => { detail.SelectionStart = 0; detail.SelectionLength = 0; };
            FormClosing += (s, e) => { if (Record == null) { Decision = RangeDecisionAction.Dismiss; Record = RangeDecisionPolicy.CreateRecord(context, Decision); } };
        }

        private Button AddDecision(FlowLayoutPanel buttons, string caption, RangeDecisionAction action, bool enabled)
        {
            var button = new Button { Text = caption, AutoSize = true, MinimumSize = new Size(150, 42),
                Margin = new Padding(0, 0, 9, 7), Padding = new Padding(8, 4, 8, 4), Enabled = enabled,
                DialogResult = action == RangeDecisionAction.Dismiss ? DialogResult.Cancel : DialogResult.None };
            button.Click += (s, e) => {
                // Checkbox alone never starts a workflow. Only explicit AdjustRetry can
                // request session suppression; Ignore, X and Esc do not opt into outputs.
                SuppressFurtherRangePrompts = action == RangeDecisionAction.AdjustRetry && suppressPrompts.Enabled && suppressPrompts.Checked;
                var effective = action == RangeDecisionAction.AdjustRetry ? WithAutomaticRetries(suppressPrompts.Checked) : context;
                Record = RangeDecisionPolicy.CreateRecord(effective, action); Record.SuppressFurtherRangePrompts = SuppressFurtherRangePrompts;
                Record.AutomaticRangeRetriesEnabled = context.IsAutomaticRangeRetry || SuppressFurtherRangePrompts;
                Decision = action; DialogResult = action == RangeDecisionAction.Dismiss ? DialogResult.Cancel : DialogResult.OK; Close();
            };
            buttons.Controls.Add(button); return button;
        }

        private RangeDecisionContext WithAutomaticRetries(bool enabled)
        {
            return new RangeDecisionContext(context.Quality, context.CurrentRangeIndex, context.RecommendedRangeIndex,
                context.TargetVoltageV, context.RetriesUsed, context.IsAutomaticFlow, context.HasRealPressurization, enabled);
        }

        private static string BuildDetails(RangeDecisionContext c, string retryReason)
        {
            var inv = CultureInfo.InvariantCulture;
            string current = "±" + BenchSettings.Ranges[c.CurrentRangeIndex].ToString("G5", inv) + " V";
            string next = "±" + BenchSettings.Ranges[c.RecommendedRangeIndex].ToString("G5", inv) + " V";
            return "本轮量程：" + current + "    推荐量程：" + next + "\r\n目标设定：" + c.TargetVoltageV.ToString("G6", inv) +
                " V    已重试：" + c.RetriesUsed + " / " + RangeDecisionPolicy.RetryLimit(c) + "\r\n\r\n" +
                c.Quality.LocationQuality + "\r\n原始峰值占用：" + c.Quality.LocationPeakFraction.ToString("P2", inv) +
                "；局放输入窗口削顶 " + c.Quality.LocationClippedSamples + " 点；窗外削顶 " + c.Quality.OutsideClippedSamples +
                " 点；驱动全段溢出：" + (c.Quality.DriverOverflow ? "是" : "否") + "\r\n\r\n推荐依据\r\n" + c.Quality.Reason +
                "\r\n\r\n重试条件\r\n" + retryReason + "\r\n\r\n" + (c.HasRealPressurization
                ? "此组件仅记录决定，重新加压必须由真实设备流程执行完整安全检查；停止自动流程不等于确认放电。"
                : "当前为人工注入校准脉冲的采集流程。重新采集不等于重新加压，不会操作 AO、HV ON/OFF 或 P0.7 光触发。") +
                "\r\n\r\n" + c.Quality.SafetyNote + "\r\n关闭或按 Esc 不会隐式选择忽略、重试或继续自动流程。";
        }
    }
}

