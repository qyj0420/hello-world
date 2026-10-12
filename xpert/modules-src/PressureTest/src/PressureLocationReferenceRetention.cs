using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Xiongxin.OWTS.Data;

namespace HVX.Controls
{
    // Separate ownership/provenance record, never a LOC, save ACK or phase source.
    // It references the already-created raw round rather than duplicating buffers.
    internal sealed class PressureLocationReferenceContext
    {
        internal PressureZeroVoltageResult Source;
        internal OscillationCycleWindow Window;
        internal string Project, Directory, AcquisitionId, Phase, RawSha256, Reason;
        internal int RequestedCycles, CompleteCycles, PdCount, ArtCount, TailSamples;
        internal double PdRate, ArtRate, CommonEnd, LastCompleteCrossing;
        internal bool Retain;
    }

    internal static class PressureLocationReferenceRetention
    {
        internal const string FileName = "location-reference-only.json";
        internal static PressureLocationReferenceContext Create(PressureZeroVoltageResult source,
            string project, string id, string phase, int requested, OscillationCycleWindow window)
        {
            Guid parsed;
            if (source == null || source.Capture == null || source.ChargePc == null || source.VoltageKv == null ||
                window == null || requested < 1 || requested > 5 || !Guid.TryParseExact(id, "N", out parsed) ||
                String.IsNullOrWhiteSpace(project) || Array.IndexOf(new[] { "A", "B", "C", "ALL" }, phase) < 0)
                throw new InvalidDataException("Missing owned round context for raw location reference.");
            int pd = source.ChargePc.Length, art = source.VoltageKv.Length;
            double pr = source.Capture.PicoRateHz, ar = source.Capture.ArtRateHz;
            if (pd < 16 || pd > Xiongxin.ZeroBench.BenchSettings.MaximumPicoSamples || art < 2 || art > 80000 ||
                source.Capture.PdAdc == null || source.Capture.PdAdc.Length != pd ||
                source.Capture.VoltageRaw == null || source.Capture.VoltageRaw.Length != art ||
                !Finite(pr) || pr <= 0 || !Finite(ar) || ar <= 0 || pd / pr > .060000000001 || art / ar > .060000000001 ||
                String.IsNullOrEmpty(source.RawSha256) || source.RawSha256.Length != 64 ||
                !System.Text.RegularExpressions.Regex.IsMatch(source.RawSha256, "^[0-9A-Fa-f]{64}$"))
                throw new InvalidDataException("Raw reference time base, extent or source hash is invalid.");
            double end = Math.Min((pd - 1) / pr, (art - 1) / ar);
            int cycles = window.Success ? window.CycleCount : 0;
            double last = cycles > 0 ? window.CrossingSeconds[window.CrossingSeconds.Length - 1] : Double.NaN;
            if (!Finite(end) || end < 0 || cycles > requested ||
                cycles > 0 && (!Finite(last) || last < 0 || last > end + 1e-12))
                throw new InvalidDataException("Raw reference does not match the validated complete-cycle window.");
            int commonSamples = Math.Min(pd, (int)Math.Floor(end * pr + 1e-9) + 1);
            int tail = cycles > 0 ? Math.Max(0, commonSamples - 1 - (int)Math.Floor(last * pr + 1e-9)) : commonSamples;
            return new PressureLocationReferenceContext {
                Source = source, Window = window, Project = Path.GetFullPath(project), Directory = source.Directory,
                AcquisitionId = id, Phase = phase, RequestedCycles = requested, CompleteCycles = cycles,
                PdCount = pd, ArtCount = art, PdRate = pr, ArtRate = ar, CommonEnd = end,
                LastCompleteCrossing = last, TailSamples = tail, RawSha256 = source.RawSha256,
                Retain = cycles == 0 || cycles < requested && tail > 0,
                Reason = cycles == 0 ? "NO_COMPLETE_LOCATION_CYCLE:" + window.FailureCode : "ACTUAL_CYCLES_BELOW_REQUEST_WITH_REAL_TAIL"
            };
        }

        // No additional whole-raw hash: the capture reader has already bound it.
        // Validation is repeated around the small atomic metadata publication.
        internal static void Publish(PressureLocationReferenceContext context, Func<bool> isCurrent, Action<Action> commit = null)
        {
            if (context == null || !context.Retain) return;
            if (isCurrent == null || !isCurrent()) throw new InvalidOperationException("Raw reference owner is no longer current.");
            string directory = ValidateDirectory(context);
            string final = Path.Combine(directory, FileName);
            if (File.Exists(final)) {
                if ((File.GetAttributes(final) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked raw reference metadata is forbidden.");
                var existing = new FileInfo(final);
                if (existing.Length < 2 || existing.Length > 65536) throw new InvalidDataException("Raw reference metadata is invalid.");
                var prior = new JavaScriptSerializer { MaxJsonLength = 65536, RecursionLimit = 8 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(final));
                object id, hash, referenceOnly;
                if (!prior.TryGetValue("acquisitionId", out id) || !Object.Equals(id, context.AcquisitionId) ||
                    !prior.TryGetValue("rawSha256", out hash) || !String.Equals(hash as string, context.RawSha256, StringComparison.OrdinalIgnoreCase) ||
                    !prior.TryGetValue("referenceOnly", out referenceOnly) || !Object.Equals(referenceOnly, true))
                    throw new InvalidDataException("Existing raw reference belongs to a different round.");
                return; // Same immutable round is not duplicated or rewritten.
            }
            string raw = Path.Combine(directory, "capture.bin");
            var data = new Dictionary<string, object> {
                {"schema", "HVX.Location.RawReference/1"}, {"referenceOnly", true}, {"validLocationRecord", false},
                {"grantsSaveAck", false}, {"includedInLocationCounts", false}, {"provesHighVoltageSafe", false},
                {"acquisitionId", context.AcquisitionId}, {"phase", context.Phase}, {"rawFile", "capture.bin"},
                {"settingsFile", "settings.xml"}, {"rawSha256", context.RawSha256}, {"rawBytes", new FileInfo(raw).Length},
                {"requestedMaximumCycles", context.RequestedCycles}, {"actualCompleteCycles", context.CompleteCycles},
                {"artSamples", context.ArtCount}, {"picoSamples", context.PdCount},
                {"artSampleRateHz", context.ArtRate}, {"picoSampleRateHz", context.PdRate},
                {"captureStartSeconds", 0d}, {"commonLastSampleSeconds", context.CommonEnd},
                {"lastCompleteCrossingSeconds", Finite(context.LastCompleteCrossing) ? (object)context.LastCompleteCrossing : null},
                {"tailSamplesWithinCommonExtent", context.TailSamples}, {"tailPhaseKnown", false}, {"reason", context.Reason},
                {"rawUnits", "Pico ADC counts; ART input volts; original independent sample rates"},
                {"derivedChargeFilterId", context.Source.HighPassFilterId ?? ""}, {"rawHasDerivedChargeFilter", false},
                {"cleanup", "Retained in place for operator review; no automatic historical deletion. Size grows with retained rounds."}
            };
            string temporary = Path.Combine(directory, ".location-reference-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                    writer.Write(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(data));
                if (!isCurrent()) throw new InvalidOperationException("Raw reference owner changed before publication.");
                ValidateDirectory(context);
                if (commit == null) File.Move(temporary, final);
                else commit(() => File.Move(temporary, final));
            } finally {
                if (File.Exists(temporary)) {
                    ValidateDirectory(context);
                    File.Delete(temporary); // Only this new, validated round temporary, never raw/history.
                }
            }
        }
        static string ValidateDirectory(PressureLocationReferenceContext context)
        {
            string boundary = Path.GetFullPath(Path.Combine(context.Project, "Acquisition", "Pressure")) + Path.DirectorySeparatorChar;
            string directory = Path.GetFullPath(context.Directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!directory.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Raw reference must remain in its owned pressure acquisition directory.");
            PressureZeroVoltageCapture.CaptureDirectory(context.Project, directory);
            long bytes = new FileInfo(Path.Combine(directory, "capture.bin")).Length;
            // Existing capture count limits plus bounded header overhead; no new longer capture permission.
            long maximum = 2L * Xiongxin.ZeroBench.BenchSettings.MaximumPicoSamples + 8L * 80000 + 65536;
            if (bytes < 1 || bytes > maximum) throw new InvalidDataException("Raw reference exceeds the existing bounded capture size.");
            return directory;
        }
        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
