using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class PressureFixtureDecisionSmoke
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly host;static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static object Call(object o,string n,params object[] args){Type t=o as Type??o.GetType();return t.GetMethods(F).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o is Type?null:o,args);}
    static object Field(object o,string n){return o.GetType().GetField(n,F).GetValue(o);}
    static void Field(object o,string n,object v){o.GetType().GetField(n,F).SetValue(o,v);}
    static object Get(object o,string n){return o.GetType().GetProperty(n,F).GetValue(o,null);}
    static void Set(object o,string n,object v){o.GetType().GetProperty(n,F).SetValue(o,v,null);}
    static object New(string n,params object[] args){return host.GetType("Xiongxin.OWTS.Desktop."+n,true).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==args.Length).Invoke(args);}
    static bool Reject(Action test){try{test();return false;}catch(TargetInvocationException e){return e.InnerException is InvalidDataException||e.InnerException is InvalidOperationException;}}
    static T Await<T>(Task<T> work){var start=DateTime.UtcNow;while(!work.IsCompleted&&(DateTime.UtcNow-start).TotalSeconds<20){Application.DoEvents();Thread.Sleep(2);}Check(work.IsCompleted,"bounded offline task");return work.GetAwaiter().GetResult();}
    static string Block(string phase,string start,string end){return "0\t"+phase+"\t\t\r\n1\t始端\t"+start+"\t0m\r\n2\t本体\tXLPE\t\r\n1\t终端\t"+end+"\t400m\r\n";}
    public sealed class FakePage:Control
    {
        public string LocationAcquisitionId{get;set;}
        public bool HasActiveHardwareSession{get;set;}
        public bool Interrupted;
        public int EmergencyCalls;
        public void ResetP04FeedbackSession(){}
        public void ReportP04Status(bool valid,bool high,DateTime utc){EmergencyCalls++;}
        public void InterruptPressureContinuation(){Interrupted=true;}
    }
    public sealed class FakeReview:Control
    {
        public double[,] Evidence=new double[,]{{1,2,1,11000,0}};
        public Task<double[,]> GetPressureTerminationEvidenceAsync(double[,] matrix,string saved){return Task.FromResult(Evidence);}
    }
    [STAThread]static int Main(string[] args){try{
        string build=Path.GetFullPath(args[0]),qa=Path.GetFullPath(args[1]);Directory.CreateDirectory(qa);
        host=Assembly.LoadFrom(Path.Combine(build,"Xiongxin.OWTS.Desktop.exe"));
        var policy=host.GetType("HVX.ProjectEvidence.PressureFixtureJournal",true);
        foreach(string eligible in new[]{"高压插拔头","6kV插拔头","10 kV separable connector","20kV plug-in"})Check((bool)Call(policy,"IsHighVoltageSeparable",eligible),"explicit HV category/rating: "+eligible);
        foreach(string unknown in new[]{"插拔头","separable connector","0.6kV插拔头","冷缩","30kV冷缩"})Check(!(bool)Call(policy,"IsHighVoltageSeparable",unknown),"no invented category/rating: "+unknown);
        string project=Path.Combine(qa,"project");Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project,"ITEM.in"),"电缆芯数: = \"三相单芯\"\r\n",Encoding.UTF8);
        File.WriteAllText(Path.Combine(project,"CABLE.in"),Block("A","高压插拔头","冷缩")+Block("B","冷缩","10kV插拔头")+Block("C","插拔头","冷缩"),Encoding.UTF8);
        var endpoints=new double[,]{{1,2,1,11000,0},{1,2,2,12000,1},{1,3,399,20000,2},{2,3,399,15000,3},{3,2,1,20000,4}};
        var evidence=(Array)Call(policy,"Select",project,endpoints);
        Check(evidence.Length==2,"largest event per eligible endpoint, no generic/other terminal");
        Check((double)Field(evidence.GetValue(0),"ChargePc")==12000,"strongest event source retained");
        Check(((Array)Call(policy,"Select",project,new double[0,5])).Length==0,"no attributed endpoint means no invented endpoint gate");
        foreach(double component in new[]{0d,1d,4d})Check(Reject(()=>Call(policy,"Select",project,new double[,]{{1,component,100,15000,0}})),"body/joint/ambiguous export rejected");
        Check(Reject(()=>Call(policy,"Select",project,new double[,]{{1,2,1,10000,0}})),"exact 10nC is not above threshold");
        string loc=Path.Combine(project,"A_13.40kV_1.loc");File.WriteAllBytes(loc,new byte[]{1,3,5,7});
        var record=Activator.CreateInstance(host.GetType("HVX.ProjectEvidence.PressureFixtureDecision",true));
        Field(record,"Trial","offline-trial");Field(record,"Acquisition","offline-round-1");Field(record,"SourceRecord",Path.GetFileName(loc));
        Field(record,"SourceSha256",Call(policy,"Hash",loc));Field(record,"MetadataSha256",Call(policy,"MetadataHash",project));Field(record,"Evidence",evidence);Field(record,"Choice","StopQueue");
        string eventPath=(string)Call(policy,"Save",project,record);Check(File.Exists(eventPath),"persist stop decision");
        string summary=(string)Call(policy,"Summary",project,true);Check(summary.Contains("选择停止")&&summary.Contains("offline-round-1")&&summary.Contains("不代表软件已验证"),"report factual decision, source round and no safety assertion");
        var report=Assembly.LoadFrom(Path.Combine(build,"modules","ReportGenerator","Xiongxin.OWTS.ReportGenerator.dll"));
        Check(((string)Call(report.GetType("HVX.ProjectEvidence.PressureFixtureJournal",true),"Summary",project,false)).Contains("stopped subsequent rounds"),"report module reads same journal contract");
        Field(record,"StartsHardware",true);Check(Reject(()=>Call(policy,"Save",project,record)),"never permit an operation log to assert hardware start");Field(record,"StartsHardware",false);
        File.AppendAllText(Path.Combine(project,"ITEM.in"),"changed\r\n");Check(Reject(()=>Call(policy,"Save",project,record)),"metadata change during confirmation rejects stale evidence");
        // A later metadata edit does not rewrite the historical decision.
        Check(((string)Call(policy,"Summary",project,true)).Contains("选择停止"),"historical metadata hash is snapshot not retroactive deletion");
        byte[] original=File.ReadAllBytes(loc);File.WriteAllBytes(loc,new byte[]{9});Check(Reject(()=>Call(policy,"Summary",project,true)),"changed source is not silently reported as verified history");File.WriteAllBytes(loc,original);
        Application.EnableVisualStyles();
        object state=New("ApplicationState"),app=New("AppSettings"),log=New("Diagnostics");
        Call(state,"TryLoadProject",project,null);
        using(var page=new FakePage{LocationAcquisitionId="round-2"})using(var review=new FakeReview())using(var moduleHost=(IDisposable)New("ModuleHost",app,state,log,Path.Combine(build,"modules"))){
            var handle=page.Handle;
            var trial=New("PressureAnalysisTrial",project);Call(trial,"Commit","round-2",loc);
            var session=New("FactoryPressureSession",page,typeof(object),"",app,project,trial,(Action)(()=>{}),log);
            try{
                ((IDictionary)Field(moduleHost,"factoryPressureSessions")).Add(page,session);
                string meta=(string)Call(policy,"MetadataHash",project),sourceHash=(string)Call(policy,"Hash",loc);
                Field(moduleHost,"PressureFixtureChoiceForTests",(Func<string,bool>)(prompt=>{Check(prompt.Contains("10 nC"),"operator sees actual threshold");return false;}));
                Check(!Await((Task<bool>)Call(moduleHost,"ConfirmPressureFixtureAsync",page,session,review,new double[0,36],loc,"round-2",meta,sourceHash))&&page.Interrupted,"decline stops continuation after persisting analyzed round");
                page.Interrupted=false;
                Field(moduleHost,"PressureFixtureChoiceForTests",(Func<string,bool>)(prompt=>true));
                Check(Await((Task<bool>)Call(moduleHost,"ConfirmPressureFixtureAsync",page,session,review,new double[0,36],loc,"round-2",meta,sourceHash))&&!page.Interrupted,"operator yes permits only subsequent software scheduling");
                Check(((string)Call(policy,"Summary",project,true)).Contains("确认已安装工装"),"accepted user statement persisted separately");
                page.HasActiveHardwareSession=true;bool blocked=false;try{Await((Task<bool>)Call(moduleHost,"ConfirmPressureFixtureAsync",page,session,review,new double[0,36],loc,"round-2",meta,sourceHash));}catch(InvalidOperationException){blocked=true;}Check(blocked,"no fixture dialog during active acquisition");page.HasActiveHardwareSession=false;
                Field(moduleHost,"PressureFixtureChoiceForTests",(Func<string,bool>)(prompt=>{File.WriteAllBytes(loc,new byte[]{8});return true;}));
                blocked=false;try{Await((Task<bool>)Call(moduleHost,"ConfirmPressureFixtureAsync",page,session,review,new double[0,36],loc,"round-2",meta,sourceHash));}catch(InvalidOperationException){blocked=true;}
                Check(blocked,"LOC changed during decision cannot be silently journaled as analyzed input");File.WriteAllBytes(loc,original);
                int notifications=0;Field(session,"ConnectionUnavailable",(Action)(()=>notifications++));var progress=(Action<string,string>)Field(session,"Progress");
                progress("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));Application.DoEvents();progress("EMERGENCY_UNKNOWN",DateTime.UtcNow.ToString("O"));Application.DoEvents();Check(notifications==0&&page.EmergencyCalls==2,"unknown DI denies permission without fabricating a transport disconnection");
                progress("EMERGENCY_ACTIVE",DateTime.UtcNow.ToString("O"));Application.DoEvents();Check(notifications==0,"pressed emergency stop does not invalidate connection");
                progress("EMERGENCY_RELEASED",DateTime.UtcNow.ToString("O"));Application.DoEvents();Check(notifications==0&&page.EmergencyCalls==4,"same monitor can report release without connection refresh");
                progress("PRESSURE_SESSION_UNAVAILABLE","synthetic terminal reader failure");Application.DoEvents();progress("PRESSURE_SESSION_UNAVAILABLE","same terminal failure");Application.DoEvents();Check(notifications==1,"terminal session failure invalidates connection exactly once");
                object actual=New("HardwareStatusSnapshot");foreach(string n in new[]{"HardwareEnabled","DriverInstalled","Connected","IdentityValid","FromLiveIntegrationProcess"})Set(actual,n,true);
                Set(actual,"StatusCode","Ready");Set(actual,"HubMac","001122334455");Set(actual,"ControllerSerial","0000000001");Set(actual,"AcquisitionSerial","QA000/0001");var utc=DateTime.UtcNow;Set(actual,"CheckedUtc",utc);Call(state,"SetHardware",actual);
                string identity=(string)Call(host.GetType("Xiongxin.OWTS.Desktop.DeviceSetupStatus",true),"ActualIdentity",actual);
                Call(moduleHost,"MarkPressureConnectionUnavailable",page,session,identity,utc.AddSeconds(-1));Check((bool)Get(state,"DeviceConnected"),"late old-generation failure cannot invalidate newer connection");
                Call(moduleHost,"MarkPressureConnectionUnavailable",page,session,identity,utc);Check(!(bool)Get(state,"DeviceConnected")&&(string)Get(Get(state,"Hardware"),"StatusCode")=="PresenceUnknown","current failure invalidates host ready cache without claiming external ownership");
                ((IDisposable)session).Dispose();int reports=page.EmergencyCalls;
                progress("EMERGENCY_ACTIVE",DateTime.UtcNow.ToString("O"));Application.DoEvents();Check(page.EmergencyCalls==reports,"disposed old session callback cannot update replacement state");
            }finally{((IDisposable)session).Dispose();}
        }
        var worker=Assembly.LoadFrom(Path.Combine(build,"modules","PressureRuntime","Xiongxin.Pressure.Runtime.exe"));var entry=worker.GetType("Xiongxin.FactoryTest.UnifiedWorkerEntry",true);
        Check((int)Call(entry,"Main",new object[]{new string[0]})==2,"worker no-args cannot open factory form");
        string workerDir=Path.Combine(qa,"worker"),settings=Path.Combine(workerDir,"factory-settings.xml");
        foreach(string command in new[]{"Discover","Calibration","FactoryPressure","Manual","SelfTest","--simulate","MonitorPressure"})Check(!(bool)Call(entry,"AcceptsArguments",new object[]{new[]{"--worker",command,settings,workerDir,"12345"}}),"non-integrated command rejected: "+command);
        Check((bool)Call(entry,"AcceptsArguments",new object[]{new[]{"--worker","Pressure",settings,workerDir,"12345"}}),"supervised pressure command syntax retained");
        // V1.0.30.9 watches the connection in its supervised resident session,
        // not through the removed MonitorPressure one-shot command. This only
        // checks argument shape; no resident process or hardware is started.
        string sessionSettings=Path.Combine(workerDir,"session-settings.xml");
        Check((bool)Call(entry,"AcceptsArguments",new object[]{new[]{"--device-session",sessionSettings,workerDir,"12345"}}),"supervised resident connection syntax retained");
        Check(!(bool)Call(entry,"AcceptsArguments",new object[]{new[]{"--device-session",settings,workerDir,"12345"}}),"resident entry rejects pressure-round settings");
        Check(!(bool)Call(entry,"AcceptsArguments",new object[]{new[]{"--device-session",sessionSettings,workerDir,"0"}}),"resident entry requires a supervisor");
        Check((int)Call(entry,"Main",new object[]{new[]{"--worker","Pressure",settings,workerDir,"2147483647"}})==2,"missing supervisor cannot enter hardware worker");
        Console.WriteLine("PRESSURE_FIXTURE_DECISION_PASS="+checks+"; HardwareAccessed=false; WorkersStarted=0");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
