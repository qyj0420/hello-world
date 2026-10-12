// Offline only: compile with HardwareStatusAdapter.cs and Integration.cs.
// All SDK/connector types below are fail-closed fakes; no hardware is accessed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xiongxin.HvDacXpert;
using Xiongxin.OWTS.Desktop;

static class HardwareDisconnectSmoke
{
    const string Mac = "001122334455";
    static int passed;
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; }
    static string State(XDocument doc) { return (string)doc.Root.Attribute("State"); }
    static Server Hub()
    {
        var hub = new Server { Mac = Mac, Ip = "192.0.2.1", RawIp = 1 };
        hub.Devices.Add(new Usb { Server = hub, Vid = 0x3063, Pid = 0x3135, Location = "1-1", Port = "CTRL" });
        hub.Devices.Add(new Usb { Server = hub, Vid = 0x0CE9, Pid = 0x1218, Location = "1-2", Port = "PICO" });
        hub.Devices.Add(new Usb { Server = hub, Vid = 0x0CE9, Pid = 0x1218, Location = "1-3", Port = "OTHER" });
        return hub;
    }
    static XDocument Run(Server hub, Dictionary<string, uint> states, List<string> calls, int code = 2,
        bool change = true, bool enabled = true, Action<int> delay = null)
    {
        return Integration.Disconnect(enabled, Mac, "1-1", "1-2", ignored => new List<Server> { hub, new Server { Mac="FFEEDDCCBBAA",Ip="192.0.2.2" } },
            (ip, port) => { Check(ip == hub.RawIp, "only selected hub inspected"); return states[port]; },
            target => { calls.Add(target.Port); if (change) foreach (string port in target.Port.Split(',')) states[port] = 1; return code; }, delay ?? (ignored => { }));
    }
    static Dictionary<string,uint> Ports(uint first=2, uint second=2)
    { return new Dictionary<string,uint> { {"CTRL",first},{"PICO",second},{"OTHER",3} }; }
    static void ConnectorTests()
    {
        var calls = new List<string>(); var states = Ports(); var hub=Hub();
        XDocument result=Run(hub,states,calls);
        Check(State(result)=="Disconnected", "verified selected targets released");
        Check(calls.SequenceEqual(new[]{"CTRL","PICO"}), "only configured target command scope");
        Check(states["OTHER"]==3, "unselected other-PC device untouched");
        Check(result.Root.Elements("Module").Count()==2 && result.Root.Elements("Module").All(e=>(int)e.Attribute("ConnectionState")==1), "explicit verified states");
        calls.Clear(); Check(State(Run(Hub(),Ports(1,1),calls))=="Disconnected"&&calls.Count==0,"already available no command");
        foreach(uint invalid in new uint[]{0,3,4,UInt32.MaxValue})
        { calls.Clear(); Check(State(Run(Hub(),Ports(2,invalid),calls))=="DisconnectFailed"&&calls.Count==0,"preflight rejects state "+invalid); }
        calls.Clear(); Check(State(Run(Hub(),Ports(),calls,0))=="DisconnectFailed"&&calls.Count==1,"exit zero not assumed disconnect success");
        foreach(int code in new[]{2,5,11})
        { calls.Clear(); Check(State(Run(Hub(),Ports(),calls,code))=="Disconnected","known release exit verified "+code); }
        calls.Clear(); int delays=0;
        Check(State(Run(Hub(),Ports(),calls,2,false,true,ignored=>delays++))=="DisconnectFailed"&&delays==20,"successful command without state change times out");
        calls.Clear(); states=Ports();
        result=Integration.Disconnect(true,Mac,"1-1","1-2",ignored=>new List<Server>{Hub()},(ip,p)=>states[p],target=>{
            calls.Add(target.Port); states[target.Port]=1; if(target.Port=="CTRL")states["PICO"]=3;return 2;},ignored=>{});
        Check(State(result)=="DisconnectFailed"&&calls.SequenceEqual(new[]{"CTRL"}),"ownership rechecked immediately before each release");
        calls.Clear(); hub=Hub(); hub.Devices[1].Location="1-1";
        Check(State(Run(hub,Ports(),calls))=="DisconnectFailed"&&calls.Count==0,"ambiguous identity rejected");
        calls.Clear(); hub=Hub(); hub.Devices[1].Vid=0xFFFF;
        Check(State(Run(hub,Ports(),calls))=="DisconnectFailed"&&calls.Count==0,"wrong VID rejected");
        calls.Clear(); hub=Hub(); hub.Devices[1].Port="CTRL";
        Check(State(Run(hub,Ports(),calls))=="DisconnectFailed"&&calls.Count==0,"overlap rejected");
        calls.Clear(); hub=Hub(); hub.Devices[0].Port="CTRL /D";
        Check(State(Run(hub,Ports(),calls))=="DisconnectFailed"&&calls.Count==0,"unsafe arguments rejected");
        Check(State(Integration.Disconnect(false,Mac,"1-1","1-2"))=="DisconnectFailed","disabled never scans");
        Check(State(Integration.Disconnect(true,"bad","1-1","1-2"))=="DisconnectFailed","bad MAC never scans");
        Check(State(Integration.Disconnect(true,Mac,"1-1","1-1"))=="DisconnectFailed","identical locations never scan");
        Check(State(Integration.Disconnect(true,Mac,"1-1","1-2",ignored=>new List<Server>()))=="DisconnectFailed","missing hub not false success");
        Check(State(Integration.Disconnect(true,Mac,"1-1","1-2",ignored=>{throw new UnauthorizedAccessException("sdk denied");}))=="DisconnectFailed","SDK failure explicit");
        hub=Hub(); hub.Devices[1].Port="PICO,PICO2";states=Ports();states["PICO2"]=3;calls.Clear();
        Check(State(Run(hub,states,calls))=="DisconnectFailed"&&calls.Count==0,"all composite ports preflight");
    }
    static void ConnectionScopeTests()
    {
        var hub=Hub(); var calls=new List<string>(); var states=Ports(1,1);
        var selected=Integration.SelectConfiguredModules(hub,"1-1","1-2");
        Check(selected.Count==2&&selected.All(u=>u.Port!="OTHER"),"connection selection excludes third same-model module");
        Integration.ConnectConfiguredModules(selected,(ip,port)=>{Check(port!="OTHER"&&ip==hub.RawIp,"connection inspects only configured ports");return states[port];},u=>{calls.Add(u.Port);states[u.Port]=2;});
        Check(calls.SequenceEqual(new[]{"CTRL","PICO"})&&states["OTHER"]==3,"connection commands scoped; remote unselected untouched");
        calls.Clear();Integration.ConnectConfiguredModules(selected,(ip,port)=>states[port],u=>calls.Add(u.Port));
        Check(calls.Count==0,"already locally connected has no extra command");
        foreach(uint invalid in new uint[]{0,3,4,UInt32.MaxValue})
        {
            states=Ports(1,invalid);calls.Clear();bool rejected=false;
            try{Integration.ConnectConfiguredModules(selected,(ip,port)=>states[port],u=>calls.Add(u.Port));}catch(IOException){rejected=true;}
            Check(rejected&&calls.Count==0,"connection preflights all targets before command: "+invalid);
        }
        states=Ports(1,1);calls.Clear();bool failed=false;
        try{Integration.ConnectConfiguredModules(selected,(ip,port)=>states[port],u=>{calls.Add(u.Port);states["PICO"]=3;});}catch(IOException){failed=true;}
        Check(failed&&calls.SequenceEqual(new[]{"CTRL"}),"connection rechecks ownership before second command");
        foreach(string[] locations in new[]{new[]{"","1-2"},new[]{"1-1","1-1"},new[]{"1-1","missing"},new[]{"1-1 /C","1-2"}})
        {
            failed=false;try{Integration.SelectConfiguredModules(Hub(),locations[0],locations[1]);}catch(InvalidDataException){failed=true;}
            Check(failed,"bad connection location rejected");
        }
        hub=Hub();hub.Devices[2].Location="1-2";failed=false;
        try{Integration.SelectConfiguredModules(hub,"1-1","1-2");}catch(InvalidDataException){failed=true;}
        Check(failed,"ambiguous configured acquisition rejected");
        Check(State(Integration.Check(true,Mac,true))=="NeedsConfiguration","legacy unscoped connect cannot scan or connect all modules");
    }
    static void TargetedDisconnectTests()
    {
        var hub = Hub(); var states = Ports(); var calls = new List<string>(); int scans = 0;
        Func<string,List<Server>> scan = ip => { scans++; Check(ip=="192.0.2.1", "only session address queried"); return new List<Server>{hub}; };
        Func<Usb,int> release = target => { calls.Add(target.Port); states[target.Port]=1; return 2; };
        var result = Integration.Disconnect(true,Mac,"1-1","1-2",scan,(ip,p)=>states[p],release,n=>{},"192.0.2.1");
        Check(State(result)=="Disconnected"&&scans==1&&calls.Count==2,"targeted release verified without full scan");
        Check((bool)result.Root.Element("DisconnectTiming").Attribute("Targeted"),"targeted mode recorded");
        Check(result.Root.Element("DisconnectTiming").Elements("Stage").Select(e=>(string)e.Attribute("Name")).SequenceEqual(
            new[]{"Identity","Ownership","ControllerRelease","AcquisitionRelease","ReleaseVerification"}),"each release phase recorded");
        foreach(var invalid in new[]{"127.0.0.1","0.0.0.0","255.255.255.255","not-ip","::1"}) {
            scans=0;calls.Clear();result=Integration.Disconnect(true,Mac,"1-1","1-2",scan,(ip,p)=>states[p],release,n=>{},invalid);
            Check(State(result)=="DisconnectFailed"&&scans==0&&calls.Count==0,"bad hint never discovers or releases "+invalid);
        }
        foreach(var change in new[]{"wrong-mac","wrong-ip","wrong-vid","unknown-owner","no-response"}) {
            hub=Hub();states=Ports();calls.Clear();scans=0;
            if(change=="wrong-mac")hub.Mac="FFEEDDCCBBAA";
            if(change=="wrong-ip")hub.Ip="192.0.2.2";
            if(change=="wrong-vid")hub.Devices[1].Vid=0xffff;
            if(change=="unknown-owner")states["PICO"]=0;
            Func<string,List<Server>> changed = ip => { scans++; return change=="no-response"?new List<Server>():new List<Server>{hub}; };
            result=Integration.Disconnect(true,Mac,"1-1","1-2",changed,(ip,p)=>states[p],release,n=>{},"192.0.2.1");
            Check(State(result)=="DisconnectFailed"&&calls.Count==0&&scans==1,"targeted identity failure no broad fallback "+change);
            Check(result.Root.Element("DisconnectTiming")!=null,"failure preserves phase evidence");
        }
        string exe=Assembly.GetExecutingAssembly().Location;
        var session=new HardwareStatusSnapshot {HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,
            StatusCode="Ready",HubMac=Mac,HubIp="192.0.2.1",FromLiveIntegrationProcess=true,
            RequestedHubMac=Mac,RequestedControllerLocation="1-1",RequestedAcquisitionLocation="1-2",
            IntegrationRequestMode="--inspect",IntegrationExecutablePathUsed=exe};
        Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(session,Mac,"1-1","1-2",exe)=="192.0.2.1","live matching session permits hint");
        var connected=session.Clone();connected.IntegrationRequestMode="--connect";
        Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(connected,Mac,"1-1","1-2",exe)=="","bare connect receipt cannot supply a hint");
        connected.ConnectionReadbackVerified=true;
        Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(connected,Mac,"1-1","1-2",exe)=="192.0.2.1","complete connect readback permits scoped hint");
        foreach(var change in new[]{"file","mac","port","path","unready"}) {
            var other=session.Clone();
            if(change=="file")other.FromLiveIntegrationProcess=false;
            if(change=="mac")other.RequestedHubMac="FFEEDDCCBBAA";
            if(change=="port")other.RequestedAcquisitionLocation="1-3";
            if(change=="path")other.IntegrationExecutablePathUsed="another.exe";
            if(change=="unready")other.Connected=false;
            Check(HardwareStatusAdapter.VerifiedDisconnectAddressHint(other,Mac,"1-1","1-2",exe)=="","untrusted hint ignored "+change);
        }
        using(var adapter=Adapter()) {
            Mode("good"); var value=adapter.DisconnectAsync(session).GetAwaiter().GetResult();
            Check(value.StatusCode=="Disconnected","session release fake completes");
            Check(File.ReadAllLines(Environment.GetEnvironmentVariable("HVX_DISCONNECT_TEST_MARKER")).Contains("--hub-ip=192.0.2.1"),"host sends proven address hint");
        }
    }
    static int Child(string[] args)
    {
        string path=args.Single(a=>a.StartsWith("--output=")).Substring(9);
        string mode=Environment.GetEnvironmentVariable("HVX_DISCONNECT_TEST_MODE")??"good";
        string marker=Environment.GetEnvironmentVariable("HVX_DISCONNECT_TEST_MARKER");
        File.WriteAllLines(marker,args);
        if(mode=="slow") Thread.Sleep(1800);
        if(mode=="hold") Thread.Sleep(15000);
        string mac=args.Single(a=>a.StartsWith("--mac=")).Substring(6);
        var doc=new XDocument(new XElement("HardwareStatus",new XAttribute("Schema",1),new XAttribute("State",mode=="failed"?"DisconnectFailed":mode=="ready"?"Ready":"Disconnected"),
            new XAttribute("Mac",mode=="wrongmac"?"FFEEDDCCBBAA":mac),new XAttribute("Utc",DateTime.UtcNow.AddSeconds(mode=="stale"?-45:0).ToString("o")),new XAttribute("MaxAgeSeconds",30),new XElement("Message","fake unavailable")));
        foreach(string kind in new[]{"Controller","Acquisition"})
        {
            var item=new XElement("Module",new XAttribute("Kind",mode=="duplicate"?"Controller":kind),new XAttribute("Location",kind=="Controller"?"1-1":mode=="wronglocation"?"1-3":"1-2"),
                new XAttribute("Connected",mode=="connected"),new XAttribute("Ready",false));
            if(mode!="missingstate")item.Add(new XAttribute("ConnectionState",mode=="unknown"?0:mode=="remote"?3:1));
            doc.Root.Add(item);
        }
        doc.Save(path);return mode=="failed"||mode=="exit"?40:0;
    }
    static void Mode(string mode) { Environment.SetEnvironmentVariable("HVX_DISCONNECT_TEST_MODE",mode); }
    static HardwareStatusAdapter Adapter(bool enabled=true)
    {
        var adapter=new HardwareStatusAdapter(new TestSettings{Hardware=new TestHardware{Enabled=enabled,IntegrationExecutablePath=Assembly.GetExecutingAssembly().Location}});
        // Production deliberately prefers a bundled connector to configured
        // paths. Check the actual resolution before any adapter operation: a
        // stub setting alone is not isolation when compiled beside the app.
        string resolved=(string)typeof(HardwareStatusAdapter).GetMethod("ResolveIntegrationExecutable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
        if(!String.Equals(Path.GetFullPath(resolved),Path.GetFullPath(Assembly.GetExecutingAssembly().Location),StringComparison.OrdinalIgnoreCase))
        { adapter.Dispose();throw new InvalidOperationException("Offline test refused a non-fake connector: "+resolved); }
        Check(true,"adapter executable resolves only to offline fake");
        return adapter;
    }
    static void AdapterTests(string root)
    {
        string marker=Path.Combine(root,"fake-request.txt");Environment.SetEnvironmentVariable("HVX_DISCONNECT_TEST_MARKER",marker);
        Mode("good");using(var adapter=Adapter())
        {
            var snapshot=adapter.DisconnectAsync().GetAwaiter().GetResult();
            Check(snapshot.StatusCode=="Disconnected"&&!snapshot.Connected&&!snapshot.Ready&&snapshot.IdentityValid,"adapter successful verified release: "+snapshot.StatusCode+"; "+snapshot.Diagnostic);
            Check(adapter.LastSnapshot.StatusCode=="Disconnected","snapshot published");
            string[] args=File.ReadAllLines(marker);
            Check(args.Contains("--disconnect")&&args.Contains("--controller-location=1-1")&&args.Contains("--acquisition-location=1-2")&&args.Contains("--mac="+Mac),"adapter explicit scope arguments");
            Check(!args.Contains("--connect")&&!args.Contains("--inspect"),"no accidental connect flag");
        }
        foreach(string mode in new[]{"wrongmac","stale","duplicate","wronglocation","connected","missingstate","unknown","remote","exit","ready","failed"})
        {
            Mode(mode);using(var adapter=Adapter()) {var snapshot=adapter.DisconnectAsync().GetAwaiter().GetResult();Check(snapshot.StatusCode=="DisconnectFailed"&&!snapshot.Ready,"fail closed output "+mode);}
        }
        File.Delete(marker);using(var adapter=Adapter(false))
            Check(adapter.DisconnectAsync().GetAwaiter().GetResult().StatusCode=="DisconnectFailed"&&!File.Exists(marker),"disabled adapter does not launch");
        Mode("slow");using(var adapter=Adapter())
        {
            Task<HardwareStatusSnapshot> first=adapter.DisconnectAsync();
            Check(!adapter.BeginRefresh(false,null),"disconnect excludes new refresh");
            bool refused=false;try{adapter.DisconnectAsync().GetAwaiter().GetResult();}catch(InvalidOperationException){refused=true;}
            Check(refused,"duplicate disconnect rejected"); Check(first.GetAwaiter().GetResult().StatusCode=="Disconnected","first request completes");
            Mode("good");Check(adapter.DisconnectAsync().GetAwaiter().GetResult().StatusCode=="Disconnected","gate released after completion");
        }
        Mode("slow");using(var adapter=Adapter())
        {
            Check(adapter.BeginRefresh(false,null),"refresh started");
            bool refused=false;try{adapter.DisconnectAsync().GetAwaiter().GetResult();}catch(InvalidOperationException){refused=true;}
            Check(refused,"refresh excludes disconnect");
            Thread.Sleep(2400);
        }
        Mode("hold");File.Delete(marker);var closing=Adapter();Task<HardwareStatusSnapshot> pending=closing.DisconnectAsync();
        var watch=Stopwatch.StartNew();while(!File.Exists(marker)&&watch.ElapsedMilliseconds<6000)Thread.Sleep(20);
        Check(File.Exists(marker),"owned fake child started");closing.Dispose();
        bool cancelled=false;try{pending.GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"dispose cancels pending result rather than publishing success");
        Check(closing.LastSnapshot.StatusCode!="Disconnected","dispose discards pending snapshot");
        Check(!Directory.EnumerateFiles(Path.Combine(root,"hardware-status")).Any(),"unique transport outputs cleaned");
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Any(a=>a.StartsWith("--output=")))return Child(args);
        try
        {
            if(args.Length!=2||args[0]!="--isolated-run")
            {
                if(args.Length!=1)throw new ArgumentException("Pass the isolated QA output directory.");
                string outer=Path.Combine(Path.GetFullPath(args[0]),"hardware-disconnect-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(outer);
                return RunIsolated(outer);
            }
            string root=Path.GetFullPath(args[1]);
            string expected=Path.Combine(root,"runner","HardwareDisconnectSmoke.exe");
            Check(String.Equals(Assembly.GetExecutingAssembly().Location,expected,StringComparison.OrdinalIgnoreCase),"offline harness runs from its own isolated directory");
            Check(!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules","HardwareIntegration","HardwareIntegration.exe")),"no staged real connector in isolated harness");
            AppPaths.LocalDataDirectory=root;ConnectorTests();ConnectionScopeTests();AdapterTests(root);TargetedDisconnectTests();
            Console.WriteLine("HardwareDisconnectSmoke "+passed+" PASS; HARDWARE_COMMANDS=0; "+root);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Environment.SetEnvironmentVariable("HVX_DISCONNECT_TEST_MODE",null);Environment.SetEnvironmentVariable("HVX_DISCONNECT_TEST_MARKER",null);}
    }
    static int RunIsolated(string root)
    {
        string runner=Path.Combine(root,"runner");Directory.CreateDirectory(runner);
        string executable=Path.Combine(runner,"HardwareDisconnectSmoke.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location,executable,false);
        using(var process=Process.Start(new ProcessStartInfo(executable,"--isolated-run \""+root+"\"") {
            WorkingDirectory=runner,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardOutput=true,RedirectStandardError=true }))
        {
            if(process==null)throw new InvalidOperationException("Offline harness could not start.");
            if(!process.WaitForExit(45000)) {process.Kill();process.WaitForExit();throw new TimeoutException("Offline harness timed out.");}
            Console.Write(process.StandardOutput.ReadToEnd());Console.Error.Write(process.StandardError.ReadToEnd());
            return process.ExitCode;
        }
    }
    public sealed class TestSettings { public TestHardware Hardware {get;set;} }
    public sealed class TestHardware
    {
        public bool Enabled {get;set;}
        public string SelectedHubMac {get{return Mac;}}
        public string ControllerLocation {get{return "1-1";}}
        public string AcquisitionLocation {get{return "1-2";}}
        public string IntegrationExecutablePath {get;set;}
        public string DriverMarkerPath {get{return IntegrationExecutablePath;}}
    }
}
namespace Xiongxin.HvDacXpert
{
    static partial class Integration
    { internal static System.Xml.Linq.XDocument CheckLocal(bool enabled,string mac,string c,string a,string art,string pico){throw new Exception("Local inspection not part of disconnect fixture");} }
    sealed class Server { public string Mac,Ip;public uint RawIp; public List<Usb> Devices=new List<Usb>(); }
    sealed class Usb { public Server Server;public ushort Vid,Pid;public string Location,Port; }
    static class Backend
    { public static List<Server> Scan(string ip){throw new Exception("Real scan forbidden in offline test");} public static List<Server> InspectSingle(string ip){throw new Exception("Real targeted scan forbidden in offline test");} public static string FormatMac(string mac){return mac;} public static bool IsBound(Usb usb){return false;} }
    static class Native
    { public static uint SxuptpGetDeviceStatus(uint ip,string port){throw new Exception("Real SDK forbidden in offline test");} public static void SetDllDirectory(string path){} }
    static class ConnectionHealth { public static bool Connected(Usb usb){throw new Exception("Unexpected connected probe");}public static bool Ready(Usb usb){throw new Exception("Unexpected PnP probe");} }
    sealed class ModuleIdentity
    { public string Serial="",TransportSerial="",InstanceId="",DeviceName="";public static ModuleIdentity Resolve(Usb usb){throw new Exception("Unexpected identity probe");} }
}
namespace Xiongxin.OWTS.Desktop
{
    static class EmbeddedModulePayload { public static bool HasEmbeddedPayload { get { return false; } } }
    static class FirstHardwareProvisioning { public static string GetVerifiedConnectorPath() { throw new Exception("Real embedded connector forbidden in offline test"); } }
    sealed class HardwareStatusSnapshot
    {
        public bool HardwareEnabled,DriverInstalled,Connected,IdentityValid,FromLiveIntegrationProcess,ConnectionReadbackVerified;
        public bool HasLiveIdentityReadback {get{return FromLiveIntegrationProcess&&(IntegrationRequestMode=="--inspect"||(IntegrationRequestMode=="--connect"&&ConnectionReadbackVerified));}}
        public string StatusCode="Offline",Message,Diagnostic,HubMac,HubIp,ControllerSerial,AcquisitionSerial;
        public string IntegrationExecutablePathUsed="",IntegrationRequestMode="",RequestedHubMac="",RequestedControllerLocation="",RequestedAcquisitionLocation="";
        public string ControllerTransportSerial="",ControllerInstanceId="",AcquisitionTransportSerial="",AcquisitionInstanceId="";
        public string ControllerDeviceName="",AcquisitionDeviceName="";
        public DateTime CheckedUtc;
        public bool Ready {get{return HardwareEnabled&&DriverInstalled&&Connected&&IdentityValid&&StatusCode=="Ready";}}
        public HardwareStatusSnapshot Clone(){return (HardwareStatusSnapshot)MemberwiseClone();}
    }
    static class AppPaths { public static string LocalDataDirectory; }
    sealed class Diagnostics {public void Info(string a,string b){}public void Warning(string a,string b){}public void Error(string a,string b,Exception c){} }
    static class LateBound
    {
        static object Read(object source,string path)
        {foreach(string part in path.Split('.')){if(source==null)return null;var property=source.GetType().GetProperty(part);if(property==null)return null;source=property.GetValue(source,null);}return source;}
        public static string ReadString(object source,params string[] names){foreach(string name in names){object value=Read(source,name);if(value!=null)return Convert.ToString(value);}return "";}
        public static bool ReadBoolean(object source,bool fallback,params string[] names){foreach(string name in names){object value=Read(source,name);if(value is bool)return (bool)value;}return fallback;}
    }
}
