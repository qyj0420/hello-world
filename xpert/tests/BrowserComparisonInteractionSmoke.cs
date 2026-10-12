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

internal static class BrowserComparisonInteractionSmoke
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object o,string n){var t=o.GetType();var p=t.GetProperty(n,Flags);return p!=null?p.GetValue(o,null):t.GetField(n,Flags).GetValue(o);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool ok,string text){checks++;if(!ok)throw new InvalidOperationException(text);}
    static void Pump(Func<bool> done){var until=DateTime.UtcNow.AddSeconds(30);do{Application.DoEvents();Thread.Sleep(5);}while(!done()&&DateTime.UtcNow<until);Check(done(),"Operation completes");Application.DoEvents();}
    static object[] Series(Control c){return ((IEnumerable)Get(Get(c,"plot"),"series")).Cast<object>().ToArray();}
    static double Peak(Control c){return ((double[])Get(Series(c).Single(),"Y")).Max();}
    static void Idle(Control c){Pump(()=>!(bool)Get(c,"IsBusy")&&!((System.Windows.Forms.Timer)Get(c,"comparisonDebounce")).Enabled);}
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string root=Path.Combine(output,"mean-fixture");string[] paths=Fixture(root);var bytes=paths.Select(File.ReadAllBytes).ToArray();
            var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            using(var form=new Form{ClientSize=new Size(1400,850),ShowInTaskbar=false})
            using(var browser=(Control)Activator.CreateInstance(asm.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                browser.Dock=DockStyle.Fill;form.Controls.Add(browser);form.Show();Application.DoEvents();
                Check((bool)Call(browser,"LoadProject",root),"Fixture load");
                var task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(()=>task.IsCompleted);Check(task.Result,"Four records compare");
                var avg=(CheckBox)Get(browser,"comparisonAverage");var norm=(CheckBox)Get(browser,"comparisonNormalize");var list=(CheckedListBox)Get(browser,"comparisonRecordsList");
                Check(!avg.Checked&&norm.Checked,"Average opt-in and normalization default on");Check(Series(browser).Length==4,"All four selected traces shown initially");
                avg.Checked=true;Idle(browser);Check(Series(browser).Length==1&&Math.Abs(Peak(browser)-1)<1e-9,"One mean curve of normalized inputs");
                Check(list.Enabled&&list.CheckedItems.Count==4,"Selections stay editable while averaging");
                norm.Checked=false;Idle(browser);Check(Math.Abs(Peak(browser)-2.7)<1e-9,"Raw mean keeps original voltage baseline and amplitude");
                list.SetItemChecked(1,false);list.SetItemChecked(3,false);Idle(browser);Check(Math.Abs(Peak(browser)-2.2)<1e-9,"Mean follows checked records only");
                avg.Checked=false;Idle(browser);Check(Series(browser).Length==2&&list.CheckedItems.Count==2,"Turning mean off restores selected individual curves");
                avg.Checked=true;list.SetItemChecked(2,false);Idle(browser);Check(Math.Abs(Peak(browser)-1.2)<1e-9,"Single selection mean equals source");
                list.SetItemChecked(0,false);Idle(browser);Check(Series(browser).Length==0,"No selection clears mean");
                list.SetItemChecked(0,true);list.SetItemChecked(2,true);list.SetItemChecked(1,true);list.SetItemChecked(1,false);Idle(browser);Check(Series(browser).Length==1&&Math.Abs(Peak(browser)-2.2)<1e-9,"Rapid selections publish last mean only");
                var search=(TextBox)Get(browser,"comparisonSearch");search.Text="1nC";Application.DoEvents();Check(list.Items.Count==1,"Search filters choices");
                Check(((IEnumerable)Get(browser,"comparisonSelected")).Cast<object>().Count()==2,"Search retains hidden selected mean contributor");search.Clear();
                foreach(float scale in new[]{1f,1.25f,1.5f,2f})
                using(var font=new Font("Microsoft YaHei UI",10f*scale))
                {
                    var content=(Panel)Get(browser,"comparisonOptionsContent");var options=(Panel)Get(browser,"comparisonOptions");
                    content.Font=font;foreach(Control control in content.Controls)control.Font=font;
                    foreach(var size in new[]{new Size(1400,850),new Size(900,560)})
                    {
                        form.ClientSize=size;Call(browser,"ArrangeComparisonOptions");Application.DoEvents();
                        var children=content.Controls.Cast<Control>().ToArray();
                        foreach(var control in children)
                        {
                            Check(content.ClientRectangle.Contains(control.Bounds),"Control within scrollable content: "+control.Name+" "+scale+" "+size);
                            if(control is CheckBox){int text=TextRenderer.MeasureText(control.Text,control.Font,new Size(control.Width-28,Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height;Check(control.Height>=text+8,"Checkbox wrapped label fits: "+control.Name);}
                            foreach(var other in children.Where(c=>String.CompareOrdinal(c.Name,control.Name)>0))Check(!control.Bounds.IntersectsWith(other.Bounds),"Options do not overlap: "+control.Name+" / "+other.Name);
                        }
                        Check(options.AutoScrollMinSize.Height>=content.Height,"Full option panel remains scroll reachable");
                        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(output,"comparison-font-"+(int)(scale*100)+"-"+size.Width+".png"));}
                    }
                    // Remove references before disposing the temporary stress font.
                    content.Font=SystemFonts.MessageBoxFont;foreach(Control control in content.Controls)control.Font=SystemFonts.MessageBoxFont;
                }
                Check(paths.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(bytes[i])).All(x=>x),"Comparison never changes source files");
                File.WriteAllText(paths[0],"damaged QA record",new UTF8Encoding(false));
                task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,false,false);Pump(()=>task.IsCompleted);
                Check(task.Result&&Math.Abs(Peak(browser)-3.2)<1e-9,"Mean excludes a damaged record with a fixed valid contributor count");
                Check(((string)Get(browser,"LastError")).Contains("A_1nC"),"Damaged mean source explicitly reported");
                task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",new[]{paths[0]},true,false);Pump(()=>task.IsCompleted);
                Check(!task.Result&&Series(browser).Length==0,"All-invalid mean clears old graph");
                File.WriteAllBytes(paths[0],bytes[0]);
                task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,true);((CancellationTokenSource)Get(browser,"waveCancellation")).Cancel();Pump(()=>task.IsCompleted);
                Check(!task.Result&&!(bool)Get(browser,"IsBusy"),"Cancellation releases averaging busy state");
                task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,true);Pump(()=>task.IsCompleted);
                Check(task.Result&&Series(browser).Length==1&&Math.Abs(Peak(browser)-1)<1e-9,"Restored sources and optional alignment average correctly");
                Check(paths.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(bytes[i])).All(x=>x),"All fixture bytes restored after corruption regression");
                TestInPlace(browser,form,output);
                form.Close();
            }
            Console.WriteLine("BROWSER_COMPARISON_INTERACTION_PASS="+checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static void TestInPlace(Control browser,Form form,string output)
    {
        string[] paths=Fixture(Path.Combine(output,"scroll-fixture"),48);
        Check((bool)Call(browser,"LoadProject",Path.GetDirectoryName(paths[0])),"Many-record comparison fixture loads");
        ((CheckBox)Get(browser,"comparisonAverage")).Checked=false;
        var task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",null,true,false);Pump(()=>task.IsCompleted);Check(task.Result,"Many-record comparison loads");
        var list=(CheckedListBox)Get(browser,"comparisonRecordsList");var plot=(Control)Get(browser,"plot");var detail=(Control)Get(browser,"detail");
        var options=(Panel)Get(browser,"comparisonOptions");
        var average=(CheckBox)Get(browser,"comparisonAverage");var normalize=(CheckBox)Get(browser,"comparisonNormalize");var align=(CheckBox)Get(browser,"comparisonAlign");
        form.ClientSize=new Size(1000,560);Application.DoEvents();
        list.SelectedIndex=30;list.TopIndex=25;list.Focus();Application.DoEvents();int top=list.TopIndex,selected=list.SelectedIndex;
        object[] choices=list.Items.Cast<object>().ToArray();
        int pageChanges=0;ControlEventHandler changed=delegate{pageChanges++;};detail.ControlAdded+=changed;detail.ControlRemoved+=changed;
        var bounds=(Rectangle)Get(Get(plot,"CurrentLayout"),"Plot");
        Call(plot,"OnMouseWheel",new MouseEventArgs(MouseButtons.None,0,bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2,120));
        double min=(double)Get(plot,"xmin"),max=(double)Get(plot,"xmax");Check(min>0&&max<20,"Horizontal zoom is active");
        object[] oldSeries=Series(browser);
        list.SetItemChecked(30,false);
        Check(Series(browser).SequenceEqual(oldSeries),"Check event keeps old curves while preparation is pending");
        Idle(browser);
        Check(Series(browser).Length==47,"Checkbox atomically publishes changed curves");
        Check(pageChanges==0,"Checkbox never detaches or re-adds complete page");
        Check(list.Items.Cast<object>().SequenceEqual(choices),"Checkbox does not rebuild list item objects");
        Check(list.TopIndex==top&&list.SelectedIndex==selected&&list.Focused,"Checkbox retains list scroll selection and focus");
        Check((double)Get(plot,"xmin")==min&&(double)Get(plot,"xmax")==max,"Checkbox retains zoom");
        average.Checked=true;Idle(browser);
        Check(Series(browser).Length==1&&(double)Get(plot,"xmin")==min&&(double)Get(plot,"xmax")==max,"Mean mode preserves time viewport");
        normalize.Checked=false;Idle(browser);
        Check((double)Get(plot,"xmin")==min&&(double)Get(plot,"xmax")==max,"Amplitude unit change keeps horizontal viewport while recalculating vertical scale");
        align.Checked=true;Idle(browser);
        Check((double)Get(plot,"xmin")==-1&&(double)Get(plot,"xmax")==19,"Time-origin change resets to aligned domain, not stale absolute coordinates");
        average.Checked=false;Idle(browser);
        oldSeries=Series(browser);
        // Keep the worker at the cache boundary so the pending snapshot assertion
        // is deterministic rather than dependent on disk speed.
        lock(Get(browser,"comparisonCacheGate"))
        {
            task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",paths.Take(3).ToArray(),false,true);
            Check(Series(browser).SequenceEqual(oldSeries),"Async preparation does not clear the currently displayed curves");
            ((CancellationTokenSource)Get(browser,"waveCancellation")).Cancel();
        }
        Pump(()=>task.IsCompleted);Check(!task.Result&&Series(browser).SequenceEqual(oldSeries),"Cancelled update keeps previous result without publishing partial curves");
        task=(Task<bool>)Call(browser,"CompareCalibrationsAsync",paths.Take(2).ToArray(),true,false);
        var latest=(Task<bool>)Call(browser,"CompareCalibrationsAsync",paths.Take(4).ToArray(),true,false);
        Pump(()=>task.IsCompleted&&latest.IsCompleted);Check(!task.Result&&latest.Result&&Series(browser).Length==4,"Rapid updates publish only the latest generation");
        Check(pageChanges==0&&list.Items.Cast<object>().SequenceEqual(choices),"All option and programmatic updates leave page and record objects mounted");
        detail.ControlAdded-=changed;detail.ControlRemoved-=changed;
        Check((bool)Call(browser,"SelectFile",paths[0]),"Single calibration selected in a multiple-record project");Idle(browser);
        Check(!((Control)Get(browser,"comparisonButton")).Visible,"Single calibration page hides comparison button even with multiple calibrations");
        Check((bool)Call(browser,"OpenAllCalibrations"),"Tree all-calibrations entry remains accessible");Idle(browser);
        Check(((Control)Get(browser,"comparisonButton")).Visible,"Comparison page retains its comparison/cancel control");
    }
    static string[] Fixture(string root,int count=4)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆芯数: = \"单芯\"\r\n电缆长度: = \"170\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tA\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t170m\r\n",new UTF8Encoding(false));
        return Enumerable.Range(1,count).Select(n=>{var text=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");for(int i=0;i<2001;i++){double t=i*.01,y=.2+n*(Math.Max(0,1-Math.Abs(t-1)/.1)+.4*Math.Max(0,1-Math.Abs(t-3)/.14));text.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}string path=Path.Combine(root,"A_"+n+"nC.cal");File.WriteAllText(path,text.ToString(),new UTF8Encoding(false));return path;}).ToArray();
    }
}
