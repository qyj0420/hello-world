using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BrowserSharedMeanJointSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p==null?o.GetType().GetField(n,All).GetValue(o):p.GetValue(o,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
    static string Block(string phase,double joint,double length=1700){return "0\t"+phase+"相\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t人工接头\t冷缩\t"+joint+"m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t"+length+"m\r\n";}
    static void Pump(Task task){var end=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(5);}Check(task.IsCompleted,"mean completes within timeout");Application.DoEvents();}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"shared-mean-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            string[] names={"shared-repeat-A","shared-ABC","shared-conflict","separate-ALL","separate-A","repeated-A-conflict","wrong-length"};
            for(int n=0;n<names.Length;n++)
            {
                string root=Path.Combine(qa,names[n]);Directory.CreateDirectory(root);
                string mode=n<3?"一体三芯":"单芯三相",phase=n<4?"ALL":"A";
                File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆芯数="+mode+"\r\nA相电缆长度=1700\r\nB相电缆长度=1700\r\nC相电缆长度=1700\r\n");
                string cable=n==0?Block("A",680)+Block("A",680)+Block("A",680):n==5?Block("A",680)+Block("A",700):n==6?Block("A",680,1800):Block("A",680)+Block("B",n==1?680:700)+Block("C",n==1?680:800);
                string cp=Path.Combine(root,"CABLE.in");File.WriteAllText(cp,cable);byte[] original=File.ReadAllBytes(cp);
                string[] calibrations=new[]{Path.Combine(root,phase+"_1nC.cal"),Path.Combine(root,phase+"_2nC.cal")};
                foreach(string path in calibrations)typeof(BrowserAverageGeometrySmoke).GetMethod("Cal",All).Invoke(null,new object[]{path,10e-6,1700d,170d,1d});
                byte[][] source=calibrations.Select(File.ReadAllBytes).ToArray();
                using(var form=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-1200),ClientSize=new Size(1280,800)})
                using(var browser=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
                {
                    browser.Dock=DockStyle.Fill;form.Controls.Add(browser);form.Show();Application.DoEvents();Check((bool)Call(browser,"LoadProject",root),names[n]+" loads");
                    ((CheckBox)Get(browser,"comparisonAverage")).Checked=true;var task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(task);Check(task.Result,names[n]+" compatible waveforms still average");
                    object plot=Get(browser,"plot");bool expected=n==0||n==1||n==4;
                    var labels=(string[])Get(plot,"CandidateLabels");var positions=(double[])Get(plot,"CandidateX");
                    Check(labels.Length==(expected?1:0),names[n]+" does not omit or invent shared joints");
                    if(expected)Check(labels[0].Contains("680.0")&&Math.Abs(positions[0]-18)<1e-8,names[n]+" timebase projects one 680m joint at 18us");
                    else Check(((string)Get(browser,"comparisonJointProjectionWarning")).Length>0&&((TextBox)Get(browser,"information")).Text.Contains("未投影"),names[n]+" displays why stored joints cannot project");
                    Check((bool)Call(browser,"RefreshCableStructure"),names[n]+" metadata refresh works");Check(((string[])Get(plot,"CandidateLabels")).Length==(expected?1:0),names[n]+" refresh preserves scope");
                    object snapshot=asm.GetType("HVX.ProjectCables.CableJointStore").GetMethod("Read",All).Invoke(null,new object[]{root});bool rejected=false;
                    try{Call(browser,"RequireComparisonProjectGeometry",snapshot);}catch(TargetInvocationException e){if(!(e.InnerException is InvalidDataException))throw;rejected=true;}
                    Check(rejected!=expected,names[n]+" manual commit geometry gate agrees with projection");
                    if(n==0){using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(qa,"shared-mean-marker.png"));}}
                    if(n==0){File.WriteAllText(cp,Block("A",700)+Block("A",700)+Block("A",700));Check((bool)Call(browser,"RefreshCableStructure"),"updated shared joint refreshes");Check(((string[])Get(plot,"CandidateLabels"))[0].Contains("700.0"),"no stale shared marker after refresh");File.WriteAllBytes(cp,original);}
                    Check(original.SequenceEqual(File.ReadAllBytes(cp))&&calibrations.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(source[i])).All(v=>v),names[n]+" display leaves project/calibration bytes unchanged");form.Close();
                }
            }
            Console.WriteLine("BROWSER_SHARED_MEAN_JOINT_PASS="+checks+";HARDWARE=False;REAL_PROJECT_WRITE=False;QA="+qa);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
