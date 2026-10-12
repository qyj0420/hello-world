using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HVX.Controls;

internal static class BrowserPlotLayoutSmoke
{
    static int passed;
    static void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);passed++;}
    static object Field(object target,string name){return target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(target);}
    static void Mouse(BrowserPlot plot,string method,MouseEventArgs args){typeof(BrowserPlot).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(plot,new object[]{args});}
    [STAThread] static int Main(string[] args)
    {
        string root=Path.Combine(args.Length>0?args[0]:Path.GetTempPath(),"browser-plot-layout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles();
            foreach(int count in new[]{4,12})foreach(float font in new[]{10f,15f,20f})foreach(Size size in new[]{new Size(1280,720),new Size(560,520)})
            using(var plot=new BrowserPlot())using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap))
            {
                plot.Size=size;plot.Font=new Font("Arial",font);plot.CreateControl();
                var data=Enumerable.Range(0,count).Select(j=>new BrowserSeries {Name=(j+1)+". ALL_"+(100+j*50)+"nC 校准波形",Color=Color.FromArgb(40+j*15,90,210-j*10),Dashed=j%2==1,
                    X=Enumerable.Range(0,1200).Select(i=>i*.14627).ToArray(),Y=Enumerable.Range(0,1200).Select(i=>Math.Exp(-Math.Pow((i-100-j)/3.0,2))-.4*Math.Exp(-Math.Pow((i-110-j)/3.0,2))).ToArray()}).ToArray();
                plot.SetData("校准对比 · 绝对峰值归一化","时间 / μs","归一化幅值","",data);
                var layout=plot.MeasureLayout(graphics);
                Check(layout.CanDrawPlot,"plot visible");
                Check(layout.Heading.Bottom<=layout.Legend.Top,"heading and legend separate");
                Check(layout.Legend.Bottom<=layout.LeftTitle.Top,"legend and Y title separate");
                Check(layout.LeftTitle.Bottom+layout.Gap<=layout.Plot.Top-(layout.LineHeight+1)/2,"Y title and top tick separate");
                Check(layout.Plot.Bottom<layout.XTicks.Top&&layout.XTicks.Bottom<=layout.XTitle.Top,"X ticks and title separate");
                Check(layout.XTitle.Bottom<=plot.Height&&layout.Plot.Right<plot.Width,"axis labels inside control");
                Check(layout.Plot.Height/layout.YDivisions>=layout.LineHeight+layout.Gap,"vertical ticks have measured separation");
                int previousRight=-1;
                for(int i=0;i<=layout.XDivisions;i++)
                {
                    string caption=(175.37773*i/layout.XDivisions).ToString("0.####",System.Globalization.CultureInfo.InvariantCulture);
                    int width=TextRenderer.MeasureText(graphics,caption,plot.Font,new Size(int.MaxValue,int.MaxValue),TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine).Width;
                    int center=layout.Plot.Left+layout.Plot.Width*i/layout.XDivisions;
                    Check(center-width/2>previousRight,"horizontal ticks do not overlap");previousRight=center+(width+1)/2;
                }
                Check(layout.LegendCells.All(cell=>cell.Height>=plot.Font.Height&&cell.Left>=0&&cell.Right<=plot.Width),"legend font height and width");
                if(layout.Pages>1)
                {
                    Check(layout.PreviousPage.Bottom<=layout.LeftTitle.Top&&layout.PreviousPage.Right<=layout.NextPage.Left,"page buttons reserve row");
                    Mouse(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,layout.NextPage.Left+1,layout.NextPage.Top+1,0));
                    Check((int)Field(plot,"legendPage")==1,"measured next-page hit target");
                    var cell=layout.LegendCells[0];Mouse(plot,"OnMouseMove",new MouseEventArgs(MouseButtons.None,0,cell.Left+1,cell.Top+1,0));
                    Check((string)Field(plot,"lastLegendTip")==data[layout.PageSize].Name,"tooltip uses measured page cell");
                    Mouse(plot,"OnMouseWheel",new MouseEventArgs(MouseButtons.None,0,cell.Left+1,cell.Top+1,120));
                    Check((int)Field(plot,"legendPage")==0,"measured legend wheel hit target");
                }
                double oldSpan=(double)Field(plot,"xmax")-(double)Field(plot,"xmin");
                Mouse(plot,"OnMouseWheel",new MouseEventArgs(MouseButtons.None,0,layout.Plot.Left+layout.Plot.Width/2,layout.Plot.Top+layout.Plot.Height/2,120));
                Check((double)Field(plot,"xmax")-(double)Field(plot,"xmin")<oldSpan,"waveform wheel still zooms");
                Mouse(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,layout.Plot.Left+1,layout.Plot.Top+1,0));
                Check(Math.Abs((double)Field(plot,"xmax")-(double)Field(plot,"xmin")-oldSpan)<1e-9,"right click restores full data");
                plot.DrawToBitmap(bitmap,new Rectangle(Point.Empty,size));
                bitmap.Save(Path.Combine(root,"plot-"+count+"-font"+font+"-"+size.Width+"x"+size.Height+".png"));
            }
            using(var plot=new BrowserPlot())using(var bitmap=new Bitmap(1000,650))using(var graphics=Graphics.FromImage(bitmap))
            {
                plot.Size=bitmap.Size;plot.Font=new Font("Arial",15);plot.CreateControl();
                var trace=new BrowserSeries {Name="双轴 / large-offset precision",Color=Color.DarkGreen,X=new[]{1e9,1e9+.1,1e9+.2},Y=new[]{1e9,1e9+.01,1e9+.02}};
                plot.SetData("双轴测试","时间 / μs","幅值 / V","电压 / kV",trace);
                var layout=plot.MeasureLayout(graphics);Check(layout.LeftTitle.Right<=layout.RightTitle.Left,"two axis titles separated");
                plot.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                var cache=(IDictionary)Field(plot,"cache");var paths=(IList)cache[trace];Check(paths.Count==1&&paths[0] is int[],"cache retains integer sample indices");
                Check(((int[])paths[0]).SequenceEqual(new[]{0,1,2}),"small differences retained until double projection");
                bitmap.Save(Path.Combine(root,"plot-large-offset.png"));
            }
            Console.WriteLine("PASS "+passed+" assertions; "+root);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
