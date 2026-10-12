using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.Controls;
using HVX.ProjectCables;

internal static class CalibrationCustomerFeedbackSmoke
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const string Canary=@"OFFLINE-ONLY D:\private\capture.cal PICO-SERIAL internal task/port1/line2";
    static int checks;
    static void Check(bool ok,string detail){checks++;if(!ok)throw new Exception(detail);}
    static object Call(object owner,string name,params object[] values)
    {return owner.GetType().GetMethods(Flags).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(owner,values);}
    static Control Input(Control owner,string name){return owner.Controls.Find(name,true).Single();}
    static void CheckCustomerText(string text,string detail)
    {Check(!String.IsNullOrWhiteSpace(text)&&!text.Contains("OFFLINE")&&!text.Contains("PICO")&&!text.Contains(@"D:\")&&!text.Contains("port1")&&!text.Contains("Exception"),detail);}
    [STAThread] static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            var failure=new IOException(Canary,new InvalidOperationException("OFFLINE inner technical evidence"));
            using(var control=new CalibrationProgramControl()) {
                string shown=null,title=null,diagnostic=null;
                control.CalibrationEntryDiagnosticSink=value=>diagnostic=value;
                control.ShowCalibrationCustomerFailureForTests=(body,heading)=>{shown=body;title=heading;};
                foreach(string[] operation in new[]{new[]{"Joint write","接头未写入","Joint not saved"},new[]{"Joint removal","接头未删除","Joint not deleted"},new[]{"Noise addition","过滤未添加","Filter not added"},new[]{"Noise removal","过滤未删除","Filter not deleted"}}) {
                    Call(control,"ShowCalibrationCustomerFailure",null,operation[0],failure,operation[1],operation[2]);
                    CheckCustomerText(shown,"ordinary operation failure has no internal details");
                    Check(title==operation[1],"failed operation remains distinguished");
                    Check(diagnostic.Contains(Canary)&&diagnostic.Contains("OFFLINE inner technical evidence"),"full exception retained in existing diagnostic sink");
                }
                control.SetLanguageCode("en");Call(control,"ShowCalibrationCustomerFailure",null,"Joint write",failure,"接头未写入","Joint not saved");
                Check(shown.StartsWith("Operation not completed.")&&title=="Joint not saved","customer text follows selected language");
                control.SetLanguageCode("zh-TW");Call(control,"ShowCalibrationCustomerFailure",null,"Joint write",failure,"接头未写入","Joint not saved");
                Check(shown.Contains("請核對")&&!shown.Contains("请"),"traditional customer feedback uses established conversion");
                control.SetLanguageCode("zh-CN");control.SimulationMode=true;control.ProjectPath=root;
                string quality=(string)control.GetType().GetProperty("CalibrationQualitySaveCustomerCaption",Flags).GetValue(control,null);
                CheckCustomerText(quality,"quality confirmation contains no internal computation or archive details");
                Check(quality.Contains("可能不可靠")&&quality.Contains("保存"),"quality confirmation preserves data risk and explicit save choice");
                control.SetLanguageCode("en");quality=(string)control.GetType().GetProperty("CalibrationQualitySaveCustomerCaption",Flags).GetValue(control,null);
                Check(quality.Contains("unreliable")&&quality.Contains("Save anyway?"),"English quality confirmation retains risk and save decision");
                control.SetLanguageCode("zh-CN");
                Check(!control.LoadCalibrationFile(),"missing simulation archive remains a failed load");
                CheckCustomerText(control.LastCalibrationError,"failed archive read does not expose the path");
                Check(!control.SelectedCalibrationSaved,"failed load never fabricates saved data");
            }
            using(var dialog=new CableJointDialog(CableJointStore.CreatePreview("A",100,"XLPE"),25,"A",0,null)) {
                Exception reported=null;dialog.DiagnosticError=diagnosticError=>reported=diagnosticError;
                string body=(string)Call(dialog,"CustomerFailure","Synthetic validation",failure);
                CheckCustomerText(body,"shared editor strips nested raw exception details");
                Check(Object.ReferenceEquals(reported,failure)&&dialog.LastDiagnosticError.Contains(Canary),"shared editor retains full callback and bounded last diagnostic");
                Call(dialog,"CustomerFailure","Oversize synthetic evidence",new IOException(new string('x',32768)));
                Check(dialog.LastDiagnosticError.Length==16384,"fallback diagnostic has a fixed bound");
                var committed=new CableJointCommittedException(new CableJointCommitResult{Warning=Canary,BackupPath=@"D:\private\backup"});
                body=(string)Call(dialog,"CustomerFailure","Saved with warning",committed);
                CheckCustomerText(body,"post-commit warning does not show backup path");
                Check(body.Contains("已保存")&&body.Contains("勿重复提交")&&!body.Contains("操作未完成"),"known committed edit is not falsely reported unsaved");
                var position=(NumericUpDown)Input(dialog,"JointPosition");position.Value=25m;
                string error;Check(dialog.TryPrepare(out error),"valid joint plan prepared before injected submit failure");
                dialog.ConfigureCommit((plan,op)=>{throw failure;});Call(dialog,"SubmitPrepared");
                CheckCustomerText(Input(dialog,"JointValidation").Text,"sync submit feedback remains short");
                Check(dialog.Plan==null&&position.Value==25m&&dialog.DialogResult!=DialogResult.OK,"failed submit retains input and never confirms success");
                Check(dialog.LastDiagnosticError.Contains(Canary),"sync submit detailed rejection retained");
                Check(dialog.TryPrepare(out error),"explicit retry requires preparation again");
                dialog.ConfigureCommitAsync((plan,op,cancel)=>Task.FromException(failure));Call(dialog,"SubmitPrepared");
                CheckCustomerText(Input(dialog,"JointValidation").Text,"async submit feedback remains short");
                Check(!dialog.IsCommitPending&&dialog.Plan==null&&position.Value==25m,"async failure releases UI busy state but not data changes");
                dialog.ConfigurePositionResolver(phases=>{throw new InvalidDataException(Canary);});
                Check(!Input(dialog,"ConfirmJoint").Enabled,"invalid coordinate still blocks submit");
                CheckCustomerText(Input(dialog,"JointValidation").Text,"coordinate failure strips internals");
                string catalog=Path.Combine(root,"invalid-material-catalog.ini");File.WriteAllText(catalog,"[Materials]\r\nOFFLINE invalid");
                byte[] before=File.ReadAllBytes(catalog);dialog.ConfigureMaterialCatalog(catalog);
                CheckCustomerText(Input(dialog,"JointValidation").Text,"material read failure does not show internal path");
                Check(!dialog.TrySaveMaterial("x",170,false,out error),"invalid catalog save still fails");CheckCustomerText(error,"material save error is customer-safe");
                Check(!dialog.TryDeleteMaterial("x",out error),"invalid catalog removal still fails");CheckCustomerText(error,"material removal error is customer-safe");
                Check(before.SequenceEqual(File.ReadAllBytes(catalog)),"invalid synthetic catalog remains unchanged");
            }
            Console.WriteLine("CALIBRATION_CUSTOMER_FEEDBACK_PASS="+checks+"; SDK_CALLS=0; PRODUCT_STARTED=0; USER_PROJECT_WRITES=0");return 0;
        } catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
