using System;
using System.IO;

namespace HVX.Controls
{
    public sealed partial class ProjectDataBrowserControl
    {
        // The browser renders LOC samples in its own detail area. Embedding the
        // Task 3 workspace would duplicate its tree, toolbar and scrollbars.
        readonly BrowserLocationPlot locationPlot = new BrowserLocationPlot();

        void InitializeLocationAnalysisView()
        {
            locationPlot.Name = "LocationWaveformPlot";
            locationPlot.MeasurementChanged += (s, e) => UpdateLocationMeasurementInformation();
        }

        void ApplyLocationAnalysisLanguage(int languageIndex)
        {
            locationPlot.LanguageIndex = languageIndex;
            UpdateLocationMeasurementInformation();
        }

        bool RenderLocationWaveform(BrowserRecord record, BrowserWaveform wave)
        {
            if (record == null || record.Kind != "loc" || wave == null ||
                string.IsNullOrEmpty(record.Path) || !File.Exists(record.Path))
                return Error(T("未找到定位数据文件。", "Location data file not found."));
            try
            {
                locationPlot.SetData(record.Path, wave.Y, wave.SampleRateHz,
                    wave.CableLengthMetres, wave.VelocityMPerUs);
                locationPlot.LanguageIndex = LanguageIndex;
                ShowControl(locationPlot);
                Buttons(false, false, false);
                UpdateLocationMeasurementInformation();
                SetStatus(T("已显示：", "Displayed: ") + record.Name);
                return true;
            }
            catch (Exception ex)
            {
                return Error(T("此文件波形无法显示。", "The waveform for this file cannot be displayed.") + " " + ex.Message);
            }
        }


        void UpdateLocationMeasurementInformation()
        {
            BrowserRecord record = selectedRecord;
            BrowserWaveform wave = selectedWave;
            if (record == null || record.Kind != "loc" || wave == null ||
                !string.Equals(locationPlot.DisplayedPath, record.Path, StringComparison.OrdinalIgnoreCase))
                return;
            string value = T("定位文件：", "Location file: ") + record.Name + Environment.NewLine +
                T("相别：", "Phase: ") + record.Phase + Environment.NewLine +
                T("电压：", "Voltage: ") + F(record.VoltageKv, "0.00") + " kV" + Environment.NewLine +
                T("采样率：", "Sample rate: ") + F(wave.SampleRateHz, "0.###") + " Hz" + Environment.NewLine +
                T("电缆长度：", "Cable length: ") + F(wave.CableLengthMetres, "0.0") + " m" + Environment.NewLine +
                T("波速：", "Velocity: ") + F(wave.VelocityMPerUs, "0.###") + " m/μs";
            if (locationPlot.Cursor1Index >= 0 && locationPlot.Cursor2Index >= 0)
            {
                value += Environment.NewLine + "T1: " +
                    F(locationPlot.Cursor1Index / wave.SampleRateHz * 1e6, "0.###") + " μs   T2: " +
                    F(locationPlot.Cursor2Index / wave.SampleRateHz * 1e6, "0.###") + " μs" +
                    Environment.NewLine + "|Δt|: " + F(locationPlot.CursorDeltaNs, "0.0") + " ns" +
                    Environment.NewLine + T("位置：", "Position: ") +
                    F(locationPlot.CursorPositionMetres, "0.0") + " m";
            }
            information.Text = value;
        }

        void DisposeLocationAnalysisView()
        {
            if (!locationPlot.IsDisposed) locationPlot.Dispose();
        }

    }
}
