using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Synthetic LOC files and an isolated UI control only. No product entry point,
// analysis algorithm, model inference, worker, SDK or device is invoked.
static class LocationOverviewLoadingOfflineSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Type type,reader;
    static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
    static object Field(object value,string name){return value.GetType().GetField(name,B).GetValue(value);}
    static object Call(object value,string name,params object[] args)
    {try{return value.GetType().GetMethod(name,B).Invoke(value,args);}catch(TargetInvocationException ex){throw ex.InnerException??ex;}}
    static object Static(string name,params object[] args)
    {try{return reader.GetMethod(name,B).Invoke(null,args);}catch(TargetInvocationException ex){throw ex.InnerException??ex;}}
    static T Property<T>(object value,string name){return (T)value.GetType().GetProperty(name,B).GetValue(value,null);}
    static Task<bool> Begin(Control control,string path){return (Task<bool>)Call(control,"BeginLocationOverviewLoad",path);}
    static bool Busy(Control control){return Property<bool>(control,"IsLocationOverviewLoading");}
    static bool Complete(Task<bool> task)
    {var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(1);}Check(task.IsCompleted,"Bounded UI load completes.");return task.GetAwaiter().GetResult();}
    static void Pump(){for(int i=0;i<8;i++){Application.DoEvents();Thread.Sleep(2);}}
    static string Hash(string path){using(var f=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(f)).Replace("-","");}
    static string Put(string root,string name,int count,int scale)
    {
        string path=Path.Combine(root,name+".loc");
        var lines=new[]{"Phase\tA","Voltage\t13.4 kV","Velocity\t169.6 m/us","SampleRate\t125000000 Hz","ChargeUnit\tpC","Reserved\t","Reserved\t","Reserved\t","Reserved\t","Reserved\t","Reserved\t"};
        File.WriteAllLines(path,lines.Concat(Enumerable.Range(0,count).Select(i=>((i%31-15)*scale).ToString(System.Globalization.CultureInfo.InvariantCulture))),new UTF8Encoding(false));return path;
    }
    static void Idle(Control control)
    {Check(!Busy(control),"Busy property restored.");Check(!((Control)Field(control,"overviewHost")).UseWaitCursor,"Wait cursor restored.");Check(!((Control)Field(control,"overviewLoadingView")).Visible,"Loading overlay restored.");Check(((Control)Field(control,"locationOverview")).Enabled,"Waveform interaction restored.");}
    static double[] Samples(object value){return (double[])Field(value,"ChargeSamples");}
    [STAThread] static int Main(string[] args)
    {
        try {
            if(args.Length!=2)throw new ArgumentException("Expected isolated module and fresh fixture directory.");
            string root=Path.GetFullPath(args[1]);if(Directory.Exists(root)||File.Exists(root))throw new IOException("Fresh fixture root required.");Directory.CreateDirectory(root);
            string a=Put(root,"A_13.40kV_a",64000,3125),b=Put(root,"B_13.40kV_b",32000,6250),bad=Put(root,"A_13.40kV_bad",32,1000);
            var badLines=File.ReadAllLines(bad);badLines[20]="NaN";File.WriteAllLines(bad,badLines,new UTF8Encoding(false));
            string[] paths={a,b,bad};string[] before=paths.Select(Hash).ToArray();
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));type=assembly.GetType("HVX.PdReview.PdReviewControl",true);reader=assembly.GetType("HVX.PdReview.ReviewLocationFileReader",true);
            Check(reader.GetMethod("Read",B).GetParameters().Length==2,"Original unique two-argument reader remains reflection-compatible.");
            var normal=Samples(Static("Read",a,root));var cancellable=Samples(Static("ReadCancellable",a,root,CancellationToken.None));Check(normal.SequenceEqual(cancellable),"Cancellable parser is sample-identical.");
            var cancelled=new CancellationTokenSource();cancelled.Cancel();try{Static("ReadCancellable",a,root,cancelled.Token);throw new Exception("Pre-cancelled reader accepted.");}catch(OperationCanceledException){checks++;}cancelled.Dispose();
            try{Static("ReadCancellable",bad,root,CancellationToken.None);throw new Exception("Invalid sample accepted.");}catch(InvalidDataException){checks++;}
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new Form{Width=1200,Height=800,ShowInTaskbar=false})using(var control=(Control)Activator.CreateInstance(type)){
                control.Dock=DockStyle.Fill;form.Controls.Add(control);form.Show();Pump();
                type.GetProperty("ProjectPath").SetValue(control,root,null);Pump();
                var gate=(SemaphoreSlim)Field(control,"overviewReaderGate");gate.Wait();
                var timer=Stopwatch.StartNew();var first=Begin(control,a);timer.Stop();
                Check(timer.ElapsedMilliseconds<1000,"Request dispatch is bounded independently of held reader gate.");Check(!first.IsCompleted&&Busy(control),"Pending request exposes immediate busy state.");
                Pump();Check(((Control)Field(control,"overviewLoadingView")).Visible,"Loading feedback paints inside content.");Check(((Control)Field(control,"overviewHost")).UseWaitCursor,"Wait cursor visible.");
                Check(!((Control)Field(control,"overviewOptions")).Enabled,"Threshold options disabled while source is loading.");
                int generation=(int)Field(control,"overviewLoadGeneration");Check(Object.ReferenceEquals(first,Begin(control,a)),"Repeated click shares the same pending request.");Check(generation==(int)Field(control,"overviewLoadGeneration"),"Dedup does not cancel or restart reader.");
                int beats=0;using(var heartbeat=new System.Windows.Forms.Timer{Interval=1}){heartbeat.Tick+=(s,e)=>beats++;heartbeat.Start();Pump();heartbeat.Stop();}Check(beats>0,"UI continues pumping while reader is queued.");
                gate.Release();Check(Complete(first),"Valid LOC publishes.");Idle(control);Check(Property<string>(control,"DisplayedLocationFilePath")==a,"Correct source path published.");Check(Property<int>(Field(control,"locationOverview"),"SampleCount")==64000,"Full unmodified source count published.");
                Check((int)Property<int>(control,"SourceRowCount")==0,"Preview does not create analyzed MAP rows.");
                gate.Wait();var cached=Begin(control,a);Check(cached.IsCompleted&&cached.Result,"Already loaded same file reuses current preview without waiting for disk gate.");gate.Release();Idle(control);
                gate.Wait();var old=Begin(control,b);Pump();var latest=Begin(control,a);Check(Complete(old)==false,"Superseded request is cancelled.");Check(Busy(control)&&!latest.IsCompleted,"Old completion cannot hide latest loading state.");gate.Release();Check(Complete(latest),"Latest selection wins.");Idle(control);
                gate.Wait();var explicitCancel=Begin(control,b);Pump();((CancellationTokenSource)Field(control,"overviewLoadCancellation")).Cancel();Check(!Complete(explicitCancel),"Cancellation reports no publication.");Idle(control);gate.Release();
                Check(Complete(Begin(control,b)),"Retry after cancellation succeeds.");Idle(control);
                gate.Wait();var reload=Begin(control,a);Pump();string next=Path.Combine(root,"new-project");Directory.CreateDirectory(next);type.GetProperty("ProjectPath").SetValue(control,next,null);Check(!Complete(reload),"Project reload cancels queued prior selection.");Idle(control);gate.Release();Check(Property<int>(Field(control,"locationOverview"),"SampleCount")==0,"Reload cannot retain prior source.");
                type.GetProperty("ProjectPath").SetValue(control,root,null);gate.Wait();var asyncOld=Begin(control,a);Pump();Check((bool)Call(control,"ShowLocationOverview",b),"Original synchronous interface remains functional.");Check(!Complete(asyncOld),"Synchronous publication cancels old async source.");gate.Release();Idle(control);Check(Property<string>(control,"DisplayedLocationFilePath")==b,"Sync source not overwritten.");
                Check(!Complete(Begin(control,bad)),"Invalid source gives failure without escaping UI.");Idle(control);Check(Property<int>(Field(control,"locationOverview"),"SampleCount")==0,"Failure does not show stale waveform.");Check(Property<string>(control,"LastError").Contains("Invalid LOC sample"),"Detailed diagnostic retained outside user text.");Check(!((string)Field(control,"operationStatus")).Contains("NaN"),"User-facing failure stays simple.");
                Check(Complete(Begin(control,a)),"Valid selection recovers after malformed input.");Idle(control);
                var tree=Property<object>(control,"AnalysisTree");Call(tree,"LoadRecords",new[]{a,b},new[]{0,1},new[]{13.4,13.4},new[]{1,2},new[]{1.0,2.0});
                gate.Wait();Call(tree,"SelectData",b);Pump();Check(Busy(control),"Actual tree selection routes through asynchronous busy path.");var treeTask=(Task<bool>)Field(control,"overviewLoadTask");gate.Release();Check(Complete(treeTask),"Actual tree selection publishes source.");Idle(control);
                gate.Wait();var dispose=Begin(control,a);Pump();control.Dispose();Check(!Complete(dispose),"Disposal cancels pending source without publication.");gate.Release();form.Close();
            }
            for(int i=0;i<paths.Length;i++)Check(Hash(paths[i])==before[i],"Synthetic source bytes unchanged.");
            Console.WriteLine("LOCATION_OVERVIEW_LOADING_PASS="+checks+"; SYNTHETIC_UI_ONLY=TRUE; PRODUCT_STARTED=FALSE; ALGORITHM_INVOKED=FALSE; WORKERS_STARTED=0; SDK_CALLED=FALSE; HARDWARE_USED=FALSE");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
