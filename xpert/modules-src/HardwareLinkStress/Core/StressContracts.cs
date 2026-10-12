using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.HardwareLinkStress
{
    // The planner chooses and freezes the requested application payload before
    // a run. These are not measurements of bytes on the Ethernet/USB wire.
    public sealed class TransferPlan
    {
        public string TierName;
        public int ArtBytesPerCycle;
        public int PicoBytesPerCycle;
        public int ArtSamplesPerCycle;
        public int PicoSamplesPerCycle;
        public int ArtBytesPerSample;
        public int PicoBytesPerSample;
        public double RequestedDurationMilliseconds;
        public bool LimitedByCapacity;
        public int NominalPicoBytesPerCycle;
        // Minimum rest AFTER a completed dual-device read. It is not the
        // acquisition segment length; every tier retains at least 2 seconds.
        public TimeSpan CyclePeriod;

        public TransferPlan Copy()
        {
            return new TransferPlan
            {
                TierName = TierName,
                ArtBytesPerCycle = ArtBytesPerCycle,
                PicoBytesPerCycle = PicoBytesPerCycle,
                ArtSamplesPerCycle = ArtSamplesPerCycle,
                PicoSamplesPerCycle = PicoSamplesPerCycle,
                ArtBytesPerSample = ArtBytesPerSample,
                PicoBytesPerSample = PicoBytesPerSample,
                RequestedDurationMilliseconds = RequestedDurationMilliseconds,
                LimitedByCapacity = LimitedByCapacity,
                NominalPicoBytesPerCycle = NominalPicoBytesPerCycle,
                CyclePeriod = CyclePeriod
            };
        }

        public void Validate()
        {
            if (String.IsNullOrWhiteSpace(TierName) || TierName.Length > 64)
                throw new ArgumentException("A named transfer tier is required.");
            if (ArtBytesPerCycle <= 0 || PicoBytesPerCycle <= 0 ||
                (long)ArtBytesPerCycle + PicoBytesPerCycle > 100L * 1024 * 1024)
                throw new ArgumentOutOfRangeException("Requested application bytes per cycle must be positive and at most 100 MiB combined.");
            if (ArtSamplesPerCycle <= 0 || PicoSamplesPerCycle <= 0 ||
                ArtBytesPerSample <= 0 || PicoBytesPerSample <= 0 ||
                (long)ArtSamplesPerCycle * ArtBytesPerSample != ArtBytesPerCycle ||
                (long)PicoSamplesPerCycle * PicoBytesPerSample != PicoBytesPerCycle)
                throw new ArgumentException("Sample counts, widths and requested application bytes disagree.");
            if (Double.IsNaN(RequestedDurationMilliseconds) || Double.IsInfinity(RequestedDurationMilliseconds) ||
                RequestedDurationMilliseconds <= 0 || RequestedDurationMilliseconds > 60)
                throw new ArgumentOutOfRangeException("RequestedDurationMilliseconds");
            if (NominalPicoBytesPerCycle <= 0 ||
                LimitedByCapacity != (NominalPicoBytesPerCycle > PicoBytesPerCycle))
                throw new ArgumentException("Nominal and capacity-limited Pico byte counts disagree.");
            if (CyclePeriod < TimeSpan.FromMilliseconds(HVX.Acquisition.PicoCaptureCadence.CooldownMilliseconds(RequestedDurationMilliseconds)) || CyclePeriod > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException("CyclePeriod");
        }
    }

    public sealed class StressRunOptions
    {
        public string DeviceKey;
        public string HubMac;
        public string ControllerLocation;
        public string AcquisitionLocation;
        public string ExpectedArtSerial;
        public string ExpectedPicoSerial;
        public int MaxCycles = Int32.MaxValue;
        public TimeSpan PerCycleTimeout = TimeSpan.FromSeconds(30);
        public TimeSpan RecoveryObservationWindow = TimeSpan.FromSeconds(30);
        public TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(1);
        public bool AutoReconnectAndResume;
        public int MaximumReconnectAttempts = 2;
        public TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(2);

        public StressRunOptions Copy()
        {
            return (StressRunOptions)MemberwiseClone();
        }

        public void Validate()
        {
            if (String.IsNullOrWhiteSpace(DeviceKey) || DeviceKey.Length > 128)
                throw new ArgumentException("An exact, nonempty selected-device identity is required.");
            if (String.IsNullOrWhiteSpace(HubMac) || !Regex.IsMatch(HubMac, "^[0-9A-Fa-f]{12}$") ||
                !ValidLocation(ControllerLocation) || !ValidLocation(AcquisitionLocation) ||
                String.Equals(ControllerLocation, AcquisitionLocation, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The selected hub MAC and two distinct port locations are required.");
            if (!String.Equals(DeviceKey, HubMac + "/" + ControllerLocation + "/" + AcquisitionLocation,
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The exclusive-lease key must match the selected hub and both module locations.");
            if (String.IsNullOrWhiteSpace(ExpectedArtSerial) || String.IsNullOrWhiteSpace(ExpectedPicoSerial))
                throw new ArgumentException("Exact ART and Pico serials are required.");
            if (MaxCycles < 1) throw new ArgumentOutOfRangeException("MaxCycles");
            if (PerCycleTimeout < TimeSpan.FromMilliseconds(10) || PerCycleTimeout > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException("PerCycleTimeout");
            if (RecoveryObservationWindow < TimeSpan.Zero || RecoveryObservationWindow > TimeSpan.FromMinutes(5))
                throw new ArgumentOutOfRangeException("RecoveryObservationWindow");
            if (RecoveryPollInterval < TimeSpan.FromMilliseconds(10) || RecoveryPollInterval > TimeSpan.FromSeconds(30))
                throw new ArgumentOutOfRangeException("RecoveryPollInterval");
            if (AutoReconnectAndResume)
            {
                if (MaximumReconnectAttempts < 1 || MaximumReconnectAttempts > 5)
                    throw new ArgumentOutOfRangeException("MaximumReconnectAttempts");
                if (ReconnectBackoff < TimeSpan.FromMilliseconds(100) || ReconnectBackoff > TimeSpan.FromSeconds(30))
                    throw new ArgumentOutOfRangeException("ReconnectBackoff");
            }
        }

        private static bool ValidLocation(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
                Regex.IsMatch(value, "^[A-Za-z0-9_.:-]+$");
        }
    }

    // Immutable snapshot of the one selected hardware set. A reconnect
    // adapter cannot mutate the identity that the lease and proof validate.
    public sealed class StressIdentity
    {
        public string HubMac { get; private set; }
        public string ControllerLocation { get; private set; }
        public string AcquisitionLocation { get; private set; }
        public string ExpectedArtSerial { get; private set; }
        public string ExpectedPicoSerial { get; private set; }
        public string LeaseKey { get; private set; }

        private StressIdentity(StressRunOptions options)
        {
            HubMac = options.HubMac.Trim().ToUpperInvariant();
            ControllerLocation = options.ControllerLocation.Trim().ToUpperInvariant();
            AcquisitionLocation = options.AcquisitionLocation.Trim().ToUpperInvariant();
            ExpectedArtSerial = options.ExpectedArtSerial.Trim();
            ExpectedPicoSerial = options.ExpectedPicoSerial.Trim();
            LeaseKey = HubMac + "/" + ControllerLocation + "/" + AcquisitionLocation;
        }

        public static StressIdentity FromOptions(StressRunOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            options.Validate();
            return new StressIdentity(options);
        }
    }

    public sealed class TransferSample
    {
        // Count the bytes actually returned by the hardware adapter. Do not
        // substitute requested bytes or an assumed DS600 transport payload.
        public int BytesRead;
    }

    public sealed class TransferReadException : IOException
    {
        public int ActualBytes { get; private set; }
        public TransferReadException(string message, int actualBytes)
            : base(message) { ActualBytes = actualBytes; }
        public TransferReadException(string message, int actualBytes, Exception innerException)
            : base(message, innerException) { ActualBytes = actualBytes; }
    }

    public interface ITransferEndpoint : IDisposable
    {
        string ObservedSerial { get; }
        Task InitializeAsync(TransferPlan plan, CancellationToken cancellationToken);
        Task<TransferSample> ReadAsync(int requestedBytes, CancellationToken cancellationToken);
    }

    public interface ITransferEndpointFactory
    {
        // Construction must be inert: no SDK load/open/enumeration until the
        // engine has verified the selected hub baseline and calls InitializeAsync.
        ITransferEndpoint CreateArt();
        ITransferEndpoint CreatePico();
    }

    public enum LinkState { Unknown, Online, Offline }

    public sealed class HubObservation
    {
        public LinkState Hub;
        public LinkState ControllerPort;
        public LinkState AcquisitionPort;
        public string Detail;

        public bool AllOnline
        {
            get { return Hub == LinkState.Online && ControllerPort == LinkState.Online && AcquisitionPort == LinkState.Online; }
        }

        public bool AnyOffline
        {
            get { return Hub == LinkState.Offline || ControllerPort == LinkState.Offline || AcquisitionPort == LinkState.Offline; }
        }
    }

    // Observe only. The monitor must not connect, disconnect, change bindings,
    // restart SX services, or produce synthetic DS600 traffic.
    public interface IHubMonitor
    {
        // A native SX call can also ignore cancellation. Production transport
        // observation must therefore run behind a bounded worker boundary.
        Task<HubObservation> ObserveAsync(CancellationToken cancellationToken);
    }

    // Implementations must coordinate with ALL host acquisition modules across
    // processes and allow Dispose on any continuation thread. Refuse an unknown
    // or busy owner; never steal a device from a measurement session.
    public interface IExclusiveLeaseProvider
    {
        Task<IDisposable> AcquireAsync(string deviceKey, CancellationToken cancellationToken);
    }

    public interface IHostIdleProbe
    {
        // Must consider ALL measurement pages/sessions and forbid active AO/HV
        // output, not just inspect the visibility of the current window. It is
        // not proof of physical electrical safety or permission to energize.
        Task<bool> IsIdleAsync(CancellationToken cancellationToken);
    }

    public sealed class ReconnectionProof
    {
        public string HubMac;
        public string ControllerLocation;
        public string AcquisitionLocation;
        public string ArtSerial;
        public string PicoSerial;
        public bool HubReady;
        public bool ControllerReady;
        public bool AcquisitionReady;
        public string Diagnostic;
    }

    public interface IReconnectAdapter
    {
        // Restricted to the selected SX transport. No driver install, service
        // reset, binding edit, output, high-voltage action or arbitrary device.
        // Must verify the identity before issuing a command and use a bounded
        // worker for native calls that may ignore cancellation.
        Task<ReconnectionProof> ReconnectAndVerifyAsync(StressIdentity identity,
            CancellationToken cancellationToken);
    }

    public interface IStressEvidenceSink
    {
        // Persistence failure must throw: do not continue an unlogged test.
        void Record(StressEvent evidence);
    }

    public interface IStressClock
    {
        DateTimeOffset UtcNow { get; }
        Task Delay(TimeSpan duration, CancellationToken cancellationToken);
    }

    public sealed class SystemStressClock : IStressClock
    {
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            return Task.Delay(duration, cancellationToken);
        }
    }

    public enum StressEventKind
    {
        Started, Baseline, CycleStarted, CycleCompleted, LinkDropped,
        TransferFault, MonitorFault, Reconnected, RecoveryObservationEnded,
        ReconnectAttempt, ReconnectRejected, ResumePlanned, ResumedCycleCompleted,
        StoppedByUser, Completed
    }

    public sealed class StressEvent
    {
        public Guid RunId;
        public DateTimeOffset Utc;
        public long Sequence;
        public int Cycle;
        public StressEventKind Kind;
        public string Detail;
        public long ArtBytesTotal;
        public long PicoBytesTotal;
        public double ElapsedSeconds;
    }

    public enum StressOutcome { Completed, CompletedWithRecoveredFaults, Cancelled, Faulted, Unavailable }

    public sealed class StressSummary
    {
        public Guid RunId;
        public StressOutcome Outcome;
        public int CompletedCycles;
        public int FailedCycles;
        public int ReconnectAttempts;
        public long ArtBytes;
        public long PicoBytes;
        public double ElapsedSeconds;
        public double TransferSeconds;
        public string Failure;

        public double ApplicationBytesPerSecond
        {
            get { return TransferSeconds > 0 ? (ArtBytes + PicoBytes) / TransferSeconds : 0; }
        }

        public double WholeRunBytesPerSecond
        {
            get { return ElapsedSeconds > 0 ? (ArtBytes + PicoBytes) / ElapsedSeconds : 0; }
        }
    }
}
