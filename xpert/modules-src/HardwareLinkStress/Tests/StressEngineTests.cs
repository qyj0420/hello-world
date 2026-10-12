using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HVX.HardwareLinkStress;

internal static class StressEngineTests
{
    private static int checks;

    private static void Main()
    {
        SuccessAndActualBytes();
        DropStopsWithoutOptIn();
        BaselineOfflineNeverOpensSdk();
        RecoveryNeedsAllAdapters();
        VerifiedRecoverySkipsFailedCycle();
        PartialTransferAfterRecoveryIsNotHidden();
        BaselineRecoveryIsNotPureSuccess();
        WrongIdentityCannotResume();
        BusyHostCannotReconnect();
        TransferErrorWithoutOfflineDoesNotReconnect();
        CooperativeTimeoutStops();
        UserCancellationStopsBeforeOpen();
        LeaseFailureNeverReads();
        MismatchedLeaseIdentityRejected();
        SerialMismatchNeverReads();
        PartialTransferStopsAndCountsActualBytes();
        ThrownShortReadCountsKnownBytes();
        LoggingFailureDoesNotDoubleCountCycle();
        ReleaseFailureStillReleasesLease();
        Console.WriteLine("STRESS_ENGINE_OFFLINE=PASS checks=" + checks + "; no native driver/device opened");
    }

    private static void SuccessAndActualBytes()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        var sink = new MemoryEvidence();
        var leases = new FakeLeaseProvider();
        var engine = Direct(art, pico, new FakeHub(), leases, sink);
        StressSummary result = Run(engine, Options(2));
        Need(result.Outcome == StressOutcome.Completed, "success outcome");
        Need(result.CompletedCycles == 2 && result.FailedCycles == 0, "two complete cycles");
        Need(result.ArtBytes == 160 && result.PicoBytes == 400, "actual bytes counted");
        Need(result.ApplicationBytesPerSecond > 0 && result.WholeRunBytesPerSecond > 0, "throughput measured");
        Need(art.InitializeCount == 1 && pico.InitializeCount == 1 && art.DisposeCount == 1 && pico.DisposeCount == 1,
            "single ownership/release");
        Need(leases.AcquireCount == 1 && leases.DisposeCount == 1, "lease released");
        Need(leases.LastKey == "AABBCCDDEEFF/H11/S51", "canonical exact lease key");
        Need(sink.Events.Count(e => e.Kind == StressEventKind.CycleCompleted) == 2, "timeline complete");
        Need(StrictSequence(sink.Events), "monotonic event sequence");
    }

    private static void DropStopsWithoutOptIn()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        var sink = new MemoryEvidence();
        var hub = new FakeHub(On(), On(), Off(), On());
        var engine = Direct(art, pico, hub, new FakeLeaseProvider(), sink);
        StressRunOptions options = Options(3);
        options.RecoveryObservationWindow = TimeSpan.FromMilliseconds(20);
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.Faulted && result.FailedCycles == 1, "drop faults first cycle");
        Need(result.CompletedCycles == 0 && art.ReadCount == 1 && pico.ReadCount == 1, "no auto acquisition retry");
        Need(sink.Events.Any(e => e.Kind == StressEventKind.LinkDropped), "drop logged");
        Need(sink.Events.Any(e => e.Kind == StressEventKind.Reconnected), "passive reconnect observation logged");
        Need(!sink.Events.Any(e => e.Kind == StressEventKind.ResumePlanned), "passive observation never resumes");
    }

    private static void RecoveryNeedsAllAdapters()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        var engine = Direct(art, pico, new FakeHub(), new FakeLeaseProvider(), new MemoryEvidence());
        StressRunOptions options = Options(2);
        options.AutoReconnectAndResume = true;
        bool rejected = false;
        try { Run(engine, options); } catch (InvalidOperationException) { rejected = true; }
        Need(rejected && art.InitializeCount == 0 && pico.InitializeCount == 0,
            "checkbox without verified adapters refused before opening hardware");
    }

    private static void BaselineOfflineNeverOpensSdk()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        StressSummary result = Run(Direct(art, pico, new FakeHub(Off()),
            new FakeLeaseProvider(), new MemoryEvidence()), Options(1));
        Need(result.Outcome == StressOutcome.Unavailable, "offline baseline unavailable");
        Need(art.InitializeCount == 0 && pico.InitializeCount == 0 && art.ReadCount == 0,
            "offline baseline never opens native SDK");
    }

    private static void VerifiedRecoverySkipsFailedCycle()
    {
        var factory = new FakeFactory();
        var sink = new MemoryEvidence();
        var leases = new FakeLeaseProvider();
        var reconnect = new FakeReconnect(ExactProof());
        var engine = Recovering(factory, new FakeHub(On(), On(), Off(), On(), On(), On(), On()),
            leases, sink, new FakeIdle(), reconnect);
        StressRunOptions options = Options(2);
        options.AutoReconnectAndResume = true;
        options.MaximumReconnectAttempts = 1;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.CompletedWithRecoveredFaults, "explicit recovered-fault outcome");
        Need(result.CompletedCycles == 1 && result.FailedCycles == 1, "failed cycle not counted complete");
        Need(result.ReconnectAttempts == 1 && reconnect.Calls == 1, "one bounded reconnect");
        Need(factory.ArtCreated == 2 && factory.PicoCreated == 2 && factory.AllDisposed, "fresh endpoints after fault");
        Need(factory.TotalReads == 4, "two device reads per attempted cycle, no replay");
        Need(leases.AcquireCount == 1 && leases.DisposeCount == 1, "one lease covers recovery");
        Need(sink.Events.Any(e => e.Kind == StressEventKind.Reconnected) &&
             sink.Events.Any(e => e.Kind == StressEventKind.ResumePlanned) &&
             sink.Events.Any(e => e.Kind == StressEventKind.ResumedCycleCompleted),
             "reconnect plan and actual resumed cycle logged separately");
        Need(StrictSequence(sink.Events), "one session event sequence across segments");
    }

    private static void WrongIdentityCannotResume()
    {
        var factory = new FakeFactory();
        var sink = new MemoryEvidence();
        ReconnectionProof wrong = ExactProof(); wrong.PicoSerial = "OTHER-PICO";
        var reconnect = new FakeReconnect(wrong);
        var engine = Recovering(factory, new FakeHub(On(), On(), Off()),
            new FakeLeaseProvider(), sink, new FakeIdle(), reconnect);
        StressRunOptions options = Options(2);
        options.AutoReconnectAndResume = true; options.MaximumReconnectAttempts = 3;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.Faulted && result.ReconnectAttempts == 1, "wrong serial stops");
        Need(reconnect.Calls == 1, "identity mismatch never sends a second reconnect");
        Need(factory.ArtCreated == 1 && factory.PicoCreated == 1, "no new adapters on wrong identity");
        Need(sink.Events.Any(e => e.Kind == StressEventKind.ReconnectRejected), "rejection logged");
    }

    private static void PartialTransferAfterRecoveryIsNotHidden()
    {
        var factory = new FakeFactory { ShortReadSecondArt = true };
        var reconnect = new FakeReconnect(ExactProof());
        var sink = new MemoryEvidence();
        var engine = Recovering(factory, new FakeHub(On(), On(), Off(), On(), On(), On(), On()),
            new FakeLeaseProvider(), sink, new FakeIdle(), reconnect);
        StressRunOptions options = Options(2);
        options.AutoReconnectAndResume = true;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.Faulted && result.CompletedCycles == 0 && result.FailedCycles == 2,
            "partial transfer after recovery remains a fault, not a pass");
        Need(result.ArtBytes == 159 && result.PicoBytes == 400,
            "both failed attempts retain only actual application bytes");
        Need(result.ReconnectAttempts == 1 && reconnect.Calls == 1,
            "second transfer fault while link online does not reconnect again");
        Need(!sink.Events.Any(e => e.Kind == StressEventKind.ResumedCycleCompleted),
            "reconnect alone never claims successful resumed capture");
    }

    private static void BaselineRecoveryIsNotPureSuccess()
    {
        var factory = new FakeFactory();
        var engine = Recovering(factory, new FakeHub(Off(), On(), On(), On(), On()),
            new FakeLeaseProvider(), new MemoryEvidence(), new FakeIdle(), new FakeReconnect(ExactProof()));
        StressRunOptions options = Options(1); options.AutoReconnectAndResume = true;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.CompletedWithRecoveredFaults && result.FailedCycles == 0 &&
             result.ReconnectAttempts == 1, "offline baseline recovery not reported as pure success");
        Need(factory.ArtCreated == 2 && factory.AllDisposed, "offline adapters disposed; fresh pair used");
    }

    private static void BusyHostCannotReconnect()
    {
        var factory = new FakeFactory();
        var reconnect = new FakeReconnect(ExactProof());
        var idle = new FakeIdle(true, true, false);
        var engine = Recovering(factory, new FakeHub(On(), On(), Off()),
            new FakeLeaseProvider(), new MemoryEvidence(), idle, reconnect);
        StressRunOptions options = Options(2); options.AutoReconnectAndResume = true;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.Faulted && reconnect.Calls == 0,
            "host activity prevents reconnect command");
    }

    private static void TransferErrorWithoutOfflineDoesNotReconnect()
    {
        var factory = new FakeFactory { FailFirstArtRead = true };
        var reconnect = new FakeReconnect(ExactProof());
        var engine = Recovering(factory, new FakeHub(), new FakeLeaseProvider(),
            new MemoryEvidence(), new FakeIdle(), reconnect);
        StressRunOptions options = Options(2); options.AutoReconnectAndResume = true;
        StressSummary result = Run(engine, options);
        Need(result.Outcome == StressOutcome.Faulted, "transfer fault stops");
        Need(result.ReconnectAttempts == 0 && reconnect.Calls == 0, "no reconnect without offline evidence");
        Need(factory.ArtCreated == 1 && factory.PicoCreated == 1, "no second segment");
    }

    private static void CooperativeTimeoutStops()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A") { WaitForCancellation = true };
        var sink = new MemoryEvidence();
        StressRunOptions options = Options(2);
        options.PerCycleTimeout = TimeSpan.FromMilliseconds(20);
        StressSummary result = Run(Direct(art, pico, new FakeHub(), new FakeLeaseProvider(), sink), options);
        Need(result.Outcome == StressOutcome.Faulted && result.FailedCycles == 1, "cooperative timeout faults");
        Need(result.ArtBytes == 80 && result.PicoBytes == 0, "successful side's actual bytes retained");
        Need(art.ReadCount == 1 && pico.ReadCount == 1, "no retry on timeout");
        Need(sink.Events.Any(e => e.Kind == StressEventKind.TransferFault && e.Detail.Contains("deadline")),
            "timeout evidence logged");
    }

    private static void UserCancellationStopsBeforeOpen()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            var sink = new MemoryEvidence();
            StressSummary result = Direct(art, pico, new FakeHub(), new FakeLeaseProvider(), sink)
                .RunAsync(Plan(), Options(2), stop.Token).GetAwaiter().GetResult();
            Need(result.Outcome == StressOutcome.Cancelled, "user cancellation outcome");
            Need(art.ReadCount == 0 && pico.ReadCount == 0, "no transfer after cancellation");
            Need(sink.Events.Any(e => e.Kind == StressEventKind.StoppedByUser), "user stop logged");
        }
    }

    private static void LeaseFailureNeverReads()
    {
        var art = new FakeEndpoint("ART-A");
        var pico = new FakeEndpoint("PICO-A");
        var lease = new FakeLeaseProvider { Refuse = true };
        StressSummary result = Run(Direct(art, pico, new FakeHub(), lease, new MemoryEvidence()), Options(1));
        Need(result.Outcome == StressOutcome.Faulted && art.InitializeCount == 0 && pico.InitializeCount == 0,
            "lease refusal prevents opening either device");
    }

    private static void SerialMismatchNeverReads()
    {
        var art = new FakeEndpoint("WRONG-ART");
        var pico = new FakeEndpoint("PICO-A");
        StressSummary result = Run(Direct(art, pico, new FakeHub(), new FakeLeaseProvider(), new MemoryEvidence()), Options(1));
        Need(result.Outcome == StressOutcome.Faulted && art.ReadCount == 0 && pico.ReadCount == 0,
            "serial mismatch prevents transfer");
    }

    private static void MismatchedLeaseIdentityRejected()
    {
        var lease = new FakeLeaseProvider();
        var options = Options(1); options.DeviceKey = "OTHER-HUB/H11/S51";
        bool rejected = false;
        try
        {
            Run(Direct(new FakeEndpoint("ART-A"), new FakeEndpoint("PICO-A"),
                new FakeHub(), lease, new MemoryEvidence()), options);
        }
        catch (ArgumentException) { rejected = true; }
        Need(rejected && lease.AcquireCount == 0, "mismatched lease key rejected before acquisition");
    }

    private static void PartialTransferStopsAndCountsActualBytes()
    {
        var art = new FakeEndpoint("ART-A") { ReturnOneByteShort = true };
        var pico = new FakeEndpoint("PICO-A");
        var sink = new MemoryEvidence();
        StressSummary result = Run(Direct(art, pico, new FakeHub(), new FakeLeaseProvider(), sink), Options(2));
        Need(result.Outcome == StressOutcome.Faulted && result.CompletedCycles == 0, "partial cycle not accepted");
        Need(result.ArtBytes == 79 && result.PicoBytes == 200, "partial actual bytes not requested bytes logged");
        Need(art.ReadCount == 1 && pico.ReadCount == 1, "partial not retried");
    }

    private static void ReleaseFailureStillReleasesLease()
    {
        var art = new FakeEndpoint("ART-A") { ThrowOnDispose = true };
        var pico = new FakeEndpoint("PICO-A");
        var lease = new FakeLeaseProvider();
        StressSummary result = Run(Direct(art, pico, new FakeHub(), lease, new MemoryEvidence()), Options(1));
        Need(result.Outcome == StressOutcome.Faulted && result.Failure.Contains("Resource release"),
            "cleanup failure changes outcome");
        Need(pico.DisposeCount == 1 && lease.DisposeCount == 1,
            "all resources released despite first Dispose exception");
    }

    private static void ThrownShortReadCountsKnownBytes()
    {
        var art = new FakeEndpoint("ART-A") { ShortReadThrows = true };
        var pico = new FakeEndpoint("PICO-A");
        StressSummary result = Run(Direct(art, pico, new FakeHub(),
            new FakeLeaseProvider(), new MemoryEvidence()), Options(2));
        Need(result.Outcome == StressOutcome.Faulted && result.CompletedCycles == 0,
            "thrown short read remains a failed cycle");
        Need(result.ArtBytes == 79 && result.PicoBytes == 200,
            "trusted actual bytes from exception and successful side retained");
        Need(art.ReadCount == 1, "thrown short read not retried");
    }

    private static void LoggingFailureDoesNotDoubleCountCycle()
    {
        var sink = new MemoryEvidence { FailOnceOn = StressEventKind.CycleCompleted };
        StressSummary result = Run(Direct(new FakeEndpoint("ART-A"), new FakeEndpoint("PICO-A"),
            new FakeHub(), new FakeLeaseProvider(), sink), Options(2));
        Need(result.Outcome == StressOutcome.Faulted && result.CompletedCycles == 1 && result.FailedCycles == 0,
            "evidence failure after complete cycle does not invent another failed cycle");
        Need(StrictSequence(sink.Events), "persisted event sequence has no artificial gap");
    }

    private static StressEngine Direct(FakeEndpoint art, FakeEndpoint pico, FakeHub hub,
        FakeLeaseProvider lease, MemoryEvidence sink)
    { return new StressEngine(art, pico, hub, lease, sink, new FakeClock()); }

    private static StressEngine Recovering(FakeFactory factory, FakeHub hub,
        FakeLeaseProvider lease, MemoryEvidence sink, FakeIdle idle, FakeReconnect reconnect)
    { return new StressEngine(factory, hub, lease, sink, new FakeClock(), idle, reconnect); }

    private static StressSummary Run(StressEngine engine, StressRunOptions options)
    { return engine.RunAsync(Plan(), options, CancellationToken.None).GetAwaiter().GetResult(); }

    private static TransferPlan Plan()
    {
        return new TransferPlan
        {
            TierName = "test", ArtBytesPerCycle = 80, PicoBytesPerCycle = 200,
            ArtSamplesPerCycle = 10, PicoSamplesPerCycle = 100,
            ArtBytesPerSample = 8, PicoBytesPerSample = 2,
            RequestedDurationMilliseconds = 1, NominalPicoBytesPerCycle = 200,
            CyclePeriod = TimeSpan.FromMilliseconds(2000)
        };
    }

    private static StressRunOptions Options(int cycles)
    {
        return new StressRunOptions
        {
            DeviceKey = "AABBCCDDEEFF/H11/S51", HubMac = "AABBCCDDEEFF",
            ControllerLocation = "H11", AcquisitionLocation = "S51",
            ExpectedArtSerial = "ART-A", ExpectedPicoSerial = "PICO-A",
            MaxCycles = cycles, RecoveryObservationWindow = TimeSpan.Zero
        };
    }

    private static HubObservation On()
    { return new HubObservation { Hub = LinkState.Online, ControllerPort = LinkState.Online, AcquisitionPort = LinkState.Online }; }
    private static HubObservation Off()
    { return new HubObservation { Hub = LinkState.Offline, ControllerPort = LinkState.Offline, AcquisitionPort = LinkState.Offline }; }
    private static ReconnectionProof ExactProof()
    {
        return new ReconnectionProof
        {
            HubMac = "AABBCCDDEEFF", ControllerLocation = "H11", AcquisitionLocation = "S51",
            ArtSerial = "ART-A", PicoSerial = "PICO-A",
            HubReady = true, ControllerReady = true, AcquisitionReady = true
        };
    }

    private static bool StrictSequence(IList<StressEvent> events)
    {
        for (int i = 1; i < events.Count; i++)
            if (events[i].RunId != events[0].RunId || events[i].Sequence != events[i - 1].Sequence + 1) return false;
        return events.Count > 0 && events[0].Sequence == 1;
    }
    private static void Need(bool condition, string message)
    { checks++; if (!condition) throw new Exception("FAIL " + message); }

    private sealed class FakeEndpoint : ITransferEndpoint
    {
        private readonly string serial;
        internal int InitializeCount, ReadCount, DisposeCount;
        internal bool FailRead, ReturnOneByteShort, ShortReadThrows, WaitForCancellation, ThrowOnDispose;
        internal FakeEndpoint(string serial) { this.serial = serial; }
        public string ObservedSerial { get { return serial; } }
        public Task InitializeAsync(TransferPlan plan, CancellationToken token)
        { token.ThrowIfCancellationRequested(); InitializeCount++; return Task.CompletedTask; }
        public async Task<TransferSample> ReadAsync(int requestedBytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ReadCount++;
            if (FailRead) throw new IOException("injected native transfer failure");
            if (ShortReadThrows) throw new TransferReadException("injected short read", requestedBytes - 1);
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new TransferSample { BytesRead = requestedBytes - (ReturnOneByteShort ? 1 : 0) };
        }
        public void Dispose() { DisposeCount++; if (ThrowOnDispose) throw new IOException("injected release failure"); }
    }

    private sealed class FakeFactory : ITransferEndpointFactory
    {
        internal int ArtCreated, PicoCreated;
        internal bool FailFirstArtRead;
        internal bool ShortReadSecondArt;
        internal readonly List<FakeEndpoint> All = new List<FakeEndpoint>();
        internal bool AllDisposed { get { return All.All(x => x.DisposeCount == 1); } }
        internal int TotalReads { get { return All.Sum(x => x.ReadCount); } }
        public ITransferEndpoint CreateArt()
        {
            var endpoint = new FakeEndpoint("ART-A")
            {
                FailRead = FailFirstArtRead && ArtCreated == 0,
                ShortReadThrows = ShortReadSecondArt && ArtCreated == 1
            };
            ArtCreated++; All.Add(endpoint); return endpoint;
        }
        public ITransferEndpoint CreatePico()
        {
            var endpoint = new FakeEndpoint("PICO-A");
            PicoCreated++; All.Add(endpoint); return endpoint;
        }
    }

    private sealed class FakeHub : IHubMonitor
    {
        private readonly Queue<HubObservation> states;
        private HubObservation last = On();
        internal FakeHub(params HubObservation[] observations)
        { states = new Queue<HubObservation>(observations ?? new HubObservation[0]); }
        public Task<HubObservation> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (states.Count > 0) last = states.Dequeue();
            return Task.FromResult(last);
        }
    }

    private sealed class FakeLeaseProvider : IExclusiveLeaseProvider
    {
        internal int AcquireCount, DisposeCount;
        internal bool Refuse;
        internal string LastKey;
        public Task<IDisposable> AcquireAsync(string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); AcquireCount++; LastKey = key;
            if (Refuse) throw new IOException("injected lease refusal");
            return Task.FromResult<IDisposable>(new Lease(this));
        }
        private sealed class Lease : IDisposable
        {
            private readonly FakeLeaseProvider owner;
            internal Lease(FakeLeaseProvider owner) { this.owner = owner; }
            public void Dispose() { owner.DisposeCount++; }
        }
    }

    private sealed class FakeIdle : IHostIdleProbe
    {
        private readonly Queue<bool> states;
        internal FakeIdle(params bool[] values) { states = new Queue<bool>(values ?? new bool[0]); }
        public Task<bool> IsIdleAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(states.Count == 0 || states.Dequeue()); }
    }

    private sealed class FakeReconnect : IReconnectAdapter
    {
        private readonly ReconnectionProof proof;
        internal int Calls;
        internal FakeReconnect(ReconnectionProof proof) { this.proof = proof; }
        public Task<ReconnectionProof> ReconnectAndVerifyAsync(StressIdentity identity, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(proof); }
    }

    private sealed class MemoryEvidence : IStressEvidenceSink
    {
        internal readonly List<StressEvent> Events = new List<StressEvent>();
        internal StressEventKind? FailOnceOn;
        public void Record(StressEvent evidence)
        {
            if (FailOnceOn == evidence.Kind)
            { FailOnceOn = null; throw new IOException("injected evidence persistence failure"); }
            Events.Add(evidence);
        }
    }

    private sealed class FakeClock : IStressClock
    {
        private DateTimeOffset now = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow { get { return now; } }
        public Task Delay(TimeSpan duration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); now = now.Add(duration); return Task.CompletedTask; }
    }
}
