using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class BrowserCalibrationActionsSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static object Field(object target,string name){return target.GetType().GetField(name,All).GetValue(target);}
    static object Get(object target,string name){return target.GetType().GetProperty(name,All).GetValue(target,null);}
    static void Set(object target,string name,object value){target.GetType().GetProperty(name,All).SetValue(target,value,null);}
    static object Call(object target,string name,params object[] args){return target.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static void Check(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
    static void Pump(Func<bool> done){DateTime end=DateTime.UtcNow.AddSeconds(15);while(!done()&&DateTime.UtcNow<end){Application.DoEvents();Thread.Sleep(10);}Check(done(),"Background work finished");Application.DoEvents();}
    static void Draw(Form form,string file){Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(file);}}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            if(args.Length!=2)throw new ArgumentException("Pass release root and QA directory.");
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            string fixture=Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
            string cal=BuildFixture(fixture);
            Assembly module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var host=new Form{ClientSize=new Size(1600,920),StartPosition=FormStartPosition.Manual,Location=new Point(10,10)})
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.Controls.ProjectDataBrowserControl",true)))
            {
                control.Dock=DockStyle.Fill;host.Controls.Add(control);Set(control,"EmbeddedMode",true);Set(control,"LanguageIndex",1);host.Show();Application.DoEvents();
                Check((bool)Call(control,"LoadProject",fixture),"Fixture loads");
                var tree=(TreeView)Field(control,"tree");var split=(SplitContainer)Field(control,"navigationSplit");
                tree.Nodes[2].ExpandAll();Application.DoEvents();
                Check(tree.Nodes[2].Nodes[0].Nodes.Count==31,"Tree contains all 31 real-format synthetic measurement files");
                Check(tree.Indent<=14,"Compact hierarchy leaves readable record labels");
                Check(split.SplitterDistance>=380,"Wide window expands navigation beyond former fixed 240 pixels");
                TreeNode record=tree.Nodes[2].Nodes[0].Nodes[12];tree.SelectedNode=record;record.EnsureVisible();Pump(()=>!(bool)Get(control,"IsBusy"));
                Check(!record.Text.Contains("A_"),"Phase prefix not repeated on every leaf");
                Check(record.Text.Contains("kV")&&record.Text.Contains("·"),"Voltage and repetition visible in short label");
                var selected=(Label)Field(control,"navigationSelection");
                Check(selected.Text.Contains("A_13.40kV_2026-09-28-10'12'00.dem"),"Selected full filename and extension shown outside shortened tree label");
                Check(selected.Height>=TextRenderer.MeasureText(selected.Text,selected.Font,new Size(selected.Width-selected.Padding.Horizontal,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix).Height+selected.Padding.Vertical,"Selected full filename fits in wrapped detail");
                object cp=tree.GetType().GetProperty("CreateParams",All).GetValue(tree,null);
                int style=(int)cp.GetType().GetProperty("Style").GetValue(cp,null);
                Check((style&0x8000)!=0,"Tree has no horizontal scrollbar style");
                Draw(host,Path.Combine(output,"browser-tree-31-records.png"));
                host.ClientSize=new Size(1065,700);Application.DoEvents();Call(control,"AdjustNavigationWidth");
                int narrow=split.SplitterDistance;Check(narrow>=260&&narrow<380,"Narrow laptop window proportionate tree width");
                host.ClientSize=new Size(1800,1000);Application.DoEvents();Call(control,"AdjustNavigationWidth");
                Check(split.SplitterDistance>narrow,"Navigation grows again after window enlarged");
                typeof(SplitContainer).GetMethod("OnSplitterMoving",All).Invoke(split,new object[]{new SplitterCancelEventArgs(390,0,390,0)});
                split.SplitterDistance=390;host.ClientSize=new Size(1600,920);Application.DoEvents();Call(control,"AdjustNavigationWidth");
                Check(split.SplitterDistance==390,"User-dragged width retained on resize");
                foreach(int language in new[]{0,1,6})
                foreach(int width in new[]{1065,1600})
                {
                    Set(control,"LanguageIndex",language);host.ClientSize=new Size(width,800);Application.DoEvents();
                    Check((bool)Call(control,"SelectFile",cal),"Calibration selectable");Pump(()=>!(bool)Get(control,"IsBusy"));
                    Check((bool)Call(control,"BeginCalibrationEdit"),"Calibration edit begins");
                    Application.DoEvents();
                    var actions=(Control)Field(control,"actions");
                    foreach(string name in new[]{"editButton","saveButton","cancelButton","jointButton"})
                    {
                        var button=(Control)Field(control,name);
                        Check(button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width+12,"Full translated calibration button: "+name);
                        Check(actions.ClientRectangle.Contains(button.Bounds),"Calibration button inside action area: "+name+" "+button.Bounds+" parent "+actions.ClientRectangle+" row "+((TableLayoutPanel)Field(control,"rightLayout")).RowStyles[2].Height+" visible "+actions.Visible+" language "+language+" width "+width);
                    }
                    if(language==1)Draw(host,Path.Combine(output,"browser-calibration-actions-"+width+".png"));
                    Call(control,"CancelCalibrationEdit");Check(!(bool)Get(control,"IsEditingCalibration"),"Cancel exits edit mode");
                }
                Set(control,"SelectedNodeKind","cal-comparison");
                Check(!(bool)Call(control,"BeginCalibrationEdit"),"Comparison view cannot edit a previously selected single calibration");
                Check(!(bool)Get(control,"IsEditingCalibration"),"Comparison rejection leaves editor closed");
                Set(control,"LanguageIndex",1);Call(control,"SelectFile",cal);Pump(()=>!(bool)Get(control,"IsBusy"));
                Check((bool)Call(control,"BeginCalibrationEdit"),"Edit before failed save");Call(control,"SetCalibrationCursorSeconds",1e-6,3.1e-6);
                byte[] before=File.ReadAllBytes(cal);
                using(var locked=new FileStream(Path.Combine(fixture,".DataBrowser.write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                {
                    Check((bool)Call(control,"SaveCalibrationEdit"),"Locked project queues save attempt");Pump(()=>!(bool)Get(control,"IsBusy"));
                    Check((bool)Get(control,"IsEditingCalibration"),"Save failure keeps edit mode");
                    Check(before.SequenceEqual(File.ReadAllBytes(cal)),"Failed save leaves waveform bytes unchanged");
                    Check(((Control)Field(control,"saveButton")).Enabled,"Save is available to retry after failure");
                }
                Check((bool)Call(control,"SaveCalibrationEdit"),"Unlocked save accepted");Pump(()=>!(bool)Get(control,"IsBusy"));
                Check(!(bool)Get(control,"IsEditingCalibration"),"Successful save exits editing");
                Check((string)Get(control,"SelectedFilePath")==cal,"Successful save restores selected calibration");
                Check((int)Get(control,"LastChangedFileCount")>0,"Successful save changed intended headers");
                Check(Directory.Exists((string)Get(control,"LastBackupDirectory")),"Successful save has backup");
                Check(before.Skip(HeaderBytes(before,13)).SequenceEqual(File.ReadAllBytes(cal).Skip(HeaderBytes(File.ReadAllBytes(cal),13))),"Calibration sample bytes are identical after save");
                host.Close();
            }
            Console.WriteLine("BROWSER_CALIBRATION_ACTIONS_PASS="+checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    static int HeaderBytes(byte[] value,int rows){int count=0;for(int i=0;i<value.Length;i++)if(value[i]==10&&++count==rows)return i+1;throw new InvalidDataException();}
    static string BuildFixture(string root)
    {
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\n电缆长度: = \"170\"\r\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\t始端\t冷缩\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t冷缩\t170m\r\n",new UTF8Encoding(false));
        string cal=Path.Combine(root,"ALL_5nC.cal");
        var text=new StringBuilder("SR\t100000000\r\nCable length (m)\t170\r\nV (m/usec)\t170\r\nStart_point\t0.000001\r\nFinal_point\t0.000003\r\n");
        for(int i=0;i<8;i++)text.Append("Field").Append(i).Append("\t0\r\n");
        for(int i=0;i<4001;i++){double t=i*.01;double y=Math.Max(0,1-Math.Abs(t-1)/.1)+.4*Math.Max(0,1-Math.Abs(t-3)/.14);text.Append(y.ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");}
        File.WriteAllText(cal,text.ToString(),new UTF8Encoding(false));
        for(int n=0;n<31;n++)
        {
            var dem=new StringBuilder("SR\t100000\r\nHz\t100\r\nC[uF]\t0.1\r\nD-losses\t0.1\r\nV-scale max\t13.4\r\nPD-scale max\t100\r\nCal.File-path\t"+cal+"\r\nDATA-TOTAL\t1000\r\n");
            for(int i=0;i<7;i++)dem.Append("Field").Append(i).Append("\t0\r\n");dem.Append("Voltage\tPD\r\n");
            for(int i=0;i<1000;i++)dem.Append((13.4*Math.Sin(i*.02)).ToString("R",CultureInfo.InvariantCulture)).Append('\t').Append((Math.Sin(i*1.73)*.1).ToString("R",CultureInfo.InvariantCulture)).Append("\r\n");
            string name="A_13.40kV_2026-09-28-10'"+n.ToString("00")+"'00.dem";File.WriteAllText(Path.Combine(root,name),dem.ToString(),new UTF8Encoding(false));
        }
        return cal;
    }
}
