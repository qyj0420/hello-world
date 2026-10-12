using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal enum ManagedDeviceManagerAction { None, Parameters, ConnectRefresh }

    // No SDK, process, timer, discovery or settings-write dependencies. A button
    // returns a navigation request; the host owns the actual guarded workflow.
    internal sealed class ManagedDeviceManagerForm : Form
    {
        internal ManagedDeviceManagerAction RequestedAction { get; private set; }
        internal ManagedDeviceManagerForm(AppSettings configured,HardwareStatusSnapshot observed,string language)
        {
            if (configured == null) throw new ArgumentNullException("configured");
            var s = configured.Clone();
            var h = observed == null ? new HardwareStatusSnapshot() : observed.Clone();
            bool zh = AppLanguages.IsChinese(language);
            Func<string,string,string> t = (z,e) => AppLanguages.Display(language,zh ? z : e);
            Text = t("设备管理","Device management");
            AutoScaleMode = AutoScaleMode.Dpi; Font = AppTheme.Font(10.5f,FontStyle.Regular); BackColor = AppTheme.Canvas;
            ClientSize = new Size(800,530); MinimumSize = new Size(620,410); StartPosition = FormStartPosition.CenterParent;
            AppTheme.ApplyDialogIcon(this);
            var layout = new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(14),ColumnCount=1,RowCount=4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = new Label { Name="ManagedDeviceSnapshotHeading",Dock=DockStyle.Fill,AutoSize=true,ForeColor=AppTheme.Ink,
                Text=t("现有设备状态快照（打开本窗口不刷新设备）","Existing device snapshot (opening this window does not refresh devices)") };
            var rows = new DataGridView { Name="ManagedDeviceSnapshot",Dock=DockStyle.Fill,ReadOnly=true,
                AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToOrderColumns=false,RowHeadersVisible=false,
                MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells,BackgroundColor=Color.White,BorderStyle=BorderStyle.FixedSingle };
            rows.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            rows.Columns.Add("Field",t("项目","Item")); rows.Columns.Add("Value",t("现有记录","Existing record"));
            rows.Columns[0].FillWeight=35; rows.Columns[1].FillWeight=65;
            Func<string,string> value = x => String.IsNullOrWhiteSpace(x) ? t("未知／未确认","Unknown / not confirmed") : x;
            bool identified = h.HasLiveIdentityReadback && h.IdentityValid && h.CheckedUtc != DateTime.MinValue;
            rows.Rows.Add(t("连接状态","Connection state"),h.Ready && identified ? t("最近一次核验通过；不是当前高压许可","Last verification passed; not current HV permission") : t("当前连接尚未确认","Connection not confirmed"));
            rows.Rows.Add(t("状态代号","Status code"),value(h.StatusCode));
            rows.Rows.Add(t("最近核验时间 (UTC)","Last verification (UTC)"),h.CheckedUtc==DateTime.MinValue ? value(null) : h.CheckedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture));
            rows.Rows.Add(t("实机身份回读","Actual identity readback"),identified ? t("现有记录包含真实回读","Existing record contains actual readback") : t("未知；配置标识不代替实机回读","Unknown; configured IDs are not actual readback"));
            rows.Rows.Add(t("回读 Hub MAC","Readback Hub MAC"),identified ? value(h.HubMac) : value(null));
            rows.Rows.Add(t("回读控制板序列号","Readback controller serial"),identified ? value(h.ControllerSerial) : value(null));
            rows.Rows.Add(t("回读采集板序列号","Readback acquisition serial"),identified ? value(h.AcquisitionSerial) : value(null));
            var hw = s.Hardware ?? new HardwareSettings();
            rows.Rows.Add(t("配置 Hub MAC","Configured Hub MAC"),value(hw.SelectedHubMac));
            rows.Rows.Add(t("配置控制板位置","Configured controller location"),value(hw.ControllerLocation));
            rows.Rows.Add(t("配置采集板位置","Configured acquisition location"),value(hw.AcquisitionLocation));
            rows.Rows.Add(t("局放采样配置","PD sampling configuration"),s.PdSampleRateHz.ToString("G",CultureInfo.InvariantCulture)+" Hz / "+s.PdSampleBits.ToString("G",CultureInfo.InvariantCulture)+" bit");
            rows.Rows.Add(t("参数核验","Parameter review"),AppLanguages.Display(language,DeviceSetupStatus.Summary(s,h,zh)));
            var note = new Label { Name="ManagedDeviceScope",AutoSize=true,Dock=DockStyle.Fill,ForeColor=AppTheme.Muted,
                Text=t("外部驱动包兼容性尚未审核；首次绑定仍待支持。本窗口不绑定、不采集、不加压。连接／刷新与参数设置仅在您明确点击后进入原有核验流程。",
                    "External driver compatibility is not reviewed; initial binding remains pending. This window does not bind, acquire or energize. Connect/refresh and parameter settings enter the existing guarded workflow only after your explicit click.") };
            var actions = new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.RightToLeft,WrapContents=true };
            var close = AppTheme.Button(t("关闭","Close"),false); close.Name="CloseManagedDeviceManager"; close.AutoSize=true; close.DialogResult=DialogResult.Cancel;
            var connect = AppTheme.Button(t("连接／刷新","Connect / refresh"),true); connect.Name="ConnectRefreshManagedDevice"; connect.AutoSize=true;
            var parameters = AppTheme.Button(t("参数设置","Parameter settings"),false); parameters.Name="OpenManagedDeviceParameters"; parameters.AutoSize=true;
            connect.Click += delegate { Request(ManagedDeviceManagerAction.ConnectRefresh); };
            parameters.Click += delegate { Request(ManagedDeviceManagerAction.Parameters); };
            actions.Controls.Add(close); actions.Controls.Add(connect); actions.Controls.Add(parameters);
            layout.Controls.Add(heading,0,0); layout.Controls.Add(rows,0,1); layout.Controls.Add(note,0,2); layout.Controls.Add(actions,0,3); Controls.Add(layout);
            Action fit = delegate { int width=Math.Max(100,layout.ClientSize.Width-layout.Padding.Horizontal-12); heading.MaximumSize=new Size(width,0); note.MaximumSize=new Size(width,0); };
            SizeChanged += delegate { fit(); }; fit(); AppTheme.FitDialogToWorkingArea(this);
            CancelButton=close;
            // No default Enter/Shown action can initiate a connection.
        }
        void Request(ManagedDeviceManagerAction action) { RequestedAction=action; DialogResult=DialogResult.OK; Close(); }
    }
}
