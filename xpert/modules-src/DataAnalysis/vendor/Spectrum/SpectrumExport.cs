using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Forms;
using LabVIEWChartCommon;
using HVX.PdReview;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        const int ChartExportReservedWidth=34;
        ExportPopupButton chartExportButton;
        void InitializeChartExport()
        {
            if(chartExportButton!=null)return;
            chartExportButton=ExportPopupButton.Attach(this,()=>english,ExportSpectrumDataDialog,ExportSpectrumImageDialog);
            chartExportButton.Anchor=AnchorStyles.None;
            LayoutChartExport();
        }
        void LayoutChartExport()
        {
            if(chartExportButton==null||chartExportButton.IsDisposed)return;
            chartExportButton.Location=new Point(Math.Max(0,ClientSize.Width-chartExportButton.Width-12),
                Math.Max(0,ClientSize.Height-ToolbarHeightForWidth(ClientSize.Width)-chartExportButton.Height-6));
            chartExportButton.UpdateLanguage();chartExportButton.BringToFront();
        }
        internal void ConfigureChartReportAction(Action action,Func<bool> canAdd)
        { InitializeChartExport();chartExportButton.ConfigureReportAction(action,canAdd); }
        void ExportSpectrumDataDialog()
        {
            using(var dialog=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName=DisplayModeName+".csv"})
                if(dialog.ShowDialog(FindForm())==DialogResult.OK)TryExportDataCsv(dialog.FileName,false);
        }
        void ExportSpectrumImageDialog()
        {
            using(var dialog=new SaveFileDialog{Filter="PNG (*.png)|*.png",FileName=DisplayModeName+".png"})
                if(dialog.ShowDialog(FindForm())==DialogResult.OK)TrySavePng(dialog.FileName,1600,900);
        }
        internal ReportChartSnapshot CaptureReportChart(int sequence)
        {
            if(!RunSelfCheck(true))throw new InvalidOperationException(lastError);
            int[] rows=(int[])VisibleRows().Clone();
            if(rows.Length==0)throw new InvalidOperationException("No displayed spectrum data.");
            // Fixed dimensions bound memory and keep the chart independent of the host window size.
            const int width=1600,height=900;
            byte[] image;
            using(var bmp=new Bitmap(width,height))using(var graphics=Graphics.FromImage(bmp))using(var stream=new MemoryStream())
            { Render(graphics,new Rectangle(0,0,width,height),false);bmp.Save(stream,System.Drawing.Imaging.ImageFormat.Png);image=stream.ToArray(); }
            string csv;
            using(var writer=new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            { WriteCsv(writer,rows);csv=writer.ToString(); }
            return new ReportChartSnapshot(sequence,"Spectrum",SpectrumTitleText,RegionStatisticsActive?RegionStatisticsSummary:"",
                projectPath,mode,LanguageIndex,image,width,height,PresentedRowsMatrix(rows),rows,csv);
        }
        static bool ExportFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is NotSupportedException || ex is ExternalException || ex is SecurityException;
        }
        void ExportChecked(Action write)
        {
            try { write(); }
            catch(Exception ex) when(ExportFailure(ex))
            { lastError = "Export: " + ex.GetType().Name + ": " + ex.Message; Invalidate(); throw; }
        }
        public bool TrySavePng(string path, int width, int height)
        {
            try { SavePng(path,width,height); return true; }
            catch(Exception ex) when(ExportFailure(ex)) { return false; }
        }
        public bool TryExportDataCsv(string path, bool selectedOnly)
        {
            try { if(selectedOnly)ExportSelectedDataCsv(path);else ExportDataCsv(path);return true; }
            catch(Exception ex) when(ExportFailure(ex)) { return false; }
        }
    }
}
