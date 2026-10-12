using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Requested AI ranges, checked against committed driver readback. These do
    // not change supply conversion scales or voltage/current protection limits.
    internal sealed class FactoryArtFeedbackPlan
    {
        internal const double RateHz = 1000;
        internal readonly double ImonRangeV, VmonRangeV, DividerRangeV;
        internal readonly double ImonNegativeLimitRawV, VmonNegativeLimitRawV;
        internal readonly int WindowSamples;
        internal readonly int RequiredVoltageChannel;
        internal FactoryArtFeedbackPlan(FactorySettings s)
        {
            if (s == null) throw new ArgumentNullException("s");
            WindowSamples = s.FeedbackWindowMs;
            if (WindowSamples < 3 || WindowSamples > 40) throw new ArgumentException("AI反馈窗口须为3～40 ms。");
            ImonRangeV = VmonRangeV = 5.5;
            DividerRangeV = DividerFullScale(s);
            RequiredVoltageChannel = s.PressureFeedbackContext ? (s.DividerOnlyFeedback ? 2 : 1) : -1;
            // A requested unipolar span is not proof that every calibrated SDK
            // sample below zero is a saturated ADC code. Preserve signed data
            // and bound the low side using the already configured protections.
            // Snapshot them with the committed plan; later setting edits cannot
            // widen a live stream's allowance. Stage-specific guards still run.
            if (!FactorySettings.Positive(s.FeedbackFullScaleV) || !FactorySettings.Positive(s.ImonFullScaleMa) ||
                !FactorySettings.Positive(s.VmonFullScaleKv) || !FactorySettings.Positive(s.CurrentLimitMa) ||
                !FactorySettings.Positive(s.VmonNegativeLimitV))
                throw new ArgumentException("ART反馈换算及负向保护配置无效。");
            ImonNegativeLimitRawV = s.CurrentLimitMa * s.FeedbackFullScaleV / s.ImonFullScaleMa;
            VmonNegativeLimitRawV = s.VmonNegativeLimitV * s.FeedbackFullScaleV / (s.VmonFullScaleKv * 1000);
            if (!FactorySettings.Positive(ImonNegativeLimitRawV) || !FactorySettings.Positive(VmonNegativeLimitRawV))
                throw new ArgumentException("ART反馈负向保护换算无效。");
        }
        internal static double DividerFullScale(FactorySettings s)
        {
            if(s==null||s.Input==null||!FactorySettings.Positive(s.MaximumKv)||!FactorySettings.Positive(s.Input.DividerVPerV))
                throw new ArgumentException("分压器量程需要有效最高电压和分压比。");
            double range=s.MaximumKv*1000/s.Input.DividerVPerV*1.5;
            if(!FactorySettings.Positive(range)||range>10)throw new ArgumentException("分压器请求量程超过ART ±10 V输入能力，请核对最高电压和分压比。");
            return range;
        }
        internal static double SelectRange(double expected)
        {
            if (!FactorySettings.Positive(expected)) throw new ArgumentException("ART 监测量程包络无效。");
            foreach (double range in new[] { 1.0, 2.0, 5.0, 10.0 }) if (expected <= range) return range;
            throw new ArgumentException("ART 监测电压包络超过 USB3135 的 ±10 V 输入范围。");
        }
        internal static double HalfCode(double range) { return range / 4096; }
        internal string Describe(FactorySettings s)
        {
            return String.Format(CultureInfo.InvariantCulture,
                "ART 监测 RSE/AGND，硬件时钟 {0:F0} S/s/通道；AI0 0～{1:G} V，AI2 0～{2:G} V，AI4 ±{3:G} V；" +
                "12 bit 理论码阶：VMON {4:F2} V、分压器 {5:F2} V。量化余量不是完整精度认证；无自动扣零；振荡有限段使用同一分压器量程 / 80 kS/s。",
                RateHz, ImonRangeV, VmonRangeV, DividerRangeV,
                HalfCode(VmonRangeV) * s.VmonFullScaleKv * 1000 / s.FeedbackFullScaleV,
                2 * HalfCode(DividerRangeV) * s.Input.DividerVPerV);
        }
        internal FactoryFeedback Parse(double[] interleaved, int count, int pending, bool emergency, double ao)
        {
            if (interleaved == null || count < 1 || interleaved.Length != count * 3 || pending < 0 || pending > 20)
                throw new InvalidOperationException("ART 监测组无效：数组=" + (interleaved == null ? -1 : interleaved.Length) +
                    "，每通道点数=" + count + "，未读点数=" + pending + "（最终要求≤20）。禁止使用积压反馈升压。");
            var f = new FactoryFeedback { TimestampUtc = DateTime.UtcNow.AddMilliseconds(-pending * 1000 / RateHz),
                SampleCount = count, EmergencyReleased = emergency, AoV = ao,
                ImonMinimumRawV = Double.PositiveInfinity, VmonMinimumRawV = Double.PositiveInfinity, DividerMinimumRawV = Double.PositiveInfinity,
                ImonMaximumRawV = Double.NegativeInfinity, VmonMaximumRawV = Double.NegativeInfinity, DividerMaximumRawV = Double.NegativeInfinity,
                ImonHalfCodeV = HalfCode(ImonRangeV)/2, VmonHalfCodeV = HalfCode(VmonRangeV)/2, DividerHalfCodeV = HalfCode(DividerRangeV) };
            for (int i = 0; i < count; i++)
            {
                double current = interleaved[i * 3], source = interleaved[i * 3 + 1], divider = interleaved[i * 3 + 2];
                if ((RequiredVoltageChannel < 0 && (!FactorySettings.Finite(current) || !FactorySettings.Finite(source) || !FactorySettings.Finite(divider) || current >= ImonRangeV * .995 || source >= VmonRangeV * .995 || Math.Abs(divider) >= DividerRangeV * .995)) ||
                    (RequiredVoltageChannel == 1 && (!FactorySettings.Finite(source) || source >= VmonRangeV * .995)) ||
                    (RequiredVoltageChannel == 2 && (!FactorySettings.Finite(divider) || Math.Abs(divider) >= DividerRangeV * .995)))
                    throw new InvalidOperationException("ART 原始反馈非有限或达到所选量程饱和边界；不平均、不截断后放行。");
                if ((RequiredVoltageChannel < 0 && current - f.ImonHalfCodeV <= -ImonNegativeLimitRawV) ||
                    (RequiredVoltageChannel != 2 && source - f.VmonHalfCodeV < -VmonNegativeLimitRawV))
                    throw new InvalidOperationException("ART 原始负向反馈超过已配置的电流保护或VMON背景边界；不平均、不截断后放行。");
                f.ImonMinimumRawV = Math.Min(f.ImonMinimumRawV, current); f.ImonMaximumRawV = Math.Max(f.ImonMaximumRawV, current);
                f.VmonMinimumRawV = Math.Min(f.VmonMinimumRawV, source); f.VmonMaximumRawV = Math.Max(f.VmonMaximumRawV, source);
                f.DividerMinimumRawV = Math.Min(f.DividerMinimumRawV, divider); f.DividerMaximumRawV = Math.Max(f.DividerMaximumRawV, divider);
                f.ImonRawV = current; f.VmonRawV = source; f.DividerRawV = divider;
            }
            FactoryFeedbackWindow.Apply(f, interleaved, count, WindowSamples, RequiredVoltageChannel);
            return f;
        }
    }

    internal interface IFactoryFeedbackBuffer
    {
        uint Available();
        double[] Read(int samplesPerChannel);
        bool EmergencyReleased();
    }
    // This is a one-shot startup allowance, not a relaxed running-feedback
    // deadline. It is invalidated by a valid first frame or any positive AO.
    // The monotonic elapsed value includes StartTask and time before ReadFeedback.
    internal sealed class FactoryFeedbackStartupWindow
    {
        private bool pending;
        private int timeoutMs;
        internal void Started(int initialTimeoutMs, bool zeroOutputPreparation)
        {
            if(initialTimeoutMs <= 0 || initialTimeoutMs > 10000)
                throw new ArgumentOutOfRangeException("initialTimeoutMs");
            timeoutMs=initialTimeoutMs;pending=zeroOutputPreparation;
        }
        internal bool NeedsPreparation(double ao, bool freshWindow, bool allowed)
        {return pending && ao == 0 && !freshWindow && allowed;}
        internal int RemainingMilliseconds(long elapsed)
        {
            if(elapsed < 0 || elapsed >= timeoutMs)
                throw new InvalidOperationException("ART 首帧准备超过已配置启动总期限="+timeoutMs+" ms；尚未取得有效反馈，不允许增加输出。");
            return checked(timeoutMs-(int)elapsed);
        }
        internal void ObserveOutput(double ao) {if(ao > 0)pending=false;}
        internal void Complete() {pending=false;}
    }
    internal static class FactoryFeedbackStream
    {
        internal const int MaximumQueue = 1000, MaximumBatches = 8, MaximumReadMs = 100;
        // A block may include at most ten new points after the observed queue
        // boundary. Wait for those points in the same 100 ms budget BEFORE
        // calling the SDK; a 20 ms native read must not request future samples.
        internal const int MaximumNewSamplesPerRead = 10;
        internal const int MaximumZeroSampleRetries = 8;
        internal static uint WaitForInitialWindow(FactoryArtFeedbackPlan plan, IFactoryFeedbackBuffer source,
            Func<long> elapsedMilliseconds, FactoryFeedbackStartupWindow startup,
            CancellationToken cancellationToken, Action<int> waitForSamples = null)
        {
            if(plan == null || source == null || elapsedMilliseconds == null || startup == null)
                throw new ArgumentNullException();
            if(waitForSamples == null)waitForSamples=Thread.Sleep;
            int polls=0;uint available=0;
            while(true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckInitialWindowBudget(startup,elapsedMilliseconds,available,plan.WindowSamples);
                if(!source.EmergencyReleased())throw new FactoryEmergencyStopException();
                available=source.Available();
                cancellationToken.ThrowIfCancellationRequested();
                CheckInitialWindowBudget(startup,elapsedMilliseconds,available,plan.WindowSamples);
                if(available > MaximumQueue)
                    throw new InvalidOperationException("ART 首帧缓存积压超过1秒：未读="+available+"点/通道；不使用积压数据放行升压。");
                if(available >= plan.WindowSamples)return available;
                // Real waits use a monotonic total deadline. This separate cap
                // also bounds a broken injected clock; polling never resets it.
                if(++polls > 10000)
                    throw new InvalidOperationException("ART 首帧等待次数超限；未读="+available+"点/通道，禁止继续升压。");
                waitForSamples(1);
            }
        }
        private static void CheckInitialWindowBudget(FactoryFeedbackStartupWindow startup,
            Func<long> elapsedMilliseconds,uint available,int required)
        {
            long elapsed=elapsedMilliseconds();
            try{startup.RemainingMilliseconds(elapsed);}
            catch(InvalidOperationException failure)
            {
                throw new InvalidOperationException("ART 首帧准备未完成：耗时="+elapsed+
                    " ms，当前未读="+available+"点/通道，完整窗口需要="+required+"点/通道。"+failure.Message,failure);
            }
        }
        internal static FactoryFeedback Read(FactoryArtFeedbackPlan plan, IFactoryFeedbackBuffer source,
            double ao, Func<long> elapsedMilliseconds, bool requireEmergency = true, bool requireFreshWindow = false,
            CancellationToken cancellationToken = default(CancellationToken), Action<int> waitForSamples = null,
            int readBudgetMs = MaximumReadMs)
        {
            if (plan == null || source == null || elapsedMilliseconds == null) throw new ArgumentNullException();
            // A startup allowance may shorten this read to its remaining total
            // time, but no caller can extend the 100 ms running/drain budget.
            if(readBudgetMs <= 0 || readBudgetMs > MaximumReadMs)throw new ArgumentOutOfRangeException("readBudgetMs");
            long start = elapsedMilliseconds(); FactoryFeedback merged = null; uint firstQueue = 0;
            var freshTail = requireFreshWindow ? new double[plan.WindowSamples * 3] : null;
            int freshTailCount = 0, freshSamples = 0, availabilityPolls = 0, zeroSampleRetries = 0;
            FactoryAnalogReadPendingException lastPending = null;
            if(waitForSamples == null) waitForSamples = Thread.Sleep;
            for (int batch = 0; batch < MaximumBatches; batch++)
            {
                CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                // false is used only by post-fault input diagnostics; it is NOT
                // an emergency permission and never enables a subsequent output.
                bool emergency = requireEmergency && source.EmergencyReleased();
                if (requireEmergency && !emergency) throw new FactoryEmergencyStopException();
                uint queued = source.Available(); if (batch == 0) firstQueue = queued;
                if (queued > MaximumQueue)
                    throw new InvalidOperationException("ART 缓存积压超过1秒：未读=" + queued + "点/通道，追读组=" + batch + "；禁止继续升压。");
                // Wait for a full fresh block if USB has fewer samples. The
                // existing 100 ms deadline and independent DI monitor remain.
                // An AO write invalidates the previous control window. The first
                // Available() call after it establishes a driver-visible queue
                // boundary, NOT a hardware timestamp or supply-settled proof.
                // Read and protect the old queue too, but use only subsequent
                // samples for the new window. Split long (up to 40 ms) windows
                // so the unchanged native timeout is never asked to wait 40 ms.
                int requested = requireFreshWindow
                    ? checked((int)queued + Math.Min(MaximumNewSamplesPerRead,plan.WindowSamples-freshTailCount))
                    : Math.Max(requireEmergency ? plan.WindowSamples : 1,(int)queued);
                // Only this AI owner consumes the queue. Keep the initial
                // post-AO boundary fixed while new USB batches arrive; neither
                // repeated availability polls nor old values count as fresh.
                uint available = queued;
                double[] raw;
                while(true)
                {
                    while(available < requested)
                    {
                        CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                        if(requireEmergency && !source.EmergencyReleased())throw new FactoryEmergencyStopException();
                        // Bound a broken injected clock as well as the real deadline.
                        if(++availabilityPolls > MaximumReadMs)
                            throw new InvalidOperationException("ART 等待反馈超过100次；未得到及时反馈，禁止继续升压。",lastPending);
                        waitForSamples(1);
                        CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                        if(requireEmergency && !source.EmergencyReleased())throw new FactoryEmergencyStopException();
                        available=source.Available();
                        if(available > MaximumQueue)
                            throw new InvalidOperationException("ART 缓存积压超过1秒：未读="+available+"点/通道；禁止继续升压。");
                    }
                    CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                    if(requireEmergency && !source.EmergencyReleased())throw new FactoryEmergencyStopException();
                    try { raw=source.Read(requested);break; }
                    catch(FactoryAnalogReadPendingException pendingRead)
                    {
                        lastPending=pendingRead;
                        CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                        if(requireEmergency && !source.EmergencyReleased())throw new FactoryEmergencyStopException();
                        if(++zeroSampleRetries > MaximumZeroSampleRetries)
                            throw new InvalidOperationException("ART反馈等待次数超限；未取得有效反馈，禁止继续升压。",lastPending);
                        // Same owner, same requested block and queue boundary,
                        // same total deadline. No feedback/timestamp/output is
                        // produced from an empty read; every valid batch below
                        // still passes the complete raw-peak protection scan.
                        waitForSamples(1);
                        CheckReadBudget(start,elapsedMilliseconds,cancellationToken,readBudgetMs,lastPending);
                        if(requireEmergency && !source.EmergencyReleased())throw new FactoryEmergencyStopException();
                        available=source.Available();
                        if(available > MaximumQueue)
                            throw new InvalidOperationException("ART 缓存积压超过1秒：未读="+available+"点/通道；禁止继续升压。");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Inspect EVERY returned batch before following the next USB packet.
                // Earlier peaks are merged rather than discarded in favor of the tail.
                FactoryFeedback part = plan.Parse(raw,requested,0,emergency,ao);
                merged = merged == null ? part : Merge(merged,part);
                if (requireFreshWindow)
                {
                    int skip = batch == 0 ? checked((int)firstQueue) : 0;
                    int added = requested-skip;
                    freshSamples = checked(freshSamples+added);
                    AppendTail(freshTail,ref freshTailCount,raw,skip,added,plan.WindowSamples);
                    // Never let Parse's per-batch window substitute old samples
                    // while the boundary-qualified window is still incomplete.
                    merged.WindowSampleCount = 0;
                    if (freshTailCount == plan.WindowSamples)
                        FactoryFeedbackWindow.Apply(merged,freshTail,plan.WindowSamples,plan.WindowSamples,plan.RequiredVoltageChannel);
                }
                uint pending = source.Available();
                long elapsed = elapsedMilliseconds() - start;
                merged.BacklogBefore = (int)firstQueue; merged.BacklogAfter = checked((int)pending);
                merged.ReadBatchCount = batch + 1; merged.ReadElapsedMs = elapsed;
                if (elapsed >= readBudgetMs)
                    throw new InvalidOperationException("ART 本次反馈读取耗时=" + elapsed + " ms，组=" + (batch + 1) +
                        "，未读=" + pending + "；超过"+readBudgetMs+" ms，不使用该反馈升压。",lastPending);
                if (pending <= 20 && (!requireFreshWindow || freshTailCount == plan.WindowSamples))
                {
                    // The residual queue limit has NOT been widened. Only the
                    // missing drain operation is added before applying it.
                    if (requireEmergency && !source.EmergencyReleased()) throw new FactoryEmergencyStopException();
                    if (requireEmergency && merged.WindowSampleCount != plan.WindowSamples)
                        throw new InvalidOperationException("反馈窗口未满，不能退回单点升压。");
                    merged.TimestampUtc = DateTime.UtcNow.AddMilliseconds(-pending * 1000 / FactoryArtFeedbackPlan.RateHz);
                    if (requireFreshWindow)
                    {
                        merged.FreshWindowAfterAoCommand = true;
                        merged.FreshWindowBoundaryQueued = checked((int)firstQueue);
                        merged.FreshWindowSamplesAfterBoundary = freshSamples;
                    }
                    return merged;
                }
            }
            throw new InvalidOperationException("ART 追读8组后仍积压：未读=" + merged.BacklogAfter +
                "，耗时=" + merged.ReadElapsedMs + " ms；未得到及时反馈，禁止继续升压。");
        }
        private static void CheckReadBudget(long start,Func<long> elapsedMilliseconds,CancellationToken cancellationToken,int readBudgetMs,
            FactoryAnalogReadPendingException pending=null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(elapsedMilliseconds()-start>=readBudgetMs)
                throw new InvalidOperationException("ART 追读缓存超过"+readBudgetMs+" ms；未得到及时反馈，禁止继续升压。",pending);
        }
        private static void AppendTail(double[] tail, ref int tailCount, double[] raw, int offset, int count, int window)
        {
            if (count >= window)
            {
                Array.Copy(raw,(offset+count-window)*3,tail,0,window*3);
                tailCount = window;
                return;
            }
            int keep = Math.Min(tailCount,window-count);
            if (keep > 0) Array.Copy(tail,(tailCount-keep)*3,tail,0,keep*3);
            if (count > 0) Array.Copy(raw,offset*3,tail,keep*3,count*3);
            tailCount = keep+count;
        }
        private static FactoryFeedback Merge(FactoryFeedback old, FactoryFeedback next)
        {
            next.SampleCount = checked(old.SampleCount + next.SampleCount);
            next.ImonMinimumRawV = Math.Min(old.ImonMinimumRawV,next.ImonMinimumRawV);
            next.ImonMaximumRawV = Math.Max(old.ImonMaximumRawV,next.ImonMaximumRawV);
            next.VmonMinimumRawV = Math.Min(old.VmonMinimumRawV,next.VmonMinimumRawV);
            next.VmonMaximumRawV = Math.Max(old.VmonMaximumRawV,next.VmonMaximumRawV);
            next.DividerMinimumRawV = Math.Min(old.DividerMinimumRawV,next.DividerMinimumRawV);
            next.DividerMaximumRawV = Math.Max(old.DividerMaximumRawV,next.DividerMaximumRawV);
            return next;
        }
    }

    // AI-only configuration shared by the feedback adapter and read-only diagnostic.
    // Coercion checks run after COMMIT, not merely against requested values.
    internal static class FactoryArtFeedbackNative
    {
        internal static FactoryArtFeedbackPlan Configure(IntPtr task, FactorySettings s, Action<int,string> check)
        {
            var plan = new FactoryArtFeedbackPlan(s);
            string[] channels = { "ai0", "ai2", "ai4" };
            double[] ranges = { plan.ImonRangeV, plan.VmonRangeV, plan.DividerRangeV };
            for (int i = 0; i < channels.Length; i++)
                check(InputOnlyArtNative.CreateAi(task, s.Input.ArtDevice + "/" + channels[i], "", 10083,
                    i<2?0:-ranges[i], ranges[i], 10348, null), "配置 " + channels[i] + " RSE 监测通道 " +(i<2?"0～":"±")+ ranges[i] + " V");
            check(InputOnlyArtNative.ConfigureClock(task, "", FactoryArtFeedbackPlan.RateHz, 10280, 10123, 1000),
                "配置 ART 三通道 1000 S/s/通道连续监测");
            check(InputOnlyArtNative.TaskControl(task, 2), "验证 ART 连续反馈采样");
            check(InputOnlyArtNative.TaskControl(task, 3), "提交 ART 连续反馈采样");
            for (int i = 0; i < channels.Length; i++)
            {
                string physical = s.Input.ArtDevice + "/" + channels[i]; double low, high;
                check(GetAiMinimum(task, physical, out low), "回读 " + physical + " 输入下限");
                check(GetAiMaximum(task, physical, out high), "回读 " + physical + " 输入上限");
                ValidateRange(low,high,i<2?0:-ranges[i],ranges[i]);
            }
            double rate;
            check(InputOnlyArtNative.GetTimingRate(task, 0x1344, out rate, sizeof(double)), "回读 ART 监测时钟");
            if (!FactorySettings.Finite(rate) || Math.Abs(rate - FactoryArtFeedbackPlan.RateHz) > .5)
                throw new InvalidOperationException("ART 监测实际时钟与请求不一致。");
            return plan;
        }
        internal static void VerifyDividerRange(IntPtr task,string channel,double range,Action<int,string> check)
        {
            double low,high;
            check(GetAiMinimum(task,channel,out low),"回读分压器输入下限");
            check(GetAiMaximum(task,channel,out high),"回读分压器输入上限");
            ValidateRange(low,high,-range,range);
        }
        internal static void ValidateRange(double low,double high,double requestedLow,double requestedHigh)
        {
            if(!FactorySettings.Finite(low)||!FactorySettings.Finite(high)||Math.Abs(low-requestedLow)>1e-6||Math.Abs(high-requestedHigh)>1e-6)
                throw new InvalidOperationException("ART提交后的输入量程与请求不一致；请核对设备支持范围，不使用未确认量程。");
        }
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetAIMin", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int GetAiMinimum(IntPtr task, string channel, out double value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("HvxAcquisitionNative.dll", EntryPoint = "ArtDAQ_GetAIMax", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private static extern int GetAiMaximum(IntPtr task, string channel, out double value);
    }

    internal static class FactoryFeedbackBounds
    {
        internal static double Minimum(double last, double minimum, int count) { return count > 0 ? minimum : last; }
        internal static double Maximum(double last, double maximum, int count) { return count > 0 ? maximum : last; }
        internal static double Absolute(double last, double minimum, double maximum, int count)
        { return Math.Max(Math.Abs(Minimum(last, minimum, count)), Math.Abs(Maximum(last, maximum, count))); }
        internal static double DividerAbs(FactoryFeedback f)
        { return Absolute(f.DividerRawV, f.DividerMinimumRawV, f.DividerMaximumRawV, f.SampleCount) + f.DividerHalfCodeV; }
        internal static double VmonAbs(FactoryFeedback f)
        { return Absolute(f.VmonRawV, f.VmonMinimumRawV, f.VmonMaximumRawV, f.SampleCount) + f.VmonHalfCodeV; }
        internal static double ImonAbs(FactoryFeedback f)
        { return Absolute(f.ImonRawV, f.ImonMinimumRawV, f.ImonMaximumRawV, f.SampleCount) + f.ImonHalfCodeV; }
    }
}
