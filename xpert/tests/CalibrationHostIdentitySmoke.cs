using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Linq;
using Xiongxin.OWTS.Desktop;
using Xiongxin.HvDacXpert;

internal static class CalibrationHostIdentitySmoke
{
    static int checks;
    static readonly BindingFlags Hidden=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Reject(Action run,string message){try{run();}catch{checks++;return;}throw new Exception("Expected rejection: "+message);}
    static AppSettings Config(bool swap=false)
    {
        var app=new AppSettings();app.Hardware.Enabled=true;app.Hardware.SelectedHubMac="001122334455";
        app.Hardware.ControllerLocation="H11";app.Hardware.AcquisitionLocation="S51";
        app.Hardware.ControllerSerial=swap?"ABCDE/1234":"1023417009";app.Hardware.AcquisitionSerial=swap?"1023417009":"ABCDE/1234";
        return app;
    }
    static HardwareStatusSnapshot Snapshot(bool swap=false)
    {
        var s=new HardwareStatusSnapshot{HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",
            HubMac="001122334455",FromLiveIntegrationProcess=true,IntegrationRequestMode="--inspect",IntegrationExecutablePathUsed="fake/connector.exe",
            RequestedHubMac="001122334455",RequestedControllerLocation="H11",RequestedAcquisitionLocation="S51",CheckedUtc=DateTime.UtcNow};
        var app=Config(swap);s.ControllerSerial=app.Hardware.ControllerSerial;s.AcquisitionSerial=app.Hardware.AcquisitionSerial;
        string art="USB\\VID_3063&PID_3135\\1023417009",pico="USB\\VID_0CE9&PID_1218\\FAKE-PORT";
        s.ControllerInstanceId=swap?pico:art;s.AcquisitionInstanceId=swap?art:pico;
        s.ControllerDeviceName=swap?"":"Dev7";s.AcquisitionDeviceName=swap?"Dev7":"";return s;
    }
    static void TrustedSelection()
    {
        foreach(bool swap in new[]{false,true})
        {
            var app=Config(swap);var s=Snapshot(swap);var binding=CalibrationHardwareIdentity.Read(app,s);
            Check(binding[1]=="Dev7"&&binding[2]=="1023417009"&&binding[3]=="ABCDE/1234","exact selected aliases and serials for either module role");
            var changed=s.Clone();if(swap)changed.ControllerInstanceId+="-NEW";else changed.AcquisitionInstanceId+="-NEW";
            Check(CalibrationHardwareIdentity.Read(app,changed)[0]!=binding[0],"Pico instance drift changes full binding in either role");
            changed=s.Clone();changed.FromLiveIntegrationProcess=false;Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"status-file evidence is not host verification");
            changed=s.Clone();changed.Connected=false;Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"lost session");
            changed=s.Clone();changed.IntegrationRequestMode="--connect";Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"uninspected connection");
            changed.ConnectionReadbackVerified=true;
            Check(CalibrationHardwareIdentity.Read(app,changed)[1]=="Dev7","complete live post-connect readback uses same identity contract");
            changed.FromLiveIntegrationProcess=false;Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"cached connect flag is not live evidence");
            changed=s.Clone();changed.CheckedUtc=DateTime.MinValue;Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"missing verification time");
            changed=s.Clone();changed.CheckedUtc=DateTime.UtcNow.AddMinutes(1);Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"future evidence");
            changed=s.Clone();changed.ControllerDeviceName="";changed.AcquisitionDeviceName="";Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"no guessed default alias");
            changed=s.Clone();changed.RequestedControllerLocation="H12";Reject(()=>CalibrationHardwareIdentity.Read(app,changed),"configuration location mismatch");
            app.Hardware.AcquisitionSerial="OTHER/0000";Reject(()=>CalibrationHardwareIdentity.Read(app,s),"configured pair drift");
        }
        var app2=Config();var old=Snapshot();old.CheckedUtc=DateTime.UtcNow.AddHours(-1);
        Check(CalibrationHardwareIdentity.Read(app2,old)[1]=="Dev7","still-established live session does not cause periodic rediscovery");
        var clone=old.Clone();clone.ControllerDeviceName="Dev8";
        var same=typeof(MainForm).GetMethod("SameConnectionHealth",BindingFlags.NonPublic|BindingFlags.Static);
        Check(!(bool)same.Invoke(null,new object[]{old,clone}),"silent health publication cannot discard new ART alias");
        clone=old.Clone();clone.AcquisitionDeviceName="other";
        Check(!(bool)same.Invoke(null,new object[]{old,clone}),"either role alias changes health fingerprint");
    }
    static void SessionEpoch(string directory)
    {
        var app=Config();var state=new ApplicationState();state.SetHardware(Snapshot());
        using(var host=new ModuleHost(app,state,new Diagnostics(),Path.Combine(directory,"modules")))
        using(var page=new Control())
        {
            var read=typeof(ModuleHost).GetMethod("ReadCurrentCalibrationHardwareIdentity",Hidden);
            var apply=typeof(ModuleHost).GetMethod("ApplyCalibrationHardwareIdentity",Hidden);
            Func<string[]> get=()=> (string[])read.Invoke(host,null);
            string first=get()[0];var later=Snapshot();later.CheckedUtc=DateTime.UtcNow.AddSeconds(-1);state.SetHardware(later);
            Check(get()[0]==first,"unchanged live health timestamp does not retire session");
            later.Connected=false;state.SetHardware(later);apply.Invoke(host,new object[]{page});
            Reject(()=>get(),"disconnected evidence cannot be consumed");
            state.SetHardware(Snapshot());Check(get()[0]!=first,"reconnected same pair receives a new host binding epoch");
        }
    }
    static void PublishInspection()
    {
        var app=Config();var state=new ApplicationState();var connected=Snapshot();
        connected.FromLiveIntegrationProcess=false;connected.IntegrationRequestMode="";connected.IntegrationExecutablePathUsed="";
        connected.RequestedHubMac="";connected.RequestedControllerLocation="";connected.RequestedAcquisitionLocation="";
        state.SetHardware(connected);
        using(var form=new MainForm(app,state,true))
        {
            // Avoid asset/profile effects; exercise actual publication of injected evidence only.
            typeof(MainForm).GetField("settingsDialogActive",Hidden).SetValue(form,true);
            int refreshes=0;form.RefreshDeviceForTests=(connect,done)=>{refreshes++;throw new Exception("No refresh allowed");};
            typeof(MainForm).GetMethod("PublishHardwareInspection",Hidden).Invoke(form,new object[]{Snapshot(),true});
            var published=state.Hardware;
            Check(published.FromLiveIntegrationProcess&&published.IntegrationRequestMode=="--inspect","silent post-connect inspection publishes new trusted origin");
            Check(published.RequestedHubMac=="001122334455"&&published.IntegrationExecutablePathUsed=="fake/connector.exe","inspection contract fields not lost in timestamp-only path");
            Check(CalibrationHardwareIdentity.Read(app,published)[1]=="Dev7"&&refreshes==0,"new verified snapshot enables binding without another hardware check");
            var alias=Snapshot();alias.ControllerDeviceName="Dev8";
            typeof(MainForm).GetMethod("PublishHardwareInspection",Hidden).Invoke(form,new object[]{alias,true});
            Check(state.Hardware.ControllerDeviceName=="Dev8"&&refreshes==0,"silent alias update publishes new name without discovery");
        }
    }
    static ArtRecord Record(string alias,uint serial,string instance)
    {return new ArtRecord{Name=alias,Serial=serial,Instance=instance,Type="USB3135",Class=1,Bus=1,Status=8};}
    static void UniqueAlias()
    {
        const string instance="USB\\VID_3063&PID_3135\\1023417009";
        var chosen=Record("Dev7",1023417009,instance);var other=Record("Dev1",1023417010,"USB\\VID_3063&PID_3135\\1023417010");
        Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{other,chosen})=="Dev7","multiple-device list selects exact pair, not first Dev1");
        Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{other})=="","missing selected record does not choose another");
        var duplicate=chosen;duplicate.Name="Dev8";Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{chosen,duplicate})=="","same serial mapped twice rejected");
        other.Name="dev7";Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{chosen,other})=="","case-insensitive alias collision rejected");
        duplicate=chosen;duplicate.Instance+="-STALE";Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{duplicate})=="","stale PnP instance cannot bind");
        duplicate=chosen;duplicate.Status=0;Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{duplicate})=="","stopped alias rejected");
        duplicate=chosen;duplicate.Name="Dev1/port1/line2";Check(ModuleIdentity.ResolveArtDeviceName("1023417009",instance,new[]{duplicate})=="","channel injection rejected");
        Check(ModuleIdentity.ResolveArtDeviceName("0x3D003711",instance,new[]{chosen})=="","hex serial is not guessed decimal");
    }
    static void SnapshotParsing(string directory)
    {
        var app=Config();using(var adapter=new HardwareStatusAdapter(app))
        {
            var parse=typeof(HardwareStatusAdapter).GetMethod("ParseStatusFile",Hidden);
            var validate=typeof(HardwareStatusAdapter).GetMethod("ValidateStatus",Hidden);
            Func<string,HardwareStatusSnapshot> read=path=>(HardwareStatusSnapshot)validate.Invoke(adapter,new object[]{parse.Invoke(adapter,new object[]{path}),null,"001122334455","H11","S51","",true});
            var xml=new XElement("HardwareStatus",new XAttribute("Schema",1),new XAttribute("State","Ready"),new XAttribute("Mac","001122334455"),new XAttribute("Utc",DateTime.UtcNow.ToString("o")),new XAttribute("MaxAgeSeconds",30),
                new XElement("Module",new XAttribute("Kind","Controller"),new XAttribute("Location","H11"),new XAttribute("Serial","1023417009"),new XAttribute("InstanceId","USB\\VID_3063&PID_3135\\1023417009"),new XAttribute("DeviceName","Dev7"),new XAttribute("Connected",true),new XAttribute("Ready",true)),
                new XElement("Module",new XAttribute("Kind","Acquisition"),new XAttribute("Location","S51"),new XAttribute("Serial","ABCDE/1234"),new XAttribute("Connected",true),new XAttribute("Ready",true)));
            string file=Path.Combine(directory,"fake-status.xml");File.WriteAllText(file,xml.ToString());var parsed=read(file);
            Check(parsed.ControllerDeviceName=="Dev7"&&parsed.Clone().ControllerDeviceName=="Dev7","XML and clone preserve exact alias");
            Check(!parsed.FromLiveIntegrationProcess,"alias in status file cannot manufacture live-origin evidence");
            Reject(()=>CalibrationHardwareIdentity.Read(app,parsed),"external file cannot authorize calibration");
            xml.SetAttributeValue("Utc",DateTime.UtcNow.AddMinutes(-5).ToString("o"));File.WriteAllText(file,xml.ToString());Reject(()=>read(file),"existing freshness gate rejects expired status file");
            string json=Path.Combine(directory,"fake-status.json");File.WriteAllText(json,"{\"Schema\":1,\"State\":\"Ready\",\"Mac\":\"001122334455\",\"Utc\":\""+DateTime.UtcNow.ToString("o")+"\",\"MaxAgeSeconds\":30,\"Modules\":[{\"Kind\":\"Controller\",\"Location\":\"H11\",\"Serial\":\"1023417009\",\"DeviceName\":\"Dev7\",\"Connected\":true,\"Ready\":true},{\"Kind\":\"Acquisition\",\"Location\":\"S51\",\"Serial\":\"ABCDE/1234\",\"Connected\":true,\"Ready\":true}]}");
            Check(read(json).ControllerDeviceName=="Dev7","JSON also preserves alias without trusting the file");
        }
    }
    [STAThread]static int Main()
    {
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fake-identity");Directory.CreateDirectory(directory);
        TrustedSelection();UniqueAlias();SnapshotParsing(directory);SessionEpoch(directory);PublishInspection();
        Console.WriteLine("CALIBRATION_HOST_IDENTITY_PASS="+checks+"; pure stored evidence, no connector, SDK, registry scan or device access.");return 0;
    }
}
namespace Xiongxin.HvDacXpert
{
    // Only the pure alias-selection method of the production identity class is invoked.
    sealed class Server {public uint RawIp;}
    sealed class Usb {public ushort Vid,Pid;public Server Server;public string Location;}
    struct ArtRecord {public string Name,Type,Instance;public uint Class,Bus,Serial,Status;
        public bool Supported{get{return Class==1&&Bus==1&&Type=="USB3135";}}public bool Started{get{return (Status&8)!=0;}}}
    static class ArtAliases {public static List<ArtRecord> Read(){throw new Exception("Native DMC access forbidden in offline fixture.");}}
}
