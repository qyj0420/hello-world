using System;
using System.IO;
using System.Threading;
using Xiongxin.Owts.Contracts.Ui;
using Xiongxin.Owts.XpertPressBridge;

namespace Xiongxin.Owts.XpertPressBridge.Smoke
{
    static class Program
    {
        static int Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "xpert-factory-press-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string opened = XpertPressHardware.OpenForXpert("Dev1", "FAKE-PICO", true);
                if (opened.IndexOf(StatusCopy.Connected, StringComparison.Ordinal) < 0)
                    return Fail("open status missing 已连接: " + opened);
                XpertPressHardware.MarkDischargeCalibrated();
                string zero = XpertPressHardware.FormatFiveStates(0);
                if (zero.IndexOf("| " + StatusCopy.ReadyToEnergize + " |", StringComparison.Ordinal) >= 0)
                    return Fail("0 kV must not be 可加压: " + zero);
                if (zero.IndexOf(StatusCopy.Calibrated, StringComparison.Ordinal) < 0)
                    return Fail("missing 已校准: " + zero);
                string pressed = XpertPressHardware.PollEmergencyStage();
                if (pressed != "EMERGENCY_ACTIVE" && pressed != "EMERGENCY_LATCHED_ACTIVE")
                    return Fail("FakeLive should open with e-stop pressed, got " + pressed);
                try
                {
                    XpertPressHardware.RunRoundAsync(SettingsXml(), 2.0, Path.Combine(dir, "denied"), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    return Fail("non-zero press was allowed while e-stop pressed");
                }
                catch (XpertPressFaultException ex)
                {
                    if (ex.Kind != "NotReady") return Fail("expected NotReady, got " + ex.Kind + " " + ex.Message);
                    if (ex.Message.IndexOf(StatusCopy.EStopPressed, StringComparison.Ordinal) < 0)
                        return Fail("gate text missing 急停按下: " + ex.Message);
                }

                ReleaseFakeEStop();
                string released = XpertPressHardware.PollEmergencyStage();
                if (released != "EMERGENCY_RELEASED" && released != "EMERGENCY_LATCHED_RELEASED")
                    return Fail("e-stop did not release: " + released);
                string ready = XpertPressHardware.FormatFiveStates(2);
                if (ready.IndexOf("| " + StatusCopy.ReadyToEnergize + " |", StringComparison.Ordinal) < 0)
                    return Fail("released calibrated non-zero should be 可加压: " + ready);
                if (ready.IndexOf(StatusCopy.EStopReleased, StringComparison.Ordinal) < 0)
                    return Fail("missing 急停解除: " + ready);

                string roundDir = Path.Combine(dir, "round");
                string returned = XpertPressHardware.RunRoundAsync(SettingsXml(), 2.0, roundDir, CancellationToken.None)
                    .GetAwaiter().GetResult();
                if (!File.Exists(Path.Combine(returned, "capture.bin")) || !File.Exists(Path.Combine(returned, "settings.xml")))
                    return Fail("workbench capture files missing");
                string manifest = File.ReadAllText(Path.Combine(returned, "factory-press-manifest.json"));
                if (manifest.IndexOf("\"rangeIndexUsed\"", StringComparison.Ordinal) < 0)
                    return Fail("range feed missing");
                if (manifest.IndexOf("\"locateSamplesPc\"", StringComparison.Ordinal) < 0)
                    return Fail("locate feed missing");
                if (manifest.IndexOf("locateRateHz", StringComparison.Ordinal) < 0)
                    return Fail("locate rate missing");
                string after = XpertPressHardware.FormatFiveStates(2);
                Console.WriteLine(after);
                Console.WriteLine("SMOKE_OK " + returned);
                XpertPressHardware.CloseSessionAsync().GetAwaiter().GetResult();
                if (XpertPressHardware.HasDeviceSession) return Fail("session still open after close");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        static void ReleaseFakeEStop()
        {
            // FakeLive e-stop is on the session. The bridge does not expose a press-time setter;
            // the smoke reaches the same session through OwtsServices only via reflection on the
            // acquisition object stored by a dedicated internal hook.
            var field = typeof(XpertPressHardware).GetField("_services", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object services = field.GetValue(null);
            var acquisition = services.GetType().GetProperty("Acquisition").GetValue(services, null);
            acquisition.GetType().GetMethod("SetEStopPressed").Invoke(acquisition, new object[] { false });
        }

        static string SettingsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<BenchSettings>"
                + "<RealHardware>true</RealHardware>"
                + "<ArtSerial>Dev1</ArtSerial>"
                + "<PicoSerial>FAKE-PICO</PicoSerial>"
                + "<RequestedPicoRateHz>125000000</RequestedPicoRateHz>"
                + "<ResolutionBits>14</ResolutionBits>"
                + "<CaptureDurationMs>1</CaptureDurationMs>"
                + "<RangeIndex>6</RangeIndex>"
                + "<AutoRange>true</AutoRange>"
                + "<PdCalibrationPcPerV>2000</PdCalibrationPcPerV>"
                + "</BenchSettings>";
        }

        static int Fail(string message)
        {
            Console.Error.WriteLine("SMOKE_FAIL " + message);
            return 2;
        }
    }
}
