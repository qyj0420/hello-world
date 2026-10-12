// Offline host contract regression. The default connector/process path is never
// invoked: discovery is injected, and every connection race stops before I/O.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;

static class DetectionDeviceSelectionSmoke
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks, discoveries;
    static readonly DateTime Now=new DateTime(2026,10,2,0,0,0,DateTimeKind.Utc);
    static void Check(bool result,string label) { if(!result) throw new Exception(label); checks++; }
    static object Field(object value,string name) { return value.GetType().GetField(name,Hidden).GetValue(value); }
    static void Set(object value,string name,object content) { value.GetType().GetField(name,Hidden).SetValue(value,content); }
    static object Call(object value,string name,params object[] args) { return value.GetType().GetMethod(name,Hidden).Invoke(value,args); }
    static string Entry(string mac="001122334455",bool bound=true,string state="Available",bool complete=true)
    { return "<Device Mac=\""+mac+"\" Ip=\"192.0.2.1\" ControllerLocation=\"1-1\" AcquisitionLocation=\"1-2\" PersistentBound=\""+bound.ToString().ToLowerInvariant()+"\" Complete=\""+complete.ToString().ToLowerInvariant()+"\" State=\""+state+"\"/>"; }
    static string Xml(string entries,DateTime? produced=null)
    { return "<HardwareDiscovery Schema=\"1\" State=\"Complete\" MaxAgeSeconds=\"30\" Utc=\""+(produced??Now).ToString("O",CultureInfo.InvariantCulture)+"\">"+entries+"</HardwareDiscovery>"; }
    static DetectionDeviceList Receipt(DateTime now) { return HardwareDeviceDiscovery.Parse(Xml(Entry(),now),now); }
    static void Reject(string value,string label)
    { bool rejected=false; try { HardwareDeviceDiscovery.Parse(value,Now); } catch(Exception error) { rejected=error is System.Xml.XmlException || error is InvalidDataException; } Check(rejected,label); }
    static void ParserFixtures()
    {
        var receipt=HardwareDeviceDiscovery.Parse(Xml(Entry()+Entry("AABBCCDDEEFF",false)),Now);
        Check(receipt.Devices.Count==2 && receipt.Devices[0].PersistentBound && !receipt.Devices[1].PersistentBound,"bound/unbound exact architecture parsed");
        Check(receipt.IsFresh(Now.AddSeconds(30)) && !receipt.IsFresh(Now.AddTicks(TimeSpan.FromSeconds(30).Ticks+1)),"TTL exact 30-second boundary");
        Check(!receipt.IsFresh(Now.AddTicks(-1)) && !receipt.IsFresh(DateTime.SpecifyKind(Now,DateTimeKind.Local)),"future/local validation clock fails closed");
        Check(receipt.CanSelect(receipt.Devices[0],Now) && !receipt.CanSelect(new DetectionDevice("001122334455","192.0.2.1","1-1","1-2",true,true,"Available"),Now),"receipt requires its exact immutable selected object");
        Check(!receipt.CanSelect(null,Now) && !receipt.CanSelect(receipt.Devices[0],Now.AddSeconds(31)),"null/expired selection rejected");
        Check(HardwareDeviceDiscovery.Parse(Xml(""),Now).Devices.Count==0,"empty result remains honest empty result");
        var rows=new List<DetectionDevice>{receipt.Devices[0]}; var immutable=new DetectionDeviceList(Now,rows); rows.Clear();
        Check(immutable.Devices.Count==1,"receipt defensively copies source list");
        Check(!new DetectionDeviceList(DateTime.SpecifyKind(Now,DateTimeKind.Unspecified),rows).IsFresh(Now),"non-UTC receipt cannot become fresh");
        foreach(string state in new[]{"InUse","Incomplete","Ambiguous"})
        {
            bool complete=state=="InUse"; string row=Entry("001122334455",false,state,complete);
            if(!complete) row=row.Replace("ControllerLocation=\"1-1\"","ControllerLocation=\"\"").Replace("AcquisitionLocation=\"1-2\"","AcquisitionLocation=\"\"");
            var one=HardwareDeviceDiscovery.Parse(Xml(row),Now); Check(!one.CanSelect(one.Devices[0],Now),"nonactionable state "+state);
        }
        foreach(string invalid in new[]{
            Xml(Entry()+Entry()), Xml(Entry().Replace("001122334455","00:11:22:33:44:55")),
            Xml(Entry().Replace("192.0.2.1","::1")), Xml(Entry().Replace("State=\"Available\"","State=\"Ready\"")),
            Xml(Entry().Replace("PersistentBound=\"true\"","PersistentBound=\"True\"")),
            Xml(Entry().Replace("Complete=\"true\"","Complete=\"1\"")),
            Xml(Entry().Replace("Complete=\"true\"","Complete=\"false\"")),
            Xml(Entry().Replace("State=\"Available\"","State=\"Ambiguous\"")),
            Xml(Entry().Replace("AcquisitionLocation=\"1-2\"","AcquisitionLocation=\"1-1\"")),
            Xml(Entry().Replace("ControllerLocation=\"1-1\"","ControllerLocation=\"1 /C\"")),
            Xml(Entry().Replace("ControllerLocation=\"1-1\"","ControllerLocation=\"abc\"")),
            Xml(Entry().Replace("ControllerLocation=\"1-1\"","ControllerLocation=\""+new string('X',65)+"\"")),
            Xml("<Unknown/>"), Xml("<Message><Nested/></Message>"), Xml("<Message>a</Message><Message>b</Message>"),
            Xml("<Message>"+new string('X',2049)+"</Message>"),
            Xml(Entry()).Replace("Schema=\"1\"","Schema=\"2\""), Xml(Entry()).Replace("State=\"Complete\"","State=\"Error\""),
            Xml(Entry()).Replace("MaxAgeSeconds=\"30\"","MaxAgeSeconds=\"300\""), Xml(Entry(),Now.AddSeconds(-31)), Xml(Entry(),Now.AddTicks(1)),
            "<!DOCTYPE HardwareDiscovery [<!ENTITY leak SYSTEM 'file:///C:/Windows/win.ini'>]>"+Xml("<Message>&leak;</Message>"),
            null,new string('X',1024*1024+1),Xml(String.Concat(Enumerable.Repeat(Entry(),257))) }) Reject(invalid,"invalid discovery receipt rejected");
        string maximum=String.Concat(Enumerable.Range(0,256).Select(index=>Entry(index.ToString("X12"))));
        Check(HardwareDeviceDiscovery.Parse(Xml(maximum),Now).Devices.Count==256,"maximum bounded receipt accepted");
        Check(HardwareDeviceDiscovery.ParseFreshResult(Xml(Entry(),Now.AddSeconds(1)),Now,Now.AddSeconds(2)).Devices.Count==1,"current request accepts only newly stamped result");
        foreach(int clock in new[]{0,1,2})
        { bool rejected=false; try { HardwareDeviceDiscovery.ParseFreshResult(Xml(Entry()),clock==0?Now.AddTicks(1):clock==1?DateTime.SpecifyKind(Now,DateTimeKind.Local):Now.AddSeconds(1),clock==0?Now.AddSeconds(2):Now); } catch(InvalidDataException) { rejected=true; } Check(rejected,"cached/new-request/backward clock rejected "+clock); }
        Console.WriteLine("ROUND_1_PARSER_PASS="+checks);
    }
    static HardwareStatusSnapshot Exact(DetectionDevice device,DateTime now)
    {
        return new HardwareStatusSnapshot { HardwareEnabled=true,DriverInstalled=true,Connected=true,IdentityValid=true,StatusCode="Ready",
            HubMac=device.Mac,RequestedHubMac=device.Mac,RequestedControllerLocation=device.ControllerLocation,RequestedAcquisitionLocation=device.AcquisitionLocation,
            ControllerSerial="CTRL-A",AcquisitionSerial="PICO-A",ControllerTransportSerial="CTRL",AcquisitionTransportSerial="PICO",
            ControllerInstanceId=@"USB\VID_3063&PID_3135\CTRL",AcquisitionInstanceId=@"USB\VID_0CE9&PID_1218\PICO",
            FromLiveIntegrationProcess=true,IntegrationRequestMode="--inspect",IntegrationExecutablePathUsed=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules","HardwareIntegration","HardwareIntegration.exe"),CheckedUtc=now };
    }
    static void IdentityFixtures()
    {
        // Source-built test executables have no embedded runtime. Production now
        // checks that the resolved connector exists; supply a non-executable,
        // isolated path fixture, never a real driver tool or a device process.
        string connector=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"modules","HardwareIntegration","HardwareIntegration.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(connector));
        if(!File.Exists(connector))File.WriteAllText(connector,"Offline path fixture; not an executable.");
        var device=Receipt(Now).Devices[0]; var exact=Exact(device,Now);
        Check(HardwareDeviceDiscovery.ExactSelection(device,exact,Now),"fresh exact inspect identity accepted");
        foreach(Action<HardwareStatusSnapshot> alter in new Action<HardwareStatusSnapshot>[] {
            s=>s.HardwareEnabled=false,s=>s.DriverInstalled=false,s=>s.Connected=false,s=>s.IdentityValid=false,s=>s.StatusCode="Error",
            s=>s.FromLiveIntegrationProcess=false,s=>s.IntegrationRequestMode="--connect",s=>s.HubMac="AABBCCDDEEFF",s=>s.RequestedHubMac="AABBCCDDEEFF",
            s=>s.RequestedControllerLocation="1-9",s=>s.RequestedAcquisitionLocation="1-9",s=>s.ControllerSerial="",s=>s.AcquisitionSerial=" ",
            s=>s.ControllerInstanceId="",s=>s.AcquisitionInstanceId=null,s=>s.ControllerTransportSerial="",s=>s.AcquisitionTransportSerial="OTHER",
            s=>s.ControllerTransportSerial="CTRL ",s=>s.ControllerInstanceId=@"USB\VID_3063&PID_FFFF\CTRL",s=>s.AcquisitionInstanceId=@"USB\VID_0CE9&PID_FFFF\PICO",
            s=>s.ControllerInstanceId=@"USB\VID_3063&PID_3135\CTRL\EXTRA",s=>s.ControllerInstanceId=new string('X',513),
            s=>s.IntegrationExecutablePathUsed="HardwareIntegration.exe",s=>s.IntegrationExecutablePathUsed=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"other.exe"),
            s=>s.CheckedUtc=Now.AddSeconds(-31),s=>s.CheckedUtc=Now.AddTicks(1),s=>s.CheckedUtc=DateTime.SpecifyKind(Now,DateTimeKind.Local) })
        { var altered=exact.Clone(); alter(altered); Check(!HardwareDeviceDiscovery.ExactSelection(device,altered,Now),"inexact connector evidence cannot authorize selection"); }
        Check(!HardwareDeviceDiscovery.ExactSelection(null,exact,Now) && !HardwareDeviceDiscovery.ExactSelection(device,null,Now),"null exact evidence safe rejection");
        var unsafeDevice=new DetectionDevice("bad","192.0.2.1","1-1","1-2",true,true,"Available");
        Check(!HardwareDeviceDiscovery.ExactSelection(unsafeDevice,exact,Now),"forged constructor identity rejected");
        Console.WriteLine("ROUND_2_EXACT_IDENTITY_PASS="+checks);
        var basis=new AppSettings(); basis.Hardware.SelectedHubMac=device.Mac; basis.Hardware.ControllerLocation=device.ControllerLocation; basis.Hardware.AcquisitionLocation=device.AcquisitionLocation;
        basis.Hardware.ControllerSerial="CTRL-A"; basis.Hardware.AcquisitionSerial="PICO-A";
        basis.Hardware.StatusJsonPath="legacy.xml"; basis.Hardware.ConnectorExecutablePath="legacy.exe"; basis.DeviceParametersReviewedHash="kept"; basis.DeviceParametersReviewedIdentity="identity"; basis.DeviceParametersReviewedUtc="date";
        var probe=HardwareDeviceDiscovery.SelectionProbe(basis,device);
        Check(!Object.ReferenceEquals(probe,basis) && !Object.ReferenceEquals(probe.Hardware,basis.Hardware),"selection works on deep clone");
        Check(probe.Hardware.Enabled && probe.Hardware.SelectedHubMac==device.Mac && probe.Hardware.ControllerLocation==device.ControllerLocation && probe.Hardware.AcquisitionLocation==device.AcquisitionLocation,"probe retains only explicit pair");
        Check(probe.Hardware.ControllerSerial=="" && probe.Hardware.AcquisitionSerial=="" && probe.Hardware.StatusJsonPath=="" && probe.Hardware.ConnectorExecutablePath=="" && probe.Hardware.DriverMarkerPath=="","probe cannot reuse old serial/status/driver markers");
        Check(probe.DeviceParametersReviewedHash=="" && probe.DeviceParametersReviewedIdentity=="" && probe.DeviceParametersReviewedUtc=="","new device cannot inherit reviewed permission");
        Check(basis.Hardware.ControllerSerial=="CTRL-A" && basis.Hardware.StatusJsonPath=="legacy.xml" && basis.DeviceParametersReviewedHash=="kept","probe never modifies basis");
        Check(HardwareDeviceDiscovery.SavedSerialsMatch(basis,device,exact),"same pair retains exact saved native serials");
        foreach(bool controller in new[]{true,false}) { var altered=exact.Clone(); if(controller) altered.ControllerSerial="REPLACED"; else altered.AcquisitionSerial="REPLACED"; Check(!HardwareDeviceDiscovery.SavedSerialsMatch(basis,device,altered),"same ports different native serial not silently accepted"); }
        basis.Hardware.SelectedHubMac="AABBCCDDEEFF"; Check(HardwareDeviceDiscovery.SavedSerialsMatch(basis,device,exact),"different selected device is new review scope");
        bool rejected=false; try { HardwareDeviceDiscovery.SelectionProbe(basis,unsafeDevice); } catch(InvalidOperationException) { rejected=true; } Check(rejected,"invalid selected identity cannot build connector arguments");
        Console.WriteLine("ROUND_3_CLONE_AND_SAVED_IDENTITY_PASS="+checks);
    }
    static void FontTree(Control control,Font font) { control.Font=font; foreach(Control child in control.Controls) FontTree(child,font); }
    static void Pump() { for(int i=0;i<3;i++) Application.DoEvents(); }
    static void LayoutFixtures(string qa)
    {
        foreach(string language in new[]{"zh-CN","zh-TW","en"}) foreach(float size in new[]{10.5f,18f})
        {
            // Use a freshly constructed receipt: the fixed parser clock is already tested separately.
            var receipt=HardwareDeviceDiscovery.Parse(Xml(Entry()+Entry("AABBCCDDEEFF",false)+Entry("112233445566",false,"InUse"),DateTime.UtcNow),DateTime.UtcNow);
            using(var form=new DetectionDeviceSelectionForm(receipt,language,receipt.Devices[0].Mac)) using(var font=new Font("Microsoft YaHei UI",size))
            {
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); FontTree(form,font); form.Size=new Size(760,500); form.Show(); Pump();
                var grid=(DataGridView)form.Controls.Find("DetectionDeviceList",true)[0]; var connect=(Button)form.Controls.Find("ConnectSelectedDetector",true)[0];
                Check(grid.Rows.Count==3 && grid.ReadOnly && !grid.MultiSelect,"read-only single-device list "+language+"/"+size);
                Check(!connect.Enabled && !form.TrySelect(DateTime.UtcNow),"no implied default selection "+language+"/"+size);
                foreach(string name in new[]{"DiscoveryIntroduction","DetectionDeviceList","DiscoveryStatus","DiscoveryActions","ConnectSelectedDetector","RefreshDetectors"})
                { var control=form.Controls.Find(name,true)[0]; var bounds=form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)); Check(form.ClientRectangle.Contains(bounds) && control.Width>0 && control.Height>0,"visible discovery layout "+name+"/"+language+"/"+size); }
                foreach(DataGridViewRow row in grid.Rows)
                {
                    Check(!row.Cells.Cast<DataGridViewCell>().Any(cell=>Convert.ToString(cell.Value).Contains(((DetectionDevice)row.Tag).Mac)),"raw MAC not customer-visible");
                    Check(row.Cells[2].ToolTipText.Contains(HardwareDeviceDiscovery.ControllerName) && !Convert.ToString(row.Cells[2].Value).Contains("DS600"),"custom component branding stable");
                    Check(row.Height<=grid.ClientSize.Height-grid.ColumnHeadersHeight,"one complete selectable row fits large-font viewport "+language+"/"+size+"/height="+row.Height+"/available="+(grid.ClientSize.Height-grid.ColumnHeadersHeight)+"/intro="+form.Controls.Find("DiscoveryIntroduction",true)[0].Height+"/note="+form.Controls.Find("DiscoveryStatus",true)[0].Height+"/actions="+form.Controls.Find("DiscoveryActions",true)[0].Height+"/grid="+grid.Size+"/form="+form.ClientSize);
                }
                grid.ClearSelection(); grid.CurrentCell=grid.Rows[2].Cells[0]; grid.Rows[2].Selected=true; Pump(); Check(!connect.Enabled && !form.TrySelect(DateTime.UtcNow),"occupied device cannot connect");
                grid.ClearSelection(); grid.CurrentCell=grid.Rows[1].Cells[0]; grid.Rows[1].Selected=true; Pump(); Check(connect.Enabled,"unbound explicit choice enabled; rows="+grid.SelectedRows.Count+"; current="+(grid.CurrentRow==null?-1:grid.CurrentRow.Index)+"; selected="+grid.Rows[1].Selected+"; fresh="+receipt.IsFresh(DateTime.UtcNow));
                Check(!form.TrySelect(receipt.ProducedUtc.AddSeconds(31)) && form.SelectedDevice==null,"expired list cannot select at click time");
                using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(Path.Combine(qa,"discovery-"+language+"-"+size.ToString(CultureInfo.InvariantCulture)+".png")); }
                Check(form.TrySelect(DateTime.UtcNow) && Object.ReferenceEquals(form.SelectedDevice,receipt.Devices[1]),"only clicked unbound receipt object returned");
                Check(!form.TrySelect(DateTime.UtcNow),"duplicate callback cannot select twice");
            }
            Console.WriteLine("ROUND_UI_"+language+"_"+size.ToString(CultureInfo.InvariantCulture)+"_PASS="+checks);
        }
    }
    static MainForm Main(string qa, out AppSettings settings)
    { settings=new AppSettings { DataRoot=qa }; return new MainForm(settings,new ApplicationState(),true); }
    static void MainGateFixtures(string qa)
    {
        AppSettings settings;
        using(var form=Main(qa,out settings))
        {
            form.ShowInTaskbar=false; form.Opacity=0; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Pump();
            Check(form.CanRefreshDetectionDevices(),"idle home permits explicit detector refresh even offline");
            foreach(string flag in new[]{"detectionSelectionBusy","firstBindingInProgress","settingsDialogActive","hardwareRefreshInProgress","hardwareDisconnectInProgress","hardwareConnectionQueued"})
            { Set(form,flag,true); Check(!form.CanRefreshDetectionDevices(),"discovery entry gate "+flag); Set(form,flag,false); }
            Set(form,"pendingHardwareNavigation",new Action<HardwareStatusSnapshot>(s=>{})); Check(!form.CanRefreshDetectionDevices(),"pending navigation cannot compete with discovery"); Set(form,"pendingHardwareNavigation",null);
            settings.SessionHardwareForcedOffline=true; Check(!form.CanRefreshDetectionDevices(),"safe-start offline gate preserved"); settings.SessionHardwareForcedOffline=false;
            using(var page=new UserControl()) { Set(form,"activeModule",page); Check(!form.CanRefreshDetectionDevices(),"active page cannot switch detector"); Set(form,"activeModule",null); }
            var notices=new List<string>(); form.DeviceConnectionNoticeForTests=notices.Add;
            var pending=new TaskCompletionSource<DetectionDeviceList>(); form.DiscoverDetectorsForTests=token=>{ discoveries++; return pending.Task; }; form.SelectDetectorForTests=receipt=>null;
            Call(form,"RefreshDetectionDevices"); Check((bool)Field(form,"detectionSelectionBusy") && !form.Enabled,"async discover disables owning page");
            Call(form,"RefreshDetectionDevices"); Check(discoveries==1,"double refresh cannot issue concurrent discovery");
            pending.SetResult(Receipt(DateTime.UtcNow)); Until(()=>!(bool)Field(form,"detectionSelectionBusy"));
            Check(form.Enabled && form.CanRefreshDetectionDevices(),"cancel selection restores UI and gate");
            Check(settings.Hardware.SelectedHubMac=="" && !settings.Hardware.Enabled,"discovery/cancel do not bind or enable hardware");
            Console.WriteLine("ROUND_10_MAIN_ENTRY_AND_CANCEL_PASS="+checks);
            foreach(int race in new[]{0,1,2,3})
            {
                pending=new TaskCompletionSource<DetectionDeviceList>(); form.DiscoverDetectorsForTests=token=>{ discoveries++; return pending.Task; };
                var selected=Receipt(DateTime.UtcNow).Devices[0]; var task=(Task)Call(form,"ConnectSelectedDetectionDevice",selected,CancellationToken.None);
                Check(!task.IsCompleted,"re-discovery is awaited before settings/hardware mutation");
                if(race==0) settings.SessionHardwareForcedOffline=true;
                if(race==1) Set(form,"hardwareConnectionQueued",true);
                if(race==2) Set(form,"hardwareRefreshInProgress",true);
                var live=Receipt(DateTime.UtcNow); if(race==3) live=HardwareDeviceDiscovery.Parse(Xml(Entry("AABBCCDDEEFF"),DateTime.UtcNow),DateTime.UtcNow);
                pending.SetResult(live); Until(()=>task.IsCompleted);
                Check(task.IsFaulted && settings.Hardware.SelectedHubMac=="" && !settings.Hardware.Enabled,"late changed state/device prevented commit race "+race);
                settings.SessionHardwareForcedOffline=false; Set(form,"hardwareConnectionQueued",false); Set(form,"hardwareRefreshInProgress",false);
            }
            Console.WriteLine("ROUND_11_REDISCOVERY_RACES_PASS="+checks);
            var stale=Receipt(DateTime.UtcNow.AddSeconds(-31)); form.DiscoverDetectorsForTests=token=>Task.FromResult(stale); int noticeCount=notices.Count;
            Call(form,"RefreshDetectionDevices"); Until(()=>!(bool)Field(form,"detectionSelectionBusy"));
            Check(notices.Count==noticeCount+1 && form.Enabled,"stale receipt reports reason and restores UI");
            form.Close();
        }
        Check(discoveries==5,"only expected injected discoveries occurred");
        using(var form=Main(qa,out settings))
        {
            form.ShowInTaskbar=false; form.Opacity=0; form.Show(); Pump();
            var pending=new TaskCompletionSource<DetectionDeviceList>(); int chosen=0;
            form.DiscoverDetectorsForTests=token=>pending.Task; form.SelectDetectorForTests=receipt=>{ chosen++; return null; };
            Call(form,"RefreshDetectionDevices"); Check((bool)Field(form,"detectionSelectionBusy"),"discovery callback genuinely pending before dispose");
            form.Dispose(); pending.SetResult(Receipt(DateTime.UtcNow)); Until(()=>!(bool)Field(form,"detectionSelectionBusy"));
            Check(chosen==0 && !settings.Hardware.Enabled && settings.Hardware.SelectedHubMac=="","late disposed-form callback cannot select or change settings");
        }
        Console.WriteLine("ROUND_12_FAIL_CLOSED_PASS="+checks);
    }
    static void Until(Func<bool> done)
    { var until=DateTime.UtcNow.AddSeconds(5); while(!done() && DateTime.UtcNow<until) Application.DoEvents(); Check(done(),"async injected operation completes"); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if(args.Length!=1) throw new ArgumentException("Pass an isolated QA directory.");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string qa=Path.Combine(Path.GetFullPath(args[0]),"ds-"+Guid.NewGuid().ToString("N").Substring(0,12)); Directory.CreateDirectory(qa);
            ParserFixtures(); IdentityFixtures(); LayoutFixtures(qa); MainGateFixtures(qa);
            Console.WriteLine("DETECTION_DEVICE_SELECTION_PASS="+checks+"; HARDWARE_COMMANDS=0; LIVE_SETTINGS_WRITES=0; QA="+qa); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
