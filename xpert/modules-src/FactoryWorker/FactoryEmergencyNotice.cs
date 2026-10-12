namespace Xiongxin.FactoryTest
{
    // Presentation only. Does not reset the native emergency latch or dispatch outputs.
    internal sealed class FactoryEmergencyNotice
    {
        internal bool Active { get; private set; }
        internal void Observe(string key, bool pressureRun)
        { if (pressureRun && key == "EMERGENCY_ACTIVE") Active = true; }
        internal void Reset() { Active = false; }
        internal const string Title = "急停已动作 · 本轮锁存\n连续加压已停止";
        internal const string Message = "急停已动作，连续加压已停止。保持停机；由具备资质人员按本机规程确认断源，使用额定匹配的放电装置限流放电，经独立验电确认后接地，确认人员安全后方可操作设备。急停复位不自动恢复本次队列。";
    }
}
