using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Xiongxin.ZeroBench
{
    // Host-owned presentation adapter. The pinned Task39 source and decision policy
    // remain unchanged; no production entry calls its legacy Form.
    // Presentation and explicit decision only. No device API, scheduler, settings file
    // or source capture is accessed by this dialog.
    internal sealed class PressureRangeDecisionDialog : Form
    {
        private readonly RangeDecisionContext context;
        private readonly bool retryPermitted;
        private readonly CheckBox suppressPrompts = new CheckBox { AutoSize = true, Text = "本次会话不再弹普通量程提示，自动调档重采" };
        internal RangeDecisionAction Decision { get; private set; }
        internal RangeDecisionRecord Record { get; private set; }
        internal bool SuppressFurtherRangePrompts { get; private set; }

        internal PressureRangeDecisionDialog(RangeDecisionContext context, Icon ownerIcon = null):this(context,ownerIcon,true) { }
        internal PressureRangeDecisionDialog(RangeDecisionContext context, Icon ownerIcon, bool retryPermitted)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context; this.retryPermitted=retryPermitted; Decision = RangeDecisionAction.Dismiss;
            Text = "量程处理确认";
            Font = new Font("Microsoft YaHei UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(620, 400); MinimumSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            BackColor = Color.FromArgb(239, 244, 247);
            Icon ownedIcon = null;
            if (ownerIcon != null) ownedIcon = (Icon)ownerIcon.Clone();
            else ownedIcon = HVX.Presentation.OemUiBranding.LoadIcon();
            if (ownedIcon != null) Icon = ownedIcon;
            Disposed += (s, e) => { if (ownedIcon != null) ownedIcon.Dispose(); };

            bool maximumRisk = RangeDecisionPolicy.IsMaximumRangeRisk(context);
            bool expectedRisk = RangeDecisionPolicy.IsExpectedCalibrationBeyondMaximum(context);
            bool forceWarning = maximumRisk || expectedRisk;
            var viewport = new Panel { Name = "RangeDecisionViewport", Dock = DockStyle.Fill, AutoScroll = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 3; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewport.Controls.Add(layout); Controls.Add(viewport);
            var title = new Label { Text = maximumRisk ? "信号超过最大量程，数据可能无效。请停止试验，按现场规程检查接线和输入信号。" : expectedRisk ? "校准信号过大。请减小输入或检查校准设置后重试。" : "请调整量程重新试验，或忽略并继续。", Dock = DockStyle.Fill,
                AutoSize = true, ForeColor = forceWarning ? Color.DarkRed : Color.FromArgb(34, 65, 79),
                BackColor = forceWarning ? Color.FromArgb(255, 220, 220) : BackColor, Padding = new Padding(5) };
            layout.Controls.Add(title, 0, 0);
            string retryReason; bool canRetry = CanOfferAdjustment(context,retryPermitted,out retryReason);
            retryReason = RetryDescription(context, canRetry, retryReason);
            var detail = new TextBox { Name = "RangeDecisionDetails", Dock = DockStyle.Fill, Height = 64, Multiline = true, ReadOnly = true, WordWrap = true,
                ScrollBars = ScrollBars.None, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                Text = BuildDetails(context, retryReason) };
            layout.Controls.Add(detail, 0, 1);
            var promptOptions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 1, Padding = new Padding(0, 8, 0, 0) };
            promptOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            promptOptions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            suppressPrompts.Dock = DockStyle.Fill;
            suppressPrompts.Text = "不再提示（自动调整）";
            suppressPrompts.Checked = context.IsAutomaticRangeRetry;
            string automaticReason;
            suppressPrompts.Enabled = CanOfferAdjustment(WithAutomaticRetries(true),retryPermitted,out automaticReason);
            promptOptions.Controls.Add(suppressPrompts, 0, 0);
            layout.Controls.Add(promptOptions, 0, 2);
            var buttons = new FlowLayoutPanel { Name = "RangeDecisionActions", Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
                Padding = new Padding(16, 8, 16, 8), FlowDirection = FlowDirection.LeftToRight };
            var retry = AddDecision(buttons, "调整量程重新试验", RangeDecisionAction.AdjustRetry, canRetry);
            AddDecision(buttons, "忽略并继续试验", RangeDecisionAction.Ignore, true);
            var tip = new ToolTip { AutoPopDelay = 20000 }; tip.SetToolTip(retry, retryReason); Disposed += (s, e) => tip.Dispose();
            suppressPrompts.CheckedChanged += (s, e) => {
                var effective = WithAutomaticRetries(suppressPrompts.Checked);
                string updatedReason;
                retry.Enabled = CanOfferAdjustment(effective,this.retryPermitted,out updatedReason);
                updatedReason = RetryDescription(effective, retry.Enabled, updatedReason);
                detail.Text = BuildDetails(effective, updatedReason);
                tip.SetToolTip(retry, updatedReason);
            };
            // X/Esc remains cancellation, never an implicit retry or acceptance.
            KeyPreview=true;KeyDown+=(s,e)=>{if(e.KeyCode==Keys.Escape){e.Handled=true;Close();}};
            Controls.Add(buttons);
            Action measure = () => {
                int width = Math.Max(100, viewport.ClientSize.Width - layout.Padding.Horizontal - 12);
                title.MaximumSize = new Size(width, 0);
                suppressPrompts.MaximumSize = new Size(width, 0);
                detail.Height = Math.Max(64, Font.Height * 3);
            };
            SizeChanged += (s, e) => measure(); FontChanged += (s, e) => measure();
            Shown += (s, e) => { FitWorkingArea(Screen.FromControl(Owner ?? this).WorkingArea); measure(); detail.SelectionStart = 0; detail.SelectionLength = 0; };
            FormClosing += (s, e) => { if (Record == null) {
                Decision = RangeDecisionAction.Dismiss;
                SuppressFurtherRangePrompts=suppressPrompts.Enabled&&suppressPrompts.Checked;
                Record = RangeDecisionPolicy.CreateRecord(context, Decision);
                Record.SuppressFurtherRangePrompts=SuppressFurtherRangePrompts;
            } };
        }

        internal void FitWorkingArea(Rectangle work)
        {
            int width = Math.Max(1, work.Width - 16), height = Math.Max(1, work.Height - 16);
            MinimumSize = new Size(Math.Min(560, width), Math.Min(360, height));
            Size = new Size(Math.Min(Width, width), Math.Min(Height, height));
            Location = new Point(Math.Max(work.Left, Math.Min(Left, work.Right - Width)),
                Math.Max(work.Top, Math.Min(Top, work.Bottom - Height)));
        }

        private Button AddDecision(FlowLayoutPanel buttons, string caption, RangeDecisionAction action, bool enabled)
        {
            var button = new HVX.Presentation.R2RoundedButton { Text = caption, AutoSize = true, MinimumSize = new Size(150, 42),
                Margin = new Padding(0, 0, 9, 7), Padding = new Padding(8, 4, 8, 4), Enabled = enabled,
                DialogResult = action == RangeDecisionAction.Dismiss ? DialogResult.Cancel : DialogResult.None };
            button.Click += (s, e) => {
                // This records a persistent presentation preference on every
                // explicit close. It never creates a start/retry/acceptance intent.
                SuppressFurtherRangePrompts = suppressPrompts.Enabled && suppressPrompts.Checked;
                var effective = action == RangeDecisionAction.AdjustRetry ? WithAutomaticRetries(suppressPrompts.Checked) : context;
                Record = RangeDecisionPolicy.CreateRecord(effective, action); Record.SuppressFurtherRangePrompts = SuppressFurtherRangePrompts;
                Record.AutomaticRangeRetriesEnabled = context.IsAutomaticRangeRetry || action==RangeDecisionAction.AdjustRetry&&SuppressFurtherRangePrompts;
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
            // Presentation only: never turn missing/ambiguous evidence into a
            // too-small/too-large verdict. Full diagnostics remain in Record.
            var q=c.Quality;
            if(!q.WindowValid || q.DriverOverflowUncertain)
                return "量程暂无法判断，请检查采集数据。";
            if(q.LocationClippedSamples>0 || q.LocationHighOccupancy || q.AtMaximumAndClipped || q.MaximumRangeInsufficient)
                return "量程过小";
            if(q.RecommendationUncertain)return "量程暂无法判断，请检查采集数据。";
            if(q.DriverOverflow && !q.SwitchingTransientExcluded)
                return "量程暂无法判断，请检查采集数据。";
            if(q.Change && c.RecommendedRangeIndex>c.CurrentRangeIndex)return "量程过小";
            if(q.Change && c.RecommendedRangeIndex<c.CurrentRangeIndex)return "量程过大";
            return "当前量程无需调整";
        }

        internal static bool CanOfferAdjustment(RangeDecisionContext c,bool permitted,out string reason)
        {
            if(c==null)throw new ArgumentNullException("c");
            var q=c.Quality;
            // Clipping is a real lower bound and can support an increase. An
            // unlocated driver overflow or uncertain unclipped recommendation
            // cannot justify a range choice, even when the frozen policy has
            // a nominal different index. Do not rewrite that quality to pass.
            if(!q.WindowValid||q.DriverOverflowUncertain||q.RecommendationUncertain&&q.LocationClippedSamples==0)
            {reason=HeldStatus(c.Quality);return false;}
            if(!permitted){reason="没有未尝试的可用量程，原始数据保留；后续轮次已停止。";return false;}
            return RangeDecisionPolicy.CanAdjustAndRetry(c,out reason);
        }
        internal static string HeldStatus(LocationRangeResult q)
        {
            if(!q.WindowValid)return "无法评估本轮数据，原始记录已保留。请检查采集设置后重新开始。";
            if(q.DriverOverflowUncertain)return "本轮采集溢出位置待复核，数据已保留；后续轮次已停止。";
            return "本轮量程质量待复核，数据已保留；后续轮次已停止。";
        }

        private static string RetryDescription(RangeDecisionContext c, bool permitted, string originalReason)
        {
            // Presentation only. The unmodified policy creates the evidence
            // record and still decides whether a repeat is permitted.
            if (!permitted) return "当前无法调整量程。请检查采集信号后重新开始。";
            string text = c.HasRealPressurization
                ? "确认现场安全后，调整量程重新进行本轮试验。"
                : "保持零电压设置，调整量程重新采集。";
            return text;
        }
    }
}
