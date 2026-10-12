using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using LabVIEWChartCommon;

namespace CalibrationWaveform
{
    public sealed partial class CalibrationWaveformControl
    {
        bool useInternalContextMenu = true;
        ExportPopupButton exportButton;

        [System.ComponentModel.Category("Interaction")]
        public bool UseInternalContextMenu
        {
            get { return useInternalContextMenu; }
            set
            {
                useInternalContextMenu = value;
                if (value) ContextMenuStrip = null;
            }
        }

        public void SetInternalContextMenuEnabled(bool enabled) { UseInternalContextMenu = enabled; }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            if (exportButton == null || exportButton.IsDisposed)
            {
                exportButton = ExportPopupButton.Attach(
                    this,
                    delegate { return languageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase); },
                    ExportCsvDialog,
                    ExportPngDialog);
                MouseDown += ResetViewOnRightMouseDown;
            }
            if (useInternalContextMenu) ContextMenuStrip = null;
        }

        void ResetViewOnRightMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) ResetView();
        }

        public void ExportDataCsv(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Path.GetTempPath(), "Calibration.csv");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("X,Y");
                for (int i = 0; i < Math.Min(xs.Length, ys.Length); i++)
                    w.WriteLine(xs[i].ToString("R", CultureInfo.InvariantCulture) + "," +
                                ys[i].ToString("R", CultureInfo.InvariantCulture));
            }
        }

        void ExportCsvDialog()
        {
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Calibration.csv" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK) ExportDataCsv(d.FileName);
        }

        void ExportPngDialog()
        {
            using (var d = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "Calibration.png" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK)
                    SavePng(d.FileName, Math.Max(1600, Width * 2), Math.Max(850, Height * 2));
        }
    }
}
