namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        private void RecordOperation(OperationAuditAction action, AuditOutcome outcome,
            string projectPath = null, AuditModule module = AuditModule.None)
        {
            if (!verificationMode) OperationAuditLog.Default.EnqueueOperation(action, outcome, projectPath, module);
        }
    }
}
