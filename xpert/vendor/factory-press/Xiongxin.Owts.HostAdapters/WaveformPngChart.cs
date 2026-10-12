using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 从已落盘样本生成简单 PNG 折线图（纯托管编码器，无 Skia/GDI 原生依赖）。
    /// 与 <see cref="WaveformSvgChart"/> 同尺寸/约定：不标注定位点或假结论。
    /// Linux Smoke 与 Windows 现场均可生成；失败时调用方应优雅跳过（SVG 仍在）。
    /// </summary>
    public static class WaveformPngChart
    {
        public const int Width = WaveformSvgChart.Width;
        public const int Height = WaveformSvgChart.Height;
        public const int DefaultMaxPoints = WaveformSvgChart.DefaultMaxPoints;

        /// <summary>当前为纯托管实现，恒可用。保留 API 以便日后切 Skia/System.Drawing 时做能力探测。</summary>
        public static bool IsAvailable { get { return true; } }

        public static bool TryWritePng(
            string absolutePath,
            double[] samples,
            string title,
            double sampleRateHz,
            string unitLabel,
            out string error,
            int maxPoints = DefaultMaxPoints)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                error = "PathEmpty";
                return false;
            }
            byte[] png;
            if (!TryRenderPng(samples, title, sampleRateHz, unitLabel, out png, out error, maxPoints))
                return false;
            try
            {
                string dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(absolutePath, png);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        public static bool TryRenderPng(
            double[] samples,
            string title,
            double sampleRateHz,
            string unitLabel,
            out byte[] pngBytes,
            out string error,
            int maxPoints = DefaultMaxPoints)
        {
            pngBytes = null;
            error = null;
            try
            {
                var bmp = new RgbBitmap(Width, Height);
                bmp.Clear(255, 255, 255);

                const double padL = 48, padR = 16, padT = 28, padB = 36;
                double plotW = Width - padL - padR;
                double plotH = Height - padT - padB;

                string head = string.IsNullOrEmpty(title) ? "waveform" : title;
                DrawText(bmp, 48, 6, TruncateAscii(head, 70), 34, 34, 34);

                if (samples == null || samples.Length == 0)
                {
                    DrawText(bmp, 24, 40, TruncateAscii(head, 40) + " (no samples)", 176, 0, 0);
                    pngBytes = EncodePng(bmp);
                    return true;
                }

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
                    max = min + 1;

                var meta = new StringBuilder();
                meta.Append("n=").Append(n.ToString(CultureInfo.InvariantCulture));
                meta.Append(" stride=").Append(stride.ToString(CultureInfo.InvariantCulture));
                if (sampleRateHz > 0)
                    meta.Append(" fs=").Append(sampleRateHz.ToString("G6", CultureInfo.InvariantCulture)).Append("Hz");
                if (!string.IsNullOrEmpty(unitLabel))
                    meta.Append(" [").Append(unitLabel).Append(']');
                DrawTextRight(bmp, Width - 16, 6, TruncateAscii(meta.ToString(), 48), 102, 102, 102);

                int ax0 = (int)padL, ay0 = (int)padT, ax1 = (int)(padL + plotW), ay1 = (int)(padT + plotH);
                DrawLine(bmp, ax0, ay0, ax0, ay1, 153, 153, 153);
                DrawLine(bmp, ax0, ay1, ax1, ay1, 153, 153, 153);

                DrawText(bmp, 4, ay0, TruncateAscii(max.ToString("G4", CultureInfo.InvariantCulture), 8), 102, 102, 102);
                DrawText(bmp, 4, ay1 - 8, TruncateAscii(min.ToString("G4", CultureInfo.InvariantCulture), 8), 102, 102, 102);
                DrawText(bmp, ax0, Height - 14, "0", 102, 102, 102);
                DrawTextRight(bmp, ax1, Height - 14, n.ToString(CultureInfo.InvariantCulture) + " samples", 102, 102, 102);

                int prevX = -1, prevY = -1;
                int pi = 0;
                for (int i = 0; i < n; i += stride, pi++)
                {
                    double v = samples[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) v = min;
                    double x = padL + (plotN <= 1 ? 0 : (pi * plotW / (plotN - 1)));
                    double y = padT + (max - v) / (max - min) * plotH;
                    int ix = (int)Math.Round(x);
                    int iy = (int)Math.Round(y);
                    if (prevX >= 0)
                        DrawLine(bmp, prevX, prevY, ix, iy, 21, 101, 192);
                    prevX = ix;
                    prevY = iy;
                }

                DrawText(bmp, ax0, Height - 26, "raw waveform only; no location markers", 136, 136, 136);

                pngBytes = EncodePng(bmp);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
                pngBytes = null;
                return false;
            }
        }

        static string TruncateAscii(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(Math.Min(s.Length, maxChars));
            foreach (char c in s)
            {
                if (sb.Length >= maxChars) break;
                if (c >= 32 && c <= 126) sb.Append(c);
                else sb.Append('?');
            }
            return sb.ToString();
        }

        #region bitmap / draw

        sealed class RgbBitmap
        {
            public readonly int W, H;
            public readonly byte[] Rgb;

            public RgbBitmap(int w, int h)
            {
                W = w;
                H = h;
                Rgb = new byte[w * h * 3];
            }

            public void Clear(byte r, byte g, byte b)
            {
                for (int i = 0; i < Rgb.Length; i += 3)
                {
                    Rgb[i] = r;
                    Rgb[i + 1] = g;
                    Rgb[i + 2] = b;
                }
            }

            public void Set(int x, int y, byte r, byte g, byte b)
            {
                if ((uint)x >= (uint)W || (uint)y >= (uint)H) return;
                int i = (y * W + x) * 3;
                Rgb[i] = r;
                Rgb[i + 1] = g;
                Rgb[i + 2] = b;
            }
        }

        static void DrawLine(RgbBitmap bmp, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            for (;;)
            {
                bmp.Set(x0, y0, r, g, b);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        static void DrawText(RgbBitmap bmp, int x, int y, string text, byte r, byte g, byte b)
        {
            if (string.IsNullOrEmpty(text)) return;
            int cx = x;
            foreach (char ch in text)
            {
                DrawGlyph(bmp, cx, y, ch, r, g, b);
                cx += 6;
            }
        }

        static void DrawTextRight(RgbBitmap bmp, int rightX, int y, string text, byte r, byte g, byte b)
        {
            if (string.IsNullOrEmpty(text)) return;
            DrawText(bmp, rightX - text.Length * 6, y, text, r, g, b);
        }

        static readonly byte[][] Glyphs = BuildGlyphs();

        static byte[][] BuildGlyphs()
        {
            var g = new byte[127][];
            Put(g, '0', 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E);
            Put(g, '1', 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E);
            Put(g, '2', 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F);
            Put(g, '3', 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E);
            Put(g, '4', 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02);
            Put(g, '5', 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E);
            Put(g, '6', 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E);
            Put(g, '7', 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08);
            Put(g, '8', 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E);
            Put(g, '9', 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C);
            Put(g, 'A', 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
            Put(g, 'B', 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E);
            Put(g, 'C', 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E);
            Put(g, 'D', 0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E);
            Put(g, 'E', 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F);
            Put(g, 'F', 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10);
            Put(g, 'G', 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F);
            Put(g, 'H', 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
            Put(g, 'I', 0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E);
            Put(g, 'J', 0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0E);
            Put(g, 'K', 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11);
            Put(g, 'L', 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F);
            Put(g, 'M', 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11);
            Put(g, 'N', 0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11);
            Put(g, 'O', 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
            Put(g, 'P', 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10);
            Put(g, 'Q', 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D);
            Put(g, 'R', 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11);
            Put(g, 'S', 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E);
            Put(g, 'T', 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04);
            Put(g, 'U', 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
            Put(g, 'V', 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04);
            Put(g, 'W', 0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11);
            Put(g, 'X', 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11);
            Put(g, 'Y', 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04);
            Put(g, 'Z', 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F);
            for (char c = 'a'; c <= 'z'; c++)
                g[c] = g[c - 32];
            Put(g, ' ', 0, 0, 0, 0, 0, 0, 0);
            Put(g, '-', 0, 0, 0, 0x1F, 0, 0, 0);
            Put(g, '_', 0, 0, 0, 0, 0, 0, 0x1F);
            Put(g, '.', 0, 0, 0, 0, 0, 0x0C, 0x0C);
            Put(g, ',', 0, 0, 0, 0, 0x04, 0x08, 0x10);
            Put(g, ':', 0, 0x0C, 0x0C, 0, 0x0C, 0x0C, 0);
            Put(g, ';', 0, 0x0C, 0x0C, 0, 0x04, 0x08, 0x10);
            Put(g, '=', 0, 0, 0x1F, 0, 0x1F, 0, 0);
            Put(g, '+', 0, 0x04, 0x04, 0x1F, 0x04, 0x04, 0);
            Put(g, '/', 0x01, 0x02, 0x04, 0x08, 0x10, 0, 0);
            Put(g, '\\', 0x10, 0x08, 0x04, 0x02, 0x01, 0, 0);
            Put(g, '(', 0x04, 0x08, 0x10, 0x10, 0x10, 0x08, 0x04);
            Put(g, ')', 0x04, 0x02, 0x01, 0x01, 0x01, 0x02, 0x04);
            Put(g, '[', 0x0E, 0x08, 0x08, 0x08, 0x08, 0x08, 0x0E);
            Put(g, ']', 0x0E, 0x02, 0x02, 0x02, 0x02, 0x02, 0x0E);
            Put(g, '?', 0x0E, 0x11, 0x01, 0x02, 0x04, 0, 0x04);
            Put(g, '!', 0x04, 0x04, 0x04, 0x04, 0x04, 0, 0x04);
            Put(g, '"', 0x0A, 0x0A, 0x0A, 0, 0, 0, 0);
            Put(g, '\'', 0x04, 0x04, 0x08, 0, 0, 0, 0);
            Put(g, '*', 0, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0);
            Put(g, '#', 0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A);
            Put(g, '%', 0x19, 0x1A, 0x02, 0x04, 0x08, 0x13, 0x03);
            Put(g, '&', 0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D);
            Put(g, '@', 0x0E, 0x11, 0x17, 0x15, 0x17, 0x10, 0x0E);
            Put(g, '<', 0x02, 0x04, 0x08, 0x10, 0x08, 0x04, 0x02);
            Put(g, '>', 0x08, 0x04, 0x02, 0x01, 0x02, 0x04, 0x08);
            Put(g, '|', 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04);
            Put(g, '~', 0, 0, 0x08, 0x15, 0x02, 0, 0);
            Put(g, '`', 0x08, 0x04, 0x02, 0, 0, 0, 0);
            Put(g, '^', 0x04, 0x0A, 0x11, 0, 0, 0, 0);
            Put(g, '{', 0x06, 0x08, 0x08, 0x10, 0x08, 0x08, 0x06);
            Put(g, '}', 0x0C, 0x02, 0x02, 0x01, 0x02, 0x02, 0x0C);
            return g;
        }

        static void Put(byte[][] g, char c, byte r0, byte r1, byte r2, byte r3, byte r4, byte r5, byte r6)
        {
            g[c] = new byte[] { r0, r1, r2, r3, r4, r5, r6 };
        }

        static void DrawGlyph(RgbBitmap bmp, int x, int y, char ch, byte r, byte g, byte b)
        {
            byte[] rows = null;
            if (ch >= 0 && ch < Glyphs.Length) rows = Glyphs[ch];
            if (rows == null)
            {
                for (int yy = 0; yy < 7; yy++)
                    for (int xx = 0; xx < 5; xx++)
                        if (yy == 0 || yy == 6 || xx == 0 || xx == 4)
                            bmp.Set(x + xx, y + yy, r, g, b);
                return;
            }
            for (int yy = 0; yy < 7; yy++)
            {
                byte row = rows[yy];
                for (int xx = 0; xx < 5; xx++)
                {
                    if (((row >> (4 - xx)) & 1) != 0)
                        bmp.Set(x + xx, y + yy, r, g, b);
                }
            }
        }

        #endregion

        #region PNG encode

        static byte[] EncodePng(RgbBitmap bmp)
        {
            byte[] raw = BuildRawImage(bmp);
            byte[] compressed = ZlibCompress(raw);
            using (var ms = new MemoryStream(8 + 25 + compressed.Length + 12 + 12))
            {
                ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                WriteChunk(ms, "IHDR", BuildIhdr(bmp.W, bmp.H));
                WriteChunk(ms, "IDAT", compressed);
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        static byte[] BuildRawImage(RgbBitmap bmp)
        {
            var raw = new byte[bmp.H * (1 + bmp.W * 3)];
            int o = 0;
            for (int y = 0; y < bmp.H; y++)
            {
                raw[o++] = 0;
                Buffer.BlockCopy(bmp.Rgb, y * bmp.W * 3, raw, o, bmp.W * 3);
                o += bmp.W * 3;
            }
            return raw;
        }

        static byte[] BuildIhdr(int w, int h)
        {
            var b = new byte[13];
            WriteBe32(b, 0, w);
            WriteBe32(b, 4, h);
            b[8] = 8;
            b[9] = 2;
            b[10] = 0;
            b[11] = 0;
            b[12] = 0;
            return b;
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            if (data == null) data = new byte[0];
            WriteBe32Stream(s, data.Length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            if (data.Length > 0) s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFFu;
            crc = CrcUpdate(crc, typeBytes, 0, 4);
            crc = CrcUpdate(crc, data, 0, data.Length);
            crc ^= 0xFFFFFFFFu;
            WriteBe32Stream(s, (int)crc);
        }

        static void WriteBe32(byte[] b, int o, int v)
        {
            unchecked
            {
                b[o] = (byte)(v >> 24);
                b[o + 1] = (byte)(v >> 16);
                b[o + 2] = (byte)(v >> 8);
                b[o + 3] = (byte)v;
            }
        }

        static void WriteBe32Stream(Stream s, int v)
        {
            unchecked
            {
                s.WriteByte((byte)(v >> 24));
                s.WriteByte((byte)(v >> 16));
                s.WriteByte((byte)(v >> 8));
                s.WriteByte((byte)v);
            }
        }

        static byte[] ZlibCompress(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                uint adler = Adler32(raw);
                ms.WriteByte((byte)((adler >> 24) & 0xFF));
                ms.WriteByte((byte)((adler >> 16) & 0xFF));
                ms.WriteByte((byte)((adler >> 8) & 0xFF));
                ms.WriteByte((byte)(adler & 0xFF));
                return ms.ToArray();
            }
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static readonly uint[] CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                table[n] = c;
            }
            return table;
        }

        static uint CrcUpdate(uint crc, byte[] data, int offset, int length)
        {
            for (int i = 0; i < length; i++)
                crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        #endregion
    }
}
