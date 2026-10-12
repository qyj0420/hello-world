using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;

// Managed presentation methods only. No windows, product entry, algorithms,
// local-learning reads, Office, SDK or devices are started.
internal static class CustomerModalTextOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static void Field(object target,string name,object value){target.GetType().GetField(name,F).SetValue(target,value);}
    static object Property(object target,string name){return target.GetType().GetProperty(name,F).GetValue(target,null);}
    static object Call(object target,string name,params object[] args)
    {return target.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static bool CustomerText(string text){return !String.IsNullOrWhiteSpace(text)&&text.Length<250&&!new[]{"512", "MiB", "D:\\", "System.IO", " at ", "q81", "reflectionIndex", "k-nearest"}.Any(text.Contains);}
    static int Main(string[] args)
    {
        try {
            string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
            var data=Assembly.LoadFrom(Path.Combine(build,"modules","DataAnalysis","HVX.PdReview.V1.0.dll"));
            var report=Assembly.LoadFrom(Path.Combine(build,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
            var journalType=data.GetType("HVX.PdReview.ReviewSessionJournal",true);
            journalType.GetField("DirectoryProvider",F).SetValue(null,new Func<string>(()=>qa));
            var journal=Activator.CreateInstance(journalType,true);
            var review=FormatterServices.GetUninitializedObject(data.GetType("HVX.PdReview.PdReviewControl",true));Field(review,"operationJournal",journal);
            string diagnostic="System.IO.InvalidDataException: ledger 512 MiB q81; D:\\internal\\source.loc\r\n at Private.Worker()";
            review.GetType().GetProperty("AnalysisArchiveStatus",F).SetValue(review,diagnostic,null);
            foreach(int language in new[]{0,1,18}) {
                Field(review,"languageIndex",language);
                string failure=(string)Call(review,"CustomerFailure","SyntheticCustomerFailure",diagnostic,"The action failed. Check storage access and retry.","操作未完成。请检查保存位置后重试。");
                Check(CustomerText(failure),"action failure is short and contains no raw diagnostic payload");
                Check(CustomerText((string)Call(review,"ArchiveCustomerFailure",false)),"archive-save failure is short and actionable");
                Check(CustomerText((string)Call(review,"ArchiveCustomerFailure",true)),"archive-load failure does not expose ledger budget/path");
                var suggestion=Activator.CreateInstance(data.GetType("HVX.PdReview.LocalAiAnalysisSuggestion",true),F,null,new object[]{true,12,.205,80d,998,"k-nearest internal reflectionIndex=998"},null);
                string suggestionText=(string)Call(review,"LocalAiSuggestionText",suggestion);
                Check(CustomerText(suggestionText)&&!suggestionText.Contains("998")&&!suggestionText.Contains("20.5"),"manual-review prompt does not present an internal index/similarity as a physical claim");
            }
            Call(journal,"Flush");string logPath=(string)Property(journal,"FilePath");((IDisposable)journal).Dispose();string log=File.ReadAllText(logPath);
            Check(log.Contains("512 MiB")&&log.Contains("source.loc")&&log.Contains("Private.Worker"),"complete diagnostic remains in the operation log");
            Check(log.Contains("reflectionIndex=998")&&log.Contains("similarity="),"local AI implementation details remain auditable outside customer prompts");

            var control=FormatterServices.GetUninitializedObject(report.GetType("HVX.ReportGenerator.ReportGeneratorControl",true));
            var service=Activator.CreateInstance(report.GetType("HVX.ReportGenerator.ConclusionService",true));Field(control,"conclusionService",service);
            var captured=new List<string>();control.GetType().GetProperty("DiagnosticSink",F).SetValue(control,new Action<string>(captured.Add),null);
            var result=Activator.CreateInstance(report.GetType("HVX.ReportGenerator.StateGridReportResult",true));Field(result,"Error",diagnostic);Field(result,"Warning",diagnostic);Field(result,"DocxPath","D:\\internal\\draft.docx");
            foreach(int language in new[]{0,1,18}) {
                Field(control,"languageIndex",language);
                string shortError=(string)Call(control,"ReportOutputMessage",diagnostic,"报告未完成，请检查输出目录后重试。","The report is incomplete. Check the output folder and retry.");
                Check(CustomerText(shortError),"report exception presentation uses only the intended action message");
                Check(((string)Property(control,"LastConclusionDiagnostic")).Contains(diagnostic),"report's existing diagnostic channel retains full failure details");
                string summary=(string)Call(control,"StateGridSummary",result);Check(CustomerText(summary),"supplementary report failure does not append raw path/error");
                Field(result,"Success",true);Check(CustomerText((string)Call(control,"StateGridSummary",result)),"supplementary partial success remains explicit and concise");Field(result,"Success",false);
            }
            Check(captured.Any(s=>s.Contains("draft.docx")&&s.Contains("512 MiB")),"report diagnostics sink receives path and error independently of prompts");
            control.GetType().GetProperty("DiagnosticSink",F).SetValue(control,new Action<string>(s=>{throw new IOException("synthetic diagnostic sink unavailable");}),null);
            Check(CustomerText((string)Call(control,"ReportOutputMessage",diagnostic,"未完成，请重试。","Not completed. Retry.")),"diagnostic sink failure does not replace customer outcome");

            string project=Directory.GetParent(Directory.GetParent(build).FullName).FullName;
            foreach(string relative in new[]{"modules-src/DataAnalysis/src/ReviewAnalysisArchiveUi.cs","modules-src/DataAnalysis/src/ReviewCableJointEditing.cs","modules-src/ReportGenerator/src/ReportGeneratorControl.cs","modules-src/ReportGenerator/src/ReportOutputActions.cs"}){
                string source=File.ReadAllText(Path.Combine(project,relative));
                Check(!source.Contains("MessageBox.Show(FindForm(), ex.Message")&&!source.Contains("return Fail(ex.Message, true)")&&!source.Contains("message.Text = ex.Message"),"reviewed modal boundary does not route raw exception text: "+relative);
            }
            Console.WriteLine("CUSTOMER_MODAL_TEXT_PASS="+checks+";ProductEntry=false;Algorithms=false;Worker=false;WindowsShown=false;Office=false;SDK=false;Hardware=false;LearningStoreRead=false");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
