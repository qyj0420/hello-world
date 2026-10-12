using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HVX.Controls;

internal static class BrowserHoverPerformanceSmoke
{
    static int checks;
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string name){checks++;if(!value)throw new InvalidOperationException(name);}
    static object Field(object value,string name){return value.GetType().GetField(name,Flags).GetValue(value);}
    static void Mouse(BrowserPlot plot,string name,MouseButtons button,int x,int y,int clicks=0)
    {typeof(BrowserPlot).GetMethod(name,Flags).Invoke(plot,new object[]{new MouseEventArgs(button,clicks,x,y,0)});}
    static void Leave(BrowserPlot plot){typeof(BrowserPlot).GetMethod("OnMouseLeave",Flags).Invoke(plot,new object[]{EventArgs.Empty});}
    static Bitmap Paint(BrowserPlot plot)
    {var result=new Bitmap(plot.Width,plot.Height);plot.DrawToBitmap(result,new Rectangle(Point.Empty,result.Size));return result;}
    static ulong Bits(double[] data)
    {unchecked{ulong hash=1469598103934665603;foreach(double n in data){hash^=(ulong)BitConverter.DoubleToInt64Bits(n);hash*=1099511628211;}return hash;}}
    static ulong Pixels(Bitmap data)
    {unchecked{ulong hash=1469598103934665603;for(int y=0;y<data.Height;y++)for(int x=0;x<data.Width;x++){hash^=(uint)data.GetPixel(x,y).ToArgb();hash*=1099511628211;}return hash;}}
    static bool Released(Bitmap image)
    {try{int width=image.Width;return false;}catch(ArgumentException){return true;}}
    static Rectangle Plot(BrowserPlot plot)
    {using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap))return plot.MeasureLayout(g).Plot;}
    static void Stable(BrowserPlot plot,int layouts,int sources,int renders,string message)
    {Check(plot.LayoutBuildCount==layouts,message+" reuses measured layout");Check(plot.SourceCacheBuildCount==sources,message+" never scans/reduces source samples again");Check(plot.StaticRenderCount==renders,message+" never projects or renders static curves again");}

    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            string qa=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:"build/BrowserHover-qa"),"browser-hover-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            const int count=1000003;
            var x=new double[count];var a=new double[count];var b=new double[count];
            for(int i=0;i<count;i++){x[i]=i*.0002;a[i]=.04*Math.Sin(i*.034);b[i]=.06*Math.Cos(i*.029);}
            a[123450]=1000;a[123451]=-1000;a[300000]=Double.NaN;b[765432]=500;
            ulong originalX=Bits(x),originalA=Bits(a),originalB=Bits(b);
            var first=new BrowserSeries{Name="Synthetic average · original full precision",X=x,Y=a,Color=Color.DarkGreen};
            var second=new BrowserSeries{Name="Synthetic comparison · not a real project",X=x,Y=b,Color=Color.DarkBlue,Secondary=true};
            using(var form=new Form{ClientSize=new Size(1280,720),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ShowInTaskbar=false})
            using(var plot=new BrowserPlot{Dock=DockStyle.Fill})
            {
                form.Controls.Add(plot);plot.SetData("Hover performance · synthetic data","Time / μs","Amplitude / V","Second trace / V",first,second);
                plot.SetCableOverlay(10,180,170,new[]{40d,90d,145d},new[]{"joint 1","joint 2","joint 3"});form.Show();Application.DoEvents();
                using(var initial=Paint(plot))
                {
                    initial.Save(Path.Combine(qa,"initial-million-point-traces.png"));ulong baseline=Pixels(initial);
                    var layer=(Bitmap)Field(plot,"staticLayer");int layouts=plot.LayoutBuildCount,sources=plot.SourceCacheBuildCount,renders=plot.StaticRenderCount;
                    var cache=(IDictionary)Field(plot,"cache");var paths=(IList)cache[first];
                    Check(paths.Count==2,"NaN discontinuity stays as two distinct paths");
                    var indices=paths.Cast<int[]>().SelectMany(v=>v).ToArray();
                    Check(indices.Contains(123450)&&indices.Contains(123451),"both signs of same-pixel narrow pulse retained");
                    Check(ReferenceEquals(first.X,x)&&ReferenceEquals(first.Y,a)&&a.Length==count,"drawing cache does not replace the full-resolution average");
                    Rectangle p=Plot(plot);int y=p.Top+p.Height/2;
                    var dirty=new List<Rectangle>();plot.Invalidated+=(s,e)=>dirty.Add(e.InvalidRect);
                    for(int i=0;i<120;i++)
                    {
                        Mouse(plot,"OnMouseMove",MouseButtons.None,p.Left+20+i*3,y);
                        using(var frame=Paint(plot)){}
                    }
                    Stable(plot,layouts,sources,renders,"120 hover frames over two million samples");
                    Check(ReferenceEquals(layer,Field(plot,"staticLayer")),"hover keeps the exact bitmap object");
                    Check(dirty.Count>0&&dirty.All(r=>r.Width<=5&&r.Height<=p.Height+1),"hover invalidates cursor strips, not whole plot");
                    int priorInvalidations=dirty.Count;var repeat=new Point(p.Left+20+119*3,y);
                    for(int i=0;i<100;i++)Mouse(plot,"OnMouseMove",MouseButtons.None,repeat.X,repeat.Y);
                    Mouse(plot,"OnMouseMove",MouseButtons.None,repeat.X,repeat.Y+5);
                    Check(dirty.Count==priorInvalidations,"same pixel or same time-column move schedules no repaint");
                    Stable(plot,layouts,sources,renders,"unchanged hover");
                    Mouse(plot,"OnMouseMove",MouseButtons.None,p.Left+(int)Math.Round((90d-x[0])/(x[count-1]-x[0])*p.Width),y);
                    string tip=(string)Field(plot,"lastLegendTip");
                    Check(tip.Contains("joint 2")&&tip.Contains("μs")&&tip.Contains(" m"),"cached hover preserves time/distance/candidate identity");
                    double hover=(double)Field(plot,"hoverX");Check(Math.Abs(plot.CableDistanceAt(hover)-(hover-10)*85)<1e-9,"distance uses true cable origin and velocity");
                    Leave(plot);using(var clear=Paint(plot)){Check(Pixels(clear)==baseline,"leaving removes cursor with no waveform or marker changes");}
                    Check((string)Field(plot,"lastLegendTip")==""&&Double.IsNaN((double)Field(plot,"hoverX")),"leave clears tooltip and coordinate");
                    priorInvalidations=dirty.Count;Leave(plot);Check(dirty.Count==priorInvalidations,"repeated leave does not repaint an empty cursor");
                    Stable(plot,layouts,sources,renders,"mouse leave");

                    double click=Double.NaN;plot.CableDoubleClick+=v=>click=v;
                    Mouse(plot,"OnMouseDoubleClick",MouseButtons.Left,p.Left+p.Width/2,y,2);
                    Check(Math.Abs(click-(x[0]+(x[count-1]-x[0])*.5))<1e-9,"double-click physical position still exact");

                    Mouse(plot,"OnMouseDown",MouseButtons.Left,p.Left+80,p.Top+30,1);
                    for(int i=0;i<40;i++){Mouse(plot,"OnMouseMove",MouseButtons.Left,p.Left+100+i*4,p.Top+50+i*2);using(var frame=Paint(plot)){}}
                    Stable(plot,layouts,sources,renders,"40 drag selection frames");
                    using(var selection=Paint(plot)){selection.Save(Path.Combine(qa,"drag-selection.png"));}
                    Mouse(plot,"OnMouseUp",MouseButtons.Left,p.Left+256,p.Top+128);
                    using(var zoom=Paint(plot)){zoom.Save(Path.Combine(qa,"selection-zoom.png"));}
                    Check(plot.StaticRenderCount==renders+1&&plot.SourceCacheBuildCount==sources+1,"drag release rebuilds once for the actual new viewport");
                    Check((double)Field(plot,"xmax")-(double)Field(plot,"xmin")<x[count-1]-x[0],"selection zoom actually changes displayed interval");
                    Check(Released(layer),"old bitmap released on viewport change");
                    layouts=plot.LayoutBuildCount;sources=plot.SourceCacheBuildCount;renders=plot.StaticRenderCount;p=Plot(plot);
                    Mouse(plot,"OnMouseMove",MouseButtons.None,p.Left+12,p.Top+12);using(var frame=Paint(plot)){}
                    Stable(plot,layouts,sources,renders,"hover after zoom");
                    plot.ApplyWheelZoom(new MouseEventArgs(MouseButtons.None,0,p.Left+p.Width/2,p.Top+p.Height/2,120),true);
                    using(var frame=Paint(plot)){}
                    Check(plot.StaticRenderCount==renders+1,"vertical zoom refreshes projected curve once");
                    Check(plot.SourceCacheBuildCount<=sources+1,"vertical zoom reuses indices unless axis label width changes plot width");

                    var previous=(Bitmap)Field(plot,"staticLayer");plot.SetCableOverlay(20,170,160,new[]{75d},new[]{"moved joint"});
                    Check(Released(previous),"overlay update releases old marker layer immediately");using(var frame=Paint(plot)){}
                    Check(plot.CableDistanceAt(75)==4400,"new cable geometry replaces old hover transform");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.ForeColor=Color.DarkSlateBlue;using(var frame=Paint(plot)){}
                    Check(Released(previous),"foreground style invalidates static layer");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.BackColor=Color.FromArgb(250,251,252);using(var frame=Paint(plot)){}
                    Check(Released(previous),"background style invalidates static layer");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.Font=new Font("Arial",15);using(var frame=Paint(plot)){}
                    Check(Released(previous),"font change invalidates measured and rendered layout");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.Dock=DockStyle.None;plot.Size=new Size(1000,600);using(var frame=Paint(plot)){}
                    Check(Released(previous)&&((Bitmap)Field(plot,"staticLayer")).Size==plot.Size,"resize releases bitmap and rebuilds to actual pixels");
                    using(var dpiImage=new Bitmap(plot.Width,plot.Height))
                    {
                        dpiImage.SetResolution(144,144);using(var g=Graphics.FromImage(dpiImage))typeof(BrowserPlot).GetMethod("OnPaint",Flags).Invoke(plot,new object[]{new PaintEventArgs(g,plot.ClientRectangle)});
                        Check(Math.Abs(((Bitmap)Field(plot,"staticLayer")).HorizontalResolution-144)<.01,"DPI-specific static bitmap matches render target");
                    }
                    typeof(BrowserPlot).GetProperty("Plot",Flags).GetValue(plot,null);
                    Check((float)Field(plot,"layoutDpiX")==plot.DeviceDpi,"mouse hit testing restores actual screen DPI immediately after offscreen export");
                    using(var frame=Paint(plot)){}
                    Check(Math.Abs(((Bitmap)Field(plot,"staticLayer")).HorizontalResolution-96)<.01,"ordinary screen repaint does not reuse exported DPI layer");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.SetData("Replacement","Time","Value","",new BrowserSeries{Name="New",X=new[]{0d,1d,2d},Y=new[]{0d,1d,0d},Color=Color.Purple});
                    Check(Released(previous)&&((IDictionary)Field(plot,"cache")).Count==0,"data replacement releases old sample-key cache before next paint");using(var frame=Paint(plot)){}
                    Check(((IDictionary)Field(plot,"cache")).Count==1&&!((IDictionary)Field(plot,"cache")).Contains(first),"old source objects no longer retained");
                    previous=(Bitmap)Field(plot,"staticLayer");plot.Dispose();
                    Check(Released(previous)&&Field(plot,"staticLayer")==null&&((IDictionary)Field(plot,"cache")).Count==0,"dispose releases bitmap and source caches");
                }
            }
            Check(Bits(x)==originalX&&Bits(a)==originalA&&Bits(b)==originalB,"all original full-resolution double values remain bit-for-bit unchanged");
            // No overlay: ordinary legend tracking must not repaint a static graph.
            using(var plot=new BrowserPlot{Size=new Size(560,520)})
            {
                plot.CreateControl();var data=Enumerable.Range(0,30).Select(i=>new BrowserSeries{Name="record "+i,X=new[]{0d,1d},Y=new[]{0d,(double)i},Color=Color.DarkGreen}).ToArray();plot.SetData("Legend","Time","Value","",data);
                using(var frame=Paint(plot)){}
                int layout=plot.LayoutBuildCount,source=plot.SourceCacheBuildCount,render=plot.StaticRenderCount;
                BrowserPlot.MeasuredLayout measured;using(var bitmap=new Bitmap(1,1))using(var g=Graphics.FromImage(bitmap))measured=plot.MeasureLayout(g);
                var cell=measured.LegendCells[0];int invalidations=0;plot.Invalidated+=(s,e)=>invalidations++;
                Mouse(plot,"OnMouseMove",MouseButtons.None,cell.Left+2,cell.Top+2);
                Check((string)Field(plot,"lastLegendTip")=="record 0"&&invalidations==0,"legend-only tooltip change does not schedule painting");
                Stable(plot,layout,source,render,"legend tooltip");
                Mouse(plot,"OnMouseDown",MouseButtons.Left,measured.NextPage.Left+2,measured.NextPage.Top+2,1);using(var frame=Paint(plot)){}
                Check(plot.StaticRenderCount==render+1&&plot.SourceCacheBuildCount==source,"legend page only redraws static image without source reduction");
                Mouse(plot,"OnMouseMove",MouseButtons.None,cell.Left+2,cell.Top+2);
                Check((string)Field(plot,"lastLegendTip")=="record "+measured.PageSize,"same pixel uses new legend page after cache invalidation");
            }
            Console.WriteLine("BROWSER_HOVER_PERFORMANCE_PASS="+checks+";SAMPLES_PER_TRACE="+count+";HARDWARE=False;REAL_PROJECT_WRITES=False;QA="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
