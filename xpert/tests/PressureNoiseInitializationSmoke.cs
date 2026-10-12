using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;

// Independent managed Main, synthetic input files and delegates only. Neither
// a product entry point, actual worker, native driver nor AO/DO is invoked.
internal static class PressureNoiseInitializationSmoke
{
    static Assembly assembly;
    static Type controlType,settingsType,captureType;
    static string root;
    static int checks,sources,requests;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static object Field(object o,string name){return o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(o);}
    static void Field(object o,string name,object value){o.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(o,value);}
    static object Get(object o,string name){return o.GetType().GetProperty(name).GetValue(o,null);}
    static void Set(object o,string name,object value){o.GetType().GetProperty(name).SetValue(o,value,null);}
    static object Call(object o,string name,params object[] values){return o.GetType().GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(o,values);}
    static void Pump(int ms=5){var clock=Stopwatch.StartNew();do{Application.DoEvents();Thread.Sleep(1);}while(clock.ElapsedMilliseconds<ms);}
    static bool Finish(Task<bool> task){var clock=Stopwatch.StartNew();while(!task.IsCompleted&&clock.ElapsedMilliseconds<10000)Pump();Check(task.IsCompleted,"independent fake-input task completed within 10 s");return task.GetAwaiter().GetResult();}
    static Exception Fails(Task<bool> task){try{Finish(task);}catch(Exception e){return e;}throw new Exception("expected failure");}
    static object Settings(string xml=null){
        if(xml!=null)using(var reader=new StringReader(xml))return new XmlSerializer(settingsType).Deserialize(reader);
        var s=Activator.CreateInstance(settingsType);Field(s,"RealHardware",true);Field(s,"ArtSerial","OFFLINE-ART-FIXTURE");Field(s,"PicoSerial","OFFLINE-PICO-FIXTURE");
        Field(s,"RequestedPicoRateHz",125000d);Field(s,"PicoCoupling",0);Field(s,"CaptureDurationMs",20d);Field(s,"RangeIndex",5);Field(s,"PdCalibrationPcPerV",1000d);
        Field(s,"AutomaticSharedTrigger",false);Field(s,"AoStepV",.037d);return s;
    }
    static string Xml(object settings){using(var writer=new StringWriter()){new XmlSerializer(settingsType).Serialize(writer,settings);return writer.ToString();}}
    static string Capture(string project,string xml,int peak=300,Action<object> alter=null){
        var settings=Settings(xml);var capture=Activator.CreateInstance(captureType);Field(capture,"Settings",settings);
        Field(capture,"VoltageRaw",Enumerable.Repeat(.001,(int)Get(settings,"ExpectedArtCount")).ToArray());
        var adc=new short[(int)Get(settings,"RequestedPicoCount")];
        double fullScale=new[]{.01,.02,.05,.1,.2,.5,1d,2,5,10,20}[(int)Field(settings,"RangeIndex")];
        // Same physical input across probe/target ranges. The fixture's peak
        // argument is expressed as an ADC code at +/-0.5 V, not at each request.
        double scale=.5/fullScale;
        for(int i=0;i<adc.Length;i++)adc[i]=(short)Math.Max(-32767,Math.Min(32767,(int)Math.Round((i%32-16)*4*scale)));
        // Deliberately place the peak near the end: use the whole 20 ms record.
        adc[adc.Length-200]=(short)Math.Max(-32767,Math.Min(32767,(int)Math.Round(peak*scale)));Field(capture,"PdAdc",adc);
        Field(capture,"PicoRateHz",(double)Field(settings,"RequestedPicoRateHz"));Field(capture,"PicoFullScaleV",fullScale);
        var configuration=assembly.GetType("Xiongxin.ZeroBench.PicoCaptureConfiguration",true);
        Field(capture,"HardwareBits",configuration.GetMethod("HardwareBits",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new[]{Field(settings,"ResolutionBits"),Field(settings,"RangeIndex")}));
        Field(capture,"ArtSerial",Field(settings,"ArtSerial"));Field(capture,"PicoSerial",Field(settings,"PicoSerial"));Field(capture,"CapturedUtc",DateTime.UtcNow);
        Field(capture,"Runtime","OFFLINE SYNTHETIC INPUT — NOT A DEVICE ACQUISITION");if(alter!=null)alter(capture);
        var directory=Path.Combine(project,"Acquisition","PressureInitialization","r"+(++sources).ToString("000"));Call(capture,"Save",directory);return directory;
    }
    static Task<bool> Initialize(object control,Func<string,CancellationToken,Task<string>> input,object settings=null,CancellationToken token=default(CancellationToken))
    {return (Task<bool>)Call(control,"InitializePressureNoiseAsync",Xml(settings??Settings()),input,token);}
    static void Run(string name,Action<Control,string> action){
        var project=Path.Combine(root,name);Directory.CreateDirectory(project);
        using(var form=new Form{ClientSize=new Size(1280,760),StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000),ShowInTaskbar=false})
        using(var control=(Control)Activator.CreateInstance(controlType)){
            control.Dock=DockStyle.Fill;form.Controls.Add(control);Set(control,"ProjectPath",project);Set(control,"FrequencyReminderEnabled",false);Set(control,"MaximumPeakVoltageKv",30d);
            Call(control,"SetPressurePlan",new[]{0d,10d},new[]{1,3},new[]{0d,0d});Call(control,"SetPressureProgramMode","PLAN");
            Call(control,"SetDischargeCalibrationWithEvidence",1000d,"OFFLINE-COEFFICIENT",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(new byte[32]));
            Call(control,"SetSelectedRange","PICO:5");form.Show();Pump();action(control,project);
        }Pump();
    }
    static void Tests(){
        foreach(bool high in new[]{false,true})Run(high?"physical-high":"physical-low",(control,project)=>{
            Call(control,"ReportP04Status",true,high,DateTime.UtcNow);Pump();
            string emergency=(string)Get(control,"EmergencyStopStatusCode"),permission=(string)Get(control,"HighVoltageStatusCode");
            long sequence=(long)Get(control,"ActionSequence");int published=0,saved=0,analyzed=0;
            int planStep=(int)Get(control,"SelectedPlanStepIndex");bool dataSaved=(bool)Get(control,"DataSaved");
            controlType.GetEvent("ActionPublished").AddEventHandler(control,(EventHandler)((s,e)=>published++));
            controlType.GetEvent("SaveRequested").AddEventHandler(control,(EventHandler)((s,e)=>saved++));
            controlType.GetEvent("AnalysisRequested").AddEventHandler(control,(EventHandler)((s,e)=>analyzed++));
            var input=new TaskCompletionSource<string>();string request=null;CancellationToken owner=default(CancellationToken);
            int inputCount=0;var ranges=new System.Collections.Generic.List<int>();
            var task=Initialize(control,(xml,ct)=>{requests++;ranges.Add((int)Field(Settings(xml),"RangeIndex"));
                if(inputCount++==0){request=xml;owner=ct;return input.Task;}return Task.FromResult(Capture(project,xml));});
            Check((bool)Get(control,"IsPressureNoiseInitializing")&&!(bool)Get(control,"PressureNoiseInitializationReady"),"busy initialization blocks start");
            Check(((Control)Field(control,"pressureActionButton")).Text.Contains("初始化")&&!((Control)Field(control,"pressureActionButton")).Enabled,"initializing caption and disabled button");
            Check(!(bool)Call(control,"CanRequestVoltageNoLock",10d),"even known HIGH cannot start pressure during initialization");
            Check((bool)Get(control,"PressureParametersLocked"),"one-time parameter display lock while input is running");
            var requested=Settings(request);Check((double)Field(requested,"CaptureDurationMs")==20d&&!(bool)Field(requested,"AutomaticSharedTrigger"),"one exact 20 ms explicit no-output request");
            Check((int)Field(requested,"RangeIndex")==10,"first probe is actual maximum +/-20 V irrespective of historical selected CAL range");
            Check((double)Field(requested,"AoStepV")==.037d,"operator AO step is not changed");
            input.SetResult(Capture(project,request));Check(Finish(task),"actual synthetic input accepted on HIGH and LOW");
            Check(!owner.IsCancellationRequested&&!(bool)Get(control,"IsPressureNoiseInitializing")&&(bool)Get(control,"PressureNoiseInitializationReady"),"completion clears busy but does not cancel owner before completion");
            Check((int)Get(control,"SelectedPicoRangeCode")==3,"100 pC existing floor bounds initial recommendation");
            Check((long)Get(control,"ActionSequence")==sequence&&published==0&&saved==0&&analyzed==0&&!(bool)Get(control,"ActionPending"),"read-only initialization does not publish/ACK/start a pressure or diagnostic round");
            Check((int)Get(control,"SelectedPlanStepIndex")==planStep&&(bool)Get(control,"DataSaved")==dataSaved,"plan and save state unchanged");
            Check((string)Get(control,"EmergencyStopStatusCode")==emergency&&(string)Get(control,"HighVoltageStatusCode")==permission,"input does not rewrite physical level or output permission");
            Check(!(bool)Get(control,"CurrentAcquisitionIsNoiseReference")&&!(bool)Get(control,"CurrentAcquisitionIsZeroVoltageAnalysis"),"background initializer never installs noise display or diagnostic data");
            Check(((double[])Call(control,"GetNoiseReferenceSamples")).Length==0,"no hidden phase-specific reference is fabricated from initialization");
            Check(ranges.SequenceEqual(new[]{10})&&Directory.GetFiles(project,"capture.bin",SearchOption.AllDirectories).Length==1,"only one maximum probe; no target or intermediate recapture");
            var proof=Directory.GetFiles(project,"pressure-range-quality.xml",SearchOption.AllDirectories).Last();
            string evidence=File.ReadAllText(proof);Check(evidence.Contains("<LocationSampleCount>2500</LocationSampleCount>")&&evidence.Contains("<ExpectedCalibrationVolts>0</ExpectedCalibrationVolts>"),"whole 20 ms native ADC quality without fabricated injected calibration pulse");
            Console.WriteLine("INITIALIZATION_OFFLINE_STAGES "+Get(control,"PressureNoiseInitializationStages"));
        });
        Run("tail-peak",(control,project)=>{
            var task=Initialize(control,(xml,ct)=>{requests++;return Task.FromResult(Capture(project,xml,16000));});Check(Finish(task),"late peak input accepted");
            Check((int)Get(control,"SelectedPicoRangeCode")==5,"late native peak prevents incorrect range reduction");
        });
        Run("max-already-appropriate",(control,project)=>{
            int count=0;var task=Initialize(control,(xml,ct)=>{requests++;count++;return Task.FromResult(Capture(project,xml,700000));});
            Check(Finish(task)&&count==1&&(int)Get(control,"SelectedPicoRangeCode")==10,"clean maximum probe may itself confirm maximum; no duplicate capture");
        });
        Run("native-failure",(control,project)=>{
            int count=0;var task=Initialize(control,(xml,ct)=>{requests++;count++;throw new IOException("synthetic capture failure, not a range fault");});
            Check(Fails(task) is IOException&&count==1,"capture/cleanup errors are never retried as clipping");
            Check(!(bool)Get(control,"PressureNoiseInitializationReady"),"capture failure cannot enable HV");
        });
        foreach(bool fail in new[]{false,true})Run(fail?"handoff-failure":"handoff-pending",(control,project)=>{
            var handoff=new TaskCompletionSource<bool>();int transitions=0;
            Func<string,CancellationToken,Task<string>> input=(xml,ct)=>{requests++;return Task.FromResult(Capture(project,xml));};
            Func<CancellationToken,Task> complete=ct=>{transitions++;return handoff.Task;};
            var task=(Task<bool>)Call(control,"InitializePressureNoiseWithHandoffAsync",Xml(Settings()),input,complete,CancellationToken.None);
            var clock=Stopwatch.StartNew();while(transitions==0&&clock.ElapsedMilliseconds<5000)Pump();
            Check(transitions==1&&!task.IsCompleted,"real input-owner handoff is awaited exactly once");
            Check((bool)Get(control,"IsPressureNoiseInitializing")&&!(bool)Get(control,"PressureNoiseInitializationReady"),"pending owner cleanup keeps initialization unready");
            Call(control,"ReportP04Status",true,true,DateTime.UtcNow);Pump();
            Check(!(bool)Call(control,"CanRequestVoltageNoLock",10d)&&(int)Get(control,"SelectedPicoRangeCode")==5,"fresh HIGH cannot permit HV or commit range before cleanup");
            if(fail){handoff.SetException(new IOException("fake input owner cleanup failed"));
                Check(Fails(task) is IOException,"cleanup error is preserved");
                Check(!(bool)Get(control,"PressureNoiseInitializationReady")&&(int)Get(control,"SelectedPicoRangeCode")==5,"failed cleanup never enables pressure or installs initial range");
            }else {handoff.SetResult(true);Check(Finish(task),"successful explicit input owner handoff permits readiness commit");
                Check((bool)Get(control,"PressureNoiseInitializationReady")&&(int)Get(control,"SelectedPicoRangeCode")==3,"range commits only after successful cleanup");}
            Check(!(bool)Get(control,"ActionPending"),"handoff never automatically publishes a pressure request");
        });
        foreach(string failure in new[]{"clip","overflow","read-error","old-record","wrong-range"})Run(failure,(control,project)=>{
            int before=sources;var task=Initialize(control,(xml,ct)=>{requests++;
                if(failure=="read-error")throw new IOException("injected actual input failure");
                return Task.FromResult(Capture(project,xml,failure=="clip"?2000000:300,c=>{
                    if(failure=="overflow")Field(c,"Overflow",true);
                    if(failure=="old-record")Field(c,"CapturedUtc",DateTime.UtcNow.AddDays(-1));
                    if(failure=="wrong-range"){Field(Field(c,"Settings"),"RangeIndex",6);Field(c,"PicoFullScaleV",1d);}
                }));
            });
            Check(Fails(task)!=null,"explicit failed record is not accepted");
            Check(!(bool)Get(control,"PressureNoiseInitializationReady")&&!(bool)Get(control,"IsPressureNoiseInitializing"),"failed owner releases busy but does not permit start");
            Call(control,"ReportP04Status",true,true,DateTime.UtcNow);Pump();
            Check(!(bool)Call(control,"CanRequestVoltageNoLock",10d)&&!((Control)Field(control,"pressureActionButton")).Enabled,"fresh HIGH cannot bypass failed initialization");
            Check((int)Get(control,"SelectedPicoRangeCode")==5&&!(bool)Get(control,"ActionPending"),"failure neither changes range nor starts pressure");
            Check(sources-before==(failure=="read-error"?0:1),"failure never requests an infinite/redundant recapture");
            if(failure=="clip"||failure=="overflow")Check(Directory.GetFiles(project,"pressure-range-quality.xml",SearchOption.AllDirectories).Length==1,"unsafe quality and source retained together");
            Check(!String.IsNullOrWhiteSpace((string)Get(control,"PressureNoiseInitializationError")),"failure has a visible specific diagnostic");
        });
        foreach(string cancel in new[]{"token","hidden","phase","range"})Run("cancel-"+cancel,(control,project)=>{
            using(var cancellation=new CancellationTokenSource()){
                var input=new TaskCompletionSource<string>();string xml=null;CancellationToken owner=default(CancellationToken);
                var task=Initialize(control,(request,ct)=>{requests++;xml=request;owner=ct;return input.Task;},null,cancellation.Token);
                if(cancel=="token")cancellation.Cancel();else if(cancel=="hidden")control.Visible=false;
                else if(cancel=="phase"){
                    Call(control,"SetSelectedPhase","B");Check((string)Get(control,"SelectedPhaseCode")=="A","ordinary phase setter is locked during input");
                    Field(control,"selectedPhaseCode","B"); // simulate an external host context rotation
                }else {
                    Call(control,"SetSelectedRange","PICO:6");Check((int)Get(control,"SelectedPicoRangeCode")==5,"ordinary range setter is locked during input");
                    Field(control,"selectedRangeCode","PICO:6");
                }
                input.SetResult(Capture(project,xml));Check(Fails(task) is OperationCanceledException,"cancel/context rotation rejects old callback: "+cancel);
                if(cancel=="token"||cancel=="hidden")Check(owner.IsCancellationRequested,"page leave/cancel cancels exact input delegate");
                Check(!(bool)Get(control,"PressureNoiseInitializationReady")&&!(bool)Get(control,"IsPressureNoiseInitializing"),"old callback never marks ready: "+cancel);
                Check(!(bool)Get(control,"CurrentAcquisitionIsNoiseReference")&&!(bool)Get(control,"ActionPending"),"old callback cannot display data or publish action: "+cancel);
            }
        });
        Run("large-125MSps",(control,project)=>{
            var settings=Settings();Field(settings,"RequestedPicoRateHz",125000000d);
            object voltageBefore=Field(control,"lastVoltageKv"),pdBefore=Field(control,"lastDischargePc");
            var elapsed=Stopwatch.StartNew();var task=Initialize(control,(xml,ct)=>{requests++;return Task.FromResult(Capture(project,xml));},settings);
            Check(Finish(task),"full 125 MS/s × 20 ms synthetic input completes");
            var files=Directory.GetFiles(project,"capture.bin",SearchOption.AllDirectories);Check(files.Length==1,"only one large raw segment retained");
            var capture=captureType.GetMethod("Load").Invoke(null,new object[]{Path.GetDirectoryName(files[0])});
            Check(((short[])Field(capture,"PdAdc")).Length==2500000,"raw source retains all 2.5 million original ADC samples");
            Check(((double[])Call(control,"GetNoiseReferenceSamples")).Length==0,"large initialization does not retain display/noise arrays");
            Check(Object.ReferenceEquals(Field(control,"lastVoltageKv"),voltageBefore)&&Object.ReferenceEquals(Field(control,"lastDischargePc"),pdBefore),"large initialization does not replace displayed waveforms");
            Console.WriteLine("INITIALIZATION_125MSPS_OFFLINE totalWithSyntheticDiskMs="+elapsed.ElapsedMilliseconds+"; sourceSamples=2500000; sourceSegments=1; sourceMs=20; displayPrepared=False; "+Get(control,"PressureNoiseInitializationStages"));
        });
        ReuseTests();
        Policy();
    }
    static void ReuseTests(){
        Run("phase-reuse",(control,project)=>{
            int count=0;Func<string,CancellationToken,Task<string>> capture=(xml,ct)=>{requests++;count++;return Task.FromResult(Capture(project,xml));};
            Check(Finish(Initialize(control,capture)),"first range measured once");
            long action=(long)Get(control,"ActionSequence");
            foreach(string phase in new[]{"B","C","A"}){
                Call(control,"SetSelectedRange","PICO:8");Call(control,"SetSelectedPhase",phase);
                Call(control,"SetDischargeCalibrationWithEvidence",1000d,"CAL-"+phase,DateTimeOffset.UtcNow.AddMinutes(-1),phase,false,100d,Convert.ToBase64String(new byte[32]));
                var s=Settings();Field(s,"RangeIndex",Get(control,"SelectedPicoRangeCode"));
                Check(Finish(Initialize(control,capture,s)),"phase reuse finishes: "+phase);
                Check(count==1&&(int)Get(control,"SelectedPicoRangeCode")==3,"phase uses original initial range, not last selected range: "+phase);
                Check((string)Get(control,"PressureNoiseInitializationStages")!=""&&((string)Get(control,"PressureNoiseInitializationStages")).Contains("reusedInitialRange=True"),"explicit cache receipt: "+phase);
                Check((long)Get(control,"ActionSequence")==action&&!(bool)Get(control,"ActionPending"),"reuse never authorizes pressure: "+phase);
            }
            Check(((double[])Call(control,"GetNoiseReferenceSamples")).Length==0,"reuse does not relabel first phase noise");
            var changed=Settings();Field(changed,"RangeIndex",Get(control,"SelectedPicoRangeCode"));Field(changed,"PicoSerial","OFFLINE-OTHER-PICO");
            Check(Finish(Initialize(control,capture,changed))&&count==2,"new actual input device invalidates physical range cache");
        });
        foreach(string kind in new[]{"profile","calibration","project"})Run("invalidate-"+kind,(control,project)=>{
            int count=0;Func<string,CancellationToken,Task<string>> capture=(xml,ct)=>{requests++;count++;return Task.FromResult(Capture((string)Get(control,"ProjectPath"),xml));};
            Check(Finish(Initialize(control,capture)),"original context initialized");
            var s=Settings();Field(s,"RangeIndex",Get(control,"SelectedPicoRangeCode"));
            if(kind=="profile")Field(s,"RequestedPicoRateHz",250000d);
            if(kind=="calibration"){
                var hash=new byte[32];hash[0]=1;Call(control,"SetDischargeCalibrationWithEvidence",1000d,"NEW-CAL",DateTimeOffset.UtcNow.AddMinutes(-1),"A",false,100d,Convert.ToBase64String(hash));
            }
            if(kind=="project") {string next=Path.Combine(project,"other");Directory.CreateDirectory(next);Set(control,"ProjectPath",next);}
            Check(Finish(Initialize(control,capture,s))&&count==2,"context change forces exactly one fresh max probe: "+kind);
        });
        Run("reuse-handoff-failure",(control,project)=>{
            int count=0;Func<string,CancellationToken,Task<string>> capture=(xml,ct)=>{requests++;count++;return Task.FromResult(Capture(project,xml));};
            Check(Finish(Initialize(control,capture)),"base reuse range exists");
            var s=Settings();Field(s,"RangeIndex",Get(control,"SelectedPicoRangeCode"));
            Func<CancellationToken,Task> fail=ct=>Task.FromException(new IOException("failed actual owner release"));
            var task=(Task<bool>)Call(control,"InitializePressureNoiseWithHandoffAsync",Xml(s),capture,fail,CancellationToken.None);
            Check(Fails(task) is IOException&&count==1,"cache cannot skip actual owner handoff");
            Check(!(bool)Get(control,"PressureNoiseInitializationReady"),"failed reused owner keeps pressure unready");
            Check(Finish(Initialize(control,capture,s))&&count==2,"failed handoff invalidates cache rather than bypassing failed initialization");
        });
    }
    static void Policy(){
        var policy=assembly.GetType("HVX.Controls.PressureNoiseInitializationPolicy",true);var method=policy.GetMethod("StartingRange",BindingFlags.Static|BindingFlags.NonPublic);
        var qualityType=assembly.GetType("Xiongxin.ZeroBench.LocationRangeResult",true);
        for(int i=0;i<11;i++){
            var quality=Activator.CreateInstance(qualityType);Field(quality,"LocationSampleCount",5000);Field(quality,"FullSampleCount",5000);Field(quality,"RecommendedIndex",i);
            Check((int)method.Invoke(null,new[]{quality,(object)1000d})==Math.Max(3,i),"initial range directly selects recommended range bounded by 100 pC floor: "+i);
            foreach(string field in new[]{"DriverOverflow","DriverOverflowUncertain","RecommendationUncertain","MaximumRangeInsufficient"}){
                Field(quality,field,true);bool rejected=false;try{method.Invoke(null,new[]{quality,(object)1000d});}catch(TargetInvocationException e){rejected=e.InnerException.GetType().Name=="PressureNoiseInitializationException";}Check(rejected,"policy rejects "+field);Field(quality,field,false);
            }
        }
    }
    [STAThread] static int Main(string[] arguments){try{
        Check(arguments.Length==2,"UI DLL and unique synthetic directory provided");root=Path.GetFullPath(arguments[1]);Directory.CreateDirectory(root);
        string dll=Path.GetFullPath(arguments[0]);string folder=Path.GetDirectoryName(dll);
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{string path=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");return File.Exists(path)?Assembly.LoadFrom(path):null;};
        assembly=Assembly.LoadFrom(dll);controlType=assembly.GetType("HVX.Controls.PressureTestControl",true);settingsType=assembly.GetType("Xiongxin.ZeroBench.BenchSettings",true);captureType=assembly.GetType("Xiongxin.ZeroBench.BenchCapture",true);
        Tests();Check(Directory.GetFiles(root,"capture.bin",SearchOption.AllDirectories).Length==sources,"reported source count equals actual synthetic capture files");Console.WriteLine("PRESSURE_NOISE_INITIALIZATION_PASS="+checks+";InputRequests="+requests+";ActualSyntheticSources="+sources+";ProductStarted=False;SDKCalled=False;ActualWorkerStarted=False;HardwareAccessed=False;OutputTasksCreated=0;PhysicalAcceptance=False");return 0;
    }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
