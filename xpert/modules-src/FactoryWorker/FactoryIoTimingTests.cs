using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryIoTimingTests
    {
        internal static string Run()
        {
            int checks=0;Action<bool,string> check=(ok,why)=>{if(!ok)throw new Exception("IO timing regression: "+why);checks++;};
            var clock=new FakeClock();var timing=clock.Create(32);
            timing.RecordDigital(100000,100019,0,1,1,0);
            timing.RecordDigital(100019,100020,1,1,1,1);
            check(timing.DigitalCount==2 && timing.SnapshotEntries().Length==0 && timing.DigitalMaxMilliseconds==19,
                "ordinary DI only updates aggregate count/max; nonnegative status is preserved as allowed");
            var operation=timing.BeginOperation("ReadFeedback");
            check(timing.CurrentOperation=="ReadFeedback","operation context is visible without a lock");
            timing.RecordDigital(100020,100040,0,1,1,1);
            var slow=timing.SnapshotEntries().Single();
            check(slow.Kind=="DI" && slow.Context=="ReadFeedback" && slow.Ended-slow.Started==20 && slow.Gc0==-1,
                "exactly 20ms records actual monotonic bounds and current operation; absent GC is unknown");
            timing.RecordDigital(100040,100041,-2,1,1,1,"CapturedAtStart",1,2,3);
            timing.RecordDigital(100041,100042,0,0,1,1);
            timing.RecordDigital(100042,100043,0,1,0,1);
            timing.RecordDigital(100043,100044,0,1,1,2);
            check(timing.SnapshotEntries().Length==5,"each status/count/bytes/raw failure records detail even below 20ms");
            var error=timing.SnapshotEntries().Single(e=>e.Status==-2);
            check(error.Context=="CapturedAtStart" && error.Gc0==1 && error.Gc1==2 && error.Gc2==3,
                "optional captured context and caller GC deltas are retained verbatim");
            clock.Advance(53);operation.Dispose();operation.Dispose();
            check(timing.CurrentOperation=="" && timing.OperationsStarted==1 && timing.OperationsCompleted==1,
                "operation disposal is idempotent and clears context");
            var io=timing.SnapshotEntries().Single(e=>e.Kind=="IO");
            check(io.Operation=="ReadFeedback" && io.Ended-io.Started==53,"business IO duration uses the injected monotonic clock");

            var outer=timing.BeginOperation("outer");var inner=timing.BeginOperation("inner");
            outer.Dispose();check(timing.CurrentOperation=="inner","ending an outer scope does not erase an overlapping current operation");
            inner.Dispose();check(timing.CurrentOperation=="","completed outer scope cannot reappear as stale context");
            using(timing.BeginOperation("outer-live"))
            {using(timing.BeginOperation("inner-live")){}check(timing.CurrentOperation=="outer-live","normal nesting restores the live parent");}

            var bounded=clock.Create(3);
            for(int i=0;i<100;i++)bounded.RecordDigital(i*30,i*30+20,0,1,1,1);
            check(bounded.SnapshotEntries().Length==1 && bounded.Dropped==0 && bounded.DigitalOverwritten==99 && bounded.DigitalCount==100,
                "dedicated DI tail ring retains its newest row and counts overwritten history separately from drops");
            check(bounded.SnapshotEntries()[0].Started==99*30,"saturation retains the most recent detail instead of only the initial samples");
            var snapshot=bounded.SnapshotEntries();snapshot[0]=null;
            check(bounded.SnapshotEntries().All(e=>e!=null),"snapshot mutation cannot alter retained diagnostics");
            for(int i=0;i<100;i++)bounded.RecordDigital(i,i+1,0,1,1,0);
            check(bounded.Dropped==0 && bounded.DigitalOverwritten==99 && bounded.DigitalCount==200,"normal DI does not consume slots or count as dropped after detail saturation");

            var concurrent=clock.Create(64);var jobs=new Task[8];
            for(int worker=0;worker<jobs.Length;worker++)
            {
                int workerId=worker;
                jobs[worker]=Task.Run(()=>
                {
                    for(int i=0;i<1000;i++)
                    {
                        long start=workerId*100000L+i*50;
                        concurrent.RecordDigital(start,start+20+workerId,0,1,1,1);
                        if(i%100==0)concurrent.SnapshotEntries();
                    }
                });
            }
            Task.WaitAll(jobs);
            check(concurrent.DigitalCount==8000 && concurrent.DigitalMaxMilliseconds==27 && concurrent.Dropped+concurrent.DigitalOverwritten==7984,
                "concurrent producers preserve exact count/max and separately account for overwritten or unpublished detail");
            check(concurrent.SnapshotEntries().Length==16 && concurrent.SnapshotEntries().All(e=>e.Ended>=e.Started) &&
                concurrent.SnapshotEntries().Min(e=>e.BankSequence)==7985 && concurrent.SnapshotEntries().Max(e=>e.BankSequence)==8000,
                "concurrent snapshots only publish complete immutable rows");
            var contexts=clock.Create(64);
            for(int worker=0;worker<jobs.Length;worker++)jobs[worker]=Task.Run(()=>
            {for(int i=0;i<20;i++)using(contexts.BeginOperation("parallel")){clock.Advance(1);var ignored=contexts.CurrentOperation;}});
            Task.WaitAll(jobs);
            check(contexts.CurrentOperation=="" && contexts.OperationsStarted==160 && contexts.OperationsCompleted==160 &&
                contexts.Dropped+contexts.IoOverwritten==112 && contexts.SnapshotEntries().Length==48,
                "overlapping operation scopes finish without stale context or unbounded entries");
            var tail=clock.Create(16);
            tail.RecordDigital(0,21,-1,1,1,1);
            for(int i=0;i<2300;i++)using(tail.BeginOperation("ART.Monitor.ReadAnalog")){clock.Advance(1);}
            using(tail.BeginOperation("Outputs.Shutdown")){clock.Advance(3);}
            using(tail.BeginOperation("Backend.Dispose")){clock.Advance(4);}
            tail.RecordDigital(5000,5025,0,1,1,1);
            var tailRows=tail.SnapshotEntries();
            check(tailRows.Any(e=>e.Kind=="DI" && e.Status==-1) && tailRows.Any(e=>e.Kind=="DI" && e.Started==5000) &&
                tailRows.Any(e=>e.Operation=="Outputs.Shutdown") && tailRows.Any(e=>e.Operation=="Backend.Dispose"),
                "2300 business reads cannot evict reserved DI faults and the IO tail retains shutdown/disposal");
            check(tailRows.Length==14 && tail.IoOverwritten==2290 && tail.Dropped==0 &&
                tailRows.Zip(tailRows.Skip(1),(left,right)=>left.Sequence<right.Sequence).All(ordered=>ordered),
                "partitioned snapshots remain bounded and are exported in global reservation order");
            var one=clock.Create(1);one.RecordDigital(1,2,-1,1,1,1);
            using(one.BeginOperation("cleanup-with-capacity-one")){}
            one.RecordDigital(2,3,-2,1,1,1);
            check(one.SnapshotEntries().Length==1 && one.SnapshotEntries()[0].Status==-2 && one.Dropped==1 &&
                one.DigitalOverwritten==1 && one.OperationsCompleted==1,
                "capacity one preserves the newest DI and explicitly drops IO detail without blocking scope completion");

            bool failClock=false;
            var faulty=new FactoryIoTimingDiagnostics(2,()=>{if(failClock)throw new InvalidOperationException("fake clock failure");return 1;},clock.Utc,1000);
            failClock=true;bool businessRan=false;
            using(var failedStart=faulty.BeginOperation("must-not-block-business"))
            {check(failedStart!=null,"failed operation start returns a safe empty scope");businessRan=true;failedStart.Dispose();}
            check(businessRan && faulty.DiagnosticFailures==1 && faulty.OperationsStarted==0 && faulty.CurrentOperation=="",
                "begin clock failure cannot escape, publish a phantom context or prevent business execution");
            failClock=false;
            var failedScope=faulty.BeginOperation("cleanup");failClock=true;failedScope.Dispose();
            check(faulty.DiagnosticFailures==2 && faulty.CurrentOperation=="" && faulty.OperationsCompleted==1,
                "diagnostic failure during scope cleanup cannot escape or leave active context");

            string root=Path.Combine(Path.GetTempPath(),"Xiongxin-IoTiming-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string path=Path.Combine(root,"io-timing.tsv");
            var serialized=clock.Create(4);
            using(serialized.BeginOperation("read\twith\r\nnewline")){clock.Advance(25);}
            serialized.RecordDigital(100000,100020,-1,0,0,2,new string('x',800)+"\t\n",0,1,0);
            serialized.WriteSnapshot(path);
            string text=File.ReadAllText(path);string[] lines=File.ReadAllLines(path);
            check(text.Contains("NOT hardware/sample timestamps") && text.Contains("GC") && text.Contains("# DigitalCount\t1"),
                "snapshot states timestamp/GC limitations and retains aggregate summary");
            check(lines.Where(line=>line.Contains("\tIO\t") || line.Contains("\tDI\t")).Count()==2 &&
                lines.Where(line=>line.Contains("\tIO\t") || line.Contains("\tDI\t")).All(line=>line.Split('\t').Length==16),
                "control characters cannot break TSV rows or columns");
            check(text.Contains("# DigitalRetained\t1") && text.Contains("# IoRetained\t1") &&
                text.Contains("# DigitalOverwritten\t0") && text.Contains("# IoOverwritten\t0") &&
                text.Contains("# DigitalDropped\t0") && text.Contains("# IoDropped\t0"),
                "snapshot separately reports retained, overwritten and dropped counts for each partition");
            check(serialized.SnapshotEntries().Single(e=>e.Kind=="DI").Context.Length==512,
                "caller context length is bounded in retained memory");
            string saved=File.ReadAllText(path);serialized.RecordDigital(0,1,0,1,1,0);
            check(File.ReadAllText(path)==saved,"recording does not perform file IO or rewrite the last snapshot");
            string exactPath=Path.Combine(root,"derived-time.tsv");var exactClock=new FakeClock();var exact=exactClock.Create(2);
            exact.RecordDigital(100100,100125,0,1,1,1);exact.WriteSnapshot(exactPath);
            string exactText=File.ReadAllText(exactPath);
            check(exactText.Contains("StartUtcDerived\tEndUtcDerived") &&
                exactText.Contains(exactClock.Utc.AddMilliseconds(100).ToString("O",CultureInfo.InvariantCulture)) &&
                exactText.Contains(exactClock.Utc.AddMilliseconds(125).ToString("O",CultureInfo.InvariantCulture)),
                "both start and end UTC are derived from the documented fake monotonic anchor");
            try{exact.WriteSnapshot(root);throw new Exception("Expected snapshot file error");}
            catch(UnauthorizedAccessException){check(exact.DigitalCount==1,"snapshot file errors remain separate from recording/control state");}
            CheckWorkerSaveHelper(exact,root,check);
            return "IO timing diagnostics: "+checks+" offline checks PASS; fake clock, bounded concurrent recording, context, GC and TSV; HardwareAccessed=False; "+root;
        }

        private static void CheckWorkerSaveHelper(FactoryIoTimingDiagnostics timing,string root,Action<bool,string> check)
        {
            // Exercise only the static persistence helper, not Program.Main,
            // worker startup, SDK construction or any device-facing workflow.
            string success=Path.Combine(root,"worker-save");Directory.CreateDirectory(success);
            var events=new System.Collections.Generic.List<Tuple<string,string>>();
            Program.SaveIoTiming(timing,success,(code,detail)=>events.Add(Tuple.Create(code,detail)));
            string savedPath=Path.Combine(success,"io-timing.tsv");
            check(File.Exists(savedPath) && File.ReadAllText(savedPath).Contains("EndUtcDerived") &&
                events.Count==1 && events[0].Item1=="IO_TIMING_FILE" && events[0].Item2.Contains(savedPath),
                "worker helper saves the snapshot and reports its actual path");

            string missing=Path.Combine(root,"directory-intentionally-missing");events.Clear();
            Program.SaveIoTiming(timing,missing,(code,detail)=>events.Add(Tuple.Create(code,detail)));
            check(!Directory.Exists(missing) && events.Count==1 && events[0].Item1=="IO_TIMING_ERROR" &&
                events[0].Item2.Contains("不改本轮结果"),
                "missing-directory save failure becomes IO_TIMING_ERROR without creating or changing run state");

            int callbackCalls=0;
            Program.SaveIoTiming(timing,success,(code,detail)=>{callbackCalls++;throw new InvalidOperationException("fake report callback failure");});
            check(callbackCalls==2 && File.Exists(savedPath),
                "success-report callback failure and its error-report failure cannot escape the helper");
            events.Clear();
            Program.SaveIoTiming(timing,success,(code,detail)=>
            {
                events.Add(Tuple.Create(code,detail));
                if(code=="IO_TIMING_FILE")throw new InvalidOperationException("injected success notification failure");
            });
            check(File.Exists(savedPath) && events.Count==2 && events[1].Item1=="IO_TIMING_REPORT_ERROR" &&
                events[1].Item2.Contains("已保存") && events[1].Item2.Contains(savedPath) && !events[1].Item2.Contains("保存失败"),
                "saved file plus failed notification is never mislabeled as a failed file save");
            callbackCalls=0;
            Program.SaveIoTiming(timing,missing,(code,detail)=>{callbackCalls++;throw new InvalidOperationException("fake error report failure");});
            check(callbackCalls==1,"failed-save report callback failure is contained");

            var businessError=new InvalidOperationException("original fake business failure");Exception observed=null;
            try
            {
                try{throw businessError;}
                finally
                {
                    Program.SaveIoTiming(timing,missing,(code,detail)=>{throw new InvalidOperationException("diagnostic callback must not mask business failure");});
                }
            }
            catch(Exception ex){observed=ex;}
            check(ReferenceEquals(observed,businessError),
                "diagnostic save/report failure in finally preserves the exact original business exception");
            Program.SaveIoTiming(timing,success,null);
            check(File.Exists(savedPath),"worker helper also saves safely without a report callback");
        }

        private sealed class FakeClock
        {
            private long ticks=100000;
            internal readonly DateTime Utc=new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc);
            internal void Advance(long delta){Interlocked.Add(ref ticks,delta);}
            internal FactoryIoTimingDiagnostics Create(int capacity)
            {return new FactoryIoTimingDiagnostics(capacity,()=>Interlocked.Read(ref ticks),Utc,1000);}
        }
    }
}
