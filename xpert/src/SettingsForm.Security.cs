using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        // Grants belong to this window only. They are never copied into settings.
        readonly HashSet<SettingsSection> maintenanceGrants = new HashSet<SettingsSection>();
        string SettingsLanguageCode { get { var item = language.SelectedItem as LanguageItem; return item == null ? original.LanguageCode : item.Code; } }

        bool IsSectionAuthorized(SettingsSection section)
        {
            if (IsDisposed || Disposing) return false;
            return !SettingsAccessControl.Enabled || section == SettingsSection.General || maintenanceGrants.Contains(section);
        }

        bool TryUnlockSection(SettingsSection section, string password)
        {
            if (IsDisposed || Disposing) return false;
            if (!SettingsAccessControl.Enabled) { OpenSection(section); return true; }
            if (!SettingsAccessControl.Verify(section, password))
            {
                LogSettingAction(OperationAuditAction.AuthorizationRejected, AuditOutcome.Failed);
                return false;
            }
            maintenanceGrants.Add(section);
            try { OpenSection(section); }
            catch { maintenanceGrants.Remove(section); throw; }
            LogSettingAction(OperationAuditAction.AuthorizationAccepted, AuditOutcome.Succeeded);
            return true;
        }

        void UnlockSection(SettingsSection section)
        {
            string password;
            if (!MaintenanceAuthorizationPrompt.TryRead(this, section, SettingsLanguageCode, out password)) return;
            try
            {
                if (!TryUnlockSection(section, password))
                    MessageBox.Show(this, SettingsText("口令不正确，未解锁设置。", "The password is incorrect. Settings remain locked."),
                        SettingsText("维护授权", "Maintenance authorization"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (InvalidOperationException)
            {
                MessageBox.Show(this, SettingsText("无法打开设置，请检查原配置后重试。", "Cannot open settings. Check the existing configuration and try again."),
                    SettingsText("配置无效", "Invalid settings"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { password = null; }
        }

        private Form CreateAuthorizationDialog(SettingsSection section)
        {
            return MaintenanceAuthorizationPrompt.Create(section, SettingsLanguageCode, Font);
        }

        void AddLockedSectionEntry(TabPage page, SettingsSection section)
        {
            var grid = CreateGrid();
            AddWideRow(grid, CreateNote("此页设置需要维护授权。关闭设置窗口后需重新授权。"));
            var unlock = AppTheme.Button("输入授权口令…", true);
            unlock.Name = "UnlockSettings" + section;
            unlock.AutoSize = true;
            unlock.Click += delegate { UnlockSection(section); };
            AddWideRow(grid, unlock);
            page.Controls.Add(grid);
        }
    }

    internal static class MaintenanceAuthorizationPrompt
    {
        internal static Form Create(SettingsSection section, string languageCode, Font font)
        {
            bool zh = AppLanguages.IsChinese(languageCode);
            Func<string,string,string> tr = (cn,en) => AppLanguages.Display(languageCode, zh ? cn : en);
            string title = section == SettingsSection.PlusData ? tr("HV DAC Xpert Plus Data 授权", "HV DAC Xpert Plus Data authorization")
                : section == SettingsSection.SystemAdmin ? tr("系统管理授权", "System administration authorization")
                : section == SettingsSection.Device ? tr("设备关键参数授权", "Critical device settings authorization")
                : section == SettingsSection.Oem ? tr("厂家 OEM 授权", "OEM authorization")
                : tr("厂家维护授权", "Manufacturer maintenance authorization");
            var prompt = new Form { Name="MaintenanceAuthorizationDialog", Text=title, StartPosition=FormStartPosition.CenterParent,
                MinimizeBox=false, MaximizeBox=false, ShowInTaskbar=false, AutoScaleMode=AutoScaleMode.Dpi,
                ClientSize=new Size(520,240), MinimumSize=new Size(360,220), Font=font, BackColor=AppTheme.Canvas };
            AppTheme.ApplyDialogIcon(prompt);
            var viewport = new Panel { Dock=DockStyle.Fill, AutoScroll=true };
            var layout = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, Padding=new Padding(18,12,18,12), ColumnCount=1, RowCount=2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = new Label { Name="AuthorizationPrompt", Text=tr("请输入维护口令：", "Enter maintenance password:"), AutoSize=true, Dock=DockStyle.Fill };
            var input = new TextBox { Name="AuthorizationPassword", UseSystemPasswordChar=true, MaxLength=256, Dock=DockStyle.Fill, Margin=new Padding(0,8,0,8) };
            var actions = new FlowLayoutPanel { Name="AuthorizationActions", Dock=DockStyle.Bottom, AutoSize=true,
                Padding=new Padding(12,6,12,12), FlowDirection=FlowDirection.RightToLeft, WrapContents=true };
            var accept = AppTheme.Button(tr("解锁", "Unlock"), true); accept.Name="AuthorizationUnlock";accept.DialogResult=DialogResult.OK;accept.AutoSize=true;
            var cancel = AppTheme.Button(tr("取消", "Cancel"), false);cancel.Name="AuthorizationCancel";cancel.DialogResult=DialogResult.Cancel;cancel.AutoSize=true;
            actions.Controls.Add(accept);actions.Controls.Add(cancel);
            layout.Controls.Add(label,0,0);layout.Controls.Add(input,0,1);
            viewport.Controls.Add(layout);prompt.Controls.Add(viewport);prompt.Controls.Add(actions);
            Action fit = delegate {
                label.MaximumSize=new Size(Math.Max(100,viewport.ClientSize.Width-layout.Padding.Horizontal-12),0);
                accept.Font=cancel.Font=prompt.Font;accept.Height=cancel.Height=Math.Max(38,prompt.Font.Height+18);
            };
            prompt.FontChanged+=delegate { fit(); };prompt.SizeChanged+=delegate { fit(); };
            prompt.Shown+=delegate { fit();prompt.PerformLayout();AppTheme.FitDialogToWorkingArea(prompt);input.Focus(); };
            prompt.AcceptButton=accept;prompt.CancelButton=cancel;fit();return prompt;
        }

        internal static bool TryRead(IWin32Window owner, SettingsSection section, string languageCode, out string password)
        {
            password = null;
            // The result is read before Dispose. Do not log input or retain it in a control.
            using (var prompt = Create(section, languageCode, (owner as Control) == null ? AppTheme.Font(9,FontStyle.Regular) : ((Control)owner).Font))
            {
                var input = (TextBox)prompt.Controls.Find("AuthorizationPassword",true)[0];
                try {
                    if (prompt.ShowDialog(owner) != DialogResult.OK) return false;
                    password = input.Text;
                    return true;
                } finally { input.Clear(); }
            }
        }
    }
}
