using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class AnalysisBatchEvidenceSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(Type t,object o,string n,params object[] a){return t.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(TargetInvocationException e){rejected=e.InnerException is ArgumentException||e.InnerException is InvalidDataException||e.InnerException is FormatException||e.InnerException is InvalidOperationException;}Check(rejected,why);}
    static string Pack(byte[] b){using(var m=new MemoryStream()){using(var g=new GZipStream(m,CompressionLevel.Optimal,true))g.Write(b,0,b.Length);return "HVX.Diagnostics.Gzip.v1:"+Convert.ToBase64String(m.ToArray());}}
    [STAThread] static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);string root=Path.Combine(qa,"project");Directory.CreateDirectory(root);root+=Path.DirectorySeparatorChar;
            var asm=Assembly.LoadFrom(Path.Combine(args[0],"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));var t=asm.GetType("HVX.PdReview.PdReviewControl",true);
            asm.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            asm.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"logs")));
            var input=new Dictionary<string,string>();
            for(int i=0;i<93;i++)input["record-"+i+".loc"]="{\"source_identity_requires_review\":true,\"rows\":["+String.Join(",",Enumerable.Repeat("{\"candidate\":true,\"acceptance\":\"not_physical_confirmation\",\"note\":\"完整复核证据\"}",1800))+"],\"id\":"+i+"}";
            Check(Encoding.UTF8.GetByteCount(String.Join("",input.Values))>4*1024*1024,"fixture exceeds original quota");
            string json=(string)Call(t,null,"SerializeAlgorithmDiagnostics",input);
            Check(Encoding.UTF8.GetByteCount(json)<4*1024*1024&&json.Contains("HVX.Diagnostics.Gzip.v1:"),"bounded lossless representation fits original session quota");
            string[] paths=input.Keys.Select(p=>Path.Combine(root,p)).ToArray();
            var back=(Dictionary<string,string>)Call(t,null,"ParseAlgorithmDiagnostics",json,root,paths);
            Check(back.Count==93&&input.All(p=>back[p.Key]==p.Value),"all 93 complete Unicode statuses survive byte-exact round trip");
            var legacy=new Dictionary<string,string>{{"record-0.loc","{\"ok\":true}"}};
            string legacyJson=new JavaScriptSerializer().Serialize(legacy);
            Check(((Dictionary<string,string>)Call(t,null,"ParseAlgorithmDiagnostics",legacyJson,root,paths))["record-0.loc"]==legacy["record-0.loc"],"legacy plain diagnostics remain readable");
            var reserved=new Dictionary<string,string>{{"record-0.loc","HVX.Diagnostics.Gzip.v1:literal-not-compressed"}};
            Check(((Dictionary<string,string>)Call(t,null,"ParseAlgorithmDiagnostics",Call(t,null,"SerializeAlgorithmDiagnostics",reserved),root,paths))["record-0.loc"]==reserved["record-0.loc"],"reserved prefix is safely escaped by encoding once");
            foreach(string bad in new[]{"HVX.Diagnostics.Gzip.v1:%%",Pack(new byte[]{0xff}),Pack(Encoding.UTF8.GetBytes(new string('x',1024*1024+1)))}){
                string invalid=new JavaScriptSerializer().Serialize(new Dictionary<string,string>{{"record-0.loc",bad}});
                Reject(()=>Call(t,null,"ParseAlgorithmDiagnostics",invalid,root,paths),"invalid base64, UTF8 or inflated size rejected");
            }
            Reject(()=>Call(t,null,"ParseAlgorithmDiagnostics","{\"../outside.loc\":\"x\"}",root,paths),"relative traversal rejected");
            var oversized=new Dictionary<string,string>{{"record-0.loc",new string('x',1024*1024+1)}};
            Reject(()=>Call(t,null,"SerializeAlgorithmDiagnostics",oversized),"per-file source quota retained");
            var bomb=new Dictionary<string,string>();string packed=Pack(Encoding.UTF8.GetBytes(new string('x',1024*1024)));
            for(int i=0;i<65;i++)bomb[i+".loc"]=packed;
            Reject(()=>Call(t,null,"ParseAlgorithmDiagnostics",new JavaScriptSerializer().Serialize(bomb),root,paths),"cumulative decompression quota enforced even for nonretained entries");
            Application.EnableVisualStyles();
            using(var control=(Control)Activator.CreateInstance(t))using(var host=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)}){
                control.Dock=DockStyle.Fill;host.Controls.Add(control);host.Show();
                t.GetProperty("PromptSavedAnalysisOnProjectLoad").SetValue(control,false,null);t.GetProperty("ProjectPath").SetValue(control,root,null);
                object tree=Get(control,"AnalysisTree");
                Check((bool)Call(tree.GetType(),tree,"LoadRecords",new[]{paths[0]},new[]{1},new[]{12d},new[]{1},new[]{1d}),"quota fixture has a valid current catalog");
                Call(t,control,"CaptureAnalysisCatalogSnapshot");
                var request=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisRequest"),true);Set(request,"LocationFilePath",paths[0]);Set(request,"ProjectPath",root);Set(request,"AHistoryPhase",1);Set(request,"LocationDataId",1L);
                var result=Activator.CreateInstance(asm.GetType("HVX.PdReview.ReviewAnalysisResult"),true);Set(result,"Success",true);Set(result,"Status",new string('x',1024*1024+1));
                var completedType=t.GetNestedType("CompletedFile",F);var completed=Activator.CreateInstance(completedType,true);Set(completed,"Request",request);Set(completed,"Result",result);
                var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(completedType));list.Add(completed);
                Set(control,"analysisSourceSnapshot",Field(control,"working"));Set(control,"analysisSourceRevision",Get(control,"ResultChangeSequence"));Set(control,"analysisBusy",true);
                Call(t,control,"CompleteProjectAnalysis",Field(control,"analysisGeneration"),list,true,false,1,"");
                Check(!(bool)Get(control,"IsAnalysisRunning")&&(int)Get(control,"SourceRowCount")==0,"failed merge ends busy state without publishing bogus results");
                Check(((string)Get(control,"StatusBarText")).Contains("分析未能正常完成"),"failed merge remains visible instead of Ready");
                Check(((Control)Field(control,"analysisProgress")).Visible,"sidebar failure message is visible");
                Set(control,"statusHoverControl",Field(control,"automaticAnalysisButton"));Call(t,control,"RefreshBottomStatus");
                Check(((string)Get(control,"StatusBarText")).Contains("分析未能正常完成"),"hover guidance cannot hide a merge failure");
                Call(t,control,"ClearAllAnalysis");Check((string)Field(control,"analysisOutcomeWarning")=="","explicit reset clears stale warning");
            }
            Console.WriteLine("ANALYSIS_BATCH_EVIDENCE_PASS="+checks);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
