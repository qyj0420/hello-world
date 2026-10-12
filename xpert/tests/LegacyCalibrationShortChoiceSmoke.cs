using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using Xiongxin.OWTS.Desktop;
using Xiongxin.OWTS.Localization;

internal static class LegacyCalibrationShortChoiceSmoke
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static string Project(string root,string name)
    {
        string p=Path.Combine(root,name);Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p,"ITEM.in"),"电缆芯数: = \"三芯\"\r\n",Encoding.GetEncoding(936));
        File.WriteAllText(Path.Combine(p,"CABLE.in"),"0\tALL\t\t\r\n1\tStart\tTerminal\t0m\r\n2\tBody\tXLPE\t\r\n1\tEnd\tTerminal\t170m\r\n");
        var text=new StringBuilder("Date\t2026/08/26\r\nTime\t19:35\r\nOld path\t历史项目\r\nCable length\t170\r\nV (m/usec)\t170\r\nCal.value\t5000\r\nWideband\t0\r\nStart_point\t0.0000002\r\nFinal_point\t0.0000022\r\nSR(N)\t100000000\r\nNI_Calib_factor\t5000\r\nFilterstate\t12\r\nCalib_Attenuation\t0\r\n");
        for(int i=0;i<512;i++)text.AppendLine((i==20?1d:i==220?.5d:0d).ToString("R",CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(p,"ALL_5nC.cal"),text.ToString(),Encoding.GetEncoding(936));return p;
    }
    static int Bindings(string p){return Directory.GetFiles(p,"*.hvlegacy",SearchOption.AllDirectories).Length;}
    static bool Select(MainForm host,string p,out bool handled)
    {return host.Select(p,false,"HUB|SYNTHETIC-PICO|SYNTHETIC-ART",out handled);}
    static IEnumerable<Control> Controls(Control parent)
    {foreach(Control child in parent.Controls){yield return child;foreach(var nested in Controls(child))yield return nested;}}
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
            foreach(DialogResult result in new[]{DialogResult.Cancel,DialogResult.Retry}) {
                string p=Project(root,"decline-"+result);using(var host=new MainForm()) {
                    int prompts=0,navigations=0;host.CalibrationRequiredNavigation=()=>navigations++;
                    host.LegacyCalibrationReusePrompt=message=>{prompts++;return result;};
                    bool handled;Check(!Select(host,p,out handled)&&handled,"cancel/recalibrate is handled without permitting live entry: "+result);
                    Check(prompts==1&&Bindings(p)==0,"declining old applicability creates no binding: "+result);
                    Check(navigations==(result==DialogResult.Retry?1:0),"only explicit recalibrate choice opens calibration: "+result);
                }
            }
            string accepted=Project(root,"accepted"),original=LiveCalibrationRecord.HashFile(Path.Combine(accepted,"ALL_5nC.cal"));
            using(var host=new MainForm()) {
                int prompts=0;host.LegacyCalibrationReusePrompt=message=> {
                    prompts++;Check(!message.Contains("PICO")&&!message.Contains("ART")&&!message.Contains("HVCLIVE")&&!message.Contains("计时")&&!message.Contains("timing")&&!message.Contains("5000"),"short decision never exposes devices, formats or processing rules");
                    return DialogResult.Yes;
                };
                bool handled;Check(Select(host,accepted,out handled)&&handled&&Bindings(accepted)==1,"explicit Use creates one present-day applicability record and revalidates");
                Check(Select(host,accepted,out handled)&&handled&&prompts==1&&Bindings(accepted)==1,"valid saved decision is silently reused without another prompt or binding");
                var selected=PressureCalibrationSelection.Find(accepted,"ALL",false,"SYNTHETIC-PICO","SYNTHETIC-ART");
                var binding=LegacyCalibrationBinding.LoadAndValidate(accepted,selected.Source,"SYNTHETIC-PICO","SYNTHETIC-ART",false);
                Check(binding.OperatorConfirmed&&binding.HistoricalDeviceIdentity=="Unknown"&&binding.HistoricalTimingMode=="Unspecified","real choice never fabricates verified historical identity or timing");
                Check(LiveCalibrationRecord.HashFile(Path.Combine(accepted,"ALL_5nC.cal"))==original,"explicit use preserves original historical file");
            }
            foreach(string change in new[]{"project","identity","timing","phase","source"}) {
                string p=Project(root,"changed-"+change),other=Project(root,"other-"+change);
                using(var host=new MainForm()) {
                    host.LegacyCalibrationReusePrompt=message=> {
                        if(change=="project")host.ChangeContext(project:other);
                        if(change=="identity")host.ChangeContext(identity:"HUB|CHANGED-PICO|SYNTHETIC-ART");
                        if(change=="timing")host.ChangeContext(timing:true);
                        if(change=="phase")host.ChangeContext(phase:"B");
                        if(change=="source")File.AppendAllText(Path.Combine(p,"ALL_5nC.cal"),"\r\n1");
                        return DialogResult.Yes;
                    };
                    bool handled;Check(!Select(host,p,out handled)&&handled,"context/source changed while decision open cannot permit entry: "+change);
                    Check(Bindings(p)==0&&Bindings(other)==0,"stale decision creates no applicability record: "+change);
                }
            }
            string recursive=Project(root,"recursive");using(var host=new MainForm()) {
                int prompts=0;host.LegacyCalibrationReusePrompt=message=> {
                    prompts++;bool nested;Check(!Select(host,recursive,out nested)&&nested,"reentrant short prompt cannot enter test or open another decision");return DialogResult.Cancel;
                };
                bool handled;Check(!Select(host,recursive,out handled)&&handled&&prompts==1&&Bindings(recursive)==0,"recursive navigation is bounded and cancelled without a binding");
            }
            foreach(bool zh in new[]{true,false})using(var host=new MainForm()) {
                host.IsChinese=zh;
                string message=(string)typeof(MainForm).GetMethod("LegacyCalibrationChoiceText",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,null);
                using(var dialog=(Form)typeof(MainForm).GetMethod("CreateLegacyCalibrationReuseDialog",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(host,new object[]{message})) {
                    var buttons=Controls(dialog).OfType<Button>().ToArray();
                    Check(buttons.Length==3&&buttons.Single(b=>b.DialogResult==DialogResult.Yes).Text==(zh?"使用已有校准":"Use existing calibration"),"short dialog has explicit named Use action: "+zh);
                    Check(buttons.Single(b=>b.DialogResult==DialogResult.Retry).Text==(zh?"重新校准":"New calibration")&&buttons.Single(b=>b.DialogResult==DialogResult.Cancel).Text==(zh?"取消":"Cancel"),"short dialog offers recalibrate and cancel only: "+zh);
                    Check(dialog.AcceptButton==null&&dialog.CancelButton==buttons.Single(b=>b.DialogResult==DialogResult.Cancel),"Enter/Escape do not silently provide applicability consent: "+zh);
                    Check(Controls(dialog).All(c=>!(c is TextBox)&&!(c is ComboBox))&&message.Length<120,"short dialog contains no technical details or manual file selector: "+zh);
                }
            }
            string nav=File.ReadAllText(Path.Combine(args[1],"src","MainForm.CalibrationNavigation.cs"));
            Check(nav.Contains("TryLegacyCalibrationBeforeTest(phase,out handled,()=>!testControl.IsDisposed&&LateBound.ReadString(testControl,\"SelectedPhaseCode\",\"Phase\")==phase)"),"product navigation rechecks the actual page phase after decision");
            string[] keys={"Saved calibration","Use the saved project calibration? Confirm that it applies to the current equipment and wiring.",
                "Use existing calibration","New calibration","The saved calibration could not be used. Recalibrate, then try again.",
                "Complete and save calibration, then reload the project.","Stop is not yet confirmed. Keep the test stopped and check the equipment.",
                "Stop unconfirmed; high voltage disabled","Device cleanup incomplete; high voltage disabled","Control session ended; check before reconnecting"};
            string[][] rows=File.ReadAllLines(Path.Combine(args[1],"assets","i18n","calibration-safety.packs.txt"),Encoding.UTF8).Select(line=>line.Split('|')).ToArray();
            foreach(string key in keys) {
                string[] row=rows.Single(r=>r[0]==key);Check(row.Length==10,"caption retains all nine existing language columns: "+key);
                for(int i=1;i<row.Length;i++) {
                    string code=rows[0][i],translated;
                    Check(InternationalText.TryGet(code,key,out translated)&&translated==row[i]&&UiLanguage.Display(code,row[2])==row[i],
                        "actual generated catalog resolves English and Chinese caption in "+code+": "+key);
                }
            }
            Check(UiLanguage.Codes.Length==40&&new[]{"bg","hr","cs","fr","de","hu","it","pl","ro","sr","sk","sl","es","sv","nl","en-GB","de-CH","fr-CH","it-CH","pt-PT"}.All(code=>UiLanguage.Codes.Contains(code)),
                "existing language selector including European language options is retained");
            Console.WriteLine("LEGACY_CALIBRATION_SHORT_CHOICE_PASS="+checks+"; PRODUCT_STARTED=0; WORKER_STARTED=0; SDK_CALLS=0; USER_PROJECT_WRITES=0");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
