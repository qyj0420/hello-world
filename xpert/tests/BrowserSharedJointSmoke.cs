using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BrowserSharedJointSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object target,string name){var t=target.GetType();var p=t.GetProperty(name,All);return p!=null?p.GetValue(target,null):t.GetField(name,All).GetValue(target);}
    static void Set(object target,string name,object value){var t=target.GetType();var p=t.GetProperty(name,All);if(p!=null)p.SetValue(target,value,null);else t.GetField(name,All).SetValue(target,value);}
    static object Call(object target,string name,params object[] values){return target.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(target,values);}
    static void Check(bool ok,string description){checks++;if(!ok)throw new InvalidOperationException(description);}
    static void Pump(Func<bool> ready){DateTime until=DateTime.UtcNow.AddSeconds(35);while(!ready()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"Asynchronous operation finished");Application.DoEvents();}
    static object Read(Assembly module,string root){return module.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Read",All).Invoke(null,new object[]{root});}
    static object Plan(Assembly module,string root,string[] phases,double position,string name){return Call(Read(module,root),"Prepare",phases,position,name,"冷缩接头","XLPE-before","Oil-paper-after");}
    static bool Save(Control browser,object plan,string root,int? generation=null,int? selection=null){return (bool)Call(browser,"SaveJointPlan",plan,root,generation??(int)Get(browser,"projectGeneration"),selection??(int)Get(browser,"selectionGeneration"));}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and QA output.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);string root=Path.Combine(output,"joint-fixture-"+Guid.NewGuid().ToString("N"));string[] files=Fixture(root);
            var module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            VerifyCancellableHash(module,root,files[0]);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            byte[] item=File.ReadAllBytes(Path.Combine(root,"ITEM.in")),cable=File.ReadAllBytes(Path.Combine(root,"CABLE.in"));var waveBytes=files.Select(File.ReadAllBytes).ToArray();
            using(var host=new Form{ClientSize=new Size(1400,850),ShowInTaskbar=false})
            using(var browser=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                browser.Dock=DockStyle.Fill;host.Controls.Add(browser);host.Show();Application.DoEvents();
                Check((bool)Call(browser,"LoadProject",root),"Fixture project loads");Check((bool)Call(browser,"SelectFile",files[0]),"A calibration selected");Pump(()=>!(bool)Get(browser,"IsBusy"));
                bool dialogSeen=false;Form jointDialog=null;Exception dialogError=null;
                using(var timer=new System.Windows.Forms.Timer{Interval=20})
                {
                    timer.Tick+=delegate {
                        var dialog=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.Name=="CableJointDialog"&&f.Visible);if(dialog==null)return;
                        jointDialog=dialog;dialogSeen=true;timer.Stop();
                        try{Check(dialog.StartPosition==FormStartPosition.CenterParent,"Shared dialog starts centered");Check(dialog.Controls.Find("JointMaterialBefore",true).Length==1&&dialog.Controls.Find("JointMaterialAfter",true).Length==1,"Shared dialog exposes both materials");}
                        catch(Exception ex){dialogError=ex;}
                        finally{dialog.DialogResult=DialogResult.Cancel;dialog.Close();}
                    };
                    timer.Start();Call(browser,"ShowJoints",33.25);
                    // ShowJoints now awaits evidence validation before entering its
                    // modal loop. Keep the observer alive until the real window is
                    // closed and disposed by that continuation, with a bounded pump.
                    Pump(()=>dialogSeen&&jointDialog.IsDisposed&&!(bool)Get(browser,"IsBusy"));
                    if(dialogError!=null)throw dialogError;
                }
                Check(dialogSeen,"Browser opens shared joint dialog");Check(cable.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"Cancel leaves cable unchanged");
                var plan=Plan(module,root,new[]{"A","C"},42.125,"J-custom");
                Check(!Save(browser,plan,root,(int)Get(browser,"projectGeneration")-1),"Stale project generation rejected before queue");
                Check(!Save(browser,plan,root,null,(int)Get(browser,"selectionGeneration")-1),"Stale selection rejected before queue");
                Set(browser,"SelectedNodeKind","cal-comparison");Check(!Save(browser,plan,root),"Comparison cannot write through a previously selected calibration");Call(browser,"SelectFile",files[0]);Pump(()=>!(bool)Get(browser,"IsBusy"));
                int savedEvents=0;module.GetType("HVX.Controls.ProjectDataBrowserControl").GetEvent("ProjectSaved").AddEventHandler(browser,new EventHandler((s,e)=>savedEvents++));
                Check(Save(browser,plan,root),"Shared plan queues save");Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(savedEvents==1,"Save refresh emits one project-saved event");Check((string)Get(browser,"SelectedFilePath")==files[0],"Save refresh retains selected waveform");Check((int)Get(browser,"LastChangedFileCount")==1,"Exactly CABLE is reported changed");
                Check(Directory.Exists((string)Get(browser,"LastBackupDirectory")),"Backup location exists");var backups=Directory.GetFiles(root,"CABLE.before-joint-*.bak");Check(backups.Length==1&&cable.SequenceEqual(File.ReadAllBytes(backups[0])),"Exact original CABLE backup retained");
                string modified=File.ReadAllText(Path.Combine(root,"CABLE.in"));Check(modified.Split(new[]{"1\tJ-custom\t冷缩接头\t42.125m"},StringSplitOptions.None).Length-1==3,"Both duplicate A blocks and selected C updated");
                var snapshot=Read(module,root);var blocks=((IEnumerable)Get(snapshot,"Blocks")).Cast<object>().ToArray();Check(blocks.Count(b=>(string)Get(b,"Phase")=="A")==2,"Duplicate A labels retained");Check(!((IEnumerable)Get(blocks.First(b=>(string)Get(b,"Phase")=="B"),"Positions")).Cast<double>().Contains(42.125),"Unselected B unchanged");
                object project=Get(browser,"project");Check(((double[])Get(project,"JointPositionsMetres")).Contains(42.125),"Custom joint name detected structurally after refresh");
                Check(item.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"ITEM.in"))),"ITEM bytes unchanged");Check(files.Select((f,i)=>File.ReadAllBytes(f).SequenceEqual(waveBytes[i])).All(x=>x),"All calibration bytes unchanged");
                var stale=Plan(module,root,new[]{"B"},67,"J-stale");File.AppendAllText(Path.Combine(root,"ITEM.in"),"备注: = \"external\"\r\n");byte[] safe=File.ReadAllBytes(Path.Combine(root,"CABLE.in"));
                Check(Save(browser,stale,root),"Concurrent edit queues guarded transaction");Pump(()=>!(bool)Get(browser,"IsBusy"));Check(!String.IsNullOrEmpty((string)Get(browser,"LastError")),"Concurrent ITEM change is reported");Check(safe.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"CABLE.in"))),"Concurrent edit writes no cable bytes");
                var comparison=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,true);Pump(()=>comparison.IsCompleted);Check(comparison.Result,"All project calibration records compare successfully: "+Get(browser,"LastError"));Check((string)Get(browser,"SelectedNodeKind")=="cal-comparison","Comparison selection explicit");
                var plot=(Control)Get(browser,"plot");var series=((IEnumerable)Get(plot,"series")).Cast<object>().ToArray();Check(series.Length==12,"All twelve records displayed with no eight-record cap");
                Check(series.All(s=>Math.Abs(((double[])Get(s,"Y")).Max(v=>Math.Abs(v))-1)<1e-10),"Complete input peak normalization retained by preview");Check(series.All(s=>((double[])Get(s,"X")).Length<=8192),"Display copies are bounded by peak-preserving reduction");
                Check(series.Select(s=>((Color)Get(s,"Color")).ToArgb()).Distinct().Count()==12,"Twelve distinguishable line colors");
                Check(!(bool)Call(browser,"BeginCalibrationEdit"),"Comparison cannot edit stale single-record calibration");
                host.ClientSize=new Size(800,500);Application.DoEvents();var plotArea=(Rectangle)Get(plot,"Plot");Check(plotArea.Height>=20&&plotArea.Bottom<plot.Height,"Paged legend preserves visible plotting area");var measured=Get(plot,"CurrentLayout");Check((int)Get(measured,"Pages")>1,"Large legend pages at small width");
                var legend=(Rectangle)Get(measured,"Legend");int page=(int)Get(plot,"legendPage");plot.GetType().GetMethod("OnMouseWheel",All).Invoke(plot,new object[]{new MouseEventArgs(MouseButtons.None,0,legend.Left+5,legend.Top+5,-120)});Check((int)Get(plot,"legendPage")>page,"Legend wheel advances page without dropping waveforms");
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(output,"all-calibrations-comparison-small.png"));}
                host.ClientSize=new Size(1400,850);Application.DoEvents();using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(output,"all-calibrations-comparison.png"));}
                Check(files.Select((f,i)=>File.ReadAllBytes(f).SequenceEqual(waveBytes[i])).All(x=>x),"Comparison leaves all raw/calibration files identical");
                var list=(CheckedListBox)Get(browser,"comparisonRecordsList");var search=(TextBox)Get(browser,"comparisonSearch");var normal=(CheckBox)Get(browser,"comparisonNormalize");
                Check(((TreeView)Get(browser,"tree")).Nodes[1].Nodes.Cast<TreeNode>().Any(n=>n.Name=="cal-comparison"),"Tree has all-calibration entry for multiple records");
                Check(((Panel)Get(browser,"comparisonOptions")).Visible&&list.Visible,"Checkbox selector embedded beside waveform");Check(normal.Checked,"Peak normalization defaults checked");
                search.Text="12nC";Application.DoEvents();Check(list.Items.Count==1,"Search filters embedded list");Check(((IEnumerable)Get(browser,"comparisonSelected")).Cast<string>().Count()==12,"Search preserves hidden selections");
                list.SetItemChecked(0,false);Pump(()=>!(bool)Get(browser,"IsBusy")&&!((System.Windows.Forms.Timer)Get(browser,"comparisonDebounce")).Enabled);Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==11,"Unchecking one curve removes only that curve");
                search.Text="";Application.DoEvents();Check(list.Items.Count==12&&list.CheckedItems.Count==11,"Clearing search restores list without losing checked states");
                var hide=(Button)Get(browser,"comparisonFold");hide.PerformClick();Check(!((Panel)Get(browser,"comparisonOptions")).Visible,"List folds without leaving comparison");hide.PerformClick();Check(((Panel)Get(browser,"comparisonOptions")).Visible,"List reopens without losing selection");
                normal.Checked=false;Pump(()=>!(bool)Get(browser,"IsBusy")&&!((System.Windows.Forms.Timer)Get(browser,"comparisonDebounce")).Enabled);series=((IEnumerable)Get(plot,"series")).Cast<object>().ToArray();Check(series.Any(s=>((double[])Get(s,"Y")).Max()>2),"Embedded normalization toggle restores original amplitude");
                Check(((IDictionary)Get(browser,"comparisonCache")).Count==12,"Display preview cache retains all records, not raw full arrays");
                var clear=browser.Controls.Find("ComparisonClearAll",true).OfType<Button>().Single();clear.PerformClick();Application.DoEvents();Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==0,"Clear all empties plotted curves");
                list.SetItemChecked(0,true);Pump(()=>!(bool)Get(browser,"IsBusy")&&!((System.Windows.Forms.Timer)Get(browser,"comparisonDebounce")).Enabled);Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==1,"Single selected calibration remains displayable");
                list.SetItemChecked(1,true);list.SetItemChecked(2,true);list.SetItemChecked(1,false);Pump(()=>!(bool)Get(browser,"IsBusy")&&!((System.Windows.Forms.Timer)Get(browser,"comparisonDebounce")).Enabled);Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==2,"Rapid checkbox changes publish only final selection");
                Check(files.Select((f,i)=>File.ReadAllBytes(f).SequenceEqual(waveBytes[i])).All(x=>x),"All embedded view toggles preserve input files");
                Call(browser,"SelectFile",files[0]);Pump(()=>!(bool)Get(browser,"IsBusy"));
                var cancelled=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,true);((CancellationTokenSource)Get(browser,"waveCancellation")).Cancel();Pump(()=>cancelled.IsCompleted);Check(!cancelled.Result,"Comparison read cancels");Check(!(bool)Get(browser,"IsBusy"),"Cancel releases busy state");
                var origin=(Task<bool>)Call(browser,"CompareCalibrationsAsync",new[]{files[0],files[1]},false,false);Pump(()=>origin.IsCompleted);Check(origin.Result,"Optional unnormalized unaligned comparison succeeds");series=((IEnumerable)Get(plot,"series")).Cast<object>().ToArray();Check(((double[])Get(series[1],"Y")).Max()>2,"Unnormalized amplitudes remain in original voltage units");
                var invalid=(Task<bool>)Call(browser,"CompareCalibrationsAsync",new[]{files[0],Path.Combine(root,"missing.cal")},true,false);Pump(()=>invalid.IsCompleted);Check(!invalid.Result,"Foreign/missing records cannot be compared as project calibration");
                File.WriteAllText(files[0],"corrupt calibration",new UTF8Encoding(false));
                var partial=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(()=>partial.IsCompleted);Check(partial.Result,"One damaged record does not block valid calibration curves");Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==11,"Damaged record is isolated from eleven valid curves");Check(((string)Get(browser,"LastError")).Contains(Path.GetFileNameWithoutExtension(files[0])),"Damaged filename explicitly reported");
                var badOnly=(Task<bool>)Call(browser,"CompareCalibrationsAsync",new[]{files[0]},true,false);Pump(()=>badOnly.IsCompleted);Check(!badOnly.Result,"All-invalid selection reports failure");Check(((IEnumerable)Get(plot,"series")).Cast<object>().Count()==0,"Failed selection cannot leave old curves under new options");
                File.WriteAllBytes(files[0],waveBytes[0]);
                var restored=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(()=>restored.IsCompleted);Check(restored.Result&&((IEnumerable)Get(plot,"series")).Cast<object>().Count()==12,"SHA256 invalidation reloads restored calibration");
                var switching=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Call(browser,"SelectFile",files[1]);Pump(()=>switching.IsCompleted&&!(bool)Get(browser,"IsBusy"));Check(!switching.Result&&(string)Get(browser,"SelectedFilePath")==files[1],"Old comparison completion cannot override a new record selection");
                host.Close();
            }
            Console.WriteLine("BROWSER_SHARED_JOINT_AND_ALL_COMPARISON_PASS="+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlapped);
    static void VerifyCancellableHash(Assembly module,string root,string source)
    {
        var type=module.GetType("HVX.Controls.ProjectDataBrowserControl",true);
        var hash=type.GetMethod("ComparisonSourceHash",All);var streamHash=type.GetMethod("ComparisonStreamHash",All);
        using(var sha=SHA256.Create())Check((string)hash.Invoke(null,new object[]{source,CancellationToken.None})==Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(source))),"Chunked comparison SHA256 matches complete source bytes");
        string huge=Path.Combine(root,"oversize-source.bin");
        using(var stream=new FileStream(huge,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
        {
            uint returned;Check(DeviceIoControl(stream.SafeFileHandle,0x900C4,IntPtr.Zero,0,IntPtr.Zero,0,out returned,IntPtr.Zero),"QA oversize fixture marked sparse");stream.SetLength(512L*1024*1024+1);stream.Flush();
            Exception failure=null;try{hash.Invoke(null,new object[]{huge,CancellationToken.None});}catch(TargetInvocationException ex){failure=ex.InnerException;}
            Check(failure is InvalidDataException&&failure.Message.Contains("512"),"Oversize sparse file rejected before opening/hash even while exclusively locked");
        }
        using(var cancellation=new CancellationTokenSource())
        using(var stream=new CancellingReadStream(source,cancellation))
        {
            Exception failure=null;try{streamHash.Invoke(null,new object[]{stream,cancellation.Token});}catch(TargetInvocationException ex){failure=ex.InnerException;}
            Check(failure is OperationCanceledException,"Cancellation during first hash chunk stops comparison hashing");Check(stream.ReadCalls==1&&stream.ReadBytes<=65536,"Cancelled hash reads at most one 64-KiB chunk");
        }
        using(var cancellation=new CancellationTokenSource())
        {
            cancellation.Cancel();Exception failure=null;try{hash.Invoke(null,new object[]{Path.Combine(root,"not-created.bin"),cancellation.Token});}catch(TargetInvocationException ex){failure=ex.InnerException;}
            Check(failure is OperationCanceledException,"Pre-cancelled hash performs no source-file probe");
        }
    }
    sealed class CancellingReadStream:FileStream
    {
        readonly CancellationTokenSource cancellation;internal int ReadCalls,ReadBytes;
        internal CancellingReadStream(string source,CancellationTokenSource cancellation):base(source,FileMode.Open,FileAccess.Read,FileShare.Read){this.cancellation=cancellation;}
        public override int Read(byte[] buffer,int offset,int count){int read=base.Read(buffer,offset,count);ReadCalls++;ReadBytes+=read;cancellation.Cancel();return read;}
    }
    static string[] Fixture(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆芯数: = \"单芯三相\"\r\n电缆长度: = \"170\"\r\n",new UTF8Encoding(false));
        var cable=new StringBuilder();foreach(string phase in new[]{"A","A","B","C"})cable.Append("0\t").Append(phase).Append("\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t170m\r\n");File.WriteAllText(Path.Combine(root,"CABLE.in"),cable.ToString(),new UTF8Encoding(false));
        return Enumerable.Range(0,12).Select(number=>{var content=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");for(int i=0;i<8;i++)content.Append("Field").Append(i).Append("\t0\r\n");for(int i=0;i<12001;i++){double t=i*.01;double y=.2+(number+1)*(Math.Max(0,1-Math.Abs(t-1)/.1)+.4*Math.Max(0,1-Math.Abs(t-3)/.14));content.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}string path=Path.Combine(root,"A_"+(number+1)+"nC.cal");File.WriteAllText(path,content.ToString(),new UTF8Encoding(false));return path;}).ToArray();
    }
}
