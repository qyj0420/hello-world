using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 从已落盘旁路文件生成 SVG 折线（无定位点/结论标注，禁止伪造）。
    /// 支持 picoA(.i16)、ai4/eng(.f64)、hs.zlib（可重放压缩通道）。
    /// </summary>
    public static class WaveformSvgChart
    {
        public const int DefaultMaxPoints = 1200;
        public const int Width = 900;
        public const int Height = 280;

        /// <summary>尝试从旁路文件读出可绘图样本；失败返回 null。</summary>
        public static double[] TryLoadSamples(string absolutePath, string channel, out string unitLabel, out string error)
        {
            unitLabel = string.Empty;
            error = null;
            if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
            {
                error = "FileMissing";
                return null;
            }
            string ch = (channel ?? string.Empty).Trim();
            string path = absolutePath;
            try
            {
                if (ch.Equals("meta", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".meta.txt", StringComparison.OrdinalIgnoreCase))
                {
                    error = "SkipMeta";
                    return null;
                }
                if (ch.Equals("hs.zlib", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".hs.zlib", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] blob = File.ReadAllBytes(path);
                    Xiongxin.Owts.Contracts.Persistence.DacMeasurementWaveform wave;
                    string decodeErr;
                    if (!Xiongxin.Owts.Persistence.DacFileImporter.TryDecodeHighSpeed(blob, out wave, out decodeErr)
                        || wave == null || wave.EngineeringSamples == null || wave.EngineeringSamples.Length == 0)
                    {
                        error = "HsZlibDecode:" + (decodeErr ?? "empty");
                        return null;
                    }
                    unitLabel = "eng_V";
                    return wave.EngineeringSamples;
                }
                if (path.EndsWith(".i16", StringComparison.OrdinalIgnoreCase)
                    || ch.StartsWith("picoA", StringComparison.OrdinalIgnoreCase))
                {
                    unitLabel = "ADC";
                    return ReadInt16Le(path);
                }
                if (path.EndsWith(".f64", StringComparison.OrdinalIgnoreCase)
                    || ch.Equals("ai4", StringComparison.OrdinalIgnoreCase)
                    || ch.Equals("eng", StringComparison.OrdinalIgnoreCase)
                    || ch.StartsWith("eng", StringComparison.OrdinalIgnoreCase))
                {
                    unitLabel = ch.Equals("ai4", StringComparison.OrdinalIgnoreCase) ? "V_div" : "eng";
                    return ReadFloat64Le(path);
                }
                error = "UnsupportedFormat";
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
                return null;
            }
        }

        public static string RenderSvg(double[] samples, string title, double sampleRateHz, string unitLabel, int maxPoints = DefaultMaxPoints)
        {
            if (samples == null || samples.Length == 0)
                return EmptySvg(title ?? "empty");

            int n = samples.Length;
            int stride = Math.Max(1, (n + Math.Max(1, maxPoints) - 1) / Math.Max(1, maxPoints));
            int plotN = (n + stride - 1) / stride;

            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (int i = 0; i < n; i += stride)
            {
                double v = samples[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (double.IsInfinity(min) || double.IsInfinity(max))
            {
                min = 0;
                max = 1;
            }
            if (Math.Abs(max - min) < 1e-18)
            {
                max = min + 1;
            }

            const double padL = 48, padR = 16, padT = 28, padB = 36;
            double plotW = Width - padL - padR;
            double plotH = Height - padT - padB;

            var sb = new StringBuilder(plotN * 24 + 512);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(Width)
                .Append("\" height=\"").Append(Height).Append("\" viewBox=\"0 0 ")
                .Append(Width).Append(' ').Append(Height).Append("\">");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>");
            sb.Append("<text x=\"").Append(padL.ToString("0", CultureInfo.InvariantCulture))
                .Append("\" y=\"18\" font-family=\"sans-serif\" font-size=\"13\" fill=\"#222\">")
                .Append(Xml(title ?? "waveform")).Append("</text>");
            sb.Append("<text x=\"").Append((Width - padR).ToString("0", CultureInfo.InvariantCulture))
                .Append("\" y=\"18\" text-anchor=\"end\" font-family=\"sans-serif\" font-size=\"11\" fill=\"#666\">")
                .Append("n=").Append(n.ToString(CultureInfo.InvariantCulture))
                .Append(" stride=").Append(stride.ToString(CultureInfo.InvariantCulture));
            if (sampleRateHz > 0)
                sb.Append(" fs=").Append(sampleRateHz.ToString("G6", CultureInfo.InvariantCulture)).Append("Hz");
            if (!string.IsNullOrEmpty(unitLabel))
                sb.Append(" [").Append(Xml(unitLabel)).Append(']');
            sb.Append("</text>");

            // axes
            sb.Append("<line x1=\"").Append(F(padL)).Append("\" y1=\"").Append(F(padT))
                .Append("\" x2=\"").Append(F(padL)).Append("\" y2=\"").Append(F(padT + plotH))
                .Append("\" stroke=\"#999\" stroke-width=\"1\"/>");
            sb.Append("<line x1=\"").Append(F(padL)).Append("\" y1=\"").Append(F(padT + plotH))
                .Append("\" x2=\"").Append(F(padL + plotW)).Append("\" y2=\"").Append(F(padT + plotH))
                .Append("\" stroke=\"#999\" stroke-width=\"1\"/>");
            sb.Append("<text x=\"").Append(F(4)).Append("\" y=\"").Append(F(padT + 10))
                .Append("\" font-family=\"sans-serif\" font-size=\"10\" fill=\"#666\">")
                .Append(Xml(max.ToString("G4", CultureInfo.InvariantCulture))).Append("</text>");
            sb.Append("<text x=\"").Append(F(4)).Append("\" y=\"").Append(F(padT + plotH))
                .Append("\" font-family=\"sans-serif\" font-size=\"10\" fill=\"#666\">")
                .Append(Xml(min.ToString("G4", CultureInfo.InvariantCulture))).Append("</text>");
            sb.Append("<text x=\"").Append(F(padL)).Append("\" y=\"").Append(F(Height - 8))
                .Append("\" font-family=\"sans-serif\" font-size=\"10\" fill=\"#666\">0</text>");
            sb.Append("<text x=\"").Append(F(padL + plotW)).Append("\" y=\"").Append(F(Height - 8))
                .Append("\" text-anchor=\"end\" font-family=\"sans-serif\" font-size=\"10\" fill=\"#666\">")
                .Append(Xml(n.ToString(CultureInfo.InvariantCulture))).Append(" samples</text>");

            sb.Append("<polyline fill=\"none\" stroke=\"#1565c0\" stroke-width=\"1.2\" points=\"");
            bool first = true;
            int pi = 0;
            for (int i = 0; i < n; i += stride, pi++)
            {
                double v = samples[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) v = min;
                double x = padL + (plotN <= 1 ? 0 : (pi * plotW / (plotN - 1)));
                double y = padT + (max - v) / (max - min) * plotH;
                if (!first) sb.Append(' ');
                first = false;
                sb.Append(F(x)).Append(',').Append(F(y));
            }
            sb.Append("\"/>");
            sb.Append("<text x=\"").Append(F(padL)).Append("\" y=\"").Append(F(Height - 20))
                .Append("\" font-family=\"sans-serif\" font-size=\"10\" fill=\"#888\">raw waveform only; no location markers</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        static string EmptySvg(string title)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\""
                + Width + "\" height=\"" + Height + "\"><rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>"
                + "<text x=\"24\" y=\"40\" font-family=\"sans-serif\" font-size=\"14\" fill=\"#b00\">"
                + Xml(title) + " (no samples)</text></svg>";
        }

        static double[] ReadInt16Le(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int n = bytes.Length / 2;
            var samples = new double[n];
            for (int i = 0; i < n; i++)
                samples[i] = BitConverter.ToInt16(bytes, i * 2);
            return samples;
        }

        static double[] ReadFloat64Le(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int n = bytes.Length / 8;
            var samples = new double[n];
            for (int i = 0; i < n; i++)
                samples[i] = BitConverter.ToDouble(bytes, i * 8);
            return samples;
        }

        static string F(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
