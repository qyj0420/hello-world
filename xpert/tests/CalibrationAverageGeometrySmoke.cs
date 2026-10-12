using System;
using System.Collections;
using System.Collections.Generic;
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

// args[2], if explicitly supplied, is a read-only existing calibration project.
// Synthetic fixtures are written only under args[1]; no edit/save APIs are used.
static class CalibrationAverageGeometrySmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Assembly module;static string output;
    static object Get(object o,string n){var p=o.GetType().GetProperty(n,All);return p!=null?p.GetValue(o,null):o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){var p=o.GetType().GetProperty(n,All);if(p!=null)p.SetValue(o,v,null);else o.GetType().GetField(n,All).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static void Near(double x,double y,string text){Check(Math.Abs(x-y)<=1e-9*Math.Max(1,Math.Abs(y)),text+" actual="+x.ToString("R")+" expected="+y.ToString("R"));}
    static string Hash(string p){using(var sha=SHA256.Create())using(var file=File.OpenRead(p))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
    static void Pump(Func<bool> done){var until=DateTime.UtcNow.AddSeconds(30);do{Application.DoEvents();Thread.Sleep(2);}while(!done()&&DateTime.UtcNow<until);Check(done(),"bounded comparison completion");Application.DoEvents();}
    static bool Compare(Control b,string[] paths,bool normalize=false,bool align=false){var t=(Task<bool>)Call(b,"CompareCalibrationsAsync",paths,normalize,align);Pump(()=>t.IsCompleted);return t.GetAwaiter().GetResult();}
    static void Idle(Control b){Pump(()=>!(bool)Get(b,"IsBusy")&&!((System.Windows.Forms.Timer)Get(b,"comparisonDebounce")).Enabled);}
    static object[] Series(Control b){return ((IEnumerable)Get(Get(b,"plot"),"series")).Cast<object>().ToArray();}
    static void NoLocation(Control b)
    {
        var plot=Get(b,"plot");Check(Get(b,"comparisonGeometry")==null,"no invented common geometry");
        Check(!(bool)Get(plot,"HasCableOverlay"),"no distance/propagation overlay");
        Check(Double.IsNaN((double)Call(plot,"CableDistanceAt",20d)),"no time-to-distance conversion");
        Check(!(bool)Call(b,"CanUseComparisonGeometry")&&!((Control)Get(b,"findComparisonJointsButton")).Enabled,"joint operation disabled");
        Check(((IEnumerable)Get(b,"comparisonJointCandidates")).Cast<object>().Count()==0,"no stale joint candidates");
        Check(((Control)Get(b,"information")).Text.Contains("已显示时间轴平均"),"display result and geometry limitation explained separately");
    }
    static string Cal(string root,string name,double length=1700,double velocity=170,double incident=10e-6,double amplitude=1,double rate=50e6)
    {
        string path=Path.Combine(root,name);var inv=CultureInfo.InvariantCulture;
        var s=new StringBuilder("SR\t"+rate.ToString("R",inv)+"\r\nCable length (m)\t"+length.ToString("R",inv)+"\r\nV (m/usec)\t"+velocity.ToString("R",inv)+"\r\nStart_point\t"+incident.ToString("R",inv)+"\r\nFinal_point\t0.00003\r\n");
        for(int i=0;i<8;i++)s.Append("Field").Append(i).Append("\t0\r\n");
        int n=(int)Math.Round(40e-6*rate)+1;
        for(int i=0;i<n;i++){double t=i/rate,y=.2+amplitude*Math.Exp(-Math.Pow((t-10e-6)/80e-9,2));s.Append(y.ToString("R",inv)).Append("\r\n");}
        File.WriteAllText(path,s.ToString(),new UTF8Encoding(false));return path;
    }
    static void Synthetic(Control browser)
    {
        string root=Path.Combine(output,"synthetic");Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆芯数=单相\r\nA相电缆长度=1700\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t1700m\r\n");
        string a=Cal(root,"A_1nC.cal"),b=Cal(root,"A_2nC.cal",amplitude:3),badLength=Cal(root,"A_3nC.cal",length:33),badSpeed=Cal(root,"A_4nC.cal",velocity:163.2),unknown=Cal(root,"A_5nC.cal",length:Double.NaN,velocity:Double.NaN),noIncident=Cal(root,"A_6nC.cal",incident:Double.NaN),outside=Cal(root,"A_7nC.cal",incident:90e-6),shifted=Cal(root,"A_8nC.cal",incident:11e-6),coarse=Cal(root,"A_9nC.cal",length:33,amplitude:3,rate:25e6);
        string[] all=Directory.GetFiles(root);var original=all.ToDictionary(p=>p,Hash);
        Check((bool)Call(browser,"LoadProject",root),"synthetic project loads");((CheckBox)Get(browser,"comparisonAverage")).Checked=true;
        Check(Compare(browser,new[]{a,b}),"identical metadata average");Check(Get(browser,"comparisonGeometry")!=null&&(bool)Get(Get(browser,"plot"),"HasCableOverlay"),"compatible metadata retains location permission");
        var y=(double[])Get(browser,"comparisonMeanY");Near(y[500],2.2,"raw signed mean retains baseline and equal weights");
        foreach(string other in new[]{badLength,badSpeed,unknown,noIncident,outside,shifted}){
            Check(Compare(browser,new[]{a,other}),"time average survives incompatible or unavailable geometry: "+Path.GetFileName(other));
            Check(Series(browser).Length==1&&((double[])Get(browser,"comparisonMeanY")).Length==2001,"one full time average remains");NoLocation(browser);
        }
        Check(Compare(browser,new[]{a,coarse},false,false),"different sample intervals retain actual-time interpolation");NoLocation(browser);Near(((double[])Get(browser,"comparisonMeanY"))[500],2.2,"mixed-rate exact shared sample mean");
        Check(Compare(browser,new[]{a,b},true,false),"normalization remains independent of alignment");Near(((double[])Get(browser,"comparisonMeanY"))[500],1,"normalize each source before average");
        Check(Compare(browser,new[]{a,shifted},true,true),"explicit incident alignment restores compatible distance origin");Near((double)Get(Get(browser,"comparisonGeometry"),"IncidentSeconds"),0,"aligned origin uses actual incident shifts");
        Check(!Compare(browser,new[]{noIncident},true,true),"alignment cannot fabricate missing incident time");Check(Series(browser).Length==0&&Get(browser,"comparisonGeometry")==null,"invalid aligned request clears stale displayed mean and geometry");
        Check(Compare(browser,new[]{noIncident},true,false),"same unknown-incident source remains usable without alignment");NoLocation(browser);
        Check(Compare(browser,new[]{a,badLength},true,false),"mismatched mean is available for interaction checks");
        var plot=(Control)Get(browser,"plot");var bounds=(Rectangle)Get(Get(plot,"CurrentLayout"),"Plot");
        int forms=Application.OpenForms.Count;
        Call(plot,"OnMouseDoubleClick",new MouseEventArgs(MouseButtons.Left,2,bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2,0));
        Application.DoEvents();Check(Application.OpenForms.Count==forms,"double click on time-only mean cannot open a joint editor");
        Check(!((Control)Get(browser,"saveButton")).Visible&&!((Control)Get(browser,"editButton")).Visible&&!((Control)Get(browser,"jointButton")).Visible,"mean does not expose single-record save/edit or distance add-joint controls");
        Check(!(bool)Call(browser,"BeginCalibrationEdit")&&!(bool)Call(browser,"SaveCalibrationEdit"),"mean cannot be written as a selected source calibration");
        var search=(Task<bool>)Call(browser,"FindComparisonJointsAsync");Pump(()=>search.IsCompleted);Check(!search.GetAwaiter().GetResult(),"time-only mean cannot start a distance search");
        var average=(CheckBox)Get(browser,"comparisonAverage");var normalize=(CheckBox)Get(browser,"comparisonNormalize");
        normalize.Checked=false;Idle(browser);Check(Series(browser).Length==1,"disable normalization retains one time-only mean");NoLocation(browser);Near(((double[])Get(browser,"comparisonMeanY"))[500],1.2,"unnormalized mismatched mean retains voltage amplitude");
        average.Checked=false;Idle(browser);Check(Series(browser).Length==2&&Get(browser,"comparisonGeometry")==null,"disable average restores individual selected curves without stale geometry");
        average.Checked=true;Idle(browser);Check(Series(browser).Length==1,"reenable average restores mismatched time mean");NoLocation(browser);
        Check((bool)Call(browser,"SelectFile",a),"can navigate from time-only mean to single source");Idle(browser);
        Check((bool)Call(browser,"OpenAllCalibrations"),"can reopen comparison after single-source inspection");Idle(browser);
        Check(average.Checked&&Series(browser).Length==1&&((IEnumerable)Get(browser,"comparisonSelected")).Cast<string>().OrderBy(p=>p).SequenceEqual(new[]{a,badLength}.OrderBy(p=>p)),"reopen retains selected contributors and averaging mode");NoLocation(browser);
        foreach(string p in all)Check(Hash(p)==original[p],"synthetic input untouched by averaging: "+Path.GetFileName(p));
    }
    static void ReadOnlyProject(Control browser,string project)
    {
        string[] names={"ALL_10nC.cal","ALL_5nC.cal","ALL_TDR.cal"};string[] paths=names.Select(n=>Path.Combine(Path.GetFullPath(project),n)).ToArray();
        foreach(string p in paths)Check(File.Exists(p),"specified read-only source exists");
        var before=paths.ToDictionary(p=>p,Hash);var reader=module.GetType("HVX.Controls.BrowserReader",true);var recordType=module.GetType("HVX.Controls.BrowserRecord",true);
        var records=Array.CreateInstance(recordType,paths.Length);var waves=new List<object>();
        for(int i=0;i<paths.Length;i++){
            var record=Activator.CreateInstance(recordType);Set(record,"Path",paths[i]);Set(record,"Name",names[i]);Set(record,"Kind","cal");Set(record,"Phase","ALL");records.SetValue(record,i);
            waves.Add(reader.GetMethods(All).Single(m=>m.Name=="LoadWaveform"&&m.GetParameters().Length==1).Invoke(null,new[]{record}));
        }
        // Only the three explicitly selected files are read; no project metadata,
        // recent-file list, saved calibration or UI editing path is invoked.
        foreach(bool normalize in new[]{false,true}){
            var prepared=Call(browser,"PrepareAverageComparison",records,normalize,false,CancellationToken.None);
            Check((int)Get(prepared,"SourceCount")==3&&((IEnumerable)Get(prepared,"Series")).Cast<object>().Count()==1,"actual three files produce one mean");
            Check(Get(prepared,"Geometry")==null&&((string)Get(prepared,"GeometryWarning")).Length>0,"actual geometry mismatch remains unavailable");
            var x=(double[])Get(prepared,"MeanX");var y=(double[])Get(prepared,"MeanY");Check(x.Length==2313&&y.Length==2313,"actual full sample count preserved");Near(x[0],0,"actual start time");Near(x.Last(),18.496e-6,"actual end time");
            var expected=new double[y.Length];
            foreach(var wave in waves){var input=(double[])Get(wave,"Y");double baseline=normalize?input.Take(22).Average():0,scale=normalize?input.Max(v=>Math.Abs(v-baseline)):1;
                for(int j=0;j<input.Length;j++)expected[j]+=(input[j]-baseline)/scale/3;
            }
            for(int j=0;j<y.Length;j++)Near(y[j],expected[j],"actual mean equals independent arithmetic reference");
        }
        foreach(string p in paths)Check(Hash(p)==before[p],"original user CAL SHA256 unchanged: "+Path.GetFileName(p));
        Console.WriteLine("READ_ONLY_ACTUAL_CAL_PASS; "+String.Join("; ",paths.Select(p=>Path.GetFileName(p)+"="+before[p])));
    }
    [STAThread]static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
        module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
        using(var form=new Form{ClientSize=new Size(1180,720),ShowInTaskbar=false})using(var browser=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectDataBrowserControl",true))){
            form.Controls.Add(browser);browser.Dock=DockStyle.Fill;form.Show();Synthetic(browser);
            if(args.Length>2)ReadOnlyProject(browser,args[2]);
            using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,form.ClientRectangle);bmp.Save(Path.Combine(output,"mean-without-distance.png"));}form.Close();
        }
        Console.WriteLine("CALIBRATION_AVERAGE_GEOMETRY_PASS="+checks+"; HARDWARE_ACCESSED=False; ORIGINAL_CAL_WRITTEN=False");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
