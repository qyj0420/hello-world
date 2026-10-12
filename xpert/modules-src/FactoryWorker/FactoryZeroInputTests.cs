using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal static class FactoryZeroInputTests
    {
        internal static string Run()
        {
            int checks = 0;
            foreach (bool high in new[] { false, true })
            {
                var s = Settings(); var clock = new Clock(); var input = new Input(clock) { High = high, Voltage = 1 };
                var stages = new List<string>();
                var r = FactoryZeroInputRunner.Run(s,input,clock,(key,detail) => stages.Add(key),CancellationToken.None);
                Require(r.Completed && r.ReadOnlyAcquisition && !r.ResidualSafe && r.CompleteCaptureAvailable() && input.Disposed,
                    "P04 high or low and high measured AI4 allow input capture, but never imply discharged"); checks++;
                Require(stages.Contains("RESIDUAL_WARNING") && stages.Contains("STEP_HV_ON_SKIPPED") && stages.Contains("STEP_OPTICAL_SKIPPED") &&
                    !stages.Contains("STEP_HV_ON_PASSED") && !stages.Contains("STEP_DISCHARGE_PASSED"),"no output/safety pass for zero read-only"); checks++;
                Require(FactoryRangeReview.Evaluate(r).WindowValid,"zero input data support range review without discharge claim"); checks++;
                r.Settings.TargetKv = 5; Require(!r.CompleteCaptureAvailable(),"read-only flag cannot authorize nonzero range retry"); checks++;
            }
            var settings = Settings(); settings.Simulation = false; settings.FeedbackWiringConfirmed = false;
            foreach(string moment in new[]{"STEP_ARM_PASSED","STEP_READ_RUNNING"})
            {
                var c=new Clock();var input=new Input(c){High=true};
                var r=FactoryZeroInputRunner.Run(Settings(),input,c,(k,t)=>{if(k==moment)input.High=false;},CancellationToken.None);
                Require(!input.High && r.Completed && input.Reads==1 && r.ReadOnlyAcquisition && !r.ResidualSafe,"P0.4 drops in later read-only stage: "+moment);checks++;
            }
            settings.Input.ArtSerial = "1023417009"; settings.Input.PicoSerial = "13000/0059";
            settings.Validate(FactoryRunKind.Pressure); checks++;
            settings.TargetKv = 5; Throws(() => settings.Validate(FactoryRunKind.Pressure)); checks++;
            settings.TargetKv = 0;
            Throws(() => new NativeFactoryBackend().Initialize(settings,false)); checks++;
            settings.Simulation = true;
            var wrongClock = new Clock(); var wrongInput = new Input(wrongClock); settings.TargetKv = 5;
            Throws(() => FactoryZeroInputRunner.Run(settings,wrongInput,wrongClock,null,CancellationToken.None));
            Require(wrongInput.Initializations == 0,"zero adapter cannot initialize nonzero settings"); checks++;
            settings = Settings(); var timeoutClock = new Clock(); var timeout = new Input(timeoutClock) { NeverComplete = true };
            Throws(() => FactoryZeroInputRunner.Run(settings,timeout,timeoutClock,null,CancellationToken.None));
            Require(timeout.Disposed,"timeout always disposes input resources"); checks++;
            var cancelClock = new Clock(); var cancelInput = new Input(cancelClock);
            using (var cts = new CancellationTokenSource())
            {
                Throws(() => FactoryZeroInputRunner.Run(settings,cancelInput,cancelClock,(key,detail) => { if (key == "STEP_ARM_PASSED") cts.Cancel(); },cts.Token));
                Require(cancelInput.Disposed && cancelInput.Reads == 0,"cancel after arm does not publish full capture"); checks++;
            }
            var errorClock = new Clock(); var errorInput = new Input(errorClock) { FailDispose = true };
            Throws(() => FactoryZeroInputRunner.Run(settings,errorInput,errorClock,null,CancellationToken.None)); checks++;
            foreach(string moment in new[]{"before-init","STEP_INIT_PASSED","STEP_ARM_PASSED","STEP_READ_PASSED"})
            foreach(bool cleanupFailure in new[]{false,true})
            using(var stop=new CancellationTokenSource())
            {
                var c=new Clock();var input=new Input(c){FailDispose=cleanupFailure};Exception observed=null;
                if(moment=="before-init")stop.Cancel();
                try {FactoryZeroInputRunner.Run(Settings(),input,c,(key,text)=>{if(key==moment)stop.Cancel();},stop.Token);}
                catch(Exception e){observed=e;}
                Require(input.Disposed,"stop always reaches actual input disposal "+moment);checks++;
                Require(cleanupFailure?observed is FactoryRunException:observed is FactoryInputStopCompletedException,
                    "only single cancellation plus successful cleanup receives typed input-stop fact "+moment);checks++;
                if(!cleanupFailure){var r=((FactoryInputStopCompletedException)observed).PartialResult;
                    Require(!r.Completed&&r.ReadOnlyAcquisition&&!r.ResidualSafe,"input-stop proof never invents complete capture or discharged state");checks++;}
            }
            foreach (var m in typeof(IFactoryZeroInput).GetMethods())
                Require(!m.Name.Contains("Hv") && !m.Name.Contains("Optical") && !m.Name.Contains("Ao") && !m.Name.Contains("Shutdown"),"zero capability has no output methods"); checks++;
            checks+=StatusPublisherChecks();
            return "PASS zero input-only regressions=" + checks + "; fake-only; no device access";
        }
        private static int StatusPublisherChecks()
        {
            int checks=0,level=0,reads=0,readerId=0;
            var events=new ConcurrentQueue<Tuple<string,string>>();
            using(var status=new FactoryZeroEmergencyStatus(ms=>{
                int id=Thread.CurrentThread.ManagedThreadId;
                if(Interlocked.CompareExchange(ref readerId,id,0)!=0&&readerId!=id)throw new Exception("second DI reader");
                Interlocked.Increment(ref reads);return Volatile.Read(ref level)!=0;
            },(key,text)=>events.Enqueue(Tuple.Create(key,text))))
            {
                status.Start();Require(!status.ReadCurrent(),"LOW is valid for read-only zero input");checks++;
                Thread.Sleep(2200);
                var low=events.ToArray();Require(low.Length>=10&&low.All(e=>e.Item1=="EMERGENCY_ACTIVE"),"continuous valid LOW beyond UI TTL, no forced UNKNOWN");checks++;
                DateTime first=ParseSample(low[0].Item2),last=ParseSample(low[low.Length-1].Item2);
                Require(last>first.AddSeconds(1.5)&&last<=DateTime.UtcNow,"timestamps follow actual fresh reads throughout long round");checks++;
                Require(low.All(e=>e.Item2.Contains("ZeroSessionId=")&&e.Item2.Contains("DiTaskGeneration=1")),"zero session identity accompanies every sample");checks++;
                Volatile.Write(ref level,1);Wait(()=>events.Any(e=>e.Item1=="EMERGENCY_RELEASED"));
                Require(status.ReadCurrent()&&reads>20,"LOW to HIGH is observed by the same sole reader");checks++;
            }
            using(var blocked=new ManualResetEvent(false))using(var release=new ManualResetEvent(false))
            {
                int calls=0;var items=new ConcurrentQueue<Tuple<string,string>>();
                var status=new FactoryZeroEmergencyStatus(ms=>{if(Interlocked.Increment(ref calls)>1){blocked.Set();release.WaitOne();}return true;},
                    (key,text)=>items.Enqueue(Tuple.Create(key,text)));
                status.Start();Require(blocked.WaitOne(1000),"fake read in flight");checks++;
                Wait(()=>items.Any(e=>e.Item1=="EMERGENCY_UNKNOWN"));
                DateTime original=ParseSample(items.First(e=>e.Item1=="EMERGENCY_RELEASED").Item2);
                Require(items.Where(e=>e.Item1=="EMERGENCY_RELEASED").All(e=>ParseSample(e.Item2)==original),"cached HIGH never receives a publication timestamp");checks++;
                Thread.Sleep(500);Throws(status.CheckFault);checks++;
                bool stopFailed=false;try{status.Dispose();}catch(InvalidOperationException){stopFailed=true;}
                Require(stopFailed,"blocked native reader prevents successful disposal/task release");checks++;
                int published=items.Count;release.Set();status.Dispose();Thread.Sleep(120);
                Require(items.Count==published,"late read after stop cannot publish into a replacement session");checks++;
            }
            return checks;
        }
        private static DateTime ParseSample(string text)
        {return DateTime.Parse(text.Split('|')[0],CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);}
        private static void Wait(Func<bool> ready)
        {var clock=System.Diagnostics.Stopwatch.StartNew();while(!ready()){if(clock.ElapsedMilliseconds>2500)throw new Exception("fake status wait timeout");Thread.Sleep(5);}}
        private static FactorySettings Settings()
        { var s = new FactorySettings(); s.Input.RequestedPicoRateHz = 1000000; s.Input.CaptureDurationMs = 20; s.Input.RangeIndex = 5; return s; }
        private sealed class Clock : IFactoryClock
        { public long ElapsedMilliseconds { get; private set; } public void Pause(int ms,CancellationToken token) { token.ThrowIfCancellationRequested(); ElapsedMilliseconds += ms; } }
        private sealed class Input : IFactoryZeroInput
        {
            private readonly Clock clock; private SimulationFactoryZeroInput synthetic; private FactorySettings settings;
            internal bool High, Disposed, NeverComplete, FailDispose; internal double Voltage; internal int Initializations, Reads;
            internal Input(Clock c) { clock = c; }
            public void Initialize(FactorySettings s) { Initializations++; settings = s; synthetic = new SimulationFactoryZeroInput(clock); synthetic.Initialize(s); }
            public bool EmergencyHigh { get { return High; } }
            public void Arm() { synthetic.Arm(); }
            public bool ArtComplete { get { return !NeverComplete && synthetic.ArtComplete; } }
            public bool PicoComplete { get { return !NeverComplete && synthetic.PicoComplete; } }
            public BenchCapture ReadCapture() { Reads++; var c = synthetic.ReadCapture(); for (int i = 0; i < c.VoltageRaw.Length; i++) c.VoltageRaw[i] = Voltage; return c; }
            public void Dispose() { Disposed = true; if (synthetic != null) synthetic.Dispose(); if (FailDispose) throw new Exception("fake cleanup failure"); }
        }
        private static void Throws(Action a) { try { a(); } catch { return; } throw new Exception("Expected rejection."); }
        private static void Require(bool ok,string message) { if (!ok) throw new Exception(message); }
    }
}
