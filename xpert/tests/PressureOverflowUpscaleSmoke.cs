using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

// Actual managed capture consumer / release / ACK / retry path with injected
// synthetic files and callbacks. No worker Main, SDK, discovery or device call.
internal static class PressureOverflowUpscaleSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type control,settings,capture,quality,review;static string qa;static int checks,rounds;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethods(B).Single(m=>m.Name==n&&m.GetParameters().Length==a.Length).Invoke(o,a);}
    static string Xml(object value){using(var text=new StringWriter()){new XmlSerializer(value.GetType()).Serialize(text,value);return text.ToString();}}
    static object ReadXml(Type t,string xml){using(var text=new StringReader(xml))return new XmlSerializer(t).Deserialize(text);}
    static string Hash(string p){using(var sha=SHA256.Create())using(var f=File.OpenRead(p))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","");}
    static void Pump(){Application.DoEvents();Thread.Sleep(1);}
    static T Wait<T>(Task<T> task){var deadline=Stopwatch.StartNew();while(!task.IsCompleted&&deadline.ElapsedMilliseconds<12000)Pump();Check(task.IsCompleted,"bounded managed action and journaling");return task.GetAwaiter().GetResult();}
    public static object RejectPrompt(object context){throw new Exception("eligible overflow or uncertain held evidence must not open a range dialog");}
    static void NoPrompt(Control c)
    {
        var f=control.GetField("PressureQualityChoiceForTests",B);var p=Expression.Parameter(f.FieldType.GetGenericArguments()[0]);
        f.SetValue(c,Expression.Lambda(f.FieldType,Expression.Convert(Expression.Call(typeof(PressureOverflowUpscaleSmoke).GetMethod("RejectPrompt"),Expression.Convert(p,typeof(object))),f.FieldType.GetGenericArguments()[1]),p).Compile());
    }
    static object Settings()
    {
        var s=Activator.CreateInstance(settings);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART-ONLY");Field(s,"PicoSerial","OFFLINE-PICO-ONLY");
        Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"RangeIndex",5);return s;
    }
    static int[] CycleBounds(object c)
    {
        var window=module.GetType("Xiongxin.OWTS.Data.OscillationCycleWindow",true).GetMethods(B)
            .Single(m=>m.Name=="FindUpTo"&&m.GetParameters().Length==7).Invoke(null,new[]{Field(c,"VoltageRaw"),Field(c,"ArtRateHz"),(object)1,
                ((short[])Field(c,"PdAdc")).Length,Field(c,"PicoRateHz"),0d,0d});
        if(!(bool)Get(window,"Success"))throw new Exception("synthetic voltage must supply one true N-cycle range window");
        var crossings=(double[])Field(window,"CrossingSeconds");double rate=(double)Field(c,"PicoRateHz");
        return new[]{(int)Math.Ceiling(crossings[0]*rate),(int)Math.Floor(crossings[crossings.Length-1]*rate)+1};
    }
    static string Capture(string project,string xml,bool driver,bool rawClip,bool severe=false)
    {
        var s=ReadXml(settings,xml);var c=Activator.CreateInstance(capture);Field(c,"Settings",s);
        int n=(int)Get(s,"RequestedPicoCount"),range=(int)Field(s,"RangeIndex");var adc=new short[n];adc[100]=30000;
        var volts=Enumerable.Range(0,(int)Get(s,"ExpectedArtCount")).Select(i=>.08*Math.Cos(2*Math.PI*i/5)).ToArray();
        Field(c,"VoltageRaw",volts);Field(c,"PdAdc",adc);Field(c,"Overflow",driver);Field(c,"PicoRateHz",(double)Field(s,"RequestedPicoRateHz"));
        Field(c,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[range]);
        Field(c,"HardwareBits",module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration").GetMethod("HardwareBits",B).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
        Field(c,"ArtSerial",Field(s,"ArtSerial"));Field(c,"PicoSerial",Field(s,"PicoSerial"));Field(c,"Runtime","OFFLINE SYNTHETIC CALLBACK; NO DEVICE");Field(c,"CapturedUtc",DateTime.UtcNow);
        if(rawClip){var bounds=CycleBounds(c);adc[0]=32767;int center=(bounds[0]+bounds[1])/2;adc[center]=32767;if(severe){adc[center-1]=-32768;adc[center+1]=32767;}}
        string dir=Path.Combine(project,"Acquisition","r"+(++rounds).ToString("000"));Call(c,"Save",dir);return dir;
    }
    static string Review(string dir,string xml,double kv,bool invalid=false,bool forecast=false)
    {
        var s=ReadXml(settings,xml);var native=capture.GetMethod("Load",B).Invoke(null,new object[]{dir});
        var advisor=module.GetType("Xiongxin.ZeroBench.LocationRangeAdvisor");
        var evaluate=advisor.GetMethods(B).Single(m=>m.Name=="Evaluate"&&m.GetParameters().Length==9);
        var bounds=CycleBounds(native);
        var q=evaluate.Invoke(null,new[]{Field(native,"PdAdc"),(object)bounds[0],bounds[1]-bounds[0],Field(native,"MaximumAdc"),Field(native,"PicoFullScaleV"),Field(s,"RangeIndex"),Field(s,"CalibrationPulsePc"),Field(s,"PdCalibrationPcPerV"),Field(native,"Overflow")});
        // Current production retry requires identifiable clipping inside the
        // genuine N-cycle window, not a whole-segment driver flag alone.
        Field(q,"FactoryCycleWindow",true);Field(q,"EvaluationCycles",1);
        Field(q,"WindowValid",!invalid);if(!forecast){Field(q,"DriverOverflowUncertain",true);Field(q,"RecommendationUncertain",true);}
        Field(q,"SwitchingTransientExcluded",false);
        if(!forecast){Field(q,"RecommendedIndex",Field(s,"RangeIndex"));Field(q,"Change",false);}
        var r=Activator.CreateInstance(review);Field(r,"Schema","HVX.Pressure.RangeReview/V1.0.30.9");Field(r,"Quality",q);
        Field(r,"CurrentRangeIndex",Field(s,"RangeIndex"));Field(r,"NextRangeIndex",forecast?Field(q,"RecommendedIndex"):Field(s,"RangeIndex"));Field(r,"CanApply",forecast&&!invalid&&!(bool)Field(q,"RecommendationUncertain"));Field(r,"NextRoundBlocked",false);
        return Xml(r);
    }
    sealed class Fixture:IDisposable
    {
        internal readonly Form Form;internal readonly Control C;internal readonly string Project;internal System.Windows.Forms.Timer Pulse;
        internal bool Driver=true,RawClip=true,InvalidWindow,Severe,Forecast;
        internal Fixture(string name,int range=5,bool manual=false)
        {
            Project=Path.Combine(qa,name);Directory.CreateDirectory(Project);
            Form=new Form{ClientSize=new Size(1250,760),Location=new Point(-28000,-28000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
            C=(Control)Activator.CreateInstance(control);Form.Controls.Add(C);C.Dock=DockStyle.Fill;
            Set(C,"ProjectPath",Project);Set(C,"MaximumPeakVoltageKv",20d);Set(C,"FrequencyReminderEnabled",false);Set(C,"AutomaticRangeReminderEnabled",true);Set(C,"AutomaticPostProcessEnabled",true);
            Call(C,"SetPressurePlan",new[]{1d},new[]{3},new[]{1d});Call(C,"SetPressureProgramMode","PLAN");
            Call(C,"SetDischargeCalibrationWithEvidence",1000d,"OFFLINE-A.cal",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(new byte[32]));
            Call(C,"SetSelectedRange","PICO:"+range);Call(C,"SetAdvancedMode",manual);
            Call(C,"ConfigurePressureHardwareBackend",Xml(Settings()),new Func<string,double,CancellationToken,Task<string>>((xml,kv,ct)=>Task.FromResult(Capture(Project,xml,Driver,RawClip,Severe))));
            Call(C,"ConfigurePressureRangeReview",new Func<string,string,double,string>((dir,xml,kv)=>Review(dir,xml,kv,InvalidWindow,Forecast)));
            NoPrompt(C);Form.Show();Call(C,"ReportP04Status",true,true,DateTime.UtcNow);
            Pulse=new System.Windows.Forms.Timer{Interval=20};Pulse.Tick+=(s,e)=>Call(C,"ReportP04Status",true,true,DateTime.UtcNow);Pulse.Start();Pump();
        }
        internal void Start()
        {
            Call(C,"PressureActionClicked");Check((bool)Get(C,"ActionPending"),"operator creates real mailbox request");
            Check((bool)Call(C,"TryConsumePressureCaptureIntent",Get(C,"PressureRequestSequence"),Get(C,"RequestedTargetPeakVoltageKv")),"exact plan start intent consumed once");
            FinishCapture();
        }
        internal void FinishCapture()
        {
            Check(Wait((Task<bool>)Call(C,"ExecuteZeroVoltageHardwareActionAsync",Get(C,"ActionSequence"))),"managed capture/ACK succeeds");
            Check(!(bool)Get(C,"HasActiveHardwareSession")&&!(bool)Get(C,"IsBusy"),"actual unique consumer lease released before review");
            string expected=InvalidWindow?"ACQUISITION_INVALID":!Driver&&!RawClip?"SAVE_REQUESTED":"OVERRANGE_DECISION_REQUIRED";
            Check((string)Get(C,"PressureStateCode")=="COMPLETE"&&(string)Get(C,"PostProcessStateCode")==expected,"real consumer publishes measured quality terminal for "+Path.GetFileName(Project)+": "+Get(C,"PressureStateCode")+" / "+Get(C,"PostProcessStateCode"));
        }
        internal string Decide(){return Wait((Task<string>)Call(C,"ReviewPressureOverRangeAsync"));}
        public void Dispose(){Pulse.Stop();Pulse.Dispose();Form.Dispose();C.Dispose();Pump();}
    }
    static void Retry(Fixture f,int expected)
    {
        var c=f.C;long seq=(long)Get(c,"PressureRequestSequence");int step=(int)Get(c,"SelectedPlanStepIndex"),count=(int)Get(c,"CurrentPlanRepetition");
        var result=Field(c,"lastPressureCapture");string q=Xml(Field(result,"Quality")),original=Xml(Field(result,"OriginalRangeQuality"));
        string raw=Path.Combine((string)Field(result,"Directory"),"capture.bin"),hash=Hash(raw),existing=Hash(Path.Combine((string)Field(result,"Directory"),"pressure-range-quality.xml"));
        string decision=f.Decide();
        if(decision!="Retry")Console.WriteLine("OVERFLOW_RETRY_DIAGNOSTIC="+decision+";range="+Get(c,"SelectedPicoRangeCode")+";auto="+Get(c,"AutomaticRange")+";advanced="+Get(c,"AdvancedMode")+";status="+Get(c,"LastPressureRangeReviewStatus")+";details="+Get(c,"LastPressureRangeReviewDetails"));
        Check(decision=="Retry","real uncertain overflow automatically retries without reminder suppression");
        Check(!(bool)Get(c,"OverRangeIgnored")&&!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"abnormal round is not accepted/saved/analyzed");
        Check(q==Xml(Field(result,"Quality"))&&original==Xml(Field(result,"OriginalRangeQuality"))&&hash==Hash(raw)&&existing==Hash(Path.Combine((string)Field(result,"Directory"),"pressure-range-quality.xml")),"original quality, raw hash and original XML unchanged");
        string[] records=Directory.GetFiles((string)Field(result,"Directory"),"pressure-overflow-retry-*.xml");Check(records.Length==1,"independent overflow purpose recorded once");
        var record=ReadXml(module.GetType("HVX.Controls.PressureOverflowRetryRecord",true),File.ReadAllText(records[0]));
        Check(!(bool)Field(record,"DataAccepted")&&!(bool)Field(record,"ProvesHighVoltageSafe")&&!(bool)Field(record,"StartsHardware"),"XML is neither data acceptance nor electrical/release proof");
        Check((long)Field(record,"RequestSequence")==seq&&(int)Field(record,"NextRangeIndex")==expected&&(string)Field(record,"RawSha256")==hash&&(bool)Field(record,"StartedPlanIntent"),"record binds exact source/request/next/consumed intent");
        Check((bool)Call(c,"RequestReviewedRangeRetry")&&(int)Get(c,"SelectedPicoRangeCode")==expected&&(long)Get(c,"PressureRequestSequence")==seq+1,"fresh one-use request moves only upwards");
        Check(!(bool)Call(c,"RequestReviewedRangeRetry"),"permit cannot replay");
        Check((int)Get(c,"SelectedPlanStepIndex")==step&&(int)Get(c,"CurrentPlanRepetition")==count,"retry does not advance plan/count");
        Check((bool)Call(c,"TryConsumePressureCaptureIntent",seq+1,Get(c,"RequestedTargetPeakVoltageKv")),"retry inherits consumed intent even when ordinary reminders remain enabled");
        Check(!(bool)Call(c,"TryConsumePressureCaptureIntent",seq+1,Get(c,"RequestedTargetPeakVoltageKv")),"inherited intent is one-use and not fake operator click");
    }
    static void Tests()
    {
        using(var f=new Fixture("invalid-window-clean-no-overflow")){
            f.Driver=f.RawClip=false;f.InvalidWindow=true;f.Start();var c=f.C;
            Check(!(bool)Get(c,"LastAcquisitionOverRange")&&!(bool)Get(c,"OverRangeDecisionPending"),"invalid calculation window is not mislabeled as physical overrange");
            Check(((string)Get(c,"LastPostProcessError")).Contains("无法评估")&&!((string)Get(c,"LastPostProcessError")).Contains("超量程"),"window failure has a concise specific customer explanation");
            Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed")&&(bool)Get(c,"AutomaticPressureStopped"),"invalid window retains evidence without continuing/saving/analyzing");
            Check((int)Field(c,"manualHigherRangeRequired")==-1,"invalid window never creates a manual higher-range lock");
            string raw=Path.Combine((string)Field(Field(c,"lastPressureCapture"),"Directory"),"capture.bin"),hash=Hash(raw);
            Call(c,"RequestSaveCurrentRound");
            Check((string)Get(c,"PostProcessStateCode")=="ACQUISITION_INVALID"&&!(bool)Get(c,"DataSaved")&&Hash(raw)==hash,"manual save cannot adopt an invalid evaluation or modify the retained raw capture");
            Check(f.Decide()=="Stale"&&!(bool)Call(c,"RequestReviewedRangeRetry"),"invalid window cannot mint a range retry permit");
        }
        using(var f=new Fixture("sustained-two-ranges")){
            f.Severe=f.Forecast=true;f.Start();var c=f.C;
            var source=Field(c,"lastPressureCapture");var original=Field(source,"OriginalRangeQuality");
            Check((int)Field(original,"RecommendedIndex")>=7,"frozen amplitude advisor independently forecasts at least two higher ranges");
            Retry(f,7);
            var floor=Field(c,"pressurePlanRangeFloor");
            Check((int)Field(floor,"Minimum")==-1&&(int)Field(floor,"IssuedMinimum")==7,"issued two-step hold is not a saved useful measurement");
            Check((bool)Call(Call(c,"PressurePlanVisitedRangesNoLock"),"Contains",6),"skipped intermediate excluded only from scheduling");
            Check(!(bool)Call(Field(floor,"failed"),"Contains",6),"unmeasured skipped range is not fabricated as failed");
            f.Driver=f.RawClip=f.Severe=false;f.FinishCapture();
            Check((int)Call(c,"SavedPressurePlanRangeFloorNoLock",Field(c,"lastPressureCapture"),7,false)==7&&(bool)Call(floor,"Blocks",6),"before save, scheduling hold prevents immediate reverse without unlocking save/analysis gates");
            Check((int)Get(c,"SelectedPicoRangeCode")==7&&(int)Field(floor,"Minimum")==-1,"before save, conservative hold is not accepted data");
            Check(!(bool)Get(c,"ActionPending"),"range hold itself does not issue a pressure request");
            Check((bool)Call(c,"CompleteSave",true,""),"actual managed save acknowledgement accepted");
            if((string)Get(c,"PostProcessStateCode")=="ANALYSIS_REQUESTED")Check((bool)Call(c,"CompleteAnalysis",true,""),"actual managed analysis acknowledgement accepted");
            Check(Wait((Task<bool>)Call(c,"ReviewNextPressureRangeAsync"))&&(int)Get(c,"SelectedPicoRangeCode")==7,"saved clean low signal does not immediately reverse two-step upscale");
            Check((int)Call(c,"SavedPressurePlanRangeFloorNoLock",Field(c,"lastPressureCapture"),7,false)==7&&(int)Field(floor,"Minimum")==7,"only accepted saved complete cycle establishes saved floor");
            Call(c,"InterruptPressureContinuation");Check((int)Field(floor,"Minimum")==-1&&(int)Field(floor,"IssuedMinimum")==-1,"cancel clears both holds and cannot authorize next round");
        }
        using(var f=new Fixture("sustained-target-visited-fallback")){
            f.Severe=f.Forecast=true;f.Start();var c=f.C;
            Field(c,"pressureRangeRetryRequest",Get(c,"PressureRequestSequence"));Field(c,"pressureRangeRetryContext",Call(c,"CaptureRangeRetryContextNoLock"));Call(Field(c,"pressureRangeRetryVisited"),"Add",7);
            Retry(f,6);Check((int)Field(Field(c,"pressurePlanRangeFloor"),"IssuedMinimum")==-1,"one-step fallback does not create two-step hold");
        }
        using(var f=new Fixture("sustained-intermediate-visited-denied")){
            f.Severe=f.Forecast=true;f.Start();var c=f.C;
            Field(c,"pressureRangeRetryRequest",Get(c,"PressureRequestSequence"));Field(c,"pressureRangeRetryContext",Call(c,"CaptureRangeRetryContextNoLock"));Call(Field(c,"pressureRangeRetryVisited"),"Add",6);
            Check(f.Decide()!="Retry"&&!(bool)Call(c,"RequestReviewedRangeRetry"),"visited intermediate is never skipped to reach two-step target");
        }
        using(var f=new Fixture("single-raw-point-strong-quality-claim")){
            f.Forecast=true;f.Start();var c=f.C;
            foreach(string name in new[]{"Quality","OriginalRangeQuality"}){var q=Field(Field(c,"lastPressureCapture"),name);Field(q,"LocationClippedSamples",100);Field(q,"LocationLongestClippedRun",100);}
            Retry(f,6);Check((int)Field(Field(c,"pressurePlanRangeFloor"),"IssuedMinimum")==-1,"measured raw isolated clipping overrides externally claimed severity");
        }
        using(var f=new Fixture("positive-bounded")) {
            f.Start();for(int next=6;next<=10;next++){Retry(f,next);f.FinishCapture();}
            var c=f.C;Check(f.Decide()=="Stopped","20 V abnormal round stops finite chain");
            Check(((string)Get(c,"LastPressureRangeReviewStatus")).Contains("放电量严重超标")&&((string)Get(c,"LastPressureRangeReviewDetails")).Contains("20 V"),"maximum alert is explicit with honest raw/ADC/calibration explanation");
            Check(!(bool)Call(c,"RequestReviewedRangeRetry")&&!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"maximum never restarts/saves/analyzes invalid data");
            string dir=(string)Field(Field(c,"lastPressureCapture"),"Directory");int held=Directory.GetFiles(dir,"pressure-quality-held-*.xml").Length;
            Check(f.Decide()=="Stale"&&Directory.GetFiles(dir,"pressure-quality-held-*.xml").Length==held,"same maximum decision is not repeatedly prompted/journaled");
        }
        using(var f=new Fixture("raw-only")){f.Driver=false;f.Start();Retry(f,6);}
        foreach(string mode in new[]{"driver-only","outside-window-only","no-actual-overflow","invalid-window","no-intent","manual","no-release-claim","missing-calibration","visited-all","retry-budget","simulated-source","wrong-range","wrong-raw-hash"})
        using(var f=new Fixture("deny-"+mode,5,mode=="manual")) {
            if(mode=="driver-only")f.RawClip=false;
            if(mode=="no-actual-overflow"){f.Driver=false;f.RawClip=false;}if(mode=="invalid-window")f.InvalidWindow=true;f.Start();var c=f.C;
            if(mode=="outside-window-only") {
                var result=Field(c,"lastPressureCapture");var native=Field(result,"Capture");var bounds=CycleBounds(native);var adc=(short[])Field(native,"PdAdc");
                for(int i=bounds[0];i<bounds[1];i++)adc[i]=0;Field(Field(result,"Quality"),"LocationClippedSamples",0);
            }
            if(mode=="no-intent")Field(c,"operatorPressureIntentConsumed",false);
            if(mode=="no-release-claim")Field(c,"zeroLastClaimedActionSequence",0L);
            if(mode=="missing-calibration")Field(c,"calibrationPcPerVolt",Double.NaN);
            if(mode=="simulated-source")Field(Field(Field(c,"lastPressureCapture"),"Capture"),"Simulation",true);
            if(mode=="wrong-range")Field(Field(Field(Field(c,"lastPressureCapture"),"Capture"),"Settings"),"RangeIndex",6);
            if(mode=="wrong-raw-hash")Field(Field(c,"lastPressureCapture"),"RawSha256",new string('0',64));
            if(mode=="visited-all"||mode=="retry-budget") {
                Field(c,"pressureRangeRetryRequest",Get(c,"PressureRequestSequence"));Field(c,"pressureRangeRetryContext",Call(c,"CaptureRangeRetryContextNoLock"));
                if(mode=="retry-budget")Field(c,"pressureRangeRetries",10);
                else {var visited=Field(c,"pressureRangeRetryVisited");for(int i=6;i<=10;i++)Call(visited,"Add",i);}
            }
            string outcome;try{outcome=f.Decide();}catch(InvalidDataException){outcome="RejectedInvalidSource";}catch(InvalidOperationException){outcome="RejectedInvalidSource";}
            Check(outcome!="Retry"&&!(bool)Call(c,"RequestReviewedRangeRetry"),mode+" cannot create automatic overflow permit");
            Check(!(bool)Get(c,"OverRangeIgnored")&&!(bool)Get(c,"DataSaved"),mode+" cannot accept abnormal quality");
        }
        foreach(string mode in new[]{"stop","LOW","unknown","project","calibration","calibration-hash","quality","raw-modified","journal-modified","new-backend-generation","late-action","visited-next"})
        using(var f=new Fixture("revoke-"+mode)) {
            f.Start();var c=f.C;Check(f.Decide()=="Retry","prepare narrow permit before "+mode);int range=(int)Get(c,"SelectedPicoRangeCode");
            var source=Field(c,"lastPressureCapture");string dir=(string)Field(source,"Directory");
            if(mode=="stop")Call(c,"InterruptPressureContinuation");
            if(mode=="LOW"||mode=="unknown"){f.Pulse.Stop();Call(c,"ReportP04Status",mode=="LOW",false,DateTime.UtcNow);}
            if(mode=="project")Set(c,"ProjectPath",f.Project+"-changed");
            if(mode=="calibration")Field(c,"calibrationPcPerVolt",1001d);
            if(mode=="calibration-hash")Field(c,"calibrationSourceHash",Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray()));
            if(mode=="quality")Field(Field(source,"Quality"),"LocationClippedSamples",123);
            if(mode=="raw-modified")File.AppendAllText(Path.Combine(dir,"capture.bin"),"tamper");
            if(mode=="journal-modified")File.AppendAllText(Directory.GetFiles(dir,"pressure-overflow-retry-*.xml")[0],"tamper");
            if(mode=="new-backend-generation")Field(c,"zeroHardwareGeneration",(long)Field(c,"zeroHardwareGeneration")+1);
            if(mode=="late-action")Field(c,"actionSequence",(long)Get(c,"ActionSequence")+1);
            if(mode=="visited-next")Call(Field(c,"pressureRangeRetryVisited"),"Add",6);
            Check(!(bool)Call(c,"RequestReviewedRangeRetry")&&(int)Get(c,"SelectedPicoRangeCode")==range,mode+" revokes before changing hardware range or publishing request");
        }
    }
    [STAThread]static int Main(string[] args)
    {
        try {
            string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"overflow");Directory.CreateDirectory(qa);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));control=module.GetType("HVX.Controls.PressureTestControl",true);
            settings=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);capture=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);quality=module.GetType("Xiongxin.ZeroBench.LocationRangeResult",true);review=module.GetType("HVX.Controls.PressureRangeReviewSnapshot",true);
            Application.EnableVisualStyles();Tests();Console.WriteLine("PRESSURE_OVERFLOW_UPSCALE_PASS="+checks+"; HardwareAccessed=false; SDKCalled=false; WorkersStarted=0; ProductStarted=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
