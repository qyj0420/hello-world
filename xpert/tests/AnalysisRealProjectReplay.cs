using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

// Explicitly supplied field-data replay. Work only on isolated copies; no device calls.
internal static class AnalysisRealProjectReplay
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static string Hash(string p){using(var s=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s));}
    static string CheckAcceptedDisplay(object review,object tree)
    {
        var matrix=(double[,])Get(review,"ResultParameterMatrix");
        var nodes=((System.Collections.IEnumerable)Call(tree,"AllNodes")).Cast<TreeNode>().ToArray();
        int accepted=0,pending=0;
        foreach(var node in nodes){
            object map=node.Tag.GetType().GetField("Map",F).GetValue(node.Tag);if(map==null)continue;
            int row=(int)map.GetType().GetField("RowIndex",F).GetValue(map);
            int expected=matrix[row,35]==1?8:7;
            if(node.StateImageIndex!=expected)throw new Exception("Accepted display disagrees with result matrix at row "+row);
            if(expected==8)accepted++;else pending++;
        }
        if(accepted+pending!=matrix.GetLength(0))throw new Exception("Not all result rows are represented in the tree");
        if(((bool[])Get(review,"AcceptanceConfirmedRows")).Any(value=>value))throw new Exception("Automatic analysis invented manual acceptance");
        return "ACCEPTED_DISPLAY_PASS="+accepted+"; PENDING="+pending;
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            if(args.Length<3||args.Length>4)throw new ArgumentException("build root, evidence root, source project, optional verify required");
            bool verify=args.Length==4&&args[3].StartsWith("verify",StringComparison.Ordinal);
            string qa=Path.GetFullPath(args[1]);if(qa.IndexOf("\\build\\",StringComparison.OrdinalIgnoreCase)<0||Directory.Exists(qa))throw new ArgumentException("Use a new isolated build evidence path.");
            Directory.CreateDirectory(qa);string project=Path.Combine(qa,"project-copy");Directory.CreateDirectory(project);
            AppDomain.CurrentDomain.FirstChanceException+=(s,e)=>{if(e.Exception.Message.Contains("maxJsonLength"))File.AppendAllText(Path.Combine(qa,"serialization-failures.txt"),e.Exception+"\n"+Environment.StackTrace+"\n");};
            var files=Directory.GetFiles(args[2]).Where(p=>Path.GetFileName(p)=="ITEM.in"||Path.GetFileName(p)=="CABLE.in"||new[]{".cal",".loc",".dem"}.Contains(Path.GetExtension(p).ToLowerInvariant())).ToArray();
            var hashes=files.ToDictionary(p=>p,Hash);
            foreach(string p in files)File.Copy(p,Path.Combine(project,Path.GetFileName(p)));
            var asm=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            foreach(int method in args.Length==4&&args[3].EndsWith("-b",StringComparison.Ordinal)?new[]{7}:new[]{5,7})foreach(int mode in verify?new[]{0,1,2}:new[]{0,1}){
                bool wholeBatch=mode!=0;
                using(var review=(Control)Activator.CreateInstance(asm.GetType("HVX.PdReview.PdReviewControl",true)))
                using(var host=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)}){
                    Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"EnableAiFeatures",false);Set(review,"EmbeddedMode",true);
                    review.Dock=DockStyle.Fill;host.Controls.Add(review);host.Show();Set(review,"ProjectPath",project);
                    Set(review,"AnalysisAlgorithmIndex",method);Set(review,"AnalysisMultipleReflectionSuppressionCount",-1);
                    Call(review,"SetAnalysisSettings",40d,40d,3d,30,true,new[]{true,false,true,false});
                    object tree=Get(review,"AnalysisTree");var paths=(string[])Call(tree,"GetDataPaths");
                    string chosen=paths.First(p=>Path.GetFileName(p)=="A_18.30kV_2026-05-05-16'12'16.loc");
                    foreach(string p in paths)Call(tree,"SetDataSelected",p,wholeBatch||p==chosen);
                    var timer=Stopwatch.StartNew();bool started=(bool)Call(review,"StartSelectedAnalysis",true);
                    bool canceled=false;
                    while((bool)Get(review,"IsAnalysisRunning")&&timer.Elapsed.TotalSeconds<240){Application.DoEvents();Thread.Sleep(10);
                        if(mode==2&&!canceled&&(int)Get(review,"AnalysisCompletedFileCount")>=25){canceled=(bool)Call(review,"CancelSelectedAnalysis");}}
                    string name="method-"+method+(mode==2?"-canceled":wholeBatch?"-batch":"-single");
                    string receipt="Started="+started+"\nRunning="+Get(review,"IsAnalysisRunning")+"\nRows="+Get(review,"SourceRowCount")+"\nCompleted="+Get(review,"AnalysisCompletedFileCount")+"\nStatus="+Get(review,"AnalysisStatus")+"\nLastError="+Get(review,"LastError")+"\nBottomStatus="+Get(review,"StatusBarText")+"\nAnalyzed="+((string[])Call(tree,"GetAnalyzedDataPaths")).Length+"\nSeconds="+timer.Elapsed.TotalSeconds;
                    File.WriteAllText(Path.Combine(qa,name+".txt"),receipt+"\n"+Get(review,"AnalysisDiagnosticText"));Console.WriteLine(name+": "+receipt);
                    using(var image=new Bitmap(review.Width,review.Height)){review.DrawToBitmap(image,review.ClientRectangle);image.Save(Path.Combine(qa,name+".png"));}
                    if((bool)Get(review,"IsAnalysisRunning"))throw new TimeoutException("bounded field replay timed out");
                    if(verify){
                        int rows=(int)Get(review,"SourceRowCount"),analyzed=((string[])Call(tree,"GetAnalyzedDataPaths")).Length;
                        if(!started||rows==0||analyzed==0||mode==1&&analyzed<80||mode==2&&(!canceled||analyzed>=80))throw new Exception("Field batch outcome regression: "+receipt);
                        string displayProof=CheckAcceptedDisplay(review,tree);Console.WriteLine(displayProof);File.AppendAllText(Path.Combine(qa,name+".txt"),"\n"+displayProof+"\n");
                        var evidence=(System.Collections.Generic.Dictionary<string,string>)review.GetType().GetField("algorithmDiagnostics",F).GetValue(review);
                        var rawEvidence=evidence.ToDictionary(p=>p.Key,p=>p.Value);
                        if(mode==1){
                            object[] session={null,null,null};
                            if(!(bool)Call(review,"ExportAnalysisSessionUtf8",session))throw new Exception("Session export: "+Get(review,"LastError"));
                            if(!(bool)Call(review,"LoadAnalysisSessionUtf8",project,session[0],session[1],session[2]))throw new Exception("Session restore: "+Get(review,"LastError"));
                            evidence=(System.Collections.Generic.Dictionary<string,string>)review.GetType().GetField("algorithmDiagnostics",F).GetValue(review);
                            if(evidence.Count!=rawEvidence.Count||rawEvidence.Any(p=>!evidence.ContainsKey(p.Key)||evidence[p.Key]!=p.Value)||(int)Get(review,"SourceRowCount")!=rows)throw new Exception("Full diagnostics or rows changed during session round trip");
                            Console.WriteLine("RESTORED_"+CheckAcceptedDisplay(review,tree));
                            Console.WriteLine("ROUND_TRIP_PASS="+name+"; ROWS="+rows+"; DIAGNOSTIC_FILES="+evidence.Count+"; UTF8_BYTES="+((byte[])session[2]).Length);
                        }
                    }
                }
            }
            foreach(var p in hashes)if(Hash(p.Key)!=p.Value)throw new Exception("Original input changed: "+p.Key);
            Console.WriteLine("ORIGINAL_INPUTS_UNCHANGED="+hashes.Count);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
