using System;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    // Idle-only worker. This type cannot create AO/DO, reset a device or enable HV.
    // It must exit before an acquisition/diagnostic worker is allowed to own DI.
    internal static class FactoryConnectionWatch
    {
        internal static void Run(BenchSettings settings, Action<string,string> report, CancellationToken token,
            IP04ReadOnlyNative injected=null,Func<DiscoveryResult> discover=null)
        {
            FactoryP04Diagnostic.Validate(settings);
            var native = injected ?? new P04ReadOnlyNative();
            native.Load(settings.ArtLibraryDirectory);
            string name = null, lastArt = null, lastPico = null;
            DateTime enumerateAt = DateTime.MinValue, reportAt = DateTime.MinValue;
            bool picoPresent = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (DateTime.UtcNow >= enumerateAt)
                    {
                        var found = discover==null?DeviceDiscovery.Run(settings, null, token):discover();
                        var selection = DiscoverySelection.Resolve(found, settings.ArtSerial, settings.PicoSerial);
                        string current = selection.ArtDevice == null ? null : selection.ArtDevice.Name;
                        if (current != name) { native.CloseLine(); name = null; }
                        if (current != null && name == null)
                        {
                            try
                            {
                                if (native.Identity(current) != "USB3135 / " + settings.ArtSerial) throw new InvalidOperationException("ART身份不匹配");
                                native.OpenLine(current, 0); name = current;
                                report("LINK_ART_NAME", current);
                            }
                            catch (Exception ex) { report("LINK_ART_ERROR", Stamp(ex.Message)); native.CloseLine(); name = null; }
                        }
                        picoPresent = selection.PicoSerial == settings.PicoSerial;
                        string ps = picoPresent ? "ONLINE" : "ERROR";
                        // A healthy Pico keeps its own heartbeat even when ART is absent.
                        if (picoPresent || ps != lastPico) report("LINK_PICO_" + ps, Stamp(picoPresent ? "已枚举到原Pico；未打开采集连接" : selection.PicoMessage + "；" + found.PicoError));
                        lastPico = ps;
                        if (current == null && lastArt != "ERROR") report("LINK_ART_ERROR", Stamp(selection.ArtMessage + "；" + found.ArtError));
                        if (current == null) lastArt = "ERROR";
                        enumerateAt = DateTime.UtcNow.AddSeconds(2);
                    }
                    if (name != null)
                    {
                        try
                        {
                            var raw = native.ReadLine(); string state = FactoryP04Diagnostic.LineState(raw);
                            if (state == "无效/未知") throw new InvalidOperationException("P0.4读取无效，status=" + raw.Status + ", samples=" + raw.Samples + ", bytes=" + raw.Bytes + ", raw=" + raw.Raw);
                            string key = raw.Raw == 1 ? "HIGH" : "LOW";
                            if (lastArt != key || DateTime.UtcNow >= reportAt)
                            {
                                report("LINK_ART_" + key, Stamp("P0.4只读实时值"));
                                if (picoPresent) report("LINK_PICO_ONLINE", Stamp("原Pico枚举身份已核对（最多2秒刷新）"));
                                reportAt = DateTime.UtcNow.AddMilliseconds(500);
                            }
                            lastArt = key;
                        }
                        catch (Exception ex)
                        {
                            report("LINK_ART_ERROR", Stamp(ex.Message)); lastArt = "ERROR";
                            native.CloseLine(); name = null; enumerateAt = DateTime.UtcNow.AddSeconds(2);
                        }
                    }
                    token.WaitHandle.WaitOne(100);
                }
            }
            finally { native.CloseLine(); }
        }
        internal static string Stamp(string detail) { return DateTime.UtcNow.ToString("o") + "|" + detail; }
    }
}
