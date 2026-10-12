using System;
using System.Collections.Generic;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed class FactoryManualEngine : IDisposable
    {
        private IFactoryBackend backend;
        private readonly Action<bool> indicator;
        private readonly IFactoryClock clock;
        private readonly Action<string,string> report;
        private readonly CancellationToken token;
        private FactorySettings settings;
        private FactoryFeedbackTrendPublisher trend;
        private bool initialized, hv, ramped, armed, closed, hvAttempted, capturedNormally;
        private long lastTelemetry = -1000, hvStarted;
        internal FactoryResult Result { get; private set; }
        internal FactoryManualEngine(IFactoryBackend backend, Action<bool> indicator, IFactoryClock clock,
            Action<string,string> report, CancellationToken token)
        {
            this.clock = clock; this.report = report; this.token = token; this.indicator = indicator;
            this.backend = backend;
        }
        private void Feedback(FactoryFeedback f)
        {
            trend.Observe(f);
            if (clock.ElapsedMilliseconds - lastTelemetry < 100) return;
            lastTelemetry = clock.ElapsedMilliseconds;
            report("TELEMETRY", "窗口VMON " + (FactoryFeedbackWindow.Vmon(f) * settings.VmonFullScaleKv / settings.FeedbackFullScaleV).ToString("F3") +
                " kV | 分压器幅值 " + Math.Abs(FactoryFeedbackWindow.Divider(f) * settings.Input.DividerVPerV / 1000).ToString("F3") +
                " kV | IMON趋势 " + (FactoryFeedbackWindow.Imon(f) * settings.ImonFullScaleMa / settings.FeedbackFullScaleV).ToString("F3") +
                " mA | AO " + f.AoV.ToString("F4") + " V\n原始 AI0/2/4: " +
                (f.ImonRawV * 1000).ToString("F3") + " / " + (f.VmonRawV * 1000).ToString("F3") + " / " +
                (f.DividerRawV * 1000).ToString("F3") + " mV；缓存 " + f.BacklogBefore + "→" + f.BacklogAfter + " 点。");
        }
        internal void Initialize(FactorySettings requested)
        {
            if (initialized || closed) throw new InvalidOperationException("手动会话不能重复初始化。");
            settings = requested.Clone(); settings.MaximumKv = settings.TargetKv = 5;settings.PressureFeedbackContext=true;
            settings.Validate(FactoryRunKind.Pressure);
            trend = new FactoryFeedbackTrendPublisher(settings,clock,report);
            backend = new FactoryEmergencyReportingBackend(backend,report,settings.Simulation,Feedback);
            backend.Initialize(settings,false); initialized = true;
            backend.Shutdown(); // AO=0/optical LOW, native skips HV OFF until attempted ON.
            Result = new FactoryResult { Settings = settings.Clone(), Kind = FactoryRunKind.Pressure,
                SafetyDetail = "手动分步验证记录；不代表自动流程通过或设备已放电。" };
            report("INIT_PASSED", "两卡初始化；AO=0、光低，未发送HV OFF。P0.4独立10 ms请求监控。VMON为正，AI4按绝对值控制、原始极性保留。等待你逐项点击。" );
        }
        private void Ready()
        { token.ThrowIfCancellationRequested(); if (!initialized || closed) throw new InvalidOperationException("请先初始化两卡。"); }
        private void Emergency()
        { if (!backend.ReadEmergencyReleased()) throw new InvalidOperationException("P0.4为低；立即停止手动会话。"); }
        internal void Tick()
        {
            Ready(); Emergency();
            if (hv && clock.ElapsedMilliseconds - hvStarted > settings.ManualHvOnTimeoutSeconds * 1000)
                throw new TimeoutException("手动HV ON保持超过独立会话时限；关断，不自动发光。");
            if (armed) { FactoryRunner.CheckUnexpectedCapture(settings,backend,clock,Result,report,token); return; }
            if(settings.SkipPressureResidualMeasurement && (!hv || !ramped))return;
            var f = FactoryRunner.ReadChecked(backend,settings);
            FactoryRunner.CheckRampVoltage(f,settings,settings.FeedbackCeilingKv*1000);
        }
        internal void HvOn(double targetKv)
        {
            Ready(); if (hv || armed || Result.Capture != null) throw new InvalidOperationException("本会话HV已启动或已采集；不自动重做，请停止后重新初始化。");
            if (targetKv == 0) { report("HV_ON_SKIPPED","目标0 kV，不启动直流源；0 kV自由采集请使用独立只读入口。"); return; }
            if (!FactorySettings.Positive(targetKv) || targetKv > 5) throw new ArgumentException("HV ON目标须大于0且不超过5 kV；0 kV请用独立只读入口。");
            settings.TargetKv = targetKv; settings.Validate(FactoryRunKind.Pressure); Result.Settings = settings.Clone();
            if(!settings.Simulation)FactoryCaptureCooldown.EnsureReady();
            report("HV_ON_RUNNING","检查真实急停；加压前残压测量已禁用，须先现场独立安全放电。之后仅启动电源，不自动升压、关源或发光。");
            FactoryRunner.WaitInitialResidual(settings,backend,clock,Result,report,token);
            Emergency(); token.ThrowIfCancellationRequested(); hvAttempted=true; backend.SetHv(true); hv = true; hvStarted = clock.ElapsedMilliseconds;
            report("HV_ON_PASSED","P0.0高调用通过；等待手动HV UP。指示灯由单独按钮控制。" );
        }
        internal void Indicator(bool on)
        { Ready(); if (on) Emergency(); indicator(on); report(on ? "LAMP_ON_PASSED" : "LAMP_OFF_PASSED","P0.3 加压指示灯=" + (on ? "高/开" : "低/关") + "，驱动调用通过，不等于物理回读。" ); }
        internal void HvUp(double targetKv)
        {
            Ready(); if (!hv || ramped || armed) throw new InvalidOperationException("HV UP需要先手动HV ON，且每个会话仅升压一次。");
            if (targetKv == 0) { HvOff(); report("HV_UP_SKIPPED","目标0 kV，AO归零并关闭已启动的直流源，不执行升压或光触发。"); return; }
            if (!FactorySettings.Positive(targetKv) || targetKv > 5) throw new ArgumentException("升压目标须大于0且不超过5 kV。");
            settings.TargetKv = targetKv; settings.Validate(FactoryRunKind.Pressure); Result.Settings = settings.Clone();
            report("HV_UP_RUNNING","AO步长 " + settings.Input.AoStepV + " V，循环请求 " + settings.Input.LoopDelayMs + " ms；" +
                (settings.DividerOnlyFeedback ? "仅|AI4|窗口中位数闭环，原始峰值保护保留；" : "双路闭环；") + "采集VMON、IMON和分压器反馈。" );
            FactoryRunner.Ramp(settings,backend,clock,Result,report,token);
            FactoryTargetHold.Verify(settings,backend,clock,Result,report,token); ramped = true;
            report("RAMP_SUMMARY",Result.DescribeRampAndHold());
            report("HV_UP_PASSED",(settings.DividerOnlyFeedback ? "|AI4分压器|" : "两路电压") + "首次有效达压后AO不再增加；"+Result.TargetHold.Detail+" 等待你手动HV OFF。不自动发光。" );
        }
        internal void HvOff()
        {
            Ready(); report("HV_OFF_RUNNING","已完成升压时开始布置两卡并同时计时；两卡就绪且累计至少500 ms后，P0.0低、AO0置零。未完成升压或发生异常则直接关断，不为关断等待布置。" );
            var failures = new List<Exception>();
            if (hv && ramped && !armed)
            {
                try
                {
                    // Never extend the energized handoff to wait for cooldown.
                    if(!settings.Simulation)FactoryCaptureCooldown.EnsureReady();
                    long armStartedMs=FactoryRunner.ArmPressureCapture(backend,clock,token); armed=true;
                    FactoryRunner.WaitArmBeforeShutdown(settings,backend,clock,Result,report,armStartedMs,token);
                }
                catch(Exception ex) { failures.Add(ex); } // never skip either disable call on arm failure
            }
            try { backend.SetHv(false); hv = false; } catch (Exception ex) { failures.Add(ex); }
            try { backend.WriteAo(0); } catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("AO归零/HV OFF失败，两项均已尝试。",failures);
            report("HV_OFF_PASSED","HV OFF → AO0=0调用通过；布置开始计时已完成，关断后不再追加500 ms。仍须手动点击光触发，不自动执行下一步。" );
        }
        internal void TriggerCapture()
        {
            Ready(); if (hv || !ramped || !armed || Result.Capture != null) throw new InvalidOperationException("先完成HV UP、两卡布置和手动HV OFF；每个会话仅允许一次光触发。");
            Emergency(); report("TRIGGER_RUNNING","两卡已在HV OFF之前完成布置及500 ms计时；再次检查急停和提前触发，随后P0.7光触发" + BenchSettings.OpticalHoldMilliseconds + " ms，不追加关断后延时。" );
            FactoryRunner.CheckUnexpectedCapture(settings,backend,clock,Result,report,token);
            token.ThrowIfCancellationRequested(); Emergency();
            long trigger = clock.ElapsedMilliseconds;
            Exception opticalError = null;
            try { backend.SetOptical(true); trigger = clock.ElapsedMilliseconds; FactoryRunner.GuardedDelay(BenchSettings.OpticalHoldMilliseconds,backend,clock,token); }
            catch (Exception ex) { opticalError = ex; }
            finally
            {
                try { backend.SetOptical(false); }
                catch (Exception ex) { opticalError = opticalError == null ? ex : new AggregateException("光触发及回低均失败。",opticalError,ex); }
            }
            if (opticalError != null) throw opticalError;
            while (true)
            {
                token.ThrowIfCancellationRequested(); Emergency();
                if (clock.ElapsedMilliseconds - trigger >= settings.Input.TriggerTimeoutMs) throw new TimeoutException("两卡采集超时；无自动触发或重发光。");
                if (backend.ArtComplete && backend.PicoComplete) break;
                clock.Pause(10,token);
            }
            Result.Capture = backend.ReadCapture(); Result.Capture.ValidateCapture();
            backend.StopCapture(); armed = false;
            Result.Metrics = FactoryAnalysis.Analyze(Result);
            report("TRIGGER_PASSED","两卡完整数据已读取，光已回低；显示80k检波与整段原始局放。不代表已放电。" );
            capturedNormally=true;
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            var failures = new List<Exception>();
            try { backend.Shutdown(); } catch (Exception ex) { failures.Add(ex); }
            if(hvAttempted && Result!=null && (!capturedNormally || failures.Count>0))
            {
                try { FactoryFaultResidual.Observe(settings,backend,clock,Result,report); }
                catch(Exception ex) { failures.Add(ex); }
            }
            try { backend.Dispose(); } catch (Exception ex) { failures.Add(ex); }
            try { if (trend != null) trend.Flush(); } catch (Exception ex) { failures.Add(ex); }
            try { if (Result != null && Result.RampStatistics.Started) report("RAMP_SUMMARY",Result.DescribeRampAndHold()); }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count != 0) throw new AggregateException("手动会话关断/清理不完整；禁止靠近设备。",failures);
        }
    }
}
