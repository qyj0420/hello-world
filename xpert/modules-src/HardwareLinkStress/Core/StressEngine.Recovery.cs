using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HVX.HardwareLinkStress
{
    public sealed partial class StressEngine
    {
        // This is intentionally a different path from ordinary read-only
        // stress. The feature is disabled unless every safety/identity adapter
        // is explicitly supplied. It owns one lease for the whole session.
        private async Task<StressSummary> RunWithRecoveryAsync(TransferPlan plan,
            StressRunOptions options, StressIdentity selected, CancellationToken cancellationToken)
        {
            var summary = new StressSummary { RunId = Guid.NewGuid(), Outcome = StressOutcome.Faulted };
            var runtime = Stopwatch.StartNew();
            var timeline = new SessionEvidenceSink(evidence, clock, summary.RunId, runtime);
            IDisposable sessionLease = null;
            int attemptedCycles = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await idleProbe.IsIdleAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Another measurement owns or uses the selected hardware.");
                sessionLease = await leases.AcquireAsync(selected.LeaseKey, cancellationToken).ConfigureAwait(false);
                if (sessionLease == null) throw new IOException("An exclusive diagnostic hardware lease was not acquired.");
                if (!await idleProbe.IsIdleAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The host became busy after the diagnostic lease was acquired.");

                while (attemptedCycles < options.MaxCycles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ITransferEndpoint artDevice = endpointFactory.CreateArt();
                    ITransferEndpoint picoDevice = null;
                    if (artDevice == null) throw new InvalidOperationException("ART input adapter factory returned null.");
                    try
                    {
                        picoDevice = endpointFactory.CreatePico();
                        if (picoDevice == null) throw new InvalidOperationException("Pico input adapter factory returned null.");
                    }
                    catch { artDevice.Dispose(); throw; }

                    int cycleOffset = attemptedCycles;
                    long artOffset = summary.ArtBytes, picoOffset = summary.PicoBytes;
                    var segmentSink = new SegmentEvidenceSink(timeline, cycleOffset, artOffset, picoOffset,
                        summary.ReconnectAttempts > 0);
                    var segmentOptions = options.Copy();
                    segmentOptions.AutoReconnectAndResume = false;
                    segmentOptions.MaxCycles = options.MaxCycles - attemptedCycles;
                    segmentOptions.RecoveryObservationWindow = TimeSpan.Zero;
                    var segment = new StressEngine(artDevice, picoDevice, hub,
                        new BorrowedLeaseProvider(selected.LeaseKey), segmentSink, clock);
                    StressSummary part = await segment.RunAsync(plan, segmentOptions, cancellationToken).ConfigureAwait(false);
                    summary.ArtBytes = checked(summary.ArtBytes + part.ArtBytes);
                    summary.PicoBytes = checked(summary.PicoBytes + part.PicoBytes);
                    summary.TransferSeconds += part.TransferSeconds;
                    summary.CompletedCycles += part.CompletedCycles;
                    summary.FailedCycles += part.FailedCycles;
                    attemptedCycles = checked(attemptedCycles + part.CompletedCycles + part.FailedCycles);

                    if (part.Outcome == StressOutcome.Cancelled)
                    {
                        summary.Outcome = StressOutcome.Cancelled;
                        summary.Failure = part.Failure;
                        break;
                    }
                    if (part.Outcome == StressOutcome.Completed)
                    {
                        summary.Outcome = summary.FailedCycles == 0 && summary.ReconnectAttempts == 0
                            ? StressOutcome.Completed : StressOutcome.CompletedWithRecoveredFaults;
                        summary.Failure = summary.FailedCycles == 0 && summary.ReconnectAttempts == 0 ? String.Empty :
                            "A connection was restored or a cycle failed; failed cycles were not replayed.";
                        break;
                    }
                    if (part.Outcome == StressOutcome.Unavailable && !segmentSink.SawOffline)
                    {
                        summary.Outcome = StressOutcome.Unavailable;
                        summary.Failure = part.Failure;
                        break;
                    }

                    summary.Failure = part.Failure;
                    // Only an observed offline state may trigger reconnection.
                    // SDK errors with all ports online are not silently retried.
                    if (!segmentSink.SawOffline || attemptedCycles >= options.MaxCycles ||
                        summary.ReconnectAttempts >= options.MaximumReconnectAttempts)
                    {
                        summary.Outcome = StressOutcome.Faulted;
                        break;
                    }

                    bool recovered = await TryVerifiedReconnect(options, selected, summary, timeline,
                        attemptedCycles, cancellationToken).ConfigureAwait(false);
                    if (!recovered)
                    {
                        summary.Outcome = StressOutcome.Faulted;
                        break;
                    }
                    timeline.Record(attemptedCycles, StressEventKind.ResumePlanned,
                        "Fresh endpoint creation is planned; no restored acquisition has succeeded yet. Failed cycle is not replayed.",
                        summary.ArtBytes, summary.PicoBytes);
                }
                if (attemptedCycles >= options.MaxCycles && summary.Outcome == StressOutcome.Faulted && summary.FailedCycles > 0)
                    summary.Failure = "The final diagnostic cycle failed; no implicit retry was made.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                summary.Outcome = StressOutcome.Cancelled;
                summary.Failure = "Stopped by the operator.";
                timeline.Record(attemptedCycles, StressEventKind.StoppedByUser, summary.Failure,
                    summary.ArtBytes, summary.PicoBytes);
            }
            catch (Exception error)
            {
                summary.Outcome = StressOutcome.Faulted;
                summary.Failure = error.GetType().Name + ": " + Compact(error.Message);
                timeline.Record(attemptedCycles, StressEventKind.ReconnectRejected,
                    "Diagnostic session stopped: " + summary.Failure, summary.ArtBytes, summary.PicoBytes);
            }
            finally
            {
                runtime.Stop();
                summary.ElapsedSeconds = runtime.Elapsed.TotalSeconds;
                if (sessionLease != null) sessionLease.Dispose();
            }
            return summary;
        }

        private async Task<bool> TryVerifiedReconnect(StressRunOptions options, StressIdentity selected,
            StressSummary summary, SessionEvidenceSink timeline, int cycle,
            CancellationToken cancellationToken)
        {
            while (summary.ReconnectAttempts < options.MaximumReconnectAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int next = ++summary.ReconnectAttempts;
                TimeSpan backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks,
                    checked(options.ReconnectBackoff.Ticks * next)));
                timeline.Record(cycle, StressEventKind.ReconnectAttempt,
                    "Attempt " + next + "/" + options.MaximumReconnectAttempts +
                    "; waiting " + backoff.TotalMilliseconds + " ms; no transfer during reconnect.",
                    summary.ArtBytes, summary.PicoBytes);
                await clock.Delay(backoff, cancellationToken).ConfigureAwait(false);
                if (!await idleProbe.IsIdleAsync(cancellationToken).ConfigureAwait(false))
                {
                    timeline.Record(cycle, StressEventKind.ReconnectRejected,
                        "Host is not idle; no reconnect command was sent.", summary.ArtBytes, summary.PicoBytes);
                    return false;
                }
                try
                {
                    ReconnectionProof proof = await reconnectAdapter.ReconnectAndVerifyAsync(selected, cancellationToken)
                        .ConfigureAwait(false);
                    if (!IdentityMatches(selected, proof))
                    {
                        timeline.Record(cycle, StressEventKind.ReconnectRejected,
                            "Reconnect did not prove the same hub, ports and ART/Pico serials; no further reconnect command will be sent. " +
                            Compact(proof == null ? "no proof" : proof.Diagnostic), summary.ArtBytes, summary.PicoBytes);
                        return false;
                    }
                    if (!proof.HubReady || !proof.ControllerReady || !proof.AcquisitionReady)
                    {
                        timeline.Record(cycle, StressEventKind.ReconnectRejected,
                            "The exact device was observed but transport or module health is not ready.",
                            summary.ArtBytes, summary.PicoBytes);
                        continue;
                    }
                    HubObservation observation = await Observe(cancellationToken).ConfigureAwait(false);
                    if (!observation.AllOnline || !await idleProbe.IsIdleAsync(cancellationToken).ConfigureAwait(false))
                    {
                        timeline.Record(cycle, StressEventKind.ReconnectRejected,
                            "Post-reconnect transport health or host-idle check failed. " + Describe(observation),
                            summary.ArtBytes, summary.PicoBytes);
                        continue;
                    }
                    timeline.Record(cycle, StressEventKind.Reconnected,
                        "Same selected hub and both serials verified; health online. No output/HV action was requested.",
                        summary.ArtBytes, summary.PicoBytes);
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    timeline.Record(cycle, StressEventKind.ReconnectRejected,
                        "Reconnect attempt failed: " + Compact(error.Message), summary.ArtBytes, summary.PicoBytes);
                }
            }
            return false;
        }

        private static bool IdentityMatches(StressIdentity expected, ReconnectionProof proof)
        {
            if (proof == null) return false;
            return EqualId(expected.HubMac, proof.HubMac) &&
                   EqualId(expected.ControllerLocation, proof.ControllerLocation) &&
                   EqualId(expected.AcquisitionLocation, proof.AcquisitionLocation) &&
                   EqualId(expected.ExpectedArtSerial, proof.ArtSerial) &&
                   EqualId(expected.ExpectedPicoSerial, proof.PicoSerial);
        }

        private static bool EqualId(string expected, string observed)
        {
            return !String.IsNullOrWhiteSpace(expected) && !String.IsNullOrWhiteSpace(observed) &&
                String.Equals(expected.Trim(), observed.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private sealed class BorrowedLeaseProvider : IExclusiveLeaseProvider
        {
            private readonly string expectedKey;
            internal BorrowedLeaseProvider(string key) { expectedKey = key; }
            public Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!String.Equals(expectedKey, key, StringComparison.Ordinal))
                    throw new InvalidOperationException("Segment attempted a different hardware lease.");
                return Task.FromResult<IDisposable>(new NoopLease());
            }
            private sealed class NoopLease : IDisposable { public void Dispose() { } }
        }

        private sealed class SessionEvidenceSink
        {
            private readonly IStressEvidenceSink sink;
            private readonly IStressClock clock;
            private readonly Guid runId;
            private readonly Stopwatch runtime;
            private long sequence;
            internal SessionEvidenceSink(IStressEvidenceSink sink, IStressClock clock, Guid runId, Stopwatch runtime)
            { this.sink = sink; this.clock = clock; this.runId = runId; this.runtime = runtime; }
            internal void Record(int cycle, StressEventKind kind, string detail, long artBytes, long picoBytes)
            {
                sink.Record(new StressEvent
                {
                    RunId = runId, Utc = clock.UtcNow, Sequence = sequence + 1, Cycle = cycle,
                    Kind = kind, Detail = Compact(detail), ArtBytesTotal = artBytes,
                    PicoBytesTotal = picoBytes, ElapsedSeconds = runtime.Elapsed.TotalSeconds
                });
                sequence++;
            }
        }

        private sealed class SegmentEvidenceSink : IStressEvidenceSink
        {
            private readonly SessionEvidenceSink session;
            private readonly int cycleOffset;
            private readonly long artOffset, picoOffset;
            private readonly bool afterRecovery;
            private bool firstResumedCycleRecorded;
            internal bool SawOffline;
            internal SegmentEvidenceSink(SessionEvidenceSink session, int cycleOffset, long artOffset, long picoOffset,
                bool afterRecovery)
            { this.session = session; this.cycleOffset = cycleOffset; this.artOffset = artOffset;
              this.picoOffset = picoOffset; this.afterRecovery = afterRecovery; }
            public void Record(StressEvent evidence)
            {
                if (evidence.Kind == StressEventKind.LinkDropped) SawOffline = true;
                session.Record(checked(cycleOffset + evidence.Cycle), evidence.Kind,
                    evidence.Detail, checked(artOffset + evidence.ArtBytesTotal),
                    checked(picoOffset + evidence.PicoBytesTotal));
                if (afterRecovery && !firstResumedCycleRecorded && evidence.Kind == StressEventKind.CycleCompleted)
                {
                    firstResumedCycleRecorded = true;
                    session.Record(checked(cycleOffset + evidence.Cycle), StressEventKind.ResumedCycleCompleted,
                        "The first new complete dual-card cycle after recovery succeeded; prior failed data was not replayed.",
                        checked(artOffset + evidence.ArtBytesTotal), checked(picoOffset + evidence.PicoBytesTotal));
                }
            }
        }
    }
}
