using System.Drawing;
using System.Windows.Forms;
using Xiongxin.OWTS.Localization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class SettingsForm
    {
        internal bool AuditEnabled { get; set; } = true;
        private void LogSettingAction(OperationAuditAction action, AuditOutcome outcome)
        {
            if (AuditEnabled) OperationAuditLog.Default.EnqueueOperation(action, outcome, null, AuditModule.Settings);
        }
        private void AddLogBrowserRow(TableLayoutPanel grid)
        {
            var browse = AppTheme.Button("浏览软件日志…", false);
            browse.Name = "BrowseOperationLogs";
            browse.AutoSize = true; browse.AutoEllipsis = false;
            browse.MinimumSize = new Size(210, 40);
            browse.Click += delegate
            {
                if (!systemAdminUnlocked) return;
                using (var viewer = new OperationLogViewer(OperationAuditLog.Default, SettingsLanguageIndex)) viewer.ShowDialog(this);
            };
            AddRow(grid, "项目建立记录 / 操作记录", browse);
        }
    }
}
