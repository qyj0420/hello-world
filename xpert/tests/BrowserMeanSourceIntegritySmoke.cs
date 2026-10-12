using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Runs only against generated projects under the supplied QA directory. No real
// settings, hardware, or user project is opened. Reflection uses the shipped DLL.
internal static class BrowserMeanSourceIntegritySmoke
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object value,string name)
    {var property=value.GetType().GetProperty(name,Flags);return property!=null?property.GetValue(value,null):value.GetType().GetField(name,Flags).GetValue(value);}
    static void Set(object value,string name,object content){value.GetType().GetField(name,Flags).SetValue(value,content);}
    static object Call(object value,string name,params object[] args)
    {return value.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static object Static(Type type,string name,params object[] args)
    {return type.GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(null,args);}
    static void Check(bool condition,string label){if(!condition)throw new InvalidOperationException(label);checks++;}
    static void Pump(Task task)
    {
        DateTime limit=DateTime.UtcNow.AddSeconds(30);
        while(!task.IsCompleted&&DateTime.UtcNow<limit){Application.DoEvents();Thread.Sleep(2);}
        Check(task.IsCompleted,"background operation finishes within the bounded wait");
    }
    static double[] Wave()
    {
        return Enumerable.Range(0,3501).Select(i=>
            Math.Exp(-Math.Pow((i-500)/3d,2))+.15*Math.Exp(-Math.Pow((i-900)/3d,2))+
            .3*Math.Exp(-Math.Pow((i-1500)/3d,2))+.1*Math.Exp(-Math.Pow((i-2500)/3d,2))).ToArray();
    }
    static string SaveFormal(Assembly assembly,string root,string level)
    {
        Type type=assembly.GetType("HVX.CalibrationStorage.LiveCalibrationRecord",true);
        object record=Activator.CreateInstance(type);
        Set(record,"Phase","A");Set(record,"Level",level);Set(record,"DeviceSerial","ISOLATED-MEAN-SMOKE");Set(record,"DeviceModel","synthetic");
        Set(record,"FilterId",assembly.GetType("HVX.SignalProcessing.PdLinearHighPass",true).GetField("FilterId",Flags).GetValue(null));
        Set(record,"ProjectHash",Static(type,"Identity",root));Set(record,"CreatedUtc",DateTime.UtcNow.ToString("o"));
        Set(record,"LengthM",1700d);Set(record,"VelocityMPerUs",170d);Set(record,"SampleRateHz",50e6);Set(record,"IncidentSeconds",10e-6);Set(record,"ReflectionSeconds",30e-6);
        Set(record,"CoefficientPcPerV",1d);Set(record,"ChargePc",1000d);Set(record,"SnrDb",40d);Set(record,"Correlation",.99);Set(record,"PulseCount",3);
        Set(record,"IncidentPeakIndex",500);Set(record,"ReflectionPeakIndex",1500);Set(record,"Average",Wave());
        Type rawType=assembly.GetType("HVX.CalibrationStorage.CalibrationRawReference",true);Array raw=Array.CreateInstance(rawType,3);
        for(int i=0;i<3;i++)
        {
            string relative="raw"+i+".bin",path=Path.Combine(root,relative);
            if(!File.Exists(path))File.WriteAllBytes(path,new byte[]{1,2,(byte)i});
            object reference=Activator.CreateInstance(rawType);Set(reference,"RelativePath",relative);Set(reference,"Sha256",Static(type,"HashFile",path));raw.SetValue(reference,i);
        }
        Set(record,"RawFiles",raw);return (string)Call(record,"Save",root);
    }
    static string SaveLegacy(string root,string level)
    {
        string path=Path.Combine(root,"A_"+level+".cal");
        var text=new StringBuilder("SR\t50000000\r\nCable length (m)\t1700\r\nV (m/usec)\t170\r\nStart_point\t0.00001\r\nFinal_point\t0.00003\r\n");
        for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");
        foreach(double value in Wave())text.Append(value.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));return path;
    }
    sealed class Fixture : IDisposable
    {
        internal readonly string Root,Item,Cable;
        internal readonly string[] Paths;
        internal readonly Control Browser;
        internal readonly Form Form;
        internal readonly byte[] CableBytes;
        readonly Assembly assembly;
        internal Fixture(Assembly assembly,string root,bool formal)
        {
            this.assembly=assembly;Root=root;Directory.CreateDirectory(root);Item=Path.Combine(root,"ITEM.in");Cable=Path.Combine(root,"CABLE.in");
            File.WriteAllText(Item,"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=1700\r\n");
            File.WriteAllText(Cable,"0\tA\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");CableBytes=File.ReadAllBytes(Cable);
            Paths=new[]{formal?SaveFormal(assembly,root,"1nC"):SaveLegacy(root,"1nC"),formal?SaveFormal(assembly,root,"2nC"):SaveLegacy(root,"2nC")};
            Form=new Form{ClientSize=new Size(1300,800),ShowInTaskbar=false};
            Browser=(Control)Activator.CreateInstance(assembly.GetType("HVX.Controls.ProjectDataBrowserControl",true));Browser.Dock=DockStyle.Fill;Form.Controls.Add(Browser);Form.Show();
            Check((bool)Call(Browser,"LoadProject",root),"isolated project loads");((CheckBox)Get(Browser,"comparisonAverage")).Checked=true;
        }
        internal void Mean()
        {
            var task=(Task<bool>)Call(Browser,"CompareCalibrationsAsync",Paths,true,false);Pump(task);
            Check(task.GetAwaiter().GetResult(),"valid sources produce a mean");
            Check(((Array)Get(Browser,"comparisonJointCandidates")).Length==0,"recomputed mean drops old candidates");
        }
        internal bool Find(){var task=(Task<bool>)Call(Browser,"FindComparisonJointsAsync");Pump(task);return task.GetAwaiter().GetResult();}
        internal object Plan()
        {return Call(Static(assembly.GetType("HVX.ProjectCables.CableJointStore",true),"Read",Root),"Prepare",new[]{"A"},680d,"接头 1","冷缩","XLPE","XLPE");}
        internal void Commit(object plan)
        {Call(Browser,"CommitJointDialog",plan,"AddJoint",Root,(int)Get(Browser,"projectGeneration"),(int)Get(Browser,"selectionGeneration"));}
        internal void RejectCommit(object plan,string label)
        {
            Exception failure=null;try{Commit(plan);}catch(TargetInvocationException ex){failure=ex.InnerException;}
            Check(failure is InvalidDataException||failure is IOException||failure is InvalidOperationException||failure is UnauthorizedAccessException,label+": commit synchronously reports invalid source ("+(failure==null?"no exception":failure.GetType().Name)+")");
            Check(!String.IsNullOrWhiteSpace(failure.Message),label+": failure explains refusal");NoWrite(label);
        }
        internal void RejectFind(string label)
        {
            Check(!Find(),label+": source-invalid search does not succeed");
            Check(((Array)Get(Browser,"comparisonJointCandidates")).Length==0,label+": stale candidates cleared");
            Check(!String.IsNullOrWhiteSpace((string)Get(Browser,"comparisonJointSearchMessage")),label+": visible failure reason retained");NoWrite(label);
        }
        internal void NoWrite(string label)
        {
            Check(!(bool)Get(Browser,"IsBusy"),label+": exits busy state");
            Check(((Control)Get(Browser,"tree")).Enabled&&((Control)Get(Browser,"actions")).Enabled,label+": navigation/editor controls remain available");
            Check(CableBytes.SequenceEqual(File.ReadAllBytes(Cable)),label+": exact CABLE bytes unchanged");
            Check(Directory.GetFiles(Root,"CABLE.before-joint-*.bak").Length==0,label+": no misleading commit backup");
        }
        internal void RewriteGeneratedCalibration(string path,byte[] bytes)
        {
            string full=Path.GetFullPath(path),root=Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!Paths.Any(p=>String.Equals(Path.GetFullPath(p),full,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Corruption fixture must belong to this generated QA project.");
            FileAttributes original=File.GetAttributes(full);
            if((original&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked fixtures cannot be rewritten.");
            try
            {
                if((original&FileAttributes.ReadOnly)!=0)File.SetAttributes(full,original&~FileAttributes.ReadOnly);
                File.WriteAllBytes(full,bytes);
            }
            finally{File.SetAttributes(full,original);}
            Check(File.GetAttributes(full)==original,"generated corruption fixture retains its original file attributes");
        }
        public void Dispose(){Form.Close();Browser.Dispose();Form.Dispose();}
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]);string qa=Path.Combine(Path.GetFullPath(args[1]),"mean-integrity-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var f=new Fixture(assembly,Path.Combine(qa,"formal"),true))
            {
                f.Mean();Check(f.Find(),"intact formal sources can be searched");Check(((Array)Get(f.Browser,"comparisonJointCandidates")).Length==1,"formal positive fixture yields one interior candidate");
                object plan=f.Plan();string raw=Path.Combine(f.Root,"raw0.bin");byte[] rawBytes=File.ReadAllBytes(raw),mainBytes=File.ReadAllBytes(f.Paths[0]);
                File.WriteAllBytes(raw,new byte[]{9,9,9});
                Check(mainBytes.SequenceEqual(File.ReadAllBytes(f.Paths[0])),"external raw edit leaves formal archive bytes unchanged");
                f.RejectFind("formal raw dependency modified");f.RejectCommit(plan,"formal raw dependency modified");File.WriteAllBytes(raw,rawBytes);
                f.Mean();Check(f.Find(),"restored formal source can be freshly averaged and searched");plan=f.Plan();
                byte[] corrupt=(byte[])mainBytes.Clone();corrupt[corrupt.Length-1]^=1;f.RewriteGeneratedCalibration(f.Paths[0],corrupt);
                f.RejectCommit(plan,"formal main file changed after candidates");f.RejectFind("formal main file changed");f.RewriteGeneratedCalibration(f.Paths[0],mainBytes);
                f.Mean();plan=f.Plan();byte[] itemBytes=File.ReadAllBytes(f.Item);File.AppendAllText(f.Item,"备注=external edit\r\n");
                f.RejectFind("formal project identity changed");f.RejectCommit(plan,"project changed after plan");File.WriteAllBytes(f.Item,itemBytes);
                f.Mean();File.Delete(raw);f.RejectFind("formal raw dependency missing");File.WriteAllBytes(raw,rawBytes);
                f.Mean();using(var locked=new FileStream(raw,FileMode.Open,FileAccess.ReadWrite,FileShare.None))f.RejectFind("formal raw dependency unreadable");
                f.Mean();var cancelled=(Task<bool>)Call(f.Browser,"FindComparisonJointsAsync");((CancellationTokenSource)Get(f.Browser,"waveCancellation")).Cancel();Pump(cancelled);
                Check(!cancelled.GetAwaiter().GetResult(),"cancelled search never publishes a successful result");Check(((Array)Get(f.Browser,"comparisonJointCandidates")).Length==0,"cancelled search leaves no candidates");f.NoWrite("cancelled search");
                f.Mean();Check(f.Find(),"new request recovers after cancellation");
                Check(rawBytes.SequenceEqual(File.ReadAllBytes(raw))&&mainBytes.SequenceEqual(File.ReadAllBytes(f.Paths[0]))&&itemBytes.SequenceEqual(File.ReadAllBytes(f.Item)),"formal fixture sources fully restored after negative checks");
            }
            using(var f=new Fixture(assembly,Path.Combine(qa,"legacy"),false))
            {
                f.Mean();Check(f.Find(),"intact legacy calibrations can be searched");object plan=f.Plan();byte[] sourceBytes=File.ReadAllBytes(f.Paths[0]);
                File.AppendAllText(f.Paths[0],"0\r\n");f.RejectCommit(plan,"legacy source changed after candidates");f.RejectFind("legacy calibration content changed");File.WriteAllBytes(f.Paths[0],sourceBytes);
                f.Mean();File.Delete(f.Paths[0]);f.RejectFind("legacy calibration removed");File.WriteAllBytes(f.Paths[0],sourceBytes);
                f.Mean();using(var locked=new FileStream(f.Paths[0],FileMode.Open,FileAccess.ReadWrite,FileShare.None))f.RejectFind("legacy calibration unreadable");
                f.Mean();Check(f.Find(),"legacy source recovers after transient failures");f.Commit(f.Plan());
                Check((int)Get(f.Browser,"LastChangedFileCount")==1,"fresh confirmed mean still permits an explicit joint save");
                string[] backups=Directory.GetFiles(f.Root,"CABLE.before-joint-*.bak");Check(backups.Length==1&&f.CableBytes.SequenceEqual(File.ReadAllBytes(backups[0])),"successful save has one exact recoverable cable backup");
                Check(!f.CableBytes.SequenceEqual(File.ReadAllBytes(f.Cable)),"positive save updates cable structure");Check(sourceBytes.SequenceEqual(File.ReadAllBytes(f.Paths[0])),"positive metadata save never rewrites calibration waveform");
            }
            Console.WriteLine("BROWSER_MEAN_SOURCE_INTEGRITY_PASS="+checks);Console.WriteLine("QA="+qa);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
