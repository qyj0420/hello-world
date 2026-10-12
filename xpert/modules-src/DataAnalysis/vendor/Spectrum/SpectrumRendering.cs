using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        static readonly Color[] PhaseColors = { Color.FromArgb(55,95,151), Color.FromArgb(154,103,0), Color.FromArgb(46,155,98), Color.FromArgb(218,65,55) };
        // Same quantitative colour ramp as task 11's PRPD image renderer.
        static readonly Color[] DensityColorStops = { Color.FromArgb(24,72,180), Color.FromArgb(0,210,230), Color.FromArgb(250,220,35), Color.FromArgb(220,35,28) };
        static readonly double[] DensityColorPositions = { 0, .33, .66, 1 };
        sealed class RenderPalette : IDisposable
        {
            internal readonly Pen[] Markers = new Pen[4];
            internal readonly Pen[] Stems = new Pen[4];
            internal readonly Brush[] PhaseFills = new Brush[4];
            internal readonly Brush[] Density;
            internal readonly double ChargeMaximum;
            internal readonly ClassificationPalette Classification;
            internal readonly Pen NeutralLegend = new Pen(HVX.PdReview.ReviewTheme.SecondaryInk,1);
            internal readonly PointF[] Triangle = new PointF[3];
            internal RenderPalette(UnifiedSpectrumControl owner)
            {
                for(int i=0;i<4;i++)
                {Markers[i]=new Pen(PhaseColors[i],1);Stems[i]=new Pen(Color.FromArgb(80,PhaseColors[i]),1);PhaseFills[i]=new SolidBrush(Color.FromArgb(165,PhaseColors[i]));}
                if(owner.mode==12||owner.view.IsHeatmap)
                {Density=new Brush[256];for(int i=0;i<256;i++)Density[i]=new SolidBrush(DensityColor(i/255.0));}
                if(owner.UseChargeColorReference)ChargeMaximum=owner.ChargeColorMaximum();
                if(owner.ClassificationActive)Classification=new ClassificationPalette();
            }
            public void Dispose()
            {
                foreach(var p in Markers)p.Dispose();foreach(var p in Stems)p.Dispose();foreach(var b in PhaseFills)b.Dispose();NeutralLegend.Dispose();
                if(Density!=null)foreach(var b in Density)b.Dispose();
                if(Classification!=null)Classification.Dispose();
            }
        }
        Rectangle PlotArea(Rectangle b) { return PlotArea(b,ToolbarHeightForWidth(b.Width)); }
        Rectangle PlotArea(Rectangle b,int toolbarHeight)
        {
            int top=HeaderLayout(b.Width).PlotTop,cameraFooter=toolbarHeight>0&&ViewPlaneButtonsVisible?Math.Max(24,ViewActionButtonHeight-2):0;
            // Native classification controls share the interaction row, below
            // the axis title. Reserve room even while classification is off.
            if(toolbarHeight>0&&ClassificationControlVisible)cameraFooter=Math.Max(cameraFooter,ClassificationFooterReserve);
            int horizontalMargins=mode==11?232:mode==12?Math.Max(210,TextRenderer.MeasureText("<0.000000001",Font).Width+135)+(IsPhaseHorizontalPlane&&HasPhaseReference?43:0):mode==9||mode==10||HasPhaseReference?190:118;
            int footer=Math.Max(76,Font.Height*2+16)+cameraFooter;
            return new Rectangle(b.Left+82,b.Top+top,Math.Max(40,b.Width-horizontalMargins),Math.Max(40,b.Height-toolbarHeight-top-footer));
        }
        double DataX(double px, Rectangle a) { return State.X0+(px-a.Left)/a.Width*(State.X1-State.X0); }
        double DataY(double py, Rectangle a) { return State.Y0+(a.Bottom-py)/a.Height*(State.Y1-State.Y0); }
        PointF Project(SpectrumPoint p, Rectangle a)
        {
            double x=(p.X-State.X0)/(State.X1-State.X0),y=(p.Y-State.Y0)/(State.Y1-State.Y0);
            if(!view.Is3D)return new PointF((float)(a.Left+x*a.Width),(float)(a.Bottom-y*a.Height));
            double z=(p.Z-view.ZMin)/Math.Max(1e-12,view.ZMax-view.ZMin);
            return ProjectNormalized3D(x,y,z,a);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); PaintWithCache(e.Graphics); }
        string AxisLabel(string key)
        {
            switch(key){
                case "L":return L("Position / m","放电位置 / m");
                case "Q":return (L("Charge / ","放电量 / "))+ChargeUnitLabel;
                case "N":return L("Pulse count","放电次数");
                case "PHI":return L("Phase / deg","相位 / °");
                case "F":return L("Bandwidth / MHz","等效频宽 / MHz");
                case "T":return L("Duration / µs","等效时长 / µs");
                case "U":return L("Test voltage / kV","试验电压 / kV");
                case "n":return L("Test order","加压次序");
                default:return key??"";
            }
        }
        string Tick(double value,string key)
        {
            if(key=="F")value/=1e6;if(key=="T")value/=1000;if(key=="Q")value/=ChargeDisplayScale;if(Math.Abs(value)<1e-10)value=0;
            return value.ToString(key=="N"||key=="n"||key=="Q"?"0":key=="L"?"0.#":"0.###",CultureInfo.InvariantCulture);
        }
        double[] AxisTicks(double min,double max,string key,int divisions)
        {
            double span=max-min;
            if(key=="PHI")
            {
                double step=divisions<=2?180:60;
                return Enumerable.Range(0,7).Select(i=>Math.Ceiling(min/step)*step+i*step).Where(v=>v<=max).ToArray();
            }
            if(key=="N"||key=="n"||key=="Q")
            {
                double unit=key=="Q"?ChargeDisplayScale:1;
                double raw=Math.Max(1,span/unit/divisions),power=Math.Pow(10,Math.Floor(Math.Log10(raw)));
                double fraction=raw/power,step=(fraction<=1?1:fraction<=2?2:fraction<=5?5:10)*power*unit;
                double start=Math.Ceiling(min/step)*step;
                return Enumerable.Range(0,divisions+2).Select(i=>start+step*i).Where(v=>Finite(v)&&v>=min&&v<=max).ToArray();
            }
            return Enumerable.Range(0,divisions+1).Select(i=>min+span*(i/(double)divisions)).ToArray();
        }
        void Render(Graphics g,Rectangle bounds,bool interactive)
        {
            g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
            if(view==null)return;int toolbarHeight=interactive||renderingBase?ToolbarHeightForWidth(bounds.Width):0;Rectangle a=PlotArea(bounds,toolbarHeight);
            using(var palette=new RenderPalette(this))
            {
            if(!renderingBase)DrawSpectrumHeader(g,bounds,palette);
            if(view.Is3D)Draw3DAxes(g,a);else Draw2DAxes(g,a);
            var saved=g.Save();g.SetClip(a);
            DrawPhaseReference(g,a);
            DrawChargeCountOverlay(g,a);
            if(mode==9||mode==10)DrawOrderPlot(g,a);
            else if(view.IsHeatmap)DrawHeatmap(g,a,palette.Density);
            else if(view.IsHistogram&&!view.Is3D)DrawHistogram(g,a,palette.PhaseFills);
            else foreach(var p in view.Is3D ? view.Points.OrderByDescending(ProjectionDepth).AsEnumerable() : view.Points)
            {
                PointF pixel=Project(p,a);if(!Finite(pixel.X)||!Finite(pixel.Y))continue;
                int phase=Math.Max(0,Math.Min(3,p.Phase));
                if(view.Is3D&&view.YKey=="N")g.DrawLine(palette.Stems[phase],Project(new SpectrumPoint{X=p.X,Y=0,Z=p.Z},a),pixel);
                if(ClassificationActive)DrawClassifiedMarker(g,pixel,p,palette.Classification);
                else if(mode==12){DrawFilledMarker(g,pixel,p.Phase,3,palette.Density[LPrpdPointColorIndex(p,palette.ChargeMaximum)],palette.Triangle);}else DrawMarker(g,pixel,p.Phase,3,palette.Markers[phase]);
            }
            if(showJoints&&view.XKey=="L"&&!view.Is3D)DrawCable(g,a);
            DrawLocationClusters(g,a);
            DrawConclusionAnnotations(g,a);
            if(!renderingBase&&RegionSelectionEnabled)
            {
                foreach(var r in State.Regions){var p=Project(new SpectrumPoint{X=r.X0,Y=r.Y1},a);var q=Project(new SpectrumPoint{X=r.X1,Y=r.Y0},a);DrawRegion(g,new RectangleF(p.X,p.Y,q.X-p.X,q.Y-p.Y),r.Circle);}
                if(interactive&&dragging)DrawRegion(g,DragBox(),shape==1);
            }
            if(!renderingBase&&SelectedSourceRowIndex>=0){var p=view.Points.FirstOrDefault(v=>v.SourceRows.Contains(SelectedSourceRowIndex));if(p!=null){var at=Project(p,a);using(var pen=new Pen(HVX.PdReview.ReviewTheme.Active,2))g.DrawEllipse(pen,at.X-7,at.Y-7,14,14);}}
            g.Restore(saved);
            if(mode==12||view.IsHeatmap)DrawDensityScale(g,a);
            if(mode==9||mode==10)DrawOrderVoltageAxis(g,a);
            DrawPhaseReferenceLabel(g,a);
            if(visibleRows.Length==0)using(var b=new SolidBrush(HVX.PdReview.ReviewTheme.SecondaryInk))using(var emptyFont=new Font(Font.FontFamily,Font.Size+3))CenterText(g,EmptyStateText,emptyFont,b,a);
            using(var b=new SolidBrush(HVX.PdReview.ReviewTheme.SecondaryInk))using(var sf=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
            {
                string hint=RegionSelectionEnabled?(L("Drag: region   Wheel: X   Shift+wheel: Y   Double-click: select pulse","拖动：选区   滚轮：横轴   Shift+滚轮：纵轴   双击：选中脉冲")):(view.Is3D?(L("Hold left button and drag: rotate","按住鼠标左键拖动：旋转观察角度")):"");
                if(ShowChargeCountOverlay&&ChargeCountOverlayAvailable&&ChargeCountMaximum>0)hint=L("Count ","次数 ")+"1–"+ChargeCountMaximum;
                if(competitionProfile&&RegionSelectionEnabled)hint=completeReadOnlyModes?L("Drag: select region; both charts update.","拖动：选区；两图同步。"):L("Read-only: drag a region; right-click for statistics. Wheel: X; Shift+wheel: Y.","只读：拖动选区，右键区域统计。滚轮：横轴；Shift+滚轮：纵轴。");
                int hintWidth=bounds.Width-(RegionSelectionEnabled?156:ViewPlaneButtonsVisible?CameraFooterReservedWidth:LPrpdColorReferenceButtonVisible?76:24);
                if(toolbarHeight>0&&ClassificationControlVisible)hintWidth=Math.Min(hintWidth,ClassificationCheckboxBounds.Left-bounds.Left-20);
                int selectionHintOffset=CompetitionLocationSelectionPlane?78:0;
                if(!UseHostStatusBar)g.DrawString(hint,Font,b,new RectangleF(bounds.Left+12+selectionHintOffset,bounds.Bottom-toolbarHeight-28,Math.Max(20,hintWidth-selectionHintOffset),24),sf);
            }
            if(interactive&&RegionSelectionEnabled)DrawSelectionButtons(g,bounds);
            }
        }
        void Draw2DAxes(Graphics g,Rectangle a)
        {
            using(var grid=new Pen(HVX.PdReview.ReviewTheme.Grid))using(var axis=new Pen(HVX.PdReview.ReviewTheme.SecondaryInk))using(var ink=new SolidBrush(ForeColor))
            {
                foreach(double value in AxisTicks(State.X0,State.X1,view.XKey,Math.Max(1,Math.Min(5,a.Width/75))))
                {float x=a.Left+(float)((value-State.X0)/(State.X1-State.X0)*a.Width);g.DrawLine(grid,x,a.Top,x,a.Bottom);string text=Tick(value,view.XKey);var size=g.MeasureString(text,Font);g.DrawString(text,Font,ink,x-size.Width/2,a.Bottom+5);}
                foreach(double value in AxisTicks(State.Y0,State.Y1,view.YKey,Math.Max(1,Math.Min(5,a.Height/Math.Max(24,Font.Height+5)))))
                {float y=a.Bottom-(float)((value-State.Y0)/(State.Y1-State.Y0)*a.Height);g.DrawLine(grid,a.Left,y,a.Right,y);string text=Tick(value,view.YKey);var size=g.MeasureString(text,Font);g.DrawString(text,Font,ink,a.Left-size.Width-8,y-size.Height/2);}
                int labelHeight=Math.Max(24,Font.Height+4);
                g.DrawRectangle(axis,a);CenterText(g,AxisLabel(view.XKey),Font,ink,new RectangleF(a.Left,a.Bottom+Font.Height+6,a.Width,labelHeight));
                string verticalLabel=AxisLabel(view.YKey);if(view.YKey=="Q"&&TextRenderer.MeasureText(verticalLabel,Font).Width>a.Height)verticalLabel=ChargeUnitLabel;
                var save=g.Save();g.TranslateTransform(a.Left-67,a.Top+a.Height/2);g.RotateTransform(-90);CenterText(g,verticalLabel,Font,ink,new RectangleF(-a.Height/2,-labelHeight/2,a.Height,labelHeight));g.Restore(save);
            }
        }
        void Draw3DAxes(Graphics g,Rectangle a)
        {
            if(Camera.PlaneIndex!=0){DrawPlaneAxes(g,a);return;}
            Func<double,double,double,PointF> at=(x,y,z)=>Project(new SpectrumPoint{X=x,Y=y,Z=z},a);
            double x0=State.X0,x1=State.X1,y0=State.Y0,y1=State.Y1,z0=view.ZMin,z1=view.ZMax;
            PointF p000=at(x0,y0,z0),p100=at(x1,y0,z0),p010=at(x0,y1,z0),p110=at(x1,y1,z0),
                p001=at(x0,y0,z1),p101=at(x1,y0,z1),p011=at(x0,y1,z1),p111=at(x1,y1,z1);
            using(var axis=new Pen(HVX.PdReview.ReviewTheme.SecondaryInk))using(var grid=new Pen(HVX.PdReview.ReviewTheme.Grid))using(var ink=new SolidBrush(ForeColor))
            using(var back=new SolidBrush(HVX.PdReview.ReviewTheme.SurfaceAlt))using(var side=new SolidBrush(HVX.PdReview.ReviewTheme.Surface))using(var floor=new SolidBrush(HVX.PdReview.ReviewTheme.SurfaceAlt))
            {
                g.FillPolygon(back,new[]{p001,p101,p111,p011});
                g.FillPolygon(side,new[]{p000,p001,p011,p010});
                g.FillPolygon(floor,new[]{p000,p100,p101,p001});
                int divisions=a.Width<380||a.Height<220?2:4;
                foreach(double x in AxisTicks(x0,x1,view.XKey,divisions))
                {
                    g.DrawLine(grid,at(x,y0,z0),at(x,y0,z1));g.DrawLine(grid,at(x,y0,z1),at(x,y1,z1));
                    PointF px=at(x,y0,z0);string xt=Tick(x,view.XKey);SizeF xs=g.MeasureString(xt,Font);
                    g.DrawString(xt,Font,ink,px.X-xs.Width/2,px.Y+6);
                }
                foreach(double y in AxisTicks(y0,y1,view.YKey,divisions))
                {
                    g.DrawLine(grid,at(x0,y,z1),at(x1,y,z1));g.DrawLine(grid,at(x0,y,z0),at(x0,y,z1));
                    PointF py=at(x0,y,z0);string yt=Tick(y,view.YKey);SizeF ys=g.MeasureString(yt,Font);
                    g.DrawString(yt,Font,ink,py.X-ys.Width-8,py.Y-ys.Height/2);
                }
                foreach(double z in AxisTicks(z0,z1,view.ZKey,divisions))
                {
                    g.DrawLine(grid,at(x0,y0,z),at(x1,y0,z));g.DrawLine(grid,at(x0,y0,z),at(x0,y1,z));
                    PointF pz=at(x1,y0,z);string zt=Tick(z,view.ZKey);SizeF zs=g.MeasureString(zt,Font);
                    // The shared front/right corner is already labelled by X; lift Z to avoid overlapping the two zero ticks.
                    g.DrawString(zt,Font,ink,pz.X+5,pz.Y-zs.Height-(z==z0?1:0));
                }
                g.DrawPolygon(axis,new[]{p000,p100,p101,p001});g.DrawPolygon(axis,new[]{p010,p110,p111,p011});
                g.DrawLine(axis,p000,p010);g.DrawLine(axis,p100,p110);g.DrawLine(axis,p001,p011);g.DrawLine(axis,p101,p111);
                CenterText(g,AxisLabel(view.XKey),Font,ink,new RectangleF(a.Left,a.Bottom+25,a.Width*.76f,24));
                var saved=g.Save();g.TranslateTransform(a.Left-64,(p000.Y+p010.Y)/2);g.RotateTransform(-90);
                CenterText(g,AxisLabel(view.YKey),Font,ink,new RectangleF(-a.Height/2,-12,a.Height,24));g.Restore(saved);
                CenterText(g,AxisLabel(view.ZKey),Font,ink,new RectangleF(a.Left+a.Width*.53f,a.Top-22,a.Width*.45f,22));
            }
        }
        void DrawHistogram(Graphics g,Rectangle a,Brush[] fills)
        {
            double width=view.XKey=="PHI"?360.0/phaseBins:(view.XMax-view.XMin)/positionBins;
            foreach(var p in view.Points){var top=Project(new SpectrumPoint{X=p.X-width*.46,Y=p.Y},a);var bottom=Project(new SpectrumPoint{X=p.X+width*.46,Y=0},a);g.FillRectangle(fills[Math.Max(0,Math.Min(3,p.Phase))],top.X,top.Y,Math.Max(1,bottom.X-top.X),Math.Max(0,bottom.Y-top.Y));}
        }
        void DrawHeatmap(Graphics g,Rectangle a,Brush[] colors)
        {
            double dx=360.0/phaseBins,dy=view.YMax/chargeBins-view.YMin/chargeBins;
            var saved=g.Save();g.SmoothingMode=SmoothingMode.None;
            foreach(var p in view.Points){var top=Project(new SpectrumPoint{X=p.X-dx/2,Y=p.Y+dy/2},a);var bottom=Project(new SpectrumPoint{X=p.X+dx/2,Y=p.Y-dy/2},a);g.FillRectangle(colors[DensityColorIndex(p.DensityCount,view.DensityMaximum)],top.X,top.Y,Math.Max(1,bottom.X-top.X),Math.Max(1,bottom.Y-top.Y));}
            g.Restore(saved);
        }
        static int DensityColorIndex(int count,int maximum)
        {
            if(maximum<=1)return 0;
            return (int)Math.Round(255*Math.Max(0,Math.Min(1,(count-1.0)/(maximum-1.0))));
        }
        static Color DensityColor(double fraction)
        {
            double f=Math.Max(0,Math.Min(1,fraction));
            int i=f<=.33?0:f<=.66?1:2;
            double t=(f-DensityColorPositions[i])/(i==2?.34:.33);
            var first=DensityColorStops[i];var last=DensityColorStops[i+1];
            return Color.FromArgb((int)Math.Round(first.R+(last.R-first.R)*t),(int)Math.Round(first.G+(last.G-first.G)*t),(int)Math.Round(first.B+(last.B-first.B)*t));
        }
        int DensityScaleTextHeight { get { return TextRenderer.MeasureText("Ag国",Font,Size.Empty,HeaderLineFlags).Height; } }
        int DensityScaleHeaderClearance { get { int h=DensityScaleTextHeight;return h+(h+1)/2+12; } }
        Rectangle DensityScaleTitleBounds(Rectangle a)
        {
            int x=a.Right+((mode==11||IsPhaseHorizontalPlane)&&HasPhaseReference?68:25);
            string text=UseChargeColorReference?AxisLabel("Q"):L("Count","次数");
            Size size=TextRenderer.MeasureText(text,Font,Size.Empty,HeaderLineFlags);
            return new Rectangle(x-4,a.Top-(DensityScaleTextHeight+1)/2-size.Height-6,size.Width+2,size.Height);
        }
        void DrawDensityScale(Graphics g,Rectangle a)
        {
            if(view.Points.Count==0)return;
            int x=a.Right+((mode==11||IsPhaseHorizontalPlane)&&HasPhaseReference?68:25),w=14;
            bool charge=UseChargeColorReference;double min=charge?0:1,max=charge?ChargeColorMaximum():view.DensityMaximum;
            if(max<=min)using(var fill=new SolidBrush(DensityColor(0)))g.FillRectangle(fill,x,a.Top,w,a.Height);
            else using(var gradient=new LinearGradientBrush(new Rectangle(x,a.Top,w,a.Height),DensityColor(1),DensityColor(0),LinearGradientMode.Vertical))
            {var blend=new ColorBlend(DensityColorStops.Length);for(int i=0;i<DensityColorStops.Length;i++){int reverse=DensityColorStops.Length-1-i;blend.Colors[i]=DensityColorStops[reverse];blend.Positions[i]=(float)(1-DensityColorPositions[reverse]);}gradient.InterpolationColors=blend;g.FillRectangle(gradient,x,a.Top,w,a.Height);}
            using(var ink=new SolidBrush(ForeColor))using(var pen=new Pen(Color.Gray))
            {
                g.DrawRectangle(pen,x,a.Top,w,a.Height);
                TextRenderer.DrawText(g,charge?AxisLabel("Q"):L("Count","次数"),Font,DensityScaleTitleBounds(a),ForeColor,HeaderLineFlags);
                var ticks=max<=min?new[]{min}:AxisTicks(min,max,charge?"Q":"N",4).Concat(new[]{min,max}).Distinct().OrderBy(v=>v).ToArray();
                float previousY=float.MaxValue;
                foreach(double v in ticks)
                {
                    float y=max<=min?a.Top+a.Height/2:a.Bottom-(float)((v-min)/(max-min)*a.Height);
                    // Keep both extrema; omit crowded interior labels on short controls.
                    if(v!=min&&v!=max&&previousY-y<Font.Height+3)continue;
                    if(v!=min&&v!=max&&y-a.Top<Font.Height+3)continue;
                    string tickText=charge?ChargeColorTick(v):Tick(v,"N");
                    Size tickSize=TextRenderer.MeasureText(tickText,Font,Size.Empty,HeaderLineFlags);
                    TextRenderer.DrawText(g,tickText,Font,new Rectangle(x+w+4,(int)Math.Round(y-tickSize.Height/2.0),tickSize.Width+2,tickSize.Height),ForeColor,HeaderLineFlags);previousY=y;
                }
            }
        }
        void DrawCable(Graphics g,Rectangle a)
        {
            double length=currentLength;if(!Finite(length)||length<=0)return;
            using(var endpoint=new Pen(Color.Gray,1))using(var joint=new Pen(Color.Teal,1))using(var ink=new SolidBrush(Color.Teal))
            {
                endpoint.DashStyle=joint.DashStyle=DashStyle.Dash;
                foreach(double x in new[]{0.0,length}.Concat(CurrentJointPositionsM))
                {float px=Project(new SpectrumPoint{X=x,Y=State.Y0},a).X;g.DrawLine(x==0||x==length?endpoint:joint,px,a.Top,px,a.Bottom);if(x>0&&x<length)g.DrawString((L("Joint ","接头 "))+x.ToString("0.#",CultureInfo.InvariantCulture),Font,ink,px+3,a.Top+4);}
            }
        }
        static void DrawMarker(Graphics g,PointF p,int phase,float r)
        {
            using(var pen=new Pen(PhaseColors[Math.Max(0,Math.Min(3,phase))],1))DrawMarker(g,p,phase,r,pen);
        }
        static void DrawMarker(Graphics g,PointF p,int phase,float r,Pen pen)
        {if(phase==3){g.DrawLine(pen,p.X,p.Y-r,p.X-r,p.Y+r);g.DrawLine(pen,p.X-r,p.Y+r,p.X+r,p.Y+r);g.DrawLine(pen,p.X+r,p.Y+r,p.X,p.Y-r);}else if(phase==2)g.DrawRectangle(pen,p.X-r,p.Y-r,2*r,2*r);else g.DrawEllipse(pen,p.X-r,p.Y-r,2*r,2*r);}
        static void DrawFilledMarker(Graphics g,PointF p,int phase,float r,Brush brush,PointF[] triangle)
        {if(phase==3){triangle[0]=new PointF(p.X,p.Y-r);triangle[1]=new PointF(p.X-r,p.Y+r);triangle[2]=new PointF(p.X+r,p.Y+r);g.FillPolygon(brush,triangle);}else if(phase==2)g.FillRectangle(brush,p.X-r,p.Y-r,2*r,2*r);else g.FillEllipse(brush,p.X-r,p.Y-r,2*r,2*r);}
        static void DrawRegion(Graphics g,RectangleF box,bool circle)
        {
            if(box.Width<=0||box.Height<=0)return;using(var b=new SolidBrush(Color.FromArgb(24,HVX.PdReview.ReviewTheme.Active)))using(var p=new Pen(HVX.PdReview.ReviewTheme.Active,1)){p.DashStyle=DashStyle.Dash;if(circle){g.FillEllipse(b,box);g.DrawEllipse(p,box);}else{g.FillRectangle(b,box);g.DrawRectangle(p,box.X,box.Y,box.Width,box.Height);}}
        }
        static void CenterText(Graphics g,string text,Font font,Brush brush,RectangleF box)
        {using(var f=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter})g.DrawString(text,font,brush,box,f);}
    }
}
