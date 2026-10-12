using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Synthetic LOC/DEM/raw files in the owned QA directory only. Never loads a
// Task17/E worker, product entry point, SDK or device.
internal static class ReviewAcquisitionQualityOfflineSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type quality,reader,engine,request,result,review;static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Call(object target,string name,params object[] args)
    {var type=target as Type??target.GetType();try{return type.GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}catch(TargetInvocationException e){throw e.InnerException;}}
    static object Get(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Prop(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Put(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static void Reject(Action a,string why){bool rejected=false;try{a();}catch(Exception e){rejected=e is InvalidDataException||e is ArgumentException||e is InvalidOperationException;}Check(rejected,why);}
    static Dictionary<string,object> Risk()
    {return new Dictionary<string,object>{{"manualOverRangeSaved",true},{"overRange",true},{"overRangeAccepted",true},
        {"peakValidity","clipped-or-uncertain"},{"locationValidity","not-validated"},{"rawSha256",new String('A',64)}};}
    static string Json(object o){return new JavaScriptSerializer().Serialize(o);}
    static string Project(string root)
    {
        Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"ITEM.in"),"Load type = Three-core\r\n");
        File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t415m\r\n");return root;
    }
    static string Loc(string root,string context,int suffix=1)
    {
        string path=Path.Combine(root,"A_12.5kV_"+suffix+".loc");
        using(var w=new StreamWriter(path,false,new UTF8Encoding(true))) {
            foreach(string line in new[]{"Cable length\t415","Velocity (m/us)\t170","Delta x\t8E-9","Phase\tA","Voltage\t12.5",
                "F(Hz)\t500","Sample units\tpC*100000","Reserved\t0","Reserved2\t0",context==null?"Reserved3\t0":"Acquisition context\t"+context,"Data"})w.WriteLine(line);
            for(int i=0;i<4096;i++)w.WriteLine(i==100?"10000000":i==300?"5000000":"0");
        }return path;
    }
    static double[,] Matrix(int rows=1)
    {
        var p=new double[rows,36];for(int r=0;r<rows;r++) {
            p[r,0]=1;p[r,1]=7;p[r,2]=12.5;p[r,3]=90;p[r,5]=415;p[r,6]=170;p[r,7]=125e6;p[r,8]=7;p[r,9]=r;
            p[r,10]=100;p[r,11]=300;p[r,12]=200;p[r,13]=136;p[r,15]=100;p[r,16]=100;p[r,21]=.9;p[r,23]=20;p[r,24]=1e6;
            p[r,26]=9;p[r,31]=0;p[r,32]=4095;p[r,33]=.9;p[r,34]=1;p[r,35]=1;
        }return p;
    }
    static double[,] Waves(int rows=1)
    {var w=new double[rows*3,4096];for(int r=0;r<rows*3;r++){w[r,100]=100;w[r,300]=50;}return w;}
    static object FakeResult(object q)
    {var r=Activator.CreateInstance(result);Set(r,"Success",true);Set(r,"Parameters",Matrix());Set(r,"Status","{\"candidate_score\":0.9}");Set(r,"AcquisitionQuality",q);return r;}
    static void ReaderAndResult(string qa)
    {
        foreach(string field in new[]{"manualOverRangeSaved","overRange","overRangeAccepted","peakValidity","locationValidity"}) {
            object value=field.EndsWith("Validity")?(object)(field=="peakValidity"?"clipped-or-uncertain":"not-validated"):true;
            var q=Call(quality,"Parse",Json(new Dictionary<string,object>{{field,value}}));Check((bool)Prop(q,"ReferenceOnly"),field+" alone cannot imply validity");
        }
        var normal=Call(quality,"Parse","{\"overRange\":false,\"overRangeAccepted\":false}");Check(!(bool)Prop(normal,"ReferenceOnly"),"normal explicitly nonoverflow metadata does not become a risk");
        foreach(string bad in new[]{"[]","null","{\"overRange\":\"false\"}","{\"rawSha256\":\"bad\"}",
            "{\"overRange\":true,\"overRange\":false}","{\"overRange\":true,\"\\u006fverRange\":false}",
            new String(' ',65537),"{\"x\":[[[[[[[[[]]]]]]]]]}"})Reject(()=>Call(quality,"Parse",bad),"bounded/typed/duplicate metadata rejected");
        string root=Project(Path.Combine(qa,"loc")),path=Loc(root,Json(Risk())),digest=(string)Call(module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord"),"HashFile",path);
        var data=Call(reader,"Read",path,root);var q2=Get(data,"AcquisitionQuality");
        Check((bool)Prop(q2,"ReferenceOnly")&&((double[])Get(data,"ChargeSamples"))[100]==100,"LOC quality read never changes calibrated samples");
        Check((bool)Prop(Get(Call(reader,"ReadMetadata",path),"AcquisitionQuality"),"ReferenceOnly"),"header-only metadata path also carries risk");
        foreach(int algorithm in new[]{5,7,8,9}) {
            var r=Activator.CreateInstance(request);Set(r,"ProjectPath",root);Set(r,"LocationFilePath",path);Set(r,"LocationDataId",7L);Set(r,"AlgorithmIndex",algorithm);
            var snapshot=Call(engine,"Snapshot",r);Check(!(bool)Get(snapshot,"AutomaticAcceptance")&&(bool)Prop(Get(snapshot,"AcquisitionQuality"),"ReferenceOnly"),"all analysis request snapshots gate acceptance without running algorithm "+algorithm);
        }
        var fake=FakeResult(q2);Set(fake,"AHistoryReceipt",FormatterServices.GetUninitializedObject(module.GetType("HVX.PdReview.AHistoryReceipt",true)));
        var before=(double[,])((double[,])Get(fake,"Parameters")).Clone();Call(quality,"Apply",fake,q2);var after=(double[,])Get(fake,"Parameters");
        Check(Get(fake,"AHistoryReceipt")==null,"risk analysis cannot commit an accepted historical prior receipt");
        Check(after[0,35]==0&&after[0,33]==.9&&after[0,13]==136,"risk policy changes acceptance only, not candidate score or distance");
        Check(Enumerable.Range(0,35).All(c=>before[0,c].Equals(after[0,c])),"every other algorithm parameter remains byte-value equivalent");
        Check(((string)Get(fake,"Status")).Contains("acquisition_quality")&&((string)Get(fake,"Status")).Contains(new String('A',64)),"status preserves risk and source raw hash");
        Set(fake,"Status","capacitor original provenance");Call(quality,"Apply",fake,q2);Check(((string)Get(fake,"Status")).Contains("capacitor original provenance"),"plain capacitor status is preserved, not lost in JSON wrapping");
        Check((string)Call(module.GetType("HVX.CalibrationStorage.LiveCalibrationRecord"),"HashFile",path)==digest,"LOC source file is never rewritten");
        string legacy=Loc(root,null,2);Check(!(bool)Prop(Get(Call(reader,"ReadMetadata",legacy),"AcquisitionQuality"),"ReferenceOnly"),"legacy missing metadata stays unrecorded, not invented valid or risky");
        string oversized=Loc(root,new String(' ',65537),3);Reject(()=>Call(reader,"ReadMetadata",oversized),"header line bound checked before unbounded allocation");
        var cache=module.GetType("HVX.PdReview.PressureAnalysisCache",true);var normalKey=new StringBuilder();var riskKey=new StringBuilder();
        Check((bool)Call(cache,"Append",normalKey,normal)&&(bool)Call(cache,"Append",riskKey,q2),"known scalar quality metadata preserves ordinary cache-key support");
        Check(normalKey.ToString()!=riskKey.ToString(),"normal and risk quality metadata never share a key");
        var changed=Risk();changed["rawSha256"]=new String('B',64);var changedKey=new StringBuilder();Call(cache,"Append",changedKey,Call(quality,"Parse",Json(changed)));
        Check(changedKey.ToString()!=riskKey.ToString(),"source raw hash also participates in quality cache identity");
        Check(!(bool)Call(cache,"Append",new StringBuilder(),new object()),"unknown cache-key reference types remain fail-closed");
        var large=Matrix();large[0,1]=1e15;string largeKey=(string)Call(review,"QualityKey",large,0);
        Check(largeKey=="1:1000000000000000"&&((IDictionary)Call(review,"ParseAcquisitionQualities",Json(new Dictionary<string,object>{{largeKey,Risk()}}))).Count==1,"valid large integer identities roundtrip without exponent/regex mismatch");
    }
    static void ReviewRoundtrip(string qa)
    {
        string root=Project(Path.Combine(qa,"review")),path=Loc(root,Json(Risk()));var p=Matrix();var w=Waves();
        using(var host=new Form{ClientSize=new Size(1280,800),ShowInTaskbar=false})using(var c=(Control)Activator.CreateInstance(review)) {
            host.Controls.Add(c);host.Show();Put(c,"PromptSavedAnalysisOnProjectLoad",false);Put(c,"ProjectPath",root);Put(c,"AnalysisAlgorithmIndex",9);
            Check((bool)Call(c,"SetProjectLocationIds",new[]{path},new[]{7d}),"catalog binds risk source identity");
            Check((bool)Call(c,"ForceReloadDataMatrices",p,w),"risk reference rows load: "+Prop(c,"LastError"));
            var wrongPhase=(double[,])p.Clone();wrongPhase[0,0]=2;
            Check(!(bool)Call(c,"ForceReloadDataMatrices",wrongPhase,w)&&(bool)Prop(c,"CurrentAcquisitionReferenceOnly"),"external wrong-phase identity cannot bypass risk or replace the previous gated view");
            Check((bool)Prop(c,"CurrentAcquisitionReferenceOnly")&&!((bool)Prop(c,"CurrentAccepted"))&&!((bool)Prop(c,"CanAccept")),"external/saved matrices cannot retain a trustworthy acceptance flag for risk source");
            Check(Double.IsNaN((double)Prop(c,"CurrentConfidence"))&&((double[,])Prop(c,"ResultParameterMatrix"))[0,33]==.9,"trust metric unavailable while original algorithm score retained");
            Check(!((bool)Call(c,"AcceptCurrent"))&&!((bool)Call(c,"RequestReportParameters"))&&!((bool)Call(c,"RequestPdMapReportChart")),"risk record cannot be accepted or exported as trustworthy pulse/report chart");
            var labels=(Label[])Get(c,"classIndicators");Check(labels[0].Text.Contains("风险")||labels[0].Text.Contains("Reference"),"visible acceptance badge names risk/reference only");
            var status=(Label)Get(c,"statusLabel");Check(status.Visible&&(status.Text.Contains("参考")||status.Text.Contains("reference")),"short risk warning remains visible without a modal technical window");
            var edited=Enumerable.Range(0,36).Select(col=>p[0,col]).ToArray();Call(c,"CommitRow",0,edited,false);Check(((double[,])Prop(c,"ResultParameterMatrix"))[0,35]==0,"manual waveform edits cannot reaccept risk row");
            Check(Call(c,"CaptureAcceptedLocationLearningSample",0,edited)==null,"risk row cannot produce an accepted-location learning sample");
            Put(c,"DisplayModeIndex",11);Put(Prop(c,"Spectrum"),"AcceptedOnly",false);var recognition=Call(c,"CaptureRecognitionInput");Check(((int[])Get(recognition,"Rows")).Length==0,"visible unaccepted risk pulses excluded from recognition/learning input");
            var tree=Get(c,"analysisTree");Call(tree,"MarkAnalyzed",new object[]{new[]{path}});
            object[] export={null,null,null,null};Check((bool)review.GetMethod("ExportAnalysisSession",F).Invoke(c,export),"risk session exports: "+Prop(c,"LastError"));
            var settings=(string[,])export[3];Check(Enumerable.Range(0,settings.GetLength(0)).Any(i=>settings[i,0]=="AcquisitionQualities"&&settings[i,1].Contains("clipped-or-uncertain")),"session carries risk provenance inside hashed metadata");
            Check((bool)Call(c,"LoadAnalysisSession",root,export[0],export[1],export[2],export[3])&&(bool)Prop(c,"CurrentAcquisitionReferenceOnly"),"risk session roundtrip remains reference only");
            // A pre-fix saved result may have accepted rows and no quality setting.
            var legacy=new string[settings.GetLength(0)-1,2];int index=0;
            for(int i=0;i<settings.GetLength(0);i++)if(settings[i,0]!="AcquisitionQualities"){legacy[index,0]=settings[i,0];legacy[index++,1]=settings[i,1];}
            var legacyP=(double[,])((double[,])export[0]).Clone();legacyP[0,35]=1;
            Check((bool)Call(c,"LoadAnalysisSession",root,legacyP,export[1],export[2],legacy),"pre-fix session remains loadable with revalidated quality: "+Prop(c,"LastError"));
            Check((bool)Prop(c,"CurrentAcquisitionReferenceOnly")&&((double[,])Prop(c,"ResultParameterMatrix"))[0,35]==0,"old saved acceptance does not override current file risk metadata");
            Check((bool)Call(c,"SaveNamedAnalysis","Offline acquisition quality"),"named risk analysis saves: "+Prop(c,"LastError"));
            string id=(string)Prop(c,"LastSavedAnalysisId");Check((bool)Call(c,"LoadSavedAnalysis",id)&&(bool)Prop(c,"CurrentAcquisitionReferenceOnly"),"named result reload preserves risk evidence and acceptance gate");
            var rollback=(Action)Call(c,"CaptureNamedAnalysisRollback");string capturedLoc=File.ReadAllText(path);File.WriteAllText(path,"broken synthetic header");
            try {rollback();Check((bool)Prop(c,"CurrentAcquisitionReferenceOnly")&&((double[,])Prop(c,"ResultParameterMatrix"))[0,35]==0,"private failed-load rollback restores captured risk state even if source files changed");}
            finally {File.WriteAllText(path,capturedLoc);}
            string mask=(string)Call(c,"CreateEffectiveSelectionMask");Check(mask=="0","risk source excluded from effective report selection");
            string context=(string)Call(c,"CreateReportInputContext",Prop(c,"ResultParameterMatrix"));Check(context.Contains("AcquisitionQualities")&&context.Contains("not-validated"),"report-input snapshot retains explicit risk provenance");
            // Old report attachments are retained visibly as reference, never silently promoted.
            var chartType=module.GetType("HVX.PdReview.ReportChartSnapshot",true);var chart=FormatterServices.GetUninitializedObject(chartType);
            Set(chart,"<Title>k__BackingField","Synthetic historical chart");
            Call(chart,"MarkAcquisitionReferenceOnly",true);Call(chart,"SetReportSelection",true);
            Check((bool)Prop(chart,"AcquisitionReferenceOnly")&&!(bool)Prop(chart,"IncludedInReport"),"reference chart cannot be reselected as an ordinary report attachment");
        }
    }
    static void DemAndBatch(string build,string qa)
    {
        var fixtureAssembly=Assembly.LoadFrom(Path.Combine(build,"CapacitorDemRawAnalysisSmoke.exe"));var helper=fixtureAssembly.GetType("CapacitorDemRawAnalysisSmoke",true);
        foreach(var field in new[]{new object[]{"module",module},new object[]{"engine",engine},new object[]{"requestType",request},new object[]{"inputType",module.GetType("HVX.PdReview.ReviewCapacitorDemInput",true)}})
            helper.GetField((string)field[0],F).SetValue(null,field[1]);
        var fixture=Call(helper,"Make",qa,"dem-risk",true,false,false);var context=(Dictionary<string,object>)Get(fixture,"Context");
        foreach(var pair in Risk())if(pair.Key!="rawSha256")context[pair.Key]=pair.Value;Call(helper,"WriteDem",fixture);
        var r=Call(helper,"Analyze",fixture,true);Check((bool)Get(r,"Success"),"risk DEM permits reference analysis after explicit save: "+Get(r,"Status"));
        Check(Enumerable.Range(0,((double[,])Get(r,"Parameters")).GetLength(0)).All(i=>((double[,])Get(r,"Parameters"))[i,35]==0),"DEM saved-overrange never auto accepts calibrated peak estimates");
        Check(((string)Get(r,"Status")).Contains("clipped-or-uncertain")&&((string)Get(r,"Status")).Contains("operator-acknowledged-warning"),"DEM status keeps both legacy warning and new reference-only meaning");
        using(var c=(Control)Activator.CreateInstance(review)) {
            Put(c,"PromptSavedAnalysisOnProjectLoad",false);Put(c,"ProjectPath",Get(fixture,"Root"));Call(c,"SetFileAnalysisThreshold",Get(fixture,"Dem"),true,20d);
            var batch=(Task<double[,]>)Call(c,"AnalyzeSavedPressureFilesAsync",new object[]{new[]{(string)Get(fixture,"Dem")}});
            Check(batch.Wait(30000),"synthetic DEM background batch finishes within bound");var rows=batch.GetAwaiter().GetResult();
            Check(rows.GetLength(0)==3&&Enumerable.Range(0,3).All(i=>rows[i,35]==0),"batch saved risk DEM outputs reference rows, not accepted statistical peaks");
        }
    }
    static void LocBatchPublication(string qa)
    {
        string root=Project(Path.Combine(qa,"loc-batch")),path=Loc(root,Json(Risk()));
        using(var c=(Control)Activator.CreateInstance(review)) {
            Put(c,"PromptSavedAnalysisOnProjectLoad",false);Put(c,"ProjectPath",root);
            Check((bool)Call(c,"SetProjectLocationIds",new[]{path},new[]{7d}),"synthetic LOC batch catalog binds identity");
            var r=Activator.CreateInstance(request);Set(r,"ProjectPath",root);Set(r,"LocationFilePath",path);Set(r,"LocationDataId",7L);Set(r,"AHistoryPhase",1);
            var fake=FakeResult(Call(quality,"Parse",Json(Risk())));
            var wave=new double[1,4096];wave[0,100]=100;wave[0,300]=50;
            Set(fake,"Raw",wave);Set(fake,"Filtered",wave);Set(fake,"Equivalent",wave);
            var completed=review.GetNestedType("CompletedFile",F);var item=Activator.CreateInstance(completed,true);Set(item,"Request",r);Set(item,"Result",fake);
            var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(completed));list.Add(item);
            Set(c,"analysisSourceSnapshot",Get(c,"working"));Set(c,"analysisSourceRevision",Prop(c,"ResultChangeSequence"));Set(c,"analysisBusy",true);Call(c,"CaptureAnalysisCatalogSnapshot");
            bool observed=false,gatedBeforePublication=false;
            EventHandler handler=(s,e)=>{observed=true;gatedBeforePublication=(bool)Prop(c,"CurrentAcquisitionReferenceOnly")&&!((bool)Prop(c,"CanAccept"));};
            review.GetEvent("CurrentPdMapChanged",F).AddEventHandler(c,handler);
            Call(c,"CompleteProjectAnalysis",Get(c,"analysisGeneration"),list,true,false,1,"");
            Check((int)Prop(c,"SourceRowCount")==1&&((double[,])Prop(c,"ResultParameterMatrix"))[0,35]==0,"LOC batch merge clamps risk before accepting engine output: "+Prop(c,"LastError"));
            Check(observed&&gatedBeforePublication,"synchronous batch UI publication cannot briefly expose risk as trustworthy");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            Console.OutputEncoding=Encoding.UTF8;string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            module=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            quality=module.GetType("HVX.PdReview.ReviewAcquisitionQuality",true);reader=module.GetType("HVX.PdReview.ReviewLocationFileReader",true);
            engine=module.GetType("HVX.PdReview.ReviewAnalysisEngine",true);request=module.GetType("HVX.PdReview.ReviewAnalysisRequest",true);
            result=module.GetType("HVX.PdReview.ReviewAnalysisResult",true);review=module.GetType("HVX.PdReview.PdReviewControl",true);
            module.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-preferences.json")));
            module.GetType("HVX.PdReview.ReviewSessionJournal",true).GetField("DirectoryProvider",F).SetValue(null,new Func<string>(()=>Path.Combine(qa,"isolated-journals")));
            Check(Directory.GetFiles(Path.GetDirectoryName(module.Location)).Length==1,"no location runtime beside isolated module");
            ReaderAndResult(qa);ReviewRoundtrip(qa);DemAndBatch(build,qa);LocBatchPublication(qa);
            Console.WriteLine("REVIEW_ACQUISITION_QUALITY_PASS="+checks+";ProductEntry=false;LocationAlgorithm=false;Worker=false;SDK=false;UserDataWrites=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
