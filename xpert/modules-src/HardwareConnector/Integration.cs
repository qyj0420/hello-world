using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Xml.Linq;
namespace Xiongxin.HvDacXpert {
    static partial class Integration {
        static string Root {get{return AppDomain.CurrentDomain.BaseDirectory;}}
        static string Config {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HV DAC Xpert","hardware.xml");}}
        public static XDocument Settings(){try{return XDocument.Load(Config);}catch{return new XDocument(new XElement("Hardware",new XAttribute("Enabled",false),new XAttribute("Mac","")));}}
        public static void Save(bool enabled,string mac){Directory.CreateDirectory(Path.GetDirectoryName(Config));new XDocument(new XElement("Hardware",new XAttribute("Enabled",enabled),new XAttribute("Mac",Normalize(mac)))).Save(Config);}
        static string Normalize(string mac){return (mac??"").Replace(":","").Replace("-","").Trim().ToUpperInvariant();}
        static XDocument Result(string state,string mac,string message){return new XDocument(new XElement("HardwareStatus",new XAttribute("Schema",1),new XAttribute("State",state),new XAttribute("Mac",mac),new XAttribute("Utc",DateTime.UtcNow.ToString("O")),new XAttribute("MaxAgeSeconds",30),new XElement("Message",message)));}
        public static XDocument Check(bool enabled,string mac,bool connect,string controllerLocation="",string acquisitionLocation="")
        {
            mac=Normalize(mac);
            if(!enabled)return Result("Disabled",mac,"Offline mode: no discovery, driver install or connection attempted.");
            if(!Regex.IsMatch(mac,"^[0-9A-F]{12}$"))return Result("NeedsConfiguration",mac,"Specify the selected hub MAC; never substitute a direct USB device.");
            bool scoped=!String.IsNullOrWhiteSpace(controllerLocation)||!String.IsNullOrWhiteSpace(acquisitionLocation);
            if(connect&&!scoped)return Result("NeedsConfiguration",mac,"Explicit Controller/Acquisition locations are required before connecting.");
            try {
                var hub=Backend.Scan("").SingleOrDefault(s=>s.Mac==mac);
                if(hub==null)return Result("Offline",mac,"Selected hub not found. Analysis remains available.");
                var modules=scoped ? SelectConfiguredModules(hub,controllerLocation,acquisitionLocation)
                    : hub.Devices.Where(u=>u.Vid==0x3063&&u.Pid==0x3135 || u.Vid==0x0CE9&&u.Pid==0x1218).ToList();
                if(connect)ConnectConfiguredModules(modules,Native.SxuptpGetDeviceStatus,ConnectSelectedModule);
                var end=DateTime.UtcNow.AddSeconds(connect?15:0);XDocument result;
                do {
                    bool ready=modules.Any(u=>u.Vid==0x3063)&&modules.Any(u=>u.Vid==0x0CE9)&&modules.All(ConnectionHealth.Ready);
                    result=Result(ready?"Ready":"NotReady",mac,ready?"Selected network modules passed PnP readiness. Acquisition interlocks still required.":"Network transport/driver not ready; open hardware configuration if needed.");
                    result.Root.Add(new XAttribute("Ip",hub.Ip));
                    foreach(var u in modules)
                    {
                        var identity=ModuleIdentity.Resolve(u);
                        var element=new XElement("Module",new XAttribute("Kind",u.Vid==0x3063?"Controller":"Acquisition"),new XAttribute("Location",u.Location),new XAttribute("Connected",ConnectionHealth.Connected(u)),new XAttribute("Ready",ConnectionHealth.Ready(u)));
                        if(!string.IsNullOrWhiteSpace(identity.Serial))element.Add(new XAttribute("Serial",identity.Serial));
                        if(!string.IsNullOrWhiteSpace(identity.TransportSerial))element.Add(new XAttribute("TransportSerial",identity.TransportSerial));
                        if(!string.IsNullOrWhiteSpace(identity.InstanceId))element.Add(new XAttribute("InstanceId",identity.InstanceId));
                        if(!string.IsNullOrWhiteSpace(identity.DeviceName))element.Add(new XAttribute("DeviceName",identity.DeviceName));
                        result.Root.Add(element);
                    }
                    if(ready||!connect)break;Thread.Sleep(500);
                }while(DateTime.UtcNow<end);
                return result;
            }catch(PicoInspectionBusyException ex){
                var busy=Result("Busy",mac,"Device inspection is temporarily unavailable while the device is in use.");
                busy.Root.Add(new XElement("InspectionDiagnostic",ex.Message));return busy;
            }catch(DllNotFoundException ex){return Result("MissingComponents",mac,ex.Message);}catch(Exception ex){return Result("Error",mac,ex.Message);}
        }
        internal static List<Usb> SelectConfiguredModules(Server hub,string controllerLocation,string acquisitionLocation)
        {
            controllerLocation=(controllerLocation??"").Trim().ToUpperInvariant();
            acquisitionLocation=(acquisitionLocation??"").Trim().ToUpperInvariant();
            IPAddress address;
            if(hub==null||!Regex.IsMatch(Normalize(hub.Mac),"^[0-9A-F]{12}$")||
                !IPAddress.TryParse(hub.Ip,out address)||address.AddressFamily!=AddressFamily.InterNetwork||
                !ValidDisconnectLocation(controllerLocation)||!ValidDisconnectLocation(acquisitionLocation)||controllerLocation==acquisitionLocation)
                throw new InvalidDataException("Selected hub and distinct module locations are required.");
            var selected=new List<Usb>{DisconnectTarget(hub,controllerLocation,0x3063,0x3135),DisconnectTarget(hub,acquisitionLocation,0x0CE9,0x1218)};
            string[] ports=selected.SelectMany(u=>u.Port.Split(',')).ToArray();
            if(ports.Distinct(StringComparer.Ordinal).Count()!=ports.Length)throw new InvalidDataException("Target ports overlap.");
            return selected;
        }
        internal static void ConnectConfiguredModules(List<Usb> selected,Func<uint,string,uint> readState,Action<Usb> connect)
        {
            // Preflight all selected ports before any command; never connect an
            // unconfigured same-model device or steal another computer's port.
            if(selected==null||selected.Count!=2)throw new InvalidDataException("Exactly two configured modules are required.");
            foreach(Usb target in selected)RequireConnectStates(target,readState);
            foreach(Usb target in selected)
            {
                uint[] states=RequireConnectStates(target,readState);
                if(states.All(state=>state==2))continue;
                connect(target);
            }
        }
        static uint[] RequireConnectStates(Usb target,Func<uint,string,uint> readState)
        {
            uint[] states=target.Port.Split(',').Select(port=>readState(target.Server.RawIp,port)).ToArray();
            if(states.Any(state=>state!=1&&state!=2))throw new IOException("Target connection state is unknown or owned by another computer: "+target.Location);
            return states;
        }
        static void ConnectSelectedModule(Usb target)
        {
            string exe=Path.Combine(Root,"runtime","SxConnectDev.exe");
            using(var process=Process.Start(new ProcessStartInfo(exe,"/C /I"+target.Server.Ip+" /E"+Backend.FormatMac(Normalize(target.Server.Mac))+" /P"+target.Port)
                {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden}))
            {
                if(process==null)throw new IOException("Connection command did not start.");
                if(!process.WaitForExit(10000)){try{process.Kill();process.WaitForExit(2000);}catch{}throw new TimeoutException("Connection command timed out.");}
                // Exit codes alone never establish readiness; Check verifies
                // selected transport/PnP states after both requested commands.
            }
        }
        // This narrow command does not change persistent bindings, drivers or
        // names. Never use Backend.Reconnect: it edits the binding registry.
        internal static XDocument Disconnect(bool enabled, string mac, string controllerLocation, string acquisitionLocation,
            Func<string, List<Server>> scan = null, Func<uint, string, uint> readState = null,
            Func<Usb, int> release = null, Action<int> delay = null, string hubIp = "")
        {
            mac = Normalize(mac);
            controllerLocation = (controllerLocation ?? "").Trim().ToUpperInvariant();
            acquisitionLocation = (acquisitionLocation ?? "").Trim().ToUpperInvariant();
            if (!enabled) return Result("DisconnectFailed", mac, "Hardware mode is disabled; no disconnect was attempted.");
            if (!Regex.IsMatch(mac, "^[0-9A-F]{12}$") || !ValidDisconnectLocation(controllerLocation) ||
                !ValidDisconnectLocation(acquisitionLocation) || controllerLocation == acquisitionLocation)
                return Result("DisconnectFailed", mac, "Specify one hub MAC and distinct Controller/Acquisition locations.");
            IPAddress hint;
            bool targeted = !String.IsNullOrWhiteSpace(hubIp);
            if (targeted && (!IPAddress.TryParse(hubIp, out hint) || hint.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(hint) || hint.Equals(IPAddress.Any) || hint.Equals(IPAddress.Broadcast)))
                return Result("DisconnectFailed", mac, "Invalid selected device address; no disconnect was attempted.");
            scan = scan ?? (targeted ? (Func<string, List<Server>>)Backend.InspectSingle : Backend.Scan);
            readState = readState ?? Native.SxuptpGetDeviceStatus;
            release = release ?? ReleaseSelectedModule; delay = delay ?? Thread.Sleep;
            var watch = Stopwatch.StartNew();
            var timing = new XElement("DisconnectTiming", new XAttribute("Targeted", targeted));
            string stage = "Identity";
            Action<string> checkpoint = name => timing.Add(new XElement("Stage", new XAttribute("Name", name),
                new XAttribute("ElapsedMs", watch.ElapsedMilliseconds)));
            Func<XDocument, XDocument> finish = result => {
                checkpoint(stage); timing.SetAttributeValue("TotalMs", watch.ElapsedMilliseconds);
                result.Root.Add(timing); return result;
            };
            try
            {
                Server hub = scan(targeted ? hubIp : "").SingleOrDefault(s => Normalize(s.Mac) == mac);
                if (hub == null) throw new InvalidOperationException("The selected hub is not discoverable; disconnection cannot be verified.");
                IPAddress ip;
                if (!IPAddress.TryParse(hub.Ip, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                    throw new InvalidDataException("Invalid hub address.");
                if (targeted && !ip.Equals(IPAddress.Parse(hubIp)))
                    throw new InvalidDataException("The selected device address changed; no disconnect was attempted.");
                Usb controller = DisconnectTarget(hub, controllerLocation, 0x3063, 0x3135);
                Usb acquisition = DisconnectTarget(hub, acquisitionLocation, 0x0CE9, 0x1218);
                Usb[] selected = { controller, acquisition };
                string[] ports = selected.SelectMany(u => u.Port.Split(',')).ToArray();
                if (ports.Distinct(StringComparer.Ordinal).Count() != ports.Length) throw new InvalidDataException("Target ports overlap.");
                checkpoint(stage); stage = "Ownership";
                // Validate every target before the first command, including a
                // module already connected to another computer.
                foreach (Usb target in selected) ReadDisconnectStates(target, readState, true);
                checkpoint(stage);
                foreach (Usb target in selected)
                {
                    stage = target == controller ? "ControllerRelease" : "AcquisitionRelease";
                    uint[] states = ReadDisconnectStates(target, readState, true);
                    if (states.All(s => s == 1)) { checkpoint(stage); continue; }
                    int code = release(target);
                    // This application's existing release path accepts these
                    // codes. A successful exit alone still proves nothing.
                    if (code != 2 && code != 5 && code != 11)
                        throw new IOException("Disconnect command failed at " + target.Location + "; exit=" + code + ".");
                    checkpoint(stage);
                }
                stage = "ReleaseVerification";
                uint[][] last = null;
                for (int attempt = 0; attempt < 21; attempt++)
                {
                    last = selected.Select(u => ReadDisconnectStates(u, readState, false)).ToArray();
                    if (last.All(states => states.All(s => s == 1)))
                    {
                        var result = Result("Disconnected", mac, "Both selected modules were verified not connected. Persistent bindings were not changed.");
                        for (int i = 0; i < selected.Length; i++)
                            result.Root.Add(new XElement("Module", new XAttribute("Kind", i == 0 ? "Controller" : "Acquisition"),
                                new XAttribute("Location", selected[i].Location), new XAttribute("Connected", false), new XAttribute("Ready", false),
                                new XAttribute("ConnectionState", 1)));
                        return finish(result);
                    }
                    if (last.Any(states => states.Any(s => s == 3))) throw new IOException("A target is now in use by another computer; no further disconnect was attempted.");
                    if (attempt < 20) delay(250);
                }
                throw new IOException("Disconnect could not be verified; port states=" + String.Join(";", last.Select(s => String.Join(",", s))) + ".");
            }
            catch (Exception error) { return finish(Result("DisconnectFailed", mac, error.Message)); }
        }
        static bool ValidDisconnectLocation(string value)
        { return value.Length > 0 && value.Length <= 64 && Regex.IsMatch(value, "^[A-Z0-9_.:-]+$"); }
        static Usb DisconnectTarget(Server hub, string location, ushort vid, ushort pid)
        {
            Usb[] matches = hub.Devices.Where(u => String.Equals((u.Location ?? "").Trim(), location, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].Vid != vid || matches[0].Pid != pid || matches[0].Server != hub)
                throw new InvalidDataException("Selected module identity/location is missing or ambiguous: " + location);
            Usb target = matches[0];
            if (String.IsNullOrEmpty(target.Port) || target.Port.Length > 128 || !Regex.IsMatch(target.Port, "^[A-Za-z0-9_.:-]+(,[A-Za-z0-9_.:-]+)*$"))
                throw new InvalidDataException("Invalid selected module port.");
            return target;
        }
        static uint[] ReadDisconnectStates(Usb target, Func<uint, string, uint> readState, bool requireActionable)
        {
            // 2/3 retain the existing local/other-PC contract. Treat 0 as
            // unverifiable, never as success; state 1 release confirmation still
            // requires deployed-SDK / field validation before hardware acceptance.
            uint[] values = target.Port.Split(',').Select(p => readState(target.Server.RawIp, p)).ToArray();
            if (values.Any(s => s > 3 || (requireActionable && s == 0)))
                throw new IOException("Target connection state is unknown; no disconnect allowed: " + target.Location);
            if (requireActionable && values.Any(s => s == 3))
                throw new IOException("Target is in use by another computer; it will not be disconnected: " + target.Location);
            return values;
        }
        static int ReleaseSelectedModule(Usb target)
        {
            string executable = Path.Combine(Root, "runtime", "SxConnectDev.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Missing SxConnectDev.exe.", executable);
            using (var process = Process.Start(new ProcessStartInfo(executable,
                "/D /I" + target.Server.Ip + " /E" + Backend.FormatMac(Normalize(target.Server.Mac)) + " /P" + target.Port)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                if (process == null) throw new IOException("Disconnect process did not start.");
                if (!process.WaitForExit(10000)) { try { process.Kill(); process.WaitForExit(2000); } catch { } throw new TimeoutException("Disconnect command timed out."); }
                return process.ExitCode;
            }
        }
        [STAThread]static int Main(string[] args){
            int discoveryExit;
            if(TryDiscoveryCommand(args,out discoveryExit))return discoveryExit;
            Application.EnableVisualStyles();Native.SetDllDirectory(Path.Combine(Root,"runtime"));
            if(args.Contains("--inspect")||args.Contains("--connect")||args.Contains("--disconnect")){
                string mac=args.FirstOrDefault(a=>a.StartsWith("--mac="));string output=args.FirstOrDefault(a=>a.StartsWith("--output="));
                if(output==null)return 40;
                if(args.Count(a=>a=="--inspect"||a=="--connect"||a=="--disconnect")!=1)return 40;
                string controller=args.FirstOrDefault(a=>a.StartsWith("--controller-location="));
                string acquisition=args.FirstOrDefault(a=>a.StartsWith("--acquisition-location="));
                string hubIp=args.FirstOrDefault(a=>a.StartsWith("--hub-ip="));
                string artSerial=args.FirstOrDefault(a=>a.StartsWith("--art-serial="));
                string picoSerial=args.FirstOrDefault(a=>a.StartsWith("--pico-serial="));
                if(args.Contains("--local-only")&&!args.Contains("--inspect"))return 40;
                var result=args.Contains("--local-only")
                    ? CheckLocal(args.Contains("--enabled"),mac==null?"":mac.Substring(6),controller==null?"":controller.Substring(22),acquisition==null?"":acquisition.Substring(23),artSerial==null?"":artSerial.Substring(13),picoSerial==null?"":picoSerial.Substring(14))
                    : args.Contains("--disconnect")
                    ? Disconnect(args.Contains("--enabled"),mac==null?"":mac.Substring(6),controller==null?"":controller.Substring(22),acquisition==null?"":acquisition.Substring(23),hubIp:hubIp==null?"":hubIp.Substring(9))
                    : Check(args.Contains("--enabled"),mac==null?"":mac.Substring(6),args.Contains("--connect"),controller==null?"":controller.Substring(22),acquisition==null?"":acquisition.Substring(23));
                try{string path=Path.GetFullPath(output.Substring(9));Directory.CreateDirectory(Path.GetDirectoryName(path));result.Save(path);}catch{return 40;}
                string state=(string)result.Root.Attribute("State");return state=="Ready"||state=="Disconnected"?0:state=="Disabled"?20:state=="MissingComponents"?30:state=="Error"||state=="DisconnectFailed"?40:10;
            }
            if(args.Contains("--offline"))Save(false,"");
            // No standalone developer window. The host application owns any future integration UI.
            return 20;
        }
        sealed class IntegrationForm:Form {
            readonly CheckBox enabled=new CheckBox{Text="启用硬件（取消：跳过硬件，离线使用）",AutoSize=true};
            readonly TextBox mac=new TextBox{Width=260};readonly TextBox log=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
            readonly FlowLayoutPanel actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=48};bool busy;
            public IntegrationForm(){
                Text="HV DAC Xpert · 独立硬件联调";ClientSize=new Size(940,530);Font=new Font("Microsoft YaHei UI",10);StartPosition=FormStartPosition.CenterScreen;
                string icon=Path.Combine(Root,"assets","xiongxin.ico");Icon=File.Exists(icon)?new Icon(icon):SystemIcons.Application;
                var top=new FlowLayoutPanel{Dock=DockStyle.Top,Height=90,Padding=new Padding(12)};
                top.Controls.Add(enabled);top.Controls.Add(new Label{Text="指定 DS600 MAC",AutoSize=true});top.Controls.Add(mac);
                Controls.Add(log);Controls.Add(actions);Controls.Add(top);
                var cfg=Settings();enabled.Checked=(bool?)cfg.Root.Attribute("Enabled")??false;mac.Text=(string)cfg.Root.Attribute("Mac")??"";
                Add("保存 / 跳过",()=>{Save(enabled.Checked,mac.Text);log.Text=enabled.Checked?"已保存。仅连接指定 MAC，不操作直连采集卡。":"已跳过；下次启动不搜索、不连接硬件。";});
                Add("检查状态",async()=>await Run(false));Add("连接并校验",async()=>await Run(true));
                Add("首次硬件配置",()=>Launch("HV-DAC-Xpert.exe",""));
                Add("驱动安装",()=>{using(var d=new OpenFileDialog{Filter="安装程序|*.exe",Title="选择 HV DAC Xpert 完整驱动安装包"})if(d.ShowDialog(this)==DialogResult.OK)Process.Start(new ProcessStartInfo(d.FileName){UseShellExecute=true,Verb="runas"});});
                Shown+=async delegate{if(enabled.Checked)await Run(true);else log.Text="离线模式。无任何自动安装或硬件连接。";};
                FormClosing+=delegate(object s,FormClosingEventArgs e){if(busy){e.Cancel=true;log.AppendText("\r\n当前请求有界等待中，请稍候。不会自动加压。");}};
            }
            void Add(string text,Action action){var b=new Button{Text=text,AutoSize=true,Height=36};b.Click+=delegate{try{action();}catch(Exception ex){log.Text=ex.Message;}};actions.Controls.Add(b);}
            void Launch(string name,string args){if(!enabled.Checked){log.Text="请先启用硬件模式。";return;}Process.Start(new ProcessStartInfo(Path.Combine(Root,name),args){UseShellExecute=true,Verb="runas"});}
            async Task Run(bool connect){if(busy)return;busy=true;actions.Enabled=false;enabled.Enabled=false;mac.Enabled=false;try{bool en=enabled.Checked;string m=mac.Text;log.Text="检查中…";var result=await Task.Run(()=>Check(en,m,connect));log.Text=result.ToString();}catch(Exception ex){log.Text=ex.Message;}finally{busy=false;actions.Enabled=true;enabled.Enabled=true;mac.Enabled=true;}}
        }
    }
}
