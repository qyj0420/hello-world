using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
namespace HVX.Controls
{
    internal sealed class BrowserSeries
    {
        internal string Name;
        internal double[] X, Y;
        internal Color Color;
        internal bool Secondary, Dashed;
    }
    internal sealed class BrowserPlot : Control
    {
        BrowserSeries[] series = new BrowserSeries[0];
        double xmin, xmax = 1, fullmin, fullmax = 1;
        string xlabel = "", left = "", right = "", title = "";
        bool singleCalibrationPresentation;
        // Keep source indices until projection. Storing raw data in PointF loses small
        // differences when a waveform has a large DC offset or a large time origin.
        readonly Dictionary<BrowserSeries, List<int[]>> cache = new Dictionary<BrowserSeries, List<int[]>>();
        int cacheWidth = -1; double cacheMin, cacheMax;
        MeasuredLayout measuredLayout;
        float layoutDpiX,layoutDpiY;
        Bitmap staticLayer;
        Point? lastMousePoint;
        int layoutBuildCount,sourceCacheBuildCount,staticRenderCount;
        internal int LayoutBuildCount {get{return layoutBuildCount;}}
        internal int SourceCacheBuildCount {get{return sourceCacheBuildCount;}}
        internal int StaticRenderCount {get{return staticRenderCount;}}
        bool rangesValid,verticalViewAdjusted; double rangeMin, rangeMax;
        int legendPage;
        readonly ToolTip legendTip=new ToolTip();string lastLegendTip="";
        internal double CableIncidentX=double.NaN,CableEndX=double.NaN,CableVelocityMPerUs=double.NaN;
        internal double[] CandidateX=new double[0];
        internal string[] CandidateLabels=new string[0];
        internal event Action<double> CableDoubleClick;
        bool selecting;Point selectStart,selectEnd;double hoverX=double.NaN;
        internal void SetCableOverlay(double incident,double end,double velocity,double[] candidates=null,string[] labels=null)
        {CableIncidentX=incident;CableEndX=end;CableVelocityMPerUs=velocity;CandidateX=candidates??new double[0];CandidateLabels=labels??new string[0];ClearHover();InvalidateStatic(false);}
        internal bool HasCableOverlay {get{return Finite(CableIncidentX)&&Finite(CableEndX)&&CableEndX>CableIncidentX&&Finite(CableVelocityMPerUs)&&CableVelocityMPerUs>0;}}
        internal double CableDistanceAt(double x){return HasCableOverlay?(x-CableIncidentX)*CableVelocityMPerUs/2:double.NaN;}
        internal BrowserPlot() { DoubleBuffered = true; BackColor = BrowserTheme.Surface; ForeColor = BrowserTheme.Ink; Font = new Font("Arial",10); SetStyle(ControlStyles.ResizeRedraw,true); }
        internal void SetData(string heading,string x,string y,string y2,params BrowserSeries[] data)
        {UpdateData(heading,x,y,y2,false,data);}
        internal void SetSingleCalibrationData(string x,string amplitudeUnit,BrowserSeries data)
        {
            SetData("",x,"","",data);
            singleCalibrationPresentation=true;
            AccessibleName=data.Name;
            AccessibleDescription=amplitudeUnit;
            InvalidateStatic(true);
        }
        // Replace only the plotted snapshot. Selection changes must not reset a
        // user's horizontal inspection window when the time coordinate is stable.
        internal void UpdateData(string heading,string x,string y,string y2,bool preserveHorizontal,params BrowserSeries[] data)
        {
            double oldMin=xmin,oldMax=xmax,oldFullMin=fullmin,oldFullMax=fullmax;
            bool zoomed=preserveHorizontal&&series.Length>0&&
                (oldMin>oldFullMin+Math.Abs(oldFullMax-oldFullMin)*1e-9||oldMax<oldFullMax-Math.Abs(oldFullMax-oldFullMin)*1e-9);
            bool keepVertical=preserveHorizontal&&rangesValid&&verticalViewAdjusted&&series.Length>0&&data!=null&&data.Length>0&&left==y&&right==y2;
            singleCalibrationPresentation=false;
            title=heading; xlabel=x;left=y;right=y2;series=data??new BrowserSeries[0];
            SetCableOverlay(double.NaN,double.NaN,double.NaN);selecting=false;Capture=false;
            var values=series.SelectMany(s=>s.X).Where(Finite).ToArray();
            fullmin=values.Length==0?0:values.Min(); fullmax=values.Length==0?1:values.Max();
            if(fullmax<=fullmin)fullmax=fullmin+1;
            xmin=fullmin;xmax=fullmax;
            if(zoomed&&series.Length>0&&oldMax>fullmin&&oldMin<fullmax)
            {
                double span=Math.Min(oldMax-oldMin,fullmax-fullmin);
                xmin=Math.Max(fullmin,Math.Min(oldMin,fullmax-span));xmax=xmin+span;
            }
            cacheWidth=-1;cache.Clear();if(!keepVertical){rangesValid=false;verticalViewAdjusted=false;}if(!preserveHorizontal)legendPage=0;InvalidateStatic(true);
        }
        internal static bool Finite(double v){return !double.IsNaN(v)&&!double.IsInfinity(v);}
        internal sealed class MeasuredLayout
        {
            internal Rectangle Heading, Legend, PreviousPage, NextPage, PageInfo, LeftTitle, RightTitle, Plot, XTicks, XTitle;
            internal Rectangle[] LegendCells;
            internal int Columns, Rows, PageSize, Pages, LineHeight, Gap, Padding, SampleWidth, XDivisions, YDivisions;
            internal bool CanDrawPlot;
        }
        const TextFormatFlags TextFlags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
        Size Measure(Graphics graphics,string text)
        {return TextRenderer.MeasureText(graphics,string.IsNullOrEmpty(text)?" ":text,Font,new Size(int.MaxValue,int.MaxValue),TextFlags);}
        internal MeasuredLayout MeasureLayout(Graphics graphics)
        {
            if(measuredLayout!=null&&layoutDpiX==graphics.DpiX&&layoutDpiY==graphics.DpiY)return measuredLayout;
            ReleaseStaticLayer();
            lastMousePoint=null;
            EnsureRanges();
            layoutBuildCount++;
            var layout=new MeasuredLayout();
            layout.LineHeight=Math.Max(Font.Height,Measure(graphics,"校准 Ag 0123456789").Height);
            layout.Gap=Math.Max(4,(int)Math.Ceiling(layout.LineHeight*.22));
            layout.Padding=Math.Max(8,(int)Math.Ceiling(layout.LineHeight*.45));
            int pad=layout.Padding,gap=layout.Gap,line=layout.LineHeight;
            int leftTickWidth=0,rightTickWidth=0;
            for(int i=0;i<=5;i++)
            {
                leftTickWidth=Math.Max(leftTickWidth,Measure(graphics,(lo1+(hi1-lo1)*i/5).ToString("0.###",CultureInfo.InvariantCulture)).Width);
                rightTickWidth=Math.Max(rightTickWidth,Measure(graphics,(lo2+(hi2-lo2)*i/5).ToString("0.###",CultureInfo.InvariantCulture)).Width);
            }
            int firstX=Measure(graphics,xmin.ToString("0.####",CultureInfo.InvariantCulture)).Width;
            int lastX=Measure(graphics,xmax.ToString("0.####",CultureInfo.InvariantCulture)).Width;
            int leftMargin=Math.Max(leftTickWidth+pad+gap,firstX/2+pad);
            int rightMargin=Math.Max(lastX/2+pad,string.IsNullOrEmpty(right)?pad:rightTickWidth+pad+gap);
            int plotWidth=Math.Max(1,Width-leftMargin-rightMargin);
            layout.Heading=new Rectangle(pad,pad,Math.Max(1,Width-pad*2),line);
            layout.SampleWidth=Math.Max(20,line+gap);
            int desiredCell=Math.Max(180,Measure(graphics,"12. ALL_100nC 校准").Width+layout.SampleWidth+gap*3);
            layout.Columns=Math.Max(1,plotWidth/desiredCell);
            int requiredRows=Math.Max(1,(series.Length+layout.Columns-1)/layout.Columns);
            int rowHeight=line+gap;
            // Reserve independent lines for heading, axis titles, tick labels and
            // X caption, then spend only the remaining height on legend rows.
            int fixedHeight=pad*2+line*4+line/2+gap*7;
            int rowBudget=Math.Max(1,Math.Min(4,(Height-fixedHeight-Math.Max(60,line*3))/rowHeight));
            layout.Rows=Math.Min(requiredRows,rowBudget);
            if(requiredRows>layout.Rows&&layout.Rows>1)layout.Rows--;
            layout.PageSize=layout.Columns*layout.Rows;
            layout.Pages=Math.Max(1,(series.Length+layout.PageSize-1)/layout.PageSize);
            int legendY=layout.Heading.Bottom+gap;
            layout.Legend=new Rectangle(leftMargin,legendY,plotWidth,layout.Rows*rowHeight);
            layout.LegendCells=new Rectangle[layout.PageSize];
            for(int i=0;i<layout.LegendCells.Length;i++)
            {
                int column=i%layout.Columns;
                int x1=leftMargin+plotWidth*column/layout.Columns,x2=leftMargin+plotWidth*(column+1)/layout.Columns;
                layout.LegendCells[i]=new Rectangle(x1,legendY+i/layout.Columns*rowHeight,Math.Max(1,x2-x1-gap),line);
            }
            int y=layout.Legend.Bottom;
            if(layout.Pages>1)
            {
                int button=Math.Min(Math.Max(line*2,Measure(graphics,"◀ 上页").Width+gap*2),Math.Max(1,plotWidth/3));
                layout.PreviousPage=new Rectangle(leftMargin,y,button,line);
                layout.NextPage=new Rectangle(layout.PreviousPage.Right+gap,y,button,line);
                layout.PageInfo=new Rectangle(layout.NextPage.Right+gap,y,Math.Max(1,leftMargin+plotWidth-layout.NextPage.Right-gap),line);
                y+=line+gap;
            }
            int titleWidth=string.IsNullOrEmpty(right)?plotWidth:Math.Max(1,(plotWidth-gap)/2);
            layout.LeftTitle=new Rectangle(leftMargin,y+gap,titleWidth,line);
            if(!string.IsNullOrEmpty(right))layout.RightTitle=new Rectangle(leftMargin+plotWidth-titleWidth,y+gap,titleWidth,line);
            int top=layout.LeftTitle.Bottom+gap+(line+1)/2;
            if(singleCalibrationPresentation)
            {
                layout.Heading=layout.Legend=layout.LeftTitle=layout.RightTitle=Rectangle.Empty;
                layout.PreviousPage=layout.NextPage=layout.PageInfo=Rectangle.Empty;
                layout.LegendCells=new Rectangle[0];layout.PageSize=0;layout.Pages=1;
                top=pad+(line+1)/2;
            }
            int bottom=Height-pad-line*2-gap*2;
            layout.Plot=new Rectangle(leftMargin,top,plotWidth,Math.Max(1,bottom-top));
            layout.XTicks=new Rectangle(0,bottom+gap,Width,line);
            layout.XTitle=new Rectangle(leftMargin,bottom+gap*2+line,plotWidth,line);
            layout.CanDrawPlot=plotWidth>=40&&bottom-top>=Math.Max(30,line);
            int xTickWidth=0;
            for(int divisions=1;divisions<=5;divisions++)for(int i=0;i<=divisions;i++)
                xTickWidth=Math.Max(xTickWidth,Measure(graphics,(xmin+(xmax-xmin)*i/divisions).ToString("0.####",CultureInfo.InvariantCulture)).Width);
            layout.XDivisions=Math.Max(1,Math.Min(5,plotWidth/Math.Max(1,xTickWidth+gap*2)));
            layout.YDivisions=Math.Max(1,Math.Min(5,layout.Plot.Height/(line+gap)));
            layoutDpiX=graphics.DpiX;layoutDpiY=graphics.DpiY;
            measuredLayout=layout;
            return layout;
        }
        MeasuredLayout CurrentLayout {get{if(measuredLayout!=null&&layoutDpiX==DeviceDpi&&layoutDpiY==DeviceDpi)return measuredLayout;using(var graphics=CreateGraphics())return MeasureLayout(graphics);}}
        void ReleaseStaticLayer()
        {var old=staticLayer;staticLayer=null;if(old!=null)old.Dispose();}
        void InvalidateStatic(bool layoutChanged)
        {ReleaseStaticLayer();if(layoutChanged)measuredLayout=null;lastMousePoint=null;Invalidate();}
        void ClearHover()
        {hoverX=double.NaN;lastMousePoint=null;if(lastLegendTip.Length>0){lastLegendTip="";legendTip.SetToolTip(this,"");}}
        protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);InvalidateStatic(true);}
        protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);InvalidateStatic(true);}
        protected override void OnBackColorChanged(EventArgs e){base.OnBackColorChanged(e);InvalidateStatic(false);}
        protected override void OnForeColorChanged(EventArgs e){base.OnForeColorChanged(e);InvalidateStatic(false);}
        protected override void OnHandleDestroyed(EventArgs e){ReleaseStaticLayer();measuredLayout=null;base.OnHandleDestroyed(e);}
        Rectangle Plot {get{return CurrentLayout.Plot;}}
        protected override void OnMouseWheel(MouseEventArgs e)
        {base.OnMouseWheel(e);ApplyWheelZoom(e,(ModifierKeys&Keys.Shift)!=0);}
        internal void ApplyWheelZoom(MouseEventArgs e,bool vertical)
        {
            if(e.Delta==0)return;
            var layout=CurrentLayout;var p=layout.Plot;if(layout.Pages>1&&(layout.Legend.Contains(e.Location)||layout.PreviousPage.Contains(e.Location)||layout.NextPage.Contains(e.Location)||layout.PageInfo.Contains(e.Location))){int page=Math.Max(0,Math.Min(layout.Pages-1,legendPage+(e.Delta>0?-1:1)));if(page!=legendPage){legendPage=page;ClearHover();InvalidateStatic(false);}return;}if(!layout.CanDrawPlot||!p.Contains(e.Location))return;
            if(vertical){EnsureRanges();double f=(p.Bottom-e.Y)/(double)p.Height,k=e.Delta>0?.8:1.25;ZoomVertical(ref lo1,ref hi1,f,k);ZoomVertical(ref lo2,ref hi2,f,k);verticalViewAdjusted=true;ClearHover();InvalidateStatic(true);return;}
            double at=xmin+(xmax-xmin)*(e.X-p.Left)/p.Width, span=(xmax-xmin)*(e.Delta>0?.8:1.25);
            span=Math.Min(fullmax-fullmin,Math.Max((fullmax-fullmin)/10000,span));
            double lo=at-span*(e.X-p.Left)/p.Width;lo=Math.Max(fullmin,Math.Min(fullmax-span,lo));if(xmin==lo&&xmax==lo+span)return;xmin=lo;xmax=lo+span;ClearHover();InvalidateStatic(true);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);Focus();var layout=CurrentLayout;
            if(e.Button==MouseButtons.Left&&layout.Pages>1&&(layout.PreviousPage.Contains(e.Location)||layout.NextPage.Contains(e.Location)))
            {int page=Math.Max(0,Math.Min(layout.Pages-1,legendPage+(layout.PreviousPage.Contains(e.Location)?-1:1)));if(page!=legendPage){legendPage=page;ClearHover();InvalidateStatic(false);}return;}
            if(e.Button==MouseButtons.Right){selecting=false;Capture=false;xmin=fullmin;xmax=fullmax;rangesValid=false;verticalViewAdjusted=false;ClearHover();InvalidateStatic(true);}
            else if(e.Button==MouseButtons.Left&&e.Clicks==1&&layout.CanDrawPlot&&layout.Plot.Contains(e.Location))
            {InvalidateCursor(layout.Plot,hoverX);ClearHover();selecting=true;selectStart=selectEnd=e.Location;Capture=true;}
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);if(!selecting)return;selecting=false;Capture=false;lastMousePoint=null;
            Rectangle p=CurrentLayout.Plot,r=Rectangle.FromLTRB(Math.Min(selectStart.X,selectEnd.X),Math.Min(selectStart.Y,selectEnd.Y),Math.Max(selectStart.X,selectEnd.X),Math.Max(selectStart.Y,selectEnd.Y));
            if(r.Width>=8&&r.Height>=8&&p.Width>0&&p.Height>0){EnsureRanges();double span=xmax-xmin,old=xmin;xmin=old+span*(r.Left-p.Left)/p.Width;xmax=old+span*(r.Right-p.Left)/p.Width;double low=lo1,ys=hi1-lo1;lo1=low+ys*(p.Bottom-r.Bottom)/p.Height;hi1=low+ys*(p.Bottom-r.Top)/p.Height;verticalViewAdjusted=true;ClearHover();InvalidateStatic(true);}else Invalidate(Rectangle.Inflate(r,2,2));
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {base.OnMouseDoubleClick(e);var selection=SelectionBounds();selecting=false;Capture=false;lastMousePoint=null;var p=CurrentLayout.Plot;if(e.Button==MouseButtons.Left&&HasCableOverlay&&p.Contains(e.Location)){double x=xmin+(xmax-xmin)*(e.X-p.Left)/p.Width;if(x>CableIncidentX&&x<CableEndX)CableDoubleClick?.Invoke(x);}if(!selection.IsEmpty)Invalidate(Rectangle.Inflate(selection,2,2));}
        static void ZoomVertical(ref double low,ref double high,double fraction,double factor){double span=(high-low)*factor;if(!Finite(span)||span<1e-15||span>1e100)return;double anchor=low+(high-low)*fraction;low=anchor-span*fraction;high=low+span;}
        Rectangle SelectionBounds(){return selecting?Rectangle.FromLTRB(Math.Min(selectStart.X,selectEnd.X),Math.Min(selectStart.Y,selectEnd.Y),Math.Max(selectStart.X,selectEnd.X),Math.Max(selectStart.Y,selectEnd.Y)):Rectangle.Empty;}
        void InvalidateCursor(Rectangle plot,double x)
        {if(Finite(x)&&plot.Width>0){int px=(int)Math.Round(plot.Left+(x-xmin)/(xmax-xmin)*plot.Width);Invalidate(Rectangle.Intersect(ClientRectangle,new Rectangle(px-2,plot.Top,5,plot.Height+1)));}}
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(lastMousePoint.HasValue&&lastMousePoint.Value==e.Location)return;lastMousePoint=e.Location;
            string text="";var layout=CurrentLayout;var p=layout.Plot;
            if(selecting)
            {
                var next=new Point(Math.Max(p.Left,Math.Min(p.Right,e.X)),Math.Max(p.Top,Math.Min(p.Bottom,e.Y)));
                if(next!=selectEnd){var old=SelectionBounds();selectEnd=next;Invalidate(Rectangle.Inflate(Rectangle.Union(old,SelectionBounds()),2,2));}return;
            }
            for(int i=0;i<layout.LegendCells.Length;i++)if(layout.LegendCells[i].Contains(e.Location)){int index=Math.Min(legendPage,layout.Pages-1)*layout.PageSize+i;if(index<series.Length)text=series[index].Name;break;}
            if(singleCalibrationPresentation&&p.Contains(e.Location))text=AccessibleDescription;
            double oldHover=hoverX;hoverX=double.NaN;
            if(HasCableOverlay&&p.Contains(e.Location))
            {
                hoverX=xmin+(xmax-xmin)*(e.X-p.Left)/p.Width;
                if(hoverX==oldHover)return; // Moving vertically in the same pixel column changes neither time nor distance.
                text="Δt = "+(hoverX-CableIncidentX).ToString("0.####",CultureInfo.InvariantCulture)+" μs · "+CableDistanceAt(hoverX).ToString("0.0",CultureInfo.InvariantCulture)+" m";
                if(hoverX<CableIncidentX||hoverX>CableEndX)text+=" · 电缆范围外 / Outside cable";
                for(int i=0;i<CandidateX.Length&&i<CandidateLabels.Length;i++)if(Math.Abs(CandidateX[i]-hoverX)<(xmax-xmin)*7/Math.Max(1,p.Width)){text+=Environment.NewLine+CandidateLabels[i];break;}
            }
            if(text!=lastLegendTip){lastLegendTip=text;legendTip.SetToolTip(this,text);}
            if(!oldHover.Equals(hoverX)){InvalidateCursor(p,oldHover);InvalidateCursor(p,hoverX);}
        }
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(Finite(hoverX)&&measuredLayout!=null)InvalidateCursor(measuredLayout.Plot,hoverX);ClearHover();}
        protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(selecting&&!Capture){var old=SelectionBounds();selecting=false;lastMousePoint=null;Invalidate(Rectangle.Inflate(old,2,2));}}
        protected override void Dispose(bool disposing){if(disposing){ReleaseStaticLayer();measuredLayout=null;cache.Clear();series=new BrowserSeries[0];CandidateX=new double[0];CandidateLabels=new string[0];legendTip.Dispose();}base.Dispose(disposing);}
        void Range(bool secondary,out double lo,out double hi)
        {
            lo=double.PositiveInfinity;hi=double.NegativeInfinity;
            foreach(var s in series.Where(v=>v.Secondary==secondary)) for(int i=0;i<s.X.Length&&i<s.Y.Length;i++)
                if(Finite(s.Y[i])) {lo=Math.Min(lo,s.Y[i]);hi=Math.Max(hi,s.Y[i]);}
            if(!Finite(lo)){lo=0;hi=1;}if(hi<=lo){lo-=.5;hi+=.5;}
            double pad=(hi-lo)*.08;lo-=pad;hi+=pad;
        }
        double lo1,hi1,lo2,hi2;
        void EnsureRanges()
        {if(rangesValid)return;Range(false,out lo1,out hi1);Range(true,out lo2,out hi2);rangeMin=xmin;rangeMax=xmax;rangesValid=true;}
        void BuildCache(int width)
        {
            if(cacheWidth==width&&cacheMin==xmin&&cacheMax==xmax)return;
            sourceCacheBuildCount++;
            EnsureRanges();
            cache.Clear();
            foreach(var s in series)
            {
                var paths=new List<int[]>();var points=new List<int>();int n=Math.Min(s.X.Length,s.Y.Length);
                // Preserve both extrema in each pixel column so narrow PD pulses survive reduction.
                int current=-1; double min=0,max=0;int minIndex=0,maxIndex=0;
                Action flush=()=>{if(current<0)return;int first=Math.Min(minIndex,maxIndex),last=Math.Max(minIndex,maxIndex);points.Add(first);if(last!=first)points.Add(last);current=-1;};
                for(int i=0;i<n;i++)
                {
                    if(!Finite(s.X[i])||!Finite(s.Y[i])){flush();if(points.Count>0){paths.Add(points.ToArray());points.Clear();}continue;}
                    if(s.X[i]<xmin||s.X[i]>xmax)continue;
                    int bucket=(int)((s.X[i]-xmin)/(xmax-xmin)*width);
                    if(bucket!=current){flush();current=bucket;min=max=s.Y[i];minIndex=maxIndex=i;}
                    else {if(s.Y[i]<min){min=s.Y[i];minIndex=i;}if(s.Y[i]>max){max=s.Y[i];maxIndex=i;}}
                }
                flush();if(points.Count>0)paths.Add(points.ToArray());cache[s]=paths;
            }
            cacheWidth=width;cacheMin=xmin;cacheMax=xmax;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);if(Width<200||Height<160)return;
            var layout=MeasureLayout(e.Graphics);
            if(staticLayer==null)
            {
                var image=new Bitmap(Width,Height);
                try
                {
                    image.SetResolution(e.Graphics.DpiX,e.Graphics.DpiY);
                    using(var graphics=Graphics.FromImage(image)){graphics.Clear(BackColor);RenderStatic(graphics,layout);}
                    staticLayer=image;staticRenderCount++;
                }
                catch{image.Dispose();throw;}
            }
            // Windows clips this blit to the cursor/selection dirty region. No
            // waveform projection, text layout or source traversal happens here.
            e.Graphics.DrawImageUnscaled(staticLayer,0,0);
            if(!layout.CanDrawPlot)return;
            var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;var state=g.Save();g.SetClip(layout.Plot,CombineMode.Intersect);
            try
            {
                if(HasCableOverlay&&Finite(hoverX))using(var cursor=new Pen(Color.FromArgb(100,BrowserTheme.Ink))){float px=(float)(layout.Plot.Left+(hoverX-xmin)/(xmax-xmin)*layout.Plot.Width);g.DrawLine(cursor,px,layout.Plot.Top,px,layout.Plot.Bottom);}
                if(selecting){var rect=SelectionBounds();using(var shade=new SolidBrush(Color.FromArgb(30,BrowserTheme.Cyan)))g.FillRectangle(shade,rect);using(var edge=new Pen(BrowserTheme.Cyan))g.DrawRectangle(edge,rect);}
            }
            finally{g.Restore(state);}
        }
        void RenderStatic(Graphics g,MeasuredLayout layout)
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;var p=layout.Plot;
            using(var ink=new SolidBrush(ForeColor))using(var grid=new Pen(BrowserTheme.Grid))using(var border=new Pen(BrowserTheme.Border))
            {
                if(!singleCalibrationPresentation)TextRenderer.DrawText(g,title,Font,layout.Heading,ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                if(!layout.CanDrawPlot){TextRenderer.DrawText(g,"请扩大波形窗口",Font,new Rectangle(layout.Padding,layout.Heading.Bottom+layout.Gap,Math.Max(1,Width-layout.Padding*2),layout.LineHeight),ForeColor,TextFlags|TextFormatFlags.EndEllipsis);return;}
                BuildCache(p.Width);
                legendPage=Math.Min(legendPage,layout.Pages-1);int legendStart=legendPage*layout.PageSize;
                for(int si=legendStart;si<Math.Min(series.Length,legendStart+layout.PageSize);si++)
                {
                    var s=series[si];var cell=layout.LegendCells[si-legendStart];int sw=Math.Min(layout.SampleWidth,Math.Max(1,cell.Width/4));
                    using(var pen=new Pen(s.Color,2)){if(s.Dashed)pen.DashStyle=DashStyle.Dash;g.DrawLine(pen,cell.Left,cell.Top+cell.Height/2,cell.Left+sw,cell.Top+cell.Height/2);}
                    TextRenderer.DrawText(g,s.Name,Font,new Rectangle(cell.Left+sw+layout.Gap,cell.Top,Math.Max(1,cell.Width-sw-layout.Gap),cell.Height),ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                }
                if(layout.Pages>1)
                {
                    TextRenderer.DrawText(g,"◀ 上页",Font,layout.PreviousPage,ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g,"下页 ▶",Font,layout.NextPage,ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g,(legendPage+1)+" / "+layout.Pages+"  ·  "+series.Length+" 条（滚轮翻页，全部波形仍叠加）",Font,layout.PageInfo,ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                }
                for(int i=0;i<=layout.XDivisions;i++)
                {
                    float x=p.Left+p.Width*i/(float)layout.XDivisions;g.DrawLine(grid,x,p.Top,x,p.Bottom);
                    string xt=(xmin+(xmax-xmin)*i/layout.XDivisions).ToString("0.####",CultureInfo.InvariantCulture);var size=Measure(g,xt);
                    int tickX=Math.Max(0,Math.Min(Width-size.Width,(int)Math.Round(x-size.Width/2.0)));
                    TextRenderer.DrawText(g,xt,Font,new Rectangle(tickX,layout.XTicks.Top,size.Width,layout.LineHeight),ForeColor,TextFlags);
                }
                for(int i=0;i<=layout.YDivisions;i++)
                {
                    float y=p.Bottom-p.Height*i/(float)layout.YDivisions;g.DrawLine(grid,p.Left,y,p.Right,y);
                    string yt=(lo1+(hi1-lo1)*i/layout.YDivisions).ToString("0.###",CultureInfo.InvariantCulture);var size=Measure(g,yt);
                    TextRenderer.DrawText(g,yt,Font,new Rectangle(p.Left-size.Width-layout.Gap,(int)Math.Round(y-layout.LineHeight/2.0),size.Width,layout.LineHeight),ForeColor,TextFlags);
                    if(!string.IsNullOrEmpty(right))TextRenderer.DrawText(g,(lo2+(hi2-lo2)*i/layout.YDivisions).ToString("0.###",CultureInfo.InvariantCulture),Font,new Rectangle(p.Right+layout.Gap,(int)Math.Round(y-layout.LineHeight/2.0),Math.Max(1,Width-p.Right-layout.Gap),layout.LineHeight),ForeColor,TextFlags);
                }
                g.DrawRectangle(border,p);if(!singleCalibrationPresentation)TextRenderer.DrawText(g,left,Font,layout.LeftTitle,ForeColor,TextFlags|TextFormatFlags.EndEllipsis);
                if(!string.IsNullOrEmpty(right))TextRenderer.DrawText(g,right,Font,layout.RightTitle,ForeColor,TextFlags|TextFormatFlags.Right|TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g,xlabel,Font,layout.XTitle,ForeColor,TextFlags|TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis);
                var state=g.Save();g.SetClip(p);
                if(HasCableOverlay){float a=(float)(p.Left+(CableIncidentX-xmin)/(xmax-xmin)*p.Width),b=(float)(p.Left+(CableEndX-xmin)/(xmax-xmin)*p.Width);using(var shade=new SolidBrush(Color.FromArgb(22,BrowserTheme.Cyan)))g.FillRectangle(shade,a,p.Top,b-a,p.Height);using(var boundary=new Pen(Color.FromArgb(120,BrowserTheme.Cyan))){boundary.DashStyle=DashStyle.Dash;g.DrawLine(boundary,a,p.Top,a,p.Bottom);g.DrawLine(boundary,b,p.Top,b,p.Bottom);}}
                foreach(var s in series)
                {
                    double low=s.Secondary?lo2:lo1,high=s.Secondary?hi2:hi1;
                    using(var pen=new Pen(s.Color,1.5f))using(var brush=new SolidBrush(s.Color))
                    {
                        if(s.Dashed)pen.DashStyle=DashStyle.Dash;
                        foreach(var line in cache[s]){var pts=line.Select(i=>new PointF((float)(p.Left+(s.X[i]-xmin)/(xmax-xmin)*p.Width),(float)(p.Bottom-(s.Y[i]-low)/(high-low)*p.Height))).ToArray();if(pts.Length>1)g.DrawLines(pen,pts);if(s.X.Length<150)foreach(var pt in pts)g.FillEllipse(brush,pt.X-2.5f,pt.Y-2.5f,5,5);}
                    }
                }
                if(HasCableOverlay){using(var marker=new Pen(Color.FromArgb(90,48,145,155))){marker.DashStyle=DashStyle.Dash;using(var brush=new SolidBrush(Color.FromArgb(180,48,145,155)))foreach(double cx in CandidateX){float px=(float)(p.Left+(cx-xmin)/(xmax-xmin)*p.Width);g.DrawLine(marker,px,p.Top,px,p.Bottom);g.FillPolygon(brush,new[]{new PointF(px-5,p.Top+2),new PointF(px+5,p.Top+2),new PointF(px,p.Top+10)});}}}
                g.Restore(state);
                if(series.Length==0)g.DrawString("—",Font,ink,p.Left+p.Width/2,p.Top+p.Height/2);
            }
        }
    }
    internal sealed class BrowserButton:Button
    {
        internal BrowserButton(){Font=new Font("Arial",10f);FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;BackColor=BrowserTheme.Primary;ForeColor=Color.White;Height=32;Width=110;Cursor=Cursors.Hand;}
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);Invalidate();}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);Invalidate();}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.Clear(Parent==null?BrowserTheme.Surface:Parent.BackColor);
            var r=new RectangleF(.5f,.5f,Math.Max(1,Width-1),Math.Max(1,Height-1));
            bool hover=Enabled&&ClientRectangle.Contains(PointToClient(Cursor.Position));
            bool neutral=BackColor.ToArgb()==BrowserTheme.Surface.ToArgb()||BackColor.ToArgb()==BrowserTheme.SurfaceAlt.ToArgb();
            Color fill=!Enabled?BrowserTheme.Disabled:Capture?(neutral?Color.FromArgb(221,233,242):ControlPaint.Dark(BackColor,.1f))
                :hover?(neutral?BrowserTheme.Selection:ControlPaint.Light(BackColor,.08f)):BackColor;
            Color edge=Enabled?(hover?BrowserTheme.Cyan:(neutral?BrowserTheme.Border:BackColor)):BrowserTheme.Border;
            using(var path=new GraphicsPath())
            {
                float d=Math.Min(12f*Math.Max(.5f,e.Graphics.DpiX/96f),Math.Min(r.Width,r.Height));
                path.AddArc(r.Left,r.Top,d,d,180,90);path.AddArc(r.Right-d,r.Top,d,d,270,90);
                path.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);path.AddArc(r.Left,r.Bottom-d,d,d,90,90);path.CloseFigure();
                using(var brush=new SolidBrush(fill))using(var pen=new Pen(edge)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
            }
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?ForeColor:Color.FromArgb(101,117,135),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            if(Focused)using(var focus=new Pen(BrowserTheme.Cyan)){var frame=Rectangle.Inflate(ClientRectangle,-4,-4);e.Graphics.DrawRectangle(focus,frame);}
        }
    }
}
