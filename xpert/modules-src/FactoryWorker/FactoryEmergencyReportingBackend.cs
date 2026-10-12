using System;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Observe safety reads (native backend consumes the independent DI cache).
    // The UI never polls the SDK or derives permission from a lamp.
    internal sealed class FactoryEmergencyReportingBackend : IFactoryBackend, IFactoryFaultMonitor, IFactoryOpticalRecovery, IFactoryFinalAi4Observation
    {
        private readonly IFactoryBackend inner;
        private readonly Action<string, string> report;
        private readonly bool simulation;
        private readonly Action<FactoryFeedback> feedbackObserver;
        private bool? previous;
        private DateTime lastReport;
        internal FactoryEmergencyReportingBackend(IFactoryBackend inner, Action<string,string> report, bool simulation, Action<FactoryFeedback> feedbackObserver = null)
        { this.inner = inner; this.report = report; this.simulation = simulation; this.feedbackObserver = feedbackObserver; }
        private void Observe(bool released)
        {
            if (previous != released || DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(500))
            {
                previous = released; lastReport = DateTime.UtcNow;
                report(released ? "EMERGENCY_RELEASED" : "EMERGENCY_ACTIVE",
                    lastReport.ToString("O",System.Globalization.CultureInfo.InvariantCulture) + "|" +
                    (simulation ? "仿真 · " : "实机读取 · ") + "P0.4 " + (released ? "高，急停解除" : "低，急停/联锁不许可") + "；不是完整加压许可。");
            }
        }
        public void Initialize(FactorySettings settings, bool calibration) { inner.Initialize(settings, calibration); }
        public FactoryFeedback ReadFeedback()
        {
            try { var value = inner.ReadFeedback(); Observe(value.EmergencyReleased); if (feedbackObserver != null) feedbackObserver(value); return value; }
            catch
            {
                // A cache-drain failure may itself be an emergency latch. Show
                // the retained low without another driver read or masking the
                // original analog/DI failure with a reporting exception.
                try { Observe(inner.ReadEmergencyReleased()); } catch { }
                throw;
            }
        }
        public bool ReadEmergencyReleased() { bool value = inner.ReadEmergencyReleased(); Observe(value); return value; }
        public void WriteAo(double volts) { inner.WriteAo(volts); }
        public void SetHv(bool on) { inner.SetHv(on); }
        public void SetOptical(bool on) { inner.SetOptical(on); }
        public void Arm(bool freeRun) { inner.Arm(freeRun); }
        public void ArmBeforeShutdown() { inner.ArmBeforeShutdown(); }
        public bool ArtComplete { get { return inner.ArtComplete; } }
        public bool PicoComplete { get { return inner.PicoComplete; } }
        public BenchCapture ReadCapture() { return inner.ReadCapture(); }
        public void StopCapture() { inner.StopCapture(); }
        public void PrepareOpticalRetry()
        {
            var recovery=inner as IFactoryOpticalRecovery;
            if(recovery==null)throw new InvalidOperationException("当前后端不支持双卡隔离后的光补发。");
            recovery.PrepareOpticalRetry();
        }
        public void Shutdown() { inner.Shutdown(); }
        public FactoryFinalAi4Observation ReadFinalAi4Observation(System.Threading.CancellationToken token)
        {
            var reader=inner as IFactoryFinalAi4Observation;
            if(reader==null)throw new InvalidOperationException("当前后端不支持末轮AI4独立均值观察。");
            return reader.ReadFinalAi4Observation(token);
        }
        public void Dispose() { inner.Dispose(); }
        public void PrepareFaultMonitoring()
        {
            var monitor=inner as IFactoryFaultMonitor;
            if(monitor==null)throw new InvalidOperationException("当前后端不支持故障只读监测；残压未知。");
            monitor.PrepareFaultMonitoring();
        }
        public FactoryFeedback ReadFaultFeedback()
        {
            var monitor=inner as IFactoryFaultMonitor;
            if(monitor==null)throw new InvalidOperationException("当前后端不支持故障只读监测；残压未知。");
            var value=monitor.ReadFaultFeedback();
            // Do not report the diagnostic's unused emergency flag as a DI read.
            if(value!=null && feedbackObserver!=null)feedbackObserver(value);
            return value;
        }
    }
}
