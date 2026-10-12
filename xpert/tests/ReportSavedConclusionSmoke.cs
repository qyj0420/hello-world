using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ReportSavedConclusionSmoke
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Call(object obj,string name,params object[] args){Type t=obj as Type??obj.GetType();return t.GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(obj is Type?null:obj,args);}
    static object P(object obj,string name){return obj.GetType().GetProperty(name,All).GetValue(obj,null);}
    static object F(object obj,string name){return obj.GetType().GetField(name,All).GetValue(obj);}
    static string Hash(string path){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-","");}
    static string MatrixHash(){using(var s=new MemoryStream()){using(var w=new BinaryWriter(s,Encoding.UTF8,true)){w.Write(1);w.Write(36);for(int i=0;i<36;i++)w.Write(0d);}using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s.ToArray())).Replace("-","");}}
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static void Write(string path,object value){File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false));}
    [STAThread]static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            string qa=Path.Combine(Path.GetFullPath(args[1]),Guid.NewGuid().ToString("N")),root=Path.Combine(qa,"Project"),archive=Path.Combine(root,"Analysis report","analysis-test");Directory.CreateDirectory(archive);
            File.WriteAllText(Path.Combine(root,"ITEM.in"),"电缆长度 = 170\r\n相电压 = 8.7\r\n",Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"CABLE.in"),"0\tALL\r\n1\t始端\t冷缩\t0\r\n1\t终端\t冷缩\t170\r\n",Encoding.UTF8);
            string parameters=Path.Combine(archive,"parameters.bin"),session=Path.Combine(archive,"session.json"),manifestPath=Path.Combine(archive,"analysis.json");
            using(var w=new BinaryWriter(File.Create(parameters))){w.Write(Encoding.ASCII.GetBytes("HVXMAT01"));w.Write(1);w.Write(36);for(int i=0;i<36;i++)w.Write(0d);}
            Write(session,new{schema="HVX.AnalysisSession.Utf8",version=1,ConclusionText="OLD SESSION ADVICE - DO NOT BORROW",settings=new[]{
                new[]{"EffectiveSelectionVersion","1"},new[]{"EffectiveSelectionMask","1"},new[]{"EffectiveSelectionMatrixSha256",MatrixHash()},
                new[]{"LocationFilterEnabled","0"},new[]{"LocationFilterStrength","40"}}});
            string text="已保存结论：重点关注；来自当前分析的全部相和区域。";
            var snapshot=new Dictionary<string,object>{{"Schema","HVX.AnalysisConclusion.1"},{"AnalysisId","analysis-test"},{"GeneratedUtc","2026-09-29T08:00:00.0000000Z"},{"Api","Task15"},{"SummaryText",text},{"ParametersSha256",Hash(parameters)},{"SessionSha256",Hash(session)},{"Regions",new[]{new{PhaseMask=7,PositionM=80d,ActionCode=2}}}};
            var manifest=new Dictionary<string,object>{{"Schema","HVX.NamedAnalysis"},{"Version",1},{"Id","analysis-test"},{"Name","QA analysis"},{"CreatedUtc","2026-09-29T07:00:00.0000000Z"},{"SavedUtc","2026-09-29T08:01:00.0000000Z"},{"Conclusion",snapshot},{"Charts",new object[0]},
                {"Files",new[]{parameters,session}.Select(p=>new{Path=Path.GetFileName(p),Sha256=Hash(p),Bytes=new FileInfo(p).Length}).ToArray()}};
            Write(manifestPath,manifest);
            Assembly module=Assembly.LoadFrom(Path.Combine(Path.GetFullPath(args[0]),"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));Type reader=module.GetType("HVX.ReportGenerator.AnalysisPackageReader",true);
            object package=Call(reader,"Read",archive);
            Check((bool)P(package,"HasEffectiveSelectionSnapshot"),"saved conclusion fixture carries a valid current statistics selection");
            Check((string)P(package,"ConclusionText")==text&&(bool)P(package,"HasSavedConclusionSnapshot"),"nested structured summary is read and validated");
            byte[] before=File.ReadAllBytes(manifestPath);
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl",true)))
            {
                control.GetType().GetProperty("PromptAnalysisOnLoad").SetValue(control,false,null);
                string diagnostic=null;control.GetType().GetProperty("DiagnosticSink").SetValue(control,new Action<string>(message=>diagnostic=message),null);
                Check((bool)Call(control,"LoadAnalysis",archive),"report loads saved analysis: "+P(control,"LastError"));
                Check(((string)P(control,"ConclusionText")).Contains(text)&&((string)P(control,"ConclusionText")).Contains("历史版本已保存结论")&&!((string)P(control,"ConclusionText")).Contains("Evidence V3"),"loading preserves saved legacy conclusion with an operator-facing version notice");
                Check(diagnostic!=null&&diagnostic.Contains("SavedLegacyConclusion")&&diagnostic.Contains("Task15"),"legacy provenance reaches diagnostic sink rather than operator text");
                Check((bool)F(control,"preferSavedConclusionSnapshot"),"snapshot mode selected on load");
                Call(control,"RefreshConclusion");Check(((string)P(control,"ConclusionText")).Contains(text),"ordinary refresh retains saved summary");
                Button button=(Button)F(control,"aiButton");typeof(Button).GetMethod("OnClick",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(button,new object[]{EventArgs.Empty});
                Check(!(bool)F(control,"preferSavedConclusionSnapshot"),"explicit Generate conclusion opts into recalculation");
                Check(before.SequenceEqual(File.ReadAllBytes(manifestPath)),"explicit regeneration never rewrites saved analysis");
                Check((bool)Call(control,"LoadAnalysis",archive)&&((string)P(control,"ConclusionText")).Contains(text),"reload restores persisted summary with legacy provenance");
                Call(control,"SetLocationMatrix",new double[0,36]);Check(!(bool)F(control,"preferSavedConclusionSnapshot"),"a changed matrix cannot retain snapshot-current flag");
            }
            snapshot["AnalysisId"]="other-analysis";Write(manifestPath,manifest);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasSavedConclusionSnapshot")&&String.IsNullOrEmpty((string)P(package,"ConclusionText")),"foreign analysis ID is rejected without borrowing session text");
            snapshot["AnalysisId"]="analysis-test";snapshot["Schema"]="unknown";Write(manifestPath,manifest);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasSavedConclusionSnapshot"),"unknown schema is not a saved validated conclusion");
            snapshot["Schema"]="HVX.AnalysisConclusion.1";snapshot["GeneratedUtc"]="2026-09-29T08:02:00Z";Write(manifestPath,manifest);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasSavedConclusionSnapshot"),"conclusion newer than its save is rejected");
            snapshot["GeneratedUtc"]="2026-09-29T08:00:00Z";Write(manifestPath,manifest);
            byte[] raw=File.ReadAllBytes(parameters),changed=(byte[])raw.Clone();changed[changed.Length-1]=1;File.WriteAllBytes(parameters,changed);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasSavedConclusionSnapshot"),"same-length parameter mutation fails actual snapshot hash");File.WriteAllBytes(parameters,raw);
            manifest["Conclusion"]="Legacy saved text";Write(manifestPath,manifest);package=Call(reader,"Read",archive);
            Check((string)P(package,"ConclusionText")=="Legacy saved text"&&!(bool)P(package,"HasSavedConclusionSnapshot"),"legacy plain-text compatibility unchanged");
            Write(session,new{schema="HVX.AnalysisSession.Utf8",version=1,ConclusionText="OLD SESSION ADVICE - DO NOT BORROW"});
            manifest["Files"]=new[]{parameters,session}.Select(p=>new{Path=Path.GetFileName(p),Sha256=Hash(p),Bytes=new FileInfo(p).Length}).ToArray();Write(manifestPath,manifest);package=Call(reader,"Read",archive);
            Check(!(bool)P(package,"HasEffectiveSelectionSnapshot"),"missing saved selection cannot be accepted as current statistics");
            using(var control=(Control)Activator.CreateInstance(module.GetType("HVX.ReportGenerator.ReportGeneratorControl",true))){
                control.GetType().GetProperty("PromptAnalysisOnLoad").SetValue(control,false,null);
                string diagnostic=null;control.GetType().GetProperty("DiagnosticSink").SetValue(control,new Action<string>(message=>diagnostic=message),null);
                Check(!(bool)Call(control,"LoadAnalysis",archive),"report refuses missing selection rather than using legacy raw statistics");
                string error=(string)P(control,"LastError");Check(error.Contains("重新打开并保存")&&!error.Contains("Missing saved selection settings"),"selection rejection gives neutral operator instructions");
                Check(diagnostic!=null&&diagnostic.Contains("Missing saved selection settings"),"selection rejection retains the full parser reason in diagnostics");
                control.GetType().GetProperty("LanguageIndex").SetValue(control,1,null);
                Check(!(bool)Call(control,"LoadAnalysis",archive)&&((string)P(control,"LastError")).Contains("Reopen and save")&&!((string)P(control,"LastError")).Contains("Missing saved selection settings"),"English selection rejection is actionable and excludes internal parser text");
            }
            Console.WriteLine("REPORT_SAVED_CONCLUSION_PASS="+checks+";hardware=false;archive_writeback=false");Console.WriteLine("EVIDENCE="+qa);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex is TargetInvocationException?((TargetInvocationException)ex).InnerException:ex);return 1;}
    }
}
