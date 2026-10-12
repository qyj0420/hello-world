using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Latest operator contract supersedes only the primary-button live numeric
// assertions in PressureActionFeedbackSmoke. Preserve that historical test;
// actual feedback, fill, result and source-epoch assertions remain required.
// Synthetic UI state only: no RequestPressure, capture delegate, SDK or product Main.
internal static class PressurePresetVoltageDisplaySmoke
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static Type type;
    static void Check(bool ok,string why){++checks;if(!ok)throw new InvalidOperationException(why);}
    static object Get(object value,string name){return value.GetType().GetProperty(name,All).GetValue(value,null);}
    static void Set(object value,string name,object content){value.GetType().GetProperty(name,All).SetValue(value,content,null);}
    static object Field(object value,string name){return value.GetType().GetField(name,All).GetValue(value);}
    static void Field(object value,string name,object content){value.GetType().GetField(name,All).SetValue(value,content);}
    static object Call(object value,string name,params object[] arguments)
    {return value.GetType().GetMethods(All).Single(m=>m.Name==name&&m.GetParameters().Length==arguments.Length).Invoke(value,arguments);}
    static void State(Control page,string state,string post,double actual)
    {Field(page,"postProcessStateCode",post);Call(page,"SetPressureState",state,actual);Application.DoEvents();}
    static void Caption(Control page,Button button,double preset,string title,string why)
    {
        bool failed=(string)Field(page,"pressureStateCode")=="FAULT"||
            ((string)Field(page,"postProcessStateCode")).EndsWith("FAILED",StringComparison.Ordinal)||
            (string)Field(page,"postProcessStateCode")=="ACQUISITION_INVALID"||
            (string)Field(page,"postProcessStateCode")=="OVERRANGE_REJECTED";
        if((bool)Get(page,"CanStopCurrentPressure"))title=(bool)Field(page,"operatorPressureStopRequested")?"正在停止 · 等待回执":
            (failed?title:(string)Call(page,"PressureWorkerDisplayTitle"))+" · 点击停止";
        else if((bool)Get(page,"CanFinishCurrentRound"))title=failed?
            title+((bool)Get(page,"FinishCurrentRoundPending")?" · 随后停止":" · 停止续轮"):
            (string)Field(page,"postProcessStateCode")=="ANALYSIS_REQUESTED"?
                (string)Call(page,"PressureAnalysisProgressTitle")+((bool)Get(page,"FinishCurrentRoundPending")?" · 随后停止":" · 可停止续轮"):
                (bool)Get(page,"FinishCurrentRoundPending")?"本轮结束后停止":"完成本轮后停止";
        string[] lines=button.Text.Replace("\r",String.Empty).Split('\n');
        Check(lines.Length==2&&lines[0]==title,why+": only the compact pressure title is shown");
        Check(new[]{"开始加压","加压完成","正在停止","已经停止","本轮未完成","保存失败","分析失败","正在保存","等待保存","正在分析","完成本轮后停止","本轮结束后停止","正在准备","正在升压","正在采集","准备采集","正在加压"}.Any(t=>lines[0].StartsWith(t,StringComparison.Ordinal)),why+": modes never overwrite a stop/finish boolean action");
        Check(!new[]{"连续加压","重复加压","单次加压"}.Any(t=>lines[0].Contains(t)),why+": mode labels live in the corner badge, never the boolean text");
        string second=preset.ToString("0.0",CultureInfo.InvariantCulture)+" kV";
        if(title!="已经停止"&&(int)Field(page,"pressureRangeRetries")>0&&(long)Field(page,"pressureRangeRetryRequest")== (long)Field(page,"pressureRequestSequence"))
            second+="  ·  重采 "+Field(page,"pressureRangeRetries");
        else if(title!="已经停止"&&(string)Get(page,"PressureProgramModeCode")=="PLAN"&&!(bool)Field(page,"advancedMode"))
            second+="  ·  "+Get(page,"CurrentPlanRepetition")+"/"+Get(page,"CurrentPlanRepetitionTotal")+" 次";
        Check(lines[1]==second,why+": selected preset and actual plan count, not measured/requested voltage numbers");
        Check(!button.Text.Contains("反馈")&&!button.Text.Contains("收尾"),why+": detailed telemetry remains outside compact primary caption");
        Check(button.AccessibleDescription.Contains("预设峰值电压"),why+": accessibility labels voltage as preset, not measured");
    }
    static void Case(bool plan)
    {
        using(var form=new Form{ClientSize=new Size(1366,768),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
        using(var page=(Control)Activator.CreateInstance(type))
        {
            Set(page,"EmbeddedMode",true);Set(page,"SimulationMode",true);Set(page,"LanguageIndex",0);
            Set(page,"FrequencyReminderEnabled",false);Set(page,"MaximumPeakVoltageKv",50d);
            page.Dock=DockStyle.Fill;form.Controls.Add(page);form.Show();Application.DoEvents();
            if(plan)
            {
                Set(page,"PlanVoltagesArePeak",false);
                Call(page,"SetPressurePlan",new[]{0d,1d,1.7d},new[]{1,3,2},new[]{0d,8.7d,14.8d});
                Call(page,"SetPressureProgramMode","PLAN");Call(page,"SetSelectedPlanStep",1);Call(page,"SetCurrentPlanRepetition",2);
                Call(page,"SetAdvancedMode",false);
            }
            else
            {
                Call(page,"SetPressureProgramMode","CUSTOM");Call(page,"SetAdvancedMode",true);
                Call(page,"SetCustomVoltageMode","PEAK");Call(page,"SetCustomVoltageValue",12.346789d);
            }
            double preset=(double)Get(page,"CalculatedTargetPeakVoltageKv");
            Check(preset>12&&preset<13,"fixture uses an unrounded nonzero selected preset");
            var button=(Button)Field(page,"pressureActionButton");
            var bottom=(Control)Field(page,"taskInformationLabel");
            int pressureEvents=0,saveEvents=0,stopEvents=0;
            type.GetEvent("PressureRequested").AddEventHandler(page,(EventHandler)((s,e)=>++pressureEvents));
            type.GetEvent("SaveRequested").AddEventHandler(page,(EventHandler)((s,e)=>++saveEvents));
            type.GetEvent("FinishCurrentRoundRequested").AddEventHandler(page,(EventHandler)((s,e)=>++stopEvents));
            long sequence=100;
            Field(page,"pressureRequestSequence",sequence);Field(page,"requestedTargetPeakVoltageKv",preset);
            var voltage=new[]{0d,1d,-1d};var discharge=new[]{3d,2d,1d};
            Field(page,"lastVoltageKv",voltage);Field(page,"lastDischargePc",discharge);
            string mode="正在加压";
            State(page,"WAITING","READY",Double.NaN);Caption(page,button,preset,"开始加压","idle "+plan);
            Check(Get(button,"PresentationMode").ToString()==(plan?"Sequence":"Single"),"mode uses its independent corner presentation");
            Check((string)Get(button,"ModeDescription")== (plan?"连续加压":"单次加压"),"corner mode description is localized independently");
            foreach(string state in new[]{"RUNNING","RAMPING","HOLDING"})
            {
                State(page,state,"WAITING_FOR_ACQUISITION",Double.NaN);
                Call(page,"ReportPressureVoltageProgress",sequence,4d,"DIV actual fixture");Application.DoEvents();
                Caption(page,button,preset,mode,state+" "+plan);
                Check((double)Get(page,"CurrentPressureFeedbackKv")==4d,"current actual voltage remains available in "+state);
                Check(Math.Abs((double)Get(button,"MeasurementProgress")-4d/preset)<1e-12,"fill still follows actual feedback / immutable requested target in "+state);
                Check(Double.IsNaN((double)Get(page,"ActualPeakVoltageKv")),"live feedback does not create a final measured peak in "+state);
                Call(page,"ReportPressureVoltageProgress",sequence-1,9d,"old source");
                Check((double)Get(page,"CurrentPressureFeedbackKv")==4d,"old request sequence cannot overwrite current actual feedback");
                Call(page,"ReportPressureWorkerStage",sequence,"STEP_READ_RUNNING");
                Call(page,"ReportPressureWorkerStage",sequence-1,"STEP_HV_UP_RUNNING");
                Check((string)Field(page,"pressureWorkerDisplayStage")=="STEP_READ_RUNNING","old worker stage cannot overwrite current source");
                Caption(page,button,preset,mode,"worker display stage "+state);
            }
            Call(page,"ReportPressureVoltageProgress",sequence,Double.NaN,"invalid actual fixture");
            Check(Double.IsNaN((double)Get(page,"CurrentPressureFeedbackKv"))&&Double.IsNaN((double)Get(button,"MeasurementProgress")),
                "invalid actual feedback is not converted to preset feedback or fictitious fill");
            Caption(page,button,preset,mode,"unknown actual feedback");
            Field(page,"pressureRangeRetries",1);Field(page,"pressureRangeRetryRequest",sequence);
            Call(page,"UpdatePressureUi");Caption(page,button,preset,"正在加压","range retry distinct from accepted plan repetition");
            Check(Get(button,"PresentationMode").ToString()=="RangeRetry"&&(string)Get(button,"ModeDescription")=="重复加压","range retry has its own corner badge and accessible description");
            Field(page,"pressureRangeRetries",0);Call(page,"UpdatePressureUi");
            Call(page,"ReportPressureVoltageProgress",sequence,preset*1.1,"actual above target");
            Check((double)Get(button,"MeasurementProgress")==.95,"measured ramp at/above target does not fabricate full completion");
            double result=preset*.82;
            foreach(string post in new[]{"SAVE_REQUESTED","AWAITING_MANUAL_SAVE","ANALYSIS_REQUESTED"})
            {
                State(page,"COMPLETE",post,result);
                string pending=post=="SAVE_REQUESTED"?"正在保存":post=="AWAITING_MANUAL_SAVE"?"等待保存":(string)Call(page,"PressureAnalysisProgressTitle");
                Caption(page,button,preset,pending,post);
                Check((double)Get(button,"MeasurementProgress")<1&&!(bool)Get(button,"Success"),"pending save/analysis does not fabricate a successful full bar");
                double actual=(double)Get(page,"ActualPeakVoltageKv"),feedback=(double)Get(page,"CurrentPressureFeedbackKv");
                Call(page,"ReportPressureVoltageProgress",sequence,1d,"late completed feedback");
                Check((double)Get(page,"ActualPeakVoltageKv")==actual&&(double)Get(page,"CurrentPressureFeedbackKv")==feedback,
                    "late completed callback changes neither captured result nor accepted feedback");
            }
            State(page,"COMPLETE","COMPLETE",result);Caption(page,button,preset,"加压完成","completed persistence");
            Check((double)Get(button,"MeasurementProgress")==1&&(double)Get(page,"ActualPeakVoltageKv")==result,
                "actual completion fills the bar without replacing measured result by preset");
            Set(page,"BackgroundPressureAnalysisEnabled",true);
            Call(page,"ReportBackgroundPressureAnalysis","offline-acquisition","A 12.3 kV #2",1,7,2,String.Empty);
            Check(bottom.Text.Contains("A 12.3 kV #2")&&bottom.Text.Contains("1/7"),"background group/progress remain in bottom status");
            Caption(page,button,preset,"加压完成","background analysis status");
            Field(page,"lastPostProcessError","OFFLINE_FAILURE_DETAIL");State(page,"FAULT","ACQUISITION_FAILED",result);
            Caption(page,button,preset,"本轮未完成","fault");
            Check(bottom.Text.Contains("OFFLINE_FAILURE_DETAIL")&&(bool)Get(button,"Danger")&&(double)Get(button,"MeasurementProgress")<1,
                "fault remains truthful in status/danger/non-complete fill despite compact primary caption");
            Check((double)Get(page,"ActualPeakVoltageKv")==result,"fault presentation preserves measured result");
            Field(page,"lastPostProcessError",String.Empty);++sequence;
            Field(page,"pressureRequestSequence",sequence);State(page,"RUNNING","WAITING_FOR_ACQUISITION",Double.NaN);
            Check(Double.IsNaN((double)Get(page,"CurrentPressureFeedbackKv"))&&Double.IsNaN((double)Get(button,"MeasurementProgress")),
                "new request never uses last round's cached voltage or fill");
            Call(page,"ReportPressureVoltageProgress",sequence-1,8d,"prior round");
            Check(Double.IsNaN((double)Get(page,"CurrentPressureFeedbackKv")),"old round callback cannot populate new round");
            Call(page,"ReportPressureVoltageProgress",sequence,3d,"fresh current fixture");
            using(var cancellation=new CancellationTokenSource())
            {
                Field(page,"zeroHardwareCancellation",cancellation);Field(page,"zeroHardwareRunning",true);
                try
                {
                    Call(page,"ReportPressureWorkerStage",sequence,"STEP_HV_UP_RUNNING");Call(page,"UpdatePressureUi");
                    Check(button.Text.Split('\n')[0].TrimEnd('\r')=="正在升压 · 点击停止","active owner keeps the actual stop action instead of preset mode caption");
                    Caption(page,button,preset,mode,"active owner action priority");
                    State(page,"FAULT","ACQUISITION_INVALID",Double.NaN);
                    Caption(page,button,preset,"本轮未完成","invalid active round preserves failure and stop action");
                    Check(button.Text.StartsWith("本轮未完成 · 点击停止",StringComparison.Ordinal),"boolean stop affordance cannot hide invalid acquisition");
                    State(page,"FAULT","SAVE_FAILED",Double.NaN);
                    Caption(page,button,preset,"保存失败","save failed active owner preserves failure");
                    Check(button.Text.StartsWith("保存失败 · 点击停止",StringComparison.Ordinal),"boolean stop affordance cannot hide save failure");
                    State(page,"RUNNING","WAITING_FOR_ACQUISITION",Double.NaN);
                    Check((bool)Call(page,"RequestCurrentPressureStop")&&cancellation.IsCancellationRequested,"stop only cancels the managed fake owner");
                    Application.DoEvents();Caption(page,button,preset,"正在停止","stop awaiting real acknowledgement");
                    Check((bool)Get(page,"HasActiveHardwareSession")&&(bool)Get(page,"PressureNavigationLocked"),"stop request does not fabricate hardware release");
                    Check(bottom.Text.Contains("正在停止，等待设备回执"),"stop acknowledgement remains in bottom status");
                    Call(page,"ReportPressureVoltageProgress",sequence,1d,"late stopped fixture");
                    Check((double)Get(page,"CurrentPressureFeedbackKv")==3d&&(double)Get(button,"MeasurementProgress")!=1,"stop ignores late numeric callback and never displays full completion");
                }
                finally{Field(page,"zeroHardwareRunning",false);Field(page,"zeroHardwareCancellation",null);}
            }
            State(page,"STOPPED","CANCELED",result);Caption(page,button,preset,"已经停止","stop acknowledgement");
            Check((double)Get(page,"ActualPeakVoltageKv")==result,"stop caption never replaces existing final result");
            Check(Object.ReferenceEquals(Field(page,"lastVoltageKv"),voltage)&&Object.ReferenceEquals(Field(page,"lastDischargePc"),discharge),
                "all caption/status/progress changes preserve original waveform objects");
            Check((double)Get(page,"CalculatedTargetPeakVoltageKv")==preset&&(double)Get(page,"RequestedTargetPeakVoltageKv")==preset,
                "display formatting never rounds or rewrites selected/immutable requested target");
            Check((long)Get(page,"PressureRequestSequence")==sequence&&pressureEvents==0&&saveEvents==0&&stopEvents==1,
                "synthetic display and stop never publish pressure/save commands or another request epoch");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args==null||args.Length<1||!Path.IsPathRooted(args[0]))throw new ArgumentException("Absolute isolated build root required.");
            string root=Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string dependency=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");
                return File.Exists(dependency)?Assembly.LoadFrom(dependency):null;
            };
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            type=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
            Case(false);Case(true);
            Console.WriteLine("PRESSURE_PRESET_VOLTAGE_DISPLAY_PASS="+checks+"; HardwareAccessed=False; SDKCalled=False; ProductEntryExecuted=False; UserProjectsWritten=False");
            Console.WriteLine("Superseded=PressureActionFeedbackSmoke primary-button live numeric/title assertions only; historical test retained unchanged; actual feedback/progress/peak/source safeguards verified.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
