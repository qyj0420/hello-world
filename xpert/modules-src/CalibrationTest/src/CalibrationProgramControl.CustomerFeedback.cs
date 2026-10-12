using System;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        internal Action<string,string> ShowCalibrationCustomerFailureForTests;
        private string CalibrationCustomerFailureCaption
        { get { return CalibrationText("操作未完成，请核对项目和输入参数后重试。", "Operation not completed. Check the project and input values, then retry."); } }
        private string CalibrationQualitySaveCustomerCaption
        { get { return CalibrationText("当前校准质量需复核，数据可能不可靠。仍要保存吗？", "The calibration quality needs review and the data may be unreliable. Save anyway?"); } }
        private string RecordCalibrationCustomerFailure(string operation,Exception error)
        {
            WriteCalibrationDiagnostic(operation+": "+error);
            return CalibrationCustomerFailureCaption;
        }
        private void ShowCalibrationCustomerFailure(IWin32Window owner,string operation,Exception error,string chineseTitle,string englishTitle)
        {
            string body=RecordCalibrationCustomerFailure(operation,error),title=CalibrationText(chineseTitle,englishTitle);
            if(IsDisposed||Disposing)return;
            var show=ShowCalibrationCustomerFailureForTests;
            if(show!=null)show(body,title);
            else MessageBox.Show(owner,body,title,MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        private void ShowSavedJointCustomerWarning(IWin32Window owner)
        {
            WriteCalibrationDiagnostic("Joint saved with a follow-up warning: "+LastJointCommitWarning);
            MessageBox.Show(owner,CalibrationText("接头已保存，请勿重复提交；请核对项目，必要时联系维护人员。", "Joint saved. Do not submit again. Check the project and contact support if needed."),
                CalibrationText("接头已保存，请检查提示", "Joint saved; attention required"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
        private void ConfigureJointCustomerDiagnostics(HVX.ProjectCables.CableJointDialog dialog)
        { dialog.DiagnosticError=error=>WriteCalibrationDiagnostic("Joint editor: "+error); }
    }
}
