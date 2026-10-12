using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using LabVIEWChartCommon;

namespace LabVIEWOscillationPd
{
    public sealed partial class OscillationPdControl
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
                exportButton = ExportPopupButton.Attach(this, delegate { return EnglishVersion; }, ExportCsvDialog, ExportPngDialog);
            }
            if (useInternalContextMenu) ContextMenuStrip = null;
        }

        public void ExportDataCsv(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Path.GetTempPath(), "HV_PD.csv");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int n = Math.Max(hv.Length, pd.Length);
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Index,HV_Time_s,HV_kV,PD_Time_s,PD_pC");
                for (int i = 0; i < n; i++)
                    w.WriteLine(string.Join(",", i,
                        (i / samplingRateHv).ToString("R", CultureInfo.InvariantCulture),
                        i < hv.Length ? hv[i].ToString("R", CultureInfo.InvariantCulture) : "",
                        (i / samplingRatePd).ToString("R", CultureInfo.InvariantCulture),
                        i < pd.Length ? pd[i].ToString("R", CultureInfo.InvariantCulture) : ""));
            }
        }

        void ExportCsvDialog()
        {
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "HV_PD.csv" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK) ExportDataCsv(d.FileName);
        }

        void ExportPngDialog()
        {
            using (var d = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "HV_PD.png" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK)
                    SavePng(d.FileName, Math.Max(1600, Width * 2), Math.Max(850, Height * 2));
        }
    }
}
