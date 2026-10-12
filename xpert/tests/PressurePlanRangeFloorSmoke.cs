using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

// Isolated synthetic capture delegates, real managed ACK/save/continuation
// consumers. Never executes product Main, worker Main, SDK or device discovery.
internal static class PressurePlanRangeFloorSmoke
{
    const BindingFlags B=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly module;static Type control,settings,capture,review;static string qa;static int checks,rounds;
    static void Check(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,B).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,B).SetValue(o,v,null);}
    static object Field(object o,string n){return o.GetType().GetField(n,B).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,B).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){var methods=o.GetType().GetMethods(B).Where(m=>m.Name==n&&m.GetParameters().Length==a.Length).ToArray();if(methods.Length!=1)throw new Exception("Missing/ambiguous method "+n+"/"+a.Length);return methods[0].Invoke(o,a);}
    static string Xml(object o){using(var w=new StringWriter()){new XmlSerializer(o.GetType()).Serialize(w,o);return w.ToString();}}
    static object Read(Type t,string x){using(var r=new StringReader(x))return new XmlSerializer(t).Deserialize(r);}
    static string Hash(string p){using(var s=SHA256.Create())using(var f=File.OpenRead(p))return BitConverter.ToString(s.ComputeHash(f)).Replace("-","");}
    static void Pump(){Application.DoEvents();Thread.Sleep(1);}
    static T Wait<T>(Task<T> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<12000)Pump();Check(t.IsCompleted,"bounded managed operation");return t.GetAwaiter().GetResult();}
    static object Settings(){var s=Activator.CreateInstance(settings);Field(s,"RealHardware",true);Field(s,"ArtSerial","QA-ART");Field(s,"PicoSerial","QA-PICO");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"RangeIndex",7);return s;}
    sealed class Fixture:IDisposable
    {
        internal readonly Form Form;internal readonly Control C;internal readonly string Project;internal readonly System.Windows.Forms.Timer Pulse;
        internal bool Overflow=true;internal int Recommended=7;internal bool Blocked,InvalidWindow;
        internal Fixture(string name,int[] counts=null)
        {
            Project=Path.Combine(qa,name);Directory.CreateDirectory(Project);Form=new Form{ClientSize=new Size(1200,760),Location=new Point(-28000,-28000),StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
            C=(Control)Activator.CreateInstance(control);Form.Controls.Add(C);C.Dock=DockStyle.Fill;
            Set(C,"ProjectPath",Project);Set(C,"MaximumPeakVoltageKv",30d);Set(C,"PlanVoltagesArePeak",true);Set(C,"FrequencyReminderEnabled",false);Set(C,"AutomaticPostProcessEnabled",true);Set(C,"AutomaticPressureCountdownEnabled",false);
            Call(C,"SetPressurePlan",new[]{1.5,1.7,1.8},counts??new[]{3,2,1},new[]{18.5,20d,22d});Call(C,"SetPressureProgramMode","PLAN");Call(C,"SetSelectedPlanStep",0);
            Call(C,"SetDischargeCalibrationWithEvidence",1000d,"QA-A.cal",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(new byte[32]));Call(C,"SetSelectedRange","PICO:7");Call(C,"SetAdvancedMode",false);
            Call(C,"ConfigurePressureHardwareBackend",Xml(Settings()),new Func<string,double,CancellationToken,Task<string>>((x,k,ct)=>Task.FromResult(Capture(x))));
            Call(C,"ConfigurePressureRangeReview",new Func<string,string,double,string>((d,x,k)=>Review(d,x)));
            Form.Show();Call(C,"ReportP04Status",true,true,DateTime.UtcNow);Pulse=new System.Windows.Forms.Timer{Interval=20};Pulse.Tick+=(s,e)=>Call(C,"ReportP04Status",true,true,DateTime.UtcNow);Pulse.Start();Pump();
        }
        string Capture(string x)
        {
            var s=Read(settings,x);var c=Activator.CreateInstance(capture);Field(c,"Settings",s);int range=(int)Field(s,"RangeIndex");
            var adc=new short[(int)Get(s,"RequestedPicoCount")];adc[100]=(short)(Overflow?32767:12000);if(Overflow)adc[0]=32767;
            Field(c,"PdAdc",adc);Field(c,"Overflow",Overflow);Field(c,"VoltageRaw",Enumerable.Range(0,(int)Get(s,"ExpectedArtCount")).Select(i=>.08*Math.Cos(2*Math.PI*i/5)).ToArray());
            Field(c,"PicoRateHz",Field(s,"RequestedPicoRateHz"));Field(c,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[range]);
            Field(c,"HardwareBits",module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration").GetMethod("HardwareBits",B).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
            Field(c,"ArtSerial","QA-ART");Field(c,"PicoSerial","QA-PICO");Field(c,"Runtime","ISOLATED FAKE CALLBACK NO DEVICE");Field(c,"CapturedUtc",DateTime.UtcNow);
            string dir=Path.Combine(Project,"Acquisition","Pressure","r"+(++rounds).ToString("000"));Call(c,"Save",dir);return dir;
        }
        string Review(string d,string x)
        {
            var s=Read(settings,x);var c=capture.GetMethod("Load",B).Invoke(null,new object[]{d});int range=(int)Field(s,"RangeIndex");
            var q=module.GetType("Xiongxin.ZeroBench.LocationRangeAdvisor").GetMethods(B).Single(m=>m.Name=="Evaluate"&&m.GetParameters().Length==9).Invoke(null,new[]{Field(c,"PdAdc"),(object)10,10000,Field(c,"MaximumAdc"),Field(c,"PicoFullScaleV"),Field(s,"RangeIndex"),Field(s,"CalibrationPulsePc"),Field(s,"PdCalibrationPcPerV"),Field(c,"Overflow")});
            // The fake external review deliberately recommends the old failed
            // range after a clean capture. Production quality bytes stay frozen.
            Field(q,"WindowValid",!InvalidWindow);Field(q,"DriverOverflowUncertain",Overflow);Field(q,"RecommendationUncertain",Overflow);Field(q,"SwitchingTransientExcluded",false);
            Field(q,"FactoryCycleWindow",true);Field(q,"EvaluationCycles",1);
            Field(q,"RecommendedIndex",Overflow?range:Recommended);Field(q,"Change",!Overflow&&Recommended!=range);
            var r=Activator.CreateInstance(review);Field(r,"Schema","HVX.Pressure.RangeReview/R27");Field(r,"Quality",q);Field(r,"CurrentRangeIndex",range);Field(r,"NextRangeIndex",Overflow?range:Recommended);
            Field(r,"CanApply",!Overflow&&Recommended!=range&&!Blocked&&!InvalidWindow);Field(r,"NextRoundBlocked",Blocked);return Xml(r);
        }
        internal void Start(){Call(C,"PressureActionClicked");Consume();Finish();}
        internal void Consume(){Check((bool)Get(C,"ActionPending"),"one real managed mailbox");Check((bool)Call(C,"TryConsumePressureCaptureIntent",Get(C,"PressureRequestSequence"),Get(C,"RequestedTargetPeakVoltageKv")),"one-use explicit/inherited start intent");}
        internal void Finish(){Check(Wait((Task<bool>)Call(C,"ExecuteZeroVoltageHardwareActionAsync",Get(C,"ActionSequence"))),"fake delegate completes actual consumer ACK");Check(!(bool)Get(C,"HasActiveHardwareSession")&&!(bool)Get(C,"IsBusy"),"owned managed lease released");Check((string)Get(C,"PressureStateCode")=="COMPLETE","consumer complete");}
        internal void Save(){Check((string)Get(C,"PostProcessStateCode")=="SAVE_REQUESTED","clean round requests real save ACK");File.WriteAllText(Path.Combine(Project,"fake-save-"+Get(C,"PressureRequestSequence")+".txt"),"OFFLINE TEST SAVE CALLBACK");Check((bool)Call(C,"CompleteSave",true,""),"save ACK accepted");if((string)Get(C,"PostProcessStateCode")=="ANALYSIS_REQUESTED")Check((bool)Call(C,"CompleteAnalysis",true,""),"fake analysis ACK accepted");Check((bool)Get(C,"DataSaved")&&(string)Get(C,"LocationCycleStatusCode")=="COMPLETE_CYCLES","only saved complete-cycle capture is useful");}
        internal void Retry(int next){long seq=(long)Get(C,"PressureRequestSequence");int rep=(int)Get(C,"CurrentPlanRepetition");Check(Wait((Task<string>)Call(C,"ReviewPressureOverRangeAsync"))=="Retry","actual overflow has independent bounded retry decision");Check((bool)Call(C,"RequestReviewedRangeRetry"),"one narrow new request");Check((int)Get(C,"SelectedPicoRangeCode")==next&&(long)Get(C,"PressureRequestSequence")==seq+1&&(int)Get(C,"CurrentPlanRepetition")==rep,"one higher calibrated range and same repetition");Consume();Finish();}
        internal int Floor(){return (int)Field(Field(C,"pressurePlanRangeFloor"),"Minimum");}
        public void Dispose(){Pulse.Stop();Pulse.Dispose();Form.Dispose();C.Dispose();Pump();}
    }
    static void AssertReview(Fixture f,int next,int floor,string source)
    {
        var c=f.C;var result=Field(c,"lastPressureCapture");var q=Field(result,"Quality");string before=Xml(q),original=Xml(Field(result,"OriginalRangeQuality"));
        string dir=(string)Field(result,"Directory"),raw=Path.Combine(dir,"capture.bin"),rawHash=Hash(raw),qualityHash=Hash(Path.Combine(dir,"pressure-range-quality.xml"));long request=(long)Get(c,"PressureRequestSequence");
        Check(Wait((Task<bool>)Call(c,"ReviewNextPressureRangeAsync")),"normal review prepares next round");Check((int)Get(c,"SelectedPicoRangeCode")==next&&f.Floor()==floor,"row floor or ordinary next range applied");
        Check(before==Xml(q)&&original==Xml(Field(result,"OriginalRangeQuality"))&&rawHash==Hash(raw)&&qualityHash==Hash(Path.Combine(dir,"pressure-range-quality.xml")),"original ADC/quality XML and recommendation unchanged");
        string xml=File.ReadAllText(Directory.GetFiles(dir,"next-range-decision-*.xml").Single());Check(xml.Contains("<DecisionSource>"+source+"</DecisionSource>")&&xml.Contains("<MinimumRetryFloor>"+floor+"</MinimumRetryFloor>"),"independent floor decision recorded");
        Check(xml.Contains("<StartsHardware>false</StartsHardware>")&&xml.Contains("<ProvesDischargeSafe>false</ProvesDischargeSafe>")&&(long)Get(c,"PressureRequestSequence")==request&&!(bool)Get(c,"ActionPending"),"floor/review grants no output and starts no capture");
    }
    static void Positive()
    {
        using(var f=new Fixture("count321")){
            f.Start();f.Retry(8);f.Retry(9);f.Overflow=false;f.Retry(10);f.Save();AssertReview(f,10,10,"AutomaticPlanOverflowFloor");
            for(int repetition=2;repetition<=3;repetition++){
                Check(Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync")),"saved same-voltage continuation issues once");Check((int)Get(f.C,"SelectedPlanStepIndex")==0&&(int)Get(f.C,"CurrentPlanRepetition")==repetition&&(int)Get(f.C,"SelectedPicoRangeCode")==10,"18.5 kV accepted repetitions retain successful range");f.Consume();f.Finish();f.Save();AssertReview(f,10,10,"AutomaticPlanOverflowFloor");
            }
            Check(Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync")),"three accepted rounds move to next voltage");Check((int)Get(f.C,"SelectedPlanStepIndex")==1&&(int)Get(f.C,"CurrentPlanRepetition")==1&&f.Floor()==-1,"new target row resets floor, not global ban on downscale");f.Consume();f.Finish();f.Save();AssertReview(f,9,-1,"AutomaticRange");
            Check(Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync")),"ordinary second repetition after downward advice");Check((int)Get(f.C,"CurrentPlanRepetition")==2&&(int)Get(f.C,"SelectedPicoRangeCode")==9,"count2 and permitted one-step downscale");f.Consume();f.Finish();f.Save();
            Check(Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync")),"two accepted rounds move to count1 row");Check((int)Get(f.C,"SelectedPlanStepIndex")==2&&(int)Get(f.C,"CurrentPlanRepetition")==1,"count1 next target reached without repeated overflow");f.Consume();f.Finish();f.Save();Check(!Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync")),"final accepted count1 row terminates finite plan");
        }
    }
    static void Guards()
    {
        foreach(string mode in new[]{"not-saved","no-release","incomplete-cycle","stop","unknown","LOW","project","phase","calibration","calibration-hash","backend","new-start","critical","invalid-window"})
        using(var f=new Fixture("deny-"+mode)){
            f.Start();f.Overflow=false;f.Retry(8);if(mode!="not-saved")f.Save();
            if(mode=="no-release")Field(f.C,"zeroLastClaimedActionSequence",0L);
            if(mode=="incomplete-cycle")Field(f.C,"locationCycleWindow",null);
            if(mode=="stop")Call(f.C,"InterruptPressureContinuation");
            if(mode=="unknown"||mode=="LOW"){f.Pulse.Stop();Call(f.C,"ReportP04Status",mode=="LOW",false,DateTime.UtcNow);}
            if(mode=="project")Set(f.C,"ProjectPath",f.Project+"-changed");if(mode=="phase")Field(f.C,"selectedPhaseCode","B");
            if(mode=="calibration")Field(f.C,"calibrationPcPerVolt",1001d);if(mode=="calibration-hash")Field(f.C,"calibrationSourceHash","changed");
            if(mode=="backend")Field(f.C,"zeroHardwareGeneration",(long)Field(f.C,"zeroHardwareGeneration")+1);
            if(mode=="new-start"){Call(f.C,"PressureActionClicked");Check(f.Floor()==-1,"fresh operator start resets history");Call(f.C,"CompleteAction",Get(f.C,"ActionSequence"),false,"isolated test canceled");continue;}
            var r=Field(Field(f.C,"lastPressureCapture"),"RangeReview");if(mode=="critical")Field(r,"NextRoundBlocked",true);if(mode=="invalid-window")Field(Field(r,"Quality"),"WindowValid",false);
            bool accepted=Wait((Task<bool>)Call(f.C,"ReviewNextPressureRangeAsync"));Check(f.Floor()==-1,mode+" cannot establish useful floor");Check(!(bool)Get(f.C,"ActionPending"),mode+" does not create new output request");
            if(mode=="stop"||mode=="unknown"||mode=="LOW"||mode=="project"||mode=="phase"||mode=="critical"||mode=="invalid-window")Check(!accepted,mode+" never prepares automatic continuation");
        }
        foreach(string mode in new[]{"late-unknown-HIGH","late-LOW-HIGH","late-stop","late-calibration-hash"})
        using(var f=new Fixture(mode)){
            f.Start();f.Overflow=false;f.Retry(8);f.Save();AssertReview(f,8,8,"AutomaticPlanOverflowFloor");
            if(mode=="late-stop")Call(f.C,"InterruptPressureContinuation");
            else if(mode=="late-calibration-hash")Field(f.C,"calibrationSourceHash","new-source");
            else {Call(f.C,"ReportP04Status",mode=="late-LOW-HIGH",false,DateTime.UtcNow);Call(f.C,"ReportP04Status",true,true,DateTime.UtcNow.AddTicks(1));}
            Check(!Wait((Task<bool>)Call(f.C,"ReviewNextPressureRangeAsync"))&&f.Floor()==-1,mode+" cannot reuse a cached next-round/floor decision even after HIGH");
            Check(!Wait((Task<bool>)Call(f.C,"ContinueCompletedPressurePlanAsync"))&&!(bool)Get(f.C,"ActionPending"),mode+" cannot start next repetition");
        }
        var memory=Activator.CreateInstance(module.GetType("HVX.Controls.PressurePlanRangeFloor",true),true);
        Call(memory,"Bind","epoch1/row0");Call(memory,"Failed",7);Call(memory,"AcceptedSaved",7);Check((int)Field(memory,"Minimum")==-1,"a failed range cannot become a successful floor");
        Call(memory,"AcceptedSaved",8);Call(memory,"Bind","epoch1/row0");Check((int)Field(memory,"Minimum")==8,"repetition-independent row retains floor");
        Call(memory,"Failed",8);Call(memory,"AcceptedSaved",9);Check((int)Field(memory,"Minimum")==9,"subsequent real overflow can only raise useful floor");
        Call(memory,"Bind","epoch1/row1");Check((int)Field(memory,"Minimum")==-1,"new row resets floor");Call(memory,"Failed",7);Call(memory,"AcceptedSaved",8);Call(memory,"Bind","epoch2/row1");Check((int)Field(memory,"Minimum")==-1,"new explicit start resets floor");
        Call(memory,"IssuedUpscale",5,7);Check((int)Field(memory,"Minimum")==-1&&(int)Field(memory,"IssuedMinimum")==7,"issued double-upscale hold remains distinct from accepted saved evidence");
        Check((bool)Call(memory,"Blocks",6)&&!(bool)Call(memory,"Blocks",7),"same-row scheduling cannot reverse into skipped intermediate");
        var empty=Activator.CreateInstance(typeof(System.Collections.Generic.HashSet<int>));
        var visited=Call(memory,"Visited",empty);Check((bool)Call(visited,"Contains",6)&&!(bool)Call(Field(memory,"failed"),"Contains",6),"skipped intermediate is scheduling-only, not fabricated failed capture");
        Call(memory,"IssuedUpscale",7,8);Check((int)Field(memory,"IssuedMinimum")==7,"ordinary one-step change does not claim new two-step hold");
        Call(memory,"Reset");Check((int)Field(memory,"Minimum")==-1&&(int)Field(memory,"IssuedMinimum")==-1&&!(bool)Call(Call(memory,"Visited",empty),"Contains",6),"reset clears issued hold and skipped scheduling memory");
    }
    [STAThread]static int Main(string[] args)
    {
        try{string root=Path.GetFullPath(args[0]);qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));control=module.GetType("HVX.Controls.PressureTestControl",true);settings=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);capture=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);review=module.GetType("HVX.Controls.PressureRangeReviewSnapshot",true);Application.EnableVisualStyles();Positive();Guards();Console.WriteLine("PRESSURE_PLAN_RANGE_FLOOR_PASS="+checks+";HardwareAccessed=false;SDKCalled=false;WorkersStarted=0;ProductStarted=false;OriginalProjectsWritten=false");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
