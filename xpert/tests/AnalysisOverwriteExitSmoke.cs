using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class AnalysisOverwriteExitSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static bool Dirty(object o){return (bool)Get(o,"HasUnsavedAnalysisChanges");}
    static bool ReportSelection(object reader,string archive,int sequence)
    {var package=Call(reader,"Load",archive);var image=((Array)Get(package,"ReportImages")).Cast<object>().Single(i=>(int)Get(i,"Sequence")==sequence);return (bool)Get(image,"IncludedInReport");}
    static Dictionary<string,string> Snapshot(string root)
    {return Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(f=>f.Substring(root.Length),f=>{using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)));},StringComparer.OrdinalIgnoreCase);}
    static bool Same(Dictionary<string,string> expected,string root){var actual=Snapshot(root);return actual.Count==expected.Count&&expected.All(p=>actual.ContainsKey(p.Key)&&actual[p.Key]==p.Value);}
    static void Change(Control review,double value){Set(review,"AnalysisCorrelationErrorPercent",value);Check(Dirty(review),"parameter adjustment is dirty");}
    static void CheckLongArchivePaths(Type type,string qa,double[,] originalParameters,double[,] waves)
    {
        foreach(int length in new[]{178,202}) {
            Check(qa.Length+2<length,"Path-boundary fixture has a representable isolated parent");
            string project=Path.Combine(qa,new String(length==178?'b':'x',length-qa.Length-1));Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t414\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(project,"A_13.40kV_1.loc"),new byte[256]);var originals=Snapshot(project);
            using(Control review=(Control)Activator.CreateInstance(type)) {
                review.Size=new Size(1280,720);Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);
                object tree=Get(review,"AnalysisTree");var catalog=(double[,])Call(tree,"GetDataCatalog");var parameters=(double[,])originalParameters.Clone();parameters[0,0]=catalog[0,0];parameters[0,1]=catalog[0,1];
                Check((bool)Call(review,"LoadDataMatrices",parameters,waves),"Long-path analysis fixture loads");
                Check((bool)Call(review,"RequestPdMapReportChart"),"Long-path fixture includes full report attachment");
                bool saved=(bool)Call(review,"SaveNamedAnalysis","path-boundary");
                if(length==178) {
                    Check(saved,"Representable deep archive succeeds with short staging names: "+Get(review,"AnalysisArchiveStatus"));
                    string id=(string)Get(review,"LoadedAnalysisId"),archive=Path.Combine(project,"Analysis report",id);var original=Snapshot(archive);
                    Change(review,13);Check((bool)Call(review,"OverwriteLoadedAnalysis"),"Deep archive overwrite succeeds without duplicated archive ID in backup path: "+Get(review,"AnalysisArchiveStatus"));
                    string backup=(string)Get(review,"LastAnalysisBackupPath");Check(Same(original,backup),"Deep archive backup retains every original member");
                    Check(Directory.GetFiles(backup,"*",SearchOption.AllDirectories).All(path=>path.Length<260),"All deep recovery members fit the actual Windows path budget");
                } else {
                    Check(!saved&&Dirty(review),"Unrepresentable final/staging/recovery path fails and retains dirty analysis");
                    Check(!Directory.Exists(Path.Combine(project,"Analysis report"))&&Same(originals,project),"Overlong archive rejected before creating lock or temporary/archive directories");
                    string error=(string)Get(review,"AnalysisArchiveStatus");Check(error.Contains("path")||error.Contains("路径"),"Path-budget rejection explains the user-visible cause");
                }
            }
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[1]),"overwrite-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            var assembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var type=assembly.GetType("HVX.PdReview.PdReviewControl",true);
            var reportAssembly=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            object reportReader=Activator.CreateInstance(reportAssembly.GetType("HVX.ReportGenerator.AnalysisPackageReader",true));
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t414\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
            string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);var raw=Snapshot(project);
            using(Control review=(Control)Activator.CreateInstance(type)) {
                review.Size=new Size(1280,720);Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);
                object tree=Get(review,"AnalysisTree");double[,] catalog=(double[,])Call(tree,"GetDataCatalog");Check(catalog.GetLength(0)==1,"Fixture catalog available");
                var p=new double[1,36];var w=new double[1,2048];p[0,0]=catalog[0,0];p[0,1]=catalog[0,1];p[0,2]=13.4;p[0,3]=45;p[0,5]=414;p[0,6]=170;p[0,7]=125e6;p[0,9]=0;p[0,10]=100;p[0,11]=710;p[0,12]=4.88;p[0,13]=200;p[0,15]=180;p[0,16]=180;p[0,20]=100;p[0,21]=.92;p[0,32]=2047;p[0,33]=.91;
                for(int i=0;i<2048;i++)w[0,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
                Check((bool)Call(review,"LoadDataMatrices",p,w),"Analysis fixture loads");Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});Call(review,"SelectSourceRow",0);
                Check((bool)Call(review,"RequestPdMapReportChart"),"Actual waveform attachment captured: "+Get(review,"ChartReportStatusMessage"));
                Check((bool)Call(review,"SaveNamedAnalysis","original"),"Initial named save succeeds");string id=(string)Get(review,"LoadedAnalysisId");
                string archive=Path.Combine(project,"Analysis report",id);File.WriteAllText(Path.Combine(archive,"operator-note.txt"),"retain complete backup",Encoding.UTF8);
                object initialInfo=((Array)Call(review,"EnumerateSavedAnalyses")).GetValue(0);string originalCreated=(string)Get(initialInfo,"CreatedUtc"),originalName=(string)Get(initialInfo,"Name");
                Check((bool)Call(review,"LoadSavedAnalysis",id)&&!Dirty(review),"Saved analysis reloads clean");
                var original=Snapshot(archive);int prompts=0;string choice="Cancel";
                Set(review,"AnalysisLeaveChoiceProvider",(Func<bool,string>)(canOverwrite=>{prompts++;Check(canOverwrite,"Loaded identity supports overwrite");return choice;}));
                Check((bool)Call(review,"ConfirmLeaveAnalysis")&&prompts==0,"Unchanged analysis exits without prompt");
                Change(review,13);Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review)&&Same(original,archive),"Cancel leaves dirty data and original archive unchanged");
                choice="Discard";Check((bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review)&&Same(original,archive),"Explicit discard permits exit without writing or falsely clearing memory");
                choice="Overwrite";
                using(var guard=new FileStream(Path.Combine(project,"Analysis report",".save.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review)&&Same(original,archive),"Concurrent save lock failure blocks exit");
                type.GetField("BeforeAnalysisReplacementPublishForTests",All).SetValue(review,(Action<string>)(stage=>{throw new IOException("Injected rename failure");}));
                Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review)&&Same(original,archive),"Publish failure restores original directory and blocks exit");
                type.GetField("BeforeAnalysisReplacementPublishForTests",All).SetValue(review,null);
                string selection=Path.Combine(archive,"selection.json");byte[] beforeSelection=File.ReadAllBytes(selection);File.AppendAllText(selection," ");
                Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review),"External archive change prevents stale overwrite");
                Check(File.ReadAllText(selection).EndsWith(" "),"Newer external content not overwritten");File.WriteAllBytes(selection,beforeSelection);
                Check((bool)Call(review,"ConfirmLeaveAnalysis")&&!Dirty(review),"Overwrite succeeds and permits exit");
                string overwriteStatus=(string)Get(review,"AnalysisArchiveStatus");
                Check((string)Get(review,"LoadedAnalysisId")==id,"Overwrite preserves archive identity");
                object replacedInfo=((Array)Call(review,"EnumerateSavedAnalyses")).GetValue(0);
                Check((string)Get(replacedInfo,"Name")==originalName&&(string)Get(replacedInfo,"CreatedUtc")==originalCreated,"Overwrite preserves archive name and creation timestamp");
                string backup=(string)Get(review,"LastAnalysisBackupPath");Check(Directory.Exists(backup)&&Same(original,backup),"Entire previous archive retained including unlisted operator attachment");
                Check(overwriteStatus.Contains(backup),"Successful overwrite reports recovery path");
                Check(((Array)Call(review,"EnumerateSavedAnalyses")).Length==1,"Backup and pending directories are not ordinary analyses");
                var overwritten=Snapshot(archive);byte[] savedSelection=File.ReadAllBytes(Path.Combine(archive,"selection.json"));
                int chartSequence=(int)Get(((Array)Call(review,"GetPendingReportCharts")).GetValue(0),"Sequence");
                Check((bool)Call(review,"SetReportChartSelected",chartSequence,false)&&Dirty(review),"Attachment selection is pending analysis data");
                Check(File.ReadAllBytes(Path.Combine(archive,"selection.json")).SequenceEqual(savedSelection),"Saved report consumer still sees last saved selection");
                Check(ReportSelection(reportReader,archive,chartSequence),"Real report reader ignores pending deselection and reads saved attachment");
                choice="SaveNew";Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(name=>null));
                Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review),"Cancelled save-as naming blocks exit");
                Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(name=>"original"));
                Check(!(bool)Call(review,"ConfirmLeaveAnalysis")&&Dirty(review),"Duplicate save-as name blocks exit rather than overwrite");
                Set(review,"AnalysisSaveNameProvider",(Func<string,string>)(name=>"new-copy"));
                Check((bool)Call(review,"ConfirmLeaveAnalysis")&&!Dirty(review),"Save-as persists pending change and permits exit");
                Check((string)Get(review,"LoadedAnalysisId")!=id&&Same(overwritten,archive),"Save-as keeps original archive intact");
                string next=(string)Get(review,"LoadedAnalysisId");Check((bool)Call(review,"LoadSavedAnalysis",next),"Newly written archive round-trips");
                Check(!(bool)Call(review,"IsReportChartSelected",chartSequence),"New archive persists changed attachment selection");
                Check(!ReportSelection(reportReader,Path.Combine(project,"Analysis report",next),chartSequence)&&ReportSelection(reportReader,archive,chartSequence),"Real report reader observes only each explicitly saved archive selection");
                Check(((string[])Call(reportReader,"EnumeratePackagePaths",project)).Length==2,"Report picker excludes overwrite recovery and pending directories");
                Check(((Array)Call(review,"EnumerateSavedAnalyses")).Length==2,"Only old and new named analyses enumerate");
                using(Form dialog=(Form)Call(review,"CreateAnalysisLeaveDialog",true)) {
                    dialog.ShowInTaskbar=false;dialog.Opacity=0;dialog.Show();Application.DoEvents();dialog.PerformLayout();
                    foreach(string name in new[]{"SaveNewAnalysis","OverwriteCurrentAnalysis","DiscardAnalysisChanges","CancelAnalysisExit"}) {
                        var button=dialog.Controls.Find(name,true).Single();Check(button.Visible&&button.Enabled&&button.Width>300&&button.Height>=30,"Four-way choice readable: "+name);
                        Check(button.Parent.ClientRectangle.Contains(button.Bounds),"Choice inside layout: "+name);
                    }
                    using(var image=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(image,new Rectangle(Point.Empty,dialog.Size));image.Save(Path.Combine(qa,"four-way-exit.png"));}
                    dialog.Close();
                }
                foreach(var item in raw){string path=project+item.Key;using(var sha=SHA256.Create())Check(Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)))==item.Value,"Raw project file unchanged");}
                CheckLongArchivePaths(type,qa,p,w);
            }
            Console.WriteLine("ANALYSIS_OVERWRITE_EXIT_PASS="+checks+"; evidence="+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
