using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryCapturePreviewTests
    {
        internal static string Run()
        {
            int n = 0;
            Action<bool,string> check = (value,text) => { if(!value) throw new Exception("R24: " + text); ++n; };
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var f = new FactoryTests.Fixture(); f.Settings.DischargeTimeoutSeconds = .12;
                if (mode == 1) f.Backend.FeedbackRule = (b,x) => { if (b.ReadCount > 0) x.DividerRawV = .05; return x; };
                int notices = 0; bool residualAfterNotice = false; FactoryResult snapshot = null;
                FactoryResult result = null; FactoryRunException fault = null;
                try
                {
                    result = FactoryRunner.Run(f.Settings, FactoryRunKind.Pressure, f.Backend, f.Clock,
                        (key,text) => { if (key == "RESIDUAL") residualAfterNotice = notices == 1; }, f.Cancellation.Token, r =>
                        {
                            ++notices; snapshot = FactoryCapturePreview.Snapshot(r);
                            check(f.Backend.ReadCount == 1 && !f.Backend.Hv && !f.Backend.Optical && f.Backend.Ao == 0, "preview after read/shutdown");
                            check(f.Backend.Events.Last().Name == "SHUTDOWN", "publish after shutdown call");
                            check(!r.Completed && !r.ResidualSafe && !snapshot.CompleteCaptureAvailable(), "preview cannot pass round");
                            if (mode == 2) throw new IOException("preview channel unavailable");
                        });
                }
                catch (FactoryRunException ex) { fault = ex; }
                check(notices == 1 && residualAfterNotice, "publish once before residual reads");
                check(f.Backend.OpticalHighCount == 1 && f.Backend.HvHighCount == 1, "no extra light or pressure");
                check(mode == 1 ? fault != null && !fault.PartialResult.ResidualSafe : result != null && result.Completed, "residual/fault unaffected by preview error");
                check(snapshot != null && !snapshot.Completed && !snapshot.ResidualSafe, "snapshot not mutated by later success");
                check(snapshot.Settings != f.Settings && snapshot.Ramp.Count == 0, "copy settings; no concurrently modified feedback lists");
            }
            {
                var f = new FactoryTests.Fixture(); f.Backend.FailRead = true; int notices = 0;
                try { FactoryRunner.Run(f.Settings, FactoryRunKind.Pressure, f.Backend, f.Clock, (k,t)=>{}, f.Cancellation.Token, r=>++notices); }
                catch (FactoryRunException) { }
                check(notices == 0, "no preview on incomplete read");
            }
            string directory = Path.Combine(Path.GetTempPath(), "Xiongxin-R24-preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new FactoryTests.Fixture(); var completed = fixture.Run();
            double voltage = completed.Capture.VoltageRaw[0]; short adc = completed.Capture.PdAdc[0];
            var events = new List<string>();
            using (var publisher = new FactoryCapturePublisher(directory, (k,v)=>events.Add(k))) publisher.Queue(completed);
            check(events.SequenceEqual(new[] { "CAPTURE_READY_FILE" }), "atomic snapshot then ready event");
            string previewDirectory = Path.Combine(directory,"capture-preview");
            var preview = FactoryCapturePreview.Load(previewDirectory);
            check(preview.Detected.OutputCount > 0 && preview.Result.Capture.PdAdc.Length == completed.Capture.PdAdc.Length, "preview reload complete data");
            check(!preview.Result.CompleteCaptureAvailable() && !preview.Result.ResidualSafe && !preview.Result.Completed, "reloaded data not accepted");
            check(completed.Completed && completed.ResidualSafe && completed.Capture.VoltageRaw[0] == voltage && completed.Capture.PdAdc[0] == adc, "original data and status unchanged");
            check(!File.Exists(Path.Combine(directory,"result.xml")), "preview not written as final success");
            check(FactoryCapturePreview.BelongsTo(directory, previewDirectory), "active preview allowed");
            check(!FactoryCapturePreview.BelongsTo(directory, directory+"-old"), "different round rejected");
            check(!FactoryCapturePreview.BelongsTo(directory, Path.Combine(directory,"..","other")), "parent traversal rejected");
            check(FactoryCapturePreview.Matches(completed.Settings,completed.Kind,preview.Result), "identity match");
            var changed = completed.Settings.Clone(); changed.TargetKv += 1;
            check(!FactoryCapturePreview.Matches(changed,completed.Kind,preview.Result), "wrong target rejected");
            check(!FactoryCapturePreview.Matches(completed.Settings,FactoryRunKind.Calibration,preview.Result), "wrong kind rejected");
            using (var publisher = new FactoryCapturePublisher(Path.Combine(directory,"report-fault"), (k,v)=>{throw new IOException("report");})) publisher.Queue(completed);
            check(true, "report failure cannot fault shutdown thread");
            return "R24 capture preview: " + n + " checks PASS; HardwareAccessed=False";
        }
    }
}
