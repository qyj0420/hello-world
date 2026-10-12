using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.HardwareLinkStress
{
    /// <summary>
    /// Input-only dual-card transfer coordinator. It never calls an output,
    /// high-voltage, connect, disconnect or SX binding API. After any fault it
    /// stops transfer immediately; optional recovery observation is passive.
    /// Native adapters must be run in an owned, killable worker process because
    /// a driver that ignores cancellation cannot be safely timed out in-process.
    /// </summary>
    public sealed partial class StressEngine
    {
        private readonly ITransferEndpoint art;
        private readonly ITransferEndpoint pico;
        private readonly ITransferEndpointFactory endpointFactory;
        private readonly IHostIdleProbe idleProbe;
        private readonly IReconnectAdapter reconnectAdapter;
        private readonly IHubMonitor hub;
        private readonly IExclusiveLeaseProvider leases;
        private readonly IStressEvidenceSink evidence;
        private readonly IStressClock clock;

        public StressEngine(ITransferEndpoint art, ITransferEndpoint pico, IHubMonitor hub,
            IExclusiveLeaseProvider leases, IStressEvidenceSink evidence, IStressClock clock)
        {
            this.art = art ?? throw new ArgumentNullException("art");
            this.pico = pico ?? throw new ArgumentNullException("pico");
            this.hub = hub ?? throw new ArgumentNullException("hub");
            this.leases = leases ?? throw new ArgumentNullException("leases");
            this.evidence = evidence ?? throw new ArgumentNullException("evidence");
            this.clock = clock ?? throw new ArgumentNullException("clock");
        }

        // Only this constructor supports opt-in automatic diagnostic recovery:
        // every resumed segment receives fresh native adapter instances.
        public StressEngine(ITransferEndpointFactory endpoints, IHubMonitor hub,
            IExclusiveLeaseProvider leases, IStressEvidenceSink evidence, IStressClock clock,
            IHostIdleProbe idleProbe, IReconnectAdapter reconnectAdapter)
        {
            endpointFactory = endpoints ?? throw new ArgumentNullException("endpoints");
            this.hub = hub ?? throw new ArgumentNullException("hub");
            this.leases = leases ?? throw new ArgumentNullException("leases");
            this.evidence = evidence ?? throw new ArgumentNullException("evidence");
            this.clock = clock ?? throw new ArgumentNullException("clock");
            this.idleProbe = idleProbe;
            this.reconnectAdapter = reconnectAdapter;
        }

        public async Task<StressSummary> RunAsync(TransferPlan suppliedPlan,
            StressRunOptions options, CancellationToken cancellationToken)
        {
            if (suppliedPlan == null) throw new ArgumentNullException("suppliedPlan");
            if (options == null) throw new ArgumentNullException("options");
            TransferPlan plan = suppliedPlan.Copy();
            options = options.Copy();
            plan.Validate();
            options.Validate();
            StressIdentity selected = StressIdentity.FromOptions(options);
            if (options.AutoReconnectAndResume)
            {
                if (endpointFactory == null || idleProbe == null || reconnectAdapter == null)
                    throw new InvalidOperationException("Automatic diagnostic recovery is unavailable without a fresh-endpoint factory, host-idle probe and verified reconnect adapter.");
                return await RunWithRecoveryAsync(plan, options, selected, cancellationToken).ConfigureAwait(false);
            }
            if (art == null || pico == null)
                throw new InvalidOperationException("Direct input endpoints are required when automatic recovery is disabled.");

            var summary = new StressSummary { RunId = Guid.NewGuid(), Outcome = StressOutcome.Faulted };
            var elapsed = Stopwatch.StartNew();
            long eventSequence = 0;
            int currentCycle = 0;
            string stage = "exclusive lease";
            IDisposable lease = null;
            bool sawOffline = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lease = await leases.AcquireAsync(selected.LeaseKey, cancellationToken).ConfigureAwait(false);
                if (lease == null) throw new IOException("The selected device's exclusive lease was not acquired.");
                Emit(summary, elapsed, ref eventSequence, 0, StressEventKind.Started,
                    "Tier=" + plan.TierName + "; RequestedArtBytes=" + plan.ArtBytesPerCycle +
                    "; RequestedPicoBytes=" + plan.PicoBytesPerCycle +
                    "; NominalPicoBytes=" + plan.NominalPicoBytesPerCycle +
                    "; CapacityLimited=" + plan.LimitedByCapacity +
                    "; DurationMs=" + plan.RequestedDurationMilliseconds.ToString("R", CultureInfo.InvariantCulture) +
                    "; RestMs=" + plan.CyclePeriod.TotalMilliseconds.ToString("R", CultureInfo.InvariantCulture));

                stage = "baseline monitor";
                HubObservation observation = await Observe(cancellationToken).ConfigureAwait(false);
                Emit(summary, elapsed, ref eventSequence, 0, StressEventKind.Baseline, Describe(observation));
                if (!observation.AllOnline)
                {
                    summary.Outcome = StressOutcome.Unavailable;
                    summary.Failure = observation.AnyOffline ? "Selected hub or module port is offline." :
                        "Selected hub or module port state is unknown.";
                    if (observation.AnyOffline)
                    {
                        sawOffline = true;
                        Emit(summary, elapsed, ref eventSequence, 0, StressEventKind.LinkDropped,
                            "Unavailable at baseline; no transfer started. " + Describe(observation));
                    }
                    return summary;
                }

                // Do not open either native SDK while the selected DS600 path
                // is already known offline or uncertain.
                stage = "endpoint initialization";
                await art.InitializeAsync(plan, cancellationToken).ConfigureAwait(false);
                await pico.InitializeAsync(plan, cancellationToken).ConfigureAwait(false);
                if (!String.Equals(art.ObservedSerial, selected.ExpectedArtSerial, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(pico.ObservedSerial, selected.ExpectedPicoSerial, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ART/Pico actual serials do not match the selected hardware identity.");

                for (currentCycle = 1; currentCycle <= options.MaxCycles; currentCycle++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stage = "pre-cycle monitor";
                    observation = await Observe(cancellationToken).ConfigureAwait(false);
                    if (!observation.AllOnline) throw new LinkNotReadyException(observation);

                    Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.CycleStarted,
                        "ART=" + plan.ArtBytesPerCycle + " B; Pico=" + plan.PicoBytesPerCycle + " B requested.");
                    stage = "dual-card transfer";
                    var transferTime = Stopwatch.StartNew();
                    int cycleArtBytes = 0, cyclePicoBytes = 0;
                    try
                    {
                        using (var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            cycleCancellation.CancelAfter(options.PerCycleTimeout);
                            Task<TransferSample> artTask = Read(art, plan.ArtBytesPerCycle, cycleCancellation.Token);
                            Task<TransferSample> picoTask = Read(pico, plan.PicoBytesPerCycle, cycleCancellation.Token);
                            // A native driver that ignores cancellation can
                            // leave WhenAll pending indefinitely. Production
                            // adapters MUST run in an owned killable worker;
                            // this in-process core does not claim hard timeout.
                            try { await Task.WhenAll(artTask, picoTask).ConfigureAwait(false); }
                            catch
                            {
                                // WhenAll has completed both tasks. Retain real bytes from
                                // either successful side even when the other side failed.
                                CountKnownBytes(summary, artTask, plan.ArtBytesPerCycle, true);
                                CountKnownBytes(summary, picoTask, plan.PicoBytesPerCycle, false);
                                throw;
                            }
                            CountKnownBytes(summary, artTask, plan.ArtBytesPerCycle, true);
                            CountKnownBytes(summary, picoTask, plan.PicoBytesPerCycle, false);
                            ValidateRead(artTask.Result, plan.ArtBytesPerCycle, "Controller");
                            ValidateRead(picoTask.Result, plan.PicoBytesPerCycle, "Acquisition");
                            cycleArtBytes = artTask.Result.BytesRead;
                            cyclePicoBytes = picoTask.Result.BytesRead;
                        }
                    }
                    finally
                    {
                        transferTime.Stop();
                        summary.TransferSeconds += transferTime.Elapsed.TotalSeconds;
                    }

                    stage = "post-cycle monitor";
                    observation = await Observe(cancellationToken).ConfigureAwait(false);
                    if (!observation.AllOnline) throw new LinkNotReadyException(observation);
                    summary.CompletedCycles++;
                    Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.CycleCompleted,
                        "Both complete; this-cycle application bytes ART=" + cycleArtBytes +
                        ", Pico=" + cyclePicoBytes + "; read seconds=" +
                        transferTime.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) +
                        "; cumulative ART=" + summary.ArtBytes + ", Pico=" + summary.PicoBytes +
                        "; " + Describe(observation));

                    // CyclePeriod is an inter-cycle rest, not segment duration.
                    // A fault/cancel never silently selects a smaller tier.
                    if (currentCycle < options.MaxCycles)
                    {
                        stage = "inter-cycle rest";
                        await clock.Delay(plan.CyclePeriod, cancellationToken).ConfigureAwait(false);
                    }
                }

                summary.Outcome = StressOutcome.Completed;
                Emit(summary, elapsed, ref eventSequence, summary.CompletedCycles, StressEventKind.Completed,
                    "All requested cycles completed without an observed link loss.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                summary.Outcome = StressOutcome.Cancelled;
                summary.Failure = "Stopped by the operator; no transfer was retried.";
                Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.StoppedByUser, summary.Failure);
            }
            catch (Exception error)
            {
                summary.Outcome = StressOutcome.Faulted;
                if (currentCycle > summary.CompletedCycles && currentCycle <= options.MaxCycles)
                    summary.FailedCycles++;
                bool timeout = error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
                summary.Failure = (timeout ? "Cycle deadline expired" : error.GetType().Name) +
                    " at " + stage + ": " + Compact(error.Message);
                var linkError = error as LinkNotReadyException;
                if (linkError != null && linkError.Observation.AnyOffline)
                {
                    sawOffline = true;
                    Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.LinkDropped,
                        summary.Failure + "; " + Describe(linkError.Observation));
                }
                else
                {
                    Emit(summary, elapsed, ref eventSequence, currentCycle,
                        stage.IndexOf("monitor", StringComparison.Ordinal) >= 0 ? StressEventKind.MonitorFault : StressEventKind.TransferFault,
                        summary.Failure);
                }

                // A failed transfer is not proof that the hub went offline.
                // Obtain one passive observation to distinguish the two cases.
                if (!cancellationToken.IsCancellationRequested && lease != null && !sawOffline)
                {
                    try
                    {
                        HubObservation after = await Observe(cancellationToken).ConfigureAwait(false);
                        if (after.AnyOffline)
                        {
                            sawOffline = true;
                            Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.LinkDropped,
                                "Offline after failure; temporal correlation, not proven cause. " + Describe(after));
                        }
                    }
                    catch (Exception monitorError)
                    {
                        Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.MonitorFault,
                            "Post-failure observation failed: " + Compact(monitorError.Message));
                    }
                }
                if (sawOffline && lease != null && options.RecoveryObservationWindow > TimeSpan.Zero)
                    eventSequence = await WatchRecovery(summary, options, elapsed, currentCycle,
                        eventSequence, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                elapsed.Stop();
                summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                Exception cleanup = null;
                try { art.Dispose(); } catch (Exception error) { cleanup = error; }
                try { pico.Dispose(); } catch (Exception error) { if (cleanup == null) cleanup = error; }
                try { if (lease != null) lease.Dispose(); }
                catch (Exception error) { if (cleanup == null) cleanup = error; }
                if (cleanup != null)
                {
                    summary.Outcome = StressOutcome.Faulted;
                    summary.Failure = (summary.Failure == null ? String.Empty : summary.Failure + " | ") +
                        "Resource release failed: " + Compact(cleanup.Message);
                    Emit(summary, elapsed, ref eventSequence, currentCycle, StressEventKind.TransferFault,
                        summary.Failure);
                }
            }
            return summary;
        }

        private async Task<long> WatchRecovery(StressSummary summary, StressRunOptions options,
            Stopwatch runTime, int cycle, long sequence, CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = clock.UtcNow.Add(options.RecoveryObservationWindow);
            while (!cancellationToken.IsCancellationRequested && clock.UtcNow < deadline)
            {
                TimeSpan rest = deadline - clock.UtcNow;
                if (rest > options.RecoveryPollInterval) rest = options.RecoveryPollInterval;
                try { await clock.Delay(rest, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                try
                {
                    HubObservation observation = await Observe(cancellationToken).ConfigureAwait(false);
                    if (observation.AllOnline)
                    {
                        Emit(summary, runTime, ref sequence, cycle, StressEventKind.Reconnected,
                            "Passive observation only; acquisition was not restarted. " + Describe(observation));
                        return sequence;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Emit(summary, runTime, ref sequence, cycle, StressEventKind.MonitorFault,
                        "Recovery observation failed: " + Compact(error.Message));
                }
            }
            Emit(summary, runTime, ref sequence, cycle, StressEventKind.RecoveryObservationEnded,
                "Observation window ended or was cancelled; no acquisition retry was attempted.");
            return sequence;
        }

        private async Task<HubObservation> Observe(CancellationToken cancellationToken)
        {
            HubObservation result = await hub.ObserveAsync(cancellationToken).ConfigureAwait(false);
            if (result == null) throw new InvalidDataException("Hub monitor returned no observation.");
            return result;
        }

        private static async Task<TransferSample> Read(ITransferEndpoint device, int requested, CancellationToken cancellationToken)
        {
            return await device.ReadAsync(requested, cancellationToken).ConfigureAwait(false);
        }

        private static void ValidateRead(TransferSample sample, int requested, string label)
        {
            if (sample == null || sample.BytesRead < 0 || sample.BytesRead > requested)
                throw new InvalidDataException(label + " returned an invalid actual byte count.");
            if (sample.BytesRead != requested)
                throw new InvalidDataException(label + " returned only " + sample.BytesRead + "/" + requested + " requested bytes.");
        }

        private static void CountKnownBytes(StressSummary summary, Task<TransferSample> task, int requested, bool isArt)
        {
            int bytes;
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                bytes = task.Result.BytesRead;
            else if (task.Status == TaskStatus.Faulted && task.Exception != null)
            {
                TransferReadException partial = null;
                foreach (Exception error in task.Exception.Flatten().InnerExceptions)
                    if (error is TransferReadException) { partial = (TransferReadException)error; break; }
                if (partial == null) return;
                bytes = partial.ActualBytes;
            }
            else return;
            if (bytes < 0 || bytes > requested) return;
            if (isArt) summary.ArtBytes = checked(summary.ArtBytes + bytes);
            else summary.PicoBytes = checked(summary.PicoBytes + bytes);
        }

        private void Emit(StressSummary summary, Stopwatch elapsed, ref long sequence,
            int cycle, StressEventKind kind, string detail)
        {
            evidence.Record(new StressEvent
            {
                RunId = summary.RunId, Utc = clock.UtcNow, Sequence = sequence + 1,
                Cycle = cycle, Kind = kind, Detail = Compact(detail),
                ArtBytesTotal = summary.ArtBytes, PicoBytesTotal = summary.PicoBytes,
                ElapsedSeconds = elapsed.Elapsed.TotalSeconds
            });
            sequence++;
        }

        private static string Describe(HubObservation state)
        {
            return "Hub=" + state.Hub + "; ControllerPort=" + state.ControllerPort +
                "; AcquisitionPort=" + state.AcquisitionPort + "; Detail=" + Compact(state.Detail);
        }

        private static string Compact(string text)
        {
            if (String.IsNullOrEmpty(text)) return String.Empty;
            string singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return singleLine.Length <= 1000 ? singleLine : singleLine.Substring(0, 1000);
        }

        private sealed class LinkNotReadyException : IOException
        {
            internal readonly HubObservation Observation;
            internal LinkNotReadyException(HubObservation state)
                : base(state.AnyOffline ? "Selected hub or module port went offline." :
                    "Selected hub or module port state became unknown.")
            {
                Observation = state;
            }
        }
    }
}
