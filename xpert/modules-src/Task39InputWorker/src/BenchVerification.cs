using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Xiongxin.ZeroBench
{
    // Explicit CLI commissioning path; never called at startup or by offline tests.
    internal static class BenchVerification
    {
        internal static int CaptureOnce(string settingsPath, string reportPath)
        {
            string path = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var log = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                object gate = new object();
                try
                {
                    var s = BenchSettings.Load(settingsPath);
                    s.Validate();
                    if (!s.RealHardware || !s.AutomaticSharedTrigger)
                        throw new ArgumentException("此入口仅用于操作者明确授权的单次真实 P0.7 共享 TRG 验证。");
                    log.WriteLine("ONE_SHOT_TRG_ONLY; NO_AO; NO_HV; ART=" + s.ArtSerial + "; PICO=" + s.PicoSerial); log.Flush();
                    var c = BenchRunner.RunAsync(s, (stage, message) =>
                    {
                        lock (gate) { log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " | " + stage + " | " + message); log.Flush(); }
                    }, CancellationToken.None).GetAwaiter().GetResult();
                    c.ValidateCapture();
                    log.WriteLine("CAPTURE_CHECK=PASS; ART=" + c.VoltageRaw.Length + "; PICO=" + c.PdAdc.Length);
                    log.WriteLine("RECORD_DIRECTORY=" + c.DirectoryPath);
                    return 0;
                }
                catch (Exception ex) { lock (gate) log.WriteLine("CAPTURE_CHECK=FAIL\n" + ex); return 1; }
            }
        }
    }
}
