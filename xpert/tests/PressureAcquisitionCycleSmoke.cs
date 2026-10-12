using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Data;

internal static class PressureAcquisitionCycleSmoke
{
    static int checks;static Type type;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Get(object c,string name){return type.GetProperty(name).GetValue(c,null);}
    static void Set(object c,string name,object value){type.GetProperty(name).SetValue(c,value,null);}
    static object Call(object c,string name,params object[] values){return type.GetMethods().Single(m=>m.Name==name&&m.GetParameters().Length==values.Length).Invoke(c,values);}
    static object Field(object c,string name){return type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c);}
    static void InjectField(object c,string name,object value){type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(c,value);}
    // Fault injection only: public context setters must refuse an unfinished
    // round. Simulate an out-of-contract host replacing that context anyway, so
    // the independent acquisition/save identity checks still get exercised.
    static void InjectInvalidProjectContext(Control c,string project)
    {
        InjectField(c,"noiseProjectPath",project);
        type.GetMethod("ResetLocationCycleAcquisition",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(c,null);
    }
    static void EndRound(Control c,bool saved)
    {
        Call(c,"SetPressureState",saved?"COMPLETE":"FAULT",0d);
        if((string)Get(c,"PostProcessStateCode")=="WAITING_FOR_ACQUISITION")Call(c,"ReportAcquisitionCompleted",false);
        if((string)Get(c,"PostProcessStateCode")=="SAVE_REQUESTED")
            Check((bool)Call(c,"CompleteSave",saved,saved?"":"Isolated fixture round failed; no save is claimed."),"round save outcome is acknowledged");
        if((bool)Get(c,"ActionPending"))
            Check((bool)Call(c,"CompleteAction",(long)Get(c,"ActionSequence"),saved,saved?"":"Isolated fixture round ended without a successful acquisition."),"round mailbox acknowledges its own sequence");
        Check(!(bool)Get(c,"PressureNavigationLocked")&&!(bool)Get(c,"ActionPending"),"independent next action waits for completed round and ACK");
    }
    static void RejectStale(Action action,string message){bool rejected=false;try{action();}catch(TargetInvocationException ex){rejected=ex.InnerException is InvalidOperationException;}Check(rejected,message);}
    static System.Collections.Generic.Dictionary<string,object> Metadata(string token,bool preview=false)
    {return new System.Collections.Generic.Dictionary<string,object>{{"locationAcquisitionId",token},{"referencePreview",preview}};}
    static string LocationPath(Control c,string project,string clock)
    {return Path.Combine(project,Get(c,"SelectedPhaseCode")+"_"+((double)Get(c,"RequestedTargetPeakVoltageKv")).ToString("0.00",CultureInfo.InvariantCulture)+"kV_2026-09-30-12-00-"+clock+".loc");}
    static Control Make(string root){var c=(Control)Activator.CreateInstance(type);Set(c,"FrequencyReminderEnabled",false);Set(c,"ProjectPath",root);Set(c,"MaximumPeakVoltageKv",50d);Call(c,"SetPressurePlan",new[]{1d},new[]{1},new[]{10d});Call(c,"SetPressureProgramMode","PLAN");var handle=c.Handle;Set(c,"ActionHandshakeEnabled",true);return c;}
    static bool Await(Task<bool> task){var watch=Stopwatch.StartNew();while(!task.IsCompleted){if(watch.ElapsedMilliseconds>30000)throw new TimeoutException("async LOC save");Application.DoEvents();Thread.Sleep(1);}return task.GetAwaiter().GetResult();}
    static Task<bool> StartSave(Control c,string id,string path,double[] data,double rate,CancellationToken token=default(CancellationToken))
    {return (Task<bool>)Call(c,"SaveLocationCycleDataAsync",id,path,data,rate,0d,170d,token);}
    static bool Save(Control c,string path,double[] data,double rate,string callbackId)
    {return Await(StartSave(c,callbackId,path,data,rate));}
    [STAThread] static int Main(string[] args){try{
        Application.EnableVisualStyles();Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        string root=Path.GetFullPath(args[0]),qa=Path.Combine(Path.GetFullPath(args[1]),"cycles-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(qa);
        type=Assembly.LoadFrom(Path.Combine(root,"modules","PressureTest","Xiongxin.OWTS.PressureTest.dll")).GetType("HVX.Controls.PressureTestControl",true);
        using(var c=Make(qa)){
            Check((string)Get(c,"HighVoltageStatusCode")=="UNKNOWN","real initial status unknown");
            long sequence=(long)Get(c,"PressureRequestSequence");Call(c,"RequestPressure");
            Check((long)Get(c,"PressureRequestSequence")==sequence,"unknown HV blocks direct API");
            Call(c,"ReportP04Status",true,false,DateTime.UtcNow);Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==sequence,"not-started blocks direct API");
            Thread.Sleep(20); // A later physical sample, not a future fabricated timestamp.
            Call(c,"ReportP04Status",true,true,DateTime.UtcNow);Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==sequence+1,"enabled allows request");
            Check(!(bool)Call(c,"ReportAcquisitionCompleted",false),"missing voltage rejected");
            Check((string)Get(c,"PostProcessStateCode")=="ACQUISITION_INVALID"&&!(bool)Get(c,"DataSaved"),"explicit invalid acquisition state");
            Check(((string)Get(c,"LastPostProcessError")).Contains("加压／采集异常"),"pressure abnormality explicitly shown");
            Check((bool)Get(c,"IsHighVoltageReady"),"bad data does not claim HV off");
            EndRound(c,false);
            Call(c,"ReportHighVoltageState",false,false);Check(!(bool)Get(c,"IsHighVoltageReady"),"disconnect becomes unknown");
            Call(c,"SetPressurePlan",new[]{0d},new[]{1},new[]{0d});Call(c,"RequestPressure");Check((long)Get(c,"PressureRequestSequence")==sequence+2,"zero allowed without HV");
        }
        using(var c=Make(qa)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");
            const double fs=20000;var voltage=Enumerable.Range(0,2000).Select(i=>10*Math.Cos(2*Math.PI*50*i/fs)).ToArray();
            var charge=Enumerable.Range(0,2000).Select(i=>(double)i).ToArray();
            var metadata=new System.Collections.Generic.Dictionary<string,object>{{"locationAcquisitionId",Get(c,"LocationAcquisitionId")}};
            Call(c,"SetAcquisitionWaveforms",voltage,charge,fs,metadata);Check((string)Get(c,"LocationCycleStatusCode")=="COMPLETE_CYCLES","new waveform validated");
            double target=(double)Get(c,"RequestedTargetPeakVoltageKv");string name="A_"+target.ToString("0.00",CultureInfo.InvariantCulture)+"kV_2026-09-30-12-00-00.loc";
            string path=Path.Combine(qa,name);
            string savedId=(string)metadata["locationAcquisitionId"];
            Check(Save(c,path,charge,fs,savedId),"explicit cycle save");
            var lines=File.ReadAllLines(path);var reference=LocationCycleReference.ParseHeader(lines,lines.Length-11,fs);
            Check(reference!=null&&lines.Length<2011,"saved record cropped and self-describing");
            Check(lines[9].Contains("\"simulation\":true"),"simulation source explicit");
            string algorithmPath=Path.Combine(root,"modules","DataAnalysis","fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll");
            Check(File.Exists(algorithmPath),"real location reader dependency present");
            var engine=Assembly.LoadFrom(algorithmPath).GetType("HVX.Task17.Location.LocationEngine",true);
            var parsed=engine.GetMethod("ReadLocation",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{path});
            Func<string,object> read=n=>parsed.GetType().GetField(n,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).GetValue(parsed);
            Check(Math.Abs((double)read("SampleRate")-fs)<1e-6,"real LOC reader preserves sample rate");
            Check((string)read("Phase")=="A"&&Math.Abs((double)read("PhaseDeg")-reference.InitialPhaseDegrees)<1e-8,"real LOC reader preserves phase identity and cycle start angle");
            var readSamples=(double[])read("Samples");int sourceStart=(int)Math.Round(reference.CaptureStartSeconds*fs);
            Check(readSamples.Length==reference.SampleCount&&readSamples.Select((v,i)=>Math.Abs(v-charge[sourceStart+i])<1e-9).All(v=>v),"real LOC reader roundtrip retains pC without second calibration multiplication");
            Check(read("CycleReference")!=null,"real algorithm accepts saved cycle reference");
            string content=File.ReadAllText(path);Check(!Save(c,path,charge,fs,savedId)&&File.ReadAllText(path)==content,"never overwrite existing LOC");
            Call(c,"ClearDisplayData");Check((string)Get(c,"LocationCycleStatusCode")=="COMPLETE_CYCLES","public clear cannot discard an unfinished round");
            EndRound(c,true);
            Call(c,"ClearDisplayData");Check((string)Get(c,"LocationCycleStatusCode")=="NOT_EVALUATED","clear invalidates stale cycle");
            Check(!Save(c,path+".loc",charge,fs,savedId),"cleared reference cannot save");
        }
        const double rate=20000;
        var testVoltage=Enumerable.Range(0,2000).Select(i=>10*Math.Cos(2*Math.PI*50*i/rate)).ToArray();
        var testCharge=Enumerable.Range(0,2000).Select(i=>i*.25).ToArray();
        string projectA=Path.Combine(qa,"project-A"),projectB=Path.Combine(qa,"project-B");Directory.CreateDirectory(projectA);Directory.CreateDirectory(projectB);
        using(var c=Make(projectA)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");string requestA=(string)Get(c,"LocationAcquisitionId");
            Check(requestA.Length>0,"request produces nonempty identity");
            Set(c,"ProjectPath",projectB);
            Check((string)Get(c,"ProjectPath")==projectA&&(string)Get(c,"LocationAcquisitionId")==requestA,"public project switch is refused during the current round");
            InjectInvalidProjectContext(c,projectB);
            var before=(double[])Field(c,"lastVoltageKv");
            RejectStale(()=>Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(requestA,true)),"project A callback rejected after selecting B");
            Check((string)Get(c,"ProjectPath")==projectB,"stale callback cannot restore old project");
            Check(!(bool)Get(c,"CurrentAcquisitionIsReferencePreview"),"rejected old callback cannot mutate preview state");
            Check(ReferenceEquals(before,Field(c,"lastVoltageKv"))&&((double[])Field(c,"lastVoltageKv")).Length==0,"rejected old callback cannot install waveforms");
            Check((string)Get(c,"LocationCycleStatusCode")=="NOT_EVALUATED","old project has no retained cycle");
            string rejectedPath=LocationPath(c,projectB,"01");
            Check(!Save(c,rejectedPath,testCharge,rate,requestA)&&!File.Exists(rejectedPath),"old A wave cannot be saved under project B");
            Check(!Directory.EnumerateFiles(projectB).Any(),"rejected switch callback leaves no temporary file");
            EndRound(c,false);
            Call(c,"RequestPressure");string requestB=(string)Get(c,"LocationAcquisitionId");Check(requestB.Length>0&&requestB!=requestA,"new project request has a distinct identity");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(requestB));
            Check(Save(c,rejectedPath,testCharge,rate,requestB),"current B acquisition may save B: "+Get(c,"LastError"));
            EndRound(c,true);
        }
        string repeatedProject=Path.Combine(qa,"repeated-request");Directory.CreateDirectory(repeatedProject);
        using(var c=Make(repeatedProject)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");string first=(string)Get(c,"LocationAcquisitionId");
            Call(c,"RequestPressure");Check((string)Get(c,"LocationAcquisitionId")==first,"a second request cannot replace the active round identity");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(first));
            Check(Save(c,LocationPath(c,repeatedProject,"10"),testCharge,rate,first),"first independent round has a real isolated saved record");
            EndRound(c,true);
            Call(c,"RequestPressure");string second=(string)Get(c,"LocationAcquisitionId");Check(first!=second,"consecutive requests rotate identity");
            RejectStale(()=>Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(first,true)),"first request cannot satisfy the second request");
            Check(!(bool)Get(c,"CurrentAcquisitionIsReferencePreview")&&(string)Get(c,"LocationCycleStatusCode")=="NOT_EVALUATED","obsolete token rejection is side-effect free");
            string path=LocationPath(c,repeatedProject,"02");
            Check(!Save(c,path,testCharge,rate,first)&&!File.Exists(path),"obsolete request cannot create LOC");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(second));var current=(double[])Field(c,"lastVoltageKv");
            RejectStale(()=>Call(c,"SetAcquisitionWaveforms",testVoltage.Select(x=>x*.2).ToArray(),testCharge,rate,Metadata(first,true)),"late first callback rejected after second waveform accepted");
            Check(ReferenceEquals(current,Field(c,"lastVoltageKv"))&&!(bool)Get(c,"CurrentAcquisitionIsReferencePreview"),"valid second waveform survives late first callback");
            string currentAction=(string)Get(c,"LastActionCode"),currentError=(string)Get(c,"LastError");
            Check(!Save(c,path,testCharge,rate,first)&&!File.Exists(path),"old PD buffer token rejected even after current voltage accepted");
            Check((string)Get(c,"LastActionCode")==currentAction&&(string)Get(c,"LastError")==currentError,"late PD save callback cannot replace current status");
            Check((string)Get(c,"LocationCycleStatusCode")=="COMPLETE_CYCLES"&&Save(c,path,testCharge,rate,second),"valid second evidence remains saveable after stale callback");
            Check(File.ReadAllLines(path)[9].Contains(second),"saved metadata identifies current request");
            EndRound(c,true);
        }
        string legacyProject=Path.Combine(qa,"legacy-preview");Directory.CreateDirectory(legacyProject);
        using(var c=Make(legacyProject)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");Call(c,"SetWaveforms",testVoltage,testCharge,rate);
            Check((string)Get(c,"LocationCycleStatusCode")=="COMPLETE_CYCLES","legacy preview can still assess complete cycles");
            string path=LocationPath(c,legacyProject,"03");
            Check(!Save(c,path,testCharge,rate,(string)Get(c,"LocationAcquisitionId"))&&!File.Exists(path),"unbound legacy preview cannot save a new LOC");
            Check(((string)Get(c,"LastError")).Contains("locationAcquisitionId"),"legacy save refusal explains the required identity");
        }
        string phaseProject=Path.Combine(qa,"phase-switch");Directory.CreateDirectory(phaseProject);
        using(var c=Make(phaseProject)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");string phaseA=(string)Get(c,"LocationAcquisitionId");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(phaseA));Call(c,"SetSelectedPhase","B");
            Check((string)Get(c,"SelectedPhaseCode")=="A","public phase switch is refused during acquisition");
            InjectField(c,"selectedPhaseCode","B");
            RejectStale(()=>Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(phaseA)),"A callback rejected after B phase selected");
            string path=LocationPath(c,phaseProject,"04");Check(!Save(c,path,testCharge,rate,phaseA)&&!File.Exists(path),"phase A cannot save into phase B");
            EndRound(c,false);
            Call(c,"RequestPressure");string phaseB=(string)Get(c,"LocationAcquisitionId");Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(phaseB));
            Check(Save(c,path,testCharge,rate,phaseB),"new B phase capture can save B");
            Set(c,"SimulationMode",false);Check((bool)Get(c,"SimulationMode"),"public provenance change is refused before round completion");
            EndRound(c,true);
            Set(c,"SimulationMode",false);Check(!Save(c,LocationPath(c,phaseProject,"05"),testCharge,rate,phaseB),"simulation change cannot relabel saved capture real");
        }
        string asyncProject=Path.Combine(qa,"async"),switchedProject=Path.Combine(qa,"async-next");Directory.CreateDirectory(asyncProject);Directory.CreateDirectory(switchedProject);
        using(var c=Make(asyncProject))using(var timer=new System.Windows.Forms.Timer()){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");string id=(string)Get(c,"LocationAcquisitionId");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(id));
            var highRate=new double[4000000];for(int i=0;i<highRate.Length;i++)highRate[i]=1d/(i+1);
            int ticks=0;timer.Interval=15;timer.Tick+=(s,e)=>ticks++;timer.Start();
            string path=LocationPath(c,asyncProject,"06");var watch=Stopwatch.StartNew();var task=StartSave(c,id,path,highRate,125000000);
            long returnMs=watch.ElapsedMilliseconds;Check(Await(task),"async 2.5M sample save succeeds");watch.Stop();
            Check(returnMs<250&&ticks>0,"large save returns promptly and UI heartbeat continues");
            Check(highRate[125000]==1d/125001,"async save leaves raw buffer unchanged");
            Console.WriteLine("ASYNC_SAVE_MS="+watch.ElapsedMilliseconds+";RETURN_MS="+returnMs+";UI_TICKS="+ticks);
            string cancelPath=LocationPath(c,asyncProject,"07");using(var cancel=new CancellationTokenSource()){
                var pending=StartSave(c,id,cancelPath,highRate,125000000,cancel.Token);
                while(!pending.IsCompleted&&!Directory.EnumerateFiles(asyncProject,".loc-*.tmp").Any()){Application.DoEvents();Thread.Sleep(1);}
                Check(!pending.IsCompleted,"cancel exercised while temporary save in progress");cancel.Cancel();
                Check(!Await(pending)&&!File.Exists(cancelPath),"cancelled save does not commit");
                Check(!Directory.EnumerateFiles(asyncProject,".loc-*.tmp").Any(),"cancel removes own temporary file");
            }
            string changedPath=LocationPath(c,asyncProject,"08");var changed=StartSave(c,id,changedPath,highRate,125000000);
            Set(c,"ProjectPath",switchedProject);
            Check((string)Get(c,"ProjectPath")==asyncProject&&(string)Get(c,"LocationAcquisitionId")==id,"public project switch cannot interrupt the pending cycle save");
            InjectInvalidProjectContext(c,switchedProject);
            Check(!Await(changed)&&!File.Exists(changedPath),"project change while worker writes forbids final commit");
            Check(!Directory.EnumerateFiles(asyncProject,".loc-*.tmp").Any()&&!Directory.EnumerateFiles(switchedProject).Any(),"project change cleans temporary and never writes into new project");
            Check((string)Get(c,"LocationCycleStatusCode")=="NOT_EVALUATED","old worker does not restore current project state");
        }
        string changedCyclesProject=Path.Combine(qa,"changed-cycles");Directory.CreateDirectory(changedCyclesProject);
        using(var c=Make(changedCyclesProject)){
            Set(c,"SimulationMode",true);Call(c,"RequestPressure");string id=(string)Get(c,"LocationAcquisitionId");
            Call(c,"SetAcquisitionWaveforms",testVoltage,testCharge,rate,Metadata(id));Call(c,"ReportAcquisitionCompleted",false);
            var data=new double[4000000];string path=LocationPath(c,changedCyclesProject,"09");var pending=StartSave(c,id,path,data,125000000);
            object originalWindow=Field(c,"locationCycleWindow");
            RejectStale(()=>Set(c,"LocationSaveCycleCount",2),"public saved-cycle count is locked during pending save");
            Check((int)Get(c,"LocationSaveCycleCount")==1&&ReferenceEquals(originalWindow,Field(c,"locationCycleWindow")),"rejected public cycle-count edit preserves the pending window");
            // Corrupt only the private original-window identity, as an illegal
            // external context replacement would; do not cancel the save task.
            InjectField(c,"locationSaveCycleCount",2);InjectField(c,"locationCycleWindow",null);InjectField(c,"cycleWaveformSeen",false);
            Check(!Await(pending)&&!File.Exists(path),"changing N invalidates the pending original cycle window");
            Check(!Directory.EnumerateFiles(changedCyclesProject,".loc-*.tmp").Any(),"changed-N save leaves no temporary data");
        }
        Console.WriteLine("PRESSURE_ACQUISITION_CYCLE_PASS="+checks);Console.WriteLine("QA="+qa);return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
