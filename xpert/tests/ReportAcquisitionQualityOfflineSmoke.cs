using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Pure managed adapter tests. No report/location/conclusion algorithm, product
// entry point, worker, Office/PDF converter, SDK or device is executed.
internal static class ReportAcquisitionQualityOfflineSmoke
{
    const BindingFlags B=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;static Assembly report;static Type adapter,packageType;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object target,string name,params object[] args){var type=target as Type??target.GetType();try{return type.GetMethods(B).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target is Type?null:target,args);}catch(TargetInvocationException e){throw e.InnerException;}}
    static object P(object target,string name){return target.GetType().GetProperty(name,B).GetValue(target,null);}
    static void S(object target,string name,object value){target.GetType().GetProperty(name,B).SetValue(target,value,null);}
    static void Reject(Action action,string why){bool rejected=false;try{action();}catch(Exception e){rejected=e is InvalidDataException||e is InvalidOperationException||e is ArgumentException;}Check(rejected,why);}
    static Dictionary<string,object> Risk(){return new Dictionary<string,object>{{"manualOverRangeSaved",true},{"overRange",true},{"overRangeAccepted",true},{"peakValidity","clipped-or-uncertain"},{"locationValidity","not-validated"},{"rawSha256",new String('A',64)}};}
    static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=16*1024*1024};}
    static object Read(string root,Dictionary<string,string> settings,Dictionary<string,object> session,double[,] matrix)
    {var package=Activator.CreateInstance(packageType);S(package,"ProjectPath",root);Call(adapter,"Read",package,settings,session,matrix,new List<string>());return package;}
    [STAThread]static int Main(string[] args)
    {
        try{
            Console.OutputEncoding=Encoding.UTF8;string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            report=Assembly.LoadFrom(Path.Combine(build,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            adapter=report.GetType("HVX.ReportGenerator.ReportAcquisitionQuality",true);packageType=report.GetType("HVX.ReportGenerator.ReportAnalysisPackage",true);
            var matrix=new double[2,36];matrix[0,0]=matrix[1,0]=1;matrix[0,1]=7;matrix[1,1]=8;
            var settings=new Dictionary<string,string>{{"AcquisitionQualities",Json().Serialize(new Dictionary<string,object>{{"1:7",Risk()}})}};
            var session=new Dictionary<string,object>();var package=Read(qa,settings,session,matrix);
            Check(((int[])P(package,"AcquisitionReferenceOnlyRows")).SequenceEqual(new[]{0}),"saved risk identity maps to raw matrix without consulting default validity");
            Check(((string)P(package,"AcquisitionQualityJson")).Contains("clipped-or-uncertain"),"report package retains explicit quality evidence");
            session["files"]=new object[]{new object[]{"missing-original.loc","1","7"}};
            Check(((int[])P(Read(qa,settings,session,matrix),"AcquisitionReferenceOnlyRows")).Length==1,"a missing original cannot erase persisted risk evidence");session.Clear();
            var wrongPhase=(double[,])matrix.Clone();wrongPhase[0,0]=2;Reject(()=>Read(qa,settings,session,wrongPhase),"wrong-phase matrix identity cannot hide a saved risk source");
            Check(((int[])P(Read(qa,new Dictionary<string,string>(),session,matrix),"AcquisitionReferenceOnlyRows")).Length==0,"unrecorded historical metadata is not fabricated as valid or as a risk");
            foreach(string bad in new[]{"[]","{\"1:7\":{\"overRange\":\"true\"}}","{\"1:7\":{\"overRange\":false}}","{\"1:7\":{\"overRange\":true,\"overRange\":false}}","{\"1:7\":{\"overRange\":true,\"\\u006fverRange\":false}}",new String(' ',16*1024*1024+1)}){
                settings["AcquisitionQualities"]=bad;Reject(()=>Read(qa,settings,session,matrix),"invalid, duplicate or relabelled saved quality is rejected");
            }
            settings.Clear();string loc=Path.Combine(qa,"legacy.loc");
            Action<string> header=context=>{using(var writer=new StreamWriter(loc,false,Encoding.UTF8))for(int i=0;i<11;i++)writer.WriteLine(i==9?"Acquisition context\t"+context:"Reserved\t0");};
            header(Json().Serialize(Risk()));session["files"]=new object[]{new object[]{"legacy.loc","1","7"}};
            package=Read(qa,settings,session,matrix);Check(((int[])P(package,"AcquisitionReferenceOnlyRows")).SequenceEqual(new[]{0}),"direct old report opening discovers risk from existing bounded LOC header");
            var large=(double[,])matrix.Clone();large[0,1]=1e15;session["files"]=new object[]{new object[]{"legacy.loc","1","1E+15"}};
            Check(((int[])P(Read(qa,settings,session,large),"AcquisitionReferenceOnlyRows")).SequenceEqual(new[]{0}),"scientific-notation legacy IDs normalize without dropping risk");session["files"]=new object[]{new object[]{"legacy.loc","1","7"}};
            header("{\"overRange\":true,\"overRange\":false}");Reject(()=>Read(qa,settings,session,matrix),"duplicate source metadata cannot erase risk in reports");
            header(new String(' ',65537));Reject(()=>Read(qa,settings,session,matrix),"source header over budget is rejected before deserialization");
            string dem=Path.Combine(qa,"legacy.dem");using(var writer=new StreamWriter(dem,false,Encoding.UTF8))for(int i=0;i<16;i++)writer.WriteLine(i==12?"Acquisition context:\t"+Json().Serialize(Risk()):"Reserved:0");
            session["files"]=new object[]{new object[]{"legacy.dem","1","7"}};Check(((int[])P(Read(qa,settings,session,matrix),"AcquisitionReferenceOnlyRows")).Length==1,"old DEM colon/tab metadata also keeps explicit risk");
            session["files"]=new object[]{new object[]{"../outside.loc","1","7"}};Reject(()=>Read(qa,settings,session,matrix),"quality migration does not follow project traversal paths");

            string sourceProject=Path.Combine(build,"ReviewAcquisitionQualityOfflineSmoke-synthetic","review");
            string archive=Directory.GetFiles(sourceProject,"analysis.json",SearchOption.AllDirectories).Single();
            package=Call(report.GetType("HVX.ReportGenerator.AnalysisPackageReader",true),"Read",archive);
            Check((bool)P(package,"HasEffectiveSelectionSnapshot")&&(bool)P(package,"HasReportInputSnapshot"),"real saved-risk package hashes and excluded report input remain loadable: "+P(package,"ReportInputStatus"));
            Check(((int[])P(package,"AcquisitionReferenceOnlyRows")).Length==1&&((double[,])P(package,"StatisticsMatrix")).GetLength(0)==0,"risk pulses are preserved as evidence but excluded from report statistics");
            using(var control=(Control)Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportGeneratorControl",true))){
                S(control,"PromptAnalysisOnLoad",false);Check((bool)Call(control,"LoadAnalysis",archive),"risk-only report review can load without running an algorithm");
                Check(((string)P(control,"ConclusionText")).Contains("采集质量")||((string)P(control,"ConclusionText")).Contains("Acquisition quality"),"report conclusion view visibly retains the short quality warning");
                var values=new Dictionary<string,string>();Call(control,"ApplyReportValues",values);
                Check(values["report.conclusion"].Contains("保存不等于")||values["report.conclusion"].Contains("Saving does not"),"ordinary exported report values preserve warning even without an algorithm result");
                var context=(Dictionary<string,object>)Call(control,"ConclusionLearningContext");Check(Object.Equals(context["acquisition_reference_only"],true),"report learning context cannot hide acquisition risk");
                Reject(()=>Call(report.GetType("HVX.ReportGenerator.ReportConclusionLearning",true),"Save",Path.Combine(qa,"must-not-write"),"original","current","corrected",3,"","en",context,new double[8],true),"low-level report feedback refuses risk as learning truth");
                Check(!Directory.Exists(Path.Combine(qa,"must-not-write")),"rejected feedback creates no local learning transactions");
            }
            var image=Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ReportImageInfo",true));S(image,"AcquisitionReferenceOnly",true);S(image,"IncludedInReport",true);S(image,"IsHistoricalOrStale",false);
            Check(!(bool)P(image,"IncludedInReport"),"risk chart cannot be reselected even if generic stale flag is cleared");
            // Synthesize a pre-fix archive whose accepted flag/mask included a
            // risk source. Rebind only the synthetic files; never edit user data.
            string legacyRoot=Path.Combine(qa,"old-report"),legacyArchive=Path.Combine(legacyRoot,"Analysis report","old");Directory.CreateDirectory(legacyArchive);
            foreach(string source in Directory.GetFiles(Path.GetDirectoryName(archive)))File.Copy(source,Path.Combine(legacyArchive,Path.GetFileName(source)));
            foreach(string name in new[]{"ITEM.in","CABLE.in"})File.Copy(Path.Combine(sourceProject,name),Path.Combine(legacyRoot,name));
            string sessionPath=Path.Combine(legacyArchive,"session.json"),manifestPath=Path.Combine(legacyArchive,"analysis.json"),parametersPath=Path.Combine(legacyArchive,"parameters.bin");
            var oldSession=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(sessionPath));
            var oldSettings=((object[])oldSession["settings"]).Cast<object[]>().Where(r=>!Object.Equals(r[0],"AcquisitionQualities")).ToDictionary(r=>(string)r[0],r=>(string)r[1]);
            var raw=(double[,])P(package,"LocationMatrix");raw[0,35]=1;
            using(var writer=new BinaryWriter(File.Create(parametersPath))){writer.Write(Encoding.ASCII.GetBytes("HVXMAT01"));writer.Write(raw.GetLength(0));writer.Write(raw.GetLength(1));foreach(double v in raw)writer.Write(v);}
            oldSettings["EffectiveSelectionMask"]="1";oldSettings["EffectiveSelectionMatrixSha256"]=(string)Call(report.GetType("HVX.ReportGenerator.ReportEffectiveSelection",true),"MatrixHash",raw);
            oldSession["settings"]=oldSettings.Select(p=>new[]{p.Key,p.Value}).ToArray();
            foreach(var row in ((object[])oldSession["files"]).Cast<object[]>())File.Copy(Path.Combine(sourceProject,(string)row[0]),Path.Combine(legacyRoot,(string)row[0]));
            File.WriteAllText(sessionPath,Json().Serialize(oldSession));var manifest=(Dictionary<string,object>)Json().DeserializeObject(File.ReadAllText(manifestPath));
            foreach(var binding in ((object[])manifest["Files"]).Cast<Dictionary<string,object>>()){
                string path=Path.Combine(legacyArchive,(string)binding["Path"]);binding["Bytes"]=new FileInfo(path).Length;
                using(var sha=System.Security.Cryptography.SHA256.Create())binding["Sha256"]=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","");
            }
            File.WriteAllText(manifestPath,Json().Serialize(manifest));var oldPackage=Call(report.GetType("HVX.ReportGenerator.AnalysisPackageReader",true),"Read",legacyArchive);
            Check(((int[])P(oldPackage,"AcquisitionReferenceOnlyRows")).Length==1&&!(bool)P(oldPackage,"HasEffectiveSelectionSnapshot"),"old accepted risk archive is rejected before valid statistics publication");
            Check(((string)P(oldPackage,"EffectiveSelectionStatus")).Contains("Risk acquisition"),"old risk rejection names quality, not an unexplained algorithm failure");
            Console.WriteLine("REPORT_ACQUISITION_QUALITY_PASS="+checks+";ProductEntry=false;Algorithms=false;Worker=false;Office=false;SDK=false;UserDataWrites=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
