using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class Task15EmbeddedIntegrationSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string text){checks++;if(!ok)throw new Exception(text);}
    static object Call(object o,string name,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static object P(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static object F(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static void S(object o,string name,object value){o.GetType().GetProperty(name,All).SetValue(o,value,null);}
    static void Ready(object review){var watch=Stopwatch.StartNew();while(((bool)P(review,"AutomaticEvaluationPending")||(bool)P(review,"EvaluationBusy"))&&watch.ElapsedMilliseconds<15000){Application.DoEvents();Thread.Sleep(5);}Check((bool)P(review,"EvaluationResultIsCurrent"),"async V3 result: "+P(review,"EvaluationDiagnostic"));}
    static double[,] CoreData()
    {
        var p=new double[49,36];for(int r=0;r<49;r++){p[r,0]=r<24?1:r<48?2:3;p[r,1]=r/6;p[r,2]=12;p[r,3]=r%24*15;p[r,9]=r;p[r,13]=r<24?200:r<48?200.5:200.2;p[r,15]=100+r;p[r,23]=20;p[r,24]=1e7;p[r,33]=.95;p[r,35]=1;}return p;
    }
    static object Core(Assembly a,double[,] input,double u0)
    {return Call(a.GetType("HVX.Location.EmbeddedConclusions",true),"Analyze",input,new[]{0d,200.5,414d},new[]{0d,210d,430d},new[]{0d,450d},u0,false,0,0,1d,.1d);}
    static void CoreParity(Assembly review,Assembly report)
    {
        var input=CoreData();var a=Core(review,input,8.7);var b=Core(report,input,8.7);
        Check((bool)P(a,"Ok")&&(bool)P(b,"Ok"),"both actual consumer assemblies execute embedded algorithm");
        Check((string)P(a,"AlgorithmId")=="HVX-EVIDENCE-15-V3"&&!(bool)P(a,"IsCalibratedProbability"),"version and non-calibrated-evidence semantics explicit");
        Check(((double[])P(a,"LocationMatrix")).SequenceEqual((double[])P(b,"LocationMatrix")),"analysis/report exact evidence parity");
        var embedded=review.GetType("HVX.Location.EmbeddedConclusions",true);
        for(int mask=1;mask<=7;mask++)
        {
            var x=(double[,])Call(embedded,"GetVisibleLocationMatrix",a,mask);
            Check(Enumerable.Range(0,x.GetLength(0)).All(r=>((int)x[r,0]&mask)==(int)x[r,0]&&x[r,8]>0),"visible projection preserves selected supported phases mask "+mask);
            if(mask==4)Check(x.GetLength(0)==0,"weak C phase is not promoted by strong neighbouring phases");
            if(mask==1)Check(x.GetLength(0)==1&&x[0,0]==1&&Math.Abs(x[0,1]-200)<.01,"single A view keeps own center of combined cluster");
        }
        var unknown=Core(review,input,0d);Check((bool)P(unknown,"Ok"),"unknown U0 is supported without inferring from test ladder");
        Check(!(bool)P(Core(review,input,-1d),"Ok"),"negative U0 remains invalid");
        var bridge=review.GetType("HVX.UnifiedSpectrum.SpectrumTask15Bridge",true);
        Check((Type)Call(bridge,"ResolveType","missing-and-ignored.dll")==review.GetType("HVX.Location.LocatorEngine"),"configured external DLL cannot replace embedded source");
    }
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"task15-"+Guid.NewGuid().ToString("N").Substring(0,10));Directory.CreateDirectory(qa);
            Assembly review=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            Assembly report=Assembly.LoadFrom(Path.Combine(build,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));CoreParity(review,report);
            review.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
            string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆长度\t414\r\n相电压\t8.7\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(project,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n2\t本体\tXLPE\r\n1\t接头 1\t中间接头\t200.5\r\n2\t本体\tXLPE\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
            string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);
            using(var control=(Control)Activator.CreateInstance(review.GetType("HVX.PdReview.PdReviewControl",true)))
            {
                S(control,"PromptSavedAnalysisOnProjectLoad",false);S(control,"ProjectPath",project);S(control,"EnableAiFeatures",false);var handle=control.Handle;
                object tree=P(control,"AnalysisTree"),spectrum=P(control,"Spectrum");var catalog=(double[,])Call(tree,"GetDataCatalog");S(spectrum,"OperatingVoltageKv",8.7d);S(spectrum,"UseCompensatedCharge",false);
                var p=new double[24,36];var waves=new double[24,1024];
                for(int r=0;r<24;r++)
                {
                    p[r,0]=catalog[0,0];p[r,1]=catalog[0,1];p[r,2]=13.4;p[r,3]=30+r*10;p[r,5]=414;p[r,6]=170;p[r,7]=125e6;p[r,9]=r;p[r,10]=100;p[r,11]=710;p[r,12]=610;p[r,13]=200;p[r,15]=180+r;p[r,16]=5000+r;p[r,20]=100;p[r,21]=.92;p[r,23]=1;p[r,24]=1;p[r,32]=1023;p[r,33]=.91;p[r,35]=1;
                    for(int i=0;i<1024;i++)waves[r,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));
                }
                Check((bool)Call(control,"LoadDataMatrices",p,waves),"load isolated real review fixture");Call(P(control,"PdMap"),"SetConnectors",new object[]{new[]{200.5d}});S(control,"DischargeChargeFilterPc",new[]{5001d,5500d});Ready(control);
                object[] output={null,null,0,0};var evaluated=(double[,])Call(control,"CreateEvaluationInput",output);
                Check(evaluated.GetLength(0)==23,"review filter is retained, not all accepted archive rows");
                Check(Enumerable.Range(0,23).All(r=>evaluated[r,13]==200&&evaluated[r,15]<500&&Double.IsNaN(evaluated[r,22])),"pre-snap position, original charge, and unknown column22 semantics preserved");
                object result=P(control,"LatestEvaluationResult");Check((string)P(result,"Api")!=""&&((string)P(result,"Api")).Contains("HVX-EVIDENCE-15-V3"),"real async host identifies V3");
                Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});Check((bool)Call(control,"SaveNamedAnalysis","V3-context"),"save V3 snapshot: "+P(control,"LastError"));string id=(string)P(control,"LoadedAnalysisId");
                string archive=Path.Combine(project,"Analysis report",id),manifest=Path.Combine(archive,"analysis.json");
                byte[] original=File.ReadAllBytes(manifest);var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};var document=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));var snapshot=(Dictionary<string,object>)document["Conclusion"];
                Check(((System.Collections.IList)snapshot["PhaseViews"]).Count==7&&((System.Collections.IList)snapshot["SourceRowIndices"]).Count==23,"saved snapshot binds phase projections and exact source subset");
                var reader=report.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);object package=Call(reader,"Read",archive);
                Check((bool)P(package,"HasSavedConclusionSnapshot")&&(bool)P(package,"UsesEvidenceV3"),"report recognizes validated current-source snapshot");
                var actual=(double[,])F(package,"ConclusionInput");Check(actual.GetLength(0)==evaluated.GetLength(0),"report replays only saved selected input rows");
                for(int r=0;r<actual.GetLength(0);r++)for(int c=0;c<actual.GetLength(1);c++)Check(actual[r,c].Equals(evaluated[r,c]),"report/input exact cell "+r+","+c);
                var joints=(double[][])F(package,"ConclusionJoints");Check(joints[0].SequenceEqual(new[]{0d,200.5,414d})&&joints[1].Length==0&&joints[2].Length==0,"saved joints contain own phase endpoints and no borrowed unobserved phase geometry");
                var service=Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ConclusionService",true));S(service,"RuntimeDllPath",Path.Combine(qa,"absent-old-runtime.dll"));
                object[] invocation={actual,joints[0],joints[1],joints[2],8.7d,2d,false,1,null};Check((bool)Call(service,"TryGenerate",invocation),"report recomputes exact V3 evidence without external DLL");
                Check((string)invocation[8]==(string)P(result,"SummaryText"),"report brief conclusion and analysis summary match for exact saved scope");
                Check(original.SequenceEqual(File.ReadAllBytes(manifest)),"report read/recompute does not mutate saved analysis");
                Check((bool)Call(control,"LoadSavedAnalysis",id),"reload new analysis");int count=(int)P(control,"EvaluationAnalysisCount");for(int i=0;i<100;i++){Application.DoEvents();Thread.Sleep(5);}Check((int)P(control,"EvaluationAnalysisCount")==count,"valid unchanged snapshot does not spuriously rerun");
                snapshot["SourceRowIndices"]=new[]{0,0};File.WriteAllText(manifest,json.Serialize(document));package=Call(reader,"Read",archive);Check(!(bool)P(package,"HasSavedConclusionSnapshot")&&F(package,"ConclusionInput")==null,"duplicate row binding is fail-closed, not unfiltered fallback");
                File.WriteAllBytes(manifest,original);
                document=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));snapshot=(Dictionary<string,object>)document["Conclusion"];snapshot["Api"]="Task15 legacy v2";snapshot.Remove("PhaseViews");snapshot.Remove("SourceRowIndices");snapshot.Remove("Joints");
                File.WriteAllText(manifest,json.Serialize(document));byte[] legacy=File.ReadAllBytes(manifest);
                count=(int)P(control,"EvaluationAnalysisCount");Check((bool)Call(control,"LoadSavedAnalysis",id),"legacy saved conclusion remains loadable");Call(control,"PublishEvaluationMarkers");
                Check((int)P(spectrum,"ConclusionMarkerCount")==0,"legacy ActionCode is not relabeled as a V3 marker before recomputation");Ready(control);
                Check((int)P(control,"EvaluationAnalysisCount")==count+1&&((string)P(P(control,"LatestEvaluationResult"),"Api")).Contains("HVX-EVIDENCE-15-V3"),"old saved algorithm snapshot is actually recalculated with V3");
                Check((bool)P(control,"HasUnsavedAnalysisChanges")&&legacy.SequenceEqual(File.ReadAllBytes(manifest)),"new result is dirty but never silently overwrites legacy archive");
            }
            Console.WriteLine("TASK15_EMBEDDED_INTEGRATION_PASS="+checks+";hardware=false;real_project_write=false");Console.WriteLine("EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex is TargetInvocationException?((TargetInvocationException)ex).InnerException:ex);return 1;}
    }
}
