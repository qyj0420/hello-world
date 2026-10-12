using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HVX.PdReview;

// Pure offline contract + real WinForms event-loop tests. All learning data is
// clearly synthetic and isolated under the caller's QA directory, never a user
// project/model, device SDK or high-voltage operation.
internal static class Task17NeuralHostSmoke
{
    static int checks;static readonly List<string> errors=new List<string>();
    static readonly JavaScriptSerializer J=new JavaScriptSerializer{MaxJsonLength=24*1024*1024};
    static Assembly A=typeof(PdReviewControl).Assembly;
    static Type Repo=A.GetType("HVX.PdReview.ReviewReflectionLearningRepository",true),Bridge=A.GetType("HVX.PdReview.ReviewReflectionLearningBridge",true),Store=A.GetType("HVX.PdReview.ReviewHybridLearningStore",true);
    static void Check(bool pass,string text){checks++;if(!pass)errors.Add(text);}
    static object Call(Type type,object target,string name,params object[] values)
    {try{return type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(target,values);}catch(TargetInvocationException ex){throw ex.InnerException??ex;}}
    static object ICall(object target,string name,params object[] values){return Call(target.GetType(),target,name,values);}
    static void Set(object target,string name,object value){target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance).SetValue(target,value);}
    static object Get(object target,string name){return target.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance).GetValue(target);}
    static void Throws(Action run,string text){bool did=false;try{run();}catch{did=true;}Check(did,text);}
    static string Hash(int n){return n.ToString("x64",CultureInfo.InvariantCulture);}
    static Dictionary<string,object> Obj(string json){return J.Deserialize<Dictionary<string,object>>(json);}
    static double[] Signal(){var values=new double[512];for(int i=0;i<values.Length;i++)values[i]=.018*Math.Sin(i*1.714)+.011*Math.Cos(i*2.13)+100*Math.Exp(-Math.Pow((i-100)/2d,2))+55*Math.Exp(-Math.Pow((i-240)/2d,2));return values;}
    static string Sample(int n,string source="manual_confirmed",int method=6)
    {
        var data=new Dictionary<string,object>{{"schema","reflection-learning-input-v1"},{"scope_key","host-synthetic"},{"project_key","not-user-data"},{"record_key","fixture-"+n},{"source_sha256",Hash(n)},{"calibration_sha256",Hash(999)},
            {"discharge_type","synthetic-not-field-evidence"},{"algorithm_index",method},{"timing_mode","peak"},{"incident_index",100},{"source_incident_index",10000},{"chosen_reflection_index",240},{"sample_rate_hz",125000000d},
            {"exact_round_trip_samples",200d},{"cable_length_m",135.68},{"selected_threshold_pc",2d},{"threshold_mode","manual"},{"threshold_human_confirmed",source=="manual_confirmed"},
            {"label_source",source},{"pair_label",source=="manual_rejected"?"negative":"positive"},{"operator_id","synthetic-host-test"},{"endpoint_band_m",10d}};
        return (string)Call(Bridge,null,"Invoke","BuildSample",new object[]{Signal(),J.Serialize(data)});
    }
    static string Token(string root){return (string)Call(Repo,null,"CurrentToken",root);}
    static string Model(string root){object[] args={root,null};var method=Repo.GetMethod("Model",BindingFlags.Static|BindingFlags.NonPublic);return (string)method.Invoke(null,args);}
    static Dictionary<string,object>[] Samples(string root){return (Dictionary<string,object>[])Call(Repo,null,"Samples",root);}
    static string Add(string root,string sample,string source,Func<bool> allowed=null){return (string)Call(Repo,null,"Add",root,sample,source,allowed,null);}
    static void Wait(Task task)
    {var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<30000){Application.DoEvents();Thread.Sleep(1);}Check(task.IsCompleted,"background task completed within bounded QA time");task.GetAwaiter().GetResult();}
    static void Until(Func<bool> condition,string text)
    {var clock=Stopwatch.StartNew();while(!condition()&&clock.ElapsedMilliseconds<30000){Application.DoEvents();Thread.Sleep(1);}Check(condition(),text);}
    static double[] Wave(int incident,int reflection,double amplitude)
    {var data=new double[4096];for(int i=0;i<data.Length;i++){double x=(i-incident)/10d,y=(i-reflection)/10d;data[i]=amplitude*(Math.Exp(-x*x/2)-.6*Math.Exp(-(x-3)*(x-3)/2))+amplitude*.6*(Math.Exp(-y*y/2)-.6*Math.Exp(-(y-3)*(y-3)/2))+.00001*amplitude*Math.Sin(i*.73);}return data;}
    static void WriteWave(string path,string[] header,double[] values)
    {File.WriteAllText(path,String.Join("\r\n",header)+"\r\n"+String.Join("\r\n",values.Select(v=>v.ToString("R",CultureInfo.InvariantCulture))),new UTF8Encoding(true));}
    static string Fixture(string parent)
    {
        string root=Path.Combine(parent,"real-offline-analysis");Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"ITEM.in"),"[ITEM]\r\nCable length = 170\r\n",new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\nVelocity: 170 m/us\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\r\n1\tEnd\tTerminal\t170m\r\n",new UTF8Encoding(true));
        WriteWave(Path.Combine(root,"ALL_1nC.cal"),new[]{"Cable length\t170","Cal.value\t1000","Velocity (m/us)\t170","NI_Calib_factor\t1","Start_point (s)\t0.000005","Final_point (s)\t0.000007","SR\t100000000","Calib_Attenuation\t4.436974992","Phase\tALL","Units\tV","Reserved\t0","Reserved2\t0","Data"},Wave(500,700,.01));
        WriteWave(Path.Combine(root,"A_12.0kV_1.loc"),new[]{"Cable length\t170","Velocity (m/us)\t170","Delta x\t1E-8","Phase\t0","Voltage\t12.0","Reserved1\t0","Reserved2\t0","Reserved3\t0","Reserved4\t0","Reserved5\t0","Data"},Wave(500,660,1000).Select(v=>v*100000).ToArray());return root;
    }
    static IEnumerable<Control> Descendants(Control control){foreach(Control child in control.Controls){yield return child;foreach(var next in Descendants(child))yield return next;}}
    static void RepositoryCases(string parent)
    {
        for(int round=0;round<5;round++)
        {
            string root=(string)Call(Repo,null,"RootFor",Path.Combine(parent,"s"+round));string initial=Token(root);
            Check(Model(root)==""&&!Directory.Exists(root),"empty getter creates no model/store/weights "+round);
            string positive=Sample(20+round),negative=Sample(50+round,"manual_rejected");Check(Convert.ToBoolean(Obj(positive)["ok"]),"real engine human sample build "+round);
            Throws(()=>Add(root,Sample(11,"weak_bootstrap"),"weak_bootstrap"),"weak labels never enter neural store "+round);
            Throws(()=>Add(root,Sample(12,"manual_confirmed",0),"manual_confirmed"),"other physical method rejected "+round);
            Throws(()=>Add(root,positive,"manual_rejected"),"authority does not match confirmed action "+round);
            Throws(()=>Add(root,positive,"manual_confirmed",()=>false),"canceled human label never commits "+round);
            Check(Samples(root).Length==0,"all invalid attempts preserve empty active set "+round);
            Add(root,positive,"manual_confirmed");Check(Samples(root).Length==1&&Model(root)==""&&!Directory.Exists(Path.Combine(root,"Models")),"label save has no hidden training "+round);
            Check(Token(root)!=initial,"label digest changed "+round);
            string model=(string)Call(Repo,null,"Rebuild",root,null,null);var parsed=Obj(model);
            Check(Convert.ToBoolean(parsed["ok"])&&(string)parsed["schema"]=="reflection-neural-model-v1","explicit real neural model schema "+round);
            Check(Model(root)==model,"atomic explicit model publication "+round);string current=Token(root);
            Throws(()=>Call(Repo,null,"Rebuild",root,(Func<bool>)(()=>false),null),"canceled train not published "+round);
            Check(Token(root)==current,"canceled train preserves current model "+round);
            Add(root,negative,"manual_rejected");Check(Samples(root).Length==2&&Model(root)=="","new human label immediately stales previous model "+round);
            int n=(int)Call(Repo,null,"Remove",root,(Func<Dictionary<string,object>,bool>)(e=>true),true,null);
            Check(n==1&&Samples(root).Length==1&&Directory.GetDirectories(Path.Combine(root,"history")).Length==1,"undo is recoverable without training "+round);
            Check(Model(root)==model,"exact restored label digest can use its prior model "+round);
            var corrupt=Obj(positive);corrupt["position_m"]=1.23;
            Throws(()=>Add(root,J.Serialize(corrupt),"manual_confirmed"),"semantic checksum before commit "+round);
            Throws(()=>Call(Store,null,"Add",root,"reflection_neural_sample_v1",new Dictionary<string,object>{{"kind","spoof"}},null),"neural reserved manifest identity rejected "+round);
            Throws(()=>Call(Store,null,"Add",root,"reflection_neural_model_v1",new Dictionary<string,object>(),new Dictionary<string,double[,]>{{"raw",new double[1,1]}}),"neural matrix payload rejected "+round);
            Check(Samples(root).Length==1,"tampering/spoofing preserves effective labels "+round);
            string before=Token(root);bool added=false;int allowedCalls=0;
            Throws(()=>Call(Repo,null,"Rebuild",root,(Func<bool>)(()=>{if(++allowedCalls==2){added=true;Add(root,Sample(200+round),"manual_confirmed");}return true;}),null),"sample snapshot changed while train pending fails publish "+round);
            Check(added&&Token(root)!=before&&Model(root)=="","immutable train digest rejects new label "+round);
            long verified=(long)Call(Repo,null,"Revision",root);Add(root,Sample(400+round),"manual_confirmed");
            Throws(()=>Call(Repo,null,"BeginApplication",root,verified),"mutation after background verification before GUI apply rejected "+round);
            long currentRevision=(long)Call(Repo,null,"Revision",root);int sampleCount=Samples(root).Length;
            using((IDisposable)Call(Repo,null,"BeginApplication",root,currentRevision))
            {
                Throws(()=>Add(root,Sample(500+round),"manual_confirmed"),"application reservation rejects another control's concurrent publication "+round);
                Check(Samples(root).Length==sampleCount&&(long)Call(Repo,null,"Revision",root)==currentRevision,"rejected concurrent mutation has no active sample or epoch change "+round);
                var cheap=Stopwatch.StartNew();Call(Repo,null,"Revision",root);Check(cheap.ElapsedMilliseconds<100,"GUI epoch gate does not scan models or block worker lock "+round);
            }
            Add(root,Sample(500+round),"manual_confirmed");Check(Samples(root).Length==sampleCount+1,"publication resumes when GUI apply lease ends "+round);
        }
        string cleanRoot=(string)Call(Repo,null,"RootFor",Path.Combine(parent,"cw"));Add(cleanRoot,Sample(701),"manual_confirmed");Call(Repo,null,"Rebuild",cleanRoot,null,null);
        string oldManifest=Directory.GetFiles(Path.Combine(cleanRoot,"Models","active"),"manifest.json",SearchOption.AllDirectories).Single();
        using(File.Open(oldManifest,FileMode.Open,FileAccess.Read,FileShare.Read))
        {string published=(string)Call(Repo,null,"Rebuild",cleanRoot,null,null);Check(Model(cleanRoot)==published,"successful model publication not misreported as failure when old archive is locked");Check(((string)Call(Repo,null,"CleanupWarning",cleanRoot)).Length>0,"post-publication archival failure has explicit warning");}
    }
    static void EvidenceCases(string parent)
    {
        using(var review=new PdReviewControl())
        {
            review.LocalAiLearningDirectory=Path.Combine(parent,"ui-library");review.AnalysisAlgorithmIndex=6;
            Check(review.ReflectionLearningScope=="ONSITE-OWTS","scope does not falsely assert 125MHz");
            var timer=Stopwatch.StartNew();var suggestion=review.GetLocalAiAnalysisSuggestion();Check(!suggestion.Available&&timer.ElapsedMilliseconds<1000,"method7 getter is cached/no training");
            Check(!Directory.Exists(review.LocalAiLearningDirectory),"getter does not create default learning artifacts");
            Check(!review.ApplyLocalAiAnalysisSuggestion(),"sync application never performs source scanning or auto-accepts");
            review.AnalysisAlgorithmIndex=0;suggestion=review.GetLocalAiAnalysisSuggestion();Check(!suggestion.Available&&suggestion.Status.Length>0,"other algorithm retains old lightweight API");review.AnalysisAlgorithmIndex=6;
            double[,] data=new double[1,36];data[0,0]=0;data[0,1]=1;data[0,5]=100;data[0,6]=170;data[0,8]=1;data[0,9]=1;data[0,10]=10000;data[0,13]=40;data[0,33]=Double.NaN;
            Set(review,"working",data);Set(review,"selected",0);
            Throws(()=>ICall(review,"CaptureReflectionLearningSnapshot",0,true),"legacy MAP without execution identity cannot become neural truth");
            var resultType=A.GetType("HVX.PdReview.ReviewAnalysisResult",true);var completeType=typeof(PdReviewControl).GetNestedType("CompletedFile",BindingFlags.NonPublic);
            var evidenceType=typeof(PdReviewControl).GetNestedType("ReflectionExecutionEvidence",BindingFlags.NonPublic);
            var requestType=A.GetType("HVX.PdReview.ReviewAnalysisRequest",true);
            object result=Activator.CreateInstance(resultType,true),request=Activator.CreateInstance(requestType,true),file=Activator.CreateInstance(completeType,true),evidence=Activator.CreateInstance(evidenceType,true);
            Set(result,"Success",true);Set(result,"Parameters",data);Set(file,"Result",result);Set(file,"Request",request);Set(evidence,"Valid",true);Set(evidence,"Project",parent);Set(evidence,"Location",Path.Combine(parent,"fixture.loc"));Set(evidence,"Hashes",new Dictionary<string,string>());
            object pending=Get(review,"pendingReflectionEvidence");ICall(pending,"Add",result,evidence);Array files=Array.CreateInstance(completeType,1);files.SetValue(file,0);
            ICall(review,"RegisterReflectionExecutionEvidence",files,0,true);Check((int)ICall(Get(review,"reflectionEvidenceRows"),"get_Count")==0,"canceled analysis retains physical rows but registers no neural provenance");
            ICall(pending,"Add",result,evidence);Set(result,"Success",false);ICall(review,"RegisterReflectionExecutionEvidence",files,0,false);Check((int)ICall(Get(review,"reflectionEvidenceRows"),"get_Count")==0,"failed analysis cannot register provenance");
            ICall(pending,"Add",result,evidence);Set(result,"Success",true);Set(evidence,"Valid",false);ICall(review,"RegisterReflectionExecutionEvidence",files,0,false);Check((int)ICall(Get(review,"reflectionEvidenceRows"),"get_Count")==0,"changed source cannot register provenance");
            ICall(pending,"Add",result,evidence);Set(evidence,"Valid",true);ICall(review,"RegisterReflectionExecutionEvidence",files,0,false);Check((int)ICall(Get(review,"reflectionEvidenceRows"),"get_Count")==1,"only completed unchanged evidence binds to current working snapshot");
            string path=Path.Combine(parent,"source-read-lock.txt");File.WriteAllText(path,"synthetic evidence",new UTF8Encoding(false));string hash=(string)Call(Store,null,"Hash",path);Type leaseType=A.GetType("HVX.PdReview.ReflectionSourceLease",true);
            using(var lease=(IDisposable)Activator.CreateInstance(leaseType,BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{new Dictionary<string,string>{{path,hash}}},CultureInfo.InvariantCulture))
            {Throws(()=>File.WriteAllText(path,"mutated"),"source lease prevents concurrent write through application/commit");Throws(()=>File.Move(path,path+".moved"),"source lease prevents concurrent path replacement");}
            File.WriteAllText(path,"now changed",new UTF8Encoding(false));Throws(()=>Activator.CreateInstance(leaseType,BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{new Dictionary<string,string>{{path,hash}}},CultureInfo.InvariantCulture),"source hash change rejects future lease");
            object gate=Repo.GetField("Gate",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
            using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                var worker=new Thread(()=>{lock(gate){held.Set();release.WaitOne(10000);}}){IsBackground=true};worker.Start();Check(held.WaitOne(2000),"training gate held in independent background thread");
                timer.Restart();Task train=review.RebuildReflectionLearningModelAsync();Check(timer.ElapsedMilliseconds<1000&&!train.IsCompleted&&review.ReflectionLearningOperationRunning,"explicit training starts without blocking GUI on repository gate");
                Check(review.CancelReflectionLearningOperation(),"background cancellation accepted");release.Set();worker.Join(5000);bool canceled=false;try{Wait(train);}catch(OperationCanceledException){canceled=true;}Check(canceled&&!review.ReflectionLearningOperationRunning&&review.LocalAiPendingWriteCount==0,"cancel clears busy state without publishing model");
                Check(!Directory.Exists(Path.Combine(review.LocalAiLearningDirectory,"ReflectionPairsMethod7Neural","Models")),"canceled worker created no model artifact");
            }
            // The physical failure return is identical with or without the two
            // provenance hooks. This invokes only invalid offline input.
            Set(request,"ProjectPath",parent);Set(request,"LocationFilePath",Path.Combine(parent,"invalid.loc"));File.WriteAllText(Path.Combine(parent,"invalid.loc"),"invalid synthetic LOC",new UTF8Encoding(false));
            object plain=Call(A.GetType("HVX.PdReview.ReviewAnalysisEngine",true),null,"Analyze",request),wrapped=ICall(review,"RunAnalysisWithReflectionEvidence",request);
            Check((bool)Get(plain,"Success")==false&&(bool)Get(wrapped,"Success")==false&&(string)Get(plain,"Status")== (string)Get(wrapped,"Status"),"provenance wrapper does not change physical result/failure status");
        }
    }
    static void UiCases(string parent)
    {
        foreach(int language in new[]{0,1})foreach(float fontSize in new[]{9f,12f,16f})
        using(var host=new Form{ClientSize=new Size(1400,900)})using(var review=new PdReviewControl{Dock=DockStyle.Fill})using(var font=new Font("Microsoft YaHei UI",fontSize))
        {
            review.LocalAiLearningDirectory=Path.Combine(parent,"ui"+language+"-"+fontSize);review.Font=font;review.LanguageIndex=language;host.Controls.Add(review);host.Show();Application.DoEvents();
            var button=Descendants(review).Single(c=>c.Name=="ReviewLocalAiSuggestion");Check(button.Visible&&button.Width>=TextRenderer.MeasureText(button.Text,button.Font).Width,"reused sidebar neural button readable "+language+"/"+fontSize);
            using(var close=new System.Windows.Forms.Timer{Interval=100})
            {
                bool found=false;close.Tick+=(s,e)=>{var manager=Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f!=host&&Descendants(f).Any(c=>c.Name=="ReviewReflectionLearningActions"));if(manager==null)return;found=true;
                    var actions=Descendants(manager).OfType<Button>().Where(b=>b.Name.StartsWith("ReviewReflection",StringComparison.Ordinal)).ToArray();Check(actions.Length==7,"neural manager has explicit async train/label/suggest/apply/cancel "+language+"/"+fontSize);
                    foreach(var action in actions)Check(action.Height>=action.Font.Height+4&&action.Parent.ClientRectangle.Contains(action.Bounds),"neural action fits manager viewport "+action.Name+"/"+language+"/"+fontSize);
                    if(fontSize==12f)using(var bitmap=new Bitmap(manager.Width,manager.Height)){manager.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(parent,"neural-manager-"+language+".png"));}
                    manager.Close();close.Stop();};
                close.Start();ICall(review,"ShowReflectionLearningManager");Check(found,"real manager opened under WinForms event loop "+language+"/"+fontSize);
            }
            host.Close();
        }
    }
    static bool SameMatrix(double[,] left,double[,] right)
    {if(left.GetLength(0)!=right.GetLength(0)||left.GetLength(1)!=right.GetLength(1))return false;for(int r=0;r<left.GetLength(0);r++)for(int c=0;c<left.GetLength(1);c++)if(!left[r,c].Equals(right[r,c]))return false;return true;}
    static void ActualAnalysisCases(string parent)
    {
        string project=Fixture(parent),loc=Path.Combine(project,"A_12.0kV_1.loc"),library=Path.Combine(parent,"al");
        var original=Directory.GetFiles(project).ToDictionary(p=>p,p=>(string)Call(Store,null,"Hash",p));
        using(var host=new Form{ClientSize=new Size(1400,900)})using(var review=new PdReviewControl{Dock=DockStyle.Fill,PromptSavedAnalysisOnProjectLoad=false,EnableDefectRecognition=false,LocalAiLearningDirectory=library})
        {
            host.Controls.Add(review);host.Show();Application.DoEvents();review.AnalysisAlgorithmIndex=6;review.AnalysisUseFirstArrivalTiming=false;review.AnalysisMaximumPulseCount=30;
            // Relative project paths are accepted by the public host contract.
            string relative=project.StartsWith(Environment.CurrentDirectory+"\\",StringComparison.OrdinalIgnoreCase)?project.Substring(Environment.CurrentDirectory.Length+1):project;
            review.ProjectPath=relative;review.SetFileAnalysisThreshold(loc,true,10);
            var requestType=A.GetType("HVX.PdReview.ReviewAnalysisRequest",true);object request=Activator.CreateInstance(requestType,true);
            Set(request,"ProjectPath",project);Set(request,"LocationFilePath",loc);Set(request,"LocationDataId",1L);Set(request,"AlgorithmIndex",6);Set(request,"AutomaticAcceptance",false);Set(request,"ManualThresholdEnabled",true);Set(request,"ManualThreshold",10d);
            object plain=Call(A.GetType("HVX.PdReview.ReviewAnalysisEngine",true),null,"Analyze",request),wrapped=ICall(review,"RunAnalysisWithReflectionEvidence",request);
            Check((bool)Get(plain,"Success")&&(bool)Get(wrapped,"Success"),"real actual-DLL method7 offline analysis succeeds before/after provenance hook");
            foreach(string matrix in new[]{"Parameters","Raw","Filtered","Equivalent"})Check(SameMatrix((double[,])Get(plain,matrix),(double[,])Get(wrapped,matrix)),"new provenance hook leaves actual physical "+matrix+" matrix exactly unchanged");
            Check(review.AnalysisTree.SetDataSelected(loc,true),"real LOC selected through public analysis tree");
            Check(review.StartSelectedAnalysis(false),"real GUI manual-review analysis starts: "+review.LastError);
            Until(()=>!review.IsAnalysisRunning,"real GUI analysis completes");Check(review.AnalysisCompletedFileCount==1&&review.SourceRowCount>0,"real GUI analysis merges completed row and registers execution evidence: "+review.LastError);
            Check(review.SelectSourceRow(0)&&!review.CurrentAccepted&&review.CanAccept,"actual physical MAP remains pending manual human review");
            var snapshot=ICall(review,"CaptureReflectionLearningSnapshot",0,true);string metadata=(string)Call(typeof(PdReviewControl),null,"ReflectionMetadata",snapshot);var query=Obj(metadata);
            double clockHz=Convert.ToDouble(query["sample_rate_hz"]),actualHz=1e9/review.PdMap.CurrentSampleIntervalNs;
            Check(Math.Abs(clockHz-actualHz)<=1e-12*actualHz&&Math.Abs(clockHz-100000000)<1e-4,"sample clock derives from actual 100MHz raw MAP dt, never hard-coded 125MHz: "+clockHz+"/"+actualHz);
            Check((string)query["source_sha256"]==original[loc]&&(string)query["calibration_sha256"]==original[Path.Combine(project,"ALL_1nC.cal")],"successful executed LOC and CAL hashes carried into immutable label metadata");
            Check(!Directory.Exists(library),"automatic algorithm completion does not create a human label or model");
            review.AiLearningMode=true;Check(review.AcceptCurrent(),"public AcceptCurrent records explicit human confirmation");Until(()=>review.LocalAiPendingWriteCount==0,"manual human label save completes in background");
            string root=(string)ICall(review,"ReflectionLearningRoot");var samples=Samples(root);Check(samples.Length==1,"real Analyze→manual→background label path saves exactly one sample: "+review.AiLearningStatus);
            if(samples.Length!=1)throw new InvalidOperationException("Real manual sample missing: "+review.AiLearningStatus);
            var saved=Obj((string)samples[0]["sample_json"]);Check((string)saved["label_source"]=="manual_confirmed"&&(string)saved["project_key"]==project&&(string)saved["scope_key"]=="ONSITE-OWTS","saved label is explicit human with executed project context");
            var binding=ICall(review,"CaptureReflectionAcceptanceRetraction",0);Check((bool)Call(typeof(PdReviewControl),null,"MatchesReflectionRetraction",samples[0],binding),"exact saved transaction belongs to its recorded partition");
            foreach(string partition in new[]{"source_sha256","calibration_sha256","project_key","scope_key","timing_mode","source_incident_index"})
            {var changed=Obj((string)samples[0]["sample_json"]);changed[partition]=partition=="source_incident_index"?(object)(Convert.ToDouble(changed[partition])+1):((string)changed[partition]+"-other");var entry=new Dictionary<string,object>(samples[0]);entry["sample_json"]=J.Serialize(changed);Check(!(bool)Call(typeof(PdReviewControl),null,"MatchesReflectionRetraction",entry,binding),"retraction never crosses "+partition+" partition");}
            var otherTransaction=new Dictionary<string,object>(samples[0]);otherTransaction["transaction_id"]="other-human-action";Check(!(bool)Call(typeof(PdReviewControl),null,"MatchesReflectionRetraction",otherTransaction,binding),"retraction targets exact saved human action, not independent same-context label");
            Check(Model(root)==""&&!Directory.Exists(Path.Combine(root,"Models")),"real public acceptance has no implicit neural training");
            Wait(review.RebuildReflectionLearningModelAsync());Check(Model(root).Length>0,"public explicit background train publishes isolated QA model only");
            review.AiLearningMode=false;var suggesting=review.RequestReflectionLearningSuggestionAsync();Wait(suggesting);Check(!suggesting.Result.Available,"insufficient real human support abstains instead of inventing a trained field suggestion");
            Check(review.ReflectionLearningSuggestionJson.Length>0,"current insufficient-support evidence JSON is inspectable");
            string currentProject=review.ProjectPath;Set(review,"projectPath",Path.Combine(parent,"other-project"));Check(review.ReflectionLearningSuggestionJson=="","cached JSON is hidden if project context differs");Set(review,"projectPath",currentProject);Check(review.ReflectionLearningSuggestionJson.Length>0,"cheap cache gate restores only exact unchanged current project context");
            review.EnableAiFeatures=false;Check(review.ReflectionLearningSuggestionJson=="","AI disabled cached JSON cannot leak previous suggestion context");review.EnableAiFeatures=true;
            string addedLoc=Path.Combine(project,"A_12.0kV_2.loc");File.Copy(loc,addedLoc);Check(review.AnalysisTree.LoadProject(project)&&review.AnalysisTree.SetDataSelected(addedLoc,true),"new offline LOC loads into actual tree for retained-map analysis");
            Check(review.StartSelectedAnalysis(false),"second public analysis starts with existing accepted row retained");Until(()=>!review.IsAnalysisRunning,"second public analysis completes");Check(review.SourceRowCount>=2&&review.SelectSourceRow(0)&&review.CurrentAccepted,"append analysis keeps original manual accepted MAP");
            Check(ICall(review,"CaptureReflectionAcceptanceRetraction",0)!=null,"exact saved label ownership rebinds only the retained original project row");
            review.ReflectionLearningScope="changed-scope";review.PdMap.MoveIncidentToIndex(review.PdMap.CurrentIncidentIndex+5);
            Check(review.DeleteCurrent(),"explicit acceptance can be revoked after scope/T1 edits");Until(()=>review.LocalAiPendingWriteCount==0,"bound old human label retract completes");Check(Samples(root).Length==0,"scope/T1 edit revokes original saved transaction rather than mutable current context");
            review.ReflectionLearningScope="ONSITE-OWTS";review.AiLearningMode=true;
            using(var committed=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                Set(review,"AfterReflectionSampleCommitForTests",(Action)(()=>{committed.Set();release.WaitOne(10000);}));
                Check(review.AcceptCurrent(),"second explicit confirmation starts deterministic postcommit binding race");Until(()=>committed.WaitOne(0),"real sample publication paused after atomic commit before saved binding");
                var timer=Stopwatch.StartNew();Check(review.DeleteCurrent(),"human revoke accepted during postcommit/prebinding race");Check(timer.ElapsedMilliseconds<1000,"revoke awaits pending identity without blocking GUI on I/O");release.Set();
                Until(()=>review.LocalAiPendingWriteCount==0,"pending exact-transaction retraction completes");Check(Samples(root).Length==0,"postcommit/prebinding human revoke leaves no active orphan positive label");Set(review,"AfterReflectionSampleCommitForTests",null);
            }
            host.Close();
        }
        foreach(var entry in original)Check((string)Call(Store,null,"Hash",entry.Key)==entry.Value,"actual offline analysis/learning leaves original acquisition file unchanged "+Path.GetFileName(entry.Key));
    }
    [STAThread] static int Main(string[] args)
    {
        string root=Path.Combine(Path.GetFullPath(args[0]),"nh-"+Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(root);
        try{Application.EnableVisualStyles();A.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,(Func<string>)(()=>Path.Combine(root,"test-settings.json")));RepositoryCases(root);EvidenceCases(root);ActualAnalysisCases(root);UiCases(root);}catch(Exception ex){errors.Add(ex.ToString());}
        File.WriteAllText(Path.Combine(root,"verification.json"),J.Serialize(new{Result=errors.Count==0?"PASS":"FAIL",Checks=checks,Failures=errors,HardwareOperationsExecuted=false,PhysicalHardwareVerified=false,ControlSHA256=(string)Call(Store,null,"Hash",A.Location),
            Note="All labels and weights are synthetic QA only; no field model or device interaction."}),new UTF8Encoding(false));
        Console.WriteLine("Task17NeuralHostSmoke "+(errors.Count==0?"PASS":"FAIL")+" "+checks+" checks; QA="+root);foreach(string error in errors)Console.WriteLine(error);return errors.Count==0?0:1;
    }
}
