using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class AuditSharedMeanJointProjection
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p==null?o.GetType().GetField(n,All).GetValue(o):p.GetValue(o,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(Task task){while(!task.IsCompleted){Application.DoEvents();Thread.Sleep(5);}Application.DoEvents();}
    [STAThread] static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();
        string root=Path.Combine(Path.GetFullPath(args[1]),"audit-shared-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆芯数=一体三芯\r\nA相电缆长度=1700\r\nB相电缆长度=1700\r\nC相电缆长度=1700\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),String.Join("",new[]{"A","A","A"}.Select(p=>"0\t"+p+"相\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t人工接头\t冷缩\t680m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n")));
        foreach(string name in new[]{"ALL_1nC.cal","ALL_2nC.cal"})typeof(BrowserAverageGeometrySmoke).GetMethod("Cal",All).Invoke(null,new object[]{Path.Combine(root,name),10e-6,1700d,170d,1d});
        var a=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
        using(var form=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-1200),ClientSize=new Size(1280,800)})
        using(var browser=(Control)Activator.CreateInstance(a.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
        {
            form.Controls.Add(browser);browser.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            if(!(bool)Call(browser,"LoadProject",root))throw new Exception("fixture load failed");
            ((CheckBox)Get(browser,"comparisonAverage")).Checked=true;
            var task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(task);if(!task.Result)throw new Exception("mean failed");
            var plot=(Control)Get(browser,"plot");var geometry=Get(browser,"comparisonGeometry");
            string[] labels=(string[])Get(plot,"CandidateLabels");
            Console.WriteLine("mean-phase="+Get(geometry,"Phase")+";saved-joint-count="+((double[])Get(Get(browser,"project"),"JointPositionsMetres")).Length+";overlay-labels="+String.Join("|",labels));
            Call(browser,"RefreshCableStructure");Console.WriteLine("after-metadata-refresh-labels="+String.Join("|",(string[])Get(plot,"CandidateLabels")));
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,form.Size));bmp.Save(Path.Combine(root,"missing-joint.png"));}
            Console.WriteLine("QA="+root);
        }
        return 0;
    }
}
