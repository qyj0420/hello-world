using System;

namespace Xiongxin.FactoryTest
{
    // Called only by the serialized device owner; never by the DI reader.
    // Each action must return successfully before ownership advances. Keeping
    // this tiny lifecycle separate makes clear-failure behavior testable offline.
    internal sealed class FactoryIdleDiRebuildPolicy
    {
        private bool attempted;
        internal bool Attempted { get { return attempted; } }
        internal void Rebuild(bool verifiedIdleNoOutput,Action stopOldReader,Action probeExistingIdentity,Action clearOldTask,Action createAndValidateReplacement)
        {
            if(!verifiedIdleNoOutput)throw new InvalidOperationException("DI重建仅允许已确认无输出的空闲会话。");
            if(attempted)throw new InvalidOperationException("本会话已尝试一次DI重建；禁止循环清错或自动重建。");
            attempted=true;
            stopOldReader();
            probeExistingIdentity();
            clearOldTask();
            createAndValidateReplacement();
        }
    }
}
