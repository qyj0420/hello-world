using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Xiongxin.HvDacXpert
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            L10n.Initialize(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Native.SetDllDirectory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime"));
            if (args.Length > 0 && (args[0] == "--names-preview" || args[0] == "--names-apply" || args[0] == "--names-restore"))
            {
                try { DeviceNames.Apply(args[0] == "--names-preview", args[0] == "--names-restore"); }
                catch { Environment.ExitCode = 1; }
                return;
            }
            if(args.Contains("--art-preview"))
            {
                try{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"art-preview.txt"),ArtAliases.Preview(),Encoding.UTF8);}
                catch(Exception ex){File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"art-preview.txt"),ex.ToString(),Encoding.UTF8);Environment.ExitCode=1;}
                return;
            }
            if (args.Length > 0 && args[0] == "--scan")
            {
                try
                {
                    var servers = Backend.Scan(args.Length > 1 ? args[1] : "");
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan-result.txt"),
                        "信号汇集器=" + servers.Count + Environment.NewLine +
                        string.Join(Environment.NewLine, servers.Select(s => Display.Hub + " 采集模块=" + s.Devices.Count + " " + string.Join(", ", s.Devices.Select(u => Display.Device(u.Name))))), Encoding.UTF8);
                }
                catch (Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scan-result.txt"), Display.Error(ex)); Environment.ExitCode = 1; }
                return;
            }
            using (var form = new MainForm(args.Contains("--first-run"),args.Contains("--smoke")))
            {
                if (args.Length > 0 && args[0] == "--smoke")
                {
                    form.Show(); form.ShowDemo(); Application.DoEvents();
                    using (var bmp = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        bmp.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-preview.png"));
                    }
                    return;
                }
                Xiongxin.OWTS.Ui.DialogPlacement.Install();
                form.Shown += async delegate { await form.RefreshDevices(); };
                Application.Run(form);
            }
        }
    }

    sealed class Server
    {
        public string Mac, Ip;
        public uint RawIp;
        public List<Usb> Devices = new List<Usb>();
    }
    sealed class Usb
    {
        public Server Server;
        public string Name, Port, Location;
        public ushort Vid, Pid, Status;
        public string Key { get { return Server.Mac + "/" + Location + "/" + Vid + "/" + Pid; } }
        public string Prefix { get { return Vid.ToString("X4") + Pid.ToString("X4") + "&" + Server.Mac + "&" + Location + "&"; } }
    }

    sealed class MainForm : Form
    {
        readonly TreeView tree = new TreeView();
        readonly Button refresh = new Button(), apply = new Button(), reconnect = new Button();
        readonly Label status = new Label(), count = new Label();
        readonly Label hubLamp=new Label(), artLamp=new Label(), scopeLamp=new Label();
        readonly System.Windows.Forms.Timer healthTimer=new System.Windows.Forms.Timer { Interval=10000 };
        List<Usb> monitored=new List<Usb>(); bool firstRun;
        BindingProgress progressWindow;
        ComboBox languageSelector; WorkflowState lastHealth=new WorkflowState(); bool previewMode;
        readonly TextBox log = new TextBox();
        readonly CheckBox clean = new CheckBox();
        readonly CheckBox persistent = new CheckBox();
        readonly TextBox targetIp = new TextBox();
        bool busy, checking;
        readonly System.Windows.Forms.Timer nameTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        List<Server> snapshot = new List<Server>();
        readonly Color blue = Color.FromArgb(22, 76, 142);

        public MainForm(bool autoFirstRun=false,bool preview=false)
        {
            previewMode=preview;
            firstRun=autoFirstRun;
            Text = "XIONGXIN · HV DAC Xpert";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(1120, 830); MinimumSize = new Size(1080, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(244, 247, 251);
            Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "xiongxin.ico"));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24,4,24,24), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            Controls.Add(layout);
            var header = new Panel { Dock = DockStyle.Fill };
            persistent.Text=L10n.PersistentModeText;persistent.Checked=Backend.LoadMode();persistent.AutoSize=true;persistent.Location=new Point(0,103);
            Workflow.RequirePersistentBinding=persistent.Checked;
            header.Controls.Add(persistent);
            L10n.Changed+=delegate{if(!IsDisposed)persistent.Text=L10n.PersistentModeText;};
            var brand = new PictureBox { Size = new Size(400, 90), Location = new Point(0, 0), SizeMode = PictureBoxSizeMode.Zoom };
            brand.Image = Image.FromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "xiongxin-original.png"));
            header.Controls.Add(brand);
            header.Controls.Add(new Label { Text = "HV DAC Xpert", Font = new Font(Font.FontFamily, 19, FontStyle.Bold), ForeColor = blue, AutoSize = true, Location = new Point(420, 17) });
            targetIp.Text = "192.168.2.5";
            layout.Controls.Add(header, 0, 0);
            status.Text = "准备检测信号汇集器"; status.ForeColor = blue; status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 1);
            tree.Dock = DockStyle.Fill; tree.CheckBoxes = true; tree.HideSelection = false; tree.ItemHeight = 34; tree.BorderStyle = BorderStyle.FixedSingle;
            tree.AfterCheck += delegate(object sender, TreeViewEventArgs e)
            {
                if (checking) return;
                checking = true;
                if (e.Node.Tag is Server) foreach (TreeNode child in e.Node.Nodes) child.Checked = e.Node.Checked;
                else if (e.Node.Parent != null) e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
                checking = false; monitored.Clear(); ShowHealth(new WorkflowState()); status.Text="选择已变更，请点击自动连接保存并重新校验。"; UpdateButtons();
            };
            layout.Controls.Add(tree, 0, 2);
            clean.Text = "同时取消本次未发现的旧汇集器 / 采集模块自动绑定"; clean.AutoSize = true; clean.Dock = DockStyle.Fill;
            layout.Controls.Add(clean, 0, 3);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            apply.Text = "自动连接 / Apply"; apply.Size = new Size(250, 42); apply.BackColor = blue; apply.ForeColor = Color.White; apply.FlatStyle = FlatStyle.Flat; apply.Enabled = false;
            refresh.Text = "刷新设备"; refresh.Size = new Size(180, 42); refresh.FlatStyle = FlatStyle.Flat; refresh.BackColor = Color.White;
            reconnect.Text = "重新连接"; reconnect.Size = new Size(200, 42); reconnect.FlatStyle = FlatStyle.Flat; reconnect.BackColor = Color.White;
            count.AutoSize = true; count.Margin = new Padding(0, 13, 20, 0); count.Text = "已勾选 0 个模块";
            actions.Controls.Add(apply); actions.Controls.Add(reconnect); actions.Controls.Add(refresh); actions.Controls.Add(count);
            layout.Controls.Add(actions, 0, 4);
            layout.Controls.Add(new Label { Text = "操作记录（连接成功后，请等待设备自身驱动初始化完成）", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 5);
            log.Dock = DockStyle.Fill; log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.BackColor = Color.White; log.Font = new Font("Microsoft YaHei UI", 9);
            layout.Controls.Add(log, 0, 6);
            var lamps=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1};
            foreach(var lamp in new[]{hubLamp,artLamp,scopeLamp}) {lamps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33f)); lamp.Dock=DockStyle.Fill;lamp.TextAlign=ContentAlignment.MiddleLeft;lamps.Controls.Add(lamp);}
            layout.Controls.Add(lamps,0,7); ShowHealth(new WorkflowState());
            refresh.Click += async delegate { await RefreshDevices(); };
            apply.Click += async delegate { await ApplySelection(); };
            reconnect.Click += async delegate
            {
                if (MessageBox.Show(this, L10n.T("stopConfirm"), "HV-DAC Xpert 600", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    await ApplySelection(true);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "操作进行中，请等待结束后关闭窗口。"; } };
            nameTimer.Tick += async delegate
            {
                nameTimer.Stop();
                if(previewMode)return;
                if (busy) { nameTimer.Start(); return; }
                try { await Task.Run(() => DeviceNames.Apply(false)); }
                catch { WriteLog("设备显示名称暂未同步，可稍后刷新或联系售后。"); }
            };
            healthTimer.Tick+=async delegate {
                if(busy||previewMode) return;
                if(firstRun) { await RefreshDevices(); return; }
                if(monitored.Count==0) return;
                SetBusy(true);
                try { var state=await Task.Run(()=>Workflow.Inspect(monitored)); ShowHealth(state); status.Text=state.Message; }
                catch { ShowHealth(new WorkflowState()); status.Text="状态校验暂时失败，请刷新重试。"; }
                finally {SetBusy(false);}
            };
            Shown+=delegate{healthTimer.Start();};
            FormClosed += delegate { nameTimer.Dispose(); healthTimer.Dispose(); if(progressWindow!=null)progressWindow.Dispose(); };
            L10n.Attach(this);
            languageSelector=L10n.AddSelector(this,delegate{
                var checks=tree.Nodes.Cast<TreeNode>().SelectMany(n=>n.Nodes.Cast<TreeNode>()).ToDictionary(n=>((Usb)n.Tag).Key,n=>n.Checked);
                Populate(snapshot,checks);ShowHealth(lastHealth);log.Clear();
                if(!previewMode)nameTimer.Start();
            });
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0219 && !IsDisposed) { nameTimer.Stop(); nameTimer.Start(); }
        }
        void ShowHealth(WorkflowState value)
        {
            lastHealth=value;
            Lamp(hubLamp,"Xpert 绑定",value.Hub); Lamp(artLamp,"art 驱动加载",value.Art); Lamp(scopeLamp,"scope 驱动加载",value.Scope);
        }
        static void Lamp(Label lamp,string title,bool ready) {lamp.Text="● "+title+"："+(ready?"通过":"待完成");lamp.ForeColor=ready?Color.FromArgb(18,130,74):Color.FromArgb(155,96,10);}
        string lastCustomerMessage;
        void WriteLog(string message) {
            string line=DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine;
            string customer=L10n.CustomerMessage(message);
            if(!string.IsNullOrEmpty(customer)&&customer!=lastCustomerMessage){log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+customer+Environment.NewLine);lastCustomerMessage=customer;}
            if(previewMode)return;
            try {string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"XIONGXIN","HvDacXpert","InstallLogs");Directory.CreateDirectory(dir);File.AppendAllText(Path.Combine(dir,"binding-"+DateTime.Today.ToString("yyyyMMdd")+".log"),line,Encoding.UTF8);} catch(IOException){} catch(UnauthorizedAccessException){}
        }
        void SetBusy(bool value) { busy = value;if(languageSelector!=null)languageSelector.Enabled=!value; refresh.Enabled = !value; targetIp.Enabled = !value; tree.Enabled = !value; clean.Enabled = !value; UseWaitCursor = value; UpdateButtons(); }
        void UpdateButtons()
        {
            int selected = tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).Count(n => n.Checked);
            count.Text = "已勾选 " + selected + " 个模块";
            apply.Enabled = !busy && snapshot.Any(s => s.Devices.Count > 0);
            reconnect.Enabled = !busy && selected > 0;
            apply.Text = selected == 0 ? "保存取消勾选 / Apply" : "自动连接 / Apply";
        }
        void Populate(List<Server> servers, Dictionary<string, bool> checks)
        {
            checking = true; tree.BeginUpdate(); tree.Nodes.Clear(); snapshot = servers;
            foreach (var s in servers)
            {
                var parent = new TreeNode(Display.Hub + (servers.Count > 1 ? "  #" + (servers.IndexOf(s) + 1) : "")) { Tag = s };
                foreach (var u in s.Devices)
                {
                    bool selected;
                    if (!checks.TryGetValue(u.Key, out selected)) selected = true;
                    string label = Display.Device(u.Name);
                    if (s.Devices.Count(d => Display.Device(d.Name) == label) > 1) label += "  #" + (s.Devices.IndexOf(u) + 1);
                    parent.Nodes.Add(new TreeNode(label) { Tag = u, Checked = selected });
                }
                parent.Checked = s.Devices.Count > 0 && parent.Nodes.Cast<TreeNode>().All(n => n.Checked);
                if (s.Devices.Count == 0) parent.Text += "  — "+L10n.T("pending");
                tree.Nodes.Add(parent); parent.Expand();
            }
            tree.EndUpdate(); checking = false; UpdateButtons();
        }
        public async Task RefreshDevices()
        {
            if (busy) return;
            var checks = tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).ToDictionary(n => ((Usb)n.Tag).Key, n => n.Checked);
            SetBusy(true); status.Text = "正在实时搜索信号汇集器…";
            ShowHealth(new WorkflowState()); monitored.Clear();
            try
            {
                string ip = targetIp.Text.Trim();
                var found = await Task.Run(() => Backend.Scan(ip)); Populate(found, checks);
                status.Text = found.Count == 0 ? "未发现在线信号汇集器，请检查电源、网线及网段后刷新。" : "发现 " + found.Count + " 台信号汇集器，" + found.Sum(s => s.Devices.Count) + " 个采集模块。";
                WriteLog(status.Text);
                monitored=tree.Nodes.Cast<TreeNode>().SelectMany(n=>n.Nodes.Cast<TreeNode>()).Where(n=>n.Checked).Select(n=>(Usb)n.Tag).ToList();
                var health=await Task.Run(()=>Workflow.Inspect(monitored)); ShowHealth(health);
                try { await Task.Run(() => DeviceNames.Apply(false)); }
                catch { WriteLog("设备显示名称暂未同步，可稍后刷新或联系售后。"); }
            }
            catch (Exception ex) { Populate(new List<Server>(), checks); status.Text = "检测失败"; WriteLog(Display.Error(ex)); }
            finally { SetBusy(false); }
            if(firstRun && snapshot.Count==1 && snapshot[0].Devices.Count>0) {firstRun=false;await ApplySelection();}
            else if(firstRun && snapshot.Count>1) {firstRun=false;status.Text="发现多个汇集器，请选择要绑定的模块并点击自动连接。";}
            else if(firstRun) status.Text="等待硬件接入；每 10 秒重新检测，也可手动刷新。";
        }
        async Task ApplySelection(bool recover = false)
        {
            if (busy) return;
            var desired = tree.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>()).ToDictionary(n => ((Usb)n.Tag).Key, n => n.Checked);
            bool cleanup = clean.Checked;
            Backend.PersistentMode=persistent.Checked;Workflow.RequirePersistentBinding=persistent.Checked;
            persistent.Enabled=false;
            string ip = targetIp.Text.Trim();
            SetBusy(true); status.Text = "正在校验在线状态、保存勾选并连接…";
            firstRun=false; ShowHealth(new WorkflowState());
            if(progressWindow!=null) {progressWindow.Dispose();progressWindow=null;}
            progressWindow=new BindingProgress(Icon);progressWindow.Show(this);
            var cancellation=new CancellationTokenSource();
            progressWindow.StopRequested+=delegate{cancellation.Cancel();};
            var token=cancellation.Token;
            Action<string> report=message=>{ if(IsDisposed)return; Invoke((Action)(()=>{WriteLog(message);if(progressWindow!=null && !progressWindow.IsDisposed)progressWindow.Report(message);})); };
            Action<WorkflowState> stateReport=value=>{ if(!IsDisposed)Invoke((Action)(()=>ShowHealth(value))); };
            try
            {
                report("正在保存绑定设置并建立通信连接，请勿关闭窗口…");
                WorkflowState verified=new WorkflowState();
                for(int attempt=0;attempt<2;attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if(attempt==1)
                        {
                            report(L10n.T("timeout")+" → "+L10n.T("refresh")+" → "+L10n.T("reconnect")+" (1/1)");
                            var refreshed=await Task.Run(()=>Backend.Scan(ip));
                            token.ThrowIfCancellationRequested();
                            // Retain the original identities and checkboxes; never select a newly found device.
                            snapshot=refreshed;Populate(refreshed,desired);
                        }
                        var result=await Task.Run(()=>(recover||attempt==1)?Backend.Reconnect(snapshot,desired,cleanup,ip,token):Backend.Apply(snapshot,desired,cleanup,ip,token));
                        foreach(var line in result)WriteLog(line);
                        token.ThrowIfCancellationRequested();
                        var live=await Task.Run(()=>Backend.Scan(ip));
                        token.ThrowIfCancellationRequested();
                        monitored=live.SelectMany(s=>s.Devices).Where(u=>desired.ContainsKey(u.Key)&&desired[u.Key]).ToList();
                        if(monitored.Count!=desired.Count(p=>p.Value))throw new InvalidOperationException("设备在连接过程中已离线，请重新刷新。");
                        verified=await Task.Run(()=>Workflow.LoadAndCheck(monitored,report,stateReport,token));
                        if(verified.AllSelectedReady || !desired.Values.Any(v=>v))break;
                    }
                    catch(BindingTimeoutException){if(attempt==1)throw;}
                }
                ShowHealth(verified);
                nameTimer.Start();
                status.Text=monitored.Count==0?"已保存取消勾选，未执行系统绑定校验。":verified.Message;
                progressWindow.Finish(status.Text);
            }
            catch(OperationCanceledException) {status.Text=L10n.StopWaitText;ShowHealth(new WorkflowState());WriteLog(status.Text);progressWindow.Finish(status.Text);}
            catch (Exception ex) { status.Text = "操作未完成，请查看下方原因。"; ShowHealth(new WorkflowState());WriteLog(Display.Error(ex));progressWindow.Finish(status.Text+" "+Display.Error(ex)); }
            finally { cancellation.Dispose();persistent.Enabled=true;SetBusy(false); }
        }
        public void ShowDemo()
        {
            var s = new Server { Mac = "1CBCEC5A9C69", Ip = "192.168.2.5" };
            s.Devices.Add(new Usb { Server = s, Name = "ART-Control ART Device", Location = "1", Vid = 1, Pid = 1 });
            s.Devices.Add(new Usb { Server = s, Name = "Pico Technology Ltd PicoScope 5000 Series PC Oscilloscope", Location = "2", Vid = 2, Pid = 2 });
            Populate(new List<Server> { s }, new Dictionary<string, bool>());
            status.Text = "界面演示 · 设备名称为示例，未执行连接";
            WriteLog("演示数据仅用于界面检查；正常启动时显示实时发现结果。");
        }
    }

    static class Backend
    {
        public static bool PersistentMode=true;
        static string ModeFile {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XIONGXIN","HvDacXpert","persistent-mode.txt");}}
        public static bool LoadMode(){try{return !File.Exists(ModeFile) || File.ReadAllText(ModeFile).Trim()!="manual";}catch{return true;}}
        static void ReloadAutoConnection(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using(var service=new ServiceController("SX Virtual Link Lite"))
            {
                // Official settings references SvlRestart.exe. Reload only this service, never USB/Pico drivers.
                try
                {
                    if(service.Status!=ServiceControllerStatus.Stopped)
                    {
                        service.Stop();var end=DateTime.UtcNow.AddSeconds(15);
                        while(service.Status!=ServiceControllerStatus.Stopped){Thread.Sleep(100);service.Refresh();if(DateTime.UtcNow>=end)throw new BindingTimeoutException();}
                    }
                }
                finally
                {
                    service.Refresh();
                    if(service.Status==ServiceControllerStatus.Stopped){service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(15));}
                }
            }
            token.ThrowIfCancellationRequested();
        }
        const string RegistryPath = @"SOFTWARE\SILEX\SX Virtual Link Lite\Svlscapi\Device";
        public static string LastSearch = "";
        static readonly string CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIONGXIN", "HvDacXpert", "discovered-ip.txt");
        public static bool IsBound(Usb u)
        {
            using(var hive=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var root=hive.OpenSubKey(RegistryPath))
            {
                if(root==null)return false;
                var names=root.GetSubKeyNames().Where(n=>n.StartsWith(u.Prefix,StringComparison.OrdinalIgnoreCase)).ToList();
                return names.Count>0 && names.All(n=>{using(var key=root.OpenSubKey(n)){var value=key==null?null:key.GetValue("Property") as byte[];return value!=null && value.Length==2 && value[0]==11 && value[1]==0;}});
            }
        }
        public static string FormatMac(string mac) { return string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))); }
        static string ReadString(IntPtr p, int offset, int length)
        {
            var bytes = new byte[length]; Marshal.Copy(IntPtr.Add(p, offset), bytes, 0, length);
            int end = Array.IndexOf(bytes, (byte)0); return Encoding.Default.GetString(bytes, 0, end < 0 ? length : end);
        }
        // Narrow revalidation for release of an established session. Never scans
        // other adapters, broadcasts or historical addresses, and never writes cache.
        public static List<Server> InspectSingle(string requestedIp)
        {
            IPAddress address;
            if (!IPAddress.TryParse(requestedIp, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                throw new ArgumentException("Invalid selected device address.");
            uint ip = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var result = new List<Server>();
            IntPtr buffer = Marshal.AllocHGlobal(42);
            try
            {
                if (!Native.SxuptpGetDeviceServerInfo(ip, buffer, 42))
                    throw new IOException("The selected device did not respond; disconnection is not confirmed.");
                AddServer(result, buffer);
                if (result.Count != 1 || result[0].RawIp != ip)
                    throw new IOException("The selected device address changed; disconnection is not confirmed.");
                result[0].Devices = ScanUsb(result[0]);
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static List<Server> Scan(string requestedIp = "")
        {
            IPAddress requested = null;
            if (!string.IsNullOrWhiteSpace(requestedIp) && (!IPAddress.TryParse(requestedIp, out requested) || requested.AddressFamily != AddressFamily.InterNetwork))
                throw new ArgumentException("请填写正确的 IPv4 地址，例如 192.168.2.5；也可以留空。");
            var broadcasts = new HashSet<uint>(); var descriptions = new List<string>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                    byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 255) || mask.All(b => b == 0)) continue;
                    byte[] broadcast = Enumerable.Range(0, 4).Select(i => (byte)(ip[i] | (255 ^ mask[i]))).ToArray();
                    broadcasts.Add(BitConverter.ToUInt32(broadcast, 0));
                    descriptions.Add(adapter.Name + ": " + address.Address + " → " + new IPAddress(broadcast));
                }
            }
            // SDK NULL search sends only 255.255.255.255. Supply subnet destinations explicitly.
            broadcasts.Add(uint.MaxValue);
            var direct = new HashSet<string>();
            if (requested != null) direct.Add(requested.ToString());
            if (File.Exists(CachePath))
                foreach (string line in File.ReadAllLines(CachePath).Take(16))
                { IPAddress cached; if (IPAddress.TryParse(line, out cached) && cached.AddressFamily == AddressFamily.InterNetwork) direct.Add(cached.ToString()); }
            LastSearch = "定向网段搜索：" + string.Join("；", descriptions) + "。指定/历史 IP：" + string.Join(", ", direct);
            var result = new List<Server>(); IntPtr p = Marshal.AllocHGlobal(42 * 256);
            IntPtr destinations = Marshal.AllocHGlobal(broadcasts.Count * 4);
            try
            {
                int index = 0; foreach (uint target in broadcasts) Marshal.WriteInt32(destinations, index++ * 4, unchecked((int)target));
                uint read, count;
                bool broadcastOk = Native.SxuptpEnumDeviceServers(destinations, (uint)broadcasts.Count, p, 42 * 256, out read, out count);
                if (!broadcastOk) count = 0;
                if (count > 256 || read < count * 42) throw new InvalidOperationException("SDK 返回的设备列表不完整，请重试。");
                for (int i = 0; i < count; i++)
                {
                    AddServer(result, IntPtr.Add(p, i * 42));
                }
                foreach (string target in direct)
                {
                    if (result.Any(s => s.Ip == target)) continue;
                    uint ip = BitConverter.ToUInt32(IPAddress.Parse(target).GetAddressBytes(), 0);
                    if (Native.SxuptpGetDeviceServerInfo(ip, p, 42)) AddServer(result, p);
                }
                if (!broadcastOk) throw new InvalidOperationException("SDK 网段搜索失败，请检查官方驱动和网络；本次结果不完整，已禁止据此清理旧绑定。");
                foreach (var s in result) s.Devices = ScanUsb(s);
                if (result.Count > 0)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)); File.WriteAllLines(CachePath, result.Select(s => s.Ip).Concat(direct).Distinct().Take(16)); }
                    catch (IOException) { LastSearch += "；历史 IP 未能保存"; }
                    catch (UnauthorizedAccessException) { LastSearch += "；历史 IP 未能保存"; }
                }
            }
            finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(destinations); }
            return result;
        }
        static void AddServer(List<Server> result, IntPtr q)
        {
            string model = ReadString(q, 10, 16);
            if (!model.StartsWith("DS-600", StringComparison.OrdinalIgnoreCase) && !model.StartsWith("DS600", StringComparison.OrdinalIgnoreCase)) return;
            var mac = new byte[6]; Marshal.Copy(q, mac, 0, 6); uint ip = unchecked((uint)Marshal.ReadInt32(q, 6));
            string key = BitConverter.ToString(mac).Replace("-", "");
            Server duplicate = result.FirstOrDefault(s => s.Mac == key);
            if (duplicate != null)
            {
                if (duplicate.RawIp != ip) throw new InvalidOperationException("同一设备身份对应多个网络地址，本次设备列表不完整，请刷新重试。");
                return;
            }
            result.Add(new Server { Mac = key, RawIp = ip, Ip = new IPAddress(BitConverter.GetBytes(ip)).ToString() });
        }
        static List<Usb> ScanUsb(Server s)
        {
            var result = new List<Usb>(); IntPtr p = Marshal.AllocHGlobal(124 * 256);
            try
            {
                uint read, count;
                if (!Native.SxuptpEnumDevices(s.RawIp, p, 124 * 256, out read, out count)) throw new InvalidOperationException(s.Ip + " 的 USB 列表读取失败，请刷新重试。");
                if (count > 256 || read < count * 124) throw new InvalidOperationException("USB 列表不完整。");
                for (int i = 0; i < count; i++)
                {
                    var q = IntPtr.Add(p, i * 124);
                    result.Add(new Usb { Server = s, Port = ReadString(q, 0, 32), Name = ReadString(q, 32, 64), Status = unchecked((ushort)Marshal.ReadInt16(q, 100)), Vid = unchecked((ushort)Marshal.ReadInt16(q, 102)), Pid = unchecked((ushort)Marshal.ReadInt16(q, 104)), Location = ReadString(q, 106, 16) });
                }
            }
            finally { Marshal.FreeHGlobal(p); }
            return result.GroupBy(u => u.Key).Select(g => { var u = g.First(); u.Port = string.Join(",", g.Select(d => d.Port).Distinct()); return u; }).ToList();
        }
        public static List<string> Apply(List<Server> shown, Dictionary<string, bool> desired, bool cleanup, string requestedIp, CancellationToken token=default(CancellationToken))
        {
            var lines = new List<string>();
            if (desired.Values.Any(v => v) && !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "SxConnectDev.exe")))
                throw new FileNotFoundException("缺少官方连接工具 SxConnectDev.exe，请恢复依赖文件后重试。");
            token.ThrowIfCancellationRequested();
            var fresh = Scan(requestedIp); token.ThrowIfCancellationRequested();var online = fresh.SelectMany(s => s.Devices).ToList();
            var targets = online.Where(u => desired.ContainsKey(u.Key)).ToList();
            if (targets.Count != desired.Count) throw new InvalidOperationException("设备列表已变化或设备离线，请刷新后重新勾选。本次尚未修改绑定。");
            if (targets.Any(u => desired[u.Key] && !System.Text.RegularExpressions.Regex.IsMatch(u.Port, @"^[A-Za-z0-9_,.:-]+$")))
                throw new InvalidOperationException("SDK 返回了未识别的 USB 端口名称，尚未修改绑定。");
            using (var service = new ServiceController("SX Virtual Link Lite"))
            {
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();var end=DateTime.UtcNow.AddSeconds(20);
                    while(service.Status!=ServiceControllerStatus.Running){Wait(200,token);service.Refresh();if(DateTime.UtcNow>=end)throw new BindingTimeoutException();}
                }
            }
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SX Virtual Link Lite", true))
            {
                if (key == null) throw new InvalidOperationException("未安装 SX Virtual Link Lite 服务。");
                key.SetValue("Start", 2, RegistryValueKind.DWord);
            }
            var deadline = DateTime.UtcNow.AddSeconds(20);
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    using (var root = hive.OpenSubKey(RegistryPath, true))
                    {
                        if (root != null)
                        {
                            var names = root.GetSubKeyNames();
                            if (targets.All(u => names.Any(n => n.StartsWith(u.Prefix, StringComparison.OrdinalIgnoreCase))))
                            {
                                var changes = new Dictionary<string, byte[]>(); var original = new Dictionary<string, byte[]>();
                                foreach (string name in names)
                                using (var key = root.OpenSubKey(name))
                                {
                                    var old = key.GetValue("Property") as byte[];
                                    var u = targets.FirstOrDefault(t => name.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase));
                                    bool stale = cleanup && string.Equals((key.GetValue("ServerName") as string ?? "").TrimEnd('\0'), "DS-600", StringComparison.OrdinalIgnoreCase)
                                        && !online.Any(t => name.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase));
                                    if (u == null && !stale) continue;
                                    if (old == null || old.Length != 2 || old[1] != 0 || (old[0] != 2 && old[0] != 11))
                                        throw new InvalidOperationException("检测到未识别的官方绑定格式，已停止，请使用官方设置界面：" + name);
                                    var value = new byte[] { (byte)(PersistentMode && u != null && desired[u.Key] ? 11 : 2), 0 };
                                    if (!old.SequenceEqual(value)) { changes.Add(name, value); original.Add(name, old); }
                                }
                                if (changes.Count > 0)
                                {
                                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIONGXIN", "HvDacXpert", "backups");
                                    Directory.CreateDirectory(folder);
                                    string backup = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".reg");
                                    var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
                                    foreach (var kv in original) text.Append("[HKEY_LOCAL_MACHINE\\" + RegistryPath + "\\" + kv.Key + "]\r\n\"Property\"=hex:" + string.Join(",", kv.Value.Select(b => b.ToString("x2"))) + "\r\n\r\n");
                                    File.WriteAllText(backup, text.ToString(), Encoding.Unicode);
                                    try { foreach (var kv in changes) using (var key = root.OpenSubKey(kv.Key, true)) key.SetValue("Property", kv.Value, RegistryValueKind.Binary); }
                                    catch { foreach (var kv in original) using (var key = root.OpenSubKey(kv.Key, true)) key.SetValue("Property", kv.Value, RegistryValueKind.Binary); throw; }
                                    lines.Add("自动绑定设置已保存，原设置已自动备份。");
                                }
                                else lines.Add("自动绑定设置已与当前勾选一致。");
                                break;
                            }
                        }
                    }
                    if (DateTime.UtcNow >= deadline) throw new BindingTimeoutException();
                    Wait(200,token);
                }
            }
            var codes = new Dictionary<string, int>();
            ReloadAutoConnection(token);
            Directory.CreateDirectory(Path.GetDirectoryName(ModeFile));File.WriteAllText(ModeFile,PersistentMode?"auto":"manual");
            lines.Add("System: "+L10n.T("service")+" — "+L10n.T("settingsSaved")+"; "+(PersistentMode?"AUTO / persistent":"MANUAL / next connection requires application"));
            foreach (var server in fresh)
            {
                var selected = targets.Where(u => u.Server.Mac == server.Mac && desired[u.Key]).ToList();
                if (selected.Count == 0) continue;
                // Preserve the previously working server-wide command when all modules are selected.
                bool all = selected.Count == server.Devices.Count;
                if (all) { int code = Connect(selected[0], true,false,token); foreach (var u in selected) codes[u.Key] = code; }
                else foreach (var u in selected) codes[u.Key] = Connect(u,false,false,token);
            }
            var readiness = new Dictionary<string, bool>();
            DateTime readyDeadline = DateTime.UtcNow; // Driver waiting is centralized in Workflow (20 seconds).
            do
            {
                foreach (var u in targets.Where(t => desired[t.Key])) readiness[u.Key] = ConnectionHealth.Ready(u);
                if (readiness.Values.All(v => v) || readiness.Count == 0) break;
                token.ThrowIfCancellationRequested();
            } while (DateTime.UtcNow < readyDeadline);
            foreach (var u in targets)
            {
                if (!desired[u.Key]) { lines.Add("已取消自动绑定：" + Display.Device(u.Name) + "（不强制中断现有采集连接）"); continue; }
                int code = codes[u.Key];
                string message = readiness.ContainsKey(u.Key) && readiness[u.Key] ? "已连接，Windows 已识别且设备状态正常" : code == 7 ? "被其他电脑占用" : code == 6 ? "信号汇集器已离线，连接未完成" : "未就绪：Windows 尚未识别到正常工作的设备；停止采集后可点击重新连接。状态码=" + code;
                lines.Add(Display.Device(u.Name) + "：" + message);
            }
            return lines;
        }
        public static List<string> Reconnect(List<Server> shown, Dictionary<string, bool> desired, bool cleanup, string ip,CancellationToken token=default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            var online = Scan(ip).SelectMany(s => s.Devices).ToList();
            token.ThrowIfCancellationRequested();
            var selected = online.Where(u => desired.ContainsKey(u.Key) && desired[u.Key]).ToList();
            if (selected.Count != desired.Count(kv => kv.Value)) throw new InvalidOperationException("设备列表已变化");
            foreach (var u in selected)
                foreach (string port in u.Port.Split(','))
                    if (Native.SxuptpGetDeviceStatus(u.Server.RawIp, port) == 3) throw new InvalidOperationException("采集模块被其他电脑占用，无法重新连接。");
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var root = hive.OpenSubKey(RegistryPath, true))
            {
                if (root == null) throw new InvalidOperationException("通信服务配置未生成。");
                var saved = new Dictionary<string, byte[]>();
                try
                {
                    foreach (string keyName in root.GetSubKeyNames().Where(n => selected.Any(u => n.StartsWith(u.Prefix, StringComparison.OrdinalIgnoreCase))))
                    using (var key = root.OpenSubKey(keyName, true))
                    {
                        var old = key.GetValue("Property") as byte[];
                        if (old == null || old.Length != 2) throw new InvalidOperationException("绑定格式异常");
                        saved[keyName] = old; key.SetValue("Property", new byte[] { 2, 0 }, RegistryValueKind.Binary);
                    }
                    Wait(500,token);
                    foreach (var u in selected) { int code = Connect(u, false, true,token); if (code != 2 && code != 11 && code != 5) throw new InvalidOperationException("重新连接的释放步骤未完成，状态码=" + code); }
                    Wait(1500,token);
                }
                finally { foreach (var kv in saved) using (var key = root.OpenSubKey(kv.Key, true)) if (key != null) key.SetValue("Property", kv.Value, RegistryValueKind.Binary); }
            }
            return Apply(shown, desired, cleanup, ip,token);
        }
        public static void Wait(int milliseconds,CancellationToken token){if(token.WaitHandle.WaitOne(milliseconds))token.ThrowIfCancellationRequested();}
        static int Connect(Usb u, bool all = false, bool disconnect = false,CancellationToken token=default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime", "SxConnectDev.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("缺少 SxConnectDev.exe，绑定已保存，请恢复依赖文件后重试。");
            if (!System.Text.RegularExpressions.Regex.IsMatch(u.Port, @"^[A-Za-z0-9_,.:-]+$")) throw new InvalidOperationException("USB 端口名称异常，未执行连接。");
            using (var p = Process.Start(new ProcessStartInfo(exe, (disconnect ? "/D" : "/C") + " /I" + u.Server.Ip + " /E" + FormatMac(u.Server.Mac) + (all ? "" : " /P" + u.Port)) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                var end=DateTime.UtcNow.AddSeconds(20);
                while(!p.WaitForExit(100))
                {
                    if(token.IsCancellationRequested || DateTime.UtcNow>=end)
                    {
                        if(!p.HasExited){p.Kill();p.WaitForExit(2000);}
                        token.ThrowIfCancellationRequested();throw new BindingTimeoutException();
                    }
                }
                token.ThrowIfCancellationRequested();return p.ExitCode;
            }
        }
    }
    static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
        [DllImport("Sxuptp.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SxuptpEnumDeviceServers(IntPtr addresses, uint addressCount, IntPtr buffer, int bufferSize, out uint bytesRead, out uint count);
        [DllImport("Sxuptp.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SxuptpEnumDevices(uint serverIp, IntPtr buffer, int bufferSize, out uint bytesRead, out uint count);
        [DllImport("Sxuptp.dll", CallingConvention = CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SxuptpGetDeviceServerInfo(uint serverIp, IntPtr buffer, int bufferSize);
        [DllImport("Sxuptp.dll", CharSet = CharSet.Ansi)] public static extern uint SxuptpGetDeviceStatus(uint ip, string port);
    }
}
