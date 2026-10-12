using System;
using System.ComponentModel;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        [Browsable(false)] public Action<string> PressureDiagnosticSink { get; set; }

        void RecordPressureDiagnostic(string source, string detail)
        {
            string record=source+": "+detail;
            try {
                if(PressureDiagnosticSink!=null)PressureDiagnosticSink(record);
                else System.Diagnostics.Trace.WriteLine(record);
            } catch(Exception error) { System.Diagnostics.Trace.WriteLine("Pressure diagnostic logging failed: "+error); }
        }

        string PressureCaptureFailureText(Exception error)
        {
            RecordPressureDiagnostic("Capture",error==null?"No completion result.":error.ToString());
            return error is OperationCanceledException
                ? PressureUiText("采集已取消。请确认设备状态后重新开始。","Acquisition was cancelled. Verify the equipment state before starting again.")
                : PressureUiText("采集未完成。请检查设备连接和采集设置后重试；如仍失败，请联系维护人员。","Acquisition did not complete. Check the device connection and acquisition settings, then retry. If it still fails, contact support.");
        }
    }
}
