using System;
using System.Collections.Generic;
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

// Uses synthetic files and injected delegates only. No driver, discovery API,
// native DLL, physical serial number, or worker executable is invoked.
internal static class PressureZeroVoltageBackendSmoke
{
    static Assembly module;
    static Type controlType, settingsType, captureType, browserType;
    static string qa;
    static int checks, rounds;
    static Func<object, object> choiceHandler;
    static void Check(bool v,string m){checks++;if(!v)throw new Exception(m);}
    static object Get(object o,string p){return o.GetType().GetProperty(p).GetValue(o,null);}
    static void Set(object o,string p,object v){o.GetType().GetProperty(p).SetValue(o,v,null);}
    static object Field(object o,string f){return o.GetType().GetField(f,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(o);}
    static void Field(object o,string f,object v){o.GetType().GetField(f,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(o,v);}
    static object Call(object o,string m,params object[] a){return o.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(x=>x.Name==m&&x.GetParameters().Length==a.Length).Invoke(o,a);}
    static void Pump(int ms=20){var w=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(1);}while(w.ElapsedMilliseconds<ms);}
    static bool Finish(Task<bool> t){var w=Stopwatch.StartNew();while(!t.IsCompleted&&w.ElapsedMilliseconds<20000)Pump(5);Check(t.IsCompleted,"capture completes within bounded offline test");return t.GetAwaiter().GetResult();}
    static string Xml(object value){using(var sw=new StringWriter()){new XmlSerializer(settingsType).Serialize(sw,value);return sw.ToString();}}
    static object Settings(string xml=null){if(xml!=null)using(var sr=new StringReader(xml))return new XmlSerializer(settingsType).Deserialize(sr);var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART-FIXTURE");Field(s,"PicoSerial","OFFLINE-PICO-FIXTURE");Field(s,"CaptureDurationMs",1d);Field(s,"PdCalibrationPcPerV",1000d);Field(s,"CalibrationPulsePc",100d);Field(s,"RangeIndex",5);return s;}
    static string Hash(string p){using(var h=SHA256.Create())using(var f=File.OpenRead(p))return BitConverter.ToString(h.ComputeHash(f));}
    static string Capture(string project,string xml,int adc=16000,Action<object> alter=null)
    {
        var s=Settings(xml);var c=Activator.CreateInstance(captureType);Field(c,"Settings",s);
        Field(c,"VoltageRaw",Enumerable.Repeat(.001,(int)Get(s,"ExpectedArtCount")).ToArray());
        Field(c,"PdAdc",Enumerable.Repeat((short)adc,(int)Get(s,"RequestedPicoCount")).ToArray());
        Field(c,"PicoRateHz",(double)Field(s,"RequestedPicoRateHz"));Field(c,"PicoFullScaleV",new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[(int)Field(s,"RangeIndex")]);
        var config=module.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration",true);
        Field(c,"HardwareBits",config.GetMethod("HardwareBits",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{Field(s,"ResolutionBits"),Field(s,"RangeIndex")}));
        Field(c,"ArtSerial",Field(s,"ArtSerial"));Field(c,"PicoSerial",Field(s,"PicoSerial"));Field(c,"Runtime","OFFLINE FIXTURE — NOT A DEVICE ACQUISITION");Field(c,"CapturedUtc",DateTime.UtcNow);
        if(alter!=null)alter(c);
        // The run root is already unique; avoid redundant GUID nesting that can
        // exhaust .NET Framework's path budget in the full release harness.
        string path=Path.Combine(project,"Acquisition","Task39","r"+Interlocked.Increment(ref rounds).ToString("000"));Call(c,"Save",path);return path;
    }
    static object Choice(object context){return choiceHandler(context);}
    static object Decision(string name,bool silent=false)
    {
        var t=module.GetType("HVX.Controls.PressureRangeChoice",true);var c=Activator.CreateInstance(t,true);
        Field(c,"Action",Enum.Parse(module.GetType("Xiongxin.ZeroBench.RangeDecisionAction"),name));Field(c,"Suppress",silent);return c;
    }
    static void HookChoice(object c,Func<object,object> handler)
    {
        choiceHandler=handler;var f=controlType.GetField("ZeroRangeDecisionForTests",BindingFlags.Instance|BindingFlags.NonPublic);
        var invoke=f.FieldType.GetMethod("Invoke");var param=Expression.Parameter(invoke.GetParameters()[0].ParameterType,"context");
        f.SetValue(c,Expression.Lambda(f.FieldType,Expression.Convert(Expression.Call(typeof(PressureZeroVoltageBackendSmoke).GetMethod("Choice",BindingFlags.Static|BindingFlags.NonPublic),Expression.Convert(param,typeof(object))),invoke.ReturnType),param).Compile());
    }
    static void Configure(object c,Func<string,CancellationToken,Task<string>> backend,object settings=null)
    {
        Call(c,"ConfigureZeroVoltageHardwareBackend",Xml(settings??Settings()),backend);
        // Explicit synthetic input: zero voltage still requires a current
        // emergency-stop sample. This test never reads a physical input.
        Call(c,"ReportP04Status",true,!(bool)Get(c,"AdvancedMode"),DateTime.UtcNow);
    }
    static Task<bool> Start(object c)
    {Call(c,"RequestPressure");Check((bool)Get(c,"ActionPending"),"pressure publishes handshake");return (Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",(long)Get(c,"ActionSequence"));}
    static void Run(string name,Action<Control,string> action)
    {
        string p=Path.Combine(qa,name);Directory.CreateDirectory(p);
        using(var form=new Form{ClientSize=new Size(1280,760),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ShowInTaskbar=false})using(var c=(Control)Activator.CreateInstance(controlType))
        using(var feedback=new System.Windows.Forms.Timer{Interval=20})
        {
            c.Dock=DockStyle.Fill;form.Controls.Add(c);Set(c,"ProjectPath",p);Set(c,"FrequencyReminderEnabled",false);Set(c,"MaximumPeakVoltageKv",20d);Set(c,"AutomaticRangeReminderEnabled",true);
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"SetPressureProgramMode","PLAN");
            Call(c,"SetDischargeCalibration",1000d,"fixture-A.cal",DateTimeOffset.UtcNow,"A",false);
            Call(c,"SetSelectedRange","PICO:5");Call(c,"SetAutomaticRange",false);form.Show();Pump();
            feedback.Tick+=delegate {
                if(!c.IsDisposed&&(bool)Get(c,"ActionHandshakeEnabled"))
                    Call(c,"ReportP04Status",true,name=="nonzero"||!(bool)Get(c,"AdvancedMode"),DateTime.UtcNow);
            };
            feedback.Start();try{action(c,p);}catch(Exception error){throw new Exception("Offline case: "+name,error);}finally{feedback.Stop();}
        }
        Pump();
    }
    static void Tests()
    {
        Run("operator-stop-at-completion",(c,p)=>{
            Set(c,"AnalyzeZeroVoltageData",true);Set(c,"AutomaticPostProcessEnabled",true);
            Call(c,"SetPressurePlan",new[]{0d,0d},new[]{1,3},new[]{0d,0d});Call(c,"SetSelectedPlanStep",1);
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Set(c,"AdvancedMode",false);
            int saves=0,analyses=0;
            controlType.GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saves++));
            controlType.GetEvent("AnalysisRequested").AddEventHandler(c,(EventHandler)((s,e)=>analyses++));
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml)));
            Field(c,"ZeroBeforeActionAcknowledgementForTests",new Action(()=>{
                Check((bool)Call(c,"RequestCurrentPressureStop"),"stop accepted at completed-worker / unacknowledged-result boundary");
                Check(!(bool)Get(c,"AdvancedMode"),"stop preserves automatic mode");
            }));
            Check(!Finish(Start(c)),"stopped result is not an accepted round");
            Check(saves==0&&analyses==0&&!(bool)Get(c,"DataSaved"),"late stop cannot trigger saving or analysis");
            Check(!(bool)Get(c,"HasActiveHardwareSession")&&!(bool)Get(c,"PressureNavigationLocked"),"actual completed owner releases without save-wait deadlock");
            Check(((double[])Field(c,"lastDischargePc")).Length>0,"already installed interrupted data remains visible");
            Check((int)Get(c,"SelectedPlanStepIndex")==1,"interruption does not advance plan");
            Call(c,"ReportAcquisitionCompleted",false);Call(c,"RequestSaveCurrentRound");
            Check(saves==0&&analyses==0,"duplicate completion and manual save cannot revive interrupted data");
            Field(c,"InterruptedPressureChoiceForTests",new Func<DialogResult>(()=>DialogResult.Cancel));
            Check(!(bool)Call(c,"ConfirmInterruptedPressureRestart"),"cancel restart keeps stopped state");
            Field(c,"InterruptedPressureChoiceForTests",new Func<DialogResult>(()=>DialogResult.Yes));
            Check((bool)Call(c,"ConfirmInterruptedPressureRestart")&&(int)Get(c,"SelectedPlanStepIndex")==1,"continue repeats interrupted step");
            Field(c,"interruptedPressureConfiguration",Call(c,"PressureRestartConfiguration"));
            Field(c,"InterruptedPressureChoiceForTests",new Func<DialogResult>(()=>DialogResult.No));
            Check((bool)Call(c,"ConfirmInterruptedPressureRestart")&&(int)Get(c,"SelectedPlanStepIndex")==0,"restart selects first step");
            Field(c,"interruptedPressureConfiguration",Call(c,"PressureRestartConfiguration"));
            Field(c,"selectedPlanStepIndex",1);
            Field(c,"InterruptedPressureChoiceForTests",new Func<DialogResult>(()=>{throw new Exception("changed configuration must not prompt stale resume");}));
            Check((bool)Call(c,"ConfirmInterruptedPressureRestart"),"changed selection discards old resume context");
            Check(!(bool)Get(c,"ActionPending"),"restart choice itself never starts hardware");
        });
        Run("zero-analysis-opt-in",(c,p)=>{
            string calHash=Convert.ToBase64String(new byte[32]);
            Call(c,"SetDischargeCalibrationWithEvidence",1000d,"fixture-calibration",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,250d,calHash);
            Set(c,"AnalyzeZeroVoltageData",true);Set(c,"AutomaticPostProcessEnabled",true);
            Call(c,"SetPressurePlan",new[]{0d,0d},new[]{1,1},new[]{0d,0d});
            Call(c,"SetSelectedPlanStep",0);
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            Set(c,"AdvancedMode",false);
            Check(!(bool)Get(c,"AdvancedMode"),"automatic zero plan is selected only after fresh synthetic HIGH");
            int saves=0,analyses=0;controlType.GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saves++));
            controlType.GetEvent("AnalysisRequested").AddEventHandler(c,(EventHandler)((s,e)=>analyses++));
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml)));
            Check(Finish(Start(c)),"opted-in zero hardware delegate completes");
            Check((bool)Get(c,"CurrentAcquisitionIsZeroVoltageAnalysis")&&!(bool)Get(c,"CurrentAcquisitionIsNoiseReference"),"opt-in real input distinguished from noise-only");
            Check(!(bool)Get(c,"HasActiveHardwareSession"),"input lease released before save/analysis");
            Check(saves==1&&analyses==0&&!(bool)Get(c,"DataSaved"),"one save requested, not acknowledged before commit");
            Check(!Finish((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync")),"zero opt-in cannot advance before save and analysis");
            Check(Field(c,"locationCycleWindow")==null,"zero input never creates oscillation cycles");
            var saveTask=(Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d);
            var deadline=Stopwatch.StartNew();while(!saveTask.IsCompleted&&deadline.ElapsedMilliseconds<20000)Pump(5);
            Check(saveTask.IsCompleted,"zero LOC save bounded");string saved=saveTask.GetAwaiter().GetResult();
            string[] lines=File.ReadAllLines(saved);
            string dem=Path.ChangeExtension(saved,".dem");
            Check(File.Exists(dem),"each saved round also publishes a same-name DEM");
            string pending=Path.ChangeExtension(saved,".record.pending");
            Check(!File.Exists(pending),"successful pair removes its short-lived publication marker");
            string[] demLines=File.ReadAllLines(dem);
            Check(demLines.Length==96&&demLines[15]=="PD (pC):\tVoltage (kV):","legacy 16-line DEM with 80 actual common-time bins");
            Check(demLines[3]=="Cal.value (pC):\t250"&&demLines[4]=="Cal.max (mV):\tNaN","selected calibration charge retained; unknown measured calibration peak not invented");
            Check(demLines[12].Contains(calHash)&&demLines[12].Contains("\"calibrationCoefficientPcPerV\":1000"),"DEM preserves selected source hash and actual capture conversion coefficient");
            Check(demLines[8].StartsWith("Delta-x [s]:\t")&&demLines[12].Contains((string)Get(c,"LocationAcquisitionId")),"DEM records explicit time unit and LOC acquisition identity");
            var reopened=browserType.GetMethod("LoadProject",new[]{typeof(string)}).Invoke(null,new object[]{p});
            var tests=(System.Collections.IList)Field(reopened,"Tests");var locations=(System.Collections.IList)Field(reopened,"Locations");
            Check(tests.Count==1&&locations.Count==1,"browser reopening finds one test and its location record");
            Check((bool)Field(tests[0],"IsValid")&&(bool)Field(locations[0],"IsValid"),"both newly saved records parse successfully");
            // Simulate the files left by process exit between the two moves
            // and marker removal, without terminating a process or using hardware.
            File.WriteAllText(pending,"interrupted write fixture");
            var interrupted=browserType.GetMethod("LoadProject",new[]{typeof(string)}).Invoke(null,new object[]{p});
            Check(((System.Collections.IList)Field(interrupted,"Records")).Cast<object>().All(x=>!(bool)Field(x,"IsValid")),"pending pair is invalid in browser after reopen, even if both files exist");
            File.Delete(pending);
            Check((int)Field(tests[0],"Order")==1&&(int)Field(locations[0],"Order")==1&&(int)Field(locations[0],"Repeat")==1,"paired LOC inherits test order and repetition instead of 000");
            var restored=browserType.GetMethod("LoadWaveform",new[]{tests[0].GetType()}).Invoke(null,new[]{tests[0]});
            Check(((double[])Field(restored,"Y")).Length==80&&(double)Field(restored,"SampleRateHz")==80000,"browser reload uses explicit 80k detected time base");
            Check(Path.GetFileName(saved).StartsWith("A_0kV_")&&lines[2]=="Phase\tNaN"&&lines[3]=="Voltage (kV)\t0","native zero save retains unknown phase and 0 kV");
            Check(lines[10].StartsWith("HVX_ZeroVoltageReference\t")&&lines[10].Contains("full-segment-before-zero-voltage-analysis"),"native high-pass provenance retained");
            Check(lines.Length==((double[])Field(Field(c,"lastPressureCapture"),"ChargePc")).Length+11,"native full Pico segment saved without false cycle crop");
            Check((bool)Call(c,"CompleteSave",true,"")&&analyses==1,"successful disk commit starts analysis exactly once");
            Check(!(bool)Call(c,"CompleteSave",true,"")&&analyses==1,"duplicate save acknowledgement does not duplicate analysis");
            Check((bool)Call(c,"CompleteAnalysis",true,""),"zero analysis completion acknowledged");
            bool continued=Finish((Task<bool>)Call(c,"ContinueCompletedPressurePlanAsync"));
            if(!continued){foreach(string name in new[]{"advancedMode","planMode","finishCurrentRoundRequested","automaticPlanInterrupted","pressureEmergencyLatched","pressureStateCode","postProcessStateCode","issuedPlanStep","selectedPlanStepIndex","issuedPlanRepetition","currentPlanRepetition","currentAcquisitionReferenceOnly","currentZeroVoltageAnalysis","dataSaved","dataAnalyzed","continuedPressureSequence","pressureRequestSequence"})Console.WriteLine(name+"="+Field(c,name));Console.WriteLine("may="+Call(c,"MayContinueIssuedPressurePlanNoLock")+"; locked="+Get(c,"PressureParametersLocked"));}
            Check(continued,"saved analyzed zero round can prepare next existing plan step");
            Check((bool)Get(c,"ActionPending")&&!(bool)Get(c,"HasActiveHardwareSession"),"preparing next step is not a hardware acquisition");
        });
        Run("paired-save-failure",(c,p)=>{
            Set(c,"AnalyzeZeroVoltageData",true);Set(c,"AutomaticPostProcessEnabled",true);
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml)));
            Check(Finish(Start(c)),"paired-save fault fixture captured");
            Field(c,"BeforeDetectedRecordPublishForTests",new Action(()=>{
                Check(Directory.GetFiles(p,"*.record.pending").Length==1,"in-progress pair has a durable write intent");
                var project=browserType.GetMethod("LoadProject",new[]{typeof(string)}).Invoke(null,new object[]{p});
                Check(((System.Collections.IList)Field(project,"Locations")).Cast<object>().All(x=>!(bool)Field(x,"IsValid")),"published LOC cannot appear valid before DEM commit");
                throw new IOException("injected DEM publish failure");}));
            var task=(Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d);
            var deadline=Stopwatch.StartNew();while(!task.IsCompleted&&deadline.ElapsedMilliseconds<20000)Pump(5);
            Check(task.IsCompleted&&task.IsFaulted,"DEM publication failure cannot acknowledge a complete save");
            Check(Directory.GetFiles(p,"*.loc").Length==0&&Directory.GetFiles(p,"*.dem").Length==0,"failed pair is not exposed as a completed browser record");
            Check(Directory.GetFiles(p,"*.record.pending").Length==0,"fully hidden failed pair does not block explicit retry");
            Check(Directory.GetFiles(p,".incomplete-*.loc.tmp").Length==1,"new incomplete LOC retained for recovery");
            Check(File.Exists(Path.Combine((string)Get(c,"LastZeroVoltageCaptureDirectory"),"capture.bin")),"raw acquisition retained after publication failure");
            Field(c,"BeforeDetectedRecordPublishForTests",null);
            var retry=(Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d);
            deadline.Restart();while(!retry.IsCompleted&&deadline.ElapsedMilliseconds<20000)Pump(5);
            Check(retry.IsCompleted&&!retry.IsFaulted&&File.Exists(Path.ChangeExtension(retry.Result,".dem")),"explicit retry publishes one complete pair");
        });
        Run("action-published",(c,p)=>{
            int captures=0;var order=new List<string>();long publishedSequence=0;
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(Capture(p,xml));});
            EventHandler changed=(sender,e)=>{order.Add("changed");Check(!(bool)Get(c,"ActionPending")&&(bool)Get(c,"IsBusy"),"legacy setter event still precedes mailbox publication");};
            EventHandler published=(sender,e)=>{
                string code=(string)Get(c,"ActionCode");order.Add(code);
                Check((bool)Get(c,"ActionPending")&&(bool)Get(c,"IsBusy"),"published event observes coherent pending busy action");
                if(code=="RANGE_MODE"){
                    publishedSequence=(long)Get(c,"ActionSequence");
                    Check(((string)Get(c,"ActionParametersJson")).Contains("\"automaticRange\":true"),"published snapshot contains applied parameter");
                    Check((bool)Call(c,"CompleteAction",publishedSequence,true,""),"host can acknowledge local parameter from published event");
                }
            };
            controlType.GetEvent("RangeModeChanged").AddEventHandler(c,changed);controlType.GetEvent("ActionPublished").AddEventHandler(c,published);
            var button=(Control)Field(c,"automaticRangeButton");typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(button,new object[]{EventArgs.Empty});
            Check(order.SequenceEqual(new[]{"changed","RANGE_MODE"})&&publishedSequence>0,"actual parameter button raises old event then published notification");
            Check(!(bool)Get(c,"IsBusy")&&!(bool)Get(c,"ActionPending")&&captures==0,"parameter ACK unlocks without starting capture");
            controlType.GetEvent("RangeModeChanged").RemoveEventHandler(c,changed);
            controlType.GetEvent("ActionPublished").RemoveEventHandler(c,published);
            // Old hosts without the new event still read and complete exactly as before.
            button=(Control)Field(c,"manualRangeButton");typeof(Control).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(button,new object[]{EventArgs.Empty});
            Check((bool)Get(c,"ActionPending")&&(bool)Get(c,"IsBusy"),"unsubscribed legacy host keeps explicit mailbox acknowledgement");
            Check((bool)Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),true,""),"legacy CompleteAction remains valid");
            controlType.GetEvent("ActionPublished").AddEventHandler(c,published);
            EventHandler pressure=(sender,e)=>Check((bool)Get(c,"ActionPending")&&(string)Get(c,"ActionCode")=="PRESSURE","published notification does not prematurely consume PRESSURE");
            controlType.GetEvent("PressureRequested").AddEventHandler(c,pressure);
            bool pressureCompleted=Finish(Start(c));
            Check(pressureCompleted&&captures==1,"PRESSURE remains independently consumed by backend: success="+pressureCompleted+" captures="+captures+" error="+Get(c,"LastActionError")+" post="+Get(c,"LastPostProcessError"));
            foreach(string action in new[]{"DELETE","SAVE"}){
                Check((bool)Call(c,"BeginAction"),"test external operation starts mailbox");Call(c,"PublishAction",action);
                Check((bool)Get(c,"ActionPending")&&(bool)Get(c,"IsBusy"),action+" excluded from local parameter ACK");
                Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),false,"test: external operation was not performed");
            }
            c.Dispose();Check(Field(c,"ActionPublished")==null,"dispose detaches publication subscribers");
        });
        Run("good",(c,p)=>{
            string path=null,rawHash=null;
            Configure(c,(xml,ct)=>{path=Capture(p,xml);rawHash=Hash(Path.Combine(path,"capture.bin"));return Task.FromResult(path);});
            Check((bool)Get(c,"ActionHandshakeEnabled"),"live config enables action handshake");bool good=Finish(Start(c));Check(good,"valid zero capture completes: "+Get(c,"LastActionError"));
            Check(!(bool)Get(c,"HasActiveHardwareSession")&&!(bool)Get(c,"IsBusy"),"worker release clears busy only after completion");
            Check((bool)Get(c,"CurrentAcquisitionIsNoiseReference")&&(string)Get(c,"NoiseReferenceStateCode")=="Pending","zero reference stays unverified without measured-voltage evidence");
            Check(!(bool)Get(c,"IsHighVoltageReady"),"capture does not manufacture fresh P0.4");
            Check((bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed"),"raw saved is not scientific analysis");
            Check((string)Get(c,"LocationCycleStatusCode")=="NOT_EVALUATED","zero data never becomes LOC complete cycles");
            Check(!Directory.GetFiles(p,"*.loc",SearchOption.AllDirectories).Any(),"zero capture writes no LOC");
            var view=Field(c,"waveformView");Check((double)Field(view,"voltageRateHz")==80000&&(double)Field(view,"dischargeRateHz")==125000000,"independent ART/Pico time bases retained");
            Check(Math.Abs(((double[])Field(c,"lastVoltageKv"))[0]-.011)<1e-12,"voltage divider conversion");
            Check(Math.Abs(((double[])Field(c,"lastDischargePc"))[0])<1e-9,"constant ADC bias removed before calibrated pC conversion");
            Check(File.Exists(Path.Combine(path,"pressure-range-quality.xml")),"derived range evidence saved");
            string record=File.ReadAllText(Path.Combine(path,"pressure-range-quality.xml"));
            Check(record.Contains((string)Get(c,"LocationAcquisitionId"))&&record.Contains("<IncludedInDischargeStatistics>false"),"evidence same acquisition identity and exclusion");
            Check(rawHash==Hash(Path.Combine(path,"capture.bin")),"raw ADC byte hash unchanged");
        });
        Run("maximum",(c,p)=>{
            Call(c,"SetSelectedRange","PICO:10");int prompts=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("20 V maximum must not show an adjustment dialog");});
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,32767)));
            Field(c,"zeroRangeSilentSession",true);
            Check(Finish(Start(c)),"maximum capture completes as retained raw evidence");Check(prompts==0,"maximum range has no adjustment dialog");
            Check((bool)Get(c,"LastAcquisitionOverRange")&&!(bool)Get(c,"OverRangeIgnored"),"no dialog cannot invent clipping acceptance");
            Check(!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"DataAnalyzed")&&(string)Get(c,"PostProcessStateCode")=="OVERRANGE_REJECTED","maximum risk is display-only and not auto-saved/statistics");
            Check((bool)Get(c,"AutomaticPressureStopped")&&(string)Get(c,"PressureStateCode")=="COMPLETE","maximum quality hold stops continuation without a fake hardware failure");
        });
        Run("same-low",(c,p)=>{
            Call(c,"SetAutomaticRange",true);int prompts=0,captures=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("same low-only range has nothing to adjust");});
            var s=Settings();Field(s,"CalibrationPulsePc",250d);
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(Capture(p,xml,100));},s);
            Check(Finish(Start(c))&&captures==1&&prompts==0,"unchanged low-only completes once without useless adjustment UI");
            Check((int)Get(c,"SelectedPicoRangeCode")==5&&!(bool)Get(c,"OverRangeIgnored"),"same-range no-prompt is not operator acceptance or range change");
            string evidence=File.ReadAllText(Directory.GetFiles(p,"pressure-range-quality.xml",SearchOption.AllDirectories).Single());
            Check(evidence.Contains("<LocationQualityPassed>false")&&evidence.Contains("<LocationLowOccupancy>true"),"low-only measured quality retained as false");
        });
        Run("maximum-zero-analysis",(c,p)=>{
            Set(c,"AnalyzeZeroVoltageData",true);Set(c,"AutomaticPostProcessEnabled",true);Call(c,"SetSelectedRange","PICO:10");
            int saves=0,analyses=0;
            controlType.GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saves++));
            controlType.GetEvent("AnalysisRequested").AddEventHandler(c,(EventHandler)((s,e)=>analyses++));
            HookChoice(c,ctx=>{throw new Exception("maximum opted-in zero data must not prompt adjustment");});
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,32767)));
            Check(Finish(Start(c))&&saves==0&&analyses==0,"maximum quality hold prevents opted-in zero save/analysis callbacks");
            bool rejected=false;try{((Task<string>)Call(c,"SaveCurrentPressureRoundAsync",170d)).GetAwaiter().GetResult();}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&Directory.GetFiles(p,"*.loc",SearchOption.AllDirectories).Length==0,"direct save cannot bypass maximum quality hold");
            var capture=Field(c,"lastPressureCapture");Check(!(bool)Field(Field(capture,"Quality"),"LocationQualityPassed"),"maximum raw failed quality stays false");
        });
        Run("floor-low",(c,p)=>{
            Call(c,"SetSelectedRange","PICO:3");Call(c,"SetAutomaticRange",true);int prompts=0,captures=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("100 pC floor cannot shrink again");});
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(Capture(p,xml,100));});
            Check(Finish(Start(c))&&captures==1&&prompts==0&&(int)Get(c,"SelectedPicoRangeCode")==3,"100 pC floor low-only retains range and avoids adjustment dialog");
            Check(!Directory.GetFiles(p,"pressure-range-decision.xml",SearchOption.AllDirectories).Any()&&!(bool)Get(c,"OverRangeIgnored"),"floor is a bound, not fabricated explicit acceptance");
        });
        Run("small-reference",(c,p)=>{
            Call(c,"SetSelectedRange","PICO:3");Call(c,"SetAutomaticRange",true);int notices=0;
            Field(c,"ZeroReferenceNoticeForTests",new Action<string>(text=>{notices++;Check(text=="校准可能存在档位混淆。","small reference uses qualified, concise message");}));
            HookChoice(c,ctx=>{throw new Exception("small reference at floor must not adjust");});
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,0,cap=>{
                var adc=(short[])Field(cap,"PdAdc");adc[adc.Length/2]=100;
            })));
            Check(Finish(Start(c))&&notices==1,"real processed nonzero peak below 1 pC warns once per completed result");
            Check(!(bool)Get(c,"OverRangeIgnored"),"small reference notice grants no quality acceptance");
        });
        Run("retry",(c,p)=>{
            Call(c,"SetAutomaticRange",true);int captures=0,prompts=0,confirmations=0;
            HookChoice(c,ctx=>{prompts++;return Decision("AdjustRetry",true);});
            Field(c,"ZeroTriggerConfirmationForTests",new Func<bool,bool>(automatic=>{confirmations++;return true;}));
            var s=Settings();Field(s,"AutomaticSharedTrigger",true);
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,++captures==1?30000:16000)),s);
            Check(Finish(Start(c)),"authorized automatic range retry reaches good raw range");
            Check(captures==2&&prompts==1&&confirmations==2,"initial plus repeat trigger separately confirmed; silent range round not reprompted");
            Check(Directory.GetFiles(p,"capture.bin",SearchOption.AllDirectories).Length==2,"all raw retry rounds retained");
            Check(Directory.GetFiles(p,"pressure-range-decision.xml",SearchOption.AllDirectories).Length==1,"retry decision preserved");
            Check((int)Get(c,"SelectedPicoRangeCode")==6,"existing range selector tracks actual next Pico range");
        });
        Run("dismiss",(c,p)=>{
            Call(c,"SetSelectedRange","PICO:5");Call(c,"SetAutomaticRange",true);HookChoice(c,ctx=>Decision("Dismiss"));Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,32767)));
            Check(!Finish(Start(c)),"dismiss never means accept");Check((bool)Get(c,"DataSaved")&&Directory.GetFiles(p,"capture.bin",SearchOption.AllDirectories).Length==1,"dismiss retains raw data");
        });
        Run("overflow-silent",(c,p)=>{
            Call(c,"SetSelectedRange","PICO:10");Call(c,"SetAutomaticRange",true);int prompts=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("maximum overflow has no adjustment dialog");});Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,100,cap=>Field(cap,"Overflow",true))));
            Field(c,"zeroRangeSilentSession",true);Check(Finish(Start(c)),"uncertain maximum data retained without acceptance");
            Check(prompts==0&&(bool)Get(c,"LastAcquisitionOverRange")&&!(bool)Get(c,"DataSaved")&&!(bool)Get(c,"OverRangeIgnored"),"maximum overflow remains blocked in silent automatic session");
        });
        Run("uncertain-no-adjust",(c,p)=>{
            Call(c,"SetAutomaticRange",true);Set(c,"AutomaticRangeReminderEnabled",false);Set(c,"AnalyzeZeroVoltageData",true);Set(c,"AutomaticPostProcessEnabled",true);
            int prompts=0,saves=0,analyses=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("unlocated overflow must not show an Ignore-only dialog");});
            controlType.GetEvent("SaveRequested").AddEventHandler(c,(EventHandler)((s,e)=>saves++));controlType.GetEvent("AnalysisRequested").AddEventHandler(c,(EventHandler)((s,e)=>analyses++));
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,16000,cap=>Field(cap,"Overflow",true))));
            Check(Finish(Start(c))&&prompts==0&&saves==0&&analyses==0,"uncertain zero capture retained non-modally without save or analysis");
            var q=Field(Field(c,"lastPressureCapture"),"Quality");
            Check((bool)Field(q,"DriverOverflowUncertain")&&!(bool)Field(q,"LocationQualityPassed")&&!(bool)Get(c,"OverRangeIgnored"),"no adjustment preserves uncertain failed quality, not Ignore acceptance");
            Check((string)Get(c,"PostProcessStateCode")=="OVERRANGE_REJECTED"&&(bool)Get(c,"AutomaticPressureStopped")&&!(bool)Get(c,"IsBusy"),"uncertain hold stops future scheduling and releases completed capture busy lease");
            Check(File.Exists(Path.Combine((string)Get(c,"LastZeroVoltageCaptureDirectory"),"capture.bin")),"uncertain raw capture remains recoverable");
        });
        Run("persistent-silent",(c,p)=>{
            Call(c,"SetAutomaticRange",true);Set(c,"AutomaticRangeReminderEnabled",false);int captures=0,prompts=0;
            HookChoice(c,ctx=>{prompts++;throw new Exception("persisted ordinary reminder suppression must survive backend reconfiguration");});
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,++captures==1?30000:16000)));
            Check(Finish(Start(c))&&captures==2&&prompts==0,"saved ordinary preference drives bounded zero input retries after backend reconfiguration");
            Check(!(bool)Get(c,"AutomaticRangeReminderEnabled")&&!(bool)Get(c,"OverRangeIgnored"),"persistent suppression creates no quality acceptance");
        });
        foreach(bool remind in new[]{false,true})Run("final-zero-low-"+remind,(c,p)=>{
            Call(c,"SetPressurePlan",new[]{0d,1d,0d},new[]{1,1,1},new[]{0d,1d,0d});Call(c,"SetSelectedPlanStep",2);
            Call(c,"SetAutomaticRange",true);Set(c,"AutomaticRangeReminderEnabled",remind);int captures=0,prompts=0;
            string path=null,rawHash=null;
            HookChoice(c,ctx=>{prompts++;return Decision("Ignore");});
            Call(c,"ConfigurePressureHardwareBackend",Xml(Settings()),new Func<string,double,CancellationToken,Task<string>>((xml,target,ct)=>{
                captures++;Check(target==0,"final planned item is genuinely zero input");
                Check((bool)Call(c,"TryConsumePressureCaptureIntent",(long)Get(c,"PressureRequestSequence"),target),"synthetic host consumes explicit start only inside capture delegate");
                path=Capture(p,xml,100);rawHash=Hash(Path.Combine(path,"capture.bin"));return Task.FromResult(path);
            }));
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            Field(c,"operatorPressureClickInProgress",true);Task<bool> pending;
            try{pending=Start(c);}finally{Field(c,"operatorPressureClickInProgress",false);}
            Check(Finish(pending),"final zero low-amplitude result is retained without fabricating quality");Pump(60);
            Check(captures==1&&prompts==(remind?1:0),"final zero reminder honors persistent preference independently of retry authorization");
            Check((int)Get(c,"SelectedPicoRangeCode")==5&&(int)Get(c,"SelectedPlanStepIndex")==2,"hidden final reminder neither changes actual range nor repeats/advances the last item");
            string qualityEvidence=File.ReadAllText(Path.Combine(path,"pressure-range-quality.xml"));
            Check(qualityEvidence.Contains("<LocationLowOccupancy>true")&&qualityEvidence.Contains("<LocationQualityPassed>false"),"hidden ordinary advice does not relabel measured low amplitude as quality passed");
            Check(rawHash==Hash(Path.Combine(path,"capture.bin")),"final zero source ADC is not rewritten");
            if(!remind){
                Check(!(bool)Get(c,"OverRangeIgnored"),"hidden reminder is not operator acceptance");
                string decision=File.ReadAllText(Path.Combine(path,"pressure-range-decision.xml"));
                Check(decision.Contains("<SuppressFurtherRangePrompts>true")&&decision.Contains("<OperatorAcceptedData>false")&&decision.Contains("<RequestedRetry>false"),"suppressed final zero advice records no acceptance or retry permission");
            }
        });
        Run("suppressed-zero-clipping-still-prompts",(c,p)=>{
            Call(c,"SetAutomaticRange",true);Set(c,"AutomaticRangeReminderEnabled",false);int captures=0,prompts=0;
            HookChoice(c,ctx=>{prompts++;return Decision("Dismiss");});
            Call(c,"ConfigurePressureHardwareBackend",Xml(Settings()),new Func<string,double,CancellationToken,Task<string>>((xml,target,ct)=>{
                captures++;return Task.FromResult(Capture(p,xml,32767));
            }));
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);
            Check(!Finish(Start(c))&&captures==1&&prompts==1,"ordinary suppression cannot hide a real clipping decision or create a retry");
            Check((bool)Get(c,"LastAcquisitionOverRange")&&!(bool)Get(c,"OverRangeIgnored"),"raw clipping risk remains explicit and unaccepted");
        });
        Run("manual-retry-limit",(c,p)=>{
            Call(c,"SetAutomaticRange",true);int captures=0,prompts=0;
            HookChoice(c,ctx=>{prompts++;if(prompts==2)Check((int)Get(ctx,"RetriesUsed")==1&&!(bool)Get(ctx,"IsAutomaticRangeRetry"),"manual retry limit retained");return Decision(prompts==1?"AdjustRetry":"Ignore");});
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(Capture(p,xml,30000));});Check(Finish(Start(c)),"manual second-round raw capture retained at retry limit");
            Check(captures==2&&prompts==1&&!(bool)Field(c,"zeroRangeSilentSession"),"manual retry limit stops without an Ignore-only dialog or silent chaining");
            Check(!(bool)Get(c,"OverRangeIgnored")&&!(bool)Get(c,"DataSaved")&&(string)Get(c,"PostProcessStateCode")=="OVERRANGE_REJECTED","exhausted retries do not auto-accept or save the failed-quality round");
        });
        Run("trigger-denied",(c,p)=>{
            int captures=0;var settings=Settings();Field(settings,"AutomaticSharedTrigger",true);
            Field(c,"ZeroTriggerConfirmationForTests",new Func<bool,bool>(automatic=>false));
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(p);},settings);
            Check(!Finish(Start(c))&&captures==0,"no P0.7 authorization means no worker starts");
        });
        Run("cancel",(c,p)=>{
            var exit=new TaskCompletionSource<string>();int calls=0;Configure(c,(xml,ct)=>{Interlocked.Increment(ref calls);return exit.Task;});
            var pending=Start(c);while(Volatile.Read(ref calls)==0)Pump();
            Check((bool)Call(c,"RequestHardwareStop"),"stop request sent");Check((bool)Get(c,"HasActiveHardwareSession"),"cancellation alone cannot assert worker released");
            exit.SetResult(p);Check(!Finish(pending),"late canceled worker cannot publish data");Check(((double[])Field(c,"lastDischargePc")).Length==0,"canceled capture not installed");
        });
        Run("duplicate",(c,p)=>{
            var exit=new TaskCompletionSource<string>();string request=null;Configure(c,(xml,ct)=>{request=xml;return exit.Task;});var pending=Start(c);while(request==null)Pump();
            var duplicate=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",(long)Get(c,"ActionSequence"));Check(!Finish(duplicate),"duplicate execution rejected");
            Check((bool)Get(c,"IsBusy")&&(bool)Get(c,"ActionPending"),"duplicate cannot acknowledge active action");exit.SetResult(Capture(p,request));Check(Finish(pending),"original action remains valid");
        });
        Run("ack-owner",(c,p)=>{
            int captures=0,ackWindows=0;long ownerSequence=0;
            Configure(c,(xml,ct)=>{captures++;return Task.FromResult(Capture(p,xml));});
            Field(c,"ZeroBeforeActionAcknowledgementForTests",new Action(()=>{
                ackWindows++;ownerSequence=(long)Get(c,"ActionSequence");
                Check((bool)Get(c,"HasActiveHardwareSession")&&(bool)Get(c,"ActionPending"),"owner lease remains held after worker exit and before ACK");
                string error=(string)Get(c,"LastActionError");
                for(int i=0;i<3;i++){
                    var duplicate=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",ownerSequence);
                    Check(duplicate.IsCompleted&&!duplicate.GetAwaiter().GetResult(),"narrow-window duplicate is bounded and rejected without waiting for owner");
                    Check((bool)Get(c,"ActionPending")&&(bool)Get(c,"HasActiveHardwareSession")&&(string)Get(c,"LastActionError")==error,"duplicate cannot mutate owner mailbox/result during ACK window");
                }
            }));
            Check(Finish(Start(c))&&(bool)Get(c,"LastActionSucceeded"),"only original owner publishes successful ACK");
            Check(captures==1&&ackWindows==1&&!(bool)Get(c,"HasActiveHardwareSession"),"one worker and one finalization; lease released after ACK");
            var late=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",ownerSequence);
            Check(late.IsCompleted&&!late.GetAwaiter().GetResult()&&captures==1&&(bool)Get(c,"LastActionSucceeded"),"late repeat cannot reclaim completed sequence or overwrite success");
            Field(c,"ZeroBeforeActionAcknowledgementForTests",null);
            Check(Finish(Start(c))&&captures==2,"new distinct sequence can acquire the released lease");
        });
        Run("ack-invalid",(c,p)=>{
            int captures=0,ackWindows=0;Configure(c,(xml,ct)=>{captures++;return Task.FromResult(p);});Call(c,"ClearDischargeCalibration");
            string diagnostic="";Set(c,"PressureDiagnosticSink",new Action<string>(text=>diagnostic+=text));
            Field(c,"ZeroBeforeActionAcknowledgementForTests",new Action(()=>{
                ackWindows++;var duplicate=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",(long)Get(c,"ActionSequence"));
                Check(duplicate.IsCompleted&&!duplicate.GetAwaiter().GetResult()&&(bool)Get(c,"ActionPending"),"invalid first request still has unique owner; duplicate cannot steal failure ACK");
            }));
            Check(!Finish(Start(c))&&captures==0&&ackWindows==1,"invalid first request fails once without worker");
            Check(!(bool)Get(c,"ActionPending")&&!(bool)Get(c,"IsBusy")&&!String.IsNullOrEmpty((string)Get(c,"LastActionError"))&&diagnostic.Contains("calibration"),"validation failure has short customer feedback and full diagnostic detail, not a swallowed error");
        });
        Run("ack-cancel",(c,p)=>{
            var exit=new TaskCompletionSource<string>();int captures=0,ackWindows=0;Configure(c,(xml,ct)=>{Interlocked.Increment(ref captures);return exit.Task;});
            Field(c,"ZeroBeforeActionAcknowledgementForTests",new Action(()=>{
                ackWindows++;var duplicate=(Task<bool>)Call(c,"ExecuteZeroVoltageHardwareActionAsync",(long)Get(c,"ActionSequence"));
                Check(duplicate.IsCompleted&&!duplicate.GetAwaiter().GetResult()&&(bool)Get(c,"ActionPending"),"canceled owner's pending failure cannot be acknowledged by duplicate");
            }));
            var pending=Start(c);while(Volatile.Read(ref captures)==0)Pump();Call(c,"RequestHardwareStop");exit.SetResult(p);
            Check(!Finish(pending)&&ackWindows==1&&!(bool)Get(c,"ActionPending")&&!String.IsNullOrEmpty((string)Get(c,"LastActionError")),"canceled request publishes one truthful failure after worker exit");
        });
        Run("context",(c,p)=>{
            var exit=new TaskCompletionSource<string>();string request=null;Configure(c,(xml,ct)=>{request=xml;return exit.Task;});var pending=Start(c);while(request==null)Pump();
            Call(c,"SetSelectedPhase","B");Check((string)Get(c,"SelectedPhaseCode")=="A","public phase switch is refused during current owned round");
            // Simulate an illegal external context change to retain the stale
            // worker-result protection independently of the new parameter lock.
            Field(c,"selectedPhaseCode","B");exit.SetResult(Capture(p,request));Check(!Finish(pending),"phase switch invalidates late results");Check(((double[])Field(c,"lastDischargePc")).Length==0,"old phase waveform not installed");
        });
        Run("project-change",(c,p)=>{
            var exit=new TaskCompletionSource<string>();string request=null;CancellationToken observed=default(CancellationToken);
            Configure(c,(xml,ct)=>{request=xml;observed=ct;return exit.Task;});var pending=Start(c);while(request==null)Pump();
            string next=Path.Combine(qa,"new-project");Directory.CreateDirectory(next);Set(c,"ProjectPath",next);Check((string)Get(c,"ProjectPath")==p,"public project switch is refused during current owned round");
            Field(c,"noiseProjectPath",next);Pump(250); // Illegal context seam, not an allowed UI change.
            Check(observed.IsCancellationRequested,"project change requests worker cancellation");
            exit.SetResult(Capture(p,request));Check(!Finish(pending),"late old-project result refused");Check(((double[])Field(c,"lastDischargePc")).Length==0,"new project display not contaminated");
        });
        Run("dispose",(c,p)=>{
            var exit=new TaskCompletionSource<string>();int calls=0;CancellationToken observed=default(CancellationToken);
            Configure(c,(xml,ct)=>{observed=ct;Interlocked.Increment(ref calls);return exit.Task;});var pending=Start(c);while(Volatile.Read(ref calls)==0)Pump();
            c.Dispose();Check(observed.IsCancellationRequested&&(bool)Get(c,"HasActiveHardwareSession"),"dispose requests stop but keeps release latch until worker exits");
            exit.SetResult(p);Check(!Finish(pending)&&!(bool)Get(c,"HasActiveHardwareSession"),"disposed late callback finishes without UI publication");
        });
        foreach(string kind in new[]{"old","future","changed-settings","outside","trailing"})
            Run(kind,(c,p)=>{
                Configure(c,(xml,ct)=>{
                    string captureProject=kind=="outside"?qa:p;
                    string path=Capture(captureProject,xml,16000,cap=>{
                        if(kind=="old")Field(cap,"CapturedUtc",DateTime.UtcNow.AddDays(-1));
                        if(kind=="future")Field(cap,"CapturedUtc",DateTime.UtcNow.AddMinutes(1));
                        if(kind=="changed-settings")Field(Field(cap,"Settings"),"PicoSerial","OTHER-FIXTURE");
                        if(kind=="changed-settings")Field(cap,"PicoSerial","OTHER-FIXTURE");
                    });
                    if(kind=="trailing")using(var f=new FileStream(Path.Combine(path,"capture.bin"),FileMode.Append))f.WriteByte(1);
                    return Task.FromResult(path);
                });
                Check(!Finish(Start(c)),kind+" capture refused");Check(((double[])Field(c,"lastDischargePc")).Length==0,kind+" no waveform publication");
            });
        Run("nonzero",(c,p)=>{
            int calls=0;Configure(c,(xml,ct)=>{calls++;throw new Exception("worker must not run");});
            Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{1d});Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Pump();
            Check(!Finish(Start(c)),"nonzero rejected by zero-only consumer");Check(calls==0,"nonzero never starts input-only worker");
        });
        Run("invalid-config",(c,p)=>{
            var s=Settings();Field(s,"PicoSerial","");bool rejected=false;
            try{Configure(c,(xml,ct)=>Task.FromResult(p),s);}catch(TargetInvocationException){rejected=true;}
            Check(rejected,"missing exact serial refused");Check(!(bool)Get(c,"ActionHandshakeEnabled"),"invalid config leaves simulation-compatible handshake unchanged");
        });
        Run("missing-calibration",(c,p)=>{
            int calls=0;Configure(c,(xml,ct)=>{calls++;return Task.FromResult(p);});Call(c,"ClearDischargeCalibration");
            Check(!Finish(Start(c))&&calls==0,"uncalibrated physical charge conversion cannot silently use template coefficient");
        });
        Run("simulation",(c,p)=>{
            Check(!(bool)Get(c,"ActionHandshakeEnabled"),"unconfigured legacy simulation contract unchanged");Set(c,"SimulationMode",true);
            Call(c,"SetWaveforms",new double[]{0,1,0},new double[]{1,2,3},1000d);Check(((double[])Field(c,"lastDischargePc")).Length==3,"simulation waveform API still works without hardware backend");
        });
        Run("full-rate-display",(c,p)=>{
            var settings=Settings();Field(settings,"CaptureDurationMs",60d);
            Configure(c,(xml,ct)=>Task.FromResult(Capture(p,xml,16000,cap=>{
                var adc=(short[])Field(cap,"PdAdc");for(int i=0;i<adc.Length;i++)adc[i]=(short)((i*7919L%24000)-12000);
            })),settings);
            int ticks=0;long longest=0,previous=0;var total=Stopwatch.StartNew();
            using(var timer=new System.Windows.Forms.Timer{Interval=10}){
                timer.Tick+=(s,e)=>{ticks++;long now=total.ElapsedMilliseconds;if(previous>0)longest=Math.Max(longest,now-previous);previous=now;};timer.Start();
                Check(Finish(Start(c)),"full 60 ms / 125 MS/s capture prepares successfully");timer.Stop();
            }
            Check(ticks>=5,"UI heartbeat remains active during full-rate file/statistics preparation");
            var view=Field(c,"waveformView");Check(((double[])Field(view,"discharge")).Length==7500000,"display retains all source samples, no extraction resampling");
            var prepare=view.GetType().GetMethod("PrepareData",BindingFlags.Static|BindingFlags.NonPublic);
            var prepared=prepare.Invoke(null,new object[]{new[]{0d,1d,0d},80000d,new[]{4d,1d,9d,2d},125000000d,CancellationToken.None});
            var install=Stopwatch.StartNew();Call(view,"InstallPreparedData",prepared);install.Stop();
            Check(install.ElapsedMilliseconds<250,"prepared installation has no full-array UI calculation");
            Check((double)Field(view,"cachedPdBaseline")==3d&&(double)Field(view,"cachedPdMaximum")==6d,"prepared median/maximum keep existing drawing semantics");
            Console.WriteLine("FULL_RATE_MS="+total.ElapsedMilliseconds+";UI_TICKS="+ticks+";MAX_TICK_GAP_MS="+longest+";INSTALL_MS="+install.ElapsedMilliseconds);
        });
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            string root=Path.GetFullPath(args[0]);qa=Path.Combine(Path.GetFullPath(args[1]),"z-"+Guid.NewGuid().ToString("N").Substring(0,12));Directory.CreateDirectory(qa);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string p=Path.Combine(root,"modules","PressureTest",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
            module=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll"));controlType=module.GetType("HVX.Controls.PressureTestControl",true);
            browserType=Assembly.LoadFrom(Path.Combine(root,"modules","DataBrowser","Xiongxin.OWTS.DataBrowser.dll")).GetType("HVX.Controls.BrowserReader",true);
            settingsType=module.GetType("Xiongxin.ZeroBench.BenchSettings",true);captureType=module.GetType("Xiongxin.ZeroBench.BenchCapture",true);
            Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);Tests();
            Console.WriteLine("PASS "+checks+" | No hardware invoked | "+qa);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
