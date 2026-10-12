using System;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private string qualityAuditWarning="";
        private void AuditQualityOperation(int phase,string level,string action,string outcome,string detail)
        {
            string error;
            qualityAuditWarning=CalibrationProjectAudit.TryWrite(ProjectPath,PhaseCodeFromIndex(phase),level,action,outcome,detail,out error)
                ? "" : CalibrationText("校准操作记录未保存，请检查项目目录后重试。","Calibration operation record was not saved. Check the project folder and retry.");
            if(!String.IsNullOrEmpty(qualityAuditWarning))WriteCalibrationDiagnostic("Calibration operation audit was not written: "+error);
        }
    }
}
