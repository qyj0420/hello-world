using System;

namespace HVX.Task17.Location
{
    // Per-call logical reservations, not a CLR RSS or instruction-cycle measurement.
    // Limits can be reduced for contracts; production defaults cannot be enlarged here.
    internal sealed class EighthKernelLimits
    {
        public int MeasurementPageSamples=4096;
        public long MaximumWorkUnits=1000000000,MaximumLogicalBytes=512L*1024*1024,MaximumMetadataBytes=128L*1024*1024;
        public int MaximumRecoveryChecks=200000,MaximumCandidates=200000,MaximumPropagationComparisons=200000,MaximumGeometrySteps=2000000;
        public long MaximumRecoverySamples=8L*8388608;
        internal EighthKernelLimits Snapshot()
        {
            if(MeasurementPageSamples<1||MeasurementPageSamples>8388608||MaximumWorkUnits<0||MaximumWorkUnits>1000000000||
                MaximumLogicalBytes<0||MaximumLogicalBytes>512L*1024*1024||MaximumMetadataBytes<0||MaximumMetadataBytes>128L*1024*1024||
                MaximumRecoveryChecks<0||MaximumRecoveryChecks>200000||MaximumCandidates<0||MaximumCandidates>200000||
                MaximumPropagationComparisons<0||MaximumPropagationComparisons>200000||MaximumGeometrySteps<0||MaximumGeometrySteps>2000000||
                MaximumRecoverySamples<0||MaximumRecoverySamples>8L*8388608)throw new ArgumentException("invalid_kernel_resource_limits");
            return (EighthKernelLimits)MemberwiseClone();
        }
    }
    internal sealed class EighthResourceReceipt
    {
        public long WorkUnits,LogicalBytes,MetadataBytes,MeasurementWorkUnits,CorrelationSamples,StandardSamplesMaterialized,RecoverySamplesMaterialized;
        public int StandardPackets,RecoveredPackets,RecoveryChecks,Candidates,PropagationComparisons,GeometrySteps;
        public bool Complete;public string FailureReason="";
        public string Accounting="Kernel-call logical charges: r02 measurement sample/sort work, compatibility sample/front copies, cumulative recovery allocations (including discarded/overlap windows), full-support correlation sample iterations, graph visits/sorts/container reservations and propagation-summary text. Caller raw/CAL inputs and wrapper display/filter/equivalent/final-matrix allocations excluded; wrapper retains its existing independent limits and transaction. Not measured CLR RSS or CPU cycles; never reset at page/phase boundaries.";
    }
    internal sealed class EighthBudgetExceeded:Exception
    {internal EighthBudgetExceeded(string reason):base(reason){}}
    internal sealed class EighthKernelMeter
    {
        internal readonly EighthKernelLimits Limits;internal readonly EighthResourceReceipt Receipt=new EighthResourceReceipt();
        internal EighthKernelMeter(EighthKernelLimits limits){Limits=limits.Snapshot();}
        internal long RemainingWork {get{return Limits.MaximumWorkUnits-Receipt.WorkUnits;}}
        internal long RemainingBytes {get{return Limits.MaximumLogicalBytes-Receipt.LogicalBytes;}}
        internal long RemainingMetadata {get{return Limits.MaximumMetadataBytes-Receipt.MetadataBytes;}}
        internal void Fail(string reason){if(Receipt.FailureReason.Length==0)Receipt.FailureReason=reason;throw new EighthBudgetExceeded(Receipt.FailureReason);}
        void Ready(){if(Receipt.FailureReason.Length!=0)throw new EighthBudgetExceeded(Receipt.FailureReason);}
        internal void Work(long units,string phase){Ready();if(units<0||units>RemainingWork)Fail("kernel_work_budget_exceeded:"+phase+":no_prefix");Receipt.WorkUnits+=units;}
        internal void Bytes(long bytes,string phase){Ready();if(bytes<0||bytes>RemainingBytes)Fail("kernel_storage_budget_exceeded:"+phase+":no_prefix");Receipt.LogicalBytes+=bytes;}
        internal void Meta(long bytes,string phase){Ready();if(bytes<0||bytes>RemainingMetadata)Fail("kernel_metadata_budget_exceeded:"+phase+":no_prefix");Bytes(bytes,phase);Receipt.MetadataBytes+=bytes;}
        internal void Sort(int n,string phase){int lg=0;long size=1;while(size<n){size*=2;lg++;}Work(2L*n*lg,phase);}
        internal void RecoveryCheck(){Ready();if(Receipt.RecoveryChecks>=Limits.MaximumRecoveryChecks)Fail("recovery_checks_budget_exceeded_no_prefix");Work(1,"recovery_check");Receipt.RecoveryChecks++;}
        internal void RecoverySamples(int n){Ready();if(n<0||n>Limits.MaximumRecoverySamples-Receipt.RecoverySamplesMaterialized)Fail("recovery_samples_budget_exceeded_no_prefix");Bytes(32+8L*n,"recovery_sample_array");Receipt.RecoverySamplesMaterialized+=n;}
        internal void Candidate(){Ready();if(Receipt.Candidates>=Limits.MaximumCandidates)Fail("candidate_budget_exceeded_no_prefix_acceptance");Work(1,"candidate");Meta(768,"candidate_pair_and_list");Receipt.Candidates++;}
        internal void PropagationComparison(){Ready();if(Receipt.PropagationComparisons>=Limits.MaximumPropagationComparisons)Fail("propagation_comparison_budget_exceeded_no_prefix");Work(1,"propagation_comparison");Receipt.PropagationComparisons++;}
        internal void Geometry(){Ready();if(Receipt.GeometrySteps>=Limits.MaximumGeometrySteps)Fail("propagation_geometry_budget_exceeded_no_prefix");Work(1,"geometry");Receipt.GeometrySteps++;}
        internal void Correlation(long samples,bool interpolated){Work(samples*(interpolated?8L:4L),"full_support_correlation");Receipt.CorrelationSamples+=samples;}
    }
    internal static class EighthKernelResources
    {
        [ThreadStatic] internal static EighthKernelMeter Current;
        internal sealed class Scope:IDisposable
        {
            readonly EighthKernelMeter previous;internal readonly EighthKernelMeter Meter;
            internal Scope(EighthKernelLimits limits){previous=Current;Meter=new EighthKernelMeter(limits);Current=Meter;}
            public void Dispose(){Current=previous;}
        }
        internal static Scope Begin(EighthKernelLimits limits){return new Scope(limits??new EighthKernelLimits());}
        internal static void Work(long n,string phase){if(Current!=null)Current.Work(n,phase);}
        internal static void Meta(long n,string phase){if(Current!=null)Current.Meta(n,phase);}
        internal static void Sort(int n,string phase){if(Current!=null)Current.Sort(n,phase);}
        internal static void Fail(string reason){if(Current!=null)Current.Fail(reason);throw new EighthBudgetExceeded(reason);}
    }
}
