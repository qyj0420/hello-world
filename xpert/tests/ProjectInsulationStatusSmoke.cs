using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ProjectInsulationStatusSmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;static Type reader;static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,All).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,All).SetValue(o,v,null);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(All).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static object Status(string project){return reader.GetMethod("Read").Invoke(null,new object[]{project});}
    static void Expect(string root,string state,int grade=-1){object s=Status(root);Check((string)Get(s,"StateCode")==state&&(int)Get(s,"SeverityCode")==grade,"status "+state+"/"+grade+": "+Get(s,"Detail"));}
    static Dictionary<string,object> Map(params object[] values){var d=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)d[(string)values[i]]=values[i+1];return d;}
    static Dictionary<string,object> Read(string file){return Json.DeserializeObject(File.ReadAllText(file,Encoding.UTF8)) as Dictionary<string,object>;}
    static void Write(string file,object value){File.WriteAllText(file,Json.Serialize(value),new UTF8Encoding(false));}
    static string Fixture(string root,string id,DateTime saved,int[] grades)
    {
        string folder=Path.Combine(root,"Analysis report",id);Directory.CreateDirectory(folder);string hash=new String('A',64);
        var files=new List<object>();foreach(string name in new[]{"parameters.bin","pdmap.bin","session.json"}){File.WriteAllText(Path.Combine(folder,name),"x");files.Add(Map("Path",name,"Bytes",1,"Sha256",hash));}
        object conclusion=grades==null?null:Map("Schema","HVX.AnalysisConclusion.1","AnalysisId",id,"GeneratedUtc",saved.AddSeconds(-1).ToString("o"),"SummaryText","仅建议文字包含及时处理，不作为等级依据", "ParametersSha256",hash,"SessionSha256",hash,"Regions",grades.Select((g,i)=>(object)Map("PhaseMask",i%3+1,"PositionM",i*10,"ActionCode",g)).ToArray());
        string file=Path.Combine(folder,"analysis.json");Write(file,Map("Schema","HVX.NamedAnalysis","Version",1,"Id",id,"Name","分析"+id,"CreatedUtc",saved.AddDays(-2).ToString("o"),"SavedUtc",saved.ToString("o"),"Files",files.ToArray(),"Conclusion",conclusion));File.SetLastWriteTimeUtc(file,saved);return file;
    }
    static void ReaderCases(string qa)
    {
        string p=Path.Combine(qa,"reader");Directory.CreateDirectory(p);Expect(p,"Unanalyzed");DateTime time=DateTime.UtcNow.AddDays(-10);
        string old=Fixture(p,"old",time,new[]{1,3,2});Expect(p,"Ready",3);
        string newer=Fixture(p,"new",time.AddHours(1),new[]{0,1});Expect(p,"Ready",1);Check((string)Get(Status(p),"AnalysisId")=="new","latest lower severity supersedes historical high severity");
        var m=Read(old);m["SavedUtc"]=time.AddHours(2).ToString("o");Write(old,m);Expect(p,"Ready",3);Check((string)Get(Status(p),"AnalysisId")=="old","overwrite SavedUtc, not old CreatedUtc, determines newest");
        m["Conclusion"]=null;Write(old,m);Expect(p,"NoConclusion");
        var restored=Read(newer);restored["SavedUtc"]=time.AddHours(3).ToString("o");Write(newer,restored);Expect(p,"Ready",1);
        ((Dictionary<string,object>)restored["Conclusion"])["Regions"]=new[]{Map("PhaseMask",1,"PositionM",0,"ActionCode",0)};Write(newer,restored);Expect(p,"Ready",0);Check((string)Get(Status(p),"StatusText")=="记录","zero is Record, never Normal");
        string[] v3Labels={"证据不足","计划跟踪","重点关注","优先核查"};
        for(int grade=0;grade<4;grade++)
        {
            var snapshot=(Dictionary<string,object>)restored["Conclusion"];
            snapshot["Api"]="hvx-conclusion-dotnet-3.0.0-evidence";
            snapshot["Regions"]=new[]{Map("PhaseMask",1,"PositionM",0,"ActionCode",grade)};
            Write(newer,restored);Expect(p,"Ready",grade);var current=Status(p);
            Check((bool)Get(current,"UsesEvidenceV3")&&(string)Get(current,"StatusText")==v3Labels[grade],"V3 grade semantics are preserved: "+grade);
        }
        ((Dictionary<string,object>)restored["Conclusion"]).Remove("Api");
        ((Dictionary<string,object>)restored["Conclusion"])["Regions"]=new[]{Map("PhaseMask",1,"PositionM",0,"ActionCode",0)};
        Write(newer,restored);Expect(p,"Ready",0);
        Check(!(bool)Get(Status(p),"UsesEvidenceV3")&&(string)Get(Status(p),"StatusText")=="记录","legacy snapshot semantics remain unchanged");
        var saved=Json.Serialize(restored);
        using(var locked=new FileStream(Path.Combine(Path.GetDirectoryName(newer),"pdmap.bin"),FileMode.Open,FileAccess.Read,FileShare.None))Expect(p,"Ready",0);
        ((Dictionary<string,object>)restored["Conclusion"])["AnalysisId"]="old";Write(newer,restored);Expect(p,"Invalid");
        restored=Json.DeserializeObject(saved) as Dictionary<string,object>;restored["Id"]="mismatch";((Dictionary<string,object>)restored["Conclusion"])["AnalysisId"]="mismatch";Write(newer,restored);Expect(p,"Invalid");
        restored=Json.DeserializeObject(saved) as Dictionary<string,object>;((Dictionary<string,object>)restored["Conclusion"])["ParametersSha256"]=new String('B',64);Write(newer,restored);Expect(p,"Invalid");
        restored=Json.DeserializeObject(saved) as Dictionary<string,object>;((Dictionary<string,object>)((object[])restored["Files"])[0])["Path"]="../outside.bin";Write(newer,restored);Expect(p,"Invalid");
        restored=Json.DeserializeObject(saved) as Dictionary<string,object>;((Dictionary<string,object>)restored["Conclusion"])["Regions"]=new[]{Map("PhaseMask",1,"PositionM",0,"ActionCode",4)};Write(newer,restored);Expect(p,"Invalid");
        restored=Json.DeserializeObject(saved) as Dictionary<string,object>;((Dictionary<string,object>)restored["Conclusion"])["GeneratedUtc"]=time.AddHours(4).ToString("o");Write(newer,restored);Expect(p,"Invalid");
        File.WriteAllText(newer,"{broken");File.SetLastWriteTimeUtc(newer,time.AddHours(3));Expect(p,"Invalid");
        Write(newer,Json.DeserializeObject(saved));Fixture(p,".pending_uncommitted",time.AddHours(8),new[]{3});Fixture(p,".replaced_backup",time.AddHours(9),new[]{3});Fixture(p,".deleted_removed",time.AddHours(10),new[]{3});Expect(p,"Ready",0);
        string legacy=Path.Combine(p,"Analysis_2026-09-01-01'02'03.04.data");File.WriteAllText(legacy,"legacy");File.SetLastWriteTimeUtc(legacy,time.AddHours(4));Expect(p,"NoConclusion");
        string session=Path.Combine(p,"session.json");Write(session,Map("schema","HVX.AnalysisSession.Utf8","version",1));File.SetLastWriteTimeUtc(session,time.AddHours(5));Expect(p,"NoConclusion");
        File.WriteAllText(session,"{broken");File.SetLastWriteTimeUtc(session,time.AddHours(5));Expect(p,"Invalid");
        string oversized=Fixture(p,"oversized",time.AddHours(6),new[]{3});File.WriteAllText(oversized,new String(' ',4*1024*1024+1));File.SetLastWriteTimeUtc(oversized,time.AddHours(6));Expect(p,"Invalid");
        string pureLegacy=Path.Combine(qa,"legacy");Directory.CreateDirectory(pureLegacy);File.WriteAllText(Path.Combine(pureLegacy,"Analysis_2026-09-01-01'02'03.04.bin"),"x");Expect(pureLegacy,"NoConclusion");
    }
    static void PersistenceCases(string build,string qa)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));Type type=assembly.GetType("HVX.PdReview.PdReviewControl",true);
        assembly.GetType("HVX.PdReview.ReviewAnalysisPreferences",true).GetField("PathProvider",All).SetValue(null,(Func<string>)(()=>Path.Combine(qa,"preferences.json")));
        string p=Path.Combine(qa,"actual");Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"ITEM.in"),"电缆长度\t414\r\n",Encoding.UTF8);File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\t\t0\r\n1\t始端\t户内冷缩\t0\r\n1\t终端\t户内冷缩\t414\r\n",Encoding.UTF8);
        string loc=Path.Combine(p,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[256]);
        using(Control review=(Control)Activator.CreateInstance(type))
        {
            Set(review,"PromptSavedAnalysisOnProjectLoad",false);Set(review,"ProjectPath",p);object tree=Get(review,"AnalysisTree");var catalog=(double[,])Call(tree,"GetDataCatalog");
            var parameters=new double[12,36];var waves=new double[12,2048];
            for(int r=0;r<12;r++){parameters[r,0]=catalog[0,0];parameters[r,1]=catalog[0,1];parameters[r,2]=13.4;parameters[r,3]=30+r*10;parameters[r,5]=414;parameters[r,6]=170;parameters[r,7]=125e6;parameters[r,9]=r;parameters[r,10]=100;parameters[r,11]=710;parameters[r,12]=610;parameters[r,13]=200;parameters[r,15]=180+r;parameters[r,16]=180;parameters[r,20]=100;parameters[r,21]=.92;parameters[r,23]=1;parameters[r,24]=1;parameters[r,32]=2047;parameters[r,33]=.91;parameters[r,35]=1;for(int i=0;i<2048;i++)waves[r,i]=180*Math.Exp(-Math.Pow((i-100)/14d,2))+60*Math.Exp(-Math.Pow((i-710)/23d,2));}
            Check((bool)Call(review,"LoadDataMatrices",parameters,waves),"actual source fixture loads");Call(tree,"MarkAnalyzed",new object[]{new[]{loc}});
            Check((bool)Call(review,"SaveNamedAnalysis","before-conclusion"),"save analysis without conclusion");Expect(p,"NoConclusion");
            object spectrum=type.GetField("spectrum",All).GetValue(review);
            string runtime=Path.Combine(build,"modules","DataAnalysis","e15c7e4a92bf60e8135ac904.V1.0.15.dll");Set(spectrum,"Task15RuntimePath",runtime);Set(spectrum,"OperatingVoltageKv",8.7d);
            Check((bool)Call(review,"RequestEvaluation"),"real Task15 conclusion: "+Get(review,"EvaluationDiagnostic"));
            Check((bool)Get(review,"HasUnsavedAnalysisChanges"),"generating conclusion marks saved analysis dirty");object result=Get(review,"LatestEvaluationResult");var regions=(double[,])Get(result,"LocationMatrix");Check(regions.GetLength(0)>0,"real Task15 supplies structured region grades");int grade=Enumerable.Range(0,regions.GetLength(0)).Max(r=>(int)regions[r,8]);
            Check((bool)Call(review,"OverwriteLoadedAnalysis"),"save real conclusion snapshot: "+Get(review,"LastError"));Expect(p,"Ready",grade);
            string id=(string)Get(review,"LoadedAnalysisId"),manifest=Path.Combine(p,"Analysis report",id,"analysis.json");var first=Read(manifest);string created=(string)first["CreatedUtc"],generated=(string)((Dictionary<string,object>)first["Conclusion"])["GeneratedUtc"];
            Check(!(bool)Get(review,"HasUnsavedAnalysisChanges"),"saved conclusion clean");
            Check((bool)Call(review,"LoadSavedAnalysis",id),"reload saved conclusion");Check((bool)Call(review,"OverwriteLoadedAnalysis"),"resave unchanged loaded analysis");Expect(p,"Ready",grade);
            var second=Read(manifest);Check((string)second["CreatedUtc"]==created&&String.CompareOrdinal((string)second["SavedUtc"],(string)first["SavedUtc"])>=0,"overwrite preserves creation and advances save time");Check((string)((Dictionary<string,object>)second["Conclusion"])["GeneratedUtc"]==generated,"reload/resave preserves original conclusion generation time");
            Check(!(bool)Call(review,"LoadSavedAnalysis","missing-id"),"failed load rejected");Check((bool)Call(review,"SaveNamedAnalysis","after-failed-load"),"save after failed load");Expect(p,"Ready",grade);
            Set(review,"AnalysisCorrelationErrorPercent",17d);Check((bool)Get(review,"HasUnsavedAnalysisChanges"),"parameter change dirty");Check((bool)Call(review,"OverwriteLoadedAnalysis"),"save modified analysis without regenerating conclusion");Expect(p,"NoConclusion");
            Check((bool)Call(review,"LoadSavedAnalysis",id),"reload earlier valid conclusion");Check((bool)Call(review,"SaveNamedAnalysis","copy-valid"),"save as new analysis preserves unchanged conclusion");Expect(p,"Ready",grade);Check((string)Get(Status(p),"AnalysisId")==Get(review,"LoadedAnalysisId").ToString(),"save-as binds conclusion to new identity");
            Call(review,"SelectSourceRow",0);Call(review,"DeleteCurrent");Check((bool)Call(review,"OverwriteLoadedAnalysis"),"save acceptance change");Expect(p,"NoConclusion");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);string build=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"insulation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);reader=Assembly.LoadFrom(Path.Combine(build,"modules","ProjectManagement","Xiongxin.OWTS.ProjectManagement.dll")).GetType("HVX.Controls.ProjectInsulationStatusReader",true);ReaderCases(qa);PersistenceCases(build,qa);Console.WriteLine("PROJECT_INSULATION_STATUS_PASS="+checks);Console.WriteLine("EVIDENCE="+qa);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
