using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;

// Only managed UI/state + synthetic files/delegates; no SDK, worker or product Main.
internal static class PressureManualOverRangeSmoke
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type type,settingsType,captureType;static string qa,choice="Ignore";static Action duringChoice;static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static string Xml(object value){using(var s=new StringWriter(CultureInfo.InvariantCulture)){new XmlSerializer(value.GetType()).Serialize(s,value);return s.ToString();}}
    static string Hash(string p){using(var s=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
    static void Pump(){Application.DoEvents();Thread.Sleep(1);}
    static T Wait<T>(Task<T> task){var time=Stopwatch.StartNew();while(!task.IsCompleted&&time.ElapsedMilliseconds<12000)Pump();Check(task.IsCompleted,"bounded managed completion");return task.GetAwaiter().GetResult();}
    public static object Choose(bool canAdjust){if(duringChoice!=null)duringChoice();return Enum.Parse(module.GetType("HVX.Controls.PressureManualRangeChoice"),choice);}
    static object Settings(){var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART");Field(s,"PicoSerial","OFFLINE-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"RangeIndex",5);Field(s,"PdCalibrationPcPerV",1000d);return s;}
    static string Capture(string project,string xml,bool overflow)
    {
        object s;using(var r=new StringReader(xml))s=new XmlSerializer(settingsType).Deserialize(r);
        var c=Activator.CreateInstance(captureType);Field(c,"Settings",s);var adc=new short[(int)Get(s,"RequestedPicoCount")];adc[0]=(short)(overflow?32767:0);adc[100]=(short)(overflow?32767:1000);
        Field(c,"PdAdc",adc);Field(c,"VoltageRaw",Enumerable.Range(0,(int)Get(s,"ExpectedArtCount")).Select(i=>.08*Math.Cos(2*Math.PI*i/5)).ToArray());
        Field(c,"PicoRateHz",Field(s,"RequestedPicoRateHz"));Field(c,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[(int)Field(s,"RangeIndex")]);
        Field(c,"HardwareBits",module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration").GetMethod("HardwareBits",F).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
        Field(c,"Overflow",overflow);Field(c,"CapturedUtc",DateTime.UtcNow);Field(c,"Runtime","SYNTHETIC OFFLINE ONLY");Field(c,"ArtSerial",Field(s,"ArtSerial"));Field(c,"PicoSerial",Field(s,"PicoSerial"));
        string dir=Path.Combine(project,"Acquisition","Pressure","r"+Guid.NewGuid().ToString("N"));Call(c,"Save",dir);return dir;
    }
    static string Review(string dir,string xml,double kv)
    {
        object s;using(var r=new StringReader(xml))s=new XmlSerializer(settingsType).Deserialize(r);
        var c=captureType.GetMethod("Load",F).Invoke(null,new object[]{dir});
        var evaluate=module.GetType("Xiongxin.ZeroBench.LocationRangeAdvisor").GetMethods(F).Single(m=>m.Name=="Evaluate"&&m.GetParameters().Length==9);
        object q=evaluate.Invoke(null,new[]{Field(c,"PdAdc"),(object)10,10000,Field(c,"MaximumAdc"),Field(c,"PicoFullScaleV"),Field(s,"RangeIndex"),Field(s,"CalibrationPulsePc"),Field(s,"PdCalibrationPcPerV"),Field(c,"Overflow")});
        Field(q,"WindowValid",true);Field(q,"DriverOverflowUncertain",Field(c,"Overflow"));Field(q,"RecommendationUncertain",Field(c,"Overflow"));Field(q,"Change",false);Field(q,"RecommendedIndex",Field(s,"RangeIndex"));
        var review=Activator.CreateInstance(module.GetType("HVX.Controls.PressureRangeReviewSnapshot"));Field(review,"Schema","HVX.Pressure.RangeReview/V1.0.30.9");
        Field(review,"Quality",q);Field(review,"CurrentRangeIndex",Field(s,"RangeIndex"));Field(review,"NextRangeIndex",Field(s,"RangeIndex"));Field(review,"CanApply",false);return Xml(review);
    }
    sealed class Fixture:IDisposable
    {
        internal Control C;internal Form Form;internal string Project;internal System.Windows.Forms.Timer Pulse;internal int Saves,Decisions,Analyses;
        internal Fixture(string name,bool autoSave=true,int range=5,bool overflow=true)
        {
            Project=Path.Combine(qa,name);Directory.CreateDirectory(Project);File.WriteAllText(Path.Combine(Project,"ITEM.in"),"负载类型 = \"单芯电缆\"",new System.Text.UTF8Encoding(true));
            Form=new Form{ClientSize=new Size(1280,780),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-30000,-30000)};
            C=(Control)Activator.CreateInstance(type);C.Dock=DockStyle.Fill;Form.Controls.Add(C);Set(C,"ProjectPath",Project);Set(C,"MaximumPeakVoltageKv",20d);Set(C,"FrequencyReminderEnabled",false);
            Call(C,"SetPressurePlan",new[]{1d},new[]{3},new[]{1d});Call(C,"SetPressureProgramMode","PLAN");Call(C,"SetAdvancedMode",true);Call(C,"SetAutomaticRange",false);
            Call(C,"SetDischargeCalibrationWithEvidence",10000d,"OFFLINE.cal",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(new byte[32]));Call(C,"SetSelectedRange","PICO:"+range);
            Set(C,"AutomaticPostProcessEnabled",autoSave);Set(C,"BackgroundPressureAnalysisEnabled",true);
            Call(C,"ConfigurePressureHardwareBackend",Xml(Settings()),new Func<string,double,CancellationToken,Task<string>>((xml,kv,ct)=>Task.FromResult(Capture(Project,xml,overflow))));
            Call(C,"ConfigurePressureRangeReview",new Func<string,string,double,string>(Review));
            var hook=type.GetField("ManualPressureRangeChoiceForTests",F);var p=Expression.Parameter(typeof(bool));hook.SetValue(C,Expression.Lambda(hook.FieldType,
                Expression.Convert(Expression.Call(typeof(PressureManualOverRangeSmoke).GetMethod("Choose"),p),hook.FieldType.GetGenericArguments()[1]),p).Compile());
            type.GetEvent("SaveRequested").AddEventHandler(C,(EventHandler)((s,e)=>Saves++));type.GetEvent("AnalysisRequested").AddEventHandler(C,(EventHandler)((s,e)=>Analyses++));
            type.GetEvent("OverRangeDecisionRequested").AddEventHandler(C,(EventHandler)((s,e)=>Decisions++));
            Form.Show();Pump();Call(C,"ReportP04Status",true,true,DateTime.UtcNow);Pulse=new System.Windows.Forms.Timer{Interval=20};Pulse.Tick+=(s,e)=>Call(C,"ReportP04Status",true,true,DateTime.UtcNow);Pulse.Start();
        }
        internal void Start(bool overrange=true){Call(C,"PressureActionClicked");Check((bool)Get(C,"ActionPending"),"explicit manual request published");
            Check((bool)Call(C,"TryConsumePressureCaptureIntent",Get(C,"PressureRequestSequence"),Get(C,"RequestedTargetPeakVoltageKv")),"exact explicit intent consumed");
            Check(Wait((Task<bool>)Call(C,"ExecuteZeroVoltageHardwareActionAsync",Get(C,"ActionSequence"))),"synthetic owner completes release and capture ACK");
            Check(!(bool)Get(C,"HasActiveHardwareSession")&&!(bool)Get(C,"IsBusy"),"hardware lease ended before range decision");
            Check(overrange?(bool)Get(C,"OverRangeDecisionPending")&&Decisions==1:!(bool)Get(C,"OverRangeDecisionPending")&&Decisions==0,"manual overrange prompts even when automatic saving is off; normal data do not prompt");}
        internal string Decide(string action){choice=action;return Wait((Task<string>)Call(C,"ReviewPressureOverRangeAsync"));}
        public void Dispose(){duringChoice=null;Pulse.Stop();Pulse.Dispose();Form.Dispose();C.Dispose();Pump();}
    }
    static void DecisionTests()
    {
        foreach(bool auto in new[]{true,false})foreach(string action in new[]{"Ignore","Adjust","Save"})using(var f=new Fixture("choice-"+auto+action,auto)){
            f.Start();var c=f.C;long sequence=(long)Get(c,"PressureRequestSequence");int range=(int)Get(c,"SelectedPicoRangeCode"),repeat=(int)Get(c,"CurrentPlanRepetition");
            var result=Field(c,"lastPressureCapture");string before=Xml(Field(result,"Quality")),raw=Path.Combine((string)Field(result,"Directory"),"capture.bin"),hash=Hash(raw);
            string outcome=f.Decide(action);
            Check(outcome=="Manual"+(action=="Save"?"Saved":action=="Ignore"?"Ignored":"Adjust"),"choice has dedicated manual outcome");
            Check((long)Get(c,"PressureRequestSequence")==sequence&&(int)Get(c,"SelectedPicoRangeCode")==range&&!(bool)Call(c,"RequestReviewedRangeRetry"),"none of the three choices changes range or publishes a new HV request");
            Check((int)Get(c,"CurrentPlanRepetition")==repeat&&!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"choice does not count/save/ACK an unfinished risk segment");
            Check(Xml(Field(result,"Quality"))==before&&Hash(raw)==hash,"raw ADC and original assessed quality are immutable");
            if(action=="Save"){
                Check(f.Saves==1&&(bool)Get(c,"CurrentRoundManualOverRangeWarning"),"explicit Save alone requests one identity-bound warning save");
                string loc=Wait((Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d));
                var q=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllLines(loc)[9].Substring("Acquisition context\t".Length));
                var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllLines(Path.ChangeExtension(loc,".dem"))[12].Substring("Acquisition context:\t".Length));
                Check(Object.Equals(q["manualOverRangeSaved"],true)&&Object.Equals(d["manualOverRangeSaved"],true),"both formal LOC and DEM retain the manual risk marker");
                Check((string)q["peakValidity"]=="clipped-or-uncertain"&&(string)q["locationValidity"]=="not-validated"&&(string)q["rawSha256"]==hash,"saved adoption is not a restored physical peak or valid location");
                Check(q.ContainsKey("qualityXml")&&q.ContainsKey("originalQualityXml")&&Hash(raw)==hash,"original raw and both quality records survive formal save");
                Check((bool)Call(c,"CompleteSave",true,"")&&(bool)Get(c,"DataSaved")&&(bool)Get(c,"DeleteEnabled")&&f.Analyses==1,"actual successful save permits analysis and current-round Delete");
                Check(!Wait((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"))&&(long)Get(c,"PressureRequestSequence")==sequence,"warning save cannot automatically continue or adjust");
                Call(c,"ReportManualOverRangeAnalysisComplete",Get(c,"LocationAcquisitionId"));
                Check((bool)Get(c,"DataAnalyzed")&&((string)Get(c,"LastPressureRangeReviewStatus")).Contains("未验证"),"analysis completion keeps warning and unverified status");
                RecoveryTests(c,loc,(string)Get(c,"LocationAcquisitionId"));
            }else{
                Check(f.Saves==0&&f.Analyses==0&&!(bool)Get(c,"OverRangeIgnored"),"Ignore/Adjust never save or analyze");
                if(action=="Adjust"){
                    Call(c,"PressureActionClicked");Check((long)Get(c,"PressureRequestSequence")==sequence,"adjust choice requires actual higher range before explicit restart");
                    Call(c,"SetSelectedRange","PICO:6");if((bool)Get(c,"ActionPending"))Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");
                    Call(c,"PressureActionClicked");Check((long)Get(c,"PressureRequestSequence")==sequence+1&&(int)Get(c,"SelectedPicoRangeCode")==6,"actual manual increase plus fresh click starts exactly one new round");
                    Call(c,"CompleteAction",Get(c,"ActionSequence"),false,"offline test stops before capture");
                }else {
                    Call(c,"RequestSaveCurrentRound");Check(f.Saves==1&&(bool)Get(c,"CurrentRoundManualOverRangeWarning"),"later explicit Save adopts current risk segment without a second pressure request");
                }
            }
        }
        foreach(string stop in new[]{"stop","low","fault","release"})using(var f=new Fixture("priority-"+stop)){
            f.Start();var c=f.C;long sequence=(long)Get(c,"PressureRequestSequence");
            duringChoice=()=>{if(stop=="stop")Call(c,"InterruptPressureContinuation");if(stop=="low"){f.Pulse.Stop();Call(c,"ReportP04Status",true,false,DateTime.UtcNow);}if(stop=="fault")Call(c,"SetPressureState","FAULT",Double.NaN);if(stop=="release")Field(c,"zeroHardwareRunning",true);};
            Check(f.Decide("Save")=="Stale"&&f.Saves==0&&!(bool)Get(c,"CurrentRoundManualOverRangeWarning"),stop+" supersedes a pending manual save decision");
            Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Call(c,"RequestReviewedRangeRetry"),stop+" cannot start new pressure");
            if(stop=="release")Field(c,"zeroHardwareRunning",false);
        }
        using(var f=new Fixture("maximum-manual",true,10)){f.Start();Check(f.Decide("Save")=="ManualSaved"&&f.Saves==1,"maximum manual range still offers explicit warning save, never automatic acceptance");}
        foreach(bool autoSave in new[]{true,false})using(var f=new Fixture("normal-save-"+autoSave,autoSave,5,false)){
            f.Start(false);var c=f.C;long sequence=(long)Get(c,"PressureRequestSequence");
            Check(f.Saves==(autoSave?1:0),"normal segment auto-save preference controls save event");
            if(!autoSave){Call(c,"RequestSaveCurrentRound");Check(f.Saves==1&&(bool)Get(c,"CurrentRoundExplicitSave"),"manual Save adopts current normal segment");}
            string loc=Wait((Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d));
            Check((bool)Call(c,"CompleteSave",true,"")&&(bool)Get(c,"DataSaved")&&(bool)Get(c,"DeleteEnabled")&&(long)Get(c,"PressureRequestSequence")==sequence,"normal Save ACK enables Delete without repressure");
            Check(!(bool)Get(c,"CurrentRoundManualOverRangeWarning"),"normal adoption is not falsely marked overrange");
            Check(!(bool)Call(c,"ReportAcquisitionCompleted",false)&&(bool)Get(c,"DataSaved"),"duplicate acquisition notification cannot erase the successful save");
        }
    }
    static void RangeModeRestartTests()
    {
        foreach(bool automaticPressure in new[]{false,true})using(var f=new Fixture("mode-restart-"+automaticPressure,true,5,true)){
            f.Start();var c=f.C;Check(f.Decide("Adjust")=="ManualAdjust","manual Adjust sets the range restart requirement");
            long sequence=(long)Get(c,"PressureRequestSequence");string id=(string)Get(c,"LocationAcquisitionId");
            var source=Field(c,"lastPressureCapture");string quality=Xml(Field(source,"Quality")),raw=Path.Combine((string)Field(source,"Directory"),"capture.bin"),hash=Hash(raw);
            int range=(int)Get(c,"SelectedPicoRangeCode");
            Call(c,"PressureActionClicked");Check((long)Get(c,"PressureRequestSequence")==sequence,"remaining in manual mode without increasing range still blocks a fresh click");
            Call(c,"SetAutomaticRange",true);
            Check((bool)Call(c,"AllowExplicitManualRangeRestart"),"automatic range does not require an unavailable manual increase");
            Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Get(c,"ActionPending")&&f.Saves==0&&f.Analyses==0,"changing range mode alone cannot request pressure, save or analysis");
            Check((int)Get(c,"SelectedPicoRangeCode")==range&&(string)Get(c,"LocationAcquisitionId")==id&&Xml(Field(source,"Quality"))==quality&&Hash(raw)==hash,
                "mode switch preserves range, capture identity, raw evidence and assessed quality");
            Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed")&&!((string)Get(c,"LastPressureRangeReviewStatus")).Contains("请调高量程"),
                "unsaved invalid data are not adopted and the obsolete manual prompt is removed");
            Call(c,"SetAutomaticRange",false);Call(c,"PressureActionClicked");
            Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Call(c,"AllowExplicitManualRangeRestart"),
                "switching back to manual before a new capture does not bypass its existing increase requirement");
            if(automaticPressure)Call(c,"SetAdvancedMode",false);else Call(c,"SetAutomaticRange",true);
            f.Pulse.Stop();Call(c,"ReportP04Status",false,true,DateTime.UtcNow);Call(c,"PressureActionClicked");
            Check((long)Get(c,"PressureRequestSequence")==sequence&&!(bool)Get(c,"ActionPending"),"automatic mode fresh click still rejects unknown safety input");
            Check((string)Get(c,"LastActionCode")=="PRESSURE_BLOCKED_HV_NOT_READY","real request interlock, not the obsolete manual gate, blocks the unsafe start");
            if(automaticPressure){
                Call(c,"SetPressurePlan",new[]{0d,1d},new[]{1,3},new[]{0d,1d});Call(c,"SetSelectedPlanStep",0);Call(c,"PressureActionClicked");
                Check((long)Get(c,"PressureRequestSequence")==sequence,"automatic zero target does not bypass unknown safety input");
                Call(c,"SetSelectedPlanStep",1);
            }
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);f.Pulse.Start();Call(c,"PressureActionClicked");
            Check((long)Get(c,"PressureRequestSequence")==sequence+1&&(bool)Get(c,"ActionPending"),"fresh explicit automatic-mode click with current permission publishes exactly one new round");
            Check((string)Get(c,"LocationAcquisitionId")!=id&&f.Saves==0&&f.Analyses==0&&Hash(raw)==hash,"new request does not save or overwrite the previous invalid capture");
            Call(c,"CompleteAction",Get(c,"ActionSequence"),false,"offline test ends before capture");
        }
    }
    static void RecoveryTests(Control c,string loc,string id)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(qa,"PressureTrialContract.dll"));var trialType=assembly.GetType("Xiongxin.OWTS.Desktop.PressureAnalysisTrial");
        object trial=Activator.CreateInstance(trialType,F,null,new object[]{Path.GetDirectoryName(loc)},CultureInfo.InvariantCulture);Call(trial,"Commit",id,loc);
        string dem=Path.ChangeExtension(loc,".dem"),originalHash=Hash(loc),demHash=Hash(dem);string untouched=Path.Combine(Path.GetDirectoryName(loc),"ALL_5nC.cal");File.WriteAllText(untouched,"synthetic calibration sentinel");
        bool rejected=false;try{Call(trial,"RecoverCommittedRound",id,new Func<bool>(()=>false));}catch(TargetInvocationException){rejected=true;}
        Check(rejected&&File.Exists(loc)&&File.Exists(dem),"stale delete cannot move current records");
        File.AppendAllText(dem,"tamper");rejected=false;try{Call(trial,"RecoverCommittedRound",id,new Func<bool>(()=>true));}catch(TargetInvocationException){rejected=true;}
        Check(rejected&&File.Exists(loc)&&File.Exists(dem),"changed companion cannot delete the untouched current record");
        using(var stream=new FileStream(dem,FileMode.Open,FileAccess.Write,FileShare.None))stream.SetLength(stream.Length-6);
        Check(Hash(dem)==demHash,"synthetic tamper restored for independent remaining tests");
        int calls=0;rejected=false;try{Call(trial,"RecoverCommittedRound",id,new Func<bool>(()=>++calls<4));}catch(TargetInvocationException){rejected=true;}
        Check(rejected&&Hash(loc)==originalHash&&Hash(dem)==demHash&&(bool)Call(trial,"HasCommitted",id),"invalidation during pair movement rolls back exact original bytes and trial ownership");
        string old=Path.Combine(Path.GetDirectoryName(loc),"A_2kV_old.loc");File.WriteAllText(old,"synthetic historical sentinel");
        string recovery=(string)Call(trial,"RecoverCommittedRound",id,new Func<bool>(()=>true));
        Check(!File.Exists(loc)&&!File.Exists(dem)&&File.Exists(Path.Combine(recovery,"recovery.json")),"exact current pair moved to recoverable directory");
        Check(Hash(Path.Combine(recovery,Path.GetFileName(loc)))==originalHash&&Hash(Path.Combine(recovery,Path.GetFileName(dem)))==demHash,"recovered records preserve original bytes");
        Check(File.ReadAllText(untouched)=="synthetic calibration sentinel"&&File.ReadAllText(old)=="synthetic historical sentinel", "CAL and historical rounds untouched");
        Check(!(bool)Call(trial,"HasCommitted",id)&&((string[])Call(trial,"Snapshot")).Length==0,"deleted acquisition removed only from this trial snapshot");
        Check((bool)Call(c,"TryRequestDelete"),"saved idle current round offers Delete action");
        Check((bool)Call(c,"CompleteCurrentRoundDeleted",id),"current deletion callback binds exact acquisition and mailbox");Call(c,"CompleteAction",Get(c,"ActionSequence"),true,"");
        Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DeleteEnabled"),"after deletion button state returns to unsaved current segment");
    }
    [STAThread]static int Main(string[] args)
    {
        try{qa=Path.GetFullPath(args[0]);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(qa,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(qa,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));type=module.GetType("HVX.Controls.PressureTestControl");settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings");captureType=module.GetType("Xiongxin.ZeroBench.BenchCapture");
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);DecisionTests();RangeModeRestartTests();
            Console.WriteLine("PRESSURE_MANUAL_OVERRANGE_PASS="+checks+"; HardwareAccessed=false; SDKCalled=false; WorkersStarted=0; ProductStarted=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
