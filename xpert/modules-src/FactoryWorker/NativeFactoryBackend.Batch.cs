using System;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class NativeFactoryBackend
    {
        private FactoryBatchPermit activeBatchPermit;
        // Called once by BeginRound before any new SDK configuration/output.
        private void AcceptBatchPermit(FactorySettings requested)
        {
            if(activeBatchPermit!=null)throw new InvalidOperationException("上一批次轮次原生收尾尚未结束。");
            if(!requested.AutomaticBatch)return;
            if(requested.BatchPermit==null)throw new InvalidOperationException("自动批次必须由当前常驻服务授权，不能直接加载参数启动。");
            requested.BatchPermit.ConsumeForNative(SessionId,requested);
            activeBatchPermit=requested.BatchPermit;
        }
        // Called only after successful EndRound cleanup, never in its failure path.
        private void CompleteBatchPermit()
        {
            if(activeBatchPermit==null)return;
            activeBatchPermit.MarkNativeEnded(); activeBatchPermit=null;
        }
    }
}
