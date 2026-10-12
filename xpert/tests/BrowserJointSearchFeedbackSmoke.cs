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

internal static class BrowserJointSearchFeedbackSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p==null?o.GetType().GetField(n,All).GetValue(o):p.GetValue(o,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static void Pump(Func<bool> done){var until=DateTime.UtcNow.AddSeconds(25);do{Application.DoEvents();Thread.Sleep(2);}while(!done()&&DateTime.UtcNow<until);Check(done(),"operation completes");}
    static void Cal(string path,bool terminal,bool joint)
    {
        var text=new StringBuilder("SR\t50000000\r\nCable length (m)\t1700\r\nV (m/usec)\t170\r\nStart_point\t0.00001\r\nFinal_point\t0.00003\r\n");
        for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");
        for(int i=0;i<3501;i++){double y=.001*Math.Sin(i*.72)+Math.Exp(-Math.Pow((i-500)/3d,2))+(joint?.15*Math.Exp(-Math.Pow((i-900)/3d,2)):0)+(terminal?.3*Math.Exp(-Math.Pow((i-1500)/3d,2)):0);text.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));
    }
    static void Mean(Control browser,string path)
    {
        var task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",new[]{path},true,false);Pump(()=>task.IsCompleted);Check(task.Result,"fixture mean ready");
        Check(!(bool)Get(browser,"IsBusy"),"mean releases busy state");
    }
    static bool Feedback(Control browser,string expected,string reason)
    {
        bool seen=false;string details="";Exception dialogError=null;
        using(var timer=new System.Windows.Forms.Timer {Interval=10})
        {
            timer.Tick+=delegate {
                var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name==expected);if(dialog==null)return;
                timer.Stop();try{seen=true;Check(dialog.Icon!=null,"owned result dialog retains brand icon");var text=dialog.Controls.Find("ComparisonJointSearchDetails",true).FirstOrDefault();if(text!=null)details=text.Text;}catch(Exception ex){dialogError=ex;}
                dialog.DialogResult=DialogResult.Cancel;dialog.Close();
            };
            timer.Start();var task=(Task<bool>)Call(browser,"FindComparisonJointsWithFeedbackAsync");Pump(()=>task.IsCompleted);if(dialogError!=null)throw dialogError;
            Check(seen,"feedback modal shown: "+expected);Check(reason==null||details.Contains(reason),"dialog explains actual screening outcome");Check(!(bool)Get(browser,"IsBusy"),"feedback releases busy state");
            Check(((Control)Get(browser,"findComparisonJointsButton")).Enabled,"finder reenabled after result");return task.GetAwaiter().GetResult();
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"joint-feedback-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            File.WriteAllText(Path.Combine(qa,"ITEM.in"),"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=1700\r\n");
            string cable=Path.Combine(qa,"CABLE.in");File.WriteAllText(cable,"0\tA相\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");byte[] cableBefore=File.ReadAllBytes(cable);
            string path=Path.Combine(qa,"A_1nC.cal");Cal(path,false,true);
            var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var host=new Form {ClientSize=new Size(1250,850),ShowInTaskbar=false})
            using(var browser=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                browser.Dock=DockStyle.Fill;host.Controls.Add(browser);host.Show();Application.DoEvents();Check((bool)Call(browser,"LoadProject",qa),"isolated project loads");
                ((CheckBox)Get(browser,"comparisonAverage")).Checked=true;Mean(browser,path);
                Check(((Control)Get(browser,"comparisonAverageNotice")).Text.Contains("1"),"single-contributor mean has visible notice");
                Check(!Feedback(browser,"ComparisonJointSearchResultDialog","terminal reflection"),"missing terminal refuses screening with feedback");
                Check(((IEnumerable)Get(browser,"comparisonJointCandidates")).Cast<object>().Count()==0,"failed screening never invents candidate");
                Cal(path,true,false);Mean(browser,path);Check(Feedback(browser,"ComparisonJointSearchResultDialog",null),"valid terminal with no interior candidate is explained");
                Check(((IEnumerable)Get(browser,"comparisonJointCandidates")).Cast<object>().Count()==0,"no candidate outcome stays empty");
                Cal(path,true,true);Mean(browser,path);Check(Feedback(browser,"ComparisonJointCandidatesDialog",null),"real candidate review dialog opens");
                Check(((IEnumerable)Get(browser,"comparisonJointCandidates")).Cast<object>().Count()==1,"one actual candidate retained");
                File.AppendAllText(path,"0\r\n");Check(!Feedback(browser,"ComparisonJointSearchResultDialog",Path.GetFileName(path)),"modified source rejects stale mean");
                Cal(path,true,true);Mean(browser,path);
                // This fixture verifies an already-cancelled entry. An await can complete
                // synchronously, so cancelling only after Call returns can leave Call
                // blocked in a result modal before the cancellation line is reached.
                // Post-dispatch cancellation without feedback remains covered by
                // BrowserMeanSourceIntegritySmoke.
                var cancellation=(CancellationTokenSource)Get(browser,"waveCancellation");cancellation.Cancel();Check(cancellation.IsCancellationRequested,"pre-cancelled entry fixture");
                bool unexpectedModal=false;
                using(var cancellationWatchdog=new System.Windows.Forms.Timer {Interval=10})
                {
                    cancellationWatchdog.Tick+=delegate {
                        var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name.StartsWith("ComparisonJointSearch",StringComparison.Ordinal)||f.Name=="ComparisonJointCandidatesDialog");if(dialog==null)return;
                        unexpectedModal=true;cancellationWatchdog.Stop();Console.Error.WriteLine("PRE_CANCELLED_ENTRY_UNEXPECTED_MODAL="+dialog.Name);dialog.DialogResult=DialogResult.Cancel;dialog.Close();
                    };
                    cancellationWatchdog.Start();
                    try {
                        var task=(Task<bool>)Call(browser,"FindComparisonJointsWithFeedbackAsync");Pump(()=>task.IsCompleted);
                        Check(!unexpectedModal,"pre-cancelled entry never opens a feedback modal");Check(!task.Result,"pre-cancelled entry does not publish result");
                    }finally{cancellationWatchdog.Stop();}
                }
                Check(!Application.OpenForms.Cast<Form>().Any(f=>f.Name.StartsWith("ComparisonJointSearch")||f.Name=="ComparisonJointCandidatesDialog"),"cancelled search has no stale popup");
                Check(File.ReadAllBytes(cable).SequenceEqual(cableBefore),"all search/close/cancel paths preserve project cable");Check(Directory.GetFiles(qa,"*.bak").Length==0,"screening creates no edit backups");host.Close();
            }
            Console.WriteLine("BROWSER_JOINT_SEARCH_FEEDBACK_PASS="+checks+"; "+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
