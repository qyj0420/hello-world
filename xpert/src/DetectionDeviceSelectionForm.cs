using System;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed class DetectionDeviceSelectionForm : Form
    {
        readonly DetectionDeviceList receipt;
        readonly DataGridView list;
        readonly Button connect;
        readonly Timer expiry=new Timer { Interval=500 };
        internal DetectionDevice SelectedDevice { get; private set; }
        internal bool RefreshRequested { get; private set; }
        internal DetectionDeviceSelectionForm(DetectionDeviceList devices,string language,string currentMac)
        {
            if(devices==null) throw new ArgumentNullException("devices"); receipt=devices;
            bool zh=AppLanguages.IsChinese(language); Func<string,string> display=text=>AppLanguages.Display(language,text);
            Text=display(zh?"局放检测设备 · 选择连接":"Partial discharge detector · Select device");
            AutoScaleMode=AutoScaleMode.Dpi; Font=AppTheme.Font(10.5f,FontStyle.Regular); BackColor=AppTheme.Canvas;
            ClientSize=new Size(960,530); MinimumSize=new Size(760,440); StartPosition=FormStartPosition.CenterParent; AppTheme.ApplyDialogIcon(this);
            var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(12), ColumnCount=1, RowCount=4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bool none=receipt.Devices.Count==0, available=receipt.Devices.Any(d=>d.Selectable);
            var intro=new Label { Name="DiscoveryIntroduction", AutoSize=true, Dock=DockStyle.Fill, Text=display(none?
                (zh?"未发现可连接设备。请检查供电、网线/USB及设备地址，然后刷新设备。":"No detectors found. Check power, network/USB and device address, then refresh."):
                !available?(zh?"已发现设备，但当前没有可连接项。请检查组件是否齐全、配对是否唯一，以及是否被其他程序占用。":"Devices found, but none are available. Check missing components, ambiguous pairing and use by another program."):
                (zh?"请选择设备；未绑定设备需要确认绑定。":"Select a detector. Unbound items require binding.")) };
            list=new DataGridView { Name="DetectionDeviceList", Dock=DockStyle.Fill, ReadOnly=true, AllowUserToAddRows=false, AllowUserToDeleteRows=false,
                RowHeadersVisible=false, MultiSelect=false, SelectionMode=DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor=Color.White, BorderStyle=BorderStyle.FixedSingle, AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells };
            list.DefaultCellStyle.WrapMode=DataGridViewTriState.True; list.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            foreach(string title in zh?new[]{"设备","标识","组件","状态"}:new[]{"Unit","ID","Parts","State"}) list.Columns.Add(title,display(title));
            list.Columns[0].FillWeight=80; list.Columns[1].FillWeight=135; list.Columns[2].FillWeight=190; list.Columns[3].FillWeight=120;
            foreach(var device in receipt.Devices)
            {
                string status=device.State=="Available"?(device.PersistentBound?(zh?"已绑定":"Bound"):(zh?"未绑定":"Not\nbound")):
                    device.State=="InUse"?(zh?"已占用":"In use"):device.State=="Ambiguous"?(zh?"配对不唯一":"Check\npair"):(zh?"组件不完整":"Check\nparts");
                string identity=CustomerIdentity(device);
                int index=list.Rows.Add(display(zh?"局放检测设备":"PD unit"),identity.Substring(0,3)+"\n"+identity.Substring(3,6)+"\n"+identity.Substring(9),
                    display(zh?"系统 / 控制 / 采集":"System / Control / Capture"),
                    display(status)+(device.Mac==currentMac?display(zh?" · 当前":"\n(now)"):""));
                list.Rows[index].Cells[1].ToolTipText=identity;
                list.Rows[index].Cells[2].ToolTipText=HardwareDeviceDiscovery.SystemName+"\n"+HardwareDeviceDiscovery.ControllerName+"\n"+HardwareDeviceDiscovery.AcquisitionName;
                list.Rows[index].Cells[3].ToolTipText=device.State+(device.Mac==currentMac?display(zh?" · 当前配置":" · Current configuration"):"");
                list.Rows[index].Tag=device; if(!device.Selectable) list.Rows[index].DefaultCellStyle.ForeColor=AppTheme.Muted;
            }
            list.ClearSelection(); if(list.Rows.Count>0) list.CurrentCell=null;
            var note=new Label { Name="DiscoveryStatus", AutoSize=true, Dock=DockStyle.Fill, Text=display(zh?"30 秒内有效；非高压许可。":"30 seconds; not an HV permit.") };
            var actions=new FlowLayoutPanel { Name="DiscoveryActions", Dock=DockStyle.Fill, AutoSize=true, FlowDirection=FlowDirection.RightToLeft, WrapContents=true };
            connect=AppTheme.Button(display(zh?"选择连接":"Connect"),true); connect.Name="ConnectSelectedDetector"; connect.AutoSize=true; connect.Enabled=false; connect.AccessibleName=display(zh?"连接所选设备":"Connect selected detector");
            var cancel=AppTheme.Button(display(zh?"取消":"Cancel"),false); cancel.AutoSize=true; cancel.DialogResult=DialogResult.Cancel;
            var refresh=AppTheme.Button(display(zh?"刷新设备":"Refresh"),false); refresh.Name="RefreshDetectors"; refresh.AutoSize=true;
            refresh.Click+=delegate { RefreshRequested=true; DialogResult=DialogResult.Retry; Close(); };
            connect.Click+=delegate { TrySelect(DateTime.UtcNow); };
            list.SelectionChanged+=delegate { connect.Enabled=receipt.CanSelect(Current(),DateTime.UtcNow); };
            list.CurrentCellChanged+=delegate { connect.Enabled=receipt.CanSelect(Current(),DateTime.UtcNow); };
            list.CellDoubleClick+=delegate(object sender,DataGridViewCellEventArgs e) { if(e.RowIndex>=0) TrySelect(DateTime.UtcNow); };
            Action updateExpiry=delegate { connect.Enabled=receipt.CanSelect(Current(),DateTime.UtcNow); if(!receipt.IsFresh(DateTime.UtcNow)) note.Text=display(zh?"列表已过期，请刷新。当前列表不能用于连接，更不是高压许可。":"Expired; refresh devices. This list cannot authorize connection or high voltage."); };
            expiry.Tick+=delegate { updateExpiry(); };
            actions.Controls.Add(connect); actions.Controls.Add(refresh); actions.Controls.Add(cancel);
            layout.Controls.Add(intro,0,0); layout.Controls.Add(list,0,1); layout.Controls.Add(note,0,2); layout.Controls.Add(actions,0,3); Controls.Add(layout);
            // WinForms can focus/select the first grid row when a dialog is shown.
            // Do not let that implicit focus become consent to connect/bind it.
            Shown+=delegate { ActiveControl=refresh; list.ClearSelection(); list.CurrentCell=null; updateExpiry(); connect.Enabled=false; };
            AppTheme.FitDialogToWorkingArea(this);
            Action fit=delegate {
                int width=Math.Max(100,layout.ClientSize.Width-layout.Padding.Horizontal-12);
                intro.MaximumSize=new Size(width,0);note.MaximumSize=new Size(width,0);
                foreach(Button button in new[]{connect,refresh,cancel}){button.Font=Font;button.Height=Math.Max(38,Font.Height+18);}
            };
            FontChanged+=delegate{fit();};SizeChanged+=delegate{fit();};fit();
            CancelButton=cancel; expiry.Start();
        }
        DetectionDevice Current() { return list.SelectedRows.Count!=1 || list.CurrentRow==null || !list.CurrentRow.Selected?null:list.CurrentRow.Tag as DetectionDevice; }
        internal static string CustomerIdentity(DetectionDevice device)
        {
            if(device==null) return String.Empty;
            using(var sha=SHA256.Create())
                return "PD-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(device.Mac??String.Empty))).Replace("-",String.Empty).Substring(0,12);
        }
        internal bool TrySelect(DateTime utc)
        { if(IsDisposed || DialogResult!=DialogResult.None || SelectedDevice!=null || RefreshRequested) return false; var selected=Current(); if(!receipt.CanSelect(selected,utc)) return false; SelectedDevice=selected; DialogResult=DialogResult.OK; Close(); return true; }
        protected override void Dispose(bool disposing) { if(disposing) expiry.Dispose(); base.Dispose(disposing); }
    }
}
