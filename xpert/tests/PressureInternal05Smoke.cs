using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
class PressureInternal05Smoke
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    [STAThread] static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            var a=Assembly.LoadFrom(Path.Combine(args[0],"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));
            var t=a.GetType("HVX.Controls.PressureTestControl",true);
            using(var c=(Control)Activator.CreateInstance(t)) {
                Action<string,object> set=(n,v)=>t.GetProperty(n).SetValue(c,v,null);
                Func<string,object> get=n=>t.GetProperty(n).GetValue(c,null);
                set("MaximumPeakVoltageKv",30.0);
                t.GetMethod("SetPressureProgramMode").Invoke(c,new object[]{"PLAN"});
                Check((string)get("PressureProgramModeCode")=="CUSTOM","empty plan fallback");
                foreach(string mode in new[]{"PEAK","RMS","U0"}) {
                    t.GetMethod("SetCustomVoltageMode").Invoke(c,new object[]{mode});
                    t.GetMethod("SetCustomVoltageValue").Invoke(c,new object[]{999.0});
                    Check((double)get("CalculatedTargetPeakVoltageKv")<=30.0,"peak ceiling "+mode);
                }
                Check(!(bool)get("VlfPolarityAvailable"),"VLF hidden by default");
                set("VlfPolarityAvailable",true);Check((bool)get("VlfPolarityAvailable"),"VLF capability enabled");
                set("MaximumPeakVoltageKv",0.0);
                t.GetMethod("RequestPressure").Invoke(c,null);
                Check(!(bool)get("IsBusy"),"invalid limit does not begin action");
            }
            using(var c=(Control)Activator.CreateInstance(t)) {
                Func<string,object> get=n=>t.GetProperty(n).GetValue(c,null);
                t.GetMethod("ReportAcquisitionCompleted").Invoke(c,new object[]{true});
                Check((long)get("RepressureRequestSequence")==0,"auto-save never repeats voltage on overflow");
                Check((string)get("PostProcessStateCode")=="OVERRANGE_DECISION_REQUIRED","overflow requires decision immediately");
                t.GetMethod("ResolveOverRangeDecision").Invoke(c,new object[]{false});
                Check((long)get("RepressureRequestSequence")==0,"declining overflow save does not repressure");
                Check((string)get("PostProcessStateCode")=="OVERRANGE_REJECTED","overflow rejected without save");
            }
            using(var c=(Control)Activator.CreateInstance(t)) {
                Func<string,object> get=n=>t.GetProperty(n).GetValue(c,null);
                t.GetMethod("ReportAcquisitionCompleted").Invoke(c,new object[]{false});
                t.GetProperty("AutomaticPostProcessEnabled").SetValue(c,false,null);
                Check((bool)get("AutomaticPostProcessEnabled"),"cannot cancel save acknowledgment by toggling preference");
                t.GetMethod("CompleteSave").Invoke(c,new object[]{true,""});
                t.GetProperty("AutomaticPostProcessEnabled").SetValue(c,false,null);
                Check((bool)get("DataSaved")&&(bool)get("DeleteEnabled"),"saved data survives auto-save off");
                t.GetProperty("AutomaticPostProcessEnabled").SetValue(c,true,null);
                Check((bool)get("DataSaved")&&(bool)get("DeleteEnabled"),"saved data survives auto-save on");
                Check((long)get("AnalysisRequestSequence")==0&&(long)get("RepressureRequestSequence")==0,"save preference does not trigger analysis or HV");
            }
            Console.WriteLine("PRESSURE_INTERNAL05_PASS="+checks+"; HARDWARE_COMMANDS=0");return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
