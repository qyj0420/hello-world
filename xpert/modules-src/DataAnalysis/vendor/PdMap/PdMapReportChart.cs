using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using HVX.PdReview;

namespace LabVIEWPdMap
{
    public partial class PdMapControl
    {
        internal void ConfigureChartReportAction(Action action,Func<bool> canAdd)
        { exportButton.ConfigureReportAction(action,canAdd); }
        internal ReportChartSnapshot CaptureReportChart(int sequence,string project)
        {
            if(currentParameterRowIndex<0||pulse.Length==0)throw new InvalidOperationException("No selected PDmap waveform.");
            const int width=1600,height=650;
            byte[] image;
            using(var bmp=new Bitmap(width,height))using(var graphics=Graphics.FromImage(bmp))using(var stream=new MemoryStream())
            { Draw(graphics,new Rectangle(0,0,width,height),false);bmp.Save(stream,ImageFormat.Png);image=stream.ToArray(); }
            string csv;
            using(var writer=new StringWriter(CultureInfo.InvariantCulture)){WriteExportCsv(writer);csv=writer.ToString();}
            double[] row=GetReviewParameterRow();var parameters=new double[1,row.Length];
            for(int i=0;i<row.Length;i++)parameters[0,i]=row[i];
            string title="PDmap_"+currentPdMapId;
            if(!ReflectionWithinWaveform)title+=L(" · Reflection unresolved"," · 反射波未确定");
            return new ReportChartSnapshot(sequence,"PDmap",title,"",project,-1,LanguageIndex,image,width,height,
                parameters,new[]{currentParameterRowIndex},csv);
        }
    }
}
