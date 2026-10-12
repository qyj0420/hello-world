using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Synthetic completed captures only. No worker, native call or product Main.
internal static class PressureOverRangeRetrySmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type type;static string qa,choice;static bool suppress,persistedReminder=true;static int checks,prompts;
    static void Check(bool ok,string m){checks++;if(!ok)throw new Exception(m);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(){Application.DoEvents();Thread.Sleep(2);}
    static string Wait(Task<string> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<6000)Pump();Check(t.IsCompleted,"bounded choice journaling");return t.GetAwaiter().GetResult();}
    public static object Choose(object context){prompts++;var c=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeChoice"),true);Field(c,"Action",Enum.Parse(module.GetType("Xiongxin.ZeroBench.RangeDecisionAction"),choice));Field(c,"Suppress",suppress);return c;}
    static void CompleteFixture(Control c,bool valid,int next)
    {
        Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");Call(c,"SetPressureState","COMPLETE",Get(c,"RequestedTargetPeakVoltageKv"));
        Field(c,"postProcessStateCode","OVERRANGE_DECISION_REQUIRED");Field(c,"hasAcquisitionResult",true);
        string id=(string)Get(c,"LocationAcquisitionId");int current=(int)Get(c,"SelectedPicoRangeCode");
        var quality=Activator.CreateInstance(module.GetType("Xiongxin.ZeroBench.LocationRangeResult"));
        Field(quality,"WindowValid",valid);Field(quality,"Change",current!=next);Field(quality,"RecommendedIndex",next);
        Field(quality,"LocationClippedSamples",2);Field(quality,"LocationQuality","synthetic clipped range");Field(quality,"Reason","synthetic only");
        var review=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeReviewSnapshot"));Field(review,"Schema","HVX.Pressure.RangeReview/V1.0.30.9");
        Field(review,"Quality",quality);Field(review,"CurrentRangeIndex",current);Field(review,"NextRangeIndex",next);Field(review,"CanApply",valid&&current!=next);Field(review,"NextRoundBlocked",!valid);
        var capture=Activator.CreateInstance(module.GetType("HVX.Controls.PressureZeroVoltageResult"),true);
        Field(capture,"Quality",quality);Field(capture,"RangeReview",review);Field(capture,"RawSha256","SYNTHETIC-UNCHANGED");
        string dir=Path.Combine(qa,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);Field(capture,"Directory",dir);
        Field(c,"lastPressureCapture",capture);Field(c,"lastPressureAcquisitionId",id);
    }
    static void Run(string mode)
    {
        using(var form=new Form{Size=new Size(1200,760),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)})
        using(var c=(Control)Activator.CreateInstance(type))using(var pulse=new System.Windows.Forms.Timer{Interval=50}) {
            form.Controls.Add(c);Set(c,"ProjectPath",qa);Set(c,"MaximumPeakVoltageKv",20d);Set(c,"FrequencyReminderEnabled",false);
            Set(c,"AutomaticRangeReminderEnabled",mode=="reloaded"?persistedReminder:mode=="silent-no-intent"?false:true);
            int preferences=0;type.GetEvent("PressurePreferencesChanged").AddEventHandler(c,(EventHandler)((s,e)=>{preferences++;persistedReminder=(bool)Get(c,"AutomaticRangeReminderEnabled");}));
            Call(c,"SetPressurePlan",new[]{1d},new[]{3},new[]{1d});Call(c,"SetPressureProgramMode","PLAN");Call(c,"SetSelectedPlanStep",0);
            int baseRange=mode.StartsWith("maximum",StringComparison.Ordinal)?10:mode=="minimum"?0:5;
            Call(c,"SetDischargeCalibration",mode=="minimum"?10000d:1000d,"fake.cal",DateTimeOffset.UtcNow,"A",false);Call(c,"SetSelectedRange","PICO:"+baseRange);
            Set(c,"ActionHandshakeEnabled",true);form.Show();Pump();Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            pulse.Tick+=(s,e)=>Call(c,"ReportP04Status",true,true,DateTime.UtcNow);pulse.Start();
            Call(c,"PressureActionClicked");long first=(long)Get(c,"PressureRequestSequence");double target=(double)Get(c,"RequestedTargetPeakVoltageKv");
            Check((bool)Call(c,"TryConsumePressureCaptureIntent",first,target),"actual start intent consumed");
            int step=(int)Get(c,"SelectedPlanStepIndex"),repeat=(int)Get(c,"CurrentPlanRepetition");
            CompleteFixture(c,mode!="invalid"&&mode!="invalid-force"&&mode!="maximum-invalid",mode=="same-range"||mode.StartsWith("maximum",StringComparison.Ordinal)||mode=="minimum"?baseRange:6);
            if(mode=="maximum-overflow")Field(Field(Field(c,"lastPressureCapture"),"Quality"),"DriverOverflow",true);
            if(mode.StartsWith("uncertain",StringComparison.Ordinal)) {
                var q=Field(Field(c,"lastPressureCapture"),"Quality");Field(q,"LocationClippedSamples",0);Field(q,"DriverOverflow",true);Field(q,"DriverOverflowUncertain",true);Field(q,"RecommendationUncertain",true);
                if(mode=="uncertain-same") {Field(q,"RecommendedIndex",baseRange);Field(q,"Change",false);var r=Field(Field(c,"lastPressureCapture"),"RangeReview");Field(r,"NextRangeIndex",baseRange);Field(r,"CanApply",false);}
            }
            if(mode=="silent-no-intent")Field(c,"operatorPressureIntentConsumed",false);
            var hook=type.GetField("PressureQualityChoiceForTests",B);var parameter=Expression.Parameter(hook.FieldType.GetGenericArguments()[0]);
            hook.SetValue(c,Expression.Lambda(hook.FieldType,Expression.Convert(Expression.Call(typeof(PressureOverRangeRetrySmoke).GetMethod("Choose"),Expression.Convert(parameter,typeof(object))),hook.FieldType.GetGenericArguments()[1]),parameter).Compile());
            choice=mode=="ignore"||mode=="persist-ignore"?"Ignore":mode=="cancel"||mode=="invalid"||mode=="same-range"||mode.StartsWith("maximum",StringComparison.Ordinal)||mode=="minimum"?"Dismiss":"AdjustRetry";suppress=mode=="silent"||mode=="persist-ignore";prompts=0;
            if(mode=="fault")Call(c,"SetPressureState","FAULT",Double.NaN);
            if(mode=="invalid-force"||mode.StartsWith("uncertain",StringComparison.Ordinal)||mode=="silent-no-intent") {
                string held=Wait((Task<string>)Call(c,"ReviewPressureOverRangeAsync"));
                Check(held=="Stopped"&&prompts==0&&!(bool)Call(c,"RequestReviewedRangeRetry")&&(bool)Get(c,"AutomaticPressureStopped"),"invalid/uncertain/no-start review does not open an Ignore-only modal or create a retry");
                Check((string)Get(c,"PressureStateCode")=="COMPLETE"&&(string)Get(c,"PostProcessStateCode")=="OVERRANGE_REJECTED","decision failure does not rewrite successful capture as hardware fault");pulse.Stop();return;
            }
            string result=Wait((Task<string>)Call(c,"ReviewPressureOverRangeAsync"));
            Check(!(bool)Get(c,"HasActiveHardwareSession")&&(long)Get(c,"PressureRequestSequence")==first,"quality dialog itself never starts hardware");
            Check((string)Field(Field(c,"lastPressureCapture"),"RawSha256")=="SYNTHETIC-UNCHANGED","source evidence not rewritten");
            if(mode=="fault")Check(result=="Stale"&&!(bool)Call(c,"RequestReviewedRangeRetry")&&(string)Get(c,"PressureStateCode")=="FAULT","quality operation cannot clear or retry a hardware fault");
            else if(mode=="ignore"||mode=="persist-ignore") {
                Check(result=="Ignored"&&(string)Get(c,"PostProcessStateCode")=="SAVE_REQUESTED"&&(bool)Get(c,"OverRangeIgnored"),"ignore keeps quality and follows existing save flow");
                if(mode=="persist-ignore")Check(preferences==1&&!persistedReminder&&!(bool)Get(c,"AutomaticRangeReminderEnabled")&&!(bool)Call(c,"RequestReviewedRangeRetry"),"Ignore persists checked suppression without creating retry permission");
            }
            else if(choice=="Dismiss") {Check(result=="Stopped"&&(string)Get(c,"PressureStateCode")=="COMPLETE"&&(bool)Get(c,"AutomaticPressureStopped"),"cancel/invalid is stopped, not acquisition fault");Check(!(bool)Call(c,"RequestReviewedRangeRetry"),"cancel cannot create a retry");}
            else {
                Check(result=="Retry"&&prompts==(mode=="reloaded"?0:1),"reminder restored or persisted suppression honored on a fresh page/request");
                if(mode=="stop")Call(c,"InterruptPressureContinuation");
                if(mode=="low-after-choice"){pulse.Stop();Call(c,"ReportP04Status",true,false,DateTime.UtcNow);}
                bool issued=(bool)Call(c,"RequestReviewedRangeRetry");
                if(mode=="stop"||mode=="low-after-choice")Check(!issued&&(int)Get(c,"SelectedPicoRangeCode")==baseRange,"stop/LOW revokes pending retry before range change");
                else {
                    Check(issued&&(long)Get(c,"PressureRequestSequence")==first+1&&(int)Get(c,"SelectedPicoRangeCode")==6,"reviewed retry issues a fresh single request");
                    Check((int)Get(c,"SelectedPlanStepIndex")==step&&(int)Get(c,"CurrentPlanRepetition")==repeat,"retry never advances plan step/count");
                    Check(!(bool)Call(c,"RequestReviewedRangeRetry"),"retry permit cannot replay");
                    Check((bool)Call(c,"TryConsumePressureCaptureIntent",first+1,target),"explicit range action authorizes exact new request");
                    Check(!(bool)Call(c,"TryConsumePressureCaptureIntent",first+1,target),"new request consumption stays one-use");
                    if(mode=="silent") {
                        CompleteFixture(c,true,7);Check(Wait((Task<string>)Call(c,"ReviewPressureOverRangeAsync"))=="Retry"&&prompts==1,"opt-in same chain avoids repeating ordinary prompt");
                        Check((bool)Call(c,"RequestReviewedRangeRetry"),"bounded automatic same-chain retry issues");
                        Check((bool)Call(c,"TryConsumePressureCaptureIntent",first+2,target),"silent retry inherits exact chain intent, not a fake click");
                        CompleteFixture(c,true,6);choice="Dismiss";Check(Wait((Task<string>)Call(c,"ReviewPressureOverRangeAsync"))=="Stopped"&&prompts==1,"visited range stops non-modally, never loops silently");
                    }
                }
            }
            if(mode.StartsWith("maximum",StringComparison.Ordinal)) {
                Check(prompts==0&&!(bool)Get(c,"OverRangeIgnored")&&!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"20 V maximum has no adjustment UI and no automatic quality acceptance/save");
                var capture=Field(c,"lastPressureCapture");Check((int)Field(Field(capture,"Quality"),"LocationClippedSamples")==2,"maximum original clipped quality is unchanged");
                Check(Directory.GetFiles((string)Field(capture,"Directory"),"pressure-quality-held-*.xml").Length==1,"maximum rejection is internally journaled once");
            }
            Check((int)Get(c,"SelectedPlanStepIndex")==step&&(int)Get(c,"CurrentPlanRepetition")==repeat,"all decisions preserve repetition counter");pulse.Stop();
        }
    }
    [STAThread]static int Main(string[] args){try {
        string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"range-retry");Directory.CreateDirectory(qa);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
        module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl",true);Application.EnableVisualStyles();
        foreach(string mode in new[]{"retry","silent","ignore","persist-ignore","reloaded","cancel","invalid","invalid-force","uncertain-same","uncertain-change","silent-no-intent","same-range","maximum","maximum-invalid","maximum-overflow","minimum","stop","low-after-choice","fault"})Run(mode);
        Console.WriteLine("PRESSURE_OVERRANGE_RETRY_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0; ProductStarted=false");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
