using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Isolated, synthetic waveform/UI failure fixture. No product entry or hardware.
internal static class BrowserWaveformPublicationSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string reason){checks++;if(!ok)throw new InvalidOperationException(reason);}
    static void Pump(Func<bool> done){var sw=Stopwatch.StartNew();while(!done()&&sw.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(2);}Check(done(),"bounded asynchronous completion");Application.DoEvents();}
    static void Loading(object browser,bool active,string reason)
    {
        var detail=(Control)Field(browser,"detail");var view=(Control)Field(browser,"detailLoadingView");var progress=(ProgressBar)Field(browser,"detailLoadingProgress");
        Check(active?(view.Parent==detail&&view.Visible&&progress.Style==ProgressBarStyle.Marquee&&progress.MarqueeAnimationSpeed>0&&detail.UseWaitCursor)
            :(view.Parent!=detail&&!view.Visible&&progress.MarqueeAnimationSpeed==0&&!detail.UseWaitCursor),reason);
    }
    static string LocationFixture(string root,string suffix)
    {
        string path=Path.Combine(root,"A_1.0kV_"+suffix+".loc");
        var text=new StringBuilder("SR\t100000000 Hz\r\nCable-length\t170\r\nVelocity\t170\r\n");
        for(int i=0;i<8;i++)text.Append("Reserved").Append(i).Append("\t0\r\n");
        for(int i=0;i<401;i++)text.Append((100000*Math.Sin(i*.1)).ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));return path;
    }
    static string Fixture(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n始端地点 = \"Offline QA\"\r\n终端地点 = \"End\"\r\n电缆长度 = \"170\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0m\r\n1\t终端\t冷缩\t170m\r\n",new UTF8Encoding(false));
        string path=Path.Combine(root,"ALL_5nC.cal");
        var s=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");
        for(int i=0;i<8;i++)s.Append("Field").Append(i).Append("\t0\r\n");
        for(int i=0;i<401;i++)s.Append((Math.Exp(-Math.Pow((i-100)/10d,2))+.4*Math.Exp(-Math.Pow((i-300)/10d,2))).ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(path,s.ToString(),new UTF8Encoding(false));return path;
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Control.CheckForIllegalCrossThreadCalls=true;
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string project=Path.Combine(qa,"fixture"),cal=Fixture(project);
            string loc=LocationFixture(project,"2026-10-11-01-00-00"),loc2=LocationFixture(project,"2026-10-11-01-00-01");
            byte[] original=File.ReadAllBytes(cal),originalLoc=File.ReadAllBytes(loc),originalLoc2=File.ReadAllBytes(loc2);
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var b=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            using(var host=new Form{ClientSize=new Size(1200,780),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-2000,-2000)}){
                b.Dock=DockStyle.Fill;host.Controls.Add(b);host.Show();Application.DoEvents();Check((bool)Call(b,"LoadProject",project),"synthetic project loads");
                var initialGate=Field(b,"waveGate");Monitor.Enter(initialGate);
                try{
                    Check((bool)Call(b,"SelectFile",loc),"LOC click starts existing asynchronous reader");
                    Loading(b,true,"LOC detail immediately shows loading, marquee and wait cursor before disk read completes");
                    Check(((Control)Field(b,"detailLoadingText")).Text=="正在读取波形…","short Chinese loading message");
                    long sequence=(long)Get(b,"SelectionSequence");
                    Check((bool)Call(b,"SelectFile",loc),"same LOC remains selectable while loading");
                    Check((long)Get(b,"SelectionSequence")==sequence,"same-file click does not create another read");
                    b.GetType().GetProperty("LanguageIndex",F).SetValue(b,1,null);
                    Check(((Control)Field(b,"detailLoadingText")).Text=="Loading waveform…","loading label changes language without starting another read");
                    Check((long)Get(b,"SelectionSequence")==sequence,"language change preserves selected request");
                    int oldSelection=(int)Field(b,"selectionGeneration"),oldProject=(int)Field(b,"projectGeneration");
                    Check((bool)Call(b,"SelectFile",loc2),"new LOC supersedes old request");
                    Call(b,"HideDetailLoading",oldSelection,oldProject);
                    Call(b,"FailWaveLoad",new OperationCanceledException("obsolete synthetic request"),oldSelection,oldProject);
                    Loading(b,true,"stale completion and cancellation cannot hide newer LOC loading");
                    Check((string)Get(b,"SelectedFilePath")==loc2&&(string)Get(b,"LastError")=="","stale callback cannot replace new selected identity or diagnostic");
                }finally{Monitor.Exit(initialGate);}
                Pump(()=>!(bool)Get(b,"IsBusy"));Loading(b,false,"LOC success clears all loading feedback");
                Check(((Control)Field(b,"locationPlot")).Parent==(Control)Field(b,"detail")&&Field(b,"selectedWave")!=null,"new LOC is actually displayed");
                Check(originalLoc.SequenceEqual(File.ReadAllBytes(loc))&&originalLoc2.SequenceEqual(File.ReadAllBytes(loc2)),"LOC loading feedback preserves original source bytes");
                b.GetType().GetProperty("LanguageIndex",F).SetValue(b,0,null);
                int escaped=0,added=0;bool injected=false;var detail=(Control)Field(b,"detail");var calibration=(Control)Field(b,"calibration");
                ThreadExceptionEventHandler unexpected=(s,e)=>{escaped++;Console.WriteLine("ESCAPED="+e.Exception.GetType().Name+":"+e.Exception.Message);};
                ControlEventHandler failOnce=null;failOnce=(s,e)=>{if(e.Control!=calibration)return;added++;if(injected)return;injected=true;throw new InvalidOperationException("synthetic waveform publish failure");};
                Application.ThreadException+=unexpected;detail.ControlAdded+=failOnce;
                try{
                    Check((bool)Call(b,"SelectFile",cal),"select waveform starts asynchronous read");Pump(()=>injected&&!(bool)Get(b,"IsBusy"));
                    Console.WriteLine("WAVE_PUBLISH injected="+injected+" escaped="+escaped+" busy="+Get(b,"IsBusy")+" lastError="+Get(b,"LastError")+" status="+((Control)Field(b,"status")).Text+" selectedWave="+(Field(b,"selectedWave")!=null));
                    Check(original.SequenceEqual(File.ReadAllBytes(cal)),"publication never changes original CAL bytes");
                    Check(injected&&escaped==0,"waveform UI publication failure must not escape the callback");
                    Loading(b,false,"publication failure clears loading feedback");
                    Check(Field(b,"selectedWave")==null,"failed waveform is not exposed as a successful selected result");
                    Check(((string)Get(b,"LastError")).Contains("synthetic waveform publish failure"),"failure retains diagnostic outside operator text");
                    Check(!((Control)Field(b,"information")).Text.Contains("synthetic waveform publish failure"),"operator message does not expose internal exception text");
                    Check(!((Control)Field(b,"actions")).Visible&&!((Control)Field(b,"editButton")).Enabled,"failure hides waveform actions and disables editing");
                    Check((bool)Call(b,"SelectFile",cal),"explicitly retry same failed selection");Pump(()=>!(bool)Get(b,"IsBusy"));
                    Check(added==2&&Field(b,"selectedWave")!=null,"same-record retry publishes successfully after transient failure");
                    Loading(b,false,"successful retry clears loading feedback");
                    Check(original.SequenceEqual(File.ReadAllBytes(cal)),"retry leaves source bytes unchanged");
                    Check((string)Get(b,"LastError")=="","successful retry clears previous diagnostic");
                    object successfulWave=Field(b,"selectedWave");Check((bool)Call(b,"SelectFile",cal),"same successful selection remains supported");Application.DoEvents();
                    Check(ReferenceEquals(successfulWave,Field(b,"selectedWave"))&&added==2,"successful same-node selection does not unnecessarily reload");

                    var tree=(TreeView)Field(b,"tree");tree.SelectedNode=tree.Nodes[4];
                    File.Delete(cal);Check((bool)Call(b,"SelectFile",cal),"indexed file may disappear before waveform read");Pump(()=>!(bool)Get(b,"IsBusy"));
                    Check(Field(b,"selectedWave")==null&&(bool)Field(b,"waveLoadFailed")&&((string)Get(b,"LastError")).Length>0,"read failure also clears result and exposes retry");
                    Loading(b,false,"read failure clears loading feedback");
                    File.WriteAllBytes(cal,original);
                    typeof(TreeView).GetMethod("OnNodeMouseClick",F).Invoke(tree,new object[]{new TreeNodeMouseClickEventArgs(tree.SelectedNode,MouseButtons.Left,1,1,1)});
                    Pump(()=>!(bool)Get(b,"IsBusy"));
                    Check(Field(b,"selectedWave")!=null&&!(bool)Field(b,"waveLoadFailed"),"real same-node mouse action retries after file is restored");

                    // Hold the reader gate to make cancellation/switching deterministic.
                    tree.SelectedNode=tree.Nodes[4];object gate=Field(b,"waveGate");
                    Monitor.Enter(gate);
                    try{
                        Check((bool)Call(b,"SelectFile",cal),"start cancellable read");
                        Loading(b,true,"current read shows loading while reader gate is held");
                        ((CancellationTokenSource)Field(b,"waveCancellation")).Cancel();
                    }finally{Monitor.Exit(gate);}
                    Pump(()=>!(bool)Get(b,"IsBusy"));
                    Check(Field(b,"selectedWave")==null&&(bool)Field(b,"waveLoadFailed"),"cancelled current read has no usable waveform and can be retried");
                    Loading(b,false,"current cancellation clears loading feedback");
                    Check(escaped==0,"cancellation never escapes UI callback");
                    Check((bool)Call(b,"SelectFile",cal),"retry cancelled selection");Pump(()=>!(bool)Get(b,"IsBusy"));
                    Check(Field(b,"selectedWave")!=null,"cancelled read can recover on explicit retry");

                    tree.SelectedNode=tree.Nodes[4];int beforeStale=added;
                    Monitor.Enter(gate);
                    try{
                        Check((bool)Call(b,"SelectFile",cal),"start read before quick navigation");
                        tree.SelectedNode=tree.Nodes[4];
                        Loading(b,false,"navigation clears loading immediately without waiting for old reader");
                    }finally{Monitor.Exit(gate);}
                    var settle=Stopwatch.StartNew();while(settle.ElapsedMilliseconds<150){Application.DoEvents();Thread.Sleep(2);}
                    Check((string)Get(b,"SelectedNodeKind")=="stats1"&&Field(b,"selectedWave")==null&&added==beforeStale,"obsolete selection cannot publish over statistics");

                    Monitor.Enter(gate);
                    try{
                        Check((bool)Call(b,"SelectFile",cal),"start read before project reload");
                        Check((bool)Call(b,"LoadProject",project),"reload project while earlier waveform is waiting");
                    }finally{Monitor.Exit(gate);}
                    settle.Restart();while(settle.ElapsedMilliseconds<150){Application.DoEvents();Thread.Sleep(2);}
                    Check((string)Get(b,"SelectedNodeKind")=="project"&&Field(b,"selectedWave")==null&&added==beforeStale,"obsolete project generation cannot republish waveform");
                    Loading(b,false,"project reload clears previous detail loading");
                    Check(escaped==0&&original.SequenceEqual(File.ReadAllBytes(cal)),"normal, failure, retry and cancellation all preserve source bytes");

                    Monitor.Enter(gate);
                    try{Check((bool)Call(b,"SelectFile",cal),"start read before dispose");b.Dispose();}
                    finally{Monitor.Exit(gate);}
                    settle.Restart();while(settle.ElapsedMilliseconds<150){Application.DoEvents();Thread.Sleep(2);}
                    Check(b.IsDisposed&&escaped==0,"dispose prevents queued waveform publication");
                    Check(((Control)Field(b,"detailLoadingView")).IsDisposed,"detached loading view and child progress are disposed");
                }finally{detail.ControlAdded-=failOnce;Application.ThreadException-=unexpected;}
            }
            Console.WriteLine("BrowserWaveformPublicationSmoke PASS: "+checks);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
