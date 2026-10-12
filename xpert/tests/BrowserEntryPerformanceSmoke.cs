using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Offline stage timings, not a claim about hardware or the user's project.
internal static class BrowserEntryPerformanceSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks,layouts;
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
    static void Watch(Control c){c.Layout+=delegate{layouts++;};foreach(Control child in c.Controls)Watch(child);}
    static void Pump(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(2);}Check(done(),"bounded load completed");}
    static double Time(Action a){var sw=Stopwatch.StartNew();a();return sw.Elapsed.TotalMilliseconds;}
    static string Fixture(string qa,string name)
    {
        string p=Path.Combine(qa,name);Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"[ITEM]\r\n始端地点 = \"Synthetic start\"\r\n终端地点 = \"Synthetic end\"\r\n所属单位 = \"Offline QA\"\r\n测试人员 = \"QA\"\r\n电缆长度 = \"176.8\"\r\n电缆芯数 = \"三相单芯\"\r\n相电压 = \"8.7\"\r\n备注 = \"Metadata rendering only\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\r\n1\t0\t0\t0\r\n1\t0\t0\t176.8\r\n",new UTF8Encoding(false));
        // A few dozen tiny legacy records; malformed fixtures remain visible as
        // invalid records, so no waveform or quality evidence is invented.
        for(int i=0;i<117;i++)File.WriteAllText(Path.Combine(p,"A_5kV_2026-10-07-01'"+(i/60).ToString("00")+"'"+(i%60).ToString("00")+".loc"),"Synthetic incomplete record\r\n",new UTF8Encoding(false));
        return p;
    }
    [STAThread]static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Control.CheckForIllegalCrossThreadCalls=true;
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string project=Fixture(qa,"project");
            Assembly a=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            Type type=a.GetType("HVX.Controls.ProjectDataBrowserControl",true),reader=a.GetType("HVX.Controls.BrowserReader",true);
            for(int pass=0;pass<3;pass++){
                Control browser=null;double construct=Time(()=>browser=(Control)Activator.CreateInstance(type));
                using(browser)using(var host=new Form{ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000),ClientSize=new Size(1200,780)}){
                    browser.Dock=DockStyle.Fill;host.Controls.Add(browser);host.Show();Application.DoEvents();Watch(browser);
                    object item=Field(browser,"item");layouts=0;
                    bool firstDone=false;EventHandler first=(s,e)=>firstDone=true;type.GetEvent("ProjectLoaded").AddEventHandler(browser,first);
                    int firstTicks=0;double firstLast=0,firstGap=0;var firstTime=Stopwatch.StartNew();
                    using(var timer=new System.Windows.Forms.Timer{Interval=15}){
                        timer.Tick+=delegate{double now=firstTime.Elapsed.TotalMilliseconds;firstGap=Math.Max(firstGap,now-firstLast);firstLast=now;firstTicks++;};timer.Start();
                        double firstDispatch=Time(()=>Call(browser,"BeginLoadProject",project));Pump(()=>firstDone);Application.DoEvents();
                        firstGap=Math.Max(firstGap,firstTime.Elapsed.TotalMilliseconds-firstLast);
                        Console.WriteLine("FIRST "+pass+": dispatch="+firstDispatch.ToString("0.0")+"ms total="+firstTime.Elapsed.TotalMilliseconds.ToString("0.0")+"ms max-heartbeat-gap="+firstGap.ToString("0.0")+"ms ticks="+firstTicks+" layouts="+layouts);
                    }
                    type.GetEvent("ProjectLoaded").RemoveEventHandler(browser,first);layouts=0;
                    double same=Time(()=>{Call(item,"SetLanguage",0);item.GetType().GetProperty("MetadataReadOnly").SetValue(item,true,null);item.GetType().GetProperty("InsulationReadOnly").SetValue(item,true,null);});int sameLayouts=layouts;
                    layouts=0;double load=Time(()=>Check((bool)Call(item,"LoadItem",project,0),"standalone metadata load"));int itemLayouts=layouts;
                    object prepared=null;double prepare=Time(()=>prepared=type.GetMethod("PrepareProjectLoad",F).Invoke(null,new object[]{project,0d,CancellationToken.None}));
                    layouts=0;double publish=Time(()=>Call(browser,"FinishLoad",prepared));int publishLayouts=layouts;
                    bool loaded=false;type.GetEvent("ProjectLoaded").AddEventHandler(browser,new EventHandler((s,e)=>loaded=true));
                    int ticks=0;double last=0,gap=0;var elapsed=Stopwatch.StartNew();
                    using(var timer=new System.Windows.Forms.Timer{Interval=15}){
                        timer.Tick+=delegate{double now=elapsed.Elapsed.TotalMilliseconds;gap=Math.Max(gap,now-last);last=now;ticks++;};timer.Start();
                        double dispatch=Time(()=>Check((bool)Call(browser,"BeginLoadProject",project),"async dispatch"));Pump(()=>loaded);Application.DoEvents();
                        gap=Math.Max(gap,elapsed.Elapsed.TotalMilliseconds-last);
                        Console.WriteLine("PASS "+pass+": construct="+construct.ToString("0.0")+"ms same-setting="+same.ToString("0.0")+"ms/"+sameLayouts+"layouts item.LoadItem="+load.ToString("0.0")+"ms/"+itemLayouts+"layouts backgroundReader="+prepare.ToString("0.0")+"ms FinishLoad="+publish.ToString("0.0")+"ms/"+publishLayouts+"layouts dispatch="+dispatch.ToString("0.0")+"ms total="+elapsed.Elapsed.TotalMilliseconds.ToString("0.0")+"ms max-heartbeat-gap="+gap.ToString("0.0")+"ms ticks="+ticks);
                    }
                    Check((string)Call(item,"GetFieldValue","StartName")=="Synthetic start","metadata value preserved");
                    Check(((TreeView)Field(browser,"tree")).Nodes.Count==5,"project and all record groups published");
                }
            }
            Console.WriteLine("BrowserEntryPerformanceSmoke PASS: "+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
