using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Xiongxin.FactoryTest
{
    // Host-side diagnostics only. Recording never waits on a lock or writes a
    // file, and it must never be consulted as a control/permission decision.
    internal sealed class FactoryIoTimingDiagnostics
    {
        internal const int DefaultCapacity=1024;
        private static readonly IDisposable EmptyScope=new EmptyOperationScope();
        private readonly TimingRing digitalEntries,ioEntries;
        private readonly Func<long> timestamp;
        private readonly long frequency,anchorTicks;
        private readonly DateTime anchorUtc;
        private long detailSequence,digitalCount,digitalMaxTicks,digitalSlowCount,digitalFaultCount;
        private long operationsStarted,operationsCompleted,diagnosticFailures;
        private OperationScope currentOperation;

        internal FactoryIoTimingDiagnostics(int capacity=DefaultCapacity)
            :this(capacity,Timestamp,DateTime.UtcNow,Stopwatch.Frequency) {}

        // Injected monotonic clock is exclusively for managed offline fixtures.
        internal FactoryIoTimingDiagnostics(int capacity,Func<long> clock,DateTime utc,long ticksPerSecond)
        {
            if(capacity<1 || capacity>65536)throw new ArgumentOutOfRangeException("capacity");
            if(clock==null)throw new ArgumentNullException("clock");
            if(ticksPerSecond<=0)throw new ArgumentOutOfRangeException("ticksPerSecond");
            int digitalCapacity=Math.Max(1,capacity/4);
            digitalEntries=new TimingRing(digitalCapacity);ioEntries=new TimingRing(capacity-digitalCapacity);
            timestamp=clock;frequency=ticksPerSecond;
            anchorUtc=utc.ToUniversalTime();anchorTicks=clock();
        }

        internal static long Timestamp() {return Stopwatch.GetTimestamp();}
        internal int Capacity {get{return digitalEntries.Entries.Length+ioEntries.Entries.Length;}}
        internal long DigitalCount {get{return Interlocked.Read(ref digitalCount);}}
        internal double DigitalMaxMilliseconds {get{return ToMilliseconds(Interlocked.Read(ref digitalMaxTicks));}}
        internal long Dropped {get{return Interlocked.Read(ref digitalEntries.Dropped)+Interlocked.Read(ref ioEntries.Dropped);}}
        internal long DigitalOverwritten {get{return Interlocked.Read(ref digitalEntries.Overwritten);}}
        internal long IoOverwritten {get{return Interlocked.Read(ref ioEntries.Overwritten);}}
        internal long OperationsStarted {get{return Interlocked.Read(ref operationsStarted);}}
        internal long OperationsCompleted {get{return Interlocked.Read(ref operationsCompleted);}}
        internal long DiagnosticFailures {get{return Interlocked.Read(ref diagnosticFailures);}}
        internal string CurrentOperation
        {
            get
            {
                var current=Interlocked.CompareExchange(ref currentOperation,null,null);
                while(current!=null && current.HasEnded)current=current.Previous;
                return current==null?"":current.Name;
            }
        }

        internal IDisposable BeginOperation(string operation)
        {
            try
            {
                var scope=new OperationScope(this,Bounded(operation),timestamp());
                OperationScope previous;
                do
                {
                    previous=Interlocked.CompareExchange(ref currentOperation,null,null);
                    scope.Previous=previous;
                }
                while(Interlocked.CompareExchange(ref currentOperation,scope,previous)!=previous);
                Interlocked.Increment(ref operationsStarted);
                return scope;
            }
            catch(Exception)
            {
                // Diagnostic clock/context allocation errors must not prevent
                // the business I/O enclosed by a using statement from running.
                Interlocked.Increment(ref diagnosticFailures);return EmptyScope;
            }
        }

        internal void RecordDigital(long started,long ended,int status,int count,int bytes,byte raw)
        {RecordDigitalCore(started,ended,status,count,bytes,raw,null,-1,-1,-1);}

        // A caller can capture context and GC.CollectionCount differences around
        // its existing DI call. -1 means not sampled, not "no collection".
        internal void RecordDigital(long started,long ended,int status,int count,int bytes,byte raw,
            string operationContext,int gc0Delta,int gc1Delta,int gc2Delta)
        {RecordDigitalCore(started,ended,status,count,bytes,raw,operationContext,gc0Delta,gc1Delta,gc2Delta);}

        private void RecordDigitalCore(long started,long ended,int status,int count,int bytes,byte raw,
            string context,int gc0,int gc1,int gc2)
        {
            try
            {
                Interlocked.Increment(ref digitalCount);
                long elapsed=ElapsedTicks(started,ended),old=Interlocked.Read(ref digitalMaxTicks);
                while(elapsed>old)
                {
                    long observed=Interlocked.CompareExchange(ref digitalMaxTicks,elapsed,old);
                    if(observed==old)break;old=observed;
                }
                bool slow=ToMilliseconds(elapsed)>=20.0;
                bool fault=status<0 || count!=1 || bytes!=1 || raw>1;
                if(slow)Interlocked.Increment(ref digitalSlowCount);
                if(fault)Interlocked.Increment(ref digitalFaultCount);
                if(!slow && !fault)return;
                long sequence,bankSequence;if(!Reserve(digitalEntries,out sequence,out bankSequence))return;
                Publish(digitalEntries,new FactoryIoTimingEntry("DI",started,ended,"DigitalRead",
                    Bounded(context??CurrentOperation),status,count,bytes,raw,gc0,gc1,gc2,sequence,bankSequence));
            }
            catch(Exception){Interlocked.Increment(ref diagnosticFailures);Interlocked.Increment(ref digitalEntries.Dropped);}
        }

        private bool Reserve(TimingRing ring,out long sequence,out long bankSequence)
        {
            sequence=Interlocked.Increment(ref detailSequence);
            bankSequence=Interlocked.Increment(ref ring.Claimed);
            if(ring.Entries.Length>0 && sequence>0 && bankSequence>0)return true;
            Interlocked.Increment(ref ring.Dropped);return false;
        }
        private static void Publish(TimingRing ring,FactoryIoTimingEntry entry)
        {
            int slot=(int)((entry.BankSequence-1)%ring.Entries.Length);
            while(true)
            {
                var prior=Interlocked.CompareExchange(ref ring.Entries[slot],null,null);
                // A producer paused before publishing must not overwrite a
                // newer generation already published into the same ring slot.
                if(prior!=null && prior.BankSequence>=entry.BankSequence)
                {Interlocked.Increment(ref ring.Dropped);return;}
                if(Interlocked.CompareExchange(ref ring.Entries[slot],entry,prior)==prior)
                {if(prior!=null)Interlocked.Increment(ref ring.Overwritten);return;}
            }
        }
        private void CompleteOperation(OperationScope scope)
        {
            try
            {
                Interlocked.Increment(ref operationsCompleted);
                long ended=timestamp(),sequence,bankSequence;
                if(Reserve(ioEntries,out sequence,out bankSequence))
                    Publish(ioEntries,new FactoryIoTimingEntry("IO",scope.Started,ended,scope.Name,"",0,0,0,0,-1,-1,-1,sequence,bankSequence));
            }
            catch(Exception){Interlocked.Increment(ref diagnosticFailures);Interlocked.Increment(ref ioEntries.Dropped);}
            finally
            {
                // Unwind completed nested/overlapping scopes without restoring
                // an already completed outer scope as the active operation.
                OperationScope current,replacement;
                do
                {
                    current=Interlocked.CompareExchange(ref currentOperation,null,null);
                    replacement=current;
                    while(replacement!=null && replacement.HasEnded)replacement=replacement.Previous;
                    if(current==replacement)break;
                }
                while(Interlocked.CompareExchange(ref currentOperation,replacement,current)!=current);
            }
        }

        internal FactoryIoTimingEntry[] SnapshotEntries()
        {
            var snapshot=new System.Collections.Generic.List<FactoryIoTimingEntry>(Capacity);
            foreach(var ring in new[]{digitalEntries,ioEntries})
            {
                for(int i=0;i<ring.Entries.Length;i++)
                {
                    var entry=Interlocked.CompareExchange(ref ring.Entries[i],null,null);
                    if(entry!=null)snapshot.Add(entry);
                }
            }
            snapshot.Sort((left,right)=>left.Sequence.CompareTo(right.Sequence));
            return snapshot.ToArray();
        }

        // Only call after worker/DI cleanup, on a non-DI thread. I/O exceptions
        // intentionally reach that caller, which must catch them independently
        // of control cleanup. A snapshot is non-destructive and best-effort if
        // the caller takes it before producers have finished.
        internal void WriteSnapshot(string path)
        {
            var snapshot=SnapshotEntries();
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(false)))
            {
                writer.WriteLine("# FactoryIoTiming\t1");
                writer.WriteLine("# Clock\tStopwatch monotonic host time; UTC derived from process anchor; NOT hardware/sample timestamps.");
                writer.WriteLine("# Scope\tTiming/context correlation only; no permission, timeout adjustment or causal proof. IO rows mean scope ended, not operation succeeded.");
                writer.WriteLine("# GC\t-1=not sampled; nonnegative values are caller-observed collection-count deltas, not GC pause duration.");
                writer.WriteLine("# Retention\tSeparate latest-detail rings: DI approximately 1/4, IO remainder; capacity=1 reserves DI only. Overwritten=older retained row replaced; Dropped=detail never published. Sequence orders reservations, not hardware events; concurrent snapshots are best-effort.");
                writer.WriteLine("# AnchorUtc\t"+anchorUtc.ToString("O",CultureInfo.InvariantCulture));
                WriteSummary(writer,"AnchorTicks",anchorTicks);WriteSummary(writer,"Frequency",frequency);
                WriteSummary(writer,"Capacity",Capacity);WriteSummary(writer,"Details",snapshot.Length);
                WriteSummary(writer,"DigitalCapacity",digitalEntries.Entries.Length);WriteSummary(writer,"IoCapacity",ioEntries.Entries.Length);
                int digitalRetained=0;foreach(var entry in snapshot)if(entry.Kind=="DI")digitalRetained++;
                WriteSummary(writer,"DigitalRetained",digitalRetained);WriteSummary(writer,"IoRetained",snapshot.Length-digitalRetained);
                WriteSummary(writer,"DigitalOverwritten",DigitalOverwritten);WriteSummary(writer,"IoOverwritten",IoOverwritten);
                WriteSummary(writer,"DigitalDropped",Interlocked.Read(ref digitalEntries.Dropped));WriteSummary(writer,"IoDropped",Interlocked.Read(ref ioEntries.Dropped));
                WriteSummary(writer,"Dropped",Dropped);WriteSummary(writer,"DiagnosticFailures",DiagnosticFailures);
                WriteSummary(writer,"DigitalCount",DigitalCount);
                writer.WriteLine("# DigitalMaxMs\t"+DigitalMaxMilliseconds.ToString("0.######",CultureInfo.InvariantCulture));
                WriteSummary(writer,"DigitalSlowCount",Interlocked.Read(ref digitalSlowCount));
                WriteSummary(writer,"DigitalFaultCount",Interlocked.Read(ref digitalFaultCount));
                WriteSummary(writer,"OperationsStarted",OperationsStarted);WriteSummary(writer,"OperationsCompleted",OperationsCompleted);
                writer.WriteLine("# CurrentOperation\t"+Sanitize(CurrentOperation));
                writer.WriteLine("Sequence\tKind\tStartedTicks\tEndedTicks\tStartUtcDerived\tEndUtcDerived\tElapsedMs\tOperation\tMainOperationContext\tStatus\tCount\tBytes\tRaw\tGc0Delta\tGc1Delta\tGc2Delta");
                foreach(var entry in snapshot)
                {
                    string digital=entry.Kind=="DI"?entry.Status+"\t"+entry.Count+"\t"+entry.Bytes+"\t"+entry.Raw:"\t\t\t";
                    writer.WriteLine(entry.Sequence.ToString(CultureInfo.InvariantCulture)+"\t"+entry.Kind+"\t"+entry.Started.ToString(CultureInfo.InvariantCulture)+"\t"+
                        entry.Ended.ToString(CultureInfo.InvariantCulture)+"\t"+DerivedUtc(entry.Started)+"\t"+DerivedUtc(entry.Ended)+"\t"+
                        ToMilliseconds(ElapsedTicks(entry.Started,entry.Ended)).ToString("0.######",CultureInfo.InvariantCulture)+"\t"+
                        Sanitize(entry.Operation)+"\t"+Sanitize(entry.Context)+"\t"+digital+"\t"+
                        entry.Gc0+"\t"+entry.Gc1+"\t"+entry.Gc2);
                }
            }
        }
        private static void WriteSummary(TextWriter writer,string name,long value)
        {writer.WriteLine("# "+name+"\t"+value.ToString(CultureInfo.InvariantCulture));}
        private string DerivedUtc(long ticks)
        {
            try{return anchorUtc.AddSeconds(((double)ticks-anchorTicks)/frequency).ToString("O",CultureInfo.InvariantCulture);}
            catch(ArgumentOutOfRangeException){return "OUT_OF_RANGE";}
        }
        private double ToMilliseconds(long ticks) {return (double)ticks*1000.0/frequency;}
        private static long ElapsedTicks(long started,long ended)
        {
            if(ended<started)return 0;
            long delta=unchecked(ended-started);return delta<0?Int64.MaxValue:delta;
        }
        private static string Bounded(string value)
        {return String.IsNullOrEmpty(value)?"":value.Length>512?value.Substring(0,512):value;}
        private static string Sanitize(string value)
        {return Bounded(value).Replace('\t',' ').Replace('\r',' ').Replace('\n',' ');}

        private sealed class EmptyOperationScope : IDisposable
        {
            public void Dispose() {}
        }

        private sealed class TimingRing
        {
            internal readonly FactoryIoTimingEntry[] Entries;
            internal long Claimed,Overwritten,Dropped;
            internal TimingRing(int capacity){Entries=new FactoryIoTimingEntry[capacity];}
        }

        private sealed class OperationScope : IDisposable
        {
            private readonly FactoryIoTimingDiagnostics owner;
            internal readonly string Name;
            internal readonly long Started;
            internal OperationScope Previous;
            private int ended;
            internal bool HasEnded {get{return Interlocked.CompareExchange(ref ended,0,0)!=0;}}
            internal OperationScope(FactoryIoTimingDiagnostics diagnostics,string name,long started)
            {owner=diagnostics;Name=name;Started=started;}
            public void Dispose()
            {if(Interlocked.Exchange(ref ended,1)==0)owner.CompleteOperation(this);}
        }
    }

    internal sealed class FactoryIoTimingEntry
    {
        internal readonly string Kind,Operation,Context;
        internal readonly long Started,Ended,Sequence,BankSequence;
        internal readonly int Status,Count,Bytes,Raw,Gc0,Gc1,Gc2;
        internal FactoryIoTimingEntry(string kind,long started,long ended,string operation,string context,
            int status,int count,int bytes,int raw,int gc0,int gc1,int gc2,long sequence,long bankSequence)
        {Kind=kind;Started=started;Ended=ended;Operation=operation;Context=context;Status=status;Count=count;Bytes=bytes;Raw=raw;Gc0=gc0;Gc1=gc1;Gc2=gc2;Sequence=sequence;BankSequence=bankSequence;}
    }
}
