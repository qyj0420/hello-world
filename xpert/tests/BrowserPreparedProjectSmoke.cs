using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BrowserPreparedProjectSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,F);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,F).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
    static void Pump(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(2);}Check(done(),"bounded operation completes");Application.DoEvents();}
    static string Fixture(string qa,string name)
    {
        string path=Path.Combine(qa,name);Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"ITEM.in"),"[ITEM]\r\n始端地点 = \""+name+"\"\r\n终端地点 = \"Terminal\"\r\n电缆长度 = \"176.8\"\r\n电缆芯数 = \"三相单芯\"\r\n相电压 = \"8.7\"\r\n备注 = \"Original\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0m\r\n1\t终端\t冷缩\t176.8m\r\n",new UTF8Encoding(false));return path;
    }
    static object Prepare(Control b,string path){return Task.Run(()=>b.GetType().GetMethod("PrepareProjectLoad",F).Invoke(null,new object[]{path,0d,CancellationToken.None})).GetAwaiter().GetResult();}
    static void Picture(Control c,string path){using(var bmp=new Bitmap(c.Width,c.Height)){c.DrawToBitmap(bmp,new Rectangle(Point.Empty,c.Size));bmp.Save(path);}}
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Control.CheckForIllegalCrossThreadCalls=true;
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string a=Fixture(qa,"Project-A"),b=Fixture(qa,"Project-B"),bad=Fixture(qa,"Broken");
            File.WriteAllText(Path.Combine(bad,"ITEM.in"),"not an item",Encoding.UTF8);
            Assembly assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            Type type=assembly.GetType("HVX.Controls.ProjectDataBrowserControl",true);
            using(var browser=(Control)Activator.CreateInstance(type))using(var host=new Form{ClientSize=new Size(1420,850),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2300,-1500)}){
                browser.Dock=DockStyle.Fill;host.Controls.Add(browser);host.Show();Application.DoEvents();
                object item=Get(browser,"item");var view=(Control)Get(browser,"projectLoadingView");var tree=(TreeView)Get(browser,"tree");
                byte[] original=File.ReadAllBytes(Path.Combine(a,"ITEM.in"));int events=0;
                EventHandler completed=(s,e)=>{Check(!(bool)Get(browser,"IsBusy"),"completion event is after apply/layout");Check((bool)Get(item,"IsLoaded"),"completion event sees metadata");Check(!view.Visible,"completion event sees final content");events++;};
                type.GetEvent("ProjectLoaded").AddEventHandler(browser,completed);
                var prepared=Prepare(browser,a);Call(browser,"StartLoad",a);
                Check(view.Visible&&browser.Controls.GetChildIndex(view)==0,"loading cover is visible and frontmost");Check(tree.Nodes.Count==0,"old tree cleared during load");
                Check(!((Control)Get(browser,"shellLayout")).Visible&&view.Bounds==browser.ClientRectangle,"partial shell is hidden behind full-client loading view");
                Picture(browser,Path.Combine(qa,"loading.png"));
                using(var itemLock=new FileStream(Path.Combine(a,"ITEM.in"),FileMode.Open,FileAccess.Read,FileShare.None))
                using(var cableLock=new FileStream(Path.Combine(a,"CABLE.in"),FileMode.Open,FileAccess.Read,FileShare.None)){
                    Check((bool)Call(browser,"FinishLoad",prepared),"prepared snapshot publishes while source files exclusively locked");
                }
                Check(events==1&&(string)Call(item,"GetFieldValue","StartName")=="Project-A","correct snapshot applied once");
                Check(((Control)Get(browser,"shellLayout")).Visible,"complete shell restored after snapshot publication");
                Picture(browser,Path.Combine(qa,"complete.png"));
                Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(a,"ITEM.in"))),"reading did not mutate ITEM");
                long sequence=(long)Get(item,"ChangeSequence");object doc=Get(item,"document");
                Call(item,"SetLanguage",0);Set(item,"MetadataReadOnly",true);Set(item,"InsulationReadOnly",true);
                Check((long)Get(item,"ChangeSequence")==sequence&&ReferenceEquals(doc,Get(item,"document")),"same settings leave document and edit sequence unchanged");
                foreach(int language in new[]{0,1,2}){
                    Call(item,"SetLanguage",language);Check((string)Call(item,"GetFieldValue","StartName")=="Project-A","translation preserves source values");
                    foreach(var size in new[]{new Size(1280,720),new Size(900,580)}){
                        host.ClientSize=size;Application.DoEvents();Call(browser,"ArrangeProjectInformation");
                        var viewport=(ScrollableControl)Get(browser,"itemViewport");var control=(Control)item;
                        Check(control.Width>=viewport.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4,"metadata width fits viewport");
                        Check(viewport.AutoScrollMinSize.Height>=control.Height,"all metadata remains reachable");
                    }
                }
                host.ClientSize=new Size(1420,850);Call(item,"SetLanguage",0);
                Check((bool)Call(item,"LoadItem",a,0),"public LoadItem remains supported");Call(item,"SetFieldValue","Remarks","Edited in QA");
                Check((bool)Get(item,"HasUnsavedChanges"),"public editing marks dirty");string copy=Path.Combine(qa,"copy.in");
                Check((bool)Call(item,"SaveItemCopy",copy)&&File.ReadAllText(copy).Contains("Edited in QA"),"public copy save retains edited value");
                Check(original.SequenceEqual(File.ReadAllBytes(Path.Combine(a,"ITEM.in"))),"public copy save preserves original");
                File.AppendAllText(Path.Combine(a,"ITEM.in"),"始端地点 = \"External change\"\r\n",Encoding.UTF8);
                Call(browser,"Activate",tree.Nodes[4]);Call(browser,"Activate",tree.Nodes[0]);Check((bool)Get(browser,"IsBusy")&&view.Visible,"project node refresh is async with stable cover");Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check((string)Call(item,"GetFieldValue","StartName")=="External change","project node sees external edits");
                int before=events;Call(browser,"BeginLoadProject",a);Thread.Sleep(60);Call(browser,"BeginLoadProject",b);Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(events==before+1&&(string)Call(item,"GetFieldValue","StartName")=="Project-B","queued old project callback cannot publish over new project");
                Call(browser,"Activate",tree.Nodes[0]);Thread.Sleep(40);Call(browser,"BeginLoadProject",a);Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check((string)Call(item,"GetFieldValue","StartName")=="External change"&&!view.Visible,"old metadata refresh cannot remove or replace current project loading state");
                type.GetEvent("ProjectLoaded").RemoveEventHandler(browser,completed);
                Call(browser,"BeginLoadProject",bad);Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(!(bool)Get(item,"IsLoaded")&&(string)Get(item,"LoadedItemPath")=="","bad ITEM does not expose previous project metadata");
                Check(((string)Get(item,"LastError")).Length>0&&!view.Visible,"metadata failure visible without permanent loading cover");
                Call(browser,"BeginLoadProject",Path.Combine(qa,"missing"));Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(view.Visible&&((Control)Get(browser,"projectLoadingText")).Text.Contains("无法读取"),"project failure leaves stable explicit error view");
                Check(((Control)Get(browser,"projectLoadRetry")).Visible&&((Control)Get(browser,"projectLoadRetry")).Enabled,"failed standalone browser retains reopen action");
                Check(!((Control)Get(browser,"projectLoadingText")).Text.Contains(Path.Combine(qa,"missing"))&&((string)Get(browser,"LastError")).Length>0,"failure view is user-facing with details retained separately");
                Check(tree.Nodes.Count==0&&Get(browser,"project")==null&&!(bool)Get(item,"IsLoaded"),"project failure clears prior state");
                EventHandler throwOnApply=null;throwOnApply=(s,e)=>{item.GetType().GetEvent("InformationChanged").RemoveEventHandler(item,throwOnApply);throw new InvalidOperationException("synthetic apply failure");};
                item.GetType().GetEvent("InformationChanged").AddEventHandler(item,throwOnApply);
                Call(browser,"BeginLoadProject",a);Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(view.Visible&&Get(browser,"project")==null&&!(bool)Get(item,"IsLoaded"),"UI apply exception is contained and busy released");
                Check(!((Control)Get(browser,"projectLoadingText")).Text.Contains("synthetic apply failure")&&((string)Get(browser,"LastError")).Contains("synthetic apply failure"),"internal UI failure details do not leak into loading error message");
                EventHandler throwOnComplete=(s,e)=>{throw new InvalidOperationException("synthetic subscriber failure");};type.GetEvent("ProjectLoaded").AddEventHandler(browser,throwOnComplete);
                Check((bool)Call(browser,"LoadProject",a),"observer failure does not reverse successful load");
                type.GetEvent("ProjectLoaded").RemoveEventHandler(browser,throwOnComplete);
                Check(!view.Visible&&!(bool)Get(browser,"IsBusy")&&(bool)Get(item,"IsLoaded"),"observer failure cannot strand busy UI");
                Check(((string)Get(browser,"LastError")).Contains("synthetic subscriber failure"),"observer failure retains diagnostic");
                var shell=(Control)Get(browser,"shellLayout");object projectBeforeReveal=Get(browser,"project");
                byte[] sourceBeforeReveal=File.ReadAllBytes(Path.Combine(a,"ITEM.in"));bool revealInjected=false;int escaped=0;
                ThreadExceptionEventHandler unexpected=(s,e)=>{escaped++;};Application.ThreadException+=unexpected;
                LayoutEventHandler throwOnReveal=null;throwOnReveal=(s,e)=>{
                    if(!view.Visible||!shell.Visible||!(bool)Get(browser,"loadingWave"))return;
                    shell.Layout-=throwOnReveal;revealInjected=true;throw new InvalidOperationException("synthetic metadata reveal layout failure");
                };
                try{
                    Call(browser,"Activate",tree.Nodes[0]);shell.Layout+=throwOnReveal;
                    Pump(()=>revealInjected&&(!(bool)Get(browser,"IsBusy")||escaped>0));
                }finally{shell.Layout-=throwOnReveal;Application.ThreadException-=unexpected;}
                Console.WriteLine("REFRESH_REVEAL injected="+revealInjected+" escaped="+escaped+" busy="+Get(browser,"IsBusy"));
                Check(revealInjected&&escaped==0&&!(bool)Get(browser,"IsBusy"),"metadata reveal exception is contained and always clears busy");
                Check(view.Visible&&((Control)Get(browser,"projectLoadRetry")).Visible&&!shell.Visible,"metadata reveal failure has stable recoverable error view");
                Check(!((Control)Get(browser,"projectLoadingText")).Text.Contains("synthetic metadata reveal")&&((string)Get(browser,"LastError")).Contains("synthetic metadata reveal"),"metadata reveal details retained outside operator error text");
                Check(ReferenceEquals(projectBeforeReveal,Get(browser,"project"))&&(string)Call(item,"GetFieldValue","StartName")=="External change","metadata reveal failure preserves correct applied project snapshot");
                Check(sourceBeforeReveal.SequenceEqual(File.ReadAllBytes(Path.Combine(a,"ITEM.in"))),"metadata reveal failure never writes source");
                Call(browser,"BeginLoadProject",a);Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(!view.Visible&&shell.Visible&&(bool)Get(item,"IsLoaded"),"explicit reopen recovers after metadata reveal failure");
                Call(browser,"BeginLoadProject",b);Thread.Sleep(60);browser.Dispose();Application.DoEvents();
                Check(browser.IsDisposed,"dispose cancels queued publication safely");
            }
            Console.WriteLine("BrowserPreparedProjectSmoke PASS: "+checks+"; screenshots="+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
