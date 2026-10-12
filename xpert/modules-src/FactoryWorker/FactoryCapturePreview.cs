using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // A display-only copy. Never assign this to the accepted/current result.
    internal sealed class FactoryCapturePreview
    {
        internal FactoryResult Result;
        internal DetectedWaveformResult Detected;
        internal static FactoryResult Snapshot(FactoryResult source)
        {
            if (source == null || source.Settings == null || source.Capture == null)
                throw new InvalidDataException("没有完整采集可供预览。");
            source.Capture.ValidateCapture();
            return new FactoryResult { Settings = source.Settings.Clone(), Kind = source.Kind,
                Capture = source.Capture, ReadOnlyAcquisition = source.ReadOnlyAcquisition,
                ZeroTriggerDiagnostic = source.ZeroTriggerDiagnostic, Completed = false, ResidualSafe = false,
                PostCaptureMonitoringSkipped = source.PostCaptureMonitoringSkipped,
                TriggerRecovery = source.TriggerRecovery,
                SafetyDetail = "采集完整性已校验；仅波形查看，不代表本轮合格或设备已放电。" };
        }
        internal static FactoryCapturePreview Load(string directory)
        {
            string metadata = Path.Combine(directory, "partial-result.xml");
            if (!File.Exists(metadata)) metadata = Path.Combine(directory, "result.xml");
            FactoryResult saved;
            using (var stream = File.OpenRead(metadata))
                saved = (FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(stream);
            saved.Capture = BenchCapture.Load(directory);
            if (saved.Settings == null || saved.Settings.Simulation != saved.Capture.Simulation ||
                saved.Settings.Input.ArtSerial != saved.Capture.Settings.ArtSerial ||
                saved.Settings.Input.PicoSerial != saved.Capture.Settings.PicoSerial)
                throw new InvalidDataException("波形与记录的设备/来源不一致，拒绝预览。");
            var result = Snapshot(saved); result.DirectoryPath = Path.GetFullPath(directory);
            return new FactoryCapturePreview { Result = result, Detected = FactoryDetectedWaveforms.Build(result.Capture) };
        }
        internal static bool BelongsTo(string activeDirectory, string directory)
        {
            if (String.IsNullOrWhiteSpace(activeDirectory) || String.IsNullOrWhiteSpace(directory)) return false;
            string active = Path.GetFullPath(activeDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            return String.Equals(active, candidate, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(Path.Combine(active, "capture-preview"), candidate, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool Matches(FactorySettings settings, FactoryRunKind kind, FactoryResult result)
        {
            return settings != null && result != null && result.Settings != null && result.Kind == kind &&
                settings.TargetKv == result.Settings.TargetKv && settings.Simulation == result.Settings.Simulation &&
                settings.Input.ArtSerial == result.Settings.Input.ArtSerial && settings.Input.PicoSerial == result.Settings.Input.PicoSerial;
        }
    }

    internal sealed class FactoryCapturePublisher : IDisposable
    {
        private readonly string directory;
        private readonly Action<string,string> report;
        private Task publication;
        internal FactoryCapturePublisher(string directory, Action<string,string> report)
        { this.directory = directory; this.report = report; }
        internal void Queue(FactoryResult source)
        {
            if (publication != null) throw new InvalidOperationException("每轮只能发布一次采集预览。");
            var snapshot = FactoryCapturePreview.Snapshot(source);
            // Arrays are completed before Queue and no longer written by the backend.
            // Use a separate directory so final result/cleanup can save independently.
            publication = Task.Run(() =>
            {
                try
                {
                    string target = Path.Combine(directory, "capture-preview");
                    Directory.CreateDirectory(target); snapshot.DirectoryPath = target;
                    Program.SaveResult(snapshot, target, true);
                    report("CAPTURE_READY_FILE", target);
                }
                catch (Exception ex)
                { try { report("CAPTURE_PREVIEW_ERROR", "预览保存失败；不改变关断/残压流程：" + ex.Message); } catch { } }
            });
        }
        public void Dispose()
        { if (publication != null) publication.GetAwaiter().GetResult(); }
    }
}
