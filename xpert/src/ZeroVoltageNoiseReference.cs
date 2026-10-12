using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Noise
{
    // Comparison only. This contract never produces a rejection threshold,
    // subtracts a waveform, certifies insulation, or controls an output.
    internal sealed class NoiseAcquisitionContext
    {
        internal string Project,Phase,Channel,Unit,Source,CalibrationId,DeviceId;
        internal double Rate,FullScale,Target,ChargeScale;
        internal int Bits,Range;
        internal bool Simulation,GeneratorZero,VoltageVerified;
        internal DateTimeOffset CapturedUtc;
        internal double Measured=Double.NaN,ZeroMin=Double.NaN,ZeroMax=Double.NaN;
        internal string VoltageSource="";
        internal static bool IsExplicitZero(IDictionary<string,object> values)
        {double value;return TryNumber(values,"targetVoltageKv",out value)&&value==0;}
        internal static NoiseAcquisitionContext Read(IDictionary<string,object> values)
        {
            if(values==null||Text(values,"schema")!="HVX.NoiseReference/1")throw new ArgumentException("invalid_schema");
            var c=new NoiseAcquisitionContext{Project=Path.GetFullPath(Text(values,"projectPath")).TrimEnd(Path.DirectorySeparatorChar),
                Phase=Text(values,"phaseCode").ToUpperInvariant(),Channel=Text(values,"channelId"),Unit=Text(values,"sampleUnit"),Source=Text(values,"sourceId"),CalibrationId=Text(values,"calibrationId"),DeviceId=Text(values,"deviceId"),
                Rate=Number(values,"sampleRateHz"),FullScale=Number(values,"fullScaleV"),Target=Number(values,"targetVoltageKv"),ChargeScale=Number(values,"chargeScalePcPerVolt"),
                Simulation=Flag(values,"simulation"),GeneratorZero=OptionalFlag(values,"generatorZero"),VoltageVerified=OptionalFlag(values,"voltageEvidenceVerified")};
            double bits=Number(values,"resolutionBits"),range=Number(values,"rangeCode");
            if(bits!=Math.Truncate(bits)||bits<1||bits>32||range!=Math.Truncate(range)||range<0||range>255||c.Rate<=0||c.FullScale<=0||c.Target<0||c.ChargeScale<=0||c.Unit!="pC"||!new[]{"A","B","C","ALL"}.Contains(c.Phase))throw new ArgumentException("invalid_context");
            c.Bits=(int)bits;c.Range=(int)range;
            object date;if(!values.TryGetValue("capturedUtc",out date)||!(date is DateTimeOffset))throw new ArgumentException("missing_capture_time");
            c.CapturedUtc=((DateTimeOffset)date).ToUniversalTime();if(c.CapturedUtc==default(DateTimeOffset)||c.CapturedUtc>DateTimeOffset.UtcNow.AddMinutes(5))throw new ArgumentException("invalid_capture_time");
            double v;if(TryNumber(values,"measuredVoltageKv",out v))c.Measured=v;if(TryNumber(values,"zeroMinimumKv",out v))c.ZeroMin=v;if(TryNumber(values,"zeroMaximumKv",out v))c.ZeroMax=v;
            object source;if(values.TryGetValue("voltageEvidenceSource",out source)&&source is string)c.VoltageSource=((string)source).Trim();
            return c;
        }
        internal bool Comparable(NoiseAcquisitionContext other)
        {return other!=null&&String.Equals(Project,other.Project,StringComparison.OrdinalIgnoreCase)&&Phase==other.Phase&&Channel==other.Channel&&DeviceId==other.DeviceId&&Unit==other.Unit&&Rate==other.Rate&&Bits==other.Bits&&Range==other.Range&&FullScale==other.FullScale&&ChargeScale==other.ChargeScale&&CalibrationId==other.CalibrationId&&Simulation==other.Simulation;}
        static string Text(IDictionary<string,object> values,string key)
        {object o;if(!values.TryGetValue(key,out o)||!(o is string)||String.IsNullOrWhiteSpace((string)o)||((string)o).Length>1024)throw new ArgumentException("missing_"+key);return ((string)o).Trim();}
        static bool Flag(IDictionary<string,object> values,string key){object o;if(!values.TryGetValue(key,out o)||!(o is bool))throw new ArgumentException("missing_"+key);return (bool)o;}
        static bool OptionalFlag(IDictionary<string,object> values,string key){object o;return values.TryGetValue(key,out o)&&o is bool&&(bool)o;}
        static double Number(IDictionary<string,object> values,string key){double value;if(!TryNumber(values,key,out value))throw new ArgumentException("missing_"+key);return value;}
        internal static bool TryNumber(IDictionary<string,object> values,string key,out double value)
        {value=Double.NaN;object o;if(values==null||!values.TryGetValue(key,out o)||!(o is double||o is float||o is int||o is long||o is decimal))return false;value=Convert.ToDouble(o,CultureInfo.InvariantCulture);return Finite(value);}
        internal static bool Finite(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
    }
    internal sealed class ZeroVoltageNoiseReference
    {
        internal readonly NoiseAcquisitionContext Context;
        internal readonly string State;
        internal readonly double Mean,Rms,PeakToPeak;
        readonly double[] samples;
        internal int Count{get{return samples.Length;}}
        internal bool Verified{get{return State=="Verified"||State=="Simulation";}}
        internal ZeroVoltageNoiseReference(double[] source,NoiseAcquisitionContext context)
        {
            if(context.Target!=0)throw new ArgumentException("not_zero_target");
            if(source==null||source.Length<2||source.Length>8000000||source.Any(x=>!NoiseAcquisitionContext.Finite(x)))throw new ArgumentException("invalid_samples");
            Context=context;samples=(double[])source.Clone();double mean=0,m2=0,low=source[0],high=source[0];
            for(int i=0;i<source.Length;i++){double delta=source[i]-mean;mean+=delta/(i+1);m2+=delta*(source[i]-mean);low=Math.Min(low,source[i]);high=Math.Max(high,source[i]);}
            Mean=mean;Rms=Math.Sqrt(Math.Max(0,m2/source.Length));PeakToPeak=high-low;
            if(!NoiseAcquisitionContext.Finite(Mean)||!NoiseAcquisitionContext.Finite(Rms)||!NoiseAcquisitionContext.Finite(PeakToPeak))throw new ArgumentException("invalid_statistics");
            bool zeroEvidence=context.VoltageVerified&&context.VoltageSource.Length>0&&NoiseAcquisitionContext.Finite(context.Measured)&&NoiseAcquisitionContext.Finite(context.ZeroMin)&&NoiseAcquisitionContext.Finite(context.ZeroMax)&&context.ZeroMin<=0&&context.ZeroMax>=0&&context.ZeroMin<=context.ZeroMax;
            State=context.Simulation?(context.GeneratorZero?"Simulation":"Pending"):
                !zeroEvidence?"Pending":context.Measured>=context.ZeroMin&&context.Measured<=context.ZeroMax?"Verified":"Rejected";
        }
        internal double[] CopySamples(){return (double[])samples.Clone();}
        internal IDictionary<string,object> Summary()
        {return new Dictionary<string,object>{{"state",State},{"projectPath",Context.Project},{"phaseCode",Context.Phase},{"channelId",Context.Channel},{"deviceId",Context.DeviceId},{"simulation",Context.Simulation},{"sourceId",Context.Source},{"capturedUtc",Context.CapturedUtc},{"sampleCount",Count},{"sampleRateHz",Context.Rate},{"resolutionBits",Context.Bits},{"rangeCode",Context.Range},{"fullScaleV",Context.FullScale},{"chargeScalePcPerVolt",Context.ChargeScale},{"calibrationId",Context.CalibrationId},{"sampleUnit",Context.Unit},{"mean",Mean},{"acRms",Rms},{"peakToPeak",PeakToPeak},{"automaticFilteringAllowed",false},{"includedInDischargeStatistics",false}};}
    }
    internal sealed class NoiseReferenceStore
    {
        readonly List<ZeroVoltageNoiseReference> entries=new List<ZeroVoltageNoiseReference>();
        internal void Clear(){entries.Clear();}
        internal void Add(ZeroVoltageNoiseReference value)
        {
            entries.RemoveAll(e=>e.Context.Comparable(value.Context));entries.Add(value);
            while(entries.Count>8||entries.Sum(e=>(long)e.Count)>8000000)entries.RemoveAt(0);
        }
        internal ZeroVoltageNoiseReference Find(NoiseAcquisitionContext context)
        {return entries.LastOrDefault(e=>e.Context.Comparable(context));}
    }
    // Shared native read-only view: the full immutable reference is retained;
    // only the paint path bins data. No algorithm or selection state is changed.
    internal sealed class NoiseReferencePanel:UserControl
    {
        readonly Label text=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
        readonly Button open=new HVX.Presentation.R2RoundedButton{Dock=DockStyle.Right,FlatStyle=FlatStyle.Flat};
        ZeroVoltageNoiseReference reference;int language;
        internal NoiseReferencePanel()
        {
            Name="NoiseReferencePanel";Dock=DockStyle.Top;BackColor=Color.FromArgb(235,246,240);Padding=new Padding(6,3,6,3);Visible=false;
            Controls.Add(text);Controls.Add(open);open.Click+=(s,e)=>ShowReference();SizeChanged+=(s,e)=>Fit();
        }
        internal void SetReference(ZeroVoltageNoiseReference value,int languageIndex)
        {reference=value;language=languageIndex;open.Enabled=value!=null;Visible=value!=null;RefreshText();}
        internal void SetPending(int languageIndex)
        {reference=null;language=languageIndex;Visible=true;open.Enabled=false;open.Text=L("查看参考","View reference");text.Text=L("零电压参考待核验：采集条件不完整；不计入放电统计，不自动过滤。","Zero-voltage reference unverified: incomplete acquisition context; excluded from discharge statistics, no automatic filtering.");Fit();}
        string L(string zh,string en){return UiLanguage.Text(language,zh,en);}
        void RefreshText()
        {
            open.Text=L("查看参考","View reference");
            if(reference==null){text.Text="";Fit();return;}
            string state=reference.State=="Verified"?L("参考噪声","Noise reference"):reference.State=="Simulation"?L("仿真参考噪声","SIM noise reference"):reference.State=="Rejected"?L("回零未确认，不适用","Zero not confirmed — not applicable"):L("参考噪声待核验","Noise reference — unverified");
            text.Text=state+" · "+reference.Context.Phase+" · "+L("AC RMS（去直流）","AC RMS (DC removed)")+" "+reference.Rms.ToString("0.###",CultureInfo.InvariantCulture)+" pC · "+L("仅对照，不自动过滤","Comparison only; no automatic filtering");Fit();
        }
        void Fit(){open.Width=Math.Max(112,TextRenderer.MeasureText(open.Text,Font).Width+24);int labelWidth=Math.Max(100,Width-open.Width-Padding.Horizontal);int words=TextRenderer.MeasureText(text.Text,Font,new Size(labelWidth,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;Height=Math.Max(46,Math.Max(Font.Height*2,words)+10);}
        void ShowReference()
        {
            if(reference==null)return;
            using(var icon=LoadReferenceIcon(FindForm()))
            using(var form=new Form{Text=L("零电压参考噪声","Zero-voltage noise reference"),ClientSize=new Size(820,470),MinimumSize=new Size(620,360),Font=Font,BackColor=Color.White,StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false})
            {
                var owner=FindForm();if(icon!=null)form.Icon=icon;
                var outer=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(12),ColumnCount=1,RowCount=2};
                outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
                var viewport=new Panel{Name="NoiseReferenceViewport",Dock=DockStyle.Fill,AutoScroll=true,Margin=Padding.Empty};
                var layout=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,RowCount=3};
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                var info=new Label{Name="NoiseReferenceSummary",Dock=DockStyle.Fill,AutoSize=true,Text=text.Text+Environment.NewLine+reference.Context.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" · "+reference.Context.Rate.ToString("G",CultureInfo.InvariantCulture)+" S/s · "+reference.Context.Bits+" bit"};
                // Long source paths remain copyable/scrollable without displacing
                // the read-only/use limitation. The latter always has its own row.
                var source=new TextBox{Name="NoiseReferenceSource",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Text=reference.Context.Source};
                var notice=new Label{Name="NoiseReferenceUseNotice",Dock=DockStyle.Fill,Text=L("原始波形只读；不计入放电次数、起始电压或诊断结论。","Read-only original waveform; excluded from discharge count, inception voltage and diagnosis.")};
                var plot=new NoiseReferencePlot(reference.CopySamples()){Name="NoiseReferencePlot",Dock=DockStyle.Fill};
                layout.Controls.Add(info,0,0);layout.Controls.Add(source,0,1);layout.Controls.Add(plot,0,2);
                viewport.Controls.Add(layout);outer.Controls.Add(viewport,0,0);outer.Controls.Add(notice,0,1);form.Controls.Add(outer);
                bool measuring=false;
                Action measure=delegate{
                    if(measuring)return;measuring=true;
                    try{
                        int noticeWidth=Math.Max(100,form.ClientSize.Width-outer.Padding.Horizontal-notice.Margin.Horizontal);
                        outer.RowStyles[1].Height=TextRenderer.MeasureText(notice.Text,notice.Font,new Size(noticeWidth,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+notice.Margin.Vertical+2;
                        int width=Math.Max(100,viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-12);
                        info.MaximumSize=new Size(width,0);source.Height=form.Font.Height*2+12;
                        // The safety notice stays outside the scrolling content.
                        // A small window scrolls the summary/plot instead of
                        // crushing the plot or losing the use restriction.
                        layout.Height=Math.Max(viewport.ClientSize.Height,info.GetPreferredSize(new Size(width,0)).Height+source.Height+190);
                    }finally{measuring=false;}
                };
                form.SizeChanged+=(s,e)=>measure();form.FontChanged+=(s,e)=>measure();viewport.SizeChanged+=(s,e)=>measure();
                outer.SizeChanged+=(s,e)=>measure();notice.FontChanged+=(s,e)=>measure();
                // WinForms can raise Form.SizeChanged before its fill container
                // has received the final client width. Measure after layout too.
                outer.Layout+=(s,e)=>measure();
                form.Shown+=(s,e)=>{var work=Screen.FromControl(owner??form).WorkingArea;form.MinimumSize=new Size(Math.Min(620,work.Width-16),Math.Min(360,work.Height-16));form.Size=new Size(Math.Min(form.Width,work.Width-16),Math.Min(form.Height,work.Height-16));measure();source.Select(0,0);form.ActiveControl=null;};
                measure();form.ShowDialog(owner);
            }
        }
        static Icon LoadReferenceIcon(Form owner)
        {
            if(owner!=null&&owner.Icon!=null)return (Icon)owner.Icon.Clone();
            return HVX.Presentation.OemUiBranding.LoadIcon();
        }
        sealed class NoiseReferencePlot:Control
        {
            readonly double[] samples;
            readonly double low,high;
            int envelopeWidth;
            double[] envelopeLow,envelopeHigh;
            internal NoiseReferencePlot(double[] values){samples=values;low=values.Min();high=values.Max();if(high==low){low-=1;high+=1;}DoubleBuffered=true;ResizeRedraw=true;}
            internal Rectangle GetPlotBounds()
            {
                int left=Math.Max(42,TextRenderer.MeasureText("pC",Font).Width+12);
                int bottom=TextRenderer.MeasureText("0",Font).Height+10;
                return new Rectangle(left,12,Math.Max(0,Width-left-20),Math.Max(0,Height-12-bottom));
            }
            void EnsureEnvelope(int width)
            {
                if(width==envelopeWidth&&envelopeLow!=null)return;
                var lows=new double[width];var highs=new double[width];
                for(int x=0;x<width;x++){
                    int from=(int)((long)x*samples.Length/width),to=Math.Min(samples.Length,(int)((long)(x+1)*samples.Length/width)+1);
                    double a=samples[from],b=a;for(int i=from;i<to;i++){a=Math.Min(a,samples[i]);b=Math.Max(b,samples[i]);}
                    lows[x]=a;highs[x]=b;
                }
                envelopeLow=lows;envelopeHigh=highs;envelopeWidth=width;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);var box=GetPlotBounds();if(box.Width<1||box.Height<1)return;
                // Immutable source: repainting an uncovered window must not scan
                // millions of samples again. Resizing still recomputes exact bins.
                EnsureEnvelope(box.Width);
                e.Graphics.DrawRectangle(Pens.LightGray,box);using(var pen=new Pen(Color.FromArgb(44,133,110)))
                for(int x=0;x<box.Width;x++){float top=box.Bottom-(float)((envelopeHigh[x]-low)/(high-low)*box.Height),bottom=box.Bottom-(float)((envelopeLow[x]-low)/(high-low)*box.Height);e.Graphics.DrawLine(pen,box.Left+x,top,box.Left+x,Math.Max(top+.5f,bottom));}
                string last=(samples.Length-1).ToString(CultureInfo.InvariantCulture);
                TextRenderer.DrawText(e.Graphics,"pC",Font,new Point(5,12),Color.DimGray);TextRenderer.DrawText(e.Graphics,"0",Font,new Point(box.Left,box.Bottom+3),Color.DimGray);TextRenderer.DrawText(e.Graphics,last,Font,new Point(Math.Max(box.Left,box.Right-TextRenderer.MeasureText(last,Font).Width),box.Bottom+3),Color.DimGray);
            }
        }
    }
}
