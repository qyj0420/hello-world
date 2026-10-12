using System;

namespace Xiongxin.Owts.Acquisition.Live
{
    /// <summary>
    /// 空闲 DI 每会话一次重建（精简自 FactoryIdleDiRebuildPolicy）。
    /// 仅无输出且 owner 串行时；加压轮次中禁用。
    /// </summary>
    public sealed class IdleDiRebuildPolicy
    {
        bool _attempted;

        public bool Attempted { get { return _attempted; } }

        public void Rebuild(
            bool verifiedIdleNoOutput,
            Action stopOldReader,
            Action probeExistingIdentity,
            Action clearOldTask,
            Action createAndValidateReplacement)
        {
            if (!verifiedIdleNoOutput)
                throw new InvalidOperationException("DI 重建仅允许已确认无输出的空闲会话。");
            if (_attempted)
                throw new InvalidOperationException("本会话已尝试一次 DI 重建；禁止循环清错或自动重建。");
            if (stopOldReader == null || probeExistingIdentity == null || clearOldTask == null || createAndValidateReplacement == null)
                throw new ArgumentNullException();
            _attempted = true;
            stopOldReader();
            probeExistingIdentity();
            clearOldTask();
            createAndValidateReplacement();
        }
    }
}
