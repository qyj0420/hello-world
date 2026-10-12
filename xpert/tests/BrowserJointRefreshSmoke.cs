using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class BrowserJointRefreshSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Get(object value,string name){var type=value.GetType();var p=type.GetProperty(name,All);return p!=null?p.GetValue(value,null):type.GetField(name,All).GetValue(value);}
    static object Call(object value,string name,params object[] args){return value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(value,args);}
    static void Check(bool value,string description){checks++;if(!value)throw new Exception(description);}
    static void Pump(Func<bool> complete){DateTime deadline=DateTime.UtcNow.AddSeconds(25);while(!complete()&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(5);}Application.DoEvents();Check(complete(),"Operation completed without full-project reload");}
    static object Plan(Assembly module,string root,double metres,string name)
    {
        object snapshot=module.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Read",All).Invoke(null,new object[]{root});
        return Call(snapshot,"Prepare",new[]{"A"},metres,name,"冷缩接头","","");
    }
    static bool Save(Control browser,object plan,string root)
    {
        return (bool)Call(browser,"SaveJointPlan",plan,root,(int)Get(browser,"projectGeneration"),(int)Get(browser,"selectionGeneration"));
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and output folder.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string root=Path.Combine(output,"joint-refresh-"+Guid.NewGuid().ToString("N"));string cal=Fixture(root);
            string cable=Path.Combine(root,"CABLE.in"),item=Path.Combine(root,"ITEM.in");
            byte[] oldCable=File.ReadAllBytes(cable),oldCal=File.ReadAllBytes(cal),oldItem=File.ReadAllBytes(item);
            var module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var host=new Form{ClientSize=new Size(1420,850),ShowInTaskbar=false})
            using(var browser=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                host.Controls.Add(browser);browser.Dock=DockStyle.Fill;host.Show();Application.DoEvents();
                Check((bool)Call(browser,"LoadProject",root),"Fixture project loaded");
                Check((bool)Call(browser,"SelectFile",cal),"Fixture calibration selected");Pump(()=>!(bool)Get(browser,"IsBusy"));
                object project=Get(browser,"project"),wave=Get(browser,"selectedWave"),record=Get(browser,"selectedRecord");
                var tree=(TreeView)Get(browser,"tree");TreeNode selected=tree.SelectedNode;
                int generation=(int)Get(browser,"projectGeneration"),loaded=0,saved=0,structureChanged=0;
                browser.GetType().GetEvent("ProjectLoaded").AddEventHandler(browser,new EventHandler((s,e)=>loaded++));
                browser.GetType().GetEvent("ProjectSaved").AddEventHandler(browser,new EventHandler((s,e)=>saved++));
                browser.GetType().GetEvent("CableStructureChanged").AddEventHandler(browser,new EventHandler((s,e)=>structureChanged++));
                object plan=Plan(module,root,42.125,"新接头");
                // Joint-only refresh must not reopen or parse the original calibration.
                using(var rawLock=new FileStream(cal,FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    Check(Save(browser,plan,root),"Joint save queued with waveform exclusively locked");
                    Pump(()=>!(bool)Get(browser,"IsBusy"));
                }
                Check(saved==1&&loaded==0,"One saved event, no project reload event");
                Check((int)Get(browser,"projectGeneration")==generation,"Project generation stays stable");
                Check(ReferenceEquals(Get(browser,"project"),project),"Project object retained instead of full scan");
                Check(ReferenceEquals(Get(browser,"selectedWave"),wave)&&ReferenceEquals(Get(browser,"selectedRecord"),record),"Existing waveform and selection retained");
                Check(tree.SelectedNode==selected&&(string)Get(browser,"SelectedFilePath")==cal,"Tree selection retained");
                var calibration=(Control)Get(browser,"calibration");
                Check(((double[])Call(calibration,"GetCablePositionsMeters")).Contains(42.125),"New joint marker immediately visible on existing waveform");
                Check(((double[])Get(project,"JointPositionsMetres")).Contains(42.125),"In-memory joint metadata refreshed");
                var phaseJoints=(IDictionary)Get(project,"JointPositionsByPhase");
                Check(((double[])phaseJoints["A"]).Contains(42.125)&&!((double[])phaseJoints["B"]).Contains(42.125),"Only selected-phase joint metadata changes");
                string status=((Label)Get(browser,"status")).Text,info=((TextBox)Get(browser,"information")).Text;
                Check(status.Contains("接头已保存")&&status.Contains("42.1 m")&&status.Contains("已更新"),"Completion status shows one decimal while precise stored position is retained");
                Check(info.Contains("接头已保存")&&info.Contains("CABLE.before-joint-"),"Persistent visible notice includes exact recovery backup");
                Thread.Sleep(80);Application.DoEvents();Check(((Label)Get(browser,"status")).Text==status,"Late waveform load cannot overwrite success notice");
                Check((int)Get(browser,"LastChangedFileCount")==1,"Exactly one changed file reported");
                var backups=Directory.GetFiles(root,"CABLE.before-joint-*.bak");
                Check(backups.Length==1&&File.ReadAllBytes(backups[0]).SequenceEqual(oldCable),"Exact original cable backup retained");
                Check(oldCal.SequenceEqual(File.ReadAllBytes(cal))&&oldItem.SequenceEqual(File.ReadAllBytes(item)),"Raw calibration and ITEM bytes unchanged");
                Check(structureChanged==1,"successful CABLE write broadcasts structure change");
                byte[] afterLocal=File.ReadAllBytes(cable);
                object external=Plan(module,root,60,"external joint");
                module.GetType("HVX.ProjectCables.CableJointStore",true).GetMethod("Commit",All).Invoke(null,new[]{external});
                using(var rawLock=new FileStream(cal,FileMode.Open,FileAccess.Read,FileShare.None))
                    Check((bool)Call(browser,"RefreshCableStructure"),"cross-page refresh reads metadata with original waveform locked");
                Check(((double[])Call(calibration,"GetCablePositionsMeters")).SequenceEqual(new[]{42.125,60d}),"cross-page addition updates joint markers");
                Check(ReferenceEquals(Get(browser,"selectedWave"),wave)&&ReferenceEquals(Get(browser,"selectedRecord"),record)&&tree.SelectedNode==selected,"cross-page refresh preserves waveform and selection");
                File.WriteAllBytes(cable,afterLocal);
                Check((bool)Call(browser,"RefreshCableStructure")&&((double[])Call(calibration,"GetCablePositionsMeters")).SequenceEqual(new[]{42.125}),"cross-page deletion removes stale joint markers");
                Check(structureChanged==1&&loaded==0&&(int)Get(browser,"projectGeneration")==generation,"metadata refresh neither rebroadcasts nor rescans project");

                var conflict=Plan(module,root,75,"冲突接头");File.AppendAllText(item,"备注: = \"other editor\"\r\n");byte[] safe=File.ReadAllBytes(cable);
                Check(Save(browser,conflict,root),"Concurrent-change guarded save queued");Pump(()=>!(bool)Get(browser,"IsBusy"));
                Check(saved==1&&loaded==0,"Failed save emits no completion or reload event");
                Check((int)Get(browser,"LastChangedFileCount")==0,"Failed save does not reuse previous success count");
                Check(((Label)Get(browser,"status")).Text.Contains("接头未保存")&&((TextBox)Get(browser,"information")).Text.StartsWith("接头未保存"),"Save failure is visible in status and information pane");
                Check(safe.SequenceEqual(File.ReadAllBytes(cable)),"Conflict leaves cable intact");
                Check(((double[])Call(calibration,"GetCablePositionsMeters")).SequenceEqual(new[]{42.125}),"Failed save leaves displayed markers intact");

                foreach(float fontSize in new[]{10f,15f,20f})
                {
                    browser.Font=new Font("Microsoft YaHei UI",fontSize);host.ClientSize=new Size(fontSize==20?1080:1420,850);
                    Application.DoEvents();Call(browser,"AdjustPhaseActionHeight");Application.DoEvents();
                    var actions=(FlowLayoutPanel)Get(browser,"actions");var visible=actions.Controls.Cast<Control>().Where(c=>c.Visible).ToArray();
                    Check(visible.Length>=4,"Calibration edit/save/cancel/joint buttons remain visible at font size "+fontSize);
                    Check(!((Control)Get(browser,"comparisonButton")).Visible,"Comparison shortcut is hidden on single calibration page");
                    foreach(var button in visible)
                    {
                        Check(button.Bounds.Top>=0&&button.Bounds.Bottom<=actions.ClientSize.Height,"Button fits action row at "+fontSize+": "+button.Text);
                        Check(button.Height>=button.Font.Height+10,"Button text has full height at "+fontSize+": "+button.Text);
                    }
                    for(int i=0;i<visible.Length;i++)for(int j=i+1;j<visible.Length;j++)
                        Check(!visible[i].Bounds.IntersectsWith(visible[j].Bounds),"Action buttons do not overlap at "+fontSize);
                    var comparison=(Control)Get(browser,"comparisonButton");
                    Check(Math.Abs(comparison.Font.Size-fontSize)<.1&&comparison.Width>=TextRenderer.MeasureText(comparison.Text,comparison.Font).Width+20,"Comparison action uses measured shared font sizing");
                    var statusControl=(Control)Get(browser,"status");Check(statusControl.Height>=statusControl.Font.Height+6,"Save status remains vertically legible at "+fontSize);
                }
                using(var image=new Bitmap(host.Width,host.Height)){host.DrawToBitmap(image,new Rectangle(Point.Empty,host.Size));image.Save(Path.Combine(output,"browser-joint-refresh-large-font.png"));}
                host.Close();
            }
            Console.WriteLine("BROWSER_JOINT_REFRESH_PASS="+checks);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    static string Fixture(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆芯数: = \"单芯三相\"\r\n电缆长度: = \"170\"\r\n",new UTF8Encoding(false));
        var cable=new StringBuilder();foreach(string phase in new[]{"A","B","C"})cable.Append("0\t").Append(phase).Append("\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t170m\r\n");File.WriteAllText(Path.Combine(root,"CABLE.in"),cable.ToString(),new UTF8Encoding(false));
        var content=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");for(int i=0;i<8;i++)content.Append("Field").Append(i).Append("\t0\r\n");
        for(int i=0;i<12001;i++){double t=i*.01,y=.2+Math.Max(0,1-Math.Abs(t-1)/.1)+.4*Math.Max(0,1-Math.Abs(t-3)/.14);content.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}
        string path=Path.Combine(root,"A_5nC.cal");File.WriteAllText(path,content.ToString(),new UTF8Encoding(false));return path;
    }
}
