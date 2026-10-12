using System;
using System.Globalization;

namespace Xiongxin.OWTS.Desktop
{
    internal sealed partial class MainForm
    {
        void PressureEmergencyStatusChanged()
        {
            if(IsDisposed||Disposing||!IsHandleCreated)return;
            if(InvokeRequired){try{BeginInvoke((Action)PressureEmergencyStatusChanged);}catch(InvalidOperationException){}return;}
            if(activeModule==null)ApplyPressureEmergencyHomeStatus();
        }
        void ApplyPressureEmergencyHomeStatus()
        {
            if(deviceDetail==null||moduleHost==null||!state.DeviceConnected||!moduleHost.HasPressureDeviceOwner||
                moduleHost.PressureEmergencyCapturedUtc==DateTime.MinValue)return;
            string stage=moduleHost.PressureEmergencyStage;
            bool latched=stage=="EMERGENCY_LATCHED_RELEASED"||stage=="EMERGENCY_LATCHED_ACTIVE";
            string level=stage=="EMERGENCY_RELEASED"||stage=="EMERGENCY_LATCHED_RELEASED"?"HIGH":
                stage=="EMERGENCY_ACTIVE"||stage=="EMERGENCY_LATCHED_ACTIVE"?"LOW":stage=="EMERGENCY_PENDING"?"PENDING":"UNKNOWN";
            string text=(IsChinese?"指定设备已连接 · DI ":"Device connected · DI ")+level+
                (latched?(IsChinese?" · 输出已锁存":" · Output latched"):"")+" · "+
                moduleHost.PressureEmergencyCapturedUtc.ToLocalTime().ToString("HH:mm:ss",CultureInfo.InvariantCulture);
            // Actual sample time changes at the DI cadence; repaint the caption
            // only when its visible second or level changes, never the full GUI.
            if(deviceDetail.Text!=text)deviceDetail.Text=text;
        }
    }
}
