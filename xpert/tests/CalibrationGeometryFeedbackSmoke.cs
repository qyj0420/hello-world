using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationGeometryFeedbackSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,All).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    static void Near(double value,double expected,string label){Check(Math.Abs(value-expected)<1e-10,label);}
    static bool Finish(Task<bool> t){DateTime until=DateTime.UtcNow.AddSeconds(30);while(!t.IsCompleted&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(t.IsCompleted,"background operation completed");return t.GetAwaiter().GetResult();}
    static void NoComparisonEvidence(Control c,string label)
    {
        Check(Get(c,"comparisonGeometry")==null&&Get(c,"comparisonMeanX")==null&&Get(c,"comparisonMeanY")==null,label+": no retained mean or geometry");
        Check(((IEnumerable)Get(c,"comparisonJointCandidates")).Cast<object>().Count()==0,label+": no retained candidates");
        Check(((IDictionary)Get(c,"comparisonMeanSourceHashes")).Count==0,label+": no retained source signatures");
        Check(!(bool)Get(Get(c,"plot"),"HasCableOverlay"),label+": no retained distance overlay");
        Check(((IEnumerable)Get(Get(c,"plot"),"series")).Cast<object>().Count()==0,label+": no retained plotted mean");
        Check(!((Control)Get(c,"findComparisonJointsButton")).Enabled,label+": finder remains disabled");
    }
    static string N(double value){return value.ToString("R",CultureInfo.InvariantCulture);}
    static void Cal(string path,string format,double length=1700,double velocity=170,double amplitude=1,int terminal=1500)
    {
        bool alias=format=="alias"||format=="bad-canonical"||format=="unknown-unit"||format=="bad-legacy-velocity"||format=="bad-length";
        string[] header=alias?new[]{"Cable length\t"+N(length),"Cal.value\t1000",(format=="unknown-unit"?"Velocity (m/s)":"Velocity (m/us)")+"\t"+N(velocity),"NI_Calib_factor\t1","Start_point (s)\t0.00001","Final_point (s)\t"+N(terminal/50e6),"SR\t50000000","Calib_Attenuation\t4.4","Phase\tALL","Units\tV","Reserved\t0",format=="bad-canonical"?"V (m/usec)\tinvalid":"Reserved2\t0","Data"}:
            new[]{(format=="legacy"?"Delta_x":"SR")+"\t50000000","Cable length (m)\t"+N(length),(format=="legacy"?"Velocity (m/usec)":"V (m/usec)")+"\t"+N(velocity),"Start_point\t0.00001","Final_point\t"+N(terminal/50e6),"NI_Calib_factor\t1","Reserved\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Reserved6\t0","Data"};
        if(format=="bad-legacy-velocity"){header[2]="Velocity (m/us)\t171";header[10]="Velocity (m/usec)\t"+N(velocity);header[11]="V (m/usec)\tinvalid";}
        if(format=="bad-length")header[11]="Cable length (m)\tinvalid";
        if(format=="bad-sr"||format=="bad-sr-unit")
        {header[0]=(format=="bad-sr"?"SR":"SR (Hz)")+"\tinvalid";header[2]="Velocity (m/usec)\t"+N(velocity);header[6]="Delta_x\t50000000";}
        var text=new StringBuilder(String.Join("\r\n",header)+"\r\n");
        for(int i=0;i<3501;i++)text.Append(N(amplitude*(.001*Math.Sin(i*.72)+Math.Exp(-Math.Pow((i-500)/3d,2))+.15*Math.Exp(-Math.Pow((i-900)/3d,2))+(terminal>=0?.3*Math.Exp(-Math.Pow((i-terminal)/3d,2)):0)))).Append("\r\n");
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));
    }
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string qa=Path.Combine(Path.GetFullPath(args[1]),"geometry-feedback-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
        File.WriteAllText(Path.Combine(qa,"ITEM.in"),"[ITEM]\r\n电缆芯数=一体三芯\r\n电缆长度=1700\r\n");
        string cable=Path.Combine(qa,"CABLE.in");File.WriteAllText(cable,"0\tALL\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");byte[] cableBefore=File.ReadAllBytes(cable);
        string[] paths=new[]{"ALL_100nC.cal","ALL_150nC.cal","ALL_200nC.cal","ALL_5nC.cal"}.Select(n=>Path.Combine(qa,n)).ToArray();
        for(int i=0;i<paths.Length;i++)Cal(paths[i],i==1?"legacy":i==2?"alias":"normal",amplitude:i+1);
        byte[][] originals=paths.Select(File.ReadAllBytes).ToArray();
        var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
        using(var form=new Form{Width=1250,Height=850,ShowInTaskbar=false})using(var c=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true))){
            form.Controls.Add(c);c.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            Action reload=()=>{Check((bool)Call(c,"LoadProject",qa),"isolated fixture loads");((CheckBox)Get(c,"comparisonAverage")).Checked=true;};
            Func<string[],bool,bool> mean=(selected,norm)=>Finish((Task<bool>)Call(c,"CompareCalibrationsAsync",selected,norm,true));
            Func<string,object> record=path=>((IEnumerable)Get(Get(c,"project"),"Calibrations")).Cast<object>().Single(r=>(string)Get(r,"Path")==path);
            reload();foreach(string path in paths){Near((double)Get(record(path),"CableLengthMetres"),1700,"exact metres read for each supported format");Near((double)Get(record(path),"VelocityMPerUs"),170,"explicit velocity unit read for each supported format");Near((double)Get(record(path),"SampleRateHz"),50e6,"actual sample rate preserved");}
            Check(mean(paths,true),"four amplitudes and three supported formats normalize and average");Check(Finish((Task<bool>)Call(c,"FindComparisonJointsAsync")),"strong real terminal permits screening");Check(((IEnumerable)Get(c,"comparisonJointCandidates")).Cast<object>().Count()==1,"one real interior candidate only");
            string missing=Path.Combine(qa,"missing.cal");
            Check(!mean(new[]{missing},true),"invalid selected path refuses a new comparison intent");NoComparisonEvidence(c,"invalid selected path after successful mean and finder");
            Check(!(bool)Get(c,"IsBusy"),"invalid comparison exits its owned busy state");
            Check(mean(paths,true),"valid comparison recovers after invalid selection");
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var pending=(Task<bool>)Call(c,"CompareCalibrationsAsync",paths,true,true);
            Check(!pending.IsCompleted,"mean continuation is deterministically pending before the UI queue is pumped");
            var staleToken=((CancellationTokenSource)Get(c,"waveCancellation")).Token;int staleGeneration=(int)Get(c,"selectionGeneration");
            Check(!mean(new[]{missing},true),"invalid request supersedes pending comparison");
            Check(staleToken.IsCancellationRequested&&(int)Get(c,"selectionGeneration")>staleGeneration,"invalid request cancels and invalidates the previous asynchronous context");
            NoComparisonEvidence(c,"invalid request while mean is pending");
            Check(!Finish(pending),"late mean result reports refusal instead of installing its old result");
            NoComparisonEvidence(c,"late continuation after invalid request");
            Check(((string)Get(c,"lastError")).Contains("请选择")||((string)Get(c,"lastError")).Contains("Choose valid"),"old cancellation cannot overwrite the invalid-selection diagnostic");
            Check(mean(paths,true),"valid comparison still recovers after the stale continuation finishes");
            Check(mean(paths,false),"raw amplitudes still average");for(int i=0;i<paths.Length;i++)Check(originals[i].SequenceEqual(File.ReadAllBytes(paths[i])),"display preserves exact source bytes");
            Cal(paths[3],"normal",length:1700.1);reload();Check(!mean(paths,true),"0.1m length difference remains refused");string error=(string)Get(c,"lastError");Check(error.Contains("length tolerance")&&error.Contains("velocity tolerance")&&error.Contains("1700.1")&&error.Contains("ΔL="),"length difference and unchanged tolerances explained");foreach(string path in paths)Check(error.Contains(Path.GetFileNameWithoutExtension(path)),"all chosen record names included");Check(Get(c,"comparisonGeometry")==null&&Get(c,"comparisonMeanY")==null,"refused mean never retains stale geometry or waveform");
            Cal(paths[3],"normal",velocity:170.001);reload();Check(!mean(paths,true),"0.001m/us speed difference remains refused");error=(string)Get(c,"lastError");Check(error.Contains("170.001")&&error.Contains("Δv="),"actual speed difference explained");
            Cal(paths[3],"normal",terminal:1600);reload();Check(mean(new[]{paths[3]},true),"single source with mismatched saved cursor may be viewed");Check(!Finish((Task<bool>)Call(c,"FindComparisonJointsAsync")),"outside-prediction real reflection still refused");error=(string)Get(c,"lastError");Check(error.Contains("No verifiable terminal reflection")&&error.Contains("predicted terminal = 20")&&error.Contains("checked window")&&error.Contains("required threshold"),"terminal rejection includes real checked bounds and threshold");Check(error.Contains("ALL_5nC")&&error.Contains("saved reflection cursor=32")&&error.Contains("not detected reflections"),"source cursor is identified as metadata not verified evidence");Check(((IEnumerable)Get(c,"comparisonJointCandidates")).Cast<object>().Count()==0,"no synthetic terminal or candidates");
            Cal(paths[3],"unknown-unit");reload();Check(Double.IsNaN((double)Get(record(paths[3]),"VelocityMPerUs")),"m/s does not silently become m/us");Check(!mean(new[]{paths[3]},true),"unknown units refuse mean geometry");Check(((string)Get(c,"lastError")).Contains("unknown/invalid"),"missing geometry distinguished from numerical conflict");
            Cal(paths[3],"bad-canonical");reload();Check(Double.IsNaN((double)Get(record(paths[3]),"VelocityMPerUs")),"valid alias cannot conceal invalid existing canonical field");Check(!mean(new[]{paths[3]},true),"invalid existing geometry remains refused");
            Cal(paths[3],"bad-legacy-velocity");reload();Check(Double.IsNaN((double)Get(record(paths[3]),"VelocityMPerUs")),"neither historical nor new velocity alias conceals invalid V (m/usec)");Check(!mean(new[]{paths[3]},true),"bad primary velocity with two valid aliases refuses averaging");
            Cal(paths[3],"bad-length");reload();Check(Double.IsNaN((double)Get(record(paths[3]),"CableLengthMetres")),"valid exact metre alias cannot conceal invalid primary cable length");Check(!mean(new[]{paths[3]},true),"bad primary length with valid new alias refuses averaging");
            foreach(string format in new[]{"bad-sr","bad-sr-unit"})
            {Cal(paths[3],format);reload();Check(Double.IsNaN((double)Get(record(paths[3]),"SampleRateHz")),format+": legacy Delta_x cannot conceal invalid recognized SR");Check(!(bool)Get(record(paths[3]),"IsValid"),format+": invalid rate remains an invalid calibration record");Check(!mean(new[]{paths[3]},true),format+": invalid rate refuses averaging");}
            Cal(paths[3],"alias");reload();Check(mean(new[]{paths[3]},true),"exact new header aliases support single mean");Check(Finish((Task<bool>)Call(c,"FindComparisonJointsAsync")),"new aliases preserve genuine terminal location");
            Check(cableBefore.SequenceEqual(File.ReadAllBytes(cable)),"search and refusal never write project geometry");Check(Directory.GetFiles(qa,"*.bak").Length==0,"no write transaction invoked");form.Close();
        }
        Console.WriteLine("CALIBRATION_GEOMETRY_FEEDBACK_PASS="+checks);Console.WriteLine("QA="+qa);return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
