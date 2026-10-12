using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

// Pure presentation helpers and source-bound modal wiring. No form construction,
// storage/profile reads, product entry, algorithm, worker, SDK or devices.
internal static class ProjectBrowserCustomerFeedbackOfflineSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Call(object target,string name,params object[] args)
    {return target.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(target,args);}
    static object Property(object target,string name){return target.GetType().GetProperty(name,F).GetValue(target,null);}
    static void SetProperty(object target,string name,object value){target.GetType().GetProperty(name,F).SetValue(target,value,null);}
    static void Language(object target,int language){target.GetType().GetField("languageIndex",F).SetValue(target,language);}
    static bool Short(string value)
    {return !String.IsNullOrWhiteSpace(value)&&value.Length<250&&!new[]{"System.IO","D:\\", "512 MiB", "Private.Worker", "SourceBlockIndex", "stack trace"}.Any(value.Contains);}
    static int Main(string[] args)
    {
        try {
            string qa=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);
            var creation=Assembly.LoadFrom(Path.Combine(qa,"modules","ProjectCreation","Xiongxin.OWTS.ProjectCreation.dll"));
            var browser=Assembly.LoadFrom(Path.Combine(qa,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll"));
            object project=FormatterServices.GetUninitializedObject(creation.GetType("HVX.Controls.ProjectCreationControl",true));
            object data=FormatterServices.GetUninitializedObject(browser.GetType("HVX.Controls.ProjectDataBrowserControl",true));
            string diagnostic="System.IO.InvalidDataException: SourceBlockIndex; ledger 512 MiB; D:\\internal\\private.loc\r\n at Private.Worker() stack trace";
            var projectLog=new List<string>();var browserLog=new List<string>();
            SetProperty(project,"DiagnosticSink",new Action<string>(projectLog.Add));SetProperty(data,"DiagnosticSink",new Action<string>(browserLog.Add));
            foreach(int language in new[]{0,1,18}) {
                Language(project,language);Language(data,language);
                string text=(string)Call(project,"CustomerProjectMessage",diagnostic,"项目未保存。请检查必填信息及保存位置后重试。","The project was not saved. Check required information and storage access, then retry.");
                Check(Short(text),"project failure is concise without raw exception/path");
                Check((string)Property(project,"LastCustomerDiagnostic")==diagnostic,"project diagnostic retains complete original text");
                Check(projectLog.Last()==diagnostic,"project diagnostic reaches host sink");
                Check(language!=1||text.StartsWith("The project was not saved."),"English failure states failure rather than success");
                Check(Short((string)Call(project,"CustomerJointFailure",diagnostic)),"joint failure is concise and actionable");
                Check((string)Property(project,"LastCustomerDiagnostic")==diagnostic,"joint diagnostic is not lost");
                foreach(bool stopped in new[]{false,true}) {
                    string browserText=(string)Call(data,"ComparisonJointCustomerMessage",diagnostic,stopped);
                    Check(Short(browserText),"browser ordinary result contains no internal diagnostic");
                    Check((string)Property(data,"LastCustomerDiagnostic")==diagnostic,"browser retains complete diagnostic");
                    Check(browserLog.Last()==diagnostic,"browser diagnostic reaches host sink");
                    Check(language!=1||(stopped?browserText.Contains("did not complete"):browserText.Contains("No joint candidates")),"browser distinguishes incomplete from no candidates");
                }
            }
            Call(project,"CompleteOperation","SaveProject",false,diagnostic);
            Check((string)Property(project,"LastError")==diagnostic,"host operation API retains original error");
            Check(!(bool)Property(project,"LastOperationSucceeded"),"short message cannot mark failed save successful");
            Check((string)Property(project,"LastOperation")=="SaveProject","operation identity retained");
            foreach(object target in new[]{project,data}) {
                SetProperty(target,"DiagnosticSink",new Action<string>(s=>{throw new IOException("Synthetic logging unavailable");}));
                string value=target==project?(string)Call(target,"CustomerJointFailure",diagnostic):(string)Call(target,"ComparisonJointCustomerMessage",diagnostic,true);
                Check(Short(value),"nonfatal logging failure does not replace user outcome");
                Check((string)Property(target,"LastCustomerDiagnostic")==diagnostic,"last complete diagnostic survives sink failure");
                SetProperty(target,"DiagnosticSink",null);
                value=target==project?(string)Call(target,"CustomerJointFailure",(object)null):(string)Call(target,"ComparisonJointCustomerMessage",null,false);
                Check(Short(value),"null diagnostic still produces actionable message");
                Check((string)Property(target,"LastCustomerDiagnostic")=="","null diagnostic normalizes safely");
            }
            string Source(string relative){return File.ReadAllText(Path.Combine(root,relative));}
            string operations=Source("modules-src/ProjectCreation/src/ProjectCreationOperations.cs");
            Check(operations.Contains("if (ShowDialogMessages && !String.IsNullOrEmpty(LastProjectSaveWarning))"),"normal successful save has status only; warning remains modal");
            Check(operations.Contains("CustomerProjectMessage(exception.ToString()")&&operations.Contains("CustomerProjectMessage(cacheError.ToString()"),"load/save/cache exception diagnostics retained");
            Check(!operations.Contains("MessageBox.Show(this, savedPath")&&!operations.Contains("SetStatus(exception.Message"),"save no longer presents path/raw exception");
            Check(operations.Contains("LastProjectSaveWarning =")&&operations.Contains("cacheError.Message"),"partial save diagnostic remains available to host");
            string joints=Source("modules-src/ProjectCreation/src/ProjectCreationJointAppend.cs");
            Check(joints.Contains("LastJointChangeConfirmation = message")&&joints.Contains("ConfirmJointMaterialMerge(message)"),"destructive material merge still requires explicit decision");
            Check(joints.Contains("MessageBoxButtons.OKCancel")&&joints.Contains("Remove the selected interior joint?"),"joint edit/delete user confirmation retains selected-object decision");
            Check(joints.Contains("backup=" )&&joints.Contains("refresh=" )&&joints.Contains("warning="),"committed-change backup/refresh diagnostics retained");
            Check(joints.Contains("reload the project")||joints.Contains("重新加载项目"),"saved-but-refresh-failed outcome advises reload");
            string catalog=Source("modules-src/ProjectCreation/src/ProjectCreationMaterialCatalog.cs");
            Check(catalog.Contains("return MaterialCatalogFailure(notice,exception.Message)"),"catalog presentation separates detailed operation error");
            Check(catalog.Contains("CompleteOperation(\"SaveMaterialCatalog\", false, diagnostic??message)"),"catalog error updates operation exactly once");
            string voltages=Source("modules-src/ProjectCreation/src/ProjectCreationVoltagePlans.cs");
            Check(voltages.Contains("CustomerProjectMessage(error.ToString()"),"voltage-plan exception is diagnosed independently of customer text");
            foreach(string name in new[]{"ProjectCreationOperations.cs","ProjectCreationJointAppend.cs","ProjectCreationUiBehavior.cs","ProjectCreationMaterialCatalog.cs","ProjectCreationVoltagePlans.cs"}) {
                string source=Source("modules-src/ProjectCreation/src/"+name);
                Check(!Regex.IsMatch(source,@"MessageBox\.Show\([^;]*(?:exception|error|ex)\.Message",RegexOptions.Singleline),"reviewed ordinary modal does not concatenate raw exception: "+name);
            }
            string search=Source("modules-src/DataBrowser/src/CalibrationComparisonJoints.cs");
            Check(search.Contains("Text=ComparisonJointCustomerMessage(reason,stopped)"),"ordinary browser modal uses separated presentation helper");
            Check(search.Contains("No joints have been saved."),"browser result explicitly states no joints were written");
            Check(search.Contains("explanation.Text = CandidateText(candidates[list.SelectedIndex])"),"actively requested candidate expert details remain available");
            Check(search.Contains("ValidateComparisonMeanSources(projectPath,comparisonMeanSourceHashes"),"source identity revalidation gate remains");
            Console.WriteLine("PROJECT_BROWSER_CUSTOMER_FEEDBACK_PASS="+checks+";ProductEntry=false;Algorithms=false;Worker=false;ControlsConstructed=false;WindowsShown=false;ProfilesRead=false;SDK=false;Hardware=false;UserProjectWritten=false");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
