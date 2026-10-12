using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        [Category("Appearance"), DefaultValue(12F), Description("坐标轴、图例与游标字体大小（磅），建议 12–14；信息栏由 InformationFontSize 独立设置。Axis, legend and cursor font size in points; information text uses InformationFontSize.")]
        public float FontSize
        {
            get { return Font.SizeInPoints; }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0) return;
                float clamped = Math.Max(6F, Math.Min(48F, value));
                if (Math.Abs(Font.SizeInPoints - clamped) < .001F) return;
                Font = new Font(Font.FontFamily, clamped, Font.Style, GraphicsUnit.Point);
            }
        }

        float informationFontSize = 10F;
        bool compactReviewInformation;

        [Category("Appearance"), DefaultValue(false)]
        [Description("组合复核界面仅显示时间差、相关度、同位置次数及定位说明，基本物理量由侧栏显示。Standalone mode retains the basic measurements; neither mode displays compensated charge.")]
        public bool CompactReviewInformation
        {
            get { return compactReviewInformation; }
            set { if (compactReviewInformation == value) return; compactReviewInformation = value; Invalidate(); }
        }
        string DisplayedHvName { get { return compactReviewInformation ? "" : CurrentHVname; } }

        [Category("Appearance"), DefaultValue(10F), Description("信息栏独立字号（磅），默认 10；不随 FontSize 改变。Independent information-text size in points; unaffected by FontSize.")]
        public float InformationFontSize
        {
            get { return informationFontSize; }
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0) return;
                float next = Math.Max(6F, Math.Min(48F, value));
                if (Math.Abs(informationFontSize - next) < .001F) return;
                informationFontSize = next;
                Invalidate();
            }
        }

        public void SetInformationFontSize(float sizeInPoints) { InformationFontSize = sizeInPoints; }

        Font InformationFont(FontStyle style)
        {
            return new Font(Font.FontFamily, informationFontSize, style, GraphicsUnit.Point);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Invalidate();
        }

        sealed class PdMapLayout
        {
            public Rectangle Plot;
            public RectangleF HvNameBounds;
            public float LegendRight;
            public float LineHeight, LegendTop, AxisCenterX, CursorLabelTop, CursorLabelHeight;
            public int XDivisions, YDivisions;
            public double[] XTicks, YTicks;
        }
        PdMapLayout cachedLayout;
        Rectangle layoutCanvas;
        Font layoutFont;
        float layoutDpiX, layoutDpiY;
        double layoutX0, layoutX1, layoutY0, layoutY1, layoutDt;
        int layoutFlags;
        string layoutHvName;
        int LayoutFlags()
        {
            int flags=(languageIndex<<16)|(english?1:0)|(showTitle?2:0)|(showOverlay?4:0)
                |(showCalibration&&hasAlignedCalibrationCurve?8:0)|(showAttenuation&&HasDampingCurve?16:0)
                |(showAttenuation&&HasFilteredDampingCurve?1<<11:0)|(compactReviewInformation?1<<12:0);
            for(int i=0;i<6;i++)if(Visible(i,i<3))flags|=1<<(i+5);
            return flags;
        }
        bool LayoutMatches(Rectangle canvas,float dpiX,float dpiY)
        {
            return cachedLayout!=null&&layoutCanvas==canvas&&ReferenceEquals(layoutFont,Font)
                &&layoutDpiX==dpiX&&layoutDpiY==dpiY&&layoutX0==viewX0&&layoutX1==viewX1
                &&layoutY0==viewY0&&layoutY1==viewY1&&layoutDt==dtNs&&layoutFlags==LayoutFlags()&&layoutHvName==DisplayedHvName;
        }

        string AxisTickX(double value)
        {
            double span=(viewX1-viewX0)*dtNs/1000;
            int digits=span>0?(int)Math.Max(0,Math.Min(9,1-Math.Floor(Math.Log10(span/10)))):3;
            return (value*dtNs/1000).ToString(digits==0?"0":"0."+new string('#',digits),System.Globalization.CultureInfo.InvariantCulture);
        }
        string AxisTickY(double value) { return value.ToString("0", System.Globalization.CultureInfo.InvariantCulture); }

        static double[] ReadableTicks(double minimum,double maximum,int divisions,bool integer)
        {
            double raw=(maximum-minimum)/Math.Max(1,divisions);
            if(raw<=0||double.IsInfinity(raw)||double.IsNaN(raw))return Array.Empty<double>();
            double power=Math.Pow(10,Math.Floor(Math.Log10(raw))),ratio=raw/power;
            double step=(ratio<=1?1:ratio<=2?2:ratio<=2.5?2.5:ratio<=5?5:10)*power;
            if(integer)step=Math.Max(1,Math.Ceiling(step));
            var ticks=new List<double>();
            double first=Math.Ceiling(minimum/step-1e-10)*step;
            for(int i=0;i<32;i++)
            {
                double value=first+i*step;
                if(value>maximum+step*1e-10)break;
                if(value>=minimum-step*1e-10)ticks.Add(Math.Abs(value)<step*1e-10?0:value);
            }
            return ticks.ToArray();
        }

        List<KeyValuePair<string, Color>> LegendItems(bool compact)
        {
            var items = new List<KeyValuePair<string, Color>>();
            string[] labels = L(new[] { "Partial discharge curve", "Calibration curve", "Damping decay curve" },new[] { "局部放电曲线", "校准曲线", "阻尼衰减曲线" });
            bool[] available = { Visible(0, true), Visible(1, true) && showCalibration && hasAlignedCalibrationCurve,
                Visible(2, true) && showAttenuation && HasDampingCurve, Visible(3, false),
                Visible(4, false) && showCalibration && hasAlignedCalibrationCurve,
                Visible(5, false) && showAttenuation && HasFilteredDampingCurve };
            Color[] colors = { pulseColor, calibrationColor, attenuationColor, filteredPulseColor, filteredCalibrationColor, filteredAttenuationColor };
            for (int group = 0; group < 2; group++)
            {
                bool first = true;
                for (int curve = 0; curve < 3; curve++)
                {
                    int index = group * 3 + curve;
                    if (!available[index]) continue;
                    string mode = group==0?L("Raw","原始"):L("Filtered","滤波");
                    string prefix = first ? mode + (compact ? " " : " · ") : "";
                    items.Add(new KeyValuePair<string, Color>(prefix + labels[curve], colors[index]));
                    first = false;
                }
            }
            return items;
        }

        PdMapLayout CalculatePlotLayout(Graphics g, Rectangle canvas)
        {
            if (pulse.Length > 0) EnsureView();
            if(LayoutMatches(canvas,g.DpiX,g.DpiY))return cachedLayout;
            using (var font = F(canvas, 12, FontStyle.Regular))
            {
                float line = (float)Math.Ceiling(g.MeasureString("Ag", font).Height);
                float maxY = 0, maxX = 0;
                for (int i = 0; i <= 8; i++)
                {
                    maxY = Math.Max(maxY, g.MeasureString(AxisTickY(viewY1 - (viewY1 - viewY0) * i / 8.0), font).Width);
                    maxX = Math.Max(maxX, g.MeasureString(AxisTickX(viewX0 + (viewX1 - viewX0) * i / 8.0), font).Width);
                }
                int left = (int)Math.Ceiling(8 + line + 5 + maxY + 8);
                int right = (int)Math.Ceiling(Math.Max(12, maxX / 2 + 5));
                left = Math.Min(left, Math.Max(60, (int)(canvas.Width * .48)));
                right = Math.Min(right, Math.Max(12, canvas.Width / 6));
                int width = Math.Max(60, canvas.Width - left - right);
                int rows = 1;
                float used = 0;
                float allLegendWidth=0,hvWidth=0;
                string hvName=DisplayedHvName;
                using(var legendFont=F(canvas,13,FontStyle.Regular))
                {
                    line=Math.Max(line,(float)Math.Ceiling(g.MeasureString("Ag",legendFont).Height));
                    foreach (var item in LegendItems(canvas.Width < 520))
                    {
                        float w = 28 + g.MeasureString(item.Key, legendFont).Width + 10;
                        allLegendWidth+=w;
                        if (used > 0 && used + w > width) { rows++; used = 0; }
                        used += w;
                    }
                    if(!string.IsNullOrEmpty(hvName))hvWidth=Math.Min(width,g.MeasureString(hvName,legendFont).Width+6);
                }
                bool hvSeparate=hvWidth>0&&allLegendWidth+hvWidth+16>width;
                float hvRow=hvSeparate?rows:0;
                if(hvSeparate)rows++;
                float titleHeight = showTitle ? line + 8 : 0;
                float cursorHeight = Math.Max(20, line + 6);
                int top = (int)Math.Ceiling(4 + titleHeight + rows * (line + 3) + 4 + cursorHeight);
                int bottom = (int)Math.Ceiling(line * 2 + 12) + DisplayOptionsFooterHeight(canvas.Width);
                // Preserve a useful plot area without ever scaling down the requested font.
                top = Math.Min(top, Math.Max(45, canvas.Height - bottom - 65));
                var plot = new Rectangle(canvas.Left + left, canvas.Top + top, width,
                    Math.Max(40, canvas.Height - top - bottom));
                int xd = Math.Max(1, Math.Min(10, (int)(plot.Width / Math.Max(52, maxX + 16))));
                int yd = Math.Max(1, Math.Min(8, (int)(plot.Height / (line + 14))));
                layoutCanvas=canvas;layoutFont=Font;layoutDpiX=g.DpiX;layoutDpiY=g.DpiY;
                layoutX0=viewX0;layoutX1=viewX1;layoutY0=viewY0;layoutY1=viewY1;layoutDt=dtNs;layoutFlags=LayoutFlags();layoutHvName=hvName;
                return cachedLayout=new PdMapLayout { Plot = plot, LineHeight = line,
                    HvNameBounds=new RectangleF(plot.Right-hvWidth,canvas.Top+4+titleHeight+hvRow*(line+3),hvWidth,line),
                    LegendRight=hvWidth>0&&!hvSeparate?plot.Right-hvWidth-16:plot.Right,
                    LegendTop = canvas.Top + 4 + titleHeight, AxisCenterX = canvas.Left + 8 + line / 2,
                    CursorLabelTop = plot.Top - cursorHeight, CursorLabelHeight = cursorHeight,
                    XDivisions = xd, YDivisions = yd,
                    XTicks = ReadableTicks(viewX0*dtNs/1000,viewX1*dtNs/1000,xd,false),
                    YTicks = ReadableTicks(viewY0,viewY1,yd,true) };
            }
        }

        Rectangle MeasurePlotRectangle(Rectangle canvas)
        {
            if(pulse.Length>0)EnsureView();
            if(LayoutMatches(canvas,DeviceDpi,DeviceDpi))return cachedLayout.Plot;
            // Live hit tests use the same device DPI as OnPaint; PNG exports pass their own Graphics.
            if (IsHandleCreated)
            {
                using (var g = CreateGraphics()) return CalculatePlotLayout(g, canvas).Plot;
            }
            using (var bitmap = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bitmap)) return CalculatePlotLayout(g, canvas).Plot;
        }

        static string CompactMetric(double value, string format)
        {
            if(!IsFinite(value))return "—";
            return value.ToString(Math.Abs(value) >= 1000000 ? "0.#e0" : format, System.Globalization.CultureInfo.InvariantCulture);
        }

        RectangleF informationBounds;
        readonly List<RectangleF> bottomLabelBounds=new List<RectangleF>();
        void DrawBottomLabel(Graphics g,Rectangle p,Font font,Brush brush,string text,float center)
        {
            if(p.Height<g.MeasureString(text,font).Height*8)return;
            RectangleF bounds;TryDrawBottomLabel(g,p,font,brush,text,center,out bounds);
        }
        bool TryDrawBottomLabel(Graphics g,Rectangle p,Font font,Brush brush,string text,float center,out RectangleF result)
        {
            result=RectangleF.Empty;
            SizeF size=g.MeasureString(text,font);
            // A compact chart still has room for one endpoint/joint label row.
            // Do not require three full text rows before drawing any label.
            if(p.Height<size.Height*2+18||size.Width>p.Width-8)return false;
            float x=ClampF(center-size.Width/2,p.Left+4,p.Right-size.Width-4);
            for(int row=0;row<2;row++)
            {
                var bounds=new RectangleF(x,p.Bottom-size.Height-12-row*(size.Height+3),size.Width,size.Height);
                if(bounds.Top<p.Top+size.Height+10)continue;
                var spaced=bounds;spaced.Inflate(4,2);
                bool overlaps=false;foreach(var placed in bottomLabelBounds)if(spaced.IntersectsWith(placed)){overlaps=true;break;}
                if(overlaps)continue;
                bottomLabelBounds.Add(bounds);g.DrawString(text,font,brush,bounds.Location);result=bounds;return true;
            }
            // The joint triangle/boundary line remain visible when labels are too dense.
            return false;
        }
        void DrawInformationSummary(Graphics g, Rectangle p, Rectangle c)
        {
            informationBounds=RectangleF.Empty;
            if(capacitorStatisticsMode) {
                using(var font=InformationFont(FontStyle.Regular))using(var brush=new SolidBrush(ForeColor))
                    g.DrawString(L("Capacitor PD: charge / voltage / phase only; no localization.","电容局放：仅统计电荷 / 电压 / 相位；无定位。"),font,brush,new RectangleF(p.Left+10,p.Top+10,p.Width-20,45));
                return;
            }
            using(var font=InformationFont(FontStyle.Regular))
            {
                string[] labels=InformationLabels();
                string[] values=InformationValues();
                string note=PositionInformationNote();
                float row=(float)Math.Ceiling(font.GetHeight(g))+7;
                const int tableRows=3;
                float[] columns=new float[3];
                for(int i=0;i<labels.Length;i++)columns[i/tableRows]=Math.Max(columns[i/tableRows],g.MeasureString(labels[i],font).Width+g.MeasureString(values[i],font).Width+14);
                float width=columns[0]+columns[1]+columns[2]+36;
                bool table=!compactReviewInformation&&p.Width>=650&&width<=p.Width*.74&&p.Height>=row*5;
                // Keep labels, values and position notes equally quiet. ForeColor
                // remains the public override; range warnings retain their words,
                // not a forced red table or an opaque background.
                using(var textBrush=new SolidBrush(ForeColor))
                using(var sf=new StringFormat{FormatFlags=StringFormatFlags.NoWrap,Trimming=StringTrimming.EllipsisCharacter})
                {
                    if(table)
                    {
                        float left=p.Right-width-12,top=p.Top+12;
                        var noteLines=WrapInformationText(g,font,width,note);
                        informationBounds=new RectangleF(left-3,top-3,width+6,row*(3+noteLines.Count)+6);
                        float x=left;
                        for(int col=0;col<3;col++)
                        {
                            for(int r=0;r<3;r++)
                            {
                                int i=col*tableRows+r;if(i>=labels.Length)continue;float y=top+r*row;
                                g.DrawString(labels[i]+(i==labels.Length-1?(L(":","：")):""),font,textBrush,x,y);
                                float vw=g.MeasureString(values[i],font).Width;
                                g.DrawString(values[i],font,textBrush,x+columns[col]-vw,y);
                            }
                            x+=columns[col]+18;
                        }
                        for(int n=0;n<noteLines.Count;n++)g.DrawString(noteLines[n],font,textBrush,new RectangleF(left,top+(3+n)*row,width,row),sf);
                    }
                    else
                    {
                        float available=Math.Max(40,p.Width-24);
                        var lines=CompactInfoLines(g,font,available);
                        lines.AddRange(WrapInformationText(g,font,available,note));
                        int capacity=Math.Max(1,(int)((p.Height-22)/row));
                        if(lines.Count>capacity)
                        {
                            lines=lines.GetRange(0,capacity);
                            // A short pane may condense measurements, but must not
                            // silently lose the endpoint/joint or range explanation.
                            lines[capacity-1]=!string.IsNullOrEmpty(note)?note:lines[capacity-1]+" …";
                        }
                        width=0;foreach(string line in lines)width=Math.Max(width,g.MeasureString(line,font).Width);
                        width=Math.Min(width,available);
                        float left=p.Right-width-12,top=p.Top+10;
                        informationBounds=new RectangleF(left-3,top-3,width+6,lines.Count*row+6);
                        for(int i=0;i<lines.Count;i++)g.DrawString(lines[i],font,textBrush,new RectangleF(left,top+i*row,width,row),sf);
                    }
                }
                // No background fill or enclosing panel: waveform and grid stay visible.
            }
        }

        string[] InformationLabels()
        {
            if(compactReviewInformation)return L(new[] { "Time difference", "Correlation", "Same-location discharges" },new[] { "时间差", "相关度", "同位置放电" });
            return L(new[] { "Location", "Discharge", "Confidence", "Time difference", "Correlation", "Voltage", "Phase", "Same-location discharges" },new[] { "放电位置", "放电量", "确信度", "时间差", "相关度", "电压", "相位", "同位置累计放电" });
        }

        string PositionInformationNote()
        {
            if(!reflectionSelected)return L("Reflection not selected; double-click waveform to select.","未选定反射波；双击波形选取。");
            if(!ReflectionWithinWaveform)return L("Reflection is outside this waveform window.","反射波位于当前波形窗口之外。");
            if(MixedCableCalculation&&!MixedCableModelValid)
                return compactReviewInformation?(L("Mixed-cable parameters unavailable","混合电缆参数不可用")):
                    L("Mixed-cable parameters unavailable [","混合电缆参数不可用 [")+MixedCableStatusCode+"]";
            if(LocationOutOfRange)
            {
                if(IsWithinBoundaryConfidenceTolerance)
                {
                    string excess=CompactMetric(BoundaryExcessPercent,"0.##")+"%";
                    return String.Format(L("Within boundary tolerance (outside {0})","边界容差内（超出{0}）"),excess);
                }
                return L("OUT OF CABLE RANGE","超出电缆范围");
            }
            if(compactReviewInformation&&PositionIsApproximated)
            {
                string error=FormatPositionDisplay(reversePositionDisplay?-PositionApproximationErrorPercent:PositionApproximationErrorPercent,true)+"%";
                return String.Format(L("Approximated to {0} (error {1})","近似至{0}（误差{1}）"),PositionApproximationKindCode=="Endpoint"?L("endpoint","端点"):L("joint","接头"),error);
            }
            string note=PositionApproximationNote;
            if(PositionApproximationStatusCode=="TargetOutsideWaveform")note=L("Joint is outside the loaded waveform; cursor unchanged.","附件时刻超出当前波形，游标未移动。");
            return note;
        }

        string[] InformationValues()
        {
            string count=samePositionPulseCount.ToString(System.Globalization.CultureInfo.InvariantCulture)+(languageIndex>1&&languageIndex!=18?"":english?(samePositionPulseCount==1?" event":" events"):"次");
            if(compactReviewInformation)return new[] { CompactMetric(TimeDifferenceNs,"0.0")+" ns", CompactMetric(correlation,"0.0")+" %", count };
            return new[] { FormatPositionDisplay(PresentedLocationM)+" m", CompactMetric(rawCharge,"0")+" pC", CompactMetric(confidence,"0.0")+" %",
                CompactMetric(TimeDifferenceNs,"0.0")+" ns", CompactMetric(correlation,"0.0")+" %", CompactMetric(voltageKv,"0.0")+" kV",
                CompactMetric(phaseDeg,"0.0")+"°", count };
        }

        static List<string> WrapInformationText(Graphics g,Font font,float width,string text)
        {
            var result=new List<string>();string line="";
            foreach(char ch in text)
            {
                if(line.Length>0&&g.MeasureString(line+ch,font).Width>width)
                {result.Add(line.TrimEnd());line="";}
                if(line.Length>0||!char.IsWhiteSpace(ch))line+=ch;
            }
            if(line.Length>0)result.Add(line);return result;
        }

        List<string> CompactInfoLines(Graphics g, Font font, float availableWidth)
        {
            string[] labels = InformationLabels(), values = InformationValues();
            var lines = new List<string>();
            string line = "";
            for (int i = 0; i < labels.Length; i++)
            {
                string field = labels[i] + (L(": ","：")) + values[i];
                string next = line.Length == 0 ? field : line + "  " + field;
                if (line.Length > 0 && g.MeasureString(next, font).Width > availableWidth)
                { lines.Add(line); line = field; }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }
    }
}
