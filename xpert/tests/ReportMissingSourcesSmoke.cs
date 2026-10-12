using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ReportMissingSourcesSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object P(object o,string name){return o.GetType().GetProperty(name,All).GetValue(o,null);}
    static object F(object o,string name){return o.GetType().GetField(name,All).GetValue(o);}
    static object Call(object o,string name,params object[] args){var t=o as Type??o.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static string Hash(string path){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
    static void Write(string path,object value){File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false));}
    static string MatrixHash(double[,] matrix)
    {using(var stream=new MemoryStream()){using(var w=new BinaryWriter(stream,Encoding.UTF8,true)){w.Write(matrix.GetLength(0));w.Write(matrix.GetLength(1));foreach(double v in matrix)w.Write(v);}using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(stream.ToArray())).Replace("-","");}}
    static double[,] Matrix()
    {
        var m=new double[2,36];for(int r=0;r<2;r++){
            m[r,0]=r+1;m[r,1]=(r+1)*10;m[r,2]=8.7;m[r,3]=r;m[r,5]=200;m[r,6]=170;m[r,7]=250;
            m[r,10]=10;m[r,11]=20;m[r,12]=10;m[r,13]=r==0?79.5:120.5;
            m[r,15]=100+r;m[r,16]=m[r,15];m[r,22]=.8;m[r,23]=.1;m[r,24]=.9;m[r,35]=1;
        }return m;
    }
    static string[] Source(string path,int phase,int id,bool done=true,bool chosen=true)
    {return new[]{path,phase.ToString(),id.ToString(),"8.7",done?"1":"0",chosen?"1":"0","", "0","100","NaN","NaN"};}
    static void Header(string path,string context="{}")
    {File.WriteAllText(path,String.Join("\r\n",Enumerable.Range(0,10).Select(i=>"header "+i).Concat(new[]{"Acquisition context\t"+context}))+"\r\n",new UTF8Encoding(false));}
    [STAThread]static int Main(string[] args)
    {
        try{
            string qa=Path.GetFullPath(args[0]),test=Path.Combine(Path.GetFullPath(args[1]),"sources-"+Guid.NewGuid().ToString("N"));
            string root=Path.Combine(test,"project"),archive=Path.Combine(root,"Analysis report","selection");Directory.CreateDirectory(archive);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆长度 = 200\r\n相电压 = 8.7\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t终端\t冷缩\t200\r\n",Encoding.UTF8);
            string first=Path.Combine(root,"A.loc"),second=Path.Combine(root,"B.loc"),empty=Path.Combine(root,"empty.loc"),unrelated=Path.Combine(root,"unused.loc");
            Header(first);Header(second);File.WriteAllBytes(empty,new byte[0]);File.WriteAllText(unrelated,"truncated unrelated header");
            var matrix=Matrix();string matrixHash=MatrixHash(matrix);
            var context=new Dictionary<string,object>{{"Schema","HVX.ReportInput.1"},{"MatrixSha256",matrixHash},{"Available",true},{"SnapEnabled",true},
                {"EndpointTolerancePercent",1d},{"JointTolerancePercent",1d},{"ProjectionRows",new[]{0,1}},{"RawPositionsM",new[]{79.5,120.5}},{"PositionsM",new[]{80d,120d}},
                {"ConclusionRows",new[]{0,1}},{"Joints",new[]{new[]{0d,80d,200d},new[]{0d,120d,200d},new double[0]}},{"OperatingVoltageKv",8.7},
                {"SelectedRecordCount",4},{"PendingSelectedRecordCount",2},{"AcquisitionReferenceOnlyRows",new int[0]}};
            var settings=new Dictionary<string,string>{{"EffectiveSelectionVersion","1"},{"EffectiveSelectionMask","11"},{"EffectiveSelectionMatrixSha256",matrixHash},{"LocationFilterEnabled","0"},{"LocationFilterStrength","40"},{"ReportInputContext",Json.Serialize(context)}};
            var sources=new List<string[]>{Source("A.loc",1,10),Source("B.loc",2,20),Source("empty.loc",1,30,false),Source("missing.loc",1,40,false),Source("unused.loc",1,50,false,false)};
            string parameters=Path.Combine(archive,"parameters.bin"),session=Path.Combine(archive,"session.json"),manifest=Path.Combine(archive,"analysis.json");
            using(var w=new BinaryWriter(File.Create(parameters))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(2);w.Write(36);foreach(double v in matrix)w.Write(v);}
            Action save=()=>{
                Write(session,new{schema="HVX.AnalysisSession.Utf8",version=1,files=sources,settings=settings.Select(x=>new[]{x.Key,x.Value}).ToArray()});
                Write(manifest,new{Schema="HVX.NamedAnalysis",Version=1,Id="selection",Name="selection",CreatedUtc="2026-10-11T00:00:00Z",SavedUtc="2026-10-11T00:01:00Z",Charts=new object[0],
                    Conclusion=new{Schema="HVX.AnalysisConclusion.1",AnalysisId="selection",GeneratedUtc="2026-10-11T00:00:30Z",Api="Task15",SummaryText="FULL ORIGINAL SCOPE",ParametersSha256=Hash(parameters),SessionSha256=Hash(session)},
                    Files=new[]{parameters,session}.Select(p=>new{Path=Path.GetFileName(p),Sha256=Hash(p),Bytes=new FileInfo(p).Length}).ToArray()});};
            var module=Assembly.LoadFrom(Path.Combine(qa,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));var reader=module.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);
            save();string[] inputs={parameters,session,manifest,first,second,empty,unrelated};string[] hashes=inputs.Select(Hash).ToArray();
            object package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasEffectiveSelectionSnapshot")&&(bool)P(package,"HasReportInputSnapshot"),"unrelated empty/missing/truncated sources do not reject validated saved selection: "+P(package,"EffectiveSelectionStatus"));
            Check((int)P(package,"SkippedSourceRecordCount")==2&&(int)P(package,"UnavailableResultRowCount")==0,"empty and missing sources counted without losing valid result rows");
            Check(((double[,])P(package,"StatisticsMatrix")).GetLength(0)==2&&((double[,])F(package,"ConclusionInput"))[0,13]==80,"surviving statistics and conclusion retain verified projection");
            Check((int)P(package,"PendingSelectedRecordCount")==0,"omitted empty pending selections do not remain pending");
            Check((bool)P(package,"HasSavedConclusionSnapshot"),"unchanged result scope may retain saved summary");
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl",true))){
                control.GetType().GetProperty("PromptAnalysisOnLoad").SetValue(control,false,null);
                Check((bool)Call(control,"LoadAnalysis",archive),"report UI accepts partial source catalog without requiring a resave: "+P(control,"LastError"));
                Check(((string)P(control,"ConclusionText")).Contains("已跳过 2 条缺失或空数据"),"operator sees short partial-load scope notice");
                control.GetType().GetProperty("LanguageIndex").SetValue(control,1,null);
                Check(((string)Call(control,"PartialSourceLoadWarning")).Contains("Skipped 2"),"partial-load notice has English wording");
            }
            Check(inputs.Select(Hash).SequenceEqual(hashes)&&!File.Exists(Path.Combine(root,"missing.loc")),"reading does not alter original sources or archive");
            sources[1]=Source("missing-B.loc",2,20);save();package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasReportInputSnapshot")&&(bool)P(package,"HasEffectiveSelectionSnapshot"),"missing referenced source is removed after original snapshot validation");
            Check(((double[,])P(package,"LocationMatrix")).GetLength(0)==2&&((double[,])P(package,"StatisticsMatrix")).GetLength(0)==1,"full raw matrix retained, unavailable rows excluded from statistics");
            var input=(double[,])F(package,"ConclusionInput");Check(input.GetLength(0)==1&&input[0,0]==1&&input[0,13]==80,"conclusion excludes unavailable source, not unrelated valid phase");
            Check(((double[][])F(package,"ConclusionJoints"))[1].Length==0,"geometry for fully omitted phase is removed from conclusion input");
            Check((int)P(package,"UnavailableResultRowCount")==1&&!(bool)P(package,"HasSavedConclusionSnapshot")&&String.IsNullOrEmpty((string)P(package,"ConclusionText")),"old full-scope conclusion cannot masquerade as partial result");
            var image=Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportImageInfo",true));image.GetType().GetProperty("Sequence").SetValue(image,1,null);image.GetType().GetProperty("IncludedInReport").SetValue(image,true,null);
            var imageArray=Array.CreateInstance(image.GetType(),1);imageArray.SetValue(image,0);
            var chartManifest=(Dictionary<string,object>)Json.DeserializeObject("{\"Charts\":[{\"Sequence\":1,\"Rows\":[1]}]}");
            Call(module.GetType("HVX.ReportGenerator.ReportEffectiveSelection",true),"ValidateCharts",package,chartManifest,imageArray,new List<string>());
            image.GetType().GetProperty("IncludedInReport").SetValue(image,true,null);
            Check((bool)P(image,"SourceUnavailable")&&!(bool)P(image,"IncludedInReport"),"chart with missing source cannot be re-included as current evidence");
            File.WriteAllBytes(first,new byte[0]);package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasReportInputSnapshot")&&((double[,])P(package,"StatisticsMatrix")).GetLength(0)==0&&((double[,])F(package,"ConclusionInput")).GetLength(0)==0,"all unavailable sources load as empty valid scope, never retained old results");
            Header(first);sources[1]=Source("B.loc",2,20);save();File.WriteAllText(second,"truncated referenced header");package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((string)P(package,"EffectiveSelectionStatus")).Contains("Incomplete acquisition quality header"),"nonempty referenced malformed header remains rejected");
            Header(second,"{\"overRange\":true}");package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((string)P(package,"EffectiveSelectionStatus")).Contains("Risk acquisition"),"real overrange evidence remains excluded, not swallowed by partial loading");
            Header(second,"{\"overRange\":true,\"overRange\":false}");package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((string)P(package,"EffectiveSelectionStatus")).Contains("Duplicate acquisition quality"),"duplicate evidence fields cannot hide real overrange flags");
            Header(second);settings["AcquisitionQualities"]="{\"2:20\":{\"overRange\":true}}";sources[1]=Source("missing-B.loc",2,20);save();package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((int[])P(package,"AcquisitionReferenceOnlyRows")).SequenceEqual(new[]{1}),"missing raw source cannot erase persisted risk or make risky saved selection valid");
            var riskContext=new Dictionary<string,object>(context);riskContext["ProjectionRows"]=new[]{0};riskContext["RawPositionsM"]=new[]{79.5};riskContext["PositionsM"]=new[]{80d};riskContext["ConclusionRows"]=new[]{0};
            riskContext["Joints"]=new[]{new[]{0d,80d,200d},new double[0],new double[0]};riskContext["AcquisitionReferenceOnlyRows"]=new[]{1};
            settings["EffectiveSelectionMask"]="10";settings["ReportInputContext"]=Json.Serialize(riskContext);save();package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasEffectiveSelectionSnapshot")&&(bool)P(package,"HasReportInputSnapshot")&&((int[])P(package,"AcquisitionReferenceOnlyRows")).SequenceEqual(new[]{1}),"properly excluded risk evidence stays loadable and retained even when original is missing");
            Check(((double[,])P(package,"StatisticsMatrix")).GetLength(0)==1&&((double[,])F(package,"ConclusionInput")).GetLength(0)==1,"missing excluded risk does not discard other valid current results");
            settings["EffectiveSelectionMask"]="11";settings["ReportInputContext"]=Json.Serialize(context);
            settings.Remove("AcquisitionQualities");sources[1]=Source("B.loc",2,20);save();File.AppendAllText(session," ");package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((string)P(package,"EffectiveSelectionStatus")).Contains("size changed"),"changed saved session still fails original file binding");
            save();byte[] original=File.ReadAllBytes(parameters),changed=(byte[])original.Clone();changed[changed.Length-1]^=1;File.WriteAllBytes(parameters,changed);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot"),"same-length changed matrix cannot pass its original saved selection hash");File.WriteAllBytes(parameters,original);
            sources.Add(Source("../escape.loc",1,60));save();package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot")&&((string)P(package,"EffectiveSelectionStatus")).Contains("leaves its project"),"missing traversal path remains rejected rather than silently omitted");
            Console.WriteLine("REPORT_MISSING_SOURCES_PASS="+checks+"; hardware=false; product_main=false; user_project_writes=false");Console.WriteLine("EVIDENCE="+test);return 0;
        }catch(Exception e){Console.Error.WriteLine(e is TargetInvocationException?((TargetInvocationException)e).InnerException:e);return 1;}
    }
}
