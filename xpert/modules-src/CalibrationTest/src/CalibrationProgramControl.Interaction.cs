using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private const float PlotLeft = 324F;
        private const float PlotTop = 160F;
        private const float PlotWidth = 1021F;
        private const float PlotHeight = 436F;
        private const float BandwidthSliderLeft = 40F;
        private const float BandwidthSliderRight = 205F;
        private const float BandwidthSliderTop = 477F;
        private const float BandwidthSliderBottom = 520F;

        private double reflectionSearchToleranceFraction = 0.15;
        // Presentation only: collapsing this panel never disables the persisted filter.
        [Browsable(false)]
        public bool ManualBandwidthPanelVisible
        {
            get { return GetPhaseStateNoLock(SelectedPhaseIndex).ManualBandwidthPanelVisible; }
            set { RunOnUiThread(delegate { GetPhaseStateNoLock(SelectedPhaseIndex).ManualBandwidthPanelVisible = value; Invalidate(); }); }
        }
        private long bandwidthChangedSequence;
        private long analysisChangedSequence;
        private long cursorChangedSequence;
        private bool bandwidthSliderDragging;
        private System.Windows.Forms.Timer bandwidthDragTimer;
        private void StartBandwidthLiveUpdates()
        {
            if (bandwidthDragTimer == null)
            {
                bandwidthDragTimer = new System.Windows.Forms.Timer { Interval = 40 };
                bandwidthDragTimer.Tick += delegate {
                    if (!bandwidthSliderDragging || IsDisposed || IsBusy) { bandwidthDragTimer.Stop(); return; }
                    SetPhaseBandwidth(SelectedPhaseIndex, true, bandwidthSliderPreviewMHz);
                };
                Disposed += delegate { bandwidthDragTimer.Stop(); bandwidthDragTimer.Dispose(); };
            }
            bandwidthDragTimer.Start();
        }
        private double bandwidthSliderPreviewMHz = 60.0;
        private int selectedCursor;
        private int draggingCursor;
        private bool boxZoomActive;
        private double dragOriginalIncidentTime = Double.NaN;
        private double dragOriginalReflectionTime = Double.NaN;
        private float cursorGrabOffsetX;
        private PointF boxZoomStart;
        private PointF boxZoomEnd;
        private WaveformData viewWaveform;
        private double viewX0;
        private double viewX1 = 1.0;
        private double viewY0 = -1.0;
        private double viewY1 = 1.0;
        private bool viewReady;
        private bool hoverMeasurementAvailable;
        private PointF hoverReferencePoint;
        private double hoverTimeSeconds;
        private double hoverDeltaSeconds;
        private double hoverReflectionDeltaSeconds;
        private double hoverDistanceMeters;

        public event EventHandler BandwidthChanged;
        public event EventHandler AnalysisChanged;
        public event EventHandler TdrCursorChanged;

        [Browsable(false)] public long BandwidthChangedSequence { get { return Volatile.Read(ref bandwidthChangedSequence); } }
        [Browsable(false)] public long AnalysisChangedSequence { get { return Volatile.Read(ref analysisChangedSequence); } }
        [Browsable(false)] public long CursorChangedSequence { get { return Volatile.Read(ref cursorChangedSequence); } }
        [Browsable(false)] public int SelectedCursor { get { return Volatile.Read(ref selectedCursor); } }
        [Browsable(false)] public bool HoverMeasurementAvailable { get { return Volatile.Read(ref hoverMeasurementAvailable); } }
        [Browsable(false)] public double HoverTimeSeconds { get { return Volatile.Read(ref hoverTimeSeconds); } }
        [Browsable(false)] public double HoverDeltaSeconds { get { return Volatile.Read(ref hoverDeltaSeconds); } }
        [Browsable(false)] public double HoverReflectionDeltaSeconds { get { return Volatile.Read(ref hoverReflectionDeltaSeconds); } }
        [Browsable(false)] public double HoverDistanceMeters { get { return Volatile.Read(ref hoverDistanceMeters); } }

        public double GetPhaseBandwidthCutoffMHz(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            return Volatile.Read(ref GetPhaseStateNoLock(phaseIndex).BandwidthCutoffMHz);
        }

        public void SetPhaseBandwidth(int phaseIndex, bool enabled, double cutoffMHz)
        {
            ValidatePhaseIndex(phaseIndex);
            if (!IsFinite(cutoffMHz) || cutoffMHz < 1.0 || cutoffMHz > 60.0)
                throw new ArgumentOutOfRangeException("cutoffMHz", "Expected a bandwidth from 1 MHz through 60 MHz.");
            RunOnUiThread(delegate
            {
                bool changed;
                lock (stateLock)
                {
                    PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                    changed = phase.WideBand != enabled || !NearlyEqual(phase.BandwidthCutoffMHz, cutoffMHz);
                    phase.WideBand = enabled;
                    phase.BandwidthCutoffMHz = cutoffMHz;
                    if (changed)
                    {
                        RebuildCurrentLiveLevelNoLock(phaseIndex);
                        System.Threading.Interlocked.Increment(ref bandwidthChangedSequence);
                        System.Threading.Interlocked.Increment(ref parameterChangedSequence);
                    }
                }
                if (!changed) return;
                if (!bandwidthSliderDragging) ResetViewInternal();
                Invalidate();
                EventHandler handler = BandwidthChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                handler = ParameterChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                handler = AnalysisChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        public void SetReflectionSearchTolerancePercent(double percent)
        {
            if (!IsFinite(percent) || percent <= 0.0 || percent > 100.0)
                throw new ArgumentOutOfRangeException("percent", "Expected a percentage greater than 0 and no more than 100.");
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    reflectionSearchToleranceFraction = percent / 100.0;
                    RebuildAllDerivedNoLock(allPhase);
                    for (int i = 0; i < phases.Length; i++) RebuildAllDerivedNoLock(phases[i]);
                    System.Threading.Interlocked.Increment(ref analysisChangedSequence);
                }
                ResetViewInternal();
                Invalidate();
                EventHandler handler = AnalysisChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        public bool AutoAnalyzePhaseWaveform(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            bool success = false;
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                    success = RebuildLevelDerivedNoLock(phase, normalized);
                    System.Threading.Interlocked.Increment(ref analysisChangedSequence);
                }
                ResetViewInternal();
                Invalidate();
                EventHandler handler = AnalysisChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
            return success;
        }

        public string GetAnalysisStatusCode(int phaseIndex, string levelCode)
        {
            string normalized = NormalizeLevelCode(levelCode);
            ValidatePhaseIndex(phaseIndex);
            return ReadLevel(GetPhaseStateNoLock(phaseIndex), normalized).Status;
        }

        public int GetIncidentPeakIndex(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? -1 : result.IncidentIndex;
        }

        public int GetReflectionPeakIndex(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? -1 : result.ReflectionIndex;
        }

        public double GetIncidentPeakTimeSeconds(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? Double.NaN : result.IncidentTimeSeconds;
        }

        public double GetReflectionPeakTimeSeconds(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? Double.NaN : result.ReflectionTimeSeconds;
        }

        public double GetDampingConstantPerSecond(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? Double.NaN : result.DecayConstantPerSecond;
        }

        public double GetAnalysisConfidence(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null ? 0.0 : result.Confidence;
        }

        public double[] GetFilteredWaveform(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            WaveformData data = ReadLevel(GetPhaseStateNoLock(phaseIndex), normalized).Filtered;
            return data == null ? new double[0] : (double[])data.Samples.Clone();
        }

        public double[] GetFittedDecayCurve(int phaseIndex, string levelCode)
        {
            TdrAutoAnalysisResult result = GetAnalysisNoLockSafe(phaseIndex, NormalizeLevelCode(levelCode));
            return result == null || result.DecayCurveMagnitude == null
                ? new double[0] : (double[])result.DecayCurveMagnitude.Clone();
        }

        public void SetPhaseIncidentHintSeconds(int phaseIndex, string levelCode, double incidentTimeSeconds)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            if (!IsFinite(incidentTimeSeconds)) throw new ArgumentOutOfRangeException("incidentTimeSeconds");
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                    WaveformData raw;
                    if (!phase.Waveforms.TryGetValue(normalized, out raw)) return;
                    WaveformData active = ActiveWaveformNoLock(phase, normalized, raw);
                    TdrAutoAnalysisOptions options = CreateAnalysisOptions();
                    options.IncidentHintSeconds = incidentTimeSeconds;
                    options.IncidentHintHalfWindowSeconds = active.DxSeconds * 3.0;
                    StoreAnalysisNoLock(phase, normalized, active,
                        TdrReflectionAnalyzer.Analyze(active.Samples, active.X0Seconds, active.DxSeconds,
                            phase.CableLengthMeters, phase.VelocityMPerUs, options));
                    System.Threading.Interlocked.Increment(ref analysisChangedSequence);
                    System.Threading.Interlocked.Increment(ref cursorChangedSequence);
                }
                Invalidate();
                RaiseAnalysisAndCursorEvents();
            });
        }

        public void ResetView()
        {
            RunOnUiThread(delegate { ResetViewInternal(); Invalidate(); });
        }

        public void ResetIncidentToInitial()
        {
            ClearManualReflectionChoice();
            AutoAnalyzePhaseWaveform(SelectedPhaseIndex, SelectedLevelCode);
        }

        public void ResetReflectionToInitial()
        {
            ClearManualReflectionChoice();
            AutoAnalyzePhaseWaveform(SelectedPhaseIndex, SelectedLevelCode);
        }

        private void ClearManualReflectionChoice()
        {
            lock(stateLock) {
                var selection=ReflectionSelectionNoLock(GetPhaseStateNoLock(SelectedPhaseIndex),SelectedLevelCode,false);
                if(selection!=null)selection.Confirmed=false;
            }
        }

        public void ClearCursorSelection()
        {
            RunOnUiThread(delegate
            {
                if (draggingCursor != 0) RestoreDraggedMarker(SelectedPhaseIndex, SelectedLevelCode);
                selectedCursor = 0;
                draggingCursor = 0;
                packetDragIncident=packetDragReflection=-1;
                boxZoomActive = false;
                if (Capture) Capture = false;
                Invalidate();
            });
        }

        public void ExportWaveformCsv(string filePath)
        {
            ExportPhaseWaveformCsv(SelectedPhaseIndex, SelectedLevelCode, filePath);
        }

        public void ExportPhaseWaveformCsv(int phaseIndex, string levelCode, string filePath)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A file path is required.", "filePath");
            WaveformData raw;
            WaveformData filtered = null;
            TdrAutoAnalysisResult analysis = null;
            lock (stateLock)
            {
                PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                if (!phase.Waveforms.TryGetValue(normalized, out raw)) throw new InvalidOperationException("The requested waveform is unavailable.");
                phase.FilteredWaveforms.TryGetValue(normalized, out filtered);
                phase.Analyses.TryGetValue(normalized, out analysis);
            }
            string path = Path.GetFullPath(filePath);
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("Index,Time_s,Raw,Filtered,DecayMagnitude,Incident,Reflection");
                for (int i = 0; i < raw.Samples.Length; i++)
                {
                    double time = raw.X0Seconds + i * raw.DxSeconds;
                    double decay = DecayValueAt(analysis, time);
                    writer.WriteLine(String.Join(",", i.ToString(CultureInfo.InvariantCulture),
                        time.ToString("R", CultureInfo.InvariantCulture),
                        raw.Samples[i].ToString("R", CultureInfo.InvariantCulture),
                        (filtered == null ? raw.Samples[i] : filtered.Samples[i]).ToString("R", CultureInfo.InvariantCulture),
                        IsFinite(decay) ? decay.ToString("R", CultureInfo.InvariantCulture) : String.Empty,
                        analysis != null && i == analysis.IncidentIndex ? "1" : "0",
                        analysis != null && i == analysis.ReflectionIndex ? "1" : "0"));
                }
            }
        }

        private PhaseState GetPhaseStateNoLock(int phaseIndex)
        {
            return phaseIndex < 0 ? allPhase : phases[phaseIndex];
        }

        private static string PhaseCodeFromIndex(int phaseIndex)
        {
            return phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C";
        }

        private TdrAutoAnalysisResult GetAnalysisNoLockSafe(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            return ReadLevel(GetPhaseStateNoLock(phaseIndex), levelCode).Analysis;
        }

        private TdrAutoAnalysisOptions CreateAnalysisOptions()
        {
            return new TdrAutoAnalysisOptions { ReflectionToleranceFraction = reflectionSearchToleranceFraction };
        }

        private WaveformData ActiveWaveformNoLock(PhaseState phase, string levelCode, WaveformData raw)
        {
            WaveformData filtered;
            return phase.WideBand && phase.FilteredWaveforms.TryGetValue(levelCode, out filtered) ? filtered : raw;
        }

        private bool RebuildLevelDerivedNoLock(PhaseState phase, string levelCode)
        {
            WaveformData raw;
            if (!phase.Waveforms.TryGetValue(levelCode, out raw)) return false;
            WaveformData active = raw;
            if (phase.WideBand)
            {
                WaveformData source, filteredData;
                double cutoff;
                if (!phase.FilterSources.TryGetValue(levelCode, out source) || !Object.ReferenceEquals(source, raw)
                    || !phase.FilterCutoffs.TryGetValue(levelCode, out cutoff) || cutoff != phase.BandwidthCutoffMHz
                    || !phase.FilteredWaveforms.TryGetValue(levelCode, out filteredData))
                {
                    double[] filtered = raw.Samples.Length < 3 ? (double[])raw.Samples.Clone()
                        : HVX.SignalProcessing.PdLinearLowPass.Apply(raw.Samples, raw.DxSeconds, phase.BandwidthCutoffMHz);
                    filteredData = new WaveformData(filtered, raw.X0Seconds, raw.DxSeconds);
                    phase.FilteredWaveforms[levelCode] = filteredData;
                    phase.FilterSources[levelCode] = raw;
                    phase.FilterCutoffs[levelCode] = phase.BandwidthCutoffMHz;
                }
                active = filteredData;
            }
            else phase.FilteredWaveforms.Remove(levelCode);
            WaveformData detection = UseFirstArrivalTiming ? raw : active;
            TdrAutoAnalysisResult result = TdrReflectionAnalyzer.Analyze(detection.Samples,
                detection.X0Seconds, detection.DxSeconds, phase.CableLengthMeters, phase.VelocityMPerUs,
                CreateAnalysisOptions());
            if(IsCapacitiveLoad) {
                // A capacitor must never inherit a cable's synthetic reflected pair or geometry.
                int index=result.IncidentIndex;
                var incident=new TdrAutoAnalysisResult {SearchStatus=TdrSearchStatus.ReflectionNotFound,
                    IncidentIndex=index,IncidentTimeSeconds=result.IncidentTimeSeconds,IncidentMagnitude=result.IncidentMagnitude,
                    IncidentValue=result.IncidentValue,Baseline=result.Baseline,NoiseSigma=result.NoiseSigma,
                    Message="电容校准仅使用入射信号。"};
                if(index>=0&&index<active.Samples.Length) {
                    double baseActive,sigmaActive;TdrReflectionAnalyzer.RawTimingStatistics(active.Samples,out baseActive,out sigmaActive);
                    incident.Baseline=baseActive;incident.NoiseSigma=sigmaActive;incident.IncidentValue=active.Samples[index];
                    incident.IncidentMagnitude=Math.Abs(active.Samples[index]-baseActive);
                }
                if(UseFirstArrivalTiming&&index>=0) {
                    double baseline,sigma;TdrReflectionAnalyzer.RawTimingStatistics(raw.Samples,out baseline,out sigma);
                    incident.IncidentFirstArrivalIndex=CalibrationArrivalMath.Find(raw.Samples,index,0,Math.Min(raw.Samples.Length-1,index+128),256,baseline,sigma);
                    incident.IncidentTimeSeconds=incident.IncidentFirstArrivalIndex<0?Double.NaN:raw.X0Seconds+incident.IncidentFirstArrivalIndex*raw.DxSeconds;
                }
                StoreAnalysisNoLock(phase,levelCode,active,incident);return false;
            }
            if (UseFirstArrivalTiming && !Object.ReferenceEquals(active, raw) && result.SearchStatus == TdrSearchStatus.Ok)
            {
                // Display filters can create leading-edge ringing. They must not choose a
                // different physical packet or silently move first-arrival timing.
                double rawSigma = result.NoiseSigma;
                result = BuildManualResult(active, phase, result.IncidentIndex, result.ReflectionIndex, result);
                result.NoiseSigma = rawSigma; result.Message = "Raw-anchored timing";
            }
            // Reprocessing a live record must retain the acquisition chain's
            // correlation requirement. A simple amplitude peak is not an upgrade
            // of an explicitly rejected live reflection after a slider move.
            Xiongxin.Calibration.CalibrationCaptureResult capture;
            WaveformData capturedSource;
            int phaseIndex = Object.ReferenceEquals(phase, allPhase) ? -1 : Array.IndexOf(phases, phase);
            string liveKey = LiveKey(phaseIndex, levelCode);
            if (!SimulationMode && liveCaptures.TryGetValue(liveKey, out capture) &&
                liveWaveSources.TryGetValue(liveKey, out capturedSource) && Object.ReferenceEquals(raw, capturedSource) &&
                capture.Assessment != null)
            {
                // First-arrival timing belongs to the accepted acquisition packet.
                // A display low-pass slider must not rebind it to a later, larger
                // negative lobe of the same bipolar pulse.
                var match = UseFirstArrivalTiming ? capture.Reflection : levelCode == "TDR" ? Xiongxin.Calibration.TdrCorrelation.Match(active.Samples, capture.Assessment.IncidentPeakIndex,
                    1 / active.DxSeconds, phase.CableLengthMeters, phase.VelocityMPerUs,
                    Math.Min(.5, reflectionSearchToleranceFraction)) :
                    Xiongxin.Calibration.TdrCorrelation.MatchTerminal(active.Samples, capture.Assessment.IncidentPeakIndex,
                    1 / active.DxSeconds, phase.CableLengthMeters, phase.VelocityMPerUs,
                    Math.Min(.5, reflectionSearchToleranceFraction));
                if (match == null || !match.Valid)
                {
                    result = IncidentOnlyAnalysis(active, capture);
                    result.Message = match == null ? "缺少原始反射评估。" : match.Message;
                }
                else result = BuildManualResult(active, phase, match.IncidentIndex, match.ReflectionIndex, result);
            }
            result=RestoreManualReflectionNoLock(phase,levelCode,active,result);
            StoreAnalysisNoLock(phase, levelCode, active, result);
            return result.SearchStatus == TdrSearchStatus.Ok;
        }

        private void RebuildAllDerivedNoLock(PhaseState phase)
        {
            string[] levels = new List<string>(phase.Waveforms.Keys).ToArray();
            int phaseIndex=Object.ReferenceEquals(phase,allPhase)?-1:Array.IndexOf(phases,phase);
            if(!SimulationMode)RememberLiveLevelParameters(phaseIndex,selectedLevelCode);
            for (int i = 0; i < levels.Length; i++) {
                if(!SimulationMode)RestoreLiveLevelParameters(phaseIndex,levels[i]);
                RebuildLevelDerivedNoLock(phase, levels[i]);
            }
            if(!SimulationMode)RestoreLiveLevelParameters(phaseIndex,selectedLevelCode);
            if (levels.Length > 0) System.Threading.Interlocked.Increment(ref analysisChangedSequence);
        }

        private void StoreAnalysisNoLock(PhaseState phase, string levelCode,
            WaveformData waveform, TdrAutoAnalysisResult result)
        {
            phase.Analyses[levelCode] = result;
            phase.VelocityAdoptions.Remove(levelCode);
            ApplyTimingNoLock(phase, levelCode, waveform, result);
            RestoreManualReflectionTimingNoLock(phase,levelCode,result);
            result.DisplayReflectionScore = IsCapacitiveLoad ? Double.NaN : ReflectionEngineeringScore(phase, waveform, result);
            UpdateReflectionCandidateNoLock(phase, levelCode, waveform, result);
            PublishLevelNoLock(phase, levelCode);
        }

        private void RaiseAnalysisAndCursorEvents()
        {
            EventHandler handler = AnalysisChanged;
            if (handler != null) handler(this, EventArgs.Empty);
            handler = TdrCursorChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void DrawBandwidthSlider(Graphics g, int phaseIndex, double cutoffMHz)
        {
            double value = bandwidthSliderDragging && phaseIndex == SelectedPhaseIndex
                ? bandwidthSliderPreviewMHz : cutoffMHz;
            using (var labelFont = CreateFont(8.5F, FontStyle.Regular))
            using (var ink = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
                g.DrawString(BuiltIn(ActualBandwidthText(GetPhaseStateNoLock(phaseIndex))),
                    labelFont, ink, new PointF(39, 461));
            using (var track = new Pen(Color.FromArgb(188, 203, 217), 4F))
            {
                track.StartCap = LineCap.Round; track.EndCap = LineCap.Round;
                g.DrawLine(track, BandwidthSliderLeft, 497F, BandwidthSliderRight, 497F);
            }
            float x = BandwidthSliderLeft + (float)((value - 1.0) / 59.0) * (BandwidthSliderRight - BandwidthSliderLeft);
            using (var fill = new SolidBrush(ColorTranslator.FromHtml("#00A3C7"))) g.FillEllipse(fill, x - 6F, 491F, 12F, 12F);
            using (var font = CreateFont(7.5F, FontStyle.Regular))
            using (var brush = new SolidBrush(Color.FromArgb(91, 112, 132)))
            {
                g.DrawString("1", font, brush, new PointF(BandwidthSliderLeft - 2F, 503F));
                g.DrawString("60 MHz", font, brush, new PointF(BandwidthSliderRight - g.MeasureString("60 MHz", font).Width, 503F));
            }
        }

        private void EnsureWaveformView(WaveformData waveform)
        {
            if (!RefreshDisplayWindowContext(waveform)) return;
            viewWaveform = waveform;
            double minimum = Double.PositiveInfinity, maximum = Double.NegativeInfinity;
            int first=Math.Max(0,(int)Math.Floor((viewX0-waveform.X0Seconds)/waveform.DxSeconds));
            int last=Math.Min(waveform.Samples.Length-1,(int)Math.Ceiling((viewX1-waveform.X0Seconds)/waveform.DxSeconds));
            for (int i = first; i <= last; i++)
            {
                double value = waveform.Samples[i];
                if (!IsFinite(value)) continue;
                if (value < minimum) minimum = value;
                if (value > maximum) maximum = value;
            }
            if (!IsFinite(minimum) || !IsFinite(maximum)) { minimum = -1.0; maximum = 1.0; }
            if (maximum <= minimum) { double pad = Math.Max(1.0, Math.Abs(maximum) * 0.1); minimum -= pad; maximum += pad; }
            else { double pad = (maximum - minimum) * 0.10; minimum -= pad; maximum += pad; }
            viewY0 = minimum; viewY1 = maximum;
            viewReady = true;
        }

        private void GetWaveformView(WaveformData waveform, out double x0, out double x1, out double y0, out double y1)
        {
            EnsureWaveformView(waveform);
            x0 = viewX0; x1 = viewX1; y0 = viewY0; y1 = viewY1;
        }

        private void ResetViewInternal()
        {
            packetDragIncident=packetDragReflection=-1;
            viewReady = false;
            viewWaveform = null;
            boxZoomActive = false;
            draggingCursor = 0;
            if (Capture) Capture = false;
        }

        private static void DrawDecayCurve(Graphics g, RectangleF plot, TdrAutoAnalysisResult analysis,
            double x0, double x1, double y0, double y1)
        {
            // An opposite-polarity pair cannot be joined by a signed exponential.
            // Keep its magnitude attenuation result, but do not draw an invented positive reflection.
            if (analysis.IncidentPolarity != analysis.ReflectionPolarity || analysis.ReflectionMagnitude > analysis.IncidentMagnitude) return;
            int count = Math.Min(analysis.DecayCurveTimeSeconds.Length, analysis.DecayCurveMagnitude.Length);
            if (count < 2) return;
            PointF[] points = new PointF[count];
            double sign = analysis.IncidentPolarity < 0 ? -1.0 : 1.0;
            for (int i = 0; i < count; i++)
            {
                double value = analysis.Baseline + sign * analysis.DecayCurveMagnitude[i];
                points[i] = new PointF(plot.Left + (float)((analysis.DecayCurveTimeSeconds[i] - x0) / Math.Max(1e-18, x1 - x0)) * plot.Width,
                    plot.Bottom - (float)((value - y0) / Math.Max(1e-18, y1 - y0)) * plot.Height);
            }
            using (var pen = new Pen(Color.FromArgb(239, 143, 38), 1.8F))
            {
                pen.DashStyle = DashStyle.Dash;
                DrawClippedPolyline(g, plot, pen, points);
            }
        }

        private void DrawInteractionOverlay(Graphics g, RectangleF plot)
        {
            if (boxZoomActive)
            {
                RectangleF selection = RectangleF.FromLTRB(Math.Min(boxZoomStart.X, boxZoomEnd.X), Math.Min(boxZoomStart.Y, boxZoomEnd.Y),
                    Math.Max(boxZoomStart.X, boxZoomEnd.X), Math.Max(boxZoomStart.Y, boxZoomEnd.Y));
                selection = RectangleF.Intersect(selection, plot);
                using (var fill = new SolidBrush(Color.FromArgb(35, 39, 109, 185))) g.FillRectangle(fill, selection);
                using (var pen = new Pen(Color.FromArgb(39, 109, 185), 1.2F)) { pen.DashStyle = DashStyle.Dash; g.DrawRectangle(pen, selection.X, selection.Y, selection.Width, selection.Height); }
            }
        }

        private void DrawHoverMeasurement(Graphics g, RectangleF plot)
        {
            PointF point;
            double delta;
            double reflectionDelta;
            double distance;
            lock (stateLock)
            {
                if (!hoverMeasurementAvailable) return;
                point = hoverReferencePoint;
                delta = hoverDeltaSeconds;
                reflectionDelta = hoverReflectionDeltaSeconds;
                distance = hoverDistanceMeters;
            }
            using (var pen = new Pen(ColorTranslator.FromHtml("#00A3C7"), 1F))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawLine(pen, point.X, plot.Top, point.X, plot.Bottom);
            }
            float badgeWidth = Px(250), badgeHeight = Px(70);
            float x = point.X + 12F;
            if (x + badgeWidth > plot.Right - 4F) x = point.X - badgeWidth - 12F;
            x = Math.Max(plot.Left + 4F, x);
            float y = Math.Max(plot.Top + 4F, Math.Min(plot.Bottom - badgeHeight - 4F, point.Y + 12F));
            RectangleF badge = new RectangleF(x, y, badgeWidth, badgeHeight);
            using (var path = RoundedRectangle(badge, 6F))
            using (var fill = new SolidBrush(Color.FromArgb(248, 235, 245, 255)))
            using (var border = new Pen(ColorTranslator.FromHtml("#00A3C7"), 1F))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            using (var font = CreateFont(9F, FontStyle.Bold))
            using (var brush = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            {
                g.DrawString(CalibrationText("距入射时差 ", "From incident ") + FormatSeconds(delta), font, brush, x + Px(8), y + Px(4));
                g.DrawString(CalibrationText("距反射时差 ", "From reflection ") + (IsFinite(reflectionDelta) ? FormatSeconds(reflectionDelta) : "—"), font, brush, x + Px(8), y + Px(25));
                g.DrawString(BuiltIn("位置 ") + distance.ToString("0.0", CultureInfo.InvariantCulture) + " m",
                    font, brush, x + Px(8), y + Px(46));
            }
        }

        private void UpdateHoverMeasurement(PointF point, PhaseState phase, WaveformData waveform, MarkerData markers)
        {
            TdrAutoAnalysisResult currentAnalysis;phase.Analyses.TryGetValue(SelectedLevelCode,out currentAnalysis);
            double incident=markers!=null?markers.StartTimeSeconds:currentAnalysis==null?Double.NaN:currentAnalysis.IncidentTimeSeconds;
            if (IsCapacitiveLoad || waveform == null || !IsFinite(incident) ||
                !IsFinite(phase.VelocityMPerUs) || phase.VelocityMPerUs <= 0.0)
            {
                ClearHoverMeasurement();
                return;
            }
            EnsureWaveformView(waveform);
            double time = ReferenceXToTime(point.X);
            double delta = time - incident;
            double distance = delta * phase.VelocityMPerUs * 1000000.0 / 2.0;
            lock (stateLock)
            {
                hoverMeasurementAvailable = true;
                hoverReferencePoint = point;
                hoverTimeSeconds = time;
                hoverDeltaSeconds = delta;
                hoverReflectionDeltaSeconds = markers==null?Double.NaN:time - markers.DistanceMarkerSeconds;
                hoverDistanceMeters = distance;
            }
            Invalidate();
        }

        private void ClearHoverMeasurement()
        {
            bool changed;
            lock (stateLock)
            {
                changed = hoverMeasurementAvailable;
                hoverMeasurementAvailable = false;
            }
            if (changed) Invalidate();
        }

        private bool TryCurrentWaveform(out PhaseState phase, out WaveformData waveform,
            out MarkerData markers, out string levelCode, out int phaseIndex)
        {
            lock (stateLock)
            {
                phaseIndex = selectedPhaseIndex;
                levelCode = selectedLevelCode;
                phase = GetPhaseStateNoLock(phaseIndex);
                WaveformData raw;
                if (!phase.Waveforms.TryGetValue(levelCode, out raw)) { waveform = null; markers = null; return false; }
                waveform = ActiveWaveformNoLock(phase, levelCode, raw);
                phase.Markers.TryGetValue(levelCode, out markers);
                return waveform != null;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (IsBusy) return;
            if(numericEditor!=null&&numericEditor.Visible&&!numericEditor.Bounds.Contains(e.Location))
            { if(!CommitNumericEditor(false))return;Focus(); }
            if(HandleNoiseDown(e))return;
            if (UseCompactLayout && e.Button == MouseButtons.Left && ManualBandwidthPanelVisible && CompactLayout.Bandwidth.Contains(e.Location))
            {
                bandwidthSliderDragging = true;
                bandwidthSliderPreviewMHz = CompactBandwidthAtX(e.X);
                StartBandwidthLiveUpdates();
                Capture = true; Cursor = Cursors.Hand; Invalidate(); return;
            }
            if (UseCompactLayout && (compactQualityTab || compactLevelsOpen || !CompactLayout.Plot.Contains(e.Location))) return;
            if (e.Button == MouseButtons.Right)
            {
                PointF restorePoint;
                if (TryReferencePoint(e.Location, out restorePoint) && PlotContains(restorePoint))
                {
                    if (TryShowJointMenu(restorePoint, e.Location)) return;
                    ResetViewInternal();
                    selectedCursor = 0;
                    ClearHoverMeasurement();
                    Invalidate();
                }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (HandleLayoutMouseDown(e)) return;
            PointF point;
            if (!TryReferencePoint(e.Location, out point)) return;
            if (ManualBandwidthPanelVisible && point.X >= BandwidthSliderLeft - 8F && point.X <= BandwidthSliderRight + 8F &&
                point.Y >= BandwidthSliderTop && point.Y <= BandwidthSliderBottom)
            {
                bandwidthSliderDragging = true;
                bandwidthSliderPreviewMHz = BandwidthAtReferenceX(point.X);
                StartBandwidthLiveUpdates();
                Capture = true; Cursor = Cursors.Hand; Invalidate(); return;
            }
            PhaseState phase; WaveformData waveform; MarkerData markers; string level; int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out markers, out level, out phaseIndex) || !PlotContains(point)) return;
            EnsureWaveformView(waveform);
            if(markers==null&&HitPendingReflection(point,phase,level)) {
                var selection=ReflectionSelectionNoLock(phase,level,true);
                selectedCursor=draggingCursor=3;pendingReflectionDragOriginalIndex=selection.Index;
                cursorGrabOffsetX=point.X-TimeToReferenceX(selection.Source.X0Seconds+selection.Index*selection.Source.DxSeconds);
                Capture=true;Cursor=Cursors.SizeWE;Invalidate();return;
            }
            MarkerData editing=CurrentPacketGuiMarkers(markers);
            int hit = HitMarker(point, editing);
            if (hit != 0)
            {
                selectedCursor = draggingCursor = hit;
                dragOriginalIncidentTime = markers == null ? Double.NaN : markers.StartTimeSeconds;
                dragOriginalReflectionTime = markers == null ? Double.NaN : markers.DistanceMarkerSeconds;
                packetDragIncident=ReferenceXToSample(TimeToReferenceX(editing.StartTimeSeconds),waveform);
                packetDragReflection=ReferenceXToSample(TimeToReferenceX(editing.DistanceMarkerSeconds),waveform);
                cursorGrabOffsetX = point.X - TimeToReferenceX(hit == 1 ? editing.StartTimeSeconds : editing.DistanceMarkerSeconds);
                Capture = true; Cursor = Cursors.SizeWE; Invalidate(); return;
            }
            selectedCursor = 0;
            boxZoomActive = true;
            boxZoomStart = boxZoomEnd = point;
            Capture = true; Cursor = Cursors.Cross; Invalidate();
        }

        private bool HandleExtendedMouseMove(MouseEventArgs e)
        {
            if(HandleNoiseMove(e))return true;
            if (UseCompactLayout && bandwidthSliderDragging)
            {
                if ((e.Button & MouseButtons.Left) == 0) { FinishBandwidthDrag(false); return true; }
                bandwidthSliderPreviewMHz = CompactBandwidthAtX(e.X);
                Invalidate(new Rectangle(0, 220, 235, 160)); return true;
            }
            if (UseCompactLayout && compactQualityTab) { UpdateNoiseHover(e.Location);ClearHoverMeasurement(); return CurrentNoisePlotRectangle.Contains(e.Location); }
            PointF point;
            if (!TryReferencePoint(e.Location, out point)) return bandwidthSliderDragging || draggingCursor != 0 || boxZoomActive;
            if(!UseCompactLayout&&CurrentNoisePlotRectangle.Contains(point)){UpdateNoiseHover(e.Location);ClearHoverMeasurement();return true;}
            if (bandwidthSliderDragging)
            {
                if ((e.Button & MouseButtons.Left) == 0) { FinishBandwidthDrag(false); return true; }
                bandwidthSliderPreviewMHz = BandwidthAtReferenceX(point.X); Invalidate(); return true;
            }
            if (draggingCursor != 0)
            {
                if ((e.Button & MouseButtons.Left) == 0) { RestoreDraggedMarker(SelectedPhaseIndex, SelectedLevelCode); EndPlotGesture(); return true; }
                PreviewDraggedCursor(point); return true;
            }
            if (boxZoomActive)
            {
                if ((e.Button & MouseButtons.Left) == 0) { EndPlotGesture(); return true; }
                boxZoomEnd = ClampPlotPoint(point); Invalidate(); return true;
            }
            PhaseState phase; WaveformData waveform; MarkerData markers; string level; int phaseIndex;
            if (TryCurrentWaveform(out phase, out waveform, out markers, out level, out phaseIndex) && PlotContains(point))
            {
                UpdateHoverMeasurement(point, phase, waveform, markers);
                Cursor = HitMarker(point, CurrentPacketGuiMarkers(markers)) != 0 || HitPendingReflection(point,phase,level) ? Cursors.SizeWE : Cursors.Cross;
                return true;
            }
            ClearHoverMeasurement();
            return false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if(noiseTooltip!=null)noiseTooltip.SetToolTip(this,null);
            ClearHoverMeasurement();
        }

        private bool HandleExtendedMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return false;
            if (UseCompactLayout && bandwidthSliderDragging)
            { bandwidthSliderPreviewMHz = CompactBandwidthAtX(e.X); FinishBandwidthDrag(true); return true; }
            PointF point;
            TryReferencePoint(e.Location, out point);
            if (bandwidthSliderDragging) { bandwidthSliderPreviewMHz = BandwidthAtReferenceX(point.X); FinishBandwidthDrag(true); return true; }
            if (draggingCursor != 0)
            {
                PreviewDraggedCursor(point);
                FinishCursorDrag();
                EndPlotGesture();
                return true;
            }
            if (boxZoomActive)
            {
                boxZoomEnd = ClampPlotPoint(point);
                ApplyBoxZoom();
                EndPlotGesture();
                return true;
            }
            return false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (UseCompactLayout && (compactQualityTab || compactLevelsOpen)) return;
            if (e.Button != MouseButtons.Left) return;
            PointF point;
            if (!TryReferencePoint(e.Location, out point) || !PlotContains(point)) return;
            if (TryOpenJointDialog(point)) return;
            MoveManualReflection(point);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if(NoiseGestureActive)return;
            if (HandleCompactLevelWheel(e)) return;
            if(!IsBusy && HandleNoiseWheel(e))return;
            if (UseCompactLayout && (compactQualityTab || compactLevelsOpen)) return;
            if (IsBusy) return;
            if (draggingCursor != 0 || boxZoomActive) return;
            PointF point;
            if (!TryReferencePoint(e.Location, out point) || !PlotContains(point)) return;
            PhaseState phase; WaveformData waveform; MarkerData markers; string level; int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out markers, out level, out phaseIndex)) return;
            EnsureWaveformView(waveform);
            ZoomWaveform(point,e.Delta,(ModifierKeys&Keys.Shift)!=0,waveform);
            Invalidate();
        }
        private void ZoomWaveform(PointF point,int delta,bool vertical,WaveformData waveform)
        {
            double factor = delta > 0 ? 0.82 : 1/.82;
            if (vertical)
            {
                double anchor = ReferenceYToValue(point.Y);
                viewY0 = anchor - (anchor - viewY0) * factor;
                viewY1 = anchor + (viewY1 - anchor) * factor;
            }
            else
            {
                double anchor = ReferenceXToTime(point.X);
                double dataMin = waveform.X0Seconds;
                double dataMax = dataMin + (waveform.Samples.Length - 1) * waveform.DxSeconds;
                viewX0 = Math.Max(dataMin, anchor - (anchor - viewX0) * factor);
                viewX1 = Math.Min(dataMax, anchor + (viewX1 - anchor) * factor);
                if (viewX1 - viewX0 < waveform.DxSeconds * 2.0)
                { viewX0 = Math.Max(dataMin, anchor - waveform.DxSeconds); viewX1 = Math.Min(dataMax, anchor + waveform.DxSeconds); }
                LimitDisplayWindow(waveform,anchor);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if(NoiseGestureActive){if(keyData==Keys.Escape)CancelNoiseGesture();return true;}
            if (HandleCalibrationKeyboardKey(keyData, (msg.LParam.ToInt64() & (1L << 30)) != 0)) return true;
            if(keyData==Keys.Escape&&noiseSelecting){noiseSelecting=false;Capture=false;noiseSelectionStart=noiseSelectionEnd=noiseSelectionPeak=-1;Invalidate();return true;}
            if (keyData == Keys.Escape && (selectedCursor != 0 || draggingCursor != 0 || boxZoomActive || bandwidthSliderDragging))
            { ClearCursorSelection(); bandwidthSliderDragging = false; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if(!Capture&&NoiseGestureActive)CancelNoiseGesture();
            if (!Capture) { if(draggingCursor!=0)RestoreDraggedMarker(SelectedPhaseIndex,SelectedLevelCode); noiseSelecting=false;bandwidthSliderDragging = false; draggingCursor = 0; boxZoomActive = false; centerSplitterDragging = false; }
        }

        private void FinishBandwidthDrag(bool commit)
        {
            bool active = bandwidthSliderDragging;
            if (bandwidthDragTimer != null) bandwidthDragTimer.Stop();
            bandwidthSliderDragging = false;
            if (Capture) Capture = false;
            Cursor = Cursors.Default;
            if (commit && active) ExecuteParameterAction("BANDWIDTH", delegate { SetPhaseBandwidth(SelectedPhaseIndex, true, bandwidthSliderPreviewMHz); });
            else Invalidate();
        }

        private static double BandwidthAtReferenceX(float x)
        {
            double fraction = Math.Max(0.0, Math.Min(1.0, (x - BandwidthSliderLeft) / (BandwidthSliderRight - BandwidthSliderLeft)));
            return Math.Round(1.0 + 59.0 * fraction, 1, MidpointRounding.AwayFromZero);
        }

        private double CompactBandwidthAtX(float x)
        {
            RectangleF track = CompactLayout.Bandwidth;
            return Math.Round(Math.Max(1, Math.Min(60, 1 + (x-track.Left)*59/track.Width)), 1);
        }

        private bool PlotContains(PointF point)
        {
            RectangleF plot = CurrentPlotRectangle;
            return plot.Contains(point);
        }

        private PointF ClampPlotPoint(PointF point)
        {
            RectangleF plot = CurrentPlotRectangle;
            return new PointF(Math.Max(plot.Left, Math.Min(plot.Right, point.X)),
                Math.Max(plot.Top, Math.Min(plot.Bottom, point.Y)));
        }

        private int HitMarker(PointF point, MarkerData markers)
        {
            if (markers == null) return 0;
            if (paintedBadgeMarkers!=null) {
                if (reflectionBadgeHit.Contains(point)) return 2;
                if (incidentBadgeHit.Contains(point)) return 1;
            }
            float incidentX = TimeToReferenceX(markers.StartTimeSeconds);
            float reflectionX = TimeToReferenceX(markers.DistanceMarkerSeconds);
            bool incidentHit = Math.Abs(point.X - incidentX) <= 9F;
            bool reflectionHit = Math.Abs(point.X - reflectionX) <= 9F;
            if (incidentHit && reflectionHit) return selectedCursor == 1 ? 2 : 1;
            return incidentHit ? 1 : reflectionHit ? 2 : 0;
        }

        private float TimeToReferenceX(double seconds)
        {
            RectangleF plot = CurrentPlotRectangle;
            return plot.Left + (float)((seconds - viewX0) / Math.Max(1e-18, viewX1 - viewX0)) * plot.Width;
        }

        private double ReferenceXToTime(float x)
        {
            RectangleF plot = CurrentPlotRectangle;
            return viewX0 + (x - plot.Left) / plot.Width * (viewX1 - viewX0);
        }

        private double ReferenceYToValue(float y)
        {
            RectangleF plot = CurrentPlotRectangle;
            return viewY1 - (y - plot.Top) / plot.Height * (viewY1 - viewY0);
        }

        private int ReferenceXToSample(float x, WaveformData waveform)
        {
            RectangleF plot = CurrentPlotRectangle;
            double seconds = ReferenceXToTime(Math.Max(plot.Left, Math.Min(plot.Right, x)));
            return Math.Max(0, Math.Min(waveform.Samples.Length - 1,
                (int)Math.Round((seconds - waveform.X0Seconds) / waveform.DxSeconds)));
        }

        private void PreviewDraggedCursor(PointF point)
        {
            if(draggingCursor==3){PreviewPendingReflection(point);return;}
            PhaseState phase; WaveformData waveform; MarkerData markers; string level; int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out markers, out level, out phaseIndex)) return;
            int target = ReferenceXToSample(point.X - cursorGrabOffsetX, waveform);
            if(packetDragIncident>=0&&packetDragReflection>packetDragIncident) {
                if(draggingCursor==1&&target<packetDragReflection)packetDragIncident=target;
                if(draggingCursor==2&&target>packetDragIncident)packetDragReflection=target;
                Invalidate();return; // Preview never publishes provisional travel timing.
            }
            if(markers==null)return;
            double time = waveform.X0Seconds + target * waveform.DxSeconds;
            lock (stateLock)
            {
                phase = GetPhaseStateNoLock(phaseIndex);
                MarkerData old;
                if (!phase.Markers.TryGetValue(level, out old)) return;
                double start = draggingCursor == 1 ? time : old.StartTimeSeconds;
                double end = draggingCursor == 2 ? time : old.DistanceMarkerSeconds;
                if (end <= start) return;
                double distance = (end-start)*phase.VelocityMPerUs*1e6/2;
                phase.Markers[level] = draggingCursor == 1
                    ? new MarkerData(time, old.DistanceMarkerSeconds, old.VelocityMarkerSeconds, distance)
                    : new MarkerData(old.StartTimeSeconds, time, old.VelocityMarkerSeconds, distance);
                PublishLevelNoLock(phase, level);
            }
            Invalidate();
        }

        private void FinishCursorDrag()
        {
            int cursor = draggingCursor;
            int phaseIndex = SelectedPhaseIndex;
            string level = SelectedLevelCode;
            if(cursor==3) {
                var selection=ReflectionSelectionNoLock(GetPhaseStateNoLock(phaseIndex),level,false);
                if(selection!=null)SelectReflectionPacket(phaseIndex,level,selection.Index);
                return;
            }
            if((cursor==1||cursor==2)&&packetDragIncident>=0) {
                SetPhasePacketPeaks(phaseIndex,level,packetDragIncident,packetDragReflection);return;
            }
            if (cursor == 1)
            {
                MarkerData timing; lock (stateLock) GetPhaseStateNoLock(phaseIndex).Markers.TryGetValue(level, out timing);
                if (timing == null || !SetPhaseTimingCursors(phaseIndex, level, timing.StartTimeSeconds, timing.DistanceMarkerSeconds)) RestoreDraggedMarker(phaseIndex, level);
                return;
            }
            if (cursor == 2)
            {
                PhaseState phase; WaveformData waveform; MarkerData marker; string ignored; int ignoredIndex;
                if (TryCurrentWaveform(out phase, out waveform, out marker, out ignored, out ignoredIndex) && marker != null)
                {
                    int incident = ReferenceXToSample(TimeToReferenceX(marker.StartTimeSeconds), waveform);
                    int target = ReferenceXToSample(TimeToReferenceX(marker.DistanceMarkerSeconds), waveform);
                    if (target > incident) StoreManualCursorAnalysis(phaseIndex, level, target, false);
                    else RestoreDraggedMarker(phaseIndex, level);
                }
            }
        }

        private void MoveManualReflection(PointF point)
        {
            PhaseState phase; WaveformData waveform; MarkerData marker; string level; int phaseIndex;
            if (!TryCurrentWaveform(out phase, out waveform, out marker, out level, out phaseIndex)) return;
            TdrAutoAnalysisResult a;if(!phase.Analyses.TryGetValue(level,out a)||a==null)return;
            int incident = marker==null?a.IncidentIndex:ReferenceXToSample(TimeToReferenceX(marker.StartTimeSeconds), waveform);
            int target = ReferenceXToSample(point.X, waveform);
            if (target <= incident) return;
            SelectReflectionPacket(phaseIndex, level, target);
            selectedCursor = 2;
        }

        private void StoreManualCursorAnalysis(int phaseIndex, string level, int reflectionIndex, bool moveIncident)
        {
            lock (stateLock)
            {
                PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                WaveformData raw;
                if (!phase.Waveforms.TryGetValue(level, out raw)) return;
                WaveformData waveform = ActiveWaveformNoLock(phase, level, raw);
                MarkerData marker;
                TdrAutoAnalysisResult previous;
                phase.Analyses.TryGetValue(level, out previous);
                if (!phase.Markers.TryGetValue(level, out marker)) {
                    if(IsCapacitiveLoad||previous==null||previous.IncidentIndex<0||!IsFinite(previous.IncidentTimeSeconds)||
                        reflectionIndex<=previous.IncidentIndex||reflectionIndex>=waveform.Samples.Length)return;
                    // Local construction is only used after the operator commits
                    // an actual sample; pending handles never enter Markers.
                    marker=new MarkerData(previous.IncidentTimeSeconds,waveform.X0Seconds+reflectionIndex*waveform.DxSeconds,Double.NaN,Double.NaN);
                }
                int incidentIndex = Math.Max(0, Math.Min(waveform.Samples.Length - 1,
                    (int)Math.Round((marker.StartTimeSeconds - waveform.X0Seconds) / waveform.DxSeconds)));
                if (reflectionIndex <= incidentIndex || reflectionIndex >= waveform.Samples.Length) return;
                // Keep exact manual timing. In first-arrival mode amplitude
                // belongs to the connected pulse peak, not its leading edge.
                // Install the new choice before refresh so old cache cannot win.
                int incidentPeak=previous!=null&&previous.IncidentIndex>=incidentIndex&&previous.IncidentIndex<reflectionIndex?previous.IncidentIndex:incidentIndex;
                int reflectionPeak=UseFirstArrivalTiming?PeakAfterArrival(waveform,reflectionIndex,waveform.Samples.Length,
                    Math.Max(12,Math.Min(96,(reflectionIndex-incidentPeak)/6)),previous):reflectionIndex;
                TdrAutoAnalysisResult result = BuildManualResult(waveform, phase, incidentPeak, reflectionPeak, previous);
                double start=marker.StartTimeSeconds,end=waveform.X0Seconds+reflectionIndex*waveform.DxSeconds;
                phase.Analyses[level]=result;
                phase.Markers[level]=new MarkerData(start,end,start+2*phase.CableLengthMeters/(phase.VelocityMPerUs*1e6),(end-start)*phase.VelocityMPerUs*1e6/2);
                RememberManualReflectionNoLock(phase,level);
                StoreAnalysisNoLock(phase, level, waveform, result);
                System.Threading.Interlocked.Increment(ref cursorChangedSequence);
                System.Threading.Interlocked.Increment(ref analysisChangedSequence);
            }
            Invalidate();
            RaiseAnalysisAndCursorEvents();
        }

        private static int PeakAfterArrival(WaveformData wave,int arrival,int limit,int width,TdrAutoAnalysisResult previous)
        {
            double baseline=previous!=null&&IsFinite(previous.Baseline)?previous.Baseline:0;
            double sigma=previous!=null&&IsFinite(previous.NoiseSigma)?Math.Max(0,previous.NoiseSigma):0;
            int best=arrival,last=Math.Min(limit-1,arrival+width);
            double largest=Math.Abs(wave.Samples[best]-baseline);
            int descending=0;
            for(int i=arrival+1;i<=last;i++) {
                double magnitude=Math.Abs(wave.Samples[i]-baseline);
                if(!IsFinite(magnitude))return arrival;
                if(magnitude>largest) {best=i;largest=magnitude;descending=0;}
                else if(largest>6*sigma && largest-magnitude>Math.Max(3*sigma,.05*largest)) {
                    // Stop at the first resolved lobe; a later, larger ringing
                    // packet must not replace the pulse selected by the user.
                    if(++descending>=2 || (wave.Samples[i]-baseline)*(wave.Samples[best]-baseline)<=0)break;
                }
            }
            return best;
        }

        private static TdrAutoAnalysisResult BuildManualResult(WaveformData waveform, PhaseState phase,
            int incidentIndex, int reflectionIndex, TdrAutoAnalysisResult previous)
        {
            var result = new TdrAutoAnalysisResult();
            result.SearchStatus = TdrSearchStatus.Ok;
            result.Message = "Manual";
            result.IncidentIndex = incidentIndex;
            result.ReflectionIndex = reflectionIndex;
            result.IncidentTimeSeconds = waveform.X0Seconds + incidentIndex * waveform.DxSeconds;
            result.ReflectionTimeSeconds = waveform.X0Seconds + reflectionIndex * waveform.DxSeconds;
            result.PredictedReflectionTimeSeconds = result.IncidentTimeSeconds +
                2.0 * phase.CableLengthMeters / (phase.VelocityMPerUs * 1000000.0);
            result.Baseline = previous == null || !IsFinite(previous.Baseline) ? 0.0 : previous.Baseline;
            result.NoiseSigma = previous == null ? Double.NaN : previous.NoiseSigma;
            if(previous==null||!IsFinite(previous.Baseline)||!IsFinite(previous.NoiseSigma)) {
                // A newly accepted acquisition pair has no analyzer predecessor.
                // Use the same measured baseline/noise as a later refresh,
                // instead of zero baseline and an unmeasured noise floor.
                double baseline,sigma;TdrReflectionAnalyzer.RawTimingStatistics(waveform.Samples,out baseline,out sigma);
                if(IsFinite(baseline))result.Baseline=baseline;
                if(IsFinite(sigma))result.NoiseSigma=sigma;
            }
            result.IncidentValue = waveform.Samples[incidentIndex] - result.Baseline;
            result.ReflectionValue = waveform.Samples[reflectionIndex] - result.Baseline;
            result.IncidentMagnitude = Math.Abs(result.IncidentValue);
            result.ReflectionMagnitude = Math.Abs(result.ReflectionValue);
            result.IncidentPolarity = result.IncidentValue < 0 ? -1 : 1;
            result.ReflectionPolarity = result.ReflectionValue < 0 ? -1 : 1;
            result.IsPolarityInverted = result.IncidentPolarity != result.ReflectionPolarity;
            result.CalculatedCableLengthMeters = Math.Max(0.0, result.ReflectionTimeSeconds - result.IncidentTimeSeconds) *
                phase.VelocityMPerUs * 1000000.0 / 2.0;
            result.TimingErrorSeconds = result.ReflectionTimeSeconds - result.PredictedReflectionTimeSeconds;
            result.Confidence = previous == null ? 0.0 : previous.Confidence;
            double duration = result.ReflectionTimeSeconds - result.IncidentTimeSeconds;
            if (duration > 0.0 && result.IncidentMagnitude > result.ReflectionMagnitude && result.ReflectionMagnitude > 0.0)
            {
                result.DecayAmplitudeA = result.IncidentMagnitude;
                result.DecayConstantPerSecond = Math.Log(result.IncidentMagnitude / result.ReflectionMagnitude) / duration;
                result.DecayConstantPerMicrosecond = result.DecayConstantPerSecond * 1e-6;
                result.DecayTauSeconds = 1.0 / result.DecayConstantPerSecond;
                result.AttenuationDb = 20.0 * Math.Log10(result.IncidentMagnitude / result.ReflectionMagnitude);
                result.FitStatus = TdrDecayFitStatus.InsufficientPoints;
                int count = reflectionIndex - incidentIndex + 1;
                result.DecayCurveTimeSeconds = new double[count]; result.DecayCurveMagnitude = new double[count];
                for (int i = 0; i < count; i++)
                {
                    double time = result.IncidentTimeSeconds + i * waveform.DxSeconds;
                    result.DecayCurveTimeSeconds[i] = time;
                    result.DecayCurveMagnitude[i] = result.IncidentMagnitude *
                        Math.Exp(-result.DecayConstantPerSecond * (time - result.IncidentTimeSeconds));
                }
            }
            else result.FitStatus = TdrDecayFitStatus.NonDecaying;
            return result;
        }

        private static int SnapIncidentFive(double[] samples, int target)
        {
            int first = Math.Max(0, target - 2), last = Math.Min(samples.Length - 1, target + 2), best = target;
            for (int i = first; i <= last; i++) if (Math.Abs(samples[i]) > Math.Abs(samples[best])) best = i;
            return best;
        }

        private int SnapReflectionMatchingIncident(WaveformData waveform, PhaseState phase, int incident, int target)
        {
            double[] samples = waveform.Samples;
            if (samples.Length < 3 || !IsFinite(phase.CableLengthMeters) || !IsFinite(phase.VelocityMPerUs) ||
                phase.CableLengthMeters <= 0.0 || phase.VelocityMPerUs <= 0.0) return -1;
            double roundTripSeconds = 2.0 * phase.CableLengthMeters / (phase.VelocityMPerUs * 1000000.0);
            double travelSamples = roundTripSeconds / waveform.DxSeconds;
            double expected = incident + travelSamples;
            if (expected < 1.0 || expected > samples.Length - 2.0) return -1;
            int halfWindow = Math.Max(8, (int)Math.Ceiling(travelSamples * reflectionSearchToleranceFraction));
            int hardFirst = Math.Max(1, (int)Math.Floor(expected - halfWindow));
            int hardLast = Math.Min(samples.Length - 2, (int)Math.Ceiling(expected + halfWindow));
            if (target < hardFirst || target > hardLast) return -1;
            bool peak = IsLocalPeak(samples, incident);
            if (!peak && !IsLocalValley(samples, incident)) peak = samples[incident] >= 0.0;
            int radius = Math.Max(12, Math.Min(128, samples.Length / 40));
            int first = Math.Max(hardFirst, target - radius), last = Math.Min(hardLast, target + radius);
            int best = -1, distance = Int32.MaxValue;
            for (int i = first; i <= last; i++)
            {
                bool match = peak ? IsLocalPeak(samples, i) : IsLocalValley(samples, i);
                int d = Math.Abs(i - target);
                if (match && (best < 0 || d < distance)) { best = i; distance = d; }
            }
            return best;
        }

        private void RestoreDraggedMarker(int phaseIndex, string level)
        {
            if(packetDragIncident>=0) {packetDragIncident=packetDragReflection=-1;Invalidate();return;}
            if(draggingCursor==3) {
                var selection=ReflectionSelectionNoLock(GetPhaseStateNoLock(phaseIndex),level,false);
                if(selection!=null&&pendingReflectionDragOriginalIndex>=0)selection.Index=pendingReflectionDragOriginalIndex;
                Invalidate();return;
            }
            if (!IsFinite(dragOriginalIncidentTime) || !IsFinite(dragOriginalReflectionTime)) return;
            lock (stateLock)
            {
                PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                MarkerData old;
                if (!phase.Markers.TryGetValue(level, out old)) return;
                phase.Markers[level] = new MarkerData(dragOriginalIncidentTime,
                    dragOriginalReflectionTime, old.VelocityMarkerSeconds,
                    (dragOriginalReflectionTime-dragOriginalIncidentTime)*phase.VelocityMPerUs*1e6/2);
                PublishLevelNoLock(phase, level);
            }
            Invalidate();
        }

        private static bool IsLocalPeak(double[] values, int index)
        { return index > 0 && index + 1 < values.Length && values[index] >= values[index - 1] && values[index] > values[index + 1]; }
        private static bool IsLocalValley(double[] values, int index)
        { return index > 0 && index + 1 < values.Length && values[index] <= values[index - 1] && values[index] < values[index + 1]; }

        private void ApplyBoxZoom()
        {
            RectangleF rectangle = RectangleF.FromLTRB(Math.Min(boxZoomStart.X, boxZoomEnd.X), Math.Min(boxZoomStart.Y, boxZoomEnd.Y),
                Math.Max(boxZoomStart.X, boxZoomEnd.X), Math.Max(boxZoomStart.Y, boxZoomEnd.Y));
            if (rectangle.Width < 6F || rectangle.Height < 6F) return;
            double left = ReferenceXToTime(rectangle.Left), right = ReferenceXToTime(rectangle.Right);
            double top = ReferenceYToValue(rectangle.Top), bottom = ReferenceYToValue(rectangle.Bottom);
            if (right > left && top > bottom) { viewX0 = left; viewX1 = right; viewY0 = bottom; viewY1 = top; viewReady = true; }
        }

        private void EndPlotGesture()
        {
            packetDragIncident=packetDragReflection=-1;
            draggingCursor = 0; boxZoomActive = false;
            if (Capture) Capture = false;
            Cursor = Cursors.Default; Invalidate();
        }

        private static double DecayValueAt(TdrAutoAnalysisResult analysis, double time)
        {
            if (analysis == null || analysis.DecayCurveTimeSeconds == null || analysis.DecayCurveTimeSeconds.Length == 0 ||
                time < analysis.IncidentTimeSeconds || time > analysis.ReflectionTimeSeconds) return Double.NaN;
            return analysis.DecayAmplitudeA * Math.Exp(-analysis.DecayConstantPerSecond * (time - analysis.IncidentTimeSeconds));
        }
    }
}
