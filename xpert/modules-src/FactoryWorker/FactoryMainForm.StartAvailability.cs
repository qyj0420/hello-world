using System;
using System.Globalization;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        // UI readiness is a convenience guard, not hardware permission. The worker
        // must still acquire its own current DI sample before enabling any output.
        private bool CanStartPressure(out string reason)
        {
            if(batchRevocationFailed)
            {reason="批次停止记录未确认，请关闭程序并检查日志后再启动。";return false;}
            if(busy || scanning || exporting || labelProcessing || closeAfterStop || workerConnectionOwned || connectionTransition)
            {reason="当前操作或停止收尾尚未结束，请等待资源释放。";return false;}
            // Reading Value here would commit the user's unfinished target edit.
            decimal target;
            bool readOnlyZero=Decimal.TryParse(targetKv.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out target) && target==0;
            if(mode.SelectedIndex==0 || readOnlyZero){reason="";return true;}
            if(String.IsNullOrEmpty(pinnedArtSerial) || String.IsNullOrEmpty(pinnedPicoSerial) ||
                connectionIdentity!=pinnedArtSerial+"|"+pinnedPicoSerial)
            {reason="等待当前序列号设备的只读急停状态；非零加压尚未就绪。";return false;}
            if(artLink=="LOW" || artLink=="LOW_LATCHED")
            {reason="已读到P0.4低电平，急停许可不满足；非零电压禁止开始，等待重新验证。";return false;}
            if(artLink!="HIGH" || artSeen>DateTime.UtcNow || DateTime.UtcNow-artSeen>=TimeSpan.FromSeconds(2))
            {reason="P0.4状态未知、读取异常或已过期；非零电压禁止开始加压。";return false;}
            reason="";return true;
        }

        private void RefreshStartAvailability()
        {
            string reason;
            bool enabled=CanStartPressure(out reason);
            if(start.Enabled!=enabled)start.Enabled=enabled;
            if(pulseTip.GetToolTip(start)!=reason)pulseTip.SetToolTip(start,reason);
        }
    }
}
