using System;
using System.Diagnostics;

namespace Xiongxin.FactoryTest
{
    // Local, reviewed adaptation of the frozen task39 worker. These values do
    // not change the timeout of AI/AO/DO, or permit an old HIGH to start output.
    internal static class FactoryEmergencyReadPolicy
    {
        internal const int AfterReturnDelayMs = 10, FirstReadTimeoutMs = 300;
        internal const int NoValidDataDeadlineMs = 500, OutputFreshnessMs = 100;
        // ART Technology ART-DAQ/Include/Art_DAQ.h, ArtDAQError_Timeout.
        internal const int ArtTimeout = -26802;
        internal const int ArtSamplesNotYetAvailable = -200284;
        internal static bool IsRetryableStatus(int status)
        { return status == ArtTimeout || status == ArtSamplesNotYetAvailable; }
        // The caller initializes the ref count to -1. An unchanged/partial
        // count is NOT evidence of an empty read, regardless of error text.
        internal static bool IsZeroSamplePending(int status,int actual)
        { return IsRetryableStatus(status) && actual == 0; }
        internal static bool DeadlineReached(long now, long lastValidOrStart)
        { return now - lastValidOrStart >= NoValidDataDeadlineMs; }
        internal static int ReadBudgetMs(long now, long lastValidOrStart)
        { return (int)Math.Max(0, Math.Min(FirstReadTimeoutMs, NoValidDataDeadlineMs - (now - lastValidOrStart))); }
        internal static bool IsFresh(long now, long sampleAt)
        { return now >= sampleAt && now - sampleAt <= OutputFreshnessMs; }
        internal static string OfficialStatusName(int status)
        {
            // Explicit names from the installed official header; neither text
            // matching nor an unrelated task's success establishes DI health.
            switch (status)
            {
                case -26802: return "ArtDAQError_Timeout";
                case -229779: return "ArtDAQError_SerivesCanNotConnect";
                case -229778: return "ArtDAQError_SendSerivesData";
                case -229776: return "ArtDAQError_RecieveSerivesData";
                case -229775: return "ArtDAQError_SerivesResponseError";
                case -50401: return "ArtDAQError_PALCommunicationsFault";
                case -50303: return "ArtDAQError_PALDeviceInitializationFault";
                case -50301: return "ArtDAQError_PALDeviceUnknown";
                case -50103: return "ArtDAQError_PALResourceReserved";
                case -200088: return "ArtDAQError_InvalidTask";
                case -200474: return "ArtDAQError_ErrorOperationTimedOut";
                case -200473: return "ArtDAQError_CannotReadWhenAutoStartFalseAndTaskNotRunningOrCommitted";
                case -200284: return "ArtDAQError_SamplesNotYetAvailable";
                case -200279: return "ArtDAQError_SamplesNoLongerAvailable";
                case -200278: return "ArtDAQError_SamplesWillNeverBeAvailable";
                case -88709: return "ArtDAQError_RuntimeAborted_Routing";
                default: return status >= 0 ? "SuccessOrWarning" : "UnclassifiedArtError";
            }
        }
    }

    internal sealed class FactoryArtCommunicationSnapshot
    {
        internal readonly bool HasObservation, HasFailure;
        internal readonly int Status;
        internal readonly string Operation;
        internal readonly long HostTicks;
        internal FactoryArtCommunicationSnapshot(bool observed, bool failed, int status, string operation, long hostTicks)
        { HasObservation=observed; HasFailure=failed; Status=status; Operation=operation; HostTicks=hostTicks; }
        internal string Describe()
        {
            return !HasObservation ? "OtherART=UNKNOWN（本会话没有其他任务返回证据；不是断线证明，也不是急停许可）" :
                "OtherART=" + Status + "/" + FactoryEmergencyReadPolicy.OfficialStatusName(Status) +
                "; operation=" + Operation + "; hostTicks=" + HostTicks +
                "; failureLatched=" + HasFailure + "；仅本会话已有操作的返回，不代替P0.4";
        }
    }

    // Separate from the lossy diagnostic ring. One instance belongs to one
    // NativeFactoryBackend session. It never opens/probes an additional task.
    internal sealed class FactoryArtCommunicationEvidence
    {
        private readonly object gate = new object();
        private FactoryArtCommunicationSnapshot latest = new FactoryArtCommunicationSnapshot(false,false,0,String.Empty,0);
        internal void Observe(int status, string operation)
        {
            lock (gate)
            {
                if (latest.HasFailure) return; // a later success cannot erase a session fault
                latest = new FactoryArtCommunicationSnapshot(true,status < 0,status,operation,Stopwatch.GetTimestamp());
            }
        }
        internal FactoryArtCommunicationSnapshot Snapshot() { lock (gate) return latest; }
    }
}
