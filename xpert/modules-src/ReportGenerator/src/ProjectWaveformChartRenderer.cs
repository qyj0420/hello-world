using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using HVX.Controls;

namespace HVX.ReportGenerator
{
    /// <summary>Print-resolution view of a project CAL or DEM, using the DataBrowser's validated parser.</summary>
    internal static class ProjectWaveformChartRenderer
    {
        internal const int Width = 3000;
        internal const int Height = 1500;
        static readonly RectangleF Plot = new RectangleF(255f, 245f, 2470f, 990f);
        static readonly Color Discharge = Color.FromArgb(31, 120, 102);
        static readonly Color Voltage = Color.FromArgb(33, 96, 151);

        internal static bool HasTimebase(BrowserWaveform wave)
        { return wave != null && Finite(wave.SampleRateHz) && wave.SampleRateHz > 0; }
        internal static double HorizontalExtent(BrowserWaveform wave, bool calibration)
        {
            int count=wave==null||wave.Y==null?0:wave.Y.Length;
            if(!calibration)return TestGeometry(wave)[0];
            if(!HasTimebase(wave))return Math.Max(1,count-1);
            return Math.Max(1,count-1)/wave.SampleRateHz*(calibration?1e6:1e3);
        }
        // [extent, PD step, voltage step, timed]. Display-only; source arrays
        // retain their own sample rates and are never independently stretched.
        internal static double[] TestGeometry(BrowserWaveform wave)
        {
            int pd=wave==null||wave.Y==null?0:wave.Y.Length, volts=wave==null||wave.VoltageKv==null?0:wave.VoltageKv.Length;
            bool timed=wave!=null&&(pd==0||HasTimebase(wave))&&(volts==0||Finite(wave.VoltageSampleRateHz)&&wave.VoltageSampleRateHz>0);
            double ps=timed&&pd>0?1000/wave.SampleRateHz:1,vs=timed&&volts>0?1000/wave.VoltageSampleRateHz:1;
            double pe=Math.Max(0,pd-1)*ps,ve=Math.Max(0,volts-1)*vs;
            return new[]{pd==0?ve:volts==0?pe:Math.Min(pe,ve),ps,vs,timed?1d:0d};
        }
        internal static int VisibleCount(int length,double step,double extent)
        { return length==0?0:(int)Math.Min(length,Math.Max(0,Math.Floor(extent/step+1e-8)+1)); }
        internal static double[] VoltageBounds(double[] values,int count)
        {
            double low=0,high=0;
            for(int i=0;i<count;i++)if(Finite(values[i])){low=Math.Min(low,values[i]);high=Math.Max(high,values[i]);}
            Expand(ref low,ref high);return new[]{Math.Min(-5,low),Math.Max(5,high)};
        }
        internal static string HorizontalLabel(BrowserWaveform wave, bool calibration, int language)
        {
            if(calibration?!HasTimebase(wave):TestGeometry(wave)[3]==0)return ReportLanguages.Ui(language,"采样点","Sample index");
            bool estimated=wave.XLabel!=null&&wave.XLabel.Contains("估算");
            if(estimated)return calibration?ReportLanguages.Ui(language,"估算时间 / µs","Estimated time / µs"):
                ReportLanguages.Ui(language,"估算时间 / ms","Estimated time / ms");
            return calibration?ReportLanguages.Ui(language,"时间 / µs","Time / µs"):
                ReportLanguages.Ui(language,"时间 / ms","Time / ms");
        }

        internal static string Render(BrowserRecord record, int language, string outputPath)
        {
            if (record == null) throw new ArgumentNullException("record");
            if (!record.IsValid) throw new InvalidDataException(record.Warning);
            if (record.Kind != "cal" && record.Kind != "dem") throw new InvalidDataException("Expected a CAL or DEM waveform.");
            BrowserWaveform wave = BrowserReader.LoadWaveform(record);
            if (wave.Y == null || wave.Y.Length < 2) throw new InvalidDataException("Waveform has too few samples.");
            string full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            using (var image = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                image.SetResolution(300f, 300f);
                using (Graphics g = Graphics.FromImage(image)) Draw(g, record, wave, language);
                image.Save(full, ImageFormat.Png);
            }
            return full;
        }

        static void Draw(Graphics g, BrowserRecord record, BrowserWaveform wave, int language)
        {
            g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            bool cal = record.Kind == "cal";
            string title = cal ? (ReportLanguages.Ui(language, "校准波形", "Calibration waveform")) :
                (ReportLanguages.Ui(language, "测试波形", "Test waveform"));
            string phaseLabel = ReportCableStructurePreview.PhaseCaption(language, record.Phase);
            string detail = cal ? (!ReportLanguages.IsChinese(language) && ReportAttachmentText.ContainsHan(record.Name)
                ? ReportLanguages.Text(language, ReportLanguages.UiKeys.CalibrationData) + " " + Math.Max(1, record.Order).ToString(CultureInfo.InvariantCulture)
                : record.Name) : phaseLabel + "  ·  " +
                (Finite(record.VoltageKv)?record.VoltageKv.ToString("0.##", CultureInfo.InvariantCulture):"—") + " kV  ·  " +
                (ReportLanguages.Ui(language, "加压次序 ", "Sequence ")) + record.Order.ToString(CultureInfo.InvariantCulture);
            double factor = cal ? 1e6 : 1e3;
            double duration = HorizontalExtent(wave,cal);
            double[] geometry=cal?new[]{duration, duration/Math.Max(1,wave.Y.Length-1),1d,HasTimebase(wave)?1d:0d}:TestGeometry(wave);
            int pdCount=cal?wave.Y.Length:VisibleCount(wave.Y.Length,geometry[1],duration);
            int voltageCount=wave.VoltageKv==null?0:VisibleCount(wave.VoltageKv.Length,geometry[2],duration);
            double yMin, yMax; Bounds(wave.Y,pdCount, out yMin, out yMax);
            if (cal) Expand(ref yMin, ref yMax);
            else { yMin = 0; yMax = NiceUpper(Math.Max(1.0, yMax * 1.04)); }
            double vMin = 0, vMax = 1;
            if (!cal && wave.VoltageKv != null && wave.VoltageKv.Length > 1)
            { double[] bounds=VoltageBounds(wave.VoltageKv,voltageCount);vMin=bounds[0];vMax=bounds[1]; }

            using (var titleFont = Font(70, FontStyle.Bold))
            using (var detailFont = Font(42, FontStyle.Regular))
            using (var tickFont = Font(49, FontStyle.Regular))
            using (var axisFont = Font(55, FontStyle.Bold))
            using (var ink = new SolidBrush(ReportTheme.Ink))
            using (var muted = new SolidBrush(ReportTheme.Muted))
            using (var plotFill = new SolidBrush(Color.FromArgb(252, 253, 254)))
            using (var grid = new Pen(Color.FromArgb(225, 233, 239), 2f))
            using (var axis = new Pen(ReportTheme.Navy, 5f))
            using (var accent = new Pen(ReportTheme.Cyan, 8f))
            {
                g.DrawString(title, titleFont, ink, new RectangleF(135, 43, 1300, 94));
                g.DrawString(detail, detailFont, muted, new RectangleF(1400, 62, 1440, 72), Right());
                g.DrawLine(accent, 135, 160, Width - 135, 160);
                g.FillRectangle(plotFill, Plot);
                for (int i = 0; i <= 6; i++)
                {
                    float y = Plot.Bottom - i * Plot.Height / 6f;
                    g.DrawLine(grid, Plot.Left, y, Plot.Right, y);
                    g.DrawString((yMin + (yMax - yMin) * i / 6).ToString("0.##", CultureInfo.InvariantCulture),
                        tickFont, ink, new RectangleF(15, y - 39, 220, 78), Right());
                    if (!cal) g.DrawString((vMin + (vMax - vMin) * i / 6).ToString("0.##", CultureInfo.InvariantCulture),
                        tickFont, ink, new RectangleF(Plot.Right + 18, y - 39, 205, 78));
                }
                for (int i = 0; i <= 6; i++)
                {
                    double tick=cal?duration*i/6:Math.Max(1,Math.Ceiling(duration/6))*i;
                    if(tick>duration)break;
                    float x = Plot.Left + (float)(tick/Math.Max(double.Epsilon,duration))*Plot.Width;
                    g.DrawLine(grid, x, Plot.Top, x, Plot.Bottom);
                    g.DrawString(tick.ToString(cal?"0.##":"0", CultureInfo.InvariantCulture), tickFont, ink,
                        new RectangleF(x - 95, Plot.Bottom + 18, 190, 72), Center());
                }
                g.DrawLine(axis, Plot.Left, Plot.Top, Plot.Left, Plot.Bottom);
                g.DrawLine(axis, Plot.Left, Plot.Bottom, Plot.Right, Plot.Bottom);
                if (!cal) g.DrawLine(axis, Plot.Right, Plot.Top, Plot.Right, Plot.Bottom);
                g.DrawString(HorizontalLabel(wave,cal,language), axisFont, ink,
                    new RectangleF(Plot.Left, Plot.Bottom + 91, Plot.Width, 76), Center());
                DrawVertical(g, cal ? (ReportLanguages.Ui(language, "校准幅值 / V", "Calibration amplitude / V")) :
                    (ReportLanguages.Ui(language, "局放量 / pC", "Discharge / pC")), axisFont, ink, 60);
                if (!cal) DrawVertical(g, ReportLanguages.Ui(language, "电压 / kV", "Voltage / kV"), axisFont, ink, Width - 60);
            }
            if (cal) DrawSeries(g, wave.Y, yMin, yMax, Discharge, 5f,pdCount,geometry[1],duration);
            else DrawDischargeColumns(g, wave.Y, yMax,pdCount,geometry[1],duration);
            if (!cal && wave.VoltageKv != null && wave.VoltageKv.Length > 1)
                DrawSeries(g, wave.VoltageKv, vMin, vMax, Voltage, 2.8f,voltageCount,geometry[2],duration);
            if (cal && HasTimebase(wave))
            {
                DrawMarker(g, wave.IncidentSeconds * factor, duration, ReportTheme.PhaseC,
                    ReportLanguages.Ui(language, "入射", "Incident"));
                DrawMarker(g, wave.ReflectionSeconds * factor, duration, ReportTheme.Cyan,
                    ReportLanguages.Ui(language, "反射", "Reflection"));
            }
            using (var legendFont = Font(44, FontStyle.Regular))
            using (var ink = new SolidBrush(ReportTheme.Ink))
            using (var pdPen = new Pen(Discharge, 8f))
            {
                float x = Plot.Left, y = Height - 91;
                if (cal) g.DrawLine(pdPen, x, y, x + 70, y);
                else { g.DrawLine(pdPen, x + 12, y + 15, x + 12, y - 5); g.DrawLine(pdPen, x + 35, y + 15, x + 35, y - 24); g.DrawLine(pdPen, x + 58, y + 15, x + 58, y - 11); }
                g.DrawString(cal ? (ReportLanguages.Ui(language, "校准波形", "Calibration")) :
                    (ReportLanguages.Ui(language, "局部放电", "Partial discharge")), legendFont, ink, x + 90, y - 35);
                if (!cal) using (var voltagePen = new Pen(Voltage, 8f))
                {
                    x += 430; g.DrawLine(voltagePen, x, y, x + 70, y);
                    g.DrawString(ReportLanguages.Ui(language, "振荡电压", "Oscillating voltage"), legendFont, ink, x + 85, y - 35);
                }
            }
        }

        static void DrawDischargeColumns(Graphics g, double[] values, double maximum,int count,double step,double duration)
        {
            if (values == null || count == 0 || maximum <= 0 || duration<=0) return;
            int columns = Math.Min((int)Plot.Width, count);
            using (var pen = new Pen(Color.FromArgb(215, Discharge), 2.8f))
            {
                for (int column = 0; column < columns; column++)
                {
                    int begin = column * count / columns;
                    int end = Math.Max(begin + 1, (column + 1) * count / columns);
                    double peak = 0;
                    for (int i = begin; i < end; i++) if (Finite(values[i])) peak = Math.Max(peak, values[i]);
                    if (peak <= 0) continue;
                    float x = Plot.Left + (float)(begin*step/duration)*Plot.Width;
                    float top = Plot.Bottom - (float)(Math.Min(peak, maximum) / maximum * Plot.Height);
                    g.DrawLine(pen, x, Plot.Bottom, x, top);
                }
            }
        }

        static void DrawSeries(Graphics g, double[] values, double min, double max, Color color, float width,int count,double step,double duration)
        {
            if (values == null || count < 2 || duration<=0) return;
            int columns = Math.Min((int)Plot.Width, count);
            using (var pen = new Pen(color, width) { LineJoin = LineJoin.Round })
            {
                for (int column = 0; column < columns; column++)
                {
                    int begin = column * count / columns, end = Math.Max(begin + 1, (column + 1) * count / columns);
                    double low = double.PositiveInfinity, high = double.NegativeInfinity;
                    for (int i = begin; i < end; i++) if (Finite(values[i]))
                    { low = Math.Min(low, values[i]); high = Math.Max(high, values[i]); }
                    if (!Finite(low) || !Finite(high)) continue;
                    float x = Plot.Left + (float)(begin*step/duration)*Plot.Width;
                    float top = Plot.Bottom - (float)((high - min) / (max - min) * Plot.Height);
                    float bottom = Plot.Bottom - (float)((low - min) / (max - min) * Plot.Height);
                    g.DrawLine(pen, x, top, x, bottom);
                    if (column > 0 && begin < values.Length && Finite(values[begin - 1]))
                    {
                        float prior = Plot.Bottom - (float)((values[begin - 1] - min) / (max - min) * Plot.Height);
                        float priorX=Plot.Left+(float)((begin-1)*step/duration)*Plot.Width;
                        g.DrawLine(pen, priorX, prior, x, bottom);
                    }
                }
            }
        }

        static void DrawMarker(Graphics g, double time, double duration, Color color, string label)
        {
            if (!Finite(time) || time < 0 || time > duration) return;
            float x = Plot.Left + (float)(time / duration) * Plot.Width;
            using (var pen = new Pen(color, 4f) { DashStyle = DashStyle.Dash })
            using (var font = Font(28, FontStyle.Bold))
            using (var brush = new SolidBrush(color))
            {
                g.DrawLine(pen, x, Plot.Top, x, Plot.Bottom);
                g.DrawString(label, font, brush, new RectangleF(x - 90, Plot.Top + 10, 180, 50), Center());
            }
        }

        static void Bounds(double[] values,int count, out double low, out double high)
        {
            low = double.PositiveInfinity; high = double.NegativeInfinity;
            for(int i=0;i<count;i++){double value=values[i];if (Finite(value)) { low = Math.Min(low, value); high = Math.Max(high, value); }}
            if (!Finite(low) || !Finite(high)) { low = 0; high = 1; }
        }
        static void Expand(ref double low, ref double high)
        {
            double span = high - low;
            if (span <= 0) span = Math.Max(1, Math.Abs(high) * 0.1);
            low -= span * 0.07; high += span * 0.07;
        }
        static double NiceUpper(double value)
        {
            if (!Finite(value) || value <= 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double scaled = value / power;
            return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * power;
        }
        static void DrawVertical(Graphics g, string value, Font font, Brush brush, float x)
        {
            GraphicsState state = g.Save(); g.TranslateTransform(x, Plot.Top + Plot.Height / 2f); g.RotateTransform(-90);
            g.DrawString(value, font, brush, new RectangleF(-Plot.Height / 2f, -35, Plot.Height, 70), Center()); g.Restore(state);
        }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        static Font Font(float size, FontStyle style) { return new Font("Microsoft YaHei", size, style, GraphicsUnit.Pixel); }
        static StringFormat Center() { return new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; }
        static StringFormat Right() { return new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center }; }
    }
}
