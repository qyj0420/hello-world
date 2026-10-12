using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BrowserAverageGeometrySmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string name){var p=o.GetType().GetProperty(name,All);return p==null?o.GetType().GetField(name,All).GetValue(o):p.GetValue(o,null);}
    static void Set(object o,string name,object value){var p=o.GetType().GetProperty(name,All);if(p!=null)p.SetValue(o,value,null);else o.GetType().GetField(name,All).SetValue(o,value);}
    static object Call(object o,string name,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static void Pump(Func<bool> done){DateTime limit=DateTime.UtcNow.AddSeconds(30);while(!done()&&DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(5);}Check(done(),"async finishes");Application.DoEvents();}
    static bool Compare(Control browser,string[] paths,bool normalize,bool align){var t=(Task<bool>)Call(browser,"CompareCalibrationsAsync",paths,normalize,align);Pump(()=>t.IsCompleted);return t.Result;}
    static void Cal(string path,double incident=10e-6,double length=1700,double velocity=170,double multiplier=1)
    {
        double sr=50e6;var text=new StringBuilder("SR\t50000000\r\nCable length (m)\t"+length.ToString("R",CultureInfo.InvariantCulture)+"\r\nV (m/usec)\t"+velocity.ToString("R",CultureInfo.InvariantCulture)+"\r\nStart_point\t"+incident.ToString("R",CultureInfo.InvariantCulture)+"\r\nFinal_point\t"+(incident+2*length/velocity*1e-6).ToString("R",CultureInfo.InvariantCulture)+"\r\n");
        for(int i=0;i<8;i++)text.Append("Field"+i+"\t0\r\n");
        for(int i=0;i<3501;i++){double center=incident*sr;double y=.001*Math.Sin(i*.72)+Math.Exp(-Math.Pow((i-center)/3,2))+.15*Math.Exp(-Math.Pow((i-center-400)/3,2))+.3*Math.Exp(-Math.Pow((i-center-1000)/3,2))+.2*Math.Exp(-Math.Pow((i-center-2000)/3,2));text.Append((y*multiplier).ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"average-geometry-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=1700\r\n");
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tA相\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");
            string a=Path.Combine(project,"A_1nC.cal"),b=Path.Combine(project,"A_2nC.cal");Cal(a);Cal(b,multiplier:2);
            byte[] originalA=File.ReadAllBytes(a),originalB=File.ReadAllBytes(b),originalCable=File.ReadAllBytes(Path.Combine(project,"CABLE.in"));
            var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var host=new Form{ClientSize=new Size(1400,880),ShowInTaskbar=false})using(var browser=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                Console.WriteLine("Icons constructed="+((ImageList)Get(browser,"navigationIcons")).Images.Count);
                host.Controls.Add(browser);browser.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                Console.WriteLine("Icons shown="+((ImageList)Get(browser,"navigationIcons")).Images.Count);
                Check((bool)Call(browser,"LoadProject",project),"fixture loads");var tree=(TreeView)Get(browser,"tree");
                Check(tree.ShowPlusMinus&&tree.ShowRootLines&&tree.ImageList.Images.Count==5,"native expansion and five independent type icons enabled: "+tree.ShowPlusMinus+"/"+tree.ShowRootLines+"/"+(tree.ImageList==null?-1:tree.ImageList.Images.Count));
                Check(tree.Nodes[1].ImageKey=="cal"&&tree.Nodes[2].ImageKey=="dem"&&tree.Nodes[3].ImageKey=="loc"&&tree.Nodes[4].ImageKey=="stats","category icons mapped by data type");
                tree.Nodes[1].Collapse();Check(!tree.Nodes[1].IsExpanded,"collapse available");tree.Nodes[1].Expand();Check(tree.Nodes[1].IsExpanded,"expand available");
                var avg=(CheckBox)Get(browser,"comparisonAverage");avg.Checked=true;
                Check(Compare(browser,null,true,false),"compatible normalized average");
                var plot=(Control)Get(browser,"plot");var geom=Get(browser,"comparisonGeometry");
                Check(Math.Abs((double)Get(plot,"CableIncidentX")-10)<1e-8&&Math.Abs((double)Get(plot,"CableEndX")-30)<1e-8,"shade follows incident plus 2L/v in microseconds");
                Check(Math.Abs((double)Call(plot,"CableDistanceAt",18d)-680)<1e-8,"hover distance uses actual incident coordinate");
                var find=(Task<bool>)Call(browser,"FindComparisonJointsAsync");Pump(()=>find.IsCompleted);Check(find.Result,"candidate extraction completes");
                var candidates=((IEnumerable)Get(browser,"comparisonJointCandidates")).Cast<object>().ToArray();Check(candidates.Length==1,"one interior candidate, no terminal/multiple");
                Check(Math.Abs((double)Get(candidates[0],"DistanceM")-680)<2,"candidate physical location");
                Check(((double[])Get(plot,"CandidateX")).Length==1&&((string[])Get(plot,"CandidateLabels"))[0].Contains("SNR"),"triangles/cursor labels include evidence");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(qa,"candidate-screening.png"));}
                Check(originalCable.SequenceEqual(File.ReadAllBytes(Path.Combine(project,"CABLE.in"))),"finding candidates never writes cable structure");
                var bounds=(Rectangle)Get(Get(plot,"CurrentLayout"),"Plot");
                bool dialogSeen=false;double proposed=Double.NaN;
                int clickX=bounds.Left+(int)Math.Round((18-(double)Get(plot,"xmin"))/((double)Get(plot,"xmax")-(double)Get(plot,"xmin"))*bounds.Width);
                double clickTime=(double)Get(plot,"xmin")+((double)Get(plot,"xmax")-(double)Get(plot,"xmin"))*(clickX-bounds.Left)/bounds.Width;
                using(var timer=new System.Windows.Forms.Timer{Interval=20})
                {
                    timer.Tick+=delegate{var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CableJointDialog");if(dialog==null)return;timer.Stop();dialogSeen=true;proposed=(double)((NumericUpDown)dialog.Controls.Find("JointPosition",true).Single()).Value;Call(dialog,"ConfigureMaterialCatalog",Path.Combine(qa,"isolated-catalog.ini"));dialog.DialogResult=DialogResult.Cancel;dialog.Close();};
                    timer.Start();Call(plot,"OnMouseDoubleClick",new MouseEventArgs(MouseButtons.Left,2,clickX,bounds.Top+bounds.Height/2,0));
                    Pump(()=>dialogSeen);
                }
                Check(dialogSeen&&Math.Abs(proposed-(clickTime-10)*85)<.11,"double-click opens shared editor with exact displayed time-to-distance conversion");
                Check(originalCable.SequenceEqual(File.ReadAllBytes(Path.Combine(project,"CABLE.in"))),"cancelled double-click dialog does not persist candidate");
                Call(plot,"OnMouseMove",new MouseEventArgs(MouseButtons.None,0,bounds.Left+bounds.Width/3,bounds.Top+bounds.Height/2,0));
                Check(((string)Get(plot,"lastLegendTip")).Contains("μs")&&((string)Get(plot,"lastLegendTip")).Contains(" m"),"moving cursor reports time and distance");
                double xmin=(double)Get(plot,"xmin"),xmax=(double)Get(plot,"xmax"),lo=(double)Get(plot,"lo1"),hi=(double)Get(plot,"hi1");
                Call(plot,"ApplyWheelZoom",new MouseEventArgs(MouseButtons.None,0,bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2,120),true);
                Check((double)Get(plot,"xmin")==xmin&&(double)Get(plot,"xmax")==xmax&&(double)Get(plot,"hi1")-(double)Get(plot,"lo1")<hi-lo,"Shift wheel changes Y only");
                Call(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,bounds.Left+10,bounds.Top+10,0));
                Call(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,bounds.Left+bounds.Width/4,bounds.Top+bounds.Height/4,0));
                Call(plot,"OnMouseMove",new MouseEventArgs(MouseButtons.Left,1,bounds.Left+bounds.Width*3/4,bounds.Top+bounds.Height*3/4,0));
                Call(plot,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,bounds.Left+bounds.Width*3/4,bounds.Top+bounds.Height*3/4,0));
                Check((double)Get(plot,"xmax")-(double)Get(plot,"xmin")<xmax-xmin,"left rectangle zooms average");
                Call(plot,"OnMouseDown",new MouseEventArgs(MouseButtons.Right,1,bounds.Left+10,bounds.Top+10,0));Check((double)Get(plot,"xmin")==xmin&&(double)Get(plot,"xmax")==xmax,"right click restores full X domain");
                Check(Compare(browser,null,false,true),"raw aligned average");Check(Math.Abs((double)Get(plot,"CableIncidentX"))<1e-10&&Math.Abs((double)Call(plot,"CableDistanceAt",8d)-680)<1e-8,"aligned origin maps same cable distance");
                Check(((IEnumerable)Get(browser,"comparisonJointCandidates")).Cast<object>().Count()==0,"changed mean clears stale candidates");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(qa,"mean-tree-shade.png"));}
                Cal(b,incident:11e-6);Check(Compare(browser,null,true,false),"different incidents retain actual-time display average");Check(!(bool)Get(plot,"HasCableOverlay")&&Get(browser,"comparisonGeometry")==null,"unaligned mean clears stale distance geometry");
                Check(Compare(browser,null,true,true),"explicit alignment supports changed source origin");
                Cal(b,length:1800);Check(Compare(browser,null,true,true)&&Get(browser,"comparisonGeometry")==null,"different lengths allow time average without fabricated distance");
                Cal(b,velocity:169);Check(Compare(browser,null,true,true)&&Get(browser,"comparisonGeometry")==null,"different velocities allow time average without fabricated velocity");
                File.WriteAllBytes(b,originalB);string bp=Path.Combine(project,"B_3nC.cal");File.WriteAllBytes(bp,originalB);Check((bool)Call(browser,"LoadProject",project),"mixed-phase fixture loads");avg.Checked=true;
                Check(Compare(browser,null,true,true)&&Get(browser,"comparisonGeometry")==null,"cross-phase time average does not invent a common phase");Check(Compare(browser,new[]{a,b},true,true),"compatible subset remains available");
                Check((bool)Call(browser,"SelectFile",a),"single calibration navigation remains available");Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(!((Control)Get(browser,"findComparisonJointsButton")).Visible,"average-only finder hidden on single view");
                Check(File.ReadAllBytes(a).SequenceEqual(originalA)&&File.ReadAllBytes(b).SequenceEqual(originalB)&&File.ReadAllBytes(Path.Combine(project,"CABLE.in")).SequenceEqual(originalCable),"all display/search operations preserve source files");
                // Explicit editor commit is distinct from screening. Simulate a
                // stale plan first; it must throw synchronously so the dialog stays.
                object snapshot=asm.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Read",All).Invoke(null,new object[]{project});
                object plan=Call(snapshot,"Prepare",new[]{"A"},680d,"接头 1","冷缩","","");
                string item=Path.Combine(project,"ITEM.in");byte[] originalItem=File.ReadAllBytes(item);File.AppendAllText(item,"备注=other editor\r\n");
                bool failed=false;try{Call(browser,"CommitJointDialog",plan,"AddJoint",project,(int)Get(browser,"projectGeneration"),(int)Get(browser,"selectionGeneration"));}catch(TargetInvocationException){failed=true;}
                Check(failed&&!(bool)Get(browser,"IsBusy")&&((Control)Get(browser,"tree")).Enabled,"transaction failure propagates synchronously and exits busy");
                Check(File.ReadAllBytes(Path.Combine(project,"CABLE.in")).SequenceEqual(originalCable),"failed modal commit leaves cable untouched");
                File.WriteAllBytes(item,originalItem);
                Call(browser,"CommitJointDialog",plan,"AddJoint",project,(int)Get(browser,"projectGeneration"),(int)Get(browser,"selectionGeneration"));
                Check((int)Get(browser,"LastChangedFileCount")==1&&Directory.GetFiles(project,"CABLE.before-joint-*.bak").Length==1,"confirmed commit saves once and retains exact backup");
                Check(((double[])Get(Get(browser,"project"),"JointPositionsMetres")).Contains(680d),"confirmed joint refreshes browser structure immediately");
                Check(File.ReadAllBytes(a).SequenceEqual(originalA)&&File.ReadAllBytes(b).SequenceEqual(originalB),"confirmed joint metadata write does not modify calibration coefficients or raw waves");
                host.Close();
            }
            Console.WriteLine("BROWSER_AVERAGE_GEOMETRY_PASS="+checks+"; "+qa);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
