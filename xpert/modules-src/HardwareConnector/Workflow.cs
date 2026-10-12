using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace Xiongxin.HvDacXpert
{
    sealed class BindingTimeoutException : Exception
    {
        public BindingTimeoutException():base("E302: binding stage timed out (20 s)"){}
    }
    sealed class DriverPrivilege : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct Privileges {public uint Count; public Luid Id; public uint Attributes;}
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValue(string system,string name,out Luid id);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref Privileges state,uint size,out Privileges previous,out uint returned);
        IntPtr token; Privileges previous;
        public static DriverPrivilege Enable()
        {
            var result=new DriverPrivilege();
            try
            {
                if(!OpenProcessToken(GetCurrentProcess(),0x28,out result.token))throw new Win32Exception(Marshal.GetLastWin32Error());
                Luid id;if(!LookupPrivilegeValue(null,"SeLoadDriverPrivilege",out id))throw new Win32Exception(Marshal.GetLastWin32Error());
                var desired=new Privileges{Count=1,Id=id,Attributes=2};uint returned;
                if(!AdjustTokenPrivileges(result.token,false,ref desired,(uint)Marshal.SizeOf(typeof(Privileges)),out result.previous,out returned) || Marshal.GetLastWin32Error()!=0)throw new Win32Exception(Marshal.GetLastWin32Error());
                return result;
            }
            catch {result.Dispose();throw;}
        }
        public void Dispose()
        {
            if(token==IntPtr.Zero)return;
            if(previous.Count>0){Privileges ignored;uint returned;AdjustTokenPrivileges(token,false,ref previous,(uint)Marshal.SizeOf(typeof(Privileges)),out ignored,out returned);}
            CloseHandle(token);token=IntPtr.Zero;
        }
    }
    sealed class ModuleState
    {
        public int Kind; // 1 art, 2 scope, 0 unsupported
        public bool Bound, Connected, Ready;
    }
    sealed class WorkflowState
    {
        public bool Hub, Art, Scope, AllSelectedReady;
        public bool Complete { get { return Hub && Art && Scope && AllSelectedReady; } }
        public string Message { get { return Complete ? "系统绑定完成：Xpert 已绑定，art / scope 驱动加载校验通过。" : "系统绑定尚未完成，请查看各项状态。"; } }
    }
    static class Workflow
    {
        public static bool RequirePersistentBinding=true;
        public static WorkflowState Evaluate(IList<ModuleState> states)
        {
            return new WorkflowState {
                Hub=states.Count>0 && states.All(s=>s.Bound && s.Connected),
                Art=states.Any(s=>s.Kind==1) && states.Where(s=>s.Kind==1).All(s=>s.Bound && s.Connected && s.Ready),
                Scope=states.Any(s=>s.Kind==2) && states.Where(s=>s.Kind==2).All(s=>s.Bound && s.Connected && s.Ready),
                AllSelectedReady=states.Count>0 && states.All(s=>s.Bound && s.Connected && s.Ready)
            };
        }
        public static WorkflowState Inspect(IList<Usb> targets, bool checkAliases=true)
        {
            var result=Evaluate(targets.Select(u=>new ModuleState {
                Kind=u.Vid==0x3063 && u.Pid==0x3135 ? 1 : u.Vid==0x0CE9 && u.Pid==0x1218 ? 2 : 0,
                Bound=!RequirePersistentBinding || Backend.IsBound(u), Connected=ConnectionHealth.Connected(u), Ready=ConnectionHealth.Ready(u)
            }).ToList());
            if(checkAliases && result.Art && !ArtAliases.Ready())result.AllSelectedReady=false;
            return result;
        }
        public static WorkflowState LoadAndCheck(IList<Usb> targets,Action<string> report,Action<WorkflowState> status,CancellationToken token=default(CancellationToken))
        {
            token.ThrowIfCancellationRequested();
            if(targets.Count==0) { report("未选择模块，未执行驱动加载。"); return Evaluate(new List<ModuleState>()); }
            report("步骤 1/3：读取 Xpert 绑定和实时连接状态…");
            var current=Inspect(targets); status(current);
            if(!current.Hub) { report("Xpert 尚未全部绑定/连接，继续等待硬件响应。"); }
            report("步骤 2/3：请求 Windows 扫描通信总线，匹配已安装的驱动包…");
            if(targets.Any(u=>!ConnectionHealth.Ready(u))) using(var privilege=DriverPrivilege.Enable()) DeviceNames.ScanCommunicationBus();
            var requested=new HashSet<string>();
            DateTime end=DateTime.UtcNow.AddSeconds(20); int tick=0,stable=0,artStable=0; bool aliasesDone=false;
            do
            {
                token.ThrowIfCancellationRequested();
                foreach(var u in targets)
                {
                    if(!ConnectionHealth.Connected(u) || ConnectionHealth.Ready(u) || requested.Contains(u.Key)) continue;
                    string message;
                    using(var privilege=DriverPrivilege.Enable()) message=ConnectionHealth.RequestInstallation(u);
                    if(!message.StartsWith("等待")) requested.Add(u.Key);
                    if(tick==0 || requested.Contains(u.Key)) report(Display.Device(u.Name)+"："+message);
                }
                current=Inspect(targets,false);
                artStable=current.Art?artStable+1:0;
                if(artStable>=3 && !aliasesDone)
                {
                    token.ThrowIfCancellationRequested();
                    current.AllSelectedReady=false;status(current);
                    report("ART Dev1…DevN: "+L10n.T("processing"));
                    try{ArtAliases.Normalize(report);aliasesDone=true;}
                    catch(Exception ex){report(ex.Message);throw;}
                    token.ThrowIfCancellationRequested();
                    current=Inspect(targets);
                }
                if(targets.Any(u=>u.Vid==0x3063 && u.Pid==0x3135) && !aliasesDone)current.AllSelectedReady=false;
                status(current);
                stable=current.AllSelectedReady?stable+1:0;
                if(stable>=3)
                {
                    current=Inspect(targets); status(current); report(current.Message); return current;
                }
                if(tick%5==0) report("步骤 3/3：art "+(current.Art?"已通过":"等待加载/未就绪")+"；scope "+(current.Scope?"已通过":"等待加载/未就绪")+"（剩余等待约 "+Math.Max(0,(int)(end-DateTime.UtcNow).TotalSeconds)+" 秒）");
                Backend.Wait(1000,token); tick++;
            } while(DateTime.UtcNow<end);
            report("校验等待结束：未通过的项目未亮绿灯。请检查电源、连接、设备管理器错误代码；如安装器要求重启，请重启后再试。");
            current.AllSelectedReady=false;
            return current;
        }
    }
    sealed class BindingProgress : Form
    {
        readonly TextBox text=new TextBox(); readonly Button close=new Button(); bool finished;
        public event Action StopRequested;
        void StopAndHide(){if(StopRequested!=null)StopRequested();Hide();}
        public BindingProgress(Icon icon)
        {
            Text="HV-DAC Xpert · 系统绑定进度"; Icon=(Icon)icon.Clone(); ClientSize=new Size(900,430); StartPosition=FormStartPosition.CenterParent;
            Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(244,247,251);
            var grid=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(16),RowCount=3,ColumnCount=1};
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); grid.RowStyles.Add(new RowStyle(SizeType.Percent,100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
            grid.Controls.Add(new Label{Text="Xpert 绑定 → art 驱动校验 → scope 驱动校验",Dock=DockStyle.Fill},0,0);
            text.Multiline=true; text.ReadOnly=true; text.ScrollBars=ScrollBars.Vertical; text.Dock=DockStyle.Fill; grid.Controls.Add(text,0,1);
            close.Text=L10n.StopWaitText; close.Enabled=true; close.Dock=DockStyle.Right; close.Width=300; close.Click+=delegate{if(finished)Close();else StopAndHide();}; grid.Controls.Add(close,0,2); Controls.Add(grid);
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!finished){e.Cancel=true;StopAndHide();}};
            L10n.Attach(this);
        }
        string lastCustomer;
        public void Report(string value){string customer=L10n.CustomerMessage(value);if(!string.IsNullOrEmpty(customer)&&customer!=lastCustomer){text.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+customer+Environment.NewLine);lastCustomer=customer;}}
        public void Finish(string value){Report(value); finished=true; close.Enabled=true;close.Text="关闭进度窗口";}
    }
}
