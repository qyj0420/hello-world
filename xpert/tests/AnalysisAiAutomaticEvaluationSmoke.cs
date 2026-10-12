using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class AnalysisAiAutomaticEvaluationSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;static Assembly assembly;static double recordId=1,phaseId=1;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Get(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static object Field(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(o,values);}
    static void Pump(int milliseconds){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<milliseconds){Application.DoEvents();Thread.Sleep(5);}}
    static void Until(Func<bool> ready,string text,int timeout=15000){var watch=Stopwatch.StartNew();while(!ready()&&watch.ElapsedMilliseconds<timeout){Application.DoEvents();Thread.Sleep(5);}Check(ready(),text);}
    static Control Named(Control parent,string name){if(parent.Name==name)return parent;foreach(Control child in parent.Controls){var found=Named(child,name);if(found!=null)return found;}return null;}
    static double[,] Parameters(int rows,bool accepted=true)
    {
        var p=new double[rows,36];for(int r=0;r<rows;r++)
        {p[r,0]=phaseId;p[r,1]=recordId;p[r,2]=13.4;p[r,3]=30+r%30*10;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;p[r,10]=100;p[r,11]=710;p[r,12]=610;p[r,13]=200;p[r,15]=180+r%20;p[r,16]=180;p[r,20]=100;p[r,21]=.92;p[r,23]=1;p[r,24]=1;p[r,32]=1023;p[r,33]=.91;p[r,35]=accepted?1:0;}return p;
    }
    static double[,] Waves(int count)
    {var values=new double[count,1024];for(int r=0;r<count;r++)for(int i=0;i<1024;i++)values[r,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));return values;}
    static void Ready(Control review){Until(()=>!(bool)Get(review,"EvaluationBusy")&&!(bool)Get(review,"AutomaticEvaluationPending"),"automatic evaluation finishes");Check((bool)Get(review,"EvaluationResultIsCurrent"),"current Task15 result: "+Get(review,"EvaluationDiagnostic"));}
    static void Draw(Control c){c.Size=new Size(900,440);using(var image=new Bitmap(c.Width,c.Height))c.DrawToBitmap(image,c.ClientRectangle);}
    static void WheelCases(Control review)
    {
        object overview=Field(review,"locationOverview");var signal=Enumerable.Range(0,4096).Select(i=>i==5?900d:Math.Sin(i*.1)*10).ToArray();
        Call(overview,"SetData",signal,125e6,2d);Draw((Control)overview);var box=(Rectangle)Get(overview,"PlotRectangle");var e=new MouseEventArgs(MouseButtons.None,0,box.Left+box.Width/2,box.Top+box.Height/2,120);
        double y0=(double)Get(overview,"ViewYMinimum"),y1=(double)Get(overview,"ViewYMaximum");Call(overview,"ApplyAxisWheel",e,false);Draw((Control)overview);
        Check((int)Get(overview,"ViewStartIndex")>5,"overview X zoom excludes extreme sample");Check(y0==(double)Get(overview,"ViewYMinimum")&&y1==(double)Get(overview,"ViewYMaximum"),"overview ordinary wheel preserves Y even when extreme leaves view");
        int x0=(int)Get(overview,"ViewStartIndex"),x1=(int)Get(overview,"ViewEndIndex");Call(overview,"ApplyAxisWheel",e,true);
        Check(x0==(int)Get(overview,"ViewStartIndex")&&x1==(int)Get(overview,"ViewEndIndex"),"overview Shift preserves X");Check((double)Get(overview,"ViewYMaximum")-(double)Get(overview,"ViewYMinimum")<y1-y0,"overview Shift zooms Y");
        var pd=(Control)Get(review,"PdMap");Draw(pd);box=(Rectangle)Field(pd,"lastPlot");e=new MouseEventArgs(MouseButtons.None,0,box.Left+box.Width/2,box.Top+box.Height/2,120);
        y0=(double)Field(pd,"viewY0");y1=(double)Field(pd,"viewY1");Call(pd,"ApplyAxisWheel",e,false);Check(y0==(double)Field(pd,"viewY0")&&y1==(double)Field(pd,"viewY1"),"PdMap ordinary wheel preserves Y");
        double px0=(double)Field(pd,"viewX0"),px1=(double)Field(pd,"viewX1");Call(pd,"ApplyAxisWheel",e,true);Check(px0==(double)Field(pd,"viewX0")&&px1==(double)Field(pd,"viewX1"),"PdMap Shift preserves X");Check((double)Field(pd,"viewY1")-(double)Field(pd,"viewY0")<y1-y0,"PdMap Shift zooms Y");
        var spectrum=(Control)Get(review,"Spectrum");Set(spectrum,"DisplayModeIndex",0);Draw(spectrum);box=(Rectangle)Call(spectrum,"PlotArea",spectrum.ClientRectangle);e=new MouseEventArgs(MouseButtons.None,0,box.Left+box.Width/2,box.Top+box.Height/2,120);
        var first=(double[])Get(spectrum,"CurrentAxisRanges");Call(spectrum,"ApplyAxisWheel",e,false);var second=(double[])Get(spectrum,"CurrentAxisRanges");Check(first[2]==second[2]&&first[3]==second[3]&&second[1]-second[0]<first[1]-first[0],"Spectrum ordinary wheel X only");Call(spectrum,"ApplyAxisWheel",e,true);var third=(double[])Get(spectrum,"CurrentAxisRanges");Check(second[0]==third[0]&&second[1]==third[1]&&third[3]-third[2]<second[3]-second[2],"Spectrum Shift wheel Y only");
        Check((int)Get(spectrum,"ConclusionAnalysisCount")==0,"chart redraw/zoom never invokes synchronous Task15 in review host");
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"ai-auto-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
            assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            Type type=assembly.GetType("HVX.PdReview.PdReviewControl",true);string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t414\r\n",Encoding.UTF8);File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
            string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);
            using(Control review=(Control)Activator.CreateInstance(type))
            {
                Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",project);Set(review,"LocalAiLearningDirectory",Path.Combine(qa,"ai-samples"));var handle=review.Handle;
                var catalog=(double[,])Call(Get(review,"AnalysisTree"),"GetDataCatalog");phaseId=catalog[0,0];recordId=catalog[0,1];
                object spectrum=Get(review,"Spectrum");Set(spectrum,"Task15RuntimePath",Path.Combine(build,"modules","DataAnalysis","e15c7e4a92bf60e8135ac904.V1.0.15.dll"));Set(spectrum,"OperatingVoltageKv",8.7d);
                Check((bool)Get(review,"EnableAiFeatures"),"AI default enabled");Check(Named(review,"ReviewGenerateConclusion")==null,"generate conclusion button removed");
                Set(review,"EnableAiFeatures",false);Set(review,"ModelLibraryPath",Path.Combine(qa,"missing-model.pdm"));Set(review,"DisplayModeIndex",11);
                Check(!Named(review,"ReviewAiLearningMode").Enabled&&!Named(review,"ReviewLocalAiSuggestion").Enabled,"AI buttons disabled");
                foreach(string name in new[]{"AddConfirmedLearningSample","UndoLearningSample","ResetLearningSamples"})Check(!Named(review,name).Enabled,"learning action disabled: "+name);
                Check(!(bool)Call(review,"AddConfirmedLearningSample","tip","qa")&&!(bool)Call(review,"UndoLatestLearningSample")&&!(bool)Call(review,"ResetLearningSamples"),"all sample write APIs gated before model/files");
                Check((int)Get(review,"LearningSampleCount")==0&&(int)Get(review,"LocalAiLocationSampleCount")==0,"disabled counts do not read a missing model/library");
                Check(!(bool)Get(Call(review,"GetLocalAiAnalysisSuggestion"),"Available")&&!(bool)Get(Call(review,"GetLocalAiConclusionSuggestion"),"Available"),"suggestion APIs gated");
                Check(!(bool)Call(review,"ApplyLocalAiAnalysisSuggestion")&&!(bool)Call(review,"RecordConclusionFeedback","x",5,""),"AI apply/feedback gated");
                bool rejected=false;try{Set(review,"AiLearningMode",true);}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected,"cannot enable learning while master disabled");
                rejected=false;try{Call(review,"RecognizeCurrentDischargeAsync");}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected,"async recognition rejected before runtime initialization");
                Check(Field(review,"task12Runtime")==null&&!Directory.Exists(Path.Combine(qa,"ai-samples")),"disabled AI creates no runtime or sample directory");
                rejected=false;try{Call(review,"RecognizeCurrentDischarge");}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected,"synchronous recognition API gated too");
                Set(review,"DisplayModeIndex",0);Set(review,"EnableAiFeatures",true);
                var snapshot=Activator.CreateInstance(type.GetNestedType("LocationLearningSnapshot",All),true);
                snapshot.GetType().GetField("Root",All).SetValue(snapshot,Path.Combine(qa,"pending-sample"));
                snapshot.GetType().GetField("Values",All).SetValue(snapshot,new Dictionary<string,object>());
                snapshot.GetType().GetField("Matrices",All).SetValue(snapshot,new Dictionary<string,double[,]>());
                object commitGate=Field(review,"aiFeatureCommitGate");Monitor.Enter(commitGate);
                try{Call(review,"QueueAcceptedLocationLearningSample",snapshot);Thread.Sleep(40);Set(review,"EnableAiFeatures",false);}finally{Monitor.Exit(commitGate);}
                Until(()=>(int)Get(review,"LocalAiPendingWriteCount")==0,"disabled queued sample settles");
                Check(!Directory.Exists(Path.Combine(qa,"pending-sample","active"))||Directory.GetDirectories(Path.Combine(qa,"pending-sample","active")).Length==0,"disabling during sample staging prevents active commit");
                Pump(380);Check((int)Get(review,"EvaluationAnalysisCount")==0,"empty data never calls Task15");
                Set(review,"DisplayModeIndex",0);Set(review,"StatisticsFilterEnabled",true);Check((bool)Get(review,"StatisticsFilterEnabled"),"ordinary statistics filter remains usable with AI disabled");Set(review,"StatisticsFilterEnabled",false);
                var parameters=Parameters(24);var waves=Waves(24);Check((bool)Call(review,"LoadDataMatrices",parameters,waves),"load valid offline pulse fixture");
                for(int i=0;i<30;i++)Set(spectrum,"ConclusionCableServiceYears",2+i*.1);
                int heartbeat=0;using(var uiTimer=new System.Windows.Forms.Timer{Interval=15}){uiTimer.Tick+=(s,e)=>heartbeat++;uiTimer.Start();Ready(review);}
                Check((int)Get(review,"EvaluationAnalysisCount")==1,"rapid settings changes coalesce into one Task15 call");Check(heartbeat>0,"UI message loop stays responsive while conclusion queued/computed");
                Console.WriteLine("PREPARE_MS="+Get(review,"LastEvaluationPreparationMilliseconds")+" WORKER_MS="+Get(review,"LastEvaluationWorkerMilliseconds"));
                object approximationMap=Get(review,"PdMap");bool originalSnap=(bool)Get(approximationMap,"ApproximateCablePositions");
                int snapCount=(int)Get(review,"EvaluationAnalysisCount");Set(approximationMap,"ApproximateCablePositions",!originalSnap);
                Ready(review);Check((int)Get(review,"EvaluationAnalysisCount")==snapCount+1,"joint approximation change refreshes automatic conclusion without another UI action");
                Set(approximationMap,"ApproximateCablePositions",originalSnap);Ready(review);
                Check((int)Get(review,"EvaluationAnalysisCount")==snapCount+2,"restoring approximation also refreshes conclusion");
                int currentCount=(int)Get(review,"EvaluationAnalysisCount");Set(spectrum,"ConclusionCableServiceYears",8d);Call(review,"StartAutomaticEvaluation");Check((bool)Get(review,"EvaluationBusy"),"worker starts without blocking UI");Set(spectrum,"ConclusionCableServiceYears",9d);
                Ready(review);Check((int)Get(review,"EvaluationAnalysisCount")==currentCount+2,"in-flight superseded request followed by newest request only");
                object tree=Get(review,"AnalysisTree");Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});
                Check((bool)Call(review,"SaveNamedAnalysis","automatic-snapshot"),"save latest automatic conclusion snapshot: "+Get(review,"LastError"));string id=(string)Get(review,"LoadedAnalysisId");
                currentCount=(int)Get(review,"EvaluationAnalysisCount");Check((bool)Call(review,"LoadSavedAnalysis",id),"load saved snapshot");Pump(450);Check((int)Get(review,"EvaluationAnalysisCount")==currentCount&&!(bool)Get(review,"HasUnsavedAnalysisChanges"),"unchanged saved snapshot not recomputed or made dirty");
                WheelCases(review);Pump(350);Check((int)Get(review,"EvaluationAnalysisCount")==currentCount,"zoom does not recalculate conclusion");
                Call(review,"SelectSourceRow",0);Check((bool)Call(review,"DeleteCurrent"),"delete accepted row");Ready(review);Check((bool)Get(review,"HasUnsavedAnalysisChanges"),"edit and new conclusion retain dirty state");Check((int)Get(Get(review,"LatestEvaluationResult"),"InputRowCount")==23,"latest result excludes deleted accepted row");
                currentCount=(int)Get(review,"EvaluationAnalysisCount");Set(review,"DischargeChargeFilterPc",new[]{1000d,2000d});Pump(450);Check(!(bool)Get(review,"EvaluationResultIsCurrent")&&(int)Get(review,"EvaluationAnalysisCount")==currentCount,"filter excluding all pulses clears result without Task15");Check((int)Get(spectrum,"ConclusionMarkerCount")==0,"no valid data leaves no old chart conclusion markers");Set(review,"DischargeChargeFilterPc",new double[0]);Ready(review);
                Check((bool)Call(review,"LoadDataMatrices",Parameters(24,false),waves),"load unaccepted fixture");Pump(450);Check(!(bool)Get(review,"EvaluationResultIsCurrent")&&(string)Get(review,"EvaluationStatusCode")=="NoAcceptedInput","no accepted rows clears conclusion, no no-defect finding");
                var zero=Parameters(24);for(int i=0;i<24;i++)zero[i,2]=0;currentCount=(int)Get(review,"EvaluationAnalysisCount");Check((bool)Call(review,"LoadDataMatrices",zero,waves),"load accepted zero-voltage rows");Pump(450);Check(!(bool)Get(review,"EvaluationResultIsCurrent")&&(string)Get(review,"EvaluationStatusCode")=="NoValidInput"&&(int)Get(review,"EvaluationAnalysisCount")==currentCount,"non-evaluable accepted inputs do not run or manufacture conclusions");
                currentCount=(int)Get(review,"EvaluationAnalysisCount");Check((bool)Call(review,"LoadDataMatrices",Parameters(24),waves),"reload accepted fixture");Call(review,"StartAutomaticEvaluation");string other=Path.Combine(qa,"other-project");Directory.CreateDirectory(other);Set(review,"ProjectPath",other);Pump(700);Check(!(bool)Get(review,"EvaluationResultIsCurrent")&&(int)Get(review,"EvaluationAnalysisCount")==currentCount+1,"project switch discards running result and does not evaluate old project rows");
                Set(review,"ProjectPath",project);Check((bool)Call(review,"ForceReloadDataMatrices",Parameters(1500),Waves(1500)),"load performance fixture");Ready(review);
                double prep=(double)Get(review,"LastEvaluationPreparationMilliseconds");Console.WriteLine("PERF_1500_ROWS_PREPARE_MS="+prep+" WORKER_MS="+Get(review,"LastEvaluationWorkerMilliseconds"));Check(prep<100,"1500 row UI snapshot stays below 100 ms safety budget");
                Set(review,"EnableAiFeatures",true);Set(review,"AiLearningMode",true);Set(review,"EnableAiFeatures",false);Check(!(bool)Get(review,"AiLearningMode"),"master off exits manual AI learning mode safely");
                Set(spectrum,"ConclusionCableServiceYears",10d);Call(review,"StartAutomaticEvaluation");review.Dispose();Pump(150);Check(review.IsDisposed,"disposing during conclusion worker is safe");
            }
            Console.WriteLine("ANALYSIS_AI_AUTOMATIC_PASS="+checks);Console.WriteLine("EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
