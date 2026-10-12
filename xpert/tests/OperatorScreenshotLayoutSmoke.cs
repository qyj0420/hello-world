using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

static class OperatorScreenshotLayoutSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string name){checks++;if(!ok)throw new Exception(name);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Set(object value,string name,object data){value.GetType().GetField(name,All).SetValue(value,data);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethod(name,All).Invoke(value,args);}
    static void Paint(Control c){using(var b=new Bitmap(c.Width,c.Height))c.DrawToBitmap(b,new Rectangle(Point.Empty,c.Size));}
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Directory.CreateDirectory(args[1]);
        var creation=Assembly.LoadFrom(Path.Combine(args[0],"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
        using(var module=(Control)Activator.CreateInstance(creation.GetType("HVX.Controls.ProjectCreationControl",true)))
        using(var host=new Form{ShowInTaskbar=false,Location=new Point(-25000,-25000),StartPosition=FormStartPosition.Manual,ClientSize=new Size(1400,900)}){
            host.Controls.Add(module);module.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
            var grid=(DataGridView)Field(module,"cableGrid");
            foreach(float size in new[]{10f,15f,20f}){
                grid.Font=new Font("Microsoft YaHei UI",size);Application.DoEvents();
                Check(grid.Columns["Phase"].AutoSizeMode==DataGridViewAutoSizeColumnMode.None&&grid.Columns["Node"].AutoSizeMode==DataGridViewAutoSizeColumnMode.None,"phase and node no longer consume equal filler width");
                foreach(string name in new[]{"MaterialBefore","Material"}){
                    var col=grid.Columns[name];var font=grid.ColumnHeadersDefaultCellStyle.Font??grid.Font;
                    int header=TextRenderer.MeasureText(col.HeaderText,font,Size.Empty,TextFormatFlags.SingleLine|TextFormatFlags.NoPadding).Width;
                    Check(col.MinimumWidth>=header+10&&col.Width>=col.MinimumWidth,"insulation header fits at font "+size+" / "+name+" header="+header+" min="+col.MinimumWidth+" width="+col.Width+" font="+font.Size);
                    Check(col.FillWeight>grid.Columns["Position"].FillWeight,"insulation has priority for extra width");
                }
                Check(grid.Columns["Phase"].Width<grid.Columns["Material"].MinimumWidth&&grid.Columns["Node"].Width<grid.Columns["MaterialBefore"].MinimumWidth,"short columns remain narrower than insulation columns");
                Check(grid.AutoSizeRowsMode==DataGridViewAutoSizeRowsMode.None&&grid.Columns.Cast<DataGridViewColumn>().All(c=>c.DefaultCellStyle.WrapMode==DataGridViewTriState.False),"large-font cell text cannot inflate rows through automatic wrapping");
                Check(grid.RowTemplate.Height>=grid.Font.Height+8&&grid.Rows.Cast<DataGridViewRow>().All(r=>r.Height>=grid.Font.Height+8),"single-line rows still fully contain the selected font");
                Check(grid.ShowCellToolTips,"long cell values remain accessible as tooltips");
            }
        }
        var browser=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
        var seriesType=browser.GetType("HVX.Controls.BrowserSeries",true);object series=Activator.CreateInstance(seriesType);
        double[] x=Enumerable.Range(0,1000).Select(i=>i*.01).ToArray(),y=x.Select(t=>Math.Sin(t)).ToArray();
        Set(series,"Name","ALL_10nC");Set(series,"X",x);Set(series,"Y",y);Set(series,"Color",Color.Teal);
        using(var plot=(Control)Activator.CreateInstance(browser.GetType("HVX.Controls.BrowserPlot",true),true)){
            plot.Size=new Size(1200,600);plot.CreateControl();
            Call(plot,"SetSingleCalibrationData","时间 / μs","原始校准幅值 / V",series);Paint(plot);
            object layout=Field(plot,"measuredLayout");
            Check(((Rectangle)Field(layout,"Heading")).IsEmpty&&((Rectangle)Field(layout,"Legend")).IsEmpty&&((Rectangle)Field(layout,"LeftTitle")).IsEmpty,"single calibration hides all three crossed-out labels");
            Check(((Rectangle[])Field(layout,"LegendCells")).Length==0,"no duplicate file-name legend remains");
            Check((int)Field(layout,"YDivisions")>0&&(int)Field(layout,"XDivisions")>0,"numeric axes remain intact");
            Check(plot.AccessibleName=="ALL_10nC"&&plot.AccessibleDescription.Contains("V"),"source name and amplitude unit retained accessibly");
            Check(Object.ReferenceEquals(Field(series,"Y"),y)&&y[10]==Math.Sin(.1),"presentation does not rewrite waveform values");
            using(var bmp=new Bitmap(plot.Width,plot.Height)){plot.DrawToBitmap(bmp,plot.ClientRectangle);bmp.Save(Path.Combine(args[1],"single-calibration-clean.png"));}
            var point=new Point(100,150);Call(plot,"OnMouseMove",new MouseEventArgs(MouseButtons.None,0,point.X,point.Y,0));
            var values=Array.CreateInstance(seriesType,1);values.SetValue(series,0);
            Call(plot,"SetData","Comparison","us","V","",values);Paint(plot);layout=Field(plot,"measuredLayout");
            Check(!((Rectangle)Field(layout,"Heading")).IsEmpty&&((Rectangle[])Field(layout,"LegendCells")).Length>0,"other plots restore their own headings and legends");
        }
        Console.WriteLine("OPERATOR_SCREENSHOT_LAYOUT_PASS="+checks+"; HARDWARE=False; USER_PROJECTS_WRITTEN=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
