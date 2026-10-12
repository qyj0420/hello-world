using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class TerminalVisualSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Field(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object value){o.GetType().GetProperty(n,All).SetValue(o,value,null);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Pump(){for(int i=0;i<3;i++)Application.DoEvents();}
    static string Digest(Bitmap b){using(var hash=System.Security.Cryptography.SHA256.Create())using(var stream=new MemoryStream()){b.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return Convert.ToBase64String(hash.ComputeHash(stream.ToArray()));}}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();string qa=Path.Combine(args[1],"terminals-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var creation=Assembly.LoadFrom(Path.Combine(args[0],"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            string saved;
            using(var host=new Form{AutoScaleMode=AutoScaleMode.None,ClientSize=new Size(1280,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
            using(var c=(Control)Activator.CreateInstance(creation.GetType("HVX.Controls.ProjectCreationControl",true)))
            {
                Set(c,"EmbeddedMode",true);Set(c,"ShowDialogMessages",false);Set(c,"SettingsPath",Path.Combine(qa,"references.ini"));Set(c,"OnlineWeatherEnabled",false);
                Set(c,"StartName","QA start");Set(c,"EndName","QA end");Set(c,"CableMode","一体三芯电缆");Set(c,"PhaseVoltage","8.7kV");Call(c,"SetPhaseCableLengths",414.8d,414.8d,414.8d);
                Set(c,"StartTerminalType","高压插拔头");Set(c,"EndTerminalType","户内冷缩");
                host.Controls.Add(c);c.Dock=DockStyle.Fill;host.Show();Pump();
                var start=(ComboBox)Field(c,"startTypeCombo");var end=(ComboBox)Field(c,"endTypeCombo");var canvas=(Control)Field(c,"topology");
                Check(((ComboBox)Field(c,"selectedPhaseCombo")).Text=="ALL","integrated draft joint scope is ALL, not a single phase");
                Check(start.ContextMenuStrip!=null&&start.ContextMenuStrip==end.ContextMenuStrip,"original catalog add/select context menu retained");
                Check((string)Field(canvas,"StartType")=="高压插拔头"&&(string)Field(canvas,"EndType")=="户内冷缩","independent endpoints reach actual model");
                string first;using(var b=new Bitmap(canvas.Width,canvas.Height)){canvas.DrawToBitmap(b,canvas.ClientRectangle);first=Digest(b);b.Save(Path.Combine(qa,"model-plug-cold.png"));}
                Set(c,"StartTerminalType","户内冷缩");Set(c,"EndTerminalType","高压插拔头");Pump();
                Check((string)Field(canvas,"StartType")=="户内冷缩"&&(string)Field(canvas,"EndType")=="高压插拔头","swapping endpoints does not change opposite selection");
                using(var b=new Bitmap(canvas.Width,canvas.Height)){canvas.DrawToBitmap(b,canvas.ClientRectangle);Check(first!=Digest(b),"actual drawing changes for independent endpoint swap");b.Save(Path.Combine(qa,"model-cold-plug.png"));}
                foreach(int language in new[]{0,1,18})foreach(Size size in new[]{new Size(720,490),new Size(1065,500),new Size(1850,990)})
                {
                    Set(c,"LanguageIndex",language);host.ClientSize=size;Pump();
                    Check(((ComboBox)Field(c,"selectedPhaseCombo")).Text=="ALL","common scope remains ALL across languages and sizes");
                    var viewport=c.Controls.Find("CableTopologyViewport",true).Single();
                    Check(start.Visible&&end.Visible,"both selectors visible above model");
                    Check(start.RectangleToScreen(start.ClientRectangle).Bottom<=viewport.RectangleToScreen(viewport.ClientRectangle).Top&&end.RectangleToScreen(end.ClientRectangle).Bottom<=viewport.RectangleToScreen(viewport.ClientRectangle).Top,"both selectors are above topology viewport");
                    Check(start.Width>80&&end.Width>80&&start.Parent.ClientRectangle.Contains(start.Bounds)&&end.Parent.ClientRectangle.Contains(end.Bounds),"selectors not clipped");
                    Check((string)Get(c,"StartTerminalType")=="户内冷缩"&&(string)Get(c,"EndTerminalType")=="高压插拔头","canonical choice survives language and size changes");
                }
                Check((bool)Call(c,"SaveProjectToRoot",Path.Combine(qa,"projects")),"project with selected endpoints saves: "+Get(c,"LastError"));saved=(string)Get(c,"CurrentProjectPath");
                Check((bool)Call(c,"LoadProject",saved),"saved project reloads");Check((string)Get(c,"StartTerminalType")=="户内冷缩"&&(string)Get(c,"EndTerminalType")=="高压插拔头","both endpoint types round trip without renaming");
                var gbk=System.Text.Encoding.GetEncoding(936,System.Text.EncoderFallback.ExceptionFallback,System.Text.DecoderFallback.ExceptionFallback);
                Check(File.ReadAllText(Path.Combine(saved,"ITEM.in"),gbk).Contains("高压插拔头")&&File.ReadAllText(Path.Combine(saved,"CABLE.in"),gbk).Contains("高压插拔头"),"ITEM and CABLE carry explicit selected type in the writer's verified GBK encoding");
            }
            var browser=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var item=(Control)Activator.CreateInstance(browser.GetType("LabVIEWItemInformation.ItemInformationControl",true)))
            {
                Check((bool)Call(item,"LoadItem",saved,1),"browser reads saved metadata");var sketch=Field(item,"sketch");
                Check((string)Field(sketch,"StartType")=="户内冷缩"&&(string)Field(sketch,"EndType")=="高压插拔头","browser uses both actual saved endpoint types");
            }
            var report=Assembly.LoadFrom(Path.Combine(args[0],"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            var data=report.GetType("HVX.ReportGenerator.ProjectReportDataReader",true).GetMethod("Load").Invoke(null,new object[]{saved});
            Check((string)Get(data,"StartAccessory")=="户内冷缩"&&(string)Get(data,"EndAccessory")=="高压插拔头","report uses both actual saved endpoint types");
            string output=Path.Combine(qa,"report-model.png");var renderer=report.GetType("HVX.ReportGenerator.CableTopologyRenderer",true);
            Check((string)renderer.GetMethod("Render",All).Invoke(null,new object[]{data,1,output})==output&&File.Exists(output),"real report cable renderer exports selected types");
            using(var b=new Bitmap(600,140))using(var g=Graphics.FromImage(b))
            {
                g.Clear(Color.White);var style=creation.GetType("HVX.ProjectCables.CableVisualStyle",true);
                style.GetMethod("DrawBody",All).Invoke(null,new object[]{g,100f,500f,70f,true,Color.Gray,1f});
                style.GetMethod("DrawTermination",All).Invoke(null,new object[]{g,100f,70f,-1,"高压插拔头",1f});
                style.GetMethod("DrawTermination",All).Invoke(null,new object[]{g,500f,70f,1,"户内冷缩",1f});
                for(int x=0;x<50;x++)for(int y=0;y<140;y++)Check(b.GetPixel(x,y).ToArgb()==Color.White.ToArgb(),"compact start extent <= 50 px");
                for(int x=550;x<600;x++)for(int y=0;y<140;y++)Check(b.GetPixel(x,y).ToArgb()==Color.White.ToArgb(),"compact end extent <= 50 px");
                b.Save(Path.Combine(qa,"shared-style.png"));
            }
            Console.WriteLine("TERMINAL_VISUAL_PASS="+checks+";HARDWARE=False;EVIDENCE="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
