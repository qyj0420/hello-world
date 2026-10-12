using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Offline audit only: inject completed algorithm outputs, never invoke Task17 or hardware.
internal static class AnalysisBatchCompletionAuditSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);}
    static object Completed(Assembly a,Type t,string project,string path,int phase,long id,bool success,string status)
    {
        var request=Activator.CreateInstance(a.GetType("HVX.PdReview.ReviewAnalysisRequest"),true);Set(request,"LocationFilePath",path);Set(request,"ProjectPath",project);Set(request,"AHistoryPhase",phase);Set(request,"LocationDataId",id);
        var result=Activator.CreateInstance(a.GetType("HVX.PdReview.ReviewAnalysisResult"),true);Set(result,"Success",success);Set(result,"Status",status);
        var completed=Activator.CreateInstance(t.GetNestedType("CompletedFile",F),true);Set(completed,"Request",request);Set(completed,"Result",result);return completed;
    }
    [STAThread] static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=System.Text.Encoding.UTF8;
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var a=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));var t=a.GetType("HVX.PdReview.PdReviewControl",true);
            a.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            a.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            Application.EnableVisualStyles();
            bool expectCallbackGap=args.Length>2&&args[2]=="expect-callback-gap";
            foreach(string mode in new[]{"quota","partial","normal","catalog-mismatch","catalog-id-drift","project-drift","foreign-result","old-matrix-rejection",
                "reload-callback-id","reload-callback-phase","results-callback-id","results-callback-phase"}) {
                string root=Path.Combine(qa,mode);Directory.CreateDirectory(root);
                string[] paths={Path.Combine(root,"A_6.09kV_1.loc"),Path.Combine(root,"A_12.18kV_2.loc"),Path.Combine(root,"B_6.09kV_3.loc")};
                using(var review=(Control)Activator.CreateInstance(t))using(var host=new Form{ClientSize=new Size(1180,720),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)}) {
                    review.Dock=DockStyle.Fill;host.Controls.Add(review);host.Show();
                    t.GetProperty("PromptSavedAnalysisOnProjectLoad").SetValue(review,false,null);t.GetProperty("ProjectPath").SetValue(review,root,null);
                    object tree=Get(review,"AnalysisTree");Set(review,"treeSync",true);
                    Check((bool)Call(tree,"LoadRecords",paths,new[]{1,1,2},new[]{6.09,12.18,6.09},new[]{1,2,3},new[]{1d,2d,3d}),"load synthetic A/B catalog");
                    Check((bool)Call(tree,"MarkAnalyzed",new object[]{new[]{paths[0]}}),"first record already analyzed");
                    Check((bool)Call(tree,"SetDataSelected",paths[1],true)&&(bool)Call(tree,"SetDataSelected",paths[2],true),"remaining A/B pending selected");Set(review,"treeSync",false);
                    double[,] prior=null;
                    if(mode=="old-matrix-rejection") {
                        prior=new double[1,36];prior[0,0]=1;prior[0,1]=1;prior[0,2]=6.09;prior[0,3]=30;prior[0,5]=176.8;prior[0,6]=170;prior[0,7]=125e6;
                        prior[0,10]=50;prior[0,11]=250;prior[0,12]=200;prior[0,13]=120;prior[0,15]=100;prior[0,23]=1;prior[0,24]=1;prior[0,32]=511;prior[0,33]=0.8;
                        var wave=new double[1,512];for(int i=0;i<512;i++)wave[0,i]=120*Math.Exp(-Math.Pow((i-50)/8d,2))+80*Math.Exp(-Math.Pow((i-250)/12d,2));
                        Check((bool)Call(review,"LoadDataMatrices",prior,wave),"prior actual MAP loads");prior=(double[,])Get(review,"ResultParameterMatrix");
                    }
                    Call(review,"CaptureAnalysisCatalogSnapshot");
                    var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetNestedType("CompletedFile",F)));
                    string firstPath=mode=="catalog-mismatch"||mode=="old-matrix-rejection"?Path.Combine(root,"A_18.27kV_missing.loc"):paths[1];
                    var first=Completed(a,t,root,firstPath,1,2,true,mode=="quota"?new string('x',1024*1024+1):"{}");list.Add(first);
                    list.Add(Completed(a,t,root,paths[2],2,3,mode!="partial",mode=="partial"?"Synthetic per-file data validation failure":"{}"));
                    if(mode=="catalog-id-drift") {
                        Set(review,"treeSync",true);
                        Check((bool)Call(tree,"LoadRecords",paths,new[]{1,1,2},new[]{6.09,12.18,6.09},new[]{1,2,3},new[]{1d,20d,3d}),"inject changed catalog ID without changing working data");
                        Call(tree,"MarkAnalyzed",new object[]{new[]{paths[0]}});Call(tree,"SetDataSelected",paths[1],true);Call(tree,"SetDataSelected",paths[2],true);Set(review,"treeSync",false);
                    }
                    if(mode=="project-drift") {string changed=Path.Combine(root,"changed-project");Directory.CreateDirectory(changed);Set(review,"projectPath",changed);}
                    if(mode=="foreign-result") {var matrix=new double[1,36];matrix[0,0]=2;matrix[0,1]=3;matrix[0,2]=6.09;Set(Field(first,"Result"),"Parameters",matrix);}
                    bool callbackCase=mode.Contains("-callback-"),callbackFired=false,callbackSucceeded=false;
                    int callbackGeneration=-1;
                    if(callbackCase) {
                        EventHandler changeCatalog=(s,e)=>{
                            if(callbackFired)return;callbackFired=true;callbackGeneration=(int)Field(review,"analysisGeneration");
                            // Only public APIs: a synchronous host replaces catalog identities
                            // without changing paths, working matrices or analysis generation.
                            callbackSucceeded=(bool)Call(tree,"LoadRecords",paths,mode.EndsWith("phase")?new[]{1,3,2}:new[]{1,1,2},
                                new[]{6.09,12.18,6.09},new[]{1,2,3},mode.EndsWith("id")?new[]{1d,20d,3d}:new[]{1d,2d,3d});
                            Call(tree,"MarkAnalyzed",new object[]{mode.StartsWith("results")?paths:new[]{paths[0]}});
                            if(mode.StartsWith("reload")){Call(tree,"SetDataSelected",paths[1],true);Call(tree,"SetDataSelected",paths[2],true);}
                        };
                        t.GetEvent(mode.StartsWith("reload")?"CurrentPdMapChanged":"ResultsChanged").AddEventHandler(review,changeCatalog);
                    }
                    Set(review,"analysisSourceSnapshot",Field(review,"working"));Set(review,"analysisSourceRevision",Get(review,"ResultChangeSequence"));Set(review,"analysisBusy",true);
                    Call(review,"OpenAnalysisProgress",2);object dialog=Field(review,"activeAnalysisDialog");Call(dialog,"UpdateProgress",paths[2],2,2);
                    Check(((ProgressBar)Field(dialog,"overallProgress")).Value==2,"processing progress already reaches 2/2 before result publication");
                    Check(((Control)Field(dialog,"overallLabel")).Text.Contains("已处理")&&((Control)Field(dialog,"stateLabel")).Text.Contains("整理结果"),"100% is explicitly processed, not committed success");
                    Call(review,"CompleteProjectAnalysis",Field(review,"analysisGeneration"),list,true,false,2,"");
                    string[] analyzed=(string[])Call(tree,"GetAnalyzedDataPaths"),pending=(string[])Call(tree,"GetSelectedDataPaths");
                    string status=(string)Get(review,"AnalysisStatus"),warning=(string)Field(review,"analysisOutcomeWarning");
                    Console.WriteLine(mode+" | processed="+Get(review,"AnalysisCompletedFileCount")+" | analyzed="+analyzed.Length+" | selectedPending="+pending.Length+" | status="+status+" | warning="+warning+" | LastError="+Get(review,"LastError"));
                    Check(!(bool)Get(review,"IsAnalysisRunning"),"batch UI released");
                    Check((int)Get(review,"SourceRowCount")==(prior==null?0:1),"no uncommitted MAP data published or prior MAP lost");
                    Check(analyzed.Contains(paths[0]),"previous analysis retained");
                    if(mode=="quota") {
                        Check(analyzed.Length==1&&pending.Length==2,"quota failure reproduces first-only analyzed with other A/B still selected");
                        Check(warning.Contains("分析未能正常完成")&&((string)Get(review,"StatusBarText")).Contains("分析未能正常完成"),"current code shows durable failure, not success");
                        Check(((Control)Field(review,"analysisProgress")).Visible,"failure label visible after progress closes");
                        Console.WriteLine("quota-diagnostic="+Get(review,"AnalysisDiagnosticText"));
                        Check(((string)Get(review,"AnalysisDiagnosticText")).Contains("status"),"quota diagnostic retained");
                    } else if(mode=="partial") {
                        Check(analyzed.Length==2&&pending.SequenceEqual(new[]{paths[2]}),"individual failure preserves successful status and failed selection");
                        Check(status.Contains("1 / 2")&&warning.Contains("失败 1"),"partial completion visibly differs from all-success");
                    } else if(mode=="normal") {
                        Check(analyzed.Length==3&&pending.Length==0,"normal no-pulse success marks every A/B record analyzed");
                        Check(warning.Length==0&&status.Contains("2 / 2"),"normal completion has no failure warning");
                    } else if(callbackCase) {
                        Check(callbackFired&&callbackSucceeded,"real synchronous host callback replaced same-path catalog");
                        Check(callbackGeneration==(int)Field(review,"analysisGeneration"),"callback kept analysis generation unchanged");
                        if(expectCallbackGap)Check(warning.Length==0&&status.Contains("分析完成：2 / 2"),"PRE-FIX: same-path callback drift still falsely reports success");
                        else {
                            Check(warning.Contains("分析未能正常完成")&&!status.Contains("分析完成：2 / 2"),"callback identity drift cannot report success");
                            if(mode.StartsWith("reload"))Check(((Dictionary<string,string>)Field(review,"algorithmDiagnostics")).Count==0&&analyzed.Length==1&&pending.Length==2,"reload callback rejected before completion flags and diagnostics publish");
                            // ResultsChanged occurs after publication. The host's own restored
                            // flags are not rolled back; require an honest failed final outcome.
                        }
                    } else {
                        Check(analyzed.Length==1&&pending.Length==2,"identity rejection keeps all new records pending and earlier analysis retained");
                        Check(warning.Contains("分析未能正常完成")&&!status.Contains("分析完成：2 / 2"),"catalog/result drift cannot be falsely reported as success");
                        Check(((Dictionary<string,string>)Field(review,"algorithmDiagnostics")).Count==0,"invalid targets rejected before diagnostics publication");
                        if(prior!=null)Check(((double[,])Get(review,"ResultParameterMatrix")).Cast<double>().SequenceEqual(prior.Cast<double>()),"previous matrix is byte-value identical after rejected batch");
                    }
                    using(var image=new Bitmap(review.Width,review.Height)){review.DrawToBitmap(image,review.ClientRectangle);image.Save(Path.Combine(qa,mode+".png"));}
                }
            }
            Console.WriteLine("ANALYSIS_BATCH_COMPLETION_AUDIT_PASS="+checks);return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
