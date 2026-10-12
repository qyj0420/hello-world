using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class CalibrationAverageSelectionSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p==null?o.GetType().GetField(n,All).GetValue(o):p.GetValue(o,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool v,string label){if(!v)throw new Exception(label);checks++;}
    static void Pump(Func<bool> done){var until=DateTime.UtcNow.AddSeconds(30);do{Application.DoEvents();Thread.Sleep(2);}while(!done()&&DateTime.UtcNow<until);Check(done(),"operation completes");}
    static void Idle(Control c){Pump(()=>!(bool)Get(c,"IsBusy")&&!((System.Windows.Forms.Timer)Get(c,"comparisonDebounce")).Enabled);}
    static bool Compare(Control c,string[] paths,bool normalized,bool aligned){var task=(Task<bool>)Call(c,"CompareCalibrationsAsync",paths,normalized,aligned);Pump(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    static double[] Mean(Control c){return (double[])Get(c,"comparisonMeanY");}
    static void Near(double value,double expected,string name){Check(Math.Abs(value-expected)<1e-8,name+" actual="+value.ToString("R"));}
    static double At(Control c,double seconds){var x=(double[])Get(c,"comparisonMeanX");int i=Enumerable.Range(0,x.Length).OrderBy(j=>Math.Abs(x[j]-seconds)).First();Check(Math.Abs(x[i]-seconds)<1e-12,"requested real time exists");return Mean(c)[i];}
    static void CheckSourceHashes(Control browser,string[] paths)
    {
        var records=((IEnumerable)Get(Get(browser,"project"),"Calibrations")).Cast<object>().Where(r=>paths.Contains((string)Get(r,"Path"))).ToArray();
        var array=Array.CreateInstance(records[0].GetType(),records.Length);for(int i=0;i<records.Length;i++)array.SetValue(records[i],i);
        var prepared=Call(browser,"PrepareAverageComparison",array,true,false,CancellationToken.None);var hashes=(IDictionary)Get(prepared,"SourceHashes");
        Check(hashes.Count==paths.Length,"source signature contains exactly participating records");
        foreach(string path in paths)using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))
            Check((string)hashes[path]==Convert.ToBase64String(sha.ComputeHash(stream)),"source signature matches actual validated calibration bytes");
    }
    static void Cal(string path,double amplitude,double reflection,double incident=10e-6,double rate=50e6)
    {
        var text=new StringBuilder("SR\t"+rate.ToString("R",CultureInfo.InvariantCulture)+"\r\nCable length (m)\t1700\r\nV (m/usec)\t170\r\nStart_point\t"+incident.ToString("R",CultureInfo.InvariantCulture)+"\r\nFinal_point\t"+(incident+20e-6).ToString("R",CultureInfo.InvariantCulture)+"\r\n");
        for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");
        int count=(int)Math.Round(70e-6*rate)+1;
        for(int i=0;i<count;i++){double t=i/rate;double y=.2+amplitude*(Math.Exp(-Math.Pow((t-incident)/60e-9,2))+reflection*Math.Exp(-Math.Pow((t-incident-20e-6)/60e-9,2)));text.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}
        File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));
    }
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        string qa=Path.Combine(Path.GetFullPath(args[1]),"mean-selection-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(qa);
        File.WriteAllText(Path.Combine(qa,"ITEM.in"),"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=1700\r\n");
        File.WriteAllText(Path.Combine(qa,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");
        var amplitudes=new[]{1d,2d,4d,8d};var reflections=new[]{.2,.4,.6,.8};var paths=Enumerable.Range(0,4).Select(i=>Path.Combine(qa,"A_"+(i+1)+"nC.cal")).ToArray();
        for(int i=0;i<4;i++)Cal(paths[i],amplitudes[i],reflections[i]);var originals=paths.Select(File.ReadAllBytes).ToArray();
        var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
        using(var form=new Form{ClientSize=new Size(1400,850),ShowInTaskbar=false})using(var browser=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true))){
            form.Controls.Add(browser);browser.Dock=DockStyle.Fill;form.Show();Check((bool)Call(browser,"LoadProject",qa),"isolated fixture loaded");
            ((CheckBox)Get(browser,"comparisonAverage")).Checked=true;
            Check(Compare(browser,new[]{paths[0]},true,false),"one of four averaged");var single=(double[])Mean(browser).Clone();Near(At(browser,10e-6),1,"single normalized incident");Near(At(browser,30e-6),.2,"single normalized reflection");
            CheckSourceHashes(browser,new[]{paths[0]});
            var list=(CheckedListBox)Get(browser,"comparisonRecordsList");Check(list.CheckedItems.Count==1,"one actual checked contributor");list.SelectedIndex=3;Application.DoEvents();Check(list.CheckedItems.Count==1&&single.SequenceEqual(Mean(browser)),"highlight alone does not add a contributor");
            for(int i=1;i<4;i++)list.SetItemChecked(i,true);Idle(browser);Near(At(browser,10e-6),1,"normalized mean incident stays unit peak");Near(At(browser,30e-6),.5,"four equal contributors change real reflection shape");Check(list.CheckedItems.Count==4,"UI check state synced to four contributors");
            CheckSourceHashes(browser,paths);
            ((CheckBox)Get(browser,"comparisonNormalize")).Checked=false;Idle(browser);Near(At(browser,10e-6),3.95,"raw mean includes original baseline and arithmetic amplitude");Near(At(browser,30e-6),2.65,"raw reflection equals amplitude-weighted input sum divided by four");
            list.SetItemChecked(1,false);list.SetItemChecked(2,false);Idle(browser);Near(At(browser,10e-6),4.7,"selected first and fourth equal-weight mean");Near(At(browser,30e-6),3.5,"only checked reflection contributors");
            var search=(TextBox)Get(browser,"comparisonSearch");search.Text="1nC";Application.DoEvents();Check(list.Items.Count==1&&((IEnumerable)Get(browser,"comparisonSelected")).Cast<object>().Count()==2,"search hides but does not silently deselect checked contributor");search.Clear();
            list.SetItemChecked(3,false);Idle(browser);Near(At(browser,10e-6),1.2,"single raw mean equals original waveform");Near(At(browser,30e-6),.4,"single raw reflection retains baseline");
            Check(paths.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(originals[i])).All(v=>v),"all display operations preserve raw calibration files");
            for(int i=0;i<4;i++)Cal(paths[i],amplitudes[i],reflections[i],10e-6+i*.4e-6);
            Check((bool)Call(browser,"LoadProject",qa),"shifted origins reload");((CheckBox)Get(browser,"comparisonAverage")).Checked=true;
            Check(Compare(browser,null,true,false),"unaligned different incidents still permit actual-time display averaging");Check(Mean(browser)!=null&&Get(browser,"comparisonGeometry")==null,"unaligned average does not fabricate a common distance origin");
            Check(Compare(browser,null,true,true),"explicit alignment subtracts each actual incident");Near(At(browser,0),1,"aligned incident at zero");Near(At(browser,20e-6),.5,"aligned physical reflection delay preserved");
            var geom=Get(browser,"comparisonGeometry");Near((double)Call(geom,"DistanceAt",20e-6),1700,"aligned time converts to true cable length, not absolute timestamp");
            Cal(paths[1],2,.4,10.4e-6,25e6);Check((bool)Call(browser,"LoadProject",qa),"mixed actual sample rates reload");((CheckBox)Get(browser,"comparisonAverage")).Checked=true;Check(Compare(browser,null,true,true),"different valid sampling rates interpolate on common real axis");Near(At(browser,20e-6),.5,"mixed-rate aligned reflection amplitude remains correct");
            using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(qa,"aligned-average.png"));}
            form.Close();
        }
        Console.WriteLine("CALIBRATION_AVERAGE_SELECTION_PASS="+checks);Console.WriteLine("QA="+qa);return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
