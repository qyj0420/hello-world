using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Xiongxin.OWTS.Desktop
{
    // Correlates a detected state transition with a small, relevant slice of the
    // Windows event logs. Temporal proximity is evidence, never proof of cause.
    internal static class HardwareDisconnectDiagnostics
    {
        internal static void RecordTransition(HardwareStatusSnapshot before, HardwareStatusSnapshot after)
        {
            if (after == null || before == null || before.Ready == after.Ready) return;
            var diagnostics = new Diagnostics();
            string transition = before.Ready ? "DISCONNECTED" : "RECONNECTED";
            diagnostics.Warning("HardwareTimeline", transition + "; UTC=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                "; Previous=" + Safe(before.StatusCode) + "; Current=" + Safe(after.StatusCode) +
                "; Hub=" + Safe(after.HubMac) + "; Controller=" + Safe(after.ControllerSerial) +
                "; Acquisition=" + Safe(after.AcquisitionSerial) + "; Diagnostic=" + Safe(after.Diagnostic));
            if (!before.Ready) return;
            HardwareStatusSnapshot snapshot = before.Clone();
            Task.Run(delegate { CorrelateWindowsEvents(snapshot, DateTime.Now); });
        }

        private static void CorrelateWindowsEvents(HardwareStatusSnapshot device, DateTime incidentLocal)
        {
            var diagnostics = new Diagnostics();
            int matches = 0;
            foreach (string logName in new[] { "System", "Application" })
            {
                try
                {
                    using (var log = new EventLog(logName))
                    {
                        EventLogEntryCollection entries = log.Entries;
                        int inspected = 0;
                        for (int i = entries.Count - 1; i >= 0 && inspected < 2000 && matches < 40; i--, inspected++)
                        {
                            EventLogEntry entry = entries[i];
                            if (entry.TimeGenerated > incidentLocal.AddMinutes(1)) continue;
                            if (entry.TimeGenerated < incidentLocal.AddMinutes(-5)) break;
                            string source = entry.Source ?? String.Empty;
                            string message = entry.Message ?? String.Empty;
                            if (!Relevant(source, message, device)) continue;
                            string category = Category(source, message);
                            diagnostics.Info("WindowsEventCorrelation", "Possible " + category + " event; Log=" + logName +
                                "; LocalTime=" + entry.TimeGenerated.ToString("O", CultureInfo.InvariantCulture) +
                                "; Source=" + Safe(source) + "; EventID=" + (entry.InstanceId & 0xffff).ToString(CultureInfo.InvariantCulture) +
                                "; Message=" + Safe(message));
                            matches++;
                        }
                    }
                }
                catch (Exception error)
                {
                    diagnostics.Warning("WindowsEventCorrelation", "Could not read " + logName + " event log: " + error.GetType().Name + ": " + error.Message);
                }
            }
            diagnostics.Info("WindowsEventCorrelation", "Relevant Windows events within the five minutes before disconnect: " +
                matches.ToString(CultureInfo.InvariantCulture) + ". No match does not exclude driver, software, cable, power or hardware faults.");
        }

        private static bool Relevant(string source, string message, HardwareStatusSnapshot device)
        {
            string combined = source + " " + message;
            foreach (string name in new[] { "PicoScope", "Pico Technology", "ps5000a", "ART-DAQ", "Art_DAQ", "HV-DAC-Xpert", "Xiongxin.OWTS" })
                if (combined.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (string serial in new[] { device.ControllerSerial, device.AcquisitionSerial })
                if (!String.IsNullOrWhiteSpace(serial) && serial.Length >= 6 &&
                    combined.IndexOf(serial, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string Category(string source, string message)
        {
            string text = source + " " + message;
            if (text.IndexOf("Application Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf(".NET Runtime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) >= 0) return "application/crash";
            if (text.IndexOf("driver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("USB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("PnP", StringComparison.OrdinalIgnoreCase) >= 0) return "driver/USB";
            return "device-related";
        }

        private static string Safe(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length <= 1500 ? compact : compact.Substring(0, 1500) + "…";
        }
    }
}
