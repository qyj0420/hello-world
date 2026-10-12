using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Windows.Forms;

namespace HVX.Controls
{
    [ToolboxItem(true)]
    public sealed partial class CalibrationProgramControl : UserControl
    {
        public const int ReferenceWidth = 1920;
        public const int ReferenceHeight = 1080;
        public const int MinimumWindowWidth = 1280;
        public const int MinimumWindowHeight = 720;
        // The approved raster and existing host splitter coordinates retain their source grid.
        // Rendering and input share the same source-to-design transform.
        private const int ArtworkWidth = 1672;
        private const int ArtworkHeight = 941;

        private static readonly string[] LevelCodes =
        {
            "TDR", "200nC", "150nC", "100nC", "50nC", "20nC", "10nC", "5nC",
            "2nC", "1nC", "500pC", "200pC", "100pC", "50pC", "20pC", "10pC"
        };

        private static readonly string[] LevelLabels =
        {
            "TDR", "200 nC", "150 nC", "100 nC", "50 nC", "20 nC", "10 nC", "5 nC",
            "2 nC", "1 nC", "500 pC", "200 pC", "100 pC", "50 pC", "20 pC", "10 pC"
        };

        private static readonly string[] BasicLevelCodes = { "TDR", "100nC", "5nC" };
        private readonly object stateLock = new object();
        private readonly object renderLock = new object();
        private readonly PhaseState allPhase = new PhaseState();
        private readonly PhaseState[] phases = { new PhaseState(), new PhaseState(), new PhaseState() };
        private readonly int ownerThreadId;
        private Bitmap approvedFrame;
        private Font ownedControlFont;
        private int lifetimeEnded;
        private int selectedPhaseIndex = -1;
        private string selectedLevelCode = "TDR";
        private bool calibrateAllLevels;
        private bool showAllLevels;
        private bool noiseBaselineVisible;
        private bool advancedMode;
        private double signalToNoiseDb = Double.NaN;
        private double peakVoltageV = Double.NaN;
        private double noiseVoltageV = Double.NaN;
        private long phaseChangedSequence;
        private long levelChangedSequence;
        private long calibrationScopeChangedSequence;
        private long jointChangedSequence;
        private long advancedModeChangedSequence;
        private long parameterChangedSequence;
        private long startRequestSequence;
        private long deleteRequestSequence;
        private long saveRequestSequence;
        private long exitRequestSequence;
        private int requestedPhaseIndex;
        private string requestedLevelCode = "5nC";
        private string[] requestedLevelCodes = { "5nC" };
        private string lastRequestActionCode = String.Empty;

        public CalibrationProgramControl()
        {
            ownerThreadId = Environment.CurrentManagedThreadId;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Canvas;
            ForeColor = Theme.Ink;
            ownedControlFont = CreateFont(9F, FontStyle.Regular);
            Font = ownedControlFont;
            // 1280 x 720 is the OUTER LabVIEW window minimum, not this container's client area.
            // The host owns that constraint; this control must fit the remaining area below host chrome.
            MinimumSize = Size.Empty;
            Size = new Size(ReferenceWidth, ReferenceHeight);
            TabStop = true;
            approvedFrame = LoadApprovedFrame();
            InitializeEditableLayout();
            StartRequested += StartHardwareAcquisitionOnOriginalPage;
            AnalysisChanged += ReviewShortCableRecommendationOnAnalysis;
            MouseMove += delegate(object sender, MouseEventArgs e)
            {
                PointF point;
                if (!TryReferencePoint(e.Location, out point)) point = new PointF(-1, -1);
                bool before = new RectangleF(1398, 796, 243, 94).Contains(themeHover);
                themeHover = point;
                if (before != new RectangleF(1398, 796, 243, 94).Contains(themeHover)) Invalidate();
            };
            MouseLeave += delegate { themeHover = new PointF(-1, -1); Invalidate(); };
        }

        public event EventHandler<CalibrationSelectionEventArgs> PhaseChanged;
        public event EventHandler<CalibrationSelectionEventArgs> LevelChanged;
        public event EventHandler CalibrationScopeChanged;
        public event EventHandler AdvancedModeChanged;
        public event EventHandler ParameterChanged;
        public event EventHandler<CalibrationRequestEventArgs> StartRequested;
        public event EventHandler<CalibrationRequestEventArgs> DeleteRequested;
        public event EventHandler<CalibrationRequestEventArgs> SaveRequested;
        public event EventHandler<CalibrationJointEventArgs> JointAdded;
        public event EventHandler<CalibrationJointEventArgs> JointRemoved;
        public event EventHandler ExitRequested;

        [Category("校准")]
        public int SelectedPhaseIndex
        {
            get { return Volatile.Read(ref selectedPhaseIndex); }
            set { SetSelectedPhase(value); }
        }

        [Browsable(false)]
        public string SelectedPhaseCode
        {
            get { return PhaseCodeFromIndex(SelectedPhaseIndex); }
        }

        [Category("校准")]
        public string SelectedLevelCode
        {
            get { return Volatile.Read(ref selectedLevelCode); }
            set { SetSelectedLevel(value); }
        }

        [Browsable(false)]
        public string StartButtonText
        {
            get { return String.Equals(SelectedLevelCode, "TDR", StringComparison.Ordinal) ? "开始测距" : "开始校准"; }
        }

        [Category("校准")]
        public bool CalibrateAllLevels
        {
            get { return Volatile.Read(ref calibrateAllLevels); }
            set { SetCalibrateAllLevels(value); }
        }

        [Category("显示")]
        public bool ShowAllLevels
        {
            get { return Volatile.Read(ref showAllLevels); }
            set { RunOnUiThread(delegate {
                // The visible list is not an acquisition batch. Keep the user's
                // selected charge even when collapsing the extra rows.
                lock (stateLock) { showAllLevels = value; calibrateAllLevels = false; }
                compactLevelOffset = 0; ClearHoverMeasurement(); Invalidate();
            }); }
        }

        [Category("信号质量")]
        public bool NoiseBaselineVisible
        {
            get { return Volatile.Read(ref noiseBaselineVisible); }
            set
            {
                RunOnUiThread(delegate
                {
                    lock (stateLock)
                    {
                        if (noiseBaselineVisible == value) return;
                        noiseBaselineVisible = value;
                    }
                    Invalidate();
                });
            }
        }

        [Category("校准")]
        public bool AdvancedMode
        {
            get { return Volatile.Read(ref advancedMode); }
            set { SetAdvancedMode(value); }
        }

        [Category("相别参数")]
        public double CableLengthAll { get { return GetPhaseCableLengthMeters(-1); } set { SetPhaseLength(-1, value); } }
        [Category("相别参数")]
        public double CableLengthA { get { return GetPhaseCableLengthMeters(0); } set { SetPhaseLength(0, value); } }
        [Category("相别参数")]
        public double CableLengthB { get { return GetPhaseCableLengthMeters(1); } set { SetPhaseLength(1, value); } }
        [Category("相别参数")]
        public double CableLengthC { get { return GetPhaseCableLengthMeters(2); } set { SetPhaseLength(2, value); } }
        [Category("相别参数")]
        public double PropagationVelocityAll { get { return GetPhaseVelocityMPerUs(-1); } set { SetPhaseVelocity(-1, value); } }
        [Category("相别参数")]
        public double PropagationVelocityA { get { return GetPhaseVelocityMPerUs(0); } set { SetPhaseVelocity(0, value); } }
        [Category("相别参数")]
        public double PropagationVelocityB { get { return GetPhaseVelocityMPerUs(1); } set { SetPhaseVelocity(1, value); } }
        [Category("相别参数")]
        public double PropagationVelocityC { get { return GetPhaseVelocityMPerUs(2); } set { SetPhaseVelocity(2, value); } }

        [Browsable(false)] public long PhaseChangedSequence { get { return Volatile.Read(ref phaseChangedSequence); } }
        [Browsable(false)] public long LevelChangedSequence { get { return Volatile.Read(ref levelChangedSequence); } }
        [Browsable(false)] public long CalibrationScopeChangedSequence { get { return Volatile.Read(ref calibrationScopeChangedSequence); } }
        [Browsable(false)] public long AdvancedModeChangedSequence { get { return Volatile.Read(ref advancedModeChangedSequence); } }
        [Browsable(false)] public long ParameterChangedSequence { get { return Volatile.Read(ref parameterChangedSequence); } }
        [Browsable(false)] public long StartRequestSequence { get { return Volatile.Read(ref startRequestSequence); } }
        [Browsable(false)] public long DeleteRequestSequence { get { return Volatile.Read(ref deleteRequestSequence); } }
        [Browsable(false)] public long SaveRequestSequence { get { return Volatile.Read(ref saveRequestSequence); } }
        [Browsable(false)] public long ExitRequestSequence { get { return Volatile.Read(ref exitRequestSequence); } }
        [Browsable(false)] public int RequestedPhaseIndex { get { return Volatile.Read(ref requestedPhaseIndex); } }
        [Browsable(false)] public string RequestedLevelCode { get { return Volatile.Read(ref requestedLevelCode); } }
        [Browsable(false)] public string[] RequestedLevelCodes { get { return (string[])Volatile.Read(ref requestedLevelCodes).Clone(); } }
        [Browsable(false)] public string LastRequestActionCode { get { return Volatile.Read(ref lastRequestActionCode); } }
        [Browsable(false)] public long JointChangedSequence { get { return Volatile.Read(ref jointChangedSequence); } }

        [Browsable(false)]
        public bool ShortCable
        {
            get { return GetPhaseShortCable(SelectedPhaseIndex); }
            set { if(SimulationMode)SetCurrentPhaseOption(true,value);else RunOnUiThread(()=>RequestManualShortCableSetting(value)); }
        }

        [Browsable(false)]
        public bool BandwidthEnhancement
        {
            get { return GetPhaseWideBand(SelectedPhaseIndex); }
            set { SetCurrentPhaseOption(false, value); }
        }

        [Browsable(false)]
        public int SelectedPhaseOptionIndex { get { int value = SelectedPhaseIndex; return value + 1; } set { SetSelectedPhaseOption(value); } }

        [Category("校准参数")]
        public double CableLengthMeters
        {
            get { return GetPhaseCableLengthMeters(SelectedPhaseIndex); }
            set { SetPhaseLength(SelectedPhaseIndex, value); }
        }

        [Category("校准参数")]
        public double PropagationVelocityMPerUs
        {
            get { return GetPhaseVelocityMPerUs(SelectedPhaseIndex); }
            set { SetPhaseVelocity(SelectedPhaseIndex, value); }
        }

        [Browsable(false)]
        public double BandwidthCutoffMHz
        {
            get { return GetPhaseBandwidthCutoffMHz(SelectedPhaseIndex); }
            set { SetPhaseBandwidth(selectedPhaseIndex, BandwidthEnhancement, value); }
        }

        [Category("分析")]
        public double ReflectionSearchTolerancePercent
        {
            get { return Volatile.Read(ref reflectionSearchToleranceFraction) * 100.0; }
            set { SetReflectionSearchTolerancePercent(value); }
        }

        public string[] GetAllLevelCodes() { return (string[])LevelCodes.Clone(); }
        public string[] GetAllLevelLabels() { return (string[])LevelLabels.Clone(); }
        public string[] GetVisibleLevelCodes()
        {
            string[] visible = FilterMeasurementLevels(ShowAllLevels ? LevelCodes : BasicLevelCodes);
            return Array.IndexOf(visible, SelectedLevelCode) >= 0 ? visible : visible.Concat(new[] { SelectedLevelCode }).ToArray();
        }
        public string[] GetSelectedCalibrationLevelCodes()
        {
            return CalibrateAllLevels ? FilterMeasurementLevels(LevelCodes) : new[] { SelectedLevelCode };
        }
        private string[] FilterMeasurementLevels(string[] values) { return values.Where(v => !IsCapacitiveLoad || v != "TDR").ToArray(); }

        public void SetCalibrateAllLevels(bool enabled)
        {
            RunOnUiThread(delegate
            {
                bool levelChanged = false;
                long levelSequence = 0;
                int phaseIndex = -1;
                lock (stateLock)
                {
                    if (calibrateAllLevels == enabled) return;
                    calibrateAllLevels = enabled;
                    showAllLevels = enabled;
                    System.Threading.Interlocked.Increment(ref calibrationScopeChangedSequence);
                    if (!enabled && Array.IndexOf(BasicLevelCodes, selectedLevelCode) < 0)
                    {
                        selectedLevelCode = "5nC";
                        levelSequence = System.Threading.Interlocked.Increment(ref levelChangedSequence);
                        phaseIndex = selectedPhaseIndex;
                        levelChanged = true;
                    }
                }
                ClearHoverMeasurement();
                Invalidate();
                EventHandler handler = CalibrationScopeChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                if (levelChanged)
                {
                    var levelHandler = LevelChanged;
                    if (levelHandler != null) levelHandler(this, new CalibrationSelectionEventArgs(levelSequence, phaseIndex, "5nC"));
                }
            });
        }

        public void SetSelectedPhaseOption(int optionIndex)
        {
            if (optionIndex < 0 || optionIndex > 3) throw new ArgumentOutOfRangeException("optionIndex", "Expected 0=ALL, 1=A, 2=B or 3=C.");
            SetSelectedPhase(optionIndex - 1);
        }

        public void SetSelectedPhaseCode(string phaseCode)
        {
            if (String.IsNullOrWhiteSpace(phaseCode)) throw new ArgumentException("A phase code is required.", "phaseCode");
            string value = phaseCode.Trim().ToUpperInvariant();
            SetSelectedPhase(value == "ALL" ? -1 : value == "A" ? 0 : value == "B" ? 1 : value == "C" ? 2 : throw new ArgumentException("Expected ALL, A, B or C.", "phaseCode"));
        }

        public void SetSelectedPhase(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            RunOnUiThread(delegate
            {
                ClearCursorSelection();
                long sequence;
                string level;
                lock (stateLock)
                {
                    if (selectedPhaseIndex == phaseIndex) return;
                    RememberLiveLevelParameters(selectedPhaseIndex,selectedLevelCode);
                    selectedPhaseIndex = phaseIndex;
                    RestoreLiveLevelParameters(phaseIndex,selectedLevelCode);
                    sequence = System.Threading.Interlocked.Increment(ref phaseChangedSequence);
                    level = selectedLevelCode;
                }
                ClearHoverMeasurement();
                Invalidate();
                var handler = PhaseChanged;
                if (handler != null) handler(this, new CalibrationSelectionEventArgs(sequence, phaseIndex, level));
            });
        }

        public void SetSelectedLevel(string levelCode)
        {
            string normalized = NormalizeLevelCode(levelCode);
            if (IsCapacitiveLoad && normalized == "TDR") normalized = "5nC";
            RunOnUiThread(delegate
            {
                ClearCursorSelection();
                long sequence;
                int phase;
                bool scopeChanged;
                bool levelChanged;
                lock (stateLock)
                {
                    // Choosing a common level must not collapse the visible all-level list.
                    scopeChanged = false;
                    levelChanged = !String.Equals(selectedLevelCode, normalized, StringComparison.Ordinal);
                    if (!scopeChanged && !levelChanged) return;
                    if (scopeChanged) calibrateAllLevels = false;
                    if (scopeChanged) System.Threading.Interlocked.Increment(ref calibrationScopeChangedSequence);
                    if (levelChanged) {
                        RememberLiveLevelParameters(selectedPhaseIndex,selectedLevelCode);
                        selectedLevelCode = normalized;
                        RestoreLiveLevelParameters(selectedPhaseIndex,normalized);
                    }
                    sequence = levelChanged ? System.Threading.Interlocked.Increment(ref levelChangedSequence) : levelChangedSequence;
                    phase = selectedPhaseIndex;
                }
                ClearHoverMeasurement();
                Invalidate();
                if (scopeChanged)
                {
                    EventHandler scopeHandler = CalibrationScopeChanged;
                    if (scopeHandler != null) scopeHandler(this, EventArgs.Empty);
                }
                if (levelChanged)
                {
                    var handler = LevelChanged;
                    if (handler != null) handler(this, new CalibrationSelectionEventArgs(sequence, phase, normalized));
                }
            });
        }

        public void SetAdvancedMode(bool enabled)
        {
            RunOnUiThread(delegate
            {
                bool changed;
                lock (stateLock)
                {
                    changed = advancedMode != enabled;
                    advancedMode = enabled;
                    if (changed) System.Threading.Interlocked.Increment(ref advancedModeChangedSequence);
                }
                if (!changed) return;
                Invalidate();
                var handler = AdvancedModeChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        public void SetPhaseParameters(int phaseIndex, double cableLengthMeters, double velocityMPerUs,
            bool shortCable, bool wideBand)
        {
            ValidatePhaseIndex(phaseIndex);
            ValidateFinitePositive(cableLengthMeters, "cableLengthMeters");
            ValidateFinitePositive(velocityMPerUs, "velocityMPerUs");
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    var phase = GetPhaseStateNoLock(phaseIndex);
                    if (phase.CableLengthMeters.Equals(cableLengthMeters) && phase.VelocityMPerUs.Equals(velocityMPerUs)
                        && phase.ShortCable == shortCable && phase.WideBand == wideBand) return;
                    phase.CableLengthMeters = !SimulationMode&&selectedLevelCode!="TDR"&&!IsCapacitiveLoad
                        ?PersistedCalibrationLength(phaseIndex,cableLengthMeters):cableLengthMeters;
                    phase.VelocityMPerUs = velocityMPerUs;
                    phase.ShortCable = shortCable;
                    phase.WideBand = wideBand;
                    RebuildCurrentLiveLevelNoLock(phaseIndex);
                    System.Threading.Interlocked.Increment(ref parameterChangedSequence);
                }
                ClearHoverMeasurement();
                Invalidate();
                var handler = ParameterChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        public double GetPhaseCableLengthMeters(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            return Volatile.Read(ref GetPhaseStateNoLock(phaseIndex).CableLengthMeters);
        }

        public double GetPhaseVelocityMPerUs(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            return Volatile.Read(ref GetPhaseStateNoLock(phaseIndex).VelocityMPerUs);
        }

        public bool GetPhaseShortCable(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            return Volatile.Read(ref GetPhaseStateNoLock(phaseIndex).ShortCable);
        }

        public bool GetPhaseWideBand(int phaseIndex)
        {
            ValidatePhaseIndex(phaseIndex);
            return Volatile.Read(ref GetPhaseStateNoLock(phaseIndex).WideBand);
        }

        public void SetPhaseWaveform(int phaseIndex, string levelCode, double[] samples,
            double x0Seconds, double dxSeconds)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            if (samples == null) throw new ArgumentNullException("samples");
            if (!IsFinite(x0Seconds)) throw new ArgumentOutOfRangeException("x0Seconds");
            ValidateFinitePositive(dxSeconds, "dxSeconds");
            double[] copy = (double[])samples.Clone();
            for (int i = 0; i < copy.Length; i++) if (!IsFinite(copy[i])) throw new ArgumentException("Waveform contains a non-finite sample.", "samples");
            RunOnUiThread(delegate
            {
                lock (stateLock) { PhaseState phase = GetPhaseStateNoLock(phaseIndex); phase.OriginalObservations.Remove(normalized); phase.VelocityAdoptions.Remove(normalized); phase.Waveforms[normalized] = new WaveformData(copy, x0Seconds, dxSeconds); RememberLiveLevelParameters(phaseIndex,normalized); RebuildLevelDerivedNoLock(phase, normalized); System.Threading.Interlocked.Increment(ref analysisChangedSequence); }
                ClearHoverMeasurement();
                ResetViewInternal();
                Invalidate();
                EventHandler analysisHandler = AnalysisChanged;
                if (analysisHandler != null) analysisHandler(this, EventArgs.Empty);
            });
        }

        public void ClearPhaseWaveform(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            RunOnUiThread(delegate
            {
                lock (stateLock) { PhaseState phase = GetPhaseStateNoLock(phaseIndex); phase.Waveforms.Remove(normalized); phase.FilteredWaveforms.Remove(normalized); phase.FilterSources.Remove(normalized); phase.FilterCutoffs.Remove(normalized); phase.Analyses.Remove(normalized); phase.ReflectionCandidates.Remove(normalized); phase.Markers.Remove(normalized); phase.OriginalObservations.Remove(normalized); phase.VelocityAdoptions.Remove(normalized); PublishLevelNoLock(phase, normalized); }
                ClearHoverMeasurement();
                Invalidate();
            });
        }

        public void SetPhaseNoiseWaveform(int phaseIndex, double[] samples, double x0Seconds, double dxSeconds)
        { SetPhaseNoiseWaveformCore(phaseIndex,samples,x0Seconds,dxSeconds,false); }

        // Only the live coordinator publishes this already-filtered observation.
        // Simulation/history APIs retain the legacy unmarked input contract.
        internal void SetPhaseAcquiredNoiseWaveform(int phaseIndex, double[] samples, double x0Seconds, double dxSeconds)
        {
            if(SimulationMode)throw new InvalidOperationException("实机噪声观察不能写入仿真校准。");
            SetPhaseNoiseWaveformCore(phaseIndex,samples,x0Seconds,dxSeconds,true);
        }
        private void SetPhaseNoiseWaveformCore(int phaseIndex, double[] samples, double x0Seconds, double dxSeconds,bool highPassed)
        {
            ValidatePhaseIndex(phaseIndex);
            if (samples == null) throw new ArgumentNullException("samples");
            if (!IsFinite(x0Seconds)) throw new ArgumentOutOfRangeException("x0Seconds");
            ValidateFinitePositive(dxSeconds, "dxSeconds");
            double[] copy = (double[])samples.Clone();
            for (int i = 0; i < copy.Length; i++)
                if (!IsFinite(copy[i])) throw new ArgumentException("Noise waveform contains a non-finite sample.", "samples");
            RunOnUiThread(delegate
            {
                // An empty input explicitly clears unavailable background data;
                // it is not a fabricated zero-valued measurement or saved wave.
                lock (stateLock) {
                    var wave=copy.Length==0?null:new WaveformData(copy,x0Seconds,dxSeconds){AcquisitionNoiseHighPassed=highPassed};
                    GetPhaseStateNoLock(phaseIndex).Noise=wave;
                    if(highPassed){noiseFilteredWave=wave;noiseFilteredSamples=wave==null?null:wave.Samples;}
                }
                Invalidate();
            });
        }

        public void SetPhaseMarkers(int phaseIndex, string levelCode, double startTimeSeconds,
            double distanceMarkerSeconds, double velocityMarkerSeconds, double distanceMeters)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            if (UseFirstArrivalTiming)
            {
                if (!SetPhaseTimingCursors(phaseIndex, normalized, startTimeSeconds, distanceMarkerSeconds))
                    throw new ArgumentException("First-arrival cursors must belong to a confirmed incident/reflection pulse pair.");
                return;
            }
            if (!IsFinite(startTimeSeconds) || !IsFinite(distanceMarkerSeconds) || !IsFinite(velocityMarkerSeconds) || !IsFinite(distanceMeters))
                throw new ArgumentOutOfRangeException("Marker values must be finite.");
            if (startTimeSeconds < 0.0) throw new ArgumentOutOfRangeException("startTimeSeconds");
            if (distanceMarkerSeconds < 0.0) throw new ArgumentOutOfRangeException("distanceMarkerSeconds");
            if (velocityMarkerSeconds < 0.0) throw new ArgumentOutOfRangeException("velocityMarkerSeconds");
            if (distanceMeters < 0.0)
                throw new ArgumentOutOfRangeException("distanceMeters", "Expected a non-negative distance.");
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    PhaseState phase = GetPhaseStateNoLock(phaseIndex);
                    phase.Markers[normalized] = new MarkerData(startTimeSeconds, distanceMarkerSeconds, velocityMarkerSeconds, distanceMeters);
                    PublishLevelNoLock(phase, normalized);
                }
                ClearHoverMeasurement();
                Invalidate();
            });
        }

        public double GetCalculatedDistanceMeters(int phaseIndex, string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);
            string normalized = NormalizeLevelCode(levelCode);
            PhaseState phase = GetPhaseStateNoLock(phaseIndex);
            MarkerData marker = ReadLevel(phase, normalized).Marker;
            if (marker == null) return 0.0;
            return Math.Abs(marker.DistanceMarkerSeconds - marker.StartTimeSeconds) * Volatile.Read(ref phase.VelocityMPerUs) * 1000000.0 / 2.0;
        }

        public void SetSignalMetrics(double signalToNoise, double peakVolts, double noiseVolts)
        {
            if (!IsFinite(signalToNoise) || !IsFinite(peakVolts) || !IsFinite(noiseVolts))
                throw new ArgumentOutOfRangeException("Signal metrics must be finite.");
            RunOnUiThread(delegate
            {
                lock (stateLock)
                {
                    signalToNoiseDb = signalToNoise;
                    peakVoltageV = peakVolts;
                    noiseVoltageV = noiseVolts;
                }
                Invalidate();
            });
        }

        public bool RequestStart() { return RaiseRequest("START"); }
        public bool RequestDelete() { return RaiseRequest("DELETE"); }
        public bool RequestSave() { return RaiseRequest("SAVE"); }

        public void RequestExit()
        {
            RunOnUiThread(delegate
            {
                if (!BeginAction()) return;
                lock (stateLock) System.Threading.Interlocked.Increment(ref exitRequestSequence);
                PublishAction("EXIT");
                var handler = ExitRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        public void SavePng(string filePath, int width, int height)
        {
            if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A file path is required.", "filePath");
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("Image dimensions must be positive.");
            string fullPath = Path.GetFullPath(filePath);
            string directory = Path.GetDirectoryName(fullPath);
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            RunOnUiThread(delegate
            {
                lock (renderLock)
                {
                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        RenderFrame(graphics, new Rectangle(0, 0, width, height));
                        bitmap.Save(fullPath, ImageFormat.Png);
                    }
                }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            lock (renderLock) RenderFrame(e.Graphics, ClientRectangle);
            PaintActionBusy(e.Graphics);
            PaintCalibrationKeyboardFocus(e.Graphics);
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            pevent.Graphics.Clear(BackColor);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (HandleLayoutMouseMove(e)) return;
            if (HandleExtendedMouseMove(e)) return;
            PointF point;
            Cursor = TryReferencePoint(e.Location, out point) && IsInteractive(point) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if(HardwareOperationBusy && e.Button==MouseButtons.Left && CalibrationStartBounds.Contains(e.Location)) {RequestCalibrationStopFromUi();return;}
            if (IsBusy) { base.OnMouseUp(e); return; }
            if(HandleNoiseUp(e)){base.OnMouseUp(e);return;}
            // Complete the active plot gesture even when release lands outside
            // the plot; compact button hit testing must not swallow that release.
            if ((bandwidthSliderDragging||draggingCursor!=0||boxZoomActive) && HandleExtendedMouseUp(e)) { base.OnMouseUp(e); return; }
            if (UseCompactLayout && HandleCompactMouseUp(e)) { base.OnMouseUp(e); return; }
            if (HandleLayoutMouseUp(e)) { base.OnMouseUp(e); return; }
            if (HandleExtendedMouseUp(e)) { base.OnMouseUp(e); return; }
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            PointF point;
            if (!TryReferencePoint(e.Location, out point)) return;

            if (!IsCapacitiveLoad && JointVisibilityBounds.Contains(point)) { JointMarkersVisible = !JointMarkersVisible; return; }

            if (point.Y >= 121 && point.Y <= 165 && point.X >= 1399 && point.X <= 1642)
            {
                int option = Math.Min(3, Math.Max(0, (int)((point.X - 1399) / 60.75)));
                ExecuteParameterAction("PHASE", delegate { SetSelectedPhaseOption(option); });
                return;
            }
            if (point.X >= 1397 && point.X <= 1642 && point.Y >= 261 && point.Y < 300)
            { ShowAllLevels = !ShowAllLevels; return; }
            if (point.X >= 1397 && point.X <= 1642 && point.Y >= 300 && point.Y < 730)
            {
                int index = (int)((point.Y - 300) / 26.8);
                string[] visible = GetVisibleLevelCodes();
                if (index >= 0 && index < visible.Length) ExecuteParameterAction("LEVEL", delegate { SetSelectedLevel(visible[index]); });
                return;
            }
            if (point.X >= 1397 && point.X <= 1643 && point.Y >= 733 && point.Y <= 785) { RequestSaveOrDelete(); return; }
            if (point.X >= 1397 && point.X <= 1643 && point.Y >= 794 && point.Y <= 891) { RequestStart(); return; }
            if (point.X >= 37 && point.X < 112 && point.Y >= 183 && point.Y <= 220) { ShowNumericEditor(1); return; }
            if (point.X >= 37 && point.X < 112 && point.Y >= 280 && point.Y <= 317) { ShowNumericEditor(2); return; }
            if (point.X >= 112 && point.X <= 136 && point.Y >= 185 && point.Y <= 218)
            { ExecuteParameterAction("CABLE_LENGTH", delegate { CableLengthMeters = Math.Max(0.1, CableLengthMeters + (point.Y < 202 ? 1.0 : -1.0)); }); return; }
            if (point.X >= 112 && point.X <= 136 && point.Y >= 282 && point.Y <= 315)
            { ExecuteParameterAction("VELOCITY", delegate { PropagationVelocityMPerUs = Math.Max(0.1, PropagationVelocityMPerUs + (point.Y < 299 ? 0.5 : -0.5)); }); return; }
            if (point.X >= 155 && point.X <= 214 && point.Y >= 363 && point.Y <= 400) { ExecuteParameterAction("SHORT_CABLE", delegate { ShortCable = !ShortCable; }); return; }
            if (point.X >= 155 && point.X <= 214 && point.Y >= 416 && point.Y <= 454) { ManualBandwidthPanelVisible = !ManualBandwidthPanelVisible; return; }
            if (point.X >= 1218 && point.X <= 1338 && point.Y >= signalQualityPanelTop + 29F && point.Y <= signalQualityPanelTop + 58F)
            { NoiseBaselineVisible = !NoiseBaselineVisible; return; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if(NoiseGestureActive)CancelNoiseGesture();
            UpdateNumericEditorBounds();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref lifetimeEnded, 1) == 0)
            {
                CloseActionMailbox();
                DisposeJointMenu();
                DisposeNoiseInteraction();
                DisposeEditableLayout();
                lock (renderLock)
                {
                    if (approvedFrame != null) { approvedFrame.Dispose(); approvedFrame = null; }
                }
                ReleaseRuntimeState();
            }
            try { base.Dispose(disposing); }
            finally
            {
                if (disposing && ownedControlFont != null)
                { ownedControlFont.Dispose(); ownedControlFont = null; }
            }
        }

        private void SetPhaseLength(int phaseIndex, double value)
        {
            ValidatePhaseIndex(phaseIndex);
            ValidateFinitePositive(value, "value");
            SetPhaseParameters(phaseIndex, value, GetPhaseVelocityMPerUs(phaseIndex), GetPhaseShortCable(phaseIndex), GetPhaseWideBand(phaseIndex));
        }

        private void SetPhaseVelocity(int phaseIndex, double value)
        {
            ValidatePhaseIndex(phaseIndex);
            ValidateFinitePositive(value, "value");
            MarkerData distanceMarker;
            GetPhaseStateNoLock(phaseIndex).Markers.TryGetValue("TDR", out distanceMarker);
            SetPhaseParameters(phaseIndex, GetPhaseCableLengthMeters(phaseIndex), value, GetPhaseShortCable(phaseIndex), GetPhaseWideBand(phaseIndex));
            // Ranging adjusts the conversion, not the measured arrival times.
            if (distanceMarker != null&&(SimulationMode||SelectedLevelCode=="TDR")) GetPhaseStateNoLock(phaseIndex).Markers["TDR"] = new MarkerData(
                distanceMarker.StartTimeSeconds, distanceMarker.DistanceMarkerSeconds, distanceMarker.VelocityMarkerSeconds,
                (distanceMarker.DistanceMarkerSeconds-distanceMarker.StartTimeSeconds)*value*1e6/2);
        }

        private void SetCurrentPhaseOption(bool shortCableOption, bool value)
        {
            int phase = SelectedPhaseIndex;
            SetPhaseParameters(phase, GetPhaseCableLengthMeters(phase), GetPhaseVelocityMPerUs(phase),
                shortCableOption ? value : GetPhaseShortCable(phase), shortCableOption ? GetPhaseWideBand(phase) : value);
        }

        private bool RaiseRequest(string actionCode)
        {
            bool accepted = false;
            RunOnUiThread(delegate
            {
                if (!BeginAction()) return;
                accepted = true;
                CalibrationRequestEventArgs args;
                EventHandler<CalibrationRequestEventArgs> handler;
                lock (stateLock)
                {
                    requestedPhaseIndex = selectedPhaseIndex;
                    bool allLevels = actionCode == "START" && calibrateAllLevels;
                    string[] levelCodes = allLevels ? FilterMeasurementLevels(LevelCodes) : new[] { selectedLevelCode };
                    Volatile.Write(ref requestedLevelCodes, levelCodes);
                    requestedLevelCode = allLevels ? "ALL_LEVELS" : selectedLevelCode;
                    lastRequestActionCode = actionCode;
                    long sequence;
                    if (actionCode == "START") { sequence = System.Threading.Interlocked.Increment(ref startRequestSequence); handler = StartRequested; }
                    else if (actionCode == "DELETE") { sequence = System.Threading.Interlocked.Increment(ref deleteRequestSequence); handler = DeleteRequested; }
                    else { sequence = System.Threading.Interlocked.Increment(ref saveRequestSequence); handler = SaveRequested; }
                    PhaseState phase = GetPhaseStateNoLock(selectedPhaseIndex);
                    TdrAutoAnalysisResult analysis;
                    phase.Analyses.TryGetValue(selectedLevelCode, out analysis);
                    args = new CalibrationRequestEventArgs(sequence, actionCode, selectedPhaseIndex, requestedLevelCode,
                        phase.CableLengthMeters, phase.VelocityMPerUs, phase.ShortCable, phase.WideBand,
                        phase.BandwidthCutoffMHz, analysis == null ? -1 : analysis.IncidentIndex,
                        analysis == null ? -1 : analysis.ReflectionIndex,
                        analysis == null ? "Unavailable" : analysis.SearchStatus.ToString(), allLevels, levelCodes);
                }
                PublishAction(actionCode);
                if (handler != null) handler(this, args);
            });
            return accepted;
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (Volatile.Read(ref lifetimeEnded) != 0 || IsDisposed || Disposing) return;
            if (Environment.CurrentManagedThreadId == ownerThreadId)
            {
                action();
                return;
            }
            if (!IsHandleCreated)
                throw new InvalidOperationException("A control handle must be created on the owner thread before cross-thread access.");
            // Commands retain their synchronous contract. No application lock is held
            // while marshaling. Polling never comes through this command path.
            bool entered = false;
            try
            {
                Invoke((Action)delegate
                {
                    entered = true;
                    if (Volatile.Read(ref lifetimeEnded) == 0 && !IsDisposed && !Disposing) action();
                });
            }
            catch (InvalidOperationException) when (!entered &&
                (Volatile.Read(ref lifetimeEnded) != 0 || IsDisposed || Disposing))
            {
                // Handle destroyed before dispatch. Never swallow a subscriber error.
            }
        }

        private void RenderLocalizedFrame(Graphics graphics, Rectangle bounds)
        {
            graphics.Clear(BackColor);
            if (approvedFrame == null) return;
            if (EmbeddedMode || IsCompactSize(bounds)) { RenderCompactFrame(graphics, bounds); return; }
            RectangleF content = GetContentBounds(bounds);
            float width = content.Width, height = content.Height, left = content.Left, top = content.Top;
            GraphicsState state = graphics.Save();
            float previousScale = themeRenderScale;
            bool previousLegacy = legacyRendering;
            try
            {
                legacyRendering = true;
                themeRenderScale = height / ArtworkHeight;
                graphics.TranslateTransform(left, top);
                graphics.ScaleTransform(width / ArtworkWidth, height / ArtworkHeight);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                DrawThemeShell(graphics);
                DrawStateOverlays(graphics, approvedFrame);
            }
            finally { legacyRendering = previousLegacy; themeRenderScale = previousScale; graphics.Restore(state); }
        }

        private void DrawStateOverlays(Graphics g, Bitmap frame)
        {
            int phaseIndex;
            string levelCode;
            PhaseState phase;
            double snr;
            double peak;
            double noise;
            bool allLevels;
            bool showNoiseBaseline;
            WaveformData waveform = null;
            WaveformData noiseWaveform = null;
            MarkerData markers = null;
            lock (stateLock)
            {
                phaseIndex = selectedPhaseIndex;
                levelCode = selectedLevelCode;
                phase = GetPhaseStateNoLock(phaseIndex).CopyShallow();
                snr = signalToNoiseDb;
                peak = peakVoltageV;
                noise = noiseVoltageV;
                allLevels = showAllLevels;
                showNoiseBaseline = noiseBaselineVisible;
                if (phase.WideBand) phase.FilteredWaveforms.TryGetValue(levelCode, out waveform);
                if (waveform == null) phase.Waveforms.TryGetValue(levelCode, out waveform);
                phase.Markers.TryGetValue(levelCode, out markers);
                noiseWaveform = phase.Noise;
            }

            DrawThemeCenter(g, waveform, noiseWaveform, snr, peak, noise);
            DrawThemePhaseSelection(g, phaseIndex);
            DrawThemeParameters(g, phaseIndex, phase);
            DrawThemeLevelList(g, levelCode, allLevels);
            TdrAutoAnalysisResult analysis = null; phase.Analyses.TryGetValue(levelCode, out analysis);
            if (waveform != null) DrawWaveformOverlay(g, waveform, markers, analysis, phase.VelocityMPerUs);
            if (waveform != null && markers != null) DrawJointMarkers(g, waveform, phase.Joints, markers, phase.VelocityMPerUs);
            if (noiseWaveform != null) DrawNoiseOverlay(g, noiseWaveform);
            if (showNoiseBaseline) DrawNoiseBaselineOverlay(g);
        }

        private static void DrawPhaseSelection(Graphics g, int selected)
        {
            string[] labels = { "ALL", "A", "B", "C" };
            int selectedOption = selected + 1;
            for (int i = 0; i < 4; i++)
            {
                RectangleF rect = new RectangleF(1399 + i * 60.75F, 122, 60.75F, 42);
                using (var brush = new SolidBrush(i == selectedOption ? ColorTranslator.FromHtml("#00A3C7") : ColorTranslator.FromHtml("#EAF2F8"))) g.FillRectangle(brush, rect);
                using (var pen = new Pen(ColorTranslator.FromHtml("#CAD8E5"))) g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
                using (var font = CreateFont(10.5F, FontStyle.Regular))
                using (var brush = new SolidBrush(i == selectedOption ? Color.White : ColorTranslator.FromHtml("#18334D")))
                    DrawCentered(g, labels[i], font, brush, rect);
            }
        }

        private void DrawParameterOverlay(Graphics g, int phaseIndex, PhaseState phase)
        {
            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, 28, 143, 190, 32);
                g.FillRectangle(white, 38, 185, 74, 33);
                g.FillRectangle(white, 38, 282, 74, 33);
            }
            using (var title = CreateFont(10.5F, FontStyle.Bold))
            using (var body = CreateFont(12F, FontStyle.Regular))
            using (var ink = new SolidBrush(ColorTranslator.FromHtml("#18334D")))
            {
                g.DrawString((phaseIndex < 0 ? "ALL" : phaseIndex == 0 ? "A" : phaseIndex == 1 ? "B" : "C") + BuiltIn("相电缆长度"), title, ink, new PointF(31, 149));
                g.DrawString(phase.CableLengthMeters.ToString("0.0", CultureInfo.InvariantCulture), body, ink, new PointF(46, 187));
                g.DrawString(phase.VelocityMPerUs.ToString("0.0", CultureInfo.InvariantCulture), body, ink, new PointF(46, 284));
            }
            if (phase.ShortCable) DrawToggle(g, new RectangleF(159, 367, 51, 29), true);
            using(var background=new SolidBrush(Color.White))g.FillRectangle(background,31,420,124,30);
            ThemeText(g, CalibrationText("显示手动调节", "Show manual controls"), new RectangleF(31,420,124,30),Theme.Ink,8.5f);
            DrawToggle(g, new RectangleF(159, 421, 51, 29), phase.ManualBandwidthPanelVisible);
            if (phase.ManualBandwidthPanelVisible) DrawBandwidthSlider(g, phaseIndex, phase.BandwidthCutoffMHz);
            else ThemeText(g,ActualBandwidthText(phase),new RectangleF(31,454,184,30),Theme.Muted,8.5F);
        }

        private static void DrawToggle(Graphics g, RectangleF rect, bool enabled)
        {
            using (var path = RoundedRectangle(rect, rect.Height / 2F))
            using (var brush = new SolidBrush(enabled ? ColorTranslator.FromHtml("#00A3C7") : Color.FromArgb(175, 189, 204)))
                g.FillPath(brush, path);
            float diameter = rect.Height - 6F;
            float x = enabled ? rect.Right - diameter - 3F : rect.Left + 3F;
            using (var brush = new SolidBrush(Color.White)) g.FillEllipse(brush, x, rect.Top + 3F, diameter, diameter);
        }

        private static void DrawRangeSelection(Graphics g, Bitmap frame, string levelCode)
        {
            int defaultIndex = Array.IndexOf(LevelCodes, "5nC");
            int selectedIndex = Array.IndexOf(LevelCodes, levelCode);
            if (selectedIndex < 0) return;
            DrawRangeRow(g, defaultIndex, false);
            DrawRangeRow(g, selectedIndex, true);
            RestoreRangeCaptionBackground(g, frame);
            using (var font = CreateFont(9.6F, FontStyle.Regular))
            using (var brush = new SolidBrush(Color.White))
                g.DrawString(BuiltIn("当前量程：") + LevelLabels[selectedIndex], font, brush, new PointF(1465, 847));
        }

        private static void DrawLevelList(Graphics g, Bitmap frame, string levelCode, bool allLevels)
        {
            using (var background = new SolidBrush(Color.White)) g.FillRectangle(background, 1392, 262, 254, 472);
            RectangleF selectAllBox = new RectangleF(1400F, 270F, 19F, 19F);
            using (var path = RoundedRectangle(selectAllBox, 3F))
            using (var fill = new SolidBrush(allLevels ? ColorTranslator.FromHtml("#00A3C7") : Color.White))
            using (var border = new Pen(ColorTranslator.FromHtml("#CAD8E5"), 1F))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            if (allLevels)
                using (var pen = new Pen(Color.White, 2F))
                    g.DrawLines(pen, new[] { new PointF(1404F, 279F), new PointF(1408F, 284F), new PointF(1415F, 274F) });
            using (var font = CreateFont(9.2F, allLevels ? FontStyle.Bold : FontStyle.Regular))
            using (var brush = new SolidBrush(allLevels ? ColorTranslator.FromHtml("#00A3C7") : ColorTranslator.FromHtml("#60758A")))
                g.DrawString(BuiltIn("全部校准档位"), font, brush, new PointF(1427F, 271F));
            string[] visible = allLevels ? LevelCodes : BasicLevelCodes;
            for (int index = 0; index < visible.Length; index++)
            {
                int levelIndex = Array.IndexOf(LevelCodes, visible[index]);
                DrawRangeRowAt(g, levelIndex, 302F + index * 26.8F,
                    String.Equals(levelCode, visible[index], StringComparison.Ordinal));
            }
            int selectedIndex = Array.IndexOf(LevelCodes, levelCode);
            if (selectedIndex >= 0)
            {
                RestoreRangeCaptionBackground(g, frame);
                using (var captionFont = CreateFont(9.6F, FontStyle.Regular))
                using (var captionBrush = new SolidBrush(Color.White))
                    g.DrawString(allLevels ? BuiltIn("当前量程：全部档位") : BuiltIn("当前量程：") + LevelLabels[selectedIndex],
                        captionFont, captionBrush, new PointF(1465, 847));
            }
            if (String.Equals(levelCode, "TDR", StringComparison.Ordinal))
                DrawStartRangingCaption(g, frame);
        }

        private static void DrawStartRangingCaption(Graphics g, Bitmap frame)
        {
            const int left = 1476, top = 811, width = 126, height = 30;
            using (var background = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                for (int x = 0; x < width; x++)
                {
                    Color upper = frame.GetPixel(left + x, 805);
                    Color lower = frame.GetPixel(left + x, 844);
                    for (int y = 0; y < height; y++)
                    {
                        double amount = y / (double)(height - 1);
                        background.SetPixel(x, y, Color.FromArgb(
                            InterpolateColor(upper.A, lower.A, amount),
                            InterpolateColor(upper.R, lower.R, amount),
                            InterpolateColor(upper.G, lower.G, amount),
                            InterpolateColor(upper.B, lower.B, amount)));
                    }
                }
                g.DrawImageUnscaled(background, left, top);
            }
            using (var font = CreateFont(16F, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
                DrawCentered(g, "开始测距", font, brush, new RectangleF(left, top, width, height));
        }

        private static void DrawBasicLevelList(Graphics g, Bitmap frame, string levelCode)
        {
            using (var background = new SolidBrush(Color.White)) g.FillRectangle(background, 1397, 300, 245, 430);
            for (int visibleIndex = 0; visibleIndex < BasicLevelCodes.Length; visibleIndex++)
            {
                int levelIndex = Array.IndexOf(LevelCodes, BasicLevelCodes[visibleIndex]);
                DrawRangeRowAt(g, levelIndex, 302F + visibleIndex * 26.8F,
                    String.Equals(levelCode, BasicLevelCodes[visibleIndex], StringComparison.Ordinal));
            }
            if (!String.Equals(levelCode, "5nC", StringComparison.Ordinal))
            {
                int selectedIndex = Array.IndexOf(LevelCodes, levelCode);
                RestoreRangeCaptionBackground(g, frame);
                using (var font = CreateFont(9.6F, FontStyle.Regular))
                using (var brush = new SolidBrush(Color.White))
                    g.DrawString(BuiltIn("当前量程：") + LevelLabels[selectedIndex], font, brush, new PointF(1465, 847));
            }
        }

        private static void RestoreRangeCaptionBackground(Graphics g, Bitmap frame)
        {
            const int left = 1461, top = 844, width = 176, height = 29;
            using (var restored = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                for (int x = 0; x < width; x++)
                {
                    Color upper = frame.GetPixel(left + x, 840);
                    Color lower = frame.GetPixel(left + x, 873);
                    for (int y = 0; y < height; y++)
                    {
                        double amount = y / (double)(height - 1);
                        restored.SetPixel(x, y, Color.FromArgb(
                            InterpolateColor(upper.A, lower.A, amount),
                            InterpolateColor(upper.R, lower.R, amount),
                            InterpolateColor(upper.G, lower.G, amount),
                            InterpolateColor(upper.B, lower.B, amount)));
                    }
                }
                g.DrawImageUnscaled(restored, left, top);
            }
        }

        private static int InterpolateColor(int start, int end, double amount)
        {
            return (int)Math.Round(start + (end - start) * amount, MidpointRounding.AwayFromZero);
        }

        private static void DrawRangeRow(Graphics g, int index, bool selected)
        {
            float y = 302F + index * 26.8F;
            DrawRangeRowAt(g, index, y, selected);
        }

        private static void DrawRangeRowAt(Graphics g, int index, float y, bool selected)
        {
            RectangleF rect = new RectangleF(1398, y, 243, 26.5F);
            using (var path = RoundedRectangle(rect, 5F))
            using (var fill = new SolidBrush(selected ? ColorTranslator.FromHtml("#00A3C7") : ColorTranslator.FromHtml("#FFFFFF")))
                g.FillPath(fill, path);
            using (var pen = new Pen(ColorTranslator.FromHtml("#CAD8E5")))
            using (var path = RoundedRectangle(rect, 5F)) g.DrawPath(pen, path);
            using (var font = CreateFont(9.5F, FontStyle.Regular))
            using (var brush = new SolidBrush(selected ? Color.White : ColorTranslator.FromHtml("#18334D")))
                g.DrawString(LevelLabels[index], font, brush, new PointF(1415, y + 4F));
            if (selected)
            {
                using (var pen = new Pen(Color.White, 2.2F))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLines(pen, new[] { new PointF(1608, y + 13), new PointF(1613, y + 18), new PointF(1622, y + 8) });
                }
            }
        }

        private void DrawMetricOverlay(Graphics g, double snr, double peak, double noise)
        {
            RectangleF[] rects = { new RectangleF(877, 807, 125, 50), new RectangleF(1040, 807, 125, 50), new RectangleF(1208, 807, 125, 50) };
            string[] values = { SignalToNoiseMetricCaption(snr), MetricCaption(peak, "0.00", "V"), MetricCaption(noise, "0.000", "V") };
            Color[] colors = { ColorTranslator.FromHtml("#238A67"), ColorTranslator.FromHtml("#00A3C7"), ColorTranslator.FromHtml("#18334D") };
            using (var white = new SolidBrush(Color.White)) foreach (RectangleF rect in rects) g.FillRectangle(white, rect);
            for (int i = 0; i < rects.Length; i++)
                using (var font = CreateFont(17F, FontStyle.Bold))
                using (var brush = new SolidBrush(colors[i])) g.DrawString(values[i], font, brush, rects[i].Location);
        }

        private void DrawWaveformOverlay(Graphics g, WaveformData waveform, MarkerData markers, TdrAutoAnalysisResult analysis, double velocity)
        {
            RectangleF plot = CurrentPlotRectangle;
            double xMin, xMax, yMin, yMax;
            GetWaveformView(waveform, out xMin, out xMax, out yMin, out yMax);
            DrawWaveformAxes(g, plot, xMin, xMax, yMin, yMax);
            if (waveform.Samples.Length >= 2)
            {
                int step = Math.Max(1, waveform.Samples.Length / 1800);
                var points = new List<PointF>();
                for (int i = 0; i < waveform.Samples.Length; i += step)
                {
                    float x = plot.Left + (float)((waveform.X0Seconds + i * waveform.DxSeconds - xMin) / Math.Max(1e-18, xMax - xMin)) * plot.Width;
                    float y = plot.Bottom - (float)((waveform.Samples[i] - yMin) / Math.Max(1e-18, yMax - yMin)) * plot.Height;
                    points.Add(new PointF(x, y));
                }
                if (points.Count > 1)
                    using (var pen = new Pen(ColorTranslator.FromHtml("#00A3C7"), 2F))
                        DrawClippedPolyline(g, plot, pen, points.ToArray());
            }
            double viewX0 = xMin, viewX1 = xMax, viewY0 = yMin, viewY1 = yMax;
            TdrAutoAnalysisResult displayDecay;
            lock (stateLock) displayDecay = DisplayDecayNoLock(GetPhaseStateNoLock(selectedPhaseIndex), selectedLevelCode, waveform, markers, analysis);
            if (displayDecay != null)
                DrawDecayCurve(g, plot, displayDecay, viewX0, viewX1, viewY0, viewY1);
            MarkerData editing;
            lock(stateLock)editing=PacketGuiMarkersNoLock(GetPhaseStateNoLock(selectedPhaseIndex),selectedLevelCode,markers);
            if (editing != null)
            {
                DrawMarkerLine(g, plot, editing.StartTimeSeconds, viewX0, viewX1, ColorTranslator.FromHtml("#C44747"));
                DrawMarkerLine(g, plot, editing.DistanceMarkerSeconds, viewX0, viewX1, ColorTranslator.FromHtml("#00A3C7"));
                DrawWaveformBadges(g, plot, editing, analysis, velocity, viewX0, viewX1);
            }
            else if (analysis != null && analysis.IncidentIndex >= 0 && IsFinite(analysis.IncidentTimeSeconds) &&
                analysis.IncidentMagnitude > 0 && analysis.IncidentTimeSeconds >= viewX0 && analysis.IncidentTimeSeconds <= viewX1)
            {
                DrawMarkerLine(g, plot, analysis.IncidentTimeSeconds, viewX0, viewX1, Theme.Accent);
                float incidentX = plot.Left + (float)((analysis.IncidentTimeSeconds-viewX0)/(viewX1-viewX0))*plot.Width;
                ThemeText(g, CalibrationText("入射波", "Incident"),new RectangleF(Math.Max(plot.Left,Math.Min(plot.Right-88,incidentX+4)),plot.Top+4,88,26),Theme.Accent,9f);
            }
            DrawInteractionOverlay(g, plot);
            DrawReflectionCandidate(g, plot, waveform, viewX0, viewX1, viewY0, viewY1);
            if(!UseCompactLayout)DrawPendingReflectionSelection(g,plot,viewX0,viewX1);
            if (!IsCapacitiveLoad) DrawHoverMeasurement(g, plot);
        }

        private static void DrawMarkerLine(Graphics g, RectangleF plot, double seconds, double minimum, double maximum, Color color)
        {
            double clampedSeconds = Math.Max(minimum, Math.Min(maximum, seconds));
            float x = plot.Left + (float)((clampedSeconds - minimum) / (maximum - minimum)) * plot.Width;
            using (var pen = new Pen(color, 1.4F)) { pen.DashStyle = DashStyle.Dash; g.DrawLine(pen, x, plot.Top, x, plot.Bottom); }
        }

        private void DrawNoiseOverlay(Graphics g, WaveformData waveform)
        {
            DrawInteractiveNoise(g,waveform);
        }

        private static void DrawClippedPolyline(Graphics g, RectangleF plot, Pen pen, PointF[] points)
        {
            // Clip the actual line segments, not their data coordinates. Preserve the
            // host's existing clip and restore it before drawing labels or other panels.
            GraphicsState state = g.Save();
            try
            {
                g.SetClip(plot, CombineMode.Intersect);
                g.DrawLines(pen, points);
            }
            finally { g.Restore(state); }
        }

        private static Bitmap LoadApprovedFrame()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HVX.CalibrationProgram.ApprovedUi.png");
            if (stream == null) throw new InvalidOperationException("Embedded approved calibration UI is missing.");
            using (stream)
            using (var image = Image.FromStream(stream))
            {
                var bitmap = new Bitmap(image);
                if (bitmap.Width != ArtworkWidth || bitmap.Height != ArtworkHeight)
                {
                    bitmap.Dispose();
                    throw new InvalidOperationException("Approved calibration UI must be 1672 x 941 pixels.");
                }
                RemoveHostControls(bitmap);
                return bitmap;
            }
        }

        private static void RemoveHostControls(Bitmap bitmap)
        {
            for (int y = 0; y <= 54; y++)
            {
                Color rowColor = bitmap.GetPixel(1050, Math.Min(y, bitmap.Height - 1));
                // Embedded controls must not draw their own language/settings/user/window chrome.
                for (int x = 1360; x < bitmap.Width; x++) bitmap.SetPixel(x, y, rowColor);
            }
        }

        private bool TryReferencePoint(Point point, out PointF reference)
        {
            if (UseCompactLayout) { reference = point; return ClientRectangle.Contains(point); }
            RectangleF content = GetContentBounds(ClientRectangle);
            if (content.Width <= 0 || content.Height <= 0 || !content.Contains(point))
            {
                reference = PointF.Empty; return false;
            }
            reference = new PointF((point.X - content.Left) * ArtworkWidth / content.Width,
                (point.Y - content.Top) * ArtworkHeight / content.Height);
            return true;
        }

        private static RectangleF GetContentBounds(Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return RectangleF.Empty;
            // Snap nearly 16:9 source exports to their exact pixels (the legacy raster differs by <0.1%).
            float aspect = bounds.Width / (float)bounds.Height;
            if (Math.Abs(aspect / (ReferenceWidth / (float)ReferenceHeight) - 1F) < 0.001F)
                return bounds;
            float scale = Math.Min(bounds.Width / (float)ReferenceWidth, bounds.Height / (float)ReferenceHeight);
            float width = ReferenceWidth * scale, height = ReferenceHeight * scale;
            return new RectangleF(bounds.Left + (bounds.Width - width) / 2F,
                bounds.Top + (bounds.Height - height) / 2F, width, height);
        }

        private bool IsInteractive(PointF p)
        {
            return (p.Y >= 121 && p.Y <= 165 && p.X >= 1399 && p.X <= 1641) ||
                   (p.X >= 1397 && p.X <= 1642 && p.Y >= 261 && p.Y < 300) ||
                   (p.X >= 1397 && p.X <= 1642 && p.Y >= 300 && p.Y < 730) ||
                   (p.X >= 1218 && p.X <= 1338 && p.Y >= signalQualityPanelTop + 29F && p.Y <= signalQualityPanelTop + 58F) ||
                   (p.X >= 37 && p.X <= 136 && ((p.Y >= 183 && p.Y <= 220) || (p.Y >= 280 && p.Y <= 317))) ||
                   (p.X >= 155 && p.X <= 214 && p.Y >= 363 && p.Y <= 454) ||
                   (p.X >= CenterPanelLeft && p.X <= CenterPanelRight && Math.Abs(p.Y - signalQualityPanelTop) <= 7F);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
        {
            float diameter = radius * 2F;
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void DrawCentered(Graphics g, string text, Font font, Brush brush, RectangleF rect)
        {
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(BuiltIn(text), font, brush, rect, format);
        }

        private static Font CreateFont(float size, FontStyle style)
        {
            return ThemeFont(style == FontStyle.Bold && size >= 13F ? 10.5F : 9.5F, style);
        }

        private static string NormalizeLevelCode(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new ArgumentException("A calibration level is required.", "value");
            // Common host codes and displayed labels need no temporary string.
            for (int i = 0; i < LevelCodes.Length; i++)
                if (String.Equals(LevelCodes[i], value, StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(LevelLabels[i], value, StringComparison.OrdinalIgnoreCase)) return LevelCodes[i];
            string compact = value.Replace(" ", String.Empty).Trim();
            for (int i = 0; i < LevelCodes.Length; i++) if (String.Equals(LevelCodes[i], compact, StringComparison.OrdinalIgnoreCase)) return LevelCodes[i];
            throw new ArgumentException("Unsupported calibration level: " + value, "value");
        }

        private static void ValidatePhaseIndex(int phaseIndex)
        {
            if (phaseIndex < -1 || phaseIndex > 2) throw new ArgumentOutOfRangeException("phaseIndex", "Expected -1=ALL, 0=A, 1=B or 2=C.");
        }

        private static void ValidateFinitePositive(double value, string name)
        {
            if (!IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name, "Expected a finite positive value.");
        }

        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool NearlyEqual(double left, double right) { return Math.Abs(left - right) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right))); }

        private sealed class PhaseState
        {
            internal readonly PublishedLevel[] PublishedLevels = new PublishedLevel[16];
            internal double CableLengthMeters = 180.0;
            internal double VelocityMPerUs = 170.5;
            internal bool ShortCable;
            internal bool WideBand;
            internal double BandwidthCutoffMHz = 60.0;
            internal bool ManualBandwidthPanelVisible;
            internal readonly Dictionary<string, ReflectionCandidateEvidence> ReflectionCandidates = new Dictionary<string, ReflectionCandidateEvidence>(StringComparer.Ordinal);
            internal readonly Dictionary<string, WaveformData> FilteredWaveforms = new Dictionary<string, WaveformData>(StringComparer.Ordinal);
            internal readonly Dictionary<string, WaveformData> FilterSources = new Dictionary<string, WaveformData>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> FilterCutoffs = new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly Dictionary<string, TdrAutoAnalysisResult> Analyses = new Dictionary<string, TdrAutoAnalysisResult>(StringComparer.Ordinal);
            internal readonly Dictionary<string, WaveformData> Waveforms = new Dictionary<string, WaveformData>(StringComparer.Ordinal);
            internal readonly Dictionary<string, MarkerData> Markers = new Dictionary<string, MarkerData>(StringComparer.Ordinal);
            internal readonly Dictionary<string, double> VelocityAdoptions = new Dictionary<string, double>(StringComparer.Ordinal);
            internal readonly List<CalibrationVelocityAdoption> VelocityHistory = new List<CalibrationVelocityAdoption>();
            internal readonly Dictionary<string, CalibrationTimingObservation> OriginalObservations = new Dictionary<string, CalibrationTimingObservation>(StringComparer.Ordinal);
            internal readonly List<CalibrationJoint> Joints = new List<CalibrationJoint>();
            internal WaveformData Noise;

            internal PhaseState CopyShallow()
            {
                var copy = new PhaseState
                {
                    CableLengthMeters = CableLengthMeters,
                    VelocityMPerUs = VelocityMPerUs,
                    ShortCable = ShortCable,
                    WideBand = WideBand,
                    ManualBandwidthPanelVisible = ManualBandwidthPanelVisible,
                    BandwidthCutoffMHz = BandwidthCutoffMHz,
                    Noise = Noise
                };
                foreach (var pair in Waveforms) copy.Waveforms[pair.Key] = pair.Value;
                foreach (var pair in FilteredWaveforms) copy.FilteredWaveforms[pair.Key] = pair.Value;
                foreach (var pair in Analyses) copy.Analyses[pair.Key] = pair.Value;
                foreach (var pair in ReflectionCandidates) copy.ReflectionCandidates[pair.Key] = pair.Value;
                foreach (var pair in Markers) copy.Markers[pair.Key] = pair.Value;
                copy.Joints.AddRange(Joints);
                return copy;
            }
        }

        private sealed class WaveformData
        {
            internal WaveformData(double[] samples, double x0Seconds, double dxSeconds) { Samples = samples; X0Seconds = x0Seconds; DxSeconds = dxSeconds; }
            internal readonly double[] Samples;
            internal readonly double X0Seconds;
            internal readonly double DxSeconds;
            internal bool AcquisitionNoiseHighPassed;
        }

        private sealed class MarkerData
        {
            internal MarkerData(double start, double distance, double velocity, double distanceMeters)
            {
                StartTimeSeconds = start; DistanceMarkerSeconds = distance; VelocityMarkerSeconds = velocity; DistanceMeters = distanceMeters;
            }
            internal readonly double StartTimeSeconds;
            internal readonly double DistanceMarkerSeconds;
            internal readonly double VelocityMarkerSeconds;
            internal readonly double DistanceMeters;
        }
    }
}
