using System;
using System.Threading;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // Four phases x sixteen levels; published objects are never modified.
        // Polling does not enter the lock held by waveform filtering/analysis.
        private sealed class PublishedLevel
        {
            internal static readonly PublishedLevel Empty = new PublishedLevel(null, null, null);
            internal readonly TdrAutoAnalysisResult Analysis;
            internal readonly WaveformData Filtered;
            internal readonly MarkerData Marker;
            internal readonly string Status;

            internal PublishedLevel(TdrAutoAnalysisResult analysis, WaveformData filtered, MarkerData marker)
            {
                Analysis = analysis; Filtered = filtered; Marker = marker;
                Status = analysis == null ? "Unavailable" : analysis.SearchStatus.ToString();
            }
        }

        private static PublishedLevel ReadLevel(PhaseState phase, string canonicalCode)
        {
            int index = Array.IndexOf(LevelCodes, canonicalCode);
            return Volatile.Read(ref phase.PublishedLevels[index]) ?? PublishedLevel.Empty;
        }

        // Caller owns stateLock; publication is one release-write after construction.
        private static void PublishLevelNoLock(PhaseState phase, string canonicalCode)
        {
            TdrAutoAnalysisResult analysis;
            WaveformData filtered;
            MarkerData marker;
            phase.Analyses.TryGetValue(canonicalCode, out analysis);
            phase.FilteredWaveforms.TryGetValue(canonicalCode, out filtered);
            phase.Markers.TryGetValue(canonicalCode, out marker);
            Volatile.Write(ref phase.PublishedLevels[Array.IndexOf(LevelCodes, canonicalCode)],
                analysis == null && filtered == null && marker == null ? PublishedLevel.Empty :
                new PublishedLevel(analysis, filtered, marker));
        }

        private void ReleaseRuntimeState()
        {
            lock (stateLock)
            {
                ReleasePhaseNoLock(allPhase);
                foreach (PhaseState phase in phases) ReleasePhaseNoLock(phase);
                viewWaveform = null;
                PhaseChanged = null; LevelChanged = null; CalibrationScopeChanged = null;
                AdvancedModeChanged = null; ParameterChanged = null;
                StartRequested = null; DeleteRequested = null; SaveRequested = null;
                JointAdded = null; JointRemoved = null; ExitRequested = null;
                BandwidthChanged = null; AnalysisChanged = null; TdrCursorChanged = null;
                CenterSplitterMoved = null;
            }
        }

        private static void ReleasePhaseNoLock(PhaseState phase)
        {
            phase.Waveforms.Clear(); phase.FilteredWaveforms.Clear(); phase.FilterSources.Clear(); phase.FilterCutoffs.Clear();
            phase.Analyses.Clear(); phase.Markers.Clear(); phase.Joints.Clear(); phase.Noise = null;
            for (int i = 0; i < phase.PublishedLevels.Length; i++)
                Volatile.Write(ref phase.PublishedLevels[i], PublishedLevel.Empty);
        }
    }
}
