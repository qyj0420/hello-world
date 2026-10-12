using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Threading;

namespace HVX.Controls
{
    /// <summary>
    /// Reads the factory-press manifest written beside capture.bin.
    /// Location samples are the factory peak-hold extract (Fs target 80 kHz, floor 20 kHz),
    /// then the existing 10 kHz high-pass. The E+ save path is not replaced.
    /// </summary>
    static class FactoryPressLocateFeed
    {
        public static double[] SamplesForSave(PressureZeroVoltageResult result)
        {
            double[] extracted;
            double rate;
            if (result == null || result.Capture == null || !TryRead(result.Directory, out extracted, out rate))
                return result == null ? new double[0] : result.ChargePc;
            return HVX.SignalProcessing.PdLinearHighPass.ApplyCancellable(extracted, rate, CancellationToken.None);
        }

        public static double RateForSave(PressureZeroVoltageResult result)
        {
            double[] extracted;
            double rate;
            if (result == null || result.Capture == null || !TryRead(result.Directory, out extracted, out rate))
                return result == null || result.Capture == null ? 0 : result.Capture.PicoRateHz;
            return rate;
        }

        public static string FilterForSave(PressureZeroVoltageResult result)
        {
            double[] extracted;
            double rate;
            if (result != null && result.Capture != null && TryRead(result.Directory, out extracted, out rate))
                return HVX.SignalProcessing.PdLinearHighPass.FilterId;
            return result == null ? "" : result.HighPassFilterId;
        }

        static bool TryRead(string directory, out double[] samples, out double rateHz)
        {
            samples = null;
            rateHz = 0;
            if (String.IsNullOrWhiteSpace(directory)) return false;
            string path = Path.Combine(directory, "factory-press-manifest.json");
            if (!File.Exists(path)) return false;
            string text = File.ReadAllText(path);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            var root = serializer.Deserialize<Dictionary<string, object>>(text);
            if (root == null || !root.ContainsKey("locateSamplesPc") || !root.ContainsKey("locateRateHz")) return false;
            rateHz = Convert.ToDouble(root["locateRateHz"], CultureInfo.InvariantCulture);
            var list = root["locateSamplesPc"] as ArrayList;
            if (list == null || list.Count == 0 || !(rateHz >= 20000)) return false;
            samples = new double[list.Count];
            for (int i = 0; i < list.Count; i++)
                samples[i] = Convert.ToDouble(list[i], CultureInfo.InvariantCulture);
            return true;
        }
    }

    public sealed partial class PressureTestControl
    {
        string factoryHardwareStatusLine = "";

        public void SetFactoryHardwareStatusLine(string line)
        {
            string next = line ?? "";
            RunOnUiThread(delegate
            {
                if (String.Equals(factoryHardwareStatusLine, next, StringComparison.Ordinal)) return;
                factoryHardwareStatusLine = next;
                // Only paint the status label — full UpdatePressureUi would rebuild
                // plan/round chrome on every PollEmergencyStage tick and flash LIVE UI.
                if (connectionStatusLabel != null)
                {
                    connectionStatusLabel.Text = !String.IsNullOrEmpty(factoryHardwareStatusLine)
                        ? factoryHardwareStatusLine
                        : SimulationMode ? PressureUiText("仿真 · 非实测", "Simulation · Not measured")
                        : HighVoltageStatusCode == "UNKNOWN" ? PressureUiText("设备反馈待确认", "Device feedback unconfirmed")
                        : PressureUiText("设备反馈已接收", "Device feedback received");
                    connectionStatusLabel.BackColor = SimulationMode || HighVoltageStatusCode == "UNKNOWN"
                        ? PressureTestTheme.SecondaryInk : PressureTestTheme.Success;
                }
            });
        }
    }
}
