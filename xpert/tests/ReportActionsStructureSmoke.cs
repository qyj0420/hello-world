using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Globalization;
using System.Windows.Forms;

internal static class ReportActionsStructureSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,All).Invoke(o,a);}
    static void Check(bool ok,string n){checks++;if(!ok)throw new Exception(n);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();string qa=Path.Combine(Path.GetFullPath(args[1]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            string project=Path.Combine(qa,"Fixture"),output=Path.Combine(qa,"Report output");Directory.CreateDirectory(project);Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"[ITEM]\r\n电缆长度=1000\r\n始端地点=验证始端名称\r\n终端地点=验证终端名称\r\n");
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA相\t\t\r\n1\t始端\t终端\t0\r\n2\t本体\t不得显示本体材料\t\r\n1\t人工名称\t中间接头\t120\r\n2\t本体\t不得显示本体材料\t\r\n1\t接头 2\t中间接头\t800\r\n2\t本体\t不得显示本体材料\t\r\n1\t终端\t终端\t1000\r\n");
            var a=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            Type t=a.GetType("HVX.ReportGenerator.ReportGeneratorControl");
            string archive=Path.Combine(qa,"legacy-analysis");Directory.CreateDirectory(archive);
            string manifest=Path.Combine(archive,"analysis.json");File.WriteAllText(manifest,"{\"Name\":\"2026年05月05日16时31分 - 004\"}");
            byte[] manifestBefore=File.ReadAllBytes(manifest);
            Type picker=a.GetType("HVX.ReportGenerator.SavedAnalysisPicker");
            foreach(string culture in new[]{"zh-CN","en-US","th-TH","hr-HR"}){
                var prior=System.Threading.Thread.CurrentThread.CurrentCulture;
                try{System.Threading.Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(culture);
                    Check((string)picker.GetMethod("DisplayName",All).Invoke(null,new object[]{archive})=="2026-05-05 16:31:00 - 004","legacy auto analysis display invariant "+culture);
                }finally{System.Threading.Thread.CurrentThread.CurrentCulture=prior;}
            }
            Check(Convert.ToBase64String(manifestBefore)==Convert.ToBase64String(File.ReadAllBytes(manifest)),"legacy manifest remains byte exact");
            using(var c=(Control)Activator.CreateInstance(t))using(var f=new Form {ClientSize=new Size(1200,780)}) {
                t.GetProperty("PromptAnalysisOnLoad").SetValue(c,false,null);t.GetProperty("OutputDirectory").SetValue(c,output,null);c.Dock=DockStyle.Fill;f.Controls.Add(c);f.Show();Application.DoEvents();
                Check((bool)Call(c,"LoadProject",project),"project loaded");
                var grid=(DataGridView)Get(c,"previewGrid");var panel=(Control)Get(c,"projectInformationPanel");var diagram=(Control)Get(c,"projectStructure");
                Check(!grid.ColumnHeadersVisible&&grid.Parent==panel&&diagram.Visible,"project table without headers plus structure");
                Check(Get(diagram,"structure")!=null,"actual cable topology includes custom named joints without using name keywords: "+Get(diagram,"structureError"));
                Check((string)Call(c,"FindOpenableReportPath")=="","no unrelated output chosen");
                string path=Path.Combine(output,"Fixture_OWTS_Report_zh-CN.pdf");File.WriteAllText(path,"synthetic shell invocation fixture - not a PDF");
                Check((string)Call(c,"FindOpenableReportPath")==path,"configured report directory searched");
                ProcessStartInfo request=null;t.GetField("ReportShellLauncher",All).SetValue(c,new Action<ProcessStartInfo>(p=>request=p));
                t.GetField("DefaultPrinterProvider",All).SetValue(c,new Func<string>(()=>"Synthetic Default Printer"));
                Check((bool)Call(c,"OpenReportDirectory")&&request.FileName==output&&request.UseShellExecute,"folder action targets report output");
                Check((bool)Call(c,"PrintGeneratedReport")&&request.FileName==path&&request.Verb=="printto"&&request.Arguments=="\"Synthetic Default Printer\"","print request explicitly targets system default printer");
                bool refused=false;try{t.GetMethod("CreateReportPrintRequest",All).Invoke(null,new object[]{path,""});}catch(TargetInvocationException){refused=true;}Check(refused,"missing printer refused without launch");
                Call(c,"ShowText","fixture");Check(!panel.Visible&&grid.Parent!=panel,"other previews release project panel");Call(c,"ShowProjectInformation");
                foreach(int width in new[]{1065,1600}) {f.ClientSize=new Size(width,850);Application.DoEvents();using(var image=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(image,new Rectangle(Point.Empty,f.Size));image.Save(Path.Combine(qa,"report-structure-"+width+".png"));}
                    foreach(string name in new[]{"openReportButton","openReportDirectoryButton","printReportButton","loadButton"}){var button=(Control)Get(c,name);Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+12,"action fits text: "+name);}
                    Check(diagram.Height>70&&diagram.Width>200,"diagram visible");}
                using(var scrollHost=new Form {ClientSize=new Size(620,120),ShowInTaskbar=false})
                using(var scrollPreview=(Control)Activator.CreateInstance(diagram.GetType(),true)){
                    scrollPreview.Dock=DockStyle.Fill;scrollHost.Controls.Add(scrollPreview);scrollHost.Show();
                    Call(scrollPreview,"SetProject",Get(diagram,"project"),0);Application.DoEvents();
                    var drawing=(Panel)Get(scrollPreview,"drawing");
                    using(var before=new Bitmap(drawing.Width,drawing.Height))using(var after=new Bitmap(drawing.Width,drawing.Height)){
                        drawing.DrawToBitmap(before,new Rectangle(Point.Empty,drawing.Size));
                        drawing.AutoScrollPosition=new Point(0,30);Application.DoEvents();
                        int shift=-drawing.AutoScrollPosition.Y;Check(shift>0,"short structure viewport can scroll");
                        drawing.DrawToBitmap(after,new Rectangle(Point.Empty,drawing.Size));
                        int equal=0,total=0;
                        for(int y=3;y<Math.Min(80,drawing.Height-shift-5);y++)for(int x=10;x<drawing.Width-35;x++){
                            total++;if(before.GetPixel(x,y+shift)==after.GetPixel(x,y))equal++;
                        }
                        Check(total>10000&&equal>total*.995,"structure text and graphics translate together when scrolled");
                        after.Save(Path.Combine(qa,"report-structure-scrolled.png"));
                    }
                    scrollHost.Close();
                }
                f.Close();
            }
            Console.WriteLine("REPORT_ACTIONS_STRUCTURE_PASS="+checks+"; PRINTED=False; HARDWARE_ACCESSED=False; EVIDENCE="+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
