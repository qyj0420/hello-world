using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace HVX.PdReview
{
    // Presentation only: never edits result matrices, acceptance, or algorithm inputs.
    internal sealed partial class ReviewLocationOverview : Control
    {
        private double[] samples=new double[0];
        private double sampleRate=1,threshold=double.NaN,center,noise,maximum;
        private bool estimated,english;
        internal Func<string,string,string> LabelTranslator;
        private string L(string en,string zh){return LabelTranslator==null?(english?en:zh):LabelTranslator(en,zh);}
        private int viewStart,viewEnd;
        private double viewYMinimum,viewYMaximum;
        private bool verticalViewReady;
        internal double ViewYMinimum {get{EnsureVerticalView();return viewYMinimum;}}
        internal double ViewYMaximum {get{EnsureVerticalView();return viewYMaximum;}}
        private string filePath="",lastError="";
        private bool acquisitionReferenceOnly;
        private Font headerFont;
        private Font HeaderFont { get { return headerFont??(headerFont=new Font(Font,FontStyle.Bold)); } }
        private int HeaderLineHeight { get { return Math.Max(20,HeaderFont.Height+4); } }
        private int AxisLineHeight { get { return Math.Max(18,Font.Height+2); } }
        private Rectangle HeaderRow(int row,int rightReserve=0)
        { return new Rectangle(10,4+row*HeaderLineHeight,Math.Max(1,Width-20-rightReserve),HeaderLineHeight); }
        protected override void OnFontChanged(EventArgs e)
        { if(headerFont!=null){headerFont.Dispose();headerFont=null;}base.OnFontChanged(e);Invalidate(); }
        protected override void Dispose(bool disposing)
        { if(disposing&&headerFont!=null){headerFont.Dispose();headerFont=null;}base.Dispose(disposing); }
        private sealed class PlotBin { internal int Start,End,Peak=-1;internal double Low,High,Amplitude,Extreme,First,Last;internal bool Gap,IsPeak; }
        private PlotBin[] cachedBins=new PlotBin[0];
        private double[] cachedSamples;
        private int cachedStart=-1,cachedEnd=-1,cachedWidth=-1;
        private double cachedMinimum,cachedMaximum;
        internal int PlotCacheBuildCount {get;private set;}
        private void PreparePlotCache(int width)
        {
            if(ReferenceEquals(cachedSamples,samples)&&cachedStart==viewStart&&cachedEnd==viewEnd&&cachedWidth==width)return;
            int count=Math.Min(width,viewEnd-viewStart+1);cachedBins=new PlotBin[count];
            cachedMinimum=double.PositiveInfinity;cachedMaximum=double.NegativeInfinity;
            for(int b=0;b<count;b++){
                var bin=new PlotBin{Start=viewStart+(int)((long)(viewEnd-viewStart+1)*b/count),End=viewStart+(int)((long)(viewEnd-viewStart+1)*(b+1)/count)-1,Low=double.PositiveInfinity,High=double.NegativeInfinity};
                for(int i=bin.Start;i<=bin.End;i++){
                    double value=samples[i];if(!Finite(value)){bin.Gap=true;continue;}
                    bin.Low=Math.Min(bin.Low,value);bin.High=Math.Max(bin.High,value);double amplitude=Math.Abs(value-center);
                    if(bin.Peak<0||amplitude>bin.Amplitude){bin.Peak=i;bin.Amplitude=amplitude;bin.Extreme=value;}
                }
                cachedMinimum=Math.Min(cachedMinimum,bin.Low);cachedMaximum=Math.Max(cachedMaximum,bin.High);
                int peak=bin.Peak;bin.IsPeak=peak>0&&peak<samples.Length-1&&Finite(samples[peak-1])&&Finite(samples[peak+1])&&bin.Amplitude>=Math.Abs(samples[peak-1]-center)&&bin.Amplitude>Math.Abs(samples[peak+1]-center);
                bin.First=samples[bin.Start];bin.Last=samples[bin.End];
                cachedBins[b]=bin;
            }
            cachedSamples=samples;cachedStart=viewStart;cachedEnd=viewEnd;cachedWidth=width;PlotCacheBuildCount++;
        }
        internal ReviewLocationOverview()
        {
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            BackColor=ReviewTheme.Surface; ForeColor=ReviewTheme.Ink;var ownedFont=ReviewTheme.RegularFont();Font=ownedFont;Disposed+=(s,e)=>ownedFont.Dispose();
            MinimumSize=new Size(200,100); TabStop=true;
        }
        internal bool EnglishVersion { get { return english; } set { english=value; Invalidate(); } }
        internal string CurrentFilePath { get { return filePath; } }
        internal string LastError { get { return lastError; } }
        internal int SampleCount { get { return samples.Length; } }
        internal double SampleRateHz { get { return sampleRate; } }
        internal double VelocityMPerUs {get;private set;}=double.NaN;
        internal double CurrentThresholdPc { get { return threshold; } }
        internal bool ThresholdIsEstimated { get { return estimated; } }
        internal double BaselinePc { get { return center; } }
        internal double MaximumAmplitudePc { get { return maximum; } }
        internal int ViewStartIndex { get { return viewStart; } }
        internal int ViewEndIndex { get { return viewEnd; } }
        internal double[] GetSamples() { return (double[])samples.Clone(); }
        internal bool LoadFile(string path,string projectPath)
        {
            try
            {
                ReviewLocationFileData data=ReviewCapacitorDemInput.IsDem(path)?ReviewCapacitorDemInput.Display(path,projectPath):ReviewLocationFileReader.Read(path,projectPath);
                ApplyFileData(data);return true;
            }
            catch(Exception ex)
            {
                ClearData(); lastError=ex.Message; Invalidate(); return false;
            }
        }
        // Read/validate can run off the UI thread; applying a completed snapshot
        // remains on the owning thread and never marks a file as analysed.
        internal void ApplyFileData(ReviewLocationFileData data)
        {
            if(data==null)throw new ArgumentNullException("data");
            SetData(data.ChargeSamples,data.SampleRateHz,double.NaN);acquisitionReferenceOnly=data.AcquisitionQuality.ReferenceOnly;
            VelocityMPerUs=data.VelocityMPerUs;filePath=data.DataPath;lastError="";Invalidate();
        }
        internal void ClearData()
        { samples=new double[0];cachedSamples=null;cachedBins=new PlotBin[0]; threshold=double.NaN; center=noise=maximum=0; viewStart=viewEnd=0;verticalViewReady=false; filePath=""; lastError=""; acquisitionReferenceOnly=false;VelocityMPerUs=double.NaN; ResetMeasurement();Invalidate(); }
        internal void SetData(double[] chargeSamples,double sampleRateHz,double thresholdPc)
        {
            if(chargeSamples==null) throw new ArgumentNullException("chargeSamples");
            if(chargeSamples.Length>ReviewLocationFileReader.MaximumSamples) throw new ArgumentException("Waveform exceeds preview sample limit.");
            if(!Finite(sampleRateHz)||sampleRateHz<=0) throw new ArgumentOutOfRangeException("sampleRateHz");
            if(!double.IsNaN(thresholdPc)&&(!Finite(thresholdPc)||thresholdPc<0||thresholdPc>1e100)) throw new ArgumentOutOfRangeException("thresholdPc");
            double[] next=(double[])chargeSamples.Clone();
            foreach(double sample in next) if(Finite(sample)&&Math.Abs(sample)>1e100) throw new ArgumentOutOfRangeException("chargeSamples","Charge is outside the numeric display range.");
            // Array callers may deliberately preserve a missing-sample gap. Do
            // not bridge or invent that data; file loading rejects malformed rows.
            samples=next;cachedSamples=null;cachedBins=new PlotBin[0]; sampleRate=sampleRateHz; threshold=thresholdPc; estimated=false;
            filePath=""; lastError="";acquisitionReferenceOnly=false;VelocityMPerUs=double.NaN; ResetView(); EstimateBaseline(); Invalidate();
        }
        internal void SetThreshold(double thresholdPc,bool isEstimate=false)
        {
            if(!double.IsNaN(thresholdPc)&&(!Finite(thresholdPc)||thresholdPc<0||thresholdPc>1e100)) throw new ArgumentOutOfRangeException("thresholdPc");
            threshold=thresholdPc; estimated=isEstimate; Invalidate();
        }
        internal double EstimateAutomaticThreshold(double multiplier)
        {
            if(!Finite(multiplier)||multiplier<0) throw new ArgumentOutOfRangeException("multiplier");
            if(samples.Length==0) return double.NaN;
            double sigma=Math.Max(noise,Math.Max(1e-15,maximum*1e-8));
            double scale=multiplier>0?multiplier:3;
            return Math.Min(maximum,Math.Max(5,Math.Sqrt(2*Math.Log(Math.Max(2,samples.Length))))*sigma*scale);
        }
        internal void SetAutomaticThresholdPreview(double multiplier)
        { SetThreshold(EstimateAutomaticThreshold(multiplier),true); }
        internal void ResetView() { viewStart=0; viewEnd=Math.Max(0,samples.Length-1);verticalViewReady=false; ResetMeasurement();Invalidate(); }
        void EnsureVerticalView()
        {
            if(verticalViewReady)return;
            double low=double.PositiveInfinity,high=double.NegativeInfinity;
            foreach(double value in samples)if(Finite(value)){low=Math.Min(low,value);high=Math.Max(high,value);}
            if(!Finite(low)){low=-1;high=1;}
            if(Finite(threshold)){low=Math.Min(low,center-threshold);high=Math.Max(high,center+threshold);}
            double span=high-low;if(!Finite(span)||span<=1e-12){low=Math.Min(low,-1);high=Math.Max(high,1);span=high-low;}
            viewYMinimum=low-span*.08;viewYMaximum=high+span*.08;verticalViewReady=true;
        }
        private void EstimateBaseline()
        {
            List<double> probe=new List<double>();
            int stride=Math.Max(1,samples.Length/4096);
            for(int i=0;i<samples.Length;i+=stride) if(Finite(samples[i])) probe.Add(samples[i]);
            if(probe.Count==0) { center=noise=maximum=0; return; }
            probe.Sort(); center=Median(probe);
            for(int i=0;i<probe.Count;i++) probe[i]=Math.Abs(probe[i]-center);
            probe.Sort(); noise=1.4826*Median(probe); maximum=0;
            for(int i=0;i<samples.Length;i++) if(Finite(samples[i])) maximum=Math.Max(maximum,Math.Abs(samples[i]-center));
        }
        private static double Median(List<double> values)
        { int mid=values.Count/2; return values.Count%2==0?values[mid-1]/2+values[mid]/2:values[mid]; }
        internal int[] GetHighlightedPeakIndices(int maximumCount)
        {
            if(maximumCount<0||maximumCount>1000000) throw new ArgumentOutOfRangeException("maximumCount");
            List<int> peaks=new List<int>(); if(!Finite(threshold)||maximumCount==0) return peaks.ToArray();
            for(int i=Math.Max(1,viewStart);i<=Math.Min(samples.Length-2,viewEnd)&&peaks.Count<maximumCount;i++)
            {
                if(!Finite(samples[i-1])||!Finite(samples[i])||!Finite(samples[i+1])) continue;
                double value=Math.Abs(samples[i]-center);
                if(value>threshold&&value>=Math.Abs(samples[i-1]-center)&&value>Math.Abs(samples[i+1]-center)) peaks.Add(i);
            }
            return peaks.ToArray();
        }
        internal Rectangle PlotRectangle { get { int top=8+(CursorsVisible?4:2)*HeaderLineHeight,left=Math.Max(68,Font.Height*4);return new Rectangle(left,top,Math.Max(1,ClientSize.Width-left-22),Math.Max(1,ClientSize.Height-top-2*AxisLineHeight-4)); } }
        internal bool LastPaintUsedRawSamples { get;private set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g=e.Graphics; Rectangle plot=PlotRectangle;
            string title=filePath.Length==0?L("Location waveform","定位数据波形"):Path.GetFileName(filePath);
            if(acquisitionReferenceOnly)title=L("Overrange · reference only · ","超量程 · 仅供参考 · ")+title;
            string thresholdLabel=Finite(threshold)?L("Threshold ","阈值 ")+threshold.ToString("0.##",CultureInfo.InvariantCulture)+" pC"+(estimated?L(" (preview estimate)","（预览估计）"):""):L("Threshold not set","未设置阈值");
            const TextFormatFlags headerFlags=TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g,title,HeaderFont,HeaderRow(0),Color.FromArgb(34,51,63),headerFlags);
            int unitWidth=TextRenderer.MeasureText(g,L("Charge / nC","放电量 / nC"),Font).Width+10;
            TextRenderer.DrawText(g,thresholdLabel,HeaderFont,HeaderRow(1,unitWidth),Color.FromArgb(132,73,6),headerFlags);
            if(samples.Length==0)
            {
                TextRenderer.DrawText(g,lastError.Length>0?lastError:L("Select a location record","请选择定位数据"),Font,plot,Color.Gray,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak); return;
            }
            PreparePlotCache(plot.Width);
            EnsureVerticalView();double minimum=viewYMinimum,maximumY=viewYMaximum;
            double ySpan=maximumY-minimum;
            if(!Finite(ySpan)||ySpan<=0) { minimum=-1; maximumY=1; ySpan=2; }
            double chargeScale=Math.Max(Math.Abs(minimum),Math.Abs(maximumY))>=1000?1000:1;
            Func<double,float> y=value=>(float)(plot.Bottom-(value-minimum)/ySpan*plot.Height);
            int indexSpan=Math.Max(1,viewEnd-viewStart);
            // Fill first: grid, thresholds, raw waveform and cursor marks retain
            // their original contrast above this subtle propagation-range tint.
            PaintReflectionRange(g,plot);
            using(Pen grid=new Pen(Color.FromArgb(231,236,240)))
            using(Pen border=new Pen(Color.FromArgb(150,166,176)))
            {
                for(int tick=0;tick<=4;tick++)
                {
                    float px=plot.Left+plot.Width*tick/4F,py=plot.Top+plot.Height*tick/4F;
                    g.DrawLine(grid,px,plot.Top,px,plot.Bottom); g.DrawLine(grid,plot.Left,py,plot.Right,py);
                    double time=(viewStart+(viewEnd-viewStart)*tick/4.0)/sampleRate*TimeDisplayScale;
                    int tickWidth=Math.Max(64,Font.Height*5);
                    TextRenderer.DrawText(g,time.ToString("0.###",CultureInfo.InvariantCulture),Font,new Rectangle((int)px-tickWidth/2,plot.Bottom+3,tickWidth,AxisLineHeight),ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                    double charge=(maximumY-ySpan*tick/4)/chargeScale;
                    TextRenderer.DrawText(g,charge.ToString(chargeScale==1?"0":"0.0",CultureInfo.InvariantCulture),Font,new Rectangle(0,(int)py-AxisLineHeight/2,plot.Left-6,AxisLineHeight),ForeColor,TextFormatFlags.Right|TextFormatFlags.VerticalCenter);
                }
                g.DrawRectangle(border,plot);
            }
            TextRenderer.DrawText(g,L("Charge / ","放电量 / ")+(chargeScale==1?"pC":"nC"),Font,new Rectangle(plot.Right-unitWidth,4+HeaderLineHeight,unitWidth,HeaderLineHeight),ForeColor,TextFormatFlags.Right|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g,L("Time / ","时间 / ")+TimeDisplayUnit,Font,new Rectangle(plot.Left,plot.Bottom+3+AxisLineHeight,plot.Width,AxisLineHeight),ForeColor,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            GraphicsState saved=g.Save(); g.SetClip(plot);
            using(Pen waveform=new Pen(Color.FromArgb(80,137,91),1F))
            using(Pen limit=new Pen(Color.FromArgb(216,131,25),1.2F){DashStyle=DashStyle.Dash})
            using(Brush peakBrush=new SolidBrush(Color.FromArgb(219,112,20)))
            {
                if(Finite(threshold)) { g.DrawLine(limit,plot.Left,y(center+threshold),plot.Right,y(center+threshold)); g.DrawLine(limit,plot.Left,y(center-threshold),plot.Right,y(center-threshold)); }
                // Zoom redraws recorded samples; overview joins bin boundaries
                // instead of leaving disconnected vertical strokes.
                PointF? previous=null;
                LastPaintUsedRawSamples=(long)viewEnd-viewStart+1<=4L*plot.Width;
                if(LastPaintUsedRawSamples)
                {
                    waveform.Width=1.25F;g.SmoothingMode=SmoothingMode.AntiAlias;
                    for(int i=viewStart;i<=viewEnd;i++){
                        if(!Finite(samples[i])){previous=null;continue;}
                        var current=new PointF(SampleX(i,plot),y(samples[i]));
                        if(previous.HasValue)g.DrawLine(waveform,previous.Value,current);previous=current;
                    }
                }
                else foreach(var bin in cachedBins){
                    if(bin.Peak<0){previous=null;continue;}
                    float x=SampleX((bin.Start+bin.End)/2.0,plot);
                    if(previous.HasValue&&Finite(bin.First))g.DrawLine(waveform,previous.Value,new PointF(SampleX(bin.Start,plot),y(bin.First)));
                    if(!bin.Gap)g.DrawLine(waveform,x,y(bin.Low),x,y(bin.High));
                    previous=Finite(bin.Last)?new PointF(SampleX(bin.End,plot),y(bin.Last)):(PointF?)null;
                }
                foreach(var bin in cachedBins){
                    float x=SampleX(bin.Peak,plot);
                    if(Finite(threshold)&&bin.Amplitude>threshold&&bin.IsPeak)
                        g.FillEllipse(peakBrush,x-2,y(bin.Extreme)-2,4,4);
                }
            }
            g.Restore(saved);
            PaintMeasurement(g,plot);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);ApplyAxisWheel(e,(ModifierKeys&Keys.Shift)!=0);
        }
        internal void ApplyAxisWheel(MouseEventArgs e,bool vertical)
        {
            if(e.Delta==0||samples.Length<3||!PlotRectangle.Contains(e.Location))return;
            EnsureVerticalView();
            if(vertical)
            {
                double factor=e.Delta>0?.7:1/.7,span=(viewYMaximum-viewYMinimum)*factor;
                if(!Finite(span)||span<1e-12||span>1e102)return;
                double anchorY=viewYMaximum-(e.Y-PlotRectangle.Top)*(viewYMaximum-viewYMinimum)/PlotRectangle.Height;
                viewYMinimum=anchorY+(viewYMinimum-anchorY)*factor;viewYMaximum=viewYMinimum+span;Invalidate();return;
            }
            int count=viewEnd-viewStart+1;
            int next=Math.Max(Math.Min(16,samples.Length),Math.Min(samples.Length,(int)Math.Round(count*(e.Delta>0?.7:1/.7))));
            double fraction=Math.Max(0,Math.Min(1,(e.X-PlotRectangle.Left)/(double)PlotRectangle.Width));
            int anchor=viewStart+(int)Math.Round((count-1)*fraction);
            viewStart=Math.Max(0,Math.Min(samples.Length-next,anchor-(int)Math.Round((next-1)*fraction))); viewEnd=viewStart+next-1; EnsureMeasurementVisible();Invalidate();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if(e.Button==MouseButtons.Left) ResetView(); }
        private static bool Finite(double value) { return !double.IsNaN(value)&&!double.IsInfinity(value); }
    }
}
