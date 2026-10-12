using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace HVX.Controls
{
    // The validated Pico/ART capture UI is hosted in the desktop surface. Its
    // WorkerClient still starts the dedicated calibration executable as a hidden
    // SDK-owning child process; no high-voltage commands enter this control.
    public sealed class CalibrationAcquisitionControl : UserControl
    {
        private readonly CalibrationProgramControl analysis = new CalibrationProgramControl();
        private readonly Xiongxin.Calibration.CalibrationForm acquisition = new Xiongxin.Calibration.CalibrationForm(null, true);
        private string projectPath = String.Empty;

        public CalibrationAcquisitionControl()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var capturePage = new TabPage("实机采集 · Pico / 控制箱");
            var analysisPage = new TabPage("校准分析与档案");
            tabs.TabPages.Add(capturePage);
            tabs.TabPages.Add(analysisPage);
            Controls.Add(tabs);
            // Move only the form's content panel into the shell. Nesting a Form
            // itself causes WinForms DrawToBitmap/layout stalls on some displays.
            Control captureContent = acquisition.Controls[0];
            acquisition.Controls.Remove(captureContent);
            captureContent.Dock = DockStyle.Fill;
            capturePage.Controls.Add(captureContent);
            analysis.Dock = DockStyle.Fill;
            analysisPage.Controls.Add(analysis);
        }

        public string ProjectPath
        {
            get { return projectPath; }
            set
            {
                string next = String.IsNullOrWhiteSpace(value) ? String.Empty : Path.GetFullPath(value);
                if (String.Equals(next, projectPath, StringComparison.OrdinalIgnoreCase)) return;
                if (HasActiveHardwareSession) throw new InvalidOperationException("请先停止采集并断开 Pico 和控制箱，再切换项目。");
                analysis.ProjectPath = next;
                projectPath = next;
                if (Directory.Exists(next)) acquisition.LoadProject(next, false);
            }
        }
        public bool SimulationMode { get { return analysis.SimulationMode; } set { analysis.SimulationMode = value; } }
        [Browsable(false)] public bool HasActiveHardwareSession { get { return acquisition.HasActiveHardwareSession; } }
        [Browsable(false)] public bool HasCalibrationData { get { return analysis.HasCalibrationData; } }
        [Browsable(false)] public bool HasUnsavedCalibrationChanges { get { return analysis.HasUnsavedCalibrationChanges; } }
        [Browsable(false)] public bool HasUnsavedChargeCalibrationChanges { get { return analysis.HasUnsavedChargeCalibrationChanges; } }
        [Browsable(false)] public string UnsavedCalibrationSummary { get { return analysis.UnsavedCalibrationSummary; } }
        [Browsable(false)] public bool IsCalibrationSavedValid { get { return analysis.IsCalibrationSavedValid; } }
        public bool SaveCalibrationFile() { return analysis.SaveCalibrationFile(); }
        public void DiscardCalibrationChanges() { analysis.DiscardCalibrationChanges(); }
        public string GetCalibrationReadiness(string phaseCode, bool simulation, bool firstArrival)
        { return analysis.GetCalibrationReadiness(phaseCode, simulation, firstArrival); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) acquisition.Dispose();
            base.Dispose(disposing);
        }
    }
}
