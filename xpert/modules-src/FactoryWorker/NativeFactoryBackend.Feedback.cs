using System;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class NativeFactoryBackend
    {
        // Bound to the same runner token for this round, cleared only after
        // runner cleanup. It never owns, restarts or cancels the independent DI.
        internal CancellationToken FeedbackReadCancellation;

        private void ObserveMonitorRead(FactoryAnalogReadReceipt receipt)
        {
            Exception reportError=null;
            if(receipt.Status < 0 || receipt.Actual != receipt.Requested)
            {
                var report=ConnectionReport;
                try { if(report!=null)report("AI_MONITOR_READ_RESULT",FactoryConnectionWatch.Stamp(receipt.Describe())); }
                catch(Exception error) { reportError=error; }
            }
            try { receipt.Validate(CheckObservedArtStatus); }
            catch(Exception readError)
            {
                if(reportError!=null)throw new AggregateException("ART读取及诊断报告均失败；保留原始状态及点数。",readError,reportError);
                throw;
            }
            if(reportError!=null)throw reportError;
        }
    }

    // The SDK count is initialized to -1 through the dedicated ref import.
    // Only a recognized pending status AND explicitly returned zero can wait.
    // A negative-status partial array is never consumed or silently discarded.
    internal sealed class FactoryAnalogReadReceipt
    {
        internal readonly int Status,Requested,Actual,ArrayLength;
        internal readonly uint AvailableBefore;
        internal readonly long ElapsedMilliseconds;
        internal FactoryAnalogReadReceipt(int status,int requested,int actual,int arrayLength,uint available,long elapsed)
        {Status=status;Requested=requested;Actual=actual;ArrayLength=arrayLength;AvailableBefore=available;ElapsedMilliseconds=elapsed;}
        internal string Describe()
        {
            return "ART monitor read status="+Status+"/"+FactoryEmergencyReadPolicy.OfficialStatusName(Status)+
                "; requestedPerChannel="+Requested+"; actualPerChannel="+Actual+
                "; availableBefore="+AvailableBefore+"; arrayLength="+ArrayLength+"; elapsedMs="+ElapsedMilliseconds;
        }
        internal void Validate(Action<int,string> check)
        {
            if(check==null)throw new ArgumentNullException("check");
            if(Requested>0 && ArrayLength==checked(Requested*3) &&
                FactoryEmergencyReadPolicy.IsZeroSamplePending(Status,Actual))
                throw new FactoryAnalogReadPendingException(this);
            check(Status,Describe());
            if(Status<0)throw new InvalidOperationException("ART negative read status cannot supply feedback: "+Describe());
            if(Requested<1 || Actual!=Requested || ArrayLength!=checked(Requested*3))
                throw new InvalidOperationException("ART 追读点数不足或数组无效："+Describe());
        }
    }

    internal sealed class FactoryAnalogReadPendingException : InvalidOperationException
    {
        internal readonly FactoryAnalogReadReceipt Receipt;
        internal FactoryAnalogReadPendingException(FactoryAnalogReadReceipt receipt)
            : base("ART暂未返回反馈样本："+(receipt==null?"unknown":receipt.Describe()))
        {
            if(receipt==null || !FactoryEmergencyReadPolicy.IsZeroSamplePending(receipt.Status,receipt.Actual))
                throw new ArgumentException("Only an explicit SDK zero-count pending receipt may wait.");
            Receipt=receipt;Data["ArtStatus"]=receipt.Status;Data["ActualSamplesPerChannel"]=receipt.Actual;
        }
    }
}
